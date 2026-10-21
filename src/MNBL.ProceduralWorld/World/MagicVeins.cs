using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MNBL.ProceduralWorld.World;

namespace MNBL.ProceduralWorld.Surface
{
    // MMR-288: the vein graph of the magic ground (docs/magic-ground.md "Veins"; map2-rev5 contract C2). Pure managed and deterministic (no
    // unseeded choice; every loop runs in canonical order), built from a MagicSnapshot (generated variants and live cells) in the canonical
    // physical frame; MagicGround.Plan draws its land stamps, MMR-289 places features along it and MMR-290 draws its water stamps.
    //   * Zones: 8-connected source samples (pole rows excluded, columns wrapping) with one look school (a hidden school takes its look) and one
    //     origin. Generated zones are built from the generated variants alone, so their graph is fixed per world: a live cell over a generated
    //     node hides that node (VeinNode.Hidden; its stamps are not made) and the rest of the generated veins keep their stamps and ids. Live
    //     zones are the live cells' own. Water samples (ocean and lake cores, samples on water travel cells) belong to zones too: their graph
    //     nodes carry OnWater, so a vein crosses the shore.
    //   * Sources: a generated zone's potency maxima (its highest sample, then every local maximum at least sourceSpacing from the chosen ones);
    //     a live zone's cast nodes (MagicNode: the capital) of its school, at the node itself when the node lies within spacing of the zone,
    //     else at the zone sample nearest the node, its Order then starting at that distance in growth steps.
    //   * Growth: space colonisation. Every zone sample is an attraction point weighted by its potency; each round, every point pulls its nearest
    //     vein node within 3 x spacing, and each pulled node grows one step (0.75 x stampLength) toward the weighted mean direction (toward its
    //     nearest point when the pulls cancel), so branches run out from the sources down the potency gradient and fork where the points split.
    //     Smoothing: a node's first child keeps the parent edge's heading (Momentum) and turns at most MaxTurn; a later child (a branch) leaves
    //     at most MaxBranch off it; a step that cannot be placed grows nothing.
    //     A step must stay in the zone (its nearest sample is a zone sample) and keep 0.45 steps from every node; a point within spacing / 2 of
    //     a node is reached. Growth stops when no node grows.
    //   * Width: the smaller of a pipe model in which every node adds its own flow (s^g = minWidth^g + sum of its children's s^g, g =
    //     forkExponent: a vein thickens with the veins it feeds, an unbranched one too) and a taper by the node's place along its longest path
    //     (minWidth + (width - minWidth) x h / (h + d), h the node's height above its farthest tip, d its depth below its source), clamped to
    //     [minWidth, width], and an edge cap minWidth + (width - minWidth) x Edge(the node's depth inside its zone in samples, edgeSamples);
    //     then no child is wider than its parent. Veins so thin from the source to the zone edge, not only at forks.
    //   * Alpha: the smaller of a potency fade (minAlpha at the mutation threshold, smoothstep to 1 over fadeBand above it) and the edge fade
    //     minAlpha + (1 - minAlpha) x Edge(depth), sources 1, so veins fade toward the zone edge; MagicGround draws it through the decal's
    //     SetFactor1 alpha.
    //   * Stamps (contract C1 columns): a burst at each source (box burstScale x the source's width square, one ray along its widest child), a
    //     trunk centred on each edge into a node with children (box stampLength x the child's width, yaw along the edge), a tip on each edge into
    //     a tip node (its end, u 0.85, at the tip; width of the edge's parent), a fork where it fits (ForkFits: its split, u 0.4, at the node, yaw
    //     the bisector of its two widest children, length fitted to their angle; where it does not fit, the trunks meet at the node on their own,
    //     since a fork whose prongs miss the children reads as a star). No other stamp exists. MagicGround.Plan keeps only the drawn components
    //     that reach a burst (or the water veins) and hold at least minStamps stamps, so nothing is an isolated splotch.
    //   * Reveal (live): a cast's veins appear outward by Order: a node of Order k is revealed at Since + revealHours x k / (the cast's highest
    //     Order); Since is the cast node's batch day in campaign hours (MMR-261's edit log). Generated veins are always revealed.
    internal sealed class VeinZone
    {
        public int Index, Key;               // Key: the zone's lowest sample index (stable while its samples do not change)
        public string Look; public bool Live;
        public int[] Samples;                // ascending
        public int FirstNode, NodeCount;
        public readonly List<int> Sources = new List<int>();   // indices into MagicVeinGraph.Sources
        public double MaxPotency;
    }

    internal sealed class VeinSource
    {
        public int Node, Zone;
        public MagicNode Cast;               // the live cast this source grows from (null: a generated potency maximum)
        public double Since = Double.NaN;    // campaign hours the cast's growth began (NaN: fully grown)
        public int OrderOffset;              // a live zone away from its cast node: the distance in growth steps
    }

