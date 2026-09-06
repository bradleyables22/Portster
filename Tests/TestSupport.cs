using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Portster;
using Portster.Platform;

namespace Portster.Tests;

internal sealed class FakeSerial : ISerialTransport
{
    private readonly BlockingCollection<byte[]> incoming = new();
    public ConcurrentQueue<byte[]> Writes { get; } = new();
    public Action<byte[]>? OnWrite { get; set; }
    public Func<SignalData>? OnGetSignals { get; set; }
    public Action<bool?, bool?>? OnSetSignals { get; set; }
    public Action<bool>? OnBreak { get; set; }
    public Action? OnDispose { get; set; }
    public bool Dtr { get; private set; }
    public bool Rts { get; private set; }
    public bool Disposed { get; private set; }
    public Exception? ReadFault { get; set; }
    public bool Break { get; private set; }
    public int BreakCleared { get; private set; }
    public ManualResetEventSlim WriteEntered { get; } = new();
    public void Feed(params byte[] bytes) => incoming.Add(bytes);
    public void Disconnect() => incoming.CompleteAdding();
    public int Read(byte[] buffer)
    {
        if (incoming.TryTake(out var data, 20)) { data.CopyTo(buffer, 0); return data.Length; }
        if (incoming.IsCompleted) throw new IOException("unplugged");
        if (ReadFault is not null) throw ReadFault;
        throw new TimeoutException();
    }
    public void Write(byte[] data) { Writes.Enqueue(data); WriteEntered.Set(); OnWrite?.Invoke(data); }
    public SignalData GetSignals() => OnGetSignals?.Invoke() ?? new(true, false, true, Dtr, Rts, Break);
    public void SetSignals(bool? dtr, bool? rts)
    { OnSetSignals?.Invoke(dtr, rts); if (dtr.HasValue) Dtr = dtr.Value; if (rts.HasValue) Rts = rts.Value; }
    public void SetBreak(bool enabled) { OnBreak?.Invoke(enabled); Break = enabled; if (!enabled) BreakCleared++; }
    public void Dispose() { OnDispose?.Invoke(); Disposed = true; incoming.CompleteAdding(); }
}
internal sealed class MemoryAudit : IAuditLog
{
    public ConcurrentQueue<(string Phase, string Outcome)> Events { get; } = new();
    public bool Fail { get; set; }
    public Action<OperationContext, string, string>? BeforeRecord { get; set; }
    public void Record(OperationContext op, string phase, string outcome)
    {
        if (Fail) throw new PortsterException("AUDIT_UNAVAILABLE", "Test audit failure");
        BeforeRecord?.Invoke(op, phase, outcome);
        Events.Enqueue((phase, outcome));
    }
}
internal sealed class FakeCatalog(params PortInfo[] ports) : IPortCatalog
{
    public Func<int, IReadOnlyList<PortInfo>>? OnList { get; set; }
    private int calls;
    public Task<IReadOnlyList<PortInfo>> ListAsync(CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.FromResult(OnList?.Invoke(Interlocked.Increment(ref calls)) ?? ports); }
}
internal sealed class FakeFactory(Func<ISerialTransport> create) : ISerialTransportFactory
{
    public int OpenCount;
    public SerialSettings? LastSettings { get; private set; }
    public ISerialTransport Open(string portName, SerialSettings settings, int writeTimeoutMs)
    { LastSettings = settings; Interlocked.Increment(ref OpenCount); return create(); }
}
internal sealed class TemporaryStore : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "PortsterTests", Guid.NewGuid().ToString("N"));
    public StoragePaths Paths => new(Path.Combine(Root, "global"), Path.Combine(Root, "project"), Path.Combine(Root, "audit"));
    public void Dispose()
    {
        var full = Path.GetFullPath(Root);
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PortsterTests")) + Path.DirectorySeparatorChar;
        if (full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full)) Directory.Delete(full, true);
    }
}
internal static class Fixtures
{
    public static ServerPolicy Policy => new() { AllowedPorts = ["COM7"], AllowSignals = false, MinWriteIntervalMs = 0 };
    public static DeviceProfile Profile => new() { Id = "test-board", DisplayName = "Test board", Selector = new() { LastSeenPort = "COM7" }, Safety = new() { AllowSignals = false } };
    public static SerialConnection Connection(FakeSerial serial, ServerPolicy? policy = null, MemoryAudit? audit = null) =>
        new(serial, new("COM7"), new(), null, policy ?? Policy, audit ?? new());
    public static ToolRunner Runner(IAuditLog? audit = null) => new(audit ?? new MemoryAudit(), NullLogger<ToolRunner>.Instance);
    public static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(3000);
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
