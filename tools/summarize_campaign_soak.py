"""MMR-51: summarizes the campaign_soak probe (evidence/runtime/in-game/campaign_soak-<pid>.json) into evidence/gameplay/campaign-soak.json.
Every scenario of tests/Runtime/CampaignSoakScenarios.json is checked from the archived outputs, not from the probe's pass lines, with the
rules, hours and motion fractions the run recorded (outputs.rules, outputs.worldPlans); the summary, not the probe, is the verdict.
Usage: python tools/summarize_campaign_soak.py <pid> [--fixture]   (--fixture also writes tests/Runtime/fixtures/campaign-soak-<pid>.json)
recompute(result, plan) and trim(result) are used by tests/Runtime/test_campaign_soak_regressions.py.
"""
import copy
import hashlib
import json
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCENARIOS = os.path.join(REPO, 'tests', 'Runtime', 'CampaignSoakScenarios.json')


def census(audit, key):
    return ((audit or {}).get('census') or {}).get(key, 0)


def counter(obs, key):
    return ((obs or {}).get('counters') or {}).get(key, 0)


def motion(obs, kind):
    m = (((obs or {}).get('motion') or {}).get(kind) or {})
    return m.get('moved', 0), m.get('tracked', 0)


def coherent(audit):
    a = audit or {}
    return [('no invalid reference (%d)' % a.get('invalidCount', -1), a.get('invalidCount') == 0),
            ('no party off navigation (%d)' % a.get('offNavigationCount', -1), a.get('offNavigationCount') == 0)]


def moved_fractions(obs, fractions):
    rows = []
    for kind, need in sorted((fractions or {}).items()):
        moved, tracked = motion(obs, kind)
        rows.append(('%s moved: %d of %d (at least %.0f%%)' % (kind, moved, tracked, need * 100), tracked > 0 and moved >= need * tracked))
    return rows


def time_ran(obs, rules):
    o = obs or {}
    hours = o.get('hours') or 0
    return [('campaign time advanced %.2f of %s hours (%d stalls, %.0f s)' % (o.get('hoursAdvanced') or 0, hours, len(o.get('stalls') or []), o.get('realSeconds') or 0),
             hours > 0 and (o.get('hoursAdvanced') or 0) >= hours - 0.01 and len(o.get('stalls') or []) < rules['maxStalls'] and o.get('timedOut') is False),
            ('no exception thrown from campaign code (%s of %s in all), no engine assert (%s), no listener error; both canaries seen (exception %s, assert %s)' % (
                o.get('campaignThrown'), o.get('thrownCount'), o.get('assertCount'), o.get('exceptionCanarySeen'), o.get('assertCanarySeen')),
             o.get('campaignThrown') == 0 and o.get('assertCount') == 0 and not o.get('listenerErrors') and o.get('exceptionCanarySeen') is True and o.get('assertCanarySeen') is True),
            ('no party position off navigation (%s), no party whose path to a settlement was not found (%s of %s unfound paths), none stuck for %s h (%s)' % (
                o.get('offNavigationCount'), o.get('unfoundToSettlementCount'), o.get('unfoundPathCount'), rules['stuckHours'], o.get('stuckCount')),
             o.get('offNavigationCount') == 0 and o.get('unfoundToSettlementCount') == 0 and o.get('stuckCount') == 0),
            ('no villager or fishing party holding for %s h without an installed reason (%s; %s parties holding in all: %s)' % (rules['stuckHours'], o.get('holdingVillagersWithoutReason'), o.get('holdingCount'), o.get('holdingByKind')),
             o.get('holdingVillagersWithoutReason') == 0)]


