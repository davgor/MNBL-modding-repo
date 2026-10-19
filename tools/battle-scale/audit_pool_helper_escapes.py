"""Execution-free entry-reference and default-Agent publication review.

Byte scans are conservative candidate discovery; no discovered numeric value
authorizes a patch or proves the absence of arbitrary computed addresses.
"""
from pathlib import Path
import hashlib,json,struct,sys
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import capstone,pefile
from patch_native_copy import safe_review_output

HELPERS=[0x5d7df0,0x5d8020,0x5d81e0,0x5d8220,0x5d8390,0x5d8610,
         0x5d8730,0x5d8740,0x5d87d0,0x5d8840,0x5d91f0,0x5d9350,0x5d94a0]

def build():
    folder=ROOT/'evidence/engine-extension'
    paths=[folder/n for n in ('native-pool-reference-inventory.json','native-pool-layout.json',
          'native-pool-ghidra-evidence.json','native-closure-ghidra-evidence.json')]
    inv,layout,pool,closure=[json.loads(p.read_text()) for p in paths]
    binary=Path(inv['binary']['path']);data=binary.read_bytes();sha=hashlib.sha256(data).hexdigest()
    if sha!=layout['binarySha256']:raise ValueError('Wrong native binary')
    pe=pefile.PE(data=data);base=pe.OPTIONAL_HEADER.ImageBase
    md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
    targets={base+x for x in HELPERS}
    def raw(i):return dict(address=hex(i.address),bytes=i.bytes.hex(),decoded=i.mnemonic+' '+i.op_str)
    def span(a,z):
        out=list(md.disasm(pe.get_data(a,z-a),base+a))
        if sum(i.size for i in out)!=z-a:raise ValueError('Decode gap')
        return out
    branches=[];rip=[]
    for section in pe.sections:
        if not section.Characteristics&0x20000000:continue
        blob=section.get_data();rva=section.VirtualAddress
        # Test every byte position with a near branch opcode; this also reports
        # possible embedded-data decodes rather than silently assuming boundaries.
        for opcode in (b'\xe8',b'\xe9',b'\xeb')+tuple(bytes((x,)) for x in range(0x70,0x80))+tuple(bytes((0x0f,x)) for x in range(0x80,0x90)):
            start=0
            while True:
                at=blob.find(opcode,start)
                if at<0:break
                start=at+1
                size=2 if opcode==b'\xeb' or 0x70<=opcode[0]<0x80 else 6 if len(opcode)==2 else 5
                if at+size>len(blob):continue
                delta=struct.unpack_from('<b' if size==2 else '<i',blob,at+len(opcode))[0]
                dest=base+rva+at+size+delta
                if dest in targets:
                    i=next(md.disasm(blob[at:at+15],base+rva+at))
                    branches.append(dict(raw(i),target=hex(dest),discovery='every-byte direct-branch candidate'))
        for prefix in tuple(bytes((rex,0x8d)) for rex in range(0x48,0x50)):
            start=0
            while True:
                at=blob.find(prefix,start)
                if at<0:break
                start=at+1
                i=next(md.disasm(blob[at:at+15],base+rva+at),None)
                if not i or i.mnemonic!='lea':continue
                for op in i.operands:
                    if op.type==capstone.x86.X86_OP_MEM and op.mem.base==capstone.x86.X86_REG_RIP:
                        dest=i.address+i.size+op.mem.disp
                        if dest in targets:rip.append(dict(raw(i),target=hex(dest)))
    literals=[]
    for target in sorted(targets):
        for size,value in ((8,target),(4,target-base)):
            needle=value.to_bytes(size,'little');start=0
            while True:
                at=data.find(needle,start)
                if at<0:break
                start=at+1
                rva=pe.get_rva_from_offset(at)
                section=pe.get_section_by_rva(rva)
                literals.append(dict(target=hex(target),width=size,fileOffset=at,rva=hex(rva),
                    section=section.Name.rstrip(b'\0').decode() if section else None,
                    bytes=needle.hex(),classification='raw-literal-candidate-not-pointer-proof'))
    # Associate every literal with actual PE exception records, not section-name
    # guesses. These associations explain address data without treating it as a
    # gameplay registration or an arbitrary callable function-pointer table.
    metadata={}
    for entry in pe.DIRECTORY_ENTRY_EXCEPTION:
        rf=entry.struct
        for n,field in enumerate(('BeginAddress','EndAddress','UnwindData')):
            metadata[rf.get_file_offset()+4*n]=dict(kind='runtime-function-'+field,owner=hex(base+rf.BeginAddress),bytes=pe.get_data(pe.get_rva_from_offset(rf.get_file_offset()),12).hex())
        u=rf.UnwindData
        head=pe.get_data(u,4)
        if len(head)!=4 or head[0]&7!=1:continue
        flags=head[0]>>3;tail=u+4+((head[2]+1)&~1)*2
        if flags&4:
            for n,field in enumerate(('BeginAddress','EndAddress','UnwindData')):
                metadata[pe.get_offset_from_rva(tail)+4*n]=dict(kind='chained-runtime-function-'+field,owner=hex(base+rf.BeginAddress),unwindInfoRva=hex(u),bytes=pe.get_data(tail,12).hex())
        elif flags&3:
            try:
                fi=struct.unpack('<I',pe.get_data(tail+4,4))[0]
                fields=struct.unpack('<10I',pe.get_data(fi,40))
            except (struct.error,pefile.PEFormatError):continue
            if fields[0]!=0x19930522 or fields[5]>10000:continue
            for n in range(fields[5]):
                row=fields[6]+8*n
                metadata[pe.get_offset_from_rva(row)]=dict(kind='CxxFrameHandler3-IP-to-state',owner=hex(base+rf.BeginAddress),funcInfoRva=hex(fi),rowRva=hex(row),bytes=pe.get_data(row,8).hex())
    for item in literals:
        if item['width']==4 and item['fileOffset'] in metadata:
            item['classification']='exception-metadata-not-gameplay-publication'
            item['metadataAssociation']=metadata[item['fileOffset']]
    records=[]
    for address in (0x5d8220,0x606a20,0x6070c0,0x696420,0x619d20):
        matches=[r for b in (pool,closure) for r in b['records'] if r.get('operation')=='procedure_pseudo_code'
                 and r.get('parameters',{}).get('procedure')==hex(base+address)]
        if not matches:raise ValueError('Missing retained constructor evidence '+hex(address))
        r=matches[-1];records.append(dict(procedure=hex(base+address),evidenceId=r['evidence_id'],normalizedResult=r['normalized_result']))
    bodies={}
    for a,z in ((0x606a20,0x607074),(0x619d20,0x619dd6),(0x04dcb0,0x04dcbb)):
        bodies[hex(base+a)]=[raw(i) for i in span(a,z)]
    # Determine the actual leaf end, without treating its first RET as enough
    # for arbitrary functions: this exact helper is straight-line, no branches.
    code=[]
    for i in md.disasm(pe.get_data(0x696420,0x300),base+0x696420):
        code.append(i)
        if i.mnemonic=='ret':break
    if any(i.group(capstone.CS_GRP_JUMP) or i.mnemonic=='call' for i in code):raise ValueError('Helper is no longer straight-line')
    bodies[hex(base+0x696420)]=[raw(i) for i in code]
    result=dict(schemaVersion=1,targetSha256=sha,status='helper-entry-and-default-constructor-review',gameExecuted=False,
        sources=[dict(path=str(p),sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in paths],
        helperEntries=[hex(t) for t in sorted(targets)],nearBranchCandidates=branches,
        ripRelativeAddressMaterializations=rip,rawAbsoluteAndRvaLiterals=literals,
        inventoryDirectCallers={hex(t):inv['directCallersByCandidateFunction'].get(hex(t),[]) for t in sorted(targets)},
        remainingUnclassifiedLiterals=[x for x in literals if x['classification']=='raw-literal-candidate-not-pointer-proof'],
        retainedConstructorEvidence=records,completeConstructorAndHelperInstructions=bodies,
        defaultConstructionReview=[
          '5d8220 receives both primary and second pool bases only from Mission constructor; EH iterator constructs2040 Agent records, then initializes index table/count/high-water fields.',
          '606a20 writes Agent/member state and returns its receiver.696420 only initializes receiver fields; its RDX input is explicitly zero.619d20 sees its reference member+30 explicitly zeroed immediately before call, so its release virtual is unreachable during this constructor.',
          'Six reference slots are initialized by04dcb0, which only writes a null QWORD and returns its argument. The later606a20 release loop reads these null slots; no pointer is published through its virtual branch.',
          'The remaining ordinary call is atan2f with scalar inputs; the other call is the existing EH iterator. Agent Mission owner+ab8 is set zero by606a20. Default construction therefore does not publish the second-pool Agent addresses to gameplay registries.',
          'This constructor fact does not establish that gameplay can never construct a computed pointer into pool B; that obligation requires the helper caller and Mission address-escape inventory.'],
        limitations=['Near-branch and literal scans produce candidates, including possible data. RVA32 matches in pdata/EH metadata are not automatically function pointers.',
          'Split arithmetic, nonstandard code generation and external unreviewed mods remain outside this scan. No runtime or whole-patch readiness claim.'])
    out=folder/'native-pool-helper-escape-review.json'
    safe_review_output(out,[binary]+paths).write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(dict(branches=len(branches),ripMaterializations=len(rip),rawLiterals=len(literals))))
if __name__=='__main__':build()
