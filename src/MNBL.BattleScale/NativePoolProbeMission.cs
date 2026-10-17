using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.MissionViews;
using TaleWorlds.ObjectSystem;
using MNBL.BattleScale.Core;

namespace MNBL.BattleScale
{
    // Explicit native-pools one-shot only. Never patches or deploys native code.
    public sealed class NativePoolProbeMission : MissionView
    {
        private const int Capacity = 4080;
        private static NativeProbeWorkload configuredWorkload = new NativeProbeWorkload("infantry", 120, 3);
        private readonly NativeProbeWorkload workload = configuredWorkload;
        public static void ConfigureWorkload(string profile, int sustainedSeconds, int reuseCycles)
        {
            if (pendingProbe != null) throw new InvalidOperationException("Cannot change an active native workload.");
            configuredWorkload = new NativeProbeWorkload(profile, sustainedSeconds, reuseCycles);
        }
        private sealed class Binding
        {
            internal int Position;
            internal int Index;
            internal Vec3 CombatPosition;
            internal float CombatHealth;
            internal bool IsMount;
        }
        private readonly Dictionary<Agent, Binding> agents = new Dictionary<Agent, Binding>();
        private readonly List<Agent> allCreatedAgents = new List<Agent>();
        private readonly HashSet<int> deletedIndices = new HashSet<int>();
        private readonly HashSet<Agent> retiring = new HashSet<Agent>();
        private readonly Queue<int> freePositions = new Queue<int>();
        private readonly Stopwatch wall = Stopwatch.StartNew();
        private int[] targets;
        private readonly List<object> checkpoints = new List<object>();
        private Team[] teams;
        private BasicCharacterObject troop;
        private Equipment equipment;
        private FormationClass formationClass;
        private Vec3 origin;
        private int stage, nextPosition, spawned, deleted, reused, peak, maxIndex;
        private float elapsed, stageTime, reportTime;
        private float combatOrderTime;
        private int combatOrderRounds, combatMovedAgents, combatDamagedAgents;
        private bool recycling, recycled, awaitingRefill, combat, finished, failed;
        private bool fullPoolVerified, fullPoolRefillVerified, managedCapacityGuardVerified;
        private bool exhaustionVerified, endCallback, stateFinalizedCallback, behaviorRemovedCallback, screenFinalizedCallback;
        private bool quitRequested;
        private int exhaustionReturnValue, exhaustionAttempts, currentEntities;
        private string exhaustionSnapshotBefore, exhaustionSnapshotAfter;
        private int[] exhaustionPoolCounts;
        private readonly NativePoolProbeCompletion completion = new NativePoolProbeCompletion();
        private Mission retainedMission;
        private Agent[] teardownAgents = new Agent[0];
        private static NativePoolProbeMission pendingProbe;
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int FullPrimaryPoolAllocator(IntPtr pool);
        private static readonly int[] poolIndexTables = { 0xc56750, 0x18ac140, 0x1adbe30, 0x1da3198, 0x206a500 };
        private const string FullAllocatorPathHex = "405356574883ec204533c9488bf9418bd94863f34869c6600c000044394c08107c17ffc381fbf00f00007ce5b8ffffffff4883c4205f5e5bc3";
        private bool unexpectedDeletion;
        private int combatCasualties, validVisuals, inCameraFrustum, visibilityChecksPassed;
        private float visibilityTime;
        private Camera camera;
        private string error;
        private static string nativeHash;
        private static IntPtr nativeBase;
        private static long initialTaskPool, initialWorkerBegin, initialWorkerEnd;
        private static int workerCount;
        private static readonly string[] approvedModules = { "Native", "SandBoxCore", "CustomBattle", "MNBL.BattleScale" };
        private static readonly Dictionary<string, FileStream> providerFiles = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        private static string profileHash;
        // Bind the CLR's shared anonymous DynamicMethod host by object identity,
        // not an assembly name that an unrelated provider could imitate.
        private static readonly System.Reflection.Assembly anonymousMethodHost =
            new System.Reflection.Emit.DynamicMethod("NativePoolProbeHostIdentity", typeof(void), Type.EmptyTypes).Module.Assembly;
        private float providerCheckTime;
        private int completedReuseCycles, cycleDeletedStart, cycleReusedStart;
        private int peakHumans, peakMounts, mountedPairsVerified, mountedPairsAtCombat, minimumActiveCombatHumans = int.MaxValue;
        private long missileShots, missileHits;
        private readonly HashSet<int> missileShooters = new HashSet<int>();
        private readonly List<object> reuseCheckpoints = new List<object>();
        private readonly List<object> exhaustionCheckpoints = new List<object>();
        private readonly List<object> resourceSamples = new List<object>();
        private readonly NativeFrameSamples frameSamples = new NativeFrameSamples();
        private readonly NativeResourceCoverage resourceCoverage = new NativeResourceCoverage();
        private int liveEntitiesAtTeardownStart;
        private double combatStartedWall, combatEndedWall = -1, lastResourceWall = -1, lastShotWall = -1;
        private double lastCpuSeconds = -1, lastCpuWall = -1;
        private bool sustainedDurationVerified, workloadUseVerified, publicSpawnAgentOverflowTested;
        private int publicOverflowAttempts;
        private string publicOverflowSnapshotBefore, publicOverflowSnapshotAfter;
        private string resourceSamplingError;

