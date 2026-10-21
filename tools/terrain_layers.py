"""MMR-255: the Python reference of the WorldBridge layer compile (src/WorldBridge/src/terrain_layers.cpp, terrain_layers.hpp).

Target fractions over terrain-materials.json layerSet per output sample (the biome row with the moisture split, tent-kernel smoothing
within the land or water class, then on land: cold, mountain, slope, coast, water edge, ice cap, permanent white), and the
over-compositing alphas a_k = f_k / (f_0 + ... + f_k) as round-half-even bytes, bytes below alpha.minByte dropped, slot 0 at 255. The
operations follow the C++ order exactly (plain double arithmetic, sqrt not hypot), so the planes agree byte for byte; the C++ adds the
zero entries of its dense rows, which leaves every sum unchanged.

Also: weights.bin v2 (MNBLWT02) read and write, the dominant visible layer (the encoder's PHYM rule), composited coverage, and the
climate inputs at the output samples (the nearest source sample, as compile() does). Standard library only.
"""
import math
import struct


def even_round(x):
    f = math.floor(x)
    d = x - f
    return f if d < .5 else f + 1 if d > .5 else (f if f % 2 == 0 else f + 1)


def read_weights(data):
    """MNBLWT02 -> (side, names, planes)."""
    if data[:8] != b'MNBLWT02':
        raise ValueError('not MNBLWT02')
    side, count = struct.unpack_from('<II', data, 8)
    p, names = 16, []
    for _ in range(count):
        n, = struct.unpack_from('<I', data, p)
        names.append(data[p + 4:p + 4 + n].decode('utf-8'))
        p += 4 + n
    N = side * side
    if len(data) != p + count * N:
        raise ValueError('MNBLWT02 size does not match its table')
    return side, names, [data[p + k * N:p + (k + 1) * N] for k in range(count)]


def weights_bytes(side, names, planes):
    out = bytearray(b'MNBLWT02' + struct.pack('<II', side, len(names)))
    for name in names:
        raw = name.encode('utf-8')
        out += struct.pack('<I', len(raw)) + raw
    for plane in planes:
        out += bytes(plane)
    return bytes(out)


def dominant(planes, i):
    rem, best, arg = 1.0, -1.0, 0
    for k in range(len(planes) - 1, 0, -1):
        a = planes[k][i] / 255.0
        if a != 0:
            c = a * rem
            if c > best + 1e-12:
                best, arg = c, k
            rem *= 1.0 - a
        if rem <= 0.0:
            break
    base = (planes[0][i] / 255.0) * rem
    return arg if best >= base and best > 0.0 else 0


def coverage(planes, i):
    out = [0.0] * len(planes)
    rem = 1.0
    for k in range(len(planes) - 1, 0, -1):
        a = planes[k][i] / 255.0
        out[k] = a * rem
        rem *= 1.0 - a
    out[0] = rem
    return out


def climate(world, nearest, snow_threshold):
    """temperature, moisture, ice_cap, rocky_coast and snow months at each output sample (nearest: the source index per sample)."""
    L = world['layers']
    flat = {k: [v for row in L[k] for v in row] for k in ('temperature', 'moisture', 'ice_cap', 'rocky_coast')}
    months = world['seasonal_environment']['months']
    if len(months) != 12:
        raise ValueError('seasonal_environment.months must hold 12 months')
    counts = [0] * len(flat['temperature'])
    for m in months:
        for i, v in enumerate(v for row in m['snow'] for v in row):
            if v >= snow_threshold:
                counts[i] += 1
    return {'temperature': [flat['temperature'][s] for s in nearest], 'moisture': [flat['moisture'][s] for s in nearest],
            'ice_cap': [flat['ice_cap'][s] for s in nearest], 'rocky_coast': [flat['rocky_coast'][s] for s in nearest],
            'snow_months': [counts[s] for s in nearest]}


