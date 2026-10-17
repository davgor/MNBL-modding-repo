using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MNBL.ProceduralWorld.CampaignRegistry;
using MNBL.ProceduralWorld.Surface;

namespace MNBL.ProceduralWorld.UI
{
    // MMR-46: the world preview, built from the same compiled world the campaign will play (docs/world-preview.md). Pure managed: the setup
    // screen builds it on the job's worker once the world is ready for review, and the offline tests (tests/UI/WorldPreviewTests.cs) build
    // it from the same inputs. No second recipe and no other resolution of the world: every input is one the activation (MMR-45) uses.
    //   * Geography: the world's own surface (WorldSurface: the encoded heights, the water intent and the biome planes the scene was built
    //     from), sampled over the logical frame (x periodic, the seam at both edges), shaded by its normals.
    //   * Settlements, gates' owners and ports: MMR-40's settlement layout report, the placement the scene's entities were built from and the
    //     campaign's settlements take (physical positions; markers keep them); names as the campaign registers them (campaign.json).
    //   * Faction regions: every land sample tinted by the kingdom of its nearest fortification (periodic distance), with kingdom borders;
    //     kingdom names and colours from FactionFactory (MMR-31), the records the campaign registers.
    // Its identity (content id, scene, layout hash, raster hash) is what the activation's committed campaign is checked against.
    internal sealed class WorldPreview
    {
        public sealed class Marker
        {
            public string Id, Name, Kind, Kingdom;   // Kind: town, castle, village, port
            public double U, V;                     // normalized over the logical frame (0..1)
            public double PhysicalX, PhysicalY;     // the scene position (the layout's)
            public bool Capital, Harbour;
        }
        public sealed class Region { public string Kingdom, Name; public uint Color; public double U, V; public int Towns, Castles, Villages; }

        public const string Schema = "mnbl.world-preview";
        public const int Version = 1;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public byte[] Rgba { get; private set; }
        public byte[] Water { get; private set; }   // each pixel's water intent (0 land, 1 ocean, 2 lake, 3 river), as the raster shows it
        public string WorldContentId { get; private set; }
        public string SceneName { get; private set; }
        public string LayoutSha256 { get; private set; }
        public string RasterSha256 { get; private set; }
        public double LogicalWidth { get; private set; }
        public double LogicalHeight { get; private set; }
        public double OriginX { get; private set; }
        public double OriginY { get; private set; }
        public List<Marker> Markers { get; private set; }
        public List<Region> Regions { get; private set; }
        public SortedDictionary<string, int> Counts { get; private set; }
        public SortedDictionary<string, string> KingdomNames { get; private set; }   // every kingdom's id -> the name the campaign registers

        // Biome colours (terrain-materials.json biome ids). Every one is also told apart by shape: water, land relief and region borders.
        private static readonly Dictionary<int, uint> BiomeColor = new Dictionary<int, uint> {
            [1] = 0x8E9A86, [2] = 0xD8C38A, [3] = 0x8DB25E, [4] = 0x4E7D3A, [5] = 0x8C8478, [6] = 0xF2F2F2, [7] = 0x2F6B33, [8] = 0x5A8FC0,
            [13] = 0x7A8A4E, [15] = 0x46705A, [16] = 0xB5BDB0, [17] = 0xE6EEF5 };

