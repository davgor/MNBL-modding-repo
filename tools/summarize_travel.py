"""Independently verify MMR-28 travel graphs and write evidence/fwg/travel-graph.json.

For each verified world (accepted or rejected) this script reads the MMR-26 terrain artifact (masks.bin,
heightfield.bin), the MMR-27 campaign records (campaign.json, id-map.json), the source world.json and the playability
policy, and re-derives in Python:
  cell classes; repairs (footprints, road passes, access passes, snaps, harbour shifts); emitted points and their
  collisions; land components, water basins and OpenSea flags; road lattice paths, crossings, joins and producer
  crossing outcomes; land groups; ports; player reachability; exclusions; every reason with its code and UIDs, and so
  every bound; and the post-exclusion campaign records with their resolved MMR-28 slots.
It compares each with travel-graph.json, ports.json, cells.bin and the travel output's campaign.json. Checks that do
not share the compiler's rules: input hashes equal the MMR-26/27 artifacts; every cell whose class changed has a repair
record and every repair changed its cell; every gate is land; every port cell is navigable, shares an edge with its land
entrance in the gate's component and lies within the policy distance; no two towns share a port pair; every transition
pair shares an edge; ids close over the campaign records; the outputs validate against both schemas; the calibration's
pinned evidence hashes match the files. A repeated compile must be byte-identical.
Usage:
  python tools/summarize_travel.py --verify NAME=SPLIT=TERRAIN=CAMPAIGN=TRAVEL [...] --repeat A=B [--gate FILE] --out FILE
"""
import argparse
import collections
import datetime
import hashlib
import heapq
import json
import math
import os
import re
import struct

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LAND, MOUNTAIN, SEA, LAKE, RIVER = 0, 1, 2, 3, 4
NAMES = ['land', 'mountain', 'sea', 'lake', 'river']
RANK = [0, 1, 4, 3, 2]
PENALTY = [0, 10, 1000, 100, 100]


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def rel(path):
    path = os.path.abspath(path)
    return os.path.relpath(path, REPO).replace('\\', '/') if path.startswith(REPO) else path


def load(path):
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f)


# ---- a small JSON Schema (2020-12) validator for the keywords the two schemas use ----
def validate(schema, value, root, path='$', errors=None):
    errors = [] if errors is None else errors
    if '$ref' in schema:
        node = root
        for part in schema['$ref'].lstrip('#/').split('/'): node = node[part]
        return validate(node, value, root, path, errors)
    t = schema.get('type')
    kinds = {'object': dict, 'array': list, 'string': str, 'boolean': bool}
    if t == 'integer' and not (isinstance(value, int) and not isinstance(value, bool)): errors.append(path + ': not an integer'); return errors
    if t == 'number' and not (isinstance(value, (int, float)) and not isinstance(value, bool)): errors.append(path + ': not a number'); return errors
    if t in kinds and not isinstance(value, kinds[t]): errors.append(path + ': not ' + t); return errors
    if 'const' in schema and value != schema['const']: errors.append(path + ': const')
    if 'enum' in schema and value not in schema['enum']: errors.append(path + ': enum ' + repr(value))
    if 'pattern' in schema and isinstance(value, str) and not re.search(schema['pattern'], value): errors.append(path + ': pattern')
    if 'minimum' in schema and value < schema['minimum']: errors.append(path + ': minimum')
    if 'exclusiveMinimum' in schema and value <= schema['exclusiveMinimum']: errors.append(path + ': exclusiveMinimum')
    if isinstance(value, dict):
        for k in schema.get('required', []):
            if k not in value: errors.append(path + ': missing ' + k)
        props = schema.get('properties', {})
        for k, v in value.items():
            if k in props: validate(props[k], v, root, path + '.' + k, errors)
            elif schema.get('additionalProperties') is False: errors.append(path + ': unknown ' + k)
            elif isinstance(schema.get('additionalProperties'), dict): validate(schema['additionalProperties'], v, root, path + '.' + k, errors)
    if isinstance(value, list):
        if 'minItems' in schema and len(value) < schema['minItems']: errors.append(path + ': minItems')
        if 'maxItems' in schema and len(value) > schema['maxItems']: errors.append(path + ': maxItems')
        prefix = schema.get('prefixItems', [])
        for i, v in enumerate(value):
            if i < len(prefix): validate(prefix[i], v, root, '%s[%d]' % (path, i), errors)
            elif 'items' in schema: validate(schema['items'], v, root, '%s[%d]' % (path, i), errors)
    for sub in schema.get('allOf', []):
        if 'if' in sub:
            if not validate(sub['if'], value, root, path, []): validate(sub.get('then', {}), value, root, path, errors)
        else: validate(sub, value, root, path, errors)
    return errors


