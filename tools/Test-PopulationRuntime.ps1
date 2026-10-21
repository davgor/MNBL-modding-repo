param([Parameter(Mandatory=$true)][string]$BundleA, [Parameter(Mandatory=$true)][string]$BundleB, [string]$GameRoot='D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord',
      [string]$HarmonyDir='D:\SteamLibrary\steamapps\workshop\content\261550\2859188632\bin\Win64_Shipping_Client', [string]$Evidence, [string]$Log)
# MMR-281: compiles the population runtime (Campaign/PopulationRuntime.cs: the wanderer tick patch and rules, the IsMinorFactionHero list,
# the homeless-house rule) with the factories against the pinned TaleWorlds assemblies and Bannerlord.Harmony, and tests/Campaign/PopulationRuntimeTests.cs,
# and runs it: the tick patch located in the installed TaleWorlds.CampaignSystem by IL content and applied with Harmony, the wanderer rules on
# a fake world with worlds A and B's new-game datasets (CompiledWorldDataset on the pinned catalog capture and each world's layout in the git-ignored
# settlement position report: day-0 wanderers, registered templates) and reserve names, the minor-faction heroes of A and B, the homeless-house rule against the
# installed 28 days, the compatibility statuses and the soak census fields. No game process.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$out=Join-Path $repo ('build/population-runtime/test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/Campaign/PopulationRuntime.cs','src/MNBL.ProceduralWorld/Campaign/CompanionsTickPatch.cs','src/MNBL.ProceduralWorld/Campaign/CampaignIdentity.cs','src/MNBL.ProceduralWorld/Campaign/FactionFactory.cs',
    'src/MNBL.ProceduralWorld/Campaign/CharacterTemplateResolver.cs','src/MNBL.ProceduralWorld/Campaign/HeroFactory.cs','src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs',
    'src/MNBL.ProceduralWorld/Campaign/SettlementFactory.cs','src/MNBL.ProceduralWorld/Campaign/VillageBinding.cs','src/MNBL.ProceduralWorld/Missions/MissionScenes.cs','src/MNBL.ProceduralWorld/Campaign/RegistrationPlan.cs',
    'src/MNBL.ProceduralWorld/Campaign/GeneratedSpawnModel.cs','src/MNBL.ProceduralWorld/Campaign/GeneratedCampaignInitializer.cs','src/MNBL.ProceduralWorld/Campaign/NavalContentFactory.cs','src/MNBL.ProceduralWorld/Campaign/ReferenceSchema.cs',
    'src/MNBL.ProceduralWorld/Campaign/CampaignGraphValidator.cs','src/MNBL.ProceduralWorld/Campaign/CompiledWorldDataset.cs','src/MNBL.ProceduralWorld/CampaignBootstrap/GeneratedCampaignDataset.cs',
    'tests/Campaign/PopulationRuntimeTests.cs')
$templates=@(Get-ChildItem -LiteralPath (Join-Path $repo 'worldgen/templates/cultures') -Filter *.json|Sort-Object Name|ForEach-Object { 'worldgen/templates/cultures/'+$_.Name })
$data=@('tools/Test-PopulationRuntime.ps1','worldgen/policies/factions.json','worldgen/policies/population.json','worldgen/policies/culture-presentations.json','worldgen/policies/campaign-compatibility.json',
    'src/MNBL.ProceduralWorld/Campaign/GeneratedPatches.cs','src/MNBL.ProceduralWorld/Campaign/GeneratedPopulationBehavior.cs','src/MNBL.ProceduralWorld/Automation/CampaignSoakProbe.cs',
    'worldgen/policies/graph-validation.json','worldgen/policies/naval-content.json','worldgen/policies/rosters.json','worldgen/policies/economy.json','worldgen/policies/startup.json','worldgen/policies/campaign-catalog.json',
    'worldgen/templates/settlements/prototypes.json','worldgen/policies/mission-scenes.json','evidence/world/settlement-position-report.json')+$templates
