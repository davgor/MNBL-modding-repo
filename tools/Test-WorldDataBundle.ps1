param([Parameter(Mandatory=$true)][string]$Bundle, [string]$WorldBridge, [string]$ParityOut)
# MMR-29: compiles the pure managed host verifier (CampaignBootstrap/WorldDataBundle.cs) with its tests against the pinned
# framework assemblies and runs it on a real worldbridge bundle. The expected WorldContentId and manifest hash come from
# worldbridge --inspect-bundle, so the C# and C++ implementations must agree. Then tools/test_bundle_parity.py runs one set of
# damaged variants of the small complete bundle bundle_transaction_tests publishes through worldbridge, the C# verifier and
# tools/summarize_bundle.py, which must agree. No game, scene or save is involved.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
if(-not $WorldBridge){$WorldBridge=Join-Path $repo 'build/worldbridge/Release/worldbridge.exe'}
$Bundle=(Resolve-Path -LiteralPath $Bundle).Path
$out=Join-Path $repo ('build/research-bundle29/managed-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$paths=@('src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs','tests/WorldDataBundleTests.cs','tools/Test-WorldDataBundle.ps1','tools/test_bundle_parity.py','tools/summarize_bundle.py')
$inputs=@($paths|ForEach-Object { @{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $repo $_)).Hash.ToLowerInvariant()} })
$lock=Get-Content "$repo/config/runtime-api-lock.json" -Raw|ConvertFrom-Json
$references=@();$referenceEvidence=@()
foreach($name in @('mscorlib','System','System.Core','System.Web.Extensions')){
    $pin=@($lock.assemblies|Where-Object { $_.scope -eq 'framework' -and $_.name -eq $name })
    if($pin.Count -ne 1){throw "Missing unique framework pin: $name"}
    $path=Join-Path $lock.frameworkRoot $pin[0].path
    $hash=(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()
    if($hash -ne $pin[0].sha256){throw "Framework pin mismatch: $path"}
    $references+="/reference:$path";$referenceEvidence+=@{path=$path;sha256=$hash}
}
$inspect=& $WorldBridge --inspect-bundle $Bundle
if($LASTEXITCODE){throw "worldbridge --inspect-bundle refused the bundle: $inspect"}
$inspected=$inspect|ConvertFrom-Json
$exe=Join-Path $out 'WorldDataBundleTests.exe'
& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references "$repo/src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs" "$repo/tests/WorldDataBundleTests.cs"
if($LASTEXITCODE){throw 'World data bundle harness compilation failed'}
$work=Join-Path $repo ('build/wdb-'+[Guid]::NewGuid().ToString('N').Substring(0,8))   # short: a copied bundle's paths must stay below MAX_PATH
& $exe $Bundle $work $inspected.worldContentId $inspected.manifestSha256 2>&1 | Tee-Object "$out/checks.log"
$failed=$LASTEXITCODE
if(Test-Path -LiteralPath $work){Remove-Item -LiteralPath $work -Recurse -Force}
if($failed){throw 'World data bundle managed regression failed'}
if(-not $ParityOut){$ParityOut=Join-Path $out 'parity.json'}
$unit=Join-Path $repo ('build/wdbp-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
& (Join-Path (Split-Path $WorldBridge) 'bundle_transaction_tests.exe') $unit | Out-File "$out/unit.log" -Encoding utf8
if($LASTEXITCODE){throw 'bundle_transaction_tests failed while making the parity baseline'}
$baseline=@(Get-ChildItem -LiteralPath (Join-Path $unit 'root-v') -Directory)
if($baseline.Count -ne 1){throw 'Expected one baseline bundle'}
& python (Join-Path $repo 'tools/test_bundle_parity.py') --worldbridge $WorldBridge --csharp $exe --baseline $baseline[0].FullName --work (Join-Path $unit 'p') --out $ParityOut 2>&1 | Tee-Object "$out/parity.log"
$parityFailed=$LASTEXITCODE
Remove-Item -LiteralPath $unit -Recurse -Force
if($parityFailed){throw 'The validators disagree on a damaged bundle'}
foreach($inputFile in $inputs){if((Get-FileHash -LiteralPath (Join-Path $repo $inputFile.path)).Hash.ToLowerInvariant() -ne $inputFile.sha256){throw "Source changed during harness: $($inputFile.path)"}}
$check=& $WorldBridge --inspect-bundle $Bundle
if($LASTEXITCODE -or $check -ne $inspect){throw 'The real bundle changed during the harness'}
@{schema='mnbl.world-bundle-managed.v1';sourceInputs=$inputs;referenceInputs=$referenceEvidence;bundle=@{worldContentId=$inspected.worldContentId;manifestSha256=$inspected.manifestSha256;files=$inspected.totals.files;bytes=$inspected.totals.bytes};worldbridge=@{sha256=(Get-FileHash -LiteralPath $WorldBridge).Hash.ToLowerInvariant()};compiler=@{path=$csc;sha256=(Get-FileHash $csc).Hash.ToLowerInvariant()};executable=@{path=$exe;sha256=(Get-FileHash $exe).Hash.ToLowerInvariant()};parity=(Resolve-Path -LiteralPath $ParityOut).Path;scope='Pure managed host verification of a worldbridge bundle, and validator parity on damaged bundles; no game, scene construction, activation or save-load claim.'}|ConvertTo-Json -Depth 6|Set-Content "$out/receipt.json"
Write-Output "World data bundle harness receipt: $out/receipt.json"
