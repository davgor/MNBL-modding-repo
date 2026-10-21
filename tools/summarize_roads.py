"""MMR-253: the road network over offline generation sweeps (tools/Run-SeedSweep.ps1 -Stage generate) -> evidence/fwg/road-network.json.

Per accepted world (the chosen attempt's travel/ outputs), recounted independently of the compiler's roadNetwork summary:
- road cells: the cells of every road piece, against grid.roadCells and roadNetwork.cells;
- road cells on water without a deck (RT-17), from cells.bin's classes and the decks of the joining route crossings: must be 0;
- every piece edge-connected (columns wrap, rows bounded), every bank end on land beside a water cell that is not a deck, and every
  feeder step onto or off a deck a step of the route that laid it;
- connected settlements per kind: a kept settlement whose cell is a road cell whose road component (edge-connected road cells) holds a
  town (or, an islet, a town's cell in a land group without road cells whose kept settlements share that cell); connected villages and
  castles whose component lacks their bound settlement are counted apart (boundNotReached);
- route pieces, feeders, ends by kind, the longest feeder.
Usage: python tools/summarize_roads.py OUT.json SWEEP_DIR [SWEEP_DIR ...]
"""
import collections
import json
import os
import struct
import sys

LAND = 0


def load(path):
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f)


def world_row(seed, attempt, travel):
    g = load(os.path.join(travel, 'travel-graph.json'))
    c = load(os.path.join(travel, 'campaign.json'))
    with open(os.path.join(travel, 'cells.bin'), 'rb') as f:
        raw = f.read()
    cols, rows = struct.unpack('<II', raw[8:16])
    cls = raw[16:16 + cols * rows]
    idx = lambda cell: cell[0] * cols + cell[1]

    def nbrs(i):
        r, q = divmod(i, cols)
        out = [r * cols + (q + 1) % cols, r * cols + (q - 1) % cols]
        if r > 0: out.append(i - cols)
        if r + 1 < rows: out.append(i + cols)
        return out

    def adjacent(a, b):
        return idx(b) in nbrs(idx(a))
    routes = [x for x in g['roads'] if x['kind'] == 'route']
    feeders = [x for x in g['roads'] if x['kind'] == 'feeder']
    decks = {idx(cell) for x in routes for k in x['crossings'] if k['joins'] for cell in k['cells']}
    route_steps = set()
    road, ends, pieces_ok, banks_ok = set(), collections.Counter(), True, True
    for x in g['roads']:
        for p in x['pieces']:
            path = p['path']
            pieces_ok = pieces_ok and len(path) > 0 and all(adjacent(a, b) for a, b in zip(path, path[1:])) and p['start']['cell'] == path[0] and p['end']['cell'] == path[-1]
            road |= {idx(cell) for cell in path}
            for e in (p['start'], p['end']):
                ends[e['kind']] += 1
                if e['kind'] == 'bank':
                    # The water beyond a bank belongs to a crossing of this route that joins nothing (the cell may still carry a deck between
                    # other land components, laid by another crossing; a route can pass one river cell twice).
                    banks_ok = banks_ok and x['kind'] == 'route' and cls[idx(e['cell'])] == LAND and cls[idx(e['water'])] != LAND and adjacent(e['cell'], e['water']) and \
                        any(not k['joins'] and e['water'] in k['cells'] for k in x['crossings'])
            if x['kind'] == 'route':
                for a, b in zip(path, path[1:]):
                    route_steps |= {(idx(a), idx(b)), (idx(b), idx(a))}
    feeder_steps_ok = all((idx(a), idx(b)) in route_steps for x in feeders for a, b in zip(x['pieces'][0]['path'], x['pieces'][0]['path'][1:])
                          if cls[idx(a)] != LAND or cls[idx(b)] != LAND)
    water_without_deck = sum(1 for i in road if cls[i] != LAND and i not in decks)
    # Road components and who is connected.
    gone = {e['id'] for e in g['exclusions']}
    kept = {s['id']: s for s in g['settlements'] if 'cell' in s and s['id'] not in gone}
    town_cells = {idx(s['cell']) for s in kept.values() if s['kind'] == 'town'}
    label = {}
    holds_town = []
    for i in sorted(road):
        if i in label: continue
        n = len(holds_town); holds_town.append(False); label[i] = n
        queue = collections.deque([i])
        while queue:
            j = queue.popleft()
            holds_town[n] = holds_town[n] or j in town_cells
            for k in nbrs(j):
                if k in road and k not in label: label[k] = n; queue.append(k)
    bound = {v['id']: v['bound'] for v in c['villages']}
    bound.update({k['id']: k['garrisonTown'] for k in c['castles'] if k.get('garrisonTown')})
    # An islet: a land group without road cells whose kept settlements share one cell (a town's) has no network to join.
    land_of, n_lands = {}, 0   # land components as the compiler labels them: by first cell, edges only
    for i in range(cols * rows):
        if cls[i] != LAND or i in land_of: continue
        land_of[i] = n_lands; queue = collections.deque([i])
        while queue:
            j = queue.popleft()
            for k in nbrs(j):
                if cls[k] == LAND and k not in land_of: land_of[k] = n_lands; queue.append(k)
        n_lands += 1
    land_group = {l['id']: l['group'] for l in g['landComponents']}
    road_groups = {land_group[l] for l in (land_of.get(i) for i in road) if l is not None}
    group_cells = collections.defaultdict(set)
    for s in kept.values(): group_cells[s['group']].add(idx(s['cell']))
    islet = lambda s: idx(s['cell']) in town_cells and s['group'] not in road_groups and len(group_cells[s['group']]) == 1
    connected = collections.defaultdict(lambda: [0, 0])
    unconnected, apart = [], []
    for sid, s in sorted(kept.items()):
        tally = connected[s['kind']]
        tally[1] += 1
        lab = label.get(idx(s['cell']))
        if lab is None and islet(s):
            tally[0] += 1; continue
        if lab is None or not holds_town[lab]:
            unconnected.append(sid); continue
        tally[0] += 1
        b = bound.get(sid)
        if b in kept and label.get(idx(kept[b]['cell'])) != lab: apart.append(sid)
    net = g['roadNetwork']
    return collections.OrderedDict([
        ('seed', seed), ('attempt', attempt), ('routes', len(routes)), ('routePieces', sum(len(x['pieces']) for x in routes)), ('feeders', len(feeders)),
        ('roadCells', len(road)), ('recordedRoadCells', g['grid']['roadCells']), ('deckCells', len(road & decks)), ('waterCellsWithoutDeck', water_without_deck),
        ('ends', dict(sorted(ends.items()))), ('maxFeederCells', max((len(x['pieces'][0]['path']) for x in feeders), default=0)),
        ('connected', {k: {'connected': v[0], 'total': v[1]} for k, v in sorted(connected.items())}), ('unconnected', unconnected), ('boundNotReached', apart),
        ('piecesEdgeConnected', pieces_ok), ('banksBesideUndeckedWater', banks_ok), ('feedersCrossWaterOnlyAlongDecks', feeder_steps_ok),
        ('matchesCompiler', n_lands == len(g['landComponents']) and len(road) == g['grid']['roadCells'] == net['cells'] and water_without_deck == net['waterCellsWithoutDeck'] and len(road & decks) == net['deckCells'] and
         {k: [v['connected'], v['total']] for k, v in net['connected'].items()} == {k: v for k, v in connected.items()} and
         sorted(u['id'] for u in net['unconnected']) == unconnected and sorted(u['id'] for u in net['boundNotReached']) == sorted(apart))])


