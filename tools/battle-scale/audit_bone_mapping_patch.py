"""Build a separate review-only renderer patch; never writes a native DLL."""
from pathlib import Path
import hashlib
import json
import struct
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / 'build/battle-scale/python-libs'), str(Path(__file__).parent)]
import capstone
import pefile
from patch_native_copy import safe_review_output

MAPPING_GUARD = bytes.fromhex('4183ff2072020f0b4d39fe777d4a8d9c35080100004d2bfe4c8d6cc50849ffc79090')
MATRIX_GUARD = bytes.fromhex('8d1c3783fb1f76020f0b488d9c31080100004c8d74c10848ffc790')
SHIFT_SITES = (0x886c8, 0x886d1, 0x88778, 0x8877b, 0x88792, 0x887db,
               0x887e2, 0x1e09e1, 0x1e09e6)


def build():
    source = ROOT / 'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
    bundle = ROOT / 'evidence/engine-extension/native-renderer-stall-ghidra-evidence.json'
    data = source.read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    assert digest == '2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
    pe = pefile.PE(data=data)
    base = pe.OPTIONAL_HEADER.ImageBase
    decoder = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    decoder.detail = True
    patches = []

    def add(rva, replacement, purpose):
        original = pe.get_data(rva, len(replacement))
        assert len(original) == len(replacement) and original != replacement
        before = list(decoder.disasm(original, base+rva))
        after = list(decoder.disasm(replacement, base+rva))
        assert sum(i.size for i in before) == len(original)
        assert sum(i.size for i in after) == len(replacement)
        patches.append(dict(rva=hex(rva), fileOffset=pe.get_offset_from_rva(rva),
                            originalBytes=original.hex(), replacementBytes=replacement.hex(),
                            purpose=purpose, originalInstructions=rows(before), candidateInstructions=rows(after)))

    def rows(decoded):
        return [dict(address=hex(i.address), bytes=i.bytes.hex(), mnemonic=i.mnemonic, operands=i.op_str)
                for i in decoded]

    for rva in SHIFT_SITES:
        ins = next(decoder.disasm(pe.get_data(rva, 15), base+rva))
        assert ins.mnemonic in ('shr', 'shl') and ins.operands[-1].imm == 11
        assert ins.imm_size == 1
        changed = bytearray(ins.bytes)
        changed[ins.imm_offset] = 14
        add(rva, bytes(changed), 'Bone-mapping page geometry: 2048 -> 16384 DWORD entries.')
    for rva in (0x8870a, 0x1e0b34):
        ins = next(decoder.disasm(pe.get_data(rva, 15), base+rva))
        assert ins.mnemonic == 'mov' and ins.operands[-1].imm == 0x2000 and ins.imm_size == 4
        changed = bytearray(ins.bytes)
        changed[ins.imm_offset:ins.imm_offset+4] = struct.pack('<I', 0x10000)
        add(rva, bytes(changed), 'Bone-mapping allocation/upload page bytes: 8192 -> 65536.')
    add(0x886d5, MAPPING_GUARD, 'Reject last mapping page >=32 with UD2 before any page-table access; retain original first>last skip.')
    add(0x89925, MATRIX_GUARD, 'Reject last matrix page >=32 with UD2 before any page-table access; retain matrix page size and prior first>last skip.')
    patches.sort(key=lambda x: int(x['rva'], 16))
    for left, right in zip(patches, patches[1:]):
        assert int(left['rva'], 16)+len(bytes.fromhex(left['originalBytes'])) <= int(right['rva'], 16)

    windows = [(0x886d5, 0x886f7), (0x89925, 0x89940)]
    candidates = []
    rip_refs = []
    for sec in pe.sections:
        if not sec.Characteristics & 0x20000000:
            continue
        blob = sec.get_data()
        for pos, opcode in enumerate(blob):
            size = 0
            target = None
            if opcode in (0xe8, 0xe9) and pos+5 <= len(blob):
                size = 5
                target = sec.VirtualAddress+pos+size+struct.unpack_from('<i', blob, pos+1)[0]
            elif (0x70 <= opcode <= 0x7f or opcode in (0xeb, 0xe0, 0xe1, 0xe2, 0xe3)) and pos+2 <= len(blob):
                size = 2
                target = sec.VirtualAddress+pos+size+struct.unpack_from('<b', blob, pos+1)[0]
            elif opcode == 0x0f and pos+6 <= len(blob) and 0x80 <= blob[pos+1] <= 0x8f:
                size = 6
                target = sec.VirtualAddress+pos+size+struct.unpack_from('<i', blob, pos+2)[0]
            if target is not None and any(start <= target < end for start, end in windows):
                candidates.append(dict(sourceRva=hex(sec.VirtualAddress+pos), targetRva=hex(target),
                                       bytes=blob[pos:pos+size].hex()))
            # A RIP-relative LEA address materialization has no trailing immediate.
            if opcode == 0x8d and pos+6 <= len(blob) and blob[pos+1] & 0xc7 == 5:
                target = sec.VirtualAddress+pos+6+struct.unpack_from('<i', blob, pos+2)[0]
                if any(start <= target < end for start, end in windows):
                    rip_refs.append(dict(opcodeRva=hex(sec.VirtualAddress+pos), targetRva=hex(target)))
    # Every-byte scanning also treats ModRM/SIB/displacement bytes as opcodes.
    # Exclude only candidates contained inside a fully decoded original procedure.
    known = []
    for start, end in ((0x88440, 0x88814), (0x898d0, 0x899c8)):
        body = list(decoder.disasm(pe.get_data(start, end-start), base+start))
        assert sum(i.size for i in body) == end-start
        known.extend(body)
    for item in candidates:
        address = base+int(item['sourceRva'], 16)
        parent = next((i for i in known if i.address < address < i.address+i.size), None)
        assert parent is not None, item
        item['classification'] = 'non-boundary bytes inside original instruction'
        item['containingInstruction'] = rows([parent])[0]
    assert not rip_refs, rip_refs
    literals = []
    for start, end in windows:
        for target in range(start, end):
            for label, needle in [('rva32', struct.pack('<I', target)), ('va64', struct.pack('<Q', base+target))]:
                pos = data.find(needle)
                while pos >= 0:
                    literals.append(dict(kind=label, targetRva=hex(target), fileOffset=hex(pos)))
                    pos = data.find(needle, pos+1)
    for item in literals:
        literal_rva = pe.get_rva_from_offset(int(item['fileOffset'], 16))
        fragment = next(e for e in pe.DIRECTORY_ENTRY_EXCEPTION
                        if e.struct.BeginAddress <= literal_rva < e.struct.EndAddress)
        body = list(decoder.disasm(pe.get_data(fragment.struct.BeginAddress,
                                 fragment.struct.EndAddress-fragment.struct.BeginAddress),
                                 base+fragment.struct.BeginAddress))
        assert sum(i.size for i in body) == fragment.struct.EndAddress-fragment.struct.BeginAddress
        parent = next((i for i in body if i.address <= base+literal_rva and
                       base+literal_rva+4 <= i.address+i.size), None)
        assert item['kind'] == 'rva32' and parent is not None
        assert parent.mnemonic == 'call' and parent.bytes[0] == 0xe8
        assert parent.operands[0].imm != base+int(item['targetRva'], 16)
        item['classification'] = 'opcode/relative displacement bytes of an unrelated direct CALL'
        item['containingInstruction'] = rows([parent])[0]
    relocations = [hex(e.rva) for block in getattr(pe, 'DIRECTORY_ENTRY_BASERELOC', []) for e in block.entries
                   if e.type and any(start <= e.rva < end or e.rva < start < e.rva+8 for start, end in windows)]
    assert not relocations, relocations
    unwind = []
    for start, end in windows:
        fragment = next(e for e in pe.DIRECTORY_ENTRY_EXCEPTION if e.struct.BeginAddress <= start < e.struct.EndAddress)
        assert end <= fragment.struct.EndAddress
        assert start >= fragment.struct.BeginAddress+fragment.unwindinfo.SizeOfProlog
        assert not any(start <= e.struct.BeginAddress < end for e in pe.DIRECTORY_ENTRY_EXCEPTION)
        chain = []
        current = fragment
        while True:
            chain.append(dict(begin=hex(current.struct.BeginAddress), end=hex(current.struct.EndAddress),
                              unwindRva=hex(current.struct.UnwindData), decoded=current.unwindinfo.dump()))
            if not current.unwindinfo.UNW_FLAG_CHAININFO:
                break
            entry = current.unwindinfo.FunctionEntry
            current = next(e for e in pe.DIRECTORY_ENTRY_EXCEPTION if e.struct.BeginAddress == entry)
        unwind.append(dict(window=[hex(start), hex(end)], chain=chain,
                           proof='All original save instructions/prolog positions remain untouched; neither replacement changes RSP or saved registers in memory.'))
    records = json.loads(bundle.read_text(encoding='utf-8-sig'))['records']
    wanted = {'0x180088440', '0x1800898d0', '0x180175ec0', '0x1801e0270'}
    evidence = [r for r in records if r.get('operation') == 'procedure_pseudo_code' and r.get('parameters', {}).get('procedure') in wanted]
    assert len({r['parameters']['procedure'] for r in evidence}) == 4
    command = next(r['normalized_result'] for r in evidence if r['parameters']['procedure'] == '0x180175ec0')
    case = command[command.index('case 0xc:'):command.index('case 0xd:')]
    assert '0xb0' in case and '0xb8' in case and 'memcpy' in case
    upload = rows(list(decoder.disasm(pe.get_data(0x176770, 0x17685f-0x176770), base+0x176770)))
    sources = [source, bundle, Path(__file__)]
    return dict(status='concrete-review-only; independent-machine-tests-and-integration-required',
                sourceSha256=digest, sources={str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in sources},
                edits=patches, editCount=len(patches), mappingCapacityEntries=524288, matrixCapacityEntries=262144,
                mappingPageBytes=65536, matrixPageBytes=524288, headerLayoutChanged=False,
                failureSemantics='UD2 is an intentional abnormal termination, never a successful test result. Counter reservation precedes the guard; no out-of-range page publication follows it.',
                guardLimits='Bounds the page-table accesses for ordinary positive reservations and preserves existing zero/first>last branches. Does not certify malformed render objects, allocator failure, or arbitrary injected code.',
                incomingControlFlow=dict(scan='Every byte of all executable sections for relative CALL/JMP/Jcc/loop and RIP-relative LEA; exact RVA32/VA64 literals over entire image.',
                                         directCandidates=candidates, ripLeaCandidates=rip_refs, addressLiterals=literals,
                                         relocationOverlaps=relocations, limitation='Opaque synthesized indirect destinations cannot be disproven by an absence scan.'),
                unwind=unwind, evidenceIds=[r['evidence_id'] for r in evidence],
                commandType12=dict(evidence=case, instructions=upload,
                                   proof='Consumer reads pageBytes from command+B0, totalBytes+B8, copies min(remaining,pageBytes), advances page pointer by8; no embedded2048 capacity. Layout/lifetime ownership unchanged.'),
                unchanged=dict(zeroCountBypass='886a0 TEST R12D; 886a3 JNE886af; else ESI=-1; JMP88769',
                               mappingLoopStart='0x886f7', mappingSkip='0x8875f', matrixLoopStart='0x89940',
                               ctorDtor='32 pointer slots and128-byte header unchanged; shared constructor/destructor remain intact.'))


if __name__ == '__main__':
    result = build()
    output = ROOT / 'evidence/engine-extension/native-bone-mapping-patch.json'
    safe_review_output(output, list(result['sources']))
    output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps({'status': result['status'], 'edits': result['editCount']}))
