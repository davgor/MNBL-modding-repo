param(
    [Parameter(Mandatory = $true)][string]$Dump,
    [string]$Out,
    [string]$Json,
    [string]$EngineThread,
    [string]$Symbols,
    [switch]$SymbolServer,
    [int]$Frames = 40,
    [int]$TimeoutMinutes = 30
)
# MMR-247 (K): analyses a full memory dump of a hung game (Run-Map2Acceptance.ps1 writes logs/hang-<pid>.dmp before it kills a blocked game)
# with tools/hangdump/HangDump.cs: Windows' own debugger engine (System32\dbgeng.dll) and the .NET Framework's SOS, nothing installed. Writes
# <dump>.analysis.txt (findings first: Monitor deadlocks, a GC or runtime suspension in progress, exceptions in flight, the engine thread's
# native top frames; then every thread's native and managed stack, and the raw !threads, !syncblk and module list) and <dump>.analysis.json.
# -EngineThread: the engine thread's OS id (the in-process hang capture's "engine thread: ... native <id>"); else the thread with the managed
# ApplicationTick frame, else thread 0. No symbols by default (no network; frames are module+offset, TaleWorlds.Native.dll offsets go to REA).
# -SymbolServer downloads Microsoft's public PDBs for Windows and the CLR into build/symbols (names clr's GC and ntdll's lock functions);
# -Symbols takes any debugger symbol path instead.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$exe = & (Join-Path $PSScriptRoot 'hangdump/Build-HangDump.ps1')
if (!(Test-Path -LiteralPath $Dump)) { throw "No dump at $Dump" }
$Dump = (Resolve-Path -LiteralPath $Dump).Path
if (!$Out) { $Out = "$Dump.analysis.txt" }
if (!$Json) { $Json = "$Dump.analysis.json" }
$arguments = @("`"$Dump`"", '--out', "`"$Out`"", '--json', "`"$Json`"", '--frames', "$Frames")
if ($EngineThread) { $arguments += @('--engine', $EngineThread) }
if ($SymbolServer -and !$Symbols) { $cache = Join-Path $repo 'build/symbols'; New-Item -ItemType Directory -Force $cache | Out-Null; $Symbols = "srv*$cache*https://msdl.microsoft.com/download/symbols" }
if ($Symbols) { $arguments += @('--symbols', "`"$Symbols`"") }
$stdout = "$Out.stdout"; $stderr = "$Out.stderr"
$p = Start-Process -FilePath $exe -ArgumentList $arguments -NoNewWindow -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
$null = $p.Handle   # keeps the handle, so ExitCode is readable after the wait
if (!$p.WaitForExit($TimeoutMinutes * 60000)) { Stop-Process -Id $p.Id -Force; throw "HangDump.exe did not finish in $TimeoutMinutes minutes ($Dump)" }
$code = $p.ExitCode
$text = @(Get-Content -LiteralPath $stdout -ErrorAction SilentlyContinue) + @(Get-Content -LiteralPath $stderr -ErrorAction SilentlyContinue)
Remove-Item -LiteralPath $stdout, $stderr -Force -ErrorAction SilentlyContinue
$text | ForEach-Object { Write-Output $_ }
if ($code -ne 0) { throw "HangDump.exe failed (exit $code) on $Dump" }
