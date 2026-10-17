"""Original-byte-bound scalar Agent operation routes; no patch or game use.

Memory-only offset consumers are not automatically lifetime-safe. This audit
selects adjacent scalar copies/stores and preserves pointer-valued, mixed-use,
late-owner and control-flow uncertainty as separate exclusions.
"""
from pathlib import Path
from collections import Counter
from copy import deepcopy
import argparse, hashlib, json, re, struct, sys
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import capstone, pefile
from capstone.x86 import X86_OP_REG,X86_OP_MEM,X86_OP_IMM
from patch_native_copy import safe_review_output
SOURCE=ROOT/'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
CONSUMERS=ROOT/'evidence/engine-extension/native-segmented-agent-consumers.json'
OWNERS=ROOT/'evidence/engine-extension/native-live-agent-access-review.json'
SHA='2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
BASE=0x180000000
# Exact reviewed subset. Others stay classified rather than silently promoted.
SELECTED={0x5c4032,0x5c55c0,0x5d3dac,0x5d3dea,0x5e5e96,0x648262,0x6a59da}
FLAGS=('CF','OF','ZF','SF','AF','PF')
def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def row(i):return {'address':hex(i.address),'bytes':i.bytes.hex(),'mnemonic':i.mnemonic,'operands':i.op_str}
def canonical(name):
    fixed={'eax':'rax','ax':'rax','al':'rax','ah':'rax','ebx':'rbx','bx':'rbx','bl':'rbx','bh':'rbx','ecx':'rcx','cx':'rcx','cl':'rcx','ch':'rcx','edx':'rdx','dx':'rdx','dl':'rdx','dh':'rdx','esi':'rsi','si':'rsi','sil':'rsi','edi':'rdi','di':'rdi','dil':'rdi','ebp':'rbp','bp':'rbp','esp':'rsp','sp':'rsp'}
    if name in fixed:return fixed[name]
    if name.startswith('r') and name[1:-1].isdigit() and name[-1] in 'dwb':return name[:-1]
    return name

def classification(candidate,owner,instructions):
    contexts=candidate['contexts'];events=[e for c in contexts for e in c.get('events',[])]
    if not events:return 'missing-consumer-evidence'
    if any(c.get('status')!='bounded-may-use-inventory' for c in contexts):return 'incomplete-context-decode'
    if any(e['kind'] not in ('memory-read-address','memory-write-address') for e in events):return 'mixed-arithmetic-escape-or-unresolved-result-use'
    unique={(e['address'],e['kind']) for e in events}
    if len(unique)!=1:return 'multiple-memory-operations-require-combined-lifetime-contract'
    if not owner.get('ownerFlowProof'):return 'owner-unproved-in-accepted-input'
    address,kind=next(iter(unique));i=instructions[int(address,16)]
    mem=[o for o in i.operands if o.type==X86_OP_MEM]
    if i.mnemonic!='mov' or len(mem)!=1:return 'operation-not-reviewed-scalar-mov'
    if mem[0].size==8:return 'qword-load-may-return-pointer-referent-lifetime-unproved'
    if mem[0].size not in (1,2,4):return 'operation-width-not-reviewed'
    start=int(candidate['multiplyRva'],16)+BASE
    if i.address!=start+instructions[start].size:return 'owner-or-other-instruction-inside-window-needs-separate-contract'
    return 'adjacent-scalar-operation-route-candidate'

def flag_overwrite(md,raw,address):
    remaining=set(FLAGS);continuation=[]
    for i in md.disasm(raw,address):
        continuation.append(row(i))
        assert not any(i.group(g) for g in (capstone.CS_GRP_CALL,capstone.CS_GRP_JUMP,capstone.CS_GRP_RET)),'Flag flow needs CFG proof'
        for flag in list(remaining):
            tested=getattr(capstone.x86,'X86_EFLAGS_TEST_'+flag)|getattr(capstone.x86,'X86_EFLAGS_PRIOR_'+flag)
            assert not i.eflags&tested,(flag,row(i))
            changed=sum(getattr(capstone.x86,'X86_EFLAGS_'+kind+'_'+flag) for kind in ('MODIFY','RESET','SET','UNDEFINED'))
            if i.eflags&changed:remaining.remove(flag)
        if not remaining:return continuation
    raise AssertionError('Arithmetic flags not all overwritten within bounded continuation')

