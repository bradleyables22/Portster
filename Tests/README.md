# Portster verification

Run the complete suite from the repository root:

```powershell
dotnet test Portster.slnx -c Release
```

Collect production line/branch coverage:

```powershell
dotnet test Portster.slnx -c Release --collect "XPlat Code Coverage" --settings Tests/coverage.runsettings --results-directory artifacts/coverage
```

The command prints the generated Cobertura XML and JSON report paths. Coverage
includes the production assembly and excludes automatic property accessors.
Tests that launch a separate STDIO server verify real process behavior but do not
add to in-process coverage. Tests use temporary storage and simulated serial
transports; Windows discovery enumerates actual ports without opening them.

## Test map

| Files | Behavior under test |
| --- | --- |
| `BufferTests.cs`, `BufferPropertyTests.cs` | Cursor replay, wrap/overrun accounting, reference-log comparisons, chunk boundaries, framing, timestamps, cancellation, concurrent append/read |
| `ValidationTests.cs`, `PolicyConfigurationTests.cs` | Encodings and byte limits, strict identifiers, UART combinations, selectors, framing, resource budgets, policy loading, storage roots |
| `ProfileTests.cs`, `PersistenceFailureTests.cs` | Atomic replacement, revision conflicts, competing writers, file/lock failures, storage limits, explicit scopes, schema/provenance rules |
| `ConnectionTests.cs`, `ConnectionRaceTests.cs`, `LifecycleTests.cs` | Transaction isolation, write uncertainty, cancelled/native opens, failed cleanup quarantine, expiry, disconnects, shutdown |
| `SignalTests.cs` | Permission checks, RTS/flow-control conflicts, rate limiting, mutation exclusion, break assertion/cleanup, driver failures |
| `CaptureTests.cs` | Independent retention, future-byte boundaries, overrun isolation, capture stop/expiry, read concurrency, capacity/slot limits |
| `AuditTests.cs`, `AuditFailureTests.cs` | Rotation, opt-in payloads, metadata, stable errors, partial results, sanitization, audit failure before/after effects |
| `InProcessMcpTests.cs` | All 19 tools through MCP serialization/dispatch, output schemas, malformed inputs, handle rejection, live profile snapshots |
| `McpIntegrationTests.cs`, `RawStdioTests.cs` | Real child processes, current/older protocol revisions, independent raw JSON-RPC, multi-process profile conflicts, clean STDIO/shutdown |
| `WindowsDiscoveryTests.cs` | Actual read-only Windows COM enumeration |

The randomized buffer test uses a fixed seed and compares 5,000 operation
sequences against an independent unbounded reference log. The concurrency tests
use controllable driver hooks and synchronization gates to place cancellation,
close, and competing calls at specific points. The reported case count includes
parameterized scenarios; it is not the number of assertions or randomized steps.

Run focused groups, for example:

```powershell
dotnet test Tests/Portster.Tests.csproj -c Release --filter "FullyQualifiedName~ConnectionRaceTests"
dotnet test Tests/Portster.Tests.csproj -c Release --filter "FullyQualifiedName~InProcessMcpTests"
dotnet test Tests/Portster.Tests.csproj -c Release --filter "FullyQualifiedName~PersistenceFailureTests"
```

After publishing x64, smoke-test the packaged executable, default permissions,
clean shutdown, and strict input binding without opening hardware:

```powershell
./Tests/Smoke-Package.ps1
```

The environment-configuration tests restore changed process variables and run
without parallel test collections. Temporary-directory cleanup verifies that its
target remains under the dedicated Portster test directory.

## Remaining acceptance work

Physical loopback, real unplug/replug and control-line behavior, virtual serial
pairs, Windows ARM64 execution, and the intended interactive MCP host still need
validation. Windows-specific discovery and file-sharing tests currently assume
the supported Windows host. More simulated tests cannot establish native driver
behavior on hardware that has not been exercised.
