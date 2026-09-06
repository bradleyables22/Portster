# Troubleshooting

| Error | Next step |
| --- | --- |
| `PORT_NOT_ALLOWED` | Add the intended port to policy and restart |
| `PORT_BUSY_OR_DENIED` | Close other serial applications; check Windows access |
| `AMBIGUOUS_DEVICE` | Add serial/interface identity or explicitly select a port |
| `DISCOVERY_TIMEOUT` | Check Windows WMI; discovery did not open hardware |
| `READ_TIMEOUT` | Inspect partial data/write disposition before another command |
| `BUFFER_OVERRUN` | Inspect lost bytes; resume retained data or increase capacity |
| `WRITE_OUTCOME_UNKNOWN` / `WRITE_CANCELLED` | Establish device state; do not blindly repeat |
| `CLOSE_PENDING` | Wait for native cleanup; a process restart may be needed |
| `CLOSE_FAILED` | Cleanup failed; the port remains reserved until server restart |
| `RATE_LIMITED` / `READ_BUSY` | Reduce polling or wait for the pending read |
| `REVISION_CONFLICT` | Read the current profile before editing |

Opening/closing and DTR/RTS/break changes can reset or affect devices. Hardware
flow control requires signal permission; manual RTS is rejected when controlled
by the driver. Verify voltage levels, TTL versus RS-232/RS-485 interfaces, ground,
and intended pins before physical tests. USB identity cannot establish those
electrical properties.
