# MMR-247: the map epic 2 acceptance run on the shared game install (docs/map-epic-2-acceptance.md). Phases, in order, each resumable:
#   build      tools/Build.ps1 (the one build that runs; its receipt is what Deploy-Dev verifies);
#   preflight  offline: the build that will be deployed (git commit, module and worldbridge hashes), MMR-284's and MMR-282's offline tests
#              (Test-GameUpdates, Test-WorldStore), the acceptance plan harness (Test-Map2Acceptance) and the summarizer's unit tests;
#   deploy     the installed Modules\MNBL.ProceduralWorld backed up (all but the SceneObj\mnbl_world_* folders that exist already, which are
#              hash-listed instead), the saves, the engine config and the terrain shader caches listed; then tools/Deploy-Dev.ps1, and the
#              tracked build receipt (evidence/runtime/build-api-inputs.json) put back as committed;
#   baseline   launch 1: map2_acceptance phase=baseline (the official War Sails sandbox: frames, memory after a day, the economy over
#              BaselineDays);
#   world1     launch 2: phase=world1 (seed 42 from the New Campaign screen and every step-2 check, the cast save continued, the soak);
#   world2     launch 3: phase=world2 (seed 73, its census; world 1 continued from its soak save in the same process);
#   mods       the synthetic content module built (tools/Build-AcceptanceContentModule.ps1) and deployed; launch 4: phase=mods with it
#              enabled (the setup screen, world 1's load); the module removed again whatever happened;
#   restore    the synthetic module removed (if still there), the installed module put back exactly as backed up (the run's own world
#              scenes removed: their copies stay in the store's world-scenes archive), the run's saves (mnbl_acc_<RunId>*) moved into the
#              run folder, the
#              engine config's safely_exited line and the run's terrain shader caches put back; -CleanStore also removes the store entries the
#              run added;
#   summarize  tools/summarize_map2_acceptance.py -> evidence/map-epic-2/acceptance.json.
#   decallab   (MMR-247 D; not in the default phases) phase=decallab: world 1 loaded from a save (-LabSave, else this run's newest world 1
#              save), or with neither started from the New Campaign screen (-Seed1, as world1 starts it) and saved (save_w1_lab); the decal
#              lab (w1.decalLab: labelled decal grids at a flat dry spot and a sea spot, then MMR-291's overlay mesh grid on each spot
#              (mesh:vein, mesh:river, mesh:magicWater: the route's materials on test pieces), the acc5 places with the fix), then the hang repro
#              (w1.hangRepro.war, w1.hangRepro.castle: a battle, then a diplomacy change and a teleport, then 300 frames; MMR-247 H). A launch
#              stopped as hung in a repro variant is that variant's outcome (blocked, its hang captures collected), not the lab's failure; the
#              lab is launched again from save_w1_lab (at most twice) for the variants left. Then tools/decal_lab_sheet.py over the lab's shots.
#              Needs the build deployed (deploy); a generated world's scene and save go in restore like world1's.
#   hangrepro  (MMR-247 K; not in the default phases) the hang repro alone: one launch per variant (-ReproKinds, default warControl, war,
#              warNoDressingMoves, warNoMagic, warNoCopySync, castle: MMR-247 X's bisect, each variant's switches set by the probe) from a
#              lab save (-LabSave, else this run's save_w1_lab; copied into Game Saves from -LabSaveFrom when missing, and moved out again in
#              restore), probe phase=decallab with w1.decalLab and the other variants skipped. Its launches are stopped as hung after
#              -ReproFlatSeconds (90: the in-process capture at 60 s gets its turn) of flat CPU with the game not responding in every 5 s
#              poll (or the general 5 minutes flat), and dumped first like every kill. A game that exits by itself with the variant's step
#              open is 'crashed', with its Windows Application log fault event (module, exception code, offset; MMR-247 X).
# Hang dumps (MMR-247 K): before this script kills its own game (CPU flat or the deadline), Write-ProcessDump.ps1 writes a full memory dump of
# it to logs/hang-<pid>.dmp (rundll32 comsvcs.dll MiniDump ... full; -NoHangDump skips it), and Analyze-HangDump.ps1 reads it into
# logs/hang-<pid>.dmp.analysis.txt/.json (-NoDumpAnalysis skips that). The launch record keeps wait.dump and analysis.
# Shared install rules (docs/runbooks/development.md "Run it in the game"): the battle-scale session's lock is held from the first phase that
# touches the install to the end (polled until free, never broken); before every deploy and launch no Bannerlord, Bannerlord.Native or
# Watchdog process may run (polled until none; no process this script did not start is ever touched); TaleWorlds.Native.dll and
# TaleWorlds.MountAndBlade.dll are hashed before and after every launch and recorded, never restored. A hung launch of this script's own
# game (CPU flat for 5 minutes, or past its deadline) is ended with taskkill /F /T on its PID (its Watchdog child with it); if it survives,
# the run stops at once and says which PID to end. The lock is released at the end of restore.
# State: build/acceptance/<RunId>/state.json (each phase's status, launch records, saves, hashes). A phase already done is skipped unless
# -Redo names it; every launch of a phase after its first saves under its own tag (<RunId>r<attempt>). -Resume re-runs world1 from its newest
# good save (soak, else cast, else day 0), skipping the steps that finished and the steps a game hung in (unless -Redo names them), and every
# phase listed after it; after a restore it deploys again and puts the run's saves back.
# A hung launch (acc4 triage hang-2/hang-3): the probe writes "map2_acceptance:<step>" at each step's start and "map2_acceptance:<step>:end
# <status>" at its end to its progress log (flushed per line); the step with a start and no end is the launch's hungStep (state.hungSteps).
# A world1 launch stopped as hung is launched again (at most 3 times) from the newest good save, skipping the finished and hung steps.
# A launch that exits by itself with a step open crashed in it (MMR-247 X): the launch keeps progress.openStep and crash (the Application
# log's fault event), state.crashedSteps lists it, and a world1 launch is launched again the same way, skipping the crashed step.
# The fresh-map rule (MMR-247 H: a land case after a mission on the loaded map reloads the save first) is off since MMR-247 X found the
# freezes' cause (c32: the cases run after missions without it); -FreshMapAfterMission turns it on for world1 again.
param(
    [string]$GameRoot = 'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord',
    [string]$RunId,
    [string[]]$Phases = @('build', 'preflight', 'deploy', 'baseline', 'world1', 'world2', 'mods', 'restore', 'summarize'),
    [string[]]$Redo = @(),
    [string]$Claim,
    [string[]]$Skip = @(),
    [switch]$Resume,
    [int]$Seed1 = 42,
    [int]$Seed2 = 73,
    [int]$SoakDays = 30,
    [int]$BaselineDays = 30,
    [int]$Frames = 600,
    [switch]$ForceVsyncOff,
    [string]$LockPath = 'C:\Users\davgo\Documents\GitHub\MNBL-modding-repo\build\battle-scale\native-test.lock',
    [int]$LockWaitMinutes = 240,
    [int]$GameFreeWaitMinutes = 120,
    [switch]$CleanStore,
    [switch]$NoDeploy,
    [string]$LabSave,
    [string]$LabSchool = 'fire',
    [string]$LabSaveFrom,
    [string[]]$ReproKinds = @('warControl', 'war', 'warNoDressingMoves', 'warNoMagic', 'warNoCopySync', 'castle'),
    [int]$ReproFlatSeconds = 90,
    [int]$ReproTimeoutSeconds = 900,
    [int]$HangFlatSeconds = 300,
    [switch]$NoHangDump,
    [switch]$NoDumpAnalysis,
    [switch]$FreshMapAfterMission
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$utf8 = New-Object Text.UTF8Encoding $false
if (!$RunId) { $RunId = 'acc' + [DateTime]::UtcNow.ToString('yyyyMMddHHmm') }
if ($RunId -notmatch '^[A-Za-z0-9]+$') { throw 'RunId must be letters and digits only (it names saves).' }
$run = Join-Path $repo "build/acceptance/$RunId"
New-Item -ItemType Directory -Force $run, (Join-Path $run 'in-game'), (Join-Path $run 'logs') | Out-Null
$statePath = Join-Path $run 'state.json'
$module = Join-Path $GameRoot 'Modules/MNBL.ProceduralWorld'
$contentId = 'MNBL.AcceptanceContent'
$contentModule = Join-Path $GameRoot "Modules/$contentId"
$store = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MNBL.ProceduralWorld'
$saveRoot = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Mount and Blade II Bannerlord/Game Saves'
$configs = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Mount and Blade II Bannerlord/Configs'
$shaders = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Mount and Blade II Bannerlord/TerrainShaders/MNBL.ProceduralWorld'
$bin = Join-Path $GameRoot 'bin/Win64_Shipping_Client'
Start-Transcript -LiteralPath (Join-Path $run ('logs/transcript-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss') + '.log')) -Append | Out-Null

