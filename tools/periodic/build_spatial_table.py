#!/usr/bin/env python3
"""MMR-65: classify every planar spatial call site in the installed campaign assemblies and emit the consumer table.

Input: the scan TSV written by tools/periodic/Scan-PlanarSites.ps1 (one row per method and callee: assembly, type::method, parameter
types, kind, callee signature, count, IL size of the method) and its .inputs.tsv (the scanned assemblies with their SHA-256). Output:
  * src/MNBL.ProceduralWorld/CampaignBootstrap/PeriodicSpatialTable.cs - the consumer table PeriodicSpatialQueries patches from
    (every method with its expected call counts; callers of small table methods to recompile; the scanned assemblies);
  * evidence/world/periodic-consumers.tsv - every audited site with its disposition and reason.
The rules are the dispositions below, applied in order; nothing is classified by hand outside them. A native scene call or a Vec3 distance
that no rule classifies stops the generation (fail closed): a new consumer of either kind needs a rule before a table can be written.
"""
from __future__ import annotations

import argparse
import collections
import csv
import sys
from pathlib import Path

KINDS = {
    'TaleWorlds.CampaignSystem.CampaignVec2::Distance(TaleWorlds.CampaignSystem.CampaignVec2)': 'Dcc',
    'TaleWorlds.CampaignSystem.CampaignVec2::Distance(TaleWorlds.Library.Vec2)': 'Dcv',
    'TaleWorlds.CampaignSystem.CampaignVec2::DistanceSquared(TaleWorlds.CampaignSystem.CampaignVec2)': 'Scc',
    'TaleWorlds.CampaignSystem.CampaignVec2::DistanceSquared(TaleWorlds.Library.Vec2)': 'Scv',
    'TaleWorlds.Library.Vec2::Distance(TaleWorlds.Library.Vec2)': 'Dvv',
    'TaleWorlds.Library.Vec2::DistanceSquared(TaleWorlds.Library.Vec2)': 'Svv',
    'TaleWorlds.Library.Vec2::op_Subtraction(TaleWorlds.Library.Vec2,TaleWorlds.Library.Vec2)': 'Sub',
    'TaleWorlds.CampaignSystem.CampaignVec2::op_Subtraction(TaleWorlds.CampaignSystem.CampaignVec2,TaleWorlds.CampaignSystem.CampaignVec2)': 'SubC',
    'TaleWorlds.CampaignSystem.CampaignVec2::op_Subtraction(TaleWorlds.CampaignSystem.CampaignVec2,TaleWorlds.Library.Vec2)': 'SubCV',
    'TaleWorlds.CampaignSystem.CampaignVec2::NearlyEquals(TaleWorlds.CampaignSystem.CampaignVec2,System.Single)': 'Ncc',
    'TaleWorlds.CampaignSystem.CampaignVec2::NearlyEquals(TaleWorlds.Library.Vec2,System.Single)': 'Ncv',
    'TaleWorlds.Library.Vec2::NearlyEquals(TaleWorlds.Library.Vec2,System.Single)': 'Nvv',
}
ORDER = ['Dcc', 'Dcv', 'Scc', 'Scv', 'Dvv', 'Svv', 'Sub', 'SubC', 'SubCV', 'Ncc', 'Ncv', 'Nvv', 'Grid']
INLINE_LIMIT = 100   # RyuJIT's default IL size limit for inlining (the scanner applies the same limit, or AggressiveInlining)
VIEW_ASSEMBLIES = {'SandBox.View', 'NavalDLC.View', 'TaleWorlds.CampaignSystem.ViewModelCollection'}
MISSION = ('NavalDLC.Missions.', 'NavalDLC.DWA.', 'DWAHelpers', 'TaleWorlds.MountAndBlade.', 'SandBox.Missions.', 'SandBox.Conversation.',
           'SandBox.Objects.', 'StoryMode.Missions.', 'NavalDLC.Storyline.MissionControllers.')
