using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.World;

namespace MNBL.ProceduralWorld.Surface
{
    // MMR-257: what the campaign map draws beside the terrain and the settlements (docs/map-dressing.md): road decals, bridges at the existing
    // decks and FWGC++ ruin markers. Pure managed: the plan is built from the world's bundle (travel-graph.json roads[] pieces, campaign.json
    // presentation cultures, cells.bin), its kept navigation report (build.bridges, because an installed-scene load skips Prepare) and the
    // source's ruins[], the same way at activation and at load (WorldReconstruction). Nothing here touches the scene, the navmesh or the
    // scene name: MapDressingInstaller draws a plan through an IMapDressingTarget (the engine adapter is MapDressingRuntime; the offline
    // harness uses a recording one) and removes everything it drew.
    //   * Roads: each piece's polyline (its start point, the centres of its cells, its end point; columns unwrapped so a piece crossing the
    //     seam stays continuous) is smoothed (Chaikin corner cutting, endpoints fixed, so the curve stays inside the piece's cells), clipped
    //     gateClip units from a gate, ended half a decal short of a bank (a crossing with no deck, RT-17) and sampled every spacing units.
    //     A decal is not placed where its footprint meets a water cell (decks carry the bridge prefab instead) or where another road's decal
    //     already stands (feeders end on routes), or where its footprint lies under the sea plane or a shown lake plane (PlaneCover, MMR-247:
    //     a land cell can dip below a plane). Past maxDecals the spacing stretches so the world keeps the cap. The family is the road's
    //     presentation culture's (roads.json cultureFamilies); each road keeps one of its family's materials.
    //   * Bridges: one run of bridge segments per straight leg of each deck (an L-shaped deck turns at its corner cell when lShaped is
    //     "rotate", and is skipped when "skip"), from bank edge to bank edge, segments of the installed map bridge pieces at the vanilla
    //     campaign map's measured segment length and scale; z is the higher bank's ground (set by the installer from the scene).
    //   * Ruins: one marker per FWGC++ ruin on land (land or mountain cells), snapped to the nearest land cell within snapCells cells or
    //     skipped (sea, lake or river; MMR-247: also where a shown water plane covers the point, the sea below the datum or a lake plane over
    //     a land cell, by LowGroundOf), skipped within minGap units of a live settlement and where another marker already stands (RT-22),
    //     named through RuinNames.Display: the compiler's cleaned name (campaign.json ruins[], MMR-286's shared name cleaner: no Age tag,
    //     unique among the ruins by a lexicon suffix); the source name stays SourceName.
    //   * Water (MMR-285, MMR-247): every travel cell parties cannot cross on land (Sea, Lake, River; decks too, under their bridge) renders
    //     as water. The sea plane shows where the ground lies below the datum and a lake plane where its plane clears the ground inside its
    //     box (GeneratedWaterSystem.Lakes, the scene's planes); rivers have no plane, so they are drawn as water decal strips. The river
    //     graph (River cells joined along edges between 8-neighbours, a diagonal only where no river cell shares an edge with both ends, and
    //     a river end joined to the sea or lake cell beside it) splits at every end and junction into chains of cell centres; each chain is
    //     smoothed (Chaikin, ends kept) and carries a strip stamp every riverSpacing units, turned along the flow and RiverStampLength steps
    //     long either way, so the strips chain. A river cell with no edge gets a round pool stamp, as does any river cell centre the strips
    //     miss (a patch). Every other water cell whose centre no plane and no river stamp paints gets a pool stamp the size of its cell
    //     (fillScale), so it paints nothing past its own cell. A stamp paints a point inside opaqueCore x its half extents whose ground lies
    //     inside its clipped box (WaterPlanes.Paints). Stamps are entity-less scene decals of the published water material
    //     (mnbl_water_surface, tools/art/water_decal.py), falling back to the tinted vanilla stamp while it is not published or (MMR-247 D) while
    //     its textures are not in the campaign map's decal atlas (World/DecalAtlas.cs: a module texture never is), each box fitted
    //     to the ground of its core (FitBox; road decals keep DecalHalfDepth). Every road and water decal carries the water plane its box
    //     stays above (Floor); the engine target clips each box over it (WaterPlanes), so no decal paints a water surface (the campaign
    //     map's water shader adds every decal whose box holds the surface, MMR-290).
    internal sealed class RuinRecord
    {
        public string Uid, Id, SourceName, Civilization, CivilizationName, Cause, Reason, CityClass;
        public string DisplayName;   // MMR-286: campaign.json ruins[] name (RuinNames.Apply)
        public int Node, Column, Row, FoundedAge, DestroyedAge;
        public string Name { get { return RuinNames.Display(this); } }
    }

