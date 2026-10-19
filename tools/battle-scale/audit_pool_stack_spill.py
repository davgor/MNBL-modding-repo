"""Exact, execution-free Mission spill survival review for6763c0 only."""
from pathlib import Path
import json,hashlib,sys,struct
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import capstone,pefile
from build_pool_patch_manifest import displacement_encoding
from patch_native_copy import safe_review_output
from native_instruction_semantics import memory_operand_writes

def build():
    folder=ROOT/'evidence/engine-extension'
    paths=[folder/n for n in ('native-pool-reference-inventory.json','pool-semantic-review-mid.json',
        'native-expansion-ghidra-evidence.json','native-closure-ghidra-evidence.json','pool-runtime-safety-contracts.json')]
    inventory,mid,expansion,closure,contracts=[json.loads(p.read_text()) for p in paths]
    binary=Path(inventory['binary']['path']);source=binary.read_bytes()
    sha=hashlib.sha256(source).hexdigest()
    if sha!=inventory['binary']['sha256']:raise ValueError('Wrong target')
    pe=pefile.PE(data=source);base=pe.OPTIONAL_HEADER.ImageBase
    md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
    def span(a,b):
        rows=list(md.disasm(pe.get_data(a-base,b-a),a))
        if sum(i.size for i in rows)!=b-a:raise ValueError('Proof decode gap')
        return rows
    def raw(i):return dict(address=hex(i.address),bytes=i.bytes.hex(),decoded=i.mnemonic+' '+i.op_str)
    body=span(0x1806763c0,0x180677916);byaddr={i.address:i for i in body}
    entry=mid['missionMethodFlowReview']['entryContracts']['0x1806763c0']
    if entry.get('entryRegisters',{}).get('r8')!='mission' or entry.get('entryBiases',{}).get('r8')!=0:
        raise ValueError('R8 Mission entry proof absent')
    store=byaddr[0x180676467]
    if store.bytes.hex()!='4c89442448':raise ValueError('Mission spill changed')
    if byaddr[0x1806763cc].op_str!='rbp, [rsp - 0x6cb0]' or byaddr[0x1806763d4].op_str!='eax, 0x6db0':
        raise ValueError('Frame geometry differs')
    writes=[];addresses=[]
    for ins in body:
        if not 0x180676467<=ins.address<0x1806778c3:continue
        for operand_index,op in enumerate(ins.operands):
            if op.type!=capstone.x86.X86_OP_MEM or op.mem.index:continue
            reg=ins.reg_name(op.mem.base)
            if reg not in ('rsp','rbp'):continue
            offset=op.mem.disp+(0x100 if reg=='rbp' else 0)
            if memory_operand_writes(ins,operand_index):
                writes.append(dict(raw(ins),stackOffset=offset,byteLength=op.size))
                if offset<0x50 and offset+op.size>0x48 and ins.address!=store.address:
                    raise ValueError('Another direct write overlaps Mission spill')
            if ins.mnemonic=='lea':
                addresses.append(dict(raw(ins),stackOffset=offset))
                if offset<0x50 and offset>=0:raise ValueError('Low frame address escapes')
    # Both leaf bit tests are fully decoded, branch-contained, read-only and R9-preserving.
    leaves=[]
    for a,b in ((0x1805e9ae0,0x1805e9b2b),(0x1805e9c00,0x1805e9c4b)):
        leaf=span(a,b)
        for ins in leaf:
            if ins.mnemonic=='call' or any(ins.reg_name(r) in ('r9','r9d','r9w','r9b') for r in ins.regs_access()[1]):
                raise ValueError('Bit-test leaf no longer preserves R9')
            if any(memory_operand_writes(ins,n) for n in range(len(ins.operands))):
                raise ValueError('Bit-test leaf writes memory')
            if ins.group(capstone.CS_GRP_JUMP) and (ins.operands[0].type!=capstone.x86.X86_OP_IMM or not a<=ins.operands[0].imm<b):
                raise ValueError('Bit-test leaf has external edge')
        leaves.append(dict(start=hex(a),endExclusive=hex(b),instructions=[raw(i) for i in leaf]))
    records={r['evidence_id']:r for bundle in (expansion,closure) for r in bundle['records']}
    evidence=[]
    for address in (0x434980,0x67f200,0x387510,0x6763c0,0x67e0a0,0x5d77d0,0x354fe0,0x5d9ef0,0x677920,
                    0x5e9c00,0x5dbb30,0x5dd420,0x745160,0x433e80,0x68f380,0x4b5e40,0x38f320,
                    0x3813d0,0x381810,0x381a30,0x3820b0,0x3828a0,0x383100):
        operation='procedure_assembly' if address==0x6763c0 else 'procedure_pseudo_code'
        matches=[r for r in records.values() if r.get('operation')==operation and r.get('parameters',{}).get('procedure')==hex(base+address)]
        if not matches:raise ValueError('Callee proof not retained: '+hex(address))
        value=matches[-1];eid=value['evidence_id']
        evidence.append(dict(evidenceId=eid,procedure=value.get('parameters',{}).get('procedure'),
            normalizedResult=value['normalized_result']))
    effects=[
        dict(calls=['0x180676632'],callee='0x180434980',outputs=[[0x68,4],[0x5d10,255*16],[0x5510,255*8]],
             reason='Count set0; positive count+6<255 bounds triangle appends.433e80 writes only supplied three Vec3 outputs and three optional material-pointer outputs. Height virtual calls receive object plus integer coordinates, not these stack addresses. Inputs at118/108 remain read-only.'),
        dict(calls=['0x180676675'],callee='0x18067e0a0',outputs=[[0x490,0x44]],
             reason='Contact count at output+40 initialized0. Write branch requires count<1; one64-byte contact and count+40.745160 only writes two explicitly supplied callee-local Vec4 outputs; caller input capsule90 is read-only.'),
        dict(calls=['0x180676783','0x180676fbc'],callee='0x18067f200',outputs=[],
             reason='Caller local records378/440 are read only. Writes target Mission job counter/page table and allocated heap records; alloc/free do not receive local record addresses.'),
        dict(calls=['0x180676885'],callee='0x1805d77d0',outputs=[[0x230,0x2c]],
             reason='Iterator initialization writes offsets0..2b. Recursive5dbb30/5d9ef0/5dd420 only modify iterator offsets0..f; Mission grid nodes are read. This closed helper cycle has no other pointer stores.'),
        dict(calls=['0x180676c0d','0x180676c2d'],callee='read-only bit-test leaves',outputs=[],reason='Full exact branch-contained leaf bodies have no memory writes or calls;5e9c00 preservesR9 before second LEA.'),
        dict(calls=['0x180676cdc'],callee='0x180354fe0',outputs=[[0x368,16],[0x128,16],[0x80,4]],
             reason='Only explicit Vec4 outputs and distance float.745160 inputs1..4 are read-only; output5/6 are callee-local vectors.'),
        dict(calls=['0x180676e73'],callee='0x180354fe0',outputs=[[0x358,16],[0x348,16],[0x88,4]],reason='Same exact callee output contract; both capsule inputs remain read-only.'),
        dict(calls=['0x180676dd2'],callee='0x1809e0960',outputs=[],reason='Floating scalar math call receives no caller-local pointer.'),
        dict(calls=['0x18067700f'],callee='0x1805d9ef0',outputs=[[0x230,16]],reason='Closed iterator helper cycle described above.'),
        dict(calls=['0x1806773b8','0x180677411'],callee='virtual190 installed PhysX387510 or null047bd0',outputs=[[0x3100,0x2004]],
             reason='Retained collisionQueryOutput proof: maximum128 records*64 and count+2000. No negative output writes; inputs3b8/b8 are read-only. Scope excludes arbitrary replacement providers.'),
        dict(calls=['0x18067745e'],callee='0x180677920',outputs=[],reason='Contact input from3100 and capsule90 read only. Math has scalar input;67dab0 receives external body object, not a stack output.'),
        dict(calls=['0x18067755a','0x18067757a'],callee='allocation/free',outputs=[],reason='Receive allocator/size or heap allocation only; no caller-local pointer.'),
        dict(calls=['0x1806775f1','0x180677806'],callee='0x18068f380',outputs=[],reason='Reads contact+1c/+20/+24 into floating values, formats allocated/callee-local message. Never writes through contact pointer or passes it onward. Loop-back paths included.'),
        dict(calls=['0x180677693'],callee='virtual260 installed PhysX body4b5e40 or null047f70',outputs=[[0x10f0,0x2004]],reason='Body-specific query with128record bound, concreteRTTI/factory/userData proof below. Count initialized0 eachiteration at677670; caller capsule208 is read-only.'),
        dict(calls=['0x1806776ca'],callee='0x180677920',outputs=[],reason='Same read-only contact/capsule contract as67745e; externalbody receiver.'),
        dict(calls=['0x180677777','0x180677792'],callee='allocation/free',outputs=[],reason='Only allocator/size or heap pointer, no local-output alias.')]
    covered={int(a,16) for e in effects for a in e['calls']}
    actual={i.address for i in body if store.address<i.address<0x180677822 and i.mnemonic=='call'}
    if covered!=actual:raise ValueError('Call-effect scope differs: '+str([hex(x) for x in covered^actual]))
    for e in effects:
        if any(start<0x50 and start+size>0x48 for start,size in e['outputs']):raise ValueError('Callee output overlaps Mission spill')
    # No later branch can revisit the reviewed spill lifetime.
    for ins in body:
        if ins.address<0x180677822 or not ins.group(capstone.CS_GRP_JUMP):continue
        if ins.operands[0].type!=capstone.x86.X86_OP_IMM or ins.operands[0].imm<0x180677822:
            raise ValueError('Later path can revisit reviewed spill lifetime')
    targets={0x1806767d0:[0x18067681d,0x180676828,0x180676846,0x18067685e],
        0x180676bfb:[0x180676c06,0x180676c26],0x1806774d4:[0x1806774d9,0x18067752e,0x18067753e],
        0x180677564:[0x180677569],0x18067757f:[0x18067758c],0x1806776e2:[0x1806776e7]}
    mappings=[]
    for reload,uses in targets.items():
        origin=byaddr[reload];reg=origin.reg_name(origin.operands[0].reg)
        if origin.mnemonic!='mov' or origin.operands[1].mem.disp!=0x48 or origin.reg_name(origin.operands[1].mem.base)!='rsp':
            raise ValueError('Expected Mission spill reload')
        for address in uses:
            ins=byaddr[address]
            immediate=address==0x1806776e7
            if immediate:
                if ins.mnemonic!='add' or ins.op_str!='r13, 0x1044ec8':raise ValueError('Tail pointer ADD changed')
                index=1
            else:index=next(n for n,o in enumerate(ins.operands) if o.type==capstone.x86.X86_OP_MEM and ins.reg_name(o.mem.base)==reg)
            between=[i for i in body if reload<i.address<address]
            aliases={'rdx':('rdx','edx','dx','dl','dh'),'r9':('r9','r9d','r9w','r9b'),'r13':('r13','r13d','r13w','r13b')}[reg]
            if any(any(i.reg_name(r) in aliases for r in i.regs_access()[1]) for i in between):
                raise ValueError('Reload alias overwritten before field')
            calls=[i for i in between if i.mnemonic=='call']
            if any(i.operands[0].type!=capstone.x86.X86_OP_IMM or i.operands[0].imm!=0x1805e9c00 for i in calls):
                raise ValueError('Unproved volatile register crosses call')
            offset,size=(ins.imm_offset,ins.imm_size) if immediate else displacement_encoding(ins)
            mappings.append(dict(address=hex(address),instructionBytes=ins.bytes.hex(),operandIndex=index,
                originalValue=hex(ins.operands[index].imm if immediate else ins.operands[index].mem.disp),missionBiasBytes=0,missionRegister=reg,
                encodedOffset=offset,encodedSize=size,classification='mission-relative-immediate' if immediate else 'mission-relative-field',
                provenanceKind='bounded-6763c0-Mission-stack-spill',provenanceInstructions=[raw(store),raw(origin)]+[raw(i) for i in between],
                evidenceIds=list(dict.fromkeys(entry.get('evidenceIds',[])+[e['evidenceId'] for e in evidence]))))
    vtables=[]
    for rva,method,name in ((0xb04638,0x4b5e40,'.?AVrglPhysX_physics_engine_body@@'),(0xaf3d20,0x47f70,'.?AVrglNull_physics_engine_body@@')):
        locator=struct.unpack('<Q',pe.get_data(rva-8,8))[0]-base
        fields=struct.unpack('<6I',pe.get_data(locator,24))
        actual_name=pe.get_data(fields[3]+16,100).split(b'\0')[0].decode()
        if fields[0]!=1 or fields[1]!=0 or fields[5]!=locator or actual_name!=name or struct.unpack('<Q',pe.get_data(rva+0x260,8))[0]!=base+method:
            raise ValueError('Body RTTI/vtable contract changed')
        vtables.append(dict(vtable=hex(base+rva),locatorRva=hex(locator),locatorBytes=pe.get_data(locator,24).hex(),typeName=name,
            methodSlotRva=hex(rva+0x260),methodSlotBytes=pe.get_data(rva+0x260,8).hex(),method=hex(base+method)))
    query=span(0x1804b5e40,0x1804b667a)
    if next(i for i in query if i.address==0x1804b644f).op_str!='dword ptr [rsi + 0x2000], 0x80':raise ValueError('Body contact bound changed')
    factory=span(0x1803820b0,0x1803825ca)
    if next(i for i in factory if i.address==0x18038252b).op_str!='qword ptr [rdi + 0x10], rsi':raise ValueError('Shape owner write changed')
    body_contact=dict(status='closed-for-installed-PhysX-or-null-body-objects',vtables=vtables,
        bodyConstructorInstructions=[raw(i) for i in span(0x18038f320,0x18038f412)],
        shapeFactoryInstructions=[raw(i) for i in factory],queryInstructions=[raw(i) for i in query],
        nullMethodInstructions=[raw(i) for i in span(0x180047f70,0x180047f73)],
        ownershipProof=['387510 emits contact+0 fromshape+10 whennonzero; otherwiseactor+10 userdata+8.',
          '38f320 installsPhysXbodyvtable and stores its ownpointer intoallocated16byteactoruserdata+8; actor+10 receives that allocation.',
          '3820b0 calls38f320 withnullactor at382186, savesreturnedbodythrough38218b/3821ae, reloadsRSI382291, then writesRSI tocreatedshapeRDI+10 at38252b.',
          'Other retainedbodyfactorypaths construct38f320 withPhysXactor;3828a0 explicitlyzeroesnewshapeuserData beforeactorattachment, usingtheactorfallback.',
          '6763c0 buildsunique localbody listfromcontactrecord+0;677693 callsitsvtable+260. Thisisa bodyquery, nottheworldprovider190.'],
        outputProof='RSI=R8 at4b5edd;CMP[RSI+2000],80/JGE skipsappend. RAX=count<<6+RSI; four16byte stores at0,10,20,30; countincrement2000. Inputcapsule isread-only. NullmethodXORAL;RET hasnowrites.',
        scope='Installed observed PhysX/null factories only; arbitrary foreign shapeuserData or custom body subclasses are excluded from the isolated probe profile.')
    result=dict(schemaVersion=1,status='exact-twelve-sites-proved-under-installed-provider-contract',targetSha256=sha,gameExecuted=False,
        sources=[dict(path=str(p),sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in paths],entryContract=entry,
        frame=dict(rbpMinusRsp=0x100,missionSpill=[0x48,8],store=raw(store),directStackWrites=writes,localAddressMaterializations=addresses),
        callEffects=effects,bitTestLeafProofs=leaves,retainedCalleeEvidence=evidence,
        functionInstructions=[raw(i) for i in body],instructionSiteMappings=mappings,bodyContactQuery=body_contact,deferred=[],
        assumptions=['Valid installed object identities and existing allocator ABI; no arbitrary custom physics provider. This is static pointer/write provenance, not runtime memory-safety certification.'],
        summary=dict(mappedOperands=len(mappings),directCallSitesWithEffects=len(covered),spillOtherDirectWrites=0))
    out=folder/'native-pool-spill-review.json'
    safe_review_output(out,[binary]+paths).write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(result['summary']))

if __name__=='__main__':build()
