param([Parameter(Mandatory=$true)][string]$BundleA, [Parameter(Mandatory=$true)][string]$BundleB, [string]$GameRoot='D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord', [string]$Evidence)
# MMR-257: compiles the pure managed map dressing (World/MapDressing.cs: road decals, bridges, ruin markers, the installer) with the
# navigation builder, the surface model, the source reader, the map epic 2 policy readers and the bundle verifier, and
# tests/World/MapDressingTests.cs against the pinned framework assemblies and the pinned TaleWorlds.Core (the installed TerrainType enum the
# navigation builder names faces with), and runs them on two real compiled world bundles. No game process: MMR-247 shows the dressing in game.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$out=Join-Path $repo ('build/map-dressing257/test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/World/MapDressing.cs','src/MNBL.ProceduralWorld/World/DecalAtlas.cs','src/MNBL.ProceduralWorld/World/GeneratedWaterSystem.cs','src/MNBL.ProceduralWorld/World/Map2Policies.cs','src/MNBL.ProceduralWorld/World/GeneratedClimateSystem.cs',
    'src/MNBL.ProceduralWorld/World/RuntimeNavigationBuilder.cs','src/MNBL.ProceduralWorld/World/NavigationValidator.cs','src/MNBL.ProceduralWorld/World/NavigationFixtures.cs',
    'src/MNBL.ProceduralWorld/World/WorldSurface.cs','src/MNBL.ProceduralWorld/World/WorldLifetime.cs','src/MNBL.ProceduralWorld/World/PeriodicWorldCoordinates.cs',
    'src/MNBL.ProceduralWorld/World/PeriodicDistanceModel.cs','src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs','src/MNBL.ProceduralWorld/World/CampaignMapScreen.cs','tests/World/MapDressingTests.cs')
$lock=Get-Content "$repo/config/runtime-api-lock.json" -Raw|ConvertFrom-Json
$references=@()
foreach($name in @('mscorlib','netstandard','System','System.Core','System.Xml','System.Web.Extensions')){
    $pinned=@($lock.assemblies|Where-Object { $_.scope -eq 'framework' -and $_.name -eq $name })
    if($pinned.Count -ne 1){throw "Missing unique framework pin: $name"}
    $path=Join-Path $lock.frameworkRoot $pinned[0].path
    if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pinned[0].sha256){throw "Framework pin mismatch: $path"}
    $references+="/reference:$path"
}
$pinned=@($lock.assemblies|Where-Object { $_.scope -eq 'game' -and $_.name -eq 'TaleWorlds.Core' })
$core=Join-Path $GameRoot $pinned[0].path
if((Get-FileHash -LiteralPath $core).Hash.ToLowerInvariant() -ne $pinned[0].sha256){throw "Game assembly pin mismatch: $core"}
$references+="/reference:$core"
$exe=Join-Path $out 'MapDressingTests.exe'
& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ })
if($LASTEXITCODE){throw 'Map dressing harness compilation failed'}
if(-not $Evidence){$Evidence=Join-Path $out 'map-dressing.json'}
# TaleWorlds.Core loads from the game folder at run time.
Copy-Item -LiteralPath $core -Destination $out
& $exe $repo (Resolve-Path -LiteralPath $BundleA).Path (Resolve-Path -LiteralPath $BundleB).Path $Evidence $GameRoot
if($LASTEXITCODE){throw 'Map dressing tests failed'}
Write-Output "Map dressing evidence: $Evidence"
