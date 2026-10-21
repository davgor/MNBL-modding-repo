param([Parameter(Mandatory=$true)][string]$BundleA, [string]$GameRoot='D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord', [string]$Evidence, [string]$Log)
# MMR-283: compiles the reduced catalog pin (Generation/CatalogPin.cs), the module attribution (Generation/ModuleSources.cs), the battle table
# check and map rebuild (Missions/GeneratedEncounterResolver.cs), the setup and load refusal texts (GenerationErrors.cs, LoadPolicy.cs) and the
# compiled world dataset with its factories, with tests/Generation/ContentCompatibilityTests.cs against the pinned framework assemblies, and runs
# it on the catalog capture, world A's bundle and the installed game's battle-scene and module files: synthetic content modules (written into
# the harness folder) are accepted or refused naming the module and the object; scene packs are absorbed or refused before any cache build.
# No game process.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$out=Join-Path $repo ('build/content-compatibility/test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/Campaign/CampaignIdentity.cs','src/MNBL.ProceduralWorld/Campaign/FactionFactory.cs','src/MNBL.ProceduralWorld/Campaign/CharacterTemplateResolver.cs','src/MNBL.ProceduralWorld/Campaign/HeroFactory.cs',
    'src/MNBL.ProceduralWorld/Campaign/SettlementFactory.cs','src/MNBL.ProceduralWorld/Campaign/VillageBinding.cs','src/MNBL.ProceduralWorld/Missions/MissionScenes.cs','src/MNBL.ProceduralWorld/Campaign/RegistrationPlan.cs','src/MNBL.ProceduralWorld/Campaign/GeneratedSpawnModel.cs',
    'src/MNBL.ProceduralWorld/Campaign/GeneratedCampaignInitializer.cs','src/MNBL.ProceduralWorld/Campaign/NavalContentFactory.cs','src/MNBL.ProceduralWorld/Campaign/ReferenceSchema.cs','src/MNBL.ProceduralWorld/Campaign/CampaignGraphValidator.cs',
    'src/MNBL.ProceduralWorld/Campaign/CompiledWorldDataset.cs','src/MNBL.ProceduralWorld/Campaign/PopulationRuntime.cs','src/MNBL.ProceduralWorld/CampaignBootstrap/GeneratedCampaignDataset.cs','src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs',
    'src/MNBL.ProceduralWorld/Generation/CatalogPin.cs','src/MNBL.ProceduralWorld/Generation/ModuleSources.cs','src/MNBL.ProceduralWorld/Missions/GeneratedEncounterResolver.cs',
    'src/MNBL.ProceduralWorld/Generation/GenerationErrors.cs','src/MNBL.ProceduralWorld/Persistence/LoadPolicy.cs','tests/Generation/ContentCompatibilityTests.cs')
$templates=@(Get-ChildItem -LiteralPath (Join-Path $repo 'worldgen/templates/cultures') -Filter *.json|Sort-Object Name|ForEach-Object { 'worldgen/templates/cultures/'+$_.Name })
$data=@('tools/Test-ContentCompatibility.ps1','worldgen/policies/graph-validation.json','worldgen/policies/naval-content.json','worldgen/policies/factions.json','worldgen/policies/rosters.json','worldgen/policies/economy.json',
    'worldgen/policies/startup.json','worldgen/policies/campaign-catalog.json','worldgen/templates/settlements/prototypes.json','worldgen/policies/culture-presentations.json','worldgen/policies/mission-scenes.json',
    'evidence/world/settlement-position-report.json','src/MNBL.ProceduralWorld/World/GeneratedMapScene.cs')+$templates
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
$exe=Join-Path $out 'ContentCompatibilityTests.exe'
$compile=@(& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /nowarn:649 /warnaserror "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ }) 2>&1|ForEach-Object { "$_" })
if($LASTEXITCODE){$compile|ForEach-Object { Write-Output $_ }; [IO.File]::WriteAllLines("$out/compile.log",[string[]]$compile,(New-Object Text.UTF8Encoding($false))); throw 'Content compatibility harness compilation failed'}
if(-not $Evidence){$Evidence=Join-Path $out 'content-compatibility-tests.json'}
$scratch=Join-Path $out 'modules'; New-Item -ItemType Directory -Path $scratch | Out-Null
$lines=@(& $exe $repo (Resolve-Path -LiteralPath $BundleA).Path $GameRoot (Join-Path $scratch 'evidence.json') 2>&1|ForEach-Object { "$_" })
$failed=$LASTEXITCODE
Copy-Item (Join-Path $scratch 'evidence.json') $Evidence
$lines|ForEach-Object { Write-Output $_ }
[IO.File]::WriteAllLines("$out/checks.log",[string[]]$lines,(New-Object Text.UTF8Encoding($false)))
if($Log){Copy-Item "$out/checks.log" $Log}
foreach($inputFile in $inputs){if((Get-FileHash -LiteralPath (Join-Path $repo $inputFile.path)).Hash.ToLowerInvariant() -ne $inputFile.sha256){throw "Input changed during harness: $($inputFile.path)"}}
@{schema='mnbl.content-compatibility-offline.v1';sourceInputs=$inputs;referenceInputs=$referenceEvidence;bundle=(Resolve-Path -LiteralPath $BundleA).Path;gameRoot=$GameRoot;executable=@{path=$exe;sha256=(Get-FileHash $exe).Hash.ToLowerInvariant()};scope='The reduced catalog pin, synthetic content modules at setup and load, the battle table check and map rebuild, and the refusal texts. No game.'}|ConvertTo-Json -Depth 6|Set-Content "$out/receipt.json"
if($failed){throw 'Content compatibility regression failed'}
Write-Output "Content compatibility harness receipt: $out/receipt.json"
exit 0
