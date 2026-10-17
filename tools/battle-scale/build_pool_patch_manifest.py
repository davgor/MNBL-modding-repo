"""Build a reviewable 4080-slot native patch manifest; never write a DLL.

Selection uses bounded semantic rules and saved evidence, never a global numeric
replacement. Missing or stale independent static approval keeps the manifest blocked.
"""
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
EVIDENCE = ROOT / "evidence/engine-extension"
sys.path.insert(0, str(ROOT / "build/battle-scale/python-libs"))
import pefile
import capstone
from capstone.x86 import X86_OP_IMM, X86_OP_MEM, X86_OP_REG, X86_REG_RIP
from pool_layout import ExpandedLayout
from patch_native_copy import safe_review_output, validate_manifest, validate_original


def require_pointer_operand_shape(ins, operand_index):
    """Reject data immediates before consulting exact pointer-provenance proof.

    This is a necessary shape check, never semantic patch authorization.
    In particular MOV [stack],0x1000001 is a scalar write, not address math.
    """
    operand = ins.operands[operand_index]
    if operand.type == X86_OP_MEM:
        if ins.reg_name(operand.mem.base) in ('rsp','esp','rip','eip'):
            raise ValueError('Stack/image-base operand is not a Mission pointer')
        return
    if (operand.type == X86_OP_IMM and operand_index == 1 and
            ins.mnemonic == 'add' and ins.operands[0].type == X86_OP_REG and
            ins.operands[0].size == 8):
        return
    raise ValueError('Data immediate is not a reviewed pointer arithmetic operand')


def displacement_encoding(ins):
    """Get x64 ModRM/SIB displacement width independently of operand width.

    Capstone5 reports disp_size=2 for66 C7 /0 despite the encoded disp32.
    Operand-size override never changes ModRM displacement width in x64.
    """
    if not ins.disp_offset or not ins.modrm_offset:
        raise ValueError("Expected explicit ModRM displacement")
    modrm = ins.bytes[ins.modrm_offset]
    mode, rm = modrm >> 6, modrm & 7
    if mode == 2:
        size = 4
    elif mode == 1:
        size = 1
    elif mode == 0 and rm == 5:
        size = 4
    elif mode == 0 and rm == 4 and ins.bytes[ins.modrm_offset + 1] & 7 == 5:
        size = 4
    else:
        raise ValueError("Unexpected ModRM displacement geometry")
    offset = ins.disp_offset
    if offset + size > len(ins.bytes):
        raise ValueError("Displacement exceeds instruction")
    return offset, size


def replace_displacement_bytes(ins, old, new):
    offset, size = displacement_encoding(ins)
    if int.from_bytes(ins.bytes[offset:offset + size], "little", signed=True) != old:
        raise ValueError("Displacement bytes disagree with decoded value")
    if not -(1 << (size * 8 - 1)) <= new < (1 << (size * 8 - 1)):
        raise ValueError("New displacement does not fit signed encoding")
    raw = bytearray(ins.bytes)
    raw[offset:offset + size] = new.to_bytes(size, "little", signed=True)
    return bytes(raw), offset, size


def canonical_patch_set_sha256(patches):
    rows=[{key:p[key] for key in ('address','original','replacement')}
          for p in sorted(patches,key=lambda p:int(p['address'],16))]
    return hashlib.sha256(json.dumps(rows,sort_keys=True,separators=(',',':')).encode('utf-8')).hexdigest()


def readiness_approval_errors(approval, source_hash, patches, sources, generator_hash, unresolved):
    """Fail closed on missing approval, changed evidence, or uncovered operands."""
    errors=[]
    if approval is None:return ['Independent restricted-test static approval is not present.']
    if approval.get('approvedForRestrictedOfflineTest') is not True:errors.append('Restricted offline test is not explicitly approved.')
    if approval.get('sourceSha256')!=source_hash:errors.append('Static approval native SHA256 differs.')
    if approval.get('canonicalPatchSetSha256')!=canonical_patch_set_sha256(patches):errors.append('Static approval exact patch set differs.')
    if approval.get('generatorSourceSha256')!=generator_hash:errors.append('Static approval generator source differs.')
    expected={name:value['sha256'] for name,value in sources.items() if name!='native-pool-static-readiness-review.json'}
    if approval.get('approvedSourceEvidenceSha256ByName')!=expected:errors.append('Static approval source evidence set differs.')
    if any(unresolved.values()):errors.append('Exact unresolved reference or capacity obligations remain: '+json.dumps(unresolved,sort_keys=True))
    return errors


