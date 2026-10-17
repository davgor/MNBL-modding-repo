"""Offline companion-offset audit; no engine code runs and no hook is approved.

Extends only a private copy of the established must-flow analysis. A numeric
multiply becomes an offset, never an object identity. Only an independently
established Mission base can turn that offset into a companion address.
"""
from pathlib import Path
import argparse, ast, hashlib, json, sys
from collections import Counter

ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
from capstone.x86 import X86_OP_MEM
from patch_native_copy import safe_review_output
SOURCE=ROOT/'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
SHA='2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
POOLS={'companion230':(0xc58750,0x230),'companion2c8A':(0xd705c8,0x2c8),'companion2c8B':(0xed3f80,0x2c8)}
HISTORY={'history230':(0x1046368,0x230),'history2c8':(0x10d2370,0x2c8)}

def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()

def load_flow():
    path=ROOT/'tools/battle-scale/prove_mission_aliases.py'
    code=path.read_text();marker='for root,contract in sorted(selected.items()):'
    assert code.count(marker)==1
    prefix=code.split(marker)[0]
    old=" if a[0]=='constant':a,b=b,a"
    new=""" if a[0]=='mission' and b[0] in ('companion-offset-230','companion-offset-2c8'):
  return ('mission-'+b[0],a[1]+b[1],defs)
 if b[0]=='mission' and a[0] in ('companion-offset-230','companion-offset-2c8'):return plus(b,a,ins)
 if a[0]=='constant':a,b=b,a"""
    assert prefix.count(old)==1;prefix=prefix.replace(old,new)
    old="def transfer(ins,st):\n out=dict(st);op=ins.operands"
    new="""def transfer(ins,st):
 if ins.address in (0x1805c87ee,0x1805c884b):return dict(st)
 out=dict(st);op=ins.operands"""
    assert prefix.count(old)==1;prefix=prefix.replace(old,new)
    old='# Actual ABI call-register values provide contracts where the decompiler recovers'
    new="""# Exact adjacent private-stack load/store copies are identity operations.
# Prove full bytes and sole predecessor before permitting those two exceptions.
identity_insns,identity_succ,identity_bad,identity_indirect=decode(0x1805c86a0)
assert not identity_bad and not identity_indirect
for load_at,store_at,disp in [(0x1805c87e9,0x1805c87ee,0x50),(0x1805c8846,0x1805c884b,0x40)]:
 load=identity_insns[load_at];store=identity_insns[store_at]
 assert load.bytes.hex()=='0f284424'+format(disp,'02x')
 assert store.bytes.hex()=='660f7f4424'+format(disp,'02x')
 assert [a for a,targets in identity_succ.items() if store_at in targets]==[load_at]
# Actual ABI call-register values provide contracts where the decompiler recovers"""
    assert prefix.count(old)==1;prefix=prefix.replace(old,new)
    old=" states={root:initial};work=deque([root]);queued={root};steps=0"
    new=""" for offset,fact in contract.get('entryStackFacts',{}).items():
  initial[('slot',int(offset))]=val(fact['kind'],fact['bias'])
 states={root:initial};work=deque([root]);queued={root};steps=0"""
    assert prefix.count(old)==1;prefix=prefix.replace(old,new)
    old="    mem=op[1].mem;bv=st.get(reg(ins,mem.base))"
    new="""    ea=address(ins,op[1].mem,st)
    if ea and ea[0]=='stack':right=st.get(('slot',ea[1]))
    mem=op[1].mem;bv=st.get(reg(ins,mem.base))"""
    assert prefix.count(old)==1;prefix=prefix.replace(old,new)
    old=" if ins.mnemonic=='imul' and len(op)==3 and op[2].type==X86_OP_IMM and op[2].imm==0xc60 and op[0].size==8:"
    new=""" if ins.mnemonic=='imul' and len(op)==3 and op[2].type==X86_OP_IMM and op[2].imm in (0x230,0x2c8) and op[0].size==8:
  out[reg(ins,op[0].reg)]=val('companion-offset-'+format(op[2].imm,'x'),0,[ins.address])
"""+old
    assert prefix.count(old)==1;prefix=prefix.replace(old,new)
    tree=ast.parse(prefix,filename=str(path))
    class Rewrite(ast.NodeTransformer):
        replacements=0
        def visit_Assign(self,node):
            if any(isinstance(x,ast.Name) and x.id=='source' for x in node.targets):
                assert ast.unparse(node.value)=="Path(inv['binary']['path']).read_bytes()"
                node.value=ast.parse('Path('+repr(str(SOURCE))+').read_bytes()',mode='eval').body
                self.replacements+=1
            return self.generic_visit(node)
        def visit_Call(self,node):
            if isinstance(node.func,ast.Attribute):
                assert node.func.attr not in ('write_text','write_bytes','unlink','mkdir','rename')
                if node.func.attr=='parse_args':node.args=[ast.List(elts=[],ctx=ast.Load())]
            return self.generic_visit(node)
    transform=Rewrite();tree=transform.visit(tree);assert transform.replacements==1
    ns={'__file__':str(path),'__name__':'readonly_companion_offset_analysis'}
    exec(compile(ast.fix_missing_locations(tree),str(path),'exec'),ns)
    # No numeric offset alone establishes an owner; wrong-stride origins fail.
    assert ns['plus'](ns['val']('companion-offset-230'),ns['val']('constant',7))[:2]==('companion-offset-230',7)
    assert ns['plus'](ns['val']('agent'),ns['val']('companion-offset-230')) is None
    assert ns['plus'](ns['val']('mission'),ns['val']('companion-offset-230'))[:2]==('mission-companion-offset-230',0)
    return ns,path,prefix

