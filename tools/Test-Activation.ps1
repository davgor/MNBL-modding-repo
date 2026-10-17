param([string]$Evidence, [string]$Log)
# MMR-45: compiles the world activation transaction (Generation/WorldActivationTransaction.cs) with MMR-62's save binding pieces it uses
# (Persistence/WorldBinding.cs, DurableFile.cs, EarlyWorldBindingStore.cs) and tests/Generation/ActivationFailureTests.cs against the pinned
# framework assemblies, and runs the component harness: a clean activation, a failure injected at each commit boundary, a process stop after
# each boundary and inside the initial save's publication, recovery and its idempotence, the receipt contract. No game process (the in-game
# evidence is the world_activation probe).
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$out=Join-Path $repo ('build/research-activation45/test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/Generation/WorldActivationTransaction.cs','src/MNBL.ProceduralWorld/Persistence/WorldBinding.cs','src/MNBL.ProceduralWorld/Persistence/DurableFile.cs',
    'src/MNBL.ProceduralWorld/Persistence/EarlyWorldBindingStore.cs','src/MNBL.ProceduralWorld/Persistence/SaveJournal.cs','tests/Generation/ActivationFailureTests.cs')
$data=@('tools/Test-Activation.ps1')
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
$exe=Join-Path $out 'ActivationFailureTests.exe'
& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ })
if($LASTEXITCODE){throw 'Activation harness compilation failed'}
if(-not $Evidence){$Evidence=Join-Path $out 'activation-tests.json'}
$lines=@(& $exe $Evidence 2>&1|ForEach-Object { "$_" })
$failed=$LASTEXITCODE
$lines|ForEach-Object { Write-Output $_ }
[IO.File]::WriteAllLines("$out/checks.log",[string[]]$lines,(New-Object Text.UTF8Encoding($false)))
if($Log){Copy-Item "$out/checks.log" $Log}
foreach($inputFile in $inputs){if((Get-FileHash -LiteralPath (Join-Path $repo $inputFile.path)).Hash.ToLowerInvariant() -ne $inputFile.sha256){throw "Input changed during harness: $($inputFile.path)"}}
@{schema='mnbl.activation-offline.v1';sourceInputs=$inputs;referenceInputs=$referenceEvidence;executable=@{path=$exe;sha256=(Get-FileHash $exe).Hash.ToLowerInvariant()};scope='The activation transaction, its journal, rollback, receipt and recovery over a scripted host, a scripted save list and temporary directories, with MMR-62''s real initial-save journal. No game process.'}|ConvertTo-Json -Depth 6|Set-Content "$out/receipt.json"
if($failed){throw 'Activation regression failed'}
Write-Output "Activation harness receipt: $out/receipt.json"
exit 0
