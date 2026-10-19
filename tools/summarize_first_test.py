"""MMR-56: summarizes the first_test_profile probe (evidence/runtime/in-game/first_test_profile-<pid>.json) into
worldgen/validation/first-test-measurements.json: the F03 first-test profile measured end to end through the New Campaign path (each phase's
time and what can be split from it, the processes' and the game's memory, the store's disk, frames and the simulation rate, the save and its
load), the hardware, settings and build that ran it, and A against B from the same build (geography and the in-engine route matrix). Every
check is recomputed from the run's outputs; what stays unresolved (a frozen start, a long frame) is listed under findings. User paths are
replaced (<home>, <repo>).
Usage: python tools/summarize_first_test.py <pid> [<pid> ...] [--budget]
  the first pid is the reported run; with --budget every pid's measurements are merged into worldgen/policies/one-shot-budget.json (each run
  once, by pid): generation to review for every generated seed, the first campaign start for runs whose navigation caches were built, and the
  provisional limits recomputed from all merged measurements.
"""
import base64
import hashlib
import json
import math
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(REPO, 'worldgen', 'validation', 'first-test-measurements.json')
BUDGET = os.path.join(REPO, 'worldgen', 'policies', 'one-shot-budget.json')
STEPS = ('Admit', 'VerifyData', 'VerifyRuntime', 'BuildWorld', 'PromotePackage', 'StartCampaign', 'PostBuild', 'BindIdentity', 'PublishSave')
JOB_PHASES = ('ValidatingRequest', 'GeneratingSource', 'Compiling', 'ValidatingData', 'BuildingScene', 'RegisteringCampaign', 'ValidatingRuntime')
ROLES = {'request': 'worldbridge --new-request', 'generate': 'worldbridge --generate (supervises the producer)', 'bundle': 'worldbridge --bundle (reads the source, converts, writes the bundle)'}
FROZEN = 5.0      # a start gap this long is reported as the game not responding
FRAME_LIMIT = 0.5  # MMR-43's responsiveness rule while generating


def load(pid):
    path = os.path.join(REPO, 'evidence', 'runtime', 'in-game', 'first_test_profile-%s.json' % pid)
    with open(path, encoding='utf-8-sig') as f:
        return path, json.load(f)


def scrub(x):
    roots = sorted({REPO, REPO.replace('\\', '/'), REPO.replace('/', '\\')}, key=len, reverse=True)
    home = re.compile(r'[A-Za-z]:[\\/]+Users[\\/]+[^\\/"]+', re.I)
    if isinstance(x, str):
        for root in roots:
            x = x.replace(root, '<repo>')
        return home.sub('<home>', x)
    if isinstance(x, list):
        return [scrub(v) for v in x]
    if isinstance(x, dict):
        return {k: scrub(v) for k, v in x.items()}
    return x


def mb(b):
    return None if b is None else round(b / 1048576.0, 1)


def sec(ms):
    return None if ms is None else ms / 1000.0


