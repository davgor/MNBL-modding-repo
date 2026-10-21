using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;
using HarmonyLib;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.CampaignRegistry;

// MMR-287 (findings §17 RT-28, RT-29, RT-32) content parity, without the game:
//   ContentParityTests REPO_DIR BUNDLE_A BUNDLE_B GAME_ROOT HARMONY_DIR OUT_JSON
//   * incidents: the transpiler's targets located in the installed TaleWorlds.CampaignSystem by IL content (incident ldstr id, first
//     delegate, IFaction.get_StringId + ldstr kingdom literal + String.op_Equality), the rewrite, the culture comparison and the
//     isolation of an unresolved condition (only its incident stays off);
//   * wanderers: the backstory-safe pool and its accessor, and a census of the companions HeroFactory makes on worlds A and B (safe
//     templates only, FWGC++ sex, an epithet in every name, distinct within a culture where possible, fallbacks recorded);
//   * hideouts: a census of the bandit culture against the cell's biome and coast on worlds A and B (GeneratedCampaignInitializer).
internal static class ContentParityTests
{
    private static int checks, failures;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static readonly Dictionary<string, object> Evidence = new Dictionary<string, object>();
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; Console.WriteLine((condition ? "ok    " : "FAIL  ") + label); }
    private static IDictionary<string, object> Parse(string text) { return (IDictionary<string, object>)Json.DeserializeObject(text.TrimStart('﻿')); }
    private static IDictionary<string, object> Load(string path) { return Parse(File.ReadAllText(path)); }
    private static IDictionary<string, object> O(IDictionary<string, object> o, string key) { return (IDictionary<string, object>)o[key]; }
    private static string S(IDictionary<string, object> o, string key) { object v; return o.TryGetValue(key, out v) ? v as string : null; }
    private static double D(IDictionary<string, object> o, string key) { return Convert.ToDouble(o[key]); }
    private static IEnumerable<IDictionary<string, object>> Records(IDictionary<string, object> o, string key) { return ((IEnumerable)o[key]).Cast<IDictionary<string, object>>(); }
    private static List<string> L(IDictionary<string, object> o, string key) { object v; return o.TryGetValue(key, out v) && v is IEnumerable && !(v is string) ? ((IEnumerable)v).Cast<object>().Select(x => Convert.ToString(x)).ToList() : new List<string>(); }
    private static string Sha(byte[] bytes) { using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }

    private static string[] probe;

    private static int Main(string[] args)
    {
        if (args.Length != 6) { Console.Error.WriteLine("usage: ContentParityTests REPO_DIR BUNDLE_A BUNDLE_B GAME_ROOT HARMONY_DIR OUT_JSON"); return 2; }
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
        try { Incidents(); }
        catch (Exception e) { Check(false, "unexpected exception (incidents): " + e); }
        try { Worlds(args[0], new[] { new KeyValuePair<string, string>("A", args[1]), new KeyValuePair<string, string>("B", args[2]) }); }
        catch (Exception e) { Check(false, "unexpected exception (worlds): " + e); }
        Evidence["schema"] = "mnbl.content-parity"; Evidence["version"] = 1; Evidence["ticket"] = "MMR-287";
        Evidence["checks"] = checks; Evidence["failures"] = failures;
        File.WriteAllText(args[5], Json.Serialize(Evidence), new UTF8Encoding(false));
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    // ---- incidents (RT-29) ----

    // An IFaction whose StringId and Culture are given (every other member returns its default).
    private sealed class FakeFaction : RealProxy
    {
        private readonly string id; private readonly object culture;
        public FakeFaction(string id, object culture) : base(typeof(TaleWorlds.CampaignSystem.IFaction)) { this.id = id; this.culture = culture; }
        public override IMessage Invoke(IMessage msg)
        {
            var call = (IMethodCallMessage)msg;
            var method = (MethodInfo)call.MethodBase;
            object value = method.Name == "get_StringId" ? id : method.Name == "get_Culture" ? culture :
                           method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null;
            return new ReturnMessage(value, null, 0, call.LogicalCallContext, call);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Incidents()
    {
        var behaviour = typeof(TaleWorlds.CampaignSystem.IFaction).Assembly.GetType(IncidentCulturePatch.BehaviourType);
        Check(behaviour != null, "the installed TaleWorlds.CampaignSystem has " + IncidentCulturePatch.BehaviourType);
        var resolutions = IncidentCulturePatch.Resolve(behaviour, IncidentCulturePatch.Instructions);
        var stringId = IncidentCulturePatch.InstalledFactionStringId;
        var rows = new List<object>();
        foreach (var r in resolutions)
        {
            var code = r.Condition == null ? new List<CodeInstruction>() : IncidentCulturePatch.Instructions(r.Condition);
            var labels = code.Sum(i => i.labels.Count);
            int count = code.Count;
            int rewritten = IncidentCulturePatch.Rewrite(code, r);
            bool clean = r.Resolved && rewritten == r.Incident.Literals.Length && code.Count == count && code.Sum(i => i.labels.Count) == labels &&
                         r.Sites.All(i => code[i].Calls(IncidentCulturePatch.FactionCultureIdMethod)) &&
                         !code.Where((c, i) => i + 1 < code.Count && c.Calls(stringId) && code[i + 1].opcode == OpCodes.Ldstr && r.Incident.Literals.Contains(code[i + 1].operand as string)).Any();
            Check(clean, r.Incident.Name + " (" + r.Incident.Id + "): its condition is the first delegate after the incident id (" + (r.Condition == null ? "none" : r.Condition.DeclaringType.Name + "." + r.Condition.Name) +
                         "), its " + r.Incident.Literals.Length + " comparison(s) with [" + String.Join(", ", r.Incident.Literals) + "] are located by IL content and rewritten to FactionCultureId, labels and length kept" +
                         (r.Resolved ? "" : "; problem: " + r.Problem));
            rows.Add(new Dictionary<string, object> { ["incident"] = r.Incident.Id, ["name"] = r.Incident.Name, ["literals"] = r.Incident.Literals, ["resolved"] = r.Resolved,
                                                       ["condition"] = r.Condition == null ? null : r.Condition.DeclaringType.Name + "." + r.Condition.Name, ["sites"] = r.Sites, ["problem"] = r.Problem });
        }
        Check(resolutions.Count == 4 && resolutions.All(r => r.Resolved) && resolutions.Select(r => r.Condition).Distinct().Count() == 4, "all four incident conditions resolve, each to its own method");

        // The Harmony entry point: a planned condition handed over by Harmony is rewritten exactly once; an unplanned method is untouched.
        foreach (var r in resolutions.Where(x => x.Resolved)) IncidentCulturePatch.Plan(r);
        int before = IncidentCulturePatch.Rewritten;
        var deer = resolutions.First(r => r.Incident.Id == "incident_the_deer_departed");
        var handed = IncidentCulturePatch.Transpiler(IncidentCulturePatch.Instructions(deer.Condition), deer.Condition).ToList();
        var other = IncidentCulturePatch.Transpiler(IncidentCulturePatch.Instructions(behaviour.GetMethod("RegisterIncident", BindingFlags.Instance | BindingFlags.NonPublic) ?? deer.Condition), null).ToList();
        Check(IncidentCulturePatch.Rewritten - before == 1 && handed.Count(i => i.Calls(IncidentCulturePatch.FactionCultureIdMethod)) == 1 && !other.Any(i => i.Calls(IncidentCulturePatch.FactionCultureIdMethod)),
              "the transpiler re-locates and rewrites the planned condition Harmony hands over, and leaves other methods alone");

        // An unresolved condition only leaves its own incident off.
        Func<MethodBase, List<CodeInstruction>> renamed = m => {
            var code = IncidentCulturePatch.Instructions(m);
            if (m == deer.Condition) foreach (var i in code.Where(i => i.opcode == OpCodes.Ldstr && (i.operand as string) == "khuzait")) i.operand = "khuzait_khanate";
            return code; };
        var mutated = IncidentCulturePatch.Resolve(behaviour, renamed);
        Func<MethodBase, List<CodeInstruction>> unregistered = m => IncidentCulturePatch.Instructions(m).Where(i => !(i.opcode == OpCodes.Ldstr && (i.operand as string) == "incident_kannic_splendors")).ToList();
        var missing = IncidentCulturePatch.Resolve(behaviour, unregistered);
        var absent = IncidentCulturePatch.Resolve(null, IncidentCulturePatch.Instructions);
        Check(mutated.Count(r => !r.Resolved) == 1 && !mutated.First(r => r.Incident.Id == "incident_the_deer_departed").Resolved &&
              missing.Count(r => !r.Resolved) == 1 && !missing.First(r => r.Incident.Id == "incident_kannic_splendors").Resolved && absent.All(r => !r.Resolved) &&
              IncidentCulturePatch.Rewrite(IncidentCulturePatch.Instructions(deer.Condition), mutated.First(r => r.Incident.Id == "incident_the_deer_departed")) == 0 &&
              IncidentCulturePatch.Unresolved(mutated.First(r => !r.Resolved)).Contains("stays off"),
              "an unresolved condition disables only its incident: a changed kingdom literal (" + mutated.First(r => !r.Resolved).Problem + ") and a missing incident id (" +
              missing.First(r => !r.Resolved).Problem + ") each leave one condition unpatched and logged, the other three resolve; no behaviour type leaves all four off");

        // The comparison: the faction's culture id, so a generated kingdom of a vanilla culture matches the literal its kingdom id never does.
        var sturgian = new TaleWorlds.CampaignSystem.CultureObject { StringId = "sturgia" };
        var generated = (TaleWorlds.CampaignSystem.IFaction)new FakeFaction("mnbl_1609fb72_k_3b5d0c11aa", sturgian).GetTransparentProxy();
        var vanilla = (TaleWorlds.CampaignSystem.IFaction)new FakeFaction("sturgia", sturgian).GetTransparentProxy();
        var cultureless = (TaleWorlds.CampaignSystem.IFaction)new FakeFaction("mnbl_x_k_1", null).GetTransparentProxy();
        Check(IncidentCulturePatch.FactionCultureId(generated) == "sturgia" && generated.StringId != "sturgia" && IncidentCulturePatch.FactionCultureId(vanilla) == "sturgia" &&
              IncidentCulturePatch.FactionCultureId(cultureless) == null && IncidentCulturePatch.FactionCultureId(null) == null,
              "FactionCultureId: a generated kingdom of the sturgia culture compares as \"sturgia\" (its id " + generated.StringId + " never does), a vanilla kingdom keeps matching, no culture or no faction gives null");
        Evidence["incidents"] = rows;
    }

    // ---- wanderers (RT-32) and hideouts (RT-28) on worlds A and B ----

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Worlds(string repo, IList<KeyValuePair<string, string>> bundles)
    {
        var rosters = Load(Path.Combine(repo, "worldgen/policies/rosters.json"));
        var population = Load(Path.Combine(repo, "worldgen/policies/population.json"));
        var economy = Load(Path.Combine(repo, "worldgen/policies/economy.json"));
        var startup = Load(Path.Combine(repo, "worldgen/policies/startup.json"));
        var prototypes = Load(Path.Combine(repo, "worldgen/templates/settlements/prototypes.json"));
        var census = Load(Path.Combine(repo, S(O(population, "wanderers"), "backstoryCensus")));
        var templates = Directory.GetFiles(Path.Combine(repo, "worldgen/templates/cultures"), "*.json").Where(f => Path.GetFileName(f) != "contract.json")
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => Load(f), StringComparer.Ordinal);
        var receipt = Load(Path.Combine(repo, S(O(rosters, "provenance"), "catalogReceipt")));
        var catalog = new Dictionary<string, XmlDocument>(StringComparer.Ordinal);
        foreach (var d in Records(O(receipt, "outputs"), "documents"))
        {
            var bytes = File.ReadAllBytes(S(d, "path"));
            if (Sha(bytes) != S(d, "sha256")) throw new InvalidOperationException("catalog " + S(d, "id") + " differs from its receipt");
            var doc = new XmlDocument { XmlResolver = null }; doc.LoadXml(new UTF8Encoding(false).GetString(bytes).TrimStart('﻿')); catalog[S(d, "id")] = doc;
        }
        var defaultsRun = Load(Path.Combine(repo, S(O(rosters, "provenance"), "defaultReferences")));
        var defaults = L(O(Records(O(defaultsRun, "outputs"), "worlds").First(), "registration"), "verifiedLiveDefaultReferences");
        var resolver = new CharacterTemplateResolver(catalog, defaults, O(rosters, "troops"));

        // The pool and its accessor.
        var pool = WandererPool.FromPolicy(population);
        var rows = Records(census, "rows").ToDictionary(r => S(r, "id"), StringComparer.Ordinal);
        var unsafeIds = L(O(population, "wanderers"), "backstoryUnsafe");
        Check(pool.All.Count() == 78 && unsafeIds.Count == L(census, "unsafe").Count && unsafeIds.All(id => rows[id]["unsafe"] is bool && (bool)rows[id]["unsafe"]) &&
              pool.Safe.All(t => !(bool)rows[t.Id]["unsafe"] && ((IEnumerable)rows[t.Id]["hits"]).Cast<object>().Count() == 0) && pool.Unsafe.SequenceEqual(unsafeIds.OrderBy(x => x, StringComparer.Ordinal)) &&
              pool.All.All(t => !String.IsNullOrWhiteSpace(t.Epithet)),
              "the pool is the census: " + pool.Safe.Count() + " of " + pool.All.Count() + " installed templates are backstory-safe (" + unsafeIds.Count + " name a Calradian proper noun), every template has an epithet");
        var uses = new Dictionary<string, int>(StringComparer.Ordinal);
        var drawn = new List<WandererPool.Choice>();
        foreach (var culture in WandererPool.Cultures) foreach (var female in new[] { true, false }) for (int i = 0; i < 6; i++) drawn.Add(pool.Pick(culture, female, uses, null, n => i % n));
        var nordWoman = pool.Pick("nord", true, new Dictionary<string, int>(), null, n => 0);
        var empireWomen = Enumerable.Range(0, 3).Select(i => (WandererPool.Choice)null).ToList();
        var empireUses = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < 3; i++) empireWomen[i] = pool.Pick("empire", true, empireUses, null, n => 0);
        int empireSafeWomen = pool.Safe.Count(t => t.Culture == "empire" && t.Female);
        Check(drawn.All(c => c != null && pool.IsSafe(c.Template.Id)) && nordWoman != null && nordWoman.Template.Culture == pool.Fallback("nord").First(c => pool.Safe.Any(t => t.Culture == c && t.Female)) &&
              nordWoman.Fallback.StartsWith("crossCulture:") && empireWomen.Take(empireSafeWomen).All(c => c.Fallback == null) && empireWomen.Skip(empireSafeWomen).All(c => c.Fallback == "repeat") &&
              pool.Pick("empire", true, null, id => false, n => 0) == null,
              "Pick: 84 draws over every culture and sex return only safe templates; nord (no safe template) falls back to " + nordWoman.Template.Culture + " (" + nordWoman.Fallback + "); the " + empireSafeWomen +
              " safe empire women's templates are used before one repeats (recorded); nothing usable gives null");
        Check(WandererPool.NameWithEpithet("Ulkdurn Wyrmward", "Wyrmward", "the Scholar") == "Ulkdurn Wyrmward" && WandererPool.NameWithEpithet("Ulkdurn", null, "the Scholar") == "Ulkdurn the Scholar" &&
              WandererPool.NameWithEpithet("Ulkdurn", "Wyrmward", "the Scholar") == "Ulkdurn Wyrmward" && WandererPool.HasEpithet("Taelkelev of the Water Line", "of the Water Line") &&
              !WandererPool.HasEpithet("the Scholar", "the Scholar") && !WandererPool.HasEpithet("Ulkdurn", "the Scholar"),
              "NameWithEpithet keeps the source's own epithet, adds it when given apart, and adds the template's (\"the Scholar\") when the source has none");

        var worlds = new Dictionary<string, object>();
        foreach (var w in bundles)
        {
            var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", WorldDataBundle.HostCapabilities);
            var bundle = WorldDataBundle.Open(w.Value, host);
            Func<string, IDictionary<string, object>> read = path => Parse(new UTF8Encoding(false).GetString(bundle.ReadVerified(path)));
            var campaign = read("compiled/campaign/campaign.json"); var idMap = read("compiled/campaign/id-map.json");
            var travel = read("compiled/travel/travel-graph.json"); var ports = read("compiled/travel/ports.json");
            var cells = bundle.ReadVerified("compiled/travel/cells.bin");
            var biomes = GeneratedCampaignInitializer.CellBiomes(bundle.ReadVerified("compiled/terrain/masks.bin"), new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/terrain/projection.json")),
                new UTF8Encoding(false).GetString(bundle.ReadVerified("policies/terrain-materials.json")));
            var factions = FactionFactory.Build(campaign, idMap, Load(Path.Combine(repo, "worldgen/policies/factions.json")), Load(Path.Combine(repo, "worldgen/policies/culture-presentations.json")), templates, Load(Path.Combine(repo, "worldgen/policies/population.json")));
            var heroes = HeroFactory.Build(campaign, idMap, factions, rosters, resolver, population);
            var factory = new SettlementFactory(economy, prototypes, catalog["Settlements"], resolver.Exists);
            var world = factory.Build(campaign, travel, ports, factions, heroes);
            var start = new GeneratedCampaignInitializer(startup, factory, catalog["Settlements"], catalog["Factions"], catalog["SPCultures"]).Build(travel, ports, cells, factions, heroes, world, biomes);
            worlds[w.Key] = new Dictionary<string, object> { ["bundle"] = Path.GetFileName(w.Value.TrimEnd('\\', '/')), ["wanderers"] = WandererCensus(w.Key, campaign, heroes, pool, rows),
                                                             ["hideouts"] = HideoutCensus(w.Key, startup, start, travel, cells, biomes) };
        }
        Evidence["pool"] = new Dictionary<string, object> { ["templates"] = pool.All.Count(), ["safe"] = pool.Safe.Count(), ["unsafe"] = pool.Unsafe.ToList(),
            ["safeByCulture"] = WandererPool.Cultures.ToDictionary(c => c, c => (object)new Dictionary<string, object> { ["female"] = pool.Safe.Count(t => t.Culture == c && t.Female), ["male"] = pool.Safe.Count(t => t.Culture == c && !t.Female) }) };
        Evidence["worlds"] = worlds;
    }

    private static object WandererCensus(string label, IDictionary<string, object> campaign, HeroFactory.Result heroes, WandererPool pool, Dictionary<string, IDictionary<string, object>> rows)
    {
        var source = Records(campaign, "heroes").ToDictionary(h => S(h, "id"), StringComparer.Ordinal);
        var planned = Records(campaign, "wanderers").ToDictionary(x => S(x, "hero"), StringComparer.Ordinal);
        var companions = heroes.Heroes.Where(h => h.Occupation == "Wanderer").ToList();
        var choices = heroes.Wanderers.ToDictionary(c => c.Id, StringComparer.Ordinal);
        int W = Convert.ToInt32(O(campaign, "census")["wanderers"]);
        var unsafeHits = companions.Where(h => !pool.IsSafe(h.Template) || (bool)rows[h.Template]["unsafe"]).Select(h => h.Id).ToList();
        var noEpithet = companions.Where(h => !WandererPool.HasEpithet(h.SourceName, choices[h.Id].Epithet) || !h.Name.Contains(choices[h.Id].Epithet)).Select(h => h.SourceName).ToList();
        var wrongSex = companions.Where(h => source[h.Id].ContainsKey("female") && (bool)source[h.Id]["female"] != h.Female).Select(h => h.Id).ToList();
        var fallbacks = heroes.Wanderers.Where(c => c.Fallback != null && c.Fallback.Contains("crossCulture")).Select(c => c.Culture + (c.Female ? " woman" : " man") + " -> " + c.Template).ToList();
        // Distinct within a culture where possible: a repeat only once every safe template of that culture and sex is in use.
        var badRepeats = heroes.Wanderers.GroupBy(c => c.TemplateCulture + "|" + c.Female).Where(g => g.Select(c => c.Template).Distinct().Count() <
                         Math.Min(g.Count(), pool.Safe.Count(t => t.Culture + "|" + t.Female == g.Key))).Select(g => g.Key).ToList();
        Check(companions.Count == W && companions.Count == planned.Count && unsafeHits.Count == 0 && noEpithet.Count == 0 && wrongSex.Count == 0 && badRepeats.Count == 0 &&
              heroes.Wanderers.Where(c => c.Fallback != null && c.Fallback.Contains("crossCulture")).All(c => !pool.Safe.Any(t => t.Culture == c.Culture && t.Female == c.Female)),
              "world " + label + ": " + companions.Count + " generated wanderers (W " + W + ") use only backstory-safe templates (" + companions.Select(h => h.Template).Distinct().Count() + " distinct), keep their FWGC++ sex, and every name keeps an epithet (e.g. " +
              String.Join(", ", companions.Take(3).Select(h => "\"" + h.SourceName + "\"")) + "); cross-culture fallbacks only where the culture has no safe template of that sex: " + (fallbacks.Count == 0 ? "none" : String.Join(", ", fallbacks)) +
              (unsafeHits.Count + noEpithet.Count + wrongSex.Count + badRepeats.Count == 0 ? "" : "; unsafe " + String.Join(",", unsafeHits) + " no epithet " + String.Join(",", noEpithet) + " sex " + String.Join(",", wrongSex) + " repeats " + String.Join(",", badRepeats)));
        return new Dictionary<string, object> {
            ["count"] = companions.Count, ["target"] = W, ["unsafeTemplates"] = unsafeHits.Count, ["withoutEpithet"] = noEpithet.Count, ["sexMismatch"] = wrongSex.Count,
            ["distinctTemplates"] = companions.Select(h => h.Template).Distinct().Count(), ["repeats"] = heroes.Wanderers.Count(c => c.Fallback != null && c.Fallback.Contains("repeat")), ["crossCulture"] = fallbacks,
            ["rows"] = heroes.Wanderers.OrderBy(c => c.Id, StringComparer.Ordinal).Select(c => new Dictionary<string, object> { ["id"] = c.Id, ["name"] = c.Name, ["culture"] = c.Culture, ["female"] = c.Female,
                ["template"] = c.Template, ["templateCulture"] = c.TemplateCulture, ["epithet"] = c.Epithet, ["fallback"] = c.Fallback }).ToList() };
    }

    private static object HideoutCensus(string label, IDictionary<string, object> startup, GeneratedCampaignInitializer.Result start, IDictionary<string, object> travel, byte[] cells,
                                        Func<double, double, string> biomes)
    {
        var hb = O(O(startup, "hideouts"), "biome");
        var listed = O(hb, "biomeWeights");
        var map = new GeneratedSpawnModel.CellMap(O(travel, "grid"), cells, L(O(startup, "passable"), "classes"), Records(travel, "settlements"));
        // Ground cultures of a hideout: those its biome lists, plus the coast culture on a coast cell.
        Func<GeneratedCampaignInitializer.HideoutGround, HashSet<string>> groundOf = g => {
            var set = new HashSet<string>(listed.ContainsKey(g.Biome ?? "") ? O(listed, g.Biome).Keys : Enumerable.Empty<string>(), StringComparer.Ordinal);
            if (g.Coast) set.Add(S(hb, "coastCulture"));
            return set; };
        var drawn = start.Hideouts.Where(h => !h.Provenance.Contains(" replaced by ")).ToList();
        int positive = drawn.Count(h => start.HideoutGrounds[h.Id].Weights.ContainsKey(h.Culture) && start.HideoutGrounds[h.Id].Weights[h.Culture] > 0);
        int consistent = drawn.Count(h => { var g = start.HideoutGrounds[h.Id]; var cell = map.Index(h.X, h.Y);
                                            return g.Biome == biomes(h.X, h.Y) && g.Coast == map.Near(cell / map.Cols, cell % map.Cols, (int)D(hb, "coastCells"), "sea"); });
        var grounded = drawn.Where(h => groundOf(start.HideoutGrounds[h.Id]).Count > 0).ToList();
        int matched = grounded.Count(h => groundOf(start.HideoutGrounds[h.Id]).Contains(h.Culture));
        double expected = grounded.Sum(h => { var g = start.HideoutGrounds[h.Id]; var set = groundOf(g); return g.Weights.Where(kv => set.Contains(kv.Key)).Sum(kv => kv.Value); });
        double variance = grounded.Sum(h => { var g = start.HideoutGrounds[h.Id]; var set = groundOf(g); double p = g.Weights.Where(kv => set.Contains(kv.Key)).Sum(kv => kv.Value); return p * (1 - p); });
        // The installed rule alone (nearest town culture's weights, share 0) as the baseline the biome term improves on.
        var weights = O(O(startup, "hideouts"), "cultureWeights");
        double baseline = grounded.Sum(h => { var g = start.HideoutGrounds[h.Id]; var set = groundOf(g);
                                              var town = GeneratedCampaignInitializer.GroundWeights(weights.ContainsKey(g.TownCulture ?? "") ? O(weights, g.TownCulture) : null, hb, "__none__", false);
                                              return town.Where(kv => set.Contains(kv.Key)).Sum(kv => kv.Value); });
        var byBiome = drawn.GroupBy(h => (start.HideoutGrounds[h.Id].Biome ?? "unknown") + (start.HideoutGrounds[h.Id].Coast ? "+coast" : "")).OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (object)g.GroupBy(h => h.Culture).OrderBy(c => c.Key, StringComparer.Ordinal).ToDictionary(c => c.Key, c => (object)c.Count()));
        Check(drawn.Count > 0 && positive == drawn.Count && consistent == drawn.Count && grounded.Count > 0 && matched >= expected - 3 * Math.Sqrt(variance) && expected > baseline,
              "world " + label + ": all " + drawn.Count + " drawn hideouts take a culture their mixed weights give (biome and coast re-read from the bundle for each); on the " + grounded.Count +
              " with listed ground, " + matched + " take a ground culture (policy expectation " + Math.Round(expected, 1) + " +- " + Math.Round(3 * Math.Sqrt(variance), 1) + ", the nearest-town rule alone " + Math.Round(baseline, 1) + "); " +
              (start.Hideouts.Count - drawn.Count) + " required-culture replacements");
        return new Dictionary<string, object> {
            ["hideouts"] = start.Hideouts.Count, ["drawn"] = drawn.Count, ["requiredReplacements"] = start.Hideouts.Count - drawn.Count, ["withListedGround"] = grounded.Count,
            ["groundMatched"] = matched, ["groundExpected"] = Math.Round(expected, 3), ["groundExpected3Sigma"] = Math.Round(3 * Math.Sqrt(variance), 3), ["nearestTownRuleExpected"] = Math.Round(baseline, 3),
            ["coast"] = drawn.Count(h => start.HideoutGrounds[h.Id].Coast), ["byGround"] = byBiome,
            ["rows"] = start.Hideouts.OrderBy(h => h.Id, StringComparer.Ordinal).Select(h => { var g = start.HideoutGrounds[h.Id]; return new Dictionary<string, object> { ["id"] = h.Id, ["culture"] = h.Culture,
                ["biome"] = g.Biome, ["coast"] = g.Coast, ["townCulture"] = g.TownCulture, ["weights"] = g.Weights.ToDictionary(kv => kv.Key, kv => (object)Math.Round(kv.Value, 4)),
                ["replaced"] = h.Provenance.Contains(" replaced by ") }; }).ToList() };
    }
}
