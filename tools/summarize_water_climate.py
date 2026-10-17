"""Summarize MMR-39's water, flow and climate evidence into evidence/world/water-climate.json.

Every value is copied or counted from the files named on the command line (the in-game generated_campaign results with
waterClimateProbe=1, and the offline harness evidence); nothing is re-derived offline. Usage:
  python tools/summarize_water_climate.py --periodic <result.json> --naval <result.json> --offline <tests.json> --out <summary.json>
    [--surface <world_surface result.json>] [--load <generated_save_load result.json>] [--regression LABEL=<result.json> ...]
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


def water_climate(result):
    worlds = result['outputs']['worlds']
    if len(worlds) != 1 or 'waterClimate' not in worlds[0]:
        raise SystemExit('expected one world with a waterClimate report')
    return worlds[0], worlds[0]['waterClimate']


def run_row(path):
    result = load(path)
    world, wc = water_climate(result)
    return result, world, wc, dict(file=rel(path), sha256=sha(path), processId=result['processId'], status=result['status'],
                                   args={k: v for k, v in result['args'].items() if k not in ('catalogReceipt',)},
                                   moduleSha256=result.get('moduleSha256'), failures=wc['failures'])


def legs(sailing):
    out = {}
    for leg in ('downwind', 'upwind'):
        r = sailing.get(leg, {})
        out[leg] = {k: r.get(k) for k in ('leg', 'from', 'to', 'samplesTaken', 'samplesStationary', 'samplesNearStorm', 'meanSpeed', 'meanWindBoost', 'displacementPerHour')}
    return out


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--periodic', required=True, help='Run A: the periodic open-sea fixture with the synthesized climate')
    parser.add_argument('--naval', required=True, help='Run B: the naval fixture with generated pirate spawn points')
    parser.add_argument('--offline', required=True)
    parser.add_argument('--surface', help="a world_surface run: the production scenes' water bodies")
    parser.add_argument('--load', help="a generated_save_load run of the periodic world's save: the climate after load")
    parser.add_argument('--regression', action='append', default=[], help='LABEL=result.json: an accepted ticket\'s probe re-run on this module')
    parser.add_argument('--history', action='append', default=[])
    parser.add_argument('--out', required=True)
    args = parser.parse_args()

    a_result, a_world, a, a_row = run_row(args.periodic)
    b_result, b_world, b, b_row = run_row(args.naval)
    offline = load(args.offline)

    storms = a.get('storms', {})
    seasons = a.get('seasons', {})
    summary = dict(
        schema='mnbl.evidence.water-climate', version=1, ticket='MMR-39',
        generated=datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0).isoformat(),
        sources=dict(periodic=a_row, naval=b_row, offline=dict(file=rel(args.offline), sha256=sha(args.offline), checks=offline['checks'], failures=offline['failures']),
                     history=[dict(file=rel(h), sha256=sha(h), status=load(h)['status']) for h in args.history]),
        offline=dict(climateVersion=offline['climateVersion'], waterVersion=offline['waterVersion'], source=offline.get('source'), synthesis=offline.get('synthesis'),
                     sourceBiomes=offline.get('sourceBiomes'), waterClasses=offline.get('waterClasses'), stormPolicy=offline.get('stormPolicy'),
                     rows=[dict(check=r['check'], passed=r['passed']) for r in offline['rows']]),
        periodicWorld=dict(
            world=a.get('world'), extent=a.get('extent'), climate=a.get('climate'),
            water=a.get('water'),
            climateReadBack={k: a['climate'].get(k) for k in ('boundWindSha256', 'boundPrecipitationSha256', 'weatherNodeDimension', 'mismatches', 'copyMismatches')},
            climateSamples=a['climate'].get('samples', [])[:6],
            colorGrade=a.get('colorGrade'),
            seasons={k: seasons.get(k) for k in ('formula', 'maxFormulaError', 'ordered', 'snowyNodeSnowBySeason', 'rainyNodeRainBySeason', 'samples',
                                                 'atmosphereProbes', 'atmosphereStates', 'nightEqualsAcrossPoints', 'noonEqualsNight', 'dayNight')},
            wakes=a.get('wakes'),
            sailing=dict(legs(a.get('sailing', {})), windBoostSource=a.get('sailing', {}).get('windBoostSource'), screenshot=a.get('sailing', {}).get('screenshot'),
                         synthetic=a.get('sailing', {}).get('synthetic')),
            dayNight={k: v for k, v in a.get('dayNight', {}).items()},
            storms={k: storms.get(k) for k in ('model', 'baseModel', 'policy', 'candidates', 'canonicalCandidates', 'spawnChanceScale', 'spawnedAt',
                                                'developingHours', 'waitedHours', 'waitedSeconds', 'rebases', 'otherStormRebases', 'after', 'acrossSeam',
                                                'damagePass', 'poleReflection')},
            stormTrackSamples=len(storms.get('track', [])),
            counters=a.get('counters'),
            mapSupportWeather=a_world.get('mapSupport', {}).get('generatedWeather')),
        navalWorld=dict(
            world=b.get('world'), climate=b.get('climate'), water=b.get('water'),
            climateReadBack={k: b['climate'].get(k) for k in ('boundWindSha256', 'boundPrecipitationSha256', 'mismatches')},
            colorGrade=b.get('colorGrade'), dayNight=b.get('dayNight'), wakes=b.get('wakes'),
            sailing=dict(legs(b.get('sailing', {})), windBoostSource=b.get('sailing', {}).get('windBoostSource')),
            pirates={k: b.get('pirates', {}).get(k) for k in ('wrapperInstalled', 'planPoints', 'servedPoints', 'patrolZones', 'campaignDays',
                                                               'partiesPatrollingGeneratedPoints', 'pirateParties')},
            counters=b.get('counters')))
    if args.surface:
        surface = load(args.surface)
        rows = []
        for w in surface['outputs']['worlds']:
            if 'label' not in w:
                continue
            water, checks = w.get('water', {}), w.get('checks', {})
            rows.append(dict(label=w['label'], worldContentId=w['worldContentId'], surfaceChecksPassed=checks.get('passed'), surfaceFailures=checks.get('failures'),
                             water={k: water.get(k) for k in ('bodies', 'lakes', 'hiddenLakes', 'unmatchedLakes', 'lakesPlaced', 'lakeRayHits', 'colorGradeManager', 'sea', 'passed', 'failures')},
                             lakeRows=[dict(name=r['name'], z=r['expected']['z'], levelZ=r['expected']['levelZ'], clamped=r['expected']['clamped'], rayZ=r.get('rayZ'), terrainZ=r.get('terrainZ'))
                                       for r in water.get('lakeRows', [])]))
        summary['sources']['surface'] = dict(file=rel(args.surface), sha256=sha(args.surface), processId=surface['processId'], status=surface['status'])
        summary['productionScenes'] = rows
    if args.load:
        loaded = load(args.load)
        worlds = loaded['outputs'].get('worlds', [])
        summary['sources']['load'] = dict(file=rel(args.load), sha256=sha(args.load), processId=loaded['processId'], status=loaded['status'])
        summary['afterLoad'] = dict(status=loaded['status'], error=loaded.get('error'), failures=loaded['outputs'].get('failures'),
                                    worlds=[dict(worldId=w.get('worldId'), failures=w.get('failures'), packageVerified=w.get('packageVerified'),
                                                 weather=w.get('mapSupport', {}).get('generatedWeather')) for w in worlds])
    regressions = []
    for item in args.regression:
        label, path = item.split('=', 1)
        result = load(path)
        outputs = result.get('outputs', {})
        worlds = outputs.get('worlds', [])
        regressions.append(dict(label=label, file=rel(path), sha256=sha(path), probe=result.get('probe'), processId=result.get('processId'), status=result.get('status'),
                                error=result.get('error'), moduleSha256=result.get('moduleSha256'), completed=outputs.get('completed'),
                                wrapFailures=[w.get('wrap', {}).get('failures') for w in worlds if isinstance(w, dict) and 'wrap' in w]))
    summary['regressions'] = regressions
    summary['failures'] = dict(periodic=a['failures'], naval=b['failures'], offline=offline['failures'])
    with open(args.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(summary, f, indent=2, sort_keys=False)
        f.write('\n')
    print('wrote', args.out, 'periodic failures', len(a['failures']), 'naval failures', len(b['failures']), 'offline failures', offline['failures'])


if __name__ == '__main__':
    main()