def generation(world):
    """One world's generation through the setup screen: the wait, the job's phases and what can be split from them."""
    w = world or {}
    gen, job = w.get('generate') or {}, w.get('job') or {}
    art = job.get('artifacts') or {}
    t = {x['phase']: x.get('seconds') for x in job.get('timings') or [] if x.get('seconds') is not None}
    phases = {p: t[p] for p in JOB_PHASES if p in t}
    procs = {p.get('role'): p for p in art.get('processes') or []}
    runs = [r for r in gen.get('producerRuns') or [] if 'error' not in r]
    bundle = art.get('bundleResult') or {}
    generate_receipt, bundle_receipt = procs.get('generate') or {}, procs.get('bundle') or {}
    producer_seconds = sum(sec(r.get('elapsedMilliseconds')) or 0 for r in runs) if runs else None
    split = {
        'producerSeconds': producer_seconds,
        'bridgeAroundProducerSeconds': None if producer_seconds is None or generate_receipt.get('elapsedMilliseconds') is None else round(sec(generate_receipt['elapsedMilliseconds']) - producer_seconds, 3),
        'bundleReadAndConvertSeconds': bundle.get('compileSeconds'),
        'bundleWriteSeconds': None if bundle.get('wallSeconds') is None or bundle.get('compileSeconds') is None else round(bundle['wallSeconds'] - bundle['compileSeconds'], 3),
        'sceneEncodeSeconds': art.get('prepareSeconds'),
        'sceneInstallSeconds': None if phases.get('BuildingScene') is None or art.get('prepareSeconds') is None else round(phases['BuildingScene'] - art['prepareSeconds'], 3),
        'previewAndSummarySeconds': None if gen.get('seconds') is None or not phases else round(gen['seconds'] - sum(phases.values()), 3),
    }
    return {'seed': w.get('seed'), 'state': gen.get('state'), 'confirmToReviewSeconds': gen.get('seconds'), 'jobPhases': phases, 'split': split,
            'processes': [{'process': ROLES.get(r, r), 'elapsedSeconds': sec(p.get('elapsedMilliseconds')), 'peakWorkingSetMB': mb(p.get('peakWorkingSetBytes')), 'exitCode': p.get('exitCode')} for r, p in procs.items()],
            'producer': [{'run': r.get('run'), 'elapsedSeconds': sec(r.get('elapsedMilliseconds')), 'peakJobMemoryMB': mb(r.get('peakJobMemoryBytes')), 'processes': r.get('totalProcesses')} for r in runs],
            'bundleExisted': bundle.get('existed'), 'bundleMB': mb((w.get('geography') or {}).get('bundleBytes')),
            'frames': gen.get('frames'), 'peakJobDirectoryMB': mb(gen.get('peakJobBytes')), 'jobDirectoryAtReviewMB': mb(w.get('jobBytes'))}


def activation(start):
    """The first campaign start: the activation's steps, the navigation caches, the time character creation was on screen (the player's),
    and the frames through it."""
    s = start or {}
    host = s.get('host') or {}
    steps = {k: (host.get(k) or {}).get('seconds') for k in STEPS if isinstance(host.get(k), dict)}
    sc = host.get('StartCampaign') or {}
    caches = sc.get('navigationCaches') or {}
    rows = [{'navigationType': c.get('navigationType'), 'source': c.get('source'), 'generateSeconds': sec(c.get('generateMilliseconds')), 'deserializeSeconds': sec(c.get('deserializeMilliseconds'))} for c in caches.get('caches') or []]
    work = round(sum(v for v in steps.values() if v), 3)
    creation = s.get('creationSeconds')
    frames = s.get('frames') or {}
    gaps = s.get('longGaps') or []
    cache_seconds = sec(caches.get('elapsedMilliseconds'))
    return {'steps': steps, 'workSeconds': work, 'creationOnScreenSeconds': creation,
            'workWithoutCreationSeconds': None if creation is None else round(work - creation, 3),
            'startCampaignBesideCachesAndCreationSeconds': None if steps.get('StartCampaign') is None or cache_seconds is None or creation is None else round(steps['StartCampaign'] - cache_seconds - creation, 3),
            'navigationCaches': {'status': caches.get('status'), 'cacheStatus': caches.get('cacheStatus'), 'elapsedSeconds': cache_seconds, 'caches': rows},
            'initialSaveSeconds': (host.get('PublishSave') or {}).get('saveSeconds'),
            'startToMapSeconds': s.get('mapAfter'), 'state': s.get('state'),
            'frames': frames, 'longGaps': gaps, 'frozenSeconds': round(sum(g.get('seconds') or 0 for g in gaps if (g.get('seconds') or 0) >= FROZEN), 3),
            'longestGap': max(gaps, key=lambda g: g.get('seconds') or 0) if gaps else None}


