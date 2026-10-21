"""Independently verify MMR-26 compiled terrain and write evidence/fwg/terrain-compile.json.

For one compiled world (the size-129 export), every output sample is recomputed in Python from the source
world.json and the three policy files: height (seam-aware bilinear through the projection's inverse), categorical
masks (nearest), intent, coastline coherence, the layer weights (weights.bin v2, tools/terrain_layers.py), the 16-bit code (global range, round-half-even) and the
big-endian byte order. The script also checks paired seam columns and shared node borders, compares the encoder's
quantized heights with the compiler's, and tabulates the fidelity and performance of every compiled source size.
Usage:
  python tools/summarize_terrain.py --verify SOURCE=COMPILED --encoded DIR --sizes N=COMPILED [...] --probe RESULT.json --out FILE
  python tools/summarize_terrain.py --shaping A=BUNDLE --shaping B=BUNDLE --out evidence/fwg/terrain-shaping.json
The --shaping mode (MMR-278) re-derives each bundle's shaped 513-sample surface with tools/terrain_shaping.py (z error 0), recomputes its
shaping record and evaluates the shaping gates (tools/verify_terrain_shaping.py).
  python tools/summarize_terrain.py --layers A=BUNDLE --layers B=BUNDLE --out evidence/map-epic-2/terrain-layers.json
The --layers mode (MMR-255) re-derives each bundle's layer planes in Python (byte parity), compares every biome's composited coverage with
the shared table, checks the periodic seam on every plane of weights.bin, encodes the surface with the C++ encoder and the tracked template
(tools/scenebin/make_surface_template.py --materialize) and checks the encoded terrain.bin's seams on every plane, and records the map trees.
"""
import argparse
import datetime
import hashlib
import json
import math
import os
import struct
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import terrain_layers as tl  # noqa: E402


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def rel(path):
    path = os.path.abspath(path)
    return os.path.relpath(path, REPO).replace('\\', '/') if path.startswith(REPO) else path


def load(path):
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f)


def even_round(x):
    f = math.floor(x)
    d = x - f
    return f if d < .5 else f + 1 if d > .5 else (f if f % 2 == 0 else f + 1)


