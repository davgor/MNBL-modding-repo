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
    public sealed class GeneratedClimate
    {
        public const int WindDimension = 512, WeatherDimension = 32;
        public readonly string Pattern, WindSha256, PrecipitationSha256;
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
            if (Text(climate, "schema") != "mnbl.generated-climate.v1") throw new InvalidDataException("Wrong generated climate schema.");
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
            return new GeneratedClimate(Text(climate, "pattern"), field, rainBytes, Text(windSpec, "sha256"), Text(rainSpec, "sha256"),
                new Vec2(Convert.ToSingle(vector[0], CultureInfo.InvariantCulture), Convert.ToSingle(vector[1], CultureInfo.InvariantCulture)),
                Convert.ToSingle(placement["extent"], CultureInfo.InvariantCulture));
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
    }
}
