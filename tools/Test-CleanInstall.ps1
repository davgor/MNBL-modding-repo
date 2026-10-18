# MMR-57: the packaged module installed on its own in the game, with a clean per-user profile, then disabled, removed, reinstalled and
# installed read-only. Every move stays on its own volume (atomic renames):
#   - the development module folder goes to <game drive>\MNBL-mmr57-aside\dev-module and comes back at the end;
#   - the per-user store %LOCALAPPDATA%\MNBL.ProceduralWorld goes to MNBL.ProceduralWorld.mmr57-dev beside it, so the package starts from
#     an empty store (no bundle, navigation cache, surface template or world of the development install), and comes back at the end; the
#     sequence's own store is kept as build\mmr57\clean-store;
#   - the battle-scale session's lock (build\battle-scale\native-test.lock) is held for the whole sequence, so no other run starts while
#     the install is swapped.
# Steps:
#   1. snapshots: the official modules' descriptors, the engine binaries and the game's saves (hashes);
#   2. install: the zip extracted alone (the installed folder must be exactly the manifest's files); clean_install phase=install generates
#      and enters a world and saves it (the game launched with the official modules, Harmony and this module only);
#   3. disabled: the game booted with the module folder present but not in its module list, to its main menu;
#   4. removed: the module folder moved away; the game booted without it, to its main menu; the store's files hashed;
#   5. reinstalled: the zip extracted again (no generated scene left); clean_install phase=reload loads the save;
#   6. read-only: the zip extracted again, the module folder denied writes for this user (create, append, write attributes, delete),
#      reads and listing checked to still work and a file creation checked to fail; clean_install phase=readonly; the denial removed;
#   7. every swap undone (each step on its own, so one failure does not stop the others); the snapshots taken again.
# Before each launch: no game process, the engine's hash the tested one. A launch whose engine log stops growing before the probe starts
# is stopped (only processes this script started). -Restore undoes an interrupted sequence. Writes build\mmr57\clean-install-raw.json.
param(
    [string]$GameRoot = 'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord',
    [string]$Zip,
    [string]$Manifest,
    [int]$Seed = 42,
    [string]$EngineSha256 = '19387f31557ff840d14f378f6bbdf1d58fcff406ab9ff2294dfbb3e49a50b87e',
    [switch]$Restore
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$work = Join-Path $repo 'build/mmr57'
New-Item -ItemType Directory -Force $work | Out-Null
$module = Join-Path $GameRoot 'Modules/MNBL.ProceduralWorld'
$aside = Join-Path (Split-Path -Qualifier $GameRoot) '\MNBL-mmr57-aside'
$devModule = Join-Path $aside 'dev-module/MNBL.ProceduralWorld'
$store = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MNBL.ProceduralWorld'
$devStore = "$store.mmr57-dev"
$utf8 = New-Object Text.UTF8Encoding $false
function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Rel([string]$Path, [string]$Root) { $Path.Substring($Root.Length).TrimStart('\','/').Replace('\','/') }
function Stamp { [DateTime]::UtcNow.ToString('yyyyMMddHHmmss') }

# Undoes every swap that is still in place; each step on its own.
function Undo-Swaps($Log) {
    if (Test-Path -LiteralPath $module) {
        try { & icacls.exe $module /remove:d "$($env:USERNAME)" | Out-Null; $Log['denial removed'] = $LASTEXITCODE } catch { $Log['denial removed'] = $_.Exception.Message }
    }
    if (Test-Path -LiteralPath $devModule) {
        try {
            if (Test-Path -LiteralPath $module) { $dest = Join-Path $aside ('package-final-' + (Stamp)); New-Item -ItemType Directory -Force $dest | Out-Null; Move-Item -LiteralPath $module -Destination $dest; $Log['package moved'] = $dest }
            Move-Item -LiteralPath $devModule -Destination (Join-Path $GameRoot 'Modules'); $Log['development module restored'] = (Test-Path -LiteralPath (Join-Path $module 'deployment.json'))
        } catch { $Log['development module restored'] = 'FAILED: ' + $_.Exception.Message }
    }
    if (Test-Path -LiteralPath $devStore) {
        try {
            if (Test-Path -LiteralPath $store) { $keep = Join-Path $work ('clean-store-' + (Stamp)); Move-Item -LiteralPath $store -Destination $keep; $Log['clean store kept'] = (Rel $keep $repo) }
            Move-Item -LiteralPath $devStore -Destination $store; $Log['development store restored'] = (Test-Path -LiteralPath (Join-Path $store 'bundles'))
        } catch { $Log['development store restored'] = 'FAILED: ' + $_.Exception.Message }
    }
}
if ($Restore) { $log = [ordered]@{}; Undo-Swaps $log; $log | ConvertTo-Json; return }
# A transcript of the whole sequence (git-ignored), so an interrupted run still shows where it stopped.
Start-Transcript -LiteralPath (Join-Path $work ('transcript-' + (Stamp) + '.log')) | Out-Null
if (!$Zip -or !$Manifest) { throw '-Zip and -Manifest are required (or -Restore).' }
if ((Test-Path -LiteralPath $devModule) -or (Test-Path -LiteralPath $devStore)) { throw 'An earlier sequence left a swap in place; run with -Restore first.' }
if (!(Test-Path -LiteralPath (Join-Path $module 'SubModule.xml'))) { throw "No development module at $module." }
$zipPath = (Resolve-Path -LiteralPath $Zip).Path
$manifestDoc = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
$raw = [ordered]@{ schema = 'mnbl.clean-install-raw'; version = 2; ticket = 'MMR-57'; startedUtc = [DateTime]::UtcNow.ToString('O'); zipSha256 = (Sha $zipPath); steps = [ordered]@{} }
function Save-Raw { [IO.File]::WriteAllText((Join-Path $work 'clean-install-raw.json'), ($raw | ConvertTo-Json -Depth 14), $utf8) }
# The engine keeps a few logs only; tools/summarize_clean_install.py reads each run's log from here.
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
    if ($WithStore) { $row['store'] = [ordered]@{}; foreach ($dir in @('bundles','worlds','worldsaves','surface-template')) { $row['store'][$dir] = Hash-Tree (Join-Path $store $dir) } }
    $raw.steps["snapshot $Label"] = $row
    Save-Raw
}

function Install-Package([string]$Label) {
    if (Test-Path -LiteralPath $module) { throw "$module exists; refusing to extract over it." }
    [IO.Compression.ZipFile]::ExtractToDirectory($zipPath, (Join-Path $GameRoot 'Modules'))
    $installed = @(Get-ChildItem -LiteralPath $module -Recurse -File | Sort-Object FullName | ForEach-Object { (Rel $_.FullName $module) + '=' + (Sha $_.FullName) })
    $expected = @($manifestDoc.files | ForEach-Object { $_.path + '=' + $_.sha256 })
    $diff = @(Compare-Object $expected $installed | ForEach-Object { $_.SideIndicator + ' ' + $_.InputObject })
    $raw.steps["install $Label"] = [ordered]@{ files = $installed.Count; matchesManifest = ($diff.Count -eq 0); differences = $diff }
    Save-Raw
}

function Remove-Module([string]$Label) {
    if (!(Test-Path -LiteralPath $module)) { return }
    $dest = Join-Path $aside "removed-$Label-$(Stamp)"
    New-Item -ItemType Directory -Force $dest | Out-Null
    Move-Item -LiteralPath $module -Destination $dest
    $raw.steps["remove $Label"] = [ordered]@{ movedTo = $dest.Replace($aside, '<aside>'); sceneObj = @(Get-ChildItem -LiteralPath (Join-Path $dest 'MNBL.ProceduralWorld/SceneObj') -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }) }
    Save-Raw
}

# Waits for this script's own probe process: one handle for the whole wait (an exited process can stay listed while handles to it
# are open), stopped when idle and not responding for 5 minutes, or when its engine log stops growing for 4 minutes before the probe starts.
function Wait-Probe([int]$ProbeId) {
    $proc = Get-Process -Id $ProbeId
    $logPath = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) "Mount and Blade II Bannerlord/logs/rgl_log_$ProbeId.txt"
    $resultPath = Join-Path $store "autorun/result-$ProbeId.json"
    $deadline = (Get-Date).AddMinutes(75); $flat = 0; $last = -1; $logSize = -1; $still = 0; $why = $null
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
    # Plain strings: Get-Content's lines carry extended properties that make ConvertTo-Json recurse without bound (it hung Save-Raw).
    @{ stopped = $why; logBytes = $logSize; logTail = $(if (Test-Path -LiteralPath $logPath) { @(Get-Content -LiteralPath $logPath -Tail 3 | ForEach-Object { [string]$_ }) } else { @() }) }
}

