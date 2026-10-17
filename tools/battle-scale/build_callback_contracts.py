"""Join read-only Cecil signatures, managed switch IL and native registration bytes.

This establishes callback-entry argument contracts, not contracts for every call
to shared native entries, and never approves instruction patches by itself.
"""
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
from collections import Counter, defaultdict

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'build/battle-scale/python-libs'))
import capstone
import pefile
from patch_native_copy import safe_review_output


def build():
    folder = ROOT / 'evidence/engine-extension'
    managed_path = folder / 'pool-managed-expansion-audit.json'
    registration_path = folder / 'native-registration.json'
    managed = json.loads(managed_path.read_text())
    registration = json.loads(registration_path.read_text())
    script = ROOT / 'tools/battle-scale/managed-audit/Read-CallbackContracts.ps1'
    result = subprocess.run(['powershell', '-NoProfile', '-File', str(script)], check=True, capture_output=True)
    metadata = json.loads(result.stdout.decode('utf-8-sig'))
    known = {x['path'].replace('\\', '/').lower(): x['sha256'] for x in managed['artifacts']}
    for artifact in metadata['artifacts']:
        if artifact['sha256'] != known[artifact['path'].replace('\\', '/').lower()]:
            raise ValueError('Managed metadata differs from retained switch audit')
    method = next(x for x in managed['methods'] if 'ScriptingInterfaceObjects::SetFunctionPointer' in x['signature'])
    lines = method['il']
    if lines[:3] != ['IL_0000: ldarg.0', 'IL_0001: stloc.0', 'IL_0002: ldloc.0']:
        raise ValueError('Switch index is not direct callback ID')
    targets = next(x for x in lines if ': switch ' in x).split(': switch ')[1].split(',')
    managed_bindings = {}
    for callback_id, target in enumerate(targets):
        index = next(i for i, line in enumerate(lines) if line.startswith(target + ':'))
        block = lines[index:index+7]
        if len(block) > 1 and ': ldtoken ' in block[1]:
            typename = block[1].split(': ldtoken ')[1]
            if block[-1].endswith(': ret') and 'stsfld ' in block[-2] and typename in block[-2]:
                managed_bindings[typename] = {'callbackId': callback_id, 'managedSwitchTarget': target, 'managedIl': block}
    source_path = Path(registration['subject']['local_path'])
    source = source_path.read_bytes()
    source_hash = hashlib.sha256(source).hexdigest()
    if source_hash != registration['subject']['digest']['sha256']:
        raise ValueError('Native source identity differs')
    pe = pefile.PE(data=source)
    base = pe.OPTIONAL_HEADER.ImageBase
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True
    # Decode registration from source rather than assuming adjacent text values.
    registration_start = 0x643720
    runtime = next(x for x in pe.DIRECTORY_ENTRY_EXCEPTION if x.struct.BeginAddress == registration_start)
    registration_instructions = list(md.disasm(pe.get_data(registration_start, runtime.struct.EndAddress-registration_start), base+registration_start))
    native_bindings = {}
    pointer = number = None
    proof = []
    for ins in registration_instructions:
        if ins.mnemonic == 'lea' and ins.op_str.startswith('rdx, [rip '):
            pointer = ins.address+ins.size+ins.operands[1].mem.disp
            proof = []
        elif ins.mnemonic == 'mov' and ins.op_str.startswith('ecx, ') and ins.operands[1].type == capstone.x86.X86_OP_IMM:
            number = ins.operands[1].imm
        elif ins.mnemonic == 'xor' and ins.op_str == 'ecx, ecx':
            number = 0
        elif ins.mnemonic == 'call' and ins.op_str == 'qword ptr [rbx + 0x108]':
            if pointer is None or number is None or number in native_bindings:
                raise ValueError(f'Unexpected native registration sequence at {ins.address:x}: pointer={pointer} number={number}')
            native_bindings[number] = {'entryAddress': hex(pointer), 'nativeRegistrationInstructions': proof+[{'address':hex(ins.address),'bytes':ins.bytes.hex(),'decoded':ins.mnemonic+' '+ins.op_str}]}
            pointer = number = None
            continue
        if pointer is not None:
            proof.append({'address':hex(ins.address),'bytes':ins.bytes.hex(),'decoded':ins.mnemonic+' '+ins.op_str})
    entries = []
    for signature in metadata['delegates']:
        binding = managed_bindings[signature['type']]
        native = native_bindings[binding['callbackId']]
        owner, name = signature['type'].split('/')
        api = owner.split('ScriptingInterfaceOf')[1]+'.'+name.removesuffix('Delegate')
        params = signature['parameters']
        receiver = bool(params and params[0]['type'] == 'System.UIntPtr')
        result_type = signature['returnType']
        result_info = metadata['returnTypes'].get(result_type)
        if result_type.startswith('System.') or result_info and result_info['isEnum']:
            indirect_return = False
        elif result_type == 'TaleWorlds.Library.Vec2':
            if [x['type'] for x in result_info['fields']] != ['System.Single', 'System.Single']:
                raise ValueError('Vec2 layout changed')
            indirect_return = False
        elif result_type in ('TaleWorlds.Library.Vec3','TaleWorlds.Library.MatrixFrame','TaleWorlds.Engine.WorldPosition',
                              'TaleWorlds.DotNet.NativeObjectPointer','TaleWorlds.MountAndBlade.Mission/AgentCreationResult',
                              'TaleWorlds.MountAndBlade.AgentProximityMap/ProximityMapSearchStructInternal'):
            indirect_return = True
        else:
            raise ValueError('Unreviewed return ABI '+result_type)
        # Generic pointer names are corroborated with managed caller CIL.
        caller_contexts = [x for x in metadata['managedCallerContexts'] if '::'+name.removesuffix('Delegate')+'(' in x['instruction']]
        if receiver and params[0]['name'] in ('ptr','pointer','scenePointer'):
            if not any('Mission::get_Pointer()' in ' '.join(x['context']) for x in caller_contexts):
                raise ValueError('Generic receiver name lacks Mission caller proof '+api)
        pointer_kind = ('agent' if '.IMBAgent/' in signature['type'] else 'mission') if receiver else None
        # Full namespace spelling is ScriptingInterfaceOfIMBAgent, not .IMBAgent.
        if receiver:
            pointer_kind = 'agent' if api.startswith('IMBAgent.') else 'mission'
        address = int(native['entryAddress'],16)
        head = []
        for ins in md.disasm(pe.get_data(address-base,160),address):
            head.append({'address':hex(ins.address),'bytes':ins.bytes.hex(),'decoded':ins.mnemonic+' '+ins.op_str})
            if ins.mnemonic in ('ret','int3') or len(head) == 20:
                break
        entry = dict(native, callbackBindings=[dict(binding,name=api,nativeRegistrationEvidenceId=registration['evidence_id'])],
                     signature=signature, returnTypeLayout=result_info, indirectStructureReturn=indirect_return,
                     entryRegister=('rdx' if indirect_return else 'rcx') if receiver else None,
                     pointerKind=pointer_kind, evidenceIds=[registration['evidence_id']], entryInstructions=head,
                     proof=('Managed delegate first parameter is the '+pointer_kind+' receiver; Win64 '+('hidden result pointer occupies RCX, shifting receiver to RDX.' if indirect_return else 'scalar/enum/8-byte Vec2 return uses the return register, so receiver remains RCX.')) if receiver else 'No native Agent/Mission receiver: this is a static or constructor callback.',
                     managedCallerContexts=caller_contexts if params and params[0]['name'] in ('ptr','pointer','scenePointer') else [])
        entries.append(entry)
    by_address = defaultdict(list)
    for item in entries:
        by_address[item['entryAddress']].append(item)
    contracts = []
    excluded = []
    for address, rows in sorted(by_address.items()):
        kinds = {(x['entryRegister'],x['pointerKind']) for x in rows}
        if len(kinds) != 1 or next(iter(kinds))[1] is None:
            excluded.append({'entryAddress':address,'reason':'shared entry has conflicting receiver contracts' if len(kinds)!=1 else 'no receiver', 'bindings':rows})
            continue
        record = dict(rows[0])
        record['callbackBindings'] = [b for row in rows for b in row['callbackBindings']]
        record['status'] = 'registered-callback-entry-contract'
        contracts.append(record)
    output = folder / 'native-callback-contracts.json'
    report = {'schemaVersion':1,'targetSha256':source_hash,'gameExecuted':False,'status':'static-entry-contract-catalog',
              'sources': [{'path':str(p),'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in (managed_path,registration_path,script)],
              'managedMetadata':metadata,'contracts':contracts,'excludedEntries':excluded,
              'summary':{'delegateCount':len(entries),'uniqueEntryCount':len(by_address),'receiverContracts':len(contracts),'excludedEntries':len(excluded),
                         'receiverRegisters':dict(Counter(x['entryRegister'] for x in contracts))},
              'limitations':['The contract describes calls through registered delegate signatures; direct and indirect native callers must separately honor the same entry contract.',
                             'Native Windows x64 ABI and exact signature/return layouts select receiver registers; first20 instructions are retained for corroboration, not a complete body review.',
                             'This catalog does not authorize any patch or claim full engine coverage.']}
    safe_review_output(output,[source_path,managed_path,registration_path,script]+[Path(x['path']) for x in metadata['artifacts']]).write_text(json.dumps(report,indent=2)+'\n')
    print(json.dumps(report['summary']))


if __name__ == '__main__':
    build()
