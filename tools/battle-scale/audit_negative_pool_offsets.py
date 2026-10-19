"""Supplement positive operand inventory with negative layout-sized operands.

This is a numeric search, not object provenance or patch approval.
"""
import hashlib
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "build/battle-scale/python-libs"))
import capstone
from capstone.x86 import X86_OP_IMM, X86_OP_MEM, X86_REG_RIP, X86_REG_EIP
import pefile


def main():
    inventory_path = ROOT / "evidence/engine-extension/native-pool-reference-inventory.json"
    raw_inventory = inventory_path.read_bytes()
    inventory = json.loads(raw_inventory)
    source = Path(inventory["binary"]["path"]).read_bytes()
    assert hashlib.sha256(source).hexdigest() == inventory["binary"]["sha256"]
    pe = pefile.PE(data=source)
    base = pe.OPTIONAL_HEADER.ImageBase
    ranges = sorted(set((x.struct.BeginAddress, x.struct.EndAddress, "pdata") for x in pe.DIRECTORY_ENTRY_EXCEPTION))
    ranges += [(int(x["rvaStart"],16), int(x["rvaEndExclusive"],16), "uncovered") for x in inventory["coverage"]["uncoveredExecutableRanges"]]
    decoder = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    decoder.detail = True
    decoder.skipdata = True
    matches = []
    skips = 0
    for start, end, kind in ranges:
        disrupted = kind != "pdata"
        for inst in decoder.disasm(pe.get_data(start, end-start), base+start):
            if not inst.id:
                skips += inst.size
                disrupted = True
                continue
            for index, op in enumerate(inst.operands):
                if op.type == X86_OP_MEM and op.mem.base not in (X86_REG_RIP, X86_REG_EIP):
                    value, encoding, size, role = op.mem.disp, inst.disp_offset, inst.disp_size, "memory_displacement"
                elif op.type == X86_OP_IMM:
                    value, encoding, size, role = op.imm, inst.imm_offset, inst.imm_size, "immediate"
                    if 0x80000000 <= value <= 0xffffffff and size == 4:
                        value -= 0x100000000
                else:
                    continue
                if -0x1184b40 <= value <= -0x116e80:
                    matches.append({"address": hex(inst.address), "functionStart": hex(base+start) if kind=="pdata" else None,
                                    "rangeSource":kind,"lowerConfidence":disrupted,"bytes":inst.bytes.hex(),
                                    "fileOffset":pe.get_offset_from_rva(inst.address-base),"mnemonic":inst.mnemonic,"operands":inst.op_str,
                                    "operandIndex":index,"kind":role,"value":value,"encodedOffset":encoding,"encodedSize":size})
    result = {"schemaVersion":1,"status":"negative-numeric-candidates-not-patch-approval", "binary":inventory["binary"],
              "sourceInventorySha256":hashlib.sha256(raw_inventory).hexdigest(),"rangeCount":len(ranges),
              "skippedDecodeBytes":skips,"matches":matches,
              "limitations":["Search covers decoded negative operands in the stated magnitude interval only.",
                             "Computed offsets, metadata, loaded constants and algebraic decompositions are not ruled out.",
                             "Linear decoding and .pdata coverage have the limitations of the primary inventory; no target code executes."]}
    output=ROOT / "evidence/engine-extension/native-pool-negative-offsets.json"
    output.write_text(json.dumps(result,indent=2)+"\n")
    print(json.dumps({"output":str(output),"matches":len(matches),"skippedDecodeBytes":skips}))


if __name__ == "__main__":
    main()
