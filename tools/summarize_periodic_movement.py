"""Summarize MMR-65 (seam route execution and periodic spatial queries) into evidence/world/periodic-movement.json.

Every value is copied or counted from the files named on the command line: the in-game generated_campaign result with wrapProbe=1 and
movementProbe=1 on the MMR-63 fixture, the offline harness evidence (tools/Test-PeriodicCoordinates.ps1, its spatialTable check) and the
consumer audit (evidence/world/periodic-consumers.tsv). Tracks are cut to their first samples. Usage:
  python tools/summarize_periodic_movement.py --run <result.json> --offline <tests.json> --audit <consumers.tsv> --out <summary.json>
    [--history <result.json> ...]
"""
import argparse
import collections
import csv
import datetime
import hashlib
import json
import os
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.dont_write_bytecode = True
sys.path.insert(0, os.path.join(REPO, 'tools'))
from summarize_wrap_probe import mmr63_checks  # noqa: E402

PATCH_ENTRIES = 20   # MMR-63/64's 16 + FindPointAroundPosition + 2 FindPointInsideArea + the spatial table


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def rel(path):
    path = os.path.abspath(path)
    return os.path.relpath(path, REPO).replace('\\', '/') if path.startswith(REPO) else path


def load(path):
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f)


def trim(value, keep=12):
    """Copies a section, cutting long sample lists (tracks) to their first entries."""
    if isinstance(value, dict):
        return {k: (v[:keep] + ['... %d more' % (len(v) - keep)] if k == 'track' and isinstance(v, list) and len(v) > keep else trim(v, keep)) for k, v in value.items()}
    if isinstance(value, list):
        return [trim(v, keep) for v in value]
    return value


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--run', required=True)
    ap.add_argument('--offline', required=True)
    ap.add_argument('--audit', required=True)
    ap.add_argument('--history', action='append', default=[])
    ap.add_argument('--out', required=True)
    a = ap.parse_args()
    run = load(a.run)
    world = run['outputs']['worlds'][0]
    movement, wrap = world.get('movement', {}), world.get('wrap', {})
    offline = load(a.offline)
    audit = collections.Counter()
    sites = collections.Counter()
    with open(a.audit, encoding='utf-8') as f:
        rows = list(csv.DictReader(f, delimiter='\t'))
    for r in rows:
        audit[r['disposition']] += 1
        sites[r['disposition']] += int(r['count'])
    regression = mmr63_checks(wrap, PATCH_ENTRIES) if wrap else {}
    summary = dict(
        schema='mnbl.evidence.periodic-movement', version=1, ticket='MMR-65',
        generated=datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0).isoformat(),
        sources=dict(run=dict(file=rel(a.run), sha256=sha(a.run), processId=run['processId'], status=run['status'], moduleSha256=run.get('moduleSha256'),
                              args={k: v for k, v in run['args'].items() if k != 'catalogReceipt'}),
                     offline=dict(file=rel(a.offline), sha256=sha(a.offline), checks=offline.get('checks'), failures=offline.get('failures')),
                     audit=dict(file=rel(a.audit), sha256=sha(a.audit), rows=len(rows)),
                     history=[dict(file=rel(h), sha256=sha(h), status=load(h)['status']) for h in a.history]),
        audit=dict(rowsByDisposition=dict(audit), callSitesByDisposition=dict(sites),
                   rule='every Distance, DistanceSquared and subtraction of CampaignVec2/Vec2, every direct LocatorGrid search and every point-around-position call in the '
                        'installed campaign assemblies; dispositions from tools/periodic/build_spatial_table.py'),
        spatialTable=offline.get('spatialTable'),
        movement=trim({k: v for k, v in movement.items()}),
        wrapRegression=dict(checks=regression, passed=all(regression.values()) if regression else None, failures=wrap.get('failures'),
                            patches=len(wrap.get('patches', [])), expectedPatches=PATCH_ENTRIES),
        failures=dict(movement=movement.get('failures'), wrap=wrap.get('failures'), offline=offline.get('failures')))
    with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(summary, f, indent=2)
        f.write('\n')
    print('wrote', a.out, 'movement failures', len(movement.get('failures') or []), 'wrap regression', summary['wrapRegression']['passed'])


if __name__ == '__main__':
    main()
