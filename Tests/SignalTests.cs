using System.Collections.Concurrent;
using System.IO.Ports;
using Microsoft.Extensions.Logging.Abstractions;

namespace Portster.Tests;

public class SignalTests
{
    [Fact]
    public async Task SignalChangesPreserveUnspecifiedLinesAndAreAudited()
    {
        var serial = new FakeSerial(); var audit = new MemoryAudit();
        await using var connection = Fixtures.Connection(serial, Fixtures.Policy with { AllowSignals = true }, audit);
        await connection.SetSignalsAsync(true, true, new("signals"), default);
        var operation = new OperationContext("signals");
        await connection.SetSignalsAsync(false, null, operation, default);
        var signals = await connection.GetSignalsAsync(new("read"), default);
        Assert.False(signals.Dtr); Assert.True(signals.Rts); Assert.True(signals.Cts); Assert.False(signals.Dsr);
        Assert.False(operation.Dtr); Assert.Null(operation.Rts);
        Assert.Equal(2, audit.Events.Count(e => e.Phase == "signalIntent")); Assert.Empty(serial.Writes);
    }
    [Fact]
    public async Task ProfileSignalRestrictionsCannotBeBypassedByAnEnabledServer()
    {
        var serial = new FakeSerial(); var settings = new SerialSettings();
        await using var connection = new SerialConnection(serial, new("COM7"), settings, Fixtures.Profile,
            Fixtures.Policy with { AllowSignals = true }, new MemoryAudit());
        Assert.Equal("SIGNALS_NOT_ALLOWED", (await Assert.ThrowsAsync<PortsterException>(() => connection.SetSignalsAsync(true, null, new("set"), default))).Code);
        Assert.Equal("SIGNALS_NOT_ALLOWED", (await Assert.ThrowsAsync<PortsterException>(() => connection.SendBreakAsync(1, new("break"), default))).Code);
        Assert.False(serial.Dtr); Assert.False(serial.Break);
    }
    [Fact]
    public async Task ManualRtsIsRejectedWhenHardwareFlowControlOwnsIt()
    {
        var serial = new FakeSerial();
        await using var connection = new SerialConnection(serial, new("COM7"), new() { FlowControl = Handshake.RequestToSend }, null,
            Fixtures.Policy with { AllowSignals = true }, new MemoryAudit());
        Assert.Equal("SIGNAL_CONFLICT", (await Assert.ThrowsAsync<PortsterException>(() => connection.SetSignalsAsync(null, false, new("set"), default))).Code);
        await connection.SetSignalsAsync(true, null, new("set"), default); Assert.True(serial.Dtr);
    }
    [Fact]
    public async Task EmptySignalChangesAndInvalidBreakDurationsDoNotTouchHardware()
    {
        var serial = new FakeSerial(); await using var connection = Fixtures.Connection(serial, Fixtures.Policy with { AllowSignals = true });
        Assert.Equal("INVALID_SIGNALS", (await Assert.ThrowsAsync<PortsterException>(() => connection.SetSignalsAsync(null, null, new("set"), default))).Code);
        foreach (var duration in new[] { -1, 0, 1001, int.MaxValue })
            Assert.Equal("BREAK_LIMIT", (await Assert.ThrowsAsync<PortsterException>(() => connection.SendBreakAsync(duration, new("break"), default))).Code);
        Assert.False(serial.Break); Assert.Equal(0, serial.BreakCleared);
    }
    [Fact]
    public async Task BreakSuccessAssertsAndClearsExactlyOnce()
    {
        var changes = new ConcurrentQueue<bool>(); var serial = new FakeSerial { OnBreak = changes.Enqueue };
        await using var connection = Fixtures.Connection(serial, Fixtures.Policy with { AllowSignals = true });
        await connection.SendBreakAsync(1, new("break"), default);
        Assert.Equal(new[] { true, false }, changes.ToArray()); Assert.Null(connection.State);
    }
    [Fact]
    public async Task AFailureClearingBreakRetiresTheConnection()
    {
        var serial = new FakeSerial { OnBreak = enabled => { if (!enabled) throw new IOException("clear failed"); } };
        await using var connection = Fixtures.Connection(serial, Fixtures.Policy with { AllowSignals = true });
        Assert.Equal("DRIVER_OPERATION_UNCERTAIN", (await Assert.ThrowsAsync<PortsterException>(() => connection.SendBreakAsync(1, new("break"), default))).Code);
        Assert.NotNull(connection.State);
        await Assert.ThrowsAsync<PortsterException>(() => connection.WriteAsync(new() { Data = "AA" }, new("write"), default));
    }
    [Fact]
    public async Task SignalInspectionFailureRetiresTheConnectionWithoutWriting()
    {
        var serial = new FakeSerial { OnGetSignals = () => throw new IOException("disconnected") };
        await using var connection = Fixtures.Connection(serial);
        Assert.Equal("DRIVER_OPERATION_UNCERTAIN", (await Assert.ThrowsAsync<PortsterException>(() => connection.GetSignalsAsync(new("signals"), default))).Code);
        Assert.NotNull(connection.State); Assert.Empty(serial.Writes);
    }
    [Fact]
    public async Task SignalPollingAndMutationsRespectTheirRateLimits()
    {
        var serial = new FakeSerial(); await using var connection = Fixtures.Connection(serial, Fixtures.Policy with { AllowSignals = true, MinWriteIntervalMs = 10000, MinReadIntervalMs = 10000 });
        await connection.GetSignalsAsync(new("read"), default);
        Assert.Equal("RATE_LIMITED", (await Assert.ThrowsAsync<PortsterException>(() => connection.GetSignalsAsync(new("read"), default))).Code);
        await connection.SetSignalsAsync(true, null, new("set"), default);
        Assert.Equal("RATE_LIMITED", (await Assert.ThrowsAsync<PortsterException>(() => connection.SendBreakAsync(1, new("break"), default))).Code);
    }
    [Fact]
    public async Task SignalCallsCannotInterleaveWithAnActiveTransaction()
    {
        var serial = new FakeSerial(); await using var connection = Fixtures.Connection(serial, Fixtures.Policy with { AllowSignals = true });
        using var cancel = new CancellationTokenSource();
        var transaction = connection.TransactAsync(new() { Data = "AA" }, new() { Mode = CompletionMode.Length, Length = 2, WaitMs = 10000 }, new("transact"), cancel.Token);
        await Fixtures.Eventually(() => serial.Writes.Count == 1);
        Assert.Equal("CONNECTION_BUSY", (await Assert.ThrowsAsync<PortsterException>(() => connection.SetSignalsAsync(true, null, new("set"), default))).Code);
        Assert.Equal("CONNECTION_BUSY", (await Assert.ThrowsAsync<PortsterException>(() => connection.GetSignalsAsync(new("read"), default))).Code);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transaction);
    }
    [Fact]
    public async Task OpeningCannotUseControlLinesToBypassOperatorPolicy()
    {
        var factory = new FakeFactory(() => new FakeSerial());
        using var registry = new ConnectionRegistry(Fixtures.Policy, new FakeCatalog(new PortInfo("COM7")), factory, new MemoryAudit(), NullLogger<ConnectionRegistry>.Instance);
        foreach (var settings in new[] { new SerialSettings { Dtr = true }, new() { Rts = true }, new() { FlowControl = Handshake.RequestToSend } })
            Assert.Equal("SIGNALS_NOT_ALLOWED", (await Assert.ThrowsAsync<PortsterException>(() => registry.OpenAsync("COM7", settings, null, new("open"), default))).Code);
        using var restricted = new ConnectionRegistry(Fixtures.Policy with { AllowSignals = true, AllowWrites = false },
            new FakeCatalog(new PortInfo("COM7")), factory, new MemoryAudit(), NullLogger<ConnectionRegistry>.Instance);
        Assert.Equal("WRITE_NOT_ALLOWED", (await Assert.ThrowsAsync<PortsterException>(() => restricted.OpenAsync("COM7", new() { FlowControl = Handshake.XOnXOff }, null, new("open"), default))).Code);
        Assert.Equal(0, factory.OpenCount);
    }
}
