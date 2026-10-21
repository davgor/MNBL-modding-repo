"""MMR-287 (RT-32): census of the installed wanderer templates' backstories for proper nouns that do not exist in a generated world.

A wanderer's conversation lines are keyed by its template id (prebackstory, backstory_a..d, response_1/2 and generic_backstory, each
"<key>.<template id>" in the loaded modules' ModuleData string files). A generated world has none of Calradia's places, realms, clans or
lords, so a template whose lines name one of them tells a Calradian story under an FWGC++ name. The lexicon is read from the merged
campaign catalog (evidence/runtime/in-game/campaign_catalog-10212.json, hash-checked), the same capture make_population_policy.py reads:
  * settlement names (towns, castles, villages, hideouts and the rest of Settlements.xml);
  * realm names and titles (Kingdoms.xml name and title), except a realm name that is also its people's name (Aserai, Khuzait, Nord):
    culture words stay, because generated kingdoms present through the vanilla cultures;
  * clan names (Factions.xml), except the bandit and pirate clans (is_bandit), which every generated world re-emits with their
    installed ids and names (startup.json banditClans, MMR-35's pirates);
  * the names of every XML hero (Heroes.xml -> NPCCharacters.xml), alive or dead;
  * the world itself (Calradia, Calradian) and its emperor or empress (a person a generated world does not have);
  * LORE: the Calradian places, peoples and companies the lines name that no catalog record carries, found by reviewing every
    capitalized word of the lines this lexicon leaves safe (2026-10-20). Personal names that belong only to a story (a father, a
    rival, a cousin) are not world nouns and stay.
Matching is whole-word and case-sensitive (proper nouns), on the English strings with the {=id} prefix removed. A template is unsafe
when any of its lines matches; the pool HeroFactory and the wanderer tick draw from (population.json wanderers.backstoryUnsafe, written
by make_population_policy.py from this file) is the rest.

Usage: python tools/census_wanderer_backstories.py [--game GAME_ROOT]
Writes evidence/map-epic-2/wanderer-backstories.json.
"""
import argparse
import collections
import hashlib
import json
import os
import re
import xml.etree.ElementTree as ET

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CATALOG = 'evidence/runtime/in-game/campaign_catalog-10212.json'
OUT = 'evidence/map-epic-2/wanderer-backstories.json'
GAME = r'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord'
# The modules a generated (Sandbox) campaign loads whose ModuleData carries strings; StoryMode is not loaded (Sandbox-only scope).
MODULES = ['Native', 'SandBoxCore', 'SandBox', 'NavalDLC']
KEYS = ['prebackstory', 'backstory_a', 'backstory_b', 'backstory_c', 'backstory_d', 'response_1', 'response_2', 'generic_backstory']
WORLD = [('world', 'Calradia'), ('world', 'Calradian'), ('world', 'Calradians'), ('emperor', 'emperor'), ('emperor', 'Emperor'),
         ('emperor', 'empress'), ('emperor', 'Empress'), ('emperor', 'emperors'), ('emperor', 'Emperors')]
CULTURE_WORDS = {'Aserai', 'Khuzait', 'Nord'}   # realm names that are also their people's names
LORE = ['Beinland', 'Beinlandings', 'Brothers of the Woods', 'Company of the Boar', 'Empire', 'Jumne', 'Ladimos', 'Nordlands', 'Nordvyg',
        'Sturgland', 'Svalfjord', 'Throndelag', 'Vaegirs', 'Vanni']


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def plain(text):
    return re.sub(r'^\{=[^}]*\}', '', text or '').strip()


def lexicon(roots):
    nouns = collections.OrderedDict()

    def add(kind, name):
        name = plain(name)
        if name and '{' not in name and name not in nouns:
            nouns[name] = kind
    for s in roots['Settlements']:
        add('settlement', s.get('name'))
    for k in roots['Kingdoms']:
        for attr in ('name', 'title'):
            if plain(k.get(attr)) not in CULTURE_WORDS:
                add('realm', k.get(attr))
    for c in roots['Factions']:
        if c.get('is_bandit') != 'true':
            add('clan', c.get('name'))
    characters = {e.get('id'): e for e in roots['NPCCharacters']}
    for h in roots['Heroes']:
        e = characters.get(h.get('id'))
        if e is not None:
            add('hero', e.get('name'))
    for kind, word in WORLD:
        add(kind, word)
    for word in LORE:
        add('lore', word)
    return nouns