def build():
    assert digest(SOURCE)==SHA
    invpath=ROOT/'evidence/engine-extension/native-live-agent-access-review.json'
    inv=json.loads(invpath.read_text())
    rows=[r for r in inv['candidates'] if set(r['classes']) & {'index-stride-multiply:companion230','index-stride-multiply:companion2c8'}]
    assert len(rows)==73
    ns,path,prefix=load_flow();base=ns['base'];pe=ns['pe']
    secondary_path=ROOT/'evidence/engine-extension/native-secondary-pool-role-review.json'
    secondary=json.loads(secondary_path.read_text())
    helper_path=ROOT/'evidence/engine-extension/native-pool-helper-caller-review.json'
    helper=json.loads(helper_path.read_text())
    candidates={int(r['address'],16):r for r in rows}
    analyses=[];reports=[]
    for root,contract in ns['selected'].items():
        states,report=ns['analysis'][root]
        if states is not None:analyses.append((root,contract,states))
    covered={a for _,_,states in analyses for a in candidates if a in states}
    for root in sorted({base+int(r['rangeStartRva'],16) for a,r in candidates.items() if a not in covered}):
        if root in ns['blocked_entries'] or root not in ns['groups']:
            reports.append({'root':hex(root),'status':'no-conflict-free-established-function-group'});continue
        contract={'kind':'global-only-analysis-root','entryPointerKind':None,'entryRegisters':{},'evidenceIds':[],
                  'proof':'No receiver identity assumed; only established global/field owner contracts.'}
        states,report=ns['analyze'](root,contract);reports.append(report)
        if states is not None:analyses.append((root,contract,states))
    # Context-sensitive shared helper: exact two reviewed callers pass A and B.
    # Do not invent a globally unique owner for its RCX pool argument.
    helper_calls=[x for x in helper['entries'] if x['target']=='0x1805d8610']
    assert {int(x['missionOffset'],16) for x in helper_calls}=={0xd705c8,0xed3f80}
    for caller in helper_calls:
        for r in [caller['instruction']]+caller['rcxFact']['definitionInstructions']:
            assert pe.get_data(int(r['address'],16)-base,len(bytes.fromhex(r['bytes']))).hex()==r['bytes']
        contract={'kind':'reviewed-caller-context-pool-base','entryPointerKind':'mission',
                  'entryRegisters':{'rcx':'mission'},'entryBiases':{'rcx':int(caller['missionOffset'],16)},
                  'evidenceIds':caller['entryContract']['evidenceIds'],'sourceCaller':caller}
        states,report=ns['analyze'](base+0x5d8610,contract);reports.append(report)
        if states is not None:analyses.append((base+0x5d8610,contract,states))
    # Higher-argument contracts are call-context-specific. They never become a
    # blanket function ABI. A failed/missing stack fact simply remains unknown.
    targets={base+x for x in (0x6763c0,0x68d800,0x68dd10,0x68eea0,0x68efc0)}
    seen=set();call_contexts=[]
    for iteration in range(3):
        added=[]
        for caller_root,caller_contract,states in analyses:
            insns=ns['cache'][caller_root][0]
            for a,st in states.items():
                ins=insns[a]
                if ins.mnemonic!='call' or len(ins.operands)!=1 or ins.operands[0].type!=ns['X86_OP_IMM']:continue
                target=ins.operands[0].imm
                if target not in targets or target not in ns['groups']:continue
                rsp=st.get('rsp')
                if not rsp or rsp[0]!='stack':continue
                registers={r:v for r,v in st.items() if r in ('rcx','rdx','r8','r9') and v[0] in ('mission','agent')}
                slots={40+8*i:st.get(('slot',rsp[1]+32+8*i)) for i in range(4)}
                slots={k:v for k,v in slots.items() if v and v[0]=='mission'}
                if not slots:continue
                key=(target,tuple(sorted((r,v[:2]) for r,v in registers.items())),tuple(sorted((k,v[:2]) for k,v in slots.items())))
                if key in seen:continue
                seen.add(key)
                definitions=set().union(*(v[2] for v in list(registers.values())+list(slots.values())))
                contract={'kind':'exact-call-context-higher-pool-arguments','entryPointerKind':None,
                    'entryRegisters':{r:v[0] for r,v in registers.items()},'entryBiases':{r:v[1] for r,v in registers.items()},
                    'entryStackFacts':{str(k):{'kind':v[0],'bias':v[1]} for k,v in slots.items()},
                    'evidenceIds':caller_contract.get('evidenceIds',[]),'callerRoot':hex(caller_root),'call':ns['flow'].basic(ins),
                    'callerEntryContract':caller_contract,
                    'definitionInstructions':[ns['flow'].basic(insns[x]) for x in sorted(definitions) if x in insns],
                    'proof':'Must-proven Mission-derived outgoing stack slots at this exact call, translated from callerRSP+20 to calleeEntryRSP+28. No unknown argument receives an assumed type.'}
                result,report=ns['analyze'](target,contract);reports.append(report)
                if result is not None:added.append((target,contract,result));call_contexts.append(contract)
        analyses.extend(added)
        if not added:break
    proofs={a:[] for a in candidates};diagnostics={a:[] for a in candidates}
    for root,contract,states in analyses:
        insns=ns['cache'][root][0]
        for a in candidates.keys() & states.keys():
            nearby=[x for x in sorted(insns) if a-24<=x<=a+100]
            diagnostics[a].append({'root':hex(root),'entryContract':contract,'localStates':[
                {'instruction':ns['flow'].basic(insns[x]),'owners':{str(k):{'kind':v[0],'bias':v[1]} for k,v in states.get(x,{}).items()}}
                for x in nearby]})
        for address,st in states.items():
            ins=insns[address]
            values=[('register:'+str(k),v) for k,v in st.items() if v[0].startswith('mission-companion-offset-')]
            # Observe the actual result before a successor merge discards it.
            post=ns['transfer'](ins,st)
            values.extend(('result-register:'+str(k),v) for k,v in post.items()
                          if v[0].startswith('mission-companion-offset-') and v!=st.get(k))
            for ix,op in enumerate(ins.operands):
                if op.type==X86_OP_MEM:
                    v=ns['address'](ins,op.mem,st)
                    if v and v[0].startswith('mission-companion-offset-'):values.append(('operand:'+str(ix),v))
            for observed,v in values:
                stride=int(v[0].rsplit('-',1)[1],16)
                for pool,(origin,width) in {**POOLS,**HISTORY}.items():
                    if stride!=width or not 0<=v[1]-origin<width:continue
                    for definition in v[2]&candidates.keys():
                        proof={'root':hex(root),'entryContract':contract,'pool':pool,'poolBase':hex(origin),
                               'fieldBias':hex(v[1]-origin),'observation':observed,'use':ns['flow'].basic(ins),
                               'definitionInstructions':[ns['flow'].basic(insns[x]) for x in sorted(v[2]) if x in insns]}
                        # Keep all memory uses and one first observation per pool/field.
                        if observed.startswith('operand:') or not any(p['pool']==pool and p['fieldBias']==proof['fieldBias'] for p in proofs[definition]):
                            proofs[definition].append(proof)
    def raw_span(start,end):
        instructions=list(ns['md'].disasm(pe.get_data(start,end-start),base+start)) if 'md' in ns else []
        if not instructions:
            import capstone
            decoder=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64)
            instructions=list(decoder.disasm(pe.get_data(start,end-start),base+start))
        assert sum(i.size for i in instructions)==end-start,(hex(start),hex(end))
        return [ns['flow'].basic(i) for i in instructions]
    allocation_families=[
        {'name':'FMOD system record allocation','sourceStringRva':'0xa3f2d0','sourceString':'C:\\Develop\\mb3_fmod_source_2\\core_api\\src\\fmod_systemi.cpp',
         'allocationMultiplyRva':'0x82415e','indexMultiplyRvas':['0x8241c0'],'ownerField':'object+1e8',
         'instructions':raw_span(0x824150,0x8241e4)},
        {'name':'FMOD music record allocation','sourceStringRva':'0xa65960','sourceString':'C:\\Develop\\mb3_fmod_source_2\\core_api\\src\\fmod_music.cpp',
         'allocationMultiplyRva':'0x8ec789','indexMultiplyRvas':['0x8ec7ca','0x8ec8e7'],'ownerField':'object+5e0',
         'instructions':raw_span(0x8ec775,0x8ec81c)+raw_span(0x8ec8e0,0x8ec8fa)}]
    distinct={}
    for family in allocation_families:
        string=family['sourceString'].encode();assert pe.get_data(int(family['sourceStringRva'],16),len(string)+1)==string+b'\0'
        family['recordConstructor']={'entry':'0x180831fb0','instructions':raw_span(0x831fb0,0x831fef),
          'proof':'Freshly allocated pointer at the owner field is indexed then passed to831fb0. Constructor writes vtable180a3f938 at record+0; this is a separate allocated record family, not inline Mission+c58750 storage.'}
        distinct[family['allocationMultiplyRva']]=('non-pool-allocation-byte-count',family)
        for rva in family['indexMultiplyRvas']:distinct[rva]=('separate-FMOD-allocated-record-array',family)
    output=[]
    for a,r in candidates.items():
        assert pe.get_data(a-base,len(bytes.fromhex(r['bytes']))).hex()==r['bytes']
        uses=proofs[a]
        classification=('proven-Mission-companion-address-contribution' if any(p['pool'] in POOLS for p in uses)
                        else 'proven-distinct-Mission-history-buffer' if uses else 'unresolved-owner')
        numeric_other=distinct.get(r['rva'])
        if numeric_other:
            assert not uses
            classification=numeric_other[0]
        rva=int(r['rva'],16)
        uncertainty=None
        if classification=='unresolved-owner':
            if 0x5c8700<=rva<0x5c8d00:uncertainty='History/source base passes through stack/SIMD aliases and optional selection. The exact post-call base is not must-proven; do not infer primary companion ownership from230.'
            elif rva==0x647a6c:uncertainty='Mission global established, but base reloaded from RBP-41 after calls lacks a preserved-spill contract.'
            elif 0x676000<=rva<0x690000:uncertainty='Worker uses caller-selected higher-argument pool bases or local copies. Some incoming contexts are proven below; local stack survival/optional selection is not closed at this multiply use. A/B or history identity must be preserved per call.'
            else:uncertainty='Uses an externally stored record array (object+1e8 or+5e0), matching the distinct FMOD allocation family below; exact receiver-to-allocation identity across these methods is not independently closed. Remains an exclusion candidate, not an authorized pool hook.'
        output.append({'multiply':r,'classification':classification,
                       'poolKinds':sorted({p['pool'] for p in uses}),'addressUses':uses,'diagnostics':diagnostics[a],
                       'distinctAllocationEvidence':numeric_other[1] if numeric_other else None,'unresolvedRequirement':uncertainty,
                       'allResultUsesProvenAddressOnly':False,'hookApproved':False,
                       'replacementContract':'Resolve each independently proven pool-kind/logical-index address; retain scalar offset and logical index for every other use. Do not replace shared IMUL output with a single resolved pointer.' if uses else 'No resolver routing authorized without an owner and use proof.'})
    deps={SOURCE,invpath,path,Path(__file__),secondary_path,helper_path}
    deps.update(x for x in ns.values() if isinstance(x,Path) and x.is_file() and x.is_relative_to(ROOT));deps.update(ns['bundle_paths'])
    return {'schemaVersion':1,'status':'bounded-companion-access-audit-not-hooks','nativeSourceSha256':SHA,
            'sources':[{'path':str(p),'sha256':digest(p)} for p in sorted(deps)],
            'analysisPrefixSha256':hashlib.sha256(prefix.encode()).hexdigest(),
            'candidateCount':len(rows),'classificationCounts':dict(Counter(r['classification'] for r in output)),
            'candidates':output,'additionalAnalysisReports':reports,
            'higherArgumentCallContexts':call_contexts,
            'distinctAllocationFamilies':allocation_families,
            'privateStackIdentityCopies':{'function':'0x1805c86a0','pairs':[['0x1805c87e9','0x1805c87ee'],['0x1805c8846','0x1805c884b']],
                'proof':'Exact adjacent MOVAPS XMM0,[RSP+offset]; MOVDQA [same RSP+offset],XMM0; sole CFG predecessor of each store is its matching load. Same16 private stack bytes are written back unchanged; preserving preexisting spill facts introduces no new pointer identity.'},
            'secondaryAllocationDomain':{'source':str(secondary_path),'originalLayout':secondary['originalPool'],
                'conclusion':secondary['conclusion'],
                'requiredContract':'Keep secondary occupancy/count/table/high-water and any future generations/free chain separate from primary. Equal physical growth capacity does not imply shared logical occupancy.',
                'companionMapping':'Unknown for secondary gameplay allocation: no supported allocator/selector or secondary-to-companion association established. Reviewed primary create/delete uses primary+230+A+B only.',
                'sharingPrimarySecondaryOccupancyApproved':False},
            'constraints':['All three companion storage identities share the Agent logical slot index; preserve same-slot transaction and lifetime.',
                           'A 2c8 multiply can feed both A and B addresses. One resolver kind cannot replace that shared offset.',
                           'Pool-relative generic removal/link helpers need caller-derived pool identity; numeric 2c8 alone cannot choose A versus B.',
                           'Existing Agent+20 caches companionA address: all blocks must preserve stable element addresses.',
                           'Metadata tables/counts/high-water and signed-short links require separate sidecar routing; this multiply audit does not cover them.',
                           'No whole-result use closure, detour window, unwind, safe publication point, or engine integration is approved.'],
            'limitations':['Only the73 numeric multiply candidates in the pinned G2 inventory are covered. LEA/add-only, pointer walks, metadata and opaque aliases require separate inventories.',
                           'Unresolved candidates may be unrelated numeric strides; absence of a Mission proof is not an exclusion proof.'],
            'tests':{'typedOffsetDoesNotImplyOwner':True,'wrongOwnerRejected':True,'independentMissionCombinationRequired':True,'originalMultiplyBytesVerified':73}}

def main():
    p=argparse.ArgumentParser();p.add_argument('--output',default=str(ROOT/'evidence/engine-extension/native-segmented-companion-accesses.json'));args=p.parse_args()
    result=build();out=safe_review_output(args.output,[s['path'] for s in result['sources']]);out.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({'output':str(out),'sha256':digest(out),'classificationCounts':result['classificationCounts']}))

if __name__=='__main__':main()
