param([string]$CatalogDirectory, [string]$DefaultReferences,
    [string]$CultureId='empire', [string]$HeroTemplateId='spc_legion_of_the_betrayed_leader_0',
    [string]$TownPrototypeId='town_EN2', [string]$VillagePrototypeId='village_EN1_3',
    [string]$Placement42, [string]$Placement43, [string]$DefaultProvenance)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$out=Join-Path $repo ('build/research-bootstrap18/test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$paths=@('src/MNBL.ProceduralWorld/CampaignBootstrap/GeneratedCampaignDataset.cs','tests/GeneratedCampaignDatasetTests.cs','tools/Test-GeneratedCampaignDataset.ps1')
$inputs=@($paths|ForEach-Object { @{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $repo $_)).Hash.ToLowerInvariant()} })
$lock=Get-Content "$repo/config/runtime-api-lock.json" -Raw|ConvertFrom-Json
$references=@();$referenceEvidence=@()
foreach($name in @('mscorlib','System','System.Core','System.Xml','System.Web.Extensions')){
    $pin=@($lock.assemblies|Where-Object { $_.scope -eq 'framework' -and $_.name -eq $name })
    if($pin.Count -ne 1){throw "Missing unique framework pin: $name"}
    $path=Join-Path $lock.frameworkRoot $pin[0].path
    $hash=(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()
    if($hash -ne $pin[0].sha256){throw "Framework pin mismatch: $path"}
    $references+="/reference:$path";$referenceEvidence+=@{path=$path;sha256=$hash}
}
$catalogInputs=@()
if($CatalogDirectory){$catalogInputs=@(Get-ChildItem -LiteralPath $CatalogDirectory -Filter *.xml|ForEach-Object {@{path=$_.FullName;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}})}
foreach($path in @($DefaultReferences,$DefaultProvenance,$Placement42,$Placement43)|Where-Object {$_}){$catalogInputs+=@{path=(Resolve-Path -LiteralPath $path).Path;sha256=(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()}}
$exe=Join-Path $out 'GeneratedCampaignDatasetTests.exe'
& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references "$repo/src/MNBL.ProceduralWorld/CampaignBootstrap/GeneratedCampaignDataset.cs" "$repo/tests/GeneratedCampaignDatasetTests.cs"
if($LASTEXITCODE){throw 'Dataset harness compilation failed'}
& $exe 2>&1 | Tee-Object "$out/checks.log"
if($LASTEXITCODE){throw 'Dataset managed regression failed'}
if($CatalogDirectory){
    if(-not $DefaultReferences){throw 'Explicit verified default reference file required for catalog check'}
    $catalogArgs=@('--catalog',$CatalogDirectory,$DefaultReferences,$CultureId,$HeroTemplateId,$TownPrototypeId,$VillagePrototypeId)
    if($Placement42 -or $Placement43){if(-not ($Placement42 -and $Placement43)){throw 'Both placement descriptors required'};$catalogArgs+=@($Placement42,$Placement43,(Join-Path $out 'datasets'))}
    & $exe @catalogArgs 2>&1 | Tee-Object "$out/catalog.log"
    if($LASTEXITCODE){throw 'Dataset real-catalog preflight failed'}
}
foreach($inputFile in $inputs){if((Get-FileHash -LiteralPath (Join-Path $repo $inputFile.path)).Hash.ToLowerInvariant() -ne $inputFile.sha256){throw "Source changed during harness: $($inputFile.path)"}}
foreach($inputFile in $catalogInputs){if((Get-FileHash -LiteralPath $inputFile.path).Hash.ToLowerInvariant() -ne $inputFile.sha256){throw "Input changed during harness: $($inputFile.path)"}}
@{schema='mnbl.generated-dataset-offline.v1';sourceInputs=$inputs;referenceInputs=$referenceEvidence;catalogInputs=$catalogInputs;compiler=@{path=$csc;sha256=(Get-FileHash $csc).Hash.ToLowerInvariant()};executable=@{path=$exe;sha256=(Get-FileHash $exe).Hash.ToLowerInvariant()};catalogExecuted=[bool]$CatalogDirectory;scope='Pure managed graph/XML regression; no game, registry, native scene or save-load claim.'}|ConvertTo-Json -Depth 6|Set-Content "$out/receipt.json"
Write-Output "Dataset harness receipt: $out/receipt.json"
