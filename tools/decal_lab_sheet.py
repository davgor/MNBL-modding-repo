"""MMR-247 (D): the decal lab's sheet (map2_acceptance phase decallab, step w1.decalLab; docs/map-epic-2-acceptance.md).

Reads the probe's result JSON, finds the lab's screenshots (collected beside it as map2_acceptance-<pid>-shot-<label>.bmp, else at the path the
probe recorded) and, for each spot:
  * labels every lab decal at its recorded viewport point on the close, map and far shots (PNG copies with the labels drawn);
  * measures, at the map distance, the mean absolute difference between the shot with the lab drawn and the shot with it hidden in a window
    around each decal's point (the same camera): a decal that draws changes its window, one that draws nothing does not;
  * writes <out>/decal-lab.json (per spot and decal: the point, the difference, drawn yes / no at the threshold) and one contact sheet per
    spot, and prints the grid (columns x rows) of yes / no.
Viewport points are taken as x, y in [0, 1] (or NDC [-1, 1]); the vertical sense is read from the grid itself (rows further north must sit higher
on the screen at bearing 0). Pillow only.

MMR-291: the overlay mesh grid of each spot (columns mesh:vein, mesh:river, mesh:magicWater; shots lab-<spot>-mesh-<view>) is read the same way,
as spot '<spot>-mesh'. MMR-291-W: its rows are the lab's meshRows (the passes against the water: blend, depth, under, forward, afterPostfx), and
the report names the policy's pass per layer (meshPasses, from the lab's overlayMeshes record).

Usage: python tools/decal_lab_sheet.py <map2_acceptance-<pid>.json> [--out <dir>] [--threshold 6] [--window 0.012]
"""
import argparse
import json
import os
import sys

from PIL import Image, ImageChops, ImageDraw, ImageStat


def shot_path(result_path, pid, label, recorded):
    collected = os.path.join(os.path.dirname(result_path), 'map2_acceptance-%s-shot-%s.bmp' % (pid, label))
    if os.path.exists(collected):
        return collected
    return recorded if recorded and os.path.exists(recorded) else None


