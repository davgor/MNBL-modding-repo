param([string]$GameRoot='D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord', [switch]$Interactive, [switch]$Crowd, [ValidateRange(1,8192)][int]$CrowdCount=4096)
$ErrorActionPreference='Stop'
if (Get-Process Bannerlord,Watchdog -ErrorAction SilentlyContinue) { throw 'Close the running game before this isolated test.' }
$repo=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot))
$source=Join-Path $repo 'build/battle-scale/module/MNBL.BattleScale.dll'
if (!(Test-Path -LiteralPath $source)) { throw 'Run Build-BattleScale.ps1 first.' }
$build=Get-Content -LiteralPath "$repo/build/battle-scale/build-receipt.json" -Raw | ConvertFrom-Json
if ($build.status -ne 'built' -or $build.moduleSha256 -ne (Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant()) { throw 'Missing or stale build receipt; rebuild Battle Scale.' }
foreach($sourceInput in $build.sources) { if ((Get-FileHash -LiteralPath $sourceInput.path).Hash.ToLowerInvariant() -ne $sourceInput.sha256) { throw "Source changed after build: $($sourceInput.path)" } }
$destination=Join-Path $GameRoot 'Modules/MNBL.BattleScale'
New-Item -ItemType Directory -Path "$destination/bin/Win64_Shipping_Client" -Force | Out-Null
Copy-Item -LiteralPath $source -Destination "$destination/bin/Win64_Shipping_Client/MNBL.BattleScale.dll"
Copy-Item -LiteralPath "$repo/Modules/MNBL.BattleScale/SubModule.xml" -Destination "$destination/SubModule.xml"
$store=Join-Path $env:LOCALAPPDATA 'MNBL.BattleScale'
New-Item -ItemType Directory -Path $store -Force | Out-Null
if (Test-Path -LiteralPath "$store/probe.once") { throw 'Unclaimed Battle Scale probe exists.' }
if (!$Interactive) {
    $request=if ($Crowd) { "crowd:$CrowdCount" } else { [guid]::NewGuid().ToString() }
    [IO.File]::WriteAllText("$store/probe.once", $request)
}
$bin=Join-Path $GameRoot 'bin/Win64_Shipping_Client'
$process=Start-Process -FilePath "$bin/Bannerlord.exe" -WorkingDirectory $bin -ArgumentList '/singleplayer _MODULES_*Native*SandBoxCore*CustomBattle*MNBL.BattleScale*_MODULES_' -WindowStyle Hidden -PassThru
Write-Output "Battle Scale process $($process.Id); result $store/result-$($process.Id).json"
if ($Interactive) { return }
$receipt=[ordered]@{pid=$process.Id;moduleSha256=(Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant();sources=$build.sources;startedUtc=[DateTime]::UtcNow.ToString('O');resultPath="$store/result-$($process.Id).json"}
$receipt | ConvertTo-Json | Set-Content -LiteralPath "$repo/build/battle-scale/probe.json" -Encoding utf8
$deadline=[DateTime]::UtcNow.AddMinutes(4)
while (!$process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
    Start-Sleep -Seconds 2
    $process.Refresh()
}
if (!$process.HasExited) {
    $process.CloseMainWindow() | Out-Null
    if (!$process.WaitForExit(10000)) { Stop-Process -Id $process.Id }
    throw 'Owned Battle Scale test timed out; inspect its module.log and crash report.'
}
$resultPath=$receipt.resultPath
if (!(Test-Path -LiteralPath $resultPath)) { throw 'Game exited without a Battle Scale result.' }
$result=Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
$receipt['result']=$result
$receipt['endedUtc']=[DateTime]::UtcNow.ToString('O')
$evidence=Join-Path $repo 'evidence/engine-extension'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$receiptName=if ($Crowd) { 'visual-crowd-runtime.json' } else { 'battle-scale-runtime.json' }
$receipt | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath "$evidence/$receiptName" -Encoding utf8
if ($result.status -ne 'passed') { throw "Battle Scale in-game test failed: $($result.status) $($result.error)" }
if ($Crowd) { Write-Output "PASS: visual crowd probe; receipt $evidence/$receiptName" }
else { Write-Output "PASS: $($result.spawned) cumulative spawns, $($result.returned) returns, peak $($result.peakEntities) native entities; $($result.logicalTroops) logical troops." }
