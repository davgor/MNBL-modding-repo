# MMR-1: every offline suite of the repository (no game launched), each in its own PowerShell or Python process, with its exit code and
# last output lines recorded. The world bundles the suites read are world A (seed 42) and world B (seed 73) as the in-game generation
# published them (build/research-bundle29, build/research-bundle37; docs/runbooks/development.md says how to make them).
# Not run: tools/Test-GenerationIdentity.ps1, which rewrites the embedded worldgen/contracts/producer-seed-width.json and so the build's
# recorded inputs (MMR-24's evidence stands). Run while no game is running: Test-WorldSurface reads the deployed module, and the suites
# compile with the build tools. Writes build/mmr1/offline-suites.json and evidence/one-shot/offline-suites.json.
param(
    [string]$BundleA = 'build/research-bundle29/bundles/f596b58cdcfc18798542fdef5a4d9c9f6d84abddca1425af8d0b87f521191d2d',
    [string]$BundleB = 'build/research-bundle37/bundles/f63c6841085725e2c437b7d5d087a52cf4dc7d5d8fe3e2d583d46cba6a775550',
    [string]$Zip = 'artifacts/release/MNBL.ProceduralWorld-v0.0.1.zip',
    [string]$ProducerRoot
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
Set-Location $repo
if (Get-Process Bannerlord,Bannerlord.Native -ErrorAction SilentlyContinue) { throw 'A game is running; the offline suites run without one.' }
foreach ($b in @($BundleA, $BundleB)) { if (!(Test-Path -LiteralPath $b)) { throw "No bundle at $b" } }
$suites = [ordered]@{
    'Test-ApiPins' = @('-VerifyBuild')
    'Test-Activation' = @(); 'Test-GenerationJob' = @(); 'Test-EarlyBinding' = @(); 'Test-SaveBinding' = @(); 'Test-WorldLoader' = @(); 'Test-AutorunCleanup' = @(); 'Test-Heightfield' = @()
    'Test-GeneratedCampaignDataset' = @(); 'Test-Doctor' = @(); 'Test-Deployment' = @()
    'Test-CampaignRegistry' = @('-Bundle', $BundleA); 'Test-Characters' = @('-Bundle', $BundleA); 'Test-Factions' = @('-Bundle', $BundleA)
    'Test-Settlements' = @('-Bundle', $BundleA); 'Test-SpawnPolicy' = @('-Bundle', $BundleA); 'Test-NavalContent' = @('-Bundle', $BundleA)
    'Test-CompiledWorld' = @('-Bundle', $BundleA); 'Test-Compatibility' = @('-Bundle', $BundleA); 'Test-PeriodicCoordinates' = @('-Bundle', $BundleA)
    'Test-WaterClimate' = @('-Bundle', $BundleA); 'Test-WorldDataBundle' = @('-Bundle', $BundleA); 'Test-GraphValidation' = @('-Bundle', $BundleA)
    'Test-EncounterResolver' = @('-BundleA', $BundleA, '-BundleB', $BundleB); 'Test-Playability' = @('-BundleA', $BundleA, '-BundleB', $BundleB)
    'Test-SettlementPlacement' = @('-BundleA', $BundleA, '-BundleB', $BundleB); 'Test-WorldNavigation' = @('-BundleA', $BundleA, '-BundleB', $BundleB)
    'Test-WorldPreview' = @('-BundleA', $BundleA, '-BundleB', $BundleB); 'Test-WorldSetup' = @('-BundleA', $BundleA, '-BundleB', $BundleB)
    'Test-WorldSurface' = @('-BundleA', $BundleA, '-BundleB', $BundleB); 'Test-Recovery' = @('-BundleA', $BundleA)
    'Test-GenerationProbe' = @(); 'Test-ProducerProcess' = @(); 'Test-SourceReader' = @(); 'Test-TravelGate' = @(); 'Test-WorldBridgeSurface' = @()
    'Test-PackagePaths' = @('-Zip', $Zip)
    'Test-CampaignSoak' = @(); 'Test-LandMissions' = @(); 'Test-NavalMissions' = @()
}
$python = [ordered]@{
    'ci unit tests' = @('-m', 'unittest', 'discover', '-s', 'tests/ci')
    'mods.py check' = @('tools/ci/mods.py', 'check', '--base', 'HEAD')
    'producer lock' = @('tests/check_producer_lock.py') + $(if ($ProducerRoot) { @('--source', $ProducerRoot) } else { @() })
    'source reader' = @('tests/test_source_reader.py')
    'terrain.bin' = @('tests/test_terrain_bin.py')
    'navmesh.bin' = @('tests/test_navmesh_bin.py')
}
# tests/WorldBridgeSurfaceTests.py takes the bridge, a template and an output; Test-WorldBridgeSurface runs it with them.
$results = [ordered]@{}
function Run-One([string]$Name, [string]$Exe, [string[]]$Arguments) {
    $start = Get-Date
    # A suite's stderr lines are output here, not errors of this runner (PowerShell 5.1 turns them into terminating errors under Stop).
    $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $out = & $Exe @Arguments 2>&1 | ForEach-Object { [string]$_ }
    $code = $LASTEXITCODE
    $ErrorActionPreference = $previous
    $results[$Name] = [ordered]@{ command = (($Exe | Split-Path -Leaf) + ' ' + ($Arguments -join ' ')); exitCode = $code; passed = ($code -eq 0)
                                  seconds = [math]::Round(((Get-Date) - $start).TotalSeconds, 1); lastLines = @($out | Select-Object -Last 4) }
    Write-Output ("{0,-30} {1} ({2} s)" -f $Name, $(if ($code -eq 0) { 'pass' } else { "FAIL exit $code" }), $results[$Name].seconds)
}
$env:MNBL_GAME_DIR = 'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord'; $env:BANNERLORD_ROOT = $env:MNBL_GAME_DIR
foreach ($s in $suites.Keys) { Run-One $s 'powershell.exe' (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot "$s.ps1")) + $suites[$s]) }
foreach ($p in $python.Keys) { Run-One $p 'python' $python[$p] }
$doc = [ordered]@{ schema = 'mnbl.offline-suites'; version = 1; ticket = 'MMR-1'; utc = [DateTime]::UtcNow.ToString('O'); commit = [string](& git rev-parse HEAD)
                   bundles = [ordered]@{ A = (Split-Path $BundleA -Leaf); B = (Split-Path $BundleB -Leaf) }
                   notRun = [ordered]@{ 'Test-GenerationIdentity' = 'rewrites the embedded worldgen/contracts/producer-seed-width.json (a build input); MMR-24 evidence stands' }
                   passed = @($results.Values | Where-Object { $_.passed }).Count; total = $results.Count; suites = $results }
$json = $doc | ConvertTo-Json -Depth 6
$json = $json -replace '(?i)C:\\\\Users\\\\[^\\\\"]+', '<user>' -replace '(?i)C:/Users/[^/"]+', '<user>'
$utf8 = New-Object Text.UTF8Encoding $false
New-Item -ItemType Directory -Force build/mmr1, evidence/one-shot | Out-Null
[IO.File]::WriteAllText((Join-Path $repo 'build/mmr1/offline-suites.json'), $json, $utf8)
[IO.File]::WriteAllText((Join-Path $repo 'evidence/one-shot/offline-suites.json'), $json, $utf8)
Write-Output "offline suites: $($doc.passed)/$($doc.total) passed"
