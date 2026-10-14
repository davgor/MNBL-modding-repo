param([Parameter(Mandatory=$true)][string]$Probe, [string]$ArgsJson='{}', [ValidateRange(60,1800)][int]$ProbeTimeoutSeconds=900)
$ErrorActionPreference='Stop'
if (Get-Process Bannerlord,Watchdog -ErrorAction SilentlyContinue) { throw 'Game already running.' }
$root='D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord'
$store=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MNBL.ProceduralWorld/autorun'
New-Item $store -ItemType Directory -Force | Out-Null
$request=Join-Path $store 'request.json'
if (Test-Path $request) { throw 'Unclaimed request already exists.' }
$record=@{requestId=[guid]::NewGuid().ToString('N'); probe=$Probe; args=($ArgsJson|ConvertFrom-Json); quit=$true; quitMethod='delayed_exit'; menuDelaySeconds=3; probeTimeoutSeconds=$ProbeTimeoutSeconds; expiresUtc=[DateTime]::UtcNow.AddMinutes(10).ToString('O')}
$saveRoot=Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Mount and Blade II Bannerlord/Game Saves'
$record['savesBefore']=@(Get-ChildItem -LiteralPath $saveRoot -Filter '*.sav' -File | ForEach-Object { @{path=$_.FullName;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()} })
$record | ConvertTo-Json -Depth 15 | Set-Content $request -Encoding UTF8
$p=Start-Process -FilePath (Join-Path $root 'bin/Win64_Shipping_Client/Bannerlord.exe') -WorkingDirectory (Join-Path $root 'bin/Win64_Shipping_Client') -ArgumentList '/singleplayer _MODULES_*Bannerlord.Harmony*Native*SandBoxCore*CustomBattle*Sandbox*StoryMode*NavalDLC*MNBL.ProceduralWorld*_MODULES_' -WindowStyle Hidden -PassThru
$record['pid']=$p.Id
# Clears Safe Mode / crash dialogs for this probe only; see Watch-ProbeDialogs.ps1.
$watcher=Join-Path $PSScriptRoot 'Watch-ProbeDialogs.ps1'
Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$watcher`"",'-ProcessId',$p.Id) -WindowStyle Hidden | Out-Null
$record['startedUtc']=[DateTime]::UtcNow.ToString('O')
$record | ConvertTo-Json -Depth 15 | Set-Content "$PSScriptRoot/../build/owned-probe.json"
Write-Output "Owned PID $($p.Id), evidence $store/result-$($p.Id).json"
