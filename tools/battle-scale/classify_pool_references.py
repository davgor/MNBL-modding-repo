"""Classify image-relative numeric matches without executing or changing Bannerlord.

Constants are propagated through decoded control flow, not through source order.
This classifier never approves a pool patch and never loads a target module.
"""
from __future__ import annotations
import argparse
from collections import Counter, defaultdict, deque
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import struct
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "build/battle-scale/python-libs"))
import capstone
from capstone import Cs, CS_ARCH_X86, CS_MODE_64, CS_GRP_JUMP, CS_GRP_RET
from capstone.x86 import X86_OP_REG, X86_OP_IMM, X86_OP_MEM, X86_REG_RIP
import pefile

ALIASES = {}
for names in ("rax eax ax al ah", "rbx ebx bx bl bh", "rcx ecx cx cl ch", "rdx edx dx dl dh",
              "rsi esi si sil", "rdi edi di dil", "rbp ebp bp bpl", "rsp esp sp spl"):
    parts = names.split()
    for name in parts:
        ALIASES[name] = parts[0]
for number in range(8, 16):
    for suffix in ("", "d", "w", "b"):
        ALIASES[f"r{number}{suffix}"] = f"r{number}"
VOLATILE = {"rax", "rcx", "rdx", "r8", "r9", "r10", "r11"}
MASK64 = (1 << 64) - 1


def register(ins, number):
    return ALIASES.get(ins.reg_name(number))


def basic(ins):
    return {"address": hex(ins.address), "bytes": ins.bytes.hex(), "mnemonic": ins.mnemonic, "operands": ins.op_str}


def join(left, right):
    if left is None:
        return dict(right)
    return {reg: (value, sources | right[reg][1]) for reg, (value, sources) in left.items()
            if reg in right and right[reg][0] == value}


def transfer(ins, incoming):
    outgoing = dict(incoming)
    ops = ins.operands
    try:
        _, writes = ins.regs_access()
    except capstone.CsError:
        return {}
    for written in writes:
        outgoing.pop(register(ins, written), None)
    if ins.mnemonic == "call":
        for reg in VOLATILE:
            outgoing.pop(reg, None)
        return outgoing
    if not ops or ops[0].type != X86_OP_REG:
        return outgoing
    dest = register(ins, ops[0].reg)
    if not dest or ops[0].size not in (4, 8):
        return outgoing
    value = None
    sources = frozenset({ins.address})
    if ins.mnemonic in ("mov", "movabs") and len(ops) == 2:
        if ops[1].type == X86_OP_IMM:
            value = ops[1].imm
        elif ops[1].type == X86_OP_REG:
            previous = incoming.get(register(ins, ops[1].reg))
            if previous is not None:
                value, earlier = previous
                sources |= earlier
    elif ins.mnemonic == "lea" and len(ops) == 2 and ops[1].type == X86_OP_MEM:
        mem = ops[1].mem
        if mem.base == X86_REG_RIP:
            value = ins.address + ins.size + mem.disp
        else:
            terms = []
            for number, scale in ((mem.base, 1), (mem.index, mem.scale)):
                if not number:
                    continue
                previous = incoming.get(register(ins, number))
                if previous is None:
                    break
                terms.append(previous[0] * scale)
                sources |= previous[1]
            else:
                value = mem.disp + sum(terms)
    elif ins.mnemonic in ("add", "sub") and len(ops) == 2 and ops[1].type == X86_OP_IMM:
        previous = incoming.get(dest)
        if previous is not None:
            value = previous[0] + (ops[1].imm if ins.mnemonic == "add" else -ops[1].imm)
            sources |= previous[1]
    elif ins.mnemonic == "xor" and len(ops) == 2 and ops[1].type == X86_OP_REG and ops[0].reg == ops[1].reg:
        value = 0
    if value is not None:
        outgoing[dest] = (value & ((1 << (ops[0].size * 8)) - 1), sources)
    return outgoing


