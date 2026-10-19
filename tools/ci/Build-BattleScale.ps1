# GitHub-hosted build of Battle Scale for releases (config/mods.json): core tests, the module compiled against the
# pinned reference assemblies (config/ci-references.json), and the module folder staged for tools/ci/mods.py package.
# Local development builds keep using tools/Build-BattleScale.ps1 against the installed game.
param(
    [Parameter(Mandatory=$true)][string]$References,
    [Parameter(Mandatory=$true)][string]$Stage,
    [Parameter(Mandatory=$true)][string]$Work
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/CiBuild.ps1"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$work = Reset-MnblCiDirectory $Work
$stage = Reset-MnblCiDirectory $Stage
$core = @(Get-ChildItem -LiteralPath "$repo/src/MNBL.BattleScale/Core" -Filter '*.cs' | ForEach-Object FullName)
& (Get-MnblVsTool 'MSBuild\Current\Bin\Roslyn\csc.exe') /nologo /langversion:7.3 /warnaserror+ /optimize+ /target:exe "/out:$work/CoreTests.exe" $core "$repo/tests/BattleScale/CoreTests.cs"
if ($LASTEXITCODE) { throw 'Battle Scale core test compilation failed.' }
& "$work/CoreTests.exe"
if ($LASTEXITCODE) { throw 'Battle Scale core tests failed.' }
& (Get-MnblVsTool 'MSBuild\Current\Bin\MSBuild.exe') "$repo/src/MNBL.BattleScale/MNBL.BattleScale.csproj" /t:Rebuild /p:Configuration=Release @(Get-MnblReferenceArgs $References $work) /nologo /v:minimal
if ($LASTEXITCODE) { throw 'Battle Scale module build failed.' }
$bin = (New-Item -ItemType Directory -Path "$stage/bin/Win64_Shipping_Client" -Force).FullName
Copy-Item -LiteralPath "$repo/Modules/MNBL.BattleScale/SubModule.xml" -Destination "$stage/SubModule.xml"
Copy-Item -LiteralPath "$work/module/MNBL.BattleScale.dll" -Destination $bin
Write-Output "Staged MNBL.BattleScale at $stage"