def verify(source_dir, compiled_dir, projection_policy, materials, imp):
    world = load(os.path.join(source_dir, 'world.json'))
    meta = load(os.path.join(compiled_dir, 'terrain.json'))
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
    side = meta['output']['side']; extent = W + 2 * strip; spacing = extent / (side - 1); period = round(W / spacing)
    intents = materials['intents']
    water = {w['code']: intents[w['intent']] for w in materials['waterTypes']}
    river = {r['code']: (intents[r['intent']] if r['intent'] != 'none' else -1) for r in materials['river']}
    eps = materials['coastEpsilon']

    def snap(v):
        r = round(v)
        return float(r) if abs(v - r) < 1e-9 else v

    N = side * side
    z = [0.0] * N; biome = [0] * N; intent = [0] * N; landform = [0] * N; nearest = [0] * N
    for r in range(side):
        for c in range(side):
            x = c * spacing - strip
            y = min(max(r * spacing - margin, 0.0), Hh)
            canon = x % W
            u = canon / W + cm / 360.0
            u -= math.floor(u)
            gc = snap(u * (n - 1)); gr = min(max(snap(y / Hh * (n - 1)), 0.0), n - 1)
            if gc >= n - 1:
                gc -= n - 1
            c0 = min(int(math.floor(gc)), n - 2); r0 = min(int(math.floor(gr)), n - 2)
            fx = gc - c0; fy = gr - r0
            at = lambda rr, cc: H0[rr * n + cc]
            top = at(r0, c0) if fx == 0 else (1 - fx) * at(r0, c0) + fx * at(r0, c0 + 1)
            bottom = at(r0 + 1, c0) if fx == 0 else (1 - fx) * at(r0 + 1, c0) + fx * at(r0 + 1, c0 + 1)
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
            z[i] = zz; biome[i] = B[s]; intent[i] = it; landform[i] = LF[s]; nearest[i] = s
    # MMR-255: the layer compile (tools/terrain_layers.py) on the compiled heights.
    padding = [1 if (r * spacing - margin < 0 or r * spacing - margin > Hh) else 0 for r in range(side) for c in range(side)]
    clim = tl.climate(world, nearest, materials['permanentWhite']['snowThreshold'])
    layers = tl.compile_layers(materials, side, spacing, period, z, biome, intent, landform, padding, clim)
    hmin = math.floor(min(z) * 1000) / 1000; hmax = math.ceil(max(z) * 1000) / 1000
    if hmax <= hmin:
        hmax = hmin + 1
    step = (hmax - hmin) / 65535
    q = [int(min(max(even_round((v - hmin) / (hmax - hmin) * 65535), 0), 65535)) for v in z]

    with open(os.path.join(compiled_dir, 'heightfield.bin'), 'rb') as f:
        hf = f.read()
    with open(os.path.join(compiled_dir, 'weights.bin'), 'rb') as f:
        wb = f.read()
    with open(os.path.join(compiled_dir, 'height-q16.bin'), 'rb') as f:
        qb = f.read()
    with open(os.path.join(compiled_dir, 'masks.bin'), 'rb') as f:
        mb = f.read()
    cz = struct.unpack_from('<%dd' % N, hf, 12)
    cq = [(qb[28 + 2 * i] << 8) | qb[29 + 2 * i] for i in range(N)]
    qhmin, qhmax = struct.unpack_from('<dd', qb, 12)
    checks = {
        'headers': hf[:8] == b'MNBLHF01' and wb[:8] == b'MNBLWT02' and qb[:8] == b'MNBLQ16B' and mb[:8] == b'MNBLMK01'
                   and all(struct.unpack_from('<I', b, 8)[0] == side for b in (hf, wb, qb, mb)),
        'projectionIdentityRecorded': meta['projectionSha256'] == sha(os.path.join(compiled_dir, 'projection.json')),
        'heightsRecomputed': max(abs(a - b) for a, b in zip(cz, z)) <= 1e-12,
        'biomeRecomputed': list(mb[12:12 + N]) == biome,
        'intentRecomputed': list(mb[12 + N:12 + 2 * N]) == intent,
        'landformRecomputed': list(mb[12 + 2 * N:12 + 3 * N]) == landform,
        'paddingRecomputed': list(mb[12 + 3 * N:12 + 4 * N]) == padding,
        'layerWeightsRecomputed': tl.read_weights(wb)[2] == [bytes(p) for p in layers['planes']] and tl.read_weights(wb)[1] == layers['names'],
        'quantizationRecomputed': cq == q and qhmin == hmin and qhmax == hmax,
        'bigEndianCodes': cq[0] == q[0] and (len(set(q)) < 2 or qb[28] == q[0] >> 8),
        'roundtripWithinHalfStep': max(abs(hmin + v * step - zz) for v, zz in zip(q, z)) <= step / 2 * (1 + 1e-9),
        'seamPairsEqual': all(z[r * side + c] == z[r * side + c + period] and biome[r * side + c] == biome[r * side + c + period]
                              and intent[r * side + c] == intent[r * side + c + period] for r in range(side) for c in range(side - period))
                          and tl.seam_mismatches(layers['planes'], side, period) == 0,
        'oceanBelowDatum': all(zz <= -eps for zz, it in zip(z, intent) if it == intents['ocean']),
        'landAboveDatum': all(zz >= eps for zz, it in zip(z, intent) if it in (intents['land'], intents['river'])),
        'engineSignSurvives': all((hmin + v * step <= -0.03) if it == intents['ocean'] else (hmin + v * step >= 0.03) if it in (intents['land'], intents['river']) else True
                                  for v, it in zip(q, intent)),
        'rowZeroIsNorthPadding': all(z[c] == z[side + c] for c in range(side)) if margin > 0 else True,
    }
    fidelity = rederive_fidelity(world, materials, z, intent, side, spacing, strip, margin, Hh, W, n, sea, V)
    for key, value in fidelity.items():
        expected = meta['fidelity'][key]
        checks['fidelity.' + key] = abs(value - expected) <= 1e-9 * max(1.0, abs(expected)) if isinstance(value, float) else value == expected
    return {'source': rel(source_dir), 'sourceWorldSha256': sha(os.path.join(source_dir, 'world.json')), 'compiled': rel(compiled_dir),
            'side': side, 'spacing': spacing, 'seamPeriodColumns': period, 'hmin': hmin, 'hmax': hmax, 'checks': checks}


