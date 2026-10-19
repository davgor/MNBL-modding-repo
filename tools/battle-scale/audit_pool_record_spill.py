"""Bounded saved-Mission proof for five record-writer fields; static only."""
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
    paths=[folder/n for n in ('native-pool-reference-inventory.json','native-callback-contracts.json','native-closure-ghidra-evidence.json')]
    inventory,catalog,bundle=[json.loads(p.read_text()) for p in paths]
    binary=Path(inventory['binary']['path']);source=binary.read_bytes();sha=hashlib.sha256(source).hexdigest()
    if sha!=inventory['binary']['sha256']:raise ValueError('Target hash differs')
    pe=pefile.PE(data=source);base=pe.OPTIONAL_HEADER.ImageBase
    md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
    def span(a,b):
        rows=list(md.disasm(pe.get_data(a-base,b-a),a))
        if sum(i.size for i in rows)!=b-a:raise ValueError('Decode gap')
        return rows
    def raw(i):return dict(address=hex(i.address),bytes=i.bytes.hex(),decoded=i.mnemonic+' '+i.op_str)
    body=span(base+0x641e00,base+0x6428a1);by={i.address:i for i in body}
    caller=span(base+0x709bf0,base+0x709c9c);cb={i.address:i for i in caller}
    entry=next(c for c in catalog['contracts'] if c['entryAddress']=='0x180709bf0')
    if entry['pointerKind']!='mission' or entry['entryRegister']!='rcx':raise ValueError('Callback ABI differs')
    for address,text in ((0x709c15,'rbp, rcx'),(0x709c6e,'qword ptr [rsp + 0x20], rbp'),(0x709c7f,'0x180641e00')):
        if cb[base+address].op_str!=text:raise ValueError('Stack argument chain changed')
    for i in caller:
        if base+0x709c15<i.address<base+0x709c6e and 'rbp' in {canonical(i.reg_name(r)) for r in i.regs_access()[1]}:raise ValueError('Caller overwrites saved Mission')
    for address,text in ((0x641e0d,'rbp, [rsp - 0x4d8]'),(0x641e15,'rsp, 0x5d8'),(0x641e6e,'rcx, qword ptr [rbp + 0x540]'),(0x641e75,'qword ptr [rbp - 0x80], rcx')):
        if by[base+address].op_str!=text:raise ValueError('Frame or incoming argument geometry changed')
    # Eight pushes +5d8 allocation => entryRSP=currentRSP+618;
    # fifth arg at entryRSP+28=currentRSP+640=RBP+540.
    store=base+0x641e75;last=base+0x6422f6
    writes=[];addresses=[];calls=[]
    for i in body:
        if store<=i.address<=last:
            if i.mnemonic=='call':calls.append(i)
            for operand_index,op in enumerate(i.operands):
                if op.type!=capstone.x86.X86_OP_MEM or i.reg_name(op.mem.base) not in ('rsp','rbp'):continue
                if op.mem.index:raise ValueError('Indexed local requires review')
                offset=op.mem.disp+(0x100 if i.reg_name(op.mem.base)=='rbp' else 0)
                if memory_operand_writes(i,operand_index):
                    writes.append(dict(raw(i),stackOffset=offset,size=op.size))
                    if offset<0x88 and offset+op.size>0x80 and i.address!=store:raise ValueError('Another saved-slot write')
                if i.mnemonic=='lea':
                    addresses.append(dict(raw(i),stackOffset=offset))
                    if 0x80<=offset<0x88:raise ValueError('Saved-slot address escaped before final reload')
        if i.address>last and i.group(capstone.CS_GRP_JUMP):
            if i.operands[0].type!=capstone.x86.X86_OP_IMM or i.operands[0].imm<=last:raise ValueError('Later code reenters saved-slot lifetime')
    evidence=[]
    for address in (0x641e00,0x709bf0,0x738c50,0x25ddf0,0x725ad0,0x725c20,0x73ae70,0x73b180,0x0c63b0,0x7273e0):
        matches=[r for r in bundle['records'] if r.get('operation')=='procedure_pseudo_code' and r.get('parameters',{}).get('procedure')==hex(base+address)]
        if not matches:raise ValueError('Missing retained callee '+hex(address))
        value=matches[-1];evidence.append(dict(procedure=hex(base+address),evidenceId=value['evidence_id'],normalizedResult=value['normalized_result']))
    effects=[
        dict(calls=[hex(base+a) for a in (0x641ea8,0x641ed7,0x641f3b,0x641f72,0x641fb5,0x641ffd,0x642057)],callee='738c50',outputs=[[0x40,24]],reason='Only buffer header fields0,8,10 written. Reallocation and memcpy receive heap backing, not header.'),
        dict(calls=['0x1806420cd'],callee='738c50',outputs=[[0xa0,24]],reason='Same buffer allocation contract, separate header.'),
        dict(calls=['0x18064206f','0x180642172'],callee='memcpy thunk9e31a1',outputs=[],reason='Destination is heap backing from the reviewed buffer/string allocator. No local destination.'),
        dict(calls=['0x180642116'],callee='25ddf0',outputs=[[0x150,64]],reason='Result writes offsets0..3f only. Local compressor118 has fixed lz4hc vtable; class branch does not dispatch its address elsewhere. Compression receives heap input/output. Nested diagnostics receive callee-local objects.'),
        dict(calls=['0x180642140'],callee='0c6970->0c63b0->7273e0',outputs=[[0xc0,32]],reason='Wrapper forwards destination. Constructor initializes string0..1f; conversion passes same header to725ad0 (writes8 and18) then writeslength10. Text bytes go to allocated heap backing.'),
        dict(calls=['0x180642159','0x180642183'],callee='725ad0',outputs=[[0xc8,8],[0xd8,4]],reason='Only destination header+8 pointer and+18 allocation size; allocator/reallocator sees heap allocation.'),
        dict(calls=['0x1806421d1'],callee='73ae70',outputs=[[0x88,8]],reason='Caller handle slot receives oneQWORD; filename read into callee-local buffers, APIs only receive those copies.'),
        dict(calls=['0x180642210'],callee='725c20->725ad0',outputs=[[0x130,32]],reason='String ctor writesheader0..1f, dynamic data goes to heap; Mission source string read only.'),
        dict(calls=['0x180642255'],callee='WriteFile',outputs=[[0x64,4]],reason='Fourthargument DWORD output; readonly local input atRSPe0+offset; fifth OVERLAPPED argument null.'),
        dict(calls=['0x180642297'],callee='73b180',outputs=[],reason='Reads callerhandle; WriteFile output count is callee-local. Caller bytes are readonly.'),
        dict(calls=['0x1806422d6'],callee='WriteFile',outputs=[[0x68,4]],reason='Fourthargument DWORD output; readonly local input atRSPe4+offset; fifth OVERLAPPED argument null.')]
    if {int(a,16) for e in effects for a in e['calls']}!={i.address for i in calls}:raise ValueError('Call-effect list differs')
    for e in effects:
        if any(a<0x88 and a+s>0x80 for a,s in e['outputs']):raise ValueError('Callee output overlaps saved Mission')
    imports={i.address:i.name.decode() if i.name else None for entry in pe.DIRECTORY_ENTRY_IMPORT for i in entry.imports}
    for address in (base+0x642255,base+0x6422d6):
        i=by[address];target=i.address+i.size+i.operands[0].mem.disp
        if imports.get(target)!='WriteFile':raise ValueError('File import differs')
    wrapper=span(base+0xc6970,base+0xc69ac)
    if [i.operands[0].imm for i in wrapper if i.mnemonic=='call']!=[base+0xc63b0]:raise ValueError('Constructor wrapper changed')
    mappings=[]
    for rva in (0x6421f2,0x642205,0x64221d,0x6422fa,0x642305):
        i=by[base+rva];index=next(n for n,o in enumerate(i.operands) if o.type==capstone.x86.X86_OP_MEM and 0x62ca50<=o.mem.disp<=0x1184b40)
        op=i.operands[index];reg=i.reg_name(op.mem.base)
        nodes,origins,count=backward_proof(body,i.address,reg,store,last,'rbp',-0x80)
        off,size=displacement_encoding(i)
        mappings.append(dict(address=hex(i.address),instructionBytes=i.bytes.hex(),operandIndex=index,originalValue=hex(op.mem.disp),missionBiasBytes=0,missionRegister=reg,
            encodedOffset=off,encodedSize=size,classification='mission-relative-field',provenanceKind='bounded-641e00-fifth-argument-stack-spill-all-predecessors',
            provenanceInstructions=[raw(by[store])]+[raw(by[a]) for a in nodes],reloadOrigins=[hex(a) for a in origins],backwardStateCount=count,
            evidenceIds=list(dict.fromkeys(entry['evidenceIds']+[e['evidenceId'] for e in evidence]))))
    result=dict(schemaVersion=1,targetSha256=sha,status='exact-five-sites-proved-under-installed-callee-contracts',gameExecuted=False,
        sources=[dict(path=str(p),sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in paths],entryContract=entry,
        entryArgumentProof='Registered Mission callback709bf0 savesRCX inRBP, preserved across calls; passesRBP atcallerRSP20. CALL adds8; callee8pushes+5d8 allocation yield arg5 atRBP540.641e6e loads it then641e75 storesprivateRSP80.',
        callerInstructions=[raw(i) for i in caller],functionInstructions=[raw(i) for i in body],constructorWrapperInstructions=[raw(i) for i in wrapper],
        frame=dict(rbpMinusRsp=256,missionSpill=[0x80,8],initialStore=raw(by[store]),lastReload=raw(by[last]),directStackWrites=writes,localAddressMaterializations=addresses),
        callEffects=effects,allCallsDuringSpillLifetime=[raw(i) for i in calls],retainedCalleeEvidence=evidence,instructionSiteMappings=mappings,
        externalAbiReferences=[dict(url='https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-writefile',checked='2026-10-17',contract='lpBuffer is input; lpNumberOfBytesWritten is DWORD output; lpOverlapped is null at these call sites.')],
        summary=dict(mappedOperands=len(mappings),spillOtherDirectWrites=0,reviewedCallSites=len(calls)),
        limitations=['Saved slot is reused as a later WriteFile result only after final Mission reload; no backward edge returns. This does not certify record/replay compatibility with expanded agent IDs. Static normal-flow provenance, not executed runtime safety.'])
    out=folder/'native-pool-record-spill-review.json';safe_review_output(out,[binary]+paths).write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(result['summary']))

if __name__=='__main__':build()
