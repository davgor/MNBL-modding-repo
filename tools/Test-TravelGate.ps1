# MMR-28: the static travel gate inside worldbridge --generate on the pinned producer.
# Request A binds the real travel policies (size 129, seed 42) and must be accepted. Request B binds a stricter
# playability variant (maxSnapFraction 0): its first attempt must be rejected with quality.repair_bound_exceeded and
# retried on a derived seed, because world-quality declares that code retryable, with the UIDs in the reason. Request C
# binds a variant the compiler refuses (grid side 300, which the terrain import calibration does not list): the
# generation must fail at once with a compiler.* reason, never retry.
# Writes evidence/fwg/travel-gate.json.
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$bridge = Join-Path $repo 'build/worldbridge/Release/worldbridge.exe'
$producer = Join-Path $repo 'build/producer/apps/Release/fwg.exe'
$lock = Join-Path $repo 'worldgen/producer.lock.json'
$seedWidth = Join-Path $repo 'worldgen/contracts/producer-seed-width.json'
$work = Join-Path $repo ('build/research-travel28/gate-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
foreach ($path in @($bridge, $producer)) { if (!(Test-Path $path)) { throw "Missing $path; run tools/Build.ps1 first." } }
$utf8 = [Text.UTF8Encoding]::new($false)
function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path).Hash.ToLowerInvariant() }
function P([string]$Rel) { Join-Path $repo $Rel }

# The strict variant sits beside a copy of the calibration so its relative calibration path resolves.
$variantDir = Join-Path $work 'policies'
New-Item -ItemType Directory -Force $variantDir, (Join-Path $work 'calibration/naval-transitions') | Out-Null
Copy-Item (P 'worldgen/calibration/naval-transitions/naval-transitions.json') (Join-Path $work 'calibration/naval-transitions/naval-transitions.json')
$strict = Join-Path $variantDir 'playability-strict.json'
$text = [IO.File]::ReadAllText((P 'worldgen/policies/playability.json'))
if (-not $text.Contains('"maxSnapFraction": 0.05')) { throw 'playability.json no longer has maxSnapFraction 0.05' }
[IO.File]::WriteAllText($strict, $text.Replace('"maxSnapFraction": 0.05', '"maxSnapFraction": 0'), $utf8)
$refused = Join-Path $variantDir 'playability-refused.json'
if (-not $text.Contains('"side": 257')) { throw 'playability.json no longer has grid side 257' }
[IO.File]::WriteAllText($refused, $text.Replace('"side": 257', '"side": 300'), $utf8)

