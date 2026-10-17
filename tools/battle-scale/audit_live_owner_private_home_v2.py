"""Corrected, execution-free 6f2770 argument and private-home audit.

The historical receipt is input evidence, not an authorization. Its false
698570 stack-output annotation is explicitly superseded. No native hook runs.
"""
from pathlib import Path
import argparse, copy, hashlib, json, struct, sys
from collections import defaultdict, deque
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import capstone, pefile
from capstone.x86 import X86_OP_REG, X86_OP_MEM, X86_OP_IMM, X86_REG_RIP
from native_instruction_semantics import memory_operand_writes
from patch_native_copy import safe_review_output
BASE=0x180000000
SOURCE=ROOT/'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
OLD=ROOT/'evidence/engine-extension/native-live-owner-private-home.json'
SHA='2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
OLD_SHA='9f6df1b349af07ebd06d08adfc16e9ee6b0eceb20ee31246f4469f9ccced5c06'
FRAME=-0x1d8
def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def basic(i):return dict(address=hex(i.address),bytes=i.bytes.hex(),mnemonic=i.mnemonic,operands=i.op_str)
def reg(i,r):
    n=i.reg_name(r)
    aliases={'eax':'rax','ax':'rax','al':'rax','ah':'rax','ebx':'rbx','bx':'rbx','bl':'rbx','bh':'rbx','ecx':'rcx','cx':'rcx','cl':'rcx','ch':'rcx','edx':'rdx','dx':'rdx','dl':'rdx','dh':'rdx','esp':'rsp','sp':'rsp','ebp':'rbp','bp':'rbp','esi':'rsi','si':'rsi','sil':'rsi','edi':'rdi','di':'rdi','dil':'rdi'}
    if n in aliases:return aliases[n]
    if n.startswith('r') and n[-1:] in ('b','w','d') and n[1:-1].isdigit():return n[:-1]
    return n
def value(kind,bias=0,defs=()):return (kind,bias,frozenset(defs))
def plus(a,b):
    if not a or not b:return None
    if a[0]=='constant':a,b=b,a
    defs=a[2]|b[2]
    if b[0]=='constant':return value(a[0],a[1]+b[1],defs)
    if {a[0],b[0]}=={'record-array','record-index'}:return value('external-record',a[1]+b[1],defs)
    return None
def address(i,m,s):
    out=value('constant',m.disp)
    if m.base:out=plus(out,value('constant',i.address+i.size) if m.base==X86_REG_RIP else s.get(reg(i,m.base)))
    if m.index:
        v=s.get(reg(i,m.index))
        if m.scale!=1:v=value('constant',v[1]*m.scale,v[2]) if v and v[0]=='constant' else None
        out=plus(out,v)
    return out

