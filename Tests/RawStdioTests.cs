using System.Diagnostics;
using System.Text.Json;

namespace Portster.Tests;

// Deliberately uses no MCP SDK for encoding/transport: an independent legacy wire check.
public class RawStdioTests
{
    [Fact]
    public async Task LegacyJsonRpcClientCanDiscoverAndReadTextFallbackWithoutStdoutNoise()
    {
        using var temp = new TemporaryStore();
        using var timeout = new CancellationTokenSource(20000);
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        start.ArgumentList.Add(McpIntegrationTests.ServerDll());
        start.Environment["PORTSTER_DATA"] = temp.Root;
        start.Environment["PORTSTER_CONFIG"] = "";
        start.Environment["PORTSTER_PROJECT"] = "";
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            async Task<JsonElement> Request(object request, int id)
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
                await process.StandardInput.FlushAsync(timeout.Token);
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                    Assert.NotNull(line);
                    using var json = JsonDocument.Parse(line); // Any diagnostic on stdout fails here.
                    if (json.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id)
                        return json.RootElement.Clone();
                }
            }
            var initialized = await Request(new
            {
                jsonrpc = "2.0", id = 1, method = "initialize",
                @params = new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "PortsterRawTest", version = "1.0" } }
            }, 1);
            Assert.False(initialized.TryGetProperty("error", out _));
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            var listed = await Request(new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } }, 2);
            var names = listed.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
            Assert.Equal(19, names.Length);
            Assert.Contains("serial_transact", names);
            var called = await Request(new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "profile_list", arguments = new { } } }, 3);
            var result = called.GetProperty("result");
            using var text = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
            Assert.Empty(text.RootElement.GetProperty("data").EnumerateArray());
            var ports = await Request(new { jsonrpc = "2.0", id = 4, method = "tools/call", @params = new { name = "serial_list_ports", arguments = new { } } }, 4);
            Assert.False(ports.TryGetProperty("error", out _));
            using var portText = JsonDocument.Parse(ports.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
            Assert.Equal(JsonValueKind.Array, portText.RootElement.GetProperty("data").ValueKind);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
