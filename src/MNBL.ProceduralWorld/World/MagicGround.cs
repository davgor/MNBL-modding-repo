using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using MNBL.ProceduralWorld.World;

namespace MNBL.ProceduralWorld.Surface
{
    // MMR-260: the magic ground overlay and the live terrain-type overlay (docs/magic-ground.md; findings sections 6, 7 and 13; RT-20, RT-26).
    // Pure managed: the overlay is built from the world's bundle (source/world.json's optional magic layers, cells.bin, the surface frame) and
    // the shared biome table, the same way at activation and at load (WorldReconstruction). MagicGroundRuntime is the engine side.
    //   * Source: world.json layers.biome_variant (-1, or an index into terrain.magical_biomes: core and school), ley_<school> (the potency) and
    //     magic {enabled, mutation_threshold, dominance_margin, school_order}. All optional: a magic-off world loads with no magic ground.
    //   * Mapping: a source sample (row r from the north pole line, column c) is the logical point (W frac(c / n1 - meridian / 360), H r / n1)
    //     (projection.json forward.sourceSample); a physical point's sample is its nearest node (inverse.sourceGrid: columns modulo the period,
    //     so every strip image and both sides of the seam reach the same sample; points past a pole line clamp to the pole node). Pole rows are
    //     lines, not ground: they carry no decal and retype nothing. Each other sample lies in exactly one travel cell (the cell holding its
    //     point), so every mutated sample reaches exactly one cell.
    //   * Snapshot: the generated variants (visual only, RT-20) plus the live mutation cells (MMR-261's, by source sample), immutable; a change
    //     builds a new one and swaps the reference (Interlocked), so a reader on any thread sees one whole snapshot.
    //   * Terrain type (live cells only): a land travel cell holding live-mutated samples takes MutatedType(school, core) of the samples (the
    //     type most of them give; a tie takes the first in row-major order), within terrain-materials.json mutation.landValid (never Dune);
    //     water cores and non-land cells are never retyped, so passability never changes. Faces: a navmesh face whose group is a land face type
    //     takes its centre's cell's type (FaceTypes; every strip image, since the physical column wraps to its logical cell).
    //   * Battle class: a battle-map cell whose centre lies in a retyped cell takes faceClass[remapped type] (RT-26); the runtime patches it
    //     through GeneratedBattleTerrain.CellPatches and re-chooses changed cells with RebuildCells, which writes every strip image.
    //   * Decals (MMR-249 delivery A): entity-less scene decals along the vein graph of the generated and live magic zones (MMR-288,
    //     World/MagicVeins.cs; MagicGroundPlan, roads.json magic): land stamps only; live veins revealed outward over the policy's time.
    //   * Colour grade: a live retyped cell's grade cells take the grade of the table's first land biome with its type (MMR-256's tick).
    //   * Navigation: nothing here reaches the navmesh file, its hash or NavigationCacheKey (a3).
    internal struct MutationCell
    {
        public int Row, Column, Variant;   // a source sample; Variant -1 clears the sample (no magic ground there, whatever was generated)
        public float Potency;              // the school's potency at the sample (NaN: full)
        public MutationCell(int row, int column, int variant, float potency) { Row = row; Column = column; Variant = variant; Potency = potency; }
    }

    // A live mutation's node (MMR-261: the capital a LeylineEdit grew): a separate root entity with a light and a particle system.
    internal sealed class MagicNode
    {
        public string School; public double X, Y;   // canonical physical
        // MMR-288: campaign hours its veins began to grow (MMR-261's edit log: the key point's first batch day x 24); NaN: fully grown. Not part
        // of the overlay's digest.
        public double Since = Double.NaN;
        public MagicNode(string school, double x, double y) { School = school; X = x; Y = y; }
    }

    internal sealed class MagicVariant { public int Index; public string Core, School; public uint Colour; }

    // The world's generated magic ground and its frame: the source's layers, the travel cells and the surface frame. Immutable.
    internal sealed class MagicGroundSource
    {
        public int Side, Columns;            // samples per side; columns per period (Side - 1: column Side - 1 duplicates column 0)
        public bool Enabled;
        public double Threshold, Dominance;
        public string[] SchoolOrder = new string[0];
        public MagicVariant[] Catalogue = new MagicVariant[0];
        public int[] Variant;                // Side x Side, -1 where unmutated
        public float[] Potency;              // the variant's school's ley value (0 where unmutated)
        public TravelCells Cells;
        public double Width, Height, Strip, PoleMargin, Meridian;
        public BiomeTable Table;
        public string SourceSha256;
        public Func<double, double, double> Ground;   // MMR-262: the ground at a physical point (MapDressing.GroundOf), for the decals' slope depth
        // MMR-247 (acc5): the scene's shown lake planes and the low ground (MapDressing.LowGroundOf): a stamp centre they show as water is a
        // water stamp (MagicVeins.ShowsWater), whatever its travel cell.
        public IList<GeneratedWaterSystem.Lake> Lakes = new GeneratedWaterSystem.Lake[0];
        public Func<double, double, double> LowGround;
        public readonly Dictionary<string, BiomeTable.Row> Cores = new Dictionary<string, BiomeTable.Row>(StringComparer.Ordinal);

