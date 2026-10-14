param(
    [string]$GameRoot = 'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord',
    # Each entry: <source folder relative to repo>=<destination relative to the module folder>
    [string[]]$Map = @('build/slots/mnbl_world_a=SceneObj/mnbl_world_a', 'build/slots/mnbl_world_b=SceneObj/mnbl_world_b',
                       'build/slots/staging43/mnbl_world_b=SlotStaging/mnbl_world_b_s43')
)
# Copies generated scene slots into the owned module only. Slot names must exist before the game starts
# (scene names are indexed at startup). Refuses redirected paths and anything outside Modules/MNBL.ProceduralWorld.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
if (Get-Process -Name Bannerlord -ErrorAction SilentlyContinue) { throw 'Bannerlord is running; slot files may be locked.' }
$module = [IO.Path]::GetFullPath((Join-Path $GameRoot 'Modules/MNBL.ProceduralWorld'))
if (!(Test-Path -LiteralPath (Join-Path $module 'SubModule.xml'))) { throw "Deploy the module first: $module" }
$manifest = foreach ($entry in $Map) {
    $source, $relative = $entry.Split('=', 2)
    $from = [IO.Path]::GetFullPath((Join-Path $repo $source))
    $to = [IO.Path]::GetFullPath((Join-Path $module $relative))
    if (!$to.StartsWith($module + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Destination escapes module: $relative" }
    if ((Split-Path $to -Leaf) -notlike 'mnbl_*') { throw "Slot folder must be named mnbl_*: $relative" }
    foreach ($p in @($module, (Split-Path $to -Parent), $to)) {
        if ((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Redirected path: $p" }
    }
    New-Item -ItemType Directory -Force -Path $to | Out-Null
    Get-ChildItem -LiteralPath $to -File | Remove-Item -Force
    Get-ChildItem -LiteralPath $from -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $to $_.Name) -Force
        [ordered]@{ slot = $relative; file = $_.Name; sha256 = (Get-FileHash -LiteralPath (Join-Path $to $_.Name)).Hash.ToLowerInvariant() }
    }
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 (Join-Path $module 'slots.json')
"Deployed $(@($manifest).Count) slot files to $module"
