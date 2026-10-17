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

function Get-MnblReferenceArgs([string]$References, [string]$Work) {
    $refs = ConvertTo-MnblMsbuildPath $References
    $out = ConvertTo-MnblMsbuildPath $Work
    @("/p:GameRoot=${refs}game", "/p:WorkshopRoot=${refs}workshop", "/p:TargetFrameworkRootPath=${refs}framework/",
      "/p:OutputPath=${out}module/", "/p:BaseIntermediateOutputPath=${out}obj/", "/p:IntermediateOutputPath=${out}obj/Release/")
}
