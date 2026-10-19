"""Independently verify an MMR-29 world data bundle and write evidence/fwg/world-bundle.json.

verify_bundle is a third implementation of the bundle validation rules (beside worldbridge validate() and the C# host
verifier; tools/test_bundle_parity.py runs damaged bundles through all three). For the real bundle this script runs it
and also re-checks, in Python:
  the manifest against worldgen/contracts/bundle-manifest.schema.json; DATA_READY names the manifest hash and the
  WorldContentId; the directory is named by the WorldContentId; every inventory file exists with its size and SHA-256,
  and nothing else is in the bundle; the WorldContentId recomputes ('mnbl.world-content-id/1\\n' + sorted
  '<path>\\t<bytes>\\t<sha256>\\n'); totals; no volatile data (absolute paths, timestamps) in the manifest.
Against the stage outputs MMR-26/27/28 accepted, compiled separately: compiled/terrain/* equals the MMR-26 artifact,
compiled/campaign/id-map.json the MMR-27 one, compiled/travel/* and compiled/campaign/campaign.json the MMR-28 travel
output; source/ is the split byte for byte; policies/ are the repository files and the manifest's policy hashes are
theirs; the engine versions are the runtime lock's. A republish reused the bundle; publication diagnostics live outside
it; the activation receipt schema accepts a complete receipt and refuses incomplete ones.
Usage:
  python tools/summarize_bundle.py --bundle DIR --split DIR --terrain DIR --campaign DIR --travel DIR
      --publish FIRST.json AGAIN.json --inspect FILE [--managed RECEIPT.json] --out FILE
"""
import argparse
import datetime
import hashlib
import json
import os
import re
import stat

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
POLICY_FILES = {
    'projection': 'worldgen/policies/projection.json', 'terrain-materials': 'worldgen/policies/terrain-materials.json',
    'terrain-import': 'worldgen/calibration/terrain-import/terrain-import.json', 'campaign-content': 'worldgen/policies/campaign-content.json',
    'culture-presentations': 'worldgen/policies/culture-presentations.json', 'playability': 'worldgen/policies/playability.json',
    'source-compatibility': 'worldgen/contracts/source-compatibility.json',
    'calibration/axis-sea-datum': 'worldgen/calibration/axis-sea-datum/calibration.json',
    'calibration/naval-transitions': 'worldgen/calibration/naval-transitions/naval-transitions.json',
}


def sha(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for block in iter(lambda: f.read(1 << 20), b''): h.update(block)
    return h.hexdigest()


def rel(path):
    path = os.path.abspath(path)
    return os.path.relpath(path, REPO).replace('\\', '/') if path.startswith(REPO) else path


def load(path):
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f)


def tree(root):
    out = {}
    for d, _, files in os.walk(root):
        for name in files:
            p = os.path.join(d, name)
            out[os.path.relpath(p, root).replace('\\', '/')] = (os.path.getsize(p), sha(p))
    return out


# ---- the bundle validation rules (worldbridge validate(), the C# WorldDataBundle; tools/test_bundle_parity.py) ----
ROOTS = ('source', 'compiled', 'policies')
DEVICES = {'con', 'prn', 'aux', 'nul'} | {'com%d' % i for i in range(1, 10)} | {'lpt%d' % i for i in range(1, 10)}
REQUIRED = ['compiled/campaign/campaign.json', 'compiled/campaign/id-map.json', 'compiled/compatibility.json', 'compiled/terrain/height-q16.bin',
            'compiled/terrain/heightfield.bin', 'compiled/terrain/masks.bin', 'compiled/terrain/projection.json', 'compiled/terrain/weights.bin',
            'compiled/travel/cells.bin', 'compiled/travel/ports.json', 'compiled/travel/travel-graph.json', 'compiled/validation.json', 'source/world.json']
HOST = {'gameVersion': 'v1.4.8.119303', 'navalVersion': 'v1.2.8.119303', 'capabilities': {'R%02d' % i for i in range(1, 11)}}


