namespace Portster;

public sealed record WriteData(int BytesSubmitted, string WriteDisposition, string DeviceOutcome = "unknown");
