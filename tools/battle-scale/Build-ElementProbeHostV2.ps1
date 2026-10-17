# Builds the separate v2 private probe package. The frozen v1 host, package,
# runner and receipts are untouched; v2 reuses the frozen ProbeRequest/ProbeInterop
# sources unchanged and the independently reviewed relocated native DLL.
param([string]$GameRoot='D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$out=Join-Path $repo 'build/element-probe-host-v2'
$package=Join-Path $out 'package'
$originalNative='2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
function Hash([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Require([string]$path,[string]$sha,[string]$what) { if ((Hash $path) -ne $sha) { throw "$what differs: $path" } }

$nativeDll=Join-Path $repo 'build/agent-storage/MNBL.RelocatedNativeElementProbe.dll'
$nativeSha='9d1f6a512dd63c833d44a4fd0bf0e7f5ede9750dcb022ef50608144306347d05'
$reviewPath=Join-Path $repo 'evidence/engine-extension/native-relocated-binding-independent-review.json'
$contractPath=Join-Path $repo 'evidence/engine-extension/native-original-watchdog-shutdown-contract.json'
$contractSha='ce986872594d4ae57c00637f5d84009b3a6b75df08fd6f7a5c1a8e942b0dc295'
Require $nativeDll $nativeSha 'Relocated native probe DLL'
Require $reviewPath 'b027c74bcf573639f833917e23570c81dfbc819c83f0b215fd9e85792bca7962' 'Relocated binding independent review'
Require $contractPath $contractSha 'Stock watchdog shutdown contract'
$review=Get-Content -LiteralPath $reviewPath -Raw | ConvertFrom-Json
if ($review.status -ne 'accepted-bounded-relocated-native-binding-offline' -or $review.outputs.'build/agent-storage/MNBL.RelocatedNativeElementProbe.dll' -ne $nativeSha) { throw 'Review does not accept this DLL.' }
foreach($pin in $review.sources.PSObject.Properties) { Require $pin.Name $pin.Value 'Reviewed relocated binding source' }
$contract=Get-Content -LiteralPath $contractPath -Raw | ConvertFrom-Json
if ($contract.nativeSourceSha256 -ne $originalNative -or $contract.patchedCandidateSha256 -ne $originalNative) { throw 'Contract is not the stock-build contract.' }

$bin=Join-Path $GameRoot 'bin/Win64_Shipping_Client'
Require (Join-Path $bin 'TaleWorlds.Native.dll') $originalNative 'Installed native DLL (no build against a patched installation)'
Require (Join-Path $bin 'Watchdog/Watchdog.exe') $contract.watchdogSha256 'Installed watchdog'

$managed="$repo/src/MNBL.AgentGrowthProbe/managed"
$core=@("$managed/ProbeInterop.cs","$managed/ProbeRequest.cs")
$host2="$repo/src/MNBL.AgentGrowthProbe/managed-v2/SubModule.cs"
$sourceFiles=$core+@($host2,"$repo/Modules/MNBL.AgentGrowthProbe/SubModule.xml","$repo/tests/AgentStorage/managed_element_host_tests.cs",
    $PSCommandPath,"$PSScriptRoot/Test-ElementProbeInGameV2.ps1","$PSScriptRoot/Test-NativeExitContract.ps1",
    "$PSScriptRoot/audit_watchdog_exit_contract_original.py",$contractPath,$reviewPath,$nativeDll)
$sources=@($sourceFiles | ForEach-Object { [ordered]@{path=[IO.Path]::GetFullPath($_);sha256=(Hash $_)} })
$references=@('TaleWorlds.Library','TaleWorlds.Localization','TaleWorlds.Core','TaleWorlds.ObjectSystem','TaleWorlds.Engine','TaleWorlds.DotNet','TaleWorlds.MountAndBlade') | ForEach-Object { Join-Path $bin ($_+'.dll') }
$providerHashes=@($references+@((Join-Path $bin 'TaleWorlds.Native.dll'),(Join-Path $bin 'Bannerlord.exe'),(Join-Path $bin 'Watchdog/Watchdog.exe')) | ForEach-Object { [ordered]@{path=[IO.Path]::GetFullPath($_);sha256=(Hash $_)} })

New-Item -ItemType Directory -Path $package -Force | Out-Null
$compiler='C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
# Unchanged frozen managed tests, now against the relocated DLL: actual no-engine interop rejection.
& $compiler /nologo /langversion:7.3 /warnaserror+ /optimize+ /platform:x64 /target:exe /r:System.Web.Extensions.dll "/out:$out/ManagedElementHostTests.exe" $core "$repo/tests/AgentStorage/managed_element_host_tests.cs"
if ($LASTEXITCODE) { throw 'Managed host tests failed to compile.' }
$testOutput=& "$out/ManagedElementHostTests.exe" $nativeDll $nativeSha 2>&1
if ($LASTEXITCODE) { throw "Managed host tests failed: $testOutput" }
$refArgs=@($references | ForEach-Object { '/r:'+$_ })
& $compiler /nologo /langversion:7.3 /warnaserror+ /optimize+ /deterministic+ /platform:x64 /target:library /r:System.Web.Extensions.dll /r:netstandard.dll $refArgs "/out:$package/MNBL.AgentGrowthProbe.dll" $core $host2
if ($LASTEXITCODE) { throw 'Managed v2 host failed to compile.' }
# Deployed under the name the frozen ProbeRequest requires; identity is pinned by hash.
Copy-Item -LiteralPath $nativeDll -Destination "$package/MNBL.NativeElementProbe.dll" -Force
Copy-Item -LiteralPath "$repo/Modules/MNBL.AgentGrowthProbe/SubModule.xml" -Destination "$package/SubModule.xml" -Force
foreach($source in $sources+$providerHashes) { if ((Hash $source.path) -ne $source.sha256) { throw "Input changed: $($source.path)" } }
$profile=[ordered]@{schemaVersion=2;status='offline-built-private-element-probe-v2';gameRoot=[IO.Path]::GetFullPath($GameRoot);sources=$sources;providers=$providerHashes;
    files=@('MNBL.AgentGrowthProbe.dll','MNBL.NativeElementProbe.dll','SubModule.xml') | ForEach-Object { [ordered]@{name=$_;sha256=(Hash (Join-Path $package $_))} };
    environmentKey='MNBL_PRIVATE_ELEMENT_PROBE_REQUEST';modules='_MODULES_*Native*SandBoxCore*CustomBattle*MNBL.AgentGrowthProbe*_MODULES_';
    sourceSha256=$originalNative;patchedSha256=$originalNative;exitContract=[ordered]@{path=[IO.Path]::GetFullPath($contractPath);sha256=$contractSha};liveApproval=$false}
$profile | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$package/element-probe-profile.json" -Encoding utf8
$receipt=[ordered]@{status='passed-managed-host-v2-offline-build';sources=$sources;providers=$providerHashes;package=$package;profileSha256=(Hash "$package/element-probe-profile.json");testOutput=($testOutput -join "`n");
    testExecutableSha256=(Hash "$out/ManagedElementHostTests.exe");outputs=$profile.files;gameLaunched=$false;installedFilesChanged=$false;liveBindingTested=$false}
$receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$repo/evidence/engine-extension/managed-element-host-v2-offline-build.json" -Encoding utf8
Write-Output $testOutput
Write-Output "Built offline private probe v2 package: $package (profile $($receipt.profileSha256))"