function Policies([string]$Playability) {
    @("product-policy=$(P 'worldgen/policies/product-policy.json')", "world-quality=$(P 'worldgen/policies/world-quality.json')",
      "source-compatibility=$(P 'worldgen/contracts/source-compatibility.json')", "projection=$(P 'worldgen/policies/projection.json')",
      "terrain-materials=$(P 'worldgen/policies/terrain-materials.json')", "terrain-import=$(P 'worldgen/calibration/terrain-import/terrain-import.json')",
      "campaign-content=$(P 'worldgen/policies/campaign-content.json')", "culture-presentations=$(P 'worldgen/policies/culture-presentations.json')",
      "playability=$Playability", "population=$(P 'worldgen/policies/population.json')") -join ','
}
function Invoke-Bridge([string[]]$Arguments, [string]$Name) {
    $ErrorActionPreference = 'Continue'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $out = & $bridge @Arguments 2> (Join-Path $work "$Name.err")
    $code = $LASTEXITCODE
    $watch.Stop()
    [IO.File]::WriteAllText((Join-Path $work "$Name.out"), [string]($out -join "`n"), $utf8)
    return @{ exit = $code; stdout = [string]($out -join "`n"); stderr = [string](Get-Content (Join-Path $work "$Name.err") -Raw); seconds = [Math]::Round($watch.Elapsed.TotalSeconds, 1) }
}
function Run-Request([string]$Name, [string]$Playability, [int]$Attempts) {
    $overrides = Join-Path $work "$Name-overrides.json"
    [IO.File]::WriteAllText($overrides, '{"size":129,"phase":16}', $utf8)
    $req = Join-Path $work "$Name-request.json"
    $list = Policies $Playability
    $n = Invoke-Bridge @('--new-request', '--out', $req, '--instance', ([Guid]::NewGuid().ToString()), '--seed', '42', '--overrides', $overrides, '--lock', $lock, '--policies', $list, '--max-attempts', "$Attempts") "$Name-new"
    if ($n.exit -ne 0) { throw "new-request $Name failed: $($n.stderr)" }
    $state = Join-Path $work "$Name-state.json"
    $g = Invoke-Bridge @('--generate', '--request', $req, '--state', $state, '--work', (Join-Path $work "$Name-work"), '--lock', $lock, '--producer', $producer, '--seed-width', $seedWidth, '--policies', $list) "$Name-generate"
    $s = Get-Content -LiteralPath $state -Raw -Encoding UTF8 | ConvertFrom-Json
    $first = Join-Path $work ("$Name-work/" + $s.requestId.Substring(0, 16) + '/attempt-0-run-1/travel/travel-graph.json')
    return @{ exit = $g.exit; seconds = $g.seconds; status = $s.status; summary = $g.stdout; attempts = @($s.attempts); playabilitySha256 = (Sha $Playability); firstAttemptTravelGraph = (Test-Path $first) }
}

$a = Run-Request 'accept' (P 'worldgen/policies/playability.json') 1
$b = Run-Request 'strict' $strict 2
$c = Run-Request 'refused' $refused 2
$first = $b.attempts[0]
$checks = [ordered]@{
    acceptedWithRealPolicies = ($a.status -eq 'accepted' -and $a.exit -eq 0 -and $a.attempts[0].state -eq 'accepted')
    strictFirstAttemptRejected = ($first.state -eq 'rejected' -and $first.failure -eq 'quality' -and @($first.reasons | Where-Object { $_.code -eq 'quality.repair_bound_exceeded' }).Count -ge 1)
    strictRetriedOnDerivedSeed = ($b.attempts.Count -eq 2 -and $b.attempts[1].seed -ne $first.seed)
    strictReasonsNameUids = (@($first.reasons | Where-Object { $_.message -like '*`[uids:*' }).Count -ge 1)
    attemptKeepsTravelOutputs = ($a.firstAttemptTravelGraph -and $b.firstAttemptTravelGraph)
    compilerRefusalFailsAtOnce = ($c.status -eq 'failed' -and $c.attempts.Count -eq 1 -and $c.attempts[0].failure -eq 'parser' -and @($c.attempts[0].reasons | Where-Object { $_.code -like 'compiler.*' }).Count -ge 1)
}
$summary = [ordered]@{
    schema = 'mnbl.travel-gate'; version = 1; ticket = 'MMR-28'; generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
    tool = @{ worldbridge = (Sha $bridge); producer = (Sha $producer) }
    policies = @{ playability = (Sha (P 'worldgen/policies/playability.json')); playabilityStrict = (Sha $strict); playabilityRefused = (Sha $refused); worldQuality = (Sha (P 'worldgen/policies/world-quality.json')) }
    accept = $a; strict = $b; refused = $c; checks = $checks
    passed = (($checks.Values | Where-Object { -not $_ }).Count -eq 0)
    work = $work.Substring($repo.Length + 1).Replace('\', '/')
    note = 'Request B shows the retry path, not a recommended policy: maxSnapFraction 0 rejects the pinned world, which needs 4 snaps.'
}
[IO.File]::WriteAllText((P 'evidence/fwg/travel-gate.json'), ($summary | ConvertTo-Json -Depth 10), $utf8)
"passed=$($summary.passed) accept=$($a.status) strict=$($b.status) attempts=$($b.attempts.Count) refused=$($c.status)"
