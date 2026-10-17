"""Independent exact-body review; original producer artifacts are never written."""
from pathlib import Path
from collections import deque
import hashlib
import importlib
import json
import struct
import sys

ROOT=Path(__file__).resolve().parents[2]
sys.path[:0]=[str(ROOT/'tools/battle-scale'),str(ROOT/'build/battle-scale/python-libs')]
import capstone
import pefile
import unicorn
from unicorn import x86_const as ux
from capstone.x86 import X86_OP_MEM,X86_OP_REG,X86_OP_IMM
from native_instruction_semantics import memory_operand_writes

PINS={4:'e72c5ea12e8a4fec54cd4a5647aa85f799f2ba09c8a624a2eb7ae8539b107559',
      5:'a8bff200f69f9f4ad3886d0a03e37b1c8e8c59fd59d126dc0171b229e845a8d0'}
BASE=0x180000000
SOURCE=ROOT/'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()


def register(i,r):
    n=i.reg_name(r)
    for full,aliases in {'rax':('eax','ax','ah','al'),'rbx':('ebx','bx','bh','bl'),
        'rcx':('ecx','cx','ch','cl'),'rdx':('edx','dx','dh','dl'),'rsi':('esi','si','sil'),
        'rdi':('edi','di','dil'),'rbp':('ebp','bp','bpl'),'rsp':('esp','sp','spl')}.items():
        if n==full or n in aliases:return full
    if n.startswith('r') and n[-1:] in ('d','w','b') and n[1:-1].isdigit():return n[:-1]
    return n


def independent_aliases(ins,edges,entry,forward_calls=(),cookie=None):
    """Strict audit of this actual helper: unsupported tainted ops reject.

    Pointer loads are excluded by disjoint well-formed external objects and the
    separately rejected pointer stores. This is not a general alias solver.
    """
    states={entry:frozenset(['rdx'])};todo=deque([entry]);steps=0;uses=set()
    while todo:
        a=todo.popleft();i=ins[a];before=set(states[a]);after=set(before);ops=i.operands;steps+=1
        assert steps<10000
        if i.mnemonic in ('cdq','cqo'):
            assert 'rax' not in before
            after.discard('rdx')
        elif i.mnemonic=='push':
            assert not (ops[0].type==X86_OP_REG and register(i,ops[0].reg) in before),'pointer pushed'
        elif i.mnemonic=='pop':
            assert ops[0].type==X86_OP_REG
            # Exact helper prolog pushes caller nonvolatile BEFORE pointer copies;
            # no output-pointer store/push is permitted anywhere in this audit.
            after.discard(register(i,ops[0].reg))
        elif i.mnemonic=='call' or i.mnemonic=='jmp' and not edges[a]:
            arguments={'rcx'} if a==cookie else {'rcx','rdx','r8','r9'}
            if a not in forward_calls:assert not before&arguments,(hex(a),before)
            # MAY semantics: unknown callees can preserve volatile values.
            # No caller-owned output may enter any ordinary call here.
            uses.add((a,'cookie' if a==cookie else 'forward' if a in forward_calls else 'no-output-call'))
        else:
            for opno,op in enumerate(ops):
                if op.type==X86_OP_MEM:
                    if memory_operand_writes(i,opno):
                        for source in ops[1:]:
                            assert source.type!=X86_OP_REG or register(i,source.reg) not in before,('pointer store',hex(a))
                    aliases={register(i,r) for r in (op.mem.base,op.mem.index) if r}&before
                    if aliases:
                        assert not op.mem.index and register(i,op.mem.base) in before and i.mnemonic!='lea'
                        limit=16 if memory_operand_writes(i,opno) else (48 if entry!=BASE+0x60c6c0 else 16)
                        assert 0<=op.mem.disp and op.mem.disp+op.size<=limit
                        uses.add((a,'output-memory'))
            if len(ops)>=1 and ops[0].type==X86_OP_REG:
                dest=register(i,ops[0].reg)
                source_alias=len(ops)>1 and ops[1].type==X86_OP_REG and register(i,ops[1].reg) in before
                if i.mnemonic in ('mov','movabs'):
                    if source_alias:
                        assert ops[0].size==ops[1].size==8
                        after.add(dest);uses.add((a,'full-pointer-copy'))
                    elif ops[0].size>=4:after.discard(dest)
                    elif dest in before:raise AssertionError(('partial-pointer-overwrite',hex(a)))
                elif i.mnemonic in ('lea','movzx','movsx','movsxd'):
                    assert not source_alias and ops[0].size>=4
                    after.discard(dest)
                else:
                    writes={register(i,r) for r in i.regs_access()[1]}
                    assert not before&writes,('unsupported pointer write',hex(a),i.mnemonic)
                    for source in ops[1:]:
                        assert source.type!=X86_OP_REG or register(i,source.reg) not in before,('unsupported pointer input',hex(a))
        for t in edges[a]:
            merged=states.get(t,frozenset())|frozenset(after)
            if t not in states or merged!=states[t]:states[t]=merged;todo.append(t)
    assert len(states)==len(ins)
    return {'steps':steps,'instructions':len(ins),'pointerUses':[{'address':hex(a),'kind':k} for a,k in sorted(uses)]}


