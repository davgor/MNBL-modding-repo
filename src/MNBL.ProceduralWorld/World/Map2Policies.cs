using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace MNBL.ProceduralWorld.World
{
    // Map epic 2 (MMR-248): the strict managed readers of the policy keys the epic freezes (docs/biome-presentation.md lists every key and
    // its consumer). Each reader refuses an unknown member, a missing one and a value outside its rule with InvalidDataException, like the
    // WorldBridge readers of the same files (material_compiler.cpp, travel_graph.cpp, policy_map2.cpp). The consumers (MMR-252..287) read
    // the typed results; nothing here changes what the module does today.
    internal static class StrictJson
    {
        public static IDictionary<string, object> Parse(byte[] bytes)
        {
            var doc = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(new UTF8Encoding(false).GetString(bytes).TrimStart('﻿')) as IDictionary<string, object>;
            if (doc == null) throw new InvalidDataException("A policy must be a JSON object.");
            return doc;
        }
        public static void Only(IDictionary<string, object> d, string what, params string[] keys)
        {
            foreach (var k in d.Keys) if (Array.IndexOf(keys, k) < 0) throw new InvalidDataException(what + " has unknown member '" + k + "'.");
        }
        public static void Header(IDictionary<string, object> d, string schema, int version, string what)
        {
            if (Str(d, "schema", what) != schema || Int(d, "version", what) != version) throw new InvalidDataException(what + " must be " + schema + " version " + version + ".");
        }
        private static object Need(IDictionary<string, object> d, string key, string what)
        {
            object v;
            if (!d.TryGetValue(key, out v)) throw new InvalidDataException(what + " lacks '" + key + "'.");
            return v;
        }
        public static IDictionary<string, object> Obj(IDictionary<string, object> d, string key, string what)
        {
            var v = Need(d, key, what) as IDictionary<string, object>;
            if (v == null) throw new InvalidDataException(what + " '" + key + "' must be an object.");
            return v;
        }
        public static object[] Arr(IDictionary<string, object> d, string key, string what)
        {
            var v = Need(d, key, what) as object[];
            if (v == null) throw new InvalidDataException(what + " '" + key + "' must be an array.");
            return v;
        }
        public static string Str(IDictionary<string, object> d, string key, string what)
        {
            var v = Need(d, key, what) as string;
            if (String.IsNullOrEmpty(v)) throw new InvalidDataException(what + " '" + key + "' must be a non-empty string.");
            return v;
        }
        public static bool Bool(IDictionary<string, object> d, string key, string what)
        {
            var v = Need(d, key, what);
            if (!(v is bool)) throw new InvalidDataException(what + " '" + key + "' must be a boolean.");
            return (bool)v;
        }
        public static double Num(IDictionary<string, object> d, string key, string what)
        {
            var v = Need(d, key, what);
            if (!(v is int || v is long || v is decimal || v is double)) throw new InvalidDataException(what + " '" + key + "' must be a number.");
            return Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }
        public static int Int(IDictionary<string, object> d, string key, string what)
        {
            var v = Need(d, key, what);
            if (!(v is int || v is long)) throw new InvalidDataException(what + " '" + key + "' must be an integer.");
            return Convert.ToInt32(v, CultureInfo.InvariantCulture);
        }
        public static List<string> Strings(IDictionary<string, object> d, string key, string what, bool allowEmpty = false)
        {
            var list = new List<string>();
            foreach (var v in Arr(d, key, what))
            {
                var s = v as string;
                if (String.IsNullOrEmpty(s) || list.Contains(s)) throw new InvalidDataException(what + " '" + key + "' must hold distinct non-empty strings.");
                list.Add(s);
            }
            if (list.Count == 0 && !allowEmpty) throw new InvalidDataException(what + " '" + key + "' is empty.");
            return list;
        }
        public static void Range(bool ok, string what) { if (!ok) throw new InvalidDataException(what + " has a value outside its range."); }
        public static void Subset(IEnumerable<string> got, string what, params string[] allowed)
        {
            foreach (var g in got) if (Array.IndexOf(allowed, g) < 0) throw new InvalidDataException(what + " has unknown entry '" + g + "'.");
        }
        public static string Sha(byte[] bytes) { using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        public static byte[] Embedded(string name)
        {
            using (var stream = typeof(StrictJson).Assembly.GetManifestResourceStream("MNBL.ProceduralWorld." + name))
            {
                if (stream == null) throw new InvalidOperationException("The module has no embedded " + name + ".");
                using (var copy = new MemoryStream()) { stream.CopyTo(copy); return copy.ToArray(); }
            }
        }
    }

    // The shared biome table (worldgen/policies/terrain-materials.json version 2): face type, layer mix, battle class, colour grade and snow
    // per biome, the permanent-white mask and the live-mutation remap (MMR-254, MMR-255, MMR-256, MMR-260).
    internal sealed class BiomeTable
    {
        public const string PolicyPath = "worldgen/policies/terrain-materials.json";
        public static readonly string[] LandFaceTypes = { "Plain", "Desert", "Snow", "Forest", "Steppe", "Swamp" };
        public static readonly string[] BattleClasses = { "open", "woodland", "steppe", "rock", "cold", "desert", "wetland" };
        private static readonly int[] Grades = { 20, 40, 60, 80, 100, 120, 140 };

        public sealed class Row
        {
            public int Id; public string Core, TerrainType, BattleClass, Snow; public bool Water; public int ColourGrade = -1, ColourGradeWet = -1;
            public readonly Dictionary<string, double> Layers = new Dictionary<string, double>(StringComparer.Ordinal);
        }
        public sealed class Layer { public int Slot; public string Name, Diffuse, Normal, Specular, Height, Physics; public double Scale; public bool Optional; public int TintMode; }

        public readonly Dictionary<int, Row> Rows = new Dictionary<int, Row>();
        public readonly List<Layer> Layers = new List<Layer>();
        public List<string> PermanentWhiteBiomes, LandValid;
        public int PermanentWhiteMonths; public double PermanentWhiteThreshold; public string PermanentWhiteLayer;
        public readonly Dictionary<string, string> Schools = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> HiddenLooks = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, Dictionary<string, string>> CoreOverrides = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        public string ThresholdKey, DominanceKey;
        public string Sha256 { get; private set; }

        public static BiomeTable Installed() { return FromBytes(StrictJson.Embedded("terrain-materials.json")); }

        // The TerrainType a live mutation of school retypes a land face of natural biome core to (generated variants never retype, RT-20):
        // a hidden school takes its look school's; a core override wins. Null for an unknown school.
        public string MutatedType(string school, string core)
        {
            string look;
            if (HiddenLooks.TryGetValue(school, out look)) school = look;
            Dictionary<string, string> overrides; string type;
            if (CoreOverrides.TryGetValue(school, out overrides) && overrides.TryGetValue(core, out type)) return type;
            return Schools.TryGetValue(school, out type) ? type : null;
        }

        public static BiomeTable FromBytes(byte[] bytes)
        {
            const string what = "terrain-materials.json";
            var doc = StrictJson.Parse(bytes);
            StrictJson.Only(doc, what, "schema", "version", "biomes", "layerSet", "rules", "permanentWhite", "alpha", "flora", "mutation", "waterTypes", "river", "landforms", "intents",
                            "riverOverridesLand", "slopeRock", "coastEpsilon", "notes");
            StrictJson.Header(doc, "mnbl.terrain-materials", 2, what);
            var t = new BiomeTable();
            foreach (IDictionary<string, object> l in StrictJson.Arr(doc, "layerSet", what))
            {
                StrictJson.Only(l, what + " layerSet[]", "slot", "name", "diffuse", "normal", "specular", "height", "scale", "tint", "physics", "optional", "use");
                var layer = new Layer { Slot = StrictJson.Int(l, "slot", what), Name = StrictJson.Str(l, "name", what), Diffuse = StrictJson.Str(l, "diffuse", what),
                                        Normal = StrictJson.Str(l, "normal", what), Specular = StrictJson.Str(l, "specular", what), Height = StrictJson.Str(l, "height", what),
                                        Physics = StrictJson.Str(l, "physics", what), Scale = StrictJson.Num(l, "scale", what), Optional = StrictJson.Bool(l, "optional", what) };
                StrictJson.Str(l, "use", what);
                var tint = StrictJson.Obj(l, "tint", what);
                StrictJson.Only(tint, what + " layerSet[].tint", "mode", "colour");
                layer.TintMode = StrictJson.Int(tint, "mode", what);
                StrictJson.Range(layer.TintMode >= 0 && layer.TintMode <= 2, what + " layerSet[].tint.mode (0 none, 1 add, 2 multiply)");
                StrictJson.Range(layer.Slot == t.Layers.Count && layer.Scale > 0 && t.Layers.All(x => x.Name != layer.Name), what + " layerSet (slots from 0, unique names)");
                t.Layers.Add(layer);
            }
            StrictJson.Range(t.Layers.Count >= 1 && t.Layers.Count <= 15, what + " layerSet (1 to 15 layers)");
            var names = new HashSet<string>(t.Layers.Select(l => l.Name), StringComparer.Ordinal);
            foreach (IDictionary<string, object> b in StrictJson.Arr(doc, "biomes", what))
            {
                StrictJson.Only(b, what + " biomes[]", "id", "core", "water", "terrainType", "layers", "battleClass", "colourGrade", "colourGradeWet", "snow", "note");
                var row = new Row { Id = StrictJson.Int(b, "id", what), Core = StrictJson.Str(b, "core", what), Water = StrictJson.Bool(b, "water", what),
                                    BattleClass = StrictJson.Str(b, "battleClass", what), Snow = StrictJson.Str(b, "snow", what) };
                string w = what + " biome " + row.Core;
                if (!b.ContainsKey("terrainType") || !b.ContainsKey("colourGrade")) throw new InvalidDataException(w + " lacks terrainType or colourGrade.");
                if (row.Water != (b["terrainType"] == null) || row.Water != (b["colourGrade"] == null)) throw new InvalidDataException(w + ": terrainType and colourGrade are null exactly for water.");
                if (!row.Water)
                {
                    row.TerrainType = StrictJson.Str(b, "terrainType", w);
                    row.ColourGrade = StrictJson.Int(b, "colourGrade", w);
                    if (Array.IndexOf(LandFaceTypes, row.TerrainType) < 0 || Array.IndexOf(Grades, row.ColourGrade) < 0) throw new InvalidDataException(w + ": not a land face type or an installed grade.");
                }
                if (b.ContainsKey("colourGradeWet"))
                {
                    row.ColourGradeWet = StrictJson.Int(b, "colourGradeWet", w);
                    StrictJson.Range(!row.Water && Array.IndexOf(Grades, row.ColourGradeWet) >= 0, w + " colourGradeWet");
                }
                StrictJson.Range(Array.IndexOf(BattleClasses, row.BattleClass) >= 0 && (row.Snow == "seasonal" || row.Snow == "permanent"), w + " battleClass or snow");
                double sum = 0;
                foreach (var kv in StrictJson.Obj(b, "layers", w))
                {
                    if (!names.Contains(kv.Key)) throw new InvalidDataException(w + " mixes layer '" + kv.Key + "', which layerSet does not list.");
                    double f = Convert.ToDouble(kv.Value, CultureInfo.InvariantCulture);
                    StrictJson.Range(f > 0 && f <= 1, w + " layers");
                    row.Layers[kv.Key] = f; sum += f;
                }
                StrictJson.Range(row.Layers.Count > 0 && Math.Abs(sum - 1) <= 1e-9, w + " layers (fractions sum to 1)");
                if (t.Rows.ContainsKey(row.Id) || t.Rows.Values.Any(r => r.Core == row.Core)) throw new InvalidDataException(what + " repeats biome " + row.Id + " " + row.Core + ".");
                t.Rows[row.Id] = row;
            }
            var cores = new HashSet<string>(t.Rows.Values.Select(r => r.Core), StringComparer.Ordinal);
            StrictJson.Obj(doc, "rules", what);   // MMR-255's rules, alphas and map trees; the WorldBridge reader checks their members
            StrictJson.Obj(doc, "alpha", what);
            StrictJson.Obj(doc, "flora", what);
            var white = StrictJson.Obj(doc, "permanentWhite", what);
            StrictJson.Only(white, what + " permanentWhite", "biomes", "snowMonths", "snowThreshold", "layer", "terrainType");
            t.PermanentWhiteBiomes = StrictJson.Strings(white, "biomes", what);
            t.PermanentWhiteMonths = StrictJson.Int(white, "snowMonths", what);
            t.PermanentWhiteThreshold = StrictJson.Num(white, "snowThreshold", what);
            t.PermanentWhiteLayer = StrictJson.Str(white, "layer", what);
            StrictJson.Range(t.PermanentWhiteMonths >= 1 && t.PermanentWhiteMonths <= 12 && t.PermanentWhiteThreshold > 0 && t.PermanentWhiteThreshold <= 1 &&
                             names.Contains(t.PermanentWhiteLayer) && StrictJson.Str(white, "terrainType", what) == "Snow" && t.PermanentWhiteBiomes.All(cores.Contains), what + " permanentWhite");
            foreach (var row in t.Rows.Values)
                if ((row.Snow == "permanent") != t.PermanentWhiteBiomes.Contains(row.Core)) throw new InvalidDataException(what + " biome " + row.Core + ": snow is permanent exactly in permanentWhite.");
            var mutation = StrictJson.Obj(doc, "mutation", what);
            StrictJson.Only(mutation, what + " mutation", "landValid", "schools", "coreOverrides", "hiddenLooks", "generatedVariants", "battleClass", "thresholds");
            t.LandValid = StrictJson.Strings(mutation, "landValid", what);
            StrictJson.Subset(t.LandValid, what + " mutation.landValid (never Dune)", LandFaceTypes);
            foreach (var kv in StrictJson.Obj(mutation, "schools", what))
            {
                var type = kv.Value as string;
                if (!t.LandValid.Contains(type)) throw new InvalidDataException(what + " mutation school " + kv.Key + " remaps outside landValid.");
                t.Schools[kv.Key] = type;
            }
            foreach (var kv in StrictJson.Obj(mutation, "coreOverrides", what))
            {
                var inner = kv.Value as IDictionary<string, object>;
                if (inner == null || !t.Schools.ContainsKey(kv.Key)) throw new InvalidDataException(what + " mutation coreOverrides names unknown school " + kv.Key + ".");
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var o in inner)
                {
                    var type = o.Value as string;
                    if (!cores.Contains(o.Key) || !t.LandValid.Contains(type)) throw new InvalidDataException(what + " mutation coreOverrides " + kv.Key + "/" + o.Key + " is outside the table.");
                    map[o.Key] = type;
                }
                t.CoreOverrides[kv.Key] = map;
            }
            foreach (var kv in StrictJson.Obj(mutation, "hiddenLooks", what))
            {
                var look = kv.Value as string;
                if (t.Schools.ContainsKey(kv.Key) || look == null || !t.Schools.ContainsKey(look)) throw new InvalidDataException(what + " mutation hiddenLooks maps a hidden school to a visible one.");
                t.HiddenLooks[kv.Key] = look;
            }
            StrictJson.Range(StrictJson.Str(mutation, "generatedVariants", what) == "visual" && StrictJson.Str(mutation, "battleClass", what) == "faceClass", what + " mutation (RT-20, RT-26)");
            var thresholds = StrictJson.Obj(mutation, "thresholds", what);
            StrictJson.Only(thresholds, what + " mutation.thresholds", "source", "threshold", "dominance");
            StrictJson.Range(StrictJson.Str(thresholds, "source", what) == "world.json", what + " mutation.thresholds.source");
            t.ThresholdKey = StrictJson.Str(thresholds, "threshold", what); t.DominanceKey = StrictJson.Str(thresholds, "dominance", what);
            t.Sha256 = StrictJson.Sha(bytes);
            return t;
        }
    }

    // worldgen/policies/roads.json (module-embedded): road decals and bridges (MMR-257), water decals (MMR-285) and the one speed model's
    // factors (MMR-254).
    internal sealed class RoadsPolicy
    {
        public const string PolicyPath = "worldgen/policies/roads.json";
        public readonly Dictionary<string, List<string>> DecalFamilies = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> CultureFamilies = new Dictionary<string, string>(StringComparer.Ordinal);
        public double DecalSpacing, DecalSize, GateClip, BridgeScale; public int SmoothingPasses, MaxDecals;
        public List<string> BridgePrefabs, SpeedChain; public string LShaped;
        public double RoadFactor; public bool RoadAtSea, CancelFordOnDecks;
        // MMR-285, MMR-247: the water decals (roads.json water): the published water material's atlas (a column per stamp kind, a row per
        // variant) and tint, the tinted vanilla stamp drawn while that material does not resolve, and the strips' and fills' geometry.
        public string WaterMaterial, WaterFallbackMaterial, WaterFallbackTexture; public uint WaterTint, WaterFallbackTint; public float[] WaterFallbackAtlas;
        public int WaterColumns, WaterRows, RiverColumn, PoolColumn, RiverSmoothingPasses;
        public double RiverWidth, RiverSpacing, FillScale, OpaqueCore; public int WaterMaxDecals;
        // MMR-247 (D): the least half depth the engine target draws a water stamp's box with (0: FitBox's own, W's tight boxes; the decal lab's
        // fit row says whether a box a few centimetres deep still paints the rendered terrain).
        public double WaterMinHalfDepth;
        public float[] WaterCell(int column, int row) { return new[] { 1f / WaterColumns, 1f / WaterRows, (float)column / WaterColumns, (float)row / WaterRows }; }
        public readonly Dictionary<string, double> TerrainFactors = new Dictionary<string, double>(StringComparer.Ordinal);
        // MMR-247 (D): the campaign map's decal atlas group (roads.json decalAtlas; World/DecalAtlas.cs) and whether a material whose diffuse
        // texture has no rect in it draws anyway (unresident "draw") or gives way to its decal's fallback ("fallback").
        public string DecalAtlasGroup; public bool DrawUnresident;
        // MMR-247 (D): a listed vanilla stamp a decal draws while its material's textures are not in the decal atlas (roads.json magic.fallback):
        // its material, diffuse texture (checked against the atlas list offline), atlas cell, the mean colour of its opaque core, and its box:
        // turned a quarter (the stamp runs along the texture's v), across x the decal's width and along x its length.
        public sealed class StampFallback { public string Material, Texture; public float[] Atlas; public int[] Core; public bool Turn; public double Along, Across; }
        // MMR-260: the magic ground overlay (roads.json magic; docs/magic-ground-textures.md "What MMR-260 uses").
        public sealed class MagicPolicy
        {
            // MMR-247 (D): the vein stamps' fallback (strokes for trunks, forks and tips; the burst stamp) and each visible school's colour.
            public StampFallback StrokeFallback, BurstFallback;
            public readonly Dictionary<string, int[]> FallbackColours = new Dictionary<string, int[]>(StringComparer.Ordinal);
            public string MaterialPrefix, ColourGrade; public List<string> Schools;
            public readonly List<KeyValuePair<string, List<string>>> Rows = new List<KeyValuePair<string, List<string>>>();   // atlas row -> (group, cores)
            public int TrunkColumn, ForkColumn, TipColumn, BurstColumn; public bool FlipV, FlipU;
            // MMR-288 veins: branch spacing, stamp length (growth step 0.75 x), widths (box width across the vein), pipe exponent, burst size per
            // source width, the least distance between a zone's sources; a live cast's reveal time in campaign hours. Gaps fix: the least stamps a
            // drawn vein component keeps (smaller ones, and shore fragments with no water layer, are not drawn), the potency band above the
            // threshold over which a stamp's alpha fades in from minAlpha (SetFactor1's alpha), the samples from a zone's rim over which veins
            // widen and fade in.
            public double Spacing, StampLength, Width, MinWidth, ForkExponent, BurstScale, SourceSpacing, RevealHours, FadeBand, MinAlpha, EdgeSamples; public int MaxDecals, MinStamps;
            public readonly Dictionary<string, string> NodeParticles = new Dictionary<string, string>(StringComparer.Ordinal);
            public double NodeLightRadius, NodeLightIntensity, NodeHeight;
            public int RowOf(string core) { for (int i = 0; i < Rows.Count; i++) if (Rows[i].Value.Contains(core)) return i; return -1; }
            public string Particle(string school) { string p; return NodeParticles.TryGetValue(school ?? "", out p) ? p : NodeParticles["default"]; }
        }
        public MagicPolicy Magic;
        // MMR-289: the magic features (roads.json magicFeatures; docs/magic-features.md): per visible school a source feature, trunk features and
        // debris, each a set of installed vanilla meta meshes (sizes from their .tpac bounding boxes, metres at scale 1) with multiply tints, the
        // source's light and particles; where they go along the vein graph, what they keep clear of and the entity budget.
        public sealed class FeaturesPolicy
        {
            public sealed class Mesh
            {
                public string Name; public double[] Min, Max; public bool Flip;
                // The farthest footprint corner from the origin, and the lowest point (flipped: the mirror of the highest), at scale 1.
                public double Radius { get { double x = Math.Max(Math.Abs(Min[0]), Math.Abs(Max[0])), y = Math.Max(Math.Abs(Min[1]), Math.Abs(Max[1])); return Math.Sqrt(x * x + y * y); } }
                public double Low { get { return Flip ? -Max[2] : Min[2]; } }
                public double Height { get { return Max[2] - Min[2]; } }
            }
            public sealed class Part { public List<string> Meshes; public double ScaleMin, ScaleMax, RingMin, RingMax, LeanMin, LeanMax; public int CountMin, CountMax; public List<uint> Tints; }
            public sealed class Glow { public double R, G, B, Radius, Intensity; public bool Flicker; }
            public sealed class Feature { public string Name; public List<Part> Parts = new List<Part>(); public List<string> Particles = new List<string>(); public Glow Light; }
            public sealed class School { public string Lean; public Feature Source; public List<Feature> Trunk, Debris; }
            public readonly Dictionary<string, Mesh> Meshes = new Dictionary<string, Mesh>(StringComparer.Ordinal);
            public readonly Dictionary<string, School> Schools = new Dictionary<string, School>(StringComparer.Ordinal);
            public double TrunkWidth, LiveTrunkWidth, TrunkSpacing, EdgeThinning, LateralMin, LateralMax, DebrisWidth, MinGap, Sink, EffectHeight; public int SourceSearch;
            public double SettlementClearance, RuinClearance, RoadClearance, BridgeClearance, WaterClearance;
            public int MaxRoots, MaxEntities, MaxLights, MaxParticles, WindowRoots, WindowEntities, WindowLights, WindowParticles; public double WindowWidth, WindowHeight;
        }
        public FeaturesPolicy Features;
        // MMR-290: magic water, the vein graph's water stamps (roads.json magicWater; docs/magic-water.md).
        public sealed class MagicWaterPolicy
        {
            public const string Surface = "surface", Bed = "bed", Shore = "shore";
            public string Material, FallbackPrefix, Mode; public int Columns, Rows, FallbackRow, MaxDecals;
            public readonly Dictionary<string, int> SchoolRows = new Dictionary<string, int>(StringComparer.Ordinal);   // look school -> atlas row
            public double WidthScale, HalfDepth, Opacity, BedMaxDepth;
        }
        public MagicWaterPolicy MagicWater;
        // MMR-291: veins, rivers and magic water as terrain overlay meshes (roads.json overlayMeshes; docs/overlay-meshes.md). Per layer a mode
        // (mesh: the layer's decals are not drawn; decal: the decals as before), its material (a published name tried first, else a runtime copy
        // of a vanilla template with the layer's published textures; rivers: vanilla water materials in order), its lift over the surface it
        // follows; the chunk grid, the draping tolerance and the budgets.
        public sealed class OverlayPolicy
        {
            public const string Mesh = "mesh", Decal = "decal";
            public bool Enabled, FlipV; public double Chunk, MaxSegment, MinSegment, MaxError;
            public int MaxVertices, MaxTriangles, MaxEntities, ViewEntities, ViewTriangles; public double ViewWidth, ViewHeight, EntityMicroseconds;
            public string VeinMode, VeinMaterial, VeinTemplate, VeinTextures; public double VeinLift; public int BurstSegments, BurstRings;
            public readonly Dictionary<string, double> VeinGlow = new Dictionary<string, double>(StringComparer.Ordinal);   // look school -> self-illumination
            public string WaterMode, WaterMaterial, WaterTemplate, WaterTextures; public double WaterLift, WaterGlow;
            public string RiverMode; public List<string> RiverMaterials; public double RiverWidth, RiverDepth, RiverDip, RiverLift, PoolScale; public bool RiverFills; public int DiscSegments;
            // MMR-291-W: how the vein and magic water materials draw against the map's water (OverlayPass in World/OverlayMeshes.cs): blend (the
            // template as it is), depth (writes depth behind an alpha test at alphaTest), forward, afterPostfx.
            public const string Blend = "blend", Depth = "depth", Forward = "forward", AfterPostfx = "afterPostfx";
            public static readonly string[] Passes = { Blend, Depth, Forward, AfterPostfx };
            public string VeinPass, WaterPass; public double AlphaTest;
            public string Pass(string layer) { return layer == "veins" ? VeinPass : layer == "magicWater" ? WaterPass : Blend; }
            public bool DrawsVeins { get { return Enabled && VeinMode == Mesh; } }
            public bool DrawsMagicWater { get { return Enabled && WaterMode == Mesh; } }
            public bool DrawsRivers { get { return Enabled && RiverMode == Mesh; } }
            public double Glow(string look) { double g; return look != null && VeinGlow.TryGetValue(look, out g) ? g : 0; }
            // A {look} name ("mnbl_magic_{look}") for a look school.
            public static string Named(string pattern, string look) { return pattern.Replace("{look}", look ?? ""); }
        }
        public OverlayPolicy Overlay;
        public string Sha256 { get; private set; }

        public static RoadsPolicy Installed() { return FromBytes(StrictJson.Embedded("roads.json")); }
        public static RoadsPolicy FromBytes(byte[] bytes)
        {
            const string what = "roads.json";
            var doc = StrictJson.Parse(bytes);
            StrictJson.Only(doc, what, "schema", "version", "decals", "bridges", "water", "magic", "decalAtlas", "magicFeatures", "magicWater", "overlayMeshes", "speed", "notes");
            StrictJson.Header(doc, "mnbl.roads", 1, what);
            var p = new RoadsPolicy();
            var decals = StrictJson.Obj(doc, "decals", what);
            StrictJson.Only(decals, what + " decals", "families", "cultureFamilies", "spacing", "size", "smoothingPasses", "gateClip", "maxDecals", "seam");
            foreach (var kv in StrictJson.Obj(decals, "families", what)) p.DecalFamilies[kv.Key] = StrictJson.Strings(StrictJson.Obj(decals, "families", what), kv.Key, what + " decals.families");
            foreach (var kv in StrictJson.Obj(decals, "cultureFamilies", what))
            {
                var family = kv.Value as string;
                if (family == null || !p.DecalFamilies.ContainsKey(family)) throw new InvalidDataException(what + " decals.cultureFamilies " + kv.Key + " names no decal family.");
                p.CultureFamilies[kv.Key] = family;
            }
            p.DecalSpacing = StrictJson.Num(decals, "spacing", what); p.DecalSize = StrictJson.Num(decals, "size", what); p.GateClip = StrictJson.Num(decals, "gateClip", what);
            p.SmoothingPasses = StrictJson.Int(decals, "smoothingPasses", what); p.MaxDecals = StrictJson.Int(decals, "maxDecals", what);
            StrictJson.Range(p.DecalFamilies.Count > 0 && p.DecalSpacing > 0 && p.DecalSize > 0 && p.GateClip >= 0 && p.SmoothingPasses >= 0 && p.MaxDecals > 0 &&
                             StrictJson.Str(decals, "seam", what) == "nearestImage", what + " decals");
            var bridges = StrictJson.Obj(doc, "bridges", what);
            StrictJson.Only(bridges, what + " bridges", "prefabs", "source", "z", "lShaped", "scale");
            p.BridgePrefabs = StrictJson.Strings(bridges, "prefabs", what);
            p.BridgeScale = StrictJson.Num(bridges, "scale", what);
            StrictJson.Str(bridges, "source", what);
            var lShaped = p.LShaped = StrictJson.Str(bridges, "lShaped", what);
            StrictJson.Range(p.BridgeScale > 0 && StrictJson.Str(bridges, "z", what) == "bankGround" && (lShaped == "rotate" || lShaped == "skip"), what + " bridges");
            var water = StrictJson.Obj(doc, "water", what);
            Func<object[], string, uint> argb = (rgb, at) =>
            {
                var v = rgb.Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray();
                StrictJson.Range(v.Length == 3 && v.All(c => c >= 0 && c <= 255 && c == Math.Floor(c)), at + " (r, g, b in 0..255)");
                return 0xFF000000u | ((uint)v[0] << 16) | ((uint)v[1] << 8) | (uint)v[2];
            };
            StrictJson.Only(water, what + " water", "material", "atlas", "tint", "fallback", "riverWidth", "riverSpacing", "smoothingPasses", "fillScale", "opaqueCore", "maxDecals", "minHalfDepth", "seam", "note");
            p.WaterMaterial = StrictJson.Str(water, "material", what);
            var cellsOf = StrictJson.Obj(water, "atlas", what);
            StrictJson.Only(cellsOf, what + " water.atlas", "columns", "rows", "riverColumn", "poolColumn");
            p.WaterColumns = StrictJson.Int(cellsOf, "columns", what); p.WaterRows = StrictJson.Int(cellsOf, "rows", what);
            p.RiverColumn = StrictJson.Int(cellsOf, "riverColumn", what); p.PoolColumn = StrictJson.Int(cellsOf, "poolColumn", what);
            p.WaterTint = argb(StrictJson.Arr(water, "tint", what), what + " water.tint");
            var fallback = StrictJson.Obj(water, "fallback", what);
            StrictJson.Only(fallback, what + " water.fallback", "material", "texture", "atlas", "tint");
            p.WaterFallbackMaterial = StrictJson.Str(fallback, "material", what); p.WaterFallbackTexture = StrictJson.Str(fallback, "texture", what);
            var atlas = StrictJson.Arr(fallback, "atlas", what);
            if (atlas.Length != 4) throw new InvalidDataException(what + " water.fallback.atlas holds 4 numbers (scaleU, scaleV, offsetU, offsetV).");
            p.WaterFallbackAtlas = atlas.Select(v => Convert.ToSingle(v, CultureInfo.InvariantCulture)).ToArray();
            p.WaterFallbackTint = argb(StrictJson.Arr(fallback, "tint", what), what + " water.fallback.tint");
            p.RiverWidth = StrictJson.Num(water, "riverWidth", what); p.RiverSpacing = StrictJson.Num(water, "riverSpacing", what); p.RiverSmoothingPasses = StrictJson.Int(water, "smoothingPasses", what);
            p.FillScale = StrictJson.Num(water, "fillScale", what); p.OpaqueCore = StrictJson.Num(water, "opaqueCore", what); p.WaterMaxDecals = StrictJson.Int(water, "maxDecals", what);
            p.WaterMinHalfDepth = StrictJson.Num(water, "minHalfDepth", what);
            StrictJson.Range(p.WaterMinHalfDepth >= 0 && p.WaterMinHalfDepth <= 3, what + " water.minHalfDepth (0..3)");
            StrictJson.Range(p.WaterMaterial != p.WaterFallbackMaterial && p.WaterColumns >= 2 && p.WaterRows >= 1 && p.RiverColumn != p.PoolColumn && p.RiverColumn >= 0 && p.RiverColumn < p.WaterColumns
                             && p.PoolColumn >= 0 && p.PoolColumn < p.WaterColumns && p.RiverWidth > 0 && p.RiverSpacing > 0 && p.RiverSmoothingPasses >= 0 && p.RiverSmoothingPasses <= 3
                             && p.FillScale > 0 && p.FillScale <= 1 && p.OpaqueCore > 0 && p.OpaqueCore <= 1 && p.WaterMaxDecals > 0 && StrictJson.Str(water, "seam", what) == "nearestImage", what + " water");
            p.Magic = ReadMagic(StrictJson.Obj(doc, "magic", what), what + " magic");
            var decalAtlas = StrictJson.Obj(doc, "decalAtlas", what);
            StrictJson.Only(decalAtlas, what + " decalAtlas", "group", "unresident", "note");
            p.DecalAtlasGroup = StrictJson.Str(decalAtlas, "group", what);
            string unresident = StrictJson.Str(decalAtlas, "unresident", what);
            StrictJson.Range(p.DecalAtlasGroup == "worldmap" && (unresident == "fallback" || unresident == "draw"), what + " decalAtlas (group worldmap, the campaign map's; unresident fallback or draw)");
            p.DrawUnresident = unresident == "draw";
            p.Features = ReadFeatures(StrictJson.Obj(doc, "magicFeatures", what), what + " magicFeatures", p.Magic);
            p.MagicWater = ReadMagicWater(StrictJson.Obj(doc, "magicWater", what), what + " magicWater", p.Magic);
            p.Overlay = ReadOverlay(StrictJson.Obj(doc, "overlayMeshes", what), what + " overlayMeshes", p.Magic);
            var speed = StrictJson.Obj(doc, "speed", what);
            StrictJson.Only(speed, what + " speed", "chain", "road", "roadAtSea", "cancelFordOnDecks", "terrain");
            p.SpeedChain = StrictJson.Strings(speed, "chain", what);
            if (!p.SpeedChain.SequenceEqual(new[] { "Road", "Naval", "Default" })) throw new InvalidDataException(what + " speed.chain is Road, Naval, Default (MMR-254).");
            p.RoadFactor = StrictJson.Num(speed, "road", what); p.RoadAtSea = StrictJson.Bool(speed, "roadAtSea", what); p.CancelFordOnDecks = StrictJson.Bool(speed, "cancelFordOnDecks", what);
            foreach (var kv in StrictJson.Obj(speed, "terrain", what))
            {
                if (Array.IndexOf(BiomeTable.LandFaceTypes, kv.Key) < 0) throw new InvalidDataException(what + " speed.terrain names " + kv.Key + ", not a land face type.");
                p.TerrainFactors[kv.Key] = Convert.ToDouble(kv.Value, CultureInfo.InvariantCulture);
            }
            StrictJson.Range(p.RoadFactor >= 0 && p.RoadFactor < 1 && p.TerrainFactors.Values.All(f => f > -1 && f < 1), what + " speed");
            p.Sha256 = StrictJson.Sha(bytes);
            return p;
        }

        private static MagicWaterPolicy ReadMagicWater(IDictionary<string, object> water, string what, MagicPolicy magic)
        {
            StrictJson.Only(water, what, "material", "atlas", "fallback", "mode", "halfDepth", "opacity", "bedMaxDepth", "maxDecals", "seam");
            var w = new MagicWaterPolicy { Material = StrictJson.Str(water, "material", what), Mode = StrictJson.Str(water, "mode", what) };
            var atlas = StrictJson.Obj(water, "atlas", what);
            StrictJson.Only(atlas, what + ".atlas", "columns", "rows", "schoolRows", "widthScale");
            w.Columns = StrictJson.Int(atlas, "columns", what); w.Rows = StrictJson.Int(atlas, "rows", what); w.WidthScale = StrictJson.Num(atlas, "widthScale", what);
            foreach (var kv in StrictJson.Obj(atlas, "schoolRows", what)) w.SchoolRows[kv.Key] = StrictJson.Int(StrictJson.Obj(atlas, "schoolRows", what), kv.Key, what + ".atlas.schoolRows");
            var fallback = StrictJson.Obj(water, "fallback", what);
            StrictJson.Only(fallback, what + ".fallback", "materialPrefix", "row");
            w.FallbackPrefix = StrictJson.Str(fallback, "materialPrefix", what); w.FallbackRow = StrictJson.Int(fallback, "row", what);
            w.HalfDepth = StrictJson.Num(water, "halfDepth", what); w.Opacity = StrictJson.Num(water, "opacity", what); w.BedMaxDepth = StrictJson.Num(water, "bedMaxDepth", what);
            w.MaxDecals = StrictJson.Int(water, "maxDecals", what);
            // Every look school has its own row (a hidden school takes its look's), the kinds fit the columns, the fallback is the ground material of the look.
            StrictJson.Range(w.Material.Length > 0 && !w.Material.StartsWith(magic.MaterialPrefix, StringComparison.Ordinal) && w.Columns == 4 && w.Rows >= magic.Schools.Count &&
                             w.SchoolRows.Count == magic.Schools.Count && magic.Schools.All(w.SchoolRows.ContainsKey) && w.SchoolRows.Values.Distinct().Count() == w.SchoolRows.Count &&
                             w.SchoolRows.Values.All(r => r >= 0 && r < w.Rows) && w.WidthScale > 0 && w.FallbackPrefix == magic.MaterialPrefix && w.FallbackRow >= 0 && w.FallbackRow < magic.Rows.Count &&
                             (w.Mode == MagicWaterPolicy.Surface || w.Mode == MagicWaterPolicy.Bed || w.Mode == MagicWaterPolicy.Shore) &&
                             w.HalfDepth > 0 && w.Opacity > 0 && w.Opacity <= 1 && w.BedMaxDepth >= 0 && w.MaxDecals > 0 && StrictJson.Str(water, "seam", what) == "nearestImage", what);
            return w;
        }

        // MMR-291: roads.json overlayMeshes (docs/overlay-meshes.md).
        private static OverlayPolicy ReadOverlay(IDictionary<string, object> o, string what, MagicPolicy magic)
        {
            StrictJson.Only(o, what, "enabled", "chunk", "segment", "budget", "flipV", "alphaTest", "veins", "magicWater", "rivers", "seam", "note");
            var p = new OverlayPolicy { Enabled = StrictJson.Bool(o, "enabled", what), Chunk = StrictJson.Num(o, "chunk", what), FlipV = StrictJson.Bool(o, "flipV", what), AlphaTest = StrictJson.Num(o, "alphaTest", what) };
            var segment = StrictJson.Obj(o, "segment", what);
            StrictJson.Only(segment, what + ".segment", "max", "min", "maxError");
            p.MaxSegment = StrictJson.Num(segment, "max", what); p.MinSegment = StrictJson.Num(segment, "min", what); p.MaxError = StrictJson.Num(segment, "maxError", what);
            var budget = StrictJson.Obj(o, "budget", what);
            StrictJson.Only(budget, what + ".budget", "vertices", "triangles", "entities", "view", "entityMicroseconds");
            p.MaxVertices = StrictJson.Int(budget, "vertices", what); p.MaxTriangles = StrictJson.Int(budget, "triangles", what); p.MaxEntities = StrictJson.Int(budget, "entities", what);
            p.EntityMicroseconds = StrictJson.Num(budget, "entityMicroseconds", what);
            var view = StrictJson.Obj(budget, "view", what);
            StrictJson.Only(view, what + ".budget.view", "width", "height", "entities", "triangles");
            p.ViewWidth = StrictJson.Num(view, "width", what); p.ViewHeight = StrictJson.Num(view, "height", what); p.ViewEntities = StrictJson.Int(view, "entities", what); p.ViewTriangles = StrictJson.Int(view, "triangles", what);
            Func<string, bool> mode = m => m == OverlayPolicy.Mesh || m == OverlayPolicy.Decal;
            var veins = StrictJson.Obj(o, "veins", what);
            StrictJson.Only(veins, what + ".veins", "mode", "material", "template", "textures", "lift", "pass", "burst", "glow");
            p.VeinMode = StrictJson.Str(veins, "mode", what); p.VeinMaterial = StrictJson.Str(veins, "material", what); p.VeinTemplate = StrictJson.Str(veins, "template", what);
            p.VeinPass = StrictJson.Str(veins, "pass", what);
            p.VeinTextures = StrictJson.Str(veins, "textures", what); p.VeinLift = StrictJson.Num(veins, "lift", what);
            var burst = StrictJson.Obj(veins, "burst", what);
            StrictJson.Only(burst, what + ".veins.burst", "segments", "rings");
            p.BurstSegments = StrictJson.Int(burst, "segments", what); p.BurstRings = StrictJson.Int(burst, "rings", what);
            var glow = StrictJson.Obj(veins, "glow", what);
            foreach (var kv in glow) p.VeinGlow[kv.Key] = StrictJson.Num(glow, kv.Key, what + ".veins.glow");
            var water = StrictJson.Obj(o, "magicWater", what);
            StrictJson.Only(water, what + ".magicWater", "mode", "material", "template", "textures", "lift", "pass", "glow");
            p.WaterMode = StrictJson.Str(water, "mode", what); p.WaterMaterial = StrictJson.Str(water, "material", what); p.WaterTemplate = StrictJson.Str(water, "template", what);
            p.WaterTextures = StrictJson.Str(water, "textures", what); p.WaterLift = StrictJson.Num(water, "lift", what); p.WaterGlow = StrictJson.Num(water, "glow", what);
            p.WaterPass = StrictJson.Str(water, "pass", what);
            var rivers = StrictJson.Obj(o, "rivers", what);
            StrictJson.Only(rivers, what + ".rivers", "mode", "materials", "width", "depth", "dip", "lift", "poolScale", "fills", "discSegments");
            p.RiverMode = StrictJson.Str(rivers, "mode", what); p.RiverMaterials = StrictJson.Strings(rivers, "materials", what);
            p.RiverWidth = StrictJson.Num(rivers, "width", what); p.RiverDepth = StrictJson.Num(rivers, "depth", what); p.RiverDip = StrictJson.Num(rivers, "dip", what);
            p.RiverLift = StrictJson.Num(rivers, "lift", what); p.PoolScale = StrictJson.Num(rivers, "poolScale", what); p.RiverFills = StrictJson.Bool(rivers, "fills", what);
            p.DiscSegments = StrictJson.Int(rivers, "discSegments", what);
            // The vein glow names every visible school once; the vein textures and material carry {look}; the magic water and river materials
            // are not decal materials of the ground; draping stays finer than the chunk and lifts clear the draping tolerance.
            StrictJson.Range(p.Chunk >= 16 && p.Chunk <= 512 && p.MinSegment > 0 && p.MinSegment <= p.MaxSegment && p.MaxSegment <= p.Chunk / 4 && p.MaxError > 0 &&
                             p.MaxVertices > 0 && p.MaxTriangles > 0 && p.MaxEntities > 0 && p.ViewWidth > 0 && p.ViewHeight > 0 && p.ViewEntities > 0 && p.ViewTriangles > 0 && p.EntityMicroseconds > 0 &&
                             mode(p.VeinMode) && mode(p.WaterMode) && mode(p.RiverMode) && p.VeinTextures.Contains("{look}") && p.VeinMaterial.Contains("{look}") &&
                             !p.VeinMaterial.StartsWith(magic.MaterialPrefix, StringComparison.Ordinal) && p.VeinTemplate.Length > 0 && p.WaterTemplate.Length > 0 &&
                             p.WaterMaterial.Length > 0 && p.WaterTextures.Length > 0 && !p.WaterMaterial.Contains("{look}") &&
                             p.VeinLift > p.MaxError && p.WaterLift > 0 && p.BurstSegments >= 6 && p.BurstRings >= 1 &&
                             p.VeinGlow.Count == magic.Schools.Count && magic.Schools.All(p.VeinGlow.ContainsKey) && p.VeinGlow.Values.All(g => g >= 0 && g <= 4) && p.WaterGlow >= 0 && p.WaterGlow <= 4 &&
                             p.RiverMaterials.Count >= 1 && p.RiverMaterials.All(m => m.Length > 0) && p.RiverWidth > 0 && p.RiverDepth > 0 && p.RiverDip >= 0 && p.RiverLift >= 0 && p.PoolScale > 0 &&
                             p.DiscSegments >= 6 && StrictJson.Str(o, "seam", what) == "chunkImage" &&
                             Array.IndexOf(OverlayPolicy.Passes, p.VeinPass) >= 0 && Array.IndexOf(OverlayPolicy.Passes, p.WaterPass) >= 0 && p.AlphaTest > 0 && p.AlphaTest < 1, what);
            return p;
        }

        // An (r, g, b) of whole numbers in 0..255.
        private static int[] Rgb(object[] rgb, string what)
        {
            var v = rgb.Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray();
            StrictJson.Range(v.Length == 3 && v.All(c => c >= 0 && c <= 255 && c == Math.Floor(c)), what + " (r, g, b in 0..255)");
            return v.Select(c => (int)c).ToArray();
        }

        // MMR-247 (D): roads.json magic.fallback stroke / burst: a vanilla stamp (material, its diffuse texture, atlas cell, core colour, box).
        private static StampFallback ReadStampFallback(IDictionary<string, object> f, string what)
        {
            StrictJson.Only(f, what, "material", "texture", "atlas", "core", "turn", "along", "across");
            var s = new StampFallback { Material = StrictJson.Str(f, "material", what), Texture = StrictJson.Str(f, "texture", what), Core = Rgb(StrictJson.Arr(f, "core", what), what + ".core"),
                Turn = StrictJson.Bool(f, "turn", what), Along = StrictJson.Num(f, "along", what), Across = StrictJson.Num(f, "across", what) };
            var atlas = StrictJson.Arr(f, "atlas", what);
            if (atlas.Length != 4) throw new InvalidDataException(what + ".atlas holds 4 numbers (scaleU, scaleV, offsetU, offsetV).");
            s.Atlas = atlas.Select(v => Convert.ToSingle(v, CultureInfo.InvariantCulture)).ToArray();
            StrictJson.Range(s.Material.Length > 0 && s.Texture.Length > 0 && s.Along > 0 && s.Across > 0 && s.Atlas[0] > 0 && s.Atlas[1] > 0 && s.Core.All(c => c > 0), what);
            return s;
        }

        private static MagicPolicy ReadMagic(IDictionary<string, object> magic, string what)
        {
            StrictJson.Only(magic, what, "materialPrefix", "schools", "atlas", "veins", "revealHours", "maxDecals", "seam", "node", "colourGrade", "fallback");
            var m = new MagicPolicy { MaterialPrefix = StrictJson.Str(magic, "materialPrefix", what), Schools = StrictJson.Strings(magic, "schools", what), ColourGrade = StrictJson.Str(magic, "colourGrade", what) };
            var atlas = StrictJson.Obj(magic, "atlas", what);
            StrictJson.Only(atlas, what + ".atlas", "rows", "columns", "flipV", "flipU");
            var cores = new HashSet<string>(StringComparer.Ordinal);
            foreach (var o in StrictJson.Arr(atlas, "rows", what))
            {
                var row = o as IDictionary<string, object>;
                if (row == null) throw new InvalidDataException(what + ".atlas.rows holds a non-object.");
                StrictJson.Only(row, what + ".atlas.rows[]", "group", "cores");
                var list = StrictJson.Strings(row, "cores", what);
                foreach (var c in list) if (!cores.Add(c)) throw new InvalidDataException(what + ".atlas.rows names core " + c + " twice.");
                m.Rows.Add(new KeyValuePair<string, List<string>>(StrictJson.Str(row, "group", what), list));
            }
            var columns = StrictJson.Obj(atlas, "columns", what);
            StrictJson.Only(columns, what + ".atlas.columns", "trunk", "fork", "tip", "burst");
            m.TrunkColumn = StrictJson.Int(columns, "trunk", what); m.ForkColumn = StrictJson.Int(columns, "fork", what); m.TipColumn = StrictJson.Int(columns, "tip", what); m.BurstColumn = StrictJson.Int(columns, "burst", what);
            m.FlipV = StrictJson.Bool(atlas, "flipV", what); m.FlipU = StrictJson.Bool(atlas, "flipU", what);
            var veins = StrictJson.Obj(magic, "veins", what);
            StrictJson.Only(veins, what + ".veins", "spacing", "stampLength", "width", "minWidth", "forkExponent", "burstScale", "sourceSpacing", "minStamps", "fadeBand", "minAlpha", "edgeSamples");
            m.Spacing = StrictJson.Num(veins, "spacing", what); m.StampLength = StrictJson.Num(veins, "stampLength", what); m.Width = StrictJson.Num(veins, "width", what);
            m.MinWidth = StrictJson.Num(veins, "minWidth", what); m.ForkExponent = StrictJson.Num(veins, "forkExponent", what); m.BurstScale = StrictJson.Num(veins, "burstScale", what);
            m.SourceSpacing = StrictJson.Num(veins, "sourceSpacing", what); m.MinStamps = StrictJson.Int(veins, "minStamps", what);
            m.FadeBand = StrictJson.Num(veins, "fadeBand", what); m.MinAlpha = StrictJson.Num(veins, "minAlpha", what); m.EdgeSamples = StrictJson.Num(veins, "edgeSamples", what);
            m.RevealHours = StrictJson.Num(magic, "revealHours", what); m.MaxDecals = StrictJson.Int(magic, "maxDecals", what);
            var node = StrictJson.Obj(magic, "node", what);
            StrictJson.Only(node, what + ".node", "particles", "lightRadius", "lightIntensity", "height");
            foreach (var kv in StrictJson.Obj(node, "particles", what)) { var name = kv.Value as string; if (String.IsNullOrEmpty(name)) throw new InvalidDataException(what + ".node.particles " + kv.Key + " is not a name."); m.NodeParticles[kv.Key] = name; }
            m.NodeLightRadius = StrictJson.Num(node, "lightRadius", what); m.NodeLightIntensity = StrictJson.Num(node, "lightIntensity", what); m.NodeHeight = StrictJson.Num(node, "height", what);
            var fallback = StrictJson.Obj(magic, "fallback", what);
            StrictJson.Only(fallback, what + ".fallback", "stroke", "burst", "colours", "note");
            m.StrokeFallback = ReadStampFallback(StrictJson.Obj(fallback, "stroke", what), what + ".fallback.stroke");
            m.BurstFallback = ReadStampFallback(StrictJson.Obj(fallback, "burst", what), what + ".fallback.burst");
            var colours = StrictJson.Obj(fallback, "colours", what);
            foreach (var kv in colours) m.FallbackColours[kv.Key] = Rgb(StrictJson.Arr(colours, kv.Key, what), what + ".fallback.colours." + kv.Key);
            StrictJson.Range(m.FallbackColours.Count == m.Schools.Count && m.Schools.All(m.FallbackColours.ContainsKey), what + ".fallback.colours (one per visible school)");
            var cells = new[] { m.TrunkColumn, m.ForkColumn, m.TipColumn, m.BurstColumn };
            StrictJson.Range(m.Rows.Count == 4 && cells.Distinct().Count() == 4 && cells.All(c => c >= 0 && c < 4) && m.Schools.Count == 8 &&
                             m.Spacing > 0 && m.StampLength > 0 && m.MinWidth > 0 && m.Width >= m.MinWidth && m.ForkExponent >= 1 && m.BurstScale > 0 && m.SourceSpacing >= m.Spacing &&
                             m.MinStamps >= 1 && m.FadeBand > 0 && m.MinAlpha > 0 && m.MinAlpha <= 1 && m.EdgeSamples > 0 &&
                             m.RevealHours >= 0 && m.MaxDecals > 0 &&
                             m.NodeParticles.ContainsKey("default") && m.NodeLightRadius > 0 && m.NodeLightIntensity >= 0 && StrictJson.Str(magic, "seam", what) == "nearestImage" && m.ColourGrade == "faceType", what);
            return m;
        }

        // MMR-289: roads.json magicFeatures. A feature set for exactly the overlay's eight visible schools; every part names listed meshes.
        private static FeaturesPolicy ReadFeatures(IDictionary<string, object> features, string what, MagicPolicy magic)
        {
            StrictJson.Only(features, what, "meshes", "schools", "placement", "clearance", "budget", "seam");
            var f = new FeaturesPolicy();
            Func<IDictionary<string, object>, string, string, double[]> range = (d, key, w) =>
            {
                var a = StrictJson.Arr(d, key, w);
                if (a.Length != 2 || !a.All(v => v is int || v is long || v is decimal || v is double)) throw new InvalidDataException(w + " '" + key + "' must be [min, max].");
                var r = a.Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)).ToArray();
                StrictJson.Range(r[0] <= r[1], w + " " + key);
                return r;
            };
            foreach (var kv in StrictJson.Obj(features, "meshes", what))
            {
                var mesh = kv.Value as IDictionary<string, object>; string w = what + " meshes." + kv.Key;
                if (mesh == null) throw new InvalidDataException(w + " must be an object.");
                StrictJson.Only(mesh, w, "min", "max", "flip");
                var min = StrictJson.Arr(mesh, "min", w).Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)).ToArray(); var max = StrictJson.Arr(mesh, "max", w).Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)).ToArray();
                StrictJson.Range(min.Length == 3 && max.Length == 3 && Enumerable.Range(0, 3).All(i => max[i] > min[i]), w + " (a bounding box min < max on x, y, z)");
                f.Meshes[kv.Key] = new FeaturesPolicy.Mesh { Name = kv.Key, Min = min, Max = max, Flip = StrictJson.Bool(mesh, "flip", w) };
            }
            Func<IDictionary<string, object>, string, FeaturesPolicy.Feature> feature = (d, w) =>
            {
                StrictJson.Only(d, w, "name", "parts", "particles", "light");
                var x = new FeaturesPolicy.Feature { Name = StrictJson.Str(d, "name", w) };
                foreach (var o in StrictJson.Arr(d, "parts", w))
                {
                    var part = o as IDictionary<string, object>; string pw = w + " parts[]";
                    if (part == null) throw new InvalidDataException(pw + " must be an object.");
                    StrictJson.Only(part, pw, "meshes", "scale", "count", "ring", "lean", "tints");
                    var p = new FeaturesPolicy.Part { Meshes = StrictJson.Strings(part, "meshes", pw), Tints = new List<uint>() };
                    foreach (var name in p.Meshes) if (!f.Meshes.ContainsKey(name)) throw new InvalidDataException(pw + " names mesh " + name + ", which meshes does not list.");
                    var s = range(part, "scale", pw); var c = range(part, "count", pw); var ring = range(part, "ring", pw); var lean = range(part, "lean", pw);
                    p.ScaleMin = s[0]; p.ScaleMax = s[1]; p.CountMin = (int)c[0]; p.CountMax = (int)c[1]; p.RingMin = ring[0]; p.RingMax = ring[1]; p.LeanMin = lean[0]; p.LeanMax = lean[1];
                    foreach (var t in StrictJson.Strings(part, "tints", pw))
                    {
                        uint argb;
                        if (t.Length != 8 || !UInt32.TryParse(t, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out argb) || (argb >> 24) != 0xFF) throw new InvalidDataException(pw + " tint " + t + " is not an opaque AARRGGBB multiply factor.");
                        p.Tints.Add(argb);
                    }
                    StrictJson.Range(p.ScaleMin > 0 && c[0] == Math.Floor(c[0]) && c[1] == Math.Floor(c[1]) && p.CountMin >= 1 && p.CountMax <= 12 && p.RingMin >= 0 && p.LeanMin >= -60 && p.LeanMax <= 60, pw);
                    x.Parts.Add(p);
                }
                StrictJson.Range(x.Parts.Count > 0, w + " parts");
                if (d.ContainsKey("particles")) x.Particles = StrictJson.Strings(d, "particles", w, true);
                if (d.ContainsKey("light") && d["light"] != null)
                {
                    var l = StrictJson.Obj(d, "light", w);
                    StrictJson.Only(l, w + " light", "colour", "radius", "intensity", "flicker");
                    var rgb = StrictJson.Arr(l, "colour", w).Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)).ToArray();
                    x.Light = new FeaturesPolicy.Glow { Radius = StrictJson.Num(l, "radius", w), Intensity = StrictJson.Num(l, "intensity", w), Flicker = StrictJson.Bool(l, "flicker", w) };
                    StrictJson.Range(rgb.Length == 3 && rgb.All(v => v >= 0 && v <= 1) && x.Light.Radius > 0 && x.Light.Intensity >= 0, w + " light");
                    x.Light.R = rgb[0]; x.Light.G = rgb[1]; x.Light.B = rgb[2];
                }
                StrictJson.Range(x.Particles.Count <= 2, w + " particles (at most 2)");
                return x;
            };
            var schools = StrictJson.Obj(features, "schools", what);
            foreach (var kv in schools)
            {
                var school = kv.Value as IDictionary<string, object>; string w = what + " schools." + kv.Key;
                if (school == null || !magic.Schools.Contains(kv.Key)) throw new InvalidDataException(w + " is not one of the overlay's visible schools.");
                StrictJson.Only(school, w, "lean", "source", "trunk", "debris");
                var s = new FeaturesPolicy.School { Lean = StrictJson.Str(school, "lean", w), Source = feature(StrictJson.Obj(school, "source", w), w + " source"),
                    Trunk = StrictJson.Arr(school, "trunk", w).Select(o => feature(o as IDictionary<string, object> ?? new Dictionary<string, object>(), w + " trunk[]")).ToList(),
                    Debris = StrictJson.Arr(school, "debris", w).Select(o => feature(o as IDictionary<string, object> ?? new Dictionary<string, object>(), w + " debris[]")).ToList() };
                StrictJson.Range((s.Lean == "outward" || s.Lean == "inward" || s.Lean == "wind") && s.Trunk.Count > 0 && s.Debris.Count > 0 && s.Trunk.Concat(s.Debris).All(t => t.Light == null && t.Particles.Count == 0), w + " (lean outward, inward or wind; trunk and debris features, which carry no light or particle)");
                f.Schools[kv.Key] = s;
            }
            if (!magic.Schools.All(f.Schools.ContainsKey)) throw new InvalidDataException(what + " schools lacks a feature set for one of " + String.Join(", ", magic.Schools) + ".");
            var place = StrictJson.Obj(features, "placement", what);
            StrictJson.Only(place, what + " placement", "trunkWidth", "liveTrunkWidth", "trunkSpacing", "edgeThinning", "lateral", "debrisWidth", "minGap", "sourceSearch", "sink", "effectHeight");
            f.TrunkWidth = StrictJson.Num(place, "trunkWidth", what); f.LiveTrunkWidth = StrictJson.Num(place, "liveTrunkWidth", what); f.TrunkSpacing = StrictJson.Num(place, "trunkSpacing", what); f.EdgeThinning = StrictJson.Num(place, "edgeThinning", what);
            var lateral = range(place, "lateral", what + " placement"); f.LateralMin = lateral[0]; f.LateralMax = lateral[1];
            f.DebrisWidth = StrictJson.Num(place, "debrisWidth", what); f.MinGap = StrictJson.Num(place, "minGap", what); f.SourceSearch = StrictJson.Int(place, "sourceSearch", what);
            f.Sink = StrictJson.Num(place, "sink", what); f.EffectHeight = StrictJson.Num(place, "effectHeight", what);
            StrictJson.Range(f.TrunkWidth >= magic.MinWidth && f.TrunkWidth <= magic.Width && f.LiveTrunkWidth >= magic.MinWidth && f.LiveTrunkWidth <= f.TrunkWidth && f.TrunkSpacing > 0 && f.EdgeThinning >= 0 && f.LateralMin >= 0 && f.LateralMax <= 1.5 && f.DebrisWidth > 0 &&
                             f.MinGap > 0 && f.SourceSearch >= 0 && f.Sink >= 0 && f.Sink < 0.5 && f.EffectHeight >= 0, what + " placement");
            var clear = StrictJson.Obj(features, "clearance", what);
            StrictJson.Only(clear, what + " clearance", "settlement", "ruin", "road", "bridge", "water");
            f.SettlementClearance = StrictJson.Num(clear, "settlement", what); f.RuinClearance = StrictJson.Num(clear, "ruin", what); f.RoadClearance = StrictJson.Num(clear, "road", what); f.BridgeClearance = StrictJson.Num(clear, "bridge", what); f.WaterClearance = StrictJson.Num(clear, "water", what);
            StrictJson.Range(f.SettlementClearance > 0 && f.RuinClearance >= 0 && f.RoadClearance >= 0 && f.BridgeClearance >= 0 && f.WaterClearance >= 0, what + " clearance");
            var budget = StrictJson.Obj(features, "budget", what);
            StrictJson.Only(budget, what + " budget", "roots", "entities", "lights", "particles", "window", "windowRoots", "windowEntities", "windowLights", "windowParticles");
            f.MaxRoots = StrictJson.Int(budget, "roots", what); f.MaxEntities = StrictJson.Int(budget, "entities", what); f.MaxLights = StrictJson.Int(budget, "lights", what); f.MaxParticles = StrictJson.Int(budget, "particles", what);
            var window = StrictJson.Arr(budget, "window", what).Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)).ToArray();
            StrictJson.Range(window.Length == 2 && window[1] > 0, what + " budget.window (width, height)");
            f.WindowWidth = window[0]; f.WindowHeight = window[1];
            f.WindowRoots = StrictJson.Int(budget, "windowRoots", what); f.WindowEntities = StrictJson.Int(budget, "windowEntities", what); f.WindowLights = StrictJson.Int(budget, "windowLights", what); f.WindowParticles = StrictJson.Int(budget, "windowParticles", what);
            StrictJson.Range(f.MaxRoots > 0 && f.MaxEntities >= f.MaxRoots && f.MaxLights >= 0 && f.MaxParticles >= 0 && f.WindowWidth > 0 && f.WindowRoots > 0 && f.WindowEntities >= f.WindowRoots &&
                             f.WindowLights >= 0 && f.WindowParticles >= 0 && f.WindowRoots <= f.MaxRoots && StrictJson.Str(features, "seam", what) == "nearestImage", what + " budget");
            return f;
        }
    }

    // worldgen/policies/population.json (module-embedded): the targets, the per-culture troop template table and the lord-trait
    // distribution the emitters read (MMR-280), and the wanderer rules of the daily tick (MMR-281). WorldBridge's population pass
    // (MMR-279) reads the same file through policy_map2.cpp.
    internal sealed class PopulationPolicy
    {
        public const string PolicyPath = "worldgen/policies/population.json";
        public static readonly string[] LordCultures = { "aserai", "battania", "empire", "khuzait", "nord", "sturgia", "vlandia" };
        public sealed class TroopLine { public string SourceClan, Kind, PartyTemplate, TemplateCulture; public List<string> CharacterTemplates; public int Tier; }
        public int BaseFiefs, WandererReserve; public double WanderersPerTown, RotationDailyChance, SharedTraitShare;
        public readonly Dictionary<string, TroopLine> Mercenary = new Dictionary<string, TroopLine>(StringComparer.Ordinal);
        public readonly Dictionary<string, List<TroopLine>> Outlaws = new Dictionary<string, List<TroopLine>>(StringComparer.Ordinal);
        public List<string> Traits, BackstoryUnsafe;
        public readonly List<KeyValuePair<int[], int>> TraitJoint = new List<KeyValuePair<int[], int>>();
        public readonly List<KeyValuePair<string, bool>> WandererTemplates = new List<KeyValuePair<string, bool>>();   // template id, female
        public readonly Dictionary<string, List<string>> WandererFallback = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        public string Sha256 { get; private set; }

        // W, War Sails' own rule: the smallest integer not below the float32 product 0.6f x towns (RT-04).
        public int WandererTarget(int towns) { return (int)Math.Ceiling((double)((float)WanderersPerTown * towns)); }

        public static PopulationPolicy Installed() { return FromBytes(StrictJson.Embedded("population.json")); }
        public static PopulationPolicy FromBytes(byte[] bytes)
        {
            const string what = "population.json";
            var doc = StrictJson.Parse(bytes);
            StrictJson.Only(doc, what, "schema", "version", "notes", "streams", "base", "targets", "kingdomMerge", "wars", "houses", "companies", "outlaws", "troopTemplates",
                            "lordTraits", "wanderers", "spill", "census");
            StrictJson.Header(doc, "mnbl.population", 1, what);
            var p = new PopulationPolicy();
            p.BaseFiefs = StrictJson.Int(StrictJson.Obj(doc, "base", what), "fiefs", what);
            StrictJson.Range(p.BaseFiefs == 133, what + " base.fiefs (one 133-fief base, RT-03)");
            var targets = StrictJson.Obj(doc, "targets", what);
            StrictJson.Only(targets, what + " targets", "kingdoms", "houses", "adultLordsPerHouse", "minorShare", "companies", "outlawClans", "wanderers", "engineChildren");
            var wanderersTarget = StrictJson.Obj(targets, "wanderers", what);
            StrictJson.Only(wanderersTarget, what + " targets.wanderers", "perTown", "rounding");
            p.WanderersPerTown = StrictJson.Num(wanderersTarget, "perTown", what);
            StrictJson.Range(p.WanderersPerTown > 0 && StrictJson.Str(wanderersTarget, "rounding", what) == "ceilFloat32", what + " targets.wanderers");
            var troops = StrictJson.Obj(StrictJson.Obj(doc, "troopTemplates", what), "cultures", what);
            Func<IDictionary<string, object>, string, TroopLine> line = (o, w) =>
            {
                StrictJson.Only(o, w, "sourceClan", "kind", "partyTemplate", "characterTemplates", "tier", "templateCulture");
                var t = new TroopLine { SourceClan = StrictJson.Str(o, "sourceClan", w), Kind = StrictJson.Str(o, "kind", w), PartyTemplate = StrictJson.Str(o, "partyTemplate", w),
                                        TemplateCulture = StrictJson.Str(o, "templateCulture", w), CharacterTemplates = StrictJson.Strings(o, "characterTemplates", w), Tier = StrictJson.Int(o, "tier", w) };
                StrictJson.Range(t.CharacterTemplates.Count == 4 && t.PartyTemplate != "kingdom_hero_party_" + t.TemplateCulture + "_template" && t.Tier >= 1 && t.Tier <= 6 &&
                                 Array.IndexOf(LordCultures, t.TemplateCulture) >= 0, w + " (4 leader templates, never a lord party template)");
                return t;
            };
            foreach (var kv in troops)
            {
                var entry = kv.Value as IDictionary<string, object>;
                string w = what + " troopTemplates." + kv.Key;
                if (entry == null) throw new InvalidDataException(w + " must be an object.");
                StrictJson.Only(entry, w, "mercenary", "outlaws", "outlawFallback");
                var merc = line(StrictJson.Obj(entry, "mercenary", w), w + ".mercenary");
                StrictJson.Range(merc.Kind == "mercenary", w + ".mercenary kind");
                p.Mercenary[kv.Key] = merc;
                p.Outlaws[kv.Key] = StrictJson.Arr(entry, "outlaws", w).Select(o => line((IDictionary<string, object>)o, w + ".outlaws[]")).ToList();
                StrictJson.Range(p.Outlaws[kv.Key].Count > 0 && p.Outlaws[kv.Key].All(o => o.Kind == "mafia" || o.Kind == "nomad" || o.Kind == "sect"), w + ".outlaws");
            }
            foreach (var c in LordCultures) if (!p.Mercenary.ContainsKey(c)) throw new InvalidDataException(what + " troopTemplates has no line for culture " + c + " (RT-03).");
            var traits = StrictJson.Obj(doc, "lordTraits", what);
            StrictJson.Only(traits, what + " lordTraits", "rule", "traits", "lords", "sameCultureSharedShare", "marginals", "joint");
            p.Traits = StrictJson.Strings(traits, "traits", what);
            p.SharedTraitShare = StrictJson.Num(traits, "sameCultureSharedShare", what);
            int counted = 0;
            foreach (IDictionary<string, object> j in StrictJson.Arr(traits, "joint", what))
            {
                StrictJson.Only(j, what + " lordTraits.joint[]", "values", "count");
                var values = StrictJson.Arr(j, "values", what).Select(v => Convert.ToInt32(v, CultureInfo.InvariantCulture)).ToArray();
                int n = StrictJson.Int(j, "count", what);
                StrictJson.Range(values.Length == p.Traits.Count && n > 0 && values.All(v => v >= -2 && v <= 2), what + " lordTraits.joint[]");
                p.TraitJoint.Add(new KeyValuePair<int[], int>(values, n)); counted += n;
            }
            StrictJson.Range(counted == StrictJson.Int(traits, "lords", what), what + " lordTraits (joint counts sum to lords)");
            var wanderers = StrictJson.Obj(doc, "wanderers", what);
            StrictJson.Only(wanderers, what + " wanderers", "rule", "sources", "rotation", "topUp", "keepEpithet", "sex", "reserve", "backstoryUnsafe", "backstoryUnsafeOwner",
                            "backstoryCensus", "fallback", "templates");
            p.WandererReserve = StrictJson.Int(wanderers, "reserve", what);
            StrictJson.Range(p.WandererReserve >= 1 && p.WandererReserve <= 256, what + " wanderers.reserve");
            var rotation = StrictJson.Obj(wanderers, "rotation", what);
            StrictJson.Only(rotation, what + " wanderers.rotation", "dailyChance", "retire");
            p.RotationDailyChance = StrictJson.Num(rotation, "dailyChance", what);
            StrictJson.Range(p.RotationDailyChance >= 0 && p.RotationDailyChance <= 1 && StrictJson.Str(rotation, "retire", what) == "unmetUnhired", what + " wanderers.rotation");
            foreach (IDictionary<string, object> t in StrictJson.Arr(wanderers, "templates", what))
            {
                StrictJson.Only(t, what + " wanderers.templates[]", "id", "culture", "female", "epithet");
                StrictJson.Str(t, "culture", what); StrictJson.Str(t, "epithet", what);
                p.WandererTemplates.Add(new KeyValuePair<string, bool>(StrictJson.Str(t, "id", what), StrictJson.Bool(t, "female", what)));
            }
            p.BackstoryUnsafe = StrictJson.Strings(wanderers, "backstoryUnsafe", what, true);
            StrictJson.Range(p.WandererTemplates.Count > 0 && p.BackstoryUnsafe.All(id => p.WandererTemplates.Any(t => t.Key == id)), what + " wanderers.templates");
            // MMR-287: each lord culture's ordered cross-culture fallback (HeroFactory.WandererPool reads the same block).
            StrictJson.Str(wanderers, "backstoryCensus", what);
            var fallback = StrictJson.Obj(wanderers, "fallback", what);
            StrictJson.Only(fallback, what + " wanderers.fallback", LordCultures);
            foreach (var c in LordCultures)
            {
                var order = StrictJson.Strings(fallback, c, what + " wanderers.fallback");
                StrictJson.Range(order.All(o => o != c && Array.IndexOf(LordCultures, o) >= 0) && order.Distinct().Count() == order.Count, what + " wanderers.fallback." + c);
                p.WandererFallback[c] = order;
            }
            p.Sha256 = StrictJson.Sha(bytes);
            return p;
        }
    }

    // worldgen/policies/settlement-looks.json (module-embedded): the look catalog, selector, fit and phase-0 pieces (MMR-258) and the ruin
    // markers (MMR-257).
    internal sealed class SettlementLooksPolicy
    {
        public const string PolicyPath = "worldgen/policies/settlement-looks.json";
        public double Scale, MinScale, ClearanceMargin, RepeatPenalty, UsageBalance, RuinMinGap, RuinScale; public int Neighbours, Stream, RuinSnapCells, WallsPerLevel;
        public bool Cull, Resnap, CastleAsSmallTown;
        public readonly Dictionary<string, bool> Normalize = new Dictionary<string, bool>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> CultureCodes = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, List<string>> Production = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        public List<string> RuinPieces, CollapsedCultures, Kinds, PortStripped, PortGrafted, Piers;
        public string CatalogScene, Keep, Wall, WallSegment, Collapsed, CollapsedFallback, Village;
        public string Sha256 { get; private set; }

        // A phase-0 mesh name: {code} the culture code, {culture} the culture, {level} the wall or hearth level 1..3.
        public string Name(string template, string culture, int level)
        {
            return template.Replace("{code}", CultureCodes[culture]).Replace("{culture}", culture).Replace("{level}", level.ToString(CultureInfo.InvariantCulture));
        }
        // A culture's collapsed wall segment of a level (the empire's where the culture has none).
        public string CollapsedWall(string culture, int level) { return Name(CollapsedCultures.Contains(culture) ? Collapsed : CollapsedFallback, culture, level); }

        public static SettlementLooksPolicy Installed() { return FromBytes(StrictJson.Embedded("settlement-looks.json")); }
        public static SettlementLooksPolicy FromBytes(byte[] bytes)
        {
            const string what = "settlement-looks.json";
            var doc = StrictJson.Parse(bytes);
            StrictJson.Only(doc, what, "schema", "version", "catalog", "normalize", "fit", "selector", "variants", "contract", "phase0", "ruins", "notes");
            StrictJson.Header(doc, "mnbl.settlement-looks", 1, what);
            var p = new SettlementLooksPolicy();
            var catalog = StrictJson.Obj(doc, "catalog", what);
            StrictJson.Only(catalog, what + " catalog", "scene", "frozen", "kinds");
            StrictJson.Range(StrictJson.Bool(catalog, "frozen", what), what + " catalog.frozen (no TaleWorlds XML ships)");
            p.Kinds = StrictJson.Strings(catalog, "kinds", what);
            StrictJson.Subset(p.Kinds, what + " catalog.kinds", "town", "castle", "village");
            p.CatalogScene = StrictJson.Str(catalog, "scene", what);
            var normalize = StrictJson.Obj(doc, "normalize", what);
            StrictJson.Only(normalize, what + " normalize", "inlinePrefabs", "stripEditorData", "stripSceneLeveler", "collidersRaycastOnly", "dropSourcePorts", "dropArcs", "dropDropoffs");
            foreach (var k in normalize.Keys) p.Normalize[k] = StrictJson.Bool(normalize, k, what + " normalize");
            var fit = StrictJson.Obj(doc, "fit", what);
            StrictJson.Only(fit, what + " fit", "scale", "minScale", "cull", "clearanceMargin", "resnapChildren", "yaw");
            p.Scale = StrictJson.Num(fit, "scale", what); p.MinScale = StrictJson.Num(fit, "minScale", what); p.ClearanceMargin = StrictJson.Num(fit, "clearanceMargin", what);
            p.Cull = StrictJson.Bool(fit, "cull", what); p.Resnap = StrictJson.Bool(fit, "resnapChildren", what);
            StrictJson.Range(p.MinScale > 0 && p.MinScale <= p.Scale && p.Scale <= 1 && p.ClearanceMargin >= 0 && StrictJson.Str(fit, "yaw", what) == "portOrGate", what + " fit");
            var selector = StrictJson.Obj(doc, "selector", what);
            StrictJson.Only(selector, what + " selector", "inputs", "neighbours", "repeatPenalty", "usageBalance", "stream");
            StrictJson.Subset(StrictJson.Strings(selector, "inputs", what), what + " selector.inputs", "culture", "kind", "port", "sizeTier");
            p.Neighbours = StrictJson.Int(selector, "neighbours", what); p.RepeatPenalty = StrictJson.Num(selector, "repeatPenalty", what); p.UsageBalance = StrictJson.Num(selector, "usageBalance", what);
            p.Stream = StrictJson.Int(selector, "stream", what);
            StrictJson.Range(p.Neighbours >= 1 && p.RepeatPenalty >= 0 && p.UsageBalance >= 0 && p.Stream >= 1, what + " selector");
            var variants = StrictJson.Obj(doc, "variants", what);
            StrictJson.Only(variants, what + " variants", "portStripped", "portGrafted", "castleAsSmallTown");
            p.PortStripped = StrictJson.Strings(variants, "portStripped", what); p.PortGrafted = StrictJson.Strings(variants, "portGrafted", what);
            StrictJson.Subset(p.PortStripped.Concat(p.PortGrafted), what + " variants", PopulationPolicy.LordCultures);
            p.CastleAsSmallTown = StrictJson.Bool(variants, "castleAsSmallTown", what);
            var contract = StrictJson.Obj(doc, "contract", what);
            StrictJson.Only(contract, what + " contract", "wallsPerLevel", "maxSolidOrBrokenPerWall", "bannerLevelBits", "dropoffParentIsVillage");
            p.WallsPerLevel = StrictJson.Int(contract, "wallsPerLevel", what);
            // WallSectionCount is 2 in the engine: any other count misaligns the wall-HP mapping (findings section 2).
            StrictJson.Range(p.WallsPerLevel == 2 && StrictJson.Int(contract, "maxSolidOrBrokenPerWall", what) == 1 && StrictJson.Bool(contract, "bannerLevelBits", what) &&
                             StrictJson.Bool(contract, "dropoffParentIsVillage", what), what + " contract");
            var phase0 = StrictJson.Obj(doc, "phase0", what);
            StrictJson.Only(phase0, what + " phase0", "cultureCodes", "keep", "wall", "wallSegment", "collapsed", "collapsedCultures", "collapsedFallback", "village", "piers", "production");
            foreach (var kv in StrictJson.Obj(phase0, "cultureCodes", what)) p.CultureCodes[kv.Key] = kv.Value as string;
            StrictJson.Range(PopulationPolicy.LordCultures.All(c => !String.IsNullOrEmpty(p.CultureCodes.ContainsKey(c) ? p.CultureCodes[c] : null)) && p.CultureCodes.Count == PopulationPolicy.LordCultures.Length,
                             what + " phase0.cultureCodes (one per presentation culture)");
            p.Keep = StrictJson.Str(phase0, "keep", what); p.Wall = StrictJson.Str(phase0, "wall", what); p.CollapsedFallback = StrictJson.Str(phase0, "collapsedFallback", what);
            p.Village = StrictJson.Str(phase0, "village", what);
            p.WallSegment = StrictJson.Str(phase0, "wallSegment", what); p.Collapsed = StrictJson.Str(phase0, "collapsed", what);
            p.CollapsedCultures = StrictJson.Strings(phase0, "collapsedCultures", what);
            p.Piers = StrictJson.Strings(phase0, "piers", what);
            StrictJson.Range(p.Keep.Contains("{code}") && p.Keep.Contains("{level}") && p.Village.Contains("{culture}") && p.WallSegment.Contains("{level}") && p.CollapsedFallback.Contains("{level}")
                             && p.CollapsedCultures.All(c => p.CultureCodes.ContainsKey(c)), what + " phase0 name templates");
            foreach (var kv in StrictJson.Obj(phase0, "production", what)) p.Production[kv.Key] = StrictJson.Strings(StrictJson.Obj(phase0, "production", what), kv.Key, what + " phase0.production");
            var ruins = StrictJson.Obj(doc, "ruins", what);
            StrictJson.Only(ruins, what + " ruins", "minGap", "snapCells", "pieces", "scale", "encyclopedia");
            p.RuinMinGap = StrictJson.Num(ruins, "minGap", what); p.RuinSnapCells = StrictJson.Int(ruins, "snapCells", what); p.RuinScale = StrictJson.Num(ruins, "scale", what);
            p.RuinPieces = StrictJson.Strings(ruins, "pieces", what);
            StrictJson.Range(p.RuinMinGap > 0 && p.RuinSnapCells >= 0 && p.RuinScale > 0 && StrictJson.Bool(ruins, "encyclopedia", what), what + " ruins");
            p.Sha256 = StrictJson.Sha(bytes);
            return p;
        }
    }

    // The map epic 2 blocks of the existing policies, read strictly: playability.json (the bundle's, MMR-251..254), economy.json
    // (MMR-252, MMR-258) and startup.json (MMR-287). The rest of those files keep their readers.
    internal static class Map2PolicyBlocks
    {
        public sealed class Playability
        {
            public readonly Dictionary<string, string> LandFaceGroups = new Dictionary<string, string>(StringComparer.Ordinal);
            public string LandFaceDefault, PermanentWhiteFace; public int PermanentWhiteMonths, TownVillages, CastleVillages, MaxTownSnapCells; public double Clearance, MinSeparation;
            public bool TownCarve, ForcedPorts, RoadCellFlag;
        }
        public static Playability FromPlayability(IDictionary<string, object> doc)
        {
            const string what = "playability.json";
            var p = new Playability();
            var faces = StrictJson.Obj(StrictJson.Obj(doc, "cells", what), "landFaceGroups", what);
            StrictJson.Only(faces, what + " cells.landFaceGroups", "default", "biomes", "permanentWhite");
            p.LandFaceDefault = StrictJson.Str(faces, "default", what);
            foreach (var kv in StrictJson.Obj(faces, "biomes", what)) p.LandFaceGroups[kv.Key] = kv.Value as string;
            var white = StrictJson.Obj(faces, "permanentWhite", what);
            StrictJson.Only(white, what + " cells.landFaceGroups.permanentWhite", "snowMonths", "snowThreshold", "faceGroup");
            p.PermanentWhiteMonths = StrictJson.Int(white, "snowMonths", what); p.PermanentWhiteFace = StrictJson.Str(white, "faceGroup", what);
            StrictJson.Num(white, "snowThreshold", what);
            StrictJson.Subset(p.LandFaceGroups.Values.Concat(new[] { p.LandFaceDefault, p.PermanentWhiteFace }), what + " cells.landFaceGroups", BiomeTable.LandFaceTypes);
            var repairs = StrictJson.Obj(doc, "repairs", what);
            p.MaxTownSnapCells = StrictJson.Int(repairs, "maxTownSnapCells", what); p.TownCarve = StrictJson.Bool(repairs, "townCarve", what); p.ForcedPorts = StrictJson.Bool(repairs, "forcedPorts", what);
            var villages = StrictJson.Obj(doc, "villages", what);
            StrictJson.Only(villages, what + " villages", "townVillages", "castleVillages", "clearance", "minSeparation", "tiers", "isletExceptions", "preference", "maxExtraHarbours", "reassign",
                            "protectedClasses", "synthesizedClass", "uidPrefix");
            p.TownVillages = StrictJson.Int(villages, "townVillages", what); p.CastleVillages = StrictJson.Int(villages, "castleVillages", what);
            p.Clearance = StrictJson.Num(villages, "clearance", what); p.MinSeparation = StrictJson.Num(villages, "minSeparation", what);
            StrictJson.Range(p.TownVillages >= 1 && p.CastleVillages >= 1 && p.Clearance >= 0 && p.MinSeparation > 0, what + " villages");
            var roads = StrictJson.Obj(doc, "roads", what);
            StrictJson.Only(roads, what + " roads", "splitAtPoles", "skipSeaLegs", "passesAreRoad", "endAtUndeckedBank", "cellFlag", "feeders");
            p.RoadCellFlag = StrictJson.Bool(roads, "cellFlag", what);
            return p;
        }

        public sealed class Economy
        {
            public int TownVillages, CastleVillages; public double CoastalFishingShare;
            public readonly Dictionary<string, double> HorseRanchShare = new Dictionary<string, double>(StringComparer.Ordinal), WheatShare = new Dictionary<string, double>(StringComparer.Ordinal);
            public readonly Dictionary<string, Dictionary<int, int>> TownWalls = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal), CastleWalls = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        }
        public static Economy FromEconomy(IDictionary<string, object> doc)
        {
            const string what = "economy.json";
            var e = new Economy();
            var binding = StrictJson.Obj(doc, "binding", what);
            e.TownVillages = StrictJson.Int(binding, "townVillages", what); e.CastleVillages = StrictJson.Int(binding, "castleVillages", what);
            var mix = StrictJson.Obj(doc, "villageMix", what);
            StrictJson.Only(mix, what + " villageMix", "rule", "coastalFishingShare", "coastal", "floors");
            e.CoastalFishingShare = StrictJson.Num(mix, "coastalFishingShare", what);
            StrictJson.Range(e.CoastalFishingShare >= 0 && e.CoastalFishingShare <= 1, what + " villageMix.coastalFishingShare");
            foreach (var kv in StrictJson.Obj(mix, "coastal", what))
            {
                var c = (IDictionary<string, object>)kv.Value;
                StrictJson.Only(c, what + " villageMix.coastal." + kv.Key, "villages", "fishing", "share");
            }
            foreach (var kv in StrictJson.Obj(mix, "floors", what))
            {
                var f = (IDictionary<string, object>)kv.Value;
                StrictJson.Only(f, what + " villageMix.floors." + kv.Key, "villages", "horseRanch", "wheat", "horseRanchShare", "wheatShare");
                e.HorseRanchShare[kv.Key] = StrictJson.Num(f, "horseRanchShare", what); e.WheatShare[kv.Key] = StrictJson.Num(f, "wheatShare", what);
            }
            foreach (var c in PopulationPolicy.LordCultures) if (!e.HorseRanchShare.ContainsKey(c)) throw new InvalidDataException(what + " villageMix.floors has no culture " + c + ".");
            var walls = StrictJson.Obj(doc, "walls", what);
            StrictJson.Only(walls, what + " walls", "rule", "towns", "castles");
            Action<string, Dictionary<string, Dictionary<int, int>>, string[]> tiers = (key, into, names) =>
            {
                var block = StrictJson.Obj(walls, key, what);
                StrictJson.Only(block, what + " walls." + key, names);
                foreach (var n in names)
                {
                    var levels = new Dictionary<int, int>();
                    foreach (var kv in StrictJson.Obj(block, n, what))
                    {
                        int level = Int32.Parse(kv.Key, CultureInfo.InvariantCulture), count = Convert.ToInt32(kv.Value, CultureInfo.InvariantCulture);
                        StrictJson.Range(level >= 1 && level <= 3 && count >= 0, what + " walls." + key + "." + n);
                        levels[level] = count;
                    }
                    StrictJson.Range(levels.Values.Sum() > 0, what + " walls." + key + "." + n);
                    into[n] = levels;
                }
            };
            tiers("towns", e.TownWalls, new[] { "small", "medium", "capital" });
            tiers("castles", e.CastleWalls, new[] { "low", "middle", "high" });
            return e;
        }

        public sealed class HideoutBiomes
        {
            public double Share, CoastWeight; public int CoastCells; public string CoastCulture;
            public readonly Dictionary<string, Dictionary<string, double>> Weights = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        }
        public static HideoutBiomes FromStartup(IDictionary<string, object> doc)
        {
            const string what = "startup.json";
            var hideouts = StrictJson.Obj(doc, "hideouts", what);
            var required = StrictJson.Strings(hideouts, "requiredCultures", what);
            var biome = StrictJson.Obj(hideouts, "biome", what);
            StrictJson.Only(biome, what + " hideouts.biome", "rule", "share", "biomeWeights", "coastCells", "coastCulture", "coastWeight");
            var h = new HideoutBiomes { Share = StrictJson.Num(biome, "share", what), CoastCells = StrictJson.Int(biome, "coastCells", what), CoastCulture = StrictJson.Str(biome, "coastCulture", what),
                                        CoastWeight = StrictJson.Num(biome, "coastWeight", what) };
            StrictJson.Range(h.Share >= 0 && h.Share <= 1 && h.CoastCells >= 0 && h.CoastWeight >= 0 && required.Contains(h.CoastCulture), what + " hideouts.biome");
            foreach (var kv in StrictJson.Obj(biome, "biomeWeights", what))
            {
                var weights = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var w in (IDictionary<string, object>)kv.Value)
                {
                    if (!required.Contains(w.Key)) throw new InvalidDataException(what + " hideouts.biome.biomeWeights." + kv.Key + " names " + w.Key + ", not a required bandit culture.");
                    weights[w.Key] = Convert.ToDouble(w.Value, CultureInfo.InvariantCulture);
                }
                StrictJson.Range(weights.Count > 0 && weights.Values.All(x => x > 0), what + " hideouts.biome.biomeWeights." + kv.Key);
                h.Weights[kv.Key] = weights;
            }
            return h;
        }
    }
}