        // lakes: the scene's lake planes (the map dressing plan's, MapDressingPlan.Lakes); null computes them by the same rule.
        public static MagicGroundSource From(GeneratedClimateSystem.Source source, WorldSurface surface, TravelCells cells, BiomeTable table, IList<GeneratedWaterSystem.Lake> lakes = null)
        {
            var s = new MagicGroundSource { Side = source.Side, Columns = source.Side - 1, Cells = cells, Width = surface.LogicalWidth, Height = surface.LogicalHeight, Strip = surface.Strip,
                PoleMargin = surface.PoleMargin, Meridian = surface.CentralMeridianDeg, Table = table, SourceSha256 = source.Sha256, Ground = MapDressing.GroundOf(surface), LowGround = MapDressing.LowGroundOf(surface) };
            if (surface.SourceGridSide != source.Side) throw new InvalidDataException("world.json's layers are " + source.Side + " samples a side, the projection's source grid " + surface.SourceGridSide + ".");
            foreach (var row in table.Rows.Values) s.Cores[row.Core] = row;
            int n = source.Side * source.Side;
            s.Variant = Enumerable.Repeat(-1, n).ToArray(); s.Potency = new float[n];
            s.Enabled = source.MagicEnabled && source.BiomeVariant != null && source.MagicalBiomes.Count > 0;
            if (!s.Enabled) return s;   // a magic-off world: no magic ground, and live mutations are refused
            int unmatched, hidden;
            s.Lakes = lakes ?? GeneratedWaterSystem.Lakes(surface, source, PeriodicWorldCoordinates.FromSurface(surface), out unmatched, out hidden);
            s.Threshold = source.MagicThreshold; s.Dominance = source.MagicDominance;
            if (Double.IsNaN(s.Threshold) || !(s.Threshold > 0 && s.Threshold < 1)) throw new InvalidDataException("world.json magic.mutation_threshold is missing or outside (0, 1).");
            s.SchoolOrder = source.MagicSchoolOrder.ToArray();
            s.Catalogue = source.MagicalBiomes.Select((e, i) =>
            {
                object core, school, colour; e.TryGetValue("core", out core); e.TryGetValue("magic_school", out school); e.TryGetValue("color", out colour);
                var rgb = colour as object[];
                uint argb = 0xFFFFFFFFu;
                if (rgb != null && rgb.Length == 3) argb = 0xFF000000u | ((uint)Convert.ToDouble(rgb[0], CultureInfo.InvariantCulture) << 16) | ((uint)Convert.ToDouble(rgb[1], CultureInfo.InvariantCulture) << 8) | (uint)Convert.ToDouble(rgb[2], CultureInfo.InvariantCulture);
                if (!(core is string) || !(school is string)) throw new InvalidDataException("world.json terrain.magical_biomes[" + i + "] lacks its core or magic_school.");
                return new MagicVariant { Index = i, Core = (string)core, School = (string)school, Colour = argb };
            }).ToArray();
            for (int i = 0; i < n; i++)
            {
                int v = (int)source.BiomeVariant[i];
                if (v < 0) continue;
                s.Variant[i] = v;
                double[] ley; s.Potency[i] = source.Ley.TryGetValue(s.Catalogue[v].School, out ley) ? (float)ley[i] : 1f;
            }
            return s;
        }

        // ---- the projection ----
        public bool PoleRow(int row) { return row <= 0 || row >= Columns; }
        public int Index(int row, int column) { return row * Side + Wrap(column); }
        public int Wrap(int column) { return ((column % Columns) + Columns) % Columns; }
        // A sample's logical point (projection.json forward.sourceSample).
        public double[] Logical(int row, int column)
        {
            double u = (double)Wrap(column) / Columns - Meridian / 360.0;
            return new[] { Width * (u - Math.Floor(u)), Height * row / Columns };
        }
        // The nearest source sample of a physical point (inverse.sourceGrid): the column modulo the period, the row clamped to the pole lines.
        public int SampleAt(double physicalX, double physicalY)
        {
            double y = Math.Max(0, Math.Min(Height, physicalY - PoleMargin));
            int row = (int)Math.Round(Columns * y / Height);
            if (row <= 0) return Index(0, 0);
            if (row >= Columns) return Index(Columns, 0);
            double x = (physicalX - Strip) % Width; if (x < 0) x += Width;
            return Index(row, (int)Math.Round(Columns * (x / Width + Meridian / 360.0)));
        }
        // The logical travel cell (r * Cols + c) holding a physical point, or -1 past the pole lines.
        public int CellAt(double physicalX, double physicalY)
        {
            double x = (physicalX - Strip) % Width; if (x < 0) x += Width;
            int r = (int)Math.Floor((physicalY - PoleMargin) / Cells.Spacing);
            if (!Cells.InRows(r)) return -1;
            return Cells.Index(r, (int)Math.Floor(x / Cells.Spacing));
        }
        // The travel cell holding a sample's point (-1 for pole rows).
        public int CellOfSample(int row, int column)
        {
            if (PoleRow(row)) return -1;
            var p = Logical(row, column);
            int r = (int)Math.Floor(p[1] / Cells.Spacing);
            return Cells.InRows(r) ? Cells.Index(r, (int)Math.Floor(p[0] / Cells.Spacing)) : -1;
        }
        public double Canonical(double logicalX) { double x = logicalX % Width; if (x < 0) x += Width; return x + Strip; }

        // The land TerrainType id a live variant retypes a cell to (0: none: a water core, an unknown school, outside landValid).
        public int MutatedTypeId(int variant)
        {
            if (variant < 0 || variant >= Catalogue.Length) return 0;
            var v = Catalogue[variant];
            BiomeTable.Row row;
            if (!Cores.TryGetValue(v.Core, out row) || row.Water) return 0;
            string type = Table.MutatedType(v.School, v.Core);
            if (type == null || !Table.LandValid.Contains(type)) return 0;
            return TerrainTypes.Id(type);
        }
    }

    // One overlay state: the generated variants plus the live mutation cells. Immutable once built.
    internal sealed class MagicSnapshot
    {
        public readonly MagicGroundSource Source;
        public readonly int Version;
        private readonly Dictionary<int, MutationCell> live;
        public readonly byte[] CellTypes;          // logical travel cell -> retyped TerrainType id (0: the cell's own); live cells only (RT-20)
        public readonly int[] RetypedCells;        // the cells CellTypes retypes, ascending
        public readonly IList<MagicNode> Nodes;
        public readonly bool MutationsDisabled;    // RT-37 (MMR-261): a digest mismatch keeps generated magic ground and drops the live cells
        public readonly string Digest;

        public MagicSnapshot(MagicGroundSource source, int version, IDictionary<int, MutationCell> liveCells, IEnumerable<MagicNode> nodes, bool disabled)
        {
            Source = source; Version = version; MutationsDisabled = disabled;
            live = new Dictionary<int, MutationCell>(liveCells ?? new Dictionary<int, MutationCell>());
            Nodes = (nodes ?? new MagicNode[0]).ToList().AsReadOnly();
            var cells = source.Cells;
            CellTypes = new byte[cells.Classes.Length];
            var votes = new Dictionary<int, Dictionary<int, int>>();
            foreach (var kv in live.OrderBy(k => k.Key))
            {
                var c = kv.Value;
                int cell = source.CellOfSample(c.Row, c.Column);
                if (cell < 0 || cells.Classes[cell] != TravelCells.Land) continue;
                int type = source.MutatedTypeId(c.Variant);
                if (type == 0) continue;
                Dictionary<int, int> v; if (!votes.TryGetValue(cell, out v)) votes[cell] = v = new Dictionary<int, int>();
                int n; v.TryGetValue(type, out n); v[type] = n + 1;
            }
            foreach (var kv in votes)
            {
                // The type most of the cell's live samples give; a tie keeps the first given (row-major sample order: Dictionary keeps insertion order here).
                int best = 0, count = -1;
                foreach (var t in kv.Value) if (t.Value > count) { best = t.Key; count = t.Value; }
                CellTypes[kv.Key] = (byte)best;
            }
            RetypedCells = Enumerable.Range(0, CellTypes.Length).Where(i => CellTypes[i] != 0).ToArray();
            Digest = Json.Sha(CanonicalText());
        }

