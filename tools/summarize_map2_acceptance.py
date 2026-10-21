"""MMR-247: grades one map epic 2 acceptance run (tools/Run-Map2Acceptance.ps1, build/acceptance/<run>) into
evidence/map-epic-2/acceptance.json: every ticket step (1-7, step 2's checks one by one) and every criterion c1-c33 as pass, fail, blocked or
not-run, with what decided it, and the run's screenshots (path, SHA-256, what they show; the images stay git-ignored in the run folder).
A launch the orchestrator stopped as hung is listed under 'hangs' (where it stopped: the progress log's last step without an end line and its
last stage, the engine log's last line); the step it hung in fails, and every rule it left unrun says so (acc4 triage hang-4). A launch that
exited by itself with a step open is listed under 'crashes' with its Windows fault event (MMR-247 X), and that step fails the same way.

Inputs: the run folder's state.json (phases, launches, native hashes, claim, restore), each phase's probe result (in-game/map2_acceptance-<pid>
.json, the map2_acceptance probe's steps), the worlds' bundles kept by the run (bundles/A, bundles/B: compiled/, policies/, source/world.json),
the repository's policies (mission-scenes.json, startup.json, population.json) and docs, and the attestations: {"c12": {"status": "pass", "by":
"...", "note": "..."}, "c15": ..., "c27Accepted": ...} for what only the owner or another ticket can state (the owner's texture review, MMR-262's
review pass, a frame-cost drop the owner accepts). By default the committed evidence/map-epic-2/attestations.json, then <run>/attestations.json
(a key there replaces the committed one); --attest FILE (repeatable) names the files instead. The balance pass (c28) is the committed
evidence/map-epic-2/balance-pass.json (--balance FILE), unless an attestation's "balance" states it. A world start that reused caches is graded
on the build that made them (same cache key): another start of this run, else a sibling run folder (build/acceptance/<other>; --builds-from
FOLDER, repeatable, names them instead).
A rule that cannot be evaluated because its input is missing says so: blocked when a probe step was blocked or its world never ran, not-run
when the phase or step did not run (skipped, or the run has not reached it).

Usage: python tools/summarize_map2_acceptance.py --run build/acceptance/<run> [--out evidence/map-epic-2/acceptance.json] [--attest FILE ...]
       [--balance FILE] [--builds-from FOLDER ...]
"""
import argparse
import hashlib
import json
import math
import os
import re
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from summarize_names import AGE, numeral  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
# MMR-247 F: what the fix batch records once for every run (the owner's and MMR-262's statements; the balance pass from acc6's soak).
ATTESTATIONS = os.path.join(REPO, 'evidence', 'map-epic-2', 'attestations.json')
BALANCE_REVIEW = os.path.join(REPO, 'evidence', 'map-epic-2', 'balance-pass.json')
# The balance pass's items (ticket step 6): the speed terms, the trade-area economy (RT-42), and each culture's home terrain (RT-27, one per
# culture the soak saw).
BALANCE_ITEMS = ('speed.Snow', 'speed.Swamp', 'speed.road', 'economy.tradeArea')
PASS, FAIL, BLOCKED, NOT_RUN = 'pass', 'fail', 'blocked', 'not-run'
LORE = ['Calradia', 'Calradian', 'Calradians', 'Beinland', 'Beinlandings', 'Brothers of the Woods', 'Company of the Boar', 'Empire', 'Jumne',
        'Ladimos', 'Nordlands', 'Nordvyg', 'Sturgland', 'Svalfjord', 'Throndelag', 'Vaegirs', 'Vanni']
CRITERIA = {
    'c1': "MMR-248's shared table governs faces, layers, battle classes and the mutation remap in the build that ran.",
    'c2': "MMR-249's probe decisions (delivery mode, decal technique, compositing, scale, map trees, 513 go/no-go) are the ones the build used.",
    'c3': 'FWGC++ fwg apply from the re-pinned producer computed the live mutation.',
    'c4': 'Both worlds kept every FWGC++ city: the in-game Town count equals sites, with the same uids.',
    'c5': 'Both worlds show exactly 3 villages per town and 2 per castle in game, with ports intact, no village within 4 units of a fortification except recorded islet exceptions, and each culture keeping its horse-ranch and wheat-farm types.',
    'c6': 'Every settlement is reached by a drawn road (routes and feeders), and no drawn road enters water without a deck.',
    'c7': "Face types follow biomes in game, and the speed tooltip shows the Snow, Swamp, Forest, Desert and road terms. A battle beside a non-port settlement uses its biome's scene class.",
    'c8': 'Each biome renders with its layers (screenshots), forests show map trees if the probe said go, and land ice stays white.',
    'c9': 'Winter whitens FWGC++ snow regions, the Snowy gameplay state follows season length, and winds vary by latitude.',
    'c10': 'Road decals, bridges at decks and ruin markers on land render, and the ruins page lists every ruin.',
    'c11': 'Towns, castles and villages show culture-correct scaled vanilla looks that match the scene you enter, with intact siege visuals and varied initial wall levels. Villages show production pieces for their type, and nearest neighbours avoid the same look where the pool allows (RT-15).',
    'c12': 'The magic ground textures render from the published packs, and the owner has reviewed them.',
    'c13': 'Generated magic variants are visible and change no terrain type; live mutations drive terrain type and battle scenes.',
    'c14': 'The fire cast mutates nearby ground live and persists through save and Continue, with no generator process at load and nav caches reused.',
    'c15': 'The review pass findings were fixed or deferred with a reason before the first live run.',
    'c16': 'The run followed CLAIM/RELEASE, recorded the native DLL hashes and left the install as found (synthetic module removed). evidence/map-epic-2/acceptance.json records each step as pass, fail, blocked or not-run.',
    'c17': 'In-game screenshots of mountains and coasts on world 1 show smoothed relief with no blade ridges or sea cliffs, settlements sit on level pads, and the camera stops at the pole lines.',
    'c18': "The day-0 census on both worlds matches the compiled population (kingdoms and starting wars, houses and sizes, fief owners, companies, outlaw clans, wanderers). The engine's random child generator ran without errors, adding about ceil(adults/2) children per house.",
    'c19': 'In game, houses, companies and outlaw clans appear as compiled. Minor-clan heroes are flagged, lord personalities vary, and wanderers keep their FWGC++ names and sex.',
    'c20': 'After 30+ days wanderers stay at W (hired included) and unmet ones rotate, companies keep 4 or more heroes, and an AI kingdom hires a company. House and kingdom changes are recorded, and no house of a dissolved kingdom was removed.',
    'c21': "Generating world 2 left world 1's scene and caches in place: Continue world 1 reused its caches. The preview's wait estimate was within 25% of the measured cache build, and MMR-282's missing-cache warning test passed.",
    'c22': 'With the synthetic content module enabled, setup and the world 1 load each worked or refused cleanly naming the module before any cache build, and nothing hung.',
    'c23': "The build that ran has no exact-build gate (MMR-284's offline tests passed), and the player docs state the update behaviour and the Sandbox-only scope.",
    'c24': 'Rivers and every lake are visible in game on both worlds (screenshots), rivers read as water (not paint), and no impassable water renders as dry land at the checked places.',
    'c25': 'Encyclopedia lists on both worlds show no Age tag or ordinal numeral, regents have personal names, and refusal messages come from localizable strings.',
    'c26': "The four culture incidents' conditions are true at a matching town, the seven terrain incidents' terrain predicates are true at one Desert, Forest and Steppe location, hideout bandits match their ground at the checked hideouts, and the wanderer backstory read names no Calradian place.",
    'c27': 'Uncapped fps and memory on world 1 are recorded against the vanilla War Sails baseline at the same camera. A drop over 20% is a blocker unless the owner accepts it (RT-23).',
    'c28': "The soak records the trade-area economy against vanilla, the cost of the exact 3/2 rule the owner kept (RT-42) and each culture's home terrain (RT-27), and the balance pass's changes (or none) are recorded.",
    'c29': 'In game, magic ground on world 1 reads as veins fanning out from their sources (screenshots of generated zones and of the Nerdun cast spreading), not as round splotches, drawn by a route that renders on the campaign map (MMR-291), and the owner has reviewed the look.',
    'c30': "In game, school-specific magic features stand in generated magic zones and appear around the Nerdun cast, never on water, pads or roads, survive save and Continue, and the frame cost stays within c27's limit.",
    'c31': 'In game, magic water shows over magical sea, lake or river cells (screenshot), continuing the land veins.',
    'c32': 'Declaring war or receiving a castle after a battle, then moving far, does not freeze the game: the hang repro passes with the fresh-map rule off, and the world 1 battles, castle and siege run without it.',
    'c33': "MMR-291's overlay meshes draw veins, rivers and magic water in game within c27's frame limit.",
}
# MMR-247 X: the hang repro's bisect variants that move the dressing entities as before the fix (their crash is the expected control).
REPRO_CONTROLS = ('w1.hangRepro.warControl', 'w1.hangRepro.warNoMagic', 'w1.hangRepro.warNoCopySync', 'w1.hangRepro.castleControl')
# c32: the world 1 battles, castle and siege (the acc4/acc5 freezes came in a war or a fief after a mission on the loaded map).
AFTER_MISSION_CASES = ('w1.battle.snow', 'w1.battle.forest', 'w1.battle.settlement', 'w1.battle.mutated', 'w1.castle', 'w1.siege')
LOOK_REVIEW = 'the look needs the screenshot review (c24 rivers, c29 veins, c31 magic water)'
# The probe's progress log (in-game/map2_acceptance-<pid>.log, '<utc> +<ms>ms <stage>'): a step's start and, since acc5, its end line.
STEP_START = re.compile(r'^map2_acceptance:((?:w1|w2|base|mods)\.[A-Za-z0-9_.]+)$')
STEP_END = re.compile(r'^map2_acceptance:((?:w1|w2|base|mods)\.[A-Za-z0-9_.]+):end (\S+)')
SNOW = ('Snowy', 'Blizzard')


def load(path):
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f)


