"""Bounded literal cross-check in the29 other inventoried native PE modules.

This looks for the concrete large offsets selected by the current review
manifest. It is not proof about computed addresses or opaque callback data.
It never executes a module or changes any installed file.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import struct
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT/"build/battle-scale/python-libs"))
import capstone
import pefile
from patch_native_copy import safe_review_output


def operand_values(manifest):
    return sorted({c["originalValue"] for p in manifest["patches"] for c in p.get("operandChanges", [])
                   if 0x100000 <= c["originalValue"] <= 0x1184B40})


def value_digest(values):
    return hashlib.sha256(b"".join(struct.pack("<I", value) for value in values)).hexdigest()


def audit(manifest_path, output):
    boundary_path = ROOT/"evidence/engine-extension/native-pool-module-boundaries.json"
    manifest = json.loads(manifest_path.read_text())
    boundary = json.loads(boundary_path.read_text())
    values = operand_values(manifest)
    if not values:
        raise ValueError("No reviewed layout operands")
    # Lookahead preserves overlapping raw-byte matches. Local decodes may include
    # false instruction starts; every candidate remains in the report.
    pattern = re.compile(b"(?=("+b"|".join(re.escape(struct.pack("<I", v)) for v in values)+b"))")
    decoder = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    decoder.detail = True
    rows = []
    paths = []
    for item in boundary["nativeModules"]:
        if item["name"] == "TaleWorlds.Native.dll":
            if item["sha256"] != manifest["sourceSha256"]:
                raise ValueError("Manifest/module inventory native identity differs")
            continue
        path = Path(item["path"])
        paths.append(path)
        data = path.read_bytes()
        if hashlib.sha256(data).hexdigest() != item["sha256"]:
            raise ValueError("External module changed: "+str(path))
        pe = pefile.PE(data=data, fast_load=True)
        hits, raw_matches, executable_bytes = [], 0, 0
        seen = set()
        for section in pe.sections:
            if not section.Characteristics & 0x20000000:
                continue
            block = section.get_data()
            va = pe.OPTIONAL_HEADER.ImageBase+section.VirtualAddress
            executable_bytes += len(block)
            for match in pattern.finditer(block):
                raw_matches += 1
                at, value = match.start(), struct.unpack("<I", match.group(1))[0]
                for start in range(max(0, at-11), at):
                    seq = list(decoder.disasm(block[start:start+15], va+start, count=1))
                    if not seq:
                        continue
                    ins = seq[0]
                    for index, operand in enumerate(ins.operands):
                        numeric_match = ((operand.type == capstone.x86.X86_OP_IMM and operand.imm == value) or
                            (operand.type == capstone.x86.X86_OP_MEM and operand.mem.disp == value and operand.mem.base != capstone.x86.X86_REG_RIP))
                        if not numeric_match or (ins.address, index) in seen:
                            continue
                        seen.add((ins.address, index))
                        hits.append({"address": hex(ins.address), "bytes": ins.bytes.hex(),
                            "instruction": ins.mnemonic+" "+ins.op_str, "operandIndex": index, "value": hex(value)})
        rows.append({"name": item["name"], "path": str(path), "sha256": item["sha256"],
                     "executableBytes": executable_bytes, "rawLiteralMatches": raw_matches, "candidates": hits})
    report = {"schemaVersion": 1, "targetSha256": manifest["sourceSha256"],
        "sourceManifestSha256AtExtraction": hashlib.sha256(manifest_path.read_bytes()).hexdigest(),
        "inventorySha256": hashlib.sha256(boundary_path.read_bytes()).hexdigest(),
        "operandSetSha256": value_digest(values), "reviewedValues": [hex(v) for v in values],
        "modules": rows, "moduleCount": len(rows), "instructionOperandCandidates": sum(len(r["candidates"]) for r in rows),
        "targetExecuted": False,
        "scope": "Executable sections of all29 other native/mixed PE modules in the retained shipping-bin inventory; every byte position and possible15-byte instruction start around each exact32-bit operand literal.",
        "limitations": ["Only the selected literal operand set is tested. Computed/split addresses, aliases and opaque callback user data require separate ownership evidence.",
            "Manifest metadata may change without invalidating this scan if operandSetSha256 still matches; newly selected operands require rerunning it.",
            "Third-party mods and files outside the inventoried shipping-bin directory are outside this test profile."]}
    output = safe_review_output(output, paths+[manifest_path, boundary_path])
    output.write_text(json.dumps(report, indent=2)+"\n", encoding="utf-8")
    print(json.dumps({"output": str(output), "moduleCount": len(rows), "valueCount": len(values),
                      "instructionOperandCandidates": report["instructionOperandCandidates"]}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, default=ROOT/"evidence/engine-extension/native-pool-patch-4080.json")
    parser.add_argument("--output", type=Path, default=ROOT/"evidence/engine-extension/native-external-layout-literals.json")
    args = parser.parse_args()
    audit(args.manifest, args.output)
