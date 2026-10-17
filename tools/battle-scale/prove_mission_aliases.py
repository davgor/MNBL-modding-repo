"""Reproduce bounded Mission-register operand proofs without executing Bannerlord.

Tracks must-proven owner identities, affine offsets, and exact stack/SIMD spills.
Unknown entry contracts and decode gaps remain unproved. Writes evidence JSON only.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import sys
from collections import deque

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "build/battle-scale/python-libs"))
import capstone
import pefile
from capstone.x86 import X86_OP_REG, X86_OP_IMM, X86_OP_MEM, X86_REG_RIP
from capstone import CS_GRP_JUMP, CS_GRP_RET
from patch_native_copy import safe_review_output
from native_instruction_semantics import memory_operand_writes

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--output", type=Path, default=ROOT / "evidence/engine-extension/pool-semantic-review-mid.json")
args = parser.parse_args()
p = ROOT / "evidence/engine-extension"
q = ROOT / "build/battle-scale/native-queue"
path = args.output
# Preserve bounded manual review and immediate-backpointer records as input.
d = json.loads((p / "pool-semantic-review-mid.json").read_text())
inv = json.loads((p / "native-pool-reference-inventory.json").read_text())
cf = json.loads((p / "native-pool-reference-classification.json").read_text())
groups = {int(x["root"], 16): x for x in cf["flowGroups"]}
candidate_roots=set(groups)
def eligible_procedure(address):
 return address>=0x1805b0000 and (address<0x180711000 or address in candidate_roots)
bundle_paths = [p / name for name in ("native-ghidra-evidence.json",
                "native-pool-ghidra-evidence.json", "native-expansion-ghidra-evidence.json",
                "native-closure-ghidra-evidence.json")]
retained_records = {}
for bundle_path in bundle_paths:
    bundle = json.loads(bundle_path.read_text())
    if any(a.get("digest", {}).get("sha256") != d["targetSha256"] for a in bundle["artifacts"]):
        raise ValueError("Retained evidence bundle contains another target")
    retained_records.update((r["evidence_id"], r) for r in bundle["records"])
request_map = d["missionMethodFlowReview"]["requestEvidenceMap"]
unusable_requests = set(d["missionMethodFlowReview"]["requestsWithoutUsableEvidence"])
texts, evs = {}, {}
def evidence(number):
    return retained_records[request_map[str(number)]]
for n in [12, 35, 36, 44] + [r["requestNumber"] for r in d["records"]]:
    if n in unusable_requests:
        continue
    x = evidence(n)
    s = x.get("normalized_result", "")
    s = s.replace("\r", "") if isinstance(s, str) else ""
    m = re.search(r"\bFUN_([0-9a-f]+)\(", s)
    if m:
        texts[int(m[1], 16)] = s
        evs[int(m[1], 16)] = x.get("evidence_id")
# Add retained procedure bodies without relying on ignored request files.
for record in retained_records.values():
    if record.get('tool') != 'procedure_pseudo_code' and record.get('operation') != 'procedure_pseudo_code':
        # Older exports use tool_name; the signature check below also rejects assembly.
        pass
    s = record.get('normalized_result', '')
    if not isinstance(s, str):
        continue
    s = s.replace('\r', '')
    m = re.search(r'\bFUN_([0-9a-f]+)\([^;]*?\)\s*\{', s)
    if m and '0x180' not in s[:m.start()].lower():
        texts.setdefault(int(m[1], 16), s)
        evs.setdefault(int(m[1], 16), record['evidence_id'])
def refs(*numbers):
    return [evidence(n)["evidence_id"] for n in numbers]
spec = importlib.util.spec_from_file_location("flow", ROOT / "tools/battle-scale/classify_pool_references.py")
flow = importlib.util.module_from_spec(spec)
spec.loader.exec_module(flow)
source = Path(inv["binary"]["path"]).read_bytes()
if hashlib.sha256(source).hexdigest() != d["targetSha256"]:
    raise ValueError("Source binary does not match reviewed target")
pe = pefile.PE(data=source)
base = pe.OPTIONAL_HEADER.ImageBase
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
md.detail = True
# Exact local __chkstk implementation saves/restores R10/R11 and RSP, and
# otherwise only probes newly reserved pages below the caller's current RSP.
stack_probe_hex = '4883ec104c8914244c895c24084d33db4c8d5424184c2bd04d0f42d3654c8b1c25100000004d3bd3f27317664181e200f04d8d9b00f0ffff41c603004d3bd3f275ef4c8b14244c8b5c24084883c410f2c3'
if pe.get_data(0x9e0760,len(bytes.fromhex(stack_probe_hex))) != bytes.fromhex(stack_probe_hex):
    raise ValueError('Reviewed stack probe implementation changed')
simple_init_hex='48c701ffffffff33d289510833c0488951104889511848895120895128c7412c0000803fc74130cdcc4cbec74134cdcc4cbe48c74138cdcccc3e66c741400001c641480148c7414cffffffff48c74178ffffffffc68180000000ff48899184000000c74174ffffffff488941544889415c488941644889416cc7818c0000000000803fc3'
if pe.get_data(0x698570,len(bytes.fromhex(simple_init_hex)))!=bytes.fromhex(simple_init_hex):raise ValueError('Reviewed leaf initializer changed')
allnew, reports = [], []
old = {(x["address"], x["operandIndex"]): x for x in d["instructionSiteMappings"]
       if x.get("provenanceKind") != "Mission-entry-register-conservative-CFG"}
def calls(s):
 for m in re.finditer(r'FUN_([0-9a-f]+)\(',s):
  a=m.end();dep=0
  for z in range(a,len(s)):
   c=s[z]
   if c=='(':dep+=1
   elif c==')':
    if dep==0:break
    dep-=1
   elif c==',' and dep==0:break
  yield int(m[1],16),re.sub(r'\s+','',s[a:z]),m.start()

def receiver_kind_for_argument(contract):
    """Ghidra param_1 is native RCX, including an untyped hidden result pointer."""
    if contract.get('entryBiases',{}).get('rcx',0)!=0:return None
    return contract.get('entryRegisters', {'rcx':contract['entryPointerKind']}).get('rcx')

def conflicting_entry_registers(contract, observed):
    """Return concrete owner contradictions; absence of a proof is not a type."""
    expected = contract.get('entryRegisters', {'rcx':contract['entryPointerKind']})
    return {register: {'expected':kind, 'observed':observed[register]}
            for register,kind in expected.items()
            if register in observed and observed[register] != kind}
contracts = {
 0x1805c0e20: {'kind':'externally-established-Mission-entry', 'evidenceIds':refs(296,119,488,61), 'proof':'Registered InitializeMission7076d0 passes Mission to5d1b60. That function stores Mission in Mission_loader_task+0xc8. Task65ce00 passes that exact+200decimal field to5c0e20.'},
 0x1805c37d0: {'kind':'externally-established-Mission-entry', 'evidenceIds':refs(489,130,62), 'proof':'572ee0 passes current Mission globalDAT180da9d60 to5d4810.5d4810 forwards unchanged param_1 to5c37d0.'},
 0x1805cd130: {'kind':'externally-established-Mission-entry', 'evidenceIds':refs(1,12), 'proof':'Registered CreateAgent wrapper7084a0 passes native Mission receiver to5cd130.'},
 0x1805cf3c0: {'kind':'externally-established-Mission-entry', 'evidenceIds':refs(35,44), 'proof':'Mission constructor5ce3f0 passes its constructed Mission receiver directly to5cf3c0.'},
 0x1805d4810: {'kind':'externally-established-Mission-entry', 'evidenceIds':refs(489,130), 'proof':'572ee0 passes current Mission global DAT180da9d60 directly to5d4810.'},
}
for contract in contracts.values():
    contract['entryPointerKind'] = 'mission'
contracts[0x180647080] = {'kind':'global-only-analysis-root','entryPointerKind':None,
 'entryRegisters':{},'evidenceIds':['ev_dbeb9110c41451f7a964f29db4d1cff34e52c9cbeedc258f5d72fdcfdb4313a1'],
 'proof':'Receiver remains unknown. Analyze only locally established current-Mission global loads and their direct descendants.'}
owner_field_proofs = {'evidenceIds':['ev_14f983a8fad3ac142186a868eae65b72c6a72a90027af17ec43ecc035ca1019e',
 'ev_f92983c2d6937c82b5bd12536186cf6d6dd398c6befcd0a342a66186ed457853',
 'ev_8091dcb32b9af15ce665769d431a718224cbb614069b408d4833f05c3064492f'],
 'proof':'Agent initializer607550 constructs69e1d0(allocation,Agent), stores returned AI in Agent+888, calls69e8a0. Constructor69e1ef stores RDX Agent at AI+0. Initializer69e8ba/bd/c4 loads AI+0 Agent, then Agent+ab8 Mission, stores AI+14d8. Agent initializer also sets Agent+148 and+ab8 Mission.'}
owner_field_proofs['exactStores']=[{'address':'0x18069e1ef','bytes':'488911'},
 {'address':'0x18069e8ba','bytes':'488b01488b90b80a0000488991d8140000'},
 {'address':'0x18069e236','bytes':'48899b80130000'},
 {'address':'0x18069bf1f','bytes':'48895128'}]
owner_field_proofs['evidenceIds'].append('ev_dcac7d366f062b76a1fcca388aba7940bcf2b84974be0dc1c8e7c8a85dd7263b')
owner_field_proofs['subobjects']='69e1d0 stores AI self at+8 and+1380, and invokes69bf00(AI+180,AI);69bf1f stores second argument at subobject+28, proving AI+1a8 points to AI.'
for store in owner_field_proofs['exactStores']:
 raw=bytes.fromhex(store['bytes'])
 if pe.get_data(int(store['address'],16)-base,len(raw))!=raw:raise ValueError('Reviewed owner initializer bytes changed')
contracts[0x18069e8a0] = {'kind':'confirmed-Agent-AI-initializer','entryPointerKind':'agent-ai',**owner_field_proofs}
safety_path=p/'pool-runtime-safety-contracts.json'
safety=json.loads(safety_path.read_text())
if safety['targetSha256']!=d['targetSha256']:raise ValueError('Worker contract target mismatch')
worker_proofs=safety['contracts']['workerClosureEntries']
list_proof=safety['contracts']['agentPointerLists']
def check_proof_bytes(value):
 if isinstance(value,dict):
  if 'address' in value and 'bytes' in value:
   raw=bytes.fromhex(value['bytes'])
   if pe.get_data(int(value['address'],16)-base,len(raw))!=raw:raise ValueError('Worker/list proof bytes changed')
  for v in value.values():check_proof_bytes(v)
 elif isinstance(value,list):
  for v in value:check_proof_bytes(v)
check_proof_bytes(worker_proofs);check_proof_bytes(list_proof)
independent_review_path=p/'pool-patch-generator-review.json'
independent_review=json.loads(independent_review_path.read_text())
visual_strength_proof=independent_review['recoveredVisualStrengthContracts']
check_proof_bytes(visual_strength_proof)
for entry in visual_strength_proof['entryContracts']:
 contracts[int(entry['procedure'],16)]=dict(entry,entryPointerKind=entry['entryRegisters'].get('rcx'))
missile_proof=independent_review['highOperandRecheck']['supportingOwnerProofs']['missileMissionOwner']
if missile_proof['targetSha256']!=d['targetSha256']:raise ValueError('Missile owner proof target mismatch')
check_proof_bytes(missile_proof)
missile_list_proof={'proof': '5d5000 rebuilds vector1044798/end10447a0 by walking Missile page table1044770. R15 at50b1 is page[(index>>4)] +(index&15)*c00;50c9 appendsR15 fastpath and5147 appendsR15 after allocation+memmove.5163 stores newbegin. Worker5dffb0 loads this vector and passes indexed pointers to661c40.', 'evidenceIds': [], 'proofInstructions': [{'address': '0x1805d5000', 'bytes': '40574883ec3080b9b847040100488bf90f84c8010000488b819847040148895c244033db488981a0470401448b81484704014585c07e2e4c8b897047040166908bc383e00f8bd348c1ea04488d0c40498b04d148c1e10a837c0808007507ffc3413bd87cdb413bd80f8d6401000048896c244848897424504c897424284c897c24200f1f400066660f1f840000000000488b8770470401488bb7a04704018bcb83e10f8bd348c1ea044c8d3c4949c1e70a4c033cd0483bb7a84704017313488d4608488987a04704014c893ee9a7000000488b9798470401488bc6482bc248c1f8034885c0740e488d2c004885ed750a4533f6eb32bd01000000440fb687b0470401488d14ed00000000488b0d97d87c00e82a4a1500488b97984704014c8bf0488bb7a0470401483bd67505498bc6eb16482bf2498bce4c8bc6e86ee0400048c1fe03488d04f04c8938488d7008488b8f984704014885c97405e811911700498d04ee4c89b798470401488987a84704014889b7a0470401448b8748470401ffc3413bd87d384c8b8f704704010f1f008bc383e00f8bd348c1ea04488d0c40498b04d148c1e10a837c0808007507ffc3413bd87cdb413bd80f8cd2feffff4c8b7c24204c8b742428488b742450488b6c2448488b5c2440c687b8470401004883c4305fc3'}]}
missile_list_proof['evidenceIds']=refs(134)
check_proof_bytes(missile_list_proof)
contracts[0x180660c00]={'kind':'registered-AddMissile-native-initializer',
 'entryPointerKind':'missile','entryRegisters':{'rcx':'missile','rdx':'mission'},
 'evidenceIds':missile_proof['evidenceIds'],'proof':missile_proof['proof']}
scene_contract_path=p/'native-scene-callback-contracts.json'
scene_contracts=json.loads(scene_contract_path.read_text())
if scene_contracts['targetSha256']!=d['targetSha256']:raise ValueError('Scene contract target mismatch')
check_proof_bytes(scene_contracts)
for entry in scene_contracts['contracts']:
 contracts[int(entry['procedure'],16)]=dict(entry,entryPointerKind='mission')
leaf_owner_path=p/'native-leaf-owner-contracts.json'
leaf_owner_contracts=json.loads(leaf_owner_path.read_text())
if leaf_owner_contracts['targetSha256']!=d['targetSha256']:raise ValueError('Leaf owner proof target mismatch')
check_proof_bytes(leaf_owner_contracts)
for entry in leaf_owner_contracts['entryContracts']:
 contracts[int(entry['procedure'],16)]=dict(entry,entryPointerKind=entry['entryRegisters'].get('rcx'))
secondary_path=p/'native-secondary-call-contracts.json'
secondary_contracts=json.loads(secondary_path.read_text())
if secondary_contracts['targetSha256']!=d['targetSha256']:raise ValueError('Secondary contract target mismatch')
check_proof_bytes(secondary_contracts)
for entry in secondary_contracts['entryContracts']:
 contracts[int(entry['procedure'],16)]=dict(entry,entryPointerKind=entry['entryRegisters'].get('rcx'))
tick_spill_path=p/'native-pool-tick-spill-review.json'
tick_spill_proof=json.loads(tick_spill_path.read_text())
if tick_spill_proof['targetSha256']!=d['targetSha256']:raise ValueError('Tick spill target mismatch')
check_proof_bytes(tick_spill_proof)
exact_reload_seeds={(0x1805c6f40,int(a,16)):seed for a,seed in tick_spill_proof['exactReloadSeeds'].items()}
initializer_path=p/'native-initializer-spill-contracts.json'
initializer_proof=json.loads(initializer_path.read_text())
if initializer_proof['targetSha256']!=d['targetSha256']:raise ValueError('Initializer spill target mismatch')
check_proof_bytes(initializer_proof)
for address_,seed in initializer_proof['exactReloadSeeds'].items():exact_reload_seeds[(0x1805c0e20,int(address_,16))]=seed
attack_spill_path=p/'native-pool-attack-spill-review.json'
attack_spill_proof=json.loads(attack_spill_path.read_text())
if attack_spill_proof['targetSha256']!=d['targetSha256']:raise ValueError('Attack spill target mismatch')
check_proof_bytes(attack_spill_proof)
for address_,seed in attack_spill_proof['exactReloadSeeds'].items():exact_reload_seeds[(0x1805c8ed0,int(address_,16))]=seed
attack_late_path=p/'native-pool-attack-late-spill-review.json'
attack_late_proof=json.loads(attack_late_path.read_text())
if attack_late_proof['targetSha256']!=d['targetSha256']:raise ValueError('Late attack target mismatch')
check_proof_bytes(attack_late_proof)
for address_,seed in attack_late_proof['exactReloadSeeds'].items():exact_reload_seeds[(0x1805c8ed0,int(address_,16))]=seed
agent_job_path=p/'native-agent-job-record-contracts.json'
agent_job_proof=json.loads(agent_job_path.read_text())
if agent_job_proof['targetSha256']!=d['targetSha256']:raise ValueError('Agent job target mismatch')
check_proof_bytes(agent_job_proof)
agent_job_dispatch={'proof': 'FrameR13Mission is preserved;5c4c47 setsRDI=Mission+1043e20. No RDI write before4e80. Consumer obtains page at[RDI+8+(index>>4)*8], adds(index%16)*50, branchesonlyonrecord48 andpassesRCXrecordunchanged to685a90or686450. This matches producer table1043e28 and80byte record whose+20Agent is independently proved.', 'proofInstructions': [{'address': '0x1805c4c47', 'bytes': '498dbd203e0401448b37418bdc4585f67e460f1f80000000008bc3c1e8048bd0c1e0048bcb2bc84c8b44d7084863c1488d0c8048c1e1044c03c14c894424604c89442460498b084885c97405e8d8951800ffc3413bde7cc1418bc48707458b85a0390401488d85180200004889442460488d05f2bb010048898528020000488d05d4bb0100488985300200004c89ad18020000488d8518020000488944242848c7442420ffffffff33d2448d4a01e8e6e0c2ff488d85380200004889442460488d0553bb010048898548020000488d0505ba0100488985500200004c89ad38020000488d8538020000488944242848c7442420ffffffff41b902000000448bc633d2e892e0c2ff48630748897c24708944247848897da0448965a885c00f84d6000000488bd8418bcc4885c07407ffc148d1f875f98d41ff4c63c04d03c00f28742470660f7fb5a00200000f287da0660f7fbdd0020000488d95a0020000488d8dd0020000e86fcc01004883fb1c7e6c48897c2470c74424781c0000000f28442470660f7f85f0020000660f7fbde0020000488d95f0020000488d8de0020000e8e4ce010048897c2470c74424781c000000660f7fb5000300000f28442470660f7f85b0020000488d9500030000488d8db0020000e80fa60100eb1d660f7fb5c0020000660f7f7d30488d95c0020000488d4d30e890ce01008b37418bdc85f67e3d0f1f80000000008bcbc1e9048bc1c1e0048bd32bd0488b4ccf08488d149248c1e2044803ca807948007407e8170c0c00eb05e8d0150c00'}]}
check_proof_bytes(agent_job_dispatch)
for entry in agent_job_proof['suggestedConsumerContracts']:
 contracts[int(entry['entryAddress'],16)]={'kind':'typed-secondary-Agent-job-record','entryPointerKind':None,'entryRegisters':{entry['entryRegister']:entry['pointerKind']},'evidenceIds':agent_job_proof['evidenceIds']+refs(62),'proof':agent_job_dispatch['proof']}
movement_path=p/'native-movement-spill-contracts.json'
movement_proof=json.loads(movement_path.read_text())
if movement_proof['targetSha256']!=d['targetSha256']:raise ValueError('Movement proof target mismatch')
check_proof_bytes(movement_proof)
for entry in movement_proof['entryContracts']:contracts[int(entry['procedure'],16)]=dict(entry,entryPointerKind=entry['entryRegisters'].get('rcx'))
for address_,seed in movement_proof['exactReloadSeeds'].items():
 root=0x1806c9430 if int(address_,16)<0x1806d0000 else 0x1806d07c0
 exact_reload_seeds[(root,int(address_,16))]=seed
private_home_proof={'root': '0x18061f470', 'entryRegisters': {'rdx': 'mission'}, 'proof': 'EntryMissionRDX saved atentryRSP+10 (currentRSP+b8)61f473. The only write to that8byte home is the entry store; its address never escapes. Caller-local pointerRSP38 is passed to5bc1a0, which writes exactly8bytes and returns thataddress;693620 reads its firstpointer and never passes/stores the localaddress, only calls virtuals on the pointee. Other calls receive heap objects/Mission, not addresses of home. Loop reloads574,5ad,63d therefore retainMission; even coldvirtualbranch684receives heapobject only. Original body and both bounded local consumers are pinned below.', 'proofInstructions': [{'address': '0x18061f470', 'bytes': '488bc44889501055565741544155415641574883ec7048c74098feffffff488958080f2970b8488bc24c8be94533c9418bf944894c24304c8db190000000498bee458d61054c8d1524d64f00f30f1035d8be500049833e000f8481010000488d98704e0401488b4310482b430848c1f8024885c07512488bcbe832a1fbff4533c94c8d15e8d54f00488b43104883c0fc8b10488943108bf2448bfa49c1ef0483e60f48c1e606488b43284a8b0cf8c7440e0801000000ff43483b137c058d42018903488b43284a8b14f84803d6488b024c8b4008493bc20f85370100004c894a104c898c24c8000000488b4a1848898c24c80000004c894a184885c97406488b01ff5038488b8424b8000000488b80984e04014a8b1cf84803de488d4424384889442448488d542438488bcde8ffcbf9ff90c6442420010f28de4c8bc0488b9424b8000000488bcbe863400700498bb5b8080000498b1e48895c24504885db7409488b03488bcbff502883cf02897c2430c7442428f5010000c64424200041b1014533c0488bd3488bcee831a2d2ff83e7fd897c24304885db7409488b03488bcbff5038498b85b808000066ff80d8020000488b88700100004885c97405e8cdecd2ff33d2488bcde803730b00488b8424b80000004533c94c8d1591d44f004881c5800100004981c6800100004983ec010f855dfeffff488b9c24b00000000f287424604883c470415f415e415d415c5f5e5dc3488bca41ffd0e9e5feffff'}, {'address': '0x1805bc1a0', 'bytes': '4889542410534883ec3048c7442428feffffff488bdac744242000000000488b0948890a4885c97407488b01ff502890c744242001000000488bc34883c4305bc3'}, {'address': '0x180693620', 'bytes': '4c89442418574883ec4048c7442420feffffff48895c245848897424680f297424300f28f3498bf0488bd948895110498b3848897c24504885ff740a488b07488bcfff502890488b4b1848894c245048897b184885c97406488b01ff5038f30f1173380fb644247088432048634330488d0d1af37000488b04c148894328488b0e4885c97406488b01ff5038488b5c2458488b7424680f287424304883c4405fc3'}], 'seeds': {'0x18061f574': {'register': 'rax', 'pointerKind': 'mission', 'biasBytes': 0, 'instruction': {'address': '0x18061f574', 'bytes': '488b8424b8000000'}}, '0x18061f5ad': {'register': 'rdx', 'pointerKind': 'mission', 'biasBytes': 0, 'instruction': {'address': '0x18061f5ad', 'bytes': '488b9424b8000000'}}, '0x18061f63d': {'register': 'rax', 'pointerKind': 'mission', 'biasBytes': 0, 'instruction': {'address': '0x18061f63d', 'bytes': '488b8424b8000000'}}}}
check_proof_bytes(private_home_proof)
for address_,seed in private_home_proof['seeds'].items():exact_reload_seeds[(0x18061f470,int(address_,16))]=seed
network_proof={'kind': 'network-Mission-global-context', 'entries': ['0x1805e5d80', '0x1805e5e40', '0x1805e6a40', '0x1805e6b60', '0x1805e6cf0', '0x1805e6dd0', '0x1805e6c10', '0x1805e5eb0', '0x1805e5fb0', '0x1805e6070', '0x1805e60e0', '0x1805e6240', '0x1805e62b0', '0x1805e63c0', '0x1805e6590', '0x1805e5ce0', '0x1805e66f0', '0x1805e68f0', '0x1805e6ec0'], 'registration': '0x1806e9ea0', 'registrationBytes': '8039000f8593010000c60101488d15adfe6b0048895138488d05c2beefff48894140488d0577bfefff48894150488d056ccbefff4889514848894160488d057dccefff4889515848894170488d05fecdefff4889516848898180000000488d05ccceefff4889517848898190000000488d05faccefff48899188000000488981a0000000488d05b5cdefff48899198000000488981b0000000488d0570bfefff488991a8000000488981c0000000488d055bc0efff488991b8000000488981d0000000488d0506c1efff488991c8000000488981e0000000488d0561c1efff488991d8000000488981f0000000488d05acc2efff488991e800000048898100010000488d0507c3efff488991f800000048898110010000488d0502c4efff4889910801000048898120010000488d05bdc5efff4889911801000048898130010000488d05f8bcefff4889912801000048898140010000488d05f3c6efff4889913801000048898150010000488d05dec8efff4889914801000048898160010000488d0599ceefff488991580100004889817001000048899168010000c3', 'dispatchInstructions': [{'address': '0x180647388', 'bytes': '48c1e604488d05252576004803f0488b0e4885c975064c39660874094c8bc3498bd6ff5608'}, {'address': '0x18064864e', 'bytes': '4803f6488d0560127600488b0cf04885c9750e4c3964f0087507ffc3e931ffffff4c8bc5498bd7ff54f008'}], 'evidenceIds': ['ev_a71eb29c22d900f362febff6d995e988b40a0244e7b1c943cdca4ef724ed71dc', 'ev_dbeb9110c41451f7a964f29db4d1cff34e52c9cbeedc258f5d72fdcfdb4313a1'], 'proof': 'Mission reset5cf3c0 calls6e9ea0(&DATda98b0). Registration binds listed function pointers beside context &DATda9d60. Dispatcher647388..3aa indexes16-byte rows fromDATda98b8, loadsRCX fromrow+0 and CALLs row+8. Seconddispatcher64864e..675 agrees. Thus first callback argument is address of currentMission global; dereference+0 yieldsMission.'}
check_proof_bytes(network_proof)
if pe.get_data(0x6e9ea0,len(bytes.fromhex(network_proof['registrationBytes'])))!=bytes.fromhex(network_proof['registrationBytes']):raise ValueError('Network registration bytes changed')
for entry in network_proof['entries']:
 contracts[int(entry,16)]={'kind':'registered-network-Mission-context','entryPointerKind':None,'entryRegisters':{'rcx':'mission-path:0'},'proof':network_proof['proof'],'evidenceIds':network_proof['evidenceIds']}
missile_worker_proof={'kind': 'two-Missile-heap-worker-captures', 'proof': 'ProvenMissile method65eeb0 savesRCX inR13 at65ef1f. Both48byte capturebranches placeR13 into second8bytefield (65f228 and65f2be), copythree16byteblocks intoheap, andbind667770/667610 respectively. Both manager667780/667690 copyall48bytes.667610 dereferencesentryR8 thenloadsheap+8 intoRCX before65ed10;667770 dereferencesR8 andforwardsheapcontext to65f490, whichloadsits+8Missile. Exactcaptureanddispatchbytesretained.', 'proofInstructions': [{'address': '0x18065eeb0', 'bytes': '488bc45556574154415541564157488da838fbffff4881ec9005000048c74508feffffff488958100f2970b80f2978a8440f294098440f294888440f299078ffffff440f299868ffffff440f29a058ffffff488b057f046a004833c4488985100400004c894da0488bfa48895424604c8be94c89442450488b5910f30f10834c480401f30f5f8350480401f30f10ab48480401f30f592d11c74c00f30f58e84c8d4210f30f104204f3410f5c4004f30f101af3410f5c18f30f59dbf30f59c0f30f58d833c0f30f100de7be4c000f2fd90f96c0660f6ec00f5bc0f30f59c1f30f58d80f28cb0f28d1f30f52c20f280d9ddc4c000f282526d74c00f30f59e0f30f59c0f30f59d0f30f5ccaf30f59e1f30f59e3f30f5925d2c34c00f30f586220f30f582529c14c00f30f11642440c7442430000000000f2f2dd4c94c000f87b80200004533e40f57c00f1145884c89642468488d44246848894424200f28dd488d8b78431801e89686f7ff488b4424684885c00f84ef0100004533ff450f57d2f3440f101d14c54c000f1f840000000000488b004a8944fd1041ffc449ffc74981ff800000000f8db7010000488d542468488d8b78431801e8b4e3f7ff84c0488b4424680f8490010000f30f106d94f30f107d8cf30f5ceff30f107590f3440f104588f3410f5cf0440f28cef3440f59ce0f28c5f30f59c5f3440f58c8f3440f1065988b7584448b75808b7c2478448b4c2474448b442470660f1f840000000000f30f10600c0f28ccf30f5ccff30f1058080f28d3f3410f5cd0f30f59d60f28c5f30f59c1f30f58d0f3410f5ed1410f2fd27306410f28d2eb05f3410f5dd30f28cdf30f59ca0f28c7f30f58c1f30f5ce00f28cef30f59caf3410f58c8f30f5cd9f30f59dbf30f59e4f30f58dc410f2fdc0f86c3000000488b401848894424684885c00f95c14885c00f85a30000006690453bc6751a443bce0f848e000000448bc7897c247041ffc144894c2474eb0841ffc04489442470488b4424704889442438448b9378431801418bc09941f7fa8bca85d279034103ca894c2438448b9b7c4318018b44243c9941f7fb85d278068954243ceb08428d041a8944243c488b44243848c1e820410fafc203c14898488d0cc0488b8380431801488b04014885c00f95c14885c00f8464ffffff488944246884c90f85c7feffff4885c00f852efeffff488b7c2460488d45a84889442460488d4510488945d84c896de048897de8488d442440488945f0488d442450488945f8488d44243048894500488d052e850000488945b8488d0513850000488945c0440fb60da3666a00ba30000000448d42d8488b0d2f377400e862a60c000f1045d80f11000f104de80f1148100f1045f80f114020488945a8488d45a84889442428458bc4e9b40000004881c338790301488d45d8488944246048895da84c896db0488955b8488d442440488945c0488d442450488945c8488d442430488945d0488d05a8830000488945e8488d051d830000488945f0440fb60d0d666a00ba30000000448d42d8488b0d99367400e8cca50c000f1045a80f11000f104db80f1148100f1045c80f114020488945d880bbe43f00000074088b83e03f0000eb12488b83c83f0000482b83c03f000048c1f803488d4dd848894c2428448bc048c7442420ffffffff41b90100000033d2e86c3ab9ff'}, {'address': '0x180667610', 'bytes': '3bca7d78534883ec30498b18488974244048897c24484863f948c1e7032bd18bf2488b1380bae43f0000007507488b92c03f0000488b4b204c8b4318488b4328488b14174889442428488b01f3410f10184c8b4310488b4b084889442420e89d76ffff4883c7084883ee0175b4488b7c2448488b7424404883c4305bc3'}, {'address': '0x180667690', 'bytes': '40574883ec3048c7442420feffffff48895c2440488bfa488bd9458bc84183e9030f84a50000004183f9010f848d0000004585c0746d4183e80174204183f801756f488b0248890148c7020000000033c0488b5c24404883c4305fc3440fb60d18e26900ba30000000448d42d8488b0da4b27300e8d7210c0090488b0f0f10010f11000f1049100f1148100f1041200f11402048890333c0488b5c24404883c4305fc3488b094885c97406e8306b0e009033c0488b5c24404883c4305fc3488b01488b5c24404883c4305fc3488d058d496b00488b5c24404883c4305fc3cccc'}, {'address': '0x180667770', 'bytes': '498bc0448bc28bd1488b08e9107dffff'}, {'address': '0x180667780', 'bytes': '40574883ec3048c7442420feffffff48895c2440488bfa488bd9458bc84183e9030f84a50000004183f9010f848d0000004585c0746d4183e80174204183f801756f488b0248890148c7020000000033c0488b5c24404883c4305fc3440fb60d28e16900ba30000000448d42d8488b0db4b17300e8e7200c0090488b0f0f10010f11000f1049100f1148100f1041200f11402048890333c0488b5c24404883c4305fc3488b094885c97406e8406a0e009033c0488b5c24404883c4305fc3488b01488b5c24404883c4305fc3488d05dd486b00488b5c24404883c4305fc3cccc'}]}
missile_worker_proof['evidenceIds']=refs(214)+missile_proof['evidenceIds']
check_proof_bytes(missile_worker_proof)
for target in [0x180667610,0x180667770]:
 contracts[target]={'kind':'exact-Missile-heap-worker-capture','entryPointerKind':None,'entryRegisters':{'r8':'missile-path:0:8'},'evidenceIds':missile_worker_proof['evidenceIds'],'proof':missile_worker_proof['proof']}
job_agent_queue_proof={'procedure': '0x1805ec6b0', 'kind': 'proven-Agent-pointer-job-producer-consumer', 'entryRegisters': {'rcx': 'agent'}, 'entryPointerKind': 'agent', 'proof': '5c5f60 readsAgent from proven1037938vector (inline orheap+3fc0) intoRBX at5fda. RBX preserved until60bd storesAgent into first8bytes of16byte jobrecord;611e writesrecord to paged10439a8 table. No caller-local pointer passedbetween60bdand611e; allocation/free calls takeglobal/heap only. Consumer5c6180 computesmatchingpage/slot, readsfirst8bytes intoRCX6248 andcalls5ec6b0. Frame resetsjobcount andsubmits producerthenconsumer; retainedjobtable lifetimeaudit covers pages.', 'proofInstructions': [{'address': '0x1805c5f60', 'bytes': '413bd00f8d14020000488bc441574883ec70488958084c8bf94c8960e84c8968e04c8d2d28ca7d004c8970d80f2970c8f30f10359c4f56000f2978b80f57ff488968184c63f249c1e603442bc248897020458be0488978f00f1f840000000000498b0f80b91cb9030100488d99387903017507488b9bc03f0000498b1c1e4533c944898c24880000004c398b880500000f842c01000083bb60050000010f851f0100008b8364050000a918000800742e498b47084c8d89f8380401488d4b284c8d842488000000f30f1008e860970b008b8364050000448b8c24880000000fbae00b7365488b8bf808000080b9b8050000007455486381c80500000f57c0498b44c500482b81c0050000f3480f2ac0f30f59c60f2fc77631c681b805000000488b8bc80a00004883c108e8f11a0a00488b8bc80a00004883c108e8e11a0a00818ba0040000007c00004585c97474498b3fbe010000004881c7a039040148895c242044894c2428f00fc1378bc6c1e8048be8488b44c7084885c0752c488b0dc5c87d0041b008ba00010000e8503a1600488bc833c0f0480fb14cef08740a4885c97405e868811800488b54ef088bc50f10442420c1e0042bf08bc64803c00f1104c2498b47108038007408488bcbe8fdca04004983c6084983ec010f857ffeffff0f287c24300f287424404c8b7424504c8b6c24584c8b642460488b7c2468488bb42498000000488bac2490000000488b9c24800000004883c470415fc3'}, {'address': '0x1805c6180', 'bytes': '413bd00f8da3010000488bc4554154415541564157488bec4881ec8000000048c745a0feffffff488958084889701048897818458bf88bf24c8be14533ed48bb05ec4fc0fe04ec4f0f1f8400000000008bcec1e9048bc1c1e0048bd62bd0498b0424488b8cc8a83904018bc248c1e0044803c8488d45b0488945480f57c0660f7f45b04c896dc0c745c8030000004c896dd0c745d8ffffffff48c745dc0000000048c745e4ffffffffc745ecffffffff66c745f00000c745f4ffffffffc645f8004c8d45b08b5108488b09e860640200488b4db8482b4db0488bc348f7e948c1fa0a488bc248c1e83f4803d0747d498b1c244881c3203e0401bf01000000f00fc13b8bc7c1e804448bf0488b44c3084885c0752c41b008ba00050000488b0d05c77d00e898381600488bc833c0f04a0fb14cf308740a4885c97405e8b07f1800418bc6c1e0042bf84a8b54f308488d0cbf48c1e1044803ca488d55b0e82f61010048bb05ec4fc0fe04ec4f488d45b048894548488b4db04885c97405e86f7f1800ffc6413bf70f8cc4feffff4c8d9c2480000000498b5b30498b7338498b7b40498be3415f415e415d415c5dc3'}]}
job_agent_queue_proof['evidenceIds']=refs(103,104,135)
check_proof_bytes(job_agent_queue_proof)
contracts[0x1805ec6b0]=job_agent_queue_proof
network_leaf_proof={'procedure':'0x1805d0970','entryRegisters':{'rcx':'mission'},'entryPointerKind':'mission','kind':'network-Mission-context-conditional-tail','evidenceIds':network_proof['evidenceIds'],'proof':'Registration6e9f24 binds5e6ce0 besideMission-global context under exactnetworkdispatcher contract. Leaf dereferencesRCX, testsnull and tailJNE5d0970 withoutchangingRCX.','proofInstructions':[{'address':'0x1806e9f24','bytes':'488d05b5cdefff'},{'address':'0x1805e6ce0','bytes':'488b094885c90f85849cfeffc3'}]}
check_proof_bytes(network_leaf_proof)
contracts[0x1805d0970]=network_leaf_proof
additional_worker_proofs=[{'procedure': '0x1805e0340', 'entryRegisters': {'r8': 'mission-path:0'}, 'entryPointerKind': None, 'kind': 'exact-inline-frame-worker-capture', 'proof': 'Mission frame R13 is copied as first8bytes of a16byte inlinecapture; retained submission stores thiscontext and exactworkerfunction in std::function. Manager operation1 copiesall16bytes. Existing verified1f2de0/fd930/fdc30 dispatcher passes capture inR8; worker loadsMission atoffset0.', 'proofInstructions': [{'address': '0x1805c42b8', 'bytes': '4c896c2450488d4dc848894c2458488d0d13c1010048898d28010000488d0d65c0010048898d300100000f104424500f118518010000488d8d1801000048894c242848c7442420ffffffff41b910000000448bc033d2e8cdeac2ff'}, {'address': '0x1805e03e0', 'bytes': '458bc84183e90374244183f901741a4585c074124183e80174064183f80175060f10020f110133c0c3488bc1c3488d05acaf7300c3'}]}, {'procedure': '0x1805e0230', 'entryRegisters': {'r8': 'mission-path:0'}, 'entryPointerKind': None, 'kind': 'exact-inline-frame-worker-capture', 'proof': 'Mission frame R13 is copied as first8bytes of a16byte inlinecapture; retained submission stores thiscontext and exactworkerfunction in std::function. Manager operation1 copiesall16bytes. Existing verified1f2de0/fd930/fdc30 dispatcher passes capture inR8; worker loadsMission atoffset0.', 'proofInstructions': [{'address': '0x1805c45aa', 'bytes': '4c896c2450488d4dd048894c2458488d0d41bd010048898d48010000488d0d63bc010048898d500100000f104424500f118538010000488d8d3801000048894c242848c7442420ffffffff41b910000000448bc033d2e8dbe7c2ff'}, {'address': '0x1805e0300', 'bytes': '458bc84183e90374244183f901741a4585c074124183e80174064183f80175060f10020f110133c0c3488bc1c3488d054cb07300c3'}]}, {'procedure': '0x1805e04c0', 'entryRegisters': {'r8': 'mission-path:0'}, 'entryPointerKind': None, 'kind': 'exact-inline-frame-worker-capture', 'proof': 'Mission frame R13 is copied as first8bytes of a16byte inlinecapture; retained submission stores thiscontext and exactworkerfunction in std::function. Manager operation1 copiesall16bytes. Existing verified1f2de0/fd930/fdc30 dispatcher passes capture inR8; worker loadsMission atoffset0.', 'proofInstructions': [{'address': '0x1805c52dc', 'bytes': '4c896d80488d4c244048894d88488d0d90b3010048898da8000000488d0dc2b1010048898db00000000f1045800f118598000000488d8d9800000048894c242848c7442420ffffffff33d2448d4a10e8b0dac2ff'}, {'address': '0x1805e0680', 'bytes': '458bc84183e90374244183f901741a4585c074124183e80174064183f80175060f10020f110133c0c3488bc1c3488d058cac7300c3'}]}]
additional_worker_proofs.append({'procedure': '0x1805dffb0', 'entryRegisters': {'r8': 'mission-path:0'}, 'entryPointerKind': None, 'kind': 'exact-inline-Missile-frame-worker-capture', 'proof': 'Mission method5ce070 preserves RCX in RSI, copies RSI as first8bytes of16byte inlinecapture atRSP50, and submits5dffb0 to verified1f2de0. Manager5e0010 operation1 copies16bytes. Worker receives captureR8 and loadsMission at+0.', 'proofInstructions': [{'address': '0x1805ce070', 'bytes': 'f30f114c24105355565741544155415641574881ec9800000048c7442438feffffff0f29b424800000000f297c2470488bf14533f6e8566f0000488d44245048898424e00000004889742440488d8424e80000004889442448488d05401f01004889442460488d05d41e010048894424680f104424400f114424504c8b86a04704014c2b869847040149c1f803488d4c245048894c242848c7c5ffffffff48896c242033d2458d4e01e8c24cc2ff'}, {'address': '0x1805e0010', 'bytes': '458bc84183e90374244183f901741a4585c074124183e80174064183f80175060f10020f110133c0c3488bc1c3488d057cb27300c3'}]})
additional_worker_proofs.append({'procedure': '0x18068fc50', 'entryRegisters': {'r8': 'mission-path:0:16:0'}, 'entryPointerKind': None, 'kind': 'exact-heap-worker-capture-Mission-home-reference', 'proof': 'Missionframe5c4664 passesR8=R13Mission to68cf30. Entry68cf33 savesR8 atentryRSP18 (=RBP70); capturethirdfield is addressRBP70 (68d071/75). Allocation48bytes receives exactthree16bytecopies and manager68fd00 copiesall48bytes. Worker firstdereferencesR8 toheap, loadsheap+10 toRAX and[RAX] toR9 before68d800. Neitherpre-submissioncallee receiveshomeaddress; originalhomeisnotwrittenagain.', 'proofInstructions': [{'address': '0x1805c464c', 'bytes': '498dbd5087c500894c24304c8974242848895c24204c8bcf4d8bc5f30f104c2440498d8dc84e0401e8b7880c00'}, {'address': '0x18068cf30', 'bytes': '488bc44c894018f30f1148105556574154415541564157488d68a84881ec2001000048c745d8feffffff488958200f2970b80f2978a8440f294098440f294888498bd9488bf9448ba1880b000044896424304585e40f8e3a0800004c8b4108488b094863b590000000498bc0482bc148c1f802483bf07612488bd6488bcfe86dd49fff4c8b4708488b0f4c2bc14983e0fcbaffffffffe8ca61350066410f6ef40f5bf6f3440f100554e24900f3410f59f0450f57c9f30f2cce410f2ff1763781f9000000807421660f6ec10f5bc00f2ec674150f14f60f50c683e00183f00103c8660f6ef10f5bf6f30f103dd8de4900f30f58f7eb3581f9000000807421660f6ec10f5bc00f2ec674150f14f60f50c683e00183f00103c8660f6ef10f5bf6f30f103da1de4900f30f5cf7488d45984889456048897c2458488d45684889442460488d4570488944246848895c2470488b85800000004889442478488d858800000048894580488d05632c0000488945a8488d05a82b0000488945b0440fb60d58886700ba30000000448d42d8488b0de4587100e817c809000f104424580f11000f104c24680f1148100f104424780f11402048894598f3440f2cce488d4598488944242848c7442420ffffffff458bc433d2e8d85cb6ff'}, {'address': '0x18068fd00', 'bytes': '40574883ec3048c7442420feffffff48895c2440488bfa488bd9458bc84183e9030f84a50000004183f9010f848d0000004585c0746d4183e80174204183f801756f488b0248890148c7020000000033c0488b5c24404883c4305fc3440fb60da85b6700ba30000000448d42d8488b0d342c7100e8679b090090488b0f0f10010f11000f1049100f1148100f1041200f11402048890333c0488b5c24404883c4305fc3488b094885c97406e8c0e40b009033c0488b5c24404883c4305fc3488b01488b5c24404883c4305fc3488d056dc46800488b5c24404883c4305fc3'}, {'address': '0x18068fc50', 'bytes': '3bca0f8da000000048897424208954241041564883ec404d8b308bf148895c245048897c246066660f1f840000000000498b46288bd6498b0e448bc64d8b5e20498b5e184c8b10498b46100fb6b988140000c1ea064c895424384c8b088bc2c1e006488b94d1900b0000442bc04c895c2430418bc04c6bc06448895c24284903d040887c2420e825dbffffffc63b7424587c9d488b7c2460488b5c2450488b7424684883c440415ec3'}]})
additional_worker_proofs.extend([{'procedure': '0x1805dfdf0', 'entryRegisters': {'r8': 'mission-path:0:0'}, 'entryPointerKind': None, 'kind': 'exact-Mission-worker-capture', 'proof': 'Mission value is independently must-proven in RCX at the exact capture store. This stores the first 8 bytes of a 32-byte capture; the exact manager copies that capture unchanged. The retained 1f2de0/fd930/fdc30 dispatch contract passes the std::function context as R8. Capture path offsets are 0:0. Both capture and manager bytes are pinned; source-register provenance is asserted after CFG convergence.', 'captureOwner': {'procedure': '0x180708120', 'address': '0x180708277', 'register': 'rcx'}, 'proofInstructions': [{'address': '0x18070826f', 'bytes': '488d45d74889457f48894db7488d4567488945bf488d456f488945c748897dcf488d053a7cedff488945e7488d054f7bedff488945ef440fb60d5fd65f00ba20000000448d42e8488b0deba66900e81e1602000f1045b70f11000f104dc70f114810488945d7488d45d7488944242848c7442420ffffffff41b910000000448bc333d2e8e9aaaeff'}, {'address': '0x1805dfed0', 'bytes': '40574883ec3048c7442420feffffff48895c2440488bfa488bd9418bc883e9030f849c00000083f9010f84850000004585c074654183e80174204183f8017567488b0248890348c7020000000033c0488b5c24404883c4305fc3440fb60dda597200ba20000000448d42e8488b0d662a7c00e89999140090488b0f0f10010f11000f1049100f11481048890333c0488b5c24404883c4305fc3488b0b4885c97406e8fae216009033c0488b5c24404883c4305fc3488b03488b5c24404883c4305fc3488d05e7b27300488b5c24404883c4305fc3'}]}, {'procedure': '0x1805e0050', 'entryRegisters': {'r8': 'mission-path:0'}, 'entryPointerKind': None, 'kind': 'exact-Mission-worker-capture', 'proof': 'Mission value is independently must-proven in RBX at the exact capture store. This stores the first 8 bytes of a 8-byte capture; the exact manager copies that capture unchanged. The retained 1f2de0/fd930/fdc30 dispatch contract passes the std::function context as R8. Capture path offsets are 0. Both capture and manager bytes are pinned; source-register provenance is asserted after CFG convergence.', 'captureOwner': {'procedure': '0x1805cde20', 'address': '0x1805ce02c', 'register': 'rbx'}, 'proofInstructions': [{'address': '0x1805ce007', 'bytes': '488d44244848898424a8000000488d05e52001004889442458488d0529200100488944246048895c2448488d442448488944242848c7442420ffffffff41b901000000448bc533d2e88c4dc2ff'}, {'address': '0x1805e0100', 'bytes': '458bc84183e90374244183f901741a4585c074124183e80174064183f8017506488b0248890133c0c3488bc1c3488d05ccb07300c3'}]}, {'procedure': '0x1805e0140', 'entryRegisters': {'r8': 'mission-path:0'}, 'entryPointerKind': None, 'kind': 'exact-Mission-worker-capture', 'proof': 'Mission value is independently must-proven in RBX at the exact capture store. This stores the first 8 bytes of a 16-byte capture; the exact manager copies that capture unchanged. The retained 1f2de0/fd930/fdc30 dispatch contract passes the std::function context as R8. Capture path offsets are 0. Both capture and manager bytes are pinned; source-register provenance is asserted after CFG convergence.', 'captureOwner': {'procedure': '0x1805cde20', 'address': '0x1805cdfa5', 'register': 'rbx'}, 'proofInstructions': [{'address': '0x1805cdf98', 'bytes': '488d44244848898424a800000048895c2438488d8424980000004889442440488d05322201004889442458488d057621010048894424600f104424380f11442448488d442448488944242848c7442420ffffffff41b901000000448bc533d2e8e44dc2ff'}, {'address': '0x1805e01f0', 'bytes': '458bc84183e90374244183f901741a4585c074124183e80174064183f80175060f10020f110133c0c3488bc1c3488d051cb07300c3'}]}, {'procedure': '0x1805e0720', 'entryRegisters': {'r8': 'mission-path:0'}, 'entryPointerKind': None, 'kind': 'exact-Mission-worker-capture', 'proof': 'Mission value is independently must-proven in R13 at the exact capture store. This stores the first 8 bytes of a 8-byte capture; the exact manager copies that capture unchanged. The retained 1f2de0/fd930/fdc30 dispatch contract passes the std::function context as R8. Capture path offsets are 0. Both capture and manager bytes are pinned; source-register provenance is asserted after CFG convergence.', 'captureOwner': {'procedure': '0x1805c37d0', 'address': '0x1805c4d22', 'register': 'r13'}, 'proofInstructions': [{'address': '0x1805c4cfa', 'bytes': '488d85380200004889442460488d0553bb010048898548020000488d0505ba0100488985500200004c89ad38020000488d8538020000488944242848c7442420ffffffff41b902000000448bc633d2e892e0c2ff'}, {'address': '0x1805e0860', 'bytes': '458bc84183e90374244183f901741a4585c074124183e80174064183f8017506488b0248890133c0c3488bc1c3488d052ca87300c3'}]}, {'procedure': '0x1805e0bc0', 'entryRegisters': {'r8': 'mission-path:0'}, 'entryPointerKind': None, 'kind': 'exact-Mission-worker-capture', 'proof': 'Mission value is independently must-proven in R13 at the exact capture store. This stores the first 8 bytes of a 16-byte capture; the exact manager copies that capture unchanged. The retained 1f2de0/fd930/fdc30 dispatch contract passes the std::function context as R8. Capture path offsets are 0. Both capture and manager bytes are pinned; source-register provenance is asserted after CFG convergence.', 'captureOwner': {'procedure': '0x1805c37d0', 'address': '0x1805c4a28', 'register': 'r13'}, 'proofInstructions': [{'address': '0x1805c4a1c', 'bytes': '488d859801000048894424604c896c2450488d4424404889442458488d05f2c10100488985a8010000488d0574c10100488985b00100000f104424500f118598010000488d8598010000488944242848c7442420ffffffff41b901000000448bc633d2e85ce3c2ff'}, {'address': '0x1805e0c30', 'bytes': '458bc84183e90374244183f901741a4585c074124183e80174064183f80175060f10020f110133c0c3488bc1c3488d059ca47300c3'}]}, {'procedure': '0x1805e1030', 'entryRegisters': {'r8': 'mission-path:0:0'}, 'entryPointerKind': None, 'kind': 'exact-Mission-worker-capture', 'proof': 'Mission value is independently must-proven in R13 at the exact capture store. This stores the first 8 bytes of a 24-byte capture; the exact manager copies that capture unchanged. The retained 1f2de0/fd930/fdc30 dispatch contract passes the std::function context as R8. Capture path offsets are 0:0. Both capture and manager bytes are pinned; source-register provenance is asserted after CFG convergence.', 'captureOwner': {'procedure': '0x1805c37d0', 'address': '0x1805c3f6f', 'register': 'r13'}, 'proofInstructions': [{'address': '0x1805c3f64', 'bytes': '488d85f8000000488945004c896d30488d44244048894538488d44244848894540488d05b4d1010048898508010000488d0596d0010048898510010000440fb60d63197400ba18000000448d42f0488b0defe97d00e8225916000f1045300f1100f20f104d40f20f114810488985f8000000488d85f8000000488944242848c7442420ffffffff41b910000000458bc633d2e8e5edc2ff'}, {'address': '0x1805e1140', 'bytes': '40574883ec3048c7442420feffffff48895c2440488bfa488bd9418bc883e9030f849e00000083f9010f84870000004585c074674183e80174204183f8017569488b0248890348c7020000000033c0488b5c24404883c4305fc3440fb60d6a477200ba18000000448d42f0488b0df6177c00e82987140090488b0f0f10010f1100f20f104910f20f11481048890333c0488b5c24404883c4305fc3488b0b4885c97406e888d016009033c0488b5c24404883c4305fc3488b03488b5c24404883c4305fc3488d05359d7300488b5c24404883c4305fc3'}]}])
check_proof_bytes(additional_worker_proofs)
for entry in additional_worker_proofs:
 entry['evidenceIds']=list(dict.fromkeys(refs(*({'0x1805dffb0':[62,112,134]}.get(entry['procedure'],[62])))+([evs[0x18068cf30]] if entry['procedure']=='0x18068fc50' else [])+[evs[int(entry['procedure'],16)]]+worker_proofs['entries'][0].get('evidenceIds',[])))
 contracts[int(entry['procedure'],16)]=entry
for entry in worker_proofs['entries']:
 kind='mission-path:'+':'.join(map(str,entry['missionDereferenceOffsets']))
 contracts[int(entry['entryAddress'],16)]={'kind':'byte-verified-Mission-frame-worker-capture',
  'entryPointerKind':None,'entryRegisters':{entry['entryRegister']:kind},
  'proof':entry,'evidenceIds':[safety['evidence'][str(n)]['evidenceId'] for n in entry['evidenceRequests']]}
callback_path = p / 'native-callback-contracts.json'
callback_catalog = json.loads(callback_path.read_text())
if callback_catalog['targetSha256'] != d['targetSha256']:
    raise ValueError('Callback contract target mismatch')
for entry in callback_catalog['contracts']:
    address = int(entry['entryAddress'],16)
    contracts[address] = {'kind':'retained-registered-receiver',
        'entryPointerKind':entry['pointerKind'],
        'entryRegisters':{entry['entryRegister']:entry['pointerKind']},
        'callbackBindings':entry['callbackBindings'], 'proof':entry['proof'],
        'evidenceIds':entry['evidenceIds']}
agent_source_contracts = {
 0x180603830: {'kind':'registered-Agent-callback', 'proof':'Registered IMBAgent.WeaponEquipped callback252 at6f4560:6f4583 RBX=RCX,6f4644 RCX=RBX beforeCALL603830.', 'evidenceIds':['ev_9e9945bb81c2694f4088be078e0583675691e0d2cfd346c408cc24fb86f5c2b8']},
 0x1805ecb90: {'kind':'registered-Agent-callback', 'proof':'Registered IMBAgent.TryToSheathWeaponInHand callback248 at6f4800:6f4809 RBX=RCX,6f4897 RCX=RBX beforeCALL5ecb90.', 'evidenceIds':['ev_9e9945bb81c2694f4088be078e0583675691e0d2cfd346c408cc24fb86f5c2b8']},
 0x1805f4770: {'kind':'Mission-frame-Agent-list', 'proof':'5d51f0 fills Mission103b920/103f908 lists with Mission+2d50+index*c60 Agent pointers. Frame5c37d0 passes those list entries to5f3c50;5f3c7e RDI=RCX,5f3d3a RCX=RDI beforeCALL5f4770.', 'evidenceIds':refs(62,135)},
 0x18060dee0: {'kind':'Mission-frame-Agent-list', 'proof':'5d51f0 fills103f908 with Agent pointers; frame5c37d0 captures Mission forworker5e09e0. Worker loads Agent from this list and CALLs5fea70 at5e0a60;5fea70 forwards unchangedparam1 to60dee0.', 'evidenceIds':refs(62,135,176)},
}
for source,s in texts.items():
 if source not in agent_source_contracts or re.search(r'\bparam_1\s*=(?!=)',s):continue
 for target,arg,pos in calls(s):
  if target!=source and re.fullmatch(r'\*\((?:undefined8|longlong)\*\)\(param_1\+0xab8\)',arg):contracts.setdefault(target,{'kind':'direct-Agent-owner-argument','entryPointerKind':'mission','sourceProcedure':hex(source),'firstArgument':arg,'sourceOwnerContract':agent_source_contracts[source],'evidenceIds':list(dict.fromkeys([evs[source],d['findings'][0]['evidenceIds'][0]]+agent_source_contracts[source]['evidenceIds']))})
for address, contract in agent_source_contracts.items():
    contracts.setdefault(address, dict(contract, entryPointerKind='agent'))
contracts[0x1805f3c50] = dict(agent_source_contracts[0x1805f4770], entryPointerKind='agent',
 proof='5d51f0 populates Agent lists from native Agent pool slots; retained Mission frame5c37d0 calls5f3c50 with an entry of those lists. Same external owner chain independently reviewed for5f4770.')
changed=True
while changed:
 changed=False
 for source,s in texts.items():
  if source not in contracts or re.search(r'\bparam_1\s*=(?!=)',s):continue
  source_kind = receiver_kind_for_argument(contracts[source])
  if source_kind is None:continue
  for target,arg,pos in calls(s):
   if target==source or target in contracts:continue
   kind = source_kind if arg=='param_1' else None
   if source_kind=='agent' and re.fullmatch(r'\*\((?:undefined8|longlong)\*\)\(param_1\+0xab8\)',arg):kind='mission'
   if kind:contracts[target]={'kind':'direct-established-owner-argument','entryPointerKind':kind,'sourceProcedure':hex(source),'firstArgument':arg,'evidenceIds':list(dict.fromkeys(contracts[source]['evidenceIds']+[evs[source]]))};changed=True
# Complete validated chained-unwind groups are needed for callback callers which
# contain no large-offset candidate of their own.
import struct
from bisect import bisect_right
from collections import defaultdict
entries={e.struct.BeginAddress:e.struct for e in pe.DIRECTORY_ENTRY_EXCEPTION}
parents={}
for start,entry in entries.items():
 h=pe.get_data(entry.UnwindData,4)
 if len(h)==4 and (h[0]>>3)&4:
  tail=entry.UnwindData+4+((h[2]+1)&~1)*2
  ps,pend,pu=struct.unpack('<III',pe.get_data(tail,12))
  if ps in entries and entries[ps].EndAddress==pend and entries[ps].UnwindData==pu:parents[start]=ps
for start,entry in entries.items():
 r=start;seen=set()
 while r in parents:
  if r in seen:raise ValueError('Unwind chain cycle')
  seen.add(r);r=parents[r]
 root=base+r
 if root not in groups:groups[root]={'root':hex(root),'runtimeRanges':[]}
 rr={'start':hex(base+start),'endExclusive':hex(base+entry.EndAddress)}
 if rr not in groups[root]['runtimeRanges']:groups[root]['runtimeRanges'].append(rr)

reviewed_leaf_ranges=[]
pdata_starts=sorted(base+e.BeginAddress for e in entries.values())
pdata_ends={base+e.BeginAddress:base+e.EndAddress for e in entries.values()}
def overlaps_pdata(address):
 i=bisect_right(pdata_starts,address)-1
 return i>=0 and address<pdata_ends[pdata_starts[i]]
def add_leaf_group(start):
 """Decode a known entry to all bounded forward branches and a terminal return.

 No guessed backward boundary, overlap with .pdata, or padding is accepted.
 """
 if start in groups:return True
 if overlaps_pdata(start):return False
 pending_targets=set();boundaries=set();end=None;raw_ins=[]
 for ins in md.disasm(pe.get_data(start-base,1024),start):
  if overlaps_pdata(ins.address):return False
  if ins.mnemonic in ['int3','ud2','hlt'] or ins.address>=start+1024:return False
  boundaries.add(ins.address);pending_targets.discard(ins.address);raw_ins.append(flow.basic(ins))
  terminal=ins.group(CS_GRP_RET)
  if ins.group(CS_GRP_JUMP):
   if ins.operands[0].type!=X86_OP_IMM:return False
   target=ins.operands[0].imm
   if start<=target<start+1024:
    if target<ins.address and target not in boundaries:return False
    if target>ins.address:pending_targets.add(target)
   elif ins.mnemonic!='jmp':return False
   terminal=ins.mnemonic=='jmp' and not pending_targets
  if terminal and not pending_targets:end=ins.address+ins.size;break
 if end is None:return False
 groups[start]={'root':hex(start),'runtimeRanges':[{'start':hex(start),'endExclusive':hex(end)}]}
 reviewed_leaf_ranges.append({'entry':hex(start),'endExclusive':hex(end),'instructions':raw_ins,
  'basis':'Known callback or direct-call entry. Complete linear instruction boundaries through every internal branch; no padding or .pdata overlap.'})
 return True
for entry in list(contracts):
 if eligible_procedure(entry):add_leaf_group(entry)

def reg(ins,number):
 name=ins.reg_name(number) or ''
 return flow.ALIASES.get(name,name if name.startswith('xmm') else None)
def val(kind,bias=0,defs=()):return (kind,bias,frozenset(defs))
def extend(v,ins):return (v[0],v[1],v[2]|frozenset([ins.address]))
AGENT_VECTOR_BASES = (0x1037938,0x103b920,0x103f908)
def plus(a,b,ins=None):
 if a is None or b is None:return None
 defs=a[2]|b[2]|(frozenset([ins.address]) if ins else frozenset())
 if a[0]=='constant':a,b=b,a
 if b[0]=='constant':
  bias=a[1]+b[1]
  if a[0]=='mission-index' and bias==0x2d50:return ('agent',0,defs)
  if a[0]=='missile' and bias%0xc00==0:return ('missile',0,defs)
  return (a[0],bias,defs)
 if a[0]=='missile' and b[0]=='missile-index':return ('missile',a[1]+b[1],defs)
 if b[0]=='missile' and a[0]=='missile-index':return plus(b,a,ins)
 if a[0]=='mission' and b[0]=='agent-index':return ('agent' if a[1]+b[1]==0x2d50 else 'mission-index',0 if a[1]+b[1]==0x2d50 else a[1]+b[1],defs)
 if b[0]=='mission' and a[0]=='agent-index':return plus(b,a,ins)
 return None

def address(ins,mem,st):
 terms=[]
 if mem.base:
  if mem.base==X86_REG_RIP:terms.append(val('constant',ins.address+ins.size))
  else:terms.append(st.get(reg(ins,mem.base)))
 if mem.index:
  v=st.get(reg(ins,mem.index))
  if v and mem.scale!=1:
   v=val('constant',v[1]*mem.scale,v[2]) if v[0]=='constant' else None
  terms.append(v)
 out=val('constant',mem.disp)
 for term in terms:out=plus(out,term)
 if out and out[0]=='mission-index':out=('agent',out[1]-0x2d50,out[2])
 return out

def transfer(ins,st):
 out=dict(st);op=ins.operands
 try:_,writes=ins.regs_access()
 except capstone.CsError:return {}
 for r in writes:out.pop(reg(ins,r),None)
 def slot(addr):return ('slot',addr)
 def kill_slots(lo=None,size=None):
  for k in list(out):
   if isinstance(k,tuple) and k[0]=='slot' and (lo is None or (k[1]<lo+size and lo<k[1]+8)):out.pop(k,None)
 # Any explicit stack write invalidates overlapping saved pointer state first.
 for operand_index,o in enumerate(op):
  # Capstone 5 marks some SIMD stores (notably MOVUPS) as memory READ.
  # Intel destination order independently establishes all explicit MOV stores.
  if memory_operand_writes(ins,operand_index):
   ea=address(ins,o.mem,st)
   if ea and ea[0]=='stack':kill_slots(ea[1],o.size)
   elif any(st.get(reg(ins,r),('',))[0]=='stack' for r in (o.mem.base,o.mem.index) if r):kill_slots()
 if ins.mnemonic=='call':
  # Model the state after the callee returns: CALL/RET balance the return slot.
  # This does not restore any escaped spill or volatile register.
  if 'rsp' in st:out['rsp']=st['rsp']
  if op and op[0].type==X86_OP_IMM and op[0].imm==0x1809e0760:
   return dict(st)
  # Escaped local-stack pointers can allow a callee to overwrite local spills.
  # No alias guessing: discard all spills on such a call.
  if any(st.get(r,('',))[0]=='stack' for r in ['rcx','rdx','r8','r9']) or any(isinstance(k,tuple) and v[0]=='stack' for k,v in st.items()):kill_slots()
  rsp=st.get('rsp')
  if rsp and rsp[0]=='stack':kill_slots(rsp[1],32) # Win64 callee shadow area.
  for r in flow.VOLATILE:out.pop(r,None)
  for r in ['xmm0','xmm1','xmm2','xmm3','xmm4','xmm5']:out.pop(r,None)
  if op and op[0].type==X86_OP_IMM:
   for r in leaf_preserved_registers(op[0].imm):
    if r in st:out[r]=st[r]
  if op and op[0].type==X86_OP_IMM and op[0].imm==0x180698570:
   for r in ['rcx','r8','r9','r10','r11','xmm0','xmm1','xmm2','xmm3','xmm4','xmm5']:
    if r in st:out[r]=st[r]
  return out
 if ins.mnemonic=='push':
  rsp=st.get('rsp')
  if rsp and rsp[0]=='stack':
   out['rsp']=val('stack',rsp[1]-8,rsp[2]|{ins.address})
   if op and op[0].type==X86_OP_REG and reg(ins,op[0].reg) in st:out[slot(rsp[1]-8)]=extend(st[reg(ins,op[0].reg)],ins)
  return out
 if ins.mnemonic=='pop':
  rsp=st.get('rsp')
  if rsp and rsp[0]=='stack':
   v=st.get(slot(rsp[1]));out['rsp']=val('stack',rsp[1]+8,rsp[2]|{ins.address})
   if v and op and op[0].type==X86_OP_REG:out[reg(ins,op[0].reg)]=extend(v,ins)
   out.pop(slot(rsp[1]),None)
  return out
 if len(op)==2 and ins.mnemonic in ['mov','movabs','movq','movsd']:
  v=None
  if op[1].type==X86_OP_REG and op[1].size in [8,16]:v=st.get(reg(ins,op[1].reg))
  elif op[1].type==X86_OP_IMM:v=val('constant',op[1].imm)
  elif op[1].type==X86_OP_MEM and op[1].size==8:
   ea=address(ins,op[1].mem,st)
   mem=op[1].mem
   bv=st.get(reg(ins,mem.base))
   iv=st.get(reg(ins,mem.index))
   if (bv is None or bv[0] not in ['agent-vector','agent-array']) and iv and iv[0] in ['agent-vector','agent-array'] and mem.scale==1:bv=iv
   if bv and bv[0] in ['agent-vector','agent-array']:
    offset=bv[1]+mem.disp
    if mem.index and offset==0:v=val('agent',0,bv[2])
    elif not mem.index and bv[0]=='agent-array' and offset%8==0:v=val('agent',0,bv[2])
    elif not mem.index and bv[0]=='agent-vector':
     if offset==0x3fc0:v=val('agent-array',0,bv[2])
     elif 0<=offset<0x3fc0 and offset%8==0:v=val('agent',0,bv[2])
   if bv and bv[0]=='missile-array' and (bv[1]+mem.disp)%8==0 and (not mem.index or mem.scale==8):v=val('missile',0,bv[2])
   if bv and bv[0]=='missile-page-table' and bv[1]+mem.disp==0 and (not mem.index or mem.scale==8):v=val('missile',0,bv[2])
   if ea:
    if ea[0]=='stack':v=st.get(slot(ea[1]))
    elif ea[0].startswith(('mission-path:','agent-path:','missile-path:')):
     path=[int(x) for x in ea[0].split(':')[1:]]
     if ea[1]==path[0]:v=val(ea[0].split(':')[0][:-5] if len(path)==1 else ea[0].split(':')[0]+':'+':'.join(map(str,path[1:])),0,ea[2])
    elif ea[0]=='agent' and ea[1] in [0xab8,0x148]:v=val('mission',0,ea[2])
    elif ea[0]=='agent' and ea[1]==0x210:v=val('agent',0,ea[2])
    elif ea[0]=='agent' and ea[1]==0x888:v=val('agent-ai',0,ea[2])
    elif ea[0]=='agent-ai' and ea[1]==0:v=val('agent',0,ea[2])
    elif ea[0]=='agent-ai' and ea[1] in [8,0x1a8,0x1380]:v=val('agent-ai',0,ea[2])
    elif ea[0]=='agent-ai' and ea[1]==0x14d8:v=val('mission',0,ea[2])
    elif ea[0]=='missile' and ea[1]==0x10:v=val('mission',0,ea[2])
    elif ea[0]=='mission' and ea[1]==0x1044798:v=val('missile-array',0,ea[2])
    elif ea[0]=='mission' and ea[1]==0x1044728:v=val('missile',0,ea[2])
    elif ea[0]=='mission' and ea[1]==0x1044730:v=val('missile-end',0,ea[2])
    elif ea[0]=='mission' and ea[1]==0x1044770:v=val('missile-page-table',0,ea[2])
    elif ea[0]=='mission' and any(ea[1]==b+0x3fc0 for b in AGENT_VECTOR_BASES):v=val('agent-array',0,ea[2])
    elif ea[0]=='constant' and ea[1]==0x180da9d60:v=val('mission',0,ea[2])
  if v and op[0].type==X86_OP_REG:
   if op[0].size==8 or ins.mnemonic in ['movq','movsd']:out[reg(ins,op[0].reg)]=extend(v,ins)
   elif op[0].size==4 and v[0]=='constant':out[reg(ins,op[0].reg)]=val('constant',v[1]&0xffffffff,v[2]|{ins.address})
  elif v and op[0].type==X86_OP_MEM and op[0].size==8:
   ea=address(ins,op[0].mem,st)
   if ea and ea[0]=='stack':out[slot(ea[1])]=extend(v,ins)
 if len(op)==2 and op[0].type==X86_OP_REG:
  dest=reg(ins,op[0].reg)
  if ins.mnemonic=='lea' and op[0].size==8 and op[1].type==X86_OP_MEM:
   v=address(ins,op[1].mem,st)
   if v and v[0]=='mission' and v[1] in AGENT_VECTOR_BASES:v=val('agent-vector',0,v[2])
   mem=op[1].mem;index=st.get(reg(ins,mem.base))
   if mem.base==mem.index and mem.scale==2 and mem.disp==0 and index and index[:2]==('bounded-index',15):v=val('bounded-times-three',45,index[2])
   if v:out[dest]=extend(v,ins)
  elif ins.mnemonic in ['add','sub'] and op[0].size==8:
   right=val('constant',op[1].imm) if op[1].type==X86_OP_IMM else st.get(reg(ins,op[1].reg)) if op[1].type==X86_OP_REG else None
   if op[1].type==X86_OP_MEM and op[1].size==8:
    mem=op[1].mem;bv=st.get(reg(ins,mem.base))
    if bv and bv[0]=='missile-page-table' and bv[1]+mem.disp==0 and (not mem.index or mem.scale==8):right=val('missile',0,bv[2])
   if right and ins.mnemonic=='sub':right=val('constant',-right[1],right[2]) if right[0]=='constant' else None
   v=plus(st.get(dest),right,ins)
   if v and v[0]=='missile-end' and v[1]==-0xc00:v=val('missile',0,v[2])
   if v and v[0]=='mission' and v[1] in AGENT_VECTOR_BASES:v=val('agent-vector',0,v[2])
   if v:out[dest]=v
  elif ins.mnemonic=='and' and op[0].size in (4,8) and op[1].type==X86_OP_IMM and op[1].imm==15:out[dest]=val('bounded-index',15,[ins.address])
  elif ins.mnemonic=='shl' and op[0].size==8 and op[1].type==X86_OP_IMM and op[1].imm==10 and st.get(dest,())[:2]==('bounded-times-three',45):out[dest]=val('missile-index',0,st[dest][2]|{ins.address})
  elif ins.mnemonic=='xor' and op[1].type==X86_OP_REG and op[0].reg==op[1].reg:out[dest]=val('constant',0,[ins.address])
 if ins.mnemonic=='imul' and len(op)==3 and op[2].type==X86_OP_IMM and op[2].imm==0xc60 and op[0].size==8:
  # Indexed native Agent storage stride, not a Mission pointer by itself.
  out[reg(ins,op[0].reg)]=val('agent-index',0,[ins.address])
 if ins.mnemonic=='imul' and len(op)==3 and op[2].type==X86_OP_IMM and op[2].imm==0xc00 and op[0].size==8:
  out[reg(ins,op[0].reg)]=val('missile-index',0,[ins.address])
 return out

def join(a,b):
 if a is None:return dict(b)
 out={r:(a[r][0],a[r][1],a[r][2]|b[r][2]) for r in a if r in b and a[r][:2]==b[r][:2]}
 for r in a.keys()&b.keys():
  if {a[r][0],b[r][0]}=={'agent-vector','agent-array'} and a[r][1]==b[r][1]==0:out[r]=val('agent-array',0,a[r][2]|b[r][2])
 return out
cache={}
reviewed_switches={0x1805d056b:{'root':0x1805d00a0,'tableStart':0x1805d0930,
 'tableHex':'8b055d0099055d006d055d007b055d007b055d007b055d0085055d007b055d007b055d008b055d007b055d007b055d007b055d007b055d007b055d00dc055d00',
 'guardStart':0x1805d0555,'dispatchHex':'83f90f0f87860000004863c18b8c8530095d004803cdffe1',
 'guardProof':'Unsigned CMP ECX,15 / JA default dominates MOVSXD RAX,ECX and four-byte image-relative table load. Exactly16 entries.',
 'evidenceIds':['ev_58f96cf516b351d35e47b3134b73170eafa2c666ea77870701888e7c97117382','ev_727f34ac30e34bff9fdddeb3a631941abd547c63f90c35962f9d78c9b432f10b']}}

# Additional exact native switch tables; guards and data validated before decode.
reviewed_switches[0x180689aba]={'root': 6449306016, 'tableStart': 6449314576, 'tableHex': '2f9c68002f9c6800bc9a6800bc9a6800bc9a6800b1a568003ea6680094a768002c9d68002c9d68002c9d68002c9d680087b868008da86800beab68000cb36800c1a2680041a46800c3b36800', 'guardStart': 6449306247, 'dispatchHex': '4183fb120f87522000000f29b42410050000488d0d606597ff0f29bc2400050000488d05516597ff428b8c9910bb68004803c8ffe1', 'imageBaseRegister': 'rax', 'guardProof': 'Unsigned comparison against 18 with JA default precedes image-relative four-byte table dispatch; 19 retained entries.', 'evidenceIds': ['ev_b738169d6c298d7090a4f890ae46e67aec9b10a39a8701c0e8aa0106eb599d28']}
reviewed_switches[0x1805d3b0f]={'root': 6448560784, 'tableStart': 6448561076, 'tableHex': '2d3b5d00263b5d001f3b5d00183b5d00113b5d00e23a5d00', 'guardStart': 6448560885, 'dispatchHex': '83f90577334863c14c8d05fcc4a2ff418b8c80b43b5d004903c8ffe1', 'imageBaseRegister': 'r8', 'guardProof': 'Unsigned comparison against 5 with JA default precedes image-relative four-byte table dispatch; 6 retained entries.', 'evidenceIds': ['ev_8d13f0c22930705f7fa391b631f2ac4c569fe7a5abc8c5a7039696d8298860ec']}
reviewed_switches[0x18064948f]={'root': 6449041904, 'tableStart': 6449042736, 'tableHex': 'b3946400c194640091946400a9946400a9946400a99464009f946400a9946400a9946400b3946400a9946400a9946400a9946400a9946400a994640004956400', 'guardStart': 6449042553, 'dispatchHex': '83f80f0f878a0000004898418b8c83309564004903cbffe1', 'imageBaseRegister': 'r11', 'guardProof': 'Unsigned comparison against 15 with JA default precedes image-relative four-byte table dispatch; 16 retained entries.', 'evidenceIds': ['ev_c6d972d513715510a5ffa48dcabe785690ee4f01e31ee90b816ae169148413a6']}
reviewed_switches[0x1806d71e2]={'root': 6449623040, 'tableStart': 6449631072, 'tableHex': 'e4716d0005726d0021726d003d726d0059726d0077726d00', 'guardStart': 6449623502, 'dispatchHex': '83f8050f87cd000000418b8c84608f6d004903ccffe1', 'imageBaseRegister': 'r12', 'guardProof': 'Unsigned comparison against 5 with JA default precedes image-relative four-byte table dispatch; 6 retained entries.', 'evidenceIds': ['ev_9bbac0f45e0cdb04ed7dbd5a9a3eea2467751bd515f8ae8f6b7130179bb1ba34']}
reviewed_switches[0x1806d730c]={'root': 6449623040, 'tableStart': 6449631096, 'tableHex': '0e736d002f736d004d736d006b736d0089736d00a7736d00', 'guardStart': 6449623800, 'dispatchHex': '83f8050f87d3000000418b8c84788f6d004903ccffe1', 'imageBaseRegister': 'r12', 'guardProof': 'Unsigned comparison against 5 with JA default precedes image-relative four-byte table dispatch; 6 retained entries.', 'evidenceIds': ['ev_9bbac0f45e0cdb04ed7dbd5a9a3eea2467751bd515f8ae8f6b7130179bb1ba34']}
reviewed_switches[0x1806d7529]={'root': 6449623040, 'tableStart': 6449631120, 'tableHex': '2b756d004a756d0066756d0082756d00a0756d00bc756d00', 'guardStart': 6449624335, 'dispatchHex': '83f8050f87d1000000488d15e18a92ff8b8c82908f6d004803caffe1', 'imageBaseRegister': 'rdx', 'guardProof': 'Unsigned comparison against 5 with JA default precedes image-relative four-byte table dispatch; 6 retained entries.', 'evidenceIds': ['ev_9bbac0f45e0cdb04ed7dbd5a9a3eea2467751bd515f8ae8f6b7130179bb1ba34']}
reviewed_switches[0x1806d7677]={'root': 6449623040, 'tableStart': 6449631144, 'tableHex': '79766d009a766d00b6766d00d4766d00f2766d000e776d00', 'guardStart': 6449624675, 'dispatchHex': '83f8050f87cd000000418b8c85a88f6d004903cdffe1', 'imageBaseRegister': 'r13', 'guardProof': 'Unsigned comparison against 5 with JA default precedes image-relative four-byte table dispatch; 6 retained entries.', 'evidenceIds': ['ev_9bbac0f45e0cdb04ed7dbd5a9a3eea2467751bd515f8ae8f6b7130179bb1ba34']}
reviewed_switches[0x1806d7837]={'root': 6449623040, 'tableStart': 6449631168, 'tableHex': '39786d005a786d0078786d0096786d00b4786d00d2786d00', 'guardStart': 6449625117, 'dispatchHex': '83f8050f87d9000000488d15d38792ff8b8c82c08f6d004803caffe1', 'imageBaseRegister': 'rdx', 'guardProof': 'Unsigned comparison against 5 with JA default precedes image-relative four-byte table dispatch; 6 retained entries.', 'evidenceIds': ['ev_9bbac0f45e0cdb04ed7dbd5a9a3eea2467751bd515f8ae8f6b7130179bb1ba34']}

reviewed_switches[0x180697b95]={'root': 6449363712, 'tableStart': 6449364096, 'tableHex': '977b6900e07b6900c37b6900aa7b6900ec7b6900a07b6900b97b6900d67b6900f17b6900cf7b6900', 'guardStart': 6449363836, 'dispatchHex': '83fa0977704863c2488d15758496ff8b8c82807c69004803caffe1', 'imageBaseRegister': 'rdx', 'guardProof': 'Unsigned comparison against 9 with JA default precedes image-relative four-byte table dispatch; 10 retained entries.', 'evidenceIds': ['ev_ddee716a92da70c0bb63bec943c1128995705760d627e85afdda53ddd44825b7']}
reviewed_switches[0x1806a8ed7]={'root': 6449433952, 'tableStart': 6449435380, 'tableHex': '6c8f6a002d916a0093916a0055916a0093916a00d98e6a00d98e6a0028926a00d98e6a00bf916a00d3916a00', 'guardStart': 6449434308, 'dispatchHex': '83f80a0f875b0300008b8c82f4926a004803caffe1', 'imageBaseRegister': 'rdx', 'guardProof': 'Unsigned comparison against 10 with JA default precedes image-relative four-byte table dispatch; 11 retained entries.', 'evidenceIds': ['ev_78b17e0194213117ad4cbcb2fad452f70f7ae35e4a1f7136b5704ceb09e10f93']}
reviewed_switches[0x1806adfc1]={'root': 6449454752, 'tableStart': 6449456180, 'tableHex': 'c3df6a00d4e06a00f2e06a0020e16a000be16a00cee16a00cee16a00dee16a00e1df6a00c0e16a0035e16a00', 'guardStart': 6449455021, 'dispatchHex': '83f80a0f8728020000418b8c8434e46a004903ccffe1', 'imageBaseRegister': 'r12', 'guardProof': 'Unsigned comparison against 10 with JA default precedes image-relative four-byte table dispatch; 11 retained entries.', 'evidenceIds': ['ev_8282e7ab20bf38571ee80abd24498cea25a3070fcb1cb087fc1fab28dfc33904']}

reviewed_external_tails={0x18060ed9e:{'root':0x18060ecb0,'proof':'Agent+ab8 owner providesMission object at1184388; loadvtable, restoreRBX/RSP/RDI, tail-dispatchvtable+18. This is an ABI epilogue tail call, not an in-function switch.','proofInstructions':[{'address':'0x18060ed80','bytes':'4c8b83b80a0000498b8888431801488bd3488b01488b5c24304883c4205f48ff6018'}]}}
check_proof_bytes(list(reviewed_external_tails.values()))
def decode(root):
 if root in cache:return cache[root]
 g=groups[root];insns={};bad=[]
 for rr in g['runtimeRanges']:
  start=int(rr['start'],16);end=int(rr['endExclusive'],16);cursor=start
  for switch in reviewed_switches.values():
   if switch['root']==root:
    table=switch['tableStart'];raw=bytes.fromhex(switch['tableHex'])
    if pe.get_data(table-base,len(raw))!=raw:raise ValueError('Reviewed switch table bytes changed')
    dispatch=bytes.fromhex(switch['dispatchHex'])
    if pe.get_data(switch['guardStart']-base,len(dispatch))!=dispatch:raise ValueError('Reviewed switch dispatch bytes changed')
    if start<table<end:end=table
    elif start==table and end==table+len(raw):end=start # Exact .pdata range is entirely reviewed switch data.
  for ins in md.disasm(pe.get_data(start-base,end-start),start):insns[ins.address]=ins;cursor=ins.address+ins.size
  if cursor!=end:bad.append(hex(cursor))
 if bad:cache[root]=(insns,None,bad,[]);return cache[root]
 succ={};indirect=[]
 for addr,ins in insns.items():
  nxt=addr+ins.size;ns=[]
  if ins.group(CS_GRP_RET) or ins.mnemonic in ['int3','ud2','hlt','int','iretq']:pass
  elif ins.group(CS_GRP_JUMP):
   if ins.operands[0].type==X86_OP_IMM:
    if ins.operands[0].imm in insns:ns.append(ins.operands[0].imm)
    if ins.mnemonic!='jmp' and nxt in insns:ns.append(nxt)
   elif addr in reviewed_external_tails:
    if reviewed_external_tails[addr]['root']!=root:raise ValueError('Tail dispatch root mismatch')
   elif addr in reviewed_switches:
    switch=reviewed_switches[addr];raw=bytes.fromhex(switch['tableHex'])
    ns=sorted({base+int.from_bytes(raw[i:i+4],'little') for i in range(0,len(raw),4)})
    if any(target not in insns for target in ns):raise ValueError('Switch target is not a decoded instruction boundary')
   else:ns=list(insns);indirect.append(hex(addr))
  elif nxt in insns:ns.append(nxt)
  succ[addr]=ns
 cache[root]=(insns,succ,bad,indirect);return cache[root]

def analyze(root,contract):
 insns,succ,bad,indirect=decode(root)
 if bad:return None,{'procedure':hex(root),'status':'skipped-decode-gap','addresses':bad}
 initial={'rsp':val('stack')}
 for rr,kind in contract.get('entryRegisters',{'rcx':contract['entryPointerKind']}).items():initial[rr]=val(kind,contract.get('entryBiases',{}).get(rr,0))
 states={root:initial};work=deque([root]);queued={root};steps=0
 while work and steps<max(10000,len(insns)*100):
  addr=work.popleft();queued.remove(addr);steps+=1;out=transfer(insns[addr],states[addr])
  if (root,addr) in exact_reload_seeds:
   seed=exact_reload_seeds[(root,addr)];out[seed['register']]=val(seed['pointerKind'],seed['biasBytes'],{addr})
  for nxt in succ[addr]:
   ns=join(states.get(nxt),out)
   if nxt not in states or ns!=states[nxt]:
    states[nxt]=ns
    if nxt not in queued:work.append(nxt);queued.add(nxt)
 if work:return None,{'procedure':hex(root),'status':'skipped-no-convergence'}
 for address_,switch in reviewed_switches.items():
  if switch['root']==root:
   rbp=states.get(address_,{}).get(switch.get('imageBaseRegister','rbp'))
   if rbp is None or rbp[:2]!=('constant',base):raise ValueError(f'Switch image-base provenance was not established at {hex(address_)}: {rbp}')
   # No branch may bypass the tested dispatch guard into its interior.
   for branch in insns.values():
    if branch.group(CS_GRP_JUMP) and branch.operands[0].type==X86_OP_IMM:
     if switch['guardStart']<branch.operands[0].imm<=address_:raise ValueError('Switch guard can be bypassed')
 return states,{'procedure':hex(root),'status':'converged','entryContract':contract,'decodedInstructions':len(insns),'reachableInstructions':len(states),'worklistSteps':steps,'indirectJumpsOverapproximatedToAllDecodedInstructions':indirect}

leaf_register_effects={}
def leaf_preserved_registers(target):
 """Only complete call-free bodies qualify; memory effects remain conservative."""
 if target in leaf_register_effects:return set(leaf_register_effects[target]['preservedRegisters'])
 leaf_register_effects[target]={'preservedRegisters':[]}
 if not eligible_procedure(target) and target!=0x18059ea00:return set() # Independently reviewed complete call-free helper721.
 if target not in groups and not add_leaf_group(target):return set()
 insns,succ,bad,indirect=decode(target)
 if bad or indirect or len(insns)>1000:return set()
 writes=set()
 for ins in insns.values():
  if ins.mnemonic in ['call','syscall','sysenter','int','int3','ud2']:return set()
  if ins.mnemonic.startswith(('rep','stos','movsb','movsw','movsq')):return set()
  if ins.group(CS_GRP_JUMP) and (ins.operands[0].type!=X86_OP_IMM or ins.operands[0].imm not in insns):return set()
  try:_,regs=ins.regs_access()
  except capstone.CsError:return set()
  writes.update(reg(ins,r) for r in regs)
 preserved=(set(flow.VOLATILE)|{'xmm0','xmm1','xmm2','xmm3','xmm4','xmm5'})-writes
 leaf_register_effects[target]={'preservedRegisters':sorted(preserved),'instructions':[flow.basic(x) for _,x in sorted(insns.items())],
  'proof':'Every decoded path is call-free; retained registers are absent from complete instruction register-write sets. Memory writes do not imply a register change. This summary restores only register values after normal call stack-spill invalidation; it never restores a memory/spill fact.'}
 return preserved

# Actual ABI call-register values provide contracts where the decompiler recovers
# a wrong argument list. Propagate only zero-bias Mission/Agent pointers.
for root in groups:
 if 0x1805b0000<=root<0x1806f0000 and root in evs and root not in contracts:
  contracts[root]={'kind':'global-only-analysis-root','entryPointerKind':None,'entryRegisters':{},
   'evidenceIds':[evs[root]],'proof':'No receiver identity assumed; only local established globals and derived owners may contribute.'}
# This private-home proof covers the whole loop, including code after the reload.
# It is deliberately an exact-function exception, not a relaxed call/spill rule.
job_loop_callee_effects={
 0x619de0:'RCX output writes only offsets0..f5; caller RBP+60. No calls.',
 0x5d4e20:'Collision record RDX at RBP+60 is typed AttackCollisionData; managed table210 has bounded byref outputs Single4 at RBP-7c, Reaction4 at RBP-80, HitParticleResultData12 at RBP-58. Other outputs are callee locals. None reaches home RBP-60..-59.',
 0x61a0c0:'Writes exactly16bytes through RDX (caller RBP+600/+610); copies input matrix to callee locals before5e4480, no original pointer forwarded.',
 0x5e4480:'Matrix operation receives only61a0c0 callee-local inputs and outputs.',
 0x2ca050:'Call-free: RDX output16bytes; RCX/R8 inputs read only. Caller outputs RBP+570,+20,+550.',
 0x689510:'Caller record at RBP+160/+190/+1c0 is read only; only original-address forwarding is record+8 to5f57a0. Network path copies values into a heap record, not its address.',
 0x5f57a0:'Param2 vector is read only and never forwarded; param3 forwarding uses689510 own local copy. Other operations mutate Agent/physics heap objects.',
 0x690c30:'Param5 vector RBP+550 and param6 particles RBP-58 read only; forwards to648d70 and690470. Other calls receive heap/global objects or own locals.',
 0x648d70:'Reads three scalars from param2; forwards scalar values only.',
 0x690470:'Reads four vector scalars and three particle IDs; copies IDs before reusing local param6 slot for handles. Neither caller pointer is written or retained.',
 0x5c6330:'Copies param3/param4 components to own locals;5c65e0 receives only local constructed matrix. Caller R9 at RBP+8 read only.',
 0x5ff1c0:'Call-free output16bytes at RDX; param4 input read only. Caller outputs RBP+560/+620/+630/+640.',
 0x63e000:'Call-free record copy writes only RCX0..8f, reads RDX; caller output RBP+650.',
 0x6899a0:'Caller records RBP+280/+650/+310/+3a0/+430/+4c0; direct input writes only+74/+78. Only original-address forwards: record+54 to1f1b00/5f57a0, whole record to63cca0. Other calls use copied scalars, heap objects, or callee locals.',
 0x1f1b00:'Call-free:16byte RDX output, RCX/R8 input read only;6899a0 supplies its own output local.',
 0x63cca0:'Caller record passed only as source to698600 compare and698750 copy. Other calls use heap/global state.',
 0x698600:'Read-only compare of input fields; forwards+54/+64 only to2692d0.',
 0x698750:'Call-free: copies source RDX to distinct heap destination RCX; never writes source.',
 0x2692d0:'Call-free: compares three floats, no stores.',
 0x6093e0:'Collision record RBP+60 forwarded only as typed ref to managed table240 and record+70 read-only input5d3e70; no original-address retention.',
 0x5d3e70:'Reads param4 two quadwords to callee locals, no input write or forwarding.',
 0x6863f0:'Only one heap-pointer argument; no caller-local pointer or higher argument consumed.',
 0x688af0:'Only one heap-pointer argument; virtual receives heap object and scalar1.',
 0x6bfd80:'Four arguments, all heap/scalar; no higher argument consumed.',
 0x613350:'One heap-pointer argument; no higher argument consumed.',
 0x6133b0:'One heap-pointer argument; no higher argument consumed.',
 0x685930:'Heap receiver, float and byte arguments; no higher argument consumed.',
 0x5d1e60:'Higher args explicit heap vector and boolean; original vector inputs read only or passed read-only5c6330.',
 0x5d42e0:'Param6 unused. Param5 explicit heap R12, param7zero; forwards heap vector to5d3e70.',
}
job_loop_raw=pe.get_data(0x686450,0x1761)
job_loop_ins=list(md.disasm(job_loop_raw,base+0x686450))
if sum(i.size for i in job_loop_ins)!=len(job_loop_raw):raise ValueError('Job loop decode incomplete')
job_loop_writes=[]
for ins in job_loop_ins:
 for index,operand in enumerate(ins.operands):
  if operand.type!=X86_OP_MEM or ins.reg_name(operand.mem.base) not in ('rbp','rsp'):continue
  # Only post-prologue accesses can reference this fixed frame.
  if ins.address<base+0x68647d:continue
  if operand.mem.index:raise ValueError('Indexed frame memory requires another job proof')
  off=operand.mem.disp-(0x100 if ins.reg_name(operand.mem.base)=='rsp' else 0)
  if ins.mnemonic=='lea' and -0x60<=off<-0x58:raise ValueError('Job Mission home address escapes')
  if memory_operand_writes(ins,index):
   job_loop_writes.append({'address':hex(ins.address),'rbpOffset':off,'size':operand.size})
   if off<-0x58 and -0x60<off+operand.size and ins.address!=base+0x6864c7:raise ValueError('Job Mission home has another overlapping write')
job_loop_calls=[{'address':hex(i.address),'target':hex(i.operands[0].imm),'bytes':i.bytes.hex()} for i in job_loop_ins if i.mnemonic=='call' and i.operands[0].type==X86_OP_IMM]
if any(i.mnemonic=='call' and i.operands[0].type!=X86_OP_IMM for i in job_loop_ins):raise ValueError('Unreviewed indirect job call')
if {int(c['target'],16)-base for c in job_loop_calls}-set(job_loop_callee_effects)-{0x9df5b0}:raise ValueError('Unreviewed job callee')
job_shared_path=p/'native-job-mission-spill-review.json'
job_shared=json.loads(job_shared_path.read_text())
if job_shared['targetSha256']!=d['targetSha256']:raise ValueError('Shared job spill target mismatch')
check_proof_bytes(job_shared)
for address_,seed in job_shared['exactReloadSeeds'].items():exact_reload_seeds[(0x180685a90,int(address_,16))]=seed
for target in job_loop_callee_effects:
 if base+target not in evs and not any(x['address']==hex(base+target) for x in job_shared['helperInstructions']):raise ValueError(f'Missing retained job callee evidence {hex(target)}')
job_callback_path=p/'managed-core-callback-bodies.json'
job_callback_data=json.loads(job_callback_path.read_text())
job_loop_proof={'root':'0x180686450','targetSha256':d['targetSha256'],
 'proof':'Typed80byte job record entryRCX+20 supplies Agent.686487 savesAgent;686490 consumes heap only.68649d reloadsAgent,6864bc loadsAgent+ab8,6864c7 storesMission atRBP-60. RBP=steadyRSP+100; home never address-escapes and has exactly one overlapping direct write, including semantic SIMD stores. Whole1176instruction body is covered, not just a linear prefix: backedges687ba6/687bac reenter686610/686661 and all their calls are included. Bounded callee writes and typed callback outputs cannot overlap home[-60,-58). Reload687050 is therefore Mission on every normal path. This is static original-binary/typed-ABI evidence, not runtime verification.',
 'proofInstructions':[{'address':'0x180686450','bytes':job_loop_raw.hex()}],
 'directFrameWrites':job_loop_writes,'calls':job_loop_calls,
 'calleeEffects':{hex(base+t):{'effect':effect,'evidenceId':evs.get(base+t),'rawHelperEvidence':str(job_shared_path) if base+t not in evs else None} for t,effect in job_loop_callee_effects.items()},
 'sharedHelperEvidence':{'path':str(job_shared_path),'sha256':hashlib.sha256(job_shared_path.read_bytes()).hexdigest()},
 'managedBoundaryEvidence':{'path':str(job_callback_path),'sha256':hashlib.sha256(job_callback_path.read_bytes()).hexdigest()},
 'seeds':{'0x180687050':{'register':'rax','pointerKind':'mission','biasBytes':0,'instruction':{'address':'0x180687050','bytes':'488b45a0'}}}}
check_proof_bytes(job_loop_proof)
exact_reload_seeds[(0x180686450,0x180687050)]=job_loop_proof['seeds']['0x180687050']

analysis={};entry_conflicts=[];blocked_entries=set();pending=set(a for a in contracts if a in groups and eligible_procedure(a))
while pending:
 root=min(pending);pending.remove(root);states,report=analyze(root,contracts[root]);analysis[root]=(states,report)
 if states is None:continue
 insns=cache[root][0]
 for addr,st in states.items():
  ins=insns[addr]
  if ins.mnemonic not in ['call','jmp'] or not ins.operands or ins.operands[0].type!=X86_OP_IMM:continue
  target=ins.operands[0].imm
  if target==root or not eligible_procedure(target):continue
  passed={r:v for r,v in st.items() if r in ['rcx','rdx','r8','r9'] and (v[0] in ['mission','agent','agent-ai','missile'] or v[0].startswith(('mission-path:','agent-path:','missile-path:')))}
  if not passed or target in blocked_entries:continue
  if target not in groups and not add_leaf_group(target):continue
  if target in contracts and contracts[target].get('kind')!='global-only-analysis-root':
   conflicts=conflicting_entry_registers(contracts[target],{r:v[0] for r,v in passed.items()})
   for rr,kind in contracts[target].get('entryRegisters',{'rcx':contracts[target]['entryPointerKind']}).items():
    if rr in passed and contracts[target].get('entryBiases',{}).get(rr,0)!=passed[rr][1]:
     conflicts[rr]={'expectedBias':contracts[target].get('entryBiases',{}).get(rr,0),'observedBias':passed[rr][1]}
   if conflicts:
    blocked_entries.add(target)
    entry_conflicts.append({'target':hex(target),'caller':hex(root),'callInstruction':flow.basic(ins),'registerConflicts':conflicts})
   continue
  contract={'kind':'exact-direct-call-register-owner','entryPointerKind':passed.get('rcx',next(iter(passed.values())))[0],
            'entryRegisters':{r:v[0] for r,v in passed.items()},'sourceProcedure':hex(root),
            'entryBiases':{r:v[1] for r,v in passed.items()},
            'callInstruction':flow.basic(ins),'argumentProvenance':{r:[flow.basic(insns[x]) for x in sorted(v[2])] for r,v in passed.items()},
            'evidenceIds':list(dict.fromkeys(contracts[root]['evidenceIds']+([evs[root]] if root in evs else [])))}
  contracts[target]=contract;pending.add(target)

# An inferred descendant cannot retain a proof whose owner contract conflicted.
changed=True
while changed:
 changed=False
 for target,contract in contracts.items():
  source_root=contract.get('sourceProcedure')
  if source_root and int(source_root,16) in blocked_entries and target not in blocked_entries:
   blocked_entries.add(target);changed=True
for capture in additional_worker_proofs:
 if 'captureOwner' not in capture:continue
 owner=capture['captureOwner'];states,report=analysis[int(owner['procedure'],16)]
 value=states.get(int(owner['address'],16),{}).get(owner['register'])
 if report['status']!='converged' or value is None or value[:2]!=('mission',0):raise ValueError('Worker capture lacks independent Mission source: '+capture['procedure'])
 capture['captureOwner']['entryContract']=contracts[int(owner['procedure'],16)]
selected={a:contracts[a] for a in analysis if eligible_procedure(a) and a not in blocked_entries}
for root,contract in sorted(selected.items()):
 states,report=analysis[root]
 if states is None:reports.append(report);continue
 insns=cache[root][0];count=0
 for c in inv['candidates']:
  addr=int(c['address'],16)
  if addr not in states:continue
  ins=insns[addr];st=states[addr]
  for m in c['matches']:
   if not 0x62ca50<=m['decodedValue']<=0x1184b40:continue
   n=m['operandIndex'];op=ins.operands[n];rr=None
   if op.type==X86_OP_MEM and op.mem.base!=X86_REG_RIP:
    br=reg(ins,op.mem.base);ir=reg(ins,op.mem.index)
    if br in st and st[br][0]=='mission' and (ir not in st or st[ir][0] in ['constant','agent-index']):rr=br
    elif ir in st and st[ir][0]=='mission' and op.mem.scale==1 and (br not in st or st[br][0] in ['constant','agent-index']):rr=ir
   elif op.type==X86_OP_IMM and ins.mnemonic=='add' and n==1 and ins.operands[0].type==X86_OP_REG and ins.operands[0].size==8:
    dr=reg(ins,ins.operands[0].reg)
    if dr in st and st[dr][0]=='mission':rr=dr
   if rr is None:continue
   bias=st[rr][1];definition_addresses=set(st[rr][2])
   if op.type==X86_OP_MEM:
    for other,multiplier in [(reg(ins,op.mem.base),1),(reg(ins,op.mem.index),op.mem.scale)]:
     if other!=rr and other in st and st[other][0]=='constant':
      bias+=st[other][1]*multiplier;definition_addresses.update(st[other][2])
   defs=[flow.basic(insns[x]) for x in sorted(definition_addresses)]
   item={'address':c['address'],'rva':c['rva'],'fileOffset':c['fileOffset'],'instructionBytes':c['bytes'],'instruction':c['mnemonic']+' '+c['operands'],'operandIndex':n,'operandKind':m['kind'],'encodedOffset':m['encodedOffset'],'encodedSize':m['encodedSize'],'originalValue':m['decodedValueHex'],'classification':'mission-relative-field','missionBiasBytes':st[rr][1],'missionRegister':rr,'provenanceKind':'Mission-entry-register-conservative-CFG','provenanceInstructions':defs,'entryContract':contract,'canonicalProcedure':hex(root),'evidenceIds':list(dict.fromkeys(contract['evidenceIds']+([evs[root]] if root in evs else []))),'reviewScope':'Exact operand only. Must-proven owner and bias on every modeled path. Includes explicit stack/SIMD spills, affine pointers and confirmed Agent owner loads; stack escapes invalidate spill state. No whole-function numeric approval.','standalonePatchSafe':False}
   key=(item['address'],n)
   item['missionBiasBytes']=bias
   if key not in old:old[key]=item;allnew.append(item)
   count+=1
 report['selectedOperands']=count;reports.append(report)

# Exact entry operand before an externally dispatched indirect tail call. This
# does not relax the generic unknown-indirect-jump CFG model.
leaf_candidate=next(x for x in inv['candidates'] if x['address']=='0x1805d7607')
leaf_contract=contracts[0x1805d7600]
if pe.get_data(0x5d7600,13)!=bytes.fromhex('488b0569b37c008b89304a1801'):raise ValueError('Registered leaf entry bytes changed')
for match in leaf_candidate['matches']:
 if match['kind']=='memory_displacement' and match['operandIndex']==1:
  item={'address':leaf_candidate['address'],'rva':leaf_candidate['rva'],'fileOffset':leaf_candidate['fileOffset'],
   'instructionBytes':leaf_candidate['bytes'],'instruction':leaf_candidate['mnemonic']+' '+leaf_candidate['operands'],
   'operandIndex':1,'operandKind':match['kind'],'encodedOffset':match['encodedOffset'],'encodedSize':match['encodedSize'],
   'originalValue':match['decodedValueHex'],'classification':'mission-relative-field','missionBiasBytes':0,'missionRegister':'rcx',
   'provenanceKind':'Mission-entry-register-conservative-CFG','provenanceInstructions':[],
   'entryContract':leaf_contract,'canonicalProcedure':'0x1805d7600','evidenceIds':leaf_contract['evidenceIds'],
   'reviewScope':'Exact entry operand only: registered Mission RCX unchanged by preceding MOV RAX,[global]. Indirect tail successor is not analyzed.','standalonePatchSafe':False}
  old[(item['address'],1)]=item;allnew.append(item)
d['instructionSiteMappings'] = sorted(old.values(), key=lambda x:(int(x['address'],16),x['operandIndex']))
d['instructionSiteMappingCount'] = len(old)
exclusions={x['address']:x for x in d['instructionSiteExclusions']}
for dispatch,switch in reviewed_switches.items():
 for candidate in inv['candidates']:
  a=int(candidate['address'],16)
  if switch['guardStart']<=a<dispatch:
   for operand in candidate['matches']:
    if operand['kind']=='memory_displacement' and operand['decodedValue']==switch['tableStart']-base:
     exclusions[candidate['address']]={'address':candidate['address'],'rva':candidate['rva'],
      'instructionBytes':candidate['bytes'],'instruction':candidate['mnemonic']+' '+candidate['operands'],
      'operandIndex':operand['operandIndex'],'classification':'image-relative-switch-table',
      'originalValue':operand['decodedValueHex'],'reason':'Exact guarded image-relative jump-table load; image base and table bytes are separately verified, not a Mission field.',
      'evidenceIds':switch['evidenceIds'],'action':'leave-unchanged'}
d['instructionSiteExclusions']=sorted(exclusions.values(),key=lambda x:int(x['address'],16))
review = d['missionMethodFlowReview']
review['functions'] = reports
review['method']='Must-proven CFG owner flow: exact ABI contracts, global Mission, reviewed Agent/AI owner fields, frame capture paths, Agent pointer lists, affine register offsets, and exact stack/SIMD spills. Calls clobber volatile registers; any escaped stack pointer invalidates all spill provenance, except byte-verified register-preserving stack probe.'
review['limitations']=['Unknown owner contracts remain unproved. Unreviewed indirect jumps join every instruction conservatively. Decoder gaps are skipped. No runtime execution or complete native call graph is claimed.']
review['additionalMappedOperands'] = len(allnew)
review.pop('reproductionSource', None)
review.pop('independentReviewPending', None)
review['reproductionScript'] = {'path':str(Path(__file__).resolve()), 'sha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest()}
review['entryContracts'] = {hex(k):v for k,v in contracts.items() if k in selected}
review['agentSourceContracts'] = {hex(k):v for k,v in agent_source_contracts.items()}
review['entryContractConflicts'] = entry_conflicts
review['ownerFieldProofs'] = owner_field_proofs
review['workerCaptureProofs'] = worker_proofs
review['additionalFrameWorkerProofs']=additional_worker_proofs
review['privateHomeReloadProof']=private_home_proof
review['initializerSpillProof']={'path':str(initializer_path),'sha256':hashlib.sha256(initializer_path.read_bytes()).hexdigest(),'exactReloadSeeds':initializer_proof['exactReloadSeeds']}
review['jobLoopMissionHomeProof']=job_loop_proof
review['jobSharedMissionHomeProof']={'path':str(job_shared_path),'sha256':hashlib.sha256(job_shared_path.read_bytes()).hexdigest(),'exactReloadSeeds':job_shared['exactReloadSeeds']}
review['memoryStoreSemantics']={'path':str(ROOT/'tools/battle-scale/native_instruction_semantics.py'),'sha256':hashlib.sha256((ROOT/'tools/battle-scale/native_instruction_semantics.py').read_bytes()).hexdigest()}
review['attackSpillReloadProof']={'path':str(attack_spill_path),'sha256':hashlib.sha256(attack_spill_path.read_bytes()).hexdigest(),'exactReloadSeeds':attack_spill_proof['exactReloadSeeds']}
review['attackLateSpillReloadProof']={'path':str(attack_late_path),'sha256':hashlib.sha256(attack_late_path.read_bytes()).hexdigest(),'exactReloadSeeds':attack_late_proof['exactReloadSeeds']}
review['secondaryAgentJobRecordProof']={'path':str(agent_job_path),'sha256':hashlib.sha256(agent_job_path.read_bytes()).hexdigest(),'recordProof':agent_job_proof,'dispatchProof':agent_job_dispatch}
review['externalTailDispatchProofs']={hex(k):v for k,v in reviewed_external_tails.items()}
review['secondaryCallProofs']={'path':str(secondary_path),'sha256':hashlib.sha256(secondary_path.read_bytes()).hexdigest(),'entryContracts':secondary_contracts['entryContracts']}
review['tickSpillReloadProofs']={'path':str(tick_spill_path),'sha256':hashlib.sha256(tick_spill_path.read_bytes()).hexdigest(),'exactReloadSeeds':tick_spill_proof['exactReloadSeeds'],'scope':'Only the exact reviewed reload instructions in5c6f40 seed values; all other stack invalidation remains conservative.'}
review['visualStrengthOwnerProof']=visual_strength_proof
review['missileOwnerProof']=missile_proof
review['missilePointerListProof']=missile_list_proof
review['additionalLeafOwnerProofs']={'path':str(leaf_owner_path),'sha256':hashlib.sha256(leaf_owner_path.read_bytes()).hexdigest(),'contracts':leaf_owner_contracts}
review['networkCallbackProofs']=network_proof
review['networkLeafProof']=network_leaf_proof
review['jobAgentQueueProof']=job_agent_queue_proof
review['missileWorkerCaptures']=missile_worker_proof
review['movementSpillProof']={'path':str(movement_path),'sha256':hashlib.sha256(movement_path.read_bytes()).hexdigest(),'entryContracts':movement_proof['entryContracts'],'exactReloadSeeds':movement_proof['exactReloadSeeds']}
review['sceneCallbackProofs']={'path':str(scene_contract_path),'sha256':hashlib.sha256(scene_contract_path.read_bytes()).hexdigest(),'contracts':scene_contracts['contracts'],'limitations':scene_contracts['limitations']}
review['agentPointerListProofs'] = list_proof
review['stackProbeCallSummary'] = {'address':'0x1809e0760','bytes':stack_probe_hex,'effect':'All GPR and RSP restored; probes only below original caller RSP.'}
review['leafInitializerCallSummary']={'address':'0x180698570','bytes':simple_init_hex,'effect':'Only RAX/RDX and flags changed; bounded stores through RCX. Stack-escape invalidation remains in force.'}
review['reviewedSwitchTables'] = [{'dispatchAddress':hex(a),**{k:(hex(v) if isinstance(v,int) else v) for k,v in x.items()}} for a,x in reviewed_switches.items()]
review['blockedEntryContracts'] = [hex(x) for x in sorted(blocked_entries)]
review['knownEntryLeafBoundaries'] = reviewed_leaf_ranges
review['pureLeafRegisterEffects']={hex(k):v for k,v in leaf_register_effects.items() if v['preservedRegisters']}
review['highOperandCrossCheck']={'scope':'Exact owner-flow proof for registered callback region6f0000..711000; original high procedure-wide approvals are not consulted.',
 'mappings':[x for x in allnew if int(x['address'],16)>=0x1806f0000]}
review['excludedUnprovedAgentSourceContracts'] = ['0x1806093e0', '0x180600750', '0x180600b00']
review['reviewedSeedIdentityEvidence'] = 'Initial four owner identities have external call/constructor/task evidence; this does not claim all possible caller enumeration.'
protected = [Path(inv['binary']['path']), p / 'native-pool-reference-inventory.json',
             p / 'native-pool-reference-classification.json', callback_path, safety_path, scene_contract_path, leaf_owner_path, independent_review_path, secondary_path, tick_spill_path, movement_path, attack_spill_path, agent_job_path, *bundle_paths,
             Path(__file__).resolve(), attack_late_path, initializer_path, job_callback_path, job_shared_path, ROOT / 'tools/battle-scale/native_instruction_semantics.py', ROOT / 'tools/battle-scale/classify_pool_references.py']
protected.extend(q.glob('*'))
default_output = p / 'pool-semantic-review-mid.json'
if path.absolute() != default_output.absolute():
    protected.append(default_output)
path = safe_review_output(path, protected)
path.write_text(json.dumps(d,indent=2)+'\n')
print(json.dumps({'mappedOperands':len(old),'generatedCFGOperands':len(allnew),'functions':len(reports),'converged':sum(x['status']=='converged' for x in reports),'output':str(path)}))
