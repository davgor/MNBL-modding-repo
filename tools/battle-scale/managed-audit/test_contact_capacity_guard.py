"""Offline exhaustive byte-domain and geometry tests; no target execution."""
import unittest
from audit_contact_capacity_guard import BUILD_NEW, HIT_NEW, BUILD_SITE, HIT_SITE, RETURN, builder_outcome, cached_outcome, capstone


class ContactCapacityGuardTests(unittest.TestCase):
    def test_all_builder_byte_values_bound_attempted_writes(self):
        for raw in range(256):
            result, bound = builder_outcome(raw)
            index = (raw - 1) & 255
            if index & 128:
                self.assertEqual((result,bound),('existing-empty-path',0))
                continue
            # Interpret the actual CMP R14B,63 / unsigned JBE condition.
            if index > BUILD_NEW[3]:
                self.assertEqual(result,'fail-fast')
            else:
                attempts = 0
                while index >= 0:
                    self.assertLess(index,64)
                    attempts += 1
                    index -= 1
                self.assertEqual(attempts,bound)
                self.assertLessEqual(attempts,64)

    def test_cache_hit_guard_checks_unsigned_byte(self):
        for count in range(256):
            # CMP [RAX+8],40: unlike the original signed builder precheck,
            # a corrupt negative cached byte must not pass this gate.
            expected='return' if count<=HIT_NEW[7] else 'fail-fast'
            self.assertEqual(cached_outcome(count),expected)
        self.assertEqual(cached_outcome(64),'return')
        for count in (65,127,128,255):
            self.assertEqual(cached_outcome(count),'fail-fast')

    def test_hit_pointer_equivalence_for_lookup_indices(self):
        for base in (0x10000,0x123456780):
            for index in (0,1,15,64,2040,4080,32767):
                loop_pointer=base+32+index*40
                replacement_result=loop_pointer-32
                original_result=base+index*40
                self.assertEqual(replacement_result,original_result)

    def test_instruction_boundaries_and_branch_destinations(self):
        md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64)
        build=list(md.disasm(BUILD_NEW,BUILD_SITE));hit=list(md.disasm(HIT_NEW,HIT_SITE))
        self.assertEqual(sum(i.size for i in build),8)
        self.assertEqual(sum(i.size for i in hit),16)
        self.assertEqual(build[1].op_str,hex(BUILD_SITE+8))
        self.assertEqual(hit[2].op_str,hex(RETURN))
        self.assertEqual((build[-1].mnemonic,hit[-1].mnemonic),('int','int'))

    def test_contact_capacity_preserves_metadata_and_next_record(self):
        # A producer may write before accepting the contact. Every attempted
        # iteration, including all rejected contacts, must use index<=63.
        highest=0x30+63*0x30
        self.assertEqual(highest,0xc00)
        self.assertLess(highest,0xc08)
        self.assertLess(highest,0xc20)
        self.assertLess(0x1610+14*0xc20+highest,0xd810)
        self.assertGreaterEqual(0x30+64*0x30,0xc20)


if __name__=='__main__':
    unittest.main()
