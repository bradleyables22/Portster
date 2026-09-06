using System.IO.Ports;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace Portster.Platform;

public sealed class WindowsPortCatalog : IPortCatalog
{
    private readonly object sync = new();
    private Task<IReadOnlyList<PortInfo>>? pending;

    public async Task<IReadOnlyList<PortInfo>> ListAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PortsterException("UNSUPPORTED_PLATFORM", "Windows discovery is the only provider in this release.");
        }
        Task<IReadOnlyList<PortInfo>> task;
        lock (sync)
        {
            // Share a pending WMI request: a stuck provider cannot cause unbounded worker growth.
            if (pending is null || pending.IsCompleted)
            {
                pending = Task.Run(Enumerate);
            }
            task = pending;
        }
        try
        {
            return await task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new PortsterException("DISCOVERY_TIMEOUT", "Windows device discovery exceeded five seconds. Check the WMI service and retry.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<PortInfo> Enumerate()
    {
        var ports = SerialPort.GetPortNames().Where(ServerPolicy.IsPortName).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => p.ToUpperInvariant(), p => new PortInfo(p.ToUpperInvariant()), StringComparer.OrdinalIgnoreCase);
        try
        {
            // PnPEntity includes USB serial devices missing from Win32_SerialPort on some drivers.
            using var searcher = new ManagementObjectSearcher("SELECT Name, Manufacturer, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
            searcher.Options.Timeout = TimeSpan.FromSeconds(4);
            using var results = searcher.Get();
            foreach (ManagementObject device in results)
            {
                using (device)
                {
                    var name = device["Name"] as string ?? "";
                    var match = Regex.Match(name, @"\((COM[0-9]+)\)$", RegexOptions.IgnoreCase);
                    if (!match.Success || !ports.ContainsKey(match.Groups[1].Value))
                    {
                        continue;
                    }
                    var port = match.Groups[1].Value.ToUpperInvariant();
                    var instance = device["PNPDeviceID"] as string;
                    var identity = ReadUsbIdentity(instance);
                    ports[port] = new(port, identity.Vid, identity.Pid, identity.Serial, identity.Interface,
                        device["Manufacturer"] as string, name, instance,
                        identity.Serial is null ? "A trustworthy USB serial number was not available; matching by VID/PID alone may be ambiguous." : null);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            foreach (var port in ports.Keys.ToArray())
            {
                ports[port] = ports[port] with
                {
                    MetadataWarning = "USB metadata unavailable. Explicit port access is still possible."
                };
            }
        }
        return ports.Values.OrderBy(p => p.PortName, StringComparer.Ordinal).ToArray();
    }

    [SupportedOSPlatform("windows")]
    private static (string? Vid, string? Pid, string? Serial, string? Interface) ReadUsbIdentity(string? instance)
    {
        if (instance is null)
        {
            return (null, null, null, null);
        }
        var vid = Match(instance, @"VID_([0-9A-F]{4})");
        var pid = Match(instance, @"PID_([0-9A-F]{4})");
        var iface = Match(instance, @"MI_([0-9A-F]{2})");
        string? serial = null;
        // Walk to the USB parent for composite devices and FTDI bus children. A USB
        // instance suffix is a serial only when Windows marks the device UniqueID.
        if (CM_Locate_DevNodeW(out var node, instance, 0) == 0)
        {
            for (var depth = 0; depth < 8; depth++)
            {
                var id = new StringBuilder(1024);
                if (CM_Get_Device_IDW(node, id, id.Capacity, 0) != 0)
                {
                    break;
                }
                var value = id.ToString();
                if (value.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase))
                {
                    vid ??= Match(value, @"VID_([0-9A-F]{4})");
                    pid ??= Match(value, @"PID_([0-9A-F]{4})");
                    uint size = sizeof(uint);
                    if (CM_Get_DevNode_Registry_PropertyW(node, 16, out _, out var capabilities, ref size, 0) == 0 &&
                        (capabilities & 0x10) != 0 && Match(value, @"MI_([0-9A-F]{2})") is null)
                    {
                        serial = value[(value.LastIndexOf('\\') + 1)..];
                        break;
                    }
                }
                if (CM_Get_Parent(out var parent, node, 0) != 0)
                {
                    break;
                }
                node = parent;
            }
        }
        return (vid, pid, serial, iface);
    }

    private static string? Match(string value, string pattern)
    {
        var match = Regex.Match(value, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Locate_DevNodeW(out uint node, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_IDW(uint node, StringBuilder buffer, int length, uint flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_DevNode_Registry_PropertyW(uint node, uint property, out uint registryType, out uint buffer, ref uint length, uint flags);
}
