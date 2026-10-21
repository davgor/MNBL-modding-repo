using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MNBL.ProceduralWorld.World;

namespace MNBL.ProceduralWorld.Surface
{
    // MMR-289: magic features (docs/magic-features.md; map2-rev5 MMR-289). School-specific features stand in the magic zones along MMR-288's vein
    // graph: a source feature at each vein source, trunk features clustered along the wide branches and debris at forks, built from installed
    // vanilla meta meshes with multiply tints (roads.json magicFeatures; MMR-289's asset survey). Pure managed and deterministic: every choice is a
    // hash of the world's source identity and the feature's id (no unseeded Random; every loop in canonical order), in the canonical physical
    // frame; MagicFeatureInstaller draws a plan through an IMagicFeatureTarget (the engine adapter is MagicFeaturesRuntime; the offline harness
    // uses a recording one), each entity at its periodic image nearest the camera.
    //   * Roles: a source feature per vein source (the school's only light and particles); a source on water or inside a clearance (a live cast's
    //     source is the capital) moves to the first clear land node of its tree within sourceSearch edges (nearest, then widest), else it is not
    //     placed (a zone wholly on water is MMR-290's). Trunk
    //     features on non-tip land nodes at least trunkWidth wide, each trunkSpacing along the branch from the last feature, the spacing shrinking
    //     with potency and width and growing toward the zone edge (edgeThinning x Order / the zone's highest Order); each is offset across the vein
    //     (lateral x its half width, the side by hash) and faces along the edge, outward from the source. Debris at fork nodes at least debrisWidth
    //     wide. Tips get none, so the veins keep thinning (a live cast's tips do: its veins are few and young, and its trunk width is
    //     liveTrunkWidth). A source the full source feature fits nowhere takes the school's trunk features in turn, with the source's light and
    //     particles. Every feature keeps minGap from every other.
    //   * Clear of (each beyond its footprint radius): water (water travel cells, water cores, ground below the datum, shown lake planes; checked at
    //     the centre and eight points round the footprint; and the opaque core of every water decal), every settlement point (settlement
    //     clearance: the terrain-shaping pads and the ruin markers' gap lie within it), every ruin marker (ruin clearance), road decals, bridge
    //     segments and their banks.
    //   * Budget: feature roots, entities, point lights and particle systems per world and in every in-view window (quarter-window steps, columns
    //     wrapping), live features included. Live features are taken first, then the generated ones, each source first, then trunk, then debris,
    //     by priority (potency x width); past the light or particle caps a feature loses those effects, past a root or entity cap it is thinned.
    //     Live zones are planned after the generated ones, so a cast never moves a generated feature outside the zones its cells regrow (only
    //     the shared caps may pass a generated source's effects to a cast, or thin a generated feature in a full window).
    //   * Live: a cast's features follow its vein reveal (a feature shows when its anchor node is revealed).
    internal sealed class FeaturePart
    {
        public string Mesh;
        public double X, Y;                  // canonical physical
        public double Lift;                  // the entity origin above the ground at (X, Y): the mesh's lowest point on the ground, sunk
        public double[] Basis;               // side, forward, up (world axes, scale included): Mat3(side, forward, up)
        public uint Tint;                    // SetFactorColor ARGB multiply (0xFFFFFFFF: none)
        public double Height;                // map units
    }

    internal sealed class MagicFeature
    {
        public const string Source = "source", Trunk = "trunk", Debris = "debris";
        public string Id, Role, Name, School, Look;
        public int Node, Zone, Order, Index;
        public bool Live;
        public double X, Y, Yaw, Radius, Potency, Width, Priority, RevealAt = Double.NegativeInfinity;
        public readonly List<FeaturePart> Parts = new List<FeaturePart>();
        public List<string> Particles = new List<string>();
        public RoadsPolicy.FeaturesPolicy.Glow Light;
        public double EffectLift;
        public bool HasEffect { get { return Light != null || Particles.Count > 0; } }
        public int Entities { get { return Parts.Count + (HasEffect ? 1 : 0); } }
        // Everything drawn: a feature with the same key is not redrawn.
        public string Key
        {
            get
            {
                Func<double, string> f = v => v.ToString("R", CultureInfo.InvariantCulture);
                var b = new StringBuilder(Id).Append(':').Append(Name);
                foreach (var p in Parts) { b.Append('|').Append(p.Mesh).Append(',').Append(f(p.X)).Append(',').Append(f(p.Y)).Append(',').Append(f(p.Lift)).Append(',').Append(p.Tint.ToString("X8", CultureInfo.InvariantCulture)); foreach (var v in p.Basis) b.Append(',').Append(f(v)); }
                b.Append("|fx ").Append(String.Join(",", Particles)).Append(Light == null ? "" : " light");
                return b.ToString();
            }
        }
    }

