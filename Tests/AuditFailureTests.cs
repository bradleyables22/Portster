using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Portster.Tests;

public class AuditFailureTests
{
    [Fact]
    public async Task CompletionAuditFailurePreservesDataAndTheWriteDisposition()
    {
        var audit = new MemoryAudit { BeforeRecord = (_, phase, _) => { if (phase == "completed") throw new PortsterException("AUDIT_UNAVAILABLE", "disk full"); } };
        var result = await Fixtures.Runner(audit).Run("serial_write", op =>
        {
            op.WriteDisposition = "submittedToDriver"; op.BytesSubmitted = 2;
            return Task.FromResult(new WriteData(2, op.WriteDisposition));
        }, default);
        Assert.True(result.IsError); var body = result.StructuredContent!.Value;
        Assert.Equal("AUDIT_UNAVAILABLE", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("submittedToDriver", body.GetProperty("error").GetProperty("writeDisposition").GetString());
        Assert.Equal(2, body.GetProperty("data").GetProperty("bytesSubmitted").GetInt32());
    }
    [Theory]
    [InlineData("overrun", "BUFFER_OVERRUN")]
    [InlineData("timeout", "READ_TIMEOUT")]
    [InlineData("maxBytes", "RESPONSE_LIMIT")]
    [InlineData("disconnected", "DEVICE_DISCONNECTED")]
    [InlineData("closed", "CONNECTION_ENDED")]
    [InlineData("expired", "CONNECTION_ENDED")]
    [InlineData("serialError", "SERIAL_LINE_ERROR")]
    public async Task ReadErrorsPreservePartialBytesInStructuredAndLegacyResults(string completion, string code)
    {
        var partial = new ReadData(3, 5, 3, 5, 3, completion, 2, "AQI=", "0102", "..", null, null);
        var result = await Fixtures.Runner().Run("serial_read", _ => Task.FromResult(partial), default);
        Assert.True(result.IsError); var body = result.StructuredContent!.Value;
        Assert.Equal(code, body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("0102", body.GetProperty("data").GetProperty("hex").GetString());
        using var legacy = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.True(JsonElement.DeepEquals(body, legacy.RootElement));
    }
    [Fact]
    public async Task ExceptionResultsAreSanitizedAndAudited()
    {
        const string privateDetail = "private device credentials must not appear in client output";
        foreach (var (exception, code) in new (Exception, string)[]
        {
            (new IOException(privateDetail), "IO_ERROR"), (new UnauthorizedAccessException(privateDetail), "ACCESS_DENIED"),
            (new OperationCanceledException(privateDetail), "CANCELLED"), (new NullReferenceException(privateDetail), "INTERNAL_ERROR")
        })
        {
            var audit = new MemoryAudit();
            var result = await Fixtures.Runner(audit).Run<Done>("operation", _ => throw exception, default);
            Assert.True(result.IsError);
            Assert.Equal(code, result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
            Assert.DoesNotContain(privateDetail, result.StructuredContent.Value.GetRawText());
            Assert.Contains(("completed", code), audit.Events);
        }
    }
    [Fact]
    public void PayloadLoggingRequiresExplicitOptInAndRecordsOperationContext()
    {
        using var temp = new TemporaryStore(); var log = new AuditLog(temp.Paths, Fixtures.Policy with { AuditPayloads = true });
        var operation = new OperationContext("serial_write")
        {
            PayloadBase64 = "AQI=", BytesRequested = 2, Device = new("COM7"), ProfileId = "board", ProfileRevision = 4,
            UartSettings = new() { BaudRate = 9600 }, ReceiveCursor = 42
        };
        log.Record(operation, "writeIntent", "pending");
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(temp.Paths.Audit, "operations.jsonl")));
        var body = json.RootElement;
        Assert.Equal("AQI=", body.GetProperty("payloadBase64").GetString()); Assert.Equal(4, body.GetProperty("profileRevision").GetInt32());
        Assert.Equal(9600, body.GetProperty("uartSettings").GetProperty("baudRate").GetInt32()); Assert.Equal(42, body.GetProperty("receiveCursor").GetInt64());
    }
    [Fact]
    public void AuditStorageFailureHasAnActionableCode()
    {
        using var temp = new TemporaryStore(); Directory.CreateDirectory(temp.Root); File.WriteAllText(temp.Paths.Audit, "not a directory");
        var error = Assert.Throws<PortsterException>(() => new AuditLog(temp.Paths, Fixtures.Policy).Record(new("read"), "started", "pending"));
        Assert.Equal("AUDIT_UNAVAILABLE", error.Code);
    }
    [Fact]
    public void ReducingAuditRetentionRemovesOnlyOldAuditArchivesOnRotation()
    {
        using var temp = new TemporaryStore(); Directory.CreateDirectory(temp.Paths.Audit);
        for (var i = 1; i <= 4; i++) File.WriteAllText(Path.Combine(temp.Paths.Audit, $"operations.{i}.jsonl"), "{}\n");
        File.WriteAllText(Path.Combine(temp.Paths.Audit, "operations.jsonl"), new string(' ', 65536));
        File.WriteAllText(Path.Combine(temp.Paths.Audit, "keep.txt"), "unrelated");
        new AuditLog(temp.Paths, Fixtures.Policy with { AuditFileBytes = 65536, AuditFileCount = 1 }).Record(new("read"), "started", "pending");
        Assert.Single(Directory.GetFiles(temp.Paths.Audit, "*.jsonl"));
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(temp.Paths.Audit, "keep.txt")));
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(temp.Paths.Audit, "operations.jsonl")));
        Assert.Equal("read", json.RootElement.GetProperty("tool").GetString());
    }
}
