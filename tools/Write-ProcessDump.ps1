param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [Parameter(Mandatory = $true)][string]$Path,
    [int]$TimeoutSeconds = 900
)
# MMR-247 (K): a full memory dump of a running process with the dumper Windows ships (rundll32 comsvcs.dll MiniDump <pid> <file> full,
# MiniDumpWithFullMemory; works on a process of the same user without a debugger installed). Waits for rundll32 to exit, then until the file
# stops growing (three equal sizes 2 s apart), and checks the MDMP header and the full-memory flag. Returns a record: path, bytes, seconds,
# exit, flags, ok, error, and from just before the dump threadStates (state/wait reason counts) and debuggerAttached.
# Used by Run-Map2Acceptance.ps1 before it kills its own blocked game (logs/hang-<pid>.dmp) and by Test-HangDump.ps1. Never stops the process.
$ErrorActionPreference = 'Stop'
$row = [ordered]@{ path = $Path; pid = $ProcessId; ok = $false; bytes = 0; seconds = 0; exit = $null; error = $null }
$started = Get-Date
try {
    if ($Path -match '\s') { throw "the dump path has a space (comsvcs.dll MiniDump splits its arguments on spaces): $Path" }
    $target = Get-Process -Id $ProcessId -ErrorAction Stop
    $folder = Split-Path $Path
    New-Item -ItemType Directory -Force $folder | Out-Null
    if (Test-Path -LiteralPath $Path) { [IO.File]::Delete($Path) }
    # A full dump is about the process's committed memory; keep a margin on the disk.
    $need = [int64]$target.PrivateMemorySize64 + [int64]$target.WorkingSet64 + 2GB
    $free = (Get-PSDrive -Name ([IO.Path]::GetPathRoot((Resolve-Path -LiteralPath $folder).Path).Substring(0, 1))).Free
    $row['privateBytes'] = [int64]$target.PrivateMemorySize64; $row['diskFree'] = [int64]$free
    if ($free -lt $need) { throw "not enough disk for a full dump: $([math]::Round($free / 1GB, 1)) GB free, about $([math]::Round($need / 1GB, 1)) GB needed" }
    # Before the dump (the dumper suspends every thread while it writes): each thread's state and wait reason (Suspended = held by
    # SuspendThread or a debugger), and whether a debugger is attached (its debug event would freeze the whole process).
    $states = [ordered]@{}
    foreach ($t in @($target.Threads)) { $k = $(try { if ([string]$t.ThreadState -eq 'Wait') { 'Wait/' + $t.WaitReason } else { [string]$t.ThreadState } } catch { '?' }); $states[$k] = 1 + [int]$states[$k] }
    $row['threadStates'] = $states
    if (-not ('MNBL.DumpNative' -as [type])) { Add-Type -Namespace MNBL -Name DumpNative -MemberDefinition '[DllImport("kernel32.dll", SetLastError = true)] public static extern bool CheckRemoteDebuggerPresent(IntPtr process, out bool present);' }
    $present = $false; $row['debuggerAttached'] = $(if ([MNBL.DumpNative]::CheckRemoteDebuggerPresent($target.Handle, [ref]$present)) { $present } else { $null })
    # Started without the shell: through Start-Process (ShellExecute) comsvcs ignores "full" and writes a stack-only minidump (flags 0x200000).
    $si = New-Object Diagnostics.ProcessStartInfo (Join-Path $env:SystemRoot 'System32\rundll32.exe'), "$(Join-Path $env:SystemRoot 'System32\comsvcs.dll'), MiniDump $ProcessId $Path full"
    $si.UseShellExecute = $false; $si.CreateNoWindow = $true
    $p = [Diagnostics.Process]::Start($si)
    $deadline = $started.AddSeconds($TimeoutSeconds)
    if (!$p.WaitForExit([int][Math]::Max(1000, ($deadline - (Get-Date)).TotalMilliseconds))) { $row['error'] = "rundll32 still writing after $TimeoutSeconds s (left running)"; return $row }
    $row['exit'] = $p.ExitCode
    $last = -1; $same = 0
    while ((Get-Date) -lt $deadline -and $same -lt 3) {
        $size = $(if (Test-Path -LiteralPath $Path) { (Get-Item -LiteralPath $Path).Length } else { -1 })
        if ($size -eq $last -and $size -gt 0) { $same++ } else { $same = 0 }
        $last = $size
        if ($same -lt 3) { Start-Sleep -Seconds 2 }
    }
    if (!(Test-Path -LiteralPath $Path)) { throw "rundll32 comsvcs.dll MiniDump wrote no file (exit $($row['exit']))" }
    $row['bytes'] = (Get-Item -LiteralPath $Path).Length
    # comsvcs gives the file a DACL of SYSTEM and Administrators only (unreadable from a non-elevated session); its owner, this user, gives itself full control (to read it, and to delete it later).
    $item = Get-Item -LiteralPath $Path; $acl = $item.GetAccessControl('Access')
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule([Security.Principal.WindowsIdentity]::GetCurrent().Name, 'FullControl', 'Allow'))); $item.SetAccessControl($acl)
    $head = New-Object byte[] 32
    $fs = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite'); try { $null = $fs.Read($head, 0, 32) } finally { $fs.Dispose() }
    if ([Text.Encoding]::ASCII.GetString($head, 0, 4) -ne 'MDMP') { throw "the file is not a minidump (header $([BitConverter]::ToString($head, 0, 4)))" }
    $flags = [BitConverter]::ToUInt64($head, 24); $row['flags'] = '0x' + $flags.ToString('x')
    if (($flags -band 2) -eq 0) { throw "the dump has no full memory (MINIDUMP_TYPE $($row['flags']))" }
    $row['ok'] = $true
}
catch { $row['error'] = [string]$_.Exception.Message }
finally { $row['seconds'] = [math]::Round(((Get-Date) - $started).TotalSeconds, 1) }
return $row
