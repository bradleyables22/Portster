# Portster

Portster lets an MCP client discover and communicate with serial devices exposed
as Windows COM ports. It supports USB-to-serial adapters and boards with virtual
COM ports. Mice, keyboards, and other USB devices use different interfaces.

The server runs locally over STDIO. It receives bytes between tool calls and
supports text or binary exchanges, captures, saved device profiles, and operation
auditing. This release is `0.1.0-beta` and has not been published to a registry.

## Setup

Build with the .NET 10 SDK from the repository root:

```powershell
dotnet build Portster.slnx
```

Portster allows all Windows COM ports, writes, and signal control by default.
No policy file is required. Timeouts, buffer sizes, and rate limits use built-in
defaults.

Configure your MCP client to launch the built server. For example:

```json
{
  "command": "dotnet",
  "args": ["C:\\source\\Portster\\Server\\bin\\Debug\\net10.0\\Portster.dll"]
}
```

Replace those paths with your actual locations. Launch the built DLL or published
executable from the client: build output must not enter the JSON-RPC stream.
Server diagnostics go to stderr.

To restrict access or change limits, copy `Server/portster.example.json` outside
the repository and point the client's `PORTSTER_CONFIG` environment variable at
it. For example, `"allowedPorts": ["COM7"]` permits only COM7; an empty list
prevents opening any port. Set `allowWrites` or `allowSignals` to `false` to
disable those operations. Existing explicit settings still apply. Restart the
server after changing the file.

## First exchange

1. Call `serial_list_ports` to find the adapter.
2. Call `serial_open` with the port and the settings required by your device:

   ```json
   {"portName":"COM7","settings":{"baudRate":115200,"dataBits":8,"parity":"None","stopBits":"One","flowControl":"None","dtr":false,"rts":false}}
   ```

3. Use the returned `connectionHandle` and `initialCursor` to read with
   `serial_read`. To send a command and wait for a reply, use `serial_transact`.
   For a device that accepts `status` and ends replies with CRLF:

   ```json
   {"connectionHandle":"HANDLE","payload":{"encoding":"Utf8","data":"status\r\n"},"options":{"mode":"Delimiter","delimiterHex":"0D0A","maxBytes":4096,"waitMs":1000}}
   ```

4. Check `isError`, any partial data, and the write disposition. Continue reads
   from `nextCursor`.
5. Call `serial_close` when finished.

Use commands and UART settings appropriate for the attached device. Portster
sends the exact bytes supplied and does not add a newline. Opening a port or
changing control lines can reset some boards. A timeout after a write can leave
the device's state unknown; writes are never retried automatically.

## Reference

- [Serial operations](docs/serial.md): all 19 tools, cursors, framing, and captures.
- [Profiles and storage](docs/profiles.md): device matching, revisions, knowledge, and storage scopes.
- [Policy and auditing](docs/policy.md): permissions, resource limits, and audit records.
- [Troubleshooting](docs/troubleshooting.md): error codes and hardware checks.
- [Development](docs/development.md): builds, packaging, source layout, and verification.

Windows is the only implemented platform. Physical loopback, unplug/replug,
control-line behavior, and Windows ARM64 execution still need validation.
