"""MMR-29: one set of damaged world data bundles through all three validators, which must agree.

The validators are worldbridge --inspect-bundle (C++ validate), the C# host verifier (WorldDataBundleTests.exe --verify,
CampaignBootstrap/WorldDataBundle.cs) and tools/summarize_bundle.py verify_bundle. Starting from a valid baseline bundle
(the small complete bundle bundle_transaction_tests publishes), each variant is a copy that is damaged, linked, renamed
or edited and resealed (a manifest edit with DATA_READY rewritten to name it, so only the deeper rules can catch it).
Every validator must accept the baseline, its trailing-separator spelling and a pretty-printed manifest, and refuse
every other variant, without crashing. Writes the verdicts to --out.
Usage:
  python tools/test_bundle_parity.py --worldbridge EXE --csharp EXE --baseline DIR --work DIR --out FILE
"""
import argparse
import datetime
import hashlib
import json
import os
import shutil
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import summarize_bundle as sb  # noqa: E402


def sha_bytes(b):
    return hashlib.sha256(b).hexdigest()


def canonical(value):
    return json.dumps(value, separators=(',', ':'), sort_keys=True, ensure_ascii=False)


class Variant:
    def __init__(self, base, work, name):
        self.name = name
        self.parent = os.path.join(work, name)
        shutil.rmtree(self.parent, ignore_errors=True)
        os.makedirs(self.parent)
        self.dir = os.path.join(self.parent, os.path.basename(base))
        shutil.copytree(base, self.dir)
        self.path = self.dir

    def file(self, rel):
        return os.path.join(self.dir, *rel.split('/'))

    def manifest_text(self):
        return open(self.file('manifest.json'), 'rb').read().decode('utf-8')

    def reseal(self, text):
        """Write a manifest and a DATA_READY that names it (the worldContentId DATA_READY names is the manifest's, if any)."""
        data = text.encode('utf-8') if isinstance(text, str) else text
        open(self.file('manifest.json'), 'wb').write(data)
        ready = json.loads(open(self.file('DATA_READY'), 'rb').read())
        ready['manifestSha256'] = sha_bytes(data)
        open(self.file('DATA_READY'), 'w', encoding='utf-8', newline='').write(canonical(ready))

    def rebuild(self, manifest):
        """A consistent bundle again after rows changed: totals, a recomputed id, the directory renamed, resealed."""
        rows = sorted(manifest['inventory'], key=lambda r: r['path'].encode())
        manifest['inventory'] = rows
        manifest['totals'] = {'files': len(rows), 'bytes': sum(r['bytes'] for r in rows)}
        text = 'mnbl.world-content-id/1\n' + ''.join('%s\t%d\t%s\n' % (r['path'], r['bytes'], r['sha256']) for r in rows)
        manifest['worldContentId'] = sha_bytes(text.encode('utf-8'))
        data = canonical(manifest).encode('utf-8')
        open(self.file('manifest.json'), 'wb').write(data)
        open(self.file('DATA_READY'), 'w', encoding='utf-8', newline='').write(canonical(
            {'manifestSha256': sha_bytes(data), 'schema': 'mnbl.data-ready', 'version': 1, 'worldContentId': manifest['worldContentId']}))
        target = os.path.join(self.parent, manifest['worldContentId'])
        os.rename(self.dir, target)
        self.dir = self.path = target


def run_cpp(exe, path):
    p = subprocess.run([exe, '--inspect-bundle', path], capture_output=True, text=True, encoding='utf-8', errors='replace')
    if p.returncode not in (0, 2): return 'crash', [('exit %d: ' % p.returncode) + (p.stderr or p.stdout)[:300]]
    try: problems = json.loads(p.stdout)['problems']
    except ValueError: return 'crash', ['unparsable output: ' + p.stdout[:300]]
    return ('valid' if p.returncode == 0 else 'invalid'), problems


def run_cs(exe, path):
    p = subprocess.run([exe, '--verify', path], capture_output=True, text=True, encoding='utf-8', errors='replace')
    if p.returncode not in (0, 2): return 'crash', [('exit %d: ' % p.returncode) + (p.stderr or p.stdout)[:300]]
    try: problems = json.loads(p.stdout)['problems']
    except ValueError: return 'crash', ['unparsable output: ' + p.stdout[:300]]
    return ('valid' if p.returncode == 0 else 'invalid'), problems