        private static string HashStream(Stream stream)
        {
            stream.Position = 0;
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static bool UnderDirectory(string path, string directory)
        {
            return path.StartsWith(System.IO.Path.GetFullPath(directory).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }

        private static HashSet<Assembly> XmlSerializerRuntimeAssemblies()
        {
            // Follow the actual System.Xml cache objects, never trust the public
            // Microsoft.GeneratedCode name. These private fields were inspected
            // in the installed .NET Framework; missing fields yield no allowance.
            var result = new HashSet<Assembly>();
            Type serializer = typeof(System.Xml.Serialization.XmlSerializer);
            FieldInfo cacheField = serializer.GetField("cache", BindingFlags.Static | BindingFlags.NonPublic);
            object cache = cacheField == null ? null : cacheField.GetValue(null);
            if (cache == null || cache.GetType().Assembly != serializer.Assembly) return result;
            FieldInfo tableField = cache.GetType().GetField("cache", BindingFlags.Instance | BindingFlags.NonPublic);
            var table = tableField == null ? null : tableField.GetValue(cache) as System.Collections.Hashtable;
            if (table == null) return result;
            foreach (object entry in table.Values)
            {
                if (entry == null || entry.GetType().Assembly != serializer.Assembly
                    || entry.GetType().FullName != "System.Xml.Serialization.TempAssembly") continue;
                FieldInfo assemblyField = entry.GetType().GetField("assembly", BindingFlags.Instance | BindingFlags.NonPublic);
                var assembly = assemblyField == null ? null : assemblyField.GetValue(entry) as Assembly;
                if (assembly != null) result.Add(assembly);
            }
            return result;
        }

        private static void ValidateLoadedProviders()
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string[] systemDirectories = { "System32", "SysWOW64", "WinSxS", "Microsoft.NET", "assembly" };
            var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<Assembly> serializerAssemblies = XmlSerializerRuntimeAssemblies();
            Action<string> check = file => {
                string full = System.IO.Path.GetFullPath(file);
                if (!systemDirectories.Any(d => UnderDirectory(full, System.IO.Path.Combine(windows, d))) && !providerFiles.ContainsKey(full))
                    unknown.Add(full);
            };
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules) check(module.FileName);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (ReferenceEquals(assembly, anonymousMethodHost) || serializerAssemblies.Contains(assembly)) continue;
                if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
                {
                    unknown.Add("dynamic:" + assembly.FullName + " types=" + string.Join(",", assembly.GetTypes().Take(12).Select(t => t.FullName)));
                    continue;
                }
                check(assembly.Location);
            }
            if (unknown.Count != 0)
                throw new InvalidOperationException("Unapproved loaded providers in isolated probe: " + string.Join("; ", unknown));
        }

