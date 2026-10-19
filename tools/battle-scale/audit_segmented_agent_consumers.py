"""Trace uses of primary offset multiplies without approving substitutions."""
from pathlib import Path
from collections import Counter,defaultdict
import ast,hashlib,inspect,json
import extend_live_agent_owner_proofs as owner
from native_offset_use_flow import trace_uses
ROOT=owner.ROOT
def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()

def load_analysis():
    # Execute only the existing analysis setup through its collected contexts.
    # No original writer is run and no frozen input source is modified.
    tree=ast.parse(inspect.getsource(owner.main));fn=tree.body[0]
    cuts=[i for i,node in enumerate(fn.body) if isinstance(node,ast.For) and
          ast.unparse(node.target)=='(root, contract, states)' and ast.unparse(node.iter)=='analyses']
    assert len(cuts)==1
    fn.body=fn.body[:cuts[0]]+[ast.Return(value=ast.Tuple(elts=[ast.Name(id='ns',ctx=ast.Load()),ast.Name(id='analyses',ctx=ast.Load())],ctx=ast.Load()))]
    scope=dict(owner.__dict__)
    exec(compile(ast.fix_missing_locations(tree),str(Path(owner.__file__)),'exec'),scope)
    return scope['main']()

def main():
    own_paths=['tools/battle-scale/audit_segmented_agent_consumers.py','tools/battle-scale/native_offset_use_flow.py',
               'tests/BattleScale/test_native_offset_use_flow.py']
    own_sources=[{'path':str(ROOT/p),'sha256':digest(ROOT/p)} for p in own_paths]
    receipt=ROOT/'evidence/engine-extension/native-live-agent-owner-extension.json'
    proof=json.loads(receipt.read_text())
    for dep in proof['dependencies']:assert digest(dep['path'])==dep['sha256'],dep['path']
    ns,analyses=load_analysis();base=ns['base']
    invpath=ROOT/'evidence/engine-extension/native-live-agent-access-review.json'
    inv=json.loads(invpath.read_text())
    candidates=[c for c in inv['candidates'] if 'index-stride-multiply:Agent' in c['classes']]
    assert len(candidates)==431
    contexts=defaultdict(list)
    for root,contract,states in analyses:contexts[root].append(states)
    reports=[];instructions={};counts=Counter()
    for candidate in candidates:
        definition=int(candidate['address'],16)
        roots=[root for root,g in ns['groups'].items() if any(int(r['start'],16)<=definition<int(r['endExclusive'],16) for r in g['runtimeRanges'])]
        roots=list(dict.fromkeys(roots))
        row={'multiplyRva':candidate['rva'],'ownerStatus':candidate['ownerStatus'],'contexts':[],
             'hookApproved':False,'allResultUsesProvenAddressOnly':False}
        if not roots:row['unresolved']='no-established-function-group'
        for root in roots:
            insns,succ,bad,indirect=ns['decode'](root)
            if bad or indirect or definition not in insns:
                row['contexts'].append({'root':hex(root),'status':'decode-gap-or-indirect-flow','bad':bad,'indirect':indirect});continue
            if root not in contexts:
                states,report=ns['analyze'](root,{'kind':'consumer-stack-only','entryPointerKind':None,'entryRegisters':{},'evidenceIds':[]})
                if states is not None:contexts[root].append(states)
            def stack_address(pc,op):
                values=[]
                for states in contexts[root]:
                    if pc not in states:continue
                    value=ns['address'](insns[pc],op.mem,states[pc])
                    if value is None or value[0]!='stack':return None
                    values.append(value[1])
                return values[0] if values and len(set(values))==1 else None
            result=trace_uses(insns,succ,definition,ns['reg'],stack_address)
            result['root']=hex(root);result['status']='bounded-may-use-inventory'
            result['eventCounts']=dict(Counter(e['kind'] for e in result['events']))
            for e in result['events']:
                a=int(e['address'],16)
                if a in insns:instructions[a]=ns['flow'].basic(insns[a])
            instructions[definition]=ns['flow'].basic(insns[definition])
            row['contexts'].append(result);counts.update(result['eventCounts'])
        reports.append(row)
    for dep in proof['dependencies']:assert digest(dep['path'])==dep['sha256'],dep['path']
    for dep in own_sources:assert digest(dep['path'])==dep['sha256'],dep['path']
    deps=[*proof['dependencies'],{'path':str(receipt),'sha256':digest(receipt)},*own_sources]
    out={'schemaVersion':1,'status':'consumer-routing-inventory-not-patch-approval','binarySha256':owner.EXPECTED,
         'sources':deps,'candidateCount':len(reports),'eventCounts':dict(counts),'candidates':reports,
         'instructions':[v for _,v in sorted(instructions.items())],
         'limits':['This is a may-flow inventory of arithmetic derived from431 numeric multiply definitions, not a type or full-program alias proof.',
                   'Caller arguments, return values and persistent stores terminate at explicit escapes; downstream consumers require separate contracts.',
                   'Unknown stack aliases, exceptional edges, unsupported value operations and incomplete decoding remain gaps.',
                   'Memory loads consume an address but do not automatically propagate offset identity to loaded content.',
                   'Incoming alternate/interior entries, unwind behavior, original flags and raw pointer lifetime still require per-hook validation.',
                   'No hook, installation or game execution is approved by this artifact.'],
         'enginePatchApproved':False,'gameLaunched':False}
    dest=ROOT/'evidence/engine-extension/native-segmented-agent-consumers.json'
    dest.write_text(json.dumps(out,indent=2)+'\n')
    print(json.dumps({'path':str(dest),'sha256':digest(dest),'candidates':len(reports),'events':dict(counts)}))
if __name__=='__main__':main()