def geography_compare(a, b):
    ga, gb = a or {}, b or {}
    fa, fb = ga.get('files') or {}, gb.get('files') or {}
    differ = lambda k: fa.get(k) is not None and fb.get(k) is not None and fa.get(k) != fb.get(k)
    out = {'heightsDiffer': differ('height-q16.bin'), 'masksDiffer': differ('masks.bin'), 'cellsDiffer': differ('cells.bin'),
           'coastlineDiffers': ga.get('coastlineSha256') is not None and gb.get('coastlineSha256') is not None and ga.get('coastlineSha256') != gb.get('coastlineSha256'),
           'landCells': [ga.get('landCells'), gb.get('landCells')], 'coastCells': [ga.get('coastCells'), gb.get('coastCells')],
           'landComponents': [ga.get('landComponents'), gb.get('landComponents')], 'largestLandComponent': [ga.get('largestLandComponent'), gb.get('largestLandComponent')],
           'compilerLandComponents': [(ga.get('compilerCounts') or {}).get('landComponents'), (gb.get('compilerCounts') or {}).get('landComponents')],
           'bundleMB': [mb(ga.get('bundleBytes')), mb(gb.get('bundleBytes'))]}
    if ga.get('classes') and gb.get('classes') and ga.get('grid') == gb.get('grid'):
        ca, cb = base64.b64decode(ga['classes']), base64.b64decode(gb['classes'])
        land = lambda c: c in (0, 1)
        same = sum(1 for x, y in zip(ca, cb) if x == y)
        disagree = sum(1 for x, y in zip(ca, cb) if land(x) != land(y))
        out.update({'cells': len(ca), 'sameClassCells': same, 'sameClassFraction': round(same / float(len(ca)), 4),
                    'landSeaDisagreeCells': disagree, 'landSeaDisagreeFraction': round(disagree / float(len(ca)), 4)})
    return out


def routes_compare(a, b):
    ra, rb = a or {}, b or {}
    sa, sb = ra.get('summary') or {}, rb.get('summary') or {}
    return {'matrixDiffers': ra.get('matrixSha256') is not None and rb.get('matrixSha256') is not None and ra.get('matrixSha256') != rb.get('matrixSha256'),
            'navmeshDiffers': ra.get('navmeshSha256') is not None and rb.get('navmeshSha256') is not None and ra.get('navmeshSha256') != rb.get('navmeshSha256'),
            'reportsAreTheJobs': [ra.get('reportIsTheJobs'), rb.get('reportIsTheJobs')],
            'routes': [ra.get('routes'), rb.get('routes')], 'reachable': [sa.get('reachableRoutes'), sb.get('reachableRoutes')], 'forbidden': [sa.get('forbiddenRoutes'), sb.get('forbiddenRoutes')],
            'byType': [ra.get('byType'), rb.get('byType')], 'reachableLength': [ra.get('reachableLength'), rb.get('reachableLength')],
            'faces': [ra.get('faces'), rb.get('faces')], 'facesByTerrainType': [ra.get('facesByTerrainType'), rb.get('facesByTerrainType')],
            'routesAgreeWithStatic': [sa.get('routesAgree'), sb.get('routesAgree')], 'playabilityRoutes': [ra.get('playabilityRoutes'), rb.get('playabilityRoutes')]}


def seed_row(world):
    w = world or {}
    job = w.get('job') or {}
    art = job.get('artifacts') or {}
    gen = w.get('generate') or {}
    summary = art.get('generateSummary') or {}
    return {'seed': w.get('seed'), 'state': gen.get('state'), 'seconds': gen.get('seconds'), 'error': gen.get('error'), 'attempts': art.get('attempts') or summary.get('attempts'),
            'attemptSeed': art.get('attemptSeed'), 'reasons': summary.get('reasons'), 'worldContentId': art.get('worldContentId'), 'jobError': job.get('error')}


def measure(result):
    o = result.get('outputs') or {}
    a = o.get('A') or {}
    mp, save, ld = a.get('map') or {}, a.get('save') or {}, a.get('load') or {}
    gen_a, act = generation(a), activation(a.get('start'))
    loader = ld.get('loader') or {}
    return {
        'generate': gen_a, 'generateB': generation(o.get('B')),
        'startCampaign': act,
        'map': {k: mp.get(k) for k in ('Stop', 'UnstoppablePlay', 'UnstoppableFastForward', 'parties', 'partiesAfter', 'settlements', 'playerIgnoredDays')},
        'save': {'seconds': save.get('seconds'), 'published': save.get('published'), 'bytes': save.get('bytes')},
        'load': {'loaderSeconds': ld.get('loaderSeconds'), 'toMapSeconds': ld.get('mapSeconds'), 'loaded': ld.get('loaded'), 'loaderPhases': loader.get('phases'), 'mapFrames': ld.get('mapFrames')},
        'totalWaitSeconds': None if gen_a['confirmToReviewSeconds'] is None or act['workWithoutCreationSeconds'] is None else round(gen_a['confirmToReviewSeconds'] + act['workWithoutCreationSeconds'], 2),
        'memory': {k: {'maxWorkingSetMB': mb(v.get('maxWorkingSetBytes')), 'maxPrivateMB': mb(v.get('maxPrivateBytes')), 'gcHeapMBAtMax': mb(v.get('gcHeapBytesAtMax')), 'samples': v.get('samples')}
                   for k, v in sorted((o.get('memory') or {}).items())},
        'peaks': {'workingSetMB': mb(o.get('peakWorkingSetBytes')), 'pagedMB': mb(o.get('peakPagedBytes')), 'virtualMB': mb(o.get('peakVirtualBytes'))},
        'disk': [{'at': d.get('at'), 'storeMB': mb(d.get('store')), 'bundlesMB': mb(d.get('bundles')), 'jobsMB': mb(d.get('jobs')), 'worldsMB': mb(d.get('worlds')),
                  'activationsMB': mb(d.get('activations')), 'navigationCachesMB': mb(d.get('navigation-caches')), 'gameSavesMB': mb(d.get('gameSaves')),
                  'freeGB': None if d.get('freeBytes') is None else round(d['freeBytes'] / 1073741824.0, 1)} for d in o.get('disk') or []],
    }


