using System.Text.Json;
using Portster;

namespace Portster.Tests;

public class ProfileTests
{
    [Fact]
    public void ProfileWritesAreAtomicRevisionCheckedAndScopeExplicit()
    {
        using var temp = new TemporaryStore();
        var store = new ProfileStore(temp.Paths, Fixtures.Policy);
        var created = store.Save(Fixtures.Profile, ProfileScope.Global, null);
        Assert.Equal(1, created.Revision);
        Assert.Empty(store.List(ProfileScope.Project));
        var updated = store.Save(created with { DisplayName = "Updated" }, ProfileScope.Global, 1);
        Assert.Equal(2, updated.Revision); Assert.Equal(created.CreatedUtc, updated.CreatedUtc);
        Assert.Equal("REVISION_CONFLICT", Assert.Throws<PortsterException>(() => store.Save(created, ProfileScope.Global, 1)).Code);
        Assert.Equal("Updated", store.Get(created.Id, ProfileScope.Global).DisplayName);
        Assert.Empty(Directory.GetFiles(temp.Paths.GlobalProfiles, "*.tmp"));
        Assert.Throws<PortsterException>(() => store.Delete(created.Id, ProfileScope.Global, 1));
        store.Delete(created.Id, ProfileScope.Global, 2);
        Assert.Empty(store.List(ProfileScope.Global));
    }
    [Fact]
    public void ProfileCannotEscalatePolicyOrConfirmItsOwnKnowledge()
    {
        using var temp = new TemporaryStore(); var store = new ProfileStore(temp.Paths, Fixtures.Policy);
        Assert.Equal("POLICY_DENIED", Assert.Throws<PortsterException>(() => store.Validate(Fixtures.Profile with { Safety = new() { AllowSignals = true } })).Code);
        Assert.Equal("CONFIRMATION_NOT_ALLOWED", Assert.Throws<PortsterException>(() => store.Validate(Fixtures.Profile with { Knowledge = [new("baud", "115200", KnowledgeSource.User, Confirmed: true)] })).Code);
        Assert.Throws<PortsterException>(() => store.Validate(Fixtures.Profile with { SecretReferences = new() { ["key"] = "actual-password" } }));
        Assert.Throws<PortsterException>(() => store.Validate(Fixtures.Profile with { Id = "../../policy" }));
        Assert.Throws<PortsterException>(() => store.Validate(Fixtures.Profile with { Knowledge = null! }));
    }
    [Fact]
    public void UnknownProfileSchemaAndFieldsAreRejectedWithoutRewriting()
    {
        using var temp = new TemporaryStore(); var store = new ProfileStore(temp.Paths, Fixtures.Policy);
        var saved = store.Save(Fixtures.Profile, ProfileScope.Global, null);
        var path = Path.Combine(temp.Paths.GlobalProfiles, saved.Id + ".json");
        var future = JsonSerializer.Serialize(saved with { SchemaVersion = 2 }, Json.Options);
        File.WriteAllText(path, future);
        Assert.Equal("UNSUPPORTED_PROFILE_VERSION", Assert.Throws<PortsterException>(() => store.Get(saved.Id, ProfileScope.Global)).Code);
        Assert.Equal(future, File.ReadAllText(path));
        File.WriteAllText(path, "{\"id\":\"test-board\",\"initializationCommands\":[\"erase\"]}");
        Assert.Equal("INVALID_PROFILE", Assert.Throws<PortsterException>(() => store.Get(saved.Id, ProfileScope.Global)).Code);
    }
    [Fact]
    public void ExportedSchemaAcceptsProfileShapeAndRejectsUnknownFields()
    {
        var root = Directory.GetParent(McpIntegrationTests.ServerDll())!;
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Portster.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var schema = global::Json.Schema.JsonSchema.FromText(File.ReadAllText(Path.Combine(root.FullName, "Server", "profile.schema.json")));
        Assert.True(schema.Evaluate(JsonSerializer.SerializeToElement(Fixtures.Profile, Json.Options)).IsValid);
        using var invalid = JsonDocument.Parse("{\"unrecognized\":true}");
        Assert.False(schema.Evaluate(invalid.RootElement).IsValid);
    }
    [Fact]
    public void MatchingNeverFallsBackToLastSeenPortOrGuessesBetweenAdapters()
    {
        PortInfo[] ports = [new("COM7", "0403", "6001", "AAA"), new("COM8", "0403", "6001", "BBB")];
        var ambiguous = new DeviceSelector { Vid = "0403", Pid = "6001", LastSeenPort = "COM7" };
        Assert.Equal("AMBIGUOUS_DEVICE", Assert.Throws<PortsterException>(() => ProfileStore.Resolve(ambiguous, ports)).Code);
        Assert.Equal("COM8", ProfileStore.Resolve(ambiguous with { UsbSerialNumber = "BBB" }, ports).PortName);
        Assert.Equal("DEVICE_NOT_FOUND", Assert.Throws<PortsterException>(() => ProfileStore.Resolve(ambiguous with { UsbSerialNumber = "MISSING" }, ports)).Code);
    }
}