def cookie_replay(pe,md):
    first=BASE+0x9df5b0;raw=pe.get_data(first-BASE,0x21);ins=list(md.disasm(raw,first))
    global_address=ins[0].address+ins[0].size+ins[0].operands[1].mem.disp
    failure=ins[-1].operands[0].imm
    assert failure==BASE+0x9e0254
    out=[]
    for cookie,argument,valid in [(0,0,True),(0x123456789abc,0x123456789abc,True),
                                  (0xffffffffffff,0xffffffffffff,True),
                                  (0x1234,0x1235,False),(0x1000000000000,0x1000000000000,False)]:
        u=unicorn.Uc(unicorn.UC_ARCH_X86,unicorn.UC_MODE_64)
        u.mem_map(BASE,(pe.OPTIONAL_HEADER.SizeOfImage+4095)&~4095);u.mem_write(BASE,pe.get_memory_mapped_image())
        u.mem_map(0x40000000,0x10000);u.mem_map(0x50000000,0x1000)
        u.mem_write(global_address,struct.pack('<Q',cookie));u.mem_write(0x40008008,struct.pack('<Q',0x50000000))
        u.reg_write(ux.UC_X86_REG_RSP,0x40008008);u.reg_write(ux.UC_X86_REG_RCX,argument)
        saved={name:0x12340000+idx*16 for idx,name in enumerate(('RAX','RDX','RBX','RBP','RSI','RDI','R8','R9','R10','R11','R12','R13','R14','R15'))}
        for name,value in saved.items():u.reg_write(getattr(ux,'UC_X86_REG_'+name),value)
        u.hook_add(unicorn.UC_HOOK_CODE,lambda uc,pc,size,_:uc.emu_stop() if pc in (failure,0x50000000) else None)
        u.emu_start(first,0x50000001,count=100)
        pc=u.reg_read(ux.UC_X86_REG_RIP)
        assert pc==(0x50000000 if valid else failure)
        assert all(u.reg_read(getattr(ux,'UC_X86_REG_'+n))==v for n,v in saved.items())
        out.append({'cookie':hex(cookie),'argument':hex(argument),'normalReturn':valid,'stoppedAt':hex(pc),'preservedGprs':list(saved)})
    return out


