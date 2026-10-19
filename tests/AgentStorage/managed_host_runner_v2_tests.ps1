# Inert scenarios for the actual v2 runner. Process start/list/kill and CIM queries are mocks;
# only a temporary fake installation under build/ is copied and restored. Runs, receipts and the
# lease stay inside this suite, so they never mix with live runs or hold the shared lock.
# Fake PIDs are not multiples of four, so they can never name a real Windows process.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runner=Join-Path $repo 'tools/battle-scale/Test-ElementProbeInGameV2.ps1'
$helper=Join-Path $repo 'tools/battle-scale/Test-NativeExitContract.ps1'
$suite=Join-Path $repo ('build/element-probe-host-v2/inert-runner-tests/'+[guid]::NewGuid().ToString('N'))
$suiteRuns=Join-Path $suite 'runs';$suiteLease=Join-Path $suite 'native-test.lock'
New-Item -ItemType Directory -Path $suiteRuns -Force | Out-Null
$observations=@()
function Digest([string]$p) { (Get-FileHash -LiteralPath $p).Hash.ToLowerInvariant() }
function Assert($condition,[string]$message) { if (!$condition) { throw $message } }
# Real stock-build logs from run 935d4269 (PID 21268), preserved with original timestamps.
$realLogs=Join-Path $repo 'build/element-probe-host-v2/real-log-fixture-21268'
$realPins=@{'watchdog_log_21268.txt'='c4bc6b4feb69a52f71de6fa97c006e9a6f8c447bc2b4edf3b1df857b5f2db488';'rgl_log_21268.txt'='df7c3108877b8d4d1de8416633fe397aa46a5178679d8b02447643a25a3e10aa';
    'rgl_log_errors_21268.txt'='2e08a4d22e9c70a8fd563ef79eee142c023bcfa3b1cb04d01a933e3dd2078e68'}
foreach($name in $realPins.Keys) { Assert ((Digest (Join-Path $realLogs $name)) -eq $realPins[$name]) "Real-log fixture changed: $name" }
$watchTemplate=[IO.File]::ReadAllText((Join-Path $realLogs 'watchdog_log_21268.txt'))
$gameTemplate="[14:08:07.332] Final_cleanup...`n[14:08:07.332] Deleting resources...`n[14:08:07.398] Pre Finalizing Managed Interface...[14:08:07.408] OK!`n[14:08:07.408] Deleting Managed Interface...`n[14:08:07.416] There are no living managed objects. `n[14:08:07.416] Managed Interface deleted.`n"
$errorTemplate=[IO.File]::ReadAllText((Join-Path $realLogs 'rgl_log_errors_21268.txt'))
$passing=@('preflight','success-watchdog','success-zero','foreign-wait','foreign-watchdog-wait','foreign-before-restore','reused-watchdog-pid','crash-dir-dangling-junction','crash-dir-created-empty')
$noLaunch=@('preflight','copy-failure','source-change','installed-native-changed')
$stuck=@('owned-timeout','debugger-held-game','unkillable-game')
$scenarios=$passing+@('foreign-result','pid-reuse-result','bad-cleanup','nonzero-return-code','missing-return-code','v1-result','other-exit-code','missing-result',
    'copy-failure','source-change','installed-native-changed','restore-conflict','partial-restore','owned-timeout','debugger-held-game','unkillable-game','no-watchdog',
    'stale-watchdog-child','watchdog-wrong-path','watchdog-replaced-during-run','lingering-watchdog','foreign-log-pid','stale-log','out-of-order-log','missing-watchdog-log',
    'crash-marker','crash-dump-file','crash-dir-state-change','crash-dir-created-with-dump','error-log-entries','missing-error-log','missing-cleanup-milestone')
