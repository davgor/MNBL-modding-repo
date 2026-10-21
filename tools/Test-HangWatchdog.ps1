param()
# MMR-247: compiles the probe's engine-thread hang capture (Automation/HangWatchdog.cs) with tests/Runtime/HangWatchdogTests.cs against the
# pinned framework assemblies and runs it: the stall rule on a test clock, and real captures (text and minidump) of a thread blocked in a
# managed wait and in native code. No game process.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$out=Join-Path $repo ('build/hang-watchdog247/test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$sources=@('src/MNBL.ProceduralWorld/Automation/HangWatchdog.cs','tests/Runtime/HangWatchdogTests.cs')
$lock=Get-Content "$repo/config/runtime-api-lock.json" -Raw|ConvertFrom-Json
$references=@()
foreach($name in @('mscorlib','netstandard','System','System.Core')){
    $pinned=@($lock.assemblies|Where-Object { $_.scope -eq 'framework' -and $_.name -eq $name })
    if($pinned.Count -ne 1){throw "Missing unique framework pin: $name"}
    $path=Join-Path $lock.frameworkRoot $pinned[0].path
    if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $pinned[0].sha256){throw "Framework pin mismatch: $path"}
    $references+="/reference:$path"
}
$exe=Join-Path $out 'HangWatchdogTests.exe'
& $csc /nologo /noconfig /nostdlib+ /target:exe /platform:x64 /langversion:7.3 /warnaserror "/out:$exe" @references @($sources|ForEach-Object { Join-Path $repo $_ })
if($LASTEXITCODE){throw 'Hang watchdog harness compilation failed'}
& $exe (Join-Path $out 'captures')
if($LASTEXITCODE){throw 'Hang watchdog tests failed'}
Write-Output "Hang watchdog captures: $(Join-Path $out 'captures')"
