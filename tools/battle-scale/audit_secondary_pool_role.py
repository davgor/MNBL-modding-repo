"""Bounded read-only investigation of the second Mission c60 pool.

Reads an offline original only. Does not patch, install, load or execute it.
Literal/reference absence is deliberately not a proof against computed pointers.
"""
from pathlib import Path
import argparse
import hashlib
import json
import struct
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'build/battle-scale/python-libs'))
import capstone
import pefile
from patch_native_copy import safe_review_output

SOURCE = ROOT / 'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
SHA = '2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def build(source=SOURCE):
    folder = ROOT / 'evidence/engine-extension'
    names = ['native-pool-layout.json', 'native-pool-helper-caller-review.json',
             'native-pool-helper-escape-review.json', 'native-pool-exhaustion-contract.json',
             'native-pool-interior-review.json', 'native-pool-reference-inventory.json',
             'native-pool-run-r10/probe-result.json']
    paths = [folder / n for n in names]
    layout, callers, escapes, exhaustion, interior, inventory, runtime = [
        json.loads(p.read_text(encoding='utf-8-sig')) for p in paths]
    data = source.read_bytes()
    if hashlib.sha256(data).hexdigest() != SHA:
        raise ValueError('Wrong offline original')
    pe = pefile.PE(data=data)
    base = pe.OPTIONAL_HEADER.ImageBase
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True

    def raw(i):
        return dict(address=hex(i.address), bytes=i.bytes.hex(), mnemonic=i.mnemonic, operands=i.op_str)

    def span(a, z):
        instructions = list(md.disasm(pe.get_data(a, z-a), base+a))
        if sum(i.size for i in instructions) != z-a:
            raise ValueError('Incomplete retained instruction span')
        return [raw(i) for i in instructions]

    checked = set()

    def verify_rows(value):
        if isinstance(value, dict):
            if 'address' in value and 'bytes' in value:
                address = int(value['address'], 16)
                blob = bytes.fromhex(value['bytes'])
                if pe.get_data(address-base, len(blob)) != blob:
                    raise ValueError('Retained original byte mismatch: '+value['address'])
                checked.add((address, blob))
            for child in value.values():
                verify_rows(child)
        elif isinstance(value, list):
            for child in value:
                verify_rows(child)

    secondary = [r for r in callers['entries'] if r.get('missionOffset') == '0x62da50']
    allocations = [r for r in callers['entries'] if r['target'] in ('0x1805d8020', '0x1805d91f0')]
    assert len(secondary) == 4
    assert {(r['address'], r['missionOffset']) for r in allocations} == {
        ('0x1805cd171', '0x2d50'), ('0x1805cd195', '0x2d50')}
    verify_rows(secondary)
    verify_rows(allocations)
    verify_rows(escapes['completeConstructorAndHelperInstructions'])
    helper_readers = [r for r in interior['instructionSiteMappings']
                      if r['address'] in ('0x1805c86c4', '0x18059ea00')]
    assert len(helper_readers) == 2
    verify_rows(helper_readers)

    # Whole-file exact DWORD searches, not only Ghidra-recognized instructions.
    literals = []
    for value in (0x62da50, 0xc57750, 0xc58740, 0xc58744, 0x62ad00):
        locations = []
        at = 0
        while True:
            at = data.find(struct.pack('<I', value), at)
            if at < 0:
                break
            locations.append(dict(fileOffset=at, rva=hex(pe.get_rva_from_offset(at))))
            at += 1
        literals.append(dict(value=hex(value), occurrences=locations))
    assert [r['rva'] for r in literals[0]['occurrences']] == [
        '0x5ce479', '0x5cf326', '0xa2338a', '0xa239da']
    assert all(not r['occurrences'] for r in literals[1:])

    # Recheck every-byte rel32 CALL/JMP targets for both activation helpers.
    branches = []
    for section in pe.sections:
        if not section.Characteristics & 0x20000000:
            continue
        blob = section.get_data()
        for opcode in (0xe8, 0xe9):
            at = 0
            while True:
                at = blob.find(bytes([opcode]), at)
                if at < 0:
                    break
                if at+5 <= len(blob):
                    address = base+section.VirtualAddress+at
                    target = address+5+struct.unpack_from('<i', blob, at+1)[0]
                    if target in (base+0x5d8020, base+0x5d91f0):
                        i = next(md.disasm(blob[at:at+15], address))
                        branches.append(dict(raw(i), target=hex(target)))
                at += 1
    assert {r['address'] for r in branches} == {'0x1805cd171', '0x1805cd195'}

    bodies = {
        'poolConstructor': span(0x5d8220, 0x5d82aa),
        'secondaryFinalDestructor': span(0x5cf323, 0x5cf353),
        'createSelectorAndPrimaryActivation': span(0x5cd130, 0x5cd1b5),
        'deletePrimaryBase': span(0x5d00a0, 0x5d00f4),
        'globalPrimaryIteratorBase': span(0x59e522, 0x59e55b),
        'clearPrimaryIteratorBase': span(0x5cfc0c, 0x5cfc1a),
    }
    verify_rows(bodies)
    assert runtime['exhaustionVerified'] is True
    assert runtime['exhaustionPoolCounts'] == [4080, 0, 4080, 4080, 4080]
    source_paths = [source, Path(__file__)] + paths
    result = dict(
        schemaVersion=1, status='bounded-secondary-pool-lifecycle-only-evidence',
        nativeSourceSha256=SHA, gameExecuted=False, installedFilesModified=False,
        sources={str(p): digest(p) for p in source_paths},
        originalPool=layout['pools'][1],
        conclusion='The observed second c60 pool is fully constructed storage with no active slots at the reviewed Single0 infantry snapshot. Identified direct native references are lifecycle-only. No supported gameplay allocator or selector for it was found. Reserved/legacy/dead-code status and universal unreachability are not established.',
        lifecycleReferences=secondary, activationHelperCallers=allocations,
        freshDirectBranchCandidates=branches, exactDwordLiteralSearch=literals,
        constructorEffects=[
            '5d8259 invokes the EH constructor iterator with stride c60, count2040 and Agent constructor606a20; the same constructor is used for both pools.',
            '5d8270 sets every slot index at element+10 to -1; 5d8290 fills the signed-short next-index table with -1; 5d8295 zeroes live count and high-water together.',
            'The retained complete default Agent constructor/helper review finds no publication and initializes Agent+ab8 owner to zero.',
            'Final Mission destruction resets secondary pool via5d7df0, then destructs every slot via6070c0 using the EH iterator; both associated exception cleanups route through5d81e0.'
        ],
        selectorFinding='CreateAgent param9 chooses allocator (-1) versus explicit index. Both paths derive pool=Mission+2d50 before the branch. It is not a primary/secondary-pool selector.',
        sharedReaderProofs=helper_readers,
        defaultConstructionEvidence=escapes['retainedConstructorEvidence'],
        defaultConstructionReview=escapes['defaultConstructionReview'],
        retainedActivationSemantics={k:exhaustion['retainedSemanticBodies'][k]
                                     for k in ('0x1805cd130','0x1805d91f0')},
        rawProofs=bodies,
        runtimeObservation=dict(source=str(paths[-1]), runId=runtime['runId'],
            counts=runtime['exhaustionPoolCounts'], phase='precombat full primary allocator exhaustion',
            interpretation='Secondary count was zero at this captured boundary. This is not a continuous watch of its count throughout combat or teardown.'),
        verification=dict(originalInstructionOrMetadataRowsChecked=len(checked),
            activationHelperNearBranchCount=len(branches), secondaryBaseLiteralCount=4,
            secondIndexTableCountHighWaterAndPoolDeltaLiteralCount=0),
        unresolved=[
            'No original source symbol names identify the intended purpose. Calling this networking, rollback, cavalry or corpse storage would be a guess.',
            'Absence of exact constants/direct helper callers does not exclude split arithmetic, interior helper entrypoints, pointer transport, unresolved computed code or other native modules.',
            'No secondary-pool activation/exhaustion was exercised. Artificially calling the generic allocator on this empty pool would create an incompletely registered Agent and is not an authorized or safe role-discovery test.',
            'The r10 observation covers the isolated infantry profile only; mounted/ranged/long-duration and alternative modes require separate observations.'
        ],
        nextSafeObservation='During authorized workload probes, capture the second live-count/high-water and negative-header invariant at existing stage boundaries without mutating it. If it becomes nonzero, preserve the first transition and identify its writer before assigning a semantic role.'
    )
    output = folder/'native-secondary-pool-role-review.json'
    safe_review_output(output, source_paths).write_text(json.dumps(result, indent=2)+'\n')
    print(json.dumps(result['verification']))
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=SOURCE)
    build(parser.parse_args().source)