foreach($scenario in $scenarios) {
    $case=Join-Path $suite $scenario;$fakeGame=Join-Path $case 'game';$pkg=Join-Path $case 'package'
    $bin=Join-Path $fakeGame 'bin/Win64_Shipping_Client';$module=Join-Path $fakeGame 'Modules/MNBL.AgentGrowthProbe'
    $logs=Join-Path $case 'ProgramData/logs';$crashes=Join-Path $case 'ProgramData/crashes'
    # The module's bin folders do not exist yet: the runner creates them and must remove them again.
    New-Item -ItemType Directory -Path $pkg,"$bin/Watchdog",$module,$logs -Force | Out-Null
    $junctionTarget=Join-Path $case 'missing-workshop-cache'
    if ($scenario -in @('crash-dir-dangling-junction','crash-dir-state-change')) {
        New-Item -ItemType Directory -Path $junctionTarget | Out-Null
        New-Item -ItemType Junction -Path $crashes -Target $junctionTarget | Out-Null
        Remove-Item -LiteralPath $junctionTarget
    } elseif ($scenario -in @('crash-dir-created-empty','crash-dir-created-with-dump')) {
        # Absent at preflight; the (mock) watchdog creates it at startup.
    } else {
        New-Item -ItemType Directory -Path $crashes | Out-Null
        Set-Content -LiteralPath "$crashes/old-crash-before-run" -Value 'pre-existing entry'
        (Get-Item -LiteralPath "$crashes/old-crash-before-run").LastWriteTimeUtc=[DateTime]::UtcNow.AddDays(-2)
    }
    $gameExe=Join-Path $bin 'Bannerlord.exe';$native=Join-Path $bin 'TaleWorlds.Native.dll';$watchdogExe=Join-Path $bin 'Watchdog/Watchdog.exe'
    Set-Content -LiteralPath $gameExe -Value 'inert, never executable'
    Set-Content -LiteralPath $native -Value 'inert stock native fixture'
    Set-Content -LiteralPath $watchdogExe -Value 'inert watchdog fixture'
    foreach($name in @('MNBL.AgentGrowthProbe.dll','MNBL.NativeElementProbe.dll','SubModule.xml')) { Set-Content -LiteralPath (Join-Path $pkg $name) -Value ('test-package-'+$name) }
    Set-Content -LiteralPath "$module/SubModule.xml" -Value 'original-module-marker'
    $original=Digest "$module/SubModule.xml"
    $contractPath=Join-Path $case 'contract.json'
    [ordered]@{status='reviewed-build-specific-watchdog-shutdown';nativeSourceSha256=(Digest $native);patchedCandidateSha256=(Digest $native);
        expectedWatchdogExitCode=-1;watchdogPath=$watchdogExe;watchdogSha256=(Digest $watchdogExe)} | ConvertTo-Json | Set-Content -LiteralPath $contractPath
    $profile=[ordered]@{schemaVersion=2;status='offline-built-private-element-probe-v2';gameRoot=$fakeGame;
        sources=@(@{path=$runner;sha256=(Digest $runner)},@{path=$helper;sha256=(Digest $helper)});
        providers=@(@{path=$gameExe;sha256=(Digest $gameExe)},@{path=$native;sha256=(Digest $native)},@{path=$watchdogExe;sha256=(Digest $watchdogExe)});
        files=@('MNBL.AgentGrowthProbe.dll','MNBL.NativeElementProbe.dll','SubModule.xml') | ForEach-Object {@{name=$_;sha256=(Digest (Join-Path $pkg $_))}};
        environmentKey='MNBL_PRIVATE_ELEMENT_PROBE_REQUEST';modules='_MODULES_*Native*SandBoxCore*CustomBattle*MNBL.AgentGrowthProbe*_MODULES_';
        sourceSha256=(Digest $native);patchedSha256=(Digest $native);exitContract=@{path=$contractPath;sha256=(Digest $contractPath)}}
    $profilePath=Join-Path $pkg 'element-probe-profile.json';$profile | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $profilePath
    $global:V2_launches=0;$global:V2_copies=0;$global:V2_gameChecks=0;$global:V2_lastMutationCheck=-1;$global:V2_unsafeMutation=$false;$global:V2_kills=0;$global:V2_waits=0;$global:V2_lastRun=$null
    $global:V2_busy=if($scenario -in @('foreign-wait','foreign-watchdog-wait')){3}else{0};$global:V2_busyName=if($scenario -eq 'foreign-watchdog-wait'){'Watchdog'}else{'Bannerlord.Native'}
    $global:V2_active=$null;$global:V2_lastNoGames=$true;$global:V2_stops=@();$global:V2_watchdogAlive=$false;$global:V2_start=$null
    function Get-Process {
        param($Id,$ErrorAction)
        if ($null -ne $Id) {
            # Handle-based lookup used only to stop the recorded owned watchdog instance.
            Assert ($Id -eq 2345) "Process lookup for a non-owned PID: $Id"
            if (!$global:V2_watchdogAlive) { return }
            $p=[pscustomobject]@{Id=2345;ProcessName='Watchdog';StartTime=$global:V2_start.AddSeconds(1);Handle=[IntPtr]1}
            $p | Add-Member ScriptMethod Kill { $global:V2_stops+=,2345;$global:V2_watchdogAlive=$false; if ($scenario -eq 'debugger-held-game') { $global:V2_active.HasExited=$true } }
            $p | Add-Member ScriptMethod WaitForExit { param($milliseconds) return $true }
            return $p
        }
        ++$global:V2_gameChecks
        if ($global:V2_busy -gt 0) {$global:V2_lastNoGames=$false;--$global:V2_busy;return [pscustomobject]@{ProcessName=$global:V2_busyName;Id=909}}
        $global:V2_lastNoGames=$true
    }
    function Stop-Process { throw 'The runner must not stop processes by bare PID.' }
    function Start-Sleep {
        param($Seconds,$Milliseconds)
        ++$global:V2_waits
        if ($scenario -in $stuck -and $null -ne $global:V2_active) {
            # Advance ONLY the runner-local deadline via its script scope, rather than sleeping.
            Set-Variable -Name deadline -Value ([datetime]::UtcNow.AddSeconds(-1)) -Scope 1
        }
    }
    function Copy-Item {
        param($LiteralPath,$Destination,[switch]$Force)
        if ([IO.Path]::GetFullPath($Destination).StartsWith($module,[StringComparison]::OrdinalIgnoreCase)) {
            if ($global:V2_gameChecks -le $global:V2_lastMutationCheck -or !$global:V2_lastNoGames) {$global:V2_unsafeMutation=$true}
            $global:V2_lastMutationCheck=$global:V2_gameChecks
            if ($LiteralPath.StartsWith($pkg,[StringComparison]::OrdinalIgnoreCase)) {
                ++$global:V2_copies
                if ($scenario -eq 'copy-failure' -and $global:V2_copies -eq 2) { Set-Content -LiteralPath $Destination -Value 'partial-copy';throw 'injected partial copy failure' }
            }
        }
        Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force
    }
    function Get-CimInstance {
        param($ClassName,$Filter,$ErrorAction)
        Assert ($ClassName -eq 'Win32_Process') 'Unexpected CIM class'
        if ($null -eq $global:V2_start) { return }
        $row=[pscustomobject]@{Name='Watchdog.exe';ProcessId=2345;ParentProcessId=78787;CreationDate=$global:V2_start.AddSeconds(1).ToLocalTime();ExecutablePath=$watchdogExe}
        if ($scenario -eq 'stale-watchdog-child') { $row.CreationDate=$global:V2_start.AddHours(-1).ToLocalTime() }
        if ($scenario -eq 'watchdog-wrong-path') { $row.ExecutablePath=$gameExe }
        if ($Filter -eq 'ParentProcessId=78787') { if ($scenario -ne 'no-watchdog') { return $row } ; return }
        if ($Filter -eq 'ProcessId=2345') {
            if ($global:V2_watchdogAlive) { return $row }
            # The PID now names a different, later process instance: not ours, never touched.
            if ($scenario -eq 'reused-watchdog-pid' -and $global:V2_active.HasExited) { $row.CreationDate=$global:V2_start.AddMinutes(5).ToLocalTime(); return $row }
            return
        }
        throw "Unexpected CIM filter: $Filter"
    }
    function Start-ArmedGame {
        param([string]$FilePath,[string]$WorkingDirectory,[string]$Arguments,[string]$Key,[string]$Value)
        ++$global:V2_launches
        Assert ($global:V2_gameChecks -gt $global:V2_lastMutationCheck -and $global:V2_lastNoGames) 'No fresh process check before launch'
        Assert ($Key -eq 'MNBL_PRIVATE_ELEMENT_PROBE_REQUEST') 'Child arming key differs'
        Assert ($FilePath -eq $gameExe -and $WorkingDirectory -eq $bin) 'Unexpected process target'
        Assert ($Arguments -eq '/singleplayer _MODULES_*Native*SandBoxCore*CustomBattle*MNBL.AgentGrowthProbe*_MODULES_ /maxThreadCount 16') 'Unexpected arguments'
        $request=Get-Content -LiteralPath $Value -Raw | ConvertFrom-Json
        Assert (([DateTime]::UtcNow-[datetime]::Parse($request.createdUtc,$null,'RoundtripKind')).TotalMinutes -lt 1) 'Request was created long before launch'
        $global:V2_lastRun=Split-Path $request.resultPath
        $start=[datetime]::UtcNow;$global:V2_start=$start
        $exit=switch($scenario){'success-zero'{0}'other-exit-code'{5}default{-1}}
        # Alive for the identity check and two poll iterations, so the runner observes its watchdog.
        $process=[pscustomobject]@{Id=78787;StartTime=$start;MainModule=[pscustomobject]@{FileName=$FilePath};HasExited=$false;ExitCode=$exit;Refreshes=0}
        $process | Add-Member ScriptMethod Refresh { if ($scenario -notin $stuck) { ++$this.Refreshes; if ($this.Refreshes -ge 3) { $this.HasExited=$true } } }
        $process | Add-Member ScriptMethod WaitForExit {param($milliseconds) return $this.HasExited}
        $process | Add-Member ScriptMethod CloseMainWindow {return $false}
        $process | Add-Member ScriptMethod Kill { ++$global:V2_kills; if ($scenario -eq 'owned-timeout') { $this.HasExited=$true } }
        $global:V2_active=$process
        $global:V2_watchdogAlive=$scenario -in @('lingering-watchdog','debugger-held-game','unkillable-game')
        if ($scenario -ne 'missing-result') {
            $r=[ordered]@{schemaVersion=2;hostVersion='managed-v2';runId=$request.runId;pid=78787;startTicks=$start.ToUniversalTime().Ticks;
                nativeSha256=$request.nativeSha256;hostSha256=$request.hostSha256;status='passed-awaiting-shutdown-verification';quitRequested=$true;mainMenuReady=$true;missionAbsent=$true;menuWaitSeconds=3.5;nativeCode=0;nativeReturnCode=0;error=$null;
                native=[ordered]@{abi_size=712;abi_version=1;status=1;stage=8;capacity=90;pointer_checks=600;header_checks=450;allocation_attempts=21;allocations=20;releases=20;constructor_attempts=488;
                    allocation_failure_checked=1;constructor_failure_checked=1;exhaustion_checked=1;reuse_checked=1;constructed=@(120,97,90,90,90);destroyed=@(120,97,90,90,90);ErrorText=''}}
            switch($scenario) {
                'foreign-result' {$r.runId=[guid]::NewGuid().ToString('D')}
                'pid-reuse-result' {$r.startTicks++}
                'bad-cleanup' {$r.native.destroyed[4]=89}
                'nonzero-return-code' {$r.nativeReturnCode=5}
                'missing-return-code' {$r.nativeReturnCode=$null}
                'v1-result' {$r.schemaVersion=1;$r.status='passed-awaiting-normal-process-exit'}
            }
            $r | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $request.resultPath
        }
        $watchText=$watchTemplate.Replace('21268','78787');$gameText=$gameTemplate;$errorText=$errorTemplate
        switch($scenario) {
            'foreign-log-pid' {$watchText=$watchText.Replace('78787','9999')}
            'out-of-order-log' {$watchText=$watchText.Replace("Debuggee message: #TW#-kp`r`n[DebuggerConfiguration] -kp `r`nEXIT_PROCESS_DEBUG_EVENT occurred. Detaching...","EXIT_PROCESS_DEBUG_EVENT occurred. Detaching...`r`nDebuggee message: #TW#-kp`r`n[DebuggerConfiguration] -kp ")
                Assert ($watchText.IndexOf('Detaching') -lt $watchText.IndexOf('#TW#-kp')) 'Out-of-order fixture was not reordered'}
            'crash-marker' {$watchText+="EXCEPTION_DEBUG_EVENT occurred.`r`n"}
            'error-log-entries' {$errorText+="[14:08:00.000] Unhandled managed exception in module`r`n"}
            'missing-cleanup-milestone' {$gameText=$gameText.Replace('Managed Interface deleted.','unfinished')}
        }
        if ($scenario -ne 'missing-watchdog-log') { Set-Content -LiteralPath "$logs/watchdog_log_78787.txt" -Value $watchText -NoNewline }
        Set-Content -LiteralPath "$logs/rgl_log_78787.txt" -Value $gameText -NoNewline
        if ($scenario -ne 'missing-error-log') { Set-Content -LiteralPath "$logs/rgl_log_errors_78787.txt" -Value $errorText -NoNewline }
        if ($scenario -eq 'stale-log') { (Get-Item -LiteralPath "$logs/watchdog_log_78787.txt").LastWriteTimeUtc=$start.AddDays(-1) }
        if ($scenario -eq 'crash-dump-file') { Set-Content -LiteralPath "$crashes/2026-10-17_dump" -Value 'new crash entry' }
        if ($scenario -eq 'crash-dir-state-change') { New-Item -ItemType Directory -Path $junctionTarget | Out-Null }
        if ($scenario -in @('crash-dir-created-empty','crash-dir-created-with-dump')) { New-Item -ItemType Directory -Path $crashes | Out-Null }
        if ($scenario -eq 'crash-dir-created-with-dump') { Set-Content -LiteralPath "$crashes/2026-10-17_dump" -Value 'new crash entry' }
        if ($scenario -eq 'watchdog-replaced-during-run') { Set-Content -LiteralPath $watchdogExe -Value 'replaced watchdog' }
        if ($scenario -eq 'restore-conflict') {Set-Content -LiteralPath "$module/SubModule.xml" -Value 'independent-foreign-edit'}
        if ($scenario -eq 'partial-restore') {Set-Content -LiteralPath (Join-Path $global:V2_lastRun 'backup-0') -Value 'corrupted backup'}
        if ($scenario -eq 'foreign-before-restore') {$global:V2_busy=3}
        return $process
    }
    if ($scenario -eq 'source-change') { Add-Content -LiteralPath "$pkg/SubModule.xml" -Value 'changed after profile' }
    if ($scenario -eq 'installed-native-changed') { Add-Content -LiteralPath $native -Value 'patched after profile' }
    $beforeEnv=[Environment]::GetEnvironmentVariable('MNBL_PRIVATE_ELEMENT_PROBE_REQUEST','Process')
    $failed=$false;$message=""
    try {
        if ($scenario -eq 'preflight') { & $runner -Package $pkg -GameRoot $fakeGame -LogRoot $logs -RunRoot $suiteRuns -LeasePath $suiteLease -PreflightOnly | Out-Null }
        else { & $runner -Package $pkg -GameRoot $fakeGame -LogRoot $logs -RunRoot $suiteRuns -LeasePath $suiteLease -Execute -ReviewedPackageSha256 (Digest $profilePath) -TimeoutMinutes 1 | Out-Null }
    } catch { $failed=$true;$message=$_.Exception.Message }
    $shouldPass=$scenario -in $passing
    Assert ($failed -ne $shouldPass) "Unexpected scenario outcome: $scenario $message"
    Assert (!$global:V2_unsafeMutation) "Mutation without fresh no-game check: $scenario"
    Assert ([Environment]::GetEnvironmentVariable('MNBL_PRIVATE_ELEMENT_PROBE_REQUEST','Process') -ceq $beforeEnv) 'Parent environment changed'
    # The lease is always released, even when restore is skipped.
    $probeLease=[IO.File]::Open($suiteLease,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);$probeLease.Dispose()
    $hostPath="$module/bin/Win64_Shipping_Client/MNBL.AgentGrowthProbe.dll";$nativePath="$module/bin/Win64_Shipping_Client/MNBL.NativeElementProbe.dll"
    if ($scenario -eq 'restore-conflict') { Assert ((Get-Content -LiteralPath "$module/SubModule.xml" -Raw).Contains('independent-foreign-edit')) 'Foreign modification overwritten' }
    elseif ($scenario -eq 'partial-restore') {
        Assert ((Get-Content -LiteralPath "$module/SubModule.xml" -Raw).Contains('test-package-SubModule.xml')) 'Corrupt backup was restored over the module'
        Assert (!(Test-Path -LiteralPath $hostPath)) 'Later deployment not removed before partial-restore stop'
        # Windows PowerShell emits a top-level JSON array as one object; unroll it.
        $records=@(Get-Content -LiteralPath (Join-Path $global:V2_lastRun 'restore-files.json') -Raw | ConvertFrom-Json | ForEach-Object { $_ })
        Assert ($records.Count -eq 3 -and $records[1].restored -and $records[2].restored -and !($records[0].PSObject.Properties.Name -contains 'restored')) 'Partial restore ledger differs'
    } elseif ($scenario -eq 'unkillable-game') {
        Assert ((Test-Path -LiteralPath $hostPath) -and (Test-Path -LiteralPath $nativePath)) 'Restore ran while the owned game was still running'
        $records=@(Get-Content -LiteralPath (Join-Path $global:V2_lastRun 'restore-files.json') -Raw | ConvertFrom-Json | ForEach-Object { $_ })
        Assert ($records.Count -eq 3 -and (Test-Path -LiteralPath $records[0].backup)) 'Restore intent/backup not retained'
    } else {
        Assert ((Digest "$module/SubModule.xml") -eq $original) "Original module not restored: $scenario"
        Assert (!(Test-Path -LiteralPath $hostPath) -and !(Test-Path -LiteralPath $nativePath)) "Deployed DLLs not removed: $scenario"
        Assert (!(Test-Path -LiteralPath "$module/bin")) "Created directories not removed: $scenario"
    }
    if ($scenario -in $noLaunch) {Assert ($global:V2_launches -eq 0) "Launched after failed preflight/deployment: $scenario"} else {Assert ($global:V2_launches -eq 1) "Expected one launch: $scenario"}
    if ($scenario -in @('foreign-wait','foreign-watchdog-wait')) {Assert ($global:V2_waits -ge 2) "Existing foreign session did not delay deployment: $scenario"}
    if ($scenario -eq 'foreign-before-restore') {Assert ($global:V2_waits -ge 3) 'New foreign game did not delay restore'}
    if ($scenario -in $stuck) {Assert ($global:V2_kills -eq 1) "Owned game should be killed exactly once: $scenario"} else {Assert ($global:V2_kills -eq 0) "Unexpected kill: $scenario"}
    $expectedStops=if ($scenario -in @('lingering-watchdog','debugger-held-game','unkillable-game')) {1} else {0}
    Assert (@($global:V2_stops).Count -eq $expectedStops) "Owned watchdog stop count differs: $scenario"
    $runtime=$null
    if ($global:V2_lastRun) {$runtime=Get-Content -LiteralPath "$global:V2_lastRun/runtime-receipt.json" -Raw | ConvertFrom-Json}
    if ($null -ne $runtime) {
        Assert ($runtime.allNativeDestructorsVerified -eq $false) 'Receipt claimed native destructor certification'
        Assert ($runtime.liveCommandsVerified -eq $false -and $runtime.sharedLease -eq $false -and $runtime.cleanHost -eq $false) 'Substituted commands not detected'
        Assert ((@($runtime.substitutedCommands) -join ',') -eq 'Get-Process,Get-CimInstance,Copy-Item,Start-Sleep') "Substituted command list differs: $(@($runtime.substitutedCommands) -join ',')"
        Assert ($runtime.commandOrigins.'Get-FileHash'.type -in @('Cmdlet','Function') -and @($runtime.substitutedCommands) -notcontains 'Get-FileHash') 'Built-in Get-FileHash treated as substituted'
        Assert ($runtime.runRoot -eq [IO.Path]::GetFullPath($suiteRuns) -and $runtime.logRoot -eq [IO.Path]::GetFullPath($logs)) 'Run/log roots not recorded'
        if ($shouldPass) { Assert ($runtime.status -eq 'passed-inert-substituted-commands' -and $runtime.nativeProbePassed -and $runtime.verifiedShutdown -and $runtime.restored) "Passing receipt incomplete: $scenario" }
        else { Assert ($runtime.status -eq 'failed' -and !$runtime.verifiedShutdown -and !$runtime.verifiedShutdownProtocol -and !$runtime.normalExitVerified) "Failing receipt claimed success: $scenario" }
    }
    switch($scenario) {
        'success-watchdog' {
            Assert ($runtime.verifiedShutdownProtocol -and !$runtime.normalExitVerified -and $runtime.exitCode -eq -1) 'Watchdog exit misreported'
            Assert ($runtime.shutdownVerification.kind -eq 'reviewed-watchdog-kp-command' -and @($runtime.shutdownVerification.logs).Count -eq 2) 'Watchdog protocol logs not retained'
            Assert (@($runtime.ownedWatchdogs).Count -eq 1 -and $runtime.ownedWatchdogs[0].pid -eq 2345 -and $runtime.ownedWatchdogs[0].parentPid -eq 78787 -and $runtime.ownedWatchdogs[0].sha256 -eq (Digest $watchdogExe)) 'Owned watchdog identity not recorded'
            Assert ($null -ne $runtime.errorLog -and $runtime.crashDirectory.state -eq 'listable' -and @($runtime.removedDirectories).Count -eq 2) 'Error log, crash state or directory cleanup not recorded'
            foreach($log in $runtime.shutdownVerification.logs) { Assert ((Digest $log.path) -eq $log.sha256) 'Retained log hash differs' }
        }
        'success-zero' { Assert ($runtime.normalExitVerified -and !$runtime.verifiedShutdownProtocol -and $runtime.exitCode -eq 0 -and $runtime.shutdownVerification.kind -eq 'process-exit-zero') 'Zero exit misreported' }
        'crash-dir-dangling-junction' { Assert ($runtime.crashDirectory.state -eq 'reparse-target-unavailable') 'Dangling junction state not recorded' }
        'crash-dir-created-empty' { Assert ($runtime.crashDirectory.state -eq 'absent') 'Absent crash directory state not recorded' }
        'owned-timeout' { Assert ($runtime.timedOut -and $runtime.forcedCleanup -and $runtime.restored) 'Timeout not recorded as forced' }
        'debugger-held-game' { Assert ($runtime.timedOut -and $runtime.forcedCleanup -and $runtime.restored -and !$runtime.ownedStillRunning) 'Debugger-held game not released through its owned watchdog' }
        'unkillable-game' { Assert ($runtime.ownedStillRunning -and !$runtime.restored -and $runtime.error.Contains('Restore skipped')) 'Unkillable game not reported' }
        'lingering-watchdog' { Assert ($runtime.forcedCleanup -and @($runtime.stoppedOwnedWatchdogs).Count -eq 1) 'Lingering watchdog cleanup not recorded' }
        'nonzero-return-code' { Assert ($runtime.nativeProbePassed -eq $false) 'Nonzero engine return code accepted' }
    }
    $observations+=,[ordered]@{scenario=$scenario;expectedFailure=!$shouldPass;observedFailure=$failed;rejectedReason=$message;launches=$global:V2_launches;freshGameChecks=$global:V2_gameChecks;
        ownedKills=$global:V2_kills;ownedWatchdogStops=@($global:V2_stops).Count;runtimeReceipt=$global:V2_lastRun}
}

