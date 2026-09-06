namespace Portster;

public sealed record CaptureInfo(string CaptureHandle, string ConnectionHandle, long InitialCursor,
    int CapacityBytes, bool Stopped, DateTimeOffset ExpiresUtc);
