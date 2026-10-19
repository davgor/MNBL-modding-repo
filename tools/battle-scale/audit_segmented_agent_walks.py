"""Classify all42 reviewed c60 ADD/SUB/LEA candidates for stable segmented pools.

No patches are produced. Reads only the offline native original and retained evidence.
"""
from pathlib import Path
import argparse
import hashlib
import json
import struct
import sys
from collections import Counter

ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import capstone
import pefile
from patch_native_copy import safe_review_output
SOURCE=ROOT/'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
SHA='2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'

# Exact high-water operands already owner-reviewed in G2's retained sources.
ANCHORS={
 0x57594a:0x575929,0x59e54d:0x59e529,0x59e67f:0x59e529,
 0x5c0c53:0x5c0c25,0x5c0cf9:0x5c0ccf,0x5c446f:0x5c4449,
 0x5ccbdb:0x5ccbbb,0x5ccc9a:0x5ccc74,0x5cf53c:0x5cf50f,
 0x5cfc3a:0x5cfc0c,0x5d062a:0x5d0602,0x5d295f:0x5d2912,
 0x5d490c:0x5d48df,0x5d52ad:0x5d528a,0x5ec9db:0x5ec9b9,
 0x5ef40a:0x5ef3e7,0x5fdf1a:0x5fdeef,0x621fa9:0x621f87,
 0x69f760:0x69f730,0x6a0050:0x69fff6,0x6a0cb1:0x6a0c8b,
 0x6a732d:0x6a730f,0x6a774a:0x6a772c,0x70804d:0x70802c,
 0x70980e:0x7097e5,0x70b8ab:0x70b878,0x70bb1a:0x70bacf,
 0x70c42a:0x70c3c7,0x70e8ba:0x70e894,
}
NONPOOL={
 0xbfebc:(0xbfcf0,'20 reference-pointer member array at object+c60; inner loop advances8 bytes'),
 0xc0299:(0xc0260,'destructs object+c60 array with count20,stride8'),
 0xc0aa9:(0xc0750,'constructs object+c60 array with count20,stride8'),
 0xc1b45:(0xc1af0,'globalD96DB8 resource member array; index modulo20, element width8'),
 0x12d00e:(0x12ca40,'cloth-editor setting member at+c60, named cloth_editor_preview_animation_name'),
 0x9ee35d:(0x9ee350,'C0260 EH action destructs20 reference pointers at member+c60'),
 0x9ee84d:(0x9ee840,'C0750 EH action destructs20 reference pointers at member+c60'),
}

def digest(path):return hashlib.sha256(Path(path).read_bytes()).hexdigest()

