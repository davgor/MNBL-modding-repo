"""Independent package review, no-engine ABI rerun and inert runner replay."""
from pathlib import Path
import hashlib,json,shutil,subprocess
ROOT=Path(__file__).resolve().parents[2]
def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
files={
 'evidence/engine-extension/managed-element-host-offline-tests.json':'df62ab131f51e3a92ae30befd34ec79f1beee3df9192871b7036a6ba4a6769c2',
 'evidence/engine-extension/managed-element-runner-offline-tests.json':'ee3e134e6a3b4ed0eb937bbbb1ea274daa96f5b2d782836ea9ec444dbc09801e',
 'build/element-probe-host/package/element-probe-profile.json':'16a6ab94d91af853c892fa9b6443569bc76def76b84bc81dff4f2a99f93ddf14'}
for p,h in files.items():assert digest(ROOT/p)==h,p
host=json.loads((ROOT/next(iter(files))).read_text(encoding='utf-8-sig'))
profile_path=ROOT/'build/element-probe-host/package/element-probe-profile.json'
profile=json.loads(profile_path.read_text(encoding='utf-8-sig'))
pins={x['path']:x['sha256'] for x in profile['sources']+profile['providers']}
for file in profile['files']:pins[str(profile_path.parent/file['name'])]=file['sha256']
test=ROOT/'build/element-probe-host/ManagedElementHostTests.exe';pins[str(test)]=host['testExecutableSha256']
mock=ROOT/'tests/AgentStorage/managed_host_runner_tests.ps1'
mock_receipt=json.loads((ROOT/'evidence/engine-extension/managed-element-runner-offline-tests.json').read_text(encoding='utf-8-sig'))
assert digest(mock)==mock_receipt['sources']['tests'];pins[str(mock)]=digest(mock)
for p,h in pins.items():assert digest(p)==h,p
native=profile_path.parent/'MNBL.NativeElementProbe.dll'
run=subprocess.run([str(test),str(native),digest(native)],cwd=ROOT,text=True,capture_output=True,timeout=60)
assert run.returncode==0 and 'PASS managed private probe host: 44 checks;' in run.stdout,(run.stdout,run.stderr)
pwsh=shutil.which('pwsh');assert pwsh
mock_text=mock.read_text()
def quote(s):return "'"+str(s).replace("'","''")+"'"
# Execute the original test logic in a fresh PowerShell process, substituting
# only its source-location variables and its output receipt filename.
mock_text=mock_text.replace('$PSScriptRoot',quote(mock.parent)).replace('$PSCommandPath',quote(mock))
mock_text=mock_text.replace('managed-element-runner-offline-tests.json','managed-element-runner-review-replay.json')
replay=subprocess.run([pwsh,'-NoProfile','-NonInteractive','-Command',mock_text],cwd=ROOT,text=True,capture_output=True,timeout=120)
assert replay.returncode==0 and 'PASS 13 actual-runner inert scenarios' in replay.stdout,(replay.stdout,replay.stderr)
replay_path=ROOT/'evidence/engine-extension/managed-element-runner-review-replay.json'
replayed=json.loads(replay_path.read_text(encoding='utf-8-sig'))
assert [c['scenario'] for c in replayed['cases']]==[c['scenario'] for c in mock_receipt['cases']]
assert all(c['expectedFailure']==c['observedFailure'] for c in replayed['cases'])
preflight=subprocess.run([pwsh,'-NoProfile','-NonInteractive','-File',str(ROOT/'tools/battle-scale/Test-ElementProbeInGame.ps1'),'-Package',str(profile_path.parent),'-PreflightOnly'],cwd=ROOT,text=True,capture_output=True,timeout=30)
assert preflight.returncode==0 and 'Private probe preflight passed.' in preflight.stdout,(preflight.stdout,preflight.stderr)
for p,h in pins.items():assert digest(p)==h,p
for p,h in files.items():assert digest(ROOT/p)==h,p
out={'schemaVersion':1,'status':'accepted-private-probe-package-for-one-controlled-runtime-check',
     'reviewedArtifacts':files,'sources':pins,'reviewerSha256':digest(__file__),
     'profileSha256':digest(profile_path),'offlineAbiReplay':{'exitCode':run.returncode,'stdout':run.stdout,'stderr':run.stderr},
     'inertRunnerReplay':{'exitCode':replay.returncode,'stdout':replay.stdout,'stderr':replay.stderr,'path':str(replay_path),'sha256':digest(replay_path)},
     'realReadOnlyPreflight':{'exitCode':preflight.returncode,'stdout':preflight.stdout,'stderr':preflight.stderr},
     'manualReview':['Arming exists only in the owned child environment; exact GUID, PID, process start, executable, host hash, freshness and exclusive claim are checked. Ordinary launches remain inert.',
                    'Host waits for initial menu plus monotonic3seconds and rejects an existing Mission. It calls only the reviewed private-element probe, validates all counters/canaries and requests normal QuitGame.',
                    'Native library is hash-checked under read-only file sharing, loaded by absolute path with DLL-directory/System32 search, verified by returned module path and fixed exports; all unmanaged output memory and module references release after return.',
                    'Runner waits for every Bannerlord session before each deployment/restore and immediately before launch; it retains one local lease and controls only exact PID/start/path it launched.',
                    'Only three private module files are deployed. Backup intent precedes writes; readback hashes and partial-copy tracking permit rollback while foreign edits are retained. Native/managed engine binaries are not replaced.',
                    'Native result success, normal process exit and restoration are separate gates. Watchdog/nonzero exit and forced timeout cleanup cannot become a pass.'],
     'openFindings':[],
     'scope':['Runtime permission here covers the private unloaded-agent constructor/growth/exhaustion/reuse/cleanup prerequisite only, at main menu.',
              'All five native pools remain private; no Mission pointer, live soldier, hook installation, battle capacity, renderer or combat is tested.',
              'Actual game shutdown behavior and positive module binding remain unobserved until the runtime check.',
              'Fresh no-other-game checks remain mandatory for the actual launch.'],
     'gameLaunched':False,'installedFilesChanged':False,'liveSoldierGrowthApproved':False}
target=ROOT/'evidence/engine-extension/managed-element-host-independent-review.json'
assert not target.exists();target.write_text(json.dumps(out,indent=2)+'\n')
print(json.dumps({'status':out['status'],'sha256':digest(target),'profileSha256':digest(profile_path)}))
