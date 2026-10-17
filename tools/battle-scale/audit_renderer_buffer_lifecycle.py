"""Read-only renderer page-buffer lifecycle audit of the pinned offline original."""
from __future__ import annotations
import argparse
import bisect
import hashlib
import json
from pathlib import Path
import struct
import sys

ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import capstone
import pefile
from capstone.x86 import X86_OP_MEM,X86_OP_IMM,X86_REG_RIP
from native_instruction_semantics import memory_operand_writes
from patch_native_copy import safe_review_output
from classify_pool_references import basic,register

EXPECTED='2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
LEAVES={0x1e4ee0:0x1e4f10,0xf2500:0xf289e,0x9f1a40:0x9f1a53,0x9f22e0:0x9f22f3,
        0xa04c90:0xa04c9c,0x9f28a0:0x9f28b3,0x9f28c0:0x9f28d3,
        0x9ffa20:0x9ffa33,0x9ffa40:0x9ffa53}
TARGETS={0xd96d18,0xe6580,0xe5ab0,0xe6120,0x1e0130,0x1e0100,0x1e49a0,0x1e4ee0,0xf2500,0x1e0270}


def run(source,output):
    source=source.resolve()
    # Never accept a live installation path, even when its current bytes match.
    inventory=ROOT/'evidence/engine-extension/native-pool-reference-inventory.json'
    installed=Path(json.loads(inventory.read_text())['binary']['path']).resolve()
    if source==installed:raise ValueError('Only an offline original copy is permitted')
    raw=source.read_bytes()
    if hashlib.sha256(raw).hexdigest()!=EXPECTED:raise ValueError('Wrong source hash')
    pe=pefile.PE(data=raw);base=pe.OPTIONAL_HEADER.ImageBase
    dec=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);dec.detail=True
    entries={e.struct.BeginAddress:e.struct for e in pe.DIRECTORY_ENTRY_EXCEPTION}
    bounds={s:e.EndAddress for s,e in entries.items()};bounds.update(LEAVES)
    # Exact retained allocation span inside a runtime range with trailing data.
    bounds[0x4ca4f]=0x4ca76
    bounds[0x88681]=0x88769
    starts=sorted(bounds)
    cache={}
    def body(start):
        if start not in cache:
            data=pe.get_data(start,bounds[start]-start)
            xs=list(dec.disasm(data,base+start))
            if sum(i.size for i in xs)!=len(data):raise ValueError('Incomplete exact body '+hex(start))
            cache[start]={i.address:i for i in xs}
        return cache[start]
    def instruction_at(addr):
        rv=addr-base;n=bisect.bisect_right(starts,rv)-1
        if n<0:return None
        start=starts[n]
        if rv>=bounds[start]:return None
        try:return body(start).get(addr)
        except ValueError:return None
    refs={};unanchored=[]
    for sec in pe.sections:
        if not sec.Characteristics&0x20000000:continue
        data=sec.get_data();rva=sec.VirtualAddress
        for k in range(len(data)-4):
            apparent=rva+k+4+struct.unpack_from('<i',data,k)[0]
            wanted=[t for t in range(apparent,apparent+9) if t in TARGETS]
            if not wanted:continue
            found=[]
            for pos in range(max(0,k-11),k):
                ins=instruction_at(base+rva+pos)
                if ins is None:continue
                for target in wanted:
                    if (any(o.type==X86_OP_MEM and o.mem.base==X86_REG_RIP and ins.address+ins.size+o.mem.disp==base+target for o in ins.operands)
                            or ((ins.mnemonic=='call' or ins.group(capstone.CS_GRP_JUMP)) and ins.operands and ins.operands[0].type==X86_OP_IMM and ins.operands[0].imm==base+target)):
                        row={'target':hex(base+target),**basic(ins),'boundaryProof':'exact runtime fragment or explicitly retained complete leaf'}
                        row['writesGlobal']=target==0xd96d18 and any(memory_operand_writes(ins,n) for n,o in enumerate(ins.operands) if o.type==X86_OP_MEM)
                        refs[(ins.address,target)]=row;found.append(ins.address)
            if not found:
                possible=[]
                for pos in range(max(0,k-11),k):
                    ins=next(dec.disasm(data[pos:pos+15],base+rva+pos,count=1),None)
                    if ins is None:continue
                    for target in wanted:
                        if (any(o.type==X86_OP_MEM and o.mem.base==X86_REG_RIP and ins.address+ins.size+o.mem.disp==base+target for o in ins.operands)
                                or ((ins.mnemonic=='call' or ins.group(capstone.CS_GRP_JUMP)) and ins.operands and ins.operands[0].type==X86_OP_IMM and ins.operands[0].imm==base+target)):
                            possible.append({'target':hex(base+target),**basic(ins),
                                'writesMemory':any(memory_operand_writes(ins,n) for n,o in enumerate(ins.operands) if o.type==X86_OP_MEM)})
                unanchored.append({'displacementRva':hex(rva+k),'possibleTargets':[hex(base+t) for t in wanted],
                    'classification':'unanchored-decoded-reference' if possible else 'incidental-rel32-arithmetic-no-reference-instruction',
                    'candidateDecodings':possible})
    actions=set(LEAVES)-{0x1e4ee0,0xf2500}
    actions.update({0x9f2a70,0x9ff9e0,0x9ffbc0})
    eh=[]
    for start,e in entries.items():
        h=pe.get_data(e.UnwindData,4);flags=h[0]>>3
        if flags&4 or not flags&3:continue
        tail=e.UnwindData+4+((h[2]+1)&~1)*2
        handler,fi=struct.unpack('<II',pe.get_data(tail,8));data=pe.get_data(fi,12)
        if len(data)!=12:continue
        magic,count,um=struct.unpack('<III',data)
        if magic not in (0x19930520,0x19930521,0x19930522) or count>10000:continue
        for n in range(count):
            previous,action=struct.unpack('<iI',pe.get_data(um+8*n,8))
            if action in actions:eh.append({'ownerProcedure':hex(base+start),'funcInfoRva':hex(fi),'unwindMapRva':hex(um),
                'state':n,'nextState':previous,'action':hex(base+action),'exactMapEntry':pe.get_data(um+8*n,8).hex()})
    selected=(0x4ca4f,0x520c0,0x88681,0xe6580,0xe5ab0,0xe6120,0x1e0130,0x1e0100,0x1e49a0,
              0xe09a0,0x2412f0,0x9f2a70,0x9ff9e0,0x9ffbc0,*LEAVES)
    exact=[]
    for start in sorted(set(selected)):
        exact.append({'start':hex(base+start),'endExclusive':hex(base+bounds[start]),
                      'instructions':[basic(i) for i in body(start).values()]})
    r15_definitions=[i.address for i in body(0xe09a0).values() if i.address<=base+0xe0d5a
                     and 'r15' in {register(i,r) for r in i.regs_access()[1]}]
    if r15_definitions!=[base+0xe09f0]:raise ValueError('Frame owner register provenance changed')
    # Retain REA evidence in the new artifact so replay need not keep ignored queues.
    previous=json.loads(output.read_text()) if output.exists() else {}
    retained=previous.get('retainedReaEvidence',[])
    for n in (3,4,5,8,9):
        path=ROOT/f'build/battle-scale/native-stall-queue/{n:03}.response.json'
        if path.exists():
            record=json.loads(path.read_text())
            if record.get('subject',{}).get('digest',{}).get('sha256')!=EXPECTED:raise ValueError('REA source mismatch')
            retained=[r for r in retained if r['evidence_id']!=record['evidence_id']]+[record]
    globalrefs=[r for r in refs.values() if r['target']==hex(base+0xd96d18)]
    writes=[r for r in globalrefs if r['writesGlobal']]
    if {r['address'] for r in writes}!={'0x18004ca6f','0x18005323e'}:raise ValueError('Unexpected global writer inventory')
    unknown_writers=[row for item in unanchored for row in item['candidateDecodings']
                     if row['target']==hex(base+0xd96d18) and row['writesMemory']]
    if unknown_writers:raise ValueError('Unanchored global writer requires review')
    absolute=[]
    for target in TARGETS:
        needle=struct.pack('<Q',base+target);position=raw.find(needle)
        while position>=0:
            absolute.append({'target':hex(base+target),'fileOffset':position})
            position=raw.find(needle,position+1)
    result={'schemaVersion':1,'status':'scoped-lifecycle-proven-with-asynchronous-completion-obligation',
      'targetSha256':EXPECTED,'gameExecuted':False,'sourcePath':str(source),
      'scriptSha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
      'dependencyHashes':{name:hashlib.sha256((ROOT/'tools/battle-scale'/name).read_bytes()).hexdigest()
                          for name in ('native_instruction_semantics.py','classify_pool_references.py','patch_native_copy.py')},
      'decoderVersion':capstone.__version__,'peParserVersion':pefile.__version__,
      'summary':{'globalReferences':len(globalrefs),'globalWrites':len(writes),'lifecycleReferences':len(refs)-len(globalrefs),
                 'exceptionCleanupStates':len(eh),'exactInstructions':sum(len(x['instructions']) for x in exact),
                 'ownerAllocationBytes':0xf910,'bufferPairs':2,'buffersPerPair':2,'pagePointersPerBuffer':32,
                 'unanchoredDecodedReferencePositions':sum(bool(x['candidateDecodings']) for x in unanchored),
                 'unanchoredLifecycleReferencePositions':sum(any(y['target']!=hex(base+0xd96d18) for y in x['candidateDecodings']) for x in unanchored),
                 'unanchoredGlobalWriters':len(unknown_writers),'absoluteVa64References':len(absolute)},
      'layout':{'globalRva':'0xd96d18','ownerSize':'0xf910','aggregateOwnerOffset':'0xb8',
        'pairOwnerOffsets':['0x9d0','0xc28'],'bufferStride':'0x128','pairSelectorOffset':'0x250',
        'pageCount':32,'pagePointers':['0x8','0x100'],'readinessBytes':['0x108','0x127'],
        'counterOffset':'0x0','firstInvalidPagePointerOffset':'0x108'},
      'contracts':{
        'allocation':'4ca52 allocates f910 bytes;4ca62 allocator;4ca6a constructor e6580;4ca6f publishes result to D96D18.',
        'construction':'e6580 uses owner+b8 aggregate, constructs two buffers of stride128 at aggregate+918 and aggregate+b70 via1e4ee0, then zeros each pair selector+250.',
        'pageInitialization':'1e4ee0 atomically zeros count; for indices0..31 stores null pointer at8+index*8 and XCHG readiness byte108+index to1.',
        'frameReset':'e09a0 calls1e0270(owner+b8) at e0d48 and f2500(owner) at e0d5a. f2500 flips each selector modulo2 and XCHGs the newly selected counter to0. Pointers and readiness flags persist; there is no per-frame page free.',
        'teardown':'520c0 invokes e6120, then e5ab0, frees owner with74e270, clears D96D18 using R15=0 established521c4. e5ab0 also calls e6120, then1e0130(owner+b8). Aggregate dtor destroys both pairs count2 stride128. 1e49a0 frees each non-null pointer0..31 via74e270 and zeros it.',
        'pageAllocationSizeIndependence':'1e49a0 does not inspect page contents or pass a size to74e270. Preserving32 pointers and128-byte buffer headers preserves its iteration and pointer ownership when page payload allocations grow.',
        'sharedUser':'2412f0 has a stack-local container; its C++ unwind map invokes a04c90, which calls1e49a0 at frame+a0. Increasing the shared destructor count or header offsets would also affect this separate stack allocation.',
        'publication':'Writer88440 uses LOCK XADD to reserve count, CAS publishes each allocated page pointer, losers free their allocation, winning thread XCHGs readiness byte from1 to0. Other reservers spin until0. This readiness protocol covers page allocation, not completion of later payload writes.'},
      'remainingObligations':[
        'CPU upload call precedes selector flip, but that alone does not prove asynchronous backend upload completion before a buffer is reused two frames later. Consumer/backend ownership must establish copying or synchronization.',
        'No inference that page-ready0 means all producers finished their payload writes; producer-task completion before upload remains a separate ordering contract.',
        'The global scan covers exact direct RIP-relative references and direct lifecycle calls/materializations at anchored instruction boundaries, not arbitrary computed aliases. Unanchored rel32 arithmetic is retained as discovery data.',
        'A payload-page-size patch must change every producer/consumer division, mask, byte size and upload descriptor consistently; this audit authorizes no patch by itself.'],
      'globalReferences':globalrefs,'lifecycleReferences':[r for r in refs.values() if r not in globalrefs],
      'unanchoredArithmeticCandidates':unanchored,'exceptionCleanupOwners':eh,'exactProcedures':exact,'retainedReaEvidence':retained}
    result['absoluteVa64References']=absolute
    output=safe_review_output(output,[source,installed,inventory,Path(__file__).resolve()])
    output.write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(result['summary']))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source',type=Path,required=True)
    parser.add_argument('--output',type=Path,default=ROOT/'evidence/engine-extension/native-renderer-buffer-lifecycle-review.json')
    args=parser.parse_args();run(args.source,args.output)