function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
# A native command's output lines and exit code. Windows PowerShell turns a native tool's stderr into error records, which stop a script
# under ErrorActionPreference Stop; here the preference is Continue for the call and every line is kept as text.
function Invoke-Native([string]$Exe, [string[]]$Arguments) {
    $saved = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $lines = @(& $Exe @Arguments 2>&1 | ForEach-Object { "$_" }); $code = $LASTEXITCODE } finally { $ErrorActionPreference = $saved }
    return [pscustomobject]@{ Lines = $lines; Exit = $code }
}
function Rel([string]$Path, [string]$Root) { $Path.Substring($Root.Length).TrimStart('\', '/').Replace('\', '/') }
function Now { [DateTime]::UtcNow.ToString('O') }
function ConvertTo-Hashtable($o) {
    if ($null -eq $o) { return $null }
    if ($o -is [System.Management.Automation.PSCustomObject]) { $h = [ordered]@{}; foreach ($p in $o.PSObject.Properties) { $h[$p.Name] = ConvertTo-Hashtable $p.Value }; return $h }
    if ($o -is [System.Collections.IEnumerable] -and $o -isnot [string]) { return @($o | ForEach-Object { ConvertTo-Hashtable $_ }) }
    return $o
}
$state = if (Test-Path -LiteralPath $statePath) { ConvertTo-Hashtable (Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json) } else { [ordered]@{} }
if (!$state.Contains('schema')) { $state['schema'] = 'mnbl.map2-acceptance-run'; $state['version'] = 1; $state['runId'] = $RunId; $state['created'] = (Now); $state['phases'] = [ordered]@{}; $state['launches'] = @(); $state['hashes'] = @(); $state['saves'] = [ordered]@{} }
if ($Claim) { $state['claim'] = [ordered]@{ text = $Claim; recorded = (Now) } }
function Save-State { [IO.File]::WriteAllText($statePath, ($state | ConvertTo-Json -Depth 20), $utf8) }
Save-State

# ---------------------------------------------------------------- the shared install

$script:lockHandle = $null
function Get-Lock {
    if ($null -ne $script:lockHandle) { return }
    if (!(Test-Path -LiteralPath (Split-Path $LockPath))) { throw "The battle-scale lock's folder is missing: $(Split-Path $LockPath)" }
    $deadline = (Get-Date).AddMinutes($LockWaitMinutes); $waited = 0
    do {
        try { $script:lockHandle = [IO.File]::Open($LockPath, 'OpenOrCreate', 'ReadWrite', 'None') }
        catch { if ((Get-Date) -gt $deadline) { throw "The battle-scale lock stayed held for $LockWaitMinutes minutes." }; if ($waited % 10 -eq 0) { Write-Host "lock busy, waiting ($LockPath)" }; Start-Sleep -Seconds 30; $waited++ }
    } until ($null -ne $script:lockHandle)
    $state['lock'] = [ordered]@{ path = $LockPath; acquired = (Now); waitedPolls = $waited }
    Save-State
}
function Free-Lock { if ($null -ne $script:lockHandle) { $script:lockHandle.Dispose(); $script:lockHandle = $null; $state['lock']['released'] = (Now); Save-State } }

function Game-Processes { @(Get-Process Bannerlord, Bannerlord.Native, Watchdog -ErrorAction SilentlyContinue) }
# Until no game process runs (another session's, or a previous launch winding down); never stops one.
function Wait-GameFree([string]$Why) {
    $deadline = (Get-Date).AddMinutes($GameFreeWaitMinutes); $polls = 0
    while ((Game-Processes).Count -gt 0) {
        if ((Get-Date) -gt $deadline) { throw "A game process kept running for $GameFreeWaitMinutes minutes before $Why ($(@(Game-Processes | ForEach-Object { "$($_.ProcessName) $($_.Id)" }) -join ', '))." }
        if ($polls % 10 -eq 0) { Write-Host "waiting for the game to be free before $Why" }
        Start-Sleep -Seconds 30; $polls++
    }
    if ($null -eq $script:lockHandle) { throw 'The battle-scale lock is not held.' }
    return $polls
}

function Native-Hashes([string]$At) {
    $row = [ordered]@{ at = $At; utc = (Now) }
    foreach ($n in @('TaleWorlds.Native.dll', 'TaleWorlds.MountAndBlade.dll')) { $p = Join-Path $bin $n; $row[$n] = $(if (Test-Path -LiteralPath $p) { Sha $p } else { $null }) }
    $state['hashes'] = @($state['hashes']) + @($row)
    Save-State
    return $row
}

function Tree-Manifest([string]$Root, [string[]]$ExcludeTop = @()) {
    $files = [ordered]@{}
    if (Test-Path -LiteralPath $Root) {
        foreach ($f in Get-ChildItem -LiteralPath $Root -Recurse -File -Force | Sort-Object FullName) {
            $rel = Rel $f.FullName $Root
            if ($ExcludeTop | Where-Object { $rel.StartsWith($_ + '/', [StringComparison]::OrdinalIgnoreCase) }) { continue }
            $files[$rel] = Sha $f.FullName
        }
    }
    return $files
}
function Folder-Hash([string]$Root) {
    $lines = (Tree-Manifest $Root).GetEnumerator() | ForEach-Object { $_.Key + '=' + $_.Value }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($utf8.GetBytes(($lines -join "`n")))).Replace('-', '').ToLowerInvariant() } finally { $sha.Dispose() }
}
function Save-List { $rows = [ordered]@{}; if (Test-Path -LiteralPath $saveRoot) { foreach ($s in Get-ChildItem -LiteralPath $saveRoot -Filter '*.sav' -File | Sort-Object Name) { $rows[$s.Name] = Sha $s.FullName } }; $rows }
function Store-List { $rows = [ordered]@{}; if (Test-Path -LiteralPath $store) { foreach ($d in Get-ChildItem -LiteralPath $store -Directory | Sort-Object Name) { $rows[$d.Name] = @(Get-ChildItem -LiteralPath $d.FullName -Force | Sort-Object Name | ForEach-Object { $_.Name }) } }; $rows }

# ---------------------------------------------------------------- launches

# Set when this script's own game could not be stopped: the run ends at once (every later phase would wait for the game to be free).
$script:fatal = $null

# Ends this script's own game: taskkill /F /T on its PID (the engine-load hang of MMR-49 survived Stop-Process; taskkill /F /T ended it, and
# /T also takes its Watchdog child), then Stop-Process if it still runs. $Proc is the handle held since the launch, so the PID cannot have
# been reused; the process is checked to still be Bannerlord. Returns what was done and whether it exited.
function Stop-OwnedGame([System.Diagnostics.Process]$Proc, [int]$ProbeId) {
    $row = [ordered]@{ exited = $Proc.HasExited }
    if ($Proc.HasExited) { return $row }
    if ($Proc.ProcessName -ne 'Bannerlord') { $row['refused'] = "PID $ProbeId is $($Proc.ProcessName), not this run's Bannerlord"; return $row }
    $kids = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$ProbeId" -ErrorAction SilentlyContinue)
    # When each child started (a Watchdog started at the block, not at the launch, would be the engine's crash handler at work).
    $row['children'] = @($kids | ForEach-Object { [ordered]@{ name = $_.Name; pid = [int]$_.ProcessId; started = $(if ($_.CreationDate) { ([DateTime]$_.CreationDate).ToUniversalTime().ToString('O') } else { $null }) } })
    $children = @($kids | Where-Object { $_.Name -eq 'Watchdog.exe' } | ForEach-Object { [int]$_.ProcessId })
    $r = Invoke-Native 'taskkill' @('/F', '/T', '/PID', "$ProbeId")
    $row['taskkill'] = [ordered]@{ exit = $r.Exit; lines = @($r.Lines | Select-Object -Last 4) }
    $exited = $Proc.WaitForExit(60000)
    if (-not $exited) { Stop-Process -Id $ProbeId -Force -ErrorAction SilentlyContinue; $row['stopProcess'] = $true; $exited = $Proc.WaitForExit(60000) }
    foreach ($c in $children) { $p = Get-Process -Id $c -ErrorAction SilentlyContinue; if ($p -and $p.ProcessName -eq 'Watchdog') { Stop-Process -Id $c -Force -ErrorAction SilentlyContinue } }
    $row['watchdogChildren'] = $children
    $row['watchdogLeft'] = @($children | Where-Object { $p = Get-Process -Id $_ -ErrorAction SilentlyContinue; $p -and $p.ProcessName -eq 'Watchdog' })
    $row['exited'] = $exited
    return $row
}

# Waits for this script's own game; stopped (with its own Watchdog child) when its CPU time stays flat (under 0.5 s per 15 s) for
# -HangFlatSeconds (5 minutes: a running game renders and uses CPU, and the navigation cache build keeps it busy; Process.Responding is not
# required, since it reads true for a game without a visible window), or, with $Watch.flatSeconds (the repro launches' rule), flat for that
# long with the game not responding in every poll of $Watch.pollSeconds; or past the deadline. Before the kill, a full memory dump of the game
# (Dump-Game, unless -NoHangDump). stillRunning: the game survived every stop.
function Wait-Probe([int]$ProbeId, [int]$DeadlineMinutes, [hashtable]$Watch = @{}) {
    $poll = $(if ($Watch['pollSeconds']) { [int]$Watch['pollSeconds'] } else { 15 })
    $cpuFlat = 0.5 * $poll / 15
    $polls = [int][Math]::Ceiling($HangFlatSeconds / $poll)
    $fastSeconds = [int]$Watch['flatSeconds']; $fastPolls = $(if ($fastSeconds -gt 0) { [int][Math]::Ceiling($fastSeconds / $poll) } else { 0 })
    $proc = Get-Process -Id $ProbeId
    $deadline = (Get-Date).AddMinutes($DeadlineMinutes); $flat = 0; $notResponding = 0; $hung = 0; $last = -1; $why = $null; $started = Get-Date
    while (-not $proc.HasExited -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds $poll
        $proc.Refresh(); if ($proc.HasExited) { break }
        $cpu = $proc.TotalProcessorTime.TotalSeconds; $responding = $proc.Responding
        if ($last -ge 0 -and ($cpu - $last) -lt $cpuFlat) { $flat++; if (-not $responding) { $notResponding++; $hung++ } else { $hung = 0 } } else { $flat = 0; $notResponding = 0; $hung = 0 }
        $last = $cpu
        if ($fastPolls -gt 0 -and $hung -ge $fastPolls) { $why = "CPU flat for $fastSeconds s, not responding in all $hung polls"; break }
        if ($flat -ge $polls) { $why = "CPU flat for $(if ($HangFlatSeconds % 60 -eq 0) { "$($HangFlatSeconds / 60) minutes" } else { "$HangFlatSeconds s" }) (not responding in $notResponding of $polls polls)"; break }
    }
    $row = [ordered]@{ stopped = $why; seconds = 0; rule = [ordered]@{ flatSeconds = $HangFlatSeconds; pollSeconds = $poll; notRespondingSeconds = $fastSeconds } }
    if (-not $proc.HasExited) {
        if (!$why) { $row['stopped'] = 'deadline' }
        if (!$NoHangDump) { $row['dump'] = Dump-Game $proc $ProbeId }
        $row['kill'] = Stop-OwnedGame $proc $ProbeId
        $row['stillRunning'] = -not $proc.HasExited
    }
    $proc.Dispose()
    $row['seconds'] = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
    return $row
}

