# Requires PowerShell7.4+ for child-only Start-Process -Environment.
# Default invocation is read-only preflight. Execute requires an exact reviewed profile hash.
param(
    [Parameter(Mandatory=$true)][string]$Package,
    [string]$GameRoot='D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord',
    [ValidateRange(1,30)][int]$TimeoutMinutes=10,
    [switch]$PreflightOnly,
    [switch]$Execute,
    [string]$ReviewedPackageSha256
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSVersion -lt [version]'7.4') { throw 'PowerShell7.4+ is required for isolated child environment.' }
if ($PreflightOnly -and $Execute) { throw 'PreflightOnly and Execute are mutually exclusive.' }
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$packagePath=[IO.Path]::GetFullPath($Package)
$gamePath=[IO.Path]::GetFullPath($GameRoot)
$bin=Join-Path $gamePath 'bin/Win64_Shipping_Client'
$moduleRoot=Join-Path $gamePath 'Modules/MNBL.AgentGrowthProbe'
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Games { return ,@(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like 'Bannerlord*' }) }
function Wait-ForGameWindow {
    while ((Games).Count -ne 0) { Write-Output 'Waiting for every existing Bannerlord session to exit.'; Start-Sleep -Seconds 10 }
}
function FreshWindow {
    Wait-ForGameWindow
    if ((Games).Count -ne 0) { throw 'A Bannerlord session started during the final window check.' }
}
function SafeDestination([string]$path) {
    $full=[IO.Path]::GetFullPath($path)
    if (!$full.StartsWith($moduleRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Destination escaped private module.' }
    $cursor=$full
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if (((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse point in deployment path.' }
        }
        $cursor=Split-Path $cursor -Parent
    }
    return $full
}
function SaveJson([string]$path,$value) {
    $temp=$path+'.tmp'
    $value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $temp -Encoding utf8
    Move-Item -LiteralPath $temp -Destination $path -Force
}
function VerifyInputs {
    if ((Hash "$packagePath/element-probe-profile.json") -ne $profileHash) { throw 'Profile changed after preflight.' }
    foreach($source in @($profile.sources)+@($profile.providers)) {
        if ((Hash $source.path) -ne $source.sha256) { throw "Input changed: $($source.path)" }
    }
    foreach($file in $profile.files) {
        if ((Hash (Join-Path $packagePath $file.name)) -ne $file.sha256) { throw "Package changed: $($file.name)" }
    }
}
function VerifyResult($r,[int]$ownedId,[long]$ownedStart) {
    if ($r.schemaVersion -ne 1 -or $r.runId -ne $runId -or $r.pid -ne $ownedId -or $r.startTicks -ne $ownedStart -or
        $r.nativeSha256 -ne $nativeHash -or $r.hostSha256 -ne $hostHash -or $r.status -ne 'passed-awaiting-normal-process-exit' -or
        $r.quitRequested -ne $true -or $r.mainMenuReady -ne $true -or $r.missionAbsent -ne $true -or $r.menuWaitSeconds -lt 3 -or $r.nativeCode -ne 0 -or $r.error) { throw 'Probe report identity or completion rejected.' }
    $n=$r.native
    $expected=@{abi_size=712;abi_version=1;status=1;stage=8;capacity=90;pointer_checks=600;header_checks=450;allocation_attempts=21;allocations=20;releases=20;constructor_attempts=488;
        allocation_failure_checked=1;constructor_failure_checked=1;exhaustion_checked=1;reuse_checked=1}
    foreach($key in $expected.Keys) { if ($n.$key -ne $expected[$key]) { throw "Native counter rejected: $key" } }
    $counts=@(120,97,90,90,90)
    if (@($n.constructed).Count -ne 5 -or @($n.destroyed).Count -ne 5 -or $n.ErrorText -ne '') { throw 'Native cleanup/error report rejected.' }
    for($i=0;$i -lt 5;$i++) { if ($n.constructed[$i] -ne $counts[$i] -or $n.destroyed[$i] -ne $counts[$i]) { throw 'Native per-pool cleanup rejected.' } }
}
function VerifyOwnedIdentity($process,[long]$start,[string]$path) {
    $process.Refresh()
    if ($process.HasExited) { return }
    if ($process.StartTime.ToUniversalTime().Ticks -ne $start -or [IO.Path]::GetFullPath($process.MainModule.FileName) -ne $path) { throw 'Owned process identity changed; refusing process control.' }
}
$profileHash=Hash "$packagePath/element-probe-profile.json"
$profile=Get-Content -LiteralPath "$packagePath/element-probe-profile.json" -Raw | ConvertFrom-Json
if ($profile.schemaVersion -ne 1 -or $profile.status -ne 'offline-built-private-element-probe' -or [IO.Path]::GetFullPath($profile.gameRoot) -ne $gamePath -or
    $profile.environmentKey -ne 'MNBL_PRIVATE_ELEMENT_PROBE_REQUEST' -or $profile.modules -ne '_MODULES_*Native*SandBoxCore*CustomBattle*MNBL.AgentGrowthProbe*_MODULES_') { throw 'Unexpected private-probe profile.' }
$names=@($profile.files | ForEach-Object name | Sort-Object)
if (($names -join '|') -ne 'MNBL.AgentGrowthProbe.dll|MNBL.NativeElementProbe.dll|SubModule.xml') { throw 'Package must contain exactly the three private module files.' }
VerifyInputs
$nativeHash=($profile.files | Where-Object name -eq 'MNBL.NativeElementProbe.dll').sha256
$hostHash=($profile.files | Where-Object name -eq 'MNBL.AgentGrowthProbe.dll').sha256
Write-Output "Private probe preflight passed. Existing Bannerlord sessions: $((Games).Count)."
if (!$Execute) { return }
if ($ReviewedPackageSha256 -cne $profileHash) { throw 'Execute requires the exact independently reviewed profile SHA256.' }

# Share the existing repository native-test lease with other MNBL runners.
$leasePath=Join-Path $repo 'build/battle-scale/native-test.lock'
$lease=[IO.File]::Open($leasePath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$runId=[guid]::NewGuid().ToString('D')
$runPath=Join-Path $repo ('build/element-probe-host/runs/'+$runId)
$owned=$null;$ownedStart=0L;$changed=@();$failure=$null;$restored=$false;$forced=$false;$timedOut=$false;$nativePassed=$false;$normalExit=$false;$exitCode=$null
$gameExe=[IO.Path]::GetFullPath((Join-Path $bin 'Bannerlord.exe'))
$receipt=[ordered]@{schemaVersion=1;runId=$runId;status='preparing';profileSha256=$profileHash;nativeSha256=$nativeHash;hostSha256=$hostHash;startedUtc=[DateTime]::UtcNow.ToString('O');
    nativeProbePassed=$false;normalExitVerified=$false;restored=$false;gameLaunched=$false;engineDllModified=$false}
try {
    FreshWindow
    VerifyInputs
    New-Item -ItemType Directory -Path $runPath | Out-Null
    $destinations=@(
        @{source=(Join-Path $packagePath 'SubModule.xml');destination=(Join-Path $moduleRoot 'SubModule.xml')},
        @{source=(Join-Path $packagePath 'MNBL.AgentGrowthProbe.dll');destination=(Join-Path $moduleRoot 'bin/Win64_Shipping_Client/MNBL.AgentGrowthProbe.dll')},
        @{source=(Join-Path $packagePath 'MNBL.NativeElementProbe.dll');destination=(Join-Path $moduleRoot 'bin/Win64_Shipping_Client/MNBL.NativeElementProbe.dll')})
    foreach($entry in $destinations) {
        FreshWindow
        $destination=SafeDestination $entry.destination
        $backup=Join-Path $runPath ('backup-'+$changed.Count)
        $existed=Test-Path -LiteralPath $destination
        $oldHash=$null
        if ($existed) {
            $oldHash=Hash $destination
            Copy-Item -LiteralPath $destination -Destination $backup
            if ((Hash $backup) -ne $oldHash -or (Hash $destination) -ne $oldHash) { throw 'Original changed while backing up.' }
        }
        New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
        $record=[ordered]@{destination=$destination;backup=$backup;existed=$existed;originalSha256=$oldHash;deployedSha256=(Hash $entry.source);copyComplete=$false}
        $changed+=,$record
        SaveJson "$runPath/restore-files.json" $changed
        # Fresh check immediately before EACH deployment copy; rollback intent already persisted.
        FreshWindow
        try { Copy-Item -LiteralPath $entry.source -Destination $destination -Force }
        catch {
            if (Test-Path -LiteralPath $destination) { $record['partialCopySha256']=Hash $destination }
            SaveJson "$runPath/restore-files.json" $changed
            throw
        }
        if ((Hash $destination) -ne $record.deployedSha256) { throw 'Private module deployment readback differs.' }
        $record.copyComplete=$true
        SaveJson "$runPath/restore-files.json" $changed
    }
    VerifyInputs
    foreach($record in $changed) { if ((Hash $record.destination) -ne $record.deployedSha256) { throw 'Deployed file changed before launch.' } }
    $requestPath=Join-Path $runPath ('request-'+$runId+'.json')
    $request=[ordered]@{schemaVersion=1;runId=$runId;createdUtc=[DateTime]::UtcNow.ToString('O');gameExecutable=$gameExe;
        nativeDll=(Join-Path $moduleRoot 'bin/Win64_Shipping_Client/MNBL.NativeElementProbe.dll');nativeSha256=$nativeHash;hostSha256=$hostHash;
        ownerPath=(Join-Path $runPath 'owner.json');claimPath=(Join-Path $runPath 'claimed.json');resultPath=(Join-Path $runPath 'result.json')}
    SaveJson $requestPath $request
    # No parent/global environment mutation: only this exact launched child receives arming.
    FreshWindow
    $owned=Start-Process -FilePath $gameExe -WorkingDirectory $bin -ArgumentList ('/singleplayer '+$profile.modules+' /maxThreadCount 16') -WindowStyle Hidden -PassThru -Environment @{MNBL_PRIVATE_ELEMENT_PROBE_REQUEST=$requestPath}
    $ownedStart=$owned.StartTime.ToUniversalTime().Ticks
    $receipt['gameLaunched']=$true;$receipt['pid']=$owned.Id;$receipt['startTicks']=$ownedStart;$receipt['status']='running'
    VerifyOwnedIdentity $owned $ownedStart $gameExe
    SaveJson $request.ownerPath ([ordered]@{runId=$runId;pid=$owned.Id;startTicks=$ownedStart})
    SaveJson "$runPath/runtime-receipt.json" $receipt
    $deadline=[DateTime]::UtcNow.AddMinutes($TimeoutMinutes)
    while (!$owned.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Seconds 2;$owned.Refresh() }
    if (!$owned.HasExited) { $timedOut=$true;throw 'Owned private-probe process timed out.' }
    $owned.WaitForExit();$exitCode=$owned.ExitCode
    if (!(Test-Path -LiteralPath $request.resultPath)) { throw 'Owned process exited without an armed probe result.' }
    $result=Get-Content -LiteralPath $request.resultPath -Raw | ConvertFrom-Json
    VerifyResult $result $owned.Id $ownedStart
    $nativePassed=$true
    # A native success report does not certify normal shutdown or watchdog termination.
    if ($exitCode -ne 0) { throw "Native probe completed but process exit $exitCode is not verified normal (including watchdog exits)." }
    $normalExit=$true
} catch {
    $failure=$_.Exception.ToString()
} finally {
    try {
        if ($null -ne $owned) {
            VerifyOwnedIdentity $owned $ownedStart $gameExe
            if (!$owned.HasExited) {
                $forced=$true
                $owned.CloseMainWindow() | Out-Null
                if (!$owned.WaitForExit(10000)) {
                    VerifyOwnedIdentity $owned $ownedStart $gameExe
                    if (!$owned.HasExited) { $owned.Kill();$owned.WaitForExit() }
                }
            }
            $exitCode=$owned.ExitCode
        }
    } catch { $failure=([string]$failure)+"`nOwned cleanup failed: "+$_.Exception.ToString() }
    try {
        # Foreign sessions may start after our exit. Wait again before EVERY restore.
        for($i=$changed.Count-1;$i -ge 0;$i--) {
            FreshWindow
            $record=$changed[$i];$destination=SafeDestination $record.destination
            if (Test-Path -LiteralPath $destination) {
                $current=Hash $destination
                $known=@($record.deployedSha256)
                if ($record.existed) { $known+=,$record.originalSha256 }
                if ($record.Contains('partialCopySha256')) { $known+=,$record.partialCopySha256 }
                if ($known -notcontains $current) { throw 'Installed private file changed independently; backup retained, refusing overwrite.' }
            }
            if ($record.existed) {
                if ((Hash $record.backup) -ne $record.originalSha256) { throw 'Restore backup hash differs.' }
                FreshWindow
                Copy-Item -LiteralPath $record.backup -Destination $destination -Force
                if ((Hash $destination) -ne $record.originalSha256) { throw 'Original module restoration readback differs.' }
            } elseif (Test-Path -LiteralPath $destination) {
                FreshWindow
                Remove-Item -LiteralPath $destination
            }
            $record['restored']=$true
            SaveJson "$runPath/restore-files.json" $changed
        }
        $restored=$true
    } catch { $failure=([string]$failure)+"`nRestoration failed: "+$_.Exception.ToString() }
    $receipt['nativeProbePassed']=$nativePassed;$receipt['normalExitVerified']=$normalExit -and !$forced;$receipt['timedOut']=$timedOut;$receipt['forcedCleanup']=$forced;$receipt['exitCode']=$exitCode;$receipt['restored']=$restored
    $receipt['error']=$failure;$receipt['finishedUtc']=[DateTime]::UtcNow.ToString('O')
    $receipt['status']=if (!$failure -and $nativePassed -and $normalExit -and !$forced -and $restored) {'passed-private-native-probe-and-normal-exit'} else {'failed'}
    try { if (Test-Path -LiteralPath $runPath) { SaveJson "$runPath/runtime-receipt.json" $receipt } } finally { $lease.Dispose() }
}
Write-Output "Private probe receipt: $runPath/runtime-receipt.json"
if ($receipt.status -eq 'failed') { throw $failure }
