using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Diagnostics;
using MNBL.ProceduralWorld.Persistence;
using MNBL.ProceduralWorld.Surface;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;

namespace MNBL.ProceduralWorld.Automation
{
    // The map presentation across the periodic seam on a generated world (seam_presentation; save=): MMR-66's sections, which ran only on
    // the MMR-63 fixture, on a save of a production-generated world (the one-shot's designated save of world A). The save loads through the
    // normal load entry; the party leaves any settlement and menu; the seam towns are the pair joined by land across the seam with the
    // shortest land route: a town within a quarter width of the east edge of the canonical range and one within a quarter width of the
    // west edge whose land distance (the distance model, Default navigation) is under half the width, so the route crosses the seam
    // (their planar separation is over half the width). Then every MMR-66 section but the wrap probe's mission (copies, follow, pan, moves, picking, tracks,
    // alignment, menu, budget), with the campaign the game runs itself (LoadedCampaignTicker). Ends the game; nothing is saved.
    internal static class SeamPresentationProbe
    {
        internal static IEnumerator Run(ProbeContext context) { return TwoWorldMatrixProbe.Nested(Body(context)); }

        private static IEnumerator Body(ProbeContext context)
        {
            context.Outputs["scope"] = "MMR-66's seam presentation (copies, camera follow and pan, fast moves, picking, tracks, alignment, a town menu across the seam, the wrap budget) on a save of a generated world.";
            var failures = new List<string>(); context.Outputs["failures"] = failures;
            string save = context.Str("save", null);
            var info = MBSaveLoad.GetSaveFileWithName(save);
            if (info == null) throw new InvalidOperationException("No save " + save);
            bool vanilla = false;
            var previous = GeneratedWorldLoader.Current;
            double start = context.Seconds;
            SandBox.SandBoxSaveHelper.TryLoadSave(info, r => vanilla = true, () => { });
            var loader = GeneratedWorldLoader.Current == previous ? null : GeneratedWorldLoader.Current;
            while (loader != null && !loader.Done && context.Seconds - start < 3600) yield return null;
            context.Outputs["load"] = new Dictionary<string, object> { ["save"] = save, ["seconds"] = context.Seconds - start, ["vanillaStarted"] = vanilla, ["loader"] = loader == null ? null : loader.Report };
            if (loader == null || loader.State != GeneratedWorldLoader.Phase.Loaded || vanilla)
                throw new InvalidOperationException("The save did not load through the world loader (" + (loader == null ? "no loader" : loader.State + " " + loader.Refusal + " " + loader.Error) + ")");
            while (!(Game.Current.GameStateManager.ActiveState is TaleWorlds.CampaignSystem.GameState.MapState) && context.Seconds - start < 3700) yield return null;
            Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
            yield return TwoWorldMatrixProbe.Frames(30);
            var world = PeriodicWorld.Current;
            if (world == null) throw new InvalidOperationException("The loaded campaign has no periodic world.");
            var binding = WorldSaveBinding.BindingOf(Campaign.Current.GetCampaignBehavior<WorldIdentityBehavior>());
            string prefix = binding.WorldId + "_";
            context.Outputs["world"] = new Dictionary<string, object> { ["worldId"] = binding.WorldId, ["contentId"] = binding.ContentId, ["width"] = world.Width, ["originX"] = world.OriginX };

            // The party on the map, outside any settlement, encounter or menu.
            var main = MobileParty.MainParty;
            var cleared = new Dictionary<string, object>(); context.Outputs["cleared"] = cleared;
            TwoWorldMatrixProbe.ClearEncounter(cleared);
            if (main.CurrentSettlement != null) { cleared["leftSettlement"] = main.CurrentSettlement.StringId; LeaveSettlementAction.ApplyForParty(main); }
            TwoWorldMatrixProbe.ExitMenu(cleared);
            yield return TwoWorldMatrixProbe.Frames(10);

            // The seam towns.
            var model = Campaign.Current.Models.MapDistanceModel;
            float quarter = world.Width / 4f;
            var towns = Settlement.All.Where(s => s.IsTown && s.StringId.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(s => s.StringId, StringComparer.Ordinal).ToList();
            var east = towns.Where(s => world.MaxX - s.GatePosition.X < quarter).ToList();
            var west = towns.Where(s => s.GatePosition.X - world.OriginX < quarter).ToList();
            var pairs = east.SelectMany(e => west.Select(w => new { East = e, West = w, Periodic = world.Distance(e.GatePosition.ToVec2(), w.GatePosition.ToVec2()) }))
                .Select(p => new { p.East, p.West, p.Periodic, Land = model.GetDistance(p.East, p.West, false, false, MobileParty.NavigationType.Default) })
                .OrderBy(p => float.IsNaN(p.Land) ? float.MaxValue : p.Land).ThenBy(p => p.East.StringId, StringComparer.Ordinal).ThenBy(p => p.West.StringId, StringComparer.Ordinal).ToList();
            var nearest = pairs.Take(5).Select(p => (object)new Dictionary<string, object> { ["east"] = p.East.StringId, ["west"] = p.West.StringId, ["periodic"] = p.Periodic, ["land"] = p.Land }).ToList();
            pairs = pairs.Where(p => p.Land > 0 && !float.IsNaN(p.Land) && p.Land < world.Width / 2).ToList();
            context.Outputs["seamTowns"] = new Dictionary<string, object> { ["eastCandidates"] = east.Count, ["westCandidates"] = west.Count, ["shortestLandRoutes"] = nearest, ["pairs"] = pairs.Count,
                ["chosen"] = pairs.Count == 0 ? null : new Dictionary<string, object> { ["east"] = pairs[0].East.StringId, ["west"] = pairs[0].West.StringId, ["periodic"] = pairs[0].Periodic, ["land"] = pairs[0].Land } };
            if (pairs.Count == 0) throw new InvalidOperationException("No pair of towns joined by land across the seam (" + east.Count + " east, " + west.Count + " west candidates).");

            var report = new Dictionary<string, object>(); context.Outputs["presentation"] = report;
            yield return PeriodicPresentationProbe.Run(context, new LoadedCampaignTicker(), pairs[0].East, pairs[0].West, null, report, PeriodicPresentationProbe.Sections.Where(s => s != "mission").ToList());
            failures.AddRange((List<string>)report["failures"]);
            context.Checkpoint();
            yield return TwoWorldMatrixProbe.EndGame(context);
            context.Outputs["completed"] = true;
            context.Checkpoint();
            if (failures.Count > 0) throw new InvalidOperationException("Seam presentation expectations failed: " + String.Join("; ", failures.Take(12)));
        }
    }
}
