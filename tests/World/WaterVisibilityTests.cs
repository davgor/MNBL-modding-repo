using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Surface;
using MNBL.ProceduralWorld.World;

// MMR-285 water visibility, without the game (MMR-247 cluster W: the water look, the box clipping and the road decals under planes):
//   WaterVisibilityTests REPO BUNDLE_A BUNDLE_B OUT_JSON [DUMP_DIR]
// On two real compiled worlds, over cells.bin v2 and the drawn water (the sea plane on the datum, the lake planes the scene carries,
// the water decals of the map dressing plan, each box clipped over the water planes as the engine target clips it), each recomputed here
// from the bundle rather than read from the plan's own covers:
//   a1  every lake group shows a plane (none hidden or unmatched under GeneratedWaterSystem.Lakes; the compiler's shaping record agrees),
//       and every River cell (decks included) has its centre inside a river stamp's opaque core, its ground inside the clipped box, or
//       under a plane;
//   a2  no cell that is impassable water for land parties (Sea, Lake, River without a road deck) renders as dry land: its centre lies
//       below the datum, under a shown lake plane, or inside a water decal's opaque core and clipped box (a centre at most the clearance
//       over the plane a covering stamp is clipped to is the shoreline itself: counted as waterline).
// MMR-247: no decal box (water or road) holds a water surface its footprint shows once clipped (how many would without the clip is
// recorded); no road decal footprint lies under a plane; fills paint no land cell and river strips' land paint is recorded; river stamps
// run along the chains (the patches a missed centre needed are recorded). Plus the decal budget shared with the road decals, the seam
// images of the water decals, determinism, and the installer drawing and removing every water decal. The evidence is
// evidence/world/water-visibility.json.
internal static class WaterVisibilityTests
{
    private static int checks, failures;
    private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; Console.WriteLine((condition ? "ok    " : "FAIL  ") + label); }

    private sealed class Recorder : IMapDressingTarget
    {
        public readonly Dictionary<int, double[]> Live = new Dictionary<int, double[]>();
        public int Decals, Tinted, WithFallback, Next;
        public object AddDecal(RoadDecal decal, double x, double y) { Decals++; if (decal.Tint != 0) Tinted++; if (decal.Fallback != null && decal.FallbackTint != 0) WithFallback++; Live[++Next] = new[] { x, y }; return Next; }
        public void MoveDecal(object handle, RoadDecal decal, double x, double y) { Live[(int)handle] = new[] { x, y }; }
        public void RemoveDecal(object handle) { if (!Live.Remove((int)handle)) throw new InvalidOperationException("removed twice"); }
        public object AddEntity(string prefab, double x, double y, double z, double yaw, double scale) { Live[++Next] = new[] { x, y, z }; return Next; }
        public void MoveEntity(object handle, double x, double y, double z, double yaw, double scale) { Live[(int)handle] = new[] { x, y, z }; }
        public void RemoveEntity(object handle) { if (!Live.Remove((int)handle)) throw new InvalidOperationException("removed twice"); }
        public double Ground(double x, double y) { return 1; }
    }

    private static int Main(string[] args)
    {
        var evidence = new Dictionary<string, object> { ["schema"] = "mnbl.water-visibility", ["version"] = 2, ["ticket"] = "MMR-285, MMR-247 (cluster W)" };
        try { Run(args, evidence); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        evidence["checks"] = checks; evidence["failures"] = failures;
        File.WriteAllText(args[3], Serializer.Serialize(evidence), new UTF8Encoding(false));
        Console.WriteLine(checks - failures + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    // The footprint of a decal sampled on an n x n grid over [-reach, reach] of its half extents (turned by its yaw): physical points.
    private static IEnumerable<double[]> Footprint(RoadDecal d, int n, double reachLength, double reachWidth)
    {
        double c = Math.Cos(d.Yaw), s = Math.Sin(d.Yaw);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double u = (n == 1 ? 0 : -1 + 2.0 * i / (n - 1)) * reachLength * d.HalfLength, v = (n == 1 ? 0 : -1 + 2.0 * j / (n - 1)) * reachWidth * d.HalfWidth;
                yield return new[] { d.X + u * c - v * s, d.Y + u * s + v * c, u / d.HalfLength, v / d.HalfWidth };
            }
    }

    private static void Run(string[] args, Dictionary<string, object> evidence)
    {
        string repo = args[0];
        var roads = RoadsPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, RoadsPolicy.PolicyPath)));
        var looks = SettlementLooksPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, SettlementLooksPolicy.PolicyPath)));
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", WorldDataBundle.HostCapabilities);
        evidence["policy"] = new Dictionary<string, object> { ["roadsSha256"] = roads.Sha256, ["material"] = roads.WaterMaterial, ["tint"] = roads.WaterTint.ToString("X8", CultureInfo.InvariantCulture),
            ["atlas"] = new[] { roads.WaterColumns, roads.WaterRows, roads.RiverColumn, roads.PoolColumn }, ["fallback"] = roads.WaterFallbackMaterial,
            ["fallbackTint"] = roads.WaterFallbackTint.ToString("X8", CultureInfo.InvariantCulture), ["riverWidth"] = roads.RiverWidth, ["riverSpacing"] = roads.RiverSpacing,
            ["smoothingPasses"] = roads.RiverSmoothingPasses, ["fillScale"] = roads.FillScale, ["opaqueCore"] = roads.OpaqueCore, ["maxDecals"] = roads.WaterMaxDecals,
            ["roadMaxDecals"] = roads.MaxDecals, ["waterVersion"] = GeneratedWaterSystem.Version, ["planeClearance"] = WaterPlanes.Clearance };

        // Unit checks: the opaque-core test wraps across the seam and follows the stamp's yaw.
        var stamp = new RoadDecal { X = 1, Y = 10, Yaw = Math.PI / 2, HalfLength = 4, HalfWidth = 1 };
        Check(stamp.Covers(1, 11.9, 100, .5) && !stamp.Covers(2.1, 10, 100, .5) && stamp.Covers(100.8, 10, 100, .5) && !stamp.Covers(1, 12.1, 100, .5),
              "a water stamp covers the ellipse inside its opaque core (half extents x opaqueCore), turned by its yaw and across the seam");
        // MMR-247: the box clip. A box straddling a plane keeps its top and lifts its bottom over the plane; one wholly under it is a sliver over
        // the plane; one with no plane stays whole. A footprint's floor is the highest plane whose water it shows: the datum where a sample lies
        // under it, a lake where a sample inside its box lies under its plane (not a lake beside it or under the ground); none over dry ground.
        var raised = new GeneratedWaterSystem.Lake { MinX = 20, MaxX = 30, MinY = 0, MaxY = 10, Z = 4 };
        var sunk = new GeneratedWaterSystem.Lake { MinX = 0, MaxX = 30, MinY = 0, MaxY = 10, Z = -2 };
        var planes = new WaterPlanes(new[] { raised, sunk });
        double z, half, z2, half2, z3, half3;
        bool kept = WaterPlanes.Clip(1, 3, 0, out z, out half), gone = WaterPlanes.Clip(-4, 3, 0, out z2, out half2), free = WaterPlanes.Clip(1, 3, Double.NegativeInfinity, out z3, out half3);
        Func<double, double, double> high = (x, y) => 5, low = (x, y) => 3, deep = (x, y) => x < 14 ? -1 : 3;
        Check(kept && Math.Abs(z - (4 + WaterPlanes.Clearance) / 2) < 1e-9 && Math.Abs(half - (4 - WaterPlanes.Clearance) / 2) < 1e-9 && !gone && z2 > 0 && half2 < .01 && free && z3 == 1 && half3 == 3
              && planes.Floor(17, 5, 0, 4, 1, low, 7) == 4 && planes.Floor(17, 5, 0, 4, 1, high, 7) == Double.NegativeInfinity && planes.Floor(15, 5, 0, 4, 1, low, 7) == Double.NegativeInfinity
              && planes.Floor(15, 5, 0, 4, 1, deep, 7) == 0 && planes.Floor(15, 5, Math.PI / 2, 4, 1, deep, 7) == Double.NegativeInfinity,
              "a decal box keeps its top and lifts its bottom " + WaterPlanes.Clearance + " over the highest water plane its footprint shows (the datum where a sample lies under it, a lake where a sample in its box lies under its plane), and stays whole over dry ground; a box wholly under is a sliver that paints nothing");
        Check(roads.WaterMaterial == "mnbl_water_surface" && roads.WaterFallbackMaterial == "mainmap_decal_undervillage" && roads.WaterMaxDecals + roads.MaxDecals <= 5000,
              "roads.json water: the published water material " + roads.WaterMaterial + ", falling back to the vanilla stamp " + roads.WaterFallbackMaterial + " tinted "
              + roads.WaterFallbackTint.ToString("X8", CultureInfo.InvariantCulture) + "; road and water caps " + roads.MaxDecals + " + " + roads.WaterMaxDecals + " stay under MMR-249's 5,000");

        var worlds = new List<object>(); evidence["worlds"] = worlds;
        foreach (var label in new[] { "A", "B" })
        {
            var bundle = WorldDataBundle.Open(label == "A" ? args[1] : args[2], host);
            var surface = WorldSurface.FromBundle(bundle);
            byte[] worldJson = bundle.ReadVerified("source/world.json");
            GeneratedClimateSystem.Source source;
            using (var stream = new MemoryStream(worldJson)) source = GeneratedClimateSystem.ReadSource(stream, GeneratedClimateSystem.Sha(worldJson));
            worldJson = null;
            var plan = MapDressing.Build(bundle, surface, null, source, roads, looks);
            var again = MapDressing.Build(bundle, surface, null, source, roads, looks);
            var graph = (IDictionary<string, object>)Serializer.DeserializeObject(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/travel/travel-graph.json")));
            var grid = Json.O(graph, "grid");
            var cells = TravelCells.Read(bundle.ReadVerified("compiled/travel/cells.bin"), Json.I(grid, "cols"), Json.I(grid, "rows"), Json.I(grid, "columnOffset"), Json.I(grid, "rowOffset"), Json.D(grid, "spacing"));
            var shaping = Json.O((IDictionary<string, object>)Serializer.DeserializeObject(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/terrain/shaping.json"))), "shaping");
            var compilerLakes = Json.O(shaping, "lakes");
            var row = new Dictionary<string, object> { ["label"] = label, ["worldContentId"] = bundle.WorldContentId };
            worlds.Add(row);

            // a1: lakes. The scene's planes by the runtime rule; the shaping record (the compiler's C++ port of the same rule) agrees.
            int unmatched, hidden;
            var lakes = GeneratedWaterSystem.Lakes(surface, source, PeriodicWorldCoordinates.FromSurface(surface), out unmatched, out hidden);
            int groups = lakes.Count + hidden + unmatched;
            int compilerGroups = Json.I(compilerLakes, "groups"), compilerShown = Json.I(compilerLakes, "shownAfter");
            row["lakes"] = new Dictionary<string, object> { ["groups"] = groups, ["shown"] = lakes.Count, ["hidden"] = hidden, ["unmatched"] = unmatched,
                ["canonical"] = lakes.Count(l => l.CenterX >= surface.Strip && l.CenterX < surface.Strip + surface.LogicalWidth), ["clamped"] = lakes.Count(l => l.Clamped),
                ["compiler"] = new Dictionary<string, object> { ["groups"] = compilerGroups, ["shownAfter"] = compilerShown, ["shownBefore"] = Json.I(compilerLakes, "shownBefore"), ["newlyHidden"] = Json.I(compilerLakes, "newlyHidden") } };
            Check(groups > 0 && hidden == 0 && unmatched == 0 && lakes.All(l => l.Z > l.BedMaxZ) && compilerGroups == groups && compilerShown == groups && plan.Lakes.Count == lakes.Count,
                  label + ": every lake group shows its plane: " + lakes.Count + " of " + groups + " (0 hidden, 0 unmatched; the compiler's shaping record: " + compilerShown + " of " + compilerGroups + " shown, " + Json.I(compilerLakes, "shownBefore") + " before shaping)");

            // The drawn water, recomputed: planes from the lakes above, decals from the plan, each box as the engine target clips it.
            var stamps = plan.Water;
            var ground = MapDressing.GroundOf(surface);
            var waterPlanes = new WaterPlanes(lakes);
            double core = roads.OpaqueCore, width = surface.LogicalWidth, sp = cells.Spacing;
            var boxes = stamps.Select(d => { double bz, bh; waterPlanes.Box(d, d.X, d.Y, ground, out bz, out bh); return new[] { bz, bh }; }).ToList();
            // A stamp paints a point inside its opaque core whose ground lies within the reach of its clipped box (the shader fades by 1 - |z|^8).
            Func<int, double, double, bool> paints = (i, x, y) => stamps[i].Covers(x, y, width, core) && Math.Abs(ground(x, y) - boxes[i][0]) <= MapDressing.DepthReach * boxes[i][1];
            Func<double, double, bool> stamped = (x, y) => Enumerable.Range(0, stamps.Count).Any(i => paints(i, x, y));
            Func<double, double, bool> riverStamped = (x, y) => Enumerable.Range(0, plan.RiverStamps).Any(i => paints(i, x, y));
            int riverCells = 0, riverDrawn = 0, riverDecks = 0, impassable = 0, dry = 0, landWet = 0, landCells = 0, coverMismatch = 0, unclippedCovers = 0, waterline = 0;
            var riverMissed = new List<object>();
            var by = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var dryCells = new List<object>();
            var planCells = plan.WaterCells.ToDictionary(w => w.Row * cells.Cols + w.Column);
            for (int r = 0; r < cells.Rows; r++)
                for (int c = 0; c < cells.Cols; c++)
                {
                    int i = r * cells.Cols + c;
                    byte k = cells.Classes[i];
                    double x = surface.Strip + (c + .5) * sp, y = surface.PoleMargin + (r + .5) * sp;
                    double g = Math.Max(surface.Height(x, y), surface.CollisionHeight(x, y));
                    bool sea = g < GeneratedWaterSystem.DatumZ, lake = lakes.Any(l => x >= l.MinX && x <= l.MaxX && y >= l.MinY && y <= l.MaxY && l.Z > g);
                    if (!TravelCells.Water(k))
                    {
                        landCells++;
                        if (sea || lake) landWet++;
                        continue;
                    }
                    WaterCell planned;
                    if (!planCells.TryGetValue(i, out planned) || Math.Abs(planned.X - x) > 1e-9 || Math.Abs(planned.Y - y) > 1e-9) coverMismatch++;
                    if (k == TravelCells.River)
                    {
                        riverCells++;
                        if (cells.Road(i)) riverDecks++;
                        if (riverStamped(x, y) || sea || lake) riverDrawn++;   // a river cell under a plane shows that water (the strip is clipped over it)
                        else if (riverMissed.Count < 10) riverMissed.Add(new[] { r, c, Math.Round(g, 3) });
                    }
                    if (cells.Road(i)) continue;   // a deck: the bridge carries land parties
                    impassable++;
                    bool inCore = !sea && !lake && stamps.Any(s => s.Covers(x, y, width, core));
                    string cover = sea ? "sea" : lake ? "lake" : stamped(x, y) ? (planned != null && planned.Cover == "fill" ? "fill" : "river") : null;
                    if (cover == null && inCore) unclippedCovers++;
                    // The shoreline itself: ground at most the clearance over the water plane a stamp covering it is clipped to (a lake plane sits
                    // 0.001 under its lowest shore sample, MMR-285): no box can paint it without also painting the water. Counted, not dry.
                    if (cover == null && Enumerable.Range(0, stamps.Count).Any(j => stamps[j].Covers(x, y, width, core) && g < boxes[j][0] - boxes[j][1] && g >= boxes[j][0] - boxes[j][1] - WaterPlanes.Clearance - 1e-9))
                    { cover = "waterline"; waterline++; }
                    string key = TravelCells.ClassNames[k] + ":" + (cover ?? "dry");
                    int n; by.TryGetValue(key, out n); by[key] = n + 1;
                    if (cover == null)
                    {
                        dry++;
                        int s0 = Enumerable.Range(0, stamps.Count).FirstOrDefault(j => stamps[j].Covers(x, y, width, core));
                        if (dryCells.Count < 20) dryCells.Add(new object[] { r, c, k, Math.Round(g, 3), inCore ? new[] { Math.Round(boxes[s0][0] - boxes[s0][1], 3), Math.Round(boxes[s0][0] + boxes[s0][1], 3) } : null });
                    }
                }
            row["rivers"] = new Dictionary<string, object> { ["cells"] = riverCells, ["decks"] = riverDecks, ["drawn"] = riverDrawn, ["edges"] = plan.RiverEdges, ["mouths"] = plan.MouthEdges, ["stamps"] = plan.RiverStamps,
                ["chains"] = plan.RiverChains, ["pools"] = plan.RiverPools, ["patches"] = plan.RiverPatches, ["spacingUsed"] = plan.WaterSpacingUsed };
            row["impassable"] = new Dictionary<string, object> { ["cells"] = impassable, ["byClassAndCover"] = by, ["dry"] = dry, ["dryCells"] = dryCells, ["dryOnlyByTheClip"] = unclippedCovers,
                ["waterline"] = waterline, ["waterlineRule"] = "ground at most the clearance over the water plane a stamp covering the centre is clipped to: the shoreline, which a clipped box cannot paint" };
            row["rivers.missed"] = riverMissed;
            row["land"] = new Dictionary<string, object> { ["cells"] = landCells, ["drawnAsWater"] = landWet, ["note"] = "land and mountain cells whose centre shows water (allowed; recorded)" };
            Check(riverCells > 0 && riverDrawn == riverCells,
                  label + ": every River cell is drawn as water: " + riverDrawn + " of " + riverCells + " centres inside a river stamp's opaque core and clipped box, or under a plane (" + riverDecks + " decks under their bridges; "
                  + plan.RiverEdges + " river edges, " + plan.MouthEdges + " mouths, " + plan.RiverChains + " chains, " + plan.RiverStamps + " stamps at spacing "
                  + plan.WaterSpacingUsed.ToString("0.###", CultureInfo.InvariantCulture) + ", " + plan.RiverPools + " lone-cell pools, " + plan.RiverPatches + " patches)");
            Check(impassable > 0 && dry == 0,
                  label + ": no impassable water cell renders as dry land: " + impassable + " cells (" + String.Join(", ", by.Select(kv => kv.Key + " " + kv.Value)) + "; " + waterline + " at the waterline)");
            Check(coverMismatch == 0 && plan.WaterCells.Count == cells.Classes.Count(k => TravelCells.Water(k)) && plan.WaterCells.All(w => w.Cover != null),
                  label + ": the plan lists every water cell of cells.bin (" + plan.WaterCells.Count + ") at its centre, each with its cover");

            // MMR-247: no decal box holds a water surface its footprint shows. A footprint (7 x 7 samples) shows the sea where its ground is under
            // the datum and a lake where it is inside the lake's box under its plane; the box holds that surface when the plane's height is inside it.
            Func<RoadDecal, double, double, bool> holdsWater = (d, bz, bh) => Footprint(d, 7, 1, 1).Any(p =>
            {
                double gx = ground(p[0], p[1]);
                if (gx < GeneratedWaterSystem.DatumZ && Math.Abs(GeneratedWaterSystem.DatumZ - bz) <= bh) return true;
                return lakes.Any(l => p[0] >= l.MinX && p[0] <= l.MaxX && p[1] >= l.MinY && p[1] <= l.MaxY && gx < l.Z && Math.Abs(l.Z - bz) <= bh);
            });
            var held = new Dictionary<string, object>();
            int waterHeldBefore = 0, waterHeldAfter = 0, roadHeldBefore = 0, roadHeldAfter = 0, waterClipped = 0, roadClipped = 0, submerged = 0;
            foreach (var group in new[] { new { Name = "water", List = plan.Water }, new { Name = "road", List = plan.Decals } })
                foreach (var d in group.List)
                {
                    double gc = ground(d.X, d.Y) + d.BoxOffset, bz, bh;
                    bool above = waterPlanes.Box(d, d.X, d.Y, ground, out bz, out bh);
                    bool before = holdsWater(d, gc, d.HalfDepth), after = above && holdsWater(d, bz, bh), clipped = bh < d.HalfDepth - 1e-9;
                    if (!above) submerged++;
                    if (group.Name == "water") { if (before) waterHeldBefore++; if (after) waterHeldAfter++; if (clipped) waterClipped++; }
                    else { if (before) roadHeldBefore++; if (after) roadHeldAfter++; if (clipped) roadClipped++; }
                }
            row["boxes"] = new Dictionary<string, object> { ["water"] = new Dictionary<string, object> { ["decals"] = plan.Water.Count, ["clipped"] = waterClipped, ["holdWaterUnclipped"] = waterHeldBefore, ["holdWaterClipped"] = waterHeldAfter },
                ["road"] = new Dictionary<string, object> { ["decals"] = plan.Decals.Count, ["clipped"] = roadClipped, ["holdWaterUnclipped"] = roadHeldBefore, ["holdWaterClipped"] = roadHeldAfter },
                ["submerged"] = submerged, ["clearance"] = WaterPlanes.Clearance,
                ["note"] = "a box holds water when a footprint sample (7 x 7) shows the sea or a shown lake and that plane's height is inside the box; unclipped = the decal's box before the clip (water stamps: fitted to their core; road decals: +-3 on the ground)" };
            Check(waterHeldAfter == 0 && roadHeldAfter == 0 && waterHeldBefore + roadHeldBefore > 0,
                  label + ": clipped over the water planes, no decal box holds a water surface its footprint shows (unclipped, " + waterHeldBefore + " water and " + roadHeldBefore + " road decals did: "
                  + "the water shader adds them to the water; " + waterClipped + " water and " + roadClipped + " road boxes clipped, " + submerged + " wholly under a plane)");
            // MMR-247: no road decal's footprint (ends and centre) lies under a plane; the plan counts the ones it dropped there.
            int roadsUnder = plan.Decals.Count(d => new[] { -d.HalfLength, 0, d.HalfLength }.Any(t =>
            {
                double x = d.X + Math.Cos(d.Yaw) * t, y = d.Y + Math.Sin(d.Yaw) * t;
                if (x >= surface.Strip + surface.LogicalWidth) x -= surface.LogicalWidth; else if (x < surface.Strip) x += surface.LogicalWidth;
                return MapDressing.PlaneCover(lakes, x, y, ground(x, y)) != null;
            }));
            row["roads"] = new Dictionary<string, object> { ["decals"] = plan.Decals.Count, ["droppedOnWater"] = plan.DecalsDroppedWater, ["droppedUnderPlanes"] = plan.DecalsDroppedPlane, ["underPlanes"] = roadsUnder };
            Check(roadsUnder == 0 && plan.DecalsDroppedPlane <= plan.DecalsDroppedWater,
                  label + ": no road decal's footprint lies under the sea plane or a shown lake plane (" + plan.DecalsDroppedPlane + " dropped there, counted in the " + plan.DecalsDroppedWater + " dropped on water)");
            // MMR-247: paint past the bank. A stamp's visible extent (alpha above about 0.25: a strip to 0.9 of its half length and 0.85 of its half
            // width, a pool to 0.85 of its radius) sampled every ~0.5 units; a sample paints dry land when it lies on a land or mountain cell, over
            // every plane, within its clipped box. Fills must paint no land cell; the strips' share is recorded (diagonal links cross land corners).
            var landPaint = new SortedDictionary<string, double>(StringComparer.Ordinal); var painted = new SortedDictionary<string, double>(StringComparer.Ordinal);
            for (int i = 0; i < stamps.Count; i++)
            {
                var d = stamps[i];
                int n = Math.Max(3, (int)Math.Ceiling(2 * Math.Max(d.HalfLength, d.HalfWidth) / .5));
                double reach = d.Kind == "river" ? .9 : .85, cellArea = (2 * d.HalfLength * reach / (n - 1)) * (2 * d.HalfWidth * .85 / (n - 1));
                foreach (var p in Footprint(d, n, reach, .85))
                {
                    if (d.Kind != "river" && p[2] * p[2] + p[3] * p[3] > .85 * .85 + 1e-9) continue;
                    double x = p[0], y = p[1], gx = ground(x, y);
                    if (Math.Abs(gx - boxes[i][0]) > boxes[i][1] || MapDressing.PlaneCover(lakes, x, y, gx) != null) continue;
                    double a; painted.TryGetValue(d.Kind, out a); painted[d.Kind] = a + cellArea;
                    double lx = x - surface.Strip, ly = y - surface.PoleMargin;
                    int r = (int)Math.Floor(ly / sp), c = (int)Math.Floor(lx / sp);
                    if (cells.InRows(r) && TravelCells.Water(cells.At(r, c))) continue;
                    double b; landPaint.TryGetValue(d.Kind, out b); landPaint[d.Kind] = b + cellArea;
                }
            }
            double fillLand; landPaint.TryGetValue("fill", out fillLand);
            row["paint"] = new Dictionary<string, object> { ["paintedArea"] = painted.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 1)), ["onLandCells"] = landPaint.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 1)),
                ["kinds"] = stamps.GroupBy(d => d.Kind).ToDictionary(g => g.Key, g => g.Count()), ["note"] = "map units^2 of dry ground each kind paints (over every plane, inside its clipped box), and the part on land or mountain cells" };
            Check(fillLand == 0 && stamps.All(d => d.Kind == "river" || d.Kind == "pool" || d.Kind == "fill") && stamps.Take(plan.RiverStamps).All(d => d.Kind != "fill") && stamps.Skip(plan.RiverStamps).All(d => d.Kind == "fill"),
                  label + ": fill stamps paint no land cell; river strips paint " + Math.Round(landPaint.ContainsKey("river") ? landPaint["river"] : 0, 1) + " of " + Math.Round(painted.ContainsKey("river") ? painted["river"] : 0, 1)
                  + " units^2 on land cells (diagonal links), pools " + Math.Round(landPaint.ContainsKey("pool") ? landPaint["pool"] : 0, 1));

            // Budget, seam images, determinism, the installer.
            int total = plan.Decals.Count + plan.Water.Count;
            row["decals"] = new Dictionary<string, object> { ["road"] = plan.Decals.Count, ["water"] = plan.Water.Count, ["riverStamps"] = plan.RiverStamps, ["fillStamps"] = plan.FillStamps, ["total"] = total,
                ["roadCap"] = roads.MaxDecals, ["waterCap"] = roads.WaterMaxDecals, ["budget"] = 5000, ["note"] = "whole-world totals (every decal drawn, not just in view); MMR-260's overlay draws its own share" };
            Check(plan.Water.Count <= roads.WaterMaxDecals && total <= 5000 && plan.FillStamps == plan.WaterCells.Count(w => w.Cover == "fill"),
                  label + ": " + plan.Water.Count + " water decals (" + plan.RiverStamps + " river, " + plan.FillStamps + " fill) within water maxDecals " + roads.WaterMaxDecals + "; with " + plan.Decals.Count + " road decals " + total + " in all, under 5,000");
            bool canonical = plan.Water.All(d => d.X >= surface.Strip - 1e-9 && d.X < surface.Strip + surface.LogicalWidth + 1e-9 && d.Y >= surface.PoleMargin - sp && d.Y <= surface.PoleMargin + surface.LogicalHeight + sp
                                               && d.Material == roads.WaterMaterial && d.Tint == roads.WaterTint && d.Fallback == roads.WaterFallbackMaterial && d.FallbackTint == roads.WaterFallbackTint
                                               && d.Atlas[2] == (d.Kind == "river" ? roads.RiverColumn : roads.PoolColumn) / (float)roads.WaterColumns);
            Check(canonical && plan.Sha256() == again.Sha256(), label + ": every water decal is canonical (drawn at its image nearest the camera, as the road decals), shows its kind's atlas column with the vanilla fallback, and the plan is deterministic (" + plan.Sha256().Substring(0, 12) + ")");
            var target = new Recorder();
            var installer = new MapDressingInstaller(plan, target);
            installer.Install(x => x);
            int drawn = installer.DrawnCount(3);
            double mid = surface.Strip + surface.LogicalWidth / 2;
            installer.Refresh(x => x >= mid ? x - surface.LogicalWidth : x + surface.LogicalWidth);
            bool moved = installer.DrawnWater().All(kv => kv.Value.Value < surface.Strip || kv.Value.Value >= surface.Strip + surface.LogicalWidth);
            installer.Remove();
            Check(drawn == plan.Water.Count && target.Tinted == plan.Water.Count && target.WithFallback == plan.Water.Count && moved && target.Live.Count == 0 && installer.Errors.Count == 0,
                  label + ": the installer draws all " + drawn + " water decals (each with its tinted fallback), moves each to its other image on a seam refresh, and removes every one on teardown");
            row["plan"] = plan.WaterRecord();
            // An optional dump of the drawn water for review images (DUMP_DIR, not evidence): decals with their kind, atlas cell and clipped box,
            // road decals, lakes, cells, and two preview windows (the densest river window and the largest lake) with ground and cell rasters.
            if (args.Length > 4) Dump(args[4], label, plan, surface, cells, lakes, boxes, ground);
        }
    }

    private static void Dump(string dir, string label, MapDressingPlan plan, WorldSurface surface, TravelCells cells, IList<GeneratedWaterSystem.Lake> lakes, List<double[]> boxes, Func<double, double, double> ground)
    {
        double sp = cells.Spacing;
        var windows = new List<object>();
        // the 80 x 50 window (on cell centres) holding the most river cells, and the one around the largest canonical lake over the datum
        int bestR = 0, bestC = 0, best = -1;
        for (int r = 0; r < cells.Rows; r += 2)
            for (int c = 0; c < cells.Cols; c += 2)
            {
                int n = 0;
                for (int dr = -4; dr <= 4; dr++) for (int dc = -6; dc <= 6; dc++) if (cells.InRows(r + dr) && cells.At(r + dr, c + dc) == TravelCells.River) n++;
                if (n > best) { best = n; bestR = r; bestC = c; }
            }
        var centres = new List<double[]> { new[] { surface.Strip + (bestC + .5) * sp, surface.PoleMargin + (bestR + .5) * sp } };
        var lake = lakes.Where(l => l.CenterX >= surface.Strip && l.CenterX < surface.Strip + surface.LogicalWidth && l.Z > GeneratedWaterSystem.DatumZ).OrderByDescending(l => l.Samples).FirstOrDefault();   // over the sea plane
        if (lake != null) centres.Add(new[] { lake.CenterX, lake.CenterY });
        foreach (var centre in centres)
        {
            double x0 = centre[0] - 40, y0 = centre[1] - 25, step = .25;
            int w = 320, h = 200;
            var heights = new double[w * h]; var classes = new int[w * h];
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    double x = x0 + i * step, y = y0 + j * step;
                    heights[j * w + i] = Math.Round(ground(x, y), 3);
                    double lx = x - surface.Strip, ly = y - surface.PoleMargin;
                    int r = (int)Math.Floor(ly / sp), c = (int)Math.Floor(lx / sp);
                    classes[j * w + i] = cells.InRows(r) ? cells.At(r, c) : TravelCells.Sea;
                }
            windows.Add(new Dictionary<string, object> { ["x0"] = x0, ["y0"] = y0, ["step"] = step, ["w"] = w, ["h"] = h, ["ground"] = heights, ["classes"] = classes });
        }
        File.WriteAllText(Path.Combine(dir, "water-" + label + ".json"), Serializer.Serialize(new Dictionary<string, object> {
            ["strip"] = surface.Strip, ["poleMargin"] = surface.PoleMargin, ["width"] = surface.LogicalWidth, ["spacing"] = sp, ["core"] = plan.WaterCore, ["clearance"] = WaterPlanes.Clearance,
            ["decals"] = plan.Water.Select(d => new[] { d.X, d.Y, d.Yaw, d.HalfLength, d.HalfWidth }).ToList(),
            ["water"] = plan.Water.Select((d, i) => new Dictionary<string, object> { ["kind"] = d.Kind, ["x"] = d.X, ["y"] = d.Y, ["yaw"] = d.Yaw, ["hl"] = d.HalfLength, ["hw"] = d.HalfWidth,
                ["hd"] = d.HalfDepth, ["atlas"] = d.Atlas, ["z"] = boxes[i][0], ["half"] = boxes[i][1] }).ToList(),
            ["roads"] = plan.Decals.Select(d => new[] { d.X, d.Y, d.Yaw, d.HalfLength, d.HalfWidth }).ToList(),
            ["lakes"] = lakes.Select(l => new[] { l.MinX, l.MinY, l.MaxX, l.MaxY, l.Z }).ToList(),
            ["cells"] = plan.WaterCells.Select(c => new object[] { c.Row, c.Column, c.Class, c.Cover }).ToList(), ["windows"] = windows }), new UTF8Encoding(false));
    }
}
