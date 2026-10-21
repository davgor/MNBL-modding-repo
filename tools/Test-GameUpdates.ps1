param([string]$GameRoot='D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord',
      [string]$HarmonyDir='D:\SteamLibrary\steamapps\workshop\content\261550\2859188632\bin\Win64_Shipping_Client', [string]$Evidence, [string]$Log)
# MMR-284: compiles the supported-build rule (CampaignBootstrap/WorldDataBundle.cs SupportedBuilds), the load policy (Persistence/LoadPolicy.cs),
# the setup errors (Generation/GenerationErrors.cs) and the patch signature table (Campaign/PatchSignatures.cs) with
# tests/Campaign/GameUpdateTests.cs against the pinned framework assemblies, and runs it: save bindings of an older build at or above the
# minimum and of a build newer than any known at release load, one below the minimum is refused with its message; the setup host likewise;
# every patch target resolves by signature in the installed game (read by reflection from GameRoot, never run), and a changed signature
# refuses its patch by name; the wanderer introduction's IL anchor is read with Bannerlord.Harmony (never patched); no exact-build literal
# gates the module's code. No game process.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$out=Join-Path $repo ('build/game-updates/test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs','src/MNBL.ProceduralWorld/Persistence/LoadPolicy.cs','src/MNBL.ProceduralWorld/Generation/GenerationErrors.cs',
    'src/MNBL.ProceduralWorld/Campaign/PatchSignatures.cs','tests/Campaign/GameUpdateTests.cs')
$inputs=@(($sources+@('tools/Test-GameUpdates.ps1'))|ForEach-Object { @{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $repo $_)).Hash.ToLowerInvariant()} })
$lock=Get-Content "$repo/config/runtime-api-lock.json" -Raw|ConvertFrom-Json
$references=@();$referenceEvidence=@()
foreach($name in @('mscorlib','System','System.Core','System.Web.Extensions')){
    $pinned=@($lock.assemblies|Where-Object { $_.scope -eq 'framework' -and $_.name -eq $name })
    if($pinned.Count -ne 1){throw "Missing unique framework pin: $name"}
    $path=Join-Path $lock.frameworkRoot $pinned[0].path
    if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pinned[0].sha256){throw "Framework pin mismatch: $path"}
    $references+="/reference:$path";$referenceEvidence+=@{path=$path;sha256=$pinned[0].sha256}
}
$harmony=Join-Path $HarmonyDir '0Harmony.dll'
if(-not (Test-Path -LiteralPath $harmony)){throw "Bannerlord.Harmony is not installed: $harmony"}
$references+="/reference:$harmony";$referenceEvidence+=@{path=$harmony;sha256=(Get-FileHash -LiteralPath $harmony).Hash.ToLowerInvariant()}
$exe=Join-Path $out 'GameUpdateTests.exe'
$compile=@(& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ }) 2>&1|ForEach-Object { "$_" })
if($LASTEXITCODE){$compile|ForEach-Object { Write-Output $_ }; throw 'Game update harness compilation failed'}
if(-not $Evidence){$Evidence=Join-Path $out 'game-update-tests.json'}
$lines=@(& $exe $repo $GameRoot $HarmonyDir $Evidence 2>&1|ForEach-Object { "$_" })
$failed=$LASTEXITCODE
$lines|ForEach-Object { Write-Output $_ }
[IO.File]::WriteAllLines("$out/checks.log",[string[]]$lines,(New-Object Text.UTF8Encoding($false)))
if($Log){Copy-Item "$out/checks.log" $Log}
foreach($inputFile in $inputs){if((Get-FileHash -LiteralPath (Join-Path $repo $inputFile.path)).Hash.ToLowerInvariant() -ne $inputFile.sha256){throw "Input changed during harness: $($inputFile.path)"}}
@{schema='mnbl.game-updates-offline.v1';sourceInputs=$inputs;referenceInputs=$referenceEvidence;gameRoot=$GameRoot;executable=@{path=$exe;sha256=(Get-FileHash $exe).Hash.ToLowerInvariant()};scope='The supported-build rule (minimum, no maximum), save bindings, the setup host, the patch signature table against the installed assemblies, and the absence of exact-build gates. No game process.'}|ConvertTo-Json -Depth 6|Set-Content "$out/receipt.json"
if($failed){throw 'Game update tests failed'}
Write-Output "Game update harness receipt: $out/receipt.json"
exit 0
