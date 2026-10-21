"""MMR-254: land face types of compiled world bundles -> evidence/world/land-faces.json.

Per bundle, recounted independently of the travel compiler:
- the land-type plane of cells.bin v2 against the shared biome table rule re-derived from the bundle's own navigation-masks.bin biome plane and
  policies/terrain-materials.json: every land cell takes the majority of its four corner samples' faces (a corner's biome terrainType; water
  biomes do not vote), ties to the main diagonal (NW, SE, then NE, SW), the playability default when no corner votes, Snow for a permanent-white
  corner. masks.bin carries no snow months, so the permanent-white corners are the permanentWhite biomes; the script checks the source
  world.json that no other land sample has snowMonths months of snow at the threshold (else it reports the recount as unverified);
- the per-type histogram against travel-graph.json grid.landTypes, and the class plane against grid.classes;
- the road bit (flags plane bit 0) against the cells of every road piece (grid.roadCells);
- RT-30: where the six installed terrain incidents sample (IncidentsCampaignBehaviour, 1.4.8 IL; the red team counted seven):
    incident_sandstorm_warning, incident_heat_and_dust   Desert under the main party (land cells of type Desert),
    incident_water_supplies                              Steppe or Desert under the besieging main party (fortifications whose
                                                         3-unit disc has such a face, the siege camp's ground),
    incident_preemptive_work, incident_through_proper_channels   a Desert face within 3 units of a (player-owned) fortification's position
                                                         (the predicate's 0.25-unit grid over the disc),
    incident_cries_in_the_mist                           a Forest face within 3 units of a village's position;
  positions are the travel graph's placement points (the resolved in-game positions may move by up to the snap radius).
With --navigation, the navmesh land faces per type (tools/Test-WorldNavigation.ps1's navigation-contract.json, landFacesByType: one face per
physical cell, strips included, so twice the cells on a 256-column terrain) are recorded per world and checked to be the land types' cells
at least times the physical / logical column ratio (bank fans beside decks add faces), with the same types.
Usage: python tools/summarize_land_faces.py OUT.json REPO BUNDLE_DIR [BUNDLE_DIR ...] [--navigation CONTRACT.json] [--label NAME ...]
"""
import collections
import json
import os
import struct
import sys

CLASSES = ['land', 'mountain', 'sea', 'lake', 'river']


def load(path):
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f)


def planes(path, magic, count):
    raw = open(path, 'rb').read()
    if raw[:8] != magic:
        raise SystemExit('%s is not %s' % (path, magic.decode()))
    return raw


def disc(spacing):
    """The incident predicates' sample offsets: x, y from -3 to 3 in 0.25 steps with x^2 + y^2 <= 9."""
    steps = [-3 + 0.25 * i for i in range(25)]
    return [(x, y) for x in steps for y in steps if x * x + y * y <= 9.0]


