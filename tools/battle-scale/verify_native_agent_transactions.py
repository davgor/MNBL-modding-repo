"""Build/replay the isolated transactional storage tests and bind a fresh receipt."""
from pathlib import Path
import hashlib
import json
import re
import subprocess
from patch_native_copy import safe_review_output

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'evidence/engine-extension/native-agent-transaction-storage.json'
INPUTS = [
    'src/MNBL.AgentStorage/NativeAgentTransactionStorage.hpp',
    'src/MNBL.AgentStorage/OwnedNativeAgentStorage.hpp',
    'src/MNBL.AgentStorage/NativeElementFactory.hpp',
    'src/MNBL.AgentStorage/AgentDomainStorage.hpp',
    'src/MNBL.AgentStorage/StableAgentStorage.hpp',
    'tests/AgentStorage/native_agent_transaction_tests.cpp',
    'tools/battle-scale/Test-NativeAgentTransactions.cmd',
    'tools/battle-scale/verify_native_agent_transactions.py',
    'evidence/engine-extension/native-agent-allocation-independent-review.json',
]


def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    protected = [ROOT / name for name in INPUTS]
    pins = {name: sha(ROOT / name) for name in INPUTS}
    completed = subprocess.run(['cmd', '/c', str(ROOT / 'tools/battle-scale/Test-NativeAgentTransactions.cmd')],
                               cwd=ROOT, capture_output=True, text=True, timeout=120)
    assert completed.returncode == 0, completed.stdout + completed.stderr
    matches = re.findall(r'^PASS native-transaction fixtures assertions=(\d+) resets=(\d+) cleanups=(\d+) failures=(\d+)$',
                         completed.stdout, re.MULTILINE)
    assert matches == [('5004', '753', '753', '35')]*2, completed.stdout
    assert pins == {name: sha(ROOT / name) for name in INPUTS}, 'Sources changed while compiling/testing'
    executables = ['build/agent-storage/native_agent_transaction_tests.exe',
                   'build/agent-storage/native_agent_transaction_asan.exe']
    output = {
        'schemaVersion': 1,
        'status': 'passed-isolated-transactional-storage-fixtures',
        'reviewedSources': pins,
        'executables': {name:sha(ROOT/name) for name in executables},
        'execution': {'returnCode':completed.returncode,'stdout':completed.stdout,'stderr':completed.stderr,
                      'configurations':['MSVC x64 optimized /O2','MSVC x64 AddressSanitizer /O1'],
                      'assertionsPerBuild':5004,'resetCallbacksPerBuild':753,'cleanupCallbacksPerBuild':753,
                      'injectedHostAllocationOrBeforeConstructorFailuresPerBuild':35,
                      'modelOperationsPerBuild':1200,'modelBlockSizes':[1,3,63,64,65]},
        'contracts': [
            'An outer shared_mutex is retained alongside Owner and the backend lease. Member destruction unlocks the backend first, outer gate second, and releases Owner last.',
            'All grow/adopt/acquire/acquire_at/release/close operations use the exclusive outer gate. No raw backend reference is exposed.',
            'Per-domain min-heaps select the globally lowest free index, preserving older holes when capacity grows and after out-of-order releases. Explicit allocation uses backend acquire_at.',
            'Heap capacity reservations occur before backend growth. Adoption builds both pending free heaps before backend adoption. Exceptions preserve published payloads, logical capacity, occupancy and free-index contents.',
            'Reset completes under the exclusive gate after ledger acquisition and before the new handle is observable through a wrapper read lease.',
            'Required cleanup executes with still-valid identity before backend release. Invalid/stale/domain-forged handles do not invoke cleanup.',
            'Release, active explicit close and active final-owner destruction were checked with the next host allocation configured to fail; none allocated.',
            'Close drains both active domains through persistent cleanup callbacks before payload/default destruction. Final moved lease retains callback and runtime owners through this cleanup.',
            'Generation-max release is excluded from the free heap according to the accepted backend retirement policy; no generation wrapping is introduced.'
        ],
        'limitations': [
            'Constructors, destructor and transitions in this test are synthetic fixtures. No actual native activation, reset, removal or Mission adoption is implemented by these callbacks.',
            'This gate coordinates only accesses routed through this wrapper. Original engine readers, jobs, retained pointers, active native metadata publication and safe snapshot boundaries remain unproved.',
            'Callbacks must be nonreentrant, nonescaping, nonfailing and persistently owned. noexcept does not contain SEH; actual engine teardown requires a separate proven adapter.',
            'The public wrapper must remain alive through in-flight method calls. A thread holding a lease must not invoke mutation, including try_close, on the same instance because the shared_mutex is nonrecursive.',
            'Read leases protect storage lifetime/publication, not concurrent payload writes. Concurrent mutators through read leases need additional synchronization.',
            'Min-heap explicit deletion/reheapification and backend acquire_at may be O(N). This prototype has no production performance certification.',
            'Generation-max retirement is structurally handled but not reached by 2^64 lifetime iterations in the tests. No private backend state was forged to claim that runtime coverage.',
            'Wide indices are retained in the API; these fixtures do not establish native signed-short compatibility or exercise a live engine index above 32767.',
            'Host allocation and before-constructor failures are injected; partially executed native constructor exceptions are not contained or tested.',
            'Borrowed occupied snapshots are supplied by the fixture; their native header consistency, ownership and quiescence remain caller obligations.'
        ],
        'acceptedBackendFactoryChanged':False,
        'gameLaunched':False,'enginePatchApproved':False,
    }
    safe_review_output(OUT,protected+[ROOT/p for p in executables]).write_text(json.dumps(output,indent=2)+'\n')
    print(json.dumps({'status':output['status'],'receiptSha256':sha(OUT),'assertionsPerBuild':5004,'failuresPerBuild':35}))


if __name__ == '__main__': main()
