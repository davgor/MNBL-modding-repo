"""Conditional constructor-backed owner contributions; never hook approval.

Only this new audit's transfer function recognizes the two additional fields.
Accepted v1/v2 flow inputs and their receiver contracts remain unchanged.
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
from native_instruction_semantics import memory_operand_writes


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def build():
    prior_path = ROOT / 'evidence/engine-extension/native-live-agent-owner-extension-v2.json'
    prior = json.loads(prior_path.read_text())
    assert digest(prior_path) == 'b0dd96c9a1c082d3ef1e0d1e643df896191475005c35e62d241db9430adc0490'
    deps = {d['path']: d['sha256'] for d in prior['dependencies']}
    deps[str(prior_path)] = digest(prior_path)
    deps[str(Path(__file__))] = digest(__file__)
    routes_path = ROOT / 'evidence/engine-extension/native-owner-extension-independent-review.json'
    assert digest(routes_path) == 'f2be91d165f0dbebbf6979187f2203bffdcfed7d1350b8318d00aedbc714b9ba'
    deps[str(routes_path)] = digest(routes_path)
    routes = {site: route for route in json.loads(routes_path.read_text())['remainingClosureRoutes']
              for site in route['sites']}
    for p, h in deps.items():
        assert digest(p) == h, p
    ns = read_namespace()
    base, pe = ns['base'], ns['pe']
    assert hashlib.sha256(pe.__data__).hexdigest() == prior['binarySha256']
    remaining = {base + int(r, 16) for r in prior['remainingRvas']}
    assert len(remaining) == 24
    rec = ns['flow'].basic

    # The established CreateAgent call supplies Agent and Mission to607550.
    # Observe the constructor's identity in the unmodified accepted analysis.
    ctor = base + 0x607550
    assert ctor in ns['contracts'] and ctor not in ns['blocked_entries']
    ctor_states, ctor_report = ns['analyze'](ctor, ns['contracts'][ctor])
    assert ctor_states is not None
    ctor_ins = ns['cache'][ctor][0]
    exact = {
        0x607571: '488bd9', 0x60760e: '488918',
        0x607611: '488983c00a0000', 0x607636: '48899b50010000',
        0x60762f: '4c89b3c00a0000',
    }
    for rva, raw in exact.items():
        assert ctor_ins[base + rva].bytes.hex() == raw
        assert pe.get_data(rva, len(bytes.fromhex(raw))).hex() == raw
    for rva in (0x60760e, 0x607611, 0x607636):
        assert ctor_states[base + rva]['rbx'][:2] == ('agent', 0)
    assert ns['address'](ctor_ins[base + 0x607636], ctor_ins[base + 0x607636].operands[0].mem,
                         ctor_states[base + 0x607636])[:2] == ('agent', 0x150)
    # Adjacent stores establish wrapper[0]=Agent and Agent+ac0=wrapper.
    assert ctor_ins[base + 0x60760e].address + ctor_ins[base + 0x60760e].size == base + 0x607611
    fields = [
        {'sourceOwner': 'agent', 'offset': 0x150, 'resultKind': 'agent',
         'proofInstructions': [rec(ctor_ins[base+x]) for x in (0x607571, 0x607636)],
         'scope': 'Live Agent initialized by607550 retains its constructor-established self pointer.'},
        {'sourceOwner': 'agent', 'offset': 0xac0, 'resultKind': 'agent-path:0',
         'proofInstructions': [rec(ctor_ins[base+x]) for x in (0x607571, 0x60760e, 0x607611, 0x60762f)],
         'scope': 'When nonnull, the live attached wrapper created/reused by607550 retains Agent in field0; teardown/null state is excluded.'},
    ]
    original = ns['transfer']
    field_loads = {}

    def transfer(i, state):
        out = original(i, state)
        ops = i.operands
        if (i.mnemonic == 'mov' and len(ops) == 2 and ops[0].type == X86_OP_REG
                and ops[0].size == 8 and ops[1].type == X86_OP_MEM and ops[1].size == 8):
            ea = ns['address'](i, ops[1].mem, state)
            if ea and ea[:2] in (('agent', 0x150), ('agent', 0xac0)):
                kind = 'agent' if ea[1] == 0x150 else 'agent-path:0'
                out[ns['reg'](i, ops[0].reg)] = ns['val'](kind, 0, ea[2] | {i.address})
                field_loads[i.address] = {'instruction': rec(i), 'sourceFieldOffset': ea[1], 'resultKind': kind}
        return out

    ns['transfer'] = transfer
    # Inert negative cases guard the intended conditional boundary: offset
    # similarity, wrong width, or an unproved receiver must not create owners.
    checks = []
    for name, raw, receiver, expected in (
        ('initialized-agent-self-field', '488b8150010000', ('agent', 0), ('agent', 0)),
        ('unknown-receiver-does-not-seed', '488b8150010000', None, None),
        ('wrong-owner-kind-does-not-seed', '488b8150010000', ('agent-ai', 0), None),
        ('adjacent-offset-does-not-seed', '488b8151010000', ('agent', 0), None),
        ('narrow-self-load-does-not-seed', '8b8150010000', ('agent', 0), None),
        ('initialized-attached-wrapper-field', '488b81c00a0000', ('agent', 0), ('agent-path:0', 0)),
        ('wrapper-first-field-unwraps-agent', '488b01', ('agent-path:0', 0), ('agent', 0)),
        ('wrapper-neighbor-does-not-unwrap', '488b4108', ('agent-path:0', 0), None),
    ):
        instruction = next(ns['md'].disasm(bytes.fromhex(raw), 0x1000))
        state = {'rcx': ns['val'](*receiver)} if receiver else {}
        observed = transfer(instruction, state).get('rax')
        actual = observed[:2] if observed else None
        assert actual == expected, (name, expected, actual)
        checks.append({'name': name, 'bytes': raw, 'receiver': receiver,
                       'expected': expected, 'observed': actual, 'passed': True})
    field_loads.clear()  # Inert fixture addresses are not native evidence.
    # Candidate roots and the exact caller group discovered by direct-call
    # scan. Existing entry contracts are reused only within their own context.
    root_for = {}
    for definition in remaining:
        root_for[definition] = [r for r, group in ns['groups'].items()
            if any(int(s['start'], 16) <= definition < int(s['endExclusive'], 16)
                   for s in group['runtimeRanges'])]
    target_roots = {r for roots in root_for.values() for r in roots}
    caller_roots = {base+x for x in (0x688530, 0x689220, 0x6899a0, 0x607f30,
                                    0x686450, 0x6ba940, 0x6c9430, 0x6d07c0)}
    queue = deque()
    for root in sorted(target_roots | caller_roots):
        if root in ns['contracts'] and root not in ns['blocked_entries']:
            queue.append((root, ns['contracts'][root], 'existing-accepted-entry-context'))
    analyses = []
    seen = set()
    contributions = {}
    incoming = []
    pointer_kinds = {'agent', 'mission', 'agent-ai', 'agent-equipment', 'agent-path:0'}
    while queue:
        root, contract, origin = queue.popleft()
        registers = contract.get('entryRegisters', {})
        fingerprint = (root, json.dumps(registers, sort_keys=True),
                       json.dumps(contract.get('entryBiases', {}), sort_keys=True),
                       contract.get('entryPointerKind'))
        if fingerprint in seen:
            continue
        seen.add(fingerprint)
        states, report = ns['analyze'](root, contract)
        analyses.append({'root': hex(root), 'origin': origin, 'entryContract': contract, 'analysis': report})
        if states is None:
            continue
        insns = ns['cache'][root][0]
        for a, state in states.items():
            i = insns[a]
            values = list(state.values()) + list(transfer(i, state).values())
            values += [ns['address'](i, op.mem, state) for op in i.operands if op.type == X86_OP_MEM]
            for v in values:
                if not v or v[0] != 'agent':
                    continue
                for definition in v[2] & remaining:
                    contributions.setdefault(definition, {
                        'multiply': rec(insns[definition]), 'root': hex(root),
                        'entryContract': contract, 'use': rec(i), 'fieldBias': v[1],
                        'definitionInstructions': [rec(insns[d]) for d in sorted(v[2]) if d in insns],
                        'hookApproved': False, 'allResultUsesProvenAddressOnly': False})
            if (i.mnemonic not in ('call', 'jmp') or not i.operands
                    or i.operands[0].type != X86_OP_IMM or i.operands[0].imm not in target_roots):
                continue
            target = i.operands[0].imm
            args = {r: state[r] for r in ('rcx', 'rdx', 'r8', 'r9')
                    if r in state and state[r][0] in pointer_kinds}
            incoming.append({'caller': hex(root), 'instruction': rec(i), 'target': hex(target),
                             'provenArguments': {r: {'kind': v[0], 'bias': v[1]} for r, v in args.items()}})
            if not args or target in ns['blocked_entries']:
                continue
            next_contract = {
                'kind': 'specific-constructor-backed-direct-call-context',
                'entryPointerKind': args.get('rcx', (None,))[0],
                'entryRegisters': {r: v[0] for r, v in args.items()},
                'entryBiases': {r: v[1] for r, v in args.items()},
                'sourceProcedure': hex(root), 'callInstruction': rec(i),
                'argumentProvenance': {r: [rec(insns[d]) for d in sorted(v[2]) if d in insns]
                                       for r, v in args.items()},
                'callerContract': contract,
                'scope': 'Only this established caller and initialized-object field context; no universal receiver ABI.'}
            queue.append((target, next_contract, 'exact-direct-call'))
    unresolved = sorted(remaining - contributions.keys())
    # Every retained instruction in the new proof is checked against the exact
    # original image, independently of whether it entered through a dependency.
    verified_rows = set()
    def verify(value):
        if isinstance(value, dict):
            if 'address' in value and 'bytes' in value:
                address = int(value['address'], 16)
                raw = bytes.fromhex(value['bytes'])
                assert pe.get_data(address-base, len(raw)) == raw, value
                verified_rows.add((address, raw))
            for item in value.values():
                verify(item)
        elif isinstance(value, list):
            for item in value:
                verify(item)
    verify(fields)
    verify(list(contributions.values()))
    verify(analyses)
    verify(incoming)
    verify(list(field_loads.values()))
    for p, h in deps.items():
        assert digest(p) == h, p
    return {
        'schemaVersion': 1, 'status': 'conditional-constructor-backed-owner-contributions-pending-independent-review',
        'binarySha256': prior['binarySha256'], 'dependencies': [{'path': p, 'sha256': h} for p, h in sorted(deps.items())],
        'constructorContract': ns['contracts'][ctor], 'constructorAnalysis': ctor_report,
        'constructorFieldRelations': fields, 'fieldLoadsObserved': list(field_loads.values()),
        'negativeBoundaryChecks': checks, 'verifiedOriginalInstructionRows': len(verified_rows),
        'analyses': analyses, 'exactIncomingCalls': incoming,
        'previousRemainingCount': 24, 'additionalProven': len(contributions),
        'proofs': [contributions[a] for a in sorted(contributions)],
        'remainingUnresolvedCount': len(unresolved),
        'remaining': [{'multiplyRva': hex(a-base), 'roots': [hex(r) for r in root_for[a]],
                       'closureRoute': routes[format(a-base, 'x')],
                       'reason': 'No owner contribution established under the bounded additional constructor relations and exact caller contexts.'}
                      for a in unresolved],
        'limitations': [
            'Conditional field contracts require initialized live Agents and nonnull live wrappers, with their constructor-established owner relations intact.',
            'This audit does not prove all writes through arbitrary aliases or establish those relations under corruption, teardown, object copying, or concurrent mutation.',
            'Only exact caller contexts are propagated; no receiver identity is inferred from field offsets alone and canonical contracts are not modified.',
            'Owner contribution is not all-consumer closure, bounds proof, hook-window approval, pointer-lifetime proof, or native engine integration.'],
        'enginePatchApproved': False, 'gameLaunched': False}


def main():
    result = build()
    dest = ROOT / 'evidence/engine-extension/native-remaining-agent-owner-proofs.json'
    dest.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({'path': str(dest), 'sha256': digest(dest),
                      'additional': result['additionalProven'], 'remaining': result['remainingUnresolvedCount']}))


if __name__ == '__main__':
    main()
