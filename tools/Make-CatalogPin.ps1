param()
# MMR-283: writes worldgen/policies/campaign-catalog.json (v2): the installed objects a generated world can reference, each by key, element and a
# fingerprint of the fields the world reads (Generation/CatalogPin.cs), from the campaign_catalog capture and the default inventory the world
# policies were made from (rosters.json provenance). Compiles tests/Generation/CatalogPinTool.cs with the module's pure sources against the
# pinned framework assemblies and runs it. No game. Re-run after a re-capture of the catalog or a policy change that names other objects.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$out=Join-Path $repo ('build/catalog-pin/'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/Campaign/CampaignIdentity.cs','src/MNBL.ProceduralWorld/Campaign/FactionFactory.cs','src/MNBL.ProceduralWorld/Campaign/CharacterTemplateResolver.cs','src/MNBL.ProceduralWorld/Campaign/HeroFactory.cs',
    'src/MNBL.ProceduralWorld/Campaign/SettlementFactory.cs','src/MNBL.ProceduralWorld/Campaign/VillageBinding.cs','src/MNBL.ProceduralWorld/Missions/MissionScenes.cs','src/MNBL.ProceduralWorld/Campaign/RegistrationPlan.cs','src/MNBL.ProceduralWorld/Campaign/GeneratedSpawnModel.cs',
    'src/MNBL.ProceduralWorld/Campaign/GeneratedCampaignInitializer.cs','src/MNBL.ProceduralWorld/Campaign/NavalContentFactory.cs','src/MNBL.ProceduralWorld/Campaign/ReferenceSchema.cs','src/MNBL.ProceduralWorld/Campaign/CampaignGraphValidator.cs',
    'src/MNBL.ProceduralWorld/Campaign/CompiledWorldDataset.cs','src/MNBL.ProceduralWorld/Campaign/PopulationRuntime.cs','src/MNBL.ProceduralWorld/CampaignBootstrap/GeneratedCampaignDataset.cs','src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs',
    'src/MNBL.ProceduralWorld/Generation/CatalogPin.cs','src/MNBL.ProceduralWorld/Generation/ModuleSources.cs','tests/Generation/CatalogPinTool.cs')
$lock=Get-Content "$repo/config/runtime-api-lock.json" -Raw|ConvertFrom-Json
$references=@()
foreach($name in @('mscorlib','System','System.Core','System.Xml','System.Web.Extensions')){
    $pin=@($lock.assemblies|Where-Object { $_.scope -eq 'framework' -and $_.name -eq $name })
    if($pin.Count -ne 1){throw "Missing unique framework pin: $name"}
    $path=Join-Path $lock.frameworkRoot $pin[0].path
    if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pin[0].sha256){throw "Framework pin mismatch: $path"}
    $references+="/reference:$path"
}
$exe=Join-Path $out 'CatalogPinTool.exe'
$compile=@(& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /nowarn:649 /warnaserror "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ }) 2>&1|ForEach-Object { "$_" })
if($LASTEXITCODE){$compile|ForEach-Object { Write-Output $_ }; throw 'Catalog pin tool compilation failed'}
& $exe $repo
if($LASTEXITCODE){throw 'Catalog pin tool failed'}
exit 0
