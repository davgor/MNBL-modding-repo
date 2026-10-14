param([Parameter(Mandatory=$true)][int]$ProcessId, [string]$Log, [int]$GraceSeconds=120, [int]$MaxMinutes=60)
# Dismisses native dialogs that block an owned probe: the startup Safe Mode prompt and crash
# dialogs from Bannerlord, its Watchdog, or WerFault for those processes. Only top-level #32770
# windows of those processes are touched. Never chooses Yes/Send. Every dialog's text is logged.
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Collections.Generic; using System.Text; using System.Runtime.InteropServices;
public static class ProbeDialogs {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    public static string Text(IntPtr h) { var s = new StringBuilder(2048); GetWindowText(h, s, s.Capacity); return s.ToString(); }
    public static string Class(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, s.Capacity); return s.ToString(); }
    public static List<IntPtr> Dialogs(int[] pids) {
        var found = new List<IntPtr>(); var set = new HashSet<int>(pids);
        EnumWindows((h, l) => { int pid; GetWindowThreadProcessId(h, out pid);
            if (set.Contains(pid) && IsWindowVisible(h) && Class(h) == "#32770") found.Add(h); return true; }, IntPtr.Zero);
        return found;
    }
    public static int Owner(IntPtr h) { int pid; GetWindowThreadProcessId(h, out pid); return pid; }
    public static List<IntPtr> Children(IntPtr h) {
        var found = new List<IntPtr>(); EnumChildWindows(h, (c, l) => { found.Add(c); return true; }, IntPtr.Zero); return found;
    }
}
'@
if (!$Log) { $Log = Join-Path $PSScriptRoot "../build/probe-dialogs-$ProcessId.log" }
$prefer = @("don't send", 'no', 'close the program', 'close program', 'close', 'ok')
$attempts = @{}
function Write-Event($record) { ($record | ConvertTo-Json -Compress -Depth 4) | Add-Content -LiteralPath $Log -Encoding utf8 }
function Get-Targets {
    $ids = @($ProcessId) + @(Get-Process Watchdog -ErrorAction SilentlyContinue | ForEach-Object Id)
    $wer = @(Get-CimInstance Win32_Process -Filter "Name='WerFault.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $cmd = $_.CommandLine; $ids | Where-Object { $cmd -match "-p $_(\s|$)" } } | ForEach-Object { [int]$_.ProcessId })
    [int[]]@($ids + $wer | Select-Object -Unique)
}
function Normalize([string]$name) { ($name -replace '&','').Trim().ToLowerInvariant() }
function Handle-Dialog([IntPtr]$handle) {
    $key = $handle.ToInt64(); $attempts[$key] = 1 + [int]$attempts[$key]
    if ($attempts[$key] -gt 20) { return }
    $title = [ProbeDialogs]::Text($handle); $owner = [ProbeDialogs]::Owner($handle)
    $children = [ProbeDialogs]::Children($handle)
    $buttons = @($children | Where-Object { [ProbeDialogs]::Class($_) -eq 'Button' -and [ProbeDialogs]::Text($_) })
    $texts = @($children | Where-Object { [ProbeDialogs]::Class($_) -eq 'Static' -and [ProbeDialogs]::Text($_) } | ForEach-Object { [ProbeDialogs]::Text($_) })
    $names = @($buttons | ForEach-Object { [ProbeDialogs]::Text($_) })
    $order = if ($title -eq 'Safe Mode') { @('no') } else { $prefer }
    $clicked = $null
    foreach ($want in $order) {
        $match = $buttons | Where-Object { (Normalize ([ProbeDialogs]::Text($_))) -eq $want } | Select-Object -First 1
        if ($match) { [void][ProbeDialogs]::SendMessage($match, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero); $clicked = [ProbeDialogs]::Text($match); break }
    }
    if (!$clicked) {
        # Task dialogs draw buttons without child windows; UI Automation can still invoke them.
        try {
            $window = [Windows.Automation.AutomationElement]::FromHandle($handle)
            $all = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
            $texts += @($all | Where-Object { $_.Current.ControlType -eq [Windows.Automation.ControlType]::Text -and $_.Current.Name } | ForEach-Object { $_.Current.Name })
            $uia = @($all | Where-Object { $_.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $_.Current.Name })
            $names += @($uia | ForEach-Object { $_.Current.Name })
            foreach ($want in $order) {
                $match = $uia | Where-Object { (Normalize $_.Current.Name) -eq $want } | Select-Object -First 1
                if ($match) { $match.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); $clicked = $match.Current.Name; break }
            }
        } catch { }
    }
    if (!$clicked) { [void][ProbeDialogs]::PostMessage($handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero); $clicked = '<WM_CLOSE>' }
    Write-Event ([ordered]@{ utc=[DateTime]::UtcNow.ToString('O'); probePid=$ProcessId; ownerPid=$owner;
        owner=(Get-Process -Id $owner -ErrorAction SilentlyContinue).ProcessName; title=$title; texts=$texts; buttons=$names;
        clicked=$clicked; attempt=$attempts[$key] })
}
$deadline = (Get-Date).AddMinutes($MaxMinutes); $exitedAt = $null
while ((Get-Date) -lt $deadline) {
    if (!$exitedAt -and !(Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { $exitedAt = Get-Date }
    $targets = Get-Targets
    if ($exitedAt -and (((Get-Date) - $exitedAt).TotalSeconds -gt $GraceSeconds -or $targets.Count -le 1)) { break }
    try { foreach ($handle in [ProbeDialogs]::Dialogs($targets)) { Handle-Dialog $handle } }
    catch { Write-Event ([ordered]@{ utc=[DateTime]::UtcNow.ToString('O'); probePid=$ProcessId; error=$_.Exception.Message }) }
    Start-Sleep -Milliseconds 750
}
