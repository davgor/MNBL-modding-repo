"""Close the listed shared-pool helper callers using exact owner proofs."""
from pathlib import Path
import hashlib,json,sys
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import capstone,pefile
from patch_native_copy import safe_review_output

def build():
    folder=ROOT/'evidence/engine-extension'
    paths=[folder/n for n in ('native-pool-helper-escape-review.json','native-helper-call-provenance.json','pool-unwind-audit.json','native-pool-reference-inventory.json')]
    escape,flow,eh,inv=[json.loads(p.read_text()) for p in paths]
    binary=Path(inv['binary']['path']);data=binary.read_bytes();sha=hashlib.sha256(data).hexdigest()
    if sha!=escape['targetSha256'] or sha!=flow['sourceSha256']:raise ValueError('Target differs')
    pe=pefile.PE(data=data);base=pe.OPTIONAL_HEADER.ImageBase
    md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
    def check(row):
        address=int(row['address'],16);i=next(md.disasm(pe.get_data(address-base,15),address))
        if i.bytes.hex()!=row['bytes']:raise ValueError('Proof instruction differs')
        return i
    def raw(i):return dict(address=hex(i.address),bytes=i.bytes.hex(),decoded=i.mnemonic+' '+i.op_str)
    pool_offsets={0x2d50:0xc60,0x62da50:0xc60,0xc58750:0x230,0xd705c8:0x2c8,0xed3f80:0x2c8}
    target_stride={0x5d7df0:0xc60,0x5d8020:0xc60,0x5d81e0:0xc60,0x5d8220:0xc60,
      0x5d8390:0x2c8,0x5d8610:0x2c8,0x5d8730:0x2c8,0x5d8740:0x2c8,
      0x5d87d0:0x230,0x5d8840:0x230,0x5d91f0:0xc60,0x5d9350:0x2c8,0x5d94a0:0x230}
    rows=[]
    for c in flow['calls']:
        ins=check(c['candidate']);target=ins.operands[0].imm-base
        proofs=[a for a in c['analyses'] if a['converged'] and a['state'].get('rcx',{}).get('pointerKind')=='mission']
        if not proofs:continue
        for proof in proofs:
            v=proof['state']['rcx'];bias=v['biasBytes']
            if pool_offsets.get(bias)!=target_stride[target]:raise ValueError('Helper pool geometry mismatch')
            for p in v['definitionInstructions']:check(p)
            rows.append(dict(address=hex(ins.address),target=hex(base+target),classification='exact-CFG-Mission-pool',missionOffset=hex(bias),
                instruction=raw(ins),entryContract=proof['entryContract'],rcxFact=v,sourceFunction=proof['functionStart']))
    manual={0x5ce470:('mission_constructor',0x2d50,0x5ce469),0x5ce47d:('mission_constructor',0x62da50,0x5ce476),
      0x5ce4f1:('mission_constructor',0xd705c8,0x5ce4ea),0x5ce4fe:('mission_constructor',0xed3f80,0x5ce4f7),
      0x5cf303:('mission_destructor',0xed3f80,0x5cf2fc),0x5cf310:('mission_destructor',0xd705c8,0x5cf309),
      0x5cf31d:('mission_destructor',0xc58750,0x5cf316),0x5cf332:('mission_destructor',0x62da50,0x5cf323),
      0x5cf362:('mission_destructor',0x2d50,0x5cf353)}
    for a,(name,offset,start) in manual.items():
        owner=eh['owners'][name];instructions=owner['functionInstructions']
        proof=[p for p in instructions if base+start<=int(p['address'],16)<=base+a]
        decoded=[check(p) for p in proof]
        if not decoded or decoded[0].mnemonic!='lea' or decoded[0].operands[1].mem.disp!=offset:raise ValueError('Constructor field differs')
        expected_base='rbx' if name=='mission_constructor' else 'rdi'
        if decoded[0].reg_name(decoded[0].operands[1].mem.base)!=expected_base:raise ValueError('This carrier differs')
        target=decoded[-1].operands[0].imm-base
        if pool_offsets[offset]!=target_stride[target]:raise ValueError('Constructor helper geometry differs')
        # Retained whole bodies preserve the actual incoming-this aliases and
        # ABI-preserved carrier across helper calls; fields are not inferred
        # merely from their numeric displacement.
        rows.append(dict(address=hex(base+a),target=hex(base+target),classification='verified-Mission-constructor-or-destructor-field',
            missionOffset=hex(offset),ownerFunction=owner['functionAddress'],thisRegister=expected_base,
            proofInstructions=proof,source='pool-unwind-audit owners.'+name+'.functionInstructions'))
    owner=eh['owners']['agent_pool_destructor'];proof=owner['functionInstructions']
    for p in proof:check(p)
    rows.append(dict(address='0x1805d81f6',target='0x1805d7df0',classification='same-pool-wrapper-forward',
      proofInstructions=proof,reason='RCX passed unchanged through prologue toreset; incomingwrapper entries are exclusively the listed Mission EH field actions.'))
    for branch in escape['nearBranchCandidates']:
        i=check(branch)
        if i.mnemonic=='call':continue
        if i.address in (base+0x5d8730,base+0x5d8840):
            rows.append(dict(address=hex(i.address),target=hex(i.operands[0].imm),classification='same-pool-unconditional-tail-alias',instruction=raw(i)))
        else:
            matches=[x for x in eh['missionFieldAdjustments'] if x['tailJumpInstruction']['address']==hex(i.address)]
            if len(matches)!=1:raise ValueError('Unclassified helper tail entry')
            x=matches[0]
            if pool_offsets.get(x['offset'])!=target_stride[i.operands[0].imm-base]:raise ValueError('EH pool geometry differs')
            for p in (x['loadInstruction'],x['adjustmentInstruction'],x['tailJumpInstruction']):check(p)
            rows.append(dict(address=hex(i.address),target=hex(i.operands[0].imm),classification='FuncInfo-associated-Mission-pool-EH',missionOffset=x['offsetHex'],association=x))
    if len({r['address'] for r in rows})!=len(rows) or {r['address'] for r in rows}!={r['address'] for r in escape['nearBranchCandidates']}:raise ValueError('Helper entry closure incomplete')
    result=dict(schemaVersion=1,targetSha256=sha,status='all-listed-shared-helper-entry-owners-classified',gameExecuted=False,
      sources=[dict(path=str(p),sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in paths+[Path(__file__)]],
      entries=rows,unclassifiedListedEntries=[],summary=dict(entries=len(rows),cfgMissionCalls=15,constructorDestructorCalls=9,poolWrapperCalls=1,tailAliases=2,missionEhActions=10),
      scope='Installed hash-bound binary, listed direct helper entries and documented address-materialization search. No separate non-Mission object shares these constructor/reset/allocator entry references.',
      limitations=['No proof against arbitrary split/computed code addresses, injected native code or unreviewed modules. Default second-pool construction publication is reviewed separately; its gameplay purpose remains unnamed.'])
    out=folder/'native-pool-helper-caller-review.json';safe_review_output(out,[binary]+paths).write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(result['summary']))
if __name__=='__main__':build()
