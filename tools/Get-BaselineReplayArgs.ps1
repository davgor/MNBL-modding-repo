param(
    [string]$ResultPath = "$PSScriptRoot/../evidence/runtime/in-game/baseline_combat-27112.json",
    [string]$SnapshotPath = "$PSScriptRoot/../build/baseline-combat-27112.json"
)
$ErrorActionPreference = 'Stop'

# Pure conversion is also callable after dot-sourcing, for read-only validation tests.
function ConvertTo-BaselineReplayArguments {
    param($Result, $Snapshot, [string]$SourceSha256)
    if ($Result.probe -ne 'baseline_combat' -or $Result.status -ne 'ok' -or $Result.final -ne $true) {
        throw 'Replay requires a final successful baseline_combat result.'
    }
    if ($Snapshot.pid -ne $Result.processId -or $Snapshot.requestId -ne $Result.requestId -or
        $Snapshot.probe -ne $Result.probe -or $Snapshot.args.saveName -ne $Result.outputs.saveName) {
        throw 'Prelaunch snapshot does not match the result PID, request, probe and save.'
    }
    $out = $Result.outputs
    if ($out.saveName -notmatch '^mnbl_baseline_[0-9a-f]{32}$' -or $out.combatStepsCompleted -ne $true) {
        throw 'Missing completed diagnostic save scenario.'
    }
    $saveRecords = @($Snapshot.savesBefore | Where-Object {
        [IO.Path]::GetFileName($_.path) -ceq ($out.saveName + '.sav')
    })
    if ($saveRecords.Count -ne 1) { throw 'Snapshot must contain exactly one matching diagnostic save hash.' }
    foreach ($hash in @($SourceSha256, $Result.moduleSha256, $saveRecords[0].sha256)) {
        if ($hash -notmatch '^[0-9a-fA-F]{64}$') { throw 'Missing or malformed source/module/save SHA-256.' }
    }
    $args = [ordered]@{
        saveName = $out.saveName
        replay = $true
        replaySourceSha256 = $SourceSha256.ToLowerInvariant()
        replaySourceModuleSha256 = $Result.moduleSha256
        replayScene = $out.scene
        replaySceneModule = $out.sceneModule
        replaySaveName = $out.saveName
        replaySaveSha256 = $saveRecords[0].sha256
        replayFieldEnemyId = $out.fieldSyntheticSetup.enemy
        replayNavalEnemyId = $out.navalSyntheticSetup.enemy
        replayPortId = $out.portSyntheticSetup.settlement
        replayStartX = $out.position.x
        replayStartY = $out.position.y
        replayCampaignHours = $out.campaignHours
        replayFieldX = $out.fieldSyntheticSetup.diagnosticX
        replayFieldY = $out.fieldSyntheticSetup.diagnosticY
        replayPortGateX = $out.portSyntheticSetup.gateX
        replayPortGateY = $out.portSyntheticSetup.gateY
        replaySeaStartX = $out.seaTravelStart.x
        replaySeaStartY = $out.seaTravelStart.y
        replaySeaTargetX = $out.seaTravelStart.targetX
        replaySeaTargetY = $out.seaTravelStart.targetY
        replaySeaEncounterX = $out.seaTravelEnd.x
        replaySeaEncounterY = $out.seaTravelEnd.y
        replayNavalX = $out.navalSyntheticSetup.diagnosticX
        replayNavalY = $out.navalSyntheticSetup.diagnosticY
    }
    # A replay of a replay keeps its fixed encounter endpoint, not its measured travel endpoint.
    if ($Result.args.replay -eq $true) {
        $args.replaySeaEncounterX = $Result.args.replaySeaEncounterX
        $args.replaySeaEncounterY = $Result.args.replaySeaEncounterY
    }
    foreach ($key in @($args.Keys)) {
        if ($null -eq $args[$key] -or [string]::IsNullOrWhiteSpace([string]$args[$key])) { throw "Missing replay field: $key" }
        if ($key -cmatch '(X|Y|Hours)$') {
            $number = [Convert]::ToDouble($args[$key], [Globalization.CultureInfo]::InvariantCulture)
            if ([double]::IsNaN($number) -or [double]::IsInfinity($number) -or [Math]::Abs($number) -gt [single]::MaxValue) {
                throw "Invalid replay coordinate/time: $key"
            }
            $args[$key] = $number
        }
    }
    return $args
}

if ($MyInvocation.InvocationName -ne '.') {
    $sourceResult = Get-Content -LiteralPath $ResultPath -Raw | ConvertFrom-Json
    $sourceSnapshot = Get-Content -LiteralPath $SnapshotPath -Raw | ConvertFrom-Json
    $sourceHash = (Get-FileHash -LiteralPath $ResultPath -Algorithm SHA256).Hash
    ConvertTo-BaselineReplayArguments $sourceResult $sourceSnapshot $sourceHash | ConvertTo-Json -Depth 6 -Compress
}
