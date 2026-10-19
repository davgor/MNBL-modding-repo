"""Execution-free native element lifetime and live-growth prerequisite audit.

This creates evidence, not a native adapter or an approval to call engine code.
Only an offline, hash-pinned original native image is read. Cecil reads managed IL.
"""
from pathlib import Path
import argparse
import hashlib
import json
import struct
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'build/battle-scale/python-libs'))
import capstone
import pefile
from patch_native_copy import safe_review_output

SOURCE = ROOT / 'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
SHA = '2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
BIN = Path('D:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord/bin/Win64_Shipping_Client')
MANAGED = BIN / 'TaleWorlds.MountAndBlade.dll'
CECIL = BIN / 'mono/lib/mono/gac/Mono.Cecil/0.11.0.0__0738eb9f132ed756/Mono.Cecil.dll'
FROZEN = ROOT / 'build/battle-scale/native4080-r13-decode-gap-input.json'


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def managed_tick():
    script = r'''
$ErrorActionPreference='Stop'
Add-Type -Path '__CECIL__'
$rp=New-Object Mono.Cecil.ReaderParameters
$rp.InMemory=$true
$a=[Mono.Cecil.AssemblyDefinition]::ReadAssembly('__MANAGED__',$rp)
try {
 $t=$a.MainModule.Types | Where-Object FullName -eq 'TaleWorlds.MountAndBlade.Mission'
 $wanted=@('OnPreTick','WaitTickCompletion','TickAgentsAndTeams','TickAgentsAndTeamsImp','TickAgentsAndTeamsAsync','OnTick')
 $methods=@($t.Methods | Where-Object { $_.Name -in $wanted } | ForEach-Object {
  $method=$_
  [ordered]@{name=$method.Name;signature=$method.FullName;instructions=@($method.Body.Instructions | ForEach-Object {
   [ordered]@{offset=('0x{0:x4}' -f $_.Offset);opcode=$_.OpCode.Name;operand=[string]$_.Operand}
  })}
 })
 [ordered]@{methods=$methods;field=@($t.Fields | Where-Object Name -eq 'tickCompleted' | ForEach-Object {
   [ordered]@{name=$_.FullName;attributes=[string]$_.Attributes;type=[string]$_.FieldType}
 })} | ConvertTo-Json -Depth 12 -Compress
} finally {$a.Dispose()}
'''.replace('__CECIL__', str(CECIL).replace("'", "''")).replace('__MANAGED__', str(MANAGED).replace("'", "''"))
    result = subprocess.run(['powershell', '-NoProfile', '-NonInteractive', '-Command', script],
                            check=True, capture_output=True, text=True)
    data = json.loads(result.stdout)
    methods = {m['name']: m for m in data['methods']}
    assert set(methods) == {'OnPreTick', 'WaitTickCompletion', 'TickAgentsAndTeams', 'TickAgentsAndTeamsImp', 'TickAgentsAndTeamsAsync', 'OnTick'}
    pre = methods['OnPreTick']['instructions']
    wait = next(i for i in pre if 'WaitTickCompletion' in i['operand'])
    callback = next(i for i in pre if 'OnPreMissionTick' in i['operand'])
    assert int(wait['offset'], 16) < int(callback['offset'], 16)
    worker = methods['TickAgentsAndTeamsImp']['instructions']
    flag = next(i for i in worker if i['opcode'] == 'stfld' and 'tickCompleted' in i['operand'])
    after = next(i for i in worker if 'AfterAsyncTickTick' in i['operand'])
    assert int(flag['offset'], 16) < int(after['offset'], 16)
    data['ordering'] = dict(waitBeforePreCallback=[wait, callback], flagBeforeWorkerTail=[flag, after],
        conclusion='OnPreMissionTick waits for a bool set before AfterAsyncTickTick callbacks finish. This bool alone is not a complete worker fence.')
    return data


