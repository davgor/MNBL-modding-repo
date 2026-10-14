// Campaign semantic compiler tests (MMR-27): campaign_semantics_tests CONTENT_POLICY CULTURE_POLICY WORK_DIR
// A synthetic source world exercises every policy branch: stable ids, realm/culture/ownership joins, nullable
// sovereigns, realm-less lords and notables, village and town notables, legends, missing names and name collisions,
// all four harbour variants, garrisoned and ungarrisoned fortresses, exclusions after placement, producer limits,
// minima, every reference check and every refusal.
#include <filesystem>
#include <fstream>
#include <functional>
#include <iostream>
#include <regex>
#include <set>
#include <sstream>
#include <string>
#include "worldbridge/campaign_compiler.hpp"

namespace fs = std::filesystem;
using namespace worldbridge;
using campaign::Compiled;

namespace {
int checks = 0, failures = 0;
fs::path content_file, culture_file, work;
const std::string kWorldSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

void check(bool ok, const std::string& what) {
    ++checks;
    if (ok) std::cout << "ok    " << what << "\n";
    else { ++failures; std::cout << "FAIL  " << what << "\n"; }
}
void refused(const std::string& what, const std::string& code, const std::function<void()>& body) {
    try { body(); check(false, what + ": accepted, expected " + code); }
    catch (const json::Error& e) { check(e.code == code, what + " -> " + e.code + (e.code == code ? "" : " (wanted " + code + "): " + e.message)); }
    catch (const std::exception& e) { check(false, what + ": unexpected " + std::string(e.what())); }
}
std::string slurp(const fs::path& p) { std::ifstream in(p, std::ios::binary); std::ostringstream s; s << in.rdbuf(); return s.str(); }
void spit(const fs::path& p, const std::string& text) { std::ofstream out(p, std::ios::binary | std::ios::trunc); out << text; }

source::SourceWorld world() {
    source::SourceWorld w;
    w.metadata.layout_seed = "42"; w.metadata.schema = "fwg.world"; w.metadata.grid_side = 33;
    w.metadata.founding_target_cities = 6; w.metadata.founding_placed_cities = 3;
    w.metadata.fortress_count = 10; w.metadata.settlement_count = 24; w.metadata.hamlets_per_core = 3;
    auto city = [](std::int64_t site, const char* uid, const char* name, const char* civ, const char* culture) {
        source::City c; c.site_id = site; c.uid = uid; c.name = name; c.kind = "city"; c.civilization_id = civ; c.culture_id = culture; c.city_class = "small"; c.population_estimate = 1000 + site;
        return c;
    };
    w.cities = {city(0, "city-a", "Alpha", "human_maritime", "cul-a"), city(1, "city-b", "Beta", "human_maritime", "cul-a"), city(2, "city-c", "Alpha", "dwarf", "cul-c")};
    w.cultures = {{"cul-a", "human_maritime", {0, 1}}, {"cul-c", "dwarf", {2}}};
    source::Realm a; a.uid = "realm-a"; a.name = "Aster"; a.english_name = "Realm of Aster"; a.civilization_id = "human_maritime"; a.civilization_name = "Maritime humans";
    a.parent_race_id = "human"; a.culture_id = "cul-a"; a.capital_uid = "city-a"; a.city_uids = {"city-a", "city-b"}; a.sovereign_uid = "hero-sov-a";
    source::Realm c; c.uid = "realm-c"; c.name = "Carn"; c.english_name = "Realm of Carn"; c.civilization_id = "dwarf"; c.civilization_name = "Dwarves";
    c.parent_race_id = "dwarf"; c.culture_id = "cul-c"; c.capital_uid = "city-c"; c.city_uids = {"city-c"};
    w.realms = {a, c};
    auto person = [](const char* uid, const char* role, const char* status, const char* realm, const char* home) {
        source::Person p; p.uid = uid; p.role = role; p.status = status; p.name = uid; p.civilization_id = "human_maritime"; p.race_id = "human";
        if (realm) p.realm_uid = std::string(realm);
        if (home) { p.home_kind = "city"; p.home_uid = home; }
        return p;
    };
    w.people = {person("hero-sov-a", "sovereign", "living", "realm-a", "city-a"), person("hero-dom", "domain_holder", "living", nullptr, nullptr),
                person("hero-council", "council", "living", "realm-a", "city-b"), person("hero-legend", "warlord", "legend", nullptr, nullptr),
                person("hero-champ", "champion", "living", nullptr, nullptr), person("hero-castellan", "castellan", "living", "realm-c", nullptr),
                person("hero-keeper", "keeper", "living", nullptr, nullptr), person("hero-heresiarch", "heresiarch", "living", nullptr, nullptr),
                person("hero-reeve", "reeve", "living", "realm-a", "city-a"), person("hero-reeve2", "reeve", "living", "realm-c", "city-c"),
                person("hero-harbour", "harbourmaster", "living", "realm-a", "city-a")};
    w.people[0].display_name = "Asterix the Bold";
    w.people[8].presence_kind = "hamlet"; w.people[8].presence_uid = "ham-1";
    w.people[10].presence_kind = "port"; w.people[10].presence_uid = "coastal-0-0";
    auto hamlet = [](const char* uid, std::int64_t core, const char* role, std::optional<std::int64_t> sea, double quality = 0) {
        source::Hamlet h; h.uid = uid; h.core_id = core; h.kind = "hamlet"; h.role = role; h.culture_id = "";
        if (sea) { source::Harbour b; b.sea_node = *sea; b.harbor_quality = quality; h.harbour = b; }
        return h;
    };
    w.hamlets = {hamlet("ham-1", 0, "farming", std::nullopt), hamlet("ham-2", 0, "harbor + fishing", 99, 0.8), hamlet("ham-3", 0, "farming", std::nullopt),
                 hamlet("ham-4", 2, "fishing", 99, 0.3), hamlet("ham-5", 1, "harbor", 98, 0.5), hamlet("ham-6", 2, "landing", 97, 0.2)};
    source::Castle f1; f1.uid = "fort-1"; f1.kind = "fortress"; f1.core_uid = "city-b"; f1.garrisoned = true; f1.culture_id = "cul-a";
    source::Castle f2; f2.uid = "fort-2"; f2.kind = "fortress"; f2.core_uid = ""; f2.garrisoned = false; f2.culture_id = "cul-c";
    w.castles = {f1, f2};
    return w;
}

template <class T> const T* by_uid(const std::vector<T>& list, const std::string& uid) {
    for (const auto& x : list) if (x.uid == uid) return &x;
    return nullptr;
}
bool has(const std::string& text, const std::string& part) { return text.find(part) != std::string::npos; }

void compile_tests() {
    const auto content = campaign::load_content(content_file);
    const auto cultures = campaign::load_cultures(culture_file);
    const Compiled c = campaign::compile(world(), content, cultures, kWorldSha);
    // c1: deterministic, unique, bounded, traceable.
    const Compiled again = campaign::compile(world(), content, cultures, kWorldSha);
    bool same = c.id_map.size() == again.id_map.size();
    for (std::size_t i = 0; same && i < c.id_map.size(); ++i) same = c.id_map[i].id == again.id_map[i].id && c.id_map[i].uid == again.id_map[i].uid;
    check(same, "the id map is deterministic");
    const std::regex shape("^mnbl_[0-9a-f]{8}_[tvcklh]_[0-9a-f]{10}$");
    std::set<std::string> ids;
    bool well = true;
    for (const auto& e : c.id_map) { well = well && std::regex_match(e.id, shape) && e.id.size() <= 26; ids.insert(e.id); }
    check(well && ids.size() == c.id_map.size(), "ids are unique, [a-z0-9_], at most 26 characters");
    auto seed43 = world().metadata; seed43.layout_seed = "43";
    const auto identity = source::write_identity(world().metadata);
    check(c.source_identity == identity && c.world_sha256 == kWorldSha && c.prefix == ids::world_prefix(identity, kWorldSha) &&
          c.prefix != ids::world_prefix(identity, std::string(64, 'f')) && c.prefix != ids::world_prefix(source::write_identity(seed43), kWorldSha),
          "the world prefix follows the source identity and the world.json hash: another seed or another world.json moves it");
    bool traced = true;
    auto traceable = [&](const std::string& id, const std::string& kind, const std::string& uid) {
        bool found = false; for (const auto& e : c.id_map) found = found || (e.id == id && e.kind == kind && e.uid == uid); traced = traced && found;
    };
    for (const auto& t : c.towns) traceable(t.id, "town", t.uid);
    for (const auto& v : c.villages) traceable(v.id, "village", v.uid);
    for (const auto& k : c.castles) traceable(k.id, "castle", k.uid);
    for (const auto& k : c.kingdoms) traceable(k.id, "kingdom", k.uid);
    for (const auto& k : c.clans) traceable(k.id, "clan", k.uid);
    for (const auto& h : c.heroes) traceable(h.id, "hero", h.uid);
    check(traced && c.id_map.size() == 3 + 6 + 2 + 2 + 2 + 11, "every record id is in the id map with its kind and source uid (26 entries)");
    check(ids::Registry("mnbl_00000000").assign("town", "city-a") == c.towns[0].id.substr(0, 5) + "00000000" + c.towns[0].id.substr(13),
          "ids depend on kind and uid only (plus the world prefix), never on names");
    // c2: closed joins.
    check(campaign::closure_problems(c).empty(), "ownership, capital, binding and membership joins are closed");
    const auto* ka = by_uid(c.kingdoms, "realm-a");
    const auto* kc = by_uid(c.kingdoms, "realm-c");
    const auto* sov = by_uid(c.heroes, "hero-sov-a");
    check(ka && sov && ka->ruler_id == sov->id && sov->ruler && sov->name == "Asterix the Bold" && ka->presentation == "empire" && ka->civilization_name == "Maritime humans" &&
          sov->presentation == "empire" && sov->race_id == "human", "realm A: kingdom ruled by its sovereign, presented as empire; the hero keeps its race and presentation");
    const auto* regent = by_uid(c.heroes, "regent:realm-c");
    check(kc && regent && kc->ruler_id == regent->id && regent->provenance.find("synthesized") == 0 && regent->name == "Regent of Carn" && kc->presentation == "sturgia" &&
          kc->parent_race_id == "dwarf" && regent->race_id == "dwarf" && regent->presentation == "sturgia", "nullable sovereign: realm C gets a synthesized regent of its parent race");
    const auto* castellan = by_uid(c.heroes, "hero-castellan");
    const auto* clan_c = by_uid(c.clans, "clan:realm-c");
    check(castellan && clan_c && castellan->clan_id == clan_c->id && clan_c->leader_id == regent->id && clan_c->members.size() == 2 && clan_c->name == "House of Carn",
          "lords join their realm's ruling clan; the regent leads it");
    const auto* dom = by_uid(c.heroes, "hero-dom");
    check(dom && dom->occupation == "Wanderer" && dom->affiliation == "none" && dom->clan_id.empty() && has(dom->provenance, "withoutRealm"),
          "a realm-less domain holder becomes a clanless Wanderer");
    const auto* keeper = by_uid(c.heroes, "hero-keeper");
    const auto* heresiarch = by_uid(c.heroes, "hero-heresiarch");
    check(keeper && heresiarch && keeper->occupation == "Wanderer" && heresiarch->occupation == "Wanderer" && keeper->affiliation == "none" && heresiarch->settlement_id.empty() &&
          has(keeper->provenance, "withoutRealm") && has(heresiarch->provenance, "withoutRealm"), "a keeper and a heresiarch with no surviving home city or realm become Wanderers");
    const auto* council = by_uid(c.heroes, "hero-council");
    const auto* town_a = by_uid(c.towns, "city-a");
    const auto* town_b = by_uid(c.towns, "city-b");
    const auto* town_c = by_uid(c.towns, "city-c");
    check(council && town_b && council->occupation == "Merchant" && council->settlement_id == town_b->id && council->settlement_kind == "town", "a council notable attaches to its home town");
    const auto* harbourmaster = by_uid(c.heroes, "hero-harbour");
    check(harbourmaster && harbourmaster->occupation == "Merchant" && harbourmaster->settlement_id == town_a->id && harbourmaster->kingdom_id == ka->id,
          "a harbourmaster is a Merchant of its home town (the installed game has no ShipWright heroes)");
    const auto* reeve = by_uid(c.heroes, "hero-reeve");
    const auto* reeve2 = by_uid(c.heroes, "hero-reeve2");
    const auto* ham1 = by_uid(c.villages, "ham-1");
    check(reeve && ham1 && reeve->occupation == "Headman" && reeve->settlement_kind == "village" && reeve->settlement_id == ham1->id && reeve->provenance == "source",
          "a reeve is the Headman of the hamlet it is present at");
    check(reeve2 && town_c && reeve2->occupation == "Headman" && reeve2->settlement_kind == "town" && reeve2->settlement_id == town_c->id && has(reeve2->provenance, "no hamlet presence"),
          "a reeve without a hamlet presence falls back to its home town, with provenance");
    check(c.not_instantiated.size() == 1 && c.not_instantiated[0] == "hero-legend" && !by_uid(c.heroes, "hero-legend"), "legends are recorded, not instantiated");
    check(town_a && town_a->capital && town_a->owner_clan_id == ka->ruling_clan_id && town_a->villages.size() == 3 && town_a->port && town_a->culture_uid == "cul-a" &&
          town_a->culture_provenance.find("source") == 0, "town A: capital, owned by the ruling clan, three villages, a port, its core's culture");
    check(town_c && town_c->name == "Alpha 2" && has(town_c->name_provenance, "disambiguated"), "a repeated source name is disambiguated with the ordinal");
    std::vector<std::string> names;
    for (const auto& v : c.villages) names.push_back(v.name);
    check(names == std::vector<std::string>{"Alpha Fields", "Alpha Harbour", "Alpha Fields 2", "Alpha 2 Fishery", "Beta Harbour", "Alpha 2 Landing"},
          "nameless hamlets get synthesized, unique village names in UID order");
    const auto* combined = by_uid(c.villages, "ham-2");
    const auto* fishing = by_uid(c.villages, "ham-4");
    const auto* harbor = by_uid(c.villages, "ham-5");
    const auto* landing = by_uid(c.villages, "ham-6");
    check(combined && combined->harbour && combined->production == "fishing" && combined->sea_node == 99 && fishing && !fishing->harbour && fishing->sea_node == 99 &&
          harbor && harbor->harbour && harbor->production == "harbor" && landing && landing->harbour && landing->production == "landing",
          "harbour variants: 'harbor + fishing', 'harbor' and 'landing' are harbours; 'fishing' keeps its sea node without a port");
    const auto port_of = [&](const std::string& town_id) -> const campaign::Port* { for (const auto& p : c.ports) if (p.town_id == town_id) return &p; return nullptr; };
    const auto* port_a = port_of(town_a->id);
    const auto* port_c = port_of(town_c->id);
    check(c.ports.size() == 3 && port_a && port_a->best_quality == 0.8 && port_a->sea_nodes == std::vector<std::int64_t>{99} && port_of(town_b->id) &&
          port_of(town_b->id)->best_quality == 0.5 && port_c && port_c->village_ids == std::vector<std::string>{landing->id} && port_c->best_quality == 0.2,
          "each town's port collects its harbour villages only");
    const auto* f1 = by_uid(c.castles, "fort-1");
    const auto* f2 = by_uid(c.castles, "fort-2");
    check(f1 && f1->owner_clan_id == ka->ruling_clan_id && f1->garrison_town_id == town_b->id && f1->name == "Beta Watch", "a garrisoned fortress is owned through its garrison city");
    check(f2 && f2->owner_clan_id == clan_c->id && f2->garrison_town_id.empty() && f2->name == "Carn Hold" && has(f2->owner_provenance, "culture"),
          "an ungarrisoned fortress is owned by the realm of its culture");
    // Slots: decided here or owned downstream, never silent.
    bool typed = !town_a->slots.empty() && !combined->slots.empty() && !c.ports[0].slots.empty() && !sov->slots.empty();
    for (const auto& s : town_a->slots) typed = typed && (s.status == "pending" || s.status == "resolved") && !s.owner.empty();
    bool troops = false;
    for (const auto& s : ka->slots) troops = troops || (s.name == "troopTreeAndRecruitTemplates" && s.status == "pending" && s.owner == "MMR-32");
    check(typed && troops, "every record carries typed slots with an owner ticket; troop trees and recruit templates are pending for MMR-32");
    // c3: producer limits and minima.
    check(c.producer.at("sites") == 3 && c.producer.at("foundingPlacedCities") == 3 && c.producer.at("maxHamletsPerCore") == 3 && c.producer.at("maxLandHamletsPerCore") == 2 &&
          c.producer.at("hamletsPerCoreLimit") == 3, "producer counts recorded: 3 sites as placed, 3 hamlets (2 land) at the busiest core, within hamlets_per_core 3");
    bool rules = c.rules.size() == content.minima.size();
    for (const auto& r : c.rules) rules = rules && (r.status == "ok" ? r.actual >= r.minimum : r.status == "deferred" && r.rule == "playerAliases");
    check(rules, "every minimum holds; the player aliases are deferred to the registry (MMR-30)");
    check(c.towns.size() == 3 && c.villages.size() == 6 && c.castles.size() == 2, "settlement counts preserved: nothing added or dropped");
    // c4: towns without names get the policy name, with an ordinal when two collide.
    auto nameless = world(); nameless.cities[0].name = ""; nameless.cities[1].name = "";
    const Compiled n = campaign::compile(nameless, content, cultures, kWorldSha);
    const auto* na = by_uid(n.towns, "city-a");
    const auto* nb = by_uid(n.towns, "city-b");
    check(na && nb && na->name == "Aster Town" && nb->name == "Aster Town 2" && na->name_provenance.find("synthesized") == 0 && nb->name_provenance.find("synthesized") == 0,
          "nameless cities of one realm become 'Aster Town' and 'Aster Town 2', marked synthesized");
    std::set<std::string> civilizations;
    for (const auto& [civ, culture] : cultures.presentation) civilizations.insert(civ);
    check(civilizations == std::set<std::string>{"human_maritime", "human_desert", "human_cold", "human_large_island", "human_rainforest", "human_heartland",
                                                 "dwarf", "elf", "gnome", "tidekin", "hill_dwarf", "frosthold_dwarf"},
          "the culture policy covers exactly the generator's 12 civilizations (content_civilizations.cpp kEntityIds)");
    // Exclusions after placement keep closure, are recorded, and move a village's notables to its town; towns are preserved.
    Compiled e = c;
    campaign::exclude(e, combined->id, "unreachable by sea (test)");
    check(e.ports.size() == 2 && !by_uid(e.towns, "city-a")->port && campaign::closure_problems(e).empty() && e.exclusions.size() == 1 &&
          e.exclusions[0].kind == "village" && e.exclusions[0].uid == "ham-2" && e.exclusions[0].reason == "unreachable by sea (test)",
          "excluding the only harbour village drops the port, keeps closure and records the exclusion");
    campaign::exclude(e, ham1->id, "no site (test)");
    const auto* moved = by_uid(e.heroes, "hero-reeve");
    check(moved && moved->settlement_kind == "town" && moved->settlement_id == town_a->id && has(moved->provenance, "excluded") && campaign::closure_problems(e).empty(),
          "excluding a village moves its Headman to the village's town");
    campaign::exclude(e, f1->id, "no site (test)");
    check(by_uid(e.towns, "city-b")->castles.empty() && campaign::closure_problems(e).empty() && e.exclusions.size() == 3 && e.exclusions[2].kind == "castle",
          "excluding a castle keeps closure and is recorded");
    refused("excluding a town", "preserved_settlement", [&] { Compiled x = c; campaign::exclude(x, town_a->id, "test"); });
    refused("excluding an unknown id", "broken_join", [&] { Compiled x = c; campaign::exclude(x, "mnbl_nope", "test"); });
    refused("excluding without a reason", "missing_field", [&] { Compiled x = c; campaign::exclude(x, combined->id, ""); });
}

void escaping_tests() {
    check(campaign::xml_escape("A&B <C> \"D\" 'E'\x01\t") == "A&amp;B &lt;C&gt; &quot;D&quot; &apos;E&apos;", "XML attribute escaping, control characters (tab included) dropped");
    check(campaign::localized("mnbl_x", "Sel{weg} (Age 1)\t") == "{=mnbl_x}Sel(weg) (Age 1)", "localized form neutralizes TextObject braces and drops control characters");
    check(campaign::localized_xml("mnbl_x", "A&{b}<c>") == "{=mnbl_x}A&amp;(b)&lt;c&gt;", "the XML-ready localized name escapes the localized form");
}

void refusal_tests() {
    const auto content = campaign::load_content(content_file);
    const auto cultures = campaign::load_cultures(culture_file);
    auto with = [&](const std::function<void(source::SourceWorld&)>& f) { auto w = world(); f(w); return campaign::compile(w, content, cultures, kWorldSha); };
    refused("unknown hamlet role", "unmapped_category", [&] { with([](auto& w) { w.hamlets[0].role = "quarry"; }); });
    refused("unknown hero role", "unmapped_category", [&] { with([](auto& w) { w.people[1].role = "bard"; }); });
    refused("unknown hero status", "unmapped_category", [&] { with([](auto& w) { w.people[1].status = "ghost"; }); });
    refused("unknown settlement kind", "unmapped_category", [&] { with([](auto& w) { w.cities[1].kind = "ruin"; }); });
    refused("city civilization without a presentation", "unmapped_category", [&] { with([](auto& w) { w.cities[2].civilization_id = "glass"; w.cities[2].culture_id = ""; }); });
    refused("hero civilization without a presentation", "unmapped_category", [&] { with([](auto& w) { w.people[2].civilization_id = "glass"; }); });
    refused("city held by no realm", "faction_distribution", [&] { with([](auto& w) { w.realms[0].city_uids = {"city-a"}; }); });
    refused("city held by two realms", "faction_distribution", [&] { with([](auto& w) { w.realms[1].city_uids.push_back("city-b"); }); });
    refused("capital outside the realm", "broken_join", [&] { with([](auto& w) { w.realms[0].capital_uid = "city-c"; }); });
    refused("realm culture unknown", "broken_join", [&] { with([](auto& w) { w.realms[0].culture_id = "cul-x"; }); });
    refused("city culture unknown", "broken_join", [&] { with([](auto& w) { w.cities[0].culture_id = "cul-zzz"; }); });
    refused("city culture of another civilization", "broken_join", [&] { with([](auto& w) { w.cities[2].culture_id = "cul-a"; }); });
    refused("hamlet culture unknown", "broken_join", [&] { with([](auto& w) { w.hamlets[0].culture_id = "cul-zzz"; }); });
    refused("fortress culture unknown", "broken_join", [&] { with([](auto& w) { w.castles[0].culture_id = "cul-zzz"; }); });
    refused("duplicate culture id", "duplicate_id", [&] { with([](auto& w) { w.cultures.push_back({"cul-a", "human_maritime", {}}); }); });
    refused("notable whose home names an unknown city", "broken_join", [&] { with([](auto& w) { w.people[2].home_uid = "city-zzz"; }); });
    refused("hero whose home is not a city", "broken_join", [&] { with([](auto& w) { w.people[2].home_kind = "ruin"; }); });
    refused("village notable present at an unknown hamlet", "broken_join", [&] { with([](auto& w) { w.people[8].presence_uid = "ham-zzz"; }); });
    refused("sovereign of another realm", "sovereign_mismatch", [&] { with([](auto& w) { w.realms[1].sovereign_uid = std::string("hero-sov-a"); }); });
    refused("sovereign who is a legend", "sovereign_mismatch", [&] { with([](auto& w) { w.realms[1].sovereign_uid = std::string("hero-legend"); w.people[3].realm_uid = std::string("realm-c"); }); });
    refused("castellan without a realm", "broken_join", [&] { with([](auto& w) { w.people[5].realm_uid.reset(); }); });
    auto strict_roles = content;
    strict_roles.hero_roles["keeper"].without_realm.clear();
    refused("a notable with no home, presence, realm or withoutRealm rule", "broken_join", [&] { campaign::compile(world(), strict_roles, cultures, kWorldSha); });
    refused("hamlet with an unknown core", "broken_join", [&] { with([](auto& w) { w.hamlets[0].core_id = 77; }); });
    refused("harbour role without a sea node", "broken_join", [&] { with([](auto& w) { w.hamlets[1].harbour.reset(); }); });
    refused("ungarrisoned fortress matching no realm", "broken_join", [&] { with([](auto& w) { w.cultures.push_back({"cul-x", "dwarf", {}}); w.castles[1].culture_id = "cul-x"; }); });
    refused("ungarrisoned fortress without a culture", "broken_join", [&] { with([](auto& w) { w.castles[1].culture_id = ""; }); });
    refused("placed cities differ from the sites", "producer_mismatch", [&] { with([](auto& w) { w.metadata.founding_placed_cities = 4; }); });
    refused("more sites than the producer target", "producer_mismatch", [&] { with([](auto& w) { w.metadata.founding_target_cities = 2; }); });
    refused("more fortresses than the producer limit", "producer_mismatch", [&] { with([](auto& w) { w.metadata.fortress_count = 1; }); });
    refused("more land hamlets at a core than hamlets_per_core", "producer_mismatch", [&] { with([](auto& w) { w.metadata.hamlets_per_core = 1; }); });
    refused("a world without the founding report", "missing_field", [&] { with([](auto& w) { w.metadata.founding_placed_cities.reset(); }); });
    refused("a world without hamlets_per_core", "missing_field", [&] { with([](auto& w) { w.metadata.hamlets_per_core.reset(); }); });
    refused("a player-adopted hamlet", "unsupported_row", [&] { with([](auto& w) { w.hamlets[0].founded_by = "player"; }); });
    refused("a player-adopted fortress", "unsupported_row", [&] { with([](auto& w) { w.castles[0].founded_by = "player"; }); });
    refused("duplicate city uid", "duplicate_id", [&] { with([](auto& w) { w.cities[2].uid = "city-a"; }); });
    refused("a world without realms", "missing_field", [&] { with([](auto& w) { w.realms.clear(); }); });
    refused("a world prefix without a world.json hash", "wrong_type", [&] { ids::world_prefix("identity", "abc"); });
    refused("id collision is refused, never renamed (17 uids over 16 one-digit hashes)", "id_collision", [&] {
        ids::Registry r("mnbl_test", 1);
        for (int i = 0; i < 17; ++i) r.assign("town", "city-" + std::to_string(i));
    });
    refused("the same (kind, uid) twice", "duplicate_id", [&] { ids::Registry r("mnbl_test"); r.assign("town", "x"); r.assign("town", "x"); });
    auto strict = content;
    for (auto& m : strict.minima) if (m.rule == "towns") m.minimum = 5;
    refused("a world below a recorded minimum", "invalid_world", [&] { campaign::compile(world(), strict, cultures, kWorldSha); });
    // Policy file strictness.
    auto mutated = [&](const fs::path& file, const std::string& name, const std::string& from, const std::string& to, const std::string& code, bool culture) {
        const std::string text = slurp(file);
        const auto at = text.find(from);
        if (at == std::string::npos) { check(false, name + ": text not found"); return; }
        std::string copy = text; copy.replace(at, from.size(), to);
        const auto out = work / (name + ".json"); spit(out, copy);
        refused("policy " + name, code, [&] { if (culture) campaign::load_cultures(out); else campaign::load_content(out); });
    };
    mutated(content_file, "content_unknown_member", "\"version\": 1,", "\"version\": 1, \"extra\": 1,", "unknown_field", false);
    mutated(content_file, "content_bad_affiliation", "\"affiliation\": \"none\"", "\"affiliation\": \"guild\"", "unmapped_category", false);
    mutated(content_file, "content_occupation_without_rule", "\"occupation\": \"Wanderer\",\n      \"affiliation\": \"none\"", "\"occupation\": \"Rover\",\n      \"affiliation\": \"none\"", "unmapped_category", false);
    mutated(content_file, "content_settlement_kind_unknown", "\"settlement\": \"village\"", "\"settlement\": \"castle\"", "unmapped_category", false);
    mutated(content_file, "content_settlement_kind_missing", "\"settlement\": \"town\",", "", "unmapped_category", false);
    mutated(content_file, "content_unknown_rule", "\"rule\": \"towns\"", "\"rule\": \"bridges\"", "unmapped_category", false);
    mutated(content_file, "content_ordinal_without_n", "\"ordinal\": \"{name} {n}\"", "\"ordinal\": \"{name}\"", "invalid_template", false);
    mutated(content_file, "content_unknown_placeholder", "\"town\": \"{realm} Town\"", "\"town\": \"{kingdom} Town\"", "invalid_template", false);
    mutated(culture_file, "culture_unofficial", "\"culture\": \"aserai\"", "\"culture\": \"looters\"", "unmapped_category", true);
    mutated(culture_file, "culture_duplicate", "{\"civilization\": \"human_desert\"", "{\"civilization\": \"human_maritime\"", "duplicate_id", true);
}
}  // namespace

int main(int argc, char** argv) {
    if (argc != 4) { std::cerr << "usage: campaign_semantics_tests CONTENT_POLICY CULTURE_POLICY WORK_DIR\n"; return 2; }
    content_file = argv[1]; culture_file = argv[2]; work = argv[3];
    fs::create_directories(work);
    try { compile_tests(); escaping_tests(); refusal_tests(); }
    catch (const std::exception& e) { check(false, std::string("unexpected exception: ") + e.what()); }
    std::cout << checks - failures << "/" << checks << " checks passed\n";
    return failures == 0 ? 0 : 1;
}
