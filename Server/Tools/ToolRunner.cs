using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Portster;

public sealed class ToolRunner(IAuditLog audit, ILogger<ToolRunner> logger)
{
    public async Task<CallToolResult> Run<T>(string name, Func<OperationContext, Task<T>> action, CancellationToken token)
    {
        var operation = new OperationContext(name);
        T? data = default;
        ToolError? error = null;
        var auditStarted = false;

        try
        {
            try
            {
                audit.Record(operation, "started", "pending");
                auditStarted = true;
            }
            catch (PortsterException ex) when (name is "serial_close" or "capture_stop")
            {
                // Resource cleanup must remain possible when the disk/log is unavailable.
                error = new(ex.Code, "Audit storage is unavailable; cleanup was allowed to proceed. Inspect data for its result.", "notAttempted");
            }
            token.ThrowIfCancellationRequested();
            data = await action(operation);
            var read = data switch
            {
                ReadData readData => readData,
                TransactionData transaction => transaction.Read,
                _ => null
            };
            if (read is not null)
            {
                operation.BytesRead = read.ByteCount;
                error = GetReadError(read, operation.WriteDisposition);
            }
        }
        catch (PortsterException ex)
        {
            error = new(ex.Code, ex.Message, operation.WriteDisposition == "notAttempted" ? ex.WriteDisposition : operation.WriteDisposition);
        }
        catch (OperationCanceledException)
        {
            error = new(
                "CANCELLED",
                "Operation cancelled. Consult writeDisposition before considering another write.",
                operation.WriteDisposition);
        }
        catch (UnauthorizedAccessException)
        {
            error = new("ACCESS_DENIED", "The device or storage is busy or access was denied.", operation.WriteDisposition);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or JsonException)
        {
            logger.LogWarning(ex, "Operation {OperationId} failed", operation.Id);
            error = new(
                "IO_ERROR",
                "Device or storage operation failed. Check the device connection and server diagnostics.",
                operation.WriteDisposition);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error in {OperationId}", operation.Id);
            error = new(
                "INTERNAL_ERROR",
                "Unexpected server error. Check stderr diagnostics using the operation ID.",
                operation.WriteDisposition);
        }

        if (auditStarted)
        {
            try
            {
                audit.Record(operation, "completed", error?.Code ?? "ok");
            }
            catch (PortsterException ex)
            {
                error = new(
                    ex.Code,
                    "The operation finished but its completion audit could not be saved. Do not assume it had no effect.",
                    operation.WriteDisposition);
            }
        }

        var response = new ToolResponse<T>(operation.Id, data, error);
        return new CallToolResult
        {
            IsError = error is not null,
            StructuredContent = JsonSerializer.SerializeToElement(response, Json.Options),
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(response, Json.Options) }]
        };
    }

    private static ToolError? GetReadError(ReadData read, string writeDisposition)
    {
        return read.Completion switch
        {
            "overrun" => new(
                "BUFFER_OVERRUN",
                $"{read.LostBytes} bytes preceding startCursor were overwritten. Returned bytes remain available; resume at nextCursor.",
                writeDisposition),
            "timeout" => new(
                "READ_TIMEOUT",
                "Read deadline reached. Partial bytes are included; no command was automatically retried.",
                writeDisposition),
            "maxBytes" => new(
                "RESPONSE_LIMIT",
                "Response reached maxBytes before its framing condition. Partial bytes are included.",
                writeDisposition),
            "disconnected" or "ioFault" => new(
                "DEVICE_DISCONNECTED",
                "The receive stream ended. Partial bytes are included. Close this handle and explicitly reopen the device.",
                writeDisposition),
            "closed" or "expired" => new(
                "CONNECTION_ENDED",
                "The connection ended while reading. Partial bytes are included.",
                writeDisposition),
            "serialError" => new(
                "SERIAL_LINE_ERROR",
                "The driver reported a parity, framing, or buffer error. Lost wire bytes cannot be counted. Close and explicitly reopen the connection.",
                writeDisposition),
            _ => null
        };
    }
}
