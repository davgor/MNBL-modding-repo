"""MMR-37: check an encoded terrain.bin (ZGR6RTRN, 4x4 nodes) for chunk seam tears and against the heights the encoder published.

Usage: python tools/check_terrain_seams.py SURFACE_DIR [OUT_JSON] [--period COLUMNS]
SURFACE_DIR is a surface encoder output (terrain.bin, scene.xscene, quantized-heightfield.bin, surface.json). The checks:
  * every node decodes heights, normals and weights at (cells+1)^2 samples;
  * the shared edge samples of horizontally and vertically adjacent nodes (corners included) have the same 16-bit height, the same
    normal RG bytes and the same weight in every layer (a layer a node does not carry counts as 0 there);
  * each node's scene.xscene layer masks name exactly the layers its WGHT record carries;
  * every decoded height is the quantized height the encoder published (quantized-heightfield.bin: lo + q * step), so the native
    geometry is the backend's model;
  * each node's scene.xscene min/max height equals its decoded extrema;
  * with --period (the bundle's seamPeriodColumns, MMR-255): the periodic seam columns c and c + period of the decoded grid carry the same
    height, the same weight in every material layer plane and the same physical material (the strips copy the other side of the seam);
  * the node's PHYM (four seasons, x-major) is the same in every season.
Exit status 0 when every check passes.
"""
import json
import math
import os
import struct
import sys
import xml.etree.ElementTree as ET

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), 'scenebin'))
import terrain_bin  # noqa: E402  (standard library only)


def read_heightfield(path):
    data = open(path, 'rb').read()
    assert data[:8] == b'MNBLHF01', 'not MNBLHF01'
    side = struct.unpack_from('<I', data, 8)[0]
    return side, struct.unpack_from('<%dd' % (side * side), data, 12)


