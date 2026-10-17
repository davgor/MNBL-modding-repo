"""Write worldgen/policies/settlement-placement.json (MMR-40) from the installed War Sails campaign map.

Every child of an installed town or castle is placed in its settlement's own frame by composing the child transforms (position, then
rotation_euler applied as Bannerlord's Mat3.ApplyEulerAngles: about up by z, about side by x, about forward by y, then the scale), and
measured in the settlement's gate frame: forward = toward its main_map_city_gate child, side = to the right of it. Each marker offset is the
median over the installed towns and castles; the breachable walls and wall engines are measured per scene level (level_1..3) and slot. The
circle and camp-area scales, the wall meshes and the level sets are the installed ones (most common). Village drop-off distances are measured
the same way. Usage:
  python tools/make_settlement_placement_policy.py [--game-root ROOT] [--out worldgen/policies/settlement-placement.json]
"""
import argparse
import collections
import hashlib
import json
import math
import os
import statistics
import xml.etree.ElementTree as ET

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GAME = r'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord'
MARKERS = ['main_map_castle_gate', 'map_camp_area_1', 'map_camp_area_2', 'map_siege_engine_0', 'map_siege_engine_1', 'map_siege_engine_2',
           'map_siege_engine_3', 'map_siege_ram', 'map_siege_tower']
LEVELS = ['level_1', 'level_2', 'level_3']


def tags(e):
    return [t.get('name') for t in e.findall('tags/tag')]


def levels(e):
    return [l.get('name') for l in e.findall('levels/level')]


def vec(text, default):
    return [float(v) for v in text.split(',')] if text else list(default)


def add(a, b):
    return [a[0] + b[0], a[1] + b[1], a[2] + b[2]]


def scale(a, k):
    return [a[0] * k, a[1] * k, a[2] * k]


class Frame:
    """Rows s, f, u (side, forward, up) as in TaleWorlds.Library.Mat3, plus an origin."""

    def __init__(self, origin=(0.0, 0.0, 0.0), s=(1.0, 0.0, 0.0), f=(0.0, 1.0, 0.0), u=(0.0, 0.0, 1.0)):
        self.o, self.s, self.f, self.u = list(origin), list(s), list(f), list(u)

    def transform(self, p):
        return add(self.o, add(scale(self.s, p[0]), add(scale(self.f, p[1]), scale(self.u, p[2]))))

    def child(self, e):
        t = e.find('transform')
        p = vec(t.get('position') if t is not None else None, (0, 0, 0))
        r = vec(t.get('rotation_euler') if t is not None else None, (0, 0, 0))
        k = vec(t.get('scale') if t is not None else None, (1, 1, 1))
        s, f, u = [1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]
        c, n = math.cos(r[2]), math.sin(r[2]); s, f = add(scale(s, c), scale(f, n)), add(scale(f, c), scale(s, -n))      # about up
        c, n = math.cos(r[0]), math.sin(r[0]); f, u = add(scale(f, c), scale(u, n)), add(scale(u, c), scale(f, -n))      # about side
        c, n = math.cos(r[1]), math.sin(r[1]); s, u = add(scale(s, c), scale(u, -n)), add(scale(u, c), scale(s, n))      # about forward
        s, f, u = scale(s, k[0]), scale(f, k[1]), scale(u, k[2])
        g = lambda v: add(scale(self.s, v[0]), add(scale(self.f, v[1]), scale(self.u, v[2])))
        return Frame(self.transform(p), g(s), g(f), g(u))

    def scales(self):
        return [math.sqrt(sum(x * x for x in row)) for row in (self.s, self.f, self.u)]


def walk(e, frame):
    for c in e.findall('children/game_entity'):
        fc = frame.child(c)
        yield c, fc
        yield from walk(c, fc)


def med(values):
    return round(statistics.median(values), 4)


