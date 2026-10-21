"""MMR-247 (cluster W): derive the water-surface decal atlas from vanilla textures, gate it, and write the module's asset sources.

acc4 showed MMR-285's water decals as blue gravel: a tinted earth stamp (mainmap_decal_undervillage x SetFactor1) keeps the ground's grain in
its albedo and normals. One published decal material, mnbl_water_surface (the publish request: docs/map-dressing.md "Water surface decal"),
draws river strips and fill pools as water instead: a smooth dark blue-green albedo (one colour, so overlapping stamps show no rings), a feathered alpha, a ripple
normal (the material sets do_not_blend_normals, so the terrain grain does not show through) and a gloss that catches the light.
Three 1024 x 1024 atlases, 2 x 2 cells of 512 px with a 16 px transparent margin:
  mnbl_water_surface_d  RGBA  albedo (one water colour); alpha = the stamp's falloff (import: Albedo_HQ BC7, has_alpha, dont_resize_in_atlas)
  mnbl_water_surface_n  RGB   tangent normal, DirectX style like the vanilla maps (import: Normal BC5, is_bumpmap)
  mnbl_water_surface_s  RGB   R specular, G gloss, B occlusion, the vanilla terrain and decal convention (import: Specular DXT1, is_specularmap)
Column 0 is the river strip (a capsule along u, round-ended so stamps chain round bends; across v opaque to about 0.55 of the width,
feathered to 0 at about 0.9 with an irregular edge), column 1 the pool (a round stamp with an irregular feathered rim:
fill cells and lone river cells). Rows are two variants of each, so an atlas V convention that differs from D3D only swaps variants. A
decal shows cell (column, row) with SetVectorArgument(0.5, 0.5, column / 2, row / 2).

Every pixel derives from installed vanilla textures (owner decision 4), decoded by tools/art/tpac_textures.py:
  worldmap_water_normal_map (Native world_map: the campaign map water's own normal) -> the ripple normal;
  water_noise (NavalDLC world_map: the War Sails campaign map water noise) -> the rim breakup and a faint albedo variation;
  worldmap_swamp_decal_3_s (Native world-map swamp decal) -> specular and gloss, from its puddles (its top-decile gloss pixels);
  mainmap_decal_undervillage_d (Native, the fallback stamp) -> the fallback tint and the BCn reference.
The colours are the in-game sea and lake as acc4 shot them (SEA and SHALLOW below: medians of named regions of the acc4 world-1 shots,
lighting factor taken as 1), recomputed with --shots. No Codex imagery is used.

Gates (evidence/art/water-decal.json): seams (alpha 0 in every margin), opaque core (alpha >= 0.9 inside half the half extents, the
coverage model of roads.json water.opaqueCore), chain (a run of strips at the policy's step reads continuous along its middle), reads as
water (CIEDE2000 of the core albedo against the acc4 sea <= 12 and against every acc4 ground sample >= 20), no grain (the core's luminance
spread far below the tinted stamp's), BCn round trip (DXT5 stand-in for BC7 <= the vanilla decals' own error, BC5 and DXT1 <= 1.25 x the
vanilla inputs' own), determinism (the output hashes; a second run must give the same).
Usage: python tools/art/water_decal.py --modules "<game>/Modules" --out Modules/MNBL.ProceduralWorld/AssetSources/water_surface
                                       --evidence evidence/art/water-decal.json [--review DIR] [--shots DIR] [--work DIR]
       python tools/art/water_decal.py check [--packs Modules/MNBL.ProceduralWorld/AssetPackages] [--out evidence/art/water-packs.json]
         (after the publish: the material's flags and its three textures in the module's packs; exit 2 while nothing is published)
"""
import argparse
import hashlib
import json
import math
import os
import re
import struct
import sys

