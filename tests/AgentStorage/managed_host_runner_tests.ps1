$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runner=Join-Path $repo 'tools/battle-scale/Test-ElementProbeInGame.ps1'
$suite=Join-Path $repo ('build/element-probe-host/inert-runner-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $suite | Out-Null
$observations=@()
function Digest([string]$p) { (Get-FileHash -LiteralPath $p).Hash.ToLowerInvariant() }
function Assert($condition,[string]$message) { if (!$condition) { throw $message } }
foreach($scenario in @('preflight','success','foreign-result','pid-reuse-result','bad-cleanup','nonzero-exit','missing-result','copy-failure','source-change','restore-conflict','foreign-wait','foreign-before-restore','owned-timeout')) {
    $case=Join-Path $suite $scenario;$fakeGame=Join-Path $case 'game';$pkg=Join-Path $case 'package'
    $module=Join-Path $fakeGame 'Modules/MNBL.AgentGrowthProbe'
    New-Item -ItemType Directory -Path $pkg,"$fakeGame/bin/Win64_Shipping_Client","$module/bin/Win64_Shipping_Client" -Force | Out-Null
    $gameExe=Join-Path $fakeGame 'bin/Win64_Shipping_Client/Bannerlord.exe'
    Set-Content -LiteralPath $gameExe -Value 'inert, never executable'
    foreach($name in @('MNBL.AgentGrowthProbe.dll','MNBL.NativeElementProbe.dll','SubModule.xml')) { Set-Content -LiteralPath (Join-Path $pkg $name) -Value ('test-package-'+$name) }
    Set-Content -LiteralPath "$module/SubModule.xml" -Value 'original-module-marker'
    $original=Digest "$module/SubModule.xml"
    $profile=[ordered]@{schemaVersion=1;status='offline-built-private-element-probe';gameRoot=$fakeGame;
        sources=@(@{path=$runner;sha256=(Digest $runner)});providers=@(@{path=$gameExe;sha256=(Digest $gameExe)});
        files=@('MNBL.AgentGrowthProbe.dll','MNBL.NativeElementProbe.dll','SubModule.xml') | ForEach-Object {@{name=$_;sha256=(Digest (Join-Path $pkg $_))}};
        environmentKey='MNBL_PRIVATE_ELEMENT_PROBE_REQUEST';modules='_MODULES_*Native*SandBoxCore*CustomBattle*MNBL.AgentGrowthProbe*_MODULES_'}
    $profilePath=Join-Path $pkg 'element-probe-profile.json';$profile | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $profilePath
    $global:ElementRunnerTest_launches=0;$global:ElementRunnerTest_copies=0;$global:ElementRunnerTest_gameChecks=0;$global:ElementRunnerTest_lastMutationCheck=-1;$global:ElementRunnerTest_unsafeMutation=$false;$global:ElementRunnerTest_kills=0;$global:ElementRunnerTest_waits=0;$global:ElementRunnerTest_lastRun=$null
    $global:ElementRunnerTest_busy=if($scenario -eq 'foreign-wait'){3}else{0};$global:ElementRunnerTest_active=$null
    function Get-Process {
        param($ErrorAction)
        ++$global:ElementRunnerTest_gameChecks
        if ($global:ElementRunnerTest_busy -gt 0) {$global:ElementRunnerTest_lastNoGames=$false;--$global:ElementRunnerTest_busy;return [pscustomobject]@{ProcessName='Bannerlord.Foreign';Id=909}}
        $global:ElementRunnerTest_lastNoGames=$true
    }
    function Start-Sleep {
        param($Seconds)
        ++$global:ElementRunnerTest_waits
        if ($scenario -eq 'owned-timeout' -and $null -ne $global:ElementRunnerTest_active) {
            # Advance ONLY the fixture's runner-local deadline via its parent
            # script scope, rather than sleeping a real minute.
            Set-Variable -Name deadline -Value ([datetime]::UtcNow.AddSeconds(-1)) -Scope 1
        }
    }
    function Copy-Item {
        param($LiteralPath,$Destination,[switch]$Force)
        if ([IO.Path]::GetFullPath($Destination).StartsWith($module,[StringComparison]::OrdinalIgnoreCase)) {
            if ($global:ElementRunnerTest_gameChecks -le $global:ElementRunnerTest_lastMutationCheck -or !$global:ElementRunnerTest_lastNoGames) {$global:ElementRunnerTest_unsafeMutation=$true}
            $global:ElementRunnerTest_lastMutationCheck=$global:ElementRunnerTest_gameChecks
            if ($LiteralPath.StartsWith($pkg,[StringComparison]::OrdinalIgnoreCase)) {
                ++$global:ElementRunnerTest_copies
                if ($scenario -eq 'copy-failure' -and $global:ElementRunnerTest_copies -eq 2) { Set-Content -LiteralPath $Destination -Value 'partial-copy';throw 'injected partial copy failure' }
            }
        }
        Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force
    }
    function Start-Process {
        param($FilePath,$WorkingDirectory,$ArgumentList,$WindowStyle,[switch]$PassThru,$Environment)
        ++$global:ElementRunnerTest_launches
        Assert ($global:ElementRunnerTest_gameChecks -gt $global:ElementRunnerTest_lastMutationCheck) 'No fresh process check before launch'
        Assert ($Environment.Keys.Count -eq 1 -and $Environment.ContainsKey('MNBL_PRIVATE_ELEMENT_PROBE_REQUEST')) 'Child arming environment missing'
        Assert ($FilePath -eq $gameExe) 'Unexpected process target'
        $request=Get-Content -LiteralPath $Environment.MNBL_PRIVATE_ELEMENT_PROBE_REQUEST -Raw | ConvertFrom-Json
        $global:ElementRunnerTest_lastRun=Split-Path $request.resultPath
        $start=[datetime]::UtcNow
        $exit=if($scenario -eq 'nonzero-exit'){-1}else{0}
        $process=[pscustomobject]@{Id=78787;StartTime=$start;MainModule=[pscustomobject]@{FileName=$FilePath};HasExited=($scenario -ne 'owned-timeout');ExitCode=$exit}
        $process | Add-Member ScriptMethod Refresh { }
        $process | Add-Member ScriptMethod WaitForExit {param($milliseconds) return $this.HasExited}
        $process | Add-Member ScriptMethod CloseMainWindow {return $false}
        $process | Add-Member ScriptMethod Kill {$this.HasExited=$true;++$global:ElementRunnerTest_kills}
        $global:ElementRunnerTest_active=$process
        if ($scenario -ne 'missing-result') {
            $r=[ordered]@{schemaVersion=1;runId=$request.runId;pid=78787;startTicks=$start.ToUniversalTime().Ticks;
                nativeSha256=$request.nativeSha256;hostSha256=$request.hostSha256;status='passed-awaiting-normal-process-exit';quitRequested=$true;mainMenuReady=$true;missionAbsent=$true;menuWaitSeconds=3.5;nativeCode=0;error=$null;
                native=[ordered]@{abi_size=712;abi_version=1;status=1;stage=8;capacity=90;pointer_checks=600;header_checks=450;allocation_attempts=21;allocations=20;releases=20;constructor_attempts=488;
                    allocation_failure_checked=1;constructor_failure_checked=1;exhaustion_checked=1;reuse_checked=1;constructed=@(120,97,90,90,90);destroyed=@(120,97,90,90,90);ErrorText=''}}
            if ($scenario -eq 'foreign-result') {$r.runId=[guid]::NewGuid().ToString('D')}
            if ($scenario -eq 'pid-reuse-result') {$r.startTicks++}
            if ($scenario -eq 'bad-cleanup') {$r.native.destroyed[4]=89}
            $r | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $request.resultPath
        }
        if ($scenario -eq 'restore-conflict') {Set-Content -LiteralPath "$module/SubModule.xml" -Value 'independent-foreign-edit'}
        if ($scenario -eq 'foreign-before-restore') {$global:ElementRunnerTest_busy=3}
        return $process
    }
    if ($scenario -eq 'source-change') { Add-Content -LiteralPath "$pkg/SubModule.xml" -Value 'changed after profile' }
    $beforeEnv=[Environment]::GetEnvironmentVariable('MNBL_PRIVATE_ELEMENT_PROBE_REQUEST','Process')
    $failed=$false;$message=""
    try {
        if ($scenario -eq 'preflight') { & $runner -Package $pkg -GameRoot $fakeGame -PreflightOnly | Out-Null }
        else { & $runner -Package $pkg -GameRoot $fakeGame -Execute -ReviewedPackageSha256 (Digest $profilePath) -TimeoutMinutes 1 | Out-Null }
    } catch { $failed=$true;$message=$_.Exception.Message }
    $shouldPass=$scenario -in @('preflight','success','foreign-wait','foreign-before-restore')
    Assert ($failed -ne $shouldPass) "Unexpected scenario outcome: $scenario $message"
    Assert (!$global:ElementRunnerTest_unsafeMutation) "Mutation without fresh no-game check: $scenario"
    Assert ([Environment]::GetEnvironmentVariable('MNBL_PRIVATE_ELEMENT_PROBE_REQUEST','Process') -ceq $beforeEnv) 'Parent environment changed'
    if ($scenario -ne 'restore-conflict') {
        Assert ((Digest "$module/SubModule.xml") -eq $original) "Original module not restored: $scenario"
        Assert (!(Test-Path -LiteralPath "$module/bin/Win64_Shipping_Client/MNBL.AgentGrowthProbe.dll")) "New host not removed: $scenario"
        Assert (!(Test-Path -LiteralPath "$module/bin/Win64_Shipping_Client/MNBL.NativeElementProbe.dll")) "New native DLL not removed: $scenario"
    } else {Assert ((Get-Content -LiteralPath "$module/SubModule.xml" -Raw).Contains('independent-foreign-edit')) 'Foreign modification overwritten'}
    if ($scenario -in @('preflight','copy-failure','source-change')) {Assert ($global:ElementRunnerTest_launches -eq 0) 'Launched after failed preflight/deployment'}
    if ($scenario -eq 'foreign-wait') {Assert ($global:ElementRunnerTest_waits -ge 2) 'Existing foreign game did not delay deployment'}
    if ($scenario -eq 'foreign-before-restore') {Assert ($global:ElementRunnerTest_waits -ge 3) 'New foreign game did not delay restore'}
    if ($scenario -eq 'owned-timeout') {Assert ($global:ElementRunnerTest_kills -eq 1) 'Only owned process should be killed'}
    $runtime=$null
    if ($global:ElementRunnerTest_lastRun) {$runtime=Get-Content -LiteralPath "$global:ElementRunnerTest_lastRun/runtime-receipt.json" -Raw | ConvertFrom-Json}
    if ($scenario -eq 'nonzero-exit') {Assert ($runtime.nativeProbePassed -and !$runtime.normalExitVerified -and $runtime.status -eq 'failed') 'Watchdog/nonzero exit invented success'}
    if ($scenario -eq 'owned-timeout') {Assert ($runtime.timedOut -and $runtime.forcedCleanup -and !$runtime.normalExitVerified) 'Timeout invented normal exit'}
    $observations+=,[ordered]@{scenario=$scenario;expectedFailure=!$shouldPass;observedFailure=$failed;launches=$global:ElementRunnerTest_launches;freshGameChecks=$global:ElementRunnerTest_gameChecks;ownedKills=$global:ElementRunnerTest_kills;runtimeReceipt=$global:ElementRunnerTest_lastRun}
}
$receipt=[ordered]@{status='passed-inert-private-probe-runner-tests';sources=@{runner=(Digest $runner);tests=(Digest $PSCommandPath)};cases=$observations;
    limitations=@('All Start-Process/Get-Process calls are inert mocks; only temporary workspace fake-installation files are copied/restored.','No actual game launch, native positive binding or real process cleanup occurred.');gameLaunched=$false;installedFilesChanged=$false}
$receipt | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath "$repo/evidence/engine-extension/managed-element-runner-offline-tests.json" -Encoding utf8
Write-Output "PASS $($observations.Count) actual-runner inert scenarios; child environment, process identity, result counters, restore and timeout separation."
