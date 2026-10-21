param([Parameter(Mandatory=$true)][string]$BundleA, [string]$Producer, [switch]$Record, [string]$GameRoot='D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord', [string]$Evidence)
# MMR-261: compiles the pure managed live mutation (World/WorldMutations.cs: the capital rule, the residue batch, fwg.operations and
# fwg.ley_delta, the canonical SyncData document, the producer archive, the background runner) with MMR-260's overlay and its readers, and
# tests/World/WorldMutationTests.cs against the pinned framework assemblies and the pinned TaleWorlds.Core, and runs them on the real
# compiled world A' (seed 42). No game process.
#   -Producer <fwg.exe>  also runs the real fwg apply on the background runner (the pinned build; about 3 s) and requires the recorded delta.
#   -Record              (with -Producer) first records tests/World/fixtures/mutation from that producer: the bundle's request.json, the
#                        operations the capital rule emits for a fire cast at Nerdun and the delta fwg apply writes.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$out=Join-Path $repo ('build/world-mutation261/test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/World/WorldMutations.cs','src/MNBL.ProceduralWorld/World/MagicGround.cs','src/MNBL.ProceduralWorld/World/MagicVeins.cs','src/MNBL.ProceduralWorld/World/MapDressing.cs','src/MNBL.ProceduralWorld/World/NavigationCacheKey.cs',
    'src/MNBL.ProceduralWorld/Missions/MissionScenes.cs','src/MNBL.ProceduralWorld/Missions/GeneratedEncounterResolver.cs','src/MNBL.ProceduralWorld/World/GeneratedWaterSystem.cs','src/MNBL.ProceduralWorld/World/Map2Policies.cs','src/MNBL.ProceduralWorld/World/GeneratedClimateSystem.cs',
    'src/MNBL.ProceduralWorld/World/RuntimeNavigationBuilder.cs','src/MNBL.ProceduralWorld/World/NavigationValidator.cs','src/MNBL.ProceduralWorld/World/NavigationFixtures.cs',
    'src/MNBL.ProceduralWorld/World/WorldSurface.cs','src/MNBL.ProceduralWorld/World/WorldLifetime.cs','src/MNBL.ProceduralWorld/World/PeriodicWorldCoordinates.cs',
    'src/MNBL.ProceduralWorld/World/PeriodicDistanceModel.cs','src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs','tests/World/WorldMutationTests.cs')
$lock=Get-Content "$repo/config/runtime-api-lock.json" -Raw|ConvertFrom-Json
$references=@()
foreach($name in @('mscorlib','netstandard','System','System.Core','System.Xml','System.Web.Extensions')){
    $pinned=@($lock.assemblies|Where-Object { $_.scope -eq 'framework' -and $_.name -eq $name })
    if($pinned.Count -ne 1){throw "Missing unique framework pin: $name"}
    $path=Join-Path $lock.frameworkRoot $pinned[0].path
    if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pinned[0].sha256){throw "Framework pin mismatch: $path"}
    $references+="/reference:$path"
}
$pinned=@($lock.assemblies|Where-Object { $_.scope -eq 'game' -and $_.name -eq 'TaleWorlds.Core' })
$core=Join-Path $GameRoot $pinned[0].path
if((Get-FileHash -LiteralPath $core).Hash.ToLowerInvariant() -ne $pinned[0].sha256){throw "Game assembly pin mismatch: $core"}
$references+="/reference:$core"
$exe=Join-Path $out 'WorldMutationTests.exe'
& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /nowarn:649 /warnaserror "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ })
if($LASTEXITCODE){throw 'World mutation harness compilation failed'}
Copy-Item -LiteralPath $core -Destination $out
$bundle=(Resolve-Path -LiteralPath $BundleA).Path
if($Record){
    if(-not $Producer){throw '-Record needs -Producer'}
    # The operations the model emits for a fire cast at Nerdun (city_uid), fixed by the tests' expected document.
    $fixtures=Join-Path $repo 'tests/World/fixtures/mutation'
    New-Item -ItemType Directory -Force $fixtures | Out-Null
    $town=((Get-Content (Join-Path $bundle 'compiled/campaign/campaign.json') -Raw|ConvertFrom-Json).towns|Where-Object { $_.uid -eq 'surface-city-2-13033-human_cold' }).id
    if(-not $town){throw 'World A has no Nerdun'}
    $utf8=New-Object Text.UTF8Encoding($false)
    $ops='{"schema":"fwg.operations","version":1,"operations":[{"kind":"shift leylines","api_version":1,"leyline_edits":[{"school":"fire","new_node":{"id":"mnbl-cast-fire-'+$town+'","intensity":4,"city_uid":"surface-city-2-13033-human_cold"}}]}]}'
    Copy-Item -LiteralPath (Join-Path $bundle 'source/request.json') -Destination (Join-Path $fixtures 'request-a.json')
    [IO.File]::WriteAllText((Join-Path $fixtures 'nerdun-fire.operations.json'),$ops,$utf8)
    & $Producer apply --request (Join-Path $fixtures 'request-a.json') --operations (Join-Path $fixtures 'nerdun-fire.operations.json') --delta (Join-Path $fixtures 'nerdun-fire.delta.json')
    if($LASTEXITCODE){throw "fwg apply exited $LASTEXITCODE"}
}
if(-not $Evidence){$Evidence=Join-Path $out 'world-mutation.json'}
$arguments=@($repo,$bundle,$Evidence)
if($Producer){$arguments+=(Resolve-Path -LiteralPath $Producer).Path}
& $exe @arguments
if($LASTEXITCODE){throw 'World mutation tests failed'}
Write-Output "World mutation evidence: $Evidence"