    internal sealed class VeinNode
    {
        public int Index, Zone, Source, Parent = -1, Sample, Variant, Order, CoreRow = -1;
        public double X, Y;                  // canonical physical
        public double Width, Potency, RevealAt = Double.NegativeInfinity, Alpha = 1;
        public string School, Look, Core;
        public bool OnWater, Live;
        public bool Hidden;                  // a generated node under a live cell: no stamp, no feature (the live zone draws there)
        public int EdgeDepth = 1;            // its sample's depth inside the zone: 8-connected steps from the nearest sample outside it (1 on the rim)
        public readonly List<int> Children = new List<int>();
        public bool IsSource { get { return Parent < 0; } }
    }

    internal sealed class VeinEdge { public int Parent, Child; public double Length, Yaw; }

    internal static class VeinKind
    {
        public const string Trunk = "trunk", Fork = "fork", Tip = "tip", Burst = "burst";
        public static readonly string[] All = { Trunk, Fork, Tip, Burst };
    }

    // One stamp of the graph (atlas cell row by the owner node's core, column by kind). OnWater: its owner node or its centre is on water (a
    // water travel cell or water-core sample, or, MMR-247, a centre a shown water plane covers: ShowsWater) (MMR-290 draws those; MagicGround.Plan
    // draws the rest).
    internal sealed class VeinStamp
    {
        public string Id, Kind;
        public int Node, Zone, Edge = -1, AtlasRow = -1, AtlasColumn;
        public double X, Y, Yaw, Length, Width, RevealAt = Double.NegativeInfinity;   // centre canonical physical; Yaw along the growth (+u); L along, W across
        public double Alpha = 1;             // the owner node's fade (SetFactor1 alpha)
        public bool OnWater, Live;
    }

    internal sealed class MagicVeinGraph
    {
        public const string Version = "mmr288/2";
        public readonly MagicGroundSource Source;
        public readonly List<VeinZone> Zones = new List<VeinZone>();
        public readonly List<VeinSource> Sources = new List<VeinSource>();
        public readonly List<VeinNode> Nodes = new List<VeinNode>();
        public readonly List<VeinEdge> Edges = new List<VeinEdge>();
        public readonly List<VeinStamp> Stamps = new List<VeinStamp>();
        public readonly SortedDictionary<string, int> Skipped = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public int MutatedSamples;
        public MagicVeinGraph(MagicGroundSource source) { Source = source; }

        // The shortest x offset from a to b on the period (canonical physical x).
        public double DeltaX(double a, double b) { double d = b - a; return d - Math.Round(d / Source.Width) * Source.Width; }
        // Edges whose two nodes lie on different sides of the shore (MMR-290 continues the land veins there).
        public IEnumerable<VeinEdge> ShoreEdges() { return Edges.Where(e => Nodes[e.Parent].OnWater != Nodes[e.Child].OnWater); }
        // Edges at least minWidth wide at their child (the trunks MMR-289 clusters features along).
        public IEnumerable<VeinEdge> Trunks(double minWidth) { return Edges.Where(e => Nodes[e.Child].Width >= minWidth); }
        public bool Revealed(VeinNode n, double hours) { return n.RevealAt <= hours; }
        // Branches: the chains of edges starting at a source or a fork.
        public int Branches { get { return Edges.Count(e => Nodes[e.Parent].IsSource || Nodes[e.Parent].Children.Count >= 2); } }

        public Dictionary<string, object> Record()
        {
            Func<bool, Dictionary<string, object>> kinds = water => VeinKind.All.ToDictionary(k => k, k => (object)Stamps.Count(s => s.Kind == k && s.OnWater == water));
            return new Dictionary<string, object> { ["version"] = Version, ["zones"] = Zones.Count, ["liveZones"] = Zones.Count(z => z.Live), ["sources"] = Sources.Count,
                ["liveSources"] = Sources.Count(s => s.Cast != null), ["branches"] = Branches, ["nodes"] = new Dictionary<string, object> { ["land"] = Nodes.Count(n => !n.OnWater), ["water"] = Nodes.Count(n => n.OnWater), ["hidden"] = Nodes.Count(n => n.Hidden) },
                ["edges"] = Edges.Count, ["shoreEdges"] = ShoreEdges().Count(), ["maxOrder"] = Nodes.Count == 0 ? 0 : Nodes.Max(n => n.Order),
                ["stamps"] = new Dictionary<string, object> { ["land"] = kinds(false), ["water"] = kinds(true) }, ["mutatedSamples"] = MutatedSamples, ["skipped"] = Skipped };
        }

