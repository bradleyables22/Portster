param(
    [string]$Executable = (Join-Path $PSScriptRoot '../artifacts/win-x64/Portster.exe')
)

$ErrorActionPreference = 'Stop'
$smokeStart = [System.Diagnostics.ProcessStartInfo]::new((Resolve-Path -LiteralPath $Executable).Path)
$smokeStart.UseShellExecute = $false
$smokeStart.CreateNoWindow = $true
$smokeStart.RedirectStandardInput = $true
$smokeStart.RedirectStandardOutput = $true
$smokeStart.RedirectStandardError = $true
$smokeStart.Environment['PORTSTER_CONFIG'] = ''
$smokeStart.Environment['PORTSTER_PROJECT'] = ''
$smokeStart.Environment['PORTSTER_DATA'] = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/package-smoke-data'))
$smokeProcess = [System.Diagnostics.Process]::Start($smokeStart)
$smokeErrors = $smokeProcess.StandardError.ReadToEndAsync()

function Read-PortsterResponse([int]$ExpectedId) {
    do {
        $line = $smokeProcess.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(15)).GetAwaiter().GetResult()
        if ($null -eq $line) { throw 'Server exited before sending its response.' }
        $reply = $line | ConvertFrom-Json
    } while ($reply.id -ne $ExpectedId)
    if ($reply.error) { throw ($reply.error | ConvertTo-Json -Compress) }
    return $reply.result
}

try {
    $smokeProcess.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"package-smoke","version":"1"}}}')
    $initialized = Read-PortsterResponse 1
    if ([string]::IsNullOrWhiteSpace($initialized.instructions)) { throw 'Packaged server instructions are missing.' }
    $smokeProcess.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $smokeProcess.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}')
    $listed = Read-PortsterResponse 2
    if ($listed.tools.Count -ne 19) { throw 'Unexpected tool catalog.' }
    $smokeProcess.StandardInput.WriteLine('{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"profile_list","arguments":{}}}')
    if ((Read-PortsterResponse 3).isError) { throw 'Profile listing failed.' }
    $smokeProcess.StandardInput.WriteLine('{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"profile_create","arguments":{"profile":{"id":"must-not-be-created","displayName":"Malformed profile","selector":{"lastSeenPort":"COM7"},"initializationCommands":["unsupported"]}}}}')
    if (-not (Read-PortsterResponse 4).isError) { throw 'Unknown nested fields were silently accepted.' }
    $smokeProcess.StandardInput.WriteLine('{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"profile_validate","arguments":{"profile":{"id":"default-permissions","displayName":"Default permissions","selector":{"lastSeenPort":"COM7"},"settings":{"dtr":true},"safety":{"allowWrites":true,"allowSignals":true}}}}}')
    $validated = Read-PortsterResponse 5
    if ($validated.isError -or -not $validated.structuredContent.data.valid) { throw 'Default policy rejected a profile with writes and signals enabled.' }
    $smokeProcess.StandardInput.Close()
    if (-not $smokeProcess.WaitForExit(15000)) { throw 'Server shutdown timed out.' }
    if ($smokeProcess.ExitCode -ne 0) { throw "Server exit code: $($smokeProcess.ExitCode)" }
    [pscustomobject]@{
        Server = $initialized.serverInfo.name
        Tools = $listed.tools.Count
        ServerInstructions = 'passed'
        ProfileList = 'passed'
        StrictInputBinding = 'passed'
        DefaultPermissions = 'passed'
        ExitCode = $smokeProcess.ExitCode
    }
}
finally {
    if (-not $smokeProcess.HasExited) { $smokeProcess.Kill($true) }
    $smokeProcess.Dispose()
}
