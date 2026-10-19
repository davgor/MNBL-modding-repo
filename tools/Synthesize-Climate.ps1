param([Parameter(Mandatory=$true)][string]$Placement, [Parameter(Mandatory=$true)][string]$WorldJson, [Parameter(Mandatory=$true)][string]$OutDir)
# MMR-39: synthesizes a periodic fixture's climate from its source world (GeneratedClimateSystem; worldgen/policies/climate-flow.json)
# and writes it next to a copy of the placement (tools/climate/SynthesizeClimate.cs). Deterministic: the same placement and source give
# the same files and identity.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$build=Join-Path $repo ('build/climate-tool/'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $build | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('tools/climate/SynthesizeClimate.cs','src/MNBL.ProceduralWorld/World/GeneratedClimateSystem.cs','src/MNBL.ProceduralWorld/World/PeriodicWorldCoordinates.cs',
    'src/MNBL.ProceduralWorld/World/PeriodicDistanceModel.cs','src/MNBL.ProceduralWorld/World/WorldSurface.cs','src/MNBL.ProceduralWorld/World/WorldLifetime.cs',
    'src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs')
$lock=Get-Content "$repo/config/runtime-api-lock.json" -Raw|ConvertFrom-Json
$references=@()
foreach($name in @('mscorlib','netstandard','System','System.Core','System.Xml','System.Web.Extensions')){
    $pinned=@($lock.assemblies|Where-Object { $_.scope -eq 'framework' -and $_.name -eq $name })
    if($pinned.Count -ne 1){throw "Missing unique framework pin: $name"}
    $path=Join-Path $lock.frameworkRoot $pinned[0].path
    if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pinned[0].sha256){throw "Framework pin mismatch: $path"}
    $references+="/reference:$path"
}
$exe=Join-Path $build 'SynthesizeClimate.exe'
& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ })
if($LASTEXITCODE){throw 'Climate tool compilation failed'}
& $exe (Resolve-Path -LiteralPath $Placement).Path (Resolve-Path -LiteralPath $WorldJson).Path ([IO.Path]::GetFullPath($OutDir))
if($LASTEXITCODE){throw 'Climate synthesis failed'}
