param([string]$Evidence, [string]$Log)
# MMR-44: compiles the generation job's pure parts (Generation/JobStateMachine.cs, ProgressEvents.cs, EngineWorkScheduler.cs,
# GenerationCoordinator.cs, GenerationErrors.cs, UI/WorldSetupModel.cs) with tests/Generation/JobStateMachineTests.cs against the pinned
# framework assemblies and runs them; then checks the built worldbridge's --new-request --seed-source rule and that every job input the
# production stages write (JobInputs.Files) is embedded in the module. No game process.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$out=Join-Path $repo ('build/research-job44/test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/Generation/JobStateMachine.cs','src/MNBL.ProceduralWorld/Generation/ProgressEvents.cs','src/MNBL.ProceduralWorld/Generation/EngineWorkScheduler.cs',
    'src/MNBL.ProceduralWorld/Generation/GenerationCoordinator.cs','src/MNBL.ProceduralWorld/Generation/GenerationErrors.cs','src/MNBL.ProceduralWorld/UI/WorldSetupModel.cs',
    'src/MNBL.ProceduralWorld/World/NavigationCacheForecast.cs','src/MNBL.ProceduralWorld/World/NavigationCacheKey.cs',
    # MMR-46: the setup model names the world preview (and what it is built from).
    'src/MNBL.ProceduralWorld/UI/WorldPreview.cs','src/MNBL.ProceduralWorld/World/WorldSurface.cs','src/MNBL.ProceduralWorld/World/WorldLifetime.cs','src/MNBL.ProceduralWorld/World/PeriodicWorldCoordinates.cs',
    'src/MNBL.ProceduralWorld/World/PeriodicDistanceModel.cs','src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs','src/MNBL.ProceduralWorld/Campaign/CampaignIdentity.cs','src/MNBL.ProceduralWorld/Campaign/FactionFactory.cs','src/MNBL.ProceduralWorld/Campaign/VillageBinding.cs',
    'src/MNBL.ProceduralWorld/Generation/WorldRecoveryService.cs','src/MNBL.ProceduralWorld/Generation/GenerationDiagnostics.cs','src/MNBL.ProceduralWorld/Persistence/StagingCleanup.cs','src/MNBL.ProceduralWorld/Persistence/DurableFile.cs',
    'tests/Generation/JobStateMachineTests.cs')
$data=@('worldgen/contracts/progress-event.schema.json','src/MNBL.ProceduralWorld/Generation/GenerationStages.cs','src/MNBL.ProceduralWorld/MNBL.ProceduralWorld.csproj','tools/Test-GenerationJob.ps1')
$inputs=@(($sources+$data)|ForEach-Object { @{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $repo $_)).Hash.ToLowerInvariant()} })
$lock=Get-Content "$repo/config/runtime-api-lock.json" -Raw|ConvertFrom-Json
$references=@();$referenceEvidence=@()
foreach($name in @('mscorlib','System','System.Core','System.Xml','System.Web.Extensions')){
    $pinned=@($lock.assemblies|Where-Object { $_.scope -eq 'framework' -and $_.name -eq $name })
    if($pinned.Count -ne 1){throw "Missing unique framework pin: $name"}
    $path=Join-Path $lock.frameworkRoot $pinned[0].path
    if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pinned[0].sha256){throw "Framework pin mismatch: $path"}
    $references+="/reference:$path";$referenceEvidence+=@{path=$path;sha256=$pinned[0].sha256}
}
$exe=Join-Path $out 'JobStateMachineTests.exe'
& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ })
if($LASTEXITCODE){throw 'Generation job harness compilation failed'}
if(-not $Evidence){$Evidence=Join-Path $out 'generation-job-tests.json'}
$lines=@(& $exe $repo $Evidence 2>&1|ForEach-Object { "$_" })
$failed=$LASTEXITCODE

# worldbridge --new-request --seed-source (MMR-44): random records a seed the host drew; only with --seed; nothing else accepted.
$bridge=Join-Path $repo 'build/worldbridge/Release/worldbridge.exe'
$policies='product-policy='+(Join-Path $repo 'worldgen/policies/product-policy.json')+',world-quality='+(Join-Path $repo 'worldgen/policies/world-quality.json')+',source-compatibility='+(Join-Path $repo 'worldgen/contracts/source-compatibility.json')
$overrides=Join-Path $out 'overrides.json'; [IO.File]::WriteAllText($overrides,'{"phase":16,"size":129}',(New-Object Text.UTF8Encoding($false)))
function New-Request([string]$Name,[string[]]$SeedArgs){
    $req=Join-Path $out "$Name.json"
    $ErrorActionPreference='Continue'
    & $bridge @(@('--new-request','--out',$req,'--instance','6f1c2d4e-8a9b-4c3d-9e2f-1a2b3c4d5e6f')+$SeedArgs+@('--overrides',$overrides,'--lock',(Join-Path $repo 'worldgen/producer.lock.json'),'--policies',$policies,'--max-attempts','3')) 2>$null | Out-Null
    $code=$LASTEXITCODE
    $source=if(Test-Path $req){(Get-Content $req -Raw|ConvertFrom-Json).seedSource}else{$null}
    @{exit=$code;seedSource=$source}
}
$cli=[ordered]@{ explicitDefault=(New-Request 'a' @('--seed','7')); random=(New-Request 'b' @('--seed','7','--seed-source','random')); explicit=(New-Request 'c' @('--seed','7','--seed-source','explicit'))
    randomWithRandom=(New-Request 'd' @('--random','1','--seed-source','random')); unknown=(New-Request 'e' @('--seed','7','--seed-source','typed')) }
