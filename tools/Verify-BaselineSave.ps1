param(
    [Parameter(Mandatory=$true)][string]$ScenarioResult,
    [Parameter(Mandatory=$true)][string]$LoadResult,
    [Parameter(Mandatory=$true)][string]$OutputPath
)
$ErrorActionPreference='Stop'
$scenario=Get-Content -LiteralPath $ScenarioResult -Raw | ConvertFrom-Json
$loaded=Get-Content -LiteralPath $LoadResult -Raw | ConvertFrom-Json
foreach ($result in @($scenario,$loaded)) {
    if (!$result.final -or $result.status -ne 'ok') { throw 'Both diagnostic runs must have completed successfully.' }
}
if ($scenario.processId -eq $loaded.processId) { throw 'Cold load requires a separate process.' }
if ($scenario.moduleSha256 -ne $loaded.moduleSha256) { throw 'Module changed between save and load.' }
$a=$scenario.outputs
$b=$loaded.outputs
if ((@($a.modules) -join "`n") -ne (@($b.modules) -join "`n")) { throw 'Loaded module order changed.' }
if (!$a.saveSucceeded -or $a.saveName -ne $b.saveName) { throw 'Save identity mismatch.' }
if ($a.scene -ne $b.scene -or $a.sceneModule -ne $b.sceneModule) { throw 'Loaded scene identity changed.' }
if ($a.vanillaRun -or $b.vanillaRun) { throw 'Instrumented runs must not be labeled vanilla.' }
if ($b.hero -ne 'MNBL Baseline') { throw 'Wrong loaded character.' }
$dx=[double]$a.savePosition.x-[double]$b.position.x
$dy=[double]$a.savePosition.y-[double]$b.position.y
$hoursDelta=[Math]::Abs([double]$a.saveCampaignHours-[double]$b.campaignHours)
if ([Math]::Sqrt($dx*$dx+$dy*$dy) -gt 0.0001 -or $hoursDelta -gt 0.0001) { throw 'Saved position/time did not round-trip.' }
$loadObservationPath=Join-Path ([IO.Path]::GetDirectoryName($LoadResult)) ([IO.Path]::GetFileNameWithoutExtension($LoadResult)+'-observation.json')
$observation=Get-Content -LiteralPath $loadObservationPath -Raw | ConvertFrom-Json
if ($observation.processStillRunning -or @($observation.existingSavesChanged).Count -or @($observation.newSaves).Count) {
    throw 'Cold-load process is running or changed save files.'
}
$scenarioObservationPath=Join-Path ([IO.Path]::GetDirectoryName($ScenarioResult)) ([IO.Path]::GetFileNameWithoutExtension($ScenarioResult)+'-observation.json')
$scenarioObservation=Get-Content -LiteralPath $scenarioObservationPath -Raw | ConvertFrom-Json
if ($scenarioObservation.processStillRunning -or @($scenarioObservation.existingSavesChanged).Count -or @($scenarioObservation.newSaves).Count -ne 1) {
    throw 'Scenario must exit with no existing save changes and exactly one new save.'
}
if ([IO.Path]::GetFileNameWithoutExtension($scenarioObservation.newSaves[0].path) -ne $a.saveName) { throw 'Unexpected new save.' }
$record=[ordered]@{
    schema='mnbl.baseline-save-roundtrip';version=1;verifiedUtc=[DateTime]::UtcNow.ToString('O')
    verified=$true;vanillaRun=$false;scenarioPid=$scenario.processId;loadPid=$loaded.processId
    moduleSha256=$scenario.moduleSha256;saveName=$a.saveName
    positionError=[Math]::Sqrt($dx*$dx+$dy*$dy);campaignHoursError=$hoursDelta
    saveSeconds=$a.saveSeconds;loadToReadySeconds=$b.loadToReadySeconds
    sourceResults=@(
        @{path=$ScenarioResult;sha256=(Get-FileHash -LiteralPath $ScenarioResult).Hash.ToLowerInvariant()},
        @{path=$LoadResult;sha256=(Get-FileHash -LiteralPath $LoadResult).Hash.ToLowerInvariant()}
    )
    scope='Instrumented official-content land/settlement/save scenario. Does not establish generated campaign persistence, naval gameplay, or the full epic.'
}
$record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath
$record | ConvertTo-Json -Depth 6
