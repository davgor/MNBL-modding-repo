"""MMR-259, MMR-288: derive the magic-ground vein-stamp atlases from vanilla textures, gate them, and write the module's
asset sources.

Delivery A (MMR-249): all magic ground is one runtime decal overlay. Each visible school gets one published decal material
(mnbl_magic_<school>) whose three textures are 2048 x 2048 stamp atlases:
  mnbl_magic_<school>_d  RGBA  diffuse, alpha = the stamp's baked falloff (import: has_alpha, dont_resize_in_atlas; BC7)
  mnbl_magic_<school>_n  RGB   tangent normal, DirectX style like the vanilla terrain normals (import: is_bumpmap; BC5)
  mnbl_magic_<school>_s  RGB   R specular, G gloss, B occlusion, the vanilla terrain convention (import: is_specularmap; DXT1)
The atlas is 4 x 4 cells of 512 px with a 16 px transparent margin. Row r is the core group (0 arid, 1 verdant, 2 rock,
3 cold: GROUPS), column c the vein stamp kind (MMR-288, contract C1): 0 trunk, 1 fork, 2 tip, 3 source burst. A decal
shows cell (c, r) with SetVectorArgument(0.25, 0.25, c / 4, r / 4). Inside the margin u runs left to right, v top to
bottom; veins grow toward +u and every stamp is (near) symmetric about v = 0.5. Placement scales a stamp non-uniformly:
box length L along the branch (u), width W across (v). Hidden schools reuse a visible school's material
(worldgen/policies/terrain-materials.json mutation.hiddenLooks).

Every colour and surface texel derives from installed vanilla textures (owner decision 4), decoded by
tools/art/tpac_textures.py: the group's parent layer at its own tile scale gives structure, height, normals and specular;
secondary vanilla textures (ashes, soil, ice, snow, moss, desert floor) recoloured by the recipes give the corrupted ground
of the halo and the vein materials; the crack detail around the veins is the crack network of the vanilla cracked-mud
decal (mainmap_cracked_mud_a). The vein skeleton (centre lines, pipe-model widths, capillaries) is procedural: it only
places things. Colour direction comes from the FWGC++ school colours and descriptors, the biomes.json variant names, the
concept art (weave) and the material briefs (MAT-020..032). See docs/magic-ground-textures.md.

Usage: python tools/art/magic_ground.py --ext <decoded vanilla dir> --out <AssetSources/magic_ground dir>
                                        [--review <dir>] [--evidence <json>] [--inputs-evidence <json>] [--schools a,b] [--fast]
"""
import argparse
import copy
import hashlib
import json
import math
import os
import random
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFilter
from PIL.ImageMath import _Operand as O, ops

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
MIN, MAX = ops['min'], ops['max']
CELL, GRID, MARGIN, CELL_UNITS = 512, 4, 16, 10.0
PPU = CELL / CELL_UNITS  # 51.2 texels per map unit: the scale the ground materials are laid out at inside a cell
ATLAS = CELL * GRID
SCHOOLS = ['weave', 'umbral', 'infernal', 'radiant', 'fire', 'water', 'earth', 'air']
GROUPS = [  # row order is the atlas contract (docs/magic-ground-textures.md)
    {'name': 'arid', 'biomes': ['desert'], 'parent': 'sand', 'slope': 'desert_rock'},
    {'name': 'verdant', 'biomes': ['grassland', 'forest', 'rainforest', 'marsh'], 'parent': 'grass', 'slope': 'rock'},
    {'name': 'rock', 'biomes': ['exposed_rock'], 'parent': 'rock', 'slope': 'rock'},
    {'name': 'cold', 'biomes': ['boreal_forest', 'cold_tundra', 'land_ice'], 'parent': 'tundra_moss', 'slope': 'rock'},
]
COLUMNS = ['trunk', 'fork', 'tip', 'burst']  # MMR-288 contract C1
# vein frame (contract C1): u, v run 0..1 across the inner square; the decal box spans the whole cell, margin included
INNER = CELL - 2 * MARGIN  # 480
PERIOD = CELL * 3 // 4  # 384: trunks chained at 0.75 x the box length repeat every 384 texels
OVERLAP = INNER - PERIOD  # 96 (u 0.2): the head u 0..0.2 of a chained stamp lies over the tail u 0.8..1 of the previous
CORE_W = INNER / 12.0  # trunk core width (40 texels)
HALO_R = INNER / 4.0  # the corruption halo reaches about this far from the axis
MEANDER, MEANDER_CAP = INNER / 96.0, INNER / 16.0  # trunk meander amplitude (rms) and its hard cap
SPLIT_U, CHILD_V, CHILD_SLOPE, CHILD_W = 0.4, 0.3, 0.4, 0.7  # fork: split, exit offset, exit heading dv/du, child width
TIP_END = 0.85
NODE_R = 0.12 * INNER
RAY_END = 0.47 * INNER
HALO_ALPHA_MAX = 0.35
HAIR = 3.0  # hairline capillary width in texels (tapers to 0.7 x)
SS = 2  # supersampling of the skeleton rasteriser
# map readability (MMR-288): 37 screen px per unit at camera distance 38; placement boxes about 5 units long, 1.2-5 wide
SCREEN_PPU, BOX_W = 37.0, (1.2, 5.0)
# vanilla textures besides the layer set that bound the stat envelope: the recipes' material sources (diffuse, normal, spec)
ENVELOPE_EXTRA = [('ashes_a_d', 'ashes_a_n', 'ashes_a_s'), ('ground_soil_a_d', 'ground_soil_a_n', 'ground_soil_a_s'), ('ice_a_d', 'ice_a_n', 'ice_a_s'),
                  ('ground_grass_stone_a_d', 'ground_grass_stone_a_n', 'ground_grass_stone_a_s')]
# vanilla world-map decal diffuses: their own DXT5 round-trip error is the ceiling for ours
BCN_REFERENCE = ['mainmap_decal_undervillage_d', 'mainmap_decal_undervillage_burnt_d', 'worldmap_swamp_decal_1_d', 'decal_b_d']
VANILLA_EXTRA = ['ashes_a_d', 'ashes_a_n', 'ashes_a_s', 'ground_soil_a_d', 'ground_soil_a_h', 'ice_a_d', 'ice_a_s', 'snow_a_d', 'snow_a_s',
                 'ground_grass_stone_a_d', 'mainmap_decal_moss_d', 'decal_b_d', 'decal_b_h', 'ground_mud_b_d', 'desert_floor_c_d',
                 'ground_drygrass_a_d', 'ground_drygrass_b_d', 'stone_desert_a_d', 'forest_a_d', 'ground_pebbles_a_d_mainmap',
                 'mainmap_decal_undervillage_d', 'mainmap_decal_undervillage_burnt_d', 'worldmap_swamp_decal_1_d']

EXT = None
_cache = {}


# ------------------------------------------------------------------ float image helpers (Pillow 'F' images through ImageMath operands)
def F(img):
    return O(img.convert('F'))


def I(o):
    return o.im if isinstance(o, O) else o


def clamp01(a):
    return MIN(MAX(a, 0.0), 1.0)


def sstep(e0, e1, x):
    t = clamp01((x - e0) * (1.0 / (e1 - e0)))
    return t * t * (3.0 - 2.0 * t)


def lerp(a, b, t):
    return a + (b - a) * t


def const(v, w=CELL):
    return O(Image.new('F', (w, w), float(v)))


def lum(r, g, b):
    return r * 0.299 + g * 0.587 + b * 0.114


