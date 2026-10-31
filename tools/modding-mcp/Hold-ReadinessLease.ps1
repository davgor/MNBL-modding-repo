param([Parameter(Mandatory=$true)][string]$EvidenceDirectory,
      [string]$GameRoot='D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord')
$ErrorActionPreference='Stop'
$repo=(Resolve-Path "$PSScriptRoot/../..").Path
$dest=[IO.Path]::GetFullPath($EvidenceDirectory)
if(Test-Path -LiteralPath $dest){throw 'Use a new evidence directory.'}
if(Get-Process Bannerlord,Bannerlord.Native,Watchdog,TaleWorlds.MountAndBlade.Launcher -ErrorAction SilentlyContinue){throw 'Game/editor already running.'}
$lease=[IO.File]::Open((Join-Path $repo 'build/battle-scale/native-test.lock'),'OpenOrCreate','ReadWrite','None')
try {
  New-Item -ItemType Directory -Path $dest | Out-Null
  $configs=Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Mount and Blade II Bannerlord/Configs'
  $paths=@(Get-ChildItem -LiteralPath $configs -File)
  $paths+=@(Get-Item -LiteralPath "$GameRoot/bin/Win64_Shipping_wEditor/imgui.ini","$GameRoot/bin/Win64_Shipping_wEditor/steam_appid.txt" -ErrorAction SilentlyContinue)
  $backup=@();$i=0
  foreach($file in $paths){
    $copy=Join-Path $dest "config-$i.bin"; $i++
    Copy-Item -LiteralPath $file.FullName -Destination $copy
    $backup+=@{path=$file.FullName;backup=$copy;sha256=(Get-FileHash -LiteralPath $file.FullName).Hash}
  }
  $backup | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $dest 'config-before.json')
  $scene=@(Get-ChildItem -LiteralPath "$GameRoot/Modules/NavalDLC/SceneObj/Main_map" -File | ForEach-Object {@{path=$_.FullName;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
  $scene | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $dest 'scene-before.json')
  @{startedUtc=[DateTime]::UtcNow.ToString('O');holderPid=$PID;status='held'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dest 'lease.json')
  Write-Output "Readiness lease held. End through finish.signal after all owned game processes exit."
  $deadline=[DateTime]::UtcNow.AddMinutes(30)
  while(!(Test-Path -LiteralPath (Join-Path $dest 'finish.signal'))){
    if([DateTime]::UtcNow -gt $deadline){throw 'Readiness lease deadline; do not restore while game remains active.'}
    Start-Sleep -Seconds 2
  }
  if(Get-Process Bannerlord,Bannerlord.Native,Watchdog,TaleWorlds.MountAndBlade.Launcher -ErrorAction SilentlyContinue){throw 'Refusing restoration while game/editor remains active.'}
  $changes=@();foreach($entry in $backup){
    $after=if(Test-Path -LiteralPath $entry.path){(Get-FileHash -LiteralPath $entry.path).Hash}else{$null}
    $changes+=@{path=$entry.path;before=$entry.sha256;after=$after}
    Copy-Item -LiteralPath $entry.backup -Destination $entry.path -Force
    if((Get-FileHash -LiteralPath $entry.path).Hash -ne $entry.sha256){throw 'Configuration restoration failed.'}
  }
  $sceneChecks=@($scene | ForEach-Object {@{path=$_.path;before=$_.sha256;after=(Get-FileHash -LiteralPath $_.path).Hash;passed=($_.sha256 -eq (Get-FileHash -LiteralPath $_.path).Hash)}})
  @{finishedUtc=[DateTime]::UtcNow.ToString('O');configsRestored=$true;configChanges=$changes;sceneChecks=$sceneChecks;sceneUnchanged=(@($sceneChecks | Where-Object {!$_.passed}).Count -eq 0)} | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $dest 'restoration.json')
  Write-Output 'Configuration restoration and scene hash checks complete.'
} finally { $lease.Dispose() }