def module_output_sha():
    with open(os.path.join(REPO, 'evidence', 'runtime', 'build-api-inputs.json'), encoding='utf-8-sig') as f:
        inputs = json.load(f)
    outs = inputs.get('outputs') if isinstance(inputs, dict) else None
    rows = outs if isinstance(outs, list) else [{'path': k, 'sha256': v} for k, v in (outs or {}).items()]
    for r in rows:
        path = (r.get('path') or r.get('name') or '') if isinstance(r, dict) else ''
        if path.replace('\\', '/').endswith('MNBL.ProceduralWorld.dll'):
            return r.get('sha256'), inputs
    return None, inputs


def checks(result, m):
    o = result.get('outputs') or {}
    a, b, r = o.get('A') or {}, o.get('B') or {}, o.get('R') or {}
    g = geography_compare(a.get('geography'), b.get('geography'))
    rt = routes_compare(a.get('routes'), b.get('routes'))
    gen, act = m['generate'], m['startCampaign']
    built, _ = module_output_sha()
    bundle_id = (a.get('geography') or {}).get('worldContentId')
    rows = [
        ('the run completed without failure (status %s, %s)' % (result.get('status'), o.get('failures')), result.get('status') == 'ok' and o.get('completed') is True and not o.get('failures')),
        ('the run\'s module is the build recorded (build-api-inputs.json %s, run %s)' % (built, result.get('moduleSha256')), built is not None and (built or '').lower() == (result.get('moduleSha256') or '').lower()),
        ('A (seed %s) was generated cold through the setup screen\'s Confirm: its bundle was not in the store (%s s)' % (a.get('seed'), gen['confirmToReviewSeconds']),
         gen['state'] == 'Generated' and gen['bundleExisted'] is False and bundle_id is not None and bundle_id not in (a.get('bundlesBefore') or [])),
        ('every generation phase is timed, with worldbridge\'s receipts and the producer\'s own run (%s; producer %s)' % (sorted(gen['jobPhases']), gen['producer']),
         all(p in gen['jobPhases'] for p in ('GeneratingSource', 'Compiling', 'BuildingScene', 'ValidatingRuntime')) and len(gen['producer']) > 0 and gen['split']['producerSeconds'] is not None),
        ('the setup screen kept drawing while A and B generated (longest frames %s s and %s s, limit %s)' % ((gen['frames'] or {}).get('maxSeconds'), (m['generateB']['frames'] or {}).get('maxSeconds'), FRAME_LIMIT),
         all((x or {}).get('maxSeconds') is not None and x['maxSeconds'] < FRAME_LIMIT for x in (gen['frames'], m['generateB']['frames']))),
        ('Start Campaign reached the campaign map through character creation (%s s to the map)' % act['startToMapSeconds'], act['state'] == 'Started' and act['startToMapSeconds'] is not None),
        ('every activation step is timed, the navigation caches with their source (caches %s, %s s)' % (act['navigationCaches']['cacheStatus'], act['navigationCaches']['elapsedSeconds']),
         all(s in act['steps'] for s in ('BuildWorld', 'StartCampaign', 'PublishSave')) and act['navigationCaches']['status'] is not None and act['creationOnScreenSeconds'] is not None),
        ('the start\'s responsiveness was measured frame by frame (%s frames, longest gap %s s)' % ((act['frames'] or {}).get('frames'), (act['frames'] or {}).get('maxSeconds')), ((act['frames'] or {}).get('frames') or 0) > 0),
        ('the map was sampled paused, at play speed and in fast forward', all(isinstance(m['map'].get(k), dict) and (m['map'][k].get('frames') or 0) > 0 for k in ('Stop', 'UnstoppablePlay', 'UnstoppableFastForward'))),
        ('play speed and fast forward advanced campaign time (%s h and %s h)' % ((m['map'].get('UnstoppablePlay') or {}).get('campaignHours'), (m['map'].get('UnstoppableFastForward') or {}).get('campaignHours')),
         ((m['map'].get('UnstoppablePlay') or {}).get('campaignHours') or 0) > 0 and ((m['map'].get('UnstoppableFastForward') or {}).get('campaignHours') or 0) > 0),
        ('a manual save was published (%s s)' % m['save']['seconds'], m['save']['published'] is True),
        ('the save loaded through the normal load entry to the map (%s s)' % m['load']['toMapSeconds'], m['load']['loaded'] is True),
        ('memory was sampled in every phase (%s)' % sorted(m['memory']), all(k in m['memory'] for k in ('A.generate', 'A.start', 'A.map', 'A.save', 'A.load', 'B.generate'))),
        ('the store was measured at each step and the job directory while generating (%s)' % [d['at'] for d in m['disk']], len(m['disk']) >= 5 and gen['peakJobDirectoryMB'] is not None),
        ('the machine, its settings and the build are recorded (CPU, memory, adapters, Windows, power, settings files; module, worldbridge, producer, native and managed engine, embedded documents)',
         bool((o.get('machine') or {}).get('cpu')) and bool((o.get('machine') or {}).get('windows')) and (o.get('machine') or {}).get('engine_config.txt') is not None
         and all((o.get('build') or {}).get(k) for k in ('moduleAssemblySha256', 'producerSha256', 'worldbridgeSha256', 'nativeBinaries', 'embeddedDocuments'))),
        ('the profile\'s next seed %s was tried and its outcome recorded (%s)' % (r.get('seed'), (r.get('generate') or {}).get('state')), (r.get('generate') or {}).get('state') in ('Generated', 'Failed')),
        ('B (seed %s) was generated by the same build (%s)' % (b.get('seed'), (b.get('generate') or {}).get('state')), (b.get('generate') or {}).get('state') == 'Generated'),
        ('A and B differ in geography: heights %s, cells %s, coastline %s; land cells %s, coast cells %s, same class in %s of the cells' % (g['heightsDiffer'], g['cellsDiffer'], g['coastlineDiffers'], g['landCells'], g['coastCells'], g.get('sameClassFraction')),
         g['heightsDiffer'] and g['cellsDiffer'] and g['coastlineDiffers'] and None not in g['landCells']),
        ('A and B differ in traversability: the engine route matrices differ (%s routes, %s reachable), the navigation meshes differ, each report the job\'s own (%s)' % (rt['routes'], rt['reachable'], rt['reportsAreTheJobs']),
         rt['matrixDiffers'] and rt['navmeshDiffers'] and None not in rt['routes'] and rt['reportsAreTheJobs'] == [True, True]),
        ('each world\'s engine routes agree with its static plan (%s of %s)' % (rt['routesAgreeWithStatic'], rt['routes']), None not in rt['routes'] and rt['routesAgreeWithStatic'] == rt['routes']),
    ]
    return [{'check': n, 'passed': bool(ok)} for n, ok in rows], g, rt


