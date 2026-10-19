"""Summarize MMR-66 (wrapped camera, render copies and map interaction) into evidence/world/periodic-camera-picking.json.

Every value is copied or counted from the in-game generated_campaign result with wrapProbe=1 and presentationProbe=1 on the MMR-63
fixture (its presentation report and the wrap probe's regression). Long sample lists are cut. Usage:
  python tools/summarize_periodic_presentation.py --run <result.json> --out <summary.json> [--history <result.json> ...]
"""
import argparse
import datetime
import hashlib
import json
import os
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.dont_write_bytecode = True
sys.path.insert(0, os.path.join(REPO, 'tools'))
from summarize_wrap_probe import mmr63_checks  # noqa: E402

PERIODIC_ENTRIES = 20       # MMR-63/64/65
PRESENTATION_ENTRIES = 32   # MMR-66 with War Sails installed: camera 9, visual positions 5, shown positions 8, nameplates 4, display copies 6
PATCH_ENTRIES = PERIODIC_ENTRIES + PRESENTATION_ENTRIES


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
    if isinstance(value, dict):
        return {k: (v[:keep] + ['... %d more' % (len(v) - keep)] if k in ('track', 'events') and isinstance(v, list) and len(v) > keep else trim(v, keep)) for k, v in value.items()}
    if isinstance(value, list):
        return [trim(v, keep) for v in value]
    return value


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--run', required=True)
    ap.add_argument('--history', action='append', default=[])
    ap.add_argument('--out', required=True)
    ap.add_argument('--patches', type=int, default=PATCH_ENTRIES)
    a = ap.parse_args()
    run = load(a.run)
    world = run['outputs']['worlds'][0]
    presentation, wrap = world.get('presentation', {}), world.get('wrap', {})
    regression = mmr63_checks(wrap, a.patches) if wrap else {}
    sections = [k for k in ('copies', 'follow', 'pan', 'moves', 'picking', 'tracks', 'alignment', 'menu', 'mission', 'budget') if k in presentation]
    summary = dict(
        schema='mnbl.evidence.periodic-camera-picking', version=1, ticket='MMR-66',
        generated=datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0).isoformat(),
        sources=dict(run=dict(file=rel(a.run), sha256=sha(a.run), processId=run['processId'], status=run['status'], moduleSha256=run.get('moduleSha256'),
                              args={k: v for k, v in run['args'].items() if k != 'catalogReceipt'}),
                     history=[dict(file=rel(h), sha256=sha(h), status=load(h)['status']) for h in a.history]),
        sections={k: dict(passed=not (presentation[k].get('failures') or presentation[k].get('error')), failures=presentation[k].get('failures'), error=presentation[k].get('error'))
                  for k in sections},
        presentation=trim({k: v for k, v in presentation.items()}),
        wrapRegression=dict(checks=regression, passed=all(regression.values()) if regression else None, failures=wrap.get('failures'),
                            patches=len(wrap.get('patches', [])), expectedPatches=a.patches, displayCopies=wrap.get('displayCopies')),
        failures=dict(presentation=presentation.get('failures'), wrap=wrap.get('failures')))
    with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(summary, f, indent=2)
        f.write('\n')
    print('wrote', a.out, 'presentation failures', len(presentation.get('failures') or []), 'wrap regression', summary['wrapRegression']['passed'])


if __name__ == '__main__':
    main()