from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import tpac_textures  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CELL, GRID, MARGIN = 512, 2, 16
ATLAS = CELL * GRID
INNER = CELL - 2 * MARGIN
NAME = 'mnbl_water_surface'
INPUTS = ['worldmap_water_normal_map', 'water_noise', 'worldmap_swamp_decal_3_s', 'mainmap_decal_undervillage_d']
# acc4 world-1 shots (build/acceptance/acc4/in-game/map2_acceptance-14208-shot-<name>.bmp), sRGB medians of a region (x0, y0, x1, y1).
SAMPLES = {
    'sea': [('water-coastFill', (700, 600, 1500, 680), (31, 58, 84)), ('seam-east', (100, 50, 1500, 300), (43, 61, 78))],   # close views
    'farSea': [('biome-forest', (50, 50, 500, 250), (74, 89, 106))],                                                          # hazy: recorded only
    'shallow': [('water-lake', (100, 430, 1100, 600), (62, 98, 98))],
    'ground': [('water-coastFill', (100, 450, 600, 600), (142, 129, 124)), ('water-river', (1100, 250, 1500, 450), (130, 116, 104)),
               ('water-lake', (200, 850, 700, 950), (139, 103, 64)), ('seam-east', (1100, 650, 1450, 900), (220, 205, 213))],
    'tintedStamp': [('water-river', (500, 780, 800, 900), (47, 65, 79))],
}
CORE_TOWARD_SHALLOW = 0.25   # the water colour: the sea a quarter of the way to the shallow lake
RIPPLE = 0.55                                          # the ripple normal's xy scale (a calm surface)
STEP_OVER_HALF = 1.0 / 1.5                             # a river stamp's step over its half length (MapDressing.RiverStampLength 1.5)


# ------------------------------------------------------------------ colour
def lin(c):
    c /= 255.0
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def srgb(v):
    v = max(0.0, min(1.0, v))
    return 255.0 * (v * 12.92 if v <= 0.0031308 else 1.055 * v ** (1 / 2.4) - 0.055)


