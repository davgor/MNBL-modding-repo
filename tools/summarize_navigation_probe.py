"""Summarize MMR17 generated-world navigation runs into evidence/runtime/navigation-probe.json.

Every value is copied or counted from the in-game result files named on the command line; nothing is
re-derived offline. Usage:
  python tools/summarize_navigation_probe.py --accepted <result.json> [--history <result.json> ...] --out <summary.json>
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


def route_summary(r):
    keys = ('navigationType', 'from', 'fromLand', 'to', 'toLand', 'found', 'reachable', 'distance', 'pathMilliseconds', 'waypoints')
    out = {k: r.get(k) for k in keys}
    if 'samples' in r:
        # The opposite-domain counter is meaningless for single-domain routes (always every sample).
        drop = {'Default': 'offSea', 'Naval': 'offLand'}.get(r.get('navigationType'))
        out['segmentSamples'] = {k: v for k, v in r['samples'].items() if k != drop}
    return out


def travel_summary(t):
    if not isinstance(t, dict):
        return t
    skip = {'track', 'movementTrace', 'engineRoute'}
    out = {k: v for k, v in t.items() if k not in skip}
    if 'track' in t:
        out['trackPoints'] = len(t['track'])
    if 'engineRoute' in t:
        out['engineRoute'] = route_summary(t['engineRoute'])
    return out


def world_summary(world, result_path):
    nav = world.get('navigation') or {}
    placement = world['registration'].get('placementPath')
    movement = nav.get('movement', {})
    return {
        'worldId': world['worldId'],
        'sceneName': world.get('sceneName'),
        'navFaces': world.get('navFaces'),
        'placement': {'path': rel(placement), 'sha256': sha(placement)} if placement and os.path.exists(placement) else placement,
        'bootValidationFailures': world.get('validationFailures'),
        'navigationFailures': nav.get('failures'),
        'engine': nav.get('engine'),
        'faces': nav.get('faces'),
        'routes': {k: route_summary(v) for k, v in nav.get('routes', {}).items()},
        'sanityChecks': nav.get('sanityChecks'),
        'distanceModel': nav.get('distanceModel'),
        'islandsAfterToggle': nav.get('islandsAfterToggle'),
        'faceCacheRefresh': nav.get('faceCacheRefresh'),
        'obstruction': nav.get('obstruction'),
        'offLandmass': nav.get('offLandmass'),
        'movement': {k: (travel_summary(v) if isinstance(v, dict) else v) for k, v in movement.items()},
        'completed': world.get('completed'),
    }


def cache_builds(log_path):
    """Seconds per world and navigation type from generate_begin to register_begin in the flushed progress log."""
    if not os.path.exists(log_path):
        return None
    marks = {}
    with open(log_path, encoding='utf-8-sig') as f:
        for line in f:
            parts = line.split()
            if len(parts) < 3 or ':generated_navigation_' not in parts[2]:
                continue
            stage = parts[2].split(':')
            stamp = datetime.datetime.fromisoformat(parts[0].rstrip('Z')[:26])
            marks[(stage[1], stage[2], stage[3])] = stamp
    out = {}
    for (event, world, nav), stamp in marks.items():
        if event == 'generated_navigation_generate_begin' and ('generated_navigation_register_begin', world, nav) in marks:
            out.setdefault(world, {})[nav] = round((marks[('generated_navigation_register_begin', world, nav)] - stamp).total_seconds(), 2)
    return out


def run_summary(path):
    result = load(path)
    worlds = result.get('outputs', {}).get('worlds', [])
    shots = {}
    prefix = os.path.splitext(path)[0]
    for name in sorted(result.get('outputs', {})):
        if name.endswith('Screenshot'):
            local = prefix + '-' + name + '.bmp'
            if os.path.exists(local):
                shots[name] = {'path': rel(local), 'sha256': sha(local)}
    return {
        'pid': result.get('processId'),
        'status': result.get('status'),
        'error': (result.get('error') or '').split('\n')[0] or None,
        'moduleSha256': result.get('moduleSha256'),
        'result': rel(path),
        'resultSha256': sha(path),
        'args': result.get('args'),
        'timings': result.get('timings'),
        'screenshots': shots,
        'navigationCacheBuildSeconds': cache_builds(os.path.splitext(path)[0] + '.log'),
        'worlds': [world_summary(w, path) for w in worlds],
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--accepted', required=True)
    parser.add_argument('--history', nargs='*', default=[])
    parser.add_argument('--notes', help='JSON file with history outcomes, criteria, gaps and limitations')
    parser.add_argument('--out', required=True)
    args = parser.parse_args()
    notes = load(args.notes) if args.notes else {}
    summary = {
        'schema': 'mnbl.generated-navigation-probe',
        'version': 1,
        'ticket': 'MMR-17',
        'utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'generator': {'path': rel(__file__), 'sha256': sha(__file__)},
        'probeSource': {p: sha(os.path.join(REPO, p)) for p in (
            'src/MNBL.ProceduralWorld/Diagnostics/GeneratedNavigationProbe.cs',
            'src/MNBL.ProceduralWorld/Automation/GeneratedCampaignBootProbe.cs',
            'tools/scenebin/build_bootstrap_world.py')},
        'accepted': run_summary(args.accepted),
        'history': [dict(run_summary(h), outcome=notes.get('historyOutcomes', {}).get(os.path.basename(h))) for h in args.history],
    }
    for key in ('criteria', 'knownGaps', 'limitations', 'reviewNotes', 'reproduce'):
        if key in notes:
            summary[key] = notes[key]
    with open(args.out, 'w', encoding='utf-8') as f:
        json.dump(summary, f, indent=2)
        f.write('\n')
    print('wrote', args.out)


if __name__ == '__main__':
    main()
