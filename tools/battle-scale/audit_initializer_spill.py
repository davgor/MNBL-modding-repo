"""Retain the bounded initializer Mission home proof without executing the game."""
from pathlib import Path
import hashlib
import json
import struct
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT/'build/battle-scale/python-libs'))
import capstone
import pefile
from patch_native_copy import safe_review_output
from native_instruction_semantics import memory_operand_writes


def audit():
    folder = ROOT/'evidence/engine-extension'
    paths = [folder/n for n in ('native-pool-reference-inventory.json', 'pool-semantic-review-mid.json',
        'native-expansion-ghidra-evidence.json', 'native-closure-ghidra-evidence.json', 'managed-core-callback-bodies.json')]
    inventory, mid, old, closure, managed = [json.loads(p.read_text(encoding='utf-8-sig')) for p in paths]
    binary = Path(inventory['binary']['path']); source = binary.read_bytes()
    sha = hashlib.sha256(source).hexdigest()
    if sha != '2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a':
        raise ValueError('Unsupported source')
    pe = pefile.PE(data=source); base = pe.OPTIONAL_HEADER.ImageBase
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64); md.detail = True
    def span(a, z):
        rows = list(md.disasm(pe.get_data(a-base, z-a), a))
        if sum(i.size for i in rows) != z-a: raise ValueError('Decode gap '+hex(a))
        return rows
    def raw(i): return dict(address=hex(i.address), bytes=i.bytes.hex(), decoded=i.mnemonic+' '+i.op_str)
    def exact(a, h):
        if pe.get_data(a-base,len(bytes.fromhex(h))).hex() != h: raise ValueError('Changed bytes '+hex(a))
    body = span(base+0x5c0e20,base+0x5c2762); by = {i.address:i for i in body}
    entry = mid['missionMethodFlowReview']['entryContracts']['0x1805c0e20']
    if entry.get('entryPointerKind') != 'mission': raise ValueError('Initializer entry not proved')
    for a,h in ((0x5c0e2e,'488da888feffff'),(0x5c0e35,'4881ec40020000'),
        (0x5c0e69,'4c8bf1'),(0x5c0e6c,'48894d88'),(0x5c2498,'4c8b7588')): exact(base+a,h)
    store = base+0x5c0e6c; reload = base+0x5c2498
    writes=[]; addresses=[]; calls=[]
    for n,i in enumerate(body):
        if not store <= i.address <= reload: continue
        if i.mnemonic == 'call': calls.append(dict(raw(i), context=[raw(j) for j in body[max(0,n-12):n]]))
        for operand_index,o in enumerate(i.operands):
            if o.type != capstone.x86.X86_OP_MEM or i.reg_name(o.mem.base) not in ('rbp','rsp'): continue
            if o.mem.index: raise ValueError('Indexed stack operand requires review')
            off = o.mem.disp+(0x100 if i.reg_name(o.mem.base)=='rbp' else 0)
            if memory_operand_writes(i,operand_index):
                writes.append(dict(raw(i),stackOffset=off,byteLength=o.size))
                if off < 0x90 and off+o.size > 0x88 and i.address != store: raise ValueError('Mission home overwritten')
            if i.mnemonic == 'lea':
                addresses.append(dict(raw(i),stackOffset=off))
                if 0x88 <= off < 0x90: raise ValueError('Mission home escapes')
    for i in body:
        if i.address > reload and i.group(capstone.CS_GRP_JUMP):
            if i.operands[0].type != capstone.x86.X86_OP_IMM or i.operands[0].imm <= reload:
                raise ValueError('Later block reenters saved-home lifetime')
    evidence=[]
    callees=(0x5c0e20,0x1ba9f0,0x725c20,0x725ad0,0x06e120,0x05bd00,0x05df60,0x725df0,0x725ef0,
        0x1c66d0,0x1cfd10,0x7307f0,0x08f580,0x08f6d0,0x1e9360,0x1e9290,0x1e9170,0x1d1a40,0x1d29a0,
        0x1f1490,0x1f0ab0,0x06a180,0x436af0,0x1f2a80,0x5d2350,0x071500,0x2509b0,0x252960,
        0x929800,0x848e60,0x828500,0x56ff70,0x57f6d0,0x57f8e0,0x646770)
    for a in callees:
        matches=[r for b in (old,closure) for r in b['records'] if r.get('operation')=='procedure_pseudo_code' and r.get('parameters',{}).get('procedure')==hex(base+{0x071500:0x072a10,0x56ff70:0x56ffc2}.get(a,a))]
        if not matches: raise ValueError('Missing retained callee '+hex(a))
        r=matches[-1]; evidence.append(dict(procedure=hex(base+a),evidenceId=r['evidence_id'],normalizedResult=r['normalized_result']))
    effects=[
        dict(calls=[0x5c0fcb],inputs=[[0x120,0x38]],outputs=[],proof='1ba9f0 only reads caller Scene-initializer record; its writes target new Scene and local allocations. Header is not forwarded.'),
        dict(calls=[0x5c10bf,0x5c1456],inputs=[],outputs=[[0xf8,32],[0xd0,32]],proof='725c20 initializes a32byte header.725ad0 changes only+8/+18 and heap payload. No caller header escapes.'),
        dict(calls=[0x5c1504],inputs=[[0x38,32]],outputs=[[0x38,32]],proof='06e120 move transfers heap string then resets source+8/+10/+18; source header never forwarded.'),
        dict(calls=[0x5c155a],inputs=[[0xd0,32]],outputs=[[0x38,32]],proof='05bd00 only reads input string; updates output vector header via05df60.05df60 writes begin/end/capacity within24bytes, other writes target heap backing.'),
        dict(calls=[0x5c163b],inputs=[[0xa0,16]],outputs=[],proof='Managed callback73 receives NativeObjectPointer by value, not ref/out. Generated callback reads Pointer into a new Scene wrapper; no caller aggregate address reaches Mission.OnSceneCreated.'),
        dict(calls=[0x5c1739,0x5c1ae9],inputs=[],outputs=[[0x188,16],[0xe8,16]],proof='Imported memcpy_s receives destination capacity16; selected stack inline character storage is bounded. Heap string contents otherwise use allocated backing.'),
        dict(calls=[0x5c1749],inputs=[[0x170,32]],outputs=[[0x68,32]],proof='1c66d0 initializes32byte string header ending exactly at saved Mission88;725ad0 only writes header+8/+18 or allocated payload. Input is read only.'),
        dict(calls=[0x5c19cb],inputs=[[0x38,32],[0x120,0x4c]],outputs=[[0x120,0x3c]],proof='1cfd10 copies source string with725df0/725ef0. Scene options are copied/read; editor camera writes three floats at+30 via7307f0 and matrix rotation08f580/08f6d0 writes offsets0..2b. Captured options are readonly in1d1a40,1e9360,1e9300,1e9290,1d29a0. Capture managers copy16/24bytes of pointer values and never follow options. No options pointer is forwarded by those jobs; scalar flag values and Scene pointers are distinct.'),
        dict(calls=[0x5c1afe],inputs=[[0xd0,32]],outputs=[],proof='All startup sound implementations slot18: two null047bd0 RET; FMOD252960 reads string+8 only.929800 reads name via848e60 and memcpy; diagnostic828500 copies to its own256bytebuffer. This literal name is shorter than128, so formatting fallback cannot execute.'),
        dict(calls=[0x5c1bb7],inputs=[[0xa0,32]],outputs=[],proof='06a180 reads string header and payload; no writes or header forwarding.'),
        dict(calls=[0x5c1cf3],inputs=[[0xa0,16],[0xf8,16]],outputs=[],proof='436af0 copies/reads two vectors; nested manager branches have just been zero-initialized and cannot run. Writes target allocated grid object.'),
        dict(calls=[0x5c1db0],inputs=[],outputs=[[0xd0,32]],proof='1f2a80 call-free output iterator fields0/8/10/18, all eight-byte writes; no output forwarding.'),
        dict(calls=[0x5c1f9c],inputs=[],outputs=[[0xa0,8],[0xf8,8]],proof='5d2350 stores only oneQWORD through each caller output; saves aliases local_d0/local_e8, never forwards them. Nested geometry computation uses callee locals; local_e8 reassigned only after final caller output.')]
    for e in effects:
        for a,z in e['outputs']:
            if a < 0x90 and a+z > 0x88: raise ValueError('Callee output overlaps Mission home')
        e['calls']=[hex(base+a) for a in e['calls']]
    tables=[]
    for table,target in ((0xae53d0,0x047bd0),(0xafb448,0x047bd0),(0xaf96c0,0x252960)):
        pointer=pe.get_data(table+0x18,8)
        if struct.unpack('<Q',pointer)[0] != base+target: raise ValueError('Sound slot changed')
        tables.append(dict(vtable=hex(base+table),slot='0x18',bytes=pointer.hex(),target=hex(base+target)))
    # Every possible instruction start around RIP displacements targeting the
    # sound-manager global, including overlapping stores and trailing immediates.
    global_address=base+0xd96d00; references={}
    for section in pe.sections:
        if not section.Characteristics & 0x20000000: continue
        block=section.get_data(); va=base+section.VirtualAddress
        for end_disp in range(4,len(block)+1):
            dest0=int.from_bytes(block[end_disp-4:end_disp],'little',signed=True)+va+end_disp
            if not global_address-23 <= dest0 <= global_address+7: continue
            for tail in (0,1,2,4,8):
                end=end_disp+tail
                if end>len(block): continue
                for start in range(max(0,end-15),end_disp-4):
                    decoded=list(md.disasm(block[start:end],va+start))
                    if len(decoded)!=1 or decoded[0].size!=end-start: continue
                    i=decoded[0]
                    for index,o in enumerate(i.operands):
                        if o.type!=capstone.x86.X86_OP_MEM or i.reg_name(o.mem.base)!='rip': continue
                        address=i.address+i.size+o.mem.disp
                        if address>=global_address+8 or address+max(o.size,1)<=global_address: continue
                        references[i.address]=dict(raw(i),memoryAddress=hex(address),memoryBytes=o.size,
                            writes=memory_operand_writes(i,index),addressTaken=i.mnemonic=='lea')
    global_writers=[r for r in references.values() if r['writes']]
    suffixes={'0x180072a11':'0x180072a10','0x180072a6f':'0x180072a6e'}
    for row in global_writers:
        if row['address'] in suffixes:
            parent=references[int(suffixes[row['address']],16)]
            if parent['bytes']!='48'+row['bytes']: raise ValueError('Writer suffix not a REX-truncated duplicate')
            row['excludedAsInstructionSuffixOf']=parent['address']
    true_writers=[r for r in global_writers if 'excludedAsInstructionSuffixOf' not in r]
    if {r['address'] for r in true_writers}!={'0x180072a10','0x180072a6e'}:
        raise ValueError('New sound-manager writer '+repr(global_writers))
    if any(r['addressTaken'] for r in references.values()): raise ValueError('Sound global address escapes')
    literal_offsets={key:source.find(value) for key,value in (
        ('rva32',(global_address-base).to_bytes(4,'little')),('va64',global_address.to_bytes(8,'little')))}
    if any(v!=-1 for v in literal_offsets.values()): raise ValueError('Sound global has additional literal alias')
    method=next(m for m in managed['methods'] if m['type']=='ManagedCallbacks.CoreCallbacksGenerated' and m['name']=='Mission_OnSceneCreated')
    if method['parameters'][1]['type'] != 'TaleWorlds.DotNet.NativeObjectPointer': raise ValueError('Callback aggregate ABI changed')
    if any('ldarga' in i or 'starg' in i for i in method['instructions']): raise ValueError('Byvalue aggregate address unexpectedly exposed')
    raw_leaves=[]
    for a,z in ((0x1e9470,0x1e9475),(0x1e9300,0x1e9313),(0x1e9160,0x1e9168),
        (0x1e9480,0x1e94b5),(0x1e9430,0x1e9465),(0x1e9320,0x1e9355),(0x1e92c0,0x1e92f5),
        (0x047bd0,0x047bd3),(0x57f8e0,0x57f904),(0x56ff70,0x56ffde)):
        raw_leaves.append(dict(start=hex(base+a),endExclusive=hex(base+z),instructions=[raw(i) for i in span(base+a,base+z)]))
    result=dict(schemaVersion=1,targetSha256=sha,status='bounded-initializer-home-reviewed',gameExecuted=False,
        sources=[dict(path=str(p),sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in paths],entryContract=entry,
        functionInstructions=[raw(i) for i in body],frame=dict(rbpMinusRsp=0x100,missionSpill=[0x88,8],store=raw(by[store]),reload=raw(by[reload]),directWrites=writes,localAddresses=addresses),
        allCallsDuringSpillLifetime=calls,callEffects=effects,soundVtables=tables,
        soundGlobalReferences=list(references.values()),soundGlobalWriters=global_writers,soundGlobalLiteralOffsets=literal_offsets,
        rawHelperBodies=raw_leaves,retainedCalleeEvidence=evidence,
        managedCallback=dict(index=73,interfaceSlot='0x270',registration='56ff70 counterzero;57f6d0 binds MBCore57f8e0; ascending CoreManaged.OnInitialize delegatearray;57f8e0 passesinterface+18 andincrementscounter;646770 case49 writes+258.',method=method),
        exactReloadSeeds={hex(reload):dict(register='r14',pointerKind='mission',biasBytes=0,instruction=raw(by[reload]))},
        proof='The sole saved Mission write is at5c0e6c. Every local address exposed before5c2498 belongs to a listed bounded callee effect or a local RAII marker. Other calls receive external objects, heap backing, scalars, or imports with their installed ABI. No later edge reenters the saved-home interval. R14 is a preserved register after this exact reload; downstream operand provenance is separately checked by the CFG prover.',
        limitations=['Installed callback/provider profile only; arbitrary hooks and memory corruption are outside the proof.','Normal-flow stack provenance only, not runtime validation or performance.'])
    out=safe_review_output(folder/'native-initializer-spill-contracts.json',[binary,*paths,Path(__file__).resolve()])
    out.write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(dict(output=str(out),instructions=len(body),calls=len(calls),reloads=1)))


if __name__ == '__main__': audit()





