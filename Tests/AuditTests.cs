using System.Text.Json;
using Portster;

namespace Portster.Tests;

public class AuditTests
{
    [Fact]
    public void AuditIsJsonLinesPayloadFreeByDefaultAndRotatesWithinLimits()
    {
        using var temp = new TemporaryStore();
        var policy = Fixtures.Policy with { AuditFileBytes = 65536, AuditFileCount = 2 };
        var log = new AuditLog(temp.Paths, policy);
        var op = new OperationContext("write") { PayloadBase64 = "DO-NOT-LOG", Device = new("COM7"), ProfileId = "board", ProfileRevision = 3 };
        for (var i = 0; i < 400; i++) log.Record(op, "completed", "ok");
        var files = Directory.GetFiles(temp.Paths.Audit, "*.jsonl");
        Assert.Equal(2, files.Length);
        foreach (var file in files)
        {
            Assert.True(new FileInfo(file).Length <= policy.AuditFileBytes);
            foreach (var line in File.ReadLines(file))
            {
                using var json = JsonDocument.Parse(line);
                Assert.Equal(op.Id, json.RootElement.GetProperty("operationId").GetString());
                Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("payloadBase64").ValueKind);
            }
        }
    }
    [Fact]
    public async Task AuditFailureStillPermitsResourceCleanup()
    {
        var performed = false;
        var result = await Fixtures.Runner(new MemoryAudit { Fail = true }).Run("serial_close", _ =>
        {
            performed = true;
            return Task.FromResult(new Done());
        }, default);
        Assert.True(performed); Assert.True(result.IsError);
        Assert.True(result.StructuredContent!.Value.GetProperty("data").GetProperty("success").GetBoolean());
        Assert.Equal("AUDIT_UNAVAILABLE", result.StructuredContent.Value.GetProperty("error").GetProperty("code").GetString());
    }
    [Fact]
    public async Task ErrorResultsCarryMachineReadableCodesAndAnOperationId()
    {
        var audit = new MemoryAudit();
        var result = await Fixtures.Runner(audit).Run<Done>("test", _ => throw new PortsterException("TEST_ERROR", "Expected failure"), default);
        Assert.True(result.IsError); Assert.NotEmpty(result.Content);
        Assert.Equal("TEST_ERROR", result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        Assert.True(Guid.TryParse(result.StructuredContent.Value.GetProperty("operationId").GetString(), out _));
        Assert.Equal(new[] { ("started", "pending"), ("completed", "TEST_ERROR") }, audit.Events.ToArray());
    }
}
