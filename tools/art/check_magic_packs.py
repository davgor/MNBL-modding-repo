"""MMR-259: verify the published magic-ground packs by their headers.

For every visible school the module's published packs (Modules/MNBL.ProceduralWorld/AssetPackages/*.tpac) must hold:
  - the textures mnbl_magic_<school>_d / _n / _s at the atlas size, imported from this module's AssetSources/magic_ground, with
    the flags the decal atlas needs (_d: has_alpha and dont_resize_in_atlas, BC7 or DXT5; _n: BC5; _s: DXT1, BC1 or DXT5);
  - the decal material mnbl_magic_<school> with the vanilla world-map decal flags (gbuffer_alpha_blend, use_albedo,
    render_on_terrain; use_normalmap and use_specular because the set has both maps), as mainmap_decal_undervillage has.
Only the pack headers are read (tools/art/tpac_textures.py), the way the game resolves materials by name.
MMR-290 (--water): the magic water decal material mnbl_magicwater with its textures mnbl_magicwater_d (BC7 or DXT5, has_alpha,
dont_resize_in_atlas) and _n (BC5) at 2048, flags gbuffer_alpha_blend, use_albedo, render_on_terrain and use_normalmap, and none of
water_foam_decal, albedo_multiply, hardlight_blend, render_on_objects, render_on_grass, height_masked, override_visibility_checks
(docs/magic-water.md "Publish request").
MMR-291 (--overlay): the optional overlay mesh materials of the route's publish request P (results/MMR-291-route.md): mnbl_overlay_vein_<school>
x 8 and mnbl_overlay_magicwater, each an override of vanilla torch_flame_base: the template's shader (its GUID, read from the installed Native
materials.tpac), alpha blend modulate, material flags dont_draw_to_gbuffer, no_modify_depth_buffer, two_sided, shader flags use_vertex_colors and
self_illumination, and none of use_animated_texture_coords, alpha_test, do_not_use_alpha, gbuffer_alpha_blend. The runtime works without them
(a copy of the template with the published textures); they matter only if the decal lab shows the copies draw nothing.
Usage: python tools/art/check_magic_packs.py [--packs DIR] [--out JSON] [--module MNBL.ProceduralWorld] [--prefix mnbl_magic_] [--water | --overlay]
Exit 1 when anything is missing or wrong, 2 when DIR holds no .tpac (nothing published yet).
"""
import argparse
import json
import os
import re
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import tpac_textures  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SCHOOLS = ['weave', 'umbral', 'infernal', 'radiant', 'fire', 'water', 'earth', 'air']
ATLAS = 2048
TEXTURES = {'d': ({'BC7', 'DXT5', 'BC3'}, ['has_alpha', 'dont_resize_in_atlas']), 'n': ({'BC5'}, []), 's': ({'DXT1', 'BC1', 'DXT5', 'BC3'}, [])}
MATERIAL_FLAGS = ['gbuffer_alpha_blend', 'use_albedo', 'render_on_terrain', 'use_normalmap', 'use_specular']
WATER = 'mnbl_magicwater'
WATER_TEXTURES = {'d': TEXTURES['d'], 'n': TEXTURES['n']}
WATER_FLAGS = ['gbuffer_alpha_blend', 'use_albedo', 'render_on_terrain', 'use_normalmap']
WATER_FORBIDDEN = ['water_foam_decal', 'albedo_multiply', 'hardlight_blend', 'render_on_objects', 'render_on_grass', 'height_masked', 'override_visibility_checks']


def materials(path, prefix, names=MATERIAL_FLAGS):
    """Material records named <prefix>...: the length-prefixed name followed (within the record) by its flag strings."""
    d, _, _ = tpac_textures.header(path)
    if d is None:
        return {}
    out = {}
    # The name is read by its u32 length prefix: the byte after it can be a letter (0x62 in the editor's material records).
    for m in re.finditer(re.escape(prefix.encode()), d):
        if m.start() < 4:
            continue
        size = struct.unpack_from('<I', d, m.start() - 4)[0]
        n = d[m.start():m.start() + size]
        if size > 128 or not re.fullmatch(rb'[a-z0-9_]+', n) or n.endswith((b'_d', b'_n', b'_s')):
            continue
        seg = d[m.start() + size:m.start() + size + 420]
        out[n.decode()] = [f for f in names if f.encode() in seg]
    return out