def build():
    assert digest(SOURCE)==SHA and digest(OLD)==OLD_SHA
    old=json.loads(OLD.read_text());pe=pefile.PE(data=SOURCE.read_bytes());md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
    ins={}
    def verify_rows(rows):
        for row in rows:
            at=int(row['address'],16);raw=bytes.fromhex(row['bytes']);assert pe.get_data(at-BASE,len(raw))==raw
            ds=list(md.disasm(raw,at));assert len(ds)==1 and ds[0].size==len(raw)
            assert basic(ds[0])==row
    verify_rows(old['fullFunctionInstructions']);verify_rows(old['leaf698570'])
    for rows in old['helperInstructions'].values():verify_rows(rows)
    for row in old['fullFunctionInstructions']:ins[int(row['address'],16)]=next(md.disasm(bytes.fromhex(row['bytes']),int(row['address'],16)))
    tables={}
    for t in old['switchProof']:
        raw=bytes.fromhex(t['tableHex']);code=bytes.fromhex(t['dispatchHex'])
        assert pe.get_data(t['tableStart']-BASE,len(raw))==raw
        assert pe.get_data(t['guardStart']-BASE,len(code))==code
        tables[t['dispatch']]=[BASE+x for x in struct.unpack('<16I',raw)]
    succ={};pred=defaultdict(set)
    for a,i in ins.items():
        nxt=a+i.size
        if a in tables:targets=tables[a]
        elif i.mnemonic in ('ret','int3','ud2'):targets=[]
        elif a==BASE+0x6f33f3:
            assert i.bytes.hex()=='90' and ins[a-1].mnemonic=='ret';targets=[]
        elif i.mnemonic.startswith('j'):
            assert i.operands[0].type==X86_OP_IMM
            targets=[i.operands[0].imm]+([] if i.mnemonic=='jmp' else [nxt])
        else:targets=[nxt]
        assert all(t in ins for t in targets),(hex(a),targets)
        succ[a]=targets
        for t in targets:pred[t].add(a)
    reloads=[BASE+0x6f2928,BASE+0x6f2afd];life=set();todo=list(reloads)
    while todo:
        a=todo.pop()
        if a in life:continue
        life.add(a);todo.extend(pred[a])
    assert life=={int(x['address'],16) for x in old['lifetimeInstructions']}
    assert BASE+0x6f2f49 not in life
    effects={int(x['instruction']['address'],16):copy.deepcopy(x) for x in old['callEffects']}
    effects[BASE+0x6f2cfe]['outputs']=[]
    # Independent pointer-only must-flow. Incoming packet/vector/descriptor
    # objects are explicit well-formed ABI preconditions, never Mission guesses.
    # Only the exact original input homes entryRSP+10/+18 survive calls. All
    # other slot facts are discarded; their no-alias obligations are checked
    # against every actual pointer argument and callee local-output interval.
    def transfer(i,s):
        out=dict(s);ops=i.operands
        for r in i.regs_access()[1]:out.pop(reg(i,r),None)
        for ix,o in enumerate(ops):
            if o.type!=X86_OP_MEM or not memory_operand_writes(i,ix):continue
            ea=address(i,o.mem,s)
            if ea and ea[0]=='stack':
                for k in list(out):
                    if isinstance(k,tuple) and k[0]=='slot' and k[1]<ea[1]+o.size and ea[1]<k[1]+8:out.pop(k)
        if i.mnemonic=='call':
            out['rsp']=s['rsp']
            for r in ('rax','rcx','rdx','r8','r9','r10','r11','xmm0','xmm1','xmm2','xmm3','xmm4','xmm5'):out.pop(r,None)
            for k in list(out):
                if isinstance(k,tuple) and k[0]=='slot' and k[1] not in (16,24):out.pop(k)
            return out
        if i.mnemonic=='push':out['rsp']=plus(s.get('rsp'),value('constant',-8));return out
        if i.mnemonic=='pop':out['rsp']=plus(s.get('rsp'),value('constant',8));return out
        v=None
        if i.mnemonic in ('mov','movabs') and len(ops)==2:
            src=ops[1]
            if src.type==X86_OP_REG and src.size==8:v=s.get(reg(i,src.reg))
            elif src.type==X86_OP_IMM:v=value('constant',src.imm)
            elif src.type==X86_OP_MEM and src.size==8:
                ea=address(i,src.mem,s)
                if ea and ea[0]=='stack':v=s.get(('slot',ea[1]))
                elif ea and ea[:2]==('record-vector',0):v=value('record-array',0,ea[2])
            if v:
                v=value(v[0],v[1],v[2]|{i.address})
                if ops[0].type==X86_OP_REG and ops[0].size==8:out[reg(i,ops[0].reg)]=v
                elif ops[0].type==X86_OP_MEM and ops[0].size==8:
                    ea=address(i,ops[0].mem,s)
                    if ea and ea[0]=='stack':out[('slot',ea[1])]=v
        elif i.mnemonic=='xor' and len(ops)==2 and ops[0].type==ops[1].type==X86_OP_REG and ops[0].reg==ops[1].reg and ops[0].size in (4,8):
            out[reg(i,ops[0].reg)]=value('constant',0,[i.address])
        elif i.mnemonic=='lea' and ops[0].size==8:
            v=address(i,ops[1].mem,s)
        elif i.mnemonic in ('add','sub') and len(ops)==2 and ops[0].type==X86_OP_REG and ops[0].size==8:
            b=s.get(reg(i,ops[1].reg)) if ops[1].type==X86_OP_REG else value('constant',ops[1].imm) if ops[1].type==X86_OP_IMM else None
            if i.mnemonic=='sub':b=value('constant',-b[1],b[2]) if b and b[0]=='constant' else None
            v=plus(s.get(reg(i,ops[0].reg)),b)
        elif i.mnemonic=='imul' and len(ops)==3 and ops[0].size==8 and ops[2].type==X86_OP_IMM and ops[2].imm==0xe0:
            v=value('record-index',0,[i.address])
        if v and ops[0].type==X86_OP_REG and ops[0].size==8:out[reg(i,ops[0].reg)]=value(v[0],v[1],v[2]|{i.address})
        return out
    def merge(a,b):
        out={}
        for k in a.keys()&b.keys():
            x,y=a[k],b[k]
            if x[:2]==y[:2]:out[k]=value(x[0],x[1],x[2]|y[2])
            elif x[:2]==('constant',0) and y[:2] in (('external-record',0),('nullable-external-record',0)) or y[:2]==('constant',0) and x[:2] in (('external-record',0),('nullable-external-record',0)) or {x[0],y[0]}=={'external-record','nullable-external-record'} and x[1]==y[1]==0:out[k]=value('nullable-external-record',0,x[2]|y[2])
            elif x[0]==y[0]=='constant' and BASE<=x[1]<BASE+pe.OPTIONAL_HEADER.SizeOfImage and BASE<=y[1]<BASE+pe.OPTIONAL_HEADER.SizeOfImage:out[k]=value('static-image',0,x[2]|y[2])
            elif {x[0],y[0]}=={'constant','static-image'} and all(v[0]=='static-image' or BASE<=v[1]<BASE+pe.OPTIONAL_HEADER.SizeOfImage for v in (x,y)):out[k]=value('static-image',0,x[2]|y[2])
        return out
    entry=BASE+0x6f2770
    states={entry:{'rsp':value('stack'),'rcx':value('packet'),'rdx':value('record-vector'),'r8':value('descriptor')}};q=deque([entry]);steps=0
    while q:
        a=q.popleft();out=transfer(ins[a],states[a]);steps+=1;assert steps<50000
        for t in succ[a]:
            if t not in life:continue
            nxt=out if t not in states else merge(states[t],out)
            if t not in states or nxt!=states[t]:states[t]=nxt;q.append(t)
    assert life<=states.keys()
    # All consumed pointer arguments, including formerly unclassified external
    # arguments. Unused volatile register residues are recorded but not treated
    # as ABI arguments. These callees have no fifth-or-later arguments.
    contracts={}
    def add(rvas,args):
        for r in rvas:contracts[BASE+r]=args
    add([0x6f27f3,0x6f2ec0],{'rcx':('packet',0),'rdx':('stack',FRAME+0x24)})
    add([0x6f2898,0x6f2972],{'rcx':('record-vector',0)})
    for call,off,bias in [(0x6f2a04,0x38,0x18),(0x6f2a19,0x3c,0x24),(0x6f2a2e,0x40,0x30)]:add([call],{'rcx':('packet',0),'rdx':('stack',FRAME+off),'r8':('descriptor',bias)})
    add([0x6f2a53],{'rcx':('descriptor',0),'rdx':('packet',0),'r8':('stack',FRAME+0x48),'r9':('stack',FRAME+0x38)})
    add([0x6f2a8e,0x6f2aa2,0x6f2ad3,0x6f2e11,0x6f2e28,0x6f2e3f],{'rcx':('packet',0),'rdx':('external-record',0),'r8':('image-descriptor',0)})
    add([0x6f2cfe],{'rcx':('external-record',0x10)})
    add([0x6f2d0a],{'rcx':('packet',0),'rdx':('external-record',0x10)})
    add([0x6f2d21],{'rcx':('stack',FRAME+0x70),'rdx':('external-record',0x10)})
    add([0x6f2d80],{'rcx':('packet',0),'rdx':('external-record',0xa0)})
    add([0x6f2dd6],{'rcx':('stack',FRAME+0x118),'rdx':('external-record',0xa8)})
    add([0x6f2deb],{'rcx':('stack',FRAME+0x128),'rdx':('external-record',0xb8)})
    assert contracts.keys()==effects.keys()
    for a,args in contracts.items():
        for r,expected in args.items():
            actual=states[a].get(r)
            ok=actual and (actual[:2]==expected or expected[0]=='image-descriptor' and (actual[0]=='static-image' or actual[0]=='constant' and BASE<=actual[1]<BASE+pe.OPTIONAL_HEADER.SizeOfImage))
            assert ok,(hex(a),r,expected,actual)
        e=effects[a];e.pop('knownLocalRegisterArguments',None)
        e['reachingPointerArguments']={r:{'kind':states[a][r][0],'bias':states[a][r][1],'steadyRspOffset':states[a][r][1]-FRAME if states[a][r][0]=='stack' else None,'provenanceInstructions':[basic(ins[x]) for x in sorted(states[a][r][2])]} for r in args}
        e['localOutputs']=e.pop('outputs');e['localReadOnlyInputs']=e.pop('readOnlyInputs')
        e['externalOutputs']=[]
        e['scalarArguments']=[]
        if a in (BASE+0x6f2898,BASE+0x6f2972):
            defs=[0x6f285d,0x6f2892] if a==BASE+0x6f2898 else [0x6f2963,0x6f296c]
            expected=['r9d, [r13 + 1]','edx, r9d'] if a==BASE+0x6f2898 else ['r9d, [r14 + 1]','edx, r9d']
            assert all(ins[BASE+x].op_str==op for x,op in zip(defs,expected))
            e['scalarArguments']=[{'register':'edx','meaning':'32-bit requested vector element count, index+1','provenanceInstructions':[basic(ins[BASE+x]) for x in defs]}]
        if a==BASE+0x6f2d21:
            assert ins[BASE+0x6f2d15].op_str=='r8b, 1'
            e['scalarArguments']=[{'register':'r8b','value':1,'provenanceInstructions':[basic(ins[BASE+0x6f2d15])]}]
        e['higherStackArguments']='None in the retained callees; recovered signatures and raw helper bodies cover at most RCX/RDX/R8/R9.'
        target=e['target']
        if target in ('746070','63e220') and args['rdx'][0]=='external-record':e['externalOutputs']=[{'register':'rdx','recordOffset':0,'length':4}]
        if target=='698570':e['externalOutputs']=[{'register':'rcx','recordOffset':0x10,'length':0x90,'effect':'reset original vector record; does not write copied stack snapshot'}]
        if target in ('6ec2d0','6ec7d0'):e['externalOutputs']=[{'register':'rdx','recordOffset':args['rdx'][1],'effect':'original record decode; not a caller-frame output'}]
        if target=='6491f0':e['externalOutputs']=[{'register':'rcx','effect':'incoming vector header/backing allocation may resize'}]
        actual_local={r:v[1]-FRAME for r,v in states[a].items() if isinstance(r,str) and r in args and v[0]=='stack'}
        allowed=e['localOutputs']+e['localReadOnlyInputs']
        assert all(any(lo<=off<lo+n for lo,n in allowed) for off in actual_local.values()),(hex(a),actual_local)
        for lo,n in e['localOutputs']:
            assert not(lo<0x38 and 0x30<lo+n)
            assert all(not(FRAME+lo<home+8 and home<FRAME+lo+n) for home in (16,24))
        # Re-audit ALL memory writes, including aliases through RAX and SIMD stores,
    # and all materialized caller-local pointers. Original input homes are
    # preserved only because these checks and all actual callee output intervals
    # exclude them, not because calls are assumed memory-pure.
    stores=[];leas=[]
    protected={FRAME+0x30:BASE+0x6f27c9,16:BASE+0x6f277b,24:BASE+0x6f2777}
    for a in sorted(life):
        i=ins[a];st=states[a]
        for ix,op in enumerate(i.operands):
            if op.type!=X86_OP_MEM:continue
            ea=address(i,op.mem,st)
            if i.mnemonic=='lea' and ea and ea[0]=='stack':
                assert not any(home<=ea[1]<home+8 for home in protected),(hex(a),ea)
                leas.append({'instruction':basic(i),'steadyRspOffset':ea[1]-FRAME})
            if not memory_operand_writes(i,ix):continue
            assert ea and ea[0] in ('stack','external-record','nullable-external-record'),(hex(a),i.op_str,ea)
            if ea[0]=='stack':
                for home,writer in protected.items():
                    assert not(ea[1]<home+8 and home<ea[1]+op.size) or a==writer,(hex(a),home)
            stores.append({'instruction':basic(i),'destinationKind':ea[0],'bias':ea[1],'length':op.size,'provenanceInstructions':[basic(ins[x]) for x in sorted(ea[2])]})
    must={entry:False};q=deque([entry])
    while q:
        a=q.popleft();out=must[a] or a==BASE+0x6f27c9
        for t in succ[a]:
            if t not in life:continue
            v=out if t not in must else must[t] and out
            if t not in must or v!=must[t]:must[t]=v;q.append(t)
    assert all(must[a] for a in reloads)
    assert ins[BASE+0x6f2798].address+ins[BASE+0x6f2798].size+ins[BASE+0x6f2798].operands[1].mem.disp==BASE+0xda9d60
    # Each exact reset argument must retain the complete input-vector lineage.
    required={BASE+x for x in (0x6f277b,0x6f2849,0x6f285a,0x6f28b4,0x6f28bb,0x6f2c4a)}
    assert required<=states[BASE+0x6f2cfe]['rcx'][2]
    old['v2MemoryWrites']=stores;old['v2StackAddressMaterializations']=leas
    old['v2GlobalStoreDominatesReloads']=True
    return old,ins,states,life,effects,steps,pe

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--output',default=str(ROOT/'evidence/engine-extension/native-live-owner-private-home-v2.json'));args=parser.parse_args()
    old,ins,states,life,effects,steps,pe=build()
    result=copy.deepcopy(old);result['schemaVersion']=2;result['status']='corrected-bounded-private-mission-home-proof-not-hooks'
    result['supersedes']={'path':str(OLD),'sha256':OLD_SHA,'reason':'698570 at6f2cfe writes external RDI+10, not local RSP70. Old effect annotation must not authorize a new hook.'}
    result['callEffects']=[effects[a] for a in sorted(effects)]
    result['argumentFlow']={'steps':steps,'reachedLifetimeInstructions':len(life),'entryPreconditions':{'rcx':'existing packet object','rdx':'existing vector header with valid external record backing','r8':'existing compression descriptor'},'method':'Independent all-predecessor pointer-only fixed point, register clobbers on calls, partial-register kills, exact saved input homes, exact 0xe0 record stride, all consumed pointer arguments asserted.'}
    paths={SOURCE,OLD,Path(__file__),ROOT/'tools/battle-scale/native_instruction_semantics.py',ROOT/'tools/battle-scale/patch_native_copy.py'}
    result['sources']=[{'path':str(p),'sha256':digest(p)} for p in sorted(paths)]
    result['calleeScope']['698570']='Call6f2cfe receives RCX=RDI+10 from6f2c4a. Exact leaf writes only this external 0x90-byte record region; RSP70 is the previously copied read-only comparison snapshot passed separately to698600.'
    result['externalRecordNonAliasProof']={
        'inputHeader':'Incoming RDX is saved at entryRSP+0x10 by6f277b and reloaded via RBP+0xe8 at6f2849/6f289d/6f2977. RBP is entryRSP-0xd8.',
        'backingField':'6f285a or post-resize6f28ae loads field0 of this input vector header into R8.6491f0 may replace that backing allocation; the reloaded pointer remains external to this private frame.',
        'recordAddress':'6f28b1 sign-extends logical index,6f28b4 multiplies by0xe0,6f28bb adds backing to RDI. This is a record address only under the explicit valid-index/preexisting-allocation precondition.',
        'resetArgument':'6f2c4a forms RCX=RDI+0x10; independent all-predecessor flow proves that value reaches6f2cfe. No subsequent RCX write changes the argument.',
        'snapshot':'6f2c52..6f2cfa copy selected original-record fields into local RSP70..ff. This snapshot is passed read-only in RCX at6f2d21; RDX there points to original record+10.',
        'conclusion':'Under well-formed caller object/index preconditions, original-record resets and decoder outputs cannot alias current private Mission home RSP30. This is not unconditional validation of arbitrary input pointers or indices.'}
    result['limitations'].append('This proof assumes well-formed incoming packet/vector/descriptor objects and valid native record indices. It does not certify record bounds or allocator behavior under corrupt packet state.')
    out=safe_review_output(args.output,[str(p) for p in paths]);out.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({'path':str(out),'sha256':digest(out),'calls':len(effects),'argumentFlowSteps':steps}))
if __name__=='__main__':main()
