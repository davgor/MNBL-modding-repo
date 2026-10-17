"""Bounded 6f2770 private Mission home proof; no installed file or game access."""
from pathlib import Path
import argparse,hashlib,json
from collections import defaultdict
import audit_segmented_companion_accesses as common
from install_live_growth_flow_contracts import install
from native_instruction_semantics import memory_operand_writes
from patch_native_copy import safe_review_output
ROOT=common.ROOT

def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()

def build():
    ns,prover,_=common.load_flow();install(ns)
    pe=ns['pe'];base=ns['base'];entry=base+0x6f2770
    from capstone.x86 import X86_OP_MEM,X86_OP_IMM
    insns,succ,bad,indirect=ns['decode'](entry)
    assert not bad and not indirect
    prior_path=ROOT/'evidence/engine-extension/native-segmented-companion-accesses.json'
    prior=json.loads(prior_path.read_text())
    deps={Path(x['path']) for x in prior['sources']}|{prior_path,Path(__file__),prover,
          ROOT/'tools/battle-scale/install_live_growth_flow_contracts.py',
          ROOT/'tools/battle-scale/audit_recursive_register_preservation.py',
          ROOT/'evidence/engine-extension/native-recursive-register-preservation-review.json',
          ROOT/'evidence/engine-extension/native-live-growth-rea-evidence-3.json',
          ROOT/'evidence/engine-extension/native-closure-ghidra-evidence.json'}
    for x in prior['sources']:assert digest(x['path'])==x['sha256']
    bundles=[json.loads(p.read_text()) for p in deps if p.name in ('native-live-growth-rea-evidence-3.json','native-closure-ghidra-evidence.json')]
    retained=[]
    for rva in (0x6f2770,0x711220,0x63e220,0x6ead40,0x6ec2d0,0x6ec7d0,0x746070,0x698600,0x2692d0,0x6491f0):
        records=[r for b in bundles for r in b['records'] if r.get('parameters',{}).get('procedure')==hex(base+rva) and r.get('operation')=='procedure_pseudo_code']
        assert records,hex(rva)
        retained.append(records[-1])
    def rec(i):return ns['flow'].basic(i)
    def exact(rva,op):
        i=insns[base+rva];assert i.op_str==op,(hex(rva),i.op_str);return rec(i)
    source=[exact(0x6f2798,'r14, qword ptr [rip + 0x6b75c1]'),exact(0x6f27c9,'qword ptr [rsp + 0x30], r14')]
    reloads=(base+0x6f2928,base+0x6f2afd)
    pred=defaultdict(set)
    for a,targets in succ.items():
        for t in targets:pred[t].add(a)
    life=set();todo=list(reloads)
    while todo:
        a=todo.pop()
        if a in life:continue
        life.add(a);todo.extend(pred[a])
    assert base+0x6f2f49 not in life
    states,report=ns['analyze'](entry,{'entryPointerKind':None,'entryRegisters':{},'kind':'global-only-no-receiver-assumption'})
    assert states is not None
    assert states[base+0x6f27c9].get('r14',())[:2]==('mission',0)
    frame=states[base+0x6f27c9]['rsp'][1];home=frame+0x30
    writes=[];address_materializations=[]
    # The full reverse lifetime includes the first switch's backedge. Direct
    # and SIMD writes use semantic destination checks, not Capstone access only.
    for a in sorted(life):
        i=insns[a];st=states[a]
        for ix,op in enumerate(i.operands):
            if op.type!=X86_OP_MEM:continue
            ea=ns['address'](i,op.mem,st)
            if ea and ea[0]=='stack':
                if i.mnemonic=='lea':
                    assert not(home<=ea[1]<home+8),hex(a)
                    address_materializations.append({'instruction':rec(i),'steadyRspOffset':ea[1]-frame})
                if memory_operand_writes(i,ix):
                    overlap=ea[1]<home+8 and home<ea[1]+op.size
                    assert not overlap or a==base+0x6f27c9,hex(a)
                    writes.append({'instruction':rec(i),'steadyRspOffset':ea[1]-frame,'length':op.size})
            elif op.mem.base and i.reg_name(op.mem.base) in ('rsp','rbp'):
                raise AssertionError(('unclassified stack address',hex(a),i.op_str))
    # Exact caller output intervals. Other arguments are original external
    # objects/scalars or the read-only local interval explicitly stated below.
    effects={
      0x6f27f3:('711220',[(0x24,4)],[]),
      0x6f2898:('6491f0',[],[]),0x6f2972:('6491f0',[],[]),
      0x6f2a04:('63e220',[(0x38,4)],[]),0x6f2a19:('63e220',[(0x3c,4)],[]),0x6f2a2e:('63e220',[(0x40,4)],[]),
      0x6f2a53:('6ead40',[(0x48,12)],[(0x38,12)]),
      0x6f2a8e:('746070',[],[]),0x6f2aa2:('746070',[],[]),0x6f2ad3:('63e220',[],[]),
      0x6f2cfe:('698570',[(0x70,0x90)],[]),
      0x6f2d0a:('6ec2d0',[],[]),0x6f2d21:('698600',[],[(0x70,0x90)]),
      0x6f2d80:('6ec7d0',[],[]),
      0x6f2dd6:('2692d0',[],[(0x118,12)]),0x6f2deb:('2692d0',[],[(0x128,12)]),
      0x6f2e11:('746070',[],[]),0x6f2e28:('746070',[],[]),0x6f2e3f:('746070',[],[]),
      0x6f2ec0:('711220',[(0x24,4)],[])}
    calls=[]
    for a in sorted(life):
        i=insns[a]
        if i.mnemonic!='call':continue
        target,outputs,inputs=effects[a-base]
        assert i.operands[0].type==X86_OP_IMM and i.operands[0].imm==base+int(target,16)
        for lo,n in outputs:assert not(lo<0x38 and 0x30<lo+n)
        st=states[a];args={}
        for reg in ('rcx','rdx','r8','r9'):
            v=st.get(reg)
            if v and v[0]=='stack':args[reg]=v[1]-frame
        assert all(any(lo<=v<lo+n for lo,n in outputs+inputs) for v in args.values()),(hex(a),args,outputs,inputs)
        calls.append({'instruction':rec(i),'target':target,'outputs':outputs,'readOnlyInputs':inputs,'knownLocalRegisterArguments':args})
    assert len(calls)==len(effects)
    # Leaf698570 writes only its exact RCX output, returns without calls.
    leaf_rows=[];leaf=list(ns['md'].disasm(pe.get_data(0x698570,0x84),base+0x698570))
    assert leaf[-1].mnemonic=='ret' and sum(i.size for i in leaf)==0x84
    for i in leaf:
        assert i.mnemonic!='call'
        for ix,op in enumerate(i.operands):
            if op.type==X86_OP_MEM and memory_operand_writes(i,ix):
                assert i.reg_name(op.mem.base)=='rcx' and not op.mem.index and 0<=op.mem.disp and op.mem.disp+op.size<=0x90
        leaf_rows.append(rec(i))
    helper_bodies={}
    for helper in (0x711220,0x63e220,0x6ead40,0x746070,0x698600):
        decoded,_,gaps,jumps=ns['decode'](base+helper)
        assert not gaps and not jumps,hex(helper)
        helper_bodies[hex(base+helper)]=[rec(i) for _,i in sorted(decoded.items())]
    compare=list(ns['md'].disasm(pe.get_data(0x2692d0,0x4a),base+0x2692d0))
    assert sum(i.size for i in compare)==0x4a and compare[-1].mnemonic=='ret'
    for i in compare:
        assert i.mnemonic!='call'
        assert not any(op.type==X86_OP_MEM and memory_operand_writes(i,ix) for ix,op in enumerate(i.operands))
    helper_bodies[hex(base+0x2692d0)]=[rec(i) for i in compare]
    # Must-dominance of the original write, including all switch predecessors.
    must={entry:False};pending=[entry]
    while pending:
        a=pending.pop();v=must[a] or a==base+0x6f27c9
        for t in succ[a]:
            out=v if t not in must else must[t] and v
            if t not in must or must[t]!=out:must[t]=out;pending.append(t)
    assert all(must[a] for a in reloads)
    seeds=[{'instruction':rec(insns[a]),'register':insns[a].reg_name(insns[a].operands[0].reg),'kind':'mission','bias':0,
            'proof':'Original global Mission stored in private RSP30; exact reverse lifetime has no overlapping store, address escape, or callee output. Later reuse excluded.'} for a in reloads]
    original_transfer=ns['transfer']
    def exact_reloads(i,st):
        out=original_transfer(i,st)
        if i.address in reloads:
            dst=i.reg_name(i.operands[0].reg)
            out[dst]=ns['val']('mission',0,[base+0x6f2798,base+0x6f27c9,i.address])
        return out
    ns['transfer']=exact_reloads
    replay,replay_report=ns['analyze'](entry,{'entryPointerKind':None,'entryRegisters':{},'kind':'global-and-two-exact-private-home-reloads'})
    assert replay is not None
    desired={base+x for x in (0x6f2921,0x6f2b96,0x6f2c11,0x6f3194)}
    observed={a:[] for a in desired}
    for a,st in replay.items():
        i=insns[a];after=exact_reloads(i,st)
        observations=list(st.items())+list(after.items())
        for ix,op in enumerate(i.operands):
            if op.type==X86_OP_MEM:
                v=ns['address'](i,op.mem,st)
                if v:observations.append(('operand:'+str(ix),v))
        for reg,v in observations:
            if v[0]!='agent':continue
            for definition in v[2]&desired:
                if not observed[definition]:observed[definition].append({'instruction':rec(i),'register':str(reg),'fieldBias':v[1],'definitions':[hex(x) for x in sorted(v[2])]})
    assert all(observed.values()),[hex(a) for a,v in observed.items() if not v]
    return {'schemaVersion':1,'status':'bounded-private-mission-home-proof-not-hooks','nativeSourceSha256':common.SHA,
      'sources':[{'path':str(p),'sha256':digest(p)} for p in sorted(deps)],'entry':hex(entry),'frameRspMinusEntryRsp':frame,
      'sourceInstructions':source,'reloadSeeds':seeds,'replayedAgentDefinitions':{hex(a):v for a,v in sorted(observed.items())},'lifetimeInstructions':[rec(insns[a]) for a in sorted(life)],
      'fullFunctionInstructions':[rec(i) for _,i in sorted(insns.items())],'directStackWrites':writes,
      'stackAddressMaterializations':address_materializations,'callEffects':calls,'leaf698570':leaf_rows,
      'helperInstructions':helper_bodies,'retainedCalleeEvidence':retained,'switchProof':ns['live_growth_extra_contracts']['switches'],
      'calleeScope':{'711220':'Writes4 bytes through RDX, forwards only its own local to63e220; never publishes caller output pointer.',
       '63e220':'Writes exactly4 through RDX; compression descriptor read-only. Diagnostic calls receive scalar decoded values/static format strings, not output pointer.',
       '746070':'Writes exactly4 through RDX; diagnostic calls receive scalars/static strings. Does not publish output pointer.',
       '6ead40':'R8 output offsets0/4/8 only; R9 input12 bytes read-only.746070 receives only callee-local outputs and descriptor.',
       '698600/2692d0':'Read-only comparison;698600 forwards offsets54/64 to2692d0, which reads three floats and has no calls/stores.',
       '6491f0/6ec2d0/6ec7d0':'This caller passes original external vector/record objects, no address of the private frame or homes. Their general heap mutation is not certified by this spill proof.'},
      'assumptions':['Ordinary well-formed native callers: preexisting packet/vector allocations do not alias this newly allocated private frame. No arbitrary corruption, external memory writes or asynchronous stack inspection certified.'],
      'limitations':['Only the two exact reloads are proved. Later RSP30 reuse is real and is not restored. No new receiver ABI, full-function Mission register invariant, segmented hook approval or thread-quiescence proof.']}

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--output',default=str(ROOT/'evidence/engine-extension/native-live-owner-private-home.json'));args=parser.parse_args()
    result=build();out=safe_review_output(args.output,[s['path'] for s in result['sources']]);out.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({'path':str(out),'sha256':digest(out),'lifetimeInstructions':len(result['lifetimeInstructions']),'calls':len(result['callEffects'])}))

if __name__=='__main__':main()

