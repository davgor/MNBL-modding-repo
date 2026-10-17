"""Prove exact Mission contexts registered in Scene callbacks, offline."""
import hashlib
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'build/battle-scale/python-libs'))
import capstone
import pefile
from patch_native_copy import safe_review_output


def audit():
    folder = ROOT / 'evidence/engine-extension'
    inventory_path = folder / 'native-pool-reference-inventory.json'
    mid_path = folder / 'pool-semantic-review-mid.json'
    bundle_path = folder / 'native-closure-ghidra-evidence.json'
    inventory = json.loads(inventory_path.read_text())
    source_path = Path(inventory['binary']['path'])
    source = source_path.read_bytes()
    if hashlib.sha256(source).hexdigest() != '2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a':
        raise ValueError('Source identity differs')
    pe = pefile.PE(data=source)
    base = pe.OPTIONAL_HEADER.ImageBase
    decoder = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    decoder.detail = True
    def span(start, end):
        result = list(decoder.disasm(pe.get_data(start-base, end-start), start))
        if sum(i.size for i in result) != end-start:
            raise ValueError('Incomplete proof decode')
        return result
    def raw(i):
        return dict(address=hex(i.address), bytes=i.bytes.hex(), instruction=i.mnemonic+' '+i.op_str)
    owner = json.loads(mid_path.read_text())['missionMethodFlowReview']['entryContracts']['0x1805c0e20']
    if owner['entryPointerKind'] != 'mission':
        raise ValueError('Initializer Mission contract missing')
    body = span(0x1805c0e20, 0x1805c2283)
    r14_writes = [i for i in body if any(i.reg_name(r) in ('r14','r14d','r14w','r14b') for r in i.regs_access()[1])]
    if len(r14_writes) != 1 or r14_writes[0].address != 0x1805c0e69 or r14_writes[0].bytes.hex() != '4c8bf1':
        raise ValueError('Initializer R14 is not preserved Mission')
    if pe.get_data(0x7ce20, 4).hex() != '488bc1c3':
        raise ValueError('Identity callback helper changed')
    records = json.loads(bundle_path.read_text())['records']
    def evidence(proc):
        values = [r['evidence_id'] for r in records if r.get('parameters', {}).get('procedure') == proc]
        if not values:
            raise ValueError('Missing retained REA record: '+proc)
        return values
    rows = []
    for start, end, target, context_offset, function_offset in [
        (0x1805c2241,0x1805c2262,0x1805c0c10,0x3368,0x3370),
        (0x1805c2262,0x1805c2283,0x1805c0cc0,0x3388,0x3390)]:
        code = span(start, end)
        expected = [('mov','rcx, r14'),('call','0x18007ce20'),('mov','rcx, qword ptr [r14 + 0x18]')]
        if [(i.mnemonic,i.op_str) for i in code[:3]] != expected:
            raise ValueError('Callback context preparation differs')
        lea, store_fn, store_ctx = code[3:]
        if lea.mnemonic != 'lea' or lea.reg_name(lea.operands[0].reg) != 'rdx' or lea.address+lea.size+lea.operands[1].mem.disp != target:
            raise ValueError('Callback function target differs')
        if store_fn.op_str != f'qword ptr [rcx + {hex(function_offset)}], rdx' or store_ctx.op_str != f'qword ptr [rcx + {hex(context_offset)}], rax':
            raise ValueError('Callback pair stores differ')
        rows.append(dict(procedure=hex(target), entryRegisters={'rcx':'mission'}, entryBiases={'rcx':0},
            kind='Scene-callback-Mission-context-registration', ownerContract=owner,
            contextOffset=hex(context_offset), functionOffset=hex(function_offset),
            proofInstructions=[raw(r14_writes[0]), *[raw(i) for i in code]],
            evidenceIds=list(dict.fromkeys(owner['evidenceIds']+evidence(hex(target))+evidence('0x18007ce20')))))
    dispatch = span(0x18042bc9d,0x18042bcc0)
    if [i.op_str for i in dispatch if i.address in (0x18042bca1,0x18042bcb6,0x18042bcbd)] != ['rcx, qword ptr [rax + 0x3388]', 'rax, qword ptr [rax + 0x3390]', 'rax']:
        raise ValueError('Second callback context dispatch differs')
    # Atmosphere callback function pointer and its separate Mission context.
    def exact(a, h):
        if pe.get_data(a-base, len(bytes.fromhex(h))).hex() != h:
            raise ValueError('Exact callback instruction differs: '+hex(a))
    exact(0x1805c10a9, '498b46184c89b0482b0000')
    exact(0x1802210e0, '49ff21')
    atmosphere_registration = span(0x1805c111f,0x1805c11af)
    atmosphere_dispatch = span(0x1801d115b,0x1801d117f)
    for target, load in [(0x1805c2790,0x1805c11a1),(0x1805c2aa0,0x1805c1159)]:
        instruction = next(i for i in atmosphere_registration if i.address == load)
        if instruction.address+instruction.size+instruction.operands[1].mem.disp != target:
            raise ValueError('Atmosphere function pointer differs')
        rows.append(dict(procedure=hex(target),entryRegisters={'rdx':'mission'},entryBiases={'rdx':0},
            kind='Scene-atmosphere-callback-separate-Mission-context',ownerContract=owner,
            contextOffset='0x2b48',functionOffset='0x2b28',
            proof='Initializer stores Mission R14 at Scene+2b48. Both branches install invoker2210e0 at Scene+2b40 and the selected callback at+2b28. Scene loader passes that context as RDX and the callable address as R9. Invoker JMP[R9] preserves RDX. The atmosphere object occupies RCX; it is not Mission.',
            proofInstructions=[raw(r14_writes[0]),*[raw(i) for i in span(0x1805c10a9,0x1805c10b4)],
                *[raw(i) for i in atmosphere_registration],*[raw(i) for i in atmosphere_dispatch],
                *[raw(i) for i in span(0x1802210e0,0x1802210e3)]],
            evidenceIds=list(dict.fromkeys(owner['evidenceIds']+evidence(hex(target))+evidence('0x1801d1165')))))
    # Scene float callback uses a bound Mission member, with zero adjustment.
    exact(0x1805c1259, '488d05f0240000')
    exact(0x1805c1260, '488945a044897da8')
    exact(0x1805c12cd, '4c897018488983d8910900')
    rows.append(dict(procedure='0x1805c3750',entryRegisters={'rcx':'mission'},entryBiases={'rcx':0},
        kind='Scene-bound-Mission-float-callback',ownerContract=owner,
        proof='Initializer binds method5c3750, zero this-adjustment and Mission R14 into a32byte binder stored at Scene+991d8. Thunk5e1350 loads the binder from RDX, computes RCX from its signed adjustment+captured Mission, and invokes its method. Float argument uses XMM1. Manager5e1380 copies method/adjustment, placeholder and Mission on copy, or transfers binder pointer on move; its RTTI names the bound Mission float member.',
        proofInstructions=[*[raw(i) for i in span(0x1805c0e20,0x1805c0ea3)],
            *[raw(i) for i in span(0x1805c1259,0x1805c12d8)],
            *[raw(i) for i in span(0x1805e1350,0x1805e137e)]],
        evidenceIds=list(dict.fromkeys(owner['evidenceIds']+evidence('0x1805c3750')+evidence('0x1805e1350')+evidence('0x1805e1380')))))
    exact(0x1805c11cf, '488d05ea360100488945a044897da8')
    exact(0x1805c1243, '4c897018488983b8910900')
    rows.append(dict(procedure='0x1805d48c0',entryRegisters={'rcx':'mission'},entryBiases={'rcx':0},
        kind='Scene-bound-Mission-physics-body-callback',ownerContract=owner,
        proof='Initializer binds method5d48c0, zero this-adjustment and Mission R14 into a32byte binder at Scene+991b8. Thunk5e1460 loads the binder from RDX, forwards the original body pointer as RDX, computes RCX from signed adjustment+captured Mission and invokes the method. Manager5e14a0 preserves method/adjustment and Mission on copy/move; RTTI names a Mission physics-body member.',
        proofInstructions=[*[raw(i) for i in span(0x1805c0e20,0x1805c0ea3)],
            *[raw(i) for i in span(0x1805c11cf,0x1805c124e)],
            *[raw(i) for i in span(0x1805e1460,0x1805e1491)]],
        evidenceIds=list(dict.fromkeys(owner['evidenceIds']+evidence('0x1805e1460')+evidence('0x1805e14a0')))))
    output = folder / 'native-scene-callback-contracts.json'
    output = safe_review_output(output, [source_path, inventory_path, mid_path, bundle_path, Path(__file__).resolve()])
    report = dict(schemaVersion=1, targetSha256=inventory['binary']['sha256'], status='exact-registration-contracts-reviewed',
        contracts=rows, identityHelper=dict(address='0x18007ce20',bytes='488bc1c3',effect='RAX=RCX; return'),
        secondCallbackDispatch=dict(instructions=[raw(i) for i in dispatch],evidenceIds=evidence('0x18042bcb6')),
        limitations=['The first callback registration is proved; its computed or external invocation is not located.',
            'These are exact callback-context contracts for the installed Mission initializer, not whole-function operand approvals.'],
        gameLaunched=False, sourceModified=False)
    output.write_text(json.dumps(report, indent=2)+'\n')
    print(json.dumps(dict(output=str(output),contracts=len(rows))))


if __name__ == '__main__':
    audit()
