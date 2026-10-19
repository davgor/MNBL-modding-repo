"""Check the emitted page geometry and upload extents at every page boundary."""
import json
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'build/battle-scale/python-libs'))
import capstone


class BoneMappingGeometryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.patch = json.loads((ROOT / 'evidence/engine-extension/native-bone-mapping-patch.json').read_text())
        cls.edits = {int(x['rva'], 16): x for x in cls.patch['edits']}
        cls.decoder = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        cls.decoder.detail = True

    def instruction(self, rva):
        return next(self.decoder.disasm(bytes.fromhex(self.edits[rva]['replacementBytes']), 0x180000000+rva))

    def test_all_writer_and_upload_shifts_use_same_page_geometry(self):
        for rva in (0x886c8, 0x886d1, 0x88778, 0x8877b, 0x88792, 0x887db,
                    0x887e2, 0x1e09e1, 0x1e09e6):
            with self.subTest(rva=hex(rva)):
                self.assertEqual(self.instruction(rva).operands[-1].imm, 14)
        self.assertEqual(self.instruction(0x8870a).operands[-1].imm, 1 << 16)
        self.assertEqual(self.instruction(0x1e0b34).operands[-1].imm, 1 << 16)

    def test_every_index_resolves_to_a_bounded_dword_in_one_of_32_pages(self):
        shift = self.instruction(0x886c8).operands[-1].imm
        page_bytes = self.instruction(0x8870a).operands[-1].imm
        for index in range(self.patch['mappingCapacityEntries']):
            page = index >> shift
            offset = (index - (page << shift)) * 4
            if not (page < 32 and 0 <= offset <= page_bytes-4):
                self.fail((index, page, offset))
        self.assertEqual(self.patch['mappingCapacityEntries'] >> shift, 32)

    def test_reservations_crossing_each_boundary_fit_allocated_pages(self):
        shift = self.instruction(0x886c8).operands[-1].imm
        size = 1 << shift
        for boundary in range(1, 33):
            for start in range(boundary*size-127, boundary*size+1):
                for count in (1, 2, 63, 127):
                    last = (start+count-1) >> shift
                    accepted = last < 32
                    self.assertEqual(accepted, start+count <= 32*size)
                    if accepted:
                        self.assertLessEqual(start >> shift, last)

    def test_upload_page_chunks_exactly_cover_used_bytes(self):
        page_bytes = self.instruction(0x1e0b34).operands[-1].imm
        shift = self.instruction(0x1e09e1).operands[-1].imm
        for pages in range(1, 33):
            for delta in (-1, 0, 1):
                count = (pages << shift)+delta
                if not 0 < count <= self.patch['mappingCapacityEntries']:
                    continue
                remaining = count*4
                chunks = []
                while remaining > 0:
                    chunks.append(min(remaining, page_bytes))
                    remaining -= page_bytes  # Actual command type12 subtracts full page size.
                self.assertEqual(sum(chunks), count*4)
                self.assertLessEqual(len(chunks), 32)
                rounded_count = (((count-1) >> shift)+1) << shift
                self.assertLessEqual(count*4, rounded_count*4)
                self.assertEqual(len(chunks)*page_bytes, rounded_count*4)


if __name__ == '__main__':
    unittest.main()