INACTIVE = ('NavalDLC.Storyline.', 'StoryMode.', 'Storymode.', 'NavalDLC.NavalDLCCheats')
COVERED = {
    'TaleWorlds.CampaignSystem.GameComponents.DefaultMapDistanceModel::GetDistance': 'MMR-63 distance model legs (PeriodicPatches.DistanceModel)',
    'NavalDLC.GameComponents.NavalDLCMapDistanceModel::GetDistance': 'MMR-63 distance model legs (PeriodicPatches.DistanceModel)',
    'Helpers.DistanceHelper::FindClosestDistanceFromMobilePartyToMobileParty': 'MMR-64 distance helpers',
    'Helpers.DistanceHelper::GetDistanceBetweenMobilePartyToMobileParty': 'MMR-64 distance helpers',
    'Helpers.SettlementHelper::FindNearestSettlementToPoint': 'MMR-64 distance helpers',
    'TaleWorlds.CampaignSystem.Party.PartyBase::TaleWorlds.CampaignSystem.Map.IInteractablePoint.CanPartyInteract': 'MMR-63 encounter range',
    'TaleWorlds.CampaignSystem.Party.PartyBase::CalculateVisibilityRangeOfMapPoint': 'MMR-63 visibility range',
    'Helpers.MenuHelper::EncounterAttackConsequence': 'MMR-63 encounter direction',
    'TaleWorlds.CampaignSystem.MapEvents.MapEvent::GetEventDirection': 'MMR-63 map-event direction',
    'TaleWorlds.CampaignSystem.GameState.MapState::ProcessTravel': 'MMR-63 click translation and direction',
    'NavalDLC.GameComponents.NavalDLCStormModel::GetNormalizedWindStrengthOfStormForPosition': 'MMR-39 GeneratedStormModel measures from the copy nearest the storm',
    'NavalDLC.GameComponents.NavalDLCStormModel::GetPositionDamageForStorm': 'MMR-39 GeneratedStormModel measures from the copy nearest the storm',
}
COVERED_PREFIX = {
    'TaleWorlds.CampaignSystem.Map.DistanceCache.SandBoxNavigationCache/<>c__DisplayClass20_0::<GetClosestSettlementsToPositionInCache>':
        'MMR-64 closest-settlement order lambdas',
}
VIEW_METHODS = {'Helpers.QuestHelper::AddMapArrowFromPointToTarget': 'quest map arrow (a view; MMR-66)'}
# View code the player reads as distances between campaign positions: lists sorted by distance, distance texts, the nearby culture's
# music and the step sounds of the parties around the main party. Transpiled like campaign code, so near the cut their order, text and
# sounds follow the periodic topology (2026-10-19; a documented limit of MMR-66 before). The transpiler measures periodically only
# between campaign positions, as everywhere. Not CampaignUIHelper::GetHeroBehaviorText: patching it when the periodic set is applied (before
# the campaign's game texts exist) runs CampaignUIHelper's type initializer, which reads GameTexts and fails, and the failed type then
# breaks character creation (one-shot run, 2026-10-19); the hero behaviour text stays planar near the cut.
VIEW_PERIODIC = {
    'TaleWorlds.CampaignSystem.ViewModelCollection.ClanManagement.Categories.ClanIncomeSortControllerVM/AlleyItemLocationComparer::GetDistanceToMainParty': 'clan income list sorted by distance (alleys)',
    'TaleWorlds.CampaignSystem.ViewModelCollection.ClanManagement.Categories.ClanIncomeSortControllerVM/WorkshopItemLocationComparer::GetDistanceToMainParty': 'clan income list sorted by distance (workshops)',
    'TaleWorlds.CampaignSystem.ViewModelCollection.ClanManagement.Categories.ClanMembersSortControllerVM/ItemLocationComparer::GetDistanceToMainHero': 'clan members sorted by distance',
    'TaleWorlds.CampaignSystem.ViewModelCollection.ClanManagement.Categories.ClanPartiesSortControllerVM/ItemLocationComparer::GetDistanceToMainParty': 'clan parties sorted by distance',
    'TaleWorlds.CampaignSystem.ViewModelCollection.GameMenu.Overlay.ArmyMenuOverlayVM::ExecuteOnSetAsActiveContextMenuItem': 'army overlay: a party near enough to join',
    'TaleWorlds.CampaignSystem.ViewModelCollection.GameMenu.Overlay.GameMenuPartyItemVM::RefreshProperties': 'game-menu party list distance',
    'SandBox.View.CampaignMusicHandler::GetNearbyCulture': 'the nearby culture\'s map music',
    'SandBox.View.Map.MapScreen::TickStepSounds': 'step sounds of the parties around the main party',
}
GENERIC = ('TaleWorlds.CampaignSystem.Map.LocatorGrid`1::', 'TaleWorlds.CampaignSystem.Map.DistanceCache.NavigationCache`1::')
GENERIC_REASON = {
    'TaleWorlds.CampaignSystem.Map.LocatorGrid`1::': 'the spatial index itself (generic, Harmony refuses it); its planar filter is answered by the image pass',
    'TaleWorlds.CampaignSystem.Map.DistanceCache.NavigationCache`1::': 'generic (Harmony refuses it); MMR-64: GetClosestSettlementToPosition useEarlyOut branch never runs, GetLandRatioOfPath exact on a continuous path',
}
WRAPPERS = ('TaleWorlds.CampaignSystem.Party.MobileParty::FindNextLocatable', 'TaleWorlds.CampaignSystem.Party.MobileParty::StartFindingLocatablesAroundPosition',
            'TaleWorlds.CampaignSystem.Settlements.Settlement::FindNextLocatable', 'TaleWorlds.CampaignSystem.Settlements.Settlement::StartFindingLocatablesAroundPosition')
