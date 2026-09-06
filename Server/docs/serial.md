# Serial operations

## Tool catalog

| Tools | Purpose |
| --- | --- |
| `serial_list_ports` | Discover COM names and USB identity without opening ports |
| `serial_open`, `serial_open_profile`, `serial_close` | Manage exclusive temporary connections |
| `serial_read`, `serial_write`, `serial_transact` | Read retained bytes, write once, or write and await a frame |
| `serial_get_signals`, `serial_set_signals`, `serial_send_break` | Inspect/control modem signals when permitted |
| `capture_start`, `capture_read`, `capture_stop` | Record future receive bytes into independent bounded memory |
| `profile_list`, `profile_get`, `profile_create`, `profile_update`, `profile_validate`, `profile_delete` | Manage profiles in an explicit storage scope |

All 19 tools advertise input and output schemas. Results contain `operationId`,
`data`, and `error`, both as structured content and JSON text for older clients.
Tool failures set MCP `isError`; timeout/overrun results can still contain useful
partial data. Input enum names include `Hex`, `Base64`, `Utf8`, `Available`,
`Delimiter`, `Length`, `IdleGap`, `Global`, and `Project`.

### Reads, transactions, timing

Reception starts when a port opens. Each connection has a bounded ring buffer.
Cursors are absolute byte offsets starting at zero. Re-reading retained bytes is
non-destructive; advance using `nextCursor`. Always keep the correct handle/cursor
pair: cursors have no meaning in another buffer or after reconnecting.

`Available` returns available bytes; `Length` includes exactly the requested
number; `Delimiter` includes the delimiter; `IdleGap` waits for a host-observed
pause after at least one byte. Reaching `maxBytes` before framing completes is
`RESPONSE_LIMIT`. Empty or partial-response deadlines are `READ_TIMEOUT`.

Timestamps and idle gaps reflect driver reads on the host, not per-byte wire
timing. USB buffering and OS scheduling make this unsuitable for certifying exact
bus timing, including Modbus RTU silence intervals. Previews show at most 256
printable ASCII bytes and replace other bytes with dots. Base64/hex preserve the
received bytes; previews do not decode binary protocols or multibyte UTF-8.

One mutation (write, transaction, or signal operation) runs per connection.
Competing mutations fail `CONNECTION_BUSY` and are not queued. A transaction
captures the receive cursor immediately before attempting its write while holding
that lock. It excludes already-buffered bytes, but delayed driver data and
unsolicited output may still be included. Framing is not request/reply correlation.

One `serial_read` may wait per connection, and one read per capture. Independent
readers observe the same bytes; transactions do not consume/reserve received data.
Writes are never automatically retried.

`submittedToDriver` means the write call completed, not that the device executed
it. Interrupted/failed writes report `attemptedOutcomeUnknown` and retire the
connection. Its port remains reserved until native I/O and cleanup finish. A
response timeout may follow a successful command: establish device state before
issuing another command.

### Capture and retention

`capture_start` allocates independent memory for **future** receive bytes, with a
cursor starting at zero. It does not copy old connection bytes. Capture retention
is independent of connection-buffer overruns. Stopped captures remain readable
until their original expiry; they are not written to disk.

An active capture keeps its connection alive until stop/expiry. Closing/unplugging
stops captures. Stopped captures occupy their retention slots until expiry. A
restart loses all handles and captured data. Reduce `captureLifetimeSeconds` if
shorter retention is desired.

`BUFFER_OVERRUN` reports exact ring-buffer `lostBytes`, earliest retained offset,
and available data. Bounded memory cannot guarantee indefinite lossless capture.
Driver-reported framing, parity or hardware-buffer errors end reception with
`SERIAL_LINE_ERROR`. Wire-level loss cannot be counted; undetected hardware loss
cannot be ruled out.
