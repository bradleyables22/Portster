namespace Portster;

public sealed record OpenedConnection(string ConnectionHandle, PortInfo Device, SerialSettings Settings,
    long InitialCursor, int BufferCapacityBytes, int InactivityTimeoutSeconds, string? ProfileId, int? ProfileRevision,
    string TargetIdentity = "unverified");