def safe_path(p):
    parts = p.split('/')
    if len(p.encode('utf-8')) > 120 or len(parts) < 2 or parts[0] not in ROOTS: return False
    for part in parts:
        if not part or part in ('.', '..') or part.endswith('.') or part.endswith(' '): return False
        if any(ord(c) < 0x20 or c in '<>:"|?*\\' for c in part): return False
        if part.split('.')[0].lower() in DEVICES: return False
    return True


def strict_json(data):
    """RFC 8259 as worldbridge reads it: UTF-8 without a BOM, no repeated keys, no NaN/Infinity."""
    def pairs(items):
        out = {}
        for k, v in items:
            if k in out: raise ValueError('repeated key %r' % k)
            out[k] = v
        return out
    def constant(name): raise ValueError('not JSON: ' + name)
    return json.loads(data.decode('utf-8'), object_pairs_hook=pairs, parse_constant=constant)


def count(v):
    return isinstance(v, int) and not isinstance(v, bool) and 0 <= v < 2 ** 63


def verify_bundle(directory, name_must_match=True, host=None):
    """Every failed check (empty when valid). host: {gameVersion, navalVersion, capabilities} to check compatibility."""
    problems = []
    try:
        d = os.path.abspath(directory)
        if not os.path.isdir(d): return ['not a bundle directory: ' + directory]
        if os.lstat(d).st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT: return ['the bundle directory is a link']
        mf, rf = os.path.join(d, 'manifest.json'), os.path.join(d, 'DATA_READY')
        if not os.path.isfile(mf): return ['manifest.json is missing']
        mb = open(mf, 'rb').read()
        m = strict_json(mb)
        if not isinstance(m, dict): return ['manifest is not an object']
        msha = hashlib.sha256(mb).hexdigest()
        if m.get('schema') != 'mnbl.world-bundle' or not (count(m.get('version')) and m['version'] == 1): return ['manifest is not mnbl.world-bundle version 1']
        wid = m.get('worldContentId')
        is_sha = lambda s: isinstance(s, str) and re.fullmatch('[0-9a-f]{64}', s) is not None
        if not is_sha(wid): problems.append('manifest worldContentId is not a SHA-256')
        if not os.path.isfile(rf): problems.append('DATA_READY is missing: the bundle was never sealed')
        else:
            r = strict_json(open(rf, 'rb').read())
            if not isinstance(r, dict): raise ValueError('DATA_READY is not an object')
            if r.get('schema') != 'mnbl.data-ready' or not (count(r.get('version')) and r['version'] == 1): problems.append('DATA_READY is not mnbl.data-ready version 1')
            if r.get('manifestSha256') != msha: problems.append('DATA_READY names a different manifest')
            if r.get('worldContentId') != wid: problems.append('DATA_READY names a different worldContentId')
        if name_must_match and os.path.basename(d) != wid: problems.append('the directory name is not the worldContentId')
        inv = m.get('inventory')
        if not isinstance(inv, list): return problems + ['manifest inventory is missing']
        listed, prev, total = {}, None, 0
        for row in inv:
            path = row.get('path') if isinstance(row, dict) else None
            if not isinstance(path, str) or not path or not is_sha(row.get('sha256')) or not count(row.get('bytes')):
                problems.append('an inventory row is malformed'); continue
            if not safe_path(path): problems.append('unsafe inventory path ' + path); continue
            if prev is not None and not prev.encode() < path.encode(): problems.append('inventory is not sorted and unique at ' + path)
            prev = path
            listed[path] = (row['bytes'], row['sha256'])
            total += row['bytes']
        t = m.get('totals')
        if not isinstance(t, dict) or not count(t.get('files')) or not count(t.get('bytes')) or t['files'] != len(inv) or t['bytes'] != total:
            problems.append('manifest totals do not match the inventory')
        disk = set()
        def walk(path, rel):
            for e in sorted(os.scandir(path), key=lambda e: e.name):
                st = os.lstat(e.path)
                name = rel + e.name
                if st.st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                    problems.append(('linked directory ' if st.st_file_attributes & stat.FILE_ATTRIBUTE_DIRECTORY else 'linked file ') + name); continue
                if st.st_file_attributes & stat.FILE_ATTRIBUTE_DIRECTORY: walk(e.path, name + '/'); continue
                if st.st_nlink != 1: problems.append('hard-linked file ' + name)
                disk.add(name)
        walk(d, '')
        for path, (size, digest) in sorted(listed.items()):
            if path not in disk: problems.append('missing file ' + path); continue
            f = os.path.join(d, *path.split('/'))
            if os.path.getsize(f) != size: problems.append('size differs: ' + path)
            elif sha(f) != digest: problems.append('hash differs: ' + path)
        for name in sorted(disk - set(listed) - {'manifest.json', 'DATA_READY'}):
            problems.append('unlisted file ' + name + (' (activation receipts never live in a bundle)' if 'receipt' in name.lower() or 'ACTIVATED' in name else ''))
        for root in ROOTS:
            if not os.path.isdir(os.path.join(d, root)): problems.append('missing root %s/' % root)
        for f in REQUIRED:
            if f not in listed: problems.append('missing required file ' + f)
        if not problems:
            text = 'mnbl.world-content-id/1\n' + ''.join('%s\t%d\t%s\n' % (p, listed[p][0], listed[p][1]) for p in sorted(listed, key=lambda p: p.encode()))
            if hashlib.sha256(text.encode('utf-8')).hexdigest() != wid: problems.append('worldContentId does not match the files')
        pol = m.get('policies')
        if not isinstance(pol, dict): problems.append('manifest policies are missing')
        else:
            for name, digest in pol.items():
                path = 'policies/%s.json' % name
                if not safe_path(path) or path not in listed or listed[path][1] != digest: problems.append('policy %s does not match its manifest hash' % name)
        val = m.get('validation')
        if not isinstance(val, dict) or val.get('travelStatus') != 'ok': problems.append('the static validation did not pass')
        else:
            rp = val.get('reportPath')
            if not isinstance(rp, str) or not safe_path(rp) or rp not in listed or listed[rp][1] != val.get('reportSha256'):
                problems.append('the validation report does not match its manifest hash')
        eng, caps = m.get('engine'), m.get('requiredCapabilities')
        def capability_list(v):
            if not isinstance(v, list): return None
            out = []
            for c in v:
                if not isinstance(c, dict) or not isinstance(c.get('id'), str) or not re.fullmatch('R[0-9]{2}', c['id']) or not isinstance(c.get('name'), str) or not c['name']: return None
                out.append((c['id'], c['name']))
            return out
        if not isinstance(eng, dict) or not isinstance(eng.get('gameVersion'), str) or not eng['gameVersion'] or not isinstance(eng.get('navalVersion'), str) or not eng['navalVersion'] or not isinstance(caps, list):
            problems.append('manifest engine versions or requiredCapabilities are missing')
        elif not problems:
            required = capability_list(caps)
            if required is None: problems.append('a required capability is malformed')
            compat = strict_json(open(os.path.join(d, 'compiled', 'compatibility.json'), 'rb').read())
            versions = compat.get('engine') if isinstance(compat, dict) else None
            ccaps = capability_list(compat.get('requiredCapabilities')) if isinstance(compat, dict) else None
            if (not isinstance(compat, dict) or compat.get('schema') != 'mnbl.bundle-compatibility' or not isinstance(versions, dict) or ccaps is None or required is None
                    or versions.get('gameVersion') != eng['gameVersion'] or versions.get('navalVersion') != eng['navalVersion'] or ccaps != required):
                problems.append('manifest engine or requiredCapabilities differ from compiled/compatibility.json')
            elif host:
                if versions['gameVersion'] != host['gameVersion']: problems.append('built for Bannerlord %s, installed %s' % (versions['gameVersion'], host['gameVersion']))
                if versions['navalVersion'] != host['navalVersion']: problems.append('built for War Sails %s, installed %s' % (versions['navalVersion'], host['navalVersion']))
                for cid, name in ccaps:
                    if cid not in host['capabilities']: problems.append('requires runtime capability %s (%s), which this host does not provide' % (cid, name))
    except (OSError, ValueError, UnicodeDecodeError, KeyError, TypeError, AttributeError) as e:
        problems.append('%s: %s' % (type(e).__name__, e))
    return problems


