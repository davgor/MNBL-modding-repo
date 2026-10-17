"""Capture source-bound offline native module identity tests."""
from pathlib import Path
from datetime import datetime,timezone
import hashlib,json,subprocess
ROOT=Path(__file__).resolve().parents[2]
inputs=['src/MNBL.AgentStorage/VerifiedNativeElementBinding.hpp','src/MNBL.AgentStorage/NativeElementFactory.hpp',
        'src/MNBL.AgentStorage/AgentDomainStorage.hpp','src/MNBL.AgentStorage/StableAgentStorage.hpp',
        'tools/battle-scale/prepare_native_element_binding.py','tests/AgentStorage/native_element_binding_tests.cpp',
        'tools/battle-scale/Test-NativeElementBinding.cmd','tools/battle-scale/verify_native_element_binding.py']
def digest(p):return hashlib.sha256((ROOT/p).read_bytes()).hexdigest()
before={p:digest(p) for p in inputs}
run=subprocess.run(['cmd','/d','/c',str(ROOT/'tools/battle-scale/Test-NativeElementBinding.cmd')],cwd=ROOT,text=True,capture_output=True)
assert run.returncode==0,run.stdout+run.stderr
assert before=={p:digest(p) for p in inputs}
assert run.stdout.count('PASS native binding:')==2
out=dict(schemaVersion=1,status='passed-offline-native-binding-identity-tests',createdAtUtc=datetime.now(timezone.utc).isoformat(),
    sources=before,outputs={p:digest(p) for p in ['build/agent-storage/native_element_identity.hpp',
        'build/agent-storage/native_element_binding_tests.exe','build/agent-storage/native_element_binding_asan.exe',
        'evidence/engine-extension/native-element-binding-identity.json']},exitCode=run.returncode,stdout=run.stdout,stderr=run.stderr,
    checks=['Optimized and ASAN builds verify full original file identity and original header/executable/function-table bytes in an offline mapped-layout buffer.',
            '55 changed region/entry byte cases are rejected; short input and changed-file cases fail.',
            'Loaded unrelated modules, interior module address and null module are rejected without executing target constructors.',
            'Loaded-image memory checks reject private allocations and overflow; runtime mutable data is intentionally excluded.',
            'Every pinned executable byte requires execute/read permission; nonexecutable MEM_IMAGE fixture rejected and executable MEM_IMAGE fixture accepted.'],
    limitations=['No positive binding to a running Bannerlord module and no live Agent constructor/destructor execution.',
                 'Full file identity and loaded header/text/pdata equality do not prove mutable runtime initialization or all relocated read-only data/exception handler metadata.',
                 'Caller must exclude concurrent code/module mutation during binding and retain factory/runtime owner through all storage leases.',
                 'Binding does not synchronize engine jobs, activate agents, install patches or widen native indices.'],
    gameLaunched=False,enginePatchApproved=False)
path=ROOT/'evidence/engine-extension/native-element-binding-tests.json';path.write_text(json.dumps(out,indent=2)+'\n')
print(json.dumps(dict(status=out['status'],sha256=hashlib.sha256(path.read_bytes()).hexdigest())))
