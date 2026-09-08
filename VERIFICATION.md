# Portster implementation verification

## Default permissions — 2026-09-06

- Without `PORTSTER_CONFIG`, all Windows COM ports, writes, and signal control
  are permitted. New profiles permit writes and signals as well. Explicit port
  lists and permission restrictions remain enforced.
- All 140 Release test cases passed, including missing/blank configuration,
  partial configuration, direct and profile-based opens, writes, DTR/RTS and
  break, restrictive policy/profile behavior, and MCP profile defaults.
- The exported profile JSON schema is unchanged. Default UART settings, resource
  limits, auditing, and retry behavior are unchanged.
- Windows x64 and ARM64 self-contained publishes succeeded. The x64 package
  smoke check passed all 19-tool discovery, strict binding, default-permission
  profile validation, and clean shutdown checks. ARM64 was cross-compiled only.

## Physical device verification — 2026-09-06

The published Windows x64 server passed a physical USB CDC serial check through
STDIO MCP: USB identity discovery, profile-based opening with DTR, ten command/reply
exchanges, cursor replay, capture retention after closing, and clean shutdown.
Capture retained all 189 response bytes. These checks are separate from the
automated coverage figures below. Device programs, tools, backups, and session
records are maintained outside this repository.

## Readability pass — 2026-09-06

- Production source now has 50 types in 50 matching files, plus `Program.cs`.
  Classes, records, interfaces, and enums were checked, including nested types.
- All 47 public type declarations match the pre-refactor API, including public
  member signatures, defaults, and attributes.
- The 22 existing test/support files were checked against their original hashes
  and were not edited. All 134 test cases passed.
- A live x64 STDIO session passed 21 tool calls, profile persistence across a
  restart, audit record checks, and two clean shutdowns.
- The complete 19-tool catalog, input/output schemas, server instructions, and
  generated profile schema match the pre-refactor versions.
- Windows x64 and ARM64 self-contained builds succeeded. The x64 package smoke
  check passed, and the new reference documentation is included in both builds.
- Production formatting and local README links were checked.

The coverage figures below were collected before the source layout and
formatting changes. They have not been recalculated for this pass.

## Implementation baseline — 2026-09-05

Verified on Windows x64 with .NET SDK 10.0.400 on 2026-09-05.

- Release build: zero warnings and zero errors.
- Release suite with coverage: **134 cases across 112 test methods; 0 failed, 0 skipped** (24 seconds on this machine).
- Production in-process line coverage: **87.8%** (712/811), up from 69.7%.
- Production in-process branch coverage: **87.0%** (737/847), up from 59.8%.
- Real STDIO MCP clients: revisions `2026-07-28` and `2025-11-25`.
- Independent raw JSON-RPC client: revision `2025-03-26`, tool discovery,
  text fallback, Windows port discovery, and clean process shutdown.
- Actual success/error tool payloads validated against advertised output schemas.
- A complete simulated-device workflow invokes all 19 tools through the MCP SDK
  and validates every successful response against the advertised schema.
- Two independent server processes race on the same profile revision; exactly one
  update succeeds and the other receives `REVISION_CONFLICT`.
- Generated profile schema validated with valid and unknown-field inputs.
- Self-contained publish succeeded for `win-x64` and `win-arm64`.
The tests cover cursor replay/overruns, split delimiters, partial timeouts,
cancellation, idle framing, malformed payloads, exclusive opens, stale handles,
mutation contention, unknown write outcomes, port reservation during native
cleanup, unplug/line-error simulation, capture retention/expiry, connection
expiry, break cancellation, policy restrictions, revision conflicts, scope
separation, unknown profile versions, provenance restrictions, audit rotation,
and cleanup when audit storage is unavailable. The expanded suite also includes
5,000 randomized ring-buffer/reference-log comparisons, concurrent append/read
stress, all chunk boundaries of an overlapping delimiter, exact frame limits,
UTF-8/Base64 boundaries, strict identifier validation, failed atomic file
replacement, storage contention, competing creates/updates, malformed MCP inputs,
signal failures/rate limits, and capture resource limits.

## Bugs exposed and corrected by the expanded suite

- A valid one-byte Base64 write was rejected at a one-byte write limit.
- Identifier validators incorrectly accepted a trailing newline.
- An oversized on-disk profile catalog could be returned partially without an error.
- Failed native close could release a port reservation before cleanup succeeded.
- Closing during write preparation could falsely report an attempted write.
- MCP binding silently discarded unknown nested profile fields instead of rejecting them.
- Nested audit settings used inconsistent property casing and enum serialization.

The native-close failure now returns `CLOSE_FAILED` and keeps the port quarantined.

## Reproducing coverage

```powershell
dotnet test Portster.slnx -c Release --collect "XPlat Code Coverage" --settings Tests/coverage.runsettings --results-directory artifacts/coverage
```

Coverlet includes the production assembly and skips generated automatic property
accessors. It measures execution inside the test process; separately spawned
STDIO server processes are tested but their execution does not contribute to these
coverage percentages. Native serial open/read/write/control-line code remains
uncovered, and USB metadata branches depend on attached hardware. The percentages
are evidence of exercised code paths, not proof of hardware correctness.

See `Tests/README.md` for the test map and targeted commands.

## Local build outputs

Build outputs:

- `artifacts/win-x64/Portster.exe`
- `artifacts/win-arm64/Portster.exe`

Each publish directory also includes the README, reference documentation under
`docs/`, operator policy example, profile schema, and debugging symbols. Artifacts
are ignored by source control.

The original baseline did not open a physical port. The hardware session above now
verifies real USB serial discovery, open, write, read, capture, and close. Native
adapter loopback, real unplug/replug during operations, external modem line
control, virtual serial pairs, Windows ARM64 execution, and the intended
interactive MCP host remain acceptance checks. ARM64 was cross-compiled, not
run on this x64 machine. No public package was published.
