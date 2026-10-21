# Shared helpers for the GitHub-hosted mod builds (tools/ci/Build-*.ps1): Visual Studio 2022 tool discovery on this
# PC (Build Tools) or a runner (Enterprise), and MSBuild arguments that compile against the reference layout written
# by `tools/ci/mods.py references` instead of an installed game.

function Get-MnblVsTool([string]$Find) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (!(Test-Path -LiteralPath $vswhere)) { throw 'vswhere.exe not found; install Visual Studio 2022 or its Build Tools.' }
    $path = @(& $vswhere -latest -products * -version '[17.0,18.0)' -find $Find)[0]
    if (!$path) { throw "Visual Studio 2022 does not provide $Find." }
    $path
}

function Get-MnblCMake {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $bundled = @(& $vswhere -latest -products * -version '[17.0,18.0)' -find 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe')[0]
    if ($bundled) { return $bundled }
    $onPath = Get-Command cmake.exe -ErrorAction SilentlyContinue
    if (!$onPath) { throw 'CMake not found; install the Visual Studio C++ CMake tools.' }
    $onPath.Source
}

# Recreates an empty build directory; refuses anything that looks like a checkout.
function Reset-MnblCiDirectory([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\','/')
    if ((Test-Path -LiteralPath (Join-Path $full '.git')) -or $full.Length -le 3) { throw "Refusing to reset $full." }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
    (New-Item -ItemType Directory -Path $full).FullName
}

# Forward slashes and a trailing slash: a trailing backslash before a closing quote breaks MSBuild's argument parsing.
function ConvertTo-MnblMsbuildPath([string]$Path) {
    [IO.Path]::GetFullPath($Path).Replace('\','/').TrimEnd('/') + '/'
}

# The Procedural World module folder as a release ships it (MMR-57): SubModule.xml, GUI, the module assembly, the pinned producer
# and the bridge with their hash pins. The release build (Build-ProceduralWorld.ps1) and the local package (tools/Package.ps1)
# both stage through this, so the two cannot drift.
function Copy-MnblProceduralWorldStage([string]$Repo, [string]$Stage, [string]$ModuleDll, [string]$Producer, [string]$Bridge, [string]$ProducerSha256) {
    $bin = (New-Item -ItemType Directory -Path "$Stage/bin/Win64_Shipping_Client" -Force).FullName
    Copy-Item -LiteralPath "$Repo/Modules/MNBL.ProceduralWorld/SubModule.xml" -Destination "$Stage/SubModule.xml"
    if (Test-Path -LiteralPath "$Repo/Modules/MNBL.ProceduralWorld/GUI") { Copy-Item -LiteralPath "$Repo/Modules/MNBL.ProceduralWorld/GUI" -Destination "$Stage/GUI" -Recurse }
    # The surface template's own files and its recipe; its official-derived files are made on the player's machine (SurfaceTemplateSource).
    Copy-Item -LiteralPath "$Repo/Modules/MNBL.ProceduralWorld/SurfaceTemplate" -Destination "$Stage/SurfaceTemplate" -Recurse
    # MMR-259: the published magic-ground packs (the sources and the editor's working packages never ship); mods.py refuses a
    # package without them.
    $packs = "$Repo/Modules/MNBL.ProceduralWorld/AssetPackages"
    if (Test-Path -LiteralPath $packs) { New-Item -ItemType Directory -Path "$Stage/AssetPackages" -Force | Out-Null; Copy-Item -Path "$packs/*.tpac" -Destination "$Stage/AssetPackages" }
    # MMR-261 (RT-37): every producer build a campaign may cast with (worldgen/producer-archive.json), at Producers/<sha256>/fwg.exe.
    $archive = Get-Content "$Repo/worldgen/producer-archive.json" -Raw | ConvertFrom-Json
    foreach ($build in @($archive.builds)) {
        $kept = "$Repo/Modules/MNBL.ProceduralWorld/$($build.path)"
        if ((Get-FileHash -LiteralPath $kept -Algorithm SHA256).Hash.ToLowerInvariant() -ne $build.sha256) { throw "Archived producer differs from its hash: $kept" }
        New-Item -ItemType Directory -Path (Split-Path "$Stage/$($build.path)") -Force | Out-Null
        Copy-Item -LiteralPath $kept -Destination "$Stage/$($build.path)"
    }
    Copy-Item -LiteralPath $ModuleDll -Destination $bin
    Copy-Item -LiteralPath $Producer -Destination "$bin/fwg.exe"
    Copy-Item -LiteralPath $Bridge -Destination "$bin/worldbridge.exe"
    # The module refuses a producer or bridge that differs from these pins (GenerationProbe.VerifyPin); same format as Deploy-Dev.ps1.
    $utf8 = New-Object Text.UTF8Encoding $false
    [IO.File]::WriteAllText("$bin/fwg.sha256", $ProducerSha256 + "`n", $utf8)
    [IO.File]::WriteAllText("$bin/worldbridge.sha256", (Get-FileHash -LiteralPath "$bin/worldbridge.exe" -Algorithm SHA256).Hash.ToLowerInvariant() + "`n", $utf8)
}

function Get-MnblReferenceArgs([string]$References, [string]$Work) {
    $refs = ConvertTo-MnblMsbuildPath $References
    $out = ConvertTo-MnblMsbuildPath $Work
    @("/p:GameRoot=${refs}game", "/p:WorkshopRoot=${refs}workshop", "/p:TargetFrameworkRootPath=${refs}framework/",
      "/p:OutputPath=${out}module/", "/p:BaseIntermediateOutputPath=${out}obj/", "/p:IntermediateOutputPath=${out}obj/Release/")
}