def build(output):
    sources = {}

    def read(name):
        path = EVIDENCE / name
        raw = path.read_bytes()
        sources[name] = {"path": str(path), "sha256": hashlib.sha256(raw).hexdigest()}
        return json.loads(raw.decode("utf-8-sig"))

    original_layout = read("native-pool-layout.json")
    inventory = read("native-pool-reference-inventory.json")
    classification = read("native-pool-reference-classification.json")
    unwind = read("pool-unwind-audit.json")
    jobs = read("native-job-page-tables.json")
    low_review = read("pool-semantic-review-low.json")
    mid_review = read("pool-semantic-review-mid.json")
    high_review = read("pool-semantic-review-high.json")
    mode_guard = read("native-single-mode-guard.json")
    contact_guard = read('native-contact-capacity-guard.json')
    contact_guard_review = read('native-contact-capacity-independent-review.json')
    ranged_patch = read('native-ranged-page-patch.json')
    ranged_stall = read('native-ranged-stall-review.json')
    ranged_guard = read('native-ranged-guard-independent-review.json')
    ranged_consumer = read('native-ranged-page-consumer-review.json')
    if (len(ranged_patch['edits']) != 38 or ranged_patch['capacity'] != 2097152
            or ranged_patch['elementsPerPage'] != 16384 or ranged_patch['pageCount'] != 128
            or ranged_patch['evidenceSha256'] != sources['native-ranged-stall-review.json']['sha256']
            or ranged_guard['status'] != 'independent-guard-review-passed'
            or ranged_guard['proposalSha256'] != sources['native-ranged-stall-review.json']['sha256']
            or ranged_consumer['status'] != 'reviewed-bounded-ranged-page-consumer-closure'
            or ranged_consumer['findings']):
        raise ValueError('Ranged page change lacks current bounded acceptance')
    if hashlib.sha256(Path(__file__).with_name('review_ranged_guards.py').read_bytes()).hexdigest() != ranged_guard['scriptSha256']:
        raise ValueError('Stale ranged guard review script')
    for dependency, expected_hash in ranged_consumer['reviewedSourceSha256'].items():
        if hashlib.sha256(Path(dependency).read_bytes()).hexdigest() != expected_hash:
            raise ValueError('Stale ranged consumer dependency: ' + dependency)
    geometry_rvas = {edit['rva'] for edit in ranged_patch['edits']} - {'0x23f610','0x23f617','0x89a06','0x89a0f'}
    if geometry_rvas != set(ranged_consumer['requiredGeometryRvas']):
        raise ValueError('Ranged geometry differs from consumer closure')
    renderer_patch = read('native-bone-mapping-patch.json')
    renderer_lifecycle = read('native-renderer-buffer-lifecycle-review.json')
    renderer_review = read('native-bone-mapping-independent-review.json')
    if (renderer_review.get('status') != 'accepted-bounded-machine-change-for-restricted-runtime-test'
            or renderer_review.get('findings') or renderer_review.get('editCount') != 13
            or renderer_patch.get('editCount') != 13 or len(renderer_patch['edits']) != 13):
        raise ValueError('Renderer change lacks bounded independent acceptance')
    for proof in (renderer_patch, renderer_review):
        for dependency, expected_hash in proof['sources'].items():
            if hashlib.sha256(Path(dependency).read_bytes()).hexdigest() != expected_hash:
                raise ValueError('Stale renderer proof dependency: ' + dependency)
    lifecycle_script = Path(__file__).with_name('audit_renderer_buffer_lifecycle.py')
    if hashlib.sha256(lifecycle_script.read_bytes()).hexdigest() != renderer_lifecycle['scriptSha256']:
        raise ValueError('Stale renderer lifecycle script')
    for name, expected_hash in renderer_lifecycle['dependencyHashes'].items():
        if hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() != expected_hash:
            raise ValueError('Stale renderer lifecycle dependency: ' + name)
    if contact_guard_review['reviewedArtifact']['sha256'] != sources['native-contact-capacity-guard.json']['sha256']:
        raise ValueError('Contact-capacity independent review is stale')
    interior_review = read("native-pool-interior-review.json")
    scalar_exclusions = read('native-scalar-arithmetic-exclusions.json')
    scalar_review = read('native-scalar-arithmetic-independent-review.json')
    if scalar_review['reviewedArtifactSha256']!=sources['native-scalar-arithmetic-exclusions.json']['sha256']:
        raise ValueError('Scalar arithmetic independent review is stale')
    residual_candidates = read('native-pool-residual-scalar-candidates.json')
    residual_review = read('native-pool-residual-scalar-review.json')
    if (residual_review['status'] != 'accepted-bounded-residual-search-review' or
            residual_review['candidateArtifactSha256'] != sources['native-pool-residual-scalar-candidates.json']['sha256']):
        raise ValueError('Residual scalar review is missing acceptance or is stale')
    helper_escape = read('native-pool-helper-escape-review.json')
    helper_escape_review = read('native-pool-helper-escape-independent-review.json')
    helper_callers = read('native-pool-helper-caller-review.json')
    helper_caller_review = read('native-pool-helper-caller-independent-review.json')
    for artifact,receipt in (('native-pool-helper-escape-review.json',helper_escape_review),
                             ('native-pool-helper-caller-review.json',helper_caller_review)):
        if receipt['reviewedArtifactSha256']!=sources[artifact]['sha256']:
            raise ValueError('Shared-helper independent receipt is stale')
    if helper_escape['remainingUnclassifiedLiterals'] or helper_callers['unclassifiedListedEntries']:
        raise ValueError('Shared-helper entry review is incomplete')
    spill_review = read('native-pool-spill-review.json')
    tick_spill_review = read('native-pool-tick-spill-review.json')
    record_spill_review = read('native-pool-record-spill-review.json')
    attack_spill_review = read('native-pool-attack-spill-review.json')
    missile_spill_review = read('native-pool-missile-spill-review.json')
    missile_spill_independent = read('native-pool-missile-spill-independent-review.json')
    if missile_spill_independent['reviewedArtifact']['sha256'] != sources['native-pool-missile-spill-review.json']['sha256']:
        raise ValueError('Missile spill independent review is stale')
    job_spill_review = read('native-job-mission-spill-review.json')
    job_spill_independent = read('native-job-mission-spill-independent-review.json')
    if job_spill_independent['reviewedArtifact']['sha256'] != sources['native-job-mission-spill-review.json']['sha256']:
        raise ValueError('Job Mission spill independent review is stale')
    pair_spill_review = read('native-pool-pair-spill-review.json')
    pair_spill_independent = read('native-pool-pair-spill-independent-review.json')
    if pair_spill_independent['reviewedArtifactSha256'] != sources['native-pool-pair-spill-review.json']['sha256']:
        raise ValueError('Pair Mission spill independent review is stale')
    fading_review = read('native-fading-entity-owner-review.json')
    fading_independent = read('native-fading-entity-owner-independent-review.json')
    if fading_independent['reviewedArtifactSha256']!=sources['native-fading-entity-owner-review.json']['sha256']:
        raise ValueError('Fading-entity owner independent review is stale')
    runtime_contracts = read('pool-runtime-safety-contracts.json')
    packed_review = read('native-packed-index-review.json')
    external_review = read('native-external-layout-literals.json')
    profile_review = read('native-isolated-profile-review.json')
    operand_recheck = read('pool-patch-generator-review.json')['highOperandRecheck']
    layout = ExpandedLayout(original_layout, 4080, expand_jobs=True)
    if inventory["sourceLayout"]["sha256"] != sources["native-pool-layout.json"]["sha256"]:
        raise ValueError("Inventory was generated from a different layout profile")
    if classification["sourceInventory"]["sha256"] != sources["native-pool-reference-inventory.json"]["sha256"]:
        raise ValueError("Classification was generated from a different inventory")
    if unwind["sourceInventory"]["sha256"] != sources["native-pool-reference-inventory.json"]["sha256"]:
        raise ValueError("Unwind audit was generated from a different inventory")
    path = Path(inventory["binary"]["path"])
    source = path.read_bytes()
    source_hash = hashlib.sha256(source).hexdigest()
    if source_hash != original_layout["binarySha256"] or source_hash != inventory["binary"]["sha256"]:
        raise ValueError("Source binary does not match reviewed evidence")
    if jobs["targetSha256"] != source_hash or unwind["binary"]["sha256"] != source_hash:
        raise ValueError("Dependent audit target mismatch")
    if mode_guard['sourceSha256'] != source_hash:
        raise ValueError('Mode-guard audit target mismatch')
    if contact_guard['targetSha256'] != source_hash or contact_guard_review['targetSha256'] != source_hash:
        raise ValueError('Contact guard target mismatch')
    if runtime_contracts['targetSha256'] != source_hash:
        raise ValueError('Runtime-contract audit target mismatch')
    if (renderer_patch['sourceSha256'] != source_hash or renderer_review['sourceSha256'] != source_hash
            or renderer_lifecycle['targetSha256'] != source_hash):
        raise ValueError('Renderer proof target mismatch')
    if residual_review['sourceSha256'] != source_hash:
        raise ValueError('Residual scalar review target mismatch')
    if any(review.get("targetSha256", review.get("binary", {}).get("sha256")) != source_hash for review in (low_review, mid_review, high_review, interior_review, spill_review, tick_spill_review, record_spill_review, attack_spill_review, missile_spill_review, job_spill_review, pair_spill_review, fading_review, scalar_exclusions, helper_escape, helper_callers)):
        raise ValueError("Semantic review target mismatch")
    if any(proof.get('nativeSourceSha256', proof.get('sourceSha256')) != source_hash for proof in (ranged_patch,ranged_guard,ranged_consumer)):
        raise ValueError('Ranged proof target mismatch')
    pe = pefile.PE(data=source)
    base = pe.OPTIONAL_HEADER.ImageBase
    decoder = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    decoder.detail = True
    records = {}
    procedure_evidence = defaultdict(list)
    for name in ("native-ghidra-evidence.json", "native-pool-ghidra-evidence.json", "native-expansion-ghidra-evidence.json", "native-closure-ghidra-evidence.json", "native-renderer-stall-ghidra-evidence.json"):
        bundle = read(name)
        if any(a.get("digest", {}).get("sha256") != source_hash for a in bundle["artifacts"]):
            raise ValueError("Native evidence bundle contains another binary")
        for record in bundle["records"]:
            records[record["evidence_id"]] = record
            procedure = record.get("parameters", {}).get("procedure")
            if procedure and isinstance(record["normalized_result"], str):
                procedure_evidence[procedure].append(record["evidence_id"])

    def refs(procedure):
        answer = sorted(set(procedure_evidence[hex(procedure)]))
        if not answer:
            raise ValueError("No retained native evidence for " + hex(procedure))
        return answer

    def local_ref(name, fragment):
        return "sha256:" + sources[name]["sha256"] + "#" + fragment

    def ins_at(address):
        return next(decoder.disasm(pe.get_data(address - base, 15), address))

    pending = {}

    def change(address, operand_index, old, new, reason, evidence_ids, scope):
        if old == new:
            return
        ins = ins_at(address)
        operand = ins.operands[operand_index]
        if operand.type == X86_OP_IMM:
            observed, offset, size = operand.imm, ins.imm_offset, ins.imm_size
        elif operand.type == X86_OP_MEM and operand.mem.base != X86_REG_RIP:
            observed = operand.mem.disp
            _, offset, size = replace_displacement_bytes(ins, observed, new)
        else:
            raise ValueError("Unsupported operand at " + hex(address))
        if observed != old or size not in (1, 2, 4, 8):
            raise ValueError("Reviewed operand mismatch at " + hex(address))
        if not 0 <= new < 1 << (8 * size):
            raise ValueError("Replacement cannot fit encoded operand at " + hex(address) + ": " + str((old, new, size, ins.mnemonic, ins.op_str)))
        entry = pending.setdefault(address, {"ins": ins, "bytes": bytearray(ins.bytes), "changes": [], "evidence": set(), "reasons": [], "scopes": set()})
        existing = [c for c in entry["changes"] if c["operandIndex"] == operand_index]
        if existing:
            if existing[0]["replacementValue"] != new:
                raise ValueError("Conflicting reviewed edit at " + hex(address))
        else:
            entry["bytes"][offset:offset + size] = new.to_bytes(size, "little")
            entry["changes"].append({"operandIndex": operand_index, "originalValue": old, "replacementValue": new,
                                     "encodedOffset": offset, "encodedSize": size})
        entry["evidence"].update(evidence_ids)
        entry["reasons"].append(reason)
        entry["scopes"].add(scope)

    def immediate(address, old, new, reason, evidence_ids, scope):
        ins = ins_at(address)
        matches = [n for n, o in enumerate(ins.operands) if o.type == X86_OP_IMM and o.imm == old]
        if len(matches) != 1:
            raise ValueError("Expected one immediate at " + hex(address))
        change(address, matches[0], old, new, reason, evidence_ids, scope)

    # Stable nonvolatile Mission aliases: constructor RBX and destructor RDI.
    # Only their explicit memory operands are eligible, not every number in the body.
    for name, register in (("mission_constructor", "rbx"), ("mission_destructor", "rdi")):
        owner = unwind["owners"][name]
        function = int(owner["functionAddress"], 16)
        established = False
        for raw in owner["functionInstructions"]:
            ins = ins_at(int(raw["address"], 16))
            if ins.mnemonic == "mov" and ins.op_str == register + ", rcx":
                established = True
            else:
                _, written = ins.regs_access()
                if any(ins.reg_name(r) == register for r in written):
                    established = False
            if established:
                for index, operand in enumerate(ins.operands):
                    if operand.type == X86_OP_MEM and ins.reg_name(operand.mem.base) == register:
                        value = operand.mem.disp
                        if 0x62CA50 <= value <= layout.original_size:
                            change(ins.address, index, value, layout.mission_offset(value),
                                   "Translate verified Mission-this field through five pool insertions and three expanded job tables; preserve element strides.",
                                   refs(function), name)

    # Compiler-emitted cleanup entries are proved by FuncInfo state maps, even
    # when they have no runtime-function entry of their own.
    for field in unwind["missionFieldAdjustments"]:
        old = field["offset"]
        immediate(int(field["adjustmentInstruction"]["address"], 16), old, layout.mission_offset(old),
                  "Translate saved-Mission-this cleanup field; FuncInfo map and prologue slot prove the owner.",
                  refs(int(field["ownerFunction"], 16)) + [local_ref("pool-unwind-audit.json", "missionFieldAdjustments")], "mission_eh_fields")

    # Core helpers have independently reviewed pool-relative metadata and capacity
    # operations. Restrict displacement selection to their three metadata fields.
    helper_specs = [
        (0x1805D7DF0, 0x1805D8020, 0xC60), (0x1805D8020, 0x1805D81E0, 0xC60),
        (0x1805D81E0, 0x1805D8220, 0xC60), (0x1805D8220, 0x1805D82AA, 0xC60),
        (0x1805D8390, 0x1805D860A, 0x2C8), (0x1805D8610, 0x1805D8729, 0x2C8),
        (0x1805D8740, 0x1805D87D0, 0x2C8),
        (0x1805D87D0, 0x1805D8840, 0x230), (0x1805D91F0, 0x1805D9350, 0xC60),
        (0x1805D9350, 0x1805D94A0, 0x2C8), (0x1805D94A0, 0x1805D95EB, 0x230)]
    for start, end, stride in helper_specs:
        evidence_ids = refs(start)
        metadata = {2040 * stride, 2040 * (stride + 2), 2040 * (stride + 2) + 4}
        for ins in decoder.disasm(pe.get_data(start - base, end - start), start):
            for index, operand in enumerate(ins.operands):
                if operand.type == X86_OP_MEM and operand.mem.base != X86_REG_RIP and operand.mem.disp in metadata:
                    change(ins.address, index, operand.mem.disp, layout.pool_offset(stride, operand.mem.disp),
                           "Translate pool-relative index table/count/high-water field in a reviewed helper; do not translate Agent member offsets.", evidence_ids, "pool_metadata")
                elif operand.type == X86_OP_IMM and operand.imm in (2040, 2041):
                    change(ins.address, index, operand.imm, 4080 if operand.imm == 2040 else 4081,
                           "Expand reviewed pool construction/reset/iteration/index-maintenance bound or signed-short sentinel.", evidence_ids, "pool_capacity")

    # Three-way and ten-way unrolling needs iteration counts, not changed strides.
    immediate(0x1805D87E1, 680, 1360, "Companion230 reset visits three slots per loop:1360*3=4080.", refs(0x1805D87D0), "unrolled_reset")
    immediate(0x1805D83A2, 204, 408, "Companion2c8 reset visits ten slots per loop:408*10=4080.", refs(0x1805D8390), "unrolled_reset")

    # Direct inlined constructor/destructor counts and independent EH counts.
    for address in (0x1805CE48A, 0x1805CE4AC, 0x1805CE4DB):
        immediate(address, 2040, 4080, "Inlined companion230 constructor/index initialization count.", refs(0x1805CE3F0), "mission_constructor_count")
    for address in (0x1805CF344, 0x1805CF374):
        immediate(address, 2040, 4080, "Destroy every constructed element of each full Agent array.", refs(0x1805CEF70), "mission_destructor_count")
    for action in unwind["vectorCleanupActions"]:
        for raw in action["instructions"]:
            ins = ins_at(int(raw["address"], 16))
            if ins.mnemonic == "mov" and ins.op_str == "r8d, 0x7f8":
                immediate(ins.address, 2040, 4080, "Full-array EH cleanup count; generic partial-construction cleanup retains its runtime completed-count behavior.",
                          refs(int(action["ownerFunction"], 16)) + [local_ref("pool-unwind-audit.json", "vectorCleanupActions")], "pool_eh_count")

    for address, function, reason in (
        (0x1805E5B63, 0x1805E5B30, "Allocate the expanded Mission before any Agent pointer escapes."),
        (0x1805CEAE9, 0x1805CEAD0, "Keep the sized deleting-destructor argument consistent with the expanded Mission allocation.")):
        immediate(address, layout.original_size, layout.size, reason, refs(function), "mission_allocation_and_delete")
    immediate(0x1806F93B0, 2040, 4080, "Update the registered maximum-agent getter only as part of the coordinated patch set.",
              ["ev_3d0d6187e3040abc60dd8a37ea801c382ea35604e11e64ebc72f67bdaa914ad3",
               "ev_9e9945bb81c2694f4088be078e0583675691e0d2cfd346c408cc24fb86f5c2b8"], "maximum_getter")

    # Spawn has mixed Mission/Agent/companion pointers. Select explicit ranges
    # and registers whose assignments are independently established.
    spawn_evidence = refs(0x1805CD130)
    for candidate in inventory["candidates"]:
        address = int(candidate["address"], 16)
        if not 0x1805CD130 <= address < 0x1805CD290:
            continue
        ins = ins_at(address)
        for index, operand in enumerate(ins.operands):
            if operand.type != X86_OP_MEM:
                continue
            reg, value = ins.reg_name(operand.mem.base), operand.mem.disp
            if reg == "r12" and 0x62CA50 <= value <= layout.original_size:
                change(address, index, value, layout.mission_offset(value), "Spawn's preserved R12 is the Mission; translate only its inline fields.", spawn_evidence, "spawn_mission_fields")
            elif address in (0x1805CD19A, 0x1805CD1A4, 0x1805CD1AF) and reg == "rdi":
                change(address, index, value, layout.pool_offset(0xC60, value), "Spawn's RDI is primary Agent pool at these three bookkeeping instructions.", spawn_evidence, "spawn_pool_metadata")
            elif address in (0x1805CD276, 0x1805CD27C, 0x1805CD287) and reg == "rbx":
                change(address, index, value, layout.pool_offset(0x2C8, value), "Spawn's RBX is companion2c8B after explicit pool-base assignment.", spawn_evidence, "spawn_pool_metadata")

    # Job-table counter/table/flag fields only, with per-producer lifetime evidence.
    job_offsets = {int(c[k], 16) for c in jobs["pageContainers"] for k in ("counterOffset", "tableOffset", "flagsOffset")}
    job_functions = {0x1805C37D0, 0x1805C5F60, 0x1805C5F72, 0x1805C6180, 0x18060B390, 0x1805E0420}
    job_refs = sorted({eid for c in jobs["pageContainers"] for eid in c["evidenceIds"]})
    for candidate in inventory["candidates"]:
        if candidate["functionStart"] is None or int(candidate["functionStart"], 16) not in job_functions:
            continue
        ins = ins_at(int(candidate["address"], 16))
        for index, operand in enumerate(ins.operands):
            if operand.type == X86_OP_MEM and operand.mem.base != X86_REG_RIP and operand.mem.disp in job_offsets:
                change(ins.address, index, operand.mem.disp, layout.mission_offset(operand.mem.disp),
                       "Translate an audited per-frame job container counter/table/flag field; preserve16 records per page.",
                       job_refs + [local_ref("native-job-page-tables.json", "pageContainers")], "job_fields")

    # 127->255 does not fit a sign-extended imm8. Preserve byte length by changing
    # the comparison to unsigned DL/BL; counters start at0 and stop before wrap.
    for candidate in jobs["sameWidthCompareCandidates"]:
        address = int(candidate["address"], 16)
        ins = ins_at(address)
        if ins.bytes.hex() != candidate["original"] or address in pending:
            raise ValueError("Job compare bytes changed or overlap")
        pending[address] = {"ins": ins, "bytes": bytearray.fromhex(candidate["replacement"]), "changes": [],
                            "evidence": set(job_refs + [local_ref("native-job-page-tables.json", "sameWidthCompareCandidates")]),
                            "reasons": [candidate["reason"]], "scopes": {"job_loop_compare"},
                            "specialExpected": "cmp dl, 0xff" if "EDX" in candidate["register"] else "cmp bl, 0xff"}
    for address in (0x1805C414E, 0x1805C47D8):
        immediate(address, 2040 * 8, 4080 * 8, "Allocate one pointer per potential Agent for audited frame worker output.",
                  [eid for c in jobs["heapFrameBuffers"] for eid in c["evidenceIds"]] + [local_ref("native-job-page-tables.json", "heapFrameBuffers")], "frame_pointer_buffers")

    reviewed_exclusions = {}
    for site in low_review["sites"]:
        address = int(site["address"], 16)
        ins = ins_at(address)
        if "bytes" in site and ins.bytes.hex() != site["bytes"]:
            raise ValueError("Low-review instruction bytes differ")
        kind = site["classification"]
        if kind.startswith("exclude-"):
            reviewed_exclusions[address] = site
            continue
        if kind not in ("mission-relative", "pool-relative"):
            continue
        if "bytes" not in site:
            raise ValueError("Patch-eligible review needs exact original bytes")
        operands = [(index, op) for index, op in enumerate(ins.operands) if op.type == X86_OP_MEM and op.mem.base != X86_REG_RIP]
        if len(operands) != 1:
            raise ValueError("Low-review site does not select a unique memory operand")
        index, operand = operands[0]
        old = operand.mem.disp
        if kind == "pool-relative":
            new = layout.pool_offset(site["poolStride"], old)
        else:
            bias = site["missionBaseBiasBytes"]
            new = layout.mission_offset(old + bias) - layout.mission_offset(bias)
        change(address, index, old, new, site["reason"], site["evidenceIds"] + [local_ref("pool-semantic-review-low.json", "sites")], "exact_low_review")
    for site in mid_review.get("instructionSiteMappings", []):
        if site["classification"] != "mission-relative-field":
            continue
        address = int(site["address"], 16)
        if ins_at(address).bytes.hex() != site["instructionBytes"]:
            raise ValueError("Mid-review instruction bytes differ")
        old = int(site["originalValue"], 16)
        bias = site["missionBiasBytes"]
        change(address, site["operandIndex"], old, layout.mission_offset(old + bias) - layout.mission_offset(bias),
               "Exact reviewed pointer provenance identifies this Mission-relative operand (" + site["provenanceKind"] + "); no other site in the function is inferred.",
               site["evidenceIds"] + [local_ref("pool-semantic-review-mid.json", "instructionSiteMappings")], "exact_mid_review")
    for site in mid_review.get("instructionSiteExclusions", []):
        if ins_at(int(site["address"], 16)).bytes.hex() != site["instructionBytes"]:
            raise ValueError("Mid-review exclusion bytes differ")
        reviewed_exclusions[int(site["address"], 16)] = site
    independently_proven_operands = {(s['address'],s['operandIndex']):s for s in mid_review['instructionSiteMappings']}
    for proof in operand_recheck['instructionSiteMappings']:
        if any(not isinstance(e,str) or not e for e in proof['evidenceIds']):
            raise ValueError('Malformed supplemental evidence identifier at '+proof['address'])
        for instruction in proof['provenanceInstructions']:
            if ins_at(int(instruction['address'],16)).bytes.hex()!=instruction['bytes']:
                raise ValueError('Supplemental high-operand proof bytes differ')
        independently_proven_operands[(proof['address'],proof['operandIndex'])]=proof
    high_without_exact_operand_proof=[]
    for site in high_review["sites"]:
        address = int(site["address"], 16)
        if ins_at(address).bytes.hex() != site["rawInstruction"]["bytes"]:
            raise ValueError("High-review instruction bytes differ")
        kind = site["classification"]
        if kind in ("image_relative_global_or_dispatch", "other_numeric_arithmetic", "non_instruction_jump_table_data", "other_owned_object_relative", "pool_element_stride"):
            reviewed_exclusions[address] = site
        elif kind == "mission_relative":
            if site["missionBaseBiasBytes"] != 0:
                raise ValueError("Nonzero high-review pointer bias needs explicit handling")
            refs_for_site = [local_ref("pool-semantic-review-high.json", "sites")]
            refs_for_site.extend(site[k] for k in ("reaEvidenceId", "callbackRegistrationEvidenceId") if site.get(k))
            for match in site["matchedOperands"]:
                if match["operandIndex"] in site["largeOperandIndexes"]:
                    require_pointer_operand_shape(ins_at(address), match['operandIndex'])
                    proof=independently_proven_operands.get((site['address'],match['operandIndex']))
                    if proof is None:
                        high_without_exact_operand_proof.append({'address':site['address'],'operandIndex':match['operandIndex'],
                            'bytes':site['rawInstruction']['bytes'],'reason':'Procedure-level ownership alone is not exact operand provenance; held for independent register/def-use proof.'})
                        continue
                    if (proof['instructionBytes']!=site['rawInstruction']['bytes'] or
                            int(proof['originalValue'],16)!=match['decodedValue'] or proof['missionBiasBytes']!=0):
                        raise ValueError('Independent high-operand proof disagrees with original review')
                    old = match["decodedValue"]
                    exact_refs=proof['evidenceIds']+[local_ref('pool-patch-generator-review.json','highOperandRecheck')
                        if proof in operand_recheck['instructionSiteMappings'] else local_ref('pool-semantic-review-mid.json','instructionSiteMappings')]
                    change(address, match["operandIndex"], old, layout.mission_offset(old), site["reason"], refs_for_site+exact_refs, "exact_high_review")

    for review_name,review in (('native-pool-spill-review.json',spill_review),('native-pool-tick-spill-review.json',tick_spill_review),('native-pool-record-spill-review.json',record_spill_review),('native-pool-attack-spill-review.json',attack_spill_review),('native-pool-missile-spill-review.json',missile_spill_review),('native-job-mission-spill-review.json',job_spill_review),('native-pool-pair-spill-review.json',pair_spill_review),('native-fading-entity-owner-review.json',fading_review)):
        for site in review['instructionSiteMappings']:
            address=int(site['address'],16)
            if ins_at(address).bytes.hex()!=site['instructionBytes'] or site['missionBiasBytes']!=0:
                raise ValueError('Exact spill proof bytes/bias differ')
            for proof_instruction in site['provenanceInstructions']:
                if ins_at(int(proof_instruction['address'],16)).bytes.hex()!=proof_instruction['bytes']:
                    raise ValueError('Spill provenance instruction bytes differ')
            require_pointer_operand_shape(ins_at(address),site['operandIndex'])
            old=int(site['originalValue'],16)
            change(address,site['operandIndex'],old,layout.mission_offset(old),
                'Exact saved Mission pointer survives bounded callees; see the scoped stack/write/CFG proof.',
                site['evidenceIds']+[local_ref(review_name,'instructionSiteMappings')],'exact_spill_review')

    for site in interior_review['instructionSiteMappings']:
        address = int(site['address'],16)
        if ins_at(address).bytes.hex() != site['bytes'] or site['classification'] not in ('pool-relative','mission-relative-immediate','mission-relative-indexed-field'):
            raise ValueError('Interior-pool review bytes or classification differ')
        old = site['originalValue']
        new = layout.pool_offset(site['poolStride'],old) if site['classification']=='pool-relative' else layout.mission_offset(old)
        change(address,site['operandIndex'],old,new,site['reason'],
               site['evidenceIds']+[local_ref('native-pool-interior-review.json','instructionSiteMappings')], 'exact_interior_review')
    for site in interior_review['instructionSiteExclusions'] + scalar_exclusions['instructionSiteExclusions']:
        if ins_at(int(site['address'],16)).bytes.hex() != site['bytes']:
            raise ValueError('Interior-review exclusion bytes differ')
        reviewed_exclusions[int(site['address'],16)] = site

    guard = mode_guard['patch']
    guard_address = int(guard['address'],16)
    guard_ins = ins_at(guard_address)
    if (guard_address != 0x180710c10 or guard_ins.bytes.hex() != '890dbe556900'
            or guard['original'] != '890dbe556900' or guard['replacement'] != '85c97402cd29'
            or ins_at(guard_address+6).bytes.hex() != 'c3' or guard_address in pending):
        raise ValueError('Single-mode guard differs from exact reviewed span')
    pending[guard_address] = {'ins':guard_ins,'bytes':bytearray.fromhex(guard['replacement']),'changes':[],
        'evidence':set(guard['evidenceIds']+[local_ref('native-single-mode-guard.json','patch')]),
        'reasons':[guard['reason']],'scopes':{'single_mode_zero_guard'},'guardSequence':True}

    expected_contact = {
        0x1805b79b8: ('0f1f840000000000','4180fe3f7602cd29',
            [('cmp','r14b, 0x3f'),('jbe','0x1805b79c0'),('int','0x29')]),
        0x1805b7a2c: ('4863c2488d0c80498d04c9e966060000','4883e820807808400f8668060000cd29',
            [('sub','rax, 0x20'),('cmp','byte ptr [rax + 8], 0x40'),('jbe','0x1805b80a2'),('int','0x29')])}
    if {int(g['address'],16) for g in contact_guard['patches']} != set(expected_contact):
        raise ValueError('Unexpected contact guard sites')
    for g in contact_guard['patches']:
        address=int(g['address'],16);old,new,decoded_expected=expected_contact[address]
        if (g['original']!=old or g['replacement']!=new or pe.get_data(address-base,len(bytes.fromhex(old))).hex()!=old
                or address in pending):
            raise ValueError('Contact guard original or replacement differs')
        pending[address]={'ins':ins_at(address),'originalSpan':bytes.fromhex(old),'bytes':bytearray.fromhex(new),'changes':[],
            'evidence':{local_ref('native-contact-capacity-guard.json','patches'),local_ref('native-contact-capacity-independent-review.json','findings')},
            'reasons':['Bound newly built and cached skeleton capsule tables to64 before indexed writes/contact production. Invalid capsule counts fail fast in the isolated probe profile.'],
            'scopes':{'contact_capsule_capacity_guard'},'contactGuardSequence':decoded_expected}

    for edit in renderer_patch['edits'] + ranged_patch['edits']:
        is_ranged = edit in ranged_patch['edits']
        rva = int(edit['rva'], 16)
        address = base + rva
        old, new = bytes.fromhex(edit['originalBytes']), bytes.fromhex(edit['replacementBytes'])
        if (len(old) != len(new) or pe.get_data(rva, len(old)) != old
                or pe.get_offset_from_rva(rva) != edit['fileOffset'] or address in pending):
            raise ValueError('Renderer patch span differs from reviewed source')
        def instruction_rows(instructions):
            return [{'address':hex(i.address),'bytes':i.bytes.hex(),'mnemonic':i.mnemonic,'operands':i.op_str}
                    for i in instructions]
        if instruction_rows(decoder.disasm(old,address)) != edit['originalInstructions']:
            raise ValueError('Renderer original instruction sequence differs')
        pending[address] = {'ins':ins_at(address),'originalSpan':old,'bytes':bytearray(new),'changes':[],
            'evidence':{local_ref(name,'pageGeometry') for name in (('native-ranged-page-patch.json',
                'native-ranged-stall-review.json','native-ranged-guard-independent-review.json',
                'native-ranged-page-consumer-review.json') if is_ranged else ('native-bone-mapping-patch.json',
                'native-renderer-buffer-lifecycle-review.json','native-bone-mapping-independent-review.json'))},
            'reasons':[edit['purpose']], 'scopes':{'ranged_page_geometry_and_bounds' if is_ranged else 'renderer_page_geometry_and_bounds'},
            'rendererSequence':edit['candidateInstructions']}

    patches = []
    for address, entry in sorted(pending.items()):
        original = entry["ins"]
        original_bytes = entry.get('originalSpan',bytes(original.bytes))
        original_decoded = list(decoder.disasm(original_bytes,address))
        if sum(i.size for i in original_decoded)!=len(original_bytes):
            raise ValueError('Original span does not decode fully')
        decoded = list(decoder.disasm(bytes(entry["bytes"]), address))
        is_guard = entry.get('guardSequence',False)
        is_contact = 'contactGuardSequence' in entry
        is_renderer = 'rendererSequence' in entry
        if is_renderer:
            if (sum(i.size for i in decoded) != len(original_bytes)
                    or instruction_rows(decoded) != entry['rendererSequence']):
                raise ValueError('Renderer candidate instruction sequence differs')
        elif is_contact:
            if (sum(i.size for i in decoded)!=len(original_bytes) or
                    [(i.mnemonic,i.op_str) for i in decoded]!=entry['contactGuardSequence']):
                raise ValueError('Contact guard sequence/branch target differs')
        elif is_guard:
            if (sum(i.size for i in decoded) != original.size or
                [(i.mnemonic,i.op_str) for i in decoded] != [('test','ecx, ecx'),('je',hex(address+6)),('int','0x29')]):
                raise ValueError('Single-mode guard instruction sequence/branch target differs')
        elif len(decoded) != 1 or decoded[0].size != original.size or decoded[0].mnemonic != original.mnemonic:
            raise ValueError("Replacement changes instruction boundary/mnemonic at " + hex(address))
        replacement = decoded[0]
        if "specialExpected" in entry:
            if replacement.mnemonic + " " + replacement.op_str != entry["specialExpected"]:
                raise ValueError("Unexpected rewritten byte comparison")
        else:
            for edit in entry["changes"]:
                operand = replacement.operands[edit["operandIndex"]]
                value = operand.imm if operand.type == X86_OP_IMM else operand.mem.disp
                if value != edit["replacementValue"]:
                    raise ValueError("Replacement sign/width decoding differs at " + hex(address))
        for eid in entry["evidence"]:
            if eid.startswith("ev_") and eid not in records:
                raise ValueError("Missing retained REA evidence: " + eid)
        patches.append({"id": "pool4080-" + format(address - base, "x"), "fileOffset": pe.get_offset_from_rva(address - base),
                        "rva": hex(address - base), "address": hex(address), "original": original_bytes.hex(),
                        "replacement": bytes(entry["bytes"]).hex(), "reason": " ".join(dict.fromkeys(entry["reasons"])),
                        "evidenceIds": sorted(entry["evidence"]), "scopes": sorted(entry["scopes"]),
                        "decodedOriginal": '; '.join(i.mnemonic+' '+i.op_str for i in original_decoded),
                        "decodedReplacement": '; '.join(i.mnemonic + ' ' + i.op_str for i in decoded),
                        "operandChanges": entry["changes"], "sameLengthDecodeVerified": True,
                        "decodeContract": "reviewed-renderer-page-and-guard-span" if is_renderer else "reviewed-contact-capacity-multi-instruction-span" if is_contact else "reviewed-single-mode-zero-three-instruction-span" if is_guard else "single-instruction-same-mnemonic",
                        "standaloneSafe": False})
    patched = set(pending)
    image_relative = {int(c["address"], 16) for c in classification["classifications"] if c["classification"] == "image_relative_constant_anchor"}
    if patched & (image_relative | set(reviewed_exclusions)):
        raise ValueError("Reviewed patches overlap image-base exclusions")
    resolved = patched | image_relative | set(reviewed_exclusions)
    remaining_large = [c for c in classification["classifications"] if int(c["address"], 16) not in resolved]
    remaining_inventory = [c for c in inventory["candidates"] if int(c["address"], 16) not in resolved]
    # Final blockers are derived from the strict approval gate below.
    blockers = []
    numeric_triage = {
        'status':'descriptive-only-not-semantic-approval',
        'remainingMatchedCategories':dict(Counter(category for candidate in remaining_inventory for match in candidate['matches'] for category in match['categories'])),
        'remainingImmediateCapacityOrAllocationOperands':[{'address':candidate['address'],'operandIndex':match['operandIndex'],'value':match['decodedValue']}
            for candidate in remaining_inventory for match in candidate['matches'] if match['kind']=='immediate' and match['decodedValue'] in (2039,2040,2041,0x1184b40)],
        'remainingShiftedRangeAddressArithmetic':[{'address':candidate['address'],'bytes':candidate['bytes'],'mnemonic':candidate['mnemonic'],'operands':candidate['operands']} for candidate in remaining_inventory if candidate['mnemonic'] in ('add','sub') and any(match['kind']=='immediate' and 'mission_later_inline_address_range' in match['categories'] for match in candidate['matches'])],
        'remainingImmediateStrideOperands':sum(match['kind']=='immediate' and match['decodedValue'] in (0xc60,0x230,0x2c8) for candidate in remaining_inventory for match in candidate['matches']),
        'interpretation':'The slot strides and primary first-slot base are unchanged by this layout. Their numeric matches are not automatically approved or excluded: owner, biased-pointer arithmetic, and shared callers still determine meaning. Categories overlap. Counts use candidate instruction addresses, not independent semantic obligations.'}
    residual_shapes=Counter()
    unclassified_shapes=[]
    scalar_shifted=[]
    for candidate in remaining_inventory:
        for match in candidate['matches']:
            categories=set(match['categories']);value=match['decodedValue']
            if 'mission_later_inline_address_range' in categories:
                if match['kind']=='immediate' and candidate['mnemonic'] not in ('add','sub','lea'):
                    shape='shifted-range-scalar-or-computation-immediate'
                    scalar_shifted.append({'address':candidate['address'],'mnemonic':candidate['mnemonic'],'value':hex(value)})
                else:shape='unresolved-moved-address-operand'
            elif any(c.startswith('pool_relative:') for c in categories):
                shape='unresolved-pool-relative-metadata'
            elif 'first_agent_inline_address_range' in categories:
                shape='unchanged-primary-first-Agent-address-range'
            elif any(c.startswith('stride:') for c in categories):
                shape='unchanged-slot-stride-numeric-match'
            elif categories <= {'agent_capacity','agent_sentinel','last_valid_agent_index'} and match['kind']=='memory_displacement':
                shape='small-memory-displacement-numerically-equal-to-capacity'
            elif categories=={'mission:agent_slots:missionOffset'}:
                shape='unchanged-primary-pool-base'
            else:
                shape='unclassified-search-shape';unclassified_shapes.append({'address':candidate['address'],'match':match})
            residual_shapes[shape]+=1
    numeric_triage['operandSearchShapes']=dict(residual_shapes)
    numeric_triage['unclassifiedSearchShapes']=unclassified_shapes
    numeric_triage['shiftedRangeScalarImmediateFamilies']=dict(Counter(x['mnemonic'] for x in scalar_shifted))
    reviewed_scalar_tuples=Counter((site['address'],family['value'],site['mnemonic'])
        for family in residual_candidates['valueFamilies'] for site in family['sites'])
    actual_scalar_tuples=Counter((site['address'],site['value'],site['mnemonic']) for site in scalar_shifted)
    if actual_scalar_tuples != reviewed_scalar_tuples or len(scalar_shifted) != residual_review['siteCount']:
        raise ValueError('Current residual scalar candidates differ from the accepted exact review')
    numeric_triage['residualScalarReview']={
        'artifact':'native-pool-residual-scalar-review.json','status':residual_review['status'],
        'candidateArtifactSha256':residual_review['candidateArtifactSha256'],
        'exactCurrentCandidateSetMatches':True,'scope':residual_review['scope'],
        'limitations':residual_review['limitations']}
    numeric_triage['searchBoundary']={
        'fileBackedExecutableBytes':inventory['coverage']['fileBackedExecutableBytes'],
        'decodedUniqueBytes':inventory['coverage']['decodedUniqueBytes'],
        'undecodableByteCount':len(inventory['coverage']['undecodableBytes']),
        'pdataRanges':inventory['coverage']['uniqueAcceptedRuntimeRanges'],
        'uncoveredExecutableBytesSwept':inventory['coverage']['uncoveredExecutableBytes'],
        'negativeAndInteriorAudit':'native-pool-interior-review.json',
        'qualification':'Counts describe decoded operand shapes, not universal semantic completeness. Scalar MOV/mask/comparison constants are retained unchanged; no inference that every numeric match is a pointer. Arbitrary split constants, indirect computed code entries, unrecognized instruction boundaries and third-party modules are outside this bounded static search. Exact owner/CFG/EH proofs govern every selected edit.'}
    result = {"schemaVersion": 1, "sourceSha256": source_hash, "sourceSize": len(source), "sourcePath": str(path),
              "imageBase": hex(base), "targetCapacity": 4080, "status": "blocked", "blockers": blockers,
              "readiness":{"staticReady":False,"runtimeVerified":False,"launchHold":True,
                  "restrictedRuntimeContracts":runtime_contracts['status'],
                  "criteria":{"changedReferenceCoverage":"incomplete","computedAndSharedOwnerCoverage":"incomplete",
                    "externalModuleAndLifecycleCoverage":"incomplete","isolatedProfileIntegration":"requires-root-verification",
                    "gameVerification":"corrected-candidate-not-run","userLaunchHold":"check-and-wait-for-other-game-before-every-launch"}},
              "generatedUtc": datetime.now(timezone.utc).isoformat(), "layout": layout.report(), "sourceEvidence": sources,
              "patches": patches, "coverage": {"instructionEdits": len(patches), "changedInstructionBytes": sum(len(bytes.fromhex(p["original"])) for p in patches),
                  "scopeCounts": dict(Counter(s for p in patches for s in p["scopes"])), "numericInventoryCandidates": len(inventory["candidates"]),
                  "patchedInventoryCandidates": sum(int(c["address"], 16) in patched for c in inventory["candidates"]),
                  "imageBaseExclusions": len(image_relative), "remainingLargeMemoryDisplacementSites": len(remaining_large),
                  "exactReviewedExclusions": len(reviewed_exclusions),
                  "remainingNumericInventoryCandidates": len(remaining_inventory), "semanticCoverageComplete": False,
                  "sameLengthDecodeChecksPassed": len(patches)},
              "remainingLargeMemoryDisplacementSites": [{"address": c["address"], "functionStart": c["functionStart"], "reason": c["reason"]} for c in remaining_large],
              "highReviewOperandsWithheld": high_without_exact_operand_proof,
              "remainingInventoryByFunction": dict(Counter(c["functionStart"] or "uncovered" for c in remaining_inventory)),
              "remainingNumericTriage":numeric_triage,
              "reviewedExclusions": list(reviewed_exclusions.values()),
              "preservedByDesign": jobs["doNotChange"],
              "notes": ["This is a concrete exact-byte review artifact, not a deployable completed patch. The offline writer rejects status=blocked.",
                        "No broad numeric substitution or blanket function relocation is performed. Explicit ABI/pointer/metadata evidence selects the bounded sites.",
                        "Whole instructions are patched as a unit; an instruction with both metadata displacement and capacity sentinel receives one combined edit.",
                        "SourceLayout and evidence hashes are retained so regeneration detects stale analysis. This generator writes JSON only."]}
    generator_hash=hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    external_values=sorted({change['originalValue'] for patch in patches for change in patch['operandChanges']
                            if 0x100000<=change['originalValue']<=0x1184b40})
    external_digest=hashlib.sha256(b''.join(struct.pack('<I',v) for v in external_values)).hexdigest()
    profile_stale=[p['path'] for p in profile_review['reviewedSources']
                   if not Path(p['path']).is_file() or hashlib.sha256(Path(p['path']).read_bytes()).hexdigest()!=p['sha256']]
    unresolved={
        'movedMemoryOperands':len(remaining_large),
        'movedAddressArithmetic':len(numeric_triage['remainingShiftedRangeAddressArithmetic']),
        'capacityOrAllocationLiterals':len(numeric_triage['remainingImmediateCapacityOrAllocationOperands']),
        'poolRelativeMetadata':residual_shapes['unresolved-pool-relative-metadata'],
        'unclassifiedSearchShapes':len(unclassified_shapes),
        'withheldHighOperands':len(high_without_exact_operand_proof),
        'sharedHelperEntries':len(helper_callers['unclassifiedListedEntries']),
        'sharedHelperLiterals':len(helper_escape['remainingUnclassifiedLiterals']),
        'packedIndexCandidates':len(packed_review['unresolvedListedCandidates']),
        'externalOperandCandidates':external_review['instructionOperandCandidates'],
        'externalOperandSetMismatch':int(external_digest!=external_review['operandSetSha256']),
        'profileSourceMismatch':len(profile_stale)}
    approval=read('native-pool-static-readiness-review.json') if (EVIDENCE/'native-pool-static-readiness-review.json').exists() else None
    approval_errors=readiness_approval_errors(approval,source_hash,patches,sources,generator_hash,unresolved)
    static_ready=not approval_errors
    result['canonicalPatchSetSha256']=canonical_patch_set_sha256(patches)
    result['generatorSourceSha256']=generator_hash
    result['readiness']['staticReady']=static_ready
    result['readiness']['staticApprovalErrors']=approval_errors
    result['readiness']['unresolvedStaticObligations']=unresolved
    result['readiness']['criteria'].update({
        'changedReferenceCoverage':'closed-within-reviewed-search-boundary' if not any(unresolved[k] for k in ('movedMemoryOperands','movedAddressArithmetic','capacityOrAllocationLiterals','poolRelativeMetadata')) else 'incomplete',
        'computedAndSharedOwnerCoverage':'listed-helper-entries-and-selected-computed-operands-reviewed',
        'externalModuleAndLifecycleCoverage':'matched-external-literal-set-and-reviewed-EH' if not unresolved['externalOperandCandidates'] and not unresolved['externalOperandSetMismatch'] else 'incomplete',
        'isolatedProfileIntegration':'source-pinned-static-review' if not profile_stale else 'source-review-stale'})
    result['readiness']['scope']='Restricted offline test candidate only: original shipping modules, Single0, fixed pools4080, reviewed provider/job/contact contracts and exact retained search boundary. No universal computed-pointer, arbitrary-mod, runtime-safety or performance claim.'
    result['coverage']['semanticCoverageComplete']=static_ready
    result['coverage']['semanticCoverageScope']='Selected patch references and listed restricted-profile contracts; residual numeric matches are classified by documented search shape, not universally proven absent.'
    result['status']='experimental-static-candidate' if static_ready else 'blocked'
    result['blockers']=approval_errors
    result['notes'][0]='Static acceptance permits preparation of an isolated test candidate. Launch requires the guarded runner and a fresh check for other game sessions; runtime remains unverified.' if static_ready else 'This exact-byte review artifact remains blocked; an exact independently signed-off static evidence set is required before offline preparation.'
    result['runtimeRestrictions']=['Wait until every existing Bannerlord session exits before EVERY launch; never terminate another session.','The prior candidate stalled at2041; corrected renderer runtime behavior, exception injection and performance remain unverified.','Network/replay/SingleRecord modes are outside the candidate and fail fast.','Existing renderer asynchronous completion ordering is retained, not independently re-proved. Mapping and matrix capacities remain finite; bounds violations terminate abnormally.']
    validate_manifest(result)
    validate_original(source, result)
    output = safe_review_output(output, [path,Path(__file__)] + [item["path"] for item in sources.values()])
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"output": str(output), "status": result["status"], "coverage": result["coverage"]}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=EVIDENCE / "native-pool-patch-4080.json")
    build(parser.parse_args().output)
