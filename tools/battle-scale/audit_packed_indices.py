"""Find possible 11-bit index operations in native combat code; no rewrite approval."""
import hashlib
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "build/battle-scale/python-libs"))
import capstone
from capstone.x86 import X86_OP_IMM
import pefile


def main():
    inventory = json.loads((ROOT / "evidence/engine-extension/native-pool-reference-inventory.json").read_text())
    data = Path(inventory["binary"]["path"]).read_bytes()
    if hashlib.sha256(data).hexdigest() != inventory["binary"]["sha256"]:
        raise ValueError("Target changed")
    pe = pefile.PE(data=data)
    decoder = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    decoder.detail = True
    sites, gaps = [], []
    for entry in pe.DIRECTORY_ENTRY_EXCEPTION:
        begin, end = entry.struct.BeginAddress, entry.struct.EndAddress
        if not 0x5B0000 <= begin < 0x720000:
            continue
        instructions = list(decoder.disasm(pe.get_data(begin, end-begin), pe.OPTIONAL_HEADER.ImageBase+begin))
        if not instructions or instructions[-1].address + instructions[-1].size != pe.OPTIONAL_HEADER.ImageBase+end:
            gaps.append(hex(pe.OPTIONAL_HEADER.ImageBase+begin))
        for index, instruction in enumerate(instructions):
            immediates = [o.imm for o in instruction.operands if o.type == X86_OP_IMM]
            match = (instruction.mnemonic in ("and", "cmp") and any(v in (0x7FE, 0x7FF, 0x800) for v in immediates)) or (instruction.mnemonic in ("shl", "shr", "sar") and 11 in immediates)
            if not match:
                continue
            sites.append({"address": hex(instruction.address), "rangeStart": hex(pe.OPTIONAL_HEADER.ImageBase+begin),
                          "bytes": instruction.bytes.hex(), "instruction": instruction.mnemonic+" "+instruction.op_str,
                          "context": [{"address": hex(x.address), "instruction": x.mnemonic+" "+x.op_str} for x in instructions[max(0,index-3):index+4]],
                          "status": "numeric-candidate-not-agent-index-proof"})
    result = {"schemaVersion": 1, "targetSha256": inventory["binary"]["sha256"],
              "scope": "PE exception-directory ranges starting in RVA5b0000..720000; AND/CMP2046,2047,2048 and shifts by11.",
              "limitations": ["This is a supplemental search, not proof that all packed indices have been found.",
                              "Flags, floating-point conversions, batching and unrelated fields can match.",
                              "Uncovered executable bytes, indirect computations and other modules are outside this scan."],
              "sites": sites, "decodeGapRanges": gaps}
    (ROOT / "evidence/engine-extension/native-packed-index-candidates.json").write_text(json.dumps(result, indent=2)+"\n")
    print(json.dumps({"sites": len(sites), "decodeGapRanges": len(gaps)}))


if __name__ == "__main__":
    main()