    // The one place a ruin's display name is read. Cleaner points at the compiler's cleaned names (MMR-286: Apply sets each ruin's
    // DisplayName from campaign.json ruins[]); a test may redirect it.
    internal static class RuinNames
    {
        public static Func<RuinRecord, string> Cleaner { get; set; } = Cleaned;
        public static string Cleaned(RuinRecord ruin) { return ruin.DisplayName ?? ruin.SourceName; }
        public static string Display(RuinRecord ruin) { var cleaner = Cleaner; return cleaner == null ? ruin.SourceName : cleaner(ruin); }
        // Every ruin takes its cleaned name from the compiled campaign (ruins[]: uid, name); a ruin the campaign does not name is refused (a
        // campaign compiled before MMR-286 has no ruins block).
        public static void Apply(IEnumerable<RuinRecord> ruins, IDictionary<string, object> campaign)
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            if (campaign != null && campaign.ContainsKey("ruins"))
                foreach (var r in Json.A(campaign, "ruins").Cast<IDictionary<string, object>>()) names[Json.S(r, "uid")] = Json.S(r, "name");
            foreach (var ruin in ruins)
            {
                string name;
                if (!names.TryGetValue(ruin.Uid ?? "", out name) || String.IsNullOrEmpty(name)) throw new InvalidDataException("campaign.json names no ruin " + ruin.Uid + " (ruins[], MMR-286).");
                ruin.DisplayName = name;
            }
        }
        // "war_international" -> "International war"; any other cause id is spaced and capitalised.
        public static string Cause(string cause)
        {
            if (String.IsNullOrEmpty(cause)) return "Unknown";
            if (cause.StartsWith("war_", StringComparison.Ordinal)) return Capital(cause.Substring(4).Replace('_', ' ')) + " war";
            if (cause == "self_magic") return "Its own magic";
            return Capital(cause.Replace('_', ' '));
        }
        private static string Capital(string s) { return s.Length == 0 ? s : Char.ToUpperInvariant(s[0]) + s.Substring(1); }
    }

    internal sealed class RoadDecal
    {
        public int Road, Piece;
        public string Family, Material;
        public double X, Y, Yaw;            // canonical physical position (x in [strip, strip + width)) and heading (radians, from +x toward +y)
        public double HalfLength, HalfWidth, HalfDepth;
        public float[] Atlas;               // SetVectorArgument (scaleU, scaleV, offsetU, offsetV)
        public uint Tint;                   // SetFactor1 ARGB (water decals); 0: none
        // MMR-247: drawn instead when Material does not resolve (water: the tinted vanilla stamp until mnbl_water_surface is published), or
        // (MMR-247 D) when its textures have no rect in the campaign map's decal atlas (World/DecalAtlas.cs: a module's never have one).
        public string Fallback; public float[] FallbackAtlas; public uint FallbackTint;
        // MMR-247 (D): the fallback's box: turned a quarter (its stamp runs along the texture's v), along x HalfLength, across x HalfWidth.
        public bool FallbackTurn { get; set; }
        public double FallbackAlong = 1, FallbackAcross = 1;
        // The yaw and the half extents along the frame's u and v this decal draws with: its own, or its fallback's.
        public void Box(bool fallback, out double yaw, out double halfU, out double halfV)
        {
            if (!fallback) { yaw = Yaw; halfU = HalfLength; halfV = HalfWidth; return; }
            double along = HalfLength * FallbackAlong, across = HalfWidth * FallbackAcross;
            if (FallbackTurn) { yaw = Yaw + Math.PI / 2; halfU = across; halfV = along; }
            else { yaw = Yaw; halfU = along; halfV = across; }
        }
        public string Kind;                 // water decals: "river" (a strip along a chain), "pool" (a lone river cell or a patch) or "fill"
        public double BoxOffset;            // MMR-247: the box centre's height over the ground at the decal's centre (water stamps fit their core)
        public double Floor = Double.NaN;   // MMR-247: the water plane the box stays above (WaterPlanes.Floor; -infinity: none; NaN: the target samples it)
        // Whether a point (canonical physical; x wraps by width) lies inside share x this decal's half extents (an ellipse).
        public bool Covers(double x, double y, double width, double share)
        {
            double dx = x - X; dx -= Math.Round(dx / width) * width;
            double dy = y - Y, c = Math.Cos(Yaw), s = Math.Sin(Yaw);
            double u = (dx * c + dy * s) / (HalfLength * share), v = (-dx * s + dy * c) / (HalfWidth * share);
            return u * u + v * v <= 1;
        }
    }

    // A travel cell parties cannot cross on land (Sea, Lake, River) and what draws it as water: "sea" (the ground at its centre lies below
    // the datum), "lake" (a shown lake plane clears it), "river" (a river stamp covers its centre) or "fill" (its own fill stamp).
    internal sealed class WaterCell
    {
        public int Row, Column;
        public byte Class;
        public bool Deck;                   // a road deck (the bridge carries land parties)
        public double X, Y, Ground;         // canonical physical centre; the encoded ground there
        public string Cover;
    }

    // MMR-247: the water planes a decal box must stay above. The campaign map's water shader draws every decal whose box holds the water
    // surface, added to the water colour (MMR-290 section 1), so a decal centred on the ground near a coast or a lake also painted the water.
    // The sea plane lies on the datum over the whole map; a shown lake plane at its Z over its box. A box keeps its top and raises its bottom
    // to Clearance over the highest plane whose water its footprint shows (Floor: the sea where a footprint sample's ground lies under the
    // datum, a lake where a sample inside its box lies under its plane; planes are flat, so only their heights matter). Terrain under that
    // plane is under water anyway, and the water surface leaves the box; a box over dry ground only is left whole. The plan sets each road
    // and water decal's Floor once (7 x 7 samples); the engine target (MapDressingRuntime.EngineTarget) clips every decal it draws with it,
    // sampling 3 x 3 itself for a decal without one (MMR-260's magic ground). A target with its own frames (MMR-290's water stamps) calls
    // Floor and Clip itself.
    internal sealed class WaterPlanes
    {
        public const double Clearance = .01;
        public readonly IList<GeneratedWaterSystem.Lake> Lakes;
        public WaterPlanes(IList<GeneratedWaterSystem.Lake> lakes) { Lakes = lakes ?? new GeneratedWaterSystem.Lake[0]; }
        public static WaterPlanes Of(MapDressingPlan plan) { return new WaterPlanes(plan == null ? null : plan.Lakes); }

        // The highest water plane a footprint shows (n x n samples over its half extents, along yaw and across it, as DecalFrame turns them):
        // the datum or a lake's Z; -infinity when it shows none.
        public double Floor(double x, double y, double yaw, double halfLength, double halfWidth, Func<double, double, double> ground, int n)
        {
            double c = Math.Cos(yaw), s = Math.Sin(yaw), ex = halfLength * Math.Abs(c) + halfWidth * Math.Abs(s), ey = halfLength * Math.Abs(s) + halfWidth * Math.Abs(c);
            var near = Lakes.Where(l => x + ex >= l.MinX && x - ex <= l.MaxX && y + ey >= l.MinY && y - ey <= l.MaxY).ToList();
            double top = Double.NegativeInfinity;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    double u = (n == 1 ? 0 : 2.0 * i / (n - 1) - 1) * halfLength, v = (n == 1 ? 0 : 2.0 * j / (n - 1) - 1) * halfWidth, px = x + u * c - v * s, py = y + u * s + v * c;
                    double level = Level(near, px, py, ground(px, py));
                    if (level > top) top = level;
                }
            return top;
        }

        // A box centred at centre with halfDepth, its bottom raised to plane + Clearance: the new centre and half depth. False when nothing of it
        // is left above the plane (then a sliver box just over the plane, which paints nothing).
        public static bool Clip(double centre, double halfDepth, double plane, out double z, out double half)
        {
            double lo = Math.Max(centre - halfDepth, plane + Clearance), hi = centre + halfDepth;
            if (hi - lo < 2e-3) { z = plane + Clearance + 1e-3; half = 1e-3; return false; }
            z = (lo + hi) / 2; half = (hi - lo) / 2;
            return true;
        }

        // A decal drawn at (x, y): its box, centred BoxOffset over the ground at its centre with its half depth (at least minHalfDepth: MMR-247 D,
        // roads.json water.minHalfDepth for water stamps), clipped over its Floor.
        public bool Box(RoadDecal d, double x, double y, Func<double, double, double> ground, out double z, out double half, double minHalfDepth = 0)
        {
            double plane = Double.IsNaN(d.Floor) ? Floor(x, y, d.Yaw, d.HalfLength, d.HalfWidth, ground, 3) : d.Floor;
            return Clip(ground(x, y) + d.BoxOffset, Math.Max(d.HalfDepth, minHalfDepth), plane, out z, out half);
        }

        // The water surface a point shows over its ground: the datum, or the highest lake whose box holds it, when above the ground; else NaN.
        public double Level(double x, double y, double ground) { return Level(Lakes, x, y, ground); }
        private static double Level(IEnumerable<GeneratedWaterSystem.Lake> lakes, double x, double y, double ground)
        {
            double level = ground < GeneratedWaterSystem.DatumZ ? GeneratedWaterSystem.DatumZ : Double.NaN;
            foreach (var l in lakes) if (l.Z > ground && x >= l.MinX && x <= l.MaxX && y >= l.MinY && y <= l.MaxY && !(l.Z <= level)) level = l.Z;
            return level;
        }

        // Whether a decal at its own position paints the ground at (x, y) (canonical; x wraps by width): inside its opaque core and within
        // DepthReach of its clipped box (the shader fades the box's ends by 1 - |z|^8).
        public bool Paints(RoadDecal d, double x, double y, double width, double core, Func<double, double, double> ground)
        {
            double z, half;
            return d.Covers(x, y, width, core) && Box(d, d.X, d.Y, ground, out z, out half) && Math.Abs(ground(x, y) - z) <= MapDressing.DepthReach * half;
        }
    }

    internal sealed class BridgeSegment
    {
        public int Bridge, Leg;
        public string Prefab, Kind;
        public double X, Y, Yaw, Scale;     // canonical physical; yaw turns the prefab's local +y (the vanilla deck axis) onto the leg
        public double[] BankA, BankB;       // canonical physical bank edge points; z is the higher bank's ground
    }

    internal sealed class RuinMarker
    {
        public RuinRecord Ruin;
        public string Prefab;
        public double X, Y, Yaw, Scale;     // canonical physical
        public string Placement;            // "direct" or "snapped"
    }

    internal sealed class MapDressingPlan
    {
        public const string Version = "mmr257/1";
        public string WorldContentId;
        public double Width, Strip, PoleMargin;
        public readonly List<RoadDecal> Decals = new List<RoadDecal>();
        public readonly List<BridgeSegment> Bridges = new List<BridgeSegment>();
        public readonly List<RuinMarker> Markers = new List<RuinMarker>();
        public readonly List<RuinRecord> Ruins = new List<RuinRecord>();
        public readonly SortedDictionary<string, int> SkippedRuins = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> DecalsByFamily = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public int Pieces, BankEnds, GateEnds, DecksSkippedLShaped, Decks, DecalsDroppedWater, DecalsDroppedOverlap, DecalsDroppedGate;
        public int DecalsDroppedPlane;      // MMR-247: of DecalsDroppedWater, those on land cells under the sea plane or a shown lake plane
        public double SpacingUsed, SampledLength;
        public double DrawnLength;          // MMR-247: the road length the decals at the policy spacing cover (after the water, gate and overlap drops)
        // MMR-285: water decals (river stamps first, then fill stamps), every water cell with its cover, and the lake planes the scene carries.
        public readonly List<RoadDecal> Water = new List<RoadDecal>();
        public readonly List<WaterCell> WaterCells = new List<WaterCell>();
        public readonly List<GeneratedWaterSystem.Lake> Lakes = new List<GeneratedWaterSystem.Lake>();
        public int LakesHidden, LakesUnmatched, RiverStamps, FillStamps, RiverEdges, MouthEdges;
        public int RiverChains, RiverPools, RiverPatches;   // MMR-247: chains stamped, lone river cells and missed centres given a pool
        public double WaterSpacingUsed, WaterCore;
        // MMR-291: the smoothed river chains the strips were stamped along (physical: x continuous along a chain from a canonical first point, y
        // from the pole margin), and the ground the plan was made over (MapDressing.GroundOf; for the river meshes). Neither is in the digest.
        public readonly List<List<double[]>> RiverLines = new List<List<double[]>>();
        public Func<double, double, double> Ground;
        public double Extent;

        // The canonical physical x of a point on the logical globe.
        public double Canonical(double logicalX) { double x = logicalX % Width; if (x < 0) x += Width; return x + Strip; }

        public Dictionary<string, object> Record()
        {
            return new Dictionary<string, object> {
                ["version"] = Version, ["worldContentId"] = WorldContentId,
                ["roads"] = new Dictionary<string, object> { ["pieces"] = Pieces, ["gateEnds"] = GateEnds, ["bankEnds"] = BankEnds, ["decals"] = Decals.Count, ["byFamily"] = DecalsByFamily,
                    ["spacingUsed"] = SpacingUsed, ["sampledLength"] = Math.Round(SampledLength, 3), ["drawnLength"] = Math.Round(DrawnLength, 3), ["droppedOnWater"] = DecalsDroppedWater, ["droppedUnderPlanes"] = DecalsDroppedPlane,
                    ["droppedOverlap"] = DecalsDroppedOverlap, ["droppedAtGates"] = DecalsDroppedGate },
                ["bridges"] = new Dictionary<string, object> { ["decks"] = Decks, ["segments"] = Bridges.Count, ["lShapedSkipped"] = DecksSkippedLShaped },
                ["ruins"] = new Dictionary<string, object> { ["total"] = Ruins.Count, ["markers"] = Markers.Count, ["snapped"] = Markers.Count(m => m.Placement == "snapped"), ["skipped"] = SkippedRuins },
                ["water"] = WaterRecord() };
        }
        public Dictionary<string, object> WaterRecord()
        {
            var covers = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var c in WaterCells) { string key = TravelCellName(c.Class) + (c.Deck ? "Deck" : "") + ":" + c.Cover; int n; covers.TryGetValue(key, out n); covers[key] = n + 1; }
            return new Dictionary<string, object> { ["decals"] = Water.Count, ["riverStamps"] = RiverStamps, ["fillStamps"] = FillStamps, ["riverEdges"] = RiverEdges, ["mouthEdges"] = MouthEdges,
                ["riverChains"] = RiverChains, ["riverPools"] = RiverPools, ["riverPatches"] = RiverPatches, ["material"] = Water.Count == 0 ? null : Water[0].Material,
                ["fallback"] = Water.Count == 0 ? null : Water[0].Fallback,
                ["spacingUsed"] = WaterSpacingUsed, ["cells"] = WaterCells.Count, ["covers"] = covers, ["lakePlanes"] = Lakes.Count, ["lakesHidden"] = LakesHidden, ["lakesUnmatched"] = LakesUnmatched };
        }
        private static string TravelCellName(byte k) { return k < TravelCells.ClassNames.Length ? TravelCells.ClassNames[k] : k.ToString(CultureInfo.InvariantCulture); }
        // A digest of everything drawn (determinism checks).
        public string Sha256()
        {
            var b = new StringBuilder();
            Func<double, string> f = v => v.ToString("R", CultureInfo.InvariantCulture);
            foreach (var d in Decals) b.Append("d ").Append(d.Material).Append(' ').Append(f(d.X)).Append(' ').Append(f(d.Y)).Append(' ').Append(f(d.Yaw)).Append('\n');
            foreach (var s in Bridges) b.Append("b ").Append(s.Prefab).Append(' ').Append(f(s.X)).Append(' ').Append(f(s.Y)).Append(' ').Append(f(s.Yaw)).Append('\n');
            foreach (var m in Markers) b.Append("r ").Append(m.Ruin.Uid).Append(' ').Append(m.Prefab).Append(' ').Append(f(m.X)).Append(' ').Append(f(m.Y)).Append('\n');
            foreach (var d in Water) b.Append("w ").Append(d.Material).Append(' ').Append(f(d.X)).Append(' ').Append(f(d.Y)).Append(' ').Append(f(d.Yaw)).Append(' ').Append(f(d.HalfLength)).Append(' ').Append(f(d.HalfWidth)).Append('\n');
            return Json.Sha(b.ToString());
        }
    }

    internal static class MapDressing
    {
        // The path decals' atlas arguments (SetVectorArgument), as MMR-249's probe drew them on the campaign map (MapEpic2Probe.PathArguments).
        public static readonly Dictionary<string, float[]> PathAtlas = new Dictionary<string, float[]>(StringComparer.Ordinal) {
            ["decal_empire_path_a"] = new[] { .53f, .53f, .48f, .01f }, ["decal_empire_path_b"] = new[] { .53f, .53f, 0f, .01f },
            ["decal_khuzait_path_a"] = new[] { .53f, .53f, .5f, .01f }, ["decal_khuzait_path_b"] = new[] { .53f, .53f, 0f, .01f },
            ["decal_battania_path_a"] = new[] { .53f, .53f, .48f, .01f }, ["decal_battania_path_b"] = new[] { .53f, .53f, -.01f, .01f } };
        public const double DecalHalfDepth = 3;           // the probe's projection depth (follows relief to about 40 degrees); road decals keep it
        public const double DepthReach = .85;             // MMR-262: the share of the half depth a footprint sample may use (the shader fades by 1 - |z|^8)
        public const double DecalOverlap = 1.1;           // a decal is 10 % longer than the spacing, so neighbours meet on curves
        // The vanilla campaign map's stone bridges (SandBox Main_map "stone_bridge"): segments 0.42 local units apart along local +y, the
        // bridge scaled 2.885 (roads.json bridges.scale); the end piece (map_bridge_e) faces outward at both ends.
        public const double BridgeSegmentLocal = 0.42;
        public const double MarkerHoverRadius = 1.5;      // map units around a marker that show its name
        public const double RiverStampLength = 1.5;       // a river stamp's half length in steps (MMR-285)

        public static MapDressingPlan Build(WorldDataBundle bundle, WorldSurface surface, IDictionary<string, object> navigationReport, GeneratedClimateSystem.Source source,
            RoadsPolicy roads, SettlementLooksPolicy looks)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            Func<string, IDictionary<string, object>> doc = path => (IDictionary<string, object>)json.DeserializeObject(new UTF8Encoding(false).GetString(bundle.ReadVerified(path)).TrimStart('﻿'));
            var graph = doc("compiled/travel/travel-graph.json");
            var grid = Json.O(graph, "grid");
            var cells = TravelCells.Read(bundle.ReadVerified("compiled/travel/cells.bin"), Json.I(grid, "cols"), Json.I(grid, "rows"), Json.I(grid, "columnOffset"), Json.I(grid, "rowOffset"), Json.D(grid, "spacing"));
            var bridges = navigationReport == null ? new object[0] : Json.A(Json.O(navigationReport, "build"), "bridges");
            // MMR-285: the scene's lake planes, by the rule RuntimeWorldBackend inserts them with (none without a source world).
            int unmatched = 0, hidden = 0;
            var lakes = source == null ? new List<GeneratedWaterSystem.Lake>() : GeneratedWaterSystem.Lakes(surface, source, PeriodicWorldCoordinates.FromSurface(surface), out unmatched, out hidden);
            return Build(bundle.WorldContentId, surface, graph, doc("compiled/campaign/campaign.json"), cells, bridges, Ruins(source), roads, looks, lakes, hidden, unmatched);
        }

        // The source's ruins, in source order.
        public static List<RuinRecord> Ruins(GeneratedClimateSystem.Source source)
        {
            var list = new List<RuinRecord>();
            if (source == null) return list;
            foreach (var r in source.Ruins)
            {
                string civ = Json.S(r, "civilization_id"), name;
                list.Add(new RuinRecord { Uid = Json.S(r, "uid"), Id = Json.S(r, "id"), SourceName = Json.S(r, "name"), Civilization = civ,
                    CivilizationName = civ != null && source.CivilizationNames.TryGetValue(civ, out name) ? name : civ, Cause = Json.S(r, "cause"), Reason = Json.S(r, "reason"),
                    CityClass = Json.S(r, "city_class"), Node = Json.I(r, "node"), Column = Json.I(r, "x"), Row = Json.I(r, "z"),
                    FoundedAge = r.ContainsKey("founded_age") && r["founded_age"] != null ? Json.I(r, "founded_age") : -1, DestroyedAge = r.ContainsKey("destroyed_age") && r["destroyed_age"] != null ? Json.I(r, "destroyed_age") : -1 });
                if (String.IsNullOrEmpty(list.Last().Uid) || String.IsNullOrEmpty(list.Last().SourceName)) throw new InvalidDataException("world.json: a ruin lacks its uid or name.");
            }
            return list;
        }

        public static MapDressingPlan Build(string worldContentId, WorldSurface surface, IDictionary<string, object> graph, IDictionary<string, object> campaign, TravelCells cells,
            object[] bridges, List<RuinRecord> ruins, RoadsPolicy roads, SettlementLooksPolicy looks, IList<GeneratedWaterSystem.Lake> lakes, int lakesHidden, int lakesUnmatched)
        {
            var plan = new MapDressingPlan { WorldContentId = worldContentId, Width = surface.LogicalWidth, Strip = surface.Strip, PoleMargin = surface.PoleMargin, Ground = GroundOf(surface), Extent = surface.Extent };
            RuinNames.Apply(ruins, campaign);
            plan.Lakes.AddRange(lakes); plan.LakesHidden = lakesHidden; plan.LakesUnmatched = lakesUnmatched;   // before the roads (MMR-247)
            Roads(plan, graph, campaign, cells, roads, surface);
            Bridges(plan, bridges, cells.Spacing, roads);
            Markers(plan, graph, cells, ruins, surface, looks);
            Water(plan, surface, cells, roads);
            return plan;
        }

        // ---- roads ----
        private sealed class Piece { public int Road, Index; public string Family; public string Material; public List<double[]> Points; public double ClipStart, ClipEnd, Length; }

        private static void Roads(MapDressingPlan plan, IDictionary<string, object> graph, IDictionary<string, object> campaign, TravelCells cells, RoadsPolicy policy, WorldSurface surface)
        {
            double sp = cells.Spacing, width = plan.Width;
            var ground = GroundOf(surface);
            var planes = new WaterPlanes(plan.Lakes);
            // Presentation culture per settlement: towns carry it; a castle takes its garrison town's, a village its bound settlement's.
            var culture = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (IDictionary<string, object> t in Json.A(campaign, "towns")) culture[Json.S(t, "id")] = Json.S(t, "presentation");
            foreach (IDictionary<string, object> k in Json.A(campaign, "castles")) { string c; if (culture.TryGetValue(Json.S(k, "garrisonTown") ?? "", out c)) culture[Json.S(k, "id")] = c; }
            foreach (IDictionary<string, object> v in Json.A(campaign, "villages")) { string c; if (culture.TryGetValue(Json.S(v, "bound") ?? "", out c)) culture[Json.S(v, "id")] = c; }
            string fallback = policy.DecalFamilies.Keys.OrderBy(k => k, StringComparer.Ordinal).First();
            var pieces = new List<Piece>();
            var gates = new List<double[]>();
            foreach (IDictionary<string, object> road in Json.A(graph, "roads"))
            {
                int index = Json.I(road, "index");
                string owner = Json.S(road, "kind") == "feeder" ? Json.S(road, "settlement") : Json.S(road, "from"), presentation, family;
                if (owner == null || !culture.TryGetValue(owner, out presentation) || presentation == null || !policy.CultureFamilies.TryGetValue(presentation, out family)) family = fallback;
                var materials = policy.DecalFamilies[family];
                string material = materials[index % materials.Count];
                int p = 0;
                foreach (IDictionary<string, object> piece in Json.A(road, "pieces"))
                {
                    var start = Json.O(piece, "start"); var end = Json.O(piece, "end");
                    var points = new List<double[]>();
                    points.Add(Json.Point(start, "point"));
                    foreach (var c in Json.A(piece, "path")) { var rc = Json.Cell(c); points.Add(new[] { (rc[1] + .5) * sp, (rc[0] + .5) * sp }); }
                    points.Add(Json.Point(end, "point"));
                    // Unwrap the columns: each point is taken at its image nearest the previous one.
                    for (int i = 1; i < points.Count; i++)
                    {
                        double dx = points[i][0] - points[i - 1][0];
                        points[i][0] -= Math.Round(dx / width) * width;
                    }
                    for (int pass = 0; pass < policy.SmoothingPasses; pass++) points = Chaikin(points);
                    var item = new Piece { Road = index, Index = p, Family = family, Material = material, Points = points, Length = Length(points) };
                    foreach (var e in new[] { new { End = start, First = true }, new { End = end, First = false } })
                    {
                        string kind = Json.S(e.End, "kind");
                        double clip = kind == "gate" ? policy.GateClip : 0;
                        if (kind == "gate") { plan.GateEnds++; gates.Add(Json.Point(e.End, "point")); } else if (kind == "bank") plan.BankEnds++;
                        if (e.First) item.ClipStart = clip; else item.ClipEnd = clip;
                    }
                    pieces.Add(item); plan.Pieces++; p++;
                }
            }
            // Spacing: the policy's, stretched when the world would pass maxDecals, by the drawn length (MMR-247, R's note): the road the decals at
            // the policy's spacing cover after the water, gate and overlap drops, so a spur's run along the road it joins (dropped as an overlap)
            // does not thin every other road. Stretched again while the count passes maxDecals; never beyond the sampled length's spacing, at
            // which the count cannot pass it.
            double usable = pieces.Sum(x => Math.Max(0, x.Length - x.ClipStart - x.ClipEnd));
            plan.SampledLength = usable;
            double spacing = policy.DecalSpacing, ceiling = Math.Max(policy.DecalSpacing, usable / policy.MaxDecals);
            Place(plan, pieces, gates, cells, policy, ground, planes, spacing);
            plan.DrawnLength = plan.Decals.Count * spacing;
            for (int pass = 0; plan.Decals.Count > policy.MaxDecals && pass < 8; pass++)
            {
                spacing = Math.Min(ceiling, pass == 0 ? plan.DrawnLength / policy.MaxDecals : spacing * plan.Decals.Count / policy.MaxDecals * 1.001);
                if (pass == 7) spacing = ceiling;
                Place(plan, pieces, gates, cells, policy, ground, planes, spacing);
            }
        }

        // The road decals at a spacing, each spacing apart along every piece between its clips, those over water, at a gate or near one already
        // placed dropped (the plan's decals and drop counts are replaced).
        private static void Place(MapDressingPlan plan, List<Piece> pieces, List<double[]> gates, TravelCells cells, RoadsPolicy policy, Func<double, double, double> ground, WaterPlanes planes, double spacing)
        {
            double width = plan.Width;
            plan.Decals.Clear(); plan.DecalsByFamily.Clear();
            plan.DecalsDroppedWater = plan.DecalsDroppedPlane = plan.DecalsDroppedGate = plan.DecalsDroppedOverlap = 0;
            plan.SpacingUsed = spacing;
            double halfLength = spacing * DecalOverlap / 2, halfWidth = policy.DecalSize / 2;
            var placed = new Dictionary<long, List<double[]>>();
            double bucket = spacing;
            foreach (var piece in pieces)
            {
                double from = piece.ClipStart, to = piece.Length - piece.ClipEnd;
                // A bank end (or start) ends half a decal short of the bank point, so no decal overhangs the water.
                for (double s = from + spacing / 2; s + spacing / 2 <= to + 1e-9; s += spacing)
                {
                    double[] at, dir;
                    Locate(piece.Points, s, out at, out dir);
                    double yaw = Math.Atan2(dir[1], dir[0]);
                    // Its footprint's ends and centre on land (a deck cell is water: the bridge covers it), and over every water plane
                    // (MMR-247: a land cell's ground can dip under the sea plane or a shown lake plane).
                    bool wet = false, under = false;
                    foreach (double t in new[] { -halfLength, 0, halfLength })
                    {
                        double x = at[0] + dir[0] * t, y = at[1] + dir[1] * t;
                        int r = (int)Math.Floor(y / cells.Spacing), c = (int)Math.Floor(x / cells.Spacing);
                        if (!cells.InRows(r) || TravelCells.Water(cells.At(r, c))) { wet = true; break; }
                        double px = plan.Canonical(x), py = y + plan.PoleMargin;
                        if (PlaneCover(plan.Lakes, px, py, ground(px, py)) != null) { wet = under = true; break; }
                    }
                    if (wet) { plan.DecalsDroppedWater++; if (under) plan.DecalsDroppedPlane++; continue; }
                    double cx = at[0] % width; if (cx < 0) cx += width;
                    // Clipped at every gate, also where another piece passes a settlement it does not end at.
                    if (gates.Any(g => Periodic(g, cx, at[1], width) < policy.GateClip)) { plan.DecalsDroppedGate++; continue; }
                    long key = Key((int)Math.Floor(cx / bucket), (int)Math.Floor(at[1] / bucket));
                    bool near = false;
                    int bx = (int)Math.Floor(cx / bucket), by = (int)Math.Floor(at[1] / bucket), columns = (int)Math.Ceiling(width / bucket);
                    for (int dx = -1; dx <= 1 && !near; dx++) for (int dy = -1; dy <= 1 && !near; dy++)
                    {
                        List<double[]> list; int nx = ((bx + dx) % columns + columns) % columns;
                        if (!placed.TryGetValue(Key(nx, by + dy), out list)) continue;
                        foreach (var q in list) { double ddx = Math.Abs(q[0] - cx); ddx = Math.Min(ddx, width - ddx); if (ddx * ddx + (q[1] - at[1]) * (q[1] - at[1]) < .25 * spacing * spacing) { near = true; break; } }
                    }
                    if (near) { plan.DecalsDroppedOverlap++; continue; }
                    List<double[]> bucketList; if (!placed.TryGetValue(key, out bucketList)) placed[key] = bucketList = new List<double[]>();
                    bucketList.Add(new[] { cx, at[1] });
                    var decal = new RoadDecal { Road = piece.Road, Piece = piece.Index, Family = piece.Family, Material = piece.Material, X = plan.Canonical(at[0]), Y = at[1] + plan.PoleMargin, Yaw = yaw,
                        HalfLength = halfLength, HalfWidth = halfWidth, HalfDepth = DecalHalfDepth, Atlas = PathAtlas.ContainsKey(piece.Material) ? PathAtlas[piece.Material] : new[] { 1f, 1f, 0f, 0f } };
                    decal.Floor = planes.Floor(decal.X, decal.Y, yaw, halfLength, halfWidth, ground, 7);   // MMR-247
                    plan.Decals.Add(decal);
                    int n; plan.DecalsByFamily.TryGetValue(piece.Family, out n); plan.DecalsByFamily[piece.Family] = n + 1;
                }
            }
        }

        private static long Key(int x, int y) { return ((long)x << 32) | (uint)y; }

        // Chaikin corner cutting with both endpoints kept.
        public static List<double[]> Chaikin(List<double[]> points)
        {
            if (points.Count < 3) return points.Select(p => (double[])p.Clone()).ToList();
            var output = new List<double[]> { (double[])points[0].Clone() };
            for (int i = 0; i + 1 < points.Count; i++)
            {
                var a = points[i]; var b = points[i + 1];
                if (i > 0) output.Add(new[] { .75 * a[0] + .25 * b[0], .75 * a[1] + .25 * b[1] });
                if (i + 2 < points.Count) output.Add(new[] { .25 * a[0] + .75 * b[0], .25 * a[1] + .75 * b[1] });
            }
            output.Add((double[])points[points.Count - 1].Clone());
            return output;
        }

        public static double Length(List<double[]> points)
        {
            double total = 0;
            for (int i = 0; i + 1 < points.Count; i++) total += Distance(points[i], points[i + 1]);
            return total;
        }
        private static double Distance(double[] a, double[] b) { return Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1])); }

        // The point at arc length s and the unit direction of its segment.
        private static void Locate(List<double[]> points, double s, out double[] at, out double[] dir)
        {
            for (int i = 0; i + 1 < points.Count; i++)
            {
                double d = Distance(points[i], points[i + 1]);
                if (d <= 1e-12) continue;
                if (s <= d || i + 2 == points.Count)
                {
                    double t = Math.Min(1, Math.Max(0, s / d));
                    var a = points[i]; var b = points[i + 1];
                    at = new[] { a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t };
                    dir = new[] { (b[0] - a[0]) / d, (b[1] - a[1]) / d };
                    return;
                }
                s -= d;
            }
            at = (double[])points[points.Count - 1].Clone(); dir = new double[] { 1, 0 };
        }

        // ---- bridges ----
        private static void Bridges(MapDressingPlan plan, object[] bridges, double sp, RoadsPolicy policy)
        {
            int index = 0;
            double segment = BridgeSegmentLocal * policy.BridgeScale;
            foreach (IDictionary<string, object> bridge in bridges)
            {
                int b = index++;
                plan.Decks++;
                var deck = Json.A(bridge, "cells").Select(Json.Cell).ToList();
                var bankA = Json.Cell(bridge["bankA"]); var bankB = Json.Cell(bridge["bankB"]);
                string kind = Json.S(bridge, "kind") ?? "river";
                // Logical cell centres from bank A to bank B, unwrapped.
                var chain = new List<int[]> { bankA }; chain.AddRange(deck); chain.Add(bankB);
                var centres = chain.Select(rc => new[] { (rc[1] + .5) * sp, (rc[0] + .5) * sp }).ToList();
                for (int i = 1; i < centres.Count; i++) centres[i][0] -= Math.Round((centres[i][0] - centres[i - 1][0]) / plan.Width) * plan.Width;
                // The straight legs: the chain splits where its step direction changes.
                var corners = new List<int> { 0 };
                for (int i = 1; i + 1 < centres.Count; i++)
                {
                    double ax = centres[i][0] - centres[i - 1][0], ay = centres[i][1] - centres[i - 1][1], bx = centres[i + 1][0] - centres[i][0], by = centres[i + 1][1] - centres[i][1];
                    if (Math.Abs(ax * by - ay * bx) > 1e-9) corners.Add(i);
                }
                corners.Add(centres.Count - 1);
                if (corners.Count > 2 && policy.LShaped == "skip") { plan.DecksSkippedLShaped++; continue; }
                // Bank edges: halfway between a bank cell and its deck cell.
                var edgeA = Mid(centres[0], centres[1]); var edgeB = Mid(centres[centres.Count - 2], centres[centres.Count - 1]);
                double[] physA = { plan.Canonical(edgeA[0]), edgeA[1] + plan.PoleMargin }, physB = { plan.Canonical(edgeB[0]), edgeB[1] + plan.PoleMargin };
                for (int leg = 0; leg + 1 < corners.Count; leg++)
                {
                    var p0 = leg == 0 ? edgeA : centres[corners[leg]];
                    var p1 = leg + 2 == corners.Count ? edgeB : centres[corners[leg + 1]];
                    double length = Distance(p0, p1);
                    if (length <= 1e-9) continue;
                    double[] dir = { (p1[0] - p0[0]) / length, (p1[1] - p0[1]) / length };
                    int count = Math.Max(1, (int)Math.Ceiling(length / segment - 1e-9));
                    double step = length / count, yaw = Math.Atan2(dir[1], dir[0]) - Math.PI / 2;
                    for (int k = 0; k < count; k++)
                    {
                        double s = (k + .5) * step;
                        string prefab;
                        bool first = leg == 0 && k == 0, last = leg + 2 == corners.Count && k == count - 1;
                        if (kind == "river" || policy.BridgePrefabs.Count < 4) prefab = policy.BridgePrefabs[0];
                        else prefab = first || last ? policy.BridgePrefabs[3] : (leg == 0 && k == 1) || (leg + 2 == corners.Count && k == count - 2) ? policy.BridgePrefabs[2] : policy.BridgePrefabs[1];
                        plan.Bridges.Add(new BridgeSegment { Bridge = b, Leg = leg, Prefab = prefab, Kind = kind, X = plan.Canonical(p0[0] + dir[0] * s), Y = p0[1] + dir[1] * s + plan.PoleMargin,
                            Yaw = leg == 0 && k < count / 2 ? yaw + Math.PI : yaw, Scale = policy.BridgeScale, BankA = physA, BankB = physB });
                    }
                }
            }
        }
        private static double[] Mid(double[] a, double[] b) { return new[] { (a[0] + b[0]) / 2, (a[1] + b[1]) / 2 }; }

        // ---- ruins ----
        private static void Markers(MapDressingPlan plan, IDictionary<string, object> graph, TravelCells cells, List<RuinRecord> ruins, WorldSurface surface, SettlementLooksPolicy looks)
        {
            plan.Ruins.AddRange(ruins);
            var live = new List<double[]>();
            foreach (IDictionary<string, object> s in Json.A(graph, "settlements")) if (s.ContainsKey("point")) live.Add(Json.Point(s, "point"));
            double width = plan.Width, sp = cells.Spacing;
            int n1 = surface.SourceGridSide - 1;
            var markers = new List<double[]>();
            // MMR-247 (acc5, B' lake 94): a logical point a shown water plane covers (a land cell's sample can lie in a small lake) is water too.
            var low = LowGroundOf(surface);
            Func<double, double, bool> wet = (lx, ly) => { double px = plan.Canonical(lx), py = ly + plan.PoleMargin; return PlaneCover(plan.Lakes, px, py, low(px, py)) != null; };
            foreach (var ruin in ruins)
            {
                if (ruin.Row <= 0 || ruin.Row >= n1 || ruin.Column < 0 || ruin.Column > n1) { Skip(plan, "pole"); continue; }
                // The projection's source sample (projection.json forward.sourceSample).
                double u = (double)(ruin.Column % n1) / n1 - surface.CentralMeridianDeg / 360.0;
                double x = width * (u - Math.Floor(u)), y = surface.LogicalHeight * ruin.Row / n1;
                int r = (int)Math.Floor(y / sp), c = cells.Wrap((int)Math.Floor(x / sp));
                string placement = "direct";
                if (!cells.InRows(r) || TravelCells.Water(cells.At(r, c)) || wet(x, y))
                {
                    double best = Double.MaxValue; int[] cell = null;
                    for (int dr = -looks.RuinSnapCells; dr <= looks.RuinSnapCells; dr++) for (int dc = -looks.RuinSnapCells; dc <= looks.RuinSnapCells; dc++)
                    {
                        if (!cells.InRows(r + dr) || TravelCells.Water(cells.At(r + dr, c + dc))) continue;
                        double cx = (c + dc + .5) * sp, cy = (r + dr + .5) * sp, d = (cx - x) * (cx - x) + (cy - y) * (cy - y);
                        if (wet(cx, cy)) continue;
                        if (d < best - 1e-12) { best = d; cell = new[] { r + dr, c + dc }; }
                    }
                    if (cell == null) { Skip(plan, "water"); continue; }
                    x = (cell[1] + .5) * sp; y = (cell[0] + .5) * sp; placement = "snapped";
                }
                x %= width; if (x < 0) x += width;
                if (live.Any(p => Periodic(p, x, y, width) < looks.RuinMinGap)) { Skip(plan, "nearSettlement"); continue; }
                if (markers.Any(p => Periodic(p, x, y, width) < MarkerHoverRadius)) { Skip(plan, "sharedSite"); continue; }
                markers.Add(new[] { x, y });
                uint h = Hash(ruin.Uid);
                plan.Markers.Add(new RuinMarker { Ruin = ruin, Prefab = looks.RuinPieces[(int)(h % (uint)looks.RuinPieces.Count)], X = plan.Canonical(x), Y = y + plan.PoleMargin,
                    Yaw = (h >> 8) % 360 * Math.PI / 180, Scale = looks.RuinScale, Placement = placement });
            }
        }
        private static void Skip(MapDressingPlan plan, string reason) { int n; plan.SkippedRuins.TryGetValue(reason, out n); plan.SkippedRuins[reason] = n + 1; }
        public static double Periodic(double[] p, double x, double y, double width) { double dx = Math.Abs(p[0] - x) % width; dx = Math.Min(dx, width - dx); return Math.Sqrt(dx * dx + (p[1] - y) * (p[1] - y)); }
        // FNV-1a of the uid: a stable per-ruin choice.
        private static uint Hash(string s) { uint h = 2166136261; foreach (char ch in s) { h ^= ch; h *= 16777619; } return h; }

        // ---- water (MMR-285) ----
        // The ground a point shows: the encoded height, or the engine's collision height (truncated toward zero) where that is higher.
        public static double Ground(WorldSurface surface, double x, double y) { return Math.Max(surface.Height(x, y), surface.CollisionHeight(x, y)); }
        // The same at any physical point, clamped to the terrain (a footprint corner may pass its edge).
        public static Func<double, double, double> GroundOf(WorldSurface surface)
        {
            double extent = surface.Extent;
            return (x, y) => Ground(surface, Math.Max(0, Math.Min(extent, x)), Math.Max(0, Math.Min(extent, y)));
        }
        // MMR-247 (acc5): the lower of the two, clamped the same way. A point shows water in game when the rendered terrain (the encoded height)
        // or the native height query (the collision height, which the acceptance probe reads) lies under a water plane there, so a test that
        // keeps something off water takes this ground.
        public static Func<double, double, double> LowGroundOf(WorldSurface surface)
        {
            double extent = surface.Extent;
            return (x, y) => { double cx = Math.Max(0, Math.Min(extent, x)), cy = Math.Max(0, Math.Min(extent, y)); return Math.Min(surface.Height(cx, cy), surface.CollisionHeight(cx, cy)); };
        }

        // MMR-262: a decal's projection half depth on shaped relief. The engine adapter sets the frame's z to the terrain at the decal's centre,
        // and the deferred decal shader clips ground outside the box [-1, 1] along z (and fades it by 1 - |z|^8), so a footprint on a slope
        // loses the parts farther than the half depth from the centre height. The 4 corners and 4 edge midpoints of the footprint (half extents
        // along yaw and across it, as DecalFrame turns them) are sampled; the half depth keeps the farthest within DepthReach of the box, and
        // never goes below DecalHalfDepth (so nearby settlement meshes are not over-projected on flat ground). z stays the centre's height.
        public static double SlopeDepth(Func<double, double, double> ground, double x, double y, double yaw, double halfLength, double halfWidth)
        {
            double c = Math.Cos(yaw), s = Math.Sin(yaw), centre = ground(x, y), reach = 0;
            for (int i = -1; i <= 1; i++)
                for (int j = -1; j <= 1; j++)
                {
                    if (i == 0 && j == 0) continue;
                    double u = i * halfLength, v = j * halfWidth;
                    reach = Math.Max(reach, Math.Abs(ground(x + u * c - v * s, y + u * s + v * c) - centre));
                }
            return Math.Max(DecalHalfDepth, reach / DepthReach);
        }

        // What shows a point (physical) as water without decals: "sea" (its ground below the datum, under the sea plane), "lake" (inside a
        // shown lake plane's box, the plane above its ground), or null.
        public static string PlaneCover(IList<GeneratedWaterSystem.Lake> lakes, double x, double y, double ground)
        {
            if (ground < GeneratedWaterSystem.DatumZ) return "sea";
            foreach (var l in lakes) if (x >= l.MinX && x <= l.MaxX && y >= l.MinY && y <= l.MaxY && l.Z > ground) return "lake";
            return null;
        }

        private static void Water(MapDressingPlan plan, WorldSurface surface, TravelCells cells, RoadsPolicy policy)
        {
            double sp = cells.Spacing, width = plan.Width, half = policy.RiverWidth / 2;
            plan.WaterCore = policy.OpaqueCore;
            Func<int, int, bool> river = (r, c) => cells.InRows(r) && cells.At(r, c) == TravelCells.River;
            // Edges between river cells, each once in scan order (columns unwrapped: c + 1 past the last column is the first).
            var edges = new List<int[]>();
            for (int r = 0; r < cells.Rows; r++)
                for (int c = 0; c < cells.Cols; c++)
                {
                    if (!river(r, c)) continue;
                    if (river(r, c + 1)) edges.Add(new[] { r, c, r, c + 1 });
                    if (river(r + 1, c)) edges.Add(new[] { r, c, r + 1, c });
                    if (river(r + 1, c + 1) && !river(r, c + 1) && !river(r + 1, c)) edges.Add(new[] { r, c, r + 1, c + 1 });
                    if (river(r + 1, c - 1) && !river(r, c - 1) && !river(r + 1, c)) edges.Add(new[] { r, c, r + 1, c - 1 });
                }
            plan.RiverEdges = edges.Count;
            // Mouths: a river end (at most one river edge) joins the sea and lake cells beside it (edge neighbours).
            var degree = new Dictionary<int, int>();
            foreach (var e in edges) foreach (int k in new[] { cells.Index(e[0], e[1]), cells.Index(e[2], e[3]) }) { int n; degree.TryGetValue(k, out n); degree[k] = n + 1; }
            int[][] four = { new[] { 0, 1 }, new[] { 1, 0 }, new[] { 0, -1 }, new[] { -1, 0 } };
            for (int r = 0; r < cells.Rows; r++)
                for (int c = 0; c < cells.Cols; c++)
                {
                    int n;
                    if (!river(r, c) || (degree.TryGetValue(cells.Index(r, c), out n) && n > 1)) continue;
                    foreach (var d in four)
                    {
                        int rr = r + d[0], cc = c + d[1];
                        if (!cells.InRows(rr) || (cells.At(rr, cc) != TravelCells.Sea && cells.At(rr, cc) != TravelCells.Lake)) continue;
                        edges.Add(new[] { r, c, rr, cc }); plan.MouthEdges++;
                    }
                }
            // MMR-247: the chains between ends and junctions, as logical polylines through their cell centres (columns unwrapped), smoothed.
            var lines = new List<List<double[]>>();
            foreach (var chain in Chains(cells, edges))
            {
                var points = new List<double[]>();
                foreach (var rc in chain)
                {
                    var p = new[] { (rc[1] + .5) * sp, (rc[0] + .5) * sp };
                    if (points.Count > 0) p[0] -= Math.Round((p[0] - points[points.Count - 1][0]) / width) * width;
                    points.Add(p);
                }
                for (int pass = 0; pass < policy.RiverSmoothingPasses; pass++) points = Chaikin(points);
                lines.Add(points);
                double shift = plan.Canonical(points[0][0]) - points[0][0];   // MMR-291: physical, continuous along the chain
                plan.RiverLines.Add(points.Select(p => new[] { p[0] + shift, p[1] + plan.PoleMargin }).ToList());
            }
            plan.RiverChains = lines.Count;
            var linked = new HashSet<int>(edges.SelectMany(e => new[] { cells.Index(e[0], e[1]), cells.Index(e[2], e[3]) }));
            // A stamp's box fits its core's ground (FitBox) as it is made; a stamp paints a point inside its opaque core and its clipped box.
            var relief = GroundOf(surface);
            var planes = new WaterPlanes(plan.Lakes);
            Func<RoadDecal, RoadDecal> fit = d => { FitBox(relief, planes, d); d.Floor = planes.Floor(d.X, d.Y, d.Yaw, d.HalfLength, d.HalfWidth, relief, 7); return d; };
            Func<List<RoadDecal>, double, double, bool> painted = (list, x, y) => list.Any(st => planes.Paints(st, x, y, width, policy.OpaqueCore, relief));
            // River stamps (strips along the chains, pools at lone river cells and at any centre the strips miss), stretched while river and fill
            // stamps together would pass maxDecals.
            double spacing = policy.RiverSpacing;
            var stamps = new List<RoadDecal>();
            var water = new List<WaterCell>();
            int riverCells = cells.Classes.Count(k => k == TravelCells.River), pools = 0, patches = 0;
            for (int attempt = 0; ; attempt++)
            {
                stamps.Clear(); water.Clear(); pools = patches = 0;
                foreach (var line in lines)
                {
                    double length = Length(line);
                    int k = Math.Max(1, (int)Math.Ceiling(length / spacing - 1e-9));
                    double step = length / k;
                    // A strip is RiverStampLength steps long either way: its opaque core reaches 3/4 of a step, so neighbouring cores overlap.
                    for (int i = 0; i <= k; i++)
                    {
                        double[] at, dir;
                        Locate(line, i * step, out at, out dir);
                        stamps.Add(fit(Stamp(plan, policy, "river", at[0], at[1], Math.Atan2(dir[1], dir[0]), RiverStampLength * step, half)));
                    }
                }
                for (int r = 0; r < cells.Rows; r++)
                    for (int c = 0; c < cells.Cols; c++)
                    {
                        if (!river(r, c)) continue;
                        double x = (c + .5) * sp, y = (r + .5) * sp, px = plan.Canonical(x), py = y + plan.PoleMargin;
                        bool lone = !linked.Contains(cells.Index(r, c));
                        // a centre under a shown plane shows that water; one the strips paint needs nothing more
                        if (!lone && (PlaneCover(plan.Lakes, px, py, Ground(surface, px, py)) != null || painted(stamps, px, py))) continue;
                        stamps.Add(fit(Stamp(plan, policy, "pool", x, y, Turn(r, c), half, half)));
                        if (lone) pools++; else patches++;
                    }
                int fills = 0;
                for (int r = 0; r < cells.Rows; r++)
                    for (int c = 0; c < cells.Cols; c++)
                    {
                        byte k = cells.At(r, c);
                        if (!TravelCells.Water(k)) continue;
                        double x = plan.Canonical((c + .5) * sp), y = (r + .5) * sp + plan.PoleMargin, ground = Ground(surface, x, y);
                        bool stamped = painted(stamps, x, y);
                        string cover = k == TravelCells.River && stamped ? "river" : PlaneCover(plan.Lakes, x, y, ground) ?? (stamped ? "river" : null);
                        if (cover == null) { cover = "fill"; fills++; }
                        water.Add(new WaterCell { Row = r, Column = c, Class = k, Deck = cells.Road(cells.Index(r, c)), X = x, Y = y, Ground = ground, Cover = cover });
                    }
                plan.WaterSpacingUsed = spacing;
                if (stamps.Count + fills <= policy.WaterMaxDecals || attempt >= 40 || stamps.Count <= riverCells) break;
                spacing *= 1.25;
            }
            plan.RiverStamps = stamps.Count; plan.RiverPools = pools; plan.RiverPatches = patches;
            plan.Water.AddRange(stamps);
            // A fill is a pool the size of its cell (fillScale 1: it paints nothing past the cell), turned by a hash of the cell.
            foreach (var cell in water.Where(w => w.Cover == "fill"))
                plan.Water.Add(fit(Stamp(plan, policy, "fill", cell.Column * sp + sp / 2, cell.Row * sp + sp / 2, Turn(cell.Row, cell.Column), sp / 2 * policy.FillScale, sp / 2 * policy.FillScale)));
            plan.FillStamps = plan.Water.Count - plan.RiverStamps;
            plan.WaterCells.AddRange(water);
        }

        // MMR-247: a water stamp's box: the ground under its core (7 x 7 samples over half its half extents, as DecalFrame turns them; a river
        // strip over its whole length, so a strip down a slope paints all of it; ground under a shown water plane taken at the plane) spans
        // DepthReach of the box around its centre (the shader fades
        // the box's ends by 1 - |z|^8), plus BoxPad for the ground between samples; the box centre sits BoxOffset over the centre's ground.
        // Ground of the stamp's rim higher than that (a bank) or lower (water) is not painted. A pool's or fill's box tops out FillHeadroom over
        // its centre's ground: it floods the low ground of its cell (a coast cell's beach just over the datum), not the slope behind it. A water material
        // renders on terrain only, so a tight box over-projects nothing.
        public const double BoxPad = .05, CoreSpan = .5, StripSpan = .95, FillHeadroom = .25;
        public static void FitBox(Func<double, double, double> ground, WaterPlanes planes, RoadDecal d)
        {
            double c = Math.Cos(d.Yaw), s = Math.Sin(d.Yaw), centre = ground(d.X, d.Y), lo = Double.MaxValue, hi = Double.MinValue, along = d.Kind == "river" ? StripSpan : CoreSpan;
            for (int i = -3; i <= 3; i++)
                for (int j = -3; j <= 3; j++)
                {
                    // ground under a visible water plane counts at the plane: the box is clipped there anyway
                    double u = i / 3.0 * along * d.HalfLength, v = j / 3.0 * CoreSpan * d.HalfWidth, x = d.X + u * c - v * s, y = d.Y + u * s + v * c, g = ground(x, y), level = planes.Level(x, y, g);
                    if (!Double.IsNaN(level)) g = level;
                    lo = Math.Min(lo, g - centre); hi = Math.Max(hi, g - centre);
                }
            if (d.Kind != "river") hi = Math.Min(hi, FillHeadroom);
            d.BoxOffset = (lo + hi) / 2;
            d.HalfDepth = (hi - lo) / 2 / DepthReach + BoxPad;
        }

        // MMR-247: the river graph's chains, each a list of cells (row, wrapped column) from an end or junction to the next: a chain runs on
        // through river cells with exactly two edges and stops at any other node (a river end, a junction, or the sea or lake cell of a
        // mouth). Cycles left over start at their first unused edge. Deterministic: nodes in index order, edges in list order.
        private static List<List<int[]>> Chains(TravelCells cells, List<int[]> edges)
        {
            var adjacent = new SortedDictionary<int, List<int>>();
            var at = new Dictionary<int, int[]>();
            for (int id = 0; id < edges.Count; id++)
                for (int end = 0; end < 2; end++)
                {
                    int r = edges[id][2 * end], c = cells.Wrap(edges[id][2 * end + 1]), key = cells.Index(r, c);
                    List<int> list;
                    if (!adjacent.TryGetValue(key, out list)) adjacent[key] = list = new List<int>();
                    list.Add(id); at[key] = new[] { r, c };
                }
            Func<int, bool> through = key => adjacent[key].Count == 2 && cells.Classes[key] == TravelCells.River;
            Func<int, int, int> other = (id, key) => { int a = cells.Index(edges[id][0], edges[id][1]); return a == key ? cells.Index(edges[id][2], edges[id][3]) : a; };
            var used = new bool[edges.Count];
            var chains = new List<List<int[]>>();
            Action<int, int> walk = (start, first) =>
            {
                var chain = new List<int[]> { at[start] };
                int key = start, id = first;
                while (id >= 0)
                {
                    used[id] = true;
                    key = other(id, key);
                    chain.Add(at[key]);
                    id = through(key) ? adjacent[key].Where(e => !used[e]).DefaultIfEmpty(-1).First() : -1;
                }
                chains.Add(chain);
            };
            foreach (var kv in adjacent) if (!through(kv.Key)) foreach (int id in kv.Value) if (!used[id]) walk(kv.Key, id);
            for (int id = 0; id < edges.Count; id++) if (!used[id]) walk(cells.Index(edges[id][0], edges[id][1]), id);
            return chains;
        }

        // A stable turn per cell for pools (their irregular rim does not repeat across neighbours).
        private static double Turn(int row, int column) { return Mix(row, column) % 3600 / 3600.0 * 2 * Math.PI; }
        private static uint Mix(int a, int b) { uint h = (uint)a * 0x9E3779B1u ^ ((uint)b + 0x7F4A7C15u) * 0x85EBCA77u; h ^= h >> 15; h *= 0xC2B2AE3Du; h ^= h >> 13; return h; }

        // A water stamp at a logical point: a river strip (the atlas's river column) or a pool (its pool column) of the published water
        // material, the atlas row (variant) by a hash of the point; the fallback is the policy's tinted vanilla stamp.
        private static RoadDecal Stamp(MapDressingPlan plan, RoadsPolicy policy, string kind, double x, double y, double yaw, double halfLength, double halfWidth)
        {
            int column = kind == "river" ? policy.RiverColumn : policy.PoolColumn, row = (int)(Mix((int)Math.Floor(x * 4), (int)Math.Floor(y * 4)) % (uint)policy.WaterRows);
            return new RoadDecal { Road = -1, Piece = -1, Family = "water", Kind = kind, Material = policy.WaterMaterial, X = plan.Canonical(x), Y = y + plan.PoleMargin, Yaw = yaw,
                HalfLength = halfLength, HalfWidth = halfWidth, HalfDepth = DecalHalfDepth, Atlas = policy.WaterCell(column, row), Tint = policy.WaterTint,
                Fallback = policy.WaterFallbackMaterial, FallbackAtlas = (float[])policy.WaterFallbackAtlas.Clone(), FallbackTint = policy.WaterFallbackTint };
        }

        // The ruins encyclopedia rows (RuinsEncyclopediaPage): every ruin of the world, in source order: id, name, description.
        public static List<string[]> EncyclopediaRows(MapDressingPlan plan)
        {
            return plan.Ruins.Select(r => new[] { r.Uid, r.Name, (r.CivilizationName ?? r.Civilization ?? "Unknown") + " - " + RuinNames.Cause(r.Cause) +
                (r.DestroyedAge >= 0 ? " (age " + r.DestroyedAge.ToString(CultureInfo.InvariantCulture) + ")" : "") }).ToList();
        }
    }

    // What the installer draws on: the engine scene (MapDressingRuntime) or a recording target offline.
    internal interface IMapDressingTarget
    {
        object AddDecal(RoadDecal decal, double x, double y);
        void MoveDecal(object handle, RoadDecal decal, double x, double y);
        void RemoveDecal(object handle);
        object AddEntity(string prefab, double x, double y, double z, double yaw, double scale);
        void MoveEntity(object handle, double x, double y, double z, double yaw, double scale);
        void RemoveEntity(object handle);
        double Ground(double x, double y);
    }

    // How a refresh takes a bridge or ruin marker (an installed prefab entity) to its new image (MMR-247 X, results/MMR-247-X.md).
    //   Recreate  removed, then instantiated again at the new image (the product). A prefab entity can carry an occlusion body
    //             (ruinous_battania_castle_tower_a and _wall_a do), which gives it an occluder record in the scene's software occlusion
    //             rasterizer. Scene.CheckResources rebuilds that rasterizer (TaleWorlds.Native 0x412620 under 0x4d8c10) without re-creating the
    //             records entities hold; MapScreen calls it whenever its scene view is enabled again (the first time after a load, after a
    //             conversation, a mission or another screen). A moved entity's record is written on the next scene tick (0x34a430 -> the
    //             rasterizer's slot 3, 0x55e5b0): a stale one writes freed memory, and after a mission's heap churn that faulted (acc4, acc5,
    //             acc5rep: c0000005 at TaleWorlds.Native+0x55e625). A new entity takes a record from the current rasterizer; its own frame is
    //             set with it. Every removal of a refresh runs before its first creation.
    //   Move      SetGlobalFrame in place (the pre-fix behaviour: the hang repro's control).
    //   Frozen    left where it is (the hang repro's bisect: no dressing entity moves).
    internal enum EntityImageMode { Recreate, Move, Frozen }

    // Draws a plan on a target (road decals, bridges, ruin markers, water decals) at each object's image (image: canonical physical x -> the drawn x, the periodic image nearest the camera),
    // refreshes the images when the camera moves, and removes everything it drew (or, on a scene the engine cleared, drops the handles).
    // Every object is tracked from its creation to its removal.
    internal sealed class MapDressingInstaller
    {
        // The bridges' and markers' refresh (EntityImageMode). Process-wide: the hang repro's variants set it (Map2AcceptanceDecalLab); the
        // product never changes it.
        internal static EntityImageMode EntityImages = EntityImageMode.Recreate;
        private sealed class Drawn { public object Handle; public double X; public int Kind; public int Index; public double Z; }
        public readonly MapDressingPlan Plan;
        private readonly IMapDressingTarget target;
        private readonly List<Drawn> drawn = new List<Drawn>();
        public readonly List<string> Errors = new List<string>();
        public int Created, Removed, Moved, Dropped;
        public int Recreated, Frozen, MovedEntities;   // MMR-247 X: entities instantiated again at a new image; left undone (Frozen); moved in place (Move)
        public Func<Dictionary<string, object>> Describe { get; set; }   // MMR-247: the target's own record (the engine target: materials drawn, boxes clipped)
        public bool Installed { get; private set; }
        public int Live { get { return drawn.Count; } }
        // MMR-291: water decals another layer draws instead (the river overlay meshes: rivers, pools and, by policy, fills); not created, counted.
        public Func<RoadDecal, bool> SkipWater { get; set; }
        public int ReplacedWater { get; private set; }

        public MapDressingInstaller(MapDressingPlan plan, IMapDressingTarget target) { Plan = plan; this.target = target; }

        public void Install(Func<double, double> image)
        {
            if (Installed) throw new InvalidOperationException("The map dressing is already installed.");
            Installed = true;
            for (int i = 0; i < Plan.Decals.Count; i++) Add(0, i, image(Plan.Decals[i].X), 0);
            for (int i = 0; i < Plan.Bridges.Count; i++)
            {
                var s = Plan.Bridges[i];
                double z = Math.Max(target.Ground(s.BankA[0], s.BankA[1]), target.Ground(s.BankB[0], s.BankB[1]));
                Add(1, i, image(s.X), z);
            }
            for (int i = 0; i < Plan.Markers.Count; i++) { var m = Plan.Markers[i]; Add(2, i, image(m.X), target.Ground(m.X, m.Y)); }
            ReplacedWater = 0;
            for (int i = 0; i < Plan.Water.Count; i++)   // MMR-285
            {
                if (SkipWater != null && SkipWater(Plan.Water[i])) { ReplacedWater++; continue; }
                Add(3, i, image(Plan.Water[i].X), 0);
            }
        }

        private void Add(int kind, int index, double x, double z)
        {
            try
            {
                object handle;
                if (kind == 0) handle = target.AddDecal(Plan.Decals[index], x, Plan.Decals[index].Y);
                else if (kind == 3) handle = target.AddDecal(Plan.Water[index], x, Plan.Water[index].Y);
                else if (kind == 1) { var s = Plan.Bridges[index]; handle = target.AddEntity(s.Prefab, x, s.Y, z, s.Yaw, s.Scale); }
                else { var m = Plan.Markers[index]; handle = target.AddEntity(m.Prefab, x, m.Y, z, m.Yaw, m.Scale); }
                if (handle == null) { if (Errors.Count < 20) Errors.Add((kind == 0 ? "decal " : kind == 1 ? "bridge " : kind == 2 ? "ruin " : "water ") + index + ": not created"); return; }
                drawn.Add(new Drawn { Handle = handle, X = x, Kind = kind, Index = index, Z = z }); Created++;
            }
            catch (Exception e) { if (Errors.Count < 20) Errors.Add("create " + kind + "/" + index + ": " + e.GetType().Name + ": " + e.Message); }
        }

        // Takes every object whose image changed to it: decals moved in place (no occluder record: Decal frames never reach the occlusion
        // code), bridges and markers by EntityImages.
        public void Refresh(Func<double, double> image)
        {
            List<KeyValuePair<Drawn, double>> renew = null;
            foreach (var d in drawn)
            {
                double x;
                if (d.Kind == 0) x = image(Plan.Decals[d.Index].X); else if (d.Kind == 1) x = image(Plan.Bridges[d.Index].X); else if (d.Kind == 2) x = image(Plan.Markers[d.Index].X); else x = image(Plan.Water[d.Index].X);
                if (Math.Abs(x - d.X) < 1e-6) continue;
                if ((d.Kind == 1 || d.Kind == 2) && EntityImages != EntityImageMode.Move)
                {
                    if (EntityImages == EntityImageMode.Frozen) Frozen++;
                    else (renew ?? (renew = new List<KeyValuePair<Drawn, double>>())).Add(new KeyValuePair<Drawn, double>(d, x));
                    continue;
                }
                try
                {
                    if (d.Kind == 0) target.MoveDecal(d.Handle, Plan.Decals[d.Index], x, Plan.Decals[d.Index].Y);
                    else if (d.Kind == 3) target.MoveDecal(d.Handle, Plan.Water[d.Index], x, Plan.Water[d.Index].Y);
                    else if (d.Kind == 1) { var s = Plan.Bridges[d.Index]; target.MoveEntity(d.Handle, x, s.Y, d.Z, s.Yaw, s.Scale); MovedEntities++; }
                    else { var m = Plan.Markers[d.Index]; target.MoveEntity(d.Handle, x, m.Y, d.Z, m.Yaw, m.Scale); MovedEntities++; }
                    d.X = x; Moved++;
                }
                catch (Exception e) { if (Errors.Count < 20) Errors.Add("move " + d.Kind + "/" + d.Index + ": " + e.GetType().Name + ": " + e.Message); }
            }
            if (renew != null) Renew(renew);
        }

        // EntityImageMode.Recreate: every entity removed first, then each instantiated at its new image. One whose removal failed stays where it
        // is (still drawn, not duplicated); one that is not created again is no longer drawn (its removal ran). Errors are recorded.
        private void Renew(List<KeyValuePair<Drawn, double>> items)
        {
            var removed = new List<KeyValuePair<Drawn, double>>();
            foreach (var kv in items)
            {
                try { target.RemoveEntity(kv.Key.Handle); removed.Add(kv); }
                catch (Exception e) { if (Errors.Count < 20) Errors.Add("renew remove " + kv.Key.Kind + "/" + kv.Key.Index + ": " + e.GetType().Name + ": " + e.Message); }
            }
            foreach (var kv in removed)
            {
                var d = kv.Key; double x = kv.Value;
                object handle = null;
                try
                {
                    if (d.Kind == 1) { var s = Plan.Bridges[d.Index]; handle = target.AddEntity(s.Prefab, x, s.Y, d.Z, s.Yaw, s.Scale); }
                    else { var m = Plan.Markers[d.Index]; handle = target.AddEntity(m.Prefab, x, m.Y, d.Z, m.Yaw, m.Scale); }
                    if (handle == null && Errors.Count < 20) Errors.Add((d.Kind == 1 ? "bridge " : "ruin ") + d.Index + ": not created again");
                }
                catch (Exception e) { if (Errors.Count < 20) Errors.Add("renew create " + d.Kind + "/" + d.Index + ": " + e.GetType().Name + ": " + e.Message); }
                d.Handle = handle;
                if (handle != null) { d.X = x; Recreated++; }
            }
            drawn.RemoveAll(d => d.Handle == null);
        }

        // How many objects of a kind are drawn (0 road decals, 1 bridge segments, 2 ruin markers, 3 water decals), and the drawn water decals'
        // plan indices with their handles and drawn x (the in-game water check).
        public int DrawnCount(int kind) { return drawn.Count(d => d.Kind == kind); }
        public IEnumerable<KeyValuePair<int, KeyValuePair<object, double>>> DrawnWater()
        {
            foreach (var d in drawn) if (d.Kind == 3) yield return new KeyValuePair<int, KeyValuePair<object, double>>(d.Index, new KeyValuePair<object, double>(d.Handle, d.X));
        }

        // The drawn x of each marker (hover), in plan order of the drawn markers.
        public IEnumerable<KeyValuePair<RuinMarker, double>> DrawnMarkers()
        {
            foreach (var d in drawn) if (d.Kind == 2) yield return new KeyValuePair<RuinMarker, double>(Plan.Markers[d.Index], d.X);
        }

        // Removes everything drawn, latest first, on a live scene; a failing removal is recorded and the rest still run. Each handle goes once
        // (it leaves the list here): the engine's decal set erases its end for a decal it does not hold.
        public void Remove()
        {
            for (int i = drawn.Count - 1; i >= 0; i--)
            {
                var d = drawn[i];
                try { if (d.Kind == 0 || d.Kind == 3) target.RemoveDecal(d.Handle); else target.RemoveEntity(d.Handle); }
                catch (Exception e) { if (Errors.Count < 20) Errors.Add("remove " + d.Kind + "/" + d.Index + ": " + e.GetType().Name + ": " + e.Message); }
                Removed++;
            }
            drawn.Clear();
            Installed = false;
        }

        // MMR-262: lets go of every handle with no target call, for a scene the engine already cleared (MapScreen.OnFinalize's Scene.ClearAll
        // took the decals and entities and freed the decal container a RemoveDecalInstance would read). Returns how many were let go.
        public int Drop()
        {
            int n = drawn.Count;
            drawn.Clear();
            Installed = false;
            Dropped += n;
            return n;
        }

        // What the installer draws on (MapDressingRuntime compares its scene with the one a new install draws on).
        public IMapDressingTarget Target { get { return target; } }

        public Dictionary<string, object> Record()
        {
            var r = Plan.Record();
            r["installed"] = new Dictionary<string, object> { ["created"] = Created, ["live"] = drawn.Count, ["water"] = DrawnCount(3), ["waterReplacedByMeshes"] = ReplacedWater, ["moved"] = Moved, ["movedEntities"] = MovedEntities, ["recreated"] = Recreated, ["frozen"] = Frozen,
                ["entityImages"] = EntityImages.ToString(), ["removed"] = Removed, ["dropped"] = Dropped, ["errors"] = Errors.ToList() };
            if (Describe != null) r["target"] = Describe();
            return r;
        }
    }
}
