"""Conditional target-cache owner contributions, without global receiver seeds.

The cache can contain null. All contributions require the selected cache value
to be nonnull, live, and unchanged since the exact reviewed producer wrote it.
This is a static producer/consumer contract, not universal writer closure.
"""
from pathlib import Path
from collections import deque
import hashlib
import json
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / 'tools/battle-scale'), str(ROOT / 'build/battle-scale/python-libs')]
from capstone.x86 import X86_OP_IMM, X86_OP_MEM, X86_OP_REG
from prove_native_ai_leaf_owner import read_namespace


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def build():
    prior_path = ROOT / 'evidence/engine-extension/native-agent-wrapper-external-tail-proof.json'
    assert digest(prior_path) == '34957deb6b4e790cc2e62a68b1d12a13cf9e6313ad971dd847692e6563e7c5bf'
    prior = json.loads(prior_path.read_text())
    deps = {d['path']: d['sha256'] for d in prior['dependencies']}
    deps[str(prior_path)] = digest(prior_path)
    deps[str(Path(__file__))] = digest(__file__)
    for path, sha in deps.items():
        assert digest(path) == sha, path
    ns = read_namespace()
    pe, base, rec = ns['pe'], ns['base'], ns['flow'].basic
    assert hashlib.sha256(pe.__data__).hexdigest() == prior['binarySha256']
    producer = base + 0x6bcab0
    contract = ns['contracts'][producer]
    assert contract['entryRegisters'] == {'rcx': 'agent-ai'} and contract['entryBiases'] == {'rcx': 8}
    ins, edges, bad, indirect = ns['decode'](producer)
    assert not bad and not indirect
    assert not [i for i in ins.values() if i.mnemonic == 'call']
    assert not [(a, target) for a, targets in edges.items() for target in targets if target <= a]
    # Enumerate every ordinary decoded producer path through its single cache
    # write. Existing owner transfer semantics are used without field additions.
    store = base + 0x6bcb2d
    queue = deque([(producer, {'rcx': ns['val']('agent-ai', 8)}, [])])
    paths = []
    while queue:
        address, state, trace = queue.popleft()
        assert len(trace) < len(ins)
        instruction = ins[address]
        trace = trace + [address]
        if address == store:
            ea = ns['address'](instruction, instruction.operands[0].mem, state)
            value = state.get('r8')
            assert ea and ea[:2] == ('agent-ai', 0xf0)
            assert value and value[:2] in (('agent', 0), ('constant', 0)), value
            paths.append({'path': list(map(hex, trace)), 'destinationKind': ea[0],
                          'destinationBias': ea[1], 'valueKind': value[0], 'valueBias': value[1],
                          'valueDefinitions': [rec(ins[a]) for a in sorted(value[2]) if a in ins]})
            continue
        out = ns['transfer'](instruction, state)
        for target in edges[address]:
            queue.append((target, out, trace))
    assert len(paths) == 10, len(paths)
    assert {p['valueKind'] for p in paths} == {'agent', 'constant'}
    # Full producer body includes both flag-setting exits, after the write.
    assert pe.get_data(0x6bcb2d, 7).hex() == '4c8982e8000000'
    assert pe.get_data(0x6bcb4e, 7).hex() == '814a0800020000'
    assert pe.get_data(0x6bcbc0, 7).hex() == '814a0800020000'
    original = ns['transfer']
    loads = {}

    def transfer(i, state):
        out = original(i, state)
        ops = i.operands
        if (i.mnemonic == 'mov' and len(ops) == 2 and ops[0].type == X86_OP_REG
                and ops[0].size == 8 and ops[1].type == X86_OP_MEM and ops[1].size == 8):
            ea = ns['address'](i, ops[1].mem, state)
            if ea and ea[:2] == ('agent-ai', 0xf0):
                out[ns['reg'](i, ops[0].reg)] = ns['val']('agent', 0, ea[2] | {i.address})
                loads[i.address] = rec(i)
        return out

    checks = []
    for name, raw, receiver, expected in (
        ('nonnull-producer-backed-cache', '488b81f0000000', ('agent-ai', 0), ('agent', 0)),
        ('unknown-receiver', '488b81f0000000', None, None),
        ('wrong-owner', '488b81f0000000', ('agent', 0), None),
        ('neighbor-field', '488b81f8000000', ('agent-ai', 0), None),
        ('partial-width', '8b81f0000000', ('agent-ai', 0), None),
        ('wrong-base-bias', '488b81f0000000', ('agent-ai', 8), None),
        ('subobject-equivalent-offset', '488b81e8000000', ('agent-ai', 8), ('agent', 0)),
    ):
        i = next(ns['md'].disasm(bytes.fromhex(raw), 0x1000))
        state = {'rcx': ns['val'](*receiver)} if receiver else {}
        v = transfer(i, state).get('rax')
        actual = v[:2] if v else None
        assert actual == expected, (name, actual)
        checks.append({'name': name, 'bytes': raw, 'receiver': receiver,
                       'expected': expected, 'observed': actual, 'passed': True})
    loads.clear()
    # Restrict added relation to these exact refresh-guarded reader contexts.
    ns['transfer'] = transfer
    analyses, incoming, proofs = [], [], {}
    remaining = {base + int(r['multiplyRva'], 16) for r in prior['remaining']}
    roots = (base + 0x6ba3f0, base + 0x6ba940)
    queue = deque((r, ns['contracts'][r]) for r in roots)
    while queue:
        root, context = queue.popleft()
        states, report = ns['analyze'](root, context)
        assert states is not None and report['status'] == 'converged'
        instructions = ns['cache'][root][0]
        analyses.append({'root': hex(root), 'context': context, 'analysis': report})
        for a, state in states.items():
            i = instructions[a]
            for op in i.operands:
                if op.type != X86_OP_MEM:
                    continue
                owner = ns['address'](i, op.mem, state)
                if not owner or owner[0] != 'agent':
                    continue
                for definition in owner[2] & remaining:
                    proofs.setdefault(definition, {'multiply': rec(instructions[definition]),
                        'root': hex(root), 'entryContract': context, 'use': rec(i), 'fieldBias': owner[1],
                        'definitionInstructions': [rec(instructions[d]) for d in sorted(owner[2]) if d in instructions],
                        'hookApproved': False, 'allResultUsesProvenAddressOnly': False})
            if (root != base + 0x6ba940 or i.mnemonic != 'call' or i.operands[0].type != X86_OP_IMM
                    or i.operands[0].imm != base + 0x608990):
                continue
            value = state.get('rcx')
            assert value and value[:2] == ('agent', 0)
            c = {'kind': 'exact-target-cache-backed-direct-call-context', 'entryPointerKind': 'agent',
                 'entryRegisters': {'rcx': 'agent'}, 'entryBiases': {'rcx': 0},
                 'sourceProcedure': hex(root), 'callInstruction': rec(i),
                 'argumentProvenance': {'rcx': [rec(instructions[d]) for d in sorted(value[2]) if d in instructions]},
                 'callerContract': context,
                 'scope': 'Nonnull live primary Agent in producer-backed AI target cache, read before this exact call.'}
            incoming.append(c)
            queue.append((base + 0x608990, c))
    assert set(proofs) == {base + 0x6ba459, base + 0x6089a7}, list(map(hex, proofs))
    assert len(incoming) == 2
    readers = []
    for root, rows in ((0x6ba3f0, (0x6ba406, 0x6ba40d, 0x6ba428, 0x6ba42a, 0x6ba42e, 0x6ba433)),
                       (0x6ba940, (0x6ba962, 0x6ba969, 0x6ba970, 0x6ba972, 0x6ba976, 0x6ba982))):
        instructions = ns['cache'][base + root][0]
        readers.append({'root': hex(base + root), 'instructions': [rec(instructions[base+r]) for r in rows]})
    # Original semantic transfer deliberately has no cache identity rule.
    ns['transfer'] = original
    for root in roots:
        s, report = ns['analyze'](root, ns['contracts'][root])
        load = base + (0x6ba433 if root == roots[0] else 0x6ba982)
        result = original(ns['cache'][root][0][load], s[load])
        dest = 'rbp' if root == roots[0] else 'r8'
        assert not result.get(dest) or result[dest][0] != 'agent'
        checks.append({'name': 'without-cache-contract-'+hex(root), 'passed': True, 'analysis': report})
    result = {'schemaVersion': 1, 'status': 'conditional-cache-owner-contributions-pending-independent-review',
        'binarySha256': prior['binarySha256'], 'dependencies': [{'path': p, 'sha256': h} for p, h in sorted(deps.items())],
        'producerContract': contract, 'producerInstructions': [rec(i) for _, i in sorted(ins.items())],
        'producerSuccessors': {hex(a): list(map(hex, ds)) for a, ds in sorted(edges.items())},
        'producerPathsThroughCacheStore': paths, 'refreshGuardedReaders': readers,
        'conditionalRelation': {'sourceOwner': 'agent-ai', 'offset': 0xf0, 'resultKind': 'agent-or-null',
            'usePreconditions': ['Exact6bcab0 produced the cache value for this AI object.',
                'The selected value is nonnull and its original primary Agent remains live.',
                'No intervening cache mutation, object teardown, storage relocation, or stale generation.'],
            'arbitraryWriterClosureProven': False},
        'fieldLoadsObserved': [loads[a] for a in sorted(loads)], 'negativeBoundaryChecks': checks,
        'analyses': analyses, 'exactIncomingCalls': incoming, 'proofs': [proofs[a] for a in sorted(proofs)],
        'previousRemainingCount': 20, 'additionalProven': len(proofs), 'remainingUnresolvedCount': 20-len(proofs),
        'remaining': [r for r in prior['remaining'] if base+int(r['multiplyRva'], 16) not in proofs],
        'limitations': ['Producer is call-free and acyclic ordinary decoded control flow; null is an explicit possible output, never a live Agent.',
            'Cache-valid flag alone is not a global type proof; conditional producer, lifetime and mutation preconditions remain required.',
            'Only two exact reader contexts and their two direct608990 calls use this relation; canonical contracts remain unchanged.',
            'No universal caller, writer, lifetime, index bounds, consumer closure, hook, or engine integration approval.'],
        'enginePatchApproved': False, 'gameLaunched': False}
    verified = set()
    def verify(value):
        if isinstance(value, dict):
            if 'address' in value and 'bytes' in value:
                a, raw = int(value['address'], 16), bytes.fromhex(value['bytes'])
                assert pe.get_data(a-base, len(raw)) == raw
                verified.add((a, raw))
            for v in value.values(): verify(v)
        elif isinstance(value, list):
            for v in value: verify(v)
    verify(result)
    result['verifiedOriginalInstructionRows'] = len(verified)
    for path, sha in deps.items(): assert digest(path) == sha, path
    return result


if __name__ == '__main__':
    result = build()
    dest = ROOT / 'evidence/engine-extension/native-remaining-agent-owner-proofs-v2.json'
    dest.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({'path': str(dest), 'sha256': digest(dest), 'additional': result['additionalProven'],
                      'remaining': result['remainingUnresolvedCount']}))
