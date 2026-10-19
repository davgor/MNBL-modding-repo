"""Independent read-only inventory for the ranged renderer 128-page container."""
from pathlib import Path
import bisect, hashlib, json, struct, sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT/'build/battle-scale/python-libs'))
import capstone, pefile
from capstone.x86 import X86_OP_MEM, X86_OP_IMM, X86_REG_RIP

SOURCE = ROOT/'build/battle-scale/native-run-20261017-102915-64ab5481/backup-0'
OUTPUT = ROOT/'evidence/engine-extension/native-ranged-page-consumer-inventory.json'
EXPECTED = '2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'

def main():
    raw=SOURCE.read_bytes(); assert hashlib.sha256(raw).hexdigest()==EXPECTED
    pe=pefile.PE(data=raw); base=pe.OPTIONAL_HEADER.ImageBase
    dec=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64); dec.detail=True
    ranges={e.struct.BeginAddress:e.struct.EndAddress for e in pe.DIRECTORY_ENTRY_EXCEPTION}
    ranges.update({0xf8720:0xf8752,0xf8260:0xf82b8,0xf2500:0xf289e})
    starts=sorted(ranges)
    global_refs=[]; shape_refs=[]; direct_refs=[]; gaps=[]; body_cache={}; selected=set()
    call_targets={0x899d0,0xf8720,0xf8260,0xf7fb0,0x1e0270,0xe6000}
    def row(i, owner): return {'rva':hex(i.address-base),'functionRva':hex(owner),'bytes':i.bytes.hex(),'mnemonic':i.mnemonic,'operands':i.op_str}
    def decode(start,end):
        data=pe.get_data(start,end-start); cursor=0; found=[]
        while cursor<len(data):
            for i in dec.disasm(data[cursor:],base+start+cursor):
                found.append(i);cursor=i.address-base-start+i.size
            if cursor<len(data):gaps.append(hex(start+cursor));cursor+=1
        return found
    for start in starts:
        xs=decode(start,ranges[start]); candidate=False
        for i in xs:
            isglobal=any(o.type==X86_OP_MEM and o.mem.base==X86_REG_RIP and i.address+i.size+o.mem.disp==base+0xd96d18 for o in i.operands)
            shape=any((o.type==X86_OP_MEM and o.mem.disp in (0x910,0x488,0x9c8)) or (o.type==X86_OP_IMM and o.imm==0x488) for o in i.operands)
            if isglobal:global_refs.append(row(i,start));candidate=True
            if shape:shape_refs.append(row(i,start));candidate=True
            if i.mnemonic in ('call','jmp') and i.operands[0].type==X86_OP_IMM and i.operands[0].imm-base in call_targets:
                direct_refs.append({**row(i,start),'targetRva':hex(i.operands[0].imm-base)});candidate=True
        if candidate or start in (0xf8720,0xf8260,0xf2500,0x1e0270,0xe6580,0xe5ab0,0x23f180,0x23f437,0x899d0,0x89a18,0x89afa,0xf7fb0):
            body_cache[hex(start)]=[row(i,start) for i in xs];selected.add(start)
    # Raw displacement coverage independently includes references outside runtime
    # function metadata. Candidate decoding alone never proves a code boundary.
    raw_refs=[]
    for sec in pe.sections:
        if not sec.Characteristics&0x20000000:continue
        data=sec.get_data(); rv=sec.VirtualAddress
        for k in range(len(data)-4):
            apparent=rv+k+4+struct.unpack_from('<i',data,k)[0]
            if not 0xd96d10<=apparent<=0xd96d18:continue
            for pos in range(max(0,k-11),k):
                i=next(dec.disasm(data[pos:pos+15],base+rv+pos,count=1),None)
                if i and any(o.type==X86_OP_MEM and o.mem.base==X86_REG_RIP and i.address+i.size+o.mem.disp==base+0xd96d18 for o in i.operands):
                    n=bisect.bisect_right(starts,i.address-base)-1;owner=starts[n] if n>=0 else 0
                    rec=row(i,owner);rec['anchoredRuntimeBoundary']=any(x['rva']==rec['rva'] and x['bytes']==rec['bytes'] for x in global_refs)
                    raw_refs.append(rec)
    raw_refs=list({(x['rva'],x['bytes']):x for x in raw_refs}.values())
    raw_shape=[]
    for sec in pe.sections:
        if not sec.Characteristics&0x20000000:continue
        data=sec.get_data();rv=sec.VirtualAddress
        for value in (0x488,0x910,0x9c8):
            needle=struct.pack('<I',value);at=data.find(needle)
            while at>=0:
                for pos in range(max(0,at-11),at):
                    i=next(dec.disasm(data[pos:pos+15],base+rv+pos,count=1),None)
                    if i and any((o.type==X86_OP_MEM and o.mem.disp==value) or(o.type==X86_OP_IMM and o.imm==value) for o in i.operands):
                        n=bisect.bisect_right(starts,i.address-base)-1;owner=starts[n] if n>=0 else 0
                        rec=row(i,owner);rec['shapeValue']=hex(value);rec['anchoredRuntimeBoundary']=any(x['rva']==rec['rva'] and x['bytes']==rec['bytes'] for x in shape_refs)
                        raw_shape.append(rec)
                at=data.find(needle,at+1)
    raw_shape=list({(x['rva'],x['bytes'],x['shapeValue']):x for x in raw_shape}.values())
    literal_refs=[];needle=struct.pack('<Q',base+0xd96d18);at=raw.find(needle)
    while at>=0:literal_refs.append(hex(at));at=raw.find(needle,at+1)
    leaf=0x5d520
    body_cache[hex(leaf)]=[row(i,leaf) for i in dec.disasm(pe.get_data(leaf,0x13),base+leaf)]
    result={'schemaVersion':1,'status':'independent-inventory-awaiting-semantic-review','sourcePath':str(SOURCE),'sourceSha256':EXPECTED,'scriptSha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),'gameExecuted':False,'runtimeFragmentsScanned':len(ranges),'globalReferences':global_refs,'rawGlobalReferenceCandidates':raw_refs,'absoluteGlobalPointerLiteralFileOffsets':literal_refs,'shapeReferences':shape_refs,'rawShapeCandidates':raw_shape,'directSelectedCallees':direct_refs,'selectedBodies':body_cache,'decodeGapBytes':gaps,'limitations':['Linear runtime-fragment decoding includes possible inline data; raw candidates require instruction-boundary corroboration.','No arbitrary indirect/computed alias closure claim follows from displacement matching alone.','This inventory does not modify the native image or propose accepted patch bytes.']}
    OUTPUT.write_text(json.dumps(result,indent=2)+'\n');print(json.dumps({'output':str(OUTPUT),'globalRefs':len(global_refs),'rawGlobalCandidates':len(raw_refs),'shapeRefs':len(shape_refs),'selectedBodies':len(body_cache),'gapBytes':len(gaps)}))

if __name__=='__main__':main()
