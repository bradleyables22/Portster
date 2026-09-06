# Portster Working Plan

## Goal

Build a local-first MCP server that gives an AI reliable, auditable access to COM ports and USB-to-UART devices through bounded, strongly typed tools.

## Current Baseline

- .NET 10 C# MCP server using the STDIO transport.
- `ModelContextProtocol` 2.2.0, aligned with MCP `2026-07-28` and compatible with older clients through SDK negotiation.
- Windows COM-port support is the first target; Linux and macOS follow after the core contract is stable.

## Design Commitments

- MCP transport state and serial-device state remain separate.
- `serial_open` returns an opaque, temporary `connectionHandle`; handles are never persisted or reused after shutdown.
- Persistent JSON profiles store device identity, serial settings, framing rules, protocol knowledge, safety policy, and knowledge provenance.
- COM names such as `COM7` are hints. Stable matching prefers USB serial number, VID/PID, and interface identity.
- A background receive loop captures incoming bytes into a bounded, timestamped ring buffer.
- Reads use cursors rather than destructively draining the buffer, making retries predictable.
- Tool results use output schemas and structured content, with concise text fallbacks for older clients.
- The tool catalog remains deterministic; connected devices are data returned by tools, not dynamically created tools.
- Serial input is always untrusted device data and is never treated as instructions.
- Server-side validation and safety limits remain authoritative; MCP annotations and client approvals are additional protections.
- Operator policy is loaded only from an explicit startup configuration and cannot be edited through MCP. Profile permissions may only restrict it.
- One transaction or other mutation may run per connection. Competing mutations fail busy; they are not queued.
- Transactions establish a receive cursor immediately before writing, but framing alone does not correlate a reply or exclude delayed driver/unsolicited bytes.
- Writes are never automatically retried. Results distinguish no attempt, driver submission, and attempted writes with unknown outcomes.
- Interrupted native operations retire the handle and keep the port reserved until both I/O and cleanup finish.
- Adapter identity and UART-target identity are distinct. The target remains unverified in generic serial tools.
- Captures have independent bounded in-memory retention and fixed expiry; all detected loss is reported.

## Proposed Tool Surface

### Device profiles

- `profile_list`
- `profile_get`
- `profile_create`
- `profile_update`
- `profile_validate`
- `profile_delete`

### Connections and data

- `serial_list_ports`
- `serial_open`
- `serial_open_profile`
- `serial_read`
- `serial_write`
- `serial_transact`
- `serial_close`

### Signals and capture

- `serial_get_signals`
- `serial_set_signals`
- `serial_send_break`
- `capture_start`
- `capture_read`
- `capture_stop`

## Persistent Profile Contents

- Schema version, profile ID, display name, and timestamps.
- Device selector: VID, PID, USB serial number, interface number, manufacturer, product, and last-seen port.
- UART configuration: baud rate, data bits, parity, stop bits, flow control, DTR, RTS, and timeouts.
- Framing: binary/text mode, encoding, line ending, delimiter, fixed length, idle gap, and maximum response size.
- Protocol knowledge: protocol/version, addressing, byte order, checksums, command references, register maps, and timing rules.
- Safety policy: restrictive write limits, polling limits, and signal permissions. Version 1 has no automatic retries or per-operation approval-token mechanism.
- Provenance for learned facts: `user`, `documentation`, `observed`, or `inferred`, plus confirmation status and validation time.
- Tool-originated knowledge is explicitly saved as draft information. MCP cannot create confirmations or validation timestamps; operator-confirmed profiles require offline editing.
- Secret references only. Passwords, tokens, private keys, and credentials are never stored directly in a profile.

## Implementation Milestones

### 1. Server foundation

- Replace sample package metadata and the random-number tool.
- Add Portster server identity and concise MCP server instructions.
- Add the serial-port runtime dependency and register connection/profile services through dependency injection.
- Preserve STDIO exclusively for MCP messages and stderr for diagnostics.

### 2. Port discovery and profiles

- Enumerate Windows COM ports and obtain stable USB identity metadata.
- Define versioned profile DTOs and JSON Schemas.
- Implement atomic profile writes and explicit global/project storage scopes.
- Reject ambiguous device matches rather than guessing.

### 3. Connection lifecycle

- Implement exclusive opens and opaque connection handles.
- Validate all UART settings before opening a device.
- Add handle expiration, cancellation, disconnect detection, and shutdown cleanup.
- Report recoverable tool errors with stable machine-readable error codes.

### 4. Receive loop and transactions

- Add a cancellable background receive loop per connection.
- Store timestamped data in a bounded ring buffer with sequence cursors and overrun reporting.
- Support bounded reads and write/read transactions using delimiter, length, or idle-gap completion.
- Represent payloads as Base64 and hexadecimal, with optional safe text previews.

### 5. Hardware control and safety

- Add modem-signal inspection, DTR/RTS control, and break signaling.
- Apply accurate read-only, destructive, idempotent, and open-world MCP annotations.
- Enforce maximum write sizes, wait durations, capture sizes, and polling rates in the server.
- Add configurable device allowlists and payload-audit settings.

