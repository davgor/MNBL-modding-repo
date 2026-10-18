using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.Generation;

namespace MNBL.ProceduralWorld.UI
{
    // MMR-43: the logic of the New Campaign setup screen (docs/world-setup.md), pure managed and tested offline; WorldSetupViewModel shows
    // it and WorldSetupScreen hosts it.
    //   * WorldSetupContract: the F03 one-shot profile (worldgen/profiles/one-shot.json), the pinned producer's published controls
    //     (worldgen/contracts/producer-controls.json), seed width (worldgen/contracts/producer-seed-width.json) and lock
    //     (worldgen/producer.lock.json), the product policy, the culture presentation policy and the waiting-time budget
    //     (worldgen/policies/one-shot-budget.json), all embedded in the module.
    //   * Seed: exact text, the worldbridge rule (decimal digits only, no sign, space or separator, no leading zero except "0", at most the
    //     producer's verified maximum). Random World draws from the operating system's cryptographic generator; no account or network.
    //   * Settings: only controls the pinned producer publishes, each within its published range; every one the profile fixes is shown with
    //     why it cannot be changed. World size is a content control, never presented as quality.
    //   * WorldChoice: what the player chose, the player's fields of the generation request MMR-44 builds.
    //   * The result: the generated world's own peoples, each with the official culture it is shown through (culture-presentations.json),
    //     which are the cultures character creation offers for this world (MMR-32).
    //   * The generation job (IWorldGenerationRunner: MMR-44's GenerationCoordinator) and the world summary run off the engine thread; its
    //     progress events are queued and Tick takes them on the engine thread, only the current job's and only in order (ProgressSink).
    internal static class WorldSetupText
    {
        public const string Menu = "{=mnbl_ws_menu}New Procedural Campaign";
        public const string Busy = "{=mnbl_ws_busy}A world is being generated. Wait for it to finish.";
        public const string Title = "{=mnbl_ws_title}A New World";
        public const string Profile = "{=mnbl_ws_profile}First-test world: one generated globe, played as the Sandbox campaign.";
        public const string Coverage = "{=mnbl_ws_coverage}The whole globe is generated and shown as one flat map that wraps from east to west. There is no region to choose.";
        public const string SeedLabel = "{=mnbl_ws_seed}Seed";
        public const string SeedHint = "{=mnbl_ws_seed_hint}A number from 0 to {MAX}. The same seed always makes the same world.";
        public const string RandomWorld = "{=mnbl_ws_random}Random World";
        public const string Generate = "{=mnbl_ws_generate}Generate";
        public const string Back = "{=mnbl_ws_back}Back";
        public const string Cancel = "{=mnbl_ws_cancel}Cancel";
        public const string Settings = "{=mnbl_ws_settings}World settings";
        public const string Wait = "{=mnbl_ws_wait}Generating the world takes about {GENERATE} seconds.";
        public const string WaitStart = "{=mnbl_ws_wait_start}The first start of its campaign takes about {START} minutes more while the world's navigation is prepared; the game may stop responding until it is done.";
        public const string Diagnostics = "{=mnbl_ws_diagnostics}Diagnostics";
        public const string Generated = "{=mnbl_ws_generated}World generated from seed {SEED}: {CITIES} cities of {PEOPLE_COUNT} peoples.";
        public const string Peoples = "{=mnbl_ws_peoples}Peoples of this world, with the culture each is shown as: {PEOPLES}";
        public const string People = "{=mnbl_ws_people}{NAME} ({CULTURE})";
        public const string Creation = "{=mnbl_ws_creation}In character creation you choose among these cultures: {CULTURES}.";
        // MMR-46: the preview, Start Campaign, New Seed.
        public const string StartCampaign = "{=mnbl_ws_start}Start Campaign";
        public const string NewSeed = "{=mnbl_ws_new_seed}New Seed";
        public const string Starting = "{=mnbl_ws_starting}Starting the campaign in this world...";
        public const string Withdrawn = "{=mnbl_ws_withdrawn}You left character creation. This world is still ready: start it again, or choose a new seed.";
        public const string PreviewTitle = "{=mnbl_ws_preview_title}Seed {SEED}: the world you will play";
        public const string CompiledCounts = "{=mnbl_ws_compiled}{TOWNS} towns, {CASTLES} castles, {VILLAGES} villages, {PORTS} ports; {KINGDOMS} kingdoms and {CLANS} clans; {CITIES} cities of {PEOPLE_COUNT} peoples in the source world.";
        public const string Legend = "{=mnbl_ws_legend}Large light square: town. Light square with a dark centre: castle. Small dark dot: village; with a blue edge, a harbour village where fleets land. Blue-edged square: a town's port. Tinted areas are kingdoms, edged by their borders. Point at a place, or use Previous and Next place, to see its full name.";
        public const string PreviewTextureFailed = "{=mnbl_ws_preview_texture}The map picture could not be drawn; the places are shown on a plain field.";
        // One whole sentence per kind (a spliced-in noun cannot agree in other languages).
        public const string PlaceTown = "{=mnbl_ws_place_town}{NAME}: a town of {KINGDOM}";
        public const string PlaceCastle = "{=mnbl_ws_place_castle}{NAME}: a castle of {KINGDOM}";
        public const string PlaceVillage = "{=mnbl_ws_place_village}{NAME}: a village of {KINGDOM}";
        public const string PlaceHarbourVillage = "{=mnbl_ws_place_harbour}{NAME}: a harbour village of {KINGDOM}";
        public const string PlaceHarbourVillageFree = "{=mnbl_ws_place_harbour_free}{NAME}: a harbour village of no kingdom";
        public const string PlacePort = "{=mnbl_ws_place_port}{NAME}: the port of a town of {KINGDOM}";
        public const string PlaceTownFree = "{=mnbl_ws_place_town_free}{NAME}: a town of no kingdom";
        public const string PlaceCastleFree = "{=mnbl_ws_place_castle_free}{NAME}: a castle of no kingdom";
        public const string PlaceVillageFree = "{=mnbl_ws_place_village_free}{NAME}: a village of no kingdom";
        public const string PlacePortFree = "{=mnbl_ws_place_port_free}{NAME}: the port of a town of no kingdom";
        public const string KingdomHover = "{=mnbl_ws_kingdom_hover}The kingdom of {KINGDOM}";
        // MMR-47: the reference of a failure, its details, and an unfinished world from an earlier session.
        public const string ErrorReference = "{=mnbl_ws_error_reference}Reference {ID} (include it when you report this).";
        public const string ErrorDetails = "{=mnbl_ws_error_details}Details";
        public const string Unfinished = "{=mnbl_ws_unfinished}A world from an earlier session was not finished (seed {SEED}). You can continue it or discard it.";
        public const string UnfinishedOther = "{=mnbl_ws_unfinished_other}A world from an earlier session was left behind (seed {SEED}) and cannot be continued: it was made by another version of the world generator. You can discard it.";
        public const string UnfinishedCommitting = "{=mnbl_ws_unfinished_committing}A world from an earlier session stopped while its campaign was starting (seed {SEED}). Its campaign was recovered or undone when the game started; this leftover can only be discarded.";
        public const string UnfinishedUnreadable = "{=mnbl_ws_unfinished_unreadable}A world from an earlier session was left behind, but what it was cannot be read. You can discard it.";
        public const string DiscardRefused = "{=mnbl_ws_discard_refused}It could not be discarded ({REASON}).";
        public const string UnfinishedMore = "{=mnbl_ws_unfinished_more}{COUNT} more after this one.";
        public const string Resume = "{=mnbl_ws_resume}Continue it";
        public const string Discard = "{=mnbl_ws_discard}Discard it";
        public const string PreviousPlace = "{=mnbl_ws_previous_place}Previous place";
        public const string NextPlace = "{=mnbl_ws_next_place}Next place";
        public const string PreviewFailed = "{=mnbl_ws_preview_failed}The world map preview could not be made. The world is not offered for play; generate it again.";
        public const string ReuseWorld = "{=mnbl_ws_reuse}The world for seed {SEED} is still ready: Generate shows it again without making it twice.";
        public const string Generating = "{=mnbl_ws_generating}Generating seed {SEED}...";
        public const string Cancelling = "{=mnbl_ws_cancelling}Cancelling...";
        public const string SeedEmpty = "{=mnbl_ws_seed_empty}Enter a seed, or choose Random World.";
        public const string SeedDigits = "{=mnbl_ws_seed_digits}Write the seed with the digits 0 to 9 only: no spaces, signs or separators.";
        public const string SeedZero = "{=mnbl_ws_seed_zero}Write the seed without leading zeros.";
        public const string SeedLarge = "{=mnbl_ws_seed_large}The largest seed is {MAX}.";
        public const string SizeName = "{=mnbl_ws_size}World size";
        public const string SizeLocked = "{=mnbl_ws_size_locked}Fixed for this world. In this generator a different size makes a different world, with more or fewer settlements, not the same world in more detail. The generator accepts {MIN} to {MAX}.";
        public const string PhaseName = "{=mnbl_ws_phase}Generation stages";
        public const string PhaseValue = "{=mnbl_ws_phase_value}{VALUE} of {MAX}";
        public const string PhaseLocked = "{=mnbl_ws_phase_locked}A campaign needs the complete world, so every stage runs.";
        // The official cultures a generated people can be shown through (culture-presentations.json officialCultures). There is no game,
        // so no CultureObject, on the main menu: the names are the setup's own.
        public const string CultureEmpire = "{=mnbl_ws_culture_empire}Empire";
        public const string CultureAserai = "{=mnbl_ws_culture_aserai}Aserai";
        public const string CultureSturgia = "{=mnbl_ws_culture_sturgia}Sturgia";
        public const string CultureVlandia = "{=mnbl_ws_culture_vlandia}Vlandia";
        public const string CultureBattania = "{=mnbl_ws_culture_battania}Battania";
        public const string CultureKhuzait = "{=mnbl_ws_culture_khuzait}Khuzait";
        public const string CultureNord = "{=mnbl_ws_culture_nord}Nord";
        public static readonly Dictionary<string, string> CultureNames = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["empire"] = CultureEmpire, ["aserai"] = CultureAserai, ["sturgia"] = CultureSturgia, ["vlandia"] = CultureVlandia,
            ["battania"] = CultureBattania, ["khuzait"] = CultureKhuzait, ["nord"] = CultureNord };
        public static string Place(string kind, bool kingdom, bool harbour = false)
        {
            if (kind == "village" && harbour) return kingdom ? PlaceHarbourVillage : PlaceHarbourVillageFree;
            if (kind == "town") return kingdom ? PlaceTown : PlaceTownFree;
            if (kind == "castle") return kingdom ? PlaceCastle : PlaceCastleFree;
            if (kind == "port") return kingdom ? PlacePort : PlacePortFree;
            return kingdom ? PlaceVillage : PlaceVillageFree;
        }
        public static IEnumerable<string> All()
        {
            return typeof(WorldSetupText).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue());
        }
    }

    internal sealed class WorldSetupContract
    {
        public sealed class Control { public string Name; public long Min, Max, Default; public string Description; }
        public sealed class WaitPhase { public string Id; public double MaxSeconds; public string Source; }
        public string ProfileSchema;
        public uint DefaultSeed;
        public ulong MaxSeed;
        public int RecipeVersion, QualityAttempts, MaxQualityAttempts, RoundToSeconds;
        // ProducerLockSha256: sha256 of worldgen/producer.lock.json, the generation request's producerLockSha256. The profile's own
        // producerLockSha256 field is the tools lock it was resolved under (ProfileToolsLockSha256), not the request's.
        public string Coverage, ProducerLockSha256, ProfileToolsLockSha256, ProducerSha256, BudgetStatus;
        public bool WarSailsRequired;
        public SortedDictionary<string, long> Overrides = new SortedDictionary<string, long>(StringComparer.Ordinal);
        public Dictionary<string, Control> Controls = new Dictionary<string, Control>(StringComparer.Ordinal);
        public List<WaitPhase> Wait = new List<WaitPhase>();
        public Dictionary<string, string> Presentations = new Dictionary<string, string>(StringComparer.Ordinal);   // civilization -> official culture
        public List<string> OfficialCultures = new List<string>();
        public SortedDictionary<string, string> Sources = new SortedDictionary<string, string>(StringComparer.Ordinal);

        public static readonly string[] Documents = { "one-shot.json", "producer-controls.json", "producer-seed-width.json", "producer.lock.json", "product-policy.json",
                                                      "culture-presentations.json", "one-shot-budget.json" };

        // The documents embedded in the module (MNBL.ProceduralWorld.<name>).
        public static WorldSetupContract Installed()
        {
            var parts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var name in Documents)
                using (var stream = typeof(WorldSetupContract).Assembly.GetManifestResourceStream("MNBL.ProceduralWorld." + name))
                {
                    if (stream == null) throw new InvalidOperationException("The module has no embedded " + name + ".");
                    using (var copy = new MemoryStream()) { stream.CopyTo(copy); parts[name] = copy.ToArray(); }
                }
            return FromDocuments(parts);
        }

        public static WorldSetupContract FromDocuments(IDictionary<string, byte[]> documents)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            Func<string, IDictionary<string, object>> parse = name =>
            {
                byte[] bytes;
                if (!documents.TryGetValue(name, out bytes)) throw new InvalidDataException("The setup document " + name + " is missing.");
                return (IDictionary<string, object>)json.DeserializeObject(new UTF8Encoding(false).GetString(bytes).TrimStart('﻿'));
            };
            var p = parse("one-shot.json"); var c = parse("producer-controls.json"); var w = parse("producer-seed-width.json"); var lockDoc = parse("producer.lock.json");
            var policy = parse("product-policy.json"); var cultures = parse("culture-presentations.json"); var b2 = parse("one-shot-budget.json");
            if ((string)p["schema"] != "mnbl.diagnostic-profile" || (string)c["schema"] != "fantasy-world-generator.world-controls" || (string)w["schema"] != "mnbl.producer-seed-width"
                || (string)lockDoc["schema"] != "mnbl.producer-lock" || (string)policy["schema"] != "mnbl.product-policy" || (string)cultures["schema"] != "mnbl.culture-presentations"
                || (string)b2["schema"] != "mnbl.one-shot-budget")
                throw new InvalidDataException("A setup document has an unexpected schema.");
            var contract = new WorldSetupContract { ProfileSchema = (string)p["schema"] };
            var request = (IDictionary<string, object>)p["producerRequest"];
            contract.DefaultSeed = Convert.ToUInt32(request["seed"], CultureInfo.InvariantCulture);
            contract.RecipeVersion = Convert.ToInt32(request["recipe_version"], CultureInfo.InvariantCulture);
            foreach (var kv in (IDictionary<string, object>)request["overrides"]) contract.Overrides[kv.Key] = Convert.ToInt64(kv.Value, CultureInfo.InvariantCulture);
            contract.QualityAttempts = Convert.ToInt32(p["qualityAttempts"], CultureInfo.InvariantCulture);
            contract.Coverage = (string)p["coverage"];
            contract.ProfileToolsLockSha256 = (string)p["producerLockSha256"];
            if (Convert.ToInt32(c["recipe_version"], CultureInfo.InvariantCulture) != contract.RecipeVersion) throw new InvalidDataException("The producer controls are for another recipe version.");
            foreach (var kv in (IDictionary<string, object>)c["controls"])
            {
                var d = (IDictionary<string, object>)kv.Value;
                if ((string)d["type"] != "integer") continue;
                contract.Controls[kv.Key] = new Control { Name = kv.Key, Min = Convert.ToInt64(d["min"], CultureInfo.InvariantCulture), Max = Convert.ToInt64(d["max"], CultureInfo.InvariantCulture),
                    Default = Convert.ToInt64(d["default"], CultureInfo.InvariantCulture), Description = (string)d["description"] };
            }
            if (!(w["verified"] is bool) || !(bool)w["verified"]) throw new InvalidDataException("The producer seed width is not verified.");
            contract.MaxSeed = Convert.ToUInt64(w["maxSeed"], CultureInfo.InvariantCulture);
            if (contract.MaxSeed > UInt32.MaxValue) throw new InvalidDataException("The producer seed width exceeds the request's 32-bit seed.");
            contract.ProducerSha256 = (string)w["producerSha256"];
            if ((string)((IDictionary<string, object>)lockDoc["producer"])["sha256"] != contract.ProducerSha256)
                throw new InvalidDataException("The seed width was verified for another producer than the locked one.");
            contract.MaxQualityAttempts = Convert.ToInt32(policy["maxQualityAttempts"], CultureInfo.InvariantCulture);
            contract.WarSailsRequired = policy.ContainsKey("warSailsRequired") && policy["warSailsRequired"] is bool && (bool)policy["warSailsRequired"];
            if ((string)policy["coverage"] != contract.Coverage) throw new InvalidDataException("The profile's coverage differs from the product policy's.");
            contract.OfficialCultures = ((object[])cultures["officialCultures"]).Cast<string>().ToList();
            foreach (var entry in ((object[])cultures["civilizations"]).Cast<IDictionary<string, object>>())
            {
                string culture = (string)entry["culture"];
                if (!contract.OfficialCultures.Contains(culture)) throw new InvalidDataException("A civilization is shown through " + culture + ", which is not an official culture.");
                contract.Presentations[(string)entry["civilization"]] = culture;
            }
            foreach (var culture in contract.OfficialCultures)
                if (!WorldSetupText.CultureNames.ContainsKey(culture)) throw new InvalidDataException("The setup has no name for the official culture " + culture + ".");
            contract.BudgetStatus = (string)b2["status"];
            contract.RoundToSeconds = Convert.ToInt32(b2["roundToSeconds"], CultureInfo.InvariantCulture);
            foreach (var kv in (IDictionary<string, object>)b2["phases"])
            {
                var d = (IDictionary<string, object>)kv.Value;
                var seconds = ((object[])d["seconds"]).Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)).ToArray();
                contract.Wait.Add(new WaitPhase { Id = kv.Key, MaxSeconds = seconds.Max(), Source = (string)d["source"] });
            }
            // Only what the pinned producer publishes, within its published range, and what the product policy allows.
            foreach (var kv in contract.Overrides)
            {
                Control control;
                if (!contract.Controls.TryGetValue(kv.Key, out control)) throw new InvalidDataException("The profile sets " + kv.Key + ", which the pinned producer does not publish.");
                if (kv.Value < control.Min || kv.Value > control.Max) throw new InvalidDataException("The profile sets " + kv.Key + " = " + kv.Value + " outside the producer's " + control.Min + ".." + control.Max + ".");
            }
            if (contract.QualityAttempts < 1 || contract.QualityAttempts > contract.MaxQualityAttempts) throw new InvalidDataException("The profile's quality attempts exceed the product policy.");
            if (contract.DefaultSeed > contract.MaxSeed) throw new InvalidDataException("The profile's seed exceeds the producer's seed width.");
            using (var sha = SHA256.Create())
                foreach (var name in Documents) contract.Sources[name] = BitConverter.ToString(sha.ComputeHash(documents[name])).Replace("-", "").ToLowerInvariant();
            contract.ProducerLockSha256 = contract.Sources["producer.lock.json"];
            return contract;
        }
    }

    internal enum SeedProblem { None, Empty, NotDigits, LeadingZero, TooLarge }

    internal static class SeedText
    {
        // The worldbridge decimal rule (generation_driver.cpp decimal()): the whole text, digits only, no leading zero except "0", <= max.
        public static SeedProblem Parse(string text, ulong max, out uint seed)
        {
            seed = 0;
            if (String.IsNullOrEmpty(text)) return SeedProblem.Empty;
            if (text.Any(ch => ch < '0' || ch > '9')) return SeedProblem.NotDigits;
            if (text.Length > 1 && text[0] == '0') return SeedProblem.LeadingZero;
            ulong value = 0;
            foreach (char ch in text)
            {
                if (value > (UInt64.MaxValue - 9) / 10) return SeedProblem.TooLarge;
                value = value * 10 + (ulong)(ch - '0');
                if (value > max) return SeedProblem.TooLarge;
            }
            if (value > UInt32.MaxValue) return SeedProblem.TooLarge;
            seed = (uint)value;
            return SeedProblem.None;
        }
        public static string Reason(SeedProblem problem)
        {
            switch (problem)
            {
                case SeedProblem.Empty: return WorldSetupText.SeedEmpty;
                case SeedProblem.NotDigits: return WorldSetupText.SeedDigits;
                case SeedProblem.LeadingZero: return WorldSetupText.SeedZero;
                case SeedProblem.TooLarge: return WorldSetupText.SeedLarge;
                default: return "";
            }
        }
    }

    internal static class RandomWorld
    {
        // Uniform in [0, max] from the operating system's cryptographic generator (rejection sampling).
        public static uint Next(ulong max, RandomNumberGenerator rng)
        {
            if (max > UInt32.MaxValue) max = UInt32.MaxValue;
            ulong range = max + 1, limit = (1UL << 32) - ((1UL << 32) % range);
            var bytes = new byte[4];
            while (true)
            {
                rng.GetBytes(bytes);
                ulong value = BitConverter.ToUInt32(bytes, 0);
                if (value < limit) return (uint)(value % range);
            }
        }
    }

    internal sealed class WorldSetting
    {
        public string Id, Name, Value, Reason;
        public bool Locked;
        public Dictionary<string, string> Variables = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    internal static class WorldSettings
    {
        // The profile's producer controls: each fixed for the F03 profile, with why. A control the setup does not know how to explain is not
        // shown (and the contract refuses an override the producer does not publish).
        public static List<WorldSetting> For(WorldSetupContract c)
        {
            var settings = new List<WorldSetting>();
            long size;
            if (c.Overrides.TryGetValue("size", out size))
            {
                var control = c.Controls["size"];
                var s = new WorldSetting { Id = "size", Name = WorldSetupText.SizeName, Value = size.ToString(CultureInfo.InvariantCulture), Locked = true, Reason = WorldSetupText.SizeLocked };
                s.Variables["MIN"] = control.Min.ToString(CultureInfo.InvariantCulture); s.Variables["MAX"] = control.Max.ToString(CultureInfo.InvariantCulture);
                settings.Add(s);
            }
            long phase;
            if (c.Overrides.TryGetValue("phase", out phase))
            {
                var control = c.Controls["phase"];
                var s = new WorldSetting { Id = "phase", Name = WorldSetupText.PhaseName, Value = WorldSetupText.PhaseValue, Locked = true, Reason = WorldSetupText.PhaseLocked };
                s.Variables["VALUE"] = phase.ToString(CultureInfo.InvariantCulture); s.Variables["MAX"] = control.Max.ToString(CultureInfo.InvariantCulture);
                settings.Add(s);
            }
            return settings;
        }
    }

    internal static class WaitTime
    {
        // The budget's measured upper bound for generating the world, rounded up; without a measurement nothing is promised. MMR-56: the
        // first start of a new world's campaign (its navigation caches built, the initial save written) as its measured upper bound in whole
        // minutes, rounded up, once the budget holds a startCampaign measurement.
        public static Dictionary<string, string> Variables(WorldSetupContract c)
        {
            var vars = new Dictionary<string, string>(StringComparer.Ordinal);
            var phase = c.Wait.FirstOrDefault(p => p.Id == "generateWorld");
            if (phase != null)
            {
                double step = Math.Max(1, c.RoundToSeconds);
                vars["GENERATE"] = (Math.Ceiling(phase.MaxSeconds / step) * step).ToString("0", CultureInfo.InvariantCulture);
            }
            var start = c.Wait.FirstOrDefault(p => p.Id == "startCampaign");
            if (start != null) vars["START"] = Math.Max(1, Math.Ceiling(start.MaxSeconds / 60)).ToString("0", CultureInfo.InvariantCulture);
            return vars;
        }
        public static bool Available(WorldSetupContract c) { return Variables(c).ContainsKey("GENERATE"); }

        // The waiting text: generation, and the first campaign start when it is measured.
        public static string Text(WorldSetupContract c, Func<string, Dictionary<string, string>, string> render)
        {
            var vars = Variables(c);
            if (!vars.ContainsKey("GENERATE")) return "";
            string text = render(WorldSetupText.Wait, vars);
            return vars.ContainsKey("START") ? text + " " + render(WorldSetupText.WaitStart, vars) : text;
        }
    }

    // What the player chose: the hand-off to the generation job. RequestFields are the player-determined fields of the generation request
    // (worldgen/contracts/generation-request.schema.json), named and valued as the schema requires; MMR-44's GenerationCoordinator adds
    // schema, version and policies (the bound policy hashes) and writes the request.
    internal sealed class WorldChoice
    {
        public string CampaignInstanceId;
        public uint Seed;
        public string SeedSource;   // "explicit" (typed or replayed) or "random" (Random World, from the OS CSPRNG): the schema's enum
        public int RecipeVersion, MaxAttempts;
        public SortedDictionary<string, long> Overrides;
        public string ProducerLockSha256, Profile;
        // MMR-47: an unfinished job's directory to continue (Resume), or null for a new job.
        public string ResumeJobRoot;
        public Dictionary<string, object> RequestFields()
        {
            return new Dictionary<string, object> { ["campaignInstanceId"] = CampaignInstanceId, ["masterSeed"] = Seed, ["seedSource"] = SeedSource, ["recipeVersion"] = RecipeVersion,
                ["overrides"] = Overrides.ToDictionary(k => k.Key, k => (object)k.Value), ["maxAttempts"] = MaxAttempts, ["producerLockSha256"] = ProducerLockSha256 };
        }
    }

    // The generated source world's peoples and cities (world.json: humans.cores by population profile, which is the civilization id; named
    // by civilizations.entities), each with the official culture it is shown through.
    internal sealed class SourceWorldSummary
    {
        public sealed class People { public string Civilization, Name, Culture; public int Cities; }
        public List<People> Peoples = new List<People>();
        public List<string> Cultures = new List<string>();   // the official cultures the world is shown through, in the policy's order
        public int Cities;

        // The setup's path: one forward pass over the file, keeping only the fields it shows (world.json is tens of megabytes).
        public static SourceWorldSummary Read(string worldJsonPath, WorldSetupContract contract)
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            var profiles = new List<string>();
            using (var reader = new StreamReader(worldJsonPath, new UTF8Encoding(false), true, 1 << 16))
                WorldJsonScan.Read(reader, (id, name) => { if (id != null) names[id] = name; }, profiles.Add);
            return Build(names, profiles, contract);
        }

        // Reference reading (the whole document), for the tests.
        public static SourceWorldSummary FromWorldJson(string text, WorldSetupContract contract)
        {
            var world = (IDictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(text);
            var names = ((object[])((IDictionary<string, object>)world["civilizations"])["entities"]).Cast<IDictionary<string, object>>().ToDictionary(e => (string)e["id"], e => (string)e["name"], StringComparer.Ordinal);
            var profiles = ((object[])((IDictionary<string, object>)world["humans"])["cores"]).Cast<IDictionary<string, object>>().Select(c => (string)c["population_profile"]).ToList();
            return Build(names, profiles, contract);
        }

        private static SourceWorldSummary Build(Dictionary<string, string> names, List<string> profiles, WorldSetupContract contract)
        {
            var summary = new SourceWorldSummary { Cities = profiles.Count };
            foreach (var group in profiles.GroupBy(p => p ?? "").OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                string name, culture;
                if (!names.TryGetValue(group.Key, out name) || String.IsNullOrEmpty(name)) name = group.Key;
                // An unlisted civilization fails the campaign compile (culture-presentations.json); the setup does not invent one.
                if (!contract.Presentations.TryGetValue(group.Key, out culture)) throw new InvalidDataException("The generated civilization " + group.Key + " has no culture presentation.");
                summary.Peoples.Add(new People { Civilization = group.Key, Name = name, Culture = culture, Cities = group.Count() });
            }
            summary.Cultures = contract.OfficialCultures.Where(c => summary.Peoples.Any(p => p.Culture == c)).ToList();
            return summary;
        }
    }

    // A forward-only reader of the two world.json members the setup shows: civilizations.entities[].{id, name} and
    // humans.cores[].population_profile. Everything else is skipped without being built; memory stays at the read buffer.
    internal static class WorldJsonScan
    {
        public static void Read(TextReader reader, Action<string, string> entity, Action<string> core)
        {
            var s = new Scanner(reader);
            s.Object(top =>
            {
                if (top == "civilizations") s.Object(k => { if (k == "entities") s.Array(() => { string id = null, name = null; s.Object(f => { if (f == "id") id = s.StringOrSkip(); else if (f == "name") name = s.StringOrSkip(); else s.Skip(); }); entity(id, name); }); else s.Skip(); });
                else if (top == "humans") s.Object(k => { if (k == "cores") s.Array(() => { string profile = null; s.Object(f => { if (f == "population_profile") profile = s.StringOrSkip(); else s.Skip(); }); core(profile); }); else s.Skip(); });
                else s.Skip();
            });
            s.Whitespace();
            if (s.Peek() != -1) throw new InvalidDataException("world.json has content after its root object.");
        }

        private sealed class Scanner
        {
            private readonly TextReader reader;
            private readonly char[] buffer = new char[1 << 16];
            private int length, position;
            private readonly StringBuilder text = new StringBuilder();
            public Scanner(TextReader reader) { this.reader = reader; }

            public int Peek()
            {
                if (position == length) { length = reader.Read(buffer, 0, buffer.Length); position = 0; if (length <= 0) { length = 0; return -1; } }
                return buffer[position];
            }
            private int Next() { int c = Peek(); if (c >= 0) position++; return c; }
            private void Expect(char c) { Whitespace(); if (Next() != c) throw new InvalidDataException("world.json: expected '" + c + "'."); }
            public void Whitespace() { while (true) { int c = Peek(); if (c == ' ' || c == '\t' || c == '\n' || c == '\r') position++; else return; } }

            // An object whose members the callback consumes, one value per key.
            public void Object(Action<string> member)
            {
                Expect('{'); Whitespace();
                if (Peek() == '}') { position++; return; }
                while (true)
                {
                    Whitespace(); string key = String(true); Expect(':'); Whitespace();
                    member(key);
                    Whitespace(); int c = Next();
                    if (c == '}') return;
                    if (c != ',') throw new InvalidDataException("world.json: expected ',' or '}'.");
                }
            }

            public void Array(Action item)
            {
                Expect('['); Whitespace();
                if (Peek() == ']') { position++; return; }
                while (true)
                {
                    Whitespace(); item();
                    Whitespace(); int c = Next();
                    if (c == ']') return;
                    if (c != ',') throw new InvalidDataException("world.json: expected ',' or ']'.");
                }
            }

            public string StringOrSkip() { Whitespace(); if (Peek() == '"') return String(true); Skip(); return null; }

            private string String(bool keep)
            {
                if (Next() != '"') throw new InvalidDataException("world.json: expected a string.");
                text.Clear();
                while (true)
                {
                    int c = Next();
                    if (c < 0) throw new InvalidDataException("world.json: unterminated string.");
                    if (c == '"') return keep ? text.ToString() : null;
                    if (c != '\\') { if (keep) text.Append((char)c); continue; }
                    int e = Next();
                    switch (e)
                    {
                        case '"': case '\\': case '/': if (keep) text.Append((char)e); break;
                        case 'b': if (keep) text.Append('\b'); break;
                        case 'f': if (keep) text.Append('\f'); break;
                        case 'n': if (keep) text.Append('\n'); break;
                        case 'r': if (keep) text.Append('\r'); break;
                        case 't': if (keep) text.Append('\t'); break;
                        case 'u':
                            int code = 0;
                            for (int i = 0; i < 4; i++)
                            {
                                int h = Next();
                                int v = h >= '0' && h <= '9' ? h - '0' : h >= 'a' && h <= 'f' ? h - 'a' + 10 : h >= 'A' && h <= 'F' ? h - 'A' + 10 : -1;
                                if (v < 0) throw new InvalidDataException("world.json: bad \\u escape.");
                                code = code * 16 + v;
                            }
                            if (keep) text.Append((char)code);
                            break;
                        default: throw new InvalidDataException("world.json: bad escape.");
                    }
                }
            }

            // Any value, without building it.
            public void Skip()
            {
                Whitespace();
                int depth = 0;
                do
                {
                    Whitespace();
                    int c = Peek();
                    if (c < 0) throw new InvalidDataException("world.json: truncated.");
                    if (c == '"') String(false);
                    else if (c == '{' || c == '[') { position++; depth++; }
                    else if (c == '}' || c == ']') { position++; depth--; }
                    else if (c == ',' || c == ':') { if (depth == 0) throw new InvalidDataException("world.json: unexpected separator."); position++; }
                    else { while (true) { int d = Peek(); if (d < 0 || d == ',' || d == '}' || d == ']' || d == ' ' || d == '\t' || d == '\n' || d == '\r') break; position++; } }
                } while (depth > 0);
            }
        }
    }

    internal static class StageText
    {
        // The generation job's phases (MMR-44) as player text, with the attempt count while the source is generated; a phase with no text
        // (review, commit, the final states) keeps the last text.
        public static string Describe(ProgressEvent e)
        {
            if (e == null) return null;
            switch (e.Phase)
            {
                case JobPhase.ValidatingRequest: return "{=mnbl_ws_stage_request}Preparing the generator...";
                case JobPhase.GeneratingSource: return e.Total.HasValue ? "{=mnbl_ws_stage_attempt}Shaping the globe: land, seas, climate and peoples (attempt {ATTEMPT} of {ATTEMPTS})..." : "{=mnbl_ws_stage_world}Shaping the globe: land, seas, climate and peoples...";
                case JobPhase.Compiling: return "{=mnbl_ws_stage_compile}Building the campaign data: terrain, settlements and routes...";
                case JobPhase.ValidatingData: return "{=mnbl_ws_stage_verify}Verifying the world data...";
                case JobPhase.BuildingScene: return "{=mnbl_ws_stage_scene}Building the world map...";
                case JobPhase.RegisteringCampaign: return "{=mnbl_ws_stage_registration}Lords, towns and parties are created when the campaign starts.";
                case JobPhase.ValidatingRuntime: return "{=mnbl_ws_stage_navigation}Checking travel by land and sea...";
                default: return null;
            }
        }
        public static Dictionary<string, string> Variables(ProgressEvent e)
        {
            var vars = new Dictionary<string, string>(StringComparer.Ordinal);
            if (e != null && e.Phase == JobPhase.GeneratingSource && e.Total.HasValue)
            {
                vars["ATTEMPT"] = Math.Min(e.Total.Value, (e.Completed ?? 0) + 1).ToString(CultureInfo.InvariantCulture);
                vars["ATTEMPTS"] = e.Total.Value.ToString(CultureInfo.InvariantCulture);
            }
            return vars;
        }
    }

    internal enum SetupCommand { None, Generate, Back, RandomWorld, StartCampaign, NewSeed, PreviousPlace, NextPlace }

    internal static class SetupInput
    {
        // The generic panel keys (GenericPanelGameKeyCategory, v1.4.8): Confirm (Enter, Numpad Enter, the controller's left face button)
        // generates; Exit (Escape, the right face button) goes back, or cancels a running generation; Randomize (R, the top face button)
        // draws a Random World seed, except while the seed box is being typed in (R would otherwise fire as the player types). The screen
        // feeds the real key state; probes feed synthetic key states through the same mapping.
        // MMR-46: the tab keys (Q and E, the controller's shoulder buttons) select the previous or next place on the preview.
        public const string Confirm = "Confirm", Exit = "Exit", Randomize = "Randomize", Previous = "SwitchToPreviousTab", Next = "SwitchToNextTab";
        public static SetupCommand Map(Func<string, bool> released, bool typing)
        {
            if (released(Exit)) return SetupCommand.Back;
            if (released(Confirm)) return SetupCommand.Generate;
            if (!typing && released(Randomize)) return SetupCommand.RandomWorld;
            if (!typing && released(Previous)) return SetupCommand.PreviousPlace;
            if (!typing && released(Next)) return SetupCommand.NextPlace;
            return SetupCommand.None;
        }

        // MMR-46: with the preview shown, Confirm is Start Campaign, Randomize is New Seed and the tab keys select places; while the campaign
        // starts, no key acts; elsewhere (a world without a preview included) the keys keep their setup meaning and the tab keys do nothing.
        public static SetupCommand ForState(SetupCommand command, SetupState state, bool previewShown)
        {
            if (state == SetupState.Starting || state == SetupState.Started) return SetupCommand.None;
            if (state == SetupState.Generated && previewShown)
            {
                if (command == SetupCommand.Generate) return SetupCommand.StartCampaign;
                if (command == SetupCommand.RandomWorld) return SetupCommand.NewSeed;
                return command;
            }
            return command == SetupCommand.PreviousPlace || command == SetupCommand.NextPlace ? SetupCommand.None : command;
        }
    }

    // MMR-46: Generated shows the preview; Starting hands the world to the activation (MMR-45); Started is a committed campaign.
    internal enum SetupState { Editing, Generating, Generated, Failed, Starting, Started }

    // What starting the campaign came to: "playing" (committed), "withdrawn" (the player left character creation; the world is ready for
    // review again) or "failed" (Error).
    internal sealed class StartOutcome
    {
        public string Kind; public GenerationError Error;
        // A finished commit as the setup reports it: committed, withdrawn (back to review), or failed with the activation's own message: a
        // disk-full error keeps its category, a failure after the initial save (MMR-45's recoverable case) says the campaign is recovered at
        // the next start, any other says the attempt was undone. Never the generation's "could not be generated".
        public static StartOutcome FromCommit(JobPhase phase, GenerationError jobError, string failureDetail, bool recoverable)
        {
            if (phase == JobPhase.Playing) return new StartOutcome { Kind = "playing" };
            if (phase == JobPhase.ReadyForReview) return new StartOutcome { Kind = "withdrawn" };
            var error = jobError != null && jobError.Category == GenerationErrorCategory.DiskFull ? jobError
                : recoverable ? GenerationErrors.ActivationUnresolved(failureDetail) : GenerationErrors.Activation(failureDetail);
            return new StartOutcome { Kind = "failed", Error = error };
        }
    }

    // The generation job the setup hands its choice to (WorldSetupScreen's CoordinatorRunner over MMR-44's GenerationCoordinator).
    internal interface IWorldGenerationRunner
    {
        // Worker thread. Returns the generated world's source directory once it is ready for review; throws when no world was generated.
        // onProgress may be called from any thread; onJobCreated gives the job's id and directory before any progress.
        string Run(WorldChoice choice, Action<ProgressEvent> onProgress, Action<string, string> onJobCreated, CancellationToken token);
        GenerationError Classify(Exception error, string jobRoot);
        // The setup no longer needs this job's files (a new Generate, or the screen closed); any thread, after the job has ended.
        void Release(string jobRoot);
        // MMR-46. Worker thread: the preview of the job's world, ready for review (the same compiled world the activation plays).
        WorldPreview Preview(string jobRoot);
        // MMR-46: hands the job awaiting review to the activation (one Start Campaign action); completes when it committed, was withdrawn or
        // failed. A committed job's files are released by the runner.
        Task<StartOutcome> StartCampaign(string jobRoot);
        // MMR-47: the jobs a stopped process left unfinished (WorldRecoveryService), and discarding one (null when removed, else why not).
        IList<WorldRecoveryService.UnfinishedJob> Unfinished();
        string Discard(WorldRecoveryService.UnfinishedJob job);
    }

    internal sealed class WorldSetupModel
    {
        private sealed class Outcome { public string Split; public SourceWorldSummary Summary; public WorldPreview Preview; public string PreviewError; }
        private readonly WorldSetupContract contract;
        private readonly IWorldGenerationRunner runner;
        private readonly RandomNumberGenerator rng;
        private CancellationTokenSource cancel;
        private Task<Outcome> job;
        private volatile string jobRoot, jobId;
        private readonly ConcurrentQueue<ProgressEvent> progress = new ConcurrentQueue<ProgressEvent>();
        private readonly ProgressSink sink = new ProgressSink();
        private string ownedJob;   // the job whose files the setup holds (released on the next Generate and on Close)
        private Task<StartOutcome> starting;   // MMR-46: the activation of the reviewed world
        private readonly GenerationDiagnostics diagnostics;   // MMR-47: where failures are retained under their correlation ids
        private WorldRecoveryService.UnfinishedJob resumedFrom;   // the unfinished job being continued (offered again if its job never started)

        public WorldSetupModel(WorldSetupContract contract, IWorldGenerationRunner runner, IList<GenerationError> hostProblems, RandomNumberGenerator rng = null, GenerationDiagnostics diagnostics = null)
        {
            this.contract = contract; this.runner = runner; this.rng = rng ?? RandomNumberGenerator.Create(); this.diagnostics = diagnostics;
            HostProblems = new List<GenerationError>(hostProblems ?? new GenerationError[0]);
            // MMR-47: a host problem is a failure the player is shown: it gets its reference (once per process; the errors are shared).
            foreach (var problem in HostProblems) Retain(problem, null, null);
            // Listing the earlier session's jobs never keeps the setup from opening (review finding: a torn file threw out of the screen).
            try { Unfinished = new List<WorldRecoveryService.UnfinishedJob>(runner.Unfinished() ?? new WorldRecoveryService.UnfinishedJob[0]); }
            catch (Exception) { Unfinished = new List<WorldRecoveryService.UnfinishedJob>(); }
            Settings = WorldSettings.For(contract);
            SetSeedText(contract.DefaultSeed.ToString(CultureInfo.InvariantCulture));
        }

        public WorldSetupContract Contract { get { return contract; } }
        public List<GenerationError> HostProblems { get; private set; }
        public List<WorldSetting> Settings { get; private set; }
        public SetupState State { get; private set; }
        public string SeedTextValue { get; private set; }
        public SeedProblem SeedProblem { get; private set; }
        public uint Seed { get; private set; }
        public string SeedSource { get; private set; }
        public WorldChoice Choice { get; private set; }
        public string StageText { get; private set; }
        public Dictionary<string, string> StageVariables { get; private set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public ProgressEvent Progress { get { return sink.Latest; } }
        public ProgressSink Sink { get { return sink; } }
        public string JobId { get { return jobId; } }
        public bool CancelRequested { get; private set; }
        public GenerationError Error { get; private set; }
        public SourceWorldSummary Result { get; private set; }
        public string ResultSplit { get; private set; }
        public string JobRoot { get { return jobRoot; } }
        public bool Closed { get; private set; }
        // Job files being released, in order, off the engine thread (a job directory holds about 900 files).
        public Task Releasing { get; private set; } = Task.CompletedTask;
        // Increments on every change the view shows, so the view refreshes only then.
        public int Revision { get; private set; }
        public GenerationDiagnostics Diagnostics { get { return diagnostics; } }
        // MMR-47: the jobs a stopped process left; the setup offers the newest unfinished one (continue or discard), then the next.
        public List<WorldRecoveryService.UnfinishedJob> Unfinished { get; private set; }
        public WorldRecoveryService.UnfinishedJob Offered { get { return Unfinished.FirstOrDefault(j => !j.Finished); } }
        public bool CanResume { get { var j = Offered; return j != null && j.Resumable && CanGenerate && State != SetupState.Generated; } }
        public bool CanDiscard { get { return Offered != null && !Closed && State != SetupState.Generating && State != SetupState.Starting && State != SetupState.Started; } }
        public string DiscardProblem { get; private set; }
        public int Resumed { get; private set; }
        // MMR-46: the preview of the reviewed world, why it could not be made, and the Start Campaign hand-off.
        public WorldPreview Preview { get; private set; }
        public string PreviewError { get; private set; }
        public bool Withdrawn { get; private set; }        // the last start was withdrawn (the player left character creation)
        public int Starts { get; private set; }
        public int Generations { get; private set; }       // generation jobs this setup ran (a reused world adds none)
        public bool Reused { get; private set; }           // the last Generate showed the kept world again
        public bool HandedOff { get; private set; }        // the screen closed for the campaign: the job stays with the activation
        public bool StartCampaignAvailable { get { return CanStartCampaign; } }
        public bool CanStartCampaign { get { return !Closed && State == SetupState.Generated && Preview != null && ownedJob != null; } }
        public bool CanGenerate { get { return !Closed && State != SetupState.Generating && State != SetupState.Starting && State != SetupState.Started && HostProblems.Count == 0 && SeedProblem == SeedProblem.None; } }
        // The seed is edited where it is shown: not while generating or starting, nor under the preview (Back returns to it; review finding: a
        // seed typed under the preview made Confirm start the reviewed world).
        public bool CanEditSeed { get { return !Closed && (State == SetupState.Editing || State == SetupState.Failed || (State == SetupState.Generated && Preview == null)); } }
        public bool PreviewShown { get { return State == SetupState.Generated && Preview != null; } }

        public void SetSeedText(string text)
        {
            if (!CanEditSeed) return;
            text = text ?? "";
            if (text == SeedTextValue) return;
            SeedSource = "explicit";
            SeedTextValue = text;
            uint seed; SeedProblem = SeedText.Parse(text, contract.MaxSeed, out seed); Seed = seed;
            Revision++;
        }

        public uint RandomWorldSeed()
        {
            if (!CanEditSeed) return Seed;
            uint seed = RandomWorld.Next(contract.MaxSeed, rng);
            SetSeedText(seed.ToString(CultureInfo.InvariantCulture));
            SeedSource = "random";
            Revision++;
            return seed;
        }

        public bool Generate()
        {
            if (!CanGenerate) return false;
            // Back from the preview keeps the world: the same seed shows it again without a second job (MMR-46).
            if (State == SetupState.Editing && ownedJob != null && Choice != null && Choice.Seed == Seed && Result != null && Preview != null)
            {
                State = SetupState.Generated; Reused = true; Withdrawn = false;
                Revision++;
                return true;
            }
            return StartJob(new WorldChoice { CampaignInstanceId = Guid.NewGuid().ToString("D"), Seed = Seed, SeedSource = SeedSource ?? "explicit", RecipeVersion = contract.RecipeVersion,
                Overrides = new SortedDictionary<string, long>(contract.Overrides, StringComparer.Ordinal), MaxAttempts = contract.QualityAttempts, ProducerLockSha256 = contract.ProducerLockSha256, Profile = "one-shot" });
        }

        // MMR-47: continues the offered unfinished job in its own directory with its own choice (seed, campaign instance, seed source); the
        // seed box shows its seed. A job made by another generator is not offered for this (WorldRecoveryService).
        public bool ResumeUnfinished()
        {
            var unfinished = Offered;
            if (!CanResume) return false;
            Unfinished.Remove(unfinished); resumedFrom = unfinished;
            Resumed++;
            SetSeedText(unfinished.Seed.ToString(CultureInfo.InvariantCulture));
            SeedSource = unfinished.SeedSource == "random" ? "random" : "explicit";
            return StartJob(new WorldChoice { CampaignInstanceId = unfinished.CampaignInstanceId, Seed = unfinished.Seed, SeedSource = SeedSource, RecipeVersion = contract.RecipeVersion,
                Overrides = new SortedDictionary<string, long>(contract.Overrides, StringComparer.Ordinal), MaxAttempts = contract.QualityAttempts, ProducerLockSha256 = contract.ProducerLockSha256,
                Profile = unfinished.Profile ?? "one-shot", ResumeJobRoot = unfinished.Root });
        }

        // MMR-47: discards the offered job's directory (only it; bundles, packages, activations and saves stay); the next one is offered.
        public bool DiscardUnfinished()
        {
            var unfinished = Offered;
            if (!CanDiscard) return false;
            string why = runner.Discard(unfinished);
            DiscardProblem = why;
            if (why == null) Unfinished.Remove(unfinished);
            Revision++;
            return why == null;
        }

        private bool StartJob(WorldChoice next)
        {
            Reused = false; Withdrawn = false;
            ReleaseOwnedJob();
            Preview = null; PreviewError = null;
            Generations++;
            if (next.ResumeJobRoot == null) resumedFrom = null;
            Choice = next;
            State = SetupState.Generating; Error = null; Result = null; ResultSplit = null; CancelRequested = false; jobRoot = null; jobId = null;
            StageText = WorldSetupText.Generating; StageVariables = new Dictionary<string, string>(StringComparer.Ordinal);
            cancel = new CancellationTokenSource();
            var choice = Choice; var token = cancel.Token; var releasing = Releasing;
            // The world summary reads world.json on the worker too: the engine thread never parses it. The previous job is released first
            // (the generation job runs one job at a time).
            job = Task.Run(() =>
            {
                try { releasing.Wait(); } catch (AggregateException) { }
                token.ThrowIfCancellationRequested();
                string split = runner.Run(choice, e => progress.Enqueue(e), (id, root) => { jobId = id; jobRoot = root; }, token);
                token.ThrowIfCancellationRequested();
                var outcome = new Outcome { Split = split, Summary = SourceWorldSummary.Read(Path.Combine(split, "world.json"), contract) };
                // MMR-46: the preview from the same compiled world (a world without one is not offered for play).
                try { outcome.Preview = runner.Preview(jobRoot); }
                catch (Exception error) { outcome.PreviewError = error.GetType().Name + ": " + error.Message; }
                return outcome;
            });
            Revision++;
            return true;
        }

        public bool Cancel()
        {
            if (State != SetupState.Generating || CancelRequested) return false;
            CancelRequested = true; StageText = WorldSetupText.Cancelling; cancel.Cancel();
            Revision++;
            return true;
        }

        // Back: cancels a running generation (the screen stays until it has stopped); from the preview, returns to the seed with the world
        // kept (MMR-46); while the campaign starts, does nothing (the way out is character creation's own Back); otherwise leaves the screen
        // (returns true).
        public bool Back()
        {
            if (State == SetupState.Generating) { Cancel(); return false; }
            if (State == SetupState.Generated) { State = SetupState.Editing; Withdrawn = false; Revision++; return false; }
            if (State == SetupState.Starting || State == SetupState.Started) return false;
            return true;
        }

        // MMR-46: the one Start Campaign action after the preview.
        public bool StartCampaign()
        {
            if (!CanStartCampaign) return false;
            State = SetupState.Starting; StageText = WorldSetupText.Starting; StageVariables = new Dictionary<string, string>(StringComparer.Ordinal); Withdrawn = false; Error = null;
            Starts++;
            starting = runner.StartCampaign(ownedJob);
            Revision++;
            return true;
        }

        // MMR-46: an explicit new seed (Random World's draw, shown in the seed box), generated at once; the previously reviewed world is
        // released and no committed world is touched.
        public bool NewSeed()
        {
            if (Closed || State != SetupState.Generated || HostProblems.Count > 0) return false;
            State = SetupState.Editing; Withdrawn = false;
            RandomWorldSeed();
            return Generate();
        }

        // The screen closed for the campaign: the job stays with the activation, the model keeps ticking (WorldSetupScreen.Pending).
        public void HandOff() { HandedOff = true; Revision++; }
        public void Resume() { HandedOff = false; Revision++; }

        // Engine thread: takes the job's state.
        public void Tick()
        {
            if (State == SetupState.Starting && starting != null && starting.IsCompleted)
            {
                var outcome = starting.Status == TaskStatus.RanToCompletion ? starting.Result
                    : new StartOutcome { Kind = "failed", Error = GenerationErrors.Activation(starting.Exception == null ? "cancelled" : starting.Exception.GetBaseException().GetType().Name + ": " + starting.Exception.GetBaseException().Message) };
                starting = null; StageText = null;
                if (outcome.Kind == "playing") { State = SetupState.Started; ownedJob = null; }   // the runner released the committed job's files
                else if (outcome.Kind == "withdrawn") { State = SetupState.Generated; Withdrawn = true; }
                else { State = SetupState.Failed; Error = outcome.Error; Retain(Error, null, ownedJob); }
                Revision++;
                return;
            }
            // Progress: only the current job's events, in order; a late event of an earlier (cancelled) job is dropped.
            string following = jobId;
            if (following != null && following != sink.JobId) sink.Follow(following);
            ProgressEvent e;
            while (progress.TryDequeue(out e))
            {
                if (!sink.Accept(e) || State != SetupState.Generating || CancelRequested) continue;
                string text = UI.StageText.Describe(e);
                if (text == null) continue;
                var vars = UI.StageText.Variables(e);
                if (text != StageText || !vars.OrderBy(kv => kv.Key).SequenceEqual(StageVariables.OrderBy(kv => kv.Key))) { StageText = text; StageVariables = vars; Revision++; }
            }
            if (State != SetupState.Generating) return;
            if (job == null || !job.IsCompleted) return;
            ownedJob = jobRoot;
            if (job.Status == TaskStatus.RanToCompletion && !CancelRequested)
            {
                ResultSplit = job.Result.Split; Result = job.Result.Summary; Preview = job.Result.Preview; PreviewError = job.Result.PreviewError;
                State = SetupState.Generated; StageText = null;
            }
            else Fail(job.Exception == null ? (Exception)new OperationCanceledException() : job.Exception.GetBaseException());
            cancel.Dispose(); cancel = null; job = null;
            Revision++;
        }

        private void Fail(Exception error)
        {
            // A cancel the player asked for is an interrupted generation, whatever the job's last failure was.
            Error = CancelRequested ? GenerationErrors.FromGeneration("cancelled", error, null, error.GetType().Name + ": " + error.Message) : runner.Classify(error, jobRoot);
            State = SetupState.Failed; StageText = null;
            Retain(Error, jobId, jobRoot);
            // A continued job that never started (the generation job was busy): it is offered again.
            if (resumedFrom != null && jobId == null && !Unfinished.Contains(resumedFrom)) Unfinished.Insert(0, resumedFrom);
        }

        // The screen is closing: a running generation is cancelled and its files released once it has stopped; finished files now.
        public void Close()
        {
            if (Closed) return;
            if (State == SetupState.Starting || State == SetupState.Started) { Closed = true; Revision++; return; }   // the job is the activation's
            if (State == SetupState.Generating && job != null)
            {
                Cancel();
                var running = job; var source = cancel;
                Releasing = Task.WhenAll(Releasing, running.ContinueWith(_ => { }, TaskScheduler.Default)).ContinueWith(_ => { runner.Release(jobRoot); source.Dispose(); }, TaskScheduler.Default);
                job = null; cancel = null;
            }
            else ReleaseOwnedJob();
            Closed = true;
            Revision++;
        }

        // MMR-47: the failure's correlation id and retained diagnostics (with the job's records, before its directory is released).
        private void Retain(GenerationError error, string forJob, string forJobRoot)
        {
            if (diagnostics == null || error == null) return;
            diagnostics.Retain(error, forJob, forJobRoot, new Dictionary<string, object> { ["seed"] = Choice == null ? (object)null : Choice.Seed, ["state"] = State.ToString() });
        }

        private void ReleaseOwnedJob()
        {
            if (ownedJob == null) return;
            string root = ownedJob; ownedJob = null;
            Releasing = Releasing.ContinueWith(_ => runner.Release(root), TaskScheduler.Default);
        }
    }
}
