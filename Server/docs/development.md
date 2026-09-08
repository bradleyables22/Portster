# Development

Development requires the .NET 10 SDK. From the repository root:

```powershell
dotnet build Portster.slnx
dotnet test Portster.slnx
```

The default policy permits all Windows COM ports, writes, and signal control.
Run without `PORTSTER_CONFIG` to use it:

```powershell
dotnet run --project Server --no-build
```

The process waits for MCP messages. STDIO is reserved for JSON-RPC; diagnostics go
to stderr. For client configuration, launch the built DLL or published executable
rather than a build command, so compiler output cannot enter the protocol stream.

To customize access or limits, copy `Server/portster.example.json` outside the
repository and set `PORTSTER_CONFIG` to its path. `"allowedPorts": []` prevents
port opens; `allowWrites: false` and `allowSignals: false` disable those
operations. Omitted fields use the permissive defaults. No MCP tool edits this
server policy; changes require a restart.

Example MCP server entry with optional policy and project storage overrides
(the outer configuration format depends on the client):

```json
{
  "command": "dotnet",
  "args": ["C:\\source\\Portster\\Server\\bin\\Debug\\net10.0\\Portster.dll"],
  "env": {
    "PORTSTER_CONFIG": "C:\\Portster\\policy.json",
    "PORTSTER_PROJECT": "C:\\source\\hardware-project"
  }
}
```

Publish builds with .NET included:

```powershell
dotnet publish Server -c Release -r win-x64 --self-contained true -o artifacts/win-x64
dotnet publish Server -c Release -r win-arm64 --self-contained true -o artifacts/win-arm64
```

Keep the entire publish directory together. Set the client's command to its
`Portster.exe` and omit `args`. Published builds need no installed .NET runtime.
The supported release asset names and GitHub Actions pipeline are documented in
[GitHub releases](releases.md). End-user setup is documented in
[Executable installation](executable-installation.md).

Edit the named instruction constants in `Server/Tools/ServerInstructions.cs` and combine
them in `ServerInstructions.All` to control the instructions sent to MCP clients.
Rebuild or republish after edits.

## Source layout

Each class, record, enum, and interface has its own file. Runtime code stays in
the `Portster` namespace so moving a file does not change its public API.

- `Program.cs` configures the host, services, and MCP tool registration.
- `Tools/` contains the MCP entry points, response handling, and server instructions.
- `Models/` contains the request, response, settings, and profile types.
- `Platform/` contains Windows discovery, native serial access, and their interfaces.
- `Serial/` manages connections, port reservations, receive buffers, framing, and captures.
- `Profiles/` handles validation and versioned profile storage.
- `Auditing/` contains operation context and audit logging.
- `Configuration/` contains operator policy and storage path configuration.
- `Infrastructure/` contains shared JSON settings, file locking, and the application exception type.

The production `.editorconfig` defines formatting for `Server/`. Keep lock scopes
and cleanup paths explicit. Comments should explain constraints such as native
driver calls that can outlive cancellation.

## Verification and release status

Tests cover simulated driver failures/cancellation, concurrency, framing, cursors,
captures, policy, persistence, audit rotation, real STDIO sessions for current and
older MCP revisions, output-schema conformance, and an independent raw JSON-RPC
client. They do not establish behavior on every USB adapter/driver.

The suite includes a complete 19-tool SDK workflow, competing
profile edits across server processes, randomized buffer/reference comparisons,
and regressions for bugs discovered by these tests. See the repository's
`Tests/README.md` and `VERIFICATION.md` for coverage, commands, and remaining gaps.

Remaining acceptance before a stable hardware release:

- Exercise virtual serial pairs with binary data, split delimiters and unsolicited output.
- Test physical TX/RX loopback and unplug/replug during open, read and write.
- Verify actual DTR/RTS/break and hardware flow control on supported adapters.
- Run on Windows ARM64 and in the intended interactive MCP host.
- Select repository ownership and Registry namespace before publication.

The template Registry manifest with placeholder identity was removed. Generate a
real manifest using the selected namespace/repository and then-current schema
before publishing. Local executables do not require a Registry manifest.
