# v2 private element probe runner. Runs under Windows PowerShell 5.1+: the game
# is started through ProcessStartInfo so only that exact child receives arming.
# Default invocation is read-only preflight. Execute requires an exact reviewed profile hash.
# Shutdown is accepted either as exit 0 (normalExitVerified) or as the reviewed stock
# watchdog kp protocol (verifiedShutdownProtocol); the two are reported separately.
param(
    [Parameter(Mandatory=$true)][string]$Package,
    [string]$GameRoot='D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord',
    [ValidateRange(1,30)][int]$TimeoutMinutes=10,
    [switch]$PreflightOnly,
    [switch]$Execute,
    [string]$ReviewedPackageSha256,
    [string]$LogRoot=(Join-Path $env:ProgramData 'Mount and Blade II Bannerlord/logs'),
    # Defaults are set below: Windows PowerShell -File leaves $PSScriptRoot empty in param defaults.
    [string]$RunRoot,
    # Shared with other MNBL runners and the procedural-world session; only inert tests change it.
    [string]$LeasePath
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if ($PreflightOnly -and $Execute) { throw 'PreflightOnly and Execute are mutually exclusive.' }
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$RunRoot) { $RunRoot=Join-Path $repo 'build/element-probe-host-v2/runs' }
if (!$LeasePath) { $LeasePath=Join-Path $repo 'build/battle-scale/native-test.lock' }
$packagePath=[IO.Path]::GetFullPath($Package)
$gamePath=[IO.Path]::GetFullPath($GameRoot)
$logPath=[IO.Path]::GetFullPath($LogRoot)
$runRootPath=[IO.Path]::GetFullPath($RunRoot)
$crashPath=[IO.Path]::GetFullPath((Join-Path (Split-Path $logPath -Parent) 'crashes'))
$bin=Join-Path $gamePath 'bin/Win64_Shipping_Client'
$moduleRoot=Join-Path $gamePath 'Modules/MNBL.AgentGrowthProbe'
$expectedWatchdog=[IO.Path]::GetFullPath((Join-Path $bin 'Watchdog/Watchdog.exe'))
. (Join-Path $PSScriptRoot 'Test-NativeExitContract.ps1')
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
# Any Bannerlord* (Bannerlord.exe, Bannerlord.Native.exe) or Watchdog process means the game window is busy.
function Games { return ,@(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like 'Bannerlord*' -or $_.ProcessName -eq 'Watchdog' }) }
function Wait-ForGameWindow {
    while ((Games).Count -ne 0) { Write-Output 'Waiting for every existing Bannerlord/Watchdog session to exit.'; Start-Sleep -Seconds 10 }
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
    if ((Hash $profile.exitContract.path) -ne $profile.exitContract.sha256) { throw 'Shutdown contract changed.' }
    if ((Hash (Join-Path $bin 'TaleWorlds.Native.dll')) -ne $profile.sourceSha256) { throw 'Installed native DLL is not the stock build this probe covers.' }
    if ((Hash $expectedWatchdog) -ne $contract.watchdogSha256) { throw 'Installed watchdog differs from the reviewed shutdown contract.' }
    if ((CrashDirectoryState) -ne $crashState) { throw 'Crash directory state changed after preflight.' }
}
# absent | listable | reparse-listable | reparse-target-unavailable. A junction whose target is
# missing cannot receive dumps; the watchdog log's exception/dump markers remain the crash check.
function CrashDirectoryState {
    if (!(Test-Path -LiteralPath $crashPath)) { return 'absent' }
    $reparse=((Get-Item -LiteralPath $crashPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    try { [void](Get-ChildItem -LiteralPath $crashPath -Force -ErrorAction Stop) }
    catch {
        if ($reparse) { return 'reparse-target-unavailable' }
        throw "Crash directory cannot be listed: $crashPath"
    }
    if ($reparse) { return 'reparse-listable' } else { return 'listable' }
}
function CrashNames { if ($crashState -in @('listable','reparse-listable')) { return ,@(Get-ChildItem -LiteralPath $crashPath -Force | ForEach-Object Name) } else { return ,@() } }
# Crash dumps or reports created during the run fail it regardless of exit code.
function NewCrashEntries([datetime]$sinceUtc) {
    $now=CrashDirectoryState
    # The stock watchdog creates an absent dump directory at startup; every entry in it is then new.
    if ($now -ne $crashState -and !($crashState -eq 'absent' -and $now -eq 'listable')) { throw 'Crash directory state changed during the run.' }
    if ($now -notin @('listable','reparse-listable')) { return ,@() }
    return ,@(Get-ChildItem -LiteralPath $crashPath -Force | Where-Object { $_.LastWriteTimeUtc -ge $sinceUtc -or $crashBefore -notcontains $_.Name } | ForEach-Object Name)
}
function VerifyResult($r,[int]$ownedId,[long]$ownedStart) {
    if ($r.schemaVersion -ne 2 -or $r.hostVersion -ne 'managed-v2' -or $r.runId -ne $runId -or $r.pid -ne $ownedId -or $r.startTicks -ne $ownedStart -or
        $r.nativeSha256 -ne $nativeHash -or $r.hostSha256 -ne $hostHash -or $r.status -ne 'passed-awaiting-shutdown-verification' -or
        $r.quitRequested -ne $true -or $r.mainMenuReady -ne $true -or $r.missionAbsent -ne $true -or $r.menuWaitSeconds -lt 3 -or $r.nativeCode -ne 0 -or $r.error) { throw 'Probe report identity or completion rejected.' }
    # Engine return code before QuitGame; nativeCode is the probe's own value and cannot substitute.
    if ($null -eq $r.nativeReturnCode -or $r.nativeReturnCode -ne 0) { throw 'Engine return code before quit was missing or nonzero.' }
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
# Children of the exact owned game PID created after it started. Path and hash must match the contract.
function ObserveOwnedWatchdogs([int]$gameId,[datetime]$gameStartUtc) {
    foreach($child in @(Get-CimInstance -ClassName Win32_Process -Filter "ParentProcessId=$gameId" -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq 'Watchdog.exe' })) {
        $created=([datetime]$child.CreationDate).ToUniversalTime()
        if ($created -lt $gameStartUtc.AddSeconds(-2)) { continue } # Parent PID reuse: predates our game.
        $known=@($watchdogs | Where-Object { $_.pid -eq $child.ProcessId })
        if ($known.Count -gt 0) { continue }
        if (!$child.ExecutablePath -or [IO.Path]::GetFullPath($child.ExecutablePath) -ne $expectedWatchdog) { throw 'Owned watchdog has an unexpected executable path.' }
        $sha=Hash $child.ExecutablePath
        if ($sha -ne $contract.watchdogSha256) { throw 'Owned watchdog executable differs from the reviewed contract.' }
        $script:watchdogs+=,([ordered]@{pid=[int]$child.ProcessId;parentPid=[int]$child.ParentProcessId;createdUtc=$created.ToString('O');path=[IO.Path]::GetFullPath($child.ExecutablePath);sha256=$sha})
    }
}
function OwnedWatchdogAlive($record) {
    $row=@(Get-CimInstance -ClassName Win32_Process -Filter "ProcessId=$($record.pid)" -ErrorAction SilentlyContinue)
    if ($row.Count -eq 0) { return $false }
    # Only the same process instance counts; a reused PID is not ours.
    return (([datetime]$row[0].CreationDate).ToUniversalTime().ToString('O') -eq $record.createdUtc -and $row[0].Name -eq 'Watchdog.exe')
}
# Re-identifies the recorded instance through one Process handle, then terminates through that
# same handle, so a PID reused after the identity check cannot be hit.
function StopOwnedWatchdog($record) {
    $process=Get-Process -Id $record.pid -ErrorAction SilentlyContinue
    if ($null -eq $process) { return }
    # Reading Handle opens and caches one process handle; StartTime and Kill then use that same
    # handle, so the instance checked is the instance terminated.
    try { [void]$process.Handle } catch { return }
    $created=[datetime]::Parse($record.createdUtc,$null,[Globalization.DateTimeStyles]::RoundtripKind)
    if ($process.ProcessName -ne 'Watchdog' -or [Math]::Abs(($process.StartTime.ToUniversalTime()-$created).TotalMilliseconds) -ge 1) { return }
    $process.Kill(); [void]$process.WaitForExit(30000)
    $script:stoppedWatchdogs+=,$record.pid
}
# The engine error log of a clean main-menu probe exists and holds only its start header.
function VerifyErrorLog([int]$gameId,[datetime]$sinceUtc) {
    $path=Join-Path $logPath "rgl_log_errors_$gameId.txt"
    if (!(Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).LastWriteTimeUtc -lt $sinceUtc) { throw 'Missing or stale engine error log.' }
    $copy=Join-Path $runPath ([IO.Path]::GetFileName($path))
    Copy-Item -LiteralPath $path -Destination $copy
    foreach($line in [IO.File]::ReadAllLines($copy)) {
        if ($line -notmatch '^\s*$|^Starting new log file\.\.\.\s*$|^\[[0-9:.]+\] (\d{1,2} \w+ \d{4} |Computer: |Build Version: )') { throw "Engine error log has entries: $line" }
    }
    return @{path=$copy;sha256=(Hash $copy)}
}
if (-not ('MNBLElementProbeV2.OwnedDialogs' -as [type])) {
    Add-Type @'
using System; using System.Collections.Generic; using System.Text; using System.Runtime.InteropServices;
namespace MNBLElementProbeV2 {
public static class OwnedDialogs {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    static string Text(IntPtr h) { var s = new StringBuilder(512); GetWindowText(h, s, s.Capacity); return s.ToString(); }
    static string Class(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, s.Capacity); return s.ToString(); }
    // Answers "No" (keep existing settings) on a Safe Mode prompt owned by exactly this PID. Nothing else.
    public static bool DeclineSafeMode(int pid) {
        var dialogs = new List<IntPtr>();
        EnumWindows((h, l) => { int owner; GetWindowThreadProcessId(h, out owner);
            if (owner == pid && IsWindowVisible(h) && Class(h) == "#32770" && Text(h) == "Safe Mode") dialogs.Add(h); return true; }, IntPtr.Zero);
        foreach (var dialog in dialogs) {
            IntPtr no = IntPtr.Zero;
            EnumChildWindows(dialog, (c, l) => { if (Class(c) == "Button" && Text(c).Replace("&", "").Trim() == "No") { no = c; return false; } return true; }, IntPtr.Zero);
            if (no != IntPtr.Zero) { SendMessage(no, 0x00F5, IntPtr.Zero, IntPtr.Zero); return true; }
        }
        return false;
    }
}}
'@
}
# Child-only arming. The parent's environment is never modified. Inert tests substitute this
# and other commands; any substitution is detected below and can never yield a live pass.
if (-not (Test-Path Function:\Start-ArmedGame)) {
    function Start-ArmedGame([string]$FilePath,[string]$WorkingDirectory,[string]$Arguments,[string]$Key,[string]$Value) {
        $info=New-Object System.Diagnostics.ProcessStartInfo
        $info.FileName=$FilePath; $info.WorkingDirectory=$WorkingDirectory; $info.Arguments=$Arguments
        $info.UseShellExecute=$false
        $info.EnvironmentVariables[$Key]=$Value
        return [System.Diagnostics.Process]::Start($info)
    }
}
# Built-in means a cmdlet, or (Windows PowerShell 5.1 Get-FileHash) a function of a shipped
# Microsoft.PowerShell.* module whose script lives under $PSHOME. Script-defined substitutes are neither.
$commandOrigins=[ordered]@{};$substituted=@()
foreach($name in @('Get-Process','Get-CimInstance','Copy-Item','Move-Item','Remove-Item','New-Item','Set-Content','Get-Content','Get-ChildItem','Get-Item','Test-Path','Get-FileHash','Start-Sleep')) {
    $command=Get-Command $name
    $file=if ($command.CommandType -eq 'Function') { $command.ScriptBlock.File } else { $null }
    $commandOrigins[$name]=[ordered]@{type=[string]$command.CommandType;source=[string]$command.Source;file=$file}
    $builtIn=$command.CommandType -eq 'Cmdlet' -or ($command.CommandType -eq 'Function' -and [string]$command.Source -like 'Microsoft.PowerShell.*' -and
        $file -and ([IO.Path]::GetFullPath($file)).StartsWith($PSHOME.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase))
    if (!$builtIn) { $substituted+=,$name }
}
$launcherSource=(Get-Command Start-ArmedGame).ScriptBlock.File
$leaseFull=[IO.Path]::GetFullPath($LeasePath)
$sharedLease=$leaseFull -eq [IO.Path]::GetFullPath((Join-Path $repo 'build/battle-scale/native-test.lock'))
# The list above cannot cover every command (Where-Object, Get-Command itself). A live pass therefore
# also requires this script to be the -File of a fresh -NoProfile host: no caller scope or profile
# exists there to define substitutes.
$hostArgs=@([Environment]::GetCommandLineArgs())
$fileAt=-1;for($i=0;$i -lt $hostArgs.Count;$i++) { if ($hostArgs[$i] -ieq '-File') { $fileAt=$i } }
$cleanHost=$fileAt -ge 2 -and $fileAt+1 -lt $hostArgs.Count -and @($hostArgs[1..($fileAt-1)] | Where-Object { $_ -ieq '-NoProfile' }).Count -eq 1 -and
    @($hostArgs[1..($fileAt-1)] | Where-Object { $_ -match '^-(c|command|encodedcommand|ec)$' }).Count -eq 0 -and
    [IO.Path]::GetFullPath($hostArgs[$fileAt+1]) -eq [IO.Path]::GetFullPath($PSCommandPath)
$liveCommands=$substituted.Count -eq 0 -and $launcherSource -eq $PSCommandPath -and $sharedLease -and $cleanHost
$profileHash=Hash "$packagePath/element-probe-profile.json"
$profile=Get-Content -LiteralPath "$packagePath/element-probe-profile.json" -Raw | ConvertFrom-Json
if ($profile.schemaVersion -ne 2 -or $profile.status -ne 'offline-built-private-element-probe-v2' -or [IO.Path]::GetFullPath($profile.gameRoot) -ne $gamePath -or
    $profile.environmentKey -ne 'MNBL_PRIVATE_ELEMENT_PROBE_REQUEST' -or $profile.modules -ne '_MODULES_*Native*SandBoxCore*CustomBattle*MNBL.AgentGrowthProbe*_MODULES_' -or
    $profile.patchedSha256 -ne $profile.sourceSha256) { throw 'Unexpected private-probe v2 profile.' }
$names=@($profile.files | ForEach-Object name | Sort-Object)
if (($names -join '|') -ne 'MNBL.AgentGrowthProbe.dll|MNBL.NativeElementProbe.dll|SubModule.xml') { throw 'Package must contain exactly the three private module files.' }
if ((Hash $profile.exitContract.path) -ne $profile.exitContract.sha256) { throw 'Shutdown contract changed.' }
$contract=Get-Content -LiteralPath $profile.exitContract.path -Raw | ConvertFrom-Json
if ($contract.status -ne 'reviewed-build-specific-watchdog-shutdown' -or $contract.nativeSourceSha256 -ne $profile.sourceSha256 -or
    $contract.patchedCandidateSha256 -ne $profile.sourceSha256 -or [IO.Path]::GetFullPath($contract.watchdogPath) -ne $expectedWatchdog) { throw 'Shutdown contract does not cover this stock build.' }
$crashState=CrashDirectoryState
VerifyInputs
$nativeHash=($profile.files | Where-Object name -eq 'MNBL.NativeElementProbe.dll').sha256
$hostHash=($profile.files | Where-Object name -eq 'MNBL.AgentGrowthProbe.dll').sha256
Write-Output "Private probe v2 preflight passed. Existing Bannerlord/Watchdog sessions: $((Games).Count). Crash directory: $crashState. Live commands: $liveCommands."
if (!$Execute) { return }
if ($ReviewedPackageSha256 -cne $profileHash) { throw 'Execute requires the exact independently reviewed profile SHA256.' }

# Exclusive lease shared with other MNBL runners; another holder makes this throw before any change.
$lease=[IO.File]::Open($leaseFull,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$runId=[guid]::NewGuid().ToString('D')
$runPath=Join-Path $runRootPath $runId
$owned=$null;$ownedStart=0L;$changed=@();$failure=$null;$restored=$false;$forced=$false;$timedOut=$false;$nativePassed=$false;$exitCode=$null;$ownedStillRunning=$false
$watchdogs=@();$shutdown=$null;$dialogs=@();$crashBefore=@();$stoppedWatchdogs=@();$errorLog=$null;$createdDirectories=@();$removedDirectories=@()
$gameExe=[IO.Path]::GetFullPath((Join-Path $bin 'Bannerlord.exe'))
$receipt=[ordered]@{schemaVersion=2;runner='Test-ElementProbeInGameV2';runId=$runId;status='preparing';profileSha256=$profileHash;nativeSha256=$nativeHash;hostSha256=$hostHash;
    exitContractSha256=$profile.exitContract.sha256;startedUtc=[DateTime]::UtcNow.ToString('O');gameRoot=$gamePath;logRoot=$logPath;runRoot=$runRootPath;leasePath=$leaseFull;sharedLease=$sharedLease;
    crashDirectory=[ordered]@{path=$crashPath;state=$crashState};launcherSource=$launcherSource;commandOrigins=$commandOrigins;substitutedCommands=$substituted;hostCommandLine=$hostArgs;cleanHost=$cleanHost;liveCommandsVerified=$liveCommands;
    nativeProbePassed=$false;verifiedShutdown=$false;verifiedShutdownProtocol=$false;normalExitVerified=$false;restored=$false;gameLaunched=$false;engineDllModified=$false}
try {
    FreshWindow
    VerifyInputs
    New-Item -ItemType Directory -Path $runPath -Force | Out-Null
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
        # Record every directory this run creates (deepest last) so restore can remove it if empty.
        $missing=@();$cursor=Split-Path $destination
        while ($cursor.StartsWith($moduleRoot,[StringComparison]::OrdinalIgnoreCase) -and !(Test-Path -LiteralPath $cursor)) { $missing=,$cursor+$missing;$cursor=Split-Path $cursor }
        New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
        $createdDirectories+=$missing
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
    # Wait for other sessions BEFORE creating the request, so a long wait cannot make it stale.
    FreshWindow
    $requestPath=Join-Path $runPath ('request-'+$runId+'.json')
    $request=[ordered]@{schemaVersion=1;runId=$runId;createdUtc=[DateTime]::UtcNow.ToString('O');gameExecutable=$gameExe;
        nativeDll=(Join-Path $moduleRoot 'bin/Win64_Shipping_Client/MNBL.NativeElementProbe.dll');nativeSha256=$nativeHash;hostSha256=$hostHash;
        ownerPath=(Join-Path $runPath 'owner.json');claimPath=(Join-Path $runPath 'claimed.json');resultPath=(Join-Path $runPath 'result.json')}
    SaveJson $requestPath $request
    $crashBefore=CrashNames
    if ((Games).Count -ne 0) { throw 'A Bannerlord session started immediately before launch; cancelling.' }
    $launchUtc=[DateTime]::UtcNow
    $owned=Start-ArmedGame -FilePath $gameExe -WorkingDirectory $bin -Arguments ('/singleplayer '+$profile.modules+' /maxThreadCount 16') -Key $profile.environmentKey -Value $requestPath
    $ownedStart=$owned.StartTime.ToUniversalTime().Ticks
    $receipt['gameLaunched']=$true;$receipt['pid']=$owned.Id;$receipt['startTicks']=$ownedStart;$receipt['status']='running'
    VerifyOwnedIdentity $owned $ownedStart $gameExe
    SaveJson $request.ownerPath ([ordered]@{runId=$runId;pid=$owned.Id;startTicks=$ownedStart})
    SaveJson "$runPath/runtime-receipt.json" $receipt
    $ownedStartUtc=New-Object DateTime ($ownedStart,[DateTimeKind]::Utc)
    $deadline=[DateTime]::UtcNow.AddMinutes($TimeoutMinutes)
    while (!$owned.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        ObserveOwnedWatchdogs $owned.Id $ownedStartUtc
        # Only the owned game's own Safe Mode prompt; the held Process object keeps its PID stable.
        if ([MNBLElementProbeV2.OwnedDialogs]::DeclineSafeMode($owned.Id)) { $dialogs+=,([ordered]@{utc=[DateTime]::UtcNow.ToString('O');pid=$owned.Id;title='Safe Mode';clicked='No'}) }
        Start-Sleep -Seconds 2;$owned.Refresh()
    }
    if (!$owned.HasExited) { $timedOut=$true;throw 'Owned private-probe process timed out.' }
    $owned.WaitForExit();$exitCode=$owned.ExitCode
    # The watchdog detaches when the game exits; wait for the exact owned instance only.
    foreach($record in $watchdogs) {
        for($i=0;$i -lt 15 -and (OwnedWatchdogAlive $record);$i++) { Start-Sleep -Seconds 2 }
        if (OwnedWatchdogAlive $record) { throw "Owned watchdog $($record.pid) did not exit after the game." }
    }
    if (!(Test-Path -LiteralPath $request.resultPath)) { throw 'Owned process exited without an armed probe result.' }
    Copy-Item -LiteralPath $request.resultPath -Destination "$runPath/probe-result.json"
    $result=Get-Content -LiteralPath $request.resultPath -Raw | ConvertFrom-Json
    VerifyResult $result $owned.Id $ownedStart
    $nativePassed=$true
    $crashes=NewCrashEntries $launchUtc
    if ($crashes.Count -gt 0) { throw "Crash dump/report entries appeared during the run: $($crashes -join ', ')" }
    $errorLog=VerifyErrorLog $owned.Id $launchUtc
    $shutdown=Confirm-NativeExitContract -Profile $profile -Probe $result -ExitCode $exitCode -GameProcessId $owned.Id -OwnedWatchdogIds @($watchdogs | ForEach-Object { $_.pid }) `
        -StartedUtc $launchUtc -GameRoot $gamePath -LogRoot $logPath -RunPath $runPath
    if (!$shutdown.verified) { throw 'Shutdown verification did not pass.' }
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
                    if (!$owned.HasExited) { $owned.Kill(); [void]$owned.WaitForExit(30000) }
                }
                # A debuggee may not finish terminating while its debugger (our watchdog) is attached.
                if (!$owned.HasExited) { foreach($record in $watchdogs) { StopOwnedWatchdog $record }; [void]$owned.WaitForExit(30000) }
                if (!$owned.HasExited) { $ownedStillRunning=$true; throw 'Owned game did not terminate; module restore skipped and lease released.' }
            }
            $exitCode=$owned.ExitCode
            # Only the recorded watchdog instances of our own game, re-identified by PID+creation time.
            foreach($record in $watchdogs) {
                for($i=0;$i -lt 15 -and (OwnedWatchdogAlive $record);$i++) { Start-Sleep -Seconds 2 }
                if (OwnedWatchdogAlive $record) { $forced=$true; StopOwnedWatchdog $record }
            }
        }
    } catch { $failure=([string]$failure)+"`nOwned cleanup failed: "+$_.Exception.ToString() }
    try {
        # Waiting for "no game" would never end while our own game is still running.
        if ($ownedStillRunning) { throw 'Restore skipped: the owned game is still running. restore-files.json retains every backup and intent.' }
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
        for($i=$createdDirectories.Count-1;$i -ge 0;$i--) {
            $directory=$createdDirectories[$i]
            if ((Test-Path -LiteralPath $directory) -and @(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) {
                FreshWindow
                # Re-check after the wait; a non-recursive delete throws (never prompts) if it gained content.
                if (@(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) { [IO.Directory]::Delete($directory,$false); $removedDirectories+=,$directory }
            }
        }
        $restored=$true
    } catch { $failure=([string]$failure)+"`nRestoration failed: "+$_.Exception.ToString() }
    $verified=$null -ne $shutdown -and $shutdown.verified -and !$forced -and !$failure
    $receipt['nativeProbePassed']=$nativePassed;$receipt['timedOut']=$timedOut;$receipt['forcedCleanup']=$forced;$receipt['ownedStillRunning']=$ownedStillRunning
    $receipt['exitCode']=$exitCode;$receipt['restored']=$restored;$receipt['createdDirectories']=$createdDirectories;$receipt['removedDirectories']=$removedDirectories
    $receipt['ownedWatchdogs']=$watchdogs;$receipt['stoppedOwnedWatchdogs']=$stoppedWatchdogs;$receipt['dialogs']=$dialogs;$receipt['errorLog']=$errorLog
    $receipt['shutdownVerification']=$shutdown
    $receipt['verifiedShutdown']=$verified
    # The watchdog kp protocol and an actual zero exit are distinct; neither implies the other.
    $receipt['verifiedShutdownProtocol']=$verified -and $shutdown.kind -eq 'reviewed-watchdog-kp-command'
    $receipt['normalExitVerified']=$verified -and $exitCode -eq 0 -and $shutdown.kind -eq 'process-exit-zero'
    $receipt['allNativeDestructorsVerified']=$false
    $receipt['error']=$failure;$receipt['finishedUtc']=[DateTime]::UtcNow.ToString('O')
    $passed=!$failure -and $nativePassed -and $verified -and $restored
    # Substituted commands (inert tests) can never produce live acceptance.
    $receipt['status']=if (!$passed) {'failed'} elseif ($liveCommands) {'passed-private-native-probe-and-verified-shutdown'} else {'passed-inert-substituted-commands'}
    try { if (Test-Path -LiteralPath $runPath) { SaveJson "$runPath/runtime-receipt.json" $receipt } } finally { $lease.Dispose() }
}
Write-Output "Private probe v2 receipt: $runPath/runtime-receipt.json"
if ($receipt.status -eq 'failed') { throw $failure }