# MMR-247 (K): a full memory dump of this script's own blocked game, before the kill: logs/hang-<pid>.dmp (Write-ProcessDump.ps1: rundll32
# comsvcs.dll MiniDump <pid> <file> full, waited until the file stops growing). Only the Bannerlord process this script launched (its handle,
# held since the launch, so the PID cannot have been reused). The record: path (run-relative), bytes, seconds, flags, ok, error.
function Dump-Game([System.Diagnostics.Process]$Proc, [int]$ProbeId) {
    if ($Proc.HasExited -or $Proc.ProcessName -ne 'Bannerlord') { return [ordered]@{ ok = $false; error = 'not this run''s running Bannerlord' } }
    $path = [IO.Path]::GetFullPath((Join-Path $run "logs/hang-$ProbeId.dmp"))
    Write-Host "dumping the blocked game (PID $ProbeId) to $path"
    $r = & (Join-Path $PSScriptRoot 'Write-ProcessDump.ps1') -ProcessId $ProbeId -Path $path -TimeoutSeconds 900
    $r['path'] = "logs/hang-$ProbeId.dmp"
    return $r
}

# MMR-247 (K): the dump read by Analyze-HangDump.ps1 (logs/hang-<pid>.dmp.analysis.txt/.json), the engine thread taken from this launch's
# in-process hang capture when it wrote one. Returns the exit, the report and the findings.
function Analyze-Dump([int]$ProbeId, [string]$Dump) {
    $row = [ordered]@{ report = "$Dump.analysis.txt"; json = "$Dump.analysis.json"; exit = $null; engineThread = $null; findings = @() }
    foreach ($c in @(Get-ChildItem -LiteralPath (Join-Path $run 'in-game') -File -Filter "map2_acceptance-$ProbeId-hang-*.txt" -ErrorAction SilentlyContinue)) {
        $m = [regex]::Match([IO.File]::ReadAllText($c.FullName), 'engine thread: managed \S+, native (\d+)')
        if ($m.Success) { $row['engineThread'] = $m.Groups[1].Value }
    }
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'Analyze-HangDump.ps1'), '-Dump', (Join-Path $run $Dump), '-TimeoutMinutes', '20')
    if ($row['engineThread']) { $arguments += @('-EngineThread', $row['engineThread']) }
    $r = Invoke-Native 'powershell' $arguments
    $row['exit'] = $r.Exit
    $json = Join-Path $run $row['json']
    if (Test-Path -LiteralPath $json) { $row['findings'] = @((Get-Content -LiteralPath $json -Raw | ConvertFrom-Json).findings | Select-Object -First 24) } else { $row['tail'] = @($r.Lines | Select-Object -Last 6) }
    return $row
}

# One launch of map2_acceptance: free game and lock checked, native hashes before and after, Start-OwnedProbe, the wait, Collect-OwnedProbe
# into the run folder, the engine log kept, the generator processes left recorded; returns the parsed result (or $null). $Watch: the hang
# rule for this launch (Wait-Probe). $ProbeArgs.phase, when given, is the probe's phase (the hangrepro launches run the probe's decallab).
function Launch([string]$Phase, [hashtable]$ProbeArgs, [int]$TimeoutSeconds, [string[]]$ExtraModules = @(), [hashtable]$Watch = @{}) {
    if (Restored-SinceDeploy) { throw "The install was restored after the last deploy; deploy again before the $Phase launch (-Resume does)." }
    $polls = Wait-GameFree "the $Phase launch"
    $before = Native-Hashes "before $Phase"
    $ProbeArgs['repo'] = $repo; if (!$ProbeArgs.Contains('phase')) { $ProbeArgs['phase'] = $Phase }
    # Every launch of a phase after its first saves under its own tag (RunId r<attempt>), so no save of an earlier attempt blocks or is reused.
    if (!$ProbeArgs.Contains('tag')) { $attempt = @($state['launches'] | Where-Object { $_.phase -eq $Phase }).Count; $ProbeArgs['tag'] = $(if ($attempt -gt 0) { $RunId + 'r' + $attempt } else { $RunId }) }
    if ($Skip.Count -gt 0 -and !$ProbeArgs.Contains('skip')) { $ProbeArgs['skip'] = @($Skip) }
    if ($ForceVsyncOff) { $ProbeArgs['forceVsyncOff'] = $true }
    $recordPath = Join-Path $run "logs/owned-$Phase.json"
    $line = & (Join-Path $PSScriptRoot 'Start-OwnedProbe.ps1') -Probe 'map2_acceptance' -ProbeTimeoutSeconds $TimeoutSeconds -ArgsJson ($ProbeArgs | ConvertTo-Json -Compress -Depth 6) -ExtraModules $ExtraModules -Record $recordPath
    $m = [regex]::Match([string]$line, 'Owned PID (\d+)')
    if (!$m.Success) { throw "Start-OwnedProbe gave no process id: $line" }
    $probeId = [int]$m.Groups[1].Value
    $launch = [ordered]@{ phase = $Phase; pid = $probeId; args = $ProbeArgs; started = (Now); gameFreePolls = $polls; extraModules = $ExtraModules; timeoutSeconds = $TimeoutSeconds }
    $state['launches'] = @($state['launches']) + @($launch); Save-State
    $wait = Wait-Probe $probeId ([int][Math]::Ceiling($TimeoutSeconds / 60) + 20) $Watch
    $launch['wait'] = $wait; $launch['ended'] = (Now)
    if ($wait['stillRunning']) {
        $launch['status'] = 'not stopped'; Save-State
        $script:fatal = "This run's own game (Bannerlord PID $probeId, $Phase launch) did not end after taskkill /F /T and Stop-Process. End it (taskkill /F /T /PID $probeId), then put the install back: -Phases restore,summarize -Redo restore."
        $state['fatal'] = $script:fatal; Save-State
        throw $script:fatal
    }
    Start-Sleep -Seconds 5
    $log = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) "Mount and Blade II Bannerlord/logs/rgl_log_$probeId.txt"
    if (Test-Path -LiteralPath $log) { Copy-Item -LiteralPath $log -Destination (Join-Path $run "logs/rgl_log_$probeId.txt") -Force; $launch['engineLog'] = "logs/rgl_log_$probeId.txt" }
    try { $observation = & (Join-Path $PSScriptRoot 'Collect-OwnedProbe.ps1') -Record $recordPath -Destination (Join-Path $run 'in-game'); $launch['collected'] = $true }
    catch { $launch['collectError'] = [string]$_.Exception.Message }
    $launch['generatorProcessesAfter'] = @(Get-Process fwg, worldbridge -ErrorAction SilentlyContinue | ForEach-Object { "$($_.ProcessName) $($_.Id)" })
    $after = Native-Hashes "after $Phase"
    $launch['nativeUnchanged'] = ($before['TaleWorlds.Native.dll'] -eq $after['TaleWorlds.Native.dll'] -and $before['TaleWorlds.MountAndBlade.dll'] -eq $after['TaleWorlds.MountAndBlade.dll'])
    $resultFile = Join-Path $run "in-game/map2_acceptance-$probeId.json"
    $doc = $null
    if (Test-Path -LiteralPath $resultFile) { $doc = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json; $launch['result'] = "in-game/map2_acceptance-$probeId.json"; $launch['status'] = $doc.status; $launch['final'] = $doc.final; $launch['error'] = $doc.error }
    else { $launch['result'] = $null; $launch['status'] = 'no result' }
    $launch['progress'] = Read-Progress $probeId
    if ($wait['dump'] -and $wait['dump']['ok'] -and !$NoDumpAnalysis) { $launch['analysis'] = Analyze-Dump $probeId $wait['dump']['path']; Save-State }
    # The open step is the hung one only when the game was stopped (a game that exits on its own ends every step it started). MMR-247 X: a
    # game that exited by itself with a step open crashed in it (acc5rep): openStep keeps it, and crash the Application log's fault event.
    $launch['progress']['openStep'] = $launch['progress']['hungStep']
    if ($launch['progress']['openStep'] -or $wait['stopped']) { $launch['crash'] = Crash-Event $probeId $launch['started'] }
    if (!$wait['stopped'] -and $launch['progress']['openStep']) {
        $crashed = [ordered]@{ phase = $Phase; pid = $probeId; step = $launch['progress']['openStep']; stepUtc = $launch['progress']['hungStepUtc']; lastStage = $launch['progress']['lastStage']
            lastStageUtc = $launch['progress']['lastStageUtc']; crash = $launch['crash']; utc = (Now) }
        $state['crashedSteps'] = @($state['crashedSteps'] | Where-Object { $_ }) + @($crashed)
    }
    if (!$wait['stopped']) { $launch['progress']['hungStep'] = $null; $launch['progress']['hungStepUtc'] = $null }
    if ($wait['stopped'] -and $launch['progress']['hungStep']) {
        $hung = [ordered]@{ phase = $Phase; pid = $probeId; step = $launch['progress']['hungStep']; stepUtc = $launch['progress']['hungStepUtc']; lastStage = $launch['progress']['lastStage']
            lastStageUtc = $launch['progress']['lastStageUtc']; stopped = $wait['stopped']; killed = [bool]($wait['kill'] -and $wait['kill']['exited']); utc = (Now) }
        $state['hungSteps'] = @($state['hungSteps'] | Where-Object { $_ }) + @($hung)
    }
    $state['launches'] = @($state['launches'] | Where-Object { $_.pid -ne $probeId }) + @($launch)
    if ($doc -and $doc.outputs) { foreach ($p in $doc.outputs.PSObject.Properties) { if ($p.Name -like 'save_*' -and $p.Value) { $state['saves'][$p.Name] = [string]$p.Value } } }
    Save-State
    Wait-GameFree "the next step after $Phase" | Out-Null
    return $doc
}

