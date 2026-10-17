"""Conservative x86 memory-store classification for static proof consumers."""
import capstone
from capstone.x86 import X86_OP_MEM


def memory_operand_writes(instruction, operand_index):
    """Treat explicit MOV/VMOV destinations as stores despite Capstone 5 flags.

    Capstone reports READ for some SIMD memory destinations. Intel operand order
    supplies the independent store fact; ordinary reported writes remain valid.
    This helper does not infer aliasing, size, or implicit string-store effects.
    """
    operand = instruction.operands[operand_index]
    return operand.type == X86_OP_MEM and bool(
        operand.access & capstone.CS_AC_WRITE
        or (operand_index == 0 and (
            instruction.mnemonic.startswith(('mov', 'vmov', 'fst', 'fist'))
            or instruction.mnemonic in ('stmxcsr', 'vstmxcsr', 'fxsave', 'fxsave64',
                                        'xsave', 'xsave64', 'xsavec', 'xsaveopt')
        ))
    )