# Native scene calls and Vec3 distances (audited so that none bypasses the campaign's map scene): the campaign reaches its map scene only
# through IMapScene, which GeneratedMapScene re-implements, or inside SandBox.MapScene, the implementation it derives from; every other
# scene or Vec3 consumer below acts on a mission scene or is cosmetic.
MAP_SCENE = 'TaleWorlds.CampaignSystem.Map.IMapScene::'
MAP_SCENE_REASON = ('through IMapScene, which GeneratedMapScene re-implements (MMR-63): path queries over the cheapest placement; point and '
                    'local queries native, the strips beyond the cut holding the twins')
MAP_SCENE_IMPLEMENTATION = ('SandBox.MapScene::', 'the IMapScene implementation GeneratedMapScene derives from (reached through GeneratedMapScene)')
MISSION_SCENE = ('SandBox.AgentNavigator::', 'SandBox.AI.', 'SandBox.BoardGames.', 'SandBox.Tournaments.', 'SandBox.Issues.IssueQuestTasks.',
                 'SandBox.SandBoxHelpers/MissionHelper::', 'SandBox.GameComponents.SandboxAgentStatCalculateModel::',
                 'SandBox.GameComponents.SandboxApplyWeatherEffectsModel::', 'SandBox.CampaignBehaviors.BoardGameCampaignBehavior::',
                 'BurningSoundNode::', 'ShipBurningSystem::', 'NavalDLC.MissionObjects.',
                 'SandBox.CampaignBehaviors.ClanMemberRolesCampaignBehavior::SelectTarget', 'SandBox.CampaignBehaviors.CommonVillagersCampaignBehavior::heroes_to_look_for',
                 'SandBox.CampaignBehaviors.GuardsCampaignBehavior::IsCastleGuard', 'SandBox.Issues.FamilyFeudIssueBehavior/FamilyFeudIssueQuest::<StartFightWithNotableGang>',
                 'SandBox.Issues.ProdigalSonIssueBehavior/ProdigalSonIssueQuest::HouseFightFinished', 'SandBox.Issues.ProdigalSonIssueBehavior/ProdigalSonIssueQuest::SelectPlayerAsTarget')
COSMETIC_SCENE = {'TaleWorlds.CampaignSystem.AtmosphereGrid': 'atmosphere interpolation (sky and fog for the map camera and a mission\'s look at a map position; cosmetic, MMR-66)'}


def disposition(assembly, method):
    if method.startswith('TaleWorlds.CampaignSystem.CampaignVec2::'):
        return 'exact', 'the planar measure itself'
    if method in VIEW_PERIODIC:
        return 'periodic', 'transpiled (PeriodicSpatialQueries): a view read as campaign distances, ' + VIEW_PERIODIC[method]
    if assembly in VIEW_ASSEMBLIES:
        return 'view', 'view code (camera, UI; MMR-66)'
    if method in VIEW_METHODS:
        return 'view', VIEW_METHODS[method]
    for prefix in MISSION:
        if method.startswith(prefix):
            return 'mission', 'mission-scene coordinates, not campaign positions'
    for prefix in INACTIVE:
        if method.startswith(prefix):
            return 'inactive', 'inactive on a generated world (War Sails storyline, story mode, cheats)'
    if method in COVERED:
        return 'covered', COVERED[method]
    for prefix, reason in COVERED_PREFIX.items():
        if method.startswith(prefix):
            return 'covered', reason
    for prefix in GENERIC:
        if method.startswith(prefix):
            return 'generic', GENERIC_REASON[prefix]
    if method in WRAPPERS:
        return 'covered', 'the locator wrapper (image pass)'
    return 'periodic', 'transpiled (PeriodicSpatialQueries)'