def main():
    out, sweeps = sys.argv[1], sys.argv[2:]
    rows = []
    for sweep in sweeps:
        for line in open(os.path.join(sweep, 'sweep.jsonl'), encoding='utf-8-sig'):
            r = json.loads(line)
            if r['status'] != 'accepted':
                rows.append(collections.OrderedDict([('seed', r['seed']), ('status', r['status'])]))
                continue
            a = r['attempts'][r['chosen']]
            rows.append(world_row(r['seed'], a['index'], os.path.join(a['dir'], 'travel')))
    rows.sort(key=lambda x: x['seed'])
    worlds = [x for x in rows if 'roadCells' in x]
    total = lambda key: sum(w[key] for w in worlds)
    kinds = collections.defaultdict(lambda: [0, 0])
    ends = collections.Counter()
    for w in worlds:
        ends.update(w['ends'])
        for k, v in w['connected'].items(): kinds[k][0] += v['connected']; kinds[k][1] += v['total']
    summary = collections.OrderedDict([
        ('worlds', len(worlds)), ('roadCells', total('roadCells')), ('feeders', total('feeders')), ('deckCells', total('deckCells')),
        ('waterCellsWithoutDeck', total('waterCellsWithoutDeck')), ('ends', dict(sorted(ends.items()))),
        ('connected', {k: {'connected': v[0], 'total': v[1]} for k, v in sorted(kinds.items())}), ('unconnected', sum(len(w['unconnected']) for w in worlds)),
        ('boundNotReached', sum(len(w['boundNotReached']) for w in worlds)), ('maxFeederCells', max((w['maxFeederCells'] for w in worlds), default=0)),
        ('allChecks', all(w['piecesEdgeConnected'] and w['banksBesideUndeckedWater'] and w['feedersCrossWaterOnlyAlongDecks'] and w['matchesCompiler'] for w in worlds))])
    doc = collections.OrderedDict([
        ('schema', 'mnbl.evidence.road-network'), ('version', 1), ('ticket', 'MMR-253'),
        ('recipe', 'tools/Run-SeedSweep.ps1 -Stage generate (size 129, phase 16, the module policy list, 3 attempts), then this script'),
        ('summary', summary), ('worlds', rows)])
    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        f.write(json.dumps(doc, indent=1) + '\n')
    print('roads: %d worlds, %d road cells, %d on water without a deck, connected %s, %d unconnected, %d bound not reached, checks %s' % (
        summary['worlds'], summary['roadCells'], summary['waterCellsWithoutDeck'], summary['connected'], summary['unconnected'], summary['boundNotReached'], summary['allChecks']))


if __name__ == '__main__':
    main()
