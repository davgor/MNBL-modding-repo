"""Prove two non-RCX owner arguments from exact callers and captured contexts."""
import hashlib
import json
from pathlib import Path
import sys

ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import capstone
import pefile
from patch_native_copy import safe_review_output


def audit():
    folder=ROOT/'evidence/engine-extension'
    inv_path=folder/'native-pool-reference-inventory.json'
    mid_path=folder/'pool-semantic-review-mid.json'
    bundle_path=folder/'native-closure-ghidra-evidence.json'
    inv=json.loads(inv_path.read_text());source_path=Path(inv['binary']['path']);source=source_path.read_bytes()
    sha=hashlib.sha256(source).hexdigest()
    if sha!='2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a':raise ValueError('Unsupported source')
    pe=pefile.PE(data=source);base=pe.OPTIONAL_HEADER.ImageBase
    md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
    def span(a,z):
        items=list(md.disasm(pe.get_data(a-base,z-a),a))
        if sum(i.size for i in items)!=z-a:raise ValueError('Incomplete decode')
        return items
    def raw(items):return [dict(address=hex(i.address),bytes=i.bytes.hex(),instruction=i.mnemonic+' '+i.op_str) for i in items]
    records=json.loads(bundle_path.read_text())['records']
    def evidence(proc):
        ids=list(dict.fromkeys(r['evidence_id'] for r in records if r.get('parameters',{}).get('procedure')==proc))
        if not ids:raise ValueError('Missing evidence '+proc)
        return ids
    mid=json.loads(mid_path.read_text())['missionMethodFlowReview']['entryContracts']
    body=span(0x1805c6950,0x1805c6dc5)
    for reg,expected in [('rsi',[0x1805c69a0]),('r13',[0x1805c69e7])]:
        writes=[i.address for i in body if any(i.reg_name(r) in (reg, 'e'+reg[1:] if reg=='rsi' else reg+'d') for r in i.regs_access()[1])]
        if writes!=expected:raise ValueError('Agent address source register changes: '+reg)
    rows=[dict(procedure='0x180690990',kind='primary-Agent-slot-call-argument',entryRegisters={'rdx':'agent'},entryBiases={'rdx':0},
        proof='5c6950 receives Mission in RCX, preserves it in RSI, sign-extends slot input R9D into RDI, multiplies by c60 into R14, and forms R13=Mission+slot*c60. R13 is unchanged until6db5 forms RDX=R13+2d50 and6dc0 calls690990. This is the primary Agent slot address; the Agent stride/base do not move in the proposed layout.',
        ownerContract=mid['0x1805c6950'],proofInstructions=raw(body),evidenceIds=evidence('0x1805c6950')+evidence('0x180690990'))]
    producer=span(0x180577b25,0x180577bdf)
    worker=span(0x18063dbe0,0x18063dc4a)
    first=producer[0]
    if first.address+first.size+first.operands[1].mem.disp!=0x180da9d60:raise ValueError('Current Mission global differs')
    rows.append(dict(procedure='0x18069b450',kind='synchronous-worker-captured-Mission-argument',entryRegisters={'r8':'mission'},entryBiases={'r8':0},
        proof='577aa0 copies current Mission global into local RSP+b8 and captures its address in tuple+10. The dispatched function object stores the tuple pointer at+0 and63dbe0 at+18. The reviewed1f2de0 dispatcher passes that function-object address as R8 and waits for task completion.63dbe0 loads tuple=[R8], then R8=*[tuple+10], and calls69b450. The float argument uses XMM1; the Mission is R8, not RDX. Manager63dc50 preserves the24-byte tuple on copy.',
        proofInstructions=raw(producer+worker),evidenceIds=evidence('0x180577aa0')+evidence('0x18063dbe0')+evidence('0x18063dc50'),
        dispatcherContract='pool-runtime-safety-contracts.json: synchronous dispatcher1f2de0'))
    output=safe_review_output(folder/'native-secondary-call-contracts.json',[source_path,inv_path,mid_path,bundle_path,Path(__file__).resolve()])
    output.write_text(json.dumps(dict(schemaVersion=1,targetSha256=sha,status='exact-call-arguments-reviewed',entryContracts=rows,gameLaunched=False,
        limitations=['Owner arguments only; every selected operand still requires local flow proof.','This does not enable network game modes or widen network serialization.']),indent=2)+'\n')
    print(json.dumps(dict(output=str(output),contracts=len(rows))))


if __name__=='__main__':audit()
