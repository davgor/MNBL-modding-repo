"""Summarize an MMR-63 periodic east/west seam run into evidence/runtime/world-wrap-probe.json.

Every value is copied or counted from the in-game result file named on the command line; nothing is
re-derived offline. Per-sample travel tracks are cut to the samples around each seam crossing. Usage:
  python tools/summarize_wrap_probe.py --accepted <result.json> [--history <result.json> ...] --out <summary.json>
"""
import argparse
import datetime
import hashlib
import json
import os

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LEGS = ['player land east->west', 'player land west->east', 'ai land east->west', 'ai land west->east',
        'player sea east->west', 'player sea west->east', 'ai sea east->west', 'ai sea west->east']


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def rel(path):
    path = os.path.abspath(path)
    return os.path.relpath(path, REPO).replace('\\', '/') if path.startswith(REPO) else path


def load(path):
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f)


def wrap_report(result):
    report = result['outputs'].get('report') or result['outputs']
    for world in report.get('worlds', []):
        if 'wrap' in world:
            return world['wrap']
    raise SystemExit('no wrap report in ' + str(result.get('processId')))


def crossing(track, width):
    # The samples on either side of the largest jump in logical x (the seam crossing), five each way.
    if not track:
        return []
    jumps = [(abs(track[i][2] - track[i - 1][2]), i) for i in range(1, len(track))]
    if not jumps:
        return track
    _, i = max(jumps)
    return track[max(0, i - 5):i + 5]


def leg(record, width):
    out = {k: v for k, v in record.items() if k != 'track'}
    out['trackSamples'] = len(record.get('track', []))
    out['trackAtCrossing'] = crossing(record.get('track', []), width)
    return out


def click_canonical(click, geometry):
    # The stored order target must be the canonical image of the clicked display point.
    if not click or not click.get('storedTarget') or not click.get('displayPoint'):
        return False
    lo, hi = geometry['canonicalRange']
    sx, sy = click['storedTarget']
    dx, dy = click['displayPoint']
    expected = dx - geometry['width'] if dx >= hi else dx + geometry['width'] if dx < lo else dx
    return lo <= sx < hi and abs(sx - expected) < 0.01 and abs(sy - dy) < 0.01


def direction_ok(d):
    if not d or not d.get('patchEncounterDir') or not d.get('expectedPeriodic'):
        return False
    p, e = d['patchEncounterDir'], d['expectedPeriodic']
    return p[0] * e[0] + p[1] * e[1] >= 0.9


def leg_ok(record):
    return bool(record and record.get('arrived') and record.get('rebases', 0) >= 1 and record.get('maxStepPeriodic', 99) <= 5
                and record.get('canonicalThroughout'))


