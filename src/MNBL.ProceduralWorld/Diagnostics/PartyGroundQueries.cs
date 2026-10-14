using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using System.Xml;
using MNBL.ProceduralWorld.CampaignBootstrap;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.ModuleManager;
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.Diagnostics
{
    // One application-thread observation. Never moves a party, advances time, creates a visual,
    // saves, or substitutes an API getter for the actual strategic entity frame.
    internal static class PartyGroundQueries
    {
        private const double Extent = 1040, GroundTolerance = .05, BindingTolerance = .001;
        private static readonly string[] Files = { "scene.xscene", "terrain.bin", "navmesh.bin", "flora.bin", "atmosphere.xml" };

        internal sealed class Fixture
        {
            internal string Folder, Interpolation;
            internal Dictionary<string, string> Hashes;
            internal double[] Heights;
            internal int Side;
        }

        internal static Fixture Prepare(string manifestPath, string heightfieldPath, string interpolation,
            Dictionary<string, object> report)
        {
            report["status"] = "preparing"; report["passed"] = false;
            report["scope"] = "One paused land-party strategic anchor on the generated 1040-unit campaign fixture; no movement, foot-contact, generated pathfinding or runtime replacement claim.";
            report["limitations"] = new[] { "Official settlement XY, navmesh XY/topology/regions, helpers and caches retained.",
                "Collision ground may include retained entities; a difference from terrain is reported, not silently excused.",
                "Reference reconstructed from hash-verified source using the retained fixture builder's quantization; not a new native terrain decoder.",
                "Interpolation is an explicit preregistered comparison choice, not proof of the native interpolation algorithm." };
            report["tolerances"] = new { groundUnits = GroundTolerance, bindingUnits = BindingTolerance };
            report["collisionPrediction"] = "Observation only: 0.03 * trunc(quantized vertex height / 0.03), then triangleA interpolation. Empirical calibration/42/43 rule; not an additional pass criterion.";
            Require(interpolation == "triangleA" || interpolation == "triangleB" || interpolation == "bilinear",
                "partyGroundInterpolation must explicitly be triangleA, triangleB or bilinear.");
            Require(Path.IsPathRooted(manifestPath ?? "") && Path.IsPathRooted(heightfieldPath ?? ""),
                "partyGroundManifest and partyGroundHeightfield must be absolute paths.");
            var manifest = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(manifestPath));
            Require(Text(manifest, "schema") == "mnbl.campaign-bootstrap-fixture" && Convert.ToInt32(manifest["version"]) == 1
                && Text(manifest, "slot") == DiagnosticMapSceneCreator.Slot, "Wrong campaign fixture manifest.");
            var extent = new List<double>();
            foreach (object value in (System.Collections.IEnumerable)manifest["extent"]) extent.Add(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            Require(extent.Count == 2 && extent[0] == Extent && extent[1] == Extent, "Fixture must be 1040 by 1040.");
            var source = (Dictionary<string, object>)manifest["source"];
            Require(Text(source, "kind") == "fwgc" && Convert.ToDouble(source["verticalScale"], CultureInfo.InvariantCulture) == .01, "Fixture source/scale mismatch.");
            string sourceHash = Hash(heightfieldPath);
            Require(String.Equals(sourceHash, Text(source, "sha256"), StringComparison.OrdinalIgnoreCase), "Heightfield does not match fixture manifest source hash.");
            string folder = Path.Combine(ModuleHelper.GetModuleFullPath(DiagnosticMapSceneCreator.ModuleId), "SceneObj", DiagnosticMapSceneCreator.Slot);
            var hashes = new Dictionary<string, string>(); var declared = (Dictionary<string, object>)manifest["files"];
            foreach (string file in Files) hashes[file] = Text(declared, file);
            var fixture = new Fixture { Folder = folder, Hashes = hashes, Interpolation = interpolation };
            VerifyFiles(fixture);
            var xml = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(Path.Combine(folder, "scene.xscene"), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) xml.Load(reader);
            Require(xml.DocumentElement.GetAttribute("name") == DiagnosticMapSceneCreator.Slot, "Fixture XML scene identity mismatch.");
            var terrain = (XmlElement)xml.SelectSingleNode("/scene/terrain");
            Require(terrain != null && terrain.GetAttribute("node_dimension_x") == "4" && terrain.GetAttribute("node_dimension_y") == "4"
                && Number(terrain.GetAttribute("node_size")) == 260, "Fixture terrain dimensions mismatch.");
            var field = DiagnosticHeightfield.Read(heightfieldPath);
            Require(field.Side == 129, "Expected retained 129x129 campaign fixture source.");
            double low = double.PositiveInfinity, high = double.NegativeInfinity;
            for (int y = 0; y < field.Side; y++) for (int x = 0; x < field.Side; x++)
            { double h = field.At(x, y) * .01; low = Math.Min(low, h); high = Math.Max(high, h); }
            low = Math.Floor(low * 1000) / 1000; high = Math.Ceiling(high * 1000) / 1000;
            if (high <= low) high = low + 1;
            Require(Number(terrain.GetAttribute("min_height")) == low && Number(terrain.GetAttribute("max_height")) == high, "Source quantization range differs from installed fixture XML.");
            fixture.Side = field.Side; fixture.Heights = new double[field.Side * field.Side];
            for (int y = 0; y < field.Side; y++) for (int x = 0; x < field.Side; x++)
            {
                double q = Math.Max(0, Math.Min(65535, Math.Round((field.At(x, y) * .01 - low) / (high - low) * 65535, MidpointRounding.ToEven)));
                fixture.Heights[y * field.Side + x] = low + q * ((high - low) / 65535);
            }
            report["fixture"] = new { folder, scene = DiagnosticMapSceneCreator.Slot, module = DiagnosticMapSceneCreator.ModuleId,
                extent = Extent, gridSide = field.Side, spacing = 8.125, minHeight = low, maxHeight = high,
                sourcePath = Path.GetFullPath(heightfieldPath), sourceSha256 = sourceHash,
                manifestPath = Path.GetFullPath(manifestPath), manifestSha256 = Hash(manifestPath), nativeFileHashes = hashes,
                interpolation, quantizationMaxError = (high - low) / 65535 / 2 };
            report["status"] = "prepared";
            return fixture;
        }

        internal static void Measure(DiagnosticMapScene wrapper, Fixture fixture, Dictionary<string, object> report, Action<string> stage)
        {
            var failures = new List<string>(); var gaps = new List<string>();
            report["failures"] = failures; report["gaps"] = gaps; report["status"] = "measuring";
            try
            {
                VerifyFiles(fixture);
                var campaign = Campaign.Current; var party = MobileParty.MainParty;
                Require(campaign != null && party != null && Object.ReferenceEquals(campaign.MapSceneWrapper, wrapper), "Campaign/main party/wrapper unavailable or changed.");
                var scene = wrapper.Scene;
                Require(scene.GetName() == DiagnosticMapSceneCreator.Slot && scene.IsLoadingFinished(), "Expected ready campaign fixture scene.");
                report["sceneName"] = scene.GetName(); report["sceneModule"] = scene.GetModulePath();
                bool eligible = Game.Current.GameStateManager.ActiveState is MapState && campaign.TimeControlMode == CampaignTimeControlMode.Stop
                    && party.Position.IsOnLand && !party.IsCurrentlyAtSea && party.CurrentSettlement == null
                    && PlayerEncounter.Current == null && party.Party.MapEvent == null && party.Army == null && party.AttachedTo == null;
                report["eligibility"] = new { eligible, partyId = party.StringId, timeControl = campaign.TimeControlMode.ToString(),
                    onLand = party.Position.IsOnLand, atSea = party.IsCurrentlyAtSea, inSettlement = party.CurrentSettlement != null,
                    encounter = PlayerEncounter.Current != null, mapEvent = party.Party.MapEvent != null, army = party.Army != null, attached = party.AttachedTo != null };
                if (!eligible) { gaps.Add("Main party is not an eligible paused unattached land party outside settlement/encounter."); return; }
                var position = party.Party.Position; var xy = position.ToVec2(); double hours = CampaignTime.Now.ToHours;
                Require(InBounds(xy.x, xy.y), "Party position outside fixture domain; no clamping or relocation.");
                report["campaignHoursBefore"] = hours; report["campaignXY"] = new[] { xy.x, xy.y };
                Vec2i nodes; float size; int layers, version;
                stage("party_ground_terrain_dimensions"); scene.GetTerrainData(out nodes, out size, out layers, out version);
                Require(nodes.X == 4 && nodes.Y == 4 && size == 260, "Native loaded terrain is not the manifest's 1040-unit fixture.");
                stage("party_ground_explicit_height"); float ground = float.NaN;
                bool hit = campaign.MapSceneWrapper.GetHeightAtPoint(position, ref ground);
                report["groundQuery"] = new { success = hit, height = N(ground), source = "IMapScene.GetHeightAtPoint(in CampaignVec2,ref float) through DiagnosticMapScene; land collision exclude flags 144" };
                stage("party_ground_as_vec3"); var logical = position.AsVec3(); report["campaignAsVec3"] = V(logical);
                stage("party_ground_terrain_height"); float terrainHeight = scene.GetTerrainHeight(xy, true);
                report["sourceAtCampaignXY"] = Predictions(fixture, xy.x, xy.y);
                report["terrainAtCampaignXY"] = N(terrainHeight);
                double expected = Expected(fixture, xy.x, xy.y);
                Check(failures, "explicit_ground_hit", hit);
                Check(failures, "terrain_vs_quantized_source", Close(terrainHeight, expected, GroundTolerance));
                Check(failures, "collision_ground_vs_quantized_source", hit && Close(ground, expected, GroundTolerance));
                Check(failures, "as_vec3_vs_explicit_ground", hit && Close(logical.z, ground, BindingTolerance));
                Check(failures, "as_vec3_xy", Close(logical.x, xy.x, BindingTolerance) && Close(logical.y, xy.y, BindingTolerance));
                // Select the land manager by installed public metadata; never infer visual type from NavalMapScreen.
                stage("party_ground_land_visual");
                Type managerType = null;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    // Bannerlord can load module assemblies outside the default name-binding context.
                    var candidate = assembly.GetType("SandBox.View.Map.Managers.MobilePartyVisualManager", false);
                    if (candidate == null) continue;
                    Require(managerType == null, "Multiple loaded land visual manager types; identity is ambiguous.");
                    managerType = candidate;
                }
                if (managerType == null) { gaps.Add("Installed land visual manager type unavailable."); return; }
                report["managerAssembly"] = new { identity = managerType.Assembly.FullName, path = managerType.Assembly.Location,
                    sha256 = Hash(managerType.Assembly.Location) };
                var current = managerType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
                var getter = managerType.GetMethod("GetVisualOfEntity", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(PartyBase) }, null);
                Require(current != null && getter != null, "Installed land visual manager public contract changed.");
                object manager = current.GetValue(null, null);
                object visual = manager == null ? null : getter.Invoke(manager, new object[] { party.Party });
                if (visual == null) { gaps.Add("Existing land party visual not available after readiness wait; no visual constructed."); return; }
                Type vt = visual.GetType(); report["visualType"] = vt.FullName;
                report["visualAssembly"] = new { path = vt.Assembly.Location, sha256 = Hash(vt.Assembly.Location) };
                Require(vt.FullName == "SandBox.View.Map.Visuals.MobilePartyVisual", "Unexpected concrete land visual; refusing to assume its contract.");
                var getPosition = vt.GetMethod("GetVisualPosition", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                var entityProperty = vt.GetProperty("StrategicEntity", BindingFlags.Public | BindingFlags.Instance);
                Require(getPosition != null && getPosition.ReturnType == typeof(Vec3) && entityProperty != null, "Installed visual position/entity contract changed.");
                var visualPosition = (Vec3)getPosition.Invoke(visual, null); report["visualGetterXYZ"] = V(visualPosition);
                var entity = entityProperty.GetValue(visual, null) as GameEntity;
                if (entity == null) { gaps.Add("StrategicEntity missing; getter XYZ does not establish actual rendered anchor."); return; }
                stage("party_ground_actual_entity_frame"); var actual = entity.GetGlobalFrame().origin;
                report["actualEntityGlobalXYZ"] = V(actual); report["actualEntityLocalXYZ"] = V(entity.GetLocalFrame().origin);
                var visualXY = party.VisualPosition2DWithoutError;
                report["visualXYWithoutError"] = new[] { visualXY.x, visualXY.y };
                Require(InBounds(actual.x, actual.y), "Actual entity outside fixture domain.");
                stage("party_ground_entity_terrain_height"); float entityTerrain = scene.GetTerrainHeight(actual.AsVec2, true);
                report["sourceAtEntityXY"] = Predictions(fixture, actual.x, actual.y); report["terrainAtEntityXY"] = N(entityTerrain);
                double entityExpected = Expected(fixture, actual.x, actual.y);
                report["residuals"] = new { terrainMinusSource = N(terrainHeight - expected), groundMinusSource = N(ground - expected),
                    asVec3MinusGround = N(logical.z - ground), entityMinusSource = N(actual.z - entityExpected),
                    entityMinusTerrain = N(actual.z - entityTerrain), entityMinusVisualGetter = N(actual.z - visualPosition.z),
                    groundMinusCollisionA = N(ground - Predictions(fixture, xy.x, xy.y)["collisionA"]),
                    entityMinusCollisionA = N(actual.z - Predictions(fixture, actual.x, actual.y)["collisionA"]) };
                Check(failures, "visual_getter_vs_ground", hit && Close(visualPosition.z, ground, BindingTolerance));
                Check(failures, "visual_getter_xy", Close(visualPosition.x, visualXY.x, BindingTolerance) && Close(visualPosition.y, visualXY.y, BindingTolerance));
                Check(failures, "actual_entity_vs_visual_getter", Close(actual.x, visualPosition.x, GroundTolerance) && Close(actual.y, visualPosition.y, GroundTolerance) && Close(actual.z, visualPosition.z, GroundTolerance));
                Check(failures, "actual_entity_vs_source_at_own_xy", Close(actual.z, entityExpected, GroundTolerance));
                Check(failures, "actual_entity_vs_native_terrain", Close(actual.z, entityTerrain, GroundTolerance));
                Check(failures, "entity_terrain_vs_source", Close(entityTerrain, entityExpected, GroundTolerance));
                var after = party.Party.Position.ToVec2();
                report["campaignHoursAfter"] = CampaignTime.Now.ToHours;
                Check(failures, "paused_unchanged", campaign.TimeControlMode == CampaignTimeControlMode.Stop && CampaignTime.Now.ToHours == hours && after.x == xy.x && after.y == xy.y);
                VerifyFiles(fixture);
            }
            catch (Exception error) { report["exception"] = error.ToString(); gaps.Add("Measurement incomplete: " + error.Message); }
            finally
            {
                bool passed = failures.Count == 0 && gaps.Count == 0;
                report["passed"] = passed; report["status"] = passed ? "passed" : gaps.Count != 0 ? "incomplete" : "failed";
            }
        }

        private static Dictionary<string, double> Predictions(Fixture f, double x, double y)
        {
            Require(InBounds(x, y), "Reference coordinate outside fixture.");
            double gx = x * (f.Side - 1) / Extent, gy = y * (f.Side - 1) / Extent;
            int ix = Math.Min((int)gx, f.Side - 2), iy = Math.Min((int)gy, f.Side - 2); double u = gx - ix, v = gy - iy;
            double a = f.Heights[iy * f.Side + ix], b = f.Heights[iy * f.Side + ix + 1], c = f.Heights[(iy + 1) * f.Side + ix], d = f.Heights[(iy + 1) * f.Side + ix + 1];
            double ca = .03 * Math.Truncate(a / .03), cb = .03 * Math.Truncate(b / .03),
                cc = .03 * Math.Truncate(c / .03), cd = .03 * Math.Truncate(d / .03);
            return new Dictionary<string, double> {
                { "bilinear", a*(1-u)*(1-v)+b*u*(1-v)+c*(1-u)*v+d*u*v },
                { "triangleA", u>=v ? a+(b-a)*u+(d-b)*v : a+(d-c)*u+(c-a)*v },
                { "triangleB", u+v<=1 ? a+(b-a)*u+(c-a)*v : d+(c-d)*(1-u)+(b-d)*(1-v) },
                { "collisionA", u>=v ? ca+(cb-ca)*u+(cd-cb)*v : ca+(cd-cc)*u+(cc-ca)*v } };
        }
        private static double Expected(Fixture f, double x, double y) { return Predictions(f, x, y)[f.Interpolation]; }
        private static void VerifyFiles(Fixture f) { foreach (string file in Files) Require(String.Equals(Hash(Path.Combine(f.Folder, file)), f.Hashes[file], StringComparison.OrdinalIgnoreCase), "Installed campaign fixture hash mismatch: " + file); }
        private static string Hash(string path) { using (var s = File.OpenRead(path)) using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant(); }
        private static string Text(Dictionary<string, object> d, string k) { return Convert.ToString(d[k], CultureInfo.InvariantCulture); }
        private static double Number(string s) { return Double.Parse(s, CultureInfo.InvariantCulture); }
        private static bool Finite(double n) { return !Double.IsNaN(n) && !Double.IsInfinity(n); }
        private static bool InBounds(double x, double y) { return Finite(x) && Finite(y) && x >= 0 && y >= 0 && x <= Extent && y <= Extent; }
        private static bool Close(double a, double b, double tolerance) { return Finite(a) && Finite(b) && Math.Abs(a-b) <= tolerance; }
        private static object N(double n) { return Finite(n) ? (object)n : n.ToString(CultureInfo.InvariantCulture); }
        private static object[] V(Vec3 v) { return new[] { N(v.x), N(v.y), N(v.z) }; }
        private static void Check(List<string> failures, string name, bool ok) { if (!ok) failures.Add(name); }
        private static void Require(bool ok, string message) { if (!ok) throw new InvalidDataException(message); }
    }
}
