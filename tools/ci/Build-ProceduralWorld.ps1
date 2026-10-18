# GitHub-hosted build of Procedural World for releases (config/mods.json): the module compiled against the pinned
# reference assemblies (config/ci-references.json), WorldBridge with its CTest suite, and the pinned FWGC++ producer,
# staged as the module folder for tools/ci/mods.py package.
# References are checked by assembly identity against config/runtime-api-lock.json. Their bytes are reference
# assemblies, not the installed game's, so the installed-file hashes that tools/Build.ps1 and tools/Deploy-Dev.ps1
# enforce for local deployment do not apply here.
param(
    [Parameter(Mandatory=$true)][string]$References,
    [Parameter(Mandatory=$true)][string]$Stage,
    [Parameter(Mandatory=$true)][string]$Work,
    # The pinned fwg.exe. When omitted it is downloaded (gh) from this repository's fwg-producer/<commit> release.
    [string]$ProducerPath
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/CiBuild.ps1"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$work = Reset-MnblCiDirectory $Work
$stage = Reset-MnblCiDirectory $Stage
$refs = [IO.Path]::GetFullPath($References)
$pins = Get-Content -LiteralPath "$repo/config/runtime-api-lock.json" -Raw | ConvertFrom-Json
$lock = Get-Content -LiteralPath "$repo/tools.lock.json" -Raw | ConvertFrom-Json
$roots = @{ framework = "$refs/framework/.NETFramework/v4.8.1"; game = "$refs/game"; workshop = "$refs/workshop" }
foreach ($entry in @($pins.assemblies)) {
    $path = Join-Path $roots[$entry.scope] $entry.path
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Reference layout lacks pinned API $($entry.scope)/$($entry.path); update config/ci-references.json." }
    $identity = [Reflection.AssemblyName]::GetAssemblyName($path).FullName
    if ($entry.identity -and $identity -cne $entry.identity) { throw "Reference $($entry.path) is $identity; config/runtime-api-lock.json pins $($entry.identity)." }
}
& (Get-MnblVsTool 'MSBuild\Current\Bin\MSBuild.exe') "$repo/src/MNBL.ProceduralWorld/MNBL.ProceduralWorld.csproj" /t:Rebuild /p:Configuration=Release @(Get-MnblReferenceArgs $References $work) /nologo /v:minimal
if ($LASTEXITCODE) { throw 'Procedural World module build failed.' }
$cmake = Get-MnblCMake
& $cmake -S "$repo/src/WorldBridge" -B "$work/worldbridge" -G 'Visual Studio 17 2022' -A x64
if ($LASTEXITCODE) { throw 'WorldBridge configure failed.' }
& $cmake --build "$work/worldbridge" --config Release
if ($LASTEXITCODE) { throw 'WorldBridge build failed.' }
& (Join-Path (Split-Path $cmake) 'ctest.exe') --test-dir "$work/worldbridge" -C Release --no-tests=error --output-on-failure
if ($LASTEXITCODE) { throw 'WorldBridge tests failed.' }
if (!$ProducerPath) {
    $tag = 'fwg-producer/' + $lock.producer.sourceCommit.Substring(0, 12)
    $download = (New-Item -ItemType Directory -Path "$work/producer" -Force).FullName
    gh release download $tag --pattern fwg.exe --dir $download
    if ($LASTEXITCODE) { throw "Could not download fwg.exe from release $tag; publish it with tools/ci/Publish-Producer.ps1." }
    $ProducerPath = Join-Path $download 'fwg.exe'
}
$producerHash = (Get-FileHash -LiteralPath $ProducerPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($producerHash -ne $lock.producer.sha256) { throw "fwg.exe SHA256 $producerHash differs from tools.lock.json $($lock.producer.sha256)." }
Copy-MnblProceduralWorldStage $repo $stage "$work/module/MNBL.ProceduralWorld.dll" $ProducerPath "$work/worldbridge/Release/worldbridge.exe" $lock.producer.sha256
Write-Output "Staged MNBL.ProceduralWorld at $stage"