### 6. Verification

- Unit-test schemas, validation, ring-buffer cursors, framing, and profile migration.
- Integration-test with virtual serial pairs and a physical loopback adapter.
- Test unplug/replug, port contention, cancellation, timeouts, malformed payloads, and buffer overruns.
- Exercise the server from Codex and at least one independent MCP inspector/client.
- Verify backward negotiation while keeping optional MRTR, Tasks, subscriptions, and MCP Apps out of the required path.

### 7. Packaging

- Replace all `.mcp/server.json` placeholders and update it to the current Registry schema before publication.
- Rename the NuGet package and fill in repository, licensing, version, and platform metadata.
- Publish self-contained builds for supported runtime identifiers.
- Document local installation, permissions, profiles, troubleshooting, and physical-layer safety.

### 8. GitHub release distribution

- Keep compiled executables out of Git history and publish them as GitHub Release assets.
- Add a GitHub Actions workflow triggered by semantic version tags such as `v0.1.0`.
- Build and test once, then publish self-contained single-file binaries for each supported runtime identifier.
- Package Windows builds as ZIP files and Unix builds as `tar.gz` archives so executable permissions are preserved.
- Use stable asset names such as `portster-win-x64.zip` so `/releases/latest/download/<asset-name>` links remain valid across releases.
- Generate a `SHA256SUMS` file and GitHub artifact attestations for downloadable binaries.
- Create the release with generated notes only after every platform build succeeds; consider enabling immutable releases.
- Treat tagged releases as durable public downloads. Use ordinary workflow artifacts only for temporary pull-request and branch builds.
- Keep NuGet MCP packaging as a parallel installation path rather than replacing direct executable downloads.
- Consider Windows code signing and macOS signing/notarization before describing public binaries as production-ready.

## Later Extensions

- Streamable HTTP for secured remote hardware laboratories.
- Optional MCP Tasks support for long-running test sweeps when the client advertises the extension.
- Optional terminal UI through MCP Apps when supported by the target host.
- Protocol helpers for Modbus RTU, AT commands, SCPI, G-code, and bootloader workflows.
- Vendor-specific USB adapter features such as FTDI GPIO or EEPROM access, isolated from generic UART tools.
- Linux and macOS device discovery and identity providers.

## Definition of Done for Version 1

- An AI can identify a permitted USB-to-UART adapter, open it from explicit settings or a saved profile, exchange text or binary data, inspect control signals, retain asynchronous output between tool calls within configured capacity, report detected overruns, and close the device cleanly.
- Every operation is bounded, validated, cancellable, structured, and auditable.
- Device unplugging, ambiguous matches, busy ports, expired handles, timeouts, and buffer overruns produce actionable errors.
- No connection handle, credential, or unconfirmed AI inference is silently persisted.

## Resolved Decisions

- Windows is the first implementation and test target. Discovery/transport interfaces isolate the shared engine for later Linux/macOS support.
- Global storage defaults to `%LOCALAPPDATA%/Portster`, overridable with `PORTSTER_DATA`. Project scope requires explicit `PORTSTER_PROJECT`; scopes never implicitly override one another.
- Operator policy can be supplied through `PORTSTER_CONFIG`; without it, all Windows COM ports, writes, and signal control are permitted. New profiles permit writes and signals by default. Explicit restrictions remain enforced; resource limits still apply.
- Default inactivity timeout is 15 minutes; receive capacity is 256 KiB per connection. Active captures retain the connection until expiry or stop.
- Captures default to 1 MiB, with 8 retained slots and one-hour lifetime. Stopped captures remain readable until expiry.
- Initialization commands, secret resolution, executable protocol knowledge, and automatic write retries are excluded from version 1.
- Audit records include durable intent before hardware effects and completion afterward, with operation IDs, identity, profile revisions, settings, byte counts and outcomes. Payload logging defaults off.
- Unknown profile schemas are rejected without mutation. Version 1 is the first format; migrations will accompany the first actual schema change.

## Implementation Status (2026-09-05)

- Implemented all 19 proposed MCP tools, Windows USB/COM discovery, bounded lifecycle, framing, signal controls, independent captures, profiles, operator policy, and rotating audits.
- Added focused unit/simulated-driver tests plus real STDIO integration for MCP `2026-07-28`, `2025-11-25`, and an independent raw `2025-03-26` client.
- Added output-schema conformance checks, a generated profile schema, example configuration, and installation/operation documentation.
- Expanded release verification: 134 tests passed, with 87.8% in-process line coverage and 87.0% branch coverage; all 19 tools exercised through the SDK. Regression tests exposed and fixed boundary, persistence, cleanup, input-binding, and audit-format issues. See `VERIFICATION.md`.
- Self-contained Windows x64/ARM64 builds are available; the published x64 executable passed a 19-tool STDIO smoke test.
- No public publication was performed by this implementation work.
- Remaining hardware acceptance: virtual serial pairs; physical loopback; real unplug/replug and control-line behavior; Windows ARM64 execution; intended interactive host verification.
- Remaining publication decisions: repository ownership, license, and Registry namespace. Template placeholder metadata must not be published.
