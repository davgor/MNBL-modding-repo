param(
    [string]$GameRoot = 'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord',
    [string]$ProducerRoot = 'C:\Users\davgo\Documents\GitHub\FWGC++',
    [string]$Output = "$PSScriptRoot/../evidence/baseline/environment.json",
    [switch]$RequireNavalEditor
)
$ErrorActionPreference = 'Stop'

function Read-SteamKeyValues {
    param([string]$Text)
    # Parse scope, not a global regex: dlcappid must belong to an InstalledDepots entry.
    $pattern = '"(?:\\.|[^"\\])*"|[{}]|//[^\r\n]*|\s+'
    $tokens = [Collections.Generic.List[string]]::new()
    $position = 0
    foreach ($match in [regex]::Matches($Text, $pattern)) {
        if ($match.Index -ne $position) { throw 'Unsupported Steam manifest syntax' }
        $position += $match.Length
        if ($match.Value.StartsWith('"') -or $match.Value -eq '{' -or $match.Value -eq '}') { $tokens.Add($match.Value) }
    }
    if ($position -ne $Text.Length) { throw 'Incomplete Steam manifest syntax' }
    $cursor = 0
    function Read-SteamBlock([bool]$Nested, [ref]$Index) {
        $result = @{}
        while ($Index.Value -lt $tokens.Count) {
            $key = $tokens[$Index.Value]
            if ($key -eq '}') {
                if (!$Nested) { throw 'Unexpected closing brace' }
                $Index.Value++
                return $result
            }
            if (!$key.StartsWith('"')) { throw 'Expected manifest key' }
            $key = $key.Substring(1, $key.Length - 2)
            $Index.Value++
            if ($Index.Value -ge $tokens.Count -or $result.ContainsKey($key)) { throw 'Missing value or duplicate key' }
            $value = $tokens[$Index.Value]
            $Index.Value++
            if ($value -eq '{') {
                # Share a cursor across recursive blocks.
                $result[$key] = Read-SteamBlock $true $Index
            } elseif ($value.StartsWith('"')) { $result[$key] = $value.Substring(1, $value.Length - 2) }
            else { throw 'Expected manifest value' }
        }
        if ($Nested) { throw 'Unclosed manifest block' }
        return $result
    }
    Read-SteamBlock $false ([ref]$cursor)
}

