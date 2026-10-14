#!/usr/bin/env python3
"""Encode an MMR-26 compiled terrain into an owned scene bundle with the proven surface encoder.

Offline only: never installs or launches. Copies the owned encoder template, renames its scene, and runs
worldbridge --surface-* with the compiled heightfield (world units, scale 1) and the compiled rock_k weights.
Usage: python tools/encode_compiled_terrain.py --compiled DIR --scene-name mnbl_... --out NEW_DIR
"""
import argparse
import json
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--compiled', required=True)
    ap.add_argument('--scene-name', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--template', default=str(REPO / 'build/slots/mnbl_world_b'))
    ap.add_argument('--encoder', default=str(REPO / 'build/worldbridge/Release/worldbridge.exe'))
    a = ap.parse_args()
    compiled, out = Path(a.compiled).resolve(), Path(a.out).resolve()
    if not a.scene_name.startswith('mnbl_') or out.name != a.scene_name:
        sys.exit('scene name must start with mnbl_ and equal the output folder name')
    if out.exists():
        sys.exit('output must not exist')
    meta = json.loads((compiled / 'terrain.json').read_text(encoding='utf-8'))
    work = out.parent / (out.name + '.template')
    if work.exists():
        shutil.rmtree(work)
    shutil.copytree(a.template, work)
    tree = ET.parse(work / 'scene.xscene')
    tree.getroot().set('name', a.scene_name)
    tree.write(work / 'scene.xscene', encoding='utf-8', xml_declaration=True)
    command = [a.encoder, '--surface-heightfield', str(compiled / 'heightfield.bin'), '--surface-template', str(work),
               '--surface-output', str(out), '--surface-extent', repr(meta['output']['extent']), '--surface-scale', '1',
               '--surface-weights', str(compiled / 'weights.bin')]
    result = subprocess.run(command, capture_output=True, text=True)
    (out.parent / (out.name + '.encoder.log')).write_text(' '.join(command) + '\n' + result.stdout + result.stderr, encoding='utf-8')
    if result.returncode != 0:
        sys.exit('encoder failed: ' + result.stderr)
    shutil.rmtree(work)
    print(result.stdout.strip())


if __name__ == '__main__':
    main()
