"""MMR-46: summarizes new_campaign_flow runs into evidence/ui/new-campaign-flow/new-campaign-flow.json.

Reads the archived results (evidence/runtime/in-game/new_campaign_flow-<pid>.json), recomputes the run's expectations from its recorded
outputs (not from its own pass/fail lines), and keeps the screenshots' names. Usage:
    python tools/summarize_new_campaign_flow.py <accepted pid> [<earlier pid> ...]
"""
import json
import os
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def load(pid):
    path = os.path.join(REPO, 'evidence', 'runtime', 'in-game', 'new_campaign_flow-%s.json' % pid)
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f), os.path.relpath(path, REPO).replace('\\', '/')


def inside(layout):
    """The panel inside the page, the map and buttons inside the panel, the panel no taller than 1080 units (as the probe checks)."""
    if not layout or 'error' in layout or 'page' not in layout:
        return False
    page = layout['page']
    panel = layout.get('SetupParent')
    if not panel:
        return False
    def box(r):
        return r['x'], r['y'], r['width'], r['height']
    x, y, w, h = box(panel)
    if x < -1 or y < -1 or x + w > page[0] + 1 or y + h > page[1] + 1:
        return False
    for key in ('SetupButtons', 'PreviewMap'):
        r = layout.get(key)
        if not r:
            return False
        rx, ry, rw, rh = box(r)
        if rx < x - 1 or ry < y - 1 or rx + rw > x + w + 1 or ry + rh > y + h + 1:
            return False
    return h / layout['scale'] <= 1080


def checks(o):
    g, rv, back, ns, again, wd, play = (o.get(k) or {} for k in ('generate', 'review', 'back', 'newSeed', 'again', 'withdraw', 'play'))
    preview = rv.get('preview') or {}
    job = rv.get('job') or {}
    cmp_ = play.get('compare') or {}
    ident = play.get('identity') or {}
    creation = wd.get('creation') or {}
    rows = [
        ('generated with a preview of the job\'s world', g.get('state') == 'Generated' and preview.get('worldContentId') == job.get('worldContentId') and preview.get('sceneName') == job.get('sceneName')),
        # Both texture modes render: the default (bytes since the review; file in the first run) and the other one.
        ('preview texture in both modes', (rv.get('texture') or {}).get('current') is True and (
            ((rv.get('texture') or {}).get('mode') == 'bytes' and (rv.get('fileMode') or {}).get('ok') is True) or
            ((rv.get('texture') or {}).get('mode') == 'file' and (rv.get('bytesMode') or {}).get('ok') is True))),
        ('north up (the preview\'s vertical against the map\'s y)', bool(rv.get('orientation')) and rv['orientation'].get('northV', 1) < rv['orientation'].get('southV', 0)),
        ('places by the tab keys, named in full', (rv.get('places') or {}).get('next') == 'NextPlace' and (rv.get('places') or {}).get('previous') == 'PreviousPlace'
         and len((rv.get('places') or {}).get('walked') or []) == 3 and rv['places']['walked'][0] == rv['places']['walked'][2] and rv['places']['walked'][0] != rv['places']['walked'][1]),
        ('pointing at a place names it in full', bool(rv.get('hover')) and rv.get('hover', '').startswith(rv.get('hoverName') or '\0')),
        ('layout fits (normal texts)', inside(rv.get('layout'))),
        ('layout fits (texts 40 % longer)', inside(rv.get('longLayout'))),
        ('Back keeps the job; Confirm reuses it', back.get('jobKept') is True and back.get('reused') is True and back.get('sameJob') is True),
        ('New Seed: random seed, reviewed world released, committed worlds untouched, cancel interrupted',
         ns.get('randomize') == 'NewSeed' and ns.get('source') == 'random' and ns.get('reviewedReleased') is True and ns.get('committedUnchanged') is True and ns.get('category') == 'InterruptedGeneration'),
        ('the same seed gives the same world', again.get('worldContentId') == preview.get('worldContentId') and bool(again.get('rasterSha256'))),
        ('withdrawn start back to review on the same world', wd.get('state') == 'Generated' and wd.get('withdrawn') is True and wd.get('reopened', 0) >= 1 and wd.get('jobPhase') == 'ReadyForReview'),
        ('withdrawn activation rolled back', (wd.get('journal') or {}).get('state') == 'failed' and wd.get('savesBound') == 0 and wd.get('committedUnchanged') is True),
        ('creation cultures kept through going back', bool(creation.get('cultures')) and creation.get('cultures') == creation.get('culturesAfterBack') and creation.get('selectedAfterBack') is not None),
        ('second start committed', play.get('state') == 'Started' and play.get('journal') == 'committed' and play.get('receipt') is True and play.get('instance') not in (None, wd.get('instance'))),
        ('markers match the live campaign', cmp_.get('missing') == 0 and cmp_.get('kindMismatches') == 0 and cmp_.get('positionMismatches') == 0 and cmp_.get('nameMismatches') == 0
         and cmp_.get('kingdomMismatches') == 0 and cmp_.get('regionNameMismatches', 0) == 0 and cmp_.get('liveUnmarked', 0) == 0),
        ('committed world is the previewed one', ident.get('datasetLayoutSha256') == ident.get('previewLayoutSha256') and ident.get('requestWorldContentId') == ident.get('previewWorldContentId')
         and ident.get('sceneName') == ident.get('previewScene')),
    ]
    return [{'check': name, 'passed': bool(ok)} for name, ok in rows]


