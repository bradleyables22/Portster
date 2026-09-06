namespace Portster;

public sealed record PortInfo(string PortName, string? Vid = null, string? Pid = null,
    string? UsbSerialNumber = null, string? InterfaceNumber = null, string? Manufacturer = null,
    string? Product = null, string? InstanceId = null, string? MetadataWarning = null);
