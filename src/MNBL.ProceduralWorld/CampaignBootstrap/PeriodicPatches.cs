using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    // MMR-63: the non-virtual planar checks that must see the periodic world (Bannerlord.Harmony; see docs/decisions/002).
    // Every patch is inert when no periodic world is active, so official campaigns behave exactly as before.
    //  * Proximity: game code reaches the party/settlement LocatorGrid through MobileParty/Settlement.FindNextLocatable
    //    (69 + 12 call sites). When a search whose circle crosses the seam is exhausted, the postfix continues it as an
    //    ordinary search at the query position's image (inside the duplicate strip), where the engine's own planar filter
    //    finds the canonical objects across the seam. Parties are canonical, so a non-canonical search position marks the
    //    image pass. (LocatorGrid<T> itself cannot be patched: Harmony rejects its generic by-ref wrapper.)
    //  * Encounter range (PartyBase IInteractablePoint.CanPartyInteract) and visibility range
    //    (PartyBase.CalculateVisibilityRangeOfMapPoint) each measure one planar Vec2 difference; the transpilers swap it
    //    for the periodic difference. The battle's encounter direction (MenuHelper.EncounterAttackConsequence,
    //    PatchEncounterDir) and the map-event visual direction (MapEvent.GetEventDirection) take leader-position
    //    differences the same way.
    //  * Strategic distance: the Default and NavalDLC MapDistanceModels take settlement-to-settlement distances from the
    //    navigation cache (built through the periodic IMapScene) but add planar CampaignVec2.Distance legs for parties
    //    (party to settlement, party to party, party to point: 11 sites each). Those calls become periodic distances.
    //  * Placement: NavigationHelper.FindReachablePointAroundPosition picks a random reachable point for map-event
    //    finalize, encounter-radius teleports, spawns, army dispersal and land patrols. Near the seam that point can fall in
    //    a strip outside any tick (a battle then starts at a non-canonical position); the postfix returns its canonical image.
    //  * Picking: MapScreen's left-click handler hands the picked terrain point to MapState.ProcessTravel (its only caller).
    //    A click on a duplicate strip carries display coordinates; the prefix translates them to the canonical point
    //    before the order is stored, and the click direction is taken periodically.
    public static class PeriodicPatches
    {
        public const string HarmonyId = "mnbl.proceduralworld.periodic";
        private static Harmony harmony;
        private static int replaced;
        public static readonly List<Dictionary<string, object>> Applied = new List<Dictionary<string, object>>();
        public static string LastError;
        public static int ImagePasses { get; private set; }
        public static int CanonicalPicks { get; private set; }
        public static int CanonicalPlacements { get; private set; }

        public static void Apply()
        {
            if (harmony != null) return;
            harmony = new Harmony(HarmonyId);
            Applied.Clear(); LastError = null; ImagePasses = 0; CanonicalPicks = 0; CanonicalPlacements = 0;
            Postfix(AccessTools.Method(typeof(MobileParty), nameof(MobileParty.FindNextLocatable)), nameof(PartyNextPostfix),
                "MobileParty.FindNextLocatable: continue at the seam image");
            Postfix(AccessTools.Method(typeof(Settlement), nameof(Settlement.FindNextLocatable)), nameof(SettlementNextPostfix),
                "Settlement.FindNextLocatable: continue at the seam image");
            Transpile(AccessTools.Method(typeof(PartyBase), "TaleWorlds.CampaignSystem.Map.IInteractablePoint.CanPartyInteract"),
                "PartyBase.CanPartyInteract: Vec2 subtraction -> periodic difference");
            Transpile(AccessTools.Method(typeof(PartyBase), "CalculateVisibilityRangeOfMapPoint"),
                "PartyBase.CalculateVisibilityRangeOfMapPoint: Vec2 subtraction -> periodic difference");
            Transpile(AccessTools.Method(typeof(Helpers.MenuHelper), "EncounterAttackConsequence"),
                "MenuHelper.EncounterAttackConsequence: encounter direction -> periodic difference");
            Transpile(AccessTools.Method(typeof(TaleWorlds.CampaignSystem.MapEvents.MapEvent), "GetEventDirection"),
                "MapEvent.GetEventDirection: leader direction -> periodic difference");
            DistanceModel(typeof(TaleWorlds.CampaignSystem.GameComponents.DefaultMapDistanceModel));
            DistanceModel(AccessTools.TypeByName("NavalDLC.GameComponents.NavalDLCMapDistanceModel"));  // absent without War Sails
            Postfix(AccessTools.Method(typeof(Helpers.NavigationHelper), nameof(Helpers.NavigationHelper.FindReachablePointAroundPosition),
                    new[] { typeof(CampaignVec2), typeof(int[]), typeof(float), typeof(float), typeof(bool) }), nameof(CanonicalPlacementPostfix),
                "NavigationHelper.FindReachablePointAroundPosition: result -> canonical image");
            var travel = AccessTools.Method(typeof(MapState), nameof(MapState.ProcessTravel));
            Prefix(travel, nameof(CanonicalTravelPrefix), "MapState.ProcessTravel: display point -> canonical point");
            Transpile(travel, "MapState.ProcessTravel: click direction -> periodic difference");
        }

        public static void Remove()
        {
            if (harmony == null) return;
            harmony.UnpatchAll(HarmonyId);
            harmony = null;
        }

        private static void Prefix(MethodBase target, string prefix, string label)
        {
            if (target == null) throw new MissingMethodException("Periodic patch target missing: " + label);
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(PeriodicPatches), prefix));
            Applied.Add(new Dictionary<string, object> { ["target"] = target.DeclaringType.FullName + "::" + target.Name, ["patch"] = label, ["kind"] = "prefix" });
        }

        private static void Postfix(MethodBase target, string postfix, string label)
        {
            if (target == null) throw new MissingMethodException("Periodic patch target missing: " + label);
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(PeriodicPatches), postfix));
            Applied.Add(new Dictionary<string, object> { ["target"] = target.DeclaringType.FullName + "::" + target.Name, ["patch"] = label, ["kind"] = "postfix" });
        }

        private static void Transpile(MethodBase target, string label)
        {
            if (target == null) throw new MissingMethodException("Periodic patch target missing: " + label);
            int before = replaced;
            harmony.Patch(target, transpiler: new HarmonyMethod(typeof(PeriodicPatches), nameof(SubtractionToPeriodic)));
            Applied.Add(new Dictionary<string, object> { ["target"] = target.DeclaringType.FullName + "::" + target.Name, ["patch"] = label, ["kind"] = "transpiler", ["replacedCalls"] = replaced - before });
            if (replaced == before) throw new InvalidOperationException("Periodic patch found nothing to replace: " + label);
        }

        private static void DistanceModel(Type model)
        {
            if (model == null) return;
            int before = replaced, overloads = 0;
            foreach (var method in model.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Where(m => m.Name == "GetDistance"))
            {
                harmony.Patch(method, transpiler: new HarmonyMethod(typeof(PeriodicPatches), nameof(DistanceToPeriodic)));
                overloads++;
            }
            Applied.Add(new Dictionary<string, object> { ["target"] = model.FullName + "::GetDistance", ["patch"] = "party legs: CampaignVec2.Distance -> periodic distance",
                ["kind"] = "transpiler", ["overloads"] = overloads, ["replacedCalls"] = replaced - before });
            if (replaced == before) throw new InvalidOperationException("Periodic patch found nothing to replace: " + model.FullName + "::GetDistance");
        }

        private static IEnumerable<CodeInstruction> DistanceToPeriodic(IEnumerable<CodeInstruction> instructions)
        {
            var planar = AccessTools.Method(typeof(CampaignVec2), nameof(CampaignVec2.Distance), new[] { typeof(CampaignVec2) });
            var periodic = AccessTools.Method(typeof(PeriodicPatches), nameof(PeriodicCampaignDistance));
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(planar)) { instruction.opcode = OpCodes.Call; instruction.operand = periodic; replaced++; }
                yield return instruction;
            }
        }

        // Same stack shape as the instance call it replaces (address of a, then b by value).
        public static float PeriodicCampaignDistance(ref CampaignVec2 a, CampaignVec2 b)
        {
            var world = PeriodicWorld.Current;
            return world == null ? a.Distance(b) : world.Distance(a.ToVec2(), b.ToVec2());
        }

        private static IEnumerable<CodeInstruction> SubtractionToPeriodic(IEnumerable<CodeInstruction> instructions)
        {
            var planar = AccessTools.Method(typeof(Vec2), "op_Subtraction", new[] { typeof(Vec2), typeof(Vec2) });
            var periodic = AccessTools.Method(typeof(PeriodicPatches), nameof(PeriodicDelta));
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(planar)) { instruction.opcode = OpCodes.Call; instruction.operand = periodic; replaced++; }
                yield return instruction;
            }
        }

        // a - b with the x component taken the short way around the seam.
        public static Vec2 PeriodicDelta(Vec2 a, Vec2 b)
        {
            var world = PeriodicWorld.Current;
            return world == null ? a - b : new Vec2(-world.DeltaX(a.X, b.X), a.Y - b.Y);
        }

        private static void CanonicalPlacementPostfix(ref CampaignVec2 __result)
        {
            var world = PeriodicWorld.Current;
            if (world == null || float.IsNaN(__result.X) || world.IsCanonical(__result.X)) return;
            CanonicalPlacements++;
            __result = new CampaignVec2(new Vec2(world.Canonical(__result.X), __result.Y), __result.IsOnLand);
        }

        private static void CanonicalTravelPrefix(ref CampaignVec2 __0)
        {
            var world = PeriodicWorld.Current;
            if (world == null || world.IsCanonical(__0.X)) return;
            CanonicalPicks++;
            __0 = new CampaignVec2(new Vec2(world.Canonical(__0.X), __0.Y), __0.IsOnLand);
        }

        // The image x a search circle must also cover, or NaN when it stays inside the canonical range.
        private static float ImageX(PeriodicWorld world, Vec2 position, float radiusSquared)
        {
            if (!world.IsCanonical(position.X)) return float.NaN;  // already the image pass (parties are canonical)
            float r = (float)Math.Sqrt(radiusSquared);
            if (position.X - world.OriginX < r) return position.X + world.Width;
            if (world.MaxX - position.X <= r) return position.X - world.Width;
            return float.NaN;
        }

        private static void PartyNextPostfix(ref LocatableSearchData<MobileParty> data, ref MobileParty __result)
        {
            var world = PeriodicWorld.Current;
            if (__result != null || world == null) return;
            float x = ImageX(world, data.Position, data.RadiusSquared);
            if (float.IsNaN(x)) return;
            ImagePasses++;
            data = MobileParty.StartFindingLocatablesAroundPosition(new Vec2(x, data.Position.Y), (float)Math.Sqrt(data.RadiusSquared));
            __result = MobileParty.FindNextLocatable(ref data);  // the nested postfix sees a non-canonical position and stops
        }

        private static void SettlementNextPostfix(ref LocatableSearchData<Settlement> data, ref Settlement __result)
        {
            var world = PeriodicWorld.Current;
            if (__result != null || world == null) return;
            float x = ImageX(world, data.Position, data.RadiusSquared);
            if (float.IsNaN(x)) return;
            ImagePasses++;
            data = Settlement.StartFindingLocatablesAroundPosition(new Vec2(x, data.Position.Y), (float)Math.Sqrt(data.RadiusSquared));
            __result = Settlement.FindNextLocatable(ref data);
        }
    }
}
