"""Retain a completed native test's evidence and independently verify restoration."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil

ROOT = Path(__file__).resolve().parents[2]

def read(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))

def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()

def retain(run, label):
    if not re.fullmatch(r'r[0-9]+(?:-[a-z0-9]+)*', label):
        raise ValueError('Expected a simple run label')
    receipt = read(run / 'runtime-receipt.json')
    package = Path(receipt['packagePath'])
    restored = []
    for item in read(run / 'restore-files.json'):
        path = Path(item['destination'])
        if item['existed']:
            assert sha(path) == item['originalSha256'] == sha(item['backup']), str(path)
        else:
            assert not path.exists(), str(path)
        restored.append(dict(path=str(path), verified=True, sha256=item['originalSha256']))
    assert receipt['restoredOriginalFiles']
    for filename, field in [('TaleWorlds.Native.dll','patchedSha256'), ('MNBL.BattleScale.dll','moduleSha256'), ('native-pool-manifest.json','manifestSha256')]:
        assert sha(package / filename) == receipt[field], filename
    if 'managedEngineSha256' in receipt:
        assert sha(package / 'TaleWorlds.MountAndBlade.dll') == receipt['managedEngineSha256']
    destination = ROOT / 'evidence/engine-extension' / ('native-pool-run-' + label)
    destination.mkdir(exist_ok=False)
    for path in run.iterdir():
        if path.is_file() and not path.name.startswith('backup-'):
            shutil.copy2(path, destination / path.name)
    pid = receipt.get('pid')
    if pid:
        for path in Path('C:/ProgramData/Mount and Blade II Bannerlord/logs').glob('*_' + str(pid) + '.txt'):
            if not (destination / path.name).exists():
                shutil.copy2(path, destination / path.name)
    for name in ['native-pool-test-profile.json', 'build-receipt.json', 'managed-admission-receipt.json']:
        if (package / name).exists():
            shutil.copy2(package / name, destination / name)
    result = receipt.get('result') or {}
    if result:
        assert result['runId'] == receipt['runId']
    summary = dict(schemaVersion=1, status=receipt['status'], runId=receipt['runId'],
        runtimeVerified=receipt['runtimeVerified'], workload=result.get('workload'),
        restoredFiles=restored, nativeSha256=receipt['patchedSha256'], moduleSha256=receipt['moduleSha256'],
        managedEngineSha256=receipt.get('managedEngineSha256'), packageFiles={p.name:sha(p) for p in package.iterdir() if p.is_file()},
        evidence=[dict(path=str(p), sha256=sha(p)) for p in sorted(destination.iterdir()) if p.is_file()],
        observations={key:result.get(key) for key in ['peakEntities','peakHumans','peakMounts','combatCasualties','missileShots','missileHits',
            'distinctMissileShooters','completedReuseCycles','publicOverflowAttempts','publicSpawnAgentOverflowTested',
            'sustainedWallSeconds','sustainedDurationVerified','frameTiming','resourceCoverage','resourceSamplingError',
            'minimumActiveCombatHumans','allCreatedAgentCount','liveEntitiesAtTeardownStart','clearedTeardownAgents','teardownVerified']},
        limitation='Observed mission-screen intervals include probe and OS overhead; not GPU timing or a constant-population benchmark.')
    resources = result.get('resourceSamples', [])
    combat = [s for s in resources if s['combat']]
    if combat:
        summary['combatResourceRange'] = {key:{'minimum':min(s[key] for s in combat if s.get(key) is not None),
                                             'maximum':max(s[key] for s in combat if s.get(key) is not None)}
            for key in ['activeHumans','activeMounts','totalEntities','secondaryCount','secondaryHighWater','workingSetBytes','privateBytes','cpuCoreEquivalent']
            if any(s.get(key) is not None for s in combat)}
        summary['firstCombatSample'] = combat[0]
        summary['lastCombatSample'] = combat[-1]
    output = ROOT / 'evidence/engine-extension' / ('native-pool-runtime-' + label + '.json')
    if output.exists(): raise FileExistsError(output)
    output.write_text(json.dumps(summary,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({'receipt':str(output),'status':summary['status'],'restoredFiles':len(restored),'observations':summary['observations']}))

if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('run',type=Path)
    parser.add_argument('label')
    args=parser.parse_args()
    retain(args.run,args.label)
