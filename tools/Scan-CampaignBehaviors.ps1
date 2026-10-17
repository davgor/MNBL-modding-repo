param([string]$GameRoot='D:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord', [string]$Out)
# MMR-34: static audit of the installed campaign behaviours and game models (tools/BehaviorScan/BehaviorScan.cs). It writes the installed
# world object ids from the hash-checked campaign catalog, compiles the scanner against the pinned framework assemblies, and scans the
# assemblies of the modules the probes load (CampaignSystem, SandBox, StoryMode, NavalDLC, and the Core and MountAndBlade models). Output: evidence/fwg/behavior-scan.json with
# the assembly hashes. Nothing in the game is executed.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
if(-not $Out){$Out=Join-Path $repo 'evidence/fwg/behavior-scan.json'}
$work=Join-Path $repo ('build/research-behaviors34/scan-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$receipt=Get-Content (Join-Path $repo 'evidence/runtime/in-game/campaign_catalog-10212.json') -Raw|ConvertFrom-Json
$ids=[ordered]@{}
foreach($pair in @(@('Settlements','settlement'),@('Heroes','hero'),@('Factions','clan'),@('Kingdoms','kingdom'),@('SPCultures','culture'))){
    $doc=@($receipt.outputs.documents|Where-Object { $_.id -eq $pair[0] })[0]
    if((Get-FileHash -LiteralPath $doc.path).Hash.ToLowerInvariant() -ne $doc.sha256){throw "catalog $($pair[0]) differs from its receipt"}
    [xml]$x=Get-Content -LiteralPath $doc.path -Raw
    $ids[$pair[1]]=[string[]]@($x.DocumentElement.ChildNodes|Where-Object { $_.NodeType -eq 'Element' -and $_.GetAttribute('id') }|ForEach-Object { $_.GetAttribute('id') }|Sort-Object -Unique)
}
$idsPath=Join-Path $work 'ids.json'
[IO.File]::WriteAllText($idsPath,($ids|ConvertTo-Json -Depth 3 -Compress),(New-Object Text.UTF8Encoding($false)))
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$lock=Get-Content "$repo/config/runtime-api-lock.json" -Raw|ConvertFrom-Json
$references=@()
foreach($name in @('mscorlib','System','System.Core','System.Web.Extensions')){
    $pin=@($lock.assemblies|Where-Object { $_.scope -eq 'framework' -and $_.name -eq $name })
    if($pin.Count -ne 1){throw "Missing unique framework pin: $name"}
    $path=Join-Path $lock.frameworkRoot $pin[0].path
    if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pin[0].sha256){throw "Framework pin mismatch: $path"}
    $references+="/reference:$path"
}
$exe=Join-Path $work 'BehaviorScan.exe'
& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references (Join-Path $repo 'tools/BehaviorScan/BehaviorScan.cs')
if($LASTEXITCODE){throw 'BehaviorScan compilation failed'}
# The campaign assemblies, plus TaleWorlds.Core and TaleWorlds.MountAndBlade, whose game models a campaign also registers.
$assemblies=@('bin/Win64_Shipping_Client/TaleWorlds.CampaignSystem.dll','Modules/SandBox/bin/Win64_Shipping_Client/SandBox.dll','Modules/StoryMode/bin/Win64_Shipping_Client/StoryMode.dll','Modules/NavalDLC/bin/Win64_Shipping_Client/NavalDLC.dll',
    'bin/Win64_Shipping_Client/TaleWorlds.Core.dll','bin/Win64_Shipping_Client/TaleWorlds.MountAndBlade.dll')|ForEach-Object { Join-Path $GameRoot $_ }
$raw=Join-Path $work 'scan.json'
& $exe $GameRoot $idsPath $raw @assemblies
if($LASTEXITCODE){throw 'BehaviorScan failed'}
$scan=Get-Content $raw -Raw|ConvertFrom-Json
$result=[ordered]@{schema='mnbl.evidence.behavior-scan';version=1;ticket='MMR-34';
    assemblies=@($assemblies|ForEach-Object { [ordered]@{path=$_.Replace('\','/');sha256=(Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant()} });
    catalogReceipt='evidence/runtime/in-game/campaign_catalog-10212.json';scanner=[ordered]@{source='tools/BehaviorScan/BehaviorScan.cs';sha256=(Get-FileHash (Join-Path $repo 'tools/BehaviorScan/BehaviorScan.cs')).Hash.ToLowerInvariant()};
    engineDefaults=$scan.engineDefaults;types=$scan.types}
[IO.File]::WriteAllText($Out,($result|ConvertTo-Json -Depth 8),(New-Object Text.UTF8Encoding($false)))
Write-Output "Behavior scan: $Out ($(@($scan.types).Count) types)"