# The launch's progress log (collected into in-game/, else the game's own): the last stage written, and the last probe step that started
# ("map2_acceptance:<step>") with no end line ("map2_acceptance:<step>:end <status>") after it: the step the game was in when it stopped.
function Read-Progress([int]$ProbeId) {
    $row = [ordered]@{ log = $null; lastStage = $null; lastStageUtc = $null; hungStep = $null; hungStepUtc = $null; ended = 0 }
    $path = Join-Path $run "in-game/map2_acceptance-$ProbeId.log"
    if (!(Test-Path -LiteralPath $path)) { return $row }
    $row['log'] = "in-game/map2_acceptance-$ProbeId.log"
    $open = $null; $openUtc = $null
    foreach ($line in [IO.File]::ReadAllLines($path)) {
        $m = [regex]::Match($line, '^(\S+)\s+\+\d+ms\s+(.*)$')
        if (!$m.Success) { continue }
        $utc = $m.Groups[1].Value; $stage = $m.Groups[2].Value
        $row['lastStage'] = $stage; $row['lastStageUtc'] = $utc
        $start = [regex]::Match($stage, '^map2_acceptance:((?:w1|w2|base|mods)\.[A-Za-z0-9_.]+)$')
        $end = [regex]::Match($stage, '^map2_acceptance:((?:w1|w2|base|mods)\.[A-Za-z0-9_.]+):end\b')
        if ($start.Success) { $open = $start.Groups[1].Value; $openUtc = $utc }
        elseif ($end.Success) { $row['ended']++; if ($end.Groups[1].Value -eq $open) { $open = $null; $openUtc = $null } }
    }
    $row['hungStep'] = $open; $row['hungStepUtc'] = $openUtc
    return $row
}

# MMR-247 X: the Windows Application log's fault event (Application Error, id 1000) for this script's own game since its launch: the faulting
# module, its version time stamp, the exception code and the fault offset (the RVA in the module, for REA), and the first exception line of
# the game's Watchdog when it faulted too (acc5rep: DirectoryNotFoundException on the crashes folder, so no TaleWorlds dump). $null if none.
function Parse-CrashEvent([string]$Message) {
    $field = { param($name) $m = [regex]::Match($Message, "(?m)^\s*$([regex]::Escape($name)):\s*(.+?)\s*$"); if ($m.Success) { $m.Groups[1].Value } else { $null } }
    $module = & $field 'Faulting module name'
    $app = & $field 'Faulting application name'
    $faultingId = & $field 'Faulting process id'
    [ordered]@{ application = $(if ($app) { $app.Split(',')[0] } else { $null }); module = $(if ($module) { $module.Split(',')[0] } else { $null })
        moduleTimeStamp = $(if ($module -and $module -match 'time stamp:\s*(0x[0-9a-fA-F]+)') { $matches[1] } else { $null })
        exceptionCode = & $field 'Exception code'; faultOffset = & $field 'Fault offset'
        processId = $(if ($faultingId) { [Convert]::ToInt32($faultingId.Replace('0x', ''), 16) } else { $null }) }
}
function Crash-Event([int]$ProbeId, [string]$SinceUtc) {
    try {
        $since = [DateTime]::Parse($SinceUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).ToLocalTime().AddMinutes(-1)
        $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000, 1026; StartTime = $since } -ErrorAction SilentlyContinue)
        $hex = '0x{0:X}' -f $ProbeId
        $own = @($events | Where-Object { $_.Id -eq 1000 -and $_.Message -match "Faulting process id:\s*0x0*$($hex.Substring(2))\b" })
        if ($own.Count -eq 0) { return $null }
        $row = Parse-CrashEvent $own[-1].Message
        $row['utc'] = $own[-1].TimeCreated.ToUniversalTime().ToString('O')
        $watchdog = @($events | Where-Object { $_.Id -eq 1026 -and $_.Message -match 'Application: Watchdog\.exe' -and [Math]::Abs(($_.TimeCreated - $own[-1].TimeCreated).TotalSeconds) -lt 120 })
        if ($watchdog.Count -gt 0) { $row['watchdog'] = @($watchdog[-1].Message -split "`r?`n" | Where-Object { $_ -match 'Exception Info:' } | Select-Object -First 1) -join '' }
        return $row
    }
    catch { return [ordered]@{ error = [string]$_.Exception.Message } }
}

function Phase-Done([string]$Name) { $state['phases'].Contains($Name) -and $state['phases'][$Name]['status'] -eq 'done' -and ($Redo -notcontains $Name) }
function Mark([string]$Name, [string]$Status, $Detail) { $state['phases'][$Name] = [ordered]@{ status = $Status; utc = (Now); detail = $Detail }; Save-State }
# The restore phase ran (whatever its outcome) after the last deploy: the installed module is no longer the run's build.
function Restored-SinceDeploy {
    $r = $state['phases']['restore']; $d = $state['phases']['deploy']
    if (!$r) { return $false }
    if (!$d -or $d['status'] -ne 'done') { return $true }
    return ([DateTime]$r['utc']).ToUniversalTime() -gt ([DateTime]$d['utc']).ToUniversalTime()
}

# ---------------------------------------------------------------- phases

function Build {
    $r = Invoke-Native 'powershell' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'Build.ps1'))
    $out = $r.Lines; $exit = $r.Exit
    [IO.File]::WriteAllLines((Join-Path $run 'logs/build.log'), [string[]]$out, $utf8)
    if ($exit -ne 0) { Mark 'build' 'failed' ([ordered]@{ exit = $exit; tail = @($out | Select-Object -Last 8) }); throw "tools/Build.ps1 failed (exit $exit); see logs/build.log." }
    Mark 'build' 'done' ([ordered]@{ exit = $exit; module = (Sha (Join-Path $repo 'build/module/MNBL.ProceduralWorld.dll')); worldbridge = (Sha (Join-Path $repo 'build/worldbridge/Release/worldbridge.exe')) })
}

function Preflight {
    $row = [ordered]@{}
    $row['commit'] = [string](& git -C $repo rev-parse HEAD)
    $row['branch'] = [string](& git -C $repo branch --show-current)
    $row['uncommitted'] = @(& git -C $repo status --porcelain -- src worldgen config Modules tools tests docs)
    foreach ($f in @('build/module/MNBL.ProceduralWorld.dll', 'build/worldbridge/Release/worldbridge.exe', 'build/producer/apps/Release/fwg.exe')) { $p = Join-Path $repo $f; $row[$f] = $(if (Test-Path -LiteralPath $p) { Sha $p } else { 'missing' }) }
    $tests = [ordered]@{}
    $evidence = Join-Path $run 'preflight'; New-Item -ItemType Directory -Force $evidence | Out-Null
    foreach ($t in @(
            @{ name = 'Test-GameUpdates'; args = @('-Evidence', (Join-Path $evidence 'game-updates.json'), '-Log', (Join-Path $evidence 'game-updates.txt')) },
            @{ name = 'Test-WorldStore'; args = @('-Evidence', (Join-Path $evidence 'world-store.json'), '-Log', (Join-Path $evidence 'world-store.txt')) },
            @{ name = 'Test-Map2Acceptance'; args = @('-Evidence', (Join-Path $evidence 'map2-acceptance-plan.json')) })) {
        $r = Invoke-Native 'powershell' (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot "$($t.name).ps1")) + @($t.args))
        $tests[$t.name] = [ordered]@{ exit = $r.Exit; tail = @($r.Lines | Select-Object -Last 4) }
    }
    Push-Location $repo
    try { $r = Invoke-Native 'python' @('-m', 'unittest', 'tests.ci.test_map2_acceptance') } finally { Pop-Location }
    $tests['test_map2_acceptance'] = [ordered]@{ exit = $r.Exit; tail = @($r.Lines | Select-Object -Last 3) }
    $row['tests'] = $tests
    Mark 'preflight' 'done' $row
}

