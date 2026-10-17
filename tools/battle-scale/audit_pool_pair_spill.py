"""Prove the exact Mission argument-eight address adjustment at68f345."""
from pathlib import Path
import hashlib,json,sys
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import capstone,pefile
from patch_native_copy import safe_review_output
from native_instruction_semantics import memory_operand_writes

def build():
    folder=ROOT/'evidence/engine-extension'
    paths=[folder/n for n in ('native-pool-reference-inventory.json','pool-semantic-review-mid.json','native-closure-ghidra-evidence.json')]
    inv,mid,bundle=[json.loads(p.read_text()) for p in paths]
    binary=Path(inv['binary']['path']);data=binary.read_bytes();sha=hashlib.sha256(data).hexdigest()
    if sha!=mid['targetSha256']:raise ValueError('Native target differs')
    pe=pefile.PE(data=data);base=pe.OPTIONAL_HEADER.ImageBase
    md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
    def span(a,z):
        out=list(md.disasm(pe.get_data(a,z-a),base+a))
        if sum(i.size for i in out)!=z-a:raise ValueError('Decode gap')
        return out
    def raw(i):return dict(address=hex(i.address),bytes=i.bytes.hex(),decoded=i.mnemonic+' '+i.op_str)
    caller=span(0x68eea0,0x68efb9);callee=span(0x68efc0,0x68f376)
    by={i.address:i for i in caller+callee}
    contract=mid['missionMethodFlowReview']['entryContracts']['0x18068eea0']
    if contract['entryRegisters']['rdx']!='mission' or contract['entryBiases']['rdx']!=0:raise ValueError('Missing exact owner contract')
    for p in contract['argumentProvenance']['rdx']+[contract['callInstruction']]:
        i=next(md.disasm(pe.get_data(int(p['address'],16)-base,15),int(p['address'],16)))
        if i.bytes.hex()!=p['bytes']:raise ValueError('Entry owner bytes differ')
    expected={0x68eee3:'mov rdi, rdx',0x68ef12:'mov qword ptr [rsp + 0x38], rdi',
              0x68ef60:'mov qword ptr [rsp + 0x38], rdi',0x68f33a:'mov rcx, qword ptr [rsp + 0x338]',
              0x68f345:'add rcx, 0x1044890'}
    for a,text in expected.items():
        if by[base+a].mnemonic+' '+by[base+a].op_str!=text:raise ValueError('Argument transfer differs')
    if [(i.address,i.op_str) for i in callee if i.mnemonic=='call']!=[(base+0x68f34f,'0x1805e9ae0')]:raise ValueError('Unexpected callee call')
    if [(i.address,i.op_str) for i in caller if i.mnemonic=='call']!=[(base+0x68ef4e,'0x18068efc0'),(base+0x68ef74,'0x18068efc0')]:raise ValueError('Unexpected caller call')
    direct=[];derived=[]
    for i in callee:
        for n,op in enumerate(i.operands):
            if not memory_operand_writes(i,n):continue
            reg=i.reg_name(op.mem.base)
            row=dict(raw(i),baseRegister=reg,displacement=op.mem.disp,size=op.size,index=i.reg_name(op.mem.index))
            if reg=='rsp':
                if op.mem.index or op.mem.disp<0x340 and op.mem.disp+op.size>0x338:raise ValueError('Argument home overwritten')
                direct.append(row)
            else:derived.append(row)
    # Caller RDI is the incoming Mission after68eee3, until its epilogue restore.
    writes=[raw(i) for i in caller if base+0x68eee3<=i.address<base+0x68ef85 and 'rdi' in {i.reg_name(r) for r in i.regs_access()[1]}]
    if len(writes)!=1 or writes[0]['address']!='0x18068eee3':raise ValueError('Mission carrier overwritten')
    rec=next(r for r in reversed(bundle['records']) if r.get('operation')=='procedure_pseudo_code' and r.get('parameters',{}).get('procedure')=='0x18068efc0')
    ins=by[base+0x68f345]
    result=dict(schemaVersion=1,targetSha256=sha,status='exact-argument-eight-Mission-address-proof',gameExecuted=False,
        sources=[dict(path=str(p),sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in paths+[Path(__file__),ROOT/'tools/battle-scale/native_instruction_semantics.py']],
        entryContract=contract,callerInstructions=[raw(i) for i in caller],calleeInstructions=[raw(i) for i in callee],
        directStackWrites=direct,derivedMemoryWrites=derived,
        retainedCalleeEvidence=dict(evidenceId=rec['evidence_id'],normalizedResult=rec['normalized_result']),
        proof=[
          'Exact68eea0 entry RDX is Mission.68eee3 moves it to nonvolatileRDI; both calls explicitly putRDI in outgoingargument8[RSP38].No RDI reassignment reaches either call. The callee preservesRDI via its own save/restore.',
          '68efc0 pushesRBX then subtracts2f0: total2f8. Incomingargument8 is entryRSP40, therefore steadyRSP338. The sole nativecall is after the targetreload, and no stackaddress escapes before it.',
          'Two fixed five-iteration128-byte copy loops plus72-byte tails use localarrayRSP20..2e7. Their other endpoints are external2c8 companionpool records supplied by the caller. No derivedstore can reach argumenthome338..33f.',
          'Other derivedstores target the external companionpool records; incomingpool arguments are separate from the current stack. DirectsaveRSP300..307 also cannot overlap338. Thus68f33a loads originalMission and68f345 is a genuine moved Mission member address.'],
        instructionSiteMappings=[dict(address=hex(ins.address),rva=hex(ins.address-base),fileOffset=pe.get_offset_from_rva(ins.address-base),
          instructionBytes=ins.bytes.hex(),operandIndex=1,operandKind='immediate',encodedOffset=ins.imm_offset,encodedSize=ins.imm_size,
          originalValue='0x1044890',classification='mission-relative-field',missionBiasBytes=0,missionRegister='rcx',
          provenanceInstructions=[raw(by[base+a]) for a in (0x68eee3,0x68ef12,0x68ef4e,0x68ef60,0x68ef74,0x68efc0,0x68efc2,0x68f33a)],
          evidenceIds=contract['evidenceIds']+[rec['evidence_id']])],
        limitations=['Exact member-address instruction only; assumes the retained Mission and companionpool entry contracts. No runtime or whole-patch approval.'])
    out=folder/'native-pool-pair-spill-review.json';safe_review_output(out,[binary]+paths).write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(dict(mapping=hex(ins.address),callerInstructions=len(caller),calleeInstructions=len(callee),derivedWrites=len(derived))))
if __name__=='__main__':build()