        public string Sha256()
        {
            var b = new StringBuilder(Version + "\n");
            Func<double, string> f = v => v.ToString("R", CultureInfo.InvariantCulture);
            foreach (var n in Nodes) b.Append(n.Zone).Append(' ').Append(n.Parent).Append(' ').Append(f(n.X)).Append(' ').Append(f(n.Y)).Append(' ').Append(f(n.Width)).Append(' ').Append(f(n.Alpha)).Append(' ').Append(n.Order).Append(n.OnWater ? " w" : " l").Append(n.Hidden ? " h" : "").Append('\n');
            foreach (var s in Stamps) b.Append(s.Id).Append(' ').Append(f(s.X)).Append(' ').Append(f(s.Y)).Append(' ').Append(f(s.Yaw)).Append(' ').Append(f(s.Length)).Append(' ').Append(f(s.Width)).Append(' ').Append(f(s.RevealAt)).Append('\n');
            return Json.Sha(b.ToString());
        }
    }

    internal static class MagicVeins
    {
        public const double StepShare = 0.75, TipEnd = 0.85, ForkSplit = 0.4, InfluenceSpacings = 3, MinGapSteps = 0.45;
        // Growth smoothing (the first child keeps Momentum of the parent's heading and turns at most MaxTurn; a branch leaves at most MaxBranch
        // off it) and the fork fit (the art's children leave at dv/du +-ForkSlope, so a fork box of width W and length L opens at
        // +-atan(ForkSlope W / L): L is fitted to the two children's half angle, and a node whose children open wider than MaxForkHalf, or whose
        // parent edge comes in more than MaxForkHalf off the bisector, gets no fork stamp: its trunks and tips meet there on their own).
        public const double Momentum = 1.0, MaxTurn = Math.PI / 4, MaxBranch = Math.PI * 5 / 12, ForkSlope = 0.4, MaxForkHalf = Math.PI * 2 / 9;