def rederive_fidelity(world, materials, z, intent, side, spacing, strip, margin, Hh, W, n, sea, V):
    # Independent fidelity: distinct source samples hit, height loss and coast agreement at every distinct source
    # sample, and source land components (8-neighbourhood, wrapped, poles joined to their rings) lost to resampling.
    L = world['layers']
    H0 = [v for row in L['height'] for v in row]
    intents = materials['intents']
    water = {w['code']: intents[w['intent']] for w in materials['waterTypes']}
    river = {r['code']: (intents[r['intent']] if r['intent'] != 'none' else -1) for r in materials['river']}
    WT = [int(v) for row in L['water_type'] for v in row]
    RV = [int(v) for row in L['river'] for v in row]

    def src_intent(i):
        wt, rv = water[WT[i]], river[RV[i]]
        return rv if (rv >= 0 and materials['riverOverridesLand'] and wt == intents['land']) else wt

    m = n - 1
    nodes = [(0, 0)] + [(c, r) for r in range(1, n - 1) for c in range(m)] + [(0, n - 1)]
    index = {rc: k for k, rc in enumerate(nodes)}

    def node_of(c, r):
        return 0 if r == 0 else len(nodes) - 1 if r == n - 1 else index[(c % m, r)]

    hit = set()
    for r in range(side):
        y = r * spacing - margin
        if y < 0 or y > Hh:
            continue
        for c in range(side):
            canon = (c * spacing - strip) % W
            gc, gr = canon / W * m, y / Hh * m
            if abs(gc - round(gc)) < 1e-9 and abs(gr - round(gr)) < 1e-9:
                hit.add(node_of(int(round(gc)) % m, int(round(gr))))

    def compiled_at(xp, yp):
        c = min(max(xp / spacing, 0), side - 1)
        r = min(max(yp / spacing, 0), side - 1)
        c0 = min(int(c), side - 2)
        r0 = min(int(r), side - 2)
        fx = c - c0
        fy = r - r0
        at = lambda rr, cc: z[rr * side + cc]
        return (1 - fy) * ((1 - fx) * at(r0, c0) + fx * at(r0, c0 + 1)) + fy * ((1 - fx) * at(r0 + 1, c0) + fx * at(r0 + 1, c0 + 1))

    total = 0.0
    worst = 0.0
    mismatch = 0
    land = [False] * len(nodes)
    represented = [False] * len(nodes)
    for k, (c, r) in enumerate(nodes):
        i = r * n + c
        pole = r == 0 or r == n - 1
        xp = (0.0 if pole else (W * c / m)) + strip
        yp = Hh * r / m + margin
        d = abs(compiled_at(xp, yp) - (H0[i] - sea) * V)
        total += d * d
        worst = max(worst, d)
        want = src_intent(i)
        got = intent[int(math.floor(yp / spacing + 0.5)) * side + int(math.floor(xp / spacing + 0.5))]  # llround: halves away from zero
        mismatch += want != got
        land[k] = want in (intents['land'], intents['river'])
        represented[k] = (not pole) and got in (intents['land'], intents['river'])
    parent = list(range(len(nodes)))

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    def join(a, b):
        a, b = find(a), find(b)
        if a != b:
            parent[max(a, b)] = min(a, b)

    for k, (c, r) in enumerate(nodes):
        if not land[k]:
            continue
        if r in (0, n - 1):
            ring = 1 if r == 0 else n - 2
            for cc in range(m):
                o = node_of(cc, ring)
                if land[o]:
                    join(k, o)
            continue
        for dr in (-1, 0, 1):
            for dc in (-1, 0, 1):
                rr = r + dr
                if 0 <= rr <= n - 1 and (dr or dc):
                    o = node_of(c + dc, rr)
                    if land[o]:
                        join(k, o)
    components = {}
    for k in range(len(nodes)):
        if land[k]:
            entry = components.setdefault(find(k), [0, False])
            entry[0] += 1
            entry[1] = entry[1] or represented[k]
    lost = [v for v in components.values() if not v[1]]
    return {'sourceSamples': len(nodes), 'sourceSamplesRepresented': len(hit), 'heightRmsAtSourceSamples': math.sqrt(total / len(nodes)),
            'heightMaxAtSourceSamples': worst, 'coastMismatchedSourceSamples': mismatch, 'landComponents': len(components),
            'landComponentsLost': len(lost), 'landSamplesLost': sum(v[0] for v in lost)}


