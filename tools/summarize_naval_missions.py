"""MMR-53: summarizes the naval_missions probe (evidence/runtime/in-game/naval_missions-<pid>.json) into evidence/gameplay/naval-scenarios.json.
Every case of tests/Runtime/NavalScenarios.json is checked from the archived outputs with the scenario file's rules, not from the probe's
pass lines.
Usage: python tools/summarize_naval_missions.py <pid> [--fixture]   (--fixture also writes tests/Runtime/fixtures/naval-missions-<pid>.json)
"""
import copy
import hashlib
import json
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCENARIOS = os.path.join(REPO, 'tests', 'Runtime', 'NavalScenarios.json')


def clean(case):
    c = case or {}
    return [('the case ran to its end without error (%s)' % ((c.get('error') or '').split('\n')[0][:160] if c else 'not run'), bool(c) and 'seconds' in c and 'error' not in c and not c.get('failures'))]


def back(case, rules, bound_key='returnBoundLand'):
    c = case or {}
    d = c.get('returnDisplacement')
    where = (c.get('returnExpected') or {}).get('reason') or 'the entry position'
    bound = c.get('returnBound') if bound_key == 'case' else rules[bound_key]
    return [('the party returned on valid navigation (%s)' % c.get('afterNavigation'), 'after' in c and c.get('afterNavigation') is None),
            ('back at %s within the return bound: %s of %s' % (where, 'none' if d is None else '%.3f' % d, bound), d is not None and bound is not None and d <= bound + 1e-9)]


def started(case, rules):
    """The case started where the previous step left the party: every placement at a gate within the land return bound of where it was."""
    moves = (case or {}).get('relocations') or []
    far = [round(m.get('distance') or 0, 3) for m in moves]
    return [('started where the previous step left the party (placed %s from it, bound %s)' % (far, rules['returnBoundLand']),
             bool(moves) and all((m.get('distance') if m.get('distance') is not None else 1e9) <= rules['returnBoundLand'] + 1e-9 for m in moves))]


def voyage(case, kind):
    c = case or {}
    s, v = c.get('sailed') or {}, c.get('voyage') or {}
    o = c.get('order') or {}
    return [('the port menu\'s set sail put the party at sea at the port (%s from it, navigation %s)' % (s.get('fromPort'), s.get('navigation')), s.get('atSea') is True and s.get('settlement') is None and s.get('navigation') is None and (s.get('fromPort') or 0) < 0.05),
            ('the map click ordered a %s voyage to the port (%s, targeting the port %s)' % (kind, o.get('navigation'), o.get('targetingPort')), o.get('canNavigate') is True and o.get('targetingPort') is True),
            ('the voyage arrived: %.1f h, %.1f sailed, %s hourly samples off naval navigation, narrowest water %s wide' % (v.get('hours') or 0, v.get('distance') or 0, v.get('offNavigationSamples'), v.get('minWidth')),
             v.get('arrived') is True and v.get('offNavigationSamples') == 0)]


def patch_scene(case):
    c = case or {}
    m = c.get('mission') or {}
    exp = ((c.get('before') or {}).get('expectedBattleScene') or {}).get('scene') or c.get('expectedScene')
    return ('the naval mission opened the patch\'s naval scene %s (%s)' % (exp, m.get('scene')), c.get('sceneMatchesPatch') is True and bool(m.get('scene')))


