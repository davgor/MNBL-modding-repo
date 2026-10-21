param([Parameter(Mandatory=$true)][string]$Placement, [string[]]$Harbour=@(), [string]$GameRoot='D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord', [string]$Report)
# MMR-40: rebuilds a scenebin fixture slot's settlement entities with the SettlementEntityFactory (tools/settlements/FixtureSettlementEntities.cs,
# compiled with the pure placement resolver and entity factory against the pinned framework assemblies). Run after build_*_world.py and before
# Deploy-SceneSlots.ps1. Rewrites the slot's scene.xscene and the placement's gate positions and scene hash; writes the layout report.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$out=Join-Path $repo ('build/research-settlements40/fixture-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/World/SettlementPositionResolver.cs','src/MNBL.ProceduralWorld/World/SettlementEntityFactory.cs','src/MNBL.ProceduralWorld/World/SettlementLooks.cs','src/MNBL.ProceduralWorld/World/SettlementEntityRules.cs','src/MNBL.ProceduralWorld/World/Map2Policies.cs',
    'src/MNBL.ProceduralWorld/World/RuntimeNavigationBuilder.cs','src/MNBL.ProceduralWorld/World/NavigationValidator.cs','src/MNBL.ProceduralWorld/World/NavigationFixtures.cs',
    'src/MNBL.ProceduralWorld/World/WorldSurface.cs','src/MNBL.ProceduralWorld/World/WorldLifetime.cs','src/MNBL.ProceduralWorld/World/PeriodicWorldCoordinates.cs','src/MNBL.ProceduralWorld/World/PeriodicDistanceModel.cs',
    'src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs','src/MNBL.ProceduralWorld/Campaign/CampaignIdentity.cs','src/MNBL.ProceduralWorld/Campaign/FactionFactory.cs',
    'src/MNBL.ProceduralWorld/Campaign/CharacterTemplateResolver.cs','src/MNBL.ProceduralWorld/Campaign/HeroFactory.cs','src/MNBL.ProceduralWorld/Campaign/SettlementFactory.cs','src/MNBL.ProceduralWorld/Campaign/VillageBinding.cs','src/MNBL.ProceduralWorld/Missions/MissionScenes.cs',
    'src/MNBL.ProceduralWorld/Campaign/RegistrationPlan.cs','tools/settlements/FixtureSettlementEntities.cs')
$lock=Get-Content "$repo/config/runtime-api-lock.json" -Raw|ConvertFrom-Json
$references=@()
foreach($name in @('mscorlib','netstandard','System','System.Core','System.Xml','System.Web.Extensions')){
    $pinned=@($lock.assemblies|Where-Object { $_.scope -eq 'framework' -and $_.name -eq $name })
    $path=Join-Path $lock.frameworkRoot $pinned[0].path
    if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pinned[0].sha256){throw "Framework pin mismatch: $path"}
    $references+="/reference:$path"
}
$pinned=@($lock.assemblies|Where-Object { $_.scope -eq 'game' -and $_.name -eq 'TaleWorlds.Core' })
$core=Join-Path $GameRoot $pinned[0].path
$references+="/reference:$core"
$exe=Join-Path $out 'FixtureSettlementEntities.exe'
& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ })
if($LASTEXITCODE){throw 'Fixture settlement tool compilation failed'}
Copy-Item $core (Join-Path $out 'TaleWorlds.Core.dll')
if(-not $Report){$Report=Join-Path $out 'fixture-settlement-layout.json'}
$harbourArgs=@(); foreach($h in $Harbour){ $harbourArgs+=@('--harbour',$h) }
& $exe (Resolve-Path -LiteralPath $Placement).Path (Join-Path $GameRoot 'Modules/NavalDLC/SceneObj/Main_map/scene.xscene') (Join-Path $repo 'worldgen/policies/settlement-placement.json') (Join-Path $repo 'worldgen/policies/naval-content.json') $Report @harbourArgs
if($LASTEXITCODE){throw 'Fixture settlement entities refused'}
Write-Output "Fixture settlement layout: $Report"
