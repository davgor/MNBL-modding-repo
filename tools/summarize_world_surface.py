"""MMR-37: summarize the runtime surface backend's evidence.

Usage: python tools/summarize_world_surface.py SURFACE_RUN_JSON CAMPAIGN_RUN_JSON OFFLINE_JSON OUT_JSON
  SURFACE_RUN_JSON  a world_surface run (bundleA, bundleB: two compiled worlds)
  CAMPAIGN_RUN_JSON a generated_campaign run (two fixture worlds in one process), whose map scenes are GeneratedMapScene
  OFFLINE_JSON      tools/Test-WorldSurface.ps1's evidence
The checks:
  * both runs used the same module build;
  * every world was prepared on a worker thread (the encoding equal to the surface model, else Prepare refuses) and installed and
    constructed on the engine thread (thread ids recorded by the backend); its staging job was deleted; installs use the encoding's
    content-addressed scene name, resolve to its folder, and remove the scene store's folders this process did not use; world A built a
    second time (warm) reused its installed scene;
  * for each world: terrain bounds and the map scene's borders and size (taken from the native scene) against the model; at every
    landmark (compiler settlements, chunk corners and centres, MMR-16 grid points, coast samples) native terrain, combined and ground
    heights equal to the collision reference within 0.001 and the quantized model within 0.031, native normals within 1 degree of the
    model's, Scene and SceneView vertical and oblique rays within 0.1 of the collision reference, and the GeneratedMapScene (IMapScene)
    answers equal to the native ones; both sides of every interior chunk border without height or normal tear; the periodic seam pairs
    equal; a native selection's terrain hit;
  * a later world answers at the previous world's landmark positions with its own heights where they differ (B after A, A after B);
  * a world prepared and then superseded before its completion reached the engine thread was refused at install and construct;
    nothing of it was installed or registered and its staging job was deleted;
  * each world's disposal released everything it held (none outstanding, no failure; scene store entry and name pairing handed to their
    owners), every query through the disposed world and every guarded member of its map scene binding was refused before any native
    call, and the objects were collected; every re-implemented IMapScene member dispatches to GeneratedMapScene;
  * in a campaign, each fixture world's map scene has the current generation, hands its scene and agent renderer to the installed engine
    owners, its guarded ground answers equal max(native terrain, sea datum 0) and its height-and-normal answers the native terrain, and
    after the in-process world switch the previous world's map scene (held on purpose, as a stale reference would be) refuses queries and
    native scene access; no validation failure;
  * the offline contract (model, encoder, encoded seams, lifetime primitives) passed.
"""
import json
import sys