def regression_checks(md):
    """Synthetic negative tests exercise selection, overlap and flag gates."""
    ds=list(md.disasm(bytes.fromhex('4869c8600c0000428b8411ac320000'),BASE))
    ins={i.address:i for i in ds};event={'address':hex(ds[1].address),'kind':'memory-read-address'}
    c={'multiplyRva':'0x0','contexts':[{'status':'bounded-may-use-inventory','events':[event]}]};o={'ownerFlowProof':{'reviewed':True}}
    checks=[]
    def expect(name,cc,oo,ii,reason):
        assert classification(cc,oo,ii)==reason,name
        checks.append(name)
    expect('adjacent-dword-load',c,o,ins,'adjacent-scalar-operation-route-candidate')
    expect('missing-owner',c,{},ins,'owner-unproved-in-accepted-input')
    q=deepcopy(c);q['contexts'][0]['events']=[]
    expect('missing-events',q,o,ins,'missing-consumer-evidence')
    q=deepcopy(c);q['contexts'][0]['status']='incomplete'
    expect('incomplete-context',q,o,ins,'incomplete-context-decode')
    q=deepcopy(c);q['contexts'][0]['events'].append(dict(event,kind='stored-product-value'))
    expect('escaped-product',q,o,ins,'mixed-arithmetic-escape-or-unresolved-result-use')
    q=deepcopy(c);q['contexts'][0]['events'].append(dict(event,address=hex(ds[1].address+8)))
    expect('second-operation',q,o,ins,'multiple-memory-operations-require-combined-lifetime-contract')
    ptr=next(md.disasm(bytes.fromhex('4a8b8411ac320000'),ds[1].address));ii=dict(ins);ii[ptr.address]=ptr
    expect('pointer-width-load',c,o,ii,'qword-load-may-return-pointer-referent-lifetime-unproved')
    late=next(md.disasm(bytes(ds[1].bytes),ds[1].address+1));ii=dict(ins);ii[late.address]=late;q=deepcopy(c);q['contexts'][0]['events'][0]['address']=hex(late.address)
    expect('intervening-owner-or-other-instruction',q,o,ii,'owner-or-other-instruction-inside-window-needs-separate-contract')
    assert canonical('eax')=='rax' and canonical('r10d')=='r10';checks.append('partial-register-overlap-normalization')
    assert flag_overwrite(md,bytes.fromhex('85c0'),BASE)[-1]['mnemonic']=='test';checks.append('test-overwrites-arithmetic-flags')
    for name,raw in [('carry-consumer-before-overwrite','11c085c0'),('conditional-branch-before-overwrite','740085c0'),('call-before-overwrite','e80000000085c0'),('no-complete-overwrite','90')]:
        try:flag_overwrite(md,bytes.fromhex(raw),BASE)
        except AssertionError:checks.append(name)
        else:raise AssertionError(name)
    return checks

