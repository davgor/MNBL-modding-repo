"""Python reference of the MMR-278 terrain shaping stage (src/WorldBridge/src/terrain_shaping.cpp, docs/terrain-compiler.md "Shaping").

shape() re-derives a bundle's shaped 513-sample surface from its navigation field (compiled/terrain/navigation-heightfield.bin and
navigation-masks.bin, the travel compile), its fine masks (masks.bin), the rows at and beyond the pole lines (which shaping leaves as
compiled), the source lake layers, the travel placements and the bundle's own policies. Every step follows the C++ operation order
(Jacobi sweeps, fixed neighbour order, sums in index order, no contraction), so the heights agree bit for bit; tools/summarize_terrain.py
--shaping compares them and evaluates the gates. Pure Python (no numpy).
"""
import json
import math
import os
import struct
from collections import deque

DR = (-1, -1, 0, 1, 1, 1, 0, -1)
DC = (0, 1, 1, 1, 0, -1, -1, -1)
PI = 3.141592653589793
BIG = 1.7976931348623157e308


def radians(deg):
    return deg * PI / 180.0


def degrees(rad):
    return rad * 180.0 / PI


def even_round(x):
    f = math.floor(x)
    d = x - f
    return f if d < .5 else f + 1 if d > .5 else (f if f % 2 == 0 else f + 1)


def round_away(x):
    f = math.floor(x)
    return f + 1 if x - f >= 0.5 else f


def load(path):
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f)


def read_plane(data, magic, side, planes, plane):
    assert data[:8] == magic and struct.unpack_from('<I', data, 8)[0] == side and len(data) == 12 + side * side * planes, magic
    n = side * side
    return list(data[12 + plane * n:12 + (plane + 1) * n])


def read_heightfield(data):
    assert data[:8] == b'MNBLHF01'
    side = struct.unpack_from('<I', data, 8)[0]
    return side, list(struct.unpack_from('<%dd' % (side * side), data, 12))


def quantize(z, decimals=3, zero_span=1.0):
    scale = 10.0 ** decimals
    hmin = math.floor(min(z) * scale) / scale
    hmax = math.ceil(max(z) * scale) / scale
    if hmax <= hmin:
        hmax = hmin + zero_span
    step = (hmax - hmin) / 65535
    q = [int(min(max(even_round((v - hmin) / (hmax - hmin) * 65535), 0.0), 65535.0)) for v in z]
    return hmin, hmax, step, q


class Ctx:
    """Constants of one world: grids, intents, policies."""

    def __init__(self, materials, shaping, projection_doc, coarse_side, fine_side):
        intents = materials['intents']
        self.land, self.ocean, self.lake, self.river = intents['land'], intents['ocean'], intents['lake'], intents['river']
        self.eps = materials['coastEpsilon']
        self.s0, self.s1 = materials['slopeRock']['start'], materials['slopeRock']['full']
        self.policy = shaping
        self.W = projection_doc['target']['logicalWidth']
        self.H = projection_doc['target']['logicalHeight']
        self.strip_units = projection_doc['periodicLayout']['strip']
        self.margin_units = projection_doc['periodicLayout']['poleMargin']
        self.extent = self.W + 2 * self.strip_units
        self.side, self.fside = coarse_side, fine_side
        self.sp = self.extent / (coarse_side - 1)
        self.spf = self.extent / (fine_side - 1)
        self.strip = int(math.floor(self.strip_units / self.sp + 0.5))
        self.strip_f = 2 * self.strip
        self.rn = int(math.floor(self.margin_units / self.sp + 0.5))
        self.rs = self.rn + int(math.floor(self.H / self.sp + 0.5))
        self.P = int(math.floor(self.W / self.sp + 0.5))
        self.PF = 2 * self.P

    def is_land(self, it):
        return it == self.land or it == self.river


