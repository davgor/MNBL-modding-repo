"""Offline correlation of the observed spin with the bone-mapping page layout.

This is evidence, not an executable patch or a claim of exhaustive consumers.
"""
from pathlib import Path
import hashlib
import json
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / 'build/battle-scale/python-libs'), str(Path(__file__).parent)]
import capstone
import pefile
from patch_native_copy import safe_review_output


def build():
    source = ROOT / 'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
    memory = ROOT / 'build/battle-scale/native-run-20261017-070802-dd8b5783/native-object-memory-46760.json'
    data = source.read_bytes()
    assert hashlib.sha256(data).hexdigest() == '2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
    pe = pefile.PE(data=data)
    decoder = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    spans = {'globalConstruction': (0x4ca4f, 0x4ca76),
             'pairConstruction': (0xe65fc, 0xe66a5),
             'mappingWriter': (0x88440, 0x88814),
             'matrixReservation': (0x898d0, 0x899c8),
             'mappingGpuUpload': (0x1e0998, 0x1e0be8),
             'commandConstructor': (0x61840, 0x61968),
             'commandExecutionThunk': (0x61830, 0x61839)}
    raw = {}
    by_address = {}
    for name, (start, end) in spans.items():
        rows = list(decoder.disasm(pe.get_data(start, end-start), 0x180000000+start))
        assert sum(i.size for i in rows) == end-start, name
        raw[name] = [dict(address=hex(i.address), bytes=i.bytes.hex(), mnemonic=i.mnemonic,
                          operands=i.op_str) for i in rows]
        by_address.update({i.address-0x180000000: i for i in rows})
    shift_sites = [0x886c8, 0x886d1, 0x88778, 0x8877b, 0x88792, 0x887db,
                   0x887e2, 0x1e09e1, 0x1e09e6]
    for site in shift_sites:
        assert by_address[site].op_str.endswith(', 0xb'), hex(site)
    assert by_address[0x8870a].op_str == 'edx, 0x2000'
    assert by_address[0x1e0b34].op_str.endswith(', 0x2000')
    assert pe.get_data(0xaf32f8, 27) == b'global_bone_mapping_buffer\x00'
    captured = json.loads(memory.read_text(encoding='utf-8-sig'))
    selected = captured['regions'][0]
    pair_base = captured['regions'][1]
    snapshot = bytes.fromhex(selected['hex'])
    assert int(selected['address'], 16)-int(pair_base['address'], 16) == 0x128
    assert snapshot[0x109] == 0x40
    sources = [source, memory, Path(__file__)]
    return {
        'status': 'observed-spin-layout-correlated; consumer-closure-and-patch-incomplete',
        'nativeExecuted': False,
        'sources': {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in sources},
        'runtimeIdentity': captured['identity'],
        'snapshotAtomic': captured['atomic'],
        'owner': {'globalRva': '0xd96d18', 'allocationBytes': '0xf910',
                  'constructorRva': '0xe6580', 'mappingPairOffset': '0x9d0',
                  'elementBytes': '0x128', 'elementCount': 2, 'selectorOffsetInPair': '0x250',
                  'matrixPairOffset': '0xc28', 'debugResourceName': 'global_bone_mapping_buffer'},
        'originalGeometry': {'pageCount': 32, 'elementsPerPage': 2048, 'elementBytes': 4,
                             'totalElements': 65536, 'pagePointerOffset': 8,
                             'readyFlagOffset': '0x108', 'pageBytes': '0x2000'},
        'reservation': {
            'countSource': '884c1 MOVSX RBX, byte [RDI+44]; 884d0 R12=RBX',
            'atomicCounter': '886bc LOCK XADD DWORD [RBP], ESI',
            'firstPage': 'oldCount >> 11', 'lastPage': '(oldCount + count - 1) >> 11',
            'missingGuard': 'No page-index <32 or total-element capacity check before pointer CAS.',
            'firstInvalidPointerStore': 'page32 => RBP+8+32*8 = RBP+108, overlapping ready flags.',
            'spin': '88744/88747/88749 wait until byte [RBX] equals zero.'},
        'snapshot': {'selectedCount': int.from_bytes(snapshot[:4], 'little'),
                     'selectedPointerInFlagBytes': hex(int.from_bytes(snapshot[0x108:0x110], 'little')),
                     'waitedFlagOffset': '0x109', 'waitedFlagValue': snapshot[0x109],
                     'otherIndexBufferCount': pair_base['dwords']['0x0'],
                     'nextMatrixBufferCount': selected['dwords']['0x130'],
                     'interpretation': 'Pointer-shaped data in flags matches page32 overwrite. The offending earlier reservation itself was not captured. +130 is the next MATRIX pair, not the other mapping buffer.'},
        'pageGrowthSitesProven': {'pageShiftSites': [hex(x) for x in shift_sites],
                                  'pageByteSites': ['0x8870a', '0x1e0b34'],
                                  'allConsumersProven': False},
        'gpuUpload': {'roundedElementCount': 'ceil(count/2048)*2048', 'strideBytes': 4,
                      'commandType': 12, 'pagePointers': 'selectedContainer+8',
                      'pageBytesField': '0x2000', 'totalBytes': 'count*4',
                      'commandConstructor': '0x61840', 'execution': '0x61830 -> 0x175ec0'},
        'remaining': ['Prove every mapping-pair reader, copy, reset and destruction path.',
                      'Trace command type12 upload implementation.',
                      'Add an atomic overflow guard before any out-of-range publication; page growth alone is not an unbounded proof.',
                      'Validate replacement on a new isolated runtime test; this receipt does not authorize deployment.'],
        'instructions': raw,
    }


if __name__ == '__main__':
    result = build()
    output = ROOT / 'evidence/engine-extension/native-bone-mapping-stall-review.json'
    safe_review_output(output, list(result['sources']))
    output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps({'status': result['status'], 'instructions': sum(map(len, result['instructions'].values()))}))