def check(packs, module, prefix, schools=SCHOOLS, atlas=ATLAS):
    files = sorted(os.path.join(packs, f) for f in os.listdir(packs) if f.endswith('.tpac')) if os.path.isdir(packs) else []
    textures, mats = {}, {}
    for p in files:
        for r in tpac_textures.records(p):
            if r['name'].startswith(prefix):
                textures[r['name']] = dict(r, pack=os.path.basename(p))
        mats.update(materials(p, prefix))
    rows, problems = [], []
    for school in schools:
        for suffix, (formats, flags) in TEXTURES.items():
            name = '%s%s_%s' % (prefix, school, suffix)
            r = textures.get(name)
            row = {'name': name, 'found': r is not None}
            if r:
                row.update({k: r[k] for k in ('pack', 'format', 'width', 'height', 'flags', 'source')})
                want_src = '$BASE/Modules/%s/AssetSources/' % module
                bad = []
                if r['format'] not in formats:
                    bad.append('format %s not in %s' % (r['format'], sorted(formats)))
                if (r['width'], r['height']) != (atlas, atlas):
                    bad.append('size %dx%d' % (r['width'], r['height']))
                bad += ['missing flag ' + f for f in flags if f not in r['flags']]
                if not r['source'].startswith(want_src):
                    bad.append('source %s outside %s' % (r['source'], want_src))
                if r['chunk'] is None:
                    bad.append('no data chunk')
                row['problems'] = bad
                problems += ['%s: %s' % (name, b) for b in bad]
            else:
                problems.append('%s: not in any pack' % name)
            rows.append(row)
        mname = prefix + school
        flags = mats.get(mname)
        rows.append({'name': mname, 'kind': 'material', 'found': flags is not None, 'flags': flags})
        if flags is None:
            problems.append('%s: material not in any pack' % mname)
        else:
            problems += ['%s: missing flag %s' % (mname, f) for f in MATERIAL_FLAGS if f not in flags]
    return {'schema': 'mnbl.magic-packs', 'version': 1, 'ticket': 'MMR-259', 'packs': [os.path.basename(f) for f in files],
            'rows': rows, 'problems': problems, 'passed': bool(files) and not problems}


def check_water(packs, module, atlas=ATLAS):
    """The magic water material and its two textures (MMR-290)."""
    files = sorted(os.path.join(packs, f) for f in os.listdir(packs) if f.endswith('.tpac')) if os.path.isdir(packs) else []
    textures, mats = {}, {}
    for p in files:
        for r in tpac_textures.records(p):
            if r['name'].startswith(WATER):
                textures[r['name']] = dict(r, pack=os.path.basename(p))
        mats.update(materials(p, WATER, WATER_FLAGS + WATER_FORBIDDEN))
    rows, problems = [], []
    for suffix, (formats, flags) in WATER_TEXTURES.items():
        name = '%s_%s' % (WATER, suffix)
        r = textures.get(name)
        row = {'name': name, 'found': r is not None}
        if r:
            bad = ([] if r['format'] in formats else ['format %s not in %s' % (r['format'], sorted(formats))]) +                   ([] if (r['width'], r['height']) == (atlas, atlas) else ['size %dx%d' % (r['width'], r['height'])]) +                   ['missing flag ' + f for f in flags if f not in r['flags']] +                   ([] if r['source'].startswith('$BASE/Modules/%s/AssetSources/' % module) else ['source %s outside the module' % r['source']])
            row.update({k: r[k] for k in ('pack', 'format', 'width', 'height', 'flags', 'source')}, problems=bad)
            problems += ['%s: %s' % (name, b) for b in bad]
        else:
            problems.append('%s: not in any pack' % name)
        rows.append(row)
    flags = mats.get(WATER)
    rows.append({'name': WATER, 'kind': 'material', 'found': flags is not None, 'flags': flags})
    if flags is None:
        problems.append('%s: material not in any pack' % WATER)
    else:
        problems += ['%s: missing flag %s' % (WATER, f) for f in WATER_FLAGS if f not in flags]
        problems += ['%s: flag %s must not be set' % (WATER, f) for f in WATER_FORBIDDEN if f in flags]
    return {'schema': 'mnbl.magic-water-packs', 'version': 1, 'ticket': 'MMR-290', 'packs': [os.path.basename(f) for f in files],
            'rows': rows, 'problems': problems, 'passed': bool(files) and not problems}


OVERLAY = ['mnbl_overlay_vein_' + s for s in SCHOOLS] + ['mnbl_overlay_magicwater']
OVERLAY_TEMPLATE = 'torch_flame_base'
OVERLAY_FLAGS = ['dont_draw_to_gbuffer', 'no_modify_depth_buffer', 'two_sided', 'use_vertex_colors', 'self_illumination']
OVERLAY_FORBIDDEN = ['use_animated_texture_coords', 'alpha_test', 'do_not_use_alpha', 'gbuffer_alpha_blend']
GAME = r'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord'


def material_record(d, name):
    """A material record's alpha blend mode, shader GUID (hex) and the strings of its record (MMR-291 survey: u32 len, name, ...,
    u32 0, u32 1, u32 len, vertex layout, u32 len, blend mode, 16 bytes shader GUID, ... flags), or None."""
    key = struct.pack('<I', len(name)) + name.encode()
    at = d.find(key)   # the u32 length prefix bounds the name (the byte after it may be a letter: the record's size)
    while at >= 0:
        rec = _material_at(d, at + len(key))
        if rec:
            return rec
        at = d.find(key, at + 1)   # the name also appears where another record refers to it
    return None


