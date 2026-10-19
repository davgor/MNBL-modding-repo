param(
    [Parameter(Mandatory=$true)][string]$Package,
    [string]$GameRoot='D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord',
    [ValidateRange(1,60)][int]$TimeoutMinutes=18,
    [switch]$PreflightOnly
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot))
. (Join-Path $PSScriptRoot 'battle-scale/Test-NativeExitContract.ps1')
$packagePath=[IO.Path]::GetFullPath($Package)
$gamePath=[IO.Path]::GetFullPath($GameRoot)
$bin=Join-Path $gamePath 'bin/Win64_Shipping_Client'
$store=Join-Path $env:LOCALAPPDATA 'MNBL.BattleScale'
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Games { return ,@(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like 'Bannerlord*' }) }
function Wait-ForGameWindow {
    while ($true) {
        $running=Games
        if ($running.Count -eq 0) { return }
        Write-Output "Waiting for existing Bannerlord session(s): $($running.Id -join ','). No files changed while waiting."
        Start-Sleep -Seconds 15
    }
}
$profile=Get-Content -LiteralPath "$packagePath/native-pool-test-profile.json" -Raw | ConvertFrom-Json
$manifest=Get-Content -LiteralPath "$packagePath/native-pool-manifest.json" -Raw | ConvertFrom-Json
$admission=($profile.PSObject.Properties.Name -contains 'publicSpawnAdmission') -and $profile.publicSpawnAdmission -eq $true
if ($admission) {
    if ($profile.schemaVersion -ne 3 -or (Hash "$packagePath/TaleWorlds.MountAndBlade.dll") -ne $profile.managedEngineSha256 -or (Hash "$packagePath/managed-admission-receipt.json") -ne $profile.managedAdmissionReceiptSha256) { throw 'Managed admission package mismatch.' }
    $admissionReceipt=Get-Content -LiteralPath "$packagePath/managed-admission-receipt.json" -Raw | ConvertFrom-Json
    if ($admissionReceipt.sourceSha256 -ne $profile.managedEngineSourceSha256 -or $admissionReceipt.patchedSha256 -ne $profile.managedEngineSha256 -or $admissionReceipt.moduleSha256 -ne $profile.probeModuleSha256 -or $admissionReceipt.patchScriptSha256 -ne (Hash "$repo/tools/battle-scale/Patch-ManagedSpawnAdmission.ps1")) { throw 'Stale managed admission build.' }
    if ((Hash "$bin/TaleWorlds.MountAndBlade.dll") -ne $profile.managedEngineSourceSha256) { throw 'Installed managed engine differs from the reviewed original.' }
}
if ($profile.staticReady -ne $true -or $manifest.status -ne 'experimental-static-candidate' -or $manifest.readiness.staticReady -ne $true) { throw 'Package lacks static acceptance.' }
if ([IO.Path]::GetFullPath($profile.installationRoot) -ne $gamePath) { throw 'Package belongs to another installation.' }
if ((Hash "$packagePath/TaleWorlds.Native.dll") -ne $profile.patchedSha256 -or (Hash "$packagePath/MNBL.BattleScale.dll") -ne $profile.probeModuleSha256 -or (Hash "$packagePath/native-pool-manifest.json") -ne $profile.manifestSha256) { throw 'Package content hash mismatch.' }
foreach($item in $manifest.sourceEvidence.PSObject.Properties) {
    if ((Hash $item.Value.path) -ne $item.Value.sha256) { throw "Stale evidence: $($item.Name)" }
}
if ((Hash "$bin/TaleWorlds.Native.dll") -ne $profile.sourceSha256) { throw 'Installed native DLL is not the reviewed original. Refusing to overwrite.' }
$build=Get-Content -LiteralPath "$packagePath/build-receipt.json" -Raw | ConvertFrom-Json
foreach($source in $build.sources) { if ((Hash $source.path) -ne $source.sha256) { throw "Probe build source changed: $($source.path)" } }
Write-Output "Preflight passed; current game sessions: $((Games).Count). Target4080; isolated Single0 profile."
if ($PreflightOnly) { return }

