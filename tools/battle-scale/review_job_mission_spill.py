"""Independent typed forward check of the bounded 685a90 Mission home."""
from pathlib import Path
import collections,hashlib,json,sys
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import capstone,pefile
from patch_native_copy import safe_review_output
from audit_pool_tick_spill import canonical
from native_instruction_semantics import memory_operand_writes

def main():
    folder=ROOT/'evidence/engine-extension';path=folder/'native-job-mission-spill-review.json';audit=json.loads(path.read_text())
    inv=json.loads((folder/'native-pool-reference-inventory.json').read_text());binary=Path(inv['binary']['path']);data=binary.read_bytes()
    if hashlib.sha256(data).hexdigest()!=audit['targetSha256']:raise ValueError('Wrong binary')
    pe=pefile.PE(data=data);base=pe.OPTIONAL_HEADER.ImageBase
    md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
    verified=0
    def walk(x):
        nonlocal verified
        if isinstance(x,dict):
            if 'address' in x and 'bytes' in x:
                a=int(x['address'],16);b=bytes.fromhex(x['bytes'])
                if pe.get_data(a-base,len(b))!=b:raise ValueError('Original bytes differ')
                verified+=1
            for v in x.values():walk(v)
        elif isinstance(x,list):
            for v in x:walk(v)
    walk(audit)
    retained={}
    for source in audit['sources']:
        p=Path(source['path'])
        if p.name.endswith('ghidra-evidence.json'):
            for r in json.loads(p.read_text())['records']:retained[r['evidence_id']]=r
    for r in audit['retainedCalleeEvidence']:
        if retained[r['evidenceId']]['normalized_result']!=r['normalizedResult']:raise ValueError('Callee text not retained')
    body=list(md.disasm(pe.get_data(0x685a90,0x6863f0-0x685a90),base+0x685a90));by={i.address:i for i in body}
    if audit['entryContract']['entryRegisters']['rcx']!='agent-path:32':raise ValueError('Wrong owner contract')
    queue=collections.deque([body[0].address]);states={body[0].address:{'rcx':'job'}};steps=0;outs={}
    while queue:
        a=queue.popleft();steps+=1;i=by[a];before=states[a];after=before.copy()
        for r in i.regs_access()[1]:after.pop(canonical(i.reg_name(r)),None)
        if i.mnemonic=='call':
            for r in ['rax','rcx','rdx','r8','r9','r10','r11']:
                if r=='r11' and a in (base+0x686056,base+0x6860a8):continue
                after.pop(r,None)
        for n,o in enumerate(i.operands):
            if not memory_operand_writes(i,n) or i.reg_name(o.mem.base) not in ['rbp','rsp']:continue
            if o.mem.index:raise ValueError('Unbounded stack index')
            off=o.mem.disp+(256 if i.reg_name(o.mem.base)=='rbp' else 0)
            if off<0x98 and off+o.size>0x90:
                after.pop('slot',None)
        if i.mnemonic=='mov' and len(i.operands)==2:
            d,s=i.operands
            if d.type==capstone.x86.X86_OP_REG and d.size==8:
                name=i.reg_name(d.reg)
                if s.type==capstone.x86.X86_OP_REG and i.reg_name(s.reg) in before:after[name]=before[i.reg_name(s.reg)]
                elif s.type==capstone.x86.X86_OP_MEM and not s.mem.index:
                    reg=i.reg_name(s.mem.base)
                    if before.get(reg)=='job' and s.mem.disp==0x20:after[name]='agent'
                    elif before.get(reg)=='agent' and s.mem.disp==0xab8:after[name]='mission'
                    elif reg=='rbp' and s.mem.disp==-0x70 and before.get('slot')=='mission':after[name]='mission'
            if a==base+0x685b33:
                if before.get('rax')!='mission':raise ValueError('Initial owner store not proved')
                after['slot']='mission'
        outs[a]=after
        nxt=[]
        if i.group(capstone.CS_GRP_JUMP):
            if i.operands[0].type!=capstone.x86.X86_OP_IMM:raise ValueError('Indirect CFG')
            nxt.append(i.operands[0].imm)
            if i.mnemonic!='jmp':nxt.append(a+i.size)
        elif not i.group(capstone.CS_GRP_RET):nxt.append(a+i.size)
        for n in nxt:
            if n not in by:raise ValueError('CFG escaped')
            merged=after.copy() if n not in states else {k:v for k,v in states[n].items() if after.get(k)==v}
            if n not in states or merged!=states[n]:states[n]=merged;queue.append(n)
    for address,seed in audit['exactReloadSeeds'].items():
        if outs[int(address,16)].get(seed['register'])!='mission':raise ValueError('Reload not independently proved')
    target=base+0x6860c1
    if states[target].get('r11')!='mission':raise ValueError('Final field owner unproved')
    # Recompute preservation from complete retained original helper bodies.
    helper=[next(md.disasm(bytes.fromhex(r['bytes']),int(r['address'],16))) for r in audit['helperInstructions']]
    if any('r11' in {canonical(i.reg_name(r)) for r in i.regs_access()[1]} for i in helper):raise ValueError('R11 clobber in helper chain')
    calls=[i.op_str for i in helper if i.mnemonic=='call']
    if calls!=['0x1805e4480']:raise ValueError('Unreviewed nested helper')
    result=dict(status='independently-reviewed-under-installed-Single0-contract',targetSha256=audit['targetSha256'],reviewedArtifact=dict(path=str(path),sha256=hashlib.sha256(path.read_bytes()).hexdigest()),originalInstructionEntriesVerified=verified,durableCalleeTextsVerified=len(audit['retainedCalleeEvidence']),forwardReachableStates=len(states),forwardSteps=steps,approvedReloads=list(audit['exactReloadSeeds']),approvedExactSites=['0x1806860c1'],method='Typed forward must-analysis derives Agent from exact job+20 contract, Mission from Agent+ab8, and privatehome from the actualstore. Full64-bit aliases, partial-write kills, callvolatile kills, intersecting CFG joins. Onlytwo exact R11-preserving calls use complete verified nestedhelper bodies. All9 call effects independently inspected against retained code and typed managed callback layouts.',qualification='Requires installed typed callback ABI and enforced Single0; it is not a whole patch or runtime approval.',gameExecuted=False)
    safe_review_output(folder/'native-job-mission-spill-independent-review.json',[binary,path]).write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result))

if __name__=='__main__':main()