def summary(pid):
    r, path = load(pid)
    o = r.get('outputs') or {}
    rows = checks(o)
    shots = sorted({os.path.basename(v) for section in o.values() if isinstance(section, dict) for k, v in section.items() if k.lower().endswith('screenshot') and isinstance(v, str)})
    return {
        'pid': pid, 'result': path, 'status': r.get('status'), 'error': r.get('error'),
        'failures': o.get('failures'), 'checks': rows, 'passed': sum(1 for x in rows if x['passed']), 'total': len(rows),
        'generateSeconds': (o.get('generate') or {}).get('seconds'), 'againSeconds': (o.get('again') or {}).get('seconds'),
        'withdrawSeconds': (o.get('withdraw') or {}).get('seconds'), 'playSeconds': (o.get('play') or {}).get('seconds'),
        'preview': (o.get('review') or {}).get('preview'), 'layout': (o.get('review') or {}).get('layout'), 'longLayout': (o.get('review') or {}).get('longLayout'),
        'compare': (o.get('play') or {}).get('compare'), 'identity': (o.get('play') or {}).get('identity'),
        'creation': {'withdraw': (o.get('withdraw') or {}).get('creation'), 'play': (o.get('play') or {}).get('creation')},
        'instances': {'withdrawn': (o.get('withdraw') or {}).get('instance'), 'committed': (o.get('play') or {}).get('instance')},
        'screenshots': shots,
    }


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    runs = [summary(pid) for pid in sys.argv[1:]]
    out = {'schema': 'mnbl.evidence.new-campaign-flow', 'version': 1, 'ticket': 'MMR-46', 'accepted': runs[0], 'earlier': runs[1:]}
    target = os.path.join(REPO, 'evidence', 'ui', 'new-campaign-flow', 'new-campaign-flow.json')
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(out, f, indent=2, ensure_ascii=False)
        f.write('\n')
    for run in runs:
        print('%s %s: %d/%d recomputed checks; status %s' % ('accepted' if run is runs[0] else 'earlier', run['pid'], run['passed'], run['total'], run['status']))
        for row in run['checks']:
            if not row['passed']:
                print('   not passed: ' + row['check'])
    return 0 if runs[0]['passed'] == runs[0]['total'] and runs[0]['status'] == 'ok' else 1


if __name__ == '__main__':
    sys.exit(main())
