# Portster

Portster is a local MCP server that lets an AI client discover and communicate
with serial devices exposed as Windows COM ports. It supports USB-to-UART
adapters and development boards with virtual COM ports; arbitrary USB devices
such as mice, keyboards, and storage devices use different interfaces.

Portster runs over STDIO and retains bytes received between MCP tool calls. It
supports text and binary exchanges, cursor-based reads, framed transactions,
bounded captures, saved device profiles, control signals, and operation
auditing.

> [!WARNING]
> Serial commands and control-line changes can reset, reconfigure, erase, or
> operate connected hardware. Confirm the voltage level, wiring, UART settings,
> and device command set before enabling writes. Portster does not provide an
> electrical or physical safety boundary.

## Project status

The current version is `0.1.0-beta`, is Windows-only, and has not been published
to NuGet or the MCP Registry. Automated tests cover the protocol and simulated
serial behavior, but physical loopback, unplug/replug, control-line behavior,
and Windows ARM64 execution still require hardware validation.

## Install the Windows executable

GitHub Releases is the intended distribution channel. Download the ZIP for your
Windows architecture, verify its checksum, extract it, and configure your MCP
client to launch `Portster.exe`. The published executable includes .NET, so users
do not need the .NET SDK or runtime.

- Most Windows PCs use `portster-win-x64.zip`.
- Windows-on-ARM PCs use `portster-win-arm64.zip`.

See [Executable installation](Server/docs/executable-installation.md) for the
complete download, verification, Codex/ChatGPT desktop configuration, first-run,
and upgrade procedure. Until the first GitHub release is published, use a local
publish build as described below.

## Build from source

Development requires the .NET 10 SDK. From the repository root:

```powershell
dotnet build Portster.slnx
dotnet test Portster.slnx
```

The development build uses the installed .NET runtime. Release publishing
produces self-contained, single-file Windows executables.

## Configure an MCP client

For an installed release, configure the client to launch the absolute path to
`Portster.exe`; the exact Codex and ChatGPT desktop setup is in
[Executable installation](Server/docs/executable-installation.md).

When developing from source, a generic MCP server entry can launch the built
DLL instead:

```json
{
  "command": "dotnet",
  "args": ["C:\\source\\Portster\\Server\\bin\\Debug\\net10.0\\Portster.dll"]
}
```

Replace the path with the absolute path on your machine. Launch the built DLL or
published executable rather than a build command: compiler output must not enter
the JSON-RPC stream. Portster writes diagnostics to stderr and reserves stdout
for MCP messages.

Portster permits all Windows COM ports, writes, and signal control by default.
To restrict access or change limits, copy
[`Server/portster.example.json`](Server/portster.example.json) outside the
repository and set the MCP server's `PORTSTER_CONFIG` environment variable to
that copy. For example, `"allowedPorts": ["COM7"]` allows only COM7, while an
empty list prevents every port from opening. Restart Portster after changing
the policy.

## First exchange

1. Call `serial_list_ports` to discover the adapter.
2. Open it with the settings required by the device:

   ```json
   {
     "portName": "COM7",
     "settings": {
       "baudRate": 115200,
       "dataBits": 8,
       "parity": "None",
       "stopBits": "One",
       "flowControl": "None",
       "dtr": false,
       "rts": false
     }
   }
   ```

3. Pass the returned `connectionHandle` and `initialCursor` to `serial_read`, or
   use `serial_transact` to write and await a framed response:

   ```json
   {
     "connectionHandle": "HANDLE",
     "payload": {
       "encoding": "Utf8",
       "data": "status\r\n"
     },
     "options": {
       "mode": "Delimiter",
       "delimiterHex": "0D0A",
       "maxBytes": 4096,
       "waitMs": 1000
     }
   }
   ```

4. Inspect `isError`, partial data, and the write disposition. Continue reading
   from `nextCursor`.
5. Call `serial_close` when finished.

Portster sends exactly the supplied bytes and does not add a newline. Opening a
port or changing DTR/RTS can reset some boards. A timeout after a write can leave
device state unknown, so Portster never retries writes automatically.

## Documentation

- [Serial operations](Server/docs/serial.md) describes all 19 tools, cursors,
  framing, and captures.
- [Profiles and storage](Server/docs/profiles.md) covers device matching,
  revisions, knowledge, and storage scopes.
- [Policy and auditing](Server/docs/policy.md) covers permissions, resource
  limits, and audit records.
- [Troubleshooting](Server/docs/troubleshooting.md) lists error codes and
  hardware checks.
- [Development](Server/docs/development.md) covers builds, packaging, source
  layout, and verification.
- [Executable installation](Server/docs/executable-installation.md) covers the
  supported Windows EXE installation and MCP client integration.
- [GitHub releases](Server/docs/releases.md) defines the release assets and a
  ready-to-use GitHub Actions pipeline.
- [Verification](VERIFICATION.md) records the current automated and physical
  validation status.

## License

Portster is available under the [MIT License](LICENSE).
