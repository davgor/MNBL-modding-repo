param([Parameter(Mandatory=$true)][string]$Assembly,[Parameter(Mandatory=$true)][string]$Receipt,
      [string]$GameBin='D:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord/bin/Win64_Shipping_Client')
$ErrorActionPreference='Stop'
Add-Type -Path (Join-Path $GameBin 'mono/lib/mono/gac/Mono.Cecil/0.11.0.0__0738eb9f132ed756/Mono.Cecil.dll')
$receiptData=Get-Content -LiteralPath $Receipt -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $Assembly).Hash.ToLowerInvariant() -ne $receiptData.patchedSha256) {throw 'Assembly receipt hash differs'}
$definition=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($Assembly)
try {
    function Types($items) { foreach($type in $items) {$type; Types $type.NestedTypes} }
    $methods=@((Types $definition.MainModule.Types) | ForEach-Object {$_.Methods})
    $results=@()
    foreach($record in $receiptData.methods) {
        $method=@($methods | Where-Object FullName -eq $record.method)
        if($method.Count -ne 1){throw 'Changed method is ambiguous'}
        $method=$method[0];$instructions=$method.Body.Instructions
        $depth=@{};$queue=[Collections.Generic.Queue[int]]::new()
        function Seed([int]$index,[int]$height) {
            if($index -lt 0 -or $index -ge $instructions.Count){throw 'Invalid instruction edge'}
            if($depth.ContainsKey($index)) {if($depth[$index] -ne $height){throw "Stack join differs at $($method.Name):$index"}}
            else {$depth[$index]=$height;$queue.Enqueue($index)}
        }
        Seed 0 0
        foreach($handler in $method.Body.ExceptionHandlers) {
            $height=if($handler.HandlerType -in @([Mono.Cecil.Cil.ExceptionHandlerType]::Catch,[Mono.Cecil.Cil.ExceptionHandlerType]::Filter)){1}else{0}
            Seed ($instructions.IndexOf($handler.HandlerStart)) $height
            if($handler.FilterStart){Seed ($instructions.IndexOf($handler.FilterStart)) 1}
        }
        $max=0
        while($queue.Count) {
            $index=$queue.Dequeue();$instruction=$instructions[$index];$height=$depth[$index]
            $pop=$instruction.OpCode.StackBehaviourPop.ToString();$push=$instruction.OpCode.StackBehaviourPush.ToString()
            if($pop -eq 'Varpop') {
                if($instruction.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ret){$pops=if($method.ReturnType.FullName -eq 'System.Void'){0}else{1}}
                elseif($instruction.Operand -is [Mono.Cecil.MethodReference]) {
                    $pops=$instruction.Operand.Parameters.Count
                    if($instruction.Operand.HasThis -and $instruction.OpCode.Code -ne [Mono.Cecil.Cil.Code]::Newobj){$pops++}
                } else {throw "Unsupported variable-pop $instruction"}
            } elseif($pop -eq 'Pop0') {$pops=0}
            elseif($pop -eq 'PopAll') {$pops=$height}
            else {$pops=($pop -split '_').Count}
            if($push -eq 'Varpush') {
                $pushes=if($instruction.Operand.ReturnType.FullName -eq 'System.Void'){0}else{1}
            } elseif($push -eq 'Push0') {$pushes=0}
            else {$pushes=($push -split '_').Count}
            if($height -lt $pops){throw "Stack underflow: $($method.Name) $instruction"}
            $after=$height-$pops+$pushes;$max=[Math]::Max($max,[Math]::Max($height,$after))
            if($instruction.OpCode.Code -in @([Mono.Cecil.Cil.Code]::Leave,[Mono.Cecil.Cil.Code]::Leave_S)){$after=0}
            if($instruction.OpCode.FlowControl -in @([Mono.Cecil.Cil.FlowControl]::Return,[Mono.Cecil.Cil.FlowControl]::Throw)) {
                if($after -ne 0){throw "Nonempty terminal stack: $instruction"};continue
            }
            if($instruction.Operand -is [Mono.Cecil.Cil.Instruction]) {Seed ($instructions.IndexOf($instruction.Operand)) $after}
            elseif($instruction.Operand -is [Mono.Cecil.Cil.Instruction[]]) {foreach($target in $instruction.Operand){Seed ($instructions.IndexOf($target)) $after}}
            if($instruction.OpCode.FlowControl -ne [Mono.Cecil.Cil.FlowControl]::Branch) {Seed ($index+1) $after}
        }
        if($max -gt $method.Body.MaxStackSize){throw 'Recorded maximum stack is too small'}
        $results+=@{method=$method.FullName;reachableInstructions=$depth.Count;instructions=$instructions.Count;maximumStack=$max;handlers=$method.Body.ExceptionHandlers.Count}
    }
    $spawn=$methods | Where-Object {$_.DeclaringType.FullName -eq 'TaleWorlds.MountAndBlade.Mission' -and $_.Name -eq 'SpawnAgent'}
    if($spawn.Body.Instructions[2].Operand.Name -ne 'BeginSpawn'){throw 'Missing entry reservation'}
    $outer=$spawn.Body.ExceptionHandlers[$spawn.Body.ExceptionHandlers.Count-1]
    if($outer.HandlerType -ne [Mono.Cecil.Cil.ExceptionHandlerType]::Finally -or
       $outer.TryStart -ne $spawn.Body.Instructions[3] -or $outer.HandlerStart.Next.Operand.Name -ne 'EndSpawn') {throw 'Reservation cleanup region differs'}
    $agent=$methods | Where-Object {$_.DeclaringType.FullName -eq 'TaleWorlds.MountAndBlade.Mission' -and $_.Name -eq 'CreateAgent'}
    $call=$agent.Body.Instructions | Where-Object {$_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'CreateAgentInternal'}
    if($call.Previous.Operand.Name -ne 'ArmReservedCreate' -or $call.Previous.Previous.Operand.Name -ne 'mnblUseReservation'){throw 'Arm window is not immediately before native-create entry'}
    @{status='passed';targetExecuted=$false;assemblySha256=$receiptData.patchedSha256;methods=$results;testSha256=(Get-FileHash -LiteralPath $PSCommandPath).Hash.ToLowerInvariant()} | ConvertTo-Json -Depth 6
} finally {$definition.Dispose()}
