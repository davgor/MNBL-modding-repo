param([Parameter(Mandatory=$true)][string]$BundleA, [Parameter(Mandatory=$true)][string]$BundleB, [string]$GameRoot='D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord',
      [string]$HarmonyDir='D:\SteamLibrary\steamapps\workshop\content\261550\2859188632\bin\Win64_Shipping_Client', [string]$Evidence, [string]$Log)
# MMR-287: compiles the incident transpiler (Campaign/IncidentCulturePatch.cs) against the pinned TaleWorlds assemblies and Bannerlord.Harmony,
# with the factories and the startup initializer, and tests/Campaign/ContentParityTests.cs, and runs it: the four incident conditions located
# in the installed TaleWorlds.CampaignSystem by IL content and rewritten (an unresolved one only leaves its incident off), the culture
# comparison, the backstory-safe wanderer pool, and the wanderer and hideout censuses on worlds A and B. No game process.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$out=Join-Path $repo ('build/content-parity/test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/Campaign/IncidentCulturePatch.cs','src/MNBL.ProceduralWorld/Campaign/CampaignIdentity.cs','src/MNBL.ProceduralWorld/Campaign/FactionFactory.cs',
    'src/MNBL.ProceduralWorld/Campaign/CharacterTemplateResolver.cs','src/MNBL.ProceduralWorld/Campaign/HeroFactory.cs','src/MNBL.ProceduralWorld/Campaign/SettlementFactory.cs',
    'src/MNBL.ProceduralWorld/Campaign/VillageBinding.cs','src/MNBL.ProceduralWorld/Missions/MissionScenes.cs','src/MNBL.ProceduralWorld/Campaign/RegistrationPlan.cs',
    'src/MNBL.ProceduralWorld/Campaign/GeneratedSpawnModel.cs','src/MNBL.ProceduralWorld/Campaign/GeneratedCampaignInitializer.cs','src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs',
    'tests/Campaign/ContentParityTests.cs')
$templates=@(Get-ChildItem -LiteralPath (Join-Path $repo 'worldgen/templates/cultures') -Filter *.json|Sort-Object Name|ForEach-Object { 'worldgen/templates/cultures/'+$_.Name })
$data=@('tools/Test-ContentParity.ps1','worldgen/policies/factions.json','worldgen/policies/rosters.json','worldgen/policies/population.json','worldgen/policies/economy.json','worldgen/policies/startup.json',
    'worldgen/templates/settlements/prototypes.json','worldgen/policies/culture-presentations.json','evidence/map-epic-2/wanderer-backstories.json')+$templates
$inputs=@(($sources+$data)|ForEach-Object { @{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $repo $_)).Hash.ToLowerInvariant()} })
$lock=Get-Content "$repo/config/runtime-api-lock.json" -Raw|ConvertFrom-Json
$references=@();$referenceEvidence=@()
foreach($name in @('mscorlib','netstandard','System','System.Core','System.Xml','System.Web.Extensions')){
    $pin=@($lock.assemblies|Where-Object { $_.scope -eq 'framework' -and $_.name -eq $name })
    if($pin.Count -ne 1){throw "Missing unique framework pin: $name"}
    $path=Join-Path $lock.frameworkRoot $pin[0].path
    if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pin[0].sha256){throw "Framework pin mismatch: $path"}
    $references+="/reference:$path";$referenceEvidence+=@{path=$path;sha256=$pin[0].sha256}
}
# The incident targets are the game's own (pinned); they load from the game at run time, Harmony from its Workshop module.
foreach($name in @('TaleWorlds.CampaignSystem','TaleWorlds.Core','TaleWorlds.ObjectSystem','TaleWorlds.Localization','TaleWorlds.Library')){
    $pin=@($lock.assemblies|Where-Object { $_.scope -eq 'game' -and $_.name -eq $name })
    if($pin.Count -ne 1){throw "Missing unique game pin: $name"}
    $path=Join-Path $GameRoot $pin[0].path
    if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pin[0].sha256){throw "Game assembly pin mismatch: $path"}
    $references+="/reference:$path";$referenceEvidence+=@{path=$path;sha256=$pin[0].sha256}
}
$harmony=Join-Path $HarmonyDir '0Harmony.dll'
if(-not (Test-Path -LiteralPath $harmony)){throw "Bannerlord.Harmony is not installed: $harmony"}
$references+="/reference:$harmony";$referenceEvidence+=@{path=$harmony;sha256=(Get-FileHash -LiteralPath $harmony).Hash.ToLowerInvariant()}
$exe=Join-Path $out 'ContentParityTests.exe'
$compile=@(& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ }) 2>&1|ForEach-Object { "$_" })
if($LASTEXITCODE){$compile|ForEach-Object { Write-Output $_ }; throw 'Content parity harness compilation failed'}
if(-not $Evidence){$Evidence=Join-Path $out 'content-parity.json'}
$lines=@(& $exe $repo (Resolve-Path -LiteralPath $BundleA).Path (Resolve-Path -LiteralPath $BundleB).Path $GameRoot $HarmonyDir $Evidence 2>&1|ForEach-Object { "$_" })
$failed=$LASTEXITCODE
$lines|ForEach-Object { Write-Output $_ }
[IO.File]::WriteAllLines("$out/checks.log",[string[]]$lines,(New-Object Text.UTF8Encoding($false)))
if($Log){Copy-Item "$out/checks.log" $Log}
foreach($inputFile in $inputs){if((Get-FileHash -LiteralPath (Join-Path $repo $inputFile.path)).Hash.ToLowerInvariant() -ne $inputFile.sha256){throw "Input changed during harness: $($inputFile.path)"}}
@{schema='mnbl.content-parity-offline.v1';sourceInputs=$inputs;referenceInputs=$referenceEvidence;bundles=@((Resolve-Path -LiteralPath $BundleA).Path,(Resolve-Path -LiteralPath $BundleB).Path);executable=@{path=$exe;sha256=(Get-FileHash $exe).Hash.ToLowerInvariant()};scope='The incident transpiler against the installed TaleWorlds.CampaignSystem, the wanderer pool and the wanderer and hideout censuses on worlds A and B. No game.'}|ConvertTo-Json -Depth 6|Set-Content "$out/receipt.json"
if($failed){throw 'Content parity tests failed'}
Write-Output "Content parity harness receipt: $out/receipt.json"
exit 0
