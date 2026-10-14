"""Summarize the MMR-19 two-process save identity run into evidence/runtime/two-world-load-matrix.json.

Process 1 (generated_campaign, saveProbe=1) creates and saves two generated worlds; process 2 (generated_save_load,
oracle=<file>) cold-loads them in order. Every value is copied or counted from the two in-game result files. Usage:
  python tools/summarize_world_save_probe.py --save <result1.json> --load <result2.json> [--history <result.json> ...] --out <summary.json>
"""
import argparse
import datetime
import hashlib
import json
import os

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXPECTED_DOCUMENTS = ['SPCultures', 'NPCCharacters', 'WorkshopTypes', 'LocationComplexTemplates', 'Settlements']


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def rel(path):
    path = os.path.abspath(path)
    return os.path.relpath(path, REPO).replace('\\', '/') if path.startswith(REPO) else path


def load(path):
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f)


def info(result, path):
    return {'file': rel(path), 'sha256': sha(path), 'status': result.get('status'), 'pid': result.get('processId'), 'moduleSha256': result.get('moduleSha256')}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--save', required=True)
    ap.add_argument('--load', required=True)
    ap.add_argument('--history', action='append', default=[])
    ap.add_argument('--out', required=True)
    a = ap.parse_args()
    saved, loaded = load(a.save), load(a.load)
    report = saved['outputs'].get('report') or saved['outputs']
    saves = [w['save'] for w in report.get('worlds', []) if 'save' in w]
    loads = (loaded['outputs'].get('report') or loaded['outputs']).get('worlds', [])
    checks = {
        'differentProcesses': saved.get('processId') != loaded.get('processId'),
        'twoWorldsSaved': len(saves) == 2 and all(s.get('saveSucceeded') and not s.get('failures') for s in saves),
        'worldASavedAtSea': len(saves) == 2 and saves[0]['snapshot']['main']['atSea'] is True,
        'worldBSavedOnLand': len(saves) == 2 and saves[1]['snapshot']['main']['atSea'] is False,
        'identityInSaveHeader': len(saves) == 2 and all(all(s['header'].get(k) == v for k, v in s['identity'].items()) for s in saves),
        'sameModuleBothProcesses': saved.get('moduleSha256') == loaded.get('moduleSha256') and bool(saved.get('moduleSha256')),
        'loadUsesThisSaveRun': (loaded.get('args') or {}).get('oracle') == saved['outputs'].get('saveOracle')
                               and [l.get('saveName') for l in loads] == [s.get('saveName') for s in saves],
        'loadedInOrderAThenB': [l.get('worldId') for l in loads] == [s['snapshot']['worldId'] for s in saves],
        'packageNamesHeaderWorld': len(loads) == 2 and all(l.get('packageNamesHeaderWorld') is True for l in loads),
        'identityReadBeforeDeserialize': len(loads) == 2 and all(l.get('gameExistsAtIdentityRead') is False and l.get('packageVerified') is True
                                                              and not any(f.startswith('Header key') for f in l.get('failures', [])) for l in loads),
        'deserialized': len(loads) == 2 and all(l.get('deserialized') is True for l in loads),
        'snapshotsMatchAfterColdLoad': len(loads) == 2 and all(l.get('mismatches') == [] for l in loads),
        'sceneCrcsMatch': len(loads) == 2 and all(l.get('snapshot') and (l.get('savedCampaign') or {}).get('sceneXmlCrc') == l['snapshot']['sceneXmlCrc']
                                                     and l['savedCampaign'].get('navMeshCrc') == l['snapshot']['navMeshCrc']
                                                     and l['snapshot']['navMeshCrc'] == s['snapshot']['navMeshCrc'] for l, s in zip(loads, saves)),
        'generatedXmlReappliedFromPackage': len(loads) == 2 and all([d.get('id') for d in (l.get('savedGameDocuments') or [])] == EXPECTED_DOCUMENTS
                                                                   and all(len(d.get('sha256') or '') == 64 for d in l['savedGameDocuments']) for l in loads),
        'noDatasetBuildDuringLoad': loaded['outputs'].get('datasetBuildsAtStart') == 0 and loaded['outputs'].get('datasetBuildsAtEnd') == 0,
        'timeRunsAfterLoad': len(loads) == 2 and all((l.get('campaignHoursRunAfterLoad') or 0) > 0 for l in loads),
        'noFailures': not loaded['outputs'].get('failures') and all(not s.get('failures') for s in saves),
    }
    summary = {
        'schema': 'mnbl.two-world-load-matrix', 'version': 1, 'ticket': 'MMR-19',
        'generatedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'save': info(saved, a.save), 'load': info(loaded, a.load),
        'history': [{'file': rel(h), 'sha256': sha(h), 'status': load(h).get('status')} for h in a.history],
        'checks': checks, 'passed': all(checks.values()) and saved.get('status') == 'ok' and loaded.get('status') == 'ok',
        'saves': [{k: s.get(k) for k in ('saveName', 'saveSucceeded', 'identity', 'header', 'package', 'travel', 'metadataCollections', 'failures')} for s in saves],
        'savedSnapshots': [s.get('snapshot') for s in saves],
        'loads': [{k: l.get(k) for k in ('worldId', 'saveName', 'gameExistsAtIdentityRead', 'header', 'packageVerified', 'packageNamesHeaderWorld', 'packageManifest', 'package', 'deserialized', 'firstChanceExceptions',
                                         'savedCampaign', 'savedGameDocuments', 'mismatches', 'identityBehaviorEntries', 'campaignHoursRunAfterLoad',
                                         'gameEnded', 'failures', 'error')} for l in loads],
        'loadedSnapshots': [l.get('snapshot') for l in loads],
        'datasetBuilds': {'atStart': loaded['outputs'].get('datasetBuildsAtStart'), 'atEnd': loaded['outputs'].get('datasetBuildsAtEnd')},
        'reproduce': [
            './tools/Build.ps1', './tools/Deploy-Dev.ps1',
            "(the MMR-59 fixtures build/research-env59/w42 and w43 installed with Deploy-SceneSlots)",
            "./tools/Start-OwnedProbe.ps1 -Probe generated_campaign -ProbeTimeoutSeconds 1800 -ArgsJson '{worldCount:2, seed:364610, secondSeed:364611, culture:empire, heroTemplate:spc_legion_of_the_betrayed_leader_0, townPrototype:town_EN2, villagePrototype:village_EN1_3, placement/secondPlacement: build/research-env59/w42|w43/placement.json, catalogReceipt: evidence/runtime/in-game/campaign_catalog-10212.json, holdSeconds:3, gameplayDays:0, saveProbe:1}'",
            './tools/Collect-OwnedProbe.ps1',
            "./tools/Start-OwnedProbe.ps1 -Probe generated_save_load -ProbeTimeoutSeconds 1800 -ArgsJson '{oracle: <outputs.saveOracle of the first run>}'",
            './tools/Collect-OwnedProbe.ps1',
            'python tools/summarize_world_save_probe.py --save evidence/runtime/in-game/generated_campaign-<pid1>.json --load evidence/runtime/in-game/generated_save_load-<pid2>.json --out evidence/runtime/two-world-load-matrix.json'],
    }
    with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(summary, f, indent=1)
    print('passed=%s failing=%s' % (summary['passed'], json.dumps([k for k, v in checks.items() if not v])))


if __name__ == '__main__':
    main()