        private static void ValidateIsolatedProviders(Dictionary<string, object> profile, string nativePath, string currentProfileHash)
        {
            string expectedModules = "_MODULES_*" + string.Join("*", approvedModules) + "*_MODULES_";
            string[] command = Environment.GetCommandLineArgs();
            string[] moduleArguments = command.Where(a => a.IndexOf("_MODULES_", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            if (moduleArguments.Length != 1 || moduleArguments[0] != expectedModules
                || command.Count(a => string.Equals(a, "/singleplayer", StringComparison.OrdinalIgnoreCase)) != 1)
                throw new InvalidOperationException("Probe requires exactly Native, SandBoxCore, CustomBattle and MNBL.BattleScale in the singleplayer module argument.");
            var moduleIds = ((System.Collections.IEnumerable)profile["allowedModuleIds"]).Cast<object>().Select(Convert.ToString).ToArray();
            if (!moduleIds.SequenceEqual(approvedModules) || Convert.ToString(profile["requiredModuleArgument"]) != expectedModules)
                throw new InvalidOperationException("Unapproved module selection in probe profile.");
            string installation = System.IO.Path.GetFullPath(Convert.ToString(profile["installationRoot"]));
            string expectedNative = System.IO.Path.Combine(installation, "bin", "Win64_Shipping_Client", "TaleWorlds.Native.dll");
            if (!string.Equals(System.IO.Path.GetFullPath(nativePath), expectedNative, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Loaded native engine is outside the bound installation.");
            if (profileHash != null && profileHash != currentProfileHash)
                throw new InvalidOperationException("Isolated test profile changed after validation.");
            if (providerFiles.Count == 0)
            {
                try
                {
                    foreach (object value in (System.Collections.IEnumerable)profile["trustedFiles"])
                    {
                        var item = (Dictionary<string, object>)value;
                        string relative = Convert.ToString(item["relativePath"]);
                        if (System.IO.Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(x => x == ".." || x.Length == 0))
                            throw new InvalidOperationException("Invalid provider path in probe profile.");
                        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(installation, relative.Replace('/', '\\')));
                        if (!UnderDirectory(full, installation)) throw new InvalidOperationException("Provider escaped the bound installation.");
                        for (string parent = full; parent != null && UnderDirectory(parent, installation); parent = System.IO.Path.GetDirectoryName(parent))
                            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                                throw new InvalidOperationException("Linked provider paths are outside the isolated profile.");
                        // Keep read handles open without write/delete sharing through the
                        // process lifetime. Ordinary file replacement cannot invalidate hashes.
                        var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
                        try
                        {
                            if (HashStream(stream) != Convert.ToString(item["sha256"]))
                                throw new InvalidOperationException("Provider changed since test preparation: " + relative);
                            providerFiles.Add(full, stream);
                        }
                        catch { stream.Dispose(); throw; }
                    }
                    // Exact signed platform infrastructure identities are separately
                    // reviewed and pinned; no directory-wide third-party allowance.
                    if (profile.ContainsKey("trustedExternalFiles"))
                    foreach (object value in (System.Collections.IEnumerable)profile["trustedExternalFiles"])
                    {
                        var item = (Dictionary<string, object>)value;
                        string full = System.IO.Path.GetFullPath(Convert.ToString(item["path"]));
                        string[] paths = {
                            @"C:\Program Files (x86)\Steam\steamclient64.dll", @"C:\Program Files (x86)\Steam\tier0_s64.dll",
                            @"C:\Program Files (x86)\Steam\vstdlib_s64.dll", @"C:\Program Files (x86)\Steam\gameoverlayrenderer64.dll",
                            @"C:\ProgramData\NVIDIA\NGX\models\dlss\versions\131529\files\160_B9D3D54.bin",
                            @"C:\Program Files\NVIDIA Corporation\NvTelemetry\NvTelemetryAPI64.dll",
                            @"C:\Program Files\NVIDIA Corporation\NvTelemetry\NvTelemetryBridge64.dll" };
                        if (!paths.Any(p => string.Equals(full, p, StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidOperationException("Unapproved external platform provider path.");
                        for (string parent = full; parent != null; parent = System.IO.Path.GetDirectoryName(parent))
                            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                                throw new InvalidOperationException("Linked platform provider path.");
                        var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
                        try
                        {
                            if (HashStream(stream) != Convert.ToString(item["sha256"]))
                                throw new InvalidOperationException("Platform provider changed after review: " + full);
                            providerFiles.Add(full, stream);
                        }
                        catch { stream.Dispose(); throw; }
                    }
                    if (!providerFiles.ContainsKey(expectedNative) || !providerFiles.ContainsKey(System.IO.Path.GetFullPath(typeof(NativePoolProbeMission).Assembly.Location)))
                        throw new InvalidOperationException("Profile omits required native or probe provider identity.");
                    profileHash = currentProfileHash;
                }
                catch
                {
                    foreach (var stream in providerFiles.Values) stream.Dispose();
                    providerFiles.Clear();
                    throw;
                }
            }
            ValidateLoadedProviders();
        }

        private static void ValidateWorkerBound(bool remember)
        {
            // Exact-profile ABI from REA062140/0627f0. Read-only; reject changed
            // task-pool storage before advancing this isolated probe.
            long pool = Marshal.ReadIntPtr(IntPtr.Add(nativeBase, 0xd92ce8)).ToInt64();
            if (pool <= 0) throw new InvalidOperationException("Native task pool was not initialized.");
            long begin = Marshal.ReadIntPtr(new IntPtr(pool + 8)).ToInt64();
            long end = Marshal.ReadIntPtr(new IntPtr(pool + 0x10)).ToInt64();
            long capacity = Marshal.ReadIntPtr(new IntPtr(pool + 0x18)).ToInt64();
            if (Marshal.ReadInt32(new IntPtr(pool + 0xa4c)) != 16 || begin <= 0 || end < begin || capacity < end
                || ((begin | end | capacity) & 7) != 0 || end - begin < 24 || end - begin > 128)
                throw new InvalidOperationException("Probe requires /maxThreadCount 16 and3..16 actual primary task entries.");
            if (!remember && (pool != initialTaskPool || begin != initialWorkerBegin || end != initialWorkerEnd))
                throw new InvalidOperationException("Native task pool changed during the probe.");
            initialTaskPool = pool; initialWorkerBegin = begin; initialWorkerEnd = end;
            workerCount = checked((int)((end - begin) / 8));
        }

        internal static void ValidateProfile()
        {
            if (string.IsNullOrEmpty(SubModule.NativeProbeRunId))
                throw new InvalidOperationException("Native probe run identity is missing.");
            if (MBCommon.CurrentGameType != MBCommon.GameType.Single)
                throw new InvalidOperationException("Native pool test requires ordinary Single mode0.");
            if (MissionState.RecordMission)
                throw new InvalidOperationException("Turn off mission recording before the native pool test.");
            string profilePath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(NativePoolProbeMission).Assembly.Location), "native-pool-test-profile.json");
            string profileText = File.ReadAllText(profilePath);
            string currentProfileHash;
            using (var bytes = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(profileText))) currentProfileHash = HashStream(bytes);
            var profile = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(profileText);
            if (Convert.ToInt32(profile["schemaVersion"]) != 3 || !profile.ContainsKey("publicSpawnAdmission") || !Convert.ToBoolean(profile["publicSpawnAdmission"])
                || !profile.ContainsKey("staticReady") || !Convert.ToBoolean(profile["staticReady"]) || Convert.ToInt32(profile["capacity"]) != Capacity)
                throw new InvalidOperationException("Native pool test profile is not statically ready.");
            using (var stream = File.OpenRead(typeof(Mission).Assembly.Location))
                if (HashStream(stream) != Convert.ToString(profile["managedEngineSha256"]))
                    throw new InvalidOperationException("Loaded managed engine does not match the admission patch profile.");
            if (profileHash == null)
                ConfigureWorkload(Convert.ToString(profile["workload"]), Convert.ToInt32(profile["sustainedSeconds"]), Convert.ToInt32(profile["reuseCycles"]));
            string probeHash;
            using (var stream = File.OpenRead(typeof(NativePoolProbeMission).Assembly.Location))
            using (var sha = SHA256.Create())
                probeHash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            if (probeHash != Convert.ToString(profile["probeModuleSha256"]))
                throw new InvalidOperationException("Probe module does not match the prepared test profile.");
            // Check the actual loaded module, including its path, rather than
            // guessing which copy the Windows loader selected.
            var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                .Where(m => string.Equals(m.ModuleName, "TaleWorlds.Native.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (modules.Length != 1) throw new InvalidOperationException("Expected exactly one loaded native engine module.");
            using (var stream = File.OpenRead(modules[0].FileName))
            using (var sha = SHA256.Create())
                nativeHash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            if (nativeHash != Convert.ToString(profile["patchedSha256"]))
                throw new InvalidOperationException("Loaded native engine does not match the prepared test profile.");
            ValidateIsolatedProviders(profile, modules[0].FileName, currentProfileHash);
            if (Convert.ToInt32(profile["configuredMaxThreadCount"]) != 16)
                throw new InvalidOperationException("Native probe profile lacks the audited worker bound.");
            nativeBase = modules[0].BaseAddress;
            ValidateWorkerBound(true);
            if (DefaultBattleMissionAgentSpawnLogic.MaxNumberOfAgentsForMission != Capacity)
                throw new InvalidOperationException("Native capacity cache is not4080; refusing the test.");
            NativeSpawnAdmission.EnableForValidatedProfile();
        }

        public override void AfterStart()
        {
            try
            {
                if (pendingProbe != null) throw new InvalidOperationException("Another native pool probe is already active.");
                retainedMission = Mission;
                pendingProbe = this;
                ValidateProfile();
                var landmark = Mission.Scene.FindEntityWithTag("attacker_mid");
                if (landmark == null) throw new InvalidOperationException("Benchmark scene landmark missing.");
                origin = landmark.GetGlobalFrame().origin;
                targets = workload.Targets;
                formationClass = workload.Profile == "cavalry" ? FormationClass.Cavalry : workload.Profile == "ranged" ? FormationClass.Ranged : FormationClass.Infantry;
                troop = MBObjectManager.Instance.GetObject<BasicCharacterObject>(workload.TroopId);
                if (troop == null || troop.FirstBattleEquipment == null) throw new InvalidOperationException("Workload troop data missing: " + workload.TroopId);
                equipment = troop.FirstBattleEquipment.Clone(false);
                if (workload.Profile == "cavalry" && (equipment.Horse.IsEmpty || !equipment.Horse.Item.HasHorseComponent
                    || !equipment.Horse.Item.HorseComponent.IsRideable))
                    throw new InvalidOperationException("Cavalry equipment lacks a rideable horse.");
                if (workload.Profile == "ranged" && (!equipment.HasWeaponOfClass(WeaponClass.Bow) || !equipment.HasWeaponOfClass(WeaponClass.Arrow)))
                    throw new InvalidOperationException("Ranged equipment must contain a bow and arrows.");
                teams = new[] {
                    Mission.Teams.Add(BattleSideEnum.Attacker, 0xff3355bb, 0xffeeeeee, new Banner("11.4.124.4345.4345.768.768.1.0.0.163.0.5.512.512.769.764.1.0.0")),
                    Mission.Teams.Add(BattleSideEnum.Defender, 0xffbb4433, 0xffeeeeee, new Banner("11.45.126.4345.4345.768.768.1.0.0.462.0.13.512.512.769.764.1.0.0"))
                };
                teams[0].SetIsEnemyOf(teams[1], false);
                Mission.Teams.Player = teams[0];
                Mission.SetMissionMode(MissionMode.Battle, true);
                Mission.SetMissionCorpseFadeOutTimeInSeconds(5);
                if (Mission.AllAgents.Count != 0) throw new InvalidOperationException("Probe requires an empty isolated mission.");
                Report("running");
            }
            catch (Exception exception) { Fail(exception); }
        }

        private bool TrySpawnOne()
        {
            // The shipping CreateAgent path indexes -1 after allocator exhaustion.
            // This guard must precede build-data creation and every native spawn.
            if (agents.Count != Mission.AllAgents.Count || agents.Count > Capacity)
                throw new InvalidOperationException("Controlled population diverged before spawn.");
            if (!workload.CanSpawn(agents.Count, Capacity)) return false;
            int position = freePositions.Count > 0 ? freePositions.Dequeue() : nextPosition++;
            int side = position % 2;
            Vec3 point = origin;
            float spacing = workload.Profile == "cavalry" ? 3.5f : 1.8f;
            point.x += ((position / 2) % 48 - 24) * spacing;
            point.y += (side == 0 ? -1 : 1) * ((workload.Profile == "ranged" ? 45 : 10) + ((position / 2) / 48) * spacing);
            point.z = Mission.Scene.GetGroundHeightAtPosition(point) + 0.1f;
            var controller = position == 0 ? AgentControllerType.Player : AgentControllerType.AI;
            var data = new AgentBuildData(troop).Team(teams[side]).Equipment(equipment.Clone(false)).FixedEquipment(true)
                .NoHorses(workload.Profile != "cavalry").Controller(controller)
                .InitialPosition(point).InitialDirection(new Vec2(0, side == 0 ? 1 : -1)).EquipmentSeed(position);
            if (controller == AgentControllerType.AI) data.Formation(teams[side].GetFormation(formationClass));
            int before = Mission.AllAgents.Count;
            Agent agent = Mission.SpawnAgent(data, false);
            if (agent == null) throw new InvalidOperationException("Native spawn returned null at population" + agents.Count);
            RegisterOwnedAgent(agent, position, false);
            if (workload.Profile == "cavalry")
            {
                Agent mount = agent.MountAgent;
                if (mount == null || !mount.IsMount || mount.RiderAgent != agent || !agent.HasMount)
                    throw new InvalidOperationException("Cavalry spawn did not create an actual linked rider and horse.");
                RegisterOwnedAgent(mount, position, true);
                mountedPairsVerified++;
            }
            else if (agent.MountAgent != null) throw new InvalidOperationException("Foot workload unexpectedly spawned a horse.");
            if (Mission.AllAgents.Count != before + workload.EntityCost || agents.Count != Mission.AllAgents.Count)
                throw new InvalidOperationException("Spawn entity cost differs from the reserved rider/horse budget.");
            peakHumans = Math.Max(peakHumans, agents.Count(p => !p.Value.IsMount));
            peakMounts = Math.Max(peakMounts, agents.Count(p => p.Value.IsMount));
            return true;
        }

        private void RegisterOwnedAgent(Agent agent, int position, bool isMount)
        {
            if (agent.Index < 0 || agent.Index >= Capacity || agents.Values.Any(a => a.Index == agent.Index))
                throw new InvalidOperationException("Native allocator returned an invalid or duplicate live index.");
            if (deletedIndices.Remove(agent.Index)) reused++;
            maxIndex = Math.Max(maxIndex, agent.Index);
            agents.Add(agent, new Binding { Position = position, Index = agent.Index, IsMount = isMount });
            allCreatedAgents.Add(agent);
            spawned++;
        }

        public override void OnMissionTick(float dt)
        {
            if (failed || finished || teams == null) return;
            try
            {
                elapsed += dt; stageTime += dt; reportTime += dt;
                if (unexpectedDeletion) throw new InvalidOperationException("An unrequested agent deletion occurred during the allocation stage.");
                if (wall.Elapsed.TotalSeconds > workload.TimeoutSeconds) throw new TimeoutException("Native workload exceeded its wall-clock timeout.");
                if (MBCommon.CurrentGameType != MBCommon.GameType.Single) throw new InvalidOperationException("Game mode changed during probe.");
                ValidateWorkerBound(false);
                providerCheckTime += dt;
                if (providerCheckTime >= 1) { providerCheckTime = 0; ValidateLoadedProviders(); }
                if (Mission.AllAgents.Count > Capacity) throw new InvalidOperationException("Physical pool budget exceeded.");
                SampleResources(false);
                if (recycling)
                {
                    if (deleted - cycleDeletedStart < 64)
                    {
                        if (stageTime > 90) throw new TimeoutException("Deletion callbacks did not release64 physical slots.");
                        if (reportTime >= 5) { reportTime = 0; Report("running"); }
                        return;
                    }
                    if (deleted - cycleDeletedStart != 64 || retiring.Count != 0)
                        throw new InvalidOperationException("Recycle released an unexpected number of physical slots.");
                    recycling = false; stageTime = 0;
                }
                if (!combat)
                {
                    int count = Math.Min(16, (targets[stage] - Mission.AllAgents.Count) / workload.EntityCost);
                    for (int i = 0; i < count; i++)
                        if (!TrySpawnOne()) throw new InvalidOperationException("Spawn guard rejected a below-capacity request.");
                    currentEntities = Mission.AllAgents.Count;
                    peak = Math.Max(peak, currentEntities);
                    if (currentEntities < targets[stage]) stageTime = 0;
                    else if (stageTime >= 10)
                    {
                        if (agents.Count != currentEntities || agents.Keys.Any(a => !a.IsActive()))
                            throw new InvalidOperationException("Agent population changed during the controlled allocation stage.");
                        int secondaryCount, secondaryHighWater;
                        ReadSecondaryPool(out secondaryCount, out secondaryHighWater);
                        checkpoints.Add(new { target = targets[stage], entities = currentEntities,
                            humans = agents.Count(p => !p.Value.IsMount), mounts = agents.Count(p => p.Value.IsMount),
                            maxIndex, seconds = elapsed, wallSeconds = wall.Elapsed.TotalSeconds, secondaryCount, secondaryHighWater });
                        if (targets[stage] != Capacity) { stage++; stageTime = 0; }
                        else
                        {
                            VerifyFullPopulation();
                            fullPoolVerified = true;
                            if (awaitingRefill)
                            {
                                if (deleted-cycleDeletedStart != 64 || reused-cycleReusedStart != 64
                                    || deletedIndices.Count != 0 || freePositions.Count != 0 || retiring.Count != 0)
                                    throw new InvalidOperationException("Refill did not reuse precisely the64 released native slots.");
                                completedReuseCycles++; awaitingRefill = false;
                                reuseCheckpoints.Add(new { cycle = completedReuseCycles, releasedSlots = 64, reusedSlots = 64,
                                    humans = agents.Count(p => !p.Value.IsMount), mounts = agents.Count(p => p.Value.IsMount),
                                    wallSeconds = wall.Elapsed.TotalSeconds });
                            }
                            managedCapacityGuardVerified = !TrySpawnOne();
                            if (!managedCapacityGuardVerified) throw new InvalidOperationException("Managed capacity guard permitted an overflow spawn.");
                            VerifyAllocatorExhaustion();
                            VerifyPublicSpawnOverflow();
                            if (completedReuseCycles < workload.ReuseCycles) BeginRecycle();
                            else
                            {
                                if (peak != Capacity || maxIndex != Capacity-1 || reused != 64*workload.ReuseCycles)
                                    throw new InvalidOperationException("Expanded allocation/index reuse acceptance failed.");
                                fullPoolRefillVerified = recycled = true;
                                BeginCombat();
                            }
                        }
                        Report("running");
                    }
                }
                else
                {
                    combatOrderTime += dt;
                    if (combatOrderTime >= 1) { combatOrderTime = 0; IssueCombatOrders(); }
                    if (wall.Elapsed.TotalSeconds-combatStartedWall >= workload.SustainedSeconds)
                    {
                        if (combatCasualties == 0) throw new InvalidOperationException("Combat produced no casualty callbacks.");
                        if (workload.Profile == "ranged" && (missileShots == 0 || missileShooters.Count < 2))
                            throw new InvalidOperationException("Ranged workload did not demonstrate multiple actual shooters.");
                        if (workload.Profile == "cavalry" && mountedPairsAtCombat != Capacity/2)
                            throw new InvalidOperationException("Cavalry workload did not begin with2040 actual mounted pairs.");
                        NativeFrameSummary frames = frameSamples.Snapshot();
                        if (frames.Samples < 30 || frames.DroppedSamples != 0 || frames.MeasuredIntervalSeconds < workload.SustainedSeconds-10)
                            throw new InvalidOperationException("Sustained frame-time sample coverage is insufficient or truncated.");
                        SampleResources(true);
                        if (resourceSamplingError != null || !resourceCoverage.IsSufficient(workload.SustainedSeconds))
                            throw new InvalidOperationException("Sustained process resource sample coverage is missing, invalid, or insufficient.");
                        sustainedDurationVerified = workloadUseVerified = true;
                        BeginTeardown();
                        return;
                    }
                }
                if (reportTime >= 5) { reportTime = 0; Report("running"); }
            }
            catch (Exception exception) { Fail(exception); }
        }

        private void BeginRecycle()
        {
            cycleDeletedStart = deleted; cycleReusedStart = reused;
            var positions = new HashSet<int>(agents.Where(p => !p.Value.IsMount && p.Value.Position != 0)
                .OrderBy(p => p.Value.Position).Take(64 / workload.EntityCost).Select(p => p.Value.Position));
            Agent[] batch = agents.Where(p => positions.Contains(p.Value.Position))
                .OrderBy(p => p.Value.Position).ThenBy(p => p.Value.IsMount).Select(p => p.Key).ToArray();
            if (batch.Length != 64) throw new InvalidOperationException("Recycle selection is not64 owned physical slots.");
            recycling = awaitingRefill = true; stageTime = 0;
            foreach (Agent agent in batch) retiring.Add(agent);
            // FadeOut's second argument is fadeOutAll: false deliberately leaves
            // a rider's mount alone. Both owned members are explicitly selected.
            foreach (Agent agent in batch) if (agents.ContainsKey(agent)) agent.FadeOut(true, false);
        }

        private void BeginCombat()
        {
            CapturePoolAllocationState(NativeMissionPointer());
            combat = true; stageTime = 0; combatStartedWall = wall.Elapsed.TotalSeconds;
            lastCpuSeconds = lastCpuWall = -1;
            mountedPairsAtCombat = agents.Count(p => !p.Value.IsMount && p.Key.MountAgent != null
                && p.Key.MountAgent.RiderAgent == p.Key);
            teams[0].SetIsEnemyOf(teams[1], true);
            foreach (var pair in agents)
            {
                pair.Value.CombatPosition = pair.Key.Position;
                pair.Value.CombatHealth = pair.Key.Health;
            }
            IssueCombatOrders(); SampleResources(true);
        }

        private void IssueCombatOrders()
        {
            // These agents spent the allocation phase friendly and idle. Wake
            // their individual AI as in the previously verified layered probe.
            // Pair stable formation positions in O(n log n), avoiding all-pairs
            // nearest-target searches at this expanded population.
            foreach (Team team in teams)
            {
                team.GetFormation(formationClass).SetMovementOrder(workload.Profile == "ranged" ? MovementOrder.MovementOrderStop : MovementOrder.MovementOrderCharge);
                if (workload.Profile == "ranged") team.GetFormation(formationClass).SetFiringOrder(FiringOrder.FiringOrderFireAtWill);
            }
            var sides = new[] {
                agents.Where(p => !p.Value.IsMount && p.Value.Position % 2 == 0 && p.Key.IsActive()).OrderBy(p => p.Value.Position).ToArray(),
                agents.Where(p => !p.Value.IsMount && p.Value.Position % 2 == 1 && p.Key.IsActive()).OrderBy(p => p.Value.Position).ToArray()
            };
            for (int side = 0; side < 2; side++)
            {
                var enemies = sides[1-side];
                if (enemies.Length == 0) continue;
                for (int i = 0; i < sides[side].Length; i++)
                {
                    var pair = sides[side][i];
                    if (pair.Value.Position == 0) continue;
                    pair.Key.SetWatchState(Agent.WatchState.Alarmed);
                    if (workload.Profile == "ranged") pair.Key.SetFiringOrder(FiringOrder.RangedWeaponUsageOrderEnum.FireAtWill);
                    pair.Key.SetTargetAgent(enemies[i % enemies.Length].Key);
                }
            }
            combatMovedAgents = agents.Count(p => p.Key.IsActive() && (p.Key.Position-p.Value.CombatPosition).LengthSquared >= 1);
            combatDamagedAgents = agents.Count(p => p.Key.IsActive() && p.Key.Health < p.Value.CombatHealth);
            combatOrderRounds++;
        }

        public override void OnAgentDeleted(Agent agent)
        {
            Binding binding;
            if (!agents.TryGetValue(agent, out binding)) return;
            if (!combat)
            {
                if (!retiring.Remove(agent)) unexpectedDeletion = true;
                deletedIndices.Add(binding.Index);
                if (!binding.IsMount) freePositions.Enqueue(binding.Position);
                deleted++;
            }
            agents.Remove(agent);
        }

        public override void OnAgentRemoved(Agent agent, Agent affector, AgentState state, KillingBlow blow)
        {
            if (combat && (state == AgentState.Killed || state == AgentState.Unconscious)) combatCasualties++;
        }

        public override void OnAgentShootMissile(Agent shooterAgent, EquipmentIndex weaponIndex, Vec3 position,
            Vec3 velocity, Mat3 orientation, bool hasRigidBody, int forcedMissileIndex)
        {
            Binding binding;
            if (!combat || !agents.TryGetValue(shooterAgent, out binding) || binding.IsMount) return;
            missileShots++; missileShooters.Add(binding.Index); lastShotWall = wall.Elapsed.TotalSeconds;
        }

        public override void OnMissileHit(Agent attacker, Agent victim, bool isCanceled, AttackCollisionData collisionData)
        {
            if (combat && !isCanceled && attacker != null && agents.ContainsKey(attacker)) missileHits++;
        }

        public override void OnMissionScreenTick(float dt)
        {
            if (teams == null || failed || finished || MissionScreen == null) return;
            try
            {
                if (combat && wall.Elapsed.TotalSeconds-combatStartedWall >= 5)
                    frameSamples.Observe(wall.Elapsed.TotalSeconds);
                if (camera == null)
                {
                    camera = Camera.CreateCamera();
                    camera.LookAt(origin + new Vec3(0, -220, 170), origin + new Vec3(0, 0, 1), new Vec3(0, 0, 1));
                    camera.SetFovVertical(0.9f, 16f / 9f, 0.1f, 2000f);
                    MissionScreen.CustomCamera = camera;
                    var frame = camera.Frame;
                    Mission.SetCameraFrame(ref frame, 1);
                }
                visibilityTime += dt;
                if (visibilityTime < 5) return;
                visibilityTime = 0;
                validVisuals = inCameraFrustum = visibilityChecksPassed = 0;
                Camera active = MissionScreen.CombatCamera;
                foreach (Agent agent in agents.Keys)
                {
                    var visual = agent.AgentVisuals;
                    if (visual == null) continue;
                    var entity = visual.GetEntity();
                    if (entity == null || entity.Pointer == UIntPtr.Zero) continue;
                    validVisuals++;
                    if (active != null && active.EnclosesPoint(agent.Position + new Vec3(0, 0, 1))) inCameraFrustum++;
                    if (active != null && active.CheckEntityVisibility(entity)) visibilityChecksPassed++;
                }
            }
            catch (Exception exception) { Fail(exception); }
        }

        public override void OnMissionScreenFinalize()
        {
            if (camera != null)
            {
                if (MissionScreen != null && MissionScreen.CustomCamera == camera) MissionScreen.CustomCamera = null;
                camera.ReleaseCamera();
                camera = null;
            }
            base.OnMissionScreenFinalize();
            screenFinalizedCallback = true;
        }

        protected override void OnEndMission()
        {
            base.OnEndMission();
            endCallback = true;
        }

        public override void OnMissionStateFinalized()
        {
            base.OnMissionStateFinalized();
            stateFinalizedCallback = true;
        }

        public override void OnRemoveBehavior()
        {
            base.OnRemoveBehavior();
            behaviorRemovedCallback = true;
        }

        private void VerifyFullPopulation()
        {
            if (agents.Count != Capacity || Mission.AllAgents.Count != Capacity
                || agents.Keys.Any(a => !a.IsActive() || a.Mission != retainedMission)
                || agents.Any(pair => pair.Key.Index != pair.Value.Index)
                || !agents.Values.Select(a => a.Index).OrderBy(i => i).SequenceEqual(Enumerable.Range(0, Capacity))
                || Mission.AllAgents.Any(a => !agents.ContainsKey(a)))
                throw new InvalidOperationException("Full pool does not contain exactly4080 owned active unique slots.");
        }

        private IntPtr NativeMissionPointer()
        {
            // This internal getter's CIL is pinned by the managed-engine hash in
            // StartDemo. No reflected native call or arbitrary address is exposed.
            if (retainedMission == null || retainedMission.IsFinalized || retainedMission != Mission
                || TaleWorlds.MountAndBlade.Mission.Current != retainedMission || IntPtr.Size != 8)
                throw new InvalidOperationException("Native exhaustion test has no live owned64-bit mission.");
            PropertyInfo property = typeof(Mission).GetProperty("Pointer", BindingFlags.Instance | BindingFlags.NonPublic);
            if (property == null || property.PropertyType != typeof(UIntPtr))
                throw new InvalidOperationException("Pinned Mission pointer getter is unavailable.");
            UIntPtr value = (UIntPtr)property.GetValue(retainedMission, null);
            if (value == UIntPtr.Zero) throw new InvalidOperationException("Mission pointer was cleared.");
            return new IntPtr(unchecked((long)value.ToUInt64()));
        }

        private byte[] CapturePoolAllocationState(IntPtr mission)
        {
            // Each table has4080 signed-short entries followed immediately by
            // count/high-water DWORDs. Include all five, including the empty pool.
            int tableBytes = Capacity * 2 + 8;
            var bytes = new byte[poolIndexTables.Length * tableBytes + Capacity * 4];
            var counts = new int[poolIndexTables.Length];
            for (int p = 0; p < poolIndexTables.Length; p++)
            {
                Marshal.Copy(IntPtr.Add(mission, poolIndexTables[p]), bytes, p * tableBytes, tableBytes);
                counts[p] = BitConverter.ToInt32(bytes, p * tableBytes + Capacity * 2);
                if (counts[p] != (p == 1 ? 0 : Capacity))
                    throw new InvalidOperationException("Unexpected full-pool allocation count in pool" + p);
            }
            for (int i = 0; i < Capacity; i++)
            {
                // The exact allocator tests this signed DWORD, not its index table.
                int header = Marshal.ReadInt32(IntPtr.Add(mission, 0x2d50 + i * 0xc60 + 0x10));
                if (header < 0) throw new InvalidOperationException("Native allocator slot is free despite a full managed population.");
                Buffer.BlockCopy(BitConverter.GetBytes(header), 0, bytes, poolIndexTables.Length * tableBytes + i * 4, 4);
            }
            exhaustionPoolCounts = counts;
            return bytes;
        }

        private void VerifyAllocatorExhaustion()
        {
            if (combat || recycling || retiring.Count != 0 || exhaustionAttempts != completedReuseCycles
                || MBCommon.CurrentGameType != MBCommon.GameType.Single)
                throw new InvalidOperationException("Allocator exhaustion requires the next controlled precombat full-pool cycle.");
            VerifyFullPopulation();
            IntPtr mission = NativeMissionPointer();
            IntPtr entry = IntPtr.Add(nativeBase, 0x5d8020);
            byte[] expected = Enumerable.Range(0, FullAllocatorPathHex.Length / 2)
                .Select(i => Convert.ToByte(FullAllocatorPathHex.Substring(i * 2, 2), 16)).ToArray();
            byte[] actual = new byte[expected.Length];
            Marshal.Copy(entry, actual, 0, actual.Length);
            if (!actual.SequenceEqual(expected)) throw new InvalidOperationException("Loaded native allocator bytes differ from the reviewed4080 full path.");
            var allocate = (FullPrimaryPoolAllocator)Marshal.GetDelegateForFunctionPointer(entry, typeof(FullPrimaryPoolAllocator));
            byte[] before = CapturePoolAllocationState(mission);
            using (var stream = new MemoryStream(before)) exhaustionSnapshotBefore = HashStream(stream);
            exhaustionAttempts++;
            Report("testing-native-allocator-exhaustion");
            // The only direct native invocation in this test. On the reviewed
            // full path it reads4080 headers, returns -1, and makes no writes or
            // calls. No callbacks/yield occur between validation and invocation.
            exhaustionReturnValue = allocate(IntPtr.Add(mission, 0x2d50));
            byte[] after = CapturePoolAllocationState(mission);
            using (var stream = new MemoryStream(after)) exhaustionSnapshotAfter = HashStream(stream);
            if (exhaustionReturnValue != -1 || !before.SequenceEqual(after))
                throw new InvalidOperationException("Native full allocator did not reject unchanged with -1.");
            VerifyFullPopulation();
            exhaustionVerified = true;
            exhaustionCheckpoints.Add(new { cycle = completedReuseCycles, attempt = exhaustionAttempts,
                returnValue = exhaustionReturnValue, before = exhaustionSnapshotBefore, after = exhaustionSnapshotAfter,
                poolCounts = exhaustionPoolCounts.ToArray(), wallSeconds = wall.Elapsed.TotalSeconds });
        }

        private void VerifyPublicSpawnOverflow()
        {
            VerifyFullPopulation();
            IntPtr mission = NativeMissionPointer();
            var request = new AgentBuildData(troop).Team(teams[0]).Equipment(equipment.Clone(false)).FixedEquipment(true)
                .NoHorses(workload.Profile != "cavalry").Controller(AgentControllerType.AI)
                .InitialPosition(origin).InitialDirection(new Vec2(0,1)).EquipmentSeed(0);
            Agent[] beforeAgents = Mission.AllAgents.ToArray();
            byte[] before = CapturePoolAllocationState(mission);
            bool rejected = false;
            publicOverflowAttempts++;
            try { Mission.SpawnAgent(request, false); }
            catch (NativeSpawnCapacityException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Public SpawnAgent did not reject the full pool with the reviewed capacity exception.");
            byte[] after = CapturePoolAllocationState(mission);
            Agent[] afterAgents = Mission.AllAgents.ToArray();
            if (!before.SequenceEqual(after) || beforeAgents.Length != afterAgents.Length
                || beforeAgents.Where((agent, index) => !ReferenceEquals(agent, afterAgents[index])).Any())
                throw new InvalidOperationException("Public overflow changed allocation metadata or the managed agent list.");
            using (var stream = new MemoryStream(before)) publicOverflowSnapshotBefore = HashStream(stream);
            using (var stream = new MemoryStream(after)) publicOverflowSnapshotAfter = HashStream(stream);
            VerifyFullPopulation();
            publicSpawnAgentOverflowTested = true;
        }

        private void BeginTeardown()
        {
            combatEndedWall = wall.Elapsed.TotalSeconds;
            completion.Begin(wall.Elapsed.TotalSeconds, fullPoolVerified && fullPoolRefillVerified
                && managedCapacityGuardVerified && exhaustionVerified && publicSpawnAgentOverflowTested
                && exhaustionAttempts == workload.ReuseCycles+1 && publicOverflowAttempts == workload.ReuseCycles+1
                && completedReuseCycles == workload.ReuseCycles && sustainedDurationVerified && workloadUseVerified && combatCasualties > 0);
            liveEntitiesAtTeardownStart = Mission.AllAgents.Count;
            // Stock OnDelete retains Agent.Mission for already-deleted wrappers;
            // EndMissionInternal clears only surviving AllAgents. Never inspect
            // a deleted wrapper's native properties during teardown.
            teardownAgents = agents.Keys.ToArray();
            currentEntities = Mission.AllAgents.Count;
            finished = true;
            Report("allocation-exhaustion-and-combat-complete-awaiting-teardown");
            Mission.EndMission();
        }

        // Mission ticks stop during finalization. The submodule keeps this observer
        // alive after OnGameEnd and after RemoveMissionBehavior clears this.Mission.
        internal static void OnApplicationTick()
        {
            NativePoolProbeMission probe = pendingProbe;
            if (probe == null || probe.failed || probe.quitRequested) return;
            try { probe.ObserveTeardown(); }
            catch (Exception exception) { probe.Fail(exception); }
        }

        private void ObserveTeardown()
        {
            if (!finished)
            {
                if (endCallback || stateFinalizedCallback || behaviorRemovedCallback || retainedMission.IsFinalized)
                    throw new InvalidOperationException("Mission ended before probe acceptance stages completed.");
                if (wall.Elapsed.TotalSeconds > workload.TimeoutSeconds) throw new TimeoutException("Native workload exceeded its wall-clock timeout.");
                return;
            }
            // IsFinalized is the managed zero-pointer check executed only after
            // native FinalizeMission returns. Agent.Mission is a managed field:
            // never read native Agent properties after finalization.
            bool cleared = endCallback && stateFinalizedCallback && behaviorRemovedCallback && screenFinalizedCallback
                && TaleWorlds.MountAndBlade.Mission.Current == null && MissionState.Current == null
                && retainedMission.IsFinalized && retainedMission.AllAgents == null
                && teardownAgents.All(a => a.Mission == null);
            NativePoolProbeCompletion.Action action = completion.Observe(wall.Elapsed.TotalSeconds, cleared);
            if (action == NativePoolProbeCompletion.Action.RequestExit)
            {
                quitRequested = true;
                Utilities.QuitGame();
            }
            else if (action == NativePoolProbeCompletion.Action.WriteReceipt)
            {
                currentEntities = 0;
                Report("passed-awaiting-normal-process-exit");
                completion.ConfirmReceiptWritten();
            }
            // Quit on a subsequent application tick, after the receipt was saved.
        }

        private void ReadSecondaryPool(out int count, out int highWater)
        {
            IntPtr metadata = IntPtr.Add(NativeMissionPointer(), poolIndexTables[1]+Capacity*2);
            count = Marshal.ReadInt32(metadata);
            highWater = Marshal.ReadInt32(IntPtr.Add(metadata,4));
            if (count < 0 || count > Capacity || highWater < 0 || highWater > Capacity)
                throw new InvalidOperationException("Secondary pool metadata exceeded the reviewed capacity.");
        }

        private void SampleResources(bool force)
        {
            double now = wall.Elapsed.TotalSeconds;
            if (!force && lastResourceWall >= 0 && now-lastResourceWall < 1) return;
            lastResourceWall = now;
            int humans = agents.Count(p => !p.Value.IsMount && p.Key.IsActive());
            int mounts = agents.Count(p => p.Value.IsMount && p.Key.IsActive());
            int mounted = agents.Count(p => !p.Value.IsMount && p.Key.IsActive() && p.Key.MountAgent != null);
            if (combat) minimumActiveCombatHumans = Math.Min(minimumActiveCombatHumans,humans);
            int secondaryCount, secondaryHighWater;
            ReadSecondaryPool(out secondaryCount,out secondaryHighWater);
            long? workingSet = null, privateBytes = null;
            double? cpuCoreEquivalent = null;
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    process.Refresh(); workingSet = process.WorkingSet64; privateBytes = process.PrivateMemorySize64;
                    double cpu = process.TotalProcessorTime.TotalSeconds;
                    if (lastCpuWall >= 0 && now > lastCpuWall && cpu >= lastCpuSeconds)
                        cpuCoreEquivalent = (cpu-lastCpuSeconds)/(now-lastCpuWall);
                    lastCpuSeconds = cpu; lastCpuWall = now;
                }
            }
            catch (Exception exception) { resourceSamplingError = exception.GetType().Name+": "+exception.Message; }
            if (combat) resourceCoverage.Observe(now-combatStartedWall, workingSet, privateBytes, cpuCoreEquivalent);
            resourceSamples.Add(new { wallSeconds = now, combatSeconds = combat ? now-combatStartedWall : (double?)null,
                stage, combat, activeHumans = humans, activeMounts = mounts, actualMountedPairs = mounted,
                totalEntities = Mission.AllAgents.Count, secondaryCount, secondaryHighWater,
                missileShots, missileHits, combatCasualties, workingSetBytes = workingSet, privateBytes,
                cpuCoreEquivalent, logicalProcessors = Environment.ProcessorCount });
        }

        private void Report(string status)
        {
            if (retainedMission != null && retainedMission.AllAgents != null)
                currentEntities = retainedMission.AllAgents.Count;
            SubModule.Report(new { status, runId = SubModule.NativeProbeRunId, scope = "experimental-native4080-pool-probe", nativeHash, capacity = Capacity,
                stage, combat, recycling, recycled, spawned, deleted, reused, peakEntities = peak, maxIndex,
                currentEntities, missionSeconds = elapsed, wallSeconds = wall.Elapsed.TotalSeconds,
                workload = workload.Profile, troopId = workload.TroopId, fixedEquipment = true,
                requestedSustainedSeconds = workload.SustainedSeconds, requestedReuseCycles = workload.ReuseCycles,
                completedReuseCycles, entityCostPerHuman = workload.EntityCost, peakHumans, peakMounts,
                mountedPairsVerified, mountedPairsAtCombat, minimumActiveCombatHumans = minimumActiveCombatHumans == int.MaxValue ? (int?)null : minimumActiveCombatHumans,
                sustainedWallSeconds = combat ? (combatEndedWall >= 0 ? combatEndedWall : wall.Elapsed.TotalSeconds)-combatStartedWall : 0,
                sustainedDurationVerified, workloadUseVerified, missileShots, missileHits, distinctMissileShooters = missileShooters.Count,
                secondsSinceLastShot = lastShotWall < 0 ? (double?)null : wall.Elapsed.TotalSeconds-lastShotWall,
                frameTiming = frameSamples.Snapshot(), frameTimingScope = "mission-screen tick wall intervals after5s combat warmup; includes probe/OS overhead, not GPU time",
                resourceSamples, resourceSamplingError, resourceCoverage, reuseCheckpoints, exhaustionCheckpoints,
                checkpoints, workerCount, combatCasualties, combatOrderRounds, combatMovedAgents, combatDamagedAgents, validVisuals, inCameraFrustum, visibilityChecksPassed,
                fullPoolVerified, fullPoolRefillVerified, managedCapacityGuardVerified, exhaustionVerified, exhaustionAttempts, exhaustionReturnValue,
                exhaustionSnapshotBefore, exhaustionSnapshotAfter, exhaustionPoolCounts,
                exhaustionScope = "primary-allocator-full-path-and-public-SpawnAgent-admission", publicSpawnAgentOverflowTested,
                publicOverflowAttempts, publicOverflowSnapshotBefore, publicOverflowSnapshotAfter,
                endCallback, stateFinalizedCallback, behaviorRemovedCallback, screenFinalizedCallback,
                liveEntitiesAtTeardownStart, allCreatedAgentCount = allCreatedAgents.Count,
                teardownAgentCount = teardownAgents.Length, clearedTeardownAgents = teardownAgents.Count(a => a.Mission == null),
                teardownAgentScope = "live survivors only; stock deleted wrappers may retain their managed Mission reference",
                teardownStableTicks = completion.StableTicks, error, teardownVerified = completion.Verified,
                nativeReturnCode = Utilities.GetReturnCode(), normalProcessExitVerified = false, visibleSoldierCountVerified = false });
        }

        private void Fail(Exception exception)
        {
            failed = true; error = exception.ToString();
            SubModule.Log(error); Report("failed"); Utilities.QuitGame();
        }
    }
}



