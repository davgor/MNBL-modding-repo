"""Build the private element probe and exercise its no-engine rejection ABI."""
from pathlib import Path
from datetime import datetime,timezone
import ctypes,hashlib,json,subprocess
ROOT=Path(__file__).resolve().parents[2]
inputs=['src/MNBL.AgentGrowthProbe/UnpublishedElementProbe.hpp','src/MNBL.AgentGrowthProbe/NativeElementProbe.cpp',
    'src/MNBL.AgentStorage/VerifiedNativeElementBinding.hpp','src/MNBL.AgentStorage/NativeElementFactory.hpp',
    'src/MNBL.AgentStorage/AgentDomainStorage.hpp','src/MNBL.AgentStorage/StableAgentStorage.hpp',
    'build/agent-storage/native_element_identity.hpp','build/agent-storage/native_element_machine_code.hpp',
    'tests/AgentStorage/unpublished_element_probe_tests.cpp','tests/AgentStorage/native_element_factory_tests.cpp',
    'tools/battle-scale/Build-NativeElementProbe.cmd','tools/battle-scale/verify_unpublished_element_probe.py']
def digest(p):return hashlib.sha256((ROOT/p).read_bytes()).hexdigest()
pins={p:digest(p) for p in inputs}
run=subprocess.run(['cmd','/d','/c',str(ROOT/'tools/battle-scale/Build-NativeElementProbe.cmd')],cwd=ROOT,capture_output=True,text=True)
assert run.returncode==0,run.stdout+run.stderr
assert run.stdout.count('PASS unpublished element probe:')==2
class Result(ctypes.Structure):
    _fields_=[(name,ctypes.c_uint64) for name in ('abi_size','abi_version','status','stage','capacity','pointer_checks','header_checks','allocation_attempts','allocations','releases','constructor_attempts')]+[
        ('constructed',ctypes.c_uint64*5),('destroyed',ctypes.c_uint64*5)]+[
        (name,ctypes.c_uint64) for name in ('allocation_failure_checked','constructor_failure_checked','exhaustion_checked','reuse_checked')]+[('error',ctypes.c_char*512)]
dll=ctypes.CDLL(str(ROOT/'build/agent-storage/MNBL.NativeElementProbe.dll'))
dll.MNBLElementProbeResultSize.argtypes=[];dll.MNBLElementProbeResultSize.restype=ctypes.c_uint64
dll.MNBLElementProbe.argtypes=[ctypes.c_void_p,ctypes.c_uint64];dll.MNBLElementProbe.restype=ctypes.c_int
size=ctypes.sizeof(Result);assert dll.MNBLElementProbeResultSize()==size
assert dll.MNBLElementProbe(None,size)==1
buffer=ctypes.create_string_buffer(b'\xa5'*(size+32));pointer=ctypes.addressof(buffer)+16
assert dll.MNBLElementProbe(pointer,size-1)==1 and buffer.raw[:size+32]==b'\xa5'*(size+32)
for _ in range(2):
    assert dll.MNBLElementProbe(pointer,size)==3
    result=Result.from_address(pointer)
    assert result.status==2 and result.stage==0 and result.abi_size==size and result.abi_version==1
    assert result.error==b'Native module is not loaded'
    assert result.allocation_attempts==result.constructor_attempts==0
    assert buffer.raw[:16]==b'\xa5'*16 and buffer.raw[size+16:size+32]==b'\xa5'*16
assert pins=={p:digest(p) for p in inputs}
out=dict(schemaVersion=1,status='passed-private-element-probe-offline-readiness',createdAtUtc=datetime.now(timezone.utc).isoformat(),sources=pins,
    outputs={p:digest(p) for p in ['build/agent-storage/unpublished_element_probe_tests.exe',
        'build/agent-storage/unpublished_element_probe_asan.exe','build/agent-storage/MNBL.NativeElementProbe.dll']},
    exitCode=run.returncode,stdout=run.stdout,stderr=run.stderr,resultAbiBytes=size,
    checks=['Optimized/ASAN private growth30->60->90;600 pointer and450 inactive-header checks.',
            'Actual companion initializer slice with synthetic Agent ctor/dtor;487 completed callbacks cleaned.',
            'Allocation and before-constructor failure rollback, both domain ledger exhaustion/reuse and teardown passed.',
            'Built DLL export ABI size matches independent ctypes structure; null/short requests rejected without writes.',
            'Two no-engine calls fail before allocation/construction, preserve canaries and release reentrancy gate.'],
    limitations=['No live game launch, positive native binding or real Agent construction. Default-path native closure review remains required.',
                 'This private probe never activates soldiers or changes native Mission capacity; it cannot satisfy live growth acceptance.',
                 'No gamehost or deployment runner is included.'],gameLaunched=False,enginePatchApproved=False)
path=ROOT/'evidence/engine-extension/unpublished-element-probe-offline-tests.json';path.write_text(json.dumps(out,indent=2)+'\n')
print(json.dumps(dict(status=out['status'],sha256=hashlib.sha256(path.read_bytes()).hexdigest(),abiBytes=size)))
