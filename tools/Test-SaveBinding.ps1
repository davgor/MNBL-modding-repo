param([string]$Evidence, [string]$Log)
# MMR-48: compiles the save binding's pure parts (Persistence/SaveJournal.cs, SaveBindingContract.cs, WorldSaveTransfer.cs, FolderSaveCatalog.cs
# with the MMR-62 store they reuse: WorldBinding.cs, DurableFile.cs, EarlyWorldBindingStore.cs, WorldReferenceIndex.cs, StagingCleanup.cs,
# StoreLock.cs, SaveFileCheck.cs, SaveFolder.cs; Campaign/CampaignIdentity.cs for SaveIds) with tests/Persistence/SaveBindingTests.cs against
# the pinned framework assemblies and runs them: later saves of every kind, the interrupted-save recovery test, the reference index and its
# counts, the compatibility keys, the save type ledger, the contract schema, export and import. No game process (the in-game evidence is
# the save_binding probe).
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$out=Join-Path $repo ('build/save-binding48/test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/Persistence/WorldBinding.cs','src/MNBL.ProceduralWorld/Persistence/DurableFile.cs','src/MNBL.ProceduralWorld/Persistence/EarlyWorldBindingStore.cs',
    'src/MNBL.ProceduralWorld/Persistence/SaveJournal.cs','src/MNBL.ProceduralWorld/Persistence/WorldReferenceIndex.cs','src/MNBL.ProceduralWorld/Persistence/StagingCleanup.cs',
    'src/MNBL.ProceduralWorld/Persistence/StoreLock.cs','src/MNBL.ProceduralWorld/Persistence/SaveFileCheck.cs','src/MNBL.ProceduralWorld/Persistence/SaveFolder.cs',
    'src/MNBL.ProceduralWorld/Persistence/FolderSaveCatalog.cs','src/MNBL.ProceduralWorld/Persistence/SaveBindingContract.cs','src/MNBL.ProceduralWorld/Persistence/WorldSaveTransfer.cs',
    'src/MNBL.ProceduralWorld/Campaign/CampaignIdentity.cs','tests/Persistence/SaveBindingTests.cs')
$data=@('tools/Test-SaveBinding.ps1','worldgen/contracts/save-binding.schema.json','worldgen/contracts/save-type-ledger.json','tests/Persistence/fixtures/save-type-ledger.v1.json')
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
$exe=Join-Path $out 'SaveBindingTests.exe'
& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ })
if($LASTEXITCODE){throw 'Save binding harness compilation failed'}
if(-not $Evidence){$Evidence=Join-Path $out 'save-binding-tests.json'}
$lines=@(& $exe $repo $Evidence 2>&1|ForEach-Object { "$_" })
$failed=$LASTEXITCODE
$lines|ForEach-Object { Write-Output $_ }
[IO.File]::WriteAllLines("$out/checks.log",[string[]]$lines,(New-Object Text.UTF8Encoding($false)))
if($Log){Copy-Item "$out/checks.log" $Log}
foreach($inputFile in $inputs){if((Get-FileHash -LiteralPath (Join-Path $repo $inputFile.path)).Hash.ToLowerInvariant() -ne $inputFile.sha256){throw "Input changed during harness: $($inputFile.path)"}}
@{schema='mnbl.save-binding-offline.v1';sourceInputs=$inputs;referenceInputs=$referenceEvidence;executable=@{path=$exe;sha256=(Get-FileHash $exe).Hash.ToLowerInvariant()};scope='The later-save journal, its interrupted-save recovery, the reference index with every save kind bound, the compatibility keys, the save type ledger, the contract schema and the export/import of saves with their world, over real temporary save folders of synthetic saves in the installed format. No game process.'}|ConvertTo-Json -Depth 6|Set-Content "$out/receipt.json"
if($failed){throw 'Save binding regression failed'}
Write-Output "Save binding harness receipt: $out/receipt.json"
exit 0
