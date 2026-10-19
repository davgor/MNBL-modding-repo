# MMR-24: reproducible, distinct and resumable generation on the pinned producer.
# Probes the producer's seed width and records worldgen/contracts/producer-seed-width.json (the driver
# refuses to generate without it), then drives worldbridge --generate end to end.
# Writes evidence/fwg/generation-identity.json.
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$bridge = Join-Path $repo 'build/worldbridge/Release/worldbridge.exe'
$tests = Join-Path $repo 'build/worldbridge/Release/identity_retry_tests.exe'
$producer = Join-Path $repo 'build/producer/apps/Release/fwg.exe'
$lock = Join-Path $repo 'worldgen/producer.lock.json'
$contract = Join-Path $repo 'worldgen/contracts/source-compatibility.json'
$quality = Join-Path $repo 'worldgen/policies/world-quality.json'
$product = Join-Path $repo 'worldgen/policies/product-policy.json'
$seedWidth = Join-Path $repo 'worldgen/contracts/producer-seed-width.json'
$work = Join-Path $repo ('build/research-identity24/run-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
foreach ($path in @($bridge, $tests, $producer)) { if (!(Test-Path $path)) { throw "Missing $path; run tools/Build.ps1 first." } }
$utf8 = [Text.UTF8Encoding]::new($false)  # no BOM: every reader is strict JSON
function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path).Hash.ToLowerInvariant() }
function Write-Json([string]$Path, $Value) { [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 10), $utf8) }