def recompute(result, plan):
    o = result.get('outputs') or {}
    rules = plan['rules']
    cases = o.get('cases') or {}
    rows = {}

    buy, repair = cases.get('shipyard.buy') or {}, cases.get('shipyard.repair') or {}
    bs, b, ship = buy.get('setup') or {}, buy.get('bought') or {}, buy.get('ship') or {}
    rs, rp = repair.get('setup') or {}, repair.get('repaired') or {}
    cb = bs.get('canBuy') if isinstance(bs.get('canBuy'), dict) else {}
    rows['shipyard'] = clean(buy) + [
        ('the port screen\'s buy button enabled for the ship (%s)' % cb, cb.get('relevant') is True and cb.get('enabled') is True),
        ('bought through the port screen\'s trade handler (confirm %s): %s (%s) for %s' % (bs.get('canConfirm'), ship.get('name'), ship.get('hull'), ship.get('price')),
         bs.get('canConfirm') is True and b.get('owner') is True and b.get('inParty') is True and b.get('goldPaid') == ship.get('price')),
        ('the bought fleet docked at the town (the screen\'s anchor fix-up)', b.get('anchorDocked') is True and b.get('naval') is True)] + back(buy, rules) + clean(repair) + started(repair, rules) + [
        ('the port menu\'s repair restored the ship (%s of %s) for the model\'s cost %s (paid %s)' % (rp.get('hitPoints'), rs.get('maxHitPoints'), rs.get('repairCost'), rp.get('goldPaid')),
         rp.get('hitPoints') is not None and rs.get('maxHitPoints') is not None and rp.get('hitPoints') >= rs.get('maxHitPoints') - 0.01 and rp.get('goldPaid') == rs.get('repairCost') and (rs.get('repairCost') or 0) > 0)]

    voyage_ids = o.get('voyage') or {}

    def route(key, kind):
        c = cases.get(key) or {}
        a = c.get('arrival') or {}
        target = (voyage_ids.get(kind) or {}).get('id')
        return clean(c) + started(c, rules) + voyage(c, kind) + [
            ('arrived in %s from the sea: on land, the fleet docked (port menu, then the town)' % a.get('settlement'), a.get('settlement') == target and a.get('atSea') is False and a.get('anchorDocked') is True)] + back(c, rules)
    rows['route.port'] = route('route.port', 'mainland')
    rows['route.island'] = route('route.island', 'island')

    d = cases.get('dropoff') or {}
    da, dd, dr = d.get('arrival') or {}, d.get('disembarked') or {}, d.get('reembarked') or {}
    co = d.get('coastalLanding') or {}
    cl, cr = co.get('landed') or {}, co.get('reboarded') or {}
    rows['dropoff'] = clean(d) + started(d, rules) + voyage(d, 'harbour village') + [
        ('in the harbour village %s from the sea, still at sea' % da.get('settlement'), da.get('settlement') == (voyage_ids.get('village') or {}).get('id') and da.get('atSea') is True),
        ('the village menu\'s disembark: on land at the gate, the fleet at the village\'s port (%s from it)' % dd.get('anchorFromPort'),
         dd.get('atSea') is False and dd.get('navigation') is None and dd.get('anchorAtVillage') is True),
        ('back aboard at the village\'s anchor (%s from it, navigation %s)' % (dr.get('fromAnchor'), dr.get('navigation')), dr.get('atSea') is True and dr.get('navigation') is None and (dr.get('fromAnchor') or 0) < 0.05)] + back(d, rules)[1:] + [
        ('a click on land from the sea (%s navigation, %.2f away) brought the party ashore through the sea-to-land transition (%s h) on land navigation (%s)' % (co.get('navigation'), co.get('fromSea') or 0, cl.get('transitionHours'), cl.get('navigation')),
         co.get('canNavigate') is True and cl.get('atSea') is False and cl.get('sawTransition') is True and cl.get('navigation') is None and 'menuOnTheWay' not in co),
        ('the landing left the anchor at the coast (%s from where the party came ashore, not at the village)' % cl.get('anchorFromAshore'), cl.get('anchorValid') is True and cl.get('anchorAtVillage') is False),
        ('back aboard at the coastal anchor (%s from it, navigation %s)' % (cr.get('fromAnchor'), cr.get('navigation')), cr.get('atSea') is True and cr.get('navigation') is None and (cr.get('fromAnchor') or 0) < 0.05)]

    legs = [(k, (cases.get(k) or {}).get('voyage') or {}) for k in ('route.port', 'route.island', 'dropoff', 'raid')]
    narrow = [(k, round(v.get('minWidth'), 2)) for k, v in legs if isinstance(v.get('minWidth'), (int, float)) and v.get('minWidth') >= 0]
    targets = plan.get('targets') or {}
    chosen = {k: (voyage_ids.get(k) or {}).get('id') for k in ('start', 'mainland', 'island', 'village')}
    chosen['raidVillage'] = (cases.get('raid') or {}).get('village')
    chosen['blockadeTown'] = (cases.get('blockade') or {}).get('settlement')
    rows['targets'] = [('the run sailed the pinned regression targets (%s)' % chosen, bool(targets) and voyage_ids.get('pinned') is True and all(chosen.get(k) == v for k, v in targets.items() if k in chosen)),
                       ('the narrowest water of every voyage recorded away from both ends (width %s)' % narrow, len(narrow) == len([1 for k, v in legs if v]) and len(narrow) > 0)]

    t = o.get('traffic') or {}
    emb, dis = sum((t.get('embarked') or {}).values()), sum((t.get('disembarked') or {}).values())
    rows['ai'] = [('AI parties took to the sea (%s) and landed (%s) while the cases ran' % (t.get('embarked'), t.get('disembarked')), emb > 0 and dis > 0),
                  ('every AI party checked an hour after its transition was on its domain\'s navigation (%s problems of %s checked: %s)' % (t.get('problemCount'), t.get('checked'), (t.get('problems') or [])[:2]),
                   (t.get('checked') or 0) > 0 and t.get('problemCount') == 0),
                  ('AI parties arrived from the sea at port towns, landing and entering in the same tick: %s (%s)' % (t.get('portArrivals'), t.get('arrivalsByKind')), (t.get('portArrivals') or 0) > 0),
                  ('AI parties arrived from the sea at island port towns: %s, at %s of %s island port towns' % (t.get('islandArrivals'), len(t.get('islandsReached') or []), t.get('islandPortTowns')), (t.get('islandArrivals') or 0) > 0)]

    p = o.get('pirates') or {}
    clans = p.get('clans') or []
    uneven = ['%s %s of %s' % (c.get('clan'), c.get('served'), c.get('planned')) for c in clans if c.get('served') != c.get('planned')]
    rows['pirates'] = [
        ('the wrapper serves the world\'s pirate spawn points (C06 through W03): %s of %s planned, frame error %s' % (p.get('served'), p.get('planPoints'), p.get('frameError')),
         (p.get('planPoints') or 0) > 0 and p.get('served') == p.get('planPoints') and p.get('frameError') is None),
        ('each pirate clan is served the plan\'s points for it (%s; mismatched %s) and the plan has none for other clans (%s of %s)' % (['%s %s' % (c.get('clan'), c.get('served')) for c in clans], uneven, p.get('plannedForPirateClans'), p.get('planPoints')),
         bool(clans) and not uneven and p.get('plannedForPirateClans') == p.get('planPoints')),
        ('every served point is on naval navigation (%s off)' % p.get('servedOffNaval'), p.get('servedOffNaval') == 0),
        ('the patrol zones are exactly the served points: %s zones, %s served, %s zones on a served point; the inner scene\'s own points %s (0 by construction)' % (p.get('zones'), p.get('served'), p.get('zonesFromServed'), p.get('fromScene')),
         p.get('zones') == p.get('served') == p.get('zonesFromServed') and (p.get('served') or 0) > 0 and p.get('fromScene') == 0),
        ('pirate parties of %s clans patrol: %s, all at sea (%s not), on naval navigation (%s off), each assigned a served zone (%s; %s unassigned)' % (p.get('pirateClans'), p.get('parties'), p.get('notAtSea'), p.get('offNavigation'), p.get('inServedZone'), p.get('unassigned')),
         (p.get('parties') or 0) > 0 and p.get('notAtSea') == 0 and p.get('offNavigation') == 0 and p.get('inServedZone') == p.get('parties') and p.get('unassigned') == 0)]

    pb = cases.get('pirate.battle') or {}
    m, me, ps = pb.get('mission') or {}, pb.get('mapEvent') or {}, pb.get('setup') or {}
    rows['pirate.battle'] = clean(pb) + [
        ('the pirate party %s (%s ships) met at sea in a naval map event of exactly the two parties (%s)' % (ps.get('pirate'), ps.get('pirateShips'), me.get('involved')),
         me.get('naval') is True and len(me.get('involved') or []) == 2 and 'player_party' in (me.get('involved') or [])),
        patch_scene(pb),
        ('the naval battle mission ran (%.2f s of mission time), then the retreat' % (m.get('missionTimeAdvanced') or 0), (m.get('missionTimeAdvanced') or 0) > 0),
        ('back at sea within the naval return bound (%s)' % pb.get('returnDisplacement'), (pb.get('after') or {}).get('atSea') is True and pb.get('returnDisplacement') is not None and pb.get('returnDisplacement') <= rules['returnBoundNaval']),
        ('on naval navigation (%s)' % pb.get('afterNavigation'), 'after' in pb and pb.get('afterNavigation') is None),
        ('the fleet after returning: %s ships before, %s after (hit points %s -> %s)' % (len((pb.get('fleetBefore') or {}).get('ships') or []), len((pb.get('fleetAfter') or {}).get('ships') or []),
            [x.get('hitPoints') for x in (pb.get('fleetBefore') or {}).get('ships') or []], [x.get('hitPoints') for x in (pb.get('fleetAfter') or {}).get('ships') or []]),
         bool(pb.get('fleetBefore')) and bool(pb.get('fleetAfter')) and len((pb.get('fleetAfter') or {}).get('ships') or []) == len((pb.get('fleetBefore') or {}).get('ships') or []) > 0),
        ('the pirate sent back where it was found, patrolling its zone (%s)' % ps.get('pirateRestored'), ps.get('pirateRestored') == 'patrol')]

    nd = cases.get('naval.defeat') or {}
    nf, rf, ae, ns = nd.get('fight') or {}, nd.get('raft') or {}, nd.get('afterEscape') or {}, nd.get('setup') or {}
    raft_menus = nd.get('raftMenus') or []
    rows['naval.defeat'] = clean(nd) + [
        ('another pirate party than the retreat\'s (%s, not %s)' % (ns.get('pirate'), ps.get('pirate')), bool(ns.get('pirate')) and ns.get('pirate') != ps.get('pirate')),
        patch_scene(nd),
        ('defeated in the naval mission (%s fell)' % nf.get('fallen'), nf.get('defeated') is True),
        ('taken prisoner by %s (at sea %s), the fleet after the defeat: %s ships' % (nd.get('captor'), nd.get('captorAtSea'), len((nd.get('fleetAfterDefeat') or {}).get('ships') or [])), nd.get('captured') is True and 'fleetAfterDefeat' in nd),
        ('the forced escape freed the party at sea (%s)' % ae, nd.get('escaped') is True and ae.get('atSea') is True),
        ('War Sails\' raft carried the party (raft state seen %s; menus %s, its wait and its end among them)' % (rf.get('sawRaft'), raft_menus),
         rf.get('sawRaft') is True and 'player_raft_state_wait' in raft_menus and 'player_raft_state_end' in raft_menus),
        ('the raft washed ashore: on land, out of raft state, ships %s of %s' % (rf.get('ships'), rf.get('shipsBefore')), rf.get('atSea') is False and rf.get('raftAtEnd') is False),
        ('on land navigation (%s)' % nd.get('afterNavigation'), 'after' in nd and nd.get('afterNavigation') is None),
        ('the pirate sent back where it was found, patrolling its zone (%s)' % ns.get('pirateRestored'), ns.get('pirateRestored') == 'patrol')]

    rd = cases.get('raid') or {}
    rm, rfi, rsel = rd.get('mission') or {}, rd.get('fight') or {}, (rd.get('setup') or {}).get('navalTroopSelection') or {}
    tags = rfi.get('sceneTags') or {}
    lnd = rfi.get('landing') or {}
    events = rd.get('mapEventsEnded') or []
    rows['raid'] = clean(rd) + voyage(rd, 'raid')[1:] + [
        ('at sea in the village %s, its hostile action\'s raid' % rd.get('village'), (rd.get('arrival') or {}).get('atSea') is True),
        ('the naval troop selection answered: %s of %s troops, ships %s' % (rsel.get('selected'), rsel.get('offered'), rsel.get('ships')), bool(rsel) and (rsel.get('selected') or 0) >= (rsel.get('minTroops') or 1)),
        ('the naval raid opened the village\'s center scene %s (%s) in its %s level, with its landing setup (%s)' % (rd.get('expectedScene'), rm.get('scene'), rfi.get('sceneLevels'), tags),
         rm.get('scene') == rd.get('expectedScene') and rd.get('expectedScene') is not None and rfi.get('sceneLevels') == 'naval_raid' and (tags.get('landing') or 0) > 0 and (tags.get('playerSpawnFrame') or 0) > 0),
        ("the landing ran as War Sails' raid controller runs it: %s landing paths read, landing completed %s after %s s, %s of the player's agents ashore (%s on a deck, %s untracked)" % (lnd.get('paths'), lnd.get('completed'), round(lnd.get('seconds') or 0, 1), lnd.get('playerAgentsAshore'), lnd.get('playerAgentsAboard'), lnd.get('playerAgentsUntracked')),
         (lnd.get('paths') or 0) > 0 and lnd.get('started') is True and lnd.get('completed') is True and (lnd.get('playerAgentsAshore') or 0) > 0),
        ('the raid won (%s defenders killed by the probe) and the village looted (%s)' % (rfi.get('killed'), rd.get('villageState')), rfi.get('victory') is True and rd.get('villageState') == 'Looted'),
        ('the raid\'s end menu %s answered, with the map events that ended at the village recorded (%s)' % (rd.get('raidEndMenu'), [(e.get('type'), e.get('isRaid'), e.get('attackerLeaderIsMain'), e.get('villageRaided')) for e in events]),
         rd.get('raidEndMenu') in ('village_player_raid_ended', 'village_raid_ended_leaded_by_someone_else') and len(events) > 0),
        ('still at sea after the raid', rd.get('atSeaAfter') is True),
        ('the raid\'s return checked at sea with the naval bound (%s)' % rd.get('returnBound'), rd.get('returnBound') == rules['returnBoundNaval'])] + back(rd, rules, 'case')

    bl = cases.get('blockade') or {}
    bb, bf, bset = bl.get('blockade') or {}, bl.get('lifted') or {}, bl.get('setup') or {}
    arcs = bb.get('arcs') or {}
    mb = bset.get('movedBack') or {}
    rows['blockade'] = clean(bl) + [
        ('the party moved to the besieged town (recorded: %s away)' % (bset.get('relocatedFrom') or {}).get('distance'), bool(bset.get('relocatedFrom'))),
        ('the land siege of the port town %s by a party with ships activated a blockade (%s activation events, blockading %s)' % (bl.get('settlement'), bb.get('activatedEvents'), bb.get('blockadingParties')),
         bb.get('active') is True and (bb.get('activatedEvents') or 0) > 0),
        ('the player\'s anchor disabled while blockading', bb.get('anchorDisabled') is True),
        ('the town\'s blockade arcs on water: %s of the fleet\'s %s points and %s of the full arcs\' %s off naval navigation' % (arcs.get('offNaval'), arcs.get('points'), arcs.get('fullOffNaval'), arcs.get('fullPoints')),
         arcs.get('script') is True and (arcs.get('fullPoints') or 0) > 0 and arcs.get('offNaval') == 0 and arcs.get('fullOffNaval') == 0),
        ('the siege lifted through the menu: the blockade deactivated (%s), the anchor enabled' % bf.get('deactivatedEvents'), bf.get('siege') is False and (bf.get('deactivatedEvents') or 0) > 0 and bf.get('anchorDisabled') is False),
        ('on land navigation after (%s)' % bl.get('afterNavigation'), 'after' in bl and bl.get('afterNavigation') is None),
        ('the party moved back to where the case found it (%s away, navigation %s)' % (mb.get('distance'), mb.get('navigation')), mb.get('distance') is not None and mb.get('distance') <= 1e-3 and mb.get('navigation') is None)]

    asset_rows = []
    for name, c in sorted(cases.items()):
        log = c.get('log') or {}
        asset_rows.append(('%s: no missing asset in the log (%s of the world, %s of an unknown kind), no engine assert (%s, canary %s)' % (name, log.get('worldMissingCount'), log.get('otherMissingCount'), c.get('assertCount'), c.get('assertCanarySeen')),
                           log.get('read') is True and log.get('worldMissingCount') == 0 and log.get('otherMissingCount') == 0 and c.get('assertCount') == 0 and c.get('assertCanarySeen') is True))
        m = c.get('mission')
        if isinstance(m, dict) and 'scene' in m:
            sp = m.get('spawns') or {}
            asset_rows.append(('%s mission: every agent spawned on the navigation mesh or a scene object inside the boundaries (%s agents, %s on objects %s, %s with nothing under, %s outside)' % (name, sp.get('agents'), sp.get('offNavmeshUsingObject'), sp.get('usedObjects'), sp.get('offNavmesh'), sp.get('outsideBoundaries')),
                               (sp.get('agents') or 0) > 0 and sp.get('offNavmesh') == 0 and sp.get('outsideBoundaries') == 0 and sp.get('mainOnNavmesh') is True))
    rows['assets'] = asset_rows or [('cases recorded', False)]
    bl_ = o.get('baselines') or []
    rows['baselines'] = [('the run\'s rules are the scenario file\'s', o.get('rules') == plan['rules'])] + \
        [('%s: no game, every tracked static at its baseline' % x.get('at'), x.get('game') is False and x.get('held') == {} and (x.get('tracked') or 0) >= 15) for x in bl_] + \
        [('the %s baseline recorded' % at, any(x.get('at') == at for x in bl_)) for at in ('start', 'end')]

    scenarios = []
    for s in plan['cases']:
        checks = [{'check': n, 'passed': bool(ok)} for n, ok in rows.get(s['id'], [('no outputs for the case', False)])]
        scenarios.append({'id': s['id'], 'source': s['source'], 'what': s['what'], 'passed': all(c['passed'] for c in checks), 'checks': checks})
    total = sum(len(s['checks']) for s in scenarios)
    passed = sum(c['passed'] for s in scenarios for c in s['checks'])
    return scenarios, passed, total


