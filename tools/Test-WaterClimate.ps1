param([Parameter(Mandatory=$true)][string]$Bundle, [string]$GameRoot='D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord', [string]$Evidence, [string]$Log)
# MMR-39: compiles the pure managed climate synthesis and water system (World/GeneratedClimateSystem.cs, GeneratedWaterSystem.cs,
# GeneratedStormPolicy.cs) with the coordinate contract, the surface model, the bundle verifier and the climate binding
# (CampaignBootstrap/GeneratedClimate.cs, against the pinned TaleWorlds.Library) and tests/World/WaterClimateTests.cs, and runs them on a
# real compiled bundle. No game process.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$out=Join-Path $repo ('build/research-water39/test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/World/GeneratedClimateSystem.cs','src/MNBL.ProceduralWorld/World/GeneratedWaterSystem.cs','src/MNBL.ProceduralWorld/World/GeneratedStormPolicy.cs',
    'src/MNBL.ProceduralWorld/World/PeriodicWorldCoordinates.cs','src/MNBL.ProceduralWorld/World/PeriodicDistanceModel.cs','src/MNBL.ProceduralWorld/World/WorldSurface.cs',
    'src/MNBL.ProceduralWorld/World/WorldLifetime.cs','src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs','src/MNBL.ProceduralWorld/CampaignBootstrap/GeneratedClimate.cs',
    'tests/World/WaterClimateTests.cs')
$data=@('worldgen/policies/climate-flow.json','tools/Test-WaterClimate.ps1')
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
foreach($name in @('TaleWorlds.Library')){
    $pinned=@($lock.assemblies|Where-Object { $_.scope -eq 'game' -and $_.name -eq $name })
    if($pinned.Count -ne 1){throw "Missing unique game pin: $name"}
    $path=Join-Path $GameRoot $pinned[0].path
    if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pinned[0].sha256){throw "Game assembly pin mismatch: $path"}
    $references+="/reference:$path";$referenceEvidence+=@{path=$path;sha256=$pinned[0].sha256}
}
$exe=Join-Path $out 'WaterClimateTests.exe'
& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ })
if($LASTEXITCODE){throw 'Water/climate harness compilation failed'}
Copy-Item (Join-Path $GameRoot 'bin/Win64_Shipping_Client/TaleWorlds.Library.dll') $out
if(-not $Evidence){$Evidence=Join-Path $out 'water-climate-tests.json'}
$lines=@(& $exe $repo (Resolve-Path -LiteralPath $Bundle).Path $Evidence 2>&1|ForEach-Object { "$_" })
$failed=$LASTEXITCODE
$lines|ForEach-Object { Write-Output $_ }
[IO.File]::WriteAllLines("$out/checks.log",[string[]]$lines,(New-Object Text.UTF8Encoding($false)))
if($Log){Copy-Item "$out/checks.log" $Log}
foreach($inputFile in $inputs){if((Get-FileHash -LiteralPath (Join-Path $repo $inputFile.path)).Hash.ToLowerInvariant() -ne $inputFile.sha256){throw "Input changed during harness: $($inputFile.path)"}}
@{schema='mnbl.water-climate-offline.v1';sourceInputs=$inputs;referenceInputs=$referenceEvidence;executable=@{path=$exe;sha256=(Get-FileHash $exe).Hash.ToLowerInvariant()};scope='Pure managed climate synthesis, water system and storm policy on one compiled bundle; no game process (the engine evidence is the generated_campaign waterClimateProbe run).'}|ConvertTo-Json -Depth 5|Set-Content "$out/receipt.json"
if($failed){throw 'Water/climate tests failed'}
Write-Output "Water/climate harness receipt: $out/receipt.json"
