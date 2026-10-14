"""Writes crafted split worlds that try to exhaust the reader's memory within the byte contract
(python make_memory_attacks.py OUT_DIR). Each copies the valid fixture and grows one structure:

  many_empty_ids:  60 humans.cores records with 200,000 empty hamlet ids each (about 36 MB);
                   each 3-byte "" would be kept as a std::string without the retained budget.
  nested_keysets:  an unknown optional block of 16 nested objects with 65,000 keys each (about 45 MB);
                   duplicate detection keeps every key of every open object without the key budget.
  root_members:    65,500 extra root members with 190-character names (about 13.6 MB); the root member
                   index (key, location, version) is kept per member, so it is charged to the budget.

The reader must refuse every case with limit_exceeded while its peak working set stays near its budgets.
"""
import json
import os
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))


def copy_valid(out, name):
    target = os.path.join(out, name)
    if os.path.exists(target):
        shutil.rmtree(target)
    shutil.copytree(os.path.join(HERE, 'valid'), target)
    with open(os.path.join(target, 'world.json'), encoding='utf-8') as f:
        return target, json.load(f)


def write(target, world):
    with open(os.path.join(target, 'world.json'), 'w', encoding='utf-8', newline='\n') as f:
        json.dump(world, f, separators=(',', ':'))
        f.write('\n')


def main(out):
    os.makedirs(out, exist_ok=True)
    target, world = copy_valid(out, 'many_empty_ids')
    cores = world['humans']['cores']
    template = dict(cores[0])
    for i in range(60):
        row = dict(template)
        row['site_id'] = 1000 + i
        row['hamlet_ids'] = [''] * 200000
        cores.append(row)
    write(target, world)

    target, world = copy_valid(out, 'nested_keysets')
    block = {'version': 1}
    node = block
    for level in range(16):
        child = {}
        for k in range(65000):
            child['L%02dk%034d' % (level, k)] = 0
        node['next'] = child
        node = child
    world['future_block'] = block
    write(target, world)

    target, world = copy_valid(out, 'root_members')
    for i in range(65500):
        world['x%0189d' % i] = {'version': 1}
    write(target, world)
    print('wrote', out)


if __name__ == '__main__':
    main(sys.argv[1])
