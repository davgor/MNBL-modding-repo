param([string]$GameRoot = 'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord')
$ErrorActionPreference = 'Stop'
if (Get-Process Bannerlord,Bannerlord.Native -ErrorAction SilentlyContinue) { throw 'Close the game before indexing the owned runtime slot.' }
$repo = Split-Path $PSScriptRoot
$module = [IO.Path]::GetFullPath((Join-Path $GameRoot 'Modules/MNBL.ProceduralWorld'))
# MMR-255: the tracked template, materialized from the installed official scene (python tools/scenebin/make_surface_template.py --materialize build/surface-template).
$source = [IO.Path]::GetFullPath((Join-Path $repo 'build/surface-template'))
if (!(Test-Path -LiteralPath (Join-Path $source 'scene.xscene'))) { throw 'Materialize the surface template first: python tools/scenebin/make_surface_template.py --materialize build/surface-template' }
if (!(Test-Path -LiteralPath (Join-Path $module 'SubModule.xml'))) { throw 'Deploy the diagnostic module first.' }
function Assert-PlainSurfacePath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Redirected path: $current" }
        $parent = Split-Path $current -Parent
        if ($parent -eq $current) { break }
        $current = $parent
    }
}
Assert-PlainSurfacePath $source
$files = @('scene.xscene','terrain.bin','navmesh.bin','flora.bin','atmosphere.xml')
$receipts = foreach ($relative in @('SceneObj/mnbl_runtime_surface','SurfaceTemplate')) {
    $target = [IO.Path]::GetFullPath((Join-Path $module $relative))
    if (!$target.StartsWith($module + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Destination escapes owned module.' }
    Assert-PlainSurfacePath $target
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    foreach ($file in $files) {
        $inputPath = Join-Path $source $file
        $outputPath = Join-Path $target $file
        Assert-PlainSurfacePath $inputPath
        Assert-PlainSurfacePath $outputPath
        if ($file -eq 'scene.xscene') {
            $xml = [IO.File]::ReadAllText($inputPath)
            $xml = $xml.Replace('<scene name="mnbl_world_b"', '<scene name="mnbl_runtime_surface"')
            [IO.File]::WriteAllText($outputPath,$xml,[Text.UTF8Encoding]::new($false))
        } else { Copy-Item -LiteralPath $inputPath -Destination $outputPath -Force }
        [ordered]@{relative="$relative/$file";sha256=(Get-FileHash -LiteralPath $outputPath).Hash.ToLowerInvariant()}
    }
}
[ordered]@{schema='mnbl.runtime-surface-slot';version=1;utc=[DateTime]::UtcNow.ToString('O');scope='Local generic fixture template and startup scene index only. The probe must overwrite all five native files with newly computed data before every Read.';files=@($receipts)} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $module 'runtime-surface-slot.json') -Encoding utf8
'Prepared one owned runtime slot and a local fixture template.'
