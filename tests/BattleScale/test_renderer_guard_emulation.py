"""Execute only isolated guard bytes in an x64 emulator, never in Bannerlord."""
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'build/battle-scale/python-libs'))
from unicorn import Uc, UcError, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE, UC_ERR_INSN_INVALID
from unicorn import x86_const as x

INDEX_OLD = bytes.fromhex('4d3bf70f8781000000488d9d080100004d2bfe4903de4c8d6d0849ffc74d8d6cc500')
INDEX_NEW = bytes.fromhex('4183ff2072020f0b4d39fe777d4a8d9c35080100004d2bfe4c8d6cc50849ffc79090')
MATRIX_OLD = bytes.fromhex('488d99080100004803de4c8d710848ffc74d8d34c6660f1f440000')
MATRIX_NEW = bytes.fromhex('8d1c3783fb1f76020f0b488d9c31080100004c8d74c10848ffc790')
REGS = {name: getattr(x, 'UC_X86_REG_' + name.upper()) for name in
        ('rax', 'rbx', 'rcx', 'rdx', 'rsi', 'rdi', 'rbp', 'rsp', 'r8', 'r9', 'r10', 'r11', 'r12', 'r13', 'r14', 'r15')}


def execute(code, address, initial, stop):
    cpu = Uc(UC_ARCH_X86, UC_MODE_64)
    cpu.mem_map(address & ~0xfff, 0x2000)
    cpu.mem_write(address, code)
    for name, value in initial.items():
        cpu.reg_write(REGS[name], value)
    reached = []
    def visit(uc, pc, size, user):
        if pc in stop:
            reached.append(pc)
            uc.emu_stop()
    cpu.hook_add(UC_HOOK_CODE, visit)
    try:
        cpu.emu_start(address, address + 0x1000, count=40)
    except UcError as error:
        if error.errno != UC_ERR_INSN_INVALID:
            raise
        return 'invalid-instruction', cpu.reg_read(x.UC_X86_REG_RIP), None
    if len(reached) != 1:
        raise AssertionError('Guard did not reach an expected boundary')
    return 'boundary', reached[0], {name: cpu.reg_read(reg) for name, reg in REGS.items()}


class RendererGuardEmulationTests(unittest.TestCase):
    def initial(self, first, last, matrix=False):
        values = {name: 0x5a5a00000000 + n * 0x123 for n, name in enumerate(REGS)}
        values.update(rax=first, rbp=0x1234567800, rcx=0x1234567800,
                      r14=first, r15=last, rsi=first, rdi=last-first if matrix else 0x2234567800)
        return values

    def test_index_all_valid_pages_preserve_registers_and_destination(self):
        for first in range(32):
            for last in range(first, 32):
                with self.subTest(first=first, last=last):
                    state = self.initial(first, last)
                    stop = {0x1800886f7, 0x18008875f}
                    old = execute(INDEX_OLD, 0x1800886d5, state, stop)
                    new = execute(INDEX_NEW, 0x1800886d5, state, stop)
                    self.assertEqual(new, old)

    def test_index_original_empty_range_skip_preserved(self):
        for first, last in ((1, 0), (31, 30), (0xffffffff, 31)):
            state = self.initial(first, last)
            stop = {0x1800886f7, 0x18008875f}
            self.assertEqual(execute(INDEX_NEW, 0x1800886d5, state, stop),
                             execute(INDEX_OLD, 0x1800886d5, state, stop))

    def test_index_out_of_range_traps_before_page_pointer_setup(self):
        for last in (32, 33, 255, 256, 0x3ffff, 0x1fffff):
            outcome = execute(INDEX_NEW, 0x1800886d5, self.initial(0, last), {0x1800886f7, 0x18008875f})
            self.assertEqual(outcome[:2], ('invalid-instruction', 0x1800886db))

    def test_original_zero_reservation_bypass_does_not_enter_guard(self):
        # Original 886a0..886ae branch is deliberately outside the replacement.
        code = bytes.fromhex('4585e4750a418d7424ffe9ba000000')
        state = self.initial(0, 0); state['r12'] = 0
        result = execute(code, 0x1800886a0, state, {0x1800886af, 0x180088769})
        self.assertEqual(result[1], 0x180088769)
        self.assertEqual(result[2]['rsi'], 0xffffffff)

    def test_matrix_all_valid_pages_preserve_general_registers(self):
        # Arithmetic flags are dead: the original next MOV/TEST overwrites them.
        for first in range(32):
            for last in range(first, 32):
                state = self.initial(first, last, matrix=True)
                old = execute(MATRIX_OLD, 0x180089925, state, {0x180089940})
                new = execute(MATRIX_NEW, 0x180089925, state, {0x180089940})
                self.assertEqual(new, old)

    def test_matrix_out_of_range_traps_before_page_pointer_setup(self):
        for first, last in ((0, 32), (31, 32), (32, 32), (1, 100), (0, 0x7ffff)):
            result = execute(MATRIX_NEW, 0x180089925, self.initial(first, last, True), {0x180089940})
            self.assertEqual(result[:2], ('invalid-instruction', 0x18008992d))


if __name__ == '__main__':
    unittest.main()
