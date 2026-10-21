using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using System.Web.Script.Serialization;
using HarmonyLib;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.CampaignRegistry;

// MMR-281 (findings §16, §17 RT-04, RT-07, RT-33) the population in play, without the game:
//   PopulationRuntimeTests REPO_DIR BUNDLE_A BUNDLE_B GAME_ROOT HARMONY_DIR OUT_JSON
//   * the wanderer tick patch: its targets located in the installed TaleWorlds.CampaignSystem by IL content (the DailyTickEvent listener,
//     its swap, kill and spawn, AdjustEquipments), the prefix applied with Harmony and intercepting the installed tick, an unresolved
//     target refused, get__desiredTotalCompanionCount untouched;
//   * the wanderer rules on a fake world: deaths below W spawn through the four creation steps, hires do not, rotation at the installed
//     daily chance retires only unmet, unhired wanderers away from the player, made wanderers come from the backstory-safe pool, keep their
//     reserve sex and an epithet, and carry FWGC++ reserve names of worlds A and B. The fake world is each world's new game as the activation
//     builds it (CompiledWorldDataset): its day-0 wanderers, and only the templates its NPCCharacters register are usable (MMR-262: on A' a
//     khuzait woman's and an aserai man's top-up take an own-culture template no living wanderer has);
//   * the IsMinorFactionHero list on worlds A and B (every company and outlaw-clan member, nobody else) and its marking;
//   * the homeless-house rule (nearest same-culture kingdom, else the nearest; never the player's clan) inside the installed 28 days;
//   * the compatibility statuses and the soak census fields MMR-247 reads (MMR-262: the per-house, per-minor-clan, fief-owner and
//     kingdom-change details, the player's clan left out).
internal static class PopulationRuntimeTests
{
    private static int checks, failures;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static readonly Dictionary<string, object> Evidence = new Dictionary<string, object>();
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; Console.WriteLine((condition ? "ok    " : "FAIL  ") + label); }
    private static IDictionary<string, object> Parse(string text) { return (IDictionary<string, object>)Json.DeserializeObject(text.TrimStart('﻿')); }
    private static IDictionary<string, object> Load(string path) { return Parse(File.ReadAllText(path)); }
    private static IDictionary<string, object> O(IDictionary<string, object> o, string key) { return (IDictionary<string, object>)o[key]; }
    private static string S(IDictionary<string, object> o, string key) { object v; return o.TryGetValue(key, out v) ? v as string : null; }
    private static IEnumerable<IDictionary<string, object>> Records(IDictionary<string, object> o, string key) { return ((IEnumerable)o[key]).Cast<IDictionary<string, object>>(); }
    private static List<string> L(IDictionary<string, object> o, string key) { object v; return o.TryGetValue(key, out v) && v is IEnumerable && !(v is string) ? ((IEnumerable)v).Cast<object>().Select(x => Convert.ToString(x)).ToList() : new List<string>(); }

    private static string[] probe;

    private static int Main(string[] args)
    {
        if (args.Length != 6) { Console.Error.WriteLine("usage: PopulationRuntimeTests REPO_DIR BUNDLE_A BUNDLE_B GAME_ROOT HARMONY_DIR OUT_JSON"); return 2; }
        string game = args[3];
        probe = new[] { Path.Combine(game, "bin/Win64_Shipping_Client"), Path.Combine(game, "Modules/SandBox/bin/Win64_Shipping_Client"),
                        Path.Combine(game, "Modules/SandBoxCore/bin/Win64_Shipping_Client"), Path.Combine(game, "Modules/StoryMode/bin/Win64_Shipping_Client"),
                        Path.Combine(game, "Modules/NavalDLC/bin/Win64_Shipping_Client"), args[4] };
        AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            foreach (var dir in probe) { var path = Path.Combine(dir, name); if (File.Exists(path)) return Assembly.LoadFrom(path); }
            return null;
        };
        string repo = args[0];
        var bundles = new[] { new KeyValuePair<string, string>("A", args[1]), new KeyValuePair<string, string>("B", args[2]) };
        Run("tick patch", () => TickPatch());
        Run("worlds", () => Worlds(repo, bundles));
        Run("homeless houses", () => Homeless());
        Run("statuses", () => Statuses(repo));
        Evidence["schema"] = "mnbl.population-runtime"; Evidence["version"] = 1; Evidence["ticket"] = "MMR-281";
        Evidence["checks"] = checks; Evidence["failures"] = failures;
        File.WriteAllText(args[5], Json.Serialize(Evidence), new UTF8Encoding(false));
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string what, Action body)
    {
        try { body(); }
        catch (Exception e) { Check(false, "unexpected exception (" + what + "): " + e); }
    }

    // ---- the tick patch against the installed TaleWorlds.CampaignSystem

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TickPatch()
    {
        var behaviour = typeof(TaleWorlds.CampaignSystem.Hero).Assembly.GetType(CompanionsTickPatch.BehaviourType);
        Check(behaviour != null, "the installed TaleWorlds.CampaignSystem has " + CompanionsTickPatch.BehaviourType);
        var r = CompanionsTickPatch.Resolve(behaviour, CompanionsTickPatch.Instructions);
        Check(r.Resolved && r.DailyTick != null && r.Swap != null && r.Kill != null && r.Spawn != null && r.AdjustEquipments != null,
              "the descriptor resolves against the installed assembly by IL content: daily tick " + Name(r.DailyTick) + " (the DailyTickEvent listener), swap " + Name(r.Swap) +
              ", kill " + Name(r.Kill) + ", spawn " + Name(r.Spawn) + ", AdjustEquipments " + Name(r.AdjustEquipments) + (r.Resolved ? "" : "; problem: " + r.Problem));
        // The names it found are the installed ones (the IL content, not the names, located them).
        Check(Name(r.DailyTick) == "DailyTick" && Name(r.Swap) == "SwapCompanions" && Name(r.Kill) == "TryKillCompanion" && Name(r.Spawn) == "TrySpawnNewCompanion" && Name(r.AdjustEquipments) == "AdjustEquipments",
              "the IL-located targets are the installed DailyTick, SwapCompanions, TryKillCompanion, TrySpawnNewCompanion and AdjustEquipments");
        // A changed body is refused, never patched blindly.
        Func<MethodBase, List<CodeInstruction>> noKill = m => { var code = CompanionsTickPatch.Instructions(m); if (m == r.DailyTick) code = code.Where(i => !(i.operand is MethodInfo && (MethodInfo)i.operand == r.Kill)).ToList(); return code; };
        Func<MethodBase, List<CodeInstruction>> noListener = m => { var code = CompanionsTickPatch.Instructions(m); if (m.Name == "RegisterEvents") code = code.Where(i => !(i.opcode == OpCodes.Ldftn && (MethodInfo)i.operand == r.DailyTick)).ToList(); return code; };
        var a = CompanionsTickPatch.Resolve(behaviour, noKill); var b = CompanionsTickPatch.Resolve(behaviour, noListener); var c = CompanionsTickPatch.Resolve(null, CompanionsTickPatch.Instructions);
        Check(!a.Resolved && !b.Resolved && !c.Resolved, "a daily tick without its kill (" + a.Problem + "), a DailyTickEvent without the listener (" + b.Problem + ") and no behaviour type are unresolved");

        // The prefix, applied with Harmony: with a handler it replaces the installed tick; without one the installed tick runs.
        var harmony = new Harmony("mnbl.proceduralworld.test.population");
        var trace = new List<string>();
        bool applied = CompanionsTickPatch.Apply(harmony, trace.Add);
        var info = Harmony.GetPatchInfo(r.DailyTick);
        var desired = behaviour.GetMethod("get__desiredTotalCompanionCount", BindingFlags.Instance | BindingFlags.NonPublic);
        Check(applied && info != null && info.Prefixes.Count == 1 && info.Prefixes[0].owner == harmony.Id && desired != null && Harmony.GetPatchInfo(desired) == null && CompanionsTickPatch.Current != null,
              "the prefix is applied to the installed daily tick (" + String.Join("; ", trace) + "); get__desiredTotalCompanionCount stays unpatched");
        var instance = FormatterServices.GetUninitializedObject(behaviour);
        int handled = 0;
        CompanionsTickPatch.Handler = o => { handled++; return ReferenceEquals(o, instance) ? false : true; };
        Exception replaced = null;
        try { r.DailyTick.Invoke(instance, null); } catch (Exception e) { replaced = e; }
        int handledThen = handled;
        CompanionsTickPatch.Handler = null;
        Exception installed = null;
        try { r.DailyTick.Invoke(instance, null); } catch (Exception e) { installed = e; }
        Check(handledThen == 1 && replaced == null && handled == 1 && installed != null,
              "with the generated handler the installed tick's body is skipped (no TryKillCompanion: it ran without a campaign and threw nothing); without one the installed body runs (" +
              (installed == null ? "nothing thrown" : (installed.InnerException ?? installed).GetType().Name + " without a campaign") + ")");
        harmony.UnpatchAll(harmony.Id); CompanionsTickPatch.Removed();
        Evidence["tickPatch"] = new Dictionary<string, object> { ["dailyTick"] = Name(r.DailyTick), ["swap"] = Name(r.Swap), ["kill"] = Name(r.Kill), ["spawn"] = Name(r.Spawn),
            ["adjustEquipments"] = Name(r.AdjustEquipments), ["applied"] = applied, ["trace"] = trace, ["refused"] = new[] { a.Problem, b.Problem, c.Problem } };
    }

    private static string Name(MethodInfo m) { return m == null ? "none" : m.Name; }

    // ---- the wanderer rules on a fake world

    private sealed class FakeHero { public string Id, Template, Name, First, Town, State; public bool Adjusted; }

    private sealed class FakeWorld : WandererRules.IWorld
    {
        public readonly List<WandererRules.Wanderer> Living = new List<WandererRules.Wanderer>();
        public readonly List<WandererRules.Town> AllTowns = new List<WandererRules.Town>();
        public readonly List<string> Calls = new List<string>();
        public readonly List<WandererRules.Wanderer> Retired = new List<WandererRules.Wanderer>();
        public readonly Random Random;
        public float? Roll;   // a fixed daily roll, else drawn
        private int next;
        public ISet<string> Registered;   // the NPCCharacters the world's new-game documents register (as the game's GetObject<CharacterObject>)
        public FakeWorld(int seed, ISet<string> registered) { Random = new Random(seed); Registered = registered; }
        public List<WandererRules.Wanderer> Wanderers() { return Living.ToList(); }
        public List<WandererRules.Town> Towns() { return AllTowns.ToList(); }
        public ISet<string> LivingNames() { return new HashSet<string>(Living.SelectMany(w => new[] { ((FakeHero)w.Hero).Name, ((FakeHero)w.Hero).First }), StringComparer.Ordinal); }
        public float RandomFloat() { return Roll ?? (float)Random.NextDouble(); }
        public int RandomInt(int n) { return n <= 1 ? 0 : Random.Next(n); }
        public int CompanionAge() { return 18 + 5 + Random.Next(12); }
        public bool TemplateUsable(string id) { return Registered.Contains(id); }
        public void Swap() { Calls.Add("Swap"); }
        public void Retire(WandererRules.Wanderer w) { Calls.Add("Retire:" + w.Id); Living.Remove(w); Retired.Add(w); }
        public object CreateSpecialHero(string template, string town, int age) { Calls.Add("CreateSpecialHero"); return new FakeHero { Id = "made_" + (next++), Template = template, Town = null, First = "Engine" + next, Name = "Engine" + next }; }
        public void AdjustEquipments(object hero) { Calls.Add("AdjustEquipments"); ((FakeHero)hero).Adjusted = true; }
        public void Activate(object hero) { Calls.Add("ChangeState(Active)"); ((FakeHero)hero).State = "Active"; }
        public void EnterSettlement(object hero, string town)
        {
            Calls.Add("EnterSettlementAction"); var h = (FakeHero)hero; h.Town = town;
            Living.Add(new WandererRules.Wanderer { Hero = h, Id = h.Id, Template = h.Template });
        }
        public string FirstName(object hero) { return ((FakeHero)hero).First; }
        public void SetName(object hero, string name, string firstName) { Calls.Add("SetName"); ((FakeHero)hero).Name = name; ((FakeHero)hero).First = firstName; }
    }

    // The world as the new game starts it: its towns, and its day-0 wanderers (the startup plan's companions with their templates); the game's
    // registered NPCCharacters answer which templates are usable.
    private static FakeWorld World(IDictionary<string, object> campaign, CompiledWorldDataset dataset, ISet<string> registered, int seed)
    {
        var w = new FakeWorld(seed, registered);
        foreach (var t in Records(campaign, "towns")) w.AllTowns.Add(new WandererRules.Town { Id = S(t, "id"), Culture = S(t, "presentation"), Eligible = true });
        var names = dataset.Heroes.Wanderers.ToDictionary(x => x.Id, x => x.Name, StringComparer.Ordinal);
        foreach (var c in dataset.Start.Plan.Companions)
        {
            string name = names[c.Id];
            var h = new FakeHero { Id = c.Id, Template = c.Template, Name = name, First = name.Split(' ')[0], Town = c.Settlement, State = "Active" };
            w.Living.Add(new WandererRules.Wanderer { Hero = h, Id = h.Id, Template = h.Template });
        }
        return w;
    }

    // The compiled world's new-game dataset, as the activation builds it (CompiledWorldDataset): the pinned catalog capture, the world's
    // settlement layout (the git-ignored position report refresh-fixtures writes for worlds A and B), the repository policies.
    private static CompiledWorldDataset Dataset(string repo, WorldDataBundle bundle, string label)
    {
        var pin = Load(Path.Combine(repo, "worldgen/policies/campaign-catalog.json"));
        var receipt = Load(Path.Combine(repo, S(O(pin, "provenance"), "catalogReceipt")));
        var catalog = new Dictionary<string, System.Xml.XmlDocument>(StringComparer.Ordinal);
        foreach (var d in Records(O(receipt, "outputs"), "documents"))
        {
            var bytes = File.ReadAllBytes(S(d, "path"));
            if (CompiledWorldDataset.Sha256(bytes) != S(d, "sha256")) throw new InvalidOperationException("catalog " + S(d, "id") + " differs from its capture");
            var doc = new System.Xml.XmlDocument { XmlResolver = null }; doc.LoadXml(new UTF8Encoding(false).GetString(bytes).TrimStart('﻿')); catalog[S(d, "id")] = doc;
        }
        var layout = O(O(Load(Path.Combine(repo, "evidence/world/settlement-position-report.json")), "worlds"), label);
        var grid = O(Parse(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/travel/travel-graph.json"))), "grid");
        Func<string, double> number = key => Convert.ToDouble(grid[key], System.Globalization.CultureInfo.InvariantCulture);
        return CompiledWorldDataset.Build(new CompiledWorldDataset.Sources {
            Bundle = bundle.ReadVerified, Policy = p => File.ReadAllBytes(Path.Combine(repo, p)), Catalog = catalog,
            CatalogSha256 = CompiledWorldDataset.Sha256(File.ReadAllBytes(Path.Combine(repo, "worldgen/policies/campaign-catalog.json"))), Defaults = L(pin, "defaults"),
            MissionScenes = MNBL.ProceduralWorld.Missions.MissionScenes.FromBytes(File.ReadAllBytes(Path.Combine(repo, MNBL.ProceduralWorld.Missions.MissionScenes.PolicyPath))),
            Layout = layout, OriginX = number("columnOffset") * number("spacing"), OriginY = number("rowOffset") * number("spacing"), Width = number("cols") * number("spacing") });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Worlds(string repo, IList<KeyValuePair<string, string>> bundles)
    {
        var worlds = new Dictionary<string, object>();
        foreach (var b in bundles)
        {
            var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", WorldDataBundle.HostCapabilities);
            var bundle = WorldDataBundle.Open(b.Value, host);
            var campaign = Parse(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/campaign/campaign.json")));
            var dataset = Dataset(repo, bundle, b.Key);
            var plan = dataset.Population;
            var registered = new HashSet<string>(dataset.Documents.Single(d => d.Key == "NPCCharacters").Value.DocumentElement.ChildNodes.OfType<System.Xml.XmlElement>()
                .Select(e => e.GetAttribute("id")), StringComparer.Ordinal);
            var row = new Dictionary<string, object>(); worlds[b.Key] = row;
            MinorFaction(b.Key, campaign, plan, row);
            Wanderers(b.Key, campaign, plan, dataset, registered, row);
        }
        Evidence["worlds"] = worlds;
    }

    // The IsMinorFactionHero list: every company and outlaw-clan member of campaign v2, no house member; and its marking.
    private static void MinorFaction(string label, IDictionary<string, object> campaign, PopulationPlan plan, Dictionary<string, object> row)
    {
        var expected = Records(campaign, "mercenaryCompanies").Concat(Records(campaign, "outlawClans")).SelectMany(c => L(c, "members")).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var houseMembers = new HashSet<string>(Records(campaign, "houses").SelectMany(c => L(c, "members")), StringComparer.Ordinal);
        var census = O(campaign, "census");
        int planned = Convert.ToInt32(census["companyMembers"]) + Convert.ToInt32(census["outlawMembers"]);
        Check(plan.MinorFactionHeroes.SequenceEqual(expected) && plan.MinorFactionHeroes.Count == planned && !plan.MinorFactionHeroes.Any(houseMembers.Contains) && plan.Houses.Count == Records(campaign, "houses").Count(),
              "world " + label + ": the IsMinorFactionHero list is exactly the " + plan.MinorFactionHeroes.Count + " members of its " + Records(campaign, "mercenaryCompanies").Count() + " companies and " +
              Records(campaign, "outlawClans").Count() + " outlaw clans (census " + planned + "), no house member; the homeless-house rule covers its " + plan.Houses.Count + " houses");
        var flags = plan.MinorFactionHeroes.Take(plan.MinorFactionHeroes.Count - 1).ToDictionary(id => id, id => false, StringComparer.Ordinal);   // one hero missing
        Func<string, object> find = id => flags.ContainsKey(id) ? id : null;
        var first = MinorFactionHeroes.Mark(plan.MinorFactionHeroes, find, h => flags[(string)h], h => flags[(string)h] = true);
        var again = MinorFactionHeroes.Mark(plan.MinorFactionHeroes, find, h => flags[(string)h], h => flags[(string)h] = true);
        Check(first.Set == plan.MinorFactionHeroes.Count - 1 && first.Missing.Count == 1 && flags.Values.All(v => v) && again.Set == 0 && again.Already == first.Set,
              "world " + label + ": marking sets the flag on every found hero, reports the missing one, and a re-assert (load) sets nothing new");
        row["minorFactionHeroes"] = plan.MinorFactionHeroes.Count;
    }

    private static void Wanderers(string label, IDictionary<string, object> campaign, PopulationPlan plan, CompiledWorldDataset dataset, ISet<string> registered, Dictionary<string, object> row)
    {
        int W = Convert.ToInt32(O(campaign, "census")["wanderers"]);
        var reserveRecords = Records(campaign, "wandererReserve").ToList();
        var heroNames = new HashSet<string>(Records(campaign, "heroes").Select(h => S(h, "name")), StringComparer.Ordinal);
        var towns = new HashSet<string>(Records(campaign, "towns").Select(t => S(t, "presentation")), StringComparer.Ordinal);
        Check(plan.Wanderers == W && plan.Reserve.Count == reserveRecords.Count && plan.Reserve.Count > 0 && plan.Reserve.Select(r => r.Name).Distinct().Count() == plan.Reserve.Count &&
              !plan.Reserve.Any(r => heroNames.Contains(r.Name)) && plan.Reserve.All(r => towns.Contains(r.Presentation)) && towns.All(p => plan.Reserve.Any(r => r.Presentation == p)) &&
              plan.Reserve.Any(r => r.Female) && plan.Reserve.Any(r => !r.Female) && plan.Reserve.Count(r => !String.IsNullOrEmpty(r.Epithet)) > 0 && Math.Abs(plan.RotationChance - 0.1) < 1e-9,
              "world " + label + ": the plan reads W " + plan.Wanderers + " from the census, the rotation chance " + plan.RotationChance + ", and " + plan.Reserve.Count + " reserve names (" +
              plan.Reserve.Count(r => r.Female) + " women) over its " + towns.Count + " town cultures, unique and carried by no hero, e.g. " +
              String.Join(", ", plan.Reserve.Take(3).Select(r => "\"" + r.Name + (r.Epithet == null ? "" : " " + r.Epithet) + "\"")));

        // The fake world starts with the day-0 wanderers and answers TemplateUsable from the NPCCharacters the new game registers.
        Func<int, FakeWorld> fresh = seed => World(campaign, dataset, registered, seed);
        Check(dataset.Start.Plan.Companions.Count == W && dataset.Start.Plan.Companions.All(c => registered.Contains(c.Template)),
              "world " + label + ": the fake world starts with the " + dataset.Start.Plan.Companions.Count + " day-0 wanderers of the startup plan, and answers which templates are usable from the " +
              registered.Count + " NPCCharacters the new game's documents register (" + plan.Pool.Safe.Count(t => registered.Contains(t.Id)) + " of the " + plan.Pool.Safe.Count() + " backstory-safe wanderer templates)");

        // Deaths below W spawn through the four creation steps, one a day (no rotation: the roll is above the chance).
        var rules = new WandererRules(plan);
        var world = fresh(7); world.Roll = 0.9f;
        foreach (var w in world.Living.Take(3).ToList()) world.Living.Remove(w);
        var made = new List<WandererRules.Made>();
        for (int day = 0; day < 4; day++) { world.Calls.Add("day"); made.AddRange(rules.Tick(world)); }
        var creation = new[] { "CreateSpecialHero", "AdjustEquipments", "ChangeState(Active)", "EnterSettlementAction", "SetName" };
        var days = String.Join(" ", world.Calls).Split(new[] { "day" }, StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim()).ToList();
        bool steps = days.Take(3).All(d => d == "Swap " + String.Join(" ", creation)) && days[3] == "Swap";
        Check(made.Count == 3 && made.All(m => m.Reason == "topUp" && m.Steps.SequenceEqual(creation.Take(4)) && ((FakeHero)m.Hero).Adjusted && ((FakeHero)m.Hero).State == "Active" && ((FakeHero)m.Hero).Town == m.Town) &&
              world.Living.Count == W && steps && rules.ToppedUp == 3,
              "world " + label + ": 3 deaths below W " + W + " spawn one wanderer a day through CreateSpecialHero, AdjustEquipments, ChangeState(Active), EnterSettlementAction (then the name), the swap runs every day, and at W nothing more is made");
        // Hires do not: hired wanderers still count (vanilla counts them), so nothing is made.
        var hires = new WandererRules(plan);
        var hired = fresh(8); hired.Roll = 0.9f;
        foreach (var w in hired.Living.Take(5)) w.Hired = true;
        var none = new List<WandererRules.Made>();
        for (int day = 0; day < 5; day++) none.AddRange(hires.Tick(hired));
        Check(none.Count == 0 && hired.Living.Count == W && hired.Retired.Count == 0, "world " + label + ": 5 hires leave W heroes of occupation Wanderer, so nothing is made and nobody retires");

        // Rotation: at the installed daily chance; only unmet, unhired wanderers away from the player's settlement retire, each replaced.
        var rot = new WandererRules(plan);
        var rw = fresh(9);
        for (int i = 0; i < rw.Living.Count; i++) { rw.Living[i].Met = i % 4 == 0; rw.Living[i].Hired = i % 4 == 1; rw.Living[i].AtPlayerSettlement = i % 4 == 2; }
        var protectedIds = new HashSet<string>(rw.Living.Where(w => w.Met || w.Hired || w.AtPlayerSettlement).Select(w => w.Id), StringComparer.Ordinal);
        int days2 = 3000;
        var rotated = new List<WandererRules.Made>();
        for (int day = 0; day < days2; day++) rotated.AddRange(rot.Tick(rw));
        double expected = days2 * plan.RotationChance, sigma = Math.Sqrt(days2 * plan.RotationChance * (1 - plan.RotationChance));
        Check(rw.Retired.Count == rot.Rotated && rw.Retired.All(w => !w.Met && !w.Hired && !w.AtPlayerSettlement && !protectedIds.Contains(w.Id)) &&
              protectedIds.All(id => rw.Living.Any(w => w.Id == id)) && Math.Abs(rot.Rotated - expected) <= 3 * sigma && rotated.Count(m => m.Reason.StartsWith("rotation:")) == rot.Rotated && rw.Living.Count == W,
              "world " + label + ": over " + days2 + " days " + rot.Rotated + " rotations (the installed 10% daily chance: " + Math.Round(expected) + " +- " + Math.Round(3 * sigma) +
              "), every one an unmet, unhired wanderer away from the player's settlement and replaced at once; met, hired and present wanderers all stay; W kept");
        var noRoll = new WandererRules(plan); var still = fresh(10); still.Roll = 0.1001f;
        for (int day = 0; day < 50; day++) noRoll.Tick(still);
        var yesRoll = new WandererRules(plan); var go = fresh(11); go.Roll = 0.1f;
        yesRoll.Tick(go);
        Check(still.Retired.Count == 0 && go.Retired.Count == 1, "world " + label + ": a roll above 0.1 never rotates and a roll of 0.1 does (the installed comparison, RandomFloat > KillChance skips)");

        // Made wanderers: backstory-safe templates, the reserve's sex and an epithet, FWGC++ reserve names, no two living with one name.
        var all = made.Concat(rotated).ToList();
        var reserve = plan.Reserve.ToDictionary(x => x.Key, StringComparer.Ordinal);
        var unsafeMade = all.Where(m => !plan.Pool.IsSafe(m.Template)).ToList();
        var sexWrong = all.Where(m => m.Reserve != null && reserve[m.Reserve].Female != m.Female).ToList();
        var noEpithet = all.Where(m => String.IsNullOrWhiteSpace(m.Epithet) || !WandererPool.HasEpithet(m.Name, m.Epithet)).ToList();
        var notReserve = all.Where(m => m.Reserve == null || !m.Name.StartsWith(reserve[m.Reserve].Name + " ", StringComparison.Ordinal)).ToList();
        var dupes = rw.Living.GroupBy(w => ((FakeHero)w.Hero).Name).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        var cross = all.Where(m => m.TemplateCulture != m.Culture).Select(m => m.Culture + "->" + m.TemplateCulture + " (" + m.Fallback + ")").Distinct().ToList();
        Check(all.Count > 200 && unsafeMade.Count == 0 && sexWrong.Count == 0 && noEpithet.Count == 0 && notReserve.Count == 0 && dupes.Count == 0 &&
              all.Where(m => m.TemplateCulture != m.Culture).All(m => m.Fallback != null && m.Fallback.Contains("crossCulture")),
              "world " + label + ": all " + all.Count + " made wanderers take backstory-safe templates, the reserve's sex and an epithet (e.g. " +
              String.Join(", ", all.Take(3).Select(m => "\"" + m.Name + "\" " + m.Template)) + "), carry reserve names, never two living with one name; cross-culture templates only as recorded fallbacks: " +
              (cross.Count == 0 ? "none" : String.Join(", ", cross)));
        // Without a reserve name (none left) the engine's name keeps the template's epithet.
        var bare = new PopulationPlan { Wanderers = plan.Wanderers, RotationChance = plan.RotationChance, Pool = plan.Pool };
        var bareRules = new WandererRules(bare); var bareWorld = fresh(12); bareWorld.Roll = 0.9f; bareWorld.Living.RemoveAt(0);
        var one = bareRules.Tick(bareWorld).Single();
        Check(one.Reserve == null && one.Fallback != null && one.Fallback.Contains("noReserveName") && WandererPool.HasEpithet(one.Name, plan.Pool.Find(one.Template).Epithet),
              "world " + label + ": with no reserve name left the wanderer keeps the engine's name and the template's epithet (\"" + one.Name + "\", recorded)");

        // MMR-262 (F05), world A': a top-up for a khuzait woman in a khuzait town and for an aserai man in an aserai town takes an own-culture
        // template no living wanderer has, from the registered ones. With only the day-0 wanderers' templates registered (the new game before
        // the fix) the same top-ups went cross-culture or repeated a living wanderer's template: recorded beside each check, not asserted.
        if (label == "A")
        {
            var dayZeroTemplates = new HashSet<string>(dataset.Start.Plan.Companions.Select(c => c.Template), StringComparer.Ordinal);
            var before = new HashSet<string>(registered.Where(id => !plan.Pool.IsSafe(id) || dayZeroTemplates.Contains(id)), StringComparer.Ordinal);
            WandererRules.Made TopUp(ISet<string> usable, string culture, bool female, out int names, out HashSet<string> living)
            {
                var p = new PopulationPlan { Wanderers = plan.Wanderers, RotationChance = plan.RotationChance, Pool = plan.Pool };
                p.Reserve.AddRange(plan.Reserve.Where(r => r.Presentation == culture && r.Female == female));
                names = p.Reserve.Count;
                var w = World(campaign, dataset, usable, 13); w.Roll = 0.9f;
                foreach (var t in w.AllTowns) t.Eligible = t.Culture == culture;
                w.Living.Remove(w.Living.First(x => plan.Pool.Find(x.Template).Culture != culture));   // a death that frees no template of this culture
                var ticked = new WandererRules(p).Tick(w);
                var m = ticked.Count == 1 ? ticked[0] : null;
                living = new HashSet<string>(w.Living.Where(x => m == null || x.Hero != m.Hero).Select(x => x.Template), StringComparer.Ordinal);
                return m;
            }
            var topUps = new List<object>();
            foreach (var want in new[] { new KeyValuePair<string, bool>("khuzait", true), new KeyValuePair<string, bool>("aserai", false) })
            {
                int names, namesBefore; HashSet<string> living, livingBefore;
                var m = TopUp(registered, want.Key, want.Value, out names, out living);
                var old = TopUp(before, want.Key, want.Value, out namesBefore, out livingBefore);
                string who = want.Key + (want.Value ? " woman" : " man");
                string then = old == null ? "none" : old.Template + " (" + (old.Fallback ?? (livingBefore.Contains(old.Template) ? "used" : "own culture, unused")) + ")";
                Check(names > 0 && m != null && m.Reason == "topUp" && m.TemplateCulture == want.Key && m.Female == want.Value && m.Fallback == null && !living.Contains(m.Template) && registered.Contains(m.Template),
                      "world A: a top-up for " + (want.Key == "aserai" ? "an " : "a ") + who + " in " + (want.Key == "aserai" ? "an " : "a ") + want.Key + " town takes the own-culture template " + (m == null ? "none" : m.Template + (m.Fallback == null ? "" : " (" + m.Fallback + ")")) +
                      ", which no living wanderer has (" + living.Count + " templates in use); with only the day-0 wanderers' templates registered, as before MMR-262: " + then);
                topUps.Add(new Dictionary<string, object> { ["wanderer"] = who, ["template"] = m == null ? null : m.Template, ["fallback"] = m == null ? null : m.Fallback,
                                                            ["beforeTemplate"] = old == null ? null : old.Template, ["beforeFallback"] = old == null ? null : old.Fallback });
            }
            row["topUps"] = topUps;
        }
        row["registeredSafeTemplates"] = plan.Pool.Safe.Count(t => registered.Contains(t.Id));
        row["W"] = W; row["reserve"] = plan.Reserve.Count; row["rotations"] = rot.Rotated; row["rotationDays"] = days2; row["made"] = all.Count;
        row["examples"] = all.Take(8).Select(m => new Dictionary<string, object> { ["name"] = m.Name, ["template"] = m.Template, ["town"] = m.Town, ["culture"] = m.Culture, ["female"] = m.Female, ["reason"] = m.Reason, ["fallback"] = m.Fallback }).ToList();
    }

    // ---- the homeless-house rule (RT-07)

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Homeless()
    {
        // The installed timing: the 28 days FactionDiscontinuationCampaignBehavior gives a clan without a kingdom, and the detail a destroyed
        // kingdom's clans leave with (the rule's trigger).
        var fd = typeof(TaleWorlds.CampaignSystem.Hero).Assembly.GetType("TaleWorlds.CampaignSystem.CampaignBehaviors.FactionDiscontinuationCampaignBehavior");
        var survival = fd == null ? null : fd.GetField("SurvivalDurationForIndependentClanInDays", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        float days = survival == null ? -1 : (float)survival.GetRawConstantValue();
        var discontinue = fd == null ? null : fd.GetMethod("DiscontinueKingdom", BindingFlags.Instance | BindingFlags.NonPublic);
        bool leaves = discontinue != null && CompanionsTickPatch.Instructions(discontinue).Any(i => i.operand is MethodInfo && ((MethodInfo)i.operand).Name == "ApplyByLeaveByKingdomDestruction");
        Check(days == HomelessHouses.SurvivalDays && HomelessHouses.MoveAfterDays < days && leaves,
              "the installed FactionDiscontinuationCampaignBehavior removes a clan " + days + " days after it is left without a kingdom, and DiscontinueKingdom makes each clan leave by ApplyByLeaveByKingdomDestruction; the rule moves a house " +
              HomelessHouses.MoveAfterDays + " day after (the next daily tick)");
        const double width = 100;
        Func<double, double, double, double, double> periodic = (ax, ay, bx, by) => { double dx = Math.Abs(ax - bx); dx = Math.Min(dx, width - dx); return Math.Sqrt(dx * dx + (ay - by) * (ay - by)); };
        Func<string, string, double[][], HomelessHouses.Realm> realm = (id, culture, fiefs) => { var k = new HomelessHouses.Realm { Id = id, Culture = culture }; k.Fiefs.AddRange(fiefs); return k; };
        var house = new HomelessHouses.House { Id = "house_a", Culture = "sturgia", Generated = true, X = 1, Y = 50 };
        var kingdoms = new List<HomelessHouses.Realm> {
            realm("near_vlandia", "vlandia", new[] { new double[] { 5, 50 } }),
            realm("far_sturgia", "sturgia", new[] { new double[] { 40, 50 } }),
            realm("seam_sturgia", "sturgia", new[] { new double[] { 97, 50 } }),
            realm("dead_sturgia", "sturgia", new double[0][]) };
        var eliminated = realm("gone_sturgia", "sturgia", new[] { new double[] { 2, 50 } }); eliminated.Eliminated = true; kingdoms.Add(eliminated);
        var player = realm("player_sturgia", "sturgia", new[] { new double[] { 3, 50 } }); player.PlayerRuled = true; kingdoms.Add(player);
        var sameCulture = HomelessHouses.Destination(house, kingdoms, periodic);
        var other = new HomelessHouses.House { Id = "house_b", Culture = "aserai", Generated = true, X = 30, Y = 50 };
        var nearest = HomelessHouses.Destination(other, kingdoms, periodic);
        var playerClan = new HomelessHouses.House { Id = "player_faction", Culture = "sturgia", Player = true, Generated = true, X = 1, Y = 50 };
        var vanillaClan = new HomelessHouses.House { Id = "rebel", Culture = "sturgia", Generated = false, X = 1, Y = 50 };
        Check(sameCulture != null && sameCulture.Id == "seam_sturgia" && nearest != null && nearest.Id == "far_sturgia" && HomelessHouses.Destination(playerClan, kingdoms, periodic) == null &&
              HomelessHouses.Destination(vanillaClan, kingdoms, periodic) == null,
              "a homeless sturgian house joins the nearest sturgian kingdom across the seam (" + (sameCulture == null ? "none" : sameCulture.Id) + ", before a nearer vlandian one; eliminated, fiefless and player-ruled " +
              "kingdoms take none); an aserai house with no aserai kingdom joins the nearest kingdom (" + (nearest == null ? "none" : nearest.Id) + "); the player's clan and a clan that is no generated house are never moved");
        Evidence["homeless"] = new Dictionary<string, object> { ["survivalDays"] = days, ["moveAfterDays"] = HomelessHouses.MoveAfterDays, ["sameCulture"] = sameCulture == null ? null : sameCulture.Id, ["nearest"] = nearest == null ? null : nearest.Id };
    }

    // ---- statuses and census fields

    private static void Statuses(string repo)
    {
        var types = O(Load(Path.Combine(repo, "worldgen/policies/campaign-compatibility.json")), "types");
        Func<string, IDictionary<string, object>> t = name => O(types, "TaleWorlds.CampaignSystem.CampaignBehaviors." + name);
        Check(S(t("CompanionsCampaignBehavior"), "status") == "adapted" && S(t("CompanionsCampaignBehavior"), "reason").Contains("daily tick is replaced (MMR-281") &&
              S(t("HeroSpawnCampaignBehavior"), "status") == "retained" && S(t("HeroSpawnCampaignBehavior"), "reason").Contains("MMR-281") &&
              S(t("FactionDiscontinuationCampaignBehavior"), "status") == "retained" && S(t("FactionDiscontinuationCampaignBehavior"), "reason").Contains("homeless-house rule") &&
              S(t("InitialChildGenerationCampaignBehavior"), "status") == "retained" && S(t("InitialChildGenerationCampaignBehavior"), "reason").Contains("owner decision 15"),
              "campaign-compatibility.json: CompanionsCampaignBehavior adapted (daily tick replaced), HeroSpawnCampaignBehavior retained, FactionDiscontinuationCampaignBehavior retained with the homeless-house rule, " +
              "InitialChildGenerationCampaignBehavior retained (the random child generator stays on)");
        var patches = File.ReadAllText(Path.Combine(repo, "src/MNBL.ProceduralWorld/Campaign/GeneratedPatches.cs"));
        var behaviour = File.ReadAllText(Path.Combine(repo, "src/MNBL.ProceduralWorld/Campaign/GeneratedPopulationBehavior.cs"));
        var soak = File.ReadAllText(Path.Combine(repo, "src/MNBL.ProceduralWorld/Automation/CampaignSoakProbe.cs"));
        var fields = new[] { "kingdoms", "kingdomWars", "warsByKingdom", "houses", "houseSizes", "adultLords", "engineChildren", "engineChildrenByHouse", "minorClans", "minorFactionHeroes",
                             "minorFactionMarkedAt", "wanderersTarget", "wanderers", "wanderersFree", "wanderersHired", "wanderersRotated", "wanderersToppedUp", "housesMoved",
                             // MMR-262 (F06): what MMR-247's c18 (fief owners) and c20 (companies at 4+, a company hired, house and kingdom changes) read.
                             "houseDetail", "housesEliminated", "fiefOwners", "minorClanDetail", "kingdomChanges" };
        var details = new[] { "[\"kind\"]", "[\"aliveHeroes\"]", "[\"flaggedHeroes\"]", "[\"underMercenaryService\"]", "[\"kingdom\"]", "[\"size\"]", "[\"engineChildren\"]", "[\"eliminated\"]" };
        Check(patches.Contains("Owner = \"MMR-281\"") && patches.Contains("CompanionsTickPatch.Apply(world, trace)") && fields.All(f => behaviour.Contains("[\"" + f + "\"]")) && details.All(behaviour.Contains) &&
              soak.Contains("[\"day0\"] = PopulationCensus()") && soak.Contains("population[\"later\"] = PopulationCensus()") && soak.Contains("context.Int(\"populationDays\", 0)"),
              "the Manifest lists the MMR-281 prefix with the world patches, and the soak probe's population census (day 0, and after populationDays=N for MMR-247) records " + String.Join(", ", fields) +
              " (per minor clan its kind, alive and flagged heroes, kingdom and mercenary service; per house its kingdom, size, engine children and elimination)");
        // The player's clan (the installed player_faction carries is_minor_faction) is neither a house nor a minor clan, and every kingdom change is
        // recorded before the homeless-house rule's early return, in its own guard.
        int record = behaviour.IndexOf("kingdomChanges.Add(", StringComparison.Ordinal), rule = behaviour.IndexOf("detail != ChangeKingdomAction.ChangeKingdomActionDetail.LeaveByKingdomDestruction", StringComparison.Ordinal);
        Check(behaviour.Contains("c.IsMinorFaction && !c.IsBanditFaction && c != Clan.PlayerClan") && behaviour.Contains("!h.Clan.IsMinorFaction || h.Clan == Clan.PlayerClan") &&
              record > 0 && rule > record && behaviour.Contains("KingdomChangeCap") && behaviour.IndexOf("catch (Exception error) { if (Problems.Count < 50) Problems.Add(\"kingdom change record", StringComparison.Ordinal) > record,
              "the census leaves the player's clan out of the minor clans and counts its heroes outside them; OnClanChangedKingdom records every change (capped, guarded) before the homeless-house rule returns");
    }
}
