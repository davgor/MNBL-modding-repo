"""MMR-290: derive the magic water vein-stamp atlas from vanilla textures, gate it, and write the module's asset sources.

Magic water is MMR-288's vein graph continued over the sea, lakes and rivers (docs/magic-water.md). The campaign map's water shader
(water_surface_worldmap.rs, use_decals) draws a scene decal whose box holds the water surface by ADDING the decal's colour (albedo x
alpha x factor x lighting) to the water: a stamp can brighten and tint the water but never darken it. So magic water is luminous threads
in each school's colour, not ground. One published decal material (mnbl_magicwater) holds every school:
  mnbl_magicwater_d  RGBA  diffuse, alpha = the stamp's baked falloff (import: has_alpha, dont_resize_in_atlas; BC7)
  mnbl_magicwater_n  RGB   tangent normal, a gently raised thread (import: is_bumpmap; BC5)
The atlas is 2048 x 2048: 4 columns x 8 rows of 512 x 256 px cells, with a 16 px transparent margin on every side (inner 480 x 224).
Column c is the vein stamp kind (contract C1: 0 trunk, 1 fork, 2 tip, 3 source burst), row r the look school (SCHOOLS, MMR-259's order;
roads.json magicWater.atlas.schoolRows). A decal shows cell (c, r) with SetVectorArgument(0.25, 0.125, c / 4, r / 8). Inside the margin u
runs left to right, v top to bottom; veins grow toward +u and every stamp is symmetric about v = 0.5 (mirrored by construction). The decal
box spans the whole cell, margin included; the inner height is 224 / 256 of the box width (a ground cell's 480 / 512), so placement widens
the box by widthScale 1.07 to keep the vein's world width.

Derivation (owner decision 4): the vein geometry is MMR-288's (tools/art/magic_ground.py vein_skeleton and vein_masks: the trunk line,
fork, tip and burst kits, chaining at 0.75 x the box), drawn in a 512 px square and squashed to the 224 px inner height; its edge break-up
and the lace inside threads and halo come from the vanilla shore foam (Native foam_shore's alpha, the lacy foam network War Sails draws on
coasts) and the vanilla wetness decal (Native decal_water_a's alpha) for the halo's soft stain; colour is the school's palette modulated by
that foam lace (the FWGC++ school colours and MMR-288's vein looks). No Codex imagery was used.

Usage: python tools/art/magic_water.py --modules <game Modules dir> --ext <decoded vanilla dir> --out <AssetSources/magic_water dir>
                                       [--review <dir>] [--evidence <json>]
"""
import argparse
import hashlib
import json
import os
import sys

from PIL import Image, ImageChops, ImageFilter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import magic_ground as mg  # noqa: E402
from magic_ground import F, I, MAX, MIN, blur, clamp01, const, lerp, sstep, to_l  # noqa: E402