function Invoke-Bridge([string[]]$Arguments, [string]$Name) {
    $ErrorActionPreference = 'Continue'  # refusals on stderr are data here, not script errors
    $out = & $bridge @Arguments 2> (Join-Path $work "$Name.err")
    $code = $LASTEXITCODE
    [IO.File]::WriteAllText((Join-Path $work "$Name.out"), [string]($out -join "`n"), $utf8)
    # Plain [string]s: Get-Content adds ETS properties that make ConvertTo-Json recurse without bound.
    return @{ exit = $code; stdout = [string]($out -join "`n"); stderr = [string](Get-Content (Join-Path $work "$Name.err") -Raw) }
}
function Policies([string]$QualityPolicy = $quality, [string]$Contract = $contract) { "product-policy=$product,world-quality=$QualityPolicy,source-compatibility=$Contract" }
function New-Request([string]$Name, [string]$Instance, [string]$Seed, [string]$Overrides, [string]$PolicyList = (Policies), [int]$Attempts = 3) {
    $o = Join-Path $work "$Name-overrides.json"
    [IO.File]::WriteAllText($o, $Overrides, $utf8)
    $req = Join-Path $work "$Name-request.json"
    $seedArgs = if ($Seed -eq 'random') { @('--random', '1') } else { @('--seed', $Seed) }
    $r = Invoke-Bridge (@('--new-request', '--out', $req, '--instance', $Instance) + $seedArgs + @('--overrides', $o, '--lock', $lock, '--policies', $PolicyList, '--max-attempts', "$Attempts")) "$Name-new"
    if ($r.exit -ne 0) { throw "new-request $Name failed: $($r.stderr)" }
    return $req
}
# RequestId recomputed independently of the bridge: SHA-256('mnbl.request-id/1 mnbl.canonical-json/1\n' + canonical request bytes).
function Request-Id([string]$Path) {
    [byte[]]$bytes = [Text.Encoding]::UTF8.GetBytes("mnbl.request-id/1 mnbl.canonical-json/1`n") + [IO.File]::ReadAllBytes($Path)
    (([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)) | ForEach-Object { $_.ToString('x2') }) -join ''
}
function Generate-Args([string]$Request, [string]$State, [string]$WorkDir, [string]$PolicyList, [string]$Lock, [string]$SeedWidthFile, [string[]]$Extra) {
    @('--generate', '--request', $Request, '--state', $State, '--work', $WorkDir, '--lock', $Lock, '--producer', $producer, '--seed-width', $SeedWidthFile, '--policies', $PolicyList) + $Extra
}
function Chosen-World([string]$WorkDir, $State) {
    if ($null -eq $State -or $null -eq $State.chosen) { return $null }
    $i = [int]$State.chosen.attempt
    Join-Path $WorkDir ($State.requestId.Substring(0, 16) + "/attempt-$i-run-$($State.attempts[$i].runs)/world")
}
function Read-State([string]$State) { if (Test-Path -LiteralPath $State) { Get-Content -LiteralPath $State -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null } }
function Invoke-Generate([string]$Name, [string]$Request, [string]$PolicyList = (Policies), [string[]]$Extra = @(), [string]$Lock = $lock, [string]$StateName = $Name,
                         [string]$Root = $work, [string]$WorkName = "$StateName-work", [string]$SeedWidthFile = $seedWidth) {
    $state = Join-Path $Root "$StateName-state.json"
    $workDir = Join-Path $Root $WorkName
    $r = Invoke-Bridge (Generate-Args $Request $state $workDir $PolicyList $Lock $SeedWidthFile $Extra) "$Name-generate"
    $r.state = Read-State $state
    $r.stateSha256 = if (Test-Path -LiteralPath $state) { Sha $state } else { $null }
    $r.world = Chosen-World $workDir $r.state
    $r.worldJsonSha256 = if ($r.world -and (Test-Path -LiteralPath (Join-Path $r.world 'world.json'))) { Sha (Join-Path $r.world 'world.json') } else { $null }
    return $r
}
function Start-Generate([string]$Name, [string]$Request, [string]$PolicyList, [string[]]$Extra = @()) {
    $p = Start-Process -FilePath $bridge -ArgumentList (Generate-Args $Request (Join-Path $work "$Name-state.json") (Join-Path $work "$Name-work") $PolicyList $lock $seedWidth $Extra) `
        -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $work "$Name-bg.out") -RedirectStandardError (Join-Path $work "$Name-bg.err")
    $null = $p.Handle  # keeps the exit code readable after exit
    return $p
}
function Wait-ForPath([string]$Pattern, [int]$Seconds = 120) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while (!(Test-Path $Pattern) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 50 }
    return (Test-Path $Pattern)
}
function Thread-Env([string]$WorkDir) {
    $record = Get-ChildItem $WorkDir -Recurse -Filter 'run-record.json' | Select-Object -First 1
    if ($record) { [string]((Get-Content $record.FullName -Raw | ConvertFrom-Json).environment | Where-Object { $_ -like 'FWG_THREADS=*' }) } else { $null }
}
function Reasons($Attempt) { return ,@($Attempt.reasons | ForEach-Object { [ordered]@{ code = [string]$_.code; message = [string]$_.message } }) }

$results = [ordered]@{}
$producerSha = Sha $producer

# Unit tests.
& $tests (Join-Path $work 'unit') *> (Join-Path $work 'unit.log')
$results.unitTests = [ordered]@{ exit = $LASTEXITCODE; summary = [string](Get-Content (Join-Path $work 'unit.log') | Where-Object { $_ -match '^(PASSED|FAILED) ' } | Select-Object -Last 1) }

# Seed width, probed against the pinned producer binary and recorded for the driver.
$probe = foreach ($seed in @('4294967296', '4294967295')) {
    $req = Join-Path $work "width-$seed.json"
    [IO.File]::WriteAllText($req, "{`"seed`":$seed,`"recipe_version`":3,`"overrides`":{`"size`":3,`"phase`":1}}", $utf8)
    $null = Invoke-Bridge @('--produce', '--lock', $lock, '--producer', $producer, '--request', $req, '--job', (Join-Path $work "width-$seed-job"), '--publish', (Join-Path $work "width-$seed-world")) "width-$seed"
    $o = Get-Content (Join-Path $work "width-$seed-job/outcome.json") -Raw | ConvertFrom-Json
    [ordered]@{ seed = $seed; outcome = [string]$o.outcome; failureCode = if ($o.failure) { [string]$o.failure.code } else { $null }; failureField = if ($o.failure) { [string]$o.failure.field } else { $null } }
}
$widthVerified = ($probe[0].outcome -eq 'refused' -and $probe[0].failureField -eq 'seed' -and $probe[1].outcome -eq 'succeeded')
$record = [ordered]@{ schema = 'mnbl.producer-seed-width'; version = 1; producerSha256 = $producerSha; maxSeed = 4294967295; verified = $widthVerified; probes = @($probe)
                      note = 'Written by tools/Test-GenerationIdentity.ps1: the pinned producer refuses seed 4294967296 (field seed) and accepts 4294967295. worldbridge --generate refuses (seed_width_unverified) unless producerSha256 equals the locked producer and maxSeed equals the request contract.' }
Write-Json $seedWidth $record
$results.seedWidth = [ordered]@{ record = 'worldgen/contracts/producer-seed-width.json'; recordSha256 = (Sha $seedWidth); probes = @($probe); maxSeed = 4294967295; verified = $widthVerified }