function Run-Probe([string]$Label, [hashtable]$Arguments) {
    Wait-GameFree
    $line = & (Join-Path $PSScriptRoot 'Start-OwnedProbe.ps1') -Probe clean_install -ProbeTimeoutSeconds 3600 -ArgsJson ($Arguments | ConvertTo-Json -Compress)
    $m = [regex]::Match([string]$line, 'Owned PID (\d+)')
    if (!$m.Success) { throw "Start-OwnedProbe gave no process id: $line" }
    $probeId = [int]$m.Groups[1].Value
    $wait = Wait-Probe $probeId
    Start-Sleep -Seconds 5
    Keep-Log $probeId
    # A game that stopped before the probe ran leaves no result; that is recorded, not a failure of the sequence.
    $collect = $null
    try { & (Join-Path $PSScriptRoot 'Collect-OwnedProbe.ps1') -ProcessId $probeId | Out-Null } catch { $collect = [string]$_.Exception.Message }
    $result = Join-Path $repo "evidence/runtime/in-game/clean_install-$probeId.json"
    $doc = if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result -Raw | ConvertFrom-Json } else { $null }
    $raw.steps["probe $Label"] = [ordered]@{ pid = $probeId; result = "evidence/runtime/in-game/clean_install-$probeId.json"; resultPresent = [bool]$doc; status = $(if ($doc) { $doc.status } else { $null })
                                             stopped = $wait.stopped; collectError = $collect; engineLogBytes = $wait.logBytes; engineLogLastLines = $wait.logTail }
    Save-Raw
    return $doc
}