$inputs=@(($sources+$data)|ForEach-Object { @{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $repo $_)).Hash.ToLowerInvariant()} })
$lock=Get-Content "$repo/config/runtime-api-lock.json" -Raw|ConvertFrom-Json
$references=@();$referenceEvidence=@()
foreach($name in @('mscorlib','netstandard','System','System.Core','System.Xml','System.Web.Extensions')){
    $pin=@($lock.assemblies|Where-Object { $_.scope -eq 'framework' -and $_.name -eq $name })
    if($pin.Count -ne 1){throw "Missing unique framework pin: $name"}
    $path=Join-Path $lock.frameworkRoot $pin[0].path
    if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pin[0].sha256){throw "Framework pin mismatch: $path"}
    $references+="/reference:$path";$referenceEvidence+=@{path=$path;sha256=$pin[0].sha256}
}
# The patch targets are the game's own (pinned); they load from the game at run time, Harmony from its Workshop module.
foreach($name in @('TaleWorlds.CampaignSystem','TaleWorlds.Core','TaleWorlds.ObjectSystem','TaleWorlds.Localization','TaleWorlds.Library')){
    $pin=@($lock.assemblies|Where-Object { $_.scope -eq 'game' -and $_.name -eq $name })
    if($pin.Count -ne 1){throw "Missing unique game pin: $name"}
    $path=Join-Path $GameRoot $pin[0].path
    if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pin[0].sha256){throw "Game assembly pin mismatch: $path"}
    $references+="/reference:$path";$referenceEvidence+=@{path=$path;sha256=$pin[0].sha256}
}
$harmony=Join-Path $HarmonyDir '0Harmony.dll'
if(-not (Test-Path -LiteralPath $harmony)){throw "Bannerlord.Harmony is not installed: $harmony"}
$references+="/reference:$harmony";$referenceEvidence+=@{path=$harmony;sha256=(Get-FileHash -LiteralPath $harmony).Hash.ToLowerInvariant()}
$exe=Join-Path $out 'PopulationRuntimeTests.exe'
$compile=@(& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ }) 2>&1|ForEach-Object { "$_" })
if($LASTEXITCODE){$compile|ForEach-Object { Write-Output $_ }; throw 'Population runtime harness compilation failed'}
if(-not $Evidence){$Evidence=Join-Path $out 'population-runtime.json'}
$lines=@(& $exe $repo (Resolve-Path -LiteralPath $BundleA).Path (Resolve-Path -LiteralPath $BundleB).Path $GameRoot $HarmonyDir $Evidence 2>&1|ForEach-Object { "$_" })
$failed=$LASTEXITCODE
$lines|ForEach-Object { Write-Output $_ }
[IO.File]::WriteAllLines("$out/checks.log",[string[]]$lines,(New-Object Text.UTF8Encoding($false)))
if($Log){Copy-Item "$out/checks.log" $Log}
foreach($inputFile in $inputs){if((Get-FileHash -LiteralPath (Join-Path $repo $inputFile.path)).Hash.ToLowerInvariant() -ne $inputFile.sha256){throw "Input changed during harness: $($inputFile.path)"}}
@{schema='mnbl.population-runtime-offline.v1';sourceInputs=$inputs;referenceInputs=$referenceEvidence;bundles=@((Resolve-Path -LiteralPath $BundleA).Path,(Resolve-Path -LiteralPath $BundleB).Path);executable=@{path=$exe;sha256=(Get-FileHash $exe).Hash.ToLowerInvariant()};scope='The wanderer tick patch against the installed TaleWorlds.CampaignSystem, the wanderer rules, the minor-faction heroes, the homeless-house rule and the compatibility statuses. No game.'}|ConvertTo-Json -Depth 6|Set-Content "$out/receipt.json"
if($failed){throw 'Population runtime tests failed'}
Write-Output "Population runtime harness receipt: $out/receipt.json"
exit 0
