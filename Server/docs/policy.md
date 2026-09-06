# Policy and auditing

Portster permits all Windows COM ports, writes, and signal control by default.
No configuration file is required. New profiles also permit writes and signals.

Set `PORTSTER_CONFIG` to a JSON file to restrict access or change resource limits.
It is loaded once at startup; omitted settings retain their built-in defaults.
`"allowedPorts": ["*"]` permits all COM ports, a list of port names restricts
access to those ports, and `[]` prevents all port opens. Set `allowWrites` or
`allowSignals` to `false` to disable those operations. An invalid or missing
explicitly selected file stops startup instead of falling back to defaults.

Profiles can impose further restrictions but cannot relax operator policy. Saved
explicit restrictions remain in effect. No MCP tool edits server policy. Client
approval and MCP annotations supplement these checks. Per-operation approval
tokens and automatic retries are not part of this release.

Default limits: 4 connections; 256 KiB receive capacity each; 16 KiB per read;
4 KiB per write; 10 s maximum read wait; 2 s native write timeout; 100 ms minimum
write interval; 25 ms minimum read interval; 15 min connection inactivity; 8
captures of at most 1 MiB each with 1 h retention; 256 profiles per scope, 64 KiB
each. Native calls exceeding deadlines are quarantined. Managed cancellation
cannot guarantee termination of a malfunctioning kernel driver.

Total configured byte capacity is capped at 16 MiB. Each byte has UTC and
monotonic timestamp metadata (17 bytes of storage total, plus objects/results),
so default buffer allocation can reach about 153 MiB when all connections and
captures are allocated. Larger capacity budgets are rejected at startup.

Audits are JSON Lines, coordinated across local processes, rotating across five
files of up to 5 MiB by default. Records include operation IDs, tool names,
timestamps, resolved adapter identity, profile revision, byte counts, relevant
UART/read/signal settings, disposition, and result codes. Hardware intent is
flushed before I/O. Missing completion after intent means an uncertain outcome.
Audit failure prevents new work; close/stop cleanup still runs and reports the audit failure. Failure after an action explicitly
reports that the operation may have occurred.

Payload logging defaults off. `auditPayloads: true` includes transmitted Base64;
received payloads and profile knowledge are not recorded. Logs still contain
identity and operation metadata. This is a local diagnostic audit, not a
tamper-evident or centralized security log.
