"""Bounded Mission-home survival and nonescaping output proof for713316."""
from pathlib import Path
from collections import defaultdict, deque
import hashlib
import json
import sys

ROOT=Path(__file__).resolve().parents[2]
sys.path[:0]=[str(ROOT/'tools/battle-scale'),str(ROOT/'build/battle-scale/python-libs')]
from capstone.x86 import X86_OP_MEM, X86_OP_REG, X86_OP_IMM
from native_instruction_semantics import memory_operand_writes
from prove_native_ai_leaf_owner import read_namespace


def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()


def prove_output_no_escape(ns, ins, edges, root, cookie_call=None):
    """MAY-union pointer aliases: no dropped alias at joins can hide escape."""
    # Bias sets retain every possible byte offset from the incoming output.
    states={root:{'rdx':frozenset([0])}};queue=deque([root]);steps=0
    while queue:
        a=queue.popleft();i=ins[a];before=states[a];after=dict(before);ops=i.operands;steps+=1
        assert steps<10000
        for r in i.regs_access()[1]:after.pop(ns['reg'](i,r),None)
        # Direct-register arithmetic on this pointer is unsupported unless
        # explicitly handled below. Memory address uses do not taint values read.
        sources=[before.get(ns['reg'](i,o.reg)) for o in ops[1:] if o.type==X86_OP_REG]
        if i.mnemonic=='call':
            args=('rcx',) if a==cookie_call else ('rcx','rdx','r8','r9')
            assert not any(before.get(r) for r in args),(hex(a),before)
            clobbers=('rcx',) if a==cookie_call else ('rax','rcx','rdx','r8','r9','r10','r11')
            for r in clobbers:after.pop(r,None)
        elif i.mnemonic in ('mov','movabs') and len(ops)==2 and ops[0].type==X86_OP_REG and ops[1].type==X86_OP_REG:
            val=before.get(ns['reg'](i,ops[1].reg))
            if val:
                assert ops[0].size==ops[1].size==8
                after[ns['reg'](i,ops[0].reg)]=val
        elif any(sources):
            # Only the exact full-pointer return copy above is present here.
            raise AssertionError(('unhandled output-pointer operation',hex(a),i.mnemonic,i.op_str))
        for ix,op in enumerate(ops):
            if op.type!=X86_OP_MEM:continue
            mem=op.mem
            bv=before.get(ns['reg'](i,mem.base)) if mem.base else None
            iv=before.get(ns['reg'](i,mem.index)) if mem.index else None
            if bv or iv:
                assert bv and not mem.index,(hex(a),'indexed output address')
                assert i.mnemonic!='lea',(hex(a),'unreviewed pointer derivation')
                assert all(0<=off+mem.disp and off+mem.disp+op.size<=16 for off in bv)
            if memory_operand_writes(i,ix):
                assert not any(sources),(hex(a),'output pointer stored/escaped')
        for target in edges[a]:
            merged={k:states.get(target,{}).get(k,frozenset())|after.get(k,frozenset()) for k in states.get(target,{}).keys()|after.keys()}
            merged={k:v for k,v in merged.items() if v}
            if target not in states or merged!=states[target]:states[target]=merged;queue.append(target)
    assert len(states)==len(ins)
    return states,steps