def build(source):
    folder=ROOT/'evidence/engine-extension'
    names=['native-live-agent-access-review.json','native-live-pool-growth-contract.json',
           'native-pool-interior-review.json','native-pool-helper-caller-review.json',
           'native-live-growth-rea-evidence.json']
    paths=[folder/n for n in names]+[Path(__file__)]
    docs={p.name:json.loads(p.read_text(encoding='utf-8-sig')) for p in paths if p.suffix=='.json'}
    data=source.read_bytes();assert hashlib.sha256(data).hexdigest()==SHA
    pe=pefile.PE(data=data);base=pe.OPTIONAL_HEADER.ImageBase;assert base==0x180000000
    md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
    inv=docs[names[0]]
    rows=[r for r in inv['candidates'] if set(r['classes']) & {'pointer-stride-step:Agent','pointer-stride-lea:Agent'}]
    assert len(rows)==42
    by={int(r['rva'],16):r for r in inv['candidates']}
    interiors={int(r['address'],16)-base:r for r in docs[names[2]]['instructionSiteMappings']}
    def raw(i):return dict(address=hex(i.address),rva=hex(i.address-base),bytes=i.bytes.hex(),mnemonic=i.mnemonic,operands=i.op_str)
    def span(a,z):
        out=list(md.disasm(pe.get_data(a,z-a),base+a));assert sum(i.size for i in out)==z-a,(hex(a),hex(z))
        return out
    def verify(r):
        a=int(r['address'],16) if 'address' in r else base+int(r['rva'],16)
        b=bytes.fromhex(r.get('bytes',r.get('instructionBytes','')))
        assert b and pe.get_data(a-base,len(b))==b,hex(a)
    def owner(a):
        r=by[a];verify(r);assert r['ownerProof'] or r['localReviewedRoutes']
        return r
    retained={}
    for r in docs[names[4]]['records']:
        p=r.get('parameters',{}).get('procedure')
        if p and r.get('normalized_result'):
            retained[int(p,16)-base]=dict(evidenceId=r['evidence_id'],procedure=p,normalizedResult=r['normalized_result'])
    assert all(a in retained for a,_ in NONPOOL.values()), 'Export fresh12..18 REA records first'

    # Independently associate the two exception funclets with their real parent objects.
    eh=[]
    for e in pe.DIRECTORY_ENTRY_EXCEPTION:
        s=e.struct
        if s.BeginAddress not in (0xc0260,0xc0750):continue
        h=pe.get_data(s.UnwindData,4);assert h[0]>>3 &3 and not h[0]>>3 &4
        tail=s.UnwindData+4+((h[2]+1)&~1)*2
        fi=struct.unpack('<I',pe.get_data(tail+4,4))[0]
        fields=struct.unpack('<10I',pe.get_data(fi,40));assert fields[0]==0x19930522
        for n in range(fields[1]):
            to,a=struct.unpack('<iI',pe.get_data(fields[2]+n*8,8))
            if a in (0x9ee350,0x9ee840):
                eh.append(dict(owner=hex(base+s.BeginAddress),funcInfoRva=hex(fi),funcInfoBytes=pe.get_data(fi,40).hex(),
                  state=n,toState=to,mapRva=hex(fields[2]+n*8),mapBytes=pe.get_data(fields[2]+n*8,8).hex(),action=hex(base+a)))
    assert {(r['owner'],r['action']) for r in eh}=={('0x1800c0260','0x1809ee350'),('0x1800c0750','0x1809ee840')}
    entries=[]
    for r in rows:
        verify(r);site=int(r['rva'],16)
        a,z=int(r['rangeStartRva'],16),int(r['rangeEndExclusiveRva'],16)
        ins=list(md.disasm(pe.get_data(a,z-a),base+a))
        n=next(k for k,i in enumerate(ins) if i.address==base+site)
        result=dict(site=r,classification=None,patchApproved=False,
                    contextDecodeEndExclusive=hex(ins[-1].address+ins[-1].size),
                    completeContainingFragmentDecoded=sum(i.size for i in ins)==z-a,
                    originalContext=[raw(i) for i in ins[max(0,n-24):n+8]])
        if site in ANCHORS or site==0x59ea21:
            result['classification']='primary-pool-forward-live-header-search'
            if site==0x59ea21:
                proof=interiors[0x59ea00];verify(proof)
                result['ownerProof']=proof
                result['ownerScope']='Generic c60 pool input; retained caller supplies primary Mission+2d50. Does not prove every possible caller.'
            else:
                proof=owner(ANCHORS[site]);result['ownerProof']=proof
            # The loop itself is a short straight-line header probe and two-index increment.
            cmp_index=next(k for k in range(n-1,max(n-9,-1),-1)
                           if ins[k].mnemonic=='cmp' and ins[k].operands[0].type==capstone.x86.X86_OP_MEM)
            head=ins[cmp_index];accept=ins[cmp_index+1]
            assert accept.mnemonic=='jge'
            incs=ins[n-2:n];assert all(i.mnemonic=='inc' for i in incs)
            bound=ins[n+1];back=ins[n+2]
            assert bound.mnemonic=='cmp' and back.mnemonic=='jl' and back.operands[0].imm==head.address
            assert ins[n].mnemonic=='add' and ins[n].operands[1].imm==0xc60
            result['loop']=dict(headerProbe=raw(head),liveExit=hex(accept.operands[0].imm),
                loopStart=hex(head.address),backedge=raw(back),endExclusive=hex(back.address+back.size),
                logicalIndexIncrementRegisters=[i.op_str for i in incs],boundComparison=raw(bound),
                cursorRegister=ins[n].op_str.split(',')[0],cursorBiasWithinAgent=16,
                instructions=[raw(i) for i in ins[cmp_index:n+5]])
            result['replacementContract']=dict(operation='find_next_live_index(pool,startInclusive,highWaterSnapshot)',
                result='First logical index whose signed32 slot header>=0; return highWaterSnapshot if none. Preserve stock logical-index output and surrounding comparisons.',
                boundary='Resolve each examined logical index through stable segment directory; do not resolve/dereference the end sentinel. Crossing2039→2040 must load next block, not addc60 to old block.',
                downstream='After this initial search, outer code can use short successor metadata and index*c60. Both must be routed/widened consistently; replacing this ADD alone is insufficient.',
                epoch='Use one published directory/high-water snapshot for this search. Header mutation and deletion must follow the same engine ownership/barrier contract.')
        elif site in NONPOOL:
            proc,reason=NONPOOL[site]
            result.update(classification='non-pool-member-address',reason=reason,retainedRea=retained[proc],
                          replacementContract={'operation':'leave this c60 member offset unchanged'})
            if site in (0x9ee35d,0x9ee84d):
                result['exceptionOwner']=next(x for x in eh if x['action']==hex(base+proc))
        elif site==0x6419f0:
            assert r['ownerStatus']=='fresh-must-flow-stack-exclusion'
            assert r['operands']=='rbp, [rsp - 0xc60]'
            result.update(classification='stack-frame-address',replacementContract={'operation':'leave stack frame setup unchanged'})
        elif site==0x5756cd:
            result.update(classification='primary-pool-field-walk',ownerProof=owner(0x5756a0),
                loop=dict(start='0x1805756b3',endExclusive='0x1805756da',cursor='RBX=Agent+564',count='RDI=high-water snapshot',
                    instructions=[raw(i) for i in span(0x5756a0,0x5756da)]),
                replacementContract=dict(operation='for logicalIndex in[0,highWaterSnapshot): Agent=resolve(index)',
                    predicate='Keep original[Agent+560]==1 and[Agent+564]&4000 tests; call621130 on[Agent+880] only when tests pass.',
                    boundary='A helper must return next Agent+564, preserving field bias and loop count. Existing callback can reenter; publish growth outside this reader epoch.'))
        elif site==0x5d081d:
            result.update(classification='primary-pool-backward-highwater-search',ownerProof=interiors[0x5d07e8],
                loop=dict(start='0x1805d0815',endExclusive='0x1805d0837',cursor='RCX=Agent+10',
                    instructions=[raw(i) for i in span(0x5d0756,0x5d0837)]),
                replacementContract=dict(operation='After deleting the previous highest slot, find previous live logical index; set highWater=found+1 or0.',
                    boundary='Resolve each predecessor index through directory; index2040→2039 crosses backwards. Do not calculate memory below block0 when cursor reaches-1.',
                    metadata='Preserve deletion transaction, successor/predecessor link updates and count decrement; old inline short metadata must be redirected.'))
        elif site==0x5d7fd1:
            result.update(classification='c60-pool-active-reset-walk',
                ownerProofSource='native-live-pool-growth-contract.json nativeBodies.agentPoolDestructor and reviewed pool helper callers',
                ownerProof=[e for e in docs[names[3]]['entries'] if e['target']=='0x1805d7df0'],
                loop=dict(start='0x1805d7e30',endExclusive='0x1805d7fe2',cursor='RDI=Agent+880',
                    instructions=[raw(i) for i in span(0x5d7df0,0x5d8018)]),
                replacementContract=dict(operation='For each constructed slot in each owned block, perform original active-reset logic when header>=0.',
                    boundary='Visit stable blocks without moving objects. Keep+880 cursor bias; use constructed count rather than active count or reservation capacity.',
                    destruction='Reset all active records, then invoke element destructor for every constructed record. Rewrite inline table clearing and normal/EH destructor traversal too.'))
        elif site==0x5d8273:
            result.update(classification='c60-pool-constructor-header-walk',
                ownerProofSource='native-live-pool-growth-contract.json elementAdapters and agentPoolConstructor',
                loop=dict(start='0x1805d8270',endExclusive='0x1805d8280',cursor='RAX=Agent+10',
                    instructions=[raw(i) for i in span(0x5d8220,0x5d82aa)]),
                replacementContract=dict(operation='After each successful actual element constructor, set its signed32 index header to-1 before publication.',
                    boundary='Iterate per newly allocated segment; never call original whole-pool constructor on payload-only external block because it writes fixed-position inline metadata.',
                    rollback='Track completed element counts per pool/block, reverse-destruct only completed unpublished Agent elements on failure.'))
        else:raise AssertionError('Unclassified '+hex(site))
        entries.append(result)
    counts=dict(Counter(r['classification'] for r in entries))
    assert counts['primary-pool-forward-live-header-search']==30
    assert counts['non-pool-member-address']==7
    # Contract-level boundary examples; these are not emitted hook execution tests.
    block=2040;stride=0xc60;bases=[0x10000000,0x90000000,0x170000000]
    def resolve(i):
        assert 0<=i<block*len(bases)
        return bases[i//block]+(i%block)*stride
    boundary=[]
    for left,right in [(2038,2039),(2039,2040),(4079,4080),(2040,2039),(4080,4079)]:
        affine=resolve(left)+(right-left)*stride;actual=resolve(right)
        crosses=left//block!=right//block;assert (affine!=actual)==crosses
        boundary.append(dict(fromIndex=left,toIndex=right,oldAffinePointer=hex(affine),requiredPointer=hex(actual),crossesBlock=crosses))
    sources=[dict(path=str(p.resolve()),sha256=digest(p)) for p in [source]+paths]
    return dict(schemaVersion=1,status='all42-stride-candidates-classified-not-hook-approved',
        nativeSourceSha256=SHA,gameExecuted=False,installedFilesReadOrModified=False,patchApproved=False,
        sources=sources,summary=dict(total=42,classes=counts,unclassified=0),entries=entries,
        boundaryContractExamples=boundary,
        pointerListDistinction='These42 c60 candidates contain no8-byte pointer-list worker increment.5d52ad scans primary headers while rebuilding Agent pointer lists; later workers dereference stored Agent pointers and iterate8-byte list entries. Stable payload addresses preserve those pointer values, but list rebuild/epoch publication still needs the engine barrier.',
        acceptanceRequirements=['Create live slots on each side of2039/2040 and4079/4080; compare forward and backward results with logical model.',
            'Make holes spanning block boundaries and remove highest live slots; check high-water reaches correct previous live index or0.',
            'Exercise empty/high-water0 and all-dead searches without resolving sentinel or negative index.',
            'Keep constructor failure and reset/teardown tests separate from active count; all constructed records must be destroyed exactly once.',
            'Verify caller register/flags/unwind/CFG and incoming edge constraints before any next-slot detour. No such detours are approved here.'],
        limitations=['Classification is complete only for the42 selected ADD/SUB/LEA candidates, not every engine iteration.',
            'Index*stride arithmetic, pointer subtraction, inline16-bit successor metadata, cached high-water, constructor iterators, other pools and unrecognized code remain separate integration requirements.',
            'Retained owner contracts are reused with exact bytes; no receiver identity is inferred solely fromc60 or62da44 numeric values.',
            'No runtime engine growth or emitted machine-code hook was tested.'])

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--source',type=Path,default=SOURCE)
    ap.add_argument('--output',type=Path,default=ROOT/'evidence/engine-extension/native-segmented-agent-walks.json')
    a=ap.parse_args();d=build(a.source)
    out=safe_review_output(a.output,[p['path'] for p in d['sources']]);out.write_text(json.dumps(d,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(status=d['status'],summary=d['summary'],sha256=digest(out))))
if __name__=='__main__':main()
