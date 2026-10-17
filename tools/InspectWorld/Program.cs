using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Persistence;

// MMR-47: tools/Inspect-World.ps1's program. Inspects a world store (or one bundle) without the game and prints a summary; the full report
// is written as JSON. Reads only.
//   InspectWorld --store DIR [--saves DIR] [--bundle DIR] --game VERSION --naval VERSION --capabilities R01,R02,... [--lock SHA256] [--out FILE]
internal static class InspectWorld
{
    private static int Main(string[] args)
    {
        var o = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i + 1 < args.Length; i += 2) o[args[i]] = args[i + 1];
        if (!o.ContainsKey("--game") || !o.ContainsKey("--naval") || (!o.ContainsKey("--store") && !o.ContainsKey("--bundle")))
        {
            Console.Error.WriteLine("usage: InspectWorld --store DIR [--saves DIR] [--bundle DIR] --game VERSION --naval VERSION --capabilities R01,... [--lock SHA256] [--out FILE]");
            return 2;
        }
        var host = new WorldDataBundle.HostProfile(o["--game"], o["--naval"], (o.ContainsKey("--capabilities") ? o["--capabilities"] : "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
        var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
        var report = new Dictionary<string, object>();
        if (o.ContainsKey("--bundle"))
        {
            var bundle = WorldInspector.Bundle(o["--bundle"], host, false);
            report["bundle"] = bundle;
            Console.WriteLine("bundle " + bundle["directory"] + ": " + bundle["status"]);
            foreach (var p in ((IEnumerable)bundle["problems"]).Cast<object>().Take(10)) Console.WriteLine("  - " + p);
        }
        if (o.ContainsKey("--store"))
        {
            string saves = o.ContainsKey("--saves") ? o["--saves"] : null;
            var store = WorldInspector.Store(o["--store"], saves, host, o.ContainsKey("--lock") ? o["--lock"] : null, null);
            report["store"] = store;
            Console.WriteLine("store " + o["--store"]);
            foreach (Dictionary<string, object> b in (IEnumerable)store["bundles"])
                Console.WriteLine("  bundle " + Short(b["directory"]) + "  " + b["status"] + "  saves " + ((IList)b["referencedBySaves"]).Count + "  activations " + ((IList)b["activations"]).Count
                    + (b.ContainsKey("gameVersion") ? "  built for " + b["gameVersion"] + " / " + b["navalVersion"] : "") + Problems(b));
            foreach (Dictionary<string, object> a in (IEnumerable)store["activations"])
                Console.WriteLine("  activation " + Short(a["instance"]) + "  " + a["state"] + (a.ContainsKey("worldContentId") ? "  world " + Short(a["worldContentId"]) + " (" + a["bundle"] + ")" : "") + (Equals(a.ContainsKey("receipt") ? a["receipt"] : null, true) ? "  receipt" : ""));
            foreach (Dictionary<string, object> s in (IEnumerable)store["saves"])
                Console.WriteLine("  save " + s["name"] + (s.ContainsKey("error") ? "  unreadable: " + s["error"] : "  instance " + Short(s["instance"]) + "  world " + Short(s["contentId"]) + " (" + s["bundle"] + ")  package " + (Equals(s["packagePresent"], true) ? "present" : "missing") + "  " + (Equals(s["loadable"], true) ? "loadable" : "NOT loadable")));
            foreach (Dictionary<string, object> j in (IEnumerable)store["jobs"])
                Console.WriteLine("  job " + Short(j["id"]) + "  " + j["phase"] + "  " + (Equals(j["finished"], true) ? "finished (cleanup removes it)" : Equals(j["resumable"], true) ? "unfinished, resumable" : "unfinished: " + j["reason"]) + (j["seed"] != null ? "  seed " + j["seed"] : ""));
            foreach (var p in (IEnumerable)store["staging"]) Console.WriteLine("  staging " + p + " (cleanup removes it)");
            Console.WriteLine("summary " + json.Serialize(store["summary"]));
        }
        if (o.ContainsKey("--out")) File.WriteAllText(o["--out"], json.Serialize(report), new UTF8Encoding(false));
        return 0;
    }

    private static string Short(object id) { var s = id as string; return s == null ? "-" : s.Length > 16 ? s.Substring(0, 12) + "..." : s; }
    private static string Problems(Dictionary<string, object> b)
    {
        var p = ((IEnumerable)b["problems"]).Cast<object>().Take(2).ToList();
        return p.Count == 0 ? "" : "  (" + String.Join("; ", p) + ")";
    }
}
