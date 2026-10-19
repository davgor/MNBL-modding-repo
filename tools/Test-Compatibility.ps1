param([string]$Bundle, [string]$Evidence, [string]$Log)
# MMR-50: compiles the compatibility policy (Persistence/WorldCompatibilityPolicy.cs) with the loader's rules (LoadPolicy.cs), the store
# readers it gathers facts with and tests/Persistence/CompatibilityTests.cs against the pinned framework assemblies, and runs them over real
# temporary stores of synthetic saves, packages and scenes. -Bundle: a real data bundle (default build/research-bundle29's seed-42 bundle)
# for the refusal text of a bundle built for another game version. No game process.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
if(-not $Bundle){$Bundle=(Get-ChildItem (Join-Path $repo 'build/research-bundle29/bundles') -Directory|Where-Object Name -like 'f596b58c*'|Select-Object -First 1).FullName}
$out=Join-Path $repo ('build/compatibility50/test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/Persistence/WorldCompatibilityPolicy.cs','src/MNBL.ProceduralWorld/Persistence/LoadPolicy.cs','src/MNBL.ProceduralWorld/Persistence/SaveBindingContract.cs',
    'src/MNBL.ProceduralWorld/Persistence/WorldSaveTransfer.cs','src/MNBL.ProceduralWorld/Persistence/FolderSaveCatalog.cs','src/MNBL.ProceduralWorld/Persistence/SaveFileCheck.cs',
    'src/MNBL.ProceduralWorld/Persistence/WorldReferenceIndex.cs','src/MNBL.ProceduralWorld/Persistence/StoreLock.cs','src/MNBL.ProceduralWorld/Persistence/WorldInspector.cs',
    'src/MNBL.ProceduralWorld/Persistence/StagingCleanup.cs','src/MNBL.ProceduralWorld/Persistence/DurableFile.cs','src/MNBL.ProceduralWorld/Persistence/WorldBinding.cs',
    'src/MNBL.ProceduralWorld/Persistence/EarlyWorldBindingStore.cs','src/MNBL.ProceduralWorld/Persistence/SaveJournal.cs','src/MNBL.ProceduralWorld/Persistence/SaveFolder.cs',
    'src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs','src/MNBL.ProceduralWorld/Generation/WorldRecoveryService.cs','src/MNBL.ProceduralWorld/Generation/JobStateMachine.cs',
    'src/MNBL.ProceduralWorld/Generation/ProgressEvents.cs','src/MNBL.ProceduralWorld/Generation/GenerationErrors.cs','src/MNBL.ProceduralWorld/Generation/WorldActivationTransaction.cs',
    'tests/Persistence/CompatibilityTests.cs')
$data=@('tools/Test-Compatibility.ps1','worldgen/contracts/save-type-ledger.json')+@(Get-ChildItem (Join-Path $repo 'tests/Persistence/fixtures/previous-saves') -Filter '*.header.json'|ForEach-Object { 'tests/Persistence/fixtures/previous-saves/'+$_.Name })
$inputs=@(($sources+$data)|ForEach-Object { @{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $repo $_)).Hash.ToLowerInvariant()} })
$lock=Get-Content "$repo/config/runtime-api-lock.json" -Raw|ConvertFrom-Json
$references=@();$referenceEvidence=@()
foreach($name in @('mscorlib','System','System.Core','System.Web.Extensions')){
    $pinned=@($lock.assemblies|Where-Object { $_.scope -eq 'framework' -and $_.name -eq $name })
    if($pinned.Count -ne 1){throw "Missing unique framework pin: $name"}
    $path=Join-Path $lock.frameworkRoot $pinned[0].path
    if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pinned[0].sha256){throw "Framework pin mismatch: $path"}
    $references+="/reference:$path";$referenceEvidence+=@{path=$path;sha256=$pinned[0].sha256}
}
$exe=Join-Path $out 'CompatibilityTests.exe'
# EarlyWorldBindingStore's header fields are assigned by the game's catalog, not by these tests: CS0649 is not an error here.
& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror /nowarn:0649 "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ })
if($LASTEXITCODE){throw 'Compatibility harness compilation failed'}
if(-not $Evidence){$Evidence=Join-Path $out 'compatibility-tests.json'}
$lines=@(& $exe $repo $Evidence $Bundle 2>&1|ForEach-Object { "$_" })
$failed=$LASTEXITCODE
$lines|ForEach-Object { Write-Output $_ }
[IO.File]::WriteAllLines("$out/checks.log",[string[]]$lines,(New-Object Text.UTF8Encoding($false)))
if($Log){Copy-Item "$out/checks.log" $Log}
foreach($inputFile in $inputs){if((Get-FileHash -LiteralPath (Join-Path $repo $inputFile.path)).Hash.ToLowerInvariant() -ne $inputFile.sha256){throw "Input changed during harness: $($inputFile.path)"}}
@{schema='mnbl.compatibility-offline.v1';sourceInputs=$inputs;referenceInputs=$referenceEvidence;executable=@{path=$exe;sha256=(Get-FileHash $exe).Hash.ToLowerInvariant()};scope='The compatibility policy (supported, rebuildable derived cache, unsupported world), its agreement with the loader, its facts from real temporary stores, the previous-save fixtures and that no migration is advertised. No game process.'}|ConvertTo-Json -Depth 6|Set-Content "$out/receipt.json"
if($failed){throw 'Compatibility regression failed'}
Write-Output "Compatibility harness receipt: $out/receipt.json"
exit 0
