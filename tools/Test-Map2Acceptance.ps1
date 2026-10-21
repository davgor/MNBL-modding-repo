param([string]$BundleA, [string]$BundleB, [string]$Evidence)
# MMR-247: compiles the acceptance probe's pure place choices (Automation/Map2AcceptancePlan.cs) with tests/World/Map2AcceptancePlanTests.cs
# against the pinned framework assemblies and runs them on two compiled world bundles (default: build/fixtures/bundles.json, written by
# C:\Users\davgo\m2\refresh-fixtures.ps1). No game process: the probe itself (Automation/Map2AcceptanceProbe.cs) runs in game.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
if (-not $BundleA -or -not $BundleB) {
    $fixtures=Join-Path $repo 'build/fixtures/bundles.json'
    if (!(Test-Path -LiteralPath $fixtures)) { throw 'Pass -BundleA and -BundleB, or write build/fixtures/bundles.json (refresh-fixtures.ps1).' }
    $f=Get-Content -LiteralPath $fixtures -Raw | ConvertFrom-Json
    if (-not $BundleA) { $BundleA=$f.A }; if (-not $BundleB) { $BundleB=$f.B }
}
$out=Join-Path $repo ('build/map2-acceptance247/test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/Automation/Map2AcceptancePlan.cs','tests/World/Map2AcceptancePlanTests.cs')
$lock=Get-Content "$repo/config/runtime-api-lock.json" -Raw|ConvertFrom-Json
$references=@()
foreach($name in @('mscorlib','netstandard','System','System.Core','System.Web.Extensions')){
    $pinned=@($lock.assemblies|Where-Object { $_.scope -eq 'framework' -and $_.name -eq $name })
    if($pinned.Count -ne 1){throw "Missing unique framework pin: $name"}
    $path=Join-Path $lock.frameworkRoot $pinned[0].path
    if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pinned[0].sha256){throw "Framework pin mismatch: $path"}
    $references+="/reference:$path"
}
$exe=Join-Path $out 'Map2AcceptancePlanTests.exe'
& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ })
if($LASTEXITCODE){throw 'Map2 acceptance plan harness compilation failed'}
if(-not $Evidence){$Evidence=Join-Path $out 'map2-acceptance-plan.json'}
& $exe $repo (Resolve-Path -LiteralPath $BundleA).Path (Resolve-Path -LiteralPath $BundleB).Path $Evidence
if($LASTEXITCODE){throw 'Map2 acceptance plan tests failed'}
Write-Output "Map2 acceptance plan evidence: $Evidence"
