"""Execution-free, hash-bound x64 instruction candidate inventory.

This is a search index, NEVER a patch generator. Numeric matches do not prove
the base register points to a Mission or pool. No target code is loaded/run.
"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "build/battle-scale/python-libs"))
import capstone
from capstone import Cs, CS_ARCH_X86, CS_MODE_64
from capstone.x86 import X86_OP_IMM, X86_OP_MEM, X86_REG_RIP, X86_REG_EIP
import pefile


def merge_ranges(ranges):
    result = []
    for start, end in sorted(ranges):
        if result and start <= result[-1][1]:
            result[-1][1] = max(end, result[-1][1])
        else:
            result.append([start, end])
    return result


def subtract_range(start, end, covered):
    cursor = start
    for left, right in covered:
        if right <= cursor:
            continue
        if left >= end:
            break
        if left > cursor:
            yield cursor, min(left, end)
        cursor = max(cursor, right)
    if cursor < end:
        yield cursor, end


def add_compact_groups(result):
    """Secondary indexes retain candidate status; never infer a structure type."""
    mission_groups = defaultdict(lambda: defaultdict(list))
    limits_by_function = defaultdict(lambda: defaultdict(list))
    for candidate in result["candidates"]:
        function = candidate["functionStart"] or ("uncovered@" + candidate["scanRangeStart"])
        for match in candidate["matches"]:
            value = match["decodedValue"]
            if match["kind"] == "memory_displacement" and 0x62CA50 <= value <= 0x1184B40:
                mission_groups[value][function].append(candidate["address"])
            if value in (2039, 2040, 2041):
                limits_by_function[function][value].append({"address": candidate["address"], "operandKind": match["kind"]})
    result["groupedMissionMemoryDisplacementCandidates"] = [
        {"value": hex(value), "operandCount": sum(len(addresses) for addresses in functions.values()),
         "functions": [{"function": function, "operandCount": len(addresses), "addresses": addresses}
                       for function, addresses in sorted(functions.items())]}
        for value, functions in sorted(mission_groups.items())]
    result["groupedCapacityConstantCandidates"] = [
        {"function": function, "values": [{"value": value, "operandCount": len(sites), "sites": sites}
                                           for value, sites in sorted(values.items())]}
        for function, values in sorted(limits_by_function.items())]
    result["summary"]["missionMemoryDisplacementDistinctValues"] = len(mission_groups)
    result["summary"]["missionMemoryDisplacementOperandCount"] = sum(len(sites) for functions in mission_groups.values() for sites in functions.values())
    result["summary"]["decodeGapCountsBySource"] = dict(Counter(g["rangeSource"] for g in result["coverage"]["undecodableBytes"]))
    return result


def verify_source_spans(result, binary):
    source = binary.read_bytes()
    if hashlib.sha256(source).hexdigest() != result["binary"]["sha256"]:
        raise ValueError("Source changed during inventory")
    operands = 0
    for candidate in result["candidates"]:
        instruction = bytes.fromhex(candidate["bytes"])
        offset = int(candidate["fileOffset"], 16)
        if source[offset:offset + len(instruction)] != instruction:
            raise ValueError("Instruction bytes disagree with source at " + candidate["address"])
        for match in candidate["matches"]:
            start, size = match["encodedOffset"], match["encodedSize"]
            if size <= 0 or start + size > len(instruction) or instruction[start:start + size].hex() != match["encodedBytes"]:
                raise ValueError("Invalid operand encoding span at " + candidate["address"])
            operands += 1
    result["sourceSpanVerification"] = {"passed": True, "instructionByteSlicesChecked": len(result["candidates"]),
                                        "encodedOperandSpansChecked": operands,
                                        "scope": "Byte provenance and encoded spans only; no semantic or runtime validation."}
    return result


def inventory(binary, layout_path):
    data = binary.read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    layout_bytes = layout_path.read_bytes()
    layout = json.loads(layout_bytes.decode("utf-8-sig"))
    if digest != layout["binarySha256"]:
        raise ValueError("Native binary SHA-256 differs from the reviewed layout; refusing scan")
    pe = pefile.PE(data=data)
    if pe.FILE_HEADER.Machine != 0x8664 or pe.OPTIONAL_HEADER.Magic != 0x20B:
        raise ValueError("Expected PE32+ AMD64")
    base = pe.OPTIONAL_HEADER.ImageBase
    if base != int(layout["imageBase"], 16):
        raise ValueError("Image base differs from the reviewed layout")
    decoder = Cs(CS_ARCH_X86, CS_MODE_64)
    decoder.detail = True
    exact = defaultdict(list)
    for value, name in [(2039, "last_valid_agent_index"), (2040, "agent_capacity"),
                        (2041, "agent_sentinel"), (layout["missionAllocationBytes"], "mission_allocation")]:
        exact[value].append(name)
    for pool in layout["pools"]:
        origin = int(pool["missionOffset"], 16)
        exact[int(pool["elementStride"], 16)].append("stride:" + pool["name"])
        for key in ("missionOffset", "indexTableOffset", "counterOffset", "highWaterOffset"):
            val = int(pool[key], 16)
            exact[val].append("mission:" + pool["name"] + ":" + key)
            if key != "missionOffset":
                exact[val - origin].append("pool_relative:" + pool["name"] + ":" + key)
    first = layout["pools"][0]
    first_start = int(first["missionOffset"], 16)
    first_end = first_start + int(first["elementStride"], 16)
    mission_start = int(first["indexTableOffset"], 16)
    mission_end = layout["missionAllocationBytes"]

    def categories(value):
        found = list(exact.get(value, ()))
        if first_start <= value < first_end:
            found.append("first_agent_inline_address_range")
        if mission_start <= value <= mission_end:
            found.append("mission_later_inline_address_range")
        return found

    exec_sections = []
    for section in pe.sections:
        if section.Characteristics & 0x20000000:
            size = min(section.Misc_VirtualSize, section.SizeOfRawData)
            exec_sections.append({"name": section.Name.rstrip(b"\0").decode("ascii", "replace"),
                                  "rvaStart": section.VirtualAddress, "rvaEnd": section.VirtualAddress + size,
                                  "fileOffset": section.PointerToRawData, "fileBackedBytes": size,
                                  "rawPaddingBytesExcluded": max(0, section.SizeOfRawData - size),
                                  "virtualZeroFillBytesExcluded": max(0, section.Misc_VirtualSize - size)})
    runtime_ranges = []
    rejected = []
    raw_functions = getattr(pe, "DIRECTORY_ENTRY_EXCEPTION", [])
    for entry in raw_functions:
        start, end = entry.struct.BeginAddress, entry.struct.EndAddress
        if end <= start or not any(s["rvaStart"] <= start < end <= s["rvaEnd"] for s in exec_sections):
            rejected.append({"rvaStart": hex(start), "rvaEnd": hex(end), "reason": "not wholly inside one file-backed executable section"})
        else:
            runtime_ranges.append((start, end))
    unique_ranges = sorted(set(runtime_ranges))
    covered = merge_ranges(unique_ranges)
    uncovered = []
    for section in exec_sections:
        uncovered.extend(subtract_range(section["rvaStart"], section["rvaEnd"], covered))
    candidates = []
    calls = []
    gaps = []
    counts = Counter()
    category_counts = Counter()
    decoded_intervals = []
    candidate_functions = set()

    def basic(ins):
        return {"address": hex(ins.address), "rva": hex(ins.address - base),
                "bytes": ins.bytes.hex(), "mnemonic": ins.mnemonic, "operands": ins.op_str}

    def scan_range(start, end, source):
        offset = pe.get_offset_from_rva(start)
        block = data[offset:offset + end - start]
        cursor = 0
        instructions = []
        recovered = False
        while cursor < len(block):
            before = cursor
            for ins in decoder.disasm(block[cursor:], base + start + cursor):
                instructions.append((ins, recovered))
                cursor = ins.address - base - start + ins.size
                decoded_intervals.append((ins.address - base, ins.address - base + ins.size))
            if cursor == len(block):
                break
            gaps.append({"rva": hex(start + cursor), "fileOffset": hex(offset + cursor),
                         "byte": hex(block[cursor]), "rangeSource": source,
                         "functionStart": hex(base + start) if source == "pdata" else None})
            cursor += 1
            recovered = True
            assert cursor > before
        for index, (ins, after_error) in enumerate(instructions):
            counts[source + "Instructions"] += 1
            if after_error:
                counts["instructionsAfterDecodeResynchronization"] += 1
            if ins.mnemonic == "call" and len(ins.operands) == 1 and ins.operands[0].type == X86_OP_IMM:
                calls.append({"address": hex(ins.address), "target": hex(ins.operands[0].imm),
                              "sourceFunctionStart": hex(base + start) if source == "pdata" else None,
                              "rangeSource": source, "afterDecodeResynchronization": after_error})
            matches = []
            for operand_index, operand in enumerate(ins.operands):
                if operand.type == X86_OP_MEM:
                    if operand.mem.base in (X86_REG_RIP, X86_REG_EIP):
                        counts["ripRelativeMemoryOperandsExcluded"] += 1
                        continue
                    value, kind, encoded_offset, encoded_size = operand.mem.disp, "memory_displacement", ins.disp_offset, ins.disp_size
                    registers = {"base": ins.reg_name(operand.mem.base), "index": ins.reg_name(operand.mem.index), "scale": operand.mem.scale}
                elif operand.type == X86_OP_IMM:
                    value, kind, encoded_offset, encoded_size = operand.imm, "immediate", ins.imm_offset, ins.imm_size
                    registers = None
                else:
                    continue
                found = categories(value)
                if not found:
                    continue
                encoded = bytes(ins.bytes[encoded_offset:encoded_offset + encoded_size])
                matches.append({"operandIndex": operand_index, "kind": kind, "decodedValue": value,
                                "decodedValueHex": hex(value), "encodedOffset": encoded_offset,
                                "encodedSize": encoded_size, "encodedBytes": encoded.hex(),
                                "encodedUnsignedValue": int.from_bytes(encoded, "little") if encoded else None,
                                "registers": registers, "categories": found})
                category_counts.update(found)
            if matches:
                result = basic(ins)
                result.update({"fileOffset": hex(pe.get_offset_from_rva(ins.address - base)),
                               "size": ins.size, "rangeSource": source,
                               "decodeConfidence": "lower_uncovered_linear_sweep" if source != "pdata" else (
                                   "lower_resynchronized_after_invalid_byte" if after_error else "pdata_anchored_linear_decode"),
                               "functionStart": hex(base + start) if source == "pdata" else None,
                               "functionEndExclusive": hex(base + end) if source == "pdata" else None,
                               "scanRangeStart": hex(base + start), "scanRangeEndExclusive": hex(base + end),
                               "matches": matches,
                               "previousInstructions": [basic(x[0]) for x in instructions[max(0, index - 2):index]],
                               "nextInstructions": [basic(x[0]) for x in instructions[index + 1:index + 3]],
                               "semanticStatus": "numeric_candidate_only_not_approved_for_patch"})
                candidates.append(result)
                if source == "pdata":
                    candidate_functions.add(base + start)

    for start, end in unique_ranges:
        scan_range(start, end, "pdata")
    for start, end in uncovered:
        scan_range(start, end, "uncovered_executable")
    decoded = merge_ranges(decoded_intervals)
    anchors = []
    expected = [(0x1805D8044, 2040, "cmp"), (0x1805D8253, 2040, "mov"),
                (0x1806F93B0, 2040, "mov")]
    for address, value, mnemonic in expected:
        found = [c for c in candidates if int(c["address"], 16) == address]
        ok = any(c["mnemonic"] == mnemonic and any(m["decodedValue"] == value for m in c["matches"]) for c in found)
        anchors.append({"address": hex(address), "expectedValue": value, "expectedMnemonic": mnemonic,
                        "passed": ok, "candidateCount": len(found)})
    if not all(a["passed"] for a in anchors):
        raise ValueError("Known REA instruction anchors failed: " + repr(anchors))
    caller_index = defaultdict(list)
    for call in calls:
        if int(call["target"], 16) in candidate_functions:
            caller_index[call["target"]].append(call)
    summary = {"candidateInstructions": len(candidates), "candidatePdataFunctions": len(candidate_functions),
               "candidateInstructionCountsBySource": dict(Counter(c["rangeSource"] for c in candidates)),
               "categoryOperandCounts": dict(sorted(category_counts.items())),
               "directCallsToCandidateFunctionStarts": sum(len(v) for v in caller_index.values()), **counts}
    return {"schemaVersion": 1, "status": "static_numeric_candidates_not_a_patch", "generatedUtc": datetime.now(timezone.utc).isoformat(),
            "binary": {"path": str(binary.resolve()), "sha256": digest, "sizeBytes": len(data), "imageBase": hex(base)},
            "sourceLayout": {"path": str(layout_path.resolve()), "sha256": hashlib.sha256(layout_bytes).hexdigest()},
            "tools": {"python": sys.version.split()[0], "pefile": pefile.__version__, "capstone": capstone.__version__},
            "method": "Decode each unique AMD64 PE exception-directory runtime range from its declared start; separately linear-sweep executable ranges absent from .pdata. On invalid instruction record one undecoded byte and resume; all later instructions in that range lose boundary confidence. Inspect non-RIP memory displacement and immediate operands only. No binary or process writes.",
            "limitations": ["Every match is a numeric candidate, not semantic proof or permission to patch; unrelated structs and constants collide.",
                            "PE .pdata ranges can be fragments rather than full functions; inline data may decode as valid instructions.",
                            "Uncovered executable bytes may be padding, data or leaf code. Linear sweep does not establish real instruction boundaries.",
                            "No control-flow or register/data-flow inference; computed, split, negative-relative, loaded or external-module offsets can be missed.",
                            "Direct callers include only immediate CALL targets equal to a candidate .pdata range start; indirect calls, jumps and interior targets are not resolved.",
                            "Byte decode coverage is not proof of semantic reference completeness, ABI correctness or safe capacity expansion.",
                            "RIP/EIP-relative memory operands are deliberately excluded even if their displacement numerically matches; data referenced through globals needs separate review."],
            "search": {"exactValues": {hex(k): v for k, v in sorted(exact.items())},
                       "firstAgentRange": {"startInclusive": hex(first_start), "endExclusive": hex(first_end)},
                       "missionLaterRange": {"startInclusive": hex(mission_start), "endInclusive": hex(mission_end)}},
            "coverage": {"executableSections": exec_sections, "exceptionDirectoryEntries": len(raw_functions),
                         "uniqueAcceptedRuntimeRanges": len(unique_ranges), "duplicateRuntimeRanges": len(runtime_ranges) - len(unique_ranges),
                         "rejectedRuntimeRanges": rejected, "runtimeRangeUnionBytes": sum(b - a for a, b in covered),
                         "uncoveredExecutableRanges": [{"rvaStart": hex(a), "rvaEndExclusive": hex(b), "bytes": b - a} for a, b in uncovered],
                         "uncoveredExecutableBytes": sum(b - a for a, b in uncovered),
                         "fileBackedExecutableBytes": sum(s["fileBackedBytes"] for s in exec_sections),
                         "decodedUniqueBytes": sum(b - a for a, b in decoded), "undecodableBytes": gaps},
            "knownAnchorVerification": anchors, "summary": summary, "candidates": candidates,
            "directCallersByCandidateFunction": dict(sorted(caller_index.items()))}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, default=Path("D:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord/bin/Win64_Shipping_Client/TaleWorlds.Native.dll"))
    parser.add_argument("--layout", type=Path, default=ROOT / "evidence/engine-extension/native-pool-layout.json")
    parser.add_argument("--output", type=Path, default=ROOT / "evidence/engine-extension/native-pool-reference-inventory.json")
    args = parser.parse_args()
    result = verify_source_spans(add_compact_groups(inventory(args.binary, args.layout)), args.binary)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"output": str(args.output), "summary": result["summary"],
                      "coverage": {k: v for k, v in result["coverage"].items() if k not in ("uncoveredExecutableRanges", "undecodableBytes")},
                      "undecodableByteCount": len(result["coverage"]["undecodableBytes"]),
                      "knownAnchorVerification": result["knownAnchorVerification"]}))


if __name__ == "__main__":
    main()
