"""Conservative intraprocedural MAY-use inventory, never a hook approver.

Taint denotes arithmetic derived from one multiplied offset, not object type.
Calls, persistent stores, unknown aliases and unsupported operations remain
explicit exits/gaps. Stack addresses must come from an independent must analysis.
"""
from collections import deque
import capstone
from capstone.x86 import X86_OP_REG, X86_OP_MEM
from native_instruction_semantics import memory_operand_writes

VOLATILE={'rax','rcx','rdx','r8','r9','r10','r11',*(f'xmm{i}' for i in range(6))}
ARGS={'rcx','rdx','r8','r9',*(f'xmm{i}' for i in range(4))}
COPIES={'mov','movabs','movq','movsd','movaps','movups','movdqa','movdqu'}
ARITHMETIC={'add','sub','adc','sbb','and','or','xor','imul','mul','shl','shr','sar','sal','neg','inc','dec','not'}
FLAGS=('cf','of','zf','sf','af','pf')

def trace_uses(insns,succ,definition,canonical,stack_address):
    original_canonical=canonical
    def canonical(i,r):
        raw=i.reg_name(r) or ''
        # Inventory aliases conservatively; this is not vector-width semantics.
        if raw.startswith(('ymm','zmm')):return 'xmm'+raw[3:]
        return original_canonical(i,r) or raw
    first=insns[definition]
    if first.mnemonic!='imul' or first.operands[0].type!=X86_OP_REG:
        raise ValueError('Expected a multiply definition')
    seed={('r',canonical(first,first.operands[0].reg)),*(('f',f) for f in FLAGS)}
    states={};pending=deque();events={}
    def event(pc,kind,detail):events[(pc,kind,detail)]={'address':hex(pc),'kind':kind,'detail':detail}
    def merge(pc,value):
        if pc not in insns:
            event(pc,'external-control-flow','live result leaves the decoded body');return
        old=states.get(pc,set());joined=old|value
        if pc not in states or joined!=old:states[pc]=joined;pending.append(pc)
    for target in succ.get(definition,()):merge(target,seed)
    if not succ.get(definition):event(definition,'unresolved-definition-exit','no successor after multiply')
    steps=0
    while pending:
        pc=pending.popleft();ins=insns[pc];st=states[pc];out=set(st);steps+=1
        if steps>100000:raise RuntimeError('Use-flow convergence budget exceeded')
        if pc==definition:
            # Re-execution creates a new value from this same static site.
            # Other aliases from the earlier iteration still remain live.
            out.discard(('r',canonical(first,first.operands[0].reg)))
            out|=seed
            for target in succ.get(pc,()):merge(target,out)
            continue
        reads,writes=ins.regs_access()
        if st and any((ins.reg_name(r) or '').startswith(('ymm','zmm')) for r in [*reads,*writes]):
            event(pc,'unsupported-vector-width-gap','YMM/ZMM aliases tracked XMM; full lane semantics not certified')
        readregs={canonical(ins,r) for r in reads}
        taintedregs={name for kind,name in st if kind=='r'}
        readtaint=readregs&taintedregs
        for reg in writes:
            name=canonical(ins,reg)
            if name and name!='rflags':out.discard(('r',name))
        # Undefined IMUL flags are conservatively marked too. Later arithmetic
        # and comparisons derive all six flags; partial updates preserve others.
        flag_input=False
        for flag in FLAGS:
            upper=flag.upper()
            tested=getattr(capstone.x86,'X86_EFLAGS_TEST_'+upper)|getattr(capstone.x86,'X86_EFLAGS_PRIOR_'+upper)
            changed=sum(getattr(capstone.x86,'X86_EFLAGS_'+kind+'_'+upper) for kind in ('MODIFY','RESET','SET','UNDEFINED'))
            if ins.eflags&tested and ('f',flag) in st:
                event(pc,'flags-use',flag);flag_input=True
            if ins.eflags&changed:
                out.discard(('f',flag))
                if readtaint and ins.eflags&getattr(capstone.x86,'X86_EFLAGS_MODIFY_'+upper):out.add(('f',flag))
                if ins.eflags&getattr(capstone.x86,'X86_EFLAGS_UNDEFINED_'+upper) and ('f',flag) in st:out.add(('f',flag))
        if ins.group(capstone.CS_GRP_CALL):
            for reg in sorted(readtaint|taintedregs&ARGS):event(pc,'call-escape',reg)
            if any(kind=='s' for kind,_ in st):event(pc,'callee-stack-alias-gap','saved offset may escape or be overwritten by callee')
            if st:event(pc,'callee-result-survival-gap','ABI permits clobbering but does not prove any original value was overwritten')
            # A caller-saved register is MAY-clobbered, not MUST-clobbered.
            # Preserve possible old values; interprocedural effects stay a gap.
            out|=st
        elif ins.group(capstone.CS_GRP_RET):
            for reg in ('rax','xmm0'):
                if reg in taintedregs:event(pc,'return-escape',reg)
            # Nonvolatile taint at RET requires a correct restoration path;
            # this pass cannot turn it into an ABI proof.
            for reg in sorted(taintedregs-VOLATILE-{'rsp','rflags'}):event(pc,'nonvolatile-exit-gap',reg)
            if any(kind=='s' for kind,_ in st):event(pc,'return-stack-alias-gap','implicit return-address read not proven independent of saved values')
        else:
            ops=ins.operands;memory_regs=set();loaded=False
            for ix,op in enumerate(ops):
                if op.type!=X86_OP_MEM:continue
                used={canonical(ins,r) for r in (op.mem.base,op.mem.index) if r}
                memory_regs|=used
                if used&taintedregs:
                    event(pc,'address-computation' if ins.mnemonic=='lea' else
                          'memory-write-address' if memory_operand_writes(ins,ix) else 'memory-read-address',
                          ','.join(sorted(used&taintedregs)))
                offset=stack_address(pc,op)
                iswrite=memory_operand_writes(ins,ix)
                isread=bool(op.access&capstone.CS_AC_READ) and not (iswrite and ins.mnemonic in COPIES and ix==0)
                if offset is not None:
                    overlaps={key for key in st if key[0]=='s' and key[1]<offset+op.size and offset<key[1]+8}
                    if isread and overlaps:
                        loaded=True;event(pc,'stack-reload',str(offset))
                    if iswrite:
                        if ins.mnemonic in COPIES and op.size==8 and all(key[1]==offset for key in overlaps):out-=overlaps
                        elif overlaps:event(pc,'partial-stack-write-gap',str(offset))
                elif any(kind=='s' for kind,_ in st):
                    if iswrite:event(pc,'unknown-store-alias-gap','store may overlap saved offset')
                    if isread and ins.mnemonic!='lea':
                        loaded=True;event(pc,'unknown-load-alias-gap','load may retrieve a saved offset')
            if any(kind=='s' for kind,_ in st) and ins.mnemonic in {'pop','popf','popfd','popfq','leave','enter','iret','iretd','iretq'}:
                loaded=True;event(pc,'implicit-stack-read-gap',ins.mnemonic)
                if ins.mnemonic in {'popf','popfd','popfq','iret','iretd','iretq'}:out.update(('f',f) for f in FLAGS)
            explicit_data={canonical(ins,op.reg) for op in ops if op.type==X86_OP_REG and op.access&capstone.CS_AC_READ}
            dataregs=((readtaint-memory_regs)|(explicit_data&taintedregs))-{'rflags','rsp'}
            source_tainted=bool(dataregs or loaded or flag_input)
            # Memory and carry inputs are known only after operand inspection.
            # CMP [saved_offset],0 and ADC with a derived carry produce flags
            # even when no explicit GPR input carries the offset.
            if source_tainted:
                for flag in FLAGS:
                    if ins.eflags&getattr(capstone.x86,'X86_EFLAGS_MODIFY_'+flag.upper()):out.add(('f',flag))
            if flag_input and any(op.type==X86_OP_REG and op.access&capstone.CS_AC_WRITE for op in ops):
                event(pc,'flags-derived-value',ins.mnemonic)
            if len(ops)==2 and ops[0].type==X86_OP_REG and ins.mnemonic in COPIES:
                dest=canonical(ins,ops[0].reg)
                if source_tainted:out.add(('r',dest));event(pc,'copy',dest)
                if source_tainted and ops[0].size<8:event(pc,'narrow-value-use',str(ops[0].size))
                if ops[0].size<4 and dest in taintedregs:out.add(('r',dest))
            elif ins.mnemonic=='lea' and ops and ops[0].type==X86_OP_REG:
                if readtaint:out.add(('r',canonical(ins,ops[0].reg)))
            elif len(ops)==2 and ops[0].type==X86_OP_MEM and ins.mnemonic in COPIES:
                if source_tainted:
                    offset=stack_address(pc,ops[0])
                    if offset is not None and ops[0].size==8:
                        out.add(('s',offset));event(pc,'stack-spill',str(offset))
                    else:event(pc,'persistent-store-escape','non-private or partial offset store')
            elif ins.mnemonic in ('cmp','test'):
                if source_tainted:event(pc,'scalar-comparison',','.join(sorted(dataregs)))
            elif ins.mnemonic in ARITHMETIC:
                if readtaint or loaded or flag_input:
                    for reg in writes:
                        name=canonical(ins,reg)
                        if name and name!='rflags':out.add(('r',name))
                    event(pc,'arithmetic-use',ins.mnemonic)
            elif readtaint or loaded or flag_input:
                # Includes conditional moves, narrow conversions, PUSH/POP and
                # implicit/string operations until an exact handler is added.
                event(pc,'unsupported-value-use',ins.mnemonic)
                for reg in writes:
                    name=canonical(ins,reg)
                    if name and name!='rflags':out.add(('r',name))
            # A partial register write cannot erase tainted upper bytes even if
            # its new low byte/word is unrelated (SETcc, MOV AL, immediate...).
            for op in ops:
                if op.type==X86_OP_REG and op.access&capstone.CS_AC_WRITE and op.size<4:
                    name=canonical(ins,op.reg)
                    if name in taintedregs:out.add(('r',name));event(pc,'partial-register-write',name)
            # Capstone can report WRITE-only for conditional writes or legacy
            # scalar SIMD operations that retain part of the old destination.
            # An unrecognized write is not sufficient evidence to kill MAY data.
            full_destinations=set()
            if ops and ops[0].type==X86_OP_REG and ops[0].size>=4:
                dest=canonical(ins,ops[0].reg)
                if not dest.startswith(('xmm','ymm','zmm')) and ins.mnemonic in COPIES|ARITHMETIC|{'lea'}:
                    full_destinations.add(dest)
                elif ins.mnemonic in {'movaps','movups','movdqa','movdqu'} and ops[0].size==16:
                    full_destinations.add(dest)
            for reg in writes:
                name=canonical(ins,reg)
                if name in taintedregs and name not in full_destinations and name!='rflags':
                    out.add(('r',name));event(pc,'unsupported-overwrite-gap',name)
            # Arithmetic/unknown memory stores need the same escape accounting
            # as MOV, including read-modify-write of a saved stack value.
            for ix,op in enumerate(ops):
                if op.type!=X86_OP_MEM or not memory_operand_writes(ins,ix) or ins.mnemonic in COPIES:continue
                if source_tainted:
                    offset=stack_address(pc,op)
                    if offset is not None and op.size==8:out.add(('s',offset));event(pc,'stack-rmw',str(offset))
                    else:event(pc,'persistent-store-escape','arithmetic or unsupported tainted memory store')
            if ins.group(capstone.CS_GRP_JUMP) and ins.operands and ins.operands[0].type!=capstone.x86.X86_OP_IMM and readtaint:
                event(pc,'indirect-control-escape',','.join(sorted(readtaint)))
        # The decoder may omit external direct branches from its local CFG.
        # Inspect the instruction itself as well, never infer a dead result
        # merely because the successor dictionary has no entry.
        if ins.group(capstone.CS_GRP_JUMP) and ins.operands and ins.operands[0].type==capstone.x86.X86_OP_IMM:
            target=ins.operands[0].imm
            if target not in succ.get(pc,()) and out:event(pc,'external-control-flow','decoded branch target absent from local CFG')
        elif ins.group(capstone.CS_GRP_JUMP) and not succ.get(pc,()) and out:
            event(pc,'external-tail-gap','live result exits through unmodeled indirect branch, even if target is untainted')
            for reg in sorted(taintedregs&ARGS):event(pc,'call-escape',reg)
        terminal=ins.group(capstone.CS_GRP_RET) or ins.mnemonic in ('jmp','ljmp','ud2','int3')
        if not terminal and pc+ins.size not in succ.get(pc,()) and out:
            event(pc,'missing-fallthrough-gap','live result may continue outside local CFG')
        for target in succ.get(pc,()):merge(target,out)
    return {'definition':hex(definition),'events':[events[k] for k in sorted(events)],
            'visitedInstructions':len(states),'fixpointSteps':steps,
            'allResultUsesProvenAddressOnly':False,'hookApproved':False}
