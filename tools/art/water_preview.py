"""MMR-247 (cluster W): a top-down offline preview of the water decals over a world window (review only, not a gate).

Reads the dumps Test-WaterVisibility writes with -Dump (water-<world>.json: water decals with kind, atlas cell and clipped box, road decals,
lake planes, and two windows of ground and travel-cell rasters: the densest river window and the largest lake) and composites, per window:
  before   MMR-285's plan from a dump of the old code (round tinted vanilla stamps, boxes centred on the ground: the stamp also adds to the water),
  fallback this plan with the tinted vanilla stamp (what draws until mnbl_water_surface is published),
  after    this plan with the derived water atlas (tools/art/water_decal.py).
The terrain is a hillshaded tile of a vanilla ground texture, the sea and lake planes a depth ramp between the acc4 shallow and deep colours.
A decal paints a pixel through its atlas alpha times the box fade (1 - |z|^8 of the ground's place in its clipped box), as the shader does;
on water the old unclipped stamps add their colour (the water shader's decal path). Lighting is flat (factor 1) with the ripple normal's tilt.
Usage: python tools/art/water_preview.py --after DUMP_DIR --before DUMP_DIR --atlas DIR --vanilla DIR --out DIR [--world A]
"""
import argparse
import json
import math
import os

from PIL import Image, ImageDraw

PX = 8                       # pixels per map unit
SEA_SHALLOW, SEA_DEEP = (62, 98, 98), (20, 34, 56)
OLD_TINT, NEW_FALLBACK_TINT = (77, 153, 255), None


def lin(c):
    c /= 255.0
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def srgb(v):
    v = max(0.0, min(1.0, v))
    return 255.0 * (v * 12.92 if v <= 0.0031308 else 1.055 * v ** (1 / 2.4) - 0.055)


class Raster:
    def __init__(self, win):
        self.x0, self.y0, self.step, self.w, self.h = win['x0'], win['y0'], win['step'], win['w'], win['h']
        self.g, self.k = win['ground'], win['classes']

    def ground(self, x, y):
        fx, fy = (x - self.x0) / self.step, (y - self.y0) / self.step
        i, j = max(0, min(self.w - 2, int(fx))), max(0, min(self.h - 2, int(fy)))
        tx, ty = max(0.0, min(1.0, fx - i)), max(0.0, min(1.0, fy - j))
        g = self.g
        a = g[j * self.w + i] * (1 - tx) + g[j * self.w + i + 1] * tx
        b = g[(j + 1) * self.w + i] * (1 - tx) + g[(j + 1) * self.w + i + 1] * tx
        return a * (1 - ty) + b * ty


def level(lakes, x, y, g):
    lv = 0.0 if g < 0 else None
    for l in lakes:
        if l[4] > g and l[0] <= x <= l[2] and l[1] <= y <= l[3] and (lv is None or l[4] > lv):
            lv = l[4]
    return lv


def base(r, lakes, ground_tex, W, H, x0, y0):
    """Terrain (hillshaded vanilla ground tile) and water planes, per pixel: colour (linear) and the water level shown (None on land)."""
    gt = ground_tex.convert('RGB')
    tw, th = gt.size
    gpx = gt.load()
    col, wet = [None] * (W * H), [None] * (W * H)
    for py in range(H):
        for px in range(W):
            x, y = x0 + (px + .5) / PX, y0 + (py + .5) / PX
            g = r.ground(x, y)
            gx, gy = r.ground(x + .25, y) - r.ground(x - .25, y), r.ground(x, y + .25) - r.ground(x, y - .25)
            shade = max(.35, min(1.25, 1 + (-gx * .7 - gy * .7) * 1.2))
            t = gpx[int(x * 25.6) % tw, int(y * 25.6) % th]
            c = [lin(t[i]) * shade for i in range(3)]
            lv = level(lakes, x, y, g)
            if lv is not None:
                depth = lv - g
                k = 1 - math.exp(-depth * 1.2)
                c = [lin(SEA_SHALLOW[i]) * (1 - k) + lin(SEA_DEEP[i]) * k for i in range(3)]
            col[py * W + px], wet[py * W + px] = c, lv
    return col, wet


def draw(r, col, wet, decals, W, H, x0, y0, sample, clip, additive_on_water):
    """Composites decals: sample(decal, u, v) -> (linear rgb, alpha, tilt); clip: use the decal's clipped box (else a box of +-hd on the ground)."""
    out = [c[:] for c in col]
    for d in decals:
        c, s = math.cos(d['yaw']), math.sin(d['yaw'])
        ext = d['hl'] * abs(c) + d['hw'] * abs(s), d['hl'] * abs(s) + d['hw'] * abs(c)
        pxa, pxb = int((d['x'] - ext[0] - x0) * PX), int((d['x'] + ext[0] - x0) * PX) + 1
        pya, pyb = int((d['y'] - ext[1] - y0) * PX), int((d['y'] + ext[1] - y0) * PX) + 1
        if pxb < 0 or pya > H or pxa > W or pyb < 0:
            continue
        gc = r.ground(d['x'], d['y'])
        z, half = (d['z'], d['half']) if clip else (gc, d.get('hd', 3.0))
        for py in range(max(0, pya), min(H, pyb)):
            for px in range(max(0, pxa), min(W, pxb)):
                x, y = x0 + (px + .5) / PX, y0 + (py + .5) / PX
                dx, dy = x - d['x'], y - d['y']
                u, v = (dx * c + dy * s) / d['hl'], (-dx * s + dy * c) / d['hw']
                if abs(u) > 1 or abs(v) > 1:
                    continue
                k = py * W + px
                surface = wet[k]
                h = r.ground(x, y) if surface is None else surface
                zl = (h - z) / half
                if abs(zl) > 1:
                    continue
                rgb, a, tilt = sample(d, u, v)
                a *= 1 - abs(zl) ** 8
                if a <= 0:
                    continue
                if surface is not None:
                    if additive_on_water:
                        out[k] = [out[k][i] + rgb[i] * a for i in range(3)]
                    continue
                lit = 1 + tilt
                out[k] = [out[k][i] * (1 - a) + rgb[i] * lit * a for i in range(3)]
    return out


