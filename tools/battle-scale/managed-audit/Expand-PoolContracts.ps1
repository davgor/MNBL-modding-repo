param([string]$ReportPath="$PSScriptRoot/../../../evidence/engine-extension/pool-managed-expansion-audit.json")
$ErrorActionPreference='Stop'
$r=Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
$bin='D:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord/bin/Win64_Shipping_Client'
Add-Type -Path "$bin/mono/lib/mono/gac/Mono.Cecil/0.11.0.0__0738eb9f132ed756/Mono.Cecil.dll"
function Types($items) {foreach($t in $items){$t; Types $t.NestedTypes}}
$methods=[Collections.Generic.List[object]]::new();foreach($m in $r.methods){$methods.Add($m)}
$fields=[Collections.Generic.List[object]]::new();foreach($f in $r.fields){$fields.Add($f)}
$typeInfo=@();$arrayCandidates=@();$narrowIndexFields=@();$cacheConsumers=@();$compressionConsumers=@();$pointerConsumers=@()
foreach($artifact in $r.artifacts) {
 $a=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($artifact.path)
 foreach($t in (Types $a.MainModule.Types)) {
  if($t.FullName -match 'Agent(ReadOnly)?List|AgentHelper|^TaleWorlds.Library.MBReadOnlyList`1$|^TaleWorlds.MountAndBlade.CompressionInfo/Integer$|^TaleWorlds.MountAndBlade.Mission/AgentCreationResult$') {
   $typeInfo += [ordered]@{assembly=$a.Name.Name;name=$t.FullName;baseType=$t.BaseType.FullName;classSize=$t.ClassSize;packingSize=$t.PackingSize;fields=@($t.Fields | ForEach-Object {$_.FullName})}
  }
  foreach($f in $t.Fields){if($f.FieldType.FullName -in @('System.Int16','System.UInt16','System.Byte','System.SByte') -and $f.Name -match 'Agent|Index') {$narrowIndexFields += [ordered]@{assembly=$a.Name.Name;field=$f.FullName}}}
  foreach($m in $t.Methods){
   if(!$m.HasBody){continue}
   $reasons=@(); $ins=$m.Body.Instructions
   if($t.FullName -match 'Agent(ReadOnly)?List|AgentHelper|^TaleWorlds.MountAndBlade.CompressionInfo/Integer$'){$reasons+='focused-type'}
   if(($t.FullName -eq 'TaleWorlds.Library.MBReadOnlyList`1' -and $m.Name -eq '.ctor') -or ($t.FullName -eq 'TaleWorlds.MountAndBlade.Agent' -and $m.Name -eq 'Clear') -or ($t.FullName -eq 'TaleWorlds.MountAndBlade.Mission' -and $m.Name -match '^FindAgentWithIndex') -or ($t.FullName -eq 'TaleWorlds.MountAndBlade.GameNetwork' -and $m.Name -match '^get_Is(Server|ServerOrRecorder|Client|Replay|Multiplayer|MultiplayerOrReplay)$')){$reasons+='lifetime-and-offline-contract'}
   foreach($i in $ins) {
    $operand=''; if($i.Operand -is [Mono.Cecil.MemberReference]){$operand=[string]$i.Operand}
    if($operand -match 'SpawningBehaviorBase::_(maxAgentCount|agentCountThreshold)|DefaultBattleMissionAgentSpawnLogic::_maxNumberOfAgentsForMissionCache|::get_MaxNumberOfAgentsForMission\('){
     $cacheConsumers += [ordered]@{assembly=$a.Name.Name;method=$m.FullName;instruction=$i.ToString()};$reasons+='cached-capacity-consumer'
    }
    if($operand -match 'CompressionMission::AgentCompressionInfo') {
     $compressionConsumers += [ordered]@{assembly=$a.Name.Name;method=$m.FullName;instruction=$i.ToString()};$reasons+='compression-consumer'
    }
    if($i.OpCode.Name -eq 'newarr' -and ($operand -match 'Agent' -or $t.FullName -match 'Mission|Agent|Formation')) {
     $at=$ins.IndexOf($i);$lo=[Math]::Max(0,$at-5);$hi=[Math]::Min($ins.Count-1,$at+2)
     $arrayCandidates += [ordered]@{assembly=$a.Name.Name;method=$m.FullName;instruction=$i.ToString();context=@($ins[$lo..$hi] | ForEach-Object {$_.ToString()})}
    }
    if($operand -match 'Agent::_(indexPointer|positionPointer|statePointer|flagsPointer|movementModePointer|controllerTypePointer|movementDirectionPointer|primaryWieldedItemIndexPointer|offHandWieldedItemIndexPointer|channel0CurrentActionPointer|channel1CurrentActionPointer|maximumForwardUnlimitedSpeed|pointer)$') {
     $pointerConsumers += [ordered]@{assembly=$a.Name.Name;method=$m.FullName;instruction=$i.ToString()}
     if($m.Name -match '^(get_|set_)|^DeleteAgent$|^InitializeAgentRecord$'){$reasons+='pointer-consumer'}
    }
   }
   if($reasons.Count -and !($methods | Where-Object {$_.assembly -eq $a.Name.Name -and $_.signature -eq $m.FullName})){
    $methods.Add([ordered]@{assembly=$a.Name.Name;signature=$m.FullName;token=('0x{0:x8}' -f $m.MetadataToken.ToInt32());il=@($ins | ForEach-Object {$_.ToString()});reasons=@($reasons | Select-Object -Unique)})
   }
  }
 }
 $a.Dispose()
}
$r.methods=@($methods);$r.fields=@($fields)
$r | Add-Member -Force -NotePropertyMembers @{focusedTypes=$typeInfo;capacityCacheConsumers=$cacheConsumers;agentCompressionConsumers=$compressionConsumers;managedArrayCandidates=$arrayCandidates;narrowIndexFieldCandidates=$narrowIndexFields;nativePointerFieldConsumers=$pointerConsumers}
$r | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
@{methods=$methods.Count;arrayCandidates=$arrayCandidates.Count;narrowFields=$narrowIndexFields.Count;pointerConsumers=$pointerConsumers.Count;cacheConsumers=$cacheConsumers.Count;compressionConsumers=$compressionConsumers.Count} | ConvertTo-Json