def playability(world):
    # A world whose starts were checked must be activatable; a fixture world replacing no start records its refused player-start route as
    # not applicable, and every other route must hold.
    p = (world or {}).get('playability') or {}
    routes = p.get('routes') or {}
    starts = p.get('startsChecked') is not False
    return ('its semantic routes hold in the live campaign: %s of %s routes passed, %s failures%s%s' % (
        routes.get('passed'), routes.get('total'), p.get('failureCount'),
        (', activatable %s' % p.get('activatable')) if starts else ', the player-start route not applicable (%d refused: the fixture replaces no creation culture\'s start)' % len(p.get('notApplicable') or []),
        (' (' + p['error'] + ')') if p.get('error') else ''),
        p.get('failureCount') == 0 and (routes.get('total') or 0) > 0 and (p.get('activatable') is True if starts else True))


def recompute(result, plan):
    """Every scenario's checks from a run's outputs; returns (scenarios, passed, total)."""
    o = result.get('outputs') or {}
    rules = o.get('rules') or plan['rules']
    plans = o.get('worldPlans') or plan['worlds']
    worlds = o.get('worlds') or {}
    a, b = worlds.get('A') or {}, worlds.get('B') or {}
    init_a, visit, day, diag, after = a.get('init') or {}, a.get('visit') or {}, a.get('day') or {}, a.get('diagnostics') or {}, a.get('after') or {}
    trade, rec, leave = visit.get('trade') or {}, visit.get('recruitment') or {}, visit.get('leave') or {}
    rows = {}

    fortified = census(init_a, 'settlement:town') + census(init_a, 'settlement:castle')
    rows['A.init'] = coherent(init_a) + [
        ('no settlement, clan or kingdom outside the world or its aliases (%d)' % init_a.get('unexpectedCount', -1), init_a.get('unexpectedCount') == 0),
        ('every town and castle garrisoned (%d of %d)' % (census(init_a, 'garrison'), fortified), fortified > 0 and census(init_a, 'garrison') == fortified),
        ('every town fed (%d without food), offering volunteers (%d without) and stocking trade goods (%d without)' % (census(init_a, 'town:noFood'), census(init_a, 'town:noVolunteers'), census(init_a, 'town:noTradeGoods')),
         census(init_a, 'town:noFood') == 0 and census(init_a, 'town:noVolunteers') == 0 and census(init_a, 'town:noTradeGoods') == 0),
        ('lords %d, lord parties %d, caravans %d, bandit parties %d, kingdoms %d' % (census(init_a, 'hero:lord'), census(init_a, 'party:lord'), census(init_a, 'party:caravan'), census(init_a, 'party:bandit'), census(init_a, 'kingdom')),
         census(init_a, 'hero:lord') > 0 and census(init_a, 'party:lord') > 0 and census(init_a, 'party:caravan') > 0 and census(init_a, 'party:bandit') > 0 and census(init_a, 'kingdom') >= 2),
        playability(a),
    ]
    town = visit.get('town')
    rows['A.visit'] = [
        ('the player\'s party travelled %.2f h by land from %s to %s (no encounter before the move) and the engine opened its settlement encounter (menu %s)' % (
            visit.get('travelHours') or 0, visit.get('startTown'), town, visit.get('menu')),
         town is not None and town != visit.get('startTown') and (visit.get('travelHours') or 0) > 0 and visit.get('encounterBefore') is False
         and visit.get('inSettlement') == town and visit.get('encounterSettlement') == town and visit.get('menu') == 'town'),
        ('the player left through the menu\'s leave path, back on the map', leave.get('inSettlement') is None and leave.get('encounter') is False and leave.get('menu') is None and 'error' not in leave),
    ]
    tb, tbuy, tsell = trade.get('before') or {}, trade.get('afterBuy') or {}, trade.get('afterSell') or {}
    buy, sell = trade.get('buy') or {}, trade.get('sell') or {}
    bp, sp = buy.get('price'), sell.get('price')
    rows['A.trade'] = [
        ('bought one %s for %s through the trade screen\'s logic (committed %s): town stock -1, party stock +1, player gold -price, town gold +price' % (trade.get('item'), bp, buy.get('done')),
         buy.get('done') is True and bool(tb) and bool(tbuy) and isinstance(bp, int) and bp > 0 and tbuy.get('townStock') == tb.get('townStock') - 1 and tbuy.get('partyStock') == tb.get('partyStock') + 1
         and tb.get('gold') - tbuy.get('gold') == bp and tbuy.get('townGold') - tb.get('townGold') == bp),
        ('sold it back for %s (committed %s): town stock +1, party stock -1, player gold +price, town gold -price' % (sp, sell.get('done')),
         sell.get('done') is True and bool(tsell) and isinstance(sp, int) and sp > 0 and tsell.get('townStock') == tbuy.get('townStock') + 1 and tsell.get('partyStock') == tbuy.get('partyStock') - 1
         and tsell.get('gold') - tbuy.get('gold') == sp and tbuy.get('townGold') - tsell.get('townGold') == sp),
    ]
    rows['A.recruit'] = [
        ('recruited one %s from %s at %s for %s: roster +1, gold -cost, slot emptied' % (rec.get('troop'), rec.get('notable'), rec.get('settlement'), rec.get('cost')),
         rec.get('settlement') == town and rec.get('countAfter') == (rec.get('countBefore') or 0) + 1 and rec.get('goldAfter') == (rec.get('goldBefore') or 0) - (rec.get('cost') or 0) and rec.get('slotCleared') is True),
    ]
    end_towns = ((day.get('after') or {}).get('towns') or [])
    starving = [t['id'] for t in end_towns if (t.get('food') or 0) <= 0]
    poor_all_day = sorted(set(init_a.get('townsWithoutGold') or []) & set(t['id'] for t in end_towns if (t.get('gold') or 0) <= 0))
    rows['A.day'] = time_ran(day, rules) + moved_fractions(day, plans['A'].get('movedFraction')) + [
        ('caravans traded in towns %d times (%d items); villagers entered towns %d times and sold their village\'s goods %d times (%d items; %d entries sold nothing to a town without gold)' % (
            counter(day, 'trade:caravan'), counter(day, 'tradeItems:caravan'), counter(day, 'entered:villager>town'), counter(day, 'villagerSold'), counter(day, 'villagerItemsSold'),
            counter(day, 'villagerSoldNothing:townWithoutGold')),
         counter(day, 'trade:caravan') > 0 and counter(day, 'entered:villager>town') > 0 and counter(day, 'villagerSold') > 0),
        ('lords recruited %d times (%d troops)' % (counter(day, 'recruited:lord'), counter(day, 'recruitedTroops:lord')), counter(day, 'recruited:lord') > 0),
        ('no town without food at the end of the day (%d: %s)' % (len(starving), ', '.join(starving[:5])), len(end_towns) > 0 and not starving),
        ('no town without gold through the whole day (%d at the start: %s; still none at the end: %s)' % (
            len(init_a.get('townsWithoutGold') or []), ', '.join((init_a.get('townsWithoutGold') or [])[:5]), ', '.join(poor_all_day) or 'none'), len(end_towns) > 0 and not poor_all_day),
    ]
    rep = a.get('replenishment') or {}
    rows['A.replenish'] = [
        ('volunteer slots empty at the start refilled through the daily update: %s of %s (operability, not a rate)' % (rep.get('refilled'), rep.get('emptyAtStart')), (rep.get('refilled') or 0) > 0),
    ]
    war, own, clan, ruler = diag.get('war') or {}, diag.get('ownership') or {}, diag.get('succession.clan') or {}, diag.get('succession.ruler') or {}
    rows['A.war'] = [('%s declared war on %s (%s fiefs): both stances at war, the event raised' % (war.get('declarer'), war.get('target'), war.get('targetFiefs')),
                      war.get('atWar') is True and war.get('event') is True and 'war:error' not in diag and (war.get('targetFiefs') or 0) >= 2)]
    villages_ok = all(v.split(':')[1] == own.get('to') for v in (own.get('villages') or []))
    rows['A.ownership'] = [
        ('%s passed from %s to %s: owner clan, faction of the declarer, %d villages, fiefs, garrison, notables, governor and the event follow' % (own.get('town'), own.get('from'), own.get('to'), len(own.get('villages') or [])),
         own.get('ownerClan') == own.get('to') and own.get('faction') == war.get('declarer') and villages_ok and own.get('inNewFiefs') is True and own.get('inOldFiefs') is False
         and own.get('garrisonFaction') in (None, war.get('declarer')) and own.get('notablesKept') is True and (own.get('governor') is None or own.get('governor').endswith(':' + str(own.get('to'))))
         and own.get('event') is True),
        ('the target kingdom keeps a fief (%s before) and stays at war with the declarer' % war.get('targetFiefs'), own.get('targetKingdomSurvives') is True and own.get('stillAtWar') is True),
    ]
    rows['A.succession.clan'] = [
        ('%s of %s died of old age: %s leads the clan, its %s fiefs kept, the event raised, no reference to the dead hero' % (clan.get('victim'), clan.get('clan'), clan.get('newLeader'), clan.get('fiefs')),
         clan.get('dead') is True and clan.get('newLeaderAlive') is True and clan.get('newLeader') not in (None, clan.get('victim')) and clan.get('eliminated') is False
         and clan.get('fiefsKept') is True and clan.get('event') is True and clan.get('deadReferences') == []),
    ]
    cands = ruler.get('candidates') or []
    applied = ruler.get('rulingClansApplied') or []
    if len(cands) >= 2:
        # Kingdom.AddDecision holds a non-player kingdom's election at once; only the player's kingdom keeps the decision pending. Without an
        # heir in the ruling clan, KillCharacterAction gives the rule to another eligible clan after the election.
        rule = (ruler.get('kingSelectionAdded') is True and ruler.get('kingSelectionPending') is ruler.get('playerKingdom') and bool(applied)
                and ruler.get('rulingClan') == applied[-1] and ruler.get('rulingClan') in cands
                and (ruler.get('rulingClanHadHeir') is True or ruler.get('rulingClan') != ruler.get('rulingClanBefore')))
    elif len(cands) == 1:
        rule = ruler.get('kingSelectionAdded') is False and ruler.get('rulingClan') == cands[0]
    else:
        rule = ruler.get('eliminated') is True
    ruled = len(cands) == 0 or (ruler.get('eliminated') is False and ruler.get('leaderAlive') is True and ruler.get('leader') != ruler.get('victim'))
    rows['A.succession.ruler'] = [
        ('%s, ruler of %s, died of old age; %d candidate clans (%s): king selection added %s, pending %s, ruling clans applied %s, ruling clan %s -> %s (heir in it: %s), led by %s; no reference to the dead ruler' % (
            ruler.get('victim'), ruler.get('kingdom'), len(cands), ruler.get('branch'), ruler.get('kingSelectionAdded'), ruler.get('kingSelectionPending'), applied, ruler.get('rulingClanBefore'), ruler.get('rulingClan'), ruler.get('rulingClanHadHeir'), ruler.get('leader')),
         ruler.get('dead') is True and rule and ruled and ruler.get('deadReferences') == []),
        ('the election branch was exercised (two or more candidate clans)', len(cands) >= 2),
    ]
    rows['A.after'] = time_ran(after, rules) + coherent(a.get('final'))
    init_b, ind, hours = b.get('init') or {}, b.get('independence') or {}, b.get('hours') or {}
    fortified_b = census(init_b, 'settlement:town') + census(init_b, 'settlement:castle')
    rows['B.init'] = coherent(init_b) + [
        ('no settlement, clan or kingdom outside world B or its aliases (%d)' % init_b.get('unexpectedCount', -1), init_b.get('unexpectedCount') == 0),
        ('nothing of world A registered (%s objects) or referenced (%s ids of A\'s world); B\'s own objects %s' % (ind.get('aObjects'), ind.get('foreignCount'), ind.get('bObjects')),
         ind.get('aObjects') == 0 and ind.get('foreignCount') == 0 and (ind.get('bObjects') or 0) > 0),
        ('B\'s campaign time starts fresh (day %.2f; A started at day %.2f and ended at day %.2f)' % (ind.get('campaignDays') or -1, ind.get('aStartDays') or -1, a.get('endDays') or -1),
         ind.get('campaignDays') is not None and ind.get('campaignDays') <= (ind.get('aStartDays') or 0) + 0.5 and ind.get('campaignDays') < (a.get('endDays') or 0)),
        ('B\'s towns fed (%s of %s), its markets stocked (%s items), its notables offering volunteers (%s slots), every town and castle garrisoned (%d of %d), lord parties %d' % (
            ind.get('townsFed'), ind.get('towns'), ind.get('marketItems'), ind.get('volunteerSlotsFilled'), census(init_b, 'garrison'), fortified_b, census(init_b, 'party:lord')),
         (ind.get('towns') or 0) > 0 and ind.get('townsFed') == ind.get('towns') and (ind.get('marketItems') or 0) > 0 and (ind.get('volunteerSlotsFilled') or 0) > 0
         and census(init_b, 'garrison') == fortified_b and census(init_b, 'party:lord') > 0 and census(init_b, 'town:noTradeGoods') == 0),
        playability(b),
    ]
    rows['B.hours'] = time_ran(hours, rules) + coherent(b.get('final')) + moved_fractions(hours, plans['B'].get('movedFraction'))
    rows['baselines'] = [('%s: no game, every tracked static at its baseline' % x.get('at'), x.get('game') is False and x.get('held') == {} and (x.get('tracked') or 0) >= 15)
                         for x in (o.get('baselines') or [])] or [('baselines recorded', False)]
    if len(o.get('baselines') or []) != 3:
        rows['baselines'].append(('three baselines (start, after A, after B): %d' % len(o.get('baselines') or []), False))

    scenarios = []
    for s in plan['scenarios']:
        checks = [{'check': n, 'passed': bool(ok)} for n, ok in rows.get(s['id'], [('no outputs for the scenario', False)])]
        scenarios.append({'id': s['id'], 'world': s['world'], 'source': s['source'], 'what': s['what'], 'passed': all(c['passed'] for c in checks), 'checks': checks})
    total = sum(len(s['checks']) for s in scenarios)
    passed = sum(c['passed'] for s in scenarios for c in s['checks'])
    return scenarios, passed, total