        // villageBounds: each village's bound town or castle as the campaign binds it (VillageBinding, MMR-33's binding); a village missing
        // from it keeps its record's town.
        public static WorldPreview Build(WorldSurface surface, IDictionary<string, object> layout, IDictionary<string, object> campaign, FactionFactory.Result factions,
            string sceneName, IDictionary<string, string> villageBounds, int width = 768, int height = 384)
        {
            if (surface == null || layout == null || campaign == null || factions == null) throw new ArgumentNullException();
            if ((string)layout["worldContentId"] != surface.WorldContentId) throw new InvalidDataException("The settlement layout is another world's.");
            if (!Equals(layout["activatable"], true)) throw new InvalidDataException("The settlement layout is not activatable.");
            var p = new WorldPreview { Width = width, Height = height, WorldContentId = surface.WorldContentId, SceneName = sceneName, LayoutSha256 = (string)layout["layoutSha256"],
                LogicalWidth = surface.LogicalWidth, LogicalHeight = surface.LogicalHeight, OriginX = surface.Strip, OriginY = surface.PoleMargin,
                Markers = new List<Marker>(), Regions = new List<Region>(), Counts = new SortedDictionary<string, int>(StringComparer.Ordinal) };

            // Names and kingdoms as the campaign registers them: a village belongs to its bound settlement's kingdom (the game's MapFaction),
            // the settlement the campaign's binding chose (runs 43992 and 13920: 3 villages of world A rebound into another kingdom).
            var records = new Dictionary<string, IDictionary<string, object>>(StringComparer.Ordinal);
            foreach (var kind in new[] { "towns", "castles", "villages" })
                foreach (IDictionary<string, object> r in (IEnumerable)campaign[kind]) records[(string)r["id"]] = r;
            Func<string, string> kingdomOf = null;
            kingdomOf = id =>
            {
                IDictionary<string, object> r;
                if (!records.TryGetValue(id, out r)) return null;
                string rebound; if (villageBounds != null && villageBounds.TryGetValue(id, out rebound) && rebound != null && rebound != id) return kingdomOf(rebound);
                object b; if (r.TryGetValue("bound", out b) && b is string) return kingdomOf((string)b);
                object k; return r.TryGetValue("kingdom", out k) && k is string ? (string)k : null;
            };
            var kingdoms = factions.Kingdoms.ToDictionary(k => k.Id, StringComparer.Ordinal);
            p.KingdomNames = new SortedDictionary<string, string>(factions.Kingdoms.ToDictionary(k => k.Id, k => k.Name ?? FactionFactory.Clean(k.SourceName), StringComparer.Ordinal), StringComparer.Ordinal);

            foreach (IDictionary<string, object> row in (IEnumerable)layout["settlements"])
            {
                string id = (string)row["id"], kind = (string)row["kind"];
                IDictionary<string, object> r;
                if (!records.TryGetValue(id, out r)) throw new InvalidDataException("The layout places " + id + ", which the campaign records do not have.");
                var c = Point(row, "centre");
                p.Markers.Add(p.MarkerAt(id, FactionFactory.Clean((string)r["name"]), kind, kingdomOf(id), c, r.ContainsKey("capital") && Equals(r["capital"], true), r.ContainsKey("harbour") && Equals(r["harbour"], true)));
                var port = Point(row, "port");
                if (port != null) p.Markers.Add(p.MarkerAt(id, FactionFactory.Clean((string)r["name"]), "port", kingdomOf(id), port, false, false));
            }

            // The raster: geography, then faction tint and borders from the fortifications.
            var forts = p.Markers.Where(m => (m.Kind == "town" || m.Kind == "castle") && m.Kingdom != null && kingdoms.ContainsKey(m.Kingdom)).ToList();
            var kingdomIndex = kingdoms.Keys.OrderBy(k => k, StringComparer.Ordinal).Select((k, i) => new { k, i }).ToDictionary(x => x.k, x => x.i, StringComparer.Ordinal);
            var fortColor = forts.Select(f => KingdomColor(kingdoms[f.Kingdom])).ToArray();
            var fortIndex = forts.Select(f => kingdomIndex[f.Kingdom]).ToArray();
            var owner = new int[width * height];
            p.Water = new byte[width * height];
            var rgb = new uint[width * height];
            for (int j = 0; j < height; j++)
                for (int i = 0; i < width; i++)
                {
                    // Row 0 is the top edge: +y is up on the campaign map (review finding: the first build drew it upside down; the bundle's frame
                    // calls +y south, so "north" below means the map's up).
                    double lx = (i + .5) * p.LogicalWidth / width, ly = (height - j - .5) * p.LogicalHeight / height;
                    var ph = surface.Physical(lx, ly);
                    int intent = surface.IntentAt(ph[0], ph[1]);
                    p.Water[j * width + i] = (byte)intent;
                    var n = surface.Normal(ph[0], ph[1]);
                    double lambert = -.5 * n[0] + .5 * n[1] + .7071 * n[2];   // light from the north-west (-x, +y), above
                    double shade = Clamp(.45 + .75 * lambert, .55, 1.15);
                    uint color;
                    if (intent == 1)
                    {
                        double depth = Clamp((surface.High > surface.Low ? (surface.Height(ph[0], ph[1]) - surface.Low) / (surface.High - surface.Low) : .5), 0, 1);
                        color = Mix(0x14305A, 0x3C78B4, depth);
                    }
                    else if (intent == 2) color = 0x4F8CC4;
                    else if (intent == 3) color = 0x5A9AD2;
                    else
                    {
                        uint b; color = BiomeColor.TryGetValue(surface.BiomeAt(ph[0], ph[1]), out b) ? b : 0x8DB25E;
                        color = Scale(color, shade);
                    }
                    int index = j * width + i, best = -1;
                    if (intent == 0 && forts.Count > 0)
                    {
                        double bestD = double.MaxValue;
                        for (int f = 0; f < forts.Count; f++)
                        {
                            double dx = Math.Abs(forts[f].U - (i + .5) / width) * p.LogicalWidth; if (dx > p.LogicalWidth / 2) dx = p.LogicalWidth - dx;
                            double dy = (forts[f].V - (j + .5) / height) * p.LogicalHeight;   // V and rows share the north-up frame
                            double d = dx * dx + dy * dy;
                            if (d < bestD) { bestD = d; best = f; }
                        }
                        if (best >= 0) color = Mix(color, fortColor[best], .30);
                    }
                    owner[index] = best < 0 ? -1 : fortIndex[best];
                    rgb[index] = color;
                }
            // Kingdom borders: a land sample whose right or lower neighbour belongs to another kingdom (x wraps at the seam).
            for (int j = 0; j < height; j++)
                for (int i = 0; i < width; i++)
                {
                    int index = j * width + i, right = j * width + (i + 1) % width, down = j + 1 < height ? index + width : -1;
                    if (owner[index] < 0) continue;
                    if ((owner[right] >= 0 && owner[right] != owner[index]) || (down >= 0 && owner[down] >= 0 && owner[down] != owner[index])) rgb[index] = Scale(rgb[index], .45);
                }
            p.Rgba = new byte[width * height * 4];
            for (int k = 0; k < rgb.Length; k++) { p.Rgba[4 * k] = (byte)(rgb[k] >> 16); p.Rgba[4 * k + 1] = (byte)(rgb[k] >> 8); p.Rgba[4 * k + 2] = (byte)rgb[k]; p.Rgba[4 * k + 3] = 255; }
            p.RasterSha256 = Sha256(p.Rgba);

            // Regions: each kingdom holding a fortification, labelled at its fortifications' periodic mean with the name the campaign registers.
            foreach (var g in p.Markers.Where(m => m.Kingdom != null && kingdoms.ContainsKey(m.Kingdom) && m.Kind != "port").GroupBy(m => m.Kingdom).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var held = g.Where(m => m.Kind == "town" || m.Kind == "castle").ToList();
                if (held.Count == 0) continue;
                double sx = held.Sum(m => Math.Cos(2 * Math.PI * m.U)), sy = held.Sum(m => Math.Sin(2 * Math.PI * m.U));
                double u = Math.Atan2(sy, sx) / (2 * Math.PI); if (u < 0) u += 1;
                var k = kingdoms[g.Key];
                p.Regions.Add(new Region { Kingdom = g.Key, Name = k.Name ?? FactionFactory.Clean(k.SourceName), Color = KingdomColor(k), U = u, V = held.Average(m => m.V),
                    Towns = g.Count(m => m.Kind == "town"), Castles = g.Count(m => m.Kind == "castle"), Villages = g.Count(m => m.Kind == "village") });
            }
            foreach (var kind in new[] { "town", "castle", "village", "port" }) p.Counts[kind + "s"] = p.Markers.Count(m => m.Kind == kind);
            p.Counts["harbourVillages"] = p.Markers.Count(m => m.Kind == "village" && m.Harbour);
            p.Counts["kingdoms"] = factions.Kingdoms.Count; p.Counts["clans"] = factions.Clans.Count; p.Counts["regions"] = p.Regions.Count;
            p.Counts["heroes"] = ((IEnumerable)campaign["heroes"]).Cast<object>().Count();
            return p;
        }