def strings(game):
    found = {}
    for module in MODULES:
        data = os.path.join(game, 'Modules', module, 'ModuleData')
        if not os.path.isdir(data):
            continue
        for name in sorted(os.listdir(data)):
            path = os.path.join(data, name)
            if not name.endswith('.xml') or not os.path.isfile(path):
                continue
            try:
                root = ET.parse(path).getroot()
            except ET.ParseError:
                continue
            if root.tag != 'strings':
                continue
            for s in root.iter('string'):
                found[s.get('id')] = (plain(s.get('text')), module + '/ModuleData/' + name)
    return found


def main():
    args = argparse.ArgumentParser()
    args.add_argument('--game', default=GAME)
    game = args.parse_args().game
    receipt = json.load(open(os.path.join(REPO, CATALOG), encoding='utf-8-sig'))
    docs = {d['id']: d for d in receipt['outputs']['documents']}
    roots = {}
    for name in ('Settlements', 'Kingdoms', 'Factions', 'Heroes', 'NPCCharacters'):
        if sha(docs[name]['path']) != docs[name]['sha256']:
            raise SystemExit('catalog %s differs from its receipt' % name)
        roots[name] = ET.parse(docs[name]['path']).getroot()
    nouns = lexicon(roots)
    # Longest names first, so "Northern Empire" is reported before a shorter overlapping noun.
    patterns = [(n, k, re.compile(r'(?<![\w-])' + re.escape(n) + r'(?![\w-])')) for n, k in sorted(nouns.items(), key=lambda x: (-len(x[0]), x[0]))]
    lines = strings(game)
    templates = [e for e in roots['NPCCharacters'] if e.get('is_template') == 'true' and e.get('occupation') == 'Wanderer']
    rows, unsafe, sources = [], [], set()
    for t in templates:
        tid = t.get('id')
        hits = []
        missing = []
        for key in KEYS:
            entry = lines.get(key + '.' + tid)
            if entry is None:
                missing.append(key)
                continue
            text, source = entry
            sources.add(source)
            for noun, kind, pattern in patterns:
                if pattern.search(text):
                    hits.append(collections.OrderedDict([('line', key), ('noun', noun), ('kind', kind)]))
        name = plain(t.get('name'))
        epithet = name.replace('{FIRSTNAME}', '').strip() if '{FIRSTNAME}' in name else None
        row = collections.OrderedDict([
            ('id', tid), ('culture', (t.get('culture') or '').split('.')[-1]), ('female', t.get('is_female') == 'true'),
            ('epithet', epithet), ('lines', len(KEYS) - len(missing)), ('missingLines', missing), ('unsafe', bool(hits)), ('hits', hits)])
        rows.append(row)
        if hits:
            unsafe.append(tid)
    by_culture = collections.OrderedDict()
    for r in sorted(rows, key=lambda r: r['culture']):
        c = by_culture.setdefault(r['culture'], collections.OrderedDict([('templates', 0), ('safe', 0), ('safeFemale', 0), ('safeMale', 0)]))
        c['templates'] += 1
        if not r['unsafe']:
            c['safe'] += 1
            c['safeFemale' if r['female'] else 'safeMale'] += 1
    out = collections.OrderedDict([
        ('schema', 'mnbl.wanderer-backstory-census'), ('version', 1), ('ticket', 'MMR-287 (RT-32)'),
        ('rule', 'a template is unsafe when one of its lines (' + ', '.join(KEYS) + ', keyed "<line>.<template id>") names, as a whole '
                 'case-sensitive word, a noun of the lexicon: installed settlement, realm (name and title, except the culture words '
                 + ', '.join(sorted(CULTURE_WORDS)) + '), non-bandit clan and XML hero names, Calradia/Calradian, the emperor or empress, and '
                 'the reviewed lore nouns (' + ', '.join(LORE) + '). Story-only personal names stay. The safe pool '
                 'is every other template; population.json wanderers.backstoryUnsafe lists the unsafe ones'),
        ('catalog', CATALOG), ('catalogSha256', sha(os.path.join(REPO, CATALOG))),
        ('strings', sorted(sources)), ('lexicon', collections.OrderedDict(sorted(collections.Counter(nouns.values()).items()))),
        ('templates', len(rows)), ('unsafeCount', len(unsafe)), ('safeCount', len(rows) - len(unsafe)),
        ('withEpithet', sum(1 for r in rows if r['epithet'])), ('byCulture', by_culture),
        ('unsafe', unsafe), ('rows', rows)])
    path = os.path.join(REPO, OUT)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(out, f, indent=1)
        f.write('\n')
    print('wrote', path, 'templates', len(rows), 'unsafe', len(unsafe), 'safe', len(rows) - len(unsafe))
    for r in rows:
        if r['unsafe']:
            print(' ', r['id'], sorted(set(h['noun'] + ' (' + h['kind'] + ')' for h in r['hits'])))


if __name__ == '__main__':
    main()