    // What a feature keeps clear of: the settlement points, and of the map dressing plan the road decals, bridges, ruin markers (hover-labelled
    // prefabs, often at a vein source), water decals (MMR-285's river and fill stamps) and the lake planes it shows.
    internal sealed class MagicFeatureSites
    {
        public readonly List<double[]> Settlements = new List<double[]>();   // canonical physical
        public readonly List<RoadDecal> Roads = new List<RoadDecal>();
        public readonly List<BridgeSegment> Bridges = new List<BridgeSegment>();
        public readonly List<RuinMarker> Ruins = new List<RuinMarker>();
        public readonly List<RoadDecal> Water = new List<RoadDecal>();
        public double WaterCore = 1;                                         // the share of a water stamp's half extents that is opaque
        public readonly List<GeneratedWaterSystem.Lake> Lakes = new List<GeneratedWaterSystem.Lake>();
        public static MagicFeatureSites From(IEnumerable<double[]> settlements, MapDressingPlan dressing)
        {
            var s = new MagicFeatureSites();
            foreach (var p in settlements ?? new double[0][]) s.Settlements.Add(new[] { p[0], p[1] });
            if (dressing != null)
            {
                s.Roads.AddRange(dressing.Decals); s.Bridges.AddRange(dressing.Bridges); s.Ruins.AddRange(dressing.Markers); s.Water.AddRange(dressing.Water); s.Lakes.AddRange(dressing.Lakes);
                if (dressing.WaterCore > 0) s.WaterCore = dressing.WaterCore;
            }
            return s;
        }
    }

    internal sealed class MagicFeaturePlan
    {
        public const string Version = "mmr289/1";
        public MagicVeinGraph Graph;
        public readonly List<MagicFeature> Features = new List<MagicFeature>();
        public readonly SortedDictionary<string, int> Skipped = new SortedDictionary<string, int>(StringComparer.Ordinal), Thinned = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public int Candidates, LightsDropped, ParticlesDropped, Hidden, SourcesMoved, SourcesReduced;
        public double Hours = Double.PositiveInfinity;
        public int Roots { get { return Features.Count; } }
        public int Entities { get { return Features.Sum(f => f.Entities); } }
        public int Lights { get { return Features.Count(f => f.Light != null); } }
        public int ParticleSystems { get { return Features.Sum(f => f.Particles.Count); } }
        public int[] WindowMax = new int[4];   // roots, entities, lights, particles in the busiest in-view window