def compile_side(world, projection_policy, materials, side):
    """The terrain compile's per-sample rule (summarize_terrain.verify's port) at one output side: z after coherence, biome, intent,
    landform and padding, physical row-major, and the nearest source sample of each (the climate inputs of the layer compile, MMR-255).
    Heights agree with the C++ compile to about 1e-12, categories exactly."""
    n = world['config']['size']
    L = world['layers']
    H0 = [v for row in L['height'] for v in row]
    B = [int(v) for row in L['biome'] for v in row]
    WT = [int(v) for row in L['water_type'] for v in row]
    RV = [int(v) for row in L['river'] for v in row]
    LF = [int(v) for row in L['landform'] for v in row]
    sea = world['effective_config']['sea_level']
    W = projection_policy['logicalWidth']; Hh = W * projection_policy['heightRatio']
    strip = W * projection_policy['stripRatio']; margin = W * projection_policy['poleMarginRatio']
    V = projection_policy['verticalScale']; cm = projection_policy['centralMeridianDeg']
    extent = W + 2 * strip; spacing = extent / (side - 1)
    intents = materials['intents']
    water = {w['code']: intents[w['intent']] for w in materials['waterTypes']}
    river = {r['code']: (intents[r['intent']] if r['intent'] != 'none' else -1) for r in materials['river']}
    eps = materials['coastEpsilon']

    def snap(v):
        r = round(v)
        return float(r) if abs(v - r) < 1e-9 else v

    N = side * side
    z = [0.0] * N; biome = [0] * N; intent = [0] * N; landform = [0] * N; padding = [0] * N; nearest = [0] * N
    for r in range(side):
        yr = r * spacing - margin
        y = min(max(yr, 0.0), Hh)
        for c in range(side):
            x = c * spacing - strip
            u = (x % W) / W + cm / 360.0
            u -= math.floor(u)
            gc = snap(u * (n - 1)); gr = min(max(snap(y / Hh * (n - 1)), 0.0), n - 1)
            if gc >= n - 1:
                gc -= n - 1
            c0 = min(int(math.floor(gc)), n - 2); r0 = min(int(math.floor(gr)), n - 2)
            fx = gc - c0; fy = gr - r0
            a00, a01 = H0[r0 * n + c0], H0[r0 * n + c0 + 1]
            a10, a11 = H0[(r0 + 1) * n + c0], H0[(r0 + 1) * n + c0 + 1]
            top = a00 if fx == 0 else (1 - fx) * a00 + fx * a01
            bottom = a10 if fx == 0 else (1 - fx) * a10 + fx * a11
            h = top if fy == 0 else (1 - fy) * top + fy * bottom
            zz = (h - sea) * V
            cn = int(math.floor(gc + .5)); rn = int(math.floor(gr + .5))
            if cn >= n - 1:
                cn -= n - 1
            rn = min(max(rn, 0), n - 1)
            s = rn * n + cn
            wt, rv = water[WT[s]], river[RV[s]]
            it = rv if (rv >= 0 and materials['riverOverridesLand'] and wt == intents['land']) else wt
            if it == intents['ocean'] and zz > -eps:
                zz = -eps
            elif it in (intents['land'], intents['river']) and zz < eps:
                zz = eps
            i = r * side + c
            z[i] = zz; biome[i] = B[s]; intent[i] = it; landform[i] = LF[s]; padding[i] = 1 if (yr < 0 or yr > Hh) else 0; nearest[i] = s
    return {'z': z, 'biome': biome, 'intent': intent, 'landform': landform, 'padding': padding, 'spacing': spacing, 'nearest': nearest}


class Canon:
    def __init__(self, rows, cols, z, intent):
        self.rows, self.cols, self.z, self.intent = rows, cols, z, intent

    def at(self, r, k):
        return r * self.cols + (k % self.cols)


def canonical_of(z, intent, side, strip, period):
    zc = [0.0] * (side * period)
    ic = [0] * (side * period)
    for r in range(side):
        base = r * side + strip
        for k in range(period):
            zc[r * period + k] = z[base + k]
            ic[r * period + k] = intent[base + k]
    return Canon(side, period, zc, ic)


def physical_of(f, side, strip):
    out = [0.0] * (side * side)
    for r in range(side):
        row = r * f.cols
        for c in range(side):
            out[r * side + c] = f.z[row + (c - strip) % f.cols]
    return out


# ---- the runtime lake rule ----

class LakeSource:
    def __init__(self, side, water_type, water_surface, sea_level, vertical_scale, central_meridian):
        self.side, self.water_type, self.water_surface = side, water_type, water_surface
        self.sea_level, self.vertical_scale, self.cm = sea_level, vertical_scale, central_meridian


