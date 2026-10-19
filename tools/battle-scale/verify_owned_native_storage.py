"""Capture source-bound callback/module ownership lifetime tests."""
from pathlib import Path
from datetime import datetime,timezone
import hashlib,json,subprocess
ROOT=Path(__file__).resolve().parents[2]
inputs=['src/MNBL.AgentStorage/OwnedNativeAgentStorage.hpp','src/MNBL.AgentStorage/NativeElementFactory.hpp',
    'src/MNBL.AgentStorage/AgentDomainStorage.hpp','src/MNBL.AgentStorage/StableAgentStorage.hpp',
    'tests/AgentStorage/owned_native_storage_tests.cpp','tests/AgentStorage/native_element_factory_tests.cpp',
    'build/agent-storage/native_element_machine_code.hpp','tools/battle-scale/Test-OwnedNativeStorage.cmd',
    'tools/battle-scale/verify_owned_native_storage.py']
def digest(p):return hashlib.sha256((ROOT/p).read_bytes()).hexdigest()
pins={p:digest(p) for p in inputs}
run=subprocess.run(['cmd','/d','/c',str(ROOT/'tools/battle-scale/Test-OwnedNativeStorage.cmd')],cwd=ROOT,capture_output=True,text=True)
assert run.returncode==0,run.stdout+run.stderr
assert run.stdout.count('PASS owned native storage:')==2
assert pins=={p:digest(p) for p in inputs}
out=dict(schemaVersion=1,status='passed-owned-native-storage-lifetime-tests',createdAtUtc=datetime.now(timezone.utc).isoformat(),sources=pins,
    outputs={p:digest(p) for p in ['build/agent-storage/owned_native_storage_tests.exe','build/agent-storage/owned_native_storage_asan.exe']},
    exitCode=run.returncode,stdout=run.stdout,stderr=run.stderr,
    checks=['Optimized and ASAN tests retain factory/runtime owner when public storage wrapper is destroyed before its moved read lease.',
            'Lease stays usable; final lease releases lock, then native objects, then callback factory/module owner exactly once.',
            'Rollback and explicit close preserve callback lifetime; domain/index/iteration methods forward to accepted backend.',
            'Copying or move-assigning leases is disabled to prevent premature owner replacement.'],
    limitations=['Main Agent ctor/dtor remain synthetic in this test; actual companion slice executes.',
                 'Owning the callback/module lifetime does not own engine workers or native Mission lifecycle. Adopted arrays still require a valid external lifetime owner and mutation barrier.',
                 'Raw payload pointers remain usable only for their retained lease/barrier duration.'],gameLaunched=False,enginePatchApproved=False)
path=ROOT/'evidence/engine-extension/owned-native-storage-tests.json';path.write_text(json.dumps(out,indent=2)+'\n')
print(json.dumps(dict(status=out['status'],sha256=hashlib.sha256(path.read_bytes()).hexdigest())))
