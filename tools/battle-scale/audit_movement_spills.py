"""Retain bounded AI movement receiver/Mission spill contracts, without execution."""
import hashlib
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT/'build/battle-scale/python-libs'))
import capstone
import pefile
from patch_native_copy import safe_review_output
from native_instruction_semantics import memory_operand_writes


def audit():
    folder = ROOT/'evidence/engine-extension'
    inputs = [folder/n for n in ('native-pool-reference-inventory.json','pool-semantic-review-mid.json','native-closure-ghidra-evidence.json')]
    inventory,mid,bundle = [json.loads(p.read_text()) for p in inputs]
    binary = Path(inventory['binary']['path'])
    source = binary.read_bytes()
    digest = hashlib.sha256(source).hexdigest()
    if digest != '2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a':
        raise ValueError('Unsupported installed source')
    pe = pefile.PE(data=source)
    base = pe.OPTIONAL_HEADER.ImageBase
    md = capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64)
    md.detail = True
    def span(a,z):
        ins = list(md.disasm(pe.get_data(a-base,z-a),a))
        if sum(i.size for i in ins) != z-a: raise ValueError('Incomplete decode')
        return ins
    def raw(i):return dict(address=hex(i.address),bytes=i.bytes.hex(),decoded=i.mnemonic+' '+i.op_str)
    def exact(a,h):
        if pe.get_data(a-base,len(bytes.fromhex(h))).hex()!=h:raise ValueError('Instruction changed '+hex(a))
    def evidence(a):
        rows = [r for r in bundle['records'] if r.get('parameters',{}).get('procedure')==hex(a) and r.get('operation')=='procedure_pseudo_code']
        if not rows:raise ValueError('Missing retained body '+hex(a))
        return dict(procedure=hex(a),evidenceId=rows[-1]['evidence_id'],normalizedResult=rows[-1]['normalized_result'])
    caller = span(base+0x6c9430,base+0x6c96ed)
    movement = span(base+0x6d07c0,base+0x6d2110)
    owner = mid['missionMethodFlowReview']['entryContracts']['0x1806c9430']
    if owner['entryRegisters'].get('rcx')!='agent-ai' or owner['entryBiases'].get('rcx')!=0x180:
        raise ValueError('Missing AI movement entry contract')
    exact(base+0x6c9430,'488bc448894808')
    exact(base+0x6c943a,'488da878feffff4881ec78020000')
    # Two pushes and allocation278: RBP=RSP100, incoming home=RSP290.
    local_addresses=[]
    for i in caller:
        for operand_index,o in enumerate(i.operands):
            if o.type!=capstone.x86.X86_OP_MEM or i.reg_name(o.mem.base) not in ('rsp','rbp'):continue
            off=o.mem.disp+(0x100 if i.reg_name(o.mem.base)=='rbp' else 0)
            if o.mem.index:raise ValueError('Indexed caller stack access needs proof')
            if memory_operand_writes(i,operand_index) and off<0x298 and off+o.size>0x290:
                raise ValueError('Saved incoming receiver overwritten')
            if i.mnemonic=='lea':
                local_addresses.append(dict(raw(i),stackOffset=off))
                if off not in (0x298,0x50):raise ValueError('Unexpected caller local address escape')
    output_helper=span(base+0x6d21d0,base+0x6d292c)
    exact(base+0x6d226a,'4c8be2')
    exact(base+0x6d2270,'48c702ffffffff')
    exact(base+0x6d2850,'49210c24')
    # Caller output helper retains RDX only in R12, never forwards it; two8byte writes.
    output_uses=[raw(i) for i in output_helper if 'r12' in i.op_str]
    if [int(x['address'],16)-base for x in output_uses]!=[0x6d21d4,0x6d226a,0x6d2850,0x6d28c7,0x6d2926]:
        raise ValueError('Output helper receiver uses changed')
    # Mission is loaded through the previously reviewed AI+1a8->AI->Agent->Mission chain.
    exact(base+0x6d0827,'488b4128')
    exact(base+0x6d0836,'4c8b304c8975d0498b86b80a0000488945e0')
    exact(base+0x6d0b43,'4c8b65e0')
    early=[i for i in movement if i.address<=base+0x6d0b43]
    for i in early:
        for operand_index,o in enumerate(i.operands):
            if o.type!=capstone.x86.X86_OP_MEM or i.reg_name(o.mem.base) not in ('rsp','rbp'):continue
            off=o.mem.disp+(0x100 if i.reg_name(o.mem.base)=='rbp' else 0)
            if o.mem.index:raise ValueError('Indexed movement stack access needs proof')
            if memory_operand_writes(i,operand_index) and off<0xe8 and off+o.size>0xe0 and i.address!=base+0x6d0844:
                raise ValueError('Mission spill overwritten')
            if i.mnemonic=='lea' and 0xe0<=off<0xe8:raise ValueError('Mission slot address escaped')
    for i in movement:
        if i.address>base+0x6d0b43 and i.group(capstone.CS_GRP_JUMP):
            if i.operands[0].type!=capstone.x86.X86_OP_IMM or i.operands[0].imm<=base+0x6d0b43:
                raise ValueError('Later path can reenter spill interval')
    if [i.operands[0].imm-base for i in early if i.mnemonic=='call']!=[0x5e91f0,0x6c16c0,0x9e0960,0x5e8c50]:
        raise ValueError('Movement spill callees changed')
    direction=span(base+0x5e91f0,base+0x5e93a9)
    exact(base+0x5e9202,'488bfa')
    exact(base+0x5e9399,'488907488bc7')
    direction_uses=[raw(i) for i in direction if 'rdi' in i.op_str]
    if [int(x['address'],16)-base for x in direction_uses]!=[0x5e91f2,0x5e9202,0x5e9399,0x5e939c,0x5e93a6]:
        raise ValueError('Direction output alias uses changed')
    evidence_rows=[evidence(base+a) for a in (0x6c9430,0x6d07c0,0x6d21d0,0x5e91f0,0x69bf00)]
    seeds={}
    for i in caller:
        if i.mnemonic=='mov' and len(i.operands)==2 and i.operands[1].type==capstone.x86.X86_OP_MEM and i.reg_name(i.operands[1].mem.base)=='rbp' and i.operands[1].mem.disp==0x190:
            seeds[hex(i.address)]=dict(register=i.reg_name(i.operands[0].reg),pointerKind='agent-ai',biasBytes=0x180,instruction=raw(i))
    reload=next(i for i in early if i.address==base+0x6d0b43)
    seeds[hex(reload.address)]=dict(register='r12',pointerKind='mission',biasBytes=0,instruction=raw(reload))
    contract=dict(procedure='0x1806d07c0',entryRegisters={'rcx':'agent-ai'},entryBiases={'rcx':0x180},
        kind='AI-movement-private-home-receiver',ownerContract=owner,
        proof='6c9430 stores incomingAI+180 in its privatehome. Only neighboring local output exposed before6c96e8 ishome+8, to6d21d0 which writes8bytes and does not forward output. Reloaded receiver reachesRCX either at96b3 directly, or viaR10 at96d9; the latter path has no intervening call after reviewedreload. Thus6d07c0 receives the same movement subobject.',
        evidenceIds=list(dict.fromkeys(owner['evidenceIds']+[r['evidenceId'] for r in evidence_rows])))
    result=dict(schemaVersion=1,targetSha256=digest,status='bounded-receiver-and-Mission-spill-contracts-reviewed',gameExecuted=False,
        entryContracts=[contract],exactReloadSeeds=seeds,
        callerInstructions=[raw(i) for i in caller],movementInstructions=[raw(i) for i in movement],
        outputHelperInstructions=[raw(i) for i in output_helper],directionHelperInstructions=[raw(i) for i in direction],
        callerLocalAddresses=local_addresses,retainedEvidence=evidence_rows,
        proof='Movement Mission private slotRBP-20 is written once before reload6d0b43.5e91f0 writes only8bytes atRBP20; its output pointer is retained only inRDI and never forwarded. Other pre-reload calls receive no local output. No later branch reenters pre-reload interval. R12 then holds Mission under normal nonvolatile ABI; individual operand proof remains the CFG prover responsibility.',
        limitations=['Only the listed reloads are certified; this is not a blanket permission to retain stack facts across calls.','Calls are assumed to obey the installed native ABI; runtime corruption is not ruled out.'])
    path=safe_review_output(folder/'native-movement-spill-contracts.json',[binary,*inputs,Path(__file__).resolve()])
    path.write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(dict(output=str(path),reloads=len(seeds))))


if __name__=='__main__':audit()


