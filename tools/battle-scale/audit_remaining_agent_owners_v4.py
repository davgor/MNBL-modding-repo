"""Exact first-reload lifetime of the6a9930 AI input home; offline only."""
from pathlib import Path
from collections import defaultdict
import hashlib
import json
import sys

ROOT=Path(__file__).resolve().parents[2]
sys.path[:0]=[str(ROOT/'tools/battle-scale'),str(ROOT/'build/battle-scale/python-libs')]
from capstone.x86 import X86_OP_MEM, X86_OP_REG, X86_OP_IMM
from native_instruction_semantics import memory_operand_writes
from prove_native_ai_leaf_owner import read_namespace


def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()


def build():
    prior_path=ROOT/'evidence/engine-extension/native-remaining-agent-owner-proofs-v3.json'
    assert digest(prior_path)=='4aefbd31a60cee46f14a8fab8170a4bceb5ac68c9c38daf129f4c6c4966a358b'
    prior=json.loads(prior_path.read_text());deps={x['path']:x['sha256'] for x in prior['dependencies']}
    deps[str(prior_path)]=digest(prior_path);deps[str(Path(__file__))]=digest(__file__)
    for p,h in deps.items():assert digest(p)==h,p
    ns=read_namespace();base,pe,rec=ns['base'],ns['pe'],ns['flow'].basic
    assert hashlib.sha256(pe.__data__).hexdigest()==prior['binarySha256']
    root,store,reload=base+0x6a9930,base+0x6a993b,base+0x6a9d8e
    ins,edges,bad,ind=ns['decode'](root);assert not bad and not ind
    original=ns['transfer'];states,baseline=ns['analyze'](root,ns['contracts'][root])
    assert states is not None and baseline['status']=='converged'
    pred=defaultdict(set)
    for a,targets in edges.items():
        for t in targets:pred[t].add(a)
    life=set();todo=[reload]
    while todo:
        a=todo.pop()
        if a in life:continue
        life.add(a);todo.extend(pred[a])
    assert store in life and base+0x6a9e9e not in life
    assert ins[store].bytes.hex()=='49894b08' and ins[reload].bytes.hex()=='4c8bbda0000000'
    assert states[store]['rcx'][:2]==('agent-ai',0)
    expected_calls={base+0x6a99a0:base+0x69d150,base+0x6a9a97:base+0x5dbb30,
                    base+0x6a9bb4:base+0x41fd70,base+0x6a9c2d:base+0x5dd420}
    assert {a:i.operands[0].imm for a,i in ins.items() if a in life and i.mnemonic=='call'}==expected_calls
    # Verify every transitive local-pointer consumer. Only the original iterator
    # may be modified: fields0..15. The distance from the actual iterator to the
    # retained home is320bytes. The vector comparison helper is read-only.
    helpers=[]
    helper_roots=(0x5dbb30,0x5dd420,0x41fd70,0x5d9ef0)
    for rva in helper_roots:
        entry=base+rva
        context={'entryPointerKind':None,'entryRegisters':{'rcx':'external','rdx':'output','r8':'external2'},
                 'entryBiases':{'rcx':0,'rdx':0,'r8':0}}
        s,report=ns['analyze'](entry,context);ii,ee,bb,jj=ns['cache'][entry]
        assert s is not None and report['status']=='converged' and not bb and not jj
        writes=[];forward=[];pointer_stores=[]
        for a,i in ii.items():
            state=s[a]
            if i.mnemonic=='call' or i.mnemonic=='jmp' and not ee[a]:
                assert i.operands[0].type==X86_OP_IMM
                target=i.operands[0].imm
                assert (rva,a-base,target-base) in ((0x5dbb30,0x5dbc9e,0x5d9ef0),(0x5d9ef0,0x5d9f00,0x5dd420))
                assert state['rdx'][:2]==('output',0) and state['rcx'][:2]==('external',0)
                forward.append({'instruction':rec(i),'target':hex(target),
                    'arguments':{k:{'kind':state[k][0],'bias':state[k][1]} for k in ('rcx','rdx')}})
            for ix,op in enumerate(i.operands):
                if op.type!=X86_OP_MEM or not memory_operand_writes(i,ix):continue
                ea=ns['address'](i,op.mem,state)
                assert ea and ea[0] in ('stack','output'),(hex(a),ea)
                if ea[0]=='output':
                    assert rva!=0x41fd70 and 0<=ea[1] and ea[1]+op.size<=16
                else:
                    assert ea[1]+op.size<=32
                writes.append({'instruction':rec(i),'destinationKind':ea[0],'offset':ea[1],'length':op.size})
                if len(i.operands)>1 and i.operands[1].type==X86_OP_REG:
                    v=state.get(ns['reg'](i,i.operands[1].reg))
                    if v and v[0]=='output':
                        assert ea[0]=='stack'
                        pointer_stores.append(rec(i))
        assert not pointer_stores # No retention/escape of caller iterator pointer.
        helpers.append({'root':hex(entry),'context':context,'analysis':report,
            'instructions':[rec(i) for _,i in sorted(ii.items())],
            'writes':writes,'pointerForwarding':forward,'callerPointerStores':pointer_stores,
            'outputWriteRange':None if rva==0x41fd70 else [0,16]})
    # Validate caller-frame accesses throughout the reverse-reachable lifetime.
    accesses=[];home_writes=[];stack_addresses=[];call_rows=[]
    for a in sorted(life):
        i=ins[a];state=states[a]
        for ix,op in enumerate(i.operands):
            if op.type!=X86_OP_MEM:continue
            ea=ns['address'](i,op.mem,state)
            if ea and ea[0]=='stack':
                row={'instruction':rec(i),'offset':ea[1],'length':op.size,'writes':memory_operand_writes(i,ix)}
                accesses.append(row)
                if row['writes'] and ea[1]<16 and 8<ea[1]+op.size:home_writes.append(a)
                if i.mnemonic=='lea':stack_addresses.append(row)
        if a in expected_calls:
            local={k:v[1] for k,v in state.items() if k in ('rcx','rdx','r8','r9') and v[0]=='stack'}
            assert local==({} if a==base+0x6a99a0 else {'rdx':-320 if a==base+0x6a9bb4 else -312})
            assert state['rsp'][:2]==('stack',-408)
            call_rows.append({'instruction':rec(i),'localPointerArguments':local,'entryRelativeRsp':-408,
                'homeOffset':8,'shadowWrites':[-408,-376],
                'localWriteRange':None if a in (base+0x6a99a0,base+0x6a9bb4) else [-312,-296]})
    assert home_writes==[store]
    assert [a['offset'] for a in stack_addresses]==[-152,-312,-320,-312]
    # Preserve only this proven private home and only at these four call sites.
    # All other slot kills and the later deliberate home reuse remain active.
    key=('slot',8)
    def transfer(i,state):
        out=original(i,state)
        if i.address in expected_calls and key in state:
            out[key]=state[key]
        return out
    ns['transfer']=transfer
    restored,report=ns['analyze'](root,ns['contracts'][root]);assert restored is not None
    assert restored[reload][key][:2]==('agent-ai',0)
    multiply,use=base+0x6aa081,base+0x6aa098
    # The call argument is Agent+580 after the scaled address addition.
    owner=restored[use].get('rcx')
    assert owner and owner[0]=='agent' and multiply in owner[2],owner
    proof={'multiply':rec(ins[multiply]),'root':hex(root),'entryContract':ns['contracts'][root],
        'use':rec(ins[use]),'fieldBias':owner[1],
        'definitionInstructions':[rec(ins[d]) for d in sorted(owner[2]) if d in ins],
        'hookApproved':False,'allResultUsesProvenAddressOnly':False}
    checks=[]
    assert not states[use].get('rcx') or states[use]['rcx'][0]!='agent'
    checks.append({'name':'without-private-home-proof-no-owner','passed':True})
    # Wrong entry identity cannot be manufactured from a home reload.
    unknown,unknown_report=ns['analyze'](root,{'entryPointerKind':None,'entryRegisters':{}})
    assert not unknown[use].get('rcx') or unknown[use]['rcx'][0]!='agent'
    checks.append({'name':'unknown-entry-not-seeded','passed':True,'analysis':unknown_report})
    # The restoration is not permitted after the original home has been reused.
    overwrite=base+0x6a9e9e
    assert overwrite not in life and overwrite not in expected_calls
    overwritten=transfer(ins[overwrite],{key:ns['val']('agent-ai',0),'rbp':ns['val']('stack',-152)})
    assert key not in overwritten
    checks.append({'name':'later-home-overwrite-still-kills-owner','passed':True,'instruction':rec(ins[overwrite])})
    unrelated=next(ns['md'].disasm(bytes.fromhex('e800000000'),0x1000))
    after=transfer(unrelated,{key:ns['val']('agent-ai',0),'rsp':ns['val']('stack',-408),'rdx':ns['val']('stack',8)})
    assert key not in after
    checks.append({'name':'unreviewed-local-pointer-call-still-kills-home','passed':True})
    result={'schemaVersion':1,'status':'conditional-private-home-owner-contribution-pending-independent-review',
        'binarySha256':prior['binarySha256'],'dependencies':[{'path':p,'sha256':h} for p,h in sorted(deps.items())],
        'entryContract':ns['contracts'][root],'baselineAnalysis':baseline,'restoredAnalysis':report,
        'homeStore':rec(ins[store]),'homeReload':rec(ins[reload]),'lifetimeInstructions':[rec(ins[a]) for a in sorted(life)],
        'lifetimeSuccessors':{hex(a):[hex(t) for t in edges[a] if t in life] for a in sorted(life)},
        'stackAccesses':accesses,'calls':call_rows,'helpers':helpers,'negativeBoundaryChecks':checks,
        'previousRemainingCount':14,'additionalProven':1,'proofs':[proof],
        'remainingUnresolvedCount':13,'remaining':[r for r in prior['remaining'] if int(r['multiplyRva'],16)!=0x6aa081],
        'limitations':['Only ordinary decoded execution to the first home reload is proved; later scalar/vector reuse is intentionally excluded.',
            'Assumes well-formed external objects do not alias the callers private frame and ordinary native ABI stack discipline; corruption or asynchronous foreign frame mutation is excluded.',
            'Caller pointer is passed only to the reviewed iterator/vector helpers; integer-divide failure and exceptional exits do not establish successful owner continuation.',
            'No hook, universal lifetime, all-consumer, bounds or engine-integration approval.'],'enginePatchApproved':False,'gameLaunched':False}
    verified=set()
    def verify(v):
        if isinstance(v,dict):
            if 'address' in v and 'bytes' in v:
                a,raw=int(v['address'],16),bytes.fromhex(v['bytes']);assert pe.get_data(a-base,len(raw))==raw;verified.add((a,raw))
            for x in v.values():verify(x)
        elif isinstance(v,list):
            for x in v:verify(x)
    verify(result);result['verifiedOriginalInstructionRows']=len(verified)
    for p,h in deps.items():assert digest(p)==h,p
    return result


if __name__=='__main__':
    result=build();dest=ROOT/'evidence/engine-extension/native-remaining-agent-owner-proofs-v4.json'
    dest.write_text(json.dumps(result,indent=2)+'\n');print(json.dumps({'path':str(dest),'sha256':digest(dest),'additional':1,'remaining':13}))