def lab(rgb):
    r, g, b = (lin(c) for c in rgb)
    x = (0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047
    y = 0.2126 * r + 0.7152 * g + 0.0722 * b
    z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883
    f = lambda t: t ** (1 / 3.0) if t > 0.008856 else 7.787 * t + 16 / 116.0
    return (116 * f(y) - 16, 500 * (f(x) - f(y)), 200 * (f(y) - f(z)))


def de2000(c1, c2):
    L1, a1, b1 = lab(c1)
    L2, a2, b2 = lab(c2)
    C1, C2 = math.hypot(a1, b1), math.hypot(a2, b2)
    Cb = (C1 + C2) / 2
    G = 0.5 * (1 - math.sqrt(Cb ** 7 / (Cb ** 7 + 25 ** 7)))
    a1p, a2p = (1 + G) * a1, (1 + G) * a2
    C1p, C2p = math.hypot(a1p, b1), math.hypot(a2p, b2)
    h1p, h2p = math.degrees(math.atan2(b1, a1p)) % 360, math.degrees(math.atan2(b2, a2p)) % 360
    dLp, dCp = L2 - L1, C2p - C1p
    dhp = 0 if C1p * C2p == 0 else (h2p - h1p if abs(h2p - h1p) <= 180 else (h2p - h1p - 360 if h2p > h1p else h2p - h1p + 360))
    dHp = 2 * math.sqrt(C1p * C2p) * math.sin(math.radians(dhp / 2))
    Lbp, Cbp = (L1 + L2) / 2, (C1p + C2p) / 2
    if C1p * C2p == 0:
        hbp = h1p + h2p
    elif abs(h1p - h2p) <= 180:
        hbp = (h1p + h2p) / 2
    else:
        hbp = (h1p + h2p + 360) / 2 if h1p + h2p < 360 else (h1p + h2p - 360) / 2
    T = 1 - 0.17 * math.cos(math.radians(hbp - 30)) + 0.24 * math.cos(math.radians(2 * hbp)) + 0.32 * math.cos(math.radians(3 * hbp + 6)) - 0.20 * math.cos(math.radians(4 * hbp - 63))
    dth = 30 * math.exp(-((hbp - 275) / 25) ** 2)
    Rc = 2 * math.sqrt(Cbp ** 7 / (Cbp ** 7 + 25 ** 7))
    Sl = 1 + 0.015 * (Lbp - 50) ** 2 / math.sqrt(20 + (Lbp - 50) ** 2)
    Sc, Sh = 1 + 0.045 * Cbp, 1 + 0.015 * Cbp * T
    Rt = -math.sin(math.radians(2 * dth)) * Rc
    return math.sqrt((dLp / Sl) ** 2 + (dCp / Sc) ** 2 + (dHp / Sh) ** 2 + Rt * (dCp / Sc) * (dHp / Sh))


def lin_mean(colours):
    return tuple(srgb(sum(lin(c[i]) for c in colours) / len(colours)) for i in range(3))


def lin_lerp(a, b, t):
    return tuple(srgb(lin(a[i]) * (1 - t) + lin(b[i]) * t) for i in range(3))


def sstep(e0, e1, x):
    t = max(0.0, min(1.0, (x - e0) / (e1 - e0)))
    return t * t * (3 - 2 * t)


def q565(v, bits):
    """The nearest value a DXT1 endpoint channel of so many bits decodes to exactly."""
    levels = [(k << (8 - bits)) | (k >> (2 * bits - 8)) for k in range(1 << bits)]
    return min(levels, key=lambda x: abs(x - v))


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def sha_img(im):
    return hashlib.sha256(im.tobytes()).hexdigest()


# ------------------------------------------------------------------ inputs
class Field:
    """A wrapping bilinear sampler over one 8-bit channel (0..1)."""
    def __init__(self, im, channel):
        ch = im.getchannel(channel)
        self.w, self.h = ch.size
        self.px = ch.tobytes()

    def at(self, x, y):
        x %= self.w
        y %= self.h
        x0, y0 = int(x), int(y)
        fx, fy = x - x0, y - y0
        x1, y1 = (x0 + 1) % self.w, (y0 + 1) % self.h
        p = self.px
        a = p[y0 * self.w + x0] * (1 - fx) + p[y0 * self.w + x1] * fx
        b = p[y1 * self.w + x0] * (1 - fx) + p[y1 * self.w + x1] * fx
        return (a * (1 - fy) + b * fy) / 255.0


def decode_inputs(modules, work):
    os.makedirs(work, exist_ok=True)
    found = tpac_textures.find(modules, INPUTS)
    lineage, images = {}, {}
    for name in INPUTS:
        rec = found.get(name)
        if rec is None:
            raise SystemExit('vanilla input not found in the installed packs: ' + name)
        im = tpac_textures.decode(rec)
        path = os.path.join(work, name + '.png')
        im.save(path)
        images[name] = im
        lineage[name] = {'pack': os.path.relpath(rec['pack'], modules).replace('\\', '/'), 'source': rec['source'], 'format': rec['format'],
                         'size': [rec['width'], rec['height']], 'flags': rec['flags'], 'decodedSha256': sha_img(im)}
    return images, lineage


def sample_shots(shots):
    """Recomputes SAMPLES from the acc4 shots (sRGB medians); returns the rows and whether they match the constants."""
    rows, same = {}, True
    for kind, items in SAMPLES.items():
        rows[kind] = []
        for shot, box, rgb in items:
            path = os.path.join(shots, 'map2_acceptance-14208-shot-%s.bmp' % shot)
            im = Image.open(path).convert('RGB').crop(box)
            chans = [sorted(c.tobytes()) for c in im.split()]
            med = tuple(c[len(c) // 2] if len(c) % 2 else (c[len(c) // 2 - 1] + c[len(c) // 2]) // 2 for c in chans)
            rows[kind].append({'shot': shot, 'box': list(box), 'median': list(med), 'shotSha256': sha(path)})
            same = same and all(abs(med[i] - rgb[i]) <= 1 for i in range(3))
    return rows, same


# ------------------------------------------------------------------ the atlas
def cell_alpha(kind, variant, noise):
    """The falloff of one cell (INNER x INNER floats, row-major, v down) and its edge noise offset."""
    out = [0.0] * (INNER * INNER)
    oy = 37 + 101 * variant
    if kind == 'river':
        # a capsule along u, so strips chained round a bend leave no corners: a middle segment |u| <= 0.35 with ends rounded out to about
        # 0.99; across v the edge sits at about 0.80 +- 0.08 (two octaves of a row of the vanilla water noise: about 2 bumps per cell length,
        # and a quarter-amplitude ripple at twice that)
        edge = lambda i, x0, y0: 0.80 + 0.12 * (noise.at(x0 + i * 20.0 / INNER, y0) - 0.5) + 0.03 * (noise.at(x0 + 77 + i * 40.0 / INNER, y0 + 29) - 0.5)
        edge_a = [edge(i, 11, oy) for i in range(INNER)]
        edge_b = [edge(i, 131, oy + 53) for i in range(INNER)]
        for j in range(INNER):
            v = (j + 0.5) / INNER * 2 - 1
            for i in range(INNER):
                u = (i + 0.5) / INNER * 2 - 1
                e = edge_a[i] if v < 0 else edge_b[i]
                rho = math.hypot(max(0.0, abs(u) - 0.35) / 0.56, abs(v) / e)
                out[j * INNER + i] = 1 - sstep(0.69, 1.15, rho)
    else:
        ring = 720
        # (two octaves around a circle in the noise: a few lobes, and a quarter-amplitude ripple)
        radius = [0.78 + 0.14 * (noise.at(128 + 5 * math.cos(2 * math.pi * k / ring), oy + 5 * math.sin(2 * math.pi * k / ring)) - 0.5)
                  + 0.05 * (noise.at(60 + 18 * math.cos(2 * math.pi * k / ring), oy + 18 * math.sin(2 * math.pi * k / ring)) - 0.5) for k in range(ring)]
        for j in range(INNER):
            y = (j + 0.5) / INNER * 2 - 1
            for i in range(INNER):
                x = (i + 0.5) / INNER * 2 - 1
                rho = math.hypot(x, y)
                k = int((math.atan2(y, x) / (2 * math.pi) % 1.0) * ring) % ring
                r = radius[k]
                out[j * INNER + i] = 1 - sstep(r - 0.22, r + 0.15, rho)
    return out


def build(images, core):
    noise = Field(images['water_noise'], 'R')
    grain = Field(images['water_noise'], 'G')
    nx_f = Field(images['worldmap_water_normal_map'].convert('RGB'), 'R')
    ny_f = Field(images['worldmap_water_normal_map'].convert('RGB'), 'G')
    spec = images['worldmap_swamp_decal_3_s'].convert('RGB')
    s_px = list(spec.tobytes())
    gloss = sorted(s_px[1::3])
    threshold = gloss[int(len(gloss) * 0.9)]
    puddle = [(s_px[k], s_px[k + 1], s_px[k + 2]) for k in range(0, len(s_px), 3) if s_px[k + 1] >= threshold]
    # the puddles' mean, snapped to values DXT1's 5:6:5 endpoints hold exactly
    s_core = tuple(q565(sum(p[i] for p in puddle) / len(puddle), 6 if i == 1 else 5) for i in range(3))
    d = Image.new('RGBA', (ATLAS, ATLAS), (0, 0, 0, 0))
    n = Image.new('RGB', (ATLAS, ATLAS), (128, 128, 255))
    s = Image.new('RGB', (ATLAS, ATLAS), s_core)
    core_l = [lin(c) for c in core]
    alphas = {}
    for col, kind in enumerate(['river', 'pool']):
        for row in range(GRID):
            a = cell_alpha(kind, row, noise)
            alphas[(col, row)] = a
            cd = bytearray(CELL * CELL * 4)
            cn = bytearray(CELL * CELL * 3)
            cs = bytearray(CELL * CELL * 3)
            ox, oy = 300 * col + 170 * row, 200 * row + 90 * col     # each cell its own crop of the vanilla maps
            for j in range(CELL):
                for i in range(CELL):
                    k = j * CELL + i
                    ii, jj = i - MARGIN, j - MARGIN
                    av = a[jj * INNER + ii] if 0 <= ii < INNER and 0 <= jj < INNER else 0.0
                    # one colour and one gloss throughout: stamps overlap along a river, and a rim of another colour would ring every overlap
                    g = 1 + 0.04 * (grain.at(i * 0.5 + ox, j * 0.5 + oy) - 0.5)      # a faint large-scale variation, no grain
                    rgb = [srgb(core_l[c] * g) for c in range(3)]
                    # colour runs to the cell edge (bilinear and mip neighbours of the opaque texels stay water-coloured)
                    cd[4 * k:4 * k + 4] = bytes((int(round(rgb[0])), int(round(rgb[1])), int(round(rgb[2])), int(round(255 * av))))
                    x = (nx_f.at(i + ox, j + oy) * 2 - 1) * RIPPLE
                    y = (ny_f.at(i + ox, j + oy) * 2 - 1) * RIPPLE
                    z = math.sqrt(max(0.0, 1 - x * x - y * y))
                    cn[3 * k:3 * k + 3] = bytes((int(round((x + 1) * 127.5)), int(round((y + 1) * 127.5)), int(round((z + 1) * 127.5))))
                    cs[3 * k:3 * k + 3] = bytes(s_core)
            d.paste(Image.frombytes('RGBA', (CELL, CELL), bytes(cd)), (col * CELL, row * CELL))
            n.paste(Image.frombytes('RGB', (CELL, CELL), bytes(cn)), (col * CELL, row * CELL))
            s.paste(Image.frombytes('RGB', (CELL, CELL), bytes(cs)), (col * CELL, row * CELL))
    return d, n, s, alphas, {'spec': list(s_core), 'puddleGlossThreshold': threshold, 'puddlePixels': len(puddle)}


# ------------------------------------------------------------------ gates
def roundtrip(im, fmt, work, tag):
    path = os.path.join(work, 'rt_%s_%s.dds' % (tag, fmt))
    im.save(path, pixel_format=fmt)
    back = Image.open(path)
    back.load()
    os.remove(path)
    return back


def mae(a, b, bands):
    total = 0.0
    for band in bands:
        x, y = a.getchannel(band).tobytes(), b.getchannel(band).tobytes()
        total += sum(abs(p - q) for p, q in zip(x, y)) / len(x)
    return total / len(bands)


def gates(d, n, s, alphas, images, core, sea, ground, tinted, work):
    res, failures = {}, []
    # seams: alpha 0 in every cell margin
    a = d.getchannel('A')
    border = 0
    for col in range(GRID):
        for row in range(GRID):
            x0, y0 = col * CELL, row * CELL
            for box in ((x0, y0, x0 + CELL, y0 + MARGIN), (x0, y0 + CELL - MARGIN, x0 + CELL, y0 + CELL), (x0, y0, x0 + MARGIN, y0 + CELL), (x0 + CELL - MARGIN, y0, x0 + CELL, y0 + CELL)):
                border = max(border, a.crop(box).getextrema()[1])
    res['seams'] = {'maxMarginAlpha': border}
    if border != 0:
        failures.append('seams: alpha %d in a margin' % border)
    # opaque core: alpha >= 0.9 inside half the half extents (river: |u|, |v| <= 0.5; pool: rho <= 0.5)
    worst = 1.0
    for (col, row), al in alphas.items():
        for j in range(INNER):
            v = (j + 0.5) / INNER * 2 - 1
            for i in range(INNER):
                u = (i + 0.5) / INNER * 2 - 1
                inside = (abs(u) <= 0.5 and abs(v) <= 0.5) if col == 0 else math.hypot(u, v) <= 0.5
                if inside:
                    worst = min(worst, al[j * INNER + i])
    res['opaqueCore'] = {'minAlphaInsideHalf': round(worst, 4), 'rule': '>= 0.9 inside 0.5 x the half extents (roads.json water.opaqueCore 0.5)'}
    if worst < 0.9:
        failures.append('opaque core: alpha %.3f inside half the half extents' % worst)
    # chain: river strips every step (= STEP_OVER_HALF x the half length) composited (alpha over); the middle line stays opaque and even
    chain = {}
    for row in range(GRID):
        al = alphas[(0, row)]
        line = []
        for x in range(4 * INNER):
            u0 = x / float(INNER) * 2           # in half lengths from the first stamp's centre
            keep = 1.0
            for kstamp in range(12):
                u = u0 - kstamp * STEP_OVER_HALF * 1.0
                if abs(u) < 1:
                    i = min(INNER - 1, int((u + 1) / 2 * INNER))
                    keep *= 1 - al[(INNER // 2) * INNER + i]
            line.append(1 - keep)
        mid = line[INNER:3 * INNER]
        chain['row%d' % row] = {'minAlongMiddle': round(min(mid), 4)}
        if min(mid) < 0.98:
            failures.append('chain: row %d middle alpha %.3f' % (row, min(mid)))
    res['chain'] = chain
    # reads as water: the opaque albedo against the acc4 sea and ground samples (lighting factor 1)
    px = d.tobytes()
    core_px = [(px[k], px[k + 1], px[k + 2]) for k in range(0, len(px), 4) if px[k + 3] >= 250]
    mean_core = lin_mean(core_px)
    de_sea = {('%s %s' % (r[0], r[1])): round(de2000(mean_core, r[2]), 2) for r in sea}
    de_ground = {('%s %s' % (r[0], r[1])): round(de2000(mean_core, r[2]), 2) for r in ground}
    de_tinted = round(de2000(tinted, lin_mean([r[2] for r in sea])), 2)
    res['readsAsWater'] = {'coreMean': [round(c, 1) for c in mean_core], 'target': [round(c, 1) for c in core], 'deToSea': de_sea,
                           'deToGround': de_ground, 'tintedStampDeToSeaMean': de_tinted,
                           'rule': 'min dE2000 to an acc4 sea sample <= 12 and to every acc4 ground sample >= 20'}
    if min(de_sea.values()) > 12:
        failures.append('reads as water: dE %.1f to the nearest sea sample' % min(de_sea.values()))
    if min(de_ground.values()) < 20:
        failures.append('reads as water: dE %.1f to a ground sample' % min(de_ground.values()))
    # no grain: the core's luminance spread against the tinted vanilla stamp's (the fallback)
    def lum_std(pixels):
        ls = [0.2126 * p[0] + 0.7152 * p[1] + 0.0722 * p[2] for p in pixels]
        m = sum(ls) / len(ls)
        return math.sqrt(sum((l - m) ** 2 for l in ls) / len(ls))
    uv = images['mainmap_decal_undervillage_d'].convert('RGBA').tobytes()
    uv_core = [(uv[k], uv[k + 1], uv[k + 2]) for k in range(0, len(uv), 4) if uv[k + 3] >= 230]
    res['grain'] = {'coreLumStd': round(lum_std(core_px), 2), 'fallbackStampCoreLumStd': round(lum_std(uv_core), 2), 'rule': '<= 0.25 x the fallback stamp'}
    if lum_std(core_px) > 0.25 * lum_std(uv_core):
        failures.append('grain: core luminance spread %.2f' % lum_std(core_px))
    # BCn round trips against the vanilla references' own
    ref_d = images['mainmap_decal_undervillage_d'].convert('RGBA')
    rt_ref = roundtrip(ref_d, 'DXT5', work, 'ref_d')
    ref_n = images['worldmap_water_normal_map'].convert('RGB')
    rt_ref_n = roundtrip(ref_n, 'BC5', work, 'ref_n')
    ref_s = images['worldmap_swamp_decal_3_s'].convert('RGB')
    rt_ref_s = roundtrip(ref_s, 'DXT1', work, 'ref_s')
    rt_d, rt_n, rt_s = roundtrip(d, 'DXT5', work, 'd'), roundtrip(n, 'BC5', work, 'n'), roundtrip(s, 'DXT1', work, 's')
    bcn = {'d': {'rgbMae': round(mae(d, rt_d.convert('RGBA'), 'RGB'), 3), 'alphaMae': round(mae(d, rt_d.convert('RGBA'), 'A'), 3)},
           'n': {'rgMae': round(mae(n, rt_n.convert('RGB'), 'RG'), 3)}, 's': {'rgbMae': round(mae(s, rt_s.convert('RGB'), 'RGB'), 3)},
           'reference': {'mainmap_decal_undervillage_d DXT5 rgb': round(mae(ref_d, rt_ref.convert('RGBA'), 'RGB'), 3),
                         'worldmap_water_normal_map BC5 rg': round(mae(ref_n, rt_ref_n.convert('RGB'), 'RG'), 3),
                         'worldmap_swamp_decal_3_s DXT1 rgb': round(mae(ref_s, rt_ref_s.convert('RGB'), 'RGB'), 3)},
           'ceilings': {'d': 4.87, 'alpha': 3.0}, 'note': 'Pillow has no BC7 encoder: DXT5 stands in (stricter); 4.87 is the worst vanilla world-map decal round trip (MMR-259)'}
    bcn['ceilings']['n'] = round(1.25 * bcn['reference']['worldmap_water_normal_map BC5 rg'], 3)
    bcn['ceilings']['s'] = round(1.25 * bcn['reference']['worldmap_swamp_decal_3_s DXT1 rgb'], 3)
    res['bcn'] = bcn
    if bcn['d']['rgbMae'] > 4.87 or bcn['d']['alphaMae'] > 3.0:
        failures.append('bcn: diffuse %.2f / alpha %.2f' % (bcn['d']['rgbMae'], bcn['d']['alphaMae']))
    if bcn['n']['rgMae'] > bcn['ceilings']['n'] or bcn['s']['rgbMae'] > bcn['ceilings']['s']:
        failures.append('bcn: normal %.2f or specular %.2f past 1.25 x the vanilla inputs' % (bcn['n']['rgMae'], bcn['s']['rgbMae']))
    return res, failures


def review(d, n, s, out):
    """The atlas over three acc4 ground colours and a chained river run (review only)."""
    os.makedirs(out, exist_ok=True)
    tiles = []
    for name, rgb in (('sand', (130, 116, 104)), ('earth', (139, 103, 64)), ('snow', (220, 205, 213))):
        bg = Image.new('RGBA', (ATLAS, ATLAS), rgb + (255,))
        tiles.append(Image.alpha_composite(bg, d).convert('RGB').resize((ATLAS // 2, ATLAS // 2)))
    sheet = Image.new('RGB', (ATLAS // 2 * 3, ATLAS // 2))
    for k, t in enumerate(tiles):
        sheet.paste(t, (k * ATLAS // 2, 0))
    sheet.save(os.path.join(out, 'water-atlas-over-ground.png'))
    n.resize((ATLAS // 2, ATLAS // 2)).save(os.path.join(out, 'water-normal.png'))
    s.resize((ATLAS // 2, ATLAS // 2)).save(os.path.join(out, 'water-spec.png'))


# ------------------------------------------------------------------ the published pack (after the Modding Kit session)
MATERIAL_FLAGS = ['gbuffer_alpha_blend', 'use_albedo', 'render_on_terrain', 'use_normalmap', 'use_specular']
# The v1.4.8 Modding Kit offers no do_not_blend_normals on a material (the 2026-10-20 publish), so it is recorded, not required.
OPTIONAL_FLAGS = ['do_not_blend_normals']
NOT_FLAGS = ['render_on_objects', 'render_on_grass', 'albedo_multiply', 'hardlight_blend', 'water_foam_decal', 'height_masked']
TEXTURES = {'d': ({'BC7', 'DXT5', 'BC3'}, ['has_alpha', 'dont_resize_in_atlas']), 'n': ({'BC5'}, []), 's': ({'DXT1', 'BC1', 'DXT5', 'BC3'}, [])}


def material_flags(path, name):
    """The flag strings of a material record (read by its u32 length-prefixed name; the editor writes a letter byte after it)."""
    d, _, _ = tpac_textures.header(path)
    if d is None:
        return None
    key = struct.pack('<I', len(name)) + name.encode()
    at = d.find(key)
    if at < 0:
        return None
    seg = d[at + len(key):at + len(key) + 420]
    known = MATERIAL_FLAGS + OPTIONAL_FLAGS + NOT_FLAGS
    return [f for f in known if re.search(rb'(?<![a-z_])' + f.encode() + rb'(?![a-z_])', seg)]


def check_packs(packs, module='MNBL.ProceduralWorld'):
    """The published water material and its three textures, by the module's pack headers (as check_magic_packs.py does for the magic sets)."""
    files = sorted(os.path.join(packs, f) for f in os.listdir(packs) if f.endswith('.tpac')) if os.path.isdir(packs) else []
    textures, flags = {}, None
    for p in files:
        for r in tpac_textures.records(p):
            if r['name'].startswith(NAME + '_'):
                textures[r['name']] = dict(r, pack=os.path.basename(p))
        flags = flags or material_flags(p, NAME)
    rows, problems = [], []
    for suffix, (formats, need) in TEXTURES.items():
        name = '%s_%s' % (NAME, suffix)
        r = textures.get(name)
        row = {'name': name, 'found': r is not None}
        if r:
            row.update({k: r[k] for k in ('pack', 'format', 'width', 'height', 'flags', 'source')})
            bad = ['format %s not in %s' % (r['format'], sorted(formats))] if r['format'] not in formats else []
            bad += ['size %dx%d' % (r['width'], r['height'])] if (r['width'], r['height']) != (ATLAS, ATLAS) else []
            bad += ['missing flag ' + f for f in need if f not in r['flags']]
            bad += ['source %s' % r['source']] if not r['source'].startswith('$BASE/Modules/%s/AssetSources/' % module) else []
            bad += ['no data chunk'] if r['chunk'] is None else []
            row['problems'] = bad
            problems += ['%s: %s' % (name, b) for b in bad]
        else:
            problems.append('%s: not in any pack' % name)
        rows.append(row)
    rows.append({'name': NAME, 'kind': 'material', 'found': flags is not None, 'flags': flags,
                 'optionalAbsent': [f for f in OPTIONAL_FLAGS if flags is not None and f not in flags]})
    if flags is None:
        problems.append('%s: material not in any pack' % NAME)
    else:
        problems += ['%s: missing flag %s' % (NAME, f) for f in MATERIAL_FLAGS if f not in flags]
        problems += ['%s: unwanted flag %s' % (NAME, f) for f in NOT_FLAGS if f in flags]
    return {'schema': 'mnbl.water-packs', 'version': 1, 'ticket': 'MMR-247 (cluster W)', 'packs': [os.path.basename(f) for f in files],
            'rows': rows, 'problems': problems, 'passed': bool(files) and not problems}


def check_main(argv):
    ap = argparse.ArgumentParser(prog='water_decal.py check')
    ap.add_argument('--packs', default=os.path.join(REPO, 'Modules/MNBL.ProceduralWorld/AssetPackages'))
    ap.add_argument('--out')
    a = ap.parse_args(argv)
    res = check_packs(a.packs)
    if a.out:
        with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
            json.dump(res, f, indent=1)
            f.write('\n')
    if not res['packs']:
        print('no published packs in', a.packs)
        return 2
    for p in res['problems']:
        print(p)
    print('%d packs, %d problems' % (len(res['packs']), len(res['problems'])))
    return 0 if res['passed'] else 1


def main():
    if len(sys.argv) > 1 and sys.argv[1] == 'check':
        return check_main(sys.argv[2:])
    ap = argparse.ArgumentParser()
    ap.add_argument('--modules', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--evidence')
    ap.add_argument('--review')
    ap.add_argument('--shots')
    ap.add_argument('--work', default=os.path.join(REPO, 'build', 'water-art', 'ext'))
    args = ap.parse_args()
    images, lineage = decode_inputs(args.modules, args.work)
    shots, same = (sample_shots(args.shots) if args.shots else (None, None))
    sea = [(r[0], tuple(r[1]), r[2]) for r in SAMPLES['sea']]
    ground = [(r[0], tuple(r[1]), r[2]) for r in SAMPLES['ground']]
    sea_mean = lin_mean([r[2] for r in sea])
    shallow = SAMPLES['shallow'][0][2]
    core = lin_lerp(sea_mean, shallow, CORE_TOWARD_SHALLOW)
    d, n, s, alphas, spec = build(images, core)
    os.makedirs(args.out, exist_ok=True)
    outputs = {}
    for suffix, im in (('d', d), ('n', n), ('s', s)):
        path = os.path.join(args.out, '%s_%s.png' % (NAME, suffix))
        im.save(path, optimize=False)
        outputs[os.path.relpath(path, REPO).replace('\\', '/')] = sha(path)
    # the fallback (the tinted vanilla stamp until the material is published): tint so its opaque core's linear mean lands on the core colour
    uv = images['mainmap_decal_undervillage_d'].convert('RGBA').tobytes()
    uv_core = [(uv[k], uv[k + 1], uv[k + 2]) for k in range(0, len(uv), 4) if uv[k + 3] >= 230]
    uv_mean = lin_mean(uv_core)
    tint = [int(round(srgb(min(1.0, lin(core[c]) / lin(uv_mean[c]))))) for c in range(3)]
    tinted = tuple(srgb(lin(uv_mean[c]) * lin(tint[c])) for c in range(3))
    res, failures = gates(d, n, s, alphas, images, core, sea, ground, tuple(SAMPLES['tintedStamp'][0][2]), args.work)
    res['fallback'] = {'material': 'mainmap_decal_undervillage', 'coreMean': [round(c, 1) for c in uv_mean], 'tint': tint,
                       'tintedCore': [round(c, 1) for c in tinted], 'deTintedCoreToCore': round(de2000(tinted, core), 2),
                       'rule': 'SetFactor1 multiplies in linear light: tint = core / the stamp core mean, per channel (roads.json water.fallback.tint)'}
    if shots is not None and not same:
        failures.append('the acc4 shot samples differ from SAMPLES')
    evidence = {
        'schema': 'mnbl.water-decal', 'version': 1, 'ticket': 'MMR-247 (cluster W)',
        'material': {'name': NAME, 'template': 'Create Override of Native mainmap_decal_undervillage (shader deferred_decal)',
                     'flags': ['gbuffer_alpha_blend', 'use_albedo', 'render_on_terrain', 'use_normalmap', 'use_specular', 'do_not_blend_normals'],
                     'notFlags': ['render_on_objects', 'render_on_grass', 'albedo_multiply', 'hardlight_blend', 'water_foam_decal', 'height_masked',
                                  'override_visibility_checks', 'is_road'],
                     'textures': {'d': '%s_d: 1024 x 1024 Albedo_HQ BC7, has_alpha, dont_resize_in_atlas, sRGB' % NAME,
                                  'n': '%s_n: 1024 x 1024 Normal BC5, is_bumpmap' % NAME, 's': '%s_s: 1024 x 1024 Specular DXT1, is_specularmap' % NAME}},
        'atlas': {'size': ATLAS, 'cell': CELL, 'margin': MARGIN, 'columns': {'0': 'river strip (along u)', '1': 'pool'}, 'rows': 'variants',
                  'argument': 'SetVectorArgument(0.5, 0.5, column / 2, row / 2)'},
        'colour': {'seaSamples': SAMPLES['sea'], 'shallowSamples': SAMPLES['shallow'], 'groundSamples': SAMPLES['ground'],
                   'seaMean': [round(c, 1) for c in sea_mean], 'core': [round(c, 1) for c in core], 'coreTowardShallow': CORE_TOWARD_SHALLOW,
                   'lighting': 'factor 1 assumed (albedo = the lit shot colour)', 'uniform': 'one colour and gloss in every cell: overlapping stamps show no rings',
                   'recomputed': shots},
        'specular': spec, 'ripple': RIPPLE,
        'inputs': lineage, 'codex': 'not used: no generated imagery fed any texture',
        'gates': res, 'failures': failures, 'outputs': outputs,
    }
    if args.review:
        review(d, n, s, args.review)
    if args.evidence:
        with open(args.evidence, 'w', newline='\n') as f:
            json.dump(evidence, f, indent=1, sort_keys=True)
            f.write('\n')
    print(json.dumps({'failures': failures, 'outputs': outputs, 'fallbackTint': tint, 'core': [round(c, 1) for c in core]}, indent=1))
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main())