def trim(result):
    """The run's result without what no check reads (stages, timings, voyage samples, the snapshots' detail), for a committed fixture; the
    repository root becomes <repo> and any other user home <home> in every string."""
    r = copy.deepcopy({k: v for k, v in result.items() if k not in ('stages', 'timings', 'requestFile', 'store', 'menuScreen')})
    o = r.get('outputs') or {}
    for case in (o.get('cases') or {}).values():
        case.pop('samples', None)
        for snapshot in ('before', 'after'):
            snap = case.get(snapshot)
            if isinstance(snap, dict):
                case[snapshot] = {k: v for k, v in snap.items() if k in ('position', 'isOnLand', 'atSea', 'face', 'expectedBattleScene')}
        mission = case.get('mission')
        if isinstance(mission, dict):
            mission.pop('initializerRecord', None)
            mission.pop('terrainSamples', None)
    v = o.get('load')
    if isinstance(v, dict):
        v.pop('loader', None)
    roots = sorted({REPO, REPO.replace('\\', '/'), REPO.replace('/', '\\')}, key=len, reverse=True)
    home = re.compile(r'[A-Za-z]:[\\/]Users[\\/][^\\/"]+', re.I)

    def scrub(x):
        if isinstance(x, str):
            for root in roots:
                x = x.replace(root, '<repo>')
            return home.sub('<home>', x)
        if isinstance(x, list):
            return [scrub(v) for v in x]
        if isinstance(x, dict):
            return {k: scrub(v) for k, v in x.items()}
        return x
    return scrub(r)


