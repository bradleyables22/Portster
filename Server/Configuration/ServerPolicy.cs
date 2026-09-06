using System.Text.Json;
using System.Text.RegularExpressions;

namespace Portster;

// Only loaded at process startup from an operator-selected file. Never mutable through MCP.
public sealed record ServerPolicy
{
    private const string InvalidPolicyMessage =
        "Server policy contains invalid values. See portster.example.json and README for limits.";

    public string[] AllowedPorts { get; init; } = ["*"];
    public bool AllowWrites { get; init; } = true;
    public bool AllowSignals { get; init; } = true;
    public int MaxConnections { get; init; } = 4;
    public int ReceiveBufferBytes { get; init; } = 262144;
    public int MaxReadBytes { get; init; } = 16384;
    public int MaxWriteBytes { get; init; } = 4096;
    public int MaxWaitMs { get; init; } = 10000;
    public int WriteTimeoutMs { get; init; } = 2000;
    public int MinWriteIntervalMs { get; init; } = 100;
    public int MinReadIntervalMs { get; init; } = 25;
    public int InactivityTimeoutSeconds { get; init; } = 900;
    public int MaxCaptures { get; init; } = 8;
    public int MaxCaptureBytes { get; init; } = 1048576;
    public int CaptureLifetimeSeconds { get; init; } = 3600;
    public int MaxProfiles { get; init; } = 256;
    public int MaxProfileBytes { get; init; } = 65536;
    public bool AuditPayloads { get; init; }
    public int AuditFileBytes { get; init; } = 5242880;
    public int AuditFileCount { get; init; } = 5;

    public static ServerPolicy Load()
    {
        var path = Environment.GetEnvironmentVariable("PORTSTER_CONFIG");
        var policy = string.IsNullOrWhiteSpace(path) ? new() :
            JsonSerializer.Deserialize<ServerPolicy>(File.ReadAllText(Path.GetFullPath(path)), Json.Options)
            ?? throw new InvalidOperationException("PORTSTER_CONFIG must contain a JSON object.");
        policy.Validate();
        return policy;
    }

    public void Validate()
    {
        ValidateAllowedPorts();
        ValidateConnectionLimits();
        ValidateReadWriteLimits();
        ValidateCaptureLimits();
        ValidateStorageLimits();
        ValidateMemoryBudget();
    }

    private void ValidateAllowedPorts()
    {
        if (AllowedPorts is null || AllowedPorts.Length > 256 ||
            AllowedPorts.Any(port => port != "*" && !IsPortName(port)))
        {
            throw new InvalidOperationException(InvalidPolicyMessage);
        }
    }

    private void ValidateConnectionLimits()
    {
        if (MaxConnections is < 1 or > 32 ||
            ReceiveBufferBytes is < 1024 or > 4194304 ||
            InactivityTimeoutSeconds is < 10 or > 86400)
        {
            throw new InvalidOperationException(InvalidPolicyMessage);
        }
    }

    private void ValidateReadWriteLimits()
    {
        if (MaxReadBytes < 1 || MaxReadBytes > ReceiveBufferBytes || MaxReadBytes > 65536 ||
            MaxWriteBytes is < 1 or > 65536 ||
            MaxWaitMs is < 100 or > 60000 ||
            WriteTimeoutMs < 100 || WriteTimeoutMs > MaxWaitMs ||
            MinWriteIntervalMs is < 0 or > 60000 ||
            MinReadIntervalMs is < 10 or > 1000)
        {
            throw new InvalidOperationException(InvalidPolicyMessage);
        }
    }

    private void ValidateCaptureLimits()
    {
        if (MaxCaptures is < 1 or > 32 ||
            MaxCaptureBytes is < 1024 or > 16777216 ||
            CaptureLifetimeSeconds is < 10 or > 86400)
        {
            throw new InvalidOperationException(InvalidPolicyMessage);
        }
    }

    private void ValidateStorageLimits()
    {
        if (MaxProfiles is < 1 or > 4096 ||
            MaxProfileBytes is < 1024 or > 1048576 ||
            AuditFileBytes is < 65536 or > 52428800 ||
            AuditFileCount is < 1 or > 20 ||
            AuditPayloads && AuditFileBytes < MaxWriteBytes * 2 + 8192)
        {
            throw new InvalidOperationException(InvalidPolicyMessage);
        }
    }

    private void ValidateMemoryBudget()
    {
        var connectionBytes = (long)MaxConnections * ReceiveBufferBytes;
        var captureBytes = (long)MaxCaptures * MaxCaptureBytes;
        if (connectionBytes + captureBytes > 16777216)
        {
            throw new InvalidOperationException(InvalidPolicyMessage);
        }
    }

    public static bool IsPortName(string? port) => port is not null && Regex.IsMatch(port, @"\ACOM[1-9][0-9]{0,5}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public void DemandPort(string port)
    {
        if (!IsPortName(port))
        {
            throw new PortsterException("INVALID_PORT", "A Windows port name such as COM7 is required.");
        }
        if (!AllowedPorts.Contains("*") && !AllowedPorts.Contains(port, StringComparer.OrdinalIgnoreCase))
        {
            throw new PortsterException(
                "PORT_NOT_ALLOWED",
                "This port is not in the operator's allowedPorts setting. Configure PORTSTER_CONFIG before opening it.");
        }
    }
}
