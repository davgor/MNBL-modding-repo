$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$package = Join-Path $repo "artifacts/fwgc-diagnostic-$stamp"
$module = Join-Path $package 'Modules/MNBL.ProceduralWorld'
$bin = Join-Path $module 'bin/Win64_Shipping_Client'
New-Item $bin -ItemType Directory -Force | Out-Null
Copy-Item "$repo/Modules/MNBL.ProceduralWorld/SubModule.xml" "$module/SubModule.xml"
Copy-Item "$repo/build/module/MNBL.ProceduralWorld.dll" $bin
Copy-Item "$repo/build/producer/apps/Release/fwg.exe" $bin
Copy-Item "$repo/build/worldbridge/Release/worldbridge.exe" $bin
if (Test-Path "$repo/Modules/MNBL.ProceduralWorld/GUI") { Copy-Item "$repo/Modules/MNBL.ProceduralWorld/GUI" "$module/GUI" -Recurse }
if (Test-Path "$repo/Modules/MNBL.ProceduralWorld/ModuleData/Languages") { New-Item "$module/ModuleData" -ItemType Directory -Force | Out-Null; Copy-Item "$repo/Modules/MNBL.ProceduralWorld/ModuleData/Languages" "$module/ModuleData/Languages" -Recurse }   # MMR-286
(Get-FileHash "$bin/fwg.exe").Hash.ToLowerInvariant() | Set-Content "$bin/fwg.sha256"
(Get-FileHash "$bin/worldbridge.exe").Hash.ToLowerInvariant() | Set-Content "$bin/worldbridge.sha256"
Copy-Item "$repo/README.md" "$package/README.md"
Copy-Item "$repo/tools.lock.json" "$package/tools.lock.json"
$files = @(Get-ChildItem $package -File -Recurse | ForEach-Object { [ordered]@{path=$_.FullName.Substring($package.Length+1).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash $_.FullName).Hash.ToLowerInvariant()} })
[ordered]@{schema='mnbl.diagnostic-package';version=1;playableCampaign=$false;runtimeValidated=$false;files=$files} | ConvertTo-Json -Depth 5 | Set-Content "$package/package.json"
Compress-Archive -Path "$package/*" -DestinationPath "$package.zip"
$package | Set-Content "$repo/build/latest-package.txt"
Write-Output "$package.zip"
