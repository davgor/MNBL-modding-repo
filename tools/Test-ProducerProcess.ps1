# Builds worldbridge in an isolated directory, runs its contract tests, then drives the real pinned
# producer through `worldbridge --preflight/--produce`. Writes evidence/fwg/producer-process-contract.json.
# Never touches the game, the deployed module, or build/worldbridge.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$cmakeBin = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin'
$build = Join-Path $repo 'build/worldbridge-contract'
$work = Join-Path $repo ('build/research-producer22/run-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$sourceFiles = @(Get-ChildItem -LiteralPath "$repo/src/WorldBridge" -Recurse -File | ForEach-Object { $_.FullName.Substring($repo.Length + 1).Replace('\','/') }) +
    @('worldgen/producer.lock.json','worldgen/contracts/producer-lock.schema.json','tests/check_producer_lock.py','tools/Test-ProducerProcess.ps1')
$sources = @($sourceFiles | Sort-Object | ForEach-Object { [ordered]@{ path=$_; sha256=(Get-FileHash -LiteralPath (Join-Path $repo $_)).Hash.ToLowerInvariant() } })

& "$cmakeBin/cmake.exe" -S "$repo/src/WorldBridge" -B $build -G 'Visual Studio 17 2022' -A x64 *> "$work/configure.log"
if ($LASTEXITCODE) { throw 'Configure failed' }
& "$cmakeBin/cmake.exe" --build $build --config Release --clean-first *> "$work/build.log"
if ($LASTEXITCODE) { throw 'Build failed; see build.log' }
& "$cmakeBin/ctest.exe" --test-dir $build -C Release --no-tests=error --output-on-failure *> "$work/ctest.log"
$ctestExit = $LASTEXITCODE
& python "$repo/tests/check_producer_lock.py" *> "$work/lock-check.log"
$lockExit = $LASTEXITCODE

$bridge = Join-Path $build 'Release/worldbridge.exe'
$producer = Join-Path $repo 'build/producer/apps/Release/fwg.exe'
$lock = Join-Path $repo 'worldgen/producer.lock.json'
$results = [ordered]@{}
function Read-Outcome([string]$job) { if (Test-Path "$job/outcome.json") { Get-Content "$job/outcome.json" -Raw | ConvertFrom-Json } else { $null } }
function Invoke-Bridge([string]$name, [string[]]$arguments) {
    $events = & $bridge @arguments 2>"$work/$name.stderr.log"
    $exit = $LASTEXITCODE
    $events | Set-Content -LiteralPath "$work/$name.events.log" -Encoding utf8
    return $exit
}
function New-Request([string]$name, [int]$size, [string]$overrides = '') {
    $path = Join-Path $work "$name-request.json"
    $body = if ($overrides) { $overrides } else { "`"size`":$size,`"phase`":16" }
    [IO.File]::WriteAllText($path, "{`"seed`":42,`"recipe_version`":3,`"overrides`":{$body}}", [Text.UTF8Encoding]::new($false))
    return $path
}

$exit = Invoke-Bridge 'preflight' @('--preflight','--lock',$lock,'--producer',$producer,'--job',"$work/preflight")
$preflight = Get-Content "$work/preflight/preflight.json" -Raw | ConvertFrom-Json
$results.preflight = [ordered]@{ exit=$exit; passed=$preflight.passed; failedChecks=@($preflight.checks | Where-Object { -not $_.passed } | ForEach-Object name) }

$exit = Invoke-Bridge 'success' @('--produce','--lock',$lock,'--producer',$producer,'--request',(New-Request 'success' 129),'--job',"$work/success-job",'--publish',"$work/success-world",'--timeout-ms','300000')
$o = Read-Outcome "$work/success-job"
$worldHash = if (Test-Path "$work/success-world/world.json") { (Get-FileHash "$work/success-world/world.json").Hash.ToLowerInvariant() } else { $null }
$results.success = [ordered]@{ exit=$exit; outcome=$o.outcome; published=[bool](Test-Path "$work/success-world/world.json"); stagingRemaining=[bool](Test-Path "$work/success-job/staging/split");
    worldSha256=$worldHash; matchesAcceptedSeed42Root=($worldHash -eq 'e9264b8d1e0ad5b45af40dca3dcdd54128c22c856a7c11d7acdef3043e5f5c69'); stray=$o.strayProcessesTerminated }

$exit = Invoke-Bridge 'refusal' @('--produce','--lock',$lock,'--producer',$producer,'--request',(New-Request 'refusal' 2),'--job',"$work/refusal-job",'--publish',"$work/refusal-world")
$o = Read-Outcome "$work/refusal-job"
$results.refusal = [ordered]@{ exit=$exit; outcome=$o.outcome; field=$o.failure.field; code=$o.failure.code; published=[bool](Test-Path "$work/refusal-world") }

# FWGC++ emits "field": null for cross-field refusals; that is still a refusal.
$exit = Invoke-Bridge 'crossfield' @('--produce','--lock',$lock,'--producer',$producer,'--request',(New-Request 'crossfield' 0 '"relief_m":1000,"globe_radius":6000000'),'--job',"$work/crossfield-job",'--publish',"$work/crossfield-world")
$o = Read-Outcome "$work/crossfield-job"
$results.crossFieldRefusal = [ordered]@{ exit=$exit; outcome=$o.outcome; code=$o.failure.code; fieldIsNull=($null -eq $o.failure.field -and $null -ne $o.failure); published=[bool](Test-Path "$work/crossfield-world") }

$braceDir = Join-Path $work 'with{brace}'
New-Item -ItemType Directory -Force -Path $braceDir | Out-Null
$braceRequest = Join-Path $braceDir 'request.json'
Copy-Item (New-Request 'brace' 2) $braceRequest
$exit = Invoke-Bridge 'brace' @('--produce','--lock',$lock,'--producer',$producer,'--request',$braceRequest,'--job',"$braceDir/job",'--publish',"$braceDir/world")
$o = Read-Outcome "$braceDir/job"
$results.bracePath = [ordered]@{ exit=$exit; outcome=$o.outcome; field=$o.failure.field }

$early = Join-Path $work 'early-cancel'
Set-Content -LiteralPath $early -Value 'cancel'
$exit = Invoke-Bridge 'earlycancel' @('--produce','--lock',$lock,'--producer',$producer,'--request',(New-Request 'earlycancel' 129),'--job',"$work/earlycancel-job",'--publish',"$work/earlycancel-world",'--cancel-file',$early)
$o = Read-Outcome "$work/earlycancel-job"
$results.cancelBeforeLaunch = [ordered]@{ exit=$exit; outcome=$o.outcome; launched=$o.launched }

$cancelFile = Join-Path $work 'cancel-signal'
$watch = [Diagnostics.Stopwatch]::StartNew()
$job = Start-Job -ScriptBlock { param($b,$a) & $b @a | Out-Null; $LASTEXITCODE } -ArgumentList $bridge, @('--produce','--lock',$lock,'--producer',$producer,'--request',(New-Request 'cancel' 129),'--job',"$work/cancel-job",'--publish',"$work/cancel-world",'--cancel-file',$cancelFile)
Start-Sleep -Milliseconds 1500
Set-Content -LiteralPath $cancelFile -Value 'cancel'
$cancelExit = [int](Receive-Job -Job $job -Wait -AutoRemoveJob)
$watch.Stop()
$o = Read-Outcome "$work/cancel-job"
$record = Get-Content "$work/cancel-job/run-record.json" -Raw | ConvertFrom-Json
$results.cancellation = [ordered]@{ exit=$cancelExit; outcome=$o.outcome; producerPid=$record.pid; producerStillRunning=[bool](Get-Process -Id $record.pid -ErrorAction SilentlyContinue);
    published=[bool](Test-Path "$work/cancel-world"); secondsUntilBridgeReturned=[math]::Round($watch.Elapsed.TotalSeconds,2) }

$exit = Invoke-Bridge 'timeout' @('--produce','--lock',$lock,'--producer',$producer,'--request',(New-Request 'timeout' 129),'--job',"$work/timeout-job",'--publish',"$work/timeout-world",'--timeout-ms','400')
$o = Read-Outcome "$work/timeout-job"
$record = Get-Content "$work/timeout-job/run-record.json" -Raw | ConvertFrom-Json
$results.timeout = [ordered]@{ exit=$exit; outcome=$o.outcome; producerStillRunning=[bool](Get-Process -Id $record.pid -ErrorAction SilentlyContinue); published=[bool](Test-Path "$work/timeout-world") }

$exit = Invoke-Bridge 'mismatch' @('--produce','--lock',$lock,'--producer',$bridge,'--request',(New-Request 'mismatch' 129),'--job',"$work/mismatch-job",'--publish',"$work/mismatch-world")
$o = Read-Outcome "$work/mismatch-job"
$results.pinMismatch = [ordered]@{ exit=$exit; outcome=$o.outcome; launched=$o.launched }

$exit = Invoke-Bridge 'missing' @('--produce','--lock',$lock,'--producer',(Join-Path $work 'absent/fwg.exe'),'--request',(New-Request 'missing' 129),'--job',"$work/missing-job",'--publish',"$work/missing-world")
$o = Read-Outcome "$work/missing-job"
$results.missingExecutable = [ordered]@{ exit=$exit; outcome=$o.outcome; launched=$o.launched }

# Reproducible build: a second clean configure/build of the same sources must give identical bytes.
$repro = Join-Path $repo 'build/worldbridge-contract-repro'
if (Test-Path $repro) { Remove-Item -Recurse -Force $repro }
& "$cmakeBin/cmake.exe" -S "$repo/src/WorldBridge" -B $repro -G 'Visual Studio 17 2022' -A x64 *> "$work/repro-configure.log"
& "$cmakeBin/cmake.exe" --build $repro --config Release --target worldbridge *> "$work/repro-build.log"
$reproHash = (Get-FileHash (Join-Path $repro 'Release/worldbridge.exe')).Hash.ToLowerInvariant()
$results.reproducibleBridge = [ordered]@{ first=(Get-FileHash $bridge).Hash.ToLowerInvariant(); second=$reproHash; identical=((Get-FileHash $bridge).Hash.ToLowerInvariant() -eq $reproHash) }

$pass = ($ctestExit -eq 0) -and ($lockExit -eq 0) -and $results.preflight.passed -and
    $results.success.outcome -eq 'succeeded' -and $results.success.matchesAcceptedSeed42Root -and -not $results.success.stagingRemaining -and
    $results.refusal.outcome -eq 'refused' -and $results.refusal.field -eq 'size' -and -not $results.refusal.published -and
    $results.cancellation.outcome -eq 'cancelled' -and -not $results.cancellation.producerStillRunning -and -not $results.cancellation.published -and
    $results.timeout.outcome -eq 'timeout' -and -not $results.timeout.producerStillRunning -and -not $results.timeout.published -and
    $results.pinMismatch.outcome -eq 'pin_mismatch' -and -not $results.pinMismatch.launched -and
    $results.missingExecutable.outcome -eq 'missing_executable' -and
    $results.crossFieldRefusal.outcome -eq 'refused' -and $results.crossFieldRefusal.fieldIsNull -and -not $results.crossFieldRefusal.published -and
    $results.bracePath.outcome -eq 'refused' -and $results.bracePath.field -eq 'size' -and
    $results.cancelBeforeLaunch.outcome -eq 'cancelled' -and -not $results.cancelBeforeLaunch.launched -and
    $results.reproducibleBridge.identical
$receipt = [ordered]@{
    schema='mnbl.producer-process-contract'; version=1; utc=[DateTime]::UtcNow.ToString('O'); passed=$pass; workDirectory=$work
    ctest=[ordered]@{ exit=$ctestExit; log=(Get-Content "$work/ctest.log" | Select-String 'tests passed|Passed|Failed' | ForEach-Object { $_.Line.Trim() }) }
    # [string] drops Get-Content's provider properties, which ConvertTo-Json would otherwise recurse into.
    lockCheck=[ordered]@{ exit=$lockExit; summary=[string](Get-Content "$work/lock-check.log" | Select-Object -Last 1) }
    bridge=[ordered]@{ path=$bridge; sha256=(Get-FileHash $bridge).Hash.ToLowerInvariant() }
    producer=[ordered]@{ path=$producer; sha256=(Get-FileHash $producer).Hash.ToLowerInvariant() }
    results=$results; sources=$sources
    scope='Bridge-side protocol and supervision only. The C# host still launches the producer directly (GenerationProbe); switching it to --produce is host integration work.'
}
$receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$repo/evidence/fwg/producer-process-contract.json" -Encoding utf8
$receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$work/receipt.json" -Encoding utf8
if (-not $pass) { throw "Producer process contract failed; see $work/receipt.json" }
Write-Output "PASS: producer process contract; evidence/fwg/producer-process-contract.json"
exit 0
