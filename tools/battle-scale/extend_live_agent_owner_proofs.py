"""Read-only extension of existing owner flow to unresolved access roots.

No receiver identity is invented. New roots start with an empty ABI contract;
only exact established global loads and their derived fields can prove owners.
This does not prove that all uses of a multiplied offset are addresses.
"""
from pathlib import Path
import ast, hashlib, json, sys
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
from capstone.x86 import X86_OP_MEM
SOURCE=ROOT/'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
EXPECTED='2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'

def main():
    assert hashlib.sha256(SOURCE.read_bytes()).hexdigest()==EXPECTED
    path=ROOT/'tools/battle-scale/prove_mission_aliases.py'
    code=path.read_text();marker='for root,contract in sorted(selected.items()):'
    assert code.count(marker)==1
    prefix=code.split(marker)[0]
    # Additional exact field contract: CreateAgent stores the equipment object
    # in Agent+ad8, then copies Agent+ab8 Mission to equipment+f18. Rebinding
    #6f4a90 repeats those assignments. No old proof/source is rewritten.
    replacements={
      "elif ea[0]=='agent' and ea[1]==0x888:v=val('agent-ai',0,ea[2])":
      "elif ea[0]=='agent' and ea[1]==0xad8:v=val('agent-equipment',0,ea[2])\n    elif ea[0]=='agent-equipment' and ea[1]==0xf18:v=val('mission',0,ea[2])\n    elif ea[0]=='agent' and ea[1]==0x888:v=val('agent-ai',0,ea[2])",
      "if bv and bv[0]=='missile-page-table' and bv[1]+mem.disp==0 and (not mem.index or mem.scale==8):right=val('missile',0,bv[2])":
      "if bv and bv[0]=='missile-page-table' and bv[1]+mem.disp==0 and (not mem.index or mem.scale==8):right=val('missile',0,bv[2])\n    ea=address(ins,mem,st)\n    if ea and ea[0]=='stack':right=st.get(slot(ea[1]))\n    elif ea and ((ea[0]=='agent-equipment' and ea[1]==0xf18) or (ea[0]=='agent' and ea[1] in (0xab8,0x148)) or (ea[0]=='agent-ai' and ea[1]==0x14d8) or (ea[0]=='missile' and ea[1]==0x10)):right=val('mission',0,ea[2])",
      "v[0] in ['mission','agent','agent-ai','missile']":"v[0] in ['mission','agent','agent-ai','missile','agent-equipment']",
      "if source_root and int(source_root,16) in blocked_entries and target not in blocked_entries:":
      "if ((source_root and int(source_root,16) in blocked_entries) or any(int(extra['caller'],16) in blocked_entries for extra in contract.get('additionalOwnerProofs',[]))) and target not in blocked_entries:",
    }
    for old,new in replacements.items():
        assert prefix.count(old)==1,old
        prefix=prefix.replace(old,new)
    marker_analysis='analysis={};entry_conflicts=[];blocked_entries=set();pending='
    assert prefix.count(marker_analysis)==1
    prefix=prefix.replace(marker_analysis,
        'from bounded_native_register_effects import install as install_register_effects\ninstall_register_effects(globals())\nfrom install_live_growth_flow_contracts import install as install_growth_contracts\ninstall_growth_contracts(globals())\n'+marker_analysis)
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
    rewrite=Rewrite();tree=rewrite.visit(tree);assert rewrite.replacements==1
    ns={'__file__':str(path),'__name__':'read_only_extended_owner_flow'}
    exec(compile(ast.fix_missing_locations(tree),str(path),'exec'),ns)
    extra_contract_bytes={0x5cd5ff:'49899ed80a0000',0x5cd60a:'498b96b80a0000',
                          0x5cd611:'488993180f0000',0x6f4a99:'488b89d80a0000',
                          0x6f4ab5:'4c8b83b80a0000',0x6f4abc:'4c8982180f0000'}
    for rva,expected in extra_contract_bytes.items():
        assert ns['pe'].get_data(rva,len(bytes.fromhex(expected))).hex()==expected
    original=ROOT/'evidence/engine-extension/native-live-agent-access-review.json'
    inventory=json.loads(original.read_text())
    candidates={int(x['address'],16):x for x in inventory['candidates'] if
        'index-stride-multiply:Agent' in x['classes'] and x['ownerStatus']=='unresolved-owner'}
    roots=set()
    for definition,candidate in candidates.items():
        owners=[root for root,group in ns['groups'].items() if any(
            int(span['start'],16)<=definition<int(span['endExclusive'],16) for span in group['runtimeRanges'])]
        roots.update(owners or [int(candidate['rangeStartRva'],16)+ns['base']])
    roots=sorted(roots)
    found={};reports=[];analyses=[]
    # Original pass looked only at memory operands. A computed Agent pointer
    # can instead be forwarded as a call argument without a local dereference.
    for root,contract in ns['selected'].items():
        states,report=ns['analysis'][root]
        if states is not None:analyses.append((root,contract,states))
    for root in roots:
        if root in ns['blocked_entries']:
            reports.append({'root':hex(root),'status':'blocked-conflicting-entry'});continue
        if root not in ns['groups']:
            reports.append({'root':hex(root),'status':'no-established-function-group'});continue
        contract={'kind':'global-only-analysis-root','entryPointerKind':None,'entryRegisters':{},
                  'evidenceIds':[],'proof':'No entry receiver identity assumed. Existing exact global/field contracts only.'}
        states,report=ns['analyze'](root,contract);reports.append(report)
        if states is None:continue
        analyses.append((root,contract,states))
    for root,contract,states in analyses:
        insns=ns['cache'][root][0]
        for address,state in states.items():
            ins=insns[address]
            values=[(str(register),value) for register,value in state.items() if value[0]=='agent']
            # Record the computed address before a following loop-header join
            # discards differing per-iteration field biases. This proves only
            # this computation, never the loop's whole lifetime or all uses.
            for register,value in ns['transfer'](ins,state).items():
                if value[0]=='agent' or value[0]=='mission-index' and 0x2d50<=value[1]<0x39b0:
                    values.append(('post-instruction:'+str(register),value))
            for operand in ins.operands:
                if operand.type!=X86_OP_MEM:continue
                value=ns['address'](ins,operand.mem,state)
                if value is None or value[0]!='agent':continue
                values.append(('effective-memory-address',value))
            for kind,value in values:
                for definition in value[2]:
                    if definition not in candidates:continue
                    found.setdefault(definition,{'multiply':candidates[definition],
                        'root':hex(root),'entryContract':contract,'use':ns['flow'].basic(ins),
                        'effectiveOwner':value[0],'fieldOffset':value[1],'observationKind':kind,
                        'definitionInstructions':[ns['flow'].basic(insns[x]) for x in sorted(value[2]) if x in insns],
                        'allResultUsesProvenAddressOnly':False,'hookApproved':False})
    diagnostics=[]
    for definition,candidate in candidates.items():
        if definition in found:continue
        observed=[]
        for root,contract,states in analyses:
            if definition not in states:continue
            insns=ns['cache'][root][0]
            addresses=[a for a in sorted(insns) if definition-48<=a<=definition+64]
            observed.append({'root':hex(root),'contract':contract,
                'localStates':[{'instruction':ns['flow'].basic(insns[a]),
                  'owners':{str(k):{'kind':v[0],'bias':v[1]} for k,v in states.get(a,{}).items()}}
                   for a in addresses]})
        diagnostics.append({'candidateRva':candidate['rva'],'analyses':observed})
    deps={path,original,Path(__file__),ROOT/'tools/battle-scale/bounded_native_register_effects.py',
          ROOT/'tools/battle-scale/install_live_growth_flow_contracts.py',
          ROOT/'tools/battle-scale/audit_recursive_register_preservation.py',
          ROOT/'evidence/engine-extension/native-recursive-register-preservation-review.json'}
    deps.update(x for x in ns.values() if isinstance(x,Path) and x.is_file() and x.is_relative_to(ROOT))
    deps.update(ns['bundle_paths'])
    out={'schemaVersion':1,'status':'bounded-extra-owner-proofs-not-hooks','binarySha256':EXPECTED,
         'inputUnresolvedCount':len(candidates),'newlyProvenCount':len(found),
         'remainingUnresolvedCount':len(candidates)-len(found),'proofs':list(found.values()),
         'extraFieldContract':{'role':'Agent equipment backpointer to Mission',
           'stores':[{'rva':hex(r),'bytes':b} for r,b in extra_contract_bytes.items()],
           'rules':['Agent+ad8 => equipment object','equipment+f18 => Mission',
             'ADD memory operand uses the same established Mission-owner field and nonescaped stack-slot facts as MOV',
             'No additional equipment arguments are merged into previously established global entry contracts'],
           'modifiedAnalysisPrefixSha256':hashlib.sha256(prefix.encode()).hexdigest()},
         'registerPreservationClosure':{hex(k):v for k,v in ns['leaf_register_effects'].items() if v['preservedRegisters']},
         'extraExactContracts':ns['live_growth_extra_contracts'],
         'remainingRvas':[x['rva'] for address,x in candidates.items() if address not in found],
         'analysis':reports,'unresolvedDiagnostics':diagnostics,
         'dependencies':[{'path':str(p),'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in sorted(deps)],
         'limitations':['No receiver inferred at new global-only roots. Exact equipment field contracts extend direct-call propagation.',
          'Owner contribution alone does not establish all-use substitution safety or complete consumer coverage.',
          'Original proof source/evidence and installed game untouched; no target execution.']}
    destination=ROOT/'evidence/engine-extension/native-live-agent-owner-extension.json'
    destination.write_text(json.dumps(out,indent=2)+'\n')
    print(json.dumps({k:out[k] for k in ['status','inputUnresolvedCount','newlyProvenCount','remainingUnresolvedCount']}))
if __name__=='__main__':main()
