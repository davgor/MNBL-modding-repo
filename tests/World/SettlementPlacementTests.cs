using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;
using MNBL.ProceduralWorld.CampaignRegistry;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Surface;
using MNBL.ProceduralWorld.World;

// MMR-40 settlement placement tests, without the game:
//   SettlementPlacementTests REPO BUNDLE_A BUNDLE_B GAME_ROOT OUT_JSON REPORT_JSON [LOOKS_JSON]
// On two real compiled worlds: the bundle's settlements as sites (towns, castles, villages, port towns, and harbour villages by the campaign's
// harbour flag, each with its harbour cell); the resolved layout against the navigation (binding points) and the surface, re-checked
// independently (every gate and gate-frame marker on passable land of its settlement's component within the slope limit and its installed
// reach, every wall section and wall engine on land of the component, every marker clear of every other settlement's centre and markers,
// ports, drop-offs and every blockade ship of a fitted arc on water of its basin, the ships replayed from the installed GetBlockadeArc in
// single precision, canonical physical positions inside the terrain, no two settlements at one place); determinism; the scene entities
// built from it against the structural contract (scene levels, one root per settlement named its id, its kind's tag and scripts, every
// marker, wall section and wall engine by name with its tag, offset and levels, unique names, borders and the baked helper), the engine's
// level visibility rule over the village icons and the wall caches, scales and marker yaw; the installed contract (helper scripts, icon and
// wall meshes) and the policy's source; the refusals raised before activation (a centre off land, a port on land, no room for a gate, an
// unfit blockade arc, a drop-off on land, a harbour village without a harbour cell, two settlements at one place); the records' positions
// written from the layout and checked against it (SettlementFactory.ApplyResolvedPositions, CheckResolvedPositions); the run-time hierarchy
// for a settlement the scene lacks, built from its record.
internal static class SettlementPlacementTests
{
    private static int checks, failures;
    private static readonly List<string> lines = new List<string>();
    private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    // MMR-258: a fortification's gate stands at its look's gate distance (the policy's installed median without a look).
    private static double GateDistanceOf(SettlementSite site, SettlementPlacementPolicy policy) { return site.Frame != null ? site.Frame.GateDistance : policy.GateDistance; }
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; string line = (condition ? "ok    " : "FAIL  ") + label; lines.Add(line); Console.WriteLine(line); }

    private static int Main(string[] args)
    {
        if (args.Length != 6 && args.Length != 7) { Console.Error.WriteLine("usage: SettlementPlacementTests REPO BUNDLE_A BUNDLE_B GAME_ROOT OUT_JSON REPORT_JSON [LOOKS_JSON]"); return 2; }
        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = "mnbl.evidence.settlement-placement-tests", ["version"] = 2, ["ticket"] = "MMR-40" };
        var report = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = "mnbl.evidence.settlement-position-report", ["version"] = 2, ["ticket"] = "MMR-40" };
        try { Run(args, evidence, report); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        evidence["checks"] = checks; evidence["failures"] = failures; evidence["lines"] = lines;
        // MMR-258: the look evidence (catalog, per world the look usage and reuse report, entity counts and scene sizes) goes to its own file.
        object looks = null;
        if (evidence.TryGetValue("looks", out looks)) evidence.Remove("looks");
        if (args.Length == 7 && looks != null) File.WriteAllText(args[6], Serializer.Serialize(looks), new UTF8Encoding(false));
        File.WriteAllText(args[4], Serializer.Serialize(evidence), new UTF8Encoding(false));
        File.WriteAllText(args[5], Serializer.Serialize(report), new UTF8Encoding(false));
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string[] args, SortedDictionary<string, object> evidence, SortedDictionary<string, object> report)
    {
        string repo = args[0], gameRoot = args[3];
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", WorldDataBundle.HostCapabilities);
        var navPolicy = NavigationPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, NavigationPolicy.PolicyPath)));
        var calibration = (IDictionary<string, object>)Serializer.DeserializeObject(File.ReadAllText(Path.Combine(repo, "worldgen/calibration/naval-transitions/naval-transitions.json")).TrimStart('﻿'));
        var model = NavigationModel.FromCalibration(calibration);
        var policy = SettlementPlacementPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, SettlementPlacementPolicy.PolicyPath)), File.ReadAllBytes(Path.Combine(repo, SettlementPlacementPolicy.NavalPath)));
        var mainMap = File.ReadAllBytes(Path.Combine(gameRoot, "Modules", "NavalDLC", "SceneObj", "Main_map", "scene.xscene"));
        var contract = SettlementEntityContract.FromInstalled(mainMap);
        evidence["policySha256"] = policy.Sha256; evidence["contractSha256"] = contract.Sha256; evidence["installedCampaignMapSha256"] = contract.SourceSha256;
        Check(policy.SourceSha256 == contract.SourceSha256 && !Throws(() => policy.RequireSource(contract.SourceSha256)) && Throws(() => policy.RequireSource(new string('0', 64))),
            "the placement policy was measured on the installed campaign map the entity contract reads (" + contract.SourceSha256.Substring(0, 12) + "); a policy from another campaign map is refused");
        Check(contract.Helpers.Count == 2 && contract.Helpers.Values.All(x => x.StartsWith("<script name=")) && contract.SiegeCacheVariables.Count == 10 && contract.SiegeCacheVariables["_attackerBallistaPrefab"] == "ballista_a_mapicon",
            "contract: the installed helper scripts (Town Scene Manager, CampaignMapSiegePrefabEntityCache) are found once each and copied verbatim, with the siege cache's 10 prefab variables; the icon meshes are installed references");
        Check(Throws(() => SettlementEntityContract.FromInstalled(Encoding.UTF8.GetBytes("<scene><entities/></scene>"))) && Throws(() => contract.RequireMeshes("mnbl_no_such_mesh")) && !Throws(() => contract.RequireMeshes(policy.SolidWallMesh, policy.BrokenWallMesh)),
            "contract: a campaign map without the helper scripts is refused; the policy's wall meshes (" + policy.SolidWallMesh + ", " + policy.BrokenWallMesh + ") are installed references, an unknown mesh is refused");
        Check(policy.Walls.Count == 6 && policy.WallEngines.Count == 12 && policy.CircleScale.Count == 2 && policy.CampScale.Count == 2 && Throws(() => SettlementPlacementPolicy.FromBytes(Encoding.UTF8.GetBytes("{\"schema\":\"mnbl.settlement-placement\",\"version\":1}"), File.ReadAllBytes(Path.Combine(repo, SettlementPlacementPolicy.NavalPath)))),
            "policy version 2: two wall sections and four wall engines per wall level, circle and camp-area scales; a version 1 policy is refused");

        // MMR-258: the look catalog from the installed campaign map and prefabs, and the policies the look plan reads.
        var looksPolicy = SettlementLooksPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, SettlementLooksPolicy.PolicyPath)));
        byte[] prototypesBytes = File.ReadAllBytes(Path.Combine(repo, "worldgen/templates/settlements/prototypes.json")), economyBytes = File.ReadAllBytes(Path.Combine(repo, "worldgen/policies/economy.json"));
        var pinDefaults = new HashSet<string>(((object[])((IDictionary<string, object>)Serializer.DeserializeObject(File.ReadAllText(Path.Combine(repo, "worldgen/policies/campaign-catalog.json")).TrimStart('﻿')))["defaults"]).Cast<string>(), StringComparer.Ordinal);
        var missionScenes = MNBL.ProceduralWorld.Missions.MissionScenes.FromBytes(File.ReadAllBytes(Path.Combine(repo, "worldgen/policies/mission-scenes.json")));
        Func<string, string, bool, bool> conforms = (p, k, port) => missionScenes.PrototypeProblems(p, k, port).Count == 0;
        var prefabFolders = new[] { "NavalDLC", "SandBox", "Native" }.Select(m => Path.Combine(gameRoot, "Modules", m, "Prefabs")).ToList();
        var catalog = SettlementLookCatalog.FromInstalled(mainMap, SettlementLookCatalog.PrefabResolver(prefabFolders), prototypesBytes, looksPolicy);
        var pools = SettlementLookCatalog.Pools(prototypesBytes);
        LooksCatalogChecks(catalog, pools, looksPolicy, evidence);
        var lookEvidence = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = "mnbl.evidence.settlement-looks", ["version"] = 1, ["ticket"] = "MMR-258",
            ["installedCampaignMapSha256"] = catalog.SourceSha256, ["policySha256"] = looksPolicy.Sha256, ["catalog"] = evidence["lookCatalog"] };
        evidence["looks"] = lookEvidence;
        var lookWorlds = new SortedDictionary<string, object>(StringComparer.Ordinal); lookEvidence["worlds"] = lookWorlds;

        var worlds = new SortedDictionary<string, object>(StringComparer.Ordinal); report["worlds"] = worlds;
        var summary = new SortedDictionary<string, object>(StringComparer.Ordinal); evidence["worlds"] = summary;
        SettlementLayout layoutA = null; NavigationPlan planA = null; NavigationGround groundA = null; List<SettlementSite> sitesA = null; NavigationInput inputA = null;
        foreach (var label in new[] { "A", "B" })
        {
            var bundle = WorldDataBundle.Open(label == "A" ? args[1] : args[2], host);
            var surface = WorldSurface.FromBundle(bundle);
            var input = NavigationInput.FromBundle(bundle, surface, navPolicy);
            var build = RuntimeNavigationBuilder.Build(input, navPolicy);
            var plan = NavigationPlan.Create(build, model);
            Check(plan.Activatable, label + ": the navigation plan is activatable (the precondition the scene build already has)");
            var ground = new NavigationGround(build);
            byte[] campaignJson = bundle.ReadVerified("compiled/campaign/campaign.json"), portsJson = bundle.ReadVerified("compiled/travel/ports.json");
            var sites = SettlementPositionResolver.SitesFromBundle(input, campaignJson, portsJson);
            var campaign = (IDictionary<string, object>)Serializer.DeserializeObject(new UTF8Encoding(false).GetString(campaignJson).TrimStart('﻿'));
            var flagged = ((object[])campaign["villages"]).Cast<IDictionary<string, object>>().Where(v => v.ContainsKey("harbour") && v["harbour"] is bool && (bool)v["harbour"]).Select(v => (string)v["id"]).Where(id => !input.Excluded.Contains(id)).ToList();
            var kinds = input.Settlements.Where(s => !input.Excluded.Contains(s.Id)).GroupBy(s => s.Kind).ToDictionary(g => g.Key, g => g.Count());
            int portTowns = input.Transitions.Count(x => x.Kind == "port");
            Check(sites.Count(s => s.Kind == SiteKind.Town) == kinds["town"] && sites.Count(s => s.Kind == SiteKind.Castle) == kinds["castle"] && sites.Count(s => s.Kind == SiteKind.Village) == kinds["village"]
                && sites.Count(s => s.HasPort) == portTowns && sites.Where(s => s.Harbour).Select(s => s.Id).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(flagged.OrderBy(x => x, StringComparer.Ordinal))
                && sites.Where(s => s.Harbour).All(s => s.HarbourCell != null),
                label + ": every compiled settlement is a site (" + kinds["town"] + " towns, " + kinds["castle"] + " castles, " + kinds["village"] + " villages; " + portTowns + " port towns); the harbour villages are the campaign's harbour flag (" + flagged.Count + "), each with the harbour cell of a port candidate naming it, as SettlementFactory gives them ports");
            // The production plan (RuntimeWorldBackend.Prepare): every settlement's look, then the placement at the looks' offsets.
            byte[] travelJson = bundle.ReadVerified("compiled/travel/travel-graph.json");
            Func<SettlementLookPlan.Inputs> lookInputs = () => {
                var x = SettlementLookPlan.ForCampaign(campaignJson, travelJson, portsJson, economyBytes, prototypesBytes, pinDefaults.Contains, looksPolicy);
                x.Catalog = catalog; x.Conforms = conforms; return x; };
            FrozenLooks frozen;
            var layout = SettlementLookPlan.Resolve(sites, plan, ground, policy, lookInputs(), out frozen);
            var refusals = layout.Problems.Where(p => p.Refusal).ToList();
            Check(layout.Activatable, label + ": the layout has no refusal" + (refusals.Count > 0 ? " (" + refusals.Count + ": " + String.Join("; ", refusals.Take(5)) + ")" : ""));
            Check(layout.Settlements.Count == sites.Count, label + ": every site is resolved");
            FrozenLooks frozenAgain;
            var again = SettlementLookPlan.Resolve(SettlementPositionResolver.SitesFromBundle(input, campaignJson, portsJson), NavigationPlan.Create(RuntimeNavigationBuilder.Build(input, navPolicy), model), ground, policy, lookInputs(), out frozenAgain);
            Check(again.Sha256 == layout.Sha256 && Serializer.Serialize(again.Report(ground)) == Serializer.Serialize(layout.Report(ground)), label + ": the layout (looks included) and its report are deterministic (" + layout.Sha256.Substring(0, 12) + ")");

            // Independent re-check of the resolved positions.
            var bad = new List<string>();
            int markers = 0, wallMarkers = 0, adjusted = 0, steepMarkers = 0, unfitArcs = 0, ships = 0;
            double worstShip = 0;
            var everyMarker = new List<KeyValuePair<string, double[]>>();
            foreach (var r in layout.Settlements)
            {
                var point = plan.Point(r.Site.Id);
                if (point == null || point.Resolved[0] != r.Centre[0] || point.Resolved[1] != r.Centre[1]) bad.Add(r.Site.Id + ": centre is not its navigation point");
                int land = ground.Land(r.Centre[0], r.Centre[1]);
                if (land < 0) bad.Add(r.Site.Id + ": centre off land");
                var phys = ground.Physical(r.Centre[0], r.Centre[1]);
                if (phys[0] < surface.Strip - 1e-9 || phys[0] >= surface.Strip + surface.LogicalWidth || !surface.Contains(phys[0], phys[1])) bad.Add(r.Site.Id + ": physical centre " + phys[0] + "," + phys[1] + " not canonical inside the terrain");
                bool steepReported = layout.Problems.Any(p => !p.Refusal && p.Code == "settlement.steep_marker" && p.Subject == r.Site.Id);
                Func<double[], bool, bool> onLand = (q, steep) => ground.Land(q[0], q[1]) == land && (ground.Slope(q[0], q[1]) <= policy.MaxMarkerSlope || (steep && steepReported));
                if (r.Site.Fortification)
                {
                    // A look's markers stand at its own offsets at its scale (MMR-258); a phase-0 settlement's at the installed medians.
                    var frame = r.Site.Frame;
                    var slots = frame == null ? null : frame.Slots.ToDictionary(x => x.Suffix, StringComparer.Ordinal);
                    double gateReach = frame == null ? policy.GateDistance : frame.GateDistance;
                    if (r.Gate == null || !onLand(r.Gate, r.GateSteep) || SettlementPositionResolver.Distance(ground, r.Centre, r.Gate) > gateReach + 1e-9) bad.Add(r.Site.Id + ": gate invalid");
                    else everyMarker.Add(new KeyValuePair<string, double[]>(r.Site.Id, r.Gate));
                    int expectedMarkers = frame == null ? policy.Markers.Count + policy.SiegeTowers - 1 : frame.Slots.Count(x => x.Level == null);
                    if (r.Markers.Count(m => m.Level == null) != expectedMarkers || r.Markers.Count(m => m.Level != null) != 12 || r.Walls.Count != 6) bad.Add(r.Site.Id + ": " + r.Markers.Count + " markers, " + r.Walls.Count + " walls");
                    foreach (var m in r.Markers.Concat(r.Walls))
                    {
                        if (m.Level == null) markers++; else wallMarkers++;
                        if (m.Turns != 0 || m.Scale < 1 || m.Steep) adjusted++; if (m.Steep) steepMarkers++;
                        var o = slots != null ? slots[m.Suffix].Offset : m.Level == null ? policy.Markers[m.Tag] : m.Tag == "map_breachable_wall" ? policy.Walls[m.Level + "/" + m.Suffix.Substring(m.Suffix.Length - 1)] : policy.WallEngines[m.Tag.Substring(m.Tag.Length - 1) + "/" + m.Level];
                        double reach = Math.Sqrt(o[0] * o[0] + o[1] * o[1]);
                        bool valid = m.Level == null ? onLand(m.Logical, m.Steep) : ground.Land(m.Logical[0], m.Logical[1]) == land;
                        if (!valid || SettlementPositionResolver.Distance(ground, r.Centre, m.Logical) > reach + 1e-9) bad.Add(r.Site.Id + ": marker " + m.Suffix + " invalid");
                        everyMarker.Add(new KeyValuePair<string, double[]>(r.Site.Id, m.Logical));
                    }
                }
                if (r.Site.HasPort)
                {
                    var t = input.Transitions.First(x => x.Kind == "port" && x.Town == r.Site.Id);
                    bool unfit = r.ArcStart == null && layout.Problems.Any(p => !p.Refusal && p.Code == "settlement.blockade_unfit" && p.Subject == r.Site.Id);
                    if (unfit) unfitArcs++;
                    foreach (var q in unfit ? new[] { r.Port } : new[] { r.Port, r.ArcStart, r.ArcEnd }) if (q == null || ground.Basin(q[0], q[1]) != t.Basin) bad.Add(r.Site.Id + ": port or arc end not on basin " + t.Basin);
                    if (r.Port != null && SettlementPositionResolver.Distance(ground, r.Gate ?? r.Centre, r.Port) > input.MaxPortGateUnits) bad.Add(r.Site.Id + ": port beyond the gate limit");
                    if (!unfit && r.ArcStart != null)
                    {
                        var replay = InstalledArc(r.ArcStart, r.ArcEnd, ground, policy);
                        if (replay.Count != Int32.Parse(policy.Blockade["MaximumNumberOfShips"], CultureInfo.InvariantCulture) || r.ArcShips == null || r.ArcShips.Count != replay.Count) bad.Add(r.Site.Id + ": " + replay.Count + " replayed ships");
                        else for (int n = 0; n < replay.Count; n++)
                        {
                            ships++; worstShip = Math.Max(worstShip, SettlementPositionResolver.Distance(ground, replay[n], r.ArcShips[n]));
                            if (ground.Basin(replay[n][0], replay[n][1]) != t.Basin) bad.Add(r.Site.Id + ": blockade ship " + n + " off basin " + t.Basin);
                        }
                    }
                }
                if (r.Site.Harbour)
                {
                    double s = ground.Spacing; int basin = r.Site.HarbourBasin >= 0 ? r.Site.HarbourBasin : ground.Basin(r.Site.HarbourCell[1] * s + s / 2, r.Site.HarbourCell[0] * s + s / 2);
                    if (r.Dropoff == null || ground.Basin(r.Dropoff[0], r.Dropoff[1]) != basin || basin < 0) bad.Add(r.Site.Id + ": drop-off not on its basin");
                }
                if (!r.Site.Fortification && (r.Gate != null || r.Markers.Count > 0 || r.Walls.Count > 0)) bad.Add(r.Site.Id + ": a village with siege markers");
            }
            if (worstShip > 1e-3) bad.Add("the resolver's ship positions differ from the installed arc's by " + worstShip);
            // Every marker clear of every other settlement's centre and markers.
            int crossPairs = 0;
            var byCell = everyMarker.GroupBy(m => Key(m.Value, policy.MinSeparation)).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var m in everyMarker)
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        List<KeyValuePair<string, double[]>> near;
                        if (!byCell.TryGetValue(Key(new[] { m.Value[0] + dx * policy.MinSeparation, m.Value[1] + dy * policy.MinSeparation }, policy.MinSeparation), out near)) continue;
                        crossPairs += near.Count(o => String.CompareOrdinal(o.Key, m.Key) > 0 && SettlementPositionResolver.Distance(ground, o.Value, m.Value) < policy.MinSeparation);
                    }
            foreach (var r in layout.Settlements)
                if (everyMarker.Any(m => m.Key != r.Site.Id && SettlementPositionResolver.Distance(ground, r.Centre, m.Value) < policy.MinSeparation)) bad.Add(r.Site.Id + ": another settlement's marker on its centre");
            if (crossPairs > 0) bad.Add(crossPairs + " marker pairs of different settlements closer than " + policy.MinSeparation);
            for (int i = 0; i < layout.Settlements.Count; i++)
                for (int j = i + 1; j < layout.Settlements.Count; j++)
                    if (SettlementPositionResolver.Distance(ground, layout.Settlements[i].Centre, layout.Settlements[j].Centre) < policy.MinSeparation) bad.Add("overlap " + layout.Settlements[i].Site.Id + "/" + layout.Settlements[j].Site.Id);
            Check(bad.Count == 0, label + ": independently re-checked: centres are navigation points on land and canonical in the terrain; " + markers + " gate-frame markers on passable land of their component within their installed reach and the slope limit, " + wallMarkers + " wall sections and wall engines on land of their component within their reach (" + adjusted + " adjusted; " + steepMarkers + " least-steep where nothing met the limit, each reported); every marker clear of every other settlement's centre and markers (0 cross pairs); ports, arc ends and all " + ships + " blockade ships (the installed GetBlockadeArc replayed in single precision, within " + worstShip.ToString("0.#####", CultureInfo.InvariantCulture) + " of the resolver's) on their basin (" + unfitArcs + " ports without a fitting arc get none, reported), ports within the gate limit; drop-offs on their basin; no two settlements at one place" + (bad.Count > 0 ? " - " + String.Join("; ", bad.Take(6)) : ""));
            Check(layout.Settlements.Where(r => r.Site.HasPort && r.ArcStart != null).All(r => r.ArcEnd != null && r.Provenance.Any(p => (string)p["field"] == "blockadeArc")) && layout.Settlements.All(r => r.Provenance.Any(p => (string)p["field"] == "centre"))
                && layout.Settlements.Where(r => r.Site.HasPort).All(r => r.Provenance.Any(p => (string)p["field"] == "port")) && layout.Settlements.Where(r => r.Site.Harbour).All(r => r.Provenance.Any(p => (string)p["field"] == "dropoff"))
                && layout.Settlements.Where(r => r.Site.Fortification).All(r => r.Provenance.Any(p => (string)p["field"] == "gate") && r.Markers.Concat(r.Walls).All(m => m.From != null && m.Rule != null)),
                label + ": every resolved position carries its provenance (centre, gate, port, blockade arc, drop-off, every marker: rule, before and after)");
            var rows = (List<Dictionary<string, object>>)layout.Report(ground)["settlements"];
            Check(rows.Where(x => (string)x["kind"] == "town" || (string)x["kind"] == "castle").All(x => x["gateScene"] != null && ((IEnumerable<Dictionary<string, object>>)x["markers"]).All(m => m["scene"] != null && m["from"] != null) && ((IEnumerable<Dictionary<string, object>>)x["walls"]).Count() == 6)
                && layout.Settlements.Where(r => r.GateSteep).All(r => r.GateRule.Contains("least steep")),
                label + ": the report gives each position canonically and where the scene builds it (beside its settlement), each marker's unadjusted position, the walls; a least-steep gate says so in its rule");

            // The scene entities (MMR-258: from the looks).
            var specs = SettlementEntityFactory.Build(layout, ground, contract, policy, looksPolicy, "mnbl_world", surface.Extent, SettlementEntityFactory.MapMaximumHeight(surface.High));
            string xml = SettlementEntityFactory.ToXml(specs);
            string template = "<scene name=\"test\">\n\t<levels>\n\t\t<level name=\"base\" mask=\"1\" />\n\t\t<level name=\"level_1\" mask=\"2\" />\n\t\t<level name=\"level_2\" mask=\"4\" />\n\t\t<level name=\"level_3\" mask=\"8\" />\n\t</levels>\n<entities>\n</entities></scene>";
            string scene = SettlementEntityFactory.DeclareSceneLevels(SettlementEntityFactory.Splice(template, xml));
            var problems = SettlementEntityFactory.Validate(scene, layout, ground, policy, "mnbl_world");
            Check(problems.Count == 0, label + ": the scene entities meet the relaxed contract (scene levels; one root per settlement named its id with its kind's tag and Town Entity Manager, circle, selection, banner placeholders (the policy's, or the look's with one child each under parents with every wall level's bit), village icons per level, gate, every marker, wall section and wall engine by name with its tag, offset and levels, each wall with at most one solid and one broken piece, port and blockade arc with BlockadePositionScript on port towns with an arc, drop-offs as direct children of harbour villages; no nested prefab instance, no solid collider, no functional tag below the direct children; unique names; borders; the baked helper only)" + (problems.Count > 0 ? " - " + String.Join("; ", problems.Take(6)) : ""));
            Check(SettlementEntityFactory.ToXml(SettlementEntityFactory.Build(layout, ground, contract, policy, looksPolicy, "mnbl_world", surface.Extent, SettlementEntityFactory.MapMaximumHeight(surface.High))) == xml, label + ": the entity XML is deterministic (the encoding's identity hashes it)");
            var doc = new XmlDocument(); doc.LoadXml(scene);
            int roots = doc.SelectNodes("/scene/entities/game_entity").Count, entities = doc.SelectNodes("//game_entity").Count;
            Check(roots == 3 + layout.Settlements.Count && doc.SelectNodes("//script[@name='CampaignMapSiegePrefabEntityCache']").Count == 0, label + ": " + roots + " root entities: 2 borders, the Town Scene Manager helper and one per settlement (" + entities + " entities in all); no siege prefab cache (its script throws in a read without a campaign, run 25412; GeneratedMapScene.Load adds it to the campaign map)");
            // MMR-262: border_max's z (the campaign's MapMaximumHeight: the camera's zoom-out cap) is the fixed floor on these low worlds, and
            // 100 above the terrain on a higher one.
            var borderMax = (XmlElement)doc.SelectSingleNode("/scene/entities/game_entity[@name='border_max']/transform");
            double borderZ = borderMax == null ? Double.NaN : Double.Parse(borderMax.GetAttribute("position").Split(',')[2].Trim(), CultureInfo.InvariantCulture);
            Check(borderZ == SettlementEntityFactory.MapMaximumHeight(surface.High) && borderZ >= SettlementEntityFactory.MinimumMapHeight && (surface.High + 100 > SettlementEntityFactory.MinimumMapHeight || borderZ == SettlementEntityFactory.MinimumMapHeight)
                  && SettlementEntityFactory.MapMaximumHeight(16.718) == 180 && SettlementEntityFactory.MapMaximumHeight(120) == 220,
                  label + ": border_max z " + borderZ.ToString("R", CultureInfo.InvariantCulture) + " (terrain high " + surface.High.ToString("0.###", CultureInfo.InvariantCulture) + ") is Max(high + 100, " + SettlementEntityFactory.MinimumMapHeight + "): the camera's zoom-out cap does not follow the vertical scale");
            var withCache = SettlementEntityFactory.Splice(scene,"<game_entity name=\"mnbl_world_helper_campaignmapsiegeprefabentitycache\">" + contract.Helpers["CampaignMapSiegePrefabEntityCache"].Replace("<script", "<scripts><script").Replace("</script>", "</script></scripts>") + "</game_entity>");
            Check(SettlementEntityFactory.Validate(withCache, layout, ground, policy, "mnbl_world").Any(p => p.Contains("CampaignMapSiegePrefabEntityCache")), label + ": a scene carrying a baked siege prefab cache fails the contract");
            int nestedPrefabs = doc.SelectNodes("//game_entity[@prefab]").Count, colliders = doc.SelectNodes("//physics[@shape]").Count;
            int solidColliders = doc.SelectNodes("/scene/entities/game_entity[tags/tag/@name='town' or tags/tag/@name='castle' or tags/tag/@name='village']//physics[@shape]").Cast<XmlElement>().Count(p => p.SelectSingleNode("body_flags/body_flag[@name='only_collide_with_raycast']") == null);
            Check(nestedPrefabs == 0 && solidColliders == 0, label + ": the baked scene has no nested prefab reference and no solid collider under a town, castle or village (" + colliders + " colliders, all raycast-only)");
            // The engine's rules over the entities (SettlementVisual reads a settlement's whole hierarchy): SetSettlementLevelVisibility shows a child
            // whose level mask holds the visual's current mask (a child without levels has every level); UpdateDefenderSiegeEntitiesCache keeps the
            // wall sections and wall engines whose mask has the current wall level's bit; a look's tree holds no functional tag.
            var levelProblems = new List<string>();
            Func<XmlElement, uint> mask = x => { var names = x.SelectNodes("levels/level").Cast<XmlElement>().Select(l => l.GetAttribute("name")).ToList(); return names.Count == 0 ? UInt32.MaxValue : SettlementEntityFactory.LevelMask(names); };
            Func<XmlElement, string, bool> tagged = (x, t) => x.SelectSingleNode("tags/tag[@name='" + t + "']") != null;
            Func<XmlElement, double[]> transform = x => LookFrame.Vec(x.SelectSingleNode("transform") as XmlElement, "rotation_euler", 0);
            Func<XmlElement, double[]> scaleOf = x => LookFrame.Vec(x.SelectSingleNode("transform") as XmlElement, "scale", 1);
            int resnapped = 0; double worstSnap = 0; int looksBuilt = 0, ownBanners = 0, emptyWalls = 0, production = 0, piers = 0;
            foreach (var r in layout.Settlements)
            {
                var root = (XmlElement)doc.SelectSingleNode("/scene/entities/game_entity[@name='" + r.Site.Id + "']");
                var direct = root.SelectNodes("children/game_entity").Cast<XmlElement>().ToList();
                var look = r.Look == null ? null : r.Look.Source;
                if (r.Site.Kind == SiteKind.Village)
                    foreach (var state in new[] { "civilian", "looted" })
                        for (int level = 1; level <= 3; level++)
                        {
                            uint current = SettlementEntityFactory.LevelMask(new[] { "level_" + level, state });
                            int visible = direct.Count(c => c.GetAttribute("name").StartsWith(r.Site.Id + "_icon", StringComparison.Ordinal) && (mask(c) & current) == current);
                            if (visible != 1) levelProblems.Add(r.Site.Id + ": " + visible + " icons visible at level_" + level + " " + state);
                        }
                // The village type's production pieces (RT-15): every production mesh of the village shows its type.
                if (r.Site.Kind == SiteKind.Village && r.Look != null && r.Look.VillageType != null)
                {
                    var allowed = looksPolicy.Production[r.Look.VillageType];
                    var meshes = root.SelectNodes(".//meta_mesh_component").Cast<XmlElement>().Select(m => m.GetAttribute("name")).Where(m => looksPolicy.Production.Values.Any(v => v.Contains(m))).ToList();
                    if (meshes.Count == 0 || meshes.Any(m => !allowed.Contains(m))) levelProblems.Add(r.Site.Id + ": production pieces [" + String.Join(",", meshes.Distinct()) + "] are not its type " + r.Look.VillageType + "'s");
                    production += meshes.Count;
                }
                piers += direct.Count(c => c.GetAttribute("name") == r.Site.Id + "_pier");
                // Re-snapped: each topmost visible piece of the look stands on the terrain under it.
                var container = direct.FirstOrDefault(c => c.GetAttribute("name") == r.Site.Id + "_look");
                if (look != null && container == null) levelProblems.Add(r.Site.Id + ": a look without its _look container");
                if (container != null)
                {
                    looksBuilt++;
                    var rootZ = LookFrame.Vec(root.SelectSingleNode("transform") as XmlElement, "position", 0)[2];
                    var cf = new LookFrame().Child(container); double cs = scaleOf(container)[0];
                    Action<XmlElement, LookFrame> walk = null;
                    walk = (parent, f) => {
                        foreach (var e in SettlementLook.Children(parent))
                        {
                            var g = f.Child(e);
                            if (SettlementLook.Visible(e) && Math.Abs(f.U[2]) > 0.2 * cs)
                            {
                                double z = ground.Elevation(r.Centre[0] + g.O[0], r.Centre[1] + g.O[1]) - rootZ;
                                worstSnap = Math.Max(worstSnap, Math.Abs(g.O[2] - z)); resnapped++;
                                continue;
                            }
                            walk(e, g);
                        } };
                    walk(container, cf);
                    foreach (var t in SettlementLook.FunctionalTags.Concat(r.Site.Fortification && look != null && look.OwnBanners ? new string[0] : new[] { SettlementLook.BannerTag }))
                        if (container.SelectNodes(".//game_entity[tags/tag/@name='" + t + "']").Count > 0) levelProblems.Add(r.Site.Id + ": its look tree holds a " + t);
                }
                if (!r.Site.Fortification) continue;
                if (look != null && look.OwnBanners) ownBanners++;
                for (int level = 1; level <= 3; level++)
                {
                    uint bit = SettlementEntityFactory.LevelMask(new[] { "level_" + level });
                    int wallCache = direct.Count(c => tagged(c, "map_breachable_wall") && (mask(c) & bit) != 0), engineCache = direct.Count(c => c.SelectNodes("tags/tag").Cast<XmlElement>().Any(t => t.GetAttribute("name").StartsWith("map_defensive_engine")) && (mask(c) & bit) != 0);
                    foreach (var state in new[] { "civilian", "siege" })
                    {
                        uint current = SettlementEntityFactory.LevelMask(new[] { "level_" + level, state });
                        if (direct.Count(c => tagged(c, "map_breachable_wall") && (mask(c) & current) == current) != 2) levelProblems.Add(r.Site.Id + ": walls at level_" + level + " " + state + " not 2 visible");
                    }
                    if (wallCache != 2 || engineCache != 4) levelProblems.Add(r.Site.Id + ": level_" + level + " caches " + wallCache + " walls and " + engineCache + " engines");
                }
                emptyWalls += direct.Count(c => tagged(c, "map_breachable_wall") && c.SelectNodes("children/game_entity[tags/tag/@name='map_solid_wall']").Count == 0);
                var circle = direct.First(c => tagged(c, "map_settlement_circle"));
                double wantCircle = look != null ? look.CircleScale * r.Look.Scale : policy.CircleScale[r.Site.Kind == SiteKind.Town ? "town" : "castle"];
                if (Math.Abs(scaleOf(circle)[0] - wantCircle) > 1e-9) levelProblems.Add(r.Site.Id + ": circle at " + scaleOf(circle)[0] + ", not " + wantCircle);
                if (look == null) foreach (var camp in policy.CampScale) if (!direct.Any(c => tagged(c, camp.Key) && scaleOf(c)[0] == camp.Value[0] && scaleOf(c)[1] == camp.Value[1])) levelProblems.Add(r.Site.Id + ": " + camp.Key + " not at the installed scale");
                // Every marker faces as its look's entity does once the look's gate is turned onto the resolved gate (phase 0: the gate, yaw = gate angle - pi/2).
                double frameAngle = look == null ? 0 : look.GateAngle;
                var gateEntity = direct.First(c => tagged(c, "main_map_city_gate"));
                if (Math.Abs(transform(gateEntity)[2] - (r.GateAngle - Math.PI / 2)) > 1e-12) levelProblems.Add(r.Site.Id + ": the gate does not face the gate direction");
                foreach (var m in r.Markers.Concat(r.Walls))
                {
                    var e = direct.First(c => c.GetAttribute("name") == r.Site.Id + "_" + m.Suffix);
                    if (Math.Abs(transform(e)[2] - (m.LocalYaw + r.GateAngle - frameAngle)) > 1e-12) levelProblems.Add(r.Site.Id + ": " + m.Suffix + " yaw " + transform(e)[2] + ", not its look's turned");
                }
            }
            Check(levelProblems.Count == 0 && worstSnap < 1e-6, label + ": with the engine's level rules exactly one village icon shows in each state (level 1-3, civilian or looted); each wall level shows its two wall sections in civilian and siege and caches two sections and four wall engines; circles at the look's scale (the installed one in phase 0) and phase-0 camp areas at the installed scales; every marker faces as its look's entity once the look is turned onto the gate; every village's production pieces are its type's (" + production + " pieces); no look tree holds a functional tag; every topmost visible look piece stands on the terrain (" + resnapped + " pieces, worst " + worstSnap.ToString("0.###E0", CultureInfo.InvariantCulture) + ")" + (levelProblems.Count > 0 ? " - " + String.Join("; ", levelProblems.Take(6)) : ""));
            var perKind = specs.Skip(3).GroupBy(sp => sp.Tags.FirstOrDefault() ?? "-").ToDictionary(g => g.Key, g => (object)Math.Round(g.Average(sp => doc.SelectSingleNode("/scene/entities/game_entity[@name='" + sp.Name + "']").SelectNodes(".//game_entity").Count + 1.0), 1));
            // Breaking the scene is caught.
            var broken = scene.Replace("main_map_city_port", "main_map_city_prt");
            Check(SettlementEntityFactory.Validate(broken, layout, ground, policy, "mnbl_world").Any(p => p.Contains("main_map_city_port")), label + ": a scene whose port children are mistagged fails the contract");
            var faultDoc = new XmlDocument(); faultDoc.LoadXml(scene);
            var solidPiece = faultDoc.SelectSingleNode("//game_entity[tags/tag/@name='map_breachable_wall']/children/game_entity[tags/tag/@name='map_solid_wall']") as XmlElement;
            var copy = (XmlElement)solidPiece.CloneNode(true); copy.SetAttribute("name", solidPiece.GetAttribute("name") + "_twin"); solidPiece.ParentNode.AppendChild(copy);
            Check(SettlementEntityFactory.Validate(faultDoc.OuterXml, layout, ground, policy, "mnbl_world").Any(p => p.Contains("more than one solid or broken")), label + ": a wall section with two solid pieces fails the relaxed contract");
            faultDoc.LoadXml(scene);
            var aLook = faultDoc.SelectSingleNode("//game_entity[substring(@name, string-length(@name) - 4) = '_look']//game_entity") as XmlElement;
            aLook.SetAttribute("prefab", "map_barrel_a");
            var raycast = faultDoc.SelectSingleNode("//game_entity[substring(@name, string-length(@name) - 4) = '_look']//physics/body_flags/body_flag[@name='only_collide_with_raycast']") as XmlElement;
            if (raycast != null) raycast.SetAttribute("name", "dynamic_constraint");
            var faultProblems = SettlementEntityFactory.Validate(faultDoc.OuterXml, layout, ground, policy, "mnbl_world");
            Check(faultProblems.Any(p => p.Contains("nested prefab instance")) && (raycast == null || faultProblems.Any(p => p.Contains("solid colliders"))), label + ": a nested prefab instance and a solid collider in a look fail the contract");
            faultDoc.LoadXml(scene);
            var stray = faultDoc.SelectSingleNode("//game_entity[substring(@name, string-length(@name) - 4) = '_look']/children/game_entity") as XmlElement;
            var strayTags = faultDoc.CreateElement("tags"); var strayTag = faultDoc.CreateElement("tag"); strayTag.SetAttribute("name", "main_map_city_gate"); strayTags.AppendChild(strayTag); stray.InsertBefore(strayTags, stray.FirstChild);
            Check(SettlementEntityFactory.Validate(faultDoc.OuterXml, layout, ground, policy, "mnbl_world").Any(p => p.Contains("main_map_city_gate entities below its direct children")), label + ": a functional tag inside a look's tree fails the contract (SettlementVisual reads the hierarchy recursively)");
            faultDoc.LoadXml(scene);
            var bannerParent = faultDoc.SelectSingleNode("//game_entity[tags/tag/@name='map_banner_placeholder']/../..") as XmlElement;
            if (bannerParent != null && bannerParent.SelectSingleNode("levels") != null)
            {
                bannerParent.RemoveChild(bannerParent.SelectSingleNode("levels"));
                Check(SettlementEntityFactory.Validate(faultDoc.OuterXml, layout, ground, policy, "mnbl_world").Any(p => p.Contains("banner placeholders")), label + ": a look banner placeholder under a parent without wall level bits fails the contract");
            }
            var policyWall = layout.Settlements.FirstOrDefault(r => r.Site.Fortification && (r.Look == null || r.Look.Source == null));
            if (policyWall != null)
            {
                faultDoc.LoadXml(scene);
                var piece = faultDoc.SelectSingleNode("//game_entity[@name='" + policyWall.Site.Id + "_wall_1_0_solid']");
                piece.ParentNode.RemoveChild(piece);
                Check(SettlementEntityFactory.Validate(faultDoc.OuterXml, layout, ground, policy, "mnbl_world").Any(p => p.Contains("solid or broken")), label + ": a phase-0 wall without its solid piece fails the contract");
            }
            Check(SettlementEntityFactory.Validate(SettlementEntityFactory.Splice(template, xml), layout, ground, policy, "mnbl_world").Any(p => p.Contains("scene level civilian")), label + ": a scene that does not declare the campaign map's levels fails the contract");
            var firstVillage = layout.Settlements.First(r => r.Site.Kind == SiteKind.Village).Site.Id;
            var missing = doc.SelectSingleNode("/scene/entities/game_entity[@name='" + firstVillage + "']"); missing.ParentNode.RemoveChild(missing);
            Check(SettlementEntityFactory.Validate(doc.OuterXml, layout, ground, policy, "mnbl_world").Any(p => p.StartsWith(firstVillage + ": no root")), label + ": a scene missing a settlement's entity fails the contract");
            LooksWorldChecks(label, layout, sites, frozen, looksPolicy, pools, missionScenes, ground, () => {
                    FrozenLooks again2; var read = FrozenLooks.Read(path => frozen.Files()[path], looksPolicy);
                    var inputs = SettlementLookPlan.ForCampaign(campaignJson, travelJson, portsJson, economyBytes, prototypesBytes, pinDefaults.Contains, looksPolicy); inputs.Frozen = read;
                    var l2 = SettlementLookPlan.Resolve(SettlementPositionResolver.SitesFromBundle(input, campaignJson, portsJson), NavigationPlan.Create(RuntimeNavigationBuilder.Build(input, navPolicy), model), ground, policy, inputs, out again2);
                    return new KeyValuePair<string, string>(l2.Sha256, SettlementEntityFactory.ToXml(SettlementEntityFactory.Build(l2, ground, contract, policy, looksPolicy, "mnbl_world", surface.Extent, SettlementEntityFactory.MapMaximumHeight(surface.High)))); }, xml);
            int sceneBytes = new UTF8Encoding(false).GetByteCount(scene), xmlBytes = new UTF8Encoding(false).GetByteCount(xml);
            lookWorlds[label] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["worldContentId"] = bundle.WorldContentId, ["layoutSha256"] = layout.Sha256, ["xmlSha256"] = Sha(xml),
                ["entities"] = entities, ["roots"] = roots, ["perKindEntities"] = perKind, ["settlementXmlBytes"] = xmlBytes, ["sceneBytesWithTemplate"] = sceneBytes,
                ["looksBuilt"] = looksBuilt, ["ownBanners"] = ownBanners, ["wallSectionsWithoutSolidPiece"] = emptyWalls, ["productionPieces"] = production, ["piers"] = piers,
                ["resnapped"] = resnapped, ["colliders"] = colliders, ["frozenFiles"] = frozen.Files().Count, ["frozenBytes"] = frozen.Files().Values.Sum(b => (long)b.Length),
                ["looks"] = layout.Looks };
            var full = layout.Report(ground); full["sceneEntities"] = new Dictionary<string, object> { ["roots"] = roots, ["entities"] = entities, ["perKindEntities"] = perKind, ["xmlSha256"] = Sha(xml), ["contractSha256"] = contract.Sha256 };
            worlds[label] = full;
            summary[label] = new Dictionary<string, object> { ["worldContentId"] = bundle.WorldContentId, ["layoutSha256"] = layout.Sha256, ["counts"] = full["counts"], ["adjusted"] = full["adjusted"],
                ["harbourVillages"] = full["harbourVillages"], ["blockadeArcs"] = full["blockadeArcs"], ["blockadeShips"] = ships,
                ["warnings"] = layout.Problems.Where(p => !p.Refusal).GroupBy(p => p.Code).ToDictionary(g => g.Key, g => (object)g.Count()), ["markers"] = markers, ["wallMarkers"] = wallMarkers, ["markersAdjusted"] = adjusted, ["roots"] = roots, ["entities"] = entities };
            if (label == "A") { layoutA = layout; planA = plan; groundA = ground; sitesA = sites; inputA = input; }
        }

        // Scene levels.
        string levelsOnly = "<scene name=\"t\">\n\t<levels>\n\t\t<level name=\"base\" mask=\"1\" />\n\t</levels>\n\t<entities>\n\t</entities>\n</scene>";
        string declared = SettlementEntityFactory.DeclareSceneLevels(levelsOnly);
        var declaredDoc = new XmlDocument(); declaredDoc.LoadXml(declared);
        Check(declaredDoc.SelectNodes("/scene/levels/level").Count == 7 && SettlementEntityFactory.DeclareSceneLevels(declared) == declared && declaredDoc.SelectSingleNode("/scene/levels/level[@name='looted' and @mask='64']") != null
            && Throws(() => SettlementEntityFactory.DeclareSceneLevels(levelsOnly.Replace("\"base\" mask=\"1\"", "\"civilian\" mask=\"2\"")))
            && new XmlDocument().Let(d => { d.LoadXml(SettlementEntityFactory.DeclareSceneLevels("<scene name=\"t\"><entities /></scene>")); return d.SelectNodes("/scene/levels/level").Count == 7; }),
            "the scene declares the installed campaign map's levels (base 1, level_1-3 2/4/8, civilian 16, siege 32, looted 64): missing ones are added once, a conflicting mask is refused, a scene without levels gets them");

        // Refusals raised before activation (each on world A, one fault at a time).
        var town = layoutA.Settlements.First(r => r.Site.HasPort && r.ArcStart != null);
        Func<IPlacementGround, NavigationPlan, IList<SettlementSite>, SettlementLayout> resolve = (g, p, s) => SettlementPositionResolver.Resolve(s, p, g, policy);
        var centreOnWater = Mutate(planA, town.Site.Id, town.Port, () => resolve(groundA, planA, sitesA));
        Check(!centreOnWater.Activatable && centreOnWater.Problems.Any(p => p.Refusal && p.Code == "settlement.centre_not_land" && p.Subject == town.Site.Id), "refusal: a settlement centre off land (" + town.Site.Id + "'s point moved onto its port)");
        var portOnLand = Mutate(planA, "port:" + town.Site.Id, town.Centre, () => resolve(groundA, planA, sitesA));
        Check(!portOnLand.Activatable && portOnLand.Problems.Any(p => p.Refusal && p.Code == "settlement.port_not_water"), "refusal: a port on land");
        var walled = new Faulted(groundA) { NoLandBeyond = 0.02, Around = town.Centre };
        var noGate = resolve(walled, planA, sitesA);
        Check(!noGate.Activatable && noGate.Problems.Any(p => p.Refusal && p.Code == "settlement.gate_unplaced" && p.Subject == town.Site.Id), "refusal: no room for a gate (the land around a town turned to mountain)");
        var dry = new Faulted(groundA) { DryBeyond = 0.5, Around = town.Port };
        var noArc = resolve(dry, planA, sitesA);
        var unfitTown = noArc.Find(town.Site.Id);
        var unfitSpec = SettlementEntityFactory.Settlement(unfitTown, dry, policy);
        Check(noArc.Problems.Any(p => !p.Refusal && p.Code == "settlement.blockade_unfit" && p.Subject == town.Site.Id) && unfitTown.ArcStart == null && unfitTown.Port != null
            && unfitSpec.Scripts.All(x => x.Key != "BlockadePositionScript") && unfitSpec.Children.All(c => !c.Tags.Contains("Blockade_Arc_Start") && !c.Tags.Contains("Blockade_Arc_End")) && unfitSpec.Children.Count(c => c.Tags.Contains("main_map_city_port")) == 1,
            "a port where no blockade arc keeps every ship on water (only its own water left) keeps its port but gets no arc and no BlockadePositionScript, reported (never ships on land)");
        // An arc is accepted by its ships, not its ends: with the water taken away only around the ships of a town's fitted arc (its ends and
        // everything else still water), that arc is no longer chosen, and any arc chosen instead has every ship on the remaining water.
        var arcTown = layoutA.Settlements.First(r => r.Site.HasPort && r.ArcStart != null);
        var shipsDry = new DryAround(groundA, arcTown.ArcShips, 0.3);
        var shipsLayout = resolve(shipsDry, planA, sitesA);
        var refit = shipsLayout.Find(arcTown.Site.Id);
        int transitionBasin = inputA.Transitions.First(x => x.Kind == "port" && x.Town == arcTown.Site.Id).Basin;
        Check(shipsDry.Basin(arcTown.ArcStart[0], arcTown.ArcStart[1]) == transitionBasin && shipsDry.Basin(arcTown.ArcEnd[0], arcTown.ArcEnd[1]) == transitionBasin
            && (refit.ArcStart == null || (SettlementPositionResolver.Distance(groundA, refit.ArcStart, arcTown.ArcStart) > 1e-6 && refit.ArcShips.All(q => shipsDry.Basin(q[0], q[1]) == transitionBasin))),
            "an arc whose two ends are on water but whose ships are not is rejected (every ship is checked, not only the ends): " + (refit.ArcStart == null ? "no arc fits" : "another arc with every ship on water is chosen (turn " + refit.ArcTurns + (refit.ArcMirrored ? ", mirrored" : "") + ", kept " + refit.ArcScale + ")"));
        // A port town's gate keeps its port within the travel graph's port-to-gate limit (2026-10-19: 10 towns of 8 swept worlds had ports
        // 11.25 from the centre and 12.5-13.8 from a gate facing an inland road; seeds 56 and 67 were refused in game). On world A a port moved
        // to water of its basin within reach of some gate but beyond the limit from the road-facing gate turns the gate; a port no gate can
        // keep within the limit is still refused, the gate left facing its road.
        double limit = planA.Build.Input.MaxPortGateUnits;
        double[] reachable = null, unreachable = null; ResolvedSettlement turnTown = null; var searched = new List<string>();
        foreach (var t in layoutA.Settlements.Where(r => r.Site.HasPort && r.Gate != null && r.Port != null))
        {
            int portBasin = inputA.Transitions.First(x => x.Kind == "port" && x.Town == t.Site.Id).Basin;
            // Water of the port's basin around the centre: the first point the predicate (distance from the centre, from the gate) accepts.
            Func<Func<double, double, bool>, double[]> water = accept => {
                for (double radius = 0.5; radius <= 17; radius += 0.25)
                    for (int k = 0; k < 72; k++)
                    {
                        double a = k * Math.PI / 36; var q = new[] { t.Centre[0] + radius * Math.Cos(a), t.Centre[1] + radius * Math.Sin(a) };
                        if (q[1] <= 0 || q[1] >= groundA.Height || groundA.Basin(q[0], q[1]) != portBasin) continue;
                        if (accept(SettlementPositionResolver.Distance(groundA, t.Centre, q), SettlementPositionResolver.Distance(groundA, t.Gate, q))) return q;
                    }
                return null; };
            // Within reach: beyond the limit from the road-facing gate, at most a unit inside it from the centre, as the swept worlds' ports
            // (11.25); a gate turned toward it keeps it.
            reachable = water((fromCentre, fromGate) => fromGate > limit && fromCentre <= limit - 1);
            // Out of reach: beyond the limit from every gate position (each stands at its look's gate distance from the centre).
            unreachable = water((fromCentre, fromGate) => fromCentre > limit + GateDistanceOf(t.Site, policy) + 0.1);
            searched.Add(t.Site.Id.Substring(t.Site.Id.Length - 4) + (reachable != null ? " r" : " -") + (unreachable != null ? "u" : "-"));
            if (reachable != null && unreachable != null) { turnTown = t; break; }
        }
        Check(turnTown != null, "world A has a port town with water of its port's basin both within and beyond any gate's reach of the limit (for the gate-turn cases; " + String.Join(", ", searched) + ")");
        if (turnTown != null)
        {
            var turned = Mutate(planA, "port:" + turnTown.Site.Id, reachable, () => resolve(groundA, planA, sitesA)).Find(turnTown.Site.Id);
            var tooFarLayout = Mutate(planA, "port:" + turnTown.Site.Id, unreachable, () => resolve(groundA, planA, sitesA));
            var tooFar = tooFarLayout.Find(turnTown.Site.Id);
            Check(turned.Gate != null && SettlementPositionResolver.Distance(groundA, turned.Gate, reachable) <= limit && turned.GateRule.Contains("turned to keep its port within")
                && SettlementPositionResolver.Distance(groundA, turned.Centre, turned.Gate) <= GateDistanceOf(turnTown.Site, policy) + 1e-9
                && tooFarLayout.Problems.Any(p => p.Refusal && p.Code == "settlement.port_far" && p.Subject == turnTown.Site.Id) && SettlementPositionResolver.Distance(groundA, tooFar.Gate, turnTown.Gate) < 1e-9,
                "a port beyond the limit from " + turnTown.Site.Id + "'s road-facing gate turns the gate (" + (turned.GateRule ?? "") + ", " + SettlementPositionResolver.Distance(groundA, turned.Gate, reachable).ToString("0.###", CultureInfo.InvariantCulture)
                + " from its port); a port no gate keeps within " + limit + " is refused (port_far), the gate facing its road");
        }
        var harbour = sitesA.First(s => s.Harbour);
        var harbourCell = harbour.HarbourCell; var villageCell = inputA.Settlements.First(s => s.Id == harbour.Id);
        harbour.HarbourCell = new[] { villageCell.Row, villageCell.Col };
        var dropoffOnLand = resolve(groundA, planA, sitesA);
        harbour.HarbourCell = null;
        var noCell = resolve(groundA, planA, sitesA);
        harbour.HarbourCell = harbourCell;
        Check(!dropoffOnLand.Activatable && dropoffOnLand.Problems.Any(p => p.Refusal && p.Code == "settlement.dropoff_not_water" && p.Subject == harbour.Id), "refusal: a drop-off on land (a harbour village's harbour cell set to its own land cell)");
        Check(!noCell.Activatable && noCell.Problems.Any(p => p.Refusal && p.Code == "settlement.harbour_cell_missing" && p.Subject == harbour.Id), "refusal: a harbour village (the campaign's flag) without a harbour cell");
        var twin = new SettlementSite { Id = "mnbl_test_o_twin", Kind = SiteKind.Hideout, Source = "test", Centre = town.Centre };
        var overlap = resolve(groundA, planA, sitesA.Concat(new[] { twin }).ToList());
        Check(!overlap.Activatable && overlap.Problems.Any(p => p.Refusal && p.Code == "settlement.overlap"), "refusal: two settlements at one place (a hideout given the town's centre)");
        string message = null; try { throw new SettlementPlacementRefusedException("World test", noGate.Problems); } catch (SettlementPlacementRefusedException e) { message = e.Message; }
        Check(message != null && message.Contains(noGate.Problems.First(p => p.Refusal).Code) && message.Contains(noGate.Problems.Count(p => p.Refusal) + " settlement placement refusals"), "the refusal names its first problem (RuntimeWorldBackend.Prepare throws it before encoding; GenerationRuntime classifies it as a runtime capability failure)");
        evidence["faults"] = new Dictionary<string, object> { ["centreOffLand"] = centreOnWater.Problems.Count(p => p.Refusal), ["portOnLand"] = portOnLand.Problems.Count(p => p.Refusal), ["noGate"] = noGate.Problems.Count(p => p.Refusal),
            ["noArc"] = noArc.Problems.Count(p => p.Refusal), ["dropoffOnLand"] = dropoffOnLand.Problems.Count(p => p.Refusal), ["harbourCellMissing"] = noCell.Problems.Count(p => p.Refusal), ["overlap"] = overlap.Problems.Count(p => p.Refusal) };

        // A hideout given as a site (MMR-34 places hideouts from the campaign build).
        var villageRecord = inputA.Settlements.First(s => s.Kind == "village");
        var hideoutSite = new SettlementSite { Id = "mnbl_test_o_hideout", Kind = SiteKind.Hideout, Source = "test", Centre = new[] { villageRecord.X + 0.3, villageRecord.Y + 0.3 } };
        var withHideout = resolve(groundA, planA, new List<SettlementSite> { hideoutSite });
        var hideoutSpec = SettlementEntityFactory.Settlement(withHideout.Settlements.Single(), groundA, policy);
        Check(withHideout.Activatable && hideoutSpec.Tags.SequenceEqual(new[] { "wm_hideout" }) && hideoutSpec.Physics == "bo_sphere_collider" && hideoutSpec.Scripts.Any(s => s.Key == "Town Entity Manager") && hideoutSpec.Children.Count == 1,
            "a given hideout site resolves on land and gets the installed hideout root (wm_hideout, sphere collider, Town Entity Manager) and its icon");
        // The run-time hierarchy for a settlement the scene lacks, from its record.
        var unresolved = SettlementEntityFactory.Unresolved("mnbl_test_t_1", SiteKind.Town, new[] { 10.0, 20.0, 3.0 }, new[] { 10.0, 22.5973 }, new[] { 14.0, 20.0 }, policy);
        Func<string, EntitySpec> child = name => unresolved.Children.FirstOrDefault(c => c.Name == "mnbl_test_t_1_" + name);
        var gateChild = child("gate"); var campChild = child("camp_1"); var o1 = policy.Markers["map_camp_area_1"];
        Check(unresolved.Position.SequenceEqual(new[] { 10.0, 20.0, 3.0 }) && unresolved.Tags.Contains("town") && gateChild != null && Math.Abs(gateChild.Position[0]) < 1e-9 && Math.Abs(gateChild.Position[1] - 2.5973) < 1e-9
            && campChild != null && Math.Abs(campChild.Position[0] - o1[1]) < 1e-9 && Math.Abs(campChild.Position[1] - o1[0]) < 1e-9
            && unresolved.Children.Count(c => c.Tags.Contains("map_breachable_wall")) == 6 && unresolved.Children.Count(c => c.Tags.Any(t => t.StartsWith("map_defensive_engine"))) == 12
            && unresolved.Children.Count(c => c.Tags.Any(t => t.StartsWith("map_siege_engine"))) == 4 && unresolved.Children.Count(c => c.Tags.Contains("map_siege_tower")) == 2 && child("port") != null && child("port").Position[0] == 4
            && unresolved.Scripts.All(s => s.Key != "BlockadePositionScript") && unresolved.Children.Count(c => c.Tags.Contains("map_banner_placeholder")) == 4,
            "the run-time hierarchy for a settlement the scene lacks is its record's: the root at its centre, the gate at the record's gate, every siege marker, wall section and wall engine at its installed offset in that gate's frame, the record's port, no blockade arc");

        // The records' positions come from the same layout, and a record that differs from it is found.
        var intents = new List<SettlementFactory.SettlementIntent>();
        var village = layoutA.Settlements.First(r => r.Site.Harbour);
        var castle = layoutA.Settlements.First(r => r.Site.Kind == SiteKind.Castle);
        foreach (var r in new[] { town, castle, village }) intents.Add(new SettlementFactory.SettlementIntent { Id = r.Site.Id, Kind = r.Site.Kind.ToString().ToLowerInvariant(), Port = r.Site.HasPort, X = -1, Y = -1, GateX = -1, GateY = -1 });
        intents.Add(new SettlementFactory.SettlementIntent { Id = "mnbl_test_v_unplaced", Kind = "village", X = -1, Y = -1 });
        var missingIds = SettlementFactory.ApplyResolvedPositions(intents, layoutA.Positions);
        bool applied = intents[0].GateX == town.Gate[0] && intents[0].GateY == town.Gate[1] && intents[0].PortX == town.Port[0] && intents[1].GateX == castle.Gate[0] && intents[2].X == village.Centre[0]
            && intents[2].PortX == village.Dropoff[0] && intents[2].GateX == village.Centre[0] && missingIds.SequenceEqual(new[] { "mnbl_test_v_unplaced" }) && intents[3].PositionStatus == "pendingEngineValidation" && intents[0].PositionStatus.StartsWith("resolved");
        Check(applied, "the settlement records take the layout's positions (centre, a fortification's gate, a port town's port, a harbour village's drop-off as its port); an unplaced settlement stays staged and is reported");
        var placed = intents.Take(3).ToList();
        var clean = SettlementFactory.CheckResolvedPositions(placed, layoutA.Positions);
        placed[1].GateX += 0.5;
        var moved = SettlementFactory.CheckResolvedPositions(placed, layoutA.Positions);
        var unplaced = SettlementFactory.CheckResolvedPositions(intents, layoutA.Positions);
        Check(clean.Count == 0 && moved.Count == 1 && moved[0].StartsWith(castle.Site.Id + " gate") && unplaced.Any(x => x == "mnbl_test_v_unplaced placement missing"),
            "a record that differs from the placement the scene was built from is found (a castle's gate moved half a unit), as is a record with no placement (MMR-45 refuses such a world)");
        evidence["placementPolicy"] = new Dictionary<string, object> { ["gateDistance"] = policy.GateDistance, ["maxPortCentre"] = policy.MaxPortCentre, ["maxDropoff"] = policy.MaxDropoff, ["maxPadSlope"] = policy.MaxPadSlope, ["maxMarkerSlope"] = policy.MaxMarkerSlope, ["minSeparation"] = policy.MinSeparation };
    }

    // MMR-258: the look catalog from the installed campaign map: every prototype root of the presentation cultures found and normalized (no
    // nested prefab instance, no solid collider, no SceneLeveler or editor data, no source port, arc or drop-off), the siege contract per look,
    // the round trip through its XML, and a look that breaks the contract (three wall sections at a level, no gate) refused.
    private static void LooksCatalogChecks(SettlementLookCatalog catalog, List<SettlementLookCatalog.PoolEntry> pools, SettlementLooksPolicy policy, SortedDictionary<string, object> evidence)
    {
        var byKind = catalog.Looks.Values.GroupBy(l => l.Kind).ToDictionary(g => g.Key, g => g.Count());
        var expected = pools.GroupBy(p => p.Kind).ToDictionary(g => g.Key, g => g.Select(p => p.Id).Distinct().Count());
        Check(byKind["town"] == 57 && byKind["castle"] == 76 && expected.All(kv => byKind[kv.Key] + catalog.Missing.Count(m => pools.Any(p => p.Id == m && p.Kind == kv.Key)) == kv.Value),
            "look catalog: the installed campaign map holds a root for the prototype pools' settlements (" + byKind["town"] + " towns, " + byKind["castle"] + " castles, " + byKind["village"] + " villages; " + catalog.Missing.Count + " prototypes without a root" + (catalog.Missing.Count > 0 ? ": " + String.Join(", ", catalog.Missing.Take(5)) : "") + ")");
        var bad = new List<string>();
        foreach (var look in catalog.Looks.Values)
        {
            var d = new XmlDocument(); d.LoadXml(look.Xml);
            if (d.SelectSingleNode("//game_entity[@prefab or @_index_ or @old_prefab_name]") != null) bad.Add(look.Id + ": prefab or editor attributes");
            if (d.SelectNodes("//physics[@shape]").Cast<XmlElement>().Any(p => p.SelectSingleNode("body_flags/body_flag[@name='only_collide_with_raycast']") == null)) bad.Add(look.Id + ": a solid collider");
            if (d.SelectSingleNode("//tag[@name='main_map_city_port' or @name='Blockade_Arc_Start' or @name='Blockade_Arc_End' or @name='main_map_village_dropoff']") != null) bad.Add(look.Id + ": a source port, arc or drop-off");
            if (d.SelectSingleNode("//script[@name='SceneLeveler' or @name='Town Entity Manager' or @name='BlockadePositionScript'] | //edit_mode_data") != null) bad.Add(look.Id + ": a SceneLeveler, nested manager or edit data");
            if (d.SelectNodes("//visibility_mask[@name='visible_only_when_editing']").Cast<XmlElement>().Any(m => !SettlementLook.Tags((XmlElement)m.ParentNode.ParentNode.ParentNode.ParentNode).Contains(SettlementLook.BannerTag)
                    && m.ParentNode.ParentNode.SelectSingleNode(".//tag") == null)) bad.Add(look.Id + ": an untagged editor-only entity that is not a banner's volume");
            if (SettlementLook.FromXml(look.Xml, policy).Sha256 != look.Sha256) bad.Add(look.Id + ": not stable through its XML");
        }
        int inlined, raycast;
        catalog.Normalized.TryGetValue("prefabsInlined", out inlined); catalog.Normalized.TryGetValue("collidersMadeRaycastOnly", out raycast);
        Check(bad.Count == 0 && inlined > 0 && raycast > 900, "look catalog: every look is normalized (" + String.Join(", ", catalog.Normalized.Select(kv => kv.Key + " " + kv.Value)) + "): no nested prefab instance, no solid collider, no SceneLeveler, nested manager, edit data or untagged editor-only entity, no source port, arc or drop-off, stable through its XML" + (bad.Count > 0 ? " - " + String.Join("; ", bad.Take(5)) : ""));
        var fortifications = catalog.Looks.Values.Where(l => l.Kind != "village").ToList();
        Check(fortifications.All(l => l.Conforms) && catalog.Looks.Values.Where(l => l.Kind == "village").Count(l => l.Conforms) > 250,
            "look catalog: every installed town and castle meets the siege contract (2 wall sections and 4 wall engines per level, at most one solid and one broken piece per wall, a gate, camps, a ram and towers); " + catalog.Looks.Values.Count(l => l.Kind == "village" && l.Conforms) + " of " + byKind["village"] + " villages have one icon per hearth level and a looted one"
            + (fortifications.Any(l => !l.Conforms) ? " - " + String.Join("; ", fortifications.Where(l => !l.Conforms).Take(4).Select(l => l.Id + ": " + l.Problems[0])) : ""));
        var castle = catalog.Looks.Values.First(l => l.Kind == "castle" && l.Conforms);
        var cd = new XmlDocument(); cd.LoadXml(castle.Xml);
        var wall = (XmlElement)cd.SelectSingleNode("//game_entity[tags/tag/@name='map_breachable_wall']");
        wall.ParentNode.AppendChild(wall.CloneNode(true));
        var threeWalls = SettlementLook.FromXml(cd.OuterXml, policy);
        cd.LoadXml(castle.Xml);
        foreach (var t in cd.SelectNodes("//tag[@name='main_map_city_gate']").Cast<XmlNode>().ToList()) t.ParentNode.RemoveChild(t);
        var noGate = SettlementLook.FromXml(cd.OuterXml, policy);
        Check(!threeWalls.Conforms && threeWalls.Problems.Any(p => p.Contains("wall sections")) && !noGate.Conforms, "look catalog: a look with three wall sections at a level, or without a city gate, breaks the contract and is never selected");
        evidence["lookCatalog"] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["looks"] = byKind, ["missingPrototypes"] = catalog.Missing, ["normalized"] = catalog.Normalized,
            ["conforming"] = catalog.Looks.Values.GroupBy(l => l.Kind).ToDictionary(g => g.Key, g => (object)g.Count(l => l.Conforms)),
            ["ownBanners"] = fortifications.Count(l => l.OwnBanners), ["wallsWithoutSolid"] = fortifications.Sum(l => l.Slots.Count(s => s.Tag == "map_breachable_wall" && (s.Children == null || !s.Children.Contains("map_solid_wall")))),
            ["coreRadius"] = catalog.Looks.Values.GroupBy(l => l.Kind).ToDictionary(g => g.Key, g => (object)Math.Round(g.Select(l => l.CoreRadius).OrderBy(x => x).ElementAt(g.Count() / 2), 2)),
            ["radius"] = catalog.Looks.Values.GroupBy(l => l.Kind).ToDictionary(g => g.Key, g => (object)Math.Round(g.Select(l => l.Radius).OrderBy(x => x).ElementAt(g.Count() / 2), 2)) };
    }

    // MMR-258: one world's looks: every town and castle culture-correct (a look of its culture, or its culture's phase-0 pieces), every look's
    // record prototype its own id unless a variant (RT-14), the reuse report (RT-15), and the frozen looks rebuilding the same scene.
    private static void LooksWorldChecks(string label, SettlementLayout layout, IList<SettlementSite> sites, FrozenLooks frozen, SettlementLooksPolicy policy, List<SettlementLookCatalog.PoolEntry> pools,
                                         MNBL.ProceduralWorld.Missions.MissionScenes scenes, IPlacementGround ground, Func<KeyValuePair<string, string>> rebuild, string xml)
    {
        var wrong = new List<string>();
        foreach (var r in layout.Settlements)
        {
            var c = r.Look;
            if (c == null) { wrong.Add(r.Site.Id + ": no look"); continue; }
            if (c.Culture == null || !policy.CultureCodes.ContainsKey(c.Culture)) wrong.Add(r.Site.Id + ": no presentation culture");
            if (c.Source != null && c.Source.Culture != c.Culture) wrong.Add(r.Site.Id + ": a " + c.Source.Culture + " look for a " + c.Culture + " settlement");
            if (c.Source != null && c.Variant == null && (c.Prototype != c.Look || !pools.Any(p => p.Id == c.Look && p.Culture == c.Culture && p.Kind == c.Kind))) wrong.Add(r.Site.Id + ": prototype " + c.Prototype + " is not its look " + c.Look);
            if (c.Prototype != null && scenes.PrototypeProblems(c.Prototype, c.Kind, r.Site.Kind == SiteKind.Town ? r.Site.HasPort : r.Site.Harbour && r.Site.HarbourCell != null).Count > 0) wrong.Add(r.Site.Id + ": prototype " + c.Prototype + " fails the mission scene contracts");
            if (c.Variant == "portGrafted" || c.Variant == "castleAsSmallTown") { if (c.Prototype != null) wrong.Add(r.Site.Id + ": a " + c.Variant + " look names a prototype"); }
            if (r.Site.Kind == SiteKind.Village && c.VillageType == null) wrong.Add(r.Site.Id + ": no village type for its production pieces");
        }
        var report = layout.Looks;
        Func<string, Dictionary<string, object>> kind = k => (Dictionary<string, object>)report[k];
        Check(wrong.Count == 0, label + ": every town, castle and village has a culture-correct look (towns " + kind("town")["looks"] + " looks and " + kind("town")["phase0"] + " phase 0, castles " + kind("castle")["looks"] + " and " + kind("castle")["phase0"] + ", villages " + kind("village")["looks"] + " and " + kind("village")["phase0"]
            + "); a look's record prototype is the look itself unless it is a variant, and meets the mission scene contracts (RT-14); every village has a type for its production pieces" + (wrong.Count > 0 ? " - " + String.Join("; ", wrong.Take(5)) : ""));
        var reuse = new List<string>();
        foreach (var k in new[] { "town", "castle", "village" })
        {
            var row = kind(k);
            reuse.Add(k + "s " + row["distinctLooks"] + " distinct looks for " + row["settlements"] + " (max " + row["maxUses"] + " uses), " + row["neighbourRepeats"] + " of " + row["neighbourPairs"] + " nearest-neighbour pairs share a look, " + row["avoidableRepeats"] + " avoidable");
        }
        Check(kind("town").ContainsKey("usage") && kind("castle").ContainsKey("usage") && kind("village").ContainsKey("usage") && report.ContainsKey("villageProduction")
            && (int)kind("town")["avoidableRepeats"] == 0 && (int)kind("castle")["avoidableRepeats"] == 0 && (int)kind("village")["avoidableRepeats"] == 0,
            label + ": the reuse report lists look usage for towns, castles and villages (RT-15): " + String.Join("; ", reuse) + "; no settlement shares a look with one of its " + policy.Neighbours + " nearest of its kind where a fitting look of its pool was free (a counted repeat had none)");
        var files = frozen.Files();
        var again = rebuild();
        Check(files.ContainsKey(FrozenLooks.SelectionPath) && files.Keys.Count(f => f.StartsWith(FrozenLooks.CatalogFolder, StringComparison.Ordinal)) == frozen.Choices.Values.Where(c => !c.Phase0).Select(c => c.Look).Distinct().Count()
            && again.Key == layout.Sha256 && again.Value == xml,
            label + ": the frozen looks (" + files.Count + " files, " + files.Values.Sum(b => (long)b.Length) / 1024 + " KiB: the selection and each used look's normalized XML) rebuild the same layout and scene entities without the installed campaign map");
    }

    private static T Let<T>(this XmlDocument d, Func<XmlDocument, T> f) { return f(d); }
    private static long Key(double[] p, double cell) { return ((long)Math.Floor(p[0] / cell) << 32) ^ (uint)(long)Math.Floor(p[1] / cell); }

    // BlockadePositionScript.GetBlockadeArc for MaximumNumberOfShips as installed (1.4.8 IL; randomization off), in single precision as the
    // engine computes it: FindCenterOfCircle(end, start), the arcs from NumberOfArcs down, MathF.Round (banker's), Vec3.RotateAboutZ (CCW).
    private static List<double[]> InstalledArc(double[] start, double[] end, IPlacementGround g, SettlementPlacementPolicy policy)
    {
        float angle = Single.Parse(policy.Blockade["Angle"], CultureInfo.InvariantCulture), between = Single.Parse(policy.Blockade["DistanceBetweenShips"], CultureInfo.InvariantCulture);
        int max = Int32.Parse(policy.Blockade["MaximumNumberOfShips"], CultureInfo.InvariantCulture), arcs = Int32.Parse(policy.Blockade["NumberOfArcs"], CultureInfo.InvariantCulture);
        float ex = (float)SettlementPositionResolver.Delta(g, start[0], end[0]), ey = (float)(end[1] - start[1]);
        float mx = (ex - 0) / 2f, my = (ey - 0) / 2f, vx = ex / 2f, vy = ey / 2f;
        float d = (float)Math.Sqrt((ex - mx) * (ex - mx) + (ey - my) * (ey - my));
        float h = d / (float)Math.Tan(angle / 2f);
        float cx = mx + h * vy / d, cy = my - h * vx / d;
        float dirx = ex - cx, diry = ey - cy, len = (float)Math.Sqrt(dirx * dirx + diry * diry); dirx /= len; diry /= len;
        float ring = (float)Math.Sqrt((ex - cx) * (ex - cx) + (ey - cy) * (ey - cy)) / arcs;
        var ships = new List<double[]>(); int placed = 0; float step = between;
        for (int k = arcs; k > 0; k--)
        {
            int n = (int)Math.Round((double)(angle * k / between));
            if (max - placed < n) { step = step * ((float)n / (max - placed)); n = max - placed; }
            float px = dirx, py = diry; Rotate(ref px, ref py, step / (k * 2));
            for (int i = 0; i < n; i++)
            {
                double x = start[0] + cx + px * k * ring;
                ships.Add(new[] { ((x % g.Width) + g.Width) % g.Width, start[1] + cy + py * k * ring });
                Rotate(ref px, ref py, step / k);
                if (++placed >= max) break;
            }
            if (placed >= max) break;
        }
        return ships;
    }

    private static void Rotate(ref float x, ref float y, float a) { float s = (float)Math.Sin(a), c = (float)Math.Cos(a), nx = x * c - y * s; y = y * c + x * s; x = nx; }

    // Replaces one navigation point's resolved position for one resolution.
    private static SettlementLayout Mutate(NavigationPlan plan, string id, double[] to, Func<SettlementLayout> run)
    {
        var p = plan.Point(id); var before = p.Resolved;
        p.Resolved = to;
        try { return run(); } finally { p.Resolved = before; }
    }

    // A ground with a fault near one place: land beyond a radius turned to mountain, or water beyond a radius turned dry.
    private sealed class Faulted : IPlacementGround
    {
        private readonly IPlacementGround inner;
        public double NoLandBeyond = -1, DryBeyond = -1;
        public double[] Around;
        public Faulted(IPlacementGround inner) { this.inner = inner; }
        private double D(double x, double y) { return SettlementPositionResolver.Distance(inner, Around, new[] { x, y }); }
        public double Width { get { return inner.Width; } }
        public double Height { get { return inner.Height; } }
        public double Spacing { get { return inner.Spacing; } }
        public byte Class(double x, double y) { return NoLandBeyond >= 0 && D(x, y) > NoLandBeyond && D(x, y) < 20 && inner.Land(x, y) >= 0 ? TravelCells.Mountain : inner.Class(x, y); }
        public int Land(double x, double y) { return NoLandBeyond >= 0 && D(x, y) > NoLandBeyond && D(x, y) < 20 ? -1 : inner.Land(x, y); }
        public int Basin(double x, double y) { return DryBeyond >= 0 && D(x, y) > DryBeyond && D(x, y) < 40 ? -1 : inner.Basin(x, y); }
        public double Elevation(double x, double y) { return inner.Elevation(x, y); }
        public double Slope(double x, double y) { return inner.Slope(x, y); }
        public double[] Physical(double x, double y) { return inner.Physical(x, y); }
    }

    // Water taken away within a radius of given points (an arc's ship positions).
    private sealed class DryAround : IPlacementGround
    {
        private readonly IPlacementGround inner; private readonly IList<double[]> points; private readonly double radius;
        public DryAround(IPlacementGround inner, IList<double[]> points, double radius) { this.inner = inner; this.points = points; this.radius = radius; }
        public double Width { get { return inner.Width; } }
        public double Height { get { return inner.Height; } }
        public double Spacing { get { return inner.Spacing; } }
        public byte Class(double x, double y) { return inner.Class(x, y); }
        public int Land(double x, double y) { return inner.Land(x, y); }
        public int Basin(double x, double y) { var q = new[] { x, y }; return points.Any(p => SettlementPositionResolver.Distance(inner, p, q) < radius) ? -1 : inner.Basin(x, y); }
        public double Elevation(double x, double y) { return inner.Elevation(x, y); }
        public double Slope(double x, double y) { return inner.Slope(x, y); }
        public double[] Physical(double x, double y) { return inner.Physical(x, y); }
    }

    private static bool Throws(Action action) { try { action(); return false; } catch (Exception) { return true; } }
    private static string Sha(string text) { using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(new UTF8Encoding(false).GetBytes(text))).Replace("-", "").ToLowerInvariant(); }
}