class Grid:
    def __init__(self, cols, rows, sp):
        self.cols, self.rows, self.sp = cols, rows, sp

    def idx(self, r, c):
        return r * self.cols + c % self.cols

    def nbrs(self, r, c):
        out = [(r, (c + 1) % self.cols), (r, (c - 1) % self.cols)]
        if r > 0: out.append((r - 1, c))
        if r + 1 < self.rows: out.append((r + 1, c))
        return out

    def dist_cells(self, a, b):
        dx = (a[1] - b[1]) % self.cols
        if dx > self.cols // 2: dx -= self.cols
        return math.hypot(dx, a[0] - b[0])

    def dist_point(self, p, c):
        dx = (c[1] + 0.5) - p[0] / self.sp
        dx -= self.cols * round(dx / self.cols)
        return math.hypot(dx, (c[0] + 0.5) - p[1] / self.sp)

    def touching(self, p):
        fx, fy = p[0] / self.sp, p[1] / self.sp
        on = lambda v: abs(v - round(v)) <= 1e-9 * max(1.0, abs(v))
        cs = [round(fx) - 1, round(fx)] if on(fx) else [math.floor(fx)]
        if on(fy):
            k = round(fy)
            rs = [0] if k <= 0 else [self.rows - 1] if k >= self.rows else [k - 1, k]
        else:
            rs = [min(max(math.floor(fy), 0), self.rows - 1)]
        out = []
        for r in rs:
            for c in cs:
                cell = (r, c % self.cols)
                if cell not in out: out.append(cell)
        return out


