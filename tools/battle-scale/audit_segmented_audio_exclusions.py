"""Source-bound audio receiver associations; no engine mutation or hooks."""
from pathlib import Path
import argparse,hashlib,json,struct,sys
from collections import Counter
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import capstone,pefile
from capstone.x86 import X86_OP_IMM
from patch_native_copy import safe_review_output
SOURCE=ROOT/'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
EXPECTED='2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'

def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()

def build():
    raw=SOURCE.read_bytes();assert hashlib.sha256(raw).hexdigest()==EXPECTED
    pe=pefile.PE(data=raw);base=pe.OPTIONAL_HEADER.ImageBase
    md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
    baseline=ROOT/'evidence/engine-extension/native-segmented-companion-accesses.json'
    prior=json.loads(baseline.read_text());assert prior['nativeSourceSha256']==EXPECTED
    bundle=ROOT/'evidence/engine-extension/native-live-growth-rea-evidence-4.json'
    evidence=json.loads(bundle.read_text())
    assert evidence['binarySha256']==EXPECTED
    targets={0x821a19,0x835a55,0x86bc50,0x86bdf7,0x8ec1a8,0x8ecc4d}
    candidates={int(r['multiply']['rva'],16):r['multiply'] for r in prior['candidates'] if int(r['multiply']['rva'],16) in targets}
    assert set(candidates)==targets
    rows={}
    def record(i):
        r={'address':hex(i.address),'bytes':i.bytes.hex(),'mnemonic':i.mnemonic,'operands':i.op_str};rows[i.address]=r;return r
    def span(a,z):
        body=list(md.disasm(pe.get_data(a,z-a),base+a));assert sum(i.size for i in body)==z-a,(hex(a),hex(z))
        return [record(i) for i in body]
    def exact(a,m,o):
        i=next(md.disasm(pe.get_data(a,16),base+a));assert (i.mnemonic,i.op_str)==(m,o),(hex(a),i.mnemonic,i.op_str)
        return record(i)
    ranges=[(x.struct.BeginAddress,x.struct.EndAddress,x.struct.UnwindData) for x in pe.DIRECTORY_ENTRY_EXCEPTION]
    def chain_root(e):
        b,z,u=e;chain=[]
        while True:
            h=pe.get_data(u,4);chain.append({'beginRva':hex(b),'endRva':hex(z),'unwindRva':hex(u),'headerBytes':h.hex()})
            if not(h[0]>>3)&4:return b,chain
            off=u+4+((h[2]+1)&~1)*2;b,z,u=struct.unpack('<III',pe.get_data(off,12))
    groups={}
    for e in ranges:
        r,c=chain_root(e);groups.setdefault(r,[]).append((e,c))
    # Nonvolatile receiver carriers: inspect every original code fragment.
    # One final runtime range includes switch data after a completed epilogue;
    # preserve it as unclassified bytes rather than disassemble it as code.
    carriers=[]
    configs=[(0x823880,'rdi',0x82389d),(0x824670,'rsi',0x82468e),(0x824760,'r13',0x82479b),
             (0x8916e0,'rdi',0x89172f),(0x894f80,'rbp',0x894fc1),(0x8955e0,'rbx',0x8955f4),
             (0x895830,'r14',0x895843)]
    for entry,reg,definition in configs:
        fragments=[];writes=[]
        aliases={'rdi':{'rdi','edi','di','dil'},'rsi':{'rsi','esi','si','sil'},'rbp':{'rbp','ebp','bp','bpl'},'rbx':{'rbx','ebx','bx','bl','bh'},'r13':{'r13','r13d','r13w','r13b'},'r14':{'r14','r14d','r14w','r14b'}}[reg]
        for (a,z,u),chain in groups[entry]:
            end=0x898138 if entry==0x895830 and a==0x89812b else z
            body=list(md.disasm(pe.get_data(a,end-a),base+a));assert sum(i.size for i in body)==end-a
            fragments.append({'start':hex(base+a),'endExclusive':hex(base+end),'unwindChain':chain,'instructions':[record(i) for i in body],
                              'unclassifiedTrailingBytes':pe.get_data(end,z-end).hex()})
            for i in body:
                if any(i.reg_name(r) in aliases for r in i.regs_access()[1]):
                    assert i.address==base+definition or (i.mnemonic=='pop' and i.op_str==reg),(hex(i.address),i.mnemonic,i.op_str)
                    writes.append(record(i))
        exact(definition,'mov',reg+', rcx')
        carriers.append({'entry':hex(base+entry),'register':reg,'definition':hex(base+definition),'writes':writes,'fragments':fragments,
            'scope':'For ordinary calls entering this method, receiver remains in its nonvolatile carrier until epilogue restoration. No blanket identity inferred from an offset or numerical stride.'})
    system_routes=[
      (0x817274,'call','0x1808269f0'),(0x81727f,'mov','rcx, qword ptr [rsp + 0x30]'),(0x81728c,'call','0x180823880'),
      (0x817509,'call','0x1808269f0'),(0x817514,'mov','rcx, qword ptr [rsp + 0x38]'),(0x817528,'call','0x180824670'),
      (0x8176d9,'call','0x1808269f0'),(0x8176e4,'mov','rcx, qword ptr [rsp + 0x38]'),(0x8176f8,'call','0x180824760'),
      (0x8244dd,'mov','rcx, rdi'),(0x8244e9,'call','0x1808218e0'),
      (0x8246c8,'call','0x18086bc10'),(0x824916,'mov','rcx, r13'),(0x824919,'call','0x18086bd80'),
      (0x835a09,'call','0x180823310'),(0x835a16,'mov','rbx, qword ptr [rsp + 0x40]'),
      (0x835a4e,'mov','rbx, qword ptr [rsp + 0x40]')]
    system_rows=[exact(*x) for x in system_routes]
    system_spans=[span(a,z) for a,z in ((0x8269f0,0x826a76),(0x823310,0x82334a),(0x817240,0x817293),
      (0x8174cd,0x81752f),(0x81769d,0x8176ff),(0x8359b0,0x835abf),(0x8246a9,0x8246cd),
      (0x8218e0,0x82191f),(0x821a02,0x821a36),(0x86bc10,0x86bc69),(0x86bd80,0x86be10))]
    # Both lookup mechanisms address exactly the same registry, not merely
    # similar fields: CF0350->160[0..7]. The pointer validator compares input
    # against all8 members; handle lookup checks selector<8 before indexing.
    for a in (0x826a03,0x823328):
        i=next(md.disasm(pe.get_data(a,16),base+a));assert i.address+i.size+i.operands[1].mem.disp==base+0xcf0350
    registry={'globalRva':'0xcf0350','arrayOffset':'0x160','entryCount':8,
      'validator':span(0x8269f0,0x826a76),'indexLookup':span(0x823310,0x82334a),
      'proof':'8269f0 outputs the exact incoming pointer only after equality with a registry member.823310 returns a member of the same array after unsigned<8. Init and play routes receive those successful outputs.'}
    music_routes=[(0x8916d0,'jmp','0x1808916e0'),(0x8942bc,'mov','rcx, rdi'),(0x8942bf,'call','0x1808ec6d0'),
       (0x890cf9,'call','0x1808ec160'),(0x894f70,'jmp','0x180894f80'),(0x895017,'mov','rcx, rbp'),
       (0x89501a,'call','0x1808955e0'),(0x895773,'mov','rcx, rbx'),(0x89577d,'call','0x180895830'),
       (0x897d19,'mov','rcx, r14'),(0x897d1c,'call','0x1808ecbe0'),(0x8ecc01,'mov','rsi, rcx')]
    music_rows=[exact(*x) for x in music_routes]
    descriptor=span(0x8915e0,0x8916cc)
    assert pe.get_string_at_rva(0xa628f0)==b'FMOD IT Codec'
    # Descriptor's init/close/read fields and instance size are emitted by
    # exact RIP-relative stores paired with their preceding address loads.
    descriptor_fields=[]
    for load,store,want in ((0x8915f8,0x8915ff,0x8916d0),(0x891606,0x89160d,0x890cf0),(0x891614,0x89161b,0x894f70)):
        li=next(md.disasm(pe.get_data(load,16),base+load));si=next(md.disasm(pe.get_data(store,16),base+store))
        assert li.address+li.size+li.operands[1].mem.disp==base+want
        assert si.mnemonic=='mov' and si.op_str.endswith(', rax')
        descriptor_fields.append({'load':record(li),'store':record(si),'callback':hex(base+want),'fieldAddress':hex(si.address+si.size+si.operands[0].mem.disp)})
    exact(0x8916c1,'mov','dword ptr [rip + 0x4abfd9], 0x46d8')
    music_spans=[span(a,z) for a,z in ((0x890cf0,0x890d0d),(0x8942a9,0x8942c4),(0x895009,0x89501f),
       (0x895769,0x895782),(0x897d0d,0x897d21),(0x8ec160,0x8ec238),(0x8ecbe0,0x8ecc85),(0x8ec6d0,0x8ec81c))]
    for family in prior['distinctAllocationFamilies']:
        assert pe.get_string_at_rva(int(family['sourceStringRva'],16)).decode()==family['sourceString']
        for row in family['instructions']:
            b=bytes.fromhex(row['bytes']);assert pe.get_data(int(row['address'],16)-base,len(b))==b
    # Retain every raw direct-call candidate into the six containing methods.
    # This is not an assertion that opaque indirect callers do not exist.
    call_targets={0x8218e0,0x8359b0,0x86bc10,0x86bd80,0x8ec160,0x8ecbe0}
    direct=[]
    for section in pe.sections:
        if not section.Characteristics&0x20000000:continue
        data=section.get_data()
        for n,b in enumerate(data[:-4]):
            if b not in (0xe8,0xe9):continue
            rva=section.VirtualAddress+n;t=rva+5+struct.unpack_from('<i',data,n+1)[0]
            if t in call_targets:direct.append({'rva':hex(rva),'targetRva':hex(t),'bytes':data[n:n+5].hex(),'status':'raw-candidate-not-automatically-boundary-proved'})
    output=[]
    for a,c in sorted(candidates.items()):
        b=bytes.fromhex(c['bytes']);assert pe.get_data(a,len(b))==b
        output.append({'instruction':c,'classification':'separate-FMOD-record-array-on-reviewed-routes','family':'system' if a<0x8e0000 else 'music',
          'ownerField':'0x1e8' if a<0x8e0000 else '0x5e0','recordStride':0x230,
          'receiverProof':'registryValidatedSystemRoutes' if a<0x8e0000 else 'ITCodecLifecycleAndWorkerRoute',
          'excludeFromMissionResolverOnReviewedRoutes':True,'universalIndirectCallerClosure':False,'hookApproved':False})
    deps={SOURCE,baseline,bundle,Path(__file__),ROOT/'tools/battle-scale/patch_native_copy.py'}
    return {'schemaVersion':1,'status':'bounded-audio-receiver-associations-not-hooks','nativeSourceSha256':EXPECTED,
      'sources':[{'path':str(p),'sha256':digest(p)} for p in sorted(deps)],'candidates':output,'classificationCounts':dict(Counter(x['classification'] for x in output)),
      'registryValidatedSystemRoutes':{'registry':registry,'routes':system_rows,'contextSpans':system_spans,
          'allocationLink':'Same validated registry receiver enters823880 and owns its independently allocated+1e8 array. Cleanup8218e0 is called from823880 with unchangedRDI; play functions reach86bc10/86bd80 through unchanged validated receiver carriers.8359b0 retrieves the same registry by handle.'},
      'ITCodecLifecycleAndWorkerRoute':{'name':'FMOD IT Codec','nameRva':'0xa628f0','descriptor':descriptor,'callbackFields':descriptor_fields,
          'instanceBytes':0x46d8,'routes':music_rows,'contextSpans':music_spans,
          'proof':'Registered init callback8916d0→8916e0 passes unchanged receiver to8ec6d0, which allocates+5e0. Close callback890cf0 passes same instance to8ec160, which releases+5e0 through81e9b0 with fmod_music.cpp provenance. Read callback894f70→894f80→8955e0→895830→8ecbe0 passes the same receiver in verified nonvolatile carriers to8ecc4d.'},
      'carrierProofs':carriers,'distinctAllocationFamilies':prior['distinctAllocationFamilies'],
      'retainedReaEvidence':evidence['records'],'rawDirectCallCandidates':direct,'instructions':[r for _,r in sorted(rows.items())],
      'assumptions':['Ordinary registered FMOD API/codec dispatch and valid object lifetime. Caller-supplied fabricated object addresses or arbitrary memory corruption are not certified.'],
      'unknowns':['Opaque indirect callers and every other codec subtype route have not been exhaustively proved. Raw direct-call candidates are retained without treating numeric byte matches as established entrypoints.','Trailing switch data in895830 is retained unclassified; this audit does not reduce canonical decode-gap counts.'],
      'limitations':['These are separate audio allocations on the exact reviewed routes, not Mission companions despite matching230 stride. No global arbitrary-owner assertion, live-growth patch, hook or runtime readiness claim.']}

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--output',default=str(ROOT/'evidence/engine-extension/native-segmented-audio-exclusions.json'));args=parser.parse_args()
    result=build();out=safe_review_output(args.output,[x['path'] for x in result['sources']]);out.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({'path':str(out),'sha256':digest(out),'counts':result['classificationCounts']}))

if __name__=='__main__':main()

