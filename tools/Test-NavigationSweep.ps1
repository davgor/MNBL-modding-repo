# The navigation half of a seed sweep: compiles the runtime navigation builder, its validator and tests/World/NavigationSweep.cs against the
# pinned framework and game assemblies (as tools/Test-WorldNavigation.ps1 does) and runs every bundle under -Root (or the -Bundle paths)
# through the navigation build, the static plan and the validator over the managed stand-in engine, as activation runs them, then the
# playability validator's host-check and transition flags from that report (World/RuntimePlayabilityValidator.cs, with the sources it
# compiles with in tools/Test-Playability.ps1; 2026-10-19: seed 43's world passed the sweep and failed a seam flag in game). No game.
# Writes <Out>/navigation-sweep.jsonl.
param([string]$Root, [string[]]$Bundle, [string]$Out, [string]$GameRoot = 'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
if (-not $Out) { $Out = Join-Path $repo ('build/navigation-sweep/' + [Guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Force $Out | Out-Null
$paths = @()
if ($Root) { $paths += @(Get-ChildItem -LiteralPath $Root -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'DATA_READY') } | Sort-Object Name | ForEach-Object { $_.FullName }) }
if ($Bundle) { $paths += @($Bundle | ForEach-Object { (Resolve-Path -LiteralPath $_).Path }) }
if ($paths.Count -eq 0) { throw 'No bundle given (-Root or -Bundle).' }
$csc = 'C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources = @('src/MNBL.ProceduralWorld/World/RuntimeNavigationBuilder.cs', 'src/MNBL.ProceduralWorld/World/NavigationValidator.cs', 'src/MNBL.ProceduralWorld/World/NavigationFixtures.cs',
    'src/MNBL.ProceduralWorld/World/WorldSurface.cs', 'src/MNBL.ProceduralWorld/World/WorldLifetime.cs', 'src/MNBL.ProceduralWorld/World/PeriodicWorldCoordinates.cs', 'src/MNBL.ProceduralWorld/World/PeriodicDistanceModel.cs',
    'src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs', 'src/MNBL.ProceduralWorld/World/RuntimePlayabilityValidator.cs', 'src/MNBL.ProceduralWorld/World/NavigationCacheKey.cs',
    'src/MNBL.ProceduralWorld/Persistence/DurableFile.cs', 'src/MNBL.ProceduralWorld/World/SettlementPositionResolver.cs', 'src/MNBL.ProceduralWorld/World/SettlementEntityFactory.cs',
    'src/MNBL.ProceduralWorld/Campaign/CampaignIdentity.cs', 'src/MNBL.ProceduralWorld/Campaign/FactionFactory.cs', 'src/MNBL.ProceduralWorld/Campaign/CharacterTemplateResolver.cs',
    'src/MNBL.ProceduralWorld/Campaign/HeroFactory.cs', 'src/MNBL.ProceduralWorld/Campaign/SettlementFactory.cs', 'src/MNBL.ProceduralWorld/Campaign/VillageBinding.cs',
    'src/MNBL.ProceduralWorld/Missions/MissionScenes.cs', 'src/MNBL.ProceduralWorld/Campaign/RegistrationPlan.cs', 'tests/World/NavigationSweep.cs')
$lock = Get-Content "$repo/config/runtime-api-lock.json" -Raw | ConvertFrom-Json
$references = @()
foreach ($name in @('mscorlib', 'netstandard', 'System', 'System.Core', 'System.Xml', 'System.Web.Extensions')) {
    $pinned = @($lock.assemblies | Where-Object { $_.scope -eq 'framework' -and $_.name -eq $name })
    if ($pinned.Count -ne 1) { throw "Missing unique framework pin: $name" }
    $path = Join-Path $lock.frameworkRoot $pinned[0].path
    if ((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pinned[0].sha256) { throw "Framework pin mismatch: $path" }
    $references += "/reference:$path"
}
foreach ($name in @('TaleWorlds.Core')) {
    $pinned = @($lock.assemblies | Where-Object { $_.scope -eq 'game' -and $_.name -eq $name })
    $path = Join-Path $GameRoot $pinned[0].path
    if ((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pinned[0].sha256) { throw "Game assembly pin mismatch: $path" }
    $references += "/reference:$path"
}
$exe = Join-Path $Out 'NavigationSweep.exe'
& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references @($sources | ForEach-Object { Join-Path $repo $_ })
if ($LASTEXITCODE) { throw 'Navigation sweep compilation failed' }
& $exe $repo $GameRoot (Join-Path $Out 'navigation-sweep.jsonl') @paths
$failed = $LASTEXITCODE
Write-Output "navigation sweep: $(Join-Path $Out 'navigation-sweep.jsonl')"
if ($failed) { exit 1 }
