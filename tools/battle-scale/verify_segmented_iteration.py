"""Retain current bitmap iteration plus adoption/rollback regression execution."""
from pathlib import Path
import hashlib,json,subprocess
from datetime import datetime,timezone
ROOT=Path(__file__).resolve().parents[2]
inputs=['src/MNBL.AgentStorage/AgentDomainStorage.hpp','src/MNBL.AgentStorage/StableAgentStorage.hpp',
        'tests/AgentStorage/agent_domain_storage_tests.cpp','tests/AgentStorage/adopted_agent_storage_tests.cpp',
        'tests/AgentStorage/segmented_iteration_tests.cpp','tools/battle-scale/Test-SegmentedIteration.cmd',
        'tools/battle-scale/verify_segmented_iteration.py']
def digest(p):return hashlib.sha256((ROOT/p).read_bytes()).hexdigest()
before={p:digest(p) for p in inputs}
result=subprocess.run(['cmd','/d','/c',str(ROOT/'tools/battle-scale/Test-SegmentedIteration.cmd')],cwd=ROOT,text=True,capture_output=True)
assert before=={p:digest(p) for p in inputs},'Source changed during tests'
assert result.returncode==0,result.stdout+result.stderr
assert result.stdout.count('7579 boundary queries;5000 randomized mutations;66049 slots;')==2
assert result.stdout.count('PASS 75 adopted-suffix failure points:')==2
assert result.stdout.count('PASS atomic shared growth:')==2
binaries=['segmented_iteration_tests.exe','segmented_iteration_asan.exe',
          'adopted_bitmap_regression.exe','adopted_bitmap_regression_asan.exe']
out={'schemaVersion':1,'status':'passed-isolated-segmented-iteration-and-regressions',
     'createdAtUtc':datetime.now(timezone.utc).isoformat(),'sources':before,
     'binaries':{p:digest('build/agent-storage/'+p) for p in binaries},
     'exitCode':result.returncode,'stdout':result.stdout,'stderr':result.stderr,
     'checks':['Per-domain bitmap enumeration crosses arbitrary partial64-bit words and block sizes1/3/63/64/65/67/2040.',
               'Forward inclusive and reverse exclusive queries match independent sorted occupancy sets; no_slot and closed bounds checked.',
               '5000 randomized allocate/release operations preserve independent primary/secondary iteration and highwater.',
               '66049 occupied indices, then alternating deletions, cross signed16/unsigned16 boundaries.',
               'Borrowed first-block occupancy survives suffix growth and new slots across segment boundaries.',
               'Current header passes full domain/adoption regressions in optimized and AddressSanitizer builds, including150 callback failure points.',
               'Payload addresses, ownership and reverse destruction remain unchanged by bitmap metadata.'],
     'limitations':['Storage backend with synthetic records only; no engine pointer walk or signed-link consumer is yet rewritten.',
                    'Historical adoption/domain receipts bind earlier header versions; this receipt binds the current bitmap version.',
                    'Caller still needs real engine allocator callbacks, mission lifetime ownership and a validated publication barrier.',
                    'ASAN does not establish absence of data races or whole-engine reader quiescence.'],
     'enginePatchApproved':False,'gameLaunched':False}
path=ROOT/'evidence/engine-extension/segmented-agent-iteration-tests.json'
path.write_text(json.dumps(out,indent=2)+'\n')
print(json.dumps({'status':out['status'],'sha256':hashlib.sha256(path.read_bytes()).hexdigest()}))
