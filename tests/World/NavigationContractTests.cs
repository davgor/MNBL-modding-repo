using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Surface;

// MMR-38 navigation contract tests, without the game:
//   NavigationContractTests REPO BUNDLE_A BUNDLE_B GAME_ROOT OUT_DIR OUT_JSON
// On two real compiled worlds, the navigation fixture and the fault variants: the installed navigation model against the MMR-17
// calibration; the built navmesh (topology invariants, encode/decode, the independent Python codec, face counts, typed faces from the
// installed enum, strips, grounding); the mesh's components against the travel graph's groups and basins; snapping; the static route
// matrix; the validator through a managed stand-in engine (logic only: the engine evidence is the in-game world_navigation probe), including
// its refusals; the activation gate; report and navmesh determinism; an L-shaped bridge.
internal static class NavigationContractTests
{
    private static int checks, failures;
    private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; Console.WriteLine((condition ? "ok    " : "FAIL  ") + label); }
    private static string gameRoot;

    private static int Main(string[] args)
    {
        if (args.Length != 6) { Console.Error.WriteLine("usage: NavigationContractTests REPO BUNDLE_A BUNDLE_B GAME_ROOT OUT_DIR OUT_JSON"); return 2; }
        gameRoot = args[3];
        AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            foreach (var dir in new[] { "bin/Win64_Shipping_Client", "Modules/NavalDLC/bin/Win64_Shipping_Client", "Modules/SandBox/bin/Win64_Shipping_Client" })
            {
                string path = Path.Combine(gameRoot, dir, name);
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = "mnbl.evidence.navigation-contract", ["version"] = 1, ["ticket"] = "MMR-38" };
        try { Run(args, evidence); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        evidence["checks"] = checks; evidence["failures"] = failures;
        File.WriteAllText(args[5], Serializer.Serialize(evidence), new UTF8Encoding(false));
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run(string[] args, SortedDictionary<string, object> evidence)
    {
        string repo = args[0], outDir = args[4];
        Directory.CreateDirectory(outDir);
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", new[] { "R01", "R02", "R03", "R04", "R05", "R06", "R07", "R08", "R09", "R10" });
        var policy = NavigationPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, NavigationPolicy.PolicyPath)));
        var calibration = (IDictionary<string, object>)Serializer.DeserializeObject(File.ReadAllText(Path.Combine(repo, "worldgen/calibration/naval-transitions/naval-transitions.json")).TrimStart('﻿'));
        evidence["policySha256"] = policy.Sha256;

        // The installed navigation model (constructed the way NavalDLC registers it) against the MMR-17 observed campaign values.
        var observed = NavigationModel.FromCalibration(calibration);
        NavigationModel model;
        try { model = NavigationModel.Installed(calibration); }
        catch (Exception e) { model = null; evidence["installedModelError"] = e.GetType().Name + ": " + e.Message; }
        Check(model != null && model.SameAs(observed) && model.SameRegions(observed.RegionMap),
              "the installed War Sails navigation model (" + (model == null ? "not constructible offline" : model.Source) + ") has the MMR-17 campaign's invalid terrain types for Default, Naval and All, region switch costs, embark threshold and land region");
        if (model == null) model = observed;
        evidence["model"] = model.Record();
        Check(TerrainTypes.Id("Plain") == 1 && TerrainTypes.Id("UnderBridge") == 25 && TerrainTypes.Name(17) == "Bridge" && Throws<InvalidDataException>(() => TerrainTypes.Id("7")) && Throws<InvalidDataException>(() => TerrainTypes.Id("Marsh")),
              "terrain types resolve by name through the installed enum only (a numeral or an unknown name is refused)");

        var worlds = new List<object>(); evidence["worlds"] = worlds;
        NavigationInput inputA = null; NavigationBuild buildA = null; NavigationPlan planA = null;
        foreach (var label in new[] { "A", "B" })
        {
            var bundle = WorldDataBundle.Open(label == "A" ? args[1] : args[2], host);
            string id = bundle.WorldContentId.Substring(0, 12);
            var surface = WorldSurface.FromBundle(bundle);
            var input = NavigationInput.FromBundle(bundle, surface, policy);
            var build = RuntimeNavigationBuilder.Build(input, policy);
            var record = new Dictionary<string, object> { ["label"] = label, ["worldContentId"] = bundle.WorldContentId, ["build"] = build.Record() };
            worlds.Add(record);
            var t = input.Cells; var mesh = build.Mesh;
            string navPath = Path.Combine(outDir, "navmesh-" + label + ".bin"); File.WriteAllBytes(navPath, build.Bytes);

            Check(build.Diagnostics.Count == 0 && mesh.Validate().Count == 0,
                  label + " " + id + ": the navmesh satisfies the installed campaign maps' invariants (every side its edge record, counter-clockwise, strictly convex, degree 3-4, manifold, opposite traversal, one record per vertex pair, no unreferenced vertex): "
                  + mesh.FaceCount + " faces, " + mesh.Vertices.Count + " vertices, " + mesh.Edges.Count + " edges, " + build.Bridges.Count + " bridges");
            var decoded = NavMesh.Decode(build.Bytes);
            bool roundTrip = decoded.FaceCount == mesh.FaceCount && decoded.Groups.SequenceEqual(mesh.Groups) && decoded.Edges.Count == mesh.Edges.Count
                && Enumerable.Range(0, mesh.FaceCount).All(f => decoded.FaceVertices[f].SequenceEqual(mesh.FaceVertices[f]) && decoded.FaceEdges[f].SequenceEqual(mesh.FaceEdges[f]))
                && Enumerable.Range(0, mesh.Vertices.Count).All(v => Enumerable.Range(0, 3).All(k => (float)mesh.Vertices[v][k] == (float)decoded.Vertices[v][k]));
            decoded.CellSize = mesh.CellSize;
            Check(roundTrip && decoded.Encode().SequenceEqual(build.Bytes), label + ": navmesh.bin (RNM1, compression 3, one LZ4 block, NMG9 with the campaign-map face metadata 2/0/group/0/-1, flag 0, standard tail) decodes to the built mesh and re-encodes byte for byte");
            var python = Inspect(repo, navPath);
            var validation = python == null ? null : (IDictionary<string, object>)python["validation"];
            var counts = validation == null ? null : (IDictionary<string, object>)validation["counts"];
            var pyGroups = python == null ? null : (IDictionary<string, object>)python["groups"];
            Check(validation != null && (bool)validation["ok"] && ((object[])validation["errors"]).Length == 0 && ((object[])validation["warnings"]).Length == 0 && Convert.ToInt32(counts["faces"]) == mesh.FaceCount
                  && pyGroups.All(kv => mesh.Groups.Count(g => g.ToString(CultureInfo.InvariantCulture) == kv.Key) == Convert.ToInt32(kv.Value)),
                  label + ": the independent Python codec (tools/scenebin/navmesh_bin.py inspect) decodes and validates the file with no error or warning, " + (counts == null ? "?" : counts["faces"] + " faces, " + counts["islands"] + " islands") + ", the same groups");

            // Face count: one quad per physical cell between the pole lines; a bank cell beside a deck is a fan of 4 + 2 per split side
            // triangles; a deck cell adds one deck face.
            int expectedFaces = build.PhysicalColumns * t.Rows;
            for (int pr = t.RowOffset; pr < t.RowOffset + t.Rows; pr++) for (int pc = 0; pc < build.PhysicalColumns; pc++)
            {
                var faces = mesh.CellFaces[NavMesh.Key(pr, pc)];
                int banks = faces.Count(f => mesh.Roles[f] == NavMesh.RoleBank), decks = faces.Count(f => mesh.Roles[f] == NavMesh.RoleDeck);
                if (banks > 0) expectedFaces += banks - 1;
                expectedFaces += decks;
            }
            int decksExpected = 0;
            foreach (var b in build.Bridges) foreach (var c in b.Cells) for (int pc = 0; pc < build.PhysicalColumns; pc++) if (t.Wrap(pc - t.ColumnOffset) == c[1]) decksExpected++;
            Check(mesh.FaceCount == expectedFaces && mesh.Roles.Count(r => r == NavMesh.RoleDeck) == decksExpected && build.PhysicalColumns == 256 && t.Rows == 64 && mesh.FaceCount <= 32768,
                  label + ": " + build.PhysicalColumns + " x " + t.Rows + " physical cells (one quad each; strips included, no face beyond the pole lines), bank fans and one deck face per bridge cell image (" + decksExpected
                  + "): " + mesh.FaceCount + " faces, within MMR-21's provisional 32,768-face navigation-cache limit");
            // Typed faces: each class's installed TerrainType; rivers and lakes are land-passable only as bridge decks (no fording).
            bool typed = true, strips = true, grounded = true;
            for (int pr = t.RowOffset; pr < t.RowOffset + t.Rows && typed; pr++) for (int pc = 0; pc < build.PhysicalColumns; pc++)
            {
                var cell = build.Cells[t.Index(pr - t.RowOffset, pc - t.ColumnOffset)];
                foreach (int f in mesh.CellFaces[NavMesh.Key(pr, pc)])
                {
                    int g = mesh.Groups[f]; byte role = mesh.Roles[f];
                    if (role == NavMesh.RoleDeck ? g != build.FaceGroups["bridge"] : g != cell.TerrainType) typed = false;
                    if (role != NavMesh.RoleDeck && cell.Passage == "underBridge" && g != build.FaceGroups["underBridge"]) typed = false;
                }
                int canonical = t.ColumnOffset + t.Wrap(pc - t.ColumnOffset);
                var a = mesh.CellFaces[NavMesh.Key(pr, pc)].Select(f => mesh.Groups[f]).OrderBy(x => x); var b = mesh.CellFaces[NavMesh.Key(pr, canonical)].Select(f => mesh.Groups[f]).OrderBy(x => x);
                if (!a.SequenceEqual(b) && mesh.CellFaces[NavMesh.Key(pr, pc)].All(f => mesh.Roles[f] != NavMesh.RoleBank && mesh.Roles[f] != NavMesh.RoleDeck)) strips = false;
            }
            for (int pr = t.RowOffset; pr <= t.RowOffset + t.Rows; pr++) for (int pc = 0; pc <= build.PhysicalColumns; pc++)
            {
                var v = mesh.FacesAt(pc * t.Spacing, pr * t.Spacing, g => true).Select(f => mesh.FaceVertices[f]).SelectMany(x => x)
                    .Select(i => mesh.Vertices[i]).FirstOrDefault(p => p[0] == pc * t.Spacing && p[1] == pr * t.Spacing);
                if (v == null) continue;
                double expected = Math.Max(surface.CollisionHeight(Math.Min(pc * t.Spacing, surface.Extent), pr * t.Spacing), policy.SeaDatum) + policy.ZOffset;
                if (Math.Abs(v[2] - expected) > 1e-9) grounded = false;
            }
            Check(typed && strips && grounded && mesh.Groups.All(g => TerrainTypes.Name(g) != null) && build.Cells.Where(c => c.Class == TravelCells.River && c.Passage != "underBridge").All(c => c.TerrainType == TerrainTypes.Id(input.FaceGroupNames["river"])),
                  label + ": every face carries its cell's installed TerrainType (" + String.Join(", ", build.FaceGroups.Select(kv => kv.Key + " " + TerrainTypes.Name(kv.Value))) + "), deck faces Bridge over UnderBridge cells, rivers River elsewhere (no fording); strip cells repeat their canonical cell; lattice vertices are grounded on the collision reference (max with the sea datum) + " + policy.ZOffset);

            var plan = NavigationPlan.Create(build, model);
            record["static"] = plan.StaticRecord();
            Check(!plan.Diagnostics.Any(d => d.Code == "navigation.topology_mismatch" || d.Code.StartsWith("navigation.group", StringComparison.Ordinal) || d.Code == "navigation.bridge_missing"),
                  label + ": the cells reproduce the travel graph (class counts, the MMR-28 OpenSea rule recomputed, " + input.LandComponentCells.Count + " land components and " + input.Basins.Count + " basins cell for cell), every group is one Default component and every basin one Naval component of the mesh, every joining crossing a deck");
            var snaps = (Dictionary<string, object>)plan.StaticRecord()["snaps"];
            bool portsInCell = plan.Points.Where(p => p.Snap == "placed toward its gate").All(p => t.CellOf(p.Resolved[0], p.Resolved[1]).SequenceEqual(t.CellOf(p.Requested[0], p.Requested[1])));
            Check(plan.Points.Count > 0 && plan.Points.All(p => p.Resolved != null) && Convert.ToDouble(snaps["maxDistance"]) <= policy.SnapRadius * t.Spacing && portsInCell,
                  label + ": every required point (" + plan.Points.Count + ": settlements, ports, port entrances, coast transitions) resolves in its intended domain and component: " + snaps["kept"] + " kept, " + snaps["moved"] + " moved, "
                  + snaps["separated"] + " separated (at most " + snaps["maxDistance"] + " units), " + snaps["portsPlaced"] + " ports placed toward their gates inside their own cells (at most " + snaps["maxPortPlacement"] + " units)");
            Check(plan.Activatable, label + ": the static route matrix holds on the mesh (" + plan.Routes.Count(r => r.Expected) + " reachable and " + plan.Routes.Count(r => !r.Expected) + " forbidden routes, " + plan.Faces.Count + " face checks, "
                  + input.HostChecks.Count + " MMR-28 host checks)" + (plan.Activatable ? "" : ": " + String.Join("; ", plan.Diagnostics.Take(6))));
            var report = NavigationValidator.Run(plan, new MeshNavigationEngine(build.Mesh, model), "test_" + label, null);
            var summary = (Dictionary<string, object>)report["summary"];
            Check((bool)report["activatable"], label + ": the validator over the managed stand-in engine agrees (" + summary["routesAgree"] + "/" + summary["routeChecks"] + " routes, " + summary["facesAgree"] + "/" + summary["faceChecks"] + " faces)"
                  + ((bool)report["activatable"] ? "" : ": " + String.Join("; ", Failures(report).Take(6).Select(x => x["subject"] + " " + x["detail"]))));
            record["standInReport"] = new Dictionary<string, object> { ["activatable"] = report["activatable"], ["summary"] = summary, ["reportSha256"] = report["reportSha256"] };
            // Determinism: a fresh build and validation give the same navmesh and report.
            var again = RuntimeNavigationBuilder.Build(NavigationInput.FromBundle(bundle, WorldSurface.FromBundle(bundle), policy), policy);
            var againReport = NavigationValidator.Run(NavigationPlan.Create(again, model), new MeshNavigationEngine(again.Mesh, model), "test_" + label, null);
            Check(again.Sha256 == build.Sha256 && (string)againReport["reportSha256"] == (string)report["reportSha256"], label + ": the navmesh (" + build.Sha256.Substring(0, 16) + ") and the semantic report are deterministic");
            // Fault variants: refused statically with the source ids.
            foreach (var fault in NavigationVariants.Faults)
            {
                var faulted = NavigationVariants.Apply(fault, input, policy);
                var faultPlan = NavigationPlan.Create(RuntimeNavigationBuilder.Build(faulted, policy), model);
                var moved = faulted.Settlements.FirstOrDefault(s => { var o = input.Settlements.First(x => x.Id == s.Id); return o.X != s.X || o.Y != s.Y; });
                bool named;
                if (fault == "fault:no-bridges")
                    named = input.Crossings.Count == 0 || input.Crossings.All(x => faultPlan.Diagnostics.Any(d => d.Code == "navigation.bridge_missing" && d.Subject == x.Subject)) && faultPlan.Diagnostics.Any(d => d.Code == "navigation.group_not_realized");
                else named = moved != null && faultPlan.Diagnostics.Count == 1 && faultPlan.Diagnostics[0].Code == "navigation.snap_refused" && faultPlan.Diagnostics[0].SourceIds.Contains(moved.Id) && faultPlan.Diagnostics[0].SourceIds.Contains(moved.Uid);
                Check(!faultPlan.Activatable && named, label + " " + fault + ": refused before activation with source-id diagnostics: " + String.Join("; ", faultPlan.Diagnostics.Take(3)) + (faultPlan.Diagnostics.Count > 3 ? "; ... (" + faultPlan.Diagnostics.Count + ")" : ""));
                record[fault] = faultPlan.Diagnostics.Select(d => d.Record()).ToList();
            }
            if (label == "A") { inputA = input; buildA = build; planA = plan; }
        }

        // The engine fault on world A (bridge faces disabled): the validator refuses, naming the crossings.
        var faultedEngine = new MeshNavigationEngine(buildA.Mesh, model); faultedEngine.Disable(buildA.FaceGroups["bridge"]);
        var faultReport = NavigationValidator.Run(planA, faultedEngine, "test_A_fault", null);
        var faultFailures = Failures(faultReport).ToList();
        Check(!(bool)faultReport["activatable"] && buildA.Bridges.All(b => b.Subjects.All(s => faultFailures.Any(f => (string)f["subject"] == "bridge crossing " + s && ((string[])f["sourceIds"]).Contains(s)))),
              "A with Bridge faces disabled in the engine: not activatable; every bridge crossing route fails naming its crossing (" + faultFailures.Count + " failures)");

        // The detour bound by route type (2026-10-19): an All route takes routes.maxStaticFactorAll (sqrt 2), the others maxStaticFactor (1.25).
        // An engine that lengthens every path by a factor stands in for the native route over the square sea faces (seed 43's world: 1.29). The
        // routes are over 100 units, so the bound's slack (half a cell) does not decide.
        Check(policy.MaxStaticFactor == 1.25 && Math.Abs(policy.MaxStaticFactorAll - Math.Sqrt(2)) < 1e-6, "the policy bounds All routes at sqrt 2 x the mesh's shortest path, the others at 1.25");
        Func<double, RouteCheck, bool> detour = (factor, route) =>
            NavigationValidator.EvaluateRoute(planA, new StretchedEngine(new MeshNavigationEngine(buildA.Mesh, model), factor), route, null).Problems.Any(x => x.EndsWith("(a detour)", StringComparison.Ordinal));
        var allRoute = planA.Routes.FirstOrDefault(r => r.Type == "All" && r.Expected && !double.IsNaN(r.StaticLength) && r.StaticLength > 100);
        var landRoute = planA.Routes.FirstOrDefault(r => r.Type == "Default" && r.Expected && !double.IsNaN(r.StaticLength) && r.StaticLength > 100);
        Check(allRoute != null && landRoute != null && !detour(1.0, allRoute) && !detour(1.3, allRoute) && detour(1.5, allRoute) && !detour(1.0, landRoute) && detour(1.3, landRoute),
              "an All route " + (allRoute == null ? "" : allRoute.Id) + " 1.3 x its mesh path passes and 1.5 x is a detour; a Default route " + (landRoute == null ? "" : landRoute.Id) + " 1.3 x is a detour");

        // The fixture world: designed expectations, production builder and validator.
        var fixtureInput = NavigationVariants.Apply(NavigationFixtures.Variant, inputA, policy);
        var fixture = RuntimeNavigationBuilder.Build(fixtureInput, policy);
        var fixturePlan = NavigationPlan.Create(fixture, model);
        File.WriteAllBytes(Path.Combine(outDir, "navmesh-fixture.bin"), fixture.Bytes);
        var fixturePython = Inspect(repo, Path.Combine(outDir, "navmesh-fixture.bin"));
        Check(fixture.Diagnostics.Count == 0 && fixture.Mesh.Validate().Count == 0 && fixture.Bridges.Count == 3 && fixturePython != null && (bool)((IDictionary<string, object>)fixturePython["validation"])["ok"]
              && ((object[])((IDictionary<string, object>)fixturePython["validation"])["warnings"]).Length == 0,
              "fixture: three bridges (one cell across x, two cells across x, one cell across y) built over under-bridge faces; the mesh is valid here and for the Python codec (" + fixture.Mesh.FaceCount + " faces)");
        Check(fixturePlan.Activatable, "fixture: every designed expectation holds statically (" + fixturePlan.Routes.Count + " routes, " + fixturePlan.Faces.Count + " faces)" + (fixturePlan.Activatable ? "" : ": " + String.Join("; ", fixturePlan.Diagnostics.Take(8))));
        var fixtureReport = NavigationValidator.Run(fixturePlan, new MeshNavigationEngine(fixture.Mesh, model), "test_fixture", null);
        var fixtureSummary = (Dictionary<string, object>)fixtureReport["summary"];
        Check((bool)fixtureReport["activatable"], "fixture: the validator over the stand-in agrees (" + fixtureSummary["routesAgree"] + "/" + fixtureSummary["routeChecks"] + " routes, " + fixtureSummary["facesAgree"] + "/" + fixtureSummary["faceChecks"] + " faces)"
              + ((bool)fixtureReport["activatable"] ? "" : ": " + String.Join("; ", Failures(fixtureReport).Take(8).Select(x => x["subject"] + " " + x["detail"]))));
        var fixtureFault = new MeshNavigationEngine(fixture.Mesh, model); fixtureFault.Disable(fixture.FaceGroups["bridge"]);
        var fixtureFaultFailures = Failures(NavigationValidator.Run(fixturePlan, fixtureFault, "test_fixture_fault", null)).Select(f => (string)f["subject"]).ToList();
        Check(new[] { "bridge1Crossing_Default", "bridge2Crossing_Default", "bridge3Crossing_Default" }.All(fixtureFaultFailures.Contains) && !fixtureFaultFailures.Contains("bridge1Under_Naval"),
              "fixture with Bridge faces disabled: the three bridge crossings fail and the under-bridge sea routes still pass (the layers are independent)");
        evidence["fixture"] = new Dictionary<string, object> { ["build"] = fixture.Record(), ["static"] = fixturePlan.StaticRecord(), ["standInSummary"] = fixtureSummary };

        // An L-shaped one-cell bridge (entered and left through adjacent sides): a convex deck connecting both banks.
        var lInput = inputA.Shallow(); lInput.Cells = inputA.Cells.Clone(); lInput.Variant = "test:l-bridge";
        var lt = lInput.Cells; int open = -1;
        for (int i = 0; i < lt.Classes.Length && open < 0; i++)
        {
            int r = i / lt.Cols, c = i % lt.Cols;
            if (r < 3 || r > lt.Rows - 4) continue;
            bool clear = true;
            for (int dr = -3; dr <= 3 && clear; dr++) for (int dc = -3; dc <= 3; dc++) if (lt.At(r + dr, c + dc) != TravelCells.Sea) { clear = false; break; }
            if (clear) open = i;
        }
        int lr = open / lt.Cols, lc = open % lt.Cols;
        lt.Classes[lt.Index(lr, lc)] = TravelCells.River; lt.Classes[lt.Index(lr - 1, lc - 1)] = TravelCells.River;
        lt.Classes[lt.Index(lr, lc - 1)] = TravelCells.Land; lt.Classes[lt.Index(lr - 1, lc)] = TravelCells.Land;
        var lLand = lt.Label(k => k == TravelCells.Land);
        lInput.Crossings = new List<CrossingRecord> { new CrossingRecord { Subject = "test L bridge", Kind = "river", Cells = new List<int[]> { new[] { lr, lc } }, LandA = lLand[lt.Index(lr, lc - 1)], LandB = lLand[lt.Index(lr - 1, lc)] } };
        var lBuild = RuntimeNavigationBuilder.Build(lInput, policy);
        var lTopology = new MeshTopology(lBuild.Mesh); var lDefault = lTopology.Components(f => model.Valid(lBuild.Mesh.Groups[f], "Default"));
        Func<int, int, int> landFace = (r, c) => { double x = (lt.ColumnOffset + c + .5) * lt.Spacing, y = (lt.RowOffset + r + .5) * lt.Spacing; return lTopology.Locate(x, y, true, model); };
        var deck = Enumerable.Range(0, lBuild.Mesh.FaceCount).Where(f => lBuild.Mesh.Roles[f] == NavMesh.RoleDeck).ToList();
        Check(lBuild.Diagnostics.Count == 0 && lBuild.Mesh.Validate().Count == 0 && lBuild.Bridges.Count == 1 && lBuild.Bridges[0].Sides[0][0] != (lBuild.Bridges[0].Sides[0][1] + 2) % 4 && deck.Count == 2 && deck.All(f => lBuild.Mesh.FaceVertices[f].Length == 4)
              && lDefault[landFace(lr, lc - 1)] >= 0 && lDefault[landFace(lr, lc - 1)] == lDefault[landFace(lr - 1, lc)],
              "an L-shaped crossing (cell " + lr + "," + lc + ", sides " + String.Join("/", lBuild.Bridges.Count == 1 ? lBuild.Bridges[0].Sides[0] : new int[0]) + ") becomes a strictly convex deck quad (one per strip image) joining the two diagonal banks; the mesh stays valid");

        // The activation gate.
        string store = Path.Combine(outDir, "store-" + Guid.NewGuid().ToString("N")), sceneFolder = Path.Combine(outDir, "scene-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sceneFolder); string installed = Path.Combine(sceneFolder, "navmesh.bin"); File.WriteAllBytes(installed, buildA.Bytes);
        string scene = SceneNames.ForWorld(inputA.WorldContentId, "encoder", "template", 1600, buildA.Sha256);
        var passing = NavigationValidator.Run(planA, new MeshNavigationEngine(buildA.Mesh, model), scene, null);
        bool missing = Refused(() => NavigationGate.Require(store, scene, installed, policy.Sha256), "navigation.no_report");
        string recordPath = NavigationGate.Persist(store, passing);
        bool passes = !Throws<NavigationRefusedException>(() => NavigationGate.Require(store, scene, installed, policy.Sha256));
        bool stalePolicy = Refused(() => NavigationGate.Require(store, scene, installed, "0000"), "navigation.report_stale");
        File.WriteAllBytes(installed, fixture.Bytes);
        bool tampered = Refused(() => NavigationGate.Require(store, scene, installed, policy.Sha256), "navigation.report_mismatch");
        File.WriteAllBytes(installed, buildA.Bytes);
        string original = File.ReadAllText(recordPath);
        File.WriteAllText(recordPath, original.Replace(NavigationValidator.Version, NavigationValidator.Version + "x"));
        bool edited = original != File.ReadAllText(recordPath) && Refused(() => NavigationGate.Require(store, scene, installed, policy.Sha256), "navigation.report_invalid");
        File.WriteAllText(recordPath, "{ not json");
        bool corrupt = Refused(() => NavigationGate.Require(store, scene, installed, policy.Sha256), "navigation.report_invalid");
        // A failing engine report (persisted as production would) refuses with its source ids; a report made with injected faults never passes.
        var failing = NavigationValidator.Run(planA, faultedEngine, scene, null);
        NavigationGate.Persist(store, failing);
        NavigationRefusedException refusal = null;
        try { NavigationGate.Require(store, scene, installed, policy.Sha256); } catch (NavigationRefusedException e) { refusal = e; }
        var labelled = NavigationValidator.Run(planA, new MeshNavigationEngine(buildA.Mesh, model), scene, null, new[] { "test fault" });
        bool faultedRefused = Refused(() => NavigationGate.Check(labelled, scene, installed, policy.Sha256), "navigation.report_faulted")
            && Refused(() => NavigationGate.Check(failing, scene, installed, policy.Sha256), "navigation.engine_route_failed");   // an in-memory failing report
        Check(missing && passes && stalePolicy && tampered && edited && corrupt && faultedRefused && refusal != null && buildA.Bridges.All(b => b.Subjects.All(x => refusal.Diagnostics.Any(d => d.SourceIds.Contains(x)))),
              "the activation gate refuses a scene without a report, a report for another navmesh or policy, an edited or unreadable record, and a report made with injected faults; it passes an intact activatable report for the installed navmesh, and refuses a failing report naming its source ids");

        // Scene identity includes the navmesh.
        Check(SceneNames.ForWorld(inputA.WorldContentId, "encoder", "template", 1600, buildA.Sha256) != SceneNames.ForWorld(inputA.WorldContentId, "encoder", "template", 1600, fixture.Sha256),
              "the content-addressed scene name changes with the navmesh (production and fixture navigation of one world are different scenes)");
    }

    // The managed stand-in with every path lengthened to factor times its own: an excursion back along the first segment and out again
    // (the cost grows by the same length, so the region switches still count right).
    private sealed class StretchedEngine : INavigationEngine
    {
        private readonly MeshNavigationEngine inner; private readonly double factor;
        public StretchedEngine(MeshNavigationEngine inner, double factor) { this.inner = inner; this.factor = factor; }
        public string Name { get { return "stretched stand-in"; } }
        public bool Face(double x, double y, bool land, out int face, out int group, out int island) { return inner.Face(x, y, land, out face, out group, out island); }
        public bool Path(int fromFace, int toFace, double[] from, double[] to, double radius, int[] excluded, double extraCost, int costToSea, int costToLand, out List<double[]> points)
        {
            if (!inner.Path(fromFace, toFace, from, to, radius, excluded, extraCost, costToSea, costToLand, out points) || points == null || factor == 1) return points != null;
            var line = new List<double[]> { from }; line.AddRange(points); line.Add(to);
            double length = 0; for (int i = 1; i < line.Count; i++) length += NavigationPlan.Distance(line[i - 1], line[i]);
            int k = Enumerable.Range(1, line.Count - 1).First(i => NavigationPlan.Distance(line[i - 1], line[i]) > 1e-9);
            double dx = line[k][0] - line[k - 1][0], dy = line[k][1] - line[k - 1][1], d = Math.Sqrt(dx * dx + dy * dy), half = (factor - 1) * length / 2;
            var back = new[] { from[0] - dx / d * half, from[1] - dy / d * half };
            points = new List<double[]> { back, new[] { from[0], from[1] } }.Concat(points).ToList();
            return true;
        }
        public bool Distance(int fromFace, int toFace, double[] from, double[] to, double radius, double limit, int[] excluded, int costToSea, int costToLand, out double distance)
        {
            if (!inner.Distance(fromFace, toFace, from, to, radius, limit, excluded, costToSea, costToLand, out distance)) return false;
            List<double[]> points;
            if (factor != 1 && inner.Path(fromFace, toFace, from, to, radius, excluded, 1, costToSea, costToLand, out points) && points != null)
            {
                var line = new List<double[]> { from }; line.AddRange(points); line.Add(to);
                double length = 0; for (int i = 1; i < line.Count; i++) length += NavigationPlan.Distance(line[i - 1], line[i]);
                distance += (factor - 1) * length;
            }
            return true;
        }
        public int FaceCount { get { return inner.FaceCount; } }
        public int FaceGroup(int face) { return inner.FaceGroup(face); }
        public double[] FaceCenter(int face) { return inner.FaceCenter(face); }
    }

    private static IEnumerable<Dictionary<string, object>> Failures(Dictionary<string, object> report) { return ((System.Collections.IEnumerable)report["failures"]).Cast<Dictionary<string, object>>(); }
    private static bool Refused(Action action, string code)
    {
        try { action(); return false; } catch (NavigationRefusedException e) { return e.Diagnostics.Any(d => d.Code == code); }
    }
    private static bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }

    private static IDictionary<string, object> Inspect(string repo, string file)
    {
        var info = new ProcessStartInfo("python", "\"" + Path.Combine(repo, "tools/scenebin/navmesh_bin.py") + "\" inspect --json \"" + file + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var p = Process.Start(info))
        {
            string stdout = p.StandardOutput.ReadToEnd(), stderr = p.StandardError.ReadToEnd(); p.WaitForExit();
            if (p.ExitCode != 0) { Console.WriteLine("  (python exit " + p.ExitCode + ": " + (stderr + stdout).Trim() + ")"); return null; }
            return (IDictionary<string, object>)Serializer.DeserializeObject(stdout);
        }
    }
}