def world_row(repo, bundle, label):
    g = load(os.path.join(bundle, 'compiled', 'travel', 'travel-graph.json'))
    grid = g['grid']
    cols, rows, c0, r0, sp = grid['cols'], grid['rows'], grid['columnOffset'], grid['rowOffset'], grid['spacing']
    n = cols * rows
    cells = planes(os.path.join(bundle, 'compiled', 'travel', 'cells.bin'), b'MNBLTC02', 4)
    cc, rr = struct.unpack('<II', cells[8:16])
    assert (cc, rr) == (cols, rows) and len(cells) == 16 + 4 * n, 'cells.bin size'
    cls = cells[16:16 + n]
    land_type = cells[16 + 2 * n:16 + 3 * n]
    flags = cells[16 + 3 * n:16 + 4 * n]
    # The travel compile's masks (MMR-278: compiled/terrain/masks.bin is the shaped surface's; the travel cells come from these).
    masks = planes(os.path.join(bundle, 'compiled', 'terrain', 'navigation-masks.bin'), b'MNBLMK01', 4)
    side = struct.unpack('<I', masks[8:12])[0]
    biome = masks[12:12 + side * side]
    materials = load(os.path.join(bundle, 'policies', 'terrain-materials.json'))
    play = load(os.path.join(bundle, 'policies', 'playability.json'))
    calib = load(os.path.join(repo, 'worldgen', 'calibration', 'naval-transitions', 'naval-transitions.json'))
    face_id = calib['faceGroups']
    name_of = {v: k for k, v in face_id.items()}
    white_biomes = set(materials['permanentWhite']['biomes'])
    snow_face = face_id[materials['permanentWhite']['terrainType']]
    default = face_id[play['cells']['landFaceGroups']['default']]
    vote_of = {}
    for b in materials['biomes']:
        if b.get('water') or b.get('terrainType') is None:
            vote_of[b['id']] = 0
        else:
            vote_of[b['id']] = snow_face if b['core'] in white_biomes else face_id[b['terrainType']]

    def vote(r, c):
        return vote_of[biome[(r0 + r) * side + c0 + (c % cols)]]

    mismatches = []
    for r in range(rows):
        for c in range(cols):
            i = r * cols + c
            if cls[i] != 0:
                if land_type[i] != 0:
                    mismatches.append((r, c, 'non-land with a type'))
                continue
            corners = [vote(r, c), vote(r + 1, c + 1), vote(r, c + 1), vote(r + 1, c)]
            best, most = 0, 0
            for k in corners:
                if k and corners.count(k) > most:
                    best, most = k, corners.count(k)
            want = best or default
            if land_type[i] != want:
                mismatches.append((r, c, '%s, want %s' % (name_of.get(land_type[i]), name_of.get(want))))
    histogram = collections.Counter(name_of[t] for i, t in enumerate(land_type) if cls[i] == 0)
    classes = collections.Counter(CLASSES[k] for k in cls)

    # The permanent-white assumption: no non-white land sample of the source has a year of snow.
    world = load(os.path.join(bundle, 'source', 'world.json'))
    months = world['seasonal_environment']['months']
    src_biome = world['layers']['biome']
    threshold, need = materials['permanentWhite']['snowThreshold'], materials['permanentWhite']['snowMonths']
    core_of = {b['id']: b['core'] for b in materials['biomes']}
    water_cores = {b['core'] for b in materials['biomes'] if b.get('water')}
    year_snow_elsewhere = 0
    for y in range(len(src_biome)):
        for x in range(len(src_biome[y])):
            core = core_of.get(int(src_biome[y][x]))
            if core in white_biomes or core in water_cores:
                continue
            if sum(1 for m in months if m['snow'][y][x] >= threshold) >= need:
                year_snow_elsewhere += 1

    # Road bit against the road pieces.
    piece_cells = set()
    for road in g['roads']:
        for piece in road['pieces']:
            for cell in piece['path']:
                piece_cells.add(cell[0] * cols + cell[1])
    road_bits = {i for i in range(n) if flags[i] & 1}
    flags_clean = all(f & ~1 == 0 for f in flags)

    # RT-30: where the terrain incidents sample.
    def type_at(x, y):
        r, c = int(y // sp), int(x // sp) % cols
        if r < 0 or r >= rows:
            return None
        i = r * cols + c
        return name_of[land_type[i]] if cls[i] == 0 else None

    offsets = disc(sp)
    camp = g['settlements']
    excluded = {e['id'] for e in g['exclusions']}
    near = collections.OrderedDict((k, collections.Counter()) for k in ('town', 'castle', 'village'))
    totals = collections.Counter()
    for s in camp:
        if s['id'] in excluded or 'point' not in s:
            continue
        x0, y0 = s['point']['x'], s['point']['y']
        seen = {type_at(x0 + dx, y0 + dy) for dx, dy in offsets}
        totals[s['kind']] += 1
        for t in ('Desert', 'Forest', 'Steppe'):
            if t in seen:
                near[s['kind']][t] += 1
        if 'Desert' in seen or 'Steppe' in seen:
            near[s['kind']]['SteppeOrDesert'] += 1
    forts = lambda key: near['town'][key] + near['castle'][key]
    incidents = collections.OrderedDict([
        ('incident_sandstorm_warning', collections.OrderedDict([('samples', 'the main party face'), ('needs', 'Desert'), ('landCells', histogram.get('Desert', 0)), ('landShare', round(histogram.get('Desert', 0) / max(1, sum(histogram.values())), 4))])),
        ('incident_heat_and_dust', collections.OrderedDict([('samples', 'the main party face'), ('needs', 'Desert'), ('landCells', histogram.get('Desert', 0)), ('landShare', round(histogram.get('Desert', 0) / max(1, sum(histogram.values())), 4))])),
        ('incident_water_supplies', collections.OrderedDict([('samples', 'the besieging main party face (a fortification\'s 3-unit disc)'), ('needs', 'Steppe or Desert'), ('fortifications', forts('SteppeOrDesert')), ('of', totals['town'] + totals['castle'])])),
        ('incident_preemptive_work', collections.OrderedDict([('samples', 'a 3-unit disc around the last visited player fortification'), ('needs', 'Desert'), ('fortifications', forts('Desert')), ('of', totals['town'] + totals['castle'])])),
        ('incident_through_proper_channels', collections.OrderedDict([('samples', 'a 3-unit disc around the current player fortification'), ('needs', 'Desert'), ('fortifications', forts('Desert')), ('of', totals['town'] + totals['castle'])])),
        ('incident_cries_in_the_mist', collections.OrderedDict([('samples', 'a 3-unit disc around the last visited village'), ('needs', 'Forest'), ('villages', near['village']['Forest']), ('of', totals['village'])])),
    ])
    return collections.OrderedDict([
        ('label', label), ('worldContentId', os.path.basename(bundle.rstrip('/\\'))), ('cells', n),
        ('classes', dict(sorted(classes.items()))), ('landTypes', dict(sorted(histogram.items()))),
        ('landTypesMatchGraph', dict(sorted(histogram.items())) == grid['landTypes'] and dict(sorted(classes.items())) == {k: v for k, v in sorted(grid['classes'].items()) if v}),
        ('tableRecount', collections.OrderedDict([('mismatches', len(mismatches)), ('examples', mismatches[:5]),
                                                 ('permanentWhiteFromBiomesOnly', year_snow_elsewhere == 0), ('yearSnowElsewhere', year_snow_elsewhere)])),
        ('roads', collections.OrderedDict([('roadBits', len(road_bits)), ('pieceCells', len(piece_cells)), ('graphRoadCells', grid['roadCells']),
                                           ('match', road_bits == piece_cells and len(road_bits) == grid['roadCells'] and flags_clean)])),
        ('nearSettlements', collections.OrderedDict((k, dict(sorted(v.items()))) for k, v in near.items())), ('settlements', dict(sorted(totals.items()))),
        ('terrainIncidents', incidents)])


def main():
    args = sys.argv[1:]
    out, repo = args[0], args[1]
    labels = []
    if '--label' in args:
        i = args.index('--label')
        labels = args[i + 1:]
        args = args[:i]
    navigation = None
    if '--navigation' in args:
        i = args.index('--navigation')
        navigation = load(args[i + 1])
        args = args[:i] + args[i + 2:]
    bundles = args[2:]
    worlds = [world_row(repo, b, labels[k] if k < len(labels) else str(k)) for k, b in enumerate(bundles)]
    for w in worlds:
        if navigation is None:
            continue
        rec = next((x for x in navigation['worlds'] if x['worldContentId'] == w['worldContentId']), None)
        if rec is None:
            w['navmesh'] = None
            continue
        faces = rec['landFacesByType']
        ratio = rec['build']['physicalColumns'] / 128.0
        w['navmesh'] = collections.OrderedDict([('landFacesByType', faces), ('physicalColumns', rec['build']['physicalColumns']), ('faces', rec['build']['faces']),
                                                ('matchesCells', sorted(faces) == sorted(w['landTypes']) and all(faces[k] >= w['landTypes'][k] * ratio for k in faces))])
    ok = all(w['landTypesMatchGraph'] and w['tableRecount']['mismatches'] == 0 and w['tableRecount']['permanentWhiteFromBiomesOnly'] and w['roads']['match'] and
             (w.get('navmesh') is None and navigation is None or (w.get('navmesh') or {}).get('matchesCells')) for w in worlds)
    doc = collections.OrderedDict([
        ('schema', 'mnbl.evidence.land-faces'), ('version', 1), ('ticket', 'MMR-254'),
        ('recipe', 'refresh-fixtures (Run-SeedSweep -Stage generate,bundle; size 129, phase 16) for seeds 42 (A\') and 73 (B\'), then this script; '
                   'navmesh face histograms: tools/Test-WorldNavigation.ps1 (landFacesByType per world)'),
        ('allChecks', ok), ('worlds', worlds)])
    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        f.write(json.dumps(doc, indent=1) + '\n')
    for w in worlds:
        print('%s: land %s; table recount mismatches %d; graph %s; roads %s; incidents %s' % (
            w['label'], w['landTypes'], w['tableRecount']['mismatches'], w['landTypesMatchGraph'], w['roads']['match'],
            {k: v.get('landCells', v.get('fortifications', v.get('villages'))) for k, v in w['terrainIncidents'].items()}))
    print('checks', ok)
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
