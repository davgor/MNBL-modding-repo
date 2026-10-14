<#
.SYNOPSIS
  Runs one MNBL.ProceduralWorld autorun probe inside Bannerlord without manual UI interaction.

.DESCRIPTION
  Writes <store>/autorun/request.json, launches Bannerlord.exe /singleplayer with an explicit module list,
  waits for <store>/autorun/result-<pid>.json and the game's own exit, dismisses the game's Safe Mode prompt
  and the watchdog crash prompt with "No" only (never "Yes", nothing is collected or uploaded), kills only the
  processes it launched on timeout, and writes evidence/runtime/in-game/harness-<probe>-<pid>.json.
  No system settings, registry values or official game files are changed.

.EXAMPLE
  tools/Invoke-InGameProbe.ps1 -Probe noop
  tools/Invoke-InGameProbe.ps1 -Probe scene_query -ArgsJson '{"sceneName":"battle_terrain_001","points":[[50,80]]}'
#>
param(
    [Parameter(Mandatory = $true)][string]$Probe,
    [string]$ArgsJson = '{}',
    [int]$TimeoutSec = 600,
    [switch]$Build,
    [switch]$Deploy,
    [string]$GameRoot = 'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord',
    # Same order the official launcher wrote for the earlier successful runs (rgl_log "Command Args").
    [string[]]$Modules = @('Native', 'SandBoxCore', 'CustomBattle', 'Sandbox', 'StoryMode', 'NavalDLC', 'MNBL.ProceduralWorld'),
    [double]$MenuDelaySec = 3,
    [int]$ExitGraceSec = 90,
    # How the module quits after writing its result (all official calls): exit_button = MBInitialScreenBase.DoExitButtonAction
    # (the "Exit Game" menu action), delayed_exit = Utilities.DoDelayedexit(0), quit_game = Utilities.QuitGame.
    # Measured on 1.4.8.119303: all three unload the module cleanly and the process exit code is -1 in every case.
    [ValidateSet('exit_button', 'delayed_exit', 'quit_game')][string]$QuitMethod = 'exit_button',
    [string]$EvidenceDirectory = ''
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
if (-not $EvidenceDirectory) { $EvidenceDirectory = Join-Path $repo 'evidence/runtime/in-game' }
if ($Probe -notmatch '^[a-z_]+(:[0-9]+)?$') { throw "Invalid probe id '$Probe'." }
try { $parsedArgs = $ArgsJson | ConvertFrom-Json } catch { throw "ArgsJson is not valid JSON: $($_.Exception.Message)" }
if ($ArgsJson.Trim() -notmatch '^\{') { throw 'ArgsJson must be a JSON object.' }
if ($Modules -notcontains 'MNBL.ProceduralWorld' -or $Modules -notcontains 'NavalDLC') { throw 'Module list must include NavalDLC and MNBL.ProceduralWorld.' }

$client = Join-Path $GameRoot 'bin\Win64_Shipping_Client'
$exe = Join-Path $client 'Bannerlord.exe'
$logDirectory = 'C:\ProgramData\Mount and Blade II Bannerlord\logs'
$store = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MNBL.ProceduralWorld'
$autorun = Join-Path $store 'autorun'

function Get-GameProcesses {
    @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -in @('Bannerlord', 'Bannerlord.Native', 'TaleWorlds.MountAndBlade.Launcher', 'Launcher.Native') -or
        ($_.ProcessName -eq 'Watchdog' -and $(try { $_.Path -like "$GameRoot*" } catch { $false }))
    })
}
$running = @(Get-GameProcesses)
if ($running.Count) { throw "Bannerlord is already running (PIDs $($running.Id -join ', ')); refusing to launch another instance." }

if ($Build) { & (Join-Path $PSScriptRoot 'Build.ps1') -GameRoot $GameRoot; if (-not $?) { throw 'Build failed.' } }
if ($Deploy) { & (Join-Path $PSScriptRoot 'Deploy-Dev.ps1') -GameRoot $GameRoot | Out-Host }