def label(grid, cls, member):
    lab = [-1] * len(cls)
    sizes = []
    for i in range(len(cls)):
        if lab[i] >= 0 or not member(cls[i]): continue
        n = len(sizes); sizes.append(0); lab[i] = n
        queue = collections.deque([i])
        while queue:
            j = queue.popleft(); sizes[n] += 1
            for (r, c) in grid.nbrs(j // grid.cols, j % grid.cols):
                k = grid.idx(r, c)
                if lab[k] < 0 and member(cls[k]): lab[k] = n; queue.append(k)
    return lab, sizes


def verify(name, split, terrain_dir, campaign_dir, travel_dir, policy, schemas):
    world = load(os.path.join(split, 'world.json'))
    graph = load(os.path.join(travel_dir, 'travel-graph.json'))
    ports_doc = load(os.path.join(travel_dir, 'ports.json'))
    out_campaign = load(os.path.join(travel_dir, 'campaign.json'))
    campaign = load(os.path.join(campaign_dir, 'campaign.json'))
    with open(os.path.join(travel_dir, 'cells.bin'), 'rb') as f: cells_bin = f.read()
    with open(os.path.join(terrain_dir, 'heightfield.bin'), 'rb') as f: hf = f.read()
    with open(os.path.join(terrain_dir, 'masks.bin'), 'rb') as f: mk = f.read()
    side = struct.unpack_from('<I', hf, 8)[0]
    z = struct.unpack_from('<%dd' % (side * side), hf, 12)
    intent = mk[12 + side * side:12 + 2 * side * side]
    g = graph['grid']
    grid = Grid(g['cols'], g['rows'], g['spacing'])
    c0, r0 = g['columnOffset'], g['rowOffset']
    inputs = graph['inputs']
    R, B, P = policy['repairs'], policy['bridges'], policy['ports']
    checks = {
        'inputsAreTheMmr26And27Artifacts': inputs['terrainMasksSha256'] == sha(os.path.join(terrain_dir, 'masks.bin')) and
        inputs['terrainHeightfieldSha256'] == sha(os.path.join(terrain_dir, 'heightfield.bin')) and
        inputs['idMapSha256'] == sha(os.path.join(campaign_dir, 'id-map.json')) and inputs['worldSha256'] == sha(os.path.join(split, 'world.json')) and
        inputs['projectionSha256'] == load(os.path.join(terrain_dir, 'terrain.json'))['projectionSha256'],
        'cellsBinHashRecorded': g['cellsSha256'] == hashlib.sha256(cells_bin).hexdigest() and ports_doc['inputs'] == inputs,
        'schemasValidate': not validate(schemas['graph'], graph, schemas['graph']) and not validate(schemas['ports'], ports_doc, schemas['ports']),
    }
    cols, rows, sp = grid.cols, grid.rows, grid.sp
    zs = lambda r, c: z[(r0 + r) * side + c0 + c % cols]
    it = lambda r, c: intent[(r0 + r) * side + c0 + c % cols]
    compiled = []
    for r in range(rows):
        for c in range(cols):
            a, b, d, e = zs(r, c), zs(r, c + 1), zs(r + 1, c), zs(r + 1, c + 1)
            s = (max(a, b, d, e) - min(a, b, d, e)) / sp
            if (a + e) / 2 < 0: k = SEA
            elif 2 in (it(r, c), it(r + 1, c + 1)): k = LAKE
            elif 3 in (it(r, c), it(r + 1, c + 1)): k = RIVER
            elif s > policy['cells']['mountainSlope']: k = MOUNTAIN
            else: k = LAND
            compiled.append(k)
    n_cells = cols * rows
    final_bin = list(cells_bin[16:16 + n_cells]); open_bin = list(cells_bin[16 + n_cells:16 + 2 * n_cells])
    checks['compiledClassesRecomputed'] = dict(collections.Counter(NAMES[k] for k in compiled)) == g['compiledClasses']
    # Settlements in the compiler's input order: towns, villages, castles.
    n = world['config']['size']
    W, H = cols * sp, rows * sp
    sample = lambda col, row: (W * col / (n - 1), H * row / (n - 1))
    sites = {s['uid']: s for s in world['settlements']['sites']}
    hamlets = {h['uid']: h for h in world['humans']['hamlets']}
    forts = {f['uid']: f for f in world['humans']['fortresses']}
    settlements = [(t['id'], t['uid'], 'town', '', sample(sites[t['uid']]['x'], sites[t['uid']]['z'])) for t in campaign['towns']]
    settlements += [(v['id'], v['uid'], 'village', v['bound'], sample(hamlets[v['uid']]['x'], hamlets[v['uid']]['z'])) for v in campaign['villages']]
    settlements += [(k['id'], k['uid'], 'castle', '', sample(forts[k['uid']]['x'], forts[k['uid']]['z'])) for k in campaign['castles']]
    uid_of = {sid: uid for sid, uid, _, _, _ in settlements}
    cls = list(compiled)
    place, footprint_cells, footprint_uids = {}, set(), []
    for sid, uid, kind, bound, p in settlements:
        t = grid.touching(p)
        land = [c for c in t if cls[grid.idx(*c)] == LAND]
        if land: place[sid] = (land[0], 'direct'); continue
        mount = [c for c in t if cls[grid.idx(*c)] == MOUNTAIN] if R['settlementFootprint'] else []
        if mount:
            cls[grid.idx(*mount[0])] = LAND; place[sid] = (mount[0], 'footprint'); footprint_cells.add(grid.idx(*mount[0])); footprint_uids.append(uid); continue
        place[sid] = (None, None)
    # Roads.
    city_by_site = {s['id']: s for s in world['settlements']['sites']}
    town_id = {t['uid']: t['id'] for t in campaign['towns']}
    def node_rc(node):
        if node == 0 or node == 1 + (n - 2) * (n - 1): return None
        return 1 + (node - 1) // (n - 1), (node - 1) % (n - 1)
    def lattice(a, b):
        if a == b: return [a]
        dc = (b[1] - a[1]) % cols
        if dc > cols // 2: dc -= cols
        clo, w = min(0, dc), abs(dc) + 1
        rlo, h = min(a[0], b[0]), abs(b[0] - a[0]) + 1
        N = w * h
        local = lambda r, d: (r - rlo) * w + (d - clo)
        cell_of = lambda i: (rlo + i // w, (a[1] + clo + i % w) % cols)
        cost, prev = [None] * N, [N] * N
        start, goal = local(a[0], 0), local(b[0], dc)
        cost[start] = 0; heap = [(0, start)]
        while heap:
            d, i = heapq.heappop(heap)
            if d != cost[i]: continue
            if i == goal: break
            r, dcol = rlo + i // w, clo + i % w
            for sr, sc in ((0, 1), (0, -1), (-1, 0), (1, 0)):
                rr, cc = r + sr, dcol + sc
                if rr < rlo or rr >= rlo + h or cc < clo or cc >= clo + w: continue
                j = local(rr, cc)
                nd = d + 1 + PENALTY[compiled[grid.idx(*cell_of(j))]]
                if cost[j] is None or nd < cost[j] or (nd == cost[j] and i < prev[j]):
                    if cost[j] is None or nd < cost[j]: heapq.heappush(heap, (nd, j))
                    cost[j] = nd; prev[j] = i
        path, i = [], goal
        while i != N:
            path.append(cell_of(i))
            if i == start: break
            i = prev[i]
        return path[::-1]
    roads, carved, pass_uids = [], [], []
    for rt in world['roads']['routes']:
        pieces = [[]]
        for node in rt['nodes']:
            if node_rc(node) is None:
                if pieces[-1]: pieces.append([])
                continue
            pieces[-1].append(node)
        if not pieces[-1]: pieces.pop()
        crossing_edges = {tuple(e) for e in rt['river_crossings']} | {tuple(e[::-1]) for e in rt['river_crossings']}
        rpieces, steps, passes = [], {}, []
        fu, tu = city_by_site[rt['from']]['uid'], city_by_site[rt['to']]['uid']
        for pi, nodes in enumerate(pieces):
            ncells = []
            for node in nodes:
                row, col = node_rc(node)
                ncells.append(sorted(grid.touching(sample(col, row)), key=lambda c: RANK[compiled[grid.idx(*c)]])[0])
            piece = []
            for k in range(len(ncells)):
                seg = lattice(ncells[k], ncells[k + 1]) if k + 1 < len(ncells) else [ncells[k]]
                if k + 1 < len(nodes) and (nodes[k], nodes[k + 1]) in crossing_edges: steps[(pi, k)] = [grid.idx(*c) for c in seg]
                for c in seg:
                    if not piece or piece[-1] != c: piece.append(c)
            for c in piece:
                i = grid.idx(*c)
                if compiled[i] == MOUNTAIN and R['roadPasses'] and i not in footprint_cells:
                    passes.append(c)
                    if i not in carved: carved.append(i); cls[i] = LAND; pass_uids += [fu, tu]
            rpieces.append(piece)
        pole = sum(1 for a, b in rt['river_crossings'] if node_rc(a) is None or node_rc(b) is None)
        roads.append({'pieces': rpieces, 'steps': steps, 'passes': passes, 'from': fu, 'to': tu, 'pole': pole})
    for sid in [s for s, (c, m) in place.items() if m == 'footprint']:
        f = place[sid][0]
        depth = {f: 0}; prev = {}; queue = collections.deque([f]); target = None
        while queue and target is None:
            j = queue.popleft()
            if depth[j] > R['maxAccessCells']: break
            for nb in sorted(grid.nbrs(*j)):
                if nb in depth: continue
                k = cls[grid.idx(*nb)]
                if k == LAND: target = nb; prev[nb] = j; break
                if k == MOUNTAIN: depth[nb] = depth[j] + 1; prev[nb] = j; queue.append(nb)
        if target is None: continue
        k = prev[target]
        while k != f:
            if grid.idx(*k) not in carved: carved.append(grid.idx(*k)); cls[grid.idx(*k)] = LAND; pass_uids.append(uid_of[sid])
            k = prev[k]
    snap_uids = []
    for sid, uid, kind, bound, p in settlements:
        if place[sid][0] is not None: continue
        base = grid.touching(p)[0]; reach = R['maxSnapCells'] + 1; best = None
        for r in range(max(0, base[0] - reach), min(rows - 1, base[0] + reach) + 1):
            for dc in range(-reach, reach + 1):
                c = (r, (base[1] + dc) % cols)
                if cls[grid.idx(*c)] != LAND: continue
                d = grid.dist_point(p, c)
                if d > R['maxSnapCells']: continue
                if best is None or d < best[0] or (d == best[0] and c < best[1]): best = (d, c)
        if best: place[sid] = (best[1], 'snap'); snap_uids.append(uid)
        else: place[sid] = (None, 'unplaceable')
    checks['finalClassesRecomputed'] = cls == final_bin and dict(collections.Counter(NAMES[k] for k in cls)) == g['classes']
    # Emitted points and collisions.
    points = {}
    for sid, uid, kind, bound, p in settlements:
        c, m = place[sid]
        if c is None: continue
        x0, y0, inset = c[1] * sp, c[0] * sp, 0.25 * sp
        x = p[0] - W * round((p[0] - (x0 + 0.5 * sp)) / W)
        points[sid] = (min(max(x, x0 + inset), x0 + sp - inset), min(max(p[1], y0 + inset), y0 + sp - inset))
    by_point = collections.defaultdict(list)
    for sid, uid, kind, bound, p in settlements:
        if sid in points: by_point[points[sid]].append(sid)
    collisions = sorted(ids for ids in by_point.values() if len(ids) > 1)
    by_id = {s['id']: s for s in graph['settlements']}
    checks['placementsAndPointsRecomputed'] = all(by_id[sid]['method'] == m and (c is None or (by_id[sid]['cell'] == list(c) and (by_id[sid]['point']['x'], by_id[sid]['point']['y']) == points[sid]))
                                                  for sid, (c, m) in place.items()) and sorted(graph['positionCollisions']) == collisions
    # Repairs: every changed cell has a record, every carving record changed its cell.
    changed = {i for i in range(n_cells) if cls[i] != compiled[i]}
    carve_records = {grid.idx(*r['cell']) for r in graph['repairs'] if r['kind'] in ('pass', 'access', 'footprint')}
    checks['repairRecordsComplete'] = changed == carve_records and len([r for r in graph['repairs'] if r['kind'] in ('pass', 'access')]) == len(carved) and \
        len([r for r in graph['repairs'] if r['kind'] == 'snap']) == len(snap_uids) and len([r for r in graph['repairs'] if r['kind'] == 'footprint']) == len(footprint_uids)
    # Components, basins, OpenSea.
    land_of, land_sizes = label(grid, cls, lambda k: k == LAND)
    basin_of, basin_sizes = label(grid, cls, lambda k: k in (SEA, LAKE, RIVER))
    dist = [-1] * n_cells; queue = collections.deque()
    for i in range(n_cells):
        if cls[i] != SEA: dist[i] = 0; queue.append(i)
    while queue:
        j = queue.popleft(); r, c = divmod(j, cols)
        for dr in (-1, 0, 1):
            for dc in (-1, 0, 1):
                if (dr or dc) and 0 <= r + dr < rows:
                    k = grid.idx(r + dr, c + dc)
                    if dist[k] < 0: dist[k] = dist[j] + 1; queue.append(k)
    open_sea = [1 if cls[i] == SEA and (dist[i] < 0 or dist[i] >= policy['cells']['openSeaCells']) else 0 for i in range(n_cells)]
    basins = []
    for b, size in enumerate(basin_sizes):
        members = [i for i in range(n_cells) if basin_of[i] == b]
        cnt = collections.Counter(cls[i] for i in members)
        basins.append({'id': b, 'cells': size, 'sea': cnt[SEA], 'lake': cnt[LAKE], 'river': cnt[RIVER], 'openSea': sum(open_sea[i] for i in members),
                       'open': cnt[SEA] > 0 and size >= policy['basins']['minOpenCells']})
    checks['openSeaRecomputed'] = open_sea == open_bin and sum(open_sea) == g['openSeaCells']
    checks['landComponentsRecomputed'] = land_sizes == [l['cells'] for l in graph['landComponents']]
    checks['basinsRecomputed'] = [{k: b[k] for k in ('id', 'cells', 'sea', 'lake', 'river', 'openSea', 'open')} for b in graph['basins']] == basins
    # Crossings, joins, producer outcomes, groups.
    parent = list(range(len(land_sizes)))
    def find(a):
        while parent[a] != a: parent[a] = parent[parent[a]]; a = parent[a]
        return a
    pairs, bridge_uids, road_ok = set(), [], True
    for rr, out in zip(roads, graph['roads']):
        xs = []
        for piece in rr['pieces']:
            last, run = None, []
            def emit(after):
                kinds = {cls[grid.idx(*c)] for c in run}
                kind = 'blocked' if MOUNTAIN in kinds else 'coast' if SEA in kinds else 'lake' if LAKE in kinds else 'river'
                la = land_of[grid.idx(*last)] if last else -1
                lb = land_of[grid.idx(*after)] if after else -1
                joins = la >= 0 and lb >= 0 and la != lb and kind in B['kinds'] and len(run) <= B['maxCells']
                if joins:
                    a_, b_ = find(la), find(lb)
                    if a_ != b_: parent[max(a_, b_)] = min(a_, b_)
                    key = (min(la, lb), max(la, lb))
                    if key not in pairs: pairs.add(key); bridge_uids.extend([rr['from'], rr['to']])
                xs.append({'kind': kind, 'cells': [list(c) for c in run], 'producerReported': any(grid.idx(*c) in {i for s in rr['steps'].values() for i in s} for c in run), 'lands': [la, lb], 'joins': joins})
            for c in piece:
                if cls[grid.idx(*c)] == LAND:
                    if run: emit(c); run = []
                    last = c
                else: run.append(c)
            if run: emit(None); run = []
        crossing_cells = {grid.idx(*c) for x in xs for c in x['cells']}
        producer = [{'piece': k[0], 'step': k[1], 'outcome': 'matched' if any(i in crossing_cells for i in v) else 'no_compiled_water'} for k, v in sorted(rr['steps'].items())]
        road_ok = road_ok and xs == out['crossings'] and [list(c) for c in rr['passes']] == out['passes'] and sum(len(p) for p in rr['pieces']) == out['cells'] and \
            out['producerCrossings'] == producer and out['producerPoleCrossings'] == rr['pole']
        for piece in rr['pieces']:
            road_ok = road_ok and all(grid.dist_cells(a, b) == 1 for a, b in zip(piece, piece[1:]))
    checks['roadsCrossingsAndProducerOutcomesRecomputed'] = road_ok and len(pairs) == graph['requiredBridges']
    group_of_root, land_group = {}, []
    for l in range(len(land_sizes)):
        group_of_root.setdefault(find(l), len(group_of_root)); land_group.append(group_of_root[find(l)])
    n_groups = len(group_of_root)
    checks['groupsRecomputed'] = [gr['lands'] for gr in graph['groups']] == [[l for l in range(len(land_sizes)) if land_group[l] == i] for i in range(n_groups)]
    group_at = lambda c: land_group[land_of[grid.idx(*c)]]
    exclusions = {}
    for sid, uid, kind, bound, p in settlements:
        if kind == 'town': continue
        c, m = place[sid]
        if m == 'unplaceable': exclusions[sid] = kind; continue
        if kind == 'village' and place[bound][0] is not None and group_at(c) != group_at(place[bound][0]): exclusions[sid] = kind
    # Ports: the gate's component, nearest the gate, held pairs skipped, within maxPortGateCells.
    village_by_id = {v['id']: v for v in campaign['villages']}
    harb = collections.defaultdict(list)
    for p in campaign['ports']:
        for vid in p['villages']:
            v = village_by_id[vid]; h = hamlets[v['uid']]
            row, col = node_rc(h['sea_node'])
            harb[p['town']].append((v['uid'], vid, sample(col, row), h['harbor_quality']))
    navigable = lambda k: k in (SEA, LAKE, RIVER)
    held, ports, shift_uids = set(), {}, []
    for tid in sorted(harb):
        gate = place[tid][0]; gl = land_of[grid.idx(*gate)] if gate else -1
        cands = []
        for vuid, vid, pt, q in sorted(harb[tid]):
            cand = {'village': vid, 'status': 'invalid', 'quality': q, 'uid': vuid}
            if vid in exclusions: cand['reason'] = 'harbour_village_excluded'; cands.append(cand); continue
            if gate is None: cand['reason'] = 'town_unplaceable'; cands.append(cand); continue
            touch = grid.touching(pt)
            hc = next((c for c in touch if navigable(cls[grid.idx(*c)])), None)
            if hc is None:
                best = None; reach = R['maxHarbourShiftCells'] + 1
                for r in range(max(0, touch[0][0] - reach), min(rows - 1, touch[0][0] + reach) + 1):
                    for dc in range(-reach, reach + 1):
                        c = (r, (touch[0][1] + dc) % cols)
                        if not navigable(cls[grid.idx(*c)]): continue
                        d = grid.dist_point(pt, c)
                        if d > R['maxHarbourShiftCells']: continue
                        if best is None or d < best[0] or (d == best[0] and c < best[1]): best = (d, c)
                if best: hc = best[1]; shift_uids.append(vuid)
            if hc is None: cand['reason'] = 'sea_node_not_navigable'; cands.append(cand); continue
            b = basin_of[grid.idx(*hc)]
            if not basins[b]['open']: cand['reason'] = 'closed_basin'; cands.append(cand); continue
            best, any_pair = None, False
            for i in range(n_cells):
                if cls[i] != LAND or land_of[i] != gl: continue
                l = divmod(i, cols)
                for s in grid.nbrs(*l):
                    if basin_of[grid.idx(*s)] != b: continue
                    any_pair = True
                    if ('L', i) in held or ('S', grid.idx(*s)) in held: continue
                    key = (grid.dist_cells(s, gate), grid.dist_cells(l, gate), grid.dist_cells(s, hc), l, s)
                    if best is None or key < best: best = key
            if best is None: cand['reason'] = 'port_pair_taken' if any_pair else 'basin_not_reachable_from_gate'; cands.append(cand); continue
            if best[0] > P['maxPortGateCells']: cand['reason'] = 'port_too_far_from_gate'; cands.append(cand); continue
            cand.update(status='valid', sea=best[4], land=best[3], gate=best[0], basin=b); cands.append(cand)
        valid = [c for c in cands if c['status'] == 'valid']
        chosen = min(valid, key=lambda c: (c['gate'], -c['quality'], c['uid'])) if valid else None
        if chosen: held.add(('L', grid.idx(*chosen['land']))); held.add(('S', grid.idx(*chosen['sea'])))
        ports[tid] = (chosen, cands)
    doc_ports = {p['town']: p for p in ports_doc['ports']}
    port_ok = set(doc_ports) == set(ports)
    for tid, (chosen, cands) in ports.items():
        d = doc_ports.get(tid, {})
        port_ok = port_ok and d.get('valid') == (chosen is not None) and [c['status'] for c in d.get('candidates', [])] == [c['status'] for c in cands]
        port_ok = port_ok and all(dc.get('reason', '').split(' (')[0] == c.get('reason', '') for dc, c in zip(d.get('candidates', []), cands))
        if chosen: port_ok = port_ok and d['chosen']['village'] == chosen['village'] and d['chosen']['seaCell'] == list(chosen['sea']) and d['chosen']['landCell'] == list(chosen['land'])
    checks['portsRecomputed'] = port_ok and len([r for r in graph['repairs'] if r['kind'] == 'harbour_shift']) == len(shift_uids)
    # Reachability, castle exclusions.
    port_basins, coast_basins = collections.defaultdict(set), collections.defaultdict(set)
    for tid, (chosen, _) in ports.items():
        if chosen: port_basins[group_at(place[tid][0])].add(chosen['basin'])
    for i in range(n_cells):
        if cls[i] != LAND: continue
        for s in grid.nbrs(*divmod(i, cols)):
            b = basin_of[grid.idx(*s)]
            if b >= 0 and basins[b]['open']: coast_basins[land_group[land_of[i]]].add(b)
    towns_in, min_uid = collections.Counter(), {}
    for t in campaign['towns']:
        c = place[t['id']][0]
        if c is None: continue
        gi = group_at(c); towns_in[gi] += 1; min_uid[gi] = min(min_uid.get(gi, t['uid']), t['uid'])
    main = min(towns_in, key=lambda gi: (-towns_in[gi], min_uid[gi])) if towns_in else -1
    fwd, back, changed_ = {main}, {main}, True
    while changed_:
        changed_ = False
        for gi in list(fwd):
            for b in port_basins[gi]:
                for gj in range(n_groups):
                    if gj not in fwd and b in coast_basins[gj]: fwd.add(gj); changed_ = True
        for gi in range(n_groups):
            if gi not in back and any(b in coast_basins[gj] for b in port_basins[gi] for gj in back): back.add(gi); changed_ = True
    safe = fwd & back
    checks['reachabilityRecomputed'] = graph['mainGroup'] == main and all(gr['playerSafe'] == (gr['id'] in safe) and gr['reachableFromMain'] == (gr['id'] in fwd) and gr['returnsToMain'] == (gr['id'] in back) for gr in graph['groups'])
    for sid, uid, kind, bound, p in settlements:
        if kind == 'castle' and sid not in exclusions and group_at(place[sid][0]) not in safe: exclusions[sid] = kind
    checks['exclusionsRecomputed'] = set(exclusions) == {e['id'] for e in graph['exclusions']}
    # Every reason recomputed with its code and UIDs (so every bound).
    reasons = []
    for t in campaign['towns']:
        c = place[t['id']][0]
        if c is None: reasons.append(('quality.unplaceable_town', (t['uid'],)))
        elif group_at(c) not in safe: reasons.append(('quality.unreachable_town', (t['uid'],)))
    mountains = compiled.count(MOUNTAIN); count = len(settlements)
    if len(carved) > R['maxPassFraction'] * mountains: reasons.append(('quality.repair_bound_exceeded', tuple(sorted(set(pass_uids)))))
    if len(footprint_uids) > R['maxFootprintFraction'] * count: reasons.append(('quality.repair_bound_exceeded', tuple(sorted(set(footprint_uids)))))
    if len(snap_uids) > R['maxSnapFraction'] * count: reasons.append(('quality.repair_bound_exceeded', tuple(sorted(set(snap_uids)))))
    if len(pairs) > B['maxRequired']: reasons.append(('quality.repair_bound_exceeded', tuple(sorted(set(bridge_uids)))))
    if len(shift_uids) > R['maxHarbourShiftFraction'] * sum(len(v) for v in harb.values()): reasons.append(('quality.repair_bound_exceeded', tuple(sorted(set(shift_uids)))))
    for kind, frac in (('village', policy['reachability']['maxExcludedVillageFraction']), ('castle', policy['reachability']['maxExcludedCastleFraction'])):
        ex = [uid_of[s] for s, k in exclusions.items() if k == kind]
        total = sum(1 for s in settlements if s[2] == kind)
        if len(ex) > frac * total: reasons.append(('quality.too_many_exclusions', tuple(sorted(ex))))
    got = sorted((r['code'], tuple(sorted(r['uids']))) for r in graph['reasons'])
    checks['reasonsAndBoundsRecomputed'] = got == sorted(reasons) and (graph['status'] == 'ok') == (not reasons)
    # Policy-independent checks.
    ids = {t['id'] for t in campaign['towns']} | {v['id'] for v in campaign['villages']} | {k['id'] for k in campaign['castles']}
    adjacent = lambda a, b: grid.dist_cells(tuple(a), tuple(b)) == 1
    valid_ports = [p for p in ports_doc['ports'] if p['valid']]
    checks['gatesOnLand'] = all(cls[grid.idx(*s['cell'])] == LAND for s in graph['settlements'] if s['kind'] == 'town' and 'cell' in s)
    checks['portsServeTheirGates'] = all(cls[grid.idx(*p['chosen']['seaCell'])] in (SEA, LAKE, RIVER) and cls[grid.idx(*p['chosen']['landCell'])] == LAND and
                                         adjacent(p['chosen']['seaCell'], p['chosen']['landCell']) and
                                         land_of[grid.idx(*p['chosen']['landCell'])] == land_of[grid.idx(*by_id[p['town']]['cell'])] and
                                         grid.dist_cells(tuple(p['chosen']['seaCell']), tuple(by_id[p['town']]['cell'])) <= P['maxPortGateCells'] for p in valid_ports) and \
        len({tuple(p['chosen']['seaCell']) for p in valid_ports}) == len(valid_ports) and len({tuple(p['chosen']['landCell']) for p in valid_ports}) == len(valid_ports)
    checks['transitionsAdjacent'] = all(adjacent(t['landCell'], t['seaCell']) and cls[grid.idx(*t['landCell'])] == LAND and basin_of[grid.idx(*t['seaCell'])] == t['basin'] for t in graph['transitions'])
    checks['idsClosed'] = {s['id'] for s in graph['settlements']} == ids and all(e['id'] in ids for e in graph['exclusions']) and all(p['town'] in ids for p in ports_doc['ports'])
    checks['everythingPendingEngineValidation'] = graph['engineValidation'] == 'pending' and all(t['status'] == 'pendingEngineValidation' for t in graph['transitions']) and all(h['status'] == 'pending' for h in graph['hostChecks'])
    # The travel output's campaign records: exclusions applied, MMR-28 slots resolved, invalid ports dropped.
    kept = lambda rows: {r['id'] for r in rows}
    slot = lambda rec, nm: next((s for s in rec['slots'] if s['name'] == nm), None)
    fmt = lambda v: repr(float(v)).rstrip('0').rstrip('.') if float(v) != int(v) else str(int(v))
    pos_ok = all(slot(r, 'position')['status'] == 'resolved' and [float(x) for x in slot(r, 'position')['value'].split(',')] == [by_id[r['id']]['point']['x'], by_id[r['id']]['point']['y']]
                 for r in out_campaign['towns'] + out_campaign['villages'] + out_campaign['castles'] if 'point' in by_id[r['id']])
    valid_towns = {p['town'] for p in valid_ports}
    checks['campaignRecordsUpdated'] = (kept(out_campaign['villages']) | kept(out_campaign['castles'])) == (kept(campaign['villages']) | kept(campaign['castles'])) - set(exclusions) and \
        {p['town'] for p in out_campaign['ports']} == valid_towns & {p['town'] for p in campaign['ports']} and pos_ok and \
        all(slot(p, 'portSite')['status'] == 'resolved' for p in out_campaign['ports']) and all(t['port'] == (t['id'] in {p['town'] for p in out_campaign['ports']}) for t in out_campaign['towns'])
    return {'name': name, 'source': rel(split), 'terrain': rel(terrain_dir), 'campaign': rel(campaign_dir), 'travel': rel(travel_dir), 'status': graph['status'],
            'reasons': [{'code': r['code'], 'uids': len(r['uids']), 'message': r['message']} for r in graph['reasons']], 'checks': checks,
            'counts': {'landComponents': len(land_sizes), 'groups': n_groups, 'basins': len(basins), 'openBasins': sum(b['open'] for b in basins), 'openSeaCells': sum(open_sea),
                       'harbourTowns': len(ports), 'validPorts': sum(1 for c, _ in ports.values() if c), 'exclusions': len(exclusions), 'requiredBridges': len(pairs),
                       'playerSafeGroups': len(safe), 'mainGroupTowns': towns_in[main], 'positionCollisions': len(collisions)},
            'invalidPortReasons': dict(collections.Counter(c.get('reason') for _, cs in ports.values() for c in cs if c['status'] == 'invalid')),
            'repairs': dict(collections.Counter(r['kind'] for r in graph['repairs'])), 'placements': dict(collections.Counter('%s/%s' % (s['kind'], s['method']) for s in graph['settlements'])),
            'compiledClasses': g['compiledClasses'], 'classes': g['classes'], 'wallSeconds': load(travel_dir + '.process.json').get('wallSeconds') if os.path.exists(travel_dir + '.process.json') else None}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--verify', action='append', default=[])
    ap.add_argument('--repeat', action='append', default=[])
    ap.add_argument('--policy', default=os.path.join(REPO, 'worldgen/policies/playability.json'))
    ap.add_argument('--game', default='D:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord')
    ap.add_argument('--unit-log'); ap.add_argument('--ctest-log'); ap.add_argument('--gate')
    ap.add_argument('--out', required=True)
    a = ap.parse_args()
    policy = load(a.policy)
    schemas = {'graph': load(os.path.join(REPO, 'worldgen/contracts/travel-graph.schema.json')), 'ports': load(os.path.join(REPO, 'worldgen/contracts/ports.schema.json'))}
    calibration_file = os.path.normpath(os.path.join(os.path.dirname(a.policy), policy['calibration']['file']))
    calibration = load(calibration_file)
    src = calibration['source']
    env = calibration['installedPortGateEnvelope']
    calibration_checks = {
        'policyPinsCalibration': sha(calibration_file) == policy['calibration']['sha256'],
        'evidenceHashes': sha(os.path.join(REPO, src['evidence'])) == src['evidenceSha256'] and sha(os.path.join(REPO, src['run'])) == src['runSha256'] and
        sha(os.path.join(REPO, src['faceGroupTable'])) == src['faceGroupTableSha256'],
        'installedEnvelopeHash': (sha(env['source']) == env['sourceSha256']) if os.path.exists(env['source']) else None,
        'portBoundCoversEnvelope': policy['ports']['maxPortGateCells'] * 6.25 >= env['max'],
    }
    verified = [verify(*spec.split('=', 4), policy, schemas) for spec in a.verify]
    repeats = [{'first': rel(x), 'second': rel(y), 'identical': all(sha(os.path.join(x, f)) == sha(os.path.join(y, f)) for f in ('travel-graph.json', 'ports.json', 'cells.bin', 'campaign.json'))}
               for x, y in (spec.split('=', 1) for spec in a.repeat)]
    gate = load(a.gate) if a.gate else None
    summary = {
        'schema': 'mnbl.travel-graph-evidence', 'version': 1, 'ticket': 'MMR-28', 'generatedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'policy': {'file': rel(a.policy), 'sha256': sha(a.policy)}, 'calibration': {'file': rel(calibration_file), 'checks': calibration_checks},
        'tools': {'worldbridge': sha(os.path.join(REPO, 'build/worldbridge/Release/worldbridge.exe'))},
        'unitLog': {'file': rel(a.unit_log), 'sha256': sha(a.unit_log)} if a.unit_log else None,
        'ctestLog': {'file': rel(a.ctest_log), 'sha256': sha(a.ctest_log)} if a.ctest_log else None,
        'gate': {'file': rel(a.gate), 'sha256': sha(a.gate), 'passed': gate['passed'], 'checks': gate['checks']} if gate else None,
        'verified': verified, 'repeats': repeats,
        'passed': all(all(v['checks'].values()) for v in verified) and all(r['identical'] for r in repeats) and (gate is None or gate['passed']) and
        all(v is not False for v in calibration_checks.values()),
        'reproduce': ['./tools/Build.ps1', 'ctest -C Release (build/worldbridge)', 'bash build/research-travel28/run.sh (worldbridge --compile-travel per world)',
                      'powershell -File tools/Test-TravelGate.ps1',
                      'python tools/summarize_travel.py --verify NAME=SPLIT=TERRAIN=CAMPAIGN=TRAVEL [...] --repeat A=B --gate evidence/fwg/travel-gate.json --out evidence/fwg/travel-graph.json'],
    }
    with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(summary, f, indent=1)
    print('passed=%s calibration=%s failing=%s' % (summary['passed'], calibration_checks, [(v['name'], k) for v in verified for k, ok in v['checks'].items() if not ok]))


if __name__ == '__main__':
    main()
