"""Bounded call-context owner proof for the previously ungrouped 7114c0 leaf.

Reads the existing analysis prefix without its output-writing suffix. Does not
change canonical contracts, eligibility policy, hooks, or installed files.
"""
from pathlib import Path
import ast
import hashlib
import json
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / 'build/battle-scale/python-libs'), str(ROOT / 'tools/battle-scale')]
from capstone.x86 import X86_OP_MEM


def read_namespace():
    path = ROOT / 'tools/battle-scale/extend_live_agent_owner_proofs.py'
    tree = ast.parse(path.read_text())
    tree.body = [n for n in tree.body if not isinstance(n, ast.If)]
    main = next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == 'main')
    stops = [i for i, n in enumerate(main.body)
             if isinstance(n, ast.Expr) and isinstance(n.value, ast.Call)
             and isinstance(n.value.func, ast.Name) and n.value.func.id == 'exec']
    assert len(stops) == 1
    main.body = main.body[:stops[0]+1] + [ast.Return(ast.Name('ns', ast.Load()))]
    scope = {'__file__': str(path), '__name__': 'read_only_leaf_proof'}
    exec(compile(ast.fix_missing_locations(tree), str(path), 'exec'), scope)
    return scope['main']()


def main():
    ns = read_namespace()
    pe, base = ns['pe'], ns['base']
    binary_sha = hashlib.sha256(pe.__data__).hexdigest()
    assert binary_sha == '2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
    caller, call, entry = base + 0x712560, base + 0x71272e, base + 0x7114c0
    states, caller_report = ns['analysis'][caller]
    assert states is not None and caller_report['status'] == 'converged'
    value = states[call].get('rcx')
    assert value and value[:2] == ('agent-ai', 0), value
    assert caller not in ns['blocked_entries']
    caller_instructions = ns['cache'][caller][0]
    assert caller_instructions[call].bytes.hex() == 'e88dedffff'
    assert caller_instructions[call-4].bytes.hex() == '488b4e08'
    assert entry not in ns['groups']
    assert pe.get_data(0x7114b4, 12) == b'\xcc' * 12
    assert ns['add_leaf_group'](entry)
    group = ns['groups'][entry]
    assert group['runtimeRanges'] == [{'start': hex(entry), 'endExclusive': hex(base + 0x7114f2)}]
    assert not any(e.BeginAddress < 0x7114f2 and 0x7114c0 < e.EndAddress for e in ns['entries'].values())
    contract = {'kind': 'specific-established-direct-call-context', 'entryPointerKind': 'agent-ai',
                'entryRegisters': {'rcx': 'agent-ai'}, 'entryBiases': {'rcx': 0},
                'sourceProcedure': hex(caller), 'callInstruction': ns['flow'].basic(caller_instructions[call]),
                'argumentProvenance': {'rcx': [ns['flow'].basic(caller_instructions[a])
                                             for a in sorted(value[2]) if a in caller_instructions]},
                'callerContract': ns['contracts'][caller],
                'scope': 'Conditional on this established caller receiver contract. No universal ABI inferred from offsets.'}
    leaf_states, report = ns['analyze'](entry, contract)
    assert leaf_states is not None and report['status'] == 'converged'
    instructions = ns['cache'][entry][0]
    multiply, use = base + 0x7114d4, base + 0x7114e2
    assert instructions[multiply].bytes.hex() == '4869d0600c0000'
    owners = [ns['address'](instructions[use], op.mem, leaf_states[use])
              for op in instructions[use].operands if op.type == X86_OP_MEM]
    assert len(owners) == 1 and owners[0] is not None and owners[0][:2] == ('agent', 0x560)
    assert multiply in owners[0][2]
    deps = ['tools/battle-scale/prove_mission_aliases.py',
            'tools/battle-scale/extend_live_agent_owner_proofs.py',
            'tools/battle-scale/bounded_native_register_effects.py',
            'tools/battle-scale/install_live_growth_flow_contracts.py',
            'tools/battle-scale/prove_native_ai_leaf_owner.py',
            'evidence/engine-extension/native-live-agent-owner-extension.json']
    output = {'schemaVersion': 1, 'status': 'bounded-specific-caller-owner-proof-not-global-hook-approval',
              'binarySha256': binary_sha,
              'reviewedSourceSha256': {p: hashlib.sha256((ROOT / p).read_bytes()).hexdigest() for p in deps},
              'entry': hex(entry), 'endExclusive': hex(base+0x7114f2),
              'bodyBytes': pe.get_data(0x7114c0, 0x32).hex(),
              'boundaryProof': 'Exact direct-call target7114c0 after12INT3; complete forward-branch leaf decode to7114f2 withtwoRET and no pdata overlap. Never starts atpadding7114b4.',
              'entryContract': contract, 'callerAnalysis': caller_report, 'leafAnalysis': report,
              'instructions': [ns['flow'].basic(i) for _, i in sorted(instructions.items())],
              'ownerUse': {'multiply': ns['flow'].basic(instructions[multiply]),
                           'use': ns['flow'].basic(instructions[use]), 'effectiveOwner': 'agent', 'fieldOffset': 0x560,
                           'definitions': [ns['flow'].basic(instructions[a]) for a in sorted(owners[0][2]) if a in instructions]},
              'nextIntegration': 'After independent review, add exactentry7114c0 as boundedeligible procedure and retainedleaf boundary so ordinary direct-call contract propagation can discover it. Do not directly seedAI without caller evidence.',
              'limitations': ['One established caller context is proven; all possible caller/interior-entry enumeration is not claimed.',
                             'No global eligibility range widened and canonical119candidate analysis unchanged.',
                             'Native index bounds, all result uses and safe substitution are separate proofs; no hook or engine execution.']}
    out = ROOT / 'evidence/engine-extension/native-ai-leaf-owner-proof.json'
    out.write_text(json.dumps(output, indent=2) + '\n')
    print(json.dumps({'output': str(out.relative_to(ROOT)), 'entry': hex(entry), 'provedMultiply': hex(multiply)}))


if __name__ == '__main__':
    main()
