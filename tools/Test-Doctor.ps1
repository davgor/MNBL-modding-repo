$ErrorActionPreference = 'Stop'
$root = Join-Path (Split-Path $PSScriptRoot) "build/research-editor/doctor-tests-$([Guid]::NewGuid().ToString('N'))"
$fakeGame = Join-Path $root 'steamapps/common/FakeGame'
New-Item $fakeGame -ItemType Directory -Force | Out-Null
foreach ($id in @('261550','1393600')) {
    @"
"AppState" {
"buildid" "incomplete-fixture"
"StateFlags" "6"
"BytesToDownload" "100"
"BytesDownloaded" "50"
"BytesToStage" "100"
"BytesStaged" "0"
}
"@ | Set-Content "$root/steamapps/appmanifest_$id.acf"
}
$refused = $false
try { & "$PSScriptRoot/Doctor.ps1" -GameRoot $fakeGame -Output "$root/result.json" } catch { $refused = $true }
$report = Get-Content "$root/result.json" -Raw | ConvertFrom-Json
if (!$refused -or $report.installationInventoryPassed) { throw 'Incomplete installation was accepted' }
if (!($report.failures -match 'Partial or pending') -or !($report.failures -match 'NavalDLC')) { throw 'Missing exact partial/DLC diagnostics' }
Write-Output 'PASS: incomplete Steam download and missing NavalDLC rejected with exact paths.'

# Load only the pure manifest functions; never invoke Doctor's default evidence output.
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile("$PSScriptRoot/Doctor.ps1", [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Doctor has syntax errors' }
foreach ($name in @('Read-SteamKeyValues','Get-NavalEditorReadiness')) {
    $functionAst = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    . ([scriptblock]::Create($functionAst.Extent.Text))
}
$fixture = Join-Path $root 'editor-manifests'
New-Item $fixture -ItemType Directory -Force | Out-Null
function Write-Fixture([string]$Id, [string]$Depots, [string]$Flags = '4') {
    @"
"AppState" { "appid" "$Id" "StateFlags" "$Flags"
"BytesToDownload" "1" "BytesDownloaded" "1" "BytesToStage" "1" "BytesStaged" "1"
"InstalledDepots" { $Depots }
"UserConfig" { "dlcappid" "4456490" }
}
"@ | Set-Content "$fixture/appmanifest_$Id.acf"
}
function Assert-Status([string]$Expected) {
    $result = Get-NavalEditorReadiness $fixture
    if ($result.status -ne $Expected -or $result.runtimeVerified) { throw "Expected $Expected; got $($result.status)" }
}
Assert-Status 'unknown'
Write-Fixture '261550' '"2927200" { "manifest" "123" "dlcappid" "2927200" } "4456490" { "manifest" "456" }'
Write-Fixture '1393600' '"1393601" { "manifest" "123" }'
Assert-Status 'missing' # Neither depot ID nor unrelated dlcappid field is sufficient.
Write-Fixture '261550' '"9999999" { "manifest" "123" "dlcappid" "4456490" }'
Assert-Status 'installed-depot-registration-not-runtime-verified'
Write-Fixture '261550' '"9999999" { "manifest" "123" "dlcappid" "4456490" }' '6'
Assert-Status 'unknown'
Write-Fixture '261550' '"9999999" { "manifest" "0" "dlcappid" "4456490" }'
Assert-Status 'unknown'
Write-Fixture '261550' '"261551" { "manifest" "123" }'
Write-Fixture '4456490' '"9999999" { "manifest" "123" }'
Assert-Status 'installed-depot-registration-not-runtime-verified'
Set-Content "$fixture/appmanifest_4456490.acf" '"AppState" { "appid" "4456490"'
Assert-Status 'unknown'
Write-Output 'PASS: missing, unknown, actual DLC association, unrelated/numeric-ID false positives, pending/invalid depot, standalone manifest, malformed manifest; runtime never claimed.'
