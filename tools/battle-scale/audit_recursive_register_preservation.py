"""Exact offline recursive-only register proof for stock helper 6089d0.

This does not install a summary or widen the generic recursive-call policy.
Only normally returning finite invocations are covered; memory is never restored.
"""
from pathlib import Path
import hashlib
import json
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'build/battle-scale/python-libs'))
import capstone
from capstone.x86 import X86_OP_IMM
import pefile

SOURCE = ROOT / 'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
BINARY_SHA256 = '2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
ENTRY = 0x1806089d0
BODY_HEX = '4883ec38488b8180080000488bd10f29742420660f6eb1840500000f5bf64885c0741af30f1088d00800000f28c1f30f59c60f28f0f30f59f1f30f59f1f30f1089e8030000f30f5889c003000048638158050000f30f58f185c078204869c8600c0000488b82b80a00004805502d00004803c8e888fffffff30f58f00f28c60f287424204883c438c3'
UNWIND_HEX = '011303001368020004620000'
VOLATILE = {'rax', 'rcx', 'rdx', 'r8', 'r9', 'r10', 'r11'} | {'xmm' + str(i) for i in range(6)}
ALLOWED = {'sub', 'mov', 'movaps', 'movd', 'cvtdq2ps', 'test', 'je', 'js',
           'movss', 'mulss', 'addss', 'movsxd', 'imul', 'add', 'call', 'ret'}


def canonical(name):
    for full, aliases in {'rax': ('eax', 'ax', 'al', 'ah'),
                          'rcx': ('ecx', 'cx', 'cl', 'ch'),
                          'rdx': ('edx', 'dx', 'dl', 'dh')}.items():
        if name == full or name in aliases:
            return full
    for i in range(8, 12):
        if name in (f'r{i}', f'r{i}d', f'r{i}w', f'r{i}b'):
            return f'r{i}'
    return name


def prove_body(raw, unwind):
    if unwind != bytes.fromhex(UNWIND_HEX):
        raise ValueError('Unverified unwind body, flags or exception handler')
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True
    decoded = list(md.disasm(raw, ENTRY))
    if not decoded or sum(i.size for i in decoded) != len(raw):
        raise ValueError('Incomplete instruction decode')
    insns = {i.address: i for i in decoded}
    edges = {}; writes = set(); calls = []; returns = []
    for ins in decoded:
        if ins.mnemonic not in ALLOWED:
            raise ValueError('Instruction outside exact bounded semantic set')
        writes.update(canonical(ins.reg_name(r)) for r in ins.regs_access()[1])
        next_pc = ins.address + ins.size
        if ins.mnemonic == 'ret':
            if ins.operands:
                raise ValueError('Unsupported return')
            returns.append(ins.address); edges[ins.address] = []
            continue
        if next_pc not in insns:
            raise ValueError('Unclosed fallthrough')
        successors = [next_pc]
        if ins.mnemonic == 'call':
            if len(ins.operands) != 1 or ins.operands[0].type != X86_OP_IMM or ins.operands[0].imm != ENTRY:
                raise ValueError('Call outside singleton recursive component')
            calls.append(ins.address)
        elif ins.group(capstone.CS_GRP_JUMP):
            if len(ins.operands) != 1 or ins.operands[0].type != X86_OP_IMM or ins.operands[0].imm not in insns:
                raise ValueError('Branch outside exact decoded body')
            successors.append(ins.operands[0].imm)
        edges[ins.address] = successors
    if not calls or not returns:
        raise ValueError('Missing recursive edge or returning path')
    seen = set(); pending = [ENTRY]
    while pending:
        pc = pending.pop()
        if pc not in seen:
            seen.add(pc); pending.extend(edges[pc])
    if seen != set(insns):
        raise ValueError('Unreachable decoded instruction requires separate analysis')
    return {'preservedRegisters': sorted(VOLATILE - writes),
            'writtenRegisters': sorted(writes),
            'recursiveCalls': [hex(a) for a in calls],
            'returns': [hex(a) for a in returns],
            'controlFlow': {hex(a): [hex(b) for b in edges[a]] for a in sorted(edges)},
            'instructions': [{'address': hex(i.address), 'bytes': i.bytes.hex(),
                              'mnemonic': i.mnemonic, 'operands': i.op_str} for i in decoded]}