$cliOk=$cli.explicitDefault.exit -eq 0 -and $cli.explicitDefault.seedSource -eq 'explicit' -and $cli.random.exit -eq 0 -and $cli.random.seedSource -eq 'random' -and $cli.explicit.seedSource -eq 'explicit' -and $cli.randomWithRandom.exit -ne 0 -and $null -eq $cli.randomWithRandom.seedSource -and $cli.unknown.exit -ne 0
$lines+=@(($(if($cliOk){'ok    '}else{'FAIL  '}))+"worldbridge --new-request --seed-source: --seed 7 alone is explicit, --seed-source random records random, explicit stays explicit; --seed-source with --random, or any other value, is refused ("+(($cli.GetEnumerator()|ForEach-Object { "$($_.Key) exit $($_.Value.exit) $($_.Value.seedSource)" }) -join '; ')+')')
if(-not $cliOk){$failed=1}

# Every job input (JobInputs.Files) is embedded under MNBL.ProceduralWorld.<name>.
$stagesText=Get-Content (Join-Path $repo 'src/MNBL.ProceduralWorld/Generation/GenerationStages.cs') -Raw
$project=Get-Content (Join-Path $repo 'src/MNBL.ProceduralWorld/MNBL.ProceduralWorld.csproj') -Raw
$layoutBlock=[regex]::Match($stagesText,'Layout = new Dictionary<string, string>\(StringComparer\.Ordinal\) \{([^}]*)\}').Groups[1].Value
$layout=@([regex]::Matches($layoutBlock,'\["([^"]+)"\] = "([^"]+)"')|ForEach-Object { @{name=$_.Groups[1].Value;path=$_.Groups[2].Value} })
$files=@($layout|ForEach-Object { $_.name })
# Each embedded input is the repository file at its layout path (the job writes it there).
$wrongSource=@($layout|Where-Object { $project -notmatch [regex]::Escape('Include="..\..\'+($_.path -replace '/','\')+'" LogicalName="MNBL.ProceduralWorld.'+$_.name+'"') })
$missing=@($files|Where-Object { $project -notmatch [regex]::Escape('LogicalName="MNBL.ProceduralWorld.'+$_+'"') })
$embedOk=$files.Count -eq 16 -and $missing.Count -eq 0 -and $wrongSource.Count -eq 0
$lines+=@(($(if($embedOk){'ok    '}else{'FAIL  '}))+"every job input the production stages write is embedded from the repository file at the path the job writes it to, keeping the layout policies resolve their calibration receipts in ($($files.Count): $($files -join ', '))"+$(if($missing.Count){' missing: '+($missing -join ', ')}else{''})+$(if($wrongSource.Count){' wrong source: '+(($wrongSource|ForEach-Object { $_.name }) -join ', ')}else{''}))
if(-not $embedOk){$failed=1}

$lines|ForEach-Object { Write-Output $_ }
[IO.File]::WriteAllLines("$out/checks.log",[string[]]$lines,(New-Object Text.UTF8Encoding($false)))
if($Log){Copy-Item "$out/checks.log" $Log}
foreach($inputFile in $inputs){if((Get-FileHash -LiteralPath (Join-Path $repo $inputFile.path)).Hash.ToLowerInvariant() -ne $inputFile.sha256){throw "Input changed during harness: $($inputFile.path)"}}
@{schema='mnbl.generation-job-offline.v1';sourceInputs=$inputs;referenceInputs=$referenceEvidence;bridge=@{path=$bridge;sha256=(Get-FileHash $bridge).Hash.ToLowerInvariant()};cli=$cli;executable=@{path=$exe;sha256=(Get-FileHash $exe).Hash.ToLowerInvariant()};scope='The pure job state machine, progress, scheduler and coordinator over scripted stages on a stand-in engine thread; the bridge CLI rule; embedded inputs. No game process (the in-game evidence is the generation_job probe).'}|ConvertTo-Json -Depth 6|Set-Content "$out/receipt.json"
if($failed){throw 'Generation job regression failed'}
Write-Output "Generation job harness receipt: $out/receipt.json"
exit 0
