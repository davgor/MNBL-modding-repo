using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.ComponentInterfaces;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.CampaignSystem.Map.DistanceCache;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.ModuleManager;
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.Surface
{
    // MMR-41: a world's navigation caches (the installed SandBoxNavigationCache for Default, Naval and All: settlement-to-settlement
    // distances and land ratios, fortification neighbours, each navmesh face's closest settlement entrance) are generated once per key
    // (NavigationCacheKey) with the installed mechanism (GenerateCacheData, which R03/MMR-17 proved on generated worlds), serialized with the
    // installed format (NavigationCache.Serialize) into an owned staging folder, and published whole under <store>/navigation-caches/<key sha>.
    // A later campaign map with the same key reads them back (NavigationCache.Deserialize) instead of regenerating; anything else regenerates
    // and republishes. Never read: the installed caches (ModuleData/DistanceCaches; SettlementPositionScript is not in a generated scene and
    // GetSettlementsDistanceCacheFileForCapability is never called). A published cache is reused only when NavigationCacheStore.Check finds
    // nothing wrong: the key text and hash, each file's hash, the native scene XML and navigation mesh CRCs written in each file (the engine's
    // own values, which the installed Deserialize ignores), the settlement ids and the face range. A missing or refused cache is regenerated
    // and the reason recorded; a deleted cache is never counted as a reuse.
    internal static class WorldNavigationCache
    {
        public const string Adapter = "mmr41/1";
        public static readonly MobileParty.NavigationType[] Types = { MobileParty.NavigationType.Default, MobileParty.NavigationType.Naval, MobileParty.NavigationType.All };
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static string Root() { return Path.Combine(SubModule.Store, "navigation-caches"); }

        // Registers the three caches on the campaign's distance model (whose cache dictionary must be empty) and returns the record.
        // options (the probe's, through the support report): "verify" also generates fresh caches after a reuse and compares them with the
        // published files and the deserialized caches; "fault" = "corrupt" damages the published All file before the check.
        public static Dictionary<string, object> Acquire(GeneratedMapScene map, string sceneName, string worldId, MapDistanceModel distance, IDictionary registered,
                                                         Action<string> trace, IDictionary<string, object> options)
        {
            var record = new Dictionary<string, object> { ["adapter"] = Adapter, ["root"] = Root() };
            var watch = Stopwatch.StartNew();
            var caches = Types.ToDictionary(t => t, t => new SandBoxNavigationCache(t));
            // The persisted copy is an optimization: when the key cannot be built, the caches are generated and registered as before MMR-41,
            // unpublished, and the error recorded (an exception escaping AfterLoad stops the campaign: run 30712 hung on one).
            NavigationCacheKey key; uint sceneXmlCrc, navigationMeshCrc; int faceCount;
            try
            {
                key = BuildKey(map, sceneName, worldId, caches);
                sceneXmlCrc = ((IMapScene)map).GetSceneXmlCrc(); navigationMeshCrc = ((IMapScene)map).GetSceneNavigationMeshCrc();
                faceCount = Call<int>(caches[MobileParty.NavigationType.Default], "GetNavMeshFaceCount");
            }
            catch (Exception error)
            {
                record["keyError"] = error.GetType().Name + ": " + error.Message;
                trace("navigation_cache_key_failed:" + error.GetType().Name);
                record["caches"] = Generate(caches, distance, registered, new Dictionary<string, object>());
                record["status"] = "generated (not published: no key)";
                record["elapsedMilliseconds"] = watch.Elapsed.TotalMilliseconds;
                return record;
            }
            string sha = key.Sha256, folder = Path.Combine(Root(), sha);
            record["keySha256"] = sha; record["key"] = key.Text; record["crc"] = new Dictionary<string, object> { ["sceneXml"] = sceneXmlCrc, ["navigationMesh"] = navigationMeshCrc };
            record["faces"] = faceCount; record["settlements"] = key.SettlementIds.Count(); record["folder"] = folder;
            string fault = Option(options, "fault");
            if (fault == "corrupt" && File.Exists(Path.Combine(folder, "All.bin")))
            {
                var bytes = File.ReadAllBytes(Path.Combine(folder, "All.bin")); bytes[bytes.Length / 2] ^= 0x5a; File.WriteAllBytes(Path.Combine(folder, "All.bin"), bytes);
                record["faultInjected"] = "one byte of the published All.bin flipped before the check";
            }
            // Reuse.
            List<string> refused;
            IDictionary<string, object> manifest = null;
            if (!Directory.Exists(folder)) refused = new List<string> { "not published" };
            else
            {
                try { manifest = NavigationCacheStore.ParseManifest(File.ReadAllText(Path.Combine(folder, "manifest.json"))); }
                catch (Exception error) { manifest = null; record["manifestError"] = error.GetType().Name + ": " + error.Message; }
                refused = NavigationCacheStore.Check(manifest, key, f => File.ReadAllBytes(Path.Combine(folder, f)), sceneXmlCrc, navigationMeshCrc, faceCount);
            }
            record["refused"] = refused;
            if (refused.Count == 0)
            {
                trace("navigation_cache_reuse:" + sha.Substring(0, 12));
                var rows = new List<Dictionary<string, object>>();
                try
                {
                    // All three are read before any is registered: a file the engine cannot read after the checks leaves nothing registered.
                    foreach (var type in Types)
                    {
                        var timer = Stopwatch.StartNew();
                        caches[type].Deserialize(Path.Combine(folder, type + ".bin"));
                        rows.Add(new Dictionary<string, object> { ["navigationType"] = type.ToString(), ["source"] = "deserialized", ["deserializeMilliseconds"] = timer.Elapsed.TotalMilliseconds });
                    }
                }
                catch (Exception error)
                {
                    refused.Add("the engine could not read the published files (" + error.GetType().Name + ": " + error.Message + ")");
                    caches = Types.ToDictionary(t => t, t => new SandBoxNavigationCache(t));
                }
                if (refused.Count == 0)
                {
                    try
                    {
                        for (int i = 0; i < Types.Length; i++)
                        {
                            var type = Types[i];
                            var timer = Stopwatch.StartNew();
                            distance.RegisterDistanceCache(type, caches[type]);   // includes FinalizeInitialization
                            if (!ReferenceEquals(registered[type], caches[type])) throw new InvalidOperationException("The registered " + type + " cache is not the one read.");
                            rows[i]["registrationMilliseconds"] = timer.Elapsed.TotalMilliseconds;
                        }
                    }
                    catch (Exception error)
                    {
                        // A cache the engine read but cannot finalize: nothing of it stays registered, and the caches are generated instead.
                        refused.Add("registering the read caches failed (" + error.GetType().Name + ": " + error.Message + ")");
                        registered.Clear();
                        caches = Types.ToDictionary(t => t, t => new SandBoxNavigationCache(t));
                    }
                }
                if (refused.Count == 0)
                {
                    record["caches"] = rows;
                    record["status"] = "reused";
                    if (Option(options, "verify") == "1")
                    {
                        try { record["verification"] = Verify(folder, caches, key, sceneXmlCrc, navigationMeshCrc, faceCount, trace); }
                        catch (Exception error) { record["verification"] = new Dictionary<string, object> { ["error"] = error.GetType().Name + ": " + error.Message, ["equal"] = false }; }
                    }
                }
            }
            if (refused.Count > 0)
            {
                trace("navigation_cache_generate:" + sha.Substring(0, 12) + ":" + refused[0]);
                var durations = new Dictionary<string, object>();
                record["caches"] = Generate(caches, distance, registered, durations);
                record["status"] = manifest == null && !Directory.Exists(folder) ? "generated" : "regenerated";
                var publish = Stopwatch.StartNew();
                try
                {
                    record["published"] = Publish(folder, caches, key, sceneName, worldId, sceneXmlCrc, navigationMeshCrc, durations);
                    trace("navigation_cache_published:" + sha.Substring(0, 12));
                }
                catch (Exception error) { record["publishError"] = error.GetType().Name + ": " + error.Message; trace("navigation_cache_publish_failed:" + error.GetType().Name); }
                record["publishMilliseconds"] = publish.Elapsed.TotalMilliseconds;
            }
            if (registered.Count != Types.Length) throw new InvalidOperationException("The distance model holds " + registered.Count + " caches after registration.");
            record["elapsedMilliseconds"] = watch.Elapsed.TotalMilliseconds;
            return record;
        }

        // MMR-282: every published cache's manifest (folder name, parsed manifest; null when unreadable), for the loader's forecast.
        public static List<KeyValuePair<string, IDictionary<string, object>>> PublishedManifests()
        {
            var rows = new List<KeyValuePair<string, IDictionary<string, object>>>();
            if (!Directory.Exists(Root())) return rows;
            foreach (var dir in Directory.GetDirectories(Root()))
            {
                string name = Path.GetFileName(dir);
                if (name.IndexOf('-') >= 0) continue;   // staging, discard and verify folders
                IDictionary<string, object> manifest = null;
                try { manifest = NavigationCacheStore.ParseManifest(File.ReadAllText(Path.Combine(dir, "manifest.json"))); }
                catch (Exception) { manifest = null; }
                rows.Add(new KeyValuePair<string, IDictionary<string, object>>(name, manifest));
            }
            return rows;
        }

        // The key the campaign would give now: compared with the registered key once the campaign has started (War Sails' wrapper has set the
        // drop-off ports by then), so a position that changed after the caches were acquired is seen.
        public static string KeyNow(GeneratedMapScene map, string sceneName, string worldId)
        {
            return BuildKey(map, sceneName, worldId, Types.ToDictionary(t => t, t => new SandBoxNavigationCache(t))).Sha256;
        }

        // GenerateCacheData and registration, type by type (the R03-proven mechanism).
        private static List<Dictionary<string, object>> Generate(IDictionary<MobileParty.NavigationType, SandBoxNavigationCache> caches, MapDistanceModel distance, IDictionary registered, Dictionary<string, object> durations)
        {
            var rows = new List<Dictionary<string, object>>();
            foreach (var type in Types)
            {
                var timer = Stopwatch.StartNew();
                caches[type].GenerateCacheData();
                double generated = timer.Elapsed.TotalMilliseconds; timer.Restart();
                distance.RegisterDistanceCache(type, caches[type]);   // includes FinalizeInitialization
                if (!ReferenceEquals(registered[type], caches[type])) throw new InvalidOperationException("The registered " + type + " cache is not the one generated.");
                durations[type + "GenerateMilliseconds"] = generated;
                rows.Add(new Dictionary<string, object> { ["navigationType"] = type.ToString(), ["source"] = "generated", ["generateMilliseconds"] = generated, ["registrationMilliseconds"] = timer.Elapsed.TotalMilliseconds });
            }
            return rows;
        }

        // The key from the campaign as it is now (models, scene, positions) and the caches' own parameters.
        public static NavigationCacheKey BuildKey(GeneratedMapScene map, string sceneName, string worldId, IDictionary<MobileParty.NavigationType, SandBoxNavigationCache> caches)
        {
            var key = new NavigationCacheKey();
            var models = Campaign.Current.Models;
            Func<object, string> type = o => o == null ? "none" : o.GetType().FullName + " " + o.GetType().Assembly.GetName().Version;
            key.Set("game", CampaignRegistry.GeneratedPatches.InstalledVersions());
            key.Set("distanceModel", type(models.MapDistanceModel));
            key.Set("partyNavigationModel", type(models.PartyNavigationModel));
            key.Set("pathFindingMaxCostLimit", Convert.ToString(Campaign.PathFindingMaxCostLimit, CultureInfo.InvariantCulture));
            foreach (var kv in caches.OrderBy(k => (int)k.Key))
            {
                var c = kv.Value; string t = kv.Key.ToString();
                key.Set(t + ".excludedFaceIds", String.Join(",", (Call<int[]>(c, "GetExcludedFaceIds") ?? new int[0]).OrderBy(x => x).Select(x => x.ToString(CultureInfo.InvariantCulture))));
                key.Set(t + ".regionSwitchCosts", Call<int>(c, "GetRegionSwitchCostTo0").ToString(CultureInfo.InvariantCulture) + "," + Call<int>(c, "GetRegionSwitchCostTo1").ToString(CultureInfo.InvariantCulture));
                key.Set(t + ".agentRadius", Float(c, "AgentRadius"));
                key.Set(t + ".neighbourCostMultiplier", Float(c, "ExtraCostMultiplierForNeighborDetection"));
            }
            key.Set("worldId", worldId);
            key.Set("scene", sceneName);
            string navmesh = Path.Combine(ModuleHelper.GetModuleFullPath(GeneratedMapSceneCreator.ModuleId), "SceneObj", sceneName, "navmesh.bin");
            key.Set("navmeshSha256", NavigationCacheKey.Sha(File.ReadAllBytes(navmesh)));
            key.Set("sceneXmlCrc", ((IMapScene)map).GetSceneXmlCrc().ToString(CultureInfo.InvariantCulture));
            key.Set("navigationMeshCrc", ((IMapScene)map).GetSceneNavigationMeshCrc().ToString(CultureInfo.InvariantCulture));
            // MMR-64: the periodic coordinate identity (its frame, rules and dimensions) and the distance model's version; a planar world says so.
            var periodic = PeriodicWorld.Current;
            key.Set("periodic", periodic == null ? "planar" : PeriodicWorldCoordinates.PolicyVersion + " " + periodic.Coordinates.Identity);
            key.Set("distanceVersion", PeriodicDistanceModel.Version);
            key.Set("adapter", Adapter);
            // This module's code shapes the content (the periodic path queries GeneratedMapScene answers, the patched closest-settlement order).
            key.Set("moduleSha256", ModuleSha256());
            key.Set("schema", NavigationCacheStore.Schema + " " + NavigationCacheStore.Version);
            // Not Settlement.All's order. It decides only which of two equidistant entrances the face cache keeps, and the files name
            // settlements by id. A new game registers the settlements that heroes, kingdoms and factions reference first (presumed
            // objects); a load replays only the Settlements document, so a world's first load would always miss the caches its start
            // published and rebuild them (one-shot run 35948: 397 s). Reusing them keeps the caches the campaign played with before the save.
            foreach (var s in Settlement.All.OrderBy(s => s.StringId, StringComparer.Ordinal))
            {
                string kind = s.IsTown ? "town" : s.IsCastle ? "castle" : s.IsVillage ? "village" : s.IsHideout ? "hideout" : "other";
                key.Settlement(s.StringId, kind + (s.HasPort ? "+port" : ""), new[] { s.Position.X, s.Position.Y }, new[] { s.GatePosition.X, s.GatePosition.Y },
                    s.HasPort ? new[] { s.PortPosition.X, s.PortPosition.Y } : null);
            }
            return key;
        }

        // The caches' protected accessors (the values GenerateCacheData uses).
        private static T Call<T>(object cache, string name)
        {
            for (var t = cache.GetType(); t != null; t = t.BaseType)
            {
                var m = t.GetMethod(name, Members, null, Type.EmptyTypes, null);
                if (m != null) return (T)m.Invoke(cache, null);
            }
            throw new MissingMethodException(cache.GetType().FullName, name);
        }

        private static string Float(object cache, string name)
        {
            for (var t = cache.GetType(); t != null; t = t.BaseType)
            {
                // Instance, static or constant (the installed cache declares these as constants).
                var f = t.GetField(name, Members | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                if (f != null) return Convert.ToSingle(f.IsLiteral ? f.GetRawConstantValue() : f.GetValue(f.IsStatic ? null : cache), CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
            }
            throw new MissingFieldException(cache.GetType().FullName, name);
        }

        private static string Option(IDictionary<string, object> options, string name)
        {
            object v; return options != null && options.TryGetValue(name, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        }

        // Serialize into a fresh staging folder, write the manifest last (durably), then move the folder into place whole (a refused cache in
        // the way is moved aside and deleted first).
        private static Dictionary<string, object> Publish(string folder, IDictionary<MobileParty.NavigationType, SandBoxNavigationCache> caches, NavigationCacheKey key, string sceneName,
                                                          string worldId, uint sceneXmlCrc, uint navigationMeshCrc, Dictionary<string, object> durations)
        {
            string root = Root(), staging = Path.Combine(root, "staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                var timer = Stopwatch.StartNew();
                foreach (var type in Types)
                {
                    string path = Path.Combine(staging, type + ".bin");
                    caches[type].Serialize(path);
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) stream.Flush(true);
                }
                durations["serializeMilliseconds"] = timer.Elapsed.TotalMilliseconds;
                var manifest = NavigationCacheStore.Manifest(key, sceneName, worldId, sceneXmlCrc, navigationMeshCrc, f => File.ReadAllBytes(Path.Combine(staging, f)), durations, ModuleSha256());
                Persistence.DurableFile.WriteAllText(Path.Combine(staging, "manifest.json"), new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(manifest));
                bool replaced = false;
                if (Directory.Exists(folder))
                {
                    string aside = Path.Combine(root, "discard-" + Guid.NewGuid().ToString("N"));
                    Directory.Move(folder, aside); Directory.Delete(aside, true); replaced = true;
                }
                Directory.Move(staging, folder);
                return new Dictionary<string, object> { ["folder"] = folder, ["replacedRefused"] = replaced, ["files"] = manifest["files"] };
            }
            catch { try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (IOException) { } throw; }
        }

        // After a reuse: the deserialized caches serialized again, and caches generated fresh now, each compared in content with the published
        // files (the reuse is the cache this world's navigation and positions produce).
        private static Dictionary<string, object> Verify(string folder, IDictionary<MobileParty.NavigationType, SandBoxNavigationCache> caches, NavigationCacheKey key,
                                                         uint sceneXmlCrc, uint navigationMeshCrc, int faceCount, Action<string> trace)
        {
            var result = new Dictionary<string, object>(); var differences = new List<string>();
            string scratch = Path.Combine(Root(), "verify-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                for (int i = 0; i < Types.Length; i++)
                {
                    var type = Types[i]; int value = NavigationCacheStore.TypeValues[i];
                    var published = NavigationCacheFile.Read(File.ReadAllBytes(Path.Combine(folder, type + ".bin")), value);
                    string again = Path.Combine(scratch, type + ".roundtrip.bin");
                    caches[type].Serialize(again);
                    var roundTrip = NavigationCacheFile.Read(File.ReadAllBytes(again), value);
                    trace("navigation_cache_verify_generate:" + type);
                    var fresh = new SandBoxNavigationCache(type);
                    var timer = Stopwatch.StartNew();
                    fresh.GenerateCacheData();
                    double ms = timer.Elapsed.TotalMilliseconds;
                    string generated = Path.Combine(scratch, type + ".fresh.bin");
                    fresh.Serialize(generated);
                    var freshFile = NavigationCacheFile.Read(File.ReadAllBytes(generated), value);
                    var d1 = NavigationCacheFile.Compare(published, roundTrip); var d2 = NavigationCacheFile.Compare(published, freshFile);
                    differences.AddRange(d1.Take(5).Select(x => type + " round trip: " + x)); differences.AddRange(d2.Take(5).Select(x => type + " fresh: " + x));
                    result[type.ToString()] = new Dictionary<string, object> { ["pairs"] = published.Pairs, ["neighbourPairs"] = published.NeighbourPairs, ["faces"] = published.Faces,
                        ["roundTripDifferences"] = d1.Count, ["freshDifferences"] = d2.Count, ["freshGenerateMilliseconds"] = ms };
                }
            }
            finally { try { Directory.Delete(scratch, true); } catch (IOException) { } }
            result["differences"] = differences; result["equal"] = differences.Count == 0;
            return result;
        }

        private static string moduleSha;
        // MMR-282: also what the loader's forecast compares a published cache's manifest with (CacheForecast).
        internal static string ModuleSha256()
        {
            if (moduleSha == null) moduleSha = NavigationCacheKey.Sha(File.ReadAllBytes(typeof(WorldNavigationCache).Assembly.Location));
            return moduleSha;
        }
    }
}
