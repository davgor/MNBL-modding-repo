"""Retain exact owner contracts for three previously unclassified native leaves."""
import hashlib
import json
from pathlib import Path
import struct
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT/'build/battle-scale/python-libs'))
import capstone
import pefile
from patch_native_copy import safe_review_output


def audit():
    folder = ROOT/'evidence/engine-extension'
    inventory_path = folder/'native-pool-reference-inventory.json'
    mid_path = folder/'pool-semantic-review-mid.json'
    inventory = json.loads(inventory_path.read_text())
    source_path = Path(inventory['binary']['path'])
    source = source_path.read_bytes()
    digest = hashlib.sha256(source).hexdigest()
    if digest != '2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a':
        raise ValueError('Unsupported source for these exact contracts')
    pe = pefile.PE(data=source)
    base = pe.OPTIONAL_HEADER.ImageBase
    decoder = capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64)
    decoder.detail = True
    def span(a,z):
        decoded = list(decoder.disasm(pe.get_data(a-base,z-a),a))
        if sum(i.size for i in decoded) != z-a:
            raise ValueError('Proof instruction boundary gap')
        return [dict(address=hex(i.address),bytes=i.bytes.hex(),instruction=i.mnemonic+' '+i.op_str) for i in decoded]
    def exact(a,h):
        if pe.get_data(a-base,len(bytes.fromhex(h))).hex() != h:
            raise ValueError('Expected instruction differs: '+hex(a))
    bundle_paths = [folder/n for n in ('native-ghidra-evidence.json','native-pool-ghidra-evidence.json','native-expansion-ghidra-evidence.json','native-closure-ghidra-evidence.json')]
    records = [r for p in bundle_paths for r in json.loads(p.read_text())['records']]
    def evidence(proc):
        values = list(dict.fromkeys(r['evidence_id'] for r in records if r.get('parameters',{}).get('procedure')==proc))
        if not values: raise ValueError('Missing retained procedure '+proc)
        return values
    mid = json.loads(mid_path.read_text())['missionMethodFlowReview']['entryContracts']
    # Agent's inline direction component stores the original Agent at component+b8.
    exact(0x180607571,'488bd9')
    exact(0x18060764b,'488d8b58010000488bd3e8c6ed0800')
    exact(0x180696420,'488991b8000000')
    exact(0x180697d63,'488b89b8000000')
    owner = dict(kind='agent',offset='0x210',valueKind='agent',valueBias=0,
        proof='607550 constructs the inline component at Agent+158 with Agent as RDX;696420 stores RDX at component+b8.697d60 reads that Agent, then its established ab8 Mission owner before tail-calling698800.',
        proofInstructions=span(0x180607550,0x18060765a)+span(0x180696420,0x180696427)+span(0x1806f7bc0,0x1806f7bf6)+span(0x180697d60,0x180697de9)+span(0x180698800,0x18069886b),
        evidenceIds=evidence('0x180607550')+evidence('0x180696420')+evidence('0x180697d60'))
    # Mission method pointer, adjustment zero, captured Mission and exact binder thunk.
    exact(0x1805c12d8,'488d0521630100')
    exact(0x1805c12df,'488945a044897da8')
    exact(0x1805c1353,'4c897018')
    binder = dict(procedure='0x1805d7600',kind='captured-Mission-member-callback',entryRegisters={'rcx':'mission'},entryBiases={'rcx':0},
        ownerContract=mid['0x1805c0e20'],
        proof='Initializer R14 is Mission and R15D is zero. It captures method5d7600 at binder+0, zero this-adjustment at+8 and Mission at+18. Installed thunk5e1220 calls that method with RCX=signextended adjustment+captured Mission. Manager5e1260 preserves all these fields on copy/move; RTTI independently names a bound Mission member method.',
        proofInstructions=span(0x1805c0e20,0x1805c0ea3)+span(0x1805c12d8,0x1805c135e)+span(0x1805e1220,0x1805e1254)+span(0x1805d7600,0x1805d7617),
        evidenceIds=evidence('0x1805e1220')+evidence('0x1805e1260'))
    # SimpleTask captures a two-word closure; the first word is the Mission.
    exact(0x1805c384e,'4c8be9')
    exact(0x1805c5107,'4c896c2460')
    exact(0x1805c5124,'488d0d95b50100')
    exact(0x1805e0700,'0f10020f1101')
    if struct.unpack('<Q',pe.get_data(0xae5300,8))[0] != 0x180073570:
        raise ValueError('SimpleTask execute vtable entry differs')
    task = dict(procedure='0x1805e06c0',kind='SimpleTask-captured-Mission-pointer',entryRegisters={'rcx':'mission-path:0'},entryBiases={'rcx':0},
        ownerContract=mid['0x1805c37d0'],
        proof='5c37d0 preserves Mission in R13 and copies {Mission,frame-local argument pointer} into the closure;073580 invokes manager5e06e0 with mode1 to copy16bytes to task+c8, then copies the callback address to task+e0. Vtableae5298+68 points to073570, which passes task+c8 as RCX to its callback.5e06c0 loads Mission from[RCX] before the selected large field.',
        proofInstructions=span(0x1805c37d0,0x1805c3851)+span(0x1805c50fe,0x1805c5147)+span(0x180073580,0x180073623)+span(0x1805e06e0,0x1805e0715)+span(0x180073570,0x18007357e)+span(0x1805e06c0,0x1805e06d7),
        evidenceIds=evidence('0x180073580'))
    output = safe_review_output(folder/'native-leaf-owner-contracts.json',[source_path, inventory_path,mid_path,*bundle_paths,Path(__file__).resolve()])
    output.write_text(json.dumps(dict(schemaVersion=1,targetSha256=digest,ownerFields=[owner],entryContracts=[binder,task],
        status='exact-owner-contracts-reviewed',gameLaunched=False,
        limitations=['Contracts grant entry pointer identities, not automatic approval of every numeric operand.','Tail-jump leaf boundaries are exact decoded source bytes; Ghidra does not name all these leaves.']),indent=2)+'\n')
    print(json.dumps(dict(output=str(output),ownerFields=1,entryContracts=2)))


if __name__=='__main__':audit()
