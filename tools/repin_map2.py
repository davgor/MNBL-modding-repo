"""Map epic 2 re-pin chain (MMR-248): recompute every pinned hash and policy literal that a tuned map-epic-2 policy changes, in
dependency order, and print what changed. Idempotent: a second run changes nothing and prints "unchanged".

Integrators run it after every merge into map2/int, and a ticket that tunes a pinned value (for example MMR-278's final
projection.json verticalScale) runs it on its branch:

    python tools/repin_map2.py            # rewrite the pins that differ and print each change
    python tools/repin_map2.py --check    # change nothing; exit 1 if any pin differs (tests/ci/test_map2_policies.py runs this)

The chain (docs/biome-presentation.md "Re-pin chain"):
 1. docs/formats/navmesh-bin.md -> worldgen/calibration/naval-transitions/naval-transitions.json source.faceGroupTableSha256
 2. naval-transitions.json -> worldgen/policies/playability.json calibration.sha256 (playability.json line 6)
 3. playability.json -> naval-content.json water.playabilitySha256; economy.json -> startup.json provenance.economySha256;
    culture-presentations.json -> factions.json provenance.culturePresentationsSha256 (RT-27's culture mapping);
    worldgen/templates/settlements/prototypes.json -> mission-scenes.json sources[].sha256 (MMR-258's looks)
 4. test literals that restate a pinned value: the calibration hash in travel_graph_tests.cpp, playability mountainSlope in
    travel_graph_tests.cpp, projection verticalScale in projection_tests.cpp, slopeRock.full/start in terrain_resampling_tests.cpp.
Every pin is the SHA-256 of the pinned file's bytes. Each step reads the files as the previous steps left them.
"""
import argparse
import hashlib
import io
import json
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# (pinning file, JSON path to the pin, pinned file). A path element that is a dict selects the array item whose members match it.
PINS = [
    ('worldgen/calibration/naval-transitions/naval-transitions.json', ['source', 'faceGroupTableSha256'], 'docs/formats/navmesh-bin.md'),
    ('worldgen/policies/playability.json', ['calibration', 'sha256'], 'worldgen/calibration/naval-transitions/naval-transitions.json'),
    ('worldgen/policies/naval-content.json', ['water', 'playabilitySha256'], 'worldgen/policies/playability.json'),
    ('worldgen/policies/startup.json', ['provenance', 'economySha256'], 'worldgen/policies/economy.json'),
    ('worldgen/policies/factions.json', ['provenance', 'culturePresentationsSha256'], 'worldgen/policies/culture-presentations.json'),
    ('worldgen/policies/mission-scenes.json', ['sources', {'path': 'worldgen/templates/settlements/prototypes.json'}, 'sha256'], 'worldgen/templates/settlements/prototypes.json'),
]


def read(rel):
    with io.open(os.path.join(REPO, rel), encoding='utf-8', newline='') as f:
        return f.read()


def write(rel, text):
    with io.open(os.path.join(REPO, rel), 'w', encoding='utf-8', newline='') as f:
        f.write(text)


def sha(rel):
    with open(os.path.join(REPO, rel), 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def at(doc, path):
    node = doc
    for part in path:
        if isinstance(part, dict):
            matches = [x for x in node if all(x.get(k) == v for k, v in part.items())]
            if len(matches) != 1:
                raise SystemExit('no unique item %r' % part)
            node = matches[0]
        else:
            node = node[part]
    return node


def token(rel, path):
    """The JSON number token of a policy value, exactly as the file spells it (so a C++ literal matches the text)."""
    value = at(json.loads(read(rel)), path)
    key = path[-1]
    found = re.findall(r'"%s"\s*:\s*(-?[0-9][0-9.eE+-]*)' % re.escape(key), read(rel))
    spelled = [t for t in found if float(t) == value]
    if not spelled:
        raise SystemExit('%s %s: no number token %r' % (rel, '.'.join(map(str, path)), value))
    return spelled[0]


def pin_step(pinning, path, pinned, check, changes):
    text = read(pinning)
    old = at(json.loads(text), path)
    new = sha(pinned)
    if old == new:
        return
    key = path[-1]
    pattern = re.compile(r'("%s"\s*:\s*")%s(")' % (re.escape(key), re.escape(old)))
    if len(pattern.findall(text)) != 1:
        raise SystemExit('%s: the pin %s=%s is not unique in the text' % (pinning, key, old))
    changes.append('%s %s: %s -> %s (sha256 of %s)' % (pinning, '.'.join(p if isinstance(p, str) else '[%s]' % p for p in path), old, new, pinned))
    if not check:
        write(pinning, pattern.sub(lambda m: m.group(1) + new + m.group(2), text))


def literal_step(rel, pattern, value, what, check, changes):
    text = read(rel)
    rx = re.compile(pattern)
    found = rx.findall(text)
    if len(found) != 1:
        raise SystemExit('%s: the %s literal is not unique (%d matches)' % (rel, what, len(found)))
    current = found[0] if isinstance(found[0], str) else found[0][1]
    if current == value:
        return
    changes.append('%s %s: %s -> %s' % (rel, what, current, value))
    if not check:
        write(rel, rx.sub(lambda m: m.group(1) + value + m.group(len(m.groups())), text))


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('--check', action='store_true', help='change nothing; exit 1 when a pin or literal differs')
    a = ap.parse_args()
    changes = []
    for pinning, path, pinned in PINS:
        pin_step(pinning, path, pinned, a.check, changes)
    play, proj, mat = 'worldgen/policies/playability.json', 'worldgen/policies/projection.json', 'worldgen/policies/terrain-materials.json'
    calibration_pin = sha(PINS[1][2])   # what playability.json pins after step 2
    literal_step('src/WorldBridge/tests/travel_graph_tests.cpp', r'(mutated\("calibration_altered", ")([0-9a-f]{64})(")', calibration_pin, 'calibration hash', a.check, changes)
    literal_step('src/WorldBridge/tests/travel_graph_tests.cpp', r'(policy\.mountain_slope == )([0-9.]+)( )', token(play, ['cells', 'mountainSlope']), 'mountainSlope', a.check, changes)
    literal_step('src/WorldBridge/tests/projection_tests.cpp', r'(p\.vertical_scale == )([0-9.]+)( )', token(proj, ['verticalScale']), 'verticalScale', a.check, changes)
    full, start = token(mat, ['slopeRock', 'full']), token(mat, ['slopeRock', 'start'])
    literal_step('src/WorldBridge/tests/terrain_resampling_tests.cpp', r'(mutated\("bad_ramp", "\\"full\\": )([0-9.]+)(")', full, 'slopeRock.full', a.check, changes)
    literal_step('src/WorldBridge/tests/terrain_resampling_tests.cpp', r'(mutated\("bad_ramp", "\\"full\\": [0-9.]+", "\\"full\\": )([0-9.]+)(")', start, 'slopeRock.start', a.check, changes)
    for c in changes:
        print(('differs: ' if a.check else 'changed: ') + c)
    if not changes:
        print('unchanged: every map epic 2 pin and literal matches')
    if a.check and changes:
        sys.exit(1)


if __name__ == '__main__':
    main()
