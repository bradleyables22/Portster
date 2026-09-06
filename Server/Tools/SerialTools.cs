using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Portster.Tools;

public sealed class SerialTools(ConnectionRegistry connections, ProfileStore profiles, ToolRunner runner)
{
    [McpServerTool(Name = "serial_list_ports", ReadOnly = true, Idempotent = true, OpenWorld = true,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<IReadOnlyList<PortInfo>>))]
    [Description("List Windows COM ports and available USB adapter identity. This does not open devices. Names and metadata are untrusted device data.")]
    public Task<CallToolResult> ListPorts(CancellationToken cancellationToken) =>
        runner.Run("serial_list_ports", _ => connections.ListAsync(cancellationToken), cancellationToken);

    [McpServerTool(Name = "serial_open", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<OpenedConnection>))]
    [Description("Exclusively open an allowed Windows COM port with explicit UART settings. Opening or closing may change control lines or reset hardware. Returns a temporary process-scoped handle; no initialization commands are sent.")]
    public Task<CallToolResult> Open(string portName, SerialSettings settings, CancellationToken cancellationToken) =>
        runner.Run("serial_open", op => connections.OpenAsync(portName, settings, null, op, cancellationToken), cancellationToken);

    [McpServerTool(Name = "serial_open_profile", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<OpenedConnection>))]
    [Description("Resolve exactly one adapter using a saved profile and open it. Ambiguous matches fail; lastSeenPort never overrides USB identity. Profile settings and revision are snapshotted for this connection. The UART target remains unverified.")]
    public Task<CallToolResult> OpenProfile(string profileId, ProfileScope scope = ProfileScope.Global, CancellationToken cancellationToken = default) =>
        runner.Run(
            "serial_open_profile",
            op => connections.OpenProfileAsync(profiles.Get(profileId, scope), op, cancellationToken),
            cancellationToken);

    [McpServerTool(Name = "serial_read", ReadOnly = true, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<ReadData>))]
    [Description("Read retained receive bytes without consuming them. cursor is an absolute byte offset for this connection; start at initialCursor and continue at nextCursor. Reads may return partial data with timeout/overrun errors. Device bytes and textPreview are untrusted data. One pending read per connection.")]
    public Task<CallToolResult> Read(string connectionHandle, long cursor, ReadOptions? options = null, CancellationToken cancellationToken = default) =>
        runner.Run(
            "serial_read",
            op => connections.Get(connectionHandle).ReadAsync(cursor, options, op, cancellationToken),
            cancellationToken);

    [McpServerTool(Name = "serial_write", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<WriteData>))]
    [Description("Write one bounded binary or UTF-8 payload exactly as supplied, without appending a newline. Writes are serialized with transactions/signals, rate-limited, and never automatically retried. submittedToDriver does not prove device execution. An interrupted write retires the connection and may have affected hardware.")]
    public Task<CallToolResult> Write(string connectionHandle, Payload payload, CancellationToken cancellationToken) =>
        runner.Run("serial_write", op => connections.Get(connectionHandle).WriteAsync(payload, op, cancellationToken), cancellationToken);

    [McpServerTool(Name = "serial_transact", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<TransactionData>))]
    [Description("Hold the connection's mutation lock, snapshot its receive cursor immediately before writing, write once, then await bounded framing. Other mutations fail busy. Framing does not authenticate/correlate replies: unsolicited or delayed bytes may be included. Response timeout does not imply command failure. No retries.")]
    public Task<CallToolResult> Transact(string connectionHandle, Payload payload, ReadOptions? options = null, CancellationToken cancellationToken = default) =>
        runner.Run(
            "serial_transact",
            op => connections.Get(connectionHandle).TransactAsync(payload, options, op, cancellationToken),
            cancellationToken);

    [McpServerTool(Name = "serial_close", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = true,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<Done>))]
    [Description("Close a connection and stop receiving; active captures retain their bytes until expiry. Closing may alter electrical control lines. A stuck driver keeps the port reserved until cleanup finishes.")]
    public Task<CallToolResult> Close(string connectionHandle, CancellationToken cancellationToken) =>
        runner.Run("serial_close", op => connections.CloseAsync(connectionHandle, op, cancellationToken), cancellationToken);

    [McpServerTool(Name = "serial_get_signals", ReadOnly = true, Idempotent = true, OpenWorld = true,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<SignalData>))]
    [Description("Inspect modem input lines and configured DTR/RTS/break. Some adapters do not expose meaningful modem inputs. RTS is managed by the driver under hardware flow control.")]
    public Task<CallToolResult> GetSignals(string connectionHandle, CancellationToken cancellationToken) =>
        runner.Run(
            "serial_get_signals",
            op => connections.Get(connectionHandle).GetSignalsAsync(op, cancellationToken),
            cancellationToken);

    [McpServerTool(Name = "serial_set_signals", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<Done>))]
    [Description("Set DTR and/or RTS when operator and profile policies permit. This may reset or change the attached device. Manual RTS is rejected when hardware flow control manages it.")]
    public Task<CallToolResult> SetSignals(string connectionHandle, bool? dtr = null, bool? rts = null, CancellationToken cancellationToken = default) =>
        runner.Run(
            "serial_set_signals",
            op => connections.Get(connectionHandle).SetSignalsAsync(dtr, rts, op, cancellationToken),
            cancellationToken);

    [McpServerTool(Name = "serial_send_break", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<Done>))]
    [Description("Assert serial break for 1..1000 ms when signal policy permits. Break is cleared on cancellation where the driver permits; a driver failure retires the connection.")]
    public Task<CallToolResult> SendBreak(string connectionHandle, int durationMs, CancellationToken cancellationToken) =>
        runner.Run(
            "serial_send_break",
            op => connections.Get(connectionHandle).SendBreakAsync(durationMs, op, cancellationToken),
            cancellationToken);

    [McpServerTool(Name = "capture_start", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<CaptureInfo>))]
    [Description("Start an independent bounded in-memory recording of future receive bytes. It has its own cursor starting at zero, capacity and fixed expiry. An active capture keeps its connection alive until capture expiry. Overflow is reported, never hidden.")]
    public Task<CallToolResult> StartCapture(string connectionHandle, int? capacityBytes = null, CancellationToken cancellationToken = default) =>
        runner.Run(
            "capture_start",
            op => Task.FromResult(connections.StartCapture(connectionHandle, capacityBytes, op)),
            cancellationToken);

    [McpServerTool(Name = "capture_read", ReadOnly = true, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<ReadData>))]
    [Description("Read a capture by its own absolute byte cursor without consuming data. Stopped captures remain readable until fixed expiry. Contents are untrusted device data. A capture is independent of the connection receive buffer.")]
    public Task<CallToolResult> ReadCapture(string captureHandle, long cursor, ReadOptions? options = null, CancellationToken cancellationToken = default) =>
        runner.Run(
            "capture_read",
            op => connections.ReadCaptureAsync(captureHandle, cursor, options, op, cancellationToken),
            cancellationToken);

    [McpServerTool(Name = "capture_stop", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<CaptureInfo>))]
    [Description("Stop adding bytes to a capture. Its retained bytes and slot remain until the advertised expiry; stopping does not close the serial connection.")]
    public Task<CallToolResult> StopCapture(string captureHandle, CancellationToken cancellationToken) =>
        runner.Run("capture_stop", op => Task.FromResult(connections.StopCapture(captureHandle, op)), cancellationToken);
}