# c1: the same request in separate runs and thread counts 1, 0 (all hardware threads) and 4, at sizes 33 and 129.
$c1 = [ordered]@{}
foreach ($size in 33, 129) {
    $req = New-Request "a$size" '3f2b8c1e-4d5a-4b6c-8d7e-9f0a1b2c3d4e' '42' "{`"size`":$size,`"phase`":16}"
    $runs = foreach ($t in 1, 0, 4) {
        $g = Invoke-Generate "a$size-threads$t" $req (Policies) @('--threads', "$t")
        [ordered]@{ threads = $t; exit = $g.exit; status = [string]$g.state.status; stateSha256 = $g.stateSha256; chosen = $g.state.chosen; worldJsonSha256 = $g.worldJsonSha256
                    environment = Thread-Env (Join-Path $work "a$size-threads$t-work") }
    }
    $states = @($runs | ForEach-Object { $_.stateSha256 } | Select-Object -Unique).Count
    $worlds = @($runs | ForEach-Object { $_.worldJsonSha256 } | Select-Object -Unique).Count
    $c1["size$size"] = [ordered]@{ request = $req; requestId = (Request-Id $req); runs = @($runs); identicalAttemptState = ($states -eq 1); identicalWorldJson = ($worlds -eq 1)
                                    passed = ($states -eq 1 -and $worlds -eq 1 -and @($runs | Where-Object { $_.status -ne 'accepted' -or $_.exit -ne 0 }).Count -eq 0 -and
                                              ($runs | ForEach-Object { $_.environment }) -join ',' -eq 'FWG_THREADS=1,FWG_THREADS=0,FWG_THREADS=4') }
}
$a33 = Read-State (Join-Path $work 'a33-threads1-state.json')
$c1.requestIdIndependent = ($a33.requestId -eq $c1.size33.requestId)
$c1.passed = ($c1.size33.passed -and $c1.size129.passed -and $c1.requestIdIndependent)
$results.c1 = $c1

# c3: same-seed replay under another campaign instance; Random World draws a new seed; requests sharing one work
# directory get their own worlds; a deleted state never adopts the work it left behind.
$reqA = $c1.size33.request
$reqB = New-Request 'b' '7a6b5c4d-3e2f-4a1b-9c8d-7e6f5a4b3c2d' '42' '{"size":33,"phase":16}'
$gB = Invoke-Generate 'b' $reqB
$reqR = New-Request 'random' '1c2d3e4f-5a6b-4c7d-8e9f-0a1b2c3d4e5f' 'random' '{"size":33,"phase":16}'
$gR = Invoke-Generate 'random' $reqR
$req43 = New-Request 's43' '5e4d3c2b-1a09-4f8e-9d7c-6b5a49382716' '43' '{"size":33,"phase":16}'
$shared42 = Invoke-Generate 'shared42' $reqB (Policies) @() $lock 'shared42' $work 'shared-work'
$shared43 = Invoke-Generate 'shared43' $req43 (Policies) @() $lock 'shared43' $work 'shared-work'
Remove-Item (Join-Path $work 'shared42-state.json')
$collision = Invoke-Generate 'shared42-again' $reqB (Policies) @() $lock 'shared42' $work 'shared-work'
$afterCollision = Invoke-Generate 'shared42-rerun' $reqB (Policies) @() $lock 'shared42' $work 'shared-work'
$results.c3 = [ordered]@{
    replay = [ordered]@{ status = [string]$gB.state.status; requestId = $gB.state.requestId; chosen = $gB.state.chosen }
    original = [ordered]@{ requestId = $a33.requestId; chosen = $a33.chosen }
    random = [ordered]@{ status = [string]$gR.state.status; masterSeed = $gR.state.masterSeed; chosen = $gR.state.chosen; seedSource = [string](Get-Content $reqR -Raw | ConvertFrom-Json).seedSource }
    sharedWorkDir = [ordered]@{
        seed42 = [ordered]@{ exit = $shared42.exit; world = $shared42.world; worldContentId = $shared42.state.chosen.worldContentId; worldJsonSha256 = $shared42.worldJsonSha256 }
        seed43 = [ordered]@{ exit = $shared43.exit; world = $shared43.world; worldContentId = $shared43.state.chosen.worldContentId; worldJsonSha256 = $shared43.worldJsonSha256 }
        deletedStateRerun = [ordered]@{ exit = $collision.exit; stderr = $collision.stderr; stateAfter = [ordered]@{ status = [string]$collision.state.status; attempt0 = $collision.state.attempts[0].state; runs = $collision.state.attempts[0].runs } }
        nextInvocation = [ordered]@{ exit = $afterCollision.exit; status = [string]$afterCollision.state.status; runs = $afterCollision.state.attempts[0].runs; history = @($afterCollision.state.attempts[0].history); world = $afterCollision.world
                                     worldContentId = $afterCollision.state.chosen.worldContentId }
    }
}
$results.c3.passed = ($gB.state.status -eq 'accepted' -and $gB.state.chosen.sourceInputId -eq $a33.chosen.sourceInputId -and $gB.state.chosen.worldContentId -eq $a33.chosen.worldContentId -and
                      $gB.state.requestId -ne $a33.requestId -and $gR.state.status -eq 'accepted' -and $gR.state.masterSeed -ne 42 -and $gR.state.chosen.sourceInputId -ne $a33.chosen.sourceInputId -and
                      $shared42.exit -eq 0 -and $shared43.exit -eq 0 -and $shared42.world -ne $shared43.world -and $shared42.state.chosen.worldContentId -eq $a33.chosen.worldContentId -and
                      $shared43.state.chosen.worldContentId -ne $shared42.state.chosen.worldContentId -and $shared43.worldJsonSha256 -ne $shared42.worldJsonSha256 -and
                      $collision.exit -eq 2 -and $collision.stderr -match 'already exists' -and $collision.state.attempts[0].state -eq 'running' -and
                      $afterCollision.exit -eq 0 -and $afterCollision.state.attempts[0].runs -eq 2 -and $afterCollision.state.chosen.worldContentId -eq $a33.chosen.worldContentId -and
                      $afterCollision.world -ne $shared42.world)

