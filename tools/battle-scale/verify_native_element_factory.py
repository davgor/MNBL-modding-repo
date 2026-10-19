"""Build and capture exact-source isolated native element tests."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib, json, subprocess
ROOT=Path(__file__).resolve().parents[2]
inputs=['src/MNBL.AgentStorage/NativeElementFactory.hpp','src/MNBL.AgentStorage/AgentDomainStorage.hpp',
        'src/MNBL.AgentStorage/StableAgentStorage.hpp','tests/AgentStorage/native_element_factory_tests.cpp',
        'tools/battle-scale/prepare_native_element_harness.py','tools/battle-scale/Test-NativeElementFactory.cmd',
        'tools/battle-scale/verify_native_element_factory.py','evidence/engine-extension/native-live-pool-growth-contract.json']
def digest(p):return hashlib.sha256((ROOT/p).read_bytes()).hexdigest()
before={p:digest(p) for p in inputs}
run=subprocess.run(['cmd','/d','/c',str(ROOT/'tools/battle-scale/Test-NativeElementFactory.cmd')],cwd=ROOT,text=True,capture_output=True)
assert before=={p:digest(p) for p in inputs},'Source changed during build/test'
assert run.returncode==0,run.stdout+run.stderr
assert run.stdout.count('PASS native element factory:')==2
replay=json.loads((ROOT/'evidence/engine-extension/native-element-companion-replay.json').read_text())
assert len(replay['cases'])==4 and len(replay['originalInstructions'])==107
receipt=dict(schemaVersion=1,status='passed-isolated-native-element-callback-integration',
    createdAtUtc=datetime.now(timezone.utc).isoformat(),sources=before,
    outputs={p:digest(p) for p in ['build/agent-storage/native_element_factory_tests.exe',
        'build/agent-storage/native_element_factory_asan.exe','build/agent-storage/native_element_machine_code.hpp',
        'evidence/engine-extension/native-element-companion-replay.json']},
    exitCode=run.returncode,stdout=run.stdout,stderr=run.stderr,
    checks=['Original230 and2c8 constructor bytes execute in isolated Windows process with original read-only constants and wrapper unwind metadata.',
            'Both optimized and ASAN builds pass full-byte companion comparison for two poison patterns, including fields intentionally untouched by native initialization.',
            'Stable five-pool growth2040->4080->6120 executes per-destination constructors and inactive header writes.',
            'All75 constructor failure positions in three appended blocks retain earlier capacity and addresses; retry succeeds and completed Agent test objects are destroyed exactly once in reverse order.',
            'Partially constructed Agent test object cleans its own resource and is not destroyed again by storage.',
            'All five missing binding/owner cases are rejected. Alignment is16 for Agent/230 and8 for each2c8 record.'],
    limitations=['Main Agent constructor/destructor are synthetic test functions, not live Bannerlord objects.',
                 'Caller must supply verified initialized native bindings and retain factory/runtime owner through every storage lease.',
                 'Production module binding, native allocator/reset metadata, active removal and whole-engine lifetime barrier remain incomplete.',
                 'ASAN instruments the C++ harness, not copied native machine instructions; the separate emulation validates their bounded writes.',
                 'No game launch, installed-file change or patch approval.'],gameLaunched=False,enginePatchApproved=False)
path=ROOT/'evidence/engine-extension/native-element-factory-integration.json'
path.write_text(json.dumps(receipt,indent=2)+'\n')
print(json.dumps(dict(status=receipt['status'],sha256=hashlib.sha256(path.read_bytes()).hexdigest())))
