"""MMR-57: the clean install's verdict. Reads build/mmr57/clean-install-raw.json (tools/Test-CleanInstall.ps1), the clean_install probe
results it names, the engine logs of its runs, evidence/release/package-manifest.json (tools/Package.ps1) and build/mmr57/paths.json
(tools/Test-PackagePaths.ps1), and writes evidence/release/clean-install.json with every check recomputed:
  c1: the package alone, with a clean per-user store, loads with War Sails, derives its surface template, generates and enters a world,
      and the engine log shows no missing kind the development install's campaign run did not have;
  c2: the package's hashes, dependencies, runtime requirements and provenance are complete and nothing official is redistributed;
  c3: disabling, removing and reinstalling the module keeps the saves, the world data and the game's own files, and the save loads again.
The read-only installation's outcome and every other unresolved observation are reported under findings. User paths are replaced.
Usage: python tools/summarize_clean_install.py [--baseline-pid 43464]  (43464: the development install's own clean_install install run, 2026-10-18)
"""
import json
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(REPO, 'evidence', 'release', 'clean-install.json')
LOGS = os.path.join(os.environ.get('PROGRAMDATA', r'C:\ProgramData'), 'Mount and Blade II Bannerlord', 'logs')
KEPT_LOGS = os.path.join(REPO, 'build', 'mmr57', 'engine-logs')
MISSING = re.compile(r'missing|not found|unable to find|could not find|cannot find|can\'t find|failed to load|couldn\'t load', re.I)
RUNTIME = {'kernel32.dll', 'user32.dll', 'advapi32.dll', 'shell32.dll', 'ole32.dll', 'bcrypt.dll', 'ws2_32.dll', 'msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll'}


def load(rel):
    path = os.path.join(REPO, rel)
    if not os.path.exists(path):
        return None
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f)


def scrub(x):
    home = re.compile(r'[A-Za-z]:[\\/]+Users[\\/]+[^\\/"]+', re.I)
    if isinstance(x, str):
        return home.sub('<home>', x.replace(REPO, '<repo>').replace(REPO.replace('\\', '/'), '<repo>'))
    if isinstance(x, list):
        return [scrub(v) for v in x]
    if isinstance(x, dict):
        return {k: scrub(v) for k, v in x.items()}
    return x


def log_path(pid):
    # The engine keeps a few logs only, so the sequence's and the baseline's are kept in build/mmr57/engine-logs first.
    return next((p for p in (os.path.join(KEPT_LOGS, 'rgl_log_%s.txt' % pid), os.path.join(LOGS, 'rgl_log_%s.txt' % pid)) if os.path.exists(p)), None) if pid else None


def ours_lines(pid):
    """This module's engine log lines, except the start-up scan that reads every module's descriptor in the Modules folder, enabled
    or not ('Loading xml file', 'LoadWithFullPath'); an enabled module adds 'Loading assembly', its GUI resources and its screens."""
    path = log_path(pid)
    if path is None:
        return None
    with open(path, encoding='utf-8', errors='replace') as f:
        return [l.strip() for l in f if 'MNBL.ProceduralWorld' in l and 'Loading xml file' not in l and 'LoadWithFullPath' not in l]


def kinds(pid):
    """The engine log's missing-line kinds (numbers and quoted names folded), or None when the log is absent."""
    if not pid:
        return None
    path = log_path(pid)
    if path is None:
        return None
    out = {}
    with open(path, encoding='utf-8', errors='replace') as f:
        for line in f:
            if not MISSING.search(line):
                continue
            body = re.sub(r'^\[[^\]]*\]\s*', '', line.strip())
            kind = re.sub(r'[\w./\\-]*\d[\w./\\-]*|"[^"]*"', '#', body)[:70]
            out.setdefault(kind, body)
    return out


def covered(deps):
    return all(d.lower() in RUNTIME or d.lower().startswith('api-ms-win-crt') for d in deps)


