param([Parameter(Mandatory=$true)][string]$BundleA, [Parameter(Mandatory=$true)][string]$BundleB, [string]$GameRoot='D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord', [string]$Evidence, [string]$Log)
# MMR-37: compiles the pure managed surface model (World/WorldSurface.cs), the lifetime primitives (World/WorldLifetime.cs) and the bundle
# verifier with tests/World/SurfaceContractTests.cs against the pinned framework assemblies, and runs them on two real compiled world bundles:
# the model against each bundle, the pinned surface encoder (build/worldbridge/Release/worldbridge.exe, the deployed module's
# SurfaceTemplate) against the model, the encoded terrain.bin's chunk seams (tools/check_terrain_seams.py), landmarks, model queries, and the
# generation tokens and resource ledger. No game.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$out=Join-Path $repo ('build/research-surface37/test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/World/WorldSurface.cs','src/MNBL.ProceduralWorld/World/WorldLifetime.cs','src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs','tests/World/SurfaceContractTests.cs')
$bridge=Join-Path $repo 'build/worldbridge/Release/worldbridge.exe'
$template=Join-Path $GameRoot 'Modules/MNBL.ProceduralWorld/SurfaceTemplate'
$pin=Join-Path $GameRoot 'Modules/MNBL.ProceduralWorld/bin/Win64_Shipping_Client/worldbridge.sha256'
$bridgeHash=(Get-FileHash -LiteralPath $bridge).Hash.ToLowerInvariant()
if((Test-Path -LiteralPath $pin) -and ((Get-Content -LiteralPath $pin -Raw).Trim() -ne $bridgeHash)){throw "The repository surface encoder differs from the deployed pin: $bridgeHash"}
$inputs=@(($sources+@('tools/Test-WorldSurface.ps1','tools/check_terrain_seams.py','tools/scenebin/terrain_bin.py'))|ForEach-Object { @{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $repo $_)).Hash.ToLowerInvariant()} })
$inputs+=@{path=$bridge;sha256=$bridgeHash}
foreach($f in Get-ChildItem -LiteralPath $template -File){$inputs+=@{path=$f.FullName;sha256=(Get-FileHash -LiteralPath $f.FullName).Hash.ToLowerInvariant()}}
$lock=Get-Content "$repo/config/runtime-api-lock.json" -Raw|ConvertFrom-Json
$references=@()
foreach($name in @('mscorlib','System','System.Core','System.Xml','System.Web.Extensions')){
    $pinned=@($lock.assemblies|Where-Object { $_.scope -eq 'framework' -and $_.name -eq $name })
    if($pinned.Count -ne 1){throw "Missing unique framework pin: $name"}
    $path=Join-Path $lock.frameworkRoot $pinned[0].path
    if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pinned[0].sha256){throw "Framework pin mismatch: $path"}
    $references+="/reference:$path"
}
$exe=Join-Path $out 'SurfaceContractTests.exe'
& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ })
if($LASTEXITCODE){throw 'Surface contract harness compilation failed'}
if(-not $Evidence){$Evidence=Join-Path $out 'surface-contract.json'}
$lines=@(& $exe $repo (Resolve-Path -LiteralPath $BundleA).Path (Resolve-Path -LiteralPath $BundleB).Path $template $bridge (Join-Path $out 'work') $Evidence 2>&1|ForEach-Object { "$_" })
$failed=$LASTEXITCODE
$lines|ForEach-Object { Write-Output $_ }
[IO.File]::WriteAllLines("$out/checks.log",[string[]]$lines,(New-Object Text.UTF8Encoding($false)))
if($Log){Copy-Item "$out/checks.log" $Log}
foreach($inputFile in $inputs){$path=if([IO.Path]::IsPathRooted($inputFile.path)){$inputFile.path}else{Join-Path $repo $inputFile.path}; if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $inputFile.sha256){throw "Input changed during harness: $($inputFile.path)"}}
@{schema='mnbl.surface-offline.v1';sourceInputs=$inputs;executable=@{path=$exe;sha256=(Get-FileHash $exe).Hash.ToLowerInvariant()};scope='Pure managed surface model and lifetime primitives on two compiled bundles, with the pinned surface encoder and the encoded terrain.bin seam check; no game.'}|ConvertTo-Json -Depth 5|Set-Content "$out/receipt.json"
if($failed){throw 'Surface contract regression failed'}
Write-Output "Surface contract harness receipt: $out/receipt.json"
