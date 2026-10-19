using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Generation;
using MNBL.ProceduralWorld.Persistence;
using MNBL.ProceduralWorld.Surface;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace MNBL.ProceduralWorld.Automation
{
    // MMR-50: compatibility decisions against real loads (compat_matrix; save=<a generated compiled world's save>, default the newest
    // mnbl_1609fb72_start_* save):
    //   1. the read-only report (WorldCompatibilityReport) of the save: supported; its world's scene and derived reports swept, as a later
    //      job's sweep does: rebuildable derived cache; then the save loaded through the normal load entry: restored by the rebuild (the
    //      surface encoder only) as the report said;
    //   2. a save of the loaded campaign in this build's format (its header kept as the previous-save fixture of this build);
    //   3. four fixtures from that save's bytes, each with one header entry changed: an incompatible save format (another binding contract),
    //      an incompatible build (another save id version), a missing DLC (a War Sails version that is not installed) and a corrupted hash
    //      (the world package's manifest hash): each reported unsupported with the code the loader then refuses with, before any game
    //      starts; the source saves, the world's bundle and package keep every byte.
    // Every expectation is recomputed by tools/summarize_compat_matrix.py from the outputs.
    internal static class CompatibilityProbe
    {
        internal static IEnumerator Run(ProbeContext context) { return TwoWorldMatrixProbe.Nested(Body(context)); }

        private static IEnumerator Body(ProbeContext context)
        {
            var failures = new List<string>();
            context.Outputs["failures"] = failures;
            Action<bool, string> expect = (ok, what) => { if (!ok) failures.Add(what); context.Stage((ok ? "ok: " : "FAIL: ") + what); };
            var baselines = new List<object>(); context.Outputs["baselines"] = baselines;
            Action<string> baseline = label =>
            {
                var held = WorldLifetimeScope.Held();
                baselines.Add(new Dictionary<string, object> { ["at"] = label, ["game"] = Game.Current != null, ["held"] = held.ToDictionary(k => k.Key, k => (object)k.Value), ["tracked"] = WorldLifetimeScope.Names.Count });
                expect(Game.Current == null && held.Count == 0, "baseline " + label + ": no game and every tracked static at its baseline");
            };
            baseline("start");
            // ---- 1. the report against the load
            string source = context.Str("save", null) ?? Directory.GetFiles(Path.GetDirectoryName(BannerlordSaveCatalog.SavePath("x")), "mnbl_1609fb72_start_*.sav")
                .OrderByDescending(File.GetLastWriteTimeUtc).Select(Path.GetFileNameWithoutExtension).FirstOrDefault();
            context.Outputs["source"] = source;
            if (source == null) throw new InvalidOperationException("No save of a generated compiled world to start from.");
            var first = WorldCompatibilityReport.ForSave(source);
            context.Outputs["reportBeforeSweep"] = first.Report(source);
            expect(first.Class == WorldCompatibilityPolicy.Supported, "report " + source + ": supported (" + first.Class + ")");
            var package = Package(source);
            string scene = (string)package["sceneName"], packageDir = (string)package["directory"], bundleDir = Path.Combine(CompileStage.BundleRoot(), (string)package["contentId"]);
            // The world's immutable bytes before anything: the rebuild and every refused load must leave them as they are.
            string worldAtStart = TwoWorldMatrixProbe.TreeHash(bundleDir) + TwoWorldMatrixProbe.TreeHash(packageDir);
            var swept = new List<string>();
            if (SceneNames.IsWorldScene(scene) && Directory.Exists(RuntimeWorldBackend.SceneFolder(scene))) { Directory.Delete(RuntimeWorldBackend.SceneFolder(scene), true); swept.Add("scene"); }
            foreach (var report in new[] { NavigationGate.ReportPath(SubModule.Store, scene), RuntimeWorldBackend.SettlementReportPath(scene), RuntimePlayabilityValidator.ReportPath(SubModule.Store, scene) })
                if (File.Exists(report)) { File.Delete(report); swept.Add(Path.GetFileName(Path.GetDirectoryName(report))); }
            context.Outputs["swept"] = swept;
            var second = WorldCompatibilityReport.ForSave(source);
            context.Outputs["reportAfterSweep"] = second.Report(source);
            expect(second.Class == WorldCompatibilityPolicy.Rebuildable, "report " + source + " after the sweep: rebuildable derived cache (" + second.Class + ")");
            var info = MBSaveLoad.GetSaveFileWithName(source);
            var previous = GeneratedWorldLoader.Current;
            bool vanilla = false;
            SandBox.SandBoxSaveHelper.TryLoadSave(info, r => vanilla = true, () => { });
            var loader = GeneratedWorldLoader.Current == previous ? null : GeneratedWorldLoader.Current;
            double start = context.Seconds;
            while (loader != null && !loader.Done && context.Seconds - start < 3600) yield return null;
            context.Outputs["load"] = loader == null ? null : loader.Report;
            context.Outputs["vanillaStarted"] = vanilla;
            context.Outputs["worldUnchangedByRebuild"] = TwoWorldMatrixProbe.TreeHash(bundleDir) + TwoWorldMatrixProbe.TreeHash(packageDir) == worldAtStart;
            expect(loader != null && loader.State == GeneratedWorldLoader.Phase.Loaded && !vanilla, "the rebuildable save loaded through the world loader (" + (loader == null ? "no loader" : loader.State + " " + loader.Refusal + " " + loader.Error) + ")");
            if (loader == null || loader.State != GeneratedWorldLoader.Phase.Loaded) { yield return TwoWorldMatrixProbe.EndGame(context); baseline("after the failed load"); throw new InvalidOperationException("The source save did not load."); }
            // ---- 2. a save in this build's format
            Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
            yield return TwoWorldMatrixProbe.Frames(20);
            string current = "mnbl_probe50_current_" + context.ProcessId;
            context.Outputs["current"] = current;
            var before = WorldSaveBinding.Last;
            Campaign.Current.SaveHandler.SaveAs(current);
            start = context.Seconds;
            yield return null;
            while (context.Seconds - start < 120 && (WorldSaveBinding.Last == before || WorldSaveBinding.Last.State == EarlyWorldBindingStore.Intent || Campaign.Current.SaveHandler.IsSaving)) yield return null;
            var journal = WorldSaveBinding.Last;
            expect(journal != null && journal != before && journal.SaveName == current && journal.State == EarlyWorldBindingStore.Published, "a save of the loaded campaign in this build's format: published");
            yield return TwoWorldMatrixProbe.EndGame(context);
            baseline("after the load and save");
            var header = SaveFolder.ReadMetadata(BannerlordSaveCatalog.SavePath(current));
            context.Outputs["currentHeader"] = header.Where(kv => kv.Key.StartsWith("mnbl.", StringComparison.Ordinal) || kv.Key == "ApplicationVersion" || kv.Key == "Modules" || kv.Key.StartsWith("Module_", StringComparison.Ordinal))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => (object)kv.Value);
            var third = WorldCompatibilityReport.ForSave(current);
            context.Outputs["reportCurrent"] = third.Report(current);
            expect(third.Class == WorldCompatibilityPolicy.Supported && SaveBindingContract.CompatibilityFrom(header).Count == SaveBindingContract.Keys.Length,
                "report " + current + " (this build's format, every build key recorded): supported (" + third.Class + ")");
            // ---- 3. the fixtures
            var bytes = File.ReadAllBytes(BannerlordSaveCatalog.SavePath(current));
            string naval, manifest, contract, saveIds;
            header.TryGetValue("mnbl.save.naval", out naval); header.TryGetValue("mnbl.world.manifestSha256", out manifest);
            header.TryGetValue("mnbl.save.contract", out contract); header.TryGetValue("mnbl.save.saveIds", out saveIds);
            string corrupted = manifest == null ? null : manifest.Substring(0, manifest.Length - 8) + new string(manifest.Substring(manifest.Length - 8).Select(c => c == '0' ? '1' : '0').ToArray());
            var fixtures = new[] {
                new { Name = "incompatible-schema", From = "\"mnbl.save.contract\":\"" + contract + "\"", To = "\"mnbl.save.contract\":\"mnbl.save-binding.v99\"", Code = LoadPolicy.IncompatibleBuild },
                new { Name = "incompatible-build", From = "\"mnbl.save.saveIds\":\"" + saveIds + "\"", To = "\"mnbl.save.saveIds\":\"7910000/v99\"", Code = LoadPolicy.IncompatibleBuild },
                new { Name = "missing-dlc", From = "\"mnbl.save.naval\":\"" + naval + "\"", To = "\"mnbl.save.naval\":\"v1.3.0.120000\"", Code = LoadPolicy.IncompatibleBuild },
                new { Name = "corrupted-hash", From = "\"mnbl.world.manifestSha256\":\"" + manifest + "\"", To = "\"mnbl.world.manifestSha256\":\"" + corrupted + "\"", Code = LoadPolicy.DamagedWorld } };
            var rows = new Dictionary<string, object>(); context.Outputs["fixtures"] = rows;
            string sourceSaveHash = WorldSaveTransfer.Sha256File(BannerlordSaveCatalog.SavePath(source)), currentSaveHash = WorldSaveTransfer.Sha256File(BannerlordSaveCatalog.SavePath(current));
            string worldBefore = TwoWorldMatrixProbe.TreeHash(bundleDir) + TwoWorldMatrixProbe.TreeHash(packageDir);
            foreach (var fixture in fixtures)
            {
                var row = new Dictionary<string, object> { ["expected"] = fixture.Code }; rows[fixture.Name] = row;
                string name = "mnbl_probe50_" + fixture.Name + "_" + context.ProcessId, path = BannerlordSaveCatalog.SavePath(name);
                try { File.WriteAllBytes(path, TwoWorldMatrixProbe.RewriteHeader(bytes, fixture.From, fixture.To)); }
                catch (Exception error) { row["error"] = error.Message; expect(false, "fixture " + fixture.Name + " written (" + error.Message + ")"); continue; }
                string fixtureHash = WorldSaveTransfer.Sha256File(path);
                var report = WorldCompatibilityReport.ForSave(name);
                row["report"] = report.Report(name);
                var fixtureInfo = MBSaveLoad.GetSaveFileWithName(name);
                bool installed = false, cancelled = false, gameSeen = false;
                previous = GeneratedWorldLoader.Current;
                if (fixtureInfo != null) SandBox.SandBoxSaveHelper.TryLoadSave(fixtureInfo, r => installed = true, () => cancelled = true);
                var refusing = GeneratedWorldLoader.Current == previous ? null : GeneratedWorldLoader.Current;
                start = context.Seconds;
                while (refusing != null && !refusing.Done && context.Seconds - start < 300) { if (Game.Current != null) gameSeen = true; yield return null; }
                yield return TwoWorldMatrixProbe.Frames(5);
                if (refusing != null) refusing.Dismiss();
                yield return TwoWorldMatrixProbe.Frames(5);
                if (Game.Current != null) gameSeen = true;
                row["listed"] = fixtureInfo != null; row["loaderState"] = refusing == null ? null : refusing.State.ToString();
                row["loaderCode"] = refusing == null || refusing.Refusal == null ? null : refusing.Refusal.Code; row["loaderDetail"] = refusing == null || refusing.Refusal == null ? null : refusing.Refusal.Detail;
                row["reportCode"] = report.Refusal == null ? null : report.Refusal.Code; row["reportClass"] = report.Class;
                row["installedLoadRan"] = installed; row["cancelled"] = cancelled; row["gameStarted"] = gameSeen; row["phases"] = refusing == null ? null : refusing.Report["phases"];
                row["fixtureUnchanged"] = WorldSaveTransfer.Sha256File(path) == fixtureHash;
                expect(report.Class == WorldCompatibilityPolicy.Unsupported && (string)row["reportCode"] == fixture.Code && (string)row["loaderCode"] == fixture.Code && !installed && !gameSeen && cancelled && (bool)row["fixtureUnchanged"],
                    "fixture " + fixture.Name + ": reported unsupported (" + row["reportCode"] + ") and refused by the load with the same code before any game started; the fixture's bytes unchanged");
                try { SaveBindingRuntime.Store.Saves.Delete(name); } catch (Exception) { }
            }
            // ---- an isolated invalid copy of the world data from a separate test root (the one-shot's step 6): the world's bundle copied into
            // a fresh folder outside the store, one byte of its compiled travel cells flipped, then opened by the verifier every load runs
            // (WorldDataBundle.Open with this host): refused before anything loads. The copy is removed; the original's bytes are checked
            // with the others below.
            var isolated = new Dictionary<string, object>(); context.Outputs["isolatedCopy"] = isolated;
            string testRoot = Path.Combine(Path.GetTempPath(), "mnbl-isolated-" + context.ProcessId);
            try
            {
                string copy = Path.Combine(testRoot, "bundles", Path.GetFileName(bundleDir));
                foreach (var file in Directory.GetFiles(bundleDir, "*", SearchOption.AllDirectories))
                {
                    string to = Path.Combine(copy, file.Substring(bundleDir.Length).TrimStart('\\', '/'));
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    File.Copy(file, to);
                }
                string damaged = Path.Combine(copy, "compiled", "travel", "cells.bin");
                var cells = File.ReadAllBytes(damaged); cells[cells.Length / 2] ^= 0x5a; File.WriteAllBytes(damaged, cells);
                isolated["root"] = "<temp>/mnbl-isolated-<pid>/bundles/" + Path.GetFileName(bundleDir); isolated["damaged"] = "compiled/travel/cells.bin";
                string refusal = null;
                try { WorldDataBundle.Open(copy, MNBL.ProceduralWorld.UI.WorldSetupHost.BundleHost()); }
                catch (WorldDataBundle.RejectedException error) { refusal = error.Message.Replace(testRoot, "<test root>"); }
                isolated["refused"] = refusal != null; isolated["refusal"] = refusal;
                expect(refusal != null, "an isolated invalid copy of the world's data, from a separate test root, is refused by the bundle verifier before anything loads");
            }
            finally
            {
                try { if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true); } catch (Exception) { }
                isolated["removed"] = !Directory.Exists(testRoot);
            }
            var preserved = new Dictionary<string, object> { ["sourceSave"] = WorldSaveTransfer.Sha256File(BannerlordSaveCatalog.SavePath(source)) == sourceSaveHash,
                ["currentSave"] = WorldSaveTransfer.Sha256File(BannerlordSaveCatalog.SavePath(current)) == currentSaveHash, ["bundleAndPackage"] = TwoWorldMatrixProbe.TreeHash(bundleDir) + TwoWorldMatrixProbe.TreeHash(packageDir) == worldBefore };
            context.Outputs["preserved"] = preserved;
            expect(preserved.Values.All(v => Equals(v, true)), "the refused loads kept every byte of the source saves, the world's bundle and its package");
            baseline("after the refused loads");
            // The probe's own save goes; the source save stays.
            bool deleted;
            try { deleted = SaveBindingRuntime.Store.Saves.Delete(current) && SaveBindingRuntime.Store.Saves.Read(current) == null; } catch (Exception) { deleted = false; }
            context.Outputs["cleanup"] = new Dictionary<string, object> { [current] = deleted };
            context.Outputs["completed"] = true;
            context.Checkpoint();
            if (failures.Count > 0) throw new InvalidOperationException("Compatibility matrix expectations failed: " + String.Join("; ", failures.Take(10)));
        }

        // The save's world package: its directory, scene and content id.
        private static Dictionary<string, object> Package(string save)
        {
            var binding = WorldBinding.FromHeader(SaveFolder.ReadMetadata(BannerlordSaveCatalog.SavePath(save)));
            string dir = Path.Combine(GeneratedWorldPackage.Root, binding.PackageName);
            var manifest = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(File.ReadAllText(Path.Combine(dir, "world.json")));
            return new Dictionary<string, object> { ["directory"] = dir, ["sceneName"] = manifest["sceneName"], ["contentId"] = binding.ContentId };
        }
    }
}
