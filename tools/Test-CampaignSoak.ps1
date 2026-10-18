# MMR-51: the campaign soak's offline checks: the scenario and regression fixture (tests/Runtime/check_campaign_soak_scenarios.py) and the
# regression replay on the committed fixture (tests/Runtime/test_campaign_soak_regressions.py). The in-game run is the campaign_soak probe,
# summarized by tools/summarize_campaign_soak.py.
param([string]$Log = 'evidence/gameplay/campaign-soak-tests.txt')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    $lines = @()
    $failed = 0
    foreach ($script in 'tests/Runtime/check_campaign_soak_scenarios.py', 'tests/Runtime/test_campaign_soak_regressions.py') {
        $output = & python $script 2>&1 | ForEach-Object { "$_" }
        if ($LASTEXITCODE -ne 0) { $failed++ }
        $lines += "== $script (exit $LASTEXITCODE)"
        $lines += $output
    }
    $lines += "campaign soak offline checks: $(2 - $failed)/2 passed"
    $directory = Split-Path -Parent $Log
    if ($directory) { New-Item -ItemType Directory -Force $directory | Out-Null }
    [System.IO.File]::WriteAllText((Join-Path $repo $Log), (($lines -join "`n") + "`n"))
    $lines | ForEach-Object { Write-Output $_ }
    if ($failed -gt 0) { exit 1 }
}
finally { Pop-Location }
