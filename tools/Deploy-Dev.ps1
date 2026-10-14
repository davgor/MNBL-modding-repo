param(
    [string]$GameRoot = 'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord',
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Test-ApiPins.ps1" -GameRoot $GameRoot
$repo = Split-Path $PSScriptRoot
$root = [IO.Path]::GetFullPath($GameRoot).TrimEnd('\','/')
$target = [IO.Path]::GetFullPath((Join-Path $root 'Modules/MNBL.ProceduralWorld'))
if (!$target.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Module destination escapes game root.' }
function Assert-PlainPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Redirected deployment path: $current" }
        }
        $parent = Split-Path $current -Parent
        if ($parent -eq $current) { break }
        $current = $parent
    }
}
Assert-PlainPath $target
$apiInputs = @(Test-MnblApiPins $repo $root)
$buildReceipt = Test-MnblBuildReceipt $repo $apiInputs
$lock = Get-Content (Join-Path $repo 'tools.lock.json') -Raw | ConvertFrom-Json
$officialBefore = @()
foreach ($id in @('Native','SandBoxCore','SandBox','StoryMode','NavalDLC')) {
    $descriptor = Join-Path $root "Modules/$id/SubModule.xml"
    $xml = [xml](Get-Content -LiteralPath $descriptor -Raw)
    $required = if ($id -eq 'NavalDLC') { $lock.game.navalVersion } else { $lock.game.version }
    $required = ($required.Split('.') | Select-Object -First 3) -join '.'
    if ($xml.Module.Version.value -ne $required) { throw "Installed $id version differs from tested lock: $($xml.Module.Version.value)" }
    $officialBefore += @{path=$descriptor; sha256=(Get-FileHash -LiteralPath $descriptor).Hash.ToLowerInvariant()}
}
$files = @(
    @{source='Modules/MNBL.ProceduralWorld/SubModule.xml'; relative='SubModule.xml'},
    @{source='build/module/MNBL.ProceduralWorld.dll'; relative='bin/Win64_Shipping_Client/MNBL.ProceduralWorld.dll'},
    @{source='build/producer/apps/Release/fwg.exe'; relative='bin/Win64_Shipping_Client/fwg.exe'},
    @{source='build/worldbridge/Release/worldbridge.exe'; relative='bin/Win64_Shipping_Client/worldbridge.exe'}
)
$manifest = @($files | ForEach-Object {
    $source = Join-Path $repo $_.source
    $destination = [IO.Path]::GetFullPath((Join-Path $target $_.relative))
    if (!$destination.StartsWith($target + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'File escapes module directory.' }
    Assert-PlainPath $destination
    @{source=$source; destination=$destination; relative=$_.relative; sha256=(Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant()}
})
if ($manifest[2].sha256 -ne $lock.producer.sha256) { throw 'Producer differs from pinned binary.' }
$pinPath = Join-Path $target 'bin/Win64_Shipping_Client/fwg.sha256'
$bridgePinPath = Join-Path $target 'bin/Win64_Shipping_Client/worldbridge.sha256'
$receiptPath = Join-Path $target 'deployment.json'
Assert-PlainPath $pinPath
Assert-PlainPath $bridgePinPath
Assert-PlainPath $receiptPath
$receipt = [ordered]@{schema='mnbl.deployment';version=1;utc=[DateTime]::UtcNow.ToString('O');dryRun=[bool]$DryRun;target=$target;files=$manifest;officialDescriptors=$officialBefore;buildInputsSha256=(Get-MnblHash "$repo/evidence/runtime/build-api-inputs.json")}
if ($DryRun) { $receipt | ConvertTo-Json -Depth 8; return }
if (Get-Process -Name Bannerlord,Bannerlord.Native -ErrorAction SilentlyContinue) { throw 'Close Bannerlord before deploying its module.' }
foreach ($file in $manifest) {
    New-Item (Split-Path $file.destination) -ItemType Directory -Force | Out-Null
    Copy-Item -LiteralPath $file.source -Destination $file.destination
    if ((Get-FileHash -LiteralPath $file.destination).Hash.ToLowerInvariant() -ne $file.sha256) { throw "Copy verification failed: $($file.relative)" }
}
[IO.File]::WriteAllText($pinPath, $lock.producer.sha256 + "`n", [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($bridgePinPath, $manifest[3].sha256 + "`n", [Text.UTF8Encoding]::new($false))
foreach ($official in $officialBefore) {
    if ((Get-FileHash -LiteralPath $official.path).Hash.ToLowerInvariant() -ne $official.sha256) { throw "Official descriptor changed during deployment: $($official.path)" }
}
$receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $receiptPath -Encoding utf8
Write-Output "Installed and hash-verified diagnostic module at $target"
