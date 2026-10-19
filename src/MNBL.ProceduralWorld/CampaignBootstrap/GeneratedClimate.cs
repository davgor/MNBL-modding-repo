using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    // MMR-59: the generated wind and snow/rain fields a fixture declares in placement['climate'] (written by
    // tools/scenebin/build_climate_world.py). The layouts are SandBox.MapScene's: wind 512x512 (x, y) float pairs,
    // snow/rain 32x32 (snow, rain) byte pairs; index row*dim+col with col=floor(x/extent*dim), row=floor(y/extent*dim),
    // origin 0, clamped (MapScene.GetTextureDataIndexForPosition). Files are hash checked against the placement.
    // MMR-39: schema v2 is the synthesized climate of a generated world (GeneratedClimateSystem, written by tools/Synthesize-Climate.ps1):
    // the same files plus the colour-grade grid (512 x 512 grade bytes, MapColorGradeManager's layout, bound by GeneratedColorGrade), and the
    // synthesis identity, which is checked against the policy text and the files' hashes. FromSynthesis binds a synthesis in memory (a
    // production world's bundle).
    public sealed class GeneratedClimate
    {
        public const int WindDimension = 512, WeatherDimension = 32, ColorGradeDimension = 512;
        public const string SchemaV1 = "mnbl.generated-climate.v1", SchemaV2 = "mnbl.generated-climate.v2";
        public readonly string Pattern, WindSha256, PrecipitationSha256;
        public string Schema { get; private set; }
        public string Identity { get; private set; }   // the synthesis identity (v2 and FromSynthesis); null for a diagnostic pattern
        public string ColorGradeSha256 { get; private set; }   // v2 and FromSynthesis; null for a diagnostic pattern (the scene keeps its own grade)
        private byte[] colorGrade;
        public bool HasColorGrade { get { return colorGrade != null; } }
        public byte[] CopyColorGrade() { return colorGrade == null ? null : (byte[])colorGrade.Clone(); }
        public readonly Vec2 WindVector;
        public readonly float Extent;
        private readonly float[] wind;
        private readonly byte[] precipitation;

        private GeneratedClimate(string pattern, float[] wind, byte[] precipitation, string windSha, string precipitationSha, Vec2 vector, float extent)
        {
            Pattern = pattern; this.wind = wind; this.precipitation = precipitation;
            WindSha256 = windSha; PrecipitationSha256 = precipitationSha; WindVector = vector; Extent = extent;
        }

        // Fresh copies for each map scene, so the scene owns its buffers and nothing here keeps them alive.
        public float[] CopyWind() { return (float[])wind.Clone(); }
        public byte[] CopyPrecipitation() { return (byte[])precipitation.Clone(); }

        public static GeneratedClimate FromPlacement(Dictionary<string, object> placement, string placementPath)
        {
            object value;
            if (!placement.TryGetValue("climate", out value) || value == null) return null;
            var climate = (Dictionary<string, object>)value;
            string schema = Text(climate, "schema");
            if (schema != SchemaV1 && schema != SchemaV2) throw new InvalidDataException("Wrong generated climate schema.");
            var folder = Path.GetDirectoryName(Path.GetFullPath(placementPath));
            var windSpec = (Dictionary<string, object>)climate["wind"];
            var rainSpec = (Dictionary<string, object>)climate["precipitation"];
            if (Convert.ToInt32(windSpec["dimension"], CultureInfo.InvariantCulture) != WindDimension ||
                Convert.ToInt32(rainSpec["dimension"], CultureInfo.InvariantCulture) != WeatherDimension)
                throw new InvalidDataException("Generated climate dimensions differ from the installed MapScene layout.");
            byte[] windBytes = Read(folder, windSpec), rainBytes = Read(folder, rainSpec);
            if (windBytes.Length != WindDimension * WindDimension * 2 * 4 || rainBytes.Length != WeatherDimension * WeatherDimension * 2)
                throw new InvalidDataException("Generated climate file sizes differ from the declared layout.");
            if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Little-endian host required.");
            var field = new float[WindDimension * WindDimension * 2];
            Buffer.BlockCopy(windBytes, 0, field, 0, windBytes.Length);
            var vector = (System.Collections.IList)windSpec["vector"];
            var result = new GeneratedClimate(Text(climate, "pattern"), field, rainBytes, Text(windSpec, "sha256"), Text(rainSpec, "sha256"),
                new Vec2(Convert.ToSingle(vector[0], CultureInfo.InvariantCulture), Convert.ToSingle(vector[1], CultureInfo.InvariantCulture)),
                Convert.ToSingle(placement["extent"], CultureInfo.InvariantCulture)) { Schema = schema };
            if (schema == SchemaV2)
            {
                var gradeSpec = (Dictionary<string, object>)climate["colorGrade"];
                if (Convert.ToInt32(gradeSpec["dimension"], CultureInfo.InvariantCulture) != ColorGradeDimension) throw new InvalidDataException("Generated colour-grade dimension differs from MapColorGradeManager's.");
                byte[] gradeBytes = Read(folder, gradeSpec);
                if (gradeBytes.Length != ColorGradeDimension * ColorGradeDimension) throw new InvalidDataException("Generated colour-grade file size differs from the declared layout.");
                result.colorGrade = gradeBytes; result.ColorGradeSha256 = Text(gradeSpec, "sha256");
                string identity = Sha256(new System.Text.UTF8Encoding(false).GetBytes(MNBL.ProceduralWorld.Surface.GeneratedClimateSystem.IdentityText(Text(climate, "policy"), result.WindSha256, result.PrecipitationSha256, result.ColorGradeSha256)));
                if (identity != Text(climate, "identity")) throw new InvalidDataException("The synthesized climate's identity does not match its policy and files.");
                if (Text(climate, "version") != MNBL.ProceduralWorld.Surface.GeneratedClimateSystem.Version) throw new InvalidDataException("The synthesized climate is version " + Text(climate, "version") + ", not this code's.");
                var coordinates = MNBL.ProceduralWorld.Surface.PeriodicWorldCoordinates.FromPlacement(placement);
                if (coordinates == null || Text(climate, "coordinatePolicy") != coordinates.Identity || Text(climate, "policy").IndexOf(";coordinates=" + coordinates.Identity + ";", StringComparison.Ordinal) < 0)
                    throw new InvalidDataException("The synthesized climate was made for another coordinate layout than this placement's.");
                result.Identity = identity;
            }
            return result;
        }

        // A synthesis bound in memory (a production world's bundle, docs/water-climate.md).
        public static GeneratedClimate FromSynthesis(MNBL.ProceduralWorld.Surface.GeneratedClimateSystem.Result synthesis, float extent)
        {
            if (synthesis == null) throw new ArgumentNullException("synthesis");
            return new GeneratedClimate("source:" + MNBL.ProceduralWorld.Surface.GeneratedClimateSystem.Version + ":" + synthesis.Identity.Substring(0, 16),
                (float[])synthesis.Wind.Clone(), (byte[])synthesis.Precipitation.Clone(), synthesis.WindSha256, synthesis.PrecipitationSha256,
                new Vec2((float)MNBL.ProceduralWorld.Surface.GeneratedClimateSystem.WindScale, 0), extent)
                { Schema = SchemaV2, Identity = synthesis.Identity, colorGrade = (byte[])synthesis.ColorGrade.Clone(), ColorGradeSha256 = synthesis.ColorGradeSha256 };
        }

        private static byte[] Read(string folder, Dictionary<string, object> spec)
        {
            string name = Text(spec, "file");
            if (name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) throw new InvalidDataException("Climate file must sit next to the placement.");
            var bytes = File.ReadAllBytes(Path.Combine(folder, name));
            if (Sha256(bytes) != Text(spec, "sha256")) throw new InvalidDataException("Generated climate file hash mismatch: " + name);
            return bytes;
        }

        public static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        public static string Sha256(float[] values)
        {
            var bytes = new byte[values.Length * 4];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return Sha256(bytes);
        }

        private static string Text(Dictionary<string, object> map, string key) { return Convert.ToString(map[key], CultureInfo.InvariantCulture); }

        private int Index(Vec2 p, int dimension)
        {
            int col = (int)Math.Floor(p.X / Extent * dimension), row = (int)Math.Floor(p.Y / Extent * dimension);
            col = Math.Max(0, Math.Min(dimension - 1, col)); row = Math.Max(0, Math.Min(dimension - 1, row));
            return row * dimension + col;
        }

        // The generated value the engine should return at p.
        public Vec2 WindAt(Vec2 p) { int i = Index(p, WindDimension); return new Vec2(wind[2 * i], wind[2 * i + 1]); }
        public int SnowAt(Vec2 p) { return precipitation[2 * Index(p, WeatherDimension)]; }
        public int RainAt(Vec2 p) { return precipitation[2 * Index(p, WeatherDimension) + 1]; }
        public int ColorGradeAt(Vec2 p) { return colorGrade == null ? -1 : colorGrade[Index(p, ColorGradeDimension)]; }
    }
}
