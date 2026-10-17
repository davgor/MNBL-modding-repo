"""Exact Fading_out_entity owner reload in Mission frame cleanup; offline only."""
import hashlib,json,sys
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import capstone,pefile
from patch_native_copy import safe_review_output


def main():
    folder=ROOT/'evidence/engine-extension'
    paths=[folder/n for n in ('native-pool-reference-inventory.json','native-helper-call-provenance.json',
                             'native-expansion-ghidra-evidence.json','native-closure-ghidra-evidence.json')]
    inv,flow,old,new=[json.loads(p.read_text()) for p in paths]
    source=Path(inv['binary']['path']);data=source.read_bytes();sha=hashlib.sha256(data).hexdigest()
    if sha!=flow['sourceSha256'] or sha!=inv['binary']['sha256']:raise ValueError('Target changed')
    pe=pefile.PE(data=data);base=pe.OPTIONAL_HEADER.ImageBase
    md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
    def row(i):return dict(address=hex(i.address),bytes=i.bytes.hex(),decoded=i.mnemonic+' '+i.op_str)
    def body(a,z):
        instructions=list(md.disasm(pe.get_data(a,z-a),base+a))
        if sum(i.size for i in instructions)!=z-a:raise ValueError('Decode gap')
        return instructions
    bodies={}
    for start in (0x5d37b0,0x5fe190,0x61f470,0x7064e0,0x693620,0x693710):
        e=next(e.struct for e in pe.DIRECTORY_ENTRY_EXCEPTION if e.struct.BeginAddress==start)
        bodies[hex(base+start)]=body(start,e.EndAddress)
    consumer=body(0x5c53bc,0x5c54fd)
    # Limit the exact consumer span to complete decoded instructions before the
    # next unrelated frame operation; its bytes are also retained below.
    by={i.address:i for v in bodies.values() for i in v};by.update({i.address:i for i in consumer})
    required={0x69364b:'mov qword ptr [rcx + 0x10], rdx',
              0x5c53bc:'lea r14, [r13 + 0x1044e70]',
              0x5c53e8:'mov rcx, qword ptr [r13 + 0x1044e98]',
              0x5c546d:'mov rcx, qword ptr [rsi + 0x10]',
              0x5c5471:'add rcx, 0x1044e70',
              0x70657a:'mov rdx, rdi',0x706580:'call 0x180693620'}
    for at,text in required.items():
        ins=by[base+at]
        if ins.mnemonic+' '+ins.op_str!=text:raise ValueError('Owner instruction changed '+hex(at))
    callproofs=[]
    for at in (0x5d3a5b,0x5fe238,0x61f5b8):
        record=next(x for x in flow['immediates'] if x['candidate']['address']==hex(base+at))
        analyses=[a for a in record['analyses'] if a.get('converged') and a.get('state',{}).get('rdx',{}).get('pointerKind')=='mission' and a['state']['rdx']['biasBytes']==0]
        if len(analyses)!=1:raise ValueError('Missing exact constructor Mission argument '+hex(at))
        callproofs.append(analyses[0])
    # Fourth entry is a managed wrapper that reads CurrentMission directly.
    wrapper=bodies[hex(base+0x7064e0)]
    load=next(i for i in wrapper if i.mnemonic=='mov' and i.op_str.startswith('rdi, qword ptr [rip') and i.address+i.size+i.operands[1].mem.disp==base+0xda9d60)
    for ins in wrapper:
        if not load.address<ins.address<=base+0x706580:continue
        if ins.mnemonic!='call' and any(ins.reg_name(r) in ('rdi','edi','di','dil') for r in ins.regs_access()[1]):
            raise ValueError('CurrentMission register overwritten')
    callproofs.append(dict(address='0x180706580',load=row(load),copy=row(by[base+0x70657a]),
                          proof='Global CurrentMission in RDI; no intervening RDI write, and Win64 nonvolatile RDI survives calls.'))
    records=old['records']+new['records'];evidence=[]
    for proc in ('0x180693620','0x180693710','0x1805d37b0','0x1805fe190','0x18061f470','0x1807064e0','0x1805c37d0'):
        r=next(r for r in reversed(records) if r['operation']=='procedure_pseudo_code' and r.get('parameters',{}).get('procedure')==proc)
        evidence.append(dict(procedure=proc,evidenceId=r['evidence_id'],normalizedResult=r['normalized_result']))
    callers=next(r for r in reversed(records) if r['operation']=='procedure_callers' and r.get('parameters',{}).get('procedure')=='0x180693620')
    if set(callers['normalized_result'])!={'0x1805d37b0','0x1805fe190','0x18061f470','0x1807064e0'}:raise ValueError('Constructor caller set changed')
    ids=[r['evidenceId'] for r in evidence]+[callers['evidence_id']]
    ins=by[base+0x5c5471]
    result=dict(schemaVersion=1,status='exact-fading-entity-Mission-owner-proof-awaiting-independent-review',
        targetSha256=sha,gameExecuted=False,sources=[dict(path=str(p),sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in paths],
        constructorCallProofs=callproofs,retainedEvidence=evidence,resolvedConstructorCallers=callers,
        rawFunctionBodies={k:[row(i) for i in v] for k,v in bodies.items()},consumerInstructions=[row(i) for i in consumer],
        proof=['Mission1044e70 is the paged Fading_out_entity container;1044e98 holds its page pointers. Each page has16 records of64bytes, and the consumer forms RSI from the selected page plus(index&15)*64.',
               'All four resolved693620 constructor callers select a record in that same Mission container and pass that Mission as RDX.69364b stores RDX atrecord+10 before setting its live entity+18.',
               'Record cleanup693710 clears owner+10 and entity+18. The frame path requires entity+18 nonnull before using owner; RSI remains the selected record across ABI-preserved calls. Survival across intervening entity addref/scene/removal calls relies on the original engine record-lifetime and non-reentrancy contract; the null check alone is not a general concurrency proof.',
               'The exact reload5c546d therefore yields the record Mission. Only the immediately following ADD1044e70 is authorized; no general object+10 rule is introduced.'],
        exactReloadSeeds={'0x1805c546d':dict(register='rcx',pointerKind='mission',biasBytes=0,instruction=row(by[base+0x5c546d]))},
        instructionSiteMappings=[dict(address=hex(ins.address),instructionBytes=ins.bytes.hex(),classification='mission-relative-field',
            originalValue='0x1044e70',operandIndex=1,missionBiasBytes=0,provenanceInstructions=[row(by[base+0x5c546d]),row(by[base+0x69364b])],evidenceIds=ids)],
        limitations=['Resolved native calls and original record lifecycle only; unknown computed callers or third-party memory mutation are outside the isolated profile.',
                     'Existing engine ownership and reference-count lifetime contracts are preserved, not newly proven under arbitrary concurrent misuse.'])
    output=safe_review_output(folder/'native-fading-entity-owner-review.json',[source,*paths])
    output.write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(dict(exactMappings=1,constructorCalls=4)))


if __name__=='__main__':main()
