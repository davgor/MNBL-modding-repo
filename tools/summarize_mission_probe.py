"""Summarize MMR-60 generated-world mission transition runs into evidence/runtime/mission-transition-probe.json.

Every value is copied or counted from the in-game result files named on the command line; nothing is
re-derived offline. Usage:
  python tools/summarize_mission_probe.py --accepted <result.json> [--history <result.json> ...] --out <summary.json>
"""
import argparse
import datetime
import hashlib
import json
import os

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def rel(path):
    path = os.path.abspath(path)
    return os.path.relpath(path, REPO).replace('\\', '/') if path.startswith(REPO) else path


def load(path):
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f)


def worlds(result):
    report = result['outputs'].get('report') or result['outputs']
    out = []
    for world in report.get('worlds', []):
        if 'missions' in world:
            out.append((world, world['missions']))
    return out


def snap(s):
    if not s:
        return None
    keys = ('position', 'isOnLand', 'atSea', 'face', 'terrainAtPosition', 'patch', 'expectedBattleScene', 'mapHeight', 'camera', 'cameraSettle', 'ships', 'troops')
    return {k: s.get(k) for k in keys}


def mission(m):
    if not m:
        return None
    keys = ('scene', 'module', 'combat', 'terrainType', 'loadSeconds', 'missionTimeAdvanced', 'agents', 'ships', 'teardownSeconds',
            'initializerRecordSource', 'initializerRecord', 'terrainSamples', 'mainAgentPosition', 'screen')
    return {k: m.get(k) for k in keys}


def phase(row):
    return {
        'passed': row.get('passed'), 'failures': row.get('failures', []), 'error': row.get('error'),
        'setup': row.get('setup'), 'mapEvent': row.get('mapEvent'), 'before': snap(row.get('before')), 'mission': mission(row.get('mission')),
        'after': snap(row.get('after')), 'returnDisplacement': row.get('returnDisplacement'), 'returnBound': row.get('returnBound'),
        'sceneMatchesPatch': row.get('sceneMatchesPatch'),
        'edgePatchSamples': row.get('edgePatchSamples'),
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--accepted', required=True)
    ap.add_argument('--history', action='append', default=[])
    ap.add_argument('--out', required=True)
    a = ap.parse_args()
    accepted = load(a.accepted)
    rows = []
    checks = {}
    for index, (world, m) in enumerate(worlds(accepted)):
        support = world.get('mapSupport', {}) if isinstance(world.get('mapSupport'), dict) else {}
        row = {
            'worldIndex': index, 'identity': m.get('identity'), 'contract': m.get('contract'), 'battleIndexMap': m.get('battleIndexMap'),
            'battleIndexMapInstall': support.get('battleSceneSelection'), 'syntheticWar': m.get('syntheticWar'),
            'fingerprintBefore': m.get('fingerprintBefore'), 'fingerprintAfter': m.get('fingerprintAfter'),
            'worldPreserved': m.get('worldPreserved'), 'failures': m.get('failures'),
            'field': phase(m.get('field', {})), 'settlement': phase(m.get('settlement', {})), 'naval': phase(m.get('naval', {})),
        }
        rows.append(row)
        w = 'world%d' % index
        checks[w + 'FieldSceneFromPatch'] = bool(row['field']['passed'] and row['field']['sceneMatchesPatch'])
        checks[w + 'SettlementLocation'] = bool(row['settlement']['passed'])
        checks[w + 'NavalSceneFromPatch'] = bool(row['naval']['passed'] and row['naval']['sceneMatchesPatch'])
        checks[w + 'NavalReturnAtSea'] = bool(row['naval']['after'] and row['naval']['after']['atSea'])
        checks[w + 'WorldPreserved'] = bool(row['worldPreserved'])
    identities = [r['identity']['worldId'] for r in rows if r.get('identity')]
    checks['twoDistinctWorlds'] = len(set(identities)) == 2
    summary = {
        'schema': 'mnbl.mission-transition-evidence', 'version': 1, 'ticket': 'MMR-60',
        'generatedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'accepted': {'file': rel(a.accepted), 'sha256': sha(a.accepted), 'status': accepted.get('status'), 'pid': accepted.get('processId') or accepted.get('pid'),
                     'moduleSha256': accepted.get('moduleSha256')},
        'history': [{'file': rel(h), 'sha256': sha(h), 'status': load(h).get('status')} for h in a.history],
        'checks': checks, 'passed': all(checks.values()) and accepted.get('status') == 'ok',
        'worlds': rows,
        'reproduce': [
            './tools/Build.ps1', './tools/Deploy-Dev.ps1',
            "./tools/Start-OwnedProbe.ps1 -Probe generated_campaign -ProbeTimeoutSeconds 1800 -ArgsJson '{worldCount:2, seed:42, secondSeed:43, culture:empire, heroTemplate:spc_legion_of_the_betrayed_leader_0, townPrototype:town_EN2, villagePrototype:village_EN1_3, placement/secondPlacement: build/research-bootstrap18/final42e|final43e/placement.json, catalogReceipt: evidence/runtime/in-game/campaign_catalog-10212.json, holdSeconds:3, gameplayDays:0, navigationProbe:0, missionProbe:1}'",
            './tools/Collect-OwnedProbe.ps1',
            'python tools/summarize_mission_probe.py --accepted evidence/runtime/in-game/generated_campaign-<pid>.json --out evidence/runtime/mission-transition-probe.json'],
    }
    with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(summary, f, indent=1)
    print('passed=%s %s' % (summary['passed'], json.dumps(checks)))


if __name__ == '__main__':
    main()
