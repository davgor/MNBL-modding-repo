using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Generation;

// MMR-283: writes worldgen/policies/campaign-catalog.json (v2), the installed objects a generated world can reference, from the campaign_catalog
// capture the world policies were made from (rosters.json provenance) and the live default inventory of a generated_campaign run:
// CatalogPinTool REPO_DIR. Run by tools/Make-CatalogPin.ps1. The policy roots are every catalog object a world policy names (any string value
// or key of the graph policy's listed policies that is an object's id or typed key; Calradia's lords and kingdoms are never roots).
internal static class CatalogPinTool
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static IDictionary<string, object> Load(string path) { return (IDictionary<string, object>)Json.DeserializeObject(File.ReadAllText(path).TrimStart('﻿')); }
    private static string Sha(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }

    private static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("usage: CatalogPinTool REPO_DIR"); return 2; }
        string repo = args[0];
        var rosters = Load(Path.Combine(repo, "worldgen/policies/rosters.json"));
        var provenance = (IDictionary<string, object>)rosters["provenance"];
        string receiptPath = (string)provenance["catalogReceipt"], defaultsPath = (string)provenance["defaultReferences"];
        var receipt = Load(Path.Combine(repo, receiptPath));
        if ((string)receipt["probe"] != "campaign_catalog" || (string)receipt["status"] != "ok" || !Equals(receipt["final"], true)) throw new InvalidDataException("the catalog receipt is not a completed campaign_catalog run");
        var outputs = (IDictionary<string, object>)receipt["outputs"];
        var captured = new Dictionary<string, XmlDocument>(StringComparer.Ordinal);
        foreach (var d in ((IEnumerable)outputs["documents"]).Cast<IDictionary<string, object>>())
        {
            var bytes = File.ReadAllBytes((string)d["path"]);
            if (Sha(bytes) != (string)d["sha256"]) throw new InvalidDataException("the captured " + d["id"] + " differs from its receipt");
            var doc = new XmlDocument { XmlResolver = null }; doc.LoadXml(new UTF8Encoding(false).GetString(bytes).TrimStart('﻿'));
            captured[(string)d["id"]] = doc;
        }
        var receiptShas = ((IEnumerable)outputs["documents"]).Cast<IDictionary<string, object>>().ToDictionary(d => (string)d["id"], d => (string)d["sha256"], StringComparer.Ordinal);
        foreach (var kv in (IDictionary<string, object>)provenance["catalogDocuments"])
            if (!receiptShas.ContainsKey(kv.Key) || receiptShas[kv.Key] != (string)kv.Value) throw new InvalidDataException("the rosters policy names another capture of " + kv.Key);
        var defaultsBytes = File.ReadAllBytes(Path.Combine(repo, defaultsPath));
        if (Sha(defaultsBytes) != (string)provenance["defaultReferencesSha256"]) throw new InvalidDataException("the default inventory run differs from the rosters policy");
        var run = (IDictionary<string, object>)Json.DeserializeObject(new UTF8Encoding(false).GetString(defaultsBytes).TrimStart('﻿'));
        var registration = (IDictionary<string, object>)((IDictionary<string, object>)((IEnumerable)((IDictionary<string, object>)run["outputs"])["worlds"]).Cast<object>().First())["registration"];
        var defaults = ((IEnumerable)registration["verifiedLiveDefaultReferences"]).Cast<object>().Select(x => (string)x).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (defaults.Count != Convert.ToInt32(provenance["defaultReferenceCount"])) throw new InvalidDataException("the default inventory count differs from the rosters policy");

        // The policy roots: every id or typed key the world policies name.
        var index = new CatalogPin.Index(captured);
        var byId = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var key in index.Keys) { string id = key.Substring(key.IndexOf('.') + 1); List<string> l; if (!byId.TryGetValue(id, out l)) byId[id] = l = new List<string>(); l.Add(key); }
        var graph = Load(Path.Combine(repo, "worldgen/policies/graph-validation.json"));
        var roots = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var policy in ((IEnumerable)graph["policies"]).Cast<object>().Select(x => (string)x))
            foreach (var value in Strings(Json.DeserializeObject(File.ReadAllText(Path.Combine(repo, policy)).TrimStart('﻿'))))
            {
                if (index.Contains(value)) { roots.Add(value); continue; }
                List<string> keys;
                if (byId.TryGetValue(value, out keys)) foreach (var k in keys) if (!k.StartsWith("Hero.", StringComparison.Ordinal) && !k.StartsWith("Kingdom.", StringComparison.Ordinal)) roots.Add(k);
            }
        var problems = new List<CatalogPin.Conflict>();
        var record = new SortedDictionary<string, object>(StringComparer.Ordinal) {
            ["catalogReceipt"] = receiptPath, ["catalogReceiptSha256"] = Sha(File.ReadAllBytes(Path.Combine(repo, receiptPath))), ["defaultReferences"] = defaultsPath,
            ["defaultReferencesSha256"] = provenance["defaultReferencesSha256"], ["moduleSha256"] = receipt["moduleSha256"], ["tool"] = "tools/Make-CatalogPin.ps1" };
        string text = CatalogPin.Make(captured, (string)outputs["gameType"], roots, GeneratedCampaignDataset.InstalledEngineCharacterIds, defaults, record, problems);
        foreach (var p in problems) Console.WriteLine("tolerated (the captured catalog's own closure problem): " + p);
        string outPath = Path.Combine(repo, CatalogPin.PolicyPath);
        File.WriteAllText(outPath, text, new UTF8Encoding(false));
        var pin = CatalogPin.Pin.FromBytes(File.ReadAllBytes(outPath));
        Console.WriteLine(outPath + ": " + pin.Objects.Count + " objects of " + index.Keys.Count() + " (" + String.Join(", ", pin.Objects.Values.GroupBy(o => o.Key.Substring(0, o.Key.IndexOf('.'))).OrderBy(g => g.Key).Select(g => g.Key + " " + g.Count())) +
            "), " + pin.Roots.Count + " policy roots, " + pin.Defaults.Count + " defaults");
        return 0;
    }

    private static IEnumerable<string> Strings(object value)
    {
        var s = value as string;
        if (s != null) { yield return s; yield break; }
        var o = value as IDictionary<string, object>;
        if (o != null) { foreach (var kv in o) { yield return kv.Key; foreach (var x in Strings(kv.Value)) yield return x; } yield break; }
        var list = value as IEnumerable;
        if (list != null) foreach (var item in list) foreach (var x in Strings(item)) yield return x;
    }
}
