using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Generation;
using MNBL.ProceduralWorld.Persistence;
using MNBL.ProceduralWorld.UI;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.Automation
{
    // MMR-57: the packaged module installed on its own (clean_install; phase=install|reload|readonly, save=<name>, seed, culture). Every phase
    // records where the module was loaded from and what its folder holds (each file's size and SHA-256, for tools/summarize_clean_install.py
    // to compare with the package manifest), the active modules and War Sails, and the engine log's missing-asset lines of the whole process.
    //   install:  the New Campaign setup screen generates the seed, Start Campaign enters the world (character creation as a player drives
    //             it), the campaign is checked, a manual save is written and the game ended;
    //   reload:   after the module was removed and reinstalled (no generated scene left in its folder), that save loads through the normal
    //             load entry to the map;
    //   readonly: with the module folder denied writes, a write is tried, the seed is generated and the save loaded; what happens is recorded.
    internal static class CleanInstallProbe
    {
        internal static IEnumerator Run(ProbeContext context) { return TwoWorldMatrixProbe.Nested(Body(context)); }

        private static readonly Regex MissingLine = new Regex(@"missing|not found|unable to find|could not find|cannot find|can't find|failed to load|couldn't load", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex[] Stock = { new Regex(@"^Missing shader from sack: "), new Regex(@"^Voice object for text id is not found: ") };

        private static IEnumerator Body(ProbeContext context)
        {
            string phase = context.Str("phase", "install");
            uint seed = (uint)context.Int("seed", 42);
            string culture = context.Str("culture", "empire");
            var failures = new List<string>(); context.Outputs["failures"] = failures;
            context.Outputs["scope"] = "MMR-57: the packaged module on its own (" + phase + ").";
            context.Outputs["phase"] = phase;
            context.Outputs["module"] = ModuleRecord();
            context.Checkpoint();
            try
            {
                if (phase == "install")
                {
                    var generate = new Dictionary<string, object>(); context.Outputs["generate"] = generate;
                    yield return Generate(context, seed, generate);
                    context.Outputs["surfaceTemplate"] = SurfaceTemplateRecord();
                    if (!Equals(generate["state"], "Generated")) throw new InvalidOperationException("the seed did not generate: " + generate["state"] + " " + Describe(generate["error"]));
                    var start = new Dictionary<string, object>(); context.Outputs["start"] = start;
                    yield return Start(context, culture, start);
                    if (Campaign.Current == null) throw new InvalidOperationException("the campaign did not start: " + start["state"] + " " + start["error"]);
                    context.Outputs["campaign"] = CampaignRecord();
                    var save = new Dictionary<string, object>(); context.Outputs["save"] = save;
                    yield return Save(context, "mnbl_probe57_" + context.ProcessId, save);
                    context.Outputs["sceneFolders"] = SceneFolders();
                    yield return TwoWorldMatrixProbe.EndGame(context);
                }
                else if (phase == "reload")
                {
                    context.Outputs["sceneFoldersBefore"] = SceneFolders();
                    var load = new Dictionary<string, object>(); context.Outputs["load"] = load;
                    yield return Load(context, context.Str("save", null), load);
                    if (Game.Current != null && Campaign.Current != null) context.Outputs["campaign"] = CampaignRecord();
                    context.Outputs["sceneFoldersAfter"] = SceneFolders();
                    if (Game.Current != null) yield return TwoWorldMatrixProbe.EndGame(context);
                }
                else if (phase == "readonly")
                {
                    context.Outputs["write"] = TryWrite();
                    var generate = new Dictionary<string, object>(); context.Outputs["generate"] = generate;
                    yield return Generate(context, seed, generate);
                    for (int i = 0; i < 4 && WorldSetupScreen.Current != null; i++) { WorldSetupScreen.Current.ViewModel.ExecuteBack(); yield return TwoWorldMatrixProbe.Frames(20); }
                    var load = new Dictionary<string, object>(); context.Outputs["load"] = load;
                    yield return Load(context, context.Str("save", null), load);
                    if (Game.Current != null) yield return TwoWorldMatrixProbe.EndGame(context);
                    context.Outputs["sceneFolders"] = SceneFolders();
                }
                else throw new InvalidOperationException("phase=install|reload|readonly");
            }
            finally
            {
                context.Outputs["log"] = Log(context);
            }
            context.Outputs["completed"] = true;
            context.Checkpoint();
            if (failures.Count > 0) throw new InvalidOperationException("Clean install expectations failed: " + String.Join("; ", failures));
        }

        // ---------------------------------------------------------------- the installed module

        private static string ModuleFolder() { return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(SubModule).Assembly.Location), "..", "..")); }

        private static string Sha(string path) { using (var s = File.OpenRead(path)) using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant(); }

        // Where the module was loaded from (relative to the game's root), its folder's files (generated scenes counted apart), whether a
        // development deploy's receipt is there, and the active modules with their versions; War Sails and Harmony among them.
        private static Dictionary<string, object> ModuleRecord()
        {
            var record = new Dictionary<string, object>();
            try
            {
                string folder = ModuleFolder(), root = Path.GetFullPath(Path.Combine(folder, "..", ".."));
                string assembly = typeof(SubModule).Assembly.Location;
                record["assembly"] = assembly.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
                record["assemblySha256"] = Sha(assembly);
                record["gameRootHasSpace"] = root.Contains(" "); record["gameRootNonAscii"] = root.Any(c => c > 127);
                var files = new List<object>(); int sceneFiles = 0;
                foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
                {
                    string rel = f.Substring(folder.Length).TrimStart('\\', '/').Replace('\\', '/');
                    if (rel.StartsWith("SceneObj/", StringComparison.OrdinalIgnoreCase)) { sceneFiles++; continue; }
                    files.Add(new Dictionary<string, object> { ["path"] = rel, ["bytes"] = new FileInfo(f).Length, ["sha256"] = Sha(f) });
                }
                record["files"] = files; record["sceneObjFiles"] = sceneFiles;
                record["developmentReceipt"] = File.Exists(Path.Combine(folder, "deployment.json"));
                var active = TaleWorlds.Engine.Utilities.GetModulesNames().ToList();
                record["activeModules"] = active.Select(id => { var info = TaleWorlds.ModuleManager.ModuleHelper.GetModuleInfo(id); return (object)new Dictionary<string, object> { ["id"] = id, ["version"] = info == null ? null : info.Version.ToString(), ["official"] = info != null && info.IsOfficial }; }).ToList();
                record["warSails"] = active.Contains("NavalDLC") && AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "NavalDLC");
                record["harmony"] = active.Contains("Bannerlord.Harmony");
                var host = WorldSetupHost.BundleHost();
                record["game"] = host.GameVersion; record["navalDlc"] = host.NavalVersion;
                record["store"] = SubModule.Store.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<home>");
            }
            catch (Exception e) { record["error"] = e.GetType().Name + ": " + e.Message; }
            return record;
        }

        private static string Describe(object error)
        {
            var d = error as Dictionary<string, object>;
            return d == null ? Convert.ToString(error, CultureInfo.InvariantCulture) : String.Join(" ", d.Select(kv => kv.Key + "=" + kv.Value));
        }

        // The surface template as the module uses it: the installed module's SurfaceTemplate holds only this module's own files and the
        // recipe; the per-user store holds the template derived from the installed official scene, each file with the recipe's SHA-256.
        private static Dictionary<string, object> SurfaceTemplateRecord()
        {
            var record = new Dictionary<string, object>();
            try
            {
                string packaged = Path.Combine(ModuleFolder(), "SurfaceTemplate");
                record["packagedFiles"] = Directory.Exists(packaged) ? Directory.GetFiles(packaged).Select(f => (object)Path.GetFileName(f)).OrderBy(f => (string)f, StringComparer.Ordinal).ToList() : new List<object>();
                var recipe = (IDictionary<string, object>)new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(File.ReadAllText(Path.Combine(packaged, "template-recipe.json")));
                var expected = (IDictionary<string, object>)recipe["expected"];
                string derived = Path.Combine(SubModule.Store, "surface-template", ((string)expected["scene.xscene"]).Substring(0, 16));
                record["derivedFolder"] = Path.GetFileName(derived); record["derivedExists"] = Directory.Exists(derived);
                record["createdThisProcess"] = MNBL.ProceduralWorld.World.SurfaceTemplateSource.CreatedThisProcess;
                record["derivedMatchesRecipe"] = Directory.Exists(derived) && expected.All(kv => File.Exists(Path.Combine(derived, kv.Key)) && Sha(Path.Combine(derived, kv.Key)) == (string)kv.Value);
            }
            catch (Exception e) { record["error"] = e.GetType().Name + ": " + e.Message; }
            return record;
        }

        private static Dictionary<string, object> SceneFolders()
        {
            string dir = Path.Combine(ModuleFolder(), "SceneObj");
            var names = Directory.Exists(dir) ? Directory.GetDirectories(dir).Select(d => (object)Path.GetFileName(d)).OrderBy(n => (string)n, StringComparer.Ordinal).ToList() : new List<object>();
            return new Dictionary<string, object> { ["exists"] = Directory.Exists(dir), ["scenes"] = names };
        }

        // A file created and deleted in the module folder: denied when the installation is read-only.
        private static Dictionary<string, object> TryWrite()
        {
            string probe = Path.Combine(ModuleFolder(), "write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
            try { File.WriteAllText(probe, "x"); File.Delete(probe); return new Dictionary<string, object> { ["writable"] = true }; }
            catch (Exception e) { return new Dictionary<string, object> { ["writable"] = false, ["error"] = e.GetType().Name }; }
        }

        // ---------------------------------------------------------------- generate, start, save, load

        private static IEnumerator Generate(ProbeContext context, uint seed, Dictionary<string, object> record)
        {
            Module.CurrentModule.ExecuteInitialStateOptionWithId(WorldSetupScreen.MenuId);
            double start = context.Seconds;
            while ((WorldSetupScreen.Current == null || !WorldSetupScreen.Current.MovieLoaded) && context.Seconds - start < 20) yield return null;
            record["setupOpened"] = WorldSetupScreen.Current != null && WorldSetupScreen.Current.MovieLoaded;
            if (WorldSetupScreen.Current == null) { record["state"] = "no setup screen"; yield break; }
            yield return TwoWorldMatrixProbe.Frames(10);
            var vm = WorldSetupScreen.Current.ViewModel; var model = vm.Model;
            record["waitText"] = vm.WaitText;
            vm.SeedText = seed.ToString(CultureInfo.InvariantCulture); yield return null;
            record["confirm"] = WorldSetupScreen.Current.HandleInput(k => k == SetupInput.Confirm, false).ToString();
            start = context.Seconds;
            while (model.State == SetupState.Generating && context.Seconds - start < 1200) yield return null;
            record["seconds"] = context.Seconds - start; record["state"] = model.State.ToString();
            record["error"] = model.Error == null ? null : new Dictionary<string, object> { ["category"] = model.Error.Category.ToString(), ["code"] = model.Error.Code, ["detail"] = model.Error.Detail };
            record["previewShown"] = model.Preview != null && vm.IsPreviewShown;
            record["sceneName"] = model.Preview == null ? null : model.Preview.SceneName;
            record["worldContentId"] = model.Preview == null ? null : model.Preview.WorldContentId;
        }

        private static IEnumerator Start(ProbeContext context, string culture, Dictionary<string, object> record)
        {
            var vm = WorldSetupScreen.Current.ViewModel; var model = vm.Model;
            vm.ExecuteStartCampaign();
            var creation = new NewCampaignFlowProbe.CreationDriver(culture, "MNBL Package");
            double start = context.Seconds, mapAt = -1;
            while (model.State == SetupState.Starting && context.Seconds - start < 3300)
            {
                if (Game.Current != null)
                {
                    creation.StepToMap();
                    if (Campaign.Current != null && Game.Current.GameStateManager.ActiveState is MapState) { Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop; if (mapAt < 0) mapAt = context.Seconds - start; }
                }
                yield return null;
            }
            record["seconds"] = context.Seconds - start; record["mapAfter"] = mapAt; record["state"] = model.State.ToString(); record["creation"] = creation.Record();
            var runner = (CoordinatorRunner)typeof(WorldSetupModel).GetField("runner", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(model);
            var stage = runner.LastStage;
            record["error"] = stage == null || stage.Error == null ? null : stage.Error.GetType().Name + ": " + stage.Error.Message;
            var caches = stage == null || stage.Host == null || !stage.Host.Record.ContainsKey("StartCampaign") ? null : stage.Host.Record["StartCampaign"] as Dictionary<string, object>;
            record["navigationCaches"] = caches == null || !caches.ContainsKey("navigationCaches") ? null : caches["navigationCaches"];
        }

        private static Dictionary<string, object> CampaignRecord()
        {
            var binding = WorldSaveBinding.BindingOf(Campaign.Current.GetCampaignBehavior<WorldIdentityBehavior>());
            string prefix = binding == null ? "" : binding.WorldId + "_";
            return new Dictionary<string, object> { ["worldId"] = binding == null ? null : binding.WorldId, ["settlements"] = Settlement.All.Count(s => s.StringId.StartsWith(prefix, StringComparison.Ordinal)),
                ["towns"] = Settlement.All.Count(s => s.IsTown && s.StringId.StartsWith(prefix, StringComparison.Ordinal)), ["mapState"] = Game.Current.GameStateManager.ActiveState is MapState,
                ["mapScene"] = Campaign.Current.MapSceneWrapper == null ? null : Campaign.Current.MapSceneWrapper.GetType().Name };
        }

        private static IEnumerator Save(ProbeContext context, string name, Dictionary<string, object> record)
        {
            record["name"] = name;
            var before = WorldSaveBinding.Last;
            double start = context.Seconds;
            Campaign.Current.SaveHandler.SaveAs(name);
            yield return null;
            while (context.Seconds - start < 120 && (WorldSaveBinding.Last == before || WorldSaveBinding.Last.State == EarlyWorldBindingStore.Intent || Campaign.Current.SaveHandler.IsSaving)) yield return null;
            var journal = WorldSaveBinding.Last;
            record["seconds"] = context.Seconds - start;
            record["published"] = journal != null && journal != before && journal.SaveName == name && journal.State == EarlyWorldBindingStore.Published;
        }

        private static IEnumerator Load(ProbeContext context, string name, Dictionary<string, object> record)
        {
            record["save"] = name;
            var info = name == null ? null : MBSaveLoad.GetSaveFileWithName(name);
            if (info == null) { record["loaded"] = false; record["error"] = "no save " + name; yield break; }
            var previous = GeneratedWorldLoader.Current;
            bool vanilla = false;
            double start = context.Seconds;
            SandBox.SandBoxSaveHelper.TryLoadSave(info, x => vanilla = true, () => { });
            var loader = GeneratedWorldLoader.Current == previous ? null : GeneratedWorldLoader.Current;
            while (loader != null && !loader.Done && context.Seconds - start < 3600) yield return null;
            record["seconds"] = context.Seconds - start; record["vanillaStarted"] = vanilla;
            record["loader"] = loader == null ? null : loader.Report;
            bool loaded = loader != null && loader.State == GeneratedWorldLoader.Phase.Loaded && !vanilla;
            if (loaded)
            {
                while (!(Game.Current.GameStateManager.ActiveState is MapState) && context.Seconds - start < 3700) yield return null;
                record["mapSeconds"] = context.Seconds - start;
                Diagnostics.RuntimeMissionProbe.Pause();
                yield return TwoWorldMatrixProbe.Frames(30);
            }
            record["loaded"] = loaded;
            record["state"] = loader == null ? null : loader.State.ToString(); record["refusal"] = loader == null || loader.Refusal == null ? null : loader.Refusal.ToString();
            record["loaderError"] = loader == null || loader.Error == null ? null : loader.Error.ToString();
        }

        // ---------------------------------------------------------------- the engine log

        // The whole process's engine log lines that report something missing: those naming this module or a generated world (ours), those of
        // a stock kind (shader variants compiled on first use, unvoiced lines), and the rest; the kinds counted.
        private static Dictionary<string, object> Log(ProbeContext context)
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Mount and Blade II Bannerlord", "logs", "rgl_log_" + context.ProcessId + ".txt");
            var record = new Dictionary<string, object> { ["path"] = "rgl_log_<pid>.txt" };
            if (!File.Exists(path)) { record["read"] = false; return record; }
            string text;
            try { using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var r = new StreamReader(s, Encoding.UTF8)) text = r.ReadToEnd(); }
            catch (Exception e) { record["read"] = false; record["error"] = e.GetType().Name; return record; }
            var ours = new List<string>(); var other = new List<string>(); int oursCount = 0, otherCount = 0, stock = 0;
            var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (!MissingLine.IsMatch(line)) continue;
                string body = Regex.Replace(line, @"^\[[^\]]*\]\s*", "");
                string kind = Regex.Replace(body, @"[\w./\\-]*\d[\w./\\-]*|""[^""]*""", "#"); kind = kind.Length > 70 ? kind.Substring(0, 70) : kind;
                int n; kinds.TryGetValue(kind, out n); kinds[kind] = n + 1;
                if (body.IndexOf("mnbl", StringComparison.OrdinalIgnoreCase) >= 0 || body.IndexOf("ProceduralWorld", StringComparison.OrdinalIgnoreCase) >= 0) { oursCount++; if (ours.Count < 20) ours.Add(body); }
                else if (Stock.Any(x => x.IsMatch(body))) stock++;
                else { otherCount++; if (other.Count < 20) other.Add(body); }
            }
            record["read"] = true; record["oursMissingCount"] = oursCount; record["oursMissing"] = ours; record["stockMissingCount"] = stock;
            record["otherMissingCount"] = otherCount; record["otherMissing"] = other; record["kinds"] = kinds.OrderByDescending(k => k.Value).Take(15).ToDictionary(k => k.Key, k => (object)k.Value);
            return record;
        }
    }
}
