using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using MNBL.ProceduralWorld.CampaignBootstrap;
using Bundle = MNBL.ProceduralWorld.CampaignBootstrap.WorldDataBundle;

// MMR-29 host verification of a real worldbridge bundle, without the game:
// WorldDataBundleTests BUNDLE_DIR WORK_DIR EXPECTED_WORLD_CONTENT_ID EXPECTED_MANIFEST_SHA256
// WorldDataBundleTests --verify DIR [--candidate]   (one verification as JSON for tools/test_bundle_parity.py; exit 0/2)
// The expected identity comes from worldbridge --inspect-bundle, so C# and C++ must agree on the WorldContentId. One copy of
// the bundle is made under WORK_DIR; each tampering is applied to it, checked, and undone, and the copy must verify again.
internal static class WorldDataBundleTests
{
    private static int checks, failures;
    private static readonly string[] AllCapabilities = { "R01", "R02", "R03", "R04", "R05", "R06", "R07", "R08", "R09", "R10" };
    private static readonly Bundle.HostProfile Host = new Bundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", AllCapabilities);

    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; Console.WriteLine((condition ? "ok    " : "FAIL  ") + label); }
    private static bool Has(IEnumerable<string> problems, string part) { return problems.Any(p => p.Contains(part)); }
    private static string Rejection(Action action)
    {
        try { action(); } catch (Bundle.RejectedException ex) { return ex.Message; }
        return null;
    }
    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(from)) CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
    private static void Mklink(string kind, string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe", "/c mklink " + kind + " \"" + link + "\" \"" + target + "\"") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        using (var p = Process.Start(start)) { p.StandardOutput.ReadToEnd(); p.WaitForExit(); if (p.ExitCode != 0) throw new Exception("mklink " + kind + " failed"); }
    }

    private static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--verify")
        {
            var problems = Bundle.Problems(args[1], Host, !(args.Length > 2 && args[2] == "--candidate"));
            Console.WriteLine(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new Dictionary<string, object> { ["valid"] = problems.Count == 0, ["problems"] = problems }));
            return problems.Count == 0 ? 0 : 2;
        }
        if (args.Length != 4) { Console.Error.WriteLine("usage: WorldDataBundleTests BUNDLE_DIR WORK_DIR WORLD_CONTENT_ID MANIFEST_SHA256"); return 2; }
        string real = Path.GetFullPath(args[0]), work = Path.GetFullPath(args[1]), expectedId = args[2], expectedManifest = args[3];
        try { Run(real, work, expectedId, expectedManifest); }
        catch (Exception ex) { Check(false, "unexpected exception: " + ex); }
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string real, string work, string expectedId, string expectedManifest)
    {
        // The real bundle, as published by worldbridge.
        var bundle = Bundle.Open(real, Host);
        Check(bundle.WorldContentId == expectedId && bundle.ManifestSha256 == expectedManifest && Path.GetFileName(real) == expectedId,
              "the real bundle verifies and its recomputed WorldContentId and manifest hash equal worldbridge --inspect-bundle's");
        Check(bundle.Inventory.Count() == Convert.ToInt32(((Dictionary<string, object>)bundle.Manifest["totals"])["files"], CultureInfo.InvariantCulture), "the inventory matches the manifest totals (" + bundle.Inventory.Count() + " files)");
        Check(Bundle.Problems(real + Path.DirectorySeparatorChar, Host, true).Count == 0, "a bundle named with a trailing separator verifies");
        Check(bundle.ReadVerified("compiled/travel/travel-graph.json").Length == bundle.Inventory.Single(e => e.Path == "compiled/travel/travel-graph.json").Bytes,
              "a bundle file is read with its hash checked at the moment of use");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (var name in new[] { "tr-TR", "fr-FR" })
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Check(Bundle.Problems(real, Host, true).Count == 0 && Bundle.ContentId(bundle.Inventory) == expectedId, "verification is locale-independent (" + name + ")");
            }
        }
        finally { Thread.CurrentThread.CurrentCulture = culture; }

        // Compatibility with the host: versions and capabilities.
        var oldGame = Bundle.Problems(real, new Bundle.HostProfile("v1.4.7.118000", "v1.2.8.119303", AllCapabilities), true);
        Check(Has(oldGame, "built for Bannerlord v1.4.8.119303, installed v1.4.7.118000"), "a different Bannerlord version is refused");
        var oldNaval = Rejection(() => Bundle.Open(real, new Bundle.HostProfile("v1.4.8.119303", "v1.2.7.0", AllCapabilities)));
        Check(oldNaval != null && oldNaval.Contains("built for War Sails v1.2.8.119303, installed v1.2.7.0") && oldNaval.Contains("migration tool"),
              "a different War Sails version is refused with an actionable message");
        var noR08 = Rejection(() => Bundle.Open(real, new Bundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", AllCapabilities.Where(c => c != "R08"))));
        Check(noR08 != null && noR08.Contains("requires runtime capability R08") && noR08.Contains("Update MNBL"), "a host without a required capability is refused");

        // One copy to tamper with.
        string root = Path.Combine(work, "root");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        string copy = Path.Combine(root, expectedId);
        CopyTree(real, copy);
        Check(Bundle.Problems(copy, Host, true).Count == 0, "a byte copy of the bundle verifies");
        Action<string, Action, Action, string> tamper = (name, change, undo, expect) => {
            change();
            var problems = Bundle.Problems(copy, Host, true);
            undo();
            Check(problems.Count > 0 && Has(problems, expect), "refuses " + name + " (" + expect + ")" + (Has(problems, expect) ? "" : ": " + String.Join("; ", problems)));
        };
        string plan = Path.Combine(copy, "source", "world.json"), aside = Path.Combine(work, "aside");
        tamper("a missing file", () => File.Move(plan, aside), () => File.Move(aside, plan), "missing file source/world.json");
        string extra = Path.Combine(copy, "compiled", "extra.bin");
        tamper("an extra file", () => File.WriteAllText(extra, "x"), () => File.Delete(extra), "unlisted file compiled/extra.bin");
        string height = Path.Combine(copy, "compiled", "terrain", "heightfield.bin");
        byte[] original = File.ReadAllBytes(height);
        tamper("an edited file", () => { var b = (byte[])original.Clone(); b[b.Length / 2] ^= 0xff; File.WriteAllBytes(height, b); }, () => File.WriteAllBytes(height, original), "hash differs: compiled/terrain/heightfield.bin");
        tamper("a resized file", () => File.WriteAllBytes(height, original.Concat(new byte[] { 0 }).ToArray()), () => File.WriteAllBytes(height, original), "size differs: compiled/terrain/heightfield.bin");
        string ready = Path.Combine(copy, "DATA_READY"), manifestFile = Path.Combine(copy, "manifest.json");
        byte[] readyBytes = File.ReadAllBytes(ready), manifestBytes = File.ReadAllBytes(manifestFile);
        tamper("an unsealed bundle", () => File.Move(ready, aside), () => File.Move(aside, ready), "never sealed");
        tamper("an edited manifest", () => File.WriteAllBytes(manifestFile, manifestBytes.Concat(new[] { (byte)' ' }).ToArray()), () => File.WriteAllBytes(manifestFile, manifestBytes), "DATA_READY names a different manifest");
        string receipt = Path.Combine(copy, "activation-receipt.json");
        tamper("an activation receipt inside", () => File.WriteAllText(receipt, "{}"), () => File.Delete(receipt), "activation receipts never live in a bundle");
        string policy = Path.Combine(copy, "policies", "playability.json");
        byte[] policyBytes = File.ReadAllBytes(policy);
        tamper("an edited policy", () => File.WriteAllBytes(policy, policyBytes.Select(b => b == (byte)'2' ? (byte)'3' : b).ToArray()), () => File.WriteAllBytes(policy, policyBytes), "hash differs: policies/playability.json");
        // A consistent manifest and DATA_READY do not make unsafe or wrong content acceptable.
        Action<Func<string, string>> reseal = edit => {
            string text = Encoding.UTF8.GetString(manifestBytes), edited = edit(text);
            if (edited == text) throw new Exception("manifest edit did not apply");
            byte[] bytes = new UTF8Encoding(false).GetBytes(edited);
            File.WriteAllBytes(manifestFile, bytes);
            File.WriteAllText(ready, Encoding.UTF8.GetString(readyBytes).Replace(expectedManifest, Bundle.Sha256(bytes)), new UTF8Encoding(false));
        };
        Action unseal = () => { File.WriteAllBytes(manifestFile, manifestBytes); File.WriteAllBytes(ready, readyBytes); };
        tamper("an inventory path escaping the bundle", () => reseal(t => t.Replace("\"path\":\"compiled/campaign/campaign.json\"", "\"path\":\"compiled/../campaign.json\"")), unseal, "unsafe inventory path compiled/../campaign.json");
        tamper("an inventory path longer than 120 bytes", () => reseal(t => t.Replace("\"path\":\"compiled/campaign/campaign.json\"", "\"path\":\"compiled/" + new string('x', 120) + "\"")), unseal, "unsafe inventory path compiled/xxx");
        tamper("a resealed manifest whose engine differs from compiled/compatibility.json", () => reseal(t => t.Replace("\"gameVersion\":\"v1.4.8.119303\"", "\"gameVersion\":\"v9.9.9.0\"")), unseal, "differ from compiled/compatibility.json");
        tamper("a resealed manifest with a repeated key", () => reseal(t => t.Replace("\"migration\":", "\"migration\":\"x\",\"migration\":")), unseal, "Repeated key 'migration'");
        tamper("a resealed manifest whose version is a string", () => reseal(t => t.Replace("\"version\":1,\"worldContentAlgorithm\"", "\"version\":\"1\",\"worldContentAlgorithm\"")), unseal, "manifest is not mnbl.world-bundle version 1");
        tamper("an empty DATA_READY", () => File.WriteAllBytes(ready, new byte[0]), () => File.WriteAllBytes(ready, readyBytes), "InvalidDataException");
        tamper("a manifest that is not an object", () => File.WriteAllText(manifestFile, "[]"), () => File.WriteAllBytes(manifestFile, manifestBytes), "Expected a JSON object");
        tamper("a resealed manifest whose inventory hash was edited with the file", () => {
            var b = (byte[])original.Clone(); b[0] ^= 0xff; File.WriteAllBytes(height, b);
            string oldSha = Bundle.Sha256(original), newSha = Bundle.Sha256(b);
            reseal(t => t.Replace(oldSha, newSha));
        }, () => { File.WriteAllBytes(height, original); unseal(); }, "worldContentId does not match the files");
        tamper("a bundle whose static validation did not pass", () => reseal(t => t.Replace("\"travelStatus\":\"ok\"", "\"travelStatus\":\"rejected\"")), unseal, "the static validation did not pass");
        // Links: a junction and a hard link inside the bundle.
        string junction = Path.Combine(copy, "compiled", "linked");
        tamper("a junction inside", () => Mklink("/J", junction, Path.Combine(copy, "policies")), () => Directory.Delete(junction), "linked directory compiled/linked");
        string hard = Path.Combine(work, "hard.json");
        tamper("a hard-linked file", () => Mklink("/H", hard, policy), () => File.Delete(hard), "hard-linked file policies/playability.json");
        // Another directory name.
        string renamed = Path.Combine(root, new string('0', 64));
        Directory.Move(copy, renamed);
        var renamedProblems = Bundle.Problems(renamed, Host, true);
        bool candidate = Bundle.Problems(renamed, Host, false).Count == 0;
        Directory.Move(renamed, copy);
        Check(Has(renamedProblems, "directory name") && candidate, "a bundle under another name is refused as published, valid as a candidate");
        Check(Bundle.Problems(copy, Host, true).Count == 0, "every tampering was undone and the copy verifies again");

        // Save binding: the identity a save stores resolves only the exact bundle.
        var opened = Bundle.Open(copy, Host);
        var identity = opened.SaveIdentity();
        Check(identity["worldContentId"] == expectedId && identity.Count == 2, "the save identity names the WorldContentId, which covers every file including compiled/compatibility.json");
        Check(Bundle.OpenForSave(root, identity, Host).WorldContentId == expectedId, "a save resolves its bundle under the bundle root");
        var missing = Rejection(() => Bundle.OpenForSave(Path.Combine(work, "empty-root"), identity, Host));
        Check(missing != null && missing.Contains("is missing from") && missing.Contains("never regenerates"), "a save whose bundle is missing is refused with what to restore");
        reseal(t => t.Replace("\"binarySha256\":\"" + ((Dictionary<string, object>)opened.Manifest["compiler"])["binarySha256"] + "\"", "\"binarySha256\":\"" + new string('b', 64) + "\""));
        string republished = null;
        try { republished = Bundle.OpenForSave(root, identity, Host).ManifestSha256; } finally { unseal(); }
        Check(republished != null && republished != expectedManifest, "a save resolves the same content republished by another compiler build (provenance differs, WorldContentId does not)");
        var other = new Dictionary<string, string>(identity) { ["worldContentId"] = new string('a', 64) };
        var otherWorld = Rejection(() => Bundle.OpenForSave(root, other, Host));
        Check(otherWorld != null && otherWorld.Contains("is missing from"), "a save naming other content never resolves this bundle");
        var incompatible = Rejection(() => Bundle.OpenForSave(root, identity, new Bundle.HostProfile("v1.4.9.0", "v1.2.8.119303", AllCapabilities)));
        Check(incompatible != null && incompatible.Contains("built for Bannerlord") && incompatible.Contains("Install the Bannerlord and War Sails versions"), "a save on an incompatible host is refused with what to install");
        var damaged = Rejection(() => { File.WriteAllText(extra, "x"); try { Bundle.OpenForSave(root, identity, Host); } finally { File.Delete(extra); } });
        Check(damaged != null && damaged.Contains("unlisted file") && damaged.Contains("Restore an intact copy"), "a save whose bundle is damaged is refused");
        var truncated = Rejection(() => { File.WriteAllBytes(ready, new byte[0]); try { Bundle.OpenForSave(root, identity, Host); } finally { File.WriteAllBytes(ready, readyBytes); } });
        Check(truncated != null && truncated.Contains("Restore an intact copy"), "a save whose bundle has an empty DATA_READY is refused, not crashed on");
        Check(Rejection(() => Bundle.OpenForSave(root, new Dictionary<string, string>(), Host)) != null, "a save without a bundle identity is refused");
        // A file changed after verification is caught at use.
        File.WriteAllBytes(height, original.Select(b => (byte)(b ^ 1)).ToArray());
        bool caught = false;
        try { opened.ReadVerified("compiled/terrain/heightfield.bin"); } catch (InvalidDataException) { caught = true; }
        File.WriteAllBytes(height, original);
        Check(caught, "a file changed after verification is refused when read");
    }
}
