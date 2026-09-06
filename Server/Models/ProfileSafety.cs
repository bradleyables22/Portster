namespace Portster;

public sealed record ProfileSafety
{
    public bool AllowWrites { get; init; } = true;
    public bool AllowSignals { get; init; } = true;
    public int MaxWriteBytes { get; init; } = 4096;
    public int MinWriteIntervalMs { get; init; } = 100;
}