        public Dictionary<string, object> Record()
        {
            Func<Func<MagicFeature, string>, SortedDictionary<string, object>> by = key =>
            {
                var d = new SortedDictionary<string, object>(StringComparer.Ordinal);
                foreach (var g in Features.GroupBy(key).OrderBy(g => g.Key, StringComparer.Ordinal)) d[g.Key] = g.Count();
                return d;
            };
            var bySchool = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var g in Features.GroupBy(f => f.Look).OrderBy(g => g.Key, StringComparer.Ordinal))
                bySchool[g.Key] = new Dictionary<string, object> { ["source"] = g.Count(f => f.Role == MagicFeature.Source), ["trunk"] = g.Count(f => f.Role == MagicFeature.Trunk), ["debris"] = g.Count(f => f.Role == MagicFeature.Debris), ["entities"] = g.Sum(f => f.Entities) };
            return new Dictionary<string, object> { ["version"] = Version, ["roots"] = Roots, ["entities"] = Entities, ["lights"] = Lights, ["particleSystems"] = ParticleSystems, ["live"] = Features.Count(f => f.Live),
                ["byRole"] = by(f => f.Role), ["byLook"] = bySchool, ["byName"] = by(f => f.Look + "/" + f.Name), ["candidates"] = Candidates, ["skipped"] = Skipped, ["thinned"] = Thinned,
                ["lightsDropped"] = LightsDropped, ["particlesDropped"] = ParticlesDropped, ["sourcesMoved"] = SourcesMoved, ["sourcesReduced"] = SourcesReduced, ["hidden"] = Hidden,
                ["windowMax"] = new Dictionary<string, object> { ["roots"] = WindowMax[0], ["entities"] = WindowMax[1], ["lights"] = WindowMax[2], ["particles"] = WindowMax[3] } };
        }
        public string Sha256()
        {
            var b = new StringBuilder(Version + "\n");
            foreach (var f in Features) b.Append(f.Key).Append('\n');
            return Json.Sha(b.ToString());
        }
        // The features shown at a campaign time (a live cast's follow its vein reveal; generated ones are always shown).
        public MagicFeaturePlan Revealed(double hours)
        {
            var r = new MagicFeaturePlan { Graph = Graph, Candidates = Candidates, LightsDropped = LightsDropped, ParticlesDropped = ParticlesDropped, SourcesMoved = SourcesMoved, SourcesReduced = SourcesReduced, Hours = hours, WindowMax = WindowMax };
            foreach (var kv in Skipped) r.Skipped[kv.Key] = kv.Value;
            foreach (var kv in Thinned) r.Thinned[kv.Key] = kv.Value;
            foreach (var f in Features) if (f.RevealAt <= hours) r.Features.Add(f); else r.Hidden++;
            return r;
        }
        public double NextReveal(double hours)
        {
            double next = Double.PositiveInfinity;
            foreach (var f in Features) if (f.RevealAt > hours && f.RevealAt < next) next = f.RevealAt;
            return next;
        }
    }

    internal static class MagicFeatures
    {
        public const double BridgeHalfSegment = 0.6;   // half of a bridge piece along its deck (MapDressing.BridgeSegmentLocal x roads.json bridges.scale = 1.21)

        public static MagicFeaturePlan Plan(MagicVeinGraph g, RoadsPolicy.FeaturesPolicy p, MagicFeatureSites sites)
        {
            var plan = new MagicFeaturePlan { Graph = g };
            var src = g.Source;
            if (!src.Enabled || g.Nodes.Count == 0) { Count(plan.Skipped, src.Enabled ? "noVeins" : "magicOff"); return plan; }
            var check = new Clearance(src, p, sites ?? new MagicFeatureSites());
            var placed = new List<MagicFeature>();
            var near = new Bucket(p.MinGap, src.Width);
            Func<double, double> potency = v => Math.Max(0, Math.Min(1, (v - src.Threshold) / Math.Max(1e-9, 1 - src.Threshold)));
            // Generated zones first, then live ones: a cast never moves a generated feature (only the zones its cells regrow change; the budget
            // below may still take a generated feature's effects or, in a full window, the feature).
            foreach (var zone in g.Zones.OrderBy(z => z.Live ? 1 : 0).ThenBy(z => z.Index))
            {
                RoadsPolicy.FeaturesPolicy.School set;
                if (!p.Schools.TryGetValue(zone.Look, out set)) { Count(plan.Skipped, "noFeatureSet"); continue; }
                int last = zone.FirstNode + zone.NodeCount, maxOrder = 1;
                for (int k = zone.FirstNode; k < last; k++) maxOrder = Math.Max(maxOrder, g.Nodes[k].Order);
                string zoneId = (zone.Live ? "L" : "G") + zone.Key.ToString(CultureInfo.InvariantCulture) + ".";
                double wind = (Hash(src.SourceSha256, zoneId + "wind") & 4095) / 4096.0 * 2 * Math.PI;
                var anchors = new HashSet<int>();
                // Sources: at the source node, else at the first clear land node of its tree within sourceSearch edges (nearest first, then
                // the widest, then the lowest index): a source on water or inside a clearance moves out along its veins.
                foreach (int si in zone.Sources)
                {
                    // The full source feature anywhere in reach first; where it fits nowhere (a crowded or narrow site), the school's trunk features
                    // in turn, smaller, carrying the source's light and particles.
                    int origin = g.Sources[si].Node; MagicFeature f = null; string why = null; int at = origin;
                    var tree = Tree(g, origin, p.SourceSearch);
                    foreach (var def in new[] { set.Source }.Concat(set.Trunk))
                    {
                        foreach (var c in tree)
                        {
                            var node = g.Nodes[c];
                            if (node.OnWater) { why = why ?? "water"; continue; }
                            if (node.Hidden) { why = why ?? "hidden"; continue; }   // MMR-288: a generated node under a live cell (the live zone draws there)
                            string id = zoneId + (c - zone.FirstNode).ToString(CultureInfo.InvariantCulture) + "." + MagicFeature.Source;
                            var candidate = Make(src, p, set, def, MagicFeature.Source, id, node, node.X, node.Y, (Hash(src.SourceSha256, id + "yaw") & 4095) / 4096.0 * 2 * Math.PI, wind);
                            string reason = check.Why(candidate.X, candidate.Y, candidate.Radius);
                            if (reason == null && near.Any(candidate.X, candidate.Y, p.MinGap)) reason = "gap";
                            if (reason == null) { f = candidate; at = c; break; }
                            if (why == null || why == "water") why = reason;
                        }
                        if (f != null) break;
                    }
                    if (f != null && f.Name != set.Source.Name) { f.Particles = set.Source.Particles.ToList(); f.Light = set.Source.Light; plan.SourcesReduced++; }
                    plan.Candidates++;
                    if (f == null) { Count(plan.Skipped, "source:" + (why ?? "water")); continue; }
                    if (at != origin) plan.SourcesMoved++;
                    anchors.Add(at); placed.Add(f); near.Add(f.X, f.Y);
                }
                // Trunks and debris, parents before children (a child always follows its parent), by the arc length since the last feature.
                var since = new Dictionary<int, double>();
                for (int k = zone.FirstNode; k < last; k++)
                {
                    var node = g.Nodes[k];
                    // A source with no feature of its own lets its first eligible node take one.
                    double run = anchors.Contains(k) ? 0 : node.IsSource ? Double.PositiveInfinity : since[node.Parent] + EdgeLength(g, node);
                    since[k] = run;
                    // Tips get none (the veins keep thinning), except on a live cast's few young veins; a hidden node (under a live cell) none.
                    if (node.IsSource || node.OnWater || node.Hidden || (node.Children.Count == 0 && !node.Live)) continue;
                    // A live cast's veins are young and thin: its trunk features start at liveTrunkWidth.
                    double trunkWidth = node.Live ? p.LiveTrunkWidth : p.TrunkWidth;
                    double density = (0.5 + 0.5 * potency(node.Potency)) * (0.5 + 0.5 * Math.Max(0, Math.Min(1, (node.Width - trunkWidth) / Math.Max(1e-9, trunkWidth))));
                    double spacing = p.TrunkSpacing * (1 + p.EdgeThinning * node.Order / (double)maxOrder) / density;
                    string role = null;
                    if (node.Width >= trunkWidth && run >= spacing) role = MagicFeature.Trunk;
                    else if (node.Children.Count >= 2 && node.Width >= p.DebrisWidth) role = MagicFeature.Debris;
                    if (role == null) continue;
                    string id = zoneId + (k - zone.FirstNode).ToString(CultureInfo.InvariantCulture) + "." + role;
                    var defs = role == MagicFeature.Trunk ? set.Trunk : set.Debris;
                    var def = defs[(int)(Hash(src.SourceSha256, id + "kind") % (uint)defs.Count)];
                    double yaw = EdgeYaw(g, node), x = node.X, y = node.Y;
                    if (role == MagicFeature.Trunk)
                    {
                        double side = (Hash(src.SourceSha256, id + "side") & 1) == 0 ? 1 : -1, u = Unit(src, id + "lateral");
                        double off = side * (p.LateralMin + (p.LateralMax - p.LateralMin) * u) * node.Width / 2;
                        x += -Math.Sin(yaw) * off; y += Math.Cos(yaw) * off;
                    }
                    else yaw = (Hash(src.SourceSha256, id + "yaw") & 4095) / 4096.0 * 2 * Math.PI;
                    var f = Make(src, p, set, def, role, id, node, src.Canonical(x - src.Strip), y, yaw, wind);
                    plan.Candidates++;
                    string why = check.Why(f.X, f.Y, f.Radius);
                    if (why == null && near.Any(f.X, f.Y, p.MinGap)) why = "gap";
                    if (why != null) { Count(plan.Skipped, role + ":" + why); continue; }
                    placed.Add(f); near.Add(f.X, f.Y);
                    if (role == MagicFeature.Trunk) since[k] = 0;
                }
            }
            // The budget: every feature counts against every world and in-view cap. Live features first (a cast's effects take their slots
            // from the lowest-priority generated sources, and stacked casts are bounded by the same caps), then the generated ones; within
            // each, sources, then trunks, then debris, each by priority. Past the light or particle caps a feature loses those effects; past a
            // root or entity cap it is thinned.
            var windows = new Windows(src, p.WindowWidth, p.WindowHeight);
            int roots = 0, entities = 0, lights = 0, particles = 0;
            Func<MagicFeature, int> rank = f => f.Role == MagicFeature.Source ? 0 : f.Role == MagicFeature.Trunk ? 1 : 2;
            foreach (var f in placed.OrderBy(f => f.Live ? 0 : 1).ThenBy(rank).ThenByDescending(f => f.Priority).ThenBy(f => f.Id, StringComparer.Ordinal))
            {
                if (f.Light != null && (lights >= p.MaxLights || windows.Max(f.X, f.Y, 2) >= p.WindowLights)) { f.Light = null; plan.LightsDropped++; }
                if (f.Particles.Count > 0 && (particles + f.Particles.Count > p.MaxParticles || windows.Max(f.X, f.Y, 3) + f.Particles.Count > p.WindowParticles)) { plan.ParticlesDropped += f.Particles.Count; f.Particles = new List<string>(); }
                int e = f.Entities;
                if (roots >= p.MaxRoots || entities + e > p.MaxEntities || windows.Max(f.X, f.Y, 0) >= p.WindowRoots || windows.Max(f.X, f.Y, 1) + e > p.WindowEntities) { Count(plan.Thinned, (f.Live ? "live " : "") + f.Role); continue; }
                roots++; entities += e; if (f.Light != null) lights++; particles += f.Particles.Count;
                windows.Add(f.X, f.Y, new[] { 1, e, f.Light != null ? 1 : 0, f.Particles.Count });
                plan.Features.Add(f);
            }
            plan.Features.Sort((a, b) => String.CompareOrdinal(a.Id, b.Id));
            for (int i = 0; i < plan.Features.Count; i++) plan.Features[i].Index = i;
            plan.WindowMax = windows.Highest();
            return plan;
        }

        // A node's tree within depth edges, nearest first (then the widest, then the lowest index).
        internal static List<int> Tree(MagicVeinGraph g, int root, int depth)
        {
            var found = new List<KeyValuePair<int, int>> { new KeyValuePair<int, int>(root, 0) };
            for (int i = 0; i < found.Count; i++)
                if (found[i].Value < depth) foreach (int c in g.Nodes[found[i].Key].Children) found.Add(new KeyValuePair<int, int>(c, found[i].Value + 1));
            return found.OrderBy(kv => kv.Value).ThenByDescending(kv => g.Nodes[kv.Key].Width).ThenBy(kv => kv.Key).Select(kv => kv.Key).ToList();
        }

        private static double EdgeLength(MagicVeinGraph g, VeinNode n) { var parent = g.Nodes[n.Parent]; double dx = g.DeltaX(parent.X, n.X), dy = n.Y - parent.Y; return Math.Sqrt(dx * dx + dy * dy); }
        private static double EdgeYaw(MagicVeinGraph g, VeinNode n) { var parent = g.Nodes[n.Parent]; return Math.Atan2(n.Y - parent.Y, g.DeltaX(parent.X, n.X)); }

        // One feature of a definition at (x, y) facing yaw: its parts (count, mesh, scale, ring place, lean, spin and tint chosen by hash of its id).
        private static MagicFeature Make(MagicGroundSource src, RoadsPolicy.FeaturesPolicy p, RoadsPolicy.FeaturesPolicy.School set, RoadsPolicy.FeaturesPolicy.Feature def, string role, string id, VeinNode node,
            double x, double y, double yaw, double wind)
        {
            var f = new MagicFeature { Id = id, Role = role, Name = def.Name, School = node.School, Look = node.Look, Node = node.Index, Zone = node.Zone, Order = node.Order, Live = node.Live,
                X = x, Y = y, Yaw = yaw, Potency = node.Potency, Width = node.Width, Priority = node.Potency * node.Width, RevealAt = node.RevealAt,
                Particles = def.Particles.ToList(), Light = def.Light, EffectLift = p.EffectHeight };
            double radius = 0;
            for (int pi = 0; pi < def.Parts.Count; pi++)
            {
                var part = def.Parts[pi];
                string key = id + "/" + pi.ToString(CultureInfo.InvariantCulture);
                int count = part.CountMin + (int)(Hash(src.SourceSha256, key + "count") % (uint)(part.CountMax - part.CountMin + 1));
                for (int i = 0; i < count; i++)
                {
                    string k = key + "." + i.ToString(CultureInfo.InvariantCulture);
                    var mesh = p.Meshes[part.Meshes[(int)(Hash(src.SourceSha256, k + "mesh") % (uint)part.Meshes.Count)]];
                    double s = Lerp(part.ScaleMin, part.ScaleMax, Unit(src, k + "scale")), ring = Lerp(part.RingMin, part.RingMax, Unit(src, k + "ring"));
                    double theta = yaw + 2 * Math.PI * (i + 0.3 * (Unit(src, k + "angle") - 0.5)) / count;
                    double lean = Lerp(part.LeanMin, part.LeanMax, Unit(src, k + "lean")) * Math.PI / 180, spin = Unit(src, k + "spin") * 2 * Math.PI;
                    double toward = set.Lean == "wind" ? wind : ring > 0 ? theta : yaw;
                    if (set.Lean == "inward" && ring > 0) toward += Math.PI;
                    double px = x + ring * Math.Cos(theta), py = y + ring * Math.Sin(theta);
                    uint tint = part.Tints.Count == 0 ? 0xFFFFFFFFu : part.Tints[(int)(Hash(src.SourceSha256, k + "tint") % (uint)part.Tints.Count)];
                    f.Parts.Add(new FeaturePart { Mesh = mesh.Name, X = src.Canonical(px - src.Strip), Y = py, Basis = Basis(spin, mesh.Flip, toward, lean, s), Tint = tint,
                        Lift = -mesh.Low * s * Math.Cos(lean) - p.Sink * mesh.Height * s, Height = mesh.Height * s * Math.Cos(lean) });
                    radius = Math.Max(radius, ring + mesh.Radius * s);
                }
            }
            f.Radius = radius;
            return f;
        }

        // The part's axes: spun about up, flipped upside down (a turn about its side axis) when the mesh hangs, then tilted by lean toward the
        // heading `toward` (Rodrigues about the horizontal axis up x toward), then scaled.
        public static double[] Basis(double spin, bool flip, double toward, double lean, double scale)
        {
            double[] side = { Math.Cos(spin), Math.Sin(spin), 0 }, forward = { -Math.Sin(spin), Math.Cos(spin), 0 }, up = { 0, 0, 1 };
            if (flip) { forward = forward.Select(v => -v).ToArray(); up = new double[] { 0, 0, -1 }; }
            double[] k = { -Math.Sin(toward), Math.Cos(toward), 0 };
            Func<double[], double[]> turn = v =>
            {
                double c = Math.Cos(lean), s = Math.Sin(lean), dot = k[0] * v[0] + k[1] * v[1] + k[2] * v[2];
                double[] cross = { k[1] * v[2] - k[2] * v[1], k[2] * v[0] - k[0] * v[2], k[0] * v[1] - k[1] * v[0] };
                return Enumerable.Range(0, 3).Select(i => (v[i] * c + cross[i] * s + k[i] * dot * (1 - c)) * scale).ToArray();
            };
            return turn(side).Concat(turn(forward)).Concat(turn(up)).ToArray();
        }

        private static double Lerp(double a, double b, double t) { return a + (b - a) * t; }
        private static double Unit(MagicGroundSource src, string key) { return (Hash(src.SourceSha256, key) & 0xFFFFFF) / (double)0x1000000; }
        // FNV-1a of the world's source identity and a key, mixed: a stable per-feature choice.
        public static uint Hash(string seed, string key)
        {
            uint h = 2166136261;
            foreach (char ch in seed ?? "") { h ^= ch; h *= 16777619; }
            h ^= 0x2F; h *= 16777619;
            foreach (char ch in key) { h ^= ch; h *= 16777619; }
            h ^= h >> 15; h *= 0x2c1b3c6d; h ^= h >> 12; h *= 0x297a2d39; h ^= h >> 15;
            return h;
        }
        internal static void Count(IDictionary<string, int> d, string key) { int n; d.TryGetValue(key, out n); d[key] = n + 1; }

        // ---- what a feature keeps clear of ----
        internal sealed class Clearance
        {
            private readonly MagicGroundSource src; private readonly RoadsPolicy.FeaturesPolicy p; private readonly MagicFeatureSites sites;
            private readonly Bucket settlements, roads, bridges, ruins, water; private readonly double roadReach, bridgeReach, waterReach;
            public Clearance(MagicGroundSource src, RoadsPolicy.FeaturesPolicy p, MagicFeatureSites sites)
            {
                this.src = src; this.p = p; this.sites = sites;
                settlements = new Bucket(8, src.Width); for (int i = 0; i < sites.Settlements.Count; i++) settlements.Add(sites.Settlements[i][0], sites.Settlements[i][1], i);
                roads = new Bucket(4, src.Width); for (int i = 0; i < sites.Roads.Count; i++) roads.Add(sites.Roads[i].X, sites.Roads[i].Y, i);
                bridges = new Bucket(4, src.Width); for (int i = 0; i < sites.Bridges.Count; i++) bridges.Add(sites.Bridges[i].X, sites.Bridges[i].Y, i);
                ruins = new Bucket(8, src.Width); for (int i = 0; i < sites.Ruins.Count; i++) ruins.Add(sites.Ruins[i].X, sites.Ruins[i].Y, i);
                water = new Bucket(4, src.Width); for (int i = 0; i < sites.Water.Count; i++) water.Add(sites.Water[i].X, sites.Water[i].Y, i);
                roadReach = sites.Roads.Count == 0 ? 0 : sites.Roads.Max(d => d.HalfLength + d.HalfWidth);
                bridgeReach = BridgeHalfSegment;
                waterReach = sites.Water.Count == 0 ? 0 : sites.Water.Max(d => Math.Max(d.HalfLength, d.HalfWidth)) * sites.WaterCore;
            }
            // Null when a footprint of radius r at (x, y) is clear, else the first reason (water, settlement, ruin, road, bridge).
            public string Why(double x, double y, double r)
            {
                double w = r + p.WaterClearance;
                if (Wet(x, y)) return "water";
                for (int i = 0; i < 8; i++) if (Wet(x + w * Math.Cos(i * Math.PI / 4), y + w * Math.Sin(i * Math.PI / 4))) return "water";
                // A water decal's opaque core (MMR-285: opaqueCore x its half extents), grown by the footprint and the water clearance.
                foreach (int i in water.Near(x, y, w + waterReach)) if (InCore(sites.Water[i], x, y, w)) return "water";
                double reach = p.SettlementClearance + r;
                foreach (int i in settlements.Near(x, y, reach)) if (Distance(sites.Settlements[i][0], sites.Settlements[i][1], x, y) < reach) return "settlement";
                foreach (int i in ruins.Near(x, y, p.RuinClearance + r)) if (Distance(sites.Ruins[i].X, sites.Ruins[i].Y, x, y) < p.RuinClearance + r) return "ruin";
                foreach (int i in roads.Near(x, y, p.RoadClearance + r + roadReach))
                {
                    var d = sites.Roads[i];
                    if (Segment(d.X, d.Y, d.Yaw, d.HalfLength, x, y) < p.RoadClearance + r + d.HalfWidth) return "road";
                }
                foreach (int i in bridges.Near(x, y, p.BridgeClearance + r + bridgeReach))
                    if (Distance(sites.Bridges[i].X, sites.Bridges[i].Y, x, y) < p.BridgeClearance + r + bridgeReach) return "bridge";
                foreach (var b in sites.Bridges)
                    if (Distance(b.BankA[0], b.BankA[1], x, y) < p.BridgeClearance + r || Distance(b.BankB[0], b.BankB[1], x, y) < p.BridgeClearance + r) return "bridge";
                return null;
            }
            // A point on water: a water travel cell (or past a pole line), a water-core sample, ground below the datum (the sea plane shows) or
            // inside a shown lake plane.
            public bool Wet(double x, double y)
            {
                double cx = src.Canonical(x - src.Strip);
                if (MagicVeins.OnWaterCell(src, cx, y)) return true;
                int sample = src.SampleAt(cx, y), v = src.Variant[sample];
                if (v >= 0 && MagicVeins.WaterCore(src, src.Catalogue[v].Core)) return true;
                if (src.Ground != null && MapDressing.PlaneCover(sites.Lakes, cx, y, src.Ground(cx, y)) != null) return true;
                return false;
            }
            // Whether a disc of radius w at (x, y) reaches a water stamp's opaque core (the ellipse of opaqueCore x its half extents): its centre
            // inside, or a point of the ellipse's edge (64 samples) within w.
            private bool InCore(RoadDecal d, double x, double y, double w)
            {
                double dx = Dx(d.X, x), dy = y - d.Y, c = Math.Cos(d.Yaw), s = Math.Sin(d.Yaw);
                double u = dx * c + dy * s, v = -dx * s + dy * c, a = d.HalfLength * sites.WaterCore, b = d.HalfWidth * sites.WaterCore;
                if ((u / a) * (u / a) + (v / b) * (v / b) <= 1) return true;
                if (Math.Abs(u) > a + w || Math.Abs(v) > b + w) return false;
                for (int k = 0; k < 64; k++) { double eu = a * Math.Cos(k * Math.PI / 32) - u, ev = b * Math.Sin(k * Math.PI / 32) - v; if (eu * eu + ev * ev < w * w) return true; }
                return false;
            }
            public double Distance(double ax, double ay, double bx, double by) { double dx = Dx(ax, bx), dy = by - ay; return Math.Sqrt(dx * dx + dy * dy); }
            private double Dx(double a, double b) { double d = b - a; return d - Math.Round(d / src.Width) * src.Width; }
            // The distance from (x, y) to the segment centred at (cx, cy) along yaw, half length h.
            public double Segment(double cx, double cy, double yaw, double h, double x, double y)
            {
                double dx = Dx(cx, x), dy = y - cy, c = Math.Cos(yaw), s = Math.Sin(yaw);
                double t = Math.Max(-h, Math.Min(h, dx * c + dy * s));
                double ex = dx - t * c, ey = dy - t * s;
                return Math.Sqrt(ex * ex + ey * ey);
            }
        }

        // A uniform grid of points over the canonical frame, columns wrapping.
        internal sealed class Bucket
        {
            private readonly double cell, width; private readonly int columns;
            private readonly Dictionary<long, List<double[]>> cells = new Dictionary<long, List<double[]>>();
            public Bucket(double cell, double width) { this.cell = cell; this.width = width; columns = Math.Max(1, (int)Math.Ceiling(width / cell)); }
            private long Key(int gx, int gy) { gx = ((gx % columns) + columns) % columns; return ((long)gx << 32) ^ (uint)gy; }
            public void Add(double x, double y, int id = -1)
            {
                long k = Key((int)Math.Floor(x / cell), (int)Math.Floor(y / cell)); List<double[]> l;
                if (!cells.TryGetValue(k, out l)) cells[k] = l = new List<double[]>();
                l.Add(new[] { x, y, id });
            }
            public IEnumerable<int> Near(double x, double y, double r)
            {
                int x0 = (int)Math.Floor((x - r) / cell), x1 = (int)Math.Floor((x + r) / cell), y0 = (int)Math.Floor((y - r) / cell), y1 = (int)Math.Floor((y + r) / cell);
                var seen = new HashSet<long>();
                for (int gx = x0; gx <= Math.Min(x1, x0 + columns - 1); gx++)
                    for (int gy = y0; gy <= y1; gy++)
                    {
                        long k = Key(gx, gy); List<double[]> l;
                        if (!seen.Add(k) || !cells.TryGetValue(k, out l)) continue;
                        foreach (var q in l) yield return (int)q[2];
                    }
            }
            public bool Any(double x, double y, double r)
            {
                int x0 = (int)Math.Floor((x - r) / cell), x1 = (int)Math.Floor((x + r) / cell), y0 = (int)Math.Floor((y - r) / cell), y1 = (int)Math.Floor((y + r) / cell);
                for (int gx = x0; gx <= Math.Min(x1, x0 + columns - 1); gx++)
                    for (int gy = y0; gy <= y1; gy++)
                    {
                        List<double[]> l;
                        if (!cells.TryGetValue(Key(gx, gy), out l)) continue;
                        foreach (var q in l) { double dx = q[0] - x; dx -= Math.Round(dx / width) * width; double dy = q[1] - y; if (dx * dx + dy * dy < r * r) return true; }
                    }
                return false;
            }
        }

        // Counts per in-view window: windows w x h at quarter-window steps from the strip and half a window above the pole margin (columns
        // wrapping), as the harness's window maxima are taken. Four counters: roots, entities, lights, particles.
        internal sealed class Windows
        {
            private readonly MagicGroundSource src; private readonly double w, h; private readonly int nx, ny;
            private readonly Dictionary<long, int[]> counts = new Dictionary<long, int[]>();
            public Windows(MagicGroundSource src, double w, double h)
            {
                this.src = src; this.w = w; this.h = h;
                nx = (int)Math.Ceiling(src.Width / (w / 4) - 1e-9); ny = (int)Math.Ceiling((src.Height + h / 2) / (h / 4) - 1e-9);
            }
            private IEnumerable<long> Of(double x, double y)
            {
                double fx = (x - src.Strip) / (w / 4), fy = (y - (src.PoleMargin - h / 2)) / (h / 4);
                int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
                for (int i = ix - 4; i <= ix + 1; i++)
                {
                    int wi = ((i % nx) + nx) % nx;
                    double x0 = src.Strip + wi * w / 4, dx = x - x0; dx -= Math.Floor(dx / src.Width) * src.Width;
                    if (!(dx < w)) continue;
                    for (int j = iy - 4; j <= iy + 1; j++)
                    {
                        if (j < 0 || j >= ny) continue;
                        double y0 = src.PoleMargin - h / 2 + j * h / 4;
                        if (y >= y0 && y < y0 + h) yield return (long)wi * 100003 + j;
                    }
                }
            }
            public int Max(double x, double y, int counter) { int best = 0; foreach (var k in Of(x, y).Distinct()) { int[] c; if (counts.TryGetValue(k, out c)) best = Math.Max(best, c[counter]); } return best; }
            public void Add(double x, double y, int[] add)
            {
                foreach (var k in Of(x, y).Distinct())
                {
                    int[] c; if (!counts.TryGetValue(k, out c)) counts[k] = c = new int[4];
                    for (int i = 0; i < 4; i++) c[i] += add[i];
                }
            }
            public int[] Highest() { var r = new int[4]; foreach (var c in counts.Values) for (int i = 0; i < 4; i++) r[i] = Math.Max(r[i], c[i]); return r; }
        }
    }

    // What the feature installer draws on: the engine scene (MagicFeaturesRuntime) or a recording target offline. Every entity is a separate
    // scene root (never inside a baked settlement); z is the ground at the drawn point plus the part's lift.
    internal interface IMagicFeatureTarget
    {
        object AddPart(FeaturePart part, double x, double y);
        void MovePart(object handle, FeaturePart part, double x, double y);
        object AddEffect(MagicFeature feature, double x, double y);
        void MoveEffect(object handle, MagicFeature feature, double x, double y);
        void Remove(object handle);
    }

    // Draws a feature plan on a target, each entity at its periodic image (image: canonical physical x -> the drawn x), keyed by feature id: Sync
    // draws a new plan by removing the features whose key changed or went and adding the new ones, so a live change or a reveal step touches only
    // its own features. A part whose mesh does not resolve is not drawn and counted (the rest of its feature stands).
    internal sealed class MagicFeatureInstaller
    {
        private sealed class Drawn { public MagicFeature Feature; public string Key; public readonly List<KeyValuePair<object, FeaturePart>> Parts = new List<KeyValuePair<object, FeaturePart>>(); public object Effect; public double[] X; public double EffectX; }
        private readonly IMagicFeatureTarget target;
        private readonly Dictionary<string, Drawn> drawn = new Dictionary<string, Drawn>(StringComparer.Ordinal);
        public readonly List<string> Errors = new List<string>();
        public readonly SortedSet<string> MissingMeshes = new SortedSet<string>(StringComparer.Ordinal);
        public MagicFeaturePlan Plan { get; private set; }
        public int Created, Removed, Moved, NotDrawn, Dropped;
        public int Live { get { return drawn.Count; } }
        public int LiveEntities { get { return drawn.Values.Sum(d => d.Parts.Count + (d.Effect == null ? 0 : 1)); } }
        public MagicFeatureInstaller(IMagicFeatureTarget target) { this.target = target; }

        public void Sync(MagicFeaturePlan plan, Func<double, double> image)
        {
            var next = new Dictionary<string, MagicFeature>(StringComparer.Ordinal);
            foreach (var f in plan.Features) next[f.Id] = f;
            foreach (var key in drawn.Keys.ToList())
            {
                MagicFeature f;
                if (next.TryGetValue(key, out f) && f.Key == drawn[key].Key) continue;
                RemoveOne(key);
            }
            NotDrawn = 0;
            foreach (var f in plan.Features)
            {
                if (drawn.ContainsKey(f.Id)) continue;
                var d = new Drawn { Feature = f, Key = f.Key, X = new double[f.Parts.Count] };
                for (int i = 0; i < f.Parts.Count; i++)
                {
                    var part = f.Parts[i];
                    if (MissingMeshes.Contains(part.Mesh)) { NotDrawn++; continue; }
                    double x = image(part.X); d.X[i] = x;
                    try
                    {
                        var handle = target.AddPart(part, x, part.Y);
                        if (handle == null) { MissingMeshes.Add(part.Mesh); NotDrawn++; continue; }
                        d.Parts.Add(new KeyValuePair<object, FeaturePart>(handle, part)); Created++;
                    }
                    catch (Exception e) { NotDrawn++; if (Errors.Count < 20) Errors.Add("create " + f.Id + "/" + i + ": " + e.GetType().Name + ": " + e.Message); }
                }
                if (f.HasEffect)
                {
                    d.EffectX = image(f.X);
                    try { d.Effect = target.AddEffect(f, d.EffectX, f.Y); if (d.Effect != null) Created++; }
                    catch (Exception e) { if (Errors.Count < 20) Errors.Add("effect " + f.Id + ": " + e.GetType().Name + ": " + e.Message); }
                }
                drawn[f.Id] = d;
            }
            Plan = plan;
        }

        public void Refresh(Func<double, double> image)
        {
            foreach (var d in drawn.Values)
            {
                foreach (var kv in d.Parts)
                {
                    int i = d.Feature.Parts.IndexOf(kv.Value);
                    double x = image(kv.Value.X);
                    if (Math.Abs(x - d.X[i]) < 1e-6) continue;
                    try { target.MovePart(kv.Key, kv.Value, x, kv.Value.Y); d.X[i] = x; Moved++; }
                    catch (Exception e) { if (Errors.Count < 20) Errors.Add("move " + d.Feature.Id + ": " + e.GetType().Name + ": " + e.Message); }
                }
                if (d.Effect == null) continue;
                double ex = image(d.Feature.X);
                if (Math.Abs(ex - d.EffectX) < 1e-6) continue;
                try { target.MoveEffect(d.Effect, d.Feature, ex, d.Feature.Y); d.EffectX = ex; Moved++; }
                catch (Exception e) { if (Errors.Count < 20) Errors.Add("move effect " + d.Feature.Id + ": " + e.GetType().Name + ": " + e.Message); }
            }
        }

        // On a live scene. The feature leaves the table first, so no handle is removed twice.
        private void RemoveOne(string key)
        {
            var d = drawn[key];
            drawn.Remove(key);
            foreach (var h in d.Parts.Select(kv => kv.Key).Concat(d.Effect == null ? new object[0] : new[] { d.Effect }))
            {
                try { target.Remove(h); }
                catch (Exception e) { if (Errors.Count < 20) Errors.Add("remove " + key + ": " + e.GetType().Name + ": " + e.Message); }
                Removed++;
            }
        }

        public void Remove() { foreach (var key in drawn.Keys.ToList()) RemoveOne(key); }
        // MMR-262: lets go of every handle with no target call, for a scene the engine already cleared (MapScreen.OnFinalize's Scene.ClearAll).
        public int Drop() { int n = LiveEntities; drawn.Clear(); Dropped += n; return n; }

        public IEnumerable<KeyValuePair<MagicFeature, int>> DrawnFeatures() { foreach (var d in drawn.Values) yield return new KeyValuePair<MagicFeature, int>(d.Feature, d.Parts.Count + (d.Effect == null ? 0 : 1)); }

        public Dictionary<string, object> Record()
        {
            var r = Plan == null ? new Dictionary<string, object>() : Plan.Record();
            r["installed"] = new Dictionary<string, object> { ["created"] = Created, ["features"] = drawn.Count, ["entities"] = LiveEntities, ["notDrawn"] = NotDrawn, ["moved"] = Moved, ["removed"] = Removed,
                ["dropped"] = Dropped, ["missingMeshes"] = MissingMeshes.ToList(), ["errors"] = Errors.ToList() };
            return r;
        }
    }
}
