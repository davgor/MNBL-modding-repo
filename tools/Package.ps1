# MMR-57: the local installable package of Procedural World, through the release pipeline's own staging (tools/ci/CiBuild.ps1
# Copy-MnblProceduralWorldStage) and packer (tools/ci/mods.py package: the zip, its .sha256, release notes and the manifest with
# every file's hash and provenance, refusing any file config/mods.json does not list or that is named like a game file). The
# inputs are this PC's verified build (tools/Build.ps1: the module compiled against the installed game, WorldBridge, the pinned
# producer), not the CI reference build. Then local audits the CI cannot run:
#   - no packaged file is byte-identical to any file of the installed game (every folder but this repository's own modules);
#   - the module assembly embeds exactly the repository's worldgen files its project lists (each resource's SHA-256), and nothing else;
#   - the packaged surface template files are the ones its recipe names;
#   - the native executables' imported DLLs (dumpbin /dependents), to state the runtime they need.
# Writes artifacts/release/ (git-ignored: the zip, its checksum, notes, manifest) and evidence/release/package-manifest.json.
param(
    [string]$GameRoot = 'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord',
    [string]$Out = 'artifacts/release'
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/ci/CiBuild.ps1"
$repo = Split-Path $PSScriptRoot
$lock = Get-Content -LiteralPath "$repo/tools.lock.json" -Raw | ConvertFrom-Json
$receipt = Get-Content -LiteralPath "$repo/evidence/runtime/build-api-inputs.json" -Raw | ConvertFrom-Json
$dll = "$repo/build/module/MNBL.ProceduralWorld.dll"; $producer = "$repo/build/producer/apps/Release/fwg.exe"; $bridge = "$repo/build/worldbridge/Release/worldbridge.exe"
foreach ($path in @($dll, $producer, $bridge)) { if (!(Test-Path -LiteralPath $path)) { throw "Missing $path; run tools/Build.ps1 first." } }
function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
if ((Sha $producer) -ne $lock.producer.sha256) { throw 'The built producer differs from tools.lock.json.' }
# The build receipt must name these exact outputs (tools/Build.ps1 verified them).
$built = @($receipt.outputs | ForEach-Object { $_.sha256 })
foreach ($path in @($dll, $bridge)) { if ($built -notcontains (Sha $path)) { throw "$path is not the output tools/Build.ps1 recorded; rebuild." } }

$stage = Reset-MnblCiDirectory "$repo/build/release/stage/MNBL.ProceduralWorld"
Copy-MnblProceduralWorldStage $repo $stage $dll $producer $bridge $lock.producer.sha256
$outDir = [IO.Path]::GetFullPath((Join-Path $repo $Out))
$result = python "$repo/tools/ci/mods.py" package MNBL.ProceduralWorld --stage $stage --out $outDir | Out-String
if ($LASTEXITCODE) { throw "mods.py package refused the stage: $result" }
$package = $result | ConvertFrom-Json
$manifest = Get-Content -LiteralPath $package.manifest -Raw | ConvertFrom-Json

# Local audit 1: no packaged file equals any installed game file (the whole installation, but this repository's modules MNBL.*).
$own = @(Get-ChildItem -LiteralPath (Join-Path $GameRoot 'Modules') -Directory | Where-Object { $_.Name -like 'MNBL.*' } | ForEach-Object { $_.FullName.TrimEnd('\') + '\' })
$officialRoots = @(Get-ChildItem -LiteralPath $GameRoot -Directory | Where-Object { $_.Name -ne 'Modules' } | ForEach-Object { $_.FullName })
$officialRoots += @(Get-ChildItem -LiteralPath (Join-Path $GameRoot 'Modules') -Directory | Where-Object { $_.Name -notlike 'MNBL.*' } | ForEach-Object { $_.FullName })
$packaged = @{}; foreach ($f in $manifest.files) { $packaged[$f.sha256] = $f.path }
$sizes = @{}; foreach ($f in $manifest.files) { $sizes[[long]$f.bytes] = $true }
$identical = @(); $scanned = 0
$rootFiles = @(Get-ChildItem -LiteralPath $GameRoot -File)
foreach ($file in @($rootFiles) + @($officialRoots | ForEach-Object { Get-ChildItem -LiteralPath $_ -Recurse -File -ErrorAction SilentlyContinue })) {
    $scanned++
    if (!$sizes.ContainsKey([long]$file.Length)) { continue }   # only same-size files can be identical
    $h = Sha $file.FullName
    if ($packaged.ContainsKey($h)) { $identical += @{packaged=$packaged[$h]; official=$file.FullName.Substring($GameRoot.Length).TrimStart('\','/')} }
}
# Local audit 2: the assembly's embedded worldgen files are the repository's (by resource name and SHA-256).
$project = [xml](Get-Content -LiteralPath "$repo/src/MNBL.ProceduralWorld/MNBL.ProceduralWorld.csproj" -Raw)
$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($dll))
$embedded = @(); $embeddedProblems = @()
foreach ($item in @($project.Project.ItemGroup.EmbeddedResource | Where-Object { $_ })) {
    $source = [IO.Path]::GetFullPath((Join-Path "$repo/src/MNBL.ProceduralWorld" $item.Include))
    $name = $item.LogicalName
    $stream = $assembly.GetManifestResourceStream($name)
    if ($null -eq $stream) { $embeddedProblems += "$name is not embedded"; continue }
    $sha = [Security.Cryptography.SHA256]::Create(); $hash = ($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') }) -join ''; $stream.Dispose()
    $repoHash = if (Test-Path -LiteralPath $source) { Sha $source } else { $null }
    $embedded += @{resource=$name; source=$source.Substring($repo.Length + 1).Replace('\','/'); sha256=$hash; matchesRepository=($hash -eq $repoHash)}
    if ($hash -ne $repoHash) { $embeddedProblems += "$name differs from $($item.Include)" }
}
if ($embedded.Count -eq 0) { $embeddedProblems += 'the project lists no embedded resource' }
$listed = @($embedded | ForEach-Object { $_.resource })
$extra = @($assembly.GetManifestResourceNames() | Where-Object { $listed -notcontains $_ -and $_ -notlike '*.resources' })
if ($extra.Count -gt 0) { $embeddedProblems += "embedded but not listed: $($extra -join ', ')" }
# Local audit 3: the packaged surface template files are the recipe's.
$recipe = Get-Content -LiteralPath "$stage/SurfaceTemplate/template-recipe.json" -Raw | ConvertFrom-Json
$templateProblems = @()
foreach ($name in @('terrain.bin','navmesh.bin')) { if ((Sha "$stage/SurfaceTemplate/$name") -ne $recipe.expected.$name) { $templateProblems += "SurfaceTemplate/$name differs from its recipe" } }
foreach ($name in @('scene.xscene','atmosphere.xml','flora.bin')) { if (Test-Path -LiteralPath "$stage/SurfaceTemplate/$name") { $templateProblems += "SurfaceTemplate/$name is made on the player's machine and must not ship" } }
# Local audit 4: what the native executables import (the runtime a player needs).
$dumpbin = Get-MnblVsTool 'VC\Tools\MSVC\*\bin\Hostx64\x64\dumpbin.exe'
$imports = [ordered]@{}
foreach ($exe in @('worldbridge.exe','fwg.exe')) {
    $lines = & $dumpbin /dependents "$stage/bin/Win64_Shipping_Client/$exe"
    $imports[$exe] = @($lines | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^[A-Za-z0-9_.-]+\.dll$' } | Sort-Object -Unique)
}
$record = [ordered]@{
    schema = 'mnbl.package-record'; version = 1; ticket = 'MMR-57'; utc = [DateTime]::UtcNow.ToString('O')
    archive = @{ file = Split-Path $package.zip -Leaf; sha256 = $manifest.archive.sha256; bytes = (Get-Item -LiteralPath $package.zip).Length; files = $package.files }
    manifest = $manifest
    localAudit = [ordered]@{ officialRootsScanned = @($officialRoots | ForEach-Object { $_.Substring($GameRoot.Length).TrimStart('\','/') }); officialFilesScanned = $scanned; identicalToOfficial = $identical
                             embeddedResources = $embedded; embeddedProblems = $embeddedProblems; templateProblems = $templateProblems; nativeImports = $imports
                             build = @{ receipt = 'evidence/runtime/build-api-inputs.json'; receiptSha256 = (Sha "$repo/evidence/runtime/build-api-inputs.json") } }
}
$evidence = "$repo/evidence/release/package-manifest.json"
New-Item -ItemType Directory -Force (Split-Path $evidence) | Out-Null
$utf8 = New-Object Text.UTF8Encoding $false
[IO.File]::WriteAllText($evidence, ($record | ConvertTo-Json -Depth 12), $utf8)
Copy-Item -LiteralPath $package.manifest -Destination (Join-Path $outDir 'package-manifest.json') -Force
if ($identical.Count -gt 0) { throw "Packaged files equal installed official files: $(($identical | ForEach-Object { $_.packaged + ' = ' + $_.official }) -join '; ')" }
if ($embeddedProblems.Count -gt 0) { throw "Embedded resources differ from the repository: $($embeddedProblems -join '; ')" }
if ($templateProblems.Count -gt 0) { throw "Surface template: $($templateProblems -join '; ')" }
Write-Output "Package $($package.zip) ($($package.files) files, sha256 $($manifest.archive.sha256)); manifest $evidence"
