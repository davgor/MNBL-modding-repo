"""Read-only native pool exhaustion/teardown contract audit; never execute target code."""
from pathlib import Path
import hashlib
import json
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'build/battle-scale/python-libs'))
import capstone
import pefile
from patch_native_copy import safe_review_output


def build():
    folder = ROOT / 'evidence/engine-extension'
    names = ['native-pool-reference-inventory.json', 'native-pool-layout.json',
             'native-pool-patch-4080.json', 'native-pool-helper-caller-review.json',
             'native-pool-helper-escape-review.json', 'pool-unwind-audit.json',
             'native-ghidra-evidence.json', 'native-pool-ghidra-evidence.json',
             'native-expansion-ghidra-evidence.json', 'native-closure-ghidra-evidence.json']
    inputs = {name: folder / name for name in names}
    docs = {name: json.loads(path.read_text(encoding='utf-8-sig')) for name, path in inputs.items()}
    inv = docs[names[0]]
    target = Path(inv['binary']['path'])
    blob = target.read_bytes()
    digest = hashlib.sha256(blob).hexdigest()
    manifest = docs['native-pool-patch-4080.json']
    if digest != inv['binary']['sha256'] or digest != manifest['sourceSha256']:
        raise ValueError('Native source mismatch')
    pe = pefile.PE(data=blob)
    base = pe.OPTIONAL_HEADER.ImageBase
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True
    patched = bytearray(blob)
    for edit in manifest['patches']:
        off = edit['fileOffset']
        old, new = bytes.fromhex(edit['original']), bytes.fromhex(edit['replacement'])
        if blob[off:off+len(old)] != old or len(old) != len(new):
            raise ValueError('Manifest original mismatch')
        patched[off:off+len(old)] = new

    def decode(start, end, candidate=False):
        off = pe.get_offset_from_rva(start)
        data = (patched if candidate else blob)[off:off+end-start]
        rows = list(md.disasm(data, base+start))
        if sum(i.size for i in rows) != len(data):
            raise ValueError('Selected span has decode gap: '+hex(start))
        return [dict(address=hex(i.address), bytes=i.bytes.hex(),
                     mnemonic=i.mnemonic, operands=i.op_str) for i in rows]

    # Every start/end below is an observed instruction boundary, never a byte-search hit.
    spans = {
        'allocatorFullFailureAndFirstMutation': (0x5d8020, 0x5d805f),
        'createAgentAllocationAndCompanionCommits': (0x5cd130, 0x5cd297),
        'managedNativeWrapper': (0x7084a0, 0x7085b7),
        'deleteAgentIndexModeGate': (0x5d00a0, 0x5d013f),
        'missionPoolDestruction': (0x5cf2fc, 0x5cf37a),
    }
    raw = {name: {'original': decode(*bounds), 'candidate': decode(*bounds, candidate=True)}
           for name, bounds in spans.items()}
    site = {int(r['address'], 16)-base: r for group in raw.values() for r in group['original']}
    expected = {0x5d8044:'81fbf8070000', 0x5d804c:'b8ffffffff', 0x5cd171:'e8aaae0000',
                0x5cd176:'448be8', 0x5cd179:'eb3a', 0x5cd1b5:'4963dd',
                0x5cd1c2:'41c6461c00', 0x5cd1f3:'4489681c',
                0x7084f6:'e8354cecff', 0x5d00fd:'0f8418080000',
                0x5d0106:'0f840f080000', 0x5cf344:'41b8f8070000',
                0x5cf374:'41b8f8070000'}
    for addr, value in expected.items():
        if site[addr]['bytes'] != value:
            raise ValueError('Contract instruction changed: '+hex(addr))
    candidate_site = {int(r['address'],16)-base:r for group in raw.values() for r in group['candidate']}
    if candidate_site[0x5d8044]['operands'] != 'ebx, 0xff0':
        raise ValueError('Candidate capacity guard is not4080')
    for a in (0x5cf344, 0x5cf374):
        if candidate_site[a]['operands'] != 'r8d, 0xff0':
            raise ValueError('Candidate destructor count is not4080')
    # The failure branch reaches RET before the first pool write at8059.
    fail_path = decode(0x5d804c, 0x5d8059)
    if [r['mnemonic'] for r in fail_path] != ['mov','add','pop','pop','pop','ret']:
        raise ValueError('Unexpected exhaustion epilogue')
    # No conditional test can reject the result between return and first invalid write.
    unchecked = decode(0x5cd176, 0x5cd17b) + decode(0x5cd1b5, 0x5cd1c7)
    if any(r['mnemonic'].startswith('j') and r['mnemonic'] != 'jmp' for r in unchecked):
        raise ValueError('Allocation-result branch requires re-review')

    procedures = {0x5d8020,0x5cd130,0x7084a0,0x5d00a0,0x5cef70,0x5cf3c0,
                  0x5d7df0,0x5d8390,0x5d87d0,0x5d8610,0x5d91f0,0x5d9350,0x5d94a0}
    retained = {}
    for name in names:
        if not name.endswith('ghidra-evidence.json'):
            continue
        for rec in docs[name]['records']:
            proc = rec.get('parameters',{}).get('procedure')
            text = rec.get('normalized_result')
            if proc and proc.startswith('0x') and int(proc,16)-base in procedures and isinstance(text,str) and 'param_' in text:
                retained.setdefault(proc, dict(evidenceId=rec['evidence_id'], bundle=name, text=text))
    if len(retained) != len(procedures):
        raise ValueError('Missing retained semantic body: '+str(procedures-{int(p,16)-base for p in retained}))

    original_pools = docs['native-pool-layout.json']['pools']
    candidate_pools = manifest['layout']['pools']
    invalid_geometry = []
    for old, new in zip(original_pools, candidate_pools):
        if old['name'] == 'second_agent_slots_role_unresolved':
            continue
        stride = int(old['elementStride'],16)
        header = 0x1c if stride in (0xc60,0x230) else 0
        invalid_geometry.append(dict(pool=old['name'], index=-1, fieldWithinSlot=hex(header),
            originalMissionOffset=hex(int(old['missionOffset'],16)-stride+header),
            candidateMissionOffset=hex(int(new['missionOffset'],16)-stride+header),
            classification='before-start-of-selected-slot-array'))

    result = {
        'schemaVersion':1, 'status':'static-hazard-confirmed-capacity-plus-one-not-safe-through-create-agent',
        'sourceSha256':digest,'manifestSha256':hashlib.sha256(inputs['native-pool-patch-4080.json'].read_bytes()).hexdigest(),
        'gameExecuted':False,'binaryWritten':False,
        'sources':{name:{'path':str(path),'sha256':hashlib.sha256(path.read_bytes()).hexdigest()} for name,path in inputs.items()},
        'scriptSha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        'allocator':{
            'entry':'0x1805d8020','originalCapacity':2040,'candidateCapacity':4080,
            'abi':{'platform':'Windowsx64','delegate':'int function(IntPtr pool)','argumentRegister':'RCX','poolFromMission':'Mission pointer+0x2d50','returnRegister':'EAX',
                   'fullPathPreserved':'RBX,RSI,RDI are pushed/restored; RSP restored. No other nonvolatile register is written on full path.',
                   'fullPathClobbered':['RAX','R9','flags'],
                   'fullPathMemory':'Exactly DWORD reads pool+i*0xc60+0x10 for i0..4079; stack-only saves/restores; no pool/global writes, CALL, virtual invocation, lock, or allocation.',
                   'privateTestPrecondition':'All4080 slot headers nonnegative, verified immediately before call; execute on owner thread without concurrent pool mutation. Any free slot takes a mutating path with virtual+0x68.'},
            'selection':'First slot whose signed Agent index at slot+0x10 is negative.',
            'fullResult':'EAX=0xffffffff (-1 as signed32); zero pool mutations on this helper failure path.',
            'fullReadBounds':'Reads slots0..capacity-1; increment/compare branches to failure before reading slot capacity.',
            'successMutationOrder':['Increment live count','Mark slot index','Update signed16 next-index table','Invoke slot virtual+0x68','Update high-water','Return index'],
            'failureEpilogue':fail_path,
            'assumptions':['All slot headers and next-index tables are coherent.','No concurrent mutation during allocation.'],
        },
        'poolContracts':[
            dict(pool=original_pools[0]['name'], allocation='Only bounded free-slot selector5d8020, unless caller supplies explicit index.', guard='selector only'),
            dict(pool=original_pools[1]['name'], allocation='No normal CreateAgent or DeleteAgent use in reviewed path; default constructed and finally destroyed.', guard='no independent allocation path established; gameplay role remains unresolved'),
            dict(pool=original_pools[2]['name'], allocation='Same primary index committed directly; helper5d94a0 maintains next-index table.', guard='no independent capacity/validity check'),
            dict(pool=original_pools[3]['name'], allocation='Same primary index committed directly; helper5d9350 maintains next-index table.', guard='no independent capacity/validity check'),
            dict(pool=original_pools[4]['name'], allocation='Same primary index committed directly; helper5d9350 maintains next-index table.', guard='no independent capacity/validity check'),
        ],
        'createFailure':{
            'entry':'0x1805cd130','wrapper':'0x1807084a0',
            'finding':'Neither native caller nor registered wrapper rejects allocator -1 before pointer arithmetic and writes.',
            'firstCorruption':'5cd1c2 writes byte0 to Mission+0x210c when allocator returns-1; this is before the primary Agent array.',
            'laterCorruption':'230/2c8 headers and signed16 index tables use index-1; companion live counts can increment. Exact later execution may fault first; no successful recovery is claimed.',
            'explicitIndex':'Any non--1 supplied index bypasses the free-slot selector and writes directly; no native range or occupied-slot check here.',
            'rollback':'No allocation-result check or rollback precedes the first write. General exception rollback after partial initialization is not established.',
            'invalidSlotGeometry':invalid_geometry,
        },
        'removeContract':{
            'entry':'0x1805d00a0',
            'modeGate':'Invalid or stale index returns early only when mode equals1 or4. Single0 falls through to pointer arithmetic even when the index/table checks fail.',
            'validRemoval':['Release Agent resources through virtual+0x70; mark primary index-1; decrement count; unlink next-index table; shrink high-water if removing highest live index.',
                            'Mark230 slot free and update its count/table/high-water; free both2c8 slots through5d8610.',
                            'Secondc60 pool is not touched by this normal removal path.'],
            'requiredCallerContract':'Only remove a successfully spawned currently-live owned Agent once; never pass invalid, stale, or already-removed index.',
        },
        'teardown':{
            'missionDestructor':'0x1805cef70','clearMission':'0x1805cf3c0',
            'clearOrder':'Clear active state/jobs and reset primary,230,2c8A,2c8B; secondc60 pool is handled by final destructor.',
            'poolDestructionOrder':['Reset2c8B','Reset2c8A','Reset230','Reset secondc60 active slots; destruct all capacity slots6070c0','Reset primaryc60 active slots; destruct all capacity slots6070c0'],
            'candidateCounts':'Bothc60 vector destructor iterators pass4080 and stride0xc60; primary reset loops and all companion table/reset limits are selected by existing manifest.',
            'emptyState':'Reset helpers mark slot headers/tables invalid and zero live-count/high-water metadata; all default-constructedc60 objects still get destructed.',
            'exceptionScope':'Existing pool-unwind-audit retains constructor/destructor C++ funclet ownership and adjusted counts. No injected exception or runtime unwind execution has been verified.',
            'runtimeObservationsNeeded':['Removal through real end-of-mission lifecycle','Reload/recreate mission after full live set teardown','No remaining job ownership before release','Repeat empty/full/recycled mission cycles','Check high-water shrink and low-index reuse after removal'],
        },
        'probeRequirements':[
            'Preflight capacity before entering registered native CreateAgent; raw capacity+1 call is not a clean failure test.',
            'Record 4080 as capacity, last valid index4079, and reject any explicit index outside0..4079.',
            'Treat native exhaustion as unsafe caller contract, not evidence that increasing the limit repaired overflow handling.',
            'Observe all four active pools staying synchronized through successful spawn/remove; secondc60 has a separate lifecycle-only proof.',
            'Do not test invalid/double removal through Single0 without a dedicated guard.',
            'A safe standalone helper exhaustion test must use controlled storage and invoke only the bounded helper, not the CreateAgent wrapper; such native execution was not performed by this audit.',
        ],
        'rawProofs':raw,'retainedSemanticBodies':retained,
        'limitations':['No target execution or memory instrumentation in this audit.','No universal constructor-exception rollback, multithreaded allocation, or arbitrary custom-provider guarantee.','Managed-side admission checks must be reviewed separately; native behavior above is independent of any such checks.'],
    }
    output = safe_review_output(folder/'native-pool-exhaustion-contract.json', [target,Path(__file__),*inputs.values()])
    output.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({'output':str(output),'status':result['status'],'rawInstructionEntries':sum(len(g[k]) for g in raw.values() for k in ('original','candidate')),'retainedBodies':len(retained)}))


if __name__ == '__main__':
    build()