def to_pixels(points, layout, size):
    """Viewport points -> pixel positions; the vertical sense from the grid (a decal further north sits higher at bearing 0)."""
    if not points:
        return {}
    vals = [v for p in points.values() for v in p[:2]]
    ndc = min(vals) < -0.05
    norm = {k: (((p[0] + 1) / 2, (p[1] + 1) / 2) if ndc else (p[0], p[1])) for k, p in points.items()}
    north = {d['id']: d['offset'][1] for d in layout}
    pairs = [(north[k], v[1]) for k, v in norm.items() if k in north]
    flip = False
    if len(pairs) > 1:
        pairs.sort()
        flip = pairs[-1][1] > pairs[0][1]   # y grows northward: screen y is 1 - v
    w, h = size
    return {k: (v[0] * w, ((1 - v[1]) if flip else v[1]) * h) for k, v in norm.items()}


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument('result')
    ap.add_argument('--out', default=None)
    ap.add_argument('--threshold', type=float, default=6.0, help='mean |diff| (0..255) over the window above which a decal counts as drawn')
    ap.add_argument('--window', type=float, default=0.012, help='half window as a share of the shot width')
    args = ap.parse_args(argv)
    doc = json.load(open(args.result, encoding='utf-8-sig'))
    pid = doc.get('processId')
    out = args.out or os.path.join(os.path.dirname(os.path.abspath(args.result)), 'decal-lab')
    os.makedirs(out, exist_ok=True)
    outputs = doc.get('outputs') or {}
    lab = (outputs.get('steps') or {}).get('w1.decalLab')
    if not lab:
        print('no w1.decalLab step in', args.result)
        return 1
    layout = lab.get('layout') or []
    columns, rows = lab.get('columns') or [], lab.get('rows') or []
    mesh_rows = lab.get('meshRows') or rows
    shots = {s.get('label'): s for s in outputs.get('shots') or [] if s.get('step') == 'w1.decalLab'}
    report = {'result': os.path.abspath(args.result), 'status': lab.get('status'), 'threshold': args.threshold, 'window': args.window, 'spots': {},
              'materials': lab.get('materials'), 'textures': lab.get('textures'), 'atlas': lab.get('atlas'),
              'meshPasses': (((lab.get('overlayMeshes') or {}).get('policy') or {}).get('passes')), 'meshRows': lab.get('meshRows')}
    # MMR-291: each spot's overlay mesh grid (columns mesh:<layer>) is shot on its own, as spot '<spot>-mesh'.
    for spot in ('land', 'sea', 'land-mesh', 'sea-mesh'):
        vis, hid = shots.get('lab-%s-map' % spot), shots.get('lab-%s-hidden' % spot)
        if not vis:
            continue
        rec = {'at': (lab.get('spots') or {}).get(spot.split('-')[0]), 'decals': {}}
        report['spots'][spot] = rec
        frames = []
        for view in ('close', 'map', 'far', 'hidden'):
            s = shots.get('lab-%s-%s' % (spot, view))
            p = shot_path(args.result, pid, 'lab-%s-%s' % (spot, view), s and s.get('path'))
            if not p:
                continue
            im = Image.open(p).convert('RGB')
            pts = to_pixels((s or {}).get('viewport') or {}, layout, im.size)
            if view != 'hidden':
                dr = ImageDraw.Draw(im)
                for k, (x, y) in pts.items():
                    dr.rectangle((x - 2, y - 2, x + 2, y + 2), outline=(255, 0, 255))
                    dr.text((x + 4, y - 6), k.replace('/', '\n'), fill=(255, 255, 0))
            im.save(os.path.join(out, 'lab-%s-%s.png' % (spot, view)))
            frames.append((view, im))
        pv = shot_path(args.result, pid, 'lab-%s-map' % spot, vis.get('path'))
        ph = shot_path(args.result, pid, 'lab-%s-hidden' % spot, hid and hid.get('path'))
        if pv and ph:
            a, b = Image.open(pv).convert('RGB'), Image.open(ph).convert('RGB')
            diff = ImageChops.difference(a, b).convert('L')
            pts = to_pixels(vis.get('viewport') or {}, layout, a.size)
            r = args.window * a.size[0]
            for k, (x, y) in pts.items():
                box = (int(max(0, x - r)), int(max(0, y - r)), int(min(a.size[0], x + r)), int(min(a.size[1], y + r)))
                if box[2] <= box[0] or box[3] <= box[1]:
                    rec['decals'][k] = {'point': [x, y], 'offscreen': True}
                    continue
                m = ImageStat.Stat(diff.crop(box)).mean[0]
                rec['decals'][k] = {'point': [round(x, 1), round(y, 1)], 'meanDiff': round(m, 2), 'drawn': m >= args.threshold}
        if frames:
            w = 800
            sheet = Image.new('RGB', (w * 2, int(w * frames[0][1].size[1] / frames[0][1].size[0]) * ((len(frames) + 1) // 2)), (30, 30, 30))
            for i, (view, im) in enumerate(frames):
                t = im.resize((w, int(w * im.size[1] / im.size[0])))
                sheet.paste(t, ((i % 2) * w, (i // 2) * t.size[1]))
                ImageDraw.Draw(sheet).text(((i % 2) * w + 6, (i // 2) * t.size[1] + 6), '%s %s' % (spot, view), fill=(255, 255, 255))
            sheet.save(os.path.join(out, 'lab-%s-sheet.png' % spot))
        grid_rows = mesh_rows if spot.endswith('-mesh') else rows
        print('%s spot %s: drawn (meanDiff >= %.1f) per column x row' % (spot, rec['at'], args.threshold))
        print('  %-22s' % '' + ''.join('%-12s' % r for r in grid_rows))
        for c in columns:
            if not any((c + '/' + r_) in rec['decals'] for r_ in grid_rows):
                continue  # a column of the other grid (decal vs mesh)
            cells = []
            for r_ in grid_rows:
                d = rec['decals'].get(c + '/' + r_)
                cells.append('-' if d is None or d.get('offscreen') else ('yes' if d['drawn'] else 'no') + '' )
            print('  %-22s' % c + ''.join('%-12s' % x for x in cells))
    json.dump(report, open(os.path.join(out, 'decal-lab.json'), 'w', encoding='utf-8'), indent=1)
    print('decal lab sheet:', out)
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
