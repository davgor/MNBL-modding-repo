"""Capture the isolated native inline-write build/run with exact input hashes."""
from pathlib import Path
import hashlib,json,subprocess
from datetime import datetime,timezone
ROOT=Path(__file__).resolve().parents[2]
inputs=['src/MNBL.AgentStorage/AgentDomainStorage.hpp','src/MNBL.AgentStorage/StableAgentStorage.hpp',
        'tools/battle-scale/prepare_segmented_write_harness.py','tests/AgentStorage/native_write_harness.cpp',
        'tools/battle-scale/Test-NativeWriteHarness.cmd','tools/battle-scale/verify_native_write_harness.py',
        'evidence/engine-extension/native-segmented-agent-consumers.json',
        'evidence/engine-extension/native-live-agent-access-review.json']
def digest(p):return hashlib.sha256((ROOT/p).read_bytes()).hexdigest()
before={p:digest(p) for p in inputs}
result=subprocess.run(['cmd','/d','/c',str(ROOT/'tools/battle-scale/Test-NativeWriteHarness.cmd')],cwd=ROOT,text=True,capture_output=True)
assert before=={p:digest(p) for p in inputs},'Input changed during execution'
assert result.returncode==0,result.stdout+result.stderr
assert '12240 writes;2040->4080->6120->8160;' in result.stdout
assert '50 instruction-boundary Windows unwinds;' in result.stdout
replay=json.loads((ROOT/'evidence/engine-extension/native-segmented-write-replay.json').read_text())
assert replay['passedCases']==20
out={'schemaVersion':1,'status':'passed-isolated-native-inline-write-integration',
     'createdAtUtc':datetime.now(timezone.utc).isoformat(),'sources':before,
     'outputs':{p:digest(p) for p in ['build/agent-storage/native_write_harness.exe','build/agent-storage/write_machine_code.hpp',
                                   'evidence/engine-extension/native-segmented-write-replay.json']},
     'exitCode':result.returncode,'stdout':result.stdout,'stderr':result.stderr,
     'checks':['20 machine-code replays preserve all GPR/XMM values required by this exact site despite deliberate volatile helper clobbering.',
               'Fixed0x108-byte frame with registered nonvolatile MOV saves; Windows unwinding matches caller stack/return/nonvolatiles at50 instruction boundaries.',
               'Actual generated x64 stub calls C++ domain storage helper for12240 writes across2040,4080,6120 capacities.',
               'Borrowed original payloads stay put; real suffix allocation reaches8160 without moving earlier objects.',
               'Negative/out-of-range/foreign/closed requests and injected helper exception produce no write; primary deletion is not masked by secondary occupancy.',
               'Current-index reuse resolves new generation. A held helper read lease preventsclose and delays growth until the write finishes.',
               'Borrowed owner disposed once and all owned synthetic elements/allocations cleaned up.'],
     'limitations':['No game process, real Bannerlord Agent construction, native worker barrier or installed patch.',
                    'Only one exact field-write sequence is routed; all other native address/iteration/metadata consumers remain separate work.',
                    'Registered network owner context is bounded; alternate incoming/interior branches and multicast/multiplayer/replay protocols are not approved.',
                    'The helper uses occupied primary-domain lookup; original callback liveness assumptions still require an engine-path contract.',
                    'A storage read lease protects lifetime and blocks metadata mutation, but does not serialize simultaneous writes to the same Agent byte; engine write ownership remains unproved.',
                    'No arbitrary-flag preservation claim: following original ADDRSP overwrites arithmetic flags before POP/RET.',
                    'These Windows runtime unwind checks are boundary checks, not execution of arbitrary native exception handlers.'],
     'gameLaunched':False,'enginePatchApproved':False}
path=ROOT/'evidence/engine-extension/native-write-storage-integration.json';path.write_text(json.dumps(out,indent=2)+'\n')
print(json.dumps({'status':out['status'],'sha256':hashlib.sha256(path.read_bytes()).hexdigest()}))