function Deploy {
    Get-Lock
    Wait-GameFree 'the backup and deploy' | Out-Null
    $backup = Join-Path $run 'backup'
    if (!(Test-Path -LiteralPath (Join-Path $backup 'manifest.json'))) {
        if (!(Test-Path -LiteralPath (Join-Path $module 'SubModule.xml'))) { throw "No installed module at $module." }
        $worlds = @(Get-ChildItem -LiteralPath (Join-Path $module 'SceneObj') -Directory -Filter 'mnbl_world_*' -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
        $exclude = @($worlds | ForEach-Object { "SceneObj/$_" })
        $files = Tree-Manifest $module $exclude
        foreach ($rel in $files.Keys) { $dest = Join-Path $backup ("module/" + $rel); New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null; Copy-Item -LiteralPath (Join-Path $module $rel) -Destination $dest }
        $scenes = [ordered]@{}; foreach ($w in $worlds) { $scenes[$w] = Folder-Hash (Join-Path $module "SceneObj/$w") }
        $config = Join-Path $configs 'engine_config.txt'
        if (Test-Path -LiteralPath $config) { Copy-Item -LiteralPath $config -Destination (Join-Path $backup 'engine_config.txt') }
        $manifest = [ordered]@{ utc = (Now); module = $files; preexistingWorldScenes = $scenes; saves = (Save-List); store = (Store-List)
            shaders = @(if (Test-Path -LiteralPath $shaders) { Get-ChildItem -LiteralPath $shaders -Directory | ForEach-Object { $_.Name } }); contentModulePresent = (Test-Path -LiteralPath $contentModule) }
        [IO.File]::WriteAllText((Join-Path $backup 'manifest.json'), ($manifest | ConvertTo-Json -Depth 8), $utf8)
        if ($manifest.contentModulePresent) { throw "$contentModule exists before the run; remove it (it is not this run's) or rename the synthetic module." }
    }
    if ($NoDeploy) { Mark 'deploy' 'done' ([ordered]@{ backup = 'backup/manifest.json'; deployed = 'not deployed (-NoDeploy): the installed build runs' }); return }
    Wait-GameFree 'the deploy' | Out-Null
    $out = & (Join-Path $PSScriptRoot 'Deploy-Dev.ps1') -GameRoot $GameRoot 2>&1 | ForEach-Object { "$_" }
    $receipt = Join-Path $module 'deployment.json'
    $null = Invoke-Native 'git' @('-C', $repo, 'checkout', '--', 'evidence/runtime/build-api-inputs.json')
    Mark 'deploy' 'done' ([ordered]@{ backup = 'backup/manifest.json'; deployOutput = @($out | Select-Object -Last 3); receiptSha256 = $(if (Test-Path -LiteralPath $receipt) { Sha $receipt } else { $null })
        moduleAssembly = (Sha (Join-Path $module 'bin/Win64_Shipping_Client/MNBL.ProceduralWorld.dll')) })
}

function Baseline {
    Get-Lock
    $doc = Launch 'baseline' @{ frames = $Frames; baselineDays = $BaselineDays; culture = 'empire' } (3 * 3600)
    Mark 'baseline' $(if ($doc -and $doc.outputs -and $doc.outputs.completed) { 'done' } else { 'incomplete' }) $null
}

# The world 1 steps a resumed run skips: those that finished (done) in an earlier attempt, except the ones every load needs again (the start
# or load itself, identity, faces). The cast, its save and the Continue are one unit (the Continue loads this attempt's cast save), as are
# the soak and its save: a unit is skipped only when its last step finished.
function Finished-Steps {
    $done = @()
    foreach ($l in @($state['launches'] | Where-Object { $_.phase -eq 'world1' -and $_.result })) {
        $p = Join-Path $run $l.result
        if (!(Test-Path -LiteralPath $p)) { continue }
        $doc = Get-Content -LiteralPath $p -Raw | ConvertFrom-Json
        if ($doc.outputs -and $doc.outputs.steps) { foreach ($s in $doc.outputs.steps.PSObject.Properties) { if ($s.Value.status -eq 'done' -and $s.Name -notin @('w1.start', 'w1.load', 'w1.save0', 'w1.identity', 'w1.faces')) { $done += $s.Name } } }
    }
    $done = @($done | Select-Object -Unique)
    if ($done -notcontains 'w1.continue') { $done = @($done | Where-Object { $_ -notin @('w1.cast', 'w1.saveCast') }) }
    if ($done -notcontains 'w1.saveSoak') { $done = @($done | Where-Object { $_ -ne 'w1.soak' }) }
    return $done
}

# The run's saves the restore moved into the run folder, put back into Game Saves (a resume after a restore loads them again).
function Return-Saves {
    $kept = Join-Path $run 'saves'; $back = @()
    if (Test-Path -LiteralPath $kept) {
        foreach ($s in @(Get-ChildItem -LiteralPath $kept -Filter '*.sav' -File)) { $dest = Join-Path $saveRoot $s.Name; if (!(Test-Path -LiteralPath $dest)) { Move-Item -LiteralPath $s.FullName -Destination $dest; $back += $s.Name } }
    }
    if ($back.Count -gt 0) { $state['savesReturned'] = @($state['savesReturned'] | Where-Object { $_ }) + @([ordered]@{ utc = (Now); saves = $back }); Save-State }
    return $back
}

# The newest good save of world 1 to resume from: the soak's when the soak unit finished, else the cast's when the cast unit (its Continue)
# finished, else day 0's.
function Newest-Save([string[]]$Finished) {
    if ($Finished -contains 'w1.saveSoak' -and $state['saves'].Contains('save_w1_soak')) { return 'save_w1_soak' }
    if ($Finished -contains 'w1.continue' -and $state['saves'].Contains('save_w1_cast')) { return 'save_w1_cast' }
    if ($state['saves'].Contains('save_w1_day0')) { return 'save_w1_day0' }
    return $null
}

# The steps world 1's games hung or crashed in (MMR-247 X), unless -Redo names them (a step name, or a prefix with *).
function Hung-Steps {
    @(@($state['hungSteps']) + @($state['crashedSteps']) | Where-Object { $_ -and $_.phase -eq 'world1' } | ForEach-Object { [string]$_.step } | Select-Object -Unique |
        Where-Object { $n = $_; -not ($Redo | Where-Object { $_ -eq $n -or ($_.EndsWith('*') -and $n.StartsWith($_.TrimEnd('*'))) }) })
}

# World 1's launch arguments resumed from the newest good save: the finished and hung steps skipped (and the start, the day-0 save), the
# saves of the units that run again this attempt's own (Launch gives it its own tag), the cast save named for w1.battle.mutated.
function Resume-Args([hashtable]$ProbeArgs) {
    $finished = Finished-Steps
    $from = Newest-Save $finished
    if (!$from) { return $null }
    $ProbeArgs['fromSave'] = $state['saves'][$from]
    $ProbeArgs['skip'] = @(@($Skip) + @($finished) + @(Hung-Steps) + @('w1.save0', 'w1.start') | Select-Object -Unique)
    if ($finished -notcontains 'w1.saveCast') { $state['saves'].Remove('save_w1_cast') | Out-Null }
    if ($finished -notcontains 'w1.saveSoak') { $state['saves'].Remove('save_w1_soak') | Out-Null }
    if ($state['saves'].Contains('save_w1_cast')) { $ProbeArgs['castSave'] = $state['saves']['save_w1_cast'] }
    return $from
}

# World 1's probe arguments (the fresh-map rule only with -FreshMapAfterMission).
function World1-Args {
    $a = @{ seed = $Seed1; culture = 'empire'; soakDays = $SoakDays; frames = $Frames }
    if ($FreshMapAfterMission) { $a['freshMapAfterMission'] = $true }
    return $a
}

function World1 {
    Get-Lock
    $probeArgs = World1-Args
    $attempts = @(); $from = $null
    if ($Resume -and $state['saves'].Contains('save_w1_day0')) {
        Wait-GameFree 'returning the run saves' | Out-Null
        $null = Return-Saves
        $from = Resume-Args $probeArgs
    }
    $relaunches = 0
    while ($true) {
        $doc = Launch 'world1' $probeArgs (6 * 3600)
        $l = @($state['launches'] | Where-Object { $_.phase -eq 'world1' })[-1]
        $attempt = [ordered]@{ pid = $l['pid']; stopped = $l['wait']['stopped']; killed = [bool]($l['wait']['kill'] -and $l['wait']['kill']['exited']); hungStep = $l['progress']['hungStep']
            crashedStep = $(if (!$l['wait']['stopped']) { $l['progress']['openStep'] } else { $null }); crash = $l['crash']
            lastStage = $l['progress']['lastStage']; lastStageUtc = $l['progress']['lastStageUtc']; fromSave = $probeArgs['fromSave']; skip = $probeArgs['skip'] }
        $attempts += @($attempt)
        if ($doc -and $doc.outputs -and $doc.outputs.build) { $state['world1Build'] = $doc.outputs.build.moduleAssemblySha256 }
        # The world's bundle (compiled data and the FWGC++ world) kept with the run for the summarizer.
        if ($doc -and $doc.outputs.steps -and $doc.outputs.steps.'w1.identity' -and $doc.outputs.steps.'w1.identity'.world) { Keep-Bundle ([string]$doc.outputs.steps.'w1.identity'.world.bundle) 'A' }
        # A hang (CPU flat or past the deadline, a step started and not ended), or a crash (MMR-247 X: the game exited by itself with a step
        # open): launched again from the newest good save, the hung or crashed step skipped.
        $hung = $attempt['stopped'] -and $attempt['hungStep'] -and ($attempt['stopped'] -like 'CPU flat*' -or $attempt['stopped'] -eq 'deadline')
        $crashed = [bool]$attempt['crashedStep']
        if (!($hung -or $crashed) -or $script:fatal -or $relaunches -ge 3) { break }
        $probeArgs = World1-Args
        $from = Resume-Args $probeArgs
        if (!$from) { break }
        $relaunches++
        Write-Output "world1: the game $(if ($crashed) { "crashed in $($attempt['crashedStep'])" } else { "hung in $($attempt['hungStep'])" }) ($($attempt['lastStage'])); launched again from $from, skipping $(@($probeArgs['skip']).Count) steps"
    }
    $status = if ($doc -and $doc.outputs -and $doc.outputs.completed) { 'done' } else { 'incomplete' }
    Mark 'world1' $status ([ordered]@{ attempts = $attempts; relaunches = $relaunches; hungSteps = @(Hung-Steps); resumedFrom = $from; freshMapAfterMission = [bool]$FreshMapAfterMission })
}

function Keep-Bundle([string]$Bundle, [string]$Key) {
    if (!$Bundle -or !(Test-Path -LiteralPath $Bundle)) { return }
    $dest = Join-Path $run "bundles/$Key"
    if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
    New-Item -ItemType Directory -Force $dest | Out-Null
    foreach ($d in @('compiled', 'policies')) { if (Test-Path -LiteralPath (Join-Path $Bundle $d)) { Copy-Item -LiteralPath (Join-Path $Bundle $d) -Destination $dest -Recurse } }
    New-Item -ItemType Directory -Force (Join-Path $dest 'source') | Out-Null
    foreach ($f in @('world.json', 'request.json')) { $p = Join-Path $Bundle "source/$f"; if (Test-Path -LiteralPath $p) { Copy-Item -LiteralPath $p -Destination (Join-Path $dest "source/$f") } }
    if (Test-Path -LiteralPath (Join-Path $Bundle 'manifest.json')) { Copy-Item -LiteralPath (Join-Path $Bundle 'manifest.json') -Destination $dest }
    $state["bundle$Key"] = [ordered]@{ source = $Bundle; kept = "bundles/$Key" }; Save-State
}

function World1-Save {
    foreach ($k in @('save_w1_soak', 'save_w1_cast', 'save_w1_day0')) { if ($state['saves'].Contains($k)) { return $state['saves'][$k] } }
    return $null
}

function World2 {
    Get-Lock
    $save = World1-Save
    $probeArgs = @{ seed = $Seed2; culture = 'empire' }
    if ($save) { $probeArgs['continueSave'] = $save }
    $doc = Launch 'world2' $probeArgs (4 * 3600)
    if ($doc -and $doc.outputs.steps -and $doc.outputs.steps.'w2.identity' -and $doc.outputs.steps.'w2.identity'.world) { Keep-Bundle ([string]$doc.outputs.steps.'w2.identity'.world.bundle) 'B' }
    Mark 'world2' $(if ($doc -and $doc.outputs -and $doc.outputs.completed) { 'done' } else { 'incomplete' }) ([ordered]@{ continueSave = $save })
}

function Remove-Content {
    if (!(Test-Path -LiteralPath $contentModule)) { return 'absent' }
    if ($script:fatal) { return 'left in place: this run''s game is still running' }
    Wait-GameFree 'removing the synthetic module' | Out-Null
    $built = Join-Path $repo "build/acceptance-content/$contentId.manifest.json"
    $ours = (Test-Path -LiteralPath (Join-Path $run 'content-deployed.json'))
    if (!$ours) { return 'present but not deployed by this run: left alone' }
    Remove-Item -LiteralPath $contentModule -Recurse -Force
    Remove-Item -LiteralPath (Join-Path $run 'content-deployed.json') -Force
    return $(if (Test-Path -LiteralPath $contentModule) { 'removal failed' } else { 'removed' })
}

function Mods {
    Get-Lock
    $save = World1-Save
    $row = [ordered]@{}
    $manifest = & (Join-Path $PSScriptRoot 'Build-AcceptanceContentModule.ps1') -GameRoot $GameRoot
    $row['built'] = [string]$manifest
    Wait-GameFree 'deploying the synthetic module' | Out-Null
    if (Test-Path -LiteralPath $contentModule) { throw "$contentModule exists; refusing to overwrite a module this run did not deploy." }
    try {
        Copy-Item -LiteralPath (Join-Path $repo "build/acceptance-content/$contentId") -Destination $contentModule -Recurse
        [IO.File]::WriteAllText((Join-Path $run 'content-deployed.json'), ((@{ utc = (Now); manifest = [string]$manifest; files = (Tree-Manifest $contentModule) }) | ConvertTo-Json -Depth 5), $utf8)
        $row['deployed'] = Folder-Hash $contentModule
        $probeArgs = @{ module = $contentId; seed = $Seed1 }
        if ($save) { $probeArgs['continueSave'] = $save }
        $doc = Launch 'mods' $probeArgs (3 * 3600) @($contentId)
        $row['completed'] = [bool]($doc -and $doc.outputs -and $doc.outputs.completed)
    }
    finally { $row['removed'] = Remove-Content }
    Mark 'mods' $(if ($row['completed'] -and $row['removed'] -eq 'removed') { 'done' } else { 'incomplete' }) $row
}

function Restore {
    Get-Lock
    Wait-GameFree 'the restore' | Out-Null
    $row = [ordered]@{ content = (Remove-Content) }
    $backup = Join-Path $run 'backup'
    $manifest = Get-Content -LiteralPath (Join-Path $backup 'manifest.json') -Raw | ConvertFrom-Json
    $preexisting = @($manifest.preexistingWorldScenes.PSObject.Properties | ForEach-Object { $_.Name })
    $wanted = [ordered]@{}; foreach ($p in $manifest.module.PSObject.Properties) { $wanted[$p.Name] = [string]$p.Value }
    $removed = @(); $restored = @(); $removedScenes = @()
    # The run's own world scenes (scene folders that did not exist before) go; their copies stay in the store's world-scenes archive.
    foreach ($d in @(Get-ChildItem -LiteralPath (Join-Path $module 'SceneObj') -Directory -Filter 'mnbl_world_*' -ErrorAction SilentlyContinue)) {
        if ($preexisting -notcontains $d.Name) { Remove-Item -LiteralPath $d.FullName -Recurse -Force; $removedScenes += $d.Name }
    }
    $current = Tree-Manifest $module @($preexisting | ForEach-Object { "SceneObj/$_" })
    foreach ($rel in @($current.Keys)) { if (!$wanted.Contains($rel)) { Remove-Item -LiteralPath (Join-Path $module $rel) -Force; $removed += $rel } }
    foreach ($rel in $wanted.Keys) {
        $target = Join-Path $module $rel
        if (!(Test-Path -LiteralPath $target) -or (Sha $target) -ne $wanted[$rel]) { New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null; Copy-Item -LiteralPath (Join-Path $backup "module/$rel") -Destination $target -Force; $restored += $rel }
    }
    # Empty folders the run left behind.
    foreach ($d in @(Get-ChildItem -LiteralPath $module -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending)) {
        $rel = Rel $d.FullName $module
        if ($preexisting | Where-Object { $rel -eq "SceneObj/$_" -or $rel.StartsWith("SceneObj/$_/") }) { continue }   # a world scene that was there before is never touched
        if (@(Get-ChildItem -LiteralPath $d.FullName -Force).Count -eq 0 -and !($wanted.Keys | Where-Object { $_.StartsWith($rel + '/') })) { Remove-Item -LiteralPath $d.FullName -Force }
    }
    $after = Tree-Manifest $module @($preexisting | ForEach-Object { "SceneObj/$_" })
    $same = ($after.Count -eq $wanted.Count) -and -not ($wanted.Keys | Where-Object { $after[$_] -ne $wanted[$_] })
    $scenes = [ordered]@{}; foreach ($w in $preexisting) { $scenes[$w] = [ordered]@{ before = [string]$manifest.preexistingWorldScenes.$w; after = $(if (Test-Path -LiteralPath (Join-Path $module "SceneObj/$w")) { Folder-Hash (Join-Path $module "SceneObj/$w") } else { 'absent' }) } }
    $row['module'] = [ordered]@{ identical = $same; removedFiles = $removed; restoredFiles = $restored; removedRunScenes = $removedScenes; preexistingScenes = $scenes }
    # Saves: the run's own (mnbl_acc_<RunId>_*, and mnbl_acc_<RunId>r<attempt>_* of later attempts) moved into the run folder; every save
    # that existed before checked unchanged.
    $kept = Join-Path $run 'saves'; New-Item -ItemType Directory -Force $kept | Out-Null
    $ownSave = '^mnbl_acc_' + [regex]::Escape($RunId) + '(r\d+)?_.*\.sav$'
    $moved = @(); foreach ($s in @(Get-ChildItem -LiteralPath $saveRoot -Filter "mnbl_acc_$($RunId)*.sav" -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $ownSave })) { Move-Item -LiteralPath $s.FullName -Destination (Join-Path $kept $s.Name) -Force; $moved += $s.Name }
    # The lab save hangrepro copied in from another run's folder (labSaveCopied) goes out again.
    if ($state['labSaveCopied']) { $n = [string]$state['labSaveCopied']['save']; $p = Join-Path $saveRoot $n; if (Test-Path -LiteralPath $p) { Move-Item -LiteralPath $p -Destination (Join-Path $kept $n) -Force; $moved += $n } }
    $savesNow = Save-List; $changed = @(); foreach ($p in $manifest.saves.PSObject.Properties) { if ($savesNow[$p.Name] -ne [string]$p.Value) { $changed += $p.Name } }
    $added = @($savesNow.Keys | Where-Object { -not $manifest.saves.PSObject.Properties[$_] })
    $row['saves'] = [ordered]@{ movedToRun = $moved; preexistingChanged = $changed; otherNewSaves = $added }
    # The engine config's crash flag, as found.
    $config = Join-Path $configs 'engine_config.txt'; $before = Join-Path $backup 'engine_config.txt'
    if ((Test-Path -LiteralPath $config) -and (Test-Path -LiteralPath $before)) {
        $was = (Get-Content -LiteralPath $before | Where-Object { $_ -match '^\s*safely_exited\s*=' } | Select-Object -First 1)
        $now = (Get-Content -LiteralPath $config | Where-Object { $_ -match '^\s*safely_exited\s*=' } | Select-Object -First 1)
        if ($was -and $now -and $was -ne $now) { $text = [IO.File]::ReadAllText($config); [IO.File]::WriteAllText($config, $text.Replace($now, $was)); $row['engineConfig'] = "safely_exited put back ($now -> $was)" } else { $row['engineConfig'] = 'unchanged' }
    }
    # Terrain shader caches of the run's own scenes.
    $shaderRemoved = @()
    if (Test-Path -LiteralPath $shaders) { foreach ($d in Get-ChildItem -LiteralPath $shaders -Directory) { if (@($manifest.shaders) -notcontains $d.Name -and ($removedScenes | Where-Object { $d.Name -like "$_*" })) { Remove-Item -LiteralPath $d.FullName -Recurse -Force; $shaderRemoved += $d.Name } } }
    $row['shaderCachesRemoved'] = $shaderRemoved
    $storeBefore = $manifest.store; $storeAdded = [ordered]@{}
    foreach ($d in @(Get-ChildItem -LiteralPath $store -Directory -ErrorAction SilentlyContinue)) {
        $was = @(); if ($storeBefore.PSObject.Properties[$d.Name]) { $was = @($storeBefore.($d.Name)) }
        $new = @(Get-ChildItem -LiteralPath $d.FullName -Force | Where-Object { $was -notcontains $_.Name } | ForEach-Object { $_.Name })
        if ($new.Count -gt 0) { $storeAdded[$d.Name] = $new }
    }
    if ($CleanStore) {
        foreach ($dir in @('bundles', 'worlds', 'activations', 'navigation-caches', 'world-scenes', 'world-navigation', 'world-settlements', 'world-playability', 'world-surfaces', 'jobs', 'previews', 'mutations', 'world-snow')) {
            if ($storeAdded.Contains($dir)) { foreach ($n in $storeAdded[$dir]) { Remove-Item -LiteralPath (Join-Path $store "$dir/$n") -Recurse -Force -ErrorAction SilentlyContinue } }
        }
        $row['storeCleaned'] = $true
    }
    $row['storeAdded'] = $storeAdded
    $row['nativeHashes'] = Native-Hashes 'after restore'
    # The install is as found: the lock is released here (the summary after it does not touch the install, and grades the release).
    Free-Lock
    Mark 'restore' $(if ($same -and $row['content'] -ne 'removal failed') { 'done' } else { 'incomplete' }) $row
}

