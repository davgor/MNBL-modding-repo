param([string]$GameRoot='D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$out=Join-Path $repo 'build/element-probe-host'
$package=Join-Path $out 'package'
function Hash([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
function CheckPins($map) {
    foreach($pin in $map.PSObject.Properties) {
        $path=$pin.Name
        if (![IO.Path]::IsPathRooted($path)) { $path=Join-Path $repo $path }
        if ((Hash $path) -ne $pin.Value) { throw "Stale prerequisite: $path" }
    }
}
$nativeReceiptPath=Join-Path $repo 'evidence/engine-extension/unpublished-element-probe-offline-tests.json'
$nativeReceipt=Get-Content -LiteralPath $nativeReceiptPath -Raw | ConvertFrom-Json
CheckPins $nativeReceipt.sources
$nativeDll=Join-Path $repo 'build/agent-storage/MNBL.NativeElementProbe.dll'
if ((Hash $nativeDll) -ne $nativeReceipt.outputs.'build/agent-storage/MNBL.NativeElementProbe.dll') { throw 'Native probe DLL differs.' }
$reviewPath=Join-Path $repo 'evidence/engine-extension/native-default-element-independent-review.json'
$review=Get-Content -LiteralPath $reviewPath -Raw | ConvertFrom-Json
if ((Hash $reviewPath) -ne 'dcae1bc7d9449f9f4dbcb31b54dc48257ac23f10111b11caadda98e80ae8efd3') { throw 'Native default constructor review differs.' }
$sourceFiles=@(Get-ChildItem -LiteralPath "$repo/src/MNBL.AgentGrowthProbe/managed" -Filter '*.cs' -File | ForEach-Object FullName)+@(
    "$repo/Modules/MNBL.AgentGrowthProbe/SubModule.xml", "$repo/tests/AgentStorage/managed_element_host_tests.cs",
    $PSCommandPath, "$PSScriptRoot/Test-ElementProbeInGame.ps1", $nativeReceiptPath, $reviewPath, $nativeDll)
$sources=@($sourceFiles | ForEach-Object { [ordered]@{path=$_;sha256=(Hash $_)} })
New-Item -ItemType Directory -Path $package -Force | Out-Null
$compiler='C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
$bin=Join-Path $GameRoot 'bin/Win64_Shipping_Client'
$references=@('TaleWorlds.Library','TaleWorlds.Localization','TaleWorlds.Core','TaleWorlds.ObjectSystem','TaleWorlds.Engine','TaleWorlds.DotNet','TaleWorlds.MountAndBlade') | ForEach-Object { Join-Path $bin ($_+'.dll') }
$providerHashes=@($references+@((Join-Path $bin 'TaleWorlds.Native.dll'),(Join-Path $bin 'Bannerlord.exe')) | ForEach-Object { [ordered]@{path=$_;sha256=(Hash $_)} })
if ((Hash (Join-Path $bin 'TaleWorlds.Native.dll')) -ne '2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a') { throw 'Installed original native DLL differs; no build against a patched installation.' }
$core=@("$repo/src/MNBL.AgentGrowthProbe/managed/ProbeInterop.cs","$repo/src/MNBL.AgentGrowthProbe/managed/ProbeRequest.cs")
& $compiler /nologo /langversion:7.3 /warnaserror+ /optimize+ /platform:x64 /target:exe /r:System.Web.Extensions.dll "/out:$out/ManagedElementHostTests.exe" $core "$repo/tests/AgentStorage/managed_element_host_tests.cs"
if ($LASTEXITCODE) { throw 'Managed host tests failed to compile.' }
$testOutput=& "$out/ManagedElementHostTests.exe" $nativeDll (Hash $nativeDll) 2>&1
if ($LASTEXITCODE) { throw "Managed host tests failed: $testOutput" }
$refArgs=@($references | ForEach-Object { '/r:'+$_ })
& $compiler /nologo /langversion:7.3 /warnaserror+ /optimize+ /deterministic+ /platform:x64 /target:library /r:System.Web.Extensions.dll /r:netstandard.dll $refArgs "/out:$package/MNBL.AgentGrowthProbe.dll" $core "$repo/src/MNBL.AgentGrowthProbe/managed/SubModule.cs"
if ($LASTEXITCODE) { throw 'Managed host failed to compile.' }
Copy-Item -LiteralPath $nativeDll -Destination "$package/MNBL.NativeElementProbe.dll"
Copy-Item -LiteralPath "$repo/Modules/MNBL.AgentGrowthProbe/SubModule.xml" -Destination "$package/SubModule.xml"
foreach($source in $sources+$providerHashes) { if ((Hash $source.path) -ne $source.sha256) { throw "Input changed: $($source.path)" } }
$profile=[ordered]@{schemaVersion=1;status='offline-built-private-element-probe';gameRoot=[IO.Path]::GetFullPath($GameRoot);sources=$sources;providers=$providerHashes;
    files=@('MNBL.AgentGrowthProbe.dll','MNBL.NativeElementProbe.dll','SubModule.xml') | ForEach-Object { [ordered]@{name=$_;sha256=(Hash (Join-Path $package $_))} };
    environmentKey='MNBL_PRIVATE_ELEMENT_PROBE_REQUEST';modules='_MODULES_*Native*SandBoxCore*CustomBattle*MNBL.AgentGrowthProbe*_MODULES_';liveApproval=$false}
$profile | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$package/element-probe-profile.json" -Encoding utf8
$receipt=[ordered]@{status='passed-managed-host-offline-build';sources=$sources;providers=$providerHashes;package=$package;profileSha256=(Hash "$package/element-probe-profile.json");testOutput=($testOutput -join "`n");
    testExecutableSha256=(Hash "$out/ManagedElementHostTests.exe");outputs=$profile.files;gameLaunched=$false;installedFilesChanged=$false;liveBindingTested=$false}
$receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$repo/evidence/engine-extension/managed-element-host-offline-tests.json" -Encoding utf8
Write-Output $testOutput
Write-Output "Built offline private probe package: $package"
