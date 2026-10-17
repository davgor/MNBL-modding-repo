"""Execution-free saved Mission proof for the two early5c8ed0 fields."""
from pathlib import Path
import hashlib,json,sys
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import capstone,pefile
from audit_pool_tick_spill import backward_proof,canonical
from build_pool_patch_manifest import displacement_encoding
from patch_native_copy import safe_review_output
from native_instruction_semantics import memory_operand_writes

def build():
    folder=ROOT/'evidence/engine-extension'
    paths=[folder/n for n in ('native-pool-reference-inventory.json','pool-semantic-review-mid.json','native-expansion-ghidra-evidence.json','native-closure-ghidra-evidence.json')]
    inventory,mid,expansion,closure=[json.loads(p.read_text()) for p in paths]
    binary=Path(inventory['binary']['path']);source=binary.read_bytes();sha=hashlib.sha256(source).hexdigest()
    if sha!=inventory['binary']['sha256']:raise ValueError('Wrong target')
    pe=pefile.PE(data=source);base=pe.OPTIONAL_HEADER.ImageBase
    md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
    def span(a,b):
        rows=list(md.disasm(pe.get_data(a-base,b-a),a))
        if sum(i.size for i in rows)!=b-a:raise ValueError('Decode gap')
        return rows
    def raw(i):return dict(address=hex(i.address),bytes=i.bytes.hex(),decoded=i.mnemonic+' '+i.op_str)
    body=span(base+0x5c8ed0,base+0x5cbb26);by={i.address:i for i in body}
    entry=mid['missionMethodFlowReview']['entryContracts']['0x1805c8ed0']
    if entry.get('entryRegisters',{}).get('rcx')!='mission' or entry.get('entryBiases',{}).get('rcx',0)!=0:raise ValueError('No exact RCX Mission entry contract')
    probe=mid['missionMethodFlowReview']['stackProbeCallSummary'];probe_bytes=bytes.fromhex(probe['bytes'])
    if pe.get_data(int(probe['address'],16)-base,len(probe_bytes))!=probe_bytes:raise ValueError('Stack probe changed')
    store=base+0x5c8f73;last=base+0x5c9665
    for address,text in ((0x5c8edc,'rbp, [rsp - 0xd7c0]'),(0x5c8ee4,'eax, 0xd8c0'),(0x5c8eee,'rsp, rax'),(0x5c8f73,'qword ptr [rbp + 0xa8], rcx')):
        if by[base+address].op_str!=text:raise ValueError('Frame geometry changed')
    for i in body:
        if i.address>=store:break
        if 'rcx' in {canonical(i.reg_name(r)) for r in i.regs_access()[1]}:raise ValueError('Entry Mission changed before spill')
        if i.mnemonic=='call' and i.op_str!='0x1809e0760':raise ValueError('Unreviewed prologue call')
    writes=[];addresses=[];calls=[]
    for i in body:
        if store<=i.address<=last:
            if i.mnemonic=='call':calls.append(i)
            for operand_index,o in enumerate(i.operands):
                if o.type!=capstone.x86.X86_OP_MEM or i.reg_name(o.mem.base) not in ('rsp','rbp'):continue
                if o.mem.index:raise ValueError('Indexed stack access')
                offset=o.mem.disp+(256 if i.reg_name(o.mem.base)=='rbp' else 0)
                if memory_operand_writes(i,operand_index):
                    writes.append(dict(raw(i),stackOffset=offset,size=o.size))
                    if offset<0x1b0 and offset+o.size>0x1a8 and i.address!=store:raise ValueError('Spill overwritten')
                if i.mnemonic=='lea':
                    addresses.append(dict(raw(i),stackOffset=offset))
                    if 0x1a8<=offset<0x1b0:raise ValueError('Saved slot address escapes')
        if i.address>last and i.group(capstone.CS_GRP_JUMP) and (i.operands[0].type!=capstone.x86.X86_OP_IMM or i.operands[0].imm<=last):raise ValueError('Later path returns to reviewed lifetime')
    records=[r for b in (expansion,closure) for r in b['records']];evidence=[]
    for a in (0x5c8ed0,0x615dd0,0x5ea810):
        matches=[r for r in records if r.get('operation')=='procedure_pseudo_code' and r.get('parameters',{}).get('procedure')==hex(base+a)]
        if not matches:raise ValueError('Missing durable helper '+hex(a))
        r=matches[-1];evidence.append(dict(procedure=hex(base+a),evidenceId=r['evidence_id'],normalizedResult=r['normalized_result']))
    helper_ranges=[(0x6d9060,0x6d909d),(0x1f1e80,0x1f1ecd),(0x1f1ed0,0x1f1ee2),(0x3568d0,0x35692f),
        (0x1bc380,0x1bc3be),(0x08bdd0,0x08bde8),(0x3e00e0,0x3e025a),(0x3e18c0,0x3e19f7),
        (0x75ca80,0x75caef),(0x5e57d0,0x5e58d4),(0x08eaf0,0x08eb1a)]
    helpers=[]
    for a,b in helper_ranges:
        code=span(base+a,base+b)
        for i in code:
            if i.group(capstone.CS_GRP_JUMP) and (i.operands[0].type!=capstone.x86.X86_OP_IMM or not base+a<=i.operands[0].imm<base+b):raise ValueError('Helper branch leaves complete reviewed body')
            if i.mnemonic=='call' and (i.operands[0].type!=capstone.x86.X86_OP_IMM or i.operands[0].imm-base not in (0x729b40,0x74e270,0x9e31ad,0x9df5b0)):raise ValueError('Unexpected helper callee')
        helpers.append(dict(start=hex(base+a),endExclusive=hex(base+b),instructions=[raw(i) for i in code]))
    effects=[
        dict(calls=[hex(base+x) for x in (0x5c8fcb,0x5c901a,0x5c904f,0x5c9068,0x5c9147)],callee='external Agent/item methods',outputs=[],reason='Receivers and other arguments derive from Agent/item objects or scalar flags; no local pointer argument. Exact caller argument contexts retained.'),
        dict(calls=['0x1805c9098'],callee='6d9060',outputs=[[0x5b8,16]],reason='Full leaf writes only16 bytes toRDX; pointer chasing onRCX is readonly.'),
        dict(calls=['0x1805c9182'],callee='1f1e80',outputs=[[0x14d0,32]],reason='Copies two16-byte blocks toRDX; RCX external lock word; security-cookie call receives scalar.'),
        dict(calls=['0x1805c9195'],callee='1f1ed0',outputs=[[0x5e0,32]],reason='Two16-byte output stores; input local14d0 readonly.'),
        dict(calls=['0x1805c91ac'],callee='3568d0',outputs=[[0x420,16]],reason='Writes exactlyfour floats toRDX; RCX transform andR8local5f0 readonly.'),
        dict(calls=['0x1805c91f9'],callee='615dd0',outputs=[[0x14f0,0xec]],reason='Output floats0..3a only; param10callerlocal1f0 is readonly. Only nested math calls receive floating scalar arguments. Returns output pointer, caller copies values without escaping it.'),
        dict(calls=['0x1805c92b8'],callee='1bc380',outputs=[[0x1420,0x48]],reason='Local vector header initializes fields0..1b and30..47; no calls.'),
        dict(calls=['0x1805c92d0'],callee='08bdd0',outputs=[[0x14a8,0x1c]],reason='Initializes local vector header0..1b; no calls.'),
        dict(calls=['0x1805c9319'],callee='5ea810',outputs=[[0x3d0,64]],reason='EightQWORD caller-result stores only.73a920/61a990 receive callee-local matrices and vectors; caller result address is not forwarded. Returns result, then caller copies values.'),
        dict(calls=['0x1805c935f'],callee='3e00e0',outputs=[[0x1420,0x48]],reason='Vector copy writes destinationheader0/8/10/20..47. Backing startsnull from1bc380 and receives allocation; memmove/free only get backing pointers, not local header.'),
        dict(calls=['0x1805c939e'],callee='3e18c0',outputs=[[0x14a8,24]],reason='Same heap-vector copy discipline for second header; source externalrbx+88. Outputheader0/8/10.'),
        dict(calls=['0x1805c93b2'],callee='75ca80',outputs=[],reason='Scales copiedvector data through header+88 begin; that is second initialized/copied header atlocal14a8, whose backing is heap. No caller header writes; callee ownhome slots used forfloatpack.'),
        dict(calls=['0x1805c958c'],callee='5e57d0',outputs=[],reason='Full leaf readonly vector maximum reduction; no writes or calls.'),
        dict(calls=['0x1805c965b'],callee='08eaf0',outputs=[],reason='Full leaf reads three floats fromlocalRSPa0; returns square root, no writes or calls.')]
    if {int(a,16) for e in effects for a in e['calls']}!={i.address for i in calls}:raise ValueError('Unaccounted call site')
    if any(a<0x1b0 and a+s>0x1a8 for e in effects for a,s in e['outputs']):raise ValueError('Bounded output overlaps spill')
    mappings=[]
    for address in (base+0x5c966c,base+0x5c9675):
        i=by[address];index=1;nodes,origins,count=backward_proof(body,address,'r13',store,last,'rbp',0xa8)
        off,size=displacement_encoding(i)
        mappings.append(dict(address=hex(address),instructionBytes=i.bytes.hex(),operandIndex=index,originalValue=hex(i.operands[index].mem.disp),missionBiasBytes=0,missionRegister='r13',
            encodedOffset=off,encodedSize=size,classification='mission-relative-field',provenanceKind='bounded-5c8ed0-early-saved-Mission',
            provenanceInstructions=[raw(by[store])]+[raw(by[a]) for a in nodes],reloadOrigins=[hex(a) for a in origins],backwardStateCount=count,
            evidenceIds=list(dict.fromkeys(entry['evidenceIds']+[e['evidenceId'] for e in evidence]))))
    result=dict(schemaVersion=1,targetSha256=sha,status='exact-two-sites-proved-under-installed-callee-contracts',gameExecuted=False,
        sources=[dict(path=str(p),sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in paths],entryContract=entry,stackProbeContract=probe,
        frame=dict(rbpMinusRsp=256,missionSpill=[0x1a8,8],initialStore=raw(by[store]),reviewedReload=raw(by[last]),directStackWrites=writes,localAddressMaterializations=addresses),
        functionInstructions=[raw(i) for i in body],helperInstructions=helpers,retainedCalleeEvidence=evidence,
        callEffects=effects,allCallsDuringReviewedLifetime=[raw(i) for i in calls],instructionSiteMappings=mappings,
        exactReloadSeeds={hex(last):dict(register='r13',pointerKind='mission',biasBytes=0,instruction=raw(by[last]))},
        summary=dict(mappedOperands=2,reviewedCallSites=len(calls),spillOtherDirectWrites=0),
        limitations=['Only early9665 reload is authorized; later reloads require their own lifetime proof. Standard installed allocator/math/security ABI; arbitrary hooks excluded. No game execution.'])
    out=folder/'native-pool-attack-spill-review.json';safe_review_output(out,[binary]+paths).write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(result['summary']))

if __name__=='__main__':build()
