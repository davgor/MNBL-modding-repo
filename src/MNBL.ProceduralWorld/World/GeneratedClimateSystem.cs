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
    // MMR-39: the deterministic synthesis of the engine's weather fields from a generated world's source climate
    // (worldgen/policies/climate-flow.json, docs/water-climate.md). FWGC++ exports no engine flow or weather field, so the host
    // builds them from what the source does carry:
    //   * source: world.json `layers` (rainfall, ice_cap, biome, moisture, temperature; 129 x 129), `seasonal_environment.months[*].snow`
    //     (12 monthly 129 x 129 grids) and `config.seed` (the world's master seed);
    //   * wind (MMR-256, RT-18): SandBox.MapScene's 512 x 512 (x, y) float layout over the physical terrain. Each sample's centre is mapped
    //     to its canonical logical location (PeriodicWorldCoordinates) and to the source grid (projection inverse.sourceGrid), whose row
    //     gives the latitude and column the longitude. The direction is the policy's latitude band (blended by smoothstep over blendDeg at
    //     each band edge and across the equator, the north component mirrored in the southern hemisphere), turned by a seeded value-noise
    //     angle (periodic in longitude), faded to zero (smoothstep over PoleTaperRows) towards each pole line (the pole is one location, so
    //     it has one value) and scaled by WindScale. Map vectors are (east, -north): physical y runs south. FWGC++'s wind_arrows (one fixed
    //     bearing per world) are no longer read;
    //   * snow and rain: the 32 x 32 (snow, rain) byte layout. Each node averages 8 x 8 sub-samples of its footprint: rain is rainfall over
    //     the layer's maximum (the source's values are relative proxies); MMR-256: snow is the byte of the footprint's mean snow-season length
    //     (months with snow at or above the policy threshold) through the inverse of the installed DefaultMapWeatherModel formula, so the
    //     Snowy share of the year equals the FWGC++ snow season; a node that is mostly ice cap takes the ice-cap byte. The season itself is
    //     the engine's (global);
    //   * snow texture (MMR-256): a 512 x 512 RGBA raster for the terrain shader's dynamic snow (R the pixel's snow byte by the same rule,
    //     G its rain byte; row 0 at physical y = extent, the shader's v = 1 - y / extent), named by its hash (GeneratedSnowTexture loads it);
    //   * colour grade: the installed MapColorGradeManager's 512 x 512 grade bytes (worldmap_color_grades.xml values) over the terrain,
    //     in place of Calradia's worldmap_colorgrade_grid texture. Each cell takes the grade of the FWGC++ natural biome of its nearest
    //     source cell (BiomeGrades; MMR-256: grassland split by the cell's moisture); water and unlisted biomes take a temperature band
    //     (cold north, temperate normandy, warm mediterranean, from the source's temperature layer at the cell); then the grid is
    //     mode-filtered (between the pole lines);
    //   * strips and padding: every sample is taken at its canonical location, so a strip sample is an exact copy of its canonical
    //     sample (the grids put a whole number of samples in a width) and the rows beyond each pole line repeat the pole's value.
    // Pure managed: the in-game binding (GeneratedClimate, GeneratedMapSupport.PrepareWeather, GeneratedSnowTexture) and the offline tests share it.
    public static class GeneratedClimateSystem
    {
        public const string Version = "mmr256/1";
        public const int WindDimension = 512, WeatherDimension = 32, NodeSubsamples = 8, SnowTextureDimension = 512;
        public const double WindScale = 0.4;   // inside War Sails' weather-model clamp [0.0667, 0.46], above the official flowmap's background (0.2)
        public const double PoleTaperRows = 16; // source rows over which the wind fades (smoothstep) to zero at each pole line
        public const int ColorGradeDimension = 512;   // MapColorGradeManager's grid (262144 bytes, index row * 512 + col over the terrain)
        // The installed grades (Modules/Native/ModuleData/worldmap_color_grades.xml).
        public const byte GradeDesert = 20, GradeSteppe = 40, GradeCentralEurope = 60, GradeHighlands = 80, GradeNormandy = 100, GradeNorth = 120, GradeMediterranean = 140;
        public const double ColdBelowC = 2, WarmFromC = 18;   // the water and fallback temperature bands (source temperature, degrees C)
        public const int GrasslandBiome = 3;
        // FWGC++ natural biome ids (generator/data/biomes.json; ids are append-only) and their grades: tundra and snow (1, 6, tombstoned
        // labels), boreal forest, cold tundra and land ice are north; desert; grassland steppe (dry; wet grassland central Europe, the
        // policy's grassland split); forest central Europe; exposed rock highlands; rainforest mediterranean; marsh normandy. Ocean (0),
        // lake (8) and any other id take the temperature band.
        public static readonly IList<KeyValuePair<int, byte>> BiomeGrades = Array.AsReadOnly(new[] {
            new KeyValuePair<int, byte>(1, GradeNorth), new KeyValuePair<int, byte>(2, GradeDesert), new KeyValuePair<int, byte>(3, GradeSteppe),
            new KeyValuePair<int, byte>(4, GradeCentralEurope), new KeyValuePair<int, byte>(5, GradeHighlands), new KeyValuePair<int, byte>(6, GradeNorth),
            new KeyValuePair<int, byte>(7, GradeMediterranean), new KeyValuePair<int, byte>(13, GradeNormandy), new KeyValuePair<int, byte>(15, GradeNorth),
            new KeyValuePair<int, byte>(16, GradeNorth), new KeyValuePair<int, byte>(17, GradeNorth) });

        // ---- the installed DefaultMapWeatherModel snow rule (TaleWorlds.CampaignSystem IL, 1.4.8) and its inverse ----
        // GetSnowAndRainDataForPosition: c = Lerp(0.55, -0.1, s), v = SmoothStep(c - 0.65, c + 0.65, byte / 255), snow = v^2; a position is
        // Snowy above 0.55 (SetIsBlizzardOrSnowFromFunction; blizzard-eligible from 0.65). CalculateTimeFactorForSnow: s is 0 at mid-summer
        // (season 1.5) and 1 at mid-winter (3.5), linear in between both ways, so s is uniform over the year and a byte is Snowy for the share
        // 1 - s* of it, s* the season factor where snow reaches 0.55. SnowByte inverts that: the smallest byte Snowy for at least the share f.
        public const double SnowyAbove = 0.55, SummerThreshold = 0.55, WinterThreshold = -0.1, ThresholdWidth = 0.65, BlizzardAbove = 0.65;
        public static readonly double SnowyEdge = InverseSmoothStep(Math.Sqrt(SnowyAbove));
        public static byte SnowByte(double share)
        {
            if (!(share > 0)) return 0;
            double s = 1 - Math.Min(1, share), c = SummerThreshold + (WinterThreshold - SummerThreshold) * s;
            double v = 255 * (c - ThresholdWidth + 2 * ThresholdWidth * SnowyEdge);
            return (byte)Math.Max(1, Math.Min(255, Math.Ceiling(v - 1e-9)));
        }
        public static double InverseSmoothStep(double y)
        {
            double lo = 0, hi = 1;
            for (int i = 0; i < 80; i++) { double m = (lo + hi) / 2; if (m * m * (3 - 2 * m) < y) lo = m; else hi = m; }
            return (lo + hi) / 2;
        }

        // ---- the policy (worldgen/policies/climate-flow.json; embedded in the module) ----
        // The values the synthesis uses, read strictly: a member it needs that is missing, a version other than Version, or a value that
        // disagrees with the code's constants (dimensions, scale, the snow-season bytes the inverse rule gives) refuses the policy.
        public sealed class Policy
        {
            public const string NamedTexture = "namedTexture", PermanentWhite = "permanentWhite";
            public sealed class Band { public double ToLatDeg, East, North; }
            public readonly List<Band> Bands = new List<Band>();
            public double BlendDeg, Amplitude, WavelengthDeg;
            public int Octaves;
            public double MonthThreshold;
            public byte IceCapByte;
            public readonly SortedDictionary<string, int> SeasonBytes = new SortedDictionary<string, int>(StringComparer.Ordinal);
            public string SnowRoute;
            public double GrassThreshold;
            public byte GrassDry, GrassWet;
            public int ModeFilterCells;

            public static Policy Installed()
            {
                using (var stream = typeof(GeneratedClimateSystem).Assembly.GetManifestResourceStream("MNBL.ProceduralWorld.climate-flow.json"))
                {
                    if (stream == null) throw new InvalidDataException("The module carries no climate-flow.json policy.");
                    var bytes = new MemoryStream(); stream.CopyTo(bytes);
                    return FromBytes(bytes.ToArray());
                }
            }

            public static Policy FromBytes(byte[] bytes)
            {
                var doc = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(new UTF8Encoding(false).GetString(bytes).TrimStart((char)0xFEFF)) as IDictionary<string, object>;
                if (doc == null) throw new InvalidDataException("climate-flow.json must be a JSON object.");
                if (Text(doc, "schema") != "mnbl.climate-flow-policy" || Text(doc, "version") != Version) throw new InvalidDataException("climate-flow.json is not mnbl.climate-flow-policy " + Version + ".");
                var p = new Policy();
                var wind = Obj(doc, "wind");
                if (Int(wind, "dimension") != WindDimension || Num(wind, "scale") != WindScale || Num(wind, "poleTaperRows") != PoleTaperRows) throw new InvalidDataException("climate-flow.json wind differs from the code's constants.");
                var bands = Obj(wind, "bands");
                double last = 0;
                foreach (var item in Arr(bands, "bands"))
                {
                    var b = item as IDictionary<string, object>;
                    if (b == null) throw new InvalidDataException("climate-flow.json wind band must be an object.");
                    var dir = Arr(b, "direction");
                    if (dir.Length != 2) throw new InvalidDataException("climate-flow.json wind band direction must be (east, north).");
                    var band = new Band { ToLatDeg = Num(b, "toLatDeg"), East = Convert.ToDouble(dir[0], CultureInfo.InvariantCulture), North = Convert.ToDouble(dir[1], CultureInfo.InvariantCulture) };
                    if (!(band.ToLatDeg > last) || Math.Abs(Math.Sqrt(band.East * band.East + band.North * band.North) - 1) > .02) throw new InvalidDataException("climate-flow.json wind bands must rise in latitude and hold unit directions.");
                    double length = Math.Sqrt(band.East * band.East + band.North * band.North);
                    band.East /= length; band.North /= length;   // the policy's directions are rounded; the code uses them as unit vectors
                    last = band.ToLatDeg; p.Bands.Add(band);
                }
                if (p.Bands.Count == 0 || last != 90) throw new InvalidDataException("climate-flow.json wind bands must end at latitude 90.");
                p.BlendDeg = Num(bands, "blendDeg");
                var perturbation = Obj(bands, "perturbation");
                p.Amplitude = Num(perturbation, "amplitude"); p.WavelengthDeg = Num(perturbation, "wavelengthDeg"); p.Octaves = Int(perturbation, "octaves");
                if (Text(perturbation, "unit") != "radians" || p.Amplitude < 0 || p.Amplitude > 1.5 || p.Octaves < 1 || p.Octaves > 4 || !(p.BlendDeg > 0)) throw new InvalidDataException("climate-flow.json wind perturbation is out of range.");
                for (int o = 0; o < p.Octaves; o++) { double cells = 360 / (p.WavelengthDeg / (1 << o)); if (Math.Abs(cells - Math.Round(cells)) > 1e-9) throw new InvalidDataException("climate-flow.json wind wavelength must divide 360 degrees at every octave."); }
                var precipitation = Obj(doc, "precipitation");
                if (Int(precipitation, "dimension") != WeatherDimension) throw new InvalidDataException("climate-flow.json precipitation dimension differs from the code's.");
                var season = Obj(precipitation, "snowSeason");
                p.MonthThreshold = Num(season, "monthThreshold");
                p.IceCapByte = (byte)Int(season, "iceCap");
                foreach (var kv in Obj(season, "bytes")) p.SeasonBytes[kv.Key] = Convert.ToInt32(kv.Value, CultureInfo.InvariantCulture);
                // The table is the inverse rule at 12, 9, 6 and 3 months and at the peak (f -> 0+).
                foreach (var kv in p.SeasonBytes)
                {
                    int expected = kv.Key == "peak" ? SnowByte(1e-9) : SnowByte(Int32.Parse(kv.Key, CultureInfo.InvariantCulture) / 12.0);
                    if (kv.Value != expected) throw new InvalidDataException("climate-flow.json snowSeason byte " + kv.Key + " is " + kv.Value + "; the inverse of the installed formula gives " + expected + ".");
                }
                var texture = Obj(precipitation, "snowTexture");
                p.SnowRoute = Text(texture, "route");
                if ((p.SnowRoute != NamedTexture && p.SnowRoute != PermanentWhite) || Int(texture, "dimension") != SnowTextureDimension) throw new InvalidDataException("climate-flow.json snowTexture route or dimension is not the code's.");
                var grade = Obj(doc, "colorGrade");
                if (Int(grade, "dimension") != ColorGradeDimension) throw new InvalidDataException("climate-flow.json colour-grade dimension differs from the code's.");
                var grass = Obj(grade, "grassland");
                if (Text(grass, "split") != "moisture") throw new InvalidDataException("climate-flow.json grassland must split by moisture.");
                p.GrassThreshold = Num(grass, "threshold"); p.GrassDry = (byte)Int(grass, "dry"); p.GrassWet = (byte)Int(grass, "wet"); p.ModeFilterCells = Int(grass, "modeFilterCells");
                if (p.ModeFilterCells < 1 || p.ModeFilterCells % 2 == 0) throw new InvalidDataException("climate-flow.json modeFilterCells must be odd.");
                var runtime = Obj(grade, "runtime");
                var threshold = Arr(runtime, "threshold");
                if (threshold.Length != 2 || Convert.ToDouble(threshold[0], CultureInfo.InvariantCulture) != SummerThreshold || Convert.ToDouble(threshold[1], CultureInfo.InvariantCulture) != WinterThreshold
                    || Num(runtime, "blizzard") != BlizzardAbove || Text(runtime, "driver") != "managed") throw new InvalidDataException("climate-flow.json colour-grade runtime differs from the installed season threshold.");
                return p;
            }

            // The values the synthesis uses, as text (part of the synthesis identity; documentary text in the file is not).
            public string Text()
            {
                Func<double, string> r = v => v.ToString("R", CultureInfo.InvariantCulture);
                return "bands=" + String.Join(",", Bands.Select(b => r(b.ToLatDeg) + ":" + r(b.East) + "/" + r(b.North))) + " blend " + r(BlendDeg) + " perturbation " + r(Amplitude) + "rad/" + r(WavelengthDeg) + "deg/" + Octaves +
                    ";snowSeason=month " + r(MonthThreshold) + " iceCap " + IceCapByte + ";snowTexture=" + SnowRoute + " " + SnowTextureDimension + ";grassland=" + r(GrassThreshold) + " " + GrassDry + "/" + GrassWet + " mode " + ModeFilterCells;
            }

            private static object Need(IDictionary<string, object> d, string key) { object v; if (!d.TryGetValue(key, out v)) throw new InvalidDataException("climate-flow.json lacks '" + key + "'."); return v; }
            private static IDictionary<string, object> Obj(IDictionary<string, object> d, string key) { var v = Need(d, key) as IDictionary<string, object>; if (v == null) throw new InvalidDataException("climate-flow.json '" + key + "' must be an object."); return v; }
            private static object[] Arr(IDictionary<string, object> d, string key) { var v = Need(d, key) as object[]; if (v == null) throw new InvalidDataException("climate-flow.json '" + key + "' must be an array."); return v; }
            private static string Text(IDictionary<string, object> d, string key) { var v = Need(d, key) as string; if (String.IsNullOrEmpty(v)) throw new InvalidDataException("climate-flow.json '" + key + "' must be a string."); return v; }
            private static double Num(IDictionary<string, object> d, string key) { var v = Need(d, key); if (!(v is int || v is long || v is decimal || v is double)) throw new InvalidDataException("climate-flow.json '" + key + "' must be a number."); return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            private static int Int(IDictionary<string, object> d, string key) { var v = Need(d, key); if (!(v is int || v is long)) throw new InvalidDataException("climate-flow.json '" + key + "' must be an integer."); return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
        }

        public sealed class Source
        {
            public int Side;
            public double[] Rainfall, IceCap, SnowMax, Biome, Temperature, Moisture;   // Side x Side, row-major (row 0 the north pole line)
            public double[][] Snow;   // the 12 monthly snow grids (Side x Side each)
            public double[] WaterType, WaterSurface;   // layers water_type (0 land, 1 ocean, 2 lake) and water_surface (metres); null when absent
            public string Sha256;
            public int Months;
            public long Seed;   // config.seed
            public double RainfallMax;
            // MMR-257: world.json `ruins[]` as read (objects of strings, numbers, booleans, arrays and objects); the map dressing reads them.
            public List<IDictionary<string, object>> Ruins = new List<IDictionary<string, object>>();
            public Dictionary<string, string> CivilizationNames = new Dictionary<string, string>(StringComparer.Ordinal);   // civilizations.entities[] id -> name
            // MMR-260: the magic layers, all optional (a magic-off world still loads): layers.biome_variant (-1 or an index into
            // terrain.magical_biomes), layers.ley_<school>, and magic {enabled, mutation_threshold, dominance_margin, school_order}.
            public double[] BiomeVariant;
            public Dictionary<string, double[]> Ley = new Dictionary<string, double[]>(StringComparer.Ordinal);
            public List<IDictionary<string, object>> MagicalBiomes = new List<IDictionary<string, object>>();
            public bool MagicEnabled;
            public double MagicThreshold = Double.NaN, MagicDominance = Double.NaN;
            public List<string> MagicSchoolOrder = new List<string>();
            // MMR-261: heroes.realms[] (uid, capital_uid, city_uids), the capital rule's source realms; empty when absent.
            public List<IDictionary<string, object>> Realms = new List<IDictionary<string, object>>();
        }

        public sealed class Result
        {
            public float[] Wind;            // WindDimension^2 (x, y) pairs
            public byte[] Precipitation;    // WeatherDimension^2 (snow, rain) pairs
            public byte[] ColorGrade;       // ColorGradeDimension^2 grade bytes
            public byte[] SnowTexture;      // SnowTextureDimension^2 RGBA pixels (row 0 at physical y = extent)
            public string SnowRoute, SnowTextureName;   // the policy's route; the texture's name ("mnbl_snow_" + 16 hex digits of its hash + ".png"), null on the fallback route
            public string WindSha256, PrecipitationSha256, ColorGradeSha256, SnowTextureSha256, Identity, PolicyText;
            public SortedDictionary<string, object> Record;
        }

        // ---- the source (a streaming read of world.json: only the climate arrays are kept) ----
        // A world.json file: hashed, then read.
        public static Source ReadSource(string path)
        {
            string sha256;
            using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) sha256 = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            using (var stream = File.OpenRead(path)) return ReadSource(stream, sha256);
        }

        // sha256: the identity of the bytes the stream holds (checked by the caller: a bundle's ReadVerified, or ReadSource(path)).
        public static Source ReadSource(Stream worldJson, string sha256)
        {
            if (worldJson == null || String.IsNullOrEmpty(sha256)) throw new ArgumentNullException();
            var source = new Source { Sha256 = sha256 };
            var layers = new Dictionary<string, double[][]>(StringComparer.Ordinal);
            var monthlySnow = new List<double[][]>();
            double seed = Double.NaN;
            using (var reader = new StreamReader(worldJson, new UTF8Encoding(false), true, 1 << 16))
            {
                var s = new Scanner(reader);
                s.Object(top =>
                {
                    if (top == "layers") s.Object(k => { if (k == "rainfall" || k == "ice_cap" || k == "biome" || k == "temperature" || k == "moisture" || k == "water_type" || k == "water_surface" || k == "biome_variant" || k.StartsWith("ley_", StringComparison.Ordinal)) layers[k] = s.Grid(); else s.Skip(); });
                    else if (top == "magic") s.Object(k =>
                    {
                        if (k == "enabled") source.MagicEnabled = Equals(s.Value(), true);
                        else if (k == "mutation_threshold") source.MagicThreshold = s.Number();
                        else if (k == "dominance_margin") source.MagicDominance = s.Number();
                        else if (k == "school_order") s.Array(() => { var name = s.Value() as string; if (name == null) throw new InvalidDataException("world.json: magic.school_order holds a non-string."); source.MagicSchoolOrder.Add(name); });
                        else s.Skip();
                    });
                    else if (top == "terrain") s.Object(k =>
                    {
                        if (k != "magical_biomes") { s.Skip(); return; }
                        s.Array(() => { var e = s.Value() as IDictionary<string, object>; if (e == null) throw new InvalidDataException("world.json: a magical biome is not an object."); source.MagicalBiomes.Add(e); });
                    });
                    else if (top == "seasonal_environment") s.Object(k => { if (k == "months") s.Array(() => s.Object(f => { if (f == "snow") monthlySnow.Add(s.Grid()); else s.Skip(); })); else s.Skip(); });
                    else if (top == "config") s.Object(k => { if (k == "seed") seed = s.Number(); else s.Skip(); });
                    else if (top == "civilizations") s.Object(k =>
                    {
                        if (k != "entities") { s.Skip(); return; }
                        s.Array(() => { var e = s.Value() as IDictionary<string, object>; object id, name; if (e != null && e.TryGetValue("id", out id) && e.TryGetValue("name", out name) && id is string && name is string) source.CivilizationNames[(string)id] = (string)name; });
                    });
                    else if (top == "heroes") s.Object(k =>
                    {
                        if (k != "realms") { s.Skip(); return; }
                        s.Array(() => { var e = s.Value() as IDictionary<string, object>; if (e == null) throw new InvalidDataException("world.json: a realm is not an object."); source.Realms.Add(e); });
                    });
                    else if (top == "ruins") s.Array(() => { var ruin = s.Value() as IDictionary<string, object>; if (ruin == null) throw new InvalidDataException("world.json: a ruin is not an object."); source.Ruins.Add(ruin); });
                    else s.Skip();
                });
                s.Whitespace();
                if (s.Peek() != -1) throw new InvalidDataException("world.json has content after its root object.");
            }
            foreach (var name in new[] { "rainfall", "ice_cap", "biome", "temperature", "moisture" })
                if (!layers.ContainsKey(name)) throw new InvalidDataException("world.json lacks the " + name + " layer.");
            if (monthlySnow.Count != 12) throw new InvalidDataException("world.json has " + monthlySnow.Count + " seasonal snow months, not 12.");
            if (Double.IsNaN(seed) || seed != Math.Floor(seed) || Math.Abs(seed) > 9e15) throw new InvalidDataException("world.json has no integral config.seed.");
            int side = layers["rainfall"].Length;
            source.Side = side; source.Months = monthlySnow.Count; source.Seed = (long)seed;
            source.Rainfall = Flatten(layers["rainfall"], side, "rainfall"); source.IceCap = Flatten(layers["ice_cap"], side, "ice_cap");
            source.Biome = Flatten(layers["biome"], side, "biome"); source.Temperature = Flatten(layers["temperature"], side, "temperature");
            source.Moisture = Flatten(layers["moisture"], side, "moisture");
            if (layers.ContainsKey("water_type") && layers.ContainsKey("water_surface"))
            { source.WaterType = Flatten(layers["water_type"], side, "water_type"); source.WaterSurface = Flatten(layers["water_surface"], side, "water_surface"); }
            foreach (double b in source.Biome) if (b != Math.Floor(b) || b < 0) throw new InvalidDataException("world.json's biome layer holds a value that is not a biome id.");
            if (layers.ContainsKey("biome_variant"))
            {
                source.BiomeVariant = Flatten(layers["biome_variant"], side, "biome_variant");
                foreach (double v in source.BiomeVariant) if (v != Math.Floor(v) || v < -1 || v >= source.MagicalBiomes.Count) throw new InvalidDataException("world.json's biome_variant layer holds a value that is not -1 or a magical biome index.");
            }
            foreach (var kv in layers) if (kv.Key.StartsWith("ley_", StringComparison.Ordinal)) source.Ley[kv.Key.Substring(4)] = Flatten(kv.Value, side, kv.Key);
            source.SnowMax = new double[side * side];
            source.Snow = monthlySnow.Select(month => Flatten(month, side, "snow")).ToArray();
            foreach (var m in source.Snow) for (int i = 0; i < m.Length; i++) source.SnowMax[i] = Math.Max(source.SnowMax[i], m[i]);
            source.RainfallMax = 0; foreach (double v in source.Rainfall) source.RainfallMax = Math.Max(source.RainfallMax, v);
            if (!(source.RainfallMax > 0)) throw new InvalidDataException("world.json's rainfall layer is zero everywhere.");
            return source;
        }

        private static double[] Flatten(double[][] grid, int side, string name)
        {
            if (grid.Length != side) throw new InvalidDataException("world.json layer " + name + " has " + grid.Length + " rows, not " + side + ".");
            var flat = new double[side * side];
            for (int r = 0; r < side; r++)
            {
                if (grid[r].Length != side) throw new InvalidDataException("world.json layer " + name + " row " + r + " has " + grid[r].Length + " columns, not " + side + ".");
                Array.Copy(grid[r], 0, flat, r * side, side);
            }
            return flat;
        }

        // ---- synthesis ----
        public static string PolicyText(Source source, PeriodicWorldCoordinates c, Policy policy)
        {
            Func<double, string> r = v => v.ToString("R", CultureInfo.InvariantCulture);
            return "schema=mnbl.climate-flow;version=" + Version + ";source=" + source.Sha256 + ";seed=" + source.Seed.ToString(CultureInfo.InvariantCulture) + ";coordinates=" + c.Identity + ";wind=" + WindDimension +
                " (x,y) float pairs, latitude bands, seeded perturbation, smoothstep pole taper over " + r(PoleTaperRows) + " source rows, scale " + r(WindScale) +
                ", map vector (east,-north);weather=" + WeatherDimension + " (snow,rain) bytes, " + NodeSubsamples + "x" + NodeSubsamples +
                " sub-samples per node, rain=rainfall/max, byte=round(255v), snow=the inverse installed formula of the mean season length, ice cap by majority;snowTexture=" + SnowTextureDimension +
                " RGBA, row 0 at y=extent;colorGrade=" + ColorGradeDimension + " grade bytes, nearest source cell's biome " + String.Join(",", BiomeGrades.Select(g => g.Key + ">" + g.Value)) + ", water and other ids by temperature (<" + r(ColdBelowC) +
                " " + GradeNorth + ", <" + r(WarmFromC) + " " + GradeNormandy + ", else " + GradeMediterranean + "), mode-filtered between the pole lines;samples=canonical location, beyond the poles the pole value;policy=" + policy.Text();
        }

        // The grade of a cell: its biome's (grassland by the cell's moisture), else (water, unlisted ids) its temperature band.
        public static byte GradeFor(int biome, double moisture, double temperatureC, Policy policy, out bool byTemperature)
        {
            byTemperature = false;
            if (biome == GrasslandBiome) return moisture < policy.GrassThreshold ? policy.GrassDry : policy.GrassWet;
            foreach (var g in BiomeGrades) if (g.Key == biome) return g.Value;
            byTemperature = true;
            return temperatureC < ColdBelowC ? GradeNorth : temperatureC < WarmFromC ? GradeNormandy : GradeMediterranean;
        }

        // The nearest source cell of a fractional grid position (columns wrap, rows clamp).
        public static int Nearest(int side, double column, double row)
        {
            int n = side - 1;
            int c = (int)Math.Round(column, MidpointRounding.AwayFromZero) % n, r = (int)Math.Round(Math.Max(0, Math.Min(n, row)), MidpointRounding.AwayFromZero);
            if (c < 0) c += n;
            return r * side + c;
        }

        // The snow of one sub-sample at a fractional source position: its season length (months at or above the threshold) and whether it
        // is ice cap.
        public static int SeasonLength(Source source, double column, double row, double threshold, out bool iceCap)
        {
            iceCap = Bilinear(source.IceCap, source.Side, column, row) >= threshold;
            int k = 0;
            foreach (var month in source.Snow) if (Bilinear(month, source.Side, column, row) >= threshold) k++;
            return k;
        }

        public static Result Synthesize(Source source, PeriodicWorldCoordinates c, Policy policy)
        {
            if (source == null || c == null || policy == null) throw new ArgumentNullException();
            if (c.SourceColumns <= 0 || c.SourceColumns + 1 != source.Side)
                throw new InvalidDataException("The layout's source grid (" + (c.SourceColumns + 1) + ") is not the source climate's (" + source.Side + ").");
            // Strip samples copy canonical samples exactly only when the width and the strip hold whole numbers of samples of every grid.
            foreach (int dimension in new[] { WindDimension, WeatherDimension, ColorGradeDimension, SnowTextureDimension })
                foreach (double length in new[] { c.Width, c.OriginX })
                {
                    double samples = length * dimension / c.Extent;
                    if (Math.Abs(samples - Math.Round(samples)) > 1e-9) throw new InvalidDataException("A " + dimension + " sample grid puts " + samples + " samples in a length of " + length + "; a whole number is required.");
                }
            var noise = new WindNoise(source.Seed, policy);
            int n = source.Side - 1;
            double windStep = c.Extent / WindDimension, nodeStep = c.Extent / WeatherDimension;
            var wind = new float[WindDimension * WindDimension * 2];
            double minAngle = 0, maxAngle = 0;
            for (int row = 0; row < WindDimension; row++)
                for (int col = 0; col < WindDimension; col++)
                {
                    double gc, gr; SourceGrid(c, (col + .5) * windStep, (row + .5) * windStep, out gc, out gr);
                    double lat = 90 - 180 * gr / n, lon = 360 * gc / n;
                    double east, north; BandDirection(policy, lat, out east, out north);
                    double angle = policy.Amplitude * noise.At(lon, lat), ca = Math.Cos(angle), sa = Math.Sin(angle);
                    minAngle = Math.Min(minAngle, angle); maxAngle = Math.Max(maxAngle, angle);
                    double e = east * ca - north * sa, no = east * sa + north * ca;
                    double taper = SmoothStep(Math.Min(gr, n - gr) / PoleTaperRows);
                    int i = 2 * (row * WindDimension + col);
                    wind[i] = (float)(WindScale * taper * e); wind[i + 1] = (float)(-WindScale * taper * no);
                }
            var precipitation = new byte[WeatherDimension * WeatherDimension * 2];
            var nodeSeason = new double[WeatherDimension * WeatherDimension];
            var nodeIce = new bool[WeatherDimension * WeatherDimension];
            for (int row = 0; row < WeatherDimension; row++)
                for (int col = 0; col < WeatherDimension; col++)
                {
                    double months = 0, rain = 0; int ice = 0;
                    for (int sy = 0; sy < NodeSubsamples; sy++)
                        for (int sx = 0; sx < NodeSubsamples; sx++)
                        {
                            double gc, gr; SourceGrid(c, (col + (sx + .5) / NodeSubsamples) * nodeStep, (row + (sy + .5) / NodeSubsamples) * nodeStep, out gc, out gr);
                            bool iceCap; int k = SeasonLength(source, gc, gr, policy.MonthThreshold, out iceCap);
                            if (iceCap) { ice++; months += 12; } else months += k;
                            rain += Bilinear(source.Rainfall, source.Side, gc, gr) / source.RainfallMax;
                        }
                    int count = NodeSubsamples * NodeSubsamples, node = row * WeatherDimension + col, i = 2 * node;
                    nodeSeason[node] = months / count / 12; nodeIce[node] = 2 * ice >= count;
                    precipitation[i] = nodeIce[node] ? policy.IceCapByte : SnowByte(nodeSeason[node]); precipitation[i + 1] = ToByte(rain / count);
                }
            double pixel = c.Extent / SnowTextureDimension;
            var snowTexture = new byte[SnowTextureDimension * SnowTextureDimension * 4];
            for (int row = 0; row < SnowTextureDimension; row++)
                for (int col = 0; col < SnowTextureDimension; col++)
                {
                    double gc, gr; SourceGrid(c, (col + .5) * pixel, c.Extent - (row + .5) * pixel, out gc, out gr);
                    bool iceCap; int k = SeasonLength(source, gc, gr, policy.MonthThreshold, out iceCap);
                    int i = 4 * (row * SnowTextureDimension + col);
                    snowTexture[i] = iceCap ? policy.IceCapByte : SnowByte(k / 12.0);
                    snowTexture[i + 1] = ToByte(Bilinear(source.Rainfall, source.Side, gc, gr) / source.RainfallMax);
                    snowTexture[i + 2] = 0; snowTexture[i + 3] = 255;
                }
            int byTemperature;
            var grade = ColorGradeGrid(source, c, policy, out byTemperature);
            var result = new Result { Wind = wind, Precipitation = precipitation, ColorGrade = grade, SnowTexture = snowTexture, SnowRoute = policy.SnowRoute, PolicyText = PolicyText(source, c, policy) };
            result.WindSha256 = Sha(Bytes(wind)); result.PrecipitationSha256 = Sha(precipitation); result.ColorGradeSha256 = Sha(grade); result.SnowTextureSha256 = Sha(snowTexture);
            result.SnowTextureName = policy.SnowRoute == Policy.NamedTexture ? SnowTextureName(result.SnowTextureSha256) : null;
            result.Identity = Sha(new UTF8Encoding(false).GetBytes(IdentityText(result.PolicyText, result.WindSha256, result.PrecipitationSha256, result.ColorGradeSha256, result.SnowTextureSha256)));
            result.Record = Statistics(result, c, source, policy, nodeSeason, nodeIce);
            result.Record["colorGradeByTemperature"] = byTemperature;
            result.Record["windPerturbationRadians"] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["min"] = Math.Round(minAngle, 6), ["max"] = Math.Round(maxAngle, 6) };
            return result;
        }

        // MMR-247 (acc4): the texture's name is its PNG's file name, extension included. The engine keeps a texture loaded with
        // Texture.LoadTextureFromPath under its file name (acc4's record: engineName "mnbl_snow_6842edaae0521518.png"); renaming it
        // (Texture.Name) did not make the scene's extension-less name resolve (resolvesByName false, and every read of the map logged "Cannot
        // find texture: mnbl_snow_6842edaae0521518"). So the scene's terrain names the file, and GeneratedSnowTexture loads that file and
        // never renames it.
        public const string SnowTextureExtension = ".png";
        public static string SnowTextureName(string sha256) { return "mnbl_snow_" + sha256.Substring(0, 16) + SnowTextureExtension; }
        public static bool IsSnowTextureName(string name)
        {
            const string prefix = "mnbl_snow_";
            return name != null && name.Length == prefix.Length + 16 + SnowTextureExtension.Length && name.StartsWith(prefix, StringComparison.Ordinal)
                && name.EndsWith(SnowTextureExtension, StringComparison.Ordinal) && name.Substring(prefix.Length, 16).All(ch => (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'));
        }

        // The text the identity hashes (the placement's v2 block is checked against it on load).
        public static string IdentityText(string policy, string windSha256, string precipitationSha256, string colorGradeSha256, string snowTextureSha256)
        {
            return policy + ";windSha256=" + windSha256 + ";precipitationSha256=" + precipitationSha256 + ";colorGradeSha256=" + colorGradeSha256 + ";snowTextureSha256=" + snowTextureSha256;
        }

        // The band direction (east, north) at a latitude: each band's weight is the smoothstep rise at its lower edge less the rise at its
        // upper edge (blendDeg wide, centred on the edge; none at 0 and 90), so the weights sum to 1; the north component flips sign across
        // the equator by the same smoothstep.
        public static void BandDirection(Policy policy, double lat, out double east, out double north)
        {
            double a = Math.Abs(lat), half = policy.BlendDeg / 2, lower = 0;
            east = 0; north = 0;
            for (int i = 0; i < policy.Bands.Count; i++)
            {
                double upper = policy.Bands[i].ToLatDeg;
                double rise = i == 0 ? 1 : SmoothStep((a - (lower - half)) / policy.BlendDeg), fall = i == policy.Bands.Count - 1 ? 0 : SmoothStep((a - (upper - half)) / policy.BlendDeg);
                double w = rise - fall;
                east += w * policy.Bands[i].East; north += w * policy.Bands[i].North;
                lower = upper;
            }
            north *= 2 * SmoothStep((lat + half) / policy.BlendDeg) - 1;
        }

        // Seeded value noise in [-1, 1] over (longitude, latitude) degrees: periodic in longitude, latitude clamped; octave o has cells of
        // wavelength / 2^o degrees and weight 2^-o.
        private sealed class WindNoise
        {
            private readonly Policy policy; private readonly double[][,] values;
            public WindNoise(long seed, Policy policy)
            {
                this.policy = policy; values = new double[policy.Octaves][,];
                for (int o = 0; o < policy.Octaves; o++)
                {
                    double cell = policy.WavelengthDeg / (1 << o);
                    int columns = (int)Math.Round(360 / cell), rows = (int)Math.Ceiling(180 / cell) + 1;
                    values[o] = new double[columns, rows];
                    for (int i = 0; i < columns; i++) for (int j = 0; j < rows; j++) values[o][i, j] = Value(seed, o, i, j);
                }
            }
            public double At(double lon, double lat)
            {
                double sum = 0, weights = 0;
                for (int o = 0; o < policy.Octaves; o++)
                {
                    var v0 = values[o]; int columns = v0.GetLength(0), rows = v0.GetLength(1);
                    double cell = policy.WavelengthDeg / (1 << o), weight = 1.0 / (1 << o);
                    double u = ((lon / cell) % columns + columns) % columns, v = Math.Max(0, Math.Min(rows - 1, (lat + 90) / cell));
                    int i0 = Math.Min(columns - 1, (int)Math.Floor(u)), j0 = Math.Min(rows - 1, (int)Math.Floor(v)); double fu = SmoothStep(u - i0), fv = SmoothStep(v - j0);
                    int i1 = (i0 + 1) % columns, j1 = Math.Min(rows - 1, j0 + 1);
                    double top = v0[i0, j0] * (1 - fu) + v0[i1, j0] * fu, bottom = v0[i0, j1] * (1 - fu) + v0[i1, j1] * fu;
                    sum += weight * (top * (1 - fv) + bottom * fv); weights += weight;
                }
                return sum / weights;
            }
            private static double Value(long seed, int octave, int i, int j)
            {
                ulong h = 14695981039346656037UL;
                foreach (byte b in Encoding.ASCII.GetBytes("wind/1:" + seed.ToString(CultureInfo.InvariantCulture) + ":" + octave + ":" + i + ":" + j)) { h ^= b; h *= 1099511628211UL; }
                // FNV-1a leaves keys that differ in their last bytes close in the high bits: the MurmurHash3 finalizer mixes them.
                h ^= h >> 33; h *= 0xff51afd7ed558ccdUL; h ^= h >> 33; h *= 0xc4ceb9fe1a85ec53UL; h ^= h >> 33;
                return (h >> 11) / (double)(1UL << 53) * 2 - 1;
            }
        }

        // The colour-grade grid: each cell's grade (GradeFor at its centre's canonical location), then a mode filter over the policy's window
        // for the cells between the pole lines (columns past the grid's edges continue one width away, rows clamp; a tie keeps the cell's own
        // grade when it is among the most frequent, else the smallest). Cells beyond the pole lines keep the pole's grade.
        private static byte[] ColorGradeGrid(Source source, PeriodicWorldCoordinates c, Policy policy, out int byTemperature)
        {
            int dim = ColorGradeDimension; double step = c.Extent / dim;
            var raw = new byte[dim * dim];
            byTemperature = 0;
            for (int row = 0; row < dim; row++)
                for (int col = 0; col < dim; col++)
                {
                    double gc, gr; SourceGrid(c, (col + .5) * step, (row + .5) * step, out gc, out gr);
                    int nearest = Nearest(source.Side, gc, gr);
                    bool band; raw[row * dim + col] = GradeFor((int)source.Biome[nearest], source.Moisture[nearest], Bilinear(source.Temperature, source.Side, gc, gr), policy, out band);
                    if (band) byTemperature++;
                }
            int period = (int)Math.Round(c.Width / step), radius = policy.ModeFilterCells / 2;
            var grade = (byte[])raw.Clone();
            var counts = new int[256];
            for (int row = 0; row < dim; row++)
            {
                double y = (row + .5) * step;
                if (y < c.OriginY || y > c.OriginY + c.Height) continue;
                for (int col = 0; col < dim; col++)
                {
                    Array.Clear(counts, 0, counts.Length);
                    for (int dy = -radius; dy <= radius; dy++)
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int r = Math.Max(0, Math.Min(dim - 1, row + dy)), x = col + dx;
                            while (x < 0) x += period; while (x >= dim) x -= period;
                            counts[raw[r * dim + x]]++;
                        }
                    byte own = raw[row * dim + col]; int best = counts[own]; byte pick = own;
                    for (int g = 0; g < 256; g++) if (counts[g] > best) { best = counts[g]; pick = (byte)g; }
                    grade[row * dim + col] = pick;
                }
            }
            return grade;
        }

        private static double SmoothStep(double t) { t = Math.Max(0, Math.Min(1, t)); return t * t * (3 - 2 * t); }

        private static byte ToByte(double v) { return (byte)Math.Round(255 * Math.Max(0, Math.Min(1, v)), MidpointRounding.AwayFromZero); }

        // The fractional source grid position (column, row) of a physical point: canonical logical x, y clamped to the pole lines.
        public static void SourceGrid(PeriodicWorldCoordinates c, double physicalX, double physicalY, out double column, out double row)
        {
            double n = c.SourceColumns, lx = c.LogicalX(physicalX), ly = Math.Max(0, Math.Min(c.Height, physicalY - c.OriginY));
            column = n * (lx / c.Width + c.CentralMeridianDeg / 360.0) % n; if (column < 0) column += n;
            row = n * ly / c.Height;
        }

        // A 129-grid layer at a fractional position: columns wrap (column n-1 duplicates 0), rows clamp.
        public static double Bilinear(double[] layer, int side, double column, double row)
        {
            int n = side - 1;
            row = Math.Max(0, Math.Min(n, row));
            int c0 = (int)Math.Floor(column), r0 = (int)Math.Floor(row);
            double fc = column - c0, fr = row - r0;
            c0 = ((c0 % n) + n) % n; int c1 = c0 + 1, r1 = Math.Min(n, r0 + 1);
            double top = layer[r0 * side + c0] * (1 - fc) + layer[r0 * side + c1] * fc, bottom = layer[r1 * side + c0] * (1 - fc) + layer[r1 * side + c1] * fc;
            return top * (1 - fr) + bottom * fr;
        }

        private static SortedDictionary<string, object> Statistics(Result r, PeriodicWorldCoordinates c, Source source, Policy policy, double[] nodeSeason, bool[] nodeIce)
        {
            double min = double.MaxValue, max = 0, sum = 0, maxStep = 0;
            int samples = WindDimension * WindDimension;
            for (int i = 0; i < samples; i++)
            {
                double m = Math.Sqrt(r.Wind[2 * i] * r.Wind[2 * i] + r.Wind[2 * i + 1] * r.Wind[2 * i + 1]);
                min = Math.Min(min, m); max = Math.Max(max, m); sum += m;
                if (i % WindDimension != WindDimension - 1) maxStep = Math.Max(maxStep, Math.Abs(r.Wind[2 * i] - r.Wind[2 * i + 2]) + Math.Abs(r.Wind[2 * i + 1] - r.Wind[2 * i + 3]));
                if (i + WindDimension < samples) maxStep = Math.Max(maxStep, Math.Abs(r.Wind[2 * i] - r.Wind[2 * (i + WindDimension)]) + Math.Abs(r.Wind[2 * i + 1] - r.Wind[2 * (i + WindDimension) + 1]));
            }
            // The mean map vector of each band's canonical, untapered rows (the per-world wind record, RT-18).
            int n = source.Side - 1;
            var bands = new List<object>();
            double lower = 0;
            foreach (var band in policy.Bands)
                foreach (int hemisphere in new[] { 1, -1 })
                {
                    double ex = 0, ey = 0; int count = 0;
                    double windStep = c.Extent / WindDimension;
                    for (int row = 0; row < WindDimension; row++)
                    {
                        double gc, gr; SourceGrid(c, c.OriginX + .5 * windStep, (row + .5) * windStep, out gc, out gr);
                        double lat = 90 - 180 * gr / n, a = Math.Abs(lat);
                        if (Math.Sign(lat) != hemisphere || a < lower + policy.BlendDeg || a > band.ToLatDeg - policy.BlendDeg || Math.Min(gr, n - gr) < PoleTaperRows) continue;
                        int origin = (int)Math.Round(c.OriginX / windStep), period = (int)Math.Round(c.Width / windStep);
                        for (int col = origin; col < origin + period; col++) { ex += r.Wind[2 * (row * WindDimension + col)]; ey += r.Wind[2 * (row * WindDimension + col) + 1]; count++; }
                    }
                    bands.Add(new SortedDictionary<string, object>(StringComparer.Ordinal) { ["fromLatDeg"] = lower, ["toLatDeg"] = band.ToLatDeg, ["hemisphere"] = hemisphere > 0 ? "north" : "south", ["samples"] = count,
                        ["meanMapVector"] = count == 0 ? null : new[] { Math.Round(ex / count, 6), Math.Round(ey / count, 6) } });
                    if (hemisphere < 0) lower = band.ToLatDeg;
                }
            int snowy = 0, rainy = 0, ice = 0;
            var bytes = new SortedDictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i < WeatherDimension * WeatherDimension; i++)
            {
                if (r.Precipitation[2 * i] > 0) snowy++; if (r.Precipitation[2 * i + 1] > 0) rainy++; if (nodeIce[i]) ice++;
                string key = r.Precipitation[2 * i].ToString(CultureInfo.InvariantCulture); object have; bytes[key] = bytes.TryGetValue(key, out have) ? (int)have + 1 : 1;
            }
            int texSnow = 0; for (int i = 0; i < r.SnowTexture.Length; i += 4) if (r.SnowTexture[i] > 0) texSnow++;
            return new SortedDictionary<string, object>(StringComparer.Ordinal) {
                ["version"] = Version, ["identity"] = r.Identity, ["sourceSha256"] = source.Sha256, ["seed"] = source.Seed, ["coordinatePolicy"] = c.Identity,
                ["windSha256"] = r.WindSha256, ["precipitationSha256"] = r.PrecipitationSha256, ["colorGradeSha256"] = r.ColorGradeSha256, ["snowTextureSha256"] = r.SnowTextureSha256,
                ["snowRoute"] = r.SnowRoute, ["snowTextureName"] = r.SnowTextureName, ["snowTexturePixels"] = texSnow, ["months"] = source.Months,
                ["colorGrades"] = r.ColorGrade.GroupBy(g => g).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => (object)g.Count()),
                ["windMagnitude"] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["min"] = Math.Round(min, 6), ["mean"] = Math.Round(sum / samples, 6), ["max"] = Math.Round(max, 6) },
                ["windMaxNeighbourStep"] = Math.Round(maxStep, 6), ["windBands"] = bands, ["snowNodes"] = snowy, ["iceCapNodes"] = ice, ["snowNodeBytes"] = bytes, ["rainNodes"] = rainy, ["rainfallMax"] = source.RainfallMax };
        }

        public static byte[] Bytes(float[] values) { var bytes = new byte[values.Length * 4]; Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length); return bytes; }
        public static string Sha(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }

        // ---- the colour-grade manager of a fixture scene, and the snow texture's scene attribute ----
        // The colour-grade manager a fixture scene carries (the installed script with the official Main_map variables); the world's grid
        // replaces the one it reads (GeneratedColorGrade). Production scenes carry none (GeneratedMapAtmosphere does its tick).
        public static string ColorGradeManagerXml(string name)
        {
            if (String.IsNullOrEmpty(name) || name.IndexOfAny(new[] { '"', '<', '>', '&' }) >= 0) throw new ArgumentException("A plain entity name is required.");
            return "<game_entity name=\"" + name + "\" mobility=\"1\"><transform position=\"0, 0, 0\" rotation_euler=\"0, 0, 0\" /><scripts><script name=\"MapColorGradeManager\"><variables>" +
                "<variable name=\"ColorGradeEnabled\" value=\"true\" /><variable name=\"AtmosphereSimulationEnabled\" value=\"false\" />" +
                "<variable name=\"TimeOfDay\" value=\"6.000\" /><variable name=\"SeasonTimeFactor\" value=\"0.000\" /></variables></script></scripts></game_entity>";
        }

        public static string WithColorGradeManager(string sceneXml, string name)
        {
            if (sceneXml == null) throw new ArgumentNullException("sceneXml");
            int close = sceneXml.IndexOf("</entities>", StringComparison.Ordinal);
            if (close < 0 || sceneXml.IndexOf("</entities>", close + 1, StringComparison.Ordinal) >= 0) throw new InvalidDataException("The scene has no single entity list.");
            if (sceneXml.IndexOf("\"MapColorGradeManager\"", StringComparison.Ordinal) >= 0) throw new InvalidDataException("The scene already has a colour-grade manager.");
            return sceneXml.Substring(0, close) + ColorGradeManagerXml(name) + sceneXml.Substring(close);
        }

        // MMR-256: the scene's terrain names its snow texture (dynamic_flowmap_texture_name, read by the terrain at Read; MMR-249). The
        // template has none; a name is added once, before encoding: the texture's file name (SnowTextureName, MMR-247).
        public const string SnowTextureAttribute = "dynamic_flowmap_texture_name";
        public static string WithSnowTexture(string sceneXml, string textureName)
        {
            if (sceneXml == null) throw new ArgumentNullException("sceneXml");
            if (!IsSnowTextureName(textureName)) throw new ArgumentException("A snow texture name is its PNG's file name: mnbl_snow_, 16 hex digits and .png.");
            int at = sceneXml.IndexOf("<terrain ", StringComparison.Ordinal);
            if (at < 0 || sceneXml.IndexOf("<terrain ", at + 1, StringComparison.Ordinal) >= 0) throw new InvalidDataException("The scene has no single terrain.");
            int end = sceneXml.IndexOf('>', at);
            if (sceneXml.IndexOf(" " + SnowTextureAttribute + "=", at, end - at, StringComparison.Ordinal) >= 0) throw new InvalidDataException("The scene's terrain already names a snow texture.");
            return sceneXml.Substring(0, end) + " " + SnowTextureAttribute + "=\"" + textureName + "\"" + sceneXml.Substring(end);
        }

        // The snow texture a scene's terrain names, or null.
        public static string SnowTextureOf(string sceneXml)
        {
            if (sceneXml == null) return null;
            int at = sceneXml.IndexOf("<terrain ", StringComparison.Ordinal);
            if (at < 0) return null;
            int end = sceneXml.IndexOf('>', at), name = sceneXml.IndexOf(" " + SnowTextureAttribute + "=\"", at, end - at, StringComparison.Ordinal);
            if (name < 0) return null;
            int start = name + SnowTextureAttribute.Length + 3, close = sceneXml.IndexOf('"', start);
            return close < 0 || close > end ? null : sceneXml.Substring(start, close - start);
        }

        // ---- a minimal streaming JSON scanner (objects, arrays, numbers; everything else skipped) ----
        private sealed class Scanner
        {
            private readonly TextReader reader;
            private readonly char[] buffer = new char[1 << 16];
            private int length, position;
            private readonly StringBuilder text = new StringBuilder();
            public Scanner(TextReader reader) { this.reader = reader; }

            public int Peek()
            {
                if (position == length) { length = reader.Read(buffer, 0, buffer.Length); position = 0; if (length <= 0) { length = 0; return -1; } }
                return buffer[position];
            }
            private int Next() { int ch = Peek(); if (ch >= 0) position++; return ch; }
            private void Expect(char ch) { Whitespace(); if (Next() != ch) throw new InvalidDataException("world.json: expected '" + ch + "'."); }
            public void Whitespace() { while (true) { int ch = Peek(); if (ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r') position++; else return; } }

            public void Object(Action<string> member)
            {
                Expect('{'); Whitespace();
                if (Peek() == '}') { position++; return; }
                while (true)
                {
                    Whitespace(); string key = Text(); Expect(':'); Whitespace();
                    member(key);
                    Whitespace(); int ch = Next();
                    if (ch == '}') return;
                    if (ch != ',') throw new InvalidDataException("world.json: expected ',' or '}'.");
                }
            }

            public void Array(Action item)
            {
                Expect('['); Whitespace();
                if (Peek() == ']') { position++; return; }
                while (true)
                {
                    Whitespace(); item();
                    Whitespace(); int ch = Next();
                    if (ch == ']') return;
                    if (ch != ',') throw new InvalidDataException("world.json: expected ',' or ']'.");
                }
            }

            public double Number()
            {
                Whitespace(); text.Clear();
                while (true) { int ch = Peek(); if ((ch >= '0' && ch <= '9') || ch == '-' || ch == '+' || ch == '.' || ch == 'e' || ch == 'E') { text.Append((char)ch); position++; } else break; }
                double v;
                if (text.Length == 0 || !Double.TryParse(text.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) throw new InvalidDataException("world.json: expected a number.");
                return v;
            }

            public double[][] Grid()
            {
                var rows = new List<double[]>();
                Array(() => { var row = new List<double>(); Array(() => row.Add(Number())); rows.Add(row.ToArray()); });
                return rows.ToArray();
            }

            private string Text()
            {
                if (Next() != '"') throw new InvalidDataException("world.json: expected a string.");
                text.Clear();
                while (true)
                {
                    int ch = Next();
                    if (ch < 0) throw new InvalidDataException("world.json: unterminated string.");
                    if (ch == '"') return text.ToString();
                    if (ch == '\\')
                    {
                        int e = Next();
                        if (e == 'u')
                        {
                            int code = 0;
                            for (int i = 0; i < 4; i++)
                            {
                                int h = Next(), d = h >= '0' && h <= '9' ? h - '0' : h >= 'a' && h <= 'f' ? h - 'a' + 10 : h >= 'A' && h <= 'F' ? h - 'A' + 10 : -1;
                                if (d < 0) throw new InvalidDataException("world.json: a bad unicode escape.");
                                code = code * 16 + d;
                            }
                            text.Append((char)code);
                        }
                        else text.Append(e == 'n' ? '\n' : e == 't' ? '\t' : e == 'r' ? '\r' : e == 'b' ? '\b' : e == 'f' ? '\f' : (char)e);
                        continue;
                    }
                    text.Append((char)ch);
                }
            }

            // MMR-257: any value, built (objects as ordinal dictionaries, arrays as object[], numbers as double, true, false and null).
            public object Value()
            {
                Whitespace();
                int ch = Peek();
                if (ch == '{') { var o = new Dictionary<string, object>(StringComparer.Ordinal); Object(k => o[k] = Value()); return o; }
                if (ch == '[') { var a = new List<object>(); Array(() => a.Add(Value())); return a.ToArray(); }
                if (ch == '"') return Text();
                if (ch == 't' || ch == 'f' || ch == 'n')
                {
                    text.Clear();
                    while (true) { int c = Peek(); if (c >= 'a' && c <= 'z') { text.Append((char)c); position++; } else break; }
                    string word = text.ToString();
                    if (word == "true") return true;
                    if (word == "false") return false;
                    if (word == "null") return null;
                    throw new InvalidDataException("world.json: unexpected '" + word + "'.");
                }
                return Number();
            }

            // Any value, without building it.
            public void Skip()
            {
                Whitespace();
                int ch = Peek();
                if (ch == '{') { Object(k => Skip()); return; }
                if (ch == '[') { Array(Skip); return; }
                if (ch == '"') { Text(); return; }
                while (true) { ch = Peek(); if (ch < 0 || ch == ',' || ch == '}' || ch == ']' || ch == ' ' || ch == '\n' || ch == '\r' || ch == '\t') return; position++; }
            }
        }
    }
}
