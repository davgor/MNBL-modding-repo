"""Conservative transitive register preservation for small direct-call bodies.

This restores register facts only. It never restores caller stack-memory facts.
Unknown targets, cycles, indirect dispatch, decode gaps and large bodies are
treated as clobbering every volatile register. No target code is executed.
"""

STOCK_SHA256 = '2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
# Keep future ISA additions outside the proof by default. This is the complete
# instruction vocabulary of the currently reviewed normal-return summaries;
# state transfer, enclave, far-call and other unmodeled operations do not gain
# preservation merely because Capstone reports an empty implicit write set.
SUPPORTED_MNEMONICS = frozenset('''
add addsd addss and andps bt bts call cdq cdqe cmova cmovb cmovbe cmove cmovg
cmovge cmovl cmovne cmovs cmp comiss cvtdq2ps cvtps2pd cvtsi2ss cvttss2si dec
div divsd divss idiv imul inc ja jae jb jbe je jg jge jl jle jmp jne jns js lea
maxss minss mov movabs movaps movd movmskps movsd movss movsx movsxd movups
movzx mulsd mulss neg nop not or pop push ret rol rsqrtss sar sbb seta setae
setb setbe sete setg setne shl shr shufps sqrtss sub subsd subss test ucomisd
ucomiss unpcklps xchg xor xorps
'''.split()) | {'lock cmpxchg', 'lock xadd'}


def exception_metadata(ns, target, stock_binary_verified=False):
    """Verify no hidden catch/continuation route bypasses the normal CFG.

    Handler-free bodies and recursively validated CHAININFO parents qualify.
    The sole handler exception is stock C++ unwind-only metadata, no try blocks
    or exception specifications. Unknown metadata fails closed.
    """
    import struct
    try:
        pe, base, entries = ns['pe'], ns['base'], ns['entries']
        ranges = ns['groups'][target]['runtimeRanges']
        if not ranges:
            return None
        group_starts = {int(r['start'], 16) - base for r in ranges}
        checked = {}; active = set()

        def check(start):
            if start in checked:
                return True
            if start in active or start not in entries:
                return False
            active.add(start)
            entry = entries[start]
            header = pe.get_data(entry.UnwindData, 4)
            if len(header) != 4 or (header[0] & 7) != 1:
                return False
            flags = header[0] >> 3
            if flags not in (0, 2, 4):
                return False
            length = 4 + ((header[2] + 1) & ~1) * 2
            if len(pe.get_data(entry.UnwindData, length)) != length:
                return False
            tail = entry.UnwindData + length
            record = {'beginRva': hex(start), 'unwindRva': hex(entry.UnwindData), 'flags': flags}
            if flags == 4:
                chain = pe.get_data(tail, 12)
                if len(chain) != 12:
                    return False
                parent, end, unwind = struct.unpack('<III', chain)
                if parent not in group_starts or parent not in entries:
                    return False
                p = entries[parent]
                if p.EndAddress != end or p.UnwindData != unwind or not check(parent):
                    return False
                record['verifiedChainParent'] = hex(parent)
            elif flags == 2:
                if not stock_binary_verified:
                    return False
                data = pe.get_data(tail, 8)
                if len(data) != 8:
                    return False
                handler, func_info = struct.unpack('<II', data)
                raw = pe.get_data(func_info, 40)
                if handler != 0x9e3177 or len(raw) != 40:
                    return False
                info = struct.unpack('<10I', raw)
                if info[0] != 0x19930522 or info[3] != 0 or info[4] != 0 or info[8] != 0:
                    return False
                record.update(handlerRva=hex(handler), funcInfoRva=hex(func_info),
                              funcInfoBytes=raw.hex(), tryBlockCount=0,
                              reason='Exact stock C++ unwind-only handler; no catch or exception specification')
            checked[start] = record
            active.remove(start)
            return True

        leaf_records = ns.get('reviewed_leaf_ranges', [])
        for span in ranges:
            begin, end = int(span['start'], 16) - base, int(span['endExclusive'], 16) - base
            if begin >= end:
                return None
            if begin in entries:
                if entries[begin].EndAddress != end or not check(begin):
                    return None
            else:
                # Only the existing explicit bounded leaf decoder can admit a
                # no-pdata body. A missing runtime entry is not itself proof.
                if any(e.BeginAddress < end and begin < e.EndAddress for e in entries.values()):
                    return None
                if not any(int(r['entry'], 16) == begin + base and
                           int(r['endExclusive'], 16) == end + base for r in leaf_records):
                    return None
                checked[begin] = {'beginRva': hex(begin), 'status': 'reviewed-complete-no-pdata-leaf'}
        return [checked[k] for k in sorted(checked)]
    except (KeyError, TypeError, ValueError, AttributeError, struct.error):
        return None


