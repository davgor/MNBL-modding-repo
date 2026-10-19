param([Parameter(Mandatory=$true)][string]$BundleA, [Parameter(Mandatory=$true)][string]$BundleB, [string]$GameRoot='D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord', [string]$Evidence, [string]$Report, [string]$Log)
# MMR-41: compiles the navigation cache key, file reader and store (World/NavigationCacheKey.cs) and the playability validator
# (World/RuntimePlayabilityValidator.cs) with the navigation builder and validator, the settlement placement resolver, the surface model, the
# bundle verifier and the settlement factory, and tests/World/PlayabilityTests.cs against the pinned framework assemblies and the pinned
# TaleWorlds.Core, and runs them on two real compiled world bundles: the cache key's inputs, the installed cache format, the store's refusals,
# the semantic route set and a dry run of it and of the pending-runtime flags through the managed mesh engine (never evidence of engine
# behaviour). Writes the tests' evidence and the offline report. No game process.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$out=Join-Path $repo ('build/research-playability41/test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/World/NavigationCacheKey.cs','src/MNBL.ProceduralWorld/World/RuntimePlayabilityValidator.cs','src/MNBL.ProceduralWorld/Persistence/DurableFile.cs',
    'src/MNBL.ProceduralWorld/World/SettlementPositionResolver.cs','src/MNBL.ProceduralWorld/World/SettlementEntityFactory.cs',
    'src/MNBL.ProceduralWorld/World/RuntimeNavigationBuilder.cs','src/MNBL.ProceduralWorld/World/NavigationValidator.cs','src/MNBL.ProceduralWorld/World/NavigationFixtures.cs',
    'src/MNBL.ProceduralWorld/World/WorldSurface.cs','src/MNBL.ProceduralWorld/World/WorldLifetime.cs','src/MNBL.ProceduralWorld/World/PeriodicWorldCoordinates.cs','src/MNBL.ProceduralWorld/World/PeriodicDistanceModel.cs',
    'src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs','src/MNBL.ProceduralWorld/Campaign/CampaignIdentity.cs','src/MNBL.ProceduralWorld/Campaign/FactionFactory.cs',
    'src/MNBL.ProceduralWorld/Campaign/CharacterTemplateResolver.cs','src/MNBL.ProceduralWorld/Campaign/HeroFactory.cs','src/MNBL.ProceduralWorld/Campaign/SettlementFactory.cs','src/MNBL.ProceduralWorld/Campaign/VillageBinding.cs','src/MNBL.ProceduralWorld/Missions/MissionScenes.cs',
    'src/MNBL.ProceduralWorld/Campaign/RegistrationPlan.cs','tests/World/PlayabilityTests.cs')
$data=@('worldgen/policies/settlement-placement.json','worldgen/policies/naval-content.json','worldgen/policies/navigation.json','worldgen/calibration/naval-transitions/naval-transitions.json',
    'tools/Test-Playability.ps1')
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
$mainMap=Join-Path $GameRoot 'Modules/NavalDLC/SceneObj/Main_map/scene.xscene'
$referenceEvidence+=@{path=$mainMap;sha256=(Get-FileHash -LiteralPath $mainMap).Hash.ToLowerInvariant()}
$exe=Join-Path $out 'PlayabilityTests.exe'
& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ })
if($LASTEXITCODE){throw 'Playability harness compilation failed'}
Copy-Item $core (Join-Path $out 'TaleWorlds.Core.dll')
if(-not $Evidence){$Evidence=Join-Path $out 'playability-tests.json'}
if(-not $Report){$Report=Join-Path $out 'playability-offline.json'}
$lines=@(& $exe $repo (Resolve-Path -LiteralPath $BundleA).Path (Resolve-Path -LiteralPath $BundleB).Path $GameRoot $Evidence $Report 2>&1|ForEach-Object { "$_" })
$failed=$LASTEXITCODE
$lines|ForEach-Object { Write-Output $_ }
[IO.File]::WriteAllLines("$out/checks.log",[string[]]$lines,(New-Object Text.UTF8Encoding($false)))
if($Log){Copy-Item "$out/checks.log" $Log}
foreach($inputFile in $inputs){if((Get-FileHash -LiteralPath (Join-Path $repo $inputFile.path)).Hash.ToLowerInvariant() -ne $inputFile.sha256){throw "Input changed during harness: $($inputFile.path)"}}
@{schema='mnbl.playability-offline.v1';sourceInputs=$inputs;referenceInputs=$referenceEvidence;bundles=@{A=(Resolve-Path -LiteralPath $BundleA).Path;B=(Resolve-Path -LiteralPath $BundleB).Path};executable=@{path=$exe;sha256=(Get-FileHash $exe).Hash.ToLowerInvariant()};scope='The cache key and store, the installed cache format, the semantic route set and a managed dry run over two real compiled bundles. No game process.'}|ConvertTo-Json -Depth 6|Set-Content "$out/receipt.json"
if($failed){throw 'Playability regression failed'}
Write-Output "Playability harness receipt: $out/receipt.json"
exit 0
