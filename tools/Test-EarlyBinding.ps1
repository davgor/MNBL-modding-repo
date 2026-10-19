param([string]$Evidence, [string]$Log)
# MMR-62: compiles the early save binding's pure parts (Persistence/WorldBinding.cs, DurableFile.cs, EarlyWorldBindingStore.cs,
# WorldReferenceIndex.cs, StagingCleanup.cs, StoreLock.cs, SaveFileCheck.cs, SaveFolder.cs) with tests/Persistence/EarlyBindingTests.cs against the pinned
# framework assemblies and runs them: the binding, the initial-save boundary matrix, the reference index, staging cleanup, the durable
# file, the store lock and the save integrity check. No game process (the in-game evidence is generated_campaign initialSaveProbe=1 then
# save_recovery).
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$out=Join-Path $repo ('build/research-binding62/test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/Persistence/WorldBinding.cs','src/MNBL.ProceduralWorld/Persistence/DurableFile.cs','src/MNBL.ProceduralWorld/Persistence/EarlyWorldBindingStore.cs','src/MNBL.ProceduralWorld/Persistence/SaveJournal.cs',
    'src/MNBL.ProceduralWorld/Persistence/WorldReferenceIndex.cs','src/MNBL.ProceduralWorld/Persistence/StagingCleanup.cs','src/MNBL.ProceduralWorld/Persistence/StoreLock.cs',
    'src/MNBL.ProceduralWorld/Persistence/SaveFileCheck.cs','src/MNBL.ProceduralWorld/Persistence/SaveFolder.cs','tests/Persistence/EarlyBindingTests.cs')
$data=@('tools/Test-EarlyBinding.ps1')
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
$exe=Join-Path $out 'EarlyBindingTests.exe'
& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ })
if($LASTEXITCODE){throw 'Early binding harness compilation failed'}
if(-not $Evidence){$Evidence=Join-Path $out 'early-binding-tests.json'}
$lines=@(& $exe $Evidence 2>&1|ForEach-Object { "$_" })
$failed=$LASTEXITCODE
$lines|ForEach-Object { Write-Output $_ }
[IO.File]::WriteAllLines("$out/checks.log",[string[]]$lines,(New-Object Text.UTF8Encoding($false)))
if($Log){Copy-Item "$out/checks.log" $Log}
foreach($inputFile in $inputs){if((Get-FileHash -LiteralPath (Join-Path $repo $inputFile.path)).Hash.ToLowerInvariant() -ne $inputFile.sha256){throw "Input changed during harness: $($inputFile.path)"}}
@{schema='mnbl.early-binding-offline.v1';sourceInputs=$inputs;referenceInputs=$referenceEvidence;executable=@{path=$exe;sha256=(Get-FileHash $exe).Hash.ToLowerInvariant()};scope='The pure binding, journal store, reference index, staging cleanup, durable file, store lock and save integrity check over a scripted save list and temporary directories. No game process.'}|ConvertTo-Json -Depth 6|Set-Content "$out/receipt.json"
if($failed){throw 'Early binding regression failed'}
Write-Output "Early binding harness receipt: $out/receipt.json"
exit 0
