namespace Portster;

public sealed class OperationContext(string name)
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Name { get; } = name;
    public string? ConnectionHandle { get; set; }
    public PortInfo? Device { get; set; }
    public string? ProfileId { get; set; }
    public int? ProfileRevision { get; set; }
    public int BytesSubmitted { get; set; }
    public int BytesRead { get; set; }
    public int BytesRequested { get; set; }
    public long? ReceiveCursor { get; set; }
    public ReadOptions? ReadOptions { get; set; }
    public SerialSettings? UartSettings { get; set; }
    public bool? Dtr { get; set; }
    public bool? Rts { get; set; }
    public int? BreakDurationMs { get; set; }
    public string? CaptureHandle { get; set; }
    public string WriteDisposition { get; set; } = "notAttempted";
    public string? PayloadBase64 { get; set; }
}