def to_image(col, W, H):
    im = Image.new('RGB', (W, H))
    im.putdata([tuple(int(round(srgb(v))) for v in c) for c in col])
    return im


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--after', required=True)
    ap.add_argument('--before', required=True)
    ap.add_argument('--atlas', required=True)
    ap.add_argument('--vanilla', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--world', default='A')
    ap.add_argument('--fallback-tint', default='77,137,216')
    args = ap.parse_args()
    after = json.load(open(os.path.join(args.after, 'water-%s.json' % args.world)))
    before = json.load(open(os.path.join(args.before, 'water-%s.json' % args.world)))
    atlas_d = Image.open(os.path.join(args.atlas, 'mnbl_water_surface_d.png')).convert('RGBA')
    atlas_n = Image.open(os.path.join(args.atlas, 'mnbl_water_surface_n.png')).convert('RGB')
    uv_d = Image.open(os.path.join(args.vanilla, 'mainmap_decal_undervillage_d.png')).convert('RGBA')
    ad, an, ud = atlas_d.load(), atlas_n.load(), uv_d.load()
    AW, UW = atlas_d.size[0], uv_d.size[0]
    tint_new = [int(t) for t in args.fallback_tint.split(',')]

    def atlas_sample(d, u, v):
        cell = d['atlas']
        tx = (cell[2] + (u + 1) / 2 * cell[0]) * AW
        ty = (cell[3] + (v + 1) / 2 * cell[1]) * AW
        p = ad[min(AW - 1, int(tx)), min(AW - 1, int(ty))]
        n = an[min(AW - 1, int(tx)), min(AW - 1, int(ty))]
        tilt = ((n[0] - 128) * .6 + (n[1] - 128) * .4) / 128 * .35
        return [lin(p[i]) for i in range(3)], p[3] / 255.0, tilt

    def stamp_sample(tint):
        def f(d, u, v):
            p = ud[min(UW - 1, int((u + 1) / 2 * UW)), min(UW - 1, int((v + 1) / 2 * UW))]
            return [lin(p[i]) * lin(tint[i]) for i in range(3)], p[3] / 255.0, 0.0
        return f

    old = [{'x': q[0], 'y': q[1], 'yaw': q[2], 'hl': q[3], 'hw': q[4], 'hd': max(3.0, q[5]) if len(q) > 5 else 3.0} for q in before['decals']]
    new = after['water']
    lakes = after['lakes']
    os.makedirs(args.out, exist_ok=True)
    names = ['river', 'lake']
    for wi, win in enumerate(after['windows']):
        r = Raster(win)
        W, H = int(win['w'] * win['step'] * PX), int(win['h'] * win['step'] * PX)
        x0, y0 = win['x0'], win['y0']
        near = [l for l in lakes if l[2] >= x0 and l[0] <= x0 + W / PX and l[3] >= y0 and l[1] <= y0 + H / PX]
        col, wet = base(r, near, uv_d.crop((UW // 4, UW // 4, 3 * UW // 4, 3 * UW // 4)), W, H, x0, y0)   # the stamp's opaque middle as a ground tile
        panels = [
            ('before (MMR-285: round tinted stamps, unclipped)', draw(r, col, wet, old, W, H, x0, y0, stamp_sample(OLD_TINT), False, True)),
            ('fallback (new shapes, tinted vanilla stamp, clipped)', draw(r, col, wet, new, W, H, x0, y0, stamp_sample(tint_new), True, False)),
            ('after (new shapes, mnbl_water_surface, clipped)', draw(r, col, wet, new, W, H, x0, y0, atlas_sample, True, False)),
        ]
        sheet = Image.new('RGB', (W, (H + 18) * len(panels)), (24, 24, 24))
        dr = ImageDraw.Draw(sheet)
        for k, (title, c) in enumerate(panels):
            sheet.paste(to_image(c, W, H), (0, k * (H + 18) + 18))
            dr.text((4, k * (H + 18) + 3), '%s %s: %s' % (args.world, names[wi] if wi < len(names) else wi, title), fill=(255, 230, 120))
        path = os.path.join(args.out, 'preview-%s-%s.png' % (args.world, names[wi] if wi < len(names) else wi))
        sheet.save(path)
        print(path)


if __name__ == '__main__':
    main()
