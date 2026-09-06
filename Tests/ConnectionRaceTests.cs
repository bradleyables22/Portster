using Microsoft.Extensions.Logging.Abstractions;

namespace Portster.Tests;

public class ConnectionRaceTests
{
    private static ConnectionRegistry Registry(FakeFactory factory, ServerPolicy? policy = null, FakeCatalog? catalog = null, MemoryAudit? audit = null) =>
        new(policy ?? Fixtures.Policy, catalog ?? new FakeCatalog(new PortInfo("COM7")), factory, audit ?? new(), NullLogger<ConnectionRegistry>.Instance);

    [Fact]
    public async Task FailedNativeCloseKeepsThePortQuarantined()
    {
        var serial = new FakeSerial { OnDispose = () => throw new IOException("native close failed") };
        using var registry = Registry(new(() => serial)); await registry.StartAsync(default);
        var opened = await registry.OpenAsync("COM7", new(), null, new("open"), default);
        try
        {
            Assert.Equal("CLOSE_FAILED", (await Assert.ThrowsAsync<PortsterException>(() => registry.CloseAsync(opened.ConnectionHandle, new("close"), default))).Code);
            await Task.Delay(1200);
            Assert.Equal("PORT_BUSY", (await Assert.ThrowsAsync<PortsterException>(() => registry.OpenAsync("COM7", new(), null, new("open"), default))).Code);
        }
        finally { serial.OnDispose = null; serial.Dispose(); await registry.StopAsync(default); }
    }
    [Fact]
    public async Task ClosingDuringWriteIntentDoesNotClaimBytesWereAttempted()
    {
        var serial = new FakeSerial(); var audit = new MemoryAudit();
        await using var connection = Fixtures.Connection(serial, audit: audit);
        audit.BeforeRecord = (_, phase, _) => { if (phase == "writeIntent") _ = connection.DisposeAsync(); };
        var operation = new OperationContext("write");
        var error = await Assert.ThrowsAsync<PortsterException>(() => connection.WriteAsync(new() { Data = "AA" }, operation, default));
        Assert.Equal("CONNECTION_ENDED", error.Code); Assert.Empty(serial.Writes);
        Assert.Equal("notAttempted", operation.WriteDisposition);
    }
    [Fact]
    public async Task CancellingBeforeWritePreventsDriverInvocation()
    {
        var serial = new FakeSerial(); await using var connection = Fixtures.Connection(serial);
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); var operation = new OperationContext("write");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.WriteAsync(new() { Data = "AA" }, operation, cancel.Token));
        Assert.Empty(serial.Writes); Assert.Equal("notAttempted", operation.WriteDisposition); Assert.Null(connection.State);
    }
    [Fact]
    public async Task CancellingAfterDriverSubmissionPreservesTheOutcomeAndReleasesTheMutationLock()
    {
        var serial = new FakeSerial(); await using var connection = Fixtures.Connection(serial);
        using var cancel = new CancellationTokenSource(); var operation = new OperationContext("transaction");
        var transaction = connection.TransactAsync(new() { Data = "AA" }, new() { Mode = CompletionMode.Length, Length = 4, WaitMs = 10000 }, operation, cancel.Token);
        await Fixtures.Eventually(() => operation.WriteDisposition == "submittedToDriver"); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transaction);
        Assert.Equal("submittedToDriver", operation.WriteDisposition); Assert.Null(connection.State);
        await connection.WriteAsync(new() { Data = "BB" }, new("write"), default);
        Assert.Equal(2, serial.Writes.Count);
    }
    [Fact]
    public async Task CloseInterruptsAWaitingTransactionWithoutRepeatingItsCommand()
    {
        var serial = new FakeSerial(); await using var connection = Fixtures.Connection(serial);
        var transaction = connection.TransactAsync(new() { Data = "AA" }, new() { Mode = CompletionMode.Length, Length = 4, WaitMs = 10000 }, new("transaction"), default);
        await Fixtures.Eventually(() => serial.Writes.Count == 1); await connection.DisposeAsync();
        var result = await transaction;
        Assert.Equal("closed", result.Read.Completion); Assert.Single(serial.Writes);
    }
    [Fact]
    public async Task AWaitingReadPreventsInactivityExpiryUntilItsLeaseIsReleased()
    {
        var serial = new FakeSerial(); await using var connection = Fixtures.Connection(serial, Fixtures.Policy with { InactivityTimeoutSeconds = 0 });
        using var cancel = new CancellationTokenSource();
        var read = connection.ReadAsync(0, new() { WaitMs = 10000 }, new("read"), cancel.Token);
        Assert.False(connection.TryExpire()); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.True(connection.TryExpire()); Assert.Equal("expired", connection.State);
    }
    [Fact]
    public async Task ConcurrentOpensPerformOnlyOneNativeOpen()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var factory = new FakeFactory(() => { entered.Set(); release.Wait(5000); return new FakeSerial(); });
        using var registry = Registry(factory);
        var first = registry.OpenAsync("com7", new(), null, new("open"), default);
        try
        {
            await Fixtures.Eventually(() => entered.IsSet);
            var rivals = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
                await Assert.ThrowsAsync<PortsterException>(() => registry.OpenAsync("COM7", new(), null, new("open"), default))));
            Assert.All(rivals, error => Assert.Equal("PORT_BUSY", error.Code)); Assert.Equal(1, factory.OpenCount);
        }
        finally { release.Set(); }
        await registry.CloseAsync((await first).ConnectionHandle, new("close"), default);
    }
    [Fact]
    public async Task ConnectionLimitIsReleasedOnlyAfterClose()
    {
        var factory = new FakeFactory(() => new FakeSerial());
        using var registry = Registry(factory, Fixtures.Policy with { MaxConnections = 1, AllowedPorts = ["COM7", "COM8"] }, new(new PortInfo("COM7"), new PortInfo("COM8")));
        var opened = await registry.OpenAsync("COM7", new(), null, new("open"), default);
        Assert.Equal("CONNECTION_LIMIT", (await Assert.ThrowsAsync<PortsterException>(() => registry.OpenAsync("COM8", new(), null, new("open"), default))).Code);
        await registry.CloseAsync(opened.ConnectionHandle, new("close"), default);
        var next = await registry.OpenAsync("COM8", new(), null, new("open"), default);
        await registry.CloseAsync(next.ConnectionHandle, new("close"), default);
        await registry.CloseAsync(next.ConnectionHandle, new("close"), default); // Retry of completed close is harmless.
    }
    [Fact]
    public async Task NativeOpenFailureReleasesTheReservation()
    {
        var deny = true; var factory = new FakeFactory(() => deny ? throw new UnauthorizedAccessException() : new FakeSerial());
        using var registry = Registry(factory);
        Assert.Equal("PORT_BUSY_OR_DENIED", (await Assert.ThrowsAsync<PortsterException>(() => registry.OpenAsync("COM7", new(), null, new("open"), default))).Code);
        deny = false;
        var opened = await registry.OpenAsync("COM7", new(), null, new("open"), default);
        await registry.CloseAsync(opened.ConnectionHandle, new("close"), default);
    }
    [Fact]
    public async Task AuditFailureBeforeOpenNeverTouchesTheDriver()
    {
        var audit = new MemoryAudit { Fail = true }; var factory = new FakeFactory(() => new FakeSerial());
        using var registry = Registry(factory, audit: audit);
        Assert.Equal("AUDIT_UNAVAILABLE", (await Assert.ThrowsAsync<PortsterException>(() => registry.OpenAsync("COM7", new(), null, new("open"), default))).Code);
        Assert.Equal(0, factory.OpenCount); audit.Fail = false;
        var opened = await registry.OpenAsync("COM7", new(), null, new("open"), default);
        await registry.CloseAsync(opened.ConnectionHandle, new("close"), default);
    }
    [Fact]
    public async Task ProfileResolutionIsRecheckedBeforeNativeOpen()
    {
        var first = new PortInfo("COM7", "0403", "6001", "AAA"); var replacement = first with { PortName = "COM8" };
        var catalog = new FakeCatalog(first) { OnList = call => call == 1 ? [first] : [first with { UsbSerialNumber = "OTHER" }, replacement] };
        var factory = new FakeFactory(() => new FakeSerial()); using var registry = Registry(factory, catalog: catalog);
        var profile = Fixtures.Profile with { Selector = new() { Vid = "0403", Pid = "6001", UsbSerialNumber = "AAA" } };
        Assert.Equal("DEVICE_CHANGED", (await Assert.ThrowsAsync<PortsterException>(() => registry.OpenProfileAsync(profile, new("open"), default))).Code);
        Assert.Equal(0, factory.OpenCount);
    }
    [Fact]
    public async Task MissingPortAndInvalidSettingsDoNotInvokeTheFactory()
    {
        var factory = new FakeFactory(() => new FakeSerial()); using var registry = Registry(factory, catalog: new FakeCatalog());
        Assert.Equal("DEVICE_NOT_FOUND", (await Assert.ThrowsAsync<PortsterException>(() => registry.OpenAsync("COM7", new(), null, new("open"), default))).Code);
        Assert.Equal("INVALID_SETTINGS", (await Assert.ThrowsAsync<PortsterException>(() => registry.OpenAsync("COM7", null!, null, new("open"), default))).Code);
        Assert.Equal(0, factory.OpenCount);
    }
    [Fact]
    public async Task ShutdownRejectsNewOpensAndClosesExistingConnections()
    {
        var serial = new FakeSerial(); var factory = new FakeFactory(() => serial); using var registry = Registry(factory);
        await registry.StartAsync(default); await registry.OpenAsync("COM7", new(), null, new("open"), default);
        await registry.StopAsync(default); Assert.True(serial.Disposed);
        Assert.Equal("SERVER_STOPPING", (await Assert.ThrowsAsync<PortsterException>(() => registry.OpenAsync("COM7", new(), null, new("open"), default))).Code);
    }
}
