# MMR-248 (map epic 2): compiles the managed strict policy readers (World/Map2Policies.cs) and the host capability list
# (CampaignBootstrap/WorldDataBundle.cs) with tests/World/Map2PolicyTests.cs against the pinned framework assemblies, and runs them on
# the repository's policies. No game, scene or world is involved.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$out = Join-Path $repo 'build/map2-policy-tests'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$csc = 'C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$lock = Get-Content "$repo/config/runtime-api-lock.json" -Raw | ConvertFrom-Json
$references = @()
foreach ($name in @('mscorlib', 'System', 'System.Core', 'System.Web.Extensions')) {
    $pin = @($lock.assemblies | Where-Object { $_.scope -eq 'framework' -and $_.name -eq $name })
    if ($pin.Count -ne 1) { throw "Missing unique framework pin: $name" }
    $path = Join-Path $lock.frameworkRoot $pin[0].path
    if ((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pin[0].sha256) { throw "Framework pin mismatch: $path" }
    $references += "/reference:$path"
}
$exe = Join-Path $out 'Map2PolicyTests.exe'
& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references `
    "$repo/src/MNBL.ProceduralWorld/World/Map2Policies.cs" "$repo/src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs" "$repo/tests/World/Map2PolicyTests.cs"
if ($LASTEXITCODE) { throw 'Map2PolicyTests build failed' }
& $exe $repo
if ($LASTEXITCODE) { throw 'Map2PolicyTests failed' }
