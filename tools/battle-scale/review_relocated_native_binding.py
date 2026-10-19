"""Independent offline review/build of the narrowly normalized native binding."""
from pathlib import Path
import ctypes
import hashlib
import json
import os
import re
import struct
import subprocess
import sys

ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import pefile

def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()


def main():
    paths=[Path(__file__)]+[ROOT/p for p in (
        'src/MNBL.AgentStorage/RelocatedNativeElementBinding.hpp',
        'src/MNBL.AgentStorage/VerifiedNativeElementBinding.hpp',
        'src/MNBL.AgentStorage/NativeElementFactory.hpp',
        'src/MNBL.AgentStorage/AgentDomainStorage.hpp',
        'src/MNBL.AgentStorage/StableAgentStorage.hpp',
        'src/MNBL.AgentGrowthProbe/UnpublishedElementProbe.hpp',
        'src/MNBL.AgentGrowthProbe/RelocatedNativeElementProbe.cpp',
        'src/MNBL.AgentGrowthProbe/NativeElementProbe.cpp',
        'tests/AgentStorage/relocated_native_binding_tests.cpp',
        'tools/battle-scale/Test-RelocatedNativeBinding.cmd',
        'build/agent-storage/native_element_identity.hpp',
        'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0',
        'evidence/engine-extension/native-element-binding-independent-review.json',
        'build/element-probe-host/runs/935d4269-1bbd-46f9-90b8-fddce29cabfe/result.json',
        'build/element-probe-host/runs/935d4269-1bbd-46f9-90b8-fddce29cabfe/runtime-receipt.json')]
    before={str(p):digest(p) for p in paths}
    original=paths[-4];assert digest(original)=='2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
    pe=pefile.PE(str(original));image=bytearray(pe.get_memory_mapped_image())
    offset=pe.OPTIONAL_HEADER.get_field_absolute_offset('ImageBase')
    assert offset==0x1c8 and pe.OPTIONAL_HEADER.ImageBase==0x180000000
    identity=(ROOT/'build/agent-storage/native_element_identity.hpp').read_text()
    regions=[]
    for a,n,execute,raw in re.findall(r'\{(0x[0-9a-f]+),(0x[0-9a-f]+),(true|false),\{([^}]+)\}\}',identity):
        start,size=int(a,16),int(n,16)
        expected=bytes(int(v,16) for v in raw.split(','))
        assert hashlib.sha256(image[start:start+size]).digest()==expected
        regions.append((start,size,execute=='true',expected))
    assert len(regions)==3 and regions[0][:3]==(0,1024,False)
    assert regions[1][2] and not regions[2][2]
    # Independently reproduce exactly the observed byte differences. No blanket
    # .rdata/unwind normalization or loader-field whitelist is introduced.
    observed=0x7ff97ec40000;changed=bytearray(image)
    struct.pack_into('<Q',changed,offset,observed)
    diffs=[i for i in range(1024) if image[i]!=changed[i]]
    assert diffs==[0x1ca,0x1cb,0x1cc,0x1cd]
    runtime=json.loads(paths[-2].read_text())
    diagnostic=runtime['native']['ErrorText']
    assert diagnostic=='base=7ff97ec40000 preferred=180000000 R0 n=4 1ca:0>c4 1cb:80>7e 1cc:1>f9 1cd:0>7f R1 n=0 R2 n=0'
    assert runtime['native']['stage']==0 and runtime['native']['allocation_attempts']==0
    normalized=bytearray(changed[:1024]);struct.pack_into('<Q',normalized,offset,0x180000000)
    assert hashlib.sha256(normalized).digest()==regions[0][3]
    outside_checks=0
    for byte in range(1024):
        if offset<=byte<offset+8:continue
        normalized[byte]^=1
        assert hashlib.sha256(normalized).digest()!=regions[0][3]
        normalized[byte]^=1;outside_checks+=1
    assert outside_checks==1016
    old=(ROOT/'src/MNBL.AgentGrowthProbe/NativeElementProbe.cpp').read_text()
    new=(ROOT/'src/MNBL.AgentGrowthProbe/RelocatedNativeElementProbe.cpp').read_text()
    assert old.replace('VerifiedNativeElementBinding.hpp','RelocatedNativeElementBinding.hpp').replace('bind_verified_native_elements','bind_relocated_verified_native_elements')==new
    command=ROOT/'tools/battle-scale/Test-RelocatedNativeBinding.cmd'
    assert 'build\\agent-storage\\relocated_native_binding_asan.exe\nif errorlevel 1 goto failed\ncl ' in command.read_text()
    run=subprocess.run(['cmd','/d','/c',str(command)],cwd=ROOT,text=True,capture_output=True,timeout=180)
    assert run.returncode==0,run.stdout+run.stderr
    assert run.stdout.count('PASS relocated native binding: 4 verified loader bases;256 forged base bit checks;')==2
    dll_path=ROOT/'build/agent-storage/MNBL.RelocatedNativeElementProbe.dll'
    dll=ctypes.CDLL(str(dll_path));dll.MNBLElementProbeResultSize.restype=ctypes.c_uint64
    dll.MNBLElementProbe.argtypes=[ctypes.c_void_p,ctypes.c_uint64];dll.MNBLElementProbe.restype=ctypes.c_int
    assert dll.MNBLElementProbeResultSize()==712
    interop=[]
    for size in (0,1,711,713,0xffffffffffffffff):
        buffer=ctypes.create_string_buffer(b'\xa5'*776,776);before_bytes=buffer.raw
        code=dll.MNBLElementProbe(ctypes.addressof(buffer)+32,size)
        assert code==1 and buffer.raw==before_bytes
        interop.append({'case':'wrong-size','size':size,'returnCode':code,'bufferUnchanged':True})
    assert dll.MNBLElementProbe(None,712)==1
    for repeat in range(2):
        buffer=ctypes.create_string_buffer(b'\xa5'*776,776)
        code=dll.MNBLElementProbe(ctypes.addressof(buffer)+32,712)
        raw=buffer.raw[32:744];fields=struct.unpack('<25Q',raw[:200]);error=raw[200:].split(b'\0')[0].decode()
        assert code==3 and fields[:4]==(712,1,2,0) and not any(fields[4:])
        assert error=='Native module is not loaded' and buffer.raw[:32]==buffer.raw[744:]==b'\xa5'*32
        interop.append({'case':'no-engine','repeat':repeat,'returnCode':code,'status':2,'stage':0,'allCountersZero':True,'canariesIntact':True,'error':error})
    output_names=['build/agent-storage/relocated_native_binding_tests.exe','build/agent-storage/relocated_native_binding_asan.exe','build/agent-storage/MNBL.RelocatedNativeElementProbe.dll']
    outputs={p:digest(ROOT/p) for p in output_names}
    assert before=={p:digest(p) for p in before}
    result={'schemaVersion':1,'status':'accepted-bounded-relocated-native-binding-offline',
        'sources':before,'outputs':outputs,'command':str(command),'exitCode':run.returncode,'stdout':run.stdout,'stderr':run.stderr,
        'checks':{'builds':['MSVC optimized','MSVC AddressSanitizer'],'basesPerBuild':4,'forgedBaseBitsPerBuild':256,
            'selectedRegionMutationsPerBuild':55,'independentHeaderOutsideBaseMutations':outside_checks,
            'actualHeaderImageBaseOffset':'0x1c8','normalizedBytes':8,'observedChangedBytes':[hex(i) for i in diffs],
            'exactProbeImplementationDelta':'Only binding include/function name changed; ABI, private exercise and reentrancy/cleanup are identical.'},
        'interop':interop,'fixedFinding':'ASAN executable result is now checked before DLL compilation, preventing a later successful compiler exit from hiding a failed test.',
        'review':['Actual module is retained by inherited GetModuleHandleEx RAII before obtaining base; interior aliases rejected and reference released on rejection.',
            'Exact original file hash remains required. Only loaded OptionalHeader.ImageBase is normalized, after equality with the actual retained HMODULE address.',
            'All remaining header bytes and exact .text/.pdata hashes are unchanged. Every selected region requires MEM_IMAGE/same AllocationBase/readability; entire .text also requires execute permission.',
            'Native entrypoints use verified actual base plus original RVAs. No target LoadLibrary, native module initializer, Mission, hook or soldier activation is added.'],
        'limitations':['No positive live native binding or real in-game constructor execution was performed by this review.',
            '.pdata is the runtime-function table, not all unwind-handler data/.rdata. Runtime data remains deliberately outside immutable identity.',
            'Engine initialization, native thread suitability and absence of code/module mutation remain caller requirements; file hashing and module references are not a worker barrier.',
            'Header normalization supports exactly this observed pinned-image loader behavior; no other PE field or binary build is admitted.'],
        'gameLaunched':False,'installedFilesChanged':False,'enginePatchApproved':False}
    output=ROOT/'evidence/engine-extension/native-relocated-binding-independent-review.json'
    output.write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps({'path':str(output),'sha256':digest(output),'outputs':outputs,'status':result['status']}))


if __name__=='__main__':main()
