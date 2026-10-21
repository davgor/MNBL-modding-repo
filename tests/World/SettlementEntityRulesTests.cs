using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Surface;
using MNBL.ProceduralWorld.World;

// MMR-247 settlement entity rules, without the game:
//   SettlementEntityRulesTests REPO BUNDLE_A BUNDLE_B GAME_ROOT OUT_JSON [--scene LABEL=PATH]... [--detail LABEL=TEXT]... [--control PATH]...
// For worlds A' and B': the production look plan and layout (RuntimeWorldBackend.Prepare's), the settlement entity XML baked as Prepare
// bakes it (SettlementEntityFactory.Build, ToXml, the water bodies, Splice, DeclareSceneLevels), then the runtime stage's own rules
// (SettlementEntityRules, the code SettlementEntityChecks runs on the engine's inspection read) over that XML, with the offline navigation
// mesh's faces, read two ways:
//   authored             the XML as written
//   same-level-visuals   the model of acceptance run acc1's engine read (XmlEntityScene): a visual entity with an explicit level set another
//                        visual entity of its settlement already has is gone
// Both must have no failure. Also:
//   --scene    a scene file another build baked for a world (an in-game world scene, <store>/world-scenes/<name>/scene.xscene), checked against
//              this build's layout of that world under both reads; with --detail, the model's runtime failure detail must be that text (the
//              in-game diagnostics' "detail")
//   --control  a scene whose engine read passed the same check in game (a pre-MMR-258 world scene): the model must drop none of its
//              settlements' direct children
// The village icon rule (SettlementEntityFactory.Validate): no entity of a village but its icons has an icon's level set.
// The resolved-visuals rule (acc4 V2, UnresolvedVisuals): prefab overrides merged in the catalog, no unresolved visual piece in any look, the
// look builder's filter, and Validate refusing a nameless meta mesh or a drawn volume box.
internal static class SettlementEntityRulesTests
{
    private static int checks, failures;
    private static readonly List<string> lines = new List<string>();
    private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; string line = (condition ? "ok    " : "FAIL  ") + label; lines.Add(line); Console.WriteLine(line); }

    private static int Main(string[] args)
    {
        if (args.Length < 5) { Console.Error.WriteLine("usage: SettlementEntityRulesTests REPO BUNDLE_A BUNDLE_B GAME_ROOT OUT_JSON [--scene LABEL=PATH]... [--detail LABEL=TEXT]... [--control PATH]..."); return 2; }
        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = "mnbl.evidence.settlement-entity-rules", ["version"] = 1, ["ticket"] = "MMR-247" };
        try { Run(args, evidence); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        evidence["checks"] = checks; evidence["failures"] = failures; evidence["lines"] = lines;
        File.WriteAllText(args[4], Serializer.Serialize(evidence), new UTF8Encoding(false));
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string[] args, SortedDictionary<string, object> evidence)
    {
        string repo = args[0], gameRoot = args[3];
        var scenes = new Dictionary<string, string>(StringComparer.Ordinal); var details = new Dictionary<string, string>(StringComparer.Ordinal); var controls = new List<string>();
        for (int i = 5; i + 1 < args.Length; i += 2)
        {
            string value = args[i + 1]; int eq = value.IndexOf('=');
            if (args[i] == "--control") { controls.Add(value); continue; }
            if (eq <= 0 || (args[i] != "--scene" && args[i] != "--detail")) throw new ArgumentException("Bad option " + args[i] + " " + value);
            (args[i] == "--scene" ? scenes : details)[value.Substring(0, eq)] = value.Substring(eq + 1);
        }
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", WorldDataBundle.HostCapabilities);
        var navPolicy = NavigationPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, NavigationPolicy.PolicyPath)));
        var calibration = (IDictionary<string, object>)Serializer.DeserializeObject(File.ReadAllText(Path.Combine(repo, "worldgen/calibration/naval-transitions/naval-transitions.json")).TrimStart('﻿'));
        var model = NavigationModel.FromCalibration(calibration);
        var policy = SettlementPlacementPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, SettlementPlacementPolicy.PolicyPath)), File.ReadAllBytes(Path.Combine(repo, SettlementPlacementPolicy.NavalPath)));
        var mainMap = File.ReadAllBytes(Path.Combine(gameRoot, "Modules", "NavalDLC", "SceneObj", "Main_map", "scene.xscene"));
        var contract = SettlementEntityContract.FromInstalled(mainMap);
        var looksPolicy = SettlementLooksPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, SettlementLooksPolicy.PolicyPath)));
        byte[] prototypesBytes = File.ReadAllBytes(Path.Combine(repo, "worldgen/templates/settlements/prototypes.json")), economyBytes = File.ReadAllBytes(Path.Combine(repo, "worldgen/policies/economy.json"));
        var pinDefaults = new HashSet<string>(((object[])((IDictionary<string, object>)Serializer.DeserializeObject(File.ReadAllText(Path.Combine(repo, "worldgen/policies/campaign-catalog.json")).TrimStart('﻿')))["defaults"]).Cast<string>(), StringComparer.Ordinal);
        var missionScenes = MNBL.ProceduralWorld.Missions.MissionScenes.FromBytes(File.ReadAllBytes(Path.Combine(repo, "worldgen/policies/mission-scenes.json")));
        var prefabFolders = new[] { "NavalDLC", "SandBox", "Native" }.Select(m => Path.Combine(gameRoot, "Modules", m, "Prefabs")).ToList();
        var catalog = SettlementLookCatalog.FromInstalled(mainMap, SettlementLookCatalog.PrefabResolver(prefabFolders), prototypesBytes, looksPolicy);
        // LookBuilder.Icons moves a look's direct children with an icon's level set under that icon; a deeper one would stay and refuse the
        // world (Validate). No installed village look has one (outside its icons, which the scene replaces).
        var iconSetNames = new[] { "level_1,civilian", "level_2,civilian", "level_3,civilian", "level_1,level_2,level_3,looted" }.Select(Set).ToList();
        var deep = new List<string>(); int villageLooks = 0;
        foreach (var look in catalog.Looks.Values.Where(l => l.Kind == "village"))
        {
            villageLooks++;
            foreach (XmlElement e in look.Root.SelectNodes(".//game_entity"))
            {
                bool underIcon = false;
                for (var x = e; x != null && x != look.Root; x = x.ParentNode as XmlElement) if (x.Name == "game_entity" && SettlementLook.IconLevel(x) >= 0) underIcon = true;
                if (underIcon || e.ParentNode == look.Root) continue;
                if (iconSetNames.Contains(Set(String.Join(",", SettlementLook.LevelNames(e))))) deep.Add(look.Id + "/" + e.GetAttribute("name"));
            }
        }
        Check(villageLooks > 0 && deep.Count == 0, "catalog: none of the " + villageLooks + " village looks has an entity with an icon's level set below its direct children (outside its icons)" + (deep.Count > 0 ? " - " + deep.Count + ": " + String.Join(", ", deep.Take(5)) : ""));
        UnresolvedVisuals(catalog, evidence);
        var worlds = new SortedDictionary<string, object>(StringComparer.Ordinal); evidence["worlds"] = worlds;
        foreach (var label in new[] { "A", "B" })
        {
            var bundle = WorldDataBundle.Open(label == "A" ? args[1] : args[2], host);
            var surface = WorldSurface.FromBundle(bundle);
            var input = NavigationInput.FromBundle(bundle, surface, navPolicy);
            var build = RuntimeNavigationBuilder.Build(input, navPolicy);
            var plan = NavigationPlan.Create(build, model);
            var ground = new NavigationGround(build);
            byte[] campaignJson = bundle.ReadVerified("compiled/campaign/campaign.json"), portsJson = bundle.ReadVerified("compiled/travel/ports.json");
            var inputs = SettlementLookPlan.ForCampaign(campaignJson, bundle.ReadVerified("compiled/travel/travel-graph.json"), portsJson, economyBytes, prototypesBytes, pinDefaults.Contains, looksPolicy);
            inputs.Catalog = catalog; inputs.Conforms = (p, k, port) => missionScenes.PrototypeProblems(p, k, port).Count == 0;
            FrozenLooks frozen;
            var layout = SettlementLookPlan.Resolve(SettlementPositionResolver.SitesFromBundle(input, campaignJson, portsJson), plan, ground, policy, inputs, out frozen);
            Check(plan.Activatable && layout.Activatable, label + ": the navigation plan and the look layout are activatable (" + bundle.WorldContentId.Substring(0, 12) + ", layout " + layout.Sha256.Substring(0, 12) + ")");
            // Prepare's bake: the settlement entities, the water bodies (sea and lakes) and the campaign map's scene levels.
            string settlementXml = SettlementEntityFactory.ToXml(SettlementEntityFactory.Build(layout, ground, contract, policy, looksPolicy, "mnbl_world", surface.Extent, SettlementEntityFactory.MapMaximumHeight(surface.High)));
            byte[] worldJson = bundle.ReadVerified("source/world.json");
            GeneratedClimateSystem.Source source;
            using (var stream = new MemoryStream(worldJson)) source = GeneratedClimateSystem.ReadSource(stream, GeneratedClimateSystem.Sha(worldJson));
            int unmatched, hidden;
            var lakes = GeneratedWaterSystem.Lakes(surface, source, PeriodicWorldCoordinates.FromSurface(surface), out unmatched, out hidden);
            string template = "<scene name=\"mnbl_rules_" + label + "\">\n\t<levels>\n\t\t<level mask=\"1\" name=\"base\"/>\n\t\t<level mask=\"2\" name=\"level_1\"/>\n\t\t<level mask=\"4\" name=\"level_2\"/>\n\t\t<level mask=\"8\" name=\"level_3\"/>\n\t</levels>\n<entities>\n</entities></scene>";
            string scene = SettlementEntityFactory.DeclareSceneLevels(SettlementEntityFactory.Splice(GeneratedWaterSystem.WithWaterBodies(template, "mnbl_rules_" + label + "_water", surface.Extent, lakes), settlementXml));
            var contractProblems = SettlementEntityFactory.Validate(scene, layout, ground, policy, "mnbl_world");
            Check(contractProblems.Count == 0, label + ": the baked scene meets SettlementEntityFactory.Validate, the village icon rule and the resolved-visuals rule included (no entity of a village but its icons has an icon's level set; every meta mesh names a mesh, no editor helper mesh draws)" + (contractProblems.Count > 0 ? " - " + contractProblems.Count + ": " + String.Join("; ", contractProblems.Take(6)) : ""));
            // The resolved-visuals rule refuses a settlement holding a nameless meta mesh (acc4 V2's magenta box) or a drawn volume box.
            var fault = new XmlDocument { XmlResolver = null }; fault.LoadXml(scene.TrimStart('﻿'));
            var village = layout.Settlements.First(x => x.Site.Kind == SiteKind.Village).Site.Id;
            var holder = (XmlElement)fault.SelectSingleNode("/scene/entities/game_entity[@name='" + village + "']/children/game_entity[@name='" + village + "_look']");
            var piece = fault.CreateElement("game_entity"); piece.SetAttribute("name", village + "_fault");
            piece.InnerXml = "<components><meta_mesh_component><mesh name=\"mi_salt_mine_c\" argument=\"-1.900, 0.000, 0.000, 0.000\" /></meta_mesh_component></components>";
            (holder.SelectSingleNode("children") ?? holder.AppendChild(fault.CreateElement("children"))).AppendChild(piece);
            bool nameless = SettlementEntityFactory.Validate(fault.OuterXml, layout, ground, policy, "mnbl_world").Any(p => p.StartsWith(village + ": 1 visual pieces do not resolve (a meta mesh component names no mesh", StringComparison.Ordinal));
            piece.InnerXml = "<components><meta_mesh_component name=\"volume_box\" /></components>";
            bool helper = SettlementEntityFactory.Validate(fault.OuterXml, layout, ground, policy, "mnbl_world").Any(p => p.StartsWith(village + ": 1 visual pieces do not resolve (the editor helper mesh volume_box", StringComparison.Ordinal));
            Check(nameless && helper, label + ": Validate refuses a village whose look holds a nameless meta mesh component, or a volume_box outside an editor-only entity");
            Func<double, double, bool, int[]> faces = (x, y, land) => { int f = plan.Topology.Locate(x, y, land, model); return new[] { f, f < 0 ? -1 : build.Mesh.Groups[f] }; };
            var world = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["worldContentId"] = bundle.WorldContentId, ["layoutSha256"] = layout.Sha256, ["settlementXmlSha256"] = Sha(settlementXml),
                ["lakes"] = lakes.Count, ["villageTwins"] = VillageTwins(scene, layout).Count };
            worlds[label] = world;
            foreach (var read in new[] { XmlEntityScene.Authored, XmlEntityScene.SameLevelVisuals })
            {
                var r = Rules(scene, read, faces, layout, ground, model, policy, lakes.Count);
                world[read] = r;
                Check((bool)r["passed"], label + " (" + read + " read): the runtime stage's settlement entity rules pass on the baked scene: " + r["found"] + "/" + r["settlements"] + " settlements, " + r["children"] + " direct children, "
                    + r["levelChecks"] + " level checks, " + r["positionChecks"] + " positions, " + r["landFaceChecks"] + " land and " + r["seaFaceChecks"] + " sea faces, " + r["rootEntities"] + " roots; " + r["dropped"] + " entities the read drops"
                    + ((bool)r["passed"] ? "" : " - " + r["detail"]));
            }
            string external;
            if (scenes.TryGetValue(label, out external))
            {
                string text = File.ReadAllText(external);
                var outside = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["path"] = external, ["sha256"] = Sha(text), ["sameSettlementEntitiesAsThisBuild"] = SettlementRoots(text) == SettlementRoots(scene) };
                world["externalScene"] = outside;
                foreach (var read in new[] { XmlEntityScene.Authored, XmlEntityScene.SameLevelVisuals })
                {
                    var r = Rules(text, read, faces, layout, ground, model, policy, lakes.Count);
                    outside[read] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["passed"] = r["passed"], ["detail"] = r["detail"], ["failureCount"] = r["failureCount"], ["dropped"] = r["dropped"],
                        ["failureKinds"] = ((List<string>)r["failures"]).GroupBy(Kind).ToDictionary(g => g.Key, g => (object)g.Count()) };
                    Console.WriteLine("info  " + label + " external scene, " + read + " read: " + r["detail"] + " (" + r["failureCount"] + " failures in all; " + r["dropped"] + " entities dropped)");
                }
                // Every settlement entity the external scene has, by name, stands in this build's bake with the same global frame (origin and
                // scaled axes, LookFrame): an entity moved under another parent (LookBuilder.Icons) keeps its place.
                var theirs = Frames(text); var ours = Frames(scene);
                int moved = 0; double worst = 0;
                foreach (var kv in theirs)
                {
                    KeyValuePair<string, LookFrame> mine;
                    if (!ours.TryGetValue(kv.Key, out mine)) { worst = Double.PositiveInfinity; continue; }
                    if (mine.Key != kv.Value.Key) moved++;
                    worst = Math.Max(worst, new[] { 0, 1, 2 }.Max(i => new[] { Math.Abs(mine.Value.O[i] - kv.Value.Value.O[i]), Math.Abs(mine.Value.S[i] - kv.Value.Value.S[i]), Math.Abs(mine.Value.F[i] - kv.Value.Value.F[i]), Math.Abs(mine.Value.U[i] - kv.Value.Value.U[i]) }.Max()));
                }
                outside["entities"] = theirs.Count; outside["entitiesHere"] = ours.Count; outside["reparented"] = moved; outside["worstFrameDifference"] = worst;
                Check(theirs.Count == ours.Count && worst < 1e-6, label + ": the " + theirs.Count + " settlement entities of the external scene are this bake's, each at the same global frame (worst " + worst.ToString("0.###E+0", CultureInfo.InvariantCulture) + "), " + moved + " of them under another parent");
                string expected;
                if (details.TryGetValue(label, out expected))
                {
                    string modelled = (string)((SortedDictionary<string, object>)outside[XmlEntityScene.SameLevelVisuals])["detail"];
                    Check(modelled == expected && (bool)((SortedDictionary<string, object>)outside[XmlEntityScene.Authored])["passed"],
                        label + ": the external scene passes as authored, and the same-level-visuals read gives the in-game detail exactly (\"" + expected + "\"; modelled \"" + modelled + "\")");
                }
            }
        }
        var controlResults = new List<object>(); evidence["controls"] = controlResults;
        foreach (var path in controls)
        {
            string text = File.ReadAllText(path);
            var doc = new XmlDocument { XmlResolver = null }; doc.LoadXml(text.TrimStart('﻿'));
            var settlementRoots = doc.SelectNodes("/scene/entities/game_entity[tags/tag/@name='town' or tags/tag/@name='castle' or tags/tag/@name='village']").Cast<XmlElement>().ToList();
            var directNames = new HashSet<string>(settlementRoots.SelectMany(e => e.SelectNodes("children/game_entity").Cast<XmlElement>()).Select(e => e.GetAttribute("name")), StringComparer.Ordinal);
            var read = new XmlEntityScene(text, (x, y, land) => new[] { -1, -1 }, XmlEntityScene.SameLevelVisuals);
            int lost = read.DroppedNames.Count(directNames.Contains);
            controlResults.Add(new Dictionary<string, object> { ["path"] = path, ["settlements"] = settlementRoots.Count, ["dropped"] = read.Dropped, ["directChildrenDropped"] = lost });
            Check(settlementRoots.Count > 0 && lost == 0, "control " + Path.GetFileName(Path.GetDirectoryName(path)) + ": the same-level-visuals read drops none of the " + settlementRoots.Count + " settlements' direct children (" + read.Dropped + " entities in all), as its engine read kept them in game");
        }
    }

    // MMR-247 (acc4 V2): the installed looks' prefab overrides are merged onto their prefabs (a nameless meta mesh left on an inlined holder
    // drew the magenta box beside Hakndum), no normalized look holds a visual piece that does not resolve, and the look builder's filter
    // removes nameless meta meshes and drawn editor helper meshes while keeping editor-only ones and real meshes.
    private static void UnresolvedVisuals(SettlementLookCatalog catalog, SortedDictionary<string, object> evidence)
    {
        var bad = new List<string>();
        foreach (var look in catalog.Looks.Values)
            foreach (XmlElement c in look.Root.SelectNodes(".//components/meta_mesh_component"))
            {
                string why = SettlementEntityRules.UnresolvedVisual(c);
                if (why != null) bad.Add(look.Id + "/" + ((XmlElement)c.ParentNode.ParentNode).GetAttribute("name") + ": " + why);
            }
        int merged, dropped; catalog.Normalized.TryGetValue("prefabOverridesMerged", out merged); catalog.Normalized.TryGetValue("prefabOverridesDropped", out dropped);
        evidence["prefabOverridesMerged"] = merged; evidence["prefabOverridesDropped"] = dropped; evidence["unresolvedVisualsInCatalog"] = bad.Count;
        Check(merged > 0 && bad.Count == 0, "catalog: " + merged + " prefab instance overrides merged onto their prefabs (" + dropped + " with nothing to edit dropped); none of the " + catalog.Looks.Count + " looks holds a visual piece that does not resolve" + (bad.Count > 0 ? " - " + bad.Count + ": " + String.Join("; ", bad.Take(4)) : ""));
        SettlementLook mine;
        if (catalog.Looks.TryGetValue("castle_village_ES5_2", out mine))
        {
            var holder = mine.Root.SelectNodes(".//game_entity[@name='map_icons_productions_saltmine_c']").Cast<XmlElement>().FirstOrDefault(e => e.SelectSingleNode("children/game_entity[@name='map_icons_productions_saltmine_c']") != null);
            var inner = holder == null ? null : holder.SelectSingleNode("children/game_entity[@name='map_icons_productions_saltmine_c']/components/meta_mesh_component[@name='mi_salt_mine_c']") as XmlElement;
            Check(holder != null && holder.SelectSingleNode("components") == null && inner != null
                  && inner.SelectSingleNode("mesh[@name='mi_salt_mine_c' and @argument='-1.900, 0.000, 0.000, 0.000']") != null && inner.SelectSingleNode("mesh[@name='mi_salt_mine_c.lod5']") != null
                  && inner.SelectSingleNode("mesh[@name='mi_salt_mine_c.1' and @argument='40.000, 0.100, 0.000, 0.000']") != null,
                "catalog: castle_village_ES5_2's salt mine (acc4's Sulkkas, the magenta box): the instance's holder keeps no component, its prefab's mi_salt_mine_c carries the instance's argument -1.9 on its own meshes and keeps its mi_salt_mine_c.1");
        }
        else Check(false, "catalog: no castle_village_ES5_2 look to check acc4's case on");
        var doc = new XmlDocument { XmlResolver = null };
        doc.LoadXml("<look><game_entity name=\"nameless\"><components><meta_mesh_component><mesh name=\"mi_salt_mine_c\" /></meta_mesh_component></components></game_entity>"
            + "<game_entity name=\"drawn_volume\"><components><meta_mesh_component name=\"volume_box\" /></components></game_entity>"
            + "<game_entity name=\"banner\"><tags><tag name=\"map_banner_placeholder\" /></tags><children><game_entity name=\"editor_volume\"><visibility_masks><visibility_mask name=\"visible_only_when_editing\" value=\"true\" /></visibility_masks>"
            + "<components><meta_mesh_component name=\"volume_box\" /></components></game_entity></children></game_entity>"
            + "<game_entity name=\"mine\"><components><meta_mesh_component name=\"mi_iron_mine_a\" /><meta_mesh_component name=\"animation_stand_arrow_blue\" /></components></game_entity>"
            + "<game_entity name=\"border\"><components><meta_mesh_component name=\"editor_cube\"><mesh name=\"editor_cube\" material=\"desert_border_mat_c\" /></meta_mesh_component></components></game_entity></look>");
        var removed = new List<string>();
        int filtered = SettlementEntityRules.FilterUnresolvedVisuals(doc.DocumentElement, removed);
        Check(filtered == 3 && doc.SelectSingleNode("//game_entity[@name='nameless']/components") == null && doc.SelectSingleNode("//game_entity[@name='drawn_volume']/components") == null
              && doc.SelectSingleNode("//game_entity[@name='editor_volume']/components/meta_mesh_component[@name='volume_box']") != null
              && doc.SelectSingleNode("//game_entity[@name='mine']/components/meta_mesh_component[@name='mi_iron_mine_a']") != null && doc.SelectSingleNode("//meta_mesh_component[@name='animation_stand_arrow_blue']") == null
              && doc.SelectSingleNode("//game_entity[@name='border']/components/meta_mesh_component[@name='editor_cube']") != null,
            "filter: FilterUnresolvedVisuals removes a nameless meta mesh, a drawn volume_box and a drawn animation stand (" + filtered + ": " + String.Join("; ", removed) + "), and keeps the banner's editor-only volume_box, the mine mesh and an editor_cube given a material of its own (installed town_EN1's border)");
    }

    private static Dictionary<string, object> Rules(string scene, string read,Func<double, double, bool, int[]> faces, SettlementLayout layout, IPlacementGround ground, NavigationModel model, SettlementPlacementPolicy policy, int lakes)
    {
        var view = new XmlEntityScene(scene, faces, read);
        var report = new Dictionary<string, object>();
        foreach (var step in SettlementEntityRules.Steps(view, layout, ground, g => model.IsLand(g), policy, lakes, "mnbl_world", GeneratedWaterSystem.Tag, SettlementEntityContract.SiegeCacheScript, null, report)) { }
        report["detail"] = SettlementEntityRules.Detail(report);
        report["dropped"] = view.Dropped;
        report.Remove("levelSamples");
        return report;
    }

    // A failure's kind: the text without the settlement and entity names.
    private static string Kind(string failure)
    {
        int colon = failure.IndexOf(": ", StringComparison.Ordinal);
        string text = colon < 0 ? failure : failure.Substring(colon + 2);
        return System.Text.RegularExpressions.Regex.Replace(text, "mnbl_[0-9a-f]{8}_[tcvh]_[0-9a-f]+", "<id>");
    }

    // Every village entity, other than its four icons, whose level set is one of the icons' (the pairs the same-level-visuals read collapses).
    private static List<string> VillageTwins(string scene, SettlementLayout layout)
    {
        var doc = new XmlDocument { XmlResolver = null }; doc.LoadXml(scene);
        var iconSets = new[] { "level_1,civilian", "level_2,civilian", "level_3,civilian", "level_1,level_2,level_3,looted" }.Select(Set).ToList();
        var twins = new List<string>();
        foreach (var s in layout.Settlements.Where(x => x.Site.Kind == SiteKind.Village))
        {
            var root = doc.SelectSingleNode("/scene/entities/game_entity[@name='" + s.Site.Id + "']") as XmlElement;
            if (root == null) continue;
            var icons = new HashSet<string>(new[] { "_icon", "_icon_2", "_icon_3", "_icon_looted" }.Select(x => s.Site.Id + x), StringComparer.Ordinal);
            foreach (XmlElement e in root.SelectNodes(".//game_entity"))
                if (!icons.Contains(e.GetAttribute("name")) && iconSets.Contains(Set(String.Join(",", e.SelectNodes("levels/level").Cast<XmlElement>().Select(l => l.GetAttribute("name")))))) twins.Add(e.GetAttribute("name"));
        }
        return twins;
    }
    private static string Set(string names) { return String.Join(",", names.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).OrderBy(x => x, StringComparer.Ordinal)); }

    // Every entity of the scene's settlements by name: its parent's name and its global frame.
    private static Dictionary<string, KeyValuePair<string, LookFrame>> Frames(string scene)
    {
        var doc = new XmlDocument { XmlResolver = null }; doc.LoadXml(scene.TrimStart('﻿'));
        var frames = new Dictionary<string, KeyValuePair<string, LookFrame>>(StringComparer.Ordinal);
        Action<XmlElement, LookFrame, string> walk = null;
        walk = (e, parent, parentName) => {
            var f = parent.Child(e); frames[e.GetAttribute("name")] = new KeyValuePair<string, LookFrame>(parentName, f);
            foreach (XmlElement c in e.SelectNodes("children/game_entity")) walk(c, f, e.GetAttribute("name"));
        };
        foreach (XmlElement root in doc.SelectNodes("/scene/entities/game_entity[tags/tag/@name='town' or tags/tag/@name='castle' or tags/tag/@name='village']")) walk(root, new LookFrame(), null);
        return frames;
    }

    // The settlement root entities' XML, normalised (whitespace between elements dropped), for comparing two bakes of one world.
    private static string SettlementRoots(string scene)
    {
        var doc = new XmlDocument { XmlResolver = null, PreserveWhitespace = false }; doc.LoadXml(scene.TrimStart('﻿'));
        return Sha(String.Concat(doc.SelectNodes("/scene/entities/game_entity[tags/tag/@name='town' or tags/tag/@name='castle' or tags/tag/@name='village']").Cast<XmlElement>().Select(e => e.OuterXml)));
    }

    private static string Sha(string text) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(new UTF8Encoding(false).GetBytes(text))).Replace("-", "").ToLowerInvariant(); }
}
