"""Byte-pinned supplemental switch proofs; does not alter canonical coverage."""
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


def build():
    source = ROOT / 'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
    canonical = ROOT / 'evidence/engine-extension/native-decode-gap-r8-review.json'
    raw = source.read_bytes()
    assert hashlib.sha256(raw).hexdigest() == '2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
    pe = pefile.PE(data=raw)
    base = pe.OPTIONAL_HEADER.ImageBase
    assert base == 0x180000000
    decoder = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)

    def pin(rva, expected):
        assert pe.get_data(rva, len(bytes.fromhex(expected))).hex() == expected, hex(rva)

    def rows(start, end):
        instructions = list(decoder.disasm(pe.get_data(start, end-start), base+start))
        assert sum(i.size for i in instructions) == end-start
        return [dict(address=hex(i.address), instructionBytes=i.bytes.hex(), mnemonic=i.mnemonic,
                     operands=i.op_str) for i in instructions]

    pin(0x45a44, '4533e4')  # R12D=0, Win64 nonvolatile across intervening calls.
    pin(0x45ab7, '418bc4448925f3d7cb00eb28')
    pin(0x45acc, 'b801000000eb12')
    pin(0x45ae0, 'b8070000008905c9d7cb00')
    pin(0x45aeb, '4898488d150ca5fbff8b8c82ec6404004803caffe1')
    pin(0x6832f, '4863513083fa080f87650400004c8d05bd7cf9ff418b8c90c08706004903c8ffe1')
    pin(0x687ad, '4883c468415f415e415d415c5f5e5b5dc36690')
    first = list(struct.unpack('<9I', pe.get_data(0x464ec, 36)))
    second = list(struct.unpack('<9I', pe.get_data(0x687c0, 36)))
    assert first == [0x45b00, 0x45b09, 0x45b12, 0x45b1b, 0x45b24, 0x45b2d, 0x45b36, 0x45b3f, 0x45b48]
    assert second == [0x687ad, 0x68350, 0x68508, 0x68617, 0x685b4, 0x68654, 0x68735, 0x68762, 0x68770]
    assert 0x464ec + 7*4 == 0x46508
    approved = [0x46508, 0x687c6, 0x687d6, 0x687e2, 0x687e3]
    current = json.loads(canonical.read_text(encoding='utf-8-sig'))
    gaps = {int(x['rva'], 16): x for x in current['bytes']}
    per_byte = []
    for rva in approved:
        gap = gaps[rva]
        assert gap['classification'] == 'unresolved-runtime-range-byte'
        assert not gap['patchedGroup']
        assert int(gap['byte'], 16) == pe.get_data(rva, 1)[0]
        table = 0x464ec if rva == 0x46508 else 0x687c0
        index = (rva-table)//4
        target = (first if table == 0x464ec else second)[index]
        assert target < table
        per_byte.append(dict(rva=hex(rva), byte=hex(pe.get_data(rva, 1)[0]),
                             tableRva=hex(table), entryIndex=index, byteWithinEntry=(rva-table)%4,
                             targetRva=hex(target), classification='bounded-dispatch-table-data'))

    # Explicit signed/unsigned edge checks for MOVSXD followed by CMP EDX/JA.
    for state in [-0x80000000, -1, 0, 1, 8, 9, 0x7fffffff]:
        accepted = (state & 0xffffffff) <= 8
        assert accepted == (0 <= state <= 8)

    proofs = [
        dict(procedure='0x1800459f0', dispatch='0x180045afe', tableRva='0x464ec',
             actualLocalIndexDomain=[0, 1, 7], observedTableEntries=[hex(x) for x in first],
             proof='All local predecessor paths into45aeb select EAX=R12D(0), literal1, or literal7. R12D was zeroed45a44 and is nonvolatile across calls. CDQE preserves these nonnegative values. RDX is image base; the DWORD RVA load and ADD produce the case address. Requested byte46508 belongs to actually selectable entry7, whose target45b3f is independently decoded code.',
             instructions=rows(0x459f0, 0x45b58), tail=rows(0x464a0, 0x464ec),
             tableBytes=pe.get_data(0x464ec,36).hex(),
             scope='Only the requested byte46508 is approved here. Nine observed DWORDs do not assert that every observed entry is reachable.'),
        dict(procedure='0x180068310', dispatch='0x18006834e', tableRva='0x687c0',
             actualLocalIndexDomain=list(range(9)), observedTableEntries=[hex(x) for x in second],
             proof='MOVSXD reads stateDWORD intoRDX. CMP EDX,8 followed by unsignedJA rejects negative and greater-than8 states. R8 is image base. The DWORD RVA load indexed byRDX*4 reads exactly9 entries; ADD R8 forms a case address. Every case target is before the table. RET687bd plus padding prevents ordinary fallthrough into687c0.',
             instructions=rows(0x68310, 0x68350), tail=rows(0x68760,0x687c0),
             caseEntries=[rows(target, target+next(decoder.disasm(pe.get_data(target,15),base+target)).size)[0] for target in second],
             tableBytes=pe.get_data(0x687c0,36).hex())]
    # Three largest remaining groups have the same explicit unsigned DWORD
    # bound; preserve each distinct register/argument geometry rather than
    # authorize arbitrary similarly-shaped displacements.
    additional = [
        dict(procedure=0x175ec0, start=0x175f07, end=0x175f2b, table=0x177108,
             bound=46, tail=0x177085,
             pin='4863919800000083fa2e0f876e1100004c8d05e2a0e8ff418b8c90087117004903c8ffe1',
             proof='MOVSXD RDX from command+98; CMP EDX,46 and unsignedJA reject every state outside0..46. R8 is image base; loadDWORD[base+RDX*4+177108], ADDbase, JMPRCX selects47 caseRVAs. Out-of-line blocks after1770c0 return by explicit backward jumps; the final block jumps176c35 at1770ff before INT3/padding and table.'),
        dict(procedure=0x4bc030, start=0x4bc089, end=0x4bc0b1, table=0x4bc684,
             bound=74, tail=0x4bc64e,
             pin='83f94a0f87c40500004863c14c8d05643fb4ff4889bc2490000000418b8c8084c64b004903c8ffe1',
             proof='IncomingECX is unchanged on all earlier local paths. CMP ECX,74 and unsignedJA reject negative or greater states before MOVSXD RAX,ECX. R8 is image base; DWORD[base+RAX*4+4bc684] selects75 caseRVAs. RET4bc682 andNOP precede table.'),
        dict(procedure=0x174d80, start=0x174dae, end=0x174dd2, table=0x174fe4,
             bound=13, tail=0x174fbb,
             pin='48638424c800000083f80d0f87fc010000488d153ab2e8ff8b8c82e44f17004803caffe1',
             proof='MOVSXD RAX reads caller stackDWORD; CMP EAX,13 and unsignedJA reject every state outside0..13. RDX is image base; DWORD[base+RAX*4+174fe4] selects14 caseRVAs. RET174fe0 andNOP precede table.'),
    ]
    for spec in additional:
        pin(spec['start'], spec['pin'])
        count = spec['bound'] + 1
        targets = list(struct.unpack('<'+'I'*count, pe.get_data(spec['table'],count*4)))
        assert all(spec['end'] <= target < spec['table'] for target in targets)
        covered = []
        for rva, gap in gaps.items():
            if gap['classification'] != 'unresolved-runtime-range-byte' or gap['canonicalProcedure'] != hex(base+spec['procedure']):
                continue
            assert not gap['patchedGroup']
            assert spec['table'] <= rva < spec['table']+count*4, hex(rva)
            assert pe.get_data(rva,1)[0] == int(gap['byte'],16)
            index = (rva-spec['table'])//4
            per_byte.append(dict(rva=hex(rva), byte=hex(pe.get_data(rva,1)[0]),
                                 tableRva=hex(spec['table']), entryIndex=index,
                                 byteWithinEntry=(rva-spec['table'])%4,
                                 targetRva=hex(targets[index]), classification='bounded-dispatch-table-data'))
            covered.append(hex(rva))
        proofs.append(dict(procedure=hex(base+spec['procedure']), tableRva=hex(spec['table']),
                           actualLocalIndexDomain=list(range(count)), proof=spec['proof'],
                           instructions=rows(spec['procedure'],spec['end']),
                           tail=rows(spec['tail'],spec['table']),
                           observedTableEntries=[hex(t) for t in targets],
                           caseEntries=[rows(t,t+next(decoder.disasm(pe.get_data(t,15),base+t)).size)[0] for t in targets],
                           tableBytes=pe.get_data(spec['table'],count*4).hex(), coveredByteRvas=covered))
        for state in [-0x80000000,-1,0,spec['bound'],spec['bound']+1,0x7fffffff]:
            assert ((state & 0xffffffff) <= spec['bound']) == (0 <= state <= spec['bound'])
    sources = {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in (source, canonical, Path(__file__))}
    return dict(schemaVersion=1, status='exact-gap-bytes-classified-by-five-bounded-switches',
                sources=sources, sourceSha256=hashlib.sha256(raw).hexdigest(),
                canonicalArtifactUnchanged=True, canonicalCountsRecomputed=False,
                perByteClassifications=per_byte, switchProofs=proofs,
                limitations=['This supplemental result does not mutate or replace the canonical401-open-byte review.',
                             'Local dispatch bounds classify only the enumerated exact bytes as table data; arbitrary external or computed jumps are not proven absent.',
                             'No game execution, attachment, deployment, native installed-file access, or patch-manifest change.'])


if __name__ == '__main__':
    result = build()
    output = ROOT / 'evidence/engine-extension/native-gap-switch-supplemental-review.json'
    safe_review_output(output, list(result['sources']))
    output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(result['status'])
