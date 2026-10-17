"""Review frozen transaction source with an independent concurrent workload."""
from pathlib import Path
import hashlib,json,subprocess
ROOT=Path(__file__).resolve().parents[2]
def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
receipt=ROOT/'evidence/engine-extension/native-agent-transaction-storage.json'
assert digest(receipt)=='098831ca5fa5073d3ba8acecc4d5c40e9f4fa0e97ef150dba0fd32267609a825'
report=json.loads(receipt.read_text());pins=dict(report['reviewedSources']);pins.update(report['executables'])
for path in ('tests/AgentStorage/native_agent_transaction_review.cpp','tools/battle-scale/Test-NativeAgentTransactionsReview.cmd','tools/battle-scale/review_native_agent_transactions.py'):
    pins[path]=digest(ROOT/path)
for p,sha in pins.items():assert digest(ROOT/p)==sha,p
run=subprocess.run(['cmd','/d','/c',str(ROOT/'tools/battle-scale/Test-NativeAgentTransactionsReview.cmd')],cwd=ROOT,text=True,capture_output=True,timeout=120)
assert run.returncode==0,(run.stdout,run.stderr)
assert run.stdout.count('PASS native-transaction fixtures assertions=5004 resets=753 cleanups=753 failures=35')==2
assert run.stdout.count('PASS independent concurrent transactions:4workers;2000 acquire/release pairs;57growth steps;1003slots;stablefirstblock;2002balancedtransitions')==2
for p,sha in pins.items():assert digest(ROOT/p)==sha,p
out={'schemaVersion':1,'status':'accepted-isolated-transaction-storage-publication-and-lifetime',
     'reviewedReceipt':{'path':str(receipt),'sha256':digest(receipt)},'sources':pins,
     'execution':{'exitCode':run.returncode,'stdout':run.stdout,'stderr':run.stderr},
     'outputs':{p:digest(ROOT/p) for p in ('build/agent-storage/native_agent_transaction_review.exe','build/agent-storage/native_agent_transaction_review_asan.exe')},
     'additionalPerBuild':{'workerThreads':4,'acquireReleasePairs':2000,'concurrentGrowthSteps':57,'finalCapacity':1003,'balancedTransitions':2002},
     'manualReview':['Lease member order unlocks inner storage then outer gate before releasing the retained Owner. Moved-from leases remain destructible; no assignment can replace owner under an old lock.',
                    'All mutation uses exclusive outer gate; reset finishes before any wrapper reader sees acquired identity. Cleanup sees still-valid identity and completes before backend release.',
                    'Both heaps reserve physical capacity before backend growth. Failed reserve/backend construction changes only spare capacity. Adoption stages heaps before backend commit, then swaps without allocation.',
                    'Invalid/stale/domain-invalid handles never call cleanup. Retired generation policy matches backend: max-generation live handle is not reinserted on release.',
                    'Close and final-owner destruction drain both domains before default destructors, with persistent transition and module owners retained. Release/drain does not allocate.',
                    'Min-heap deletion and backend explicit-index removal remain linear operations; this is a correctness integration step, not production scaling approval.'],
     'openFindings':[],
     'limitations':['Transitions and constructors in these fixtures are synthetic; no engine activation/removal adapter is certified.',
                    'Gate covers only wrapper clients. Engine job barriers, all native access routing and lifetime of escaped native pointers are unresolved.',
                    'Concurrent stress uses host synchronization and ASAN, not a race detector or exhaustive interleaving proof.',
                    'Callback failure/reentrancy is forbidden by contract, not recovered. Arbitrary SEH is not contained.',
                    'Occupied adoption remains a fixture API; real attachment must use a proved lifecycle boundary.'],
     'gameLaunched':False,'enginePatchApproved':False}
target=ROOT/'evidence/engine-extension/native-agent-transaction-independent-review.json'
assert not target.exists();target.write_text(json.dumps(out,indent=2)+'\n')
print(json.dumps({'status':out['status'],'sha256':digest(target)}))
