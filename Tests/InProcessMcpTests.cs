using System.IO.Pipelines;
using System.Text.Json;
using Json.Schema;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Portster.Platform;
using Portster.Tools;

namespace Portster.Tests;

internal sealed class McpHarness : IAsyncDisposable
{
    public TemporaryStore Storage { get; } = new();
    public FakeSerial? Serial { get; private set; }
    public FakeFactory Factory { get; }
    public MemoryAudit Audit { get; } = new();
    public IHost Host { get; private set; } = null!;
    public McpClient Client { get; private set; } = null!;
    private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
    private readonly Pipe input = new();
    private readonly Pipe output = new();
    public CancellationToken Token => timeout.Token;
    public McpHarness() => Factory = new(() => Serial = new FakeSerial());
    public async Task StartAsync()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(Fixtures.Policy with { AllowSignals = true });
        builder.Services.AddSingleton(Storage.Paths);
        builder.Services.AddSingleton<IAuditLog>(Audit);
        builder.Services.AddSingleton<IPortCatalog>(new FakeCatalog(new PortInfo("COM7", "0403", "6001", "AAA", "00")));
        builder.Services.AddSingleton<ISerialTransportFactory>(Factory);
        builder.Services.AddSingleton<ProfileStore>(); builder.Services.AddSingleton<ToolRunner>();
        builder.Services.AddSingleton<ConnectionRegistry>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ConnectionRegistry>());
        builder.Services.AddMcpServer(options => options.ServerInfo = new() { Name = "PortsterTest", Version = "1" })
            .WithStreamServerTransport(input.Reader.AsStream(), output.Writer.AsStream())
            .WithTools<SerialTools>(Json.Options).WithTools<ProfileTools>(Json.Options);
        Host = builder.Build(); await Host.StartAsync(Token);
        Client = await McpClient.CreateAsync(new StreamClientTransport(input.Writer.AsStream(), output.Reader.AsStream()), cancellationToken: Token);
    }
    public async Task<CallToolResult> Call(string name, object arguments)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(arguments, Json.Options))!;
        return await Client.CallToolAsync(name, values.ToDictionary(p => p.Key, p => (object?)p.Value), cancellationToken: Token);
    }
    public async ValueTask DisposeAsync()
    {
        if (Client is not null) await Client.DisposeAsync();
        if (Host is not null) { await Host.StopAsync(CancellationToken.None); Host.Dispose(); }
        timeout.Dispose(); Storage.Dispose();
    }
}