        private Marker MarkerAt(string id, string name, string kind, string kingdom, double[] physical, bool capital, bool harbour)
        {
            double lx = physical[0] - OriginX, ly = physical[1] - OriginY;
            lx %= LogicalWidth; if (lx < 0) lx += LogicalWidth;
            // V runs down the picture: north (larger y) has the smaller V.
            return new Marker { Id = id, Name = name, Kind = kind, Kingdom = kingdom, PhysicalX = physical[0], PhysicalY = physical[1], U = lx / LogicalWidth, V = 1 - ly / LogicalHeight, Capital = capital, Harbour = harbour };
        }

        // A kingdom's colour as its banner gives it (FactionFactory's Color, "FFRRGGBB" or "RRGGBB"); a fixed grey when it has none.
        private static uint KingdomColor(FactionFactory.KingdomIntent k)
        {
            string c = (k.Color ?? "").Trim();
            if (c.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) c = c.Substring(2);
            uint v; return c.Length >= 6 && UInt32.TryParse(c.Substring(c.Length - 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v) ? v : 0x808080;
        }

        // The preview's identity and contents, without the raster (its hash stands for it).
        public Dictionary<string, object> Record()
        {
            return new Dictionary<string, object> { ["schema"] = Schema, ["version"] = Version, ["worldContentId"] = WorldContentId, ["sceneName"] = SceneName, ["layoutSha256"] = LayoutSha256,
                ["raster"] = new Dictionary<string, object> { ["width"] = Width, ["height"] = Height, ["sha256"] = RasterSha256 }, ["counts"] = Counts,
                ["regions"] = Regions.Select(r => new Dictionary<string, object> { ["kingdom"] = r.Kingdom, ["name"] = r.Name, ["color"] = r.Color.ToString("X6", CultureInfo.InvariantCulture),
                    ["u"] = Math.Round(r.U, 4), ["v"] = Math.Round(r.V, 4), ["towns"] = r.Towns, ["castles"] = r.Castles, ["villages"] = r.Villages }).ToList(),
                ["markers"] = Markers.Select(m => new Dictionary<string, object> { ["id"] = m.Id, ["kind"] = m.Kind, ["name"] = m.Name, ["kingdom"] = m.Kingdom,
                    ["physical"] = new[] { m.PhysicalX, m.PhysicalY }, ["u"] = Math.Round(m.U, 5), ["v"] = Math.Round(m.V, 5) }).ToList() };
        }

        // A label shortened to at most max characters (an ellipsis marks it); the panel shows the full text below the map for the place pointed at
        // or selected, and lists the kingdoms in full.
        public static string Shorten(string text, int max)
        {
            text = text ?? "";
            var info = new StringInfo(text);
            if (info.LengthInTextElements <= max) return text;
            return info.SubstringByTextElements(0, Math.Max(1, max - 1)).TrimEnd() + "…";
        }

        // The raster as a PNG (8-bit RGBA, no interlace): evidence, and the file the in-game texture is made from.
        public byte[] Png()
        {
            using (var output = new MemoryStream())
            {
                output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
                var header = new byte[13];
                WriteBig(header, 0, (uint)Width); WriteBig(header, 4, (uint)Height); header[8] = 8; header[9] = 6;
                Chunk(output, "IHDR", header);
                byte[] raw = new byte[(Width * 4 + 1) * Height];
                for (int j = 0; j < Height; j++) { raw[j * (Width * 4 + 1)] = 0; Buffer.BlockCopy(Rgba, j * Width * 4, raw, j * (Width * 4 + 1) + 1, Width * 4); }
                using (var z = new MemoryStream())
                {
                    z.WriteByte(0x78); z.WriteByte(0x9C);
                    using (var deflate = new DeflateStream(z, CompressionLevel.Optimal, true)) deflate.Write(raw, 0, raw.Length);
                    var adler = new byte[4]; WriteBig(adler, 0, Adler32(raw)); z.Write(adler, 0, 4);
                    Chunk(output, "IDAT", z.ToArray());
                }
                Chunk(output, "IEND", new byte[0]);
                return output.ToArray();
            }
        }

        private static void Chunk(Stream s, string type, byte[] data)
        {
            var length = new byte[4]; WriteBig(length, 0, (uint)data.Length); s.Write(length, 0, 4);
            var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            s.Write(typed, 0, typed.Length);
            var crc = new byte[4]; WriteBig(crc, 0, Crc32(typed)); s.Write(crc, 0, 4);
        }
        private static void WriteBig(byte[] b, int at, uint v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }
        private static uint[] crcTable;
        private static uint Crc32(byte[] data)
        {
            if (crcTable == null)
            {
                var t = new uint[256];
                for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; t[n] = c; }
                crcTable = t;
            }
            uint crc = 0xFFFFFFFF;
            foreach (byte b in data) crc = crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }
        private static uint Adler32(byte[] data) { uint a = 1, b = 0; foreach (byte x in data) { a = (a + x) % 65521; b = (b + a) % 65521; } return (b << 16) | a; }

        private static double[] Point(IDictionary<string, object> row, string key)
        {
            object v; if (!row.TryGetValue(key, out v) || v == null) return null;
            return ((IEnumerable)v).Cast<object>().Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray();
        }
        private static double Clamp(double v, double lo, double hi) { return v < lo ? lo : v > hi ? hi : v; }
        private static uint Mix(uint a, uint b, double t)
        {
            Func<int, uint> ch = shift => (uint)Math.Round(((a >> shift) & 0xFF) * (1 - t) + ((b >> shift) & 0xFF) * t);
            return (ch(16) << 16) | (ch(8) << 8) | ch(0);
        }
        private static uint Scale(uint c, double f)
        {
            Func<int, uint> ch = shift => (uint)Clamp(Math.Round(((c >> shift) & 0xFF) * f), 0, 255);
            return (ch(16) << 16) | (ch(8) << 8) | ch(0);
        }
        public static string Sha256(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    }
}