# Replay of the preserved real stock-build shutdown logs (run 935d4269, PID 21268) through the
# shared verifier with the new stock contract. The engine return code and the watchdog PID are
# fixture values; that run's host did not record them.
. $helper
$contractPath=Join-Path $repo 'evidence/engine-extension/native-original-watchdog-shutdown-contract.json'
$stockProfile=[pscustomobject]@{sourceSha256='2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a';patchedSha256='2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a';
    exitContract=[pscustomobject]@{path=$contractPath;sha256=(Digest $contractPath)}}
$gameRoot='D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord'
$replay=Join-Path $suite 'real-log-replay';New-Item -ItemType Directory -Path $replay | Out-Null
function Start-Sleep { param($Seconds,$Milliseconds) }
$answer=Confirm-NativeExitContract -Profile $stockProfile -Probe ([pscustomobject]@{nativeReturnCode=0}) -ExitCode -1 -GameProcessId 21268 -OwnedWatchdogIds @(1) `
    -StartedUtc ([datetime]::Parse('2026-10-17T19:07:56.3329772Z',$null,'RoundtripKind')) -GameRoot $gameRoot -LogRoot $realLogs -RunPath $replay
Assert ($answer.verified -and $answer.kind -eq 'reviewed-watchdog-kp-command' -and $answer.logs.Count -eq 2) 'Real stock shutdown logs were not accepted'
$missingRejected=$false
try { Confirm-NativeExitContract -Profile $stockProfile -Probe ([pscustomobject]@{nativeReturnCode=0}) -ExitCode -1 -GameProcessId 556 -OwnedWatchdogIds @(1) `
    -StartedUtc ([datetime]::Parse('2026-10-17T19:01:58.5527711Z',$null,'RoundtripKind')) -GameRoot $gameRoot -LogRoot $realLogs -RunPath $replay | Out-Null } catch { $missingRejected=$true }
