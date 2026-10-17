import sys,unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
sys.path[:0]=[str(ROOT/'tools/battle-scale'),str(ROOT/'build/battle-scale/python-libs')]
import capstone
from capstone.x86 import X86_OP_IMM
from native_offset_use_flow import trace_uses

def run(raw):
    md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
    insns={i.address:i for i in md.disasm(bytes.fromhex(raw),0x1000)}
    def canonical(i,r):
        n=i.reg_name(r)
        aliases={'eax':'rax','ax':'rax','al':'rax','edx':'rdx','dx':'rdx','dl':'rdx',
                 'ecx':'rcx','cx':'rcx','cl':'rcx','ebx':'rbx','bx':'rbx','bl':'rbx',
                 'esi':'rsi','edi':'rdi','esp':'rsp','ebp':'rbp','eflags':'rflags'}
        return aliases.get(n,n)
    succ={}
    for a,i in insns.items():
        nxt=a+i.size;targets=[]
        if i.group(capstone.CS_GRP_JUMP):
            if i.operands[0].type==X86_OP_IMM:targets.append(i.operands[0].imm)
            if i.mnemonic!='jmp':targets.append(nxt)
        elif not i.group(capstone.CS_GRP_RET):targets.append(nxt)
        succ[a]=targets
    def stack(a,op):
        return op.mem.disp if insns[a].reg_name(op.mem.base)=='rsp' and not op.mem.index else None
    result=trace_uses(insns,succ,0x1000,canonical,stack)
    return result,{e['kind'] for e in result['events']}

class OffsetFlow(unittest.TestCase):
    def test_dereference_does_not_taint_loaded_scalar(self):
        r,k=run('4869c0600c0000 8b0410 c3')
        self.assertIn('memory-read-address',k);self.assertNotIn('return-escape',k)
    def test_shared_offset_is_two_addresses(self):
        r,k=run('4869c0600c0000 8b0c10 8b1403 31c0 c3')
        self.assertEqual(sum(e['kind']=='memory-read-address' for e in r['events']),2)
    def test_pointer_copy_and_call(self):
        r,k=run('4869c0600c0000 488d0c10 e800000000 31c0 c3')
        self.assertIn('address-computation',k);self.assertIn('call-escape',k)
    def test_same_base_and_store_value_still_escapes(self):
        r,k=run('4869c0600c0000 488900 31c0 c3')
        self.assertIn('memory-write-address',k);self.assertIn('persistent-store-escape',k)
    def test_spill_reload_after_call_is_gap_not_proof(self):
        r,k=run('4869c0600c0000 4889442440 e800000000 488b442440 c3')
        self.assertTrue({'stack-spill','stack-reload','callee-stack-alias-gap','return-escape'}<=k)
        self.assertFalse(r['hookApproved'])
    def test_narrow_result_never_approved(self):
        r,k=run('4869c0600c0000 89c1 31c0 c3')
        self.assertIn('narrow-value-use',k)
    def test_flags_survive_increment_carry(self):
        r,k=run('4869c0600c0000 ffc2 7202 31c0 c3')
        self.assertTrue(any(e['kind']=='flags-use' and e['detail']=='cf' for e in r['events']))
    def test_flags_reset_by_xor(self):
        r,k=run('4869c0600c0000 31d2 7202 31c0 c3')
        self.assertNotIn('flags-use',k)
    def test_unknown_store_keeps_possible_spill(self):
        r,k=run('4869c0600c0000 4889442440 488912 488b442440 c3')
        self.assertTrue({'unknown-store-alias-gap','stack-reload','return-escape'}<=k)
    def test_loop_redefinition_terminates(self):
        r,k=run('4869c0600c0000 8b0c10 75f4 31c0 c3')
        self.assertIn('memory-read-address',k);self.assertLess(r['fixpointSteps'],100)
    def test_setcc_preserves_tainted_upper_bytes(self):
        r,k=run('4869c0600c0000 0f95c0 488b10 31c0 c3')
        self.assertIn('partial-register-write',k);self.assertIn('memory-read-address',k)
    def test_stack_rmw_keeps_taint(self):
        r,k=run('4869c0600c0000 4889442440 b800000000 488344244001 488b4c2440 e800000000 c3')
        self.assertIn('stack-rmw',k);self.assertIn('call-escape',k)
    def test_volatile_is_may_preserved(self):
        r,k=run('4869c0600c0000 4989c2 31c0 e800000000 4c89d0 c3')
        self.assertIn('callee-result-survival-gap',k);self.assertIn('return-escape',k)
    def test_xmm_return_escapes(self):
        r,k=run('4869c0600c0000 66480f6ec0 31c0 c3')
        self.assertTrue(any(e['kind']=='return-escape' and e['detail']=='xmm0' for e in r['events']))
    def test_unknown_load_alias_gap(self):
        r,k=run('4869c0600c0000 4889442440 31c0 488b0a e800000000 c3')
        self.assertIn('unknown-load-alias-gap',k);self.assertIn('call-escape',k)
    def test_flags_to_boolean(self):
        r,k=run('4869c0600c0000 4839d0 0f94c1 31c0 e800000000 c3')
        self.assertIn('flags-derived-value',k);self.assertIn('call-escape',k)
    def test_arithmetic_memory_escape(self):
        r,k=run('4869c0600c0000 480103 31c0 c3')
        self.assertIn('persistent-store-escape',k)
    def test_external_tail_keeps_explicit_gap(self):
        r,k=run('4869c0600c0000 e900100000')
        self.assertIn('external-control-flow',k)
    def test_missing_fallthrough_keeps_gap(self):
        r,k=run('4869c0600c0000 4889c1')
        self.assertIn('external-control-flow',k)
    def test_conditional_write_retains_false_path(self):
        r,k=run('4869c0600c0000 480f45c2 c3')
        self.assertIn('unsupported-overwrite-gap',k);self.assertIn('return-escape',k)
    def test_scalar_simd_preserves_possible_upper_bits(self):
        r,k=run('4869c0600c0000 66480f6ec0 31c0 f2480f2ac1 c3')
        self.assertIn('unsupported-overwrite-gap',k)
        self.assertTrue(any(e['kind']=='return-escape' and e['detail']=='xmm0' for e in r['events']))
    def test_saved_offset_comparison_derives_flags(self):
        r,k=run('4869c0600c0000 4889442440 b800000000 48837c244000 0f94c1 e800000000 c3')
        self.assertIn('flags-derived-value',k);self.assertIn('call-escape',k)
    def test_pop_implicit_reload_gap(self):
        r,k=run('4869c0600c0000 48890424 b800000000 58 c3')
        self.assertIn('implicit-stack-read-gap',k);self.assertIn('return-escape',k)
    def test_carry_derived_flags_flow(self):
        r,k=run('4869c0600c0000 11d1 0f94c2 b800000000 e800000000 c3')
        self.assertTrue(any(e['kind']=='flags-use' and e['detail']=='zf' for e in r['events']))
    def test_avx_width_is_gap(self):
        r,k=run('4869c0600c0000 c5fc28c1 c3')
        self.assertIn('unsupported-vector-width-gap',k)
    def test_indirect_tail_with_untainted_target(self):
        r,k=run('4869c9600c0000 ff22')
        self.assertIn('external-tail-gap',k);self.assertIn('call-escape',k)

if __name__=='__main__':unittest.main()
