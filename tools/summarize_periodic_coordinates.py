"""Summarize an MMR-64 periodic coordinate and distance run into evidence/runtime/periodic-coordinates.json.

The run is generated_campaign on the MMR-63 fixture with wrapProbe=1. Every value is copied or recomputed from the in-game
result file named on the command line (the MMR-63 checks are rerun as the regression, with the MMR-64 patch count). Usage:
  python tools/summarize_periodic_coordinates.py --accepted <result.json> [--history <result.json> ...] --offline <tests.json> --out <summary.json>
"""
import argparse
import datetime
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from summarize_wrap_probe import load, mmr63_checks, rel, sha, wrap_report  # noqa: E402

PATCHES = 16  # MMR-63's 11 entries + the two closest-settlement ordering lambdas + the three distance helpers (--patches for a later build: MMR-65 has 20)


def short(entry):
    v, lower, planar = entry['value'], entry['lowerBound'], entry['planarStraight']
    return v >= lower - 1e-2 and v <= 3 * lower + 30 and v < planar / 2


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--accepted', required=True)
    ap.add_argument('--history', action='append', default=[])
    ap.add_argument('--offline', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--patches', type=int, default=PATCHES, help='periodic patch entries of the build that made the runs')
    a = ap.parse_args()
    accepted = load(a.accepted)
    offline = load(a.offline)
    w = wrap_report(accepted)
    cov = w.get('distanceCoverage') or {}
    refl = cov.get('reflection') or {}
    patches = cov.get('patches') or {}
    values = cov.get('values') or {}
    trans = cov.get('transitions') or {}
    exists = cov.get('pathExists') or {}
    entrance = cov.get('closestEntranceSummary') or {}
    battle = cov.get('battleTerrainMap') or {}
    helpers = cov.get('helpers') or {}
    seam = cov.get('seamLegs') or {}
    seam_numbers = {k: v for k, v in seam.items() if isinstance(v, dict) and 'withoutPeriodicWorld' in v and not isinstance(v.get('value'), str)}
    nearest = seam.get('SettlementHelper.FindNearestSettlementToPoint (towns)') or {}
    save = (w.get('seamSave') or {}).get('savePolicy') or {}
    tie = w.get('tie') or {}
    coords = w.get('coordinates') or {}
    naval = refl.get('activeModel') == 'NavalDLC.GameComponents.NavalDLCMapDistanceModel'
    measured = {k: v for k, v in values.items() if isinstance(v, dict) and 'value' in v}
    l2s, s2l = trans.get('landToSea'), trans.get('seaToLand')
    expected_trans = ((-s2l, -l2s, l2s + s2l) if naval else (0, 0, 0)) if l2s is not None else None
    regression = mmr63_checks(w, a.patches)
    checks = {
        'offlineContract': offline.get('failures') == 0 and offline.get('checks', 0) > 0,
        'coordinatePolicyRecorded': coords.get('version') == 'mmr64/1' and len(coords.get('identity', '')) == 64,
        'coverageManifestMatchesInstalledModel': refl.get('uncovered') == [] and refl.get('stale') == [] and bool(refl.get('installedMembers'))
                                                 and all(v == [] for v in (refl.get('notOverridden') or {'-': ['missing']}).values()),
        'closestSettlementOrderPatched': patches.get('orderingLambdas') == 2 and patches.get('applied') == a.patches,
        'distanceHelpersPatchedAndCovered': helpers.get('uncovered') == [] and len(helpers.get('scan') or []) == 3 and sorted((patches.get('helpers') or {}).values()) == [1, 1, 2],
        'seamLegsPeriodicAndChanged': len(seam_numbers) == 6 and all(v['value'] >= v['lowerBound'] - 1e-2 and v['value'] <= 3 * v['lowerBound'] + 30
                                                                     and abs(v['value'] - v['withoutPeriodicWorld']) > 1 for v in seam_numbers.values())
                                      and seam.get('eastPointEntrance', '').endswith('town_2') and str(nearest.get('value', '')).endswith('town_2')
                                      and not str(nearest.get('withoutPeriodicWorld', '')).endswith('town_2'),
        'settlementCacheSymmetric': values.get('cacheSymmetric') is True,
        'crossSeamOverloadsShortAndBounded': len(measured) >= 9 and all(short(v) for v in measured.values()),
        'directedTransitionCosts': expected_trans is not None and (trans.get('town1 port -> town2 gate'), trans.get('town2 gate -> town1 port'), trans.get('town1 port -> town2 port')) == expected_trans,
        'pathExistsAcrossSeamForMatchingCapability': exists.get('gate1 -> gate2 Default') is True and exists.get('port1 -> port2 Naval') is True,
        'townAcrossSeamIsNeighbour': any('town_2' in n for n in values.get('neighbours of town1 (land)', [])),
        'closestEntranceNearSeamIsPeriodicNearest': entrance.get('samples', 0) > 0 and entrance.get('wrong') == 0,
        'battleTerrainMapWraps': battle.get('stripCells', 0) > 0 and battle.get('mismatchedStripCells') == 0 and battle.get('blockOffsetAtSeam') == 0,
        'saveCarriesPolicyAndRefusesOthers': save.get('saved') == save.get('expected') == coords.get('frameIdentity') and save.get('asSaved') == 'accepted'
                                             and str(save.get('anotherPolicy', '')).startswith('refused') and str(save.get('noPolicy', '')).startswith('refused'),
        'tieIndependentOfCopy': bool(tie.get('routesEqual')) and tie.get('chosenWhenWestCopyPassed') == tie.get('chosen') == tie.get('to'),
        'mmr63Regression': all(regression.values()),
    }
    summary = {
        'schema': 'mnbl.periodic-coordinates-evidence', 'version': 1, 'ticket': 'MMR-64',
        'generatedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'accepted': {'file': rel(a.accepted), 'sha256': sha(a.accepted), 'status': accepted.get('status'), 'pid': accepted.get('processId'),
                     'moduleSha256': accepted.get('moduleSha256')},
        'history': [{'file': rel(h), 'sha256': sha(h), 'status': load(h).get('status')} for h in a.history],
        'offline': {'file': rel(a.offline), 'sha256': sha(a.offline), 'checks': offline.get('checks'), 'failures': offline.get('failures'),
                    'oracle': offline.get('oracle'), 'productionIdentity': offline.get('productionIdentity')},
        'checks': checks, 'passed': all(checks.values()) and accepted.get('status') == 'ok',
        'regressionChecks': regression,
        'coordinates': coords, 'coordinatePolicyText': w.get('coordinatePolicyText'),
        'distanceCoverage': cov, 'tie': tie, 'savePolicy': save,
        'patches': w.get('patches'), 'patchError': w.get('patchError'),
        'counters': {k: w.get(k) for k in ('locatorImagePasses', 'canonicalPicks', 'canonicalPlacements', 'targetsCanonicalized', 'periodicQueries', 'periodicImageWins', 'periodicNativeQueries', 'periodicPruned', 'periodicStartMoves', 'rebases')},
        'failures': w.get('failures'),
        'reproduce': [
            './tools/Test-PeriodicCoordinates.ps1 -Bundle build/research-bundle29/bundles/f596b58c... -Evidence evidence/runtime/periodic-coordinates-tests.json -Log evidence/runtime/periodic-coordinates-tests.txt',
            './tools/Build.ps1', './tools/Deploy-Dev.ps1',
            "./tools/Deploy-SceneSlots.ps1 (installs build/research-wrap63/fixture-c as scene mnbl_generated_00063042)",
            "./tools/Start-OwnedProbe.ps1 -Probe generated_campaign -ProbeTimeoutSeconds 1800 -ArgsJson '{worldCount:1, seed:405570, culture:empire, heroTemplate:spc_legion_of_the_betrayed_leader_0, townPrototype:town_EN2, villagePrototype:village_EN1_3, placement: build/research-wrap63/fixture-c/placement.json, catalogReceipt: evidence/runtime/in-game/campaign_catalog-10212.json, holdSeconds:3, gameplayDays:0, navigationProbe:0, missionProbe:0, wrapProbe:1}'",
            './tools/Collect-OwnedProbe.ps1',
            'python tools/summarize_periodic_coordinates.py --accepted evidence/runtime/in-game/generated_campaign-<pid>.json --offline evidence/runtime/periodic-coordinates-tests.json --out evidence/runtime/periodic-coordinates.json'],
    }
    with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(summary, f, indent=1)
    print('passed=%s failing=%s regressionFailing=%s' % (summary['passed'], [k for k, v in checks.items() if not v], [k for k, v in regression.items() if not v]))


if __name__ == '__main__':
    main()
