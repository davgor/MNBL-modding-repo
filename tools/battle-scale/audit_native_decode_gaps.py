"""Audit retained decode failures and local split constants without target execution.

This writes a separate evidence artifact; it does not modify the accepted patch.
"""
from __future__ import annotations
import argparse
from collections import Counter, defaultdict, deque
import hashlib
import json
from pathlib import Path
import struct
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "build/battle-scale/python-libs"))
import capstone
import pefile
from capstone.x86 import X86_OP_REG, X86_OP_MEM, X86_OP_IMM, X86_REG_RIP
from capstone import CS_GRP_JUMP, CS_GRP_RET
import classify_pool_references as flow
from patch_native_copy import safe_review_output

EXPECTED = "2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a"


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def decode(decoder, pe, base, start, end):
    """Retain failures, resynchronization, and exact instruction bytes separately."""
    block = pe.get_data(start, end-start)
    cursor, instructions, bad = 0, {}, []
    while cursor < len(block):
        for ins in decoder.disasm(block[cursor:], base+start+cursor):
            instructions[ins.address] = ins
            cursor = ins.address-base-start+ins.size
        if cursor < len(block):
            bad.append(base+start+cursor)
            cursor += 1
    return instructions, bad


def switch_candidates(instructions, pe, base, ranges, deferred_width_guards=frozenset()):
    """Recognize only adjacent CMP/JA/(byte map)/RVA load/ADD/JMP templates."""
    xs = sorted(instructions.values(), key=lambda x:x.address)
    result = {}
    for n, jump in enumerate(xs):
        if jump.mnemonic != 'jmp' or len(jump.operands)!=1 or jump.operands[0].type!=X86_OP_REG or n<4:
            continue
        add, load = xs[n-1], xs[n-2]
        if (add.mnemonic!='add' or len(add.operands)!=2 or any(o.type!=X86_OP_REG for o in add.operands)
                or load.mnemonic not in ('mov','movsxd') or len(load.operands)!=2
                or load.operands[0].type!=X86_OP_REG or load.operands[1].type!=X86_OP_MEM):
            continue
        dest=flow.register(jump,jump.operands[0].reg)
        mem=load.operands[1].mem
        br=flow.register(load,mem.base); ix=flow.register(load,mem.index)
        if (mem.scale!=4 or not br or not ix or load.operands[1].size!=4
                or flow.register(add,add.operands[0].reg)!=dest
                or flow.register(load,load.operands[0].reg)!=dest
                or flow.register(add,add.operands[1].reg)!=br):
            continue
        first=n-2; mapping=None
        prev=xs[n-3]
        if (prev.mnemonic=='movzx' and len(prev.operands)==2 and prev.operands[0].type==X86_OP_REG
                and prev.operands[1].type==X86_OP_MEM and prev.operands[1].size==1
                and flow.register(prev,prev.operands[0].reg)==ix):
            pm=prev.operands[1].mem
            if pm.scale!=1 or {flow.register(prev,pm.base),flow.register(prev,pm.index)}!={br,ix}:continue
            mapping=prev;first-=1
        guard_index=first-1;guard_register=ix;widened_index=False
        # MSVC may widen the bounded index and materialize image base between
        # the guard and lookup. Only these exact side-effect-free forms qualify.
        while guard_index>=1 and first-guard_index<=4:
            between=xs[guard_index];ops=between.operands
            if between.mnemonic=='cdqe' and guard_register=='rax':widened_index=True;guard_index-=1;continue
            if (between.mnemonic in ('mov','movsxd') and len(ops)==2 and all(o.type==X86_OP_REG for o in ops)
                    and flow.register(between,ops[0].reg)==guard_register and ops[0].size in (4,8)):
                widened_index|=ops[1].size==4 or ops[0].size==4
                guard_register=flow.register(between,ops[1].reg);guard_index-=1;continue
            if (between.mnemonic=='lea' and len(ops)==2 and ops[0].type==X86_OP_REG and ops[1].type==X86_OP_MEM
                    and flow.register(between,ops[0].reg)==br and ops[1].mem.base==X86_REG_RIP
                    and between.address+between.size+ops[1].mem.disp==base):
                guard_index-=1;continue
            break
        if guard_index<1:continue
        compare,guard=xs[guard_index-1:guard_index+1]
        if (compare.mnemonic!='cmp' or len(compare.operands)!=2 or compare.operands[0].type!=X86_OP_REG
                or compare.operands[0].size not in (4,8) or compare.operands[1].type!=X86_OP_IMM
                or flow.register(compare,compare.operands[0].reg)!=guard_register or guard.mnemonic!='ja'
                or guard.operands[0].type!=X86_OP_IMM):continue
        bound=compare.operands[1].imm
        if not 0<=bound<=65535:continue
        if compare.operands[0].size==4 and not widened_index:
            zero_extended=False
            for previous in reversed(xs[max(0,guard_index-21):guard_index-1]):
                if previous.mnemonic=='call' or previous.group(CS_GRP_JUMP):break
                if guard_register in {flow.register(previous,r) for r in previous.regs_access()[1]}:
                    po=previous.operands
                    zero_extended=bool(po and po[0].type==X86_OP_REG and flow.register(previous,po[0].reg)==guard_register
                                       and (po[0].size==4 or (previous.mnemonic=='movzx' and po[1].size<=2)))
                    break
            if not zero_extended and (compare.address,guard_register) not in deferred_width_guards:continue
        span=xs[guard_index-1:n+1]
        if any(a.address+a.size!=b.address for a,b in zip(span,span[1:])):continue
        if any(compare.address < z.operands[0].imm <=jump.address for z in xs
               if z.group(CS_GRP_JUMP) and z.operands and z.operands[0].type==X86_OP_IMM):
            continue
        mapbytes=pe.get_data(mapping.operands[1].mem.disp,bound+1) if mapping else None
        if mapping and len(mapbytes)!=bound+1:continue
        count=max(mapbytes)+1 if mapping else bound+1
        table=pe.get_data(mem.disp,count*4)
        if len(table)!=count*4:continue
        targets=[base+struct.unpack_from('<I',table,4*i)[0] for i in range(count)]
        if not all(any(base+a<=t<base+b for a,b in ranges) for t in targets):continue
        intervals=[(base+mem.disp,base+mem.disp+count*4,'rva-jump-target-table',table.hex())]
        if mapping:intervals.append((base+mapping.operands[1].mem.disp,base+mapping.operands[1].mem.disp+len(mapbytes),'compressed-switch-index-table',mapbytes.hex()))
        result[jump.address]={'baseRegister':br,'baseLoadAddress':load.address,'targets':targets,'intervals':intervals,
                              'instructions':[flow.basic(i) for i in span],'guardBound':bound,
                              'guardAddress':hex(compare.address),'guardRegister':guard_register,
                              'deferredWidthProofRequired':(compare.address,guard_register) in deferred_width_guards}
    return result