def trim(result):
    """The run's result without what no check reads (stages, timings, hourly samples, per-event samples), for a committed fixture; the
    repository root becomes <repo> and any other user home <home> in every string."""
    r = copy.deepcopy({k: v for k, v in result.items() if k not in ('stages', 'timings', 'requestFile', 'store', 'menuScreen')})
    for world in ((r.get('outputs') or {}).get('worlds') or {}).values():
        for obs in ('day', 'after', 'hours'):
            o = world.get(obs)
            if isinstance(o, dict):
                o.pop('hourly', None)
                o.pop('samples', None)
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
    path = os.path.join(REPO, 'evidence', 'runtime', 'in-game', 'campaign_soak-%s.json' % pid)
    with open(path, encoding='utf-8-sig') as f:
        result = json.load(f)
    with open(SCENARIOS, 'rb') as f:
        raw = f.read()
    plan = json.loads(raw.decode('utf-8'))
    scenarios, passed, total = recompute(result, plan)
    o = result.get('outputs') or {}
    a, b = (o.get('worlds') or {}).get('A') or {}, (o.get('worlds') or {}).get('B') or {}

    def brief(obs):
        x = obs or {}
        keep = ('hours', 'hoursAdvanced', 'realSeconds', 'setup', 'motion', 'counters', 'samples', 'stalls', 'stuck', 'holding', 'holdingByKind', 'holdingVillagersWithoutReason',
                'unfoundPaths', 'unfoundPathCount', 'unfoundToSettlementCount', 'offNavigation', 'thrownCount', 'campaignThrown', 'thrown', 'assertCount', 'asserts', 'exceptionCanarySeen', 'assertCanarySeen')
        out = {k: x.get(k) for k in keep}
        out['economyBefore'] = {k: v for k, v in (x.get('before') or {}).items() if k != 'towns'}
        out['economyAfter'] = {k: v for k, v in (x.get('after') or {}).items() if k != 'towns'}
        return out

    def audit(x):
        x = x or {}
        return {'census': x.get('census'), 'wars': x.get('wars'), 'invalid': x.get('invalid'), 'unexpected': x.get('unexpected'), 'villagesWithoutTradeTown': x.get('villagesWithoutTradeTown'),
                'townsWithoutGold': x.get('townsWithoutGold'), 'townsWithoutTradeGoods': x.get('townsWithoutTradeGoods'), 'economy': {k: v for k, v in (x.get('economy') or {}).items() if k != 'towns'}}

    out = {'schema': 'mnbl.evidence.campaign-soak', 'version': 1, 'ticket': 'MMR-51', 'pid': pid, 'result': os.path.relpath(path, REPO).replace('\\', '/'),
           'status': result.get('status'), 'error': result.get('error'),
           'scenarioFile': {'run': o.get('scenarioFile'), 'current': hashlib.sha256(raw).hexdigest(),
                            'note': 'checked with the rules, hours and motion fractions the run recorded; regressions appended after the run change only the file\'s hash'},
           'failures': o.get('failures'), 'passed': passed, 'total': total, 'scenarios': scenarios,
           'worlds': {
               'A': {'seed': a.get('seed'), 'worldId': a.get('worldId'), 'startSeconds': a.get('startSeconds'), 'init': audit(a.get('init')), 'playability': a.get('playability'),
                     'visit': a.get('visit'), 'day': brief(a.get('day')), 'replenishment': a.get('replenishment'), 'afterDay': audit(a.get('afterDay')), 'diagnostics': a.get('diagnostics'),
                     'after': brief(a.get('after')), 'followUp': a.get('followUp'), 'final': audit(a.get('final'))},
               'B': {'seed': b.get('seed'), 'worldId': b.get('worldId'), 'startSeconds': b.get('startSeconds'), 'fixture': b.get('fixture'), 'init': audit(b.get('init')), 'playability': b.get('playability'),
                     'independence': b.get('independence'), 'hours': brief(b.get('hours')), 'final': audit(b.get('final'))}},
           'regressions': plan.get('regressions')}
    target = os.path.join(REPO, 'evidence', 'gameplay', 'campaign-soak.json')
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(out, f, indent=2, ensure_ascii=False)
        f.write('\n')
    if '--fixture' in sys.argv:
        fixture = os.path.join(REPO, 'tests', 'Runtime', 'fixtures', 'campaign-soak-%s.json' % pid)
        os.makedirs(os.path.dirname(fixture), exist_ok=True)
        with open(fixture, 'w', encoding='utf-8', newline='\n') as f:
            json.dump(trim(result), f, indent=1, ensure_ascii=False)
            f.write('\n')
        print('fixture: ' + os.path.relpath(fixture, REPO).replace('\\', '/'))
    print('campaign_soak %s: %d/%d recomputed checks; %d/%d scenarios; status %s' % (pid, passed, total, sum(s['passed'] for s in scenarios), len(scenarios), out['status']))
    for s in scenarios:
        for c in s['checks']:
            if not c['passed']:
                print('   not passed: %s: %s' % (s['id'], c['check']))
    return 0 if passed == total and out['status'] == 'ok' else 1


if __name__ == '__main__':
    sys.exit(main())
