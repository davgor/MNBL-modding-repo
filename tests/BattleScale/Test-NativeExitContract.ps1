# Inert fixtures exercise the real exit classifier; no game or watchdog starts.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $repo 'tools/battle-scale/Test-NativeExitContract.ps1')
$suite=Join-Path $repo ('build/battle-scale/exit-contract-tests-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $suite | Out-Null
function Put([string]$path,[string]$value) {
    New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null
    [IO.File]::WriteAllText($path,$value)
}
function Digest([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
# Complete fixtures are immediate. Missing markers must not sleep during tests.
function Start-Sleep { param($Milliseconds) }
$results=@()
foreach($case in @('valid-zero','valid-watchdog','other-code','native-failure','missing-contract','stale-contract',
                   'wrong-native','wrong-patch','wrong-watchdog','no-owned-watchdog','wrong-pid','missing-kp','reversed-kp',
                   'exception','missing-cleanup','reversed-cleanup','stale-log')) {
    $folder=Join-Path $suite $case; $game=Join-Path $folder 'game'; $logs=Join-Path $folder 'logs'; $out=Join-Path $folder 'out'
    New-Item -ItemType Directory -Path $out -Force | Out-Null
    $started=[DateTime]::UtcNow.AddSeconds(-5)
    $watchdog=Join-Path $game 'bin/Win64_Shipping_Client/Watchdog/Watchdog.exe'
    Put $watchdog 'inert watchdog fixture'
    $contractPath=Join-Path $folder 'contract.json'
    $contract=@{status='reviewed-build-specific-watchdog-shutdown';nativeSourceSha256='original-native-fixture';
                patchedCandidateSha256='patched-native-fixture';expectedWatchdogExitCode=-1;watchdogPath=$watchdog;watchdogSha256=(Digest $watchdog)}
    if ($case -eq 'wrong-native') { $contract.nativeSourceSha256='other-build' }
    if ($case -eq 'wrong-patch') { $contract.patchedCandidateSha256='other-patch' }
    Put $contractPath ($contract | ConvertTo-Json)
    $profile=[pscustomobject]@{sourceSha256='original-native-fixture';patchedSha256='patched-native-fixture';exitContract=[pscustomobject]@{path=$contractPath;sha256=(Digest $contractPath)}}
    $probe=[pscustomobject]@{nativeReturnCode=0}
    $code=-1; $watchdogIds=@(2345)
    $watchText="PID: 1234`nDebuggee message: #TW#-kp`n[DebuggerConfiguration] -kp`nEXIT_PROCESS_DEBUG_EVENT occurred. Detaching...`n"
    $gameText="Final_cleanup...`nDeleting resources...`nPre Finalizing Managed Interface...`nDeleting Managed Interface...`nThere are no living managed objects.`nManaged Interface deleted.`n"
    switch($case) {
        'valid-zero' { $code=0; $watchdogIds=@() }
        'other-code' { $code=5 }
        'native-failure' { $probe.nativeReturnCode=5 }
        'missing-contract' { $profile=[pscustomobject]@{sourceSha256='original-native-fixture'} }
        'stale-contract' { Put $contractPath '{}' }
        'wrong-watchdog' { Put $watchdog 'changed watchdog fixture' }
        'no-owned-watchdog' { $watchdogIds=@() }
        'wrong-pid' { $watchText=$watchText.Replace('PID: 1234','PID: 9999') }
        'missing-kp' { $watchText=$watchText.Replace('Debuggee message: #TW#-kp','unrelated event') }
        'reversed-kp' { $watchText="PID: 1234`n[DebuggerConfiguration] -kp`nDebuggee message: #TW#-kp`nEXIT_PROCESS_DEBUG_EVENT occurred. Detaching...`n" }
        'exception' { $watchText+="EXCEPTION_DEBUG_EVENT occurred.`n" }
        'missing-cleanup' { $gameText=$gameText.Replace('Managed Interface deleted.','unfinished') }
        'reversed-cleanup' { $gameText="Managed Interface deleted.`nThere are no living managed objects.`nDeleting Managed Interface...`nPre Finalizing Managed Interface...`nDeleting resources...`nFinal_cleanup...`n" }
    }
    Put (Join-Path $logs 'watchdog_log_1234.txt') $watchText
    Put (Join-Path $logs 'rgl_log_1234.txt') $gameText
    if ($case -eq 'stale-log') { (Get-Item -LiteralPath (Join-Path $logs 'watchdog_log_1234.txt')).LastWriteTimeUtc=$started.AddDays(-1) }
    $errorText=$null; $answer=$null
    try { $answer=Confirm-NativeExitContract -Profile $profile -Probe $probe -ExitCode $code -GameProcessId 1234 -OwnedWatchdogIds $watchdogIds -StartedUtc $started -GameRoot $game -LogRoot $logs -RunPath $out }
    catch { $errorText=$_.Exception.Message }
    $shouldPass=$case -in @('valid-zero','valid-watchdog')
    if ($shouldPass -and ($errorText -or !$answer.verified)) { throw "$case unexpectedly failed: $errorText" }
    if (!$shouldPass -and !$errorText) { throw "$case incorrectly passed" }
    if ($case -eq 'valid-watchdog' -and $answer.logs.Count -ne 2) { throw 'Accepted protocol did not retain logs' }
    $results+=,@{case=$case;passed=$true;rejectedReason=$errorText}
}
$report=@{status='passed';gameExecuted=$false;helperSha256=(Digest (Join-Path $repo 'tools/battle-scale/Test-NativeExitContract.ps1'));testSha256=(Digest $PSCommandPath);cases=$results}
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $suite 'review.json') -Encoding utf8
$report | ConvertTo-Json -Depth 5
