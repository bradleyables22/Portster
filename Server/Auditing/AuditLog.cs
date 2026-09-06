using System.Text.Json;

namespace Portster;

public sealed class AuditLog(StoragePaths paths, ServerPolicy policy) : IAuditLog
{
    private static readonly JsonSerializerOptions AuditJson = new(Json.Options) { WriteIndented = false };

    public void Record(OperationContext operation, string phase, string outcome)
    {
        try
        {
            using var lease = FileLease.Acquire(paths.Audit);
            var path = Path.Combine(paths.Audit, "operations.jsonl");
            var recordBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                processId = Environment.ProcessId,
                operationId = operation.Id,
                tool = operation.Name,
                phase,
                outcome,
                connectionHandle = operation.ConnectionHandle,
                device = operation.Device,
                profileId = operation.ProfileId,
                profileRevision = operation.ProfileRevision,
                bytesSubmitted = operation.BytesSubmitted,
                bytesRead = operation.BytesRead,
                bytesRequested = operation.BytesRequested,
                receiveCursor = operation.ReceiveCursor,
                readOptions = operation.ReadOptions,
                uartSettings = operation.UartSettings,
                dtr = operation.Dtr,
                rts = operation.Rts,
                breakDurationMs = operation.BreakDurationMs,
                captureHandle = operation.CaptureHandle,
                writeDisposition = operation.WriteDisposition,
                payloadBase64 = policy.AuditPayloads ? operation.PayloadBase64 : null
            }, AuditJson);

            RotateIfNeeded(path, recordBytes.Length);

            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(recordBytes);
            stream.WriteByte((byte)'\n');
            stream.Flush(flushToDisk: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PortsterException)
        {
            throw new PortsterException(
                "AUDIT_UNAVAILABLE",
                "The audit record could not be saved. Check storage permissions, available disk space, and audit directory locks.",
                operation.WriteDisposition);
        }
    }

    private void RotateIfNeeded(string path, int recordLength)
    {
        if (!File.Exists(path) || new FileInfo(path).Length + recordLength + 1 <= policy.AuditFileBytes)
        {
            return;
        }

        foreach (var archived in Directory.EnumerateFiles(paths.Audit, "operations.*.jsonl"))
        {
            var part = Path.GetFileNameWithoutExtension(archived).Split('.').Last();
            if (int.TryParse(part, out var index) && index >= policy.AuditFileCount)
            {
                File.Delete(archived);
            }
        }

        for (var index = policy.AuditFileCount - 1; index >= 1; index--)
        {
            var destination = Path.Combine(paths.Audit, $"operations.{index}.jsonl");
            var source = index == 1 ? path : Path.Combine(paths.Audit, $"operations.{index - 1}.jsonl");
            if (File.Exists(source))
            {
                File.Move(source, destination, overwrite: true);
            }
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
