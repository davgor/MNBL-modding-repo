"""Recheck bounded residual numeric search; no target execution or binary writes."""
import hashlib
import json
import struct
import sys
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'build/battle-scale/python-libs'))
import capstone
import pefile
from patch_native_copy import safe_review_output


def review():
    folder = ROOT / 'evidence/engine-extension'
    candidates_path = folder / 'native-pool-residual-scalar-candidates.json'
    candidates = json.loads(candidates_path.read_text())
    manifest = json.loads((folder / 'native-pool-patch-4080.json').read_text())
    source = Path(manifest['sourcePath'])
    raw = source.read_bytes()
    if hashlib.sha256(raw).hexdigest() != manifest['sourceSha256']:
        raise ValueError('Native source changed')
    pe = pefile.PE(data=raw)
    base = pe.OPTIONAL_HEADER.ImageBase
    decoder = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    decoder.detail = True
    selected = {int(p['address'], 16) for p in manifest['patches']}
    selected_values = {c['originalValue'] for p in manifest['patches'] for c in p['operandChanges']}
    sites = [s for f in candidates['valueFamilies'] for s in f['sites']]
    if len(sites) != 399 or len(candidates['valueFamilies']) != 58:
        raise ValueError('Residual set changed; review again')
    for family in candidates['valueFamilies']:
        if int(family['value'], 16) in selected_values:
            raise ValueError('Residual scalar overlaps selected offset value')
        if family['count'] != len(family['sites']):
            raise ValueError('Family count mismatch')
        for site in family['sites']:
            address = int(site['address'], 16)
            if address in selected:
                raise ValueError('Residual site is now selected')
            for instruction in [*site['previousInstructions'], site, *site['nextInstructions']]:
                a = int(instruction['address'], 16)
                b = bytes.fromhex(instruction['bytes'])
                if pe.get_data(a-base, len(b)) != b:
                    raise ValueError('Residual context bytes differ')
            instruction = next(decoder.disasm(bytes.fromhex(site['bytes']), address))
            if instruction.mnemonic != site['mnemonic'] or instruction.op_str != site['operands']:
                raise ValueError('Residual decode differs')
            if instruction.mnemonic == 'mov' and instruction.operands[0].type == capstone.x86.X86_OP_REG and instruction.operands[0].size == 8:
                raise ValueError('Unexpected wide immediate pointer materialization')
    bundle_path = folder / 'native-closure-ghidra-evidence.json'
    bundle = json.loads(bundle_path.read_text())
    procedures = ('0x1808e31b8', '0x1800e8a10', '0x18031b330', '0x18039c7c0')
    retained = []
    for procedure in procedures:
        records = [r for r in bundle['records'] if r.get('operation') == 'procedure_pseudo_code' and r.get('parameters', {}).get('procedure') == procedure]
        if not records:
            raise ValueError('Missing retained semantic evidence: ' + procedure)
        retained.append({'procedure': procedure, 'evidenceId': records[-1]['evidence_id']})
    # This apparent IMUL is an overlapping interpretation of two image-relative
    # switch-table DWORDs, rather than a reachable arithmetic instruction.
    table_rva = 0x7b4ff4
    table_bytes = pe.get_data(table_rva, 40)
    table = struct.unpack('<10I', table_bytes)
    if struct.unpack('<2I', pe.get_data(0x7b500c, 8)) != (0x7b4c69, 0x7b4c80):
        raise ValueError('Switch table changed')
    dispatch = list(decoder.disasm(pe.get_data(0x7b4a40, 0x53c), base+0x7b4a40))
    dispatch_refs = [dict(address=hex(i.address), bytes=i.bytes.hex(), decoded=i.mnemonic+' '+i.op_str)
                     for i in dispatch if any(o.type == capstone.x86.X86_OP_MEM and o.mem.disp == table_rva for o in i.operands)]
    if not dispatch_refs or not all(0x7b4a40 <= value < 0x7b4f7c for value in table):
        raise ValueError('Switch table dispatch/target proof changed')
    special = {
        '0x1808e31b8': 'FMOD WASAPI frame duration conversion: multiply by 10,000,000 before sample-rate division; not a Mission offset.',
        '0x1807b500c': 'Switch-table data overlaps the linear decoder; two DWORD code RVAs, not an executed IMUL.',
        '0x1800e8aa7': 'Packed texture/cubemap descriptor flags; 128x128, six faces, format31 passed to texture creation.',
        '0x18031b39d': 'Packed render-texture descriptor flags for ship_top_down_depth.',
        '0x18039c829': 'Packed filtered_cubemap descriptor flags; 128x128, six faces, format31.',
        '0x180604af3': 'AttackCollisionData local header after initializer619de0; packed scalar fields.',
        '0x18068ec41': 'AttackCollisionData local header after initializer619de0; packed scalar fields.'}
    output = dict(schemaVersion=1, status='accepted-bounded-residual-search-review', sourceSha256=manifest['sourceSha256'],
        candidateArtifactSha256=hashlib.sha256(candidates_path.read_bytes()).hexdigest(),
        siteCount=len(sites), valueFamilyCount=58, families=dict(Counter(s['mnemonic'] for s in sites)),
        allSiteAndContextBytesRechecked=True, selectedChangedOperandValueOverlap=[], wideRegisterMovImmediateCount=0,
        specialSiteFindings=special, retainedEvidence=retained,
        switchTable=dict(rva=hex(table_rva), bytes=table_bytes.hex(), targetRvas=[hex(x) for x in table], dispatchReferences=dispatch_refs),
        disposition='Retain these exact numeric matches unchanged. No bulk scalar substitution. Selected edits require independent owner/CFG/ABI proofs.',
        scope='Restricted shipping Single0 candidate. This reviews residual numeric-match selection, not every possible computation in the engine.',
        limitations=['32-bit materialization can participate in computed addresses; width alone is not semantic proof. No claim of complete arbitrary computed-pointer coverage.',
            '3,099 undecodable executable bytes and indirect/split address constructions remain outside the exact decoded-operand search boundary.',
            'No target execution, rendered soldier count, performance, teardown or runtime safety has been verified.'],
        gameExecuted=False)
    target = safe_review_output(folder / 'native-pool-residual-scalar-review.json', [source, candidates_path, bundle_path, Path(__file__)])
    target.write_text(json.dumps(output, indent=2)+'\n')
    print(json.dumps({'sites':len(sites),'families':58,'output':str(target)}))


if __name__ == '__main__':
    review()
