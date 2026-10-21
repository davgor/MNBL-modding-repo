using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;
using MNBL.ProceduralWorld.Surface;

// MMR-40: rebuilds a fixture slot's settlement entities with SettlementEntityFactory (the code production scenes use), so an in-game fixture
// campaign exercises the factory's hierarchy. The scenebin builder's settlement, border and helper entities are removed (its water plane
// stays) and the factory's are spliced in, resolved by SettlementPositionResolver over the fixture's own terrain (FixtureGround: water at or
// below the sea datum, slopes from the quantized height grid), with the campaign map's scene levels declared. Like a world's scene it bakes
// no siege prefab cache (GeneratedMapScene.Load adds it). The placement's gate positions and scene hash are updated to match.
//   FixtureSettlementEntities PLACEMENT MAIN_MAP PLACEMENT_POLICY NAVAL_POLICY REPORT [--harbour VILLAGE_ID] ...
// --harbour designates a fixture village as a harbour village (the fixtures have none): its drop-off is placed in its town's port cell.
internal static class FixtureSettlementEntities
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };

    private static int Main(string[] args)
    {
        if (args.Length < 5) { Console.Error.WriteLine("usage: FixtureSettlementEntities PLACEMENT MAIN_MAP PLACEMENT_POLICY NAVAL_POLICY REPORT [--harbour VILLAGE_ID]..."); return 2; }
        string placementPath = args[0], reportPath = args[4];
        var harbours = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 5; i + 1 < args.Length; i += 2) if (args[i] == "--harbour") harbours.Add(args[i + 1]);
        var placement = (Dictionary<string, object>)Json.DeserializeObject(File.ReadAllText(placementPath).TrimStart('﻿'));
        string worldId = (string)placement["worldId"], sceneName = (string)placement["sceneName"];
        double extent = Convert.ToDouble(placement["extent"]), seaLevel = Convert.ToDouble(placement["seaLevel"]);
        string folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(placementPath)), sceneName);
        var ground = FixtureGround.Read(Path.Combine(folder, "quantized-heightfield.bin"), extent, seaLevel);
        var policy = SettlementPlacementPolicy.FromBytes(File.ReadAllBytes(args[2]), File.ReadAllBytes(args[3]));
        var contract = SettlementEntityContract.FromInstalled(File.ReadAllBytes(args[1]));
        policy.RequireSource(contract.SourceSha256);

        var records = ((object[])placement["settlements"]).Cast<Dictionary<string, object>>().ToList();
        var sites = new List<SettlementSite>();
        foreach (var s in records)
        {
            string kind = (string)s["kind"];
            var site = new SettlementSite { Id = (string)s["id"], Kind = kind == "town" ? SiteKind.Town : kind == "castle" ? SiteKind.Castle : kind == "village" ? SiteKind.Village : SiteKind.Hideout,
                Source = "fixture placement " + kind, Centre = Xy(s["position"]), BoundTo = s.ContainsKey("boundTo") ? s["boundTo"] as string : null };
            if (site.Kind == SiteKind.Town && s.ContainsKey("portPosition") && s["portPosition"] != null) { site.HasPort = true; site.Port = Xy(s["portPosition"]); site.PortBasin = 0; }
            sites.Add(site);
        }
        foreach (var id in harbours)
        {
            var village = sites.FirstOrDefault(s => s.Id == id && s.Kind == SiteKind.Village);
            var town = village == null ? null : sites.FirstOrDefault(s => s.Id == village.BoundTo && s.HasPort);
            if (town == null) { Console.Error.WriteLine("--harbour " + id + ": not a village bound to a port town"); return 2; }
            village.Harbour = true; village.HarbourBasin = 0; village.HarbourCell = new[] { (int)Math.Floor(town.Port[1] / ground.Spacing), (int)Math.Floor(town.Port[0] / ground.Spacing) };
        }
        var layout = SettlementPositionResolver.Resolve(sites, null, ground, policy);
        var report = layout.Report(ground);
        if (!layout.Activatable) { File.WriteAllText(reportPath, Json.Serialize(report)); Console.Error.WriteLine("refused: " + String.Join("; ", layout.Problems.Where(p => p.Refusal))); return 1; }
        string entities = SettlementEntityFactory.ToXml(SettlementEntityFactory.Build(layout, ground, contract, policy, null, worldId, extent, ground.MaxHeight + 100));

        // Keep only the scenebin water plane; splice the factory's entities in.
        string scenePath = Path.Combine(folder, "scene.xscene");
        var doc = new XmlDocument { PreserveWhitespace = false }; doc.Load(scenePath);
        var container = (XmlElement)doc.SelectSingleNode("/scene/entities");
        foreach (var e in container.SelectNodes("game_entity").Cast<XmlElement>().ToList()) if (e.GetAttribute("name") != worldId + "_water") container.RemoveChild(e);
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "\t", NewLineChars = "\n", OmitXmlDeclaration = false, Encoding = new UTF8Encoding(false) };
        string text;
        using (var sw = new Utf8StringWriter()) { using (var w = XmlWriter.Create(sw, settings)) doc.Save(w); text = sw.ToString(); }
        text = SettlementEntityFactory.DeclareSceneLevels(SettlementEntityFactory.Splice(text, entities));
        var problems = SettlementEntityFactory.Validate(text, layout, ground, policy, worldId);
        if (problems.Count > 0) { Console.Error.WriteLine("contract: " + String.Join("; ", problems.Take(10))); return 1; }
        File.WriteAllText(scenePath, text, new UTF8Encoding(false));

        // The placement follows: gates from the layout (physical, with height), the drop-off as a designated harbour village's port, the scene hash.
        foreach (var s in records)
        {
            var r = layout.Find((string)s["id"]);
            if (r.Gate != null) s["gatePosition"] = new object[] { r.Gate[0], r.Gate[1], ground.Elevation(r.Gate[0], r.Gate[1]) };
            if (r.Dropoff != null) { s["portPosition"] = new object[] { r.Dropoff[0], r.Dropoff[1], 0.0 }; s["harbour"] = true; }
        }
        ((Dictionary<string, object>)placement["sceneFiles"])["scene.xscene"] = Sha(File.ReadAllBytes(scenePath));
        placement["settlementEntities"] = new Dictionary<string, object> { ["builder"] = "SettlementEntityFactory " + SettlementEntityFactory.Version, ["layoutSha256"] = layout.Sha256, ["policySha256"] = policy.Sha256,
            ["contractSha256"] = contract.Sha256, ["harbours"] = harbours.ToList() };
        File.WriteAllText(placementPath, Json.Serialize(placement), new UTF8Encoding(false));
        report["sceneXmlSha256"] = Sha(File.ReadAllBytes(scenePath)); report["entitiesSha256"] = Sha(new UTF8Encoding(false).GetBytes(entities));
        File.WriteAllText(reportPath, Json.Serialize(report), new UTF8Encoding(false));
        Console.WriteLine("placed " + layout.Settlements.Count + " settlements (" + String.Join(", ", layout.Settlements.GroupBy(r => r.Site.Kind).Select(g => g.Count() + " " + g.Key)) + "); warnings " + layout.Problems.Count(p => !p.Refusal) + "; scene " + report["sceneXmlSha256"]);
        return 0;
    }

    private static double[] Xy(object value) { var a = (object[])value; return new[] { Convert.ToDouble(a[0]), Convert.ToDouble(a[1]) }; }
    private static string Sha(byte[] b) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(b)).Replace("-", "").ToLowerInvariant(); }
    private sealed class Utf8StringWriter : StringWriter { public override Encoding Encoding { get { return new UTF8Encoding(false); } } }

    // A fixture's terrain from its quantized height grid (MNBLHF01: side, then side x side binary64 heights, row-major, +Y rows): water at or below
    // the sea datum (one basin), everything else one land component; logical positions are physical (fixtures are not periodic).
    private sealed class FixtureGround : IPlacementGround
    {
        private double[] h; private int side; private double extent, sea, node;
        public double MaxHeight { get { return h.Max(); } }
        public static FixtureGround Read(string path, double extent, double sea)
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < 12 || Encoding.ASCII.GetString(data, 0, 8) != "MNBLHF01") throw new InvalidDataException("Not an MNBLHF01 height field: " + path);
            int side = BitConverter.ToInt32(data, 8);
            if (data.Length != 12 + side * side * 8) throw new InvalidDataException("Height field length differs from its side.");
            var g = new FixtureGround { side = side, extent = extent, sea = sea, node = extent / (side - 1), h = new double[side * side] };
            for (int i = 0; i < g.h.Length; i++) g.h[i] = BitConverter.ToDouble(data, 12 + i * 8);
            return g;
        }
        public double Width { get { return extent; } }
        public double Height { get { return extent; } }
        public double Spacing { get { return node; } }
        private double At(int c, int r) { c = Math.Max(0, Math.Min(side - 1, c)); r = Math.Max(0, Math.Min(side - 1, r)); return h[r * side + c]; }
        public double Elevation(double x, double y)
        {
            double fx = Math.Max(0, Math.Min(side - 1, x / node)), fy = Math.Max(0, Math.Min(side - 1, y / node));
            int c = Math.Min(side - 2, (int)fx), r = Math.Min(side - 2, (int)fy); double u = fx - c, v = fy - r;
            return (At(c, r) * (1 - u) + At(c + 1, r) * u) * (1 - v) + (At(c, r + 1) * (1 - u) + At(c + 1, r + 1) * u) * v;
        }
        private bool Inside(double x, double y) { return x >= 0 && y >= 0 && x <= extent && y <= extent; }
        public byte Class(double x, double y) { return !Inside(x, y) ? TravelCells.Mountain : Elevation(x, y) <= sea ? TravelCells.Sea : TravelCells.Land; }
        public int Land(double x, double y) { return Class(x, y) == TravelCells.Land ? 0 : -1; }
        public int Basin(double x, double y) { return Class(x, y) == TravelCells.Sea ? 0 : -1; }
        public double Slope(double x, double y)
        {
            double d = node / 2, gx = (Elevation(x + d, y) - Elevation(x - d, y)) / (2 * d), gy = (Elevation(x, y + d) - Elevation(x, y - d)) / (2 * d);
            return Math.Atan(Math.Sqrt(gx * gx + gy * gy)) * 180 / Math.PI;
        }
        public double[] Physical(double x, double y) { return new[] { x, y }; }
    }
}
