param([string]$Evidence, [string]$Log)
# MMR-282: compiles the world store's sweep and scene archive (Persistence/WorldStoreSweep.cs) with the reference index and the save
# journals it reads, the load's scene and build rules (LoadPolicy.cs), the navigation cache forecast and wait model
# (World/NavigationCacheForecast.cs) and tests/Persistence/WorldStoreTests.cs against the pinned framework assemblies, and runs them over
# real temporary stores and the repository's worldgen/policies/one-shot-budget.json. No game process.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$out=Join-Path $repo ('build/world-store282/test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/Persistence/WorldStoreSweep.cs','src/MNBL.ProceduralWorld/Persistence/WorldReferenceIndex.cs','src/MNBL.ProceduralWorld/Persistence/WorldBinding.cs',
    'src/MNBL.ProceduralWorld/Persistence/DurableFile.cs','src/MNBL.ProceduralWorld/Persistence/EarlyWorldBindingStore.cs','src/MNBL.ProceduralWorld/Persistence/SaveJournal.cs',
    'src/MNBL.ProceduralWorld/Persistence/StagingCleanup.cs','src/MNBL.ProceduralWorld/Persistence/StoreLock.cs','src/MNBL.ProceduralWorld/Persistence/SaveFileCheck.cs',
    'src/MNBL.ProceduralWorld/Persistence/SaveFolder.cs','src/MNBL.ProceduralWorld/Persistence/LoadPolicy.cs','src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs','src/MNBL.ProceduralWorld/World/WorldLifetime.cs',
    'src/MNBL.ProceduralWorld/World/NavigationCacheKey.cs','src/MNBL.ProceduralWorld/World/NavigationCacheForecast.cs','tests/Persistence/WorldStoreTests.cs')
$data=@('worldgen/policies/one-shot-budget.json','tools/Test-WorldStore.ps1')
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
$exe=Join-Path $out 'WorldStoreTests.exe'
# EarlyWorldBindingStore's header fields are assigned by the game's catalog, not by these tests: CS0649 is not an error here.
& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror /nowarn:0649 "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ })
if($LASTEXITCODE){throw 'World store harness compilation failed'}
if(-not $Evidence){$Evidence=Join-Path $out 'world-store-tests.json'}
$lines=@(& $exe $repo $Evidence 2>&1|ForEach-Object { "$_" })
$failed=$LASTEXITCODE
$lines|ForEach-Object { Write-Output $_ }
[IO.File]::WriteAllLines("$out/checks.log",[string[]]$lines,(New-Object Text.UTF8Encoding($false)))
if($Log){Copy-Item "$out/checks.log" $Log}
foreach($inputFile in $inputs){if((Get-FileHash -LiteralPath (Join-Path $repo $inputFile.path)).Hash.ToLowerInvariant() -ne $inputFile.sha256){throw "Input changed during harness: $($inputFile.path)"}}
@{schema='mnbl.world-store-offline.v1';sourceInputs=$inputs;referenceInputs=$referenceEvidence;executable=@{path=$exe;sha256=(Get-FileHash $exe).Hash.ToLowerInvariant()};scope='The world store sweep (references from packages, saves and this process; fail closed), the scene archive, an earlier build''s binding with its kept scene, the navigation cache forecast and the wait model. No game process.'}|ConvertTo-Json -Depth 6|Set-Content "$out/receipt.json"
if($failed){throw 'World store regression failed'}
Write-Output "World store harness receipt: $out/receipt.json"
exit 0