def build(source):
    folder = ROOT / 'evidence/engine-extension'
    names = ['native-pool-layout.json', 'native-pool-helper-escape-review.json',
             'native-pool-helper-caller-review.json', 'native-pool-exhaustion-contract.json',
             'native-secondary-pool-role-review.json', 'pool-managed-expansion-audit.json',
             'pool-unwind-audit.json', 'native-callback-contracts.json',
             'native-pool-ghidra-evidence.json', 'native-closure-ghidra-evidence.json',
             'native-live-growth-rea-evidence.json', 'managed-core-callback-bodies.json']
    paths = [folder / name for name in names] + [FROZEN, MANAGED, CECIL, Path(__file__)]
    inputs = {p.name: json.loads(p.read_text(encoding='utf-8-sig')) for p in paths if p.suffix == '.json'}
    blob = source.read_bytes()
    assert hashlib.sha256(blob).hexdigest() == SHA, 'Wrong offline original'
    pe = pefile.PE(data=blob)
    base = pe.OPTIONAL_HEADER.ImageBase
    assert base == 0x180000000
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True

    def disassemble(a, z):
        code = pe.get_data(a, z-a)
        ins = list(md.disasm(code, base+a))
        assert sum(i.size for i in ins) == len(code), hex(a)
        return ins

    def row(i):
        return dict(address=hex(i.address), bytes=i.bytes.hex(), mnemonic=i.mnemonic, operands=i.op_str)

    spans = {
        'agentConstructor': (0x606a20, 0x607074), 'agentDestructor': (0x6070c0, 0x607388),
        'agentActivation': (0x607550, 0x607df1),
        'agentPoolConstructor': (0x5d8220, 0x5d82aa), 'agentPoolDestructor': (0x5d81e0, 0x5d821e),
        'companion230Constructor': (0x5e58e0, 0x5e5905), 'companion230Initializer': (0x694630, 0x694858),
        'companion2c8Initializer': (0x5d8860, 0x5d88ac), 'companion2c8PoolConstructor': (0x5d8740, 0x5d87cf),
        'companion230Reset': (0x5d87d0, 0x5d8839), 'companion2c8Reset': (0x5d8390, 0x5d860a),
        'automaticAgentAllocation': (0x5d8020, 0x5d81de), 'explicitAgentIndexInsert': (0x5d91f0, 0x5d933c),
        'createAgentPublication': (0x5cd130, 0x5cd2b4), 'missionConstructorPoolSequence': (0x5ce470, 0x5ce504),
        'vectorConstruction': (0x9dfd98, 0x9dfe08), 'vectorDestruction': (0x9dfe08, 0x9dfe73),
        'vectorPartialRollback': (0x9dfe74, 0x9dfed5), 'vectorRollbackHandler': (0xa2a45d, 0xa2a488),
        'managedAsyncSubmission': (0x70a6a0, 0x70a7ae), 'simpleTaskConstructor': (0x73580, 0x73623),
        'missionFinalization': (0x707750, 0x707a2e), 'taskWait': (0x633d0, 0x63491),
        'ordinaryFrameTaskWait': (0x536b4, 0x537ab), 'alternateFrameTaskWait': (0x9c641, 0x9c6c3),
        'synchronousParallelDispatcher': (0x1f2de0, 0x1f3394),
        'simpleTaskExecute': (0x73570, 0x7357e), 'simpleTaskIsComplete': (0x48590, 0x4859a),
        'simpleTaskMarkComplete': (0x4b7e0, 0x4b7f1), 'managedTickClosure': (0x70cbc0, 0x70cbe1),
        'taskWorkerA': (0x245760, 0x245820), 'taskWorkerB': (0x245820, 0x245ab8),
        'taskOwnerConstructor': (0x1748e0, 0x174986), 'taskOwnerDeletingDestructor': (0x174990, 0x174a15),
        'managedCallbackBinder': (0x57f8e0, 0x57f904), 'managedCallbackSwitch': (0x646770, 0x646790),
        'managedTickCallbackStore': (0x6469ce, 0x6469d6),
    }
    decoded = {name: disassemble(*span) for name, span in spans.items()}
    all_ins = {i.address: i for ins in decoded.values() for i in ins}

    def exact(rva, expected):
        actual = pe.get_data(rva, len(bytes.fromhex(expected)))
        assert actual.hex() == expected, (hex(rva), actual.hex(), expected)

    exact(0x606f46, '4c89bbb80a0000')  # this+ab8 <- zero R15
    exact(0x5cd28d, '49897620')         # Agent+20 <- companion2c8 pointer
    exact(0x5cd297, 'e8b4a20300')       # Agent initialization with Mission
    exact(0x5d8790, '448900')           # caller sets 2c8 header, not initializer
    exact(0x5377e, 'e84dfc0000')        # ordinary async task wait
    for key in ['companion230Initializer', 'companion2c8Initializer', 'companion230Reset', 'companion2c8Reset']:
        assert not any(i.mnemonic in ('call', 'jmp') for i in decoded[key]), key
    for key in ['companion230Initializer', 'companion2c8Initializer']:
        assert decoded[key][-1].mnemonic == 'ret'
    assert any(i.address == base+0x5d804c and i.op_str == 'eax, 0xffffffff' for i in decoded['automaticAgentAllocation'])

    # Keep exact retained REA records; deliberately do not infer evidence from a numeric hit in pseudocode.
    wanted = {0x5e5b30, 0x5ce3f0, 0x5d8220, 0x606a20, 0x6070c0, 0x73580, 0x70a6a0, 0x1f2de0, 0xfd930}
    rea = []
    for name in ['native-pool-ghidra-evidence.json', 'native-closure-ghidra-evidence.json', 'native-live-growth-rea-evidence.json']:
        for rec in inputs[name]['records']:
            val = rec.get('parameters', {}).get('procedure')
            if isinstance(val, str):
                try:
                    rva = int(val, 16) - base
                except ValueError:
                    continue
                if rva in wanted and rec.get('normalized_result'):
                    rea.append(dict(evidenceId=rec['evidence_id'], source=name, procedure=val,
                                    normalizedResult=rec['normalized_result']))
    assert any(r['procedure'] == '0x180073580' for r in rea)
    fresh = inputs['native-live-growth-rea-evidence.json']['records']
    for rec in fresh:
        if rec.get('parameters', {}).get('procedure') in ('0x1800633d0', '0x180053300', '0x18009c230', '0x180053950'):
            rea.append(dict(evidenceId=rec['evidence_id'], source='native-live-growth-rea-evidence.json',
                            procedure=rec['parameters']['procedure'], normalizedResult=rec['normalized_result']))
    xref = next(r for r in fresh if r.get('operation') == 'xrefs' and
                r.get('parameters',{}).get('address') == '0x180d9ae48')
    # This scope is exactly direct-global-reference runtime fragments. It does not prove aliases absent.
    groups = {}
    outside = []
    for address in xref['normalized_result']:
        rva = int(address, 16)-base
        owner = next((e.struct for e in pe.DIRECTORY_ENTRY_EXCEPTION
                      if e.struct.BeginAddress <= rva < e.struct.EndAddress), None)
        if owner is None:
            outside.append(address)
        else:
            groups[owner.BeginAddress] = owner.EndAddress
    table_bytes = pe.get_data(0x174fe4, 14*4)
    table_targets = struct.unpack('<14I', table_bytes)
    table_code = disassemble(0x174d80,0x174fe4)
    table_boundaries = {i.address-base for i in table_code}
    assert all(t in table_boundaries and 0x174dd2 <= t < 0x174fe4 for t in table_targets)
    table_dispatch = {i.address-base:i for i in table_code}
    assert table_dispatch[0x174db6].mnemonic == 'cmp' and table_dispatch[0x174db6].op_str == 'eax, 0xd'
    assert table_dispatch[0x174db9].mnemonic == 'ja' and table_dispatch[0x174db9].op_str == '0x180174fbb'
    assert table_dispatch[0x174dc6].op_str == 'ecx, dword ptr [rdx + rax*4 + 0x174fe4]'
    bounded_table = dict(start='0x180174fe4', endExclusive='0x18017501c', bytes=table_bytes.hex(),
        targets=[hex(base+t) for t in table_targets], dispatchInstructions=[row(table_dispatch[x]) for x in
            (0x174dae,0x174db6,0x174db9,0x174dbf,0x174dc6,0x174dcd,0x174dd0)],
        proof='Unsigned boundEAX<=13 selects14 image-relative DWORDs, all target decoded code boundaries before table; RET174fe0 precedes padding/table.')
    task_fields = []
    incomplete_fragments = []
    for a,z in groups.items():
        code_end = 0x174fe4 if a == 0x174d80 and z == 0x17501c else z
        instructions = list(md.disasm(pe.get_data(a,code_end-a),base+a))
        end = a + sum(i.size for i in instructions)
        if end != code_end:
            incomplete_fragments.append(dict(fragmentStart=hex(base+a), undecodedStart=hex(base+end),
                endExclusive=hex(base+code_end), bytes=pe.get_data(end,code_end-end).hex()))
        for i in instructions:
            if any(op.type == capstone.x86.X86_OP_MEM and op.mem.disp == 0x2c8 for op in i.operands):
                task_fields.append(dict(fragmentStart=hex(base+a), instruction=row(i),
                                        isFirstOperandMemory=i.operands[0].type==capstone.x86.X86_OP_MEM))
    leaf_tails = []
    for address in outside:
        start = int(address,16)
        tail = []
        for i in md.disasm(pe.get_data(start-base,64),start):
            tail.append(i)
            assert i.mnemonic not in ('call','ret') and not i.mnemonic.startswith('j') or i.mnemonic == 'jmp'
            assert not any(op.type == capstone.x86.X86_OP_MEM and op.mem.disp == 0x2c8 for op in i.operands)
            if i.mnemonic == 'jmp':
                break
        assert tail[-1].mnemonic == 'jmp'
        leaf_tails.append(dict(globalReference=address,instructions=[row(i) for i in tail],
            scope='From global load to tail jump only; no claimed function-start boundary or opaque callback-body coverage.'))
    stores = [r['instruction']['address'] for r in task_fields if r['isFirstOperandMemory']]
    assert set(stores) == {hex(base+x) for x in (0x53799,0x9c6b1,0x17491d,0x174965,0x7077c8,0x70a789)}
    task_vtable = {hex(off):hex(struct.unpack('<Q',pe.get_data(0xae5298+off,8))[0]) for off in [0x68,0x70,0x78]}
    assert task_vtable == {'0x68':'0x180073570','0x70':'0x180048590','0x78':'0x18004b7e0'}
    callback_target = struct.unpack('<I',pe.get_data(0x646ae0+77*4,4))[0]
    assert callback_target == 0x6469ce
    callback_methods = [r for r in inputs['managed-core-callback-bodies.json']['methods']
                        if r.get('name') in ('Initialize','Mission_TickAgentsAndTeams')]
    assert any('IL_060c: ldc.i4.s 77' in r.get('instructions',[]) for r in callback_methods)
    manifest = inputs[FROZEN.name]
    assert len(manifest['patches']) == 1714
    pools = inputs['native-pool-layout.json']['pools']
    for pool in pools:
        assert int(pool['missionOffset'], 16) + int(pool['elementStride'], 16)*2040 == int(pool['indexTableOffset'], 16)
    tick = managed_tick()
    bindings = [dict(path=str(p.resolve()), sha256=digest(p)) for p in [source] + paths]
    native = {name: dict(start=hex(base+a), endExclusive=hex(base+z),
                         instructions=[row(i) for i in decoded[name]]) for name, (a,z) in spans.items()}
    return dict(schemaVersion=1, status='static-element-contracts-reviewed-live-engine-growth-not-ready',
        nativeSourceSha256=SHA, targetExecuted=False, installedNativeRead=False,
        runtimeAdapterApproved=False, sources=bindings,
        summary=dict(originalInstructionCount=sum(len(v) for v in decoded.values()),
            retainedReaRecords=len(rea), fivePoolAdjacencyChecks=5,
            recommendedStorage='stable segmented payload blocks with separate wide metadata',
            safeManagedGrowthPointProven=False),
        elementAdapters=[
            dict(pool='primary and secondary Agent', stride=0xc60, alignmentMinimum=16,
                 constructor='0x180606a20', constructorAbi='Windows x64 RCX=uninitialized destination; RAX=this on success; no Mission argument',
                 destructor='0x1806070c0', destructorAbi='Windows x64 RCX=fully constructed destination; no deleting flag; ignore return',
                 afterConstructorWrites=[dict(offset='0x10', width=4, value=-1)],
                 ownerState='606f46 sets Agent+ab8 to zero. Stock activation607550 stores Mission at60758c(+ab8), stores self at607636(+150), mirrors Mission at607644(+148). Do not activate merely to grow storage.',
                 companionState='Stock activation stores first2c8 pointer into Agent+20 at5cd28d before607550. A segmented resolver must supply the same logical index from every companion pool.',
                 defaultDependencies='Live engine allocator metadata/vtables/constants and C++ exception runtime. Execute constructor on each actual destination; never memcpy a template or existing live Agent.',
                 interiorPointerFinding='Constructor forms interior addresses for member construction/EH temporaries, including+4b0,+670,+890,+ae0,+b88. Existing managed Agent wrappers retain13 pointers. No relocation permission follows from default initialization.',
                 rollback='Destroy only successfully completed, unpublished elements, in reverse order. Destructor frees member resources but not the Agent block. Active objects first need ordinary removal/reset and managed-reference detachment.'),
            dict(pool='230', stride=0x230, alignmentMinimum=16, constructor='0x1805e58e0',
                 constructorAbi='Windows x64 RCX=destination; RAX=this; calls leaf694630 which preserves RCX',
                 afterConstructorWrites=[dict(offset='0x1c', width=4, value=-1)],
                 destructor=None, destructorConclusion='Reviewed stock pool cleanup5d87d0 invalidates header/table/count only, with no calls. No separate per-element destructor is required for default unpublished elements by this stock lifecycle.',
                 bounds='694630..694858 is complete and call-free; last initialized field+224 width2 lies inside230 bytes. This is not zero-initialization of every byte.'),
            dict(pool='both 2c8 companions', stride=0x2c8, alignmentMinimum=8, constructor='0x1805d8860',
                 constructorAbi='Windows x64 RCX=destination; RAX=this; complete call-free leaf',
                 afterConstructorWrites=[dict(offset='0x0', width=4, value=-1)],
                 destructor=None, destructorConclusion='Default unpublished elements need no separate resource destructor in reviewed stock lifecycle. Active reset5d8390 has no calls and resets+0,+b0..bf,+c0,+c8,+d0,+d4. Use normal logical removal for active objects.',
                 bounds='Constructor initializes selected fields only; caller must initialize signed index header+0.')],
        layoutBarrier=dict(originalPools=pools,
            statement='Every original payload ends exactly at its short index table. Frozen4080 layout retains this adjacency. Appending slot4080 overwrites metadata, even if more virtual address space exists after Mission.',
            contiguousReserve='A reserve/commit layout needs fixed maximum offsets before Mission construction or comprehensive address interception. It is not an unbounded live-growth solution.',
            segmented='Keep old payload blocks at their addresses. Construct new blocks for all five pools, create wide sidecar metadata, then publish a new immutable directory/capacity epoch. Existing direct Mission+index*stride consumers must be replaced or routed; sidecar allocation alone cannot enable engine access.',
            widths='Original short metadata uses positive capacity+1 sentinel: representable capacity<=32766, current unroll-compatible multiple30<=32760. These are arithmetic bounds, not tested limits. Sidecar64-bit IDs/32-bit metadata still require translation or widening of every native int32 Agent index, short-table, packed/network/replay consumer.'),
        transaction=dict(stockRollback='9dfd98 tracks completed elements; handlera2a45d calls9dfe74 to destroy completed prefix backwards. It supplies a per-vector guarantee, not a transaction across five pools.',
            prepare=['Validate source identity and ensure engine runtime initialized.',
                     'Allocate all payload and metadata segments privately with checked multiplication/alignment.',
                     'Run606a20 for both Agent pools and companion initializers; record completed count after each success; then set inactive headers.',
                     'On failure reverse-destroy completed Agent elements, free unpublished segments, leave old directories/capacity/live counts unchanged. Do not destroy the element whose constructor failed a second time.',
                     'Acquire a proven engine publication barrier, publish all five payload directories and metadata as one epoch, then expose capacity to admission and enumerators.',
                     'Keep prior directory versions alive until all readers leave their epoch; never move or destroy existing payload merely to grow.'],
            stockMutationRisk='5d8020 increments count and writes header before virtual+68. Explicit5cd130 path writes header before reset then commits table/count. Neither is a general transaction or a thread-safe growth primitive.',
            teardown='Replace full-capacity stock destructor/reset/EH loops with constructed-count per segment traversal; retain original removal semantics for live slots. Native callbacks and managed cached pointers must be detached before block release.'),
        quiescence=dict(managed=tick,
            nativeTask=dict(producer='70a6a0 creates073580 simple task and stores pointer at *D9AE48+2c8 (70a789).',
                normalWait=['53300 non-overlap branch:53756 load task,53770 retain,5377e wait,53799 clear.',
                            '09c230 alternate path:09c662 load task,09c687 retain,09c696 wait,09c6b1 clear.'],
                teardownWait='707750 FinalizeMission wrapper:70779c retain,7077ab wait,7077c8 clear. Never call full FinalizeMission to implement growth.',
                waitAbi='0633d0 RCX=taskpool,RDX=pointer to retained task,R8B=worker mode. It polls virtual+70, may pump other jobs via0635c0/virtual+20 or wait virtual+88, and releases the retained task reference via+38 on return.',
                taskCompletion=dict(vtableAddress='0x180ae5298', slots=task_vtable,
                    callbackBinding='CoreCallbacksGenerated index77; native switch table646ae0[77]→6469ce storesRCX+278; binder57f8f2 passes managedInterface+18. Therefore effective callback offset290 matches closure70cbda.',
                    callbackMethods=callback_methods,
                    completionOrder='Worker24580e calls task+68, then245817 calls+78 after return. Other paths245a04→245a0d and245aa3→245aac agree. SimpleTask+68 tailcalls captured managed callback; +78 atomically XCHGs task+18=1. Native completion in these reviewed execution paths therefore covers managed callback return including its tail callbacks.',
                    limitation='Execution paths reviewed are these worker implementations; this does not prove all task flag writers or all other engine readers participate.'),
                directGlobalFieldScope=dict(evidenceId=xref['evidence_id'], globalReferences=xref['normalized_result'],
                    runtimeFragmentCount=len(groups), referencesOutsideRuntimeFragments=outside,
                    boundedSwitchTable=bounded_table, outsideRuntimeForwardTails=leaf_tails,
                    incompleteFragments=incomplete_fragments, fieldInstructions=task_fields,
                    stores=stores, conclusion='Decoded portions of these direct-global-reference fragments have one task-submission writer, two constructor zero stores and three post-wait clears. Undecoded tails, opaque owner aliases and other routines without directglobal reference remain outside this bounded scan.'),
                waitScope='A task-completion primitive, not a global reader fence. Reentrant/pumped jobs, task completion publication after managed callback, native physics/render readers, and exact safe hook placement still require integration proof.'),
            synchronousJobs='1f2de0 drains its own remaining-work counter before return; fd930 decrements after each worker callback. This proves a particular batch, not absence of all Mission readers.',
            candidates=['After native task wait and reference release in a phase that cannot submit new Mission readers until growth ends.',
                        'Explicit engine-integrated reader epoch/exclusive growth barrier around every routed lookup and native job lifetime.'],
            blockers=['OnPreMissionTick alone is not proven safe because managed tickCompleted precedes worker-tail callbacks.',
                      'No complete native/managed/physics/render reader participation or growth publication protocol is yet established.',
                      'Taskwait pumps jobs and consumes a retained reference; a raw call from managed code without exact owner/thread/refcount contract is unsafe.']),
        hookInventory=[
            dict(address='0x1805e5b30', purpose='Mission allocator/ownership; contiguous reserve alternative requires matching deallocator'),
            dict(address='0x1805ce3f0', purpose='Mission birth constructs all five pools; adopt original slabs without relocating live objects'),
            dict(address='0x1805d8020', purpose='automatic slot allocation and immediate mutation'),
            dict(address='0x1805d91f0', purpose='explicit-index primary table insertion'),
            dict(address='0x1805cd130', purpose='four-pool activation plus Agent/Mission/companion wiring'),
            dict(address='0x1805d00a0', purpose='logical removal; invalid/double index unsafe inSingle0'),
            dict(address='0x1805d7df0', purpose='active Agent reset before element destruction'),
            dict(address='0x1805d81e0', purpose='Agent pool reset+fixed-count element destruction'),
            dict(address='0x1805cef70', purpose='Mission teardown plus associated EH cleanup thunks'),
            dict(address='0x1800633d0', purpose='retained-task wait primitive; not a general growth lock')],
        remaining=['Full lookup/enumeration/metadata interception belongs to separate G2 inventory; this evidence does not certify completeness.',
                   'A native callback adapter still needs ABI/CFG/unwind/exception-boundary testing with the actual engine runtime.',
                   'No runtime allocation failure, live growth, concurrent publication, or segmented teardown has been tested against Bannerlord by this audit.'],
        nativeBodies=native, retainedRea=rea)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--source', type=Path, default=SOURCE)
    parser.add_argument('--output', type=Path, default=ROOT/'evidence/engine-extension/native-live-pool-growth-contract.json')
    args = parser.parse_args()
    report = build(args.source)
    output = safe_review_output(args.output, [p['path'] for p in report['sources']])
    output.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(status=report['status'], summary=report['summary'], output=str(output), sha256=digest(output))))


if __name__ == '__main__':
    main()
