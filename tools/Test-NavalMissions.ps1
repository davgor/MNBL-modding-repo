# MMR-53: the naval missions' offline checks: the scenario file (tests/Runtime/check_naval_scenarios.py) and the regression replay on the
# committed fixture (tests/Runtime/test_naval_regressions.py). The in-game run is the naval_missions probe, summarized by
# tools/summarize_naval_missions.py.
param([string]$Log = 'evidence/gameplay/naval-scenarios-tests.txt')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    $lines = @()
    $failed = 0
    foreach ($script in 'tests/Runtime/check_naval_scenarios.py', 'tests/Runtime/test_naval_regressions.py') {
        $output = & python $script 2>&1 | ForEach-Object { "$_" }
        if ($LASTEXITCODE -ne 0) { $failed++ }
        $lines += "== $script (exit $LASTEXITCODE)"
        $lines += $output
    }
    $lines += "naval missions offline checks: $(2 - $failed)/2 passed"
    $directory = Split-Path -Parent $Log
    if ($directory) { New-Item -ItemType Directory -Force $directory | Out-Null }
    [System.IO.File]::WriteAllText((Join-Path $repo $Log), (($lines -join "`n") + "`n"))
    $lines | ForEach-Object { Write-Output $_ }
    if ($failed -gt 0) { exit 1 }
}
finally { Pop-Location }
