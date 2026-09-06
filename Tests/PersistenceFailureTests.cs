using System.Text.Json;

namespace Portster.Tests;

public class PersistenceFailureTests
{
    [Fact]
    public async Task ConcurrentCreatesHaveExactlyOneWinner()
    {
        using var temp = new TemporaryStore();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() =>
        {
            var store = new ProfileStore(temp.Paths, Fixtures.Policy);
            try { store.Save(Fixtures.Profile with { DisplayName = $"Writer {i}" }, ProfileScope.Global, null); return "saved"; }
            catch (PortsterException ex) { return ex.Code; }
        })));
        Assert.Equal(1, results.Count(r => r == "saved")); Assert.Equal(11, results.Count(r => r == "PROFILE_EXISTS"));
        Assert.Equal(1, new ProfileStore(temp.Paths, Fixtures.Policy).Get("test-board", ProfileScope.Global).Revision);
    }
    [Fact]
    public async Task ConcurrentUpdatesCannotLoseAnAcceptedRevision()
    {
        using var temp = new TemporaryStore(); var store = new ProfileStore(temp.Paths, Fixtures.Policy);
        var original = store.Save(Fixtures.Profile, ProfileScope.Global, null);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() =>
        {
            try { return store.Save(original with { DisplayName = $"Writer {i}" }, ProfileScope.Global, 1).DisplayName; }
            catch (PortsterException ex) { return ex.Code; }
        })));
        var winner = Assert.Single(results, r => r != "REVISION_CONFLICT");
        var saved = store.Get(original.Id, ProfileScope.Global);
        Assert.Equal(winner, saved.DisplayName); Assert.Equal(2, saved.Revision);
    }
    [Fact]
    public void FailedAtomicReplacePreservesTheOriginalAndRemovesTemporaryFile()
    {
        using var temp = new TemporaryStore(); var store = new ProfileStore(temp.Paths, Fixtures.Policy);
        var saved = store.Save(Fixtures.Profile, ProfileScope.Global, null);
        var path = Path.Combine(temp.Paths.GlobalProfiles, saved.Id + ".json");
        var originalBytes = File.ReadAllBytes(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = Record.Exception(() => store.Save(saved with { DisplayName = "Must not replace" }, ProfileScope.Global, 1));
            Assert.True(failure is IOException or UnauthorizedAccessException, $"Unexpected failure: {failure}");
        }
        Assert.Equal(originalBytes, File.ReadAllBytes(path)); Assert.Empty(Directory.GetFiles(temp.Paths.GlobalProfiles, "*.tmp"));
    }
    [Fact]
    public void StorageContentionTimesOutWithoutChangingTheProfile()
    {
        using var temp = new TemporaryStore(); var store = new ProfileStore(temp.Paths, Fixtures.Policy);
        var saved = store.Save(Fixtures.Profile, ProfileScope.Global, null);
        using (var locked = new FileStream(Path.Combine(temp.Paths.GlobalProfiles, ".portster.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal("STORE_BUSY", Assert.Throws<PortsterException>(() => store.Save(saved, ProfileScope.Global, 1)).Code);
        Assert.Equal(1, store.Get(saved.Id, ProfileScope.Global).Revision);
    }
    [Fact]
    public void OversizedUpdateLeavesTheExistingProfileUntouched()
    {
        using var temp = new TemporaryStore(); var policy = Fixtures.Policy with { MaxProfileBytes = 2048 };
        var store = new ProfileStore(temp.Paths, policy); var saved = store.Save(Fixtures.Profile, ProfileScope.Global, null);
        var updated = saved with { Knowledge = [new("large", new string('A', 3000), KnowledgeSource.Observed)] };
        Assert.Equal("PROFILE_TOO_LARGE", Assert.Throws<PortsterException>(() => store.Save(updated, ProfileScope.Global, 1)).Code);
        Assert.Equal(1, store.Get(saved.Id, ProfileScope.Global).Revision); Assert.Empty(Directory.GetFiles(temp.Paths.GlobalProfiles, "*.tmp"));
    }
    [Fact]
    public void OversizedAndMisnamedFilesAreRejectedWhenLoaded()
    {
        using var temp = new TemporaryStore(); var store = new ProfileStore(temp.Paths, Fixtures.Policy);
        var saved = store.Save(Fixtures.Profile, ProfileScope.Global, null);
        var path = Path.Combine(temp.Paths.GlobalProfiles, saved.Id + ".json");
        File.WriteAllText(path, new string(' ', Fixtures.Policy.MaxProfileBytes + 1));
        Assert.Equal("PROFILE_TOO_LARGE", Assert.Throws<PortsterException>(() => store.Get(saved.Id, ProfileScope.Global)).Code);
        File.WriteAllText(path, JsonSerializer.Serialize(saved with { Id = "another-board" }, Json.Options));
        Assert.Equal("INVALID_PROFILE", Assert.Throws<PortsterException>(() => store.Get(saved.Id, ProfileScope.Global)).Code);
    }
    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("[]")]
    public void MalformedSavedProfilesAreNotRewritten(string json)
    {
        using var temp = new TemporaryStore(); Directory.CreateDirectory(temp.Paths.GlobalProfiles);
        var path = Path.Combine(temp.Paths.GlobalProfiles, "board.json"); File.WriteAllText(path, json);
        Assert.Equal("INVALID_PROFILE", Assert.Throws<PortsterException>(() => new ProfileStore(temp.Paths, Fixtures.Policy).Get("board", ProfileScope.Global)).Code);
        Assert.Equal(json, File.ReadAllText(path));
    }
    [Fact]
    public void ProfileLimitsAreEnforcedForCreatesAndOversizedExistingCatalogs()
    {
        using var temp = new TemporaryStore(); var store = new ProfileStore(temp.Paths, Fixtures.Policy with { MaxProfiles = 1 });
        store.Save(Fixtures.Profile, ProfileScope.Global, null);
        Assert.Equal("PROFILE_LIMIT", Assert.Throws<PortsterException>(() => store.Save(Fixtures.Profile with { Id = "second" }, ProfileScope.Global, null)).Code);
        File.WriteAllText(Path.Combine(temp.Paths.GlobalProfiles, "second.json"), JsonSerializer.Serialize(Fixtures.Profile with { Id = "second" }, Json.Options));
        Assert.Equal("PROFILE_LIMIT", Assert.Throws<PortsterException>(() => store.List(ProfileScope.Global)).Code);
    }
    [Fact]
    public void GlobalAndProjectProfilesWithTheSameIdRemainIndependent()
    {
        using var temp = new TemporaryStore(); var store = new ProfileStore(temp.Paths, Fixtures.Policy);
        store.Save(Fixtures.Profile with { DisplayName = "Global" }, ProfileScope.Global, null);
        store.Save(Fixtures.Profile with { DisplayName = "Project" }, ProfileScope.Project, null);
        store.Delete(Fixtures.Profile.Id, ProfileScope.Global, 1);
        Assert.Equal("Project", store.Get(Fixtures.Profile.Id, ProfileScope.Project).DisplayName);
        Assert.Equal("PROFILE_NOT_FOUND", Assert.Throws<PortsterException>(() => store.Get(Fixtures.Profile.Id, ProfileScope.Global)).Code);
    }
    [Fact]
    public void MissingOrInvalidScopesAndProfilesHaveStableErrors()
    {
        using var temp = new TemporaryStore(); var store = new ProfileStore(temp.Paths with { ProjectProfiles = null }, Fixtures.Policy);
        Assert.Equal("PROJECT_SCOPE_UNAVAILABLE", Assert.Throws<PortsterException>(() => store.List(ProfileScope.Project)).Code);
        Assert.Equal("INVALID_SCOPE", Assert.Throws<PortsterException>(() => store.List((ProfileScope)99)).Code);
        Assert.Equal("PROFILE_NOT_FOUND", Assert.Throws<PortsterException>(() => store.Get("missing", ProfileScope.Global)).Code);
        Assert.Equal("PROFILE_NOT_FOUND", Assert.Throws<PortsterException>(() => store.Delete("missing", ProfileScope.Global, 1)).Code);
        Assert.False(Directory.Exists(temp.Paths.GlobalProfiles));
    }
    [Fact]
    public void ToolUpdatesCannotStripOfflineConfirmations()
    {
        using var temp = new TemporaryStore(); var store = new ProfileStore(temp.Paths, Fixtures.Policy);
        var saved = store.Save(Fixtures.Profile, ProfileScope.Global, null);
        var confirmed = saved with { Knowledge = [new("baud", "115200", KnowledgeSource.Documentation, "manual", true, DateTimeOffset.UtcNow)] };
        var path = Path.Combine(temp.Paths.GlobalProfiles, saved.Id + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(confirmed, Json.Options));
        Assert.True(store.Get(saved.Id, ProfileScope.Global).Knowledge[0].Confirmed);
        Assert.Equal("CONFIRMED_PROFILE", Assert.Throws<PortsterException>(() => store.Save(saved, ProfileScope.Global, 1)).Code);
        Assert.True(store.Get(saved.Id, ProfileScope.Global).Knowledge[0].Confirmed);
    }
    [Fact]
    public void InvalidKnowledgeCannotAcquireTrustOrExceedStorageRules()
    {
        using var temp = new TemporaryStore(); var store = new ProfileStore(temp.Paths, Fixtures.Policy);
        foreach (var fact in new KnowledgeFact[] { null!, new("", "value", KnowledgeSource.User), new("name", null!, KnowledgeSource.User),
            new("name", "value", (KnowledgeSource)99), new("name", new string('x', 8193), KnowledgeSource.User) })
            Assert.Equal("INVALID_KNOWLEDGE", Assert.Throws<PortsterException>(() => store.Validate(Fixtures.Profile with { Knowledge = [fact] })).Code);
        Assert.Equal("CONFIRMATION_NOT_ALLOWED", Assert.Throws<PortsterException>(() => store.Validate(Fixtures.Profile with { Knowledge = [new("name", "value", KnowledgeSource.Observed, ValidatedUtc: DateTimeOffset.UtcNow)] })).Code);
        Assert.Equal("INVALID_KNOWLEDGE", Assert.Throws<PortsterException>(() => store.Validate(Fixtures.Profile with { Knowledge = [new("name", "value", KnowledgeSource.Inferred, Confirmed: true)] }, fromTool: false)).Code);
    }
}
