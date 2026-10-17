param([Parameter(Mandatory=$true)][string]$Bundle, [string]$GameRoot='D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord', [string]$Evidence, [string]$Log)
# MMR-64: compiles the pure managed periodic coordinate and distance contract (World/PeriodicWorldCoordinates.cs, PeriodicDistanceModel.cs)
# with the surface model and the bundle verifier and tests/World/PeriodicCoordinateTests.cs against the pinned framework assemblies, and runs
# it: canonicalization, the tie rule, images, poles, frames, identities, seam splitting, cache keys, save compatibility, the policy file, a
# real compiled bundle's layout, ShortestOverImages against a periodic grid oracle (blocked corridor, capabilities, directed costs) and the
# coverage manifest against the pinned TaleWorlds.CampaignSystem and NavalDLC assemblies (loaded from the game at run time). No game process.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$out=Join-Path $repo ('build/research-periodic64/test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/World/PeriodicWorldCoordinates.cs','src/MNBL.ProceduralWorld/World/PeriodicDistanceModel.cs','src/MNBL.ProceduralWorld/World/WorldSurface.cs',
    'src/MNBL.ProceduralWorld/World/WorldLifetime.cs','src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs','src/MNBL.ProceduralWorld/CampaignBootstrap/PeriodicSpatialTable.cs',
    'tests/World/PeriodicCoordinateTests.cs')
$data=@('worldgen/policies/periodic-coordinates.json','tools/Test-PeriodicCoordinates.ps1')
$inputs=@(($sources+$data)|ForEach-Object { @{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $repo $_)).Hash.ToLowerInvariant()} })
$lock=Get-Content "$repo/config/runtime-api-lock.json" -Raw|ConvertFrom-Json
$references=@();$referenceEvidence=@()
foreach($name in @('mscorlib','netstandard','System','System.Core','System.Xml','System.Web.Extensions')){
    $pinned=@($lock.assemblies|Where-Object { $_.scope -eq 'framework' -and $_.name -eq $name })
    if($pinned.Count -ne 1){throw "Missing unique framework pin: $name"}
    $path=Join-Path $lock.frameworkRoot $pinned[0].path
    if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pinned[0].sha256){throw "Framework pin mismatch: $path"}
    $references+="/reference:$path";$referenceEvidence+=@{path=$path;sha256=$pinned[0].sha256}
}
# The distance model types are the game's own (pinned); they load from the game at run time for the coverage check.
foreach($name in @('TaleWorlds.CampaignSystem','NavalDLC')){
    $pinned=@($lock.assemblies|Where-Object { $_.scope -eq 'game' -and $_.name -eq $name })
    if($pinned.Count -ne 1){throw "Missing unique game pin: $name"}
    $path=Join-Path $GameRoot $pinned[0].path
    if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pinned[0].sha256){throw "Game assembly pin mismatch: $path"}
    $referenceEvidence+=@{path=$path;sha256=$pinned[0].sha256}
}
$exe=Join-Path $out 'PeriodicCoordinateTests.exe'
& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ })
if($LASTEXITCODE){throw 'Periodic coordinate harness compilation failed'}
if(-not $Evidence){$Evidence=Join-Path $out 'periodic-coordinates-tests.json'}
$lines=@(& $exe $repo (Resolve-Path -LiteralPath $Bundle).Path $GameRoot $Evidence 2>&1|ForEach-Object { "$_" })
$failed=$LASTEXITCODE
$lines|ForEach-Object { Write-Output $_ }
[IO.File]::WriteAllLines("$out/checks.log",[string[]]$lines,(New-Object Text.UTF8Encoding($false)))
if($Log){Copy-Item "$out/checks.log" $Log}
foreach($inputFile in $inputs){if((Get-FileHash -LiteralPath (Join-Path $repo $inputFile.path)).Hash.ToLowerInvariant() -ne $inputFile.sha256){throw "Input changed during harness: $($inputFile.path)"}}
@{schema='mnbl.periodic-coordinates-offline.v1';sourceInputs=$inputs;referenceInputs=$referenceEvidence;executable=@{path=$exe;sha256=(Get-FileHash $exe).Hash.ToLowerInvariant()};scope='Pure managed periodic coordinate and distance contract over synthetic layouts, grid oracles and one compiled bundle, with the coverage manifest against the pinned distance model assemblies; no game process (the engine evidence is the generated_campaign wrapProbe run).'}|ConvertTo-Json -Depth 5|Set-Content "$out/receipt.json"
if($failed){throw 'Periodic coordinate tests failed'}
Write-Output "Periodic coordinate harness receipt: $out/receipt.json"