def mean(o):
    im = I(o)
    while im.size[0] > 64 or im.size[1] > 64:
        im = im.resize((max(1, im.size[0] // 4), max(1, im.size[1] // 4)), Image.BOX)
    return im.resize((1, 1), Image.BOX).getpixel((0, 0))


def std(o):
    m = mean(o)
    return max(0.0, mean(o * o) - m * m) ** 0.5


def wmean(o, w):
    return mean(o * w) / max(1e-9, mean(w))


def quant(o, q):
    im = I(o)
    lo, hi = im.getextrema()
    if hi <= lo:
        return lo
    hist = im.histogram(extrema=(lo, hi))
    tot, acc = float(sum(hist)), 0.0
    for i, c in enumerate(hist):
        acc += c
        if acc / tot >= q:
            return lo + (hi - lo) * (i + 0.5) / len(hist)
    return hi


def norm_q(o, qlo=0.02, qhi=0.98):
    lo, hi = quant(o, qlo), quant(o, qhi)
    return clamp01((o - lo) * (1.0 / max(1e-6, hi - lo)))


def shift(o, dx, dy):
    return O(ImageChops.offset(I(o), dx, dy))


def wrap_pad(im, p):
    w, h = im.size
    big = Image.new(im.mode, (w + 2 * p, h + 2 * p))
    for oy in (-h, 0, h):
        for ox in (-w, 0, w):
            big.paste(im, (p + ox, p + oy))
    return big


def binom(o, passes):
    for _ in range(passes):
        o = (shift(o, 1, 0) + shift(o, -1, 0) + o * 2.0) * 0.25
        o = (shift(o, 0, 1) + shift(o, 0, -1) + o * 2.0) * 0.25
    return o


def up_wrap(im, w):
    n = im.size[0]
    k = w // n
    p = 4
    big = wrap_pad(im, p).resize(((n + 2 * p) * k, (n + 2 * p) * k), Image.BICUBIC)
    return big.crop((p * k, p * k, p * k + w, p * k + w))


def blur(o, sigma):
    im = I(o)
    w = im.size[0]
    if sigma <= 0:
        return o
    k = 1
    while sigma / k > 3.0 and w // (k * 2) >= 16:
        k *= 2
    small = im.resize((w // k, w // k), Image.BOX) if k > 1 else im
    s = sigma / k
    small = I(binom(O(small), max(1, int(round(2 * s * s)))))
    return O(up_wrap(small, w)) if k > 1 else O(small)


def white(n, seed):
    return F(Image.frombytes('L', (n, n), random.Random(seed).randbytes(n * n))) * (1 / 255.0)


def noise(scale_px, seed, w=CELL):
    """Isotropic smooth noise, feature size about scale_px, mean 0.5."""
    n = 8
    while n * 2 <= w and w / (n * 2) >= scale_px / 4.0:
        n *= 2
    o = binom(white(n, seed), 6)
    m, s = mean(o), std(o)
    o = (o - m) * (0.17 / max(1e-6, s)) + 0.5
    return clamp01(O(up_wrap(I(o), w)) if n < w else o)


def fbm(scale_px, seed, octaves=4, gain=0.5):
    tot, amp, acc = None, 1.0, 0.0
    for i in range(octaves):
        nz = (noise(max(2, scale_px / (2 ** i)), seed * 31 + i) - 0.5) * amp
        tot = nz if tot is None else tot + nz
        acc += amp
        amp *= gain
    return clamp01(tot * (1.0 / acc) + 0.5)


def streak_noise(along_px, across_px, seed):
    """Anisotropic noise stretched along x (wind streaks): noise squeezed across x, then stretched back."""
    k = max(1, int(round(along_px / max(1.0, across_px))))
    im = I(noise(across_px, seed, CELL)).crop((0, 0, max(8, CELL // k), CELL))
    return O(im.resize((CELL, CELL), Image.BICUBIC))


def grad(o):
    return (shift(o, -1, 0) - shift(o, 1, 0)) * 0.5, (shift(o, 0, -1) - shift(o, 0, 1)) * 0.5


def ramp_lut(t, keys):
    pal = []
    for i in range(256):
        x = i / 255.0
        for j in range(len(keys) - 1):
            x0, c0 = keys[j]
            x1, c1 = keys[j + 1]
            if x0 <= x <= x1:
                u = (x - x0) / max(1e-6, x1 - x0)
                pal += [int(round(c0[k] + (c1[k] - c0[k]) * u)) for k in range(3)]
                break
    l8 = to_l(t)
    p = Image.frombytes('P', l8.size, l8.tobytes())
    p.putpalette(pal)
    return [F(c) * (1 / 255.0) for c in p.convert('RGB').split()]


def tint(rgb, target, amt, gain=1.0):
    """Recolour to the target colour, keeping the source's relative luminance structure: the result's mean is about
    target (times gain), its texture the source's own."""
    L = lum(*rgb)
    rel = L * (gain / max(1e-3, mean(L)))
    return [lerp(c, rel * target[k], amt) for k, c in enumerate(rgb)]


def mix(a, b, t):
    return [lerp(a[k], b[k], t) for k in range(3)]


def c255(v):
    return tuple(x / 255.0 for x in v)


def to_l(c):
    return I(clamp01(c) * 255.0 + 0.5).convert('L')


def to_rgb(ch):
    return Image.merge('RGB', [to_l(c) for c in ch])


def hpn(o, sigma, gain):
    return clamp01((o - blur(o, sigma)) * gain + 0.5)


def jmask(field, thr, detail, jit, edge):
    return sstep(thr - edge, thr + edge, field + (detail - 0.5) * jit)


def dilate(o, px):
    im = I(o)
    return O(im.filter(ImageFilter.MaxFilter(px * 2 + 1)))


_dot_peak = {}


def dots(radius, density, seed, soft=False):
    """Round dots of about the given radius at the given density (seeds per pixel): single-pixel seeds spread by a blur,
    scaled by an isolated dot's peak, then thresholded at half height (soft: kept as domes for cushions)."""
    sigma = radius * 0.85
    if sigma not in _dot_peak:
        one = Image.new('F', (CELL, CELL), 0.0)
        one.putpixel((CELL // 2, CELL // 2), 1.0)
        _dot_peak[sigma] = I(blur(O(one), sigma)).getextrema()[1]
    q = density ** 0.5  # two independent 8-bit draws give a fine enough seed probability
    seeds = (white(CELL, seed) > (1.0 - q)) * (white(CELL, seed + 1) > (1.0 - q))
    b = blur(seeds, sigma) * (1.0 / _dot_peak[sigma])
    return clamp01(b * 1.2) if soft else sstep(0.30, 0.55, b)


# ------------------------------------------------------------------ vanilla inputs
def load(name):
    if name not in _cache:
        _cache[name] = Image.open(os.path.join(EXT, name + '.png'))
    return _cache[name]


def tiled_img(name, scale_units, off, mode):
    """A vanilla tile resampled to its world scale (scale_units map units per repeat at PPU) and wrapped over one cell."""
    src = load(name).convert(mode)
    px = max(16, int(round(scale_units * PPU)))
    key = (name, px, mode)
    if key not in _cache:
        _cache[key] = src.resize((px, px), Image.BOX if px < src.size[0] else Image.BICUBIC)
    t = _cache[key]
    out = Image.new(mode, (CELL, CELL))
    ox, oy = off[0] % px, off[1] % px
    for y in range(-oy, CELL, px):
        for x in range(-ox, CELL, px):
            out.paste(t, (x, y))
    return out


def tiled_rgb(name, scale, off):
    return [F(c) * (1 / 255.0) for c in tiled_img(name, scale, off, 'RGB').split()]


def tiled_l(name, scale, off):
    return F(tiled_img(name, scale, off, 'L')) * (1 / 255.0)


def tiled_normal(name, scale, off):
    r, g, _ = tiled_img(name, scale, off, 'RGB').split()
    return F(r) * (2 / 255.0) - 1.0, F(g) * (2 / 255.0) - 1.0


def window():
    """1 inside the cell, 0 within MARGIN of every edge (no bleed into neighbouring cells down to mip 4)."""
    row = Image.new('F', (CELL, 1))
    row.putdata([1.0 if MARGIN + 2 <= x < CELL - MARGIN - 2 else 0.0 for x in range(CELL)])
    a = O(row.resize((CELL, CELL), Image.NEAREST))
    return a * O(I(a).transpose(Image.Transpose.TRANSPOSE))


# ------------------------------------------------------------------ vein skeletons (MMR-288, contract C1)
# The skeleton only places things: centre lines with pipe-model widths, hairline capillaries and the source node, drawn in
# cell texels. Every colour and surface on and around it comes from the recipes over vanilla textures.
def ux(u):
    return MARGIN + u * INNER


def sstep_f(e0, e1, x):
    t = min(1.0, max(0.0, (x - e0) / (e1 - e0)))
    return t * t * (3.0 - 2.0 * t)


class PNoise:
    """Smooth noise of unit RMS, periodic over the trunk period: sines k = kmin..kmax with amplitude k^-slope."""

    def __init__(self, seed, kmin, kmax, slope=1.0, period=PERIOD):
        rng = random.Random(seed)
        self.t = [(2 * math.pi * k / period, rng.uniform(0, 2 * math.pi), k ** -slope) for k in range(kmin, kmax + 1)]
        self.n = math.sqrt(sum(a * a for _, _, a in self.t) / 2.0)

    def __call__(self, s):
        return sum(a * math.sin(w * s + p) for w, p, a in self.t) / self.n


def pin(s):
    """0 with zero slope where a trunk meets its neighbours on the axis (u = 0 and u = 1, i.e. s = 0 and s = OVERLAP), 1 between."""
    s %= PERIOD
    return sstep_f(0.0, 40.0, min(s, abs(s - OVERLAP), PERIOD - s))


class TrunkLine:
    """The trunk's centre-line offset and core width as functions of x, periodic over PERIOD so that chained trunks match texel
    for texel. Fork and tip stamps start on the same line, so a chain may run trunk -> fork or trunk -> tip seamlessly."""

    def __init__(self, style, seed):
        self.lo = PNoise(seed, 1, 3, 1.0)
        self.hi = PNoise(seed + 1, 6, 40, style.get('jagSlope', 1.0))
        self.wn = PNoise(seed + 2, 2, 8, 1.0)
        self.jag, self.knot = style['jag'], style.get('angular', 0)

    def hi_at(self, s):
        if not self.knot:
            return self.hi(s)
        s0 = math.floor(s / self.knot) * self.knot  # crystalline: straight runs between knots that divide the period
        t = (s - s0) / self.knot
        return self.hi(s0) * (1 - t) + self.hi(s0 + self.knot) * t

    def offset(self, x):
        s = (x - MARGIN) % PERIOD
        o = (self.lo(s) * MEANDER + self.hi_at(s) * self.jag) * pin(s)
        return max(-MEANDER_CAP, min(MEANDER_CAP, o))

    def width(self, x):
        return CORE_W * (1.0 + 0.1 * abs(self.wn((x - MARGIN) % PERIOD)))  # never under CORE_W: the readability floor


def capillary(rng, x, y, ang, length, style, w0=HAIR * 1.25, depth=0):
    """A hairline from (x, y) heading ang (radians, y down), tapering to 0.7 x HAIR; may branch once. Returns paths."""
    mode, step = style['caps'], 3.0
    n = max(2, int(length / step))
    curv = rng.uniform(-0.035, 0.035)
    a0, pts = ang, []
    for i in range(n + 1):
        t = i / float(n)
        pts.append((x, y, w0 + (HAIR * 0.7 - w0) * t))
        if mode == 'crack':
            ang += rng.gauss(0.0, 0.22)
        elif mode == 'smooth':
            ang += curv
        elif mode == 'angular':
            if i and i % 7 == 0:
                ang += rng.choice((-1, 1)) * rng.uniform(0.25, 0.55)
        elif mode == 'filament':
            ang += curv * 0.25
        x += step * math.cos(ang)
        y += step * math.sin(ang)
    out = [pts]
    if depth == 0 and rng.random() < style['capBranch']:
        j = rng.randrange(n // 3, max(n // 3 + 1, 2 * n // 3))
        bx, by, bw = pts[j]
        out += capillary(rng, bx, by, a0 + rng.choice((-1, 1)) * rng.uniform(0.45, 0.85), length * 0.5, style, bw, 1)
    return out


def inside_v(paths, lo=MARGIN + 6, hi=CELL - MARGIN - 6, both=False):
    """Paths cut where they leave the inner square across v (and across u too when both)."""
    out = []
    for p in paths:
        keep = []
        for q in p:
            if not (lo <= q[1] <= hi) or (both and not (lo <= q[0] <= hi)):
                break
            keep.append(q)
        if len(keep) > 1:
            out.append(keep)
    return out


def side_caps(rng, line_pts, style, count, sides=(1, -1)):
    """Capillaries leaving a main path's edge, heading forward (toward +u along the path) at 30-65 degrees."""
    caps = []
    if len(line_pts) < 4 or count <= 0:
        return caps
    for i in range(count):
        j = rng.randrange(1, len(line_pts) - 2)
        (x0, y0, w), (x1, y1, _) = line_pts[j], line_pts[j + 1]
        d = math.atan2(y1 - y0, x1 - x0)
        side = sides[i % len(sides)]
        if style['caps'] == 'filament':
            spread, length = rng.uniform(0.06, 0.2), rng.uniform(80, 150)
        else:
            spread, length = rng.uniform(0.5, 1.1), rng.uniform(*style['capLen'])
        a = d + side * spread
        ex, ey = x0 - math.sin(d) * side * w * 0.42, y0 + math.cos(d) * side * w * 0.42
        caps += capillary(rng, ex, ey, a, length, style)
    return caps


def parallel_filaments(line_pts, style, seed):
    """Air: broken silver hairlines running alongside the core (gaps from periodic noise, so trunks stay periodic)."""
    gap = PNoise(seed, 3, 12, 0.8)
    out = []
    for side in (1, -1):
        for k, off in enumerate(style.get('parallel', ())):
            run = []
            for x, y, w in line_pts:
                if gap((x - MARGIN) % PERIOD + 97 * k + (0 if side > 0 else 191)) > -0.2:
                    run.append((x, y + side * (w * 0.5 + off), HAIR))
                elif len(run) > 1:
                    out.append(run)
                    run = []
                else:
                    run = []
            if len(run) > 1:
                out.append(run)
    return out


def vein_skeleton(kind, style, seed):
    """Main paths [(x, y, width)], capillary paths and the node outline of one stamp kind, in cell texels."""
    line = TrunkLine(style, seed)
    rng = random.Random(seed * 13 + COLUMNS.index(kind))
    mains, caps, node = [], [], None
    xs = [x * 2.0 for x in range(-6, CELL // 2 + 7)]
    if kind == 'trunk':
        main = [(x, CELL / 2.0 + line.offset(x), line.width(x)) for x in xs]
        mains.append(main)
        # one period of capillaries, repeated every PERIOD so chained trunks stay texel-identical
        one = [p for p in main if MARGIN <= p[0] < MARGIN + PERIOD]
        base = side_caps(rng, one, style, style['capCount'])
        for k in (-1, 0, 1):
            caps += [[(x + k * PERIOD, y, w) for x, y, w in p] for p in base]
        caps = inside_v(caps + parallel_filaments(main, style, seed + 7))
    elif kind == 'fork':
        sx = ux(SPLIT_U)
        parent = []
        for x in xs:
            if x > sx:
                break
            fade = 1.0 - sstep_f(ux(0.2), sx, x)
            parent.append((x, CELL / 2.0 + line.offset(x) * fade, line.width(x) + (CORE_W - line.width(x)) * sstep_f(ux(0.2), sx, x)))
        mains.append(parent)
        jag = PNoise(seed + 5, 6, 40, style.get('jagSlope', 1.0))
        for sign in (1, -1):
            child = []
            u = SPLIT_U
            while u <= 1.0 + MARGIN / float(INNER) + 0.01:
                if u <= 0.75:  # cubic Hermite from (0.4, 0) slope 0 to (0.75, 0.2) slope CHILD_SLOPE, then straight to (1, 0.3)
                    t, d = (u - SPLIT_U) / 0.35, 0.35
                    v = (-2 * t ** 3 + 3 * t ** 2) * (CHILD_V - 0.25 * CHILD_SLOPE) + (t ** 3 - t ** 2) * d * CHILD_SLOPE
                else:
                    v = CHILD_V - CHILD_SLOPE * (1.0 - u)
                j = jag(u * INNER) * style['jag'] * sstep_f(SPLIT_U, SPLIT_U + 0.1, u) * (1.0 - sstep_f(0.7, 0.8, u))
                w = CORE_W * (0.85 + (CHILD_W - 0.85) * sstep_f(SPLIT_U, SPLIT_U + 0.15, u)) * (1.0 + 0.06 * jag(u * INNER + 61))
                child.append((ux(u), CELL / 2.0 + sign * (v * INNER + j), w))
                u += 2.0 / INNER
            mains.append(child)
        caps += side_caps(rng, parent, style, max(1, style['capCount'] // 3))
        for c in mains[1:]:
            caps += side_caps(rng, c[:int(len(c) * 0.8)], style, max(1, style['capCount'] // 3))
        for c in mains:
            caps += parallel_filaments([p for p in c if p[0] < ux(0.8)], style, seed + 7)
        caps = inside_v(caps)
    elif kind == 'tip':
        tx = ux(TIP_END)
        main = []
        for x in xs:
            if x > tx:
                break
            u = (x - MARGIN) / float(INNER)
            fade = 1.0 - sstep_f(0.2, 0.6, u)
            taper = (1.0 - sstep_f(0.2, TIP_END, u)) ** 0.8
            main.append((x, CELL / 2.0 + line.offset(x) * fade, max(HAIR * 0.8, line.width(x) * taper)))
        mains.append(main)
        ex, ey, _ = main[-1]
        for a in style['splay']:  # a fine capillary splay at the end, mirrored about the axis
            half = capillary(random.Random(seed * 7 + int(a * 100)), ex, ey, a, rng.uniform(32, 56), style, HAIR * 1.1, 1)
            caps += half + [[(x, 2 * ey - y, w) for x, y, w in p] for p in half]
        caps += side_caps(rng, main[:int(len(main) * 0.7)], style, max(2, style['capCount'] // 2))
        caps += parallel_filaments(main[:int(len(main) * 0.6)], style, seed + 7)
        caps = inside_v(caps, both=True)
    elif kind == 'burst':
        c = CELL / 2.0
        nr = PNoise(seed + 9, 3, 9, 1.0, period=2 * math.pi)
        node = [(c + NODE_R * (1 + 0.12 * nr(abs(a))) * math.cos(a), c + NODE_R * (1 + 0.12 * nr(abs(a))) * math.sin(a))
                for a in [math.pi * (i / 90.0 - 1.0) for i in range(180)]]
        jag = PNoise(seed + 5, 6, 40, style.get('jagSlope', 1.0))
        rays = [0.0, math.pi] + [s * math.radians(a + style['rayJitter'] * (rng.random() - 0.5)) for a in (42, 94, 140) for s in (1,)]
        rays = rays[:2] + [r for a in rays[2:] for r in (a, -a)]
        bend = style.get('rayBend', 0.0)
        for a in rays:
            sign = 0.0 if a in (0.0, math.pi) else (1.0 if a > 0 else -1.0)
            ray = []
            r = NODE_R * 0.5
            while r <= RAY_END:
                t = (r - NODE_R) / (RAY_END - NODE_R)
                off = sign * (jag(r * 1.7 + 13 * abs(a)) * style['jag'] * sstep_f(NODE_R, NODE_R + 30, r) + bend * max(0.0, t) ** 2 * INNER * 0.06)
                w = CORE_W * (1.05 + (0.3 - 1.05) * sstep_f(NODE_R * 0.7, RAY_END, r))
                ray.append((c + r * math.cos(a) - off * math.sin(a), c + r * math.sin(a) + off * math.cos(a), w))
                r += 2.0
            mains.append(ray)
            caps += side_caps(rng, ray[int(len(ray) * 0.3):int(len(ray) * 0.9)], style, max(1, style['capCount'] // 4))
        caps = inside_v(caps, both=True)
    else:
        raise ValueError(kind)
    return {'mains': mains, 'caps': caps, 'node': node}


class Canvas:
    """An antialiased 'L' raster of variable-width paths (SS x supersampled, box-filtered to the cell)."""

    def __init__(self):
        self.im = Image.new('L', (CELL * SS, CELL * SS), 0)
        self.d = ImageDraw.Draw(self.im)

    def path(self, pts, scale=1.0, add=0.0):
        k = SS
        for (x0, y0, w0), (x1, y1, w1) in zip(pts, pts[1:]):
            dx, dy = x1 - x0, y1 - y0
            ln = math.hypot(dx, dy) or 1e-6
            nx, ny = -dy / ln, dx / ln
            r0, r1 = (w0 * scale + add) / 2.0 * k, (w1 * scale + add) / 2.0 * k
            self.d.polygon([(x0 * k + nx * r0, y0 * k + ny * r0), (x1 * k + nx * r1, y1 * k + ny * r1),
                            (x1 * k - nx * r1, y1 * k - ny * r1), (x0 * k - nx * r0, y0 * k - ny * r0)], fill=255)
        for x, y, w in pts:
            r = (w * scale + add) / 2.0 * k
            if r >= 0.5:
                self.d.ellipse((x * k - r, y * k - r, x * k + r, y * k + r), fill=255)

    def poly(self, pts, scale=1.0):
        c = CELL / 2.0
        self.d.polygon([((c + (x - c) * scale) * SS, (c + (y - c) * scale) * SS) for x, y in pts], fill=255)

    def mask(self):
        return F(self.im.resize((CELL, CELL), Image.BOX)) * (1 / 255.0)


def vein_masks(cell, kind, style, seed):
    """The stamp's geometry masks, stored in cell.masks for the vein layers: core (the vein), rim (a band around it), axis
    (its centre line), caps (hairlines), node (the source burst's core), near (the halo's reach, for the vanilla crack
    detail) and halo (the low-alpha corruption stain). Edges break up along the parent's own detail."""
    sk = vein_skeleton(kind, style, seed)
    cv = {n: Canvas() for n in ('core', 'rim', 'axis', 'halo', 'caps', 'node')}
    for p in sk['mains']:
        cv['core'].path(p)
        cv['rim'].path(p, style['rimW'])
        cv['axis'].path(p, 0.3)
        cv['halo'].path(p, 3.5)
    for p in sk['caps']:
        cv['caps'].path(p)
        cv['halo'].path(p, 1.0, 10.0)
    if sk['node']:
        cv['node'].poly(sk['node'])
        cv['core'].poly(sk['node'])
        cv['rim'].poly(sk['node'], 1.0 + 0.25 * (style['rimW'] - 1.0))
        cv['halo'].poly(sk['node'], 1.6)
    d = cell.detail
    m = {n: c.mask() for n, c in cv.items()}
    core = sstep(0.3, 0.7, blur(m['core'], 1.2) + (d - 0.5) * 0.45)
    node = sstep(0.3, 0.7, blur(m['node'], 2.0) + (d - 0.5) * 0.45)
    rim = clamp01(sstep(0.25, 0.75, blur(m['rim'], 1.5) + (d - 0.5) * 0.5) - core)
    rim = rim * sstep(0.25, 0.6, fbm(26, seed + 11, 3) * 0.6 + d * 0.5)  # a broken, scorched edge, not a band
    caps = clamp01(m['caps'] * 1.25)
    reach = blur(m['halo'], 26.0)
    stain = clamp01(sstep(0.03, 0.75, reach) * (fbm(70, seed + 3, 3) * 0.7 + d * 0.6))
    cell.masks.update({'core': core, 'rim': rim, 'axis': clamp01(blur(m['axis'], 1.3) * 1.2), 'caps': caps, 'node': node,
                       'near': sstep(0.08, 0.45, reach), 'halo': stain})
    return sk


def feather(kind):
    """Optical-depth weights along u: alpha' = 1 - (1 - alpha)^f. A chained stamp's head ramp (u 0..0.2) and the previous
    stamp's tail ramp (u 0.8..1) sum to 1 at every texel, so the overlap composites to exactly one stamp's alpha."""
    row = Image.new('F', (CELL, 1))
    vals = []
    for x in range(CELL):
        u = (x + 0.5 - MARGIN) / float(INNER)
        f = 1.0
        if kind in ('trunk', 'fork', 'tip'):
            f *= sstep_f(0.0, 0.2, u)
        if kind in ('trunk', 'fork'):
            f *= 1.0 - sstep_f(0.8, 1.0, u)
        vals.append(f)
    row.putdata(vals)
    return O(row.resize((CELL, CELL), Image.NEAREST)), vals


def apply_feather(alpha, kind):
    """alpha (float operand) -> feathered 8-bit alpha. Pillow has no per-texel pow, and f depends on u only, so each column
    with f < 1 goes through its own 256-entry table."""
    a8 = to_l(MIN(alpha, 254.0 / 255.0))
    _, fs = feather(kind)
    for x, f in enumerate(fs):
        if f >= 1.0:
            continue
        col = a8.crop((x, 0, x + 1, CELL)).point([int(round((1.0 - (1.0 - i / 255.0) ** f) * 255.0)) for i in range(256)])
        a8.paste(col, (x, 0))
    return a8


def head_blend(img, head_src):
    """Paste head_src's u 0..0.2 block into img, fading into img's own content toward u = 0.2 (fork and tip start where a
    trunk's tail is)."""
    ramp = Image.linear_gradient('L').rotate(90, expand=True).transpose(Image.Transpose.FLIP_LEFT_RIGHT).resize((OVERLAP, CELL))
    box = (MARGIN, 0, MARGIN + OVERLAP, CELL)
    out = img.copy()
    out.paste(Image.composite(img.crop(box), head_src.crop(box), ramp), box[:2])
    return out


def periodic(img):
    """Make a trunk image periodic over PERIOD: its head block (u 0..0.2) becomes a crossfade of its own head and its tail,
    and the tail block a copy of the head, so stamp n's tail and stamp n+1's head are the same texels."""
    ramp = Image.linear_gradient('L').rotate(90, expand=True).transpose(Image.Transpose.FLIP_LEFT_RIGHT).resize((OVERLAP, CELL))
    head = img.crop((MARGIN, 0, MARGIN + OVERLAP, CELL))
    tail = img.crop((MARGIN + PERIOD, 0, MARGIN + PERIOD + OVERLAP, CELL))
    blend = Image.composite(head, tail, ramp)
    out = img.copy()
    out.paste(blend, (MARGIN, 0))
    out.paste(blend, (MARGIN + PERIOD, 0))
    return out


# ------------------------------------------------------------------ recipes
FILM = [(0.00, (236, 222, 200)), (0.18, (228, 182, 192)), (0.36, (184, 140, 222)), (0.52, (140, 146, 230)),
        (0.68, (122, 200, 222)), (0.84, (196, 222, 186)), (1.00, (236, 222, 200))]
OPAL = [(0.0, (150, 110, 214)), (0.3, (96, 140, 230)), (0.55, (112, 206, 222)), (0.8, (236, 176, 108)), (1.0, (172, 112, 210))]

# A recipe is a list of steps over the cell state. Step kinds:
#   base:   recolour the parent (tint, amount, gain) and scale its fine detail (detail)
#   layer:  blend a material over a mask. material: src ('tex:<name>[@scale]', 'base', 'color', 'film', 'opal', 'glow'),
#           tint/amount/gain recolour it (tint: the result's mean colour), soften lowers its detail contrast, color (+ dm
#           detail modulation) for flat paints, glow a fissure from color2 (rim) to color (core); mask: (kind, ...) with kinds
#           fbm (scale, thr, edge, jit), cavity (lo, hi), ridge (lo, hi), cracks and fractures (thr, width; the vanilla
#           cracked-mud network), flecks (density, cluster scale, cluster thr), clumps (diameter, density, cluster scale,
#           cluster thr), streaks (along, across, thr, edge), all; 'within' multiplies by an earlier named mask,
#           'within_dilated' by its grown neighbourhood, 'outside' by its complement; opacity; gloss/spec/ao (0..255 targets),
#           height (bump for the normal map), flatten (normal flattening).
RECIPES = {
    'weave': {
        'arid': [  # Glass mirages: pervasive opal glass sand, thin-film sheens, milky fused crusts, crystal flecks
            ('base', dict(tint=(232, 220, 214), amount=.8, detail=.9)),
            ('layer', dict(name='film', src='film', mask=('fbm', 60, .54, .07, .4), opacity=.5, gloss=120)),
            ('layer', dict(name='crust', src='base', blur=2.0, tint=(226, 218, 236), amount=.55, mask=('fbm', 40, .68, .04, .3), opacity=.75, gloss=135, flatten=.6)),
            ('layer', dict(src='opal', mask=('flecks', .05, 45, .58), opacity=.85, gloss=150, height=.4)),
        ],
        'verdant': [  # Possibility meadows: living-magic growth drifts through the meadow, prismatic sheen, flower specks
            ('base', dict(tint=(116, 146, 88), amount=.45)),
            ('layer', dict(name='drift', src='tex:ground_grass_stone_a_d', tint=(150, 134, 206), amount=.85, mask=('fbm', 70, .50, .05, .45), opacity=.85, gloss=70)),
            ('layer', dict(src='film', mask=('fbm', 30, .62, .05, .5), within='drift', opacity=.45, gloss=100)),
            ('layer', dict(src='color', color=(206, 92, 160), dm=.6, mask=('flecks', .035, 60, .55), opacity=.9, height=.3)),
            ('layer', dict(src='color', color=(226, 196, 84), dm=.6, mask=('flecks', .025, 50, .6), opacity=.9, height=.3)),
        ],
        'rock': [  # Floating stonefields: pale fractured stone, opalescent fracture interiors, ochre lichen
            ('base', dict(tint=(158, 154, 160), amount=.7)),
            ('layer', dict(src='tex:forest_a_d@4', tint=(150, 132, 72), amount=.8, mask=('fbm', 50, .66, .05, .5), opacity=.65)),
            ('layer', dict(name='seam', src='opal', mask=('fractures', .80, 2), opacity=.9, gloss=140, height=-.4)),
            ('layer', dict(src='opal', mask=('flecks', .04, 60, .6), within_dilated='seam', opacity=.85, gloss=150)),
        ],
        'cold': [  # Shifting frostlands: violet and teal living moss cushions, lavender frost feathers on the open ground
            ('base', dict(tint=(108, 110, 112), amount=.55)),
            ('layer', dict(name='moss', src='tex:ground_nord_grass_a_d@2', tint=(112, 92, 168), amount=.9, mask=('clumps', 12, 3.0, 60, .36), opacity=.9, height=1.4, ao=170)),
            ('layer', dict(src='tex:ground_nord_grass_a_d@2', tint=(70, 134, 128), amount=.9, mask=('fbm', 24, .54, .05, .5), within='moss', opacity=.6)),
            ('layer', dict(src='tex:snow_a_d@3', tint=(206, 196, 234), amount=.8, mask=('fbm', 36, .58, .06, .5), outside='moss', opacity=.7, gloss=110, flatten=.4)),
        ],
    },
    'umbral': {
        'arid': [  # Haunted tombs: ashen grey dust, dark cracked crust, bone-pale grit
            ('base', dict(tint=(140, 134, 128), amount=.85)),
            ('layer', dict(name='crust', src='tex:ground_soil_a_d', tint=(96, 88, 84), amount=.9, mask=('fbm', 80, .52, .05, .4), opacity=.85, height=-.2)),
            ('layer', dict(src='color', color=(36, 32, 32), dm=.3, mask=('cracks', .78, 2), within='crust', opacity=.85, height=-.5, ao=110)),
            ('layer', dict(src='color', color=(212, 206, 192), dm=.5, mask=('flecks', .02, 40, .5), opacity=.85, height=.3)),
        ],
        'verdant': [  # Ashen meadows: withered pale grey grass under ash, dead soil
            ('base', dict(tint=(148, 148, 142), amount=.92, detail=1.1)),
            ('layer', dict(name='dead', src='tex:ground_soil_a_d', tint=(96, 86, 78), amount=.9, mask=('fbm', 70, .58, .05, .5), opacity=.8)),
            ('layer', dict(src='tex:ashes_a_d@3', tint=(188, 184, 178), amount=.9, mask=('cavity', .40, .16), opacity=.7, flatten=.5, gloss=10)),
        ],
        'rock': [  # Ossuary crags: bleached bone-grey stone, dark seams
            ('base', dict(tint=(200, 192, 178), amount=.85, detail=.2)),
            ('layer', dict(name='seam', src='color', color=(70, 62, 58), dm=.4, mask=('fractures', .80, 2), opacity=.5, height=-.4, ao=110)),
            ('layer', dict(src='tex:ashes_a_d@3', tint=(116, 110, 106), amount=.8, mask=('cavity', .30, .10), opacity=.6)),
        ],
        'cold': [  # Deathfrost plains: blue-grey frost over dead grey moss, black rot patches
            ('base', dict(tint=(122, 124, 120), amount=.95)),
            ('layer', dict(src='tex:snow_a_d@4', tint=(158, 166, 178), amount=.9, mask=('fbm', 50, .5, .06, .5), opacity=.75, gloss=90, flatten=.4)),
            ('layer', dict(src='tex:ashes_a_d@3', tint=(52, 50, 52), amount=.9, mask=('fbm', 70, .68, .05, .5), opacity=.85)),
        ],
    },
    'infernal': {
        'arid': [  # Hellglass wastes: scorched red-brown sand, black glass shards, crimson veins
            ('base', dict(tint=(120, 80, 68), amount=.9)),
            ('layer', dict(name='glass', src='tex:ashes_a_d@2.5', tint=(46, 36, 40), amount=.85, mask=('fbm', 45, .60, .04, .4), opacity=.9, gloss=140, height=.3)),
            ('layer', dict(src='glow', color=(166, 52, 44), color2=(96, 36, 32), mask=('cracks', .78, 2), outside='glass', opacity=.95, height=-.4, gloss=60)),
        ],
        'verdant': [  # Cinder blight: charred ground, sickly red soil, crimson cracks
            ('base', dict(tint=(100, 86, 100), amount=.9)),
            ('layer', dict(name='soil', src='tex:ground_soil_a_d', tint=(130, 66, 58), amount=.9, mask=('fbm', 60, .55, .05, .5), opacity=.8)),
            ('layer', dict(src='tex:ashes_a_d@5', tint=(56, 50, 50), amount=.85, mask=('fbm', 40, .56, .05, .5), outside='soil', opacity=.85)),
            ('layer', dict(src='glow', color=(184, 46, 36), color2=(100, 30, 28), mask=('cracks', .74, 2), within='soil', opacity=.95, height=-.4)),
        ],
        'rock': [  # Demon spires: dark basalt-like stone with crimson veins
            ('base', dict(tint=(94, 82, 86), amount=.9)),
            ('layer', dict(src='glow', color=(190, 48, 38), color2=(104, 30, 30), mask=('fractures', .80, 2), opacity=.95, height=-.3, gloss=70)),
            ('layer', dict(src='tex:ashes_a_d@3', tint=(46, 40, 42), amount=.85, mask=('cavity', .32, .12), opacity=.7, gloss=110)),
        ],
        'cold': [  # Torment barrens: soot-stained frozen ground, rust-red patches, dark cracks
            ('base', dict(tint=(130, 102, 106), amount=.9)),
            ('layer', dict(name='rust', src='tex:ground_soil_a_d', tint=(132, 72, 58), amount=.9, mask=('fbm', 60, .56, .05, .5), opacity=.8, gloss=40)),
            ('layer', dict(src='tex:ashes_a_d@3', tint=(58, 52, 54), amount=.85, mask=('fbm', 40, .62, .05, .5), outside='rust', opacity=.8)),
            ('layer', dict(src='color', color=(44, 30, 30), dm=.3, mask=('cracks', .78, 2), opacity=.8, height=-.4)),
        ],
    },
    'radiant': {
        'arid': [  # Golden sanctuaries: smooth pale gold sand, mineral crust lines
            ('base', dict(tint=(236, 212, 148), amount=.85, detail=.7)),
            ('layer', dict(name='crust', src='tex:desert_floor_c_d', tint=(246, 232, 196), amount=.9, mask=('fbm', 70, .6, .05, .3), opacity=.6, gloss=100, flatten=.4)),
            ('layer', dict(src='color', color=(208, 168, 74), dm=.5, mask=('cracks', .78, 2), within='crust', opacity=.8, gloss=120)),
        ],
        'verdant': [  # Healing meadows: lush warm yellow-green grass, white and gold flowers
            ('base', dict(tint=(156, 178, 92), amount=.85)),
            ('layer', dict(src='tex:ground_grass_stone_a_d', tint=(176, 194, 104), amount=.8, mask=('fbm', 60, .55, .05, .4), opacity=.6)),
            ('layer', dict(src='color', color=(244, 240, 220), dm=.5, mask=('flecks', .03, 50, .5), opacity=.9, height=.3)),
            ('layer', dict(src='color', color=(236, 200, 96), dm=.5, mask=('flecks', .03, 40, .55), opacity=.9, height=.3)),
        ],
        'rock': [  # Hallowed heights: pale chalky stone, gold mineral crusts
            ('base', dict(tint=(216, 208, 188), amount=.9)),
            ('layer', dict(name='vein', src='color', color=(206, 172, 88), dm=.5, mask=('fractures', .80, 2), opacity=.85, gloss=120, height=.2)),
            ('layer', dict(src='tex:forest_a_d@4', tint=(200, 186, 124), amount=.8, mask=('fbm', 50, .66, .05, .5), opacity=.5)),
        ],
        'cold': [  # Peaceful frostlands: warm cream frost and golden moss
            ('base', dict(tint=(196, 184, 128), amount=.85)),
            ('layer', dict(name='moss', src='tex:ground_nord_grass_a_d@2', tint=(206, 174, 82), amount=.85, mask=('clumps', 10, 2.2, 55, .45), opacity=.9, height=1.2, ao=170)),
            ('layer', dict(src='tex:snow_a_d@4', tint=(242, 232, 204), amount=.7, mask=('fbm', 40, .6, .05, .5), outside='moss', opacity=.7, gloss=90, flatten=.4)),
        ],
    },
    'fire': {
        'arid': [  # Furnace dunes: heat-baked rust crust plates, ash in the hollows, charcoal grit
            ('base', dict(tint=(214, 150, 104), amount=.7)),
            ('layer', dict(name='crust', src='tex:ground_soil_a_d', tint=(176, 116, 82), amount=.9, mask=('fbm', 70, .43, .025, .25), opacity=.95, height=.2)),
            ('layer', dict(src='tex:ashes_a_d@3', tint=(184, 176, 164), amount=.9, mask=('cavity', .40, .15), opacity=.75, flatten=.5, gloss=8)),
            ('layer', dict(src='color', color=(54, 48, 43), dm=.3, mask=('flecks', .02, 60, .5), opacity=.8)),
        ],
        'verdant': [  # Flamegrass plains: grass cured rust-orange by heat, burnt-down patches with ash, ember specks
            ('base', dict(tint=(212, 140, 106), amount=.9)),
            ('layer', dict(name='burn', src='tex:ashes_a_d@3', tint=(70, 62, 60), amount=.85, mask=('fbm', 80, .62, .05, .45), opacity=.85)),
            ('layer', dict(src='tex:ashes_a_d@3', tint=(172, 166, 160), amount=.9, mask=('cavity', .40, .16), within='burn', opacity=.7, flatten=.5)),
            ('layer', dict(src='color', color=(232, 118, 44), dm=.4, mask=('flecks', .03, 40, .5), within_dilated='burn', opacity=.9)),
        ],
        'rock': [  # Molten crags: dark scorched stone with glowing orange veins, ash
            ('base', dict(tint=(92, 66, 56), amount=.9)),
            ('layer', dict(src='glow', color=(236, 128, 46), color2=(146, 54, 26), mask=('fractures', .80, 2), opacity=.95, height=-.3, gloss=80)),
            ('layer', dict(src='tex:ashes_a_d@3', tint=(150, 144, 138), amount=.85, mask=('cavity', .30, .12), opacity=.6, flatten=.4)),
        ],
        'cold': [  # Ashfrost plains: grey ash over scorched, thawed tundra
            ('base', dict(tint=(134, 120, 112), amount=.85, detail=.8)),
            ('layer', dict(name='ash', src='tex:ashes_a_d@3', tint=(144, 140, 136), amount=.9, soften=.5, mask=('fbm', 60, .42, .05, .5), opacity=.85, flatten=.5, gloss=30)),
            ('layer', dict(src='color', color=(60, 50, 46), dm=.3, mask=('flecks', .025, 50, .5), within='ash', opacity=.8)),
        ],
    },
    'water': {
        'arid': [  # Frostglass dunes: blue-white rime crust over sand, glassy ice patches
            ('base', dict(tint=(196, 210, 220), amount=.85)),
            ('layer', dict(name='ice', src='tex:ice_a_d@5', tint=(150, 192, 222), amount=.8, mask=('fbm', 60, .58, .05, .4), opacity=.85, gloss=150, flatten=.6)),
            ('layer', dict(src='color', color=(234, 242, 248), dm=.5, mask=('flecks', .03, 40, .5), outside='ice', opacity=.8, gloss=130)),
        ],
        'verdant': [  # Dew meadows: saturated wet blue-green grass, pooled water sheen
            ('base', dict(tint=(70, 112, 102), amount=.85)),
            ('layer', dict(name='pool', src='tex:ground_mud_b_d', tint=(62, 88, 104), amount=.9, mask=('cavity', .42, .18), opacity=.85, gloss=150, flatten=.8)),
            ('layer', dict(src='color', color=(150, 204, 218), dm=.4, mask=('flecks', .03, 40, .55), outside='pool', opacity=.85, gloss=140)),
        ],
        'rock': [  # Springstone cliffs: dark wet stone, blue-teal mineral seams, moss
            ('base', dict(tint=(80, 94, 102), amount=.85)),
            ('layer', dict(src='color', color=(80, 164, 184), dm=.5, mask=('fractures', .80, 2), opacity=.9, gloss=140, height=-.3)),
            ('layer', dict(src='tex:forest_a_d@4', tint=(56, 96, 80), amount=.85, mask=('fbm', 50, .62, .05, .5), opacity=.7)),
        ],
        'cold': [  # Bluefrost tundra: blue ice crust and rime
            ('base', dict(tint=(110, 132, 140), amount=.8)),
            ('layer', dict(name='ice', src='tex:ice_a_d@5', tint=(160, 196, 222), amount=.85, mask=('fbm', 60, .5, .05, .4), opacity=.85, gloss=150, flatten=.7)),
            ('layer', dict(src='color', color=(226, 238, 248), dm=.5, mask=('cracks', .76, 2), within='ice', opacity=.6, gloss=150)),
            ('layer', dict(src='color', color=(224, 238, 248), dm=.5, mask=('flecks', .03, 40, .5), within='ice', opacity=.8, gloss=130)),
        ],
    },
    'earth': {
        'arid': [  # Blooming dunes: green growth and dark loam spreading through the sand
            ('base', dict(tint=(184, 162, 112), amount=.6)),
            ('layer', dict(name='loam', src='tex:ground_soil_a_d', tint=(100, 82, 60), amount=.85, mask=('fbm', 60, .44, .05, .4), opacity=.85)),
            ('layer', dict(src='tex:ground_grass_stone_a_d', tint=(86, 126, 50), amount=.85, mask=('fbm', 40, .46, .05, .5), within='loam', opacity=.9, height=.4)),
        ],
        'verdant': [  # Titan meadows: deep saturated green, root ridges, rich dark loam
            ('base', dict(tint=(64, 102, 68), amount=.9)),
            ('layer', dict(src='tex:forest_a_d', tint=(56, 90, 56), amount=.85, mask=('fbm', 60, .48, .05, .5), opacity=.8)),
            ('layer', dict(src='color', color=(84, 64, 46), dm=.5, mask=('fractures', .80, 2), opacity=.6, height=.5)),
        ],
        'rock': [  # Living monoliths: stone overgrown with moss and lichen
            ('base', dict(tint=(120, 126, 108), amount=.7)),
            ('layer', dict(name='moss', src='tex:ground_nord_grass_a_d@3', tint=(80, 114, 50), amount=.9, mask=('fbm', 55, .45, .05, .5), opacity=.9, height=.3)),
            ('layer', dict(src='tex:forest_a_d@4', tint=(160, 146, 72), amount=.85, mask=('fbm', 40, .66, .05, .5), outside='moss', opacity=.8)),
        ],
        'cold': [  # Lichenstone plains: ochre and green lichen over stone
            ('base', dict(tint=(126, 128, 106), amount=.8)),
            ('layer', dict(name='lichen', src='tex:forest_a_d@4', tint=(170, 150, 70), amount=.9, mask=('fbm', 50, .52, .05, .5), opacity=.85, gloss=30)),
            ('layer', dict(src='tex:ground_nord_grass_a_d@3', tint=(94, 124, 60), amount=.85, mask=('fbm', 35, .58, .05, .5), outside='lichen', opacity=.8)),
        ],
    },
    'air': {
        'arid': [  # Whistling dunes: wind-scoured pale grey-cyan sand with streaks of exposed grit
            ('base', dict(tint=(204, 210, 204), amount=.85)),
            ('layer', dict(src='tex:desert_floor_c_d', tint=(150, 158, 158), amount=.85, mask=('streaks', 200, 10, .56, .05), opacity=.8, height=-.3)),
            ('layer', dict(src='tex:snow_a_d@4', tint=(224, 234, 234), amount=.7, mask=('streaks', 160, 14, .62, .06), opacity=.6)),
        ],
        'verdant': [  # Thundergrass plains: wind-combed silver-green grass streaks
            ('base', dict(tint=(150, 170, 150), amount=.85)),
            ('layer', dict(src='base', tint=(188, 204, 194), amount=.8, mask=('streaks', 200, 12, .55, .05), opacity=.8)),
            ('layer', dict(src='base', tint=(92, 110, 96), amount=.8, mask=('streaks', 160, 10, .60, .05), opacity=.7, height=-.2)),
        ],
        'rock': [  # Windcarved spires: pale scoured stone with streak grooves
            ('base', dict(tint=(186, 194, 198), amount=.85, detail=.8)),
            ('layer', dict(src='base', tint=(122, 130, 136), amount=.8, mask=('streaks', 220, 8, .58, .05), opacity=.8, height=-.4)),
        ],
        'cold': [  # Force-swept tundra: scoured blue-grey ground, snow streaks
            ('base', dict(tint=(126, 138, 146), amount=.85, detail=.7)),
            ('layer', dict(src='tex:snow_a_d@4', tint=(220, 228, 234), amount=.7, mask=('streaks', 200, 12, .55, .05), opacity=.6, gloss=100, flatten=.4)),
        ],
    },
}

# MMR-288 vein looks. The recipe above is the corrupted ground of the halo; these layers paint the vein over it. style shapes
# the skeleton: jag (texels of fine wobble; angular: straight runs between knots), caps (capillary mode crack / smooth /
# angular / filament), capCount per trunk period, capLen, capBranch (chance of one sub-branch), splay (tip end angles, rad),
# rayJitter (burst ray angles, deg), rayBend, rimW (rim band width / core width), parallel (air: hairlines beside the core).
# alpha gives each geometry mask's opacity; the stamp's alpha is their maximum (halo <= HALO_ALPHA_MAX).
# Mask names: core, rim, axis, caps, node, near (halo reach), halo; vcracks and glints are named by the steps that make them.
VEIN_STYLE = dict(jag=3.0, jagSlope=1.0, angular=0, caps='crack', capCount=9, capLen=(24, 60), capBranch=0.45,
                  splay=(0.22, 0.6), rayJitter=10, rayBend=0.4, rimW=1.7, parallel=())
VEIN_ALPHA = dict(core=1.0, node=1.0, rim=0.85, caps=0.85, vcracks=0.55, glints=1.0, halo=0.32)
VEINS = {
    'fire': {  # glowing ember cracks in scorched ground
        'style': dict(jag=3.5, caps='crack'),
        'layers': [
            ('layer', dict(src='tex:ashes_a_d@3', tint=(66, 52, 46), amount=.9, mask=('geo', 'halo'), opacity=.85, gloss=20)),  # the corruption stain
            ('layer', dict(name='vcracks', src='tex:ashes_a_d@2', tint=(54, 42, 36), amount=.9, mask=('cracks', .80, 2), within='near', opacity=.9, height=-.3, gloss=20)),
            ('layer', dict(src='tex:ashes_a_d@2', tint=(44, 34, 30), amount=.9, mask=('geo', 'rim'), opacity=.95, height=.25, gloss=25)),
            ('layer', dict(src='glow', color=(244, 144, 70), color2=(156, 66, 36), dm=.5, mask=('geo', 'caps'), opacity=.95, height=-.3, gloss=60)),
            ('layer', dict(src='glow', color=(252, 180, 92), color2=(166, 64, 34), hot='axis', dm=.5, mask=('geo', 'core'), opacity=1.0, height=-.8, gloss=70)),
            ('layer', dict(src='glow', color=(255, 220, 150), color2=(226, 120, 60), hot='node', dm=.4, mask=('geo', 'node'), opacity=1.0, height=-.2, gloss=80)),
            ('layer', dict(src='color', color=(150, 46, 18), dm=.4, mask=('cracks', .76, 3), within='node', opacity=.6)),  # a cracked source
        ]},
    'infernal': {  # molten crimson cracks with ash
        'style': dict(jag=4.0, caps='crack', capBranch=0.55),
        'alpha': dict(rim=0.95),
        'layers': [
            ('layer', dict(src='tex:ashes_a_d@3', tint=(150, 134, 128), amount=.9, mask=('geo', 'halo'), opacity=.85, gloss=30)),  # the corruption stain
            ('layer', dict(name='vcracks', src='tex:ashes_a_d@2', tint=(150, 142, 140), amount=.9, mask=('cracks', .80, 2), within='near', opacity=.85, height=-.1, gloss=15)),
            ('layer', dict(src='tex:ashes_a_d@2', tint=(32, 26, 28), amount=.95, mask=('geo', 'rim'), opacity=1.0, height=.15, gloss=110)),
            ('layer', dict(src='color', color=(176, 168, 164), dm=.5, mask=('flecks', .03, 30, .4), within='rim', opacity=.9, height=.1)),
            ('layer', dict(src='glow', color=(214, 52, 38), color2=(112, 22, 22), dm=.5, mask=('geo', 'caps'), opacity=.95, height=-.3, gloss=70)),
            ('layer', dict(src='glow', color=(236, 84, 60), color2=(120, 28, 30), hot='axis', dm=.5, mask=('geo', 'core'), opacity=1.0, height=-.35, gloss=90)),
            ('layer', dict(src='glow', color=(250, 142, 100), color2=(160, 44, 40), hot='node', dm=.4, mask=('geo', 'node'), opacity=1.0, height=0.0, gloss=100)),
            ('layer', dict(src='color', color=(110, 18, 20), dm=.4, mask=('cracks', .76, 3), within='node', opacity=.6)),  # a cracked source
        ]},
    'weave': {  # pearl / opal glass threads
        'style': dict(jag=1.2, jagSlope=1.6, caps='smooth', capCount=8, capLen=(40, 90), capBranch=0.35, rimW=1.45),
        'alpha': dict(rim=0.8, vcracks=0.5),
        'layers': [
            ('layer', dict(src='tex:snow_a_d@3', tint=(196, 180, 226), amount=.85, mask=('geo', 'halo'), opacity=.85, gloss=110)),  # the corruption stain
            ('layer', dict(name='vcracks', src='color', color=(196, 170, 230), dm=.4, mask=('cracks', .80, 2), within='near', opacity=.8, gloss=120)),
            ('layer', dict(src='tex:ice_a_d@3', tint=(226, 218, 236), amount=.85, mask=('geo', 'rim'), opacity=.9, height=.2, gloss=140)),
            ('layer', dict(src='film', mask=('geo', 'caps'), opacity=.95, height=.3, gloss=165)),
            ('layer', dict(src='opal', mask=('geo', 'core'), opacity=1.0, height=.6, gloss=175)),
            ('layer', dict(src='tex:ice_a_d@3', tint=(232, 224, 242), amount=.85, mask=('fbm', 40, .5, .08, .5), within='core', opacity=.5, gloss=185)),  # pearl
            ('layer', dict(src='color', color=(246, 240, 252), dm=.3, mask=('geo', 'axis'), within='core', opacity=.75, gloss=195)),
            ('layer', dict(src='film', mask=('geo', 'node'), opacity=1.0, height=.4, gloss=185)),
            ('layer', dict(src='color', color=(250, 246, 255), dm=.2, mask=('fbm', 30, .55, .05, .4), within='node', opacity=.6, gloss=200)),
            ('layer', dict(src='color', color=(120, 150, 220), dm=.4, mask=('cracks', .76, 3), within='node', opacity=.6)),  # a cracked source
        ]},
    'radiant': {  # white-gold crystalline light seams
        'style': dict(jag=2.5, angular=32, caps='angular', capCount=8, capLen=(24, 56), capBranch=0.3, rimW=1.5),
        'alpha': dict(rim=0.75, vcracks=0.45),
        'layers': [
            ('layer', dict(src='tex:snow_a_d@3', tint=(232, 214, 156), amount=.85, mask=('geo', 'halo'), opacity=.85, gloss=90)),  # the corruption stain
            ('layer', dict(name='vcracks', src='color', color=(232, 204, 132), dm=.4, mask=('cracks', .80, 2), within='near', opacity=.8, gloss=130)),
            ('layer', dict(src='tex:snow_a_d@3', tint=(240, 226, 184), amount=.85, mask=('geo', 'rim'), opacity=.9, height=.15, gloss=130)),
            ('layer', dict(src='color', color=(252, 238, 186), dm=.3, mask=('geo', 'caps'), opacity=1.0, height=.3, gloss=170)),
            ('layer', dict(src='tex:ice_a_d@3', tint=(220, 176, 78), amount=.9, mask=('geo', 'core'), opacity=1.0, height=.5, gloss=180)),
            ('layer', dict(src='color', color=(255, 248, 224), dm=.2, mask=('geo', 'axis'), within='core', opacity=.9, gloss=200)),
            ('layer', dict(name='glints', src='color', color=(255, 255, 244), dm=.2, mask=('flecks', .06, 30, .35), within='core', opacity=1.0, gloss=210)),
            ('layer', dict(src='glow', color=(255, 250, 232), color2=(232, 194, 96), hot='node', mask=('geo', 'node'), opacity=1.0, height=.3, gloss=200)),
            ('layer', dict(src='color', color=(214, 170, 70), dm=.4, mask=('cracks', .76, 3), within='node', opacity=.6)),  # a cracked source
        ]},
    'umbral': {  # ink-black veins with violet rims
        'style': dict(jag=1.6, jagSlope=1.4, caps='smooth', capCount=9, capLen=(36, 80), capBranch=0.5, rimW=1.8),
        'alpha': dict(rim=0.9),
        'layers': [
            ('layer', dict(src='tex:ashes_a_d@3', tint=(132, 122, 146), amount=.9, mask=('geo', 'halo'), opacity=.85, gloss=60)),  # the corruption stain
            ('layer', dict(name='vcracks', src='color', color=(46, 36, 54), dm=.3, mask=('cracks', .80, 2), within='near', opacity=.85, height=-.2)),
            ('layer', dict(src='tex:ashes_a_d@3', tint=(126, 76, 170), amount=.9, mask=('geo', 'rim'), opacity=.95, height=-.1, gloss=90)),
            ('layer', dict(src='color', color=(28, 22, 32), dm=.3, mask=('geo', 'caps'), opacity=1.0, height=-.3, gloss=110)),
            ('layer', dict(src='tex:ashes_a_d@3', tint=(34, 28, 40), amount=.95, mask=('geo', 'core'), opacity=1.0, height=-.6, gloss=120)),
            ('layer', dict(src='glow', color=(152, 98, 204), color2=(26, 20, 32), hot='node', mask=('geo', 'node'), opacity=1.0, height=-.3, gloss=130)),
            ('layer', dict(src='color', color=(30, 24, 36), dm=.4, mask=('cracks', .76, 3), within='node', opacity=.6)),  # a cracked source
        ]},
    'earth': {  # deep fissures with amber / geode glints
        'style': dict(jag=4.0, caps='crack', capCount=8, capLen=(20, 50), capBranch=0.4, rimW=1.9),
        'alpha': dict(rim=0.85),
        'layers': [
            ('layer', dict(src='tex:ground_soil_a_d', tint=(140, 116, 84), amount=.9, mask=('geo', 'halo'), opacity=.85)),  # the corruption stain
            ('layer', dict(name='vcracks', src='color', color=(62, 46, 32), dm=.4, mask=('cracks', .80, 2), within='near', opacity=.85, height=-.3)),
            ('layer', dict(src='tex:ground_soil_a_d', tint=(124, 94, 60), amount=.9, mask=('geo', 'rim'), opacity=.95, height=.45, gloss=30)),
            ('layer', dict(src='color', color=(48, 34, 24), dm=.4, mask=('geo', 'caps'), opacity=1.0, height=-.5)),
            ('layer', dict(src='tex:ground_soil_a_d', tint=(22, 16, 12), amount=.95, mask=('geo', 'core'), opacity=1.0, height=-1.0, ao=90)),
            ('layer', dict(name='glints', src='color', color=(242, 164, 48), dm=.3, mask=('flecks', .04, 30, .35), within='core', opacity=1.0, gloss=150, height=.2)),
            ('layer', dict(src='color', color=(150, 96, 196), dm=.3, mask=('flecks', .02, 40, .45), within='core', opacity=1.0, gloss=160, height=.2)),
            ('layer', dict(src='tex:ground_soil_a_d', tint=(44, 32, 22), amount=.95, mask=('geo', 'node'), opacity=1.0, height=-.2, ao=90)),
            ('layer', dict(src='color', color=(238, 164, 62), dm=.3, mask=('cracks', .76, 3), within='node', opacity=.85, gloss=150)),  # a cracked source
        ]},
    'water': {  # brine-teal crystal frost veins
        'style': dict(jag=2.0, angular=24, caps='angular', capCount=12, capLen=(20, 46), capBranch=0.6, rimW=1.8),
        'alpha': dict(rim=0.8, vcracks=0.5),
        'layers': [
            ('layer', dict(src='tex:snow_a_d@3', tint=(140, 190, 198), amount=.85, mask=('geo', 'halo'), opacity=.85, gloss=120)),  # the corruption stain
            ('layer', dict(name='vcracks', src='color', color=(196, 230, 238), dm=.3, mask=('cracks', .80, 2), within='near', opacity=.8, gloss=140)),
            ('layer', dict(src='tex:snow_a_d@3', tint=(222, 238, 244), amount=.85, mask=('geo', 'rim'), opacity=.9, height=.2, gloss=150)),
            ('layer', dict(src='tex:ice_a_d@3', tint=(206, 236, 242), amount=.85, mask=('geo', 'caps'), opacity=1.0, height=.3, gloss=170)),
            ('layer', dict(src='tex:ice_a_d@3', tint=(50, 142, 152), amount=.9, mask=('geo', 'core'), opacity=1.0, height=.4, gloss=190)),
            ('layer', dict(src='color', color=(146, 226, 228), dm=.3, mask=('geo', 'axis'), within='core', opacity=.8, gloss=200)),
            ('layer', dict(src='glow', color=(176, 240, 240), color2=(40, 128, 146), hot='node', mask=('geo', 'node'), opacity=1.0, height=.3, gloss=200)),
            ('layer', dict(src='color', color=(40, 120, 136), dm=.4, mask=('cracks', .76, 3), within='node', opacity=.6)),  # a cracked source
        ]},
    'air': {  # pale wind-scoured streaks with silver filaments
        'style': dict(jag=1.0, jagSlope=1.8, caps='filament', capCount=6, capBranch=0.15, rimW=1.6, parallel=(5.0, 13.0), rayBend=0.0),
        'alpha': dict(rim=0.7, caps=0.95, vcracks=0.4),
        'layers': [
            ('layer', dict(src='tex:desert_floor_c_d', tint=(204, 214, 220), amount=.85, mask=('geo', 'halo'), opacity=.85)),  # the corruption stain
            ('layer', dict(name='vcracks', src='color', color=(206, 216, 222), dm=.3, mask=('streaks', 200, 8, .6, .05), within='near', opacity=.7)),
            ('layer', dict(src='tex:desert_floor_c_d', tint=(132, 146, 156), amount=.85, mask=('geo', 'rim'), opacity=.85, height=-.2)),
            ('layer', dict(src='color', color=(238, 244, 250), dm=.2, mask=('geo', 'caps'), opacity=1.0, height=.2, gloss=180)),
            ('layer', dict(src='tex:snow_a_d@4', tint=(140, 188, 204), amount=.9, mask=('geo', 'core'), opacity=1.0, height=-.3, gloss=90)),
            ('layer', dict(src='color', color=(244, 248, 252), dm=.2, mask=('geo', 'axis'), within='core', opacity=.85, gloss=190)),
            ('layer', dict(src='glow', color=(214, 236, 244), color2=(118, 170, 192), hot='node', mask=('geo', 'node'), opacity=1.0, height=-.1, gloss=170)),
            ('layer', dict(src='color', color=(150, 180, 192), dm=.4, mask=('cracks', .76, 3), within='node', opacity=.6)),  # a cracked source
        ]},
}


def vein_style(school):
    return dict(VEIN_STYLE, **VEINS[school]['style'])


def vein_alpha(school):
    return dict(VEIN_ALPHA, **VEINS[school].get('alpha', {}))


class Cell:
    """One stamp: material, height for normals, specular, alpha."""

    def __init__(self, layers, group, seed):
        self.seed = seed
        p = layers[group['parent']]
        self.scale = p['scale']
        rng = random.Random(seed)
        self.off = (rng.randrange(4096), rng.randrange(4096))
        self.rgb = tiled_rgb(p['diffuse'], self.scale, self.off)
        self.parent_rgb = list(self.rgb)
        L = lum(*self.rgb)
        self.detail = hpn(L, 2.5, 3.0)
        self.height = norm_q(tiled_l(p['height'], self.scale, self.off)) if p['height'] != 'none' else norm_q(blur(L, 1.0))
        self.nx, self.ny = tiled_normal(p['normal'], self.scale, self.off)
        sr, sg, sb = tiled_rgb(p['specular'], self.scale, self.off) if p['specular'] != 'none' else (const(.05), const(.12), const(1.0))
        self.spec, self.gloss, self.ao = sr, sg, sb
        self.parent_n = Image.merge('RGB', [to_l(self.nx * 0.5 + 0.5), to_l(self.ny * 0.5 + 0.5), Image.new('L', (CELL, CELL), 255)])
        self.parent_s = Image.merge('RGB', [to_l(sr), to_l(sg), to_l(sb)])
        self.add_h = const(0.0)
        self.flat = const(0.0)
        self.masks = {}
        self.coverage = const(0.0)

    def rand(self, k):
        return (self.seed * 7919 + k * 104729) % 2147483647


def make_mask(cell, spec, k):
    kind = spec[0]
    d = cell.detail
    if kind == 'all':
        return const(1.0)
    if kind == 'geo':  # a vein geometry mask (vein_masks)
        return cell.masks[spec[1]]
    if kind == 'fbm':
        _, scale, thr, edge, jit = spec
        # edges broken up by the ground's own detail, like a height blend, not a cut-out
        return jmask(fbm(scale, cell.rand(k), 4), thr, d, jit + 0.15, edge * 1.6)
    if kind == 'cavity':
        _, lo, hi = spec  # lo > hi: high at low ground
        return sstep(lo, hi, blur(cell.height, 2.0) + (d - 0.5) * 0.1)
    if kind == 'ridge':
        _, lo, hi = spec
        return sstep(lo, hi, blur(cell.height, 2.0))
    if kind in ('cracks', 'fractures'):
        # the crack network of the vanilla cracked-mud decal (mainmap_cracked_mud_a: decal_b_d), turned per cell. cracks: its
        # cracked centre over the whole cell (crack cells about 1.2 map units); fractures: a smaller window, so fewer, wider
        # and longer cracks (cells about 3 units). thr is the crack darkness relative to the local mean.
        _, thr, width = spec
        src = load('decal_b_d').convert('L')
        size = src.size[0]
        half = size * (0.29 if kind == 'cracks' else 0.12)
        t = cell.rand(k) % 8
        jx = (cell.rand(k + 2) % 200 - 100) if kind == 'fractures' else 0
        jy = (cell.rand(k + 3) % 200 - 100) if kind == 'fractures' else 0
        cx, cy = size / 2.0 + jx, size / 2.0 + jy
        im = src.crop((int(cx - half), int(cy - half), int(cx + half), int(cy + half))).resize((CELL, CELL), Image.BICUBIC if 2 * half < CELL else Image.BOX)
        for _ in range(t % 4):
            im = im.transpose(Image.Transpose.ROTATE_90)
        if t >= 4:
            im = im.transpose(Image.Transpose.FLIP_LEFT_RIGHT)
        L = blur(F(im) * (1 / 255.0), 1.2)
        rel = L / MAX(blur(L, 8.0 if kind == 'cracks' else 14.0), 0.05)
        lines = sstep(thr + 0.06 * width, thr - 0.06, rel)
        return clamp01(blur(lines, 0.7))
    if kind == 'flecks':
        # sparse clustered accents: round dots of two sizes, 4 to 7 px across (minimum accent size 4 px)
        _, density, cscale, cthr = spec
        cluster = sstep(cthr, cthr + 0.18, noise(cscale, cell.rand(k)))
        return clamp01(MAX(dots(2.0, density * 0.1, cell.rand(k + 1)), dots(3.2, density * 0.04, cell.rand(k + 2))) * cluster)
    if kind == 'clumps':
        # rounded cushions (moss, growth): large soft dots packed inside a cluster field, about diameter px across
        _, diameter, density, cscale, cthr = spec
        cluster = sstep(cthr, cthr + 0.2, fbm(cscale, cell.rand(k), 3) + (d - 0.5) * 0.2)
        return clamp01(blur(dots(diameter / 2.0, density * 0.002, cell.rand(k + 1)), 1.5) * cluster)
    if kind == 'streaks':
        _, along, across, thr, edge = spec
        # wind streaks broken by coarser isotropic noise and the ground's detail, so they read as scouring, not stripes
        s = streak_noise(along, across, cell.rand(k)) * 0.7 + noise(across * 4, cell.rand(k + 1)) * 0.3
        return jmask(s, thr, d, 0.5, edge * 2.0)
    raise ValueError(kind)


def material(cell, st, k):
    src = st.get('src', 'base')
    if src == 'base':
        m = list(cell.rgb)
        if st.get('blur'):
            m = [blur(c, st['blur']) for c in m]
    elif src.startswith('tex:'):
        name, _, sc = src[4:].partition('@')
        m = tiled_rgb(name, float(sc) if sc else 5.0, (cell.rand(k) % 2048, cell.rand(k + 3) % 2048))
    elif src == 'color':
        dm = st.get('dm', 0.5)
        mod = (cell.detail - 0.5) * dm + 1.0
        m = [mod * v for v in c255(st['color'])]
    elif src in ('film', 'opal'):
        if src == 'film':
            t = clamp01(cell.height * 0.35 + noise(60, cell.rand(k)) * 0.55 + noise(14, cell.rand(k + 1)) * 0.10)
        else:  # opal: fine play of colour
            t = clamp01(cell.height * 0.25 + noise(20, cell.rand(k)) * 0.45 + noise(6, cell.rand(k + 1)) * 0.30)
        keys = FILM if src == 'film' else OPAL
        f = ramp_lut(t, keys)
        base_l = lum(*cell.rgb)
        fl = MAX(lum(*f), 0.02)
        m = [lerp(f[i], base_l * (f[i] / fl) * 1.05, 0.5) for i in range(3)] if src == 'film' else [c * (cell.detail * 0.5 + 0.75) for c in f]
    elif src == 'glow':
        m = None  # resolved in apply(): colour ramps across the mask from color2 (rim) to color (core)
    else:
        raise ValueError(src)
    if m is not None and st.get('soften'):
        m = [lerp(c, blur(c, 3.0), st['soften']) for c in m]
    if m is not None and 'tint' in st:
        m = tint(m, c255(st['tint']), st.get('amount', 1.0), st.get('gain', 1.0))
    return m


def apply(cell, recipe, k0=0):
    for k, (kind, st) in enumerate(recipe, k0):
        if kind == 'base':
            if st.get('detail', 1.0) != 1.0:
                L = lum(*cell.rgb)
                lb = blur(L, 3.0)
                fac = (L - lb) * (st['detail'] - 1.0)
                cell.rgb = [c + fac for c in cell.rgb]
            cell.rgb = tint(cell.rgb, c255(st['tint']), st.get('amount', 1.0), st.get('gain', 1.0))
            continue
        m = make_mask(cell, st['mask'], 10 * (k + 1))
        if 'within' in st:
            m = m * cell.masks[st['within']]
        if 'within_dilated' in st:
            m = m * clamp01(blur(dilate(cell.masks[st['within_dilated']], 6), 4.0) * 1.5)
        if 'outside' in st:
            m = m * (cell.masks[st['outside']] * -1.0 + 1.0)
        if st.get('name'):
            cell.masks[st['name']] = m
        a = m * st.get('opacity', 1.0)
        if st['src'] == 'glow':
            # a fissure: dark rim, hot core broken up by the ground's own detail and fine noise; hot names the mask that
            # carries the hot centre (a vein's axis), otherwise the step's own mask
            core = sstep(0.35, 0.95, cell.masks[st['hot']] if 'hot' in st else m) * (noise(10, cell.rand(k)) * 0.6 + 0.7)
            rim, hot = c255(st['color2']), c255(st['color'])
            mod = (cell.detail - 0.5) * st.get('dm', 0.8) + 1.0
            mat = [mod * lerp(const(rim[i]), const(hot[i]), clamp01(core)) for i in range(3)]
        else:
            mat = material(cell, st, 100 * (k + 1))
        cell.rgb = mix(cell.rgb, mat, a)
        if 'gloss' in st:
            cell.gloss = lerp(cell.gloss, const(st['gloss'] / 255.0), a)
        if 'spec' in st:
            cell.spec = lerp(cell.spec, const(st['spec'] / 255.0), a)
        if 'ao' in st:
            cell.ao = lerp(cell.ao, const(st['ao'] / 255.0), a)
        if 'height' in st:
            cell.add_h = cell.add_h + blur(m, 0.8) * st['height']
        if 'flatten' in st:
            cell.flat = MAX(cell.flat, a * st['flatten'])
        cell.coverage = MAX(cell.coverage, a)


def finish_normal(cell):
    # sign convention of the stored xy against the parent's height gradient (DirectX-style maps flip y)
    dx, dy = grad(blur(cell.height, 1.0))
    cx = mean(cell.nx * (dx * -1.0)) - mean(cell.nx) * mean(dx * -1.0)
    cy = mean(cell.ny * (dy * -1.0)) - mean(cell.ny) * mean(dy * -1.0)
    sx, sy = (1.0 if cx >= 0 else -1.0), (1.0 if cy >= 0 else -1.0)
    hx, hy = grad(blur(cell.add_h, 0.7))
    keep = clamp01(cell.flat * -1.0 + 1.0)
    nx = MIN(MAX(cell.nx * keep + hx * (-2.5 * sx), -0.95), 0.95)
    ny = MIN(MAX(cell.ny * keep + hy * (-2.5 * sy), -0.95), 0.95)
    return nx, ny, (sx, sy)


# ------------------------------------------------------------------ colour science for the gates
def srgb_to_lin(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def lin_to_lab(r, g, b):
    x = (0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047
    y = (0.2126 * r + 0.7152 * g + 0.0722 * b)
    z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883
    f = lambda t: t ** (1 / 3.0) if t > 0.008856 else 7.787 * t + 16 / 116.0
    return (116 * f(y) - 16, 500 * (f(x) - f(y)), 200 * (f(y) - f(z)))


def de2000(l1, l2):
    L1, a1, b1 = l1
    L2, a2, b2 = l2
    C1, C2 = math.hypot(a1, b1), math.hypot(a2, b2)
    Cb = (C1 + C2) / 2
    G = 0.5 * (1 - math.sqrt(Cb ** 7 / (Cb ** 7 + 25 ** 7)))
    a1p, a2p = (1 + G) * a1, (1 + G) * a2
    C1p, C2p = math.hypot(a1p, b1), math.hypot(a2p, b2)
    h1p = math.degrees(math.atan2(b1, a1p)) % 360
    h2p = math.degrees(math.atan2(b2, a2p)) % 360
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


LIN = [int(round(srgb_to_lin(i / 255.0) * 65535.0)) for i in range(256)]


def lin_channels(rgb_img):
    """sRGB 8-bit channels to linear light: a 16-bit lookup split into two 8-bit lookups."""
    hi, lo = [v >> 8 for v in LIN], [v & 255 for v in LIN]
    return [(F(c.point(hi)) * 256.0 + F(c.point(lo))) * (1 / 65535.0) for c in rgb_img.convert('RGB').split()]


def macro_lab(rgb_img, weight=None):
    """Perceived colour at map distance: the (weighted) mean in linear light, as CIELAB."""
    ch = lin_channels(rgb_img)
    m = [wmean(c, weight) if weight is not None else mean(c) for c in ch]
    return lin_to_lab(*m)


def tex_stats(rgb_img, weight=None):
    """Vanilla-comparable statistics of a diffuse (fine-scale lum std at the stamp's texel density, mean saturation)."""
    r, g, b = [F(c) * (1 / 255.0) for c in rgb_img.convert('RGB').split()]
    L = lum(r, g, b) * 255.0
    S = F(rgb_img.convert('RGB').convert('HSV').split()[1]) * (1 / 255.0)
    w = weight if weight is not None else const(1.0, rgb_img.size[0])
    lm = wmean(L, w)
    ls = max(0.0, wmean(L * L, w) - lm * lm) ** 0.5
    hp = L - blur(L, 3.0)  # surface detail only: the luminance minus its 3 px blur
    ld = max(0.0, wmean(hp * hp, w) - wmean(hp, w) ** 2) ** 0.5
    return {'lumMean': round(lm, 1), 'lumStd': round(ls, 2), 'lumDetailStd': round(ld, 2), 'satMean': round(wmean(S, w), 4)}


def quant_in(o, w, q):
    """Quantile of o over the pixels where w >= 0.5 (others are pushed below the range and skipped)."""
    inside = w > 0.5
    frac = mean(inside)
    v = o * inside + (inside * -1.0 + 1.0) * -1000.0
    return quant(v, (1.0 - frac) + q * frac)


def spec_stats(s_img, weight=None):
    sr, sg, _ = [F(c) for c in s_img.convert('RGB').split()]
    w = weight if weight is not None else const(1.0, s_img.size[0])
    return {'specMean': round(wmean(sr, w), 1), 'glossMean': round(wmean(sg, w), 1), 'glossP99': round(quant_in(sg, w, 0.99), 1)}


def normal_stats(n_img, weight=None):
    r, g, _ = [F(c) * (2 / 255.0) - 1.0 for c in n_img.convert('RGB').split()]
    mag = r * r + g * g
    w = weight if weight is not None else const(1.0, n_img.size[0])
    return {'normalXyRms': round(wmean(mag, w) ** 0.5, 4)}


# ------------------------------------------------------------------ build
def policy():
    with open(os.path.join(REPO, 'worldgen/policies/terrain-materials.json'), encoding='utf-8-sig') as f:
        return json.load(f)


def layer_table(pol):
    return {l['name']: l for l in pol['layerSet']}


def neighbours(pol, group):
    layers = []
    for b in pol['biomes']:
        if b['core'] in group['biomes']:
            layers += list(b['layers'])
    layers += [group['parent'], group['slope']]
    return list(dict.fromkeys(layers))


def layer_parent_img(layers, name):
    """A layer's diffuse resampled to the stamp's texel density (one cell)."""
    l = layers[name]
    return to_rgb(tiled_rgb(l['diffuse'], l['scale'], (0, 0)))


def normal_img(nx, ny):
    zz = to_l((nx * nx + ny * ny) * -1.0 + 1.0).point([int(round((math.sqrt(i / 255.0) * 0.5 + 0.5) * 255)) for i in range(256)])
    return Image.merge('RGB', [to_l(nx * 0.5 + 0.5), to_l(ny * 0.5 + 0.5), zz])


def renorm(n_img):
    """Re-derive z after a crossfade of two normal maps."""
    r, g, _ = [F(c) * (2 / 255.0) - 1.0 for c in n_img.split()]
    return normal_img(r, g)


def core_widths(core_img, kind):
    """Core width in texels (core >= 0.5 across v) where it matters for map readability."""
    def run(x, y0, y1):
        col = core_img.crop((x, y0, x + 1, y1)).tobytes()
        return sum(1 for v in col if v >= 128)
    if kind == 'trunk':
        return {'trunkMin': min(run(int(ux(u)), 0, CELL) for u in [0.25 + i * 0.025 for i in range(21)])}
    if kind == 'fork':
        return {'parent': run(int(ux(0.15)), 0, CELL), 'childAtU0.9': run(int(ux(0.9)), 0, CELL // 2)}
    if kind == 'tip':
        return {'u0.1': run(int(ux(0.1)), 0, CELL), 'u0.5': run(int(ux(0.5)), 0, CELL), 'u0.8': run(int(ux(0.8)), 0, CELL)}
    return {'nodeDiameter': run(CELL // 2, 0, CELL), 'rayAtHalf': run(int(CELL / 2 + (NODE_R + RAY_END) / 2), CELL // 2 - 60, CELL // 2 + 60)}


def build_school(school, pol, layers, fast):
    d_atlas = Image.new('RGBA', (ATLAS, ATLAS))
    n_atlas = Image.new('RGB', (ATLAS, ATLAS))
    s_atlas = Image.new('RGB', (ATLAS, ATLAS))
    cells = []
    win = window()
    style, weights = vein_style(school), vein_alpha(school)
    for row, group in enumerate(GROUPS):
        if fast and row > 1:
            continue
        # one seed per school and row: every column shares the ground and the trunk line, so a fork or tip continues a trunk
        seed = int(hashlib.sha256(('%s/%s' % (school, group['name'])).encode()).hexdigest()[:8], 16)
        ground = Cell(layers, group, seed)
        apply(ground, RECIPES[school][group['name']])
        trunk = None
        for col, kind in enumerate(COLUMNS):
            cell = copy.copy(ground)
            cell.masks = dict(ground.masks)
            sk = vein_masks(cell, kind, style, seed)
            apply(cell, VEINS[school]['layers'], 20)
            nx, ny, signs = finish_normal(cell)
            alpha = const(0.0)
            for name, w in weights.items():
                if name in cell.masks:
                    alpha = MAX(alpha, cell.masks[name] * (min(w, HALO_ALPHA_MAX) if name == 'halo' else w))
            # chroma smoothed by a pixel (luminance detail untouched): invisible at map zoom, and it keeps block compression
            # from smearing saturated single-pixel colour noise
            y, cb, cr = to_rgb(cell.rgb).convert('YCbCr').split()
            rgba = Image.merge('YCbCr', [y, cb.filter(ImageFilter.GaussianBlur(1.0)), cr.filter(ImageFilter.GaussianBlur(1.0))]).convert('RGB')
            rgba.putalpha(to_l(alpha))
            n_img = normal_img(nx, ny)
            s_img = Image.merge('RGB', [to_l(cell.spec), to_l(cell.gloss), to_l(cell.ao)]).filter(ImageFilter.GaussianBlur(0.8))
            if kind == 'trunk':
                rgba, n_img, s_img = periodic(rgba), renorm(periodic(n_img)), periodic(s_img)
                trunk = (rgba, n_img, s_img)
            elif kind in ('fork', 'tip'):
                rgba, n_img, s_img = head_blend(rgba, trunk[0]), renorm(head_blend(n_img, trunk[1])), head_blend(s_img, trunk[2])
            raw = F(rgba.getchannel('A')) * (1 / 255.0) * win
            a_raw = to_l(raw)
            a_img = apply_feather(raw, kind)
            rgb = rgba.convert('RGB')
            rgba.putalpha(a_img)
            x, y = col * CELL, row * CELL
            d_atlas.paste(rgba, (x, y))
            n_atlas.paste(n_img, (x, y))
            s_atlas.paste(s_img, (x, y))
            core = to_l(MAX(cell.masks['core'], cell.masks['node']))
            near = to_l(cell.masks['near'] * win)
            hair = to_l(sstep(0.4, 0.6, cell.masks['caps']) * (sstep(0.05, 0.15, MAX(cell.masks['core'], cell.masks['rim'])) * -1.0 + 1.0))
            dy = [abs(q[1] - CELL / 2.0) for p in sk['mains'] for q in p if MARGIN <= q[0] <= CELL - MARGIN] if kind == 'trunk' else []
            cells.append({'row': row, 'col': col, 'group': group['name'], 'kind': kind, 'rgb': rgb, 'alpha': a_img, 'alphaRaw': a_raw, 'near': near,
                          'parent': to_rgb(cell.parent_rgb), 'n': n_img, 's': s_img, 'normalSigns': signs, 'core': core, 'hair': hair,
                          'parentN': cell.parent_n, 'parentS': cell.parent_s, 'alphaMean': round(mean(F(a_img) * (1 / 255.0)), 4),
                          'widths': core_widths(core, kind), 'meanderMax': round(max(dy), 1) if dy else None,
                          'skeleton': {'mains': len(sk['mains']), 'capillaries': len(sk['caps']), 'node': sk['node'] is not None}})
    return d_atlas, n_atlas, s_atlas, cells


# ------------------------------------------------------------------ gates
def envelope(pol, layers):
    """Vanilla stat envelope over every land layer of the shared layer set, at the stamp's texel density."""
    rows = {}
    for name, l in layers.items():
        if name == 'riverbed':
            continue
        d = layer_parent_img(layers, name)
        st = tex_stats(d)
        if l['specular'] != 'none':
            st.update(spec_stats(to_rgb(tiled_rgb(l['specular'], l['scale'], (0, 0)))))
        nxy = tiled_normal(l['normal'], l['scale'], (0, 0))
        st.update(normal_stats(Image.merge('RGB', [to_l(c * 0.5 + 0.5) for c in nxy] + [Image.new('L', (CELL, CELL), 0)])))
        rows[name] = st
    for name, nrm, spc in ENVELOPE_EXTRA:
        d = to_rgb(tiled_rgb(name, 5.0, (0, 0)))
        st = tex_stats(d)
        if spc:
            st.update(spec_stats(to_rgb(tiled_rgb(spc, 5.0, (0, 0)))))
        if nrm:
            nxy = tiled_normal(nrm, 5.0, (0, 0))
            st.update(normal_stats(Image.merge('RGB', [to_l(c * 0.5 + 0.5) for c in nxy] + [Image.new('L', (CELL, CELL), 0)])))
        rows[name] = st
    for name in BCN_REFERENCE:  # MMR-288: the stamps are world-map decals, so the vanilla world-map decals bound their colour too
        im = load(name).convert('RGBA').resize((CELL, CELL), Image.BOX)
        rows['decal:' + name] = tex_stats(im.convert('RGB'), F(im.getchannel('A')) * (1 / 255.0))
    keys = ['lumMean', 'lumStd', 'lumDetailStd', 'satMean', 'specMean', 'glossMean', 'glossP99', 'normalXyRms']
    env = {}
    for k in keys:
        vals = [r[k] for r in rows.values() if k in r]
        env[k] = [min(vals), max(vals)]
    return env, rows


# The vein gates measure two regions (MMR-288 adaptation of MMR-259's core-region envelope): the footprint, the stamp
# composited over its parent over the halo's reach (what the ground looks like where the vein lies: lumMean, satMean),
# and the vein core (the surface of the vein itself: lumDetailStd, specular, gloss, normals). A vein's own colour is meant
# to sit far from vanilla ground (an ink or ember line); the raw lumStd of the core is recorded, not gated.
FOOTPRINT_KEYS, CORE_KEYS = ('lumMean', 'satMean'), ('lumDetailStd', 'specMean', 'glossMean', 'glossP99', 'normalXyRms')


def bcn_roundtrip(img, fmt, tmpdir, tag):
    path = os.path.join(tmpdir, 'rt_%s_%s.dds' % (tag, fmt))
    img.save(path, pixel_format=fmt)
    back = Image.open(path)
    back.load()
    return back


def mae(a, b, band, weight=None):
    d = O(ImageChops.difference(a.getchannel(band), b.getchannel(band)).convert('F'))
    return wmean(d, weight) if weight is not None else mean(d)


def visible(img):
    """The texels a decal shows: its alpha as a weight."""
    return F(img.getchannel('A')) * (1 / 255.0)


def bcn_reference(tmpdir):
    """DXT5 round-trip error (RGB and alpha MAE) of the vanilla world-map decal diffuses, each decoded from its shipped BCn."""
    out = {}
    for name in BCN_REFERENCE:
        im = load(name).convert('RGBA')
        rt = bcn_roundtrip(im, 'DXT5', tmpdir, 'vanilla_' + name)
        w = visible(im)
        out[name] = {'rgbMae': round(sum(mae(im, rt, b) for b in 'RGB') / 3, 3), 'visibleRgbMae': round(sum(mae(im, rt, b, w) for b in 'RGB') / 3, 3),
                     'alphaMae': round(mae(im, rt, 'A'), 3)}
        os.remove(os.path.join(tmpdir, 'rt_vanilla_%s_DXT5.dds' % name))
    return out


def over(ground, layers):
    """Stamps over an opaque ground in order (the deferred decal blend): [(rgba, x, y)] at integer offsets. Returns the
    composite and the stack's combined alpha."""
    out = ground.copy()
    acc = Image.new('L', ground.size, 0)
    for rgba, x, y in layers:
        a = rgba.getchannel('A')
        out.paste(rgba.convert('RGB'), (x, y), a)
        inv = ImageChops.invert(acc).crop((x, y, x + a.size[0], y + a.size[1]))
        acc.paste(ImageChops.invert(ImageChops.multiply(inv, ImageChops.invert(a))), (x, y))
    return out, acc


def strip_ground(cell, w):
    g = Image.new('RGB', (w, CELL))
    for x in range(0, w, CELL):
        g.paste(cell['parent'], (x, 0))
    return g


def chain_gate(row_cells, last):
    """Trunk, trunk, trunk and then `last` (fork or tip) at the contract spacing (0.75 x box, PERIOD texels), along a strip at
    the cell's own texel density, against the ideal: the same stamps pasted with their unfeathered alpha, each over the last
    (a seamless chain is texel-identical to it at every joint). Measures the axis coverage, and per joint the mean alpha
    difference over the whole joint block and the largest colour difference of an 8 x 16 texel bin on the axis."""
    by = {c['kind']: c for c in row_cells}
    stamps = [by['trunk']] * 3 + [by[last]]
    w = 3 * PERIOD + CELL
    real, ideal = [], []
    for i, c in enumerate(stamps):
        im = c['rgb'].copy()
        im.putalpha(c['alpha'])
        real.append((im, i * PERIOD, 0))
        im = c['rgb'].copy()
        im.putalpha(c['alphaRaw'])
        ideal.append((im, i * PERIOD, 0))
    ground = strip_ground(stamps[0], w)
    comp, acc = over(ground, real)
    comp_i = ground.copy()
    acc_i = Image.new('L', (w, CELL), 0)
    for im, x, y in ideal:  # the ideal: each stamp replaces what lies under it
        comp_i.paste(ground.crop((x, y, x + CELL, y + CELL)), (x, y), im.getchannel('A').point(lambda v: 255 if v else 0))
        comp_i.paste(im.convert('RGB'), (x, y), im.getchannel('A'))
        acc_i.paste(im.getchannel('A'), (x, y), im.getchannel('A').point(lambda v: 255 if v else 0))
    x0, x1 = int(ux(0.2)), int(3 * PERIOD + ux(0.25))
    band = acc.crop((0, CELL // 2 - 8, w, CELL // 2 + 8))
    cover = min(max(band.crop((x, 0, x + 1, 16)).tobytes()) for x in range(x0, x1)) / 255.0
    out = {'chain': 'trunk x3 -> ' + last, 'coreCoverageMin': round(cover, 3), 'joints': []}
    for k in (1, 2, 3):
        a, b = k * PERIOD + MARGIN, k * PERIOD + MARGIN + OVERLAP
        diff = ImageChops.difference(acc.crop((a, 0, b, CELL)), acc_i.crop((a, 0, b, CELL)))
        de = max(de2000(macro_lab(comp.crop((x, CELL // 2 - 8, x + 8, CELL // 2 + 8))),
                        macro_lab(comp_i.crop((x, CELL // 2 - 8, x + 8, CELL // 2 + 8)))) for x in range(a, b, 8))
        out['joints'].append({'into': 'trunk' if k < 3 else last, 'alphaMae': round(mean(F(diff)) / 255.0, 4),
                              'alphaMax': round(diff.getextrema()[1] / 255.0, 3), 'axisDeMax': round(de, 2)})
    return out, comp


def v_symmetry(alpha_img):
    a = blur(F(alpha_img) * (1 / 255.0), 6.0)
    m = O(I(a).transpose(Image.Transpose.FLIP_TOP_BOTTOM))
    diff = MAX(a - m, m - a)
    return mean(diff) / max(1e-6, mean(a))


def gate_school(school, d, n, s, cells, env, layer_labs, pol, layers, tmpdir, bref, review=None):
    res = {'cells': [], 'chains': {}, 'failures': []}
    fail = res['failures'].append
    for c in cells:
        group = GROUPS[c['row']]
        a = F(c['alpha']) * (1 / 255.0)
        core = sstep(0.45, 0.55, F(c['core']) * (1 / 255.0))
        comp = Image.composite(c['rgb'], c['parent'], c['alpha'])
        lab = macro_lab(comp, core)
        de_parent = de2000(lab, macro_lab(c['parent']))
        de_nb = {nb: round(de2000(lab, layer_labs[nb]), 2) for nb in neighbours(pol, group)}
        foot = tex_stats(comp, F(c['near']) * (1 / 255.0))
        surf = tex_stats(comp, core)
        surf.update(spec_stats(c['s'], core))
        surf.update(normal_stats(c['n'], core))
        st = {k: foot[k] for k in FOOTPRINT_KEYS}
        st.update({k: surf[k] for k in CORE_KEYS})
        st['coreLumStd'] = surf['lumStd']
        env_fail = [k for k in FOOTPRINT_KEYS + CORE_KEYS if not (env[k][0] <= st[k] <= env[k][1])]
        al = c['alpha']
        border = max(al.crop((0, 0, CELL, MARGIN)).getextrema()[1], al.crop((0, CELL - MARGIN, CELL, CELL)).getextrema()[1],
                     al.crop((0, 0, MARGIN, CELL)).getextrema()[1], al.crop((CELL - MARGIN, 0, CELL, CELL)).getextrema()[1])
        sym = v_symmetry(al)
        w_px = c['widths']
        screen = {name: [round(v * BOX_W[0] * SCREEN_PPU / CELL, 2), round(v * BOX_W[1] * SCREEN_PPU / CELL, 2)] for name, v in w_px.items()}
        row = {'row': c['row'], 'col': c['col'], 'group': c['group'], 'kind': c['kind'], 'macroLab': [round(v, 2) for v in lab],
               'deParent': round(de_parent, 2), 'deNeighbours': de_nb, 'minDe': round(min([de_parent] + list(de_nb.values())), 2),
               'stats': st, 'envelopeFailures': env_fail, 'alphaMean': c['alphaMean'], 'alphaMarginMax': border,
               'vSymmetry': round(sym, 4), 'coreWidthTexels': w_px, 'coreWidthScreenPxAtW': screen, 'meanderMax': c['meanderMax'],
               'skeleton': c['skeleton']}
        if row['minDe'] < 10:
            worst = min(de_nb, key=de_nb.get) if min(de_nb.values()) <= de_parent else 'parent'
            fail('%s %s %s: macro dE %.1f < 10 (against %s)' % (school, c['group'], c['kind'], row['minDe'], worst))
        if env_fail:
            fail('%s %s %s: outside vanilla envelope %s' % (school, c['group'], c['kind'], {k: (st[k], env[k]) for k in env_fail}))
        if border != 0:
            fail('%s %s %s: alpha %d inside the %d px margin' % (school, c['group'], c['kind'], border, MARGIN))
        if sym > 0.25:
            fail('%s %s %s: v-symmetry difference %.3f > 0.25' % (school, c['group'], c['kind'], sym))
        if c['meanderMax'] is not None and c['meanderMax'] > MEANDER_CAP + CORE_W * 0.1:
            fail('%s %s trunk: meander %.1f texels over the cap %.1f' % (school, c['group'], c['meanderMax'], MEANDER_CAP))
        if c['kind'] == 'trunk' and screen['trunkMin'][0] < 3.0:
            fail('%s %s trunk: core %.2f screen px at W %.1f < 3' % (school, c['group'], screen['trunkMin'][0], BOX_W[0]))
        res['cells'].append(row)
    # ---- chained stamps (seams across the 0.75 x L overlap)
    for r, group in enumerate(GROUPS):
        rc = [c for c in cells if c['row'] == r]
        if len(rc) < 4:
            continue
        for last in ('fork', 'tip'):
            g, comp = chain_gate(rc, last)
            res['chains']['%s->%s' % (group['name'], last)] = g
            if review and last == 'fork':
                comp.resize((comp.size[0] // 2, comp.size[1] // 2), Image.BOX).save(os.path.join(review, '%s_seam_%s.png' % (school, group['name'])))
            for j in g['joints']:
                lim = (0.01, 2.0) if j['into'] == 'trunk' else (0.03, 6.0)  # into a fork or tip: its head fades from the trunk's own
                if j['alphaMae'] > lim[0] or j['axisDeMax'] > lim[1]:
                    fail('%s %s chain %s: joint into %s alpha MAE %.3f (> %.2f) or axis dE %.2f (> %.1f)' % (
                        school, group['name'], g['chain'], j['into'], j['alphaMae'], lim[0], j['axisDeMax'], lim[1]))
            if g['coreCoverageMin'] < 0.95:
                fail('%s %s chain %s: axis coverage %.2f < 0.95' % (school, group['name'], g['chain'], g['coreCoverageMin']))
    # ---- BCn round-trips of the whole atlases, against the same round-trip of the parents tiled the same way (reference)
    ref = Image.new('RGBA', d.size)
    for c in cells:
        p = c['parent'].copy()
        p.putalpha(c['alpha'])
        ref.paste(p, (c['col'] * CELL, c['row'] * CELL))
    rt = bcn_roundtrip(d, 'DXT5', tmpdir, school + '_d')
    rt_ref = bcn_roundtrip(ref, 'DXT5', tmpdir, school + '_ref')
    rgb_all = sum(mae(d, rt, b) for b in 'RGB') / 3
    rgb_err = sum(mae(d, rt, b, visible(d)) for b in 'RGB') / 3  # gated: error where the decal shows (alpha-weighted)
    rgb_ref = sum(mae(ref, rt_ref, b, visible(ref)) for b in 'RGB') / 3
    a_err = mae(d, rt, 'A')
    # hairlines: alpha and local luminance contrast kept on the capillary texels (outside core and rim)
    hair_a, hair_a_rt, hair_c, hair_c_rt = 0.0, 0.0, 0.0, 0.0
    for c in cells:
        box = (c['col'] * CELL, c['row'] * CELL, c['col'] * CELL + CELL, c['row'] * CELL + CELL)
        h = F(c['hair']) * (1 / 255.0) * (F(c['alpha']) * (1 / 255.0) > 0.4)
        if mean(h) <= 0:
            continue
        before, after = d.crop(box), rt.crop(box)
        lb, la = [lum(*[F(ch) for ch in im.convert('RGB').split()]) for im in (before, after)]
        con_b, con_a = MAX(lb - blur(lb, 4.0), blur(lb, 4.0) - lb), MAX(la - blur(la, 4.0), blur(la, 4.0) - la)
        wsum = mean(h)
        hair_a += mean(F(before.getchannel('A')) * h) / wsum
        hair_a_rt += mean(F(after.getchannel('A')) * h) / wsum
        hair_c += mean(con_b * h) / wsum
        hair_c_rt += mean(con_a * h) / wsum
    hair = {'alphaKept': round(hair_a_rt / max(1e-6, hair_a), 4), 'contrastKept': round(hair_c_rt / max(1e-6, hair_c), 4)}
    ref_n, ref_s = Image.new('RGB', n.size, (128, 128, 255)), Image.new('RGB', s.size)
    for c in cells:
        ref_n.paste(c['parentN'], (c['col'] * CELL, c['row'] * CELL))
        ref_s.paste(c['parentS'], (c['col'] * CELL, c['row'] * CELL))
    rtn = bcn_roundtrip(n, 'BC5', tmpdir, school + '_n').convert('RGB')
    n_err = (mae(n, rtn, 'R') + mae(n, rtn, 'G')) / 2
    rtn_ref = bcn_roundtrip(ref_n, 'BC5', tmpdir, school + '_nref').convert('RGB')
    n_ref = (mae(ref_n, rtn_ref, 'R') + mae(ref_n, rtn_ref, 'G')) / 2
    rts = bcn_roundtrip(s, 'DXT1', tmpdir, school + '_s').convert('RGB')
    s_err = sum(mae(s, rts, b) for b in 'RGB') / 3
    rts_ref = bcn_roundtrip(ref_s, 'DXT1', tmpdir, school + '_sref').convert('RGB')
    s_ref = sum(mae(ref_s, rts_ref, b) for b in 'RGB') / 3
    ceil_rgb = max(v['visibleRgbMae'] for v in bref.values())
    ceil_a = max(3.0, max(v['alphaMae'] for v in bref.values()))
    ceil_n, ceil_s = max(1.25 * n_ref, n_ref + 0.5), max(1.25 * s_ref, s_ref + 0.5)
    res['bcn'] = {'diffuseDxt5RgbMae': round(rgb_err, 3), 'diffuseDxt5RgbMaeUnweighted': round(rgb_all, 3), 'vanillaDecalCeiling': ceil_rgb, 'parentReferenceRgbMae': round(rgb_ref, 3),
                  'alphaMae': round(a_err, 3), 'alphaCeiling': ceil_a, 'normalBc5RgMae': round(n_err, 3), 'normalParentReference': round(n_ref, 3),
                  'specDxt1Mae': round(s_err, 3), 'specParentReference': round(s_ref, 3), 'hairlines': hair,
                  'note': 'Pillow has no BC7 encoder; DXT5 (BC3) is the conservative stand-in for the BC7 the editor writes'}
    if rgb_err > ceil_rgb:
        fail('%s: DXT5 diffuse error %.2f above the vanilla world-map decal ceiling %.2f' % (school, rgb_err, ceil_rgb))
    if a_err > ceil_a or n_err > ceil_n or s_err > ceil_s:
        fail('%s: BCn round-trip alpha %.2f / normal %.2f / spec %.2f over %.2f / %.2f / %.2f' % (school, a_err, n_err, s_err, ceil_a, ceil_n, ceil_s))
    if hair['alphaKept'] < 0.85 or hair['contrastKept'] < 0.7:
        fail('%s: DXT5 keeps hairline alpha %.2f (< 0.85) or contrast %.2f (< 0.7)' % (school, hair['alphaKept'], hair['contrastKept']))
    for f in os.listdir(tmpdir):
        if f.startswith('rt_' + school):
            os.remove(os.path.join(tmpdir, f))
    return res


# ------------------------------------------------------------------ review images
def ground_img(layers, group, ppu, w, h):
    l = layers[group['parent']]
    tile_px = max(8, int(round(l['scale'] * ppu)))
    t = load(l['diffuse']).convert('RGB').resize((tile_px, tile_px), Image.BOX)
    g = Image.new('RGB', (w, h))
    for y in range(0, h, tile_px):
        for x in range(0, w, tile_px):
            g.paste(t, (x, y))
    return g


def chain_plan(seed):
    """A synthetic vein graph for review, laid out by the joint rule placement uses: a stamp's head anchor (u = 0.1) sits on
    the previous stamp's tail anchor (u = 0.9), 0.375 L from each centre; a fork's children start at (0.9, 0.5 +- 0.24) with
    heading +-atan(CHILD_SLOPE W / L). Returns (kind, cx, cy, yaw_deg, L, W) in map units, y down."""
    rng = random.Random(seed)
    out = []
    yaw0 = rng.uniform(0, 360)
    out.append(('burst', 0.0, 0.0, yaw0, 7.0, 7.0))
    child_v = (CHILD_V - CHILD_SLOPE * 0.1) * INNER / CELL

    def stamp(kind, hx, hy, th, L, W):
        dx, dy = math.cos(math.radians(th)), math.sin(math.radians(th))
        cx, cy = hx + dx * 0.375 * L, hy + dy * 0.375 * L
        out.append((kind, cx, cy, th, L, W))
        return cx, cy, dx, dy

    def branch(hx, hy, th, W, depth):
        for _ in range(rng.choice((2, 3)) if depth == 0 else 1):
            cx, cy, dx, dy = stamp('trunk', hx, hy, th, 5.0, W)
            hx, hy = cx + dx * 0.375 * 5.0, cy + dy * 0.375 * 5.0
            th += rng.uniform(-14, 14)
            W *= 0.92
        if depth == 0:
            cx, cy, dx, dy = stamp('fork', hx, hy, th, 5.0, W)
            for sgn in (1, -1):
                ax = cx + dx * 0.375 * 5.0 - dy * sgn * child_v * W
                ay = cy + dy * 0.375 * 5.0 + dx * sgn * child_v * W
                branch(ax, ay, th + sgn * math.degrees(math.atan(CHILD_SLOPE * W / 5.0)), W * CHILD_W, 1)
        else:
            stamp('tip', hx, hy, th, 5.0, W)

    for a in (0, -42, 94, -140, 180):
        th = yaw0 + a
        branch(math.cos(math.radians(th)) * 7.0 * 0.28, math.sin(math.radians(th)) * 7.0 * 0.28, th, 3.4, 0)
    return out


def draw_plan(atlas, row, plan, ground, ppu, cx0, cy0):
    out = ground.copy()
    for kind, cx, cy, yaw, L, W in plan:
        col = COLUMNS.index(kind)
        st = atlas.crop((col * CELL, row * CELL, col * CELL + CELL, row * CELL + CELL))
        lw, lh = max(2, int(round(L * ppu))), max(2, int(round(W * ppu)))
        st = st.resize((lw, lh), Image.BOX).rotate(-yaw, Image.BICUBIC, expand=True)
        x, y = cx0 + cx * ppu - st.size[0] / 2.0, cy0 + cy * ppu - st.size[1] / 2.0
        out.paste(st.convert('RGB'), (int(round(x)), int(round(y))), st.getchannel('A'))
    return out


def chain_sheet(school, d, layers, review):
    plan = chain_plan(SCHOOLS.index(school) * 101 + 7)
    W, H = 1000, 760
    sheet = Image.new('RGB', (W * 2 + 8, H * 2 + 8), (20, 20, 20))
    for row, group in enumerate(GROUPS):
        ppu = 13.0  # about a third of the closest camera, to show a whole graph
        sheet.paste(draw_plan(d, row, plan, ground_img(layers, group, ppu, W, H), ppu, W / 2.0, H / 2.0), ((row % 2) * (W + 8), (row // 2) * (H + 8)))
    sheet.save(os.path.join(review, school + '_chain.png'))
    close = Image.new('RGB', (900 * 2 + 8, 600), (20, 20, 20))
    for i, row in enumerate((0, 1)):
        ppu = SCREEN_PPU  # the closest camera
        close.paste(draw_plan(d, row, plan, ground_img(layers, GROUPS[row], ppu, 900, 600), ppu, 300, 300), (i * 908, 0))
    close.save(os.path.join(review, school + '_chain_close.png'))


def atlas_over_parents(cells, path):
    """Each cell composited over its row parent (the ground the stamp lies on)."""
    out = Image.new('RGB', (ATLAS, ATLAS), (20, 20, 20))
    for c in cells:
        out.paste(Image.composite(c['rgb'], c['parent'], c['alpha']), (c['col'] * CELL, c['row'] * CELL))
    out.resize((1024, 1024), Image.BOX).save(path)


# ------------------------------------------------------------------ inputs and lineage
def needed_textures(pol):
    names = []
    for l in pol['layerSet']:
        names += [l[r] for r in ('diffuse', 'normal', 'specular', 'height') if l[r] != 'none']
    names += VANILLA_EXTRA + [n for row in ENVELOPE_EXTRA for n in row if n] + BCN_REFERENCE
    for school in RECIPES.values():
        for recipe in school.values():
            for _, st in recipe:
                if st.get('src', '').startswith('tex:'):
                    names.append(st['src'][4:].partition('@')[0])
    return list(dict.fromkeys(names))


def extract_inputs(modules, ext, names):
    """Decode every vanilla input to ext/<name>.png (tools/art/tpac_textures.py) and record where each came from."""
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import tpac_textures
    os.makedirs(ext, exist_ok=True)
    found = tpac_textures.find(modules, names)
    missing = [n for n in names if n not in found]
    if missing:
        raise SystemExit('vanilla textures not found: ' + ', '.join(missing))
    manifest = {}
    for n in names:
        r = found[n]
        path = os.path.join(ext, n + '.png')
        tpac_textures.decode(r).save(path)
        manifest[n] = {'pack': os.path.relpath(r['pack'], modules).replace(os.sep, '/'), 'source': r['source'], 'format': r['format'],
                       'size': [r['width'], r['height']], 'flags': r['flags'], 'decodedPngSha256': sha(path)}
    with open(os.path.join(ext, 'inputs.json'), 'w', encoding='utf-8', newline='\n') as f:
        json.dump(manifest, f, indent=1)
        f.write('\n')


CONCEPT = os.path.join(os.environ.get('FWGC_ROOT', r'C:/Users/davgo/Documents/GitHub/FWGC++'), 'concept art/tier-1-style/world-and-environments')
WEAVE_CONCEPTS = {'arid': 'biome.desert.weave.style-study.png', 'verdant': 'biome.grassland.weave.style-study.png',
                  'rock': 'biome.exposed_rock.weave.style-study.png', 'cold': 'biome.cold_tundra.weave.style-study.png'}


def palette_labs(img, k=8):
    q = img.convert('RGB').quantize(k, method=Image.Quantize.MEDIANCUT)
    pal = q.getpalette()[:3 * k]
    counts = sorted(q.getcolors(), reverse=True)
    return [(c / float(img.size[0] * img.size[1]), lin_to_lab(*[srgb_to_lin(pal[3 * i + j] / 255.0) for j in range(3)])) for c, i in counts]


def weave_concept_match(d):
    """Weave against the approved concept art: the ground palette (lower 45 % of each study, 8 median-cut colours) against
    the burst cell's vein palette; per group, the coverage-weighted mean of each cell colour's nearest concept colour (CIEDE2000)."""
    out = {}
    for row, group in enumerate(GROUPS):
        path = os.path.join(CONCEPT, WEAVE_CONCEPTS[group['name']])
        if not os.path.isfile(path):
            out[group['name']] = {'concept': WEAVE_CONCEPTS[group['name']], 'missing': True}
            continue
        art = Image.open(path).convert('RGB')
        w, h = art.size
        ground = art.crop((0, int(h * 0.55), w, h))
        cp = palette_labs(ground)
        cell = d.crop((3 * CELL + 96, row * CELL + 96, 4 * CELL - 96, row * CELL + CELL - 96))  # the burst: the most vein
        core = Image.new('RGB', cell.size)
        core.paste(cell, (0, 0), cell.getchannel('A').point(lambda v: 255 if v > 200 else 0))
        sp = [(f, lab) for f, lab in palette_labs(core) if lab[0] > 2]
        tot = sum(f for f, _ in sp)
        score = sum(f * min(de2000(lab, c) for _, c in cp) for f, lab in sp) / max(1e-9, tot)
        out[group['name']] = {'concept': WEAVE_CONCEPTS[group['name']], 'conceptSha256': sha(path), 'meanNearestDe': round(score, 2),
                              'conceptGroundPalette': [[round(v, 1) for v in lab] for _, lab in cp]}
    return out


# ------------------------------------------------------------------ main
def sha(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for b in iter(lambda: f.read(1 << 20), b''):
            h.update(b)
    return h.hexdigest()


def main():
    global EXT
    ap = argparse.ArgumentParser()
    ap.add_argument('--ext', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--review')
    ap.add_argument('--evidence', help='the full gate and lineage report (evidence/art/magic-veins.json)')
    ap.add_argument('--inputs-evidence', help='the slim atlas record: contract, vanilla inputs, output hashes (evidence/art/magic-textures.json)')
    ap.add_argument('--schools', default=','.join(SCHOOLS))
    ap.add_argument('--fast', action='store_true', help='rows arid and verdant only, no outputs; for recipe iteration')
    ap.add_argument('--modules', help='the game Modules folder: decode the vanilla inputs into --ext first')
    ap.add_argument('--format', default='png', choices=['png', 'tga'], help='asset source file format')
    a = ap.parse_args()
    EXT = a.ext
    pol = policy()
    if a.modules:
        extract_inputs(a.modules, a.ext, needed_textures(pol))
    layers = layer_table(pol)
    os.makedirs(a.out, exist_ok=True)
    tmp = a.review or a.out
    if a.review:
        os.makedirs(a.review, exist_ok=True)
    env, env_rows = envelope(pol, layers)
    bref = bcn_reference(tmp)
    layer_labs = {name: macro_lab(layer_parent_img(layers, name)) for name in layers}
    child9 = CHILD_V - CHILD_SLOPE * 0.1
    atlas = {'size': ATLAS, 'grid': GRID, 'cell': CELL, 'marginPx': MARGIN, 'rows': [g['name'] for g in GROUPS],
             'rowBiomes': {g['name']: g['biomes'] for g in GROUPS}, 'columns': COLUMNS,
             'vectorArgument': 'SetVectorArgument(0.25, 0.25, column / 4, row / 4)',
             'frame': 'u = (x - %d) / %d left to right, v likewise top to bottom; growth toward +u; the decal box (L along u, W across) spans '
                      'the whole %d px cell, margin included' % (MARGIN, INNER, CELL),
             'chain': {'spacing': '0.75 L (%d texels)' % PERIOD, 'overlapU': [round(1 - OVERLAP / float(INNER), 3), 1.0],
                       'jointRule': "a chained stamp's head anchor (u 0.1, v 0.5) sits on the previous stamp's tail anchor (u 0.9): "
                                    "centres 0.375 L + 0.375 L' apart along each heading",
                       'feather': 'optical depth: alpha = 1 - (1 - a)^f, f a smoothstep over u 0..0.2 (head) and 1 - smoothstep over u 0.8..1 '
                                  '(tail; trunk and fork); the two sum to 1 across a joint',
                       'trunkPeriodic': 'trunk texels at u 0.8..1 equal those at u 0..0.2 (x + %d)' % PERIOD},
             'kinds': {'trunk': {'coreWidthTexels': round(CORE_W, 1), 'coreWidth': 'about 1/12 of the inner height, +-10 percent',
                                 'meanderMaxTexels': MEANDER_CAP, 'haloAlphaMax': HALO_ALPHA_MAX, 'haloReach': 'about 1/4 of the height'},
                       'fork': {'splitU': SPLIT_U, 'childExit': [[1.0, 0.5 - CHILD_V], [1.0, 0.5 + CHILD_V]],
                                'childAtU0.9': [[0.9, round(0.5 - child9, 3)], [0.9, round(0.5 + child9, 3)]],
                                'childHeading': 'dv/du = +-%.1f over u 0.75..1, a world yaw of +-atan(%.1f W / L)' % (CHILD_SLOPE, CHILD_SLOPE),
                                'childWidth': CHILD_W},
                       'tip': {'endU': TIP_END, 'end': 'hairline with a mirrored capillary splay'},
                       'burst': {'nodeRadius': 0.12, 'rays': 8, 'rayAngles': 'about 0, +-42, +-94, +-140, 180 degrees from +u',
                                 'rayEndRadius': round(RAY_END / INNER, 3)}}}
    report = {'schema': 'mnbl.magic-veins', 'version': 1, 'ticket': 'MMR-288', 'atlas': atlas,
              'gates': {'macroDe': 'CIEDE2000 of the vein core (core and burst node masks >= 0.5; linear-light mean of the stamp composited over '
                                   'its parent) against the parent and every layer of the row biomes plus the slope layer; >= 10',
                        'envelope': 'footprint (stamp over parent, weighted by the halo reach mask: the vein and the ground around it): lumMean, satMean; vein core: lumDetailStd (luminance minus a '
                                    '3 px blur), specular, gloss mean and p99, normal xy rms; each inside the min..max of the shared layer set, the '
                                    'recipe vanilla sources (ENVELOPE_EXTRA) and, for the colour keys, the vanilla world-map decals (bcnReference, '
                                    'alpha-weighted) at the same texel density. coreLumStd is recorded, not gated',
                        'seams': 'alpha 0 within the %d px cell margin; chains trunk x3 -> fork and trunk x3 -> tip at %d texel spacing over the '
                                 'parent: axis coverage >= 0.95, halo median alpha at joints / interiors in 0.8..1.25, largest colour step between '
                                 '8-texel bins on the axis at a joint <= the largest inside a stamp + 1 dE' % (MARGIN, PERIOD),
                        'vSymmetry': 'mean |A - flipV(A)| / mean A of the alpha blurred by 6 px <= 0.25; trunk meander <= 1/16 of the height',
                        'readability': 'trunk core >= 3 screen px at the narrowest box (W %.1f units at %d px per unit); every kind recorded'
                                       % (BOX_W[0], SCREEN_PPU),
                        'bcn': 'DXT5 diffuse RGB error weighted by alpha (where the decal shows) <= the worst vanilla world-map decal round-trip weighted the same way (bcnReference; unweighted errors recorded); alpha <= max(3, vanilla); BC5 '
                               'normal and DXT1 spec <= max(1.25 x, +0.5) the parents in the same layout; hairline texels (capillaries outside core '
                               'and rim) keep >= 0.85 of their alpha and >= 0.7 of their local luminance contrast'},
              'envelope': env, 'envelopeLayers': env_rows, 'bcnReference': bref,
              'layerMacroLab': {k: [round(x, 2) for x in v] for k, v in layer_labs.items()}, 'schools': {}, 'failures': []}
    for school in a.schools.split(','):
        d, n, s, cells = build_school(school, pol, layers, a.fast)
        g = gate_school(school, d, n, s, cells, env, layer_labs, pol, layers, tmp, bref, a.review)
        if school == 'weave' and not a.fast:
            g['conceptMatch'] = weave_concept_match(d)
        g['style'] = vein_style(school)
        g['alphaWeights'] = vein_alpha(school)
        report['schools'][school] = g
        report['failures'] += g['failures']
        print('%-9s minDe %s  dxt5 %.2f  hair %s  failures %d' % (school, [c['minDe'] for c in g['cells']], g['bcn']['diffuseDxt5RgbMae'],
                                                                g['bcn']['hairlines'], len(g['failures'])), flush=True)
        for f in g['failures']:
            print('   ', f)
        if a.review:
            atlas_over_parents(cells, os.path.join(a.review, school + '_atlas.png'))
            chain_sheet(school, d, layers, a.review)
        if not a.fast:
            files = {}
            for suffix, img in (('d', d), ('n', n), ('s', s)):
                p = os.path.join(a.out, 'mnbl_magic_%s_%s.%s' % (school, suffix, a.format))
                img.save(p, compress_level=9) if a.format == 'png' else img.save(p, rle=False)
                files[suffix] = {'file': os.path.basename(p), 'sha256': sha(p), 'mode': img.mode, 'size': list(img.size)}
            g['files'] = files
    if a.evidence and not a.fast:
        report['passed'] = not report['failures']
        manifest = os.path.join(a.ext, 'inputs.json')
        report['inputs'] = 'not recorded (run with --modules)'
        if os.path.isfile(manifest):
            with open(manifest, encoding='utf-8') as f:
                report['inputs'] = json.load(f)
        report['lineage'] = {
            'colourAndSurface': 'vanilla textures in inputs, recoloured by RECIPES (the corrupted ground of the halo) and VEINS (the vein materials)',
            'crackDetail': 'the crack network of the vanilla cracked-mud decal (decal_b_d, mainmap_cracked_mud_a) around each vein',
            'skeleton': 'procedural (vein_skeleton: periodic sine noise, cubic Hermite fork children, mirrored burst rays), seeded by '
                        'sha256(school/row); it only places the vanilla-derived materials',
            'codex': 'not used: no generated imagery fed any texture or skeleton'}
        report['hiddenLooks'] = pol['mutation']['hiddenLooks']
        with open(a.evidence, 'w', encoding='utf-8', newline='\n') as f:
            json.dump(report, f, indent=1)
            f.write('\n')
        if a.inputs_evidence:
            slim = {'schema': 'mnbl.magic-textures', 'version': 2, 'ticket': 'MMR-288 (re-derived from MMR-259)', 'atlas': atlas,
                    'gatesAndLineage': os.path.relpath(a.evidence, os.path.dirname(os.path.abspath(a.inputs_evidence))).replace(os.sep, '/'),
                    'passed': report['passed'], 'files': {k: v['files'] for k, v in report['schools'].items()},
                    'inputs': report['inputs'], 'hiddenLooks': report['hiddenLooks']}
            with open(a.inputs_evidence, 'w', encoding='utf-8', newline='\n') as f:
                json.dump(slim, f, indent=1)
                f.write('\n')
    if a.review:
        ims = []
        for sc in a.schools.split(','):
            im = Image.open(os.path.join(a.review, sc + '_chain.png'))
            ims.append(im.crop((im.size[0] // 2 + 4, 0, im.size[0], im.size[1] // 2 - 4)).resize((500, 380), Image.BOX))  # the verdant row
        sheet = Image.new('RGB', (504 * 4, 384 * ((len(ims) + 3) // 4)), (0, 0, 0))
        for i, im in enumerate(ims):
            sheet.paste(im, ((i % 4) * 504, (i // 4) * 384))
        sheet.save(os.path.join(a.review, 'contact.png'))
    print('failures:', len(report['failures']))
    return 1 if report['failures'] else 0


if __name__ == '__main__':
    sys.exit(main())