def mmr63_checks(w, expected_patches=11):
    # The MMR-63 checks of one wrap report (MMR-64 reuses them as its regression with its own patch count).
    width = w['geometry']['width']
    travel = {r['leg']: r for r in w.get('travel', [])}
    seam = w.get('seamEncounter', {})
    picking = w.get('picking', {})
    save = w.get('seamSave', {})
    land = [r for r in w['routes'] if r['type'] == 'Default']
    sea = [r for r in w['routes'] if r['type'] == 'Naval']
    start = seam.get('encounterStart') or {}
    ret = seam.get('afterReturn') or {}
    reloaded = save.get('reloaded') or {}
    checks = {
        'settlementsCanonical': all(s['canonical'] for s in w['geometry']['settlements']),
        'displayCopiesWithoutIdentity': bool((w.get('displayCopies') or {}).get('entities')) and w['displayCopies'].get('campaignObjectsNamedDisplay') == 0
                                        and w['displayCopies'].get('settlementIdsUnique') is True,
        'landRoutesShortThroughSeam': bool(land) and all(r['periodicFound'] and r['periodicDistance'] < 0.2 * r['straightPlanar'] for r in land),
        'seaRoutesShortThroughSeam': bool(sea) and all(r['periodicFound'] and r['periodicDistance'] < 0.2 * r['planarDistance'] for r in sea),
        'distanceModelPeriodic': w['distanceModel']['town1ToTown2Land'] < 0.2 * w['distanceModel']['planarSettlementDistance'],
        'tieDeterministic': bool(w['tie'].get('routesEqual')) and w['tie'].get('chosen') == w['tie'].get('to'),
        'allTravelLegsRun': all(name in travel for name in LEGS),
        'travelLegsCrossShort': all(leg_ok(travel.get(name)) for name in LEGS),
        'partyDistancesPeriodic': bool(seam.get('distanceModelFromPlayer')) and seam['distanceModelFromPlayer'].get('toEnemy', 99) < 5
                                  and seam['distanceModelFromPlayer'].get('toWestTown', 999) < width / 4,
        'locatorFindsAcrossSeam': seam.get('setup', {}).get('enemy') in seam.get('locatorAroundPlayer', []),
        'interactionRangeAcrossSeam': seam.get('canPartyInteract') is True,
        'visibleAcrossSeam': seam.get('enemyVisible') is True,
        'engineEncounterAcrossSeam': bool(start.get('oppositeSides')),
        'mapEventAtParties': (seam.get('battleStart') or {}).get('mapEventDistanceToPlayer', 99) <= 2 and (seam.get('battleStart') or {}).get('mapEventCanonical') is True,
        'encounterDirectionPeriodic': direction_ok(seam.get('encounterDirection')),
        'battleEnteredAndReturned': bool(seam.get('mission')) and ret.get('canonical') is True and ret.get('displacement', 99) <= 0.635,
        'stripClickStoresCanonical': click_canonical(picking.get('click'), w['geometry']),
        'stripClickTravelsShort': leg_ok(picking.get('travel')),
        'cameraFollowsAcrossSeam': all(0 < (r or {}).get('cameraFrameMaxDistance', 0) < 40
                                       for r in [travel.get(n) for n in LEGS if n.startswith('player')] + [picking.get('travel')]),
        'saveNearSeamCanonical': bool(save.get('saveSucceeded')) and bool(save.get('deserialized')) and reloaded.get('mismatched') == 0
                                 and reloaded.get('nonCanonical') == 0 and reloaded.get('parties') == reloaded.get('liveParties'),
        'patchesApplied': w.get('patchError') is None and len(w.get('patches', [])) == expected_patches,
        'noNonCanonicalPartiesAtEnd': w.get('nonCanonicalPartiesAtEnd') == [],
        'noFailures': w.get('failures') == [],
    }
    return checks


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--accepted', required=True)
    ap.add_argument('--history', action='append', default=[])
    ap.add_argument('--out', required=True)
    a = ap.parse_args()
    accepted = load(a.accepted)
    w = wrap_report(accepted)
    width = w['geometry']['width']
    checks = mmr63_checks(w)
    travel = {r['leg']: r for r in w.get('travel', [])}
    seam = w.get('seamEncounter', {})
    picking = w.get('picking', {})
    save = w.get('seamSave', {})
    summary = {
        'schema': 'mnbl.world-wrap-evidence', 'version': 1, 'ticket': 'MMR-63',
        'generatedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'accepted': {'file': rel(a.accepted), 'sha256': sha(a.accepted), 'status': accepted.get('status'), 'pid': accepted.get('processId'),
                     'moduleSha256': accepted.get('moduleSha256')},
        'history': [{'file': rel(h), 'sha256': sha(h), 'status': load(h).get('status')} for h in a.history],
        'checks': checks, 'passed': all(checks.values()) and accepted.get('status') == 'ok',
        'geometry': w['geometry'], 'displayCopies': w.get('displayCopies'), 'routes': w['routes'], 'distanceModel': w['distanceModel'], 'tie': w['tie'],
        'travel': [leg(travel[name], width) for name in LEGS if name in travel],
        'seamEncounter': seam, 'picking': dict(picking, travel=leg(picking['travel'], width)) if picking.get('travel') else picking,
        'seamSave': save, 'patches': w.get('patches'), 'patchError': w.get('patchError'),
        'counters': {k: w.get(k) for k in ('locatorImagePasses', 'canonicalPicks', 'canonicalPlacements', 'targetsCanonicalized', 'periodicQueries', 'periodicImageWins', 'periodicNativeQueries', 'periodicPruned', 'rebases')},
        'nonCanonicalPartiesAtEnd': w.get('nonCanonicalPartiesAtEnd'),
        'reproduce': [
            './tools/Build.ps1', './tools/Deploy-Dev.ps1',
            "./tools/Deploy-SceneSlots.ps1 (installs build/research-wrap63/fixture-c as scene mnbl_generated_00063042)",
            "./tools/Start-OwnedProbe.ps1 -Probe generated_campaign -ProbeTimeoutSeconds 1800 -ArgsJson '{worldCount:1, seed:405570, culture:empire, heroTemplate:spc_legion_of_the_betrayed_leader_0, townPrototype:town_EN2, villagePrototype:village_EN1_3, placement: build/research-wrap63/fixture-c/placement.json, catalogReceipt: evidence/runtime/in-game/campaign_catalog-10212.json, holdSeconds:3, gameplayDays:0, navigationProbe:0, missionProbe:0, wrapProbe:1}'",
            './tools/Collect-OwnedProbe.ps1',
            'python tools/summarize_wrap_probe.py --accepted evidence/runtime/in-game/generated_campaign-<pid>.json --out evidence/runtime/world-wrap-probe.json'],
    }
    with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(summary, f, indent=1)
    print('passed=%s %s' % (summary['passed'], json.dumps({k: v for k, v in checks.items() if not v})))


if __name__ == '__main__':
    main()
