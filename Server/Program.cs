using System.Text.Json.Schema;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Portster;
using Portster.Platform;
using Portster.Tools;

if (args is ["--export-profile-schema", var schemaPath])
{
    var schema = Json.Options.GetJsonSchemaAsNode(typeof(DeviceProfile));
    schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
    File.WriteAllText(Path.GetFullPath(schemaPath), schema.ToJsonString(Json.Options));
    return;
}

var builder = Host.CreateApplicationBuilder(args);
var policy = ServerPolicy.Load();
var paths = StoragePaths.FromEnvironment();

// STDIO clients read stdout as JSON-RPC, so diagnostics must go to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(policy);
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton<IAuditLog, AuditLog>();
builder.Services.AddSingleton<IPortCatalog, WindowsPortCatalog>();
builder.Services.AddSingleton<ISerialTransportFactory, SerialTransportFactory>();
builder.Services.AddSingleton<ProfileStore>();
builder.Services.AddSingleton<ToolRunner>();
builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ConnectionRegistry>());

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation { Name = "Portster", Version = "0.1.0-beta" };
        options.ServerInstructions = ServerInstructions.All;
    })
    .WithStdioServerTransport()
    .WithTools<SerialTools>(Json.Options)
    .WithTools<ProfileTools>(Json.Options);

await builder.Build().RunAsync();