def build():
    prior_path=ROOT/'evidence/engine-extension/native-remaining-agent-owner-proofs-v4.json'
    assert digest(prior_path)=='e72c5ea12e8a4fec54cd4a5647aa85f799f2ba09c8a624a2eb7ae8539b107559'
    prior=json.loads(prior_path.read_text());deps={x['path']:x['sha256'] for x in prior['dependencies']}
    deps[str(prior_path)]=digest(prior_path);deps[str(Path(__file__))]=digest(__file__)
    for p,h in deps.items():assert digest(p)==h,p
    ns=read_namespace();base,pe,rec=ns['base'],ns['pe'],ns['flow'].basic
    assert hashlib.sha256(pe.__data__).hexdigest()==prior['binarySha256']
    root,store,reload=base+0x712560,base+0x712602,base+0x71331d
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
    assert len(life)==234 and store in life
    assert ns['address'](ins[store],ins[store].operands[0].mem,states[store])[:2]==('stack',-800)
    assert states[store]['rdi'][:2]==('mission',0)
    assert base+0x71363e not in life
    helper=base+0x60c6c0
    helper_context={'entryPointerKind':'agent','entryRegisters':{'rcx':'agent','rdx':'output'},'entryBiases':{'rcx':0,'rdx':0}}
    hs,hr=ns['analyze'](helper,helper_context);hi,he,hb,hj=ns['cache'][helper]
    assert hs is not None and not hb and not hj
    cookie_bytes=pe.get_data(0x9df5b0,0x21)
    assert cookie_bytes.hex()=='483b0dd1fd3100f2751248c1c11066f7c1fffff27502f2c348c1c910e9830c0000'
    assert hi[base+0x60d231].operands[0].imm==base+0x9df5b0
    may,steps=prove_output_no_escape(ns,hi,he,helper,base+0x60d231)
    helper_writes=[];helper_calls=[]
    for a,i in hi.items():
        if i.mnemonic=='call':
            consumed=('rcx',) if a==base+0x60d231 else ('rcx','rdx','r8','r9')
            helper_calls.append({'instruction':rec(i),'possibleOutputArguments':
                {r:sorted(may[a][r]) for r in consumed if r in may[a]},
                'consumedRegisters':list(consumed),
                'unusedVolatileResidues':{r:sorted(may[a][r]) for r in ('rcx','rdx','r8','r9') if r not in consumed and r in may[a]}})
            assert not helper_calls[-1]['possibleOutputArguments']
        for ix,op in enumerate(i.operands):
            if op.type!=X86_OP_MEM or not memory_operand_writes(i,ix):continue
            ea=ns['address'](i,op.mem,hs[a])
            if ea and ea[0]=='output':
                assert 0<=ea[1] and ea[1]+op.size<=16
                helper_writes.append({'instruction':rec(i),'offset':ea[1],'length':op.size})
    assert [(int(w['instruction']['address'],16)-base,w['offset'],w['length']) for w in helper_writes]==[
        (0x60d1ba,0,4),(0x60d1be,4,4),(0x60d1c3,8,4),(0x60d1c8,12,4),(0x60d21f,8,4)]
    calls={a:i.operands[0].imm for a,i in ins.items() if a in life and i.mnemonic=='call'}
    assert len(calls)==10
    accesses=[];home_writes=[];local_calls=[];address_uses=[];stack_pointer_stores=[]
    for a in sorted(life):
        i=ins[a];state=states[a]
        for ix,op in enumerate(i.operands):
            if op.type!=X86_OP_MEM:continue
            ea=ns['address'](i,op.mem,state)
            if ea and ea[0]=='stack':
                row={'instruction':rec(i),'offset':ea[1],'length':op.size,'writes':memory_operand_writes(i,ix)}
                accesses.append(row)
                if row['writes'] and ea[1]<-792 and -800<ea[1]+op.size:home_writes.append(a)
                if i.mnemonic=='lea':address_uses.append(row)
            if memory_operand_writes(i,ix) and len(i.operands)>1 and i.operands[1].type==X86_OP_REG:
                v=state.get(ns['reg'](i,i.operands[1].reg))
                if v and v[0]=='stack':stack_pointer_stores.append({'instruction':rec(i),'valueOffset':v[1]})
        if a in calls:
            local={k:v[1] for k,v in state.items() if k in ('rcx','rdx','r8','r9') and v[0]=='stack'}
            assert local==({'rdx':-520} if a==base+0x712b12 else {})
            assert state['rsp'][:2]==('stack',-952)
            local_calls.append({'instruction':rec(i),'localPointerArguments':local,'entryRelativeRsp':-952,
                'homeOffset':-800,'shadowWriteRange':[-952,-920],'localWriteRange':[-520,-504] if local else None})
    assert home_writes==[store]
    assert [r['offset'] for r in address_uses]==[-696,-520]
    assert not stack_pointer_stores
    key=('slot',-800)
    def transfer(i,state):
        out=original(i,state)
        if i.address in calls and key in state:out[key]=state[key]
        return out
    ns['transfer']=transfer
    restored,report=ns['analyze'](root,ns['contracts'][root]);assert restored is not None
    assert restored[reload][key][:2]==('mission',0)
    multiply,use=base+0x713316,base+0x713321
    # Find first actual Agent-address consumer carrying this scaled index.
    matches=[]
    for a,state in restored.items():
        i=ins[a]
        for op in i.operands:
            if op.type!=X86_OP_MEM:continue
            owner=ns['address'](i,op.mem,state)
            if owner and owner[0]=='agent' and multiply in owner[2]:matches.append((a,owner))
    assert matches
    use,owner=min(matches)
    proof={'multiply':rec(ins[multiply]),'root':hex(root),'entryContract':ns['contracts'][root],
        'use':rec(ins[use]),'fieldBias':owner[1],
        'definitionInstructions':[rec(ins[d]) for d in sorted(owner[2]) if d in ins],
        'hookApproved':False,'allResultUsesProvenAddressOnly':False}
    checks=[]
    unknown,ur=ns['analyze'](root,{'entryPointerKind':None,'entryRegisters':{}})
    assert not any((o:=ns['address'](ins[use],op.mem,unknown[use])) and o[0]=='agent' for op in ins[use].operands if op.type==X86_OP_MEM)
    checks.append({'name':'unknown-entry-not-seeded','passed':True,'analysis':ur})
    baseline_owner=[ns['address'](ins[use],op.mem,states[use]) for op in ins[use].operands if op.type==X86_OP_MEM]
    assert not any(o and o[0]=='agent' for o in baseline_owner)
    checks.append({'name':'original-slot-kill-does-not-prove-owner','passed':True})
    # Inject a synthetic call consuming the tainted output to ensure MAY aliases
    # are rejected, including a mixed join that a must-only checker could lose.
    fixture_base=0x1000
    for name,raw in [('direct-escape','488bcae800000000c3'),
                     ('mixed-join-escape','85c07405488bcaeb0233c9e800000000c3'),
                     ('output-pointer-retained','488911c3')]:
        fi={i.address:i for i in ns['md'].disasm(bytes.fromhex(raw),fixture_base)};fe={}
        for a,i in fi.items():
            nxt=a+i.size
            if i.mnemonic=='ret':fe[a]=[]
            elif i.mnemonic.startswith('j'):fe[a]=[i.operands[0].imm]+([] if i.mnemonic=='jmp' else [nxt])
            else:fe[a]=[nxt]
        try:prove_output_no_escape(ns,fi,fe,fixture_base)
        except AssertionError:pass
        else:raise AssertionError(name)
        checks.append({'name':name,'bytes':raw,'passed':True})
    result={'schemaVersion':1,'status':'conditional-private-home-owner-contribution-pending-independent-review',
        'binarySha256':prior['binarySha256'],'dependencies':[{'path':p,'sha256':h} for p,h in sorted(deps.items())],
        'entryContract':ns['contracts'][root],'baselineAnalysis':baseline,'restoredAnalysis':report,
        'homeStore':rec(ins[store]),'homeReload':rec(ins[reload]),'lifetimeInstructions':[rec(ins[a]) for a in sorted(life)],
        'lifetimeSuccessors':{hex(a):[hex(t) for t in edges[a] if t in life] for a in sorted(life)},
        'stackAccesses':accesses,'callerLocalAddressUses':address_uses,'callerStackPointerStores':stack_pointer_stores,'calls':local_calls,
        'outputHelper':{'root':hex(helper),'context':helper_context,'analysis':hr,
            'instructions':[rec(i) for _,i in sorted(hi.items())],'writes':helper_writes,'calls':helper_calls,
            'mayAliasWorklistSteps':steps,'mayAliases':{hex(a):{k:sorted(v) for k,v in s.items()} for a,s in sorted(may.items()) if s},
            'cookieCheck':{'entry':'0x1809df5b0','bytes':cookie_bytes.hex(),
                'normalReturnContract':'With intact stack cookie, this exact helper reads/writesRCX only, compares the cookie global, and returns. Failure tail9e0254 is excluded, never assumed to return.',
                'preservedOutputResidues':['rdx','rax']},
            'semantics':'Output pointer is confined to a16-byte caller vector and returned directly; no pointer argument or stored alias escapes to another callee.'},
        'negativeBoundaryChecks':checks,'previousRemainingCount':13,'additionalProven':1,'proofs':[proof],
        'remainingUnresolvedCount':12,'remaining':[r for r in prior['remaining'] if int(r['multiplyRva'],16)!=0x713316],
        'limitations':['Only reverse-reachable ordinary execution to the selected first Mission-home use; later deliberate home reuse is excluded.',
            'Assumes ordinary native ABI stack discipline and well-formed external objects disjoint from the callers private frame; corruption/foreign mutation excluded.',
            'The exact security-cookie helper consumes RCX only on its validated normal-return path; RDX/RAX output pointer residues are not parameters. Cookie failure is excluded.',
            'Callee output-MAY aliases preserve both paths at joins; nested calls never receive this output pointer. This is not a proof that those other external operations cannot fail.',
            'No hook, all-consumer, bounds, lifetime or engine integration approval.'],'enginePatchApproved':False,'gameLaunched':False}
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
    result=build();dest=ROOT/'evidence/engine-extension/native-remaining-agent-owner-proofs-v5.json'
    dest.write_text(json.dumps(result,indent=2)+'\n');print(json.dumps({'path':str(dest),'sha256':digest(dest),'additional':1,'remaining':12}))