def build():
    assert digest(SOURCE)==SHA
    deps=[SOURCE,CONSUMERS,OWNERS,Path(__file__),ROOT/'tools/battle-scale/patch_native_copy.py',ROOT/'tools/battle-scale/native_offset_use_flow.py',ROOT/'evidence/engine-extension/native-offset-use-flow-independent-review.json']
    before={str(p):digest(p) for p in deps}
    consumer=json.loads(CONSUMERS.read_text());inventory=json.loads(OWNERS.read_text())
    assert consumer['binarySha256']==SHA
    for s in consumer['sources']:assert digest(s['path'])==s['sha256'],s['path']
    pe=pefile.PE(data=SOURCE.read_bytes());md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
    tests=regression_checks(md)
    instructions={}
    for r in consumer['instructions']:
        a=int(r['address'],16);raw=bytes.fromhex(r['bytes']);assert pe.get_data(a-BASE,len(raw))==raw
        ds=list(md.disasm(raw,a));assert len(ds)==1 and row(ds[0])==r
        instructions[a]=ds[0]
    owners={c['rva']:c for c in inventory['candidates']};decisions=[];routes=[];memory_only=[]
    for c in consumer['candidates']:
        owner=owners[c['multiplyRva']];reason=classification(c,owner,instructions)
        contexts=c['contexts'];events=[e for x in contexts for e in x.get('events',[])]
        if contexts and events and all(e['kind'] in ('memory-read-address','memory-write-address') for e in events):memory_only.append(c)
        record={'multiplyRva':c['multiplyRva'],'classification':reason,'contextCount':len(contexts),'events':events,'hookApproved':False}
        decisions.append(record)
        a=BASE+int(c['multiplyRva'],16)
        if reason!='adjacent-scalar-operation-route-candidate':continue
        assert a-BASE in SELECTED,'New candidate requires exact independent review'
        imul=instructions[a];operation=instructions[int(events[0]['address'],16)];proof=owner['ownerFlowProof']
        assert imul.mnemonic=='imul' and imul.operands[0].size==8 and imul.operands[1].type==X86_OP_REG and imul.operands[2].imm==0xc60
        assert proof['useInstruction']==row(operation) and proof['effectiveKind']=='agent'
        for r in proof['definitionInstructions']:
            raw=bytes.fromhex(r['bytes']);assert pe.get_data(int(r['address'],16)-BASE,len(raw))==raw
        mem=next(o for o in operation.operands if o.type==X86_OP_MEM)
        product=imul.reg_name(imul.operands[0].reg);index=imul.reg_name(imul.operands[1].reg)
        terms=[operation.reg_name(mem.mem.base),operation.reg_name(mem.mem.index)]
        assert mem.mem.scale==1 and product in terms and all(terms)
        mission=next(r for r in terms if r!=product)
        assert mem.mem.disp-0x2d50==proof['effectiveBias']
        assert product!=index and product!=mission and index!=mission
        writing=operation.operands[0].type==X86_OP_MEM
        endpoint=operation.operands[1] if writing else operation.operands[0]
        assert endpoint.type in (X86_OP_REG,X86_OP_IMM)
        dest=operation.reg_name(endpoint.reg) if endpoint.type==X86_OP_REG else None
        end=operation.address+operation.size;raw=pe.get_data(a-BASE,end-a)
        assert raw==imul.bytes+operation.bytes and len(raw)>=5
        # Every incoming IMUL arithmetic flag is overwritten before any flag
        # consumer on the straight-line continuation. Undefined flag outputs
        # are also dead values, not requirements to synthesize a particular bit.
        continuation=flag_overwrite(md,pe.get_data(end-BASE,96),end)
        routes.append({'multiplyRva':hex(a-BASE),'windowStart':hex(a),'windowEndExclusive':hex(end),'windowLength':len(raw),'fileOffset':pe.get_offset_from_rva(a-BASE),'originalBytes':raw.hex(),
            'instructions':[row(imul),row(operation)],'ownerProof':proof,'consumerContexts':contexts,
            'entryInputs':{'missionRegister':mission,'logicalIndexRegister':index,'indexWidthBits':64,'indexValueContract':'Capture unchanged original full-width index before helper argument marshalling; do not assume an arbitrary entry value has been sign-extended correctly. Existing native valid-index guards remain prerequisites.'},
            'operation':{'kind':'write' if writing else 'read','fieldOffset':hex(proof['effectiveBias']),'widthBytes':mem.size,'value':endpoint.imm if endpoint.type==X86_OP_IMM else None,'register':dest},
            'registerEffects':{'productRegister':product,'productValue':'low64(original index *0xc60), computed by original IMUL','loadDestination':None if writing else dest,'loadZeroExtends32':not writing and endpoint.size==4,'indexOverwrittenByLoad':not writing and canonical(dest)==index,'missionOverwrittenByLoad':not writing and canonical(dest)==mission,'requiredOrdering':'Snapshot Mission/index; execute original IMUL or restore its exact result; perform field operation entirely under helper lease; return scalar only; apply scalar destination after register restoration so overlap does not restore stale index.'},
            'flags':{'scope':'normal execution; arithmetic flags dead before first flag consumer','overwriteInstruction':continuation[-1],'continuationThroughOverwrite':continuation,'genericFlagsPreserved':False},
            'lifetimeContract':'Acquire registered Mission storage lease, validate occupied primary domain/index, perform this exact scalar copy/store while held, and release before returning. Return no Agent or pointee address. Shared leases protect lifetime, not same-byte concurrent writer serialization.',
            'invalidLookupContract':'Unresolved: original instruction assumes a valid native index/object. No silent-zero or ignored-write policy is approved; helper failure must follow an independently defined engine-path contract.',
            'overwriteContract':'Two complete original instructions form this window. A standard-call stub must preserve every unmodified GPR/SIMD register, original stack geometry and required output; exact unwind metadata and current native prologue alignment still require per-site integration proof.',
            'hookApproved':False})
    assert {int(r['multiplyRva'],16) for r in routes}==SELECTED
    # Exhaustive raw relative-control-transfer encoding scan across executable
    # sections. Mid-instruction false matches stay explicit candidates; absence
    # does not close computed jumps, callbacks or alternate exception entries.
    windows=[(int(r['windowStart'],16),int(r['windowEndExclusive'],16),r) for r in routes]
    direct={a:[] for a,_,_ in windows}
    pattern=re.compile(b'[\xe8\xe9\xeb\x70-\x7f]|\x0f[\x80-\x8f]')
    scanned=0
    for sec in pe.sections:
        if not sec.Characteristics&0x20000000:continue
        raw=sec.get_data();origin=BASE+sec.VirtualAddress;scanned+=len(raw)
        for match in pattern.finditer(raw):
            off=match.start();op=raw[off];n=6 if op==15 else 5 if op in (0xe8,0xe9) else 2
            if off+n>len(raw):continue
            rel=struct.unpack_from('<i' if n>2 else '<b',raw,off+n-(4 if n>2 else 1))[0]
            target=origin+off+n+rel
            for lo,hi,r in windows:
                if lo<=target<hi:
                    direct[lo].append({'source':hex(origin+off),'target':hex(target),'bytes':raw[off:off+n].hex(),'entersInterior':target!=lo,'status':'raw-encoding-candidate-not-necessarily-instruction-boundary'})
    filebytes=SOURCE.read_bytes();boundary_bodies={}
    def boundary_context(address,length):
        rva=address-BASE
        entries=[e.struct for e in pe.DIRECTORY_ENTRY_EXCEPTION if e.struct.BeginAddress<=rva<e.struct.EndAddress]
        if len(entries)!=1:return {'status':'unresolved-runtime-boundary'}
        e=entries[0];key=hex(BASE+e.BeginAddress)
        if key not in boundary_bodies:
            raw=pe.get_data(e.BeginAddress,e.EndAddress-e.BeginAddress);body=list(md.disasm(raw,BASE+e.BeginAddress))
            complete=bool(body) and body[-1].address+body[-1].size==BASE+e.EndAddress
            boundary_bodies[key]={'start':key,'endExclusive':hex(BASE+e.EndAddress),'bytes':raw.hex(),'sha256':hashlib.sha256(raw).hexdigest(),'completeLinearDecode':complete,'instructions':[row(i) for i in body]}
        body=boundary_bodies[key]
        if not body['completeLinearDecode']:return {'status':'unresolved-incomplete-linear-decode','body':key}
        parent=next((r for r in body['instructions'] if int(r['address'],16)<=address and address+length<=int(r['address'],16)+len(bytes.fromhex(r['bytes']))),None)
        if parent is None:return {'status':'crosses-retained-instruction-boundaries','body':key}
        return {'status':'at-retained-instruction-start' if address==int(parent['address'],16) else 'inside-retained-instruction','body':key,'instruction':parent}
    for lo,hi,r in windows:
        entry=boundary_context(lo,hi-lo)
        # A two-instruction window intentionally crosses an instruction
        # boundary; use the enclosing runtime body only to retain context.
        assert 'body' in entry and boundary_bodies[entry['body']]['completeLinearDecode']
        body=boundary_bodies[entry['body']]
        assert int(body['start'],16)<=lo<hi<=int(body['endExclusive'],16)
        preceding=[i for i in body['instructions'] if int(i['address'],16)<lo]
        r['entryContext']={'runtimeBody':entry['body'],'precedingInstructions':preceding[-16:],'scope':'Exact nearby index loads/guards are retained as context, not a fresh all-predecessor proof. Mission identity comes from the accepted owner flow; valid native index and registry participation remain integration prerequisites.'}
        literals=[]
        for target in range(lo+1,hi):
            for kind,raw in [('VA64',struct.pack('<Q',target)),('RVA32',struct.pack('<I',target-BASE))]:
                pos=0
                while True:
                    pos=filebytes.find(raw,pos)
                    if pos<0:break
                    context=boundary_context(BASE+pe.get_rva_from_offset(pos),len(raw))
                    interpretation='unresolved-address-materialization-candidate'
                    if 'instruction' in context:
                        parent=context['instruction'];pi=next(md.disasm(bytes.fromhex(parent['bytes']),int(parent['address'],16)))
                        operands=[o for o in pi.operands if o.type==X86_OP_MEM and pi.reg_name(o.mem.base)=='rip']
                        if len(operands)==1 and pi.disp_offset==BASE+pe.get_rva_from_offset(pos)-pi.address and pi.disp_size==len(raw):
                            actual=pi.address+pi.size+operands[0].mem.disp
                            assert actual!=target
                            context['ripRelativeEffectiveAddress']=hex(actual)
                            interpretation='RIP-relative-displacement-match-not-an-absolute-entry-address'
                    literals.append({'fileOffset':pos,'target':hex(target),'encoding':kind,'boundaryContext':context,'interpretation':interpretation});pos+=1
        boundaries=[]
        for e in pe.DIRECTORY_ENTRY_EXCEPTION:
            f=e.struct
            for field in ('BeginAddress','EndAddress'):
                value=BASE+getattr(f,field)
                if lo<value<hi:boundaries.append({'field':field,'address':hex(value),'runtimeFunctionBegin':hex(BASE+f.BeginAddress)})
        for item in direct[lo]:
            item['boundaryContext']=boundary_context(int(item['source'],16),len(bytes.fromhex(item['bytes'])))
        r['incomingControlFlow']={'executableBytesScanned':scanned,'rawRelativeCandidates':direct[lo],'interiorLiteralCandidates':literals,'interiorRuntimeFunctionBoundaries':boundaries,'indirectEntryClosureProven':False,'boundaryClassificationScope':'A raw match inside a retained decoded instruction is not an ordinary direct branch/literal at that boundary; alternate computed instruction entries remain unproved. Literal matches may be unrelated RIP-relative displacements, never automatically absolute addresses.','normalFallthrough':'Operation follows IMUL directly; an entry at the operation or any other overwritten byte would bypass capture and is not allowed without rewriting that entry.'}
    assert before=={str(p):digest(p) for p in deps}
    return {'schemaVersion':1,'status':'bounded-scalar-agent-operation-route-contracts-not-installable','nativeSourceSha256':SHA,
        'sources':[{'path':p,'sha256':h} for p,h in sorted(before.items())],
        'candidateCount':len(decisions),'memoryOnlyUniqueCandidates':len(memory_only),'memoryOnlyContexts':sum(len(c['contexts']) for c in memory_only),'selectedAdjacentScalarRoutes':len(routes),
        'routes':routes,'candidateDecisions':decisions,'decisionCounts':dict(Counter(d['classification'] for d in decisions)),
        'rawMatchBoundaryBodies':list(boundary_bodies.values()),'regressionChecks':{'passed':len(tests),'cases':tests},
        'limits':['These seven adjacent scalar operations avoid returning an Agent or pointee address, but do not establish full-program object lifetime or installation safety.',
                  'QWORD loads are conservatively deferred because loaded pointers may outlive a short lease. Numeric scalar use is not guessed from width alone.',
                  'Near-adjacent owner loads from existing Agent/AI fields need a source-object lifetime contract and overlap-safe replay; they are excluded from this first subset.',
                  'Raw branch and literal scans preserve all candidates; unknown indirect/exception entry cannot be disproved by absence of literal references.',
                  'No constructed stub, exact native helper ABI, real-game concurrency ownership, invalid-index fallback or per-site unwind registration is approved.'],
        'enginePatchApproved':False,'gameLaunched':False}

def main():
    p=argparse.ArgumentParser();p.add_argument('--output',default=str(ROOT/'evidence/engine-extension/native-simple-agent-operation-routes.json'));args=p.parse_args()
    result=build();out=safe_review_output(args.output,[x['path'] for x in result['sources']]);out.write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps({'path':str(out),'sha256':digest(out),'routes':len(result['routes']),'memoryOnlyContexts':result['memoryOnlyContexts'],'decisions':result['decisionCounts']}))
if __name__=='__main__':main()
