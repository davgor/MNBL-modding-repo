"""Exact outstanding call/write obligations for the late attack Mission home."""
from pathlib import Path
import collections,hashlib,json,sys
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import capstone,pefile
from patch_native_copy import safe_review_output
from native_instruction_semantics import memory_operand_writes

# Complete original helper bodies, including cold blocks after the first RET.
LEAVES={0x061ed0:0x061ed9,0x174810:0x1748ac,0x3211d0:0x3211ef,
0x1b7030:0x1b7094,0x3634f0:0x363571,0x2692d0:0x26931a,
0x5e5780:0x5e57a0,0x5e5700:0x5e5777,0x5e56e0:0x5e5700,
0x1b70a0:0x1b7120,0x75caf0:0x75cc07,0x1eefd0:0x1eeffc,
0x5e5640:0x5e56d7,0x40bc60:0x40bcd6,0x5e55f0:0x5e5637,
0x5c8e70:0x5c8ec6,0x04acd0:0x04acf8,0x3211f0:0x3212ee,
0x73ddb0:0x73defb,0x3be900:0x3be9a0,
0x5e5ab0:0x5e5ae4,0x689490:0x689510,0x5e9a10:0x5e9ada,
0x5e57a0:0x5e57c8,0x613350:0x6133a6,0x361500:0x3615b6,
0x6132a0:0x613346,0x6130a0:0x61329d,0x5e55d0:0x5e55e3}

