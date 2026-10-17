$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$output = Join-Path $repo ('build/research-cleanup15/run-' + [Guid]::NewGuid().ToString('N'))
New-Item $output -ItemType Directory -Force | Out-Null
$csc = 'C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sourcePaths = @('src/MNBL.ProceduralWorld/Automation/AutorunHost.cs','src/MNBL.ProceduralWorld/Automation/ProbeContext.cs','tests/AutorunCleanupTests.cs','tools/Test-AutorunCleanup.ps1')
$inputs = @($sourcePaths | ForEach-Object { [ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $repo $_)).Hash.ToLowerInvariant()} })
$exe = Join-Path $output 'AutorunCleanupTests.exe'
& $csc /nologo /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" /reference:System.Web.Extensions.dll "$repo/tests/AutorunCleanupTests.cs" "$repo/src/MNBL.ProceduralWorld/Automation/AutorunHost.cs" "$repo/src/MNBL.ProceduralWorld/Automation/ProbeContext.cs"
if ($LASTEXITCODE) { throw 'Managed cleanup harness compilation failed' }
& $exe "$output/cases" 2>&1 | Tee-Object "$output/checks.log"
$code = $LASTEXITCODE
foreach ($inputFile in $inputs) {
    if ((Get-FileHash -LiteralPath (Join-Path $repo $inputFile.path)).Hash.ToLowerInvariant() -ne $inputFile.sha256) {
        throw "Source changed during test; discard this run: $($inputFile.path)"
    }
}
[ordered]@{
    schema='mnbl.autorun-cleanup-harness-receipt';exitCode=$code;sourceInputs=$inputs
    compiler=@{path=$csc;sha256=(Get-FileHash -LiteralPath $csc).Hash.ToLowerInvariant()}
    executable=@{path=$exe;sha256=(Get-FileHash -LiteralPath $exe).Hash.ToLowerInvariant()}
    scope='Actual AutorunHost and ProbeContext compiled unchanged with minimal game API doubles; managed regression only.'
} | ConvertTo-Json -Depth 6 | Set-Content "$output/receipt.json"
if ($code) { throw "Managed cleanup tests failed; see $output/checks.log" }
Write-Output "Managed cleanup evidence: $output"