def sha(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for block in iter(lambda: f.read(1 << 20), b''):
            h.update(block)
    return h.hexdigest()


def progress(path):
    """A probe's progress log: its last stage and when, and the last step started with no end line after it (the step a hang stopped in)."""
    rows = []
    with open(path, encoding='utf-8-sig', errors='replace') as f:
        for line in f:
            parts = line.rstrip('\r\n').split(' ', 2)
            if len(parts) == 3 and parts[1].startswith('+'):
                rows.append((parts[0], parts[2]))
    out = {'lastStage': rows[-1][1] if rows else None, 'lastStageUtc': rows[-1][0] if rows else None, 'hungStep': None, 'hungStepUtc': None, 'ended': {}}
    started = []
    for utc, stage in rows:
        m = STEP_END.match(stage)
        if m:
            out['ended'][m.group(1)] = m.group(2)
            started = [s for s in started if s[0] != m.group(1)]
            continue
        m = STEP_START.match(stage)
        if m:
            started.append((m.group(1), utc))
    if started:
        out['hungStep'], out['hungStepUtc'] = started[-1]
    return out


def last_line(path, tail=1 << 16):
    """The last non-empty line of a (large) text file, or None."""
    if not path or not os.path.exists(path):
        return None
    with open(path, 'rb') as f:
        f.seek(0, 2)
        f.seek(max(0, f.tell() - tail))
        lines = [l.strip() for l in f.read().decode('utf-8', 'replace').splitlines() if l.strip()]
    return lines[-1] if lines else None


def measured_builds(folder):
    """MMR-247 F: every navigation cache build a world start of the run in `folder` measured: a '.start' step whose cacheBuild is generated
    or regenerated with its seconds, by the caches' key (cacheBuild.record.keySha256: module, scene, settlements, game and navigation models,
    so one key is one build of the same caches on this machine's install)."""
    rows = []
    path = os.path.join(folder, 'state.json')
    if not os.path.exists(path):
        return rows
    try:
        state = load(path)
    except ValueError:
        return rows
    for launch in state.get('launches') or []:
        result = launch.get('result') and os.path.join(folder, launch['result'])
        if not result or not os.path.exists(result):
            continue
        try:
            doc = load(result)
        except ValueError:
            continue
        for name, row in ((doc.get('outputs') or {}).get('steps') or {}).items():
            build = row.get('cacheBuild') if isinstance(row, dict) else None
            key = g(build or {}, 'record', 'keySha256')
            if name.endswith('.start') and key and build.get('status') in ('generated', 'regenerated') and build.get('generateSeconds') is not None:
                rows.append({'key': key, 'seconds': build['generateSeconds'], 'run': state.get('runId') or os.path.basename(folder), 'phase': launch.get('phase'),
                             'pid': doc.get('processId'), 'step': name, 'started': launch.get('started')})
    return rows


def hang_captures(folder, pid):
    """MMR-247: the probe's engine-thread hang captures of a launch (HangWatchdog: hang-<pid>-<k>.txt and .dmp, collected as
    in-game/map2_acceptance-<pid>-hang-<k>.*): each text's path, its MNBL section line and whether its stall resumed, and the dump's size."""
    rows = []
    base = os.path.join(folder, 'in-game')
    if not os.path.isdir(base):
        return rows
    prefix = 'map2_acceptance-%s-hang-' % pid
    for name in sorted(os.listdir(base)):
        if not (name.startswith(prefix) and name.endswith('.txt')):
            continue
        with open(os.path.join(base, name), encoding='utf-8', errors='replace') as f:
            text = f.read()
        section = next((l.split(': ', 1)[1] for l in text.splitlines() if l.startswith('MNBL section on the engine thread: ')), None)
        dump = os.path.join(base, name[:-4] + '.dmp')
        rows.append({'path': 'in-game/' + name, 'section': section, 'resumed': 'resumed ' in text,
                     'dumpBytes': os.path.getsize(dump) if os.path.exists(dump) else None})
    return rows


class Verdict:
    def __init__(self, status, *detail):
        self.status, self.detail = status, [d for d in detail if d]

    def record(self, **extra):
        r = {'status': self.status, 'detail': self.detail}
        r.update(extra)
        return r


def combine(parts):
    """Worst of the parts: fail > blocked > not-run > pass; the details of every part that is not pass, else all."""
    order = {FAIL: 0, BLOCKED: 1, NOT_RUN: 2, PASS: 3}
    if not parts:
        return Verdict(NOT_RUN, 'nothing evaluated')
    worst = min(parts, key=lambda p: order[p[1].status])[1].status
    detail = ['%s: %s' % (name, '; '.join(v.detail) or v.status) for name, v in parts if v.status != PASS or worst == PASS]
    return Verdict(worst, *detail)


# ---------------------------------------------------------------- the run

class Run:
    def __init__(self, folder, attest, builds_from=None, balance=None):
        self.folder = folder
        self.builds_from = builds_from   # None: the sibling run folders
        self.balance_path = balance or BALANCE_REVIEW
        self.state = load(os.path.join(folder, 'state.json')) if os.path.exists(os.path.join(folder, 'state.json')) else {}
        self.attest = attest or {}
        self.results = {}   # phase -> list of probe results (oldest first)
        for launch in self.state.get('launches', []) or []:
            if launch.get('result'):
                p = os.path.join(folder, launch['result'])
                if os.path.exists(p):
                    doc = load(p)
                    doc['_file'] = launch['result']
                    doc['_launch'] = launch
                    self.results.setdefault(launch['phase'], []).append(doc)
        self.bundles = {}
        for key in ('A', 'B'):
            kept = os.path.join(folder, 'bundles', key)
            if os.path.isdir(kept):
                self.bundles[key] = kept
        self.captures = {}
        for name in sorted(os.listdir(os.path.join(folder, 'in-game'))) if os.path.isdir(os.path.join(folder, 'in-game')) else []:
            m = re.match(r'map2_acceptance-(\d+)-shot-(.+)\.bmp$', name)
            if m:
                self.captures[(int(m.group(1)), m.group(2))] = os.path.join(folder, 'in-game', name)

    def phase_status(self, phase):
        return ((self.state.get('phases') or {}).get(phase) or {}).get('status')

    def steps(self, phase):
        """The phase's steps merged over its attempts: a later attempt's row replaces an earlier one unless it was skipped. A world's start
        keeps the attempt that measured its cache build (a later start on the same build reuses the caches and measures nothing)."""
        merged = {}
        measured = lambda r: g(r, 'cacheBuild', 'status') in ('generated', 'regenerated') and g(r, 'cacheBuild', 'generateSeconds') is not None
        for doc in self.results.get(phase, []):
            for name, row in ((doc.get('outputs') or {}).get('steps') or {}).items():
                if name.endswith('.start') and name in merged and measured(merged[name]) and not measured(row):
                    continue
                if name not in merged or row.get('status') != 'skipped':
                    row = dict(row)
                    row['_pid'] = doc.get('processId')
                    merged[name] = row
        return merged

    def step(self, phase, name):
        return self.steps(phase).get(name)

    def shots(self):
        rows = []
        for phase, docs in self.results.items():
            for doc in docs:
                pid = doc.get('processId')
                for s in ((doc.get('outputs') or {}).get('shots') or []):
                    label = re.sub(r'[^A-Za-z0-9_-]', '-', s.get('label') or '')
                    kept = self.captures.get((pid, label))
                    row = {k: v for k, v in s.items() if k != 'path'}
                    row.update({'phase': phase, 'pid': pid, 'source': s.get('path'), 'path': os.path.relpath(kept, REPO).replace('\\', '/') if kept else None,
                                'sha256': sha(kept) if kept else None})
                    rows.append(row)
        return rows

    def shot(self, phase, label):
        for s in self.shots():
            if s['phase'] == phase and s['label'] == label and s['path']:
                return s
        return None

    def build_outputs(self, phase):
        docs = self.results.get(phase) or []
        return (docs[-1].get('outputs') or {}).get('build') if docs else None

    def launch_started(self, pid):
        return next((l.get('started') for l in self.state.get('launches') or [] if l.get('pid') == pid), None)

    def carried_build(self, key, before=None):
        """MMR-247 F (acc6 c21): the measured build of the caches a start of this run reused (same key): from another start of this run, else
        from a sibling run folder (build/acceptance/<other>: acc6 reused the caches acc6lab built for the same world just before). Among
        several, the latest that started before the reusing start, else the latest. None when no run measured that key."""
        if not key:
            return None
        own = [b for b in measured_builds(self.folder) if b['key'] == key]
        if own:
            return own[-1]
        found = []
        for other in self.other_runs():
            found += [b for b in measured_builds(other) if b['key'] == key]
        if not found:
            return None
        earlier = [b for b in found if before and b['started'] and b['started'] <= before]
        return max(earlier or found, key=lambda b: b['started'] or '')

    def other_runs(self):
        """The run folders a reused cache build may come from: --builds-from's, else this run's siblings (build/acceptance/<other>)."""
        if self.builds_from is not None:
            return [f for f in self.builds_from if os.path.isdir(f)]
        here = os.path.normcase(os.path.abspath(self.folder))
        parent = os.path.dirname(os.path.abspath(self.folder))
        return [os.path.join(parent, n) for n in sorted(os.listdir(parent)) if os.path.isdir(os.path.join(parent, n)) and os.path.normcase(os.path.join(parent, n)) != here]

    def hangs(self):
        """Every launch the orchestrator stopped as hung (CPU flat, or past its deadline): where it stopped (the launch's progress record, else
        the collected progress log: the last step started without an end line, and the last stage), whether it was killed, and the last line
        of its engine log; the orchestrator's own hungSteps fill a launch without a record. The decal lab's and the hang repro's launches are not the
        run's: they block or crash the engine on purpose (Run-Map2Acceptance.ps1 records those in the decallab and hangrepro phases' hangRepro)."""
        rows = []
        for launch in self.state.get('launches') or []:
            if launch.get('phase') in ('decallab', 'hangrepro'):
                continue
            wait = launch.get('wait') or {}
            stopped = wait.get('stopped')
            if not stopped or not (str(stopped).startswith('CPU flat') or stopped == 'deadline'):
                continue
            pid = launch.get('pid')
            prog = dict(launch.get('progress') or {})
            log = os.path.join(self.folder, 'in-game', 'map2_acceptance-%s.log' % pid)
            if not prog.get('lastStage') and os.path.exists(log):
                prog.update({k: v for k, v in progress(log).items() if k != 'ended'})
            engine = os.path.join(self.folder, launch.get('engineLog') or 'logs/rgl_log_%s.txt' % pid)
            rows.append({'phase': launch.get('phase'), 'pid': pid, 'stopped': stopped, 'killed': g(wait, 'kill', 'exited'), 'waitSeconds': wait.get('seconds'),
                         'hungStep': prog.get('hungStep'), 'hungStepUtc': prog.get('hungStepUtc'), 'lastStage': prog.get('lastStage'), 'lastStageUtc': prog.get('lastStageUtc'),
                         'lastEngineLogLine': last_line(engine), 'captures': hang_captures(self.folder, pid)})
        for h in self.state.get('hungSteps') or []:
            if h.get('phase') in ('decallab', 'hangrepro'):
                continue
            if not any(r['pid'] == h.get('pid') and r['phase'] == h.get('phase') for r in rows):
                rows.append({'phase': h.get('phase'), 'pid': h.get('pid'), 'stopped': h.get('stopped'), 'killed': None, 'waitSeconds': None, 'hungStep': h.get('step'),
                             'hungStepUtc': None, 'lastStage': h.get('lastStage'), 'lastStageUtc': h.get('lastStageUtc'), 'lastEngineLogLine': None})
        return rows

    def crashes(self):
        """MMR-247 X: every launch that exited by itself with a probe step open (Run-Map2Acceptance.ps1's progress.openStep, not stopped): the
        game crashed in that step. Its Windows fault event (module, exception code, offset) when the runner found one, and the engine log's last
        line. The decal lab's and the hang repro's launches are left out, as for hangs."""
        rows = []
        for launch in self.state.get('launches') or []:
            if launch.get('phase') in ('decallab', 'hangrepro') or (launch.get('wait') or {}).get('stopped'):
                continue
            prog = launch.get('progress') or {}
            if not prog.get('openStep'):
                continue
            crash = launch.get('crash') or {}
            pid = launch.get('pid')
            engine = os.path.join(self.folder, launch.get('engineLog') or 'logs/rgl_log_%s.txt' % pid)
            rows.append({'phase': launch.get('phase'), 'pid': pid, 'step': prog.get('openStep'), 'lastStage': prog.get('lastStage'), 'lastStageUtc': prog.get('lastStageUtc'),
                         'crash': crash or None, 'fault': '%s in %s+%s' % (crash.get('exceptionCode'), crash.get('module'), crash.get('faultOffset')) if crash.get('module') else 'no fault event found',
                         'lastEngineLogLine': last_line(engine)})
        return rows


def gate(row, phase_ran=True):
    """None when the step's row can be graded, else the verdict for its absence."""
    if row is None:
        return Verdict(NOT_RUN, 'the step did not run' if phase_ran else 'its phase did not run')
    st = row.get('status')
    if st == 'skipped':
        return Verdict(NOT_RUN, 'skipped (%s)' % row.get('reason'))
    if st == 'blocked':
        return Verdict(BLOCKED, row.get('reason'))
    if st == 'error':
        return Verdict(FAIL, 'error: ' + (row.get('error') or '').split('\n')[0])
    if st == 'timeout':
        return Verdict(FAIL, 'timed out after its budget of %s s (recover: not idle after %s)' % (row.get('budgetSeconds'), g(row, 'recover', 'notIdleAfter') or 'no: idle again'))
    return None


def g(d, *keys, default=None):
    for k in keys:
        if not isinstance(d, dict) or k not in d:
            return default
        d = d[k]
    return d


# ---------------------------------------------------------------- bundle data

class Bundle:
    def __init__(self, folder):
        self.folder = folder
        self.campaign = self._load('compiled/campaign/campaign.json')
        self.idmap = self._load('compiled/campaign/id-map.json')
        self.travel = self._load('compiled/travel/travel-graph.json')
        self.materials = self._load('policies/terrain-materials.json')
        self.projection = self._load('compiled/terrain/projection.json')
        self.world_sites = None
        wp = os.path.join(folder, 'source/world.json')
        if os.path.exists(wp):
            try:
                world = load(wp)
                self.world_sites = sorted(s['uid'] for s in world['settlements']['sites'])
            except (KeyError, ValueError, TypeError):
                self.world_sites = None
        self.masks = None
        mp = os.path.join(folder, 'compiled/terrain/masks.bin')
        if os.path.exists(mp):
            with open(mp, 'rb') as f:
                self.masks = f.read()
        self.cells = None
        cp = os.path.join(folder, 'compiled/travel/cells.bin')
        if os.path.exists(cp):
            with open(cp, 'rb') as f:
                self.cells = f.read()

    def _load(self, rel):
        p = os.path.join(self.folder, rel)
        return load(p) if os.path.exists(p) else None

    def table(self):
        return {b['core']: b for b in (self.materials or {}).get('biomes', [])}

    def biome_at(self, x, y):
        """The natural biome at a physical point (masks.bin plane 0, GeneratedCampaignInitializer.CellBiomes)."""
        if not self.masks or self.masks[:8] != b'MNBLMK01' or not self.projection:
            return None
        side = struct.unpack_from('<I', self.masks, 8)[0]
        width = self.projection['periodicLayout']['physicalWidth']
        spacing = width / (side - 1)
        c = int(max(0, min(side - 1, round(x / spacing))))
        r = int(max(0, min(side - 1, round(y / spacing))))
        names = {b['id']: b['core'] for b in (self.materials or {}).get('biomes', [])}
        return names.get(self.masks[12 + r * side + c])

    def cell(self, x, y):
        """The travel cell holding a physical point (cells.bin MNBLTC02: header, then the class, OpenSea, land type and flag planes): its class
        name, its land type's TerrainType name (None off land) and the point's distance to the cell's nearest edge; None off the grid."""
        grid = (self.travel or {}).get('grid')
        if not self.cells or self.cells[:8] != b'MNBLTC02' or not grid:
            return None
        cols, rows, sp = grid['cols'], grid['rows'], grid['spacing']
        fx, fy = x / sp - grid['columnOffset'], y / sp - grid['rowOffset']
        r, c = int(math.floor(fy)), int(math.floor(fx)) % cols
        if not 0 <= r < rows:
            return None
        n, i = cols * rows, r * cols + c
        k, t = self.cells[16 + i], self.cells[16 + 2 * n + i]
        edge = sp * min(fx - math.floor(fx), math.floor(fx) + 1 - fx, fy - math.floor(fy), math.floor(fy) + 1 - fy)
        return (CLASSES[k] if k < len(CLASSES) else 'class %d' % k), (face_names().get(t) if k == 0 else None), edge

    def land_types(self):
        """The land cells per TerrainType name (cells.bin land type plane), or None without the cells."""
        grid = (self.travel or {}).get('grid')
        if not self.cells or self.cells[:8] != b'MNBLTC02' or not grid:
            return None
        n = grid['cols'] * grid['rows']
        counts = {}
        for i in range(n):
            if self.cells[16 + i] == 0:
                name = face_names().get(self.cells[16 + 2 * n + i])
                counts[name] = counts.get(name, 0) + 1
        return counts

    def land_groups(self, passable):
        """GeneratedSpawnModel.CellMap's land groups: components of the passable cells (4-neighbour, columns wrap), each taking the MMR-28 group
        of the travel settlements on it. Returns (group of a physical point or None, the travel settlements' groups by id)."""
        if getattr(self, '_groups', None) is None:
            grid = (self.travel or {}).get('grid')
            if not self.cells or self.cells[:8] != b'MNBLTC02' or not grid:
                self._groups = (lambda x, y: None, {})
                return self._groups
            cols, rows, sp, c0, r0 = grid['cols'], grid['rows'], grid['spacing'], grid['columnOffset'], grid['rowOffset']
            allowed = {CLASSES.index(k) for k in passable if k in CLASSES}
            n = cols * rows
            ok = [self.cells[16 + i] in allowed for i in range(n)]
            comp = [-1] * n
            nxt = 0
            for start in range(n):
                if not ok[start] or comp[start] >= 0:
                    continue
                comp[start] = nxt
                todo = [start]
                while todo:
                    i = todo.pop(); r, c = divmod(i, cols)
                    for j in ((i - cols) if r > 0 else -1, (i + cols) if r < rows - 1 else -1, r * cols + (c - 1) % cols, r * cols + (c + 1) % cols):
                        if j >= 0 and ok[j] and comp[j] < 0:
                            comp[j] = nxt; todo.append(j)
                nxt += 1

            def index(lx, ly):
                r, c = int(math.floor(ly / sp)), int(math.floor(lx / sp)) % cols
                return r * cols + c if 0 <= r < rows else -1
            group_of, by_id = {}, {}
            for s in (self.travel or {}).get('settlements') or []:
                by_id[s.get('id')] = s.get('group')
                if 'point' in s:
                    i = index(s['point']['x'], s['point']['y'])
                    if i >= 0 and comp[i] >= 0:
                        group_of.setdefault(comp[i], s.get('group'))

            def group(x, y):
                i = index(x - c0 * sp, y - r0 * sp)
                return group_of.get(comp[i]) if i >= 0 and comp[i] >= 0 else None
            self._groups = (group, by_id)
        return self._groups

    def near_sea(self, x, y, cells=1):
        """Whether a physical point's travel cell is within `cells` of a Sea cell (cells.bin MNBLTC02, the travel grid)."""
        if not self.cells or self.cells[:8] != b'MNBLTC02' or not self.travel:
            return None
        grid = self.travel['grid']
        cols, rows, sp = grid['cols'], grid['rows'], grid['spacing']
        c0 = int(math.floor(x / sp - grid['columnOffset'])) % cols
        r0 = int(math.floor(y / sp - grid['rowOffset']))
        for dr in range(-cells, cells + 1):
            for dc in range(-cells, cells + 1):
                r = r0 + dr
                if 0 <= r < rows and self.cells[16 + r * cols + (c0 + dc) % cols] == 2:
                    return True
        return False


CLASSES = ['land', 'mountain', 'sea', 'lake', 'river']
_FACES = {}


def face_names():
    """Installed TerrainType ids to names (the calibration's face groups, as tools/summarize_land_faces.py reads them)."""
    if not _FACES:
        p = os.path.join(REPO, 'worldgen/calibration/naval-transitions/naval-transitions.json')
        _FACES.update({v: k for k, v in load(p)['faceGroups'].items()} if os.path.exists(p) else {})
    return _FACES


def periodic_distance(a, b, width):
    dx = abs(a[0] - b[0])
    if width:
        dx = min(dx, width - dx)
    return math.hypot(dx, a[1] - b[1])


# ---------------------------------------------------------------- images

# Near-white: every channel >= 160 with a spread <= 40 (notrun-1 recalibration on acc4's daylight shots: the map's tonemapping keeps snow below
# 200, so the old >= 200 / 35 rule scored correctly rendered land ice 0.076). Measured on the central half at 128 x 128 (acc4, campaign hour 9):
# land_ice 0.738, mountain-0 (a snowy peak) 0.562, desert 0.060, boreal_forest 0.010, cold_tundra 0.004, grassland/forest/marsh 0.000. The
# gates against these: land ice white >= 0.5 at both extremes (0.738 passes, desert's 0.060 is far below), a cold region whitens by >= 0.15
# from summer to winter (an unwhitened tundra scores about 0), a warm region does not whiten: its winter share stays below its summer share
# plus WARM_WHITENING. One-sided (MMR-247 F, acc6): pale desert sand in clear summer light counts as near-white on its own (acc6's warm shot
# 0.46 in summer, 0.00 in winter under the winter grade and cloud shade, same hour, camera and settled scene), so a warm region that gets
# darker in winter is not snow; only a gain in white is, or a winter share at land ice's level (a warm region white all year).
WHITE_MIN, WHITE_SPREAD = 160, 40
WARM_WHITENING = 0.1
ICE_WHITE = 0.5


def central_pixels(path):
    """The central half of a screenshot resized to 128 x 128, as (r, g, b) tuples; None without Pillow or the file."""
    try:
        from PIL import Image
    except ImportError:
        return None
    if not path or not os.path.exists(os.path.join(REPO, path) if not os.path.isabs(path) else path):
        return None
    img = Image.open(os.path.join(REPO, path) if not os.path.isabs(path) else path).convert('RGB')
    w, h = img.size
    data = img.crop((w // 4, h // 4, 3 * w // 4, 3 * h // 4)).resize((128, 128)).tobytes()
    return [(data[i], data[i + 1], data[i + 2]) for i in range(0, len(data), 3)]


def pixel_stats(px):
    """Mean luminance (0..1) and the share of near-white pixels."""
    white = sum(1 for r, gg, b in px if min(r, gg, b) >= WHITE_MIN and max(r, gg, b) - min(r, gg, b) <= WHITE_SPREAD)
    lum = sum(0.2126 * r + 0.7152 * gg + 0.0722 * b for r, gg, b in px) / (255.0 * len(px))
    return {'white': white / len(px), 'luminance': lum}


def image_stats(path):
    px = central_pixels(path)
    return pixel_stats(px) if px else None


def diff_share(a, b, threshold=8):
    """The share of pixel pairs (same view, two screenshots) whose largest channel difference is at least threshold (of 255)."""
    if not a or not b or len(a) != len(b):
        return None
    return sum(1 for p, q in zip(a, b) if max(abs(p[0] - q[0]), abs(p[1] - q[1]), abs(p[2] - q[2])) >= threshold) / float(len(a))


def snow_value(season, byte):
    """DefaultMapWeatherModel's snow at a weather node (1.4.8 IL, GeneratedClimateSystem's rule): c = Lerp(0.55, -0.1, s), v = SmoothStep(c - 0.65,
    c + 0.65, byte / 255), snow = v^2; Snowy above 0.55."""
    c = 0.55 + (-0.1 - 0.55) * season
    t = max(0.0, min(1.0, (byte / 255.0 - (c - 0.65)) / 1.3))
    v = t * t * (3 - 2 * t)
    return v * v


def number(v):
    try:
        f = float(v)
        return None if math.isnan(f) else f
    except (TypeError, ValueError):
        return None


# ---------------------------------------------------------------- the grades

class Grader:
    def __init__(self, run):
        self.run = run
        self.w1 = run.steps('world1')
        self.w2 = run.steps('world2')
        self.base = run.steps('baseline')
        self.mods = run.steps('mods')
        self.ba = Bundle(run.bundles['A']) if 'A' in run.bundles else None
        self.bb = Bundle(run.bundles['B']) if 'B' in run.bundles else None
        self.scenes = load(os.path.join(REPO, 'worldgen/policies/mission-scenes.json'))
        self.hangs = run.hangs()
        self.hung = {(h['phase'], h['hungStep']): h for h in self.hangs if h.get('hungStep')}
        self.crashes = run.crashes()
        self.crashed = {(c['phase'], c['step']): c for c in self.crashes}
        self.steps_out = []
        self.criteria_out = {}
        self._recount = {}

    def ran(self, phase):
        return bool(self.run.results.get(phase))

    def row(self, phase, name):
        return {'world1': self.w1, 'world2': self.w2, 'baseline': self.base, 'mods': self.mods}[phase].get(name)

    def need(self, phase, name):
        """The step's row and the verdict for its absence. A step the game hung in (no finished row from a later attempt) fails: the
        orchestrator killed the game while it ran."""
        r = self.row(phase, name)
        h = self.hung.get((phase, name))
        unfinished = r is None or r.get('status') in ('skipped', None)   # a row checkpointed while the step ran has no status yet
        if h and unfinished:
            return r, Verdict(FAIL, 'the game hung in this step (%s; pid %s killed %s; last stage %s at %s)' % (h['stopped'], h['pid'], h['killed'], h['lastStage'], h['lastStageUtc']))
        c = self.crashed.get((phase, name))
        if c and unfinished:
            return r, Verdict(FAIL, 'the game crashed in this step (%s; pid %s; last stage %s at %s)' % (c['fault'], c['pid'], c['lastStage'], c['lastStageUtc']))
        return r, gate(r, self.ran(phase))

    def width(self, phase):
        for name in ('w1.identity', 'w1.start', 'w1.load', 'w2.identity', 'w2.start'):
            f = g(self.row(phase, name) or {}, 'world', 'frame')
            if f:
                return f[2]
        return None

    def attested(self, key, what):
        a = self.run.attest.get(key)
        if not a:
            return Verdict(BLOCKED, what + ' is not attested yet (attestations.json "%s")' % key)
        return Verdict(a.get('status', FAIL), '%s by %s: %s' % (what, a.get('by', '?'), a.get('note', '')))

    # -- step 1
    def prepare(self):
        st = self.run.state
        if not st.get('launches') and not (st.get('phases') or {}).get('deploy'):
            return Verdict(NOT_RUN, 'the run has not touched the install yet')
        parts = []
        claim = st.get('claim')
        parts.append(('claim', Verdict(PASS, claim.get('text')) if claim else Verdict(FAIL, 'no CLAIM recorded')))
        launches = st.get('launches') or []
        parts.append(('process check', Verdict(PASS, '%d launches, each after the game-free check' % len(launches)) if launches else Verdict(NOT_RUN, 'no launch')))
        build = (st.get('phases') or {}).get('build') or {}
        deployed = g((st.get('phases') or {}).get('deploy') or {}, 'detail', 'moduleAssembly')
        running = {p: g(self.run.build_outputs(p) or {}, 'moduleAssemblySha256') for p in ('baseline', 'world1', 'world2', 'mods') if self.run.build_outputs(p)}
        if build.get('status') != 'done':
            parts.append(('one build', Verdict(NOT_RUN, 'the build phase did not run')))
        else:
            same = deployed == g(build, 'detail', 'module') and all(v == deployed for v in running.values())
            parts.append(('one build', Verdict(PASS if same else FAIL, 'built %s, deployed %s, running %s' % (g(build, 'detail', 'module'), deployed, running))))
        hashes = st.get('hashes') or []
        natives = {(h.get('TaleWorlds.Native.dll'), h.get('TaleWorlds.MountAndBlade.dll')) for h in hashes}
        parts.append(('native hashes', Verdict(PASS if len(hashes) >= 2 and len(natives) == 1 else (NOT_RUN if not hashes else FAIL),
                                               '%d records, %d distinct pairs' % (len(hashes), len(natives)))))
        base = [self.need('baseline', 'base.frames'), self.need('baseline', 'base.memoryDay')]
        parts.append(('vanilla baseline', next((v for _, v in base if v), Verdict(PASS, 'frames and memory after a day recorded'))))
        return combine(parts)

    # -- step 2
    def generate(self):
        r, v = self.need('world1', 'w1.start')
        if v:
            r2, v2 = self.need('world1', 'w1.load')
            return v if v2 else Verdict(BLOCKED, 'world 1 was loaded from its save (resumed run), not generated in this attempt')
        est = g(r, 'preview', 'estimateSeconds'); measured, carried = self.cache_build(r)
        return Verdict(PASS if r.get('seed') == 42 and est is not None and measured is not None else FAIL,
                       'seed %s generated in %.0f s; preview estimate %s s (%s); cache build %s s (%s%s); start %.0f s' % (
                           r.get('seed'), r.get('generateSeconds') or 0, est, g(r, 'preview', 'countsText'), measured, g(r, 'cacheBuild', 'status'),
                           ': ' + carried if carried else '', r.get('startSeconds') or 0))

    def cache_build(self, r):
        """A world start's measured cache build, and where it came from when the start reused caches (MMR-247 F, acc6 c21: acc6's world 1
        reused the caches acc6lab built for the same world shortly before): the build of the same cache key by another start of this run, else
        of a sibling run (Run.carried_build), the latest one before this start. (None, None) when nothing measured them."""
        measured = g(r, 'cacheBuild', 'generateSeconds')
        if measured is not None or g(r, 'cacheBuild', 'status') != 'reused':
            return measured, None
        b = self.run.carried_build(g(r, 'cacheBuild', 'record', 'keySha256'), self.run.launch_started(r.get('_pid')))
        if not b:
            return None, None
        return b['seconds'], 'built in %.0f s by run %s (%s, pid %s, %s, started %s) for the same caches (key %s...)' % (
            b['seconds'], b['run'], b['phase'], b['pid'], b['step'], b['started'], b['key'][:12])

    def shots_of(self, phase, prefix):
        return [s for s in self.run.shots() if s['phase'] == phase and (s['label'] or '').startswith(prefix)]

    def biomes(self):
        r, v = self.need('world1', 'w1.biomes')
        if v:
            return v, v
        spots = r.get('spots') or {}
        shots = {s['label'][6:]: s for s in self.shots_of('world1', 'biome-') if s['path']}
        missing = sorted(set(spots) - set(shots))
        shots_v = Verdict(PASS if spots and not missing else FAIL, '%d biomes present with screenshots %s; missing %s' % (len(spots), sorted(shots), missing))
        return shots_v, self.faces_follow(r)

    def faces_follow(self, r):
        """c1/c7 'faces follow biomes' (fails-1): the in-game face equals the compiled travel cell's land type (cells.bin, the corner majority of
        the shared table's types) on >= 99% of the land samples, points within 0.05 of a cell edge skipped; the probe's own cell samples (land-cell
        centres), else its biome face samples mapped onto the kept bundle's cells (acc4's probe sampled biome points, a third of them on water or
        mountain cells, where the face is invalid). The offline recount (tools/summarize_land_faces.py: cells.bin = the corner majority of the
        table types) must find no mismatch. The per-biome table agreement is information only (a biome thinner than a cell loses the vote)."""
        table = self.ba.table() if self.ba else {}
        agree = {}
        for biome, samples in (r.get('faceSamples') or {}).items():
            want = (table.get(biome) or {}).get('terrainType')
            agree[biome] = (sum(1 for s in samples if s[2] == want), len(samples), want)
        info = 'per-biome table agreement (information): ' + ', '.join('%s %d/%d %s' % (b, a[0], a[1], a[2]) for b, a in sorted(agree.items()))
        pairs, off, edge = [], 0, 0
        if r.get('cellSamples'):
            source = 'the probe\'s land-cell samples'
            for s in r['cellSamples']:
                if s[2] != 'land' or not s[3]:
                    off += 1; continue
                cell = self.ba.cell(s[0], s[1]) if self.ba else None
                if cell and cell[2] < 0.05:
                    edge += 1; continue
                pairs.append((s[0], s[1], s[4], s[3]))
        elif self.ba and self.ba.cells and r.get('faceSamples'):
            source = 'the biome face samples on the kept bundle\'s cells.bin'
            for samples in r['faceSamples'].values():
                for s in samples:
                    cell = self.ba.cell(s[0], s[1])
                    if not cell or cell[0] != 'land':
                        off += 1; continue
                    if cell[2] < 0.05:
                        edge += 1; continue
                    pairs.append((s[0], s[1], s[2], cell[1]))
        else:
            return Verdict(BLOCKED, 'no land-cell samples and no kept bundle to map the face samples onto', info)
        same = sum(1 for p in pairs if p[2] == p[3])
        wrong = [(round(p[0], 2), round(p[1], 2), p[2], p[3]) for p in pairs if p[2] != p[3]]
        recount = self.recount('A')
        ok = pairs and same >= 0.99 * len(pairs) and recount[0] is not False
        return Verdict(PASS if ok else FAIL, 'in-game face = travel cell land type at %d of %d land samples (%s; gate 99%%; %d off land, %d on a cell edge skipped); mismatches %s' % (
            same, len(pairs), source, off, edge, wrong[:5]), recount[1], info)

    def recount(self, key):
        """tools/summarize_land_faces.py's table recount of the kept bundle: (True/False, detail), or (None, why it is unavailable)."""
        if key in self._recount:
            return self._recount[key]
        b = self.ba if key == 'A' else self.bb
        if not b:
            self._recount[key] = (None, 'table recount unavailable: the bundle was not kept with the run')
            return self._recount[key]
        try:
            import gc
            import warnings
            import summarize_land_faces
            with warnings.catch_warnings():
                warnings.simplefilter('ignore', ResourceWarning)   # the tool reads its planes with open(...).read()
                t = summarize_land_faces.world_row(REPO, b.folder, key)['tableRecount']
                gc.collect()
            self._recount[key] = (t['mismatches'] == 0, 'table recount (cells.bin = the corner majority of the table types): %d mismatches %s' % (t['mismatches'], t['examples'][:3]))
        except (Exception, SystemExit) as e:   # noqa: BLE001 - an input the kept bundle lacks
            self._recount[key] = (None, 'table recount unavailable: %s: %s' % (type(e).__name__, e))
        return self._recount[key]

    def relief(self):
        r, v = self.need('world1', 'w1.relief')
        if v:
            return v
        mountains = self.shots_of('world1', 'mountain-'); coasts = self.shots_of('world1', 'coast-')
        poles = r.get('poles') or {}
        held = all(g(poles, k, 'held') for k in ('north', 'south'))
        over = r.get('padsOver25')
        return combine([('mountain and coast screenshots', Verdict(PASS if mountains and coasts else FAIL, '%d mountain, %d coast (judge: no blade ridges or sea cliffs)' % (len(mountains), len(coasts)))),
                        ('pads', Verdict(PASS if over is not None and over <= 10 else FAIL, '%s pads over 25 deg (gate <= 10)' % over)),
                        ('pole clamp', Verdict(PASS if held else FAIL, 'north %s, south %s' % (g(poles, 'north', 'targetY'), g(poles, 'south', 'targetY'))))])

    def water(self, phase, name):
        r, v = self.need(phase, name)
        if v:
            return v
        vis = r.get('visibility') or {}
        bodies = vis.get('bodies') or {}
        # fails-10: a lake is visible when the ray meets any shown water plane above the terrain: its own, the sea plane when the lake lies
        # under the datum, or another lake's whose box holds the sample. Rows of the acc5 probe name the plane; acc4's (6 values) are recounted
        # from what they hold (the plan record keeps no lake boxes, so another lake's plane cannot be told there).
        missed = bodies.get('missed') or []
        boxes = [l for l in g(vis, 'plan', 'lakes', default=[]) or [] if isinstance(l, dict) and l.get('box')]
        planes, unexplained = {}, []
        for m in missed:
            plane = m[6] if len(m) > 6 else None
            if plane is None:
                hit, terrain, z = m[4], m[5], m[3]
                if hit is not None and hit > terrain and z < 0 and abs(hit) < 2e-3:
                    plane = 'sea'
                else:
                    other = next((l for l in boxes if l.get('index') != m[0] and l['box'][0] <= m[1] <= l['box'][2] and l['box'][1] <= m[2] <= l['box'][3]
                                  and hit is not None and abs(hit - l.get('z', 1e9)) < 2e-3 and hit > terrain), None)
                    plane = 'lake:%s' % other.get('index') if other else 'none'
            planes[plane if not plane.startswith('lake:') else 'lake'] = planes.get(plane if not plane.startswith('lake:') else 'lake', 0) + 1
            if plane == 'none':
                unexplained.append(m[:6])
        lakes, hits = bodies.get('lakes'), bodies.get('lakeRayHits')
        beyond = max(0, (lakes or 0) - (hits or 0) - len(missed)) if lakes is not None and hits is not None else 0
        failures = [f for f in vis.get('failures') or [] if not (f.startswith('bodies:') and 'lake rays' in f)]
        lakes_ok = not unexplained and not beyond
        if not lakes_ok:
            failures.append('bodies: %d lake rays met no shown water plane above the terrain %s%s' % (len(unexplained) + beyond, unexplained[:4], ' (%d beyond the record cap)' % beyond if beyond else ''))
        shots = [s for s in self.shots_of(phase, 'water-')]
        dry = g(vis, 'cells', 'dryCount')
        ok = not failures and shots and (vis.get('passed') or (lakes_ok and not [f for f in vis.get('failures') or [] if not f.startswith('bodies:')]))
        return Verdict(PASS if ok else FAIL, 'water visibility %s (%d failures: %s); lakes %s, ray hits %s, misses explained by %s; %d screenshots; dry cells %s of %s' % (
            vis.get('passed'), len(failures), '; '.join(failures[:3]), lakes, hits, planes or 'none', len(shots), dry, g(vis, 'cells', 'impassable')))

    def decals_on_water(self, phase, kind):
        """Road or magic decals on water (fails-5). The acc5 probe tests every decal centre analytically (ground under the datum: the sea plane
        covers it; inside a shown lake box with the plane above the ground); acc4's ray test also counted bridge and settlement mesh hits, so
        there only its examples with the ground under the datum are confirmed, and unconfirmed counts are blocked."""
        r, v = self.need(phase, 'w1.water' if phase == 'world1' else 'w2.water')
        if v:
            return v
        counts = r.get('decalsOnWater')
        if not isinstance(counts, dict):
            return Verdict(BLOCKED, 'decals on water not measured: %s' % counts)
        n = counts.get(kind, 0)
        examples = [e for e in r.get('decalsOnWaterExamples') or [] if e and e[0] == kind and len(e) >= 5]
        tested = g(r, 'decalsTested', kind)
        if r.get('decalsOnWaterRule'):
            return Verdict(PASS if n == 0 else FAIL, '%s %s decals of %s on water (%s); examples %s' % (n, kind, tested, r['decalsOnWaterRule'], examples[:4]))
        confirmed = [e for e in examples if isinstance(e[3], (int, float)) and e[3] < 0]
        if n == 0:
            return Verdict(PASS, '0 %s decals of %s on water (the acc4 ray test)' % (kind, tested))
        if confirmed:
            return Verdict(FAIL, '%d %s decals of %s on water by the acc4 ray test (it also counts bridge and settlement mesh hits); %d of its examples confirmed with the ground under the datum %s' % (
                n, kind, tested, len(confirmed), [e[1:3] for e in confirmed[:4]]))
        return Verdict(BLOCKED, '%d %s decals of %s on water by the acc4 ray test, none confirmed among its examples (the test also counts non-water hits): re-measure with the analytic test' % (n, kind, tested))

    def speed(self):
        """c7's speed tooltip terms (fails-2, fails-3): the named term per face type, sampled off road cells (the Road sample on a road cell); the
        Plain reference off road, else Steppe (neither has a term). Swamp on a world with no Swamp cell is n/a (owner default 2026-10-20)."""
        r, v = self.need('world1', 'w1.speed')
        if v:
            return v
        off = lambda t: not g(r, t, 'absent') and g(r, t, 'lines') and (g(r, t, 'offRoad') is True or g(r, t, 'roadCell') is False)
        reference = next((t for t in ('Plain', 'Steppe') if off(t)), None) or next((t for t in ('Plain', 'Steppe') if not g(r, t, 'absent') and g(r, t, 'lines')), None)
        if reference is None:
            return Verdict(BLOCKED, 'no Plain or Steppe face to compare the terms with')
        base = {l[0] for l in (g(r, reference, 'lines') or [])}
        cells = r.get('cellTypes') or ((self.ba.land_types() or {}) if self.ba else {})
        terms = {'Snow': ('Terrain', 'Snow'), 'Swamp': ('Terrain', 'Swamp'), 'Forest': ('Forest',), 'Desert': ('Desert',), 'Road': ('Road',), 'Bridge': ('Bridge on a road',)}
        parts = []
        for t in ('Snow', 'Swamp', 'Forest', 'Desert', 'Road', 'Bridge'):
            row = r.get(t) or {}
            if t == 'Bridge' and (not row or row.get('absent')):
                continue
            if row.get('absent'):
                if t == 'Swamp' and cells and not cells.get('Swamp'):
                    parts.append((t, Verdict(PASS, 'n/a (owner default): no Swamp cell on this world; Test-PartySpeed covers the term offline')))
                else:
                    parts.append((t, Verdict(BLOCKED, 'no free %s face on this world%s' % (t, ' (%s Swamp cells)' % cells.get('Swamp') if t == 'Swamp' and cells else ''))))
                continue
            names = [l[0] for l in row.get('lines') or []]
            found = [n for n in terms[t] if n in names]
            notes = []
            if t == 'Road' and row.get('roadCell') is not True:
                found = []; notes.append('the Road sample is not on a road cell')
            if t not in ('Road', 'Bridge') and row.get('roadCell'):
                notes.append('sampled on a road cell')
            extra = [l for l in row.get('lines') or [] if l[0] not in base]
            parts.append((t, Verdict(PASS if found else FAIL, 'term %s %s (face %s); lines beyond %s: %s; final %s (%s %s)%s' % (
                '/'.join(terms[t]), 'shown' if found else 'missing', row.get('face'), reference, extra, row.get('final'), reference, g(r, reference, 'final'),
                '; note: ' + ', '.join(notes) if notes else ''))))
        return combine(parts)

    def battle_class(self, case, biome=None, face=None, live=None):
        """Whether a field battle's opened scene serves the expected class: the class of the biome the resolver read (the battle cell's centre,
        case.fieldAt.biome, notrun-3), else the resolver's own class, else the given biome's or face's. live: the TerrainType a live magic
        mutation retyped the battle cell to (acc5 c13): MMR-260 patches that cell's battle class to faceClass[live] over the resolver's natural
        input, so that class is expected, and the mission's terrain is that type (an acc5 probe compared it with the navmesh face group, which a
        live mutation never changes, a3; that failure is checked against the live type here)."""
        if not case:
            return Verdict(NOT_RUN, 'no case row')
        failures = case.get('failures') or []
        scene = g(case, 'mission', 'scene')
        table = self.ba.table() if self.ba else {}
        by = lambda b: (table.get(b) or {}).get('battleClass') or self.scenes['biomeClass'].get(b)
        at = case.get('fieldAt') or {}
        expected, source = None, None
        if live:
            expected, source = self.scenes['faceClass'].get(live), 'the live retyped type %s' % live
            ids = {v: k for k, v in face_names().items()}
            if g(case, 'mission', 'initializerRecord', 'TerrainType') == ids.get(live):
                failures = [f for f in failures if not f.startswith('Initializer record terrain differs')]
        if not expected and at.get('biome'):
            expected, source = by(at['biome']), "the resolver's input biome %s" % at['biome']
        if not expected and at.get('class'):
            expected, source = at['class'], "the resolver's class"
        if not expected and biome:
            expected, source = by(biome), 'the point biome %s' % biome
        if not expected and face:
            expected, source = self.scenes['faceClass'].get(face), 'the face %s' % face
        classes = next((s['classes'] for s in self.scenes['battleScenes'] if s['id'] == scene), None)
        ok = not failures and scene and expected and classes and expected in classes
        return Verdict(PASS if ok else FAIL, 'scene %s (classes %s), expected class %s (%s; resolver class %s, face %s); case failures: %s' % (
            scene, classes, expected, source, at.get('class'), at.get('face'), '; '.join(failures[:3]) or 'none'))

    def face_battles(self):
        parts = []
        for t in ('snow', 'forest'):
            r, v = self.need('world1', 'w1.battle.' + t)
            parts.append((t + ' battle', v or self.battle_class(r.get('case'), face=t.capitalize())))
        r, v = self.need('world1', 'w1.battle.settlement')
        parts.append(('battle beside a non-port settlement', v or self.battle_class(r.get('case'), biome=r.get('biome'))))
        r, v = self.need('world1', 'w1.conversation')
        parts.append(('map conversation', v or Verdict(PASS if r.get('conversationInProgress') and r.get('mapConversationActive') and r.get('afterIdle') is None else FAIL,
                                                       'open %s/%s, idle after: %s' % (r.get('conversationInProgress'), r.get('mapConversationActive'), r.get('afterIdle') or 'yes'))))
        return combine(parts)

    def seam(self):
        r, v = self.need('world1', 'w1.seam')
        if v:
            return v
        ok = r.get('arrived') and g(r, 'chosen', 'west') != g(r, 'chosen', 'east') and self.run.shot('world1', 'seam-west') and self.run.shot('world1', 'seam-east')
        return Verdict(PASS if ok else FAIL, 'from %s to %s, arrived %s in %.1f s, canonical jump %s, faces %s -> %s' % (
            g(r, 'chosen', 'west'), g(r, 'chosen', 'east'), r.get('arrived'), r.get('seconds') or 0, r.get('canonicalJump'), r.get('westFace'), r.get('eastFace')))

    def roads(self):
        r, v = self.need('world1', 'w1.dressing')
        if v:
            return v, v
        drawn, planned = r.get('drawn') or {}, r.get('planned') or {}
        exempt = set()
        net = g(self.ba.travel or {}, 'roadNetwork') if self.ba else None
        for k in ('unconnected', 'islets'):
            for x in (net or {}).get(k) or []:
                exempt.add(x if isinstance(x, str) else x.get('id') or x.get('settlement'))
        far = [n for n in r.get('nearestRoad') or [] if (n[2] is None or (isinstance(n[2], (int, float)) and n[2] > 4.0) or isinstance(n[2], str)) and n[0] not in exempt]
        reach = combine([('settlements reached', Verdict(PASS if not far else FAIL, '%d settlements farther than 4 units from a drawn road (exempt %d): %s' % (len(far), len(exempt), far[:5]))),
                         ('world 1 road decals on water', self.decals_on_water('world1', 'road')), ('world 2 road decals on water', self.decals_on_water('world2', 'road'))])
        render = (drawn.get('roads') == planned.get('roads') and drawn.get('bridges') == planned.get('bridges') and drawn.get('ruins') == planned.get('ruins') and not r.get('errors'))
        shots = [l for l in ('road-at-town', 'bridge', 'ruin-marker') if self.run.shot('world1', l)]
        page = self.w1.get('w1.ruinsPage')
        pv = gate(page, self.ran('world1'))
        page_ok = pv is None and page.get('pageFound') and page.get('rows') == page.get('planRuins') and self.run.shot('world1', 'ruins-page')
        drawn_v = combine([('drawn = planned', Verdict(PASS if render else FAIL, 'drawn %s planned %s errors %s' % (drawn, planned, r.get('errors')))),
                           ('screenshots', Verdict(PASS if len(shots) >= 2 else FAIL, 'shots %s (no bridge shot when the world has none)' % shots)),
                           ('ruins page', pv or Verdict(PASS if page_ok else FAIL, 'rows %s of %s ruins, top screen %s' % (page.get('rows'), page.get('planRuins'), page.get('topScreen'))))])
        return reach, drawn_v

    def looks(self):
        parts = []
        for name in ('w1.town', 'w1.castle', 'w1.village', 'w1.siege'):
            r, v = self.need('world1', name)
            if not v:
                f = r.get('caseFailures') or []
                v = Verdict(PASS if not f and r.get('case') else FAIL, 'case failures: %s' % ('; '.join(f[:3]) or 'none'))
            parts.append((name[3:], v))
        siege_row, siege_v = self.need('world1', 'w1.siege')
        siege = self.run.shot('world1', 'siege-map')
        parts.append(('siege state on the map', siege_v or Verdict(PASS if siege else FAIL, 'screenshot %s' % (siege['path'] if siege else None))))
        r, v = self.need('world1', 'w1.looks')
        if v:
            parts.append(('looks', v))
        else:
            levels = r.get('wallLevels') or {}
            shots = self.shots_of('world1', 'look-')
            varied = len({k.split(':')[1] for k in levels if k.startswith('castle:')}) > 1 or len({k.split(':')[1] for k in levels if k.startswith('town:')}) > 1
            parts.append(('culture looks', Verdict(PASS if shots else FAIL, '%d look screenshots (judge: culture-correct, production pieces)' % len(shots))))
            parts.append(('varied wall levels', Verdict(PASS if varied else FAIL, 'wall levels %s' % levels)))
        return combine(parts)

    def villages(self, phase, key):
        r, v = self.need(phase, 'w1.census0' if phase == 'world1' else 'w2.census0')
        if v:
            return v
        b = self.ba if key == 'A' else self.bb
        c = r.get('census') or {}
        towns, castles, villages = c.get('towns') or [], c.get('castles') or [], c.get('villages') or []
        if not isinstance(towns, list) or not isinstance(villages, list):
            return Verdict(FAIL, 'the census has no settlement lists')
        bad3 = [t['id'] for t in towns if len(t.get('villages') or []) != 3]
        bad2 = [t['id'] for t in castles if len(t.get('villages') or []) != 2]
        width = self.width(phase)
        exceptions = set()
        if b and b.campaign:
            for e in g(b.campaign, 'villageQuota', 'exceptions') or []:
                exceptions.add(e if isinstance(e, str) else e.get('village') or e.get('id'))
        forts = [(f['id'], f['position']) for f in towns + castles]
        crowd = []
        for vil in villages:
            near = min((periodic_distance(vil['position'], p, width) for _, p in forts), default=99)
            if near < 4 and vil['id'] not in exceptions:
                crowd.append((vil['id'], round(near, 2)))
        ports_live = sorted(t['id'] for t in towns if t.get('port'))
        ports_compiled = sorted(t['id'] for t in (b.campaign or {}).get('towns', []) if t.get('port')) if b and b.campaign else None
        cultures = {}
        for vil in villages:
            cultures.setdefault(vil.get('culture'), set()).add(vil.get('type') or '')
        town_cultures = {t.get('culture') for t in towns}
        # fails-6: each culture keeps the protected types (wheat farm, its horse ranch) its installed villages have: the village type pools of
        # economy.json, counted from the installed settlements.xml (provenance settlementsSha256); War Sails' Nord villages have no ranch.
        pools = g(load(os.path.join(REPO, 'worldgen/policies/economy.json')), 'villageTypes', 'pools', default={}) or {}
        lacking = []
        for k in sorted(x for x in town_cultures if x in cultures):
            installed = {t for cls in (pools.get(k) or {}).values() for t in cls} if k in pools else {'wheat_farm', 'horse_ranch'}
            wants = (['wheat_farm'] if 'wheat_farm' in installed else []) + (['horse_ranch'] if any('horse_ranch' in t for t in installed) else [])
            missing = [w for w in wants if not any(w in t for t in cultures[k])]
            if missing:
                lacking.append((k, missing))
        ok = not bad3 and not bad2 and not crowd and (ports_compiled is None or ports_live == ports_compiled) and not lacking
        return Verdict(PASS if ok else FAIL, '%d towns not 3 villages %s, %d castles not 2 %s; %d villages within 4 units (not exceptions) %s; ports live %d compiled %s; cultures lacking an installed protected type %s' % (
            len(bad3), bad3[:3], len(bad2), bad2[:3], len(crowd), crowd[:3], len(ports_live), None if ports_compiled is None else len(ports_compiled), lacking))

    def towns_sites(self, phase, key):
        r, v = self.need(phase, 'w1.census0' if phase == 'world1' else 'w2.census0')
        if v:
            return v
        b = self.ba if key == 'A' else self.bb
        if not b or b.world_sites is None or not b.idmap:
            return Verdict(BLOCKED, 'the bundle (world.json, id-map.json) was not kept with the run')
        towns = {t['id'] for t in (g(r, 'census', 'towns') or [])}
        id_of = {e['uid']: e['id'] for e in b.idmap.get('entries', []) if e.get('kind') == 'town'}
        site_ids = {id_of.get(u) for u in b.world_sites}
        ok = len(towns) == len(b.world_sites) and site_ids == towns
        return Verdict(PASS if ok else FAIL, 'in-game towns %d, FWGC++ sites %d, same uid set %s' % (len(towns), len(b.world_sites), site_ids == towns))

    def names(self, phase):
        r, v = self.need(phase, 'w1.census0' if phase == 'world1' else 'w2.census0')
        if v:
            return v
        c = r.get('census') or {}
        names = []
        for kind in ('kingdoms', 'clans', 'towns', 'castles', 'villages', 'heroes'):
            rows = c.get(kind)
            if isinstance(rows, list):
                names += [(kind, x.get('name')) for x in rows if x.get('name')]
        age = [n for n in names if AGE.search(n[1])]
        num = [n for n in names if numeral(n[1])]
        key = 'A' if phase == 'world1' else 'B'
        b = self.ba if key == 'A' else self.bb
        regents = []
        if b and b.campaign:
            regent_ids = {h['id'] for h in b.campaign.get('heroes', []) if h.get('role') == 'regent' or h.get('bucket') == 'regent' or 'regent' in (h.get('roles') or [])}
            live = {h['id']: h.get('name') for h in (c.get('heroes') or []) if isinstance(c.get('heroes'), list)}
            regents = [(i, live.get(i)) for i in sorted(regent_ids) if live.get(i) and live.get(i).lower().startswith('regent')]
        return Verdict(PASS if names and not age and not num and not regents else FAIL, '%d names: %d Age tags %s, %d numerals %s; regents without a personal name %s' % (
            len(names), len(age), age[:3], len(num), num[:3], regents[:3]))

    def census_match(self, phase, key):
        r, v = self.need(phase, 'w1.census0' if phase == 'world1' else 'w2.census0')
        if v:
            return v, v
        b = self.ba if key == 'A' else self.bb
        if not b or not b.campaign:
            return Verdict(BLOCKED, 'the bundle was not kept with the run'), Verdict(BLOCKED, 'the bundle was not kept with the run')
        camp, c = b.campaign, r.get('census') or {}
        clans = {x['id']: x for x in c.get('clans') or []} if isinstance(c.get('clans'), list) else {}
        kingdoms = {x['id']: x for x in c.get('kingdoms') or []} if isinstance(c.get('kingdoms'), list) else {}
        fiefs = {x['id']: x for x in (c.get('towns') or []) + (c.get('castles') or [])} if isinstance(c.get('towns'), list) else {}
        problems = []
        if len(kingdoms) != len(camp.get('kingdoms', [])):
            problems.append('kingdoms %d live, %d compiled' % (len(kingdoms), len(camp.get('kingdoms', []))))
        for w in camp.get('wars', []):
            if w['b'] not in (kingdoms.get(w['a']) or {}).get('warsWith', []):
                problems.append('war %s-%s not live' % (w['a'], w['b']))
        for h in camp.get('houses', []):
            live = clans.get(h['id'])
            if not live:
                problems.append('house %s missing' % h['id']); continue
            if live.get('kingdom') != h.get('kingdom'):
                problems.append('house %s in %s, compiled %s' % (h['id'], live.get('kingdom'), h.get('kingdom')))
            for f in h.get('fiefs', []):
                if (fiefs.get(f) or {}).get('owner') != h['id']:
                    problems.append('fief %s owned by %s, compiled %s' % (f, (fiefs.get(f) or {}).get('owner'), h['id']))
        for kind in ('mercenaryCompanies', 'outlawClans'):
            for m in camp.get(kind, []):
                if m['id'] not in clans:
                    problems.append('%s %s missing' % (kind, m['id']))
        pop = c.get('population') or {}
        W = g(camp, 'census', 'wanderers')
        if pop.get('wanderers') != W and pop.get('wanderersTarget') != W:
            problems.append('wanderers %s (target %s), compiled W %s' % (pop.get('wanderers'), pop.get('wanderersTarget'), W))
        match = Verdict(PASS if not problems else FAIL, '; '.join(problems[:8]) or 'kingdoms, wars, houses, owners, companies, outlaw clans and W as compiled')
        return match, self.engine_children(camp, clans, pop)

    def engine_children(self, camp, clans, pop):
        """c18's child generator (fails-12): the children the engine added (the population census's engineChildren, which leaves the planned
        minors out) against the compiled estimate within max(3, 10%), and the engine rule per house from the live census: 1.4.8's
        InitialChildGenerationCampaignBehavior adds Ceiling(adult lords / 2) less the existing children to every non-minor, non-bandit clan, so a
        house never has more than that and the world about that many (a child whose parent it cannot draw in 10 tries is skipped). A census
        without the population's engineChildren (older probes) compares the houses' children with the estimate."""
        expected = g(camp, 'census', 'engineChildren')
        problems = pop.get('problems') or []
        by_house = pop.get('engineChildrenByHouse')
        if pop.get('engineChildren') is None or not isinstance(by_house, dict):
            live = sum(x.get('children', 0) for x in clans.values() if x['id'] in {h['id'] for h in camp.get('houses', [])})
            ok = expected is not None and abs(live - expected) <= max(3, 0.25 * expected) and not problems
            return Verdict(PASS if ok else FAIL, 'children in houses %d (the census has no engine-added count), compiled engineChildren %s; population problems %s' % (live, expected, problems[:3]))
        live = pop['engineChildren']
        rule, over = 0, []
        for h, n in sorted(by_house.items()):
            clan = clans.get(h)
            if not clan:
                continue
            allowed = max(0, int(math.ceil(clan.get('adults', 0) / 2.0)) - (clan.get('children', 0) - n))
            rule += allowed
            if n > allowed:
                over.append((h, n, allowed))
        near = expected is not None and abs(live - expected) <= max(3, 0.1 * expected)
        ok = near and not over and live >= 0.9 * rule and not problems
        return Verdict(PASS if ok else FAIL, 'engine-added children %d, compiled engineChildren %s, the engine rule ceil(adults / 2) less the existing children %d over %d houses; houses over the rule %s; population problems %s' % (
            live, expected, rule, len(by_house), over[:3], problems[:3]))

    def compiled_flags(self, phase, key):
        r, v = self.need(phase, 'w1.census0' if phase == 'world1' else 'w2.census0')
        if v:
            return v
        b = self.ba if key == 'A' else self.bb
        if not b or not b.campaign:
            return Verdict(BLOCKED, 'the bundle was not kept with the run')
        camp, c = b.campaign, r.get('census') or {}
        heroes = {h['id']: h for h in c.get('heroes') or []} if isinstance(c.get('heroes'), list) else {}
        problems = []
        for kind in ('mercenaryCompanies', 'outlawClans'):
            for m in camp.get(kind, []):
                unflagged = [x for x in m.get('members', []) if x in heroes and heroes[x].get('alive') and not heroes[x].get('minorFactionHero')]
                if unflagged:
                    problems.append('%s %s: %d members not IsMinorFactionHero' % (kind, m['id'], len(unflagged)))
        traits = c.get('traits') or {}
        if not traits or traits.get('distinctVectors', 0) < 5 or any(len(traits.get(t) or {}) < 2 for t in ('Honor', 'Valor', 'Mercy', 'Generosity', 'Calculating')):
            problems.append('lord traits barely vary: %s' % {k: v for k, v in traits.items() if k != 'lords'})
        live_w = {w['id']: w for w in c.get('wanderers') or []} if isinstance(c.get('wanderers'), list) else {}
        compiled_heroes = {h['id']: h for h in camp.get('heroes', [])}
        for w in camp.get('wanderers', []):
            wid = w.get('id') or w.get('hero')
            lw = live_w.get(wid)
            if lw is None:
                continue
            name = w.get('name') or (compiled_heroes.get(wid) or {}).get('name') or ''
            if bool(w.get('female')) != bool(lw.get('female')) or (name and not (lw.get('name') or '').startswith(name.split(' ')[0])):
                problems.append('wanderer %s live %s/%s, compiled %s/%s' % (wid, lw.get('name'), lw.get('female'), name, w.get('female')))
        return Verdict(PASS if not problems else FAIL, '; '.join(problems[:6]) or 'minor-clan heroes flagged, traits vary, wanderers keep names and sex')

    def parity(self):
        r, v = self.need('world1', 'w1.incidents')
        parts = []
        if v:
            parts.append(('incidents', v))
        else:
            for iid, c in (r.get('culture') or {}).items():
                parts.append((iid, self.culture_incident(c)))
            terrain = r.get('terrain') or {}
            conds = terrain.get('conditions') or []
            for t in ('Desert', 'Forest', 'Steppe'):
                at = terrain.get(t) or {}
                if at.get('absent'):
                    parts.append(('terrain ' + t, Verdict(BLOCKED, 'no free %s face' % t))); continue
                expected = [p for p in at.get('predicates') or [] if p[2]]
                ok = expected and all(p[1] for p in expected)
                parts.append(('terrain ' + t, Verdict(PASS if ok else FAIL, '%d predicates read %s; true there %s' % (len(expected), t, [p[0] for p in expected if p[1]]))))
            parts.append(('terrain conditions found', Verdict(PASS if len(conds) >= 6 else FAIL, '%d conditions read the face type (the ticket says seven, MMR-254 found six in 1.4.8)' % len(conds))))
        c0, v0 = self.need('world1', 'w1.census0')
        if v0:
            parts.append(('hideouts and backstories', v0))
        else:
            parts.append(('hideouts', self.hideouts(c0)))
            parts.append(('wanderer backstory', self.backstory(c0)))
        return combine(parts)

    def culture_incident(self, c):
        """One culture incident's condition (fails-7, fails-9): true at a matching settlement; not-run when every try could not run it (acc4's
        probe could not invoke an instance lambda); n/a (owner default) when no kingdom of its literal's culture exists on the world (the
        probe's kingdomOfCulture, else the day-0 census's kingdoms); blocked when such a kingdom has no settlement."""
        tries = c.get('tries') or []
        if c.get('candidates') == 0:
            census0 = self.w1.get('w1.census0') or {}
            live = {k.get('culture') for k in g(census0, 'census', 'kingdoms', default=[]) or [] if isinstance(k, dict) and not k.get('eliminated')}
            kingdoms = set(c['kingdomCultures']) if isinstance(c.get('kingdomCultures'), list) else live
            has = c.get('kingdomOfCulture') if isinstance(c.get('kingdomOfCulture'), bool) else bool(set(c.get('literals') or []) & kingdoms)
            if not has and (kingdoms or isinstance(c.get('kingdomOfCulture'), bool)):
                return Verdict(PASS, 'n/a (owner default): no kingdom of %s on this world (kingdoms: %s)' % (c.get('literals'), sorted(x for x in kingdoms if x)))
            return Verdict(BLOCKED, 'no settlement of a kingdom of %s on this world' % c.get('literals'))
        results = [t[3] for t in tries if len(t) > 3]
        if results and all(isinstance(x, str) and x.startswith('not run') for x in results):
            return Verdict(NOT_RUN, 'the condition was not run: %s' % results[0])
        return Verdict(PASS if c.get('trueAt') else FAIL, 'true at %s; tries %s' % (c.get('trueAt'), tries))

    def hideouts(self, census_row):
        if not self.ba:
            return Verdict(BLOCKED, 'the bundle was not kept with the run')
        startup = load(os.path.join(REPO, 'worldgen/policies/startup.json'))
        policy = startup['hideouts']
        biome_policy = policy['biome']
        towns = g(census_row, 'census', 'towns') or []
        hideouts = g(census_row, 'census', 'hideouts') or []
        width = self.width('world1')
        # fails-8: GeneratedCampaignInitializer.Hideouts draws from the nearest town of the hideout's own land group (its cell's component, which
        # takes the MMR-28 group of the travel settlements on it), not the nearest town worldwide.
        group_at, group_of = self.ba.land_groups(g(startup, 'passable', 'classes', default=['land']))
        pooled = {}
        for weights in policy['cultureWeights'].values():
            for k, v in weights.items():
                pooled[k] = pooled.get(k, 0) + v
        required = set(policy.get('requiredCultures') or [])
        bad, checked, notes, grouped = [], 0, [], 0

        def norm(d):
            s = sum(d.values()) or 1
            return {k: v / s for k, v in d.items()}
        for h in hideouts:
            pos = h.get('position')
            if not pos or not towns:
                continue
            checked += 1
            group = group_at(pos[0], pos[1])
            same = [t for t in towns if group is not None and group_of.get(t['id']) == group]
            if same:
                grouped += 1
            town = min(same or towns, key=lambda t: periodic_distance(t['position'], pos, width))
            weights = {k: (1 - biome_policy['share']) * v for k, v in norm(policy['cultureWeights'].get(town.get('culture')) or pooled).items()}
            biome = self.ba.biome_at(pos[0], pos[1])
            ground = dict(biome_policy['biomeWeights'].get(biome, {}))
            if self.ba.near_sea(pos[0], pos[1], biome_policy.get('coastCells', 1)):
                ground[biome_policy['coastCulture']] = ground.get(biome_policy['coastCulture'], 0) + biome_policy['coastWeight']
            for k, v in norm(ground).items() if ground else []:
                weights[k] = weights.get(k, 0) + biome_policy['share'] * v
            if weights.get(h.get('culture'), 0) <= 0:
                # Every required bandit culture takes a donor hideout when no draw gave it one (the initializer's coverage rule).
                if h.get('culture') in required and sum(1 for o in hideouts if o.get('culture') == h.get('culture')) == 1:
                    notes.append('%s takes %s as the coverage hideout' % (h['id'], h.get('culture')))
                else:
                    bad.append((h['id'], h.get('culture'), biome, town['id'], town.get('culture'), 'group %s' % group))
            for p in h.get('parties') or []:
                parts = p.split('|')
                if len(parts) == 3 and parts[2] and parts[2] != h.get('culture'):
                    bad.append((h['id'], 'party ' + parts[0] + ' of ' + parts[2]))
        return Verdict(PASS if checked and not bad else FAIL, '%d hideouts checked (%d against the towns of their land group); mismatches %s%s' % (
            checked, grouped, bad[:5], '; ' + '; '.join(notes[:3]) if notes else ''))

    def backstory(self, census_row):
        stories = census_row.get('backstories') or []
        unsafe = set(g(load(os.path.join(REPO, 'worldgen/policies/population.json')), 'wanderers', 'backstoryUnsafe', default=[]) or [])
        if not stories:
            return Verdict(FAIL, 'no wanderer backstory read')
        bad = []
        for s in stories:
            text = ' '.join(v for v in (s.get('lines') or {}).values() if isinstance(v, str))
            words = [w for w in LORE if re.search(r'\b' + re.escape(w) + r'\b', text)]
            if s.get('template') in unsafe or words:
                bad.append((s.get('hero'), s.get('template'), words))
        first = stories[0]
        return Verdict(PASS if not bad else FAIL, '%d wanderers read (first: %s, %s); naming Calradia or unsafe templates: %s' % (len(stories), first.get('name'), first.get('template'), bad[:3]))

    def performance(self):
        b, vb = self.need('baseline', 'base.frames')
        w, vw = self.need('world1', 'w1.frames')
        mb, vmb = self.need('baseline', 'base.memoryDay')
        mw, vmw = self.need('world1', 'w1.memoryDay')
        for v in (vb, vw, vmb, vmw):
            if v:
                return v
        rows, drop = [], False
        for view in ('near', 'far'):
            bm, wm = g(b, view, 'meanMainMs'), g(w, view, 'meanMainMs')
            if bm and wm:
                change = wm / bm - 1
                drop = drop or change > 0.2
                rows.append('%s main thread %.2f ms vs %.2f ms (%+.0f%%), frame cap %s' % (view, wm, bm, 100 * change, g(w, view, 'frameCapSuspected')))
        ws_b, ws_w = g(mb, 'after', 'workingSetBytes'), g(mw, 'after', 'workingSetBytes')
        rows.append('working set after a day %s vs vanilla %s' % (ws_w, ws_b))
        if drop:
            accepted = self.run.attest.get('c27Accepted')
            return Verdict(PASS if accepted else FAIL, *(rows + ['a drop over 20%: ' + ('accepted by the owner' if accepted else 'a blocker until the owner accepts it')]))
        return Verdict(PASS if len(rows) == 3 else FAIL, *rows)

    def magic(self):
        r, v = self.need('world1', 'w1.magic')
        if v:
            return v, v
        shot = self.run.shot('world1', 'magic-ground')
        retyped = g(r, 'snapshot', 'retypedCells')
        missing = g(r, 'installer', 'installed', 'missingMaterials') or []
        drawn = Verdict(PASS if shot and (r.get('drawn') or 0) > 0 and retyped == 0 else FAIL, 'drawn %s, retyped cells before any cast %s, screenshot %s' % (r.get('drawn'), retyped, bool(shot)))
        visible = combine([('drawn', drawn), ('magic stamps on water', self.decals_on_water('world1', 'magic'))])
        packs = Verdict(PASS if (r.get('drawn') or 0) > 0 and not missing else FAIL, 'missing materials %s' % missing)
        return visible, packs

    def veins_look(self):
        """c29: the vein look in game: screenshots of generated zones (the densest and two more) and of the Nerdun cast spreading (before, while
        its veins reveal, fully grown), and the owner's review of them."""
        m, vm = self.need('world1', 'w1.magic')
        if vm:
            zones = vm
        elif 'zones' not in m:
            zones = Verdict(NOT_RUN, 'the probe that ran took no zone screenshots (a probe before c29)')
        else:
            got = [s['label'] for s in self.shots_of('world1', 'magic-') if s['path'] and (s['label'] == 'magic-ground' or s['label'].startswith('magic-zone-'))]
            want = min(3, max(1, len(m.get('zones') or [])))   # the densest zone and up to two more, as many as the world has
            zones = Verdict(PASS if 'magic-ground' in got and len(got) >= want else FAIL, 'zone screenshots %s of %d zones (%d wanted)' % (got, len(m.get('zones') or []), want))
        c, vc = self.need('world1', 'w1.cast')
        if vc:
            spread = vc
        else:
            want = ('cast-before', 'cast-mid', 'cast-full')
            missing = [l for l in want if not self.run.shot('world1', l)]
            spread = Verdict(PASS if not missing else FAIL, 'cast screenshots: missing %s; reveal %s' % (missing, {k: v for k, v in (c.get('reveal') or {}).items() if k != 'hours'}))
        # rev6: drawn by a route that renders on the campaign map (MMR-291's vein meshes), and the look seen, not inferred from records.
        route = self.mesh_layer('world1', 'w1.magic', 'veins')
        toggle = (m or {}).get('toggle') if isinstance((m or {}).get('toggle'), dict) else {}
        look = vm or self.look_shown(toggle.get('label') or 'magic-veins', toggle.get('hiddenLabel') or 'magic-veins-hidden', 'veins', 'c29', 'the owner review of the vein look')
        return combine([('generated zones', zones), ('the Nerdun cast spreading', spread), ('drawn as overlay meshes (MMR-291)', route), ('veins shown', look),
                        ('owner review', self.attested('c29', 'the owner review of the vein look'))])

    def look_shown(self, shown_label, hidden_label, layer, attest_key, what):
        """rev6 (c29, c31): a look is never passed on records alone. It passes when the screenshot step recorded the overlay mesh layer visible
        (the shown shot's mesh record: drawn as meshes, not hidden, entities in the view; the hidden shot's: hidden; and hiding the layer changes
        the view, the toggle diff >= 0.02), or when the owner's review of the screenshots is attested; else it is blocked on that review."""
        if self.run.attest.get(attest_key):
            return self.attested(attest_key, what)
        shot, hidden = self.run.shot('world1', shown_label), self.run.shot('world1', hidden_label)
        m, mh = (shot or {}).get('meshLayer') or {}, (hidden or {}).get('meshLayer') or {}
        recorded = bool(m.get('meshes')) and not m.get('hidden') and (m.get('inView') or 0) > 0 and bool(mh.get('hidden'))
        share = diff_share(central_pixels(shot['path']), central_pixels(hidden['path'])) if shot and hidden else None
        facts = '%s at %s: meshes %s, %s of %s entities in view, hidden %s; at %s: hidden %s; toggle diff %s' % (
            layer, shown_label if shot else shown_label + ' (no screenshot)', m.get('meshes'), m.get('inView'), m.get('entities'), m.get('hidden'),
            hidden_label if hidden else hidden_label + ' (no screenshot)', mh.get('hidden'), 'not measured' if share is None else '%.3f (gate 0.02)' % share)
        if recorded and share is not None and share >= 0.02:
            return Verdict(PASS, 'the screenshot step recorded the mesh layer visible (%s)' % facts)
        hint = '' if share is None or share >= 0.02 else '; hiding the layer barely changes the view: read the decal lab\'s mesh column, the fallback is MMR-291\'s publish request P'
        return Verdict(BLOCKED, '%s is not attested yet (attestations.json "%s") and the screenshot step did not record the mesh layer visible (%s)%s' % (what, attest_key, facts, hint))

    def mesh_layer(self, phase, name, layer):
        """c33 (MMR-291): one overlay mesh layer as the probe recorded it in its step (OverlayMeshRuntime.Record and the toggle pair): drawn as
        meshes (policy mode mesh, materials ready), entities live, no missing material and no error, every material of the layer resolved
        (published, a template copy with the published textures, or a vanilla water material), the visibility toggle taken both ways, and both
        screenshots on disk. Records only: what the meshes look like is the screenshot review's (c24, c29, c31)."""
        r, v = self.need(phase, name)
        if v:
            return v
        rec = r.get('overlayMeshes')
        if rec is None:
            rec = g(r, 'record', 'overlayMeshes')
        if not isinstance(rec, dict):
            return Verdict(NOT_RUN, 'the probe that ran recorded no overlay meshes in %s (a probe before MMR-291)' % name)
        if 'error' in rec and 'policy' not in rec:
            return Verdict(FAIL, 'the overlay mesh record failed: %s' % rec['error'])
        part = rec.get('rivers' if layer == 'rivers' else 'magic') or {}
        live = g(part, 'installed', 'byLayer', layer) or {}
        missing = g(part, 'installed', 'missingMaterials') or []
        errors = (rec.get('errors') or []) + (g(part, 'installed', 'errors') or [])
        of_layer = lambda key: 'rivers' if key.startswith('river') else 'veins' if key.startswith('vein:') else 'magicWater' if key == 'magicWater' else None
        mats = {k: m for k, m in (part.get('materials') or {}).items() if of_layer(k) == layer}
        resolved = lambda m: isinstance(m, dict) and not m.get('error') and bool(m.get('published') or m.get('copy') or m.get('material'))
        toggle = r.get('toggle')
        t = toggle if isinstance(toggle, dict) else {}
        shot, hidden = self.run.shot(phase, t.get('label') or '?'), self.run.shot(phase, t.get('hiddenLabel') or '?')
        problems = []
        if g(rec, 'policy', layer) != 'mesh' or g(rec, 'policy', 'enabled') is False:
            problems.append('not drawn as meshes by the policy (enabled %s, mode %s)' % (g(rec, 'policy', 'enabled'), g(rec, 'policy', layer)))
        if g(rec, 'ready', layer) is not True:
            problems.append('materials not ready (%s)' % g(rec, 'ready', layer))
        if not live.get('entities'):
            problems.append('no live %s entity (status %s)' % (layer, part.get('status')))
        if missing:
            problems.append('missing materials %s' % missing)
        if not mats or not all(resolved(m) for m in mats.values()):
            problems.append('materials unresolved %s' % ({k: m for k, m in mats.items() if not resolved(m)} or 'none recorded'))
        if errors:
            problems.append('errors %s' % errors[:3])
        if not (t.get('setVisibleFalse') is True and t.get('setVisibleTrue') is True):
            problems.append('the visibility toggle did not take (%s)' % (toggle if not t else 'hide %s, show %s' % (t.get('setVisibleFalse'), t.get('setVisibleTrue'))))
        if not shot or not hidden:
            problems.append('screenshots missing (shown %s, hidden %s)' % (bool(shot), bool(hidden)))
        facts = '%s entities, %s pieces, %s triangles live (status %s); materials %s; toggle %s/%s; screenshots %s, %s' % (
            live.get('entities'), live.get('pieces'), live.get('triangles'), part.get('status'),
            {k: (m.get('published') or m.get('material') or ('copy of %s' % m.get('template') if m.get('copy') else m)) if isinstance(m, dict) else m for k, m in mats.items()},
            t.get('setVisibleFalse'), t.get('setVisibleTrue'), shot['path'] if shot else None, hidden['path'] if hidden else None)
        return Verdict(FAIL if problems else PASS, *(problems + [facts]))

    def lab_meshes(self):
        """c33: MMR-291's mesh grid in the decal lab (phase decallab, step w1.decalLab): each overlay layer's material on its test pieces at the
        land and sea spots, drawn yes / no from the lab's shown-vs-hidden difference (tools/decal_lab_sheet.py's decal-lab/decal-lab.json). A
        column drawn nowhere fails (route risk M1: the runtime material draws nothing on a campaign-map mesh). MMR-291-W: where the sheet names the
        policy's passes (meshPasses) and the sea grid has the magic water layer's pass row, that piece must draw over the sea (magic water's own
        place), else it fails. None when the lab did not run."""
        path = os.path.join(self.run.folder, 'decal-lab', 'decal-lab.json')
        if not os.path.exists(path):
            return Verdict(BLOCKED, 'the decal lab ran (%s) but wrote no sheet (decal-lab/decal-lab.json)' % self.run.phase_status('decallab')) if self.run.phase_status('decallab') else None
        report = load(path)
        spots = report.get('spots') or {}
        cols = {}
        for spot in ('land-mesh', 'sea-mesh'):
            for key, d in ((spots.get(spot) or {}).get('decals') or {}).items():
                col, _, row = key.partition('/')
                cols.setdefault(col, []).append(('%s %s' % (spot.split('-')[0], row), bool((d or {}).get('drawn'))))
        if not cols:
            return Verdict(BLOCKED, 'the decal lab has no mesh grid measured (no lab-land-mesh / lab-sea-mesh shot pair)')
        want = ('mesh:vein', 'mesh:river', 'mesh:magicWater')
        none = [c for c in want if not any(drawn for _, drawn in cols.get(c, []))]
        problems = ['drawn nowhere: %s' % none] if none else []
        facts = ['%s drawn at %s' % (c, [where for where, drawn in cols.get(c, []) if drawn] or 'none') for c in want]
        passes = report.get('meshPasses') or {}
        sea = (spots.get('sea-mesh') or {}).get('decals') or {}
        piece = sea.get('mesh:magicWater/%s' % passes.get('magicWater')) if passes.get('magicWater') else None
        if piece is not None and not piece.get('offscreen'):
            if not piece.get('drawn'):
                problems.append("magic water's pass %s draws nothing over the sea (mesh:magicWater/%s)" % (passes['magicWater'], passes['magicWater']))
            facts.append("magic water's pass %s over the sea: %s (meanDiff %s)" % (passes['magicWater'], 'drawn' if piece.get('drawn') else 'not drawn', piece.get('meanDiff')))
        return Verdict(FAIL if problems else PASS, *(problems + facts))

    def overlay_meshes(self):
        """c33: MMR-291's overlay meshes in game, graded from the probe's records (each layer's entities, materials, errors and toggle at its
        world 1 step), the screenshots' existence, the decal lab's mesh grid when the lab ran in this run, and c27's frame cost (w1.frames runs
        with the meshes installed). The look itself is the screenshot review's; the grade says so."""
        parts = [('veins', self.mesh_layer('world1', 'w1.magic', 'veins')), ('rivers', self.mesh_layer('world1', 'w1.water', 'rivers')),
                 ('magic water', self.mesh_layer('world1', 'w1.magicWater', 'magicWater'))]
        lab = self.lab_meshes()
        if lab is not None:
            parts.append(('decal lab mesh grid', lab))
        parts.append(('frame cost within c27', self.performance()))
        v = combine(parts)
        return Verdict(v.status, *(v.detail + [LOOK_REVIEW]))

    def no_freeze(self):
        """c32 (MMR-247 X): with the fresh-map rule off, no world 1 step hung or crashed, no case reloaded for a fresh map, the battles, castle
        and siege ran (at least one of them after a mission on the same loaded map: the acc4/acc5 freezes' condition), and, where the hang repro
        ran (phase hangrepro, or the decal lab's own war and castle), every variant with the fix on ran. The pre-fix controls (dressing entities
        moved) are listed, not graded: they crash by design."""
        if not self.ran('world1'):
            return Verdict(NOT_RUN, 'world 1 did not run')
        parts = []
        hangs = [h for h in self.hangs if h['phase'] == 'world1']
        crashes = [c for c in self.crashes if c['phase'] == 'world1']
        parts.append(('no world 1 step hung or crashed', Verdict(PASS if not hangs and not crashes else FAIL, 'hangs %s; crashes %s' % (
            [(h['hungStep'], h['lastStage']) for h in hangs] or 'none', [(c['step'], c['fault'], c['lastStage']) for c in crashes] or 'none'))))
        flags = [(d.get('outputs') or {}).get('freshMapAfterMission') for d in self.run.results.get('world1') or []]
        reloads = sorted(n for n, r in self.w1.items() if g(r, 'case', 'freshMapBefore'))
        if reloads:
            fresh = Verdict(FAIL, 'fresh-map reloads before %s: c32 needs the cases to run without them' % reloads)
        elif any(f is True for f in flags):
            fresh = Verdict(NOT_RUN, 'the fresh-map rule was on (-FreshMapAfterMission) in a world 1 launch %s: c32 needs it off' % flags)
        elif any(f is None for f in flags):
            fresh = Verdict(NOT_RUN, 'a world 1 probe that ran does not record the fresh-map rule (a probe before c32, where it was on) %s' % flags)
        else:
            fresh = Verdict(PASS, 'off in all %d world 1 launches, no fresh-map reload' % len(flags))
        parts.append(('fresh-map rule off', fresh))
        ran, after = [], []
        for name in AFTER_MISSION_CASES:
            r, v = self.need('world1', name)
            if v:
                parts.append((name, v))
                continue
            ran.append(name)
            if g(r, 'case', 'missionOnThisMapBefore') is True:
                after.append(name)
        recorded = any(g(self.w1.get(n) or {}, 'case', 'missionOnThisMapBefore') is not None for n in ran)
        parts.append(('cases after a mission', Verdict(PASS, 'ran %s; after a mission on the same loaded map: %s' % (ran, after)) if after else
                      Verdict(NOT_RUN, 'ran %s, none after a mission on the same loaded map%s' % (ran, '' if recorded or not ran else ' recorded (a probe before c32)'))))
        for phase in ('hangrepro', 'decallab'):
            p = (self.run.state.get('phases') or {}).get(phase)
            if not p:
                continue
            rows = g(p, 'detail', 'hangRepro') or {}
            fix = {k: r for k, r in rows.items() if k not in REPRO_CONTROLS and isinstance(r, dict)}
            controls = {k: (r or {}).get('outcome') for k, r in rows.items() if k in REPRO_CONTROLS}
            bad = [k for k, r in fix.items() if r.get('outcome') in ('engine blocked', 'crashed', 'error', 'timeout')]
            status = FAIL if bad else PASS if fix and all(r.get('outcome') == 'ran' for r in fix.values()) else BLOCKED
            parts.append(('hang repro (%s)' % phase, Verdict(status, 'with the fix: %s; pre-fix controls (crash expected): %s' % (
                {k: '%s%s' % (r.get('outcome'), ' (%s)' % r.get('detail') if r.get('outcome') != 'ran' and r.get('detail') else '') for k, r in fix.items()} or 'no outcome (phase %s)' % p.get('status'),
                controls or 'none'))))
        return combine(parts)

    def features(self):
        """c30: MMR-289's magic features in game: drawn in a generated zone, around the Nerdun cast and again after Continue, none on water, a
        road or a settlement's clearance, within c27's frame limit (the frames step runs with them installed)."""
        m, vm = self.need('world1', 'w1.magic')
        if vm:
            return vm
        f = m.get('features')
        if not isinstance(f, dict):
            return Verdict(NOT_RUN, 'the probe that ran read no magic features (a probe before c30)')
        if not f.get('available'):
            return Verdict(BLOCKED, 'MMR-289 not in this build: %s' % (f.get('reason') or 'MagicFeaturesRuntime absent'))
        c, vc = self.need('world1', 'w1.cast')
        k, vk = self.need('world1', 'w1.continue')
        parts = [('generated zone', Verdict(PASS if (f.get('live') or 0) > 0 and (f.get('generated') or 0) > 0 and not f.get('errors') else FAIL,
                                            'live %s, generated %s, errors %s' % (f.get('live'), f.get('generated'), (f.get('errors') or [])[:2])))]
        cf = (c or {}).get('features') or {}
        parts.append(('around the Nerdun cast', vc or Verdict(PASS if (cf.get('liveMutation') or 0) > 0 else FAIL, 'live-mutation features %s of %s live' % (cf.get('liveMutation'), cf.get('live')))))
        kf = (k or {}).get('features') or {}
        parts.append(('after Continue', vk or Verdict(PASS if (kf.get('live') or 0) > 0 and (kf.get('liveMutation') or 0) >= (cf.get('liveMutation') or 0) else FAIL,
                                                      'live %s, live-mutation %s (cast %s)' % (kf.get('live'), kf.get('liveMutation'), cf.get('liveMutation')))))
        off = {'%s %s' % (name, key): row.get(key) for name, row in (('generated zone', f), ('cast', cf), ('Continue', kf)) for key in ('onWater', 'onRoad', 'nearSettlement') if row and row.get(key)}
        parts.append(('never on water, roads or pads', Verdict(PASS if not off else FAIL, 'features off their clearance %s' % (off or 'none'))))
        parts.append(('frame cost within c27', self.performance()))
        return combine(parts)

    def magic_water(self):
        """c31: MMR-290's magic water in game: stamps drawn without errors, a screenshot over magic water, and the toggle diff (the same view
        with the stamps hidden): it settles R1 (does native fill the water shader's decal list); when the stamps barely change the view, the
        documented fallback is magicWater.mode shore (F1 + F2)."""
        r, v = self.need('world1', 'w1.magicWater')
        magic = self.w1.get('w1.magic')
        if r is None and v.status != FAIL and magic and not any(k in magic for k in ('zones', 'features', 'veins')):
            return Verdict(NOT_RUN, 'the probe that ran has no magic water step (a probe before c31)')
        if v:
            return v
        record = r.get('record') or {}
        errors = (r.get('errors') or []) + (record.get('layerErrors') or [])
        drawn = r.get('drawn') or 0
        shot, hidden = self.run.shot('world1', g(r, 'toggle', 'label') or 'magic-water'), self.run.shot('world1', g(r, 'toggle', 'hiddenLabel') or 'magic-water-hidden')
        parts = [('drawn', Verdict(PASS if drawn > 0 and not errors else FAIL, 'drawn %s, errors %s, record %s' % (drawn, errors[:2], {k: v for k, v in record.items() if not isinstance(v, (list, dict))})))]
        parts.append(('screenshot', Verdict(PASS if shot else FAIL, 'magic water screenshot %s' % (shot['path'] if shot else None))))
        # rev6: magic water shows (MMR-291's meshes): the screenshot step's mesh record and toggle diff, else the owner's review of the shots.
        parts.append(('magic water shown', self.look_shown(g(r, 'toggle', 'label') or 'magic-water', g(r, 'toggle', 'hiddenLabel') or 'magic-water-hidden', 'magicWater', 'c31',
                                                           'the owner review of the magic water look')))
        return combine(parts)

    def winter(self):
        r, v = self.need('world1', 'w1.season')
        if v:
            # No season evidence decides no snow route.
            return v, Verdict(BLOCKED, 'no season evidence: %s' % ', '.join(v.detail))
        ext = {e.get('extreme'): e for e in r.get('extremes') or [] if e.get('extreme')}
        whites, unusable = {}, {}
        for s in self.shots_of('world1', 'season-'):
            m = re.match(r'season-(winter|summer)-(.+)$', s['label'])
            if m:
                whites[(m.group(1), m.group(2))] = image_stats(s['path'])
                # notrun-2: a shot at night or before the scene settled decides nothing (the probe shoots at midday after the scene settles).
                hour = number(s.get('hour'))
                why = [w for w in ('hour %.1f is outside daylight (10-14)' % hour if hour is not None and not 10 <= hour <= 14 else None,
                                   'the scene had not settled' if s.get('settled') is False else None) if w]
                if why:
                    unusable[(m.group(1), m.group(2))] = '%s %s: %s' % (m.group(1), m.group(2), ', '.join(why))
        parts = []
        places = r.get('places') or {}

        def usable(place, name, verdict):
            bad = [unusable[(e, place)] for e in ('winter', 'summer') if (e, place) in unusable]
            return (name, Verdict(BLOCKED, 'season shots not usable: ' + '; '.join(bad))) if bad else (name, verdict)
        # The snow route's evidence: the cold region's and the land ice's screenshots at both extremes, for each of the two this world has.
        # On the permanentWhite route (in force since acc5) the cold region must not whiten: only land ice is white (docs/water-climate.md).
        permanent = self.snow_route_in_force() == 'permanentWhite'
        route_parts = []
        if 'winter' not in ext or 'summer' not in ext:
            parts.append(('extremes', Verdict(FAIL, 'reached %s' % sorted(ext))))
            route_parts.append(('extremes', Verdict(BLOCKED, 'both extremes not reached (%s)' % sorted(ext))))
        else:
            cw, cs = whites.get(('winter', 'cold')), whites.get(('summer', 'cold'))
            ww, ws = whites.get(('winter', 'warm')), whites.get(('summer', 'warm'))
            iw, is_ = whites.get(('winter', 'ice')), whites.get(('summer', 'ice'))
            if 'cold' in places:
                if cw is None or cs is None:
                    route_parts.append(('cold region', Verdict(BLOCKED, 'no cold region screenshots (or Pillow missing)')))
                elif permanent:
                    route_parts.append(usable('cold', 'cold region not whitened (permanentWhite)', Verdict(PASS if cw['white'] - cs['white'] < 0.15 else FAIL, 'white share winter %.2f, summer %.2f' % (cw['white'], cs['white']))))
                else:
                    route_parts.append(usable('cold', 'cold region whitens', Verdict(PASS if cw['white'] - cs['white'] >= 0.15 else FAIL, 'white share winter %.2f, summer %.2f' % (cw['white'], cs['white']))))
            if ww and ws:
                parts.append(usable('warm', 'warm region not whitened', Verdict(PASS if ww['white'] - ws['white'] < WARM_WHITENING and ww['white'] < ICE_WHITE else FAIL, 'white share winter %.2f, summer %.2f (luminance %s, %s)' % (
                    ww['white'], ws['white'], *('%.2f' % s['luminance'] if s.get('luminance') is not None else '-' for s in (ww, ws))))))
            if 'ice' in places:
                if iw is None or is_ is None:
                    route_parts.append(('land ice', Verdict(BLOCKED, 'no land ice screenshots (or Pillow missing)')))
                else:
                    route_parts.append(usable('ice', 'land ice white', Verdict(PASS if iw['white'] >= ICE_WHITE and is_['white'] >= ICE_WHITE else FAIL, 'white share winter %.2f, summer %.2f' % (iw['white'], is_['white']))))
            if not route_parts:
                route_parts.append(('places', Verdict(BLOCKED, 'no cold region or land ice on this world')))
            parts.extend(route_parts)
        parts.append(('snow weather', self.snow_weather(r)))
        winds = [w for w in r.get('winds') or [] if len(w) == 3]
        angles = [math.degrees(math.atan2(w[2], w[1])) for w in winds if w[1] or w[2]]
        spread = (max(angles) - min(angles)) if angles else 0
        parts.append(('winds by latitude', Verdict(PASS if spread > 30 else FAIL, 'wind directions over %d latitudes spread %.0f deg' % (len(angles), spread))))
        # namedTexture only on passing evidence; missing evidence leaves the route undecided (blocked), failing evidence is the fallback.
        statuses = [p[1].status for p in route_parts]
        route_status = FAIL if FAIL in statuses else (BLOCKED if BLOCKED in statuses or not statuses else PASS)
        route = Verdict(route_status, *(['%s: %s' % (n, ', '.join(v.detail)) for n, v in route_parts] + ['snow texture %s' % g(r, 'support', 'climateVisuals')]))
        return combine(parts), route

    def snow_weather(self, r):
        """c9's Snowy state follows season length (notrun-6): each place's Snowy or Blizzard share over the sampled days (the gameplay weather
        event; the terrain effect has only Default and Wet in 1.4.8) against the share the engine rule gives its weather node's snow byte on
        those days' season factors (within 0.15); without the bytes, the fixed bounds (cold between 0.05 and 0.95, warm below 0.05, ice above 0.9)."""
        days = r.get('days') or []

        def share(k):
            seen = [d for d in days if k in d]
            return sum(1 for d in seen if d.get(k) in SNOW) / float(len(seen)) if seen else None
        snow_bytes = r.get('snowBytes') or {}
        if not snow_bytes:
            cold, warm, ice = share('cold'), share('warm'), share('ice')
            return Verdict(PASS if cold is not None and 0.05 < cold < 0.95 and (warm is None or warm < 0.05) and (ice is None or ice > 0.9) else FAIL,
                           'Snowy or Blizzard share over %d days: cold %s, warm %s, ice %s (fixed bounds: no snow bytes recorded)' % (len(days), cold, warm, ice))
        rows, ok = [], True
        for place, byte in sorted(snow_bytes.items()):
            seen = [d for d in days if place in d and number(d.get('season')) is not None]
            if not seen or byte is None:
                rows.append('%s: no sampled days' % place); ok = False
                continue
            actual = sum(1 for d in seen if d.get(place) in SNOW) / float(len(seen))
            expected = sum(1 for d in seen if snow_value(number(d['season']), byte) > 0.55) / float(len(seen))
            good = abs(actual - expected) <= 0.15
            ok = ok and good
            rows.append('%s byte %s: Snowy %.2f, expected %.2f on the same %d days (year share %s)%s' % (place, byte, actual, expected, len(seen), g(r, 'snowyShareExpected', place), '' if good else ' (off by more than 0.15)'))
        return Verdict(PASS if ok and rows else FAIL, *rows)

    def zone_battle(self):
        """The battle inside the cast's zone: w1.battle.mutated's case (the acc5 order), else acc4's battle inside w1.cast. The battle cell row is
        [cell, x, y, overlay type, face type]: the expected class is the overlay type's (battle_class live)."""
        r, v = self.need('world1', 'w1.battle.mutated')
        if r is not None or (v and v.status == FAIL):
            return v or self.battle_class(r.get('case'), live=(r.get('battleCell') or [None] * 4)[3])
        c = self.w1.get('w1.cast')
        if not c or gate(c, self.ran('world1')):
            return v
        battle, cell = c.get('battle'), c.get('battleCell')
        if isinstance(battle, dict):
            return gate(battle) or self.battle_class(battle.get('case'), live=cell[3] if cell else None)
        return Verdict(BLOCKED, battle or 'no battle')

    def cast(self):
        r, v = self.need('world1', 'w1.cast')
        if v:
            return v, v, v
        apply_ok = (g(r, 'processStarts', 'producer_apply') or 0) >= 1
        retyped = g(r, 'after', 'retypedCells') or g(r, 'afterChange', 'retypedCells') or 0
        lock = load(os.path.join(REPO, 'worldgen/producer.lock.json'))
        producer = r.get('producer')
        repinned = producer == lock.get('sha256') or producer == g(lock, 'producer', 'sha256')
        faces = r.get('retyped') or []
        face_ok = faces and all(f[3] == f[4] for f in faces)
        c3 = Verdict(PASS if apply_ok and repinned and retyped > 0 else FAIL, 'producer_apply processes %s, producer %s (pin %s), retyped cells %s' % (g(r, 'processStarts', 'producer_apply'), producer, g(lock, 'producer', 'sha256'), retyped))
        zone = Verdict(PASS if retyped > 0 and face_ok and self.run.shot('world1', 'cast-before') and self.run.shot('world1', 'cast-after') else FAIL,
                       'retyped %s cells; overlay type = face type at %d of %d sampled cells' % (retyped, sum(1 for f in faces if f[3] == f[4]), len(faces)))
        return c3, zone, self.zone_battle()

    def continued(self):
        r, v = self.need('world1', 'w1.continue')
        if v:
            return v
        ok = r.get('mutationPersisted') and r.get('generatorStarted') is False and g(r, 'cacheAcquired', 'status') == 'reused' and g(r, 'loader', 'cacheForecast', 'reuse') is True
        return Verdict(PASS if ok else FAIL, 'mutation persisted %s, generator started %s, caches %s, forecast reuse %s' % (
            r.get('mutationPersisted'), r.get('generatorStarted'), g(r, 'cacheAcquired', 'status'), g(r, 'loader', 'cacheForecast', 'reuse')))

    def soak(self):
        r, v = self.need('world1', 'w1.soak')
        if v:
            return v
        pop = g(r, 'census', 'population') or {}
        clans = g(r, 'census', 'clans') or []
        counts = r.get('eventCounts') or {}
        events = r.get('events') or []
        problems = []
        days = g(r, 'advance', 'daysAdvanced') or 0
        if days < 30:
            problems.append('only %s days' % days)
        if pop.get('wanderers') != pop.get('wanderersTarget'):
            problems.append('wanderers %s, W %s' % (pop.get('wanderers'), pop.get('wanderersTarget')))
        if not pop.get('wanderersRotated'):
            problems.append('no wanderer rotated')
        companies = [c for c in clans if isinstance(c, dict) and c.get('mercenary') and c.get('minor')]
        small = [c['id'] for c in companies if c.get('alive', 0) < 4]
        if not companies or small:
            problems.append('%d companies, below 4 heroes: %s' % (len(companies), small[:3]))
        # notrun-5: a hire is ChangeKingdomActionDetail.JoinAsMercenary of a minor clan (LeaveAsMercenary also names a mercenary), or a company
        # serving at the end that did not serve at day 0.
        hired = [e for e in events if e[1] == 'clanChangedKingdom' and len(e[2]) > 4 and e[2][3] == 'JoinAsMercenary' and e[2][4]]
        before = {c.get('id') for c in g(self.w1.get('w1.census0') or {}, 'census', 'clans', default=[]) or [] if isinstance(c, dict) and c.get('underMercenaryService')}
        serving = [c['id'] for c in companies if c.get('underMercenaryService') and c['id'] not in before]
        if not hired and not serving:
            problems.append('no AI kingdom hired a company (JoinAsMercenary heard %d, newly serving %d)' % (len(hired), len(serving)))
        # c20's last clause: no house of a dissolved kingdom was removed (its compiled houses still listed, neither removed nor eliminated).
        destroyed = [e[2][0] for e in events if e[1] == 'kingdomDestroyed']
        detail = pop.get('houseDetail') or {}
        houses = (self.ba.campaign or {}).get('houses', []) if self.ba else []
        lost = []
        for k in destroyed:
            for h in (x for x in houses if x.get('kingdom') == k):
                d = detail.get(h['id'])
                if d is None or d.get('removed') or d.get('eliminated'):
                    lost.append((k, h['id'], 'absent from the census' if d is None else 'removed' if d.get('removed') else 'eliminated'))
        if destroyed and not houses:
            problems.append('kingdoms dissolved %s, but the bundle (its houses) was not kept with the run' % destroyed)
        if lost:
            problems.append('houses of dissolved kingdoms lost: %s' % lost[:4])
        if not counts:
            problems.append('no house or kingdom change heard')
        return Verdict(PASS if not problems else FAIL, *(problems or ['%s days; events %s; hired %d, newly serving %d; kingdoms dissolved %s, their houses kept' % (days, counts, len(hired), len(serving), destroyed)]))

    def economy(self):
        base, vb = self.need('baseline', 'base.soak')
        soak, vs = self.need('world1', 'w1.soak')
        if vb or vs:
            return vb or vs
        f = lambda e, k: g(e, k)
        rows = []
        for k in ('meanTownProsperity', 'meanVillageHearth', 'meanVillageStock', 'meanTownFood'):
            rows.append('%s: generated %s -> %s, vanilla %s -> %s' % (k, f(soak.get('economyBefore'), k), f(soak.get('economyAfter'), k), f(base.get('economyBefore'), k), f(base.get('economyAfter'), k)))
        home = soak.get('homeTerrain') or {}
        rows.append('home terrain per culture: ' + '; '.join('%s %s' % (c, sorted((d.get('faces') or {}).items(), key=lambda kv: -kv[1])[:2]) for c, d in sorted(home.items()) if isinstance(d, dict)))
        balance = self.run.attest.get('balance')
        if balance:
            rows.append('balance pass: %s (%s)' % (balance.get('note'), balance.get('by')))
            return Verdict(PASS, *rows)
        v = self.balance_review(sorted(c for c, d in home.items() if isinstance(d, dict)))
        return Verdict(v.status, *(rows + v.detail))

    def balance_review(self, cultures):
        """MMR-247 F: the balance pass (ticket step 6) as recorded in the committed review (evidence/map-epic-2/balance-pass.json): an item per
        speed term (Snow, Swamp, road), the trade-area economy (RT-42) and each culture the soak's home terrain names (RT-27), each with its
        observation, its decision ('none' or 'change', a change naming what changed) and the reason. Missing: blocked (not recorded yet); an
        item missing or without a decision and reason: blocked, naming it."""
        path = self.run.balance_path
        if not path or not os.path.exists(path):
            return Verdict(BLOCKED, 'balance pass: not recorded yet (%s)' % os.path.relpath(path or BALANCE_REVIEW, REPO).replace('\\', '/'))
        doc = load(path)
        items = {i.get('id'): i for i in doc.get('items') or [] if isinstance(i, dict)}
        wanted = list(BALANCE_ITEMS) + ['home.' + c for c in cultures]
        missing = [k for k in wanted if k not in items]
        bad = [k for k in wanted if k in items and not (items[k].get('decision') in ('none', 'change') and items[k].get('reason') and items[k].get('observation')
                                                     and (items[k]['decision'] == 'none' or items[k].get('change')))]
        changes = ['%s: %s' % (k, items[k]['change']) for k in wanted if k in items and items[k].get('decision') == 'change' and items[k].get('change')]
        head = 'balance pass (%s, from run %s by %s): %d items, %s' % (os.path.relpath(path, REPO).replace('\\', '/'), doc.get('run'), doc.get('by'), len(wanted),
                                                                    ('changes: ' + '; '.join(changes)) if changes else 'no policy change')
        lines = [head] + ['%s: %s (%s)' % (k, items[k].get('decision'), items[k].get('reason')) for k in wanted if k in items]
        if missing or bad:
            return Verdict(BLOCKED, *(lines + ['balance pass items not recorded: %s; without a decision, observation or reason: %s' % (missing, bad)]))
        return Verdict(PASS, *lines)

    def world2(self):
        parts = [('Town count = sites', self.towns_sites('world2', 'B'))]
        m, ch = self.census_match('world2', 'B')
        parts.append(('day-0 census', m))
        before, vb = self.need('world2', 'w2.world1Before')
        after, va = self.need('world2', 'w2.world1After')
        cont, vc = self.need('world2', 'w2.continue1')
        if va:
            parts.append(("world 1's scene and caches kept", va))
        else:
            ok = after.get('sceneInstalled') and g(after, 'forecast', 'reuse') and (vb or before.get('sceneTree') == after.get('sceneTree'))
            parts.append(("world 1's scene and caches kept", Verdict(PASS if ok else FAIL, 'scene installed %s (tree unchanged %s), forecast reuse %s' % (
                after.get('sceneInstalled'), None if vb else before.get('sceneTree') == after.get('sceneTree'), g(after, 'forecast', 'reuse')))))
        parts.append(('Continue world 1', vc or Verdict(PASS if g(cont, 'cacheAcquired', 'status') == 'reused' and cont.get('generatorStarted') is False else FAIL,
                                                        'caches %s, generator started %s, %.0f s' % (g(cont, 'cacheAcquired', 'status'), cont.get('generatorStarted'), cont.get('seconds') or 0))))
        return combine(parts)

    def estimates(self):
        parts = []
        for phase, name in (('world1', 'w1.start'), ('world2', 'w2.start')):
            r, v = self.need(phase, name)
            if v:
                parts.append((phase, v)); continue
            est = g(r, 'preview', 'estimateSeconds')
            measured, carried = self.cache_build(r)
            if not est or not measured:
                parts.append((phase, Verdict(FAIL, 'estimate %s, measured %s (cache status %s; no run measured a build of these caches)' % (est, measured, g(r, 'cacheBuild', 'status'))))); continue
            err = abs(est - measured) / measured
            parts.append((phase, Verdict(PASS if err <= 0.25 else FAIL, 'estimate %.0f s vs measured %.0f s (%.0f%%; %s settlements)%s' % (
                est, measured, 100 * err, g(r, 'preview', 'settlements'), '; the start reused its caches, ' + carried if carried else ''))))
        return combine(parts)

    def missing_cache(self):
        pre = (self.run.state.get('phases') or {}).get('preflight') or {}
        t = g(pre, 'detail', 'tests', 'Test-WorldStore')
        offline = Verdict(NOT_RUN, 'preflight did not run') if not t else Verdict(PASS if t.get('exit') == 0 else FAIL, 'Test-WorldStore exit %s' % t.get('exit'))
        r, v = self.need('world1', 'w1.continue')
        live = v or Verdict(PASS if g(r, 'forecastWithoutCaches', 'reuse') is False and g(r, 'forecastWithoutCaches', 'kind') == 'missing' else FAIL,
                            'forecast with the caches hidden: %s' % r.get('forecastWithoutCaches'))
        return combine([('offline (Test-WorldStore)', offline), ('in game (the forecast without the caches)', live)])

    def mods_check(self):
        parts = []
        m, vm = self.need('mods', 'mods.modules')
        if vm:
            return vm
        s, vs = self.need('mods', 'mods.setup')
        parts.append(('setup', vs or Verdict(PASS if (s.get('outcome') == 'generated' or ((s.get('outcome') or '').startswith('refused') and s.get('namesModule'))) and not s.get('timedOut') else FAIL,
                                             'outcome %s, names the module %s, timed out %s' % (s.get('outcome'), s.get('namesModule'), s.get('timedOut')))))
        l, vl = self.need('mods', 'mods.load')
        if vl:
            parts.append(('load', vl))
        else:
            phases = [p.get('phase') if isinstance(p, dict) else p for p in g(l, 'loader', 'phases') or []]
            built = g(l, 'cacheAcquired', 'status') in ('generated', 'regenerated')
            ok = (l.get('outcome') == 'loaded' and not built) or (l.get('outcome') == 'refused' and l.get('namesModule') and 'Loading' not in [str(p) for p in phases])
            parts.append(('load', Verdict(PASS if ok and not l.get('timedOut') else FAIL, 'outcome %s, names the module %s, caches %s, phases %s, timed out %s' % (
                l.get('outcome'), l.get('namesModule'), g(l, 'cacheAcquired', 'status'), phases, l.get('timedOut')))))
        removed = g((self.run.state.get('phases') or {}).get('mods') or {}, 'detail', 'removed')
        parts.append(('module removed', Verdict(PASS if removed == 'removed' else (NOT_RUN if removed is None else FAIL), str(removed))))
        return combine(parts)

    def game_updates(self):
        pre = (self.run.state.get('phases') or {}).get('preflight') or {}
        t = g(pre, 'detail', 'tests', 'Test-GameUpdates')
        offline = Verdict(NOT_RUN, 'preflight did not run') if not t else Verdict(PASS if t.get('exit') == 0 else FAIL, 'Test-GameUpdates exit %s' % t.get('exit'))
        r, v = self.need('world1', 'w1.identity')
        sig = v or Verdict(PASS if g(r, 'audit', 'patchSignatureFailures') == [] else FAIL, 'patch signature failures in game: %s' % g(r, 'audit', 'patchSignatureFailures'))
        with open(os.path.join(REPO, 'docs/runbooks/player-worlds.md'), encoding='utf-8') as f:
            doc = f.read()
        docs = Verdict(PASS if 'Game updates' in doc and 'Sandbox' in doc else FAIL, 'player-worlds.md states the update behaviour and the Sandbox-only scope')
        return combine([('offline', offline), ('in game', sig), ('docs', docs)])

    def install(self):
        st = self.run.state
        phases = st.get('phases') or {}
        if not st.get('launches') and not phases.get('deploy'):
            return Verdict(NOT_RUN, 'the run has not touched the install yet')
        restore = phases.get('restore') or {}
        parts = [('claim', Verdict(PASS, st['claim']['text']) if st.get('claim') else Verdict(FAIL, 'no CLAIM recorded')),
                 ('lock', Verdict(PASS if g(st, 'lock', 'released') else (NOT_RUN if not st.get('lock') else FAIL), 'held %s, released %s' % (g(st, 'lock', 'acquired'), g(st, 'lock', 'released'))))]
        hashes = st.get('hashes') or []
        pairs = {(h.get('TaleWorlds.Native.dll'), h.get('TaleWorlds.MountAndBlade.dll')) for h in hashes}
        parts.append(('native hashes', Verdict(PASS if hashes and len(pairs) == 1 else (NOT_RUN if not hashes else FAIL), '%d records: %s' % (len(hashes), sorted(pairs)))))
        if restore.get('status') is None:
            parts.append(('install as found', Verdict(NOT_RUN, 'the restore phase did not run')))
        else:
            d = restore.get('detail') or {}
            ok = restore.get('status') == 'done' and g(d, 'module', 'identical') and not g(d, 'saves', 'preexistingChanged') and d.get('content') in ('absent', 'removed')
            parts.append(('install as found', Verdict(PASS if ok else FAIL, 'module identical %s, pre-existing saves changed %s, synthetic module %s, pre-existing scenes %s' % (
                g(d, 'module', 'identical'), g(d, 'saves', 'preexistingChanged'), d.get('content'), g(d, 'module', 'preexistingScenes')))))
        return combine(parts)

    def c1(self):
        if not self.ran('world1'):
            return Verdict(NOT_RUN, 'world 1 did not run')
        if not self.ba:
            return Verdict(BLOCKED, "world 1's bundle was not kept with the run")
        built = self.run.build_outputs('world1') or {}
        policies = built.get('embeddedPolicies') or {}
        repo_sha = sha(os.path.join(REPO, 'worldgen/policies/terrain-materials.json'))
        bundle_sha = sha(os.path.join(self.ba.folder, 'policies/terrain-materials.json')) if self.ba and os.path.exists(os.path.join(self.ba.folder, 'policies/terrain-materials.json')) else None
        embedded = policies.get('MNBL.ProceduralWorld.terrain-materials.json')
        table = Verdict(PASS if bundle_sha == repo_sha and (embedded is None or embedded == repo_sha) else FAIL, 'terrain-materials.json repo %s, bundle %s, embedded %s' % (repo_sha[:12], (bundle_sha or '-')[:12], (embedded or 'not embedded')[:12]))
        _, faces = self.biomes()
        battles = self.face_battles()
        cast = self.cast()
        return combine([('one table', table), ('faces', faces), ('battle classes', battles), ('mutation remap', cast[1])])

    def c2(self):
        r, v = self.need('world1', 'w1.identity')
        if v:
            return v
        flora = g(r, 'audit', 'flora', 'instances')
        magic = g(r, 'audit', 'magic', 'drawn')
        size = None
        if self.ba and self.ba.masks:
            size = struct.unpack_from('<I', self.ba.masks, 8)[0]
        return Verdict(PASS if (flora or 0) > 0 and (magic or 0) > 0 and size == 513 else FAIL,
                       'map trees %s (go), magic ground by decals %s (delivery A), terrain %s samples (513 go)' % (flora, magic, size))

    def grade(self):
        S = self.steps_out
        def step(sid, text, verdict):
            S.append(dict(verdict.record(), id=sid, text=text))
            return verdict
        prep = step('1', 'Prepare: CLAIM, process check, one build, native DLL hashes, vanilla War Sails baseline', self.prepare())
        gen = step('2.generate', 'World 1 (seed 42) from the New Campaign screen: the preview wait estimate, the timed cache build', self.generate())
        biome_shots, face_agree = self.biomes()
        step('2.biomes', 'Biome looks: a screenshot per biome (map trees where the probe said go)', biome_shots)
        relief = step('2.relief', 'Smooth terrain: mountains and coasts, pads, the camera stops at the pole lines', self.relief())
        water1 = step('2.water', 'Water: rivers drawn and every lake visible', self.water('world1', 'w1.water'))
        speed = self.speed(); battles = self.face_battles()
        step('2.faces', 'Face types: the speed tooltip on each, Snow and Forest field battles, a battle beside a non-port settlement, a map conversation',
             combine([('faces follow biomes', face_agree), ('speed tooltip', speed), ('battles and conversation', battles)]))
        seam = step('2.seam', 'A seam crossing where the biome differs', self.seam())
        reach, drawn = self.roads()
        step('2.roads', 'Roads, feeder roads, bridges and ruin markers, the ruins encyclopedia page; no drawn road enters water without a deck', combine([('reach', reach), ('drawn', drawn)]))
        looks = step('2.looks', 'Culture looks (towns, castles, villages), siege state, the scene you enter', self.looks())
        vil1 = step('2.villages', '3/2 villages and ports, no village crowding a fortification', self.villages('world1', 'A'))
        names1 = step('2.names', 'Names: no Age tags or numerals in the encyclopedia lists; regents have personal names', self.names('world1'))
        census1, children1 = self.census_match('world1', 'A')
        step('2.census', 'Day-0 population census', combine([('as compiled', census1), ('engine children', children1)]))
        parity = step('2.parity', 'Vanilla parity: culture incidents, terrain incident predicates, hideout bandits, a wanderer backstory', self.parity())
        perf = step('2.performance', 'fps and memory against the baseline', self.performance())
        magic_visible, packs = self.magic()
        step('2.magic', 'Generated magic ground (visual only)', magic_visible)
        winter, route = self.winter()
        step('2.winter', 'Advance to winter: seasonal snow, land ice white all year (MMR-256: the named-texture route or the permanentWhite fallback)', winter)
        c3, zone, zone_battle = self.cast()
        step('2.cast', 'Debug-cast fire at Nerdun: the background apply, the zone appears, face types and a battle inside change', combine([('apply', c3), ('zone', zone), ('battle', zone_battle)]))
        cont = step('2.continue', 'Save, then Continue: no generator process at load, caches reused, the mutation persists', self.continued())
        soak = step('3', 'Soak 30+ days and the census again', self.soak())
        econ = self.economy()
        S[-1]['economy'] = econ.record()
        w2 = step('4', 'World 2 (seed 73): Town count = sites, its day-0 census; then Continue world 1 (scene and caches in place)', self.world2())
        mods = step('5', 'Mods: the synthetic content module, setup and world 1 load, then removed', self.mods_check())
        step('6', 'Fix and confirm (one fix batch with the balance pass, one confirming run)', Verdict(NOT_RUN, 'a later stage: the fix batch and the confirming run reuse this harness (docs/map-epic-2-acceptance.md)'))
        step('7', 'Hand off: the owner reviews textures, looks and terrain against the concept art; findings section 13 updated', self.attested('handoff', 'the owner review and the findings update'))

        C = self.criteria_out
        names2 = self.names('world2')
        C['c1'] = self.c1()
        C['c2'] = self.c2()
        C['c3'] = c3
        C['c4'] = combine([('world 1', self.towns_sites('world1', 'A')), ('world 2', self.towns_sites('world2', 'B'))])
        C['c5'] = combine([('world 1', vil1), ('world 2', self.villages('world2', 'B'))])
        C['c6'] = reach
        C['c7'] = combine([('faces follow biomes', face_agree), ('speed tooltip', speed), ('battle beside a settlement', battles)])
        trees = self.c2()
        C['c8'] = combine([('biome screenshots', biome_shots), ('map trees', trees), ('land ice and winter', winter)])
        C['c9'] = winter
        C['c10'] = drawn
        C['c11'] = looks
        C['c12'] = combine([('rendered from the published packs', packs), ('owner review', self.attested('c12', 'the owner review of the magic textures'))])
        C['c13'] = combine([('generated variants', magic_visible), ('live mutation', zone), ('battle in the zone', zone_battle)])
        C['c14'] = combine([('cast', zone), ('Continue', cont)])
        C['c15'] = self.attested('c15', "MMR-262's review pass findings fixed or deferred")
        C['c16'] = self.install()
        C['c17'] = relief
        m2, ch2 = self.census_match('world2', 'B')
        C['c18'] = combine([('world 1', census1), ('world 1 children', children1), ('world 2', m2), ('world 2 children', ch2)])
        C['c19'] = combine([('world 1', self.compiled_flags('world1', 'A')), ('world 2', self.compiled_flags('world2', 'B'))])
        C['c20'] = soak
        C['c21'] = combine([('world 2 and Continue world 1', w2), ('estimates', self.estimates()), ('missing-cache warning', self.missing_cache())])
        C['c22'] = mods
        C['c23'] = self.game_updates()
        C['c24'] = combine([('world 1', water1), ('world 2', self.water('world2', 'w2.water'))])
        C['c25'] = combine([('world 1', names1), ('world 2', names2)])
        C['c26'] = parity
        C['c27'] = perf
        C['c28'] = econ
        C['c29'] = self.veins_look()
        C['c30'] = self.features()
        C['c31'] = self.magic_water()
        C['c32'] = self.no_freeze()
        C['c33'] = self.overlay_meshes()
        return route

    def snow_route_in_force(self):
        """The snow route the run's build took (acc5 moved the policy to permanentWhite): permanentWhite when world 1's identity records a snow
        texture naming none (the scene names no texture), namedTexture when it names one, None without the record."""
        tex = g(self.w1.get('w1.identity') or {}, 'support', 'climateVisuals', 'snowTexture')
        if not isinstance(tex, dict) or not tex:
            return None
        return 'namedTexture' if (tex.get('named') or tex.get('name')) else 'permanentWhite'

    def snow_texture(self):
        """hang-5 / fails-11: the named snow texture route's own evidence before any season shot: the identity's snow texture record (does the
        engine resolve the runtime texture by name) and the engine logs' 'Cannot find texture: mnbl_snow_' lines."""
        r = self.w1.get('w1.identity') or {}
        tex = g(r, 'support', 'climateVisuals', 'snowTexture') or {}
        rows = ['snow texture %s: status %s, engine name %s, resolves by name %s' % (tex.get('named') or tex.get('name'), tex.get('status'), tex.get('engineName'), tex.get('resolvesByName'))] if tex else []
        logs = os.path.join(self.run.folder, 'logs')
        for name in sorted(os.listdir(logs)) if os.path.isdir(logs) else []:
            if not re.match(r'rgl_log_\d+\.txt$', name):
                continue
            hits, first = 0, None
            with open(os.path.join(logs, name), encoding='utf-8', errors='replace') as f:
                for i, line in enumerate(f, 1):
                    if 'Cannot find texture: mnbl_snow_' in line:
                        hits += 1
                        first = first or (i, line.strip()[:120])
            if hits:
                rows.append('%s: %d "Cannot find texture: mnbl_snow_" lines (first at line %d: %s)' % (name, hits, first[0], first[1]))
        return rows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--run', required=True)
    ap.add_argument('--out', default=os.path.join(REPO, 'evidence/map-epic-2/acceptance.json'))
    ap.add_argument('--attest', action='append', help='an attestations file (repeatable; later files replace earlier keys); default: the committed '
                    'evidence/map-epic-2/attestations.json, then <run>/attestations.json')
    ap.add_argument('--balance', help='the balance pass review (default: evidence/map-epic-2/balance-pass.json)')
    ap.add_argument('--builds-from', action='append', help='a run folder whose cache builds a reused start may be graded on (repeatable; default: the sibling run folders)')
    a = ap.parse_args()
    attest_paths = [p for p in (a.attest if a.attest is not None else [ATTESTATIONS, os.path.join(a.run, 'attestations.json')]) if p and os.path.exists(p)]
    attest = {}
    for p in attest_paths:
        attest.update({k: v for k, v in load(p).items() if isinstance(v, dict)})
    run = Run(a.run, attest, a.builds_from, a.balance)
    grader = Grader(run)
    route = grader.grade()
    steps = grader.steps_out
    criteria = [dict(v.record(), id=k, text=CRITERIA[k]) for k, v in sorted(grader.criteria_out.items(), key=lambda kv: int(kv[0][1:]))]
    # hang-4: a hung launch is named where its phase is reported, and every rule its hang left unrun says so.
    hangs = grader.hangs
    phases = json.loads(json.dumps(run.state.get('phases') or {}))
    names = {'world1': 'world 1', 'world2': 'world 2', 'baseline': 'the baseline', 'mods': 'the mods launch'}
    for h in hangs:
        where = {'stopped': h['stopped'], 'killed': h['killed'], 'pid': h['pid'], 'hungStep': '%s / %s' % (h['hungStep'], h['lastStage']), 'lastStageUtc': h['lastStageUtc'],
                 'lastEngineLogLine': h['lastEngineLogLine']}
        p = phases.setdefault(h['phase'], {'status': None, 'detail': None})
        if not p.get('detail'):
            p['detail'] = where
        elif isinstance(p['detail'], dict) and 'hungStep' not in p['detail']:
            p['detail'].setdefault('hangs', []).append(where)
    # MMR-247 X: a crashed launch likewise (its fault event with it).
    crashes = grader.crashes
    for c in crashes:
        where = {'crashedStep': '%s / %s' % (c['step'], c['lastStage']), 'pid': c['pid'], 'fault': c['fault'], 'lastStageUtc': c['lastStageUtc'], 'lastEngineLogLine': c['lastEngineLogLine']}
        p = phases.setdefault(c['phase'], {'status': None, 'detail': None})
        if not p.get('detail'):
            p['detail'] = where
        elif isinstance(p['detail'], dict):
            p['detail'].setdefault('crashes', []).append(where)
    if hangs or crashes:
        cause = 'not run: ' + '; '.join(['%s hung at %s (%s)' % (names.get(h['phase'], h['phase']), h['hungStep'], h['lastStage']) for h in hangs] +
                                        ['%s crashed at %s (%s; %s)' % (names.get(c['phase'], c['phase']), c['step'], c['lastStage'], c['fault']) for c in crashes])
        for r in steps + criteria:
            if r['status'] == NOT_RUN and any('the step did not run' in d for d in r['detail']):
                r['detail'] = [cause] + r['detail']
    tally = {}
    for r in steps + criteria:
        tally[r['status']] = tally.get(r['status'], 0) + 1
    out = {'schema': 'mnbl.map2-acceptance', 'version': 1, 'ticket': 'MMR-247', 'run': run.state.get('runId'), 'runFolder': os.path.relpath(a.run, REPO).replace('\\', '/'),
           'attestations': [os.path.relpath(p, REPO).replace('\\', '/') for p in attest_paths] or None, 'phases': phases, 'claim': run.state.get('claim'), 'nativeHashes': run.state.get('hashes'),
           'snowRoute': {'decision': ('permanentWhite (in force): ' + {PASS: 'confirmed', FAIL: 'fails'}.get(route.status, 'undecided')) if grader.snow_route_in_force() == 'permanentWhite' else
                         ('namedTexture' if route.status == PASS else ('permanentWhite fallback (rebuild with precipitation.snowTexture.route = permanentWhite)' if route.status == FAIL else 'undecided')),
                         'detail': route.detail + grader.snow_texture()},
           'steps': steps, 'criteria': criteria, 'tally': tally, 'hangs': hangs, 'crashes': crashes,
           'screenshots': [s for s in run.shots()]}
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(out, f, indent=1, sort_keys=False)
        f.write('\n')
    print('%s: %s%s%s' % (a.out, ', '.join('%s %d' % kv for kv in sorted(tally.items())),
                          '; hangs: ' + ', '.join('%s %s at %s' % (h['phase'], h['pid'], h['hungStep']) for h in hangs) if hangs else '',
                          '; crashes: ' + ', '.join('%s %s at %s (%s)' % (c['phase'], c['pid'], c['step'], c['fault']) for c in crashes) if crashes else ''))
    return 0


if __name__ == '__main__':
    sys.exit(main())
