# MMR-261: the pinned producer's accepted verification (worldgen/producer.lock.json acceptedEvidence.verification). Offline; no game.
# For seeds 42 and 43 (size 129, phase 16): the world worldbridge --produce publishes, its world.json SHA-256 (rootSha256; seed 42's is
# the root Test-ProducerProcess accepts) and the same world from the producer's own CLI (replay); then fwg apply's two fixed points on
# seed 42 (MMR-250): no edit lists no cell, and a fire key point of intensity 4 at Nerdun lists cells within its reach. Writes
# evidence/fwg/producer-verification.json; re-pin the lock's verification hash after (tests/check_producer_lock.py checks it).
param([string]$Out)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$bridge = Join-Path $repo 'build/worldbridge/Release/worldbridge.exe'
$producer = Join-Path $repo 'build/producer/apps/Release/fwg.exe'
$lock = Join-Path $repo 'worldgen/producer.lock.json'
foreach ($path in @($bridge, $producer)) { if (!(Test-Path $path)) { throw "Missing $path; run tools/Build.ps1 and tools/Build-Fwg.ps1 first." } }
if (-not $Out) { $Out = Join-Path $repo 'evidence/fwg/producer-verification.json' }
$work = Join-Path $repo ('build/producer-verify/run-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path).Hash.ToLowerInvariant() }
$producerHash = Sha $producer
if ($producerHash -ne (Get-Content $lock -Raw | ConvertFrom-Json).producer.sha256) { throw 'The built producer is not the pinned one.' }
$roots = [ordered]@{}; $replay = @(); $runs = @()
foreach ($seed in @(42, 43)) {
    $request = Join-Path $work "request-$seed.json"
    [IO.File]::WriteAllText($request, "{`"seed`":$seed,`"recipe_version`":3,`"overrides`":{`"size`":129,`"phase`":16}}", $utf8)
    $job = Join-Path $work "job-$seed"; $world = Join-Path $work "world-$seed"
    & $bridge --produce --lock $lock --producer $producer --request $request --job $job --publish $world --timeout-ms 300000 2>(Join-Path $work "produce-$seed.err") | Out-Null
    $exit = $LASTEXITCODE
    $outcome = Get-Content "$job/outcome.json" -Raw | ConvertFrom-Json
    $record = Get-Content "$job/run-record.json" -Raw | ConvertFrom-Json
    if ($outcome.outcome -ne 'succeeded') { throw "seed ${seed}: $($outcome.outcome) $($outcome.message)" }
    $roots["$seed"] = Sha "$world/world.json"
    $runs += [ordered]@{ seed = "$seed"; status = $outcome.outcome; elapsedMilliseconds = $record.elapsedMilliseconds; peakWorkingSetBytes = $record.peakJobMemoryBytes; exitCode = $exit
                         requestHash = (Sha $request); producerHash = $producerHash }
    $split = Join-Path $work "cli-$seed"
    & $producer generate --request $request --split $split --quiet 2>(Join-Path $work "cli-$seed.err") | Out-Null
    $replay += [ordered]@{ seed = "$seed"; matchesCli = ($LASTEXITCODE -eq 0 -and (Sha "$split/world.json") -eq $roots["$seed"]) }
}
# fwg apply on seed 42: the generated world is its fixed point, and a key point changes only ground within its reach (MMR-250).
$request = Join-Path $work 'request-42.json'
$apply = [ordered]@{}
foreach ($case in @(
    @{ name = 'zero'; ops = '{"schema":"fwg.operations","version":1,"operations":[{"kind":"shift leylines","api_version":1,"leyline_edits":[]}]}' },
    @{ name = 'nerdun'; ops = '{"schema":"fwg.operations","version":1,"operations":[{"kind":"shift leylines","api_version":1,"leyline_edits":[{"school":"fire","new_node":{"id":"cast-nerdun-1","intensity":4,"city_uid":"surface-city-2-13033-human_cold"}}]}]}' })) {
    $ops = Join-Path $work "ops-$($case.name).json"; $delta = Join-Path $work "delta-$($case.name).json"
    [IO.File]::WriteAllText($ops, $case.ops, $utf8)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & $producer apply --request $request --operations $ops --delta $delta 2>(Join-Path $work "apply-$($case.name).err") | Out-Null
    $exit = $LASTEXITCODE; $watch.Stop()
    if ($exit -ne 0) { throw "fwg apply $($case.name) exited $exit" }
    $d = Get-Content $delta -Raw | ConvertFrom-Json
    $changed = @($d.cells | Where-Object { $_.before.biome_variant -ne $_.after.biome_variant }).Count
    $apply[$case.name] = [ordered]@{ exitCode = $exit; cells = @($d.cells).Count; variantChanged = $changed; deltaSha256 = (Sha $delta); elapsedMilliseconds = $watch.ElapsedMilliseconds }
}
if ($apply.zero.cells -ne 0) { throw 'fwg apply with no edit lists cells: the generated world is not its fixed point.' }
$doc = [ordered]@{ rootSha256 = $roots; replay = $replay; runs = $runs; apply = $apply; producer = [ordered]@{ sha256 = $producerHash; sourceCommit = (Get-Content $lock -Raw | ConvertFrom-Json).producer.sourceCommit }
                   note = 'Written by tools/Verify-Producer.ps1 (MMR-261): seeds 42 and 43 at size 129, phase 16, through worldbridge --produce and the producer CLI; fwg apply on seed 42 with no edit and with a fire key point at Nerdun.' }
[IO.File]::WriteAllText($Out, ($doc | ConvertTo-Json -Depth 6), $utf8)
Write-Output "producer verification: $Out ($(Sha $Out))"
