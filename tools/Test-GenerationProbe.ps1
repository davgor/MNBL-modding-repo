$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$output = Join-Path $repo ('build/research-generator15/harness-' + [Guid]::NewGuid().ToString('N'))
New-Item $output -ItemType Directory -Force | Out-Null
$lock = Get-Content "$repo/tools.lock.json" -Raw | ConvertFrom-Json
$receipt = Get-Content "$repo/evidence/runtime/build-api-inputs.json" -Raw | ConvertFrom-Json
$producer = Join-Path $repo $lock.producer.binary
$bridgeRecord = $receipt.outputs | Where-Object path -eq 'build/worldbridge/Release/worldbridge.exe'
if (!$bridgeRecord) { throw 'Missing accepted reader output pin' }
$bridge = Join-Path $repo $bridgeRecord.path
foreach ($item in @(@{path=$producer;sha256=$lock.producer.sha256},@{path=$bridge;sha256=$bridgeRecord.sha256})) {
    if ((Get-FileHash -LiteralPath $item.path).Hash.ToLowerInvariant() -ne $item.sha256) { throw "Binary differs from accepted pin: $($item.path)" }
}
$csc = 'C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
& $csc /nologo /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$output/GenerationProbeTests.exe" /reference:System.Web.Extensions.dll "$repo/tests/GenerationProbeTests.cs" "$repo/src/MNBL.ProceduralWorld/Diagnostics/GenerationProbe.cs" "$repo/src/MNBL.ProceduralWorld/Diagnostics/OwnedProcessJob.cs"
if ($LASTEXITCODE) { throw 'Harness build failed' }
Copy-Item -LiteralPath $producer -Destination "$output/fwg.exe"
[IO.File]::WriteAllText("$output/fwg.sha256", $lock.producer.sha256)
Copy-Item -LiteralPath $bridge -Destination "$output/worldbridge.exe"
[IO.File]::WriteAllText("$output/worldbridge.sha256", $bridgeRecord.sha256)
& "$output/GenerationProbeTests.exe" "$output/owned jobs" 2>&1 | Tee-Object "$output/checks.log"
if ($LASTEXITCODE) { throw "Probe tests failed; see $output/checks.log" }
Write-Output "Standalone harness evidence: $output"
