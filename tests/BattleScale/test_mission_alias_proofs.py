"""Regression tests for the actual owner-proof transfer functions, without a game.

Load only named pure function definitions from the proof script. Its CLI reads
the real evidence bundles, so importing that script as a module is inappropriate.
"""
import ast
import importlib.util
from pathlib import Path
import sys
import unittest
from bisect import bisect_right

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'build/battle-scale/python-libs'))
sys.path.insert(0, str(ROOT / 'tools/battle-scale'))
from native_instruction_semantics import memory_operand_writes
import capstone
from capstone import CS_GRP_JUMP, CS_GRP_RET
from capstone.x86 import X86_OP_REG, X86_OP_IMM, X86_OP_MEM, X86_REG_RIP

spec = importlib.util.spec_from_file_location('flow', ROOT / 'tools/battle-scale/classify_pool_references.py')
flow = importlib.util.module_from_spec(spec)
spec.loader.exec_module(flow)
source_path = ROOT / 'tools/battle-scale/prove_mission_aliases.py'
tree = ast.parse(source_path.read_text())
names = {'receiver_kind_for_argument', 'conflicting_entry_registers', 'reg',
         'val', 'extend', 'plus', 'address', 'transfer', 'join',
         'add_leaf_group','overlaps_pdata','leaf_preserved_registers'}
definitions = [n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name in names]
assert {n.name for n in definitions} == names
scope = dict(globals())
scope['AGENT_VECTOR_BASES']=(0x1037938,0x103b920,0x103f908)
scope['leaf_preserved_registers']=lambda target:set()
scope['eligible_procedure']=lambda target:0x1805b0000<=target<0x180711000
exec(compile(ast.Module(body=definitions, type_ignores=[]), str(source_path), 'exec'), scope)
leaf_effect_function=scope['leaf_preserved_registers']
scope['leaf_preserved_registers']=lambda target:set()
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
md.detail = True


def run(hex_bytes, state):
    for ins in md.disasm(bytes.fromhex(hex_bytes), 0x1000):
        state = scope['transfer'](ins, state)
    return state


