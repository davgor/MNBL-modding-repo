$ErrorActionPreference = 'Stop'
$taskRoot = 'C:\Users\davgo\Documents\GitHub\MNBL-modding-repo'
$taskBin = 'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord\bin\Win64_Shipping_Client'
$outDir = Join-Path $taskRoot 'evidence\engine-extension\native-exit-trace-control'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$nativeHash = (Get-FileHash -LiteralPath (Join-Path $taskBin 'TaleWorlds.Native.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
if ($nativeHash -ne '2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a') { throw 'Native DLL is not original' }
$existing = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like '*Bannerlord*' })
if ($existing.Count -gt 0) { throw 'An existing Bannerlord session is active; wait for it to exit before trying again.' }
$checkedUtc = [DateTime]::UtcNow.ToString('o')
$argsText = '/singleplayer _MODULES_*Native*SandBoxCore*CustomBattle*_MODULES_ /maxThreadCount 16'
$ownedGame = Start-Process -FilePath (Join-Path $taskBin 'Bannerlord.exe') -WorkingDirectory $taskBin -ArgumentList $argsText -PassThru
$control = [ordered]@{ scope = 'Original native normal-menu exit with observational REA Frida tracing'; pid = $ownedGame.Id; prelaunchNoGameCheckedUtc = $checkedUtc; nativeSha256 = $nativeHash; arguments = $argsText; installedFilesChanged = $false; status = 'running' }
$control | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outDir 'control.json') -Encoding utf8
$ownedGame.WaitForExit()
$ownedGame.Refresh()
$control.status = 'exited'
$control.exitCode = $ownedGame.ExitCode
$control.endedUtc = [DateTime]::UtcNow.ToString('o')
$control | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outDir 'control.json') -Encoding utf8
$control | ConvertTo-Json -Depth 4