def main():
    args = sys.argv[1:]
    period = None
    if '--period' in args:
        at = args.index('--period')
        period = int(args[at + 1])
        del args[at:at + 2]
    folder = args[0]
    out_path = args[1] if len(args) > 1 else None
    surface = json.load(open(os.path.join(folder, 'surface.json'), encoding='utf-8'))
    side, quantized = read_heightfield(os.path.join(folder, 'quantized-heightfield.bin'))
    lo, step = surface['minHeight'], surface['quantizationStep']
    nodes_x, nodes_y = surface['nodes']
    cells = (side - 1) // nodes_x
    R = cells + 1
    model = terrain_bin.parse(open(os.path.join(folder, 'terrain.bin'), 'rb').read())
    records = model.nodes()
    xml_nodes = {(int(n.get('idx')), int(n.get('idy'))): n for n in ET.parse(os.path.join(folder, 'scene.xscene')).getroot().iter('node')}
    checks = []

    def check(ok, label):
        checks.append({'ok': bool(ok), 'label': label})

    check(len(records) == nodes_x * nodes_y and len(xml_nodes) == nodes_x * nodes_y, 'terrain.bin and scene.xscene hold %d x %d nodes (%d records, %d xml)' % (nodes_x, nodes_y, len(records), len(xml_nodes)))
    decoded = {}
    physics = {}
    for index, record in enumerate(records):
        ix, iy = index % nodes_x, index // nodes_x
        w, h, ch, heights = terrain_bin.png_decode(record.height_png)
        nw, nh, nch, normals = terrain_bin.png_decode(record.normal_png)
        layers = {}
        for slot, png in enumerate(record.weights):
            if png is None:
                continue
            ww, wh, wch, values = terrain_bin.png_decode(png)
            assert (ww, wh, wch) == (R, R, 1), 'weight layer size'
            layers[slot] = values
        assert (w, h, ch) == (R, R, 1) and (nw, nh, nch) == (R, R, 4), 'node %d,%d sample size' % (ix, iy)
        seasons = [bytes(m) for _, m in record.physics]
        decoded[(ix, iy)] = (heights, normals, layers)
        physics[(ix, iy)] = seasons
    check(len(decoded) == nodes_x * nodes_y, 'every node decodes heights, normals and weights at %d x %d samples' % (R, R))

    # Shared edges: node (ix,iy) sample (r,c) is grid (iy*cells + r, ix*cells + c).
    def sample(ix, iy, r, c):
        heights, normals, layers = decoded[(ix, iy)]
        i = r * R + c
        return heights[i], (normals[4 * i], normals[4 * i + 1]), {s: v[i] for s, v in layers.items()}

    edges = mismatched_height = mismatched_normal = mismatched_weight = 0
    examples = []
    for (ix, iy) in sorted(decoded):
        for dx, dy in ((1, 0), (0, 1), (1, 1)):
            jx, jy = ix + dx, iy + dy
            if (jx, jy) not in decoded:
                continue
            if (dx, dy) == (1, 0):
                pairs = [((r, cells), (r, 0)) for r in range(R)]
            elif (dx, dy) == (0, 1):
                pairs = [((cells, c), (0, c)) for c in range(R)]
            else:
                pairs = [((cells, cells), (0, 0))]
            for a, b in pairs:
                edges += 1
                ha, na, wa = sample(ix, iy, *a)
                hb, nb, wb = sample(jx, jy, *b)
                slots = set(wa) | set(wb)
                weights_equal = all(wa.get(s, 0) == wb.get(s, 0) for s in slots)
                if ha != hb:
                    mismatched_height += 1
                if na != nb:
                    mismatched_normal += 1
                if not weights_equal:
                    mismatched_weight += 1
                if (ha != hb or na != nb or not weights_equal) and len(examples) < 5:
                    examples.append({'nodes': [[ix, iy], [jx, jy]], 'samples': [a, b], 'heights': [ha, hb], 'normals': [na, nb], 'weights': [wa, wb]})
    check(edges > 0 and mismatched_height == 0, 'all %d shared node-edge samples have the same 16-bit height (%d differ)' % (edges, mismatched_height))
    check(edges > 0 and mismatched_normal == 0, 'all %d shared node-edge samples have the same normal (%d differ)' % (edges, mismatched_normal))
    check(edges > 0 and mismatched_weight == 0, 'all %d shared node-edge samples have the same weight in every material layer (%d differ)' % (edges, mismatched_weight))

    mask_problems = []
    extrema_problems = []
    height_errors = 0
    max_error = 0.0
    for (ix, iy), (heights, normals, layers) in decoded.items():
        node = xml_nodes[(ix, iy)]
        expected_mask = sum(1 << s for s in layers)
        for season in ('summer', 'fall', 'winter', 'spring'):
            if int(node.get('layer_is_used_mask_' + season)) != expected_mask:
                mask_problems.append('%d,%d %s' % (ix, iy, season))
        values = []
        for r in range(R):
            for c in range(R):
                q = heights[r * R + c]
                z = lo + q * step
                ref = quantized[(iy * cells + r) * side + (ix * cells + c)]
                err = abs(z - ref)
                max_error = max(max_error, err)
                if err > 1e-9:
                    height_errors += 1
                values.append(z)
        if abs(float(node.get('min_height')) - min(values)) > 0.0015 or abs(float(node.get('max_height')) - max(values)) > 0.0015:
            extrema_problems.append('%d,%d' % (ix, iy))
    check(not mask_problems, 'every node\'s layer masks name exactly the material layers it carries (%s)' % (', '.join(mask_problems) or 'all %d nodes' % len(decoded)))
    check(height_errors == 0, 'every decoded height equals the published quantized heightfield (%d samples, max error %.3g)' % (len(decoded) * R * R, max_error))
    check(not extrema_problems, 'each node\'s scene.xscene min/max height equals its decoded extrema at XML precision (%s)' % (', '.join(extrema_problems) or 'all nodes'))
    check(all(len(set(s)) == 1 for s in physics.values()), 'every node\'s physical materials are the same in all four seasons')
    periodic = None
    if period is not None:
        # Grid sample (gr, gc) -> node (gc // cells clamped, gr // cells clamped), local (gr - iy*cells, gc - ix*cells).
        def grid_sample(gr, gc):
            ix, iy = min(gc // cells, nodes_x - 1), min(gr // cells, nodes_y - 1)
            r, c = gr - iy * cells, gc - ix * cells
            heights, _normals, layers = decoded[(ix, iy)]
            i = r * R + c
            phys = physics[(ix, iy)][0][c * R + r]
            return heights[i], {s: v[i] for s, v in layers.items()}, phys
        pairs = bad_h = bad_w = bad_p = 0
        planes = set()
        for gr in range(side):
            for gc in range(side - period):
                ha, wa, pa = grid_sample(gr, gc)
                hb, wb, pb = grid_sample(gr, gc + period)
                pairs += 1
                slots = set(wa) | set(wb)
                planes |= slots
                bad_h += ha != hb
                bad_w += any(wa.get(s, 0) != wb.get(s, 0) for s in slots)
                bad_p += pa != pb
        periodic = {'period': period, 'pairs': pairs, 'planes': sorted(planes), 'heightMismatches': bad_h, 'weightMismatches': bad_w, 'physicsMismatches': bad_p}
        check(pairs > 0 and bad_h == 0, 'periodic seam: all %d sample pairs c, c + %d have the same 16-bit height (%d differ)' % (pairs, period, bad_h))
        check(pairs > 0 and bad_w == 0, 'periodic seam: all %d sample pairs have the same weight in every one of the %d material layer planes present (%d differ)' % (pairs, len(planes), bad_w))
        check(pairs > 0 and bad_p == 0, 'periodic seam: all %d sample pairs have the same physical material (%d differ)' % (pairs, bad_p))
    summary = {'schema': 'mnbl.evidence.terrain-seams', 'version': 1, 'ticket': 'MMR-37', 'surface': folder.replace('\\', '/'), 'sceneName': surface.get('sceneName'),
               'nodes': [nodes_x, nodes_y], 'cellsPerNode': cells, 'gridSide': side, 'sharedEdgeSamples': edges, 'examples': examples, 'periodic': periodic,
               'checks': checks, 'passed': sum(c['ok'] for c in checks), 'total': len(checks)}
    if out_path:
        with open(out_path, 'w', encoding='utf-8', newline='\n') as fh:
            json.dump(summary, fh, indent=1)
            fh.write('\n')
    for c in checks:
        print(('ok    ' if c['ok'] else 'FAIL  ') + c['label'])
    print('%d/%d checks passed' % (summary['passed'], summary['total']))
    sys.exit(0 if summary['passed'] == summary['total'] else 1)


if __name__ == '__main__':
    main()
