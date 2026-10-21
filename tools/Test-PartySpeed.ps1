param([Parameter(Mandatory=$true)][string]$Bundle, [string]$GameRoot='D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord', [string]$Log)
# MMR-254: compiles the generated world's party speed model (Campaign/GeneratedPartySpeedModel.cs) with its pure terms and road cells
# (World/RoadSpeed.cs) and the strict roads policy reader (World/Map2Policies.cs), with tests/Campaign/PartySpeedModelTests.cs against the
# pinned framework and game assemblies, and runs it: the chain Road -> Naval -> Default built as CampaignGameStarter.AddModel builds it, the
# policy's terms, and a compiled bundle's road cells (cells.bin v2). No game process.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$out=Join-Path $repo ('build/research-speed254/test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/World/Map2Policies.cs','src/MNBL.ProceduralWorld/World/RoadSpeed.cs','src/MNBL.ProceduralWorld/Campaign/GeneratedPartySpeedModel.cs','tests/Campaign/PartySpeedModelTests.cs')
$lock=Get-Content "$repo/config/runtime-api-lock.json" -Raw|ConvertFrom-Json
$references=@()
foreach($name in @('mscorlib','netstandard','System','System.Core','System.Web.Extensions')){
    $pinned=@($lock.assemblies|Where-Object { $_.scope -eq 'framework' -and $_.name -eq $name })
    if($pinned.Count -ne 1){throw "Missing unique framework pin: $name"}
    $path=Join-Path $lock.frameworkRoot $pinned[0].path
    if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pinned[0].sha256){throw "Framework pin mismatch: $path"}
    $references+="/reference:$path"
}
foreach($name in @('TaleWorlds.Core','TaleWorlds.CampaignSystem','TaleWorlds.Library','TaleWorlds.Localization','TaleWorlds.ObjectSystem','TaleWorlds.SaveSystem')){
    $pinned=@($lock.assemblies|Where-Object { $_.scope -eq 'game' -and $_.name -eq $name })
    if($pinned.Count -ne 1){throw "Missing unique game pin: $name"}
    $path=Join-Path $GameRoot $pinned[0].path
    if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pinned[0].sha256){throw "Game assembly pin mismatch: $path"}
    $references+="/reference:$path"
}
$exe=Join-Path $out 'PartySpeedModelTests.exe'
& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ })
if($LASTEXITCODE){throw 'Party speed harness compilation failed'}
$lines=@(& $exe $repo $GameRoot (Resolve-Path -LiteralPath $Bundle).Path 2>&1|ForEach-Object { "$_" })
$failed=$LASTEXITCODE
$lines|ForEach-Object { Write-Output $_ }
[IO.File]::WriteAllLines("$out/checks.log",[string[]]$lines,(New-Object Text.UTF8Encoding($false)))
if($Log){Copy-Item "$out/checks.log" $Log}
if($failed){throw 'Party speed tests failed'}
