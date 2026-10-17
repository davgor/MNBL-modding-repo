param([Parameter(Mandatory=$true)][string]$Bundle, [string]$Evidence, [string]$Log)
# MMR-33: compiles the pure managed SettlementFactory (with the MMR-31/32 factories it builds on) and tests/Campaign/SettlementTests.cs against the pinned framework assemblies
# and runs it on the real bundle's compiler records, the repository policies and the installed catalog the roster policy pins. No game.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$out=Join-Path $repo ('build/research-settlements33/test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/Campaign/CampaignIdentity.cs','src/MNBL.ProceduralWorld/Campaign/FactionFactory.cs','src/MNBL.ProceduralWorld/Campaign/CharacterTemplateResolver.cs','src/MNBL.ProceduralWorld/Campaign/HeroFactory.cs','src/MNBL.ProceduralWorld/Campaign/SettlementFactory.cs','src/MNBL.ProceduralWorld/Campaign/VillageBinding.cs','src/MNBL.ProceduralWorld/Missions/MissionScenes.cs','src/MNBL.ProceduralWorld/Campaign/RegistrationPlan.cs','src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs','tests/Campaign/SettlementTests.cs')
$templates=@(Get-ChildItem -LiteralPath (Join-Path $repo 'worldgen/templates/cultures') -Filter *.json|Sort-Object Name|ForEach-Object { 'worldgen/templates/cultures/'+$_.Name })
$inputs=@(($sources+@('tools/Test-Settlements.ps1','worldgen/policies/factions.json','worldgen/policies/rosters.json','worldgen/policies/economy.json','worldgen/templates/settlements/prototypes.json','worldgen/policies/culture-presentations.json')+$templates)|ForEach-Object { @{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $repo $_)).Hash.ToLowerInvariant()} })
$lock=Get-Content "$repo/config/runtime-api-lock.json" -Raw|ConvertFrom-Json
$references=@()
foreach($name in @('mscorlib','System','System.Core','System.Xml','System.Web.Extensions')){
    $pin=@($lock.assemblies|Where-Object { $_.scope -eq 'framework' -and $_.name -eq $name })
    if($pin.Count -ne 1){throw "Missing unique framework pin: $name"}
    $path=Join-Path $lock.frameworkRoot $pin[0].path
    if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pin[0].sha256){throw "Framework pin mismatch: $path"}
    $references+="/reference:$path"
}
$exe=Join-Path $out 'SettlementTests.exe'
& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ })
if($LASTEXITCODE){throw 'Settlement harness compilation failed'}
if(-not $Evidence){$Evidence=Join-Path $out 'settlements.json'}
# The check log is written as UTF-8 (Tee-Object would write UTF-16 under Windows PowerShell).
$lines=@(& $exe (Resolve-Path -LiteralPath $Bundle).Path $repo $Evidence 2>&1|ForEach-Object { "$_" })
$failed=$LASTEXITCODE
$lines|ForEach-Object { Write-Output $_ }
[IO.File]::WriteAllLines("$out/checks.log",[string[]]$lines,(New-Object Text.UTF8Encoding($false)))
if($Log){Copy-Item "$out/checks.log" $Log}
foreach($inputFile in $inputs){if((Get-FileHash -LiteralPath (Join-Path $repo $inputFile.path)).Hash.ToLowerInvariant() -ne $inputFile.sha256){throw "Input changed during harness: $($inputFile.path)"}}
@{schema='mnbl.settlements-offline.v1';sourceInputs=$inputs;executable=@{path=$exe;sha256=(Get-FileHash $exe).Hash.ToLowerInvariant()};scope='Pure managed settlement intents and XML from the real compiler records, travel graph and catalog; no game or registration.'}|ConvertTo-Json -Depth 5|Set-Content "$out/receipt.json"
if($failed){throw 'Settlement regression failed'}
Write-Output "Settlement harness receipt: $out/receipt.json"