CELL_W, CELL_H, MARGIN, COLS, ROWS = 512, 256, 16, 4, 8
INNER_H = CELL_H - 2 * MARGIN  # 224
ATLAS = CELL_W * COLS  # 2048 = CELL_H * ROWS
SCHOOLS = mg.SCHOOLS  # row order: weave, umbral, infernal, radiant, fire, water, earth, air
COLUMNS = mg.COLUMNS
WIDTH_SCALE = round((mg.INNER / float(mg.CELL)) / (INNER_H / float(CELL_H)), 2)  # 1.07: the box widening that keeps a vein's world width
HALO_ALPHA_MAX = 0.25
# acc4 world-1 water colours (median sRGB of the shots, MMR-290-route.md section 3): deep sea, coast sea, B lake, A shallow lake
WATERS = {'deepSea': (19, 35, 58), 'coastSea': (35, 47, 62), 'lakeB': (52, 63, 75), 'shallowLakeA': (62, 98, 98)}
VANILLA = ['foam_shore', 'decal_water_a', 'water_foam_circular']
# Luminous thread palettes (sRGB): hot (the axis and the source), thread (the vein), halo (the faint glow around it). Hues follow the
# FWGC++ school colours and MMR-288's ground veins; nothing reaches 255 (no clipping); umbral is a violet glow, since additive light cannot darken.
PALETTE = {
    'weave': {'hot': (244, 240, 250), 'thread': None, 'halo': (187, 173, 245)},  # thread: the opal play of colour (mg.OPAL)
    'umbral': {'hot': (206, 168, 246), 'thread': (138, 74, 214), 'halo': (101, 50, 158)},
    'infernal': {'hot': (246, 150, 112), 'thread': (214, 38, 40), 'halo': (158, 22, 29)},
    'radiant': {'hot': (248, 244, 222), 'thread': (236, 206, 116), 'halo': (191, 166, 86)},
    'fire': {'hot': (248, 216, 140), 'thread': (246, 126, 38), 'halo': (191, 72, 22)},
    'water': {'hot': (200, 246, 246), 'thread': (38, 200, 222), 'halo': (18, 140, 169)},
    'earth': {'hot': (232, 206, 162), 'thread': (160, 118, 26), 'halo': (104, 72, 16)},  # ochre-bronze: apart from fire's orange on water
    'air': {'hot': (238, 244, 250), 'thread': (230, 236, 248), 'halo': (156, 164, 198)},  # silver-white: apart from water's cyan
}
SEPARATION_MIN = 15  # CIEDE2000 between any two schools' threads added to each acc4 water colour
WEIGHTS = {'core': 1.0, 'node': 1.0, 'rim': 0.8, 'caps': 0.85}

EXT = None


def flip(o):
    return F(I(o).transpose(Image.Transpose.FLIP_TOP_BOTTOM))


def mirror(o):
    """Symmetric about v = 0.5 by construction: the larger of a field and its V flip."""
    return MAX(o, flip(o))