# c2: with minCities 46 at size 129, seed 42 is rejected and the derived seed accepted. Cancel during attempt 1 (after the
# rejection) and resume; hard-kill the bridge during attempt 0 and resume; compare both with an uninterrupted run.
$q46 = Join-Path $work 'world-quality-46.json'
$q = Get-Content $quality -Raw | ConvertFrom-Json; $q.minCities = 46
Write-Json $q46 $q
$reqU = New-Request 'u' 'aa11bb22-cc33-4d44-8e55-ff6600112233' '42' '{"size":129,"phase":16}' (Policies $q46)
$straight = Invoke-Generate 'u' $reqU (Policies $q46)
$reqC = New-Request 'c' '0f1e2d3c-4b5a-4968-8776-655443322110' '42' '{"size":129,"phase":16}' (Policies $q46)
$cancelFlag = Join-Path $work 'c-cancel.flag'
$p = Start-Generate 'c' $reqC (Policies $q46) @('--cancel-file', $cancelFlag)
$sawAttempt1 = Wait-ForPath (Join-Path $work 'c-work/*/attempt-1-run-1/job/staging/split')
Start-Sleep -Milliseconds 1200
New-Item -ItemType File $cancelFlag | Out-Null
$p.WaitForExit(); $cancelExit = $p.ExitCode
$cancelled = Read-State (Join-Path $work 'c-state.json')
Remove-Item $cancelFlag
$resumed = Invoke-Generate 'c-resume' $reqC (Policies $q46) @() $lock 'c'
$reqK = New-Request 'k' '9d8c7b6a-5f4e-4d3c-ab2a-190817263544' '42' '{"size":129,"phase":16}' (Policies $q46)
$p = Start-Generate 'k' $reqK (Policies $q46)
$sawAttempt0 = Wait-ForPath (Join-Path $work 'k-work/*/attempt-0-run-1/job/staging/split')
Start-Sleep -Milliseconds 500
Stop-Process -Id $p.Id -Force
$p.WaitForExit()
Start-Sleep -Milliseconds 1500
$killed = Read-State (Join-Path $work 'k-state.json')
$strayProducers = @(Get-Process -Name fwg -ErrorAction SilentlyContinue).Count
$lockLeft = Test-Path (Join-Path $work 'k-state.json.lock')
$killResumed = Invoke-Generate 'k-resume' $reqK (Policies $q46) @() $lock 'k'
$same = { param($g) $g.state.chosen.seed -eq $straight.state.chosen.seed -and $g.state.chosen.sourceInputId -eq $straight.state.chosen.sourceInputId -and $g.state.chosen.worldContentId -eq $straight.state.chosen.worldContentId }
$results.c2 = [ordered]@{
    uninterrupted = [ordered]@{ exit = $straight.exit; status = [string]$straight.state.status; attempt0 = [ordered]@{ seed = $straight.state.attempts[0].seed; state = [string]$straight.state.attempts[0].state; reasons = Reasons $straight.state.attempts[0] }; chosen = $straight.state.chosen }
    cancel = [ordered]@{ sawAttempt1Running = $sawAttempt1; exit = $cancelExit; status = [string]$cancelled.status; attempts = @($cancelled.attempts | ForEach-Object { [ordered]@{ index = $_.index; seed = $_.seed; state = [string]$_.state; runs = $_.runs; reasons = Reasons $_ } }) }
    cancelResumed = [ordered]@{ exit = $resumed.exit; status = [string]$resumed.state.status; attempts = $resumed.state.attempts.Count; runsOfAttempt1 = $resumed.state.attempts[1].runs; historyOfAttempt1 = @($resumed.state.attempts[1].history); chosen = $resumed.state.chosen }
    kill = [ordered]@{ sawAttempt0Running = $sawAttempt0; status = [string]$killed.status; attempt0 = [string]$killed.attempts[0].state; runs = $killed.attempts[0].runs; strayProducersAfterKill = $strayProducers; lockFileLeft = $lockLeft }
    killResumed = [ordered]@{ exit = $killResumed.exit; status = [string]$killResumed.state.status; runsOfAttempt0 = $killResumed.state.attempts[0].runs; historyOfAttempt0 = @($killResumed.state.attempts[0].history); chosen = $killResumed.state.chosen }
}
$results.c2.passed = ($straight.state.status -eq 'accepted' -and $straight.state.attempts.Count -eq 2 -and $straight.state.attempts[0].state -eq 'rejected' -and $straight.state.attempts[0].reasons[0].code -eq 'quality.too_few_cities' -and
                      $sawAttempt1 -and $cancelExit -eq 4 -and $cancelled.status -eq 'cancelled' -and $cancelled.attempts.Count -eq 2 -and $cancelled.attempts[0].state -eq 'rejected' -and $cancelled.attempts[1].state -eq 'cancelled' -and
                      $resumed.state.status -eq 'accepted' -and $resumed.state.attempts.Count -eq 2 -and $resumed.state.attempts[1].runs -eq 2 -and $resumed.state.attempts[1].history[0].state -eq 'cancelled' -and (& $same $resumed) -and
                      $sawAttempt0 -and $killed.status -eq 'in_progress' -and $killed.attempts[0].state -eq 'running' -and $strayProducers -eq 0 -and -not $lockLeft -and
                      $killResumed.state.status -eq 'accepted' -and $killResumed.state.attempts[0].runs -eq 2 -and $killResumed.state.attempts[0].history[0].state -eq 'running' -and (& $same $killResumed))

