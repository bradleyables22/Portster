namespace Portster;

public sealed record ReadOptions
{
    public CompletionMode Mode { get; init; } = CompletionMode.Available;
    public int MaxBytes { get; init; } = 4096;
    public int WaitMs { get; init; } = 1000;
    public string? DelimiterHex { get; init; }
    public int? Length { get; init; }
    public int IdleGapMs { get; init; } = 100;

    public byte[]? Validate(ServerPolicy policy)
    {
        if (!Enum.IsDefined(Mode) || MaxBytes < 1 || MaxBytes > policy.MaxReadBytes || WaitMs < 0 || WaitMs > policy.MaxWaitMs)
        {
            throw new PortsterException("READ_LIMIT", $"maxBytes must be 1..{policy.MaxReadBytes}; waitMs 0..{policy.MaxWaitMs}.");
        }
        if (Mode == CompletionMode.Length && (Length is null || Length < 1 || Length > MaxBytes))
        {
            throw new PortsterException("INVALID_FRAMING", "Length completion requires length between 1 and maxBytes.");
        }
        if (Mode == CompletionMode.IdleGap && (IdleGapMs < 20 || IdleGapMs > policy.MaxWaitMs))
        {
            throw new PortsterException(
                "INVALID_FRAMING",
                "idleGapMs must be at least 20 and within the server wait limit. Timing is host-observed, not wire timing.");
        }
        if (Mode != CompletionMode.Delimiter)
        {
            return null;
        }
        try
        {
            if (DelimiterHex is null || DelimiterHex.Length is < 2 or > 128)
            {
                throw new FormatException();
            }
            var delimiter = Convert.FromHexString(DelimiterHex);
            if (delimiter.Length > MaxBytes)
            {
                throw new FormatException();
            }
            return delimiter;
        }
        catch (FormatException)
        {
            throw new PortsterException("INVALID_FRAMING", "Delimiter completion requires 1..64 hex bytes, fitting within maxBytes.");
        }
    }
}
