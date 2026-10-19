using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.CampaignSystem.Map.DistanceCache;
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
    //    finds the canonical objects across the seam. (LocatorGrid<T> itself cannot be patched: Harmony rejects its generic by-ref
    //    wrapper.) MMR-65: an image pass is marked by its search position (a thread-local ring, MarkImage), not by a non-canonical
    //    centre, so a search centred in a strip (a party or storm between its move and its rebase) also continues at its canonical
    //    copy; the direct LocatorGrid callers get the same pass (PeriodicSpatialQueries).
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
    //    MMR-65: FindPointAroundPosition (bandit, pirate and fishing spawns, naval patrol points) and both FindPointInsideArea
    //    overloads get the same postfix.
    //  * MMR-65 spatial consumers: PeriodicSpatialQueries patches every audited campaign method's planar position measures and direct
    //    LocatorGrid searches from its generated table (PeriodicSpatialTable), with exact call counts.
    //  * Picking: MapScreen's left-click handler hands the picked terrain point to MapState.ProcessTravel (its only caller).
    //    A click on a duplicate strip carries display coordinates; the prefix translates them to the canonical point
    //    before the order is stored, and the click direction is taken periodically.
    //  * Closest settlement order (MMR-64): NavigationCache<Settlement>.GetClosestSettlementToPosition (the per-face closest
    //    settlement cache behind GetClosestEntranceToFace, and SandBoxNavigationCache.CheckBeingNeighbor) takes candidates in the
    //    order of SandBoxNavigationCache.GetClosestSettlementsToPositionInCache's two lambdas, by planar squared distance. Both
    //    installed callers pass useEarlyOut = false, so every candidate's real path distance (over images, GeneratedMapScene) is
    //    evaluated and the result is the exact nearest in any order; the order sets only the search limits (twice the best so far)
    //    and which of two exactly equal candidates wins. The lambdas take the wrapped geometric distance, so both follow the
    //    periodic metric. The method's useEarlyOut branch (two planar comparisons choosing gate or port, one planar cut-off) never
    //    runs and is not patched: Harmony refuses the generic method ("The given generic instantiation was invalid", run 14048).
    //  * Distance helpers (MMR-64): Helpers.DistanceHelper's party-to-party distance (the bird-fly threshold test and the two straight
    //    legs of its entrance formula) and SettlementHelper.FindNearestSettlementToPoint measure planar distances; they become periodic
    //    (PeriodicDistanceModel.HelperCoverage lists every planar call in both helpers).
    // MMR-64: Apply is all or nothing: when any patch fails, every patch already applied is removed before the error is rethrown,
    // so Harmony never leaves a periodic world partly patched. Preflight applies and removes the whole set before a periodic campaign
    // starts, so a refusal ends the start instead of a running world; a world whose Load-time Apply still fails is frozen
    // (PeriodicSeamBehavior locks campaign time). Every rule comes from PeriodicWorldCoordinates through PeriodicWorld.
    public static class PeriodicPatches
    {
        public const string HarmonyId = "mnbl.proceduralworld.periodic";
        private static Harmony harmony;
        private static int replaced;
        public static readonly List<Dictionary<string, object>> Applied = new List<Dictionary<string, object>>();
        public static string LastError;
        private static int imagePasses, canonicalPicks, canonicalPlacements;   // party and cache ticks run in parallel: counted atomically
        public static int ImagePasses { get { return imagePasses; } }
        public static int CanonicalPicks { get { return canonicalPicks; } }
        public static int CanonicalPlacements { get { return canonicalPlacements; } }

        public static bool IsApplied { get { return harmony != null; } }

        public static void Apply()
        {
            if (harmony != null) return;
            MNBL.ProceduralWorld.CampaignRegistry.GeneratedPatches.RequireInstalledVersion("periodic");   // MMR-34: every patch is version pinned
            harmony = new Harmony(HarmonyId);
            Applied.Clear(); LastError = null; imagePasses = 0; canonicalPicks = 0; canonicalPlacements = 0;
            try { ApplyAll(); }
            catch (Exception error)
            {
                harmony = null;
                Applied.Clear();
                LastError = error.ToString();
                try { UnpatchById(); }
                catch (Exception unpatch) { LastError += Environment.NewLine + "and removing the applied patches failed: " + unpatch; }
                throw;
            }
        }

        // Applies the whole set and removes it again: a periodic campaign starts only where every patch target accepts its patch, and
        // only for a world whose pole margin the periodic measures can rely on (MMR-65).
        public static void Preflight(PeriodicWorld world)
        {
            PeriodicSpatialQueries.RequireMargin(world);
            if (harmony != null) return;   // already applied for a loaded periodic world
            Apply();
            Remove();
        }

        private static void ApplyAll()
        {
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
            ClosestSettlementOrder();
            DistanceHelpers();
            // MMR-65: the remaining random-point helpers return canonical points, and the audited spatial consumers become periodic.
            Postfix(AccessTools.Method(typeof(Helpers.NavigationHelper), nameof(Helpers.NavigationHelper.FindPointAroundPosition),
                    new[] { typeof(CampaignVec2), typeof(MobileParty.NavigationType), typeof(float), typeof(float), typeof(bool), typeof(bool) }), nameof(CanonicalPlacementPostfix),
                "NavigationHelper.FindPointAroundPosition: result -> canonical image");
            Postfix(AccessTools.Method(typeof(Helpers.NavigationHelper), nameof(Helpers.NavigationHelper.FindPointInsideArea),
                    new[] { typeof(Vec2), typeof(Vec2), typeof(MobileParty.NavigationType) }), nameof(CanonicalPlacementPostfix),
                "NavigationHelper.FindPointInsideArea(min, max, type): result -> canonical image");
            Postfix(AccessTools.Method(typeof(Helpers.NavigationHelper), nameof(Helpers.NavigationHelper.FindPointInsideArea),
                    new[] { typeof(Vec2), typeof(Vec2), typeof(CampaignVec2), typeof(MobileParty.NavigationType), typeof(float), typeof(float), typeof(bool) }), nameof(CanonicalPlacementPostfix),
                "NavigationHelper.FindPointInsideArea(min, max, centre, ...): result -> canonical image");
            PeriodicSpatialQueries.ApplyAll(harmony, Applied);
            // MMR-66: the map presentation (camera, visuals, nameplates, settlement display copies).
            MNBL.ProceduralWorld.World.PeriodicMapPresentation.ApplyAll(harmony, Applied);
        }

        // MMR-64: the planar distance calls of the installed distance helpers (PeriodicDistanceModel.HelperCoverage).
        private static void DistanceHelpers()
        {
            var party = typeof(MobileParty); var navigation = typeof(MobileParty.NavigationType); var single = typeof(float).MakeByRefType();
            Transpile(AccessTools.Method(typeof(Helpers.DistanceHelper), "FindClosestDistanceFromMobilePartyToMobileParty", new[] { party, party, navigation, single }),
                "DistanceHelper.FindClosestDistanceFromMobilePartyToMobileParty: bird-fly test CampaignVec2.DistanceSquared -> periodic", nameof(CampaignDistanceSquaredToPeriodic));
            Transpile(AccessTools.Method(typeof(Helpers.DistanceHelper), "GetDistanceBetweenMobilePartyToMobileParty", new[] { party, party, navigation, single }),
                "DistanceHelper.GetDistanceBetweenMobilePartyToMobileParty: straight legs CampaignVec2.Distance -> periodic", nameof(DistanceToPeriodic));
            Transpile(AccessTools.Method(typeof(Helpers.SettlementHelper), nameof(Helpers.SettlementHelper.FindNearestSettlementToPoint)),
                "SettlementHelper.FindNearestSettlementToPoint: CampaignVec2.Distance -> periodic", nameof(DistanceToPeriodic));
        }

        // MMR-64: the closest settlement search's candidate order (SandBoxNavigationCache is not generic, so its lambdas can be patched).
        private static void ClosestSettlementOrder()
        {
            var cacheType = typeof(SandBoxNavigationCache);
            var lambdas = cacheType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                .Where(m => m.Name.StartsWith("<GetClosestSettlementsToPositionInCache>", StringComparison.Ordinal) && m.ReturnType == typeof(float))
                .OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
            if (lambdas.Count != 2) throw new MissingMethodException("Periodic patch target missing: SandBoxNavigationCache.GetClosestSettlementsToPositionInCache's two ordering lambdas (found " + lambdas.Count + ")");
            foreach (var lambda in lambdas)
                Transpile(lambda, "SandBoxNavigationCache.GetClosestSettlementsToPositionInCache " + lambda.Name + ": ordering Vec2.DistanceSquared -> periodic",
                    nameof(Vec2DistanceSquaredToPeriodic));
        }

        public static void Remove()
        {
            if (harmony == null) return;
            harmony = null;
            UnpatchById();
            MNBL.ProceduralWorld.World.PeriodicMapPresentation.Release();
        }

        // Removes every patch owned by HarmonyId, whichever Harmony instance applied it.
        private static void UnpatchById() { new Harmony(HarmonyId).UnpatchAll(HarmonyId); }

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

        private static void Transpile(MethodBase target, string label, string transpiler = nameof(SubtractionToPeriodic))
        {
            if (target == null) throw new MissingMethodException("Periodic patch target missing: " + label);
            int before = replaced;
            harmony.Patch(target, transpiler: new HarmonyMethod(typeof(PeriodicPatches), transpiler));
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

        private static IEnumerable<CodeInstruction> Vec2DistanceSquaredToPeriodic(IEnumerable<CodeInstruction> instructions)
        {
            var planar = AccessTools.Method(typeof(Vec2), nameof(Vec2.DistanceSquared), new[] { typeof(Vec2) });
            var periodic = AccessTools.Method(typeof(PeriodicPatches), nameof(PeriodicVec2DistanceSquared));
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(planar)) { instruction.opcode = OpCodes.Call; instruction.operand = periodic; replaced++; }
                yield return instruction;
            }
        }

        private static IEnumerable<CodeInstruction> CampaignDistanceSquaredToPeriodic(IEnumerable<CodeInstruction> instructions)
        {
            var planar = AccessTools.Method(typeof(CampaignVec2), nameof(CampaignVec2.DistanceSquared), new[] { typeof(CampaignVec2) });
            var periodic = AccessTools.Method(typeof(PeriodicPatches), nameof(PeriodicCampaignDistanceSquared));
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(planar)) { instruction.opcode = OpCodes.Call; instruction.operand = periodic; replaced++; }
                yield return instruction;
            }
        }

        // Same stack shapes as the instance calls they replace (address of a, then b by value).
        public static float PeriodicCampaignDistanceSquared(ref CampaignVec2 a, CampaignVec2 b)
        {
            var world = PeriodicWorld.Current;
            return world == null ? a.DistanceSquared(b) : world.DistanceSquared(a.ToVec2(), b.ToVec2());
        }

        public static float PeriodicVec2DistanceSquared(ref Vec2 a, Vec2 b)
        {
            var world = PeriodicWorld.Current;
            return world == null ? a.DistanceSquared(b) : world.DistanceSquared(a, b);
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

        // a - b with the x component taken the short way around the seam (DeltaX(b, a): an exact half width resolves east, +).
        public static Vec2 PeriodicDelta(Vec2 a, Vec2 b)
        {
            var world = PeriodicWorld.Current;
            return world == null ? a - b : new Vec2(world.DeltaX(b.X, a.X), a.Y - b.Y);
        }

        private static void CanonicalPlacementPostfix(ref CampaignVec2 __result, object[] __args)
        {
            var world = PeriodicWorld.Current;
            if (world == null || float.IsNaN(__result.X) || world.IsCanonical(__result.X)) return;
            // A helper that finds no point returns its centre: that answer is the caller's own position, left as given (callers test the
            // failure by comparing the two).
            foreach (var arg in __args) if (arg is CampaignVec2 && ((CampaignVec2)arg).X == __result.X && ((CampaignVec2)arg).Y == __result.Y) return;
            System.Threading.Interlocked.Increment(ref canonicalPlacements);
            __result = new CampaignVec2(new Vec2(world.Canonical(__result.X), __result.Y), __result.IsOnLand);
        }

        private static void CanonicalTravelPrefix(ref CampaignVec2 __0)
        {
            var world = PeriodicWorld.Current;
            if (world == null || world.IsCanonical(__0.X)) return;
            System.Threading.Interlocked.Increment(ref canonicalPicks);
            __0 = new CampaignVec2(new Vec2(world.Canonical(__0.X), __0.Y), __0.IsOnLand);
        }

        // MMR-65: an image pass is known by its own search data. It is started with the primary radius raised by one ulp (the squares of
        // adjacent floats differ after rounding, so no search the engine starts at the same centre has its RadiusSquared), and its centre,
        // that RadiusSquared and the primary centre's x and RadiusSquared are marked in a per-thread ring. A search that is not an image
        // pass never matches a mark, however old (a party standing where an earlier image pass was centred still gets its own image pass),
        // and a nested search never evicts its caller's mark (32 slots; searches nest a few deep at most).
        private const int ImageSlots = 32, SlotSize = 5;
        [ThreadStatic] private static float[] imageMarks;   // per slot: x, y, radiusSquared, primary x, primary radiusSquared
        [ThreadStatic] private static int imageNext;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct FloatBits { [System.Runtime.InteropServices.FieldOffset(0)] public float F; [System.Runtime.InteropServices.FieldOffset(0)] public int I; }

        // The radius an image pass of a primary search is started with.
        internal static float ImageRadius(float primaryRadiusSquared)
        {
            var bits = new FloatBits { F = (float)Math.Sqrt(primaryRadiusSquared) }; bits.I++;
            return bits.F;
        }

        internal static void MarkImage(Vec2 position, float radiusSquared, Vec2 primaryPosition, float primaryRadiusSquared)
        {
            if (imageMarks == null) { imageMarks = new float[ImageSlots * SlotSize]; for (int i = 0; i < imageMarks.Length; i++) imageMarks[i] = float.NaN; }
            int k = imageNext * SlotSize; imageNext = (imageNext + 1) % ImageSlots;
            imageMarks[k] = position.X; imageMarks[k + 1] = position.Y; imageMarks[k + 2] = radiusSquared; imageMarks[k + 3] = primaryPosition.X; imageMarks[k + 4] = primaryRadiusSquared;
        }

        // The slot of the image pass (position, radiusSquared) this thread started, else -1.
        private static int ImageSlot(Vec2 position, float radiusSquared)
        {
            var marks = imageMarks;
            if (marks == null) return -1;
            for (int k = 0; k < marks.Length; k += SlotSize) if (marks[k] == position.X && marks[k + 1] == position.Y && marks[k + 2] == radiusSquared) return k;
            return -1;
        }

        // For a radius of half the width or more the primary and image circles overlap inside the canonical range: an image-pass result
        // inside the primary circle was found already and is skipped (no installed search is that wide; the rule keeps the no-duplicate
        // contract regardless).
        internal static bool SeenByPrimary(Vec2 position, float radiusSquared, Vec2 found)
        {
            var world = PeriodicWorld.Current;
            if (world == null || radiusSquared < world.Width * world.Width / 4) return false;
            int k = ImageSlot(position, radiusSquared);
            if (k < 0) return false;
            float dx = found.X - imageMarks[k + 3], dy = found.Y - position.Y;
            return dx * dx + dy * dy <= imageMarks[k + 4];
        }

        // The image x an exhausted search must continue at: for a canonical centre within the radius of the seam, its copy across the
        // seam; for a centre in a strip, its canonical copy. None for an image pass, or when the copy's circle misses the canonical range.
        internal static bool TryImage(Vec2 position, float radiusSquared, out float imageX)
        {
            imageX = float.NaN;
            var world = PeriodicWorld.Current;
            if (world == null || ImageSlot(position, radiusSquared) >= 0) return false;
            float r = (float)Math.Sqrt(radiusSquared), x = position.X;
            float candidate = x < world.OriginX ? x + world.Width : x >= world.MaxX ? x - world.Width
                : x - world.OriginX < r ? x + world.Width : world.MaxX - x <= r ? x - world.Width : float.NaN;
            if (float.IsNaN(candidate) || candidate + r < world.OriginX || candidate - r >= world.MaxX) return false;
            imageX = candidate;
            return true;
        }

        // The nested FindNextLocatable calls below run these postfixes again: on the image pass they skip what the primary circle held and
        // stop when it is exhausted (its data is marked).
        private static void PartyNextPostfix(ref LocatableSearchData<MobileParty> data, ref MobileParty __result)
        {
            if (__result != null) { if (SeenByPrimary(data.Position, data.RadiusSquared, __result.GetPosition2D)) __result = MobileParty.FindNextLocatable(ref data); return; }
            float x;
            if (!TryImage(data.Position, data.RadiusSquared, out x)) return;
            System.Threading.Interlocked.Increment(ref imagePasses);
            var primary = data;
            data = MobileParty.StartFindingLocatablesAroundPosition(new Vec2(x, primary.Position.Y), ImageRadius(primary.RadiusSquared));
            MarkImage(data.Position, data.RadiusSquared, primary.Position, primary.RadiusSquared);
            __result = MobileParty.FindNextLocatable(ref data);
        }

        private static void SettlementNextPostfix(ref LocatableSearchData<Settlement> data, ref Settlement __result)
        {
            if (__result != null) { if (SeenByPrimary(data.Position, data.RadiusSquared, __result.GetPosition2D)) __result = Settlement.FindNextLocatable(ref data); return; }
            float x;
            if (!TryImage(data.Position, data.RadiusSquared, out x)) return;
            System.Threading.Interlocked.Increment(ref imagePasses);
            var primary = data;
            data = Settlement.StartFindingLocatablesAroundPosition(new Vec2(x, primary.Position.Y), ImageRadius(primary.RadiusSquared));
            MarkImage(data.Position, data.RadiusSquared, primary.Position, primary.RadiusSquared);
            __result = Settlement.FindNextLocatable(ref data);
        }
    }
}