# MMR-247 X: the probe's variants (Map2AcceptancePlan.HangRepros; the hangrepro phase's bisect), and the two the decal lab's own launch runs.
$reproVariants = @('w1.hangRepro.warControl', 'w1.hangRepro.war', 'w1.hangRepro.warNoDressingMoves', 'w1.hangRepro.warNoMagic', 'w1.hangRepro.warNoCopySync', 'w1.hangRepro.castle', 'w1.hangRepro.castleControl')
$labReproVariants = @('w1.hangRepro.war', 'w1.hangRepro.castle')

# The hang repro's outcome per variant over the decallab launches (a later launch's run of a variant replaces an earlier one's): 'engine
# blocked' (the game was stopped as hung with the variant's step open; the stop, the last stage and the collected hang captures), 'crashed'
# (MMR-247 X: the game exited by itself with the variant's step open; its fault event and last stage), 'ran' (its frames ran with no block),
# 'not run' (a precondition failed: the step's reason), else the step's own status (error, timeout).
function Repro-Outcomes([string]$Phase = 'decallab') {
    $rows = [ordered]@{}
    foreach ($l in @($state['launches'] | Where-Object { $_.phase -eq $Phase })) {
        $doc = $null
        if ($l['result'] -and (Test-Path -LiteralPath (Join-Path $run $l['result']))) { $doc = Get-Content -LiteralPath (Join-Path $run $l['result']) -Raw | ConvertFrom-Json }
        foreach ($v in $reproVariants) {
            $hung = $l['wait'] -and $l['wait']['stopped'] -and $l['progress'] -and $l['progress']['hungStep'] -eq $v
            $crashed = $l['wait'] -and !$l['wait']['stopped'] -and $l['progress'] -and $l['progress']['openStep'] -eq $v
            $step = $(if ($doc -and $doc.outputs -and $doc.outputs.steps) { $doc.outputs.steps.$v } else { $null })
            if ($hung) {
                $captures = @(Get-ChildItem -LiteralPath (Join-Path $run 'in-game') -File -Filter "map2_acceptance-$($l['pid'])-hang-*" -ErrorAction SilentlyContinue | ForEach-Object { "in-game/$($_.Name)" })
                $rows[$v] = [ordered]@{ outcome = 'engine blocked'; pid = $l['pid']; stopped = $l['wait']['stopped']; killed = [bool]($l['wait']['kill'] -and $l['wait']['kill']['exited'])
                    lastStage = $l['progress']['lastStage']; lastStageUtc = $l['progress']['lastStageUtc']; stepUtc = $l['progress']['hungStepUtc']; captures = $captures; engineLog = $l['engineLog']
                    dump = $(if ($l['wait']['dump']) { $l['wait']['dump'] } else { $null }); analysis = $(if ($l['analysis']) { $l['analysis'] } else { $null }); crash = $l['crash'] }
            }
            elseif ($crashed) {
                $rows[$v] = [ordered]@{ outcome = 'crashed'; pid = $l['pid']; lastStage = $l['progress']['lastStage']; lastStageUtc = $l['progress']['lastStageUtc']; crash = $l['crash']; engineLog = $l['engineLog']
                    detail = $(if ($l['crash'] -and $l['crash']['module']) { "$($l['crash']['exceptionCode']) in $($l['crash']['module'])+$($l['crash']['faultOffset'])" } else { 'exited with the step open; no fault event found' }) }
            }
            elseif ($step -and $step.status -and $step.status -ne 'skipped') {
                $rows[$v] = [ordered]@{ outcome = $(if ($step.status -eq 'done' -and $step.blocked -eq $false) { 'ran' } elseif ($step.status -eq 'blocked') { 'not run' } else { [string]$step.status }); pid = $l['pid']; frames = $step.frames
                    detail = $(if ($step.outcome) { $step.outcome } elseif ($step.reason) { $step.reason } elseif ($step.error) { ([string]$step.error).Split("`n")[0] } else { $null }) }
            }
        }
    }
    return $rows
}

