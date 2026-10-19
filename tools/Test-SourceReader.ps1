# MMR-23: exercises the bounded FWGC++ split reader on real exports from the pinned producer and on the
# contract fixture. Writes evidence/fwg/source-reader.json.
#   tools/Test-SourceReader.ps1 [-Sizes 129,257,513,1025] [-Regenerate]
param([int[]]$Sizes = @(129, 257, 513, 1025), [switch]$Regenerate)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$bridge = Join-Path $repo 'build/worldbridge/Release/worldbridge.exe'
$tests = Join-Path $repo 'build/worldbridge/Release/source_reader_tests.exe'
$producer = Join-Path $repo 'build/producer/apps/Release/fwg.exe'
$contract = Join-Path $repo 'worldgen/contracts/source-compatibility.json'
$lock = Get-Content (Join-Path $repo 'worldgen/producer.lock.json') -Raw | ConvertFrom-Json
$work = Join-Path $repo 'build/research-source23'
New-Item -ItemType Directory -Force $work | Out-Null
foreach ($path in @($bridge, $tests, $producer)) { if (!(Test-Path $path)) { throw "Missing $path; run tools/Build.ps1 and build the pinned producer first." } }
$producerHash = (Get-FileHash $producer).Hash.ToLowerInvariant()
if ($producerHash -ne $lock.producer.sha256) { throw "Producer hash $producerHash differs from the lock" }

# Contract fixture tests.
$testLog = Join-Path $work 'source-contract-tests.log'
& $tests (Join-Path $repo 'src/WorldBridge/tests/fixtures/source_contract') $contract (Join-Path $work 'source-contract') *> $testLog
$testExit = $LASTEXITCODE
$testLines = [string[]](Get-Content $testLog)
$summary = ($testLines | Where-Object { $_ -match '^(PASSED|FAILED) ' } | Select-Object -Last 1)

$runs = foreach ($size in $Sizes) {
    $split = Join-Path $work "split-$size"
    if ($Regenerate -or !(Test-Path (Join-Path $split 'world.json'))) {
        if (Test-Path $split) { Remove-Item -Recurse -Force $split }
        $request = Join-Path $work "req-$size.json"
        [IO.File]::WriteAllText($request, "{`"seed`":42,`"recipe_version`":3,`"overrides`":{`"size`":$size,`"phase`":16}}", [Text.UTF8Encoding]::new($false))
        & $producer generate --request $request --split $split --quiet
        if ($LASTEXITCODE -ne 0) { throw "Producer failed for size $size" }
    }
    $files = Get-ChildItem -LiteralPath $split -Recurse -File
    $measure = @{}
    foreach ($mode in 'baseline', 'materialize') {
        $report = Join-Path $work "read-$size-$mode.json"
        $arguments = @('--read-source', $split, '--contract', $contract, '--report', $report)
        if ($mode -eq 'materialize') { $arguments += @('--layers', 'height,land,biome', '--identity', (Join-Path $work "identity-$size.json")) }
        & $bridge @arguments > $null 2> (Join-Path $work "read-$size-$mode.err")
        if ($LASTEXITCODE -ne 0) { throw "Reader refused size $size ($mode): $(Get-Content (Join-Path $work "read-$size-$mode.err") -Raw)" }
        $measure[$mode] = Get-Content $report -Raw | ConvertFrom-Json
    }
    $m = $measure['materialize']
    $side = [int64]$m.metadata.gridSide
    # Provisional ceiling: 32 MiB for streaming plus one materialized layer (8 * side^2 bytes) held at a time.
    $ceiling = 32MB + 8 * $side * $side
    [ordered]@{
        size = $size
        worldJsonSha256 = (Get-FileHash (Join-Path $split 'world.json')).Hash.ToLowerInvariant()
        splitFiles = $files.Count
        splitBytes = ($files | Measure-Object Length -Sum).Sum
        records = $m.records
        plans = $m.plans
        layersValidated = $m.layers.validated
        samplesValidated = $m.layers.samplesValidated
        materialized = $m.layers.materialized
        blockVersions = $m.blockVersions
        layoutSeed = $m.metadata.layoutSeed
        identity = Get-Content (Join-Path $work "identity-$size.json") -Raw | ConvertFrom-Json
        retainedBytes = $m.bytes.retained
        seconds = [ordered]@{ baseline = $measure['baseline'].seconds; materialize = $m.seconds }
        peakWorkingSetBytes = [ordered]@{ baseline = $measure['baseline'].memory.peakWorkingSetBytes; materializeOneLayerAtATime = $m.memory.peakWorkingSetBytes }
        provisionalCeilingBytes = $ceiling
        withinCeiling = ($m.memory.peakWorkingSetBytes -le $ceiling -and $measure['baseline'].memory.peakWorkingSetBytes -le 32MB)
    }
}

