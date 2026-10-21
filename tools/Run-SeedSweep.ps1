# MMR-251: the offline seed sweep (no game) as one script. Per seed: worldbridge --new-request and --generate with the module's policy list
# (the nine policies Test-TravelGate binds) and the one-shot overrides {"size":129,"phase":16}, -MaxAttempts quality attempts (3, the
# product policy's ceiling, by default); then the accepted attempt's world through worldbridge --bundle, the settlement harness
# (tools/Test-Settlements.ps1), the navigation sweep with placement and flags (tools/Test-NavigationSweep.ps1) and the per-bundle activation
# harnesses, as tools/summarize_seed_yield.py documents. Each -Stage can run alone on an existing -Out (generate, bundle, settlements,
# navigation, harnesses; all by default). Writes under -Out: sweep.jsonl (one row per seed: status, every attempt's seed, state, city
# count and reason codes, the chosen attempt), bundles/, settlement-checks.jsonl with settlements-<seed>.json, navigation/ and harnesses/
# in the formats summarize_seed_yield.py reads. tools/summarize_city_preservation.py turns them into evidence/fwg/city-preservation.json.
param([int[]]$Seeds = (42..61), [int]$MaxAttempts = 3, [string]$Out, [string[]]$Stage = @('generate', 'bundle', 'settlements', 'navigation', 'harnesses'),
      [string]$GameRoot = 'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord')