# c4: quality exhaustion and immediate (never retried) input, parser and mechanical failures, all through the real driver.
$strict = Join-Path $work 'world-quality-strict.json'
$q = Get-Content $quality -Raw | ConvertFrom-Json; $q.minCities = 1000
Write-Json $strict $q
$reqX = New-Request 'x' 'b1c2d3e4-f5a6-4b7c-8d9e-0f1a2b3c4d5e' '42' '{"size":17,"phase":16}' (Policies $strict)
$gX = Invoke-Generate 'x' $reqX (Policies $strict)
$reqI = New-Request 'i' 'c2d3e4f5-a6b7-4c8d-9e0f-1a2b3c4d5e6f' '42' '{"size":2,"phase":16}'
$gI = Invoke-Generate 'i' $reqI
$layerContract = Join-Path $work 'source-compatibility-extra-layer.json'
$cc = Get-Content $contract -Raw | ConvertFrom-Json; $cc.required_layers = @($cc.required_layers) + @('mnbl_absent_layer')
Write-Json $layerContract $cc
$reqP = New-Request 'p' 'd3e4f5a6-b7c8-4d9e-8f0a-2b3c4d5e6f7a' '42' '{"size":33,"phase":16}' (Policies $quality $layerContract)
$gP = Invoke-Generate 'p' $reqP (Policies $quality $layerContract)
$reqM = New-Request 'm' 'e4f5a6b7-c8d9-4e0f-9a1b-3c4d5e6f7a8b' '42' '{"size":33,"phase":16}'
$gM = Invoke-Generate 'm' $reqM (Policies) @('--timeout-ms', '1')
$results.c4 = [ordered]@{
    exhausted = [ordered]@{ exit = $gX.exit; status = [string]$gX.state.status; seeds = @($gX.state.attempts | ForEach-Object { $_.seed }); reasons = @($gX.state.attempts | ForEach-Object { Reasons $_ }); summary = $gX.stdout }
    inputFailure = [ordered]@{ exit = $gI.exit; status = [string]$gI.state.status; attempts = $gI.state.attempts.Count; failure = [string]$gI.state.attempts[0].failure; reasons = Reasons $gI.state.attempts[0] }
    parserFailure = [ordered]@{ exit = $gP.exit; status = [string]$gP.state.status; attempts = $gP.state.attempts.Count; failure = [string]$gP.state.attempts[0].failure; reasons = Reasons $gP.state.attempts[0] }
    mechanicalFailure = [ordered]@{ exit = $gM.exit; status = [string]$gM.state.status; attempts = $gM.state.attempts.Count; failure = [string]$gM.state.attempts[0].failure; reasons = Reasons $gM.state.attempts[0] }
}
$results.c4.passed = ($gX.exit -eq 3 -and $gX.state.status -eq 'exhausted' -and $gX.state.attempts.Count -eq 3 -and (($gX.state.attempts | ForEach-Object { $_.seed }) -join ',') -eq '42,2675596451,3430524811' -and
                      $gI.exit -eq 3 -and $gI.state.status -eq 'failed' -and $gI.state.attempts.Count -eq 1 -and $gI.state.attempts[0].failure -eq 'input' -and
                      $gP.exit -eq 3 -and $gP.state.status -eq 'failed' -and $gP.state.attempts.Count -eq 1 -and $gP.state.attempts[0].failure -eq 'parser' -and $gP.state.attempts[0].reasons[0].message -match 'mnbl_absent_layer' -and
                      $gM.exit -eq 3 -and $gM.state.status -eq 'failed' -and $gM.state.attempts.Count -eq 1 -and $gM.state.attempts[0].failure -eq 'mechanical' -and $gM.state.attempts[0].reasons[0].code -eq 'producer.timeout')