def lake_level(s, width, origin_x, origin_y, height, x, y):
    n = s.side - 1
    canonical = x
    if not (origin_x <= x < origin_x + width):
        t = math.fmod(x - origin_x, width)
        if t < 0:
            t += width
        if t >= width:
            t = 0.0
        canonical = origin_x + t
    lx = canonical - origin_x
    ly = max(0.0, min(height, y - origin_y))
    nn = float(n)
    column = math.fmod(nn * (lx / width + s.cm / 360.0), nn)
    if column < 0:
        column += nn
    row = nn * ly / height
    c = int(round_away(column)) % n
    r = int(round_away(max(0.0, min(nn, row))))
    cell = r * s.side + c
    if s.water_type[cell] == 2:
        return s.water_surface[cell]
    level = -math.inf
    for dr in (-1, 0, 1):
        for dc in (-1, 0, 1):
            r2, c2 = r + dr, (c + dc) % n
            if r2 < 0 or r2 > n:
                continue
            k = r2 * s.side + c2
            if s.water_type[k] == 2:
                level = max(level, s.water_surface[k])
    return level


def lake_rule(heights, intent, side, spacing, width, origin_x, origin_y, height, source):
    n = side * side
    seen = bytearray(n)
    owner = [-1] * n
    groups, lakes = [], []
    for start in range(n):
        if seen[start] or intent[start] != 2:
            continue
        group = []
        queue = deque([start])
        seen[start] = 1
        while queue:
            i = queue.popleft()
            group.append(i)
            col, row = i % side, i // side
            for dc, dr in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                cc, rr = col + dc, row + dr
                if cc < 0 or rr < 0 or cc >= side or rr >= side:
                    continue
                j = rr * side + cc
                if not seen[j] and intent[j] == 2:
                    seen[j] = 1
                    queue.append(j)
        lake = {'index': len(groups), 'samples': len(group), 'matched': 0, 'firstColumn': group[0] % side, 'firstRow': group[0] // side,
                'levelM': 0.0, 'levelZ': 0.0, 'z': 0.0, 'bedMaxZ': 0.0, 'shoreMinZ': 0.0, 'clamped': False, 'shown': False}
        level, min_x, max_x, min_y, max_y, bed = -math.inf, 1e300, -1e300, 1e300, -1e300, -1e300
        for i in group:
            x, y = (i % side) * spacing, (i // side) * spacing
            min_x, max_x, min_y, max_y = min(min_x, x), max(max_x, x), min(min_y, y), max(max_y, y)
            bed = max(bed, heights[i])
            lv = lake_level(source, width, origin_x, origin_y, height, x, y)
            if not math.isinf(lv):
                lake['matched'] += 1
                level = max(level, lv)
        lake['bedMaxZ'] = bed
        for i in group:
            owner[i] = len(groups)
        groups.append(group)
        if lake['matched']:
            c0, c1 = int(math.floor(min_x / spacing + 0.5)) - 1, int(math.floor(max_x / spacing + 0.5)) + 1
            r0, r1 = int(math.floor(min_y / spacing + 0.5)) - 1, int(math.floor(max_y / spacing + 0.5)) + 1
            period = int(math.floor(width / spacing + 0.5))
            shore = BIG
            for rr in range(max(0, r0), min(side - 1, r1) + 1):
                for cc in range(c0, c1 + 1):
                    wrapped = cc + period if cc < 0 else cc - period if cc >= side else cc
                    if wrapped < 0 or wrapped >= side:
                        continue
                    at = rr * side + wrapped
                    if intent[at] == 2:  # MMR-285: every lake sample is water, its own or another group's (or the copy's)
                        continue
                    shore = min(shore, heights[at])
            lake['levelM'] = level
            lake['levelZ'] = (level - source.sea_level) * source.vertical_scale
            lake['shoreMinZ'] = shore
            lake['z'] = min(lake['levelZ'], shore - .001)
            lake['clamped'] = lake['z'] < lake['levelZ']
        lakes.append(lake)
    sample_period = int(math.floor(width / spacing + 0.5))
    shown = [bool(l['matched']) and l['z'] > l['bedMaxZ'] for l in lakes]
    verdict = list(shown)
    for k, lake in enumerate(lakes):
        if not lake['matched'] or not any(i % side in (0, side - 1) for i in groups[k]):
            continue
        first = groups[k][0]
        col, row = first % side, first // side
        copy = col + sample_period if col + sample_period < side else col - sample_period
        twin = owner[row * side + copy] if 0 <= copy < side else -1
        if twin < 0 or twin == k:
            continue
        verdict[k] = shown[twin]
        if not shown[twin]:
            continue
        for key in ('levelM', 'levelZ', 'z', 'clamped'):
            lake[key] = lakes[twin][key]
    for k, lake in enumerate(lakes):
        lake['shown'] = verdict[k]
    return lakes, groups


# ---- metrics ----

def faces(z, intent, c, side, sp, strip, period, r_lo, r_hi):
    water_edge = bytearray(side * side)
    for r in range(side):
        for col in range(side):
            edge = not c.is_land(intent[r * side + col])
            j = 0
            while j < 8 and not edge:
                rr, cc = r + DR[j], col + DC[j]
                if 0 <= rr < side and 0 <= cc < side:
                    edge = not c.is_land(intent[rr * side + cc])
                j += 1
            water_edge[r * side + col] = edge

    def tri(r, col, which):
        if which == 0:
            v = (r * side + col, r * side + col + 1, (r + 1) * side + col + 1)
            dx = z[v[1]] - z[v[0]]
            dy = z[v[2]] - z[v[1]]
        else:
            v = (r * side + col, (r + 1) * side + col + 1, (r + 1) * side + col)
            dx = z[v[1]] - z[v[2]]
            dy = z[v[2]] - z[v[0]]
        all_land, any_land, any_ocean, any_edge = True, False, False, False
        for i in v:
            l = c.is_land(intent[i])
            all_land = all_land and l
            any_land = any_land or l
            any_ocean = any_ocean or intent[i] == c.ocean
            any_edge = any_edge or water_edge[i]
        cls = 1 if (all_land and not any_edge) else 2 if (any_ocean and any_land) else 0
        a, b = -dx / sp, -dy / sp
        return (a, b, math.sqrt(a * a + b * b + 1.0), cls)

    def slope(t):
        return degrees(math.atan(math.sqrt(t[0] * t[0] + t[1] * t[1])))

    def crease(p, q):
        d = max(-1.0, min(1.0, (p[0] * q[0] + p[1] * q[1] + 1.0) / (p[2] * q[2])))
        return degrees(math.acos(d))

    interior, coast, creases = [], [], []
    for r in range(r_lo, r_hi):
        for col in range(strip, strip + period):
            t0, t1 = tri(r, col, 0), tri(r, col, 1)
            for t in (t0, t1):
                if t[3] == 1:
                    interior.append(slope(t))
                elif t[3] == 2:
                    coast.append(slope(t))
            if t0[3] == 1 and t1[3] == 1:
                creases.append(crease(t0, t1))
            right = tri(r, col + 1, 1)
            if t0[3] == 1 and right[3] == 1:
                creases.append(crease(t0, right))
            if r + 1 < r_hi:
                below = tri(r + 1, col, 0)
                if t1[3] == 1 and below[3] == 1:
                    creases.append(crease(t1, below))
    return interior, coast, creases


def stats(v):
    v = sorted(v)
    if not v:
        return {'p95': 0.0, 'max': 0.0, 'faces': 0}
    return {'p95': v[min(len(v) - 1, len(v) * 95 // 100)], 'max': v[-1], 'faces': len(v)}


def spikes(z, intent, pad, c, side, sp, strip, period, r_lo, r_hi, t):
    count = 0
    diag = sp * math.sqrt(2.0)
    for r in range(r_lo + 1, r_hi):
        for col in range(strip, strip + period):
            i = r * side + col
            if not c.is_land(intent[i]) or (pad and pad[i]):
                continue
            above = below = steep = True
            for j in range(8):
                nz = z[(r + DR[j]) * side + col + DC[j]]
                bound = t * (diag if j % 2 else sp)
                above = above and z[i] > nz
                below = below and z[i] < nz
                steep = steep and abs(z[i] - nz) > bound
            if (above or below) and steep:
                count += 1
    return count


# ---- pads ----

def block(f, c, spacing, x, y, radius, r_lo, r_hi):
    out = []
    u, v, rad = x / spacing, y / spacing, radius / spacing
    k0, k1 = int(math.floor(u - rad)), int(math.ceil(u + rad))
    r0, r1 = max(r_lo + 1, int(math.floor(v - rad))), min(r_hi - 1, int(math.ceil(v + rad)))
    for r in range(r0, r1 + 1):
        for k in range(k0, k1 + 1):
            i = f.at(r, k)
            if c.is_land(f.intent[i]):
                out.append((i, k * spacing, r * spacing))
    return out


def fit(samples, z, tmax):
    sx = sy = sz = 0.0
    for i, x, y in samples:
        sx += x
        sy += y
        sz += z[i]
    n = float(len(samples))
    mx, my, mz = sx / n, sy / n, sz / n
    xx = xy = yy = xz = yz = 0.0
    for i, x, y in samples:
        dx, dy, dz = x - mx, y - my, z[i] - mz
        xx += dx * dx
        xy += dx * dy
        yy += dy * dy
        xz += dx * dz
        yz += dy * dz
    a = b = 0.0
    det = xx * yy - xy * xy
    if det > 1e-9 * (xx * yy):
        a = (xz * yy - yz * xy) / det
        b = (yz * xx - xz * xy) / det
    g = math.sqrt(a * a + b * b)
    if g > tmax:
        a = a * (tmax / g)
        b = b * (tmax / g)
    return (mx, my, mz, a, b)


def plane_at(p, x, y):
    return p[2] + p[3] * (x - p[0]) + p[4] * (y - p[1])


# ---- the stage ----

def shape(c, coarse_z, coarse_intent, fine_intent, fixed_rows_z, pads, source, before_shown):
    """coarse_z/intent: the navigation field (side c.side); fine_intent: masks.bin intent (side c.fside); fixed_rows_z: fine physical
    heights whose rows at and beyond the pole lines are the compiled ones (only those rows are read); pads: [(id, kind, x, y)];
    before_shown: per lake group of the fine grid, whether the unshaped fine compile showed it. Returns (fine physical z, the
    quantization (hmin, hmax, step, codes), record)."""
    pol = c.policy
    eps, sp, spf = c.eps, c.sp, c.spf
    strip, strip_f, rn, rs, P, PF = c.strip, c.strip_f, c.rn, c.rs, c.P, c.PF
    side_c, side = c.side, c.fside
    rec = {'sweeps': 0, 'residual': 0.0}

    def active(r):
        return rn < r < rs

    def active_f(r):
        return 2 * rn < r < 2 * rs

    g = canonical_of(coarse_z, coarse_intent, side_c, strip, P)
    fm = canonical_of(fixed_rows_z, fine_intent, side, strip_f, PF)
    original = list(g.z)
    pad = bytearray(len(g.z))

    def level_z(x, y):
        lv = lake_level(source, c.W, c.strip_units, c.margin_units, c.H, x, y)
        return lv if math.isinf(lv) else (lv - source.sea_level) * source.vertical_scale

    def lake_groups(f, act):
        groups = []
        seen = bytearray(len(f.z))
        for r in range(f.rows):
            for k in range(f.cols):
                s = f.at(r, k)
                if not act(r) or seen[s] or f.intent[s] != c.lake:
                    continue
                group = []
                queue = deque([s])
                seen[s] = 1
                while queue:
                    i = queue.popleft()
                    group.append(i)
                    rr, kk = i // f.cols, i % f.cols
                    for d0, d1 in ((0, 1), (0, -1), (1, 0), (-1, 0)):
                        r2 = rr + d0
                        if not act(r2):
                            continue
                        j = f.at(r2, kk + d1)
                        if not seen[j] and f.intent[j] == c.lake:
                            seen[j] = 1
                            queue.append(j)
                groups.append(sorted(group))
        return groups

    def lakes_step(f, act, spacing, col_strip, skip):
        for group in lake_groups(f, act):
            level = -math.inf
            for i in group:
                r, k = i // f.cols, i % f.cols
                level = max(level, level_z((col_strip + k) * spacing, r * spacing))
            if math.isinf(level):
                continue
            own = set(group)
            for i in group:
                f.z[i] = min(f.z[i], level - pol['lakes']['bedBelowLevel'])
            for i in group:
                r, k = i // f.cols, i % f.cols
                for j in range(8):
                    r2 = r + DR[j]
                    if not act(r2):
                        continue
                    s = f.at(r2, k + DC[j])
                    if s in own or not c.is_land(f.intent[s]) or (skip is not None and skip[s]):
                        continue
                    f.z[s] = max(f.z[s], level + pol['lakes']['shoreAboveLevel'])

    radius_of = pol['pads']['radius']
    pads = sorted(pads, key=lambda p: p[0])
    blocks = [[s[0] for s in block(fm, c, spf, x, y + c.margin_units, radius_of[kind], 2 * rn, 2 * rs)] for _id, kind, x, y in pads]
    parent = list(range(len(pads)))

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    if pol['pads']['mergeTouching']:
        first = {}
        for a in range(len(pads)):
            reach = set()
            for s in blocks[a]:
                reach.add(s)
                r, k = s // fm.cols, s % fm.cols
                for j in range(8):
                    if 0 <= r + DR[j] < fm.rows:
                        reach.add(fm.at(r + DR[j], k + DC[j]))
            for s in sorted(reach):
                if s in first:
                    x, y = find(a), find(first[s])
                    if x != y:
                        parent[max(x, y)] = min(x, y)
            for s in blocks[a]:
                first.setdefault(s, a)
    unions = {}
    for a in range(len(pads)):
        root = find(a)
        u = unions.setdefault(root, {'pads': [], 'shift': [], 'plane': None, 'empty': True})
        ref = pads[a][2] if not u['pads'] else pads[u['pads'][0]][2]
        u['pads'].append(a)
        u['shift'].append(c.W * float(round_half_away((ref - pads[a][2]) / c.W)))
    order = sorted(unions)
    rec['pads'] = len(pads)
    rec['merged'] = len(pads) - len(unions)

    def union_samples(f, spacing, u, r_lo, r_hi):
        out, seen = [], set()
        for j, a in enumerate(u['pads']):
            _id, kind, x, y = pads[a]
            for s in block(f, c, spacing, x + u['shift'][j], y + c.margin_units, radius_of[kind], r_lo, r_hi):
                if s[0] not in seen:
                    seen.add(s[0])
                    out.append(s)
        return out

    def ring_of(f, spacing, samples, band, act, is_pad):
        seen, ring = {}, {}
        queue = deque()
        for i, x, y in samples:
            seen[i] = (0, x, y)
            queue.append(i)
        while queue:
            s = queue.popleft()
            here = seen[s]
            if here[0] >= band:
                continue
            r, k = s // f.cols, s % f.cols
            for j in range(8):
                if not act(r + DR[j]):
                    continue
                o = f.at(r + DR[j], k + DC[j])
                if o in seen:
                    continue
                nxt = (here[0] + 1, here[1] + DC[j] * spacing, here[2] + DR[j] * spacing)
                seen[o] = nxt
                queue.append(o)
                if c.is_land(f.intent[o]) and not is_pad[o]:
                    ring[o] = nxt
        return ring

    def place_pads(f, spacing, is_pad, r_lo, r_hi, band, fit_planes):
        tmax = math.tan(radians(pol['pads']['maxSlopeDeg']))
        placed = []
        for root in order:
            u = unions[root]
            samples = union_samples(f, spacing, u, r_lo, r_hi)
            if fit_planes:
                u['empty'] = not samples
                if samples:
                    u['plane'] = fit(samples, f.z, tmax)
            if u['empty']:
                placed.append([])
                continue
            for i, x, y in samples:
                f.z[i] = max(plane_at(u['plane'], x, y), eps)
                is_pad[i] = 1
            placed.append(samples)
        if band <= 0:
            return
        for n, root in enumerate(order):
            u = unions[root]
            if u['empty']:
                continue
            ring = ring_of(f, spacing, placed[n], band, lambda r: r_lo < r < r_hi, is_pad)
            for s in sorted(ring):
                dist, x, y = ring[s]
                w = float(band + 1 - dist) / float(band + 1)
                f.z[s] = f.z[s] + (max(plane_at(u['plane'], x, y), eps) - f.z[s]) * w

    def coast_step(f):
        tl = math.tan(radians(pol['coast']['landMaxSlopeDeg']))
        ts = math.tan(radians(pol['coast']['seaMaxSlopeDeg']))
        band = pol['coast']['bandSamples']
        for land_side in (True, False):
            dist = [0] * len(f.z)
            queue = deque()
            for r in range(f.rows):
                for k in range(f.cols):
                    s = f.at(r, k)
                    mine = c.is_land(f.intent[s]) if land_side else f.intent[s] == c.ocean
                    if not mine or not active(r):
                        continue
                    shore = False
                    j = 0
                    while j < 8 and not shore:
                        r2 = r + DR[j]
                        if 0 <= r2 < f.rows:
                            o = f.intent[f.at(r2, k + DC[j])]
                            shore = (o == c.ocean) if land_side else c.is_land(o)
                        j += 1
                    if shore:
                        dist[s] = 1
                        queue.append(s)
            while queue:
                s = queue.popleft()
                if dist[s] >= band:
                    continue
                r, k = s // f.cols, s % f.cols
                for j in range(8):
                    r2 = r + DR[j]
                    if not active(r2):
                        continue
                    o = f.at(r2, k + DC[j])
                    mine = c.is_land(f.intent[o]) if land_side else f.intent[o] == c.ocean
                    if mine and not dist[o]:
                        dist[o] = dist[s] + 1
                        queue.append(o)
            for s in range(len(f.z)):
                if not dist[s]:
                    continue
                if land_side:
                    f.z[s] = min(f.z[s], eps + tl * sp * (dist[s] - 0.5))
                else:
                    f.z[s] = max(f.z[s], -eps - ts * sp * (dist[s] - 0.5))

    def despike_step(f):
        t = math.tan(radians(pol['despike']['maxSlopeDeg']))
        diag = sp * math.sqrt(2.0)
        bound = [t * (diag if j % 2 else sp) for j in range(8)]
        for sweep in range(pol['despike']['sweeps']):
            nxt = list(f.z)
            changed = 0
            residual = 0.0
            for r in range(rn + 1, rs):
                for k in range(f.cols):
                    s = f.at(r, k)
                    if not c.is_land(f.intent[s]) or pad[s]:
                        continue
                    zs = f.z[s]
                    n = [f.z[f.at(r + DR[j], k + DC[j])] for j in range(8)]
                    within = [abs(zs - n[j]) <= bound[j] for j in range(8)]
                    above = all(zs > v for v in n)
                    below = all(zs < v for v in n)
                    steep = not all(within)
                    ridge = sum(1 for j in range(8) if within[j] and within[(j + 4) % 8])
                    if not steep or (pol['despike']['keepRidges'] and ridge >= pol['despike']['ridgeNeighbours']):
                        continue
                    if above:
                        v = n[0] + bound[0]
                        for j in range(1, 8):
                            v = min(v, n[j] + bound[j])
                    elif below:
                        v = n[0] - bound[0]
                        for j in range(1, 8):
                            v = max(v, n[j] - bound[j])
                    else:
                        continue
                    v = max(v, eps)
                    if v != zs:
                        residual = max(residual, abs(v - zs))
                        nxt[s] = v
                        changed += 1
            f.z = nxt
            rec['sweeps'] = sweep + 1
            rec['residual'] = residual
            if not changed:
                break

    for step in pol['coherence']['order']:
        if step == 'lakes' and pol['lakes']['enabled']:
            lakes_step(g, active, sp, strip, None)
        elif step == 'coast' and pol['coast']['enabled']:
            coast_step(g)
        elif step == 'pads' and pol['pads']['enabled']:
            place_pads(g, sp, pad, rn, rs, pol['pads']['blendSamples'], True)
        elif step == 'despike' and pol['despike']['enabled']:
            despike_step(g)
    lp = pol['lowPass']
    if lp['enabled']:
        keep = set(lp['keep'])
        free = bytearray(len(g.z))
        for r in range(rn + 1, rs):
            for k in range(g.cols):
                s = g.at(r, k)
                it = g.intent[s]
                if pad[s] or (it == c.ocean and 'sea' in keep) or (it == c.lake and 'lake' in keep):
                    continue
                if not c.is_land(it) and it != c.ocean and it != c.lake:
                    continue
                if it == c.ocean or it == c.lake:
                    free[s] = 1
                    continue
                edge = False
                for j in range(8):
                    o = g.intent[g.at(r + DR[j], k + DC[j])]
                    edge = edge or o == c.ocean or o == c.lake
                if edge and 'coast' in keep:
                    continue
                free[s] = 1
        kern = lp['kernel']
        half = len(kern) // 2
        total = 0.0
        for w in kern:
            total += w
        tmp = list(g.z)
        for r in range(g.rows):
            for k in range(g.cols):
                s = g.at(r, k)
                if not free[s]:
                    continue
                acc = 0.0
                for j in range(len(kern)):
                    acc += kern[j] * g.z[g.at(r, k + j - half)]
                tmp[s] = acc / total
        res = list(tmp)
        for r in range(g.rows):
            for k in range(g.cols):
                s = g.at(r, k)
                if not free[s]:
                    continue
                acc = 0.0
                for j in range(len(kern)):
                    acc += kern[j] * tmp[g.at(min(max(r + j - half, 0), g.rows - 1), k)]
                res[s] = acc / total
                if c.is_land(g.intent[s]):
                    res[s] = max(res[s], eps)
        g.z = res
    rec['samplesChanged'] = sum(1 for a, b in zip(g.z, original) if a != b)

    # Cubic B-spline 2x.
    rows = g.rows
    xpass = [0.0] * (rows * PF)
    for r in range(rows):
        row = r * P
        zr = g.z

        def at(k):
            return zr[row + k % P]
        for i in range(P):
            xpass[r * PF + 2 * i] = (at(i - 1) + 4 * at(i) + at(i + 1)) / 6.0
            xpass[r * PF + 2 * i + 1] = (at(i - 1) + 23 * at(i) + 23 * at(i + 1) + at(i + 2)) / 48.0
    hz = [0.0] * (side * PF)
    last = rows - 1
    for k in range(PF):
        def at(rr):
            return xpass[min(max(rr, 0), last) * PF + k]
        for r in range(rows):
            hz[(2 * r) * PF + k] = (at(r - 1) + 4 * at(r) + at(r + 1)) / 6.0
            if r < last:
                hz[(2 * r + 1) * PF + k] = (at(r - 1) + 23 * at(r) + 23 * at(r + 1) + at(r + 2)) / 48.0
    h = Canon(side, PF, hz, list(fm.intent))
    pad_f = bytearray(len(h.z))
    for r in range(side):
        for k in range(PF):
            s = r * PF + k
            if not active_f(r):
                h.z[s] = fm.z[s]
                continue
            if h.intent[s] == c.ocean:
                h.z[s] = min(h.z[s], -eps)
            elif c.is_land(h.intent[s]):
                h.z[s] = max(h.z[s], eps)
    if pol['pads']['enabled']:
        place_pads(h, spf, pad_f, 2 * rn, 2 * rs, 2 * pol['pads']['blendSamples'], False)
    if pol['lakes']['enabled']:
        lakes_step(h, active_f, spf, strip_f, pad_f)
    for r in range(2 * rn + 1, 2 * rs):
        for k in range(PF):
            s = r * PF + k
            if c.is_land(h.intent[s]):
                h.z[s] = max(h.z[s], eps)

    # Lake groups shown under the runtime rule (two phases, as the C++).
    phase = 0
    passes = 0
    for pass_ in range(10 ** 6):
        phys = physical_of(h, side, strip_f)
        hmin, hmax, step, q = quantize(phys)
        qv = [hmin + v * step for v in q]
        after, members = lake_rule(qv, fine_intent, side, spf, c.W, c.strip_units, c.margin_units, c.H, source)
        if not pol['lakes']['enabled'] or pass_ >= 16:
            break
        changed = False
        for k, lk in enumerate(after):
            if not lk['matched'] or lk['shown'] or (phase == 1 and not before_shown[k]):
                continue
            target = lk['z'] - pol['lakes']['bedBelowLevel']
            if phase == 0:
                c0, c1, r0, r1 = side, -1, side, -1
                for i in members[k]:
                    col, row = i % side, i // side
                    c0, c1, r0, r1 = min(c0, col), max(c1, col), min(r0, row), max(r1, row)
                shore = BIG
                for rr in range(max(0, r0 - 1), min(side - 1, r1 + 1) + 1):
                    for cc in range(c0 - 1, c1 + 2):
                        wrapped = cc + PF if cc < 0 else cc - PF if cc >= side else cc
                        if wrapped < 0 or wrapped >= side:
                            continue
                        at_ = rr * side + wrapped
                        if fine_intent[at_] == c.lake:
                            continue
                        shore = min(shore, qv[at_])
                target = min(lk['levelZ'], shore - .001) - pol['lakes']['bedBelowLevel']
            for i in members[k]:
                s = (i // side) * PF + (i % side - strip_f) % PF
                if h.z[s] > target:
                    h.z[s] = target
                    changed = True
        if not changed:
            if phase == 0:
                phase = 1
                continue
            break
        passes = pass_ + 1
    rec['bedPasses'] = passes
    rec['after'] = after
    rec['members'] = members
    rec['padF'] = physical_pad(pad_f, side, strip_f, PF)
    rec['pads_sorted'] = pads
    return phys, (hmin, hmax, step, q), rec


def round_half_away(v):
    # std::round: halves away from zero.
    f = math.floor(abs(v))
    r = f + 1 if abs(v) - f >= 0.5 else f
    return r if v >= 0 else -r


def physical_pad(pad_f, side, strip_f, PF):
    out = bytearray(side * side)
    for r in range(side):
        for col in range(side):
            out[r * side + col] = pad_f[r * PF + (col - strip_f) % PF]
    return out
