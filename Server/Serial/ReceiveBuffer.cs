namespace Portster;

// Cursors are absolute byte offsets within one buffer; a reconnect always creates a new handle.
public sealed class ReceiveBuffer
{
    private readonly object bufferLock = new();
    private readonly byte[] buffer;
    private readonly long[] utcTicks;
    private readonly long[] receiveTimestamps;
    private long endCursor;
    private string? completionState;
    private TaskCompletionSource dataChanged = CreateChangeSignal();

    public int Capacity => buffer.Length;

    public long End
    {
        get
        {
            lock (bufferLock)
            {
                return endCursor;
            }
        }
    }

    public ReceiveBuffer(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        buffer = new byte[capacity];
        utcTicks = new long[capacity];
        receiveTimestamps = new long[capacity];
    }

    private static TaskCompletionSource CreateChangeSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Append(ReadOnlySpan<byte> data, DateTimeOffset utc, long timestamp)
    {
        lock (bufferLock)
        {
            if (completionState is not null || data.Length == 0)
            {
                return;
            }
            // Skip already-overwritten bytes even when a single driver read exceeds capacity.
            var newEnd = checked(endCursor + data.Length);
            for (var i = Math.Max(0, data.Length - Capacity); i < data.Length; i++)
            {
                var index = (int)((endCursor + i) % Capacity);
                buffer[index] = data[i];
                utcTicks[index] = utc.UtcTicks;
                receiveTimestamps[index] = timestamp;
            }
            endCursor = newEnd;
            NotifyReaders();
        }
    }

    public void Complete(string state)
    {
        lock (bufferLock)
        {
            completionState ??= state;
            NotifyReaders();
        }
    }

    private void NotifyReaders()
    {
        var old = dataChanged;
        dataChanged = CreateChangeSignal();
        old.TrySetResult();
    }

    public BufferSnapshot Snapshot(long cursor, int count)
    {
        lock (bufferLock)
        {
            if (cursor < 0 || cursor > endCursor)
            {
                throw new PortsterException("INVALID_CURSOR", $"Cursor must be between 0 and {endCursor} for this handle.");
            }
            var earliest = Math.Max(0, endCursor - Capacity);
            var start = Math.Max(cursor, earliest);
            var length = (int)Math.Min(count, endCursor - start);
            var result = new byte[length];
            for (var i = 0; i < length; i++)
            {
                result[i] = buffer[(int)((start + i) % Capacity)];
            }
            return new(start, earliest, endCursor, start - cursor, result,
                length == 0 ? null : new DateTimeOffset(utcTicks[(int)(start % Capacity)], TimeSpan.Zero),
                length == 0 ? null : new DateTimeOffset(utcTicks[(int)((start + length - 1) % Capacity)], TimeSpan.Zero),
                length == 0 ? 0 : receiveTimestamps[(int)((start + length - 1) % Capacity)], completionState, dataChanged.Task);
        }
    }
}
