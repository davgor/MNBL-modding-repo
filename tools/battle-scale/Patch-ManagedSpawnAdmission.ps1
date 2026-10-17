param([Parameter(Mandatory=$true)][string]$Source, [Parameter(Mandatory=$true)][string]$Module,
      [Parameter(Mandatory=$true)][string]$Output, [Parameter(Mandatory=$true)][string]$Receipt)
$ErrorActionPreference='Stop'
$expected='19387f31557ff840d14f378f6bbdf1d58fcff406ab9ff2294dfbb3e49a50b87e'
if ((Get-FileHash -LiteralPath $Source).Hash.ToLowerInvariant() -ne $expected) { throw 'Unreviewed managed engine source.' }
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$outputPath=[IO.Path]::GetFullPath($Output)
$receiptPath=[IO.Path]::GetFullPath($Receipt)
if ($outputPath -eq $receiptPath) { throw 'Assembly and receipt outputs must be different paths.' }
foreach($path in @($outputPath,$receiptPath)) {
    if (!$path.StartsWith((Join-Path $repo 'build/battle-scale/'),[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $path)) { throw 'Requires a fresh output in the offline battle-scale build directory.' }
    for($parent=Split-Path $path; $parent; $parent=Split-Path $parent) {
        if ((Get-Item -LiteralPath $parent).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked output path refused.' }
    }
}
$bin=Split-Path $Source
Add-Type -Path (Join-Path $bin 'mono/lib/mono/gac/Mono.Cecil/0.11.0.0__0738eb9f132ed756/Mono.Cecil.dll')
$resolver=[Mono.Cecil.DefaultAssemblyResolver]::new()
$resolver.AddSearchDirectory([IO.Path]::GetFullPath($bin))
$resolver.AddSearchDirectory([IO.Path]::GetFullPath((Split-Path $Module)))
$resolver.AddSearchDirectory([IO.Path]::GetFullPath((Join-Path $bin 'mono/lib/mono/4.5')))
$reader=[Mono.Cecil.ReaderParameters]::new();$reader.AssemblyResolver=$resolver
$engine=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($Source,$reader)
$moduleAssembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($Module,$reader)
try {
    if ($engine.Name.HasPublicKey) { throw 'Cannot rewrite a signed engine assembly.' }
    $mission=$engine.MainModule.Types | Where-Object FullName -eq 'TaleWorlds.MountAndBlade.Mission'
    $guard=$moduleAssembly.MainModule.Types | Where-Object FullName -eq 'MNBL.BattleScale.NativeSpawnAdmission'
    function Method($type,[string]$name) {
        $matches=@($type.Methods | Where-Object Name -ceq $name)
        if ($matches.Count -ne 1 -or !$matches[0].HasBody) { throw "Ambiguous or missing method: $name" }
        return $matches[0]
    }
    function GuardReference([string]$name,[string]$returns,[string[]]$parameters) {
        $m=Method $guard $name
        if (!$m.IsStatic -or $m.ReturnType.FullName -ne $returns -or
            (($m.Parameters | ForEach-Object {$_.ParameterType.FullName}) -join '|') -ne ($parameters -join '|')) { throw "Guard signature differs: $name" }
        return $engine.MainModule.ImportReference($m)
    }
    function Shape($method) {
        $instructions=$method.Body.Instructions
        function Position($instruction) { if ($null -eq $instruction) { return -1 }; return $instructions.IndexOf($instruction) }
        $rows=@(foreach($instruction in $instructions) {
            $operand=$instruction.Operand
            if ($operand -is [Mono.Cecil.Cil.Instruction]) { $operand='target:'+(Position $operand) }
            elseif ($operand -is [Mono.Cecil.Cil.Instruction[]]) { $operand='targets:'+(($operand | ForEach-Object {Position $_}) -join ',') }
            elseif ($operand -is [Mono.Cecil.Cil.VariableDefinition]) { $operand='local:'+$operand.Index }
            elseif ($operand -is [Mono.Cecil.ParameterDefinition]) { $operand='argument:'+$operand.Index }
            elseif ($null -ne $operand) { $operand=$operand.ToString() }
            @{opcode=$instruction.OpCode.Name;operand=$operand}
        })
        $handlers=@(foreach($handler in $method.Body.ExceptionHandlers) {
            @{kind=$handler.HandlerType.ToString();tryStart=(Position $handler.TryStart);tryEnd=(Position $handler.TryEnd);
              handlerStart=(Position $handler.HandlerStart);handlerEnd=(Position $handler.HandlerEnd);filterStart=(Position $handler.FilterStart);
              catchType=if($handler.CatchType){$handler.CatchType.FullName}else{$null}}
        })
        return (@{instructions=$rows;handlers=$handlers;locals=@($method.Body.Variables | ForEach-Object {$_.VariableType.FullName});initLocals=$method.Body.InitLocals} | ConvertTo-Json -Depth 10 -Compress)
    }
    function TextHash([string]$text) {
        $hash=[Security.Cryptography.SHA256]::Create()
        try { return ([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)))).Replace('-','').ToLowerInvariant() }
        finally {$hash.Dispose()}
    }
    function Assert-NoInteriorEntry($method,$site) {
        foreach($instruction in $method.Body.Instructions) {
            if($instruction.Operand -is [Mono.Cecil.Cil.Instruction] -and $instruction.Operand -eq $site){throw 'Insertion call has a preexisting branch target.'}
            if($instruction.Operand -is [Mono.Cecil.Cil.Instruction[]] -and $instruction.Operand -contains $site){throw 'Insertion call has a preexisting switch target.'}
        }
        foreach($handler in $method.Body.ExceptionHandlers) {
            if(@($handler.TryStart,$handler.TryEnd,$handler.HandlerStart,$handler.HandlerEnd,$handler.FilterStart) -contains $site){throw 'Insertion call is an exception-region boundary.'}
        }
    }
    $begin=GuardReference 'BeginSpawn' 'System.Void' @('TaleWorlds.MountAndBlade.Mission','TaleWorlds.MountAndBlade.AgentBuildData')
    $arm=GuardReference 'ArmReservedCreate' 'System.Void' @('System.Boolean')
    $release=GuardReference 'EndSpawn' 'System.Void' @('TaleWorlds.MountAndBlade.Mission')
    $admit=GuardReference 'ValidateNativeCreate' 'System.Int32' @('TaleWorlds.MountAndBlade.Mission','System.Int32')
    $spawn=Method $mission 'SpawnAgent'
    $create=Method $mission 'CreateAgentInternal'
    $agent=Method $mission 'CreateAgent'
    $horse=Method $mission 'CreateHorseAgentFromRosterElements'
    if (!$agent.IsPrivate -or !$horse.IsPrivate) { throw 'Reservation route must remain private to the rewritten assembly.' }
    if ($spawn.Parameters[0].ParameterType.FullName -ne 'TaleWorlds.MountAndBlade.AgentBuildData' -or
        $create.Parameters[1].ParameterType.FullName -ne 'System.Int32') { throw 'Pinned engine signature differs.' }
    function AllTypes($types) { foreach($type in $types) { $type; AllTypes $type.NestedTypes } }
    $routes=@()
    $changed=[Collections.Generic.List[Mono.Cecil.MethodDefinition]]::new()
    foreach($m in @($spawn,$create,$agent,$horse)) {$changed.Add($m)}
    foreach($type in (AllTypes $engine.MainModule.Types)) {
        foreach($caller in $type.Methods) {
            if (!$caller.HasBody) {continue}
            foreach($instruction in $caller.Body.Instructions) {
                if ($instruction.Operand -isnot [Mono.Cecil.MethodReference]) {continue}
                $callee=$instruction.Operand
                if ($callee.FullName -notin @($agent.FullName,$horse.FullName)) {continue}
                if ($instruction.OpCode.Code -ne [Mono.Cecil.Cil.Code]::Call) {throw 'Private spawn helper has a non-call reference.'}
                if (!$changed.Contains($caller)) {$changed.Add($caller)}
                $routes+=@{caller=$caller;instruction=$instruction;callee=$callee.Resolve();originalOffset=$instruction.Offset}
            }
        }
    }
    $records=@(foreach($m in $changed) {@{originalMethod=$m.FullName;originalShape=(Shape $m);definition=$m}})
    $unchanged=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach($type in (AllTypes $engine.MainModule.Types)) {
        foreach($m in $type.Methods) {
            if($m.HasBody -and !$changed.Contains($m)) {$unchanged[$m.FullName]=TextHash (Shape $m)}
        }
    }
    $agentFlag=[Mono.Cecil.ParameterDefinition]::new('mnblUseReservation',[Mono.Cecil.ParameterAttributes]::None,$engine.MainModule.TypeSystem.Boolean)
    $horseFlag=[Mono.Cecil.ParameterDefinition]::new('mnblUseReservation',[Mono.Cecil.ParameterAttributes]::None,$engine.MainModule.TypeSystem.Boolean)
    $agent.Parameters.Add($agentFlag);$horse.Parameters.Add($horseFlag)
    $spawnRoutes=@($routes | Where-Object {$_.caller -eq $spawn})
    if ($spawnRoutes.Count -ne 2 -or $spawnRoutes[0].originalOffset -ne 0x63 -or $spawnRoutes[1].originalOffset -ne 0x7be) {throw 'Pinned rider/horse call sites differ.'}
    foreach($route in $routes) {
        Assert-NoInteriorEntry $route.caller $route.instruction
        $processor=$route.caller.Body.GetILProcessor()
        if ($route.caller -eq $spawn) {$flag=$processor.Create([Mono.Cecil.Cil.OpCodes]::Ldc_I4_1)}
        elseif ($route.caller -eq $horse -and $route.callee -eq $agent) {$flag=$processor.Create([Mono.Cecil.Cil.OpCodes]::Ldarg,$horseFlag)}
        else {$flag=$processor.Create([Mono.Cecil.Cil.OpCodes]::Ldc_I4_0)}
        $processor.InsertBefore($route.instruction,$flag)
        $route.instruction.Operand=$route.callee
    }
    # Arm after all preparation calls, directly before the guarded native-create
    # call. A nested callback in Fill* therefore cannot consume this reservation.
    $nativeCalls=@($agent.Body.Instructions | Where-Object {$_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Call -and $_.Operand.FullName -eq $create.FullName})
    if ($nativeCalls.Count -ne 1) {throw 'Private CreateAgent native-create route differs.'}
    Assert-NoInteriorEntry $agent $nativeCalls[0]
    $processor=$agent.Body.GetILProcessor()
    $processor.InsertBefore($nativeCalls[0],$processor.Create([Mono.Cecil.Cil.OpCodes]::Ldarg,$agentFlag))
    $processor.InsertBefore($nativeCalls[0],$processor.Create([Mono.Cecil.Cil.OpCodes]::Call,$arm))

    $first=$create.Body.Instructions[0];$il=$create.Body.GetILProcessor()
    $il.InsertBefore($first,$il.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0))
    $il.InsertBefore($first,$il.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_2))
    $il.InsertBefore($first,$il.Create([Mono.Cecil.Cil.OpCodes]::Call,$admit))
    $il.InsertBefore($first,$il.Create([Mono.Cecil.Cil.OpCodes]::Starg_S,$create.Parameters[1]))

    $first=$spawn.Body.Instructions[0];$il=$spawn.Body.GetILProcessor()
    # Begin stays outside the try: failed admission must not pop an outer scope.
    $il.InsertBefore($first,$il.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0))
    $il.InsertBefore($first,$il.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_1))
    $il.InsertBefore($first,$il.Create([Mono.Cecil.Cil.OpCodes]::Call,$begin))
    $returnLocal=[Mono.Cecil.Cil.VariableDefinition]::new($spawn.ReturnType)
    $spawn.Body.Variables.Add($returnLocal)
    $finallyStart=$il.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0)
    $epilogue=$il.Create([Mono.Cecil.Cil.OpCodes]::Ldloc,$returnLocal)
    $returns=@($spawn.Body.Instructions | Where-Object {$_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ret})
    if ($returns.Count -ne 1) { throw 'Pinned SpawnAgent return count differs.' }
    foreach($ret in $returns) {
        $ret.OpCode=[Mono.Cecil.Cil.OpCodes]::Stloc;$ret.Operand=$returnLocal
        $il.InsertAfter($ret,$il.Create([Mono.Cecil.Cil.OpCodes]::Leave,$epilogue))
    }
    foreach($handler in $spawn.Body.ExceptionHandlers) {
        if ($null -eq $handler.TryEnd) {$handler.TryEnd=$finallyStart}
        if ($null -eq $handler.HandlerEnd) {$handler.HandlerEnd=$finallyStart}
    }
    $il.Append($finallyStart)
    $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Call,$release))
    $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Endfinally))
    $il.Append($epilogue)
    $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ret))
    $finally=[Mono.Cecil.Cil.ExceptionHandler]::new([Mono.Cecil.Cil.ExceptionHandlerType]::Finally)
    $finally.TryStart=$first;$finally.TryEnd=$finallyStart;$finally.HandlerStart=$finallyStart;$finally.HandlerEnd=$epilogue
    $spawn.Body.ExceptionHandlers.Add($finally)
    # Insertion may increase a preexisting short branch beyond signed-byte range.
    foreach($method in $changed) {
        foreach($instruction in $method.Body.Instructions) {
            if ($instruction.OpCode.OperandType -eq [Mono.Cecil.Cil.OperandType]::ShortInlineBrTarget) {
                $name=$instruction.OpCode.Code.ToString() -replace '_S$',''
                $instruction.OpCode=[Mono.Cecil.Cil.OpCodes].GetField($name).GetValue($null)
            }
        }
        $method.Body.MaxStackSize=[Math]::Max(2,$method.Body.MaxStackSize+1)
    }
    foreach($record in $records) {
        $record['method']=$record.definition.FullName
        $record['expectedShape']=Shape $record.definition
        $record.Remove('definition')
    }
    $engine.MainModule.Mvid=[guid]::NewGuid()
    $engine.Write($outputPath)
    $check=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($outputPath)
    try {
        $newMethods=@((AllTypes $check.MainModule.Types) | ForEach-Object {$_.Methods})
        foreach($record in $records) {
            $m=$newMethods | Where-Object FullName -ceq $record.method
            if ((Shape $m) -ne $record.expectedShape) { throw 'Written guard/control-flow/exception-region readback failed.' }
            $record['patchedInstructionCount']=$m.Body.Instructions.Count
        }
        foreach($m in $newMethods) {
            if($unchanged.ContainsKey($m.FullName)) {
                if((TextHash (Shape $m)) -ne $unchanged[$m.FullName]) {throw "Untouched method IL/EH changed: $($m.FullName)"}
            }
        }
        if(@($newMethods | Where-Object {$unchanged.ContainsKey($_.FullName)}).Count -ne $unchanged.Count){throw 'Untouched methods are missing after rewrite.'}
    } finally { $check.Dispose() }
    @{schemaVersion=2;status='offline-public-spawn-admission-inserted';sourceSha256=$expected;patchedSha256=(Get-FileHash $outputPath).Hash.ToLowerInvariant();moduleSha256=(Get-FileHash $Module).Hash.ToLowerInvariant();patchScriptSha256=(Get-FileHash $PSCommandPath).Hash.ToLowerInvariant();methods=$records;unchangedMethodCount=$unchanged.Count;unchangedMethodShapesSha256=(TextHash (($unchanged.Keys | Sort-Object | ForEach-Object {$_+'='+$unchanged[$_]}) -join "`n"));targetExecuted=$false} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $receiptPath -Encoding utf8
} finally { $engine.Dispose();$moduleAssembly.Dispose();$resolver.Dispose() }
