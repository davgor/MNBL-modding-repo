"""MMR-278: verify a bundle's shaped terrain against the Python reference (tools/terrain_shaping.py) and evaluate the shaping gates.

Used by tools/summarize_terrain.py --shaping NAME=BUNDLE (evidence/fwg/terrain-shaping.json). For each bundle:
  * parity: the shaped 513-sample heights re-derived in Python equal compiled/terrain/heightfield.bin (z error 0), and the 16-bit codes,
    range equal height-q16.bin; the layer compile re-derived in Python (tools/terrain_layers.py, MMR-255) on the shaped heights equals
    weights.bin v2 plane for plane, and its record equals shaping.json layers / layerCompile; the Python fine compile's masks equal masks.bin;
  * metrics recomputed from the files (slopes, creases, spikes, pads, lakes, the nav/visual coast mismatch) equal shaping.json;
  * gates (worldgen/policies/terrain-shaping.json gates): interior slope p95/max, coast p95, isolated spikes, steep pads, lakes newly
    hidden, seam pairs identical, the coast_sign rule, and travel classes byte-identical to the pre-smoothing compile (worldbridge
    --compile-travel at the pre-epic verticalScale 0.01 with mountainSlope and slopeRock scaled back);
  * the polar land share (RT-19): land cells by east-west stretch band.
"""
import hashlib
import json
import math
import os
import shutil
import struct
import subprocess
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import terrain_layers as tl  # noqa: E402
import terrain_shaping as ts  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def read(path):
    with open(path, 'rb') as f:
        return f.read()


def sha(data):
    return hashlib.sha256(data).hexdigest()


def close(a, b, tol=1e-9):
    return abs(a - b) <= tol * max(1.0, abs(b))


def pre_smoothing_cells(bundle, bridge, work):
    """cells.bin from worldbridge --compile-travel on the bundle's split with the pre-epic vertical scale (0.01) and mountainSlope and
    slopeRock scaled back by the same factor."""
    pol = os.path.join(bundle, 'policies')
    proj = ts.load(os.path.join(pol, 'projection.json'))
    factor = 0.01 / proj['verticalScale']
    os.makedirs(os.path.join(work, 'policies'))
    for cal in ('axis-sea-datum', 'naval-transitions'):
        src = os.path.join(pol, 'calibration', cal + '.json')
        dst = os.path.join(work, 'calibration', cal, ('calibration.json' if cal == 'axis-sea-datum' else 'naval-transitions.json'))
        os.makedirs(os.path.dirname(dst))
        shutil.copyfile(src, dst)

    def variant(name, edits):
        text = read(os.path.join(pol, name + '.json')).decode('utf-8')
        for a, b in edits:
            assert text.count(a) == 1, (name, a)
            text = text.replace(a, b)
        out = os.path.join(work, 'policies', name + '.json')
        with open(out, 'w', encoding='utf-8', newline='') as f:
            f.write(text)
        return out
    play = ts.load(os.path.join(pol, 'playability.json'))
    mats = ts.load(os.path.join(pol, 'terrain-materials.json'))
    ms = play['cells']['mountainSlope']
    sr = mats['slopeRock']
    fmt = lambda v: repr(float(v)).rstrip('0').rstrip('.') if '.' in repr(float(v)) else repr(v)
    projection = variant('projection', [('"verticalScale": %s' % fmt(proj['verticalScale']), '"verticalScale": 0.01')])
    playability = variant('playability', [('"mountainSlope": %s' % fmt(ms), '"mountainSlope": %s' % fmt(ms * factor))])
    materials = variant('terrain-materials', [('"slopeRock": {"start": %s, "full": %s}' % (fmt(sr['start']), fmt(sr['full'])),
                                               '"slopeRock": {"start": %s, "full": %s}' % (fmt(sr['start'] * factor), fmt(sr['full'] * factor)))])
    out = os.path.join(work, 'travel')
    args = [bridge, '--compile-travel', os.path.join(bundle, 'source'), '--contract', os.path.join(pol, 'source-compatibility.json'),
            '--projection-policy', projection, '--materials', materials, '--import', os.path.join(pol, 'terrain-import.json'),
            '--content', os.path.join(pol, 'campaign-content.json'), '--cultures', os.path.join(pol, 'culture-presentations.json'),
            '--playability', playability, '--population', os.path.join(pol, 'population.json'), '--out', out]
    run = subprocess.run(args, capture_output=True, text=True)
    if run.returncode not in (0, 3):
        raise RuntimeError('pre-smoothing --compile-travel failed: ' + run.stderr[-2000:])
    return read(os.path.join(out, 'cells.bin')), {'verticalScale': 0.01, 'mountainSlope': ms * factor, 'slopeRock': [sr['start'] * factor, sr['full'] * factor]}


