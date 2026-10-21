"""MMR-286: the display-name census of generated worlds into evidence/map-epic-2/names.json.

Reads each world's compiled campaign.json (a bundle's compiled/campaign/campaign.json) and counts, per kind (kingdoms, clans = houses,
mercenary companies and outlaw clans together, towns, castles, villages, settlements = the three together, heroes, ruins), the display
names carrying an FWGC++ Age tag or an ordinal numeral (a word holding a digit, or a roman ordinal word II..XX, as
src/WorldBridge/src/name_cleaner.cpp has_numeral) and the repeated names. It also checks that every regent carries a personal name (not
"Regent of ..."), that every record whose name differs from its source keeps the source as sourceName, that no source name is lost
(a record with an Age-tagged source name has sourceName), and that the runtime wanderer reserve names are clean and unused by any hero.

Usage: python tools/summarize_names.py OUT LABEL=CAMPAIGN_JSON [LABEL=CAMPAIGN_JSON ...]
Exit status 1 when any world has a problem.
"""
import collections
import json
import os
import re
import sys

AGE = re.compile(r'\(\s*age\s+\d+\s*\)', re.IGNORECASE)
ROMAN = {'ii', 'iii', 'iv', 'v', 'vi', 'vii', 'viii', 'ix', 'x', 'xi', 'xii', 'xiii', 'xiv', 'xv', 'xvi', 'xvii', 'xviii', 'xix', 'xx'}
KINDS = [('kingdoms', ['kingdoms']), ('clans', ['houses', 'mercenaryCompanies', 'outlawClans']), ('towns', ['towns']), ('castles', ['castles']),
         ('villages', ['villages']), ('settlements', ['towns', 'castles', 'villages']), ('heroes', ['heroes']), ('ruins', ['ruins'])]


def numeral(name):
    # As name_cleaner.cpp has_numeral: a digit in a word, or a roman ordinal word that is not all lower case ("V", "IV"; "v" is a word).
    for w in re.split(r'[\s,()-]+', name):
        if w and (any(ch.isdigit() for ch in w) or (w.lower() in ROMAN and w != w.lower())):
            return True
    return False


def census(path):
    with open(path, encoding='utf-8-sig') as f:
        doc = json.load(f)
    row = {'campaign': path.replace('\\', '/'), 'kinds': {}, 'problems': []}
    for kind, keys in KINDS:
        names = [r['name'] for k in keys for r in doc.get(k, [])]
        age = [n for n in names if AGE.search(n)]
        num = [n for n in names if numeral(n)]
        repeated = sorted(n for n, c in collections.Counter(names).items() if c > 1)
        row['kinds'][kind] = {'names': len(names), 'ageTag': len(age), 'numeral': len(num), 'repeated': len(repeated),
                              'examples': {'ageTag': age[:3], 'numeral': num[:3], 'repeated': repeated[:3]}}
        if age or num or repeated:
            row['problems'].append('%s: %d Age tags, %d numerals, %d repeated' % (kind, len(age), len(num), len(repeated)))
    if 'ruins' not in doc:
        row['problems'].append('campaign.json has no ruins block')
    regents = [h for h in doc['heroes'] if h.get('role') == 'regent']
    bad = [h['name'] for h in regents if h['name'].lower().startswith('regent') or ' of ' in h['name']]
    row['regents'] = {'count': len(regents), 'names': sorted(h['name'] for h in regents), 'notPersonal': bad}
    if bad:
        row['problems'].append('regents without a personal name: %s' % bad[:3])
    # The source name stays: a record renamed from a source name keeps it (sourceName), and none still shows a tagged source.
    kept = lost = 0
    for k in ('kingdoms', 'towns', 'heroes', 'ruins'):
        for r in doc.get(k, []):
            if 'sourceName' in r:
                kept += 1
                if r['sourceName'] == r['name']:
                    row['problems'].append('%s %s: sourceName equals name' % (k, r.get('id', r.get('uid'))))
            elif k != 'heroes' and r.get('nameProvenance', 'source').startswith('source') and r.get('nameProvenance', 'source') != 'source':
                lost += 1
    row['sourceNames'] = {'kept': kept, 'lost': lost}
    if lost:
        row['problems'].append('%d renamed records without sourceName' % lost)
    heroes = {h['name'] for h in doc['heroes']}
    reserve = [r['name'] for r in doc.get('wandererReserve', [])]
    dirty = [n for n in reserve if AGE.search(n) or numeral(n) or n in heroes]
    row['wandererReserve'] = {'names': len(reserve), 'dirtyOrTaken': len(dirty), 'repeated': len(reserve) - len(set(reserve))}
    if dirty or len(reserve) != len(set(reserve)):
        row['problems'].append('wanderer reserve: %d dirty or taken, %d repeated' % (len(dirty), len(reserve) - len(set(reserve))))
    row['samples'] = {k: [r['name'] for r in doc.get(k, [])][:8] for k in ('kingdoms', 'towns', 'castles', 'villages', 'ruins')}
    return row


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 2
    out, worlds = argv[1], collections.OrderedDict()
    for arg in argv[2:]:
        label, path = arg.split('=', 1)
        worlds[label] = census(path)
    doc = {'schema': 'mnbl.name-census', 'version': 1, 'ticket': 'MMR-286',
           'rule': 'per kind: 0 display names with an Age tag or an ordinal numeral, none repeated; regents carry personal names; renamed records keep sourceName',
           'command': 'python tools/summarize_names.py ' + out + ' ' + ' '.join(argv[2:]).replace('\\', '/'),
           'worlds': worlds, 'pass': all(not w['problems'] for w in worlds.values())}
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(doc, f, indent=2, ensure_ascii=False)
        f.write('\n')
    for label, w in worlds.items():
        print(label, 'PASS' if not w['problems'] else 'FAIL', {k: (v['names'], v['ageTag'], v['numeral'], v['repeated']) for k, v in w['kinds'].items()}, w['problems'][:5])
    return 0 if doc['pass'] else 1


if __name__ == '__main__':
    sys.exit(main(sys.argv))