def _material_at(d, start):
    seg = d[start:start + 700]
    for m in re.finditer(rb'(?s)\x00\x00\x00\x00\x01\x00\x00\x00(.{4})([a-z_0-9]{3,40})', seg):
        if struct.unpack('<I', m.group(1))[0] != len(m.group(2)):
            continue
        at = m.end()
        n = struct.unpack_from('<I', seg, at)[0] if at + 4 <= len(seg) else 0
        if not 3 <= n <= 40 or at + 4 + n + 16 > len(seg):
            continue
        blend = seg[at + 4:at + 4 + n]
        if not re.fullmatch(rb'[a-z_]+', blend):
            continue
        guid = seg[at + 4 + n:at + 4 + n + 16]
        strings = [x.decode() for x in re.findall(rb'[a-z_]{4,}', seg[:at + 4 + n + 16 + 300])]
        return {'blend': blend.decode(), 'shader': guid.hex(), 'strings': strings}
    return None


def check_overlay(packs, game=GAME):
    """The overlay mesh materials of publish request P (MMR-291), against the vanilla template's shader."""
    files = sorted(os.path.join(packs, f) for f in os.listdir(packs) if f.endswith('.tpac')) if os.path.isdir(packs) else []
    vanilla, _, _ = tpac_textures.header(os.path.join(game, 'Modules', 'Native', 'AssetPackages', 'materials.tpac'))
    template = material_record(vanilla, OVERLAY_TEMPLATE) if vanilla else None
    rows, problems = [], []
    if template is None:
        problems.append('the template %s is not readable in the installed Native materials.tpac' % OVERLAY_TEMPLATE)
    headers = [(os.path.basename(f), tpac_textures.header(f)[0]) for f in files]
    for name in OVERLAY:
        rec = None; pack = None
        for pname, d in headers:
            rec = material_record(d, name) if d else None
            if rec:
                pack = pname
                break
        row = {'name': name, 'found': rec is not None, 'pack': pack}
        if rec is None:
            problems.append('%s: material not in any pack' % name)
        else:
            flags = [f for f in OVERLAY_FLAGS + OVERLAY_FORBIDDEN if f in rec['strings']]
            bad = ([] if rec['blend'] == 'modulate' else ['blend %s, not modulate' % rec['blend']]) +                   ([] if template is None or rec['shader'] == template['shader'] else ['shader %s, not the template shader %s' % (rec['shader'], template['shader'])]) +                   ['missing flag ' + f for f in OVERLAY_FLAGS if f not in flags] + ['flag %s must not be set' % f for f in OVERLAY_FORBIDDEN if f in flags]
            row.update(blend=rec['blend'], shader=rec['shader'], flags=flags, problems=bad)
            problems += ['%s: %s' % (name, b) for b in bad]
        rows.append(row)
    return {'schema': 'mnbl.overlay-packs', 'version': 1, 'ticket': 'MMR-291', 'packs': [os.path.basename(f) for f in files], 'template': template and {'name': OVERLAY_TEMPLATE, 'blend': template['blend'], 'shader': template['shader']},
            'rows': rows, 'problems': problems, 'passed': bool(files) and not problems}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--packs', default=os.path.join(REPO, 'Modules/MNBL.ProceduralWorld/AssetPackages'))
    ap.add_argument('--module', default='MNBL.ProceduralWorld')
    ap.add_argument('--prefix', default='mnbl_magic_')
    ap.add_argument('--out')
    ap.add_argument('--water', action='store_true', help='check the magic water material (MMR-290) instead of the ground ones')
    ap.add_argument('--overlay', action='store_true', help='check the overlay mesh materials of the MMR-291 publish request P instead')
    ap.add_argument('--template-check', action='store_true', help='with --overlay: check the parser on the installed vanilla template alone')
    a = ap.parse_args()
    if a.overlay and a.template_check:
        d, _, _ = tpac_textures.header(os.path.join(GAME, 'Modules', 'Native', 'AssetPackages', 'materials.tpac'))
        rec = material_record(d, OVERLAY_TEMPLATE)
        print(json.dumps(rec and {'blend': rec['blend'], 'shader': rec['shader'], 'flags': [f for f in OVERLAY_FLAGS + OVERLAY_FORBIDDEN if f in rec['strings']]}))
        return 0 if rec and rec['blend'] == 'modulate' and all(f in rec['strings'] for f in OVERLAY_FLAGS) else 1
    res = check_overlay(a.packs) if a.overlay else check_water(a.packs, a.module) if a.water else check(a.packs, a.module, a.prefix)
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


if __name__ == '__main__':
    sys.exit(main())