def build():
    folder=ROOT/'evidence/engine-extension'
    paths=[folder/n for n in ('native-pool-attack-spill-review.json','native-pool-reference-inventory.json','native-closure-ghidra-evidence.json','native-expansion-ghidra-evidence.json','managed-core-callback-bodies.json')]
    early,inv,bundle,expansion,managed=[json.loads(p.read_text()) for p in paths]
    binary=Path(inv['binary']['path']);data=binary.read_bytes();sha=hashlib.sha256(data).hexdigest()
    if sha!=early['targetSha256']:raise ValueError('Wrong target')
    pe=pefile.PE(data=data);base=pe.OPTIONAL_HEADER.ImageBase
    md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
    def span(a,z):
        body=list(md.disasm(pe.get_data(a,z-a),base+a))
        if sum(i.size for i in body)!=z-a:raise ValueError('Decode gap')
        return body
    def raw(i):return dict(address=hex(i.address),bytes=i.bytes.hex(),decoded=i.mnemonic+' '+i.op_str)
    body=span(0x5c8ed0,0x5cbb26);by={i.address:i for i in body};pred=collections.defaultdict(set)
    for i in body:
        nexts=[]
        if i.group(capstone.CS_GRP_JUMP):
            if i.operands[0].type!=capstone.x86.X86_OP_IMM:raise ValueError('Indirect jump')
            nexts.append(i.operands[0].imm)
            if i.mnemonic!='jmp':nexts.append(i.address+i.size)
        elif not i.group(capstone.CS_GRP_RET):nexts.append(i.address+i.size)
        for a in nexts:
            if a not in by:raise ValueError('Unknown branch boundary')
            pred[a].add(i.address)
    # Every predecessor capable of reaching either late reload must preserve home.
    targets=[base+0x5caef7,base+0x5cb53f];relevant=set(targets);queue=list(targets)
    while queue:
        a=queue.pop()
        for prev in pred[a]:
            if prev not in relevant:relevant.add(prev);queue.append(prev)
    calls=[];writes=[];indexed_stack=[];derived_writes=[];counter_writes=[]
    for i in body:
        if i.address not in relevant:continue
        if i.mnemonic=='call':calls.append(i)
        for operand_index,op in enumerate(i.operands):
            if op.type!=capstone.x86.X86_OP_MEM:continue
            if i.reg_name(op.mem.base) not in ('rsp','rbp'):
                if memory_operand_writes(i,operand_index):derived_writes.append(raw(i))
                continue
            off=op.mem.disp+(0x100 if i.reg_name(op.mem.base)=='rbp' else 0)
            if op.mem.index:
                indexed_stack.append(raw(i));continue
            if memory_operand_writes(i,operand_index) and off<0x1b0 and off+op.size>0x1a8:
                writes.append(raw(i))
            if memory_operand_writes(i,operand_index) and off<0xb4 and off+op.size>0xb0:counter_writes.append(raw(i))
            if i.mnemonic=='lea' and 0x1a8<=off<0x1b0:raise ValueError('Home address escape')
    if [x['address'] for x in writes]!=['0x1805c8f73']:raise ValueError('Additional direct home write')
    if [x['address'] for x in counter_writes]!=['0x1805c9514','0x1805c9a87']:raise ValueError('Candidate count writers differ')
    if [x['address'] for x in indexed_stack]!=['0x1805ca6d7','0x1805ca6e3','0x1805ca6eb']:raise ValueError('Unreviewed indexed stack access')
    helpers=[]
    for a,z in LEAVES.items():
        code=span(a,z)
        for i in code:
            if i.group(capstone.CS_GRP_JUMP) and (i.operands[0].type!=capstone.x86.X86_OP_IMM or not base+a<=i.operands[0].imm<base+z):
                if a!=0x04acd0 or i.op_str!='0x18074e270':raise ValueError('Unexpected helper tail '+hex(a))
        helpers.append(dict(start=hex(base+a),endExclusive=hex(base+z),instructions=[raw(i) for i in code]))
    callees={}
    for i in calls:
        if i.operands[0].type!=capstone.x86.X86_OP_IMM:raise ValueError('Unexpected indirect call')
        name=hex(i.operands[0].imm);callees.setdefault(name,[]).append(hex(i.address))
    records=[]
    wanted={0x75cc10,0x73fca0,0x73f2d0,0x5d7690,0x5d76b0,0x5da1b0,0x5da0a0,0x60c2d0,0x5c8d30,0x5d3bd0,0x75bfa0,0x5da900,0x5eb0d0,0x5eb3f0,0x5eadd0,0x5ea0d0,0x6956c0,0x73e060,
        0x5dd500,0x617ac0,0x73e3f0,0x5db9d0,0x5dd420,0x5dc900,0x3bdac0,0x4214c0,0x73fe40,0x73fda0,0x434490,0x6957d0,0x695b50,0x695e90,0x696170,0x05c720,0x73df00,
        0x745160,0x73e190,0x40bc10,0x434570,0x73e090,0x4342f0,0x434600,0x354fe0,0x5cbdd0,0x646770,0x57f8e0}
    missing=[]
    for a in wanted:
        candidates=[r for r in expansion['records']+bundle['records'] if r.get('operation')=='procedure_pseudo_code' and r.get('parameters',{}).get('procedure')==hex(base+a)]
        if candidates:
            r=candidates[-1];records.append(dict(procedure=hex(base+a),evidenceId=r['evidence_id'],normalizedResult=r['normalized_result']))
        else:missing.append(hex(base+a))
    effects=[]
    def effect(sites,outputs,reason):
        effects.append(dict(calls=[hex(base+a) for a in sites],outputs=outputs,reason=reason))
    for e in early['callEffects']:
        subset=[a for a in e['calls'] if int(a,16) in relevant]
        if subset:effects.append(dict(calls=subset,outputs=e['outputs'],reason=e['reason'],source='independently-reviewed early attack artifact'))
    effect([0x5c8ee9],[], 'Exact retained __chkstk body touches guard pages and preservesGPR/RSP; it precedes Mission slot initialization.')
    effect([0x5ca167,0x5ca866,0x5cabc8,0x5c9989,0x5c99e4,0x5c99a6,0x5c99b9,0x5ca797,0x5c99d0,0x5ca7e8,0x5c99fe,0x5ca9d0,0x5cabd7,0x5cb052,0x5ca80f,0x5ca8ef,0x5cb481,0x5cb5bf],[],
        'Argument registers derive from external Agent/item/Mission objects or scalar math values; no pointer into this frame. Complete predecessor instructions retain exact argument setup.3211d0 additionally is a readonly leaf.')
    effect([0x5cac04],[[0x12f0,32]],'1f1e80 copies two16-byte blocks toRDX; known early helper, external lock receiver.')
    effect([0x5cac17],[[0x590,32]],'1f1ed0 copies two16-byte blocks toRDX; localRCX input12f0 is readonly.')
    effect([0x5cac3c],[[0x340,16]],'3568d0 four-float RDX output; external transform andlocal5a0 input readonly.')
    effect([0x5c9698],[[0x1380,0x48]],'1bc380 fixed initialized localheader; no callee.')
    effect([0x5ca9e7],[[0x12f0,64]],'5ea810 eightQWORD output only; nested helpers receive its ownlocals, as early review proves.')
    effect([0x5c96b2],[[0x90,4],[0x8c,4]],'75cc10 onlydereferences param2/3 asfloatoutputs; polygonheader/backing are readonly.')
    effect([0x5c96e3],[[0x15e0,48]],'061ed0 oneQWORD output; caller6-iteration loop incrementsRBX by8, beginningRBP14e0.')
    effect([0x5c979d],[[0x420,16]],'174810 readonlyRCX input andexact16byteRDXoutput.')
    effect([0x5c9c2a],[[0x1b0,16]],'174810 exact16byteRDXoutput starts immediately after savedhome end1b0; no negative writes.')
    effect([0x5c9cc1],[[0x600,16]],'174810 exact16byteRDXoutput.')
    effect([0x5c9869],[[0x15e0,48]],'Sort count is5 or6.73fca0 computesdepth4;73fe40 seesrange40/48<e1 andnonzerodepth, returnswithoutpartition.73fda0 insertion checksfirstboundary beforedecrement; writesonly5/6QWORD array.73e060 comparator reads only.')
    effect([0x5c987f],[[0x1380,0x40]],'73f2d0 writespolygonheader0..3f andheap backing.3bdac0/4214c0 changeheader0/8/10/18 andallocatedbacking; initializedheaderbackingnull from1bc380, never currentframe. RDXpoints tosource-array pointerlocalf8, read only.')
    effect([0x5c9908],[[0x550,0x30]],'5d7690 writes only output0,20,28.')
    effect([0x5c992f],[[0x550,0x34]],'5d76b0 writes fifthargument cursor0..33. Its inputsb8/f0 are readonly; nested5db9d0/5da0a0/5dd420 onlywrite cursor0,8,c;5dc900 readonly.')
    effect([0x5c9a16,0x5c9a4d],[], '5da1b0 copies inputcapsules toits ownlocals andpasses polygonheader1380 to73e3f0 ->73e190 ->73e090, allreadonly for callerheader. Otheroutputs arecallee-owned.')
    effect([0x5c9a79],[[0x1610,0xd7a1-0x1610]],'6956c0 receivescandidate baseRSP1610+index*c20,index0..14. Four695* branches initializebytecount0 anditeratepositive signedchar skeletoncount<=127, incrementatmostonce/iteration, so writeindex0..126 nevernegative. Maximumrelativewrite30+126*30=17d0, lastrecordendd7a1.1f1b00 receives onlycallee-local output; candidate stores copy its returnedfloats. This boundssaved-home separation, not a claim eachc20recordfits127contacts.')
    effect([0x5c9aa4],[[0x550,16]],'5da0a0/5dd420 writecursor0/8/c;5dc900 readonly; cursor fieldsusedforhashlookup are notpointerarithmetic onthecursor itself.')
    effect([0x5c9ad0,0x5cab90,0x5cabab],[], '04acd0 readsheader[0] then tailfreesheapbacking; no callerheader writes. Headers are initialized heapvectors; neverpoint atsavedMissionhome.')
    effect([0x5c9e5c],[[0x12f0,36]],'60c2d0 copies4QWORDs plusDWORD toRDX;66b620 receives externalitem pointers/scalars only.')
    effect([0x5c9e86],[[0x610,16]],'1b7030 fourfloatRCX output; inputs external, no calls.')
    effect([0x5c9ea0],[[0x15e0,48]],'3211f0 writes resultcolumns+10/+20 andforwards sameRCXoutput to08e630, whosefixedfloatwrites0..28 remain inside48bytes. Returnedpointer usedforcopies.')
    effect([0x5c9ed0],[[0x540,16]],'3634f0 fourfloatRDX output, no calls.')
    effect([0x5c9ed9],[[0x150,44]],'08e630 writesfloatoffsets0..28 only. Highestend17c is belowMission1a8.')
    effect([0x5c9eec],[], '2692d0 complete readonly comparison leaf, including secondRET path.')
    effect([0x5ca473,0x5ca4c1,0x5ca547],[[0x1c8,16],[0xa0,16],[0x120,8],[0xb8,8]],'5c8d30 writesonlyparam3/4 twoQWORDseach,param5/6 oneQWORDeach. Inputcapsulecopiedreadonly; virtual190 receives entirelycallee-local input/output, never calleroutput addresses.')
    effect([0x5ca574],[[0x15e0,48]],'5e5780 direct fixedheader stores0..2f.')
    effect([0x5ca587,0x5ca5bc],[[0x15e0,48]],'5d3bd0 output0..23 and+28QWORD.434490 outputparam4first16,param5oneQWORD; positionreadonly via4342f0/434600/434570. Heightvirtualcalls receive scalarcoordinates, notcallerposition.')
    effect([0x5ca72d],[[0x1240,0xad]],'5e5700 receivesRSP620, touchesonly+c20..ccc includingmemset+c28length80. Runtime memset thunk receives exact boundedrange.')
    effect([0x5ca767],[[0x15e0,40]],'5e56e0 writesfixedoutput0..27.')
    effect([0x5ca92d],[[0xc0,8]],'1b70a0 writesonlytwofloats atRCX0/4.')
    effect([0x5ca967],[[0x14f0,0x48]],'73ddb0 constructsheader0..47 andheapbacking.05c720 onlyheader0..1b andfreesoldbacking; rawbodyshowsno outputpointergiven toheapcopy/free.')
    effect([0x5ca9a7],[[0x1578,0x1c]],'3be900 initializesheader0..1b andcopiesbacking into newlyallocatedheap.')
    effect([0x5ca9c4],[], '75caf0 readsheader14f0 andwrites secondheader+88 heapbacking. Complete coldblocks retained; no headerwrites.')
    effect([0x5caa10,0x5caa57,0x5caa91],[[0x440,16]],'75bfa0 fourfloat outputR9 only. Inputsreadonly;745160 outputs arecallee locals;73df00/40bc10 input-only.')
    effect([0x5caab8],[[0x150,60]],'1eefd0 initializesfixedoutput0..3b, ends18c beforeMission1a8.')
    effect([0x5caaf9],[[0x5e0,16]],'5e5640 fourfloatRDX output; otherarguments readonly.')
    effect([0x5cac56],[[0x14d0,16]],'40bc60 fourfloatRDX output; returnedpointer is passedreadonly asR8 to5e55f0.')
    effect([0x5cac68],[[0x450,16]],'5e55f0 fourfloatRDX output; R8 priorresultreadonly.')
    effect([0x5cad3e,0x5cad8b],[[0x410,16],[0x1e0,16],[0x90,4]],'354fe0 firsttwo argumentsreadonly;third/fourth outputvectors16byteseach,fifthonefloat.745160 receivesonlycallee-local outputs.')
    effect([0x5cae63,0x5cae79,0x5cb4ab,0x5cb4c1,0x5cb92c,0x5cb93f],[], '5c8e70 complete readonly scalar leaf includingbothconditionalpaths.')
    effect([0x5caefe,0x5cb546],[[0x12d8,4],[0x12d4,4],[0x12e9,1],[0x12ea,1]],'5cbdd0 initializes2float refs and2byte refs. Param5byte onlynativewritten; otherthree forwardedtoCorecallback208,index60Mission_GetDefendCollisionResults. ExactdelegateInvoke Single&,Single&,Boolean& U1 boundsmanagedwrites4/4/1. Nativehandle retain/release sees integers; no localoutput pointers elsewhere.')
    effect([0x5cb09b],[[0x15e0,33]],'5eadd0 callerparam2candidate readonly;param3polygon/param4matrix inputsreadonly through75bfa0. Param5writes2QWORDs,4floats,+20byte only.')
    effect([0x5cb147,0x5cb1f9],[[0x15e0,40]],'5eb3f0 onlycalleroutputparam4[0..4],40bytes.617ac0 outputfloats0..9, param2/3 inputsreadonly through745160. Param5/7 directionvectorsreadonly.')
    effect([0x5cb28e,0x5cb40b],[[0x15e0,40]],'5da900 copiescapsule inputtocall-local, candidateinputreadonly.5dd500 calleroutputonlyfloats0..9;745160 firstfour inputsreadonly, lasttwooutputs arecallee-local.')
    effect([0x5cb324],[[0x15e0,40],[0x8c,4],[0x81,1]],'5eb0d0 outputparam4 via5dd500 fixed40bytes;param9onefloat,param10onebyte. Capsule,candidate,directioninputsreadonly; no caller-local pointers forwardedelsewhere.')
    effect([0x5cb614,0x5cb643],[], '5ea0d0 callerposition/direction readonly;positionforwardedonlyreadonly param3 of75bfa0;alloutputbufferscalleelocal.')
    effect([0x5cb985],[[0x620,0xc20]],'Runtime memcpy receivesexplicitR8=c20 anddestinationRBP520. Sourcecandidatearray distinctfrom savedMission.')
    effect([0x5cba3b],[], '5d9870 receivesexternal outputrecord andreadonlylocalRSP620 source; same exactheader-only/heapappend proof insecondaryjobartifact. Currentframehome is neither destinationheader norbacking.')
    covered=[int(a,16) for e in effects for a in e['calls']]
    if len(set(covered))!=len(covered) or set(covered)!={i.address for i in calls}:raise ValueError('Call effects missing/duplicated '+str(set(covered)^{i.address for i in calls}))
    if any(a<0x1b0 and a+n>0x1a8 for e in effects for a,n in e['outputs']):raise ValueError('Callee output overlaps Missionhome')
    if any(a<0xb4 and a+n>0xb0 for e in effects for a,n in e['outputs']):raise ValueError('Callee output overlaps candidate count')
    delegate=next(m for m in managed['methods'] if m['name']=='Invoke' and m['type'].endswith('/Mission_GetDefendCollisionResults_delegate'))
    if [p['type'] for p in delegate['parameters'][-3:]]!=['System.Single&','System.Single&','System.Boolean&'] or delegate['parameters'][-1]['nativeMarshalType']!='U1':raise ValueError('Callback output ABI differs')
    result=dict(schemaVersion=1,targetSha256=sha,status='exact-late-home-lifetime-proved-under-installed-callee-contracts',gameExecuted=False,
        sources=[dict(path=str(p),sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in paths],
        savedSlotBodyRsp=[0x1a8,8],onlyDirectSlotWrite=writes[0],lateReloads=[raw(by[a]) for a in targets],
        computedWriteProof=dict(indexedStackInstructions=indexed_stack,candidateCounterWrites=counter_writes,derivedMemoryWrites=derived_writes,
            candidateArray='CountRSPb0 starts0 at9514 and increments at9a87 togetherwithR12. R12 starts0 at9944 andexits when15 at9a94/98; counttherefore0..15. No other directwriter or boundedcalleeoutput overlapsb0. ca6d0 computescount*c20; threeindexedstores start1610+count*c20 and2220/2228+count*c20, so allareabove saved1a8.',
            candidateCalls='RDI startsRSP2220 at9947; eachacceptedcandidate increasesit c20; RCX=RDI-c10 =>RSP1610+index*c20,index0..14. DirectRDI-8/0/+8 stores remain abovehome.',
            otherDerivedStores='91fe setsRCX=RSP200; fixedSSEcopy advancesRCX80 andwrites0..eb, ending2ec. Allremainingnonstack writes targetR14, which is entryparam2 externalattackobject andis ABI-preserved throughout. No store through a computedcurrentframeaddress remains unclassified.'),
        allPredecessorInstructions=[raw(i) for i in body if i.address in relevant],
        callSitesByCallee=callees,completeSmallHelperBodies=helpers,retainedCalleeEvidence=records,
        callEffects=effects,callbackDelegate=delegate,
        callbackBindingProof='Retained646770 switch case0x3c stores callback pointer atparam1+1f0.57f8e0 passes Managedinterface+18 asparam1, so combinedoffset208 identifies index60 Mission_GetDefendCollisionResults. Exactdelegate finalSingle&,Single&,Boolean& U1 bounds4/4/1bytes.',
        groupedExternalArgumentsProof='Complete rawbodies5e5ab0,689490,5e9a10,5e57a0,613350,361500,6132a0,6130a0,5e55d0 consume no incoming higherstackarguments.689490 allocates itsown58-byteframe andpasses itsownRSP20 output to66cf40;361500 receivesonlyexternalRCX andforwards externalreceiver/scalarvalues;6132a0/6130a0 invoke externalitem virtualreceivers withscalar1.613350 onlyforwards externalRCX to6132a0. Otherfive are complete leafs withoutstackargumentreads.',
        missingRequestedCalleeEvidence=missing,instructionSiteMappings=[],exactReloadSeeds={hex(a):dict(register='rcx',pointerKind='mission',biasBytes=0,instruction=raw(by[a])) for a in targets},
        limitations=['Exact two reloads only. Installed native and managed ABI contracts are required; this is not a capacity guarantee for candidate contact records or a whole-patch/runtime approval.'])
    out=folder/'native-pool-attack-late-spill-review.json'
    safe_review_output(out,[binary]+paths).write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(dict(relevantInstructions=len(relevant),calls=len(calls),callees=len(callees),smallHelpers=len(helpers),missingEvidence=len(missing))))

if __name__=='__main__':build()