# Crafted inputs inside the byte contract that try to exhaust memory; the reader must refuse them
# with limit_exceeded. They refuse before writing a report, so the peak is sampled from outside.
$attackDir = Join-Path $work 'attacks'
& python (Join-Path $repo 'src/WorldBridge/tests/fixtures/source_contract/make_memory_attacks.py') $attackDir | Out-Null
$attacks = foreach ($case in 'many_empty_ids', 'nested_keysets', 'root_members') {
    $dir = Join-Path $attackDir $case
    $err = Join-Path $work "attack-$case.err"
    $p = Start-Process -FilePath $bridge -ArgumentList @('--read-source', "`"$dir`"", '--contract', "`"$contract`"") -NoNewWindow -PassThru -RedirectStandardError $err -RedirectStandardOutput (Join-Path $work "attack-$case.out")
    $null = $p.Handle  # cache the handle so ExitCode is available after exit
    $peak = 0
    while (-not $p.HasExited) { try { $p.Refresh(); if ($p.PeakWorkingSet64 -gt $peak) { $peak = $p.PeakWorkingSet64 } } catch {} ; Start-Sleep -Milliseconds 20 }
    $p.WaitForExit()
    $refusal = Get-Content $err -Raw | ConvertFrom-Json
    [ordered]@{ case = $case; worldJsonBytes = (Get-Item (Join-Path $dir 'world.json')).Length; exit = $p.ExitCode; code = $refusal.code; message = $refusal.message
                path = $refusal.path; offset = $refusal.offset; sampledPeakWorkingSetBytes = $peak
                refusedWithinBudget = ($p.ExitCode -eq 2 -and $refusal.code -eq 'limit_exceeded' -and $peak -le 64MB) }
}

$evidence = [ordered]@{
    schema = 'mnbl.source-reader-evidence'
    version = 1
    ticket = 'MMR-23'
    utc = [DateTime]::UtcNow.ToString('O')
    producer = [ordered]@{ sha256 = $producerHash; commit = $lock.producer.sourceCommit; request = '{"seed":42,"recipe_version":3,"overrides":{"size":N,"phase":16}}' }
    bridgeSha256 = (Get-FileHash $bridge).Hash.ToLowerInvariant()
    contractSha256 = (Get-FileHash $contract).Hash.ToLowerInvariant()
    sources = [ordered]@{}
    provisionalCeiling = 'Real exports: peak working set <= 32 MiB while streaming (no materialization), and <= 32 MiB + 8*side^2 bytes with one materialized layer held at a time. Measured on this machine; provisional and implementer-recorded.'
    enforcedBound = 'Any input within the contract: the tokenizer caps open-object keys at 16 MiB, tokens at 1 MiB and depth at 64; retained records are capped by max_retained_bytes (16 MiB); a materialized layer costs 8*side^2. Crafted inputs refused at <= 64 MiB sampled peak (see attacks).'
    attacks = @($attacks)
    fixtureTests = [ordered]@{ exit = $testExit; summary = $summary; log = 'build/research-source23/source-contract-tests.log'; lines = $testLines }
    runs = @($runs)
    passed = ($testExit -eq 0 -and @($runs | Where-Object { -not $_.withinCeiling }).Count -eq 0 -and @($attacks | Where-Object { -not $_.refusedWithinBudget }).Count -eq 0)
}
foreach ($file in 'src/WorldBridge/include/worldbridge/json_stream.hpp', 'src/WorldBridge/src/json_stream.cpp', 'src/WorldBridge/include/worldbridge/source_world.hpp',
                  'src/WorldBridge/src/source_reader.cpp', 'src/WorldBridge/tests/source_reader_tests.cpp', 'src/WorldBridge/tests/fixtures/source_contract/make_fixture.py',
                  'src/WorldBridge/tests/fixtures/source_contract/make_memory_attacks.py', 'worldgen/contracts/source-identity.schema.json', 'src/WorldBridge/main.cpp') {
    $evidence.sources[$file] = (Get-FileHash (Join-Path $repo $file)).Hash.ToLowerInvariant()
}
$out = Join-Path $repo 'evidence/fwg/source-reader.json'
$evidence | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 $out
"Source reader evidence: passed=$($evidence.passed) $summary -> $out"
