param([Parameter(Mandatory=$true)][string]$BundleA, [string]$Evidence, [string]$Log)
# MMR-47: compiles the recovery pieces (Generation/GenerationDiagnostics.cs, GenerationErrors.cs, WorldRecoveryService.cs, JobStateMachine.cs,
# Persistence/WorldInspector.cs, StagingCleanup.cs, DurableFile.cs, the bundle verifier and the activation journal) with
# tests/Generation/RecoveryTests.cs against the pinned framework assemblies, and runs them on temporary stores (junctions made with mklink /J)
# and a copy of a real published bundle: the failure matrix with correlation ids and retained diagnostics, the data root, unfinished jobs,
# discard, cleanup, and bundle and store inspection. No game process.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$out=Join-Path $repo ('build/research-recovery47/test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/Generation/GenerationDiagnostics.cs','src/MNBL.ProceduralWorld/Generation/GenerationErrors.cs','src/MNBL.ProceduralWorld/Generation/WorldRecoveryService.cs',
    'src/MNBL.ProceduralWorld/Generation/JobStateMachine.cs','src/MNBL.ProceduralWorld/Generation/ProgressEvents.cs','src/MNBL.ProceduralWorld/Generation/WorldActivationTransaction.cs',
    'src/MNBL.ProceduralWorld/Persistence/WorldCompatibilityPolicy.cs','src/MNBL.ProceduralWorld/Persistence/LoadPolicy.cs','src/MNBL.ProceduralWorld/Persistence/SaveBindingContract.cs',
    'src/MNBL.ProceduralWorld/Persistence/WorldSaveTransfer.cs','src/MNBL.ProceduralWorld/Persistence/FolderSaveCatalog.cs','src/MNBL.ProceduralWorld/Persistence/SaveFileCheck.cs',
    'src/MNBL.ProceduralWorld/Persistence/WorldReferenceIndex.cs','src/MNBL.ProceduralWorld/Persistence/StoreLock.cs','src/MNBL.ProceduralWorld/Persistence/WorldInspector.cs','src/MNBL.ProceduralWorld/Persistence/StagingCleanup.cs','src/MNBL.ProceduralWorld/Persistence/DurableFile.cs',
    'src/MNBL.ProceduralWorld/Persistence/WorldBinding.cs','src/MNBL.ProceduralWorld/Persistence/EarlyWorldBindingStore.cs','src/MNBL.ProceduralWorld/Persistence/SaveJournal.cs','src/MNBL.ProceduralWorld/Persistence/SaveFolder.cs','src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs',
    'tests/Generation/RecoveryTests.cs')
$data=@('tools/Test-Recovery.ps1')
$inputs=@(($sources+$data)|ForEach-Object { @{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $repo $_)).Hash.ToLowerInvariant()} })
$bundle=(Resolve-Path -LiteralPath $BundleA).Path
$inputs+=@{path=(Join-Path $bundle 'manifest.json');sha256=(Get-FileHash -LiteralPath (Join-Path $bundle 'manifest.json')).Hash.ToLowerInvariant()}
$lock=Get-Content "$repo/config/runtime-api-lock.json" -Raw|ConvertFrom-Json
$references=@();$referenceEvidence=@()
foreach($name in @('mscorlib','System','System.Core','System.Web.Extensions')){
    $pinned=@($lock.assemblies|Where-Object { $_.scope -eq 'framework' -and $_.name -eq $name })
    if($pinned.Count -ne 1){throw "Missing unique framework pin: $name"}
    $path=Join-Path $lock.frameworkRoot $pinned[0].path
    if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pinned[0].sha256){throw "Framework pin mismatch: $path"}
    $references+="/reference:$path";$referenceEvidence+=@{path=$path;sha256=$pinned[0].sha256}
}
$exe=Join-Path $out 'RecoveryTests.exe'
$compile=@(& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ }) 2>&1|ForEach-Object { "$_" })
if($LASTEXITCODE){$compile|ForEach-Object { Write-Output $_ }; throw 'Recovery harness compilation failed'}
if(-not $Evidence){$Evidence=Join-Path $out 'recovery-tests.json'}
$lines=@(& $exe $repo $bundle $Evidence 2>&1|ForEach-Object { "$_" })
$failed=$LASTEXITCODE
$lines|ForEach-Object { Write-Output $_ }
[IO.File]::WriteAllLines("$out/checks.log",[string[]]$lines,(New-Object Text.UTF8Encoding($false)))
if($Log){Copy-Item "$out/checks.log" $Log}
foreach($inputFile in $inputs){$p=if([IO.Path]::IsPathRooted($inputFile.path)){$inputFile.path}else{Join-Path $repo $inputFile.path}; if((Get-FileHash -LiteralPath $p).Hash.ToLowerInvariant() -ne $inputFile.sha256){throw "Input changed during harness: $($inputFile.path)"}}
@{schema='mnbl.recovery-offline.v1';sourceInputs=$inputs;referenceInputs=$referenceEvidence;executable=@{path=$exe;sha256=(Get-FileHash $exe).Hash.ToLowerInvariant()};scope='Failure references and retained diagnostics, the data root, unfinished jobs, discard, cleanup and inspection over temporary stores and a copy of a published bundle. No game process.'}|ConvertTo-Json -Depth 6|Set-Content "$out/receipt.json"
if($failed){throw 'Recovery regression failed'}
Write-Output "Recovery harness receipt: $out/receipt.json"
exit 0
