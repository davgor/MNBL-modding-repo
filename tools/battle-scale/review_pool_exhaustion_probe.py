"""Independent source/PE correspondence for the bounded full-pool probe."""
from pathlib import Path
import hashlib,json,re,sys
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import pefile
from patch_native_copy import safe_review_output

def build():
    folder=ROOT/'evidence/engine-extension'
    source=ROOT/'src/MNBL.BattleScale/NativePoolProbeMission.cs'
    audit=folder/'native-pool-exhaustion-contract.json'
    manifest_path=folder/'native-pool-patch-4080.json'
    manifest=json.loads(manifest_path.read_text())
    text=source.read_text()
    native=Path(manifest['sourcePath']);data=native.read_bytes()
    if hashlib.sha256(data).hexdigest()!=manifest['sourceSha256']:raise ValueError('Source mismatch')
    pe=pefile.PE(data=data)
    code=bytearray(data)
    for patch in manifest['patches']:
        off=patch['fileOffset'];old=bytes.fromhex(patch['original']);new=bytes.fromhex(patch['replacement'])
        if data[off:off+len(old)]!=old:raise ValueError('Patch mismatch')
        code[off:off+len(old)]=new
    declared=bytes.fromhex(re.search(r'FullAllocatorPathHex\s*=\s*"([0-9a-f]+)"',text).group(1))
    at=pe.get_offset_from_rva(0x5d8020)
    if len(declared)!=57 or bytes(code[at:at+57])!=declared:raise ValueError('Probe full path is not the exact57 reviewed bytes')
    offsets=[int(x.strip(),16) for x in re.search(r'poolIndexTables\s*=\s*\{([^}]+)\}',text).group(1).split(',')]
    expected=[int(p['indexTableOffset'],16) for p in manifest['layout']['pools']]
    if offsets!=expected:raise ValueError('Wrong snapshot offsets')
    for pool in manifest['layout']['pools']:
        table=int(pool['indexTableOffset'],16)
        if (int(pool['counterOffset'],16)!=table+4080*2 or
                int(pool['highWaterOffset'],16)!=table+4080*2+4):
            raise ValueError('Metadata snapshot geometry differs')
    required=['[UnmanagedFunctionPointer(CallingConvention.Winapi)]','private delegate int FullPrimaryPoolAllocator(IntPtr pool);',
              'IntPtr.Add(nativeBase, 0x5d8020)','allocate(IntPtr.Add(mission, 0x2d50))',
              '0x2d50 + i * 0xc60 + 0x10','if (header < 0)',
              'exhaustionReturnValue != -1 || !before.SequenceEqual(after)',
              'if (agents.Count == Capacity) return false;',
              'publicSpawnAgentOverflowTested = false']
    if any(s not in text for s in required):raise ValueError('Reviewed source contract differs')
    result={'status':'accepted-bounded-full-helper-test-source-review','gameExecuted':False,
        'sourceSha256':manifest['sourceSha256'],'sources':{
            p.name:{'path':str(p),'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in (source,audit,manifest_path,Path(__file__))},
        'exactNativePathBytes':declared.hex(),'pathByteCount':len(declared),
        'tableOffsets':[hex(v) for v in offsets],'snapshotBytes':5*(4080*2+8)+4080*4,
        'findings':[
            'Winapi int(IntPtr) maps to Windowsx64 RCX argument and signed EAX result, matching native helper.',
            'Loaded57-byte path is checked immediately before invoking the hard-coded module-relative address; these bytes include all branches taken when every header is occupied.',
            'The whole five next-index tables plus contiguous count/high-water fields and all4080 primary signed index headers are compared before/after.',
            'All headers must be nonnegative; all four active pool counts must equal4080 and secondc60 count must be0; full managed population is checked before and after.',
            'Invocation occurs only once in controlled precombat Single0 owner-thread scope. Report between snapshot and invocation performs managed state reporting/file output, no explicit yield or native allocation callback.',
            'Public spawn is never called at capacity; the managed admission guard returnsfalse. This does not repair the native public overflow path.',
        ],'limitations':[
            'Concurrency exclusion is the controlled precombat/main-thread profile accepted by the parent, not a newly acquired native lock.',
            'Source review and byte correspondence do not establish runtime success, final resource release, or exception rollback.',
            'Snapshot covers allocation metadata and primary headers, not every payload byte or unrelated engine memory.',
        ]}
    dest=safe_review_output(folder/'native-pool-exhaustion-probe-review.json',[native,source,audit,manifest_path,Path(__file__)])
    dest.write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps({'status':result['status'],'pathByteCount':len(declared),'snapshotBytes':result['snapshotBytes']}))

if __name__=='__main__':build()
