"""Summarize MMR-41 in game into evidence/world/playability-report.json.

Runs:
  --navigation      world_navigation: worlds A, B, warm A and A with Bridge faces disabled, each with outputs.worlds[*].playability (the semantic
                    route set through the engine and the pending-runtime flags) next to its navigation report
  --job             generation_job: the full pipeline's runtime stage ran the playability step, persisted its report, and the gate allows it
  --cold            generated_campaign (fixture): world 0's caches generated and published; the campaign route set
  --reuse           generated_campaign with navigationCacheVerify=1: the caches reused, compared with a round trip and with fresh caches
  --fault           generated_campaign with navigationCacheFault=corrupt: the damaged published copy refused, regenerated and republished
  --periodic-cold, --periodic-reuse   the same on the periodic fixture (the 567 s cache): generated and published, then reused and verified; the fixture
                    has no startup fixtures, so only the selected culture's start is routed (playabilityStartCultures, recorded as excludedStarts)
  --starts-refused  generated_campaign on the periodic fixture with every creation culture's start: the six installed Calradia start points the
                    fixture cannot replace are refused (no engine path, both ways), the generated start passes; the run ends in error by design
Every value is copied or counted from the results. Usage:
  python tools/summarize_playability.py --navigation N --job J --cold C --reuse R --fault F --periodic-cold PC --periodic-reuse PR --out OUT --starts-refused S [--history H ...]
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


def cache_record(run, world=0):
    worlds = run['outputs']['worlds']
    if world >= len(worlds):
        return {}, {}
    nav = (worlds[world].get('mapSupport') or {}).get('generatedNavigationCaches') or {}
    return nav.get('navigationCache') or {}, nav


def key_lines(rec, prefix):
    return [l for l in (rec.get('key') or '').split('\n') if l.startswith(prefix)]


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    for name in ('navigation', 'job', 'cold', 'reuse', 'fault', 'periodic-cold', 'periodic-reuse', 'out'):
        ap.add_argument('--' + name, required=True)
    ap.add_argument('--starts-refused', required=True)
    ap.add_argument('--history', action='append', default=[])
    a = ap.parse_args()
    navigation, job, cold, reuse, fault, pcold, preuse = (load(x) for x in (a.navigation, a.job, a.cold, a.reuse, a.fault, a.periodic_cold, a.periodic_reuse))
    refused_run = load(a.starts_refused)
    refused_play = refused_run['outputs']['worlds'][0].get('playability') or {}
    refused_world = refused_play.get('world') or {}
    selected = refused_run.get('args', {}).get('culture')
    generated_starts = [s for s in refused_world.get('starts') or [] if s['culture'] == selected]
    installed_starts = [s for s in refused_world.get('starts') or [] if s['culture'] != selected]
    point_of = lambda s: ','.join(('%.3f' % v).rstrip('0').rstrip('.') for v in s['point'])   # RuntimePlayabilityValidator's label: rounded to 3 places
    refused_ids = [f['id'] for f in refused_play.get('failures') or []]

    production = []
    for w in navigation['outputs']['worlds']:
        p = w.get('playability')
        if p is None:
            continue
        flags = p['flags']
        production.append(dict(label=w['label'], sceneName=w.get('sceneName'), worldContentId=w.get('worldContentId'), navigationActivatable=w.get('activatable'),
                               routes=p['routes'], failures=p['failures'][:20],
                               flags=dict(total=flags['total'], resolved=flags['resolved'], failed=flags['failed'], byKind=flags['byKind'], failedFlags=flags['failedFlags'][:20]),
                               steepPads=p['steepPads'], notFlags=p.get('notFlags'), engineFaults=p.get('engineFaults'), activatable=p['activatable'], reportSha256=p['reportSha256'],
                               gate=w.get('playabilityGate'), seconds=w.get('playabilitySeconds'), engine=p['engine']))
    by = {w['label']: w for w in production}
    full = next(j for j in job['outputs']['jobs'] if j.get('phase') == 'ReadyForReview')
    job_events = json.dumps(full)
    caches = {}
    for name, run in (('cold', cold), ('reuse', reuse), ('fault', fault), ('periodicCold', pcold), ('periodicReuse', preuse)):
        rec, nav = cache_record(run)
        rec1, _ = cache_record(run, 1)
        caches[name] = dict(status=rec.get('status'), keySha256=rec.get('keySha256'), refused=rec.get('refused'), faultInjected=rec.get('faultInjected'), keyError=rec.get('keyError'),
                            settlements=rec.get('settlements'), faces=rec.get('faces'), crc=rec.get('crc'), caches=rec.get('caches'), published=rec.get('published'),
                            publishError=rec.get('publishError'), publishMilliseconds=rec.get('publishMilliseconds'), verification=rec.get('verification'),
                            elapsedMilliseconds=rec.get('elapsedMilliseconds'), registrationStatus=nav.get('status'),
                            periodicKey=(key_lines(rec, 'periodic=') or [None])[0], dropOffPorts=nav.get('dropOffPorts'),
                            villagePortLines=[l for l in key_lines(rec, 'settlement=') if '|village+port|' in l],
                            keyAtCampaignStart=[w.get('navigationCacheKeyAtCampaignStart') for w in run['outputs']['worlds']],
                            world1=rec1.get('status'), world1KeySha256=rec1.get('keySha256'))
    plays = {name: (run['outputs']['worlds'][0].get('playability') or {}) for name, run in (('cold', cold), ('reuse', reuse), ('fault', fault), ('periodicCold', pcold), ('periodicReuse', preuse))}
    gen = lambda c: sum(x.get('generateMilliseconds') or 0 for x in c['caches'] or [])
    read = lambda c: sum(x.get('deserializeMilliseconds') or 0 for x in c['caches'] or [])
    checks = dict(
        productionRoutesPass=all(by.get(l, {}).get('activatable') for l in ('A', 'B', 'A (warm)')) and all(by[l]['routes']['failed'] == 0 for l in ('A', 'B')),
        everyCategoryInBothDirections=all(all(v['routes'] > 0 and v['routes'] % 2 == 0 and v['passed'] == v['routes'] for v in by[l]['routes']['byCategory'].values()) for l in ('A', 'B')),
        everyPendingFlagResolved=all(by[l]['flags']['failed'] == 0 and by[l]['flags']['total'] > 0 for l in ('A', 'B', 'A (warm)')),
        notFlagsListed=all(by[l].get('notFlags') and by[l]['notFlags'].get('pendingSlots') is not None and by[l]['notFlags'].get('pendingRuntimePolicies') is not None for l in ('A', 'B')),
        warmSameAsCold=by.get('A', {}).get('reportSha256') == by.get('A (warm)', {}).get('reportSha256'),
        faultedWorldRefused=any(l.startswith('A (engine fault') and not by[l]['activatable'] and by[l]['routes']['failed'] > 0 and by[l]['engineFaults'] for l in by),
        productionGateAllows=all(by[l].get('gate') == 'activation allowed' for l in ('A', 'B', 'A (warm)')),
        jobRanPlayability='playability_verified' in job_events and 'ready_for_review' in job_events and full.get('playabilityGate') == 'activation allowed'
                          and (full.get('playabilityRoutes') or 0) > 0 and full.get('playabilityRoutesFailed') == 0 and full.get('playabilityFlagsFailed') == 0,
        coldGeneratedAndPublished=caches['cold']['status'] in ('generated', 'regenerated') and caches['cold']['published'] is not None,
        reuseDeserialized=caches['reuse']['status'] == 'reused' and caches['reuse']['keySha256'] == caches['cold']['keySha256'] and all(c.get('source') == 'deserialized' for c in caches['reuse']['caches'] or [{}]),
        reuseEqualsFreshAndRoundTrip=bool(caches['reuse']['verification']) and caches['reuse']['verification'].get('equal') is True,
        damagedCopyRefusedAndRegenerated=caches['fault']['status'] == 'regenerated' and bool(caches['fault']['faultInjected']) and any('hash' in r for r in caches['fault']['refused'] or []) and caches['fault']['published'] is not None,
        otherWorldOtherKey=caches['cold']['world1KeySha256'] is not None and caches['cold']['world1KeySha256'] != caches['cold']['keySha256'],
        dropOffPortsInCachesAndKey=bool(caches['cold']['dropOffPorts']) and any(d.get('port') for d in caches['cold']['dropOffPorts']) and len(caches['cold']['villagePortLines']) > 0,
        keyUnchangedAtCampaignStart=all(k and k.get('same') is True for name in ('cold', 'reuse', 'fault', 'periodicCold', 'periodicReuse') for k in caches[name]['keyAtCampaignStart']),
        campaignRoutesPass=all(bool(p) and p.get('activatable') is True and p['routes']['total'] > 0 for p in plays.values()),
        periodicKeyed=(caches['periodicCold']['periodicKey'] or '').startswith('periodic=mmr64/1 ') and caches['periodicReuse']['keySha256'] == caches['periodicCold']['keySha256'],
        periodicGeneratedThenReused=caches['periodicCold']['status'] in ('generated', 'regenerated') and caches['periodicCold']['published'] is not None and caches['periodicReuse']['status'] == 'reused'
                                    and bool(caches['periodicReuse']['verification']) and caches['periodicReuse']['verification'].get('equal') is True,
        periodicStartsLimitedAndRecorded=all((p.get('world') or {}).get('startCultures') == ['empire'] and len((p.get('world') or {}).get('excludedStarts') or []) == 6 for p in (plays['periodicCold'], plays['periodicReuse'])),
        installedStartsRefused=refused_run['status'] == 'error' and refused_play.get('activatable') is False and len(installed_starts) == 6 and len(generated_starts) == 1
                               and all(f.get('category') == 'player-start' for f in refused_play.get('failures') or [])
                               and all(sum(1 for i in refused_ids if 'start point ' + point_of(s) + ' ' in i + ' ') == 2 for s in installed_starts)
                               and not any('start point ' + point_of(s) + ' ' in i + ' ' for s in generated_starts for i in refused_ids),
        runsOk=all(r['status'] == 'ok' for r in (navigation, job, cold, reuse, fault, pcold, preuse)))
    summary = dict(
        schema='mnbl.evidence.playability', version=2, ticket='MMR-41',
        generated=datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0).isoformat(),
        sources=dict(navigation=source(a.navigation), job=source(a.job), cold=source(a.cold), reuse=source(a.reuse), fault=source(a.fault), periodicCold=source(a.periodic_cold),
                     periodicReuse=source(a.periodic_reuse), startsRefused=source(a.starts_refused), history=[source(h) for h in a.history]),
        checks=checks, passed=all(checks.values()),
        production=production,
        job={k: full.get(k) for k in ('sceneName', 'navigationActivatable', 'settlementEntitiesPassed', 'playabilityReport', 'playabilityActivatable', 'playabilityRoutes', 'playabilityRoutesFailed',
                                      'playabilityFlags', 'playabilityFlagsFailed', 'playabilityReportSha256', 'playabilityItems', 'playabilityGate')},
        caches=caches,
        cost=dict(fixtureGenerateMilliseconds=gen(caches['cold']), fixtureReadMilliseconds=read(caches['reuse']), periodicGenerateMilliseconds=gen(caches['periodicCold']),
                  periodicReadMilliseconds=read(caches['periodicReuse'])),
        campaign={name: dict(routes=p.get('routes'), failures=p.get('failures'), world=p.get('world'), activatable=p.get('activatable')) for name, p in plays.items()},
        startsRefused=dict(selectedCulture=selected, generatedStarts=generated_starts, installedStarts=installed_starts, routes=refused_play.get('routes'), failures=refused_play.get('failures'),
                           error=refused_run.get('error')))
    with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(summary, f, indent=2)
        f.write('\n')
    print('wrote', a.out, 'passed', summary['passed'], {k: v for k, v in checks.items() if not v})


if __name__ == '__main__':
    main()