def scene_disposition(assembly, method, kind, callee):
    """Native scene calls and Vec3 distances: (disposition, reason), or None when no rule classifies the site."""
    disp, reason = disposition(assembly, method)
    if disp in ('view', 'mission', 'inactive'):
        return disp, reason
    if kind == 'native' and callee.startswith(MAP_SCENE):
        return 'covered', MAP_SCENE_REASON
    if method.startswith(MAP_SCENE_IMPLEMENTATION[0]):
        return 'covered', MAP_SCENE_IMPLEMENTATION[1]
    if any(method.startswith(p) for p in MISSION_SCENE):
        return 'mission', 'acts on a mission scene (agents, board games, tournaments, mission weather), not the campaign map'
    owner = method.split('::', 1)[0].split('/', 1)[0]
    if owner in COSMETIC_SCENE:
        return 'view', COSMETIC_SCENE[owner]
    return None


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--scan', required=True)
    ap.add_argument('--cs', required=True)
    ap.add_argument('--tsv', required=True)
    args = ap.parse_args()
    methods = collections.OrderedDict()
    sizes = {}
    callers = collections.defaultdict(list)   # callee (type::method(params)) -> [(assembly, method, params, count, size)]
    audit = []
    unclassified = []
    with open(args.scan, encoding='utf-8-sig') as f:
        for row in csv.reader(f, delimiter='\t'):
            assembly, method, params, kind, callee, count, size = row
            count, size = int(count), int(size)
            sizes[(assembly, method, params)] = size
            if kind == 'caller':
                callers[callee].append((assembly, method, params, count, size))
                continue
            if kind in ('native', 'vec3'):
                verdict = scene_disposition(assembly, method, kind, callee)
                if verdict is None:
                    unclassified.append('%s %s %s' % (kind, method, callee))
                    continue
                audit.append([assembly, method, params, kind, callee, count, verdict[0], verdict[1]])
                continue
            disp, reason = disposition(assembly, method)
            if kind in ('pointAround', 'locatorWrapper'):
                target = 'canonical result (PeriodicPatches postfix)' if kind == 'pointAround' else 'image pass (patched wrapper)'
                audit.append([assembly, method, params, kind, callee, count, 'callee-patched' if disp in ('periodic', 'covered') else disp, target if disp in ('periodic', 'covered') else reason])
                continue
            if kind == 'locator' and '::FindNextLocatable(' not in callee:
                # The search start stays: the wrapped FindNextLocatable continues it at the seam image.
                audit.append([assembly, method, params, kind, callee, count, 'covered' if disp == 'periodic' else disp,
                              'search start; its FindNextLocatable calls are wrapped' if disp == 'periodic' else reason])
                continue
            code = 'Grid' if kind == 'locator' else KINDS[callee]
            if disp == 'periodic' and code == 'Grid' and method in WRAPPERS:
                disp, reason = 'covered', 'the locator wrapper (image pass)'
            audit.append([assembly, method, params, kind, callee, count, disp, reason])
            if disp != 'periodic':
                continue
            methods.setdefault((assembly, method, params), collections.Counter())[code] += count
    if unclassified:
        sys.exit('unclassified native scene or Vec3 sites (add a rule):\n  ' + '\n  '.join(sorted(set(unclassified))))
    table = list(methods.items())
    # Callers of small table methods, closed over small callers: patched too, so that no copy of a table method inlined into code compiled
    # before the patch survives (Harmony marks a patched method not inlinable for later compilations).
    def key(assembly, method, params):
        owner, name = method.split('::', 1)
        return '%s::%s(%s)' % (owner, name, params)
    in_table = {k for k, _ in table}
    seen, recompile = set(), collections.OrderedDict()
    frontier = sorted(k for k in in_table if sizes.get(k, INLINE_LIMIT + 1) <= INLINE_LIMIT)   # sorted: the table's order is reproducible
    small_table = sorted(frontier)
    while frontier:
        nxt = []
        for k in frontier:
            for assembly, method, params, count, size in callers.get(key(*k), []):
                caller = (assembly, method, params)
                if caller in seen:
                    continue
                seen.add(caller)
                if caller not in in_table:
                    recompile[caller] = key(*k)
                    audit.append([assembly, method, params, 'caller', key(*k), count, 'recompile',
                                  'calls a small table method (inlining candidate): patched so that no copy compiled before the patch survives'])
                if size <= INLINE_LIMIT:
                    nxt.append(caller)
        frontier = nxt
    lines = []
    for (assembly, method, params), counts in table:
        owner, name = method.split('::', 1)
        values = ', '.join(str(counts[c]) for c in ORDER)
        lines.append('            new Entry("%s", "%s", "%s", "%s", %s),' % (assembly, owner.replace('/', '+'), name, params, values))
    for (assembly, method, params) in recompile:
        owner, name = method.split('::', 1)
        lines.append('            new Entry("%s", "%s", "%s", "%s", %s),' % (assembly, owner.replace('/', '+'), name, params, ', '.join('0' for _ in ORDER)))
    inputs = []
    with open(args.scan + '.inputs.tsv', encoding='utf-8-sig') as f:
        for row in csv.reader(f, delimiter='\t'):
            inputs.append('            new[] { "%s", "%s" },' % (row[0], row[2]))
    cs = '''// Generated by tools/periodic/build_spatial_table.py from tools/periodic/Scan-PlanarSites.ps1 over the installed v1.4.8.119303 /
// NavalDLC v1.2.8 assemblies; do not edit by hand. MMR-65: every campaign method whose planar distance, position difference, near-equality
// or direct LocatorGrid search PeriodicSpatialQueries makes periodic, with the exact number of each call it must find, then the callers of
// its small methods (no counted call: recompiled), and the scanned assemblies (the audit of every site, with the dispositions of the rest,
// is evidence/world/periodic-consumers.tsv).
namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    internal static partial class PeriodicSpatialQueries
    {
        internal sealed class Entry
        {
            public readonly string Assembly, Type, Method, Parameters;
            public readonly int[] Counts;
            public Entry(string assembly, string type, string method, string parameters, params int[] counts)
            {
                Assembly = assembly; Type = type; Method = method; Parameters = parameters; Counts = counts;
            }
            // Every counted call is replaced.
            public int Replaced { get { int n = 0; for (int i = 0; i < Counts.Length; i++) n += Counts[i]; return n; } }
            // A caller of a small table method, patched only so that it is compiled again after its callee's patch.
            public bool Recompile { get { return Replaced == 0; } }
        }

        // Counts: CampaignVec2.Distance(CampaignVec2), CampaignVec2.Distance(Vec2), CampaignVec2.DistanceSquared(CampaignVec2),
        // CampaignVec2.DistanceSquared(Vec2), Vec2.Distance, Vec2.DistanceSquared, Vec2 - Vec2, CampaignVec2 - CampaignVec2, CampaignVec2 - Vec2,
        // CampaignVec2.NearlyEquals(CampaignVec2), CampaignVec2.NearlyEquals(Vec2), Vec2.NearlyEquals, direct LocatorGrid.FindNextLocatable.
        internal static readonly Entry[] Table = {
%s
        };

        // The assemblies the audit scanned, with their SHA-256 (the offline check compares them with the installed files).
        internal static readonly string[][] ScannedAssemblies = {
%s
        };
    }
}
''' % ('\n'.join(lines), '\n'.join(inputs))
    Path(args.cs).write_text(cs, encoding='utf-8', newline='\n')
    with open(args.tsv, 'w', encoding='utf-8', newline='\n') as f:
        w = csv.writer(f, delimiter='\t', lineterminator='\n')
        w.writerow(['assembly', 'method', 'parameters', 'kind', 'callee', 'count', 'disposition', 'reason'])
        for row in sorted(audit, key=lambda r: (r[6], r[0], r[1], r[4])):
            w.writerow(row)
    summary = collections.Counter(r[6] for r in audit)
    print('methods patched', len(table), 'recompiled', len(recompile), 'small table methods', len(small_table), 'sites by disposition', dict(summary))
    print('small table methods:', '; '.join('%s(%s)' % (k[1], k[2]) for k in small_table))


if __name__ == '__main__':
    main()