Assert $missingRejected 'PID 556, whose watchdog log was never preserved, was accepted'
$replayRecord=[ordered]@{source='run 935d4269 / PID 21268, preserved copy of ProgramData logs';fixture=$realLogs;pins=$realPins;accepted=$true;retainedLogs=$answer.logs;
    pid556MissingWatchdogLogRejected=$true;fixtureValues=@('nativeReturnCode=0','ownedWatchdogIds=[1]')}

$receipt=[ordered]@{status='passed-inert-private-probe-runner-v2-tests';sources=@{runner=(Digest $runner);helper=(Digest $helper);tests=(Digest $PSCommandPath);contract=(Digest $contractPath)};
    scenarioCount=$observations.Count;cases=$observations;realLogReplay=$replayRecord;powershell=$PSVersionTable.PSVersion.ToString();suite=$suite;
    limitations=@('All process start/list/kill and CIM queries are inert mocks; only temporary fake-installation files are copied/restored.',
                  'Mocked runs report passed-inert-substituted-commands and liveCommandsVerified=false; no mocked receipt can read as a live pass.',
                  'The real-log replay checks the verifier on genuine stock-build logs; it is not a new run and its return code and watchdog PID are fixtures.',
                  'No game launch, positive native binding, Safe Mode dialog handling or real process cleanup occurred.');gameLaunched=$false;installedFilesChanged=$false}
$receipt | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath "$repo/evidence/engine-extension/managed-element-runner-v2-offline-tests.json" -Encoding utf8
Write-Output "PASS $($observations.Count) actual-runner v2 inert scenarios + real stock-log replay."
