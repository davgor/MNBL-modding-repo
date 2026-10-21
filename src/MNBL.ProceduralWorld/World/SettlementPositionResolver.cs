using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace MNBL.ProceduralWorld.Surface
{
    internal enum SiteKind { Town, Castle, Village, Hideout }

    // One campaign settlement to place: its record identity, kind and the inputs its markers are placed from (logical positions).
    internal sealed class SettlementSite
    {
        public string Id, Uid, BoundTo, Source;
        public SiteKind Kind;
        public bool HasPort, Harbour;
        public int[] HarbourCell;      // a harbour village's MMR-28 harbour cell (row, column)
        public int HarbourBasin = -1;
        public double[] Centre;        // logical; for sites without a navigation point (hideouts placed by MMR-34; fixture worlds)
        public double[] Port { get; set; }   // logical; a port town's given port when there is no navigation plan (fixture worlds)
        public int PortBasin = -1;
        public LookChoice Look;        // MMR-258: the settlement's look (null: no selection ran; phase 0 at the policy's medians)
        public SettlementFrame Frame;  // MMR-258: a fortification's markers from its look (null: the policy's installed medians)
        public bool Fortification { get { return Kind == SiteKind.Town || Kind == SiteKind.Castle; } }
    }

    // MMR-258: a fortification's gate and markers from its look (SettlementLook), at the look's scale in its gate frame: the gate distance, every
    // gate-frame marker, wall section and wall engine the look holds (so the scene's markers stand where the look's do until a ground rule moves
    // them), and for a port town whose look has a harbour the gate direction that turns the look's harbour to the world's port.
    internal sealed class SettlementFrame
    {
        public double GateDistance;
        public double? GateAngle;    // the first gate candidate's direction (logical radians); null: toward the road
        public string GateRule;
        public readonly List<SettlementPositionResolver.MarkerSlot> Slots = new List<SettlementPositionResolver.MarkerSlot>();

        public static SettlementFrame FromLook(SettlementLook look, double scale)
        {
            var frame = new SettlementFrame { GateDistance = look.GateDistance * scale };
            foreach (var s in look.Slots.Where(x => x.Level == null))
                frame.Slots.Add(new SettlementPositionResolver.MarkerSlot { Tag = s.Tag, Suffix = SettlementPositionResolver.Suffix(s.Tag, s.Copy), Offset = new[] { s.Forward * scale, s.Side * scale },
                                                                           LocalYaw = s.Yaw, LocalScale = s.Scale });
            foreach (var level in SettlementPlacementPolicy.WallLevels)
            {
                foreach (var s in look.Slots.Where(x => x.Tag == "map_breachable_wall" && x.Level == level))
                    frame.Slots.Add(new SettlementPositionResolver.MarkerSlot { Tag = s.Tag, Suffix = "wall_" + level.Substring(6) + "_" + s.Copy, Level = level, Offset = new[] { s.Forward * scale, s.Side * scale },
                                                                               Wall = true, LocalYaw = s.Yaw, LocalScale = s.Scale, Children = s.Children });
                for (int n = 0; n < 4; n++)
                    foreach (var s in look.Slots.Where(x => x.Tag == "map_defensive_engine_" + n && x.Level == level))
                        frame.Slots.Add(new SettlementPositionResolver.MarkerSlot { Tag = s.Tag, Suffix = "defender_" + n + "_" + level.Substring(6) + (s.Copy > 0 ? "_" + s.Copy : ""), Level = level, Offset = new[] { s.Forward * scale, s.Side * scale },
                                                                                   LocalYaw = s.Yaw, LocalScale = s.Scale });
            }
            return frame;
        }
    }

    internal sealed class PlacedMarker
    {
        public string Tag, Suffix, Rule, Level;   // Level: the scene level of a per-level copy (wall sections and wall engines), else null
        public double[] Logical;       // x, y
        public double[] From;          // its unadjusted position (the installed offset in the gate frame)
        public double Moved;           // distance from its unadjusted position
        public int Turns;              // 22.5-degree turns about the centre (signed)
        public double Scale = 1;       // fraction of the installed offset kept
        public double Slope;           // degrees at its position
        public bool Steep;             // no candidate met the slope limit: the least steep valid one was taken
        public double LocalYaw = -Math.PI / 2, LocalScale = 1;   // MMR-258: its look entity's yaw (the gate frame's forward is local +x) and scale
        public string Children;        // MMR-258: a look wall section's solid and broken wall children (normalized XML)
    }

    // A settlement's resolved placement: the derived world artifact the scene entities and the settlement records are built from.
    internal sealed class ResolvedSettlement
    {
        public SettlementSite Site;
        public double[] Centre, Gate, Port, PortEntrance, Dropoff, ArcStart, ArcEnd;   // logical x, y
        public double GateAngle;                                                         // radians, logical frame
        public double GateSlope; public bool GateSteep;
        public string GateRule;
        public int ArcTurns; public bool ArcMirrored; public double ArcScale = 1, ArcShift;   // ArcShift: cells the arc's frame moved seaward
        public List<double[]> ArcShips;                                                  // the ship positions BlockadePositionScript computes (logical)
        public double PadSlope;
        public LookChoice Look;                                                          // MMR-258: its look (null: phase 0 without a selection)
        public readonly List<PlacedMarker> Markers = new List<PlacedMarker>();          // gate-frame markers and per-level wall engines
        public readonly List<PlacedMarker> Walls = new List<PlacedMarker>();            // breachable wall sections, two per wall level
        public readonly List<Dictionary<string, object>> Provenance = new List<Dictionary<string, object>>();
    }

    internal sealed class SettlementProblem
    {
        public string Code, Subject, Detail;
        public bool Refusal;
        public override string ToString() { return (Refusal ? "refused " : "warning ") + Code + " " + Subject + ": " + Detail; }
        public Dictionary<string, object> Record() { return new Dictionary<string, object> { ["code"] = Code, ["subject"] = Subject, ["detail"] = Detail, ["refusal"] = Refusal }; }
    }

    internal sealed class SettlementPlacementRefusedException : Exception
    {
        public readonly IList<SettlementProblem> Problems;
        public SettlementPlacementRefusedException(string world, IList<SettlementProblem> problems)
            : base(world + ": " + problems.Count(p => p.Refusal) + " settlement placement refusals, first: " + problems.First(p => p.Refusal)) { Problems = problems; }
    }

    // What the resolver asks of the ground, at logical positions (x wraps): the MMR-28 cell class, the land component (-1 off land or on
    // mountain), the basin (-1 off water), the collision height at the physical position, and the slope in degrees.
    internal interface IPlacementGround
    {
        double Width { get; }
        double Height { get; }
        double Spacing { get; }
        byte Class(double x, double y);
        int Land(double x, double y);
        int Basin(double x, double y);
        double Elevation(double x, double y);
        double Slope(double x, double y);
        double[] Physical(double x, double y);
    }

    // The production ground: the navigation build's cells (classes, land components, basins) and the surface's collision heights.
    internal sealed class NavigationGround : IPlacementGround
    {
        private readonly NavigationBuild build;
        private readonly WorldSurface surface;
        private readonly PeriodicWorldCoordinates coordinates;
        public NavigationGround(NavigationBuild build) { this.build = build; surface = build.Input.Surface; coordinates = PeriodicWorldCoordinates.FromSurface(surface); }
        private TravelCells T { get { return build.Input.Cells; } }
        public double Width { get { return T.Cols * T.Spacing; } }
        public double Height { get { return T.Rows * T.Spacing; } }
        public double Spacing { get { return T.Spacing; } }
        private int Index(double x, double y, out bool inside)
        {
            var c = T.CellOf(Wrap(x), y); inside = T.InRows(c[0]);
            return inside ? T.Index(c[0], c[1]) : -1;
        }
        private double Wrap(double x) { return ((x % Width) + Width) % Width; }
        public byte Class(double x, double y) { bool inside; int i = Index(x, y, out inside); return inside ? T.Classes[i] : TravelCells.Mountain; }
        public int Land(double x, double y) { bool inside; int i = Index(x, y, out inside); return inside && T.Classes[i] == TravelCells.Land ? build.LandOf[i] : -1; }
        public int Basin(double x, double y) { bool inside; int i = Index(x, y, out inside); return inside && TravelCells.Water(T.Classes[i]) ? build.BasinOf[i] : -1; }
        public double[] Physical(double x, double y) { return coordinates.ToPhysical(Wrap(x), y); }
        public double Elevation(double x, double y) { var p = Physical(x, y); return surface.CollisionHeight(p[0], p[1]); }
        public double Slope(double x, double y)
        {
            var p = Physical(x, y); var n = surface.Normal(p[0], p[1]);
            double z = Math.Max(-1, Math.Min(1, n[2] / Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2])));
            return Math.Acos(z) * 180 / Math.PI;
        }
    }

    // worldgen/policies/settlement-placement.json (tools/make_settlement_placement_policy.py, from the installed War Sails campaign map) and the
    // MMR-35 blockade arc (worldgen/policies/naval-content.json).
    internal sealed class SettlementPlacementPolicy
    {
        public const string Resource = "MNBL.ProceduralWorld.settlement-placement.json", NavalResource = "MNBL.ProceduralWorld.naval-content.json";
        public const string PolicyPath = "worldgen/policies/settlement-placement.json", NavalPath = "worldgen/policies/naval-content.json";
        public static readonly string[] WallLevels = { "level_1", "level_2", "level_3" };
        public string Sha256, SourceSha256, SolidWallMesh, BrokenWallMesh;
        public double GateDistance, MaxPortCentre, MaxPortGate, MaxDropoff, MaxPadSlope, MaxMarkerSlope, Interior, MinSeparation, TurnStepDegrees;
        public int SiegeTowers, TownBanners, CastleBanners, VillageBanners;
        public Dictionary<string, double[]> Markers = new Dictionary<string, double[]>(StringComparer.Ordinal);   // tag -> forward, side
        public Dictionary<string, double[]> Walls = new Dictionary<string, double[]>(StringComparer.Ordinal);     // "level_N/slot" -> forward, side
        public Dictionary<string, double[]> WallEngines = new Dictionary<string, double[]>(StringComparer.Ordinal); // "engine/level_N" -> forward, side
        public Dictionary<string, string[]> WallLevelSets = new Dictionary<string, string[]>(StringComparer.Ordinal), WallEngineLevelSets = new Dictionary<string, string[]>(StringComparer.Ordinal);
        public Dictionary<string, double> CircleScale = new Dictionary<string, double>(StringComparer.Ordinal);    // town, castle
        public Dictionary<string, double[]> CampScale = new Dictionary<string, double[]>(StringComparer.Ordinal);  // camp tag -> sx, sy
        public double[] ArcStart, ArcEnd;   // along, left in the port's seaward frame
        public Dictionary<string, string> Blockade = new Dictionary<string, string>(StringComparer.Ordinal);       // BlockadePositionScript variables

        public static SettlementPlacementPolicy Installed()
        {
            Func<string, byte[]> read = name => {
                using (var stream = typeof(SettlementPlacementPolicy).Assembly.GetManifestResourceStream(name))
                {
                    if (stream == null) throw new InvalidOperationException("The module has no embedded " + name + ".");
                    using (var copy = new MemoryStream()) { stream.CopyTo(copy); return copy.ToArray(); }
                }
            };
            return FromBytes(read(Resource), read(NavalResource));
        }

        public static SettlementPlacementPolicy FromBytes(byte[] placement, byte[] naval)
        {
            var json = new JavaScriptSerializer();
            Func<byte[], IDictionary<string, object>> parse = b => (IDictionary<string, object>)json.DeserializeObject(new UTF8Encoding(false).GetString(b).TrimStart('﻿'));
            var doc = parse(placement); var navalDoc = parse(naval);
            if (Json.S(doc, "schema") != "mnbl.settlement-placement" || Json.I(doc, "version") != 2) throw new InvalidDataException("Not an mnbl.settlement-placement version 2 policy.");
            var ground = Json.O(doc, "ground"); var counts = Json.O(doc, "counts"); var banners = Json.O(counts, "banners");
            var walls = Json.O(doc, "walls"); var engines = Json.O(doc, "wallEngines"); var scales = Json.O(doc, "scales");
            var p = new SettlementPlacementPolicy {
                SourceSha256 = Json.S(Json.O(doc, "source"), "sha256"), GateDistance = Json.D(Json.O(doc, "gate"), "distance"),
                MaxPortCentre = Json.D(Json.O(doc, "port"), "maxCentreDistance"), MaxPortGate = Json.D(Json.O(doc, "port"), "maxGateDistance"), MaxDropoff = Json.D(Json.O(doc, "dropoff"), "maxDistance"),
                MaxPadSlope = Json.D(ground, "maxPadSlopeDegrees"), MaxMarkerSlope = Json.D(ground, "maxMarkerSlopeDegrees"), Interior = Json.D(ground, "interior"),
                MinSeparation = Json.D(ground, "minMarkerSeparation"), TurnStepDegrees = Json.D(ground, "turnStepDegrees"),
                SiegeTowers = Json.I(counts, "siegeTowers"), TownBanners = Json.I(banners, "town"), CastleBanners = Json.I(banners, "castle"), VillageBanners = Json.I(banners, "village"),
                SolidWallMesh = Json.S(walls, "solidMesh"), BrokenWallMesh = Json.S(walls, "brokenMesh") };
            Func<object, double[]> pair = v => ((object[])v).Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray();
            foreach (var kv in Json.O(doc, "markers")) { var m = (IDictionary<string, object>)kv.Value; p.Markers[kv.Key] = new[] { Json.D(m, "forward"), Json.D(m, "side") }; }
            foreach (IDictionary<string, object> w in Json.A(walls, "slots")) p.Walls[Json.S(w, "level") + "/" + Json.I(w, "slot")] = new[] { Json.D(w, "forward"), Json.D(w, "side") };
            foreach (IDictionary<string, object> w in Json.A(engines, "slots")) p.WallEngines[Json.I(w, "engine") + "/" + Json.S(w, "level")] = new[] { Json.D(w, "forward"), Json.D(w, "side") };
            foreach (var kv in Json.O(walls, "levels")) p.WallLevelSets[kv.Key] = ((object[])kv.Value).Cast<string>().ToArray();
            foreach (var kv in Json.O(engines, "levels")) p.WallEngineLevelSets[kv.Key] = ((object[])kv.Value).Cast<string>().ToArray();
            foreach (var kv in Json.O(scales, "circle")) p.CircleScale[kv.Key] = Convert.ToDouble(kv.Value, CultureInfo.InvariantCulture);
            foreach (var kv in Json.O(scales, "campArea")) p.CampScale[kv.Key] = pair(kv.Value);
            foreach (var level in WallLevels)
            {
                if (!p.Walls.ContainsKey(level + "/0") || !p.Walls.ContainsKey(level + "/1") || !p.WallLevelSets.ContainsKey(level) || !p.WallEngineLevelSets.ContainsKey(level)) throw new InvalidDataException("The placement policy lacks the walls or wall engines of " + level + ".");
                for (int n = 0; n < 4; n++) if (!p.WallEngines.ContainsKey(n + "/" + level)) throw new InvalidDataException("The placement policy lacks wall engine " + n + " of " + level + ".");
            }
            var blockades = Json.O(navalDoc, "blockades"); var arc = Json.O(blockades, "arc");
            p.ArcStart = pair(arc["start"]); p.ArcEnd = pair(arc["end"]);
            foreach (var kv in Json.O(blockades, "parameters")) p.Blockade[kv.Key] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
            using (var sha = SHA256.Create()) p.Sha256 = BitConverter.ToString(sha.ComputeHash(placement.Concat(new byte[] { 10 }).Concat(naval).ToArray())).Replace("-", "").ToLowerInvariant();
            return p;
        }

        // Refuses a policy measured on another War Sails campaign map than the installed one (an update would leave old medians silently).
        public void RequireSource(string installedSha256)
        {
            if (installedSha256 != SourceSha256) throw new InvalidDataException("The settlement placement policy was measured on campaign map " + SourceSha256.Substring(0, 12) + ", the installed one is " + installedSha256.Substring(0, 12) + "; run tools/make_settlement_placement_policy.py.");
        }
    }

    internal sealed class SettlementLayout
    {
        public string WorldContentId, PolicySha256, Sha256;
        public readonly List<ResolvedSettlement> Settlements = new List<ResolvedSettlement>();
        public readonly List<SettlementProblem> Problems = new List<SettlementProblem>();
        public Dictionary<string, object> Looks;   // MMR-258: the look-usage and reuse report (SettlementLookSelector.Report), null without a selection
        public bool Activatable { get { return !Problems.Any(p => p.Refusal); } }
        public ResolvedSettlement Find(string id) { return Settlements.FirstOrDefault(s => s.Site.Id == id); }

        // The positions the settlement records take from this layout (SettlementFactory.ApplyResolvedPositions, logical like the records), so
        // the records and the scene entities come from one placement; SettlementFactory.CheckResolvedPositions refuses a record that differs.
        public CampaignRegistry.SettlementFactory.ResolvedPositions Positions(string id)
        {
            var r = Find(id);
            return r == null ? null : new CampaignRegistry.SettlementFactory.ResolvedPositions { Centre = r.Centre, Gate = r.Gate, Port = r.Port, Dropoff = r.Dropoff, Status = "resolved (MMR-40 layout " + Sha256.Substring(0, 12) + ")" };
        }

        // The derived world artifact. Each position is given canonically ("at") and where the scene builds it ("scene": a child at its image
        // beside its settlement's centre, so a child across the periodic cut from its settlement stands in the strip).
        public Dictionary<string, object> Report(IPlacementGround ground)
        {
            Func<double[], object> phys = l => { if (l == null) return null; var p = ground.Physical(l[0], l[1]); return new[] { Math.Round(p[0], 4), Math.Round(p[1], 4) }; };
            Func<ResolvedSettlement, double[], object> scene = (s, l) => {
                if (l == null) return null; var c = ground.Physical(s.Centre[0], s.Centre[1]);
                return new[] { Math.Round(c[0] + SettlementPositionResolver.Delta(ground, s.Centre[0], l[0]), 4), Math.Round(c[1] + l[1] - s.Centre[1], 4) }; };
            Func<ResolvedSettlement, PlacedMarker, Dictionary<string, object>> marker = (s, m) => new Dictionary<string, object> { ["tag"] = m.Tag, ["suffix"] = m.Suffix, ["level"] = m.Level,
                ["at"] = phys(m.Logical), ["scene"] = scene(s, m.Logical), ["from"] = scene(s, m.From), ["rule"] = m.Rule, ["moved"] = Math.Round(m.Moved, 4), ["turns"] = m.Turns, ["scale"] = m.Scale,
                ["slope"] = Math.Round(m.Slope, 3), ["steep"] = m.Steep };
            var rows = Settlements.Select(s => new Dictionary<string, object> {
                ["id"] = s.Site.Id, ["kind"] = s.Site.Kind.ToString().ToLowerInvariant(), ["source"] = s.Site.Source, ["harbour"] = s.Site.Harbour,
                ["centre"] = phys(s.Centre), ["gate"] = phys(s.Gate), ["gateScene"] = scene(s, s.Gate), ["gateRule"] = s.GateRule, ["gateAngle"] = Math.Round(s.GateAngle, 6),
                ["port"] = phys(s.Port), ["portScene"] = scene(s, s.Port), ["portEntrance"] = phys(s.PortEntrance), ["dropoff"] = phys(s.Dropoff), ["dropoffScene"] = scene(s, s.Dropoff),
                ["arcStart"] = phys(s.ArcStart), ["arcEnd"] = phys(s.ArcEnd), ["arcStartScene"] = scene(s, s.ArcStart), ["arcEndScene"] = scene(s, s.ArcEnd),
                ["arcTurns"] = s.ArcTurns, ["arcMirrored"] = s.ArcMirrored, ["arcScale"] = s.ArcScale, ["arcShiftCells"] = s.ArcShift, ["arcShips"] = s.ArcShips == null ? 0 : s.ArcShips.Count,
                ["padSlope"] = Math.Round(s.PadSlope, 3), ["gateSlope"] = Math.Round(s.GateSlope, 3), ["gateSteep"] = s.GateSteep,
                ["markers"] = s.Markers.Select(m => marker(s, m)).ToList(), ["walls"] = s.Walls.Select(m => marker(s, m)).ToList(),
                ["look"] = s.Look == null ? null : s.Look.Record(), ["provenance"] = s.Provenance }).ToList();
            return new Dictionary<string, object> { ["schema"] = "mnbl.settlement-layout", ["version"] = 2, ["worldContentId"] = WorldContentId, ["policySha256"] = PolicySha256,
                ["layoutSha256"] = Sha256, ["activatable"] = Activatable, ["counts"] = Settlements.GroupBy(s => s.Site.Kind.ToString().ToLowerInvariant()).ToDictionary(g => g.Key, g => (object)g.Count()),
                ["harbourVillages"] = Settlements.Count(s => s.Site.Harbour), ["portTowns"] = Settlements.Count(s => s.Site.HasPort), ["blockadeArcs"] = Settlements.Count(s => s.ArcStart != null),
                ["adjusted"] = Settlements.Sum(s => s.Markers.Concat(s.Walls).Count(m => m.Turns != 0 || m.Scale < 1 || m.Steep) + (s.GateRule != null && s.GateRule.Contains("turned") ? 1 : 0)),
                ["problems"] = Problems.Select(p => p.Record()).ToList(), ["looks"] = Looks, ["settlements"] = rows };
        }
    }

    // MMR-40: where each campaign settlement's scene markers stand, resolved against the world's navigation (W02: the MMR-38 navigation points
    // are binding) and its surface (W01: classes, land components, basins and collision heights), with bounded deterministic adjustments and
    // a before/after record of each. Pure managed: the scene entities (SettlementEntityFactory) and the settlement records are built from the
    // same layout. A placement that cannot be made valid is a refusal, raised before anything is encoded (RuntimeWorldBackend.Prepare).
    //   centre       the settlement's navigation point (resolved, separated); must be passable land; a pad steeper than the policy is reported
    //                (the centre is binding: a steep pad is a terrain property, not moved here)
    //   gate         towns and castles: the installed gate distance from the centre, facing the road to the nearest connected settlement,
    //                turned in policy steps (then shrunk by quarters) until it stands on passable land of the centre's component, within the
    //                slope limit and clear of every other settlement's centre and markers; where no candidate meets the slope limit
    //                (generated terrain is far steeper than Calradia's) the least steep valid one is taken and reported; a port town's gate
    //                that leaves its port beyond the port-to-gate limit turns on until the port is within it (unchanged otherwise)
    //   markers      towns and castles: the outer gate, siege camps, siege engines, ram and towers at the installed medians in the gate frame,
    //                each adjusted the same way
    //   walls        towns and castles: two breachable wall sections per wall level and four wall engines per wall level at their installed
    //                medians (SettlementVisual keeps the copies of the current wall level and indexes them by the targeted section); land of
    //                the component, clear of other settlements
    //   port         port towns: the navigation port point (MMR-38); water of the transition's basin, within the travel graph's port-to-gate
    //                limit; a centre distance beyond the installed maximum is reported
    //   blockade     port towns: the installed arc in the port's seaward frame (MMR-35's rule), turned in 15-degree steps round the full
    //                circle and mirrored (start and end swapped, keeping the arc's handedness), its frame moved seaward by half a cell and a
    //                cell (MMR-35 fitted from the staged sea-cell port, a cell further out), then shrunk to 3/4 and 1/2; accepted only when
    //                every ship position BlockadePositionScript.GetBlockadeArc computes (BlockadeShips) is water of the port's basin; a port
    //                where none fits gets no blockade visual (no arc, no BlockadePositionScript) and is reported
    //   drop-off     harbour villages (the campaign's harbour flag, the rule SettlementFactory gives them ports by): the interior point of the
    //                village's MMR-28 harbour cell nearest the village; water of the cell's basin; a harbour village without a harbour cell
    //                is refused
    internal static class SettlementPositionResolver
    {
        // The bundle's campaign settlements (towns, castles, villages; hideouts are placed by MMR-34 from the campaign build and are given as
        // sites with a centre), their ports (MMR-28 port transitions) and harbour villages: the campaign's harbour flag, with the harbour
        // cell of any port candidate naming the village (compiled/travel/ports.json), exactly as SettlementFactory gives harbour villages ports.
        public static List<SettlementSite> SitesFromBundle(NavigationInput input, byte[] campaignJson, byte[] portsJson)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            Func<byte[], IDictionary<string, object>> parse = b => (IDictionary<string, object>)json.DeserializeObject(new UTF8Encoding(false).GetString(b).TrimStart('﻿'));
            var ports = parse(portsJson); var campaign = parse(campaignJson);
            if (Json.S(ports, "schema") != "mnbl.ports") throw new InvalidDataException("Not an mnbl.ports document.");
            var portTowns = new HashSet<string>(input.Transitions.Where(x => x.Kind == "port").Select(x => x.Town), StringComparer.Ordinal);
            var harbourCells = new Dictionary<string, KeyValuePair<int[], int>>(StringComparer.Ordinal);
            foreach (IDictionary<string, object> p in Json.A(ports, "ports"))
                foreach (IDictionary<string, object> c in Json.A(p, "candidates"))
                {
                    object village, cell, basin;
                    if (c.TryGetValue("village", out village) && village is string && c.TryGetValue("harbourCell", out cell) && cell is object[] && ((object[])cell).Length == 2)
                        harbourCells[(string)village] = new KeyValuePair<int[], int>(Json.Cell(cell), c.TryGetValue("basin", out basin) && basin != null ? Convert.ToInt32(basin, CultureInfo.InvariantCulture) : -1);
                }
            var harbourVillages = new HashSet<string>(Json.A(campaign, "villages").Cast<IDictionary<string, object>>().Where(v => { object h; return v.TryGetValue("harbour", out h) && h is bool && (bool)h; }).Select(v => Json.S(v, "id")), StringComparer.Ordinal);
            var sites = new List<SettlementSite>();
            foreach (var s in input.Settlements.Where(s => !input.Excluded.Contains(s.Id)).OrderBy(s => s.Id, StringComparer.Ordinal))
            {
                var kind = s.Kind == "town" ? SiteKind.Town : s.Kind == "castle" ? SiteKind.Castle : s.Kind == "village" ? SiteKind.Village : (SiteKind?)null;
                if (kind == null) throw new InvalidDataException("Settlement " + s.Id + " has an unknown kind " + s.Kind + ".");
                var site = new SettlementSite { Id = s.Id, Uid = s.Uid, Kind = kind.Value, Source = "bundle " + s.Kind + " (" + s.Method + ")", HasPort = kind == SiteKind.Town && portTowns.Contains(s.Id) };
                string bound; if (input.VillageTown.TryGetValue(s.Id, out bound)) site.BoundTo = bound;
                if (kind == SiteKind.Village && harbourVillages.Contains(s.Id))
                {
                    site.Harbour = true;
                    KeyValuePair<int[], int> cell; if (harbourCells.TryGetValue(s.Id, out cell)) { site.HarbourCell = cell.Key; site.HarbourBasin = cell.Value; }
                }
                sites.Add(site);
            }
            return sites;
        }

        public static SettlementLayout Resolve(IList<SettlementSite> sites, NavigationPlan plan, IPlacementGround ground, SettlementPlacementPolicy policy)
        {
            var layout = new SettlementLayout { WorldContentId = plan == null ? null : plan.Build.Input.WorldContentId, PolicySha256 = policy.Sha256 };
            Action<string, string, string, bool> problem = (code, subject, detail, refusal) => layout.Problems.Add(new SettlementProblem { Code = code, Subject = subject, Detail = detail, Refusal = refusal });
            // Centres first: every marker keeps clear of every other settlement's centre and of the markers already placed for others.
            var centres = new Dictionary<string, double[]>(StringComparer.Ordinal);
            foreach (var site in sites)
            {
                var point = plan == null ? null : plan.Point(site.Id);
                double[] centre = point != null && point.Resolved != null ? point.Resolved : site.Centre;
                if (centre == null) { problem("settlement.no_position", site.Id, "no navigation point and no given centre", true); continue; }
                centres[site.Id] = centre;
            }
            var placedMarkers = new List<KeyValuePair<string, double[]>>();   // (settlement, position) of every marker placed so far
            foreach (var site in sites.Where(s => centres.ContainsKey(s.Id)))
            {
                var r = new ResolvedSettlement { Site = site, Centre = centres[site.Id], Look = site.Look };
                var point = plan == null ? null : plan.Point(site.Id);
                r.Provenance.Add(Prov("centre", point != null ? "navigation point (" + point.Snap + ")" : "given centre (" + site.Source + ")", point != null ? point.Requested : site.Centre, r.Centre));
                int land = ground.Land(r.Centre[0], r.Centre[1]);
                if (land < 0) problem("settlement.centre_not_land", site.Id, "the centre " + P(r.Centre) + " is " + TravelCells.ClassNames[ground.Class(r.Centre[0], r.Centre[1])], true);
                r.PadSlope = ground.Slope(r.Centre[0], r.Centre[1]);
                if (r.PadSlope > policy.MaxPadSlope) problem("settlement.steep_pad", site.Id, "the pad slopes " + r.PadSlope.ToString("0.0", CultureInfo.InvariantCulture) + " degrees (limit " + policy.MaxPadSlope + "); the centre is the navigation point and is not moved", false);
                Func<double[], string> clearOfOthers = q => centres.Where(c => c.Key != site.Id).Any(c => Distance(ground, c.Value, q) < policy.MinSeparation) ? "another settlement"
                    : placedMarkers.Any(m => m.Key != site.Id && Distance(ground, m.Value, q) < policy.MinSeparation) ? "another settlement's marker" : null;
                Func<double[], bool, string> landOk = (q, slopeLimited) => {
                    int at = ground.Land(q[0], q[1]);
                    if (at < 0) return TravelCells.ClassNames[ground.Class(q[0], q[1])];
                    if (at != land) return "another land component";
                    var clear = clearOfOthers(q); if (clear != null) return clear;
                    if (slopeLimited && ground.Slope(q[0], q[1]) > policy.MaxMarkerSlope) return "steep";   // last: a steep candidate is otherwise valid
                    return null; };
                Func<string, string, double, double[], bool, PlacedMarker> place = (tag, suffix, angle, offset, slopeLimited) => {
                    Dictionary<string, int> why;
                    var m = Place(r.Centre, angle, offset, q => landOk(q, slopeLimited), ground, policy, out why);
                    if (m == null) { problem("settlement.marker_unplaced", site.Id, tag + " (" + suffix + ") has no valid position near its installed offset (" + Reasons(why) + ")", true); return null; }
                    if (m.Steep) problem("settlement.steep_marker", site.Id, tag + " (" + suffix + ") slopes " + m.Slope.ToString("0.0", CultureInfo.InvariantCulture) + " degrees: no candidate met " + policy.MaxMarkerSlope + " (the least steep was taken)", false);
                    m.Tag = tag; m.Suffix = suffix; placedMarkers.Add(new KeyValuePair<string, double[]>(site.Id, m.Logical));
                    return m; };
                if (site.Fortification)
                {
                    double road = RoadAngle(site, plan, centres, ground, out r.GateRule);
                    // MMR-258: the look's gate distance (at its scale), and a harbour look's gate turned so the look's harbour faces the world's port.
                    double gateDistance = site.Frame != null ? site.Frame.GateDistance : policy.GateDistance;
                    if (site.Frame != null && site.Frame.GateAngle.HasValue) { road = site.Frame.GateAngle.Value; r.GateRule = site.Frame.GateRule; }
                    Dictionary<string, int> why;
                    var gate = Place(r.Centre, road, new[] { gateDistance, 0 }, q => landOk(q, true), ground, policy, out why);
                    // A port town's gate keeps its port within the travel graph's port-to-gate limit. The navigation places the port toward the
                    // town's point (the centre), and the bridge bounds it from there; the gate stands GateDistance further, facing its road, so a
                    // road inland can put it beyond the limit (2026-10-19: 10 towns of 8 of the 19 swept worlds, ports 11.25 from the centre and
                    // 12.5-13.8 from the gate, seeds 56 and 67 refused in game). Then the gate turns in the same steps until its port is within
                    // the limit; a gate that already keeps it is unchanged. When none does, the gate stays and the port is refused below.
                    var portForGate = site.HasPort && plan != null ? plan.Point("port:" + site.Id) : null;
                    double portGateLimit = plan == null ? 0 : plan.Build.Input.MaxPortGateUnits;
                    if (gate != null && portForGate != null && portForGate.Resolved != null && Distance(ground, gate.Logical, portForGate.Resolved) > portGateLimit)
                    {
                        Dictionary<string, int> whyPort;
                        var toward = Place(r.Centre, road, new[] { gateDistance, 0 }, q => {
                            string reason = landOk(q, false);
                            if (reason == null && Distance(ground, q, portForGate.Resolved) > portGateLimit) reason = "beyond its port's port-to-gate limit";
                            if (reason == null && ground.Slope(q[0], q[1]) > policy.MaxMarkerSlope) reason = "steep";
                            return reason; }, ground, policy, out whyPort);
                        if (toward != null) { gate = toward; r.GateRule = r.GateRule + "; turned to keep its port within " + portGateLimit; }
                    }
                    if (gate == null) problem("settlement.gate_unplaced", site.Id, "no gate position within " + gateDistance + " of the centre stands on passable land of component " + land + ", clear of other settlements (" + Reasons(why) + ")", true);
                    else
                    {
                        r.GateSlope = gate.Slope; r.GateSteep = gate.Steep;
                        if (gate.Steep) problem("settlement.steep_marker", site.Id, "the gate slopes " + gate.Slope.ToString("0.0", CultureInfo.InvariantCulture) + " degrees: no candidate met " + policy.MaxMarkerSlope + " (the least steep was taken)", false);
                        r.Gate = gate.Logical; r.GateAngle = Math.Atan2(r.Gate[1] - r.Centre[1], Delta(ground, r.Centre[0], r.Gate[0]));
                        r.GateRule = r.GateRule + (gate.Turns != 0 || gate.Scale < 1 ? "; turned " + gate.Turns + ", kept " + gate.Scale : "") + (gate.Steep ? "; least steep (no candidate within the slope limit)" : "");
                        r.Provenance.Add(Prov("gate", r.GateRule, gate.From, r.Gate));
                        placedMarkers.Add(new KeyValuePair<string, double[]>(site.Id, r.Gate));
                        foreach (var slot in site.Frame != null ? site.Frame.Slots : MarkerSlots(policy).ToList())
                        {
                            var m = place(slot.Tag, slot.Suffix, r.GateAngle, slot.Offset, slot.Level == null);   // walls and wall engines: no slope limit
                            if (m == null) continue;
                            m.Level = slot.Level; m.LocalYaw = slot.LocalYaw; m.LocalScale = slot.LocalScale; m.Children = slot.Children;
                            (slot.Wall ? r.Walls : r.Markers).Add(m);
                        }
                    }
                }
                if (site.HasPort)
                {
                    var port = plan == null ? null : plan.Point("port:" + site.Id); var entrance = plan == null ? null : plan.Point("portEntrance:" + site.Id);
                    var transition = plan == null ? null : plan.Build.Input.Transitions.FirstOrDefault(x => x.Kind == "port" && x.Town == site.Id);
                    // A fixture world has no navigation plan: its port is given with the site, and the installed port-to-gate maximum bounds it.
                    double[] portAt = port != null && port.Resolved != null && transition != null ? port.Resolved : plan == null ? site.Port : null;
                    int basin = transition != null ? transition.Basin : site.PortBasin;
                    double gateLimit = plan == null ? policy.MaxPortGate : plan.Build.Input.MaxPortGateUnits;
                    if (portAt == null) problem("settlement.port_missing", site.Id, "the port town has no resolved navigation port point", true);
                    else
                    {
                        r.Port = portAt; r.PortEntrance = entrance == null ? null : entrance.Resolved;
                        r.Provenance.Add(port != null ? Prov("port", "navigation port point (" + port.Snap + ")", port.Requested, r.Port) : Prov("port", "given port (" + site.Source + ")", site.Port, r.Port));
                        if (ground.Basin(r.Port[0], r.Port[1]) != basin) problem("settlement.port_not_water", site.Id, "the port " + P(r.Port) + " is not water of basin " + basin, true);
                        double gateDistance = Distance(ground, r.Gate ?? r.Centre, r.Port), centreDistance = Distance(ground, r.Centre, r.Port);
                        if (gateDistance > gateLimit) problem("settlement.port_far", site.Id, "the port is " + gateDistance.ToString("0.###", CultureInfo.InvariantCulture) + " from the gate (limit " + gateLimit + ")", true);
                        if (centreDistance > policy.MaxPortCentre) problem("settlement.port_beyond_installed", site.Id, "the port is " + centreDistance.ToString("0.###", CultureInfo.InvariantCulture) + " from the centre (installed maximum " + policy.MaxPortCentre + ")", false);
                        FitArc(r, basin, ground, policy, problem);
                    }
                }
                if (site.Harbour && site.HarbourCell == null) problem("settlement.harbour_cell_missing", site.Id, "the campaign marks the village a harbour village but no port candidate names its harbour cell", true);
                else if (site.Harbour)
                {
                    double s = ground.Spacing, margin = policy.Interior * s; int[] cell = site.HarbourCell;
                    double x0 = cell[1] * s, y0 = cell[0] * s;
                    int harbourBasin = site.HarbourBasin >= 0 ? site.HarbourBasin : ground.Basin(x0 + s / 2, y0 + s / 2);   // a candidate without a basin: its cell's
                    double vx = r.Centre[0]; if (vx - (x0 + s / 2) > ground.Width / 2) vx -= ground.Width; else if ((x0 + s / 2) - vx > ground.Width / 2) vx += ground.Width;
                    var placed = new[] { Math.Max(x0 + margin, Math.Min(x0 + s - margin, vx)), Math.Max(y0 + margin, Math.Min(y0 + s - margin, r.Centre[1])) };
                    r.Dropoff = new[] { Wrap(placed[0], ground.Width), placed[1] };
                    r.Provenance.Add(Prov("dropoff", "harbour cell " + cell[0] + "," + cell[1] + " interior point nearest the village", new[] { x0 + s / 2, y0 + s / 2 }, r.Dropoff));
                    if (harbourBasin < 0 || ground.Basin(r.Dropoff[0], r.Dropoff[1]) != harbourBasin) problem("settlement.dropoff_not_water", site.Id, "the drop-off " + P(r.Dropoff) + " is not water of basin " + harbourBasin, true);
                    double d = Distance(ground, r.Centre, r.Dropoff);
                    if (d > policy.MaxDropoff) problem("settlement.dropoff_beyond_installed", site.Id, "the drop-off is " + d.ToString("0.###", CultureInfo.InvariantCulture) + " from the village (installed maximum " + policy.MaxDropoff + ")", false);
                }
                layout.Settlements.Add(r);
            }
            // Two settlements at one place.
            var placedCentres = layout.Settlements.ToList();
            for (int i = 0; i < placedCentres.Count; i++)
                for (int j = i + 1; j < placedCentres.Count; j++)
                    if (Distance(ground, placedCentres[i].Centre, placedCentres[j].Centre) < policy.MinSeparation)
                        problem("settlement.overlap", placedCentres[i].Site.Id + ", " + placedCentres[j].Site.Id, "the centres are closer than " + policy.MinSeparation, true);
            using (var sha = SHA256.Create())
            {
                var text = new StringBuilder();
                foreach (var s in layout.Settlements)
                {
                    text.Append(s.Site.Id).Append('|').Append(s.Site.Harbour).Append('|').Append(P(s.Centre)).Append('|').Append(P(s.Gate)).Append('|').Append(P(s.Port)).Append('|').Append(P(s.Dropoff)).Append('|').Append(P(s.ArcStart)).Append('|').Append(P(s.ArcEnd));
                    foreach (var m in s.Markers.Concat(s.Walls)) text.Append('|').Append(m.Suffix).Append('=').Append(P(m.Logical));
                    if (s.Look != null) text.Append("|look=").Append(s.Look.Look ?? "phase0").Append('/').Append(s.Look.Variant).Append('/').Append(s.Look.Scale.ToString("R", CultureInfo.InvariantCulture))
                        .Append('/').Append(s.Look.VillageType).Append('/').Append(s.Look.Source == null ? "-" : s.Look.Source.Sha256);
                    text.Append('\n');
                }
                layout.Sha256 = BitConverter.ToString(sha.ComputeHash(new UTF8Encoding(false).GetBytes(policy.Sha256 + "\n" + text))).Replace("-", "").ToLowerInvariant();
            }
            return layout;
        }

        internal struct MarkerSlot { public string Tag, Suffix, Level, Children; public double[] Offset; public bool Wall; public double LocalYaw, LocalScale; }

        // A fortification's markers in the gate frame (forward, side from the centre): the gate-frame markers (the second siege tower mirrored),
        // then per wall level its two breachable wall sections and four wall engines.
        public static IEnumerable<MarkerSlot> MarkerSlots(SettlementPlacementPolicy policy)
        {
            int towers = 0;
            foreach (var kv in policy.Markers)
                for (int copy = 0; copy < (kv.Key == "map_siege_tower" ? policy.SiegeTowers : 1); copy++)
                    yield return new MarkerSlot { Tag = kv.Key, Suffix = Suffix(kv.Key, kv.Key == "map_siege_tower" ? towers++ : 0), Offset = new[] { kv.Value[0], kv.Value[1] * (copy == 1 ? -1 : 1) }, LocalYaw = -Math.PI / 2, LocalScale = 1 };
            foreach (var level in SettlementPlacementPolicy.WallLevels)
            {
                for (int slot = 0; slot < 2; slot++)
                    yield return new MarkerSlot { Tag = "map_breachable_wall", Suffix = "wall_" + level.Substring(6) + "_" + slot, Level = level, Offset = policy.Walls[level + "/" + slot], Wall = true, LocalYaw = -Math.PI / 2, LocalScale = 1 };
                for (int n = 0; n < 4; n++)
                    yield return new MarkerSlot { Tag = "map_defensive_engine_" + n, Suffix = "defender_" + n + "_" + level.Substring(6), Level = level, Offset = policy.WallEngines[n + "/" + level], LocalYaw = -Math.PI / 2, LocalScale = 1 };
            }
        }

        // A settlement placed from its record alone (a settlement its scene lacks, built at run time): the record's centre, gate and port, every
        // marker at its installed offset in the record gate's frame (the gate angle is 0 when the record's gate is its centre), no ground
        // checks and no blockade arc.
        public static ResolvedSettlement Unadjusted(SettlementSite site, double[] centre, double[] gate, double[] port, IPlacementGround ground, SettlementPlacementPolicy policy)
        {
            var r = new ResolvedSettlement { Site = site, Centre = centre, Port = site.HasPort ? port : null };
            r.Provenance.Add(Prov("centre", "the settlement record (run time, no scene entity)", centre, centre));
            if (!site.Fortification) return r;
            r.Gate = gate ?? centre;
            double dx = Delta(ground, centre[0], r.Gate[0]), dy = r.Gate[1] - centre[1];
            r.GateAngle = dx * dx + dy * dy > 1e-12 ? Math.Atan2(dy, dx) : 0;
            r.GateRule = "the settlement record's gate (run time, unresolved)";
            foreach (var slot in MarkerSlots(policy))
            {
                var q = Offset(ground, centre, r.GateAngle, slot.Offset[0], slot.Offset[1]);
                (slot.Wall ? r.Walls : r.Markers).Add(new PlacedMarker { Tag = slot.Tag, Suffix = slot.Suffix, Level = slot.Level, Logical = q, From = q, Rule = "installed offset (run time, unresolved)" });
            }
            return r;
        }

        private static string Reasons(Dictionary<string, int> why) { return String.Join(", ", why.OrderByDescending(k => k.Value).Select(k => k.Value + " " + k.Key)); }

        // A marker's child name suffix; a look's further copies of a tag (MMR-258: most installed hierarchies have three map_camp_area_2) take
        // _<copy> (the towers are numbered from 0, as before).
        public static string Suffix(string tag, int copy)
        {
            string more = copy > 0 ? "_" + copy.ToString(CultureInfo.InvariantCulture) : "";
            switch (tag)
            {
                case "main_map_castle_gate": return "outer_gate" + more;
                case "map_camp_area_1": return "camp_1" + more;
                case "map_camp_area_2": return "camp_2" + more;
                case "map_siege_ram": return "ram" + more;
                case "map_siege_tower": return "tower_" + copy;
            }
            if (tag.StartsWith("map_siege_engine_", StringComparison.Ordinal)) return "attacker_" + tag.Substring("map_siege_engine_".Length) + more;
            throw new ArgumentException("No suffix for marker tag " + tag);
        }

        // The direction of the road to the nearest settlement it connects to (MMR-28 roads), else to the nearest settlement, else east.
        private static double RoadAngle(SettlementSite site, NavigationPlan plan, Dictionary<string, double[]> centres, IPlacementGround ground, out string rule)
        {
            var centre = centres[site.Id];
            IEnumerable<string> neighbours = plan == null ? Enumerable.Empty<string>() : plan.Build.Input.Roads.Where(x => x.From == site.Id || x.To == site.Id).Select(x => x.From == site.Id ? x.To : x.From);
            var best = neighbours.Where(centres.ContainsKey).OrderBy(n => Distance(ground, centre, centres[n])).ThenBy(n => n, StringComparer.Ordinal).FirstOrDefault();
            rule = "road";
            if (best == null) { best = centres.Keys.Where(k => k != site.Id).OrderBy(n => Distance(ground, centre, centres[n])).ThenBy(n => n, StringComparer.Ordinal).FirstOrDefault(); rule = "nearest settlement (no road)"; }
            if (best == null) { rule = "east (alone)"; return 0; }
            var to = centres[best];
            rule += " to " + best;
            return Math.Atan2(to[1] - centre[1], Delta(ground, centre[0], to[0]));
        }

        // An offset (forward, side) in the frame facing `angle`, turned about the centre in policy steps (0, +1, -1, ... up to half a turn),
        // then shrunk by quarters toward the centre; the first position `check` accepts (null reason). A candidate refused only for its slope
        // is kept, and the least steep of them is returned (Steep) when no candidate meets every rule.
        private static PlacedMarker Place(double[] centre, double angle, double[] offset, Func<double[], string> check, IPlacementGround ground, SettlementPlacementPolicy policy, out Dictionary<string, int> rejected)
        {
            rejected = new Dictionary<string, int>(StringComparer.Ordinal);
            var original = Offset(ground, centre, angle, offset[0], offset[1]);
            int steps = (int)Math.Round(180 / policy.TurnStepDegrees);
            PlacedMarker leastSteep = null;
            foreach (var scale in new[] { 1.0, 0.75, 0.5, 0.25 })
                for (int k = 0; k <= steps; k++)
                    foreach (int turn in k == 0 ? new[] { 0 } : new[] { k, -k })
                    {
                        if (k == steps && turn < 0) continue;
                        var q = Offset(ground, centre, angle + turn * policy.TurnStepDegrees * Math.PI / 180, offset[0] * scale, offset[1] * scale);
                        string reason = q[1] < 0 || q[1] >= ground.Height ? "beyond the poles" : check(q);
                        string rule = turn == 0 && scale == 1 ? "installed offset" : "installed offset turned " + turn + " steps, kept " + scale;
                        if (reason == "steep")
                        {
                            double slope = ground.Slope(q[0], q[1]);
                            if (leastSteep == null || slope < leastSteep.Slope - 1e-12)
                                leastSteep = new PlacedMarker { Logical = q, From = original, Turns = turn, Scale = scale, Rule = rule + ", least steep (no candidate within the slope limit)", Moved = Distance(ground, original, q), Slope = slope, Steep = true };
                        }
                        if (reason != null) { int n; rejected.TryGetValue(reason, out n); rejected[reason] = n + 1; continue; }
                        return new PlacedMarker { Logical = q, From = original, Turns = turn, Scale = scale, Rule = rule, Moved = Distance(ground, original, q), Slope = ground.Slope(q[0], q[1]) };
                    }
            return leastSteep;
        }

        // MMR-35's arc rule on the placed port, accepted only when every ship BlockadePositionScript would place is on the port's basin.
        private static void FitArc(ResolvedSettlement r, int basin, IPlacementGround ground, SettlementPlacementPolicy policy, Action<string, string, string, bool> problem)
        {
            double sx = 0, sy = 0, s = ground.Spacing;
            for (int dr = -3; dr <= 3; dr++)
                for (int dc = -3; dc <= 3; dc++)
                {
                    if (dr == 0 && dc == 0) continue;
                    double cx = r.Port[0] + dc * s, cy = r.Port[1] + dr * s;
                    if (ground.Basin(cx, cy) == basin) { double n = Math.Sqrt(dc * dc + dr * dr); sx += dc / n; sy += dr / n; }
                }
            double orientation = Math.Abs(sx) + Math.Abs(sy) > 1e-9 ? Math.Atan2(sy, sx)
                : r.PortEntrance != null ? Math.Atan2(r.Port[1] - r.PortEntrance[1], Delta(ground, r.PortEntrance[0], r.Port[0])) : 0;
            var steps = new List<int> { 0 };
            for (int k = 1; k <= 12; k++) { steps.Add(k); if (k < 12) steps.Add(-k); }
            int bestOff = -1; string bestNote = null;
            foreach (var scale in new[] { 1.0, 0.75, 0.5 })
                foreach (var shift in new[] { 0.0, 0.5, 1.0 })
                {
                    var origin = Offset(ground, r.Port, orientation, shift * s, 0);
                    if (ground.Basin(origin[0], origin[1]) != basin) continue;
                    foreach (var mirror in new[] { false, true })
                        foreach (var step in steps)
                        {
                            double a = orientation + step * Math.PI / 12, side = mirror ? -1 : 1;
                            var start = Offset(ground, origin, a, policy.ArcStart[0] * scale, -side * policy.ArcStart[1] * scale);
                            var end = Offset(ground, origin, a, policy.ArcEnd[0] * scale, -side * policy.ArcEnd[1] * scale);
                            if (mirror) { var t = start; start = end; end = t; }   // the mirrored arc keeps its handedness (FindCenterOfCircle)
                            if (ground.Basin(start[0], start[1]) != basin || ground.Basin(end[0], end[1]) != basin) continue;
                            var ships = BlockadeShips(start, end, ground, policy);
                            int off = ships.Count(q => ground.Basin(q[0], q[1]) != basin);
                            if (off > 0) { if (bestOff < 0 || off < bestOff) { bestOff = off; bestNote = off + " of " + ships.Count + " ships off the basin at the best arc"; } continue; }
                            r.ArcStart = start; r.ArcEnd = end; r.ArcTurns = step; r.ArcMirrored = mirror; r.ArcScale = scale; r.ArcShift = shift; r.ArcShips = ships;
                            r.Provenance.Add(Prov("blockadeArc", "installed arc in the seaward frame, turned " + step + " x 15 degrees" + (mirror ? ", mirrored (ends swapped)" : "") + (shift > 0 ? ", moved " + shift + " cell seaward" : "") + (scale < 1 ? ", kept " + scale : "")
                                + "; all " + ships.Count + " ships on the basin", Offset(ground, r.Port, orientation, policy.ArcStart[0], -policy.ArcStart[1]), start));
                            return;
                        }
                }
            problem("settlement.blockade_unfit", r.Site.Id, "no turn, mirror, seaward move or shrink of the installed blockade arc keeps every ship on water of basin " + basin + (bestNote == null ? "" : " (" + bestNote + ")") + ": the port gets no blockade visual", false);
        }

        // BlockadePositionScript.GetBlockadeArc (installed 1.4.8 IL, randomization off) for MaximumNumberOfShips ships: the circle's centre is
        // FindCenterOfCircle(end, start) (the chord's midpoint moved along its clockwise normal by half the chord over tan(Angle/2)); arcs at
        // radius k*R/NumberOfArcs for k = NumberOfArcs down to 1 from the direction to the end, each with round(Angle*k/DistanceBetweenShips)
        // ships (fewer when the remaining ships run out, the step widened to match), a half step first, then DistanceBetweenShips/k apart,
        // turning counterclockwise. Positions are logical.
        public static List<double[]> BlockadeShips(double[] start, double[] end, IPlacementGround ground, SettlementPlacementPolicy policy)
        {
            var ships = new List<double[]>();
            double ex = Delta(ground, start[0], end[0]), ey = end[1] - start[1];
            double v1x = ex / 2, v1y = ey / 2, d = Math.Sqrt(v1x * v1x + v1y * v1y);
            if (d < 1e-9) return ships;
            float angle = Single.Parse(policy.Blockade["Angle"], CultureInfo.InvariantCulture), step = Single.Parse(policy.Blockade["DistanceBetweenShips"], CultureInfo.InvariantCulture);
            int max = Int32.Parse(policy.Blockade["MaximumNumberOfShips"], CultureInfo.InvariantCulture), arcs = Int32.Parse(policy.Blockade["NumberOfArcs"], CultureInfo.InvariantCulture);
            double h = d / Math.Tan(angle / 2.0), cx = v1x + h * v1y / d, cy = v1y - h * v1x / d;
            double dx = ex - cx, dy = ey - cy, radius = Math.Sqrt(dx * dx + dy * dy); dx /= radius; dy /= radius;
            double ring = radius / arcs, spacing = step;
            int placed = 0;
            for (int k = arcs; k > 0 && placed < max; k--)
            {
                double vx = dx, vy = dy;
                int n = (int)Math.Round(angle * k / step);
                if (max - placed < n) { spacing = spacing * n / (max - placed); n = max - placed; }
                Rotate(ref vx, ref vy, spacing / (k * 2));
                for (int i = 0; i < n && placed < max; i++)
                {
                    double x = start[0] + cx + vx * k * ring, y = start[1] + cy + vy * k * ring;
                    ships.Add(new[] { Wrap(x, ground.Width), y });
                    Rotate(ref vx, ref vy, spacing / k);
                    placed++;
                }
            }
            return ships;
        }

        private static void Rotate(ref double x, ref double y, double a) { double c = Math.Cos(a), s = Math.Sin(a), nx = x * c - y * s; y = y * c + x * s; x = nx; }

        // (forward, side) in the frame facing `angle`: side is to the right of forward.
        private static double[] Offset(IPlacementGround ground, double[] centre, double angle, double forward, double side)
        {
            double fx = Math.Cos(angle), fy = Math.Sin(angle);
            double x = centre[0] + forward * fx + side * fy, y = centre[1] + forward * fy - side * fx;
            return new[] { Wrap(x, ground.Width), y };
        }

        // x into [0, width); a value already there is kept exactly (the modulo form rounds it).
        public static double Wrap(double x, double width) { return x >= 0 && x < width ? x : ((x % width) + width) % width; }

        // The signed x difference in (-width/2, width/2]; a difference already there is kept exactly.
        public static double Delta(IPlacementGround ground, double from, double to)
        {
            double d = to - from, w = ground.Width;
            if (d > w / 2 || d <= -w / 2) { d = ((d % w) + w) % w; if (d > w / 2) d -= w; }
            return d;
        }

        public static double Distance(IPlacementGround ground, double[] a, double[] b) { double dx = Delta(ground, a[0], b[0]), dy = b[1] - a[1]; return Math.Sqrt(dx * dx + dy * dy); }

        private static Dictionary<string, object> Prov(string field, string rule, double[] from, double[] to)
        {
            return new Dictionary<string, object> { ["field"] = field, ["rule"] = rule, ["from"] = from == null ? null : NavPoint.Round(from), ["to"] = to == null ? null : NavPoint.Round(to) };
        }

        private static string P(double[] p) { return p == null ? "-" : p[0].ToString("R", CultureInfo.InvariantCulture) + "," + p[1].ToString("R", CultureInfo.InvariantCulture); }
    }
}
