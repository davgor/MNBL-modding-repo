"""Correct v5 MAY aliases; preserve partial registers, spills and call residues.

Supersedes the v5 checker for the same713316 contribution, not another site.
Unsupported pointer operations fail closed. This remains a bounded checker.
"""
from pathlib import Path
from collections import deque
import json
import sys

ROOT=Path(__file__).resolve().parents[2]
sys.path[:0]=[str(ROOT/'tools/battle-scale'),str(ROOT/'build/battle-scale/python-libs')]
from capstone.x86 import X86_OP_REG, X86_OP_IMM, X86_OP_MEM
from native_instruction_semantics import memory_operand_writes
import audit_remaining_agent_owners_v5 as previous

UNKNOWN=1<<100
VOLATILE=('rax','rcx','rdx','r8','r9','r10','r11')


def prove_output_no_escape(ns,ins,edges,root,cookie_call=None):
    # Ordinary register keys hold MAY output aliases; F: keys track concrete
    # frame addresses; S: keys hold eight-byte stack spill aliases.
    def frame(r):return 'F:'+str(r)
    def slot(offset):return 'S:'+str(offset)
    def shifted(values,delta):return frozenset(UNKNOWN if v==UNKNOWN else v+delta for v in values)
    def frame_address(i,mem,state):
        if mem.index:return None
        value=state.get(frame(ns['reg'](i,mem.base))) if mem.base else None
        return shifted(value,mem.disp) if value else None
    def spill_read(addresses,size,state):
        out=frozenset()
        if not addresses:return out
        for key,values in state.items():
            if not key.startswith('S:'):continue
            off=int(key[2:])
            for start in addresses:
                if start==UNKNOWN or start<off+8 and off<start+size:
                    out|=values if start==off and size==8 else frozenset([UNKNOWN])
        return out
    states={root:{'rdx':frozenset([0]),frame('rsp'):frozenset([0])}}
    queue=deque([root]);steps=0
    while queue:
        a=queue.popleft();i=ins[a];before=states[a];after=dict(before);ops=i.operands;steps+=1
        assert steps<20000
        written={ns['reg'](i,r) for r in i.regs_access()[1]}-{None}
        for r in written:
            after.pop(r,None);after.pop(frame(r),None)
        handled=set()
        if i.mnemonic=='call':
            args=('rcx',) if a==cookie_call else ('rcx','rdx','r8','r9')
            assert not any(before.get(r) for r in args),(hex(a),'output passed as consumed argument')
            if a!=cookie_call:
                assert not any(k.startswith('S:') for k in before),(hex(a),'unreviewed stack argument or spill at call')
            after[frame('rsp')]=before[frame('rsp')]
            if a==cookie_call:
                # Exact pinned cookie success path consumes/changesRCX only.
                for r in VOLATILE:
                    if r!='rcx' and r in before:after[r]=before[r]
            else:
                # ABI says volatile values MAY change, never that they must.
                for r in VOLATILE:
                    if r in before:after[r]=before[r]|frozenset([UNKNOWN])
            handled.update(written)
        elif i.mnemonic in ('ret','bnd ret'):
            after[frame('rsp')]=shifted(before[frame('rsp')],8)
            handled.add('rsp')
        elif i.mnemonic=='push':
            sp=before[frame('rsp')];assert UNKNOWN not in sp
            new=shifted(sp,-8);after[frame('rsp')]=new
            source=before.get(ns['reg'](i,ops[0].reg)) if ops[0].type==X86_OP_REG else None
            for off in new:
                if source:after[slot(off)]=source
                else:after.pop(slot(off),None)
            handled.add('rsp')
        elif i.mnemonic=='pop':
            sp=before[frame('rsp')];assert UNKNOWN not in sp
            assert ops[0].type==X86_OP_REG and ops[0].size==8
            dest=ns['reg'](i,ops[0].reg);values=frozenset().union(*(before.get(slot(off),frozenset()) for off in sp))
            if values:after[dest]=values
            for off in sp:after.pop(slot(off),None)
            after[frame('rsp')]=shifted(sp,8);handled.update(('rsp',dest))
        elif i.mnemonic in ('mov','movabs') and len(ops)==2 and ops[0].type==X86_OP_REG:
            dest=ns['reg'](i,ops[0].reg);src=ops[1];value=None;fv=None
            if src.type==X86_OP_REG:
                value=before.get(ns['reg'](i,src.reg));fv=before.get(frame(ns['reg'](i,src.reg)))
            elif src.type==X86_OP_MEM:
                addresses=frame_address(i,src.mem,before)
                if addresses:
                    value=spill_read(addresses,src.size,before)
            if ops[0].size==8:
                if value:after[dest]=value
                if fv:after[frame(dest)]=fv
            elif value or before.get(dest):
                # Conservatively retain a possible address after any partial
                # write. Even a byte write can leave most of the pointer intact.
                after[dest]=frozenset([UNKNOWN])
            handled.add(dest)
        elif i.mnemonic=='lea' and len(ops)==2 and ops[0].type==X86_OP_REG:
            dest=ns['reg'](i,ops[0].reg);mem=ops[1].mem
            addresses=frame_address(i,mem,before)
            if addresses:after[frame(dest)]=addresses
            bv=before.get(ns['reg'](i,mem.base)) if mem.base else None
            iv=before.get(ns['reg'](i,mem.index)) if mem.index else None
            if bv or iv:
                assert bv and not mem.index and ops[0].size==8
                after[dest]=shifted(bv,mem.disp)
            handled.add(dest)
        elif i.mnemonic in ('add','sub') and len(ops)==2 and ops[0].type==X86_OP_REG and ops[1].type==X86_OP_IMM:
            dest=ns['reg'](i,ops[0].reg);delta=ops[1].imm*(1 if i.mnemonic=='add' else -1)
            if before.get(dest):after[dest]=shifted(before[dest],delta) if ops[0].size==8 else frozenset([UNKNOWN])
            if before.get(frame(dest)) and ops[0].size==8:after[frame(dest)]=shifted(before[frame(dest)],delta)
            handled.add(dest)
        # Every other explicit read of a possible output alias is rejected when
        # it can transform a register value. Supported writes through the alias
        # below are confined to the16-byte output; comparisons do not copy it.
        read_regs={ns['reg'](i,r) for r in i.regs_access()[0]}
        tainted_reads={r for r in read_regs if r in before}
        address_regs={ns['reg'](i,r) for op in ops if op.type==X86_OP_MEM for r in (op.mem.base,op.mem.index) if r}
        explicit_regs={ns['reg'](i,op.reg) for op in ops if op.type==X86_OP_REG}
        tainted_reads-=address_regs-explicit_regs
        if i.mnemonic not in ('call','push','pop','mov','movabs','lea','add','sub','cmp','test','ret','bnd ret'):
            assert not tainted_reads,(hex(a),'unsupported alias operation',i.mnemonic,i.op_str,tainted_reads)
        for r in written-handled:
            if before.get(r):after[r]=frozenset([UNKNOWN])
        for ix,op in enumerate(ops):
            if op.type!=X86_OP_MEM:continue
            mem=op.mem;bv=before.get(ns['reg'](i,mem.base)) if mem.base else None
            iv=before.get(ns['reg'](i,mem.index)) if mem.index else None
            if bv or iv:
                assert bv and not mem.index,(hex(a),'indexed output address')
                if i.mnemonic!='lea':assert all(v!=UNKNOWN and 0<=v+mem.disp and v+mem.disp+op.size<=16 for v in bv),(hex(a),'unbounded output access')
            addresses=frame_address(i,mem,before)
            loaded=spill_read(addresses,op.size,before)
            if loaded and not memory_operand_writes(i,ix) and i.mnemonic!='lea':
                assert i.mnemonic in ('mov','movabs') and ix==1 and ops[0].type==X86_OP_REG and ns['reg'](i,ops[0].reg), (hex(a),'unsupported pointer spill read')
            if not memory_operand_writes(i,ix):continue
            source=before.get(ns['reg'](i,ops[1].reg)) if len(ops)>1 and ops[1].type==X86_OP_REG else None
            if loaded:assert i.mnemonic.startswith(('mov','vmov')),(hex(a),'unsupported read-modify-write of pointer spill')
            if source:
                assert addresses and UNKNOWN not in addresses,(hex(a),'output pointer escaped')
                assert op.size==8,(hex(a),'partial pointer spill unsupported')
                for off in addresses:after[slot(off)]=source
            elif addresses and UNKNOWN not in addresses:
                for key,aliases in list(after.items()):
                    if not key.startswith('S:'):continue
                    off=int(key[2:])
                    for start in addresses:
                        if start<off+8 and off<start+op.size:
                            if len(addresses)==1 and start<=off and off+8<=start+op.size:after.pop(key,None)
                            else:after[key]=aliases|frozenset([UNKNOWN])
        assert frame('rsp') in after,(hex(a),'unhandled stack pointer change')
        for target in edges[a]:
            old=states.get(target,{})
            merged={k:old.get(k,frozenset())|after.get(k,frozenset()) for k in old.keys()|after.keys()}
            merged={k:v for k,v in merged.items() if v}
            if target not in states or merged!=old:states[target]=merged;queue.append(target)
    assert len(states)==len(ins)
    return {a:{k:v for k,v in s.items() if not k.startswith('F:')} for a,s in states.items()},steps


