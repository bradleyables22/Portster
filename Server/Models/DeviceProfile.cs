using System.ComponentModel;

namespace Portster;

public sealed record DeviceProfile
{
    public int SchemaVersion { get; init; } = 1;
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public int Revision { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
    public DeviceSelector Selector { get; init; } = new();
    public SerialSettings Settings { get; init; } = new();
    public ReadOptions Framing { get; init; } = new();
    public ProfileSafety Safety { get; init; } = new();
    [Description("Descriptive target identity only. The UART target is not authenticated by matching its USB adapter.")]
    public string? TargetDescription { get; init; }
    public KnowledgeFact[] Knowledge { get; init; } = [];
    public Dictionary<string, string> SecretReferences { get; init; } = [];
}