def encoded_nodes(encoded_dir, compiled_dir, side, nodes_xy):
    # Decode the encoded terrain.bin: every node's 16-bit heights and layer weights must equal the compiled rasters (a layer a node does
    # not carry is 0 there), and the shared borders of neighbouring nodes must agree.
    sys.path.insert(0, os.path.join(REPO, 'tools', 'scenebin'))
    import terrain_bin
    with open(os.path.join(encoded_dir, 'terrain.bin'), 'rb') as f:
        tb = terrain_bin.parse(f.read())
    with open(os.path.join(compiled_dir, 'height-q16.bin'), 'rb') as f:
        qb = f.read()
    with open(os.path.join(compiled_dir, 'weights.bin'), 'rb') as f:
        _side, _names, planes = tl.read_weights(f.read())
    q = [(qb[28 + 2 * i] << 8) | qb[29 + 2 * i] for i in range(side * side)]
    nx = nodes_xy[0]
    cells = (side - 1) // nx
    R = cells + 1
    tiles = {}
    content_ok = True
    compared = 0
    for (ix, iy), rec in zip(tb.node_grid_positions(nx), tb.nodes()):
        _w, _h, _ch, hs = terrain_bin.png_decode(rec.height_png)
        heights = list(hs)
        weights = {}
        for k, png in enumerate(rec.weights):
            if png is not None:
                weights[k] = list(terrain_bin.png_decode(png)[3])
        tiles[(ix, iy)] = (heights, weights)
        for rr in range(R):
            for cc in range(R):
                g = (iy * cells + rr) * side + ix * cells + cc
                content_ok = content_ok and heights[rr * R + cc] == q[g] and all((weights[k][rr * R + cc] if k in weights else 0) == planes[k][g] for k in range(len(planes)))
                compared += 1
    borders_ok = True
    border_samples = 0
    for (ix, iy), (h1, w1) in tiles.items():
        for (jx, jy), pairs in (((ix + 1, iy), [(k * R + cells, k * R) for k in range(R)]), ((ix, iy + 1), [(cells * R + k, k) for k in range(R)])):
            if (jx, jy) not in tiles:
                continue
            h2, w2 = tiles[(jx, jy)]
            for a, b in pairs:
                borders_ok = borders_ok and h1[a] == h2[b] and all(w1.get(k, [0] * (R * R))[a] == w2.get(k, [0] * (R * R))[b] for k in set(w1) | set(w2))
                border_samples += 1
    return {'nodesDecoded': len(tiles), 'nodeSamplesCompared': compared, 'nodeContentEqualsCompiled': content_ok,
            'sharedBorderSamples': border_samples, 'sharedBordersEqual': borders_ok}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--verify')
    ap.add_argument('--encoded')
    ap.add_argument('--shaping', action='append', default=[], help='NAME=BUNDLE: verify the shaped terrain of a bundle (MMR-278)')
    ap.add_argument('--layers', action='append', default=[], help='NAME=BUNDLE: verify the layer compile and map trees of a bundle (MMR-255)')
    ap.add_argument('--template', default=os.path.join(REPO, 'build/surface-template'), help='the materialized tracked surface template')
    ap.add_argument('--bridge', default=os.path.join(REPO, 'build/worldbridge/Release/worldbridge.exe'))
    ap.add_argument('--sizes', action='append', default=[])
    ap.add_argument('--probe')
    ap.add_argument('--unit-log')
    ap.add_argument('--ctest-log')
    ap.add_argument('--projection-policy', default=os.path.join(REPO, 'worldgen/policies/projection.json'))
    ap.add_argument('--materials', default=os.path.join(REPO, 'worldgen/policies/terrain-materials.json'))
    ap.add_argument('--import', dest='imp', default=os.path.join(REPO, 'worldgen/calibration/terrain-import/terrain-import.json'))
    ap.add_argument('--out', required=True)
    a = ap.parse_args()
    if a.shaping:
        return shaping_main(a)
    if a.layers:
        return layers_main(a)
    if not a.verify or not a.encoded:
        ap.error('--verify and --encoded are required without --shaping')
    projection_policy, materials, imp = load(a.projection_policy), load(a.materials), load(a.imp)
    source_dir, compiled_dir = a.verify.split('=', 1)
    verified = verify(source_dir, compiled_dir, projection_policy, materials, imp)
    # The encoder re-derives the same range and codes from the compiled heightfield.
    with open(os.path.join(a.encoded, 'quantized-heightfield.bin'), 'rb') as f:
        enc = f.read()
    side = verified['side']
    encoded = struct.unpack_from('<%dd' % (side * side), enc, 12)
    with open(os.path.join(compiled_dir, 'height-q16.bin'), 'rb') as f:
        qb = f.read()
    hmin, hmax = struct.unpack_from('<dd', qb, 12)
    step = (hmax - hmin) / 65535
    mine = [hmin + ((qb[28 + 2 * i] << 8) | qb[29 + 2 * i]) * step for i in range(side * side)]
    surface = load(os.path.join(a.encoded, 'surface.json'))
    verified['checks']['encoderUsesCompiledRangeAndCodes'] = max(abs(x - y) for x, y in zip(encoded, mine)) == 0 and surface['minHeight'] == hmin and surface['maxHeight'] == hmax
    verified['checks']['encoderUsesCompiledWeights'] = surface['weightSource'].startswith('compiled layer weights')
    nodes = encoded_nodes(a.encoded, compiled_dir, side, imp['terrain']['nodes'])
    verified['encodedNodes'] = nodes
    verified['checks']['encodedNodeContentEqualsCompiled'] = nodes['nodeContentEqualsCompiled'] and nodes['nodesDecoded'] == imp['terrain']['nodes'][0] * imp['terrain']['nodes'][1]
    verified['checks']['encodedSharedNodeBordersEqual'] = nodes['sharedBordersEqual'] and nodes['sharedBorderSamples'] > 0
    sizes = []
    for spec in a.sizes:
        size, folder = spec.split('=', 1)
        meta = load(os.path.join(folder, 'terrain.json'))
        process = load(folder + '.process.json') if os.path.exists(folder + '.process.json') else {}
        f = meta['fidelity']
        sizes.append({'sourceSide': int(size), 'compiled': rel(folder), 'terrainJsonSha256': sha(os.path.join(folder, 'terrain.json')),
                      'sourceCellWorldUnits': f['sourceCellWorldUnits'], 'outputSpacing': f['outputSpacing'],
                      'outputSamplesPerSourceCell': f['outputSamplesPerSourceCell'],
                      'outputSamplesOnSourceSamples': f['outputSamplesOnSourceSamples'], 'outputSamplesInDomain': f['outputSamplesInDomain'],
                      'sourceSamples': f['sourceSamples'], 'sourceSamplesRepresented': f['sourceSamplesRepresented'], 'unrepresentedSourceSamplesFraction': 1 - f['sourceSamplesRepresented'] / f['sourceSamples'],
                      'heightRmsAtSourceSamples': f['heightRmsAtSourceSamples'], 'heightMaxAtSourceSamples': f['heightMaxAtSourceSamples'],
                      'coastMismatchedFraction': f['coastMismatchedSourceSamples'] / f['sourceSamples'],
                      'landComponents': f['landComponents'], 'landComponentsLost': f['landComponentsLost'], 'landSamplesLost': f['landSamplesLost'],
                      'coherence': meta['coherence'], 'checks': meta['checks'], 'performance': meta['performance'],
                      'wallSeconds': process.get('wallSeconds'), 'peakWorkingSetBytes': process.get('peakWorkingSetBytes')})
    probe = None
    if a.probe and os.path.exists(a.probe):
        result = load(a.probe)
        probe = {'file': rel(a.probe), 'sha256': sha(a.probe), 'status': result.get('status'), 'pid': result.get('processId'),
                 'moduleSha256': result.get('moduleSha256'), 'screenshots': (result.get('outputs') or {}).get('screenshots'),
                 'extent': (result.get('outputs') or {}).get('extent'), 'centerHeight': (result.get('outputs') or {}).get('centerHeight')}
    summary = {
        'schema': 'mnbl.terrain-compile', 'version': 1, 'ticket': 'MMR-26',
        'generatedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'policies': {name: {'file': rel(path), 'sha256': sha(path)} for name, path in
                     (('projection', a.projection_policy), ('materials', a.materials), ('import', a.imp))},
        'tools': {'worldbridge': sha(os.path.join(REPO, 'build/worldbridge/Release/worldbridge.exe'))},
        'unitLog': {'file': rel(a.unit_log), 'sha256': sha(a.unit_log)} if a.unit_log else None,
        'ctestLog': {'file': rel(a.ctest_log), 'sha256': sha(a.ctest_log)} if a.ctest_log else None,
        'verified': verified, 'encoded': {'dir': rel(a.encoded), 'surfaceJsonSha256': sha(os.path.join(a.encoded, 'surface.json')),
                                          'terrainBinSha256': sha(os.path.join(a.encoded, 'terrain.bin'))},
        'sizes': sizes, 'visualProbe': probe,
        'passed': all(verified['checks'].values()) and (probe is None or probe['status'] == 'ok'),
        'reproduce': [
            './tools/Build.ps1', 'ctest -C Release (build/worldbridge)',
            'worldbridge --compile-terrain build/research-source23/split-<n> --contract worldgen/contracts/source-compatibility.json --projection-policy worldgen/policies/projection.json --materials worldgen/policies/terrain-materials.json --import worldgen/calibration/terrain-import/terrain-import.json --side 257 --out build/research-terrain26/s<n>',
            'python tools/encode_compiled_terrain.py --compiled build/research-terrain26/s129 --scene-name mnbl_terrain26_s129 --out build/research-terrain26/mnbl_terrain26_s129',
            "./tools/Deploy-SceneSlots.ps1 -Map 'build/research-terrain26/mnbl_terrain26_s129=SceneObj/mnbl_terrain26_s129'",
            "./tools/Start-OwnedProbe.ps1 -Probe native_render -ArgsJson '{sceneName: mnbl_terrain26_s129, views: ...}'"],
    }
    with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(summary, f, indent=1)
    print('passed=%s failing=%s' % (summary['passed'], [k for k, v in verified['checks'].items() if not v]))


