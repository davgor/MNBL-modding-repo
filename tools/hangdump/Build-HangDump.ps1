param([string]$Source, [string]$OutDir)
# MMR-247 (K): compiles a .NET Framework tool source (default tools/hangdump/HangDump.cs) with Roslyn csc against the pinned framework
# assemblies (config/runtime-api-lock.json), x64, into build/hangdump/; rebuilt only when the source is newer than the exe. Prints the exe path.
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot)
if (!$Source) { $Source = Join-Path $PSScriptRoot 'HangDump.cs' }
if (!$OutDir) { $OutDir = Join-Path $repo 'build/hangdump' }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$exe = Join-Path $OutDir ([IO.Path]::GetFileNameWithoutExtension($Source) + '.exe')
if ((Test-Path -LiteralPath $exe) -and (Get-Item -LiteralPath $exe).LastWriteTimeUtc -ge (Get-Item -LiteralPath $Source).LastWriteTimeUtc) { return $exe }
$csc = 'C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$lock = Get-Content (Join-Path $repo 'config/runtime-api-lock.json') -Raw | ConvertFrom-Json
$references = @()
foreach ($name in @('mscorlib', 'System', 'System.Core')) {
    $pinned = @($lock.assemblies | Where-Object { $_.scope -eq 'framework' -and $_.name -eq $name })
    if ($pinned.Count -ne 1) { throw "Missing unique framework pin: $name" }
    $path = Join-Path $lock.frameworkRoot $pinned[0].path
    if ((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pinned[0].sha256) { throw "Framework pin mismatch: $path" }
    $references += "/reference:$path"
}
$saved = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
try { $log = @(& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /optimize+ /warnaserror "/out:$exe" @references $Source 2>&1 | ForEach-Object { "$_" }); $code = $LASTEXITCODE }
finally { $ErrorActionPreference = $saved }
if ($code -ne 0) { throw "csc failed on $Source`n$($log -join "`n")" }
return $exe
