"""Build and retain isolated adoption tests; never starts or accesses the game."""
from pathlib import Path
import hashlib, json, subprocess
from datetime import datetime, timezone

ROOT = Path(__file__).resolve().parents[2]
inputs = [
    'src/MNBL.AgentStorage/AgentDomainStorage.hpp',
    'src/MNBL.AgentStorage/StableAgentStorage.hpp',
    'tests/AgentStorage/agent_domain_storage_tests.cpp',
    'tests/AgentStorage/adopted_agent_storage_tests.cpp',
    'tools/battle-scale/Test-AdoptedAgentStorage.cmd',
    'tools/battle-scale/verify_adopted_storage.py',
]
def digest(name): return hashlib.sha256((ROOT/name).read_bytes()).hexdigest()
before = {name:digest(name) for name in inputs}
result = subprocess.run(['cmd', '/d', '/c', str(ROOT/'tools/battle-scale/Test-AdoptedAgentStorage.cmd')],
                        cwd=ROOT, text=True, capture_output=True)
assert before == {name:digest(name) for name in inputs}, 'Inputs changed during test'
assert result.returncode == 0, result.stdout + result.stderr
assert result.stdout.count('PASS borrowed original arrays:') == 2
assert result.stdout.count('PASS 75 adopted-suffix failure points:') == 2
artifacts = ['build/agent-storage/adopted_agent_storage_tests.exe',
             'build/agent-storage/adopted_agent_storage_asan.exe']
output = {
    'schemaVersion':1, 'status':'passed-isolated-adoption-tests-pending-independent-review',
    'createdAtUtc':datetime.now(timezone.utc).isoformat(),
    'sources':before, 'binaries':{name:digest(name) for name in artifacts},
    'command':'tools/battle-scale/Test-AdoptedAgentStorage.cmd',
    'exitCode':result.returncode, 'stdout':result.stdout, 'stderr':result.stderr,
    'checks':['Original five arrays adopted without element construction, relocation, destruction or release',
              'Separate primary and secondary occupied-index snapshots, holes and highwater',
              '2040 borrowed elements followed by actual new blocks at4080 and6120',
              'Inactive physical record access for native scans; invalid and closed ranges rejected',
              'Explicit index allocation unlinks one free slot, rejects occupied/out-of-range slots, preserves generations',
              '75 adopted-suffix callback failures preserve old objects, pointers, both ledgers and highwater; retry succeeds',
              'Earlier domain test suite runs in both builds, including75 additional fault points and66049 indices/domain',
              'Borrowed owner retained through last lease, released once; only newly owned records invoke storage destruction'],
    'limits':['Synthetic existing objects; not live Bannerlord Agents or a validated engine allocator adapter',
              'Caller must supply a real engine lifetime owner and frozen occupancy snapshots; no game barrier implemented',
              'Free slots rebuilt in ascending order; replacing native allocation metadata and every consumer is still required',
              'Primary companion linkage reflects reviewed create/delete paths only; arbitrary companion writers remain unproved',
              'The earlier domain receipt binds pre-adoption header bytes and is historical; this receipt binds current source',
              'MSVC AddressSanitizer is not a data-race detector; no full-engine concurrency or exception-handler claim'],
    'gameLaunched':False, 'enginePatchApproved':False, 'independentReviewPending':True,
}
path=ROOT/'evidence/engine-extension/adopted-agent-storage-tests.json'
path.write_text(json.dumps(output,indent=2)+'\n')
print(json.dumps({'status':output['status'],'sha256':hashlib.sha256(path.read_bytes()).hexdigest()}))