def verify(bundle, bridge):
    pol = os.path.join(bundle, 'policies')
    terrain = os.path.join(bundle, 'compiled', 'terrain')
    materials = ts.load(os.path.join(pol, 'terrain-materials.json'))
    shaping = ts.load(os.path.join(pol, 'terrain-shaping.json'))
    projection_policy = ts.load(os.path.join(pol, 'projection.json'))
    projection_doc = ts.load(os.path.join(terrain, 'projection.json'))
    record = ts.load(os.path.join(terrain, 'shaping.json'))
    world = ts.load(os.path.join(bundle, 'source', 'world.json'))
    graph = ts.load(os.path.join(bundle, 'compiled', 'travel', 'travel-graph.json'))
    nav_side, nav_z = ts.read_heightfield(read(os.path.join(terrain, 'navigation-heightfield.bin')))
    nav_intent = ts.read_plane(read(os.path.join(terrain, 'navigation-masks.bin')), b'MNBLMK01', nav_side, 4, 1)
    side, fine_z = ts.read_heightfield(read(os.path.join(terrain, 'heightfield.bin')))
    masks = read(os.path.join(terrain, 'masks.bin'))
    fine_biome, fine_intent, fine_landform, fine_padding = (ts.read_plane(masks, b'MNBLMK01', side, 4, k) for k in range(4))
    qb = read(os.path.join(terrain, 'height-q16.bin'))
    wb = read(os.path.join(terrain, 'weights.bin'))
    c = ts.Ctx(materials, shaping, projection_doc, nav_side, side)
    N = side * side
    checks = {}

    # The fine compile (unshaped), independently in Python.
    fine = ts.compile_side(world, projection_policy, materials, side)
    checks['fineMasksRecomputed'] = fine['biome'] == fine_biome and fine['intent'] == fine_intent and fine['landform'] == fine_landform and fine['padding'] == fine_padding
    coarse = ts.compile_side(world, projection_policy, materials, nav_side)
    checks['navigationFieldRecomputed'] = max(abs(a - b) for a, b in zip(coarse['z'], nav_z)) <= 1e-9 and coarse['intent'] == nav_intent
    fixed_ok = True
    for r in range(side):
        if 2 * c.rn < r < 2 * c.rs:
            continue
        for col in range(side):
            fixed_ok = fixed_ok and abs(fine['z'][r * side + col] - fine_z[r * side + col]) <= 1e-9
    checks['poleAndPaddingRowsAsCompiled'] = fixed_ok
    pole_values = [len(set(fine_z[r * side:(r + 1) * side])) for r in (2 * c.rn, 2 * c.rs)]

    source = ts.LakeSource(world['config']['size'], [int(v) for row in world['layers']['water_type'] for v in row],
                           [float(v) for row in world['layers']['water_surface'] for v in row], projection_doc['vertical']['datumM'],
                           projection_doc['vertical']['worldUnitsPerMetre'], projection_doc['centralMeridianDeg'])
    hmin0, hmax0, step0, q0 = ts.quantize(fine['z'])
    before, _ = ts.lake_rule([hmin0 + v * step0 for v in q0], fine_intent, side, c.spf, c.W, c.strip_units, c.margin_units, c.H, source)
    before_shown = [l['shown'] for l in before]

    excluded = {e['id'] for e in graph['exclusions']}
    pads = [(s['id'], s['kind'], s['point']['x'], s['point']['y']) for s in graph['settlements'] if s.get('cell') is not None and s['id'] not in excluded]
    phys, (hmin, hmax, step, q), rec = ts.shape(c, nav_z, nav_intent, fine_intent, fine_z, pads, source, before_shown)

    # Parity with the bundle.
    z_error = max(abs(a - b) for a, b in zip(phys, fine_z))
    codes = [(qb[28 + 2 * i] << 8) | qb[29 + 2 * i] for i in range(N)]
    qhmin, qhmax = struct.unpack_from('<dd', qb, 12)
    checks['heightsZErrorZero'] = z_error == 0
    checks['codesAndRangeEqual'] = codes == q and qhmin == hmin and qhmax == hmax
    # The layer compile on the shaped surface (MMR-255), in Python.
    wside, wnames, wplanes = tl.read_weights(wb)
    clim = tl.climate(world, fine['nearest'], materials['permanentWhite']['snowThreshold'])
    lay = tl.compile_layers(materials, side, c.spf, c.PF, phys, fine_biome, fine_intent, fine_landform, fine_padding, clim)
    differing = sum(1 for a, b in zip(lay['planes'], wplanes) for x, y in zip(a, b) if x != y)
    checks['layerWeightsEqual'] = wside == side and wnames == lay['names'] and differing == 0
    lr = lay['record']
    rc = record['layerCompile']
    checks['layerRecordEqual'] = (rc['landSamples'] == lr['land'] and rc['interiorSamples'] == lr['interior'] and close(rc['interiorMaxError'], lr['interiorMaxError'])
                                  and close(rc['moisturePairMaxError'], lr['moisturePairMaxError']) and all(close(a, b) for a, b in zip(rc['target'], lr['target']))
                                  and all(close(e['coverage'], lr['dominant'][e['slot']]) for e in record['layers'])
                                  and record['permanentWhite'] == {'samples': lr['permanentWhite']['samples'], 'biomeSamples': lr['permanentWhite']['biome'],
                                                                   'seasonSamples': lr['permanentWhite']['season']})
    sh = record['shaping']
    checks['sweepsAndChangesEqual'] = sh['sweeps'] == rec['sweeps'] and sh['samplesChanged'] == rec['samplesChanged'] and sh['residual'] == rec['residual']

    # Metrics recomputed from the files.
    i0, c0, k0 = ts.faces(nav_z, nav_intent, c, nav_side, c.sp, c.strip, c.P, c.rn, c.rs)
    i1, c1, k1 = ts.faces(fine_z, fine_intent, c, side, c.spf, c.strip_f, c.PF, 2 * c.rn, 2 * c.rs)
    metrics = {'interiorSlope': {'before': ts.stats(i0), 'after': ts.stats(i1)}, 'coastSlope': {'before': ts.stats(c0), 'after': ts.stats(c1)},
               'crease': {'before': ts.stats(k0), 'after': ts.stats(k1)}}
    same = True
    for key, v in metrics.items():
        for when in ('before', 'after'):
            for m in ('p95', 'max'):
                same = same and close(v[when][m], sh[key][when][m])
            same = same and v[when]['faces'] == sh[key][when]['faces']
    t = math.tan(ts.radians(shaping['despike']['maxSlopeDeg']))
    spikes_after = ts.spikes(fine_z, fine_intent, rec['padF'], c, side, c.spf, c.strip_f, c.PF, 2 * c.rn, 2 * c.rs, t)
    spikes_before = ts.spikes(nav_z, nav_intent, None, c, nav_side, c.sp, c.strip, c.P, c.rn, c.rs, t)
    same = same and spikes_after == sh['isolatedSpikes'] and spikes_before == sh['isolatedSpikesBefore']
    slopes = []
    for _id, _kind, x, y in rec['pads_sorted']:
        gx, gy = (x + c.strip_units) / c.spf, (y + c.margin_units) / c.spf
        ix, iy = min(int(gx), side - 2), min(int(gy), side - 2)
        u, v = gx - ix, gy - iy
        Z = lambda rr, cc: fine_z[rr * side + cc]
        if u >= v:
            dx, dy = Z(iy, ix + 1) - Z(iy, ix), Z(iy + 1, ix + 1) - Z(iy, ix + 1)
        else:
            dx, dy = Z(iy + 1, ix + 1) - Z(iy + 1, ix), Z(iy + 1, ix) - Z(iy, ix)
        a, b = dx / c.spf, dy / c.spf
        slopes.append(ts.degrees(math.atan(math.sqrt(a * a + b * b))))
    over = sum(1 for s in slopes if s > shaping['gates']['steepPadDeg'])
    same = same and over == sh['pads']['over25'] and rec['pads'] == sh['pads']['count'] and rec['merged'] == sh['pads']['merged']
    after = rec['after']
    shown_after = sum(1 for l in after if l['shown'])
    newly = sum(1 for b, l in zip(before_shown, after) if b and not l['shown'])
    same = same and shown_after == sh['lakes']['shownAfter'] and sum(before_shown) == sh['lakes']['shownBefore'] and newly == sh['lakes']['newlyHidden'] and len(after) == sh['lakes']['groups']
    for l, r in zip(after, sh['lakes']['records']):
        same = same and l['shown'] == r['shownAfter'] and l['samples'] == r['samples'] and (not l['matched'] or close(l['z'], r['planeZ']))
    cells = read(os.path.join(bundle, 'compiled', 'travel', 'cells.bin'))
    grid = graph['grid']
    cols, rows = struct.unpack_from('<II', cells, 8)
    mismatch = checked = 0
    for r in range(rows):
        for col in range(cols):
            cls = cells[16 + r * cols + col]
            if cls > 2:
                continue
            centre = (2 * (grid['rowOffset'] + r) + 1) * side + 2 * (grid['columnOffset'] + col) + 1
            checked += 1
            mismatch += (fine_z[centre] >= 0) if cls == 2 else (fine_z[centre] <= 0)
    same = same and mismatch == sh['navCoastMismatch'] and checked == sh['navCellsChecked']
    checks['metricsEqualRecord'] = same

    # Seams, coast sign.
    period = c.PF
    seam = True
    planes = [fine_z, codes, fine_biome, fine_intent, fine_landform] + [list(p) for p in wplanes]
    for r in range(side):
        for col in range(side - period):
            a, b = r * side + col, r * side + col + period
            seam = seam and all(p[a] == p[b] for p in planes)
    imp = ts.load(os.path.join(pol, 'terrain-import.json'))
    trunc = imp['height']['engineVertexTruncation']
    sign = all(((qhmin + v * step) <= -trunc) if it == c.ocean else ((qhmin + v * step) >= trunc) if c.is_land(it) else True for v, it in zip(codes, fine_intent))

    # Travel classes against the pre-smoothing compile.
    work = tempfile.mkdtemp(prefix='mmr278-', dir=os.path.join(REPO, 'build'))
    try:
        pre, pre_policy = pre_smoothing_cells(bundle, bridge, work)
    finally:
        shutil.rmtree(work, ignore_errors=True)
    travel_identical = pre == cells

    # Polar land share (RT-19): land cells (Land, Mountain) by the east-west stretch 1/cos(latitude) of their row.
    land = [0, 0, 0, 0]
    for r in range(rows):
        lat = 90.0 - 180.0 * (r + 0.5) / rows
        stretch = 1.0 / math.cos(ts.radians(lat))
        n = sum(1 for col in range(cols) if cells[16 + r * cols + col] in (0, 1))
        land[0] += n
        for k, bound in enumerate((2.0, 3.0, 6.0)):
            if stretch >= bound:
                land[k + 1] += n
    polar = {'landCells': land[0], 'stretchAtLeast2': land[1] / land[0], 'stretchAtLeast3': land[2] / land[0], 'stretchAtLeast6': land[3] / land[0]}

    g = shaping['gates']
    gates = {
        'interiorSlopeP95': {'value': metrics['interiorSlope']['after']['p95'], 'limit': g['interiorSlopeP95Deg'], 'passed': metrics['interiorSlope']['after']['p95'] <= g['interiorSlopeP95Deg']},
        'interiorSlopeMax': {'value': metrics['interiorSlope']['after']['max'], 'limit': g['interiorSlopeMaxDeg'], 'passed': metrics['interiorSlope']['after']['max'] <= g['interiorSlopeMaxDeg']},
        'coastSlopeP95': {'value': metrics['coastSlope']['after']['p95'], 'limit': g['coastSlopeP95Deg'], 'passed': metrics['coastSlope']['after']['p95'] <= g['coastSlopeP95Deg']},
        'isolatedSpikes': {'value': spikes_after, 'limit': g['isolatedSpikes'], 'passed': spikes_after <= g['isolatedSpikes']},
        'steepPads': {'value': over, 'limit': g['steepPadsMax'], 'overDeg': g['steepPadDeg'], 'passed': over <= g['steepPadsMax']},
        'travelClassesIdentical': {'value': travel_identical, 'preSmoothingPolicy': pre_policy, 'passed': travel_identical},
        'noLakeNewlyHidden': {'value': newly, 'passed': newly == 0},
        'seamPairsIdentical': {'value': seam, 'pairs': side * (side - period), 'planes': 5 + len(wplanes), 'passed': seam},
        'coastSign': {'value': sign, 'engineSignViolations': record['surface']['engineSignViolations'], 'passed': sign and record['surface']['engineSignViolations'] == 0},
        'pythonParity': {'zError': z_error, 'passed': all(checks.values())},
    }
    lakes = {'groups': len(after), 'shownBefore': sum(before_shown), 'shownAfter': shown_after, 'newlyHidden': newly,
             'hiddenAfter': [{'index': l['index'], 'firstSample': [l['firstColumn'], l['firstRow']], 'samples': l['samples'], 'levelZ': l['levelZ'], 'planeZ': l['z'],
                              'bedMaxZ': l['bedMaxZ'], 'shoreMinZ': l['shoreMinZ']} for l in after if not l['shown']]}
    return {'bundle': os.path.basename(bundle), 'worldContentId': os.path.basename(bundle), 'shapingRecordSha256': sha(read(os.path.join(terrain, 'shaping.json'))),
            'side': side, 'navigationSide': nav_side, 'metrics': metrics, 'isolatedSpikes': {'before': spikes_before, 'after': spikes_after},
            'pads': {'count': rec['pads'], 'merged': rec['merged'], 'over': over, 'maxSlope': max(slopes) if slopes else 0.0},
            'lakes': lakes, 'navCoastMismatch': {'after': mismatch, 'before': sh['navCoastMismatchBefore'], 'cells': checked},
            'polarLandShare': polar, 'poleRowDistinctValues': pole_values,
            'layers': {'names': lay['names'], 'planesDiffering': differing, 'record': lr, 'flora': record['flora']},
            'checks': checks, 'gates': gates, 'passed': all(v['passed'] for v in gates.values())}
