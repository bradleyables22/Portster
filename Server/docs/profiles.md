# Profiles and storage

| Setting | Default / behavior |
| --- | --- |
| `PORTSTER_CONFIG` | Optional operator JSON policy path, loaded once at startup |
| `PORTSTER_DATA` | `%LOCALAPPDATA%\Portster`; global `profiles` and `audit` directories |
| `PORTSTER_PROJECT` | Explicit project root; project profiles in `.portster/profiles` |

Scope is explicit, defaulting to `Global`; there is no implicit project/global
precedence, directory scanning, or merging. Policy is never loaded from profiles
or automatically from a project directory.

Profiles contain adapter selectors, UART settings, framing defaults, restrictive
safety settings, descriptive target knowledge/provenance, and secret references.
New profiles permit writes and signals by default. Explicit `false` values
restrict those operations, and operator policy remains the upper permission
limit. Opening with DTR, RTS, or flow control still uses the profile's explicit
serial settings; permitting signals does not assert them automatically.

Connections snapshot profile settings/revision at open. Later edits do not alter
live devices. A cross-process storage lock protects edits; saves flush a temporary
file and atomically rename it in the same directory. Update/delete require the
expected revision to prevent lost edits. Atomic replacement is not a backup.

Profile IDs use lowercase letters, digits, hyphens and underscores. The first
schema version is `1`; unknown versions/fields are rejected without rewriting.
There is no older Portster format to migrate. Future migrations must be explicit.
`profile.schema.json` describes serialized shape; runtime validation additionally
enforces configured limits and cross-field constraints. Regenerate it with:

```powershell
dotnet run --project Server -- --export-profile-schema Server/profile.schema.json
```

USB matching uses VID/PID, serial number, and interface when specified. Ambiguous
matches fail. `lastSeenPort` selects a port only when no USB identity is specified;
it never overrides a failed stable match. Windows discovery walks USB parents and
checks UniqueID before treating an instance suffix as a USB serial. Missing
metadata is reported, not invented. Adapter identity does not authenticate the
board connected to UART pins. Discovery/open cannot eliminate every hotplug race.

Knowledge is descriptive and never executed. MCP can explicitly save draft facts
with provenance, but cannot mark confirmations or validation timestamps.
`source: User` is attribution, not authority. Confirmations require offline
operator edits; confirmed profiles are protected from tool updates. Inferences
cannot be confirmed without establishing another basis. `profile_validate`
checks configuration, not the truth of protocol knowledge.

Only `env:VARIABLE_NAME` values are accepted in `secretReferences`; v1 does not
resolve references or inject them into commands. Do not put credentials in
free-text knowledge. No initialization commands, scripts, or protocol helpers run
when opening profiles.