        public IEnumerable<MutationCell> LiveCells { get { return live.OrderBy(k => k.Key).Select(k => k.Value); } }
        public int LiveCount { get { return live.Count; } }
        public bool IsLive(int sample) { return live.ContainsKey(sample); }
        // The variant drawn at a sample: the live cell's where there is one, else the generated one.
        public int VariantAt(int sample) { MutationCell c; return live.TryGetValue(sample, out c) ? c.Variant : Source.Variant[sample]; }
        public float PotencyAt(int sample) { MutationCell c; return live.TryGetValue(sample, out c) ? (float.IsNaN(c.Potency) ? 1f : c.Potency) : Source.Potency[sample]; }
        // The canonical text of the live state (MMR-261 keeps its own digest of the stored cells; this one names the overlay's content).
        public string CanonicalText()
        {
            var b = new StringBuilder("mnbl.magic-overlay 1\n");
            foreach (var c in LiveCells) b.Append(c.Row).Append(',').Append(c.Column).Append(',').Append(c.Variant).Append(',').Append(c.Potency.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            foreach (var n in Nodes) b.Append("node ").Append(n.School).Append(',').Append(n.X.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(n.Y.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            return b.ToString();
        }
        // The cell's TerrainType id: the retyped one, else cells.bin's land type (0 off land).
        public int TypeOf(int cell) { return CellTypes[cell] != 0 ? CellTypes[cell] : Source.Cells.LandTypes[cell]; }
    }

    // A change of the live state: the snapshots before and after, the travel cells whose type changed and the land shares (RT-20, r1).
    internal sealed class MagicChange
    {
        public MagicSnapshot Before, After;
        public int[] ChangedCells;
        public int[] ChangedSamples;
        public SortedDictionary<string, object> SharesBefore, SharesAfter;
        public Dictionary<string, object> Record()
        {
            return new Dictionary<string, object> { ["version"] = After.Version, ["digest"] = After.Digest, ["liveCells"] = After.LiveCount, ["retypedCells"] = After.RetypedCells.Length,
                ["changedCells"] = ChangedCells.Length, ["changedSamples"] = ChangedSamples.Length, ["mutationsDisabled"] = After.MutationsDisabled, ["landSharesBefore"] = SharesBefore, ["landSharesAfter"] = SharesAfter };
        }
    }

    // The world's overlay: one current snapshot, replaced whole.
    internal sealed class WorldSurfaceOverlay
    {
        public readonly MagicGroundSource Source;
        private MagicSnapshot current;
        public WorldSurfaceOverlay(MagicGroundSource source) { Source = source; current = new MagicSnapshot(source, 0, null, null, false); }
        public MagicSnapshot Current { get { return Volatile.Read(ref current); } }

        // The whole live state (a load's replay of the stored cells, MMR-261): replaces every live cell and node.
        public MagicChange Replace(IEnumerable<MutationCell> cells, IEnumerable<MagicNode> nodes)
        {
            var before = Current;
            return Swap(before, new MagicSnapshot(Source, before.Version + 1, Validated(cells, new Dictionary<int, MutationCell>()), nodes, false));
        }
        // Changed cells of a cast (MMR-261's fwg apply delta) merged into the live state; nodes are added.
        public MagicChange Apply(IEnumerable<MutationCell> cells, IEnumerable<MagicNode> nodes)
        {
            var before = Current;
            if (before.MutationsDisabled) throw new InvalidOperationException("Live mutations are disabled for this world (" + "a digest mismatch, RT-37); no cast applies.");
            var merged = new Dictionary<int, MutationCell>();
            foreach (var c in before.LiveCells) merged[Source.Index(c.Row, c.Column)] = c;
            return Swap(before, new MagicSnapshot(Source, before.Version + 1, Validated(cells, merged), before.Nodes.Concat(nodes ?? new MagicNode[0]), false));
        }
        // RT-37 (MMR-261): the live cells and nodes are dropped and later casts refused; the generated magic ground stays drawn.
        public MagicChange Disable()
        {
            var before = Current;
            return Swap(before, new MagicSnapshot(Source, before.Version + 1, null, null, true));
        }

        private Dictionary<int, MutationCell> Validated(IEnumerable<MutationCell> cells, Dictionary<int, MutationCell> into)
        {
            if (!Source.Enabled && cells != null && cells.Any()) throw new InvalidOperationException("The world has no magic layers (a magic-off world); a live mutation cannot apply.");
            foreach (var c in cells ?? new MutationCell[0])
            {
                if (c.Row < 0 || c.Row >= Source.Side || c.Column < 0 || c.Column >= Source.Side) throw new ArgumentOutOfRangeException("cells", "sample " + c.Row + "," + c.Column + " is outside the " + Source.Side + "-sample source grid.");
                if (c.Variant < -1 || c.Variant >= Source.Catalogue.Length) throw new ArgumentOutOfRangeException("cells", "variant " + c.Variant + " is not -1 or a magical biome index.");
                into[Source.Index(c.Row, c.Column)] = new MutationCell(c.Row, Source.Wrap(c.Column), c.Variant, c.Potency);
            }
            return into;
        }

        private MagicChange Swap(MagicSnapshot before, MagicSnapshot after)
        {
            if (!ReferenceEquals(Interlocked.CompareExchange(ref current, after, before), before)) throw new InvalidOperationException("The magic overlay changed under a concurrent update.");
            var changed = Enumerable.Range(0, after.CellTypes.Length).Where(i => after.CellTypes[i] != before.CellTypes[i]).ToArray();
            var samples = new HashSet<int>(before.LiveCells.Select(c => Source.Index(c.Row, c.Column)));
            samples.SymmetricExceptWith(after.LiveCells.Select(c => Source.Index(c.Row, c.Column)));
            foreach (var c in after.LiveCells) { int i = Source.Index(c.Row, c.Column); if (before.IsLive(i) && before.VariantAt(i) != c.Variant) samples.Add(i); }
            return new MagicChange { Before = before, After = after, ChangedCells = changed, ChangedSamples = samples.OrderBy(x => x).ToArray(), SharesBefore = LandShares(before), SharesAfter = LandShares(after) };
        }

        // Per TerrainType: the land travel cells of that type and their share of the land (RT-20's record).
        public static SortedDictionary<string, object> LandShares(MagicSnapshot s)
        {
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal); int land = 0;
            var cells = s.Source.Cells;
            for (int i = 0; i < cells.Classes.Length; i++)
            {
                if (cells.Classes[i] != TravelCells.Land) continue;
                land++;
                string name = TerrainTypes.Name(s.TypeOf(i)); int n; counts.TryGetValue(name, out n); counts[name] = n + 1;
            }
            var r = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var kv in counts) r[kv.Key] = new Dictionary<string, object> { ["cells"] = kv.Value, ["share"] = Math.Round((double)kv.Value / Math.Max(1, land), 6) };
            r["landCells"] = land;
            return r;
        }
    }