# Every listed module must resolve to an installed descriptor with that exact id.
$moduleFolders = @{}
foreach ($descriptor in Get-ChildItem (Join-Path $GameRoot 'Modules') -Directory | ForEach-Object { Join-Path $_.FullName 'SubModule.xml' } | Where-Object { Test-Path -LiteralPath $_ }) {
    $id = ([xml](Get-Content -LiteralPath $descriptor -Raw)).Module.Id.value
    $moduleFolders[$id] = Split-Path $descriptor
}
foreach ($id in $Modules) { if (-not $moduleFolders.ContainsKey($id)) { throw "Module id '$id' is not installed under $GameRoot\Modules." } }
$deployedDll = Join-Path $moduleFolders['MNBL.ProceduralWorld'] 'bin\Win64_Shipping_Client\MNBL.ProceduralWorld.dll'
$deployedHash = (Get-FileHash -LiteralPath $deployedDll -Algorithm SHA256).Hash.ToLowerInvariant()

if (-not ('MnblWin' -as [type])) { Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class MnblWin
{
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, StringBuilder l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    public static List<IntPtr> TopLevel()
    {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => { list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
    public static List<IntPtr> Children(IntPtr parent)
    {
        var list = new List<IntPtr>();
        EnumChildWindows(parent, (h, l) => { list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
    public static uint Owner(IntPtr hwnd) { uint pid; GetWindowThreadProcessId(hwnd, out pid); return pid; }
    public static string ClassOf(IntPtr hwnd) { var b = new StringBuilder(256); GetClassName(hwnd, b, b.Capacity); return b.ToString(); }
    // WM_GETTEXT is permitted across integrity levels; GetWindowText cannot read controls of other processes.
    public static string Text(IntPtr hwnd)
    {
        var b = new StringBuilder(2048); IntPtr result;
        if (SendMessageTimeout(hwnd, 0x000D, (IntPtr)b.Capacity, b, 0x0002, 1000, out result) != IntPtr.Zero && b.Length > 0) return b.ToString();
        b.Clear(); GetWindowText(hwnd, b, b.Capacity); return b.ToString();
    }
    // Brings a dialog to the foreground; returns true only when it really is the foreground window.
    public static bool Activate(IntPtr hwnd)
    {
        var foreground = GetForegroundWindow();
        uint ignored;
        uint foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out ignored);
        uint self = GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != self && AttachThreadInput(self, foregroundThread, true);
        try { ShowWindow(hwnd, 5); BringWindowToTop(hwnd); SetForegroundWindow(hwnd); }
        finally { if (attached) AttachThreadInput(self, foregroundThread, false); }
        if (GetForegroundWindow() == hwnd) return true;
        // A bare Alt tap releases the foreground lock for the next SetForegroundWindow call.
        keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero);
        SetForegroundWindow(hwnd);
        return GetForegroundWindow() == hwnd;
    }
    // Alt+N = the message box's "&No" accelerator. Never Enter (could be a default Yes) and never Alt+Y.
    public static void AltN()
    {
        keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x4E, 0, 0, UIntPtr.Zero);
        keybd_event(0x4E, 0, 2, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero);
    }
}
'@ }

function Read-Shared([string]$Path) { $fs = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite, Delete'); try { @((New-Object IO.StreamReader($fs)).ReadToEnd() -split "`r?`n") } finally { $fs.Dispose() } }
$events = New-Object System.Collections.ArrayList
$clock = [Diagnostics.Stopwatch]::StartNew()
function Add-Event([string]$Kind, [hashtable]$Data) {
    $entry = [ordered]@{ utc = [DateTime]::UtcNow.ToString('O'); tSec = [Math]::Round($clock.Elapsed.TotalSeconds, 2); kind = $Kind }
    foreach ($key in $Data.Keys) { $entry[$key] = $Data[$key] }
    [void]$events.Add($entry)
    Write-Host ("[{0,7:F1}s] {1} {2}" -f $clock.Elapsed.TotalSeconds, $Kind, (($Data.GetEnumerator() | Where-Object { $_.Key -in 'title','action','method','closed','pid','status','stage','buttons' } | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' '))
}
function Get-ProcessName([uint32]$ProcessId) { try { (Get-Process -Id $ProcessId -ErrorAction Stop).ProcessName } catch { '?' } }
function Get-Dialog([IntPtr]$Hwnd) {
    $children = @([MnblWin]::Children($Hwnd) | ForEach-Object { [ordered]@{ hwnd = $_; class = [MnblWin]::ClassOf($_); id = [MnblWin]::GetDlgCtrlID($_); text = [MnblWin]::Text($_) } })
    [ordered]@{
        hwnd = ('0x{0:X}' -f $Hwnd.ToInt64()); ownerPid = [MnblWin]::Owner($Hwnd); ownerName = Get-ProcessName ([MnblWin]::Owner($Hwnd))
        class = [MnblWin]::ClassOf($Hwnd); title = [MnblWin]::Text($Hwnd)
        buttons = @($children | Where-Object { $_.class -eq 'Button' } | ForEach-Object { $_.text })
        texts = @($children | Where-Object { $_.class -ne 'Button' -and $_.text } | ForEach-Object { $_.text })
        noButton = @($children | Where-Object { $_.class -eq 'Button' -and $_.text -match '^&?No$' } | Select-Object -First 1)
    }
}
# Answers a dialog with "No" only. Tries WM_COMMAND(IDNO-style id) first (same integrity), then activation + Alt+N.
function Close-WithNo([IntPtr]$Hwnd, [hashtable]$Info, [string]$Reason, [bool]$AllowBlindAltN) {
    $no = $Info.noButton
    if (-not $no -and -not $AllowBlindAltN) { Add-Event 'dialog_left_untouched' @{ reason = "$Reason (no 'No' button found)"; title = $Info.title; buttons = ($Info.buttons -join '|') }; return $false }
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        if ($no) {
            [void][MnblWin]::PostMessage($Hwnd, 0x0111, [IntPtr]$no[0].id, $no[0].hwnd)
            Start-Sleep -Milliseconds 1500
            if (-not [MnblWin]::IsWindow($Hwnd)) { Add-Event 'dialog_answered_no' @{ reason = $Reason; title = $Info.title; method = 'WM_COMMAND(No)'; attempt = $attempt; closed = $true }; return $true }
        }
        if ([MnblWin]::Activate($Hwnd)) {
            [MnblWin]::AltN()
            Start-Sleep -Milliseconds 2000
            if (-not [MnblWin]::IsWindow($Hwnd)) { Add-Event 'dialog_answered_no' @{ reason = $Reason; title = $Info.title; method = 'activate+Alt+N'; attempt = $attempt; closed = $true }; return $true }
        } else {
            Add-Event 'dialog_activation_failed' @{ reason = $Reason; title = $Info.title; attempt = $attempt }
            Start-Sleep -Milliseconds 1000
        }
    }
    Add-Event 'dialog_not_closed' @{ reason = $Reason; title = $Info.title; closed = $false }
    return $false
}

# --- request -------------------------------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $autorun | Out-Null
$requestPath = Join-Path $autorun 'request.json'
if (Test-Path -LiteralPath $requestPath) {
    $stale = Join-Path $autorun ("request.stale-{0:yyyyMMddHHmmss}.json" -f (Get-Date))
    Move-Item -LiteralPath $requestPath -Destination $stale
    Add-Event 'stale_request_archived' @{ file = (Split-Path $stale -Leaf) }
}
$requestId = [Guid]::NewGuid().ToString('N')
$created = [DateTime]::UtcNow
$request = '{"schema":"mnbl.autorun-request","version":1,"requestId":"' + $requestId + '","probe":"' + $Probe + '","quit":true,"quitMethod":"' + $QuitMethod + '"' +
    ',"menuDelaySeconds":' + $MenuDelaySec.ToString([Globalization.CultureInfo]::InvariantCulture) +
    ',"probeTimeoutSeconds":' + $TimeoutSec + ',"createdUtc":"' + $created.ToString('O') + '","expiresUtc":"' + $created.AddMinutes(10).ToString('O') +
    '","args":' + $ArgsJson.Trim() + '}'
$null = $request | ConvertFrom-Json
$moduleLog = Join-Path $store 'module.log'
$moduleLogOffset = if (Test-Path -LiteralPath $moduleLog) { (Get-Item -LiteralPath $moduleLog).Length } else { 0 }
# Pre-existing CSRSS dialogs are never attributed to this run.
$preexisting = @{}
foreach ($h in [MnblWin]::TopLevel()) { if ([MnblWin]::IsWindowVisible($h) -and [MnblWin]::ClassOf($h) -eq '#32770') { $preexisting[$h.ToInt64()] = $true } }
[IO.File]::WriteAllText($requestPath, $request, (New-Object Text.UTF8Encoding($false)))

# --- launch --------------------------------------------------------------------------------------------
$arguments = '/singleplayer _MODULES_*' + ($Modules -join '*') + '*_MODULES_'
$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName = $exe
$start.Arguments = $arguments
$start.WorkingDirectory = $client
$start.UseShellExecute = $false
$launchUtc = [DateTime]::UtcNow
$clock.Restart()
$game = [Diagnostics.Process]::Start($start)
$null = $game.Handle  # keeps the handle so ExitCode is available after exit
$gamePid = $game.Id
Add-Event 'launched' @{ pid = $gamePid; commandLine = "`"$exe`" $arguments"; workingDirectory = $client }

# Ownership is held through open process handles, never bare PIDs: a PID can be reused by an unrelated process.
$owned = @{ $gamePid = 'Bannerlord.exe (launched)' }
$ownedProc = @{ $gamePid = $game }
function Test-Alive($Process) { try { -not $Process.HasExited } catch { $false } }
function Get-AliveOwned { @($ownedProc.GetEnumerator() | Where-Object { Test-Alive $_.Value } | ForEach-Object { $_.Key }) }
function Stop-Owned([int]$ProcessId, [string]$Reason) {
    try { $ownedProc[$ProcessId].Kill(); $script:killed += $ProcessId; Add-Event 'killed_owned_process' @{ pid = $ProcessId; reason = $Reason } }
    catch { Add-Event 'kill_failed' @{ pid = $ProcessId; reason = $Reason; error = $_.Exception.Message } }
}
$resultPath = Join-Path $autorun "result-$gamePid.json"
$progressPath = Join-Path $autorun "progress-$gamePid.log"
$watchdogLog = Join-Path $logDirectory "watchdog_log_$gamePid.txt"
$handledDialogs = @{}
$resultFinalAt = $null; $exitAt = $null; $exitCode = $null; $killed = @(); $timedOut = $false
$crashReported = $false; $crashDismissed = $false; $lastDescendantScan = -10.0; $exitedAndQuietSince = $null
$deadline = $TimeoutSec + 180  # module load + menu time on top of the probe budget

try {
while ($true) {
    $t = $clock.Elapsed.TotalSeconds
    # Descendants are discovered only while the game runs, only under a live owned parent, and only when created
    # after that parent started; each is then held by handle.
    if ($t - $lastDescendantScan -ge 2 -and -not $game.HasExited) {
        $lastDescendantScan = $t
        $all = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, Name, CommandLine, CreationDate -ErrorAction SilentlyContinue)
        do {
            $added = $false
            foreach ($p in $all) {
                $childId = [int]$p.ProcessId; $parentId = [int]$p.ParentProcessId
                if ($owned.ContainsKey($childId) -or -not $ownedProc.ContainsKey($parentId) -or -not (Test-Alive $ownedProc[$parentId])) { continue }
                try {
                    if ($p.CreationDate -lt $ownedProc[$parentId].StartTime.AddSeconds(-1)) { continue }
                    $child = [Diagnostics.Process]::GetProcessById($childId); $null = $child.Handle
                    if ([Math]::Abs(($child.StartTime - $p.CreationDate).TotalSeconds) -gt 2) { continue }
                } catch { continue }
                $owned[$childId] = "$($p.Name) (child of $parentId)"; $ownedProc[$childId] = $child; $added = $true
                Add-Event 'owned_process' @{ pid = $childId; name = $p.Name; parentPid = $parentId; commandLine = $p.CommandLine }
            }
        } while ($added)
    }
    if (-not $crashReported -and (Test-Path -LiteralPath $watchdogLog)) {
        if (Select-String -LiteralPath $watchdogLog -Pattern 'Crash occurred' -SimpleMatch -Quiet) {
            $crashReported = $true
            $lastStage = if (Test-Path -LiteralPath $progressPath) { [string](Read-Shared $progressPath | Where-Object { $_.Trim() } | Select-Object -Last 1) } else { $null }
            Add-Event 'watchdog_crash_reported' @{ pid = $gamePid; stage = $lastStage }
        }
    }
    foreach ($h in [MnblWin]::TopLevel()) {
        $key = $h.ToInt64()
        if ($handledDialogs.ContainsKey($key) -or $preexisting.ContainsKey($key)) { continue }
        if (-not [MnblWin]::IsWindowVisible($h) -or [MnblWin]::ClassOf($h) -ne '#32770') { continue }
        $ownerPid = [int][MnblWin]::Owner($h)
        $ownerName = Get-ProcessName $ownerPid
        $isOwned = $ownedProc.ContainsKey($ownerPid) -and (Test-Alive $ownedProc[$ownerPid])
        $isCsrss = $ownerName -eq 'csrss'
        if (-not $isOwned -and -not $isCsrss) { continue }
        # A CSRSS-hosted box is only attributed to this run once our game's watchdog log reports the crash.
        if ($isCsrss -and -not $crashReported) { continue }
        Start-Sleep -Milliseconds 500   # let the dialog finish creating its controls
        if (-not [MnblWin]::IsWindow($h)) { continue }
        $info = Get-Dialog $h
        $allText = ($info.title + ' ' + ($info.texts -join ' ')).ToLowerInvariant()
        if ($isOwned -and ($info.title -eq 'Safe Mode' -or $allText -match 'safe mode')) {
            $handledDialogs[$key] = $true
            Add-Event 'dialog_seen' @{ title = $info.title; texts = $info.texts; buttons = ($info.buttons -join '|'); ownerPid = $ownerPid; ownerName = $ownerName }
            [void](Close-WithNo $h $info 'safe-mode prompt of launched game (No = start normally)' $false)
        } else {
            $handledDialogs[$key] = $true
            Add-Event 'dialog_seen' @{ title = $info.title; texts = $info.texts; buttons = ($info.buttons -join '|'); ownerPid = $ownerPid; ownerName = $ownerName }
            $crashLike = $isCsrss -or $allText -match 'crash|dump|report|upload'
            if ($crashLike) {
                # The CSRSS-hosted prompt's controls are not always readable; Alt+N was verified on this machine before.
                if (Close-WithNo $h $info 'crash prompt of launched game (No = no dump, no upload)' $true) { $crashDismissed = $true }
            } else {
                Add-Event 'dialog_left_untouched' @{ reason = 'unrecognised dialog of launched game'; title = $info.title }
            }
        }
    }
    if (-not $resultFinalAt -and (Test-Path -LiteralPath $resultPath)) {
        try {
            $snapshot = (Read-Shared $resultPath) -join "`n" | ConvertFrom-Json
            if ($snapshot.final) { $resultFinalAt = $clock.Elapsed.TotalSeconds; Add-Event 'result_final' @{ status = $snapshot.status } }
        } catch { }
    }
    if (-not $exitAt -and $game.HasExited) {
        $exitAt = $clock.Elapsed.TotalSeconds; $exitCode = $game.ExitCode
        Add-Event 'game_exited' @{ pid = $gamePid; exitCode = $exitCode }
    }
    if ($exitAt) {
        $alive = Get-AliveOwned
        if ($alive.Count -eq 0) { if (-not $exitedAndQuietSince) { $exitedAndQuietSince = $clock.Elapsed.TotalSeconds } }
        if ($exitedAndQuietSince -and $clock.Elapsed.TotalSeconds - $exitedAndQuietSince -ge 3) { break }
        if ($clock.Elapsed.TotalSeconds - $exitAt -gt 60) {
            foreach ($p in $alive) { Stop-Owned $p 'still alive 60 s after game exit' }
            break
        }
    }
    $overdue = (-not $exitAt) -and (($clock.Elapsed.TotalSeconds -gt $deadline) -or ($resultFinalAt -and $clock.Elapsed.TotalSeconds - $resultFinalAt -gt $ExitGraceSec))
    if ($overdue) {
        $timedOut = $true
        $reason = if ($resultFinalAt) { "no exit within $ExitGraceSec s of final result" } else { "timeout after $deadline s" }
        # A crash prompt whose watchdog log line was not seen would survive the kill; answer recognisable ones with No.
        foreach ($h in [MnblWin]::TopLevel()) {
            $key = $h.ToInt64()
            if ($handledDialogs.ContainsKey($key) -or $preexisting.ContainsKey($key) -or -not [MnblWin]::IsWindowVisible($h) -or [MnblWin]::ClassOf($h) -ne '#32770') { continue }
            if ((Get-ProcessName ([MnblWin]::Owner($h))) -ne 'csrss') { continue }
            $handledDialogs[$key] = $true
            $info = Get-Dialog $h
            Add-Event 'dialog_seen' @{ title = $info.title; texts = $info.texts; buttons = ($info.buttons -join '|'); ownerName = 'csrss'; atTimeout = $true }
            if ((($info.title + ' ' + ($info.texts -join ' ')) -match '(?i)crash|dump|bannerlord|report')) {
                if (Close-WithNo $h $info 'crash-like CSRSS prompt at timeout (No)' $false) { $crashDismissed = $true }
            } else { Add-Event 'dialog_left_untouched' @{ reason = 'unattributed CSRSS dialog at timeout'; title = $info.title } }
        }
        foreach ($p in (Get-AliveOwned)) { Stop-Owned $p $reason }
        Start-Sleep -Seconds 2
        if ($game.HasExited) { $exitAt = $clock.Elapsed.TotalSeconds; $exitCode = $game.ExitCode }
        break
    }
    Start-Sleep -Milliseconds 500
}
} catch {
    # Never leave a launched game behind when the harness itself fails.
    foreach ($p in (Get-AliveOwned)) { try { $ownedProc[$p].Kill(); Write-Host "harness error: killed owned PID $p" } catch { } }
    throw
}

# A crash prompt can outlive the process it belongs to; answer it before leaving.
if ($crashReported -and -not $crashDismissed) {
    foreach ($h in [MnblWin]::TopLevel()) {
        if ($preexisting.ContainsKey($h.ToInt64()) -or -not [MnblWin]::IsWindowVisible($h) -or [MnblWin]::ClassOf($h) -ne '#32770') { continue }
        if ((Get-ProcessName ([MnblWin]::Owner($h))) -ne 'csrss') { continue }
        $info = Get-Dialog $h
        Add-Event 'dialog_seen' @{ title = $info.title; texts = $info.texts; buttons = ($info.buttons -join '|'); ownerName = 'csrss'; late = $true }
        if (Close-WithNo $h $info 'crash prompt left after exit (No)' $true) { $crashDismissed = $true }
    }
}
$remainingOwned = @(Get-AliveOwned)
$remainingGame = @(Get-GameProcesses)

# --- evidence ------------------------------------------------------------------------------------------
function Read-Lines([string]$Path) { if (Test-Path -LiteralPath $Path) { @(Read-Shared $Path | Where-Object { $_.Trim() -ne '' }) } else { @() } }
$result = $null
if (Test-Path -LiteralPath $resultPath) { try { $result = (Read-Shared $resultPath) -join "`n" | ConvertFrom-Json } catch { $result = "unparseable: $($_.Exception.Message)" } }
$progress = @(Read-Lines $progressPath)
$moduleLines = @()
if (Test-Path -LiteralPath $moduleLog) {
    $stream = [IO.File]::Open($moduleLog, 'Open', 'Read', 'ReadWrite')
    try { [void]$stream.Seek($moduleLogOffset, 'Begin'); $reader = New-Object IO.StreamReader($stream); $moduleLines = @($reader.ReadToEnd() -split "`n" | Where-Object { $_.Trim() }) } finally { $stream.Dispose() }
}
$raw = Join-Path $repo "build/in-game-probe/$gamePid"
New-Item -ItemType Directory -Force -Path $raw | Out-Null
$copied = @()
foreach ($file in @($resultPath, $progressPath, (Join-Path $autorun "request.$gamePid.taken.json"), (Join-Path $logDirectory "rgl_log_$gamePid.txt"), (Join-Path $logDirectory "rgl_log_errors_$gamePid.txt"), $watchdogLog)) {
    if (Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination $raw -Force; $copied += (Split-Path $file -Leaf) }
}
$dumps = Join-Path $autorun "terrain-$gamePid"
if (Test-Path -LiteralPath $dumps) { Copy-Item -LiteralPath $dumps -Destination $raw -Recurse -Force; $copied += "terrain-$gamePid/" }

$status = if ($result -and $result.PSObject.Properties['status']) { $result.status } else { $null }
$final = [bool]($result -and $result.PSObject.Properties['final'] -and $result.final)
# Bannerlord 1.4.8 returns -1 from every official quit path, so a clean shutdown is judged by its markers instead:
# the module's own submodule_unloaded stage, its module.log "unloaded" record for this PID, and no watchdog crash.
$unloadedStage = [bool]($progress | Where-Object { $_ -match ' submodule_unloaded$' })
$unloadedLog = [bool]($moduleLines | Where-Object { $_ -match '"state":"unloaded"' -and $_ -match ('"processId":' + $gamePid + '\b') })
$cleanShutdown = [bool]$exitAt -and -not $crashReported -and -not $killed.Count -and $unloadedStage -and $unloadedLog
$outcome = if ($crashReported) { 'crashed' } elseif ($timedOut) { 'timeout' } elseif (-not $final) { 'no_result' } elseif ($status -ne 'ok') { "probe_$status" } elseif (-not $cleanShutdown) { 'unclean_exit' } else { 'ok' }
$lastStage = if ($progress.Count) { $progress[-1] } else { $null }
# Wall-clock offsets of module stages from the launch (progress lines start with an ISO-8601 UTC stamp).
function Get-StageOffset([string]$Pattern) {
    $line = $progress | Where-Object { $_ -match $Pattern } | Select-Object -First 1
    if (-not $line) { return $null }
    $stamp = [DateTime]::Parse(($line -split ' ')[0], [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AdjustToUniversal)
    [Math]::Round(($stamp - $launchUtc).TotalSeconds, 2)
}
$evidence = [ordered]@{
    schema = 'mnbl.in-game-harness'; version = 1; outcome = $outcome
    probe = $Probe; args = $parsedArgs; requestId = $requestId; quitMethod = $QuitMethod
    launch = [ordered]@{ utc = $launchUtc.ToString('O'); executable = $exe; arguments = $arguments; workingDirectory = $client; commandLine = "`"$exe`" $arguments"; pid = $gamePid; deployedModuleSha256 = $deployedHash }
    store = $store; autorunDirectory = $autorun
    ownedProcesses = @($owned.GetEnumerator() | ForEach-Object { [ordered]@{ pid = $_.Key; description = $_.Value } })
    timings = [ordered]@{
        launchToRequestTakenSec = Get-StageOffset ' request_taken '
        launchToMenuSec = Get-StageOffset ' menu_visible '
        launchToProbeStartSec = Get-StageOffset ' probe_start '
        launchToProbeEndSec = Get-StageOffset ' probe_end '
        launchToFinalResultSec = if ($resultFinalAt) { [Math]::Round($resultFinalAt, 2) } else { $null }
        launchToExitSec = if ($exitAt) { [Math]::Round($exitAt, 2) } else { $null }
        resultToExitSec = if ($resultFinalAt -and $exitAt) { [Math]::Round($exitAt - $resultFinalAt, 2) } else { $null }
    }
    exit = [ordered]@{ exited = [bool]$exitAt; exitCode = $exitCode; cleanShutdown = $cleanShutdown; submoduleUnloadedStage = $unloadedStage; moduleLogUnloaded = $unloadedLog; killedByHarness = $killed; timedOut = $timedOut; remainingOwnedProcesses = $remainingOwned; remainingGameProcesses = @($remainingGame | ForEach-Object { $_.Id }) }
    crash = [ordered]@{ watchdogReportedCrash = $crashReported; promptDismissedWithNo = $crashDismissed; lastStage = $lastStage; uploaded = $false; dumpRequested = $false }
    dialogs = $events
    progress = $progress
    result = $result
    logs = [ordered]@{
        moduleLogSinceLaunch = $moduleLines
        watchdog = Read-Lines $watchdogLog
        rglErrors = Read-Lines (Join-Path $logDirectory "rgl_log_errors_$gamePid.txt")
        rglTail = @(Read-Lines (Join-Path $logDirectory "rgl_log_$gamePid.txt") | Select-Object -Last 40)
    }
    rawCopies = [ordered]@{ directory = $raw; files = $copied }
}
New-Item -ItemType Directory -Force -Path $EvidenceDirectory | Out-Null
$evidencePath = Join-Path $EvidenceDirectory ("harness-{0}-{1}.json" -f ($Probe -replace '[^a-z0-9_]', '-'), $gamePid)
$evidence | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $evidencePath -Encoding UTF8
$exitText = if ($null -ne $exitCode) { $exitCode } else { 'none' }
$summary = "probe=$Probe pid=$gamePid outcome=$outcome status=$status exit=$exitText crash=$crashReported dismissed=$crashDismissed result_s=$(if ($resultFinalAt) { [Math]::Round($resultFinalAt,1) } else { '-' }) exit_s=$(if ($exitAt) { [Math]::Round($exitAt,1) } else { '-' }) last_stage=`"$lastStage`" evidence=$evidencePath"
Write-Output $summary
if ($outcome -ne 'ok') { exit 1 }