# Bindings and usage: refused before any attempt runs.
# Requests --new-request would refuse, written by hand (a host could still hand one over).
function Edit-Request([string]$Name, [scriptblock]$Change) {
    $j = Get-Content $reqA -Raw | ConvertFrom-Json
    & $Change $j
    $out = Join-Path $work "$Name-request.json"
    [IO.File]::WriteAllText($out, ($j | ConvertTo-Json -Depth 5 -Compress), $utf8)
    return $out
}
$reqNoContract = Edit-Request 'nocontract' { param($j) $j.policies.PSObject.Properties.Remove('source-compatibility') }
$reqFour = Edit-Request 'four' { param($j) $j.maxAttempts = 4 }
$reqWide = Join-Path $work 'wide-request.json'
[IO.File]::WriteAllText($reqWide, ([IO.File]::ReadAllText($reqA).Replace('"phase":16', '"phase":9007199254740993.0')), $utf8)
$otherWidth = Join-Path $work 'producer-seed-width-other.json'
$w = Get-Content $seedWidth -Raw | ConvertFrom-Json; $w.producerSha256 = '0' * 64
Write-Json $otherWidth $w
$bindings = [ordered]@{
    changedQualityPolicy = Invoke-Generate 'bind-quality' $reqA (Policies $strict)
    requestWithoutContract = Invoke-Generate 'bind-nocontract' $reqNoContract "product-policy=$product,world-quality=$quality"
    attemptsAbovePolicy = Invoke-Generate 'bind-four' $reqFour
    seedWidthOfAnotherProducer = Invoke-Generate 'bind-width' $reqA (Policies) @() $lock 'bind-width' $work 'bind-width-work' $otherWidth
    threadsOutOfRange = Invoke-Generate 'bind-threads' $reqA (Policies) @('--threads', '5000')
    timeoutNotNumeric = Invoke-Generate 'bind-timeout' $reqA (Policies) @('--timeout-ms', '10s')
    overrideBeyond2_53 = Invoke-Generate 'bind-wide' $reqWide
}
$expect = [ordered]@{ changedQualityPolicy = 'policy_changed'; requestWithoutContract = 'missing_field'; attemptsAbovePolicy = 'policy_violation'; seedWidthOfAnotherProducer = 'seed_width_unverified'
                      threadsOutOfRange = '--threads must be a decimal integer'; timeoutNotNumeric = '--timeout-ms must be a decimal integer'
                      overrideBeyond2_53 = 'out_of_range' }
$results.bindings = [ordered]@{}
$bindingsPassed = $true
foreach ($name in $expect.Keys) {
    $g = $bindings[$name]
    $ok = ($g.exit -eq 2 -and $g.stderr -match [regex]::Escape($expect[$name]) -and $null -eq $g.state)
    $bindingsPassed = $bindingsPassed -and $ok
    $results.bindings[$name] = [ordered]@{ exit = $g.exit; stderr = $g.stderr; stateWritten = ($null -ne $g.state); expected = $expect[$name]; passed = $ok }
}
$results.bindings.passed = $bindingsPassed