    // A navmesh face as the engine reports it: its index, centre and group.
    internal struct FaceSample { public int Index, Group; public double X, Y; public FaceSample(int index, double x, double y, int group) { Index = index; X = x; Y = y; Group = group; } }

    internal static class MagicFaces
    {
        // Per face index, the TerrainType a live mutation gives it (0: its own group), or null when nothing is retyped. Only faces whose group is
        // a land face type change (never Bridge, Fording, Beach, water or Mountain); each face takes its centre's cell (the physical column
        // wraps, so every strip image's twin takes the same type).
        public static byte[] Build(MagicSnapshot s, int faceCount, Func<int, FaceSample> face)
        {
            if (s.RetypedCells.Length == 0 || faceCount <= 0) return null;
            var land = new HashSet<int>(BiomeTable.LandFaceTypes.Select(TerrainTypes.Id));
            var samples = new FaceSample[faceCount]; int max = -1;
            for (int i = 0; i < faceCount; i++) { samples[i] = face(i); max = Math.Max(max, samples[i].Index); }
            var types = new byte[max + 1];
            foreach (var f in samples)
            {
                if (f.Index < 0 || !land.Contains(f.Group)) continue;
                int cell = s.Source.CellAt(f.X, f.Y);
                if (cell >= 0 && s.CellTypes[cell] != 0) types[f.Index] = s.CellTypes[cell];
            }
            return types;
        }
    }

    // The battle map's cells (GeneratedBattleTerrain: the reader's own cell size over the terrain; canonical columns counted from the strip).
    internal sealed class MagicBattleCells
    {
        public int Width, Height, OriginCell, PeriodCells; public double CellX, CellY;
        // The scene class a live retyped cell gives the battle cell (lx canonical, y), or null.
        public string ClassAt(MagicSnapshot s, IDictionary<string, string> faceClass, int lx, int y)
        {
            int cell = s.Source.CellAt((OriginCell + lx + .5) * CellX, (y + .5) * CellY);
            if (cell < 0 || s.CellTypes[cell] == 0) return null;
            string cls; return faceClass.TryGetValue(TerrainTypes.Name(s.CellTypes[cell]), out cls) ? cls : null;
        }
        // The canonical battle cells (MapRequest.Key) whose centre lies in one of the given travel cells.
        public List<long> Keys(MagicGroundSource source, ICollection<int> travelCells)
        {
            var keys = new List<long>();
            if (travelCells.Count == 0) return keys;
            var set = new HashSet<int>(travelCells);
            for (int y = 0; y < Height; y++)
                for (int lx = 0; lx < PeriodCells; lx++)
                {
                    int cell = source.CellAt((OriginCell + lx + .5) * CellX, (y + .5) * CellY);
                    if (cell >= 0 && set.Contains(cell)) keys.Add((long)y * 1000003 + lx);
                }
            return keys;
        }
    }

    // The colour grade cells (GeneratedMapAtmosphere's 512 x 512 grid over the terrain, every physical image) a snapshot's live cells grade.
    internal static class MagicGrades
    {
        public static byte GradeOf(BiomeTable table, int type)
        {
            string name = TerrainTypes.Name(type);
            var row = table.Rows.Values.Where(r => !r.Water && r.TerrainType == name).OrderBy(r => r.Id).FirstOrDefault();
            return row == null ? (byte)0 : (byte)row.ColourGrade;
        }
        public static Dictionary<int, byte> Cells(MagicSnapshot s, double terrainX, double terrainY, int dimension)
        {
            var grades = new Dictionary<int, byte>();
            if (s.RetypedCells.Length == 0) return grades;
            for (int row = 0; row < dimension; row++)
                for (int col = 0; col < dimension; col++)
                {
                    int cell = s.Source.CellAt((col + .5) * terrainX / dimension, (row + .5) * terrainY / dimension);
                    if (cell < 0 || s.CellTypes[cell] == 0) continue;
                    byte g = GradeOf(s.Source.Table, s.CellTypes[cell]);
                    if (g != 0) grades[row * dimension + col] = g;
                }
            return grades;
        }
    }

    // One magic ground decal: a land stamp of the vein graph (its owner node's sample, look and atlas cell) and the decal.
    internal sealed class MagicStamp
    {
        public string Id, Kind; public int Sample, Node, Zone, Order; public bool Live; public string School, Look, Core, Group; public int AtlasRow, AtlasColumn; public double Potency, Alpha = 1;
        public double RevealAt = Double.NegativeInfinity;   // campaign hours a live stamp appears (generated: always)
        public RoadDecal Decal;
        // The stamp's identity and everything drawn: a stamp with the same key is not redrawn.
        public string Key
        {
            get
            {
                Func<double, string> f = v => v.ToString("R", CultureInfo.InvariantCulture);
                return Id + ":" + Decal.Material + ":" + AtlasRow + "," + AtlasColumn + ":" + f(Decal.X) + "," + f(Decal.Y) + "," + f(Decal.Yaw) + "," + f(Decal.HalfLength) + "," + f(Decal.HalfWidth) + ":" + Decal.Tint.ToString("x8", CultureInfo.InvariantCulture);
            }
        }
    }

