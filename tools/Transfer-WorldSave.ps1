param(
    [Parameter(Mandatory=$true)][ValidateSet('export','verify','import')][string]$Action,
    [string[]]$Save, [string]$To, [string]$Name, [string]$From, [string[]]$Rename,
    [string]$Store, [string]$Saves, [string]$Out, [string]$GameVersion='v1.4.8.119303', [string]$NavalVersion)
# MMR-48: moves saves of generated campaigns with their worlds, without the game and without regenerating anything (WorldSaveTransfer).
#   tools/Transfer-WorldSave.ps1 -Action export -Save 'My campaign','saveauto1' -To D:\exports -Name my-campaign
#   tools/Transfer-WorldSave.ps1 -Action verify -From D:\exports\my-campaign
#   tools/Transfer-WorldSave.ps1 -Action import -From D:\exports\my-campaign [-Rename 'My campaign=My campaign (moved)'] [-Store DIR -Saves DIR]
# Defaults: the per-user store and the game's save folder. An import takes the store lock: close the game first. A save name already in the
# save folder is refused (give it another name with -Rename); world content already in the store must be identical and is reused.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
if(-not $Store){$Store=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MNBL.ProceduralWorld'}
if(-not $Saves){$Saves=Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Mount and Blade II Bannerlord\Game Saves'}
$lock=Get-Content "$repo/config/runtime-api-lock.json" -Raw|ConvertFrom-Json
if(-not $NavalVersion){$NavalVersion=$lock.navalVersion}
$work=Join-Path $repo ('build/transfer-world-save/'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/Persistence/WorldSaveTransfer.cs','src/MNBL.ProceduralWorld/Persistence/SaveBindingContract.cs','src/MNBL.ProceduralWorld/Persistence/FolderSaveCatalog.cs',
    'src/MNBL.ProceduralWorld/Persistence/SaveJournal.cs','src/MNBL.ProceduralWorld/Persistence/SaveFolder.cs','src/MNBL.ProceduralWorld/Persistence/SaveFileCheck.cs',
    'src/MNBL.ProceduralWorld/Persistence/WorldReferenceIndex.cs','src/MNBL.ProceduralWorld/Persistence/EarlyWorldBindingStore.cs','src/MNBL.ProceduralWorld/Persistence/WorldBinding.cs',
    'src/MNBL.ProceduralWorld/Persistence/DurableFile.cs','src/MNBL.ProceduralWorld/Persistence/StagingCleanup.cs','src/MNBL.ProceduralWorld/Persistence/StoreLock.cs',
    'src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs','tools/WorldSaveTransfer/Program.cs')
$references=@()
foreach($n in @('mscorlib','System','System.Core','System.Web.Extensions')){
    $pinned=@($lock.assemblies|Where-Object { $_.scope -eq 'framework' -and $_.name -eq $n })
    if($pinned.Count -ne 1){throw "Missing unique framework pin: $n"}
    $references+="/reference:"+(Join-Path $lock.frameworkRoot $pinned[0].path)
}
$exe=Join-Path $work 'WorldSaveTransfer.exe'
$compile=@(& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ }) 2>&1|ForEach-Object { "$_" })
if($LASTEXITCODE){$compile|ForEach-Object { Write-Output $_ }; throw 'Transfer-WorldSave compilation failed'}
$arguments=@($Action,'--game',$GameVersion,'--naval',$NavalVersion,'--capabilities','R01,R02,R03,R04,R05,R06,R07,R08,R09,R10,R11')
switch($Action){
    'export' { if(-not $Save -or -not $To -or -not $Name){throw '-Save, -To and -Name are required'}; $arguments+=@('--store',$Store,'--saves',$Saves,'--to',$To,'--name',$Name); foreach($s in $Save){$arguments+=@('--save',$s)} }
    'verify' { if(-not $From){throw '-From is required'}; $arguments+=@('--from',$From) }
    'import' { if(-not $From){throw '-From is required'}; $arguments+=@('--from',$From,'--store',$Store,'--saves',$Saves); foreach($r in $Rename){$arguments+=@('--rename',$r)} }
}
if($Out){$arguments+=@('--out',$Out)}
& $exe @arguments
$code=$LASTEXITCODE
if($code -eq 3){throw "Transfer-WorldSave refused the $Action"}
if($code){throw "Transfer-WorldSave failed ($code)"}