function DecalLab {
    Get-Lock
    $save = $(if ($LabSave) { $LabSave } else { World1-Save })
    if ($save -and !(Test-Path -LiteralPath (Join-Path $saveRoot "$save.sav"))) {
        Wait-GameFree 'returning the run saves' | Out-Null
        $null = Return-Saves
        if (!(Test-Path -LiteralPath (Join-Path $saveRoot "$save.sav"))) { throw "The save $save is not in $saveRoot (an acceptance run's restore moves its saves into build/acceptance/<RunId>/saves)." }
    }
    # A save given or found: loaded; none: world 1 generated (seed $Seed1) through the New Campaign screen, its first start's cache build included.
    $probeArgs = $(if ($save) { @{ fromSave = $save; school = $LabSchool; culture = 'empire' } } else { @{ seed = $Seed1; school = $LabSchool; culture = 'empire' } })
    # MMR-247 X: the lab's own launches run its two repro variants (the product's); the bisect variants are the hangrepro phase's.
    $labSkip = @(@($Skip) + @($reproVariants | Where-Object { $labReproVariants -notcontains $_ }))
    $probeArgs['skip'] = $labSkip
    $doc = Launch 'decallab' $probeArgs $(if ($save) { 3600 } else { 7200 })
    $relaunches = 0
    while (!$script:fatal -and $relaunches -lt 2) {
        $l = @($state['launches'] | Where-Object { $_.phase -eq 'decallab' })[-1]
        $hungIn = $(if ($l['wait'] -and $l['wait']['stopped'] -and $l['progress']) { $l['progress']['hungStep'] } else { $null })
        $outcomes = Repro-Outcomes
        $left = @($labReproVariants | Where-Object { -not $outcomes.Contains($_) })
        $from = $(if ($state['saves'].Contains('save_w1_lab')) { $state['saves']['save_w1_lab'] } else { $save })
        # Only a block in a repro variant relaunches (from the lab's save, the lab and the variants with an outcome skipped).
        if ($labReproVariants -notcontains $hungIn -or $left.Count -eq 0 -or !$from) { break }
        $relaunches++
        Write-Output "decallab: the game blocked in $hungIn ($($l['progress']['lastStage'])); launched again from $from for $($left -join ', ')"
        $doc = Launch 'decallab' @{ fromSave = $from; school = $LabSchool; culture = 'empire'; skip = @(@($labSkip) + @('w1.decalLab') + @($labReproVariants | Where-Object { $left -notcontains $_ })) } 3600
    }
    # The lab's own launch: the one whose result holds w1.decalLab done (a block after it leaves its checkpointed record intact).
    $labLaunch = $null; $lab = $null
    foreach ($l in @($state['launches'] | Where-Object { $_.phase -eq 'decallab' -and $_.result })) {
        $p = Join-Path $run $l['result']
        if (!(Test-Path -LiteralPath $p)) { continue }
        $d = Get-Content -LiteralPath $p -Raw | ConvertFrom-Json
        $s = $(if ($d.outputs -and $d.outputs.steps) { $d.outputs.steps.'w1.decalLab' } else { $null })
        if ($s -and $s.status -ne 'skipped') { $labLaunch = $l; $lab = $s }
    }
    $sheet = $null
    if ($labLaunch) {
        $r = Invoke-Native 'python' @((Join-Path $PSScriptRoot 'decal_lab_sheet.py'), (Join-Path $run $labLaunch['result']), '--out', (Join-Path $run 'decal-lab'))
        $sheet = [ordered]@{ exit = $r.Exit; tail = @($r.Lines | Select-Object -Last 40) }   # MMR-291: the mesh grids print after the decal grids
    }
    Mark 'decallab' $(if ($lab -and $lab.status -eq 'done') { 'done' } else { 'incomplete' }) ([ordered]@{ save = $save; generated = !$save; labSave = $state['saves']['save_w1_lab']; school = $LabSchool
        status = $(if ($lab) { $lab.status } else { $null }); labPid = $(if ($labLaunch) { $labLaunch['pid'] } else { $null }); sheet = $sheet; hangRepro = (Repro-Outcomes); relaunches = $relaunches })
}

