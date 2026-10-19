# MMR-1: the one-shot acceptance scenario on the clean local package, with the development module and store unavailable. The package is
# installed alone in place of the development module (moved to <game drive>\MNBL-mmr1-aside\dev-module), the per-user store starts empty
# (the development store renamed to MNBL.ProceduralWorld.mmr1-dev beside it), and the battle-scale lock is held for the whole sequence;
# every swap is undone at the end (-Restore undoes an interrupted sequence). Each step is one game process started by this script
# (tools/Start-OwnedProbe.ps1), so every load after the first is a cold restart:
#   1. first_test_profile: identities; world A (seed 42) from the New Campaign screen, its culture, character creation, the map; A saved and
#      loaded; seed 43 refused; seed 73 (B) generated in the same process and compared with A (geography, routes).
#   2. new_campaign_flow: the preview's settlements and kingdoms against the live campaign (source-derived identities); a second generation
#      cancelled through the screen.
#   3. land_missions phase=prepare: A's designated save (named mnbl_oneshot_A_designated through copies of the scenario files, so no
#      existing save is overwritten).
#   4. naval_missions: port, ship purchase, voyages, drop-off and coastal landing, blockade and anchor, pirate battle, a voyage across
#      the seam and a pirate encounter across it, seaborne raid, defeat.
#   5. land_missions phase=run seedB=73: field battles, town, castle, village, siege, capture, hideout on A; the battle classes and the
#      crossing A lacks on B (seed 73, started from the setup screen).
#   6. campaign_soak fixtureB=false: travel by land, trade and recruitment in a generated town, a day of campaign on A.
#   7. two_world_matrix phase=create seedB=73: A played into mutable land and naval state across the seam, saved; B (seed 73) generated,
#      started and changed, saved; A then B loaded in the same process; refusals.
#   8. two_world_matrix phase=cold: a new process loads A (scene swept and re-encoded) then B and compares each with what was saved.
#   9. seam_presentation on B's matrix save: MMR-66's seam sections (display copies, camera follow and pan, fast moves, picking, tracks,
#      alignment, a town menu across the seam, the wrap budget) on a generated world. B, not A: A's only land across the seam is a
#      two-row strip with no town west of the seam, and the sections travel between two towns joined by land across it.
#  10. compat_matrix: invalid copies of A's save refused, an isolated damaged copy of A's world data refused, A's save, package and
#      bundle unchanged.
# After each step: generator processes (fwg, worldbridge) left running are recorded. Writes build\mmr1\one-shot-raw.json;
# tools/summarize_one_shot.py grades it.
param(
    [string]$GameRoot = 'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord',
    [string]$Zip,
    [string]$Manifest,
    [string]$EngineSha256 = '19387f31557ff840d14f378f6bbdf1d58fcff406ab9ff2294dfbb3e49a50b87e',
    [switch]$Restore
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$work = Join-Path $repo 'build/mmr1'
New-Item -ItemType Directory -Force $work | Out-Null
$module = Join-Path $GameRoot 'Modules/MNBL.ProceduralWorld'
$aside = Join-Path (Split-Path -Qualifier $GameRoot) '\MNBL-mmr1-aside'
$devModule = Join-Path $aside 'dev-module/MNBL.ProceduralWorld'
$store = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MNBL.ProceduralWorld'
$devStore = "$store.mmr1-dev"
$utf8 = New-Object Text.UTF8Encoding $false
function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Rel([string]$Path, [string]$Root) { $Path.Substring($Root.Length).TrimStart('\','/').Replace('\','/') }
function Stamp { [DateTime]::UtcNow.ToString('yyyyMMddHHmmss') }

# Undoes every swap that is still in place; each step on its own.
function Undo-Swaps($Log) {
    if (Test-Path -LiteralPath $devModule) {
        try {
            if (Test-Path -LiteralPath $module) { $dest = Join-Path $aside ('package-final-' + (Stamp)); New-Item -ItemType Directory -Force $dest | Out-Null; Move-Item -LiteralPath $module -Destination $dest; $Log['package moved'] = $dest }
            Move-Item -LiteralPath $devModule -Destination (Join-Path $GameRoot 'Modules'); $Log['development module restored'] = (Test-Path -LiteralPath (Join-Path $module 'deployment.json'))
        } catch { $Log['development module restored'] = 'FAILED: ' + $_.Exception.Message }
    }
    if (Test-Path -LiteralPath $devStore) {
        try {
            if (Test-Path -LiteralPath $store) { $keep = Join-Path $work ('one-shot-store-' + (Stamp)); Move-Item -LiteralPath $store -Destination $keep; $Log['one-shot store kept'] = (Rel $keep $repo) }
            Move-Item -LiteralPath $devStore -Destination $store; $Log['development store restored'] = (Test-Path -LiteralPath (Join-Path $store 'bundles'))
        } catch { $Log['development store restored'] = 'FAILED: ' + $_.Exception.Message }
    }
}
if ($Restore) {
    if (Get-Process Bannerlord,Bannerlord.Native,Watchdog -ErrorAction SilentlyContinue) { throw 'A game is running; -Restore moves the module and store only while none is.' }
    $log = [ordered]@{}; Undo-Swaps $log; $log | ConvertTo-Json; return
}
Start-Transcript -LiteralPath (Join-Path $work ('transcript-' + (Stamp) + '.log')) | Out-Null
if (!$Zip -or !$Manifest) { throw '-Zip and -Manifest are required (or -Restore).' }
if ((Test-Path -LiteralPath $devModule) -or (Test-Path -LiteralPath $devStore)) { throw 'An earlier sequence left a swap in place; run with -Restore first.' }
if (!(Test-Path -LiteralPath (Join-Path $module 'SubModule.xml'))) { throw "No development module at $module." }
$zipPath = (Resolve-Path -LiteralPath $Zip).Path
$manifestDoc = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
$raw = [ordered]@{ schema = 'mnbl.one-shot-raw'; version = 1; ticket = 'MMR-1'; startedUtc = [DateTime]::UtcNow.ToString('O'); zipSha256 = (Sha $zipPath)
                   commit = [string](& git -C $repo rev-parse HEAD); workingTreeClean = -not [string](& git -C $repo status --porcelain -- src worldgen config Modules); steps = [ordered]@{} }
function Save-Raw { [IO.File]::WriteAllText((Join-Path $work 'one-shot-raw.json'), ($raw | ConvertTo-Json -Depth 14), $utf8) }
function Keep-Log([int]$ProcessId) {
    $log = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) "Mount and Blade II Bannerlord/logs/rgl_log_$ProcessId.txt"
    $kept = Join-Path $work 'engine-logs'
    if (Test-Path -LiteralPath $log) { New-Item -ItemType Directory -Force -Path $kept | Out-Null; Copy-Item -LiteralPath $log -Destination $kept -Force }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Wait-GameFree {
    $deadline = (Get-Date).AddMinutes(30)
    while (Get-Process Bannerlord,Bannerlord.Native,Watchdog -ErrorAction SilentlyContinue) { if ((Get-Date) -gt $deadline) { throw 'A game process kept running for 30 minutes.' }; Start-Sleep -Seconds 30 }
    $engine = Sha (Join-Path $GameRoot 'bin/Win64_Shipping_Client/TaleWorlds.MountAndBlade.dll')
    if ($engine -ne $EngineSha256) { throw "The engine binary changed ($engine); not launching." }
}

function Hash-Tree([string]$Root) {
    $files = [ordered]@{}
    if (Test-Path -LiteralPath $Root) { foreach ($f in Get-ChildItem -LiteralPath $Root -Recurse -File | Sort-Object FullName) { $files[(Rel $f.FullName $Root)] = Sha $f.FullName } }
    $files
}

function Snapshot([string]$Label, [switch]$WithStore) {
    $official = [ordered]@{}
    foreach ($id in @('Native','SandBoxCore','SandBox','StoryMode','CustomBattle','NavalDLC')) { $d = Join-Path $GameRoot "Modules/$id/SubModule.xml"; if (Test-Path -LiteralPath $d) { $official[$id] = Sha $d } }
    $binaries = [ordered]@{}
    foreach ($f in Get-ChildItem -LiteralPath (Join-Path $GameRoot 'bin/Win64_Shipping_Client') -File | Where-Object { $_.Name -like 'TaleWorlds*.dll' -or $_.Name -eq 'Bannerlord.exe' } | Sort-Object Name) { $binaries[$f.Name] = Sha $f.FullName }
    $saveRoot = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Mount and Blade II Bannerlord/Game Saves'
    $saves = [ordered]@{}; foreach ($s in Get-ChildItem -LiteralPath $saveRoot -Filter '*.sav' -File | Sort-Object Name) { $saves[$s.Name] = Sha $s.FullName }
    $row = [ordered]@{ official = $official; binaries = $binaries; saves = $saves; moduleInstalled = (Test-Path -LiteralPath $module) }
    if ($WithStore) { $row['store'] = [ordered]@{}; foreach ($dir in @('bundles','worlds')) { $row['store'][$dir] = Hash-Tree (Join-Path $store $dir) } }
    $raw.steps["snapshot $Label"] = $row
    Save-Raw
}

function Install-Package {
    if (Test-Path -LiteralPath $module) { throw "$module exists; refusing to extract over it." }
    [IO.Compression.ZipFile]::ExtractToDirectory($zipPath, (Join-Path $GameRoot 'Modules'))
    $installed = @(Get-ChildItem -LiteralPath $module -Recurse -File | Sort-Object FullName | ForEach-Object { (Rel $_.FullName $module) + '=' + (Sha $_.FullName) })
    $expected = @($manifestDoc.files | ForEach-Object { $_.path + '=' + $_.sha256 })
    $diff = @(Compare-Object $expected $installed | ForEach-Object { $_.SideIndicator + ' ' + $_.InputObject })
    $raw.steps['install'] = [ordered]@{ files = $installed.Count; matchesManifest = ($diff.Count -eq 0); differences = $diff; developmentReceipt = (Test-Path -LiteralPath (Join-Path $module 'deployment.json')) }
    Save-Raw
}

# Waits for this script's own probe process (one handle for the whole wait); stopped when idle and not responding for 5 minutes, or when
# its engine log stops growing for 4 minutes before the probe starts.
function Wait-Probe([int]$ProbeId) {
    $proc = Get-Process -Id $ProbeId
    $logPath = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) "Mount and Blade II Bannerlord/logs/rgl_log_$ProbeId.txt"
    $resultPath = Join-Path $store "autorun/result-$ProbeId.json"
    $deadline = (Get-Date).AddMinutes(75); $flat = 0; $last = -1; $logSize = -1; $still = 0; $why = $null; $started = Get-Date
    while (-not $proc.HasExited -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 15
        $proc.Refresh(); if ($proc.HasExited) { break }
        $cpu = $proc.TotalProcessorTime.TotalSeconds
        if ($last -ge 0 -and ($cpu - $last) -lt 0.5 -and -not $proc.Responding) { $flat++ } else { $flat = 0 }
        $last = $cpu
        $size = if (Test-Path -LiteralPath $logPath) { (Get-Item -LiteralPath $logPath).Length } else { 0 }
        if (!(Test-Path -LiteralPath $resultPath) -and $size -eq $logSize) { $still++ } else { $still = 0 }
        $logSize = $size
        if ($flat -ge 20) { $why = 'idle and not responding for 5 minutes'; break }
        if ($still -ge 16) { $why = 'the engine log stopped growing for 4 minutes before the probe started'; break }
    }
    if (-not $proc.HasExited) { if (!$why) { $why = 'deadline' }; Stop-Process -Id $ProbeId -Force -ErrorAction SilentlyContinue; $null = $proc.WaitForExit(60000) }
    $proc.Dispose()
    @{ stopped = $why; seconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1); logTail = $(if (Test-Path -LiteralPath $logPath) { @(Get-Content -LiteralPath $logPath -Tail 3 | ForEach-Object { [string]$_ }) } else { @() }) }
}

function Run-Probe([string]$Label, [string]$Probe, [hashtable]$Arguments) {
    Wait-GameFree
    $line = & (Join-Path $PSScriptRoot 'Start-OwnedProbe.ps1') -Probe $Probe -ProbeTimeoutSeconds 3600 -ArgsJson ($Arguments | ConvertTo-Json -Compress)
    $m = [regex]::Match([string]$line, 'Owned PID (\d+)')
    if (!$m.Success) { throw "Start-OwnedProbe gave no process id: $line" }
    $probeId = [int]$m.Groups[1].Value
    $wait = Wait-Probe $probeId
    Start-Sleep -Seconds 5
    Keep-Log $probeId
    $collect = $null
    try { & (Join-Path $PSScriptRoot 'Collect-OwnedProbe.ps1') | Out-Null } catch { $collect = [string]$_.Exception.Message }
    $result = "evidence/runtime/in-game/$Probe-$probeId.json"
    $doc = if (Test-Path -LiteralPath (Join-Path $repo $result)) { Get-Content -LiteralPath (Join-Path $repo $result) -Raw | ConvertFrom-Json } else { $null }
    # Generator processes still running after the game exited: none may be left by a probe.
    $orphans = @(Get-Process fwg,worldbridge -ErrorAction SilentlyContinue | ForEach-Object { "$($_.ProcessName) $($_.Id)" })
    $raw.steps["probe $Label"] = [ordered]@{ probe = $Probe; args = $Arguments; pid = $probeId; result = $result; resultPresent = [bool]$doc; status = $(if ($doc) { $doc.status } else { $null })
                                             error = $(if ($doc -and $doc.error) { [string]$doc.error } else { $null }); stopped = $wait.stopped; seconds = $wait.seconds; collectError = $collect
                                             generatorProcessesAfter = $orphans; engineLogLastLines = $wait.logTail }
    Save-Raw
    return $doc
}

# Copies of the land and naval scenario files whose designated save is this sequence's own (no existing save is overwritten).
$scenarios = Join-Path $work 'scenarios'
New-Item -ItemType Directory -Force $scenarios | Out-Null
foreach ($name in @('LandMissionScenarios.json', 'NavalScenarios.json')) {
    $text = [IO.File]::ReadAllText((Join-Path $repo "tests/Runtime/$name"))
    if (-not $text.Contains('"mnbl_probe52_A_designated"')) { throw "$name does not name the designated save mnbl_probe52_A_designated" }
    [IO.File]::WriteAllText((Join-Path $scenarios $name), $text.Replace('"mnbl_probe52_A_designated"', '"mnbl_oneshot_A_designated"'), $utf8)
}
$landPlan = (Join-Path $scenarios 'LandMissionScenarios.json'); $navalPlan = (Join-Path $scenarios 'NavalScenarios.json')
# The sequence's designated save must not exist yet (an earlier sequence's would be overwritten).
$designated = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Mount and Blade II Bannerlord/Game Saves/mnbl_oneshot_A_designated.sav'
if (Test-Path -LiteralPath $designated) { throw "$designated exists (an earlier sequence's); move it away first." }

# The battle-scale lock, held for the whole sequence (created if it does not exist yet, so the sequence never runs unlocked).
$lockPath = Join-Path $repo 'build/battle-scale/native-test.lock'
New-Item -ItemType Directory -Force (Split-Path $lockPath) | Out-Null
$lockHandle = $null
$deadline = (Get-Date).AddMinutes(30)
while ($null -eq $lockHandle) {
    try { $lockHandle = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None') } catch { if ((Get-Date) -gt $deadline) { throw 'The battle-scale lock stayed held for 30 minutes.' }; Start-Sleep -Seconds 30 }
}
$raw.steps['battle-scale lock held'] = [ordered]@{ held = ($null -ne $lockHandle) }
$undo = [ordered]@{}
try {
    Wait-GameFree
    Snapshot 'before'
    New-Item -ItemType Directory -Force (Split-Path $devModule) | Out-Null
    Move-Item -LiteralPath $module -Destination (Split-Path $devModule)
    if (Test-Path -LiteralPath $store) { Move-Item -LiteralPath $store -Destination $devStore }
    $raw.steps['swapped'] = [ordered]@{ developmentModule = '<aside>/dev-module'; developmentStore = 'MNBL.ProceduralWorld.mmr1-dev'; cleanStoreBefore = (Test-Path -LiteralPath $store) }
    Save-Raw
    Install-Package

    $null = Run-Probe 'step1 profile' 'first_test_profile' @{ seedA = 42; seedRejected = 43; seedB = 73; culture = 'empire' }
    $null = Run-Probe 'step1 identities' 'new_campaign_flow' @{ seed = 42; culture = 'empire' }
    $null = Run-Probe 'step3 prepare' 'land_missions' @{ scenarios = $landPlan; phase = 'prepare' }
    $null = Run-Probe 'step3 naval' 'naval_missions' @{ scenarios = $navalPlan }
    $null = Run-Probe 'step2 land' 'land_missions' @{ scenarios = $landPlan; phase = 'run'; fixtureB = $false; seedB = 73 }
    $null = Run-Probe 'step2 visit' 'campaign_soak' @{ scenarios = (Join-Path $repo 'tests/Runtime/CampaignSoakScenarios.json'); fixtureB = $false }
    Snapshot 'before matrix' -WithStore
    $create = Run-Probe 'step4-5 create' 'two_world_matrix' @{ phase = 'create'; seed = 42; seedB = 73; culture = 'empire'; fixtureB = $false }
    $createResult = Join-Path $repo $raw.steps['probe step4-5 create'].result
    if ($create) { $null = Run-Probe 'step5 cold' 'two_world_matrix' @{ phase = 'cold'; previous = $createResult; fixtureB = $false; cleanup = $false } }
    else { $raw.steps['probe step5 cold'] = [ordered]@{ skipped = 'the create phase left no result' }; Save-Raw }
    $bSave = if ($create -and $create.outputs -and $create.outputs.worlds.B.save) { [string]$create.outputs.worlds.B.save } else { $null }
    if ($bSave) { $null = Run-Probe 'step4 seam' 'seam_presentation' @{ save = $bSave } }
    else { $raw.steps['probe step4 seam'] = [ordered]@{ skipped = 'the create phase left no save of world B' }; Save-Raw }
    $null = Run-Probe 'step6 refusals' 'compat_matrix' @{}
    Snapshot 'after probes' -WithStore
}
catch { $raw.steps['error'] = $_.Exception.Message + ' at ' + $_.InvocationInfo.PositionMessage; Save-Raw }
finally {
    Undo-Swaps $undo
    $raw.steps['undone'] = $undo
    if ($null -ne $lockHandle) { $lockHandle.Dispose() }
    Save-Raw
}
Snapshot 'after'
$raw.finishedUtc = [DateTime]::UtcNow.ToString('O')
Save-Raw
Write-Output "one-shot sequence finished: $(Join-Path $work 'one-shot-raw.json')"
Stop-Transcript | Out-Null