    internal sealed class MagicGroundPlan
    {
        public const string Version = "mmr288/2";
        public MagicVeinGraph Graph;
        public bool WaterDrawn;                          // a vein layer draws the graph's water stamps (MMR-290)
        public readonly SortedDictionary<string, int> Components = new SortedDictionary<string, int>(StringComparer.Ordinal);   // the drawn components (MagicGround.Components)
        // The graph stamps of components too small to draw (land and, when WaterDrawn, water ones): a water layer skips the water ids here.
        public readonly HashSet<string> Dropped = new HashSet<string>(StringComparer.Ordinal);
        public readonly List<MagicStamp> Stamps = new List<MagicStamp>();
        public readonly SortedDictionary<string, int> BySchool = new SortedDictionary<string, int>(StringComparer.Ordinal), ByGroup = new SortedDictionary<string, int>(StringComparer.Ordinal),
                                                      ByKind = new SortedDictionary<string, int>(StringComparer.Ordinal), Skipped = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public int Generated, Live, Hidden, Thinned, MutatedSamples;
        public double Hours = Double.PositiveInfinity;   // the reveal time this plan shows
        public Dictionary<string, object> Record()
        {
            return new Dictionary<string, object> { ["version"] = Version, ["decals"] = Stamps.Count, ["generated"] = Generated, ["live"] = Live, ["hiddenLive"] = Hidden, ["thinned"] = Thinned,
                ["mutatedSamples"] = MutatedSamples, ["byKind"] = ByKind, ["bySchool"] = BySchool, ["byGroup"] = ByGroup, ["skipped"] = Skipped, ["waterDrawn"] = WaterDrawn, ["components"] = Components, ["zonesByStamps"] = ZonesByStamps(),
                ["faded"] = Stamps.Count(x => x.Alpha < 1), ["veins"] = Graph == null ? null : Graph.Record() };
        }
        // Zones by the land stamps they draw: 0, 1, 2, 3 or more.
        public SortedDictionary<string, int> ZonesByStamps()
        {
            var r = new SortedDictionary<string, int>(StringComparer.Ordinal) { ["0"] = 0, ["1"] = 0, ["2"] = 0, ["3+"] = 0 };
            if (Graph == null) return r;
            var drawn = Stamps.GroupBy(x => x.Zone).ToDictionary(gr => gr.Key, gr => gr.Count());
            foreach (var z in Graph.Zones) { int c; drawn.TryGetValue(z.Index, out c); r[c >= 3 ? "3+" : c.ToString(CultureInfo.InvariantCulture)]++; }
            return r;
        }
        public string Sha256()
        {
            var b = new StringBuilder();
            foreach (var s in Stamps) b.Append(s.Key).Append('\n');
            return Json.Sha(b.ToString());
        }
        // The stamps revealed at a campaign time (live veins grow outward from their cast; generated ones are always shown).
        public MagicGroundPlan Revealed(double hours)
        {
            var r = new MagicGroundPlan { Graph = Graph, Thinned = Thinned, MutatedSamples = MutatedSamples, Hours = hours, WaterDrawn = WaterDrawn };
            foreach (var kv in Skipped) r.Skipped[kv.Key] = kv.Value;
            foreach (var kv in Components) r.Components[kv.Key] = kv.Value;
            r.Dropped.UnionWith(Dropped);
            foreach (var s in Stamps) if (s.RevealAt <= hours) r.Stamps.Add(s); else r.Hidden++;
            r.Tally();
            return r;
        }
        // MMR-291: the same plan with no stamp to draw (its graph, records and reveal time kept): the land decals while the overlay meshes draw
        // the veins (roads.json overlayMeshes.veins.mode mesh).
        public MagicGroundPlan WithoutStamps()
        {
            var r = Revealed(Hours);
            r.Stamps.Clear(); r.Hidden = 0; r.Tally();
            return r;
        }
        // The first reveal time after hours (+infinity when every stamp is shown by then). MMR-290: over the graph's nodes too, so the overlay's
        // reveal steps include those of its vein layers (water stamps and features take their owner node's RevealAt; a node with no land stamp,
        // or above the cast's highest land Order, would otherwise show late or not until a reload).
        public double NextReveal(double hours)
        {
            double next = Double.PositiveInfinity;
            foreach (var s in Stamps) if (s.RevealAt > hours && s.RevealAt < next) next = s.RevealAt;
            if (Graph != null) foreach (var n in Graph.Nodes) if (n.RevealAt > hours && n.RevealAt < next) next = n.RevealAt;
            return next;
        }
        internal void Tally()
        {
            Generated = Live = 0; BySchool.Clear(); ByGroup.Clear(); ByKind.Clear();
            foreach (var x in Stamps)
            {
                if (x.Live) Live++; else Generated++;
                MagicGround.Count(BySchool, x.School); MagicGround.Count(ByGroup, x.Group); MagicGround.Count(ByKind, x.Kind);
            }
        }
    }

    // MMR-288: the live reveal's clock (MagicGroundRuntime: Install starts it, a live change restarts it, the campaign tick ticks it). Pure, so
    // the recorder tests run it. A start without a campaign clock (+infinity: Campaign.Current null or CampaignTime unreadable at AfterLoad)
    // shows every vein grown, and the first tick with a real time re-hides what that time does not show yet.
    internal sealed class MagicRevealClock
    {
        public double Hours = Double.PositiveInfinity, Next = Double.PositiveInfinity;
        public bool Unknown = true;   // no campaign time read yet
        // The plan to draw at now (+infinity or NaN: no campaign clock).
        public MagicGroundPlan Start(MagicGroundPlan full, double now)
        {
            Unknown = Double.IsNaN(now) || Double.IsInfinity(now);
            Hours = Unknown ? Double.PositiveInfinity : now;
            Next = full.NextReveal(Hours);
            return full.Revealed(Hours);
        }
        // A live change: the new plan at the clock's own time.
        public MagicGroundPlan Restart(MagicGroundPlan full) { return Start(full, Hours); }
        // The campaign tick: the plan to draw when a reveal step is due (or the first real time after a start without a clock), else null.
        public MagicGroundPlan Tick(MagicGroundPlan full, double now)
        {
            if (full == null || Double.IsNaN(now) || Double.IsInfinity(now)) return null;
            bool resync = Unknown;
            Unknown = false; Hours = now;
            if (!resync && now < Next) return null;
            Next = full.NextReveal(now);
            return full.Revealed(now);
        }
        // Live stamps with a reveal time (a start without a clock shows them grown until the first tick).
        public static int Timed(MagicGroundPlan full) { return full.Stamps.Count(x => !Double.IsNegativeInfinity(x.RevealAt)); }
    }

    internal static class MagicGround
    {
        public const double DecalHalfDepth = 3;   // MMR-249's projection depth, as the road decals; the floor of each decal's slope depth (MMR-262)

