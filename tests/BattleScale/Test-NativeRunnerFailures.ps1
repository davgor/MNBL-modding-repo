# Runs the actual runner against inert text fixtures. No native process starts.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runner=Join-Path $repo 'tools/Test-NativePoolInGame.ps1'
$suite=Join-Path $repo ('build/battle-scale/runner-failure-tests-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $suite | Out-Null
$savedLocalAppData=$env:LOCALAPPDATA
$results=@()
function Digest([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }
function Put([string]$p,[string]$text) {
    New-Item -ItemType Directory -Path (Split-Path $p) -Force | Out-Null
    [IO.File]::WriteAllText($p,$text)
}
function Check([bool]$ok,[string]$message) { if (!$ok) { throw $message } }
function Get-Process {
    if ($global:NativeRunnerTest_gamePolls -gt 0) {
        $global:NativeRunnerTest_gamePolls--
        return [pscustomobject]@{ProcessName='Bannerlord.MockOtherSession';Id=777777}
    }
    return @()
}
function Get-CimInstance { return @() }
function Start-Sleep {
    param($Seconds)
    $global:NativeRunnerTest_waits++
    Check (([IO.File]::ReadAllText($global:NativeRunnerTest_nativePath)) -eq 'ORIGINAL NATIVE FIXTURE') 'Runner modified native fixture while waiting for another session.'
}
function Stop-Process { throw 'Unexpected stop attempted by fixture run.' }
function Copy-Item {
    param([string]$LiteralPath,[string]$Destination,[switch]$Force)
    if ($global:NativeRunnerTest_scenario -eq 'partial-copy' -and !$global:NativeRunnerTest_injected -and $LiteralPath -eq "$global:NativeRunnerTest_package/TaleWorlds.Native.dll") {
        $global:NativeRunnerTest_injected=$true
        [IO.File]::WriteAllText($Destination,'PARTIAL COPY')
        throw 'Injected interrupted deployment copy.'
    }
    if ($global:NativeRunnerTest_scenario -eq 'partial-managed-copy' -and !$global:NativeRunnerTest_injected -and $LiteralPath -eq "$global:NativeRunnerTest_package/TaleWorlds.MountAndBlade.dll") {
        $global:NativeRunnerTest_injected=$true
        [IO.File]::WriteAllText($Destination,'PARTIAL MANAGED COPY')
        throw 'Injected interrupted managed deployment copy.'
    }
    Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force
}
function Start-Process {
    param($FilePath,$WorkingDirectory,$ArgumentList,$WindowStyle,[switch]$PassThru)
    $global:NativeRunnerTest_launches++
    Check ([IO.Path]::GetFullPath($FilePath) -eq [IO.Path]::GetFullPath("$global:NativeRunnerTest_game/bin/Win64_Shipping_Client/Bannerlord.exe")) 'Unexpected mock launch target.'
    Check (([IO.File]::ReadAllText($global:NativeRunnerTest_nativePath)) -eq 'PATCHED NATIVE FIXTURE') 'Native fixture was not deployed before launch.'
    Check ($global:NativeRunnerTest_gamePolls -eq 0) 'Launch happened while another session was reported.'
    $request=[IO.File]::ReadAllText("$env:LOCALAPPDATA/MNBL.BattleScale/probe.once")
    $nonce=$request.Substring('native-pools:'.Length)
    if ($global:NativeRunnerTest_scenario -eq 'stale-result') { $nonce='stale-other-run' }
    $report=@{runId=$nonce;status='passed-awaiting-normal-process-exit';exhaustionVerified=$true;teardownVerified=$true;nativeReturnCode=0}
    if ($global:NativeRunnerTest_scenario -eq 'success-admission') {
        Check (([IO.File]::ReadAllText("$global:NativeRunnerTest_game/bin/Win64_Shipping_Client/TaleWorlds.MountAndBlade.dll")) -eq 'PATCHED MANAGED FIXTURE') 'Managed admission was not deployed.'
        $report.publicSpawnAgentOverflowTested=$true;$report.workload='cavalry';$report.requestedSustainedSeconds=300;$report.requestedReuseCycles=3
    }
    Put "$env:LOCALAPPDATA/MNBL.BattleScale/result-888888.json" ($report | ConvertTo-Json)
    $process=[pscustomobject]@{Id=888888;HasExited=$true;ExitCode=0}
    $process | Add-Member ScriptMethod WaitForExit { return $true }
    $process | Add-Member ScriptMethod Refresh { if ($global:NativeRunnerTest_scenario -eq 'cleanup-throw') { throw 'Injected cleanup Refresh failure.' } }
    $process | Add-Member ScriptMethod CloseMainWindow { throw 'Exited fixture process should not be closed.' }
    return $process
}
try {
    foreach($case in @('preexisting-request','partial-copy','cleanup-throw','stale-result','success-after-wait','partial-managed-copy','success-admission')) {
        $global:NativeRunnerTest_scenario=$case; $global:NativeRunnerTest_injected=$false; $global:NativeRunnerTest_launches=0; $global:NativeRunnerTest_waits=0
        $global:NativeRunnerTest_gamePolls=0
        if ($case -eq 'success-after-wait') { $global:NativeRunnerTest_gamePolls=2 }
        $casePath=Join-Path $suite $case
        $global:NativeRunnerTest_package=Join-Path $casePath 'package'; $global:NativeRunnerTest_game=Join-Path $casePath 'game'
        $env:LOCALAPPDATA=Join-Path $casePath 'local'
        $global:NativeRunnerTest_nativePath="$global:NativeRunnerTest_game/bin/Win64_Shipping_Client/TaleWorlds.Native.dll"
        Put $global:NativeRunnerTest_nativePath 'ORIGINAL NATIVE FIXTURE'
        Put "$global:NativeRunnerTest_package/TaleWorlds.Native.dll" 'PATCHED NATIVE FIXTURE'
        Put "$global:NativeRunnerTest_package/MNBL.BattleScale.dll" 'PROBE FIXTURE'
        Put "$global:NativeRunnerTest_package/SubModule.xml" '<fixture />'
        Put "$global:NativeRunnerTest_package/native-pool-manifest.json" (@{status='experimental-static-candidate';readiness=@{staticReady=$true};sourceEvidence=@{}} | ConvertTo-Json -Depth 4)
        Put "$global:NativeRunnerTest_package/build-receipt.json" (@{sources=@()} | ConvertTo-Json)
        $profile=@{staticReady=$true;installationRoot=$global:NativeRunnerTest_game;patchedSha256=(Digest "$global:NativeRunnerTest_package/TaleWorlds.Native.dll");sourceSha256=(Digest $global:NativeRunnerTest_nativePath);probeModuleSha256=(Digest "$global:NativeRunnerTest_package/MNBL.BattleScale.dll");manifestSha256=(Digest "$global:NativeRunnerTest_package/native-pool-manifest.json");trustedFiles=@();requiredModuleArgument='_MODULES_*fixture*_MODULES_'}
        if ($case -in @('partial-managed-copy','success-admission')) {
            Put "$global:NativeRunnerTest_game/bin/Win64_Shipping_Client/TaleWorlds.MountAndBlade.dll" 'ORIGINAL MANAGED FIXTURE'
            Put "$global:NativeRunnerTest_package/TaleWorlds.MountAndBlade.dll" 'PATCHED MANAGED FIXTURE'
            $profile.schemaVersion=3;$profile.publicSpawnAdmission=$true;$profile.workload='cavalry';$profile.sustainedSeconds=300;$profile.reuseCycles=3
            $profile.managedEngineSourceSha256=Digest "$global:NativeRunnerTest_game/bin/Win64_Shipping_Client/TaleWorlds.MountAndBlade.dll"
            $profile.managedEngineSha256=Digest "$global:NativeRunnerTest_package/TaleWorlds.MountAndBlade.dll"
            Put "$global:NativeRunnerTest_package/managed-admission-receipt.json" (@{sourceSha256=$profile.managedEngineSourceSha256;patchedSha256=$profile.managedEngineSha256;moduleSha256=$profile.probeModuleSha256;patchScriptSha256=(Digest "$repo/tools/battle-scale/Patch-ManagedSpawnAdmission.ps1")} | ConvertTo-Json)
            $profile.managedAdmissionReceiptSha256=Digest "$global:NativeRunnerTest_package/managed-admission-receipt.json"
        }
        Put "$global:NativeRunnerTest_package/native-pool-test-profile.json" ($profile | ConvertTo-Json)
        if ($case -eq 'preexisting-request') { Put "$env:LOCALAPPDATA/MNBL.BattleScale/probe.once" 'native-pools:other-owner' }
        $failure=$null
        try { & $runner -Package $global:NativeRunnerTest_package -GameRoot $global:NativeRunnerTest_game -TimeoutMinutes 1 | Out-Null } catch { $failure=$_.Exception.Message }
        Check (([IO.File]::ReadAllText($global:NativeRunnerTest_nativePath)) -eq 'ORIGINAL NATIVE FIXTURE') "$case left the native fixture modified."
        if ($case -in @('partial-managed-copy','success-admission')) { Check (([IO.File]::ReadAllText("$global:NativeRunnerTest_game/bin/Win64_Shipping_Client/TaleWorlds.MountAndBlade.dll")) -eq 'ORIGINAL MANAGED FIXTURE') "$case left the managed engine modified." }
        Check (!(Test-Path -LiteralPath "$global:NativeRunnerTest_game/Modules/MNBL.BattleScale/SubModule.xml")) "$case left a test module file deployed."
        if ($case -eq 'preexisting-request') {
            Check (([IO.File]::ReadAllText("$env:LOCALAPPDATA/MNBL.BattleScale/probe.once")) -eq 'native-pools:other-owner') 'Another owner request was removed.'
            Check ($global:NativeRunnerTest_launches -eq 0) 'Preexisting request did not prevent launch.'
        } else { Check (!(Test-Path -LiteralPath "$env:LOCALAPPDATA/MNBL.BattleScale/probe.once")) "$case left its own request."
        }
        if ($case -eq 'success-admission') { Check ($null -eq $failure -and $global:NativeRunnerTest_launches -eq 1) "Expected managed admission success: $failure" }
        elseif ($case -eq 'success-after-wait') { Check ($null -eq $failure -and $global:NativeRunnerTest_launches -eq 1 -and $global:NativeRunnerTest_waits -gt 0) "Expected success after waiting; actual failure=$failure launches=$global:NativeRunnerTest_launches waits=$global:NativeRunnerTest_waits" }
        else {
            $expectedFailure=@{'preexisting-request'='Another unclaimed probe request';'partial-copy'='Injected interrupted deployment';'partial-managed-copy'='Injected interrupted managed deployment';'cleanup-throw'='Injected cleanup Refresh';'stale-result'='Probe result belongs to another run'}[$case]
            Check ($null -ne $failure -and $failure.Contains($expectedFailure)) "$case did not reach its intended fault: $failure"
        }
        # Exclusive reopen proves cleanup released the real runner lease.
        $lease=[IO.File]::Open("$repo/build/battle-scale/native-test.lock",[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        $lease.Dispose()
        $results+=,@{scenario=$case;passed=$true;mockLaunches=$global:NativeRunnerTest_launches;failure=$failure;originalRestored=$true}
    }
    $receipt=@{status='passed';runnerSha256=(Digest $runner);testSha256=(Digest $PSCommandPath);gameExecuted=$false;installedFilesTouched=$false;fixtureDirectory=$suite;cases=$results}
    $receipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$suite/review.json" -Encoding utf8
    Write-Output ($receipt | ConvertTo-Json -Depth 6)
} finally { $env:LOCALAPPDATA=$savedLocalAppData }
