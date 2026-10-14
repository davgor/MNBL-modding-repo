$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$output = Join-Path $repo 'build/heightfield-tests'
New-Item $output -ItemType Directory -Force | Out-Null
$csc = 'C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
& $csc /nologo /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$output/HeightfieldTests.exe" "$repo/tests/HeightfieldTests.cs" "$repo/src/MNBL.ProceduralWorld/Diagnostics/DiagnosticHeightfield.cs"
if ($LASTEXITCODE) { throw 'Heightfield harness build failed' }
& "$output/HeightfieldTests.exe" "$output/fixtures"
if ($LASTEXITCODE) { throw 'Heightfield tests failed' }
