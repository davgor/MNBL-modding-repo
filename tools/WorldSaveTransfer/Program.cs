using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Persistence;

// MMR-48: tools/Transfer-WorldSave.ps1's program. Exports saves of generated campaigns with the world packages and data bundles they need,
// verifies an export, or imports one into a store and save folder (WorldSaveTransfer); never regenerates a world.
//   WorldSaveTransfer export --store DIR --saves DIR --save NAME [--save NAME ...] --to DIR --name NAME --game V --naval V --capabilities R01,... [--out FILE]
//   WorldSaveTransfer verify --from DIR --game V --naval V --capabilities R01,... [--out FILE]
//   WorldSaveTransfer import --from DIR --store DIR --saves DIR [--rename OLD=NEW ...] --game V --naval V --capabilities R01,... [--out FILE]
internal static class TransferWorldSave
{
    private static int Main(string[] args)
    {
        if (args.Length == 0) return Usage();
        string verb = args[0];
        var one = new Dictionary<string, string>(StringComparer.Ordinal);
        var saves = new List<string>();
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i + 1 < args.Length; i += 2)
        {
            if (args[i] == "--save") saves.Add(args[i + 1]);
            else if (args[i] == "--rename") { var pair = args[i + 1].Split(new[] { '=' }, 2); if (pair.Length != 2) return Usage(); renames[pair[0]] = pair[1]; }
            else one[args[i]] = args[i + 1];
        }
        if (!one.ContainsKey("--game") || !one.ContainsKey("--naval")) return Usage();
        var host = new WorldDataBundle.HostProfile(one["--game"], one["--naval"], (one.ContainsKey("--capabilities") ? one["--capabilities"] : "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
        Func<string, bool, IList<string>> bundleProblems = (directory, nameMustMatch) => WorldDataBundle.Problems(directory, host, nameMustMatch);
        var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
        object report;
        try
        {
            switch (verb)
            {
                case "export":
                    if (!one.ContainsKey("--store") || !one.ContainsKey("--saves") || !one.ContainsKey("--to") || !one.ContainsKey("--name") || saves.Count == 0) return Usage();
                    report = WorldSaveTransfer.Export(new WorldSaveTransfer.Options(one["--store"], one["--saves"], bundleProblems), saves, one["--to"], one["--name"]);
                    break;
                case "verify":
                    if (!one.ContainsKey("--from")) return Usage();
                    var manifest = WorldSaveTransfer.Verify(one["--from"], bundleProblems);
                    report = new Dictionary<string, object> { ["export"] = manifest.Directory, ["verified"] = true, ["saves"] = manifest.Saves.Select(s => s.Name).ToList(),
                        ["worlds"] = manifest.Packages, ["bundles"] = manifest.Bundles, ["files"] = manifest.Files.Count, ["bytes"] = manifest.Files.Sum(f => f.Length) };
                    break;
                case "import":
                    if (!one.ContainsKey("--from") || !one.ContainsKey("--store") || !one.ContainsKey("--saves")) return Usage();
                    report = WorldSaveTransfer.Import(new WorldSaveTransfer.Options(one["--store"], one["--saves"], bundleProblems), one["--from"], renames);
                    break;
                default: return Usage();
            }
        }
        catch (WorldSaveTransfer.Refused refused)
        {
            Console.Error.WriteLine("refused: " + refused.Message);
            foreach (var p in refused.Problems.Take(20)) Console.Error.WriteLine("  - " + p);
            if (one.ContainsKey("--out")) File.WriteAllText(one["--out"], json.Serialize(new Dictionary<string, object> { ["refused"] = refused.Message, ["problems"] = refused.Problems }), new UTF8Encoding(false));
            return 3;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("failed: " + error.GetType().Name + ": " + error.Message);
            if (one.ContainsKey("--out")) File.WriteAllText(one["--out"], json.Serialize(new Dictionary<string, object> { ["failed"] = error.GetType().Name + ": " + error.Message }), new UTF8Encoding(false));
            return 4;
        }
        Console.WriteLine(json.Serialize(report));
        if (one.ContainsKey("--out")) File.WriteAllText(one["--out"], json.Serialize(report), new UTF8Encoding(false));
        return 0;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: WorldSaveTransfer export|verify|import ... (see tools/Transfer-WorldSave.ps1)");
        return 2;
    }
}