def prove_guard_index(instructions, root, guard_address, register, switches, ranges):
    """Prove a 32-bit compare bounds the full index on every modeled path.

    Only zero-extended 32-bit values or signed extensions from 32 bits qualify.
    The latter qualify because unsigned CMP/JA against a nonnegative bound
    below 2**31 rejects every negative signed value. Calls preserve only the
    Win64 nonvolatile registers. Every partial/other register write kills the
    fact. Unknown indirect jumps or reachable decode holes reject the proof.

    Switch edges are provisional here. Together with the independent image-
    base must-analysis they establish an inductive dispatch invariant: before
    a hypothetical first invalid dispatch, every previous dispatch follows
    exactly these table edges, so the invariant also holds at that dispatch.
    Both width and base obligations must pass before either table is accepted.
    """
    compare=instructions.get(guard_address)
    if (compare is None or compare.mnemonic!='cmp' or len(compare.operands)!=2
            or compare.operands[0].type!=X86_OP_REG or compare.operands[0].size!=4
            or flow.register(compare,compare.operands[0].reg)!=register
            or compare.operands[1].type!=X86_OP_IMM or not 0<=compare.operands[1].imm<0x80000000):
        return {'accepted':False,'reason':'unsupported unsigned guard'}
    # None means no width fact; a frozenset records all reaching establishing writes.
    states={root:None};todo=deque([root]);pending={root};missing=set();unknown=set();steps=0
    nonvolatile={'rbx','rbp','rsi','rdi','r12','r13','r14','r15'}
    while todo and steps<1000000:
        a=todo.popleft();pending.discard(a);steps+=1
        if a not in instructions:missing.add(a);continue
        ins=instructions[a];after=states[a];ops=ins.operands
        writes={flow.register(ins,r) for r in ins.regs_access()[1]}
        if register in writes:
            after=None
            if ops and ops[0].type==X86_OP_REG and flow.register(ins,ops[0].reg)==register:
                if ((ops[0].size==4 and ins.mnemonic in ('mov','movzx','movsxd'))
                        or (ins.mnemonic=='movsxd' and len(ops)==2 and ops[0].size==8 and ops[1].size==4)):
                    after=frozenset([a])
        if ins.mnemonic=='call' and register not in nonvolatile:after=None
        following=a+ins.size
        if ins.group(CS_GRP_RET) or ins.mnemonic in ('int','int3','ud2','hlt'):nexts=[]
        elif ins.group(CS_GRP_JUMP):
            if ops[0].type==X86_OP_IMM:nexts=[ops[0].imm]+([] if ins.mnemonic=='jmp' else [following])
            elif a in switches:nexts=switches[a]['targets']
            else:unknown.add(a);nexts=[]
        else:nexts=[following]
        for target in nexts:
            if target not in instructions:
                if any(lo<=target<hi for lo,hi in ranges):missing.add(target)
                continue
            old=states.get(target)
            merged=after if target not in states else (old|after if old is not None and after is not None else None)
            if target not in states or old!=merged:
                states[target]=merged
                if target not in pending:todo.append(target);pending.add(target)
    fact=states.get(guard_address)
    accepted=not todo and not missing and not unknown and fact is not None
    return {'accepted':accepted,'guardAddress':hex(guard_address),'register':register,
            'guardInstruction':flow.basic(compare),'establishingInstructions':[flow.basic(instructions[a]) for a in sorted(fact or [])],
            'reachableInstructions':len(states),'flowSteps':steps,'converged':not todo,
            'missingTargets':[hex(a) for a in sorted(missing)],'unknownIndirectDispatches':[hex(a) for a in sorted(unknown)],
            'semantics':'Unsigned 32-bit bound below 2^31 plus reaching zero/sign extension implies the same nonnegative full 64-bit index; Win64 nonvolatile preservation only.'}


