"""Exact imported-tail closure and one conditional wrapper-owner contribution.

No canonical proof mutation, hook approval, installation or game execution.
"""
from pathlib import Path
import hashlib
import json
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / 'tools/battle-scale'), str(ROOT / 'build/battle-scale/python-libs')]
from capstone import CS_GRP_JUMP, CS_GRP_RET
from capstone.x86 import X86_OP_IMM, X86_OP_MEM, X86_OP_REG
from prove_native_ai_leaf_owner import read_namespace

BODY_SHA = 'c9a246df521d9697b67890091a0eba79c3934d164d840c1ae491a7487947fc53'
PRIOR_SHA = '95f9bd0e0c16fbe8da7b4bb79dace261441bef226e39d882d9b8cdcb9604c083'
IMPORT = ('MSVCP140.dll', '?_Throw_C_error@std@@YAXH@Z')


def digest(p):
    return hashlib.sha256(Path(p).read_bytes()).hexdigest()


def verified_tail(body, root_rva, tail_rva, slot_rva, import_identity, unwind):
    """Fail closed on this exact body and import identity; not a general rule."""
    return (hashlib.sha256(body).hexdigest() == BODY_SHA
            and root_rva == 0x6df380 and tail_rva == 0x6df4f2
            and slot_rva == 0xa2d838 and import_identity == IMPORT
            and unwind == [
                (0x6df380, 0x6df387, 0xb87098, '0104010004420000'),
                (0x6df387, 0x6df4a3, 0xcb4680, '21120600127404000d6407000534060080f36d0087f36d009870b800'),
                (0x6df4a3, 0x6df4fe, 0xcb469c, '2100000080f36d0087f36d009870b800')])


