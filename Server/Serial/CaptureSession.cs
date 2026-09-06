using System.Diagnostics;

namespace Portster;

public sealed class CaptureSession(string connectionHandle, int capacity, ServerPolicy policy)
{
    private readonly object captureLock = new();
    private int readInProgress;
    private long nextReadTimestamp;

    public string Handle { get; } = Guid.NewGuid().ToString("N");
    public string ConnectionHandle { get; } = connectionHandle;
    public ReceiveBuffer Buffer { get; } = new(capacity);
    public DateTimeOffset ExpiresUtc { get; } = DateTimeOffset.UtcNow.AddSeconds(policy.CaptureLifetimeSeconds);

    private readonly long createdTimestamp = Stopwatch.GetTimestamp();

    public bool Expired => Stopwatch.GetElapsedTime(createdTimestamp).TotalSeconds >= policy.CaptureLifetimeSeconds;

    public bool Stopped { get; private set; }

    public CaptureInfo Describe()
    {
        lock (captureLock)
        {
            return new(Handle, ConnectionHandle, 0, capacity, Stopped, ExpiresUtc);
        }
    }

    public void Append(ReadOnlySpan<byte> bytes, DateTimeOffset utc, long timestamp)
    {
        lock (captureLock)
        {
            if (Expired)
            {
                Stop("expired");
            }
            if (!Stopped)
            {
                Buffer.Append(bytes, utc, timestamp);
            }
        }
    }

    public void Stop(string reason = "stopped")
    {
        lock (captureLock)
        {
            Stopped = true;
            Buffer.Complete(reason);
        }
    }

    public async Task<ReadData> ReadAsync(long cursor, ReadOptions? options, CancellationToken token)
    {
        if (Expired)
        {
            throw new PortsterException("EXPIRED_CAPTURE", "Capture retention has expired.");
        }
        if (Interlocked.CompareExchange(ref readInProgress, 1, 0) != 0)
        {
            throw new PortsterException("READ_BUSY", "Only one read may wait on this capture at a time.");
        }
        try
        {
            SerialConnection.RateLimit(ref nextReadTimestamp, policy.MinReadIntervalMs);
            return await BufferReader.ReadAsync(Buffer, cursor, options ?? new(), policy, token);
        }
        finally
        {
            Volatile.Write(ref readInProgress, 0);
        }
    }
}
