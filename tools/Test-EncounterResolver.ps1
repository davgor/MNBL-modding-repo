param([Parameter(Mandatory=$true)][string]$BundleA, [Parameter(Mandatory=$true)][string]$BundleB, [string]$GameRoot='D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord', [string]$Evidence, [string]$Log)
# MMR-42: compiles the mission scene catalogue and contracts (Missions/MissionScenes.cs) and the encounter resolver (Missions/
# GeneratedEncounterResolver.cs) with the navigation builder and its managed mesh stand-in, the world surface and the bundle verifier, and
# tests/Missions/EncounterResolverTests.cs against the pinned framework assemblies and the pinned TaleWorlds.Core, and runs them on the
# installed game's scene files and two real compiled world bundles: the catalogue against the installed tables and scenes, the mission
# contracts over every prototype, the battle-cell choice on every geography class, full battle maps of A and B (the managed stand-in, never
# evidence of engine behaviour), the periodic seam and the mismatch check. Writes the tests' evidence. No game process.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$out=Join-Path $repo ('build/research-missions42/test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/Missions/MissionScenes.cs','src/MNBL.ProceduralWorld/Missions/GeneratedEncounterResolver.cs','src/MNBL.ProceduralWorld/Persistence/DurableFile.cs',
    'src/MNBL.ProceduralWorld/World/SettlementPositionResolver.cs','src/MNBL.ProceduralWorld/World/SettlementEntityFactory.cs','src/MNBL.ProceduralWorld/World/SettlementLooks.cs','src/MNBL.ProceduralWorld/World/SettlementEntityRules.cs','src/MNBL.ProceduralWorld/World/Map2Policies.cs',
    'src/MNBL.ProceduralWorld/World/RuntimeNavigationBuilder.cs','src/MNBL.ProceduralWorld/World/NavigationValidator.cs','src/MNBL.ProceduralWorld/World/NavigationFixtures.cs',
    'src/MNBL.ProceduralWorld/World/WorldSurface.cs','src/MNBL.ProceduralWorld/World/WorldLifetime.cs','src/MNBL.ProceduralWorld/World/PeriodicWorldCoordinates.cs','src/MNBL.ProceduralWorld/World/PeriodicDistanceModel.cs',
    'src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs','src/MNBL.ProceduralWorld/Campaign/CampaignIdentity.cs','src/MNBL.ProceduralWorld/Campaign/FactionFactory.cs',
    'src/MNBL.ProceduralWorld/Campaign/CharacterTemplateResolver.cs','src/MNBL.ProceduralWorld/Campaign/HeroFactory.cs','src/MNBL.ProceduralWorld/Campaign/SettlementFactory.cs','src/MNBL.ProceduralWorld/Campaign/VillageBinding.cs',
    'src/MNBL.ProceduralWorld/Campaign/RegistrationPlan.cs','src/MNBL.ProceduralWorld/Generation/PostBuildBattleMap.cs','src/MNBL.ProceduralWorld/World/MagicGround.cs','src/MNBL.ProceduralWorld/World/MagicVeins.cs','src/MNBL.ProceduralWorld/World/MapDressing.cs',
    'src/MNBL.ProceduralWorld/World/GeneratedClimateSystem.cs','src/MNBL.ProceduralWorld/World/GeneratedWaterSystem.cs','tests/Missions/EncounterResolverTests.cs')
$data=@('worldgen/policies/mission-scenes.json','worldgen/policies/navigation.json','worldgen/policies/terrain-materials.json','worldgen/policies/settlement-placement.json','worldgen/policies/naval-content.json','worldgen/policies/roads.json',
    'worldgen/calibration/naval-transitions/naval-transitions.json','tools/make_mission_scenes.py','tools/Test-EncounterResolver.ps1')
$inputs=@(($sources+$data)|ForEach-Object { @{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $repo $_)).Hash.ToLowerInvariant()} })
$lock=Get-Content "$repo/config/runtime-api-lock.json" -Raw|ConvertFrom-Json
$references=@();$referenceEvidence=@()
foreach($name in @('mscorlib','netstandard','System','System.Core','System.Xml','System.Web.Extensions')){
    $pinned=@($lock.assemblies|Where-Object { $_.scope -eq 'framework' -and $_.name -eq $name })
    if($pinned.Count -ne 1){throw "Missing unique framework pin: $name"}
    $path=Join-Path $lock.frameworkRoot $pinned[0].path
    if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pinned[0].sha256){throw "Framework pin mismatch: $path"}
    $references+="/reference:$path";$referenceEvidence+=@{path=$path;sha256=$pinned[0].sha256}
}
$pinned=@($lock.assemblies|Where-Object { $_.scope -eq 'game' -and $_.name -eq 'TaleWorlds.Core' })
$core=Join-Path $GameRoot $pinned[0].path
if((Get-FileHash -LiteralPath $core).Hash.ToLowerInvariant() -ne $pinned[0].sha256){throw "Game assembly pin mismatch: $core"}
$references+="/reference:$core";$referenceEvidence+=@{path=$core;sha256=$pinned[0].sha256}
$exe=Join-Path $out 'EncounterResolverTests.exe'
& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ })
if($LASTEXITCODE){throw 'Encounter resolver harness compilation failed'}
Copy-Item $core (Join-Path $out 'TaleWorlds.Core.dll')
if(-not $Evidence){$Evidence=Join-Path $out 'encounter-resolver-tests.json'}
$lines=@(& $exe $repo (Resolve-Path -LiteralPath $BundleA).Path (Resolve-Path -LiteralPath $BundleB).Path $GameRoot $Evidence 2>&1|ForEach-Object { "$_" })
$failed=$LASTEXITCODE
$lines|ForEach-Object { Write-Output $_ }
[IO.File]::WriteAllLines("$out/checks.log",[string[]]$lines,(New-Object Text.UTF8Encoding($false)))
if($Log){Copy-Item "$out/checks.log" $Log}
foreach($inputFile in $inputs){if((Get-FileHash -LiteralPath (Join-Path $repo $inputFile.path)).Hash.ToLowerInvariant() -ne $inputFile.sha256){throw "Input changed during harness: $($inputFile.path)"}}
@{schema='mnbl.encounter-resolver-offline.v1';sourceInputs=$inputs;referenceInputs=$referenceEvidence;bundles=@{A=(Resolve-Path -LiteralPath $BundleA).Path;B=(Resolve-Path -LiteralPath $BundleB).Path};gameRoot=$GameRoot;executable=@{path=$exe;sha256=(Get-FileHash $exe).Hash.ToLowerInvariant()};scope='The mission scene catalogue against the installed game, the mission contracts, the battle-cell choice and full battle maps of two compiled worlds through the managed mesh stand-in (never evidence of engine behaviour), the periodic seam and the mismatch check'}|ConvertTo-Json -Depth 6|Set-Content -Encoding utf8 "$out/receipt.json"
if($failed){throw 'Encounter resolver regression failed'}
Write-Output "Encounter resolver harness receipt: $out/receipt.json"
exit 0
