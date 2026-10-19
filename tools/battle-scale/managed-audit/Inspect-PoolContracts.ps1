param(
    [string]$GameRoot = 'D:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord',
    [string]$OutputPath = "$PSScriptRoot/../../../evidence/engine-extension/pool-managed-expansion-audit.json"
)
$ErrorActionPreference = 'Stop'
$bin = Join-Path $GameRoot 'bin/Win64_Shipping_Client'
Add-Type -Path (Join-Path $bin 'mono/lib/mono/gac/Mono.Cecil/0.11.0.0__0738eb9f132ed756/Mono.Cecil.dll')
function Types($items) { foreach ($t in $items) { $t; Types $t.NestedTypes } }
function MethodRecord($m, $assembly) {
    [ordered]@{ assembly=$assembly; signature=$m.FullName; token=('0x{0:x8}' -f $m.MetadataToken.ToInt32()); il=@($m.Body.Instructions | ForEach-Object { $_.ToString() }) }
}
$paths = [Collections.Generic.List[string]]::new()
Get-ChildItem -LiteralPath $bin -Filter 'TaleWorlds*.dll' | ForEach-Object { $paths.Add($_.FullName) }
foreach ($module in @('Native','SandBoxCore','SandBox','StoryMode','CustomBattle','Multiplayer')) {
    $p = Join-Path $GameRoot "Modules/$module/bin/Win64_Shipping_Client"
    if (Test-Path -LiteralPath $p) { Get-ChildItem -LiteralPath $p -Filter '*.dll' | ForEach-Object { $paths.Add($_.FullName) } }
}
$identities = @(); $skipped=@(); $constantHits=@(); $getterCalls=@(); $agentIndexConversions=@(); $selected=@(); $fields=@(); $methodCount=0
foreach ($path in ($paths | Sort-Object -Unique)) {
    try { $a=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($path) } catch { $skipped += [ordered]@{path=$path;reason=$_.Exception.Message}; continue }
    $name=$a.Name.Name
    $identities += [ordered]@{path=$path;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant();mvid=$a.MainModule.Mvid.ToString()}
    foreach ($t in (Types $a.MainModule.Types)) {
        if ($t.FullName -in @('TaleWorlds.MountAndBlade.Agent','TaleWorlds.MountAndBlade.Mission/AgentCreationResult','TaleWorlds.MountAndBlade.Mission')) {
            $fields += @($t.Fields | ForEach-Object { [ordered]@{assembly=$name;type=$t.FullName;field=$_.FullName;attributes=$_.Attributes.ToString()} })
        }
        foreach($m in $t.Methods) {
            if (!$m.HasBody) { continue }; $methodCount++
            $ins=$m.Body.Instructions
            $selectedReason=[Collections.Generic.List[string]]::new()
            foreach ($i in $ins) {
                if ($i.OpCode.Name -eq 'ldc.i4' -and [int]$i.Operand -in @(2039,2040,2041,2046,2047,2048,4095,4096)) {
                    $at=$ins.IndexOf($i); $lo=[Math]::Max(0,$at-3); $hi=[Math]::Min($ins.Count-1,$at+5)
                    $constantHits += [ordered]@{assembly=$name;method=$m.FullName;token=('0x{0:x8}' -f $m.MetadataToken.ToInt32());value=[int]$i.Operand;instruction=$i.ToString();context=@($ins[$lo..$hi] | ForEach-Object { $_.ToString() })}
                }
                if ($i.Operand -and $i.Operand.ToString() -match 'GetMaximumNumberOfAgents|get_MaximumNumberOfAgents') {
                    $getterCalls += [ordered]@{assembly=$name;method=$m.FullName;instruction=$i.ToString()}; $selectedReason.Add('maximum-getter-reference')
                }
            }
            $text=($ins | ForEach-Object { $_.ToString() }) -join "`n"
            if ($text -match 'Agent::get_Index' -and $text -match 'conv\.[iu]2') {
                $agentIndexConversions += [ordered]@{assembly=$name;method=$m.FullName}; $selectedReason.Add('agent-index-and-16-bit-conversion')
            }
            if (($t.FullName -eq 'TaleWorlds.MountAndBlade.Agent' -and $m.Name -in @('.ctor','get_Index','get_Position','get_Controller','get_State','get_AgentState','get_IsActive','get_MaximumNumberOfAgents')) -or
                ($t.FullName -eq 'TaleWorlds.MountAndBlade.Mission' -and $m.Name -in @('.ctor','CreateAgentInternal','OnAgentDeleted','GetAgentFromIndex','get_MaximumNumberOfAgents')) -or
                ($t.FullName -match '^TaleWorlds.MountAndBlade.CompressionInfo/Integer$') -or
                ($t.FullName -eq 'TaleWorlds.MountAndBlade.CompressionMission' -and $m.Name -eq '.cctor') -or
                ($m.Name -match '^(Read|Write)AgentIndexFromPacket$|^(Read|Write)AgentReferenceToPacket$|^(Read|Write)AgentIndexToPacket$|^(Read|Write)AgentReferenceFromPacket$')) {
                $selectedReason.Add('contract-entry')
            }
            if ($selectedReason.Count) { $record=MethodRecord $m $name; $record.reasons=@($selectedReason | Select-Object -Unique); $selected += $record }
        }
    }
    $a.Dispose()
}
$report=[ordered]@{
    schemaVersion=1; inspectedUtc=[DateTime]::UtcNow.ToString('o'); scope='Execution-free Mono.Cecil metadata/CIL inspection. Game assemblies are never loaded for execution. Main TaleWorlds assemblies and six installed official module bins only; third-party mods, native binaries and absent modules excluded.';
    proposedCapacity=4080; originalCapacity=2040; artifacts=$identities; skipped=$skipped; scannedMethodBodies=$methodCount;
    getterReferences=$getterCalls; capacityConstantCandidates=$constantHits; agentIndexNarrowingCandidates=$agentIndexConversions; fields=$fields; methods=$selected;
    findings=@(); unknowns=@(); validation=@{targetExecuted=$false;managedPatched=$false;nativePatched=$false}
}
$report | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
[ordered]@{output=[IO.Path]::GetFullPath($OutputPath);assemblies=$identities.Count;skipped=$skipped.Count;methods=$methodCount;getterRefs=$getterCalls.Count;constants=$constantHits.Count;selected=$selected.Count} | ConvertTo-Json

