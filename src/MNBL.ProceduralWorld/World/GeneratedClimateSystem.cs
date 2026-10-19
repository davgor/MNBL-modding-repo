using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MNBL.ProceduralWorld.Surface
{
    // MMR-39: the deterministic synthesis of the engine's weather fields from a generated world's source climate
    // (worldgen/policies/climate-flow.json, docs/water-climate.md). FWGC++ exports no engine flow or weather field, so the host
    // builds them from what the source does carry:
    //   * source: world.json `climate.wind_arrows` (fixed compass-bearing transport directions on an 8 x 6 lattice of the 129 grid),
    //     `layers` (rainfall, ice_cap; 129 x 129) and `seasonal_environment.months[*].snow` (12 monthly 129 x 129 grids);
    //   * wind: SandBox.MapScene's 512 x 512 (x, y) float layout over the physical terrain. Each sample's centre is mapped to its
    //     canonical logical location (PeriodicWorldCoordinates) and to the source grid (projection inverse.sourceGrid); the
    //     arrows' local (east, north) directions are interpolated bilinearly on their lattice (columns periodic, rows clamped),
    //     faded to zero (smoothstep over PoleTaperRows) towards each pole line (the pole is one location, so it has one value) and
    //     scaled by WindScale. Map
    //     vectors are (east, -north): physical y runs south. Calms appear where neighbouring arrows disagree;
    //   * snow and rain: the 32 x 32 (snow, rain) byte layout. Each node averages 8 x 8 sub-samples of its footprint: rain is
    //     rainfall over the layer's maximum (the source's values are relative proxies), snow the largest monthly snow cover
    //     (or the ice cap, whichever is larger); bytes are round(255 x value). The season itself is the engine's (global);
    //   * colour grade: the installed MapColorGradeManager's 512 x 512 grade bytes (worldmap_color_grades.xml values) over the terrain,
    //     in place of Calradia's worldmap_colorgrade_grid texture. Each cell takes the grade of the FWGC++ natural biome of its nearest
    //     source cell (BiomeGrades); water and unlisted biomes take a temperature band (cold north, temperate normandy, warm
    //     mediterranean, from the source's temperature layer at the cell);
    //   * strips and padding: every sample is taken at its canonical location, so a strip sample is an exact copy of its canonical
    //     sample (the grids put a whole number of samples in a width) and the rows beyond each pole line repeat the pole's value.
    // Pure managed: the in-game binding (GeneratedClimate, GeneratedMapSupport.PrepareWeather) and the offline tests share it.
    public static class GeneratedClimateSystem
    {
        public const string Version = "mmr39/1";
        public const int WindDimension = 512, WeatherDimension = 32, NodeSubsamples = 8;
        public const double WindScale = 0.4;   // inside War Sails' weather-model clamp [0.0667, 0.46], above the official flowmap's background (0.2)
        public const double PoleTaperRows = 16; // source rows over which the wind fades (smoothstep) to zero at each pole line
        public const int ColorGradeDimension = 512;   // MapColorGradeManager's grid (262144 bytes, index row * 512 + col over the terrain)
        // The installed grades (Modules/Native/ModuleData/worldmap_color_grades.xml).
        public const byte GradeDesert = 20, GradeSteppe = 40, GradeCentralEurope = 60, GradeHighlands = 80, GradeNormandy = 100, GradeNorth = 120, GradeMediterranean = 140;
        public const double ColdBelowC = 2, WarmFromC = 18;   // the water and fallback temperature bands (source temperature, degrees C)
        // FWGC++ natural biome ids (generator/data/biomes.json; ids are append-only) and their grades: tundra and snow (1, 6, tombstoned
        // labels), boreal forest, cold tundra and land ice are north; desert; grassland steppe; forest central Europe; exposed rock
        // highlands; rainforest mediterranean; marsh normandy. Ocean (0), lake (8) and any other id take the temperature band.
        public static readonly IList<KeyValuePair<int, byte>> BiomeGrades = Array.AsReadOnly(new[] {
            new KeyValuePair<int, byte>(1, GradeNorth), new KeyValuePair<int, byte>(2, GradeDesert), new KeyValuePair<int, byte>(3, GradeSteppe),
            new KeyValuePair<int, byte>(4, GradeCentralEurope), new KeyValuePair<int, byte>(5, GradeHighlands), new KeyValuePair<int, byte>(6, GradeNorth),
            new KeyValuePair<int, byte>(7, GradeMediterranean), new KeyValuePair<int, byte>(13, GradeNormandy), new KeyValuePair<int, byte>(15, GradeNorth),
            new KeyValuePair<int, byte>(16, GradeNorth), new KeyValuePair<int, byte>(17, GradeNorth) });

        public sealed class Arrow { public double Column, Row, East, North; }

        public sealed class Source
        {
            public int Side;
            public double[] Rainfall, IceCap, SnowMax, Biome, Temperature;   // Side x Side, row-major (row 0 the north pole line)
            public double[] WaterType, WaterSurface;   // layers water_type (0 land, 1 ocean, 2 lake) and water_surface (metres); null when absent
            public List<Arrow> Arrows = new List<Arrow>();
            public string Sha256;
            public int Months;
            public double RainfallMax;
        }

        public sealed class Result
        {
            public float[] Wind;            // WindDimension^2 (x, y) pairs
            public byte[] Precipitation;    // WeatherDimension^2 (snow, rain) pairs
            public byte[] ColorGrade;       // ColorGradeDimension^2 grade bytes
            public string WindSha256, PrecipitationSha256, ColorGradeSha256, Identity, PolicyText;
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
            using (var reader = new StreamReader(worldJson, new UTF8Encoding(false), true, 1 << 16))
            {
                var s = new Scanner(reader);
                s.Object(top =>
                {
                    if (top == "layers") s.Object(k => { if (k == "rainfall" || k == "ice_cap" || k == "biome" || k == "temperature" || k == "water_type" || k == "water_surface") layers[k] = s.Grid(); else s.Skip(); });
                    else if (top == "seasonal_environment") s.Object(k => { if (k == "months") s.Array(() => s.Object(f => { if (f == "snow") monthlySnow.Add(s.Grid()); else s.Skip(); })); else s.Skip(); });
                    else if (top == "climate") s.Object(k => { if (k == "wind_arrows") s.Array(() => source.Arrows.Add(s.ArrowValue())); else s.Skip(); });
                    else s.Skip();
                });
                s.Whitespace();
                if (s.Peek() != -1) throw new InvalidDataException("world.json has content after its root object.");
            }
            foreach (var name in new[] { "rainfall", "ice_cap", "biome", "temperature" })
                if (!layers.ContainsKey(name)) throw new InvalidDataException("world.json lacks the " + name + " layer.");
            if (monthlySnow.Count != 12) throw new InvalidDataException("world.json has " + monthlySnow.Count + " seasonal snow months, not 12.");
            if (source.Arrows.Count == 0) throw new InvalidDataException("world.json has no climate wind arrows.");
            int side = layers["rainfall"].Length;
            source.Side = side; source.Months = monthlySnow.Count;
            source.Rainfall = Flatten(layers["rainfall"], side, "rainfall"); source.IceCap = Flatten(layers["ice_cap"], side, "ice_cap");
            source.Biome = Flatten(layers["biome"], side, "biome"); source.Temperature = Flatten(layers["temperature"], side, "temperature");
            if (layers.ContainsKey("water_type") && layers.ContainsKey("water_surface"))
            { source.WaterType = Flatten(layers["water_type"], side, "water_type"); source.WaterSurface = Flatten(layers["water_surface"], side, "water_surface"); }
            foreach (double b in source.Biome) if (b != Math.Floor(b) || b < 0) throw new InvalidDataException("world.json's biome layer holds a value that is not a biome id.");
            source.SnowMax = new double[side * side];
            foreach (var month in monthlySnow) { var m = Flatten(month, side, "snow"); for (int i = 0; i < m.Length; i++) source.SnowMax[i] = Math.Max(source.SnowMax[i], m[i]); }
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
        public static string PolicyText(Source source, PeriodicWorldCoordinates c)
        {
            Func<double, string> r = v => v.ToString("R", CultureInfo.InvariantCulture);
            return "schema=mnbl.climate-flow;version=" + Version + ";source=" + source.Sha256 + ";coordinates=" + c.Identity + ";wind=" + WindDimension +
                " (x,y) float pairs, arrows bilinear on their lattice (columns periodic, rows clamped), smoothstep pole taper over " + r(PoleTaperRows) + " source rows, scale " + r(WindScale) +
                ", map vector (east,-north);weather=" + WeatherDimension + " (snow,rain) bytes, " + NodeSubsamples + "x" + NodeSubsamples +
                " sub-samples per node, rain=rainfall/max, snow=max(monthly snow max, ice cap), byte=round(255v);colorGrade=" + ColorGradeDimension +
                " grade bytes, nearest source cell's biome " + String.Join(",", BiomeGrades.Select(g => g.Key + ">" + g.Value)) + ", water and other ids by temperature (<" + r(ColdBelowC) +
                " " + GradeNorth + ", <" + r(WarmFromC) + " " + GradeNormandy + ", else " + GradeMediterranean + ");samples=canonical location, beyond the poles the pole value";
        }

        // The grade of a cell: its biome's, else (water, unlisted ids) its temperature band.
        public static byte GradeFor(int biome, double temperatureC, out bool byTemperature)
        {
            foreach (var g in BiomeGrades) if (g.Key == biome) { byTemperature = false; return g.Value; }
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

        public static Result Synthesize(Source source, PeriodicWorldCoordinates c)
        {
            if (source == null || c == null) throw new ArgumentNullException();
            if (c.SourceColumns <= 0 || c.SourceColumns + 1 != source.Side)
                throw new InvalidDataException("The layout's source grid (" + (c.SourceColumns + 1) + ") is not the source climate's (" + source.Side + ").");
            // Strip samples copy canonical samples exactly only when the width and the strip hold whole numbers of samples of every grid.
            foreach (int dimension in new[] { WindDimension, WeatherDimension, ColorGradeDimension })
                foreach (double length in new[] { c.Width, c.OriginX })
                {
                    double samples = length * dimension / c.Extent;
                    if (Math.Abs(samples - Math.Round(samples)) > 1e-9) throw new InvalidDataException("A " + dimension + " sample grid puts " + samples + " samples in a length of " + length + "; a whole number is required.");
                }
            var lattice = Lattice(source);
            double windStep = c.Extent / WindDimension, nodeStep = c.Extent / WeatherDimension;
            var wind = new float[WindDimension * WindDimension * 2];
            for (int row = 0; row < WindDimension; row++)
                for (int col = 0; col < WindDimension; col++)
                {
                    double gc, gr; SourceGrid(c, (col + .5) * windStep, (row + .5) * windStep, out gc, out gr);
                    double east, north; lattice.At(gc, gr, out east, out north);
                    double taper = SmoothStep(Math.Min(gr, (source.Side - 1) - gr) / PoleTaperRows);
                    int i = 2 * (row * WindDimension + col);
                    wind[i] = (float)(WindScale * taper * east); wind[i + 1] = (float)(-WindScale * taper * north);
                }
            var precipitation = new byte[WeatherDimension * WeatherDimension * 2];
            for (int row = 0; row < WeatherDimension; row++)
                for (int col = 0; col < WeatherDimension; col++)
                {
                    double snow = 0, rain = 0;
                    for (int sy = 0; sy < NodeSubsamples; sy++)
                        for (int sx = 0; sx < NodeSubsamples; sx++)
                        {
                            double gc, gr; SourceGrid(c, (col + (sx + .5) / NodeSubsamples) * nodeStep, (row + (sy + .5) / NodeSubsamples) * nodeStep, out gc, out gr);
                            snow += Math.Max(Bilinear(source.SnowMax, source.Side, gc, gr), Bilinear(source.IceCap, source.Side, gc, gr));
                            rain += Bilinear(source.Rainfall, source.Side, gc, gr) / source.RainfallMax;
                        }
                    int n = NodeSubsamples * NodeSubsamples, i = 2 * (row * WeatherDimension + col);
                    precipitation[i] = ToByte(snow / n); precipitation[i + 1] = ToByte(rain / n);
                }
            double gradeStep = c.Extent / ColorGradeDimension;
            var grade = new byte[ColorGradeDimension * ColorGradeDimension];
            int byTemperature = 0;
            for (int row = 0; row < ColorGradeDimension; row++)
                for (int col = 0; col < ColorGradeDimension; col++)
                {
                    double gc, gr; SourceGrid(c, (col + .5) * gradeStep, (row + .5) * gradeStep, out gc, out gr);
                    bool band; grade[row * ColorGradeDimension + col] = GradeFor((int)source.Biome[Nearest(source.Side, gc, gr)], Bilinear(source.Temperature, source.Side, gc, gr), out band);
                    if (band) byTemperature++;
                }
            var result = new Result { Wind = wind, Precipitation = precipitation, ColorGrade = grade, PolicyText = PolicyText(source, c) };
            result.WindSha256 = Sha(Bytes(wind)); result.PrecipitationSha256 = Sha(precipitation); result.ColorGradeSha256 = Sha(grade);
            result.Identity = Sha(new UTF8Encoding(false).GetBytes(IdentityText(result.PolicyText, result.WindSha256, result.PrecipitationSha256, result.ColorGradeSha256)));
            result.Record = Statistics(result, c, source);
            result.Record["colorGradeByTemperature"] = byTemperature;
            return result;
        }

        // The text the identity hashes (the placement's v2 block is checked against it on load).
        public static string IdentityText(string policy, string windSha256, string precipitationSha256, string colorGradeSha256)
        {
            return policy + ";windSha256=" + windSha256 + ";precipitationSha256=" + precipitationSha256 + ";colorGradeSha256=" + colorGradeSha256;
        }

        // The colour-grade manager a generated scene carries (the installed script with the official Main_map variables); the world's grid
        // replaces the one it reads (GeneratedColorGrade).
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

        private sealed class ArrowLattice
        {
            public double[] Columns, Rows; public double[,] East, North; public double Period;
            public void At(double column, double row, out double east, out double north)
            {
                int nc = Columns.Length, nr = Rows.Length;
                double step = Period / nc, u = column / step;
                int i0 = (int)Math.Floor(u); double fu = u - i0; i0 = ((i0 % nc) + nc) % nc; int i1 = (i0 + 1) % nc;
                int j0 = 0; double fv = 0;
                if (row <= Rows[0]) { j0 = 0; fv = 0; }
                else if (row >= Rows[nr - 1]) { j0 = nr - 1; fv = 0; }
                else { while (j0 + 1 < nr && Rows[j0 + 1] <= row) j0++; fv = (row - Rows[j0]) / (Rows[j0 + 1] - Rows[j0]); }
                int j1 = Math.Min(nr - 1, j0 + 1);
                east = (East[j0, i0] * (1 - fu) + East[j0, i1] * fu) * (1 - fv) + (East[j1, i0] * (1 - fu) + East[j1, i1] * fu) * fv;
                north = (North[j0, i0] * (1 - fu) + North[j0, i1] * fu) * (1 - fv) + (North[j1, i0] * (1 - fu) + North[j1, i1] * fu) * fv;
            }
        }

        // The arrows as a regular lattice: equally spaced columns over the period, rows as given (each row complete).
        private static ArrowLattice Lattice(Source source)
        {
            var columns = new SortedSet<double>(); var rows = new SortedSet<double>();
            foreach (var a in source.Arrows) { columns.Add(a.Column); rows.Add(a.Row); }
            var cl = new List<double>(columns); var rl = new List<double>(rows);
            int n = source.Side - 1;
            double step = (double)n / cl.Count;
            for (int i = 0; i < cl.Count; i++) if (Math.Abs(cl[i] - i * step) > 1e-9) throw new InvalidDataException("The wind arrows' columns are not equally spaced over the period.");
            if (cl.Count * rl.Count != source.Arrows.Count) throw new InvalidDataException("The wind arrows do not form a complete lattice.");
            var lattice = new ArrowLattice { Columns = cl.ToArray(), Rows = rl.ToArray(), East = new double[rl.Count, cl.Count], North = new double[rl.Count, cl.Count], Period = n };
            foreach (var a in source.Arrows) { int j = rl.IndexOf(a.Row), i = cl.IndexOf(a.Column); lattice.East[j, i] = a.East; lattice.North[j, i] = a.North; }
            return lattice;
        }

        private static SortedDictionary<string, object> Statistics(Result r, PeriodicWorldCoordinates c, Source source)
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
            int snowy = 0, rainy = 0;
            for (int i = 0; i < WeatherDimension * WeatherDimension; i++) { if (r.Precipitation[2 * i] > 0) snowy++; if (r.Precipitation[2 * i + 1] > 0) rainy++; }
            return new SortedDictionary<string, object>(StringComparer.Ordinal) {
                ["version"] = Version, ["identity"] = r.Identity, ["sourceSha256"] = source.Sha256, ["coordinatePolicy"] = c.Identity,
                ["windSha256"] = r.WindSha256, ["precipitationSha256"] = r.PrecipitationSha256, ["colorGradeSha256"] = r.ColorGradeSha256, ["arrows"] = source.Arrows.Count, ["months"] = source.Months,
                ["colorGrades"] = r.ColorGrade.GroupBy(g => g).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => (object)g.Count()),
                ["windMagnitude"] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["min"] = Math.Round(min, 6), ["mean"] = Math.Round(sum / samples, 6), ["max"] = Math.Round(max, 6) },
                ["windMaxNeighbourStep"] = Math.Round(maxStep, 6), ["snowNodes"] = snowy, ["rainNodes"] = rainy, ["rainfallMax"] = source.RainfallMax };
        }

        public static byte[] Bytes(float[] values) { var bytes = new byte[values.Length * 4]; Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length); return bytes; }
        public static string Sha(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }

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

            public Arrow ArrowValue()
            {
                double x = Double.NaN, z = Double.NaN; double[] position3 = null, wind3 = null;
                Object(f =>
                {
                    if (f == "x") x = Number(); else if (f == "z") z = Number();
                    else if (f == "direction") { var v = new List<double>(); Array(() => v.Add(Number())); position3 = v.ToArray(); }
                    else if (f == "wind") { var v = new List<double>(); Array(() => v.Add(Number())); wind3 = v.ToArray(); }
                    else Skip();
                });
                if (Double.IsNaN(x) || Double.IsNaN(z) || position3 == null || wind3 == null || position3.Length != 3 || wind3.Length != 3) throw new InvalidDataException("world.json: a wind arrow lacks x, z, direction or wind.");
                // The source frame is y-up: a position (cos lat cos lon, sin lat, cos lat sin lon). Local east = (-sin lon, 0, cos lon),
                // north = (-sin lat cos lon, cos lat, -sin lat sin lon).
                double lat = Math.Asin(Math.Max(-1, Math.Min(1, position3[1]))), lon = Math.Atan2(position3[2], position3[0]);
                double east = -Math.Sin(lon) * wind3[0] + Math.Cos(lon) * wind3[2];
                double north = -Math.Sin(lat) * Math.Cos(lon) * wind3[0] + Math.Cos(lat) * wind3[1] - Math.Sin(lat) * Math.Sin(lon) * wind3[2];
                return new Arrow { Column = x, Row = z, East = east, North = north };
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
                    if (ch == '\\') { int e = Next(); if (e == 'u') { for (int i = 0; i < 4; i++) Next(); text.Append('?'); } else text.Append((char)e); continue; }
                    text.Append((char)ch);
                }
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
