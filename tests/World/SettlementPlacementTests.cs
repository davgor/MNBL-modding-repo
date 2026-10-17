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

// MMR-40 settlement placement tests, without the game:
//   SettlementPlacementTests REPO BUNDLE_A BUNDLE_B GAME_ROOT OUT_JSON REPORT_JSON
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
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; string line = (condition ? "ok    " : "FAIL  ") + label; lines.Add(line); Console.WriteLine(line); }

    private static int Main(string[] args)
    {
        if (args.Length != 6) { Console.Error.WriteLine("usage: SettlementPlacementTests REPO BUNDLE_A BUNDLE_B GAME_ROOT OUT_JSON REPORT_JSON"); return 2; }
        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = "mnbl.evidence.settlement-placement-tests", ["version"] = 2, ["ticket"] = "MMR-40" };
        var report = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = "mnbl.evidence.settlement-position-report", ["version"] = 2, ["ticket"] = "MMR-40" };
        try { Run(args, evidence, report); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        evidence["checks"] = checks; evidence["failures"] = failures; evidence["lines"] = lines;
        File.WriteAllText(args[4], Serializer.Serialize(evidence), new UTF8Encoding(false));
        File.WriteAllText(args[5], Serializer.Serialize(report), new UTF8Encoding(false));
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string[] args, SortedDictionary<string, object> evidence, SortedDictionary<string, object> report)
    {
        string repo = args[0], gameRoot = args[3];
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", new[] { "R01", "R02", "R03", "R04", "R05", "R06", "R07", "R08", "R09", "R10" });
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
            var layout = SettlementPositionResolver.Resolve(sites, plan, ground, policy);
            var refusals = layout.Problems.Where(p => p.Refusal).ToList();
            Check(layout.Activatable, label + ": the layout has no refusal" + (refusals.Count > 0 ? " (" + refusals.Count + ": " + String.Join("; ", refusals.Take(5)) + ")" : ""));
            Check(layout.Settlements.Count == sites.Count, label + ": every site is resolved");
            var again = SettlementPositionResolver.Resolve(SettlementPositionResolver.SitesFromBundle(input, campaignJson, portsJson), NavigationPlan.Create(RuntimeNavigationBuilder.Build(input, navPolicy), model), ground, policy);
            Check(again.Sha256 == layout.Sha256 && Serializer.Serialize(again.Report(ground)) == Serializer.Serialize(layout.Report(ground)), label + ": the layout and its report are deterministic (" + layout.Sha256.Substring(0, 12) + ")");

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
                    if (r.Gate == null || !onLand(r.Gate, r.GateSteep) || SettlementPositionResolver.Distance(ground, r.Centre, r.Gate) > policy.GateDistance + 1e-9) bad.Add(r.Site.Id + ": gate invalid");
                    else everyMarker.Add(new KeyValuePair<string, double[]>(r.Site.Id, r.Gate));
                    if (r.Markers.Count(m => m.Level == null) != policy.Markers.Count + policy.SiegeTowers - 1 || r.Markers.Count(m => m.Level != null) != 12 || r.Walls.Count != 6) bad.Add(r.Site.Id + ": " + r.Markers.Count + " markers, " + r.Walls.Count + " walls");
                    foreach (var m in r.Markers.Concat(r.Walls))
                    {
                        if (m.Level == null) markers++; else wallMarkers++;
                        if (m.Turns != 0 || m.Scale < 1 || m.Steep) adjusted++; if (m.Steep) steepMarkers++;
                        var o = m.Level == null ? policy.Markers[m.Tag] : m.Tag == "map_breachable_wall" ? policy.Walls[m.Level + "/" + m.Suffix.Substring(m.Suffix.Length - 1)] : policy.WallEngines[m.Tag.Substring(m.Tag.Length - 1) + "/" + m.Level];
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

            // The scene entities.
            var specs = SettlementEntityFactory.Build(layout, ground, contract, policy, "mnbl_world", surface.Extent, surface.High + 100);
            string xml = SettlementEntityFactory.ToXml(specs);
            string template = "<scene name=\"test\">\n\t<levels>\n\t\t<level name=\"base\" mask=\"1\" />\n\t\t<level name=\"level_1\" mask=\"2\" />\n\t\t<level name=\"level_2\" mask=\"4\" />\n\t\t<level name=\"level_3\" mask=\"8\" />\n\t</levels>\n<entities>\n</entities></scene>";
            string scene = SettlementEntityFactory.DeclareSceneLevels(SettlementEntityFactory.Splice(template, xml));
            var problems = SettlementEntityFactory.Validate(scene, layout, ground, policy, "mnbl_world");
            Check(problems.Count == 0, label + ": the scene entities meet the structural contract (scene levels; one root per settlement named its id with its kind's tag and Town Entity Manager, circle, selection, banner placeholders with one volume_box each, village icons per level, gate, every marker, wall section and wall engine by name with its tag, offset and levels, each wall with a solid and a broken child, port and blockade arc with BlockadePositionScript on port towns with an arc, drop-offs on harbour villages; unique names; borders; the baked helper only)" + (problems.Count > 0 ? " - " + String.Join("; ", problems.Take(6)) : ""));
            Check(SettlementEntityFactory.ToXml(SettlementEntityFactory.Build(layout, ground, contract, policy, "mnbl_world", surface.Extent, surface.High + 100)) == xml, label + ": the entity XML is deterministic (the encoding's identity hashes it)");
            var doc = new XmlDocument(); doc.LoadXml(scene);
            int roots = doc.SelectNodes("/scene/entities/game_entity").Count, entities = doc.SelectNodes("//game_entity").Count;
            Check(roots == 3 + layout.Settlements.Count && doc.SelectNodes("//script[@name='CampaignMapSiegePrefabEntityCache']").Count == 0, label + ": " + roots + " root entities: 2 borders, the Town Scene Manager helper and one per settlement (" + entities + " entities in all); no siege prefab cache (its script throws in a read without a campaign, run 25412; GeneratedMapScene.Load adds it to the campaign map)");
            var withCache = SettlementEntityFactory.Splice(scene, "<game_entity name=\"mnbl_world_helper_campaignmapsiegeprefabentitycache\">" + contract.Helpers["CampaignMapSiegePrefabEntityCache"].Replace("<script", "<scripts><script").Replace("</script>", "</script></scripts>") + "</game_entity>");
            Check(SettlementEntityFactory.Validate(withCache, layout, ground, policy, "mnbl_world").Any(p => p.Contains("CampaignMapSiegePrefabEntityCache")), label + ": a scene carrying a baked siege prefab cache fails the contract");
            // The engine's rules over the entities: SettlementVisual.SetSettlementLevelVisibility shows a child whose level mask holds the visual's
            // current mask (a child without levels has every level); UpdateDefenderSiegeEntitiesCache keeps the wall sections and wall engines whose
            // mask has the current wall level's bit.
            var levelProblems = new List<string>();
            Func<EntitySpec, uint> mask = sp => sp.Levels.Count == 0 ? UInt32.MaxValue : SettlementEntityFactory.LevelMask(sp.Levels);
            foreach (var sp in specs.Skip(3))
            {
                var rs = layout.Find(sp.Name);
                if (rs.Site.Kind == SiteKind.Village)
                    foreach (var state in new[] { "civilian", "looted" })
                        for (int level = 1; level <= 3; level++)
                        {
                            uint current = SettlementEntityFactory.LevelMask(new[] { "level_" + level, state });
                            int visible = sp.Children.Count(c => c.Name.StartsWith(sp.Name + "_icon", StringComparison.Ordinal) && (mask(c) & current) == current);
                            if (visible != 1) levelProblems.Add(sp.Name + ": " + visible + " icons visible at level_" + level + " " + state);
                        }
                if (!rs.Site.Fortification) continue;
                for (int level = 1; level <= 3; level++)
                {
                    uint bit = SettlementEntityFactory.LevelMask(new[] { "level_" + level });
                    int wallCache = sp.Children.Count(c => c.Tags.Contains("map_breachable_wall") && (mask(c) & bit) != 0), engineCache = sp.Children.Count(c => c.Tags.Any(t => t.StartsWith("map_defensive_engine")) && (mask(c) & bit) != 0);
                    foreach (var state in new[] { "civilian", "siege" })
                    {
                        uint current = SettlementEntityFactory.LevelMask(new[] { "level_" + level, state });
                        if (sp.Children.Count(c => c.Tags.Contains("map_breachable_wall") && (mask(c) & current) == current) != 2) levelProblems.Add(sp.Name + ": walls at level_" + level + " " + state + " not 2 visible");
                    }
                    if (wallCache != 2 || engineCache != 4) levelProblems.Add(sp.Name + ": level_" + level + " caches " + wallCache + " walls and " + engineCache + " engines");
                }
                if (sp.Children.Any(c => c.Levels.Count == 0 && c.Tags.Contains("map_settlement_circle") && (c.Scale == null || c.Scale[0] != policy.CircleScale[rs.Site.Kind == SiteKind.Town ? "town" : "castle"]))) levelProblems.Add(sp.Name + ": circle not at the installed scale");
                foreach (var camp in policy.CampScale) if (!sp.Children.Any(c => c.Tags.Contains(camp.Key) && c.Scale != null && c.Scale[0] == camp.Value[0] && c.Scale[1] == camp.Value[1])) levelProblems.Add(sp.Name + ": " + camp.Key + " not at the installed scale");
                if (sp.Children.Where(c => c.Tags.Any(t => t == "main_map_city_gate" || t.StartsWith("map_siege") || t.StartsWith("map_camp") || t == "map_breachable_wall")).Any(c => Math.Abs(c.Yaw - (rs.GateAngle - Math.PI / 2)) > 1e-12)) levelProblems.Add(sp.Name + ": a marker does not face the gate (yaw " + (rs.GateAngle - Math.PI / 2) + ")");
            }
            Check(levelProblems.Count == 0, label + ": with the engine's level rules exactly one village icon shows in each state (level 1-3, civilian or looted); each wall level shows its two wall sections in civilian and siege and caches two sections and four wall engines; circles and camp areas at the installed scales; every marker faces the gate (yaw = gate angle - pi/2)" + (levelProblems.Count > 0 ? " - " + String.Join("; ", levelProblems.Take(6)) : ""));
            var perKind = specs.Skip(3).GroupBy(sp => sp.Tags.FirstOrDefault() ?? "-").ToDictionary(g => g.Key, g => (object)g.Average(sp => sp.SelfAndDescendants().Count()));
            // Breaking the scene is caught.
            var broken = scene.Replace("main_map_city_port", "main_map_city_prt");
            Check(SettlementEntityFactory.Validate(broken, layout, ground, policy, "mnbl_world").Any(p => p.Contains("main_map_city_port")), label + ": a scene whose port children are mistagged fails the contract");
            var noWall = scene.Replace("<tag name=\"map_solid_wall\" />", "<tag name=\"map_solid_wal\" />");
            Check(SettlementEntityFactory.Validate(noWall, layout, ground, policy, "mnbl_world").Any(p => p.Contains("solid and one broken")), label + ": a scene whose walls lack their solid child fails the contract");
            Check(SettlementEntityFactory.Validate(SettlementEntityFactory.Splice(template, xml), layout, ground, policy, "mnbl_world").Any(p => p.Contains("scene level civilian")), label + ": a scene that does not declare the campaign map's levels fails the contract");
            var firstVillage = layout.Settlements.First(r => r.Site.Kind == SiteKind.Village).Site.Id;
            var missing = doc.SelectSingleNode("/scene/entities/game_entity[@name='" + firstVillage + "']"); missing.ParentNode.RemoveChild(missing);
            Check(SettlementEntityFactory.Validate(doc.OuterXml, layout, ground, policy, "mnbl_world").Any(p => p.StartsWith(firstVillage + ": no root")), label + ": a scene missing a settlement's entity fails the contract");

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
        var walled = new Faulted(groundA) { NoLandBeyond = 0.2, Around = town.Centre };
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