# MMR-247 (K): the hang repro alone. Each variant (-ReproKinds) in its own launch from the lab save (no mission since its load, as the lab's
# own launches ran them), the probe's decallab phase with w1.decalLab and the other variants skipped; the 90 s hang rule (-ReproFlatSeconds,
# with the game not responding in every 5 s poll); a block is dumped (logs/hang-<pid>.dmp) before the kill and analysed after it. The
# lab save: -LabSave (e.g. mnbl_acc_acc5lab_w1_lab), else this run's save_w1_lab; when it is not in Game Saves, this run's own saves are put
# back, else it is copied from -LabSaveFrom (a folder holding <save>.sav, or the .sav) and recorded as labSaveCopied (restore moves it out).
function HangRepro {
    Get-Lock
    $save = $(if ($LabSave) { $LabSave } elseif ($state['saves'].Contains('save_w1_lab')) { $state['saves']['save_w1_lab'] } else { $null })
    if (!$save) { throw 'hangrepro needs -LabSave (a decal lab save such as mnbl_acc_acc5lab_w1_lab) or this run''s own save_w1_lab.' }
    $savePath = Join-Path $saveRoot "$save.sav"
    if (!(Test-Path -LiteralPath $savePath)) {
        Wait-GameFree 'putting the lab save in place' | Out-Null
        $null = Return-Saves
        if (!(Test-Path -LiteralPath $savePath) -and $LabSaveFrom) {
            $src = $(if (Test-Path -LiteralPath $LabSaveFrom -PathType Container) { Join-Path $LabSaveFrom "$save.sav" } else { $LabSaveFrom })
            if (!(Test-Path -LiteralPath $src)) { throw "-LabSaveFrom has no $save.sav ($src)." }
            Copy-Item -LiteralPath $src -Destination $savePath
            $state['labSaveCopied'] = [ordered]@{ save = "$save.sav"; from = $src; sha256 = (Sha $savePath); utc = (Now) }; Save-State
        }
        if (!(Test-Path -LiteralPath $savePath)) { throw "The save $save is not in $saveRoot; pass -LabSaveFrom <folder or .sav> (e.g. C:\Users\davgo\m2\int\build\acceptance\acc5lab\saves)." }
    }
    $variants = @($ReproKinds | ForEach-Object { if ($_ -like 'w1.hangRepro.*') { $_ } else { "w1.hangRepro.$_" } })
    foreach ($v in $variants) { if ($reproVariants -notcontains $v) { throw "Unknown repro variant $v (known: $($reproVariants -join ', '))." } }
    $watch = @{ flatSeconds = $ReproFlatSeconds; pollSeconds = 5 }
    $launches = [ordered]@{}
    foreach ($v in $variants) {
        if ($script:fatal) { break }
        $skipList = @(@($Skip) + @('w1.decalLab') + @($reproVariants | Where-Object { $_ -ne $v }))   # not $skip: that name is the -Skip parameter
        Write-Output "hangrepro: $v from $save"
        $null = Launch 'hangrepro' @{ phase = 'decallab'; fromSave = $save; school = $LabSchool; culture = 'empire'; skip = $skipList } $ReproTimeoutSeconds @() $watch
        $l = @($state['launches'] | Where-Object { $_.phase -eq 'hangrepro' })[-1]
        $launches[$v] = [ordered]@{ pid = $l['pid']; status = $l['status']; stopped = $l['wait']['stopped']; lastStage = $l['progress']['lastStage']; hungStep = $l['progress']['hungStep']
            dump = $(if ($l['wait']['dump']) { $l['wait']['dump']['path'] } else { $null }); analysis = $(if ($l['analysis']) { $l['analysis']['report'] } else { $null }) }
    }
    $rows = Repro-Outcomes 'hangrepro'
    foreach ($v in $variants) { if (!$rows.Contains($v) -and $launches.Contains($v)) { $rows[$v] = [ordered]@{ outcome = 'no outcome'; launch = $launches[$v] } } }
    $complete = -not ($variants | Where-Object { !$rows.Contains($_) -or $rows[$_]['outcome'] -eq 'no outcome' })
    Mark 'hangrepro' $(if ($complete) { 'done' } else { 'incomplete' }) ([ordered]@{ save = $save; copied = $state['labSaveCopied']; rule = $watch; variants = $variants; launches = $launches; hangRepro = $rows })
}

function Summarize {
    $r = Invoke-Native 'python' @((Join-Path $PSScriptRoot 'summarize_map2_acceptance.py'), '--run', $run, '--out', (Join-Path $repo 'evidence/map-epic-2/acceptance.json'))
    Mark 'summarize' $(if ($r.Exit -eq 0) { 'done' } else { 'failed' }) ([ordered]@{ exit = $r.Exit; tail = @($r.Lines | Select-Object -Last 6) })
}

# -Resume: world1 again from its day-0 save, and every phase listed after it again (they read world 1's new saves). After a restore the
# module is the pre-run one: deploy runs again first (the backup manifest is kept, so the restore target stays the original install), and
# World1 puts the run's saves back into Game Saves.
if ($Resume) {
    if ($Phases -notcontains 'world1') { throw '-Resume resumes world1: -Phases must list world1.' }
    if (Restored-SinceDeploy) { $rest = @($Phases | Where-Object { $_ -ne 'deploy' }); $i = [Array]::IndexOf([string[]]$rest, 'world1'); $Phases = @($rest | Select-Object -First $i) + @('deploy') + @($rest | Select-Object -Skip $i); $Redo = @($Redo) + @('deploy') }
    $i = [Array]::IndexOf([string[]]$Phases, 'world1')
    $Redo = @($Redo) + @($Phases | Select-Object -Skip $i)
    Write-Output "resume: phases $($Phases -join ','); again: $(@($Redo | Select-Object -Unique) -join ',')"
}
$touchesInstall = @('deploy', 'baseline', 'world1', 'world2', 'mods', 'restore', 'decallab', 'hangrepro')
if (($Phases | Where-Object { $touchesInstall -contains $_ }) -and !$state.Contains('claim')) { throw '-Claim is required before the run touches the shared install: the CLAIM sent to the battle-scale session (or "no partner alive: <how it was checked>").' }
try {
    foreach ($p in $Phases) {
        if (Phase-Done $p) { Write-Output "phase ${p}: done earlier, skipped"; continue }
        Write-Output "phase ${p}: start $(Now)"
        try {
            switch ($p) {
                'build' { Build } 'preflight' { Preflight } 'deploy' { Deploy } 'baseline' { Baseline } 'world1' { World1 } 'world2' { World2 } 'mods' { Mods } 'restore' { Restore } 'summarize' { Summarize } 'decallab' { DecalLab } 'hangrepro' { HangRepro }
                default { throw "Unknown phase $p" }
            }
        }
        catch { Mark $p 'error' ([ordered]@{ error = $_.Exception.Message; at = $_.InvocationInfo.PositionMessage }); Write-Output "phase ${p}: ERROR $($_.Exception.Message)"; if ($script:fatal -or $p -in @('build', 'deploy', 'restore')) { throw } }
        Write-Output "phase ${p}: $($state['phases'][$p]['status']) $(Now)"
    }
}
finally {
    Free-Lock
    Save-State
    Stop-Transcript | Out-Null
}
Write-Output "acceptance run ${RunId}: $statePath"
