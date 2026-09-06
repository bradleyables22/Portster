using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Portster;

namespace Portster.Tests;

public class ConnectionTests
{
    [Fact]
    public async Task TransactionExcludesAlreadyBufferedBytesAndBlocksConcurrentMutations()
    {
        var serial = new FakeSerial(); await using var connection = Fixtures.Connection(serial);
        serial.Feed(0x99); await Fixtures.Eventually(() => connection.Buffer.End == 1);
        var transaction = connection.TransactAsync(new() { Data = "01" }, new() { Mode = CompletionMode.Delimiter, DelimiterHex = "0A" }, new("transaction"), default);
        await Fixtures.Eventually(() => serial.Writes.Count == 1);
        var busy = await Assert.ThrowsAsync<PortsterException>(() => connection.WriteAsync(new() { Data = "02" }, new("write"), default));
        Assert.Equal("CONNECTION_BUSY", busy.Code);
        serial.Feed(65, 10, 66);
        var result = await transaction;
        Assert.Equal("410A", result.Read.Hex); Assert.Equal(1, result.Read.StartCursor);
        Assert.Equal("submittedToDriver", result.Write.WriteDisposition); Assert.Single(serial.Writes);
    }
    [Fact]
    public async Task TimedOutResponseReturnsPartialDataAndNeverRepeatsWrite()
    {
        var serial = new FakeSerial(); serial.OnWrite = _ => serial.Feed(0xAA);
        await using var connection = Fixtures.Connection(serial);
        var result = await Fixtures.Runner().Run("serial_transact", op => connection.TransactAsync(new() { Data = "01" },
            new() { Mode = CompletionMode.Length, Length = 2, WaitMs = 100 }, op, default), default);
        Assert.True(result.IsError);
        var body = result.StructuredContent!.Value;
        Assert.Equal("READ_TIMEOUT", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("submittedToDriver", body.GetProperty("error").GetProperty("writeDisposition").GetString());
        Assert.Equal("AA", body.GetProperty("data").GetProperty("read").GetProperty("hex").GetString());
        Assert.Single(serial.Writes);
    }
    [Fact]
    public async Task CancellationAfterWriteAttemptRetiresConnectionAndReportsUncertainty()
    {
        using var release = new ManualResetEventSlim();
        var serial = new FakeSerial { OnWrite = _ => release.Wait(3000) };
        await using var connection = Fixtures.Connection(serial);
        using var cancellation = new CancellationTokenSource();
        var operation = new OperationContext("write");
        var writing = connection.WriteAsync(new() { Data = "01" }, operation, cancellation.Token);
        await Fixtures.Eventually(() => serial.WriteEntered.IsSet);
        cancellation.Cancel();
        try
        {
            var error = await Assert.ThrowsAsync<PortsterException>(() => writing);
            Assert.Equal("WRITE_CANCELLED", error.Code); Assert.Equal("attemptedOutcomeUnknown", operation.WriteDisposition);
            await Assert.ThrowsAsync<PortsterException>(() => connection.WriteAsync(new() { Data = "02" }, new("write"), default));
            Assert.Single(serial.Writes);
        }
        finally { release.Set(); }
    }
    [Fact]
    public async Task WriteTimeoutClosesConnectionRatherThanAllowingOverlappingRetry()
    {
        using var release = new ManualResetEventSlim();
        var serial = new FakeSerial { OnWrite = _ => release.Wait(3000) };
        await using var connection = Fixtures.Connection(serial, Fixtures.Policy with { WriteTimeoutMs = 100 });
        try
        {
            var error = await Assert.ThrowsAsync<PortsterException>(() => connection.WriteAsync(new() { Data = "01" }, new("write"), default));
            Assert.Equal("WRITE_OUTCOME_UNKNOWN", error.Code);
            Assert.Equal("attemptedOutcomeUnknown", error.WriteDisposition);
            Assert.NotNull(connection.State);
        }
        finally { release.Set(); }
    }
    [Fact]
    public async Task UnplugCompletesWaitingReadAndPreservesReceivedBytes()
    {
        var serial = new FakeSerial(); await using var connection = Fixtures.Connection(serial);
        var task = connection.ReadAsync(0, new() { Mode = CompletionMode.Length, Length = 20 }, new("read"), default);
        serial.Feed(1, 2, 3); serial.Disconnect();
        var read = await task;
        Assert.Equal("disconnected", read.Completion); Assert.Equal("010203", read.Hex);
        await Assert.ThrowsAsync<PortsterException>(() => connection.WriteAsync(new() { Data = "FF" }, new("write"), default));
    }
    [Fact]
    public async Task CaptureSurvivesReceiveBufferOverrunAndConnectionClose()
    {
        var serial = new FakeSerial(); await using var connection = Fixtures.Connection(serial, Fixtures.Policy with { ReceiveBufferBytes = 4 });
        var capture = connection.StartCapture(1024);
        serial.Feed(0, 1, 2, 3, 4, 5, 6, 7);
        await Fixtures.Eventually(() => capture.Buffer.End == 8);
        var normal = await connection.ReadAsync(0, new() { WaitMs = 0 }, new("read"), default);
        Assert.Equal(4, normal.LostBytes);
        await connection.DisposeAsync();
        var recorded = await capture.ReadAsync(0, new() { WaitMs = 0 }, default);
        Assert.Equal("0001020304050607", recorded.Hex); Assert.Equal(0, recorded.LostBytes);
        Assert.True(capture.Stopped);
    }
    [Fact]
    public async Task AuditFailurePreventsWritingAndPolicyCannotBeBypassed()
    {
        var serial = new FakeSerial(); var audit = new MemoryAudit { Fail = true };
        await using var connection = Fixtures.Connection(serial, audit: audit);
        Assert.Equal("AUDIT_UNAVAILABLE", (await Assert.ThrowsAsync<PortsterException>(() => connection.WriteAsync(new() { Data = "01" }, new("write"), default))).Code);
        Assert.Empty(serial.Writes);
        await using var denied = Fixtures.Connection(new(), Fixtures.Policy with { AllowWrites = false });
        Assert.Equal("WRITE_NOT_ALLOWED", (await Assert.ThrowsAsync<PortsterException>(() => denied.WriteAsync(new() { Data = "01" }, new("write"), default))).Code);
    }
    [Fact]
    public async Task BreakIsClearedWhenCancelled()
    {
        var serial = new FakeSerial(); await using var connection = Fixtures.Connection(serial, Fixtures.Policy with { AllowSignals = true });
        using var cancellation = new CancellationTokenSource();
        var task = connection.SendBreakAsync(1000, new("break"), cancellation.Token);
        await Fixtures.Eventually(() => serial.Break);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(serial.Break); Assert.Equal(1, serial.BreakCleared);
    }
    [Fact]
    public async Task OpenIsExclusiveAndOldHandlesCannotReachReopenedPort()
    {
        var factory = new FakeFactory(() => new FakeSerial());
        using var registry = new ConnectionRegistry(Fixtures.Policy, new FakeCatalog(new PortInfo("COM7")), factory, new MemoryAudit(), NullLogger<ConnectionRegistry>.Instance);
        var first = await registry.OpenAsync("COM7", new(), null, new("open"), default);
        Assert.Equal("PORT_BUSY", (await Assert.ThrowsAsync<PortsterException>(() => registry.OpenAsync("COM7", new(), null, new("open"), default))).Code);
        await registry.CloseAsync(first.ConnectionHandle, new("close"), default);
        var second = await registry.OpenAsync("COM7", new(), null, new("open"), default);
        Assert.NotEqual(first.ConnectionHandle, second.ConnectionHandle);
        Assert.Equal("CLOSED_HANDLE", Assert.Throws<PortsterException>(() => registry.Get(first.ConnectionHandle)).Code);
        await registry.CloseAsync(second.ConnectionHandle, new("close"), default);
    }
    [Fact]
    public async Task CancelledOpenKeepsReservationUntilNativeOpenIsCleanedUp()
    {
        using var release = new ManualResetEventSlim(); using var entered = new ManualResetEventSlim();
        var serial = new FakeSerial(); var factory = new FakeFactory(() => { entered.Set(); release.Wait(3000); return serial; });
        using var registry = new ConnectionRegistry(Fixtures.Policy, new FakeCatalog(new PortInfo("COM7")), factory, new MemoryAudit(), NullLogger<ConnectionRegistry>.Instance);
        using var cancellation = new CancellationTokenSource();
        var open = registry.OpenAsync("COM7", new(), null, new("open"), cancellation.Token);
        await Fixtures.Eventually(() => entered.IsSet); cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open);
            Assert.Equal("PORT_BUSY", (await Assert.ThrowsAsync<PortsterException>(() => registry.OpenAsync("COM7", new(), null, new("open"), default))).Code);
            Assert.Equal(1, factory.OpenCount);
        }
        finally { release.Set(); }
        await Fixtures.Eventually(() => serial.Disposed);
    }
    [Fact]
    public async Task RateLimitAndDuplicateReadAreBounded()
    {
        var serial = new FakeSerial(); await using var connection = Fixtures.Connection(serial, Fixtures.Policy with { MinWriteIntervalMs = 10000 });
        await connection.WriteAsync(new() { Data = "01" }, new("write"), default);
        Assert.Equal("RATE_LIMITED", (await Assert.ThrowsAsync<PortsterException>(() => connection.WriteAsync(new() { Data = "01" }, new("write"), default))).Code);
        using var cancel = new CancellationTokenSource();
        var first = connection.ReadAsync(0, new() { WaitMs = 10000 }, new("read"), cancel.Token);
        Assert.Equal("READ_BUSY", (await Assert.ThrowsAsync<PortsterException>(() => connection.ReadAsync(0, new(), new("read"), default))).Code);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }
    [Fact]
    public async Task DriverLineErrorsAreReportedWithoutInventingLostByteCounts()
    {
        var serial = new FakeSerial(); await using var connection = Fixtures.Connection(serial);
        serial.Feed(1, 2); serial.ReadFault = new Portster.Platform.SerialLineException("Framing error");
        var result = await Fixtures.Runner().Run("read", op => connection.ReadAsync(0,
            new() { Mode = CompletionMode.Length, Length = 3 }, op, default), default);
        Assert.True(result.IsError);
        Assert.Equal("SERIAL_LINE_ERROR", result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, result.StructuredContent.Value.GetProperty("data").GetProperty("lostBytes").GetInt64());
    }
    [Fact]
    public void ExplicitRestrictionsAndInvalidPortNamesAreStillRejected()
    {
        Assert.Equal("PORT_NOT_ALLOWED", Assert.Throws<PortsterException>(() => new ServerPolicy { AllowedPorts = [] }.DemandPort("COM7")).Code);
        Assert.Equal("INVALID_PORT", Assert.Throws<PortsterException>(() => new ServerPolicy().DemandPort("../policy")).Code);
        Assert.Throws<InvalidOperationException>(() => (Fixtures.Policy with { ReceiveBufferBytes = 4194304, MaxConnections = 32 }).Validate());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultsPermitOpeningWritingAndSignalsWithOrWithoutAProfile(bool useProfile)
    {
        using var temp = new TemporaryStore();
        var policy = new ServerPolicy();
        var serial = new FakeSerial();
        var factory = new FakeFactory(() => serial);
        using var registry = new ConnectionRegistry(policy, new FakeCatalog(new PortInfo("COM8")), factory,
            new MemoryAudit(), NullLogger<ConnectionRegistry>.Instance);
        var settings = new SerialSettings { Dtr = true };
        var profile = new ProfileStore(temp.Paths, policy).Save(new DeviceProfile
        {
            Id = "default-permissions",
            DisplayName = "Default permissions",
            Selector = new() { LastSeenPort = "COM8" },
            Settings = settings
        }, ProfileScope.Global, null);
        var opened = useProfile
            ? await registry.OpenProfileAsync(profile, new("open"), default)
            : await registry.OpenAsync("COM8", settings, null, new("open"), default);
        try
        {
            Assert.True(factory.LastSettings!.Dtr);
            var connection = registry.Get(opened.ConnectionHandle);
            await connection.WriteAsync(new() { Data = "AA" }, new("write"), default);
            Assert.Equal(new byte[] { 0xAA }, Assert.Single(serial.Writes));
            await Task.Delay(policy.MinWriteIntervalMs + 10);
            await connection.SetSignalsAsync(true, true, new("signals"), default);
            Assert.True(serial.Dtr);
            Assert.True(serial.Rts);
            await Task.Delay(policy.MinWriteIntervalMs + 10);
            await connection.SendBreakAsync(1, new("break"), default);
            Assert.Equal(1, serial.BreakCleared);
            Assert.False(serial.Break);
        }
        finally
        {
            await registry.CloseAsync(opened.ConnectionHandle, new("close"), default);
        }
    }
}
