"""Exact saved-Mission lifetime and backward CFG review of 5c6f40; no execution."""
from pathlib import Path
import hashlib,json,sys
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import capstone,pefile
from build_pool_patch_manifest import displacement_encoding
from patch_native_copy import safe_review_output
from native_instruction_semantics import memory_operand_writes

def canonical(name):
    families={'rax':['eax','ax','al','ah'],'rbx':['ebx','bx','bl','bh'],
        'rcx':['ecx','cx','cl','ch'],'rdx':['edx','dx','dl','dh'],
        'rsi':['esi','si','sil'],'rdi':['edi','di','dil'],
        'rbp':['ebp','bp','bpl'],'rsp':['esp','sp','spl']}
    for n in range(8,16):families['r'+str(n)]=['r'+str(n)+s for s in ('d','w','b')]
    return next((reg for reg,aliases in families.items() if name in aliases),name)

def backward_proof(body,target,register,store_address,last_reload,spill_base='rsp',spill_displacement=0x70):
    """Every predecessor must reach a reviewed reload, allowing only full MOV aliases."""
    by={i.address:i for i in body};pred={a:[] for a in by}
    for i in body:
        successors=[]
        if i.group(capstone.CS_GRP_JUMP):
            if i.operands[0].type!=capstone.x86.X86_OP_IMM:raise ValueError('Indirect CFG jump')
            successors.append(i.operands[0].imm)
        if i.mnemonic not in ('jmp','ret','retf','int3') and i.address+i.size in by:successors.append(i.address+i.size)
        for successor in successors:
            if successor in pred:pred[successor].append(i.address)
            elif i.group(capstone.CS_GRP_JUMP):raise ValueError('External branch')
    volatile={'rax','rcx','rdx','r8','r9','r10','r11'}
    pending=[(target,register)];seen=set();origins=set();instructions=set()
    while pending:
        address,reg=pending.pop()
        if (address,reg) in seen:continue
        seen.add((address,reg))
        if not pred[address]:raise ValueError('Unproved entry path: '+hex(address)+' '+reg)
        for previous in pred[address]:
            i=by[previous];instructions.add(previous)
            if i.mnemonic=='call' and reg in volatile:raise ValueError('Volatile value crosses call: '+hex(previous))
            writes={canonical(i.reg_name(r)) for r in i.regs_access()[1]}
            if reg not in writes:
                pending.append((previous,reg));continue
            if i.mnemonic=='mov' and i.operands[0].type==capstone.x86.X86_OP_REG and i.operands[0].size==8 and canonical(i.reg_name(i.operands[0].reg))==reg:
                src=i.operands[1]
                if src.type==capstone.x86.X86_OP_REG:
                    pending.append((previous,canonical(i.reg_name(src.reg))));continue
                if src.type==capstone.x86.X86_OP_MEM and not src.mem.index and i.reg_name(src.mem.base)==spill_base and src.mem.disp==spill_displacement and store_address<previous<=last_reload:
                    origins.add(previous);continue
            raise ValueError('Unproved reaching definition: '+hex(previous)+' '+i.mnemonic+' '+i.op_str)
    if not origins:raise ValueError('No originating reload')
    # Prove the sole initial store dominates every reload, not only that it is earlier.
    for origin in origins:
        todo=[origin];visited=set()
        while todo:
            address=todo.pop()
            if address==store_address or address in visited:continue
            visited.add(address)
            if not pred[address]:raise ValueError('Reload bypasses initial Mission store')
            todo.extend(pred[address])
    return sorted(instructions),sorted(origins),len(seen)

