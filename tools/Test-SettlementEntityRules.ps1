param([Parameter(Mandatory=$true)][string]$BundleA, [Parameter(Mandatory=$true)][string]$BundleB, [string]$GameRoot='D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord', [string]$Evidence, [string]$Log,
    [string]$SceneA, [string]$SceneB, [string]$DetailA, [string]$DetailB, [string[]]$Control)
# MMR-247: compiles the runtime stage's settlement entity rules (World/SettlementEntityRules.cs, which SettlementEntityChecks runs on the
# engine's inspection read) with the look plan, the placement resolver, the entity factory, the water bodies and the navigation builder, and
# tests/World/SettlementEntityRulesTests.cs against the pinned framework assemblies and the pinned TaleWorlds.Core, and runs them on worlds A'
# and B': the scene baked as RuntimeWorldBackend.Prepare bakes it, checked by the same rules as authored and under the model of acceptance run
# acc1's engine read (same-level visuals collapse). -SceneA/-SceneB: an in-game world scene of that world, checked against this build's layout
# (-DetailA/-DetailB: the in-game failure detail the model must give); -Control: scenes whose engine read passed in game. No game process.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$out=Join-Path $repo ('build/research-entity-rules/test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/World/SettlementEntityRules.cs','src/MNBL.ProceduralWorld/World/SettlementPositionResolver.cs','src/MNBL.ProceduralWorld/World/SettlementEntityFactory.cs','src/MNBL.ProceduralWorld/World/SettlementLooks.cs','src/MNBL.ProceduralWorld/World/Map2Policies.cs',
    'src/MNBL.ProceduralWorld/World/GeneratedWaterSystem.cs','src/MNBL.ProceduralWorld/World/GeneratedClimateSystem.cs','src/MNBL.ProceduralWorld/World/MapDressing.cs',
    'src/MNBL.ProceduralWorld/World/RuntimeNavigationBuilder.cs','src/MNBL.ProceduralWorld/World/NavigationValidator.cs','src/MNBL.ProceduralWorld/World/NavigationFixtures.cs',
    'src/MNBL.ProceduralWorld/World/WorldSurface.cs','src/MNBL.ProceduralWorld/World/WorldLifetime.cs','src/MNBL.ProceduralWorld/World/PeriodicWorldCoordinates.cs','src/MNBL.ProceduralWorld/World/PeriodicDistanceModel.cs',
    'src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs','src/MNBL.ProceduralWorld/Campaign/CampaignIdentity.cs','src/MNBL.ProceduralWorld/Campaign/FactionFactory.cs',
    'src/MNBL.ProceduralWorld/Campaign/CharacterTemplateResolver.cs','src/MNBL.ProceduralWorld/Campaign/HeroFactory.cs','src/MNBL.ProceduralWorld/Campaign/SettlementFactory.cs','src/MNBL.ProceduralWorld/Campaign/VillageBinding.cs','src/MNBL.ProceduralWorld/Missions/MissionScenes.cs',
    'src/MNBL.ProceduralWorld/Campaign/RegistrationPlan.cs','tests/World/SettlementEntityRulesTests.cs')
$data=@('worldgen/policies/settlement-placement.json','worldgen/policies/naval-content.json','worldgen/policies/settlement-looks.json','worldgen/policies/economy.json','worldgen/policies/mission-scenes.json','worldgen/policies/campaign-catalog.json','worldgen/templates/settlements/prototypes.json','worldgen/policies/navigation.json','worldgen/calibration/naval-transitions/naval-transitions.json',
    'tools/Test-SettlementEntityRules.ps1')
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
$exe=Join-Path $out 'SettlementEntityRulesTests.exe'
& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ })
if($LASTEXITCODE){throw 'Settlement entity rules harness compilation failed'}
Copy-Item $core (Join-Path $out 'TaleWorlds.Core.dll')
if(-not $Evidence){$Evidence=Join-Path $out 'settlement-entity-rules.json'}
$extra=@()
if($SceneA){$extra+='--scene';$extra+="A=$SceneA"}
if($SceneB){$extra+='--scene';$extra+="B=$SceneB"}
if($DetailA){$extra+='--detail';$extra+="A=$DetailA"}
if($DetailB){$extra+='--detail';$extra+="B=$DetailB"}
foreach($c in @($Control|Where-Object { $_ })){$extra+='--control';$extra+=$c}
$lines=@(& $exe $repo (Resolve-Path -LiteralPath $BundleA).Path (Resolve-Path -LiteralPath $BundleB).Path $GameRoot $Evidence @extra 2>&1|ForEach-Object { "$_" })
$failed=$LASTEXITCODE
$lines|ForEach-Object { Write-Output $_ }
[IO.File]::WriteAllLines("$out/checks.log",[string[]]$lines,(New-Object Text.UTF8Encoding($false)))
if($Log){Copy-Item "$out/checks.log" $Log}
foreach($inputFile in $inputs){if((Get-FileHash -LiteralPath (Join-Path $repo $inputFile.path)).Hash.ToLowerInvariant() -ne $inputFile.sha256){throw "Input changed during harness: $($inputFile.path)"}}
@{schema='mnbl.settlement-entity-rules-offline.v1';sourceInputs=$inputs;referenceInputs=$referenceEvidence;bundles=@{A=(Resolve-Path -LiteralPath $BundleA).Path;B=(Resolve-Path -LiteralPath $BundleB).Path};executable=@{path=$exe;sha256=(Get-FileHash $exe).Hash.ToLowerInvariant()};scope='The runtime stage settlement entity rules over the baked scene XML of two real compiled bundles. No game process.'}|ConvertTo-Json -Depth 6|Set-Content "$out/receipt.json"
if($failed){throw 'Settlement entity rules regression failed'}
Write-Output "Settlement entity rules harness receipt: $out/receipt.json"
exit 0
