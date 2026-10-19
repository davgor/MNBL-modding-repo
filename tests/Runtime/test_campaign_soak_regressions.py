"""MMR-51: offline replay of the campaign soak's regression fixtures.
The committed fixture (tests/Runtime/CampaignSoakScenarios.json "fixture": a passing campaign_soak run's trimmed output) must pass every
scenario when recomputed by tools/summarize_campaign_soak.py; each regression guarded by a mutation reintroduces its failure into a copy of
the fixture, and the scenario it names must then fail.
Usage: python tests/Runtime/test_campaign_soak_regressions.py
"""
import copy
import json
import os
import sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.join(REPO, 'tools'))
from summarize_campaign_soak import recompute  # noqa: E402


def world(r, key):
    return r['outputs']['worlds'][key]


def town_short_paid(r):
    # Run 47176: the town received 2 gold less than the price.
    world(r, 'A')['visit']['trade']['afterBuy']['townGold'] -= 2


def no_travel(r):
    v = world(r, 'A')['visit']
    v['travelHours'] = 0
    v['town'] = v['startTown']


def villager_holds_without_reason(r):
    world(r, 'A')['day']['holdingVillagersWithoutReason'] = 1


def no_villager_sales(r):
    world(r, 'A')['day']['counters']['villagerSold'] = 0


def king_selection_left_pending(r):
    ruler = world(r, 'A')['diagnostics']['succession.ruler']
    ruler['kingSelectionPending'] = True
    ruler['playerKingdom'] = False


def target_dissolved(r):
    world(r, 'A')['diagnostics']['ownership']['targetKingdomSurvives'] = False


def trade_not_committed(r):
    world(r, 'A')['visit']['trade']['buy']['done'] = False


def no_recruit(r):
    world(r, 'A')['visit']['recruitment'] = {'notables': 5}


def fishing_party_holds_idle(r):
    world(r, 'A')['day']['holdingVillagersWithoutReason'] = 1


def route_fails_in_b(r):
    world(r, 'B')['playability']['failureCount'] = 1


def town_without_trade_goods_at_start(r):
    world(r, 'A')['init']['census']['town:noTradeGoods'] = 1


def town_poor_all_day(r):
    a = world(r, 'A')
    town = a['day']['after']['towns'][0]
    town['gold'] = 0
    a['init']['townsWithoutGold'] = [town['id']]


MUTATIONS = {
    'town-short-paid': town_short_paid,
    'no-travel': no_travel,
    'villager-holds-without-reason': villager_holds_without_reason,
    'no-villager-sales': no_villager_sales,
    'king-selection-left-pending': king_selection_left_pending,
    'target-dissolved': target_dissolved,
    'town-poor-all-day': town_poor_all_day,
    'trade-not-committed': trade_not_committed,
    'no-recruit': no_recruit,
    'fishing-party-holds-idle': fishing_party_holds_idle,
    'route-fails-in-b': route_fails_in_b,
    'town-without-trade-goods-at-start': town_without_trade_goods_at_start,
}


def main():
    with open(os.path.join(REPO, 'tests', 'Runtime', 'CampaignSoakScenarios.json'), encoding='utf-8') as f:
        plan = json.load(f)
    lines, failed = [], 0

    def report(ok, text):
        nonlocal failed
        failed += 0 if ok else 1
        lines.append(('PASS ' if ok else 'FAIL ') + text)

    if not plan.get('fixture'):
        report(False, 'the scenario file names a fixture')
    else:
        with open(os.path.join(REPO, plan['fixture']), encoding='utf-8') as f:
            fixture = json.load(f)
        scenarios, passed, total = recompute(fixture, plan)
        bad = ['%s: %s' % (s['id'], c['check']) for s in scenarios for c in s['checks'] if not c['passed']]
        report(passed == total, 'the fixture %s passes every scenario (%d/%d checks)%s' % (plan['fixture'], passed, total, ('; not passed: ' + '; '.join(bad[:5])) if bad else ''))
        for reg in plan.get('regressions') or []:
            guard = reg.get('guard') or {}
            if guard.get('kind') != 'mutation':
                continue
            mutate = MUTATIONS.get(guard.get('mutation'))
            if mutate is None:
                report(False, '%s: mutation %s is defined' % (reg['id'], guard.get('mutation')))
                continue
            copy_ = copy.deepcopy(fixture)
            mutate(copy_)
            mutated, _, _ = recompute(copy_, plan)
            verdict = {s['id']: s['passed'] for s in mutated}
            report(verdict.get(guard['fails']) is False, '%s: reintroduced (%s), %s fails' % (reg['id'], guard['mutation'], guard['fails']))
    print('campaign soak regressions: %d checks, %d failed' % (len(lines), failed))
    for line in lines:
        print('   ' + line)
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
