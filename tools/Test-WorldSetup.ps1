param([Parameter(Mandatory=$true)][string]$BundleA, [Parameter(Mandatory=$true)][string]$BundleB, [string]$GameRoot='D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord', [string]$Evidence, [string]$Log)
# MMR-43: compiles the setup screen's logic (UI/WorldSetupModel.cs, Generation/GenerationErrors.cs) and view models (UI/WorldSetupViewModel.cs,
# for the prefab binding check) with tests/UI/WorldSetupTests.cs against the pinned framework and game assemblies, and runs them on the
# repository's setup documents, the generation request schema, two published bundles (their source/world.json and compiled
# campaign/campaign.json) and GUI/Prefabs/WorldSetup.xml. No game process.
# MMR-46: with the world preview (UI/WorldPreview.cs and what it reads, UI/WorldPreviewViewModel.cs, GUI/Prefabs/WorldPreview.xml) and the
# review flow; the engine texture is tests/UI/WorldPreviewTexturesStub.cs (the game runs the real one).
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$out=Join-Path $repo ('build/research-setup43/test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/UI/WorldSetupModel.cs','src/MNBL.ProceduralWorld/UI/WorldSetupViewModel.cs','src/MNBL.ProceduralWorld/Generation/GenerationErrors.cs',
    'src/MNBL.ProceduralWorld/Generation/ProgressEvents.cs','src/MNBL.ProceduralWorld/Generation/JobStateMachine.cs',
    'src/MNBL.ProceduralWorld/UI/WorldPreview.cs','src/MNBL.ProceduralWorld/UI/WorldPreviewViewModel.cs','src/MNBL.ProceduralWorld/World/WorldSurface.cs','src/MNBL.ProceduralWorld/World/WorldLifetime.cs',
    'src/MNBL.ProceduralWorld/World/PeriodicWorldCoordinates.cs','src/MNBL.ProceduralWorld/World/PeriodicDistanceModel.cs','src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs',
    'src/MNBL.ProceduralWorld/Campaign/CampaignIdentity.cs','src/MNBL.ProceduralWorld/Campaign/FactionFactory.cs','src/MNBL.ProceduralWorld/Campaign/VillageBinding.cs','src/MNBL.ProceduralWorld/Generation/WorldRecoveryService.cs','src/MNBL.ProceduralWorld/Generation/GenerationDiagnostics.cs','src/MNBL.ProceduralWorld/Persistence/StagingCleanup.cs','src/MNBL.ProceduralWorld/Persistence/DurableFile.cs','src/MNBL.ProceduralWorld/UI/GenerationErrorViewModel.cs','tests/UI/WorldPreviewTexturesStub.cs','tests/UI/WorldSetupTests.cs')
$data=@('worldgen/profiles/one-shot.json','worldgen/contracts/producer-controls.json','worldgen/contracts/producer-seed-width.json','worldgen/producer.lock.json','worldgen/policies/product-policy.json',
    'worldgen/policies/culture-presentations.json','worldgen/policies/one-shot-budget.json','worldgen/contracts/generation-request.schema.json',
    'Modules/MNBL.ProceduralWorld/GUI/Prefabs/WorldSetup.xml','Modules/MNBL.ProceduralWorld/GUI/Prefabs/WorldPreview.xml','tools/Test-WorldSetup.ps1',
    'worldgen/policies/factions.json','worldgen/policies/economy.json','evidence/world/settlement-position-report.json')+@(Get-ChildItem -LiteralPath (Join-Path $repo 'worldgen/templates/cultures') -Filter *.json|Sort-Object Name|ForEach-Object { 'worldgen/templates/cultures/'+$_.Name })
$inputs=@(($sources+$data)|ForEach-Object { @{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $repo $_)).Hash.ToLowerInvariant()} })
$bundles=@((Resolve-Path -LiteralPath $BundleA).Path,(Resolve-Path -LiteralPath $BundleB).Path)
foreach($bundle in $bundles){ foreach($file in @('source/world.json','compiled/campaign/campaign.json','compiled/campaign/id-map.json')){ $path=Join-Path $bundle $file; $inputs+=@{path=$path;sha256=(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()} } }
$lock=Get-Content "$repo/config/runtime-api-lock.json" -Raw|ConvertFrom-Json
$references=@();$referenceEvidence=@()
foreach($name in @('mscorlib','netstandard','System','System.Core','System.Xml','System.Web.Extensions')){
    $pinned=@($lock.assemblies|Where-Object { $_.scope -eq 'framework' -and $_.name -eq $name })
    if($pinned.Count -ne 1){throw "Missing unique framework pin: $name"}
    $path=Join-Path $lock.frameworkRoot $pinned[0].path
    if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pinned[0].sha256){throw "Framework pin mismatch: $path"}
    $references+="/reference:$path";$referenceEvidence+=@{path=$path;sha256=$pinned[0].sha256}
}
$xmlLinq=Join-Path $lock.frameworkRoot 'System.Xml.Linq.dll'
$references+="/reference:$xmlLinq";$referenceEvidence+=@{path=$xmlLinq;sha256=(Get-FileHash -LiteralPath $xmlLinq).Hash.ToLowerInvariant()}
foreach($name in @('TaleWorlds.Library','TaleWorlds.Localization','TaleWorlds.InputSystem','TaleWorlds.MountAndBlade.ViewModelCollection')){
    $pinned=@($lock.assemblies|Where-Object { $_.scope -eq 'game' -and $_.name -eq $name })
    if($pinned.Count -ne 1){throw "Missing unique game pin: $name"}
    $path=Join-Path $GameRoot $pinned[0].path
    if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pinned[0].sha256){throw "Game assembly pin mismatch: $path"}
    $references+="/reference:$path";$referenceEvidence+=@{path=$path;sha256=$pinned[0].sha256}
}
$exe=Join-Path $out 'WorldSetupTests.exe'
& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ })
if($LASTEXITCODE){throw 'World setup harness compilation failed'}
if(-not $Evidence){$Evidence=Join-Path $out 'world-setup-tests.json'}
$lines=@(& $exe $repo $bundles[0] $bundles[1] $GameRoot $Evidence 2>&1|ForEach-Object { "$_" })
$failed=$LASTEXITCODE
$lines|ForEach-Object { Write-Output $_ }
[IO.File]::WriteAllLines("$out/checks.log",[string[]]$lines,(New-Object Text.UTF8Encoding($false)))
if($Log){Copy-Item "$out/checks.log" $Log}
foreach($inputFile in $inputs){$path=if([IO.Path]::IsPathRooted($inputFile.path)){$inputFile.path}else{Join-Path $repo $inputFile.path}; if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $inputFile.sha256){throw "Input changed during harness: $($inputFile.path)"}}
@{schema='mnbl.world-setup-offline.v1';sourceInputs=$inputs;referenceInputs=$referenceEvidence;executable=@{path=$exe;sha256=(Get-FileHash $exe).Hash.ToLowerInvariant()};scope='Pure setup logic, error categories and prefab bindings against the view models; no game process (the in-game evidence is the world_setup probe).'}|ConvertTo-Json -Depth 5|Set-Content "$out/receipt.json"
if($failed){throw 'World setup regression failed'}
Write-Output "World setup harness receipt: $out/receipt.json"