public class InProcessMcpTests
{
    [Fact]
    public async Task AllNineteenToolsCompleteADeviceWorkflowWithValidOutputSchemas()
    {
        await using var harness = new McpHarness(); await harness.StartAsync();
        var tools = await harness.Client.ListToolsAsync(cancellationToken: harness.Token);
        var schemas = tools.ToDictionary(t => t.Name, t => JsonSchema.FromText(t.ProtocolTool.OutputSchema!.Value.GetRawText()));
        var invoked = new HashSet<string>();
        async Task<JsonElement> Success(string name, object args)
        {
            invoked.Add(name); var result = await harness.Call(name, args);
            Assert.NotEqual(true, result.IsError);
            Assert.True(schemas[name].Evaluate(result.StructuredContent!.Value).IsValid, $"Schema mismatch for {name}: {result.StructuredContent}");
            return result.StructuredContent.Value.GetProperty("data");
        }
        Assert.Single((await Success("serial_list_ports", new { })).EnumerateArray());
        var explicitOpen = await Success("serial_open", new { portName = "COM7", settings = new SerialSettings() });
        await Success("serial_close", new { connectionHandle = explicitOpen.GetProperty("connectionHandle").GetString() });
        var profile = Fixtures.Profile with
        {
            Safety = new() { AllowSignals = true, MinWriteIntervalMs = 0 },
            Framing = new() { Mode = CompletionMode.Delimiter, DelimiterHex = "0A" }
        };
        await Success("profile_validate", new { profile });
        await Success("profile_create", new { profile });
        Assert.Single((await Success("profile_list", new { })).EnumerateArray());
        await Success("profile_get", new { profileId = profile.Id });
        var updated = await Success("profile_update", new { profile = profile with { DisplayName = "Updated" }, expectedRevision = 1 });
        Assert.Equal(2, updated.GetProperty("revision").GetInt32());
        var opened = await Success("serial_open_profile", new { profileId = profile.Id });
        var handle = opened.GetProperty("connectionHandle").GetString()!;
        Assert.Equal(2, opened.GetProperty("profileRevision").GetInt32());
        var capture = await Success("capture_start", new { connectionHandle = handle, capacityBytes = 1024 });
        var captureHandle = capture.GetProperty("captureHandle").GetString()!;
        harness.Serial!.Feed(65, 10);
        var read = await Success("serial_read", new { connectionHandle = handle, cursor = 0 });
        Assert.Equal("410A", read.GetProperty("hex").GetString());
        var recorded = await Success("capture_read", new { captureHandle, cursor = 0 });
        Assert.Equal("410A", recorded.GetProperty("hex").GetString());
        await Success("serial_write", new { connectionHandle = handle, payload = new Payload { Data = "0102" } });
        harness.Serial.OnWrite = _ => harness.Serial.Feed(79, 75, 10);
        var transaction = await Success("serial_transact", new { connectionHandle = handle, payload = new Payload { Encoding = PayloadEncoding.Utf8, Data = "ping\n" } });
        Assert.Equal("4F4B0A", transaction.GetProperty("read").GetProperty("hex").GetString());
        await Success("serial_set_signals", new { connectionHandle = handle, dtr = true, rts = false });
        Assert.True((await Success("serial_get_signals", new { connectionHandle = handle })).GetProperty("dtr").GetBoolean());
        await Success("serial_send_break", new { connectionHandle = handle, durationMs = 1 });
        await Success("capture_stop", new { captureHandle });
        await Success("serial_close", new { connectionHandle = handle });
        await Success("profile_delete", new { profileId = profile.Id, expectedRevision = 2 });
        Assert.Equal(19, invoked.Count);
        Assert.Equal(tools.Select(t => t.Name), (await harness.Client.ListToolsAsync(cancellationToken: harness.Token)).Select(t => t.Name));
    }
    [Fact]
    public async Task UnknownNestedProfileFieldsAreRejectedRatherThanSilentlyDiscarded()
    {
        await using var harness = new McpHarness(); await harness.StartAsync();
        var profile = JsonSerializer.SerializeToNode(Fixtures.Profile, Json.Options)!;
        profile["initializationCommands"] = "erase";
        var result = await harness.Call("profile_create", new { profile });
        Assert.True(result.IsError); Assert.Equal(0, harness.Factory.OpenCount);
        Assert.False(File.Exists(Path.Combine(harness.Storage.Paths.GlobalProfiles, "test-board.json")));
    }
    [Fact]
    public async Task MalformedToolArgumentsNeverReachHardware()
    {
        await using var harness = new McpHarness(); await harness.StartAsync();
        foreach (var arguments in new object[]
        {
            new { portName = "COM7", settings = new { baudRate = "not a number" } },
            new { portName = "COM7", settings = new { parity = "Imaginary" } },
            new { portName = "COM7" }, new { portName = "COM7", settings = (object?)null }
        })
        {
            var response = await harness.Call("serial_open", arguments);
            Assert.True(response.IsError);
        }
        Assert.Equal(0, harness.Factory.OpenCount);
    }
    [Fact]
    public async Task EveryHandleBasedToolRejectsUnknownHandlesThroughTheProtocol()
    {
        await using var harness = new McpHarness(); await harness.StartAsync();
        foreach (var (tool, args) in new (string, object)[]
        {
            ("serial_read", new { connectionHandle = "bad", cursor = 0 }),
            ("serial_write", new { connectionHandle = "bad", payload = new Payload { Data = "AA" } }),
            ("serial_transact", new { connectionHandle = "bad", payload = new Payload { Data = "AA" } }),
            ("serial_close", new { connectionHandle = "bad" }), ("serial_get_signals", new { connectionHandle = "bad" }),
            ("serial_set_signals", new { connectionHandle = "bad", dtr = true }),
            ("serial_send_break", new { connectionHandle = "bad", durationMs = 1 }),
            ("capture_start", new { connectionHandle = "bad" }), ("capture_read", new { captureHandle = "bad", cursor = 0 }),
            ("capture_stop", new { captureHandle = "bad" })
        })
        {
            var result = await harness.Call(tool, args); Assert.True(result.IsError);
            var code = result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString();
            Assert.Contains(code, new[] { "INVALID_HANDLE", "INVALID_CAPTURE" });
        }
        Assert.Equal(0, harness.Factory.OpenCount);
    }
    [Fact]
    public async Task LiveConnectionsKeepTheirOpenedProfileRevisionAfterAnEdit()
    {
        await using var harness = new McpHarness(); await harness.StartAsync();
        await harness.Call("profile_create", new { profile = Fixtures.Profile });
        var result = await harness.Call("serial_open_profile", new { profileId = Fixtures.Profile.Id });
        var handle = result.StructuredContent!.Value.GetProperty("data").GetProperty("connectionHandle").GetString()!;
        await harness.Call("profile_update", new { profile = Fixtures.Profile with { Settings = new() { BaudRate = 9600 } }, expectedRevision = 1 });
        var connection = harness.Host.Services.GetRequiredService<ConnectionRegistry>().Get(handle);
        Assert.Equal(1, connection.Profile!.Revision); Assert.Equal(115200, connection.Settings.BaudRate);
        await harness.Call("serial_close", new { connectionHandle = handle });
        var reopened = await harness.Call("serial_open_profile", new { profileId = Fixtures.Profile.Id });
        Assert.Equal(2, reopened.StructuredContent!.Value.GetProperty("data").GetProperty("profileRevision").GetInt32());
        Assert.Equal(9600, harness.Factory.LastSettings!.BaudRate);
    }
}
