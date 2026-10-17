# This recognizes one source-bound engine/watchdog shutdown protocol. It does
# not accept an arbitrary nonzero exit or prove every process destructor ran.
function Confirm-NativeExitContract {
    param($Profile, $Probe, [int]$ExitCode, [int]$GameProcessId,
          [int[]]$OwnedWatchdogIds, [datetime]$StartedUtc, [string]$GameRoot,
          [string]$LogRoot, [string]$RunPath)
    if ($Probe.nativeReturnCode -ne 0) { throw 'Native return code before quit was not zero.' }
    if ($ExitCode -eq 0) {
        return @{verified=$true;kind='process-exit-zero';exitCode=0;allNativeDestructorsVerified=$false}
    }
    if ($ExitCode -ne -1) { throw "Game exited abnormally: $ExitCode" }
    if ($Profile.PSObject.Properties.Name -notcontains 'exitContract') { throw 'Nonzero exit lacks a reviewed shutdown contract.' }
    $reference=$Profile.exitContract
    $actual=(Get-FileHash -LiteralPath $reference.path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $reference.sha256) { throw 'Shutdown contract evidence changed.' }
    $contract=Get-Content -LiteralPath $reference.path -Raw | ConvertFrom-Json
    if ($contract.status -ne 'reviewed-build-specific-watchdog-shutdown' -or
        $contract.nativeSourceSha256 -ne $Profile.sourceSha256 -or
        $contract.patchedCandidateSha256 -ne $Profile.patchedSha256 -or
        $contract.expectedWatchdogExitCode -ne -1) { throw 'Shutdown contract does not cover this native build.' }
    $expectedWatchdog=[IO.Path]::GetFullPath((Join-Path $GameRoot 'bin/Win64_Shipping_Client/Watchdog/Watchdog.exe'))
    if ([IO.Path]::GetFullPath($contract.watchdogPath) -ne $expectedWatchdog -or
        (Get-FileHash -LiteralPath $expectedWatchdog -Algorithm SHA256).Hash.ToLowerInvariant() -ne $contract.watchdogSha256) {
        throw 'Watchdog differs from the reviewed shutdown implementation.'
    }
    if (!$OwnedWatchdogIds -or $OwnedWatchdogIds.Count -eq 0) { throw 'No owned watchdog was observed for this process.' }
    $watchdogLog=Join-Path $LogRoot "watchdog_log_$GameProcessId.txt"
    $gameLog=Join-Path $LogRoot "rgl_log_$GameProcessId.txt"
    # The debugger may finish writing its exit event just after the game ends.
    for ($attempt=0; $attempt -lt 25; $attempt++) {
        if ((Test-Path -LiteralPath $watchdogLog) -and
            ([IO.File]::ReadAllText($watchdogLog)).Contains('EXIT_PROCESS_DEBUG_EVENT occurred. Detaching...')) { break }
        Start-Sleep -Milliseconds 200
    }
    foreach($path in @($watchdogLog,$gameLog)) {
        if (!(Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).LastWriteTimeUtc -lt $StartedUtc) {
            throw 'Missing or stale per-process shutdown log.'
        }
    }
    $watchdog=[IO.File]::ReadAllText($watchdogLog)
    $game=[IO.File]::ReadAllText($gameLog)
    if ($watchdog -notmatch ('(?m)^\s*PID:\s*'+$GameProcessId+'\s*$')) { throw 'Watchdog log belongs to a different PID.' }
    $markers=@('Debuggee message: #TW#-kp','[DebuggerConfiguration] -kp','EXIT_PROCESS_DEBUG_EVENT occurred. Detaching...')
    $cursor=0
    foreach($marker in $markers) {
        $found=$watchdog.IndexOf($marker,$cursor,[StringComparison]::Ordinal)
        if ($found -lt 0) { throw 'Watchdog did not record the ordered normal shutdown command and exit.' }
        $cursor=$found+$marker.Length
    }
    if ($watchdog -match '(?i)EXCEPTION_DEBUG_EVENT|Unhandled exception|ExceptionCode|Exception code|Writing[^\r\n]*dump|Creating[^\r\n]*dump') {
        throw 'Watchdog recorded an exception or crash dump.'
    }
    $cursor=0
    foreach($marker in @('Final_cleanup...','Deleting resources...','Pre Finalizing Managed Interface...',
                         'Deleting Managed Interface...','There are no living managed objects.','Managed Interface deleted.')) {
        $found=$game.IndexOf($marker,$cursor,[StringComparison]::Ordinal)
        if ($found -lt 0) { throw 'Engine final-cleanup milestones are missing or out of order.' }
        $cursor=$found+$marker.Length
    }
    $retained=@()
    foreach($path in @($watchdogLog,$gameLog)) {
        $copy=Join-Path $RunPath ([IO.Path]::GetFileName($path))
        Copy-Item -LiteralPath $path -Destination $copy
        $retained+=,@{path=$copy;sha256=(Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash.ToLowerInvariant()}
    }
    return @{verified=$true;kind='reviewed-watchdog-kp-command';exitCode=-1;gamePid=$GameProcessId;
             ownedWatchdogIds=$OwnedWatchdogIds;contractSha256=$reference.sha256;logs=$retained;
             allNativeDestructorsVerified=$false;
             limitation='The stock watchdog terminates the process after main shutdown; remaining native releases after the command are not certified.'}
}