# ---- a small JSON Schema (2020-12) validator for the keywords the two schemas use ----
def validate(schema, value, root, path='$', errors=None):
    errors = [] if errors is None else errors
    if '$ref' in schema:
        node = root
        for part in schema['$ref'].lstrip('#/').split('/'): node = node[part]
        return validate(node, value, root, path, errors)
    if 'anyOf' in schema:
        if not any(not validate(s, value, root, path, []) for s in schema['anyOf']): errors.append(path + ': anyOf')
        return errors
    kinds = {'object': dict, 'array': list, 'string': str, 'boolean': bool, 'null': type(None),
             'integer': int, 'number': (int, float)}
    t = schema.get('type')
    if t is not None:
        types = t if isinstance(t, list) else [t]
        if not any(isinstance(value, kinds[k]) and not (k in ('integer', 'number') and isinstance(value, bool)) for k in types):
            errors.append(path + ': not ' + '/'.join(types)); return errors
    if 'const' in schema and value != schema['const']: errors.append(path + ': const')
    if 'pattern' in schema and isinstance(value, str) and not re.search(schema['pattern'], value): errors.append(path + ': pattern')
    if 'minimum' in schema and value < schema['minimum']: errors.append(path + ': minimum')
    if isinstance(value, dict):
        for k in schema.get('required', []):
            if k not in value: errors.append(path + ': missing ' + k)
        props = schema.get('properties', {})
        for k, v in value.items():
            if k in props: validate(props[k], v, root, path + '.' + k, errors)
            elif schema.get('additionalProperties') is False: errors.append(path + ': unknown ' + k)
            elif isinstance(schema.get('additionalProperties'), dict): validate(schema['additionalProperties'], v, root, path + '.' + k, errors)
    if isinstance(value, list) and 'items' in schema:
        for i, v in enumerate(value): validate(schema['items'], v, root, '%s[%d]' % (path, i), errors)
    return errors


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--bundle', required=True); ap.add_argument('--split', required=True)
    ap.add_argument('--terrain', required=True); ap.add_argument('--campaign', required=True); ap.add_argument('--travel', required=True)
    ap.add_argument('--publish', nargs=2, required=True); ap.add_argument('--inspect', required=True)
    ap.add_argument('--managed'); ap.add_argument('--out', required=True)
    a = ap.parse_args()
    checks = []

    def check(name, ok, detail=''):
        checks.append({'name': name, 'ok': bool(ok), 'detail': detail})

    b = os.path.abspath(a.bundle)
    manifest_bytes = open(os.path.join(b, 'manifest.json'), 'rb').read()
    m = json.loads(manifest_bytes)
    manifest_sha = hashlib.sha256(manifest_bytes).hexdigest()
    ready = load(os.path.join(b, 'DATA_READY'))
    schema = load(os.path.join(REPO, 'worldgen/contracts/bundle-manifest.schema.json'))
    errors = validate(schema, m, schema)
    check('manifest validates against bundle-manifest.schema.json', not errors, '; '.join(errors[:5]))
    check('DATA_READY names the manifest hash and the WorldContentId',
          ready == {'schema': 'mnbl.data-ready', 'version': 1, 'worldContentId': m['worldContentId'], 'manifestSha256': manifest_sha})
    check('the directory is named by the WorldContentId', os.path.basename(b) == m['worldContentId'])
    vp = verify_bundle(b, True, HOST)
    check('the Python verifier (the rules of worldbridge validate and the C# host) accepts it on a v1.4.8.119303 / v1.2.8.119303 host', not vp, '; '.join(vp[:5]))

    # Inventory against the files.
    disk = tree(b)
    listed = {r['path']: (r['bytes'], r['sha256']) for r in m['inventory']}
    paths = [r['path'] for r in m['inventory']]
    check('inventory is sorted by UTF-8 bytes and unique', paths == sorted(set(paths), key=lambda p: p.encode()))
    extra = sorted(set(disk) - set(listed) - {'manifest.json', 'DATA_READY'})
    wrong = sorted(p for p in listed if disk.get(p) != listed[p])
    check('every inventory file exists with its size and SHA-256, and nothing else is in the bundle', not extra and not wrong,
          'extra %s wrong %s' % (extra[:3], wrong[:3]))
    check('totals match the inventory', m['totals'] == {'files': len(listed), 'bytes': sum(v[0] for v in listed.values())})
    text = 'mnbl.world-content-id/1\n' + ''.join('%s\t%d\t%s\n' % (p, listed[p][0], listed[p][1]) for p in sorted(listed, key=lambda p: p.encode()))
    recomputed = hashlib.sha256(text.encode('utf-8')).hexdigest()
    check('the WorldContentId recomputes from the inventory', recomputed == m['worldContentId'], recomputed)
    def strings(value):
        if isinstance(value, str): yield value
        elif isinstance(value, dict):
            for x in value.values(): yield from strings(x)
        elif isinstance(value, list):
            for x in value: yield from strings(x)
    volatile = sorted({p for s in strings(m) for p in (r'(?<![A-Za-z])[A-Za-z]:[\\/]', r'^\\\\|[\s"]\\\\[A-Za-z]', r'[\\/]Users[\\/]', r'\d{4}-\d{2}-\d{2}T\d{2}:')
                       if re.search(p, s)})
    check('the manifest holds no absolute paths or timestamps', not volatile, ', '.join(volatile))
    check('only source/, compiled/ and policies/ hold content', all(p.split('/')[0] in ('source', 'compiled', 'policies') for p in listed))
    check('every bundle path is at most 120 bytes', max(len(p.encode()) for p in listed) <= 120, str(max(len(p.encode()) for p in listed)))

    # The stage outputs accepted separately.
    pairs = [('compiled/terrain/' + f, os.path.join(a.terrain, f)) for f in ('heightfield.bin', 'masks.bin', 'weights.bin', 'height-q16.bin', 'projection.json')]
    pairs += [('compiled/campaign/id-map.json', os.path.join(a.campaign, 'id-map.json')), ('compiled/campaign/campaign.json', os.path.join(a.travel, 'campaign.json'))]
    pairs += [('compiled/travel/' + f, os.path.join(a.travel, f)) for f in ('travel-graph.json', 'ports.json', 'cells.bin')]
    differ = [p for p, f in pairs if listed.get(p, (0, ''))[1] != sha(f)]
    check('compiled/ equals the MMR-26 terrain, MMR-27 id map and MMR-28 travel outputs compiled separately', not differ, ', '.join(differ))
    split = tree(a.split)
    source = {p[len('source/'):]: v for p, v in listed.items() if p.startswith('source/')}
    check('source/ is the producer split byte for byte', split == source, '%d split files, %d source files' % (len(split), len(source)))
    policy_differ = [n for n, f in POLICY_FILES.items() if m['policies'].get(n) != sha(os.path.join(REPO, f)) or listed.get('policies/%s.json' % n, (0, ''))[1] != sha(os.path.join(REPO, f))]
    check('policies/ are the repository policy files and the manifest names their hashes', not policy_differ and set(m['policies']) == set(POLICY_FILES), ', '.join(policy_differ))
    lock = load(os.path.join(REPO, 'config/runtime-api-lock.json'))
    compat = load(os.path.join(b, 'compiled/compatibility.json'))
    check('compiled/compatibility.json (inside the WorldContentId) names the runtime lock\'s versions and the manifest\'s capabilities',
          compat['engine'] == {'gameVersion': lock['gameVersion'], 'navalVersion': lock['navalVersion']} and compat['requiredCapabilities'] == m['requiredCapabilities'])
    check('engine versions are the runtime lock\'s', m['engine']['gameVersion'] == lock['gameVersion'] and m['engine']['navalVersion'] == lock['navalVersion']
          and m['engine']['runtimeLockSha256'] == sha(os.path.join(REPO, 'config/runtime-api-lock.json')))
    graph = load(os.path.join(b, 'compiled/travel/travel-graph.json'))
    report = load(os.path.join(b, 'compiled/validation.json'))
    check('a bundle is sealed only for an ok travel graph, and the validation report says so',
          graph['status'] == 'ok' and report['travelStatus'] == 'ok' and m['validation']['travelStatus'] == 'ok')
    check('the producer lock hash is the repository lock\'s', m['source']['producer']['lockSha256'] == sha(os.path.join(REPO, 'worldgen/producer.lock.json')))

    # Publication runs.
    first, again = load(a.publish[0]), load(a.publish[1])
    check('the first publish created the bundle and a republish reused it unchanged',
          first['status'] == again['status'] == 'published' and first['existed'] is False and again['existed'] is True
          and first['worldContentId'] == again['worldContentId'] == m['worldContentId'] and first['manifestSha256'] == again['manifestSha256'] == manifest_sha)
    check('publication diagnostics (timings, absolute inputs) are written outside the bundle',
          'wallSeconds' in first and not os.path.commonpath([os.path.abspath(a.publish[0]), b]) == b)
    inspected = load(a.inspect)
    check('worldbridge --inspect-bundle reports it valid with the same identity',
          inspected['valid'] is True and inspected['state'] == 'DATA_READY' and inspected['worldContentId'] == m['worldContentId'] and inspected['manifestSha256'] == manifest_sha)

    # The activation receipt contract.
    rs = load(os.path.join(REPO, 'worldgen/contracts/activation-receipt.schema.json'))
    v = {'status': 'passed', 'evidenceSha256': '0' * 64}
    receipt = {'schema': 'mnbl.activation-receipt', 'version': 1, 'worldContentId': m['worldContentId'], 'manifestSha256': manifest_sha,
               'campaignInstanceId': '3f2b8c1e-9d4a-4b6f-8e2a-1c5d7f9a0b3e', 'runtimeDerivedBundleSha256': '1' * 64,
               'validations': {k: dict(v) for k in ('W02', 'W03', 'W04', 'W05', 'C07')}, 'activatedBy': 'U03'}
    bad = [dict(receipt, validations={k: dict(v) for k in ('W02', 'W03', 'W04', 'W05')}), dict(receipt, activatedBy='compiler'),
           dict(receipt, validations=dict(receipt['validations'], W03={'status': 'failed', 'evidenceSha256': '0' * 64}))]
    check('the activation receipt schema accepts a complete U03 receipt and refuses a missing C07, another writer or a failed W03',
          not validate(rs, receipt, rs) and all(validate(rs, r, rs) for r in bad))

    managed = None
    if a.managed:
        mr = load(a.managed)
        log = open(os.path.join(os.path.dirname(a.managed), 'checks.log'), encoding='utf-8-sig').read()
        passed = re.search(r'(\d+)/(\d+) checks passed', log)
        managed = {'receipt': rel(a.managed), 'checks': passed.group(0) if passed else None, 'worldContentId': mr['bundle']['worldContentId']}
        check('the C# host verifier accepted the bundle and refused every tampering (tools/Test-WorldDataBundle.ps1)',
              passed and passed.group(1) == passed.group(2) and mr['bundle']['worldContentId'] == m['worldContentId'] and mr['bundle']['manifestSha256'] == manifest_sha)

    out = {
        'schema': 'mnbl.evidence.world-bundle', 'version': 1, 'ticket': 'MMR-29',
        'generatedUtc': datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'),
        'bundle': {'directory': rel(b), 'worldContentId': m['worldContentId'], 'manifestSha256': manifest_sha, 'totals': m['totals'],
                   'roots': {r: {'files': sum(1 for p in listed if p.startswith(r + '/')), 'bytes': sum(v[0] for p, v in listed.items() if p.startswith(r + '/'))}
                             for r in ('source', 'compiled', 'policies')},
                   'compiler': m['compiler'], 'engine': {k: m['engine'][k] for k in ('gameVersion', 'navalVersion')},
                   'requiredCapabilities': [c['id'] for c in m['requiredCapabilities']]},
        'publish': {'first': {k: first[k] for k in ('existed', 'compileSeconds', 'wallSeconds')}, 'again': {k: again[k] for k in ('existed', 'compileSeconds', 'wallSeconds')}},
        'managed': managed,
        'checks': checks, 'passed': sum(c['ok'] for c in checks), 'total': len(checks),
    }
    with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(out, f, indent=2, sort_keys=False)
        f.write('\n')
    print('%d/%d checks passed -> %s' % (out['passed'], out['total'], rel(a.out)))
    for c in checks:
        if not c['ok']: print('FAIL', c['name'], c['detail'])
    return 0 if out['passed'] == out['total'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
