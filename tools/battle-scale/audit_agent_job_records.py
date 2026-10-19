"""Exact producer/copy proof for Agent in80-byte Mission job records."""
from pathlib import Path
import hashlib,json,sys
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import capstone,pefile
from patch_native_copy import safe_review_output
from native_instruction_semantics import memory_operand_writes

def build():
    folder=ROOT/'evidence/engine-extension'
    paths=[folder/n for n in ('native-pool-reference-inventory.json','pool-semantic-review-mid.json','native-leaf-owner-contracts.json')]
    inventory,mid,owners=[json.loads(p.read_text()) for p in paths]
    binary=Path(inventory['binary']['path']);source=binary.read_bytes();sha=hashlib.sha256(source).hexdigest()
    if sha!=inventory['binary']['sha256']:raise ValueError('Wrong target')
    pe=pefile.PE(data=source);base=pe.OPTIONAL_HEADER.ImageBase
    md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
    def span(a,b):
        code=list(md.disasm(pe.get_data(a,b-a),base+a))
        if sum(i.size for i in code)!=b-a:raise ValueError('Decode gap')
        return code
    def raw(i):return dict(address=hex(i.address),bytes=i.bytes.hex(),decoded=i.mnemonic+' '+i.op_str)
    ranges={'consumerAndSecondaryProducer':(0x5c6180,0x5c632d),'agentDispatch':(0x5ec6b0,0x5ec983),
        'componentDispatch':(0x687bc0,0x688052),'secondaryComponentDispatch':(0x689040,0x689215),
        'recordPopulation':(0x5c8ed0,0x5cbb26),'recordCopy':(0x5dc410,0x5dc502),
        'vectorAppend':(0x5d9870,0x5d99a4),'vectorTail':(0x5d8850,0x5d885c),
        'agentConstructorOwnerFields':(0x60761e,0x60764b)}
    bodies={name:span(*bounds) for name,bounds in ranges.items()};all_ins={i.address:i for code in bodies.values() for i in code}
    expected={0x687f10:'rax, qword ptr [rsi + 0x128]',0x687f1a:'qword ptr [rbp + 0x20], rax',
        0x6891b5:'qword ptr [rbx + 0x20], rdx',0x5dc4da:'xmm0, xmmword ptr [rdi + 0x20]',0x5dc4de:'xmmword ptr [rbx + 0x20], xmm0',
        0x5c62d8:'rdx, [rbp - 0x50]',0x5c62dc:'0x1805dc410',0x607636:'qword ptr [rbx + 0x150], rbx'}
    for rva,op in expected.items():
        if all_ins[base+rva].op_str!=op:raise ValueError('Record chain changed '+hex(rva))
    flow=mid['missionMethodFlowReview'];entry=flow['entryContracts']['0x180687bc0']
    if entry['entryRegisters'].get('rcx')!='agent' or entry['entryBiases'].get('rcx')!=0x28:raise ValueError('Missing typed source Agent component')
    upstream=flow['jobAgentQueueProof']
    # Population uses a stable R15 receiver. No alias escapes except the two
    # reviewed vector helpers; these modify only header0/8/10 or heap backing.
    population=bodies['recordPopulation'];aliases=[];writes=[];register_writes=[];other_uses=[]
    for n,i in enumerate(population):
        if capstone.x86.X86_REG_R15 in i.regs_access()[1]:register_writes.append(raw(i))
        if any(op.type==capstone.x86.X86_OP_REG and op.reg==capstone.x86.X86_REG_R15 and op.access&capstone.CS_AC_READ for op in i.operands) and i.mnemonic not in ('mov','push','pop'):
            other_uses.append(raw(i))
        for operand_index,op in enumerate(i.operands):
            if op.type==capstone.x86.X86_OP_MEM and i.reg_name(op.mem.base)=='r15' and memory_operand_writes(i,operand_index):
                writes.append(raw(i))
                if op.mem.index or op.mem.disp<0x28 and op.mem.disp+op.size>0x20:raise ValueError('Population overwrites Agent field')
        if i.mnemonic=='mov' and len(i.operands)==2 and i.operands[1].type==capstone.x86.X86_OP_REG and i.reg_name(i.operands[1].reg)=='r15':
            aliases.append(i.address)
    if aliases!=[base+0x5cba38,base+0x5cba82]:raise ValueError('New record pointer escape')
    if [i['decoded'] for i in register_writes]!=['mov r15, r8','pop r15'] or other_uses:raise ValueError('Record receiver identity or escape differs')
    result=dict(schemaVersion=1,targetSha256=sha,status='typed-secondary-job-record-owner-proof',gameExecuted=False,
        sources=[dict(path=str(p),sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in paths],
        upstreamAgentQueueProof=upstream,componentEntryContract=entry,
        proofSteps=[
            'Upstream16-byte queue producer and5c6180 consumer establish5ec6b0 RCX=Agent. Non-3 dispatch adds28 and calls687bc0.',
            '607636 storesAgent self atAgent+150. ThereforeAgent+28 component+128 is that Agent; Agent+148 is Mission.',
            '687bc0 saves entrycomponent inRSI and outputR8 inRBP.687f10 loadscomponent+128 Agent;687f1a storesit atoutput+20 before5c8ed0.',
            'Other component branch tail68804d forwards samecomponent and outputrecord to689040.6891b5 storesloadedcomponent+128 Agent atoutput+20; explicit null path requires componentAgent fieldnull, excluded for the constructedAgent self field.',
            '5c8ed0 savesoutputrecord inR15. It neverwritesrecord+20 and onlyforwardsheader to5d9870/5d8850.5d9870 modifiesheader0/8/10 andallocatedbacking;5d8850 forwardsbackingbegin/end, notheader.',
            '5c6180 initializedlocalrecord empty. Special dispatch3 and earlyreturns leaveempty vector;626c skipsqueueappend when elementcountzero. Otherwise recordAgent came from oneof the two populationstores.',
            '5c6272 selectsMission+1043e20counter, pageindex=oldcounter>>4, slot=(counter-page*16)*50;62dc copieslocalrecord tothatpage.',
            '5dc410 usesRDI=source,RBX=destination, copies16bytesfromsource+20 todestination+20 at4da/4de. Otherwritesinitializevectorheader orcopy+30/+40. Thusstoredrecord+20 retainsthe Agent pointer.' ],
        pointerRecord=dict(missionTableOffset='0x1043e28',pageRecordCount=16,recordStride='0x50',fieldOffset='0x20',pointerKind='agent',nullable=False,
            qualification='Records enqueued by the observed normal Mission frame producer using constructed Agent components; excludes arbitrary injected record pointers.'),
        suggestedConsumerContracts=[dict(entryAddress='0x180685a90',entryRegister='rcx',pointerKind='agent-path:32'),dict(entryAddress='0x180686450',entryRegister='rcx',pointerKind='agent-path:32')],
        consumerQualification='Consumer entry contracts still require the existing exact1043e28 page-reader/dispatch proof; these addresses alone are not authorization.',
        instructions={name:[raw(i) for i in code] for name,code in bodies.items()},populationDirectRecordWrites=writes,populationReceiverRegisterWrites=register_writes,
        evidenceIds=entry['evidenceIds'],limitations=['Exact observed producer/copy contract; no generic field-offset heuristic, no game run.'])
    out=folder/'native-agent-job-record-contracts.json';safe_review_output(out,[binary]+paths).write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(dict(recordStride=80,agentFieldOffset=32,instructionEntries=sum(len(c) for c in bodies.values()))))

if __name__=='__main__':build()