def compile_layers(materials, side, spacing, period, z, biome, intent, landform, padding, clim):
    """The layer compile on one output grid. Returns {'names', 'planes', 'flora_share', 'record'}."""
    layers = materials['layerSet']
    L = len(layers)
    slot = {l['name']: l['slot'] for l in layers}
    rules = materials['rules']
    intents = materials['intents']
    LAND, OCEAN, LAKE, RIVER = intents['land'], intents['ocean'], intents['lake'], intents['river']
    K = rules['kernelSamples']
    cs, ws = rules['coast']['samples'], rules['waterEdge']['samples']
    reach = max(K, cs, ws, materials['flora']['clearance']['coastSamples'], 1)
    P = period
    if P < 1 or side - 1 - P < reach:
        raise ValueError('the layer neighbourhood is wider than the seam strip')
    moist = rules['moisture']
    wet, dry, cold = slot[moist['wetLayer']], slot[moist['dryLayer']], slot[rules['temperature']['coldLayer']]
    mountain, slope_l, desert_slope = slot[rules['mountainLayer']], slot[rules['slopeLayer']], slot[rules['desertSlopeLayer']]
    coast_l, rocky, edge_l = slot[rules['coast']['layer']], slot[rules['coast']['rockyLayer']], slot[rules['waterEdge']['layer']]
    ice, white_l = slot[rules['iceCap']['layer']], slot[materials['permanentWhite']['layer']]
    mountain_landform = [l['name'] for l in materials['landforms']].index(rules['mountainLandform'])
    edge_lake, edge_river = 'lake' in rules['waterEdge']['intents'], 'river' in rules['waterEdge']['intents']
    split, band = moist['split'], moist['band']
    below = rules['temperature']['belowC']
    share, ice_t = rules['mountainShare'], rules['iceCap']['threshold']
    s0, s1 = materials['slopeRock']['start'], materials['slopeRock']['full']
    pw = materials['permanentWhite']
    min_byte = materials['alpha']['minByte']
    flora_layer = {c: slot[b['layer']] for c, b in materials['flora']['biomes'].items()}
    rows = {}
    for b in materials['biomes']:
        row = [0.0] * L
        for name, f in b['layers'].items():
            row[slot[name]] = f
        rows[b['id']] = {'row': row, 'moisture': b['core'] == moist['biome'], 'desert': b['terrainType'] == 'Desert',
                         'white': b['core'] in pw['biomes'], 'flora': flora_layer.get(b['core'], -1)}
    N = side * side
    land = bytearray(1 if it in (LAND, RIVER) else 0 for it in intent)

    def col(c):
        return c + P if c < 0 else c - P if c > side - 1 else c

    def at(r, c):
        return min(max(r, 0), side - 1) * side + col(c)

    # 1. Rows (sparse): (slot, value) of the nonzero entries in slot order, the moisture split applied.
    sparse_rows = {bid: [(k, v) for k, v in enumerate(b['row']) if v != 0] for bid, b in rows.items()}
    base = [None] * N
    for i in range(N):
        b = rows[biome[i]]
        if b['moisture']:
            row = list(b['row'])
            total = row[wet] + row[dry]
            if total > 0:
                r0 = row[wet] / total
                d = (clim['moisture'][i] - split) / band
                w = r0 + (1.0 - r0) * min(d, 1.0) if d >= 0 else r0 * max(1.0 + d, 0.0)
                row[wet] = total * w
                row[dry] = total * (1.0 - w)
            base[i] = [(k, v) for k, v in enumerate(row) if v != 0]
        else:
            base[i] = sparse_rows[biome[i]]
    planes = [bytearray(N) for _ in range(L)]
    flora_share = [0.0] * N
    plain = bytearray(N)
    targets = [None] * N
    offsets = [(dr, dc, float((K + 1 - abs(dr)) * (K + 1 - abs(dc)))) for dr in range(-K, K + 1) for dc in range(-K, K + 1)]
    reach_c = max(cs, ws)
    near = [(dr, dc, max(abs(dr), abs(dc))) for dr in range(-reach_c, reach_c + 1) for dc in range(-reach_c, reach_c + 1)]
    white = {'samples': 0, 'biome': 0, 'season': 0}
    for r in range(side):
        for c in range(side):
            i = r * side + c
            cls = land[i]
            g = [0.0] * L
            wsum = 0.0
            for dr, dc, w in offsets:
                j = at(r + dr, c + dc)
                if land[j] != cls:
                    continue
                wsum += w
                for k, v in base[j]:
                    g[k] += w * v
            for k in range(L):
                g[k] = g[k] / wsum
            if cls:
                b = rows[biome[i]]
                changed = False
                if clim['temperature'][i] < below:
                    g[cold] = g[cold] + g[wet] + g[dry]
                    g[wet] = 0.0
                    g[dry] = 0.0
                    changed = True
                if landform[i] == mountain_landform:
                    keep = 1.0 - share
                    g = [v * keep for v in g]
                    g[mountain] = g[mountain] + share
                    changed = True
                left, right, up, down = col(c - 1), col(c + 1), max(r - 1, 0), min(r + 1, side - 1)
                sx = (z[r * side + right] - z[r * side + left]) / (2 * spacing)
                sy = (z[down * side + c] - z[up * side + c]) / ((down - up) * spacing)
                s = min(max((math.sqrt(sx * sx + sy * sy) - s0) / (s1 - s0), 0.0), 1.0)
                if s > 0:
                    keep = 1.0 - s
                    g = [v * keep for v in g]
                    k = desert_slope if b['desert'] else slope_l
                    g[k] = g[k] + s
                    changed = True
                coast = False
                edge = edge_river and intent[i] == RIVER
                for dr, dc, d in near:
                    j = at(r + dr, c + dc)
                    if d <= cs and intent[j] == OCEAN:
                        coast = True
                    if d <= ws and ((edge_lake and intent[j] == LAKE) or (edge_river and intent[j] == RIVER)):
                        edge = True
                if coast:
                    rho = max(min(max(clim['rocky_coast'][i], 0.0), 1.0), s)
                    g = [0.0] * L
                    g[coast_l] = 1.0 - rho
                    g[rocky] = g[rocky] + rho
                    changed = True
                if edge:
                    g = [0.0] * L
                    g[edge_l] = 1.0
                    changed = True
                if clim['ice_cap'][i] >= ice_t:
                    g = [0.0] * L
                    g[ice] = 1.0
                    changed = True
                season = clim['snow_months'][i] >= pw['snowMonths']
                if b['white'] or season:
                    g = [0.0] * L
                    g[white_l] = 1.0
                    changed = True
                    if c < P and not padding[i]:
                        white['samples'] += 1
                        white['biome'] += 1 if b['white'] else 0
                        white['season'] += 1 if (season and not b['white']) else 0
                if b['flora'] >= 0:
                    flora_share[i] = g[b['flora']]
                if not changed:
                    plain[i] = all(land[at(r + dr, c + dc)] and biome[at(r + dr, c + dc)] == biome[i] for dr, dc, _ in offsets)
            total = 0.0
            for k in range(L):
                total += g[k]
                if k == 0:
                    byte = 255
                elif g[k] > 0 and total > 0:
                    byte = min(255.0, even_round(g[k] / total * 255.0))
                else:
                    byte = 0
                if k > 0 and byte < min_byte:
                    byte = 0
                planes[k][i] = int(byte)
            targets[i] = g
    # The record over canonical land.
    rec = {'land': 0, 'target': [0.0] * L, 'dominant': [0.0] * L, 'interior': 0, 'interiorMaxError': 0.0, 'moisturePairMaxError': 0.0,
           'biomeCoverage': {}, 'biomeLand': {}, 'permanentWhite': white}
    for r in range(side):
        for c in range(P):
            i = r * side + c
            if padding[i] or not land[i]:
                continue
            rec['land'] += 1
            g = targets[i]
            for k in range(L):
                rec['target'][k] += g[k]
            rec['dominant'][dominant(planes, i)] += 1
            cov = coverage(planes, i)
            bc = rec['biomeCoverage'].setdefault(biome[i], [0.0] * L)
            for k in range(L):
                bc[k] += cov[k]
            rec['biomeLand'][biome[i]] = rec['biomeLand'].get(biome[i], 0) + 1
            if not plain[i]:
                continue
            rec['interior'] += 1
            b = rows[biome[i]]
            if b['moisture']:
                rec['moisturePairMaxError'] = max(rec['moisturePairMaxError'], abs(cov[wet] + cov[dry] - (b['row'][wet] + b['row'][dry])))
                for k in range(L):
                    if k not in (wet, dry):
                        rec['interiorMaxError'] = max(rec['interiorMaxError'], abs(cov[k] - b['row'][k]))
            else:
                for k in range(L):
                    rec['interiorMaxError'] = max(rec['interiorMaxError'], abs(cov[k] - b['row'][k]))
    if rec['land']:
        rec['target'] = [v / rec['land'] for v in rec['target']]
        rec['dominant'] = [v / rec['land'] for v in rec['dominant']]
    for bid, bc in rec['biomeCoverage'].items():
        rec['biomeCoverage'][bid] = [v / rec['biomeLand'][bid] for v in bc]
    return {'names': [l['name'] for l in layers], 'planes': planes, 'flora_share': flora_share, 'record': rec}


def seam_mismatches(planes, side, period):
    """Samples whose plane value differs between columns c and c + period, over every plane."""
    bad = 0
    for plane in planes:
        for r in range(side):
            o = r * side
            for c in range(side - period):
                if plane[o + c] != plane[o + c + period]:
                    bad += 1
    return bad


def template_layer_names(scene_xml_text):
    """The material layer names of a surface template's scene.xscene, in slot order."""
    import re
    block = scene_xml_text[scene_xml_text.index('<layers version="1">'):scene_xml_text.index('</layers>')]
    return re.findall(r'<layer name="([^"]+)">', block)


def base_weights(side, names):
    """MNBLWT02 with the base layer only (255) and every other layer empty: for fixture encodings that carry no compiled layers."""
    N = side * side
    return weights_bytes(side, names, [b'\xff' * N] + [bytes(N)] * (len(names) - 1))