def shaping_main(a):
    sys.path.insert(0, os.path.join(REPO, 'tools'))
    import verify_terrain_shaping
    worlds = {}
    for spec in a.shaping:
        name, folder = spec.split('=', 1)
        worlds[name] = verify_terrain_shaping.verify(os.path.abspath(folder), a.bridge)
        worlds[name]['bundle'] = rel(folder)
    policy = os.path.join(REPO, 'worldgen/policies/terrain-shaping.json')
    summary = {
        'schema': 'mnbl.terrain-shaping-gates', 'version': 1, 'ticket': 'MMR-278',
        'generatedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'policy': {'file': rel(policy), 'sha256': sha(policy)},
        'tools': {'worldbridge': sha(a.bridge)},
        'worlds': worlds,
        'passed': all(w['passed'] for w in worlds.values()),
        'reproduce': ['./tools/Build.ps1', 'C:/Users/davgo/m2/refresh-fixtures.ps1 -Root <worktree> (A\' seed 42, B\' seed 73)',
                      'python tools/summarize_terrain.py --shaping A=<A bundle> --shaping B=<B bundle> --out evidence/fwg/terrain-shaping.json'],
    }
    with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(summary, f, indent=1)
    for name, w in worlds.items():
        print(name, 'passed=%s' % w['passed'], 'failing gates=%s' % [k for k, v in w['gates'].items() if not v['passed']],
              'failing checks=%s' % [k for k, v in w['checks'].items() if not v], 'zError=%r' % w['gates']['pythonParity']['zError'])


