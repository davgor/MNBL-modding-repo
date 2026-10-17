"""Independent forward must-analysis of the bounded Missile Mission spill."""
from pathlib import Path
import collections,hashlib,json,sys
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import capstone,pefile
from patch_native_copy import safe_review_output

def main():
    folder=ROOT/'evidence/engine-extension'
    path=folder/'native-pool-missile-spill-review.json';audit=json.loads(path.read_text())
    inv=json.loads((folder/'native-pool-reference-inventory.json').read_text())
    binary=Path(inv['binary']['path']);data=binary.read_bytes()
    if hashlib.sha256(data).hexdigest()!=audit['targetSha256']:raise ValueError('Target mismatch')
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
    records={}
    for p in audit['sources']:
        source=Path(p['path'])
        if source.name.endswith('ghidra-evidence.json'):
            for r in json.loads(source.read_text())['records']:records[r['evidence_id']]=r
    for r in audit['retainedCalleeEvidence']:
        if records[r['evidenceId']]['normalized_result']!=r['normalizedResult']:raise ValueError('Unretained callee text')
    body=list(md.disasm(pe.get_data(0x660c00,0x661753-0x660c00),base+0x660c00))
    by={i.address:i for i in body};queue=collections.deque([body[0].address]);states={body[0].address:frozenset(['rdx'])};steps=0
    aliases={'eax':'rax','ax':'rax','al':'rax','ah':'rax','ebx':'rbx','bx':'rbx','bl':'rbx','bh':'rbx','ecx':'rcx','cx':'rcx','cl':'rcx','ch':'rcx','edx':'rdx','dx':'rdx','dl':'rdx','dh':'rdx','esi':'rsi','si':'rsi','sil':'rsi','edi':'rdi','di':'rdi','dil':'rdi','ebp':'rbp','bp':'rbp','bpl':'rbp','esp':'rsp','sp':'rsp','spl':'rsp'}
    for n in range(8,16):
        for suffix in ('d','w','b'):aliases['r'+str(n)+suffix]='r'+str(n)
    def canonical(r):return aliases.get(r,r)
    while queue:
        a=queue.popleft();steps+=1;i=by[a];before=states[a];after=set(before)
        after.difference_update(canonical(i.reg_name(r)) for r in i.regs_access()[1])
        if i.mnemonic=='call':after.difference_update(['rax','rcx','rdx','r8','r9','r10','r11'])
        if i.mnemonic=='mov' and len(i.operands)==2:
            dst,src=i.operands
            if dst.type==capstone.x86.X86_OP_REG and dst.size==8:
                name=i.reg_name(dst.reg)
                if src.type==capstone.x86.X86_OP_REG and i.reg_name(src.reg) in before:after.add(name)
                elif src.type==capstone.x86.X86_OP_MEM and i.reg_name(src.mem.base)=='rbp' and not src.mem.index and src.mem.disp==-0x68 and 'slot' in before:after.add(name)
            if a==base+0x660c37:
                if 'rdx' not in before:raise ValueError('Initial store not Mission')
                after.add('slot')
        successors=[]
        if i.group(capstone.CS_GRP_JUMP):
            if i.operands[0].type!=capstone.x86.X86_OP_IMM:raise ValueError('Indirect CFG')
            successors.append(i.operands[0].imm)
            if i.mnemonic!='jmp':successors.append(a+i.size)
        elif not i.group(capstone.CS_GRP_RET):successors.append(a+i.size)
        for nxt in successors:
            if nxt not in by:raise ValueError('CFG leaves supplied function')
            merged=frozenset(after) if nxt not in states else states[nxt]&after
            if nxt not in states or merged!=states[nxt]:states[nxt]=merged;queue.append(nxt)
    targets=[]
    for m in audit['instructionSiteMappings']:
        a=int(m['address'],16);i=by[a];op=i.operands[m['operandIndex']]
        if i.reg_name(op.mem.base) not in states[a] or op.mem.disp!=int(m['originalValue'],16):raise ValueError('Unproved target')
        targets.append(m['address'])
    out=dict(status='independently-reviewed-under-installed-component-contract',targetSha256=audit['targetSha256'],reviewedArtifact=dict(path=str(path),sha256=hashlib.sha256(path.read_bytes()).hexdigest()),originalInstructionEntriesVerified=verified,durableCalleeTextsVerified=len(audit['retainedCalleeEvidence']),reachableStates=len(states),worklistSteps=steps,approvedExactSites=targets,method='Independent forward must-analysis: aliases only for full64-bit MOV, canonical partial-register kills, volatile call kills, intersection joins; spill identity begins only at the reviewed entry RDX store. Callee bounded outputs and installed component158 input-only family independently inspected against retained pseudocode/raw helper instructions.',qualification='Depends on the explicit installed component domain and separately reviewed bounded call outputs. This is neither whole-patch nor runtime certification.',gameExecuted=False)
    safe_review_output(folder/'native-pool-missile-spill-independent-review.json',[binary,path]).write_text(json.dumps(out,indent=2)+'\n')
    print(json.dumps(out))

if __name__=='__main__':main()