def run_py(path):
    try: problems = sb.verify_bundle(path, True, sb.HOST)
    except Exception as e:  # a crash is a finding, not a verdict
        return 'crash', ['%s: %s' % (type(e).__name__, e)]
    return ('valid' if not problems else 'invalid'), problems


def variants(base, work):
    """(name, expected verdict, prepare(variant)) for every case."""
    m0 = json.loads(open(os.path.join(base, 'manifest.json'), 'rb').read())
    first = m0['inventory'][0]
    world = next(r for r in m0['inventory'] if r['path'] == 'source/world.json')

    def edit(fn):
        return lambda v: v.reseal(fn(v.manifest_text()))

    def manifest_edit(fn):
        def go(v):
            m = json.loads(v.manifest_text()); fn(m); v.reseal(canonical(m))
        return go

    def add_file(rel, data):
        def go(v):
            os.makedirs(os.path.dirname(v.file(rel)), exist_ok=True)
            open(v.file(rel), 'wb').write(data)
            m = json.loads(v.manifest_text())
            m['inventory'].append({'path': rel, 'bytes': len(data), 'sha256': sha_bytes(data)})
            v.rebuild(m)
        return go

    def drop_file(rel):
        def go(v):
            os.remove(v.file(rel))
            m = json.loads(v.manifest_text())
            m['inventory'] = [r for r in m['inventory'] if r['path'] != rel]
            v.rebuild(m)
        return go

    def trailing(v): v.path = v.dir + os.sep
    def pretty(v): v.reseal(json.dumps(json.loads(v.manifest_text()), indent=2, sort_keys=True, ensure_ascii=False))
    def missing(v): os.remove(v.file('source/world.json'))
    def extra(v): open(v.file('compiled/extra.bin'), 'wb').write(b'x')
    def edited(v):
        b = bytearray(open(v.file('compiled/terrain/heightfield.bin'), 'rb').read()); b[len(b) // 2] ^= 0xff
        open(v.file('compiled/terrain/heightfield.bin'), 'wb').write(bytes(b))
    def resized(v): open(v.file('compiled/terrain/heightfield.bin'), 'ab').write(b'\0')
    def unsealed(v): os.remove(v.file('DATA_READY'))
    def unresealed(v): open(v.file('manifest.json'), 'ab').write(b' ')
    def empty_ready(v): open(v.file('DATA_READY'), 'wb').close()
    def array_manifest(v): open(v.file('manifest.json'), 'wb').write(b'[]')
    def receipt(v): open(v.file('activation-receipt.json'), 'wb').write(b'{}')
    def unsorted(m): m['inventory'][0], m['inventory'][1] = m['inventory'][1], m['inventory'][0]
    def bom(v): v.reseal(b'\xef\xbb\xbf' + v.manifest_text().encode('utf-8'))
    def junction_top(v): subprocess.run(['cmd', '/c', 'mklink', '/J', v.file('extra'), v.file('policies')], check=True, capture_output=True)
    def junction_inside(v): subprocess.run(['cmd', '/c', 'mklink', '/J', v.file('compiled/linked'), v.file('policies')], check=True, capture_output=True)
    def hard(v): os.link(v.file('policies/playability.json'), os.path.join(v.parent, 'hard.json'))
    def renamed(v):
        target = os.path.join(v.parent, '0' * 64); os.rename(v.dir, target); v.dir = v.path = target
    def case_row(m):
        m['inventory'].append({'path': 'source/World.json', 'bytes': world['bytes'], 'sha256': world['sha256']})
        m['inventory'].sort(key=lambda r: r['path'].encode())
        m['totals'] = {'files': len(m['inventory']), 'bytes': sum(r['bytes'] for r in m['inventory'])}

    return [
        ('baseline', 'valid', lambda v: None),
        ('a trailing separator', 'valid', trailing),
        ('a pretty-printed manifest (resealed)', 'valid', pretty),
        ('a missing file', 'invalid', missing),
        ('an extra file', 'invalid', extra),
        ('an edited file', 'invalid', edited),
        ('a resized file', 'invalid', resized),
        ('an unsealed bundle', 'invalid', unsealed),
        ('an edited manifest not resealed', 'invalid', unresealed),
        ('an empty DATA_READY', 'invalid', empty_ready),
        ('a manifest that is not an object', 'invalid', array_manifest),
        ('an activation receipt inside', 'invalid', receipt),
        ('wrong totals (resealed)', 'invalid', manifest_edit(lambda m: m['totals'].update(files=m['totals']['files'] + 1))),
        ('an unsorted inventory (resealed)', 'invalid', manifest_edit(unsorted)),
        ('no policies (resealed)', 'invalid', manifest_edit(lambda m: m.pop('policies'))),
        ('no validation (resealed)', 'invalid', manifest_edit(lambda m: m.pop('validation'))),
        ('version as a string (resealed)', 'invalid', manifest_edit(lambda m: m.update(version='1'))),
        ('bytes as a string (resealed)', 'invalid', manifest_edit(lambda m: m['inventory'][0].update(bytes=str(first['bytes'])))),
        ('bytes with a fraction (resealed)', 'invalid', edit(lambda t: t.replace('"bytes":%d,"path":"%s"' % (first['bytes'], first['path']), '"bytes":%d.0,"path":"%s"' % (first['bytes'], first['path'])))),
        ('a case-variant extra row (resealed)', 'invalid', manifest_edit(case_row)),
        ('a rejected static validation (resealed)', 'invalid', manifest_edit(lambda m: m['validation'].update(travelStatus='rejected'))),
        ('a report path outside the bundle (resealed)', 'invalid', manifest_edit(lambda m: m['validation'].update(reportPath='C:/Windows/win.ini'))),
        ('a repeated key (resealed)', 'invalid', edit(lambda t: t.replace('"policies":', '"policies":{},"policies":', 1))),
        ('a single-quoted key (resealed)', 'invalid', edit(lambda t: t.replace('"policies":', "'policies':", 1))),
        ('a NaN value (resealed)', 'invalid', edit(lambda t: t[:-1] + ',"x":NaN}')),
        ('a byte-order mark (resealed)', 'invalid', bom),
        ('an inventory path escaping the bundle (resealed)', 'invalid', manifest_edit(lambda m: m['inventory'][0].update(path='compiled/../x'))),
        ('an inventory path longer than 120 bytes (resealed)', 'invalid', manifest_edit(lambda m: m['inventory'][0].update(path='compiled/' + 'x' * 120))),
        ('a DOS device name, consistent bundle', 'invalid', add_file('compiled/con.json', b'{}')),
        ('a required file missing, consistent bundle', 'invalid', drop_file('compiled/travel/cells.bin')),
        ('manifest engine differs from compiled/compatibility.json (resealed)', 'invalid', manifest_edit(lambda m: m['engine'].update(gameVersion='v9.9.9.0'))),
        ('a junction at the top level', 'invalid', junction_top),
        ('a junction inside compiled/', 'invalid', junction_inside),
        ('a hard-linked file', 'invalid', hard),
        ('another directory name', 'invalid', renamed),
    ]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--worldbridge', required=True); ap.add_argument('--csharp', required=True)
    ap.add_argument('--baseline', required=True); ap.add_argument('--work', required=True); ap.add_argument('--out', required=True)
    a = ap.parse_args()
    base = os.path.abspath(a.baseline)
    work = os.path.abspath(a.work)
    os.makedirs(work, exist_ok=True)
    rows, failures = [], 0
    for name, expected, prepare in variants(base, work):
        v = Variant(base, work, 'v%02d' % len(rows))
        prepare(v)
        verdicts = {'worldbridge': run_cpp(a.worldbridge, v.path), 'csharp': run_cs(a.csharp, v.path), 'python': run_py(v.path)}
        ok = all(verdict == expected for verdict, _ in verdicts.values())
        failures += not ok
        rows.append({'variant': name, 'expected': expected, 'agree': ok,
                     'verdicts': {k: {'verdict': verdict, 'firstProblem': problems[0] if problems else None} for k, (verdict, problems) in verdicts.items()}})
        print(('ok    ' if ok else 'FAIL  ') + name + ('' if ok else ' ' + json.dumps({k: [vv[0], vv[1][:2]] for k, vv in verdicts.items()})))
    out = {'schema': 'mnbl.evidence.bundle-parity', 'version': 1, 'ticket': 'MMR-29',
           'generatedUtc': datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'),
           'baseline': {'worldContentId': os.path.basename(base)}, 'validators': ['worldbridge --inspect-bundle', 'WorldDataBundleTests.exe --verify', 'summarize_bundle.verify_bundle'],
           'variants': rows, 'agreed': len(rows) - failures, 'total': len(rows)}
    with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(out, f, indent=2); f.write('\n')
    print('%d/%d variants: all three validators agree with the expected verdict' % (len(rows) - failures, len(rows)))
    return 0 if failures == 0 else 1


if __name__ == '__main__':
    raise SystemExit(main())
