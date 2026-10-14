param([string]$GameRoot = 'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord', [string[]]$ExtraTypes = @())
$ErrorActionPreference = 'Stop'
$client = Join-Path $GameRoot 'bin/Win64_Shipping_Client'
Add-Type -Path (Join-Path $client 'mono/lib/mono/gac/Mono.Cecil/0.11.0.0__0738eb9f132ed756/Mono.Cecil.dll')
$names = @('Scene','Mesh','GameEntity','IMapScene','IMapSceneCreator','MapScene','MapScreen','MBSubModuleBase','InitialStateOption','Module','MBObjectManager','SandBoxGameManager','NavigationCache','MapDistanceModel','PartyNavigationModel','SceneInitializationData','MBGameManager')
$names += $ExtraTypes
$assemblies = @((Get-ChildItem $client -Filter 'TaleWorlds*.dll')) + @(Get-ChildItem (Join-Path $GameRoot 'Modules') -Filter '*.dll' -Recurse | Where-Object FullName -match 'Win64_Shipping_Client')
$result = foreach ($file in $assemblies) {
    try { $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($file.FullName) } catch { continue }
    foreach ($type in $assembly.MainModule.Types | Where-Object Name -in $names) {
        [ordered]@{
            assembly = $file.FullName; sha256 = (Get-FileHash $file.FullName).Hash.ToLowerInvariant()
            framework = @($assembly.CustomAttributes | Where-Object { $_.AttributeType.Name -eq 'TargetFrameworkAttribute' } | ForEach-Object { $_.ConstructorArguments[0].Value })
            type = $type.FullName; base = [string]$type.BaseType
            fields = @($type.Fields | ForEach-Object { [ordered]@{ signature = $_.FullName; public = $_.IsPublic } })
            methods = @($type.Methods | ForEach-Object { [ordered]@{ signature = $_.FullName; public = $_.IsPublic; family = $_.IsFamily; virtual = $_.IsVirtual; parameters = @($_.Parameters | ForEach-Object { [ordered]@{name=$_.Name;type=$_.ParameterType.FullName;optional=$_.IsOptional;default=$_.Constant} }); body = @($_.Body.Instructions | Where-Object { $null -ne $_ } | ForEach-Object { $_.ToString() }) } })
        }
    }
    $assembly.Dispose()
}
New-Item evidence/runtime -ItemType Directory -Force | Out-Null
$result | ConvertTo-Json -Depth 12 | Set-Content evidence/runtime/api-inventory.json
$result | ForEach-Object { "$($_.type) [$($_.framework -join ',')]"; $_.methods | Where-Object { $_.public -or $_.family } | ForEach-Object signature } | Set-Content evidence/runtime/api-signatures.txt