function Get-NavalEditorReadiness {
    param([string]$SteamApps)
    $observations = @(); $registrations = @(); $uncertain = $false
    foreach ($id in @('261550','1393600','4456490')) {
        $path = Join-Path $SteamApps "appmanifest_$id.acf"
        if (!(Test-Path -LiteralPath $path)) {
            $observations += [ordered]@{ appId = $id; path = $path; status = 'absent' }
            if ($id -ne '4456490') { $uncertain = $true }
            continue
        }
        try {
            $bytes = [IO.File]::ReadAllBytes($path)
            $hasher = [Security.Cryptography.SHA256]::Create()
            try { $hash = [BitConverter]::ToString($hasher.ComputeHash($bytes)).Replace('-','').ToLowerInvariant() } finally { $hasher.Dispose() }
            $state = (Read-SteamKeyValues ([Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xfeff))).AppState
            if ($state -isnot [Collections.IDictionary] -or $state.appid -ne $id -or $state.InstalledDepots -isnot [Collections.IDictionary]) { throw 'Missing app identity or InstalledDepots block' }
            $complete = $state.StateFlags -eq '4'
            foreach ($pair in @(@('BytesToDownload','BytesDownloaded'),@('BytesToStage','BytesStaged'))) {
                if ($state[$pair[0]] -notmatch '^\d+$' -or $state[$pair[1]] -notmatch '^\d+$' -or $state[$pair[0]] -ne $state[$pair[1]]) { $complete = $false }
            }
            $depots = @()
            foreach ($depotId in $state.InstalledDepots.Keys) {
                $depot = $state.InstalledDepots[$depotId]
                if ($depot -isnot [Collections.IDictionary]) { throw 'Invalid installed depot' }
                $depots += [ordered]@{ depotId = $depotId; manifest = $depot.manifest; dlcAppId = $depot.dlcappid }
                if ($depot.dlcappid -eq '4456490' -or $id -eq '4456490') {
                    if ($complete -and $depot.manifest -match '^[1-9][0-9]*$') {
                        $registrations += [ordered]@{ appId = $id; depotId = $depotId; manifest = $depot.manifest; dlcAppId = $depot.dlcappid; association = $(if ($id -eq '4456490') { 'standalone-app-identity' } else { 'installed-depot-dlcappid' }) }
                    } else { $uncertain = $true }
                }
            }
            if (!$complete -or (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $hash) { $uncertain = $true }
            $observations += [ordered]@{ appId = $id; path = $path; status = $(if ($complete) { 'complete-snapshot' } else { 'pending-or-incomplete' }); sha256 = $hash; installedDepots = $depots }
        } catch {
            $uncertain = $true
            $observations += [ordered]@{ appId = $id; path = $path; status = 'unreadable-or-unrecognized'; reason = 'Could not establish a complete Steam depot snapshot.' }
        }
    }
    $status = if ($uncertain) { 'unknown' } elseif ($registrations.Count) { 'installed-depot-registration-not-runtime-verified' } else { 'missing' }
    [ordered]@{
        appId = '4456490'; status = $status; registrationPassed = $status -eq 'installed-depot-registration-not-runtime-verified'
        runtimeVerified = $false; assetResolutionStatus = 'unknown'; observedUtc = [DateTime]::UtcNow.ToString('O')
        source = 'https://store.steampowered.com/app/4456490/'
        meaning = 'Separate free War Sails Modding Kit DLC supplies editor content. Depot registration is not proof of usable assets or editor startup. Missing means no registration in inspected manifests; unknown includes pending, changing or unreadable manifests. Rerun after Steam finishes installation.'
        registrations = @($registrations); manifests = @($observations)
    }
}

$failures = [Collections.Generic.List[string]]::new()
$steamapps = Split-Path (Split-Path $GameRoot)
$manifests = foreach ($id in @('261550','1393600')) {
    $path = Join-Path $steamapps "appmanifest_$id.acf"
    if (!(Test-Path $path)) { $failures.Add("Missing Steam manifest: $path"); continue }
    $raw = Get-Content $path -Raw
    $values = @{}
    foreach ($key in @('buildid','StateFlags','BytesToDownload','BytesDownloaded','BytesToStage','BytesStaged')) {
        $match = [regex]::Match($raw, '"' + $key + '"\s+"([^"\r\n]+)"')
        $values[$key] = $match.Groups[1].Value
    }
    if ($values.StateFlags -ne '4' -or $values.BytesToDownload -ne $values.BytesDownloaded -or $values.BytesToStage -ne $values.BytesStaged) { $failures.Add("Partial or pending Steam installation: $path") }
    [ordered]@{ appId = $id; path = $path; values = $values; sha256 = (Get-FileHash $path).Hash.ToLowerInvariant() }
}
$modules = foreach ($name in @('Native','SandBoxCore','SandBox','StoryMode','NavalDLC')) {
    $path = Join-Path $GameRoot "Modules/$name/SubModule.xml"
    if (!(Test-Path $path)) { $failures.Add("Missing required module: $path"); continue }
    [xml]$xml = Get-Content $path
    $expectedVersion = if ($name -eq 'NavalDLC') { 'v1.2.8' } else { 'v1.4.8' }
    if ($xml.Module.Version.value -ne $expectedVersion) { $failures.Add("Unsupported $name version: $($xml.Module.Version.value); expected $expectedVersion") }
    [ordered]@{ id = $xml.Module.Id.value; version = $xml.Module.Version.value; requiredBaseVersion = $xml.Module.RequiredBaseVersion.value; sha256 = (Get-FileHash $path).Hash.ToLowerInvariant() }
}
$files = foreach ($relative in @('bin/Win64_Shipping_Client/Bannerlord.exe','bin/Win64_Shipping_Client/TaleWorlds.CampaignSystem.dll','bin/Win64_Shipping_Client/TaleWorlds.Engine.dll','bin/Win64_Shipping_wEditor/Bannerlord.exe','Modules/NavalDLC/bin/Win64_Shipping_Client/NavalDLC.dll')) {
    $path = Join-Path $GameRoot $relative
    if (!(Test-Path $path)) { $failures.Add("Missing required binary: $path"); continue }
    $file = Get-Item $path
    [ordered]@{ path = $relative; bytes = $file.Length; version = $file.VersionInfo.FileVersion; sha256 = (Get-FileHash $path).Hash.ToLowerInvariant() }
}
$os = Get-CimInstance Win32_OperatingSystem
$toolPaths = @('C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe','C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe','C:/Program Files (x86)/Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8.1')
foreach ($toolPath in $toolPaths) { if (!(Test-Path $toolPath)) { $failures.Add("Missing build prerequisite: $toolPath") } }
$defaultInventoryPassed = $failures.Count -eq 0
$navalEditor = Get-NavalEditorReadiness $steamapps
if ($RequireNavalEditor -and !$navalEditor.registrationPassed) { $failures.Add("War Sails editor-content registration: $($navalEditor.status). Required DLC app 4456490; see navalEditor evidence. Shipping NavalDLC is not editor-content proof.") }
$report = [ordered]@{
    schema = 'mnbl.environment'; version = 1; capturedUtc = [DateTime]::UtcNow.ToString('O')
    gameRoot = $GameRoot; producerRoot = $ProducerRoot
    installed = @{ manifests = @($manifests); modules = @($modules); files = @($files) }
    defaultInstallationInventoryPassed = $defaultInventoryPassed
    requireNavalEditor = [bool]$RequireNavalEditor; navalEditor = $navalEditor
    target = @{ game = 'v1.4.8'; naval = 'v1.2.8'; framework = '.NET Framework 4.8.1 (host API .NET Standard 2.0)' }
    tools = @{ cmake = 'C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'; msbuild = 'C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe'; frameworkReference = 'C:/Program Files (x86)/Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8.1'; dotnetSdkInstalled = $false }
    memory = @{ totalBytes = [long]$os.TotalVisibleMemorySize * 1024; freeBytes = [long]$os.FreePhysicalMemory * 1024 }
    disks = @(Get-PSDrive -PSProvider FileSystem | Select-Object Name,Used,Free)
    failures = @($failures); installationInventoryPassed = $failures.Count -eq 0
    runtimePassed = $false
}
New-Item (Split-Path $Output) -ItemType Directory -Force | Out-Null
$report | ConvertTo-Json -Depth 10 | Set-Content $Output
if ($failures.Count) { throw ($failures -join "`n") }
Write-Output "Installation inventory passed. Runtime capability remains unproven. Evidence: $Output"
