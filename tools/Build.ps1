param([string]$GameRoot = 'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord')
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Test-ApiPins.ps1" -GameRoot $GameRoot
$repo = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot)).TrimEnd('\','/')
$apiInputs = @(Test-MnblApiPins $repo $GameRoot)
$sources = @(Get-MnblSourceInputs $repo)
$receiptPath = "$repo/evidence/runtime/build-api-inputs.json"
New-Item (Split-Path $receiptPath) -ItemType Directory -Force | Out-Null
# Invalidate any earlier success before invoking either compiler.
$receipt = [ordered]@{schema='mnbl.build-api-inputs';version=1;status='building';utc=[DateTime]::UtcNow.ToString('O');apiInputs=$apiInputs;sources=$sources;resolvedReferences=@();outputs=@()}
$receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $receiptPath -Encoding utf8
$scratch = Join-Path $env:TEMP ('mnbl-build-api-' + [Guid]::NewGuid().ToString('N'))
New-Item $scratch -ItemType Directory | Out-Null
$referenceList = Join-Path $scratch 'references.txt'
$referenceReport = Join-Path $scratch 'references.json'
$targetsPath = Join-Path $scratch 'VerifyApi.targets'
# The hook validates the actual ReferencePath consumed by CoreCompile, not just HintPath.
$checker = [Security.SecurityElement]::Escape("$PSScriptRoot/Test-ApiPins.ps1")
$shellPath = if (Test-Path -LiteralPath "$PSHOME/pwsh.exe") { "$PSHOME/pwsh.exe" } else { "$PSHOME/powershell.exe" }
$shellArgument = [Security.SecurityElement]::Escape($shellPath)
$gameArgument = [Security.SecurityElement]::Escape($GameRoot)
$listArgument = [Security.SecurityElement]::Escape($referenceList)
$reportArgument = [Security.SecurityElement]::Escape($referenceReport)
@"
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <Target Name="MnblVerifyCompilerInputs" BeforeTargets="CoreCompile">
    <WriteLinesToFile File="$listArgument" Lines="@(ReferencePath-&gt;'%(FullPath)|%(CopyLocal)')" Overwrite="true" />
    <Exec Command="&quot;$shellArgument&quot; -NoProfile -ExecutionPolicy Bypass -File &quot;$checker&quot; -GameRoot &quot;$gameArgument&quot; -ResolvedReferences &quot;$listArgument&quot; -ReportPath &quot;$reportArgument&quot;" />
  </Target>
</Project>
"@ | Set-Content -LiteralPath $targetsPath -Encoding utf8
$msbuild = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
& $msbuild "$PSScriptRoot/../src/MNBL.ProceduralWorld/MNBL.ProceduralWorld.csproj" /t:Rebuild /p:Configuration=Release "/p:GameRoot=$GameRoot" "/p:CustomAfterMicrosoftCommonTargets=$targetsPath" /nologo /v:minimal
if ($LASTEXITCODE -ne 0) { throw "Module build failed: $LASTEXITCODE" }
if (!(Test-Path -LiteralPath $referenceReport)) { throw 'Compiler reference verification did not execute; build is not deployable.' }
$cmake = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
& $cmake -S "$PSScriptRoot/../src/WorldBridge" -B "$PSScriptRoot/../build/worldbridge" -G 'Visual Studio 17 2022' -A x64
if ($LASTEXITCODE) { throw 'Bridge configure failed' }
& $cmake --build "$PSScriptRoot/../build/worldbridge" --config Release --clean-first
if ($LASTEXITCODE) { throw 'Bridge build failed' }
$apiAfter = @(Test-MnblApiPins $repo $GameRoot)
Assert-MnblSameInputs $apiInputs $apiAfter 'API inputs changed during build'
Assert-MnblSameInputs $sources (Get-MnblSourceInputs $repo) 'Sources changed during build'
$receipt.resolvedReferences = @(Test-MnblResolvedReferences $apiAfter (Get-Content -LiteralPath $referenceList))
$receipt.outputs = @('build/module/MNBL.ProceduralWorld.dll','build/worldbridge/Release/worldbridge.exe' | ForEach-Object {
    [pscustomobject]@{path=$_;sha256=Get-MnblHash (Join-Path $repo $_)}
})
$receipt.status = 'built'
$receipt.utc = [DateTime]::UtcNow.ToString('O')
$receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $receiptPath -Encoding utf8
Write-Output "Verified build inputs and output hashes: $receiptPath"
