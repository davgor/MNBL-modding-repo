param([string]$GameRoot = 'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord')
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Test-ApiPins.ps1" -GameRoot $GameRoot
$repo = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot)).TrimEnd('\','/')
$officialBefore = @(Test-MnblApiPins $repo $GameRoot)
$fixture = Join-Path $env:TEMP ('mnbl-deploy-test-' + [Guid]::NewGuid().ToString('N'))
$fixtureRepo = Join-Path $fixture 'repo'
$fixtureGame = Join-Path $fixture 'non-default-game'
New-Item $fixtureRepo -ItemType Directory -Force | Out-Null

function Copy-FixtureFile([string]$Source, [string]$Destination) {
    New-Item (Split-Path $Destination) -ItemType Directory -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination
}
function Assert-Refused([scriptblock]$Action, [string]$Message, [string]$Label) {
    $refused = $false
    try { & $Action | Out-Null } catch {
        if ($_.Exception.Message -notmatch $Message) { throw }
        $refused = $true
    }
    if (!$refused) { throw "Accepted invalid fixture: $Label" }
    Write-Output "PASS: $Label"
}
function Add-FixtureByte([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Append, [IO.FileAccess]::Write)
    try { $stream.WriteByte(0) } finally { $stream.Dispose() }
}

# Every mutation below targets disposable copies, never the installed game or real outputs.
foreach ($inputFile in Get-MnblSourceInputs $repo) {
    Copy-FixtureFile (Join-Path $repo $inputFile.path) (Join-Path $fixtureRepo $inputFile.path)
}
Copy-FixtureFile "$repo/tools/Deploy-Dev.ps1" "$fixtureRepo/tools/Deploy-Dev.ps1"
foreach ($path in @('build/module/MNBL.ProceduralWorld.dll','build/worldbridge/Release/worldbridge.exe','build/producer/apps/Release/fwg.exe')) {
    Copy-FixtureFile (Join-Path $repo $path) (Join-Path $fixtureRepo $path)
}
foreach ($inputFile in $officialBefore | Where-Object { $_.scope -ne 'framework' }) {
    Copy-FixtureFile $inputFile.resolvedPath (Join-Path $fixtureGame $inputFile.path)
}
$fixtureApi = @(Test-MnblApiPins $fixtureRepo $fixtureGame)
Assert-MnblSameInputs $officialBefore $fixtureApi 'Fixture API copy'

# Synthetic receipt is fixture-only; it must never certify a production build.
$fixtureReceipt = [ordered]@{
    schema='mnbl.build-api-inputs';version=1;status='built';fixtureOnly=$true
    apiInputs=$fixtureApi;sources=@(Get-MnblSourceInputs $fixtureRepo)
    resolvedReferences=@(Test-MnblResolvedReferences $fixtureApi @($fixtureApi | Where-Object compile | ForEach-Object { "$($_.resolvedPath)|false" }))
    outputs=@('build/module/MNBL.ProceduralWorld.dll','build/worldbridge/Release/worldbridge.exe' | ForEach-Object {
        [pscustomobject]@{path=$_;sha256=Get-MnblHash (Join-Path $fixtureRepo $_)}
    })
}
$fixtureReceiptPath = "$fixtureRepo/evidence/runtime/build-api-inputs.json"
New-Item (Split-Path $fixtureReceiptPath) -ItemType Directory -Force | Out-Null
$fixtureReceipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $fixtureReceiptPath -Encoding utf8
$deploy = "$fixtureRepo/tools/Deploy-Dev.ps1"
$fixtureBefore = @(Get-ChildItem $fixture -Recurse -File | ForEach-Object { [pscustomobject]@{path=$_.FullName;sha256=Get-MnblHash $_.FullName} })
$dry = & $deploy -GameRoot $fixtureGame -DryRun | ConvertFrom-Json
$guiFiles = @(Get-ChildItem -LiteralPath "$repo/Modules/MNBL.ProceduralWorld/GUI" -Recurse -File -ErrorAction SilentlyContinue).Count
if (!$dry.dryRun -or $dry.files.Count -ne (4 + $guiFiles) -or $dry.target -ne "$fixtureGame\Modules\MNBL.ProceduralWorld" -or (Test-Path -LiteralPath $dry.target)) {
    throw 'Dry run mutated destination, ignored non-default root, or returned incorrect manifest'
}
$fixtureAfter = @(Get-ChildItem $fixture -Recurse -File | ForEach-Object { [pscustomobject]@{path=$_.FullName;sha256=Get-MnblHash $_.FullName} })
Assert-MnblSameInputs $fixtureBefore $fixtureAfter 'Dry-run filesystem'
Write-Output "PASS: non-default-root dry run is read-only and lists the four owned outputs and $guiFiles GUI prefab file(s)"

$dllRelative = 'bin/Win64_Shipping_Client/TaleWorlds.CampaignSystem.dll'
$dll = Join-Path $fixtureGame $dllRelative
Add-FixtureByte $dll
Assert-Refused { & $deploy -GameRoot $fixtureGame -DryRun } 'Pinned API bytes differ.*TaleWorlds.CampaignSystem.dll' 'mutated API DLL refused before deployment'
Assert-Refused { & "$fixtureRepo/tools/Build.ps1" -GameRoot $fixtureGame } 'Pinned API bytes differ.*TaleWorlds.CampaignSystem.dll' 'build honors non-default root and rejects mutated API before compilation'
Copy-FixtureFile (Join-Path $GameRoot $dllRelative) $dll
Move-Item -LiteralPath $dll -Destination "$dll.missing"
Assert-Refused { & $deploy -GameRoot $fixtureGame -DryRun } 'Missing pinned API file.*TaleWorlds.CampaignSystem.dll' 'missing API DLL has actionable refusal'
Move-Item -LiteralPath "$dll.missing" -Destination $dll

$descriptor = "$fixtureGame/Modules/Native/SubModule.xml"
$original = [IO.File]::ReadAllBytes($descriptor)
Add-FixtureByte $descriptor
Assert-Refused { & $deploy -GameRoot $fixtureGame -DryRun } 'Pinned API bytes differ.*SubModule.xml' 'changed official descriptor refused'
[IO.File]::WriteAllBytes($descriptor, $original)

$source = "$fixtureRepo/src/MNBL.ProceduralWorld/SubModule.cs"
Add-FixtureByte $source
Assert-Refused { & $deploy -GameRoot $fixtureGame -DryRun } 'Build source/input mismatch' 'source changed after build refused'
Copy-FixtureFile "$repo/src/MNBL.ProceduralWorld/SubModule.cs" $source
$output = "$fixtureRepo/build/module/MNBL.ProceduralWorld.dll"
Add-FixtureByte $output
Assert-Refused { & $deploy -GameRoot $fixtureGame -DryRun } 'Build output mismatch' 'module output changed after build refused'
Copy-FixtureFile "$repo/build/module/MNBL.ProceduralWorld.dll" $output
$bridge = "$fixtureRepo/build/worldbridge/Release/worldbridge.exe"
Add-FixtureByte $bridge
Assert-Refused { & $deploy -GameRoot $fixtureGame -DryRun } 'Build output mismatch' 'bridge output changed after build refused'
Copy-FixtureFile "$repo/build/worldbridge/Release/worldbridge.exe" $bridge
Move-Item -LiteralPath $fixtureReceiptPath -Destination "$fixtureReceiptPath.missing"
Assert-Refused { & $deploy -GameRoot $fixtureGame -DryRun } 'Missing verified build receipt' 'missing build provenance refused'
Move-Item -LiteralPath "$fixtureReceiptPath.missing" -Destination $fixtureReceiptPath

$referenceLines = @($fixtureApi | Where-Object compile | ForEach-Object { "$($_.resolvedPath)|false" })
$badLines = @($referenceLines)
$badLines[0] = $badLines[0].Replace('|false','|true')
Assert-Refused { Test-MnblResolvedReferences $fixtureApi $badLines } 'Unpinned or copy-local compiler reference' 'copy-local proprietary/framework reference refused'
$badLines = @($referenceLines)
$badLines[0] = "$fixture/outside.dll|false"
Assert-Refused { Test-MnblResolvedReferences $fixtureApi $badLines } 'Unpinned or copy-local compiler reference' 'compiler reference outside pinned roots refused'
Assert-Refused { Test-MnblResolvedReferences $fixtureApi $referenceLines[1..($referenceLines.Count-1)] } 'Resolved compiler API mismatch' 'missing resolved compiler reference refused'

New-Item "$fixture/redirect" -ItemType Directory | Out-Null
New-Item "$fixtureGame/Modules/MNBL.ProceduralWorld" -ItemType Junction -Target "$fixture/redirect" | Out-Null
Assert-Refused { & $deploy -GameRoot $fixtureGame -DryRun } 'Redirected deployment path' 'junction destination refused before copy'
Assert-MnblSameInputs $officialBefore @(Test-MnblApiPins $repo $GameRoot) 'Installed API after tests'
Write-Output 'PASS: installed API and official descriptors unchanged'
Write-Output "Fixture retained for inspection: $fixture"
