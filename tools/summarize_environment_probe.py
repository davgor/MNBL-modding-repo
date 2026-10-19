"""Summarize an MMR-59 environment rebind run into evidence/runtime/environment-rebind-probe.json.

Every value is copied or counted from the in-game result file; nothing is re-derived offline. Per-sample sailing
tracks are kept (they are short). Usage:
  python tools/summarize_environment_probe.py --accepted <result.json> [--history <result.json> ...] --out <summary.json>
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


def section_ok(env, name):
    return name in env and 'error' not in env[name] and not any(f.startswith(name + ':') for f in env.get('failures', []))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--accepted', required=True)
    ap.add_argument('--history', action='append', default=[])
    ap.add_argument('--out', required=True)
    a = ap.parse_args()
    accepted = load(a.accepted)
    report = accepted['outputs'].get('report') or accepted['outputs']
    worlds = [w for w in report.get('worlds', []) if 'environment' in w]
    envs = [w['environment'] for w in worlds]
    checks = {'twoWorldsProbed': len(envs) == 2}
    if len(envs) == 2:
        checks['patternsDistinct'] = envs[0]['pattern'] != envs[1]['pattern'] and envs[0]['windVector'] != envs[1]['windVector']
    for i, env in enumerate(envs):
        w = 'world%d' % i
        checks[w + 'FieldsBoundToScene'] = section_ok(env, 'contract')
        checks[w + 'WindQueriesReadGeneratedField'] = section_ok(env, 'wind') and len(env['wind'].get('samples', [])) >= 4
        checks[w + 'SnowRainQueriesReadGeneratedField'] = section_ok(env, 'precipitation')
        checks[w + 'StormSpawnAreaFromGeneratedOpenSea'] = section_ok(env, 'stormArea') and env['stormArea'].get('candidates', 0) > 0
        checks[w + 'StormSpawnedAtCandidate'] = section_ok(env, 'storm')
        checks[w + 'ShipSailedBothWays'] = section_ok(env, 'sailing') and all(env['sailing'][leg].get('samplesUsed', 0) >= 5 and env['sailing'][leg].get('displacement', 0) > 1
                                                                               for leg in ('forward', 'back'))
        checks[w + 'WindBoostEntersSpeed'] = section_ok(env, 'sailing') and bool(env['sailing'].get('speedWithoutWindSpreadByTerrain')) \
            and all(v <= 1.001 for v in env['sailing']['speedWithoutWindSpreadByTerrain'].values())
        zones = [s for s in env.get('wind', {}).get('samples', [])[-2:]]
        checks[w + 'WindLayoutTestedByZones'] = len(zones) == 2 and zones[0]['raw'] != zones[1]['raw'] and all(z['raw'] == z['generated'] for z in zones)
    along = [e for e in envs if e.get('pattern') == 'along']
    checks['windMovesShips'] = bool(along) and section_ok(along[0], 'sailing') \
        and along[0]['sailing']['forward']['meanWindBoost'] > along[0]['sailing']['back']['meanWindBoost'] + 0.02 \
        and along[0]['sailing']['forward']['displacementPerHour'] > along[0]['sailing']['back']['displacementPerHour']
    if len(envs) == 2:
        checks['worldSwitchReplacesData'] = section_ok(envs[1], 'worldSwitch') and not envs[1]['worldSwitch'].get('first', False)
        checks['previousWorldReleased'] = section_ok(envs[1], 'previousWorldReleased')
    checks['windBoostMatchesEngineFormula'] = bool(envs) and all(section_ok(e, 'sailing') and all(e['sailing'][leg].get('maxBoostFormulaError', 1) < 1e-4
                                                     for leg in ('forward', 'back')) for e in envs)
    checks['noFailures'] = all(not e.get('failures') for e in envs)
    summary = {
        'schema': 'mnbl.environment-rebind-evidence', 'version': 1, 'ticket': 'MMR-59',
        'generatedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'accepted': {'file': rel(a.accepted), 'sha256': sha(a.accepted), 'status': accepted.get('status'), 'pid': accepted.get('processId'),
                     'moduleSha256': accepted.get('moduleSha256')},
        'history': [{'file': rel(h), 'sha256': sha(h), 'status': load(h).get('status')} for h in a.history],
        'checks': checks, 'passed': all(checks.values()) and accepted.get('status') == 'ok',
        'worlds': [{'worldId': w['worldId'], 'weatherSetup': (w.get('mapSupport') or {}).get('generatedWeather'), 'environment': w['environment']} for w in worlds],
        'reproduce': [
            'python tools/scenebin/build_climate_world.py --world-id mnbl_00059042 --scene-name mnbl_generated_00059042 --heightfield build/heightfield-source42/heightfield.bin --extent 800 --climate along --out build/research-env59/w42',
            'python tools/scenebin/build_climate_world.py --world-id mnbl_00059042 --scene-name mnbl_generated_00059042 --from build/research-env59/w42 --climate along --wind-zones 2 --out build/research-env59/w42z',
            'python tools/scenebin/build_climate_world.py --world-id mnbl_00059043 --scene-name mnbl_generated_00059043 --heightfield build/heightfield-source43/heightfield.bin --extent 640 --climate across --out build/research-env59/w43',
            'python tools/scenebin/build_climate_world.py --world-id mnbl_00059043 --scene-name mnbl_generated_00059043 --from build/research-env59/w43 --climate across --wind-zones 2 --out build/research-env59/w43z',
            './tools/Build.ps1', './tools/Deploy-Dev.ps1',
            "./tools/Deploy-SceneSlots.ps1 -Map 'build/research-env59/w42/mnbl_generated_00059042=SceneObj/mnbl_generated_00059042','build/research-env59/w43/mnbl_generated_00059043=SceneObj/mnbl_generated_00059043'",
            "./tools/Start-OwnedProbe.ps1 -Probe generated_campaign -ProbeTimeoutSeconds 1800 -ArgsJson '{worldCount:2, seed:364610, secondSeed:364611, culture:empire, heroTemplate:spc_legion_of_the_betrayed_leader_0, townPrototype:town_EN2, villagePrototype:village_EN1_3, placement/secondPlacement: build/research-env59/w42z|w43z/placement.json, catalogReceipt: evidence/runtime/in-game/campaign_catalog-10212.json, holdSeconds:3, gameplayDays:0, environmentProbe:1}'",
            './tools/Collect-OwnedProbe.ps1',
            'python tools/summarize_environment_probe.py --accepted evidence/runtime/in-game/generated_campaign-<pid>.json --out evidence/runtime/environment-rebind-probe.json'],
    }
    with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(summary, f, indent=1)
    print('passed=%s failing=%s' % (summary['passed'], json.dumps([k for k, v in checks.items() if not v])))


if __name__ == '__main__':
    main()