$ErrorActionPreference = 'Stop'
$Stage = @($Stage | ForEach-Object { $_ -split ',' })   # powershell -File passes "a,b" as one string
$repo = Split-Path $PSScriptRoot
if (-not $Out) { $Out = Join-Path $repo ('build/seed-sweep/' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path -LiteralPath $Out).Path
$bridge = Join-Path $repo 'build/worldbridge/Release/worldbridge.exe'
$producer = Join-Path $repo 'build/producer/apps/Release/fwg.exe'
$lock = Join-Path $repo 'worldgen/producer.lock.json'
$seedWidth = Join-Path $repo 'worldgen/contracts/producer-seed-width.json'
foreach ($path in @($bridge, $producer)) { if (!(Test-Path $path)) { throw "Missing $path; build first." } }
$utf8 = [Text.UTF8Encoding]::new($false)
function P([string]$Rel) { Join-Path $repo $Rel }
function Append([string]$File, $Row) { [IO.File]::AppendAllText($File, (($Row | ConvertTo-Json -Depth 10 -Compress) + "`n"), $utf8) }
$policies = @("product-policy=$(P 'worldgen/policies/product-policy.json')", "world-quality=$(P 'worldgen/policies/world-quality.json')",
    "source-compatibility=$(P 'worldgen/contracts/source-compatibility.json')", "projection=$(P 'worldgen/policies/projection.json')",
    "terrain-materials=$(P 'worldgen/policies/terrain-materials.json')", "terrain-import=$(P 'worldgen/calibration/terrain-import/terrain-import.json')",
    "campaign-content=$(P 'worldgen/policies/campaign-content.json')", "culture-presentations=$(P 'worldgen/policies/culture-presentations.json')",
    "playability=$(P 'worldgen/policies/playability.json')", "population=$(P 'worldgen/policies/population.json')") -join ','
function Bridge([string[]]$Arguments, [string]$Name) {
    $ErrorActionPreference = 'Continue'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $o = & $bridge @Arguments 2> (Join-Path $Out "$Name.err")
    $code = $LASTEXITCODE
    return @{ exit = $code; stdout = [string]($o -join "`n"); seconds = [Math]::Round($watch.Elapsed.TotalSeconds, 1) }
}
function State([int]$Seed) { Get-Content -LiteralPath (Join-Path $Out "state-$Seed.json") -Raw -Encoding UTF8 | ConvertFrom-Json }
function WorldOf($s) {
    $a = $s.attempts[$s.chosen.attempt]
    Join-Path $Out ("w$($s.masterSeed)/" + $s.requestId.Substring(0, 16) + "/attempt-$($a.index)-run-$($a.runs)")
}

if ($Stage -contains 'generate') {
    $sweep = Join-Path $Out 'sweep.jsonl'
    if (Test-Path $sweep) { Remove-Item $sweep }
    [IO.File]::WriteAllText((Join-Path $Out 'overrides.json'), '{"size":129,"phase":16}', $utf8)
    foreach ($seed in $Seeds) {
        $req = Join-Path $Out "req-$seed.json"
        foreach ($f in @($req, (Join-Path $Out "state-$seed.json"))) { if (Test-Path $f) { Remove-Item $f } }
        if (Test-Path (Join-Path $Out "w$seed")) { Remove-Item -Recurse -Force (Join-Path $Out "w$seed") }
        $instance = '00000000-0000-4000-8000-0000000000' + ('{0:d2}' -f ($seed % 100))
        $n = Bridge @('--new-request', '--out', $req, '--instance', $instance, '--seed', "$seed", '--overrides', (Join-Path $Out 'overrides.json'), '--lock', $lock, '--policies', $policies, '--max-attempts', "$MaxAttempts") "new-$seed"
        if ($n.exit -ne 0) { throw "new-request $seed failed" }
        $g = Bridge @('--generate', '--request', $req, '--state', (Join-Path $Out "state-$seed.json"), '--work', (Join-Path $Out "w$seed"), '--lock', $lock, '--producer', $producer, '--seed-width', $seedWidth, '--policies', $policies) "gen-$seed"
        $s = State $seed
        $attempts = @($s.attempts | ForEach-Object {
            $dir = Join-Path $Out ("w$seed/" + $s.requestId.Substring(0, 16) + "/attempt-$($_.index)-run-$($_.runs)")
            @{ index = $_.index; seed = $_.seed; state = $_.state; failure = $_.failure; cities = $_.cities; codes = @($_.reasons | ForEach-Object { $_.code } | Sort-Object -Unique)
               messages = @($_.reasons | ForEach-Object { $_.message.Substring(0, [Math]::Min(300, $_.message.Length)) }); dir = $dir
               graph = $(if (Test-Path "$dir/travel/travel-graph.json") { "$dir/travel/travel-graph.json" } else { $null }) } })
        Append $sweep @{ seed = $seed; exit = $g.exit; status = $s.status; seconds = $g.seconds; chosen = $(if ($s.chosen) { $s.chosen.attempt } else { $null })
                         worldContentId = $(if ($s.chosen) { $s.chosen.worldContentId } else { '' }); attempts = $attempts }
        "seed $seed $($s.status) attempts=$($s.attempts.Count) $($g.seconds)s"
    }
}

$rows = @(Get-Content -LiteralPath (Join-Path $Out 'sweep.jsonl') -Encoding UTF8 | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $Seeds -contains $_.seed })
$accepted = @($rows | Where-Object { $_.status -eq 'accepted' })
$bundles = Join-Path $Out 'bundles'
if ($Stage -contains 'bundle') {
    New-Item -ItemType Directory -Force $bundles | Out-Null
    foreach ($r in $accepted) {
        $world = WorldOf (State $r.seed)
        $b = Bridge @('--bundle', "$world/world", '--contract', (P 'worldgen/contracts/source-compatibility.json'), '--projection-policy', (P 'worldgen/policies/projection.json'),
            '--materials', (P 'worldgen/policies/terrain-materials.json'), '--import', (P 'worldgen/calibration/terrain-import/terrain-import.json'), '--content', (P 'worldgen/policies/campaign-content.json'),
            '--cultures', (P 'worldgen/policies/culture-presentations.json'), '--playability', (P 'worldgen/policies/playability.json'), '--shaping', (P 'worldgen/policies/terrain-shaping.json'), '--population', (P 'worldgen/policies/population.json'), '--producer-lock', $lock,
            '--runtime-lock', (P 'config/runtime-api-lock.json'), '--recipe', "$world/request.json", '--root', $bundles, '--diagnostics', (Join-Path $Out "diag-$($r.seed)")) "bundle-$($r.seed)"
        "bundle $($r.seed) exit=$($b.exit) $($b.seconds)s"
    }
}
# Bundles to seeds: a bundle holds its world's travel graph, the same bytes as the accepted attempt's.
function BundleOf($r) {
    $graph = (Get-FileHash -LiteralPath ((WorldOf (State $r.seed)) + '/travel/travel-graph.json')).Hash
    foreach ($d in @(Get-ChildItem -LiteralPath $bundles -Directory -ErrorAction SilentlyContinue | Where-Object { Test-Path (Join-Path $_.FullName 'DATA_READY') })) {
        if ((Get-FileHash -LiteralPath (Join-Path $d.FullName 'compiled/travel/travel-graph.json')).Hash -eq $graph) { return $d.FullName }
    }
    return $null
}
function Child([string]$Script, [string[]]$Arguments, [string]$Log) {
    $ErrorActionPreference = 'Continue'
    $o = & powershell -NoProfile -ExecutionPolicy Bypass -File (P $Script) @Arguments 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    [IO.File]::WriteAllLines($Log, [string[]]$o, $utf8)
    return @{ exit = $code; lines = @($o) }
}
if ($Stage -contains 'settlements') {
    $checks = Join-Path $Out 'settlement-checks.jsonl'
    if (Test-Path $checks) { Remove-Item $checks }
    foreach ($r in $accepted) {
        $bundle = BundleOf $r
        if (-not $bundle) { continue }
        $c = Child 'tools/Test-Settlements.ps1' @('-Bundle', $bundle, '-Evidence', (Join-Path $Out "settlements-$($r.seed).json")) (Join-Path $Out "settlements-$($r.seed).log")
        Append $checks @{ seed = $r.seed; exit = $c.exit; fails = @($c.lines | Where-Object { $_ -like 'FAIL*' }); tail = @($c.lines | Select-Object -Last 2) }
        "settlements $($r.seed) exit=$($c.exit)"
    }
}
if ($Stage -contains 'navigation') {
    $paths = @($accepted | ForEach-Object { BundleOf $_ } | Where-Object { $_ })
    if ($paths.Count) {
        $nav = Join-Path $Out 'navigation'
        if (Test-Path (Join-Path $nav 'navigation-sweep.jsonl')) { Remove-Item (Join-Path $nav 'navigation-sweep.jsonl') }
        # -Root: powershell -File binds only the first value of an array parameter (-Bundle a b).
        $c = Child 'tools/Test-NavigationSweep.ps1' @('-Out', $nav, '-GameRoot', $GameRoot, '-Root', $bundles) (Join-Path $Out 'navigation.log')
        "navigation exit=$($c.exit)"
    }
}
if ($Stage -contains 'harnesses') {
    $dir = Join-Path $Out 'harnesses'
    New-Item -ItemType Directory -Force $dir | Out-Null
    $list = Join-Path $dir 'harnesses.jsonl'
    if (Test-Path $list) { Remove-Item $list }
    foreach ($r in $accepted) {
        $bundle = BundleOf $r
        if (-not $bundle) { continue }
        $short = (Split-Path $bundle -Leaf).Substring(0, 12)
        foreach ($h in @('NavalContent', 'SpawnPolicy', 'GraphValidation', 'Factions', 'Characters', 'CampaignRegistry', 'WaterClimate')) {
            $args_ = @('-Bundle', $bundle)
            if ($h -ne 'CampaignRegistry') { $args_ += @('-Evidence', (Join-Path $dir "$short-$h.json")) }
            $c = Child "tools/Test-$h.ps1" $args_ (Join-Path $dir "$short-$h.log")
            Append $list @{ bundle = $short; harness = $h; exit = $c.exit; fails = @($c.lines | Where-Object { $_ -like 'FAIL*' }); tail = @($c.lines | Select-Object -Last 2) }
        }
        "harnesses $($r.seed) done"
    }
}
"sweep: $Out"