def main():
    baseline_pid = sys.argv[sys.argv.index('--baseline-pid') + 1] if '--baseline-pid' in sys.argv else '43464'
    raw = load('build/mmr57/clean-install-raw.json') or {}
    steps = raw.get('steps') or {}
    record = load('evidence/release/package-manifest.json') or {}
    manifest = record.get('manifest') or {}
    audit = record.get('localAudit') or {}
    paths = load('build/mmr57/paths.json') or {}
    ansi, uni = (paths.get('cases') or {}).get('ansi') or {}, (paths.get('cases') or {}).get('unicode') or {}
    probes = {}
    for label in ('install', 'reload', 'readonly'):
        step = steps.get('probe ' + label) or {}
        probes[label] = (load(step['result']) or {}) if step.get('result') else {}
    o = {k: (v.get('outputs') or {}) for k, v in probes.items()}
    inst, rel, ro = o['install'], o['reload'], o['readonly']
    mod, tmpl = inst.get('module') or {}, inst.get('surfaceTemplate') or {}
    files = {f['path']: f['sha256'] for f in mod.get('files') or []}
    want = {f['path']: f['sha256'] for f in manifest.get('files') or []}
    gen, start, log = inst.get('generate') or {}, inst.get('start') or {}, inst.get('log') or {}
    expected_scene = ((((load('worldgen/validation/first-test-measurements.json') or {}).get('worlds') or {}).get('A') or {}).get('routes') or {}).get('sceneName')
    before, installed, removed, after = (steps.get('snapshot ' + k) or {} for k in ('before', 'installed', 'removed', 'after'))
    same = lambda a, b, key: bool(a.get(key)) and all((b.get(key) or {}).get(k) == v for k, v in (a.get(key) or {}).items())
    istore, rstore = installed.get('store') or {}, removed.get('store') or {}
    store_kept = bool(istore.get('bundles')) and bool(istore.get('worlds')) and all(istore.get(d) == rstore.get(d) for d in istore)
    disabled, removed_boot = steps.get('boot disabled') or {}, steps.get('boot removed') or {}
    disabled_lines = ours_lines(disabled.get('pid'))
    reload_ = rel.get('load') or {}
    undone = steps.get('undone') or {}
    base, ours_kinds = kinds(baseline_pid), kinds((steps.get('probe install') or {}).get('pid'))
    new_kinds = None if base is None or ours_kinds is None else {k: v for k, v in ours_kinds.items() if k not in base}
    reload_pid = (steps.get('probe reload') or {}).get('pid')
    reload_kinds = kinds(reload_pid)
    new_reload = None if base is None or reload_kinds is None else {k: v for k, v in reload_kinds.items() if k not in base}
    install_step = steps.get('install first') or {}
    archive_sha = (manifest.get('archive') or {}).get('sha256')
    imports = audit.get('nativeImports') or {}
    rows = [
        # c1
        ('c1', 'the package was extracted alone in place of the development module, with the development store moved aside: the installed folder holds exactly the manifest\'s files (%s)' % install_step.get('files'),
         install_step.get('matchesManifest') is True and (steps.get('swapped') or {}).get('cleanStoreBefore') is False),
        ('c1', 'the game loaded the module from the package (%s), with no development receipt, its files the manifest\'s (%d of %d)' % (mod.get('assembly'), sum(1 for p, h in want.items() if files.get(p) == h), len(want)),
         mod.get('developmentReceipt') is False and bool(want) and files == want and (mod.get('assembly') or '').startswith('Modules/MNBL.ProceduralWorld/bin/')),
        ('c1', 'War Sails and Harmony are active beside it (%s)' % [m.get('id') for m in mod.get('activeModules') or []], mod.get('warSails') is True and mod.get('harmony') is True),
        ('c1', 'the surface template was derived in this process from the installed official scene (the package holds %s; derived %s, matching its recipe %s)' % (tmpl.get('packagedFiles'), tmpl.get('derivedFolder'), tmpl.get('derivedMatchesRecipe')),
         tmpl.get('createdThisProcess') is True and tmpl.get('derivedMatchesRecipe') is True and bool(tmpl.get('packagedFiles')) and not any(f in tmpl.get('packagedFiles') for f in ('scene.xscene', 'atmosphere.xml'))),
        ('c1', 'New Campaign generated seed 42 from the package (%s, %s s)' % (gen.get('state'), round(gen.get('seconds') or 0, 1)), gen.get('state') == 'Generated' and gen.get('previewShown') is True),
        ('c1', 'its scene is the one the development install made for the same world (%s), so the derived template is byte-identical in use' % gen.get('sceneName'), expected_scene is not None and gen.get('sceneName') == expected_scene),
        ('c1', 'Start Campaign entered the world (%s; the map after %s s; %s settlements)' % (start.get('state'), round(start.get('mapAfter') or 0, 1), (inst.get('campaign') or {}).get('settlements')),
         start.get('state') == 'Started' and (start.get('mapAfter') or -1) > 0 and ((inst.get('campaign') or {}).get('settlements') or 0) > 0),
        ('c1', 'the generated scene was installed in the package\'s own folder (%s)' % (inst.get('sceneFolders') or {}).get('scenes'), bool((inst.get('sceneFolders') or {}).get('scenes'))),
        ('c1', 'no missing asset of this module or its world in the engine log (%s lines; %s stock)' % (log.get('oursMissingCount'), log.get('stockMissingCount')), log.get('read') is True and log.get('oursMissingCount') == 0),
        ('c1', 'no missing-line kind the development install\'s campaign run %s did not have: install %s, reload %s' % (baseline_pid, None if new_kinds is None else list(new_kinds.values())[:3], None if new_reload is None else list(new_reload.values())[:3]),
         new_kinds == {} and (new_reload == {} or not reload_pid)),
        ('c1', 'the install run completed (status %s)' % (probes['install'] or {}).get('status'), (probes['install'] or {}).get('status') == 'ok'),
        ('c1', 'the packaged executables work under a path with spaces, an ampersand and accented letters of the ANSI code page %s: seed %s published %s (expected %s)' % (paths.get('ansiCodePage'), paths.get('seed'), ansi.get('worldContentId'), paths.get('expectedWorldContentId')),
         ansi.get('passed') is True and ansi.get('nonAscii') is True and ansi.get('hasSpace') is True and ansi.get('hasAmpersand') is True),
        ('c1', 'under a path with letters outside that code page worldbridge refuses cleanly (%s), as designed' % uni.get('refusal'), uni.get('passed') is True and uni.get('refusal') == 'unrepresentable_path'),
        # c2
        ('c2', 'every packaged file has its size, SHA-256 and provenance (%d files)' % len(manifest.get('files') or []),
         bool(manifest.get('files')) and all(f.get('sha256') and f.get('provenance') and f.get('bytes') is not None for f in manifest.get('files') or [])),
        ('c2', 'the dependencies, runtime requirements, supported build and licenses are recorded (%s; %s)' % ([d.get('id') for d in manifest.get('dependencies') or []], manifest.get('runtimeRequirements')),
         bool(manifest.get('dependencies')) and bool(manifest.get('runtimeRequirements')) and bool((manifest.get('supportedBuild') or {}).get('game')) and bool(((manifest.get('supportedBuild') or {}).get('producer') or {}).get('sha256')) and bool(manifest.get('licenses'))),
        ('c2', 'the native executables\' imports are recorded and covered by Windows and the declared runtime (%s)' % imports, bool(imports) and all(covered(d) for d in imports.values())),
        ('c2', 'no packaged file is byte-identical to an installed game file (%s files scanned)' % audit.get('officialFilesScanned'), audit.get('identicalToOfficial') == [] and (audit.get('officialFilesScanned') or 0) > 0),
        ('c2', 'the module embeds exactly the repository\'s worldgen files and nothing else (%d resources)' % len(audit.get('embeddedResources') or []),
         audit.get('embeddedProblems') == [] and bool(audit.get('embeddedResources')) and all(e.get('matchesRepository') for e in audit.get('embeddedResources'))),
        ('c2', 'the surface template ships only this module\'s own files, matching its recipe', 'templateProblems' in audit and audit.get('templateProblems') == []),
        ('c2', 'the archive is the one the clean install and the path test used (%s)' % archive_sha, bool(archive_sha) and archive_sha == raw.get('zipSha256') and archive_sha == paths.get('zipSha256')),
        # c3
        ('c3', 'with the module present but not enabled the game booted to its main menu without loading it (%s s; %s engine log lines of this module beyond the scan of every module\'s descriptor, the assembly loaded %s)'
         % (disabled.get('secondsToMenu'), None if disabled_lines is None else len(disabled_lines), None if disabled_lines is None else any('Loading assembly' in l for l in disabled_lines)),
         disabled.get('mainMenu') is True and disabled_lines == [] and disabled.get('moduleFolderPresent') is True),
        ('c3', 'with the module removed the game booted to its main menu (%s s)' % removed_boot.get('secondsToMenu'), removed_boot.get('mainMenu') is True and removed_boot.get('moduleFolderPresent') is False),
        ('c3', 'the world the package made (its bundle, package, world save, surface template) is byte for byte the same after disabling and removing the module (%s files)' % sum(len(v or {}) for v in istore.values()), store_kept),
        ('c3', 'reinstalled with no generated scene left, the module loaded the save the install wrote (%s; scenes before %s, after %s)' % (reload_.get('state'), (rel.get('sceneFoldersBefore') or {}).get('scenes'), (rel.get('sceneFoldersAfter') or {}).get('scenes')),
         reload_.get('loaded') is True and not (rel.get('sceneFoldersBefore') or {}).get('scenes') and bool((rel.get('sceneFoldersAfter') or {}).get('scenes'))),
        ('c3', 'every save there before the sequence is still there, byte for byte (%d saves)' % len(before.get('saves') or {}), same(before, after, 'saves')),
        ('c3', 'the official modules\' descriptors and the engine binaries are unchanged', same(before, after, 'official') and same(before, after, 'binaries')),
        ('c3', 'the development module and store were put back (%s)' % {k: v for k, v in undone.items() if 'restored' in k}, undone.get('development module restored') is True and undone.get('development store restored') is True),
    ]
    ro_step, ro_probe = steps.get('read-only') or {}, steps.get('probe readonly') or {}
    rgen, rload = ro.get('generate') or {}, ro.get('load') or {}
    access = ro_step.get('access') or {}
    readonly = {'tested': bool(ro_step), 'access': access, 'probe': {k: ro_probe.get(k) for k in ('pid', 'resultPresent', 'status', 'stopped', 'engineLogLastLines')},
                'generate': {k: rgen.get(k) for k in ('state', 'error', 'seconds')}, 'load': {k: rload.get(k) for k in ('loaded', 'state', 'refusal', 'loaderError')}}
    findings = []
    if ro_step:
        valid = (access.get('readDescriptor') is True and access.get('listFolder') is True and access.get('readAssembly') is True
                 and access.get('createFile') is False and access.get('createSceneFolder') is False)
        readonly['valid'] = valid
        if not valid:
            findings.append('The read-only test is not valid: the denial did not leave reads working and creation failing (%s).' % access)
        elif ro_probe.get('stopped'):
            findings.append('With the module folder read-only (reads allowed, creation denied) the game did not reach its menu: %s (engine log ends %s).' % (ro_probe.get('stopped'), (ro_probe.get('engineLogLastLines') or [])[-1:]))
        elif rgen.get('state') != 'Generated' or rload.get('loaded') is not True:
            findings.append('With the module folder read-only (reads allowed, creation denied) the module cannot make or load a world: generating gave %s (%s), loading the save gave %s (%s). Generated scenes must be installed in the module\'s SceneObj folder, the only place the engine reads scenes from, so a read-only installation is not supported.'
                            % (rgen.get('state'), (rgen.get('error') or {}).get('detail'), 'loaded' if rload.get('loaded') else rload.get('state'), rload.get('refusal') or rload.get('loaderError')))
        else:
            findings.append('With the module folder read-only the module generated and loaded worlds.')
    if uni.get('refusal') == 'unrepresentable_path':
        findings.append('A job directory the system ANSI code page (%s) cannot spell is refused (unrepresentable_path): fwg.exe takes narrow arguments. Jobs live in the per-user store under %%LOCALAPPDATA%%, so a Windows user name with letters outside that code page cannot generate a world.' % paths.get('ansiCodePage'))
    for note in manifest.get('contentNotes') or []:
        findings.append('Open: ' + note)
    checks = [{'criterion': c, 'check': n, 'passed': bool(ok)} for c, n, ok in rows]
    out = {'schema': 'mnbl.clean-install', 'version': 2, 'ticket': 'MMR-57', 'raw': 'build/mmr57/clean-install-raw.json', 'baselineRun': baseline_pid,
           'runs': {k: (steps.get('probe ' + k) or {}).get('result') for k in ('install', 'reload', 'readonly')},
           'passed': sum(c['passed'] for c in checks), 'total': len(checks), 'checks': checks, 'readOnly': readonly, 'findings': findings,
           'install': {'module': {k: mod.get(k) for k in ('assembly', 'assemblySha256', 'developmentReceipt', 'warSails', 'harmony', 'game', 'navalDlc', 'gameRootHasSpace', 'activeModules')},
                       'surfaceTemplate': tmpl, 'generate': {k: gen.get(k) for k in ('state', 'seconds', 'sceneName', 'worldContentId', 'waitText')},
                       'start': {k: start.get(k) for k in ('state', 'seconds', 'mapAfter', 'navigationCaches')}, 'campaign': inst.get('campaign'), 'save': inst.get('save'), 'log': log,
                       'missingKindsNotInBaseline': new_kinds},
           'boots': {'disabled': disabled, 'removed': removed_boot}, 'reload': {k: reload_.get(k) for k in ('loaded', 'seconds', 'mapSeconds', 'state')}, 'reloadMissingKindsNotInBaseline': new_reload,
           'paths': {'ansiCodePage': paths.get('ansiCodePage'), 'cases': {k: {kk: c.get(kk) for kk in ('folder', 'hasSpace', 'hasAmpersand', 'nonAscii', 'worldContentId', 'refusal', 'passed', 'steps')} for k, c in (paths.get('cases') or {}).items()}},
           'undone': undone, 'error': steps.get('error'), 'snapshots': {k: steps.get('snapshot ' + k) for k in ('before', 'installed', 'removed', 'after')}}
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(scrub(out), f, indent=1, ensure_ascii=False)
        f.write('\n')
    print('clean install: %d/%d checks' % (out['passed'], out['total']))
    for c in checks:
        if not c['passed']:
            print('   not passed (%s): %s' % (c['criterion'], c['check']))
    for f_ in findings:
        print('   finding: ' + f_)
    return 0 if out['passed'] == out['total'] else 1


if __name__ == '__main__':
    sys.exit(main())