def layers_one(bundle, bridge, template):
    import shutil
    import subprocess
    import tempfile
    import terrain_shaping as ts
    pol = os.path.join(bundle, 'policies')
    terrain = os.path.join(bundle, 'compiled', 'terrain')
    materials = load(os.path.join(pol, 'terrain-materials.json'))
    projection_policy = load(os.path.join(pol, 'projection.json'))
    record = load(os.path.join(terrain, 'shaping.json'))
    world = load(os.path.join(bundle, 'source', 'world.json'))
    with open(os.path.join(terrain, 'heightfield.bin'), 'rb') as f:
        side, z = ts.read_heightfield(f.read())
    with open(os.path.join(terrain, 'masks.bin'), 'rb') as f:
        masks = f.read()
    biome, intent, landform, padding = (ts.read_plane(masks, b'MNBLMK01', side, 4, k) for k in range(4))
    with open(os.path.join(terrain, 'weights.bin'), 'rb') as f:
        wside, names, planes = tl.read_weights(f.read())
    period = record['surface']['seamPeriodColumns']
    spacing = record['surface']['spacing']
    checks = {}
    # Python parity: the layer compile from the shaped heights, the masks and the nearest source climate.
    fine = ts.compile_side(world, projection_policy, materials, side)
    clim = tl.climate(world, fine['nearest'], materials['permanentWhite']['snowThreshold'])
    lay = tl.compile_layers(materials, side, spacing, period, z, biome, intent, landform, padding, clim)
    differing = sum(1 for a, b in zip(lay['planes'], planes) for x, y in zip(a, b) if x != y)
    checks['pythonParity'] = wside == side and names == lay['names'] and differing == 0
    # The layer set is the policy's; every plane is seam-identical; plane 0 is the base.
    checks['layersAreThePolicySet'] = names == [l['name'] for l in materials['layerSet']] and len(names) == 15
    seam = tl.seam_mismatches(planes, side, period)
    checks['seamEveryPlaneIdentical'] = seam == 0 and record['layerCompile']['seamPairs'] == side * (side - period)
    checks['basePlane255'] = all(v == 255 for v in planes[0])
    # The shared table: plain interior samples composite to their biome row (within byte rounding), and every land biome's mean coverage.
    lc = record['layerCompile']
    checks['interiorMatchesTable'] = lc['interiorSamples'] > 0 and lc['interiorMaxError'] <= 0.01 and lc['moisturePairMaxError'] <= 0.01
    rows = {b['id']: b for b in materials['biomes']}
    biomes = {}
    for key, b in sorted(lc['biomeCoverage'].items(), key=lambda kv: int(kv[0])):
        row = rows[int(key)]
        table = {n: row['layers'].get(n, 0.0) for n in names}
        cov = {n: round(v, 4) for n, v in zip(names, b['coverage']) if v >= 0.0005}
        biomes[row['core']] = {'landSamples': b['landSamples'], 'table': {n: v for n, v in table.items() if v}, 'coverage': cov,
                               'tableLayersCoverage': round(sum(b['coverage'][names.index(n)] for n, v in table.items() if v), 4)}
    # The permanent-white mask: land ice (and 12-month snow) is all permanent snow.
    white = names.index(materials['permanentWhite']['layer'])
    land_ice = [i for i in range(side * side) if rows[biome[i]]['core'] in materials['permanentWhite']['biomes'] and intent[i] in (materials['intents']['land'], materials['intents']['river'])]
    checks['permanentWhiteIsSnow'] = all(planes[white][i] == 255 and tl.dominant(planes, i) == white for i in land_ice) and record['permanentWhite']['samples'] > 0
    # The encoded terrain: encode with the C++ encoder and the tracked template, then the seam checker on every plane (periodic too).
    work = tempfile.mkdtemp(prefix='mmr255-', dir=os.path.join(REPO, 'build'))
    try:
        local = os.path.join(work, 'template')
        shutil.copytree(template, local)
        scene = os.path.join(local, 'scene.xscene')
        text = open(scene, encoding='utf-8').read().replace('<scene name="mnbl_runtime_surface"', '<scene name="mnbl_world_layers_check"', 1)
        open(scene, 'w', encoding='utf-8', newline='\n').write(text)
        out = os.path.join(work, 'surface')
        cmd = [bridge, '--surface-heightfield', os.path.join(terrain, 'heightfield.bin'), '--surface-template', local, '--surface-output', out,
               '--surface-extent', repr(float(load(os.path.join(terrain, 'projection.json'))['periodicLayout']['physicalWidth'])), '--surface-scale', '1',
               '--surface-weights', os.path.join(terrain, 'weights.bin'), '--surface-normals', 'smoothed', '--surface-flora', os.path.join(terrain, 'flora.bin')]
        run = subprocess.run(cmd, capture_output=True, text=True)
        checks['encoderAccepts'] = run.returncode == 0
        seams = None
        if run.returncode == 0:
            report = os.path.join(work, 'seams.json')
            sr = subprocess.run([sys.executable, '-I', os.path.join(REPO, 'tools', 'check_terrain_seams.py'), out, report, '--period', str(period)], capture_output=True, text=True)
            seams = load(report)
            checks['encodedSeamsEveryPlane'] = sr.returncode == 0 and seams['passed'] == seams['total']
            surface = load(os.path.join(out, 'surface.json'))
            with open(os.path.join(out, 'flora.bin'), 'rb') as f:
                encoded_flora = f.read()
            with open(os.path.join(terrain, 'flora.bin'), 'rb') as f:
                flora = f.read()
            checks['encodedFloraIsTheBundles'] = encoded_flora == flora and surface['floraInstances'] == record['flora']['trees']
            nodes = encoded_nodes(out, terrain, side, [4, 4])
            checks['encodedNodesEqualCompiled'] = nodes['nodeContentEqualsCompiled'] and nodes['sharedBordersEqual']
            masks_per_node = sorted(bin(int(n.split('layer_is_used_mask_summer="')[1].split('"')[0])).count('1') for n in open(os.path.join(out, 'scene.xscene'), encoding='utf-8').read().split('<node ')[1:])
        else:
            masks_per_node = []
            checks['encodedSeamsEveryPlane'] = checks['encodedFloraIsTheBundles'] = checks['encodedNodesEqualCompiled'] = False
    finally:
        shutil.rmtree(work, ignore_errors=True)
    fl = record['flora']
    checks['mapTreesOnForest'] = fl['trees'] > 0 and set(fl['biomes']) <= set(materials['flora']['biomes']) and fl['trees'] <= materials['flora']['maxTrees']
    return {'bundle': os.path.basename(bundle), 'layers': names, 'planesDiffering': differing, 'seamMismatches': seam, 'seamPairs': side * (side - period),
            'dominantShare': {e['name']: round(e['coverage'], 4) for e in record['layers']}, 'targetShare': {n: round(v, 4) for n, v in zip(names, lc['target'])},
            'interior': {'samples': lc['interiorSamples'], 'maxError': lc['interiorMaxError'], 'moisturePairMaxError': lc['moisturePairMaxError']},
            'biomes': biomes, 'permanentWhite': record['permanentWhite'], 'layersPerNode': masks_per_node,
            'encodedSeams': {k: seams[k] for k in ('sharedEdgeSamples', 'periodic', 'passed', 'total')} if seams else None,
            'mapTrees': {k: v for k, v in fl.items()}, 'checks': checks, 'passed': all(checks.values())}


