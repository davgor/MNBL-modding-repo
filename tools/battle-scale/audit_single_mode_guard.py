"""Build an exact-build, offline-only review of the native Single-mode guard.

No target code is loaded or executed. The replacement is a leaf function guard:
TEST ECX,ECX; JZ original RET; INT29. Nonzero modes fail fast before returning.
The original store can be omitted because the global starts at zero and its
only other identified writer is the zero initializer, verified below.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "build/battle-scale/python-libs"))
import capstone
import pefile
from capstone.x86 import X86_OP_MEM, X86_REG_RIP
from patch_native_copy import safe_review_output
from native_instruction_semantics import memory_operand_writes

EXPECTED = "2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a"
SOURCE = Path("D:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord/bin/Win64_Shipping_Client/TaleWorlds.Native.dll")
SETTER = 0x180710C10
GLOBAL = 0x180DA61D4
ORIGINAL = bytes.fromhex("890dbe556900")
REPLACEMENT = bytes.fromhex("85c97402cd29")
REGISTRATION_EVIDENCE = "ev_9e9945bb81c2694f4088be078e0583675691e0d2cfd346c408cc24fb86f5c2b8"


def guard_outcome(value):
    """Win64 int argument: TEST considers all 32 bits, including unknown modes."""
    return "return-single" if value & 0xFFFFFFFF == 0 else "fail-fast"


def audit(output):
    data = SOURCE.read_bytes()
    if hashlib.sha256(data).hexdigest() != EXPECTED:
        raise ValueError("Native build mismatch")
    pe = pefile.PE(data=data)
    base = pe.OPTIONAL_HEADER.ImageBase
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True
    def raw(address, size):
        return pe.get_data(address-base, size)
    if raw(SETTER, 7) != ORIGINAL + b"\xc3":
        raise ValueError("Setter bytes/RET differ")
    replacement = list(md.disasm(REPLACEMENT + b"\xc3", SETTER))
    if [(i.mnemonic, i.op_str) for i in replacement] != [
        ("test", "ecx, ecx"), ("je", hex(SETTER+6)), ("int", "0x29"), ("ret", "")]:
        raise ValueError("Guard control flow differs")
    sec = pe.get_section_by_rva(GLOBAL-base)
    if not (sec.VirtualAddress + sec.SizeOfRawData <= GLOBAL-base and
            GLOBAL-base+4 <= sec.VirtualAddress + sec.Misc_VirtualSize):
        raise ValueError("Mode global is not wholly in loader-zeroed data")

    # Candidate enumeration at every byte offset, including non-pdata leafs and
    # instructions with immediates after disp32. False aligned candidates remain
    # visible; only verified true writers are accepted below. Also catch wider
    # stores whose starting address precedes this four-byte global.
    candidates = {}
    executable_bytes = 0
    for section in pe.sections:
        if not section.Characteristics & 0x20000000:
            continue
        block = section.get_data()
        va = base + section.VirtualAddress
        executable_bytes += len(block)
        for disp_end in range(4, len(block)+1):
            dest0 = int.from_bytes(block[disp_end-4:disp_end], "little", signed=True) + va + disp_end
            if not GLOBAL-23 <= dest0 <= GLOBAL+3:
                continue
            for tail in (0, 1, 2, 4, 8):
                end = disp_end + tail
                dest = dest0 + tail
                if not GLOBAL-15 <= dest <= GLOBAL+3 or end > len(block):
                    continue
                for start in range(max(0, end-15), disp_end-4):
                    decoded = list(md.disasm(block[start:end], va+start))
                    if len(decoded) != 1 or decoded[0].size != end-start:
                        continue
                    ins = decoded[0]
                    for operand_index,op in enumerate(ins.operands):
                        if op.type != X86_OP_MEM or op.mem.base != X86_REG_RIP:
                            continue
                        address = ins.address + ins.size + op.mem.disp
                        if address >= GLOBAL+4 or address+max(op.size, 1) <= GLOBAL:
                            continue
                        candidates[ins.address] = {"address": hex(ins.address), "bytes": ins.bytes.hex(),
                            "instruction": ins.mnemonic+" "+ins.op_str, "access": op.access,
                            "memoryAddress": hex(address), "memoryBytes": op.size,
                            "writes": memory_operand_writes(ins,operand_index)}
    writers = [c for c in candidates.values() if c["writes"]]
    if {int(c["address"], 16) for c in writers} != {0x18056EAA5, SETTER}:
        raise ValueError("Unexpected direct writer of native game mode: " + repr(writers))
    taken = [c for c in candidates.values() if c["instruction"].startswith("lea ")]
    relocations = []
    for page in pe.DIRECTORY_ENTRY_BASERELOC:
        for entry in page.entries:
            if entry.type == 10 and int.from_bytes(pe.get_data(entry.rva, 8), "little") == GLOBAL:
                relocations.append(hex(entry.rva))
    if taken or relocations:
        raise ValueError("Native game mode address escapes through direct LEA/relocation")
    constants = {"rva32": data.find((GLOBAL-base).to_bytes(4, "little")),
                 "va64": data.find(GLOBAL.to_bytes(8, "little"))}
    if any(offset != -1 for offset in constants.values()):
        raise ValueError("Native mode address has an additional RVA/VA constant reference")

    # Initializer is straight-line from XOR ESI,ESI through the store, with no
    # call/branch and no intervening write of any RSI alias.
    init = list(md.disasm(raw(0x18056E9D4, 0x18056EAAB-0x18056E9D4), 0x18056E9D4))
    if init[0].bytes.hex() != "33f6" or init[-1].address != 0x18056EAA5:
        raise ValueError("Initializer boundaries differ")
    for ins in init[1:]:
        if ins.group(capstone.CS_GRP_JUMP) or ins.group(capstone.CS_GRP_CALL) or ins.group(capstone.CS_GRP_RET):
            raise ValueError("Initializer is not straight-line")
        if any(ins.reg_name(r) in ("rsi", "esi", "si", "sil") for r in ins.regs_access()[1]):
            raise ValueError("Initializer zero register is overwritten")
    registration_path = ROOT / "evidence/engine-extension/native-registration.json"
    registration = json.loads(registration_path.read_text())
    text = registration["normalized_result"]
    if "0x18064671a: LEA RDX, [0x180710c10]" not in text or "0x180646721: MOV ECX, 0x2ab" not in text:
        raise ValueError("Callback683 registration changed")
    managed_path = ROOT / "evidence/engine-extension/pool-managed-expansion-audit.json"
    managed = json.loads(managed_path.read_text(encoding="utf-8-sig"))
    setter = next(m for m in managed["methods"] if "ScriptingInterfaceObjects::SetFunctionPointer" in m["signature"])
    switch = next(line for line in setter["il"] if ": switch " in line)
    if switch.split(": switch ", 1)[1].split(",")[683] != "IL_52b3":
        raise ValueError("Managed callback switch no longer maps683 to expected block")
    if not any("IL_52c8: stsfld" in line and "call_SetGameTypeDelegate" in line for line in setter["il"]):
        raise ValueError("Managed delegate join differs")
    bundle_path = ROOT / "evidence/engine-extension/native-closure-ghidra-evidence.json"
    bundle = json.loads(bundle_path.read_text())
    init_evidence = next(r for r in bundle["records"] if r["operation"] == "procedure_assembly" and
                         r.get("parameters", {}).get("procedure") == "0x18056eaa5")
    if "0x18056e9d4: XOR ESI, ESI" not in init_evidence["normalized_result"] or "0x18056eaa5: MOV dword ptr [0x180da61d4], ESI" not in init_evidence["normalized_result"]:
        raise ValueError("Retained REA initializer evidence differs")
    result = {"schemaVersion": 1, "sourceSha256": EXPECTED, "scope": "Exact installed build, execution-free native mode gate review",
        "status": "static-verified-runtime-unverified", "gameExecuted": False,
        "sourceEvidence": {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest() for p in (registration_path, managed_path, bundle_path)},
        "global": hex(GLOBAL), "loaderInitialValue": 0, "callbackId": 683,
        "modeNames": managed["gameTypeMetadata"],
        "patch": {"address": hex(SETTER), "original": ORIGINAL.hex(), "replacement": REPLACEMENT.hex(),
            "retAddress": hex(SETTER+6), "retBytes": "c3", "reason": "Reject every nonzero game type with native INT29 fail-fast; Single0 returns. Preserve the leaf ABI and original RET; zero initializer remains unchanged.",
            "evidenceIds": [REGISTRATION_EVIDENCE, init_evidence["evidence_id"]]},
        "abi": {"argument": "ECX int32", "stackModified": False, "nonvolatileRegistersModified": False,
            "flags": "TEST changes volatile arithmetic flags", "newBranchTarget": hex(SETTER+6),
            "newInstructions": [{"address": hex(i.address), "bytes": i.bytes.hex(), "instruction": i.mnemonic+" "+i.op_str} for i in replacement]},
        "directWriterAudit": {"executableBytesScanned": executable_bytes, "allByteOffsetsExamined": True,
            "trailingImmediateWidths": [0, 1, 2, 4, 8], "overlappingStoreStartsIncluded": True,
            "candidates": sorted(candidates.values(), key=lambda c: c["address"]), "writers": writers,
            "directAddressTaking": taken, "absolutePointerRelocations": relocations,
            "literalAddressFirstFileOffsets": constants,
            "zeroInitializer": [{"address": hex(i.address), "bytes": i.bytes.hex(), "instruction": i.mnemonic+" "+i.op_str} for i in init]},
        "outcomes": {str(v): guard_outcome(v) for v in (0, 1, 2, 3, 4, 5, 6, -1, 0x7fffffff, -0x80000000)},
        "limitations": ["This is a fail-fast experimental test restriction, not a graceful multiplayer UI feature.",
            "Direct native references and exact callback registration are audited; arbitrary third-party code that writes process memory is outside this profile.",
            "Native fail-fast handling has not been executed in the game; the user has held all game launches."]}
    output = safe_review_output(output, [SOURCE, registration_path, managed_path, bundle_path])
    output.write_text(json.dumps(result, indent=2)+"\n", encoding="utf-8")
    print(json.dumps({"output": str(output), "status": result["status"], "directWriterCount": len(writers)}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT/"evidence/engine-extension/native-single-mode-guard.json")
    audit(parser.parse_args().output)
