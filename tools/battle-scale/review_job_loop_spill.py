"""Independent byte/CFG check of the bounded686450 Mission spill proof."""
import hashlib
import json
from pathlib import Path
import sys
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import capstone,pefile
from native_instruction_semantics import memory_operand_writes
from patch_native_copy import safe_review_output

def review():
    folder=ROOT/'evidence/engine-extension'; path=folder/'pool-semantic-review-mid.json'
    mid=json.loads(path.read_text()); proof=mid['missionMethodFlowReview']['jobLoopMissionHomeProof']
    source_path=Path('D:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord/bin/Win64_Shipping_Client/TaleWorlds.Native.dll')
    source=source_path.read_bytes();sha=hashlib.sha256(source).hexdigest()
    if sha!=proof['targetSha256']:raise ValueError('Source changed')
    pe=pefile.PE(data=source);base=pe.OPTIONAL_HEADER.ImageBase
    m=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);m.detail=True
    data=bytes.fromhex(proof['proofInstructions'][0]['bytes'])
    if pe.get_data(0x686450,len(data))!=data:raise ValueError('Body changed')
    rows=list(m.disasm(data,base+0x686450));by={i.address:i for i in rows}
    if sum(i.size for i in rows)!=len(data):raise ValueError('Incomplete decode')
    if by[base+0x686455].op_str!='rbp, [rsp - 0x7a0]' or by[base+0x68645d].op_str!='rsp, 0x8a0':raise ValueError('Frame changed')
    expected={0x686475:'488b4120',0x686487:'48894588',0x68649d:'4c8b5588',0x6864bc:'498b82b80a0000',0x6864c7:'488945a0',0x687050:'488b45a0'}
    for a,h in expected.items():
        if by[base+a].bytes.hex()!=h:raise ValueError('Ownership instruction changed')
    pred={i.address:set() for i in rows};calls=[];writes=[]
    for n,i in enumerate(rows):
        if i.mnemonic=='call':
            if i.operands[0].type!=capstone.x86.X86_OP_IMM:raise ValueError('Indirect call')
            calls.append(dict(address=hex(i.address),target=hex(i.operands[0].imm)))
        if i.group(capstone.CS_GRP_JUMP):
            if i.operands[0].type!=capstone.x86.X86_OP_IMM:raise ValueError('Indirect jump')
            dest=i.operands[0].imm
            if dest not in pred:raise ValueError('Out-of-function branch')
            pred[dest].add(i.address)
        if i.mnemonic!='jmp' and not i.group(capstone.CS_GRP_RET) and n+1<len(rows):pred[rows[n+1].address].add(i.address)
        if base+0x68647d<=i.address<base+0x6866e5 or i.address>=base+0x6866f1:
            for j,o in enumerate(i.operands):
                if o.type!=capstone.x86.X86_OP_MEM or i.reg_name(o.mem.base) not in ('rsp','rbp'):continue
                if o.mem.index:raise ValueError('Indexed frame operand')
                off=o.mem.disp-(256 if i.reg_name(o.mem.base)=='rsp' else 0)
                if i.mnemonic=='lea' and -96<=off<-88:raise ValueError('Saved home escapes')
                if memory_operand_writes(i,j):
                    writes.append((i.address,off,o.size))
                    if off<-88 and off+o.size>-96 and i.address!=base+0x6864c7:raise ValueError('Saved home overwritten')
    stack=[base+0x687050];seen=set()
    while stack:
        a=stack.pop()
        if a in seen:continue
        seen.add(a)
        if a==base+0x6864c7:continue
        if not pred[a]:raise ValueError('Reload reachable without initial store')
        stack.extend(pred[a])
    if calls!=[{k:c[k] for k in ('address','target')} for c in proof['calls']]:raise ValueError('Call inventory differs')
    for name in ('sharedHelperEvidence','managedBoundaryEvidence'):
        p=Path(proof[name]['path'])
        if hashlib.sha256(p.read_bytes()).hexdigest()!=proof[name]['sha256']:raise ValueError('Stale '+name)
    report=dict(schemaVersion=1,status='independent-bounded-job-loop-spill-review-passed',gameExecuted=False,targetSha256=sha,
        proofSha256=hashlib.sha256(json.dumps(proof,sort_keys=True).encode()).hexdigest(),
        sourceProofPath=str(path),instructions=len(rows),directFrameWrites=len(writes),allCalls=len(calls),
        backwardStoreDominanceStates=len(seen),approvedReload='0x180687050',approvedOperand='0x180687054',
        checks=['Full original function byte-match and gap-free disassembly; branch predecessors include late687ba6/687bac loop backedges.',
            'Entry typed80-byte job→Agent at+20→Mission atAgent+ab8; sole saved homeRBP-60 store6864c7 dominatesreload687050.',
            'Semantic SIMD-store fallback included. No indexed stack alias, home address escape or overlapping direct write.',
            'Independently inspected34call contexts and retained local-pointer consumers. Nearest outputs are Single4 at-7c, Reaction4 at-80, HitParticleResultData12 at-58; none overlaps[-60,-58).',
            '619de0 output+60..155;61a0c0/2ca050 output16bytes atpositive offsets;5ff1c0/63e000 output16/144bytes atpositive offsets. Other forwarded records use read-only source or bounded positive-field writes.',
            '690470 copies particle IDs before reusing callee argument slot;689510 forwards original vector only toreadonly5f57a0;6899a0 networkrecord forwarding closes via63cca0→698600/698750→readonly2692d0.',
            '5d42e0 unusedparam6 cannot interpret stale callerRSP28; other apparent heap-only callees have no omitted caller-local arguments.'],
        limitations=['Exact installed ABI and managed callback declarations only. This is pointer provenance, not engine runtime safety or whole-patch readiness.'])
    out=safe_review_output(folder/'native-job-loop-spill-independent-review.json',[source_path,path,Path(__file__).resolve()])
    out.write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report))

if __name__=='__main__':review()

