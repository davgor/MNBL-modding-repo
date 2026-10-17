"""Exclude seven exact non-Mission arithmetic operands; no binary writes."""
import hashlib,json,sys
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import capstone,pefile
from patch_native_copy import safe_review_output

def audit():
    folder=ROOT/'evidence/engine-extension'
    paths=[folder/n for n in ('native-pool-reference-inventory.json','pool-semantic-review-high.json','native-expansion-ghidra-evidence.json','native-closure-ghidra-evidence.json')]
    inv,high,old,new=[json.loads(p.read_text()) for p in paths]
    binary=Path(inv['binary']['path']);data=binary.read_bytes();sha=hashlib.sha256(data).hexdigest()
    if sha!=inv['binary']['sha256']:raise ValueError('Source changed')
    pe=pefile.PE(data=data);base=pe.OPTIONAL_HEADER.ImageBase
    md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
    def raw(i):return dict(address=hex(i.address),bytes=i.bytes.hex(),decoded=i.mnemonic+' '+i.op_str)
    bodies={};evidence=[];by={}
    for rva in (0x18b650,0x1c9590,0x311f70,0x4a0410,0x1da640,0x1da040,0x30f050):
        entry=next(r.struct for r in pe.DIRECTORY_ENTRY_EXCEPTION if r.struct.BeginAddress==rva)
        rows=list(md.disasm(pe.get_data(rva,entry.EndAddress-rva),base+rva))
        if sum(i.size for i in rows)!=entry.EndAddress-rva:raise ValueError('Decode gap')
        bodies[hex(base+rva)]=[raw(i) for i in rows];by.update({i.address:i for i in rows})
        found=[r for bundle in (old,new) for r in bundle['records'] if r.get('operation')=='procedure_pseudo_code' and r.get('parameters',{}).get('procedure')==hex(base+rva)]
        if not found:raise ValueError('Missing callee '+hex(rva))
        r=found[-1];evidence.append(dict(procedure=hex(base+rva),evidenceId=r['evidence_id'],normalizedResult=r['normalized_result']))
    expected={0x18ba8f:'rsp, 0x8109f0',0x1c99b3:'r12d, 0xffffff',0x312080:'rbx, 0x8106c0',
        0x4a0820:'ebp, 0x1010000',0x4a0826:'r12d, 0x1010000',0x4a082d:'r14d, 0x1010000',0x4a0834:'r15d, 0x1010000'}
    descriptions={0x18ba8f:('exclude-stack-frame-restoration','Matching prologue MOV EAX,8109f0; stack probe; SUB RSP,RAX. This ADD restores the same frame immediately before register pops/RET.'),
        0x1c99b3:('exclude-packed-color-arithmetic','Immediately preceding CVTTSS2SI R12D,XMM0 and SHL R12D,24 form alpha; ADDffffff fills RGB, followed by DWORD local store. This32-bit scalar is not a native pointer offset.'),
        0x312080:('exclude-separate-object-field-arithmetic','311f70 receives the separately constructed8107b0-byte scene auxiliary object. Direct callers1da640 load Scene+628 and30f050 preserves its destructor receiver. ADD8106c0 selects its16-element8-byte member vector, followed by bounded80-byte traversal; same owner as independently reviewed EH cleanup offsets.')}
    out=[]
    for rva,operand in expected.items():
        i=by[base+rva]
        if i.mnemonic!='add' or i.op_str!=operand:raise ValueError('Operand changed')
        kind,reason=descriptions.get(rva,('exclude-scalar-image-transform','Call-free8x8 coefficient transform: this32-bit integer rounding/bias constant is added to sums of fixed-point short coefficients. Results shift right17, clamp to0..255, and store one byte each to the output image. No Mission receiver or address arithmetic is involved.'))
        out.append(dict(raw(i),classification=kind,reason=reason,evidenceIds=[r['evidenceId'] for r in evidence]))
    owner=next(o for o in high['ehOwnerProof'] if o['ownerParentFunction']=='0x18030f050')
    if owner['ownerAllocationSize']!='0x8107b0' or owner['destructionCaller']!='0x1801da640':raise ValueError('Owner proof changed')
    result=dict(schemaVersion=1,targetSha256=sha,status='seven-exact-non-Mission-arithmetic-exclusions',gameExecuted=False,
        sources=[dict(path=str(p),sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in paths],
        instructionSiteExclusions=out,rawFunctionBodies=bodies,separateObjectOwner=owner,retainedEvidence=evidence,
        limitations=['Only seven listed operands are excluded. This does not establish complete computed-address coverage.'])
    path=safe_review_output(folder/'native-scalar-arithmetic-exclusions.json',[binary,*paths,Path(__file__).resolve()])
    path.write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(dict(exclusions=len(out),instructions=sum(map(len,bodies.values())))))

if __name__=='__main__':audit()