def main():
    args = [x for x in sys.argv[1:] if not x.startswith('--')]
    if len(args) != 1:
        print(__doc__)
        return 2
    pid = args[0]
    path = os.path.join(REPO, 'evidence', 'runtime', 'in-game', 'naval_missions-%s.json' % pid)
    with open(path, encoding='utf-8-sig') as f:
        result = json.load(f)
    with open(SCENARIOS, 'rb') as f:
        raw = f.read()
    plan = json.loads(raw.decode('utf-8'))
    scenarios, passed, total = recompute(result, plan)
    o = result.get('outputs') or {}
    out = {'schema': 'mnbl.evidence.naval-scenarios', 'version': 1, 'ticket': 'MMR-53', 'pid': pid, 'result': os.path.relpath(path, REPO).replace('\\', '/'),
           'status': result.get('status'), 'error': result.get('error'), 'scenarioFile': {'run': o.get('scenarioFile'), 'current': hashlib.sha256(raw).hexdigest()},
           'failures': o.get('failures'), 'passed': passed, 'total': total, 'scenarios': scenarios,
           'load': {k: (o.get('load') or {}).get(k) for k in ('save', 'loaded', 'seconds', 'worldId')}, 'voyage': o.get('voyage'), 'pirates': o.get('pirates'), 'traffic': o.get('traffic'),
           'menus': o.get('menus'), 'cases': {k: {kk: vv for kk, vv in v.items() if kk not in ('before', 'after', 'samples')} for k, v in (o.get('cases') or {}).items()},
           'captures': sorted(k for k in o if k.endswith('Screenshot'))}
    target = os.path.join(REPO, 'evidence', 'gameplay', 'naval-scenarios.json')
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(out, f, indent=2, ensure_ascii=False)
        f.write('\n')
    if '--fixture' in sys.argv:
        fixture = os.path.join(REPO, 'tests', 'Runtime', 'fixtures', 'naval-missions-%s.json' % pid)
        os.makedirs(os.path.dirname(fixture), exist_ok=True)
        with open(fixture, 'w', encoding='utf-8', newline='\n') as f:
            json.dump(trim(result), f, indent=1, ensure_ascii=False)
            f.write('\n')
        print('fixture: ' + os.path.relpath(fixture, REPO).replace('\\', '/'))
    print('naval_missions %s: %d/%d recomputed checks; %d/%d cases; status %s' % (pid, passed, total, sum(s['passed'] for s in scenarios), len(scenarios), out['status']))
    for s in scenarios:
        for c in s['checks']:
            if not c['passed']:
                print('   not passed: %s: %s' % (s['id'], c['check']))
    return 0 if passed == total and out['status'] == 'ok' else 1


if __name__ == '__main__':
    sys.exit(main())