def common(values):
    return collections.Counter(values).most_common(1)[0][0] if values else None


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--game-root', default=GAME)
    ap.add_argument('--out', default=os.path.join(REPO, 'worldgen', 'policies', 'settlement-placement.json'))
    a = ap.parse_args()
    path = os.path.join(a.game_root, 'Modules', 'NavalDLC', 'SceneObj', 'Main_map', 'scene.xscene')
    raw = open(path, 'rb').read()
    root = ET.fromstring(raw)
    gate_distance, port_centre, port_gate, dropoff, towers = [], [], [], [], []
    frames = {m: [] for m in MARKERS}
    circle = {'town': [], 'castle': []}
    camp_scale = {'map_camp_area_1': [], 'map_camp_area_2': []}
    walls = collections.defaultdict(list)          # (level, slot) -> [(forward, side)]
    wall_levels = collections.defaultdict(list)    # level -> [level sets]
    wall_meshes = {'map_solid_wall': [], 'map_broken_wall': []}
    defenders = collections.defaultdict(list)      # (engine, level) -> [(forward, side)]
    defender_levels = collections.defaultdict(list)
    banners = {'town': [], 'castle': [], 'village': []}
    for e in root.find('entities'):
        name = e.get('name') or ''
        kind = ('village' if name.startswith('castle_village_') or name.startswith('village_') else
                'town' if name.startswith('town_') else 'castle' if name.startswith('castle_') else None)
        if kind is None:
            continue
        kids = list(walk(e, Frame()))
        banners[kind].append(sum(1 for c, _ in kids if 'map_banner_placeholder' in tags(c)))
        if kind == 'village':
            dropoff.extend(math.hypot(f.o[0], f.o[1]) for c, f in kids if 'main_map_village_dropoff' in tags(c))
            continue
        gates = [f.o for c, f in kids if 'main_map_city_gate' in tags(c)]
        if not gates or math.hypot(gates[0][0], gates[0][1]) < 1e-6:
            continue
        g = gates[0]
        length = math.hypot(g[0], g[1])
        gate_distance.append(length)
        fx, fy = g[0] / length, g[1] / length
        sx, sy = fy, -fx
        local = lambda p: (p[0] * fx + p[1] * fy, p[0] * sx + p[1] * sy)
        level_walls = collections.defaultdict(list)
        towers.append(sum(1 for c, _ in kids if 'map_siege_tower' in tags(c)))
        for c, f in kids:
            t = tags(c)
            for m in t:
                if m in frames:
                    frames[m].append(local(f.o))
                if m in camp_scale:
                    camp_scale[m].append(f.scales()[:2])
                if m == 'map_settlement_circle':
                    circle[kind].append(f.scales()[0])
                if m == 'main_map_city_port':
                    port_centre.append(math.hypot(f.o[0], f.o[1]))
                    port_gate.append(math.hypot(f.o[0] - g[0], f.o[1] - g[1]))
                if m in wall_meshes:
                    mesh = c.find('components/meta_mesh_component')
                    if mesh is not None:
                        wall_meshes[m].append(mesh.get('name'))
            lv = [l for l in levels(c) if l in LEVELS]
            if 'map_breachable_wall' in t and len(lv) == 1:
                level_walls[lv[0]].append(local(f.o)); wall_levels[lv[0]].append(tuple(levels(c)))
            for n in range(4):
                if 'map_defensive_engine_%d' % n in t and len(lv) == 1:
                    defenders[(n, lv[0])].append(local(f.o)); defender_levels[lv[0]].append(tuple(levels(c)))
        for level, items in level_walls.items():
            for slot, p in enumerate(sorted(items, key=lambda q: q[1])[:2]):
                walls[(level, slot)].append(p)
    markers = {m: dict(forward=med([p[0] for p in v]), side=med([p[1] for p in v]), measured=len(v)) for m, v in frames.items()}
    policy = dict(
        schema='mnbl.settlement-placement', version=2,
        source=dict(scene='Modules/NavalDLC/SceneObj/Main_map/scene.xscene', sha256=hashlib.sha256(raw).hexdigest(),
                    method='median over installed towns and castles of each child in its settlement gate frame (forward toward main_map_city_gate, side to its right), child transforms composed (position, rotation_euler as Mat3.ApplyEulerAngles, scale)'),
        gate=dict(distance=med(gate_distance), measured=len(gate_distance), rule='the gate stands this far from the centre, facing its road; candidates turn in 22.5-degree steps from the road direction'),
        markers=markers,
        walls=dict(slots=[dict(level=level, slot=slot, forward=med([p[0] for p in walls[(level, slot)]]), side=med([p[1] for p in walls[(level, slot)]]), measured=len(walls[(level, slot)]))
                          for level in LEVELS for slot in (0, 1) if walls[(level, slot)]],
                   levels={level: list(common(wall_levels[level])) for level in LEVELS if wall_levels[level]},
                   solidMesh=common(wall_meshes['map_solid_wall']), brokenMesh=common(wall_meshes['map_broken_wall']),
                   rule='two breachable wall sections per wall level (Settlement.WallSectionCount is 2 for a fortification; SettlementVisual indexes its current level cache by the targeted section), each with a solid and a broken wall child'),
        wallEngines=dict(slots=[dict(engine=n, level=level, forward=med([p[0] for p in defenders[(n, level)]]), side=med([p[1] for p in defenders[(n, level)]]), measured=len(defenders[(n, level)]))
                                for n in range(4) for level in LEVELS if defenders[(n, level)]],
                         levels={level: list(common(defender_levels[level])) for level in LEVELS if defender_levels[level]},
                         rule='wall engines per wall level, as the installed per-level copies (SettlementVisual keeps those whose level mask matches the current level)'),
        scales=dict(circle={k: med(v) for k, v in circle.items() if v},
                    campArea={k: [med([s[0] for s in v]), med([s[1] for s in v])] for k, v in camp_scale.items() if v},
                    rule='the fortification circle (OnStartup takes its frame for the hover outline) and the camp areas (their spread bounds the besieger camp party positions) at the installed median scales'),
        counts=dict(siegeTowers=max(1, round(statistics.median(towers))), banners=dict(town=4, castle=4, village=1),
                    bannersInstalledMedian={k: (statistics.median(v) if v else 0) for k, v in banners.items()},
                    bannerRule='4 for a fortification and 1 for a village, each with one volume_box child (verified in game since MMR-33: a childless placeholder faults natively)'),
        port=dict(maxCentreDistance=round(max(port_centre), 4), maxGateDistance=round(max(port_gate), 4), measured=len(port_centre),
                  rule='a town port is its navigation port point (MMR-38); it must be water of its basin, within the travel graph port-to-gate limit; beyond the installed centre maximum is reported'),
        dropoff=dict(medianDistance=med(dropoff), maxDistance=round(max(dropoff), 4), measured=len(dropoff),
                     rule="a harbour village (the campaign's harbour flag) drops off at its MMR-28 harbour cell, placed at the cell's interior point nearest the village; it must be water of the cell's basin"),
        ground=dict(maxPadSlopeDegrees=25.0, maxMarkerSlopeDegrees=35.0, interior=0.1, minMarkerSeparation=0.5, turnStepDegrees=22.5,
                    rule='a gate, camp, siege engine, ram or tower must stand on passable land (not mountain) of its settlement land component, within the slope limit and at least minMarkerSeparation from every other settlement centre and marker; wall sections and wall engines need land of the component and the same clearance from other settlements; candidates turn about the centre in turnStep steps, then shrink toward the centre by quarters; where no candidate meets the slope limit the least steep valid one is taken and reported; the pad slope above maxPadSlope is reported, not moved (the centre is the navigation point)'),
        blockade=dict(source='worldgen/policies/naval-content.json blockades (MMR-35)', rule='the installed arc offsets in the port seaward frame, turned in 15-degree steps round the full circle and mirrored (start and end swapped, keeping the arc handedness), the frame moved seaward by half a cell and a cell, then shrunk to 3/4 and 1/2; every ship position BlockadePositionScript.GetBlockadeArc computes (randomization off) must be water of the port basin; otherwise no blockade visual'))
    with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(policy, f, indent=2)
        f.write('\n')
    print('wrote', a.out, 'gate', policy['gate']['distance'], 'walls', len(policy['walls']['slots']), 'wall engines', len(policy['wallEngines']['slots']), 'scales', policy['scales'])


if __name__ == '__main__':
    main()