def main():
    paths=[Path(__file__),SOURCE];reports={};mods={}
    for v,h in PINS.items():
        p=ROOT/f'evidence/engine-extension/native-remaining-agent-owner-proofs-v{v}.json'
        assert digest(p)==h;paths.append(p);reports[v]=json.loads(p.read_text())
        producer=ROOT/f'tools/battle-scale/audit_remaining_agent_owners_v{v}.py';paths.append(producer)
        mods[v]=importlib.import_module(producer.stem)
        for dep in reports[v]['dependencies']:
            assert digest(dep['path'])==dep['sha256'];paths.append(Path(dep['path']))
    pins={str(p):digest(p) for p in paths}
    # Reuse the read-only original namespace initialization once; each producer
    # gets an independent ordinary transfer binding. Neither main writer runs.
    from prove_native_ai_leaf_owner import read_namespace
    ns=read_namespace();ordinary=ns['transfer'];pe=ns['pe'];md=ns['md']
    replays=[];strict=[]
    for v in (4,5):
        ns['transfer']=ordinary
        original_reader=mods[v].read_namespace
        mods[v].read_namespace=lambda:ns
        try:actual=mods[v].build()
        finally:mods[v].read_namespace=original_reader
        assert actual==reports[v],f'v{v} replay differs'
        replays.append({'version':v,'fullReplayMatches':True,'instructionRows':actual['verifiedOriginalInstructionRows']})
    ns['transfer']=ordinary
    for entry,forward,cookie in [(0x5dbb30,(BASE+0x5dbc9e,),None),(0x5dd420,(),None),
                                  (0x41fd70,(),None),(0x5d9ef0,(BASE+0x5d9f00,),None),
                                  (0x60c6c0,(),BASE+0x60d231)]:
        ins,edges,bad,ind=ns['decode'](BASE+entry);assert not bad and not ind
        result=independent_aliases(ins,edges,BASE+entry,forward,cookie)
        strict.append({'entry':hex(BASE+entry),**result})
    # Preserve the known producer limitations with exact executable negatives.
    gaps=[]
    for name,raw in [('partial-register-survivor','488bc2b000488901c3'),
                     ('implicit-stack-escape','5258488901c3'),
                     ('may-preserved-volatile','488bc2ba00000000e800000000488903c3')]:
        ins={i.address:i for i in md.disasm(bytes.fromhex(raw),0x1000)}
        edges={a:[] if i.mnemonic=='ret' else [a+i.size] for a,i in ins.items()}
        mods[5].prove_output_no_escape(ns,ins,edges,0x1000)
        rejected=False
        try:independent_aliases(ins,edges,0x1000)
        except AssertionError:rejected=True
        assert rejected
        gaps.append({'name':name,'bytes':raw,'v5IncorrectlyAccepts':True,'independentStrictCheckRejects':True,
                     'impact':'Not present on the exact reviewed helper pointer paths; prevents generic checker reuse approval.'})
    cookies=cookie_replay(pe,md)
    rows=set()
    def check(x):
        if isinstance(x,dict):
            if 'address' in x and 'bytes' in x:
                a=int(x['address'],16);raw=bytes.fromhex(x['bytes']);assert pe.get_data(a-BASE,len(raw))==raw;rows.add((a,raw))
            for value in x.values():check(value)
        elif isinstance(x,list):
            for value in x:check(value)
    for report in reports.values():check(report)
    assert pins=={p:digest(p) for p in pins}
    result={'schemaVersion':1,'status':'accepted-exact-v4-v5-owner-contributions-generic-v5-checker-not-approved',
        'sources':pins,'replay':replays,'independentlyVerifiedOriginalRows':len(rows),'independentStrictHelperAudits':strict,
        'cookieNativeReplay':cookies,'genericCheckerFindings':gaps,
        'acceptedMultiplyRvas':['0x6aa081','0x713316'],
        'scope':['Exact frozen native ordinary-execution contributions only, conditional on inherited registered caller contracts.',
            'Private stack/output objects must be disjoint from well-formed external objects and ordinary ABI stack discipline must hold.',
            'V4 iterator writes stay within first16bytes, comparison helper writes only its own frame; exact transitive forwarding remains within reviewed helper set.',
            'V5 output remains in full-width RSI, optional originalRDX residue and finalRAX; no actual partial alias overwrite or pointer push/store occurs.',
            'Cookie helper normal return preserves RAX/RDX. Both mismatched cookie and invalid high bits reach excluded failure tail; no return from failure is assumed.'],
        'limitations':['Generic v5 output checker has three reproduced false-negative classes. Corrected subsequent producer is required for reuse; existing artifacts preserved.',
            'No all-consumer, native bounds, arbitrary-input, exception continuation, hook or game integration approval.'],
        'gameLaunched':False,'enginePatchApproved':False}
    output=ROOT/'evidence/engine-extension/native-remaining-agent-owner-v4-v5-independent-review.json'
    output.write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps({'path':str(output),'sha256':digest(output),'status':result['status']}))


if __name__=='__main__':main()