# --new-request never replaces an accepted request and never publishes one that does not validate.
$reqAHash = Sha $reqA
$newArgs = { param($Out, $Instance) @('--new-request', '--out', $Out, '--instance', $Instance, '--seed', '7', '--overrides', (Join-Path $work 'a33-overrides.json'), '--lock', $lock, '--policies', (Policies), '--max-attempts', '3') }
$existing = Invoke-Bridge (& $newArgs $reqA '0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d') 'new-existing'
$badOut = Join-Path $work 'bad-instance-request.json'
$badInstance = Invoke-Bridge (& $newArgs $badOut 'NOT-A-UUID') 'new-badinstance'
$lax = [ordered]@{}
foreach ($case in @(@('seed-suffix', '--seed', '42abc'), @('seed-hex', '--seed', '0x10'), @('seed-negative', '--seed', '-18446744073709551574'), @('random-zero', '--random', '0'), @('attempts-suffix', '--max-attempts', '3x'))) {
    $out = Join-Path $work "lax-$($case[0])-request.json"
    $argv = (& $newArgs $out '0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d')
    if ($case[1] -eq '--random') { $argv[5] = '--random'; $argv[6] = $case[2] }
    elseif ($case[1] -eq '--seed') { $argv[6] = $case[2] }
    else { $argv[$argv.Count - 1] = $case[2] }
    $r = Invoke-Bridge $argv "new-lax-$($case[0])"
    $lax[$case[0]] = [ordered]@{ value = $case[2]; exit = $r.exit; stderr = $r.stderr; written = (Test-Path $out) }
}
$policyCases = [ordered]@{
    missingContract = @{ policies = "product-policy=$product,world-quality=$quality"; attempts = '3'; expect = "must bind 'source-compatibility'" }
    attemptsAbovePolicy = @{ policies = (Policies); attempts = '4'; expect = 'exceeds the product policy' }
}
$policyRefusals = [ordered]@{}
foreach ($name in $policyCases.Keys) {
    $out = Join-Path $work "policy-$name-request.json"
    $argv = (& $newArgs $out '0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d')
    $argv[12] = $policyCases[$name].policies; $argv[14] = $policyCases[$name].attempts
    $r = Invoke-Bridge $argv "new-policy-$name"
    $policyRefusals[$name] = [ordered]@{ exit = $r.exit; stderr = $r.stderr; written = (Test-Path $out); passed = ($r.exit -eq 2 -and $r.stderr -match [regex]::Escape($policyCases[$name].expect) -and -not (Test-Path $out)) }
}
$wideOverrides = Join-Path $work 'wide-overrides.json'
[IO.File]::WriteAllText($wideOverrides, '{"size":33,"phase":9007199254740993}', $utf8)
$wideOut = Join-Path $work 'wide-new-request.json'
$argv = (& $newArgs $wideOut '0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d'); $argv[8] = $wideOverrides
$wideNew = Invoke-Bridge $argv 'new-wide'
$results.newRequest = [ordered]@{
    policyRefusals = $policyRefusals
    overrideBeyond2_53 = [ordered]@{ exit = $wideNew.exit; stderr = $wideNew.stderr; written = (Test-Path $wideOut) }
    existingOut = [ordered]@{ exit = $existing.exit; stderr = $existing.stderr; requestUnchanged = ((Sha $reqA) -eq $reqAHash) }
    invalidInstance = [ordered]@{ exit = $badInstance.exit; stderr = $badInstance.stderr; written = (Test-Path $badOut); pendingLeft = (Test-Path "$badOut.pending") }
    laxNumbers = $lax
}
$results.newRequest.passed = ($existing.exit -eq 2 -and $existing.stderr -match 'already exists' -and (Sha $reqA) -eq $reqAHash -and
                              $badInstance.exit -eq 2 -and $badInstance.stderr -match 'campaignInstanceId' -and -not (Test-Path $badOut) -and -not (Test-Path "$badOut.pending") -and
                              @($lax.Values | Where-Object { $_.exit -ne 2 -or $_.written }).Count -eq 0 -and
                              @($policyRefusals.Values | Where-Object { -not $_.passed }).Count -eq 0 -and
                              $wideNew.exit -eq 2 -and $wideNew.stderr -match 'out_of_range' -and -not (Test-Path $wideOut))

# Non-ASCII locations: a Windows-1252 representable directory generates the same world; a directory outside the ANSI code
# page cannot reach the producer's narrow argv and is refused before any state exists (it can be retried elsewhere).
$latin = Join-Path $work ('w' + [char]0x00E9 + 'rld-' + [char]0x00F6 + [char]0x00DF)
$cjk = Join-Path $work ('world-' + [char]0x4E16 + [char]0x754C)
New-Item -ItemType Directory -Force $latin, $cjk | Out-Null
$gLatin = Invoke-Generate 'latin' $reqA (Policies) @() $lock 'latin' $latin
$gCjk = Invoke-Generate 'cjk' $reqA (Policies) @() $lock 'cjk' $cjk
$acp = [Text.Encoding]::Default.CodePage
$results.nonAscii = [ordered]@{
    ansiCodePage = $acp
    latin = [ordered]@{ exit = $gLatin.exit; status = [string]$gLatin.state.status; worldContentId = $gLatin.state.chosen.worldContentId; worldExists = [bool]($gLatin.world -and (Test-Path -LiteralPath $gLatin.world)) }
    outsideCodePage = [ordered]@{ exit = $gCjk.exit; stderr = $gCjk.stderr; stateWritten = ($null -ne $gCjk.state) }
}
$results.nonAscii.passed = ($gLatin.exit -eq 0 -and $gLatin.state.chosen.worldContentId -eq $a33.chosen.worldContentId -and $results.nonAscii.latin.worldExists -and
                            (($acp -eq 65001 -and $gCjk.exit -eq 0) -or ($gCjk.exit -eq 2 -and $gCjk.stderr -match 'unrepresentable_path' -and $null -eq $gCjk.state)))