def lace(seed, w=mg.CELL):
    """The vanilla shore foam's lace (foam_shore alpha) over one square cell: a 768 px window of its dense foam band, chosen by the seed,
    mirrored about v = 0.5, its high-pass normalised to 0..1."""
    a = Image.open(os.path.join(EXT, 'foam_shore.png')).getchannel('A')
    x0 = 300 + seed % 600
    y0 = 1080 + (seed // 600) % 140
    crop = a.crop((x0, y0, x0 + 768, y0 + 384)).resize((w, w // 2), Image.BOX)
    full = Image.new('L', (w, w))
    full.paste(crop.transpose(Image.Transpose.FLIP_TOP_BOTTOM), (0, 0))
    full.paste(crop, (0, w // 2))
    return mg.norm_q(F(full) * (1 / 255.0))


def stain():
    """The vanilla wetness decal's soft alpha (decal_water_a), as a 0..1 modulation of the halo."""
    a = Image.open(os.path.join(EXT, 'decal_water_a.png')).getchannel('A').resize((mg.CELL, mg.CELL), Image.BOX)
    return mirror(F(a) * (1 / 255.0)) * 0.5 + 0.5


class Cell:
    """What mg.vein_masks reads: the detail its edges break up along, and the masks it writes."""

    def __init__(self, detail):
        self.detail = detail
        self.masks = {}


def colour(school, masks, lacef, seed):
    pal = PALETTE[school]
    if pal['thread'] is None:  # weave: opal threads, the play of colour along the lace
        t = clamp01(lacef * 0.45 + mg.noise(40, seed) * 0.55)
        thread = mg.ramp_lut(t, mg.OPAL)
    else:
        thread = [const(v) for v in mg.c255(pal['thread'])]
    hot, halo = [const(v) for v in mg.c255(pal['hot'])], [const(v) for v in mg.c255(pal['halo'])]
    mod = lacef * 0.3 + 0.7  # the foam lace inside every thread
    rgb = [h * (lacef * 0.5 + 0.5) for h in halo]
    for name, paint in (('rim', [c * mod * 0.8 for c in thread]), ('caps', [c * mod for c in thread]), ('core', [lerp(c * mod, hc, masks['axis']) for c, hc in zip(thread, hot)]),
                        ('node', hot)):
        m = masks[name]
        rgb = [lerp(a, b, m) for a, b in zip(rgb, paint)]
    return [clamp01(c) for c in rgb]


def squash(img):
    """A 512 px square stamp to a 512 x 256 cell: the inner 480 px height to 224, the margins transparent."""
    if img.mode == 'RGBA':  # colour and alpha apart: a premultiplied resize garbles the colour of near-transparent texels
        rgb, a = squash(img.convert('RGB')), squash(img.getchannel('A'))
        rgb.putalpha(a)
        return rgb
    inner = img.crop((0, mg.MARGIN, mg.CELL, mg.CELL - mg.MARGIN)).resize((CELL_W, INNER_H), Image.BOX)  # a box filter: no overshoot, symmetric
    out = Image.new(img.mode, (CELL_W, CELL_H))
    out.paste(inner, (0, MARGIN))
    return out


def build_school(school):
    seed = int(hashlib.sha256(('water/%s' % school).encode()).hexdigest()[:8], 16)
    lacef = mirror(lace(seed))
    detail = mg.hpn(lacef, 2.5, 3.0)
    soft = stain()
    style = mg.vein_style(school)
    win = mg.window()
    cells, trunk = {}, None
    for kind in COLUMNS:
        cell = Cell(detail)
        sk = mg.vein_masks(cell, kind, style, seed)
        masks = {k: mirror(v) for k, v in cell.masks.items()}
        rgb = colour(school, masks, lacef, seed)
        halo = masks['halo'] * (lacef * 0.6 + 0.4) * soft * HALO_ALPHA_MAX
        alpha = MIN(halo, const(HALO_ALPHA_MAX))
        for name, w in WEIGHTS.items():
            alpha = MAX(alpha, masks[name] * w)
        rgba = mg.to_rgb(rgb)
        rgba.putalpha(to_l(alpha))
        height = blur(MAX(masks['core'], masks['node']) + masks['rim'] * 0.4 + masks['caps'] * 0.3, 2.0)
        dx, dy = mg.grad(height)
        n_img = mg.normal_img(MIN(MAX(dx * -1.2, -0.6), 0.6), MIN(MAX(dy * -1.2, -0.6), 0.6))
        if kind == 'trunk':
            rgba, n_img = mg.periodic(rgba), mg.renorm(mg.periodic(n_img))
            trunk = (rgba, n_img)
        elif kind in ('fork', 'tip'):
            rgba, n_img = mg.head_blend(rgba, trunk[0]), mg.renorm(mg.head_blend(n_img, trunk[1]))
        raw = F(rgba.getchannel('A')) * (1 / 255.0) * win
        a_raw = to_l(raw)
        a_img = mg.apply_feather(raw, kind)
        rgba.putalpha(a_img)
        core = to_l(MAX(masks['core'], masks['node']) * win)
        haloonly = to_l(sstep(0.05, 0.3, masks['halo']) * (sstep(0.02, 0.1, MAX(MAX(masks['core'], masks['rim']), MAX(masks['caps'], masks['node']))) * -1.0 + 1.0) * win)
        cells[kind] = {'d': squash(rgba), 'n': mg.renorm(squash(n_img)), 'alphaRaw': squash(a_raw), 'core': squash(core), 'halo': squash(haloonly),
                       'skeleton': {'mains': len(sk['mains']), 'capillaries': len(sk['caps']), 'node': sk['node'] is not None}}
    return cells


# ------------------------------------------------------------------ gates
def lin(c):
    return mg.srgb_to_lin(c / 255.0)


def to_srgb8(v):
    v = max(0.0, min(1.0, v))
    return 255.0 * (v * 12.92 if v <= 0.0031308 else 1.055 * v ** (1 / 2.4) - 0.055)


SRGB_LUT = [int(round(to_srgb8(i / 255.0))) for i in range(256)]


def additive(cell_d, weight_img, water):
    """The mean colour a stamp's region adds to a water colour (the water shader: surface + albedo x alpha, lighting 1, linear light),
    and its CIEDE2000 against the bare water."""
    w = F(weight_img) * (1 / 255.0)
    if mg.mean(w) <= 1e-6:
        return None
    a = F(cell_d.getchannel('A')) * (1 / 255.0)
    ch = mg.lin_channels(cell_d.convert('RGB'))
    add = [mg.wmean(c * a, w) for c in ch]
    base = [lin(v) for v in water]
    lab_w = mg.lin_to_lab(*base)
    lab_s = mg.lin_to_lab(*[min(1.0, b + d) for b, d in zip(base, add)])
    return round(mg.de2000(lab_w, lab_s), 2), [round(to_srgb8(min(1.0, b + d)), 1) for b, d in zip(base, add)]


def thread_lin(school):
    """A school's thread colour in linear light (weave: the mean of its opal keys)."""
    t = PALETTE[school]['thread']
    keys = [mg.c255(c) for _, c in mg.OPAL] if t is None else [mg.c255(t)]
    return [sum(mg.srgb_to_lin(k[i]) for k in keys) / len(keys) for i in range(3)]


def separation():
    """Every pair of schools' threads added to each acc4 water colour (linear light, lighting 1): the closest pair per water and overall."""
    pairs, fail = [], []
    for i, a in enumerate(SCHOOLS):
        for b in SCHOOLS[i + 1:]:
            ta, tb = thread_lin(a), thread_lin(b)
            de = {}
            for name, water in WATERS.items():
                base = [lin(v) for v in water]
                de[name] = round(mg.de2000(mg.lin_to_lab(*[min(1.0, x + y) for x, y in zip(base, ta)]), mg.lin_to_lab(*[min(1.0, x + y) for x, y in zip(base, tb)])), 2)
            pairs.append({'pair': [a, b], 'de': de, 'min': min(de.values())})
            if min(de.values()) < SEPARATION_MIN:
                fail.append('separation %s/%s: thread on water dE %s < %d' % (a, b, de, SEPARATION_MIN))
    pairs.sort(key=lambda p: p['min'])
    return {'min': pairs[0]['min'], 'closest': pairs[:5], 'pairs': pairs}, fail


def gate(school, cells, tmpdir):
    out = {'cells': [], 'failures': []}
    fail = out['failures']
    for kind in COLUMNS:
        c = cells[kind]
        d = c['d']
        a = d.getchannel('A')
        margin = max(a.crop(box).getextrema()[1] for box in ((0, 0, CELL_W, MARGIN), (0, CELL_H - MARGIN, CELL_W, CELL_H), (0, 0, MARGIN, CELL_H), (CELL_W - MARGIN, 0, CELL_W, CELL_H)))
        sym = ImageChops.difference(a, a.transpose(Image.Transpose.FLIP_TOP_BOTTOM)).getextrema()[1]
        reads = {k: additive(d, c['core'], v) for k, v in WATERS.items()}
        halo = {k: additive(d, c['halo'], v) for k, v in WATERS.items()}
        core_px = [p for p, m in zip(d.get_flattened_data(), c['core'].get_flattened_data()) if m >= 128 and p[3] >= 64]
        clipped = sum(1 for p in core_px if max(p[:3]) >= 254) / float(max(1, len(core_px)))
        row = {'kind': kind, 'marginAlphaMax': margin, 'vSymmetryMaxAlpha': sym, 'coreOnWaterDe': {k: v[0] for k, v in reads.items() if v},
               'coreOnWaterSrgb': {k: v[1] for k, v in reads.items() if v}, 'haloOnWaterDe': {k: v[0] for k, v in halo.items() if v},
               'coreClippedShare': round(clipped, 4), 'alphaMean': round(mg.mean(F(a) * (1 / 255.0)), 4), 'skeleton': c['skeleton']}
        out['cells'].append(row)
        if margin:
            fail.append('%s %s: margin alpha %d' % (school, kind, margin))
        if sym > 2:
            fail.append('%s %s: v-symmetry %d/255 > 2' % (school, kind, sym))
        if any(v[0] < 15 for v in reads.values() if v):
            fail.append('%s %s: core on water dE %s < 15' % (school, kind, row['coreOnWaterDe']))
        if any(v[0] > 8 for v in halo.values() if v):
            fail.append('%s %s: halo on water dE %s > 8' % (school, kind, row['haloOnWaterDe']))
        if clipped > 0.01:
            fail.append('%s %s: %.1f%% of core texels clip' % (school, kind, 100 * clipped))
    # chain: the trunk's raw alpha repeats every PERIOD texels (u 0..0.2 == u 0.8..1), and the feathered head and tail sum to one stamp
    t = cells['trunk']
    raw, fea = t['alphaRaw'], t['d'].getchannel('A')
    x0, x1 = mg.MARGIN, mg.MARGIN + mg.OVERLAP
    head, tail = raw.crop((x0, 0, x1, CELL_H)), raw.crop((x0 + mg.PERIOD, 0, x1 + mg.PERIOD, CELL_H))
    period_mae = mg.mean(F(ImageChops.difference(head, tail))) * (1.0 / 255.0) * 255
    fh, ft = F(fea.crop((x0, 0, x1, CELL_H))) * (1 / 255.0), F(fea.crop((x0 + mg.PERIOD, 0, x1 + mg.PERIOD, CELL_H))) * (1 / 255.0)
    joint = (fh * -1.0 + 1.0) * (ft * -1.0 + 1.0) * -1.0 + 1.0  # one stamp over the other
    joint_mae = mg.mean(MAX(joint - F(head) * (1 / 255.0), F(head) * (1 / 255.0) - joint)) * 255
    out['chain'] = {'periodAlphaMae': round(period_mae, 3), 'jointAlphaMae': round(joint_mae, 3)}
    if period_mae > 4 or joint_mae > 4:
        fail.append('%s: chain alpha MAE period %.2f joint %.2f > 4/255' % (school, period_mae, joint_mae))
    return out


def bcn(d_atlas, n_atlas, tmpdir):
    rt = mg.bcn_roundtrip(d_atlas, 'DXT5', tmpdir, 'magicwater_d')
    w = mg.visible(d_atlas)
    res = {'diffuseDxt5RgbMaeVisible': round(sum(mg.mae(d_atlas, rt, b, w) for b in 'RGB') / 3, 3),
           'diffuseDxt5RgbMae': round(sum(mg.mae(d_atlas, rt, b) for b in 'RGB') / 3, 3), 'alphaMae': round(mg.mae(d_atlas, rt, 'A'), 3)}
    os.remove(os.path.join(tmpdir, 'rt_magicwater_d_DXT5.dds'))
    return res


def review(d_atlas, path):
    """Each cell added to the deep sea and to the shallow lake colour (the water shader's additive blend), lighting 1."""
    sheet = Image.new('RGB', (ATLAS * 2 + 16, ATLAS), (0, 0, 0))
    for k, (name, water) in enumerate((('deepSea', WATERS['deepSea']), ('shallowLakeA', WATERS['shallowLakeA']))):
        base = [lin(v) for v in water]
        a = F(d_atlas.getchannel('A')) * (1 / 255.0)
        ch = mg.lin_channels(d_atlas.convert('RGB'))
        out = []
        for i in range(3):
            o = MIN(ch[i] * a + base[i], 1.0)
            out.append(to_l(o).point(SRGB_LUT))
        sheet.paste(Image.merge('RGB', out), (k * (ATLAS + 16), 0))
    sheet.resize((sheet.width // 2, sheet.height // 2), Image.BOX).save(path)


def chain_review(d_atlas, path, ppu=37.0, length=8.0, width=3.0):
    """Per school, trunk x3 -> fork and trunk x3 -> tip at the contract spacing, added to the deep sea at map scale (37 px per unit)."""
    lw, lh = int(length * ppu), int(width * WIDTH_SCALE * ppu)
    step = int(0.75 * length * ppu)
    rows = []
    base = tuple(int(v) for v in WATERS['deepSea'])
    for r, school in enumerate(SCHOOLS):
        strip_w = 3 * step + lw
        acc = Image.new('RGB', (strip_w * 2 + 20, lh + 8), (0, 0, 0))
        for k, last in enumerate(('fork', 'tip')):
            add = Image.new('RGB', (strip_w, lh), (0, 0, 0))
            for i, kind in enumerate(['trunk'] * 3 + [last]):
                c = COLUMNS.index(kind)
                cell = d_atlas.crop((c * CELL_W, r * CELL_H, (c + 1) * CELL_W, (r + 1) * CELL_H)).resize((lw, lh), Image.BILINEAR)
                rgb = Image.composite(cell.convert('RGB'), Image.new('RGB', cell.size), cell.getchannel('A'))
                add.paste(ImageChops.add(add.crop((i * step, 0, i * step + lw, lh)), rgb), (i * step, 0))
            acc.paste(ImageChops.add(Image.new('RGB', add.size, base), add), (k * (strip_w + 20), 4))
        rows.append(acc)
    sheet = Image.new('RGB', (rows[0].width, sum(x.height for x in rows)))
    y = 0
    for x in rows:
        sheet.paste(x, (0, y))
        y += x.height
    sheet.save(path)


def main():
    global EXT
    ap = argparse.ArgumentParser()
    ap.add_argument('--ext', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--modules', help='the game Modules folder: decode the vanilla inputs into --ext first')
    ap.add_argument('--review')
    ap.add_argument('--evidence')
    a = ap.parse_args()
    EXT = a.ext
    mg.EXT = a.ext
    if a.modules:
        mg.extract_inputs(a.modules, a.ext, VANILLA)
    os.makedirs(a.out, exist_ok=True)
    tmp = a.review or a.out
    if a.review:
        os.makedirs(a.review, exist_ok=True)
    d_atlas = Image.new('RGBA', (ATLAS, ATLAS))
    n_atlas = Image.new('RGB', (ATLAS, ATLAS), (128, 128, 255))
    report = {'schema': 'mnbl.magic-water-art', 'version': 1, 'ticket': 'MMR-290',
              'atlas': {'size': ATLAS, 'columns': COLUMNS, 'rows': SCHOOLS, 'cell': [CELL_W, CELL_H], 'marginPx': MARGIN, 'inner': [CELL_W - 2 * MARGIN, INNER_H],
                        'vectorArgument': 'SetVectorArgument(0.25, 0.125, column / 4, row / 8)', 'widthScale': WIDTH_SCALE,
                        'frame': 'u left to right, v top to bottom inside the margin; growth toward +u; the box spans the whole cell; every stamp mirrored about v = 0.5',
                        'chain': 'as magic_ground.py (contract C1): 0.75 L spacing, head u 0..0.2 over tail u 0.8..1, feather in optical depth'},
              'blend': 'the water shader adds albedo x alpha x factor x lighting to the water colour (water_surface_worldmap.rs :400-468); additive only',
              'palette': PALETTE, 'haloAlphaMax': HALO_ALPHA_MAX, 'waters': WATERS,
              'gates': {'seams': 'alpha 0 within the 16 px margin of every cell',
                        'vSymmetry': 'max |a(u, v) - a(u, 1 - v)| <= 2/255 (mirrored by construction)',
                        'chain': "trunk raw alpha at u 0..0.2 equals u 0.8..1 (period 384 texels) and the feathered head over tail composites to one stamp's alpha: MAE <= 4/255 each",
                        'readsOnWater': 'core texels (core and burst node masks) added to each acc4 water colour (linear light, lighting 1): CIEDE2000 >= 15; '
                                        'halo-only texels <= 8, so the halo never reads as a slab',
                        'noClipping': 'at most 1% of core texels (alpha >= 64) have a channel >= 254 (adaptation: a luminous additive thread is not a ground albedo, so the '
                                      "vanilla world-map decal envelope MMR-259 used is recorded by MMR-288's evidence, not applied here)",
                        'separation': 'every pair of schools: thread colours (weave: its opal mean) added to each acc4 water colour (linear light, lighting 1) differ by CIEDE2000 >= %d' % SEPARATION_MIN,
                        'bcn': 'DXT5 stand-in for BC7: diffuse RGB MAE weighted by alpha <= 4.87 (MMR-259 ceiling), alpha MAE <= 3'},
              'schools': {}, 'failures': []}
    for row, school in enumerate(SCHOOLS):
        cells = build_school(school)
        for col, kind in enumerate(COLUMNS):
            d_atlas.paste(cells[kind]['d'], (col * CELL_W, row * CELL_H))
            n_atlas.paste(cells[kind]['n'], (col * CELL_W, row * CELL_H))
        g = gate(school, cells, tmp)
        g['row'] = row
        report['schools'][school] = g
        report['failures'] += g['failures']
        print('%-9s coreDe(deep) %s  haloDe(max) %s  chain %s  failures %d' % (school, [c['coreOnWaterDe'].get('deepSea') for c in g['cells']],
                                                                              max(max(c['haloOnWaterDe'].values() or [0]) for c in g['cells']), g['chain'], len(g['failures'])), flush=True)
        for f in g['failures']:
            print('   ', f)
    sep, sep_fail = separation()
    report['separation'] = sep
    report['failures'] += sep_fail
    print('separation min %.2f %s' % (sep['min'], [(p['pair'], p['min']) for p in sep['closest'][:3]]))
    for f in sep_fail:
        print('   ', f)
    b = bcn(d_atlas, n_atlas, tmp)
    report['bcn'] = b
    if b['diffuseDxt5RgbMaeVisible'] > 4.87 or b['alphaMae'] > 3:
        report['failures'].append('bcn: %s' % b)
    print('bcn', b)
    files = {}
    for suffix, img in (('d', d_atlas), ('n', n_atlas)):
        p = os.path.join(a.out, 'mnbl_magicwater_%s.png' % suffix)
        img.save(p, compress_level=9)
        files[suffix] = {'file': os.path.basename(p), 'sha256': mg.sha(p), 'mode': img.mode, 'size': list(img.size)}
    report['files'] = files
    if a.review:
        review(d_atlas, os.path.join(a.review, 'atlas_on_water.png'))
        chain_review(d_atlas, os.path.join(a.review, 'chains_on_sea.png'))
    report['passed'] = not report['failures']
    manifest = os.path.join(a.ext, 'inputs.json')
    report['inputs'] = 'not recorded (run with --modules)'
    if os.path.isfile(manifest):
        with open(manifest, encoding='utf-8') as f:
            report['inputs'] = json.load(f)
    report['lineage'] = {
        'geometry': "MMR-288's vein kits (tools/art/magic_ground.py vein_skeleton, vein_masks, periodic, head_blend, apply_feather), seeded by "
                    "sha256('water/<school>'), mirrored about v = 0.5 and squashed from the 480 px to the 224 px inner height",
        'lace': "Native foam_shore's alpha (the vanilla shore-foam network), a 768 x 384 window of its dense band per school, mirrored: the edge "
                'break-up of every thread and the lace modulating thread colour and halo alpha',
        'halo': "Native decal_water_a's alpha (the vanilla wetness decal) modulates the halo's alpha",
        'colour': 'per-school luminous palettes (PALETTE) after the FWGC++ school colours and the MMR-288 vein looks; weave uses mg.OPAL',
        'codex': 'not used: no generated imagery fed the atlas'}
    if a.evidence:
        with open(a.evidence, 'w', encoding='utf-8', newline='\n') as f:
            json.dump(report, f, indent=1)
            f.write('\n')
    print('failures:', len(report['failures']))
    return 1 if report['failures'] else 0


if __name__ == '__main__':
    sys.exit(main())