def build():
    folder=ROOT/'evidence/engine-extension'
    paths=[folder/n for n in ('native-pool-reference-inventory.json','pool-semantic-review-mid.json','native-closure-ghidra-evidence.json')]
    inventory,mid,bundle=[json.loads(p.read_text()) for p in paths]
    binary=Path(inventory['binary']['path']);source=binary.read_bytes();sha=hashlib.sha256(source).hexdigest()
    if sha!=inventory['binary']['sha256']:raise ValueError('Target hash differs')
    pe=pefile.PE(data=source);base=pe.OPTIONAL_HEADER.ImageBase
    md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
    def span(a,b):
        rows=list(md.disasm(pe.get_data(a-base,b-a),a))
        if sum(i.size for i in rows)!=b-a:raise ValueError('Decode gap')
        return rows
    def raw(i):return dict(address=hex(i.address),bytes=i.bytes.hex(),decoded=i.mnemonic+' '+i.op_str)
    body=span(0x1805c6f40,0x1805c85b3);by={i.address:i for i in body}
    entry=mid['missionMethodFlowReview']['entryContracts']['0x1805c6f40']
    if entry['entryRegisters'].get('rcx')!='mission' or entry['entryBiases'].get('rcx')!=0:raise ValueError('Missing entry contract')
    store_address=0x1805c6fbd;last_reload=0x1805c82a8
    if by[store_address].bytes.hex()!='48894c2470':raise ValueError('Store changed')
    for address,text in ((0x1805c6f40,'rax, rsp'),(0x1805c6f4f,'rbp, [rax - 0x398]'),(0x1805c6f56,'rsp, 0x458')):
        if by[address].op_str!=text:raise ValueError('Frame changed')
    writes=[];addresses=[];calls=[]
    for i in body:
        if store_address<=i.address<=last_reload or i.address in (0x1805c85aa,0x1805c85ac,0x1805c85ad):
            if i.mnemonic=='call':calls.append(raw(i))
            for operand_index,op in enumerate(i.operands):
                if op.type!=capstone.x86.X86_OP_MEM or i.reg_name(op.mem.base) not in ('rsp','rbp'):continue
                if op.mem.index:raise ValueError('Indexed stack reference needs review')
                offset=op.mem.disp+(0x100 if i.reg_name(op.mem.base)=='rbp' else 0)
                if memory_operand_writes(i,operand_index):
                    writes.append(dict(raw(i),stackOffset=offset,byteLength=op.size))
                    if offset<0x78 and offset+op.size>0x70 and i.address!=store_address:raise ValueError('Spill overwritten')
                if i.mnemonic=='lea':
                    addresses.append(dict(raw(i),stackOffset=offset))
                    if 0<=offset<0xa0:raise ValueError('Unreviewed low stack address escape')
        if i.address>last_reload and i.address!=0x1805c85ad and i.group(capstone.CS_GRP_JUMP):
            if i.operands[0].type!=capstone.x86.X86_OP_IMM or i.operands[0].imm<=last_reload:raise ValueError('Late edge reenters saved-slot lifetime')
    # Out-of-line virtual call belongs to the earlier matrix-update loop, not
    # the later post-reload path. Its only incoming branch is7ddb.
    if by[0x1805c85aa].op_str!='rdx' or by[0x1805c85ad].op_str!='0x1805c7df0':raise ValueError('Cold block changed')
    cold_incoming=[i.address for i in body if i.group(capstone.CS_GRP_JUMP) and i.operands[0].type==capstone.x86.X86_OP_IMM and 0x1805c85aa<=i.operands[0].imm<0x1805c85b2]
    if cold_incoming!=[0x1805c7ddb]:raise ValueError('Cold block has another incoming edge')
    evidence=[]
    for address in (0x5c6f40,0x1d3990,0x5dd010,0x05bbb0,0x08e250,0x09f0a0,0x08fe20,0x434860,0x434600,0x4342f0):
        matches=[r for r in bundle['records'] if r.get('operation')=='procedure_pseudo_code' and r.get('parameters',{}).get('procedure')==hex(base+address)]
        if not matches:raise ValueError('Evidence not retained '+hex(address))
        value=matches[-1]
        evidence.append(dict(procedure=hex(base+address),evidenceId=value['evidence_id'],normalizedResult=value['normalized_result']))
    effects=[
        dict(calls=['0x1805c70da','0x1805c72c3'],callee='0x1801d3990',inputs=[[0x150,16],[0x170,16]],outputs=[[0x230,56]],reason='Input position copied/read; output fields0..37 only.434860/434600 forward position to4342f0; retained leaf proof closes readonly input. World virtual178/1b8 get callee-local copies only.'),
        dict(calls=['0x1805c786a'],callee='0x1805dd010',inputs=[[0x118,56]],outputs=[[0x118,44]],reason='Move-append writes source offsets0..2b; remaining stores target heap vector backing. No negative caller-input offsets.'),
        dict(calls=['0x1805c7884'],callee='0x18005bbb0',inputs=[[0x128,24]],outputs=[],reason='Reads local vector begin/end; destructor virtual calls and free receive heap element pointers, not header address.'),
        dict(calls=['0x1805c7c8c'],callee='0x18008e250',inputs=[[0x180,64]],outputs=[[0x330,64]],reason='Call-free inverse; exactly16 output floats and input reads.'),
        dict(calls=['0x1805c7d03'],callee='0x18009f0a0',inputs=[[0x270,64],[0x330,64]],outputs=[[0x370,64]],reason='Call-free multiply writes16 floats, returns output pointer. Caller reads four16-byte chunks then replaces RAX with external vtable before7d3d; no further alias escape.'),
        dict(calls=['0x1805c7d8d'],callee='0x18008fe20',inputs=[[0x2f0,64]],outputs=[],reason='Reads matrix; sole nested08fdd0 receives input+30, has full branch-contained readonly leaf proof below.')]
    for effect in effects:
        for start,size in effect['outputs']:
            if start<0x78 and start+size>0x70:raise ValueError('Bounded output overlaps spill')
    leaf=span(0x18008fdd0,0x18008fe12)
    for i in leaf:
        if i.mnemonic=='call' or any(memory_operand_writes(i,n) for n in range(len(i.operands))):raise ValueError('Readonly leaf changed')
        if i.group(capstone.CS_GRP_JUMP) and (i.operands[0].type!=capstone.x86.X86_OP_IMM or not leaf[0].address<=i.operands[0].imm<0x18008fe12):raise ValueError('Leaf branch escapes')
    targets=[0x5c74e8,0x5c74fa,0x5c76a3,0x5c7726,0x5c78cf,0x5c78d6,0x5c7953,0x5c8308,0x5c8359,0x5c8376,0x5c837d,0x5c83cd,0x5c83da,0x5c83f8,0x5c8422]
    mappings=[];reload_seeds={}
    for rva in targets:
        i=by[base+rva]
        index=next(n for n,o in enumerate(i.operands) if o.type==capstone.x86.X86_OP_MEM and 0x62ca50<=o.mem.disp<=0x1184b40)
        op=i.operands[index];reg=i.reg_name(op.mem.base)
        if op.mem.index:raise ValueError('Indexed operand not reviewed')
        nodes,origins,count=backward_proof(body,i.address,reg,store_address,last_reload)
        offset,size=displacement_encoding(i)
        mappings.append(dict(address=hex(i.address),instructionBytes=i.bytes.hex(),operandIndex=index,originalValue=hex(op.mem.disp),
            missionBiasBytes=0,missionRegister=reg,encodedOffset=offset,encodedSize=size,classification='mission-relative-field',
            provenanceKind='bounded-5c6f40-Mission-stack-spill-all-predecessors',
            provenanceInstructions=[raw(by[store_address])]+[raw(by[a]) for a in nodes],
            reloadOrigins=[hex(a) for a in origins],backwardStateCount=count,
            evidenceIds=list(dict.fromkeys(entry['evidenceIds']+[e['evidenceId'] for e in evidence]))))
        for a in origins:reload_seeds[hex(a)]=dict(register=by[a].reg_name(by[a].operands[0].reg),pointerKind='mission',biasBytes=0,instruction=raw(by[a]))
    result=dict(schemaVersion=1,targetSha256=sha,status='exact-fifteen-sites-proved-under-installed-callee-contracts',gameExecuted=False,
        sources=[dict(path=str(p),sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in paths],entryContract=entry,
        functionStart=hex(body[0].address),functionEndExclusive='0x1805c85b3',functionInstructions=[raw(i) for i in body],
        frame=dict(rbpMinusRsp=256,missionSpill=[0x70,8],initialStore=raw(by[store_address]),lastReload=raw(by[last_reload]),directStackWrites=writes,localAddressMaterializations=addresses),
        callEffects=effects,allCallsDuringSpillLifetime=calls,retainedCalleeEvidence=evidence,readonlyLeafInstructions=[raw(i) for i in leaf],
        callScopeProof='All explicit caller-local pointer arguments before last reload are enumerated in callEffects. Other calls use external objects, heap allocations, scalar arguments, or allocator/import ABI. Cold virtual170 call85aa has only predecessor7ddb, external RCX receiver, and rejoins7df0; it is included despite later address. Saved RAII header addresses are local exception-state records; normal cleanup occurs after last reload. Callee-local writes cannot reach this caller frame under the installed ABI. Arbitrary hooks/providers are excluded.',
        instructionSiteMappings=mappings,exactReloadSeeds=reload_seeds,
        summary=dict(mappedOperands=len(mappings),spillOtherDirectWrites=0,reviewedLocalPointerCalls=sum(len(e['calls']) for e in effects),allCallsDuringSpillLifetime=len(calls)),
        limitations=['Static normal-flow provenance only; not runtime memory safety or arbitrary provider compatibility. Unwind is separately audited.'])
    out=folder/'native-pool-tick-spill-review.json';safe_review_output(out,[binary]+paths).write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(result['summary']))

if __name__=='__main__':build()