def build():
    prior_path=ROOT/'evidence/engine-extension/native-remaining-agent-owner-proofs-v5.json'
    assert previous.digest(prior_path)=='a8bff200f69f9f4ad3886d0a03e37b1c8e8c59fd59d126dc0171b229e845a8d0'
    previous.prove_output_no_escape=prove_output_no_escape
    result=previous.build()
    result['status']='corrected-MAY-private-home-owner-contribution-pending-independent-review'
    result['supersedes']={'path':str(prior_path),'sha256':previous.digest(prior_path),
        'scope':'Same713316 contribution, with conservative partial-register, implicit/explicit stack and volatile-call MAY handling. No extra candidate counted.'}
    result['additionalProven']=1
    result['additionalBeyondV5']=0
    deps={d['path']:d['sha256'] for d in result['dependencies']}
    deps[str(prior_path)]=previous.digest(prior_path);deps[str(Path(__file__))]=previous.digest(__file__)
    result['dependencies']=[{'path':p,'sha256':h} for p,h in sorted(deps.items())]
    result['outputHelper']['mayUnknownBiasSentinel']=UNKNOWN
    result['outputHelper']['maySemantics']='Partial writes retain unknown pointer bias; push/pop and full stack spills retain aliases; generic calls retain possible volatile aliases instead of deleting them. Unsupported operations reject.'
    ns=previous.read_namespace();start=0x1000
    fixtures=[
        ('partial-AL-write-escape','488bc2b000488901c3'),
        ('implicit-push-pop-escape','5258488901c3'),
        ('generic-call-may-preserve-RAX','488bc2ba00000000e800000000488903c3'),
        ('explicit-stack-spill-reload-escape','4883ec2048891424488b0424488901c3'),
        ('partial-spill-overwrite-escape','524889e0c6000058488901c3'),
        ('misaligned-spill-read-escape','52488b442401488901c3'),
        ('SIMD-spill-read-rejects','52660f6f042466480f7ec0488901c3'),
        ('stack-read-modify-write-rejects','52488304240158488901c3'),
        ('stack-argument-escape-rejects','4889542420ba00000000e800000000c3')]
    for name,raw in fixtures:
        fi={i.address:i for i in ns['md'].disasm(bytes.fromhex(raw),start)}
        assert sum(i.size for i in fi.values())==len(bytes.fromhex(raw))
        fe={a:[] if i.mnemonic=='ret' else [a+i.size] for a,i in fi.items()}
        try:prove_output_no_escape(ns,fi,fe,start)
        except AssertionError as error:reason=str(error)
        else:raise AssertionError(name)
        result['negativeBoundaryChecks'].append({'name':name,'bytes':raw,'passed':True,'rejection':reason})
    result['limitations'].append('The bounded MAY checker rejects unsupported pointer transformations; it is not a general x64 alias analyzer or arbitrary native exception model.')
    for p,h in deps.items():assert previous.digest(p)==h,p
    return result


if __name__=='__main__':
    result=build();dest=ROOT/'evidence/engine-extension/native-remaining-agent-owner-proofs-v6.json'
    dest.write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps({'path':str(dest),'sha256':previous.digest(dest),'sameContributionAsV5':True,'remaining':result['remainingUnresolvedCount']}))