# A local lease prevents this runner being started twice; the process checks
# additionally cover independently launched Claude/user sessions.
New-Item -ItemType Directory -Path "$repo/build/battle-scale" -Force | Out-Null
$lease=[IO.File]::Open("$repo/build/battle-scale/native-test.lock",[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$runPath=Join-Path "$repo/build/battle-scale" ("native-run-"+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8))
$owned=$null; $changed=@(); $ownedWatchdogs=@(); $runError=$null; $result=$null; $exitCode=$null; $restored=$false; $requestCreated=$false
$runId=[guid]::NewGuid().ToString('D'); $requestText='native-pools:'+$runId
$receipt=[ordered]@{status='preparing';authorization='User authorized in-game testing with a fresh no-other-game check before every launch.';packagePath=$packagePath;startedUtc=[DateTime]::UtcNow.ToString('O');sourceSha256=$profile.sourceSha256;patchedSha256=$profile.patchedSha256;moduleSha256=$profile.probeModuleSha256;manifestSha256=$profile.manifestSha256;runtimeVerified=$false;visibleSoldierCountVerified=$false}
$receipt['runId']=$runId
if ($admission) { $receipt['managedEngineSha256']=$profile.managedEngineSha256; $receipt['workload']=$profile.workload }
try {
    Wait-ForGameWindow
    New-Item -ItemType Directory -Path $runPath | Out-Null
    New-Item -ItemType Directory -Path $store -Force | Out-Null
    if (Test-Path -LiteralPath "$store/probe.once") { throw 'Another unclaimed probe request exists.' }
    if ((Hash "$bin/TaleWorlds.Native.dll") -ne $profile.sourceSha256) { throw 'Installed native source changed while waiting.' }
    if ($admission -and (Hash "$bin/TaleWorlds.MountAndBlade.dll") -ne $profile.managedEngineSourceSha256) { throw 'Installed managed source changed while waiting.' }
    $modulePath=Join-Path $gamePath 'Modules/MNBL.BattleScale'
    $destinations=@(
        @{source="$packagePath/TaleWorlds.Native.dll";destination="$bin/TaleWorlds.Native.dll"},
        @{source="$packagePath/MNBL.BattleScale.dll";destination="$modulePath/bin/Win64_Shipping_Client/MNBL.BattleScale.dll"},
        @{source="$packagePath/SubModule.xml";destination="$modulePath/SubModule.xml"},
        @{source="$packagePath/native-pool-test-profile.json";destination="$modulePath/bin/Win64_Shipping_Client/native-pool-test-profile.json"}
    )
    if ($admission) { $destinations+=@{source="$packagePath/TaleWorlds.MountAndBlade.dll";destination="$bin/TaleWorlds.MountAndBlade.dll"} }
    foreach($entry in $destinations) {
        if ((Games).Count -gt 0) { throw 'Another Bannerlord started before deployment completed; cancelling this launch.' }
        $destination=[IO.Path]::GetFullPath($entry.destination)
        if (!$destination.StartsWith($gamePath.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Destination escaped installation.' }
        $backup=Join-Path $runPath ("backup-"+$changed.Count)
        $exists=Test-Path -LiteralPath $destination
        $oldHash=$null
        if ($exists) { Copy-Item -LiteralPath $destination -Destination $backup; $oldHash=Hash $backup }
        New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
        $record=@{destination=$destination;backup=$backup;existed=$exists;originalSha256=$oldHash;testSha256=(Hash $entry.source);copyComplete=$false}
        # Record rollback intent before copy, so a partially failed copy is recoverable.
        $changed+=,$record
        $changed | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$runPath/restore-files.json" -Encoding utf8
        Copy-Item -LiteralPath $entry.source -Destination $destination -Force
        if ((Hash $destination) -ne $record.testSha256) { throw 'Deployment readback differs.' }
        $record.copyComplete=$true
        $changed | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$runPath/restore-files.json" -Encoding utf8
    }
    $changed | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$runPath/restore-files.json" -Encoding utf8
    # Validate the entire pinned provider profile before starting native code.
    foreach($provider in $profile.trustedFiles) {
        $path=Join-Path $gamePath $provider.relativePath
        if ((Hash $path) -ne $provider.sha256) { throw "Installed provider differs: $($provider.relativePath)" }
    }
    if ($profile.PSObject.Properties.Name -contains 'trustedExternalFiles') {
        foreach($provider in $profile.trustedExternalFiles) {
            if ((Hash $provider.path) -ne $provider.sha256) { throw "Platform provider differs: $($provider.path)" }
        }
    }
    if ((Games).Count -gt 0) { throw 'Another Bannerlord started during preflight; cancelling this launch.' }
    # Exclusive create prevents consuming/replacing another pending request.
    $requestStream=[IO.File]::Open("$store/probe.once",[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    $requestCreated=$true
    try { $requestBytes=[Text.Encoding]::UTF8.GetBytes($requestText); $requestStream.Write($requestBytes,0,$requestBytes.Length) } finally { $requestStream.Dispose() }
    if ((Games).Count -gt 0) { throw 'Another Bannerlord started before launch; cancelling.' }
    $arguments='/singleplayer '+$profile.requiredModuleArgument+' /maxThreadCount 16'
    $owned=Start-Process -FilePath "$bin/Bannerlord.exe" -WorkingDirectory $bin -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $receipt['pid']=$owned.Id; $receipt['status']='running'; $receipt['arguments']=$arguments
    $receipt['resultPath']="$store/result-$($owned.Id).json"
    $receipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$runPath/runtime-receipt.json" -Encoding utf8
    Write-Output "Owned native probe PID $($owned.Id); receipt $runPath/runtime-receipt.json"
    $deadline=[DateTime]::UtcNow.AddMinutes($TimeoutMinutes); $lastStatus=''
    while (!$owned.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        $children=@(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($owned.Id)" | Where-Object { $_.Name -eq 'Watchdog.exe' })
        foreach($child in $children) {
            $expectedWatchdog=[IO.Path]::GetFullPath((Join-Path $bin 'Watchdog/Watchdog.exe'))
            if (!$child.ExecutablePath -or [IO.Path]::GetFullPath($child.ExecutablePath) -ne $expectedWatchdog) {
                throw 'Owned watchdog has an unexpected executable path.'
            }
            if ($ownedWatchdogs -notcontains $child.ProcessId) { $ownedWatchdogs+=,$child.ProcessId }
        }
        if (Test-Path -LiteralPath $receipt.resultPath) {
            try {
                $snapshot=Get-Content -LiteralPath $receipt.resultPath -Raw | ConvertFrom-Json
                $status=$snapshot.status+' stage='+$snapshot.stage+' count='+$snapshot.currentEntities
                if ($status -ne $lastStatus) { Write-Output $status; $lastStatus=$status }
            } catch { } # Reporter may be replacing the file; retry next observation.
        }
        Start-Sleep -Seconds 2; $owned.Refresh()
    }
    if (!$owned.HasExited) { throw 'Owned native probe exceeded its wall-clock deadline.' }
    $owned.WaitForExit(); $exitCode=$owned.ExitCode
    if (!(Test-Path -LiteralPath $receipt.resultPath)) { throw "Game exited with code $exitCode without a probe result." }
    Copy-Item -LiteralPath $receipt.resultPath -Destination "$runPath/probe-result.json"
    $result=Get-Content -LiteralPath $receipt.resultPath -Raw | ConvertFrom-Json
    if ($result.runId -ne $runId) { throw 'Probe result belongs to another run; refusing stale PID result.' }
    if ($result.status -ne 'passed-awaiting-normal-process-exit' -or $result.exhaustionVerified -ne $true -or $result.teardownVerified -ne $true) { throw "Probe did not pass exhaustion and teardown: $($result.status)" }
    if ($admission) {
        if ($result.publicSpawnAgentOverflowTested -ne $true -or $result.workload -ne $profile.workload -or $result.requestedSustainedSeconds -ne $profile.sustainedSeconds -or $result.requestedReuseCycles -ne $profile.reuseCycles) { throw 'Expanded workload/public spawn acceptance did not match the requested profile.' }
    }
    $shutdown=Confirm-NativeExitContract -Profile $profile -Probe $result -ExitCode $exitCode -GameProcessId $owned.Id -OwnedWatchdogIds $ownedWatchdogs -StartedUtc ([datetime]$receipt.startedUtc) -GameRoot $gamePath -LogRoot (Join-Path $env:ProgramData 'Mount and Blade II Bannerlord/logs') -RunPath $runPath
    $receipt['shutdownVerification']=$shutdown
    $receipt['runtimeVerified']=$true; $receipt['status']='passed-exhaustion-and-teardown'
} catch {
    $runError=$_.Exception.ToString(); $receipt['status']='failed'; $receipt['error']=$runError
} finally {
    try {
        try {
            if ($null -ne $owned) {
                $owned.Refresh()
                if (!$owned.HasExited) {
                    $owned.CloseMainWindow() | Out-Null
                    if (!$owned.WaitForExit(10000)) { Stop-Process -Id $owned.Id; $owned.WaitForExit() }
                }
                $exitCode=$owned.ExitCode
            }
            # Only recorded children of our own PID may be stopped.
            foreach($watchdogId in $ownedWatchdogs) {
                $watchdog=Get-CimInstance Win32_Process -Filter "ProcessId=$watchdogId" -ErrorAction SilentlyContinue
                if ($watchdog -and $null -ne $owned -and $watchdog.ParentProcessId -eq $owned.Id -and $watchdog.Name -eq 'Watchdog.exe') { Stop-Process -Id $watchdogId -ErrorAction SilentlyContinue }
            }
        } catch { $receipt['cleanupError']=$_.Exception.ToString(); $runError=$_.Exception.ToString(); $receipt['status']='cleanup-failed' }
        try {
            if ($requestCreated -and (Test-Path -LiteralPath "$store/probe.once")) {
                if ((Get-Content -LiteralPath "$store/probe.once" -Raw) -eq $requestText) { Remove-Item -LiteralPath "$store/probe.once" }
            }
        } catch { $receipt['requestCleanupError']=$_.Exception.ToString(); $runError=$_.Exception.ToString(); $receipt['status']='cleanup-failed' }
        try {
            if ($changed.Count -gt 0) {
                Wait-ForGameWindow
                foreach($entry in $changed) {
                    if ($entry.copyComplete -and (Test-Path -LiteralPath $entry.destination)) {
                        $current=Hash $entry.destination
                        if ($current -ne $entry.testSha256 -and $current -ne $entry.originalSha256) { throw "Unexpected external file change; retained backup $($entry.backup)" }
                    }
                    # An interrupted write by this run is restored from its
                    # pre-write backup even when the partial bytes hash differently.
                    if ($entry.existed) {
                        if (!(Test-Path -LiteralPath $entry.destination) -or (Hash $entry.destination) -ne $entry.originalSha256) {
                            Copy-Item -LiteralPath $entry.backup -Destination $entry.destination -Force
                        }
                        if ((Hash $entry.destination) -ne $entry.originalSha256) { throw 'Restoration hash mismatch.' }
                    } elseif (Test-Path -LiteralPath $entry.destination) { Remove-Item -LiteralPath $entry.destination }
                }
                $restored=(Hash "$bin/TaleWorlds.Native.dll") -eq $profile.sourceSha256
            }
        } catch { $receipt['restorationError']=$_.Exception.ToString(); $runError=$_.Exception.ToString(); $receipt['status']='restoration-failed' }
        $receipt['restoredOriginalFiles']=$restored; $receipt['exitCode']=$exitCode; $receipt['result']=$result; $receipt['endedUtc']=[DateTime]::UtcNow.ToString('O')
        if (Test-Path -LiteralPath $runPath) { $receipt | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath "$runPath/runtime-receipt.json" -Encoding utf8 }
    } finally { $lease.Dispose() }
}
Write-Output "Run result: $($receipt.status); originals restored=$restored; $runPath/runtime-receipt.json"
if ($runError) { throw $runError }
