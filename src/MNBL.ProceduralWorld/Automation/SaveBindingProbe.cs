using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using HarmonyLib;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Generation;
using MNBL.ProceduralWorld.Persistence;
using MNBL.ProceduralWorld.UI;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.Automation
{
    // MMR-48: the save binding in the running game (save_binding; seed=<n>, culture=<id>). A campaign is started through the production path
    // (the setup screen generates the seed, Start Campaign, character creation, the activation commits and publishes the initial save), then:
    //   1. the startup pass ran the later-save recovery (nothing left unresolved) and the index binds every save kind;
    //   2. mutable changes: a town changes owner, the main party moves;
    //   3. a Save As, a quick save (over the Save As: the active slot), an autosave (the next saveauto slot; a file already there is copied
    //      first and restored after) and an Ironman save are each journaled before the write and published after their readback: bound to
    //      this campaign instance and its exact world (package, manifest, content id), whole, with the build's compatibility keys;
    //   4. the world package and data bundle are byte-identical after all of it, the bundle still verifies, every header names its content id;
    //   5. the reference index counts the campaign's saves;
    //   6. recovery with the game's own save list: an intent whose file was torn is removed, one whose file was never touched is abandoned;
    //   7. the Save As and Ironman saves are exported with their world and imported into a temporary store twice (copied, then reused), and the
    //      imported saves inspect as loadable, without regenerating anything;
    //   8. back at the main menu, with no game, the saves' bindings are read from their headers (before any scene or object is reconstructed).
    // Cleanup (Cleanup, idempotent) deletes the probe's saves and the campaign's initial save and puts the autosave slot's earlier file back.
    // It runs at the end and from a finally whatever fails (the runner disposes every suspended step on an exception); the earlier file is
    // also kept beside the store with a record, and a later save_binding run restores one an interrupted run left (review finding).
    internal static class SaveBindingProbe
    {
        internal static IEnumerator Run(ProbeContext context)
        {
            var stack = new Stack<IEnumerator>(); stack.Push(Body(context));
            try
            {
                while (stack.Count > 0)
                {
                    if (!stack.Peek().MoveNext()) { (stack.Pop() as IDisposable)?.Dispose(); continue; }
                    var nested = stack.Peek().Current as IEnumerator;
                    if (nested != null) stack.Push(nested); else yield return stack.Peek().Current;
                }
            }
            finally
            {
                // A step that threw, or a run abandoned: every suspended step's finally runs (the cleanup among them).
                while (stack.Count > 0) { try { (stack.Pop() as IDisposable)?.Dispose(); } catch (Exception) { } }
            }
        }

        private sealed class State
        {
            public List<string> Failures = new List<string>();
            public Action<bool, string> Expect;
            public WorldBinding Binding;
            public Dictionary<string, object> Saves = new Dictionary<string, object>();
        }

        // What the probe changed in the real save folder, put back once.
        private sealed class Cleanup
        {
            public readonly List<string> Delete = new List<string>();
            public string AutosavePath, BackupPath, RecordPath;
            public byte[] AutosaveOriginal;
            public DateTime AutosaveOriginalTime;
            public bool? WasIronman;
            public bool Done;
            public readonly Dictionary<string, object> Report = new Dictionary<string, object>();

            public void Run(ISaveCatalog catalog)
            {
                if (Done) return;
                Done = true;
                if (WasIronman.HasValue) CampaignOptions.IsIronmanMode = WasIronman.Value;
                var deleted = new Dictionary<string, object>();
                foreach (var name in Delete.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try { deleted[name] = catalog.Read(name) == null || (catalog.Delete(name) && catalog.Read(name) == null); }
                    catch (Exception error) { deleted[name] = error.GetType().Name + ": " + error.Message; }
                }
                Report["deleted"] = deleted;
                if (AutosavePath != null)
                {
                    try
                    {
                        if (AutosaveOriginal != null)
                        {
                            File.WriteAllBytes(AutosavePath, AutosaveOriginal); File.SetLastWriteTimeUtc(AutosavePath, AutosaveOriginalTime);
                            Report["autosaveRestored"] = File.ReadAllBytes(AutosavePath).SequenceEqual(AutosaveOriginal);
                            if (Equals(Report["autosaveRestored"], true)) { File.Delete(BackupPath); File.Delete(RecordPath); }
                        }
                        else Report["autosaveRestored"] = true;   // the slot had no file: the probe's autosave is among Delete
                    }
                    catch (Exception error) { Report["autosaveRestored"] = error.GetType().Name + ": " + error.Message; }
                }
            }

            public bool Clean
            {
                get
                {
                    var deleted = Report.ContainsKey("deleted") ? (Dictionary<string, object>)Report["deleted"] : new Dictionary<string, object>();
                    return Done && deleted.Values.All(v => Equals(v, true)) && (AutosavePath == null || Equals(Report.ContainsKey("autosaveRestored") ? Report["autosaveRestored"] : null, true));
                }
            }
        }

        private static IEnumerator Body(ProbeContext context)
        {
            context.Outputs["scope"] = "MMR-48 save binding: a committed campaign's Save As, quick save, autosave and Ironman save journaled and read back bound to the exact world; mutable ownership and party changes leave the immutable world byte-identical; recovery with the game's save list; export and import of saves with their world without regeneration; headers read at the main menu.";
            uint seed = (uint)context.Int("seed", 42);
            string culture = context.Str("culture", "empire");
            int pid = context.ProcessId;
            var st = new State();
            context.Outputs["failures"] = st.Failures;
            st.Expect = (ok, what) => { if (!ok) st.Failures.Add(what); context.Stage((ok ? "ok: " : "FAIL: ") + what); };
            var expect = st.Expect;
            var catalog = SaveBindingRuntime.Store.Saves;
            string saveAs = "mnbl_probe48_saveas_" + pid;
            var cleanup = new Cleanup();
            context.Outputs["cleanup"] = cleanup.Report;
            context.Outputs["leftoverBackups"] = RestoreLeftovers(pid);

            // 1. The startup pass.
            var startup = SaveBindingRuntime.Startup;
            var startupIndex = startup != null && startup.ContainsKey("index") ? startup["index"] as Dictionary<string, object> : null;
            var laterRecovery = startup != null && startup.ContainsKey("laterRecovery") ? startup["laterRecovery"] as Dictionary<string, object> : null;
            context.Outputs["startup"] = new Dictionary<string, object> { ["laterRecovery"] = laterRecovery,
                ["saveKindsBound"] = startupIndex == null ? null : (startupIndex.ContainsKey("saveKindsBound") ? startupIndex["saveKindsBound"] : null),
                ["discoveryComplete"] = startupIndex == null ? null : startupIndex["discoveryComplete"], ["gaps"] = startupIndex == null ? null : startupIndex["gaps"] };
            expect(laterRecovery != null && laterRecovery.ContainsKey("resolved") && laterRecovery["unresolved"] is IList && ((IList)laterRecovery["unresolved"]).Count == 0
                   && laterRecovery["unreadableJournals"] is IList && ((IList)laterRecovery["unreadableJournals"]).Count == 0 && startupIndex != null && startupIndex.ContainsKey("counts"),
                "the startup pass ran the later-save recovery (no intent left unresolved, no unreadable journal) and reported the index's reference counts");

            // Start a campaign through the production path.
            Module.CurrentModule.ExecuteInitialStateOptionWithId(WorldSetupScreen.MenuId);
            double start = context.Seconds;
            while ((WorldSetupScreen.Current == null || !WorldSetupScreen.Current.MovieLoaded) && context.Seconds - start < 20) yield return null;
            if (WorldSetupScreen.Current == null) throw new InvalidOperationException("The setup screen did not open.");
            var screen = WorldSetupScreen.Current; var vm = screen.ViewModel; var model = vm.Model;
            var runner = (CoordinatorRunner)typeof(WorldSetupModel).GetField("runner", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(model);
            vm.SeedText = seed.ToString(CultureInfo.InvariantCulture); yield return null;
            screen.HandleInput(k => k == SetupInput.Confirm, false);
            context.Stage("save_binding:generate");
            start = context.Seconds;
            while (model.State == SetupState.Generating && context.Seconds - start < 1200) yield return null;
            if (model.State != SetupState.Generated) throw new InvalidOperationException("The world was not generated: " + model.State + " " + model.Error);
            vm.ExecuteStartCampaign();
            context.Stage("save_binding:start");
            var creation = new NewCampaignFlowProbe.CreationDriver(culture, "MNBL Saves");
            start = context.Seconds;
            while (model.State == SetupState.Starting && context.Seconds - start < 3300)
            {
                if (Game.Current != null)
                {
                    string step = creation.StepToMap();
                    if (step != null) context.Stage("save_binding:" + step);
                    if (Campaign.Current != null && Game.Current.GameStateManager.ActiveState is MapState) Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
                }
                yield return null;
            }
            context.Outputs["startSeconds"] = context.Seconds - start;
            if (model.State != SetupState.Started || Campaign.Current == null) throw new InvalidOperationException("The campaign did not start: " + model.State + (runner.LastStage != null && runner.LastStage.Error != null ? " " + runner.LastStage.Error.Message : ""));
            try
            {
                while (!(Game.Current.GameStateManager.ActiveState is MapState) && context.Seconds - start < 3400) yield return null;
                Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
                yield return Frames(30);
                var identity = Campaign.Current.GetCampaignBehavior<WorldIdentityBehavior>();
                var binding = identity == null ? null : WorldSaveBinding.BindingOf(identity);
                st.Binding = binding;
                context.Outputs["binding"] = binding == null ? null : new Dictionary<string, object> { ["worldId"] = binding.WorldId, ["package"] = binding.PackageName, ["instance"] = binding.Instance, ["contentId"] = binding.ContentId };
                expect(WorldSaveBinding.Attached && binding != null && !binding.Legacy && binding.ContentId != WorldBinding.NoContent && runner.LastStage != null && runner.LastStage.Instance == binding.Instance,
                    "the committed campaign is bound: its world identity names the activation's campaign instance, package and data bundle, and the save binding is attached");
                if (binding == null) throw new InvalidOperationException("No world binding.");
                var initial = SaveBindingRuntime.Store.Journal(binding.Instance);
                context.Outputs["initialSave"] = initial == null ? null : initial.Report();
                expect(initial != null && initial.State == EarlyWorldBindingStore.Published, "the activation's initial save is published through the shared journal (MMR-62)");
                if (initial != null) cleanup.Delete.Add(initial.SaveName);
                string packageDir = Path.Combine(GeneratedWorldPackage.Root, binding.PackageName), bundleDir = Path.Combine(CompileStage.BundleRoot(), binding.ContentId);
                var host = WorldSetupHost.BundleHost();
                var immutableBefore = new Dictionary<string, object> { ["package"] = TreeHash(packageDir), ["bundle"] = TreeHash(bundleDir), ["bundleProblems"] = WorldDataBundle.Problems(bundleDir, host, true).Count };
                context.Outputs["immutableBefore"] = immutableBefore;

                // 2. Mutable changes.
                var mutable = new Dictionary<string, object>(); context.Outputs["mutable"] = mutable;
                var town = Settlement.All.Where(s => s.IsTown && s.OwnerClan != null && s.OwnerClan != Clan.PlayerClan).OrderBy(s => s.StringId, StringComparer.Ordinal).First();
                mutable["town"] = town.StringId; mutable["ownerBefore"] = town.OwnerClan.StringId;
                ChangeOwnerOfSettlementAction.ApplyByDefault(Hero.MainHero, town);
                mutable["ownerAfter"] = town.OwnerClan == null ? null : town.OwnerClan.StringId;
                var village = Settlement.All.Where(s => s.IsVillage).OrderBy(s => s.StringId, StringComparer.Ordinal).Last();
                string partyBefore = MobileParty.MainParty.Position.ToString();
                MobileParty.MainParty.Position = village.GatePosition;
                yield return Frames(10);
                mutable["partyBefore"] = partyBefore; mutable["partyAfter"] = MobileParty.MainParty.Position.ToString(); mutable["movedTo"] = village.StringId;
                expect(town.OwnerClan == Clan.PlayerClan && (string)mutable["partyAfter"] != partyBefore, "mutable campaign changes: a town of the world changes owner to the player's clan and the main party moves");

                // 3. Every save kind.
                context.Outputs["saves"] = st.Saves;
                cleanup.Delete.Add(saveAs);
                yield return Save(context, st, SaveJournal.SaveAs, saveAs, () => Campaign.Current.SaveHandler.SaveAs(saveAs));
                yield return Save(context, st, SaveJournal.QuickSave, MBSaveLoad.ActiveSaveSlotName, () => Campaign.Current.SaveHandler.QuickSaveCurrentGame());
                int autoIndex = (int)AccessTools.Field(typeof(MBSaveLoad), "AutoSaveIndex").GetValue(null);
                string autosave = "saveauto" + (autoIndex + 1 > 3 ? 1 : autoIndex + 1).ToString(CultureInfo.InvariantCulture);
                cleanup.AutosavePath = BannerlordSaveCatalog.SavePath(autosave);
                if (File.Exists(cleanup.AutosavePath))
                {
                    cleanup.AutosaveOriginal = File.ReadAllBytes(cleanup.AutosavePath); cleanup.AutosaveOriginalTime = File.GetLastWriteTimeUtc(cleanup.AutosavePath);
                    cleanup.BackupPath = Path.Combine(SubModule.Store, "probe", "autosave-backup-" + pid + ".sav");
                    cleanup.RecordPath = Path.Combine(SubModule.Store, "probe", "autosave-backup-" + pid + ".json");
                    Directory.CreateDirectory(Path.GetDirectoryName(cleanup.BackupPath));
                    File.WriteAllBytes(cleanup.BackupPath, cleanup.AutosaveOriginal);
                    File.WriteAllText(cleanup.RecordPath, new JavaScriptSerializer().Serialize(new Dictionary<string, object> { ["pid"] = pid, ["slot"] = autosave,
                        ["writtenUtcTicks"] = cleanup.AutosaveOriginalTime.Ticks.ToString(CultureInfo.InvariantCulture), ["sha256"] = WorldSaveTransfer.Sha256File(cleanup.BackupPath) }));
                }
                else cleanup.Delete.Add(autosave);
                cleanup.Report["autosaveSlot"] = autosave; cleanup.Report["autosaveHadFile"] = cleanup.AutosaveOriginal != null;
                yield return Save(context, st, SaveJournal.AutoSave, autosave, () =>
                {
                    var modeType = AccessTools.Inner(AccessTools.Inner(typeof(SaveHandler), "SaveArgs"), "SaveMode");
                    AccessTools.Method(typeof(SaveHandler), "SetSaveArgs").Invoke(Campaign.Current.SaveHandler, new object[] { Enum.ToObject(modeType, 2), null });
                });
                cleanup.WasIronman = CampaignOptions.IsIronmanMode;
                CampaignOptions.IsIronmanMode = true;
                string ironman = Campaign.Current.SaveHandler.IronmanModSaveName;
                cleanup.Delete.Add(ironman);
                yield return Save(context, st, SaveJournal.Ironman, ironman, () => Campaign.Current.SaveHandler.SaveAs(saveAs));
                CampaignOptions.IsIronmanMode = cleanup.WasIronman.Value;
                context.Checkpoint();

                // 4. The immutable world is unchanged by all of it.
                var immutableAfter = new Dictionary<string, object> { ["package"] = TreeHash(packageDir), ["bundle"] = TreeHash(bundleDir), ["bundleProblems"] = WorldDataBundle.Problems(bundleDir, host, true).Count };
                context.Outputs["immutableAfter"] = immutableAfter;
                var bundleRow = WorldInspector.Bundle(bundleDir, host);
                var headerContent = new[] { saveAs, autosave, ironman }.Select(n => Bound(catalog, n)).ToList();
                context.Outputs["bundleWorldContentId"] = bundleRow.ContainsKey("worldContentId") ? bundleRow["worldContentId"] : null;
                expect((string)immutableBefore["package"] == (string)immutableAfter["package"] && (string)immutableBefore["bundle"] == (string)immutableAfter["bundle"] && (int)immutableAfter["bundleProblems"] == 0
                       && Equals(bundleRow.ContainsKey("worldContentId") ? bundleRow["worldContentId"] : null, binding.ContentId) && headerContent.All(b => b != null && b.ContentId == binding.ContentId && b.ManifestSha256 == binding.ManifestSha256),
                    "c2: after the ownership change, the party move and four saves the world package and data bundle are byte-identical, the bundle still verifies with the same worldContentId, and every save names that content id and package manifest");

                // 5. The reference index.
                var index = WorldReferenceIndex.Build(catalog, SaveBindingRuntime.Store, new[] { binding }, true, SaveBindingRuntime.Later);
                var ours = index.For(binding.PackageName).Where(r => r.Instance == binding.Instance).ToList();
                var counts = index.Counts();
                object packageCounts; ((Dictionary<string, object>)counts["packages"]).TryGetValue(binding.PackageName, out packageCounts);
                object bundleCounts; ((Dictionary<string, object>)counts["bundles"]).TryGetValue(binding.ContentId, out bundleCounts);
                context.Outputs["index"] = new Dictionary<string, object> { ["discoveryComplete"] = index.DiscoveryComplete, ["gaps"] = index.Gaps.Take(5).ToList(), ["gapCount"] = index.Gaps.Count,
                    ["campaignReferences"] = ours.Select(r => r.Kind + " " + r.Name).ToList(), ["package"] = packageCounts, ["bundle"] = bundleCounts };
                var names = ours.Where(r => r.Kind == WorldReference.Save).Select(r => r.Name).ToList();
                expect(initial != null && names.Contains(initial.SaveName) && names.Contains(saveAs) && names.Contains(autosave) && names.Contains(ironman) && ours.Any(r => r.Kind == WorldReference.Active)
                       && bundleCounts != null && (int)((Dictionary<string, object>)bundleCounts)["instances"] >= 1,
                    "the reference index finds the campaign's initial save, Save As, autosave and Ironman save and the active campaign by their headers, and counts the bundle's references and campaign instances");

                // 6. Recovery with the game's save list.
                var recovery = new Dictionary<string, object>(); context.Outputs["recovery"] = recovery;
                string torn = "mnbl_probe48_torn_" + pid, untouched = "mnbl_probe48_untouched_" + pid;
                cleanup.Delete.Add(torn); cleanup.Delete.Add(untouched);
                string saveAsPath = BannerlordSaveCatalog.SavePath(saveAs);
                if (!File.Exists(saveAsPath)) throw new InvalidOperationException("The Save As file is missing; recovery cannot be planted.");
                var bytes = File.ReadAllBytes(saveAsPath);
                int headerLength = BitConverter.ToInt32(bytes, 0);
                File.WriteAllBytes(BannerlordSaveCatalog.SavePath(untouched), bytes);
                var tornOp = SaveBindingRuntime.Later.Begin(binding, SaveJournal.QuickSave, torn);
                var untouchedOp = SaveBindingRuntime.Later.Begin(binding, SaveJournal.AutoSave, untouched);
                File.WriteAllBytes(BannerlordSaveCatalog.SavePath(torn), bytes.Take(4 + headerLength + (bytes.Length - 4 - headerLength) / 2).ToArray());
                var report = SaveBindingRuntime.Later.Recover();
                var tornAfter = SaveBindingRuntime.Later.Journal(tornOp.Id); var untouchedAfter = SaveBindingRuntime.Later.Journal(untouchedOp.Id);
                recovery["report"] = report.ToDictionary(); recovery["torn"] = tornAfter == null ? null : tornAfter.Report(); recovery["untouched"] = untouchedAfter == null ? null : untouchedAfter.Report();
                expect(tornAfter != null && tornAfter.State == EarlyWorldBindingStore.Removed && catalog.Read(torn) == null && untouchedAfter != null && untouchedAfter.State == EarlyWorldBindingStore.Abandoned && catalog.Read(untouched) != null,
                    "recovery with the game's save list: an intent whose file holds this campaign's header before torn game data is removed (the file deleted through the game); one whose file was never touched after the intent is abandoned and the file kept");

                // 7. Export and import without regeneration.
                var transfer = new Dictionary<string, object>(); context.Outputs["transfer"] = transfer;
                string probeRoot = Path.Combine(SubModule.Store, "probe", "transfer-" + pid);
                Func<string, bool, IList<string>> verifier = (d, n) => WorldDataBundle.Problems(d, host, n);
                try
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var exported = WorldSaveTransfer.Export(new WorldSaveTransfer.Options(SubModule.Store, BannerlordSaveCatalog.Folder, verifier), new[] { saveAs, ironman }, probeRoot, "export");
                    transfer["exportSeconds"] = watch.Elapsed.TotalSeconds; transfer["export"] = exported.Where(k => k.Key != "directory").ToDictionary(k => k.Key, k => k.Value);
                    var importOptions = new WorldSaveTransfer.Options(Path.Combine(probeRoot, "store"), Path.Combine(probeRoot, "saves"), verifier);
                    watch.Restart();
                    var first = WorldSaveTransfer.Import(importOptions, (string)exported["directory"]);
                    transfer["importSeconds"] = watch.Elapsed.TotalSeconds;
                    var second = WorldSaveTransfer.Import(importOptions, (string)exported["directory"], new Dictionary<string, string> { [saveAs] = saveAs + " moved", [ironman] = ironman + " moved" });
                    transfer["first"] = Strip(first); transfer["second"] = Strip(second);
                    var inspected = WorldInspector.Store(Path.Combine(probeRoot, "store"), Path.Combine(probeRoot, "saves"), host, null, null);
                    var summary = (Dictionary<string, object>)inspected["summary"];
                    transfer["inspection"] = summary;
                    var afterCounts = (Dictionary<string, object>)((Dictionary<string, object>)((Dictionary<string, object>)second["countsAfter"])["packages"])[binding.PackageName];
                    transfer["packageSchemaAgrees"] = WorldSaveTransfer.PackageSchema == GeneratedWorldPackage.Schema;
                    expect((string)((Dictionary<string, object>)first["packages"])[binding.PackageName] == "copied" && (string)((Dictionary<string, object>)first["bundles"])[binding.ContentId] == "copied"
                           && (string)((Dictionary<string, object>)second["packages"])[binding.PackageName] == "reused" && (string)((Dictionary<string, object>)second["bundles"])[binding.ContentId] == "reused"
                           && (int)afterCounts["references"] == 4 && (int)afterCounts["instances"] == 1 && Equals(first["regenerated"], false) && Equals(second["indexComplete"], true) && (int)summary["savesLoadable"] == 4
                           && summary["bundles"] is IDictionary && ((IDictionary)summary["bundles"]).Contains(WorldInspector.Valid) && (bool)transfer["packageSchemaAgrees"],
                        "c3: the Save As and Ironman saves exported with their world package and data bundle (verified), imported into an empty store (copied) and again under new names (the identical world reused: 4 references, 1 campaign, the target's index complete); the inspector finds all four saves loadable; nothing regenerated");
                }
                catch (Exception error) { expect(false, "c3: export and import: " + error.GetType().Name + ": " + error.Message); }
                finally { try { if (Directory.Exists(probeRoot)) StagingCleanup.DeleteTree(probeRoot); transfer["cleaned"] = !Directory.Exists(probeRoot); } catch (Exception error) { transfer["cleanError"] = error.Message; } }
                context.Checkpoint();

                // 8. The main menu: headers read before anything is reconstructed.
                MBGameManager.EndGame();
                start = context.Seconds;
                while ((Game.Current != null || MBGameManager.Current != null) && context.Seconds - start < 120) yield return null;
                yield return Frames(30);
                var menu = new Dictionary<string, object> { ["game"] = Game.Current != null, ["attachedAfterEnd"] = WorldSaveBinding.Attached };
                context.Outputs["mainMenu"] = menu;
                var menuBindings = new[] { initial == null ? null : initial.SaveName, saveAs, autosave, ironman }.Select(n => n == null ? null : Bound(catalog, n)).ToList();
                var saveAsHeader = catalog.Read(saveAs);
                var menuCompat = saveAsHeader == null || !saveAsHeader.Readable ? new Dictionary<string, string>() : SaveBindingContract.CompatibilityFrom(saveAsHeader.Metadata);
                menu["bound"] = menuBindings.Count(b => b != null && b.Instance == binding.Instance && b.SameWorld(binding)); menu["compatibility"] = menuCompat;
                expect(Game.Current == null && !WorldSaveBinding.Attached && menuBindings.All(b => b != null && b.Instance == binding.Instance && b.SameWorld(binding)) && menuCompat.Count == SaveBindingContract.Keys.Length,
                    "at the main menu, with no game (before any scene or object is reconstructed), every save of the campaign is found bound to its exact world from its header, with the build that wrote it; the save binding left with the campaign");
                cleanup.Run(catalog);
                expect(cleanup.Clean, "cleanup: the probe's saves and the campaign's initial save are removed, and the autosave slot holds its earlier file again");
            }
            finally { cleanup.Run(catalog); }
            context.Outputs["completed"] = true;
            context.Checkpoint();
            if (st.Failures.Count > 0) throw new InvalidOperationException("Save binding expectations failed: " + String.Join("; ", st.Failures));
        }

        // A save_binding run that stopped before its cleanup left the autosave slot's earlier file beside the store, with a record: put it back
        // when its process is gone and the slot does not hold it already.
        private static List<object> RestoreLeftovers(int pid)
        {
            var rows = new List<object>();
            string directory = Path.Combine(SubModule.Store, "probe");
            if (!Directory.Exists(directory)) return rows;
            foreach (var record in Directory.GetFiles(directory, "autosave-backup-*.json"))
            {
                var row = new Dictionary<string, object> { ["record"] = Path.GetFileName(record) };
                rows.Add(row);
                try
                {
                    var data = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(record));
                    int owner = Convert.ToInt32(data["pid"], CultureInfo.InvariantCulture);
                    bool alive; try { alive = owner == pid || !System.Diagnostics.Process.GetProcessById(owner).HasExited; } catch (ArgumentException) { alive = false; }
                    if (alive) { row["skipped"] = "its process is running"; continue; }
                    string backup = Path.ChangeExtension(record, ".sav"), slot = BannerlordSaveCatalog.SavePath((string)data["slot"]);
                    if (WorldSaveTransfer.Sha256File(backup) != (string)data["sha256"]) { row["skipped"] = "the backup does not match its record"; continue; }
                    if (!File.Exists(slot) || WorldSaveTransfer.Sha256File(slot) != (string)data["sha256"])
                    {
                        File.Copy(backup, slot, true);
                        File.SetLastWriteTimeUtc(slot, new DateTime(Int64.Parse((string)data["writtenUtcTicks"], CultureInfo.InvariantCulture), DateTimeKind.Utc));
                        row["restored"] = data["slot"];
                    }
                    File.Delete(backup); File.Delete(record);
                }
                catch (Exception error) { row["error"] = error.GetType().Name + ": " + error.Message; }
            }
            return rows;
        }

        private static WorldBinding Bound(ISaveCatalog catalog, string name)
        {
            try { var h = catalog.Read(name); return h == null || !h.Readable ? null : WorldBinding.FromHeader(h.Metadata); }
            catch (Exception) { return null; }
        }

        // One save of the given kind: started, then followed until the engine is done and the journal resolved; then read back.
        private static IEnumerator Save(ProbeContext context, State st, string kind, string expectedName, Action begin)
        {
            var before = WorldSaveBinding.Last;
            int problems = WorldSaveBinding.Problems.Count;
            var prior = BannerlordSaveCatalog.Stat(expectedName);
            double start = context.Seconds;
            begin();
            context.Stage("save_binding:save:" + kind + ":" + expectedName);
            yield return null;
            while (context.Seconds - start < 120 && (WorldSaveBinding.Last == before || WorldSaveBinding.Last.State == EarlyWorldBindingStore.Intent || Campaign.Current.SaveHandler.IsSaving)) yield return null;
            var operation = WorldSaveBinding.Last;
            var row = new Dictionary<string, object> { ["kind"] = kind, ["expectedName"] = expectedName, ["seconds"] = context.Seconds - start, ["readbackSeconds"] = WorldSaveBinding.LastReadbackSeconds,
                ["problems"] = WorldSaveBinding.Problems.Skip(problems).ToList(), ["priorExisted"] = prior != null };
            st.Saves[kind] = row;
            if (operation == null || operation == before) { st.Expect(false, "a " + kind + " save was journaled"); yield break; }
            row["journal"] = operation.Report();
            var header = SaveBindingRuntime.Store.Saves.Read(operation.SaveName);
            var found = header != null && header.Readable ? WorldBinding.FromHeader(header.Metadata) : null;
            var compat = header != null && header.Readable ? SaveBindingContract.CompatibilityFrom(header.Metadata) : new Dictionary<string, string>();
            DateTime intentUtc = DateTime.Parse((string)operation.History[0]["utc"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            string path = BannerlordSaveCatalog.SavePath(operation.SaveName);
            DateTime written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            row["intentUtc"] = intentUtc.ToString("o", CultureInfo.InvariantCulture); row["fileWrittenUtc"] = written.ToString("o", CultureInfo.InvariantCulture);
            row["header"] = new Dictionary<string, object> { ["instance"] = found == null ? null : found.Instance, ["package"] = found == null ? null : found.PackageName, ["contentId"] = found == null ? null : found.ContentId,
                ["whole"] = header == null ? null : header.BodyWhole, ["compatibility"] = compat };
            var differences = SaveBindingContract.Differences(compat, WorldSaveBinding.Compatibility());
            row["compatibilityDifferences"] = differences;
            bool ok = operation.Kind == kind && operation.SaveName == expectedName && operation.State == EarlyWorldBindingStore.Published && operation.PriorExists == (prior != null)
                && operation.History[0]["state"] as string == EarlyWorldBindingStore.Intent && intentUtc <= written
                && found != null && found.Instance == st.Binding.Instance && found.SameWorld(st.Binding) && header.BodyWhole == true && differences.Count == 0 && ((List<string>)row["problems"]).Count == 0;
            st.Expect(ok, "a " + kind + " save (" + expectedName + (prior != null ? ", over an earlier file" : "") + ") is journaled before the engine writes it, read back bound to this campaign instance and its exact world (package, manifest, content id), whole, with the build's compatibility keys, and published");
        }

        private static Dictionary<string, object> Strip(Dictionary<string, object> report) { return report.Where(k => k.Key != "export").ToDictionary(k => k.Key, k => k.Value); }

        private static string TreeHash(string root)
        {
            if (!Directory.Exists(root)) return "absent";
            var text = new StringBuilder();
            foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
                text.Append(f.Substring(root.Length)).Append('=').Append(WorldSaveTransfer.Sha256File(f)).Append('\n');
            using (var sha = SHA256.Create()) return String.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }

        private static IEnumerator Frames(int count) { for (int i = 0; i < count; i++) yield return null; }
    }
}
