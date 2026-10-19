"""Summarize MMR-42 in game into evidence/world/mission-scenes-report.json.

Runs:
  --fixture   generated_campaign on the two fixture worlds with missionProbe=1 and missionScenesProbe=1 (and the settlement and startup
              fixtures): each world's battle map built by the resolver, every generated settlement's live location scenes against the
              mission contracts and the engine's scene lookup, a field, a settlement and a naval mission entered with the scene the generated
              patch names, and the mismatch guard's record of every battle scene choice
  --seam      generated_campaign on the periodic fixture with wrapProbe=1 and missionScenesProbe=1: the seam battle's patch at the main party
              and at its other image, the scene the model opened against the patch, and the short-way encounter direction
Every value is copied or counted from the results. Usage:
  python tools/summarize_mission_scenes.py --fixture F --seam S --out OUT [--history H ...]
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


def source(path):
    run = load(path)
    return dict(file=rel(path), sha256=sha(path), processId=run['processId'], probe=run['probe'], status=run['status'], moduleSha256=run.get('moduleSha256'), args=run.get('args'))


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    for name in ('fixture', 'seam', 'out'):
        ap.add_argument('--' + name, required=True)
    ap.add_argument('--history', action='append', default=[])
    a = ap.parse_args()
    fixture, seam = load(a.fixture), load(a.seam)
    catalogue_path = os.path.join(REPO, 'worldgen/policies/mission-scenes.json')
    catalogue = load(catalogue_path)
    catalogue_sha = sha(catalogue_path)
    worlds = []
    for run_name, run in (('fixture', fixture), ('seam', seam)):
        for w in run['outputs']['worlds']:
            bs = (w.get('mapSupport') or {}).get('battleSceneSelection') or {}
            ms = w.get('missionScenes') or {}
            guard = w.get('missionSceneGuard') or {}
            missions = w.get('missions') or {}
            phases = {}
            for k in ('field', 'settlement', 'naval'):
                ph = missions.get(k) or {}
                if ph:
                    phases[k] = dict(scene=(ph.get('mission') or {}).get('scene'), sceneMatchesPatch=ph.get('sceneMatchesPatch'),
                                     guard=(ph.get('mismatchGuard') or {}).get('agrees'), expected=((ph.get('before') or {}).get('expectedBattleScene') or {}).get('scene'),
                                     rule=((ph.get('before') or {}).get('expectedBattleScene') or {}).get('rule'), terrainAtPosition=(ph.get('before') or {}).get('terrainAtPosition'),
                                     patch=(ph.get('before') or {}).get('patch'))
            seam_row = (w.get('wrap') or {}).get('seamEncounter') or {}
            worlds.append(dict(
                run=run_name, worldId=w.get('worldId'),
                battleMap=dict((k, bs.get(k)) for k in ('status', 'sceneTableSha256', 'catalogueSha256', 'width', 'height', 'sha256', 'landCells', 'landDeterministic', 'seaTerrainChecks',
                                                        'seaDeterministic', 'seaOwnTerrain', 'preferredCells', 'distinctCells', 'cellsByTier', 'cellsByClass', 'cellsByGeography',
                                                        'biomeSource', 'periodic', 'buildMilliseconds', 'navigationSets')),
                settlements=[dict(id=s['id'], kind=s['kind'], port=s['port'], problems=s['contractProblems'], references=s.get('references'),
                                  scenes={k: v for k, v in s['locations'].items() if k in ('center', 'lordshall', 'port', 'village_center', 'hideout_center')}) for s in ms.get('settlements') or []],
                sceneReferences=ms.get('sceneReferences'), engineMissing=ms.get('engineMissing'),
                guard=dict(mismatches=guard.get('mismatches'), observations=guard.get('observations')),
                missions=phases, missionFailures=missions.get('failures'),
                seam=dict((k, seam_row.get(k)) for k in ('battleStart', 'encounterDirection', 'missionScene', 'afterReturn')) if seam_row else None))
    fixture_worlds = [w for w in worlds if w['run'] == 'fixture']
    seam_world = next((w for w in worlds if w['run'] == 'seam'), None)
    kinds = {}
    for w in worlds:
        for s in w['settlements']:
            kinds.setdefault(s['kind'] + (' harbour' if s['kind'] == 'village' and s['port'] else ''), []).append(s['id'])
    sm = (seam_world or {}).get('seam') or {}
    seam_scene = sm.get('missionScene') or {}
    patch, image = seam_scene.get('patch') or {}, seam_scene.get('imagePatch') or {}
    direction = sm.get('encounterDirection') or {}
    dir_ok = False
    if direction.get('patchEncounterDir') and direction.get('expectedPeriodic'):
        d, e = direction['patchEncounterDir'], direction['expectedPeriodic']
        dir_ok = d[0] * e[0] + d[1] * e[1] >= 0.9
    checks = dict(
        runsOk=fixture['status'] == 'ok' and seam['status'] == 'ok',
        battleMapsFromCatalogue=all(w['battleMap']['status'] == 'installed' and w['battleMap']['sceneTableSha256'] == catalogue['battleTable']['sha256']
                                    and w['battleMap']['catalogueSha256'] == catalogue_sha for w in worlds),
        everyCellDeterministic=all(w['battleMap']['landCells'] == w['battleMap']['landDeterministic'] and w['battleMap']['seaTerrainChecks'] == w['battleMap']['seaDeterministic'] for w in worlds),
        fieldAndNavalBattlesTakeThePatchScene=all(w['missions'].get(k, {}).get('sceneMatchesPatch') is True and w['missions'].get(k, {}).get('guard') is True
                                                  for w in fixture_worlds for k in ('field', 'naval')) and len(fixture_worlds) == 2,
        settlementLocationsEntered=all(w['missions'].get('settlement', {}).get('scene') for w in fixture_worlds),
        guardSawEveryBattleWithoutMismatch=all(w['guard']['mismatches'] == 0 and len(w['guard']['observations'] or []) >= 2 for w in fixture_worlds)
                                         and seam_world is not None and seam_world['guard']['mismatches'] == 0 and len(seam_world['guard']['observations'] or []) >= 1,
        settlementScenesMeetContracts=all(not s['problems'] for w in worlds for s in w['settlements']) and all(not w['engineMissing'] for w in worlds)
                                      and all(k in kinds for k in ('town', 'castle', 'village', 'village harbour', 'hideout')),
        seamCanonicalPatch=bool(patch) and patch.get('index') == image.get('index') and patch.get('coordinates') == image.get('coordinates'),
        seamSceneIsThePatchScene=bool(seam_scene.get('guard')) and seam_scene['guard'].get('agrees') is True and seam_scene['guard'].get('opened') == seam_scene.get('scene'),
        seamShortWayDirection=dir_ok,
        seamMapPeriodic=bool(seam_world and seam_world['battleMap'].get('periodic')))
    summary = dict(
        schema='mnbl.evidence.mission-scenes', version=1, ticket='MMR-42',
        generated=datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0).isoformat(),
        catalogue=dict(file='worldgen/policies/mission-scenes.json', sha256=catalogue_sha, sceneTableSha256=catalogue['battleTable']['sha256']),
        sources=dict(fixture=source(a.fixture), seam=source(a.seam), history=[source(h) for h in a.history]),
        checks=checks, passed=all(checks.values()), settlementKinds={k: len(v) for k, v in sorted(kinds.items())}, worlds=worlds)
    with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(summary, f, indent=1)
        f.write('\n')
    print('wrote', a.out, 'passed', summary['passed'], {k: v for k, v in checks.items() if not v})


if __name__ == '__main__':
    main()