def image_base_flow(instructions, root, switches, ranges):
    """Must-analysis; an unmodeled indirect jump conservatively reaches all boundaries."""
    states={root:{}}; todo=deque([root]); pending={root}; steps=0;missing_internal=set()
    while todo and steps<1000000:
        a=todo.popleft();pending.discard(a);steps+=1
        if a not in instructions:continue
        ins=instructions[a];after=flow.transfer(ins,states[a]);following=a+ins.size
        if ins.group(CS_GRP_RET) or ins.mnemonic in ('int','int3','ud2','hlt'):nexts=[]
        elif ins.group(CS_GRP_JUMP):
            op=ins.operands[0]
            if op.type==X86_OP_IMM:nexts=[op.imm]+([] if ins.mnemonic=='jmp' else [following])
            else:nexts=switches[a]['targets'] if a in switches else list(instructions)
        else:nexts=[following]
        for target in nexts:
            if target not in instructions:
                if any(lo<=target<hi for lo,hi in ranges):missing_internal.add(target)
                continue
            merged=flow.join(states.get(target),after)
            if states.get(target)!=merged:
                states[target]=merged
                if target not in pending:todo.append(target);pending.add(target)
    return states,not todo and not missing_internal,steps,missing_internal


def split_offset_scan(pe, decoder, base, inventory_addresses, patched):
    """Discover local affine constructions; unknown receivers are never approved.

    All facts reset at branches, calls, returns and decode failures. Results
    require at least two distinct nonzero constant contributions, preventing
    this from just repeating the original direct-displacement inventory.
    """
    candidates={};covered=0;instruction_count=0
    for section in pe.sections:
        if not section.Characteristics & 0x20000000:continue
        block=section.get_data();cursor=0;state={}
        while cursor<len(block):
            before=cursor
            for ins in decoder.disasm(block[cursor:],base+section.VirtualAddress+cursor):
                instruction_count+=1;cursor=ins.address-base-section.VirtualAddress+ins.size
                ops=ins.operands;prior=dict(state)
                def regvalue(number):
                    name=flow.register(ins,number)
                    return prior.get(name,(0,('unknown-register',name),{})) if name else (0,None,{})
                def combine(left,right):
                    if left[1] is not None and right[1] is not None:return None
                    return (left[0]+right[0],left[1] or right[1],{**left[2],**right[2]})
                def memory(mem):
                    if mem.base==X86_REG_RIP:return None
                    left=regvalue(mem.base) if mem.base else (0,None,{})
                    right=regvalue(mem.index) if mem.index else (0,None,{})
                    if right[1] is not None and mem.scale!=1:return None
                    right=(right[0]*mem.scale,right[1],right[2])
                    out=combine(left,right)
                    if out is None:return None
                    defs=dict(out[2])
                    if mem.disp:defs[ins.address]=flow.basic(ins)
                    return (out[0]+mem.disp,out[1],defs)
                def consider(value,kind):
                    nonlocal covered
                    if value is None or value[1] is None or not 0x62ca50<=value[0]<=0x1184b40 or len(value[2])<2:return
                    if ins.address in inventory_addresses or any(a in patched for a in value[2]):covered+=1;return
                    candidates[(ins.address,kind)]={'address':hex(ins.address),'instruction':flow.basic(ins),'computedBias':hex(value[0]),
                        'kind':kind,'ownerStatus':'unproved-symbolic-register','constantContributions':list(value[2].values()),
                        'action':'inspect owner and relocation placement; not an approved patch'}
                for op in ops:
                    if op.type==X86_OP_MEM:consider(memory(op.mem),'computed-memory-address')
                try:
                    for written in ins.regs_access()[1]:state.pop(flow.register(ins,written),None)
                except capstone.CsError:state.clear()
                if ins.mnemonic=='call' or ins.group(CS_GRP_JUMP) or ins.group(CS_GRP_RET):state.clear();continue
                if not ops or ops[0].type!=X86_OP_REG or ops[0].size not in (4,8):continue
                dest=flow.register(ins,ops[0].reg);value=None
                if len(ops)==2 and ins.mnemonic in ('mov','movabs'):
                    if ops[1].type==X86_OP_IMM:value=(ops[1].imm,None,{ins.address:flow.basic(ins)} if ops[1].imm else {})
                    elif ops[1].type==X86_OP_REG:value=regvalue(ops[1].reg)
                elif len(ops)==2 and ins.mnemonic=='lea' and ops[0].size==8 and ops[1].type==X86_OP_MEM:value=memory(ops[1].mem)
                elif len(ops)==2 and ins.mnemonic in ('add','sub') and ops[0].size==8:
                    left=regvalue(ops[0].reg)
                    if ops[1].type==X86_OP_IMM:
                        delta=ops[1].imm*(1 if ins.mnemonic=='add' else -1)
                        value=(left[0]+delta,left[1],{**left[2],**({ins.address:flow.basic(ins)} if delta else {})})
                    elif ops[1].type==X86_OP_REG and ins.mnemonic=='add':value=combine(left,regvalue(ops[1].reg))
                elif len(ops)==2 and ins.mnemonic=='shl' and ops[1].type==X86_OP_IMM:
                    left=regvalue(ops[0].reg)
                    if left[1] is None:value=(left[0]<<ops[1].imm,None,{**left[2],ins.address:flow.basic(ins)})
                if value is not None:
                    if ops[0].size==4:
                        if value[1] is not None:continue
                        value=(value[0]&0xffffffff,None,value[2])
                    state[dest]=value;consider(value,'computed-register-address')
            if cursor<len(block):cursor+=1;state.clear()
            if cursor<=before:raise ValueError('Decoder made no progress')
    return {'status':'bounded-local-discovery-only','decodedInstructions':instruction_count,'alreadyInventoriedOrRelocatedObservations':covered,
            'newUnprovedCandidates':list(candidates.values()),
            'limitations':['No across-block/call affine facts, memory-loaded constants, multiplicative variable expressions, or arbitrary alias inference.','An unproved symbolic receiver is a discovery candidate, not evidence that the expression refers to Mission.']}


