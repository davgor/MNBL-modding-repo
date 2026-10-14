param([string]$Record="$PSScriptRoot/../build/owned-probe.json")
$ErrorActionPreference='Stop'
$run=Get-Content -LiteralPath $Record -Raw | ConvertFrom-Json
$processId=[int]$run.pid
$store=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MNBL.ProceduralWorld/autorun'
$dest=Join-Path $PSScriptRoot '../evidence/runtime/in-game'
$result=Get-Content (Join-Path $store "result-$processId.json") -Raw | ConvertFrom-Json
if ($result.requestId -ne $run.requestId -or $result.processId -ne $processId) { throw 'Result identity mismatch.' }
$prefix="$($run.probe)-$processId"
Copy-Item (Join-Path $store "result-$processId.json") (Join-Path $dest "$prefix.json")
Copy-Item (Join-Path $store "progress-$processId.log") (Join-Path $dest "$prefix.log")
if ($result.outputs.screenshot -and (Test-Path -LiteralPath $result.outputs.screenshot)) {
    # Copy the engine-generated image as-is. This is not a desktop capture.
    Copy-Item -LiteralPath $result.outputs.screenshot -Destination (Join-Path $dest "$prefix.bmp")
}
$captures=@()
foreach ($property in $result.outputs.PSObject.Properties) {
    if ($property.Name -notmatch '^[A-Za-z0-9_-]+Screenshot$' -or !$property.Value) { continue }
    $capture=[IO.Path]::GetFullPath([string]$property.Value)
    if (!(Test-Path -LiteralPath $capture) -or !([IO.Path]::GetDirectoryName($capture).Equals([IO.Path]::GetFullPath($store),[StringComparison]::OrdinalIgnoreCase))) {
        throw "Invalid or missing probe screenshot: $($property.Name)"
    }
    $captureDest=Join-Path $dest "$prefix-$($property.Name).bmp"
    Copy-Item -LiteralPath $capture -Destination $captureDest
    $captures += @{path=$captureDest;sha256=(Get-FileHash -LiteralPath $captureDest).Hash.ToLowerInvariant()}
}
$dialogs=@()
$dialogLog=Join-Path $PSScriptRoot "../build/probe-dialogs-$processId.log"
if (Test-Path -LiteralPath $dialogLog) {
    Copy-Item -LiteralPath $dialogLog -Destination (Join-Path $dest "$prefix.dialogs.log")
    $dialogs=@(Get-Content -LiteralPath $dialogLog | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
}
$beforePath=Join-Path $PSScriptRoot '../build/saves-before-boot.json'
$changed=@()
$before=@($run.savesBefore)
if (!$run.PSObject.Properties['savesBefore'] -and (Test-Path -LiteralPath $beforePath)) { $before=@(Get-Content $beforePath -Raw | ConvertFrom-Json) }
if ($before.Count) {
    foreach($save in $before) {
        if (!(Test-Path -LiteralPath $save.path) -or (Get-FileHash -LiteralPath $save.path).Hash -ne $save.sha256) { $changed += $save.path }
    }
}
$saveRoot=Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Mount and Blade II Bannerlord/Game Saves'
$newSaves=@(Get-ChildItem -LiteralPath $saveRoot -Filter '*.sav' -File | Where-Object { $_.FullName -notin @($before.path) } | ForEach-Object { @{path=$_.FullName;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()} })
$summary=[ordered]@{probe=$run.probe;pid=$processId;status=$result.status;final=$result.final;processStillRunning=[bool](Get-Process -Id $processId -ErrorAction SilentlyContinue);existingSavesChanged=$changed;newSaves=$newSaves;captures=$captures;dismissedDialogs=$dialogs;resultSha256=(Get-FileHash (Join-Path $dest "$prefix.json")).Hash.ToLowerInvariant()}
$summary | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $dest "$prefix-observation.json")
$summary | ConvertTo-Json -Depth 4