        // The land stamps of the snapshot's vein graph (generated zones and live mutations, MMR-288), every one with its reveal time: a fully
        // grown plan. Plan(...).Revealed(hours) is what a campaign time shows.
        // waterDrawn: a vein layer draws the graph's water stamps (MMR-290), so a land fragment hanging off a water node continues a drawn vein.
        public static MagicGroundPlan Plan(MagicSnapshot s, RoadsPolicy.MagicPolicy p, bool waterDrawn = false)
        {
            var graph = MagicVeins.Build(s, p);
            var plan = new MagicGroundPlan { Graph = graph, MutatedSamples = graph.MutatedSamples, WaterDrawn = waterDrawn };
            foreach (var kv in graph.Skipped) plan.Skipped[kv.Key] = kv.Value;
            var src = s.Source;
            var made = new MagicStamp[graph.Stamps.Count];
            for (int k = 0; k < graph.Stamps.Count; k++)
            {
                var st = graph.Stamps[k];
                if (st.OnWater) { Count(plan.Skipped, "waterStamp"); continue; }
                if (st.AtlasRow < 0) { Count(plan.Skipped, "noAtlasRow"); continue; }
                made[k] = Make(p, graph.Nodes[st.Node], st.Id, st.Kind, st.AtlasRow, st.AtlasColumn, st.X, st.Y, st.Yaw, st.Length, st.Width, st.RevealAt, st.Live, st.Alpha);
            }
            Components(plan, made, p, waterDrawn);
            // Past maxDecals, generated stamps go outermost first (highest Order, a fork before the trunk into its node, then the thinnest), so the
            // kept stamps stay joined to their source: every stamp upstream of a kept one is kept. Live stamps and bursts are always drawn.
            if (plan.Stamps.Count > p.MaxDecals)
            {
                int drop = plan.Stamps.Count - p.MaxDecals;
                Func<string, int> rank = kind => kind == VeinKind.Fork ? 0 : kind == VeinKind.Tip ? 1 : 2;
                var gone = new HashSet<MagicStamp>(plan.Stamps.Where(x => !x.Live && x.Kind != VeinKind.Burst).OrderByDescending(x => x.Order).ThenBy(x => rank(x.Kind)).ThenBy(x => x.Decal.HalfWidth).ThenBy(x => x.Id, StringComparer.Ordinal).Take(drop));
                plan.Thinned = gone.Count;
                plan.Stamps.RemoveAll(gone.Contains);
            }
            foreach (var x in plan.Stamps)
            {
                // MMR-262: deep enough for the relief under the footprint (the frame's z stays the centre's height).
                var d = x.Decal;
                if (src.Ground != null) d.HalfDepth = MapDressing.SlopeDepth(src.Ground, d.X, d.Y, d.Yaw, d.HalfLength, d.HalfWidth);
            }
            plan.Tally();
            return plan;
        }

        private static MagicStamp Make(RoadsPolicy.MagicPolicy p, VeinNode node, string id, string kind, int atlasRow, int column, double x, double y, double yaw, double length, double width, double revealAt, bool live, double alpha)
        {
            int row = p.FlipV ? 3 - atlasRow : atlasRow;
            // SetFactor1 multiplies the stamp, alpha included (Shaders/Sources/pbr_shading_functions.rsh:278, the terrain's tiled decal path): white
            // with the node's fade; a full-alpha stamp keeps no tint (MMR-249's plain path).
            uint tint = alpha >= 1 ? 0u : ((uint)Math.Round(255 * Math.Max(0, alpha)) << 24) | 0x00FFFFFFu;
            var decal = new RoadDecal { Road = -1, Piece = -1, Family = "magic", Material = p.MaterialPrefix + node.Look, X = x, Y = y, Yaw = p.FlipU ? yaw + Math.PI : yaw,
                HalfLength = length / 2, HalfWidth = width / 2, HalfDepth = DecalHalfDepth, Atlas = new[] { .25f, .25f, column / 4f, row / 4f }, Tint = tint };
            SetFallback(decal, p, kind, node.Look, alpha);
            return new MagicStamp { Id = id, Kind = kind, Sample = node.Sample, Node = node.Index, Zone = node.Zone, Order = node.Order, Live = live, School = node.School, Look = node.Look,
                Core = node.Core, Group = p.Rows[atlasRow].Key, AtlasRow = atlasRow, AtlasColumn = column, Potency = node.Potency, Alpha = alpha, RevealAt = revealAt, Decal = decal };
        }

        // MMR-247 (D): the stamp's fallback while its material's textures are not in the campaign map's decal atlas (roads.json magic.fallback):
        // a burst draws the burst stamp, any other kind the stroke, tinted toward the look's colour with the stamp's alpha (a stroke turned by
        // flipU's half turn is the same stroke).
        internal static void SetFallback(RoadDecal d, RoadsPolicy.MagicPolicy p, string kind, string look, double alpha)
        {
            var f = kind == VeinKind.Burst ? p.BurstFallback : p.StrokeFallback;
            int[] colour;
            if (f == null || look == null || !p.FallbackColours.TryGetValue(look, out colour)) return;
            d.Fallback = f.Material; d.FallbackAtlas = f.Atlas; d.FallbackTint = FallbackTint(colour, f.Core, alpha);
            d.FallbackTurn = f.Turn; d.FallbackAlong = f.Along; d.FallbackAcross = f.Across;
        }

        // SetFactor1 multiplies the stamp in linear light (pbr_shading_functions.rsh: INPUT_TEX_GAMMA, then decal_d *= factor_color_1): per channel
        // 255 x the linear colour / the linear core of the stamp, clamped at 255 (a multiply cannot lighten), alpha the stamp's.
        public static uint FallbackTint(int[] colour, int[] core, double alpha)
        {
            Func<int, double> lin = c => { double v = c / 255.0; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); };
            uint t = (uint)Math.Round(255 * Math.Max(0, Math.Min(1, alpha))) << 24;
            for (int k = 0; k < 3; k++) t |= (uint)Math.Round(255 * Math.Min(1, lin(colour[k]) / lin(core[k]))) << (16 - 8 * k);
            return t;
        }

