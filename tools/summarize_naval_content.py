"""MMR-35: summarize the naval audit of a generated_campaign run (navalAudit=<repo>, settlementFixtures=<repo>, worldCount 2, gameplayDays > 0,
saveProbe=1) and, when given, the generated_save_load run that loads its saves.

Usage: python tools/summarize_naval_content.py RUN_JSON OUT_JSON [LOAD_JSON]
The first world carries the naval fixtures: the installed pirate clans as world aliases homed at the fixture hideout, and the fleet rule with
clan 2 declared landlocked (both fixture towns are port towns, so the declaration is the fixture's, to exercise the rule). The checks read the
probe's records and compare them with values fixed outside the game (naval-content.json):
  * every creation culture's hulls are registered ShipHulls of the policy's type, whose mission ships name the policy's prefabs (their
    files are verified offline), and every upgrade piece's siege engine is registered;
  * each fixture port town has a shipyard, and the engine's CanTownCreateShipFromHull agrees with the policy's tiers for every hull of its
    culture at its level (the fixture's two towns have levels 1 and 3);
  * every ship the installed production and trade made at new game, and after the gameplay window, is where its owner says and held by no
    landlocked clan, the landlocked clan exists, and there are ships;
  * War Sails reads the generated ClanShipOwnershipModel and ShipDistributionModel: installed answers for clans that may hold ships; the
    landlocked clan's parties refused ships and fleet transfers, and its ideal fleet 0;
  * a ship the installed ShipProductionCampaignBehavior.CreateShip built and a lord's party bought (ApplyByTrade) is owned and listed by
    that party alone, which gains naval navigation and a valid anchor, with its naval visual marked for refresh and refreshed by the end of
    the gameplay window;
  * the pirate clans are pirate clans to PiratesCampaignBehavior, homed at a hideout, with ship templates (patrol zones reported);
  * with LOAD_JSON: the world saved with naval content loads with its fleet rule effective, the same landlocked clans and no ship held by
    them, and its pirate clans; the only differences from the save-time snapshots are path state of moving AI parties (MMR-62);
  * the run finished ok with no validation failure.
"""
import hashlib
import json
import os
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def main():
    run_path, out_path = sys.argv[1], sys.argv[2]
    load_path = sys.argv[3] if len(sys.argv) > 3 else None
    run = json.load(open(run_path, encoding='utf-8-sig'))
    worlds = run['outputs']['worlds']
    policy = json.load(open(os.path.join(REPO, 'worldgen/policies/naval-content.json'), encoding='utf-8'))
    tiers = policy['shipyards']['hullTiers']
    checks = []

    def check(ok, label):
        checks.append({'ok': bool(ok), 'label': label})

    check(run.get('status') == 'ok' and len(worlds) == 2, 'the run finished ok with two worlds (%s, %d)' % (run.get('status'), len(worlds)))
    w = worlds[0]
    a = w.get('navalAudit') or {}
    h = a.get('hulls') or {}
    expected_hulls = sum(len(v) for v in policy['hulls']['cultures'].values())
    check(not a.get('error') and h.get('checked') == expected_hulls and not h.get('problems') and h.get('missionShipType') and not h.get('siegeEnginesMissing') and
          h.get('siegeEngines') == len(policy['provenance']['siegeEngines']),
          'world 0: all %s creation-culture hulls are registered ShipHulls of the policy\'s type, whose %s records name the policy\'s %d prefabs (verified with their deck navmeshes in the installed files; %d loaded on the campaign map); all %d upgrade-piece siege engines are registered' % (
              h.get('checked'), h.get('missionShipType'), h.get('prefabs', 0), h.get('prefabsLoadedOnTheMap', 0), h.get('siegeEngines', 0)))
    yards = a.get('shipyards') or {}
    gating_ok = bool(yards)
    detail = []
    for town, y in sorted(yards.items()):
        level = y['shipyardLevel']; culture = y['culture']
        for hull, can in y['canCreate'].items():
            expect = level >= 1 and tiers[policy['hulls']['cultures'][culture][hull]['type']] <= level
            gating_ok = gating_ok and can == expect
        detail.append('%s level %d builds %d of %d' % (town[-6:], level, sum(1 for v in y['canCreate'].values() if v), len(y['canCreate'])))
    check(gating_ok and len({y['shipyardLevel'] for y in yards.values()}) >= 2 and all(y['shipyardLevel'] >= 1 for y in yards.values()),
          'world 0: each port town has a shipyard and the engine\'s CanTownCreateShipFromHull matches the policy tiers for every hull of its culture (%s)' % '; '.join(detail))
    g = w.get('navalAuditAfterGameplay') or {}
    for when, f in (('after the new game', a.get('fleets') or {}), ('after the gameplay window', g.get('fleets') or {})):
        landlocked = f.get('landlocked') or []
        check(not f.get('problems') and (f.get('townShips', 0) + f.get('partyShips', 0)) > 0 and landlocked and all(not (f.get('byClan') or {}).get(c) for c in landlocked),
              'world 0: %s the installed production and trade hold %d ships at settlements and %d in parties (%s), each where its owner says; the landlocked %s holds none' % (
                  when, f.get('townShips', 0), f.get('partyShips', 0), ', '.join('%s %s' % (k[-12:], v) for k, v in sorted((f.get('byClan') or {}).items())), ', '.join(landlocked) or 'clan: MISSING'))
    r = a.get('fleetRule') or {}
    values = r.get('values') or {}
    check(r.get('effectiveModel', '').endswith('GeneratedClanShipOwnershipModel') and r.get('installedModel', '').endswith('NavalDLCClanShipOwnershipModel') and
          r.get('effectiveDistributionModel', '').endswith('GeneratedShipDistributionModel') and r.get('installedDistributionModel', '').endswith('NavalDLCShipDistributionModel') and
          r.get('whileLandlocked') == 0 and (r.get('afterwards') or 0) > 0 and any(v.startswith('0 ') for v in values.values()),
          'world 0: War Sails reads the generated ClanShipOwnershipModel and ShipDistributionModel over its own: ideal fleets %s; %s' % (
              ', '.join('%s %s' % (k[-8:], v) for k, v in sorted(values.items())), '; '.join('%s %s' % (k[-18:], v) for k, v in sorted((r.get('distributionAnswers') or {}).items()))))
    s = a.get('sanctionedPath') or {}
    check(not s.get('error') and s.get('producedAtTown') and s.get('ownedByParty') and s.get('inPartyList') and s.get('leftTown') and s.get('navalNavigation') and s.get('inCampaignParties') and
          s.get('anchorValid') and s.get('visualMarkedDirty') and s.get('playerOwns') and s.get('playerVisualMarkedDirty') and g.get('buyerVisualDirty') is False and g.get('buyerShips', 0) > 0,
          'world 0: a %s the installed CreateShip built at %s and %s bought (ApplyByTrade) is owned and listed by that party alone (ships %s), with naval navigation, a valid anchor and its naval visual marked for refresh; a second (%s) moved to the player party (ApplyByTransferring) had its visual refreshed by the visual layer by the end of the window' % (
              s.get('hull'), (s.get('seller') or '')[-6:], s.get('party'), s.get('partyShips'), s.get('playerShip')))
    p = a.get('pirates') or {}
    check(sorted(p) == ['northern_pirates', 'southern_pirates'] and all(v['isPirateClan'] and v['homeIsHideout'] and v['templateShipStacks'] > 0 for v in p.values()),
          'world 0: the pirate clan aliases are pirate clans to PiratesCampaignBehavior, homed at a hideout, with ship templates (%s); patrol zones on the generated scene: %s (PirateSpawnPoint entities are MMR-40\'s)' % (
              ', '.join('%s home %s, %d stacks' % (k, (v['home'] or '')[-9:], v['templateShipStacks']) for k, v in sorted(p.items())), ', '.join('%s %s' % (k, v['patrolZones']) for k, v in sorted(p.items()))))
    for i, x in enumerate(worlds):
        check(not x.get('validationFailures'), 'world %d: no validation failure (%d)' % (i, len(x.get('validationFailures') or [])))
    load_summary = None
    if load_path:
        load = json.load(open(load_path, encoding='utf-8-sig'))
        rows = load['outputs'].get('worlds', [])
        n0 = (rows[0].get('naval') if rows else None) or {}
        n1 = (rows[1].get('naval') if len(rows) > 1 else None) or {}
        path_state = lambda m: m.startswith('.parties.') and ('.nextTarget[' in m or '.saved[3][' in m)
        mismatches = [m for r_ in rows for m in (r_.get('mismatches') or [])]
        other = [f for r_ in rows for f in (r_.get('failures') or []) if 'values differ from the saved snapshot' not in f]
        check(len(rows) == 2 and not other and all(path_state(m) for m in mismatches) and n0.get('restored') and n0.get('model') and n0.get('effective') and
              n0.get('landlockedAtSave') and n0.get('landlockedAtSave') == n0.get('landlockedLoaded') and n0.get('landlockedShips') == 0 and n0.get('pirateClans') == 2 and
              n1.get('restored') is False and n1.get('model') is False,
              'world 0 loads with its fleet rule effective, the same landlocked clans (%s) holding no ship, and its 2 pirate clans; world 1 (no naval content) loads without the models; the only snapshot differences are %d path-state values of moving AI parties (MMR-62)' % (
                  ', '.join(n0.get('landlockedLoaded') or []), len(mismatches)))
        load_summary = {'run': load_path.replace('\\', '/'), 'pid': load.get('processId'), 'worlds': [r_.get('naval') for r_ in rows]}
    summary = {'schema': 'mnbl.evidence.naval-audit', 'version': 1, 'ticket': 'MMR-35', 'run': run_path.replace('\\', '/'), 'pid': run.get('processId'),
               'moduleSha256': run.get('moduleSha256'), 'policySha256': sha(os.path.join(REPO, 'worldgen/policies/naval-content.json')),
               'navalAudit': a, 'afterGameplay': w.get('navalAuditAfterGameplay'), 'load': load_summary,
               'checks': checks, 'passed': sum(x['ok'] for x in checks), 'total': len(checks)}
    with open(out_path, 'w', encoding='utf-8', newline='\n') as fh:
        json.dump(summary, fh, indent=1)
        fh.write('\n')
    for x in checks:
        print(('ok    ' if x['ok'] else 'FAIL  ') + x['label'])
    print('%d/%d checks passed' % (summary['passed'], summary['total']))
    sys.exit(0 if summary['passed'] == summary['total'] else 1)


if __name__ == '__main__':
    main()
