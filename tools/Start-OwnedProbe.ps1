param([Parameter(Mandatory=$true)][string]$Probe, [string]$ArgsJson='{}', [ValidateRange(60,86400)][int]$ProbeTimeoutSeconds=900,
      [string[]]$ExtraModules=@(), [int]$WatchMinutes=0, [string]$Record="$PSScriptRoot/../build/owned-probe.json")
# The request hashtable is $entry, not $record: PowerShell names are case-insensitive, so $record would be the [string]$Record parameter.
# -ExtraModules: modules loaded after MNBL.ProceduralWorld (MMR-247's synthetic content module). -WatchMinutes: how long the dialog watcher
# runs (default: the probe timeout plus 30 minutes, at least 60). -Record: where the run record goes (Collect-OwnedProbe reads it).
$ErrorActionPreference='Stop'
if (Get-Process Bannerlord,Bannerlord.Native,Watchdog -ErrorAction SilentlyContinue) { throw 'Game already running.' }
$root='D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord'
$store=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MNBL.ProceduralWorld/autorun'
New-Item $store -ItemType Directory -Force | Out-Null
$request=Join-Path $store 'request.json'
if (Test-Path $request) { throw 'Unclaimed request already exists.' }
$entry=@{requestId=[guid]::NewGuid().ToString('N'); probe=$Probe; args=($ArgsJson|ConvertFrom-Json); quit=$true; quitMethod='delayed_exit'; menuDelaySeconds=3; probeTimeoutSeconds=$ProbeTimeoutSeconds; expiresUtc=[DateTime]::UtcNow.AddMinutes(10).ToString('O')}
$saveRoot=Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Mount and Blade II Bannerlord/Game Saves'
$entry['savesBefore']=@(Get-ChildItem -LiteralPath $saveRoot -Filter '*.sav' -File | ForEach-Object { @{path=$_.FullName;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()} })
$entry | ConvertTo-Json -Depth 15 | Set-Content $request -Encoding UTF8
$modules='Bannerlord.Harmony*Native*SandBoxCore*CustomBattle*Sandbox*StoryMode*NavalDLC*MNBL.ProceduralWorld'
foreach ($m in $ExtraModules) { if ($m -notmatch '^[A-Za-z0-9_.]+$') { throw "Invalid module id: $m" }; $modules += "*$m" }
$entry['modules']=$modules
$p=Start-Process -FilePath (Join-Path $root 'bin/Win64_Shipping_Client/Bannerlord.exe') -WorkingDirectory (Join-Path $root 'bin/Win64_Shipping_Client') -ArgumentList "/singleplayer _MODULES_*$modules*_MODULES_" -WindowStyle Hidden -PassThru
$entry['pid']=$p.Id
# Clears Safe Mode / crash dialogs for this probe only; see Watch-ProbeDialogs.ps1.
if ($WatchMinutes -le 0) { $WatchMinutes=[Math]::Max(60, [int][Math]::Ceiling($ProbeTimeoutSeconds/60) + 30) }
$watcher=Join-Path $PSScriptRoot 'Watch-ProbeDialogs.ps1'
Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$watcher`"",'-ProcessId',$p.Id,'-MaxMinutes',$WatchMinutes) -WindowStyle Hidden | Out-Null
$entry['watchMinutes']=$WatchMinutes
$entry['startedUtc']=[DateTime]::UtcNow.ToString('O')
$entry | ConvertTo-Json -Depth 15 | Set-Content $Record
Write-Output "Owned PID $($p.Id), evidence $store/result-$($p.Id).json"
