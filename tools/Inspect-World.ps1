param([string]$Store, [string]$Bundle, [string]$Saves, [string]$Out, [string]$GameVersion, [string]$NavalVersion, [string]$GameRoot='D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord')
# MMR-47: inspects the world store, or one data bundle, without launching the game. For each bundle: valid, missing, incompatible (built for
# other game or War Sails versions, needing other capabilities, another schema) or corrupt (anything else the verifier refuses: changed
# bytes, unsafe paths, links), with its versions and generator, and the saves and activations that reference it. For each save of a
# generated world (read from its header): its campaign instance, world, bundle status, package and whether it can be loaded. Then the
# activations, the unfinished jobs and the staging the startup cleanup would remove. Reads only.
# MMR-50: each save's compatibility decision (supported, rebuildable derived cache, unsupported world: WorldCompatibilityPolicy) against
# the running build known without the game (binding contract, save id version from the ledger, identity schema, game, War Sails, backend
# with this producer lock), with the scene and reports when the module is installed under -GameRoot. The read-only report before a load.
#   tools/Inspect-World.ps1                       # the per-user store and the game's saves
#   tools/Inspect-World.ps1 -Bundle <dir>         # one bundle (any directory name)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
if(-not $Store -and -not $Bundle){$Store=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MNBL.ProceduralWorld'}
if($Store -and -not $Saves){$Saves=Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Mount and Blade II Bannerlord\Game Saves'}
$lock=Get-Content "$repo/config/runtime-api-lock.json" -Raw|ConvertFrom-Json
if(-not $NavalVersion){$NavalVersion=$lock.navalVersion}
if(-not $GameVersion){$GameVersion=$lock.gameVersion}   # MMR-50: the pinned installed version, not a constant
$work=Join-Path $repo ('build/inspect-world/'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/Persistence/WorldCompatibilityPolicy.cs','src/MNBL.ProceduralWorld/Persistence/LoadPolicy.cs','src/MNBL.ProceduralWorld/Persistence/SaveBindingContract.cs',
    'src/MNBL.ProceduralWorld/Persistence/WorldSaveTransfer.cs','src/MNBL.ProceduralWorld/Persistence/FolderSaveCatalog.cs','src/MNBL.ProceduralWorld/Persistence/SaveFileCheck.cs',
    'src/MNBL.ProceduralWorld/Persistence/WorldReferenceIndex.cs','src/MNBL.ProceduralWorld/Persistence/StoreLock.cs','src/MNBL.ProceduralWorld/Persistence/WorldInspector.cs','src/MNBL.ProceduralWorld/Persistence/StagingCleanup.cs','src/MNBL.ProceduralWorld/Persistence/DurableFile.cs',
    'src/MNBL.ProceduralWorld/Persistence/WorldBinding.cs','src/MNBL.ProceduralWorld/Persistence/EarlyWorldBindingStore.cs','src/MNBL.ProceduralWorld/Persistence/SaveJournal.cs','src/MNBL.ProceduralWorld/Persistence/SaveFolder.cs','src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs',
    'src/MNBL.ProceduralWorld/Generation/WorldRecoveryService.cs','src/MNBL.ProceduralWorld/Generation/JobStateMachine.cs','src/MNBL.ProceduralWorld/Generation/ProgressEvents.cs',
    'src/MNBL.ProceduralWorld/Generation/GenerationErrors.cs','src/MNBL.ProceduralWorld/Generation/WorldActivationTransaction.cs','tools/InspectWorld/Program.cs')
$references=@()
foreach($name in @('mscorlib','System','System.Core','System.Web.Extensions')){
    $pinned=@($lock.assemblies|Where-Object { $_.scope -eq 'framework' -and $_.name -eq $name })
    if($pinned.Count -ne 1){throw "Missing unique framework pin: $name"}
    $references+="/reference:"+(Join-Path $lock.frameworkRoot $pinned[0].path)
}
$exe=Join-Path $work 'InspectWorld.exe'
# EarlyWorldBindingStore's header fields are assigned by the game's catalog, not by this tool: CS0649 is not an error here.
$compile=@(& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror /nowarn:0649 "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ }) 2>&1|ForEach-Object { "$_" })
if($LASTEXITCODE){$compile|ForEach-Object { Write-Output $_ }; throw 'Inspect-World compilation failed'}
$producerLock=(Get-FileHash -LiteralPath (Join-Path $repo 'worldgen/producer.lock.json')).Hash.ToLowerInvariant()
$capabilities='R01,R02,R03,R04,R05,R06,R07,R08,R09,R10'
if(-not $Out){$Out=Join-Path $work 'inspection.json'}
$arguments=@('--game',$GameVersion,'--naval',$NavalVersion,'--capabilities',$capabilities,'--lock',$producerLock,'--out',$Out)
if($Store){$arguments+=@('--store',$Store); if(Test-Path -LiteralPath $Saves){$arguments+=@('--saves',$Saves)}}
$ledger=Get-Content "$repo/worldgen/contracts/save-type-ledger.json" -Raw|ConvertFrom-Json
$arguments+=@('--save-ids',("{0}/v{1}" -f $ledger.saveIdBase,@($ledger.entries)[-1].saveIdsVersion))
$scenes=Join-Path $GameRoot 'Modules\MNBL.ProceduralWorld\SceneObj'
if(Test-Path -LiteralPath $scenes){$arguments+=@('--scenes',$scenes)}
if($Bundle){$resolved=Resolve-Path -LiteralPath $Bundle -ErrorAction SilentlyContinue; $arguments+=@('--bundle',$(if($resolved){$resolved.Path}else{$Bundle}))}
& $exe @arguments
if($LASTEXITCODE){throw "Inspect-World failed ($LASTEXITCODE)"}
Write-Output "report: $Out"
