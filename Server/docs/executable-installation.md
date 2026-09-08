# Install the Windows executable

Portster's supported end-user distribution is a self-contained, single-file
Windows executable delivered through GitHub Releases. The release ZIP also
contains the README, documentation, example policy, profile schema, and license.
Keep those files together after extraction.

## Download and verify

1. Open the repository's [Releases page](https://github.com/bradleyables22/Portster/releases).
2. Choose the asset for the computer running the MCP client:
   - `portster-win-x64.zip` for most Intel or AMD Windows PCs.
   - `portster-win-arm64.zip` for Windows-on-ARM PCs.
3. Download `SHA256SUMS` from the same release.
4. In PowerShell, calculate the archive's checksum:

   ```powershell
   Get-FileHash .\portster-win-x64.zip -Algorithm SHA256
   ```

5. Confirm that the displayed hash matches the asset's line in `SHA256SUMS`.
   Do not run an archive whose checksum differs.
6. Extract the ZIP into a versioned, user-controlled directory such as
   `C:\Users\YOUR_NAME\Apps\Portster\v0.1.0-beta`.

Published builds include the .NET runtime. The target computer does not need the
.NET SDK or a separate .NET runtime. Windows may still show its normal warning
for a newly downloaded executable; verify the repository, release, checksum, and
build attestation rather than disabling Windows security checks globally.

## Configure Codex or ChatGPT desktop

ChatGPT desktop and Codex can launch local STDIO MCP servers. In the desktop UI,
open **Settings > MCP servers > Add server**, select **STDIO**, and set the
command to the absolute path of `Portster.exe`. Save the entry and restart the
client if requested. These locations and the configuration fields below follow
the current [official OpenAI MCP setup documentation](https://learn.chatgpt.com/docs/extend/mcp?surface=cli).

Codex users can instead add this entry to the user configuration at
`~/.codex/config.toml`, or to `.codex/config.toml` in a trusted project:

```toml
[mcp_servers.portster]
command = 'C:\Users\YOUR_NAME\Apps\Portster\v0.1.0-beta\Portster.exe'
enabled = true
required = false
startup_timeout_sec = 10
tool_timeout_sec = 120
default_tools_approval_mode = "writes"

[mcp_servers.portster.env]
PORTSTER_CONFIG = 'C:\Users\YOUR_NAME\.portster\policy.json'
PORTSTER_PROJECT = 'C:\Users\YOUR_NAME\source\my-firmware'
```

Replace `YOUR_NAME` and the version with real values. TOML literal strings use
single quotes here so Windows backslashes do not need escaping.

`PORTSTER_CONFIG` is optional. To use it, copy `portster.example.json` from the
release to a stable location outside the versioned installation directory,
rename it to `policy.json`, and edit it. The example mirrors Portster's
permissive defaults but exposes every setting that can be tightened. Omitting
the variable also uses the permissive defaults.

`PORTSTER_PROJECT` is also optional. It selects the project-specific storage
location used by saved connection profiles and knowledge. Point it at the
firmware project whose device information should be reused. See
[Profiles and storage](profiles.md) and [Policy and auditing](policy.md) before
enabling unattended work. Project profiles are written below
`PORTSTER_PROJECT` in `.portster/profiles`; add `.portster/` to that project's
`.gitignore` when the data should remain local to the machine.

The approval mode `writes` allows read-only discovery without repeated prompts
while requiring approval for tools that can change device or stored state. Use a
more restrictive client approval policy when working with unfamiliar hardware.

## Confirm the connection

After restarting the client:

1. Run `codex mcp list` or use `/mcp` in Codex to confirm that `portster` started.
2. Ask the client to call `serial_list_ports`.
3. Open only the intended COM port with the UART settings from the device
   documentation.
4. Start with a read or a harmless identification command before enabling an
   automated write loop.

Portster reserves stdout for MCP JSON-RPC messages and writes diagnostics to
stderr. Running `Portster.exe` directly in a terminal appears to wait silently;
that is normal because it is waiting for an MCP client on stdin.

## Upgrade or remove

Stop the MCP client before replacing Portster because a running executable may
be locked. Extract the new release into a new versioned directory, verify it,
update the `command` path, and restart the client. Keep policy and profile data
outside the versioned program directory so an upgrade cannot overwrite them.

To remove Portster, delete its MCP server entry and the extracted version
directories. Delete the external policy or profile storage only if that saved
device information is no longer needed.

## Generic STDIO clients

Clients that do not use Codex's TOML format need the same underlying values: an
STDIO transport, the absolute `Portster.exe` command, no command arguments, and
optional `PORTSTER_CONFIG` and `PORTSTER_PROJECT` environment variables. Consult
that client's current MCP documentation for its outer configuration format.
