"""Read-only audit of Mission/pool C++ exception metadata and cleanup code."""
from __future__ import annotations
import argparse
from collections import Counter, defaultdict
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import struct
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "build/battle-scale/python-libs"))
import pefile
import capstone
from capstone.x86 import X86_OP_REG, X86_OP_IMM, X86_OP_MEM, X86_REG_RIP


def audit(output):
    inventory_path = ROOT / "evidence/engine-extension/native-pool-reference-inventory.json"
    inventory_bytes = inventory_path.read_bytes()
    inv = json.loads(inventory_bytes)
    binary_path = Path(inv["binary"]["path"])
    binary_bytes = binary_path.read_bytes()
    if hashlib.sha256(binary_bytes).hexdigest() != inv["binary"]["sha256"]:
        raise ValueError("Native file differs from hash-bound inventory")
    pe = pefile.PE(data=binary_bytes)
    base = pe.OPTIONAL_HEADER.ImageBase
    dis = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    dis.detail = True
    runtime = {e.struct.BeginAddress: e.struct for e in pe.DIRECTORY_ENTRY_EXCEPTION}
    inventory_addresses = {int(c["address"], 16) for c in inv["candidates"]}
    imports = {i.address: {"dll": e.dll.decode(), "symbol": i.name.decode() if i.name else str(i.ordinal)}
               for e in pe.DIRECTORY_ENTRY_IMPORT for i in e.imports}

    def blob(rva, size):
        return {"rva": hex(rva), "address": hex(base + rva), "fileOffset": hex(pe.get_offset_from_rva(rva)),
                "size": size, "bytes": pe.get_data(rva, size).hex()}

    def instruction(ins):
        result = {"address": hex(ins.address), "rva": hex(ins.address - base),
                  "fileOffset": hex(pe.get_offset_from_rva(ins.address - base)), "bytes": ins.bytes.hex(),
                  "mnemonic": ins.mnemonic, "operands": ins.op_str,
                  "presentInOriginalNumericInventory": ins.address in inventory_addresses}
        if ins.imm_size:
            result["encodedImmediate"] = {"offset": ins.imm_offset, "size": ins.imm_size,
                                          "bytes": ins.bytes[ins.imm_offset:ins.imm_offset + ins.imm_size].hex()}
        if ins.disp_size:
            result["encodedDisplacement"] = {"offset": ins.disp_offset, "size": ins.disp_size,
                                             "bytes": ins.bytes[ins.disp_offset:ins.disp_offset + ins.disp_size].hex()}
        return result

    def instructions(rva, size=None, stop=True):
        if size is None:
            size = runtime[rva].EndAddress - rva if rva in runtime else 128
        result = []
        for ins in dis.disasm(pe.get_data(rva, size), base + rva):
            result.append(ins)
            if stop and ins.mnemonic in ("jmp", "ret", "int3"):
                break
        return result

    def unwind_header(entry):
        rva = entry.UnwindData
        head = pe.get_data(rva, 4)
        flags = head[0] >> 3
        tail = rva + 4 + ((head[2] + 1) & ~1) * 2
        result = {"unwindRva": hex(rva), "flags": flags, "prologSize": head[1], "unwindCodeSlots": head[2],
                  "frameRegisterNumber": head[3] & 15, "frameOffsetScaled": head[3] >> 4,
                  "encodedHeaderAndCodes": blob(rva, tail - rva)}
        if flags & 3 and not flags & 4:
            handler = struct.unpack("<I", pe.get_data(tail, 4))[0]
            thunk = next(iter(instructions(handler)), None)
            imported = None
            if thunk and thunk.mnemonic == "jmp" and thunk.operands[0].type == X86_OP_MEM and thunk.operands[0].mem.base == X86_REG_RIP:
                imported = imports.get(thunk.address + thunk.size + thunk.operands[0].mem.disp)
            result.update({"handlerRva": hex(handler), "handlerThunk": instruction(thunk) if thunk else None,
                           "handlerImport": imported, "handlerDataRva": hex(tail + 4),
                           "encodedHandlerRva": blob(tail, 4)})
        return result

    def cpp_info(start):
        entry = runtime[start]
        header = unwind_header(entry)
        if header.get("handlerImport", {}).get("symbol") != "__CxxFrameHandler3":
            raise ValueError("Unexpected C++ handler at " + hex(start))
        data_rva = int(header["handlerDataRva"], 16)
        fi = struct.unpack("<I", pe.get_data(data_rva, 4))[0]
        fields = struct.unpack("<10I", pe.get_data(fi, 40))
        if fields[0] != 0x19930522 or fields[1] > 1000:
            raise ValueError("Unexpected FuncInfo format")
        actions = []
        for index in range(fields[1]):
            map_rva = fields[2] + index * 8
            to_state, action = struct.unpack("<iI", pe.get_data(map_rva, 8))
            body = instructions(action) if action else []
            actions.append({"state": index, "toState": to_state, "actionAddress": hex(base + action) if action else None,
                            "mapEntry": blob(map_rva, 8), "actionInstructions": [instruction(i) for i in body],
                            "actionHasOwnPdataEntry": action in runtime})
        ip_map = []
        for index in range(fields[5]):
            row_rva = fields[6] + index * 8
            ip, state = struct.unpack("<Ii", pe.get_data(row_rva, 8))
            ip_map.append({"address": hex(base + ip), "state": state, "entry": blob(row_rva, 8)})
        body = instructions(start, entry.EndAddress - start, False)
        prologue = [i for i in body if i.address < base + start + header["prologSize"]]
        stack_delta = 0
        for ins in prologue:
            if ins.mnemonic == "push":
                stack_delta += 8
            elif ins.mnemonic == "sub" and ins.op_str.startswith("rsp,") and ins.operands[1].type == X86_OP_IMM:
                stack_delta += ins.operands[1].imm
        return {"functionAddress": hex(base + start), "endExclusive": hex(base + entry.EndAddress),
                "unwindInfo": header, "encodedFuncInfoPointer": blob(data_rva, 4), "funcInfo": blob(fi, 40),
                "magic": hex(fields[0]), "stateCount": fields[1], "unwindMapRva": hex(fields[2]),
                "tryBlockCount": fields[3], "ipMapCount": fields[5], "ipMapRva": hex(fields[6]),
                "unwindHelpStackDisplacement": fields[7], "ehFlags": fields[9],
                "prologueInstructions": [instruction(i) for i in prologue], "prologueStackBytes": stack_delta,
                "entryThisSavedAtRspPlus8": bool(body and body[0].mnemonic == "mov" and body[0].op_str == "qword ptr [rsp + 8], rcx"),
                "derivedSavedThisFromEstablisherFrame": hex(stack_delta + 8),
                "actions": actions, "ipToStateMap": ip_map, "functionInstructions": [instruction(i) for i in body]}

    owners = {name: cpp_info(start) for name, start in (
        ("mission_constructor", 0x5CE3F0), ("mission_destructor", 0x5CEF70),
        ("agent_pool_destructor", 0x5D81E0), ("agent_pool_constructor", 0x5D8220))}
    field_adjustments = []
    local_adjustments = []
    vector_actions = []
    for name, owner in owners.items():
        expected_this_offset = int(owner["derivedSavedThisFromEstablisherFrame"], 16)
        for action in owner["actions"]:
            address = int(action["actionAddress"], 16) - base if action["actionAddress"] else None
            body = instructions(address) if address is not None else []
            if len(body) >= 3 and body[0].mnemonic == "mov" and body[0].operands[0].type == X86_OP_REG and body[0].reg_name(body[0].operands[0].reg) == "rcx" and body[0].operands[1].type == X86_OP_MEM:
                memory = body[0].operands[1].mem
                if body[0].reg_name(memory.base) == "rdx" and body[1].mnemonic in ("add", "sub") and body[1].operands[1].type == X86_OP_IMM and body[2].mnemonic == "jmp":
                    field_offset = body[1].operands[1].imm * (-1 if body[1].mnemonic == "sub" else 1)
                    record = {"owner": name, "ownerFunction": owner["functionAddress"], "state": action["state"],
                              "toState": action["toState"], "actionAddress": action["actionAddress"],
                              "mapEntry": action["mapEntry"], "frameSlot": hex(memory.disp),
                              "offset": field_offset, "offsetHex": hex(field_offset),
                              "adjustmentInstruction": instruction(body[1]), "targetDestructor": hex(body[2].operands[0].imm),
                              "loadInstruction": instruction(body[0]), "tailJumpInstruction": instruction(body[2])}
                    if name.startswith("mission_") and memory.disp == expected_this_offset and owner["entryThisSavedAtRspPlus8"]:
                        record["association"] = "Mission saved-this field address; exact FuncInfo state/action and prologue slot agree"
                        field_adjustments.append(record)
                    else:
                        record["association"] = "Cleanup of local/subobject pointer; frame slot differs from saved Mission this; object identity not inferred solely from numeric offset"
                        local_adjustments.append(record)
            if address in (0xA23E70, 0xA241C0, 0xA241F0):
                immediates = [(i.mnemonic, i.reg_name(i.operands[0].reg), i.operands[1].imm)
                              for i in body if len(i.operands) == 2 and i.operands[0].type == X86_OP_REG and i.operands[1].type == X86_OP_IMM]
                if ("mov", "r8d", 2040) not in immediates or ("mov", "edx", 0xC60) not in immediates:
                    raise ValueError("Vector cleanup argument bytes differ from audited count/stride")
                if not any(i.mnemonic == "lea" and i.reg_name(i.operands[0].reg) == "r9" and
                           i.operands[1].type == X86_OP_MEM and i.operands[1].mem.base == X86_REG_RIP and
                           i.address + i.size + i.operands[1].mem.disp == base + 0x6070C0 for i in body):
                    raise ValueError("Vector cleanup destructor target differs")
                if not any(i.mnemonic == "call" and i.operands[0].type == X86_OP_IMM and i.operands[0].imm == base + 0x9DFE08 for i in body):
                    raise ValueError("Vector cleanup iterator target differs")
                vector_actions.append({"owner": name, "ownerFunction": owner["functionAddress"], "state": action["state"],
                                       "toState": action["toState"], "actionAddress": action["actionAddress"], "mapEntry": action["mapEntry"],
                                       "instructions": action["actionInstructions"], "stride": 0xC60, "count": 2040,
                                       "elementDestructor": "0x1806070c0", "iterator": "0x1809dfe08"})
    seh_helpers = []
    for name, start in (("vector_constructor", 0x9DFD98), ("vector_destructor", 0x9DFE08), ("partial_vector_destructor", 0x9DFE74)):
        entry = runtime[start]
        header = unwind_header(entry)
        data_rva = int(header["handlerDataRva"], 16)
        count = struct.unpack("<I", pe.get_data(data_rva, 4))[0]
        if header.get("handlerImport", {}).get("symbol") != "__C_specific_handler" or count > 64:
            raise ValueError("Unexpected SEH scope table")
        scopes = []
        for index in range(count):
            row = data_rva + 4 + index * 16
            begin, end, handler, jump = struct.unpack("<4I", pe.get_data(row, 16))
            scopes.append({"entry": blob(row, 16), "begin": hex(base + begin), "endExclusive": hex(base + end),
                           "handler": hex(base + handler), "jumpTarget": hex(base + jump) if jump else None,
                           "handlerInstructions": [instruction(i) for i in instructions(handler)]})
        seh_helpers.append({"name": name, "address": hex(base + start), "unwindInfo": header,
                            "scopeCount": count, "scopes": scopes,
                            "instructions": [instruction(i) for i in instructions(start, entry.EndAddress - start, False)]})
    result = {"schemaVersion": 1, "status": "verified_static_eh_associations_not_runtime_test_or_patch_approval", "generatedUtc": datetime.now(timezone.utc).isoformat(),
              "binary": inv["binary"], "sourceInventory": {"path": str(inventory_path), "sha256": hashlib.sha256(inventory_bytes).hexdigest()},
              "method": "Parse PE exception-directory UNWIND_INFO, imported handler thunks, MSVC FuncInfo magic0x19930522 state/action and IP maps, and SEH scope tables; decode only the table-selected funclet entries. Compare entry RCX save and prologue stack movement to each funclet's RDX-relative slot.",
              "summary": {"missionConstructorStates": owners["mission_constructor"]["stateCount"],
                          "missionDestructorStates": owners["mission_destructor"]["stateCount"],
                          "missionFieldAdjustments": len(field_adjustments),
                          "missionFieldAdjustmentsByOwner": dict(Counter(x["owner"] for x in field_adjustments)),
                          "distinctMissionFieldOffsets": len(set(x["offset"] for x in field_adjustments)),
                          "missionFieldAdjustmentsMissingNumericInventory": sum(not x["adjustmentInstruction"]["presentInOriginalNumericInventory"] for x in field_adjustments),
                          "vectorCleanupStateAssociations": len(vector_actions)},
              "owners": owners, "missionFieldAdjustments": field_adjustments, "localPointerAdjustments": local_adjustments,
              "vectorCleanupActions": vector_actions, "vectorIteratorSeh": seh_helpers,
              "observations": ["Mission constructor and destructor saved-this cleanup actions include ADD RCX,fieldOffset instructions without their own .pdata entry. They are real code selected by FuncInfo tables; a memory-displacement-only classifier necessarily omits them.",
                               "All five pool offsets occur in both Mission cleanup maps. Fields after the pools through0x1184ae8 also occur and move when the Mission layout expands.",
                               "Mission destructor states60/61 use shared full-agent-array cleanup0x180a23e70; pool destructor state0 uses0x180a241c0; pool constructor state0 uses0x180a241f0. All pass2040 elements of0xc60 bytes and element destructor0x1806070c0.",
                               "The pool constructor's full-array cleanup is activated after the vector constructor returns, as its IP-to-state map shows. The vector constructor separately tracks the number of completed elements and its SEH finally handler destroys that completed prefix in reverse order if construction exits abnormally.",
                               "The generic vector destructor also tracks the remaining count and has a SEH finally cleanup path. These helpers use runtime count parameters rather than their own hardcoded2040 bound.",
                               "Constructor states12,16,20 share action0x180a237b0, which adds0x3fc0 to a local/subobject pointer. This offset is absent from the initial numeric inventory; it coincides with2040 pointer entries and needs a separate subobject-layout decision if the three trailing vectors grow."],
              "limitations": ["No exception was injected and no game code executed; correctness under actual throwing constructors/destructors is untested.",
                              "Frame-slot interpretation assumes the installed Windows x64 unwind and MSVC C++ handler conventions and is corroborated by exact prologue instructions, not runtime stack observation.",
                              "Audit covers four selected C++ owners and three selected vector helper routines; other functions, foreign modules, landing pads and metadata may require additional work.",
                              "Field association proves the old pointer adjustment, not the correct new offset or whether widening a particular member is required.",
                              "No universal negative-offset search is claimed. The decoded saved-this ADD/SUB funclets in this scope have nonnegative adjustments; computed/interior-pointer addressing elsewhere remains outside this audit."]}
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"output": str(output), "summary": result["summary"]}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "evidence/engine-extension/pool-unwind-audit.json")
    audit(parser.parse_args().output)