def layers_main(a):
    worlds = {}
    for spec in a.layers:
        name, folder = spec.split('=', 1)
        worlds[name] = layers_one(os.path.abspath(folder), a.bridge, a.template)
    policy = os.path.join(REPO, 'worldgen/policies/terrain-materials.json')
    recipe = os.path.join(REPO, 'Modules/MNBL.ProceduralWorld/SurfaceTemplate/template-recipe.json')
    summary = {
        'schema': 'mnbl.terrain-layers', 'version': 1, 'ticket': 'MMR-255',
        'generatedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'policy': {'file': rel(policy), 'sha256': sha(policy)}, 'templateRecipe': {'file': rel(recipe), 'sha256': sha(recipe)},
        'tools': {'worldbridge': sha(a.bridge)},
        'worlds': worlds,
        'passed': all(w['passed'] for w in worlds.values()),
        'reproduce': ['./tools/Build.ps1', 'C:/Users/davgo/m2/refresh-fixtures.ps1 -Root <worktree> (A\' seed 42, B\' seed 73)',
                      'python tools/scenebin/make_surface_template.py --materialize build/surface-template',
                      'python tools/summarize_terrain.py --layers A=<A bundle> --layers B=<B bundle> --out evidence/map-epic-2/terrain-layers.json'],
    }
    with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(summary, f, indent=1)
    for name, w in worlds.items():
        print(name, 'passed=%s' % w['passed'], 'failing=%s' % [k for k, v in w['checks'].items() if not v], 'trees=%d' % w['mapTrees']['trees'],
              'interiorMaxError=%.4f' % w['interior']['maxError'], 'planesDiffering=%d' % w['planesDiffering'])


if __name__ == '__main__':
    main()