class OwnerProofTests(unittest.TestCase):
    def test_simd_memory_stores_invalidate_overlapping_spills(self):
        for store in ('0f11442438','0f29442438','f30f7f442438','660f7f442438','c5f811442438'):
            with self.subTest(store=store):
                initial={'rsp':('stack',-128,frozenset()),'rcx':('mission',0,frozenset())}
                state=run('48894c2440'+store+'488b442440',initial)
                self.assertNotIn('rax',state)

    def test_simd_store_does_not_kill_disjoint_spill(self):
        initial={'rsp':('stack',-128,frozenset()),'rcx':('mission',0,frozenset())}
        state=run('48894c24400f11442420488b442440',initial)
        self.assertEqual(state['rax'][:2],('mission',0))

    def test_unknown_indexed_stack_store_invalidates_spill(self):
        initial={'rsp':('stack',-128,frozenset()),'rcx':('mission',0,frozenset())}
        state=run('48894c24400f11440438488b442440',initial)
        self.assertNotIn('rax',state)

    def test_hidden_result_is_not_receiver(self):
        contract = {'entryPointerKind': 'mission', 'entryRegisters': {'rdx': 'mission'}}
        self.assertIsNone(scope['receiver_kind_for_argument'](contract))

    def test_distinct_pointer_types_do_not_merge(self):
        conflict = scope['conflicting_entry_registers'](
            {'entryPointerKind': 'mission'}, {'rcx': 'agent'})
        self.assertEqual(conflict['rcx'], {'expected': 'mission', 'observed': 'agent'})
        self.assertEqual(scope['join']({'rcx': ('mission', 0, frozenset())},
                                     {'rcx': ('agent', 0, frozenset())}), {})

    def test_owner_load_requires_proved_agent(self):
        code = '488b81b80a0000'  # mov rax,[rcx+ab8]
        self.assertEqual(run(code, {'rcx': ('agent', 0, frozenset())})['rax'][:2], ('mission', 0))
        self.assertNotIn('rax', run(code, {'rcx': ('mission', 0, frozenset())}))

    def test_partial_write_destroys_pointer(self):
        self.assertNotIn('rax', run('b001', {'rax': ('mission', 0, frozenset())}))

    def test_call_clobbers_volatile_but_preserves_nonvolatile(self):
        state = run('e800000000', {'rcx': ('mission', 0, frozenset()),
                                  'rbx': ('agent', 0, frozenset())})
        self.assertNotIn('rcx', state)
        self.assertEqual(state['rbx'][:2], ('agent', 0))

    def test_returned_call_restores_stack_pointer_without_restoring_escaped_spills(self):
        initial={'rsp':('stack',-128,frozenset()),'rcx':('mission',0,frozenset())}
        state=run('48894c2440e800000000488b442440',initial)
        self.assertEqual(state['rsp'][:2],('stack',-128))
        self.assertEqual(state['rax'][:2],('mission',0))
        escaped=run('48894c2440488d4c2440e800000000488b442440',initial)
        self.assertEqual(escaped['rsp'][:2],('stack',-128))
        self.assertNotIn('rax',escaped)

    def test_exact_stack_spill_and_reload(self):
        state = run('48894c2440488b442440', {'rsp': ('stack', -128, frozenset()),
                                            'rcx': ('mission', 0, frozenset())})
        self.assertEqual(state['rax'][:2], ('mission', 0))

    def test_escaped_stack_pointer_invalidates_spill(self):
        state = run('48894c2440488d4c2440e800000000488b442440',
                    {'rsp': ('stack', -128, frozenset()), 'rcx': ('mission', 0, frozenset())})
        self.assertNotIn('rax', state)

    def test_overlapping_stack_write_invalidates_spill(self):
        state = run('48894c2440c744244400000000488b442440',
                    {'rsp': ('stack', -128, frozenset()), 'rcx': ('mission', 0, frozenset())})
        self.assertNotIn('rax', state)

    def test_affine_mission_bias_is_retained(self):
        state = run('4881c1502d0000', {'rcx': ('mission', 0, frozenset())})
        self.assertEqual(state['rcx'][:2], ('mission', 0x2d50))

    def test_different_biases_do_not_merge(self):
        self.assertEqual(scope['join']({'rcx': ('mission', 0, frozenset())},
                                     {'rcx': ('mission', 8, frozenset())}), {})

    def test_indexed_primary_agent_owner_load(self):
        state=run('4869d0600c0000488b8c3a08380000',{'rdi':('mission',0,frozenset())})
        self.assertEqual(state['rcx'][:2],('mission',0))
        self.assertNotIn('rcx',run('4869d0600c0000488b8c3a10380000',{'rdi':('mission',0,frozenset())}))

    def test_agent_ai_owner_chain(self):
        state=run('488b8988080000488b81d8140000',{'rcx':('agent',0,frozenset())})
        self.assertEqual(state['rax'][:2],('mission',0))
        self.assertNotIn('rax',run('488b81d8140000',{'rcx':('mission',0,frozenset())}))

    def test_typed_capture_paths_keep_owner_kind(self):
        self.assertEqual(run('488b01488b4008488b4010',{'rcx':('missile-path:0:8',0,frozenset())})['rax'][:2],('mission',0))
        self.assertEqual(run('488b4120488b80b80a0000',{'rcx':('agent-path:32',0,frozenset())})['rax'][:2],('mission',0))
        self.assertNotIn('rax',run('488b4118488b80b80a0000',{'rcx':('agent-path:32',0,frozenset())}))

    def test_missile_owner_requires_missile_identity(self):
        self.assertEqual(run('488b4110',{'rcx':('missile',0,frozenset())})['rax'][:2],('mission',0))
        self.assertNotIn('rax',run('488b4110',{'rcx':('agent',0,frozenset())}))

    def test_bounded_missile_page_index(self):
        state=run('83e10f4c8d3c4949c1e70a4c033cd0',{'rax':('missile-page-table',0,frozenset())})
        self.assertEqual(state['r15'][:2],('missile',0))
        self.assertNotIn('r15',run('4c8d3c4949c1e70a4c033cd0',{'rax':('missile-page-table',0,frozenset())}))
        self.assertNotIn('r15',run('83e10f4c8d3c4949c1e7094c033cd0',{'rax':('missile-page-table',0,frozenset())}))

    def test_partial_mask_does_not_prove_bounded_index(self):
        for code in ['80e10f','6683e10f']:
            self.assertNotIn('rcx',run(code,{}))
            self.assertNotIn('r15',run(code+'4c8d3c4949c1e70a4c033cd0',{'rax':('missile-page-table',0,frozenset())}))

    def test_missile_pointer_list_begin_and_stride(self):
        state=run('488b8198470401488b04d8488b4010',{'rcx':('mission',0,frozenset())})
        self.assertEqual(state['rax'][:2],('mission',0))
        self.assertNotIn('rax',run('488b8198470401488b0498488b4010',{'rcx':('mission',0,frozenset())}))
        self.assertNotIn('rax',run('488b81a0470401488b04d8488b4010',{'rcx':('mission',0,frozenset())}))

    def test_missile_paged_storage_chain(self):
        state=run('488b8970470401488b04d1488b4010',{'rcx':('mission',0,frozenset())})
        self.assertEqual(state['rax'][:2],('mission',0))

    def test_inline_agent_component_owner(self):
        state=run('488b81b8000000',{'rcx':('agent',0x158,frozenset())})
        self.assertEqual(state['rax'][:2],('agent',0))

    def test_capture_path_checks_every_offset(self):
        state={'r8':('mission-path:0:8',0,frozenset())}
        self.assertEqual(run('498b00488b4008',state)['rax'][:2],('mission',0))
        self.assertNotIn('rax',run('498b00488b00',state))

    def test_vector_end_is_not_typed_as_begin(self):
        state={'rcx':('agent-vector',0,frozenset())}
        self.assertEqual(run('488b81c03f0000',state)['rax'][:2],('agent-array',0))
        self.assertNotIn('rax',run('488b81c83f0000',state))

    def test_stack_probe_summary_preserves_registers(self):
        instruction=list(md.disasm(bytes.fromhex('e85bf79d00'),0x180001000))[0]
        self.assertEqual(instruction.operands[0].imm,0x1809e0760)
        state={'r8':('mission',0,frozenset()),'rax':('constant',0x10000,frozenset())}
        self.assertEqual(scope['transfer'](instruction,state),state)

    def test_known_leaf_boundary_covers_both_returns(self):
        class PE:
            def get_data(self,rva,size):return bytes.fromhex('85c97403b001c3b002c3cccc')
        scope.update(pe=PE(),base=0x180000000,groups={},pdata_starts=[],pdata_ends={},
                     reviewed_leaf_ranges=[],md=md)
        self.assertTrue(scope['add_leaf_group'](0x180001000))
        self.assertEqual(scope['groups'][0x180001000]['runtimeRanges'][0]['endExclusive'],'0x18000100a')

    def test_leaf_boundary_rejects_padding_before_branch_target(self):
        class PE:
            def get_data(self,rva,size):return bytes.fromhex('7404c3ccccccb001c3')
        scope.update(pe=PE(),base=0x180000000,groups={},pdata_starts=[],pdata_ends={},
                     reviewed_leaf_ranges=[],md=md)
        self.assertFalse(scope['add_leaf_group'](0x180001000))

    def test_readonly_leaf_preserves_only_unwritten_registers(self):
        target=0x180600000
        insns={x.address:x for x in md.disasm(bytes.fromhex('488bd18b01c3'),target)}
        scope.update(groups={target:{}},leaf_register_effects={},decode=lambda address:(insns,{},[],[]))
        preserved=leaf_effect_function(target)
        self.assertIn('r9',preserved)
        self.assertNotIn('rax',preserved)
        self.assertNotIn('rdx',preserved)

    def test_nested_call_gets_no_preservation_summary(self):
        target=0x180600000
        for code in ['e800000000c3']:
            insns={x.address:x for x in md.disasm(bytes.fromhex(code),target)}
            scope.update(groups={target:{}},leaf_register_effects={},decode=lambda address:(insns,{},[],[]))
            self.assertEqual(leaf_effect_function(target),set())

    def test_leaf_memory_write_does_not_claim_register_write(self):
        target=0x180600000
        insns={x.address:x for x in md.disasm(bytes.fromhex('488901c3'),target)}
        scope.update(groups={target:{}},leaf_register_effects={},decode=lambda address:(insns,{},[],[]))
        self.assertIn('r10',leaf_effect_function(target))

    def test_register_summary_cannot_restore_escaped_spill(self):
        previous=scope['leaf_preserved_registers']
        scope['leaf_preserved_registers']=lambda target:{'r10','rcx'}
        try:
            state=run('48894c2440488d4c2440e800000000488b442440',
                      {'rsp':('stack',-128,frozenset()),'rcx':('mission',0,frozenset()),'r10':('agent',0,frozenset())})
            self.assertNotIn('rax',state)
            self.assertEqual(state['r10'][:2],('agent',0))
        finally:scope['leaf_preserved_registers']=previous


if __name__ == '__main__':
    unittest.main()