        public static MagicVeinGraph Build(MagicSnapshot s, RoadsPolicy.MagicPolicy p)
        {
            var src = s.Source;
            var g = new MagicVeinGraph(src);
            if (!src.Enabled) { Count(g.Skipped, "magicOff"); return g; }
            var looks = src.Table.HiddenLooks;
            int side = src.Side, n1 = src.Columns;
            double cellW = src.Width / n1, cellH = src.Height / n1;
            // ---- zones: the generated ones from the generated variants alone (fixed per world), then the live cells' own ----
            for (int r = 0; r < side; r++) for (int c = 0; c < n1; c++) if (s.VariantAt(src.Index(r, c)) >= 0) g.MutatedSamples++;
            var genZone = Enumerable.Repeat(-1, side * side).ToArray(); var liveZone = Enumerable.Repeat(-1, side * side).ToArray();
            var genUx = new double[side * side]; var liveUx = new double[side * side];   // unwrapped logical x of each zone sample (consistent along its zone)
            Func<int, int> genVariant = i => src.Variant[i], liveVariant = i => s.IsLive(i) ? s.VariantAt(i) : -1;
            Func<int, double> genPotency = i => src.Potency[i], livePotency = i => s.PotencyAt(i);
            foreach (bool live in new[] { false, true })
            {
                var zoneOf = live ? liveZone : genZone; var ux = live ? liveUx : genUx; var variantOf = live ? liveVariant : genVariant; var potencyOf = live ? livePotency : genPotency;
                var lookOf = new string[side * side];
                for (int r = 0; r < side; r++)
                    for (int c = 0; c < n1; c++)
                    {
                        int i = src.Index(r, c), v = variantOf(i);
                        if (v < 0) continue;
                        if (src.PoleRow(r)) { Count(g.Skipped, "poleRow"); continue; }
                        string look = MagicGround.Look(looks, src.Catalogue[v].School);
                        if (!p.Schools.Contains(look)) { Count(g.Skipped, "noLook"); continue; }
                        lookOf[i] = look;
                    }
                for (int r = 1; r < n1; r++)
                    for (int c = 0; c < n1; c++)
                    {
                        int start = src.Index(r, c);
                        if (lookOf[start] == null || zoneOf[start] >= 0) continue;
                        var zone = new VeinZone { Index = g.Zones.Count, Look = lookOf[start], Live = live };
                        var members = new List<int>();
                        var queue = new Queue<int>(); queue.Enqueue(start); zoneOf[start] = zone.Index; ux[start] = src.Logical(r, c)[0];
                        while (queue.Count > 0)
                        {
                            int j = queue.Dequeue(), jr = j / side, jc = j % side; members.Add(j);
                            for (int dr = -1; dr <= 1; dr++)
                                for (int dc = -1; dc <= 1; dc++)
                                {
                                    if ((dr == 0 && dc == 0) || src.PoleRow(jr + dr)) continue;
                                    int k = src.Index(jr + dr, jc + dc);
                                    if (zoneOf[k] >= 0 || lookOf[k] != zone.Look) continue;
                                    zoneOf[k] = zone.Index; ux[k] = ux[j] + dc * cellW; queue.Enqueue(k);
                                }
                        }
                        zone.Samples = members.OrderBy(x => x).ToArray();
                        zone.Key = zone.Samples[0];
                        zone.MaxPotency = zone.Samples.Max(potencyOf);
                        g.Zones.Add(zone);
                    }
            }
            // ---- growth, zone by zone ----
            double step = StepShare * p.StampLength, kill = p.Spacing / 2, influence = InfluenceSpacings * p.Spacing, minGap = MinGapSteps * step;
            Func<int, double> uy = i => cellH * (i / side);
            foreach (var zone in g.Zones)
            {
                var zoneOf = zone.Live ? liveZone : genZone; var ux = zone.Live ? liveUx : genUx;
                Func<int, double> potencyAt = zone.Live ? livePotency : genPotency; Func<int, int> variantAt = zone.Live ? liveVariant : genVariant;
                zone.FirstNode = g.Nodes.Count;
                var lx = new List<double>(); var ly = new List<double>();   // zone-local unwrapped positions of its nodes
                Action<double, double, int, int, int, int> add = (x, y, parent, source, sample, order) =>
                {
                    var node = new VeinNode { Index = g.Nodes.Count, Zone = zone.Index, Source = source, Parent = parent, Sample = sample, Order = order, Live = zone.Live };
                    g.Nodes.Add(node); lx.Add(x); ly.Add(y);
                    if (parent >= 0) g.Nodes[parent].Children.Add(node.Index);
                };
                // Sources.
                var points = zone.Samples;
                if (zone.Live && s.Nodes.Any(m => MagicGround.Look(looks, m.School) == zone.Look))
                {
                    var casts = new List<KeyValuePair<MagicNode, double[]>>();   // node, {x, y, nearest sample, distance}
                    foreach (var m in s.Nodes.Where(m => MagicGround.Look(looks, m.School) == zone.Look))
                    {
                        double mx = m.X - src.Strip, my = m.Y - src.PoleMargin, best = Double.PositiveInfinity, bx = 0; int at = -1;
                        foreach (int i in points)
                        {
                            double dx = mx - ux[i]; dx -= Math.Round(dx / src.Width) * src.Width;
                            double d = Math.Sqrt(dx * dx + (my - uy(i)) * (my - uy(i)));
                            if (d < best) { best = d; at = i; bx = ux[i] + dx; }
                        }
                        casts.Add(new KeyValuePair<MagicNode, double[]>(m, new[] { bx, my, at, best }));
                    }
                    var near = casts.Where(kv => kv.Value[3] <= p.SourceSpacing).ToList();
                    if (near.Count == 0) near = casts.OrderBy(kv => kv.Value[3]).Take(1).ToList();
                    foreach (var kv in near)
                    {
                        int sample = (int)kv.Value[2]; bool inside = kv.Value[3] <= p.Spacing;
                        var source = new VeinSource { Node = g.Nodes.Count, Zone = zone.Index, Cast = kv.Key, Since = kv.Key.Since, OrderOffset = inside ? 0 : (int)Math.Round(kv.Value[3] / step) };
                        zone.Sources.Add(g.Sources.Count); g.Sources.Add(source);
                        add(inside ? kv.Value[0] : ux[sample], inside ? kv.Value[1] : uy(sample), -1, zone.Sources[zone.Sources.Count - 1], sample, source.OrderOffset);
                    }
                }
                else
                {
                    var chosen = new List<int>();
                    foreach (int i in points.OrderByDescending(x => potencyAt(x)).ThenBy(x => x))
                    {
                        bool localMax = true;
                        int ir = i / side, ic = i % side;
                        for (int dr = -1; dr <= 1 && localMax; dr++) for (int dc = -1; dc <= 1; dc++)
                        {
                            if ((dr == 0 && dc == 0) || src.PoleRow(ir + dr)) continue;
                            int k = src.Index(ir + dr, ic + dc);
                            if (zoneOf[k] == zone.Index && potencyAt(k) > potencyAt(i)) { localMax = false; break; }
                        }
                        if (chosen.Count > 0 && (!localMax || chosen.Any(j => Distance(ux[i], uy(i), ux[j], uy(j)) < p.SourceSpacing))) continue;
                        chosen.Add(i);
                        zone.Sources.Add(g.Sources.Count); g.Sources.Add(new VeinSource { Node = g.Nodes.Count, Zone = zone.Index });
                        add(ux[i], uy(i), -1, zone.Sources[zone.Sources.Count - 1], i, 0);
                    }
                }
                // Attraction points: the zone's samples, weighted by potency; the ones a source reaches are gone.
                int count = points.Length;
                var ax = points.Select(i => ux[i]).ToArray(); var ay = points.Select(i => uy(i)).ToArray();
                var aw = points.Select(i => 0.5 + 0.5 * Math.Max(0, Math.Min(1, (potencyAt(i) - src.Threshold) / Math.Max(1e-9, 1 - src.Threshold)))).ToArray();
                var alive = Enumerable.Repeat(true, count).ToArray();
                var pointGrid = new Grid(kill); for (int a = 0; a < count; a++) pointGrid.Add(a, ax[a], ay[a]);
                var nodeGrid = new Grid(influence); var closeGrid = new Grid(minGap);
                Action<int> reach = k => { foreach (int a in pointGrid.Near(lx[k], ly[k], kill)) if (alive[a] && Distance(ax[a], ay[a], lx[k], ly[k]) <= kill) alive[a] = false; };
                for (int k = 0; k < lx.Count; k++) { nodeGrid.Add(k, lx[k], ly[k]); closeGrid.Add(k, lx[k], ly[k]); reach(k); }
                int cap = 4 * count + 16;
                Func<double, double, bool> inside2 = (x, y) =>
                {
                    if (y <= 0 || y >= src.Height) return false;
                    int sample = src.SampleAt(src.Canonical(x), y + src.PoleMargin);
                    return !src.PoleRow(sample / side) && zoneOf[sample] == zone.Index;
                };
                while (lx.Count < cap)
                {
                    int nodes = lx.Count;
                    var sx = new double[nodes]; var sy = new double[nodes]; var sw = new double[nodes]; var nearest = Enumerable.Repeat(-1, nodes).ToArray(); var nearestD = new double[nodes];
                    bool pulled = false;
                    for (int a = 0; a < count; a++)
                    {
                        if (!alive[a]) continue;
                        int best = -1; double bd = Double.PositiveInfinity;
                        foreach (int k in nodeGrid.Near(ax[a], ay[a], influence))
                        {
                            double d = Distance(ax[a], ay[a], lx[k], ly[k]);
                            if (d <= influence && (d < bd || (d == bd && k < best))) { best = k; bd = d; }
                        }
                        if (best < 0) continue;
                        if (bd < 1e-9) { alive[a] = false; continue; }
                        sx[best] += (ax[a] - lx[best]) / bd * aw[a]; sy[best] += (ay[a] - ly[best]) / bd * aw[a]; sw[best] += aw[a];
                        if (nearest[best] < 0 || bd < nearestD[best]) { nearest[best] = a; nearestD[best] = bd; }
                        pulled = true;
                    }
                    if (!pulled) break;
                    bool grew = false;
                    for (int k = 0; k < nodes && lx.Count < cap; k++)
                    {
                        if (sw[k] <= 0) continue;
                        double dx = sx[k], dy = sy[k], len = Math.Sqrt(dx * dx + dy * dy);
                        if (len < 0.2 * sw[k]) { int a = nearest[k]; dx = ax[a] - lx[k]; dy = ay[a] - ly[k]; len = Math.Sqrt(dx * dx + dy * dy); }
                        if (len < 1e-12) continue;
                        dx /= len; dy /= len;
                        // Smooth veins: a node's first child keeps some of the parent edge's heading and turns at most MaxTurn from it; a later
                        // child (a branch) leaves at most MaxBranch from it. A source grows along the raw pull. A blocked step grows nothing (a raw
                        // fallback brings the zigzags back; the zone's other nodes still reach its points: coverage A' 99.4%, B' 99.7%).
                        var self = g.Nodes[zone.FirstNode + k];
                        double cx, cy;
                        if (self.Parent >= 0)
                        {
                            int pk = self.Parent - zone.FirstNode;
                            double px = lx[k] - lx[pk], py = ly[k] - ly[pk], pl = Math.Sqrt(px * px + py * py);
                            px /= pl; py /= pl;
                            double hx = dx, hy = dy;
                            if (self.Children.Count == 0) { hx += Momentum * px; hy += Momentum * py; double hl = Math.Sqrt(hx * hx + hy * hy); if (hl > 1e-12) { hx /= hl; hy /= hl; } else { hx = px; hy = py; } }
                            double limit = self.Children.Count == 0 ? MaxTurn : MaxBranch, turn = Math.Atan2(px * hy - py * hx, px * hx + py * hy);
                            if (Math.Abs(turn) > limit) { double t = Math.Atan2(py, px) + Math.Sign(turn) * limit; hx = Math.Cos(t); hy = Math.Sin(t); }
                            double tx = lx[k] + step * hx, ty = ly[k] + step * hy;
                            if (!Placeable(tx, ty, inside2, closeGrid, lx, ly, minGap)) continue;
                            cx = tx; cy = ty;
                        }
                        else
                        {
                            double tx = lx[k] + step * dx, ty = ly[k] + step * dy;
                            if (!Placeable(tx, ty, inside2, closeGrid, lx, ly, minGap)) continue;
                            cx = tx; cy = ty;
                        }
                        int sample = src.SampleAt(src.Canonical(cx), cy + src.PoleMargin);
                        var parent = g.Nodes[zone.FirstNode + k];
                        add(cx, cy, parent.Index, parent.Source, sample, parent.Order + 1);
                        int added = lx.Count - 1;
                        nodeGrid.Add(added, cx, cy); closeGrid.Add(added, cx, cy); reach(added);
                        grew = true;
                    }
                    if (!grew) break;
                }
                zone.NodeCount = g.Nodes.Count - zone.FirstNode;
                // Widths, children before parents (a child is always added after its parent): the pipe with each node's own flow, capped by the
                // taper along the node's longest path (height h above its farthest tip, depth d below its source) and by the edge cap (a node near
                // the zone's rim is thin); then no child is wider than its parent.
                var inner = Depths(src, zone, zoneOf);
                var flow = new double[zone.NodeCount]; var height = new int[zone.NodeCount];
                for (int k = g.Nodes.Count - 1; k >= zone.FirstNode; k--)
                {
                    var node = g.Nodes[k]; int local = k - zone.FirstNode;
                    double sum = Math.Pow(p.MinWidth, p.ForkExponent);
                    foreach (int c in node.Children) { sum += Math.Pow(flow[c - zone.FirstNode], p.ForkExponent); height[local] = Math.Max(height[local], height[c - zone.FirstNode] + 1); }
                    flow[local] = Math.Pow(sum, 1 / p.ForkExponent);
                    int depth = node.Order - g.Nodes[g.Sources[node.Source].Node].Order;
                    double t = height[local] + depth == 0 ? 1 : (double)height[local] / (height[local] + depth);
                    int edge; node.EdgeDepth = inner.TryGetValue(node.Sample, out edge) ? edge : 1;
                    double e = node.IsSource ? 1 : Edge(node.EdgeDepth, p);
                    node.Width = Math.Max(p.MinWidth, Math.Min(p.Width, Math.Min(flow[local], p.MinWidth + (p.Width - p.MinWidth) * Math.Min(t, e))));
                }
                for (int k = zone.FirstNode; k < g.Nodes.Count; k++) { var node = g.Nodes[k]; if (node.Parent >= 0) node.Width = Math.Min(node.Width, g.Nodes[node.Parent].Width); }
                // The canonical frame and each node's sample, variant and shore side.
                for (int k = zone.FirstNode; k < g.Nodes.Count; k++)
                {
                    var node = g.Nodes[k];
                    node.X = src.Canonical(lx[k - zone.FirstNode]); node.Y = ly[k - zone.FirstNode] + src.PoleMargin;
                    node.Variant = variantAt(node.Sample); node.Potency = potencyAt(node.Sample); node.Hidden = !zone.Live && s.IsLive(node.Sample);
                    node.Alpha = Math.Min(Fade(node.Potency, src.Threshold, p), node.IsSource ? 1 : p.MinAlpha + (1 - p.MinAlpha) * Edge(node.EdgeDepth, p));
                    var variant = src.Catalogue[node.Variant];
                    node.School = variant.School; node.Look = zone.Look; node.Core = variant.Core; node.CoreRow = p.RowOf(variant.Core);
                    node.OnWater = WaterCore(src, variant.Core) || OnWaterCell(src, node.X, node.Y);
                    if (node.Parent >= 0)
                    {
                        var parent = g.Nodes[node.Parent];
                        double dx = g.DeltaX(parent.X, node.X), dy = node.Y - parent.Y;
                        g.Edges.Add(new VeinEdge { Parent = parent.Index, Child = node.Index, Length = Math.Sqrt(dx * dx + dy * dy), Yaw = Math.Atan2(dy, dx) });
                    }
                }
            }
            // ---- reveal (live): outward by Order over revealHours, per cast ----
            var highest = new Dictionary<MagicNode, int>();
            foreach (var node in g.Nodes)
            {
                var cast = g.Sources[node.Source].Cast;
                if (cast == null) continue;
                int h; highest.TryGetValue(cast, out h); highest[cast] = Math.Max(h, node.Order);
            }
            foreach (var node in g.Nodes)
            {
                var source = g.Sources[node.Source];
                if (source.Cast == null || Double.IsNaN(source.Since)) continue;
                node.RevealAt = source.Since + p.RevealHours * node.Order / Math.Max(1, highest[source.Cast]);
            }
            // ---- stamps ----
            var edgeOf = new Dictionary<int, int>();
            for (int e = 0; e < g.Edges.Count; e++) edgeOf[g.Edges[e].Child] = e;
            foreach (var zone in g.Zones)
                for (int k = zone.FirstNode; k < zone.FirstNode + zone.NodeCount; k++)
                {
                    var node = g.Nodes[k];
                    if (node.Hidden) continue;   // a live cell covers it: the live zone draws there
                    string id = (zone.Live ? "L" : "G") + zone.Key.ToString(CultureInfo.InvariantCulture) + "." + (k - zone.FirstNode).ToString(CultureInfo.InvariantCulture) + ".";
                    if (node.IsSource)
                    {
                        double size = p.BurstScale * node.Width;
                        // One ray (the art's u axis) along the widest child, so the burst's rays lead into the source's veins.
                        double yaw = node.Children.Count > 0 ? g.Edges[edgeOf[node.Children.OrderByDescending(ch => g.Nodes[ch].Width).ThenBy(ch => ch).First()]].Yaw
                            : (MagicGround.Hash(src.SourceSha256, node.Sample + 0x30000000) & 4095) / 4096.0 * 2 * Math.PI;
                        Stamp(g, p, node, -1, id + VeinKind.Burst, VeinKind.Burst, p.BurstColumn, node.X, node.Y, yaw, size, size);
                    }
                    else
                    {
                        var e = g.Edges[edgeOf[k]]; var parent = g.Nodes[e.Parent];
                        double c = Math.Cos(e.Yaw), sn = Math.Sin(e.Yaw);
                        if (node.Children.Count > 0)
                            Stamp(g, p, node, edgeOf[k], id + VeinKind.Trunk, VeinKind.Trunk, p.TrunkColumn, Wrap(src, parent.X + c * e.Length / 2), parent.Y + sn * e.Length / 2, e.Yaw, p.StampLength, node.Width);
                        else
                        {
                            double back = (TipEnd - 0.5) * p.StampLength;   // the tip's end (u 0.85) at the tip node
                            Stamp(g, p, node, edgeOf[k], id + VeinKind.Tip, VeinKind.Tip, p.TipColumn, Wrap(src, node.X - c * back), node.Y - sn * back, e.Yaw, p.StampLength, parent.Width);
                        }
                    }
                    double forkLength;
                    if (ForkFits(g, node, edgeOf, p, out forkLength))
                    {
                        var two = node.Children.OrderByDescending(ch => g.Nodes[ch].Width).ThenBy(ch => ch).Take(2).Select(ch => g.Edges[edgeOf[ch]].Yaw).ToArray();
                        double yaw = Bisector(two[0], two[1]);
                        double ahead = (0.5 - ForkSplit) * forkLength;   // the split (u 0.4) at the fork node
                        Stamp(g, p, node, -1, id + VeinKind.Fork, VeinKind.Fork, p.ForkColumn, Wrap(src, node.X + Math.Cos(yaw) * ahead), node.Y + Math.Sin(yaw) * ahead, yaw, forkLength, node.Width);
                    }
                }
            return g;
        }

