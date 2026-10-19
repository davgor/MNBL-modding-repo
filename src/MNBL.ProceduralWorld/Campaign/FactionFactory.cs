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
    // MMR-31: faction definitions (docs/factions.md). Pure managed. From the MMR-27 compiler records (campaign.json, id-map.json)
    // and the faction policy (worldgen/policies/factions.json) it builds closed kingdom and clan intents, the generated
    // cultures' presentation, the kingdom stances and ruler relations, the lord-to-clan affiliations C03 writes into Heroes.xml,
    // and the Kingdoms and Factions XML the registry loads (MMR-30). Identities, ownership and relationships come from the
    // generated world; assets (official cultures, banner meshes, palette, icons, policies, title strings) are the installed public
    // ones the policy lists. No engine object is created: heroes and settlements are referenced by their compiler ids and
    // instantiated later (C03 MMR-32, C04 MMR-33), and registered closure is proven by C07 (MMR-36).
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
            public string BannerKey, Color, Color2, PrimaryBannerColor, SecondaryBannerColor, RulerTitle, Title, Text;
            public int Fiefs, ColorIndex1, ColorIndex2;
        }

        public sealed class ClanIntent
        {
            public string Id, Uid, Name, SourceName, NameLocalized, Culture, KingdomId, LeaderId, HomeId, BannerKey, Text, Provenance;
            public List<string> Members = new List<string>();
            public int Tier;
            public bool Ruling;
        }

        // A kingdom pair: its starting stance (always neutral, written nowhere: the engine default) and the relation between the
        // two rulers, a hero relation intent C03 applies once the heroes exist.
        public sealed class Relation { public string A, B, RulerA, RulerB, Stance; public int RulerRelation; }
        // A lord's clan and kingdom as C03 must write them into Heroes.xml (faction="Faction.<clan>"); null for an unaffiliated lord.
        public sealed class Affiliation { public string Hero, Clan, Kingdom, Note; }
        public sealed class CultureIntent { public string Uid, Civilization, CivilizationName, Presentation, Template; public List<string> Kingdoms = new List<string>(); }
        public sealed class Dissolution { public string Kingdom, Uid, Reason; public List<string> Lords = new List<string>(), Castles = new List<string>(), Clans = new List<string>(); }

        public sealed class Result
        {
            public string WorldPrefix;
            public List<KingdomIntent> Kingdoms = new List<KingdomIntent>();
            public List<ClanIntent> Clans = new List<ClanIntent>();
            public List<Relation> Relations = new List<Relation>();
            public List<Affiliation> Affiliations = new List<Affiliation>();
            public List<CultureIntent> Cultures = new List<CultureIntent>();
            public List<Dissolution> Dissolved = new List<Dissolution>();
            public List<KeyValuePair<string, XmlDocument>> Documents = new List<KeyValuePair<string, XmlDocument>>();
            public List<string> DeclaredKeys = new List<string>();
            public string IntentsJson, IntentsSha256;
        }

        public sealed class FactionException : InvalidOperationException
        {
            public FactionException(IList<string> problems) : base("Faction definitions refused: " + String.Join("; ", problems.Take(20)) + (problems.Count > 20 ? "; ..." : "")) { Problems = problems; }
            public IList<string> Problems { get; private set; }
        }

        // Colliding names take the policy's ordinal form counting from 2, as the MMR-27 compiler's UniqueNames does.
        private sealed class UniqueNames
        {
            private readonly HashSet<string> used;
            private readonly string ordinal;
            public UniqueNames(string ordinal, IEnumerable<string> taken) { this.ordinal = ordinal; used = new HashSet<string>(taken.Where(n => n != null), StringComparer.Ordinal); }
            public string Take(string wanted)
            {
                if (used.Add(wanted)) return wanted;
                for (int n = 2; ; n++)
                {
                    string candidate = Fill(ordinal, new Dictionary<string, string> { ["name"] = wanted, ["n"] = n.ToString(CultureInfo.InvariantCulture) });
                    if (used.Add(candidate)) return candidate;
                }
            }
        }

        private readonly IDictionary<string, object> policy;
        private readonly Dictionary<string, int> streams = new Dictionary<string, int>(StringComparer.Ordinal);
        private string prefix;

        private FactionFactory(IDictionary<string, object> policy)
        {
            this.policy = policy;
            var s = Obj(policy, "streams");
            foreach (var name in new[] { "banner", "colors", "relations" }) streams[name] = Int(s, name);
        }

        // campaign: campaign.json (mnbl.campaign-records v1). idMap: id-map.json (every id the compiler assigned or reserved).
        // factions: worldgen/policies/factions.json. presentations: worldgen/policies/culture-presentations.json. templates:
        // official culture id -> worldgen/templates/cultures/<culture>.json.
        public static Result Build(IDictionary<string, object> campaign, IDictionary<string, object> idMap, IDictionary<string, object> factions,
                                   IDictionary<string, object> presentations, IDictionary<string, IDictionary<string, object>> templates)
        {
            return new FactionFactory(factions).Run(campaign, idMap, presentations, templates);
        }

        private Result Run(IDictionary<string, object> campaign, IDictionary<string, object> idMap, IDictionary<string, object> presentations,
                           IDictionary<string, IDictionary<string, object>> templates)
        {
            var problems = new List<string>();
            var result = new Result();
            if (Text(campaign, "schema") != "mnbl.campaign-records") problems.Add("not mnbl.campaign-records");
            if (Text(policy, "schema") != "mnbl.factions" || Int(policy, "version") != 1) problems.Add("not mnbl.factions version 1");
            var heroes = Records(campaign, "heroes").ToDictionary(h => Text(h, "id"), StringComparer.Ordinal);
            var towns = Records(campaign, "towns").ToDictionary(t => Text(t, "id"), StringComparer.Ordinal);
            var clans = Records(campaign, "clans").ToDictionary(c => Text(c, "id"), StringComparer.Ordinal);
            prefix = heroes.Keys.Concat(towns.Keys).Select(CampaignIdentity.WorldPrefix).FirstOrDefault(p => p != null);
            result.WorldPrefix = prefix;
            if (prefix == null) problems.Add("no world-qualified record");
            var banners = Obj(policy, "banners");
            var palette = List(banners, "palette").OfType<IDictionary<string, object>>().Select(c => new KeyValuePair<int, string>(Int(c, "id"), Text(c, "argb"))).ToList();
            if (palette.Select(c => c.Key).Distinct().Count() < 2) problems.Add("the banner palette has fewer than two colors");
            string ordinal = Text(Obj(policy, "names"), "ordinal") ?? "";
            if (!ordinal.Contains("{name}") || !ordinal.Contains("{n}")) problems.Add("names.ordinal must contain {name} and {n}");
            if (problems.Count > 0) throw new FactionException(problems);

            var reserved = new HashSet<string>(AssignedIds(idMap), StringComparer.Ordinal);
            var cultures = Obj(policy, "cultures");
            var civilizations = List(presentations, "civilizations").OfType<IDictionary<string, object>>().ToDictionary(c => Text(c, "civilization"), c => Text(c, "culture"), StringComparer.Ordinal);
            // A presentation is usable when the policy lists it and its template is an installed main culture's with every required
            // contract member set. For the installed cultures requiredMissing is empty by construction; this guards any other template.
            var required = new Func<string, bool>(culture => {
                IDictionary<string, object> template;
                return culture != null && cultures.ContainsKey(culture) && templates.TryGetValue(culture, out template) && Text(template, "culture") == culture &&
                       !List(template, "requiredMissing").Any() && List(Obj(cultures, culture), "bannerIcons").Any();
            });
            var vassal = Obj(policy, "vassalClans");
            var leaderRoles = new HashSet<string>(List(vassal, "leaderRoles").Select(Str), StringComparer.Ordinal);
            var usedPairs = new HashSet<string>(StringComparer.Ordinal);
            string action = Text(Obj(policy, "zeroTownRealm"), "action");
            var lords = heroes.Values.Where(h => Text(h, "occupation") == "Lord").OrderBy(h => Text(h, "id"), StringComparer.Ordinal).ToList();

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
                    Lords = lords.Where(h => Text(h, "kingdom") == id).Select(h => Text(h, "id")).ToList(),
                    Castles = List(k, "castles").Select(Str).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    Clans = clans.Values.Where(c => Text(c, "kingdom") == id).Select(c => Text(c, "id")).OrderBy(x => x, StringComparer.Ordinal).ToList() });
            }
            var kingdomNames = new UniqueNames(ordinal, Enumerable.Empty<string>());
            var displayName = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var k in kept.OrderBy(k => Text(k, "uid"), StringComparer.Ordinal).ThenBy(k => Text(k, "id"), StringComparer.Ordinal))
                displayName[Text(k, "id")] = kingdomNames.Take(Clean(Text(k, "name")));

            foreach (var k in kept)
            {
                string id = Text(k, "id");
                IDictionary<string, object> rulingRecord;
                clans.TryGetValue(Text(k, "rulingClan") ?? "", out rulingRecord);
                string culture = Text(k, "presentation");
                var intent = new KingdomIntent {
                    Id = id, Uid = Text(k, "uid"), SourceName = Text(k, "name"), Name = displayName[id], Culture = culture, CultureUid = Text(k, "cultureUid"),
                    Civilization = Text(k, "civilization"), CivilizationName = Clean(Text(k, "civilizationName")), RulerId = Text(k, "ruler"), RulingClanId = Text(k, "rulingClan"),
                    CapitalId = Text(k, "capital"), Towns = List(k, "towns").Select(Str).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    Castles = List(k, "castles").Select(Str).OrderBy(x => x, StringComparer.Ordinal).ToList() };
                intent.NameLocalized = Localized(id, intent.Name);
                intent.Fiefs = intent.Towns.Count + intent.Castles.Count;
                // c1: the ruler, ruling clan and capital resolve in the compiler graph, and the ruler leads the ruling clan.
                IDictionary<string, object> ruler;
                if (!heroes.TryGetValue(intent.RulerId ?? "", out ruler)) problems.Add("kingdom " + id + " ruler " + intent.RulerId + " is not a compiler hero");
                else if (Text(ruler, "occupation") != "Lord") problems.Add("kingdom " + id + " ruler " + intent.RulerId + " is not a lord");
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
                    ["realm"] = intent.Name, ["civilization"] = intent.CivilizationName, ["capital"] = capitalName }));
                // Colours: an ordered pair of distinct palette colours, unique among kingdoms, drawn in pairs from the colors stream.
                int c1 = -1, c2 = -1;
                for (int draw = 0; draw < 4096 && c1 < 0; draw++)
                {
                    int a = Draw("colors", id, 2 * draw, palette.Count), b = Draw("colors", id, 2 * draw + 1, palette.Count);
                    if (palette[a].Key != palette[b].Key && usedPairs.Add(palette[a].Key + "." + palette[b].Key)) { c1 = a; c2 = b; }
                }
                if (c1 < 0) { problems.Add("kingdom " + id + " has no unique color pair"); continue; }
                intent.ColorIndex1 = c1; intent.ColorIndex2 = c2;
                intent.Color = palette[c1].Value; intent.Color2 = palette[c2].Value;
                intent.PrimaryBannerColor = "0x" + palette[c1].Value.ToLowerInvariant(); intent.SecondaryBannerColor = "0x" + palette[c2].Value.ToLowerInvariant();
                result.Kingdoms.Add(intent);

                // Clans: the ruling clan, then one vassal clan per lord with a leader role. Every member is a lord of this kingdom.
                if (rulingRecord == null) continue;
                var members = List(rulingRecord, "members").Select(Str).OrderBy(x => x, StringComparer.Ordinal).ToList();
                foreach (var m in members)
                {
                    IDictionary<string, object> hero;
                    if (!heroes.TryGetValue(m, out hero)) problems.Add("ruling clan " + intent.RulingClanId + " member " + m + " is not a compiler hero");
                    else if (Text(hero, "occupation") != "Lord") problems.Add("ruling clan " + intent.RulingClanId + " member " + m + " is a " + Text(hero, "occupation") + ", not a lord");
                    else if (Text(hero, "kingdom") != id) problems.Add("ruling clan " + intent.RulingClanId + " member " + m + " is not a lord of " + id);
                }
                var leaders = members.Where(m => m != intent.RulerId && heroes.ContainsKey(m) && leaderRoles.Contains(Text(heroes[m], "role")))
                                     .OrderBy(m => Text(heroes[m], "uid"), StringComparer.Ordinal).ToList();
                string rulingCulture = Text(rulingRecord, "presentation");
                if (!required(rulingCulture)) problems.Add("ruling clan " + intent.RulingClanId + " culture " + rulingCulture + " has no complete culture template");
                var ruling = new ClanIntent {
                    Id = intent.RulingClanId, Uid = Text(rulingRecord, "uid"), SourceName = Text(rulingRecord, "name"), Name = Clean(Text(rulingRecord, "name")),
                    Culture = rulingCulture, KingdomId = id, LeaderId = intent.RulerId, HomeId = intent.CapitalId, Ruling = true,
                    Tier = Tier(intent.Fiefs), Members = members.Where(m => !leaders.Contains(m)).ToList(), Provenance = "MMR-27 ruling clan of " + intent.Uid };
                ruling.Text = Localized(ruling.Id + "_text", Fill(Text(Obj(policy, "texts"), "clanEncyclopedia"), new Dictionary<string, string> { ["realm"] = intent.Name }));
                var kingdomClans = new List<ClanIntent> { ruling };
                foreach (var leaderId in leaders)
                {
                    var leader = heroes[leaderId];
                    string uid = "clan:" + Text(leader, "uid"), clanId = StableId("clan", "l", uid);
                    if (reserved.Contains(clanId) || clans.ContainsKey(clanId) || result.Clans.Concat(kingdomClans).Any(c => c.Id == clanId)) { problems.Add("vassal clan id " + clanId + " for " + uid + " collides with an assigned id"); continue; }
                    reserved.Add(clanId);
                    string leaderCulture = Text(leader, "presentation");
                    if (!required(leaderCulture)) problems.Add("vassal clan " + clanId + " leader " + leaderId + " culture " + leaderCulture + " has no complete culture template");
                    var clan = new ClanIntent {
                        Id = clanId, Uid = uid, SourceName = Fill(Text(vassal, "nameTemplate"), new Dictionary<string, string> { ["leader"] = Text(leader, "name") }), Culture = leaderCulture,
                        KingdomId = id, LeaderId = leaderId, HomeId = intent.CapitalId, Tier = Int(Obj(vassal, "tierByRole"), Text(leader, "role")),
                        Members = new List<string> { leaderId }, Provenance = "vassal clan of " + intent.Uid + " founded by " + Text(leader, "role") + " " + Text(leader, "uid") };
                    clan.Name = Clean(clan.SourceName);
                    clan.Text = Localized(clan.Id + "_text", Fill(Text(Obj(policy, "texts"), "clanEncyclopedia"), new Dictionary<string, string> { ["realm"] = intent.Name }));
                    kingdomClans.Add(clan);
                }
                // Banners: the kingdom's icon is one banner-stream draw; each clan (ruling first, then vassals by id) draws until its icon
                // differs from the kingdom's and every earlier clan's. Colour pairs separate kingdoms, so every banner is unique.
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
            // Clan names: unique among all clans (the compiler's ruling clan names first), vassal clans in uid order.
            var clanNames = new UniqueNames(ordinal, result.Clans.Where(c => c.Ruling).Select(c => c.Name));
            foreach (var group in result.Clans.Where(c => c.Ruling).GroupBy(c => c.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
                problems.Add("ruling clans " + String.Join(", ", group.Select(c => c.Id)) + " share the name " + group.Key);
            foreach (var clan in result.Clans.Where(c => !c.Ruling).OrderBy(c => c.Uid, StringComparer.Ordinal)) clan.Name = clanNames.Take(clan.Name);
            foreach (var clan in result.Clans) clan.NameLocalized = Localized(clan.Id, clan.Name);
            // c1: memberships are closed. Every lord of a kingdom is in exactly one clan of that kingdom; a dissolved realm's lords are in none.
            var clanOf = new Dictionary<string, List<ClanIntent>>(StringComparer.Ordinal);
            foreach (var clan in result.Clans)
            {
                if (!clan.Members.Contains(clan.LeaderId)) problems.Add("clan " + clan.Id + " leader is not a member");
                foreach (var m in clan.Members) { List<ClanIntent> list; if (!clanOf.TryGetValue(m, out list)) clanOf[m] = list = new List<ClanIntent>(); list.Add(clan); }
            }
            var dissolvedIds = new HashSet<string>(result.Dissolved.Select(d => d.Kingdom), StringComparer.Ordinal);
            var kingdomIds = new HashSet<string>(result.Kingdoms.Select(k => k.Id), StringComparer.Ordinal);
            foreach (var lord in lords)
            {
                string id = Text(lord, "id"), kingdom = Text(lord, "kingdom");
                List<ClanIntent> list;
                clanOf.TryGetValue(id, out list);
                int count = list == null ? 0 : list.Count;
                if (kingdom != null && dissolvedIds.Contains(kingdom))
                {
                    if (count > 0) problems.Add("lord " + id + " of dissolved " + kingdom + " is in a clan");
                    result.Affiliations.Add(new Affiliation { Hero = id, Note = "unaffiliated: realm " + kingdom + " was dissolved" });
                }
                else if (kingdom == null || !kingdomIds.Contains(kingdom)) problems.Add("lord " + id + " belongs to no kingdom intent (" + kingdom + ")");
                else if (count != 1) problems.Add("lord " + id + " is in " + count + " clans");
                else result.Affiliations.Add(new Affiliation { Hero = id, Clan = list[0].Id, Kingdom = kingdom, Note = list[0].Ruling ? "ruling clan" : list[0].LeaderId == id ? "vassal clan leader" : "vassal clan" });
            }
            foreach (var key in result.Kingdoms.Select(k => k.Id).Concat(result.Clans.Select(c => c.Id)))
                if (CampaignIdentity.Problem(key, prefix) != null) problems.Add("id " + key + ": " + CampaignIdentity.Problem(key, prefix));
            var allBanners = result.Kingdoms.Select(k => k.BannerKey).Concat(result.Clans.Select(c => c.BannerKey)).Where(b => b != null).ToList();
            if (allBanners.Distinct(StringComparer.Ordinal).Count() != allBanners.Count) problems.Add("banner keys are not unique in the world");
            // Diplomacy: every kingdom pair starts neutral (the engine default, so nothing is written); the rulers' relation comes from
            // the relations stream and is applied by C03 once the heroes exist.
            var diplomacy = Obj(policy, "diplomacy");
            if (Text(diplomacy, "stance") != "neutral") problems.Add("diplomacy.stance must be neutral");
            int baseRelation = Int(diplomacy, "baseRelation"), spread = Int(diplomacy, "spread");
            for (int i = 0; i < result.Kingdoms.Count; i++)
                for (int j = i + 1; j < result.Kingdoms.Count; j++)
                {
                    var a = result.Kingdoms[i]; var b = result.Kingdoms[j];
                    double u = Unit("relations", a.Id + "|" + b.Id, 0);
                    result.Relations.Add(new Relation { A = a.Id, B = b.Id, RulerA = a.RulerId, RulerB = b.RulerId, Stance = "neutral",
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
            result.DeclaredKeys.AddRange(result.Kingdoms.Select(k => "Kingdom." + k.Id).Concat(result.Clans.Select(c => "Faction." + c.Id)));
            result.IntentsJson = Canonical(result);
            result.IntentsSha256 = Sha256(result.IntentsJson);
            return result;
        }

        private int Tier(int fiefs)
        {
            int tier = 0;
            foreach (var row in List(Obj(policy, "rulingClan"), "tierByFiefs").OfType<IDictionary<string, object>>())
                if (fiefs >= Int(row, "minFiefs")) tier = Math.Max(tier, Int(row, "tier"));
            return tier;
        }

        private static string BannerKey(IDictionary<string, object> banners, int c1, int c2, int icon)
        {
            int bg = Int(banners, "backgroundMesh"), bgSize = Int(banners, "backgroundSize"), iconSize = Int(banners, "iconSize"), center = Int(banners, "center");
            return String.Join(".", new[] { bg, c1, c1, bgSize, bgSize, center, center, 1, 0, 0, icon, c2, c2, iconSize, iconSize, center, center, 1, 0, 0 }.Select(v => v.ToString(CultureInfo.InvariantCulture)));
        }

        // The id MMR-27 gives a synthesized record (worldbridge stable_ids.cpp): <prefix><code>_<sha256("mnbl.stable-id/1\n" + kind + "\n" + uid)[:10]>.
        private string StableId(string kind, string code, string uid)
        {
            return prefix + code + "_" + Sha256("mnbl.stable-id/1\n" + kind + "\n" + uid).Substring(0, 10);
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

        // Only attributes the installed Kingdom.Deserialize reads. No <relationships>: it reads only a value's sign (below 0 declares war),
        // and every pair starts neutral, the default.
        private XmlDocument KingdomsXml(Result r)
        {
            var doc = new XmlDocument(); var root = doc.CreateElement("Kingdoms"); doc.AppendChild(root);
            foreach (var k in r.Kingdoms)
            {
                var e = Element(doc, root, "Kingdom", "id", k.Id, "owner", "Hero." + k.RulerId, "banner_key", k.BannerKey, "primary_banner_color", k.PrimaryBannerColor,
                    "secondary_banner_color", k.SecondaryBannerColor, "color", k.Color, "color2", k.Color2, "culture", "Culture." + k.Culture, "name", k.NameLocalized,
                    "short_name", k.NameLocalized, "title", k.Title, "ruler_title", k.RulerTitle, "text", k.Text, "initial_home_settlement", "Settlement." + k.CapitalId);
                var policies = doc.CreateElement("policies"); e.AppendChild(policies);
                foreach (var p in k.Policies) Element(doc, policies, "policy", "id", p);
            }
            return doc;
        }

        private XmlDocument FactionsXml(Result r)
        {
            var doc = new XmlDocument(); var root = doc.CreateElement("Factions"); doc.AppendChild(root);
            foreach (var c in r.Clans.OrderBy(c => c.Ruling ? 0 : 1).ThenBy(c => c.Id, StringComparer.Ordinal))
                Element(doc, root, "Faction", "id", c.Id, "owner", "Hero." + c.LeaderId, "super_faction", "Kingdom." + c.KingdomId, "name", c.NameLocalized, "short_name", c.NameLocalized,
                    "culture", "Culture." + c.Culture, "tier", c.Tier.ToString(CultureInfo.InvariantCulture), "banner_key", c.BannerKey, "is_noble", "true",
                    "initial_home_settlement", "Settlement." + c.HomeId, "text", c.Text);
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
                ["schema"] = "mnbl.faction-intents", ["version"] = 2, ["worldPrefix"] = r.WorldPrefix,
                ["kingdoms"] = r.Kingdoms.Select(k => new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["id"] = k.Id, ["uid"] = k.Uid, ["name"] = k.Name, ["sourceName"] = k.SourceName, ["culture"] = k.Culture, ["cultureUid"] = k.CultureUid, ["civilization"] = k.Civilization,
                    ["ruler"] = k.RulerId, ["rulingClan"] = k.RulingClanId, ["capital"] = k.CapitalId, ["towns"] = k.Towns, ["castles"] = k.Castles, ["fiefs"] = k.Fiefs,
                    ["banner"] = k.BannerKey, ["color"] = k.Color, ["color2"] = k.Color2, ["policies"] = k.Policies, ["rulerTitle"] = k.RulerTitle, ["text"] = k.Text }).ToList(),
                ["clans"] = r.Clans.Select(c => new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["id"] = c.Id, ["uid"] = c.Uid, ["name"] = c.Name, ["sourceName"] = c.SourceName, ["culture"] = c.Culture, ["kingdom"] = c.KingdomId, ["leader"] = c.LeaderId,
                    ["members"] = c.Members, ["home"] = c.HomeId, ["tier"] = c.Tier, ["ruling"] = c.Ruling, ["banner"] = c.BannerKey, ["provenance"] = c.Provenance }).ToList(),
                ["relations"] = r.Relations.Select(x => new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["a"] = x.A, ["b"] = x.B, ["rulerA"] = x.RulerA, ["rulerB"] = x.RulerB, ["rulerRelation"] = x.RulerRelation, ["stance"] = x.Stance }).ToList(),
                ["affiliations"] = r.Affiliations.Select(x => new SortedDictionary<string, object>(StringComparer.Ordinal) { ["hero"] = x.Hero, ["clan"] = x.Clan, ["kingdom"] = x.Kingdom, ["note"] = x.Note }).ToList(),
                ["cultures"] = r.Cultures.Select(c => new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["uid"] = c.Uid, ["civilization"] = c.Civilization, ["civilizationName"] = c.CivilizationName, ["presentation"] = c.Presentation, ["template"] = c.Template, ["kingdoms"] = c.Kingdoms }).ToList(),
                ["dissolved"] = r.Dissolved.Select(d => new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["kingdom"] = d.Kingdom, ["uid"] = d.Uid, ["reason"] = d.Reason, ["lords"] = d.Lords, ["castles"] = d.Castles, ["clans"] = d.Clans }).ToList()
            };
            return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(o);
        }

        private static IEnumerable<string> AssignedIds(IDictionary<string, object> idMap)
        {
            foreach (var key in new[] { "entries", "ids", "records" })
                foreach (var e in List(idMap, key).OfType<IDictionary<string, object>>())
                    if (Text(e, "id") != null) yield return Text(e, "id");
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