def classify(inventory_path, output_path):
    inventory_bytes = inventory_path.read_bytes()
    inv = json.loads(inventory_bytes)
    path = Path(inv["binary"]["path"])
    source = path.read_bytes()
    if hashlib.sha256(source).hexdigest() != inv["binary"]["sha256"]:
        raise ValueError("Native binary no longer matches the inventory")
    pe = pefile.PE(data=source)
    image_base = pe.OPTIONAL_HEADER.ImageBase
    image_end = image_base + pe.OPTIONAL_HEADER.SizeOfImage
    decoder = Cs(CS_ARCH_X86, CS_MODE_64)
    decoder.detail = True
    entries = {e.struct.BeginAddress: e.struct for e in pe.DIRECTORY_ENTRY_EXCEPTION}
    chains = {}
    chain_errors = []
    for start, entry in entries.items():
        unwind = entry.UnwindData
        header = pe.get_data(unwind, 4)
        if len(header) != 4:
            chain_errors.append({"start": hex(image_base + start), "reason": "missing unwind header"})
            continue
        if (header[0] >> 3) & 4:
            tail = unwind + 4 + ((header[2] + 1) & ~1) * 2
            parent_start, parent_end, parent_unwind = struct.unpack("<III", pe.get_data(tail, 12))
            parent = entries.get(parent_start)
            if parent is None or parent.EndAddress != parent_end or parent.UnwindData != parent_unwind:
                chain_errors.append({"start": hex(image_base + start), "reason": "chain target not matching runtime directory", "parent": hex(image_base + parent_start)})
            else:
                chains[start] = parent_start

    def root(start):
        visited = set()
        while start in chains:
            if start in visited:
                raise ValueError("Unwind chain cycle")
            visited.add(start)
            start = chains[start]
        return start

    groups = defaultdict(list)
    for start, entry in entries.items():
        groups[root(start)].append((start, entry.EndAddress))
    targets = [c for c in inv["candidates"] if any(m["kind"] == "memory_displacement" and 0x62CA50 <= m["decodedValue"] <= 0x1184B40 for m in c["matches"])]
    targets_by_group = defaultdict(list)
    results = []
    for candidate in targets:
        if candidate["functionStart"] is None:
            results.append({"address": candidate["address"], "classification": "unresolved", "reason": "No .pdata entry; no proved function-entry register state", "functionStart": None})
        else:
            targets_by_group[root(int(candidate["functionStart"], 16) - image_base)].append(candidate)
    group_records = []
    definition_records = {}
    for group_start, group_targets in sorted(targets_by_group.items()):
        instructions = {}
        recovered_addresses = set()
        undecoded = []
        for start, end in sorted(groups[group_start]):
            block = pe.get_data(start, end - start)
            cursor = 0
            recovered = False
            while cursor < len(block):
                for ins in decoder.disasm(block[cursor:], image_base + start + cursor):
                    instructions[ins.address] = ins
                    if recovered:
                        recovered_addresses.add(ins.address)
                    cursor = ins.address - image_base - start + ins.size
                if cursor < len(block):
                    undecoded.append(image_base + start + cursor)
                    cursor += 1
                    recovered = True
        all_addresses = tuple(sorted(instructions))
        successors = {}
        indirect_jumps = []
        for address, ins in instructions.items():
            following = address + ins.size
            nexts = []
            if ins.group(CS_GRP_RET) or ins.mnemonic in ("int3", "ud2", "hlt", "iretq", "int"):
                pass
            elif ins.group(CS_GRP_JUMP):
                if ins.operands and ins.operands[0].type == X86_OP_IMM:
                    target = ins.operands[0].imm
                    if target in instructions:
                        nexts.append(target)
                    if ins.mnemonic != "jmp" and following in instructions:
                        nexts.append(following)
                else:
                    # Overapproximate unknown jump destinations. Propagating to every
                    # decoded instruction is deliberately stricter than guessing a
                    # switch table. Contradictory paths erase the constant proof.
                    nexts = all_addresses
                    indirect_jumps.append(address)
            elif following in instructions:
                nexts.append(following)
            successors[address] = nexts
        entry_address = image_base + group_start
        states = {entry_address: {}}
        work = deque([entry_address])
        queued = {entry_address}
        steps = 0
        maximum_steps = max(10000, len(instructions) * 100)
        while work and steps < maximum_steps:
            address = work.popleft()
            queued.remove(address)
            steps += 1
            ins = instructions.get(address)
            if ins is None:
                continue
            outgoing = transfer(ins, states[address])
            for successor in successors[address]:
                new = join(states.get(successor), outgoing)
                if successor not in states or new != states[successor]:
                    states[successor] = new
                    if successor not in queued:
                        work.append(successor)
                        queued.add(successor)
        converged = not work
        for candidate in group_targets:
            address = int(candidate["address"], 16)
            ins = instructions.get(address)
            state = states.get(address)
            record = {"address": candidate["address"], "functionStart": candidate["functionStart"],
                      "unwindChainRoot": hex(entry_address), "classification": "unresolved"}
            if not converged:
                record["reason"] = "Constant-flow work limit reached; no classification accepted"
            elif ins is None or state is None:
                record["reason"] = "No decoded reachable instruction state from unwind-root entry"
            elif address in recovered_addresses:
                record["reason"] = "Instruction boundary after decode resynchronization is not proved"
            else:
                anchors = []
                for match in candidate["matches"]:
                    if match["kind"] != "memory_displacement" or not 0x62CA50 <= match["decodedValue"] <= 0x1184B40:
                        continue
                    mem = ins.operands[match["operandIndex"]].mem
                    for position, number, scale in (("base", mem.base, 1), ("index", mem.index, mem.scale)):
                        reg = register(ins, number)
                        known = state.get(reg)
                        if known is not None and scale == 1 and image_base <= known[0] < image_end:
                            value, definitions = known
                            definitions_safe = not any(d in recovered_addresses for d in definitions)
                            if not definitions_safe:
                                continue
                            anchor = {"operandIndex": match["operandIndex"], "registerPosition": position,
                                      "register": reg, "constantValue": hex(value), "isExactImageBase": value == image_base,
                                      "numericDisplacement": hex(mem.disp),
                                      "constantAddressComponent": hex((value + mem.disp) & MASK64),
                                      "constantAddressComponentRva": hex(value + mem.disp - image_base),
                                      "otherAddressRegisterMayRemainDynamic": bool(mem.index if position == "base" else mem.base),
                                      "definitionInstructions": [hex(d) for d in sorted(definitions)]}
                            anchors.append(anchor)
                            for definition in definitions:
                                definition_records[hex(definition)] = basic(instructions[definition])
                if anchors:
                    record["classification"] = "image_relative_constant_anchor"
                    record["reason"] = "An unscaled address register has an image-address constant on every path in the conservative decoded flow model; the displacement is therefore not a demonstrated Mission-member offset"
                    record["anchors"] = anchors
                    record["instruction"] = basic(ins)
                else:
                    record["reason"] = "No invariant image-address constant in an unscaled address register"
            results.append(record)
        group_records.append({"root": hex(entry_address), "runtimeRanges": [{"start": hex(image_base + s), "endExclusive": hex(image_base + e)} for s, e in sorted(groups[group_start])],
                              "decodedInstructions": len(instructions), "undecodableBytes": [hex(a) for a in undecoded],
                              "indirectJumpsOverapproximatedToEveryDecodedInstruction": [hex(a) for a in indirect_jumps],
                              "worklistSteps": steps, "converged": converged,
                              "reachableInstructions": len(states), "candidateInstructions": len(group_targets)})
    results.sort(key=lambda r: int(r["address"], 16))
    classifications = Counter(r["classification"] for r in results)
    result = {"schemaVersion": 1, "status": "static_reference_classification_not_patch_approval", "generatedUtc": datetime.now(timezone.utc).isoformat(),
              "binary": inv["binary"], "sourceInventory": {"path": str(inventory_path.resolve()), "sha256": hashlib.sha256(inventory_bytes).hexdigest()},
              "sourceLayout": inv.get("sourceLayout"),
              "tools": {"python": sys.version.split()[0], "pefile": pefile.__version__, "capstone": capstone.__version__},
              "flowModel": ["Resolve valid AMD64 chained-unwind entries and combine their .pdata instruction ranges; start the root with every register unknown.",
                            "Propagate MOV immediate/register, RIP-relative and constant-register LEA, ADD/SUB immediate and XOR self; zero-extend 32-bit writes; invalidate all unsupported writes via Capstone regs_access.",
                            "Calls invalidate Windows x64 volatile integer registers; callee-preserved registers follow the Windows x64 ABI.",
                            "At joins retain only equal constants on every incoming path; keep exact defining instruction chains.",
                            "Direct branches/fallthrough use decoded instruction boundaries. Every indirect jump is conservatively allowed to reach every decoded instruction of the chained function group.",
                            "No proof is accepted from failed convergence, unreachable state, recovered candidate boundaries or recovered defining instructions."],
              "limitations": ["Proves constant address components within the declared static flow model, not runtime execution, object provenance, or patch safety.",
                              "Assumes ordinary Windows x64 ABI and unwind-root function entry; nonstandard external jumps into function interiors and binary self-modification are not modeled.",
                              "Hardware exceptions, handlers and longjmp entry states are not modeled; candidates in independent handler routines start unknown.",
                              "Disassembly may include inline data; indirect-jump overapproximation intentionally destroys proofs when such paths conflict.",
                              "A constant image-address component is not proof that the final address stays inside the image when another register is dynamic.",
                              "Unresolved means insufficient proof, not a Mission offset; no candidate is approved for rewriting."],
              "summary": {"candidateInstructions": len(results), "classificationCounts": dict(classifications),
                          "sitesWithExactImageBaseAnchor": sum(any(a["isExactImageBase"] for a in r.get("anchors", [])) for r in results),
                          "analyzedChainedFunctionGroups": len(group_records),
                          "groupsReachingWorkLimit": sum(not g["converged"] for g in group_records), "validUnwindChainLinks": len(chains), "unwindChainErrors": chain_errors},
              "classifications": results, "definitionInstructions": definition_records, "flowGroups": group_records}
    output_path.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"output": str(output_path), "summary": result["summary"]}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--inventory", type=Path, default=ROOT / "evidence/engine-extension/native-pool-reference-inventory.json")
    parser.add_argument("--output", type=Path, default=ROOT / "evidence/engine-extension/native-pool-reference-classification.json")
    args = parser.parse_args()
    classify(args.inventory, args.output)
