"""Source-pinned execution receipt for seven isolated scalar routing adapters."""
from pathlib import Path
from datetime import datetime,timezone
import hashlib,json,subprocess
ROOT=Path(__file__).resolve().parents[2]
INPUTS=['src/MNBL.AgentStorage/AgentDomainStorage.hpp','src/MNBL.AgentStorage/StableAgentStorage.hpp',
        'tools/battle-scale/prepare_segmented_scalar_harness.py','tests/AgentStorage/native_scalar_harness.cpp',
        'tools/battle-scale/Test-NativeScalarHarness.cmd','tools/battle-scale/verify_native_scalar_harness.py',
        'evidence/engine-extension/native-simple-agent-operation-routes.json']
PRESERVED=['tools/battle-scale/prepare_segmented_write_harness.py','tests/AgentStorage/native_write_harness.cpp',
           'tools/battle-scale/Test-NativeWriteHarness.cmd','tools/battle-scale/verify_native_write_harness.py',
           'build/agent-storage/write_machine_code.hpp','evidence/engine-extension/native-write-storage-integration.json',
           'evidence/engine-extension/native-segmented-write-independent-review.json']
OUTPUTS=['build/agent-storage/scalar_machine_code.hpp','build/agent-storage/native_scalar_harness.exe',
         'build/agent-storage/native_scalar_harness_asan.exe','evidence/engine-extension/native-segmented-scalar-replay.json']
def digest(p):return hashlib.sha256((ROOT/p).read_bytes()).hexdigest()
def main():
    before={p:digest(p) for p in INPUTS};preserved={p:digest(p) for p in PRESERVED}
    result=subprocess.run(['cmd','/d','/c',str(ROOT/'tools/battle-scale/Test-NativeScalarHarness.cmd')],cwd=ROOT,text=True,capture_output=True)
    assert before=={p:digest(p) for p in INPUTS},'Source changed during verification'
    assert preserved=={p:digest(p) for p in PRESERVED},'Accepted write prototype changed'
    assert result.returncode==0,result.stdout+result.stderr
    assert result.stdout.count('85680 operations;2040->4080->6120->8160->10200;')==2
    assert result.stdout.count('670 instruction-boundary Windows unwinds;91 exact UD2 error-path unwinds;')==2
    replay=json.loads((ROOT/OUTPUTS[-1]).read_text());assert replay['passedCases']==112 and len(replay['sites'])==7
    assert replay['generatedHeaderSha256']==digest(OUTPUTS[0])
    for path,sha in replay['sources'].items():assert hashlib.sha256(Path(path).read_bytes()).hexdigest()==sha
    routes=json.loads((ROOT/INPUTS[-1]).read_text())
    for source in routes['sources']:assert hashlib.sha256(Path(source['path']).read_bytes()).hexdigest()==source['sha256']
    out={'schemaVersion':1,'status':'passed-isolated-native-scalar-storage-integration',
         'createdAtUtc':datetime.now(timezone.utc).isoformat(),'sources':before,'preservedAcceptedInputs':preserved,
         'outputs':{p:digest(p) for p in OUTPUTS},'exitCode':result.returncode,'stdout':result.stdout,'stderr':result.stderr,
         'checks':{'sites':7,'unicornCases':112,'originalMachineInstructionComparisons':70,'explicitUnicornFailureTraps':42,
                   'nativeBuilds':['MSVC optimized','MSVC AddressSanitizer'],'scalarOperationsPerBuild':85680,
                   'instructionBoundaryUnwindsPerBuild':670,'actualExplicitErrorUnwindsPerBuild':91,
                   'storageCapacities':[2040,4080,6120,8160,10200],
                   'coverage':['All15 GPR outputs and XMM0..15 match original operations despite deliberate volatile helper clobbers in Unicorn.',
                               'Original 64-bit index is captured even when the DWORD result overwrites RAX; load destinations zero-extend.',
                               'Fixed0x108-byte frames, exact helper alignment, real nonvolatile MOV unwind saves, registered wrapper/stub lookup.',
                               'Poisoned unsaved slots/current nonvolatiles check partial prolog and body unwind states at every decoded boundary.',
                               'Invalid owner/index/inactive/closed requests, injected failure status, and caught C++ exceptions lead to exact UD2, never successful zero/ignored operation.',
                               'Actual exception propagation unwinds both generated frames before the narrowly matching fixture handler.',
                               'Borrowed original addresses survive four suffix-growth publications; independent secondary occupancy cannot satisfy primary requests; deleted index gets new generation.',
                               'Held read and write helper leases block close and growth until the operation completes; synthetic storage teardown balances callbacks/allocations.']},
         'limitations':['Isolated standard-CALL wrappers only: no native game entry redirection, prologue integration, incoming/indirect entry closure, engine worker safe point or installed patch.',
                        'Synthetic payloads and callback initialization are not real Bannerlord Agent constructors/destructors.',
                        'UD2 plus narrow fixture SEH interception is a test error contract, not an approved production error policy.',
                        'Arithmetic flags are dead under accepted site continuation proofs; arbitrary condition-code preservation is not claimed.',
                        'Read leases protect lifetime, not same-location concurrent write serialization or pointee lifetime. Helpers return scalar values only.',
                        'Windows boundary unwinding and generated trap propagation are exercised; arbitrary native engine handlers and asynchronous engine exceptions are not certified.'],
         'gameLaunched':False,'enginePatchApproved':False}
    path=ROOT/'evidence/engine-extension/native-scalar-storage-integration.json';path.write_text(json.dumps(out,indent=2)+'\n')
    print(json.dumps({'status':out['status'],'sha256':digest(path)}))
if __name__=='__main__':main()