        private static void Stamp(MagicVeinGraph g, RoadsPolicy.MagicPolicy p, VeinNode owner, int edge, string id, string kind, int column, double x, double y, double yaw, double length, double width)
        {
            var src = g.Source;
            g.Stamps.Add(new VeinStamp { Id = id, Kind = kind, Node = owner.Index, Zone = owner.Zone, Edge = edge, AtlasRow = owner.CoreRow, AtlasColumn = column, X = x, Y = y, Yaw = yaw,
                Length = length, Width = width, RevealAt = owner.RevealAt, Alpha = owner.Alpha, Live = owner.Live, OnWater = owner.OnWater || OnWaterCell(src, x, y) || OnWaterSample(src, g, x, y) || ShowsWater(src, x, y) });
        }

        // The edge factor of a node edgeDepth samples inside its zone: 0 outside, rising linearly to 1 at edgeSamples + 0.5 (rim 1 gives
        // 0.5 / edgeSamples).
        public static double Edge(int depth, RoadsPolicy.MagicPolicy p) { return Math.Max(0, Math.Min(1, (depth - 0.5) / p.EdgeSamples)); }
        // Each zone sample's depth inside the zone (8-connected steps from the nearest sample outside it or on a pole row: 1 on the rim).
        private static Dictionary<int, int> Depths(MagicGroundSource src, VeinZone zone, int[] zoneOf)
        {
            var depth = new Dictionary<int, int>(); var queue = new Queue<int>(); int side = src.Side;
            foreach (int i in zone.Samples)
            {
                int ir = i / side, ic = i % side; bool rim = false;
                for (int dr = -1; dr <= 1 && !rim; dr++) for (int dc = -1; dc <= 1; dc++) if ((dr != 0 || dc != 0) && (src.PoleRow(ir + dr) || zoneOf[src.Index(ir + dr, ic + dc)] != zone.Index)) { rim = true; break; }
                if (rim) { depth[i] = 1; queue.Enqueue(i); }
            }
            while (queue.Count > 0)
            {
                int j = queue.Dequeue(), jr = j / side, jc = j % side;
                for (int dr = -1; dr <= 1; dr++) for (int dc = -1; dc <= 1; dc++)
                {
                    if ((dr == 0 && dc == 0) || src.PoleRow(jr + dr)) continue;
                    int k = src.Index(jr + dr, jc + dc);
                    if (zoneOf[k] == zone.Index && !depth.ContainsKey(k)) { depth[k] = depth[j] + 1; queue.Enqueue(k); }
                }
            }
            return depth;
        }
        // A node's alpha: minAlpha at the mutation threshold, rising by smoothstep to 1 over fadeBand of potency above it.
        public static double Fade(double potency, double threshold, RoadsPolicy.MagicPolicy p)
        {
            double u = Math.Max(0, Math.Min(1, (potency - threshold) / p.FadeBand));
            return p.MinAlpha + (1 - p.MinAlpha) * u * u * (3 - 2 * u);
        }
        public static bool WaterCore(MagicGroundSource src, string core) { BiomeTable.Row row; return !src.Cores.TryGetValue(core, out row) || row.Water; }
        // A point on a water travel cell (sea, lake, river) or past a pole line.
        public static bool OnWaterCell(MagicGroundSource src, double x, double y) { int cell = src.CellAt(x, y); return cell < 0 || TravelCells.Water(src.Cells.Classes[cell]); }
        // MMR-247 (acc5 c13): a point the scene shows as water whatever its travel cell: the low ground below the datum (the sea plane) or inside
        // a shown lake plane's box under its plane (MapDressing.PlaneCover). A land cell's edge can dip under the sea or a lake plane, so a stamp
        // centred there is a water stamp (MMR-290 draws it on the plane), never a land one.
        public static bool ShowsWater(MagicGroundSource src, double x, double y) { return src.LowGround != null && MapDressing.PlaneCover(src.Lakes, x, y, src.LowGround(x, y)) != null; }
        private static bool OnWaterSample(MagicGroundSource src, MagicVeinGraph g, double x, double y)
        {
            int sample = src.SampleAt(x, y), v = src.Variant[sample];
            return v >= 0 && WaterCore(src, src.Catalogue[v].Core);
        }
        public static double Bisector(double a, double b)
        {
            double bx = Math.Cos(a) + Math.Cos(b), by = Math.Sin(a) + Math.Sin(b);
            return Math.Sqrt(bx * bx + by * by) < 1e-9 ? a : Math.Atan2(by, bx);
        }
        // A fork stamp fits a node (not a source) with two or more children whose two widest open at most MaxForkHalf each side of their
        // bisector and whose parent edge comes in within MaxForkHalf of it; its length makes the art's children open at the children's angle.
        public static bool ForkFits(MagicVeinGraph g, VeinNode node, IDictionary<int, int> edgeOf, RoadsPolicy.MagicPolicy p, out double length)
        {
            length = 0;
            if (node.IsSource || node.Children.Count < 2) return false;
            var two = node.Children.OrderByDescending(ch => g.Nodes[ch].Width).ThenBy(ch => ch).Take(2).Select(ch => g.Edges[edgeOf[ch]].Yaw).ToArray();
            double half = Angle(two[0], two[1]) / 2, bisector = Bisector(two[0], two[1]);
            if (half > MaxForkHalf || Angle(g.Edges[edgeOf[node.Index]].Yaw, bisector) > MaxForkHalf) return false;
            length = Math.Min(p.StampLength, ForkSlope * node.Width / Math.Tan(Math.Max(half, 1e-6)));
            return true;
        }
        private static bool Placeable(double x, double y, Func<double, double, bool> inside, Grid close, List<double> lx, List<double> ly, double minGap)
        {
            if (!inside(x, y)) return false;
            foreach (int j in close.Near(x, y, minGap)) if (Distance(x, y, lx[j], ly[j]) < minGap) return false;
            return true;
        }
        private static double Angle(double a, double b) { double d = Math.IEEERemainder(b - a, 2 * Math.PI); return Math.Abs(d); }
        private static double Wrap(MagicGroundSource src, double x) { return src.Canonical(x - src.Strip); }
        private static double Distance(double ax, double ay, double bx, double by) { double dx = ax - bx, dy = ay - by; return Math.Sqrt(dx * dx + dy * dy); }
        private static void Count(IDictionary<string, int> d, string key) { int n; d.TryGetValue(key, out n); d[key] = n + 1; }

        // A uniform grid of ids over the zone's unwrapped plane.
        private sealed class Grid
        {
            private readonly double cell;
            private readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();
            public Grid(double cell) { this.cell = cell; }
            private static long Key(long gx, long gy) { return (gx << 32) ^ (uint)gy; }
            public void Add(int id, double x, double y)
            {
                long k = Key((long)Math.Floor(x / cell), (long)Math.Floor(y / cell)); List<int> l;
                if (!cells.TryGetValue(k, out l)) cells[k] = l = new List<int>();
                l.Add(id);
            }
            public IEnumerable<int> Near(double x, double y, double r)
            {
                long x0 = (long)Math.Floor((x - r) / cell), x1 = (long)Math.Floor((x + r) / cell), y0 = (long)Math.Floor((y - r) / cell), y1 = (long)Math.Floor((y + r) / cell);
                for (long gx = x0; gx <= x1; gx++)
                    for (long gy = y0; gy <= y1; gy++)
                    {
                        List<int> l;
                        if (cells.TryGetValue(Key(gx, gy), out l)) foreach (int id in l) yield return id;
                    }
            }
        }
    }
}
