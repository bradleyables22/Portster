using System.Collections.Concurrent;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Portster;
using Json.Schema;

namespace Portster.Tests;

public class McpIntegrationTests
{
    [Fact]
    public async Task SeparateServerProcessesCannotOverwriteTheSameProfileRevision()
    {
        using var temp = new TemporaryStore(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        async Task<McpClient> Connect() => await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Command = "dotnet", Arguments = [ServerDll()],
            EnvironmentVariables = new Dictionary<string, string?> { ["PORTSTER_DATA"] = temp.Root, ["PORTSTER_CONFIG"] = "", ["PORTSTER_PROJECT"] = "" }
        }), cancellationToken: timeout.Token);
        await using var first = await Connect(); await using var second = await Connect();
        var created = await first.CallToolAsync("profile_create", new Dictionary<string, object?>
        { ["profile"] = JsonSerializer.SerializeToElement(Fixtures.Profile, Json.Options) }, cancellationToken: timeout.Token);
        Assert.NotEqual(true, created.IsError);
        Task<CallToolResult> Update(McpClient client, string name) => client.CallToolAsync("profile_update", new Dictionary<string, object?>
        {
            ["profile"] = JsonSerializer.SerializeToElement(Fixtures.Profile with { DisplayName = name }, Json.Options), ["expectedRevision"] = 1
        }, cancellationToken: timeout.Token).AsTask();
        var results = await Task.WhenAll(Update(first, "Process A"), Update(second, "Process B"));
        var winner = Assert.Single(results, r => r.IsError != true);
        var loser = Assert.Single(results, r => r.IsError == true);
        Assert.Equal("REVISION_CONFLICT", loser.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        var fetched = await second.CallToolAsync("profile_get", new Dictionary<string, object?> { ["profileId"] = Fixtures.Profile.Id }, cancellationToken: timeout.Token);
        Assert.Equal(2, fetched.StructuredContent!.Value.GetProperty("data").GetProperty("revision").GetInt32());
        Assert.Equal(winner.StructuredContent!.Value.GetProperty("data").GetProperty("displayName").GetString(), fetched.StructuredContent.Value.GetProperty("data").GetProperty("displayName").GetString());
    }
    internal static string ServerDll()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Portster.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        return Path.Combine(directory.FullName, "Server", "bin", configuration, "net10.0", "Portster.dll");
    }
    [Theory]
    [InlineData("2026-07-28")]
    [InlineData("2025-11-25")]
    public async Task RealStdioServerAdvertisesSchemasAndReturnsStructuredToolErrors(string version)
    {
        using var temp = new TemporaryStore();
        Directory.CreateDirectory(temp.Root);
        var policyPath = Path.Combine(temp.Root, "policy.json");
        File.WriteAllText(policyPath, "{\"allowedPorts\":[]}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var errors = new ConcurrentQueue<string>();
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Command = "dotnet", Arguments = [ServerDll()],
            EnvironmentVariables = new Dictionary<string, string?> { ["PORTSTER_DATA"] = temp.Root, ["PORTSTER_CONFIG"] = policyPath, ["PORTSTER_PROJECT"] = "" },
            StandardErrorLines = line => errors.Enqueue(line)
        }), new() { ProtocolVersion = version }, cancellationToken: timeout.Token);
        Assert.Equal("Portster", client.ServerInfo.Name);
        Assert.Equal(ServerInstructions.All, client.ServerInstructions);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.Equal(19, tools.Count);
        Assert.Equal(19, tools.Select(t => t.Name).Distinct().Count());
        Assert.DoesNotContain(tools, t => t.Name.Contains("random"));
        foreach (var tool in tools) Assert.NotNull(tool.ProtocolTool.OutputSchema);
        void AssertConforms(string toolName, CallToolResult response)
        {
            var schemaText = tools.Single(t => t.Name == toolName).ProtocolTool.OutputSchema!.Value.GetRawText();
            var schema = JsonSchema.FromText(schemaText);
            var validation = schema.Evaluate(response.StructuredContent!.Value);
            Assert.True(validation.IsValid, $"{toolName}: {JsonSerializer.Serialize(validation)}\n{schemaText}\n{response.StructuredContent}");
        }
        Assert.False(tools.Single(t => t.Name == "serial_write").ProtocolTool.Annotations!.ReadOnlyHint);
        var listed = await client.CallToolAsync("profile_list", new Dictionary<string, object?>(), cancellationToken: timeout.Token);
        Assert.NotEqual(true, listed.IsError);
        Assert.Equal(0, listed.StructuredContent!.Value.GetProperty("data").GetArrayLength());
        AssertConforms("profile_list", listed);
        var invalid = await client.CallToolAsync("serial_read", new Dictionary<string, object?> { ["connectionHandle"] = "not-a-handle", ["cursor"] = 0 }, cancellationToken: timeout.Token);
        Assert.True(invalid.IsError);
        Assert.Equal("INVALID_HANDLE", invalid.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        AssertConforms("serial_read", invalid);
        var create = await client.CallToolAsync("profile_create", new Dictionary<string, object?>
        {
            ["profile"] = new { id = Fixtures.Profile.Id, displayName = Fixtures.Profile.DisplayName, selector = new { lastSeenPort = "COM7" } }
        }, cancellationToken: timeout.Token);
        Assert.NotEqual(true, create.IsError);
        Assert.Equal(1, create.StructuredContent!.Value.GetProperty("data").GetProperty("revision").GetInt32());
        var safety = create.StructuredContent.Value.GetProperty("data").GetProperty("safety");
        Assert.True(safety.GetProperty("allowWrites").GetBoolean());
        Assert.True(safety.GetProperty("allowSignals").GetBoolean());
        AssertConforms("profile_create", create);
        Assert.True(File.Exists(Path.Combine(temp.Root, "profiles", "test-board.json")));
        var denied = await client.CallToolAsync("serial_open", new Dictionary<string, object?>
        { ["portName"] = "COM7", ["settings"] = new Dictionary<string, object?>() }, cancellationToken: timeout.Token);
        Assert.True(denied.IsError);
        Assert.Equal("PORT_NOT_ALLOWED", denied.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        AssertConforms("serial_open", denied);
    }
}