        // The drawn components (MMR-288 gaps): the graph's land stamps (plus its water stamps when a layer draws them) joined to the stamp upstream
        // of each (a trunk or tip to its parent node's trunk or the source's burst; a fork to the trunk into its node). Each component is a tree
        // with one root:
        //   * a burst: kept when it holds at least minStamps stamps (a lone burst, or a burst with one or two stamps, is a scattered splotch);
        //   * a stamp whose upstream is a water stamp no layer draws (a land branch hanging off a water node): with at least 2 x minStamps land
        //     stamps and no drawn burst within sourceSpacing / 2 (largest fragments first) it is re-rooted at a burst on its first land node
        //     (VeinKind.Burst, id <zone>.<node>.shore), else not drawn (a live cast's fragment needs only minStamps, wherever it is);
        //   * a stamp whose upstream node a live cell hides (a generated vein cut by a cast): kept with at least minStamps land stamps (it runs out
        //     of the cast's ground), else not drawn.
        // So every drawn stamp reaches a drawn burst, the water veins or a live cut through its own vein, and no drawn piece is smaller than
        // minStamps.
        private static void Components(MagicGroundPlan plan, MagicStamp[] made, RoadsPolicy.MagicPolicy p, bool waterDrawn)
        {
            var g = plan.Graph; int n = g.Stamps.Count;
            var edgeStamp = new Dictionary<int, int>(); var burst = new Dictionary<int, int>();
            for (int k = 0; k < n; k++)
            {
                var st = g.Stamps[k];
                if (st.Kind == VeinKind.Burst) burst[st.Node] = k; else if (st.Kind != VeinKind.Fork) edgeStamp[st.Node] = k;
            }
            var up = new int[n];   // the upstream stamp, -1 for a burst, -2 when its node is hidden (no stamp)
            for (int k = 0; k < n; k++)
            {
                var st = g.Stamps[k]; int at;
                if (st.Kind == VeinKind.Burst) { up[k] = -1; continue; }
                int node = st.Kind == VeinKind.Fork ? st.Node : g.Nodes[st.Node].Parent;
                bool source = st.Kind != VeinKind.Fork && g.Nodes[node].IsSource;
                up[k] = (source ? burst : edgeStamp).TryGetValue(node, out at) ? at : -2;
            }
            Func<int, bool> drawn = k => made[k] != null || (waterDrawn && g.Stamps[k].OnWater);
            // Each stamp's root: walk up while the upstream stamp is drawn.
            var top = new int[n];
            for (int k = 0; k < n; k++) { int r = k; while (up[r] >= 0 && drawn(up[r])) r = up[r]; top[k] = r; }
            var land = new Dictionary<int, int>(); var total = new Dictionary<int, int>();
            for (int k = 0; k < n; k++)
            {
                if (!drawn(k)) continue;
                int r = top[k], c;
                total.TryGetValue(r, out c); total[r] = c + 1;
                if (made[k] != null) { land.TryGetValue(r, out c); land[r] = c + 1; }
            }
            var keep = new HashSet<int>(); var shore = new Dictionary<int, MagicStamp>(); var shoreRoots = new List<KeyValuePair<int, int>>();
            var count = plan.Components;
            foreach (var kv in total.OrderBy(x => x.Key))
            {
                int r = kv.Key, landCount; land.TryGetValue(r, out landCount);
                var st = g.Stamps[r];
                string kind = landCount == 0 ? "water" : up[r] == -1 ? "burst" : up[r] == -2 ? "liveCut" : "shore";
                int size = kind == "burst" || kind == "water" ? kv.Value : landCount;
                Count(count, kind + "Components");
                if (kind == "shore" && landCount == 1) Count(count, "shoreSingles");
                if (size < p.MinStamps)
                {
                    Count(count, kind + "Dropped"); count[kind + "DroppedStamps"] = Get(count, kind + "DroppedStamps") + (kind == "water" ? kv.Value : landCount);
                    for (int k = 0; k < n; k++) if (top[k] == r && drawn(k)) plan.Dropped.Add(g.Stamps[k].Id);
                    continue;
                }
                if (kind == "water") continue;   // a water-only component: the water layer draws it
                if (kind == "shore") { shoreRoots.Add(new KeyValuePair<int, int>(r, landCount)); continue; }
                keep.Add(r);
            }
            // Shore fragments, the largest first: re-rooted at a burst on the first land node only with at least 2 x minStamps land stamps and
            // no drawn burst within sourceSpacing / 2 (a crowd of shore stars reads as the scattered splotches the veins replace); else not drawn.
            Action<int, int> drop = (r, landCount) =>
            {
                Count(count, "shoreDropped"); count["shoreDroppedStamps"] = Get(count, "shoreDroppedStamps") + landCount;
                for (int k = 0; k < n; k++) if (top[k] == r && drawn(k)) plan.Dropped.Add(g.Stamps[k].Id);
            };
            var bursts = keep.Where(r => g.Stamps[r].Kind == VeinKind.Burst).Select(r => new[] { g.Stamps[r].X, g.Stamps[r].Y }).ToList();
            foreach (var kv in shoreRoots.OrderByDescending(x => x.Value).ThenBy(x => x.Key))
            {
                int r = kv.Key, landCount = kv.Value;
                var st = g.Stamps[r];
                // The first land node of the fragment: the parent of a root trunk or tip when it lies on land, else the root's own node.
                var owner = g.Nodes[st.Node];
                var parent = st.Kind == VeinKind.Fork ? null : g.Nodes[owner.Parent];
                // MMR-247: a burst never stands where the scene shows water (a land node's point can lie under the sea or a lake plane).
                Func<VeinNode, bool> wet = v => MagicVeins.OnWaterCell(g.Source, v.X, v.Y) || MagicVeins.ShowsWater(g.Source, v.X, v.Y);
                var anchor = parent != null && !parent.OnWater && !parent.Hidden && parent.CoreRow >= 0 && !wet(parent) ? parent : owner;
                bool shared = shore.ContainsKey(anchor.Index);
                // A live cast's fragment (its spread, beside the cast node's light) needs only minStamps and may sit near other bursts.
                if (!shared && (landCount < (anchor.Live ? p.MinStamps : 2 * p.MinStamps) || anchor.CoreRow < 0 || wet(anchor)
                    || (!anchor.Live && bursts.Any(b => Math.Sqrt(Math.Pow(g.DeltaX(b[0], anchor.X), 2) + Math.Pow(b[1] - anchor.Y, 2)) < p.SourceSpacing / 2)))) { drop(r, landCount); continue; }
                if (!shared)
                {
                    var zone = g.Zones[anchor.Zone];
                    string id = (zone.Live ? "L" : "G") + zone.Key.ToString(CultureInfo.InvariantCulture) + "." + (anchor.Index - zone.FirstNode).ToString(CultureInfo.InvariantCulture) + ".shore";
                    double size = p.BurstScale * anchor.Width;
                    shore[anchor.Index] = Make(p, anchor, id, VeinKind.Burst, anchor.CoreRow, p.BurstColumn, anchor.X, anchor.Y, st.Yaw, size, size, anchor.RevealAt, anchor.Live, anchor.Alpha);
                    bursts.Add(new[] { anchor.X, anchor.Y });
                }
                Count(count, "shoreReRooted");
                keep.Add(r);
            }
            for (int k = 0; k < n; k++) if (made[k] != null && keep.Contains(top[k])) plan.Stamps.Add(made[k]);
            plan.Stamps.AddRange(shore.OrderBy(kv => kv.Key).Select(kv => kv.Value));
        }
        private static int Get(IDictionary<string, int> d, string key) { int v; return d.TryGetValue(key, out v) ? v : 0; }

