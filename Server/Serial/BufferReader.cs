using System.Diagnostics;
using System.Text;

namespace Portster;

public static class BufferReader
{
    public static async Task<ReadData> ReadAsync(ReceiveBuffer buffer, long cursor, ReadOptions options,
        ServerPolicy policy, CancellationToken cancellationToken)
    {
        var delimiter = options.Validate(policy);
        var timer = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = buffer.Snapshot(cursor, options.MaxBytes);
            var count = snapshot.Bytes.Length;
            string? completion = snapshot.Lost > 0 ? "overrun" : null;
            if (completion is null)
            {
                switch (options.Mode)
                {
                    case CompletionMode.Available when count > 0:
                        completion = "available";
                        break;
                    case CompletionMode.Length when count >= options.Length:
                        count = options.Length!.Value;
                        completion = "length";
                        break;
                    case CompletionMode.Delimiter:
                        var index = snapshot.Bytes.AsSpan().IndexOf(delimiter);
                        if (index >= 0)
                        {
                            count = index + delimiter!.Length;
                            completion = "delimiter";
                        }
                        break;
                    case CompletionMode.IdleGap when count > 0 &&
                        snapshot.Start + count == snapshot.Latest &&
                        Stopwatch.GetElapsedTime(snapshot.LastTimestamp).TotalMilliseconds >= options.IdleGapMs:
                        completion = "idleGap";
                        break;
                }
            }
            completion ??= count == options.MaxBytes ? "maxBytes" : snapshot.EndState;
            completion ??= timer.ElapsedMilliseconds >= options.WaitMs ? "timeout" : null;
            if (completion is not null)
            {
                // Re-snapshot an exact frame length so timestamps describe returned bytes only.
                if (count != snapshot.Bytes.Length)
                {
                    snapshot = buffer.Snapshot(cursor, count);
                    if (snapshot.Lost > 0)
                    {
                        completion = "overrun";
                    }
                }
                var data = snapshot.Bytes;
                var preview = new StringBuilder();
                foreach (var value in data.AsSpan(0, Math.Min(data.Length, 256)))
                {
                    preview.Append(value is >= 32 and <= 126 ? (char)value : '.');
                }
                return new(snapshot.Start, snapshot.Start + data.Length, snapshot.Earliest, snapshot.Latest,
                    snapshot.Lost, completion, data.Length, Convert.ToBase64String(data), Convert.ToHexString(data),
                    preview.ToString(), snapshot.FirstUtc, snapshot.LastUtc, snapshot.EndState);
            }
            var delay = Math.Max(1, options.WaitMs - (int)timer.ElapsedMilliseconds);
            if (options.Mode == CompletionMode.IdleGap && count > 0)
            {
                delay = Math.Min(delay, Math.Max(1, options.IdleGapMs - (int)Stopwatch.GetElapsedTime(snapshot.LastTimestamp).TotalMilliseconds));
            }
            try
            {
                await snapshot.Changed.WaitAsync(TimeSpan.FromMilliseconds(delay), cancellationToken);
            }
            catch (TimeoutException)
            {
                // Check the read deadline and idle gap again after the wait.
            }
        }
    }
}