def findings(m):
    """What stays unresolved, stated rather than failed: responsiveness and resource observations a player meets."""
    out = []
    act = m['startCampaign']
    if act['longestGap'] and (act['longestGap'].get('seconds') or 0) >= FROZEN:
        out.append('The game did not respond for %.0f s in total during the first campaign start (longest gap %.0f s, on %s / %s): the navigation caches are generated on the engine thread while the loading screen shows.'
                   % (act['frozenSeconds'], act['longestGap']['seconds'], act['longestGap'].get('screen'), act['longestGap'].get('state')))
    if act['navigationCaches']['cacheStatus'] == 'generated':
        out.append('The first start of a world after any module build generates its navigation caches (%.0f s of the start\'s %.0f s of work).' % (act['navigationCaches']['elapsedSeconds'] or 0, act['workSeconds']))
    ff = m['map'].get('UnstoppableFastForward') or {}
    if (ff.get('maxSeconds') or 0) >= FRAME_LIMIT:
        out.append('Fast forward on the map had a %.2f s frame.' % ff['maxSeconds'])
    return out


def ceil_to(x, step):
    return int(math.ceil(x / float(step)) * step)


def budget_merge(runs, budget):
    """Merges each run's measurements into the budget once (by pid) and recomputes the provisional limits from everything merged."""
    phases = budget['phases']
    store = budget.setdefault('mmr56Measurements', {'runs': []})
    for pid, (_, result) in runs:
        if pid in store['runs']:
            continue
        store['runs'].append(pid)
        o = result.get('outputs') or {}
        m = measure(result)
        add = lambda key, value: store.setdefault(key, []).append(value) if value is not None else None
        for key in ('A', 'R', 'B'):
            g = (o.get(key) or {}).get('generate') or {}
            if g.get('state') == 'Generated' and g.get('seconds') is not None:
                phases['generateWorld']['seconds'].append(round(g['seconds'], 1))
                w = generation(o.get(key))
                add('bundleMB', w['bundleMB']); add('peakJobDirectoryMB', w['peakJobDirectoryMB']); add('generationLongestFrameSeconds', round((w['frames'] or {}).get('maxSeconds') or 0, 2))
                for p in w['processes']:
                    if p['process'].startswith('worldbridge --bundle'):
                        add('worldbridgePeakMB', p['peakWorkingSetMB'])
                for p in w['producer']:
                    add('producerPeakMB', p['peakJobMemoryMB'])
        act = m['startCampaign']
        if act['navigationCaches']['cacheStatus'] == 'generated' and act['workWithoutCreationSeconds'] is not None:
            phases.setdefault('startCampaign', {'seconds': []})['seconds'].append(round(act['workWithoutCreationSeconds'], 1))
        add('loadSeconds', None if m['load']['toMapSeconds'] is None else round(m['load']['toMapSeconds'], 1))
        add('gameWorkingSetMB', m['peaks']['workingSetMB'])
        add('gamePrivateMB', max((v['maxPrivateMB'] or 0) for v in m['memory'].values()) if m['memory'] else None)
    if 'startCampaign' in phases:
        phases['startCampaign']['source'] = ('MMR-56 first_test_profile runs %s: the first start of world A after a module build (its navigation caches built), the activation\'s steps from Start Campaign to the committed campaign summed, less the time character creation was on screen (the player\'s), on the machine of worldgen/validation/first-test-measurements.json.' % ', '.join(store['runs']))
    phases['generateWorld']['mmr56'] = 'first_test_profile runs %s add their generations from Confirm to review (A cold, the profile\'s next seed when it generates, and B).' % ', '.join(store['runs'])
    gen = [x for x in phases['generateWorld']['seconds']]
    limits = {
        'generateWorldSeconds': {'limit': ceil_to(1.5 * max(gen), 10), 'measured': gen},
        'firstCampaignStartSeconds': {'limit': ceil_to(1.5 * max(phases['startCampaign']['seconds']), 30), 'measured': phases['startCampaign']['seconds'], 'note': 'the navigation caches built'} if 'startCampaign' in phases else None,
        'loadSeconds': {'limit': ceil_to(1.5 * max(store.get('loadSeconds') or [0]), 5), 'measured': store.get('loadSeconds'), 'note': 'the navigation caches reused'},
        'generationLongestFrameSeconds': {'limit': FRAME_LIMIT, 'measured': store.get('generationLongestFrameSeconds'), 'note': 'MMR-43\'s rule while the job runs off the engine thread'},
        'gameWorkingSetMB': {'limit': ceil_to(1.5 * max(store.get('gameWorkingSetMB') or [0]), 512), 'measured': store.get('gameWorkingSetMB')},
        'gamePrivateMB': {'limit': ceil_to(1.5 * max(store.get('gamePrivateMB') or [0]), 512), 'measured': store.get('gamePrivateMB')},
        'producerPeakMB': {'limit': ceil_to(1.5 * max(store.get('producerPeakMB') or [0]), 64), 'measured': store.get('producerPeakMB'), 'note': 'fwg.exe\'s job object, from worldbridge\'s run record'},
        'worldbridgePeakMB': {'limit': ceil_to(1.5 * max(store.get('worldbridgePeakMB') or [0]), 64), 'measured': store.get('worldbridgePeakMB'), 'note': 'worldbridge --bundle'},
        'storePerWorldMB': {'limit': ceil_to(1.5 * max(store.get('bundleMB') or [0]), 50), 'measured': store.get('bundleMB'), 'note': 'the published bundle; its activation package, navigation caches and saves add a few MB'},
        'generationTemporaryMB': {'limit': ceil_to(1.5 * max(store.get('peakJobDirectoryMB') or [0]), 50), 'measured': store.get('peakJobDirectoryMB'), 'note': 'the job directory at its largest while generating, released after review'},
    }
    limits['freeDiskBeforeGenerationMB'] = {'limit': ceil_to(limits['storePerWorldMB']['limit'] + limits['generationTemporaryMB']['limit'], 256), 'note': 'the per-world store and temporary limits together'}
    budget['provisionalLimits']['values'] = {k: v for k, v in limits.items() if v is not None}
    return phases['generateWorld']['seconds'], (phases.get('startCampaign') or {}).get('seconds')


