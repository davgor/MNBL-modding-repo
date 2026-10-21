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

// MMR-289 magic feature tests, without the game:
//   MagicFeaturesTests REPO BUNDLE_A BUNDLE_B OUT_JSON GAME_ROOT
// On two real compiled worlds (A' seed 42, B' seed 73):
//   a1  every school's feature set (roads.json magicFeatures) names only meshes present in an installed TaleWorlds asset pack header and
//       particle effects defined in the installed Native module data; hidden schools take their look's set;
//   a2  the builder places features along the vein graph of the generated zones (a source feature per source, trunk features on wide nodes,
//       debris at forks, none at tips), deterministically; counts per school; no feature or part on water (or a water decal's opaque core), a
//       settlement pad, within the settlement clearance, within the ruin clearance of a ruin marker, on a road decal or a bridge (each checked
//       independently by brute force against the bundle's travel graph, the terrain-shaping pads and the map dressing plan); world and in-view
//       entity budgets kept, live features included (with the cast and with eight stacked casts); MMR-261's recorded Nerdun fire cast gets its
//       features (A'), each shown at its own reveal time at the overlay's reveal steps;
//   a3  lifetime through the installer over a recording target: created at load, a live cast's features added at the safe point (only its own),
//       revealed with its veins, moved on a seam refresh, released (natively, or with no target call after Scene.ClearAll), and re-created from the
//       stored mutation document on Continue with the same keys; the engine side wires this as the overlay's layer (source assertions).
// Evidence: OUT_JSON (evidence/world/magic-features.json).
internal static class MagicFeaturesTests
{
    private static int checks, failures;
    private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; Console.WriteLine((condition ? "ok    " : "FAIL  ") + label); }

    // Once Cleared is set (the engine ran Scene.ClearAll), every call fails and is counted: MMR-262's teardown must make none.
    private sealed class Recorder : IMagicFeatureTarget
    {
        public readonly Dictionary<int, double[]> Live = new Dictionary<int, double[]>();
        public readonly Dictionary<int, string> Kind = new Dictionary<int, string>();
        public int Next, AfterClear, Adds, Removes; public bool Cleared; public Func<string, bool> Resolves = m => true;
        private void Engine() { if (Cleared) { AfterClear++; throw new InvalidOperationException("an engine call on a cleared scene"); } }
        public object AddPart(FeaturePart part, double x, double y) { Engine(); if (!Resolves(part.Mesh)) return null; Live[++Next] = new[] { x, y }; Kind[Next] = part.Mesh; Adds++; return Next; }
        public void MovePart(object handle, FeaturePart part, double x, double y) { Engine(); Live[(int)handle] = new[] { x, y }; }
        public object AddEffect(MagicFeature f, double x, double y) { Engine(); Live[++Next] = new[] { x, y }; Kind[Next] = "fx"; Adds++; return Next; }
        public void MoveEffect(object handle, MagicFeature f, double x, double y) { Engine(); Live[(int)handle] = new[] { x, y }; }
        public void Remove(object handle) { Engine(); if (!Live.Remove((int)handle)) throw new InvalidOperationException("removed twice"); Removes++; }
    }

    private static int Main(string[] args)
    {
        var evidence = new Dictionary<string, object> { ["schema"] = "mnbl.magic-features-offline", ["version"] = 1, ["ticket"] = "MMR-289" };
        try { Run(args, evidence); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        evidence["checks"] = checks; evidence["failures"] = failures;
        File.WriteAllText(args[3], Serializer.Serialize(evidence), new UTF8Encoding(false));
        Console.WriteLine(checks - failures + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string[] args, Dictionary<string, object> evidence)
    {
        string repo = args[0], game = args[4];
        var roads = RoadsPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, RoadsPolicy.PolicyPath)));
        var magic = roads.Magic; var fp = roads.Features;
        var looks = SettlementLooksPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, SettlementLooksPolicy.PolicyPath)));
        var table = BiomeTable.FromBytes(File.ReadAllBytes(Path.Combine(repo, BiomeTable.PolicyPath)));
        var navigationPolicy = NavigationPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, NavigationPolicy.PolicyPath)));
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", WorldDataBundle.HostCapabilities);
        var shaping = (IDictionary<string, object>)Serializer.DeserializeObject(File.ReadAllText(Path.Combine(repo, "worldgen/policies/terrain-shaping.json")).TrimStart('﻿'));
        var padRadius = ((IDictionary<string, object>)((IDictionary<string, object>)shaping["pads"])["radius"]).ToDictionary(kv => kv.Key, kv => Convert.ToDouble(kv.Value, CultureInfo.InvariantCulture));
        var materials = (IDictionary<string, object>)Serializer.DeserializeObject(File.ReadAllText(Path.Combine(repo, BiomeTable.PolicyPath)).TrimStart('﻿'));
        double padMargin = Convert.ToDouble(((IDictionary<string, object>)((IDictionary<string, object>)materials["flora"])["clearance"])["padMargin"], CultureInfo.InvariantCulture);

        // ---- a1: the feature sets and their lineage (installed vanilla assets only) ----
        Check(fp.Schools.Count == 8 && magic.Schools.All(fp.Schools.ContainsKey) && table.HiddenLooks.Values.All(fp.Schools.ContainsKey)
              && fp.Schools.Values.All(s => s.Source != null && s.Trunk.Count > 0 && s.Debris.Count > 0),
              "roads.json magicFeatures: a source, trunk and debris set for each of the 8 visible schools; hidden schools take their look's (" + String.Join(", ", table.HiddenLooks.Select(kv => kv.Key + " -> " + kv.Value)) + ")");
        var meshes = fp.Schools.Values.SelectMany(s => new[] { s.Source }.Concat(s.Trunk).Concat(s.Debris)).SelectMany(f => f.Parts).SelectMany(p => p.Meshes).Distinct().OrderBy(m => m, StringComparer.Ordinal).ToList();
        var particles = fp.Schools.Values.SelectMany(s => s.Source.Particles).Distinct().OrderBy(m => m, StringComparer.Ordinal).ToList();
        var packs = PackIdentifiers(game, new HashSet<string>(meshes, StringComparer.Ordinal));
        var effects = EffectFiles(game, particles);
        var lineage = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var m in meshes) lineage[m] = new Dictionary<string, object> { ["kind"] = "meta mesh", ["packs"] = packs.ContainsKey(m) ? packs[m] : new List<string>(), ["bbox"] = new[] { fp.Meshes[m].Min, fp.Meshes[m].Max }, ["flip"] = fp.Meshes[m].Flip };
        foreach (var e in particles) lineage[e] = new Dictionary<string, object> { ["kind"] = "particle effect", ["files"] = effects.ContainsKey(e) ? effects[e] : new List<string>() };
        evidence["lineage"] = lineage;
        Check(meshes.All(packs.ContainsKey) && fp.Meshes.Keys.All(meshes.Contains),
              "every one of the " + meshes.Count + " meshes the sets name is in an installed TaleWorlds pack header (Modules/*/AssetPackages, EmAssetPackages; " + (meshes.Count - meshes.Count(packs.ContainsKey)) + " missing: " +
              String.Join(", ", meshes.Where(m => !packs.ContainsKey(m))) + "); every listed mesh is used");
        Check(particles.All(effects.ContainsKey) && fp.Schools.Values.All(s => s.Source.Light != null || s.Source.Particles.Count > 0),
              "every one of the " + particles.Count + " particle effects is defined in the installed module data (" + String.Join(", ", particles.Select(p => p + (effects.ContainsKey(p) ? "" : " MISSING"))) + "); each source carries a light or particles");
        Check(fp.SettlementClearance >= padRadius.Values.Max() + padMargin && fp.SettlementClearance >= looks.RuinMinGap,
              "the settlement clearance " + fp.SettlementClearance + " covers the largest terrain-shaping pad (" + padRadius.Values.Max() + " + flora padMargin " + padMargin + ") and the ruin markers' minGap " + looks.RuinMinGap);

        var calibration = (IDictionary<string, object>)Serializer.DeserializeObject(File.ReadAllText(Path.Combine(repo, "worldgen/calibration/naval-transitions/naval-transitions.json")).TrimStart('﻿'));
        var model = NavigationModel.FromCalibration(calibration);
        var worlds = new List<object>(); evidence["worlds"] = worlds;
        foreach (var label in new[] { "A", "B" })
        {
            var world = new Dictionary<string, object> { ["label"] = label }; worlds.Add(world);
            var bundle = WorldDataBundle.Open(label == "A" ? args[1] : args[2], host);
            world["bundle"] = bundle.WorldContentId;
            var surface = WorldSurface.FromBundle(bundle);
            var navigationInput = NavigationInput.FromBundle(bundle, surface, navigationPolicy);
            var navigation = RuntimeNavigationBuilder.Build(navigationInput, navigationPolicy);
            // The validator's report as activation and a load read it back (JSON: build.bridges as plain arrays).
            var report = (IDictionary<string, object>)Serializer.DeserializeObject(Serializer.Serialize(NavigationValidator.Run(NavigationPlan.Create(navigation, model), new MeshNavigationEngine(navigation.Mesh, model), "test_" + label.ToLowerInvariant(), null)));
            byte[] worldJson = bundle.ReadVerified("source/world.json");
            GeneratedClimateSystem.Source source;
            using (var stream = new MemoryStream(worldJson)) source = GeneratedClimateSystem.ReadSource(stream, GeneratedClimateSystem.Sha(worldJson));
            worldJson = null;
            var overlay = new WorldSurfaceOverlay(MagicGroundSource.From(source, surface, navigationInput.Cells, table));
            var src = overlay.Source;
            var dressing = MapDressing.Build(bundle, surface, report, source, roads, looks);
            var graph = (IDictionary<string, object>)Serializer.DeserializeObject(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/travel/travel-graph.json")));
            var campaign = (IDictionary<string, object>)Serializer.DeserializeObject(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/campaign/campaign.json")));
            var mutationSites = MutationSites.From(campaign, graph, source.Realms, surface.Strip, surface.PoleMargin);
            // As the runtime builds them: the mutation context's settlement points and the drawn map dressing plan.
            var sites = MagicFeatureSites.From(mutationSites.Point.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value), dressing);
            var settlements = Json.A(graph, "settlements").Cast<IDictionary<string, object>>().Where(s => s.ContainsKey("point"))
                .Select(s => { var p = Json.Point(s, "point"); return new { Id = Json.S(s, "id"), Kind = Json.S(s, "kind"), X = p[0] + surface.Strip, Y = p[1] + surface.PoleMargin }; }).ToList();

            var veins = MagicGround.Plan(overlay.Current, magic).Graph;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var plan = MagicFeatures.Plan(veins, fp, sites);
            double planMs = watch.Elapsed.TotalMilliseconds;
            var again = MagicFeatures.Plan(MagicGround.Plan(overlay.Current, magic).Graph, fp, MagicFeatureSites.From(mutationSites.Point.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value), MapDressing.Build(bundle, surface, report, source, roads, looks)));
            var bySchool = plan.Features.GroupBy(f => f.Look).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
            // Every school with land veins (at least 20 land nodes) stands; a school whose zones lie (almost) wholly on water has none (MMR-290's water).
            var landLooks = veins.Nodes.Where(n => !n.OnWater).GroupBy(n => n.Look).Where(g => g.Count() >= 20).Select(g => g.Key).ToList();
            var zoneLooks = veins.Zones.Select(z => z.Look).Distinct().OrderBy(l => l, StringComparer.Ordinal).ToList();
            world["landNodesByLook"] = veins.Nodes.Where(n => !n.OnWater).GroupBy(n => n.Look).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (object)g.Count());
            Check(plan.Features.Count > 0 && plan.Sha256() == again.Sha256() && landLooks.All(bySchool.ContainsKey),
                  label + ": two builds give the same " + plan.Features.Count + " features (" + plan.Sha256().Substring(0, 12) + "; " + plan.Entities + " entities, " + plan.Lights + " lights, " + plan.ParticleSystems + " particle systems); every school with land veins (20 or more land nodes) has features: " +
                  String.Join(", ", bySchool.Select(kv => kv.Key + " " + kv.Value)) + (zoneLooks.Any(l => !bySchool.ContainsKey(l)) ? "; none for " + String.Join(", ", zoneLooks.Where(l => !bySchool.ContainsKey(l)).Select(l => l + " (" + veins.Nodes.Count(n => n.Look == l && !n.OnWater) + " land nodes)")) : ""));

            // Roles along the graph.
            var sources = plan.Features.Where(f => f.Role == MagicFeature.Source).ToList();
            bool sourcesOk = sources.All(f =>
            {
                var n = veins.Nodes[f.Node]; int steps = 0;
                while (!n.IsSource) { n = veins.Nodes[n.Parent]; steps++; }
                return steps <= fp.SourceSearch && !veins.Nodes[f.Node].OnWater && f.X == veins.Nodes[f.Node].X && f.Y == veins.Nodes[f.Node].Y;
            }) && sources.Select(f => veins.Nodes[f.Node].Source).Distinct().Count() == sources.Count;
            int sourceSkips = plan.Skipped.Where(kv => kv.Key.StartsWith("source:", StringComparison.Ordinal)).Sum(kv => kv.Value);
            Check(sourcesOk && sources.Count + sourceSkips == veins.Sources.Count && sources.Count > 0,
                  label + ": one source feature per vein source (" + sources.Count + " of " + veins.Sources.Count + "; " + plan.SourcesMoved + " moved to a clear land node of their tree, " + plan.SourcesReduced + " reduced to a trunk feature with the source's light and particles, " + sourceSkips + " found none within " + fp.SourceSearch + ": " +
                  String.Join(", ", plan.Skipped.Where(kv => kv.Key.StartsWith("source:", StringComparison.Ordinal)).Select(kv => kv.Key.Substring(7) + " " + kv.Value)) + ")");
            var trunks = plan.Features.Where(f => f.Role == MagicFeature.Trunk).ToList(); var debris = plan.Features.Where(f => f.Role == MagicFeature.Debris).ToList();
            bool trunksOk = trunks.All(f => { var n = veins.Nodes[f.Node]; return !n.IsSource && n.Children.Count > 0 && n.Width >= fp.TrunkWidth && Dist(src, n.X, n.Y, f.X, f.Y) <= fp.LateralMax * n.Width / 2 + 1e-6; });
            bool debrisOk = debris.All(f => { var n = veins.Nodes[f.Node]; return n.Children.Count >= 2 && n.Width >= fp.DebrisWidth && f.X == n.X && f.Y == n.Y; });
            bool noTips = plan.Features.All(f => veins.Nodes[f.Node].Children.Count > 0 || f.Role == MagicFeature.Source || f.Live);
            bool gaps = true;
            for (int i = 0; i < plan.Features.Count && gaps; i++) for (int j = i + 1; j < plan.Features.Count; j++) if (Dist(src, plan.Features[i].X, plan.Features[i].Y, plan.Features[j].X, plan.Features[j].Y) < fp.MinGap - 1e-9) { gaps = false; break; }
            var trunkNodes = veins.Nodes.Where(n => !n.IsSource && !n.OnWater && n.Children.Count > 0 && n.Width >= fp.TrunkWidth).ToList();
            Check(trunksOk && debrisOk && noTips && gaps && trunks.Count > 0,
                  label + ": " + trunks.Count + " trunk features on non-tip nodes at least " + fp.TrunkWidth + " wide (" + trunkNodes.Count + " such land nodes), each within lateral x half its width of its node; " + debris.Count +
                  " debris at forks; none at a tip; every feature " + fp.MinGap + " units from every other");

            // ---- clear of water, pads, settlements, roads and bridges (brute force, independent of the planner's clearance) ----
            int wet = 0, onPad = 0, inClearance = 0, onRoad = 0, onBridge = 0, nearRuin = 0, inWaterCore = 0, partsInWaterBox = 0;
            var lakes = dressing.Lakes;
            foreach (var f in plan.Features)
            {
                var spots = f.Parts.Select(pt => new { pt.X, pt.Y, R = fp.Meshes[pt.Mesh].Radius * Scale(pt) }).ToList();
                // Ruin markers (MMR-257's hover-labelled prefabs): the footprint beyond the ruin clearance, and every part's disc.
                if (dressing.Markers.Any(m => Dist(src, m.X, m.Y, f.X, f.Y) < fp.RuinClearance + f.Radius - 1e-9 || spots.Any(sp => Dist(src, m.X, m.Y, sp.X, sp.Y) < fp.RuinClearance + sp.R))) nearRuin++;
                // Water decals (MMR-285): no part's disc reaches a stamp's opaque core (sampled on the disc); parts over a stamp's outer box are counted.
                if (dressing.Water.Any(d => spots.Any(sp => DiscInCore(src, d, dressing.WaterCore, sp.X, sp.Y, sp.R)))) inWaterCore++;
                partsInWaterBox += spots.Count(sp => dressing.Water.Any(d => SegmentDistance(src, d, sp.X, sp.Y) < d.HalfWidth + sp.R));
                if (spots.Any(sp => WetAt(src, lakes, sp.X, sp.Y)) || Enumerable.Range(0, 16).Any(k => WetAt(src, lakes, f.X + f.Radius * Math.Cos(k * Math.PI / 8), f.Y + f.Radius * Math.Sin(k * Math.PI / 8)))) wet++;
                if (settlements.Any(s => spots.Any(sp => Dist(src, s.X, s.Y, sp.X, sp.Y) < padRadius[s.Kind] + padMargin + sp.R))) onPad++;
                if (settlements.Any(s => Dist(src, s.X, s.Y, f.X, f.Y) < fp.SettlementClearance + f.Radius - 1e-9)) inClearance++;
                if (dressing.Decals.Any(d => spots.Any(sp => SegmentDistance(src, d, sp.X, sp.Y) < d.HalfWidth + sp.R))) onRoad++;
                if (dressing.Bridges.Any(b => spots.Any(sp => Dist(src, b.X, b.Y, sp.X, sp.Y) < MagicFeatures.BridgeHalfSegment + sp.R || Dist(src, b.BankA[0], b.BankA[1], sp.X, sp.Y) < sp.R || Dist(src, b.BankB[0], b.BankB[1], sp.X, sp.Y) < sp.R))) onBridge++;
            }
            var skips = plan.Skipped.Where(kv => !kv.Key.StartsWith("source:", StringComparison.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value);
            Check(wet == 0 && onPad == 0 && inClearance == 0 && onRoad == 0 && onBridge == 0 && nearRuin == 0 && inWaterCore == 0 && dressing.Decals.Count > 0 && settlements.Count > 0 && dressing.Markers.Count > 0 && dressing.Water.Count > 0,
                  label + ": 0 features on water (travel cells, water cores, ground below the datum, " + lakes.Count + " lake planes; " + inWaterCore + " reach the opaque core of the " + dressing.Water.Count + " water decals, " + partsInWaterBox + " parts lie over a stamp's outer box), " +
                  nearRuin + " within the " + fp.RuinClearance + "-unit ruin clearance of the " + dressing.Markers.Count + " ruin markers, " + onPad + " on the " + settlements.Count + " settlement pads, " + inClearance + " within the " + fp.SettlementClearance +
                  "-unit settlement clearance, " + onRoad + " on the " + dressing.Decals.Count + " road decals, " + onBridge + " on the " + dressing.Bridges.Count + " bridge segments (" + wet + " wet); candidates refused: " + String.Join(", ", skips.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + " " + kv.Value)));
            // MMR-247 (acc5 c30: 11 features stood within 5 units of a hideout): the settlements the campaign start places (hideouts, the retreat:
            // MutationSites.Placed) count as settlements. No offline harness builds the campaign start, so stand-ins at every third feature here.
            var placed = plan.Features.Where((f, i) => i % 3 == 0).Select(f => new[] { f.X, f.Y }).ToList();
            var withPlaced = MagicFeatures.Plan(veins, fp, MagicFeatureSites.From(mutationSites.Point.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value).Concat(placed), dressing));
            int nearPlaced = withPlaced.Features.Count(f => placed.Any(p => Dist(src, p[0], p[1], f.X, f.Y) < fp.SettlementClearance + f.Radius - 1e-9));
            Check(placed.Count > 0 && nearPlaced == 0 && withPlaced.Features.Count > 0,
                  label + ": the campaign start's settlements count: with " + placed.Count + " stand-in hideouts at every third feature, " + nearPlaced + " of " + withPlaced.Features.Count + " features (" + plan.Features.Count + " without them) stand within the " +
                  fp.SettlementClearance + "-unit settlement clearance of one");
            // Seam images: every part lies in the canonical frame; the frame wraps, so a feature by the seam keeps its parts together.
            bool canonical = plan.Features.All(f => f.X >= surface.Strip && f.X < surface.Strip + surface.LogicalWidth && f.Parts.All(pt => pt.X >= surface.Strip && pt.X < surface.Strip + surface.LogicalWidth && Dist(src, f.X, f.Y, pt.X, pt.Y) <= f.Radius + 1e-6));
            int seamFeatures = plan.Features.Count(f => f.Parts.Any(pt => Math.Abs(pt.X - f.X) > surface.LogicalWidth / 2));

            // ---- budget ----
            int[] window, close;
            bool withinCaps = Budget(plan, fp, surface, out window, out close);
            Check(withinCaps && canonical,
                  label + ": entity budget: " + plan.Roots + " roots / " + plan.Entities + " entities / " + plan.Lights + " lights / " + plan.ParticleSystems + " particle systems (caps " + fp.MaxRoots + " / " + fp.MaxEntities + " / " + fp.MaxLights + " / " + fp.MaxParticles +
                  "); busiest " + fp.WindowWidth + " x " + fp.WindowHeight + " window " + window[0] + " / " + window[1] + " / " + window[2] + " / " + window[3] + " (caps " + fp.WindowRoots + " / " + fp.WindowEntities + " / " + fp.WindowLights + " / " + fp.WindowParticles +
                  "), 58 x 37 window " + close[0] + " roots / " + close[1] + " entities; thinned " + String.Join(", ", plan.Thinned.Select(kv => kv.Key + " " + kv.Value)) + ", lights dropped " + plan.LightsDropped + "; every part in the canonical frame (" + seamFeatures + " features straddle the seam)");
            world["features"] = plan.Record(); world["planMs"] = Math.Round(planMs, 1);
            world["budget"] = new Dictionary<string, object> { ["roots"] = plan.Roots, ["entities"] = plan.Entities, ["lights"] = plan.Lights, ["particleSystems"] = plan.ParticleSystems,
                ["window"] = new[] { fp.WindowWidth, fp.WindowHeight }, ["windowMax"] = new Dictionary<string, object> { ["roots"] = window[0], ["entities"] = window[1], ["lights"] = window[2], ["particles"] = window[3] },
                ["closestViewWindow"] = new[] { 58, 37 }, ["closestViewMax"] = new Dictionary<string, object> { ["roots"] = close[0], ["entities"] = close[1], ["lights"] = close[2], ["particles"] = close[3] },
                ["frameCostEstimateMs"] = new Dictionary<string, object> { ["conservative"] = Math.Round(window[1] * 0.36e-3, 3), ["basis"] = "in-view entities x MMR-249's 0.36 us per in-view decal (upper bound; acc4 shows static mesh entities cheaper: vanilla Main_map runs ~20k more entities than world 1 at about the same main-thread time)" } };
            world["clearance"] = new Dictionary<string, object> { ["wet"] = wet, ["onPads"] = onPad, ["inSettlementClearance"] = inClearance, ["onRoads"] = onRoad, ["onBridges"] = onBridge, ["settlements"] = settlements.Count, ["roadDecals"] = dressing.Decals.Count,
                ["nearRuins"] = nearRuin, ["ruinMarkers"] = dressing.Markers.Count, ["inWaterDecalCore"] = inWaterCore, ["partsOverWaterDecalBox"] = partsInWaterBox, ["waterDecals"] = dressing.Water.Count,
                ["bridgeSegments"] = dressing.Bridges.Count, ["lakePlanes"] = lakes.Count, ["seamFeatures"] = seamFeatures };
            world["hiddenSchoolFeatures"] = plan.Features.Count(f => f.School != f.Look);
            world["veins"] = new Dictionary<string, object> { ["zones"] = veins.Zones.Count, ["sources"] = veins.Sources.Count, ["trunkNodes"] = trunkNodes.Count, ["graphSha256"] = veins.Sha256() };

            // ---- a3: lifetime over a recording target ----
            var recorder = new Recorder();
            var installer = new MagicFeatureInstaller(recorder);
            installer.Sync(plan.Revealed(Double.PositiveInfinity), x => x);
            bool loadOk = installer.Live == plan.Features.Count && recorder.Live.Count == plan.Entities && installer.LiveEntities == plan.Entities && installer.Errors.Count == 0;
            installer.Refresh(x => x + surface.LogicalWidth);
            bool moved = recorder.Live.Values.All(p => p[0] >= surface.Strip + surface.LogicalWidth) && installer.Moved == plan.Entities;
            installer.Refresh(x => x);
            var missing = new Recorder { Resolves = m => m != "icicle" };
            var partial = new MagicFeatureInstaller(missing);
            partial.Sync(plan, x => x);
            int icicles = plan.Features.Sum(f => f.Parts.Count(pt => pt.Mesh == "icicle"));
            Check(loadOk && moved && partial.MissingMeshes.SequenceEqual(new[] { "icicle" }) && partial.NotDrawn == icicles && partial.Errors.Count == 0 && missing.Live.Count == plan.Entities - icicles,
                  label + ": at load the installer draws all " + plan.Features.Count + " features as " + plan.Entities + " root entities and moves every one on a seam refresh; a mesh that does not resolve (icicle, " + icicles + " parts) draws nothing and the rest stand, with no error");
            if (label == "A")
            {
                // MMR-261's recorded Nerdun fire cast, from its stored document, applied at the safe point (MagicGroundRuntime.Set -> Changed).
                const string NerdunUid = "surface-city-2-13033-human_cold";
                string nerdun = mutationSites.CityTown[NerdunUid]; var at = mutationSites.Point[nerdun];
                var delta = MutationDelta.Read(File.ReadAllText(Path.Combine(repo, "tests/World/fixtures/mutation/nerdun-fire.delta.json"), new UTF8Encoding(false)), src, source.Seed);
                var edit = new MutationEdit { Day = 10, School = "fire", Site = nerdun, At = nerdun, Rule = "capital", Node = "mnbl-cast-fire-" + nerdun, Intensity = 4, City = NerdunUid, X = at[0], Y = at[1] };
                var castNodes = MutationOperations.Nodes(new[] { edit }).Select(kv => new MagicNode(kv.Key.School, kv.Key.X, kv.Key.Y)).ToList();
                var state = new MutationState { Producer = "p", World = source.Sha256, Request = "r", Day = 10, Edits = new List<MutationEdit> { edit }, Cells = delta.Cells, Nodes = castNodes };
                string stored = state.ToJson();
                var mutation = new MutationModel(new MutationModel.Context { Source = src, WorldSha256 = source.Sha256, RequestSha256 = "r" });
                List<MutationCell> cells; List<MagicNode> nodes;
                bool loaded = mutation.Load(stored, out cells, out nodes);
                double since = 10 * 24.0, grown = since + magic.RevealHours;
                overlay.Replace(cells, nodes);
                var castVeins = MagicGround.Plan(overlay.Current, magic).Graph;
                var castPlan = MagicFeatures.Plan(castVeins, fp, sites);
                var live = castPlan.Features.Where(f => f.Live).ToList();
                var liveSkips = castPlan.Skipped;
                int removedBefore = installer.Removed, createdBefore = installer.Created;
                var baseKeys = new HashSet<string>(plan.Features.Select(f => f.Key)); var castKeys = new HashSet<string>(castPlan.Features.Select(f => f.Key));
                int expectedTouched = plan.Features.Where(f => !castKeys.Contains(f.Key)).Sum(f => f.Entities) + castPlan.Features.Where(f => !baseKeys.Contains(f.Key) && f.RevealAt <= since).Sum(f => f.Entities);
                installer.Sync(castPlan.Revealed(since), x => x);   // the safe point, the cast's day
                int touched = (installer.Removed - removedBefore) + (installer.Created - createdBefore);
                var castSamples = new HashSet<int>(cells.Select(c => src.Index(c.Row, c.Column)));
                var keptZones = new HashSet<int>(veins.Zones.Where(z => !z.Samples.Any(castSamples.Contains)).Select(z => z.Index));
                // Outside the zones the cast's cells regrow every feature keeps its meshes and frames; only the shared light and particle caps may
                // pass an effect to another source.
                Func<MagicFeature, string> shape = f => f.Key.Substring(0, f.Key.LastIndexOf("|fx ", StringComparison.Ordinal));
                var castShapes = new HashSet<string>(castPlan.Features.Select(shape));
                bool othersKept = plan.Features.Where(f => keptZones.Contains(f.Zone)).All(f => castShapes.Contains(shape(f)));
                int effectsMoved = plan.Features.Count(f => keptZones.Contains(f.Zone) && castShapes.Contains(shape(f)) && !castKeys.Contains(f.Key));
                var steps = new[] { 0.0, 0.25, 0.5, 0.75, 1.0 }.Select(t => castPlan.Revealed(since + t * magic.RevealHours).Features.Count(f => f.Live)).ToList();
                bool growing = steps.Zip(steps.Skip(1), (a, b) => a <= b).All(x => x) && steps[4] == live.Count && castPlan.Revealed(since - 1).Features.Count(f => f.Live) == 0;
                bool liveClear = live.All(f => !settlements.Any(s => Dist(src, s.X, s.Y, f.X, f.Y) < fp.SettlementClearance + f.Radius - 1e-9) && !f.Parts.Any(pt => WetAt(src, lakes, pt.X, pt.Y)));
                var liveSource = live.FirstOrDefault(f => f.Role == MagicFeature.Source);
                // MMR-288: a generated node under a live cell is hidden (the live zone draws there): no feature stands on one.
                int hiddenNodes = castVeins.Nodes.Count(n => n.Hidden);
                bool noHidden = castPlan.Features.All(f => !castVeins.Nodes[f.Node].Hidden);
                Check(loaded && live.Count > 0 && liveClear && othersKept && touched == expectedTouched && growing && noHidden,
                      label + ": the recorded Nerdun fire cast (stored document, " + cells.Count(c => c.Variant >= 0) + " live cells, " + hiddenNodes + " generated nodes hidden under them, none with a feature) gets " + live.Count + " live features (" + String.Join(", ", live.GroupBy(f => f.Role).Select(g => g.Key + " " + g.Count())) + "; source " +
                      (liveSource == null ? "none (" + String.Join(", ", liveSkips.Where(kv => kv.Key.StartsWith("source:", StringComparison.Ordinal)).Select(kv => kv.Key + " " + kv.Value)) + ")" : "moved " + Math.Round(Dist(src, at[0], at[1], liveSource.X, liveSource.Y), 2) + " units from the capital") +
                      "), clear of every settlement and of water; at the safe point the installer touches " + touched + " entities, exactly the key difference (the cast's features, its regrown zones' and " + effectsMoved + " source effects the shared light and particle caps pass on); every other feature keeps its meshes and frames; shown outward with the veins over " + magic.RevealHours + " h: " + String.Join("/", steps));
                installer.Sync(castPlan.Revealed(grown), x => x);
                int liveEntities = recorder.Live.Count;
                var keysBefore = installer.DrawnFeatures().Select(kv => kv.Key.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
                // Release: the map's teardown after Scene.ClearAll lets go of every handle with no engine call; a native removal is the control.
                recorder.Cleared = true;
                int dropped = installer.Drop();
                bool releasedClean = recorder.AfterClear == 0 && installer.Live == 0 && dropped == liveEntities;
                var control = new Recorder(); var removing = new MagicFeatureInstaller(control);
                removing.Sync(castPlan, x => x); int controlDrawn = control.Live.Count; removing.Remove();
                // Continue: the stored document restores the live state (MagicGroundRuntime.Restore -> Begin -> Install), and the features come back.
                var restoredModel = new MutationModel(new MutationModel.Context { Source = src, WorldSha256 = source.Sha256, RequestSha256 = "r" });
                List<MutationCell> cells2; List<MagicNode> nodes2;
                bool reloaded = restoredModel.Load(stored, out cells2, out nodes2);
                var continued = new WorldSurfaceOverlay(src);
                continued.Replace(cells2, nodes2);
                var continuedPlan = MagicFeatures.Plan(MagicGround.Plan(continued.Current, magic).Graph, fp, sites);
                var fresh = new Recorder(); var reinstalled = new MagicFeatureInstaller(fresh);
                reinstalled.Sync(continuedPlan.Revealed(grown + 24 * 30), x => x);
                var keysAfter = reinstalled.DrawnFeatures().Select(kv => kv.Key.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
                Check(releasedClean && controlDrawn == castPlan.Entities && control.Live.Count == 0 && control.Removes == controlDrawn && reloaded && continuedPlan.Sha256() == castPlan.Sha256() && keysAfter.SequenceEqual(keysBefore) && fresh.Live.Count == liveEntities,
                      label + ": release after Scene.ClearAll drops all " + dropped + " entity handles with 0 engine calls (a native release removes all " + controlDrawn + "); Continue re-creates the same " + keysAfter.Count + " features (" + fresh.Live.Count + " entities, " +
                      castPlan.Features.Count(f => f.Live) + " live) from the stored mutation document");
                world["nerdunCast"] = new Dictionary<string, object> { ["liveCells"] = cells.Count, ["liveFeatures"] = live.Count, ["byRole"] = live.GroupBy(f => f.Role).ToDictionary(g => g.Key, g => (object)g.Count()),
                    ["liveEntities"] = live.Sum(f => f.Entities), ["revealed"] = steps, ["sourceDistanceFromCapital"] = liveSource == null ? (object)null : Math.Round(Dist(src, at[0], at[1], liveSource.X, liveSource.Y), 3),
                    ["wholePlanSkipped"] = castPlan.Skipped, ["hiddenNodes"] = hiddenNodes, ["touchedAtSafePoint"] = touched, ["effectsPassedOn"] = effectsMoved, ["capital"] = nerdun, ["since"] = since, ["revealHours"] = magic.RevealHours };
                // The budget holds with the cast (live features count inside every cap) and with eight stacked casts: the Nerdun delta and its
                // nodes repeated at every eighth of the globe's columns, wherever that lands.
                int[] castWindow, castClose, stackedWindow, stackedClose;
                bool castWithin = Budget(castPlan, fp, surface, out castWindow, out castClose);
                const int Casts = 8; int shift = src.Columns / Casts;
                var stackedCells = new List<MutationCell>(); var stackedNodes = new List<MagicNode>();
                for (int k = 0; k < Casts; k++)
                {
                    double dx = src.Width * k * shift / src.Columns;
                    foreach (var c in cells) stackedCells.Add(new MutationCell(c.Row, src.Wrap(c.Column + k * shift), c.Variant, c.Potency));
                    foreach (var n in nodes) stackedNodes.Add(new MagicNode(n.School, src.Canonical(n.X - src.Strip + dx), n.Y) { Since = n.Since });
                }
                var stackedOverlay = new WorldSurfaceOverlay(src);
                stackedOverlay.Replace(stackedCells, stackedNodes);
                var stackedPlan = MagicFeatures.Plan(MagicGround.Plan(stackedOverlay.Current, magic).Graph, fp, sites);
                bool stackedWithin = Budget(stackedPlan, fp, surface, out stackedWindow, out stackedClose);
                int stackedLive = stackedPlan.Features.Count(f => f.Live);
                bool stackedClear = stackedPlan.Features.Where(f => f.Live).All(f => !settlements.Any(s => Dist(src, s.X, s.Y, f.X, f.Y) < fp.SettlementClearance + f.Radius - 1e-9) && !f.Parts.Any(pt => WetAt(src, lakes, pt.X, pt.Y)));
                Check(castWithin && stackedWithin && stackedClear && stackedLive > live.Count,
                      label + ": live features count inside the caps (caps " + fp.MaxRoots + " / " + fp.MaxEntities + " / " + fp.MaxLights + " / " + fp.MaxParticles + ", window " + fp.WindowRoots + " / " + fp.WindowEntities + " / " + fp.WindowLights + " / " + fp.WindowParticles +
                      "): with the Nerdun cast " + Counts(castPlan, castWindow) + " (" + castPlan.LightsDropped + " lights, " + castPlan.ParticlesDropped + " particle systems dropped); with " + Casts + " stacked casts " + stackedLive + " live features, " + Counts(stackedPlan, stackedWindow) +
                      " (thinned " + String.Join(", ", stackedPlan.Thinned.Select(kv => kv.Key + " " + kv.Value)) + "; " + stackedPlan.LightsDropped + " lights, " + stackedPlan.ParticlesDropped + " particle systems dropped), every live feature clear of settlements and water");
                // Reveal steps: the runtime draws a layer at the overlay's reveal steps (MagicGroundRuntime.Advance at MagicGroundPlan.NextReveal).
                // Every live feature shows at its own RevealAt, also with every land stamp of its reveal time taken away (a feature node with no
                // land stamp, or above the cast's highest land Order).
                var revealTimes = new HashSet<double>(live.Where(f => f.RevealAt > since).Select(f => f.RevealAt));
                Func<MagicGroundPlan, HashSet<double>> schedule = gp => { var t = new HashSet<double>(); double h = gp.NextReveal(since); while (!Double.IsInfinity(h) && t.Count < 10000) { t.Add(h); h = gp.NextReveal(h); } return t; };
                var groundPlan = MagicGround.Plan(overlay.Current, magic);
                var bare = MagicGround.Plan(overlay.Current, magic);
                int stampsRemoved = bare.Stamps.RemoveAll(st => revealTimes.Contains(st.RevealAt));
                var stampOnly = new HashSet<double>(bare.Stamps.Where(st => st.RevealAt > since).Select(st => st.RevealAt));
                int lateByStamps = revealTimes.Count(t => !stampOnly.Contains(t));
                bool onTime = revealTimes.Count > 0 && revealTimes.All(schedule(groundPlan).Contains) && revealTimes.All(schedule(bare).Contains) && stampsRemoved > 0 && lateByStamps == revealTimes.Count;
                Check(onTime,
                      label + ": every live feature shows at its own reveal time (" + String.Join(", ", revealTimes.OrderBy(t => t)) + " h) at the overlay's reveal steps, also with the " + stampsRemoved + " land stamps of those times removed (by land stamps alone " + lateByStamps + " of them would show late or not in the session)");
                var cast = (Dictionary<string, object>)world["nerdunCast"];
                cast["budget"] = new Dictionary<string, object> { ["roots"] = castPlan.Roots, ["entities"] = castPlan.Entities, ["lights"] = castPlan.Lights, ["particleSystems"] = castPlan.ParticleSystems, ["windowMax"] = castWindow, ["lightsDropped"] = castPlan.LightsDropped, ["particlesDropped"] = castPlan.ParticlesDropped };
                cast["stackedCasts"] = new Dictionary<string, object> { ["casts"] = Casts, ["liveFeatures"] = stackedLive, ["roots"] = stackedPlan.Roots, ["entities"] = stackedPlan.Entities, ["lights"] = stackedPlan.Lights, ["particleSystems"] = stackedPlan.ParticleSystems,
                    ["windowMax"] = stackedWindow, ["thinned"] = stackedPlan.Thinned, ["lightsDropped"] = stackedPlan.LightsDropped, ["particlesDropped"] = stackedPlan.ParticlesDropped };
                cast["revealSteps"] = new Dictionary<string, object> { ["featureRevealHours"] = revealTimes.OrderBy(t => t).ToList(), ["landStampsRemoved"] = stampsRemoved, ["lateByLandStampsAlone"] = lateByStamps };
                overlay.Replace(null, null);
            }
        }

        // The engine side: the overlay's layer, its lifetime calls and its settlement table (source assertions; MMR-247 runs it in game).
        string runtime = Text(repo, "src/MNBL.ProceduralWorld/World/MagicFeaturesRuntime.cs"), ground = Text(repo, "src/MNBL.ProceduralWorld/World/MagicGroundRuntime.cs");
        string release = Body(runtime, "public void Release(bool native)"), install = Body(runtime, "public void Install(");
        Check(System.Text.RegularExpressions.Regex.IsMatch(ground, @"Layers\s*=\s*new\s+List<IMagicVeinLayer>\s*\{\s*MagicFeaturesRuntime\.Layer\b[^}]*\}") && release.Contains("if (native) drawn.Remove(); else drawn.Drop();") && install.Contains("Release(false);")
              && install.Contains("ReferenceEquals(c.Source, veins.Source)") && install.Contains("MapDressingRuntime.Current") && install.Contains("if (dressing == null) { record[\"status\"] = \"none: no map dressing plan") && runtime.Contains("GameEntity.CreateEmpty(scene, false, true, true)") && !runtime.Contains("GameEntity.Instantiate(")
              && Body(ground, "internal static void ReleaseHandles(").Contains("layer.Release(false)") && Body(ground, "internal static void Install(").Contains("layer.Install(scene, full.Graph, clock.Hours, Image)")
              && install.Contains(".Concat(context.Sites.Placed)") && Text(repo, "src/MNBL.ProceduralWorld/Generation/WorldReconstruction.cs").Contains("foreach (var h in dataset.Start.Hideouts) mutation.Sites.Placed.Add("),
              "engine side: MagicFeaturesRuntime is the overlay's registered layer (installed after the decals with the map's full vein graph and campaign time, changed at each live change and reveal step, released natively on the live scene and by handle after Scene.ClearAll); " +
              "it keeps clear of this overlay's settlement table (with the hideouts and the retreat the campaign start places, MMR-247) and the drawn map dressing, and places nothing without either; it makes separate root entities (no prefab, no physics body)");
    }

    // ---- helpers ----
    // Whether a plan keeps every world and in-view cap of the policy (live features included); the busiest windows' counters out.
    private static bool Budget(MagicFeaturePlan plan, RoadsPolicy.FeaturesPolicy fp, WorldSurface surface, out int[] window, out int[] close)
    {
        var points = plan.Features.Select(f => new[] { f.X, f.Y, 1, f.Entities, f.Light != null ? 1 : 0, f.Particles.Count }).ToList();
        window = WindowMax(points, surface, fp.WindowWidth, fp.WindowHeight); close = WindowMax(points, surface, 58, 37);
        return plan.Roots <= fp.MaxRoots && plan.Entities <= fp.MaxEntities && plan.Lights <= fp.MaxLights && plan.ParticleSystems <= fp.MaxParticles && window[0] <= fp.WindowRoots && window[1] <= fp.WindowEntities
               && window[2] <= fp.WindowLights && window[3] <= fp.WindowParticles;
    }
    private static string Counts(MagicFeaturePlan plan, int[] window) { return plan.Roots + " roots / " + plan.Entities + " entities / " + plan.Lights + " lights / " + plan.ParticleSystems + " particle systems, busiest window " + String.Join(" / ", window); }
    // Whether a disc of radius r at (x, y) reaches a water decal's opaque core: its centre, or one of 32 points on rings at r/2 and r, inside it.
    private static bool DiscInCore(MagicGroundSource src, RoadDecal d, double core, double x, double y, double r)
    {
        if (Dist(src, d.X, d.Y, x, y) > Math.Max(d.HalfLength, d.HalfWidth) * core + r) return false;
        if (d.Covers(x, y, src.Width, core)) return true;
        for (int k = 0; k < 16; k++) foreach (double q in new[] { r / 2, r }) if (d.Covers(x + q * Math.Cos(k * Math.PI / 8), y + q * Math.Sin(k * Math.PI / 8), src.Width, core)) return true;
        return false;
    }
    private static double Scale(FeaturePart p) { var b = p.Basis; return Math.Sqrt(b[6] * b[6] + b[7] * b[7] + b[8] * b[8]); }
    private static double Dist(MagicGroundSource src, double ax, double ay, double bx, double by) { double dx = bx - ax; dx -= Math.Round(dx / src.Width) * src.Width; double dy = by - ay; return Math.Sqrt(dx * dx + dy * dy); }
    private static double SegmentDistance(MagicGroundSource src, RoadDecal d, double x, double y)
    {
        double dx = x - d.X; dx -= Math.Round(dx / src.Width) * src.Width; double dy = y - d.Y, c = Math.Cos(d.Yaw), s = Math.Sin(d.Yaw);
        double t = Math.Max(-d.HalfLength, Math.Min(d.HalfLength, dx * c + dy * s));
        return Math.Sqrt(Math.Pow(dx - t * c, 2) + Math.Pow(dy - t * s, 2));
    }
    private static bool WetAt(MagicGroundSource src, IList<GeneratedWaterSystem.Lake> lakes, double x, double y)
    {
        double cx = src.Canonical(x - src.Strip);
        int cell = src.CellAt(cx, y);
        if (cell < 0 || TravelCells.Water(src.Cells.Classes[cell])) return true;
        int v = src.Variant[src.SampleAt(cx, y)];
        if (v >= 0 && MagicVeins.WaterCore(src, src.Catalogue[v].Core)) return true;
        double ground = src.Ground(cx, y);
        return ground < GeneratedWaterSystem.DatumZ || MapDressing.PlaneCover(lakes, cx, y, ground) != null;
    }
    // The most of each counter (roots, entities, lights, particles) in any window of w x h units (stepped by a quarter window, columns wrapping).
    private static int[] WindowMax(List<double[]> points, WorldSurface surface, double w, double h)
    {
        var best = new int[4];
        for (double x0 = surface.Strip; x0 < surface.Strip + surface.LogicalWidth; x0 += w / 4)
            for (double y0 = surface.PoleMargin - h / 2; y0 < surface.PoleMargin + surface.LogicalHeight; y0 += h / 4)
            {
                var n = new int[4];
                foreach (var p in points)
                {
                    double dx = p[0] - x0; dx -= Math.Floor(dx / surface.LogicalWidth) * surface.LogicalWidth;
                    if (dx < w && p[1] >= y0 && p[1] < y0 + h) for (int i = 0; i < 4; i++) n[i] += (int)p[2 + i];
                }
                for (int i = 0; i < 4; i++) best[i] = Math.Max(best[i], n[i]);
            }
        return best;
    }
    private static string Text(string repo, string path) { return File.ReadAllText(Path.Combine(repo, path)).Replace("\r\n", "\n"); }
    private static string Body(string source, string signature)
    {
        int begin = source.IndexOf(signature, StringComparison.Ordinal), end = begin < 0 ? -1 : source.IndexOf("\n        }\n", begin, StringComparison.Ordinal);
        return begin < 0 || end < 0 ? "" : source.Substring(begin, end - begin);
    }

    // The packs whose header names each mesh as a length-prefixed identifier (the asset survey's method: a .tpac starts "TPAC", its header length
    // at 0x1C). Read only.
    private static Dictionary<string, List<string>> PackIdentifiers(string game, HashSet<string> names)
    {
        var hits = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string modules = Path.Combine(game, "Modules");
        var wanted = names.ToDictionary(n => n, n => { var b = Encoding.ASCII.GetBytes(n); var r = new byte[b.Length + 4]; BitConverter.GetBytes(b.Length).CopyTo(r, 0); b.CopyTo(r, 4); return r; });
        foreach (var dir in Directory.GetDirectories(modules))
            foreach (var sub in new[] { "AssetPackages", "EmAssetPackages" })
            {
                string d = Path.Combine(dir, sub);
                if (!Directory.Exists(d)) continue;
                foreach (var pack in Directory.GetFiles(d, "*.tpac").OrderBy(p => p, StringComparer.Ordinal))
                {
                    byte[] header;
                    using (var f = File.OpenRead(pack))
                    {
                        var head = new byte[0x24];
                        if (f.Read(head, 0, head.Length) != head.Length || Encoding.ASCII.GetString(head, 0, 4) != "TPAC") continue;
                        long length = BitConverter.ToUInt32(head, 0x1C);
                        if (length <= 0 || length > 256L * 1024 * 1024) continue;
                        header = new byte[0x24 + length]; f.Seek(0, SeekOrigin.Begin);
                        int read = 0; while (read < header.Length) { int n = f.Read(header, read, header.Length - read); if (n <= 0) break; read += n; }
                    }
                    string rel = Path.GetFileName(dir) + "/" + sub + "/" + Path.GetFileName(pack);
                    foreach (var kv in wanted)
                        if (IndexOf(header, kv.Value) >= 0) { List<string> l; if (!hits.TryGetValue(kv.Key, out l)) hits[kv.Key] = l = new List<string>(); l.Add(rel); }
                }
            }
        return hits;
    }
    private static int IndexOf(byte[] hay, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= hay.Length; i++)
        {
            if (hay[i] != needle[0]) continue;
            int k = 1; while (k < needle.Length && hay[i + k] == needle[k]) k++;
            if (k == needle.Length) return i;
        }
        return -1;
    }
    // The Native module data files that define each particle effect (<effect name="...").
    private static Dictionary<string, List<string>> EffectFiles(string game, List<string> effects)
    {
        var hits = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string data = Path.Combine(game, "Modules", "Native", "ModuleData");
        foreach (var file in Directory.GetFiles(data, "*.xml", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            string text = File.ReadAllText(file);
            foreach (var e in effects)
                if (System.Text.RegularExpressions.Regex.IsMatch(text, "<effect\\s+name=\"" + System.Text.RegularExpressions.Regex.Escape(e) + "\"")) { List<string> l; if (!hits.TryGetValue(e, out l)) hits[e] = l = new List<string>(); l.Add(file.Substring(data.Length + 1).Replace('\\', '/')); }
        }
        return hits;
    }
}