def main():
    data = SOURCE.read_bytes()
    assert hashlib.sha256(data).hexdigest() == BINARY_SHA256
    pe = pefile.PE(data=data)
    matches = [e.struct for e in pe.DIRECTORY_ENTRY_EXCEPTION
               if e.struct.BeginAddress <= 0x6089d0 < e.struct.EndAddress]
    assert len(matches) == 1
    entry = matches[0]
    assert (entry.BeginAddress, entry.EndAddress, entry.UnwindData) == (0x6089d0, 0x608a59, 0xca04b8)
    raw = pe.get_data(entry.BeginAddress, entry.EndAddress - entry.BeginAddress)
    unwind = pe.get_data(entry.UnwindData, len(bytes.fromhex(UNWIND_HEX)))
    assert raw.hex() == BODY_HEX
    proof = prove_body(raw, unwind)
    expected = ['r10', 'r11', 'r8', 'r9', 'xmm2', 'xmm3', 'xmm4', 'xmm5']
    assert proof['preservedRegisters'] == expected
    tests = []
    cases = [('missing-ret', raw[:-1], unwind),
             ('foreign-recursion-target', raw[:0x73] + bytes.fromhex('e889ffffff') + raw[0x78:], unwind),
             ('external-conditional', raw[:0x21] + bytes.fromhex('747f') + raw[0x23:], unwind),
             ('handler-flags', raw, bytes([unwind[0] | 8]) + unwind[1:]),
             ('unsupported-state-restore', bytes.fromhex('0fae09') + raw[3:], unwind)]
    for name, body, metadata in cases:
        try:
            prove_body(body, metadata)
        except ValueError as error:
            tests.append({'name': name, 'rejected': True, 'reason': str(error)})
        else:
            raise AssertionError(name + ' was accepted')
    # Equal-length RCX->R9 substitution must remove R9 from the proof, even
    # without the outer exact-byte pin. This tests alias/write sensitivity.
    mutated = bytearray(raw)
    assert mutated[0x5c:0x63].hex() == '4869c8600c0000'
    mutated[0x5c:0x63] = bytes.fromhex('4c69c8600c0000')
    changed = prove_body(bytes(mutated), unwind)
    assert 'r9' not in changed['preservedRegisters']
    tests.append({'name': 'direct-r9-write', 'removedFromPreserved': 'r9'})
    result = {'schemaVersion': 1, 'status': 'accepted-exact-singleton-recursion-normal-return-register-proof',
              'source': {'path': str(SOURCE.relative_to(ROOT)), 'sha256': BINARY_SHA256},
              'reviewedSourceSha256': {str(Path(__file__).relative_to(ROOT)): hashlib.sha256(Path(__file__).read_bytes()).hexdigest()},
              'entry': hex(ENTRY), 'endExclusive': hex(ENTRY + len(raw)),
              'bodyBytes': raw.hex(), 'bodySha256': hashlib.sha256(raw).hexdigest(),
              'runtimeFunction': {'beginRva': '0x6089d0', 'endRva': '0x608a59', 'unwindRva': '0xca04b8',
                                  'unwindBytes': unwind.hex(), 'version': 1, 'flags': 0, 'handlerOrChainedEntry': False},
              'proof': proof, 'tests': tests,
              'reasoning': 'Complete decoded normal CFG has one recursive call to its own exact entry and no other call, tail transfer, handler or indirect edge. The named registers are never written locally. Induction on finite returning recursion depth proves each invocation returns them unchanged. No fixed recursion limit or assumption about child arguments is needed for this register-only property.',
              'limitations': ['Does not prove termination, recursion depth safety, valid Agent owner or pointer lifetime.',
                             'Abnormal non-returning exception paths are outside the fact restored at the next instruction after CALL.',
                             'No caller memory or spill fact is preserved; no hooks, deployment or native engine execution.',
                             'Generic recursive-call rejection remains unchanged. Integration must gate exact body, runtime metadata and whole binary identity.']}
    out = ROOT / 'evidence/engine-extension/native-recursive-register-preservation-review.json'
    out.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({'path': str(out.relative_to(ROOT)), 'preserved': expected, 'negativeChecks': len(tests)}))


if __name__ == '__main__':
    main()
