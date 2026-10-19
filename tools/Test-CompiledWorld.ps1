param([Parameter(Mandatory=$true)][string]$Bundle, [string]$Evidence, [string]$Log)
# MMR-45: compiles the compiled world dataset (Campaign/CompiledWorldDataset.cs) with the MMR-31..35 factories, the MMR-36 graph validator and
# the fixture dataset's re-emission closure, and tests/Campaign/CompiledWorldTests.cs against the pinned framework assemblies, and runs it on
# the real bundle (world A), the repository policies, the pinned installed catalog and world A's settlement layout: the graph validates, every
# record stands on its scene entity, the documents plan through the registry, determinism and the refusals. No game.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$out=Join-Path $repo ('build/research-activation45/compiled-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/Campaign/CampaignIdentity.cs','src/MNBL.ProceduralWorld/Campaign/FactionFactory.cs','src/MNBL.ProceduralWorld/Campaign/CharacterTemplateResolver.cs','src/MNBL.ProceduralWorld/Campaign/HeroFactory.cs',
    'src/MNBL.ProceduralWorld/Campaign/SettlementFactory.cs','src/MNBL.ProceduralWorld/Campaign/VillageBinding.cs','src/MNBL.ProceduralWorld/Missions/MissionScenes.cs','src/MNBL.ProceduralWorld/Campaign/RegistrationPlan.cs','src/MNBL.ProceduralWorld/Campaign/GeneratedSpawnModel.cs',
    'src/MNBL.ProceduralWorld/Campaign/GeneratedCampaignInitializer.cs','src/MNBL.ProceduralWorld/Campaign/NavalContentFactory.cs','src/MNBL.ProceduralWorld/Campaign/ReferenceSchema.cs','src/MNBL.ProceduralWorld/Campaign/CampaignGraphValidator.cs',
    'src/MNBL.ProceduralWorld/Campaign/CompiledWorldDataset.cs','src/MNBL.ProceduralWorld/CampaignBootstrap/GeneratedCampaignDataset.cs','src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs','tests/Campaign/CompiledWorldTests.cs')
$templates=@(Get-ChildItem -LiteralPath (Join-Path $repo 'worldgen/templates/cultures') -Filter *.json|Sort-Object Name|ForEach-Object { 'worldgen/templates/cultures/'+$_.Name })
$data=@('tools/Test-CompiledWorld.ps1','worldgen/policies/graph-validation.json','worldgen/policies/naval-content.json','worldgen/policies/factions.json','worldgen/policies/rosters.json','worldgen/policies/economy.json',
    'worldgen/policies/startup.json','worldgen/policies/campaign-catalog.json','worldgen/templates/settlements/prototypes.json','worldgen/policies/culture-presentations.json','worldgen/policies/mission-scenes.json',
    'evidence/world/settlement-position-report.json')+$templates
$inputs=@(($sources+$data)|ForEach-Object { @{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $repo $_)).Hash.ToLowerInvariant()} })
$lock=Get-Content "$repo/config/runtime-api-lock.json" -Raw|ConvertFrom-Json
$references=@();$referenceEvidence=@()
foreach($name in @('mscorlib','System','System.Core','System.Xml','System.Web.Extensions')){
    $pin=@($lock.assemblies|Where-Object { $_.scope -eq 'framework' -and $_.name -eq $name })
    if($pin.Count -ne 1){throw "Missing unique framework pin: $name"}
    $path=Join-Path $lock.frameworkRoot $pin[0].path
    if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pin[0].sha256){throw "Framework pin mismatch: $path"}
    $references+="/reference:$path";$referenceEvidence+=@{path=$path;sha256=$pin[0].sha256}
}
$exe=Join-Path $out 'CompiledWorldTests.exe'
$compile=@(& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ }) 2>&1|ForEach-Object { "$_" })
if($LASTEXITCODE){$compile|ForEach-Object { Write-Output $_ }; [IO.File]::WriteAllLines("$out/compile.log",[string[]]$compile,(New-Object Text.UTF8Encoding($false))); throw 'Compiled world harness compilation failed'}
if(-not $Evidence){$Evidence=Join-Path $out 'compiled-world-tests.json'}
$lines=@(& $exe (Resolve-Path -LiteralPath $Bundle).Path $repo $Evidence 2>&1|ForEach-Object { "$_" })
$failed=$LASTEXITCODE
$lines|ForEach-Object { Write-Output $_ }
[IO.File]::WriteAllLines("$out/checks.log",[string[]]$lines,(New-Object Text.UTF8Encoding($false)))
if($Log){Copy-Item "$out/checks.log" $Log}
foreach($inputFile in $inputs){if((Get-FileHash -LiteralPath (Join-Path $repo $inputFile.path)).Hash.ToLowerInvariant() -ne $inputFile.sha256){throw "Input changed during harness: $($inputFile.path)"}}
@{schema='mnbl.compiled-world-offline.v1';sourceInputs=$inputs;referenceInputs=$referenceEvidence;executable=@{path=$exe;sha256=(Get-FileHash $exe).Hash.ToLowerInvariant()};scope='The compiled world dataset of a real bundle (factories, layout positions, merged documents, re-emission closure, graph validation, physical frame) against the pinned catalog capture. No game.'}|ConvertTo-Json -Depth 6|Set-Content "$out/receipt.json"
if($failed){throw 'Compiled world regression failed'}
Write-Output "Compiled world harness receipt: $out/receipt.json"
exit 0
