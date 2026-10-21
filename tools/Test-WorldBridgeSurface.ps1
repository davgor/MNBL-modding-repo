param(
    [string]$Template,
    [string]$PreparedTemplate,
    [string]$CMake = 'C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe',
    [string]$Python = 'python'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('mnbl-worldbridge-surface-' + [guid]::NewGuid().ToString('N'))
# MMR-255: the tracked surface template (tools/scenebin/make_surface_template.py), materialized from the installed official scene.
if (!$Template) {
    $Template = Join-Path $scratch 'template'
    & $Python -B "$repo/tools/scenebin/make_surface_template.py" --materialize $Template
    if ($LASTEXITCODE) { throw 'Surface template materialization failed' }
}
$sourceFiles = @('src/WorldBridge/main.cpp','src/WorldBridge/surface.cpp','src/WorldBridge/surface.hpp','src/WorldBridge/CMakeLists.txt','tests/WorldBridgeSurfaceTests.py','tools/Test-WorldBridgeSurface.ps1','tools/scenebin/terrain_bin.py','tools/scenebin/navmesh_bin.py','tools/scenebin/build_slot.py','tools/scenebin/make_surface_template.py','tools/terrain_layers.py')
$inputs = @($sourceFiles | ForEach-Object { @{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $repo $_)).Hash} })
& $CMake -S (Join-Path $repo 'src/WorldBridge') -B "$scratch/compiler" -G 'Visual Studio 17 2022' -A x64
if ($LASTEXITCODE) { throw 'Surface CMake configure failed' }
& $CMake --build "$scratch/compiler" --config Release
if ($LASTEXITCODE) { throw 'Surface C++ build failed' }
$testArgs = @('-B', "$repo/tests/WorldBridgeSurfaceTests.py", '--exe', "$scratch/compiler/Release/worldbridge.exe", '--output', "$scratch/cases", '--template', $Template)
if ($PreparedTemplate) { $testArgs += @('--prepared-template', $PreparedTemplate) }
& $Python @testArgs
if ($LASTEXITCODE) { throw "Surface verification failed; scratch retained: $scratch" }
foreach ($inputFile in $inputs) {
    if ((Get-FileHash -LiteralPath (Join-Path $repo $inputFile.path)).Hash -ne $inputFile.sha256) { throw "Source changed during verification: $($inputFile.path)" }
}
[ordered]@{inputs=$inputs;scope='Isolated native utility build and offline independent Python codec checks; no deploy/game/editor';checks="$scratch/cases/checks.json"} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$scratch/build-receipt.json"
Write-Output "Surface build/test receipt: $scratch/build-receipt.json"
