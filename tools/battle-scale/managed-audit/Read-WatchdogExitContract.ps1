$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$extractor = Join-Path $repoRoot 'tools/battle-scale/audit_watchdog_exit_contract.py'
& python $extractor --extract
if ($LASTEXITCODE -ne 0) { throw 'Read-only bundle extraction failed.' }
$gameBin = 'D:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord/bin/Win64_Shipping_Client'
$cecilPath = Join-Path $gameBin 'mono/lib/mono/gac/Mono.Cecil/0.11.0.0__0738eb9f132ed756/Mono.Cecil.dll'
Add-Type -Path $cecilPath
$derivedRoot = Join-Path $repoRoot 'build/battle-scale/watchdog-static'
$watchdogPath = Join-Path $gameBin 'Watchdog/Watchdog.exe'
$methods = @()
$pinvokes = @()
$hashes = [ordered]@{}
foreach ($name in @('Watchdog.dll', 'WatchdogLib.dll', 'System.Diagnostics.Process.dll')) {
    $path = Join-Path $derivedRoot $name
    $hashes[$name] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($path)
    try {
        foreach ($type in $assembly.MainModule.GetTypes()) {
            foreach ($method in $type.Methods) {
                $wanted = ($type.FullName -eq 'WatchdogLib.Debugger' -and $method.Name -in @('process_debuggee_message','process_command_message','kill_process','.ctor')) -or
                    ($type.FullName -eq 'System.Diagnostics.Process' -and $method.Name -eq 'Kill' -and $method.Parameters.Count -eq 0)
                if ($wanted -and $method.HasBody) {
                    $methods += [ordered]@{
                        assembly = $name
                        method = $method.FullName
                        token = $method.MetadataToken.ToString()
                        il = @($method.Body.Instructions | ForEach-Object { $_.ToString() })
                    }
                }
                if ($method.Name -eq 'TerminateProcess' -and $method.IsPInvokeImpl) {
                    $pinvokes += [ordered]@{method=$method.FullName;module=$method.PInvokeInfo.Module.Name;entryPoint=$method.PInvokeInfo.EntryPoint;attributes=$method.PInvokeInfo.Attributes.ToString()}
                }
            }
        }
    } finally { $assembly.Dispose() }
}
if ($methods.Count -lt 5 -or $pinvokes.Count -ne 1) { throw 'Expected shutdown IL contracts missing.' }
$output = Join-Path $repoRoot 'evidence/engine-extension/native-watchdog-managed-exit-evidence.json'
[ordered]@{
    schemaVersion=1
    method='Execution-free Mono.Cecil metadata/IL reading of exact selected .NET6 single-file bundle byte slices.'
    watchdogPath=$watchdogPath
    watchdogSha256=(Get-FileHash -LiteralPath $watchdogPath -Algorithm SHA256).Hash.ToLowerInvariant()
    extractedSha256=$hashes
    cecilSha256=(Get-FileHash -LiteralPath $cecilPath -Algorithm SHA256).Hash.ToLowerInvariant()
    methods=$methods
    pinvokes=$pinvokes
} | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $output -Encoding UTF8
Write-Output "Retained $($methods.Count) exact methods and $($pinvokes.Count) termination import."