# The game started by this script with the official modules and Harmony only, to its main menu, then closed (its own process).
function Boot-WithoutModule([string]$Label) {
    Wait-GameFree
    $bin = Join-Path $GameRoot 'bin/Win64_Shipping_Client'
    $p = Start-Process -FilePath (Join-Path $bin 'Bannerlord.exe') -WorkingDirectory $bin -ArgumentList '/singleplayer _MODULES_*Bannerlord.Harmony*Native*SandBoxCore*CustomBattle*Sandbox*StoryMode*NavalDLC*_MODULES_' -WindowStyle Hidden -PassThru
    Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$(Join-Path $PSScriptRoot 'Watch-ProbeDialogs.ps1')`"",'-ProcessId',$p.Id) -WindowStyle Hidden | Out-Null
    $logPath = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) "Mount and Blade II Bannerlord/logs/rgl_log_$($p.Id).txt"
    $start = Get-Date; $menu = $false
    while (((Get-Date) - $start).TotalSeconds -lt 300 -and -not $p.HasExited) {
        Start-Sleep -Seconds 5
        if ((Test-Path -LiteralPath $logPath) -and (Select-String -LiteralPath $logPath -Pattern 'GauntletInitialScreen::HandleActivate' -SimpleMatch -Quiet)) { $menu = $true; break }
    }
    $seconds = ((Get-Date) - $start).TotalSeconds
    Start-Sleep -Seconds 5
    $ours = if (Test-Path -LiteralPath $logPath) { @(Select-String -LiteralPath $logPath -Pattern 'MNBL.ProceduralWorld' -SimpleMatch | Where-Object { $_.Line -notmatch 'Loading xml file|LoadWithFullPath' } | Select-Object -First 5 | ForEach-Object { [string]$_.Line }) } else { @() }
    $alive = -not $p.HasExited
    if ($alive) { $null = $p.CloseMainWindow(); $null = $p.WaitForExit(30000) }
    $graceful = $p.HasExited
    if (!$graceful) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; $null = $p.WaitForExit(30000) }
    Keep-Log $p.Id
    $raw.steps["boot $Label"] = [ordered]@{ pid = $p.Id; log = "rgl_log_$($p.Id).txt"; mainMenu = $menu; secondsToMenu = [math]::Round($seconds, 1); aliveAtMenu = $alive; closedGracefully = $graceful
                                           moduleLinesInLog = $ours; moduleFolderPresent = (Test-Path -LiteralPath $module) }
    Save-Raw
}

# This user's access to the read-only module folder, checked directly.
function Test-Access {
    $r = [ordered]@{}
    try { $null = [IO.File]::ReadAllText((Join-Path $module 'SubModule.xml')); $r['readDescriptor'] = $true } catch { $r['readDescriptor'] = $_.Exception.GetType().Name }
    try { $r['listFolder'] = @(Get-ChildItem -LiteralPath $module).Count -gt 0 } catch { $r['listFolder'] = $_.Exception.GetType().Name }
    try { $null = [IO.File]::ReadAllBytes((Join-Path $module 'bin/Win64_Shipping_Client/MNBL.ProceduralWorld.dll')); $r['readAssembly'] = $true } catch { $r['readAssembly'] = $_.Exception.GetType().Name }
    $probe = Join-Path $module ('write-test-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try { [IO.File]::WriteAllText($probe, 'x'); Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue; $r['createFile'] = $true } catch { $r['createFile'] = $false }
    try { New-Item -ItemType Directory -Path (Join-Path $module 'SceneObj/write-test') -ErrorAction Stop | Out-Null; Remove-Item -LiteralPath (Join-Path $module 'SceneObj/write-test') -Force -ErrorAction SilentlyContinue; $r['createSceneFolder'] = $true } catch { $r['createSceneFolder'] = $false }
    $r
}

$lockPath = Join-Path $repo 'build/battle-scale/native-test.lock'
$lockHandle = $null
if (Test-Path -LiteralPath $lockPath) {
    $deadline = (Get-Date).AddMinutes(30)
    while ($null -eq $lockHandle) {
        try { $lockHandle = [IO.File]::Open($lockPath, 'Open', 'ReadWrite', 'None') } catch { if ((Get-Date) -gt $deadline) { throw 'The battle-scale lock stayed held for 30 minutes.' }; Start-Sleep -Seconds 30 }
    }
}
$raw.steps['battle-scale lock held'] = [ordered]@{ held = ($null -ne $lockHandle) }
$undo = [ordered]@{}
try {
    Wait-GameFree
    Snapshot 'before'
    New-Item -ItemType Directory -Force (Split-Path $devModule) | Out-Null
    Move-Item -LiteralPath $module -Destination (Split-Path $devModule)
    if (Test-Path -LiteralPath $store) { Move-Item -LiteralPath $store -Destination $devStore }
    $raw.steps['swapped'] = [ordered]@{ developmentModule = '<aside>/dev-module'; developmentStore = 'MNBL.ProceduralWorld.mmr57-dev'; cleanStoreBefore = (Test-Path -LiteralPath $store) }
    Save-Raw

    Install-Package 'first'
    $install = Run-Probe 'install' @{ phase = 'install'; seed = $Seed; culture = 'empire' }
    $save = if ($install -and $install.outputs.save -and $install.outputs.save.published) { $install.outputs.save.name } else { $null }
    Snapshot 'installed' -WithStore
    Boot-WithoutModule 'disabled'
    Remove-Module 'after-install'
    Boot-WithoutModule 'removed'
    Snapshot 'removed' -WithStore
    Install-Package 'again'
    if ($save) { $null = Run-Probe 'reload' @{ phase = 'reload'; save = $save } } else { $raw.steps['probe reload'] = [ordered]@{ skipped = 'the install wrote no save' }; Save-Raw }
    Remove-Module 'after-reload'
    Install-Package 'read-only'
    # The denial is set through .NET, not icacls: icacls adds SYNCHRONIZE to every deny entry, even one of listed rights, and a denied
    # SYNCHRONIZE fails every open, reads included (the game then stops reading the module's descriptor).
    $denied = $null
    try {
        $rights = [Security.AccessControl.FileSystemRights]'WriteData, AppendData, WriteExtendedAttributes, WriteAttributes, Delete, DeleteSubdirectoriesAndFiles'
        $rule = New-Object Security.AccessControl.FileSystemAccessRule("$env:USERDOMAIN\$env:USERNAME", $rights, 'ContainerInherit, ObjectInherit', 'None', 'Deny')
        $folder = Get-Item -LiteralPath $module; $acl = $folder.GetAccessControl('Access'); $acl.AddAccessRule($rule); $folder.SetAccessControl($acl)
        $denied = @((Get-Acl -LiteralPath $module).Access | Where-Object { $_.AccessControlType -eq 'Deny' -and !$_.IsInherited } | ForEach-Object { [string]$_.FileSystemRights })
    } catch { $denied = 'not set: ' + [string]$_.Exception.Message }
    $access = Test-Access
    $valid = $access.readDescriptor -eq $true -and $access.listFolder -eq $true -and $access.readAssembly -eq $true -and $access.createFile -eq $false -and $access.createSceneFolder -eq $false
    $raw.steps['read-only'] = [ordered]@{ denied = 'create files and folders, append, write attributes and extended attributes, delete (inherited by every file and folder)'; denyEntries = $denied; access = $access; valid = $valid }
    Save-Raw
    if ($valid) { $null = Run-Probe 'readonly' @{ phase = 'readonly'; seed = $Seed; save = $save } }
    else { $raw.steps['probe readonly'] = [ordered]@{ skipped = 'the denial did not leave reads working and creation failing' }; Save-Raw }
    & icacls.exe $module /remove:d "$($env:USERNAME)" | Out-Null
    $raw.steps['read-only'].removed = $LASTEXITCODE
    Remove-Module 'after-readonly'
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
Write-Output "clean install sequence finished: $(Join-Path $work 'clean-install-raw.json')"
Stop-Transcript | Out-Null
