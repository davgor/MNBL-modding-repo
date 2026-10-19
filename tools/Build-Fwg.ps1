param([string]$Source = 'C:\Users\davgo\Documents\GitHub\FWGC++')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$cmakeBin = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin'
Push-Location $repo
try {
    & "$cmakeBin/cmake.exe" -S "$Source/generator" -B build/producer -G 'Visual Studio 17 2022' -A x64
    if ($LASTEXITCODE) { throw 'Producer configure failed' }
    & "$cmakeBin/cmake.exe" --build build/producer --config Release --parallel 8
    if ($LASTEXITCODE) { throw 'Producer build failed' }
    & "$cmakeBin/ctest.exe" --test-dir build/producer -C Release --no-tests=error --output-on-failure
    if ($LASTEXITCODE) { throw 'Producer tests failed' }
} finally { Pop-Location }