def build():
    prior_path = ROOT / 'evidence/engine-extension/native-remaining-agent-owner-proofs.json'
    assert digest(prior_path) == PRIOR_SHA
    prior = json.loads(prior_path.read_text())
    deps = {d['path']: d['sha256'] for d in prior['dependencies']}
    deps[str(prior_path)] = PRIOR_SHA
    deps[str(Path(__file__))] = digest(__file__)
    for p, h in deps.items():
        assert digest(p) == h, p
    ns = read_namespace()
    pe, base = ns['pe'], ns['base']
    assert hashlib.sha256(pe.__data__).hexdigest() == prior['binarySha256']
    root, tail = base + 0x6df380, base + 0x6df4f2
    body = pe.get_data(0x6df380, 0x17e)
    insns, old_succ, bad, indirect = ns['decode'](root)
    assert not bad and indirect == [hex(tail)]
    assert set(insns) == {i.address for i in ns['md'].disasm(body, root)}
    assert max(a + i.size for a, i in insns.items()) == base + 0x6df4fe
    i = insns[tail]
    assert i.bytes.hex() == '48ff253fe33400' and i.mnemonic == 'jmp'
    assert i.operands[0].type == X86_OP_MEM
    mem = i.operands[0].mem
    assert i.reg_name(mem.base) == 'rip' and not mem.index
    slot = i.address + i.size + mem.disp - base
    imports = [(d.dll.decode(), imp.name.decode() if imp.name else imp.ordinal)
               for d in pe.DIRECTORY_ENTRY_IMPORT for imp in d.imports
               if imp.address - base == slot]
    assert imports == [IMPORT]
    metadata = []
    for e in pe.DIRECTORY_ENTRY_EXCEPTION:
        e = e.struct
        if e.BeginAddress >= 0x6df4fe or e.EndAddress <= 0x6df380:
            continue
        header = pe.get_data(e.UnwindData, 4)
        flags, codes = header[0] >> 3, header[2]
        size = 4 + ((codes + 1) & ~1) * 2
        assert flags in (0, 4), 'No unknown handler/exception reentry accepted'
        if flags == 4:
            size += 12
        metadata.append((e.BeginAddress, e.EndAddress, e.UnwindData, pe.get_data(e.UnwindData, size).hex()))
    assert verified_tail(body, 0x6df380, 0x6df4f2, slot, imports[0], metadata)
    # Both chained records resolve to the exact parent with no language handler.
    for row in metadata[1:]:
        assert bytes.fromhex(row[3])[-12:] == bytes.fromhex('80f36d0087f36d009870b800')
    assert insns[base + 0x6df4ee].bytes.hex() == '4883c428'
    assert insns[base + 0x6df4ec].bytes.hex() == '8bc8'
    assert insns[base + 0x6df380].bytes.hex() == '4883ec28'

    fixtures = []
    standard = [body, 0x6df380, 0x6df4f2, slot, IMPORT, metadata]
    cases = [('exact-original', None, None, True),
             ('wrong-root', 1, 0x6df381, False),
             ('wrong-dispatch', 2, 0x6df4f3, False),
             ('neighbor-import-slot', 3, slot + 8, False),
             ('different-import-name', 4, ('MSVCP140.dll', '_Mtx_unlock'), False),
             ('unknown-import-module', 4, ('unverified.dll', IMPORT[1]), False),
             ('changed-body', 0, bytes([body[0] ^ 1]) + body[1:], False),
             ('changed-epilogue', 0, body[:0x16e] + b'\x90' + body[0x16f:], False),
             ('unverified-exception-metadata', 5, metadata[:-1], False)]
    for name, index, changed, expected in cases:
        args = standard.copy()
        if index is not None:
            args[index] = changed
        observed = verified_tail(*args)
        assert observed is expected, name
        fixtures.append({'name': name, 'expected': expected, 'observed': observed, 'passed': True})

    context = next(a['entryContract'] for a in prior['analyses'] if a['root'] == hex(root))
    assert context['sourceProcedure'] == '0x180607f30'
    assert context['callInstruction']['address'] == '0x180607ff0'
    assert context['entryRegisters'] == {'rcx': 'agent-path:0'}
    caller = base + 0x607f30
    assert caller not in ns['blocked_entries'] and root not in ns['blocked_entries']
    assert ns['contracts'][caller] == context['callerContract']
    # Replay only the retained wrapper-field relation in this exact caller.
    # No entry seed is created from an offset or unknown receiver.
    original_transfer = ns['transfer']
    def transfer(instruction, state):
        out = original_transfer(instruction, state)
        ops = instruction.operands
        if (instruction.mnemonic == 'mov' and len(ops) == 2
                and ops[0].type == X86_OP_REG and ops[0].size == 8
                and ops[1].type == X86_OP_MEM and ops[1].size == 8):
            ea = ns['address'](instruction, ops[1].mem, state)
            if ea and ea[:2] == ('agent', 0xac0):
                out[ns['reg'](instruction, ops[0].reg)] = ns['val']('agent-path:0', 0, ea[2] | {instruction.address})
        return out
    ns['transfer'] = transfer
    caller_states, caller_report = ns['analyze'](caller, context['callerContract'])
    assert caller_states is not None and caller_report['status'] == 'converged'
    argument = caller_states[base + 0x607ff0]['rcx']
    assert argument[:2] == ('agent-path:0', 0)
    caller_ins = ns['cache'][caller][0]
    assert caller_ins[base + 0x607ff0].bytes.hex() == 'e88b730d00'
    for row in context['argumentProvenance']['rcx']:
        assert int(row['address'], 16) in argument[2]
        assert caller_ins[int(row['address'], 16)].bytes.hex() == row['bytes']
    ns['transfer'] = original_transfer
    baseline_states, baseline = ns['analyze'](root, context)
    use, multiply = base + 0x6df3b2, base + 0x6df3a4
    memory = next(op.mem for op in insns[use].operands if op.type == X86_OP_MEM)
    before = ns['address'](insns[use], memory, baseline_states[use])
    assert not before or before[0] != 'agent'

    # Add this exact imported tail only to the isolated namespace's reviewed
    # tails. Existing unknown indirect jumps elsewhere keep their backedges.
    assert tail not in ns['reviewed_external_tails']
    ns['reviewed_external_tails'][tail] = {'root': root, 'proof': 'Exact PE-import tail to MSVCP140 std::_Throw_C_error after restoring RSP; no local continuation.'}
    ns['cache'].pop(root)
    insns, succ, bad, indirect = ns['decode'](root)
    assert not bad and not indirect and succ[tail] == []
    # Independent edge construction: no arbitrary removals or added backedges.
    expected_edges = {}
    for a, instruction in insns.items():
        nxt = a + instruction.size
        targets = []
        if a == tail or instruction.group(CS_GRP_RET):
            pass
        elif instruction.group(CS_GRP_JUMP):
            assert instruction.operands[0].type == X86_OP_IMM
            dest = instruction.operands[0].imm
            assert dest in insns
            targets.append(dest)
            if instruction.mnemonic != 'jmp':
                assert nxt in insns
                targets.append(nxt)
        else:
            assert nxt in insns
            targets.append(nxt)
        expected_edges[a] = targets
    assert expected_edges == succ
    assert [a for a in succ if succ[a] != old_succ[a]] == [tail]
    # No instruction can transfer back into the owner prefix from its suffix.
    assert not [(a, b) for a, targets in succ.items() for b in targets
                if a > use and b <= use]
    states, report = ns['analyze'](root, context)
    assert states is not None and report['status'] == 'converged'
    owner = ns['address'](insns[use], memory, states[use])
    assert owner and owner[:2] == ('agent', 0xac8) and multiply in owner[2]
    unknown_context = {'entryPointerKind': None, 'entryRegisters': {}}
    unknown_states, unknown_report = ns['analyze'](root, unknown_context)
    unknown_owner = ns['address'](insns[use], memory, unknown_states[use])
    assert not unknown_owner or unknown_owner[0] != 'agent'
    fixtures.append({'name': 'correct-cfg-does-not-seed-unknown-receiver', 'passed': True,
                     'analysis': unknown_report})
    rec = ns['flow'].basic
    proof = {'multiply': rec(insns[multiply]), 'root': hex(root), 'use': rec(insns[use]),
             'entryContract': context, 'fieldBias': owner[1],
             'definitionInstructions': [rec(insns[d]) for d in sorted(owner[2]) if d in insns],
             'hookApproved': False, 'allResultUsesProvenAddressOnly': False}
    remaining = [r for r in prior['remaining'] if r['multiplyRva'] != '0x6df3a4']
    assert len(remaining) == 20
    for p, h in deps.items():
        assert digest(p) == h, p
    return {'schemaVersion': 1, 'status': 'conditional-import-tail-owner-contribution-pending-independent-review',
            'binarySha256': prior['binarySha256'],
            'dependencies': [{'path': p, 'sha256': h} for p, h in sorted(deps.items())],
            'classification': 'PE-import epilogue tail, not an indexed switch; no table or index bounds apply.',
            'dispatch': rec(insns[tail]), 'iatSlotRva': hex(slot), 'importModule': IMPORT[0], 'importSymbol': IMPORT[1],
            'bodyStart': hex(root), 'bodyEndExclusive': hex(base + 0x6df4fe), 'bodySha256': BODY_SHA,
            'unwindRecords': [{'beginRva': hex(b), 'endRva': hex(e), 'unwindRva': hex(u), 'bytes': raw}
                              for b, e, u, raw in metadata],
            'instructions': [rec(instruction) for a, instruction in sorted(insns.items())],
            'localSuccessors': {hex(a): list(map(hex, dest)) for a, dest in sorted(succ.items())},
            'changedEdgeSources': [hex(tail)], 'removedArtificialEdges': len(old_succ[tail]),
            'beforeAnalysis': baseline, 'afterAnalysis': report, 'callerAnalysis': caller_report,
            'negativeBoundaryChecks': fixtures, 'previousRemainingCount': 21, 'additionalProven': 1,
            'cumulativeAdditionalSinceCanonicalV2': 4, 'proofs': [proof],
            'remainingUnresolvedCount': len(remaining), 'remaining': remaining,
            'limitations': [
                'Exact PE import identity assumes the loader-resolved IAT is intact; no injected or modified import target is modeled.',
                'One accepted caller context and constructor-backed wrapper relation only; initialized live Agent and nonnull live attached wrapper remain required.',
                'Neither universal receiver identity nor arbitrary alias writes, concurrent mutation, corrupt objects or teardown validity is proved.',
                'Ordinary decoded control flow only. The two chained unwind records have no language handler and resolve to the verified parent; foreign reentry is not inferred.',
                'Owner identity is not bounds, all-consumer, stable-address, hook or engine-integration approval.'],
            'enginePatchApproved': False, 'gameLaunched': False}


if __name__ == '__main__':
    result = build()
    path = ROOT / 'evidence/engine-extension/native-agent-wrapper-external-tail-proof.json'
    path.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({'path': str(path), 'sha256': digest(path), 'additionalProven': result['additionalProven'],
                      'remainingUnresolvedCount': result['remainingUnresolvedCount'],
                      'boundaryChecks': len(result['negativeBoundaryChecks'])}))
