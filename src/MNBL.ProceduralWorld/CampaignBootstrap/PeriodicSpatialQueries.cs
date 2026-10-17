using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    // MMR-65: the planar spatial consumers outside the distance surface (docs/periodic-movement.md). tools/periodic/Scan-PlanarSites.ps1
    // audits every campaign method of the installed assemblies for planar position measures (CampaignVec2/Vec2 Distance, DistanceSquared,
    // subtraction, NearlyEquals), direct LocatorGrid searches, native scene calls and Vec3 distances; tools/periodic/build_spatial_table.py
    // classifies each site (evidence/world/periodic-consumers.tsv) and writes Table, the methods made periodic, each with the exact number
    // of every call it holds, then the callers of its small methods (recompiled: no inlined planar copy survives a patch).
    //  * One transpiler patches every Table method: each planar measure becomes its periodic counterpart, and each direct
    //    LocatorGrid.FindNextLocatable a wrapper that continues an exhausted search at the seam image. The calls it finds must equal the
    //    table's counts, else the whole periodic set is refused (PeriodicPatches.Apply is all or nothing; a world whose patches failed is
    //    frozen): no required consumer is left planar silently. A later re-run of the transpiler (another patch on the same method) that
    //    finds other counts throws, so that patch fails instead of leaving the method planar.
    //  * A replaced measure is periodic only when both operands are campaign positions: inside the terrain and the logical rows (between
    //    the pole lines, where every campaign object lies). Anything else (an offset, a direction, a velocity) is measured as before, so a
    //    position minus an offset still yields a position. That holds only while every offset is shorter than the pole margin: a world
    //    whose margin is under MaxOffset is refused (RequireMargin).
    internal static partial class PeriodicSpatialQueries
    {
        public static int Patched { get; private set; }
        public static int CallsReplaced { get; private set; }
        public static int GridImagePasses;   // direct grid searches continued at the seam image (probe evidence; the measures themselves are not counted: party and cache ticks run them in parallel)

        // Every offset, direction or velocity a table method subtracts or compares is shorter than this (formation slots, flee and patrol
        // offsets are tens of units); the pole margin must exceed it by the row tolerance, else an offset could pass for a position.
        public const double MaxOffset = 100;

        private static readonly Dictionary<MethodBase, int[]> found = new Dictionary<MethodBase, int[]>();
        private static readonly Dictionary<MethodBase, int[]> expected = new Dictionary<MethodBase, int[]>();
        private static readonly HashSet<MethodBase> recompiled = new HashSet<MethodBase>();
        private static bool applying;
        private static Replacement[] replacements;

        private sealed class Replacement { public MethodInfo[] Planar; public MethodInfo Periodic; }

        // The Table's count columns, in order.
        private static Replacement[] Replacements()
        {
            if (replacements != null) return replacements;
            Func<string, MethodInfo> ours = name => AccessTools.Method(typeof(PeriodicSpatialQueries), name);
            var c = typeof(CampaignVec2); var v = typeof(Vec2);
            replacements = new[] {
                new Replacement { Planar = new[] { AccessTools.Method(c, nameof(CampaignVec2.Distance), new[] { c }) }, Periodic = ours(nameof(CampaignDistance)) },
                new Replacement { Planar = new[] { AccessTools.Method(c, nameof(CampaignVec2.Distance), new[] { v }) }, Periodic = ours(nameof(CampaignDistanceToVec2)) },
                new Replacement { Planar = new[] { AccessTools.Method(c, nameof(CampaignVec2.DistanceSquared), new[] { c }) }, Periodic = ours(nameof(CampaignDistanceSquared)) },
                new Replacement { Planar = new[] { AccessTools.Method(c, nameof(CampaignVec2.DistanceSquared), new[] { v }) }, Periodic = ours(nameof(CampaignDistanceSquaredToVec2)) },
                new Replacement { Planar = new[] { AccessTools.Method(v, nameof(Vec2.Distance), new[] { v }) }, Periodic = ours(nameof(Vec2Distance)) },
                new Replacement { Planar = new[] { AccessTools.Method(v, nameof(Vec2.DistanceSquared), new[] { v }) }, Periodic = ours(nameof(Vec2DistanceSquared)) },
                new Replacement { Planar = new[] { AccessTools.Method(v, "op_Subtraction", new[] { v, v }) }, Periodic = ours(nameof(PositionDifference)) },
                new Replacement { Planar = new[] { AccessTools.Method(c, "op_Subtraction", new[] { c, c }) }, Periodic = ours(nameof(CampaignPositionDifference)) },
                new Replacement { Planar = new[] { AccessTools.Method(c, "op_Subtraction", new[] { c, v }) }, Periodic = ours(nameof(CampaignMinusVec2)) },
                new Replacement { Planar = new[] { AccessTools.Method(c, nameof(CampaignVec2.NearlyEquals), new[] { c, typeof(float) }) }, Periodic = ours(nameof(CampaignNearlyEquals)) },
                new Replacement { Planar = new[] { AccessTools.Method(c, nameof(CampaignVec2.NearlyEquals), new[] { v, typeof(float) }) }, Periodic = ours(nameof(CampaignNearlyEqualsVec2)) },
                new Replacement { Planar = new[] { AccessTools.Method(v, nameof(Vec2.NearlyEquals), new[] { v, typeof(float) }) }, Periodic = ours(nameof(Vec2NearlyEquals)) },
                new Replacement { Planar = new[] { GridNext(typeof(MobileParty)), GridNext(typeof(Settlement)), GridNext(typeof(Track)) }, Periodic = null } };
            var missing = replacements.Select((r, i) => r.Planar.Any(m => m == null) || (i < replacements.Length - 1 && r.Periodic == null) ? i : -1).Where(i => i >= 0).ToList();
            foreach (var t in new[] { typeof(MobileParty), typeof(Settlement), typeof(Track) })
                if (GridType(t).GetMethod("StartFindingLocatablesAroundPosition", Members) == null) missing.Add(replacements.Length - 1);
            if (missing.Count > 0) { replacements = null; throw new MissingMethodException("The planar measures PeriodicSpatialQueries replaces are not the installed methods (table columns " + String.Join(",", missing) + ")."); }
            return replacements;
        }

        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static MethodInfo GridNext(Type t) { return GridType(t).GetMethod("FindNextLocatable", Members); }

        private static MethodInfo GridWrapper(MethodInfo planar)
        {
            var t = planar.DeclaringType.GetGenericArguments()[0];
            return AccessTools.Method(typeof(PeriodicSpatialQueries), t == typeof(MobileParty) ? nameof(GridNextParty) : t == typeof(Settlement) ? nameof(GridNextSettlement) : nameof(GridNextTrack));
        }

        public static int Recompiled { get; private set; }

        // A world whose pole margin is not wider than every offset (plus the row tolerance) cannot tell offsets from positions: refused.
        internal static void RequireMargin(PeriodicWorld world)
        {
            if (world != null && world.Coordinates.OriginY - 1 <= MaxOffset)
                throw new InvalidOperationException("The periodic world's pole margin (" + world.Coordinates.OriginY + ") must exceed " + (MaxOffset + 1) + " units: the periodic measures tell positions from offsets by it.");
        }

        // Every Table method, patched and checked; the caller (PeriodicPatches.ApplyAll) removes the whole set when this throws.
        internal static void ApplyAll(Harmony harmony, List<Dictionary<string, object>> applied)
        {
            RequireMargin(PeriodicWorld.Current);
            Replacements();
            // The grid delegates are built here, not on the first seam search inside a tick: a failure refuses the set.
            Grid<MobileParty>.Touch(); Grid<Settlement>.Touch(); Grid<Track>.Touch();
            found.Clear(); expected.Clear(); recompiled.Clear(); Patched = 0; Recompiled = 0; CallsReplaced = 0; GridImagePasses = 0;
            var transpiler = new HarmonyMethod(typeof(PeriodicSpatialQueries), nameof(Transpiler));
            var mismatches = new List<string>();
            applying = true;
            try
            {
                foreach (var entry in Table)
                {
                    var method = Resolve(entry);
                    if (method == null) throw new MissingMethodException("Periodic spatial consumer missing: " + entry.Type + "::" + entry.Method + "(" + entry.Parameters + ")");
                    if (entry.Recompile) recompiled.Add(method);
                    expected[method] = entry.Counts;
                    harmony.Patch(method, transpiler: transpiler);
                    int[] counts;
                    if (!found.TryGetValue(method, out counts)) throw new InvalidOperationException("The periodic spatial transpiler did not run for " + entry.Type + "::" + entry.Method);
                    if (!counts.SequenceEqual(entry.Counts)) mismatches.Add(entry.Type + "::" + entry.Method + " found [" + String.Join(",", counts) + "], expected [" + String.Join(",", entry.Counts) + "]");
                    if (entry.Recompile) Recompiled++; else { Patched++; CallsReplaced += entry.Replaced; }
                }
            }
            finally { applying = false; }
            if (mismatches.Count > 0) throw new InvalidOperationException("Periodic spatial consumers differ from the audited table (" + mismatches.Count + "): " + String.Join("; ", mismatches));
            applied.Add(new Dictionary<string, object> { ["target"] = Patched + " campaign methods (PeriodicSpatialTable)", ["patch"] = "planar position measures -> periodic between campaign positions; direct LocatorGrid searches -> image pass",
                ["kind"] = "transpiler", ["methods"] = Patched, ["replacedCalls"] = CallsReplaced, ["recompiledCallers"] = Recompiled });
        }

        internal static MethodBase Resolve(Entry entry)
        {
            var type = AccessTools.TypeByName(entry.Type);
            if (type == null) return null;
            return type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                .FirstOrDefault(m => m.Name == entry.Method && String.Join(",", m.GetParameters().Select(p => CecilName(p.ParameterType))) == entry.Parameters);
        }

        // A parameter type as Mono.Cecil spells it (the scan's spelling): nested types with '/', generic arguments in angle brackets.
        internal static string CecilName(Type t)
        {
            if (t.IsByRef) return CecilName(t.GetElementType()) + "&";
            if (t.IsArray) return CecilName(t.GetElementType()) + "[]";
            if (t.IsGenericType && !t.IsGenericTypeDefinition) return CecilName(t.GetGenericTypeDefinition()) + "<" + String.Join(",", t.GetGenericArguments().Select(CecilName)) + ">";
            if (t.IsNested) return CecilName(t.DeclaringType) + "/" + t.Name;
            return String.IsNullOrEmpty(t.Namespace) ? t.Name : t.Namespace + "." + t.Name;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var table = Replacements();
            var counts = new int[table.Length];
            if (recompiled.Contains(original)) { found[original] = counts; return instructions; }   // compiled again, unchanged
            var output = new List<CodeInstruction>();
            foreach (var instruction in instructions)
            {
                for (int k = 0; k < table.Length; k++)
                {
                    var planar = table[k].Planar.FirstOrDefault(m => instruction.Calls(m));
                    if (planar == null) continue;
                    counts[k]++;
                    if (k == table.Length - 1) { instruction.opcode = OpCodes.Call; instruction.operand = GridWrapper(planar); }
                    else if (table[k].Periodic != null) { instruction.opcode = OpCodes.Call; instruction.operand = table[k].Periodic; }
                    break;
                }
                output.Add(instruction);
            }
            int[] audited;
            // A re-run after the set was applied (another patch on this method, which recompiles it) must find the audited calls too.
            if (!applying && expected.TryGetValue(original, out audited) && !counts.SequenceEqual(audited))
                throw new InvalidOperationException("Periodic spatial consumer " + original.DeclaringType.FullName + "::" + original.Name + " changed under another patch: found ["
                    + String.Join(",", counts) + "], audited [" + String.Join(",", audited) + "]; the method would be left planar.");
            found[original] = counts;
            return output;
        }

        // ---- the periodic measures (same stack shapes as the calls they replace: the address of a, then b) ----
        // Both operands must be campaign positions: inside the terrain, and in the logical rows (with one unit of tolerance).
        private static bool Positions(PeriodicWorld world, float ax, float ay, float bx, float by)
        {
            var c = world.Coordinates;
            double low = c.OriginY - 1, high = c.OriginY + c.Height + 1;
            return ax >= 0 && ax <= c.Extent && bx >= 0 && bx <= c.Extent && ay >= low && ay <= high && by >= low && by <= high;
        }

        public static float CampaignDistance(ref CampaignVec2 a, CampaignVec2 b)
        {
            var world = PeriodicWorld.Current;
            return world == null || !Positions(world, a.X, a.Y, b.X, b.Y) ? a.Distance(b) : world.Distance(a.ToVec2(), b.ToVec2());
        }

        public static float CampaignDistanceToVec2(ref CampaignVec2 a, Vec2 b)
        {
            var world = PeriodicWorld.Current;
            return world == null || !Positions(world, a.X, a.Y, b.X, b.Y) ? a.Distance(b) : world.Distance(a.ToVec2(), b);
        }

        public static float CampaignDistanceSquared(ref CampaignVec2 a, CampaignVec2 b)
        {
            var world = PeriodicWorld.Current;
            return world == null || !Positions(world, a.X, a.Y, b.X, b.Y) ? a.DistanceSquared(b) : world.DistanceSquared(a.ToVec2(), b.ToVec2());
        }

        public static float CampaignDistanceSquaredToVec2(ref CampaignVec2 a, Vec2 b)
        {
            var world = PeriodicWorld.Current;
            return world == null || !Positions(world, a.X, a.Y, b.X, b.Y) ? a.DistanceSquared(b) : world.DistanceSquared(a.ToVec2(), b);
        }

        public static float Vec2Distance(ref Vec2 a, Vec2 b)
        {
            var world = PeriodicWorld.Current;
            return world == null || !Positions(world, a.X, a.Y, b.X, b.Y) ? a.Distance(b) : world.Distance(a, b);
        }

        public static float Vec2DistanceSquared(ref Vec2 a, Vec2 b)
        {
            var world = PeriodicWorld.Current;
            return world == null || !Positions(world, a.X, a.Y, b.X, b.Y) ? a.DistanceSquared(b) : world.DistanceSquared(a, b);
        }

        // a - b with x taken the short way around the seam (DeltaX(b, a)) when both are positions.
        public static Vec2 PositionDifference(Vec2 a, Vec2 b)
        {
            var world = PeriodicWorld.Current;
            return world == null || !Positions(world, a.X, a.Y, b.X, b.Y) ? a - b : new Vec2(world.DeltaX(b.X, a.X), a.Y - b.Y);
        }

        public static CampaignVec2 CampaignPositionDifference(CampaignVec2 a, CampaignVec2 b)
        {
            var planar = a - b;
            var world = PeriodicWorld.Current;
            return world == null || !Positions(world, a.X, a.Y, b.X, b.Y) ? planar : new CampaignVec2(new Vec2(world.DeltaX(b.X, a.X), planar.Y), planar.IsOnLand);
        }

        // A position minus a Vec2: an offset (planar, the result is a position) or, in BesiegerCamp.GetSiegeCampPartyPosition, another
        // party's position (periodic).
        public static CampaignVec2 CampaignMinusVec2(CampaignVec2 a, Vec2 b)
        {
            var planar = a - b;
            var world = PeriodicWorld.Current;
            return world == null || !Positions(world, a.X, a.Y, b.X, b.Y) ? planar : new CampaignVec2(new Vec2(world.DeltaX(b.X, a.X), planar.Y), planar.IsOnLand);
        }

        // Vec2.NearlyEquals (|dx| < epsilon and |dy| < epsilon) with dx taken the short way around the seam when both are positions.
        public static bool Vec2NearlyEquals(ref Vec2 a, Vec2 b, float epsilon)
        {
            var world = PeriodicWorld.Current;
            if (world == null || !Positions(world, a.X, a.Y, b.X, b.Y)) return a.NearlyEquals(b, epsilon);
            return Math.Abs(world.DeltaX(b.X, a.X)) < epsilon && Math.Abs(a.Y - b.Y) < epsilon;
        }

        public static bool CampaignNearlyEquals(ref CampaignVec2 a, CampaignVec2 b, float epsilon) { var p = a.ToVec2(); return Vec2NearlyEquals(ref p, b.ToVec2(), epsilon); }
        public static bool CampaignNearlyEqualsVec2(ref CampaignVec2 a, Vec2 b, float epsilon) { var p = a.ToVec2(); return Vec2NearlyEquals(ref p, b, epsilon); }

        // ---- direct LocatorGrid searches (same stack shape as grid.FindNextLocatable(ref data); LocatorGrid<T> is internal, so the grid
        // arrives as an object and is called through delegates built once with visibility checks skipped) ----
        private delegate T NextFn<T>(object grid, ref LocatableSearchData<T> data);
        private delegate LocatableSearchData<T> StartFn<T>(object grid, Vec2 position, float radius);

        private static class Grid<T>
        {
            internal static readonly NextFn<T> Next;
            internal static readonly StartFn<T> Start;
            internal static void Touch() { if (Next == null || Start == null) throw new InvalidOperationException("No LocatorGrid delegates for " + typeof(T).Name + "."); }
            static Grid()
            {
                var gridType = GridType(typeof(T));
                var next = new DynamicMethod("PeriodicGridNext", typeof(T), new[] { typeof(object), typeof(LocatableSearchData<T>).MakeByRefType() }, typeof(PeriodicSpatialQueries).Module, true);
                var il = next.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, gridType); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Callvirt, gridType.GetMethod("FindNextLocatable", Members)); il.Emit(OpCodes.Ret);
                Next = (NextFn<T>)next.CreateDelegate(typeof(NextFn<T>));
                var start = new DynamicMethod("PeriodicGridStart", typeof(LocatableSearchData<T>), new[] { typeof(object), typeof(Vec2), typeof(float) }, typeof(PeriodicSpatialQueries).Module, true);
                il = start.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, gridType); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Callvirt, gridType.GetMethod("StartFindingLocatablesAroundPosition", Members)); il.Emit(OpCodes.Ret);
                Start = (StartFn<T>)start.CreateDelegate(typeof(StartFn<T>));
            }

            internal static T FindNext(object grid, ref LocatableSearchData<T> data, Func<T, Vec2> position)
            {
                while (true)
                {
                    var result = Next(grid, ref data);
                    if (result != null)
                    {
                        if (PeriodicPatches.SeenByPrimary(data.Position, data.RadiusSquared, position(result))) continue;
                        return result;
                    }
                    float x;
                    if (!PeriodicPatches.TryImage(data.Position, data.RadiusSquared, out x)) return result;
                    System.Threading.Interlocked.Increment(ref GridImagePasses);
                    var primary = data;
                    data = Start(grid, new Vec2(x, primary.Position.Y), PeriodicPatches.ImageRadius(primary.RadiusSquared));
                    PeriodicPatches.MarkImage(data.Position, data.RadiusSquared, primary.Position, primary.RadiusSquared);
                }
            }
        }

        private static readonly Func<MobileParty, Vec2> partyPosition = p => p.GetPosition2D;
        private static readonly Func<Settlement, Vec2> settlementPosition = s => s.GetPosition2D;
        private static readonly Func<Track, Vec2> trackPosition = t => t.GetPosition2D;

        private static Type GridType(Type t) { return AccessTools.TypeByName("TaleWorlds.CampaignSystem.Map.LocatorGrid`1").MakeGenericType(t); }

        public static MobileParty GridNextParty(object grid, ref LocatableSearchData<MobileParty> data) { return Grid<MobileParty>.FindNext(grid, ref data, partyPosition); }
        public static Settlement GridNextSettlement(object grid, ref LocatableSearchData<Settlement> data) { return Grid<Settlement>.FindNext(grid, ref data, settlementPosition); }
        public static Track GridNextTrack(object grid, ref LocatableSearchData<Track> data) { return Grid<Track>.FindNext(grid, ref data, trackPosition); }

        // A direct grid search as a consumer starts it (the probe exercises the party and track wrappers this way).
        internal static LocatableSearchData<T> GridStart<T>(object grid, Vec2 position, float radius) { return Grid<T>.Start(grid, position, radius); }
    }
}
