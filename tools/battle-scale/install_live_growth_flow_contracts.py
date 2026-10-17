"""Exact offline contracts used only by the new live-growth owner analysis."""
from pathlib import Path
import hashlib,json,struct

def install(ns):
    root=ns['ROOT'];pe=ns['pe'];base=ns['base']
    path=root/'evidence/engine-extension/native-recursive-register-preservation-review.json'
    proof=json.loads(path.read_text())
    assert proof['source']['sha256']==hashlib.sha256(pe.__data__).hexdigest()
    for name,expected in proof['reviewedSourceSha256'].items():
        assert hashlib.sha256((root/name).read_bytes()).hexdigest()==expected
    from audit_recursive_register_preservation import prove_body,BODY_HEX,UNWIND_HEX
    entry=ns['entries'][0x6089d0]
    assert (entry.BeginAddress,entry.EndAddress,entry.UnwindData)==(0x6089d0,0x608a59,0xca04b8)
    raw=pe.get_data(entry.BeginAddress,entry.EndAddress-entry.BeginAddress)
    unwind=pe.get_data(entry.UnwindData,len(bytes.fromhex(UNWIND_HEX)))
    assert raw.hex()==BODY_HEX and unwind.hex()==UNWIND_HEX
    current=prove_body(raw,unwind)
    assert current==proof['proof']
    ns['leaf_register_effects'][base+0x6089d0]={**current,
        'status':'exact-reviewed-finite-normal-return-recursion',
        'proof':proof['reasoning'],'reviewPath':str(path),'reviewSha256':hashlib.sha256(path.read_bytes()).hexdigest()}
    tables=[
        (0x6f29ef,0x6f29d0,0x6f33f4,'r15',
         '4183ff0f0f87cd040000498bc74c8d3d1cd690ff418b8c87f4336f004903cfffe1',
         '472c6f003a2d6f00f1296f00812a6f00952a6f00bd2a6f00c62a6f002f2c6f003b2c6f00472c6f00042e6f001b2e6f00322e6f00892e6f00922e6f009b2e6f00'),
        (0x6f2ef4,0x6f2edd,0x6f3434,'r13',
         '83fe0f0f87970400004863c6418b8c8534346f004903cdffe1',
         '30306f0086306f005b336f00f62e6f001f2f6f005d2f6f009b2f6f00b62f6f0008326f00df2f6f00ab306f00ca306f0012316f002a316f0042316f005d316f00')]
    for dispatch,guard,table,image_register,code,tablehex in tables:
        assert pe.get_data(guard,len(bytes.fromhex(code))).hex()==code
        assert pe.get_data(table,64).hex()==tablehex
        ns['reviewed_switches'][base+dispatch]={
            'root':base+0x6f2770,'tableStart':base+table,'tableHex':tablehex,
            'guardStart':base+guard,'dispatchHex':code,'imageBaseRegister':image_register,
            'guardProof':'Unsigned<=15 bounds sixteen image-relative DWORD entries. First index has a must-proven MOVSXD R15,EAX definition; second is MOVSXD RAX,ESI after the unsigned guard.',
            'evidenceIds':[]}
    ns['cache'].pop(base+0x6f2770,None)
    insns,succ,bad,indirect=ns['decode'](base+0x6f2770)
    assert not bad and not indirect
    assert insns[base+0x6f2876].bytes.hex()=='4c63f8'
    # Must provenance of the first table's 64-bit index, including backedges.
    # A low32 comparison alone would NOT bound a possibly unrelated high half.
    states={base+0x6f2770:False};pending=[base+0x6f2770]
    while pending:
        pc=pending.pop();ins=insns[pc];value=states[pc]
        if pc==base+0x6f2876:value=True
        elif any(ns['reg'](ins,r)=='r15' for r in ins.regs_access()[1]):value=False
        for dest in succ[pc]:
            joined=value if dest not in states else states[dest] and value
            if dest not in states or states[dest]!=joined:states[dest]=joined;pending.append(dest)
    assert states[base+0x6f29d0], 'First switch index may have an unbounded upper half'
    for dispatch,guard,table,_,_,_ in tables:
        for source,targets in succ.items():
            for dest in targets:
                if base+guard<dest<=base+dispatch:
                    assert source+insns[source].size==dest and base+guard<=source<dest, 'Switch guard bypass'
    ns['live_growth_extra_contracts']={
        'recursiveReviewPath':str(path),'recursiveReviewSha256':hashlib.sha256(path.read_bytes()).hexdigest(),
        'switches':[{**ns['reviewed_switches'][base+dispatch],'dispatch':base+dispatch} for dispatch,*_ in tables],
        'indexWidthDefinition':ns['flow'].basic(insns[base+0x6f2876]),
        'fullSwitchBody':[ns['flow'].basic(i) for _,i in sorted(insns.items())],
        'limitations':['Normal decoded control flow only; no universal external/interior entry proof or hook approval',
                       'No caller stack-spill facts are restored by the recursive summary']}