# Frozen: an accepted campaign loads its verified world under a changed lock; nothing is regenerated. A missing or altered
# world is refused, never regenerated.
$newerLock = Join-Path $work 'producer.lock.newer.json'
[IO.File]::WriteAllText($newerLock, ((Get-Content $lock -Raw) + ' '), $utf8)
$aWork = Join-Path $work ('a33-threads1-work/' + $a33.requestId.Substring(0, 16))
$before = @(Get-ChildItem $aWork -Directory).Count
$frozen = Invoke-Generate 'a-frozen' $reqA (Policies) @() $newerLock 'a33-threads1'
$after = @(Get-ChildItem $aWork -Directory).Count
$newGeneration = Invoke-Generate 'a-newlock' $reqA (Policies) @() $newerLock 'a-newlock'
$a4 = Read-State (Join-Path $work 'a33-threads4-state.json')
$a4World = Chosen-World (Join-Path $work 'a33-threads4-work') $a4
[IO.File]::AppendAllText((Join-Path $a4World 'world.json'), ' ', $utf8)
$tampered = Invoke-Generate 'a-tampered' $reqA (Policies) @() $lock 'a33-threads4'
$results.frozen = [ordered]@{ exit = $frozen.exit; status = [string]$frozen.state.status; summary = $frozen.stdout; attemptDirsBefore = $before; attemptDirsAfter = $after
                              newGenerationWithChangedLock = [ordered]@{ exit = $newGeneration.exit; stderr = $newGeneration.stderr }
                              alteredWorld = [ordered]@{ exit = $tampered.exit; stderr = $tampered.stderr; stateUnchanged = ($tampered.stateSha256 -eq $c1.size33.runs[2].stateSha256) }
                              passed = ($frozen.exit -eq 0 -and $frozen.stdout -match [regex]::Escape($a33.chosen.worldContentId) -and $before -eq $after -and
                                        $newGeneration.exit -eq 2 -and $newGeneration.stderr -match 'producer_changed' -and
                                        $tampered.exit -eq 2 -and $tampered.stderr -match 'frozen_world_mismatch' -and $tampered.stateSha256 -eq $c1.size33.runs[2].stateSha256) }

# Persisted state is independent of how --work is spelled (lowercase, 8.3 short form) and carries no absolute path.
$short = (New-Object -ComObject Scripting.FileSystemObject).GetFolder($work).ShortPath
$spellings = [ordered]@{ asGiven = $gP; lowercase = (Invoke-Generate 'p-lower' $reqP (Policies $quality $layerContract) @() $lock 'p-lower' $work.ToLowerInvariant())
                         shortName = (Invoke-Generate 'p-short' $reqP (Policies $quality $layerContract) @() $lock 'p-short' $short) }
$leaks = @(Get-ChildItem $work -Recurse -Filter '*-state.json' | Where-Object { [IO.File]::ReadAllText($_.FullName) -match '[A-Za-z]:[\\/]' } | ForEach-Object { $_.Name })
$results.statePaths = [ordered]@{ statesChecked = @(Get-ChildItem $work -Recurse -Filter '*-state.json').Count; statesWithDrivePaths = @($leaks); shortWorkPath = $short
                                  parserStateBySpelling = [ordered]@{ asGiven = $gP.stateSha256; lowercase = $spellings.lowercase.stateSha256; shortName = $spellings.shortName.stateSha256 }
                                  passed = ($leaks.Count -eq 0 -and @($spellings.Values | ForEach-Object { $_.stateSha256 } | Select-Object -Unique).Count -eq 1 -and $null -ne $gP.stateSha256) }

$evidence = [ordered]@{
    schema = 'mnbl.generation-identity-evidence'; version = 1; ticket = 'MMR-24'; utc = [DateTime]::UtcNow.ToString('O'); work = $work
    producerSha256 = $producerSha; producerLockSha256 = (Sha $lock); bridgeSha256 = (Sha $bridge)
    policies = [ordered]@{ product = (Sha $product); worldQuality = (Sha $quality); sourceCompatibility = (Sha $contract); seedWidth = (Sha $seedWidth) }
    results = $results
}
$evidence.passed = ($results.unitTests.exit -eq 0 -and $results.seedWidth.verified -and $results.c1.passed -and $results.c2.passed -and $results.c3.passed -and $results.c4.passed -and
                    $results.bindings.passed -and $results.newRequest.passed -and $results.nonAscii.passed -and $results.frozen.passed -and $results.statePaths.passed)
$out = Join-Path $repo 'evidence/fwg/generation-identity.json'
[IO.File]::WriteAllText($out, ($evidence | ConvertTo-Json -Depth 12), $utf8)
"Generation identity evidence: passed=$($evidence.passed) unit=$($results.unitTests.summary) width=$($results.seedWidth.verified) c1=$($results.c1.passed) c2=$($results.c2.passed) c3=$($results.c3.passed) c4=$($results.c4.passed) bindings=$($results.bindings.passed) newRequest=$($results.newRequest.passed) nonAscii=$($results.nonAscii.passed) frozen=$($results.frozen.passed) statePaths=$($results.statePaths.passed)"
exit ([int](-not $evidence.passed))
