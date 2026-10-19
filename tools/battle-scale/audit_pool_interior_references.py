"""Review exact interior-pool operands and negative displacement candidates.

No numeric match alone grants patch approval. Unproved operands remain explicit.
"""
from pathlib import Path
import hashlib
import json
import struct
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT/'build/battle-scale/python-libs'))
import capstone
import pefile
from patch_native_copy import safe_review_output
from build_pool_patch_manifest import displacement_encoding


def build():
    folder = ROOT/'evidence/engine-extension'
    paths = [folder/n for n in ('native-pool-reference-inventory.json','native-pool-negative-offsets.json','pool-semantic-review-mid.json',
        'pool-semantic-review-low.json','native-expansion-ghidra-evidence.json','native-closure-ghidra-evidence.json','pool-semantic-review-high.json')]
    inventory, negative, mid, low, expansion, closure, high = [json.loads(p.read_text()) for p in paths]
    helper_path=folder/'native-pool-helper-caller-review.json'
    constructor_path=folder/'native-pool-helper-escape-review.json'
    helper=json.loads(helper_path.read_text());constructor=json.loads(constructor_path.read_text())
    paths.extend([helper_path,constructor_path])
    if helper['unclassifiedListedEntries'] or constructor['remainingUnclassifiedLiterals']:raise ValueError('Listed helper closure remains incomplete')
    source_path = Path(inventory['binary']['path']); source = source_path.read_bytes()
    source_hash = hashlib.sha256(source).hexdigest()
    if source_hash != inventory['binary']['sha256']:
        raise ValueError('Source hash mismatch')
    pe=pefile.PE(data=source); base=pe.OPTIONAL_HEADER.ImageBase
    md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
    def span(a,b):
        decoded=list(md.disasm(pe.get_data(a-base,b-a),a))
        if sum(i.size for i in decoded)!=b-a: raise ValueError('Decode gap in proof span')
        return decoded
    def raw(i): return {'address':hex(i.address),'bytes':i.bytes.hex(),'decoded':i.mnemonic+' '+i.op_str}
    def at(a): return next(md.disasm(pe.get_data(a-base,15),a))
    records={x['evidence_id']:x for bundle in (expansion,closure) for x in bundle['records']}
    def pseudo_refs(address):
        return [x['evidence_id'] for x in records.values() if x.get('parameters',{}).get('procedure')==hex(address) and
                isinstance(x.get('normalized_result'),str) and 'FUN_' in x['normalized_result']]
    rules=[
        (0x1805cf3c0,0x1805cf516,0x1805cf59c,'r14',0x1805cf593),
        (0x1805cfb50,0x1805cfc13,0x1805cfc9c,'r12',0x1805cfc93),
        (0x1805d00a0,0x1805d00d2,0x1805d0837,'r15',None),
    ]
    mappings=[]
    for function,definition,end,register,selected in rules:
        contract=mid['missionMethodFlowReview']['entryContracts'][hex(function)]
        if contract['entryPointerKind']!='mission':raise ValueError('Expected externally established Mission entry')
        instructions=span(definition,end)
        first=instructions[0]
        if first.mnemonic!='lea' or first.reg_name(first.operands[0].reg)!=register or first.operands[1].mem.disp!=0x2d50:
            raise ValueError('Interior pool origin differs')
        owner_register=first.reg_name(first.operands[1].mem.base)
        prologue=span(function,definition)
        if owner_register!='rcx':
            definitions=[i for i in prologue if any(i.reg_name(r)==owner_register for r in i.regs_access()[1])]
            if len(definitions)!=1 or definitions[0].mnemonic!='mov' or definitions[0].op_str!=owner_register+', rcx':
                raise ValueError('Mission alias is not a unique preserved entry copy')
            capture=definitions[0]
            if any(i.mnemonic.startswith('j') or i.mnemonic=='call' for i in prologue if i.address<capture.address):
                raise ValueError('Mission entry copy is not established before control flow')
        else:
            if any(i.mnemonic.startswith('j') or i.mnemonic=='call' or any(i.reg_name(r)=='rcx' for r in i.regs_access()[1]) for i in prologue):
                raise ValueError('Entry RCX changed before pool alias definition')
        for ins in instructions[1:]:
            if any(ins.reg_name(r)==register for r in ins.regs_access()[1]):
                raise ValueError('Pool alias changes before selected end')
            if selected is not None and ins.address!=selected:continue
            for index,op in enumerate(ins.operands):
                if op.type==capstone.x86.X86_OP_MEM and ins.reg_name(op.mem.base)==register and op.mem.disp in (0x629d00,0x62acf0,0x62acf4):
                    offset,size=displacement_encoding(ins)
                    mappings.append(dict(raw(ins),classification='pool-relative',poolStride=0xc60,operandIndex=index,
                        originalValue=op.mem.disp,encodedOffset=offset,encodedSize=size,functionStart=hex(function),
                        pointerDefinition=raw(first),entryContract=contract,evidenceIds=list(dict.fromkeys(contract['evidenceIds']+pseudo_refs(function))),
                        proofInstructions=[raw(i) for i in prologue+instructions],
                        reason='Exact interior alias is Mission+0x2d50; it remains unchanged through this metadata access. ABI calls preserve this nonvolatile register. Relocate c60-pool metadata, preserving slot stride and signed-short element width.'))
    #647080 constructs a real primary Agent from the current Mission global,
    #then forwards Agent.ab8 into5c86a0 at both call sites.
    caller_proof=span(0x1806475f1,0x180647656)
    if caller_proof[0].address+caller_proof[0].size+caller_proof[0].operands[1].mem.disp!=0x180da9d60:
        raise ValueError('Current-Mission global load differs')
    if at(0x18064762d).op_str!='rcx, qword ptr [rsi + 0xab8]':raise ValueError('Agent owner load differs')
    interior_prefix=span(0x1805c86a0,0x1805c8766)
    alias=at(0x1805c86bd)
    if alias.mnemonic!='add' or alias.op_str!='rcx, 0x2d50':raise ValueError('Primary pool alias differs')
    if any(i.mnemonic=='call' or any(i.reg_name(r) in ('rcx','ecx') for r in i.regs_access()[1]) for i in interior_prefix if i.address>alias.address):
        raise ValueError('RCX no longer holds primary pool before helper call')
    callback_refs=['ev_1a09771a444704ed72f2af6bb8672c3b35a3812672f352b84e02a33efcde5234',
        'ev_dbeb9110c41451f7a964f29db4d1cff34e52c9cbeedc258f5d72fdcfdb4313a1',
        'ev_823ea3eb895557ec3bf347c45bc9caaca665dc30c42e3a148a8bc97eaff7615d',
        'ev_bff745da319c2255660372897468b914506d523b9653cf0321aec330db183ee6']
    if not all(e in records for e in callback_refs):raise ValueError('Missing retained caller-chain proof')
    for address in (0x1805c86c4,0x1805c86d0,0x18059ea00):
        ins=at(address)
        index=next(n for n,o in enumerate(ins.operands) if o.type==capstone.x86.X86_OP_MEM)
        op=ins.operands[index]
        if op.mem.disp not in (0x629d00,0x62acf4) or ins.reg_name(op.mem.base)!='rcx':raise ValueError('Selected pool operand differs')
        offset,size=displacement_encoding(ins)
        mappings.append(dict(raw(ins),classification='pool-relative',poolStride=0xc60,operandIndex=index,originalValue=op.mem.disp,
            encodedOffset=offset,encodedSize=size,functionStart='0x18059ea00' if address==0x18059ea00 else '0x1805c86a0',
            pointerDefinition=raw(alias),proofInstructions=[raw(i) for i in caller_proof+interior_prefix],
            otherDirectCallerInstructions=[raw(i) for i in span(0x180605a77,0x180605a8a)],
            evidenceIds=callback_refs+pseudo_refs(0x18059ea00)+pseudo_refs(0x1805c86a0),
            reason='647080 loads current Mission global, constructs primary Agent with index*c60+2d50, and passes Agent.ab8 to5c86a0. Its ADD RCX,2d50 remains live until59ea00; other direct caller604f30 explicitly passes Mission+2d50. Shared helper reads c60-pool high-water and scans element+10 at stridec60.'))
    owner_site=next(x for x in mid['instructionSiteMappings'] if x['address']=='0x1805c43ba')
    if owner_site['missionRegister']!='r13' or owner_site['missionBiasBytes']!=0:raise ValueError('Computed companion field owner differs')
    arithmetic=span(0x1805c4381,0x1805c43ce)
    ins=at(0x1805c43c4)
    if (arithmetic[0].op_str!='r8, rcx, 0x2c8' or ins.op_str!='r8, 0xd70740' or arithmetic[-1].op_str!='r8, r13'):
        raise ValueError('Computed companion field sequence differs')
    if any(i.mnemonic=='call' or any(i.reg_name(r) in ('r8','r8d','r13','r13d') for r in i.regs_access()[1]) for i in arithmetic[1:-2]):
        raise ValueError('Computed companion field intermediates changed')
    mappings.append(dict(raw(ins),classification='mission-relative-immediate',operandIndex=1,originalValue=0xd70740,
        encodedOffset=ins.imm_offset,encodedSize=ins.imm_size,missionBaseBiasBytes=0,
        proofInstructions=[raw(i) for i in arithmetic],missionRegisterProof=owner_site,evidenceIds=owner_site['evidenceIds'],
        reason='R8 holds index*2c8, receives field offsetd70740, then adds proven MissionR13. The constant denotes companionA base+0x178, not an unrelated scalar; preserve stride and relocate this offset before the Mission addition.'))
    from audit_pool_tick_spill import backward_proof
    owner_site=next(x for x in mid['instructionSiteMappings'] if x['address']=='0x1805e04f3')
    if owner_site['missionRegister']!='rdx' or owner_site['missionBiasBytes']!=0:raise ValueError('Companion worker owner differs')
    worker=span(0x1805e04c0,0x1805e0675)
    # Here the origin is a proven closure load, not a stack slot. The same
    # predecessor traversal verifies RDX cannot arrive from the other branch's
    # unrelated object-pointer load at55f.
    nodes,origins,_=backward_proof(worker,0x1805e05da,'rdx',0x1805e04c0,0x1805e04f0,'r14',0)
    if origins!=[0x1805e04f0]:raise ValueError('Unexpected Mission definition at indexed addition')
    arithmetic=span(0x1805e05cf,0x1805e061d);ins=at(0x1805e0616)
    if arithmetic[0].op_str!='rbx, rax, 0x230' or at(0x1805e05da).op_str!='rbx, rdx' or ins.op_str!='rdx, [rbx + 0xc58750]':
        raise ValueError('Indexed companion base changed')
    if any(any(i.reg_name(r) in ('rbx','ebx','bx','bl','bh') for r in i.regs_access()[1]) for i in arithmetic if 0x1805e05da<i.address<ins.address):
        raise ValueError('Indexed base overwritten')
    mappings.append(dict(raw(ins),classification='mission-relative-indexed-field',operandIndex=1,originalValue=0xc58750,
        missionBaseBiasBytes=0,missionRegisterProof=owner_site,evidenceIds=owner_site['evidenceIds'],
        proofInstructions=[raw(i) for i in worker if i.address in set(nodes)]+[raw(i) for i in arithmetic],
        reason='All predecessor paths giveRDX=Mission at5e05da. RBX=index*230+Mission then survives virtualcall asABI nonvolatile; LEA adds230poolbasec58750. Relocate field displacement, preserve element stride.'))
    selected={x['address'] for x in mappings}
    existing=set()
    manifest=json.loads((folder/'native-pool-patch-4080.json').read_text())
    existing={x['address'] for x in manifest['patches'] if 'exact_interior_review' not in x['scopes']}
    remaining=[]
    for candidate in inventory['candidates']:
        if candidate['address'] in selected|existing:continue
        if any(any(c.startswith('pool_relative:') for c in match['categories']) for match in candidate['matches']):
            remaining.append(candidate)
    negative_sites=[]
    for candidate in negative['matches']:
        if candidate['kind']!='memory_displacement':continue
        address=int(candidate['address'],16);ins=at(address)
        if ins.bytes.hex()!=candidate['bytes']:raise ValueError('Negative candidate changed')
        if address==0x18018b658:
            kind='exclude-stack-frame';reason='RSP-relative frame-pointer establishment before stack-probe/allocation; not an object-derived address.'
            proof=[raw(i) for i in span(0x18018b650,0x18018b66d)]
        elif address==0x1806e4fa8:
            reviewed=next(x for x in low['sites'] if x['address']==candidate['address'])
            kind=reviewed['classification'];reason=reviewed['reason'];proof=reviewed
        else:
            proof_span=span(0x1809e1fcd,address+ins.size)
            if proof_span[0].mnemonic!='lea' or proof_span[0].op_str!='rdx, [rip + 0x4d502c]':raise ValueError('Numeric-table anchor differs')
            if any(i.mnemonic.startswith('j') or i.mnemonic=='call' or any(i.reg_name(r)=='rdx' for r in i.regs_access()[1]) for i in proof_span[1:]):
                raise ValueError('Numeric-table anchor is not stable')
            kind='exclude-static-numeric-table';reason='RDX comes from RIP-relative LEA0x180eb7000; scalar index registers come from SIMD bit extraction. The negative bias addresses a static numeric table, not Mission.'
            proof=[raw(i) for i in proof_span]
        negative_sites.append(dict(candidate,classification=kind,reason=reason,proof=proof))
    exclusions=[]
    image_fragment=next(row.struct for row in pe.DIRECTORY_ENTRY_EXCEPTION if row.struct.BeginAddress==0x689a91)
    image_body=span(base+image_fragment.BeginAddress,base+image_fragment.EndAddress)
    image_switch=span(0x180689a87,0x180689abc)
    if at(0x180689a87).op_str!='r11d, 0x12' or at(0x180689a8b).mnemonic!='ja' or at(0x180689aaf).op_str!='ecx, dword ptr [rcx + r11*4 + 0x68bb10]':
        raise ValueError('Image-fragment switch dispatch changed')
    image_switch_bytes=pe.get_data(0x68bb10,19*4)
    image_switch_targets=[base+x for x in struct.unpack('<19I',image_switch_bytes)]
    for definition,address in ((0x18068a776,0x18068a77d),(0x18068a836,0x18068a843),(0x18068b062,0x18068b06c)):
        ins=at(address);proof=span(definition,address+ins.size);anchor=proof[0]
        if (anchor.mnemonic!='lea' or anchor.op_str.split(',')[0]!='r8' or anchor.operands[1].mem.base!=capstone.x86.X86_REG_RIP or
                anchor.address+anchor.size+anchor.operands[1].mem.disp!=base or
                ins.mnemonic!='mov' or ins.reg_name(ins.operands[1].mem.base)!='r8' or ins.operands[1].mem.disp!=0xda29b0):
            raise ValueError('Exact indexed image-global load differs')
        if any(i.mnemonic=='call' or i.group(capstone.CS_GRP_JUMP) or any(i.reg_name(r) in ('r8','r8d','r8w','r8b') for r in i.regs_access()[1]) for i in proof[1:-1]):
            raise ValueError('Image-base anchor does not survive to load')
        if any(i.group(capstone.CS_GRP_JUMP) and ((i.operands[0].type==capstone.x86.X86_OP_IMM and definition<i.operands[0].imm<=address) or
                (i.operands[0].type!=capstone.x86.X86_OP_IMM and i.address!=0x180689aba)) for i in image_body) or any(definition<t<=address for t in image_switch_targets):
            raise ValueError('A fragment branch may bypass the image-base anchor')
        exclusions.append(dict(raw(ins),classification='exclude-image-base-indexed-global',evidenceIds=[],
            proofInstructions=[raw(i) for i in proof],resolvedTableAddress=hex(base+0xda29b0),
            branchCheckedFragment=[hex(base+image_fragment.BeginAddress),hex(base+image_fragment.EndAddress)],
            indirectSwitchProof=dict(instructions=[raw(i) for i in image_switch],tableRva='0x68bb10',tableBytes=image_switch_bytes.hex(),targets=[hex(t) for t in image_switch_targets]),
            reason='Exact RIP-relative LEA establishesR8=imagebase; only non-GPR XORPS instructions can intervene. Operand is an indexed global table RVA, not a Mission-relative address.'))
    deque_ins=at(0x1806e4f95)
    deque_review=next(x for x in low['sites'] if x['address']=='0x1806e4fa8')
    if deque_ins.op_str!='rax, [rcx + 0x1000000]':raise ValueError('Deque quotient bias differs')
    exclusions.append(dict(raw(deque_ins),classification='exclude-deque-index-arithmetic',
        evidenceIds=deque_review.get('evidenceIds',[]),pairedNegativeOperandReview=deque_review,
        proofInstructions=[raw(i) for i in span(0x1806e4f70,0x1806e4fb3)],
        reason='Integer quotient/remainder sequence: add0x1000000, signed divide by8, multiplyby8 and subtract0x1000000 atpaired6e4fa8. RCX is a computed deque index; neither LEA dereferences a Mission object.'))
    for address,start,end,procedure,reason in (
        (0x1804965ae,0x180496595,0x1804965de,0x180496414,
         'SIMD reduction threshold2041 for a separately allocated2048-element uint32 workbuffer (8192bytes). Vector loop begins1 and consumes8elements until2041, then scalar tail; no Agent pool/sentinel role.'),
        *[(a,0x1807e83ee,0x1807e841b,0x1807e8170,
           'One of sixteen unrolled float-buffer positions2047..2032 minus input count in a2048-element processing window. This is an8192-byte float-buffer addressing schedule, not Agent capacity or sentinel.')
          for a in (0x1807e83ee,0x1807e83fd,0x1807e840c)]):
        ins=at(address)
        if ins.mnemonic!='mov' or ins.operands[1].imm not in (2039,2040,2041):raise ValueError('Nonpool numeric constant differs')
        refs=pseudo_refs(procedure)
        if not refs:raise ValueError('Missing retained nonpool constant proof')
        exclusions.append(dict(raw(ins),classification='exclude-unrelated-2048-element-buffer',evidenceIds=refs,
            proofInstructions=[raw(i) for i in span(start,end)],reason=reason))
    # Uncovered leaf switch tables still use real image-base anchored code.
    for start,address,table,count,register in ((0x18064365e,0x18064366e,0x6436e0,16,'r8'),
                                              (0x180646770,0x180646783,0x646ae0,111,'r9')):
        proof=span(start,address+13)
        ins=at(address)
        anchor=next(i for i in proof if i.mnemonic=='lea')
        if (anchor.reg_name(anchor.operands[0].reg)!=register or anchor.operands[1].mem.base!=capstone.x86.X86_REG_RIP or
            anchor.address+anchor.size+anchor.operands[1].mem.disp!=base or
            ins.operands[1].mem.disp!=table or ins.reg_name(ins.operands[1].mem.base)!=register or
            proof[0].mnemonic!='cmp' or proof[0].operands[1].imm!=count-1 or proof[1].mnemonic!='ja'):
            raise ValueError('Uncovered switch-table image-base proof differs')
        table_bytes=pe.get_data(table,count*4)
        targets=struct.unpack('<'+'I'*count,table_bytes)
        if any(not (address-base < value < table) for value in targets):
            raise ValueError('Switch destination leaves reviewed bounded leaf body')
        exclusions.append(dict(raw(ins),classification='exclude-image-base-switch-table',evidenceIds=[],
            proofInstructions=[raw(i) for i in proof],tableRva=hex(table),tableBytes=table_bytes.hex(),
            tableEntryCount=count,targetAddresses=[hex(base+t) for t in targets],
            reason='Immediate preceding RIP-relative LEA resolves the exact image base. The unsigned index bound selects RVA32 entries, then adds that base and jumps; this displacement is a static switch table RVA, not Mission.'))
    # Inline vectors deliberately keep2040 inline pointers and spill dynamically.
    for address in (0x1805d52fc,0x1805d533d,0x1805d5381):
        ins=at(address)
        if ins.mnemonic!='cmp' or ins.operands[1].imm!=2040:raise ValueError('Spill threshold differs')
        exclusions.append(dict(raw(ins),classification='exclude-inline-vector-spill-threshold',evidenceIds=pseudo_refs(0x1805d52c4),
            reason='Each comparison gates conversion through5d9000 to a dynamic vector before063870 append. Inline vector storage is deliberately not expanded; threshold2040 remains correct.'))
    for owner in high['ehOwnerProof']:
        fi_rva=int(owner['funcInfoRva'],16)
        if pe.get_data(fi_rva,40).hex()!=owner['funcInfoBytes']:raise ValueError('Separate-object EH identity differs')
        fields=struct.unpack('<10I',pe.get_data(fi_rva,40))
        targets=(0x180a0d197,0x180a0d1f7) if owner['ownerParentFunction']=='0x18030eae0' else (0x180a0d2c7,0x180a0d327)
        for address in targets:
            action=address-7
            rows=[]
            for state in range(fields[1]):
                rva=fields[2]+8*state
                destination,cleanup=struct.unpack('<iI',pe.get_data(rva,8))
                if cleanup+base==action:rows.append({'state':state,'toState':destination,'rva':hex(rva),'bytes':pe.get_data(rva,8).hex()})
            if not rows:raise ValueError('Cleanup is not associated with separate-object owner')
            load,ins,jump=list(md.disasm(pe.get_data(action-base,24),action))[:3]
            if load.mnemonic!='mov' or load.op_str!='rcx, qword ptr [rdx + '+owner['savedObjectStackOffset']+']' or ins.address!=address or ins.mnemonic!='add' or ins.operands[1].imm not in (0x8106a0,0x810740):
                raise ValueError('Separate-object cleanup receiver differs')
            exclusions.append(dict(raw(ins),classification='exclude-separate-object-eh',ownerProof=owner,mapRows=rows,
                proofInstructions=[raw(x) for x in (load,ins,jump)],evidenceIds=[],
                reason='MSVC unwind map associates this saved-object field adjustment with separately allocated8107b0-byte object owned through+628, not Mission. Same owner proof as neighboring8106c0 cleanup.'))
    result={'schemaVersion':1,'targetSha256':source_hash,'status':'bounded-interior-reference-review','gameExecuted':False,
        'sources':[{'path':str(p),'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in paths],
        'instructionSiteMappings':mappings,'instructionSiteExclusions':exclusions,'remainingPoolRelativeCandidates':remaining,
        'remainingLargeImmediateAddressArithmeticCandidates':[c for c in inventory['candidates']
            if c['address'] not in selected|existing|{x['address'] for x in manifest['reviewedExclusions']}|{x['address'] for x in exclusions}
            and c['mnemonic'] in ('add','sub') and any(m['kind']=='immediate' and 'mission_later_inline_address_range' in m['categories'] for m in c['matches'])],
        'coverageSnapshotNote':'Remaining candidate lists are a convenience snapshot against the current blocked manifest; exact-site proof dependencies are separately hashed. Snapshot counts are not semantic proof or patch authority.',
        'negativeMemoryReview':negative_sites,'negativeImmediateSummary':{'candidates':len(negative['matches'])-len(negative_sites),
            'status':'not-all-semantically-classified','limitation':'AND/TEST/CMP/MOV immediates include masks, colors and constants. No ADD/SUB immediate in this scanned negative range; this does not exclude smaller split constants or dynamically computed offsets.'},
        'sharedHelperClosure':{'listedEntries':len(helper['entries']),'unclassifiedListedEntries':0,'defaultConstructionPublishesSecondPool':False,'proofs':['native-pool-helper-caller-review.json','native-pool-helper-escape-review.json']},
        'limitations':['Pool-relative review and computed supplements are exact-site proofs. Listed shared helper entries are separately closed; arbitrary split/computed code addresses remain outside the bounded search.',
            'Second c60 pool constructor/destructor geometry and default-construction non-publication are reviewed. Its gameplay purpose is not named; absence of direct gameplay matches alone is not a disuse proof.',
            'Negative-range search does not discover arbitrary split immediates or opaque register-computed offsets. These are explicit restricted-profile search limits, not blanket patch authorization.'],
        'summary':{'mappedOperands':len(mappings),'poolRelativeOperands':17,'computedMissionOffsetOperands':2,'remainingPoolRelativeCandidates':len(remaining),'negativeMemoryCandidatesReviewed':len(negative_sites),'inlineSpillThresholdExclusions':3,'separateObjectEhExclusions':4,'uncoveredSwitchTableExclusions':2,'indexedImageGlobalExclusions':3,'positiveDequeBiasExclusions':1,'unrelated2048ElementBufferExclusions':4}}
    output=folder/'native-pool-interior-review.json'
    safe_review_output(output,[source_path]+paths).write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(result['summary']))


if __name__=='__main__':build()
