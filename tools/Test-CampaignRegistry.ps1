param([Parameter(Mandatory=$true)][string]$Bundle, [string]$Scan, [string]$Log)
# MMR-30: compiles the pure managed registry (Campaign/CampaignIdentity.cs, RegistrationPlan.cs, CampaignObjectRegistry.cs)
# with tests/Campaign/RegistrationTests.cs against the pinned framework assemblies and runs it. WorldDataBundle.cs reads the
# real bundle's compiler records. No game, registry or save is involved.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
if(-not $Scan){$Scan=Join-Path $repo 'evidence/runtime/save-definer-scan.json'}
$out=Join-Path $repo ('build/research-registry30/test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/Campaign/CampaignIdentity.cs','src/MNBL.ProceduralWorld/Campaign/RegistrationPlan.cs','src/MNBL.ProceduralWorld/Campaign/CampaignObjectRegistry.cs',
    'src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs','tests/Campaign/RegistrationTests.cs')
$inputs=@(($sources+@('tools/Test-CampaignRegistry.ps1'))|ForEach-Object { @{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $repo $_)).Hash.ToLowerInvariant()} })
$lock=Get-Content "$repo/config/runtime-api-lock.json" -Raw|ConvertFrom-Json
$references=@()
foreach($name in @('mscorlib','System','System.Core','System.Xml','System.Web.Extensions')){
    $pin=@($lock.assemblies|Where-Object { $_.scope -eq 'framework' -and $_.name -eq $name })
    if($pin.Count -ne 1){throw "Missing unique framework pin: $name"}
    $path=Join-Path $lock.frameworkRoot $pin[0].path
    if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pin[0].sha256){throw "Framework pin mismatch: $path"}
    $references+="/reference:$path"
}
$exe=Join-Path $out 'RegistrationTests.exe'
& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ })
if($LASTEXITCODE){throw 'Registry harness compilation failed'}
& $exe (Resolve-Path -LiteralPath $Bundle).Path (Resolve-Path -LiteralPath $Scan).Path 2>&1 | Tee-Object "$out/checks.log"
$failed=$LASTEXITCODE
if($Log){Copy-Item "$out/checks.log" $Log}
foreach($inputFile in $inputs){if((Get-FileHash -LiteralPath (Join-Path $repo $inputFile.path)).Hash.ToLowerInvariant() -ne $inputFile.sha256){throw "Source changed during harness: $($inputFile.path)"}}
@{schema='mnbl.registry-offline.v1';sourceInputs=$inputs;executable=@{path=$exe;sha256=(Get-FileHash $exe).Hash.ToLowerInvariant()};scope='Pure managed registry regression against a model of MBObjectManager; no game run.'}|ConvertTo-Json -Depth 5|Set-Content "$out/receipt.json"
if($failed){throw 'Registry regression failed'}
Write-Output "Registry harness receipt: $out/receipt.json"
