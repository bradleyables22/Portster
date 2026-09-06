namespace Portster;

public sealed record ReadData(long StartCursor, long NextCursor, long EarliestCursor, long LatestCursor,
    long LostBytes, string Completion, int ByteCount, string Base64, string Hex, string TextPreview,
    DateTimeOffset? FirstReceivedUtc, DateTimeOffset? LastReceivedUtc, string? ConnectionState = null);
