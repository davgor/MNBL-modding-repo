"""Offline evidence and unapplied patch proposal for the r12 renderer page overflow."""
from pathlib import Path
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
RUN = ROOT / 'build/battle-scale/native-run-20261017-102915-64ab5481'
SNAPSHOT = RUN / 'native-threads-134367065574470802-50268.json'
MEMORY = RUN / 'paged-container-memory.json'
LIFECYCLE = ROOT / 'evidence/engine-extension/native-renderer-buffer-lifecycle-review.json'
FROZEN_MANIFEST = ROOT / 'build/battle-scale/native4080-test-r12-ranged/native-pool-manifest.json'
FROZEN_NATIVE = ROOT / 'build/battle-scale/native4080-test-r12-ranged/TaleWorlds.Native.dll'
RUNTIME = RUN / 'runtime-receipt.json'
OUTPUT = ROOT / 'evidence/engine-extension/native-ranged-stall-review.json'
PATCH_OUTPUT = ROOT / 'evidence/engine-extension/native-ranged-page-patch.json'
EXPECTED = '2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
BASE = 0x180000000
GUARD = bytes.fromhex('83fd7f76020f0b483bf576020f0b488d9c3708040000482bee4c8d7cc70848ffc5')
GUARD += b'\x90' * (0x29 - len(GUARD))


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run():
    assert sha(SOURCE) == EXPECTED
    pe = pefile.PE(data=SOURCE.read_bytes())
    assert pe.OPTIONAL_HEADER.ImageBase == BASE
    decoder = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    decoder.detail = True
    def rows(start, end):
        raw = pe.get_data(start, end-start)
        decoded = list(decoder.disasm(raw, BASE+start))
        assert sum(i.size for i in decoded) == len(raw), hex(start)
        return [{'address': hex(i.address), 'bytes': i.bytes.hex(),
                 'mnemonic': i.mnemonic, 'operands': i.op_str} for i in decoded]
    edits = []
    def patch(rva, old, new, reason):
        old, new = bytes.fromhex(old), bytes.fromhex(new)
        assert len(old) == len(new) and pe.get_data(rva, len(old)) == old, hex(rva)
        def decode(data):
            instructions=list(decoder.disasm(data,BASE+rva))
            assert sum(i.size for i in instructions)==len(data)
            return [{'address':hex(i.address),'bytes':i.bytes.hex(),'mnemonic':i.mnemonic,'operands':i.op_str} for i in instructions]
        edits.append({'address': hex(BASE+rva), 'rva': hex(rva),
                      'fileOffset': pe.get_offset_from_rva(rva), 'originalBytes': old.hex(),
                      'replacementBytes': new.hex(), 'purpose': reason,
                      'originalInstructions':decode(old),'candidateInstructions':decode(new)})
    for rva, old in [(0x23f608,'c1e80b'),(0x23f613,'48c1ed0b'),
                     (0x23f68c,'41c1e00b'),(0x23f6a6,'c1e80b'),(0x23f6ae,'c1e00b'),
                     (0x23f757,'c1ea0b'),(0x23f762,'c1e00b'),
                     (0x1e053a,'c1eb0b'),(0x1e053f,'c1e30b')]:
        patch(rva, old, old[:-2]+'0e', '2048 to 16384 QWORD elements per page; exact producer or uploader geometry')
    patch(0x23f656,'ba00400000','ba00000200','Allocate 16384 QWORD elements, 131072 bytes')
    patch(0x23f690,'458d8800080000','458d8800400000','Initialize every element in the enlarged page')
    patch(0x1e068d,'48c7858001000000400000','48c7858001000000000200','Upload descriptor page byte stride matches allocation')
    patch(0x23f610,'4103ec','4903ec','Compute inclusive reservation end in 64 bits from two zero-extended uint32 operands, preventing wraparound')
    patch(0x23f617,pe.get_data(0x23f617,0x29).hex(),GUARD.hex(),
          'Fail with UD2 before pointer/flag setup unless last page <=127 and first<=last; no stack or unwind changes')
    for rva in [0x87a35,0x87a41,0x87a4d,0x87a8d,0x87a95,
                0x87b3d,0x87b4d,0x87b5c,0x87b5f,0x87b70,0x87b7d,0x87bb9,0x87bbe,0x87bcf,0x87bd7,
                0x89a03,0x89a0b,0x89a8c,0x89aa6,0x89aae]:
        ins=next(decoder.disasm(pe.get_data(rva,15),BASE+rva,count=1))
        assert ins.mnemonic in ['shr','shl'] and ins.bytes[-1]==11
        patch(rva,ins.bytes.hex(),ins.bytes[:-1].hex()+'0e',
              'Shared current/previous-buffer copy or allocator page geometry:2048 to16384 QWORD elements')
    patch(0x89a58,'ba00400000','ba00000200','Shared allocator:16384 QWORD page allocation')
    patch(0x89a90,'458d8800080000','458d8800400000','Shared allocator:initialize16384 QWORD elements')
    patch(0x89a06,'4103ef','4903ef','Shared allocator:33-bit inclusive end from zero-extended EBP and R15D')
    patch(0x89a0f,'483bf50f87e2000000','4883fd7f76020f0b90',
          'Fail with UD2 unless 64-bit last page<=127 before unchanged chained-unwind boundary89a18 and saved registers')
    snap = json.loads(SNAPSHOT.read_text()); mem = json.loads(MEMORY.read_text())
    assert snap['pid'] == mem['identity']['pid'] == 48216
    assert snap['identity']['creationFileTime'] == mem['identity']['creationFileTime']
    b = bytes.fromhex(mem['dataHex']); address = int(mem['address'],16)
    waiters = []
    for t in snap['threads']:
        if t.get('rip',{}).get('rva') != '0x23f6d4': continue
        r = {k:int(v,16) for k,v in t['registers'].items()}
        assert r['rdi']==address and r['rsi']==128
        assert r['r15']==address+8+128*8 == address+0x408
        assert r['rbx']==address+0x408+128 == address+0x488
        assert r['rax']&255 == b[0x488] == 0x30
        waiters.append({'tid':t['tid'],'reservationStart':r['r12'],
                        'page':r['rsi'],'flagAddress':hex(r['rbx']),
                        'pagePointerAddress':hex(r['r15']),'frames':t['frames'][:5]})
    assert len(waiters)==6
    assert struct.unpack_from('<I',b,0)[0] == 0x40248
    assert struct.unpack_from('<Q',b,0x408)[0] == 0x3791142c000
    assert struct.unpack_from('<I',b,0x488)[0] == 0x40030
    spans = [(0x23f50a,0x23f53a),(0x23f5f6,0x23f796),
             (0xf8720,0xf8752),(0xf8260,0xf82b8),
             (0xe65fc,0xe6637),(0xf27c2,0xf27f3),
             (0x1e04f8,0x1e06b4),(0x899d0,0x89b12),
             (0x879f1,0x87c1a)]
    exact = [{'start':hex(BASE+s),'endExclusive':hex(BASE+e),'instructions':rows(s,e)} for s,e in spans]
    pd = next(e.struct for e in pe.DIRECTORY_ENTRY_EXCEPTION if e.struct.BeginAddress<=0x23f617<e.struct.EndAddress)
    assert pd.BeginAddress==0x23f437 and pd.EndAddress==0x23f876
    guard_decoded = list(decoder.disasm(GUARD,BASE+0x23f617))
    assert sum(i.size for i in guard_decoded)==0x29
    frozen=json.loads(FROZEN_MANIFEST.read_text()); runtime=json.loads(RUNTIME.read_text())
    assert runtime['sourceSha256']==EXPECTED and runtime['patchedSha256']==sha(FROZEN_NATIVE)
    assert runtime['manifestSha256']==sha(FROZEN_MANIFEST)
    previous=frozen['patches']; assert len(previous)==1676
    prior_bytes=FROZEN_NATIVE.read_bytes()
    for edit in edits:
        begin=edit['fileOffset'];end=begin+len(bytes.fromhex(edit['originalBytes']))
        assert prior_bytes[begin:end].hex()==edit['originalBytes']
        for old in previous:
            old_begin=old['fileOffset'];old_begin=int(old_begin,0) if isinstance(old_begin,str) else old_begin
            old_end=old_begin+len(bytes.fromhex(old['original']))
            assert end<=old_begin or begin>=old_end
    result = {
      'schemaVersion':1,'status':'confirmed-overflow-unapplied-patch-proposal-pending-independent-closure',
      'nativeSourceSha256':EXPECTED,'gameExecutedByAudit':False,'binaryWritten':False,
      'sourceHashes':{str(p.relative_to(ROOT)):sha(p) for p in [SOURCE,SNAPSHOT,MEMORY,LIFECYCLE,FROZEN_MANIFEST,FROZEN_NATIVE,RUNTIME,Path(__file__)]},
      'priorPatchNonoverlap':{'priorEditCount':1676,'candidateEditCount':len(edits),'allNewSitesUnmodifiedInR12':True,
        'priorCandidateSha256':runtime['patchedSha256'],'r12RuntimeStatus':runtime['status']},
      'observed':{'pid':48216,'creationFileTime':mem['identity']['creationFileTime'],
        'container':hex(address),'selectedCount':0x40248,'otherCount':0x40030,'selector':struct.unpack_from('<I',b,0x910)[0],
        'page128PointerOverwritesFlags':hex(struct.unpack_from('<Q',b,0x408)[0]),'waiters':waiters,
        'conclusion':'Page128 pointer aliases first ready flags; page128 ready byte aliases next container count low byte0x30. Six workers wait on that byte. This is an out-of-bounds renderer page reservation, not an uncompleted legitimate page initializer.'},
      'layout':{'globalOwnerRva':'0xd96d18','ownerToAggregate':0xb8,'containerStride':0x488,
        'selectorOffset':0x910,'countOffset':0,'pointerTableOffset':8,'readyFlagsOffset':0x408,
        'pages':128,'oldElementsPerPage':2048,'candidateElementsPerPage':16384,'elementBytes':8,
        'oldCapacity':262144,'candidateCapacity':2097152},
      'contracts':[
        'Ctorf8720 zeros count and128 pointers and sets128 ready flags to1. Dtorf8260 frees128 pointers without page-size dependence.',
        'Owner+0xb8 pair construction and resetf27c2 preserve two 0x488 containers and selector+910. No header relocation is proposed.',
        'Producer23f5f9 reserves via locked32-bit xadd; count0 bypass23f529 remains unchanged.',
        'After xadd R12D holds uint32 old count. LEA EBP,[RBX-1] zero extends count-1. ADD RBP,R12 forms <=0x1fffffffe inclusive end without32-bit wrap.',
        'Guard rejects inclusive-end pages>=128 and reversed first/last before pointer or ready-flag address derivation. Invalid reservation increments the counter before failfast; it is not recoverable exhaustion.',
        'Uploader selects same+910/488 header and rounds count by page shift. Its descriptor stores page byte stride and pointer table+8; total payload is count*8.'
      ],
      'proposedEdits':sorted(edits,key=lambda e:int(e['rva'],16)),
      'guardInstructions':[{'address':hex(i.address),'bytes':i.bytes.hex(),'mnemonic':i.mnemonic,'operands':i.op_str} for i in guard_decoded],
      'guardUnwindFragment':{'start':hex(pd.BeginAddress),'endExclusive':hex(pd.EndAddress),'unwindRva':hex(pd.UnwindData),
        'scope':'No stack/nonvolatile-save change; fragment metadata unchanged. Incoming interior targets still require independent review.'},
      'exactSpans':exact,
      'remaining':[
        'Independent all-consumer/selector alias inventory is pending; numeric matching alone cannot prove completeness.',
        'Independent incoming-target and machine emulation review is pending.',
        'No patched binary or runtime validation was performed. Ranged r12 remains failed.',
        '16384QWORD/page is a bounded test capacity choice (8xstock), not an asset-demand worst-case proof. Lazy payload:16MiB maximum per container,32MiB both; captured262728 entries require17 enlarged pages (2.125MiB) versus129 old pages (2.015625MiB).',
        'Capacity is finite; failfast guard prevents silent metadata corruption but intentionally terminates an over-capacity run.',
        'Preserves existing upload/reset ordering; does not newly certify asynchronous backend lifetime.'
      ]}
    inputs=[SOURCE,SNAPSHOT,MEMORY,LIFECYCLE,FROZEN_MANIFEST,FROZEN_NATIVE,RUNTIME,Path(__file__)]
    safe_review_output(OUTPUT,inputs)
    OUTPUT.write_text(json.dumps(result,indent=2)+'\n')
    safe_review_output(PATCH_OUTPUT,inputs+[OUTPUT])
    PATCH_OUTPUT.write_text(json.dumps({'schemaVersion':1,'status':result['status'],
        'nativeSourceSha256':EXPECTED,'evidencePath':str(OUTPUT.relative_to(ROOT)),
        'evidenceSha256':sha(OUTPUT),'pageCount':128,'elementsPerPage':16384,'elementBytes':8,
        'capacity':2097152,'edits':result['proposedEdits'],'binaryWritten':False},indent=2)+'\n')
    print(json.dumps({'output':str(OUTPUT),'edits':len(edits),'waiters':len(waiters),'instructions':sum(len(x['instructions']) for x in exact)}))


if __name__=='__main__': run()



