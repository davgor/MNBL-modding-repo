using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-31, MMR-280: faction definitions (docs/factions.md). Pure managed. From the compiler records (campaign.json v2, id-map.json: MMR-27
    // and the MMR-279 population pass), the faction policy (worldgen/policies/factions.json) and the population policy
    // (worldgen/policies/population.json) it builds closed intents for the merged kingdoms with their starting wars, the planned noble
    // houses, mercenary companies and outlaw clans, the generated cultures' presentation, the kingdom stances and ruler relations, the
    // lord-to-clan affiliations C03 writes into Heroes.xml, and the Kingdoms and Factions XML the registry loads (MMR-30). Identities,
    // ownership, membership and wars come from the generated world; assets (official cultures, banner meshes, palette, icons, policies,
    // title strings, the minor clans' troop lines) are the installed public ones the policies list. No engine object is created: heroes
    // and settlements are referenced by their compiler ids and instantiated later (C03 MMR-32, C04 MMR-33), and registered closure is
    // proven by C07 (MMR-36).
    //
    // Determinism: records are processed in id order (names in uid order) and every synthesized value comes from a named,
    // versioned stream (Stream): value = sha256("mnbl.faction-stream/1\n" + stream + "\n" + version + "\n" + prefix + "\n" + entity
    // + "\n" + draw). Ownership and membership use no stream; the colors stream draws only colours, the banner stream only icons,
    // the relations stream only ruler relations.
    internal sealed class FactionFactory
    {
        public sealed class KingdomIntent
        {
            public string Id, Uid, Name, SourceName, NameLocalized, Culture, CultureUid, Civilization, CivilizationName, RulerId, RulingClanId, CapitalId;
            public List<string> Towns = new List<string>(), Castles = new List<string>(), Policies = new List<string>();
            public List<string> Wars = new List<string>();   // the kingdoms it starts at war with (campaign.json v2 wars[], RT-06)
            public string BannerKey, Color, Color2, PrimaryBannerColor, SecondaryBannerColor, RulerTitle, Title, Text;
            public int Fiefs, ColorIndex1, ColorIndex2;
        }

        // A clan: a noble house of a kingdom (Kind "house"), or a minor clan with no kingdom, a mercenary company ("company") or an outlaw
        // clan ("outlaw", OutlawKind mafia, nomad or sect).
        public sealed class ClanIntent
        {
            public string Id, Uid, Name, SourceName, NameLocalized, Culture, KingdomId, LeaderId, HomeId, BannerKey, Text, Provenance;
            public string Kind = "house", OutlawKind, PartyTemplate, Color, Color2;
            public List<string> Members = new List<string>();          // living planned Lords (a house's minors included)
            public List<string> DeadParents = new List<string>();      // a house's alive="false" parents: of the clan, not members
            public List<string> Fiefs = new List<string>();            // a house's towns and castles
            public List<string> CharacterTemplates = new List<string>();
            public List<string> Wars = new List<string>();             // an outlaw clan's kingdoms at war with it (the engine declares them)
            public int Tier;
            public bool Ruling;
            public bool Minor { get { return Kind != "house"; } }
        }

        // A kingdom pair: its starting stance ("war" for a campaign wars[] pair, written on both kingdoms; else "neutral", the engine default,
        // written nowhere) and the relation between the two rulers, a hero relation intent C03 applies once the heroes exist.
        public sealed class Relation { public string A, B, RulerA, RulerB, Stance; public int RulerRelation; }
        // A lord's clan and kingdom as C03 must write them into Heroes.xml (faction="Faction.<clan>"); null for an unaffiliated lord.
        public sealed class Affiliation { public string Hero, Clan, Kingdom, Note; public bool Dead; }
        public sealed class CultureIntent { public string Uid, Civilization, CivilizationName, Presentation, Template; public List<string> Kingdoms = new List<string>(); }
        public sealed class Dissolution { public string Kingdom, Uid, Reason; public List<string> Lords = new List<string>(), Castles = new List<string>(), Clans = new List<string>(); }

        public sealed class Result
        {
            public string WorldPrefix;
            public List<KingdomIntent> Kingdoms = new List<KingdomIntent>();
            public List<ClanIntent> Clans = new List<ClanIntent>();        // the noble houses
            public List<ClanIntent> MinorClans = new List<ClanIntent>();   // mercenary companies and outlaw clans
            public List<Relation> Relations = new List<Relation>();
            public List<Affiliation> Affiliations = new List<Affiliation>();
            public List<CultureIntent> Cultures = new List<CultureIntent>();
            public List<Dissolution> Dissolved = new List<Dissolution>();
            public List<string> DroppedWars = new List<string>();          // wars[] pairs naming a dissolved kingdom
            public List<KeyValuePair<string, XmlDocument>> Documents = new List<KeyValuePair<string, XmlDocument>>();
            public List<string> DeclaredKeys = new List<string>();
            public string IntentsJson, IntentsSha256;
            public IEnumerable<ClanIntent> AllClans { get { return Clans.Concat(MinorClans); } }
            // The members of the mercenary companies and outlaw clans: the heroes MMR-281 marks Hero.IsMinorFactionHero at new game and on load.
            public IEnumerable<string> MinorFactionHeroIds { get { return MinorClans.SelectMany(c => c.Members).OrderBy(x => x, StringComparer.Ordinal); } }
        }

        public sealed class FactionException : InvalidOperationException
        {
            public FactionException(IList<string> problems) : base("Faction definitions refused: " + String.Join("; ", problems.Take(20)) + (problems.Count > 20 ? "; ..." : "")) { Problems = problems; }
            public IList<string> Problems { get; private set; }
        }

        public const int AdultAge = 18;
        private readonly IDictionary<string, object> policy, population;
        private readonly Dictionary<string, int> streams = new Dictionary<string, int>(StringComparer.Ordinal);
        private string prefix;

        private FactionFactory(IDictionary<string, object> policy, IDictionary<string, object> population)
        {
            this.policy = policy; this.population = population;
            var s = Obj(policy, "streams");
            foreach (var name in new[] { "banner", "colors", "relations" }) streams[name] = Int(s, name);
        }

        // campaign: campaign.json (mnbl.campaign-records v2). idMap: id-map.json (every id the compiler assigned or reserved).
        // factions: worldgen/policies/factions.json. presentations: worldgen/policies/culture-presentations.json. templates:
        // official culture id -> worldgen/templates/cultures/<culture>.json. population: worldgen/policies/population.json (the minor clans'
        // troop lines and tiers, the war limits).
        public static Result Build(IDictionary<string, object> campaign, IDictionary<string, object> idMap, IDictionary<string, object> factions,
                                   IDictionary<string, object> presentations, IDictionary<string, IDictionary<string, object>> templates, IDictionary<string, object> population)
        {
            return new FactionFactory(factions, population).Run(campaign, idMap, presentations, templates);
        }

        // The world's id prefix: the first world-qualified hero or town id of the campaign records (MMR-258's look plan reads it too).
        public static string WorldPrefixOf(IDictionary<string, object> campaign)
        {
            return Records(campaign, "heroes").Select(h => Text(h, "id")).Concat(Records(campaign, "towns").Select(t => Text(t, "id"))).Where(id => id != null).Select(CampaignIdentity.WorldPrefix).FirstOrDefault(p => p != null);
        }

        // A campaign v2 hero record: living unless alive is false; its planned age (-1 when absent).
        public static bool Alive(IDictionary<string, object> hero) { object v; return hero == null || !hero.TryGetValue("alive", out v) || !Equals(v, false); }
        public static int AgeOf(IDictionary<string, object> hero) { object v; return hero != null && hero.TryGetValue("age", out v) && v != null ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : -1; }

        private Result Run(IDictionary<string, object> campaign, IDictionary<string, object> idMap, IDictionary<string, object> presentations,
                           IDictionary<string, IDictionary<string, object>> templates)
        {
            var problems = new List<string>();
            var result = new Result();
            if (Text(campaign, "schema") != "mnbl.campaign-records" || Int(campaign, "version") != 2) problems.Add("not mnbl.campaign-records version 2 (the population blocks)");
            if (Text(policy, "schema") != "mnbl.factions" || Int(policy, "version") != 1) problems.Add("not mnbl.factions version 1");
            if (Text(population, "schema") != "mnbl.population" || Int(population, "version") != 1) problems.Add("not mnbl.population version 1");
            var heroes = Records(campaign, "heroes").ToDictionary(h => Text(h, "id"), StringComparer.Ordinal);
            var towns = Records(campaign, "towns").ToDictionary(t => Text(t, "id"), StringComparer.Ordinal);
            var castleIds = new HashSet<string>(Records(campaign, "castles").Select(c => Text(c, "id")), StringComparer.Ordinal);
            var clans = Records(campaign, "houses").ToDictionary(c => Text(c, "id"), StringComparer.Ordinal);
            prefix = WorldPrefixOf(campaign);
            result.WorldPrefix = prefix;
            if (prefix == null) problems.Add("no world-qualified record");
            var banners = Obj(policy, "banners");
            var palette = List(banners, "palette").OfType<IDictionary<string, object>>().Select(c => new KeyValuePair<int, string>(Int(c, "id"), Text(c, "argb"))).ToList();
            if (palette.Select(c => c.Key).Distinct().Count() < 2) problems.Add("the banner palette has fewer than two colors");
            string sourceNote = Text(Obj(policy, "texts"), "sourceNote") ?? "";
            if (!sourceNote.Contains("{source}")) problems.Add("texts.sourceNote must contain {source}");
            if (Text(Obj(policy, "diplomacy"), "stance") != "campaignWars") problems.Add("diplomacy.stance must be campaignWars");
            if (problems.Count > 0) throw new FactionException(problems);

            var cultures = Obj(policy, "cultures");
            var civilizations = List(presentations, "civilizations").OfType<IDictionary<string, object>>().ToDictionary(c => Text(c, "civilization"), c => Text(c, "culture"), StringComparer.Ordinal);
            // A presentation is usable when the policy lists it and its template is an installed main culture's with every required
            // contract member set. For the installed cultures requiredMissing is empty by construction; this guards any other template.
            var required = new Func<string, bool>(culture => {
                IDictionary<string, object> template;
                return culture != null && cultures.ContainsKey(culture) && templates.TryGetValue(culture, out template) && Text(template, "culture") == culture &&
                       !List(template, "requiredMissing").Any() && List(Obj(cultures, culture), "bannerIcons").Any();
            });
            var usedPairs = new HashSet<string>(StringComparer.Ordinal);
            string action = Text(Obj(policy, "zeroTownRealm"), "action");
            // Every Lord is planned by campaign v2 (MMR-279): a bucket, an age and an FWGC++ sex. House lords are the living members of the
            // houses; minors (under 18) only with both parents set. A dead parent (alive false) belongs to its house without being a member.
            foreach (var h in heroes.Values.Where(h => Text(h, "occupation") == "Lord").OrderBy(h => Text(h, "id"), StringComparer.Ordinal))
            {
                string bucket = Text(h, "bucket");
                if (bucket != "house" && bucket != "company" && bucket != "outlaw") problems.Add("lord " + Text(h, "id") + " has bucket " + (bucket ?? "none") + ", not house, company or outlaw");
                if (AgeOf(h) < 0 || !(h.ContainsKey("female") && h["female"] is bool)) problems.Add("lord " + Text(h, "id") + " has no planned age and sex (campaign v2)");
            }
            var lords = heroes.Values.Where(h => Text(h, "occupation") == "Lord" && Text(h, "bucket") == "house" && Alive(h)).OrderBy(h => Text(h, "id"), StringComparer.Ordinal).ToList();
            var deadParents = heroes.Values.Where(h => Text(h, "occupation") == "Lord" && !Alive(h)).OrderBy(h => Text(h, "id"), StringComparer.Ordinal).ToList();

            // Culture presentation: exhaustive over every accepted source civilization the records carry.
            foreach (var civ in heroes.Values.Select(h => Text(h, "civilization")).Concat(Records(campaign, "kingdoms").Select(k => Text(k, "civilization"))).Distinct().OrderBy(c => c, StringComparer.Ordinal))
            {
                string culture;
                if (!civilizations.TryGetValue(civ ?? "", out culture)) problems.Add("civilization " + civ + " has no presentation");
                else if (!required(culture)) problems.Add("civilization " + civ + " presents through " + culture + ", which has no complete culture template");
            }

            // Zero-town realms are dissolved (or refused); the others are kingdoms, named uniquely in uid order.
            var kept = new List<IDictionary<string, object>>();
            foreach (var k in Records(campaign, "kingdoms").OrderBy(k => Text(k, "id"), StringComparer.Ordinal))
            {
                string id = Text(k, "id");
                if (List(k, "towns").Any()) { kept.Add(k); continue; }
                if (action != "dissolve") { problems.Add("kingdom " + id + " has no town"); continue; }
                result.Dissolved.Add(new Dissolution {
                    Kingdom = id, Uid = Text(k, "uid"), Reason = "no town after the compiler and travel exclusions",
                    Lords = lords.Concat(deadParents).Where(h => Text(h, "kingdom") == id).Select(h => Text(h, "id")).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    Castles = List(k, "castles").Select(Str).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    Clans = clans.Values.Where(c => Text(c, "kingdom") == id).Select(c => Text(c, "id")).OrderBy(x => x, StringComparer.Ordinal).ToList() });
            }
            // Kingdom names come unique and clean from the compiler's name cleaner (MMR-286); a repeated one is refused, never renamed.
            var displayName = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var k in kept) displayName[Text(k, "id")] = Clean(Text(k, "name"));
            foreach (var group in kept.GroupBy(k => displayName[Text(k, "id")], StringComparer.Ordinal).Where(g => g.Count() > 1))
                problems.Add("kingdoms " + String.Join(", ", group.Select(k => Text(k, "id"))) + " share the name " + group.Key);

            foreach (var k in kept)
            {
                string id = Text(k, "id");
                IDictionary<string, object> rulingRecord;
                clans.TryGetValue(Text(k, "rulingHouse") ?? "", out rulingRecord);
                string culture = Text(k, "presentation");
                var intent = new KingdomIntent {
                    Id = id, Uid = Text(k, "uid"), SourceName = Text(k, "sourceName") ?? Text(k, "name"), Name = displayName[id], Culture = culture, CultureUid = Text(k, "cultureUid"),
                    Civilization = Text(k, "civilization"), CivilizationName = Clean(Text(k, "civilizationName")), RulerId = Text(k, "ruler"), RulingClanId = Text(k, "rulingHouse"),
                    CapitalId = Text(k, "capital"), Towns = List(k, "towns").Select(Str).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    Castles = List(k, "castles").Select(Str).OrderBy(x => x, StringComparer.Ordinal).ToList() };
                intent.NameLocalized = Localized(id, intent.Name);
                intent.Fiefs = intent.Towns.Count + intent.Castles.Count;
                // c1: the ruler, ruling house and capital resolve in the compiler graph, and the ruler leads the ruling house.
                IDictionary<string, object> ruler;
                if (!heroes.TryGetValue(intent.RulerId ?? "", out ruler)) problems.Add("kingdom " + id + " ruler " + intent.RulerId + " is not a compiler hero");
                else if (Text(ruler, "occupation") != "Lord" || !Alive(ruler)) problems.Add("kingdom " + id + " ruler " + intent.RulerId + " is not a living lord");
                if (rulingRecord == null) problems.Add("kingdom " + id + " ruling clan " + intent.RulingClanId + " is not a compiler clan");
                else if (Text(rulingRecord, "leader") != intent.RulerId || Text(rulingRecord, "kingdom") != id) problems.Add("kingdom " + id + " ruler does not lead its ruling clan");
                if (!towns.ContainsKey(intent.CapitalId ?? "") || !intent.Towns.Contains(intent.CapitalId)) problems.Add("kingdom " + id + " capital " + intent.CapitalId + " is not one of its towns");
                string expected;
                if (!civilizations.TryGetValue(intent.Civilization ?? "", out expected) || expected != culture) problems.Add("kingdom " + id + " culture " + culture + " is not its civilization's presentation");
                if (!required(culture)) { problems.Add("kingdom " + id + " culture " + culture + " has no complete culture template"); continue; }
                var assets = Obj(cultures, culture);
                intent.Policies = List(assets, "policies").Select(Str).ToList();
                intent.RulerTitle = Text(assets, "rulerTitle");
                intent.Title = Localized(id + "_title", intent.Name);
                string capitalName = towns.ContainsKey(intent.CapitalId ?? "") ? Clean(Text(towns[intent.CapitalId], "name")) : intent.CapitalId;
                intent.Text = Localized(id + "_text", Fill(Text(Obj(policy, "texts"), "kingdomEncyclopedia"), new Dictionary<string, string> {
                    ["realm"] = intent.Name, ["civilization"] = intent.CivilizationName, ["capital"] = capitalName }) + SourceNote(sourceNote, Text(k, "sourceName")));
                // Colours: an ordered pair of distinct palette colours, unique world-wide, drawn in pairs from the colors stream.
                int c1, c2;
                if (!ColorPair(id, palette, usedPairs, out c1, out c2)) { problems.Add("kingdom " + id + " has no unique color pair"); continue; }
                intent.ColorIndex1 = c1; intent.ColorIndex2 = c2;
                intent.Color = palette[c1].Value; intent.Color2 = palette[c2].Value;
                intent.PrimaryBannerColor = "0x" + palette[c1].Value.ToLowerInvariant(); intent.SecondaryBannerColor = "0x" + palette[c2].Value.ToLowerInvariant();
                result.Kingdoms.Add(intent);

                // Houses: the kingdom's planned houses (campaign.json v2, MMR-279), the ruling house first. Every member is a living Lord of this
                // kingdom and of the house; a minor has both parents set; every dead parent of the house is a parent of one of its members.
                if (rulingRecord == null) continue;
                var kingdomClans = new List<ClanIntent>();
                foreach (var house in clans.Values.Where(c => Text(c, "kingdom") == id).OrderBy(c => c == rulingRecord ? 0 : 1).ThenBy(c => Text(c, "id"), StringComparer.Ordinal))
                {
                    string houseId = Text(house, "id");
                    bool isRuling = house == rulingRecord;
                    var members = List(house, "members").Select(Str).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    foreach (var m in members)
                    {
                        IDictionary<string, object> hero;
                        if (!heroes.TryGetValue(m, out hero)) problems.Add("house " + houseId + " member " + m + " is not a compiler hero");
                        else if (Text(hero, "occupation") != "Lord") problems.Add("house " + houseId + " member " + m + " is a " + Text(hero, "occupation") + ", not a lord");
                        else if (!Alive(hero)) problems.Add("house " + houseId + " member " + m + " is a dead parent, not a member");
                        else if (Text(hero, "kingdom") != id) problems.Add("house " + houseId + " member " + m + " is not a lord of " + id);
                        else if (Text(hero, "clan") != houseId) problems.Add("house " + houseId + " member " + m + " names clan " + Text(hero, "clan"));
                        else if (AgeOf(hero) >= 0 && AgeOf(hero) < AdultAge && (!Parent(heroes, Text(hero, "father"), houseId) || !Parent(heroes, Text(hero, "mother"), houseId)))
                            problems.Add("house " + houseId + " minor " + m + " (age " + AgeOf(hero) + ") does not have both parents set in the house");
                    }
                    var dead = deadParents.Where(h => Text(h, "clan") == houseId).Select(h => Text(h, "id")).ToList();
                    foreach (var d in dead)
                        if (!members.Any(m => heroes.ContainsKey(m) && (Text(heroes[m], "father") == d || Text(heroes[m], "mother") == d))) problems.Add("house " + houseId + " dead parent " + d + " is the parent of none of its members");
                    string houseCulture = Text(house, "presentation");
                    if (!required(houseCulture)) problems.Add("house " + houseId + " culture " + houseCulture + " has no complete culture template");
                    var fiefs = List(house, "fiefs").Select(Str).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    string home = Text(house, "home");
                    if (home == null || !fiefs.Contains(home)) problems.Add("house " + houseId + " home " + home + " is not one of its fiefs");
                    foreach (var f in fiefs.Where(f => !intent.Towns.Contains(f) && !intent.Castles.Contains(f))) problems.Add("house " + houseId + " fief " + f + " is not a fief of its kingdom " + id);
                    // Tiers: the ruling house from the kingdom's fiefs, every other house from its own.
                    int tier = isRuling ? Tier(Obj(policy, "rulingClan"), intent.Fiefs) : Tier(Obj(policy, "houses"), fiefs.Count);
                    var clan = new ClanIntent {
                        Id = houseId, Uid = Text(house, "uid"), SourceName = Text(house, "name"), Name = Clean(Text(house, "name")), Culture = houseCulture, KingdomId = id,
                        LeaderId = Text(house, "leader"), HomeId = home, Ruling = isRuling, Tier = tier, Members = members, DeadParents = dead, Fiefs = fiefs,
                        Provenance = "MMR-279 house of " + intent.Uid + " (" + Text(house, "origin") + ")" };
                    if (isRuling && clan.LeaderId != intent.RulerId) problems.Add("kingdom " + id + " ruler does not lead its ruling house");
                    clan.Text = Localized(clan.Id + "_text", Fill(Text(Obj(policy, "texts"), "clanEncyclopedia"), new Dictionary<string, string> { ["realm"] = intent.Name }));
                    kingdomClans.Add(clan);
                }
                // Banners: the kingdom's icon is one banner-stream draw; each house (ruling first, then the others by id) draws until its icon
                // differs from the kingdom's and every earlier house's. Colour pairs separate kingdoms, so every banner is unique.
                var icons = List(assets, "bannerIcons").Select(Convert.ToInt32).ToList();
                int distinctIcons = icons.Distinct().Count();
                int kingdomIcon = icons[Draw("banner", id, 0, icons.Count)];
                intent.BannerKey = BannerKey(banners, palette[c1].Key, palette[c2].Key, kingdomIcon);
                var usedIcons = new HashSet<int> { kingdomIcon };
                if (kingdomClans.Count + 1 > distinctIcons) problems.Add("kingdom " + id + " has " + (kingdomClans.Count + 1) + " banners but its culture has " + distinctIcons + " icons");
                else
                    foreach (var clan in kingdomClans.OrderBy(c => c.Ruling ? 0 : 1).ThenBy(c => c.Id, StringComparer.Ordinal))
                    {
                        int icon = -1;
                        for (int draw = 0; draw < 64 * icons.Count && icon < 0; draw++)
                        {
                            int candidate = icons[Draw("banner", clan.Id, draw, icons.Count)];
                            if (usedIcons.Add(candidate)) icon = candidate;
                        }
                        if (icon < 0) { icon = icons.First(i => !usedIcons.Contains(i)); usedIcons.Add(icon); }
                        clan.BannerKey = BannerKey(banners, palette[c1].Key, palette[c2].Key, icon);
                    }
                result.Clans.AddRange(kingdomClans);
            }
            var kingdomIds = new HashSet<string>(result.Kingdoms.Select(k => k.Id), StringComparer.Ordinal);
            var dissolvedIds = new HashSet<string>(result.Dissolved.Select(d => d.Kingdom), StringComparer.Ordinal);

            // Minor clans (campaign.json v2 mercenaryCompanies[] and outlawClans[], MMR-279): no kingdom, a town home, a leader among the
            // living planned adult Lords, the tier the population policy gives the kind, and the troop line of population.json troopTemplates
            // for its presentation culture, never a lord party template (RT-03).
            var lordPartyTemplates = new HashSet<string>(templates.Values.Select(t => Text(Obj(t, "values"), "default_party_template")).Where(p => p != null).Select(p => p.StartsWith("PartyTemplate.", StringComparison.Ordinal) ? p.Substring(14) : p), StringComparer.Ordinal);
            var kindFlags = Obj(Obj(policy, "minorClans"), "kindFlags");
            int maxWarKingdoms = Int(Obj(population, "outlaws"), "maxWarKingdoms");
            foreach (var list in new[] { "mercenaryCompanies", "outlawClans" })
                foreach (var m in Records(campaign, list).OrderBy(x => Text(x, "id"), StringComparer.Ordinal))
                {
                    string id = Text(m, "id"), culture = Text(m, "presentation");
                    bool company = list == "mercenaryCompanies";
                    var clan = new ClanIntent {
                        Id = id, Uid = Text(m, "uid"), SourceName = Text(m, "name"), Name = Clean(Text(m, "name")), Culture = culture, Kind = company ? "company" : "outlaw",
                        OutlawKind = company ? null : Text(m, "kind"), LeaderId = Text(m, "leader"), HomeId = Text(m, "home"), Tier = Int(m, "tier"),
                        Members = List(m, "members").Select(Str).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                        PartyTemplate = Text(m, "partyTemplate"), CharacterTemplates = List(m, "characterTemplates").Select(Str).ToList(),
                        Wars = List(m, "wars").Select(Str).Where(k => !dissolvedIds.Contains(k)).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                        Provenance = "MMR-279 " + (company ? "mercenary company" : "outlaw clan") + " (" + Text(m, "source") + ")" };
                    string what = (company ? "company " : "outlaw clan ") + id;
                    if (!required(culture)) { problems.Add(what + " culture " + culture + " has no complete culture template"); continue; }
                    if (!towns.ContainsKey(clan.HomeId ?? "")) problems.Add(what + " home " + clan.HomeId + " is not a town");
                    if (!clan.Members.Contains(clan.LeaderId ?? "")) problems.Add(what + " leader " + clan.LeaderId + " is not a member");
                    foreach (var member in clan.Members)
                    {
                        IDictionary<string, object> hero;
                        if (!heroes.TryGetValue(member, out hero)) problems.Add(what + " member " + member + " is not a compiler hero");
                        else if (Text(hero, "occupation") != "Lord" || !Alive(hero) || AgeOf(hero) < AdultAge) problems.Add(what + " member " + member + " is not a living adult lord");
                        else if (Text(hero, "bucket") != (company ? "company" : "outlaw") || Text(hero, "clan") != id) problems.Add(what + " member " + member + " is planned as " + Text(hero, "bucket") + " of " + Text(hero, "clan"));
                        else if (Text(hero, "kingdom") != null) problems.Add(what + " member " + member + " is a lord of kingdom " + Text(hero, "kingdom"));
                    }
                    int tier = Int(Obj(population, company ? "companies" : "outlaws"), "tier");
                    if (clan.Tier != tier) problems.Add(what + " tier " + clan.Tier + " is not the population policy's " + tier);
                    if (!company && (clan.OutlawKind == null || !kindFlags.ContainsKey(clan.OutlawKind))) problems.Add(what + " kind " + clan.OutlawKind + " is not mafia, nomad or sect");
                    var line = TroopLine(culture, company, clan.OutlawKind);
                    if (line == null) problems.Add(what + " culture " + culture + " has no population.json troop line");
                    else if (clan.PartyTemplate != Text(line, "partyTemplate") || !clan.CharacterTemplates.SequenceEqual(List(line, "characterTemplates").Select(Str)))
                        problems.Add(what + " templates (" + clan.PartyTemplate + "; " + String.Join(", ", clan.CharacterTemplates) + ") are not its population.json troop line " + Text(line, "sourceClan"));
                    if (clan.CharacterTemplates.Count != 4 || clan.CharacterTemplates.Distinct().Count() != 4) problems.Add(what + " has " + clan.CharacterTemplates.Count + " character templates, not 4 distinct");
                    if (clan.PartyTemplate == null || lordPartyTemplates.Contains(clan.PartyTemplate)) problems.Add(what + " party template " + clan.PartyTemplate + " is a lord party template");
                    if (!company)
                    {
                        // The engine puts an outlaw clan at war with every kingdom of its culture; the plan keeps that within the war guard.
                        var expected = result.Kingdoms.Where(k => k.Culture == culture).Select(k => k.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
                        if (!clan.Wars.SequenceEqual(expected)) problems.Add(what + " records wars with " + String.Join(", ", clan.Wars) + ", not the kingdoms of its culture (" + String.Join(", ", expected) + ")");
                        if (clan.Wars.Count > maxWarKingdoms) problems.Add(what + " is at war with " + clan.Wars.Count + " kingdoms, above outlaws.maxWarKingdoms " + maxWarKingdoms);
                    }
                    else if (clan.Wars.Count > 0) problems.Add(what + " records wars");
                    int c1, c2;
                    if (!ColorPair(id, palette, usedPairs, out c1, out c2)) { problems.Add(what + " has no unique color pair"); continue; }
                    clan.Color = palette[c1].Value; clan.Color2 = palette[c2].Value;
                    var icons = List(Obj(cultures, culture), "bannerIcons").Select(Convert.ToInt32).ToList();
                    clan.BannerKey = BannerKey(banners, palette[c1].Key, palette[c2].Key, icons[Draw("banner", id, 0, icons.Count)]);
                    string homeName = towns.ContainsKey(clan.HomeId ?? "") ? Clean(Text(towns[clan.HomeId], "name")) : clan.HomeId;
                    clan.Text = Localized(id + "_text", Fill(Text(Obj(policy, "texts"), company ? "companyEncyclopedia" : "outlawEncyclopedia"), new Dictionary<string, string> { ["home"] = homeName }));
                    result.MinorClans.Add(clan);
                }

            // Clan names: the planned houses', companies' and outlaw clans' names, unique world-wide by MMR-279's name cleaner (never an ordinal).
            foreach (var group in result.AllClans.GroupBy(c => c.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
                problems.Add("clans " + String.Join(", ", group.Select(c => c.Id)) + " share the name " + group.Key);
            foreach (var clan in result.AllClans) clan.NameLocalized = Localized(clan.Id, clan.Name);
            // c1: memberships are closed. Every living house lord of a kingdom is in exactly one house of that kingdom, every company and outlaw
            // clan lord in exactly its clan, every dead parent of its house; a dissolved realm's lords are in none.
            var clanOf = new Dictionary<string, List<ClanIntent>>(StringComparer.Ordinal);
            foreach (var clan in result.AllClans)
            {
                if (!clan.Members.Contains(clan.LeaderId)) problems.Add("clan " + clan.Id + " leader is not a member");
                else if (heroes.ContainsKey(clan.LeaderId) && AgeOf(heroes[clan.LeaderId]) < AdultAge) problems.Add("clan " + clan.Id + " leader " + clan.LeaderId + " is a minor");
                foreach (var m in clan.Members) { List<ClanIntent> list; if (!clanOf.TryGetValue(m, out list)) clanOf[m] = list = new List<ClanIntent>(); list.Add(clan); }
            }
            foreach (var lord in heroes.Values.Where(h => Text(h, "occupation") == "Lord").OrderBy(h => Text(h, "id"), StringComparer.Ordinal))
            {
                string id = Text(lord, "id"), kingdom = Text(lord, "kingdom"), bucket = Text(lord, "bucket");
                if (kingdom != null && dissolvedIds.Contains(kingdom))
                {
                    if (clanOf.ContainsKey(id)) problems.Add("lord " + id + " of dissolved " + kingdom + " is in a clan");
                    result.Affiliations.Add(new Affiliation { Hero = id, Dead = !Alive(lord), Note = "unaffiliated: realm " + kingdom + " was dissolved" });
                    continue;
                }
                if (!Alive(lord))
                {
                    var house = result.Clans.FirstOrDefault(c => c.DeadParents.Contains(id));
                    if (house == null) problems.Add("dead parent " + id + " belongs to no house intent (" + Text(lord, "clan") + ")");
                    else if (clanOf.ContainsKey(id)) problems.Add("dead parent " + id + " is a clan member");
                    else result.Affiliations.Add(new Affiliation { Hero = id, Clan = house.Id, Kingdom = house.KingdomId, Dead = true, Note = "dead parent in " + house.Id });
                    continue;
                }
                List<ClanIntent> of;
                clanOf.TryGetValue(id, out of);
                int count = of == null ? 0 : of.Count;
                if (bucket == "house" && (kingdom == null || !kingdomIds.Contains(kingdom))) problems.Add("lord " + id + " belongs to no kingdom intent (" + kingdom + ")");
                else if (count != 1) problems.Add("lord " + id + " is in " + count + " clans");
                else
                {
                    var c = of[0];
                    if (c.Minor != (bucket != "house")) problems.Add("lord " + id + " of bucket " + bucket + " is a member of " + c.Kind + " " + c.Id);
                    result.Affiliations.Add(new Affiliation { Hero = id, Clan = c.Id, Kingdom = c.KingdomId,
                        Note = c.Kind == "company" ? "mercenary company" : c.Kind == "outlaw" ? "outlaw clan" : c.Ruling ? "ruling house" : c.LeaderId == id ? "house leader" : "house" });
                }
            }
            foreach (var key in result.Kingdoms.Select(k => k.Id).Concat(result.AllClans.Select(c => c.Id)))
                if (CampaignIdentity.Problem(key, prefix) != null) problems.Add("id " + key + ": " + CampaignIdentity.Problem(key, prefix));
            var allBanners = result.Kingdoms.Select(k => k.BannerKey).Concat(result.AllClans.Select(c => c.BannerKey)).Where(b => b != null).ToList();
            if (allBanners.Distinct(StringComparer.Ordinal).Count() != allBanners.Count) problems.Add("banner keys are not unique in the world");
            // Starting wars (RT-06): each campaign wars[] pair is at war from day 0, written on both kingdoms; no kingdom above
            // population.json wars.maxPerKingdom. A pair naming a dissolved kingdom is dropped (recorded).
            int maxWars = Int(Obj(population, "wars"), "maxPerKingdom");
            var byKingdom = result.Kingdoms.ToDictionary(k => k.Id, StringComparer.Ordinal);
            var warPairs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var w in Records(campaign, "wars"))
            {
                string a = Text(w, "a"), b = Text(w, "b");
                if (dissolvedIds.Contains(a ?? "") || dissolvedIds.Contains(b ?? "")) { result.DroppedWars.Add(a + "|" + b); continue; }
                if (a == null || b == null || a == b || !byKingdom.ContainsKey(a) || !byKingdom.ContainsKey(b)) { problems.Add("war " + a + "|" + b + " does not name two kingdom intents"); continue; }
                string key = String.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a;
                if (!warPairs.Add(key)) { problems.Add("war " + key + " is listed twice"); continue; }
                byKingdom[a].Wars.Add(b); byKingdom[b].Wars.Add(a);
            }
            foreach (var k in result.Kingdoms)
            {
                k.Wars.Sort(StringComparer.Ordinal);
                if (k.Wars.Count > maxWars) problems.Add("kingdom " + k.Id + " starts " + k.Wars.Count + " wars, above wars.maxPerKingdom " + maxWars);
            }
            // Diplomacy: the war pairs start at war, every other pair neutral (the engine default, written nowhere); the rulers' relation comes
            // from the relations stream and is applied by C03 once the heroes exist.
            var diplomacy = Obj(policy, "diplomacy");
            int baseRelation = Int(diplomacy, "baseRelation"), spread = Int(diplomacy, "spread");
            for (int i = 0; i < result.Kingdoms.Count; i++)
                for (int j = i + 1; j < result.Kingdoms.Count; j++)
                {
                    var a = result.Kingdoms[i]; var b = result.Kingdoms[j];
                    double u = Unit("relations", a.Id + "|" + b.Id, 0);
                    result.Relations.Add(new Relation { A = a.Id, B = b.Id, RulerA = a.RulerId, RulerB = b.RulerId, Stance = a.Wars.Contains(b.Id) ? "war" : "neutral",
                                                        RulerRelation = baseRelation + (int)Math.Round(spread * (2 * u - 1), MidpointRounding.AwayFromZero) });
                }
            // Generated cultures: each source culture uid, its civilization and its presentation.
            foreach (var group in result.Kingdoms.GroupBy(k => k.CultureUid ?? k.Civilization).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var first = group.First();
                result.Cultures.Add(new CultureIntent { Uid = group.Key, Civilization = first.Civilization, CivilizationName = first.CivilizationName, Presentation = first.Culture,
                                                        Template = "worldgen/templates/cultures/" + first.Culture + ".json", Kingdoms = group.Select(k => k.Id).ToList() });
            }
            if (problems.Count > 0) throw new FactionException(problems);
            result.Documents.Add(new KeyValuePair<string, XmlDocument>("Kingdoms", KingdomsXml(result)));
            result.Documents.Add(new KeyValuePair<string, XmlDocument>("Factions", FactionsXml(result)));
            result.DeclaredKeys.AddRange(result.Kingdoms.Select(k => "Kingdom." + k.Id).Concat(result.AllClans.Select(c => "Faction." + c.Id)));
            result.IntentsJson = Canonical(result);
            result.IntentsSha256 = Sha256(result.IntentsJson);
            return result;
        }

        // A hero id that names a Lord of the house (living or a dead parent).
        private static bool Parent(Dictionary<string, IDictionary<string, object>> heroes, string id, string house)
        {
            IDictionary<string, object> h;
            return id != null && heroes.TryGetValue(id, out h) && Text(h, "occupation") == "Lord" && Text(h, "clan") == house;
        }

        // The population.json troop line of a minor clan: the culture's mercenary line, or its first outlaw line of the clan's kind, else its first.
        private IDictionary<string, object> TroopLine(string culture, bool company, string kind)
        {
            var c = Obj(Obj(Obj(population, "troopTemplates"), "cultures"), culture ?? "");
            if (c == null) return null;
            if (company) return Obj(c, "mercenary");
            var lines = List(c, "outlaws").OfType<IDictionary<string, object>>().ToList();
            return lines.FirstOrDefault(l => Text(l, "kind") == kind) ?? lines.FirstOrDefault();
        }

        // An ordered pair of distinct palette colours not used yet in the world, drawn in pairs from the colors stream.
        private bool ColorPair(string entity, List<KeyValuePair<int, string>> palette, HashSet<string> used, out int c1, out int c2)
        {
            c1 = -1; c2 = -1;
            for (int draw = 0; draw < 4096 && c1 < 0; draw++)
            {
                int a = Draw("colors", entity, 2 * draw, palette.Count), b = Draw("colors", entity, 2 * draw + 1, palette.Count);
                if (palette[a].Key != palette[b].Key && used.Add(palette[a].Key + "." + palette[b].Key)) { c1 = a; c2 = b; }
            }
            return c1 >= 0;
        }

        private static int Tier(IDictionary<string, object> block, int fiefs)
        {
            int tier = 0;
            foreach (var row in List(block, "tierByFiefs").OfType<IDictionary<string, object>>())
                if (fiefs >= Int(row, "minFiefs")) tier = Math.Max(tier, Int(row, "tier"));
            return tier;
        }

        private static string BannerKey(IDictionary<string, object> banners, int c1, int c2, int icon)
        {
            int bg = Int(banners, "backgroundMesh"), bgSize = Int(banners, "backgroundSize"), iconSize = Int(banners, "iconSize"), center = Int(banners, "center");
            return String.Join(".", new[] { bg, c1, c1, bgSize, bgSize, center, center, 1, 0, 0, icon, c2, c2, iconSize, iconSize, center, center, 1, 0, 0 }.Select(v => v.ToString(CultureInfo.InvariantCulture)));
        }

        private ulong Stream(string stream, string entity, int draw)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(new UTF8Encoding(false).GetBytes("mnbl.faction-stream/1\n" + stream + "\n" + streams[stream].ToString(CultureInfo.InvariantCulture) + "\n" +
                                                                               prefix + "\n" + entity + "\n" + draw.ToString(CultureInfo.InvariantCulture)));
                ulong value = 0;
                for (int i = 0; i < 8; i++) value = (value << 8) | bytes[i];
                return value;
            }
        }

        private int Draw(string stream, string entity, int draw, int count)
        {
            if (count <= 0) throw new FactionException(new[] { "empty " + stream + " pool for " + entity });
            return (int)(Stream(stream, entity, draw) % (ulong)count);
        }

        private double Unit(string stream, string entity, int draw) { return (Stream(stream, entity, draw) >> 11) / (double)(1UL << 53); }

        // Only attributes the installed Kingdom.Deserialize reads. <relationships> carries the starting wars as the installed spkingdoms.xml
        // writes them: value -1 (it reads only the sign; below 0 calls FactionManager.DeclareWar) and isAtWar true, on both kingdoms.
        private XmlDocument KingdomsXml(Result r)
        {
            var doc = new XmlDocument(); var root = doc.CreateElement("Kingdoms"); doc.AppendChild(root);
            string relation = Int(Obj(policy, "diplomacy"), "warRelation").ToString(CultureInfo.InvariantCulture);
            foreach (var k in r.Kingdoms)
            {
                var e = Element(doc, root, "Kingdom", "id", k.Id, "owner", "Hero." + k.RulerId, "banner_key", k.BannerKey, "primary_banner_color", k.PrimaryBannerColor,
                    "secondary_banner_color", k.SecondaryBannerColor, "color", k.Color, "color2", k.Color2, "culture", "Culture." + k.Culture, "name", k.NameLocalized,
                    "short_name", k.NameLocalized, "title", k.Title, "ruler_title", k.RulerTitle, "text", k.Text, "initial_home_settlement", "Settlement." + k.CapitalId);
                if (k.Wars.Count > 0)
                {
                    var relationships = doc.CreateElement("relationships"); e.AppendChild(relationships);
                    foreach (var w in k.Wars) Element(doc, relationships, "relationship", "kingdom", "Kingdom." + w, "value", relation, "isAtWar", "true");
                }
                var policies = doc.CreateElement("policies"); e.AppendChild(policies);
                foreach (var p in k.Policies) Element(doc, policies, "policy", "id", p);
            }
            return doc;
        }

        // Houses are noble clans of their kingdom; companies and outlaw clans are minor clans with the flags of their kind (as the installed
        // spclans.xml writes them), their troop line and no super_faction.
        private XmlDocument FactionsXml(Result r)
        {
            var doc = new XmlDocument(); var root = doc.CreateElement("Factions"); doc.AppendChild(root);
            foreach (var c in r.Clans.OrderBy(c => c.Ruling ? 0 : 1).ThenBy(c => c.Id, StringComparer.Ordinal))
                Element(doc, root, "Faction", "id", c.Id, "owner", "Hero." + c.LeaderId, "super_faction", "Kingdom." + c.KingdomId, "name", c.NameLocalized, "short_name", c.NameLocalized,
                    "culture", "Culture." + c.Culture, "tier", c.Tier.ToString(CultureInfo.InvariantCulture), "banner_key", c.BannerKey, "is_noble", "true",
                    "initial_home_settlement", "Settlement." + c.HomeId, "text", c.Text);
            var minor = Obj(policy, "minorClans");
            var kindFlags = Obj(minor, "kindFlags");
            foreach (var c in r.MinorClans.OrderBy(c => c.Kind, StringComparer.Ordinal).ThenBy(c => c.Id, StringComparer.Ordinal))
            {
                var e = Element(doc, root, "Faction", "id", c.Id, "owner", "Hero." + c.LeaderId, "name", c.NameLocalized, "short_name", c.NameLocalized,
                    "culture", "Culture." + c.Culture, "tier", c.Tier.ToString(CultureInfo.InvariantCulture), "banner_key", c.BannerKey, "color", c.Color, "color2", c.Color2,
                    "default_party_template", "PartyTemplate." + c.PartyTemplate, "initial_home_settlement", "Settlement." + c.HomeId, "text", c.Text);
                foreach (var flag in List(minor, c.Kind == "company" ? "companyFlags" : "outlawFlags").Select(Str)) e.SetAttribute(flag, "true");
                if (c.Kind == "outlaw") e.SetAttribute(Text(kindFlags, c.OutlawKind), "true");
                var list = doc.CreateElement("minor_faction_character_templates"); e.AppendChild(list);
                foreach (var t in c.CharacterTemplates) Element(doc, list, "template", "id", "NPCCharacter." + t);
            }
            return doc;
        }

        private static XmlElement Element(XmlDocument doc, XmlNode parent, string name, params string[] attributes)
        {
            var e = doc.CreateElement(name);
            for (int i = 0; i < attributes.Length; i += 2) e.SetAttribute(attributes[i], attributes[i + 1]);
            parent.AppendChild(e);
            return e;
        }

        // Text that is legal in an XML attribute: control characters, characters XML forbids (U+FFFE, U+FFFF) and unpaired surrogates
        // are dropped; a well-formed surrogate pair is kept.
        // MMR-286: the encyclopedia keeps a renamed record's FWGC++ source name (texts.sourceNote, " ... {source}."); empty when not renamed.
        public static string SourceNote(string note, string sourceName)
        {
            return String.IsNullOrEmpty(sourceName) || String.IsNullOrEmpty(note) ? "" : Fill(note, new Dictionary<string, string> { ["source"] = Clean(sourceName) });
        }

        public static string Clean(string text)
        {
            var s = text ?? "";
            var b = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (Char.IsHighSurrogate(ch) && i + 1 < s.Length && Char.IsLowSurrogate(s[i + 1])) { b.Append(ch).Append(s[i + 1]); i++; continue; }
                if (Char.IsSurrogate(ch) || Char.IsControl(ch) || !XmlConvert.IsXmlChar(ch)) continue;
                b.Append(ch);
            }
            return b.ToString();
        }

        // A localized string "{=id}text": the text is cleaned and its braces become parentheses (TextObject reads braces as
        // variables); XML escaping happens when the attribute is written.
        public static string Localized(string id, string text)
        {
            return "{=" + id + "}" + Clean(text).Replace('{', '(').Replace('}', ')');
        }

        // One-pass substitution of {name} placeholders; a value containing a placeholder stays literal.
        public static string Fill(string template, IDictionary<string, string> values)
        {
            var b = new StringBuilder();
            for (int i = 0; i < template.Length; i++)
            {
                int close = template[i] == '{' ? template.IndexOf('}', i) : -1;
                string key = close > i ? template.Substring(i + 1, close - i - 1) : null;
                if (key != null && values.ContainsKey(key)) { b.Append(values[key]); i = close; }
                else b.Append(template[i]);
            }
            return b.ToString();
        }

        private static string Canonical(Result r)
        {
            var o = new SortedDictionary<string, object>(StringComparer.Ordinal) {
                ["schema"] = "mnbl.faction-intents", ["version"] = 3, ["worldPrefix"] = r.WorldPrefix,
                ["kingdoms"] = r.Kingdoms.Select(k => new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["id"] = k.Id, ["uid"] = k.Uid, ["name"] = k.Name, ["sourceName"] = k.SourceName, ["culture"] = k.Culture, ["cultureUid"] = k.CultureUid, ["civilization"] = k.Civilization,
                    ["ruler"] = k.RulerId, ["rulingClan"] = k.RulingClanId, ["capital"] = k.CapitalId, ["towns"] = k.Towns, ["castles"] = k.Castles, ["fiefs"] = k.Fiefs,
                    ["banner"] = k.BannerKey, ["color"] = k.Color, ["color2"] = k.Color2, ["policies"] = k.Policies, ["rulerTitle"] = k.RulerTitle, ["text"] = k.Text, ["wars"] = k.Wars }).ToList(),
                ["clans"] = r.AllClans.Select(c => new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["id"] = c.Id, ["uid"] = c.Uid, ["kind"] = c.Kind, ["outlawKind"] = c.OutlawKind, ["name"] = c.Name, ["sourceName"] = c.SourceName, ["culture"] = c.Culture,
                    ["kingdom"] = c.KingdomId, ["leader"] = c.LeaderId, ["members"] = c.Members, ["deadParents"] = c.DeadParents, ["fiefs"] = c.Fiefs, ["home"] = c.HomeId,
                    ["tier"] = c.Tier, ["ruling"] = c.Ruling, ["banner"] = c.BannerKey, ["color"] = c.Color, ["color2"] = c.Color2, ["partyTemplate"] = c.PartyTemplate,
                    ["characterTemplates"] = c.CharacterTemplates, ["wars"] = c.Wars, ["provenance"] = c.Provenance }).ToList(),
                ["relations"] = r.Relations.Select(x => new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["a"] = x.A, ["b"] = x.B, ["rulerA"] = x.RulerA, ["rulerB"] = x.RulerB, ["rulerRelation"] = x.RulerRelation, ["stance"] = x.Stance }).ToList(),
                ["affiliations"] = r.Affiliations.Select(x => new SortedDictionary<string, object>(StringComparer.Ordinal) { ["hero"] = x.Hero, ["clan"] = x.Clan, ["kingdom"] = x.Kingdom, ["dead"] = x.Dead, ["note"] = x.Note }).ToList(),
                ["cultures"] = r.Cultures.Select(c => new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["uid"] = c.Uid, ["civilization"] = c.Civilization, ["civilizationName"] = c.CivilizationName, ["presentation"] = c.Presentation, ["template"] = c.Template, ["kingdoms"] = c.Kingdoms }).ToList(),
                ["dissolved"] = r.Dissolved.Select(d => new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["kingdom"] = d.Kingdom, ["uid"] = d.Uid, ["reason"] = d.Reason, ["lords"] = d.Lords, ["castles"] = d.Castles, ["clans"] = d.Clans }).ToList(),
                ["droppedWars"] = r.DroppedWars
            };
            return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(o);
        }

        private static string Sha256(string text) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(new UTF8Encoding(false).GetBytes(text))).Replace("-", "").ToLowerInvariant(); }
        private static IDictionary<string, object> Obj(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) ? v as IDictionary<string, object> : null; }
        private static string Text(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) ? v as string : null; }
        private static int Int(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) && v != null ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : 0; }
        private static string Str(object v) { return Convert.ToString(v, CultureInfo.InvariantCulture); }
        private static IEnumerable<object> List(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) && v is IEnumerable && !(v is string) ? ((IEnumerable)v).Cast<object>() : Enumerable.Empty<object>(); }
        private static IEnumerable<IDictionary<string, object>> Records(IDictionary<string, object> o, string key) { return List(o, key).OfType<IDictionary<string, object>>(); }
    }
}
