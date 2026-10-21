// Population pass tests (MMR-279): population_tests POLICIES_DIR
// The shared name cleaner (Age tags, naming patterns, lexicon-suffix dedupe without numerals), the W rule and the age mapping, and
// the population pass on a synthetic world: the kingdom merge (civilization and land first, then presentation and proximity, the
// split above the maximum), houses with 1..4 fiefs of their kingdom, castellans with their castle's house, an adult of each sex and
// vanilla lord cultures, companies and outlaw clans with their sizes and the war guard, wanderers W, the census with HeroFactory's
// caps and 0 unassigned, starting wars, and a byte-identical re-run.
#include <algorithm>
#include <filesystem>
#include <functional>
#include <iostream>
#include <map>
#include <set>
#include <string>
#include "worldbridge/campaign_compiler.hpp"
#include "worldbridge/canonical_json.hpp"
#include "worldbridge/name_cleaner.hpp"
#include "worldbridge/policy_map2.hpp"
#include "worldbridge/population.hpp"

namespace fs = std::filesystem;
using namespace worldbridge;

namespace {
int checks = 0, failures = 0;
fs::path policies;

void check(bool ok, const std::string& what) {
    ++checks;
    if (ok) std::cout << "ok    " << what << "\n";
    else { ++failures; std::cout << "FAIL  " << what << "\n"; }
}

void cleaner() {
    check(names::strip_age_tag("Selweg (Age 1)") == "Selweg" && names::strip_age_tag("Sits at Dunmar (age 2)mark at  Dunmar (Age 2).") == "Sits at Dunmarmark at Dunmar." &&
          names::strip_age_tag("the Order of Shtrandbrig (Age 2)") == "the Order of Shtrandbrig" && names::strip_age_tag("Plain (Agent 2)") == "Plain (Agent 2)",
          "the Age tag goes wherever it stands, in any case; other parentheses stay");
    check(names::has_age_tag("Korbrig (Age 2)") && !names::has_age_tag("Korbrig"), "has_age_tag finds the tag");
    check(names::has_numeral("Selweg Fields 2") && names::has_numeral("House IV") && names::has_numeral("the 2nd Watch") && !names::has_numeral("House Torbarg") &&
          !names::has_numeral("Crew of the Vela"), "numerals: digits in a word or a roman ordinal word");
    check(names::house_name("given_plus_house", "Torbarg") == "House Torbarg" && names::house_name("given_plus_clan", "Torbarg") == "Clan Torbarg" &&
          names::house_name("given_plus_father", "Torbarg") == "Line of Torbarg" && names::house_name("given_plus_workshop", "Torbarg") == "Workshop of Torbarg" &&
          names::house_name("given_plus_cohort", "Torbarg") == "Torbarg Cohort" && names::house_name("given_plus_vessel", "Torbarg (Age 1)") == "Crew of the Torbarg" &&
          names::house_name("", "Torbarg") == "House Torbarg", "house names follow each FWGC++ naming pattern, without the Age tag");
    check(names::person_name("given_plus_father", "Hilde", "Torbarg", "Arn", true) == "Hilde Arnsdottir" && names::person_name("given_plus_father", "Hal", "Torbarg", "Arn", false) == "Hal Arnsson" &&
          names::person_name("given_plus_house", "Hal", "Torbarg", "", false) == "Hal Torbarg" && names::person_name("given_plus_vessel", "Hal", "Vela", "", false) == "Hal of the Vela",
          "person names follow the pattern: patronymic, family root, vessel");
    check(names::compound({"Kor", "brand"}) == "Korbrand", "compound roots make one capitalized word");
    names::Heritage h{"given_plus_house", {{"hill", "tor"}, {"stone", "kor"}}};
    names::Unique u(names::Policy{});
    const auto a = u.take("House Torbarg (Age 2)", h, 0), b = u.take("House Torbarg", h, 0), c = u.take("House Torbarg", h, 0), d = u.take("House Torbarg", h, 0);
    check(a == "House Torbarg" && b == "House Torbargtor" && c == "House Torbargkor" && d == "House Torbargtortor" && !names::has_numeral(d),
          "collisions take a lexicon suffix on the last word, then two, never a numeral: " + b + ", " + c + ", " + d);
    names::Unique v(names::Policy{});
    v.take("Alpha", h, 1);
    check(v.take("Alpha", h, 1) == "Alphakor", "the salt rotates the lexicon, so the suffix is a per-entity draw");
    names::Unique e(names::Policy{});
    e.take("Beta", names::Heritage{}, 0);
    check(e.take("Beta", names::Heritage{}, 0) == "Betadun", "an empty lexicon uses the neutral roots");
}

void rules() {
    check(population::wanderer_target(0.6, 45) == 28 && population::wanderer_target(0.6, 66) == 40 && population::wanderer_target(0.6, 57) == 35 &&
          population::wanderer_target(0.6, 5) == 3, "W is the smallest integer not below the float32 0.6 x towns (45 -> 28, 66 -> 40, 57 -> 35)");
    source::LifeSpan human; human.id = "human_desert"; human.adult = 15; human.max = 80;
    human.bands = {{"young", {15, 30}}, {"middle", {31, 45}}, {"old", {46, 80}}};
    source::LifeSpan elf; elf.id = "elf"; elf.adult = 111; elf.max = 1000;
    elf.bands = {{"young", {111, 388}}, {"middle", {389, 666}}, {"old", {667, 1000}}};
    check(population::bannerlord_age(&human, 15, "young", 18, 70) == 20 && population::bannerlord_age(&human, 30, "young", 18, 70) == 34 &&
          population::bannerlord_age(&human, 80, "old", 18, 70) == 70 && population::bannerlord_age(&elf, 355, "young", 18, 70) == 32 &&
          population::bannerlord_age(&elf, 500, "", 18, 70) == 41 && population::bannerlord_age(nullptr, 30, "young", 18, 70) == -1,
          "ages map the people's life stage onto Bannerlord years (young 20..34): an elf of 355 is a young adult of 32");
    check(population::stream_value("houses", 1, "mnbl_00000000", "x", 0) == population::stream_value("houses", 1, "mnbl_00000000", "x", 0) &&
          population::stream_value("houses", 1, "mnbl_00000000", "x", 0) != population::stream_value("houses", 1, "mnbl_00000000", "x", 1),
          "stream values are deterministic per draw");
}
// A synthetic world of 6 realms (9 towns, 4 castles): two same-civilization desert realms that border on land, a dwarf realm and a
// frosthold-dwarf island realm (both presented as sturgia), two vlandia realms; castellans, a pretender, a council magistrate, remnant
// champions, domain holders, a magister, a warlord, two keepers at one town (HeroFactory's Preacher cap is 1), a deserter band and a camp.
struct World { source::SourceWorld w; std::map<std::string, std::pair<double, double>> at; std::map<std::string, int> group; };
World synthetic() {
    World x;
    auto& w = x.w;
    w.metadata.layout_seed = "279"; w.metadata.schema = "fwg.world"; w.metadata.grid_side = 101;
    w.metadata.founding_target_cities = 9; w.metadata.founding_placed_cities = 9; w.metadata.fortress_count = 10; w.metadata.settlement_count = 9; w.metadata.hamlets_per_core = 3;
    struct T { const char* uid; const char* civ; const char* culture; double px, py; int group; };
    const std::vector<T> towns{{"t1", "human_desert", "cu-desert", 0, 0, 0}, {"t2", "human_desert", "cu-desert", 10, 0, 0}, {"t3", "human_desert", "cu-desert2", 20, 0, 0},
                               {"t4", "dwarf", "cu-dwarf", 40, 0, 0}, {"t5", "dwarf", "cu-dwarf", 50, 0, 0}, {"t6", "frosthold_dwarf", "cu-frost", 60, 30, 1},
                               {"t7", "human_heartland", "cu-heart", 80, 0, 0}, {"t8", "human_heartland", "cu-heart", 90, 0, 0}, {"t9", "hill_dwarf", "cu-hill", 70, 0, 0}};
    std::int64_t site = 0;
    for (const auto& t : towns) {
        source::City c; c.site_id = site; c.uid = t.uid; c.name = std::string("Town ") + t.uid + (site == 2 ? " (Age 2)" : ""); c.kind = "city"; c.civilization_id = t.civ;
        c.culture_id = t.culture; c.city_class = site == 0 ? "capital" : "small"; c.population_estimate = 1000 + 100 * site; c.x = static_cast<std::int64_t>(t.px); c.z = static_cast<std::int64_t>(t.py);
        w.cities.push_back(c);
        source::Hamlet h; h.uid = std::string("ham-") + t.uid; h.core_id = site; h.kind = "hamlet"; h.role = "farming"; w.hamlets.push_back(h);
        x.at[t.uid] = {t.px, t.py}; x.group[t.uid] = t.group;
        ++site;
    }
    w.cultures = {{"cu-desert", "human_desert", {0, 1}}, {"cu-desert2", "human_desert", {2}}, {"cu-dwarf", "dwarf", {3, 4}}, {"cu-frost", "frosthold_dwarf", {5}},
                  {"cu-heart", "human_heartland", {6, 7}}, {"cu-hill", "hill_dwarf", {8}}};
    auto realm = [](const char* uid, const char* civ, const char* culture, std::vector<std::string> cities, const char* sovereign) {
        source::Realm r; r.uid = uid; r.name = std::string("Realm ") + uid; r.english_name = r.name; r.civilization_id = civ; r.civilization_name = civ; r.parent_race_id = "human";
        r.culture_id = culture; r.capital_uid = cities.front(); r.city_uids = cities; if (sovereign) r.sovereign_uid = std::string(sovereign);
        return r;
    };
    w.realms = {realm("r1", "human_desert", "cu-desert", {"t1", "t2"}, "sov-1"), realm("r2", "human_desert", "cu-desert2", {"t3"}, nullptr),
                realm("r3", "dwarf", "cu-dwarf", {"t4", "t5"}, "sov-3"), realm("r4", "frosthold_dwarf", "cu-frost", {"t6"}, "sov-4"),
                realm("r5", "human_heartland", "cu-heart", {"t7", "t8"}, "sov-5"), realm("r6", "hill_dwarf", "cu-hill", {"t9"}, nullptr)};
    auto fort = [&](const char* uid, const char* core, const char* culture, double px, double py, int group) {
        source::Castle c; c.uid = uid; c.kind = "fortress"; c.core_uid = core; c.garrisoned = true; c.culture_id = culture; w.castles.push_back(c);
        x.at[uid] = {px, py}; x.group[uid] = group;
    };
    fort("f1", "t1", "cu-desert", 2, 4, 0); fort("f2", "t4", "cu-dwarf", 42, 4, 0); fort("f3", "t5", "cu-dwarf", 52, 4, 0); fort("f4", "t7", "cu-heart", 82, 4, 0);
    auto person = [&](const std::string& uid, const char* role, const char* civ, const char* realm_uid, const char* home, bool female, int age) {
        source::Person p; p.uid = uid; p.role = role; p.status = "living"; p.name = "N";
        for (char ch : uid) if (ch != '-') p.name += ch >= '0' && ch <= '9' ? static_cast<char>('a' + (ch - '0')) : ch;
        p.civilization_id = civ; p.race_id = "human"; p.epithet = "the Test";
        if (realm_uid) p.realm_uid = std::string(realm_uid);
        if (home) { p.home_kind = "city"; p.home_uid = home; p.presence_kind = "city"; p.presence_uid = home; }
        w.people.push_back(p);
        source::Npc n; n.uid = "npc-" + uid; n.name = p.name; n.hero_uid = uid; n.female = female; n.known_sex = true; n.age_years = age; n.age_band = age < 31 ? "young" : age < 46 ? "middle" : "old";
        n.civilization_id = civ; w.npc_links.push_back(n);
        return w.people.size() - 1;
    };
    person("sov-1", "sovereign", "human_desert", "r1", "t1", false, 40);
    person("sov-3", "sovereign", "dwarf", "r3", "t4", true, 50);
    person("sov-4", "sovereign", "frosthold_dwarf", "r4", "t6", false, 44);
    person("sov-5", "sovereign", "human_heartland", "r5", "t7", false, 60);
    auto cast = person("cast-1", "castellan", "human_desert", "r1", "t1", false, 35); w.people[cast].presence_kind = "fortress"; w.people[cast].presence_uid = "f1";
    auto cast2 = person("cast-2", "castellan", "dwarf", "r3", "t4", true, 38); w.people[cast2].presence_kind = "fortress"; w.people[cast2].presence_uid = "f2";
    person("pret-1", "pretender", "dwarf", "r3", "t5", false, 30);
    auto mag = person("council-t2-magistrate", "council", "human_desert", "r1", "t2", true, 45); w.people[mag].seat = "magistrate";
    auto chap = person("council-t2-chaplain", "council", "human_desert", "r1", "t2", false, 45); w.people[chap].seat = "chaplain";
    for (int i = 0; i < 4; ++i) {
        auto c = person("champ-" + std::to_string(i), "champion", i % 2 ? "dwarf" : "human_heartland", nullptr, i < 2 ? "t7" : "t4", i == 1, 30);
        w.people[c].fame = 10 - i;
        if (i < 2) { w.people[c].org_uid = "remnant-" + std::to_string(i); w.people[c].join_condition = "hired"; }
    }
    w.orgs = {{"remnant-0", "remnant", "the Order of Ash (Age 1)", "ruin", "ruin-0", {"champ-0"}}, {"remnant-1", "remnant", "the Order of Salt", "ruin", "ruin-1", {"champ-1"}}};
    for (int i = 0; i < 8; ++i) {
        auto d = person("dom-" + std::to_string(i), "domain_holder", i % 2 ? "human_desert" : "dwarf", nullptr, nullptr, i % 3 == 0, 40);
        w.people[d].presence_kind = "ruin"; w.people[d].presence_uid = "ruin-" + std::to_string(i); w.people[d].lost_name = "Ruin " + std::to_string(i);
    }
    person("mag-1", "magister", "human_heartland", nullptr, "t8", true, 50);
    person("war-1", "warlord", "human_heartland", "r5", "t8", false, 45);
    person("keep-1", "keeper", "human_desert", "r1", "t1", false, 50);
    person("keep-2", "keeper", "human_desert", "r1", "t1", true, 52);
    auto reeve = person("reeve-1", "reeve", "dwarf", "r3", "t4", false, 40); w.people[reeve].presence_kind = "hamlet"; w.people[reeve].presence_uid = "ham-t4";
    for (const char* civ : {"human_desert", "dwarf", "frosthold_dwarf", "human_heartland", "hill_dwarf"})
        for (int i = 0; i < 12; ++i) {
            source::Npc r; r.uid = std::string("res-") + civ + "-" + std::to_string(i); r.name = std::string(1, static_cast<char>('A' + i)) + civ[0] + civ[1] + (i % 2 ? "ra" : "ven");
            r.civilization_id = civ; r.female = i % 2 == 1; r.known_sex = true; r.age_years = 25 + i; r.age_band = "young"; r.post = i == 0 ? "commander" : "soldier";
            r.site_uid = i == 0 ? (std::string(civ) == "dwarf" ? "f3" : "f1") : "t1";
            w.residents.push_back(r);
        }
    // MMR-281: more garrison residents, for the wanderer reserve (FWGC++ worlds have thousands).
    for (const char* civ : {"human_desert", "dwarf", "frosthold_dwarf", "human_heartland", "hill_dwarf"})
        for (int i = 0; i < 60; ++i) {
            source::Npc r; r.uid = std::string("rex-") + civ + "-" + std::to_string(i);
            const std::string tag = std::string(civ) == "human_heartland" ? "Hea" : std::string(civ) == "hill_dwarf" ? "Hil" : std::string(civ) == "frosthold_dwarf" ? "Fro" : std::string(civ) == "dwarf" ? "Dwa" : "Des";
            r.name = tag + std::string(1, static_cast<char>('a' + i % 26)) + std::string(1, static_cast<char>('a' + i / 26)) + "n";
            r.civilization_id = civ; r.female = i % 3 == 1; r.known_sex = true; r.age_years = 30; r.age_band = "young"; r.post = "soldier"; r.site_uid = "t1";
            w.residents.push_back(r);
        }
    w.heritage ={{"human_desert", "given_plus_house", {{"hill", "tor"}, {"sand", "tam"}}}, {"dwarf", "given_plus_clan", {{"stone", "kor"}, {"deep", "graf"}}},
                  {"frosthold_dwarf", "given_plus_clan", {{"cold", "fros"}}}, {"human_heartland", "given_plus_father", {{"field", "fen"}, {"road", "veg"}}},
                  {"hill_dwarf", "given_plus_father", {{"hill", "tor"}}}};
    source::LifeSpan span; span.id = "human"; span.adult = 15; span.max = 80; span.bands = {{"young", {15, 30}}, {"middle", {31, 45}}, {"old", {46, 80}}};
    w.life_spans = {span};
    w.bands = {{"band-1", "deserters", 0, 41, 1, 30}};
    w.camps = {{"camp-1", "ruin-0", "the camp in the ruins of Ash", "human_heartland", "ruin_squat", 0, 85, 2}};
    return x;
}

const std::string kWorldSha = "279279279279279279279279279279279279279279279279279279279279279a";

void pass() {
    const auto content = campaign::load_content(policies / "campaign-content.json");
    const auto cultures = campaign::load_cultures(policies / "culture-presentations.json");
    auto pop = policy::load_population(policies / "population.json");
    // Targets scaled to 13 fiefs: K 3, H 7, companies 2, outlaw clans 2; W = ceil(0.6f x 9) = 6.
    pop.kingdoms_per_base = 30.7; pop.houses_per_base = 71.7; pop.companies_per_base = 20.5; pop.outlaws_per_base = 20.5;
    const auto world = synthetic();
    auto run = [&](const policy::Population& p) {
        auto c = campaign::compile(world.w, content, cultures, kWorldSha);
        population::Geometry g; g.width = 200;
        for (const auto& t : c.towns) { g.point[t.id] = {world.at.at(t.uid).first, world.at.at(t.uid).second}; g.group[t.id] = world.group.at(t.uid); }
        for (const auto& k : c.castles) { g.point[k.id] = {world.at.at(k.uid).first, world.at.at(k.uid).second}; g.group[k.id] = world.group.at(k.uid); }
        population::plan(c, world.w, g, p, content, cultures);
        return c;
    };
    const auto c = run(pop);
    std::map<std::string, const campaign::Hero*> heroes;
    for (const auto& h : c.heroes) heroes[h.id] = &h;
    std::map<std::string, const campaign::House*> houses;
    for (const auto& x : c.houses) houses[x.id] = &x;
    std::map<std::string, std::string> kingdom_of_realm;
    for (const auto& k : c.kingdoms) for (const auto& r : k.member_realms) kingdom_of_realm[r] = k.id;
    std::map<std::string, std::string> step_of;
    for (const auto& k : c.kingdoms) for (const auto& s : k.merge_steps) step_of[s.realm] = s.step + (s.crosses_water ? "+water" : "");
    check(c.kingdoms.size() == 3, "the realms merge to K = round(9 F / 133) scaled (3 kingdoms of 6 realms): " + std::to_string(c.kingdoms.size()));
    check(kingdom_of_realm["r1"] == kingdom_of_realm["r2"] && (step_of["r2"] == "civilizationLand" || step_of["r1"] == "civilizationLand"),
          "the two desert realms that border on land merge first, by civilization (" + step_of["r1"] + ", " + step_of["r2"] + ")");
    check(kingdom_of_realm["r3"] == kingdom_of_realm["r4"] && (step_of["r4"] == "presentationProximity+water" || step_of["r3"] == "presentationProximity+water"),
          "the island realm joins the dwarf realm of its presentation culture across water (" + step_of["r3"] + ", " + step_of["r4"] + ")");
    check(campaign::closure_problems(c).empty(), "the planned records are closed");
    bool owned = true, cap = true, sexes = true, sizes = true, cultures_ok = true;
    for (const auto& t : c.towns) owned = owned && houses.count(t.owner_clan_id) && houses.at(t.owner_clan_id)->kingdom_id == t.kingdom_id;
    for (const auto& k : c.castles) owned = owned && houses.count(k.owner_clan_id) && houses.at(k.owner_clan_id)->kingdom_id == k.kingdom_id;
    for (const auto& x : c.houses) {
        cap = cap && !x.fiefs.empty() && x.fiefs.size() <= 4 && std::count(x.fiefs.begin(), x.fiefs.end(), x.home_id) == 1;
        bool m = false, f = false;
        for (const auto& id : x.members) { const auto* h = heroes.at(id); if (h->age >= 18) (h->female ? f : m) = true; cultures_ok = cultures_ok && std::count(pop.lord_cultures.begin(), pop.lord_cultures.end(), h->presentation); }
        sexes = sexes && m && f;
        sizes = sizes && x.members.size() >= 3 && x.members.size() <= 10;
    }
    check(owned && cap, "every fief is owned by a house of its kingdom, each house holds 1..4 fiefs and its home is one of them");
    check(sexes && sizes && cultures_ok, "every house has 3..10 members, an adult of each sex, and vanilla lord cultures");
    std::string f1_owner, cast1_clan;
    for (const auto& k : c.castles) if (k.uid == "f1") f1_owner = k.owner_clan_id;
    for (const auto& h : c.heroes) if (h.uid == "cast-1") cast1_clan = h.clan_id;
    check(!f1_owner.empty() && f1_owner == cast1_clan, "a castellan heads or belongs to the house owning its castle");
    bool kin = true; int minors = 0, dead = 0;
    for (const auto& h : c.heroes) {
        if (!h.alive) { ++dead; kin = kin && h.bucket == "house"; continue; }
        if (h.age < 18) { ++minors; kin = kin && !h.father_id.empty() && !h.mother_id.empty(); }
        if (!h.spouse_id.empty()) kin = kin && heroes.at(h.spouse_id)->spouse_id == h.id && heroes.at(h.spouse_id)->female != h.female;
    }
    check(kin && dead > 0, "kin links hold: spouses on both heroes and of the other sex, minors with both parents, siblings' dead parents (" + std::to_string(dead) + ", " + std::to_string(minors) + " minors)");
    bool companies = c.companies.size() == 2, outlaws = c.outlaws.size() == 2;
    for (const auto& k : c.companies) companies = companies && k.members.size() >= 6 && k.members.size() <= 10 && k.kind == "mercenary" && k.character_templates.size() == 4;
    for (const auto& k : c.outlaws) {
        outlaws = outlaws && k.members.size() >= 4 && k.members.size() <= 6 && k.wars.size() <= 2;
        for (const auto& id : k.wars) for (const auto& kk : c.kingdoms) if (kk.id == id) outlaws = outlaws && kk.presentation == k.presentation;
    }
    std::string captain;
    for (const auto& k : c.companies) if (k.source == "remnant:remnant-0") captain = heroes.at(k.leader_id)->uid + " " + k.name;
    check(companies && captain == "champ-0 Order of Ash", "2 companies of 6..10, the first captained by its remnant's hired champion and named after the order without the tag (" + captain + ")");
    check(outlaws, "2 outlaw clans of 4..6, at war only with kingdoms of their culture, within the war guard");
    check(c.wanderers.size() == 6 && c.census && c.census->wanderers == 6, "wanderers equal W = ceil(0.6f x 9) = 6");
    // MMR-281: the runtime wanderers' names: reserve entries per presentation culture of the towns, names no hero carries.
    std::set<std::string> presentations, hero_names, reserve_names;
    for (const auto& t : c.towns) presentations.insert(t.presentation);
    for (const auto& h : c.heroes) hero_names.insert(h.name);
    std::map<std::string, int> per_culture; bool reserve_ok = true; int women = 0;
    for (const auto& r : c.wanderer_reserve) {
        ++per_culture[r.presentation]; women += r.female;
        reserve_ok = reserve_ok && !r.name.empty() && reserve_names.insert(r.name).second && !hero_names.count(r.name) && !names::has_age_tag(r.name) && !names::has_numeral(r.name) &&
                     r.epithet == "the Test" && presentations.count(r.presentation);
    }
    int short_notes = 0;
    for (const auto& note : c.census->notes) short_notes += note.find("the wanderer reserve of") == 0;
    int full = 0;
    for (const auto& p : presentations) { reserve_ok = reserve_ok && per_culture[p] >= 1 && per_culture[p] <= pop.wanderer_reserve; full += per_culture[p] == pop.wanderer_reserve; }
    check(reserve_ok && women > 0 && women < static_cast<int>(c.wanderer_reserve.size()) && per_culture.size() == presentations.size() &&
          short_notes == static_cast<int>(presentations.size()) - full,
          "the wanderer reserve holds up to " + std::to_string(pop.wanderer_reserve) + " FWGC++ names per town culture (" + std::to_string(c.wanderer_reserve.size()) + ", " + std::to_string(women) +
          " women; a culture whose names run out is noted), unique, carried by no hero, without Age tags or numerals, each with the world's archetypal epithet");
    auto few = pop; few.wanderer_reserve = 2;
    const auto small = run(few);
    std::map<std::string, int> small_per;
    for (const auto& r : small.wanderer_reserve) ++small_per[r.presentation];
    bool two = small_per.size() == presentations.size();
    for (const auto& [p, n] : small_per) two = two && n == 2;
    check(two, "with reserve 2 every town culture gets exactly 2 names");
    check(c.census && c.census->unassigned == 0 && c.census->notables_bounded == 1, "every living FWGC++ hero is in one bucket; the second keeper at one town (Preacher cap 1) overflows to a house");
    std::string keep2;
    for (const auto& h : c.heroes) if (h.uid == "keep-2" || h.uid == "keep-1") keep2 += h.bucket + " ";
    check(keep2 == "notable house " || keep2 == "house notable ", "the capped-out keeper is a house lord now (" + keep2 + ")");
    std::set<std::string> names;
    bool clean = true;
    for (const auto& x : c.houses) { clean = clean && names.insert(x.name).second && !names::has_numeral(x.name) && !names::has_age_tag(x.name); }
    for (const auto* list : {&c.companies, &c.outlaws}) for (const auto& x : *list) clean = clean && names.insert(x.name).second && !names::has_numeral(x.name) && !names::has_age_tag(x.name);
    check(clean, "house, company and outlaw clan names are unique, without numerals or Age tags");
    bool wars = !c.wars.empty();
    std::map<std::string, int> per;
    for (const auto& x : c.wars) { ++per[x.a]; ++per[x.b]; }
    for (const auto& k : c.kingdoms) wars = wars && per[k.id] >= 1 && per[k.id] <= pop.wars_max_per_kingdom;
    check(wars, "every kingdom starts in one or two wars with a neighbour");
    const auto again = run(pop);
    check(campaign::records_document(again, content, cultures) == campaign::records_document(c, content, cultures) &&
          campaign::id_map_document(again) == campaign::id_map_document(c), "a re-run is byte-identical");
    // The split: one kingdom wanted (K 1) but at most 6 fiefs each: the merged kingdom splits above the maximum.
    auto tight = pop; tight.kingdoms_per_base = 1; tight.kingdom_max_fiefs = 6;
    const auto split = run(tight);
    bool fits = true; int splits = 0;
    for (const auto& k : split.kingdoms) { fits = fits && k.towns.size() + k.castles.size() <= 6; for (const auto& s : k.merge_steps) splits += s.step == "splitAboveMax"; }
    check(split.kingdoms.size() >= 3 && fits && splits > 0, "a kingdom above maxFiefs splits until each part fits (" + std::to_string(split.kingdoms.size()) + " kingdoms)");
}
}  // namespace

int main(int argc, char** argv) {
    if (argc != 2) { std::cerr << "usage: population_tests POLICIES_DIR\n"; return 2; }
    policies = argv[1];
    try {
        cleaner();
        rules();
        pass();
    } catch (const std::exception& e) { check(false, std::string("unexpected exception: ") + e.what()); }
    std::cout << checks << " checks, " << failures << " failed\n";
    return failures == 0 ? 0 : 1;
}
