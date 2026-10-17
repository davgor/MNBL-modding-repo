"""Summarize MMR-40 in game into evidence/world/settlement-entities.json.

Two runs: world_surface (production scenes of bundles A and B, and warm A, inspected: outputs.worlds[*].settlements) and generated_campaign
with settlementEntityProbe=1 on the factory-built fixture (outputs.worlds[0].settlementEntities, worlds[1].settlementEntityTeardown). Every
value is copied or counted from the results. Usage:
  python tools/summarize_settlement_entities.py --surface <world_surface result> --campaign <generated_campaign result> --out <summary.json>
      [--history <result.json> ...]
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
    return dict(file=rel(path), sha256=sha(path), processId=run['processId'], probe=run['probe'], status=run['status'], moduleSha256=run.get('moduleSha256'))


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--surface', required=True)
    ap.add_argument('--campaign', required=True)
    ap.add_argument('--history', action='append', default=[])
    ap.add_argument('--out', required=True)
    a = ap.parse_args()
    surface, campaign = load(a.surface), load(a.campaign)
    worlds = []
    for w in surface['outputs']['worlds']:
        s = w.get('settlements') or {}
        counts = s.get('counts') or {}
        worlds.append(dict(label=w.get('label'), settlements=s.get('settlements'), found=s.get('found'), counts=counts, children=s.get('children'),
                           fortifications=counts.get('Town', 0) + counts.get('Castle', 0), walls=s.get('walls'), wallEngines=s.get('wallEngines'),
                           levelChecks=s.get('levelChecks'), levelSamples=s.get('levelSamples'),
                           blockadeScripts=s.get('blockadeScripts'), blockadeShips=s.get('blockadeShips'), landFaceChecks=s.get('landFaceChecks'), seaFaceChecks=s.get('seaFaceChecks'),
                           positionChecks=s.get('positionChecks'), worstOffset=s.get('worstOffset'), rootEntities=s.get('rootEntities'),
                           authoredRootEntities=s.get('expectedRootEntities'), engineRootEntities=s.get('engineRootEntities'), layoutSha256=s.get('layoutSha256'),
                           problems=s.get('problems'), reportPath=s.get('reportPath'), passed=s.get('passed'), failures=s.get('failures'),
                           surfaceChecksPassed=(w.get('checks') or {}).get('passed'), waterPassed=(w.get('water') or {}).get('passed')))
    cw = campaign['outputs']['worlds']
    entities = cw[0].get('settlementEntities') or {}
    rows = entities.get('settlements') or []
    teardown = cw[1].get('settlementEntityTeardown') if len(cw) > 1 else None
    fortifications = [r for r in rows if r['kind'] in ('town', 'castle')]
    towns = [r for r in rows if r['kind'] == 'town']
    harbours = [r for r in rows if r['kind'] == 'village' and r.get('dropoffs')]
    cache = entities.get('siegePrefabCache') or {}
    by = {w['label']: w for w in worlds}
    spawns = lambda r: r.get('siegeSpawns') or {}
    checks = dict(
        everySettlementHasItsEntity=all(w['found'] == w['settlements'] and w['settlements'] > 0 for w in worlds),
        sceneChildrenAtLayoutPositions=all(w['worstOffset'] is not None and w['worstOffset'] <= 2e-3 for w in worlds),
        blockadeScriptOnEveryPortTownWithArc=all(w['blockadeScripts'] and w['blockadeScripts'] > 0 for w in worlds),
        everyBlockadeShipOverSea=all(w['blockadeShips'] == 10 * w['blockadeScripts'] for w in worlds),
        wallsAndWallEnginesPerLevel=all(w['walls'] == 6 * w['fortifications'] and w['wallEngines'] == 12 * w['fortifications'] and w['fortifications'] > 0 for w in worlds),
        levelMasksResolved=all(w['levelChecks'] and w['levelChecks'] > 0 for w in worlds),
        markersOverTheirNavigationDomain=all(w['passed'] for w in worlds),
        rootCensusExact=all(w['rootEntities'] == w['authoredRootEntities'] + len(w['engineRootEntities'] or []) for w in worlds),
        sameEngineRootsInEveryWorld=len({tuple(w['engineRootEntities'] or []) for w in worlds}) == 1,
        warmWorldSameAsFirst=('A' in by and 'A (warm)' in by and by['A']['rootEntities'] == by['A (warm)']['rootEntities'] and by['A']['layoutSha256'] == by['A (warm)']['layoutSha256']),
        campaignVisualsBound=bool(rows) and all(r.get('visualBound') for r in rows),
        noVanillaPrefab=all(r.get('made') != 'prefab' for r in rows),
        runtimeFortificationBuilt=any(r.get('made') == 'runtime' for r in fortifications),
        gatesAreTheLayout=all('failure' not in r for r in fortifications),
        besiegerCampsFromChildren=bool(fortifications) and all(r.get('besiegerCampPositions') == [1, 1] for r in fortifications),
        siegeSpawnsFromChildren=bool(fortifications) and all(spawns(r).get('attackerRanged') == 4 and spawns(r).get('batteringRam') == 1 and spawns(r).get('siegeTower') == 2 for r in fortifications),
        wallSectionsCachedForCurrentLevel=bool(fortifications) and all(spawns(r).get('breachableWallsAllLevels') == 6 and spawns(r).get('defenderRangedAllLevels') == 12
                                                                         and spawns(r).get('breachableWallsCurrentLevel') == 2 and spawns(r).get('defenderRangedCurrentLevel') == 4 for r in fortifications),
        portTownsHavePortsAndBlockadeScripts=bool(towns) and all(r.get('hasPort') and r.get('blockadeScript') for r in towns),
        blockadeShipsOnWater=bool(towns) and all(r.get('blockadeShips') == 10 and r.get('blockadeShipsOnWater') == 10 for r in towns),
        warSailsTookTheDropoff=bool(harbours) and all(r.get('hasPort') for r in harbours),
        oneIconVisibleWithTheCampaignLevels=all(r.get('iconVisible') or r.get('iconHiddenBecause') for r in rows) and any(r['kind'] == 'village' and r.get('iconVisible') for r in rows),
        siegePrefabCacheAddedAndTaken=cache.get('source') == 'added' and cache.get('inScene') is True and cache.get('takenByMapScreen') is True,
        teardownLeavesNothing=teardown is not None and not teardown.get('previousVisualsLeft') and teardown.get('currentVisuals') == teardown.get('currentSettlements') and teardown.get('currentSettlements', 0) > 0
                              and teardown.get('unboundVisuals') == 0 and not teardown.get('entitiesLeft') and not teardown.get('runtimeListedLeft') and not teardown.get('missingBakedLeft') and not teardown.get('prefabsLeft'),
        runsOk=surface['status'] == 'ok' and campaign['status'] == 'ok')
    summary = dict(
        schema='mnbl.evidence.settlement-entities', version=2, ticket='MMR-40',
        generated=datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0).isoformat(),
        sources=dict(surface=source(a.surface), campaign=source(a.campaign), history=[source(h) for h in a.history]),
        checks=checks, passed=all(checks.values()),
        production=worlds,
        campaign=dict(world=cw[0].get('worldId'), builder=entities.get('builder'), baked=entities.get('baked'), runtime=entities.get('runtime'), fixturePrefabs=entities.get('fixturePrefabs'),
                      siegePrefabCache=cache, missingBakedEntities=entities.get('missingBakedEntities'), settlements=rows, rootEntities=entities.get('rootEntities')),
        teardown=teardown)
    with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(summary, f, indent=2)
        f.write('\n')
    print('wrote', a.out, 'passed', summary['passed'], {k: v for k, v in checks.items() if not v})


if __name__ == '__main__':
    main()