        internal static string Look(IDictionary<string, string> hidden, string school) { string look; return school != null && hidden.TryGetValue(school, out look) ? look : school; }
        internal static void Count(IDictionary<string, int> d, string key) { int n; d.TryGetValue(key, out n); d[key] = n + 1; }
        // FNV-1a of the source's identity and a sample: a stable per-sample choice.
        public static uint Hash(string seed, int sample)
        {
            uint h = 2166136261;
            foreach (char ch in seed ?? "") { h ^= ch; h *= 16777619; }
            for (int k = 0; k < 4; k++) { h ^= (uint)((sample >> (8 * k)) & 255); h *= 16777619; }
            h ^= h >> 15; h *= 0x2c1b3c6d; h ^= h >> 12;
            return h;
        }
    }

    // Draws a magic ground plan on a target (the map dressing's engine target, or a recording one offline), each decal at its periodic image,
    // keyed by stamp id: Sync draws a new plan by removing the decals whose stamp changed or went and adding the new ones, so a live mutation
    // or a reveal step touches only its own zones' stamps. A decal whose material does not resolve (MMR-259's materials not published) is not
    // drawn and is counted; the engine target draws a stamp's fallback while its material's textures are not in the decal atlas (MMR-247 D).
    internal sealed class MagicGroundInstaller
    {
        private sealed class Drawn { public object Handle; public double X; public MagicStamp Stamp; public string Key; }
        private readonly IMapDressingTarget target;
        private readonly Dictionary<string, Drawn> drawn = new Dictionary<string, Drawn>(StringComparer.Ordinal);
        public readonly List<string> Errors = new List<string>();
        public readonly SortedSet<string> MissingMaterials = new SortedSet<string>(StringComparer.Ordinal);
        public Func<Dictionary<string, object>> Describe { get; set; }   // MMR-247 (D): the target's own record (materials drawn, fallbacks, the atlas)
        public MagicGroundPlan Plan { get; private set; }
        public int Created, Removed, Moved, NotDrawn, Dropped;
        public int Live { get { return drawn.Count; } }
        public MagicGroundInstaller(IMapDressingTarget target) { this.target = target; }

        public void Sync(MagicGroundPlan plan, Func<double, double> image)
        {
            var next = new Dictionary<string, MagicStamp>(StringComparer.Ordinal);
            foreach (var s in plan.Stamps) next[s.Id] = s;
            foreach (var key in drawn.Keys.ToList())
            {
                MagicStamp stamp;
                if (next.TryGetValue(key, out stamp) && stamp.Key == drawn[key].Key) continue;
                RemoveOne(key);
            }
            NotDrawn = 0;
            foreach (var s in plan.Stamps)
            {
                if (drawn.ContainsKey(s.Id)) continue;
                if (MissingMaterials.Contains(s.Decal.Material)) { NotDrawn++; continue; }
                double x = image(s.Decal.X);
                try
                {
                    var handle = target.AddDecal(s.Decal, x, s.Decal.Y);
                    if (handle == null) { MissingMaterials.Add(s.Decal.Material); NotDrawn++; continue; }
                    drawn[s.Id] = new Drawn { Handle = handle, X = x, Stamp = s, Key = s.Key }; Created++;
                }
                catch (Exception e) { NotDrawn++; if (Errors.Count < 20) Errors.Add("create " + s.Id + ": " + e.GetType().Name + ": " + e.Message); }
            }
            Plan = plan;
        }

        public void Refresh(Func<double, double> image)
        {
            foreach (var d in drawn.Values)
            {
                double x = image(d.Stamp.Decal.X);
                if (Math.Abs(x - d.X) < 1e-6) continue;
                try { target.MoveDecal(d.Handle, d.Stamp.Decal, x, d.Stamp.Decal.Y); d.X = x; Moved++; }
                catch (Exception e) { if (Errors.Count < 20) Errors.Add("move " + d.Stamp.Id + ": " + e.GetType().Name + ": " + e.Message); }
            }
        }

        // On a live scene. The handle leaves the table first, so it is never removed twice (the engine's decal set erases its end for a decal
        // it does not hold).
        private void RemoveOne(string key)
        {
            var d = drawn[key];
            drawn.Remove(key);
            try { target.RemoveDecal(d.Handle); }
            catch (Exception e) { if (Errors.Count < 20) Errors.Add("remove " + key + ": " + e.GetType().Name + ": " + e.Message); }
            Removed++;
        }

        public void Remove() { foreach (var key in drawn.Keys.ToList()) RemoveOne(key); }
        // MMR-262: lets go of every handle with no target call, for a scene the engine already cleared (MapScreen.OnFinalize's Scene.ClearAll).
        public int Drop() { int n = drawn.Count; drawn.Clear(); Dropped += n; return n; }

        public IEnumerable<KeyValuePair<MagicStamp, double>> DrawnStamps() { foreach (var d in drawn.Values) yield return new KeyValuePair<MagicStamp, double>(d.Stamp, d.X); }

        public Dictionary<string, object> Record()
        {
            var r = Plan == null ? new Dictionary<string, object>() : Plan.Record();
            r["installed"] = new Dictionary<string, object> { ["created"] = Created, ["live"] = drawn.Count, ["notDrawn"] = NotDrawn, ["moved"] = Moved, ["removed"] = Removed, ["dropped"] = Dropped,
                ["missingMaterials"] = MissingMaterials.ToList(), ["errors"] = Errors.ToList() };
            if (Describe != null) r["target"] = Describe();
            return r;
        }
    }
}