def audit(output, source, manifest_path):
    inventory_path=ROOT/'evidence/engine-extension/native-pool-reference-inventory.json'
    mid_path=ROOT/'evidence/engine-extension/pool-semantic-review-mid.json'
    inv=json.loads(inventory_path.read_text());installed=Path(inv['binary']['path']);native=source.resolve()
    if native==installed.resolve():raise ValueError('Use a hash-matched offline original copy; do not inspect the live installed DLL')
    source_bytes=native.read_bytes()
    if hashlib.sha256(source_bytes).hexdigest()!=EXPECTED or inv['binary']['sha256']!=EXPECTED:raise ValueError('Native source identity mismatch')
    # PE(data=...) retains no file mapping or open source handle during analysis.
    pe=pefile.PE(data=source_bytes);base=pe.OPTIONAL_HEADER.ImageBase
    decoder=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);decoder.detail=True
    entries={e.struct.BeginAddress:e.struct for e in pe.DIRECTORY_ENTRY_EXCEPTION}
    parents={}
    for start,e in entries.items():
        head=pe.get_data(e.UnwindData,4)
        if head[0]>>3&4:
            tail=e.UnwindData+4+((head[2]+1)&~1)*2
            parent,end,unwind=struct.unpack('<III',pe.get_data(tail,12))
            if parent not in entries or (entries[parent].EndAddress,entries[parent].UnwindData)!=(end,unwind):raise ValueError('Unwind chain mismatch')
            parents[start]=parent
    def canonical(start):
        seen=set()
        while start in parents:
            if start in seen:raise ValueError('Unwind cycle')
            seen.add(start);start=parents[start]
        return start
    groups=defaultdict(list)
    for start,e in entries.items():groups[canonical(start)].append((start,e.EndAddress))
    gaps=inv['coverage']['undecodableBytes'];byroot=defaultdict(list)
    for gap in gaps:
        addr=int(gap['rva'],16)
        if pe.get_data(addr,1)!=bytes([int(gap['byte'],16)]):raise ValueError('Retained undecoded byte mismatch')
        byroot[canonical(int(gap['functionStart'],16)-base) if gap['functionStart'] else None].append(gap)
    # Some compiler lookup tables sit immediately outside the .pdata range.
    # Audit their predecessor body as a candidate, but require an actual table
    # reference proof before assigning any of those uncovered bytes to it.
    none_gaps=byroot.pop(None,[])
    end_to_start={e.EndAddress:start for start,e in entries.items()}
    for region in inv['coverage']['uncoveredExecutableRanges']:
        lo,hi=int(region['rvaStart'],16),int(region['rvaEndExclusive'],16)
        if lo in end_to_start and any(lo<=int(g['rva'],16)<hi for g in none_gaps):
            root=canonical(end_to_start[lo]);byroot.setdefault(root,[])
            groups[root].append((lo,hi))
    records=[];tables=[];group_reports=[];all_intervals=[];width_proofs=[]
    retained=json.loads(mid_path.read_text())
    if retained['targetSha256']!=EXPECTED:raise ValueError('Retained switch identity mismatch')
    retained_tables=retained['missionMethodFlowReview']['reviewedSwitchTables']
    for reviewed in retained_tables:
        lo=int(reviewed['tableStart'],16);raw=bytes.fromhex(reviewed['tableHex'])
        guard=int(reviewed['guardStart'],16);code=bytes.fromhex(reviewed['dispatchHex'])
        if pe.get_data(lo-base,len(raw))!=raw or pe.get_data(guard-base,len(code))!=code:raise ValueError('Retained switch bytes mismatch')
        all_intervals.append((lo,lo+len(raw),'retained-reviewed-rva-jump-table',int(reviewed['dispatchAddress'],16)))
    manifest=json.loads(manifest_path.read_text())
    if manifest['sourceSha256']!=EXPECTED:raise ValueError('Manifest target identity mismatch')
    for patch in manifest['patches']:
        original=bytes.fromhex(patch['original'])
        if pe.get_data(int(patch['rva'],16),len(original))!=original:raise ValueError('Manifest original patch bytes mismatch')
    patched={int(p['address'],16) for p in manifest['patches']}
    for root,rootgaps in sorted(byroot.items(),key=lambda x:x[0] or 0):
        insns={};bad=[]
        for start,end in sorted(groups[root]):
            decoded,failures=decode(decoder,pe,base,start,end);insns.update(decoded);bad.extend(failures)
        # This bounded exception has a separate all-predecessor width proof;
        # no other procedure receives the provisional width relaxation.
        deferred={(base+0x6f29d0,'r15')} if root==0x6f2770 else set()
        switches=switch_candidates(insns,pe,base,groups[root],deferred)
        width_ok=True
        if deferred:
            for guard,reg in sorted(deferred):
                proof=prove_guard_index(insns,base+root,guard,reg,switches,[(base+s,base+e) for s,e in groups[root]])
                proof['procedure']=hex(base+root)
                width_proofs.append(proof);width_ok &= proof['accepted']
        states,converged,steps,missing=image_base_flow(insns,base+root,switches,[(base+s,base+e) for s,e in groups[root]])
        joint_base_ok=all(states.get(s['baseLoadAddress'],{}).get(s['baseRegister'],(None,))[0]==base for s in switches.values())
        if deferred:
            # Every provisional table edge must be justified, not only the
            # table whose output interval happens to contain a decode gap.
            width_ok &= joint_base_ok and converged
            width_proofs[-1]['allProvisionalImageBasesProven']=joint_base_ok and converged
            width_proofs[-1]['jointlyAccepted']=bool(width_ok)
            width_proofs[-1]['exactProcedureCode']={'start':hex(base+root),'endExclusive':hex(base+0x6f33f4),
                'bytes':pe.get_data(root,0x6f33f4-root).hex()}
            proof_spans=[]
            for entry in width_proofs[-1]['establishingInstructions']:
                proof_spans.append((int(entry['address'],16),len(bytes.fromhex(entry['bytes']))))
            for sw in switches.values():
                for entry in sw['instructions']:
                    proof_spans.append((int(entry['address'],16),len(bytes.fromhex(entry['bytes']))))
                for lo,hi,_,_ in sw['intervals']:proof_spans.append((lo,hi-lo))
                fact=states.get(sw['baseLoadAddress'],{}).get(sw['baseRegister'])
                if fact:
                    proof_spans.extend((a,insns[a].size) for a in fact[1])
            intersecting=[]
            for patch in manifest['patches']:
                lo=int(patch['address'],16);hi=lo+len(bytes.fromhex(patch['original']))
                if any(lo<a+size and a<hi for a,size in proof_spans):intersecting.append(patch['id'])
            width_proofs[-1]['frozenManifestProofSpanOverlaps']=intersecting
            width_proofs[-1]['proofSpansUnchangedByFrozenManifest']=not intersecting
            width_ok &= not intersecting
            width_proofs[-1]['jointlyAccepted']=bool(width_ok)
        accepted=list(all_intervals);local_accepted_switches=0
        for dispatch,sw in switches.items():
            fact=states.get(sw['baseLoadAddress'],{}).get(sw['baseRegister'])
            if not width_ok or not converged or fact is None or fact[0]!=base:continue
            local_accepted_switches+=1
            sw['baseDefinitionInstructions']=[flow.basic(insns[a]) for a in sorted(fact[1])]
            sw['procedure']=hex(base+root);sw['dispatchAddress']=hex(dispatch)
            sw['targets']=[hex(a) for a in sw['targets']]
            if deferred:
                sw['jointDispatchProof']='All deferred index-width obligations and image-base facts pass over the same provisional table-edge graph; first-invalid-dispatch induction.'
            for lo,hi,kind,raw in sw['intervals']:
                accepted.append((lo,hi,kind,dispatch))
            sw['intervals']=[{'start':hex(lo),'endExclusive':hex(hi),'kind':kind,'bytes':raw} for lo,hi,kind,raw in sw['intervals']]
            sw['baseLoadAddress']=hex(sw['baseLoadAddress']);tables.append(sw)
        all_intervals.extend(accepted[len(all_intervals):])
        overlap=[hex(a) for a in patched if any(base+s<=a<base+e for s,e in groups[root])]
        group_reports.append({'procedure':hex(base+root),'undecodedByteCount':len(rootgaps),'recognizedSwitches':len(switches),'acceptedSwitches':local_accepted_switches,
                              'constantFlowConverged':converged,'constantFlowSteps':steps,'patchedInstructionsInGroup':overlap})
        group_reports[-1]['unmodeledReachableInternalTargets']=[hex(a) for a in sorted(missing)]
        for gap in rootgaps:
            addr=base+int(gap['rva'],16);matches=[(kind,dispatch) for lo,hi,kind,dispatch in accepted if lo<=addr<hi]
            records.append({**gap,'canonicalProcedure':hex(base+root),'classification':matches[0][0] if matches else 'unresolved-runtime-range-byte',
                            'dispatchProofs':[hex(d) for _,d in matches], 'patchBlocker':None if matches else 'Reference/data classification remains open; not approved as executable or harmless data',
                            'patchedGroup':bool(overlap),'contextBytes':None if matches else pe.get_data(addr-base-16,48).hex()})
    for gap in none_gaps:
        addr=base+int(gap['rva'],16);matches=[(kind,dispatch) for lo,hi,kind,dispatch in all_intervals if lo<=addr<hi]
        records.append({**gap,'classification':matches[0][0] if matches else 'unresolved-uncovered-executable-byte',
                        'dispatchProofs':[hex(d) for _,d in matches], 'patchBlocker':None if matches else 'No function entry or referenced-data proof yet',
                        'contextBytes':None if matches else pe.get_data(addr-base-16,48).hex()})
    split=split_offset_scan(pe,decoder,base,{int(c['address'],16) for c in inv['candidates']},patched)
    excluded={r['address']:r for r in manifest['reviewedExclusions']}
    split['reviewedSeparateOwnerCandidates']=[]
    still_open=[]
    for candidate in split['newUnprovedCandidates']:
        matches=[excluded[c['address']] for c in candidate['constantContributions']
                 if c['address'] in excluded and excluded[c['address']].get('classification') in
                 ('exclude-separate-scene-resource','exclude-separate-object-field-arithmetic')]
        # The bounded affine chain keeps the same symbolic base; these exact
        # contributions already have a retained separate-object owner proof.
        if matches:
            candidate['ownerStatus']='retained-separate-scene-resource-owner'
            candidate['ownerEvidence']=matches;candidate['action']='leave unchanged; affine address remains within separately owned object'
            split['reviewedSeparateOwnerCandidates'].append(candidate)
        else:still_open.append(candidate)
    split['newUnprovedCandidates']=still_open
    patterns={}
    for patch in manifest['patches']:
        original=bytes.fromhex(patch['original'])
        for change in patch.get('operandChanges',[]):
            size=change.get('encodedSize',0);offset=change.get('encodedOffset',0)
            if size>=2:
                raw=original[offset:offset+size]
                if len(raw)==size:patterns.setdefault(raw,[]).append(patch['address'])
    window_matches={}
    for record in records:
        if record['patchBlocker'] is None:continue
        center=int(record['rva'],16);lo=max(0,center-14);data=pe.get_data(lo,29)
        for pattern,examples in patterns.items():
            position=data.find(pattern)
            while position>=0:
                key=(lo+position,pattern.hex())
                window_matches[key]={'rva':hex(lo+position),'bytes':pattern.hex(),'nearUndecodedByteRva':record['rva'],
                                     'knownPatchExamples':examples[:3],'status':'raw-byte candidate; not proof of an instruction'}
                position=data.find(pattern,position+1)
    result={'schemaVersion':1,'status':'static-decode-gap-audit-with-explicit-open-cases','targetSha256':EXPECTED,'gameExecuted':False,
            'sourceEvidence':[{'path':str(p),'sha256':sha(p)} for p in (native,inventory_path,manifest_path,mid_path,Path(__file__).resolve(),ROOT/'tools/battle-scale/classify_pool_references.py')],
            'summary':{'inventoryUndecodableBytes':len(gaps),'classifications':dict(Counter(r['classification'] for r in records)),
                       'unresolvedBytesInPatchedGroups':sum(bool(r.get('patchedGroup')) and r['patchBlocker'] is not None for r in records),
                       'newUnprovedSplitOffsetCandidates':len(split['newUnprovedCandidates']),
                       'reviewedSeparateOwnerSplitExpressions':len(split['reviewedSeparateOwnerCandidates']),
                       'changedOperandLiteralMatchesNearUnresolvedBytes':len(window_matches)},
            'method':'Exact source-pinned bounded switch templates and all-predecessor image-base constant propagation over chained runtime ranges. Unmodeled indirect jumps reach every decoded boundary. Byte records are classified only inside bounded referenced tables; other failures stay open.',
            'limitations':['Linear resynchronization is used to discover candidates, not to authorize a patch. This audit does not change accepted artifacts.','No claim that direct-call/control-flow enumeration proves absence of all computed code pointers.'],
            'switchProofs':tables,'guardIndexWidthProofs':width_proofs,'retainedExactSwitchProofs':retained_tables,'functionGroups':group_reports,'bytes':records,'splitOffsetDiscovery':split,
            'unresolvedWindowLiteralScan':{'window':'14 bytes before through14 bytes after each unresolved byte; x86 instruction maximum15 bytes',
                'patterns':'Every >=2-byte encoded original operand slice in the exact accepted patch; no single-byte literal completeness claim',
                'distinctPatterns':len(patterns),'matches':list(window_matches.values())}}
    output=safe_review_output(output,[installed,native,inventory_path,manifest_path,mid_path,Path(__file__).resolve(),ROOT/'tools/battle-scale/classify_pool_references.py'])
    output.write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(result['summary']))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output',type=Path,default=ROOT/'evidence/engine-extension/native-decode-gap-review.json')
    parser.add_argument('--source',type=Path,required=True,help='Offline original DLL or backup with the pinned source hash; installed DLL is refused')
    parser.add_argument('--manifest',type=Path,required=True,help='Frozen package manifest whose original patch operands are checked')
    args=parser.parse_args()
    audit(args.output,args.source,args.manifest.resolve())
