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

// MMR-257 map dressing tests, without the game:
//   MapDressingTests REPO BUNDLE_A BUNDLE_B OUT_JSON GAME_ROOT   (the game root: Native's decal atlas list, MMR-247 D)
// On two real compiled worlds: the plan's road decals (counts per family, none on water or past a bank with no deck, none inside a gate's
// clip, the per-world cap), the bridges at the navigation's decks (from a navigation report persisted and read back as activation and a load
// read it, MMR-247), the ruin markers (every ruin counted: placed on land, snapped or skipped,
// clear of live settlements), the ruins encyclopedia rows, determinism, and the installer over a recording target (everything it draws is
// moved on a seam refresh and removed on teardown).
internal static class MapDressingTests
{
    private static int checks, failures;
    private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; Console.WriteLine((condition ? "ok    " : "FAIL  ") + label); }

    // Records what is drawn; every handle is live until removed. Once Cleared is set (the engine ran Scene.ClearAll), every call fails and is
    // counted: MMR-262's teardown must make none.
    private sealed class Recorder : IMapDressingTarget
    {
        public readonly Dictionary<int, double[]> Live = new Dictionary<int, double[]>();
        public int Decals, Entities, Moves, EntityMoves, Next, AfterClear;
        public bool Cleared, FailAdds, FailRemoves;                  // MMR-247 X: an entity the engine does not create again / does not remove
        public readonly List<char> EntityCalls = new List<char>();   // 'a' AddEntity, 'r' RemoveEntity, in order
        private void Engine() { if (Cleared) { AfterClear++; throw new InvalidOperationException("an engine call on a cleared scene"); } }
        public object AddDecal(RoadDecal decal, double x, double y) { Engine(); Decals++; Live[++Next] = new[] { x, y }; return Next; }
        public void MoveDecal(object handle, RoadDecal decal, double x, double y) { Engine(); Moves++; Live[(int)handle] = new[] { x, y }; }
        public void RemoveDecal(object handle) { Engine(); if (!Live.Remove((int)handle)) throw new InvalidOperationException("removed twice"); }
        public object AddEntity(string prefab, double x, double y, double z, double yaw, double scale) { Engine(); EntityCalls.Add('a'); if (FailAdds) return null; Entities++; Live[++Next] = new[] { x, y, z }; return Next; }
        public void MoveEntity(object handle, double x, double y, double z, double yaw, double scale) { Engine(); Moves++; EntityMoves++; Live[(int)handle] = new[] { x, y, z }; }
        public void RemoveEntity(object handle) { Engine(); EntityCalls.Add('r'); if (FailRemoves) throw new InvalidOperationException("removal refused"); if (!Live.Remove((int)handle)) throw new InvalidOperationException("removed twice"); }
        public double Ground(double x, double y) { Engine(); return 1; }
    }

    private static double Ground(WorldSurface surface, double x, double y) { return MapDressing.Ground(surface, Math.Max(0, Math.Min(surface.Extent, x)), Math.Max(0, Math.Min(surface.Extent, y))); }

    // A method's body in a source file ("" when not found).
    internal static string Body(string source, string signature)
    {
        int begin = source.IndexOf(signature, StringComparison.Ordinal), end = begin < 0 ? -1 : source.IndexOf("\n        }\n", begin, StringComparison.Ordinal);
        return begin < 0 || end < 0 ? "" : source.Substring(begin, end - begin);
    }

    private static int Main(string[] args)
    {
        var evidence = new Dictionary<string, object> { ["schema"] = "mnbl.map-dressing-offline", ["version"] = 1 };
        try { Run(args, evidence); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        evidence["checks"] = checks; evidence["failures"] = failures;
        File.WriteAllText(args[3], Serializer.Serialize(evidence), new UTF8Encoding(false));
        Console.WriteLine(checks - failures + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string[] args, Dictionary<string, object> evidence)
    {
        string repo = args[0];
        var roads = RoadsPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, RoadsPolicy.PolicyPath)));
        var looks = SettlementLooksPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, SettlementLooksPolicy.PolicyPath)));
        var navigationPolicy = NavigationPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, NavigationPolicy.PolicyPath)));
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", WorldDataBundle.HostCapabilities);

        // Unit checks.
        var line = new List<double[]> { new double[] { 0, 0 }, new double[] { 1, 0 }, new double[] { 1, 1 } };
        var smooth = MapDressing.Chaikin(MapDressing.Chaikin(line));
        Check(smooth.First()[0] == 0 && smooth.First()[1] == 0 && smooth.Last()[0] == 1 && smooth.Last()[1] == 1 && smooth.All(p => p[0] >= 0 && p[0] <= 1 && p[1] >= 0 && p[1] <= 1)
              && MapDressing.Length(smooth) < MapDressing.Length(line), "smoothing keeps both ends, stays inside the corner cells and shortens the corner");
        Check(RuinNames.Cause("war_international") == "International war" && RuinNames.Cause("dragon") == "Dragon" && RuinNames.Cause("self_magic") == "Its own magic",
              "ruin causes read as words");
        // MMR-262: the slope depth. Flat ground keeps the probe's 3; a 1-in-1 slope along x reaches 5 at a 5-unit corner (5 / 0.85); a long thin
        // footprint turned across the slope (yaw 90 degrees: its length runs along y) reaches only its half width.
        Func<double, double, double> flat = (x, y) => 2, tilted = (x, y) => x;
        Check(MapDressing.SlopeDepth(flat, 10, 10, .7, 5, 5) == MapDressing.DecalHalfDepth && Math.Abs(MapDressing.SlopeDepth(tilted, 10, 10, 0, 5, 5) - 5 / MapDressing.DepthReach) < 1e-9
              && Math.Abs(MapDressing.SlopeDepth(tilted, 10, 10, 0, 5, 1) - 5 / MapDressing.DepthReach) < 1e-9 && MapDressing.SlopeDepth(tilted, 10, 10, Math.PI / 2, 5, 1) == MapDressing.DecalHalfDepth,
              "a decal's slope depth is max(3, the farthest footprint corner or edge midpoint from the centre height / 0.85), its footprint turned by its yaw as the engine frame turns it");

        // MMR-247 (D, acc5): the campaign map's decal atlas. A list parses to rects; an engine target draws a material whose diffuse texture the
        // atlas holds, else the decal's fallback; the installed world-map list (Native's, the only one the engine reads) holds the fallbacks'
        // vanilla stamps and none of the module's decal textures; a fallback's own box; water.minHalfDepth.
        Func<Action, bool> refuses = act => { try { act(); return false; } catch (InvalidDataException) { return true; } };
        var sample = DecalAtlas.Parse("<decal_textures>\n\t\t<decal_texture\n\t\t\tname=\"a_d\"\n\t\t\tatlas_pos=\"0.011719, 0.011719, 0.250000, 0.250000\"\n\t\t\tis_dynamic=\"false\" />\n" +
                                      "\t\t<decal_texture\n\t\t\tname=\"b_d\"\n\t\t\tatlas_pos=\"0.5, 0.5, 0.125, 0.125\"\n\t\t\tis_dynamic=\"true\" />\n</decal_textures>", "worldmap", "inline");
        Check(sample.Rects.Count == 2 && sample.Resident("a_d") && !sample.Resident("mnbl_water_surface_d") && sample.Dynamic.SetEquals(new[] { "b_d" }) && Math.Abs(sample.Rects["a_d"][2] - .25f) < 1e-6
              && DecalAtlas.Resident(null, "anything") && sample.Resident(null) && refuses(() => DecalAtlas.Parse("<decal_textures/>", "worldmap", "empty"))
              && refuses(() => DecalAtlas.Parse("<decal_texture name=\"x\" atlas_pos=\"0, 0, 2, 0.1\" is_dynamic=\"false\" />", "worldmap", "bad")),
              "a decal_textures list parses to rects (name, atlas_pos x y w h, is_dynamic); no list or no texture name judges nothing; an empty list or a rect outside [0, 1] is refused");
        Check(DecalAtlas.Choose(true, true, true, true, false) == DecalChoice.Material && DecalAtlas.Choose(true, false, true, true, false) == DecalChoice.Fallback
              && DecalAtlas.Choose(true, false, false, false, false) == DecalChoice.None && DecalAtlas.Choose(true, false, true, false, false) == DecalChoice.None
              && DecalAtlas.Choose(false, true, true, true, false) == DecalChoice.Fallback && DecalAtlas.Choose(true, false, true, true, true) == DecalChoice.Material
              && DecalAtlas.Choose(false, false, true, false, true) == DecalChoice.Fallback && !roads.DrawUnresident && roads.DecalAtlasGroup == "worldmap",
              "an engine target draws a material whose diffuse texture the atlas holds, else its decal's resolving resident fallback, else nothing; roads.json decalAtlas: worldmap, unresident fallback");
        string gameRoot = args.Length > 4 ? args[4] : null;
        string nativeList = gameRoot == null ? null : DecalAtlas.PathIn(Path.Combine(gameRoot, "Modules", "Native"), roads.DecalAtlasGroup);
        if (nativeList != null && File.Exists(nativeList))
        {
            var atlas = DecalAtlas.Load(nativeList, roads.DecalAtlasGroup);
            var vanilla = new[] { roads.WaterFallbackTexture, roads.Magic.StrokeFallback.Texture, roads.Magic.BurstFallback.Texture };
            var module = new[] { "mnbl_water_surface_d", "mnbl_water_surface_n", "mnbl_magicwater_d" }.Concat(roads.Magic.Schools.Select(s => roads.Magic.MaterialPrefix + s + "_d")).ToList();
            Check(vanilla.All(atlas.Resident) && module.All(t => !atlas.Resident(t)) && !atlas.Rects.Keys.Any(k => k.StartsWith("mnbl_", StringComparison.Ordinal)),
                  "the installed world-map atlas list (" + atlas.Rects.Count + " textures, " + atlas.Dynamic.Count + " dynamic) holds the fallbacks' vanilla stamps (" + String.Join(", ", vanilla) +
                  ") and none of the module's decal textures: acc5's 2,826 module-material decals sampled no rect");
            evidence["decalAtlas"] = new Dictionary<string, object> { ["source"] = nativeList, ["textures"] = atlas.Rects.Count, ["dynamic"] = atlas.Dynamic.Count, ["vanilla"] = vanilla, ["moduleResident"] = module.Where(atlas.Resident).ToList() };
        }
        else Check(false, "the installed Native decal_textures_" + roads.DecalAtlasGroup + ".xml is readable (the game root is the fifth argument)");
        var stamp = new RoadDecal { Yaw = .3, HalfLength = 4, HalfWidth = 1, FallbackTurn = true, FallbackAlong = 1.1, FallbackAcross = 4.5 };
        double boxYaw, boxU, boxV;
        stamp.Box(false, out boxYaw, out boxU, out boxV);
        bool own = boxYaw == .3 && boxU == 4 && boxV == 1;
        stamp.Box(true, out boxYaw, out boxU, out boxV);
        Check(own && Math.Abs(boxYaw - (.3 + Math.PI / 2)) < 1e-12 && Math.Abs(boxU - 4.5) < 1e-12 && Math.Abs(boxV - 4.4) < 1e-12,
              "a fallback turned a quarter runs along the decal's length on its v (along x 1.1) and across its width on its u (across x 4.5); the decal keeps its own box");
        var thin = new RoadDecal { X = 0, Y = 0, HalfLength = 1, HalfWidth = 1, HalfDepth = .05, Floor = Double.NegativeInfinity };
        double thinZ, thinHalf, deepZ, deepHalf;
        new WaterPlanes(null).Box(thin, 0, 0, flat, out thinZ, out thinHalf);
        new WaterPlanes(null).Box(thin, 0, 0, flat, out deepZ, out deepHalf, .5);
        string dressingRuntime = File.ReadAllText(Path.Combine(repo, "src/MNBL.ProceduralWorld/World/MapDressingRuntime.cs")), waterRuntime = File.ReadAllText(Path.Combine(repo, "src/MNBL.ProceduralWorld/World/MagicWaterRuntime.cs"));
        Check(Math.Abs(thinHalf - .05) < 1e-12 && Math.Abs(deepHalf - .5) < 1e-12 && thinZ == 2 && deepZ == 2 && roads.WaterMinHalfDepth == 0 && dressingRuntime.Contains("DecalAtlasRuntime.WaterMinHalfDepth")
              && Body(dressingRuntime, "public object AddDecal(RoadDecal d, double x, double y)").Contains("chooser.Choose(d, out fallback)")
              && Body(waterRuntime, "public object AddDecal(RoadDecal d, double x, double y, double z)").Contains("Chooser.Choose(d, out fallback)"),
              "a water box takes at least water.minHalfDepth (0: W's fitted boxes); both engine targets (the dressing's and magic water's) choose each decal's material by the decal atlas");

        var calibration = (IDictionary<string, object>)Serializer.DeserializeObject(File.ReadAllText(Path.Combine(repo, "worldgen/calibration/naval-transitions/naval-transitions.json")).TrimStart('﻿'));
        var model = NavigationModel.FromCalibration(calibration);
        string store = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "store-" + Guid.NewGuid().ToString("N"));
        var worlds = new List<object>(); evidence["worlds"] = worlds;
        foreach (var label in new[] { "A", "B" })
        {
            var bundle = WorldDataBundle.Open(label == "A" ? args[1] : args[2], host);
            var surface = WorldSurface.FromBundle(bundle);
            var navigation = RuntimeNavigationBuilder.Build(NavigationInput.FromBundle(bundle, surface, navigationPolicy), navigationPolicy);
            // MMR-247: the report as activation and a load read it. The validator's report over the managed stand-in, persisted as production
            // persists it (NavigationGate.Persist: the gate record, the report as hashed text inside), then read back through NavigationGate.ReadAt
            // (ActivationCommitStage.BuildWorld, GeneratedWorldLoader.CheckCompiled) after the gate passes it for the navmesh (RequireAt).
            string sceneName = "test_" + label.ToLowerInvariant(), navmesh = Path.Combine(store, sceneName + "-navmesh.bin");
            var validated = NavigationValidator.Run(NavigationPlan.Create(navigation, model), new MeshNavigationEngine(navigation.Mesh, model), sceneName, null);
            string recordPath = NavigationGate.Persist(store, validated);
            File.WriteAllBytes(navmesh, navigation.Bytes);
            NavigationGate.RequireAt(recordPath, sceneName, navmesh, navigationPolicy.Sha256);
            var report = NavigationGate.ReadAt(recordPath, sceneName);
            var persisted = (IDictionary<string, object>)Serializer.DeserializeObject(File.ReadAllText(recordPath));
            string recordRefusal = null;
            try { MapDressing.Build(bundle, surface, persisted, null, roads, looks); } catch (Exception error) { recordRefusal = error.Message; }
            Check(Json.S(report, "reportSha256") == Json.S(validated, "reportSha256") && Json.A(Json.O(report, "build"), "bridges").Length == navigation.Bridges.Count && !persisted.ContainsKey("build") && recordRefusal == "Missing object build.",
                  label + ": the persisted navigation record reads back through NavigationGate.ReadAt as the validator's report (" + Json.S(report, "reportSha256").Substring(0, 12) + ", "
                  + navigation.Bridges.Count + " decks in build.bridges); the record itself has no build, and the map dressing refuses it (" + recordRefusal + ")");
            byte[] worldJson = bundle.ReadVerified("source/world.json");
            GeneratedClimateSystem.Source source;
            using (var stream = new MemoryStream(worldJson)) source = GeneratedClimateSystem.ReadSource(stream, GeneratedClimateSystem.Sha(worldJson));
            var world = (IDictionary<string, object>)Serializer.DeserializeObject(new UTF8Encoding(false).GetString(worldJson));
            worldJson = null;
            var plan = MapDressing.Build(bundle, surface, report, source, roads, looks);
            var again = MapDressing.Build(bundle, surface, report, source, roads, looks);
            var record = plan.Record();
            record["label"] = label; worlds.Add(record);

            var graph = (IDictionary<string, object>)Serializer.DeserializeObject(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/travel/travel-graph.json")));
            var campaign = (IDictionary<string, object>)Serializer.DeserializeObject(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/campaign/campaign.json")).TrimStart('\uFEFF'));
            var grid = Json.O(graph, "grid");
            var cells = TravelCells.Read(bundle.ReadVerified("compiled/travel/cells.bin"), Json.I(grid, "cols"), Json.I(grid, "rows"), Json.I(grid, "columnOffset"), Json.I(grid, "rowOffset"), Json.D(grid, "spacing"));
            Func<double, double, byte> cellAt = (px, py) =>
            {
                double lx = px - surface.Strip, ly = py - surface.PoleMargin;
                int r = (int)Math.Floor(ly / cells.Spacing), c = (int)Math.Floor(lx / cells.Spacing);
                return cells.InRows(r) ? cells.At(r, c) : TravelCells.Sea;
            };

            // Roads.
            string families = String.Join(", ", plan.DecalsByFamily.Select(kv => kv.Key + " " + kv.Value));
            Check(plan.Decals.Count > 0 && plan.Decals.Count <= roads.MaxDecals && plan.DecalsByFamily.Values.Sum() == plan.Decals.Count && plan.SpacingUsed >= roads.DecalSpacing,
                  label + ": " + plan.Decals.Count + " road decals (" + families + ") over " + plan.Pieces + " pieces, at most maxDecals " + roads.MaxDecals + ", spacing " + plan.SpacingUsed.ToString("0.###", CultureInfo.InvariantCulture));
            bool canonical = plan.Decals.All(d => d.X >= surface.Strip - 1e-9 && d.X < surface.Strip + surface.LogicalWidth + 1e-9 && d.Y >= surface.PoleMargin && d.Y <= surface.PoleMargin + surface.LogicalHeight);
            bool dry = plan.Decals.All(d => new[] { -d.HalfLength, 0, d.HalfLength }.All(t =>
            {
                double x = d.X + Math.Cos(d.Yaw) * t, y = d.Y + Math.Sin(d.Yaw) * t;
                if (x >= surface.Strip + surface.LogicalWidth) x -= surface.LogicalWidth; else if (x < surface.Strip) x += surface.LogicalWidth;
                return !TravelCells.Water(cellAt(x, y));
            }));
            Check(canonical && dry, label + ": every decal is canonical and its footprint (ends and centre) is on land, so no drawn road enters water, decks included (" + plan.DecalsDroppedWater + " dropped on water, " + plan.DecalsDroppedOverlap + " overlapping another road)");
            int bankEnds = 0, gates = 0; bool clipped = true;
            foreach (IDictionary<string, object> road in Json.A(graph, "roads"))
            {
                int index = Json.I(road, "index");
                var mine = plan.Decals.Where(d => d.Road == index).ToList();
                foreach (IDictionary<string, object> piece in Json.A(road, "pieces"))
                    foreach (var end in new[] { Json.O(piece, "start"), Json.O(piece, "end") })
                    {
                        string kind = Json.S(end, "kind");
                        if (kind == "bank") bankEnds++;
                        if (kind != "gate") continue;
                        gates++;
                        var p = Json.Point(end, "point");
                        var inside = plan.Decals.Where(d => MapDressing.Periodic(new[] { d.X - surface.Strip, d.Y - surface.PoleMargin }, p[0], p[1], surface.LogicalWidth) < roads.GateClip - 1e-6).ToList();
                        if (inside.Count > 0) { if (clipped) Console.WriteLine("      road " + index + " (" + Json.S(road, "kind") + ") gate " + Json.S(end, "settlement") + " at " + p[0] + "," + p[1] + ": decal of piece " + inside[0].Piece + " at " + (inside[0].X - surface.Strip) + "," + (inside[0].Y - surface.PoleMargin)); clipped = false; }
                    }
            }
            Check(clipped && gates == plan.GateEnds && bankEnds == plan.BankEnds && (label == "A" ? bankEnds == 18 : bankEnds == 52),
                  label + ": no decal inside any gate's " + roads.GateClip + "-unit clip (" + gates + " gate ends, " + plan.DecalsDroppedGate + " decals dropped there); roads stop at " + bankEnds + " banks with no deck (RT-17; MMR-253's 18 on A', 52 on B')");
            // Bridges.
            var deckCells = new HashSet<long>(navigation.Bridges.SelectMany(b => b.Cells).Select(c => ((long)c[0] << 32) | (uint)c[1]));
            bool onDecks = plan.Bridges.All(s =>
            {
                double lx = s.X - surface.Strip, ly = s.Y - surface.PoleMargin;
                int r = (int)Math.Floor(ly / cells.Spacing), c = cells.Wrap((int)Math.Floor(lx / cells.Spacing));
                for (int dr = -1; dr <= 1; dr++) for (int dc = -1; dc <= 1; dc++) if (deckCells.Contains(((long)(r + dr) << 32) | (uint)cells.Wrap(c + dc))) return true;
                return false;
            });
            Check(plan.Decks == navigation.Bridges.Count && (plan.Decks == 0 || plan.Bridges.Count >= plan.Decks - plan.DecksSkippedLShaped) && onDecks
                  && plan.Bridges.All(s => roads.BridgePrefabs.Contains(s.Prefab) && s.Scale == roads.BridgeScale),
                  label + ": " + plan.Bridges.Count + " bridge segments at the " + plan.Decks + " existing decks of the kept navigation build (" + plan.DecksSkippedLShaped + " L-shaped skipped), every one on its deck");
            // Ruins.
            var ruins = Json.A(world, "ruins");
            int skipped = plan.SkippedRuins.Values.Sum();
            var live = Json.A(graph, "settlements").Cast<IDictionary<string, object>>().Where(s => s.ContainsKey("point")).Select(s => Json.Point(s, "point")).ToList();
            bool landed = plan.Markers.All(m => !TravelCells.Water(cellAt(m.X, m.Y)));
            bool clear = plan.Markers.All(m => live.All(p => MapDressing.Periodic(p, m.X - surface.Strip, m.Y - surface.PoleMargin, surface.LogicalWidth) >= looks.RuinMinGap));
            // MMR-247 (acc5, B' lake 94: a marker snapped onto a land cell's lake sample): no marker stands where a shown plane covers the ground (the
            // lower of the encoded and the collision height under the datum or under a lake plane in its box).
            int wetMarkers = plan.Markers.Count(m => MapDressing.PlaneCover(plan.Lakes, m.X, m.Y, Math.Min(surface.Height(m.X, m.Y), surface.CollisionHeight(m.X, m.Y))) != null);
            Check(plan.Ruins.Count == ruins.Length && plan.Markers.Count + skipped == ruins.Length && plan.Markers.Count > 0 && landed && clear && wetMarkers == 0 && plan.Markers.All(m => looks.RuinPieces.Contains(m.Prefab)),
                  label + ": " + ruins.Length + " FWGC++ ruins: " + plan.Markers.Count + " markers on land (" + plan.Markers.Count(m => m.Placement == "snapped") + " snapped), skipped "
                  + String.Join(", ", plan.SkippedRuins.Select(kv => kv.Key + " " + kv.Value)) + "; every marker at least " + looks.RuinMinGap + " units from a live settlement (RT-22); " + wetMarkers + " under the sea or one of the " + plan.Lakes.Count + " shown lake planes");
            var rows = MapDressing.EncyclopediaRows(plan);
            Check(rows.Count == ruins.Length && rows.Select(r => r[0]).Distinct().Count() == rows.Count && rows.All(r => !String.IsNullOrEmpty(r[1]) && r[2].Contains(" - "))
                  && plan.Ruins.All(r => !String.IsNullOrEmpty(r.Civilization) && !String.IsNullOrEmpty(r.Cause)),
                  label + ": the ruins encyclopedia lists all " + rows.Count + " ruins with name, civilization and cause (e.g. " + rows[0][1] + ": " + rows[0][2] + ")");
            // MMR-286: markers and rows show the compiler's cleaned names (campaign.json ruins[]): no Age tag, unique, the source name kept.
            var age = new System.Text.RegularExpressions.Regex(@"\(\s*age\s+\d+\s*\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var compiled = Json.A(campaign, "ruins").Cast<IDictionary<string, object>>().ToDictionary(r => Json.S(r, "uid"), r => Json.S(r, "name"));
            Check(plan.Ruins.All(r => r.Name == compiled[r.Uid] && !age.IsMatch(r.Name) && !String.IsNullOrEmpty(r.SourceName)) && plan.Ruins.Select(r => r.Name).Distinct().Count() == plan.Ruins.Count &&
                  rows.All(r => !age.IsMatch(r[1])) && plan.Ruins.Count(r => age.IsMatch(r.SourceName)) > 0,
                  label + ": ruin markers and rows show the compiler's cleaned, unique names (" + plan.Ruins.Count(r => age.IsMatch(r.SourceName)) + " Age-tagged source names cleaned, e.g. "
                  + plan.Ruins.Where(r => age.IsMatch(r.SourceName)).Select(r => r.SourceName + " -> " + r.Name).FirstOrDefault() + ")");
            Check(plan.Sha256() == again.Sha256(), label + ": the plan is deterministic (" + plan.Sha256().Substring(0, 12) + ")");
            // MMR-262, MMR-247: road decals keep the probe's depth; each water stamp's box spans its core's ground on the shaped relief (7 x 7
            // samples over half its half extents, a strip over its whole length, ground under a shown plane taken at the plane, within DepthReach of the
            // box around its centre, plus BoxPad; a pool or fill tops out FillHeadroom over its centre's ground) and no more, centred BoxOffset over its
            // centre's ground.
            bool roadsFlat = plan.Decals.All(d => d.HalfDepth == MapDressing.DecalHalfDepth && d.BoxOffset == 0);
            int spanning = 0, tight = 0, deepened = 0, raised = 0;
            var planes = new WaterPlanes(plan.Lakes);
            double deepest = 0, offsetMax = 0;
            foreach (var d in plan.Water)
            {
                double c = Math.Cos(d.Yaw), sn = Math.Sin(d.Yaw), centre = Ground(surface, d.X, d.Y) + d.BoxOffset, far = 0, along = d.Kind == "river" ? MapDressing.StripSpan : MapDressing.CoreSpan;
                for (int i = -3; i <= 3; i++)
                    for (int j = -3; j <= 3; j++)
                    {
                        double u = i / 3.0 * along * d.HalfLength, v = j / 3.0 * MapDressing.CoreSpan * d.HalfWidth, x = d.X + u * c - v * sn, y = d.Y + u * sn + v * c, g = Ground(surface, x, y);
                        double level = planes.Level(x, y, g);
                        if (!Double.IsNaN(level)) g = level;                                                                // under a plane: at the plane
                        if (d.Kind != "river") g = Math.Min(g, Ground(surface, d.X, d.Y) + MapDressing.FillHeadroom);   // a pool or fill tops out over its centre
                        far = Math.Max(far, Math.Abs(g - centre));
                    }
                double reach = MapDressing.DepthReach * (d.HalfDepth - MapDressing.BoxPad);
                if (far <= reach + 1e-9) spanning++;
                if (Math.Abs(far - reach) < 1e-9) tight++;
                if (d.HalfDepth > MapDressing.DecalHalfDepth) deepened++;
                if (Math.Abs(d.BoxOffset) > 1e-9) raised++;
                deepest = Math.Max(deepest, d.HalfDepth); offsetMax = Math.Max(offsetMax, Math.Abs(d.BoxOffset));
            }
            record["decalDepth"] = new Dictionary<string, object> { ["roads"] = MapDressing.DecalHalfDepth, ["water"] = new Dictionary<string, object> { ["stamps"] = plan.Water.Count, ["spanning"] = spanning, ["tight"] = tight,
                ["deeperThanRoads"] = deepened, ["offCentre"] = raised, ["maxHalfDepth"] = Math.Round(deepest, 3), ["maxOffset"] = Math.Round(offsetMax, 3), ["reachShare"] = MapDressing.DepthReach, ["pad"] = MapDressing.BoxPad } };
            Check(roadsFlat && spanning == plan.Water.Count && tight == plan.Water.Count && plan.Water.Count > 0,
                  label + ": road decals keep half depth 3; every water stamp's box spans its core's ground and no more (" + plan.Water.Count + " stamps, " + raised + " centred off their centre's ground by up to "
                  + offsetMax.ToString("0.##", CultureInfo.InvariantCulture) + ", " + deepened + " deeper than 3, up to " + deepest.ToString("0.##", CultureInfo.InvariantCulture) + ")");

            // Installer: draw, refresh at the other image, remove. MMR-247 X: decals move in place; bridges and ruin markers (prefab entities,
            // which can hold a stale occluder record after Scene.CheckResources) are removed and instantiated again at the new image, every
            // removal of a refresh before its first creation.
            var target = new Recorder();
            var installer = new MapDressingInstaller(plan, target);
            installer.Install(x => x);
            int total = plan.Decals.Count + plan.Bridges.Count + plan.Markers.Count + plan.Water.Count;   // MMR-285: the water decals too
            int decalCount = plan.Decals.Count + plan.Water.Count, entityCount = plan.Bridges.Count + plan.Markers.Count;
            bool drawn = target.Live.Count == total && target.Decals == decalCount && target.Entities == entityCount && installer.Errors.Count == 0;
            // The strip image of every object: x - W past the middle, x + W before it.
            double mid = surface.Strip + surface.LogicalWidth / 2;
            Func<double, double> flip = x => x >= mid ? x - surface.LogicalWidth : x + surface.LogicalWidth;
            Func<Recorder, bool> atStrip = t => t.Live.Values.All(v => v[0] < surface.Strip || v[0] >= surface.Strip + surface.LogicalWidth);
            var firstHandles = new HashSet<int>(target.Live.Keys);
            target.EntityCalls.Clear();
            installer.Refresh(flip);
            string calls = new string(target.EntityCalls.ToArray());
            bool moved = target.Moves == decalCount && target.EntityMoves == 0 && target.Entities == 2 * entityCount && installer.Recreated == entityCount && target.Live.Count == total
                         && target.Live.Keys.Count(k => !firstHandles.Contains(k)) == entityCount && atStrip(target) && calls == new string('r', entityCount) + new string('a', entityCount)
                         && installer.DrawnMarkers().All(m => m.Value < surface.Strip || m.Value >= surface.Strip + surface.LogicalWidth) && installer.Errors.Count == 0;
            installer.Refresh(flip);
            bool idle = target.Moves == decalCount && installer.Recreated == entityCount;
            installer.Remove();
            Check(drawn && moved && idle && target.Live.Count == 0 && installer.Live == 0 && installer.Removed == total && !installer.Installed && entityCount > 0,
                  label + ": the installer draws all " + total + " objects; a seam refresh moves the " + decalCount + " decals in place and instantiates the " + entityCount
                  + " bridges and markers again at their other image (all removals first), nothing when no image changes; it removes every one on a live scene (each handle once)");
            record["installer"] = installer.Record()["installed"];
            // MMR-247 X: each marker's prefab and position (which ones carry an occlusion body is read off the installed prefabs, results/MMR-247-X.md).
            record["markerPlaces"] = plan.Markers.Select(m => (object)new Dictionary<string, object> { ["prefab"] = m.Prefab, ["x"] = Math.Round(m.X, 2), ["y"] = Math.Round(m.Y, 2) }).ToList();
            // MMR-247 X: the hang repro's switches. Move: the pre-fix move in place (the control); Frozen: entities left where they are.
            var modeBefore = MapDressingInstaller.EntityImages;
            try
            {
                MapDressingInstaller.EntityImages = EntityImageMode.Move;
                var moving = new Recorder(); var inPlace = new MapDressingInstaller(plan, moving); inPlace.Install(x => x); inPlace.Refresh(flip);
                bool inPlaceControl = moving.Moves == total && moving.EntityMoves == entityCount && moving.Entities == entityCount && inPlace.MovedEntities == entityCount && inPlace.Recreated == 0 && atStrip(moving);
                MapDressingInstaller.EntityImages = EntityImageMode.Frozen;
                var still = new Recorder(); var frozen = new MapDressingInstaller(plan, still); frozen.Install(x => x);
                var entityHandles = new HashSet<int>(still.Live.Where(kv => kv.Value.Length == 3).Select(kv => kv.Key));
                var at = entityHandles.ToDictionary(k => k, k => still.Live[k][0]);
                frozen.Refresh(flip);
                bool held = still.Moves == decalCount && still.Entities == entityCount && frozen.Frozen == entityCount && frozen.Recreated == 0 && entityHandles.All(k => still.Live[k][0] == at[k]);
                Check(inPlaceControl && held, label + ": EntityImageMode.Move moves all " + total + " objects in place (the pre-fix control); Frozen moves the decals and leaves the " + entityCount + " entities where they are");
            }
            finally { MapDressingInstaller.EntityImages = modeBefore; }
            // A failed re-creation: the removed entity is no longer drawn (its handle went); a failed removal: it stays, not duplicated.
            var failing = new Recorder(); var lost = new MapDressingInstaller(plan, failing); lost.Install(x => x);
            failing.FailAdds = true; lost.Refresh(flip);
            bool dropped1 = lost.Live == decalCount && failing.Live.Count == decalCount && lost.Recreated == 0 && lost.Errors.Count == Math.Min(20, entityCount) && lost.Errors[0].Contains("not created again");
            failing.FailAdds = false; lost.Remove();
            var refusing = new Recorder(); var kept = new MapDressingInstaller(plan, refusing); kept.Install(x => x);
            refusing.FailRemoves = true; kept.Refresh(flip);
            bool stayed = kept.Live == total && refusing.Entities == entityCount && refusing.Live.Count == total && kept.Recreated == 0 && kept.Errors[0].StartsWith("renew remove", StringComparison.Ordinal);
            refusing.FailRemoves = false; kept.Errors.Clear(); kept.Remove();
            Check(dropped1 && failing.Live.Count == 0 && stayed && refusing.Live.Count == 0 && kept.Errors.Count == 0,
                  label + ": an entity not created again is dropped (its removal ran; teardown removes the rest once), one whose removal fails stays drawn and is not duplicated");
            // MMR-262: the map's teardown runs after Scene.ClearAll; the drop lets go of every handle with no target call. A Remove there would
            // call the cleared scene for every object (the control).
            var cleared = new Recorder();
            var dropping = new MapDressingInstaller(plan, cleared);
            dropping.Install(x => x);
            cleared.Cleared = true;
            int dropped = dropping.Drop();
            var control = new Recorder();
            var removing = new MapDressingInstaller(plan, control);
            removing.Install(x => x);
            control.Cleared = true;
            removing.Remove();
            record["teardown"] = new Dictionary<string, object> { ["dropped"] = dropped, ["engineCallsAfterClear"] = cleared.AfterClear, ["controlRemoveCallsAfterClear"] = control.AfterClear };
            Check(dropped == total && cleared.AfterClear == 0 && dropping.Live == 0 && !dropping.Installed && dropping.Removed == 0 && dropping.Dropped == total && dropping.Errors.Count == 0 && cleared.Live.Count == total
                  && control.AfterClear == total,
                  label + ": on a cleared scene the installer drops all " + dropped + " handles with 0 target calls (a Remove there would make " + control.AfterClear + ")");
        }
        // MMR-286's redirect: the display name comes from one function.
        var probe = new RuinRecord { Uid = "u", SourceName = "Fenpor (Age 1)" };
        RuinNames.Cleaner = r => r.SourceName.Replace(" (Age 1)", "");
        bool redirected = probe.Name == "Fenpor";
        RuinNames.Cleaner = null;
        bool raw = probe.Name == "Fenpor (Age 1)";
        RuinNames.Cleaner = RuinNames.Cleaned;
        probe.DisplayName = "Fenpor";
        Check(redirected && raw && probe.Name == "Fenpor", "ruin display names are read through RuinNames.Display, pointed at the compiler's cleaned names (MMR-286)");

        // MMR-262: the map's Destroy (after MapScreen.OnFinalize's Scene.ClearAll) lets go of the map dressing and the magic ground with no
        // engine call; native removal stays for an install on the same live scene.
        string scene = File.ReadAllText(Path.Combine(repo, "src/MNBL.ProceduralWorld/World/GeneratedMapScene.cs")).Replace("\r\n", "\n");
        string runtime = File.ReadAllText(Path.Combine(repo, "src/MNBL.ProceduralWorld/World/MapDressingRuntime.cs")).Replace("\r\n", "\n");
        string destroy = Body(scene, "public new void Destroy()"), handles = Body(runtime, "internal static void ReleaseHandles("), install = Body(runtime, "internal static void Install(");
        string[] native = { ".Remove()", "RemoveDecal", "RemoveEntity", "Release(trace)", "Release(null)" };
        Check(destroy.Contains("MapDressingRuntime.ReleaseHandles(") && destroy.Contains("MagicGroundRuntime.ReleaseHandles(") && !destroy.Contains(".Release(")
              && handles.Length > 0 && handles.Contains(".Drop()") && native.All(n => !handles.Contains(n))
              && install.Contains("held.Scene == mapScene) Release(trace); else ReleaseHandles(trace);"),
              "GeneratedMapScene.Destroy lets go of the map dressing and magic ground handles only (ReleaseHandles: the installer's Drop, no engine call); an install removes natively only what it holds on the same live scene");

        // MMR-247: activation and a load hand WorldReconstruction the report read through NavigationGate.ReadAt (acc2 handed it the record and
        // failed at BuildWorld with "Missing object build"); no module source reads a navigation record as JSON itself.
        Func<string, string> read = path => File.ReadAllText(Path.Combine(repo, path)).Replace("\r\n", "\n");
        string buildWorld = Body(read("src/MNBL.ProceduralWorld/Generation/ActivationCommitStage.cs"), "private void BuildWorld(");
        string checkCompiled = Body(read("src/MNBL.ProceduralWorld/Persistence/GeneratedWorldLoader.cs"), "private Restored CheckCompiled(");
        var rawReaders = Directory.GetFiles(Path.Combine(repo, "src", "MNBL.ProceduralWorld"), "*.cs", SearchOption.AllDirectories)
            .Where(f => { string t = File.ReadAllText(f); return t.Contains("ReadAllText(NavigationGate.ReportPath(") || t.Contains("ReadReport(work.NavigationReportPath") || t.Contains("(string)wrapper[\"report\"]"); })
            .Select(Path.GetFileName).ToList();
        Check(buildWorld.Contains("NavigationGate.ReadAt(NavigationGate.ReportPath(SubModule.Store, prepared.SceneName), prepared.SceneName)") && buildWorld.Contains("WorldReconstruction.Build(bundle, layout, coordinates, prepared.Surface, catalog, navigationReport)")
              && checkCompiled.Contains("NavigationGate.ReadAt(work.NavigationReportPath, plan.Scene)") && checkCompiled.Contains("WorldReconstruction.Build(plan.Bundle, layout, coordinates, surface, frozen, navigationReport)") && rawReaders.Count == 0,
              "ActivationCommitStage.BuildWorld and GeneratedWorldLoader.CheckCompiled build the world from the report NavigationGate.ReadAt returns; no source reads a navigation record raw"
              + (rawReaders.Count == 0 ? "" : " (raw in " + String.Join(", ", rawReaders) + ")"));
        // MMR-247: the campaign map screen is MapScreen or a subclass; with War Sails it is NavalDLC.View.Map.NavalMapScreen (acc2). The
        // stand-in types below carry the installed names and the installed inheritance.
        Check(CampaignMapScreen.Is(new SandBox.View.Map.MapScreen()) && CampaignMapScreen.Is(new NavalDLC.View.Map.NavalMapScreen()) && CampaignMapScreen.Is(new NavalDLC.View.Map.NavalMapScreen())
              && !CampaignMapScreen.Is(null) && !CampaignMapScreen.Is(new object()) && !CampaignMapScreen.Is(new SandBox.View.Map.MapScreenNeighbour()),
              "the campaign map screen test accepts MapScreen and War Sails' NavalMapScreen subclass, and nothing else");
        string atmosphere = File.ReadAllText(Path.Combine(repo, "src/MNBL.ProceduralWorld/CampaignBootstrap/GeneratedMapAtmosphere.cs")).Replace("\r\n", "\n");
        var exact = new System.Text.RegularExpressions.Regex(@"FullName\s*[!=]=\s*""SandBox\.View\.Map\.MapScreen""");
        var exactUses = Directory.GetFiles(Path.Combine(repo, "src"), "*.cs", SearchOption.AllDirectories).Where(f => exact.IsMatch(File.ReadAllText(f))).Select(f => f.Substring(repo.Length + 1)).ToList();
        Check(exactUses.Count == 0 && Body(atmosphere, "internal static void Tick(float dt)").Contains("CampaignMapScreen.Is(screen)") && Body(runtime, "internal static void Tick()").Contains("CampaignMapScreen.Is(screen)"),
              "the atmosphere tick and the ruin hover find the map through CampaignMapScreen.Is; no source tests the map screen by its exact type name (" + (exactUses.Count == 0 ? "none" : String.Join(", ", exactUses)) + ")");
    }
}

// MMR-247: stand-ins with the installed names and inheritance (NavalDLC.View: NavalMapScreen extends SandBox.View.Map.MapScreen).
namespace SandBox.View.Map
{
    internal class MapScreen { }
    internal sealed class MapScreenNeighbour { }
}
namespace NavalDLC.View.Map
{
    internal sealed class NavalMapScreen : SandBox.View.Map.MapScreen { }
}
