param([string]$GameRoot='D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot))
$out=Join-Path $repo 'build/battle-scale'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$inputFiles=@(Get-ChildItem -LiteralPath "$repo/src/MNBL.BattleScale" -File -Recurse | Where-Object { $_.Extension -in '.cs','.csproj' -and $_.FullName -notmatch '[\\/]obj[\\/]' }) + @(Get-Item -LiteralPath "$repo/Modules/MNBL.BattleScale/SubModule.xml")
$inputHashes=@($inputFiles | ForEach-Object { [ordered]@{path=$_.FullName;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()} })
[ordered]@{status='building';utc=[DateTime]::UtcNow.ToString('O')} | ConvertTo-Json | Set-Content -LiteralPath "$out/build-receipt.json" -Encoding utf8
$msbuild='C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
$compiler='C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
$core=@(Get-ChildItem -LiteralPath "$repo/src/MNBL.BattleScale/Core" -Filter '*.cs' | ForEach-Object FullName)
& $compiler /nologo /langversion:7.3 /warnaserror+ /optimize+ /target:exe "/out:$out/CoreTests.exe" $core "$repo/tests/BattleScale/CoreTests.cs"
if ($LASTEXITCODE) { throw 'Battle Scale core test compilation failed.' }
& "$out/CoreTests.exe"
if ($LASTEXITCODE) { throw 'Battle Scale core tests failed.' }
& $msbuild "$repo/src/MNBL.BattleScale/MNBL.BattleScale.csproj" /t:Rebuild /p:Configuration=Release "/p:GameRoot=$GameRoot" /nologo /v:minimal
if ($LASTEXITCODE) { throw 'Battle Scale module build failed.' }
foreach($sourceInput in $inputHashes) { if ((Get-FileHash -LiteralPath $sourceInput.path).Hash.ToLowerInvariant() -ne $sourceInput.sha256) { throw "Source changed during build: $($sourceInput.path)" } }
[ordered]@{status='built';utc=[DateTime]::UtcNow.ToString('O');sources=$inputHashes;moduleSha256=(Get-FileHash -LiteralPath "$out/module/MNBL.BattleScale.dll").Hash.ToLowerInvariant()} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$out/build-receipt.json" -Encoding utf8
Write-Output "Built and core-tested: $out/module/MNBL.BattleScale.dll"