def main():
    pids = [x for x in sys.argv[1:] if not x.startswith('--')]
    if not pids:
        print(__doc__)
        return 2
    runs = [(p, load(p)) for p in pids]
    path, result = runs[0][1]
    o = result.get('outputs') or {}
    m = measure(result)
    rows, g, rt = checks(result, m)
    built, build_inputs = module_output_sha()
    locks = {}
    for name in ('tools.lock.json', 'worldgen/producer.lock.json', 'worldgen/profiles/one-shot.json'):
        p = os.path.join(REPO, name)
        if os.path.exists(p):
            with open(p, 'rb') as f:
                locks[name] = hashlib.sha256(f.read()).hexdigest()
    out = {'schema': 'mnbl.first-test-measurements', 'version': 2, 'ticket': 'MMR-56', 'pid': pids[0], 'result': os.path.relpath(path, REPO).replace('\\', '/'),
           'otherRuns': pids[1:], 'status': result.get('status'), 'error': result.get('error'),
           'passed': sum(1 for x in rows if x['passed']), 'total': len(rows), 'checks': rows, 'findings': findings(m),
           'profile': {'seed': (o.get('A') or {}).get('seed'), 'overrides': (o.get('build') or {}).get('overrides'), 'recipeVersion': (o.get('build') or {}).get('recipeVersion'),
                       'qualityAttempts': (o.get('build') or {}).get('qualityAttempts'), 'waitTextShown': (o.get('A') or {}).get('waitText')},
           'machine': o.get('machine'), 'build': o.get('build'), 'moduleSha256OfRun': result.get('moduleSha256'),
           'buildRecord': {'moduleOutputSha256': built, 'inputs': 'evidence/runtime/build-api-inputs.json'}, 'repositoryFilesAtSummary': locks,
           'measurements': m, 'seeds': {'A': seed_row(o.get('A')), 'R': seed_row(o.get('R')), 'B': seed_row(o.get('B'))}, 'geography': g, 'routes': rt,
           'worlds': {k: {'geography': {kk: vv for kk, vv in ((o.get(k) or {}).get('geography') or {}).items() if kk != 'classes'}, 'routes': (o.get(k) or {}).get('routes')} for k in ('A', 'B')}}
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(scrub(out), f, indent=1, ensure_ascii=False)
        f.write('\n')
    if '--budget' in sys.argv:
        with open(BUDGET, encoding='utf-8') as f:
            budget = json.load(f)
        gen, start = budget_merge(runs, budget)
        with open(BUDGET, 'w', encoding='utf-8', newline='\n') as f:
            json.dump(budget, f, indent=1, ensure_ascii=False)
            f.write('\n')
        print('budget: generateWorld %s, startCampaign %s' % (gen, start))
    print('first_test_profile %s: %d/%d checks; status %s' % (pids[0], out['passed'], out['total'], out['status']))
    for x in rows:
        if not x['passed']:
            print('   not passed: ' + x['check'])
    for f_ in out['findings']:
        print('   finding: ' + f_)
    return 0 if out['passed'] == out['total'] and out['status'] == 'ok' else 1


if __name__ == '__main__':
    sys.exit(main())
