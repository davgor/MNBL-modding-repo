$ErrorActionPreference = 'Stop'
$bin = 'D:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord/bin/Win64_Shipping_Client'
Add-Type -Path "$bin/mono/lib/mono/gac/Mono.Cecil/0.11.0.0__0738eb9f132ed756/Mono.Cecil.dll"
function AllTypes($items) { foreach ($t in $items) { $t; AllTypes $t.NestedTypes } }
$identities=@(); $records=@()
foreach ($name in @('TaleWorlds.MountAndBlade','TaleWorlds.Core')) {
    $path=Join-Path $bin "$name.dll"
    $a=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($path)
    try {
        $identities += @{path=$path;sha256=(Get-FileHash $path).Hash.ToLowerInvariant();mvid=$a.MainModule.Mvid.ToString();publicKey=$a.Name.HasPublicKey}
        foreach ($t in (AllTypes $a.MainModule.Types)) {
            foreach ($m in $t.Methods) {
                if (($t.FullName -eq 'TaleWorlds.MountAndBlade.Mission' -and $m.Name -match 'SpawnAgent|SpawnMonster|SpawnMount|CreateAgentInternal') -or
                    ($t.FullName -eq 'TaleWorlds.MountAndBlade.AgentBuildData') -or
                    ($t.FullName -eq 'TaleWorlds.Core.Equipment' -and $m.Name -match 'Horse|Clone')) {
                    $records += @{type=$t.FullName;valueType=$t.IsValueType;name=$m.Name;signature=$m.FullName;token=$m.MetadataToken.ToInt32();parameters=@($m.Parameters | ForEach-Object { @{name=$_.Name;type=$_.ParameterType.FullName} });il=@(if ($m.HasBody) {$m.Body.Instructions | ForEach-Object {$_.ToString()}})}
                }
            }
        }
    } finally { $a.Dispose() }
}
@{sources=$identities;methods=$records;targetExecuted=$false} | ConvertTo-Json -Depth 8 | Set-Content "$PSScriptRoot/../../../evidence/engine-extension/managed-spawn-admission-input.json" -Encoding utf8
