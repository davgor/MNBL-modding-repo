"""Prepare exact closed-slice code/data for a separate Windows test process."""
from pathlib import Path
import hashlib,json,struct,sys
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import pefile
SOURCE=ROOT/'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
CONTRACT=ROOT/'evidence/engine-extension/native-unpublished-element-runtime-contract.json'
REVIEW=ROOT/'evidence/engine-extension/native-unpublished-element-contract-independent-review.json'
SHA='2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
CONTRACT_SHA='036733361062f0aa087b8f7321c0132aceaa85cb0ee244bb27fa984b9a4e5660'
def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def main():
    paths=[SOURCE,CONTRACT,REVIEW,Path(__file__)];before={str(p):digest(p) for p in paths}
    assert digest(SOURCE)==SHA and digest(CONTRACT)==CONTRACT_SHA
    contract=json.loads(CONTRACT.read_text());pe=pefile.PE(data=SOURCE.read_bytes());image=pe.get_memory_mapped_image();regions=[];functions=[]
    def region(name,rva,raw,executable):regions.append({'name':name,'rva':rva,'bytes':raw.hex(),'executable':executable})
    for name,body in contract['nativeBodies'].items():
        a=int(body['start'],16)-0x180000000;raw=bytes.fromhex(body['bytes']);assert pe.get_data(a,len(raw))==raw
        region(name,a,raw,True)
        entries=[e.struct for e in pe.DIRECTORY_ENTRY_EXCEPTION if e.struct.BeginAddress==a]
        assert len(entries)<=1
        if not entries:continue
        entry=entries[0];assert entry.EndAddress==int(body['endExclusive'],16)-0x180000000
        h=pe.get_data(entry.UnwindData,4);assert h[0]&7==1 and not h[0]>>3&4
        n=4+((h[2]+1)&~1)*2;handler=None
        if h[0]>>3&3:
            handler=struct.unpack('<I',pe.get_data(entry.UnwindData+n,4))[0]
            if handler==0x9e3177:n+=8 # exact CxxFrameHandler3 RVA + original FuncInfo RVA
            elif handler==0x9e318f:
                count=struct.unpack('<I',pe.get_data(entry.UnwindData+n+4,4))[0];assert count==1;n+=8+16*count
            else:raise AssertionError(hex(handler))
        region(name+'Unwind',entry.UnwindData,pe.get_data(entry.UnwindData,n),False)
        functions.append({'begin':entry.BeginAddress,'end':entry.EndAddress,'unwind':entry.UnwindData,'handlerRva':handler,'originalUnwindBytes':pe.get_data(entry.UnwindData,n).hex()})
    for r in contract['imageReadRanges']:
        a=int(r['rva'],16);z=int(r['endExclusiveRva'],16);raw=image[a:z];assert raw.hex()==r['originalMappedBytes']
        region('provenData'+hex(a),a,raw,False)
    region('nonNullRegistryFixture',0xda2a28,image[0xda2a28:0xda2a30],False)
    pages={}
    for r in regions:
        for page in range(r['rva']&~4095,(r['rva']+len(bytes.fromhex(r['bytes']))+4095)&~4095,4096):
            assert page not in pages or pages[page]==r['executable'],'Mixed code/data page requires separate policy'
            pages[page]=r['executable']
    # Unsupported handlers share the approved math/CFG code page. They remain
    # INT3-filled rather than silently acquiring zero-filled executable bytes.
    for handler in (0x9e3177,0x9e318f):
        assert pages[handler&~4095]
        assert not any(r['executable'] and r['rva']<=handler<r['rva']+len(bytes.fromhex(r['bytes'])) for r in regions)
    header='#pragma once\n#include <array>\n#include <span>\n#include <cstdint>\n'
    for i,r in enumerate(regions):
        raw=bytes.fromhex(r['bytes']);header+=f'inline constexpr std::array<unsigned char,{len(raw)}> default_region_{i}'+'{'+','.join(hex(x) for x in raw)+'};\n'
    header+='struct DefaultImageRegion { unsigned rva; std::span<const unsigned char> bytes; bool executable; };\n'
    header+=f'inline constexpr std::array<DefaultImageRegion,{len(regions)}> default_image_regions'+'{{\n'
    header+=''.join('{'+str(r['rva'])+f',default_region_{i},'+('true' if r['executable'] else 'false')+'},\n' for i,r in enumerate(regions))+'}};\n'
    header+='struct DefaultRuntimeFunction { unsigned begin,end,unwind; };\n'
    header+=f'inline constexpr std::array<DefaultRuntimeFunction,{len(functions)}> default_runtime_functions'+'{{'+','.join('{'+','.join(str(f[k]) for k in ('begin','end','unwind'))+'}' for f in sorted(functions,key=lambda f:f['begin']))+'}};\n'
    header+=f'inline constexpr unsigned default_image_size={pe.OPTIONAL_HEADER.SizeOfImage};\n'
    header+=f'inline constexpr std::array<unsigned,{len(pages)}> default_committed_pages'+'{'+','.join(str(p) for p in sorted(pages))+'};\n'
    out_header=ROOT/'build/agent-storage/native_default_image.hpp';out_header.write_text(header)
    assert before=={str(p):digest(p) for p in paths}
    report={'schemaVersion':1,'status':'prepared-closed-default-native-image-test-only','sources':before,'imageSize':pe.OPTIONAL_HEADER.SizeOfImage,'regions':regions,'runtimeFunctions':functions,'pagePermissions':[{'rva':p,'executable':e} for p,e in sorted(pages.items())],'generatedHeaderSha256':digest(out_header),
            'runtimeBindings':{'atan2fIatRva':'0xa2dfa8','cfgDispatcherSlotRva':'0xa2e4e8','cfgDispatcherTargetRva':'0x9e3660','emptySentinelRva':'0xda29b8','registryFixtureRva':'0xda2a28'},
            'unwindScope':'Exact original unwind headers/codes/handler data are registered. Unreviewed exception handlers at9e3177/9e318f are trap-filled on a shared executable page; other unreviewed pages are uncommitted. Unexpected native exceptions fail the isolated process. No native EH flag is removed; no mid-constructor exception containment is certified.',
            'mappingScope':'Reserve original RVA geometry, commit only proven pages. Code pages start INT3-filled then receive only exact approved spans; data pages start zero-filled then receive approved original data. No module initializer or game DLL is loaded.',
            'enginePatchApproved':False,'gameLaunched':False}
    out=ROOT/'evidence/engine-extension/native-default-element-image.json';out.write_text(json.dumps(report,indent=2)+'\n')
    print(json.dumps({'regions':len(regions),'pages':len(pages),'runtimeFunctions':len(functions),'sha256':digest(out)}))
if __name__=='__main__':main()