def main():
    surface_path, campaign_path, offline_path, out_path = sys.argv[1:5]
    run = json.load(open(surface_path, encoding='utf-8-sig'))
    campaign = json.load(open(campaign_path, encoding='utf-8-sig'))
    offline = json.load(open(offline_path, encoding='utf-8-sig'))
    o = run['outputs']
    worlds = o.get('worlds') or []
    engine = o.get('engineThread')
    checks = []

    def check(ok, label):
        checks.append({'ok': bool(ok), 'label': label})

    check(run.get('status') == 'ok' and o.get('completed') is True and [w['label'] for w in worlds] == ['A', 'B', 'A (warm)'],
          'the world_surface run finished ok with worlds %s' % ', '.join(w['label'] for w in worlds))
    check(run.get('moduleSha256') and run.get('moduleSha256') == campaign.get('moduleSha256'), 'both in-game runs used one module build (%s)' % (run.get('moduleSha256') or 'MISSING')[:12])
    imap = o.get('interfaceMap') or {}
    check(imap.get('missing') == [] and len(imap.get('expectedGenerated') or []) == 13, 'every re-implemented IMapScene member (%d) dispatches to GeneratedMapScene' % len(imap.get('expectedGenerated') or []))
    for w in worlds:
        label, c = w['label'], w.get('checks') or {}
        surface, install, built = w.get('surface') or {}, w.get('install') or {}, w.get('constructed') or {}
        name = install.get('sceneName', '')
        check(w.get('preparedOnThread') not in (None, engine) and install.get('threadId') == engine and built.get('constructedOnThread') == engine and engine == o.get('probeThread')
              and w.get('jobDeleted') is True and name.startswith('mnbl_world_' + w['worldContentId'][:16]) and str(install.get('resolved', '')).replace('\\', '/').endswith(name + '/scene.xscene') and not w.get('failure'),
              'world %s (%s): prepared on worker thread %s in %.1f s (encoding equal to the model), installed and constructed on the engine thread %s as %s (%s), staging job deleted; %d x %d samples over %s units, heights %s..%s, vertical exaggeration %s' % (
                  label, w['worldContentId'][:12], w.get('preparedOnThread'), w.get('encodeSeconds', 0), engine, name, 'reused the installed scene' if install.get('reused') else 'installed fresh',
                  surface.get('side'), surface.get('side'), surface.get('extent'), surface.get('low'), surface.get('high'), (surface.get('scale') or {}).get('verticalExaggeration')))
        b = c.get('bounds') or {}
        check(b.get('nodesX') == 4 and b.get('nodesY') == 4 and abs(b.get('nodeSize', 0) - surface.get('extent', 0) / 4) < 1e-3 and not any(f.startswith('bounds') or f.startswith('map_scene') for f in c.get('failures') or []),
              'world %s: native terrain 4 x 4 nodes of %s units, height range %s..%s and bounding box agree with the model, and so do the map scene\'s borders and size taken from the native scene' % (label, b.get('nodeSize'), b.get('minHeight'), b.get('maxHeight')))
        rows = c.get('rows') or []
        kinds = {}
        for r in rows:
            kinds[r['kind']] = kinds.get(r['kind'], 0) + 1
        landmark_failures = [f for f in c.get('failures') or [] if ':' in f and not f.split(':')[0].startswith(('vertical_', 'horizontal_', 'seam_', 'selection', 'previous_world', 'bounds', 'map_scene'))]
        worst_collision = max((abs(r['collisionError']) for r in rows), default=None)
        worst_normal = max((max(r['normalVsModelDegrees'], r['normalAtVsModelDegrees']) for r in rows), default=None)
        worst_ray = max((r['vertical']['error'] for r in rows if r['vertical'].get('error') is not None), default=None)
        check(len(rows) == c.get('landmarks') and len(rows) >= 60 and not landmark_failures,
              'world %s: all %d landmarks (%s) agree: native heights equal the collision reference (worst %.2g) within 0.031 of the quantized model, ground masks too, native normals within 1 deg of the model\'s (worst %.3f deg), Scene/SceneView vertical and oblique rays within 0.1 (worst vertical %.2g), and the GeneratedMapScene answers equal the native ones' % (
                  label, len(rows), ', '.join('%s %d' % kv for kv in sorted(kinds.items())), worst_collision or 0, worst_normal or 0, worst_ray or 0))
        borders = c.get('chunkBorders') or []
        border_failures = [f for f in c.get('failures') or [] if f.startswith(('vertical_', 'horizontal_'))]
        check(len(borders) == 36 and not border_failures, 'world %s: no chunk seam tear at %d points across the 6 interior node borders: native heights on both sides are the collision reference (0.001) and native normals are within 1 deg of the model\'s' % (label, len(borders)))
        seams = c.get('seamPairs') or []
        check(len(seams) == 12 and not [f for f in c.get('failures') or [] if f.startswith('seam_')], 'world %s: the %d periodic seam pairs (x and x + logical width) answer the same native height' % (label, len(seams)))
        sel = c.get('selection') or {}
        check(sel.get('terrainHits', 0) >= 1 and not [f for f in c.get('failures') or [] if f.startswith('selection')], 'world %s: the native entity/terrain selection hits the terrain at the collision reference height (%d hit)' % (label, sel.get('terrainHits', 0)))
        if label != 'A':
            p = c.get('previousWorldPositions') or {}
            check(p.get('differing', 0) > 0 and p.get('answeredByThisWorld') == p.get('differing') and not p.get('stale'),
                  'world %s answers at the previous world\'s %d landmark positions with its own heights where the worlds differ (%d of %d), never the previous world\'s' % (label, p.get('checkedPositions', 0), p.get('answeredByThisWorld', 0), p.get('differing', 0)))
        if label == 'A (warm)':
            check(w.get('installedAlready') is True and install.get('reused') is True and install.get('sceneName') == worlds[0]['install']['sceneName'],
                  'world A built again reused its installed scene %s (identical encoding; nothing rewritten)' % install.get('sceneName'))
        after = w.get('afterDispose') or {}
        refused = w.get('refusedAfterDispose') or {}
        collected = w.get('collected') or {}
        ledger = sorted(after.get('ledger') or [], key=lambda e: -e['order'])
        released = [e['resource'] for e in ledger if e.get('released')]
        handed = [e['resource'] for e in ledger if e.get('handedTo')]
        check(after.get('disposed') is True and after.get('outstanding') == 0 and not after.get('disposeFailures') and len(refused) == 14 and all(v != 'ANSWERED' for v in refused.values()) and collected and all(collected.values())
              and set(handed) == {'scene name pairing', 'scene store entry SceneObj/' + name},
              'world %s: disposal released %s latest first and handed the scene store entry and name pairing to their owners; all %d queries through the disposed world and its map scene binding were refused before any native call; the objects were collected' % (
                  label, ', '.join(released), len(refused)))
    first_install = (worlds[0].get('install') or {}) if worlds else {}
    check(o.get('jobsLeft') == 0 and not first_install.get('retainedUnusedScenes') and set(o.get('sceneStoreAfter') or []) == {w['install']['sceneName'] for w in worlds},
          'staging jobs left: %s; the first install removed the scene store\'s unused folders (%s); the store now holds exactly the worlds\' content-addressed scenes (%s)' % (
              o.get('jobsLeft'), ', '.join(first_install.get('sweptUnusedScenes') or []) or 'none', ', '.join(o.get('sceneStoreAfter') or [])))
    late = o.get('lateCallback') or {}
    check(late.get('installRefused') is True and late.get('constructRefused') is True and late.get('folderInstalled') is False and late.get('jobDeleted') is True
          and late.get('registeredAfter') == late.get('registeredBefore'),
          'a world prepared to completion (generation %s) and superseded by world B (generation %s) before its completion reached the engine thread (%s) was refused at install and construct; nothing of it was installed and the registration of its name is unchanged (%s); its staging job was deleted' % (
              late.get('generationC'), late.get('generationB'), late.get('deliveredThrough'), 'already in the startup index of the engine' if late.get('registeredBefore') else 'not registered'))
    gens = o.get('generations') or []
    check(o.get('currentGeneration') == 0 and any('retires 2' in g for g in gens), 'generations: %s' % '; '.join(gens))
    cw = campaign['outputs'].get('worlds') or []
    surfaces = [w.get('mapSurface') or {} for w in cw]
    previous_world = (surfaces[1].get('previousMapWorld') or {}) if len(surfaces) > 1 else {}
    check(campaign.get('status') == 'ok' and len(cw) == 2 and all(s.get('generation') == s.get('currentGeneration') and s.get('queries') for s in surfaces)
          and all(any(e['resource'] == 'scene' and 'MapScreen.OnFinalize' in e['owner'] for e in (s.get('world') or {}).get('ledger') or []) for s in surfaces)
          and previous_world.get('ledger') and all(e.get('handedTo') == e['owner'] and not e['released'] for e in previous_world.get('ledger')) and previous_world.get('outstanding') == 0
          and previous_world.get('disposed') is True
          and surfaces[0].get('previousMap', '').startswith('none') and surfaces[1].get('previousMap', '').startswith('refused') and surfaces[1].get('previousMapHeight') == 'refused'
          and surfaces[1].get('previousMapScene') == 'refused'
          and all(abs(q['mapSceneLand'] - max(q['terrain'], 0)) <= .05 and abs(q['mapSceneSea'] - max(q['terrain'], 0)) <= .05 and abs(q['combined'] - q['terrain']) <= .05 for s in surfaces for q in s['queries'])
          and not [f for w in cw for f in (w.get('validationFailures') or [])],
          'in a campaign (run %s): both fixture worlds\' map scenes have the current generation (%s); the first world\'s map world handed its scene and agent renderer to the installed engine owners when destroyed; guarded ground answers (land, sea) equal max(native terrain, sea datum 0) and height-and-normal answers the native terrain at 9 points each; after the in-process world switch the first world\'s map scene (its world disposed, held on purpose) refused terrain size, height and native scene access (%s); no validation failure' % (
              campaign.get('processId'), ', '.join(str(s.get('generation')) for s in surfaces), surfaces[1].get('previousMap', 'MISSING')[:90] if len(surfaces) > 1 else 'MISSING'))
    check(offline.get('failures') == 0 and offline.get("checks", 0) >= 24, 'the offline contract passed (%s checks: model, pinned encoder, encoded terrain.bin seams, landmarks, model queries, scene names, generation tokens, resource ledger)' % offline.get('checks'))
    summary = {'schema': 'mnbl.evidence.world-surface', 'version': 1, 'ticket': 'MMR-37', 'surfaceRun': surface_path.replace('\\', '/'), 'surfacePid': run.get('processId'),
               'campaignRun': campaign_path.replace('\\', '/'), 'campaignPid': campaign.get('processId'), 'moduleSha256': run.get('moduleSha256'), 'engineThread': engine,
               'interfaceMap': imap, 'sceneStoreBefore': o.get('sceneStoreBefore'), 'sceneStoreAfter': o.get('sceneStoreAfter'), 'jobsLeft': o.get('jobsLeft'),
               'worlds': [{k: w.get(k) for k in ('label', 'worldContentId', 'generation', 'encodeSeconds', 'preparedOnThread', 'installedAlready', 'jobDeleted', 'surface', 'install', 'constructed', 'afterDispose', 'refusedAfterDispose', 'collected')} for w in worlds],
               'lateCallback': late, 'generations': gens, 'campaignMaps': surfaces,
               'checks': checks, 'passed': sum(c['ok'] for c in checks), 'total': len(checks)}
    with open(out_path, 'w', encoding='utf-8', newline='\n') as fh:
        json.dump(summary, fh, indent=1)
        fh.write('\n')
    for c in checks:
        print(('ok    ' if c['ok'] else 'FAIL  ') + c['label'])
    print('%d/%d checks passed' % (summary['passed'], summary['total']))
    sys.exit(0 if summary['passed'] == summary['total'] else 1)


if __name__ == '__main__':
    main()
