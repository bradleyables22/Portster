namespace Portster;

public sealed record DeviceSelector
{
    public string? Vid { get; init; }
    public string? Pid { get; init; }
    public string? UsbSerialNumber { get; init; }
    public string? InterfaceNumber { get; init; }
    public string? LastSeenPort { get; init; }
}