def install(ns):
    import capstone
    import hashlib
    from capstone.x86 import X86_OP_IMM
    effects=ns['leaf_register_effects']
    effects.clear()
    # The analysis prefix reuses its local name `source` for an integer key.
    # Bind identity to the PE object's actual input buffer, not that name.
    try:
        stock_verified = hashlib.sha256(ns['pe'].__data__).hexdigest() == STOCK_SHA256
    except (KeyError, AttributeError, TypeError, ValueError):
        stock_verified = False
    volatile=set(ns['flow'].VOLATILE)|{'xmm0','xmm1','xmm2','xmm3','xmm4','xmm5'}
    def preserved(target):
        if target in effects:return set(effects[target]['preservedRegisters'])
        effects[target]={'preservedRegisters':[],'status':'unknown-or-active-recursion'}
        if not ns['eligible_procedure'](target) and target!=0x18059ea00:return set()
        if target not in ns['groups'] and not ns['add_leaf_group'](target):return set()
        metadata = exception_metadata(ns, target, stock_verified)
        if metadata is None:
            effects[target]['status'] = 'unverified-exception-metadata'
            return set()
        insns,succ,bad,indirect=ns['decode'](target)
        if bad or indirect or not insns or target not in insns or len(insns)>1000:return set()
        writes=set();calls=[];exits=0
        for ins in insns.values():
            if ins.mnemonic not in SUPPORTED_MNEMONICS or ins.group(capstone.CS_GRP_PRIVILEGE):return set()
            if ins.group(capstone.CS_GRP_CALL) and ins.mnemonic!='call':return set()
            # Capstone does not enumerate all implicit XMM writes for state
            # restore/zero instructions, and this flow's register aliases do
            # not cover YMM/ZMM overlap. Reject this entire instruction family
            # until independently modeled; no current accepted summary needs it.
            if ins.mnemonic in ('ud2','hlt','rsm','xbegin','xend','xabort') or ins.mnemonic.startswith(
                ('sys','int','iret','rep','stos','movsb','movsw','movsq','fxrstor','xrstor','frstor','v')):return set()
            is_return=ins.group(capstone.CS_GRP_RET)
            is_jump=ins.group(capstone.CS_GRP_JUMP)
            if is_return:
                if ins.mnemonic!='ret' or ins.operands:return set()
                exits+=1
            # decode() records only successors within its retained ranges. A
            # missing fallthrough is not proof of an exit: it can hide unknown
            # instructions that clobber the very register being preserved.
            if not is_return and ins.mnemonic!='jmp' and ins.address+ins.size not in insns:return set()
            try:_,registers=ins.regs_access()
            except capstone.CsError:return set()
            if any((ins.reg_name(r) or '').startswith(('ymm','zmm')) for r in registers):return set()
            writes.update(ns['reg'](ins,r) for r in registers)
            if ins.mnemonic=='call' or is_jump and (
                not ins.operands or ins.operands[0].type!=X86_OP_IMM or ins.operands[0].imm not in insns):
                if not ins.operands or ins.operands[0].type!=X86_OP_IMM:return set()
                destination=ins.operands[0].imm
                if is_jump:exits+=1
                child=preserved(destination)
                writes.update(volatile-child)
                calls.append({'instruction':ns['flow'].basic(ins),'target':hex(destination),'preservedRegisters':sorted(child)})
        if not exits:return set()
        keep=volatile-writes
        effects[target]={'preservedRegisters':sorted(keep),'status':'complete-bounded-register-write-closure',
            'exceptionMetadata':metadata,
            'instructions':[ns['flow'].basic(i) for _,i in sorted(insns.items())],
            'directTransfers':calls,
            'proof':'Every decoded fallthrough remains in the body; exits are near RET or summarized direct tail transfers. Register is never written in any decoded instruction or reachable direct callee summary. Unsupported vector/state-restore instructions are rejected. Unresolved/recursive callees clobber all volatile registers. This does not preserve caller memory/spill facts or establish object lifetime.'}
        return keep
    ns['leaf_preserved_registers']=preserved
