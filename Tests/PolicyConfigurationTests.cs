using System.Text.Json;

namespace Portster.Tests;

[CollectionDefinition("Process environment", DisableParallelization = true)]
public class ProcessEnvironmentCollection { }

[Collection("Process environment")]
public class PolicyConfigurationTests
{
    private sealed class EnvironmentValue : IDisposable
    {
        private readonly string name;
        private readonly string? previous;
        public EnvironmentValue(string name, string? value)
        { this.name = name; previous = Environment.GetEnvironmentVariable(name); Environment.SetEnvironmentVariable(name, value); }
        public void Dispose() => Environment.SetEnvironmentVariable(name, previous);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingConfigurationPermitsAllPortsWritesAndSignals(string? path)
    {
        using var env = new EnvironmentValue("PORTSTER_CONFIG", path);
        var policy = ServerPolicy.Load();
        policy.DemandPort("COM7");
        policy.DemandPort("com999");
        Assert.True(policy.AllowWrites);
        Assert.True(policy.AllowSignals);
    }
    [Theory]
    [InlineData("{}", 4)]
    [InlineData("{\"maxConnections\":2}", 2)]
    public void OmittedPermissionsKeepTheDefaults(string json, int maxConnections)
    {
        using var temp = new TemporaryStore(); Directory.CreateDirectory(temp.Root);
        var path = Path.Combine(temp.Root, "policy.json");
        File.WriteAllText(path, json);
        using var env = new EnvironmentValue("PORTSTER_CONFIG", path);
        var policy = ServerPolicy.Load();
        policy.DemandPort("COM7");
        policy.DemandPort("COM8");
        Assert.True(policy.AllowWrites);
        Assert.True(policy.AllowSignals);
        Assert.Equal(maxConnections, policy.MaxConnections);
    }
    [Fact]
    public void ExplicitConfigurationLoadsValidatedPolicyAndSupportsCaseInsensitivePorts()
    {
        using var temp = new TemporaryStore(); Directory.CreateDirectory(temp.Root);
        var path = Path.Combine(temp.Root, "policy.json");
        File.WriteAllText(path, JsonSerializer.Serialize(Fixtures.Policy with { AllowedPorts = ["COM7"], AllowWrites = false, AllowSignals = false }, Json.Options));
        using var env = new EnvironmentValue("PORTSTER_CONFIG", path);
        var policy = ServerPolicy.Load(); Assert.False(policy.AllowWrites); Assert.False(policy.AllowSignals); policy.DemandPort("com7");
        Assert.Throws<PortsterException>(() => policy.DemandPort("COM8"));
        (policy with { AllowedPorts = ["*"] }).DemandPort("COM8");
    }
    [Fact]
    public void InvalidConfigurationNeverFallsBackToPermissiveDefaults()
    {
        using var temp = new TemporaryStore(); Directory.CreateDirectory(temp.Root);
        var path = Path.Combine(temp.Root, "policy.json"); using var env = new EnvironmentValue("PORTSTER_CONFIG", path);
        foreach (var json in new[] { "{", "{\"allowSignalz\":true}", "{\"maxConnections\":\"unbounded\"}" })
        { File.WriteAllText(path, json); Assert.Throws<JsonException>(() => ServerPolicy.Load()); }
        File.WriteAllText(path, "null"); Assert.Throws<InvalidOperationException>(() => ServerPolicy.Load());
        File.WriteAllText(path, "{\"maxConnections\":0}"); Assert.Throws<InvalidOperationException>(() => ServerPolicy.Load());
        File.Delete(path); Assert.Throws<FileNotFoundException>(() => ServerPolicy.Load());
    }
    [Fact]
    public void StorageUsesExplicitRootsAndHasNoImplicitProjectScope()
    {
        using var temp = new TemporaryStore();
        using (var data = new EnvironmentValue("PORTSTER_DATA", temp.Root))
        using (var project = new EnvironmentValue("PORTSTER_PROJECT", Path.Combine(temp.Root, "workspace")))
        {
            var paths = StoragePaths.FromEnvironment();
            Assert.Equal(Path.Combine(temp.Root, "profiles"), paths.GlobalProfiles);
            Assert.Equal(Path.Combine(temp.Root, "audit"), paths.Audit);
            Assert.Equal(Path.Combine(temp.Root, "workspace", ".portster", "profiles"), paths.ProjectProfiles);
        }
        using (var data = new EnvironmentValue("PORTSTER_DATA", null))
        using (var project = new EnvironmentValue("PORTSTER_PROJECT", null))
        {
            var paths = StoragePaths.FromEnvironment();
            Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), paths.GlobalProfiles);
            Assert.Null(paths.ProjectProfiles);
        }
        Assert.False(Directory.Exists(temp.Root));
    }
    [Fact]
    public void ResourceConfigurationRejectsInvalidLimitsAndUnsafeCombinations()
    {
        foreach (var policy in new[]
        {
            Fixtures.Policy with { AllowedPorts = null! }, Fixtures.Policy with { AllowedPorts = ["COM0"] },
            Fixtures.Policy with { AllowedPorts = Enumerable.Repeat("COM7", 257).ToArray() },
            Fixtures.Policy with { MaxConnections = 0 }, Fixtures.Policy with { MaxConnections = 33 },
            Fixtures.Policy with { ReceiveBufferBytes = 1023 }, Fixtures.Policy with { ReceiveBufferBytes = 4194305 },
            Fixtures.Policy with { MaxReadBytes = 0 }, Fixtures.Policy with { MaxReadBytes = 65537 },
            Fixtures.Policy with { ReceiveBufferBytes = 1024, MaxReadBytes = 1025 },
            Fixtures.Policy with { MaxWriteBytes = 0 }, Fixtures.Policy with { MaxWriteBytes = 65537 },
            Fixtures.Policy with { MaxWaitMs = 99 }, Fixtures.Policy with { MaxWaitMs = 60001 },
            Fixtures.Policy with { WriteTimeoutMs = 99 }, Fixtures.Policy with { WriteTimeoutMs = 10001 },
            Fixtures.Policy with { MinWriteIntervalMs = -1 }, Fixtures.Policy with { MinWriteIntervalMs = 60001 },
            Fixtures.Policy with { MinReadIntervalMs = 9 }, Fixtures.Policy with { MinReadIntervalMs = 1001 },
            Fixtures.Policy with { InactivityTimeoutSeconds = 9 }, Fixtures.Policy with { InactivityTimeoutSeconds = 86401 },
            Fixtures.Policy with { MaxCaptures = 0 }, Fixtures.Policy with { MaxCaptures = 33 },
            Fixtures.Policy with { MaxCaptureBytes = 1023 }, Fixtures.Policy with { MaxCaptureBytes = 16777217 },
            Fixtures.Policy with { CaptureLifetimeSeconds = 9 }, Fixtures.Policy with { CaptureLifetimeSeconds = 86401 },
            Fixtures.Policy with { MaxProfiles = 0 }, Fixtures.Policy with { MaxProfiles = 4097 },
            Fixtures.Policy with { MaxProfileBytes = 1023 }, Fixtures.Policy with { MaxProfileBytes = 1048577 },
            Fixtures.Policy with { AuditFileBytes = 65535 }, Fixtures.Policy with { AuditFileBytes = 52428801 },
            Fixtures.Policy with { AuditFileCount = 0 }, Fixtures.Policy with { AuditFileCount = 21 },
            Fixtures.Policy with { AuditPayloads = true, MaxWriteBytes = 65536, AuditFileBytes = 65536 },
            Fixtures.Policy with { MaxConnections = 32, ReceiveBufferBytes = 4194304 }
        }) Assert.Throws<InvalidOperationException>(policy.Validate);
        Fixtures.Policy.Validate();
        (Fixtures.Policy with { MaxConnections = 32 }).Validate(); // Exactly 16 MiB across configured receive/capture buffers.
        (Fixtures.Policy with { AuditPayloads = true, MaxWriteBytes = 65536 }).Validate();
    }
}
