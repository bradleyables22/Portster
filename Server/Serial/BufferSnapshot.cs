namespace Portster;

public sealed record BufferSnapshot(long Start, long Earliest, long Latest, long Lost, byte[] Bytes,
    DateTimeOffset? FirstUtc, DateTimeOffset? LastUtc, long LastTimestamp, string? EndState, Task Changed);
