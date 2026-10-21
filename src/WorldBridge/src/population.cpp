#include "worldbridge/population.hpp"
#include <algorithm>
#include <cmath>
#include <functional>
#include <set>
#include <stdexcept>
#include <tuple>
#include "worldbridge/canonical_json.hpp"
#include "worldbridge/name_cleaner.hpp"
#include "worldbridge/stable_ids.hpp"
#include "worldbridge/travel_graph.hpp"

namespace worldbridge::population {
namespace {
using campaign::Hero;
using campaign::House;
using campaign::MinorClan;
using json::Error;
using projection::Point;

[[noreturn]] void refuse(const std::string& code, const std::string& message) { throw Error(code, message, json::Location{"", 0, 1, 1, "$"}); }

std::int64_t round_half_up(double v) { return static_cast<std::int64_t>(std::floor(v + 0.5)); }


class Pass {
public:
    Pass(campaign::Compiled& c, const source::SourceWorld& w, const Geometry& g, const policy::Population& p, const campaign::ContentPolicy& content,
         const campaign::CulturePolicy& cultures)
        : c(c), w(w), g(g), pop(p), content(content), cultures(cultures), reg(c.prefix, hex_of(c)), clan_names(content.cleaner), hero_names(content.cleaner) {
        for (const auto& e : c.id_map) reg.assign(e.kind, e.uid);
        for (const auto& h : w.heritage) heritage[h.civilization_id] = names::Heritage{h.naming_pattern, h.lexicon};
        for (const auto& p2 : w.people) person[p2.uid] = &p2;
        for (const auto& o : w.orgs) org[o.uid] = &o;
        for (const auto& n : w.npc_links) if (!link.count(n.hero_uid)) link[n.hero_uid] = &n;
        for (const auto& s : w.life_spans) if (!span.count(s.id)) span[s.id] = &s;
        for (const auto& r : w.residents) {
            pool[{r.civilization_id, r.female}].push_back(&r);
            if (r.post == "commander" && !commander.count(r.site_uid)) commander[r.site_uid] = &r;
            if (r.post == "magistrate" && !magistrate.count(r.site_uid)) magistrate[r.site_uid] = &r;
        }
        for (auto& [key, list] : pool) std::sort(list.begin(), list.end(), [](const source::Npc* a, const source::Npc* b) { return a->uid < b->uid; });
        for (const auto& t : c.towns) { town_of_uid[t.uid] = t.id; fief_kind[t.id] = "town"; }
        for (const auto& k : c.castles) { castle_of_uid[k.uid] = k.id; fief_kind[k.id] = "castle"; }
        for (const auto& cu : w.cultures) culture_civ[cu.id] = cu.civilization_id;
        for (const auto& r : w.castellan_rolls) roll[r.fortress_uid] = &r;
    }

    void run();

private:
    campaign::Compiled& c;
    const source::SourceWorld& w;
    const Geometry& g;
    const policy::Population& pop;
    const campaign::ContentPolicy& content;
    const campaign::CulturePolicy& cultures;
    ids::Registry reg;
    names::Unique clan_names, hero_names;
    std::map<std::string, names::Heritage> heritage;
    std::map<std::string, const source::Person*> person;
    std::map<std::string, const source::Org*> org;
    std::map<std::string, const source::Npc*> link, commander, magistrate;
    std::map<std::string, const source::LifeSpan*> span;
    std::map<std::pair<std::string, bool>, std::vector<const source::Npc*>> pool;
    std::map<std::pair<std::string, bool>, std::size_t> pool_next;
    std::map<std::string, std::string> town_of_uid, castle_of_uid, fief_kind, culture_civ;
    std::map<std::string, const source::CastellanRoll*> roll;
    campaign::Census census;
    int K = 1, H = 0, M = 0, O = 0, W = 0;

    static int hex_of(const campaign::Compiled& c) { return c.id_map.empty() ? 10 : static_cast<int>(c.id_map.front().id.size() - c.prefix.size() - 3); }

    // Streams (population.json streams).
    std::uint64_t value(const std::string& stream, const std::string& entity, int draw) const { return stream_value(stream, pop.streams.at(stream), c.prefix, entity, draw); }
    double unit(const std::string& stream, const std::string& entity, int draw) const { return static_cast<double>(value(stream, entity, draw) >> 11) / 9007199254740992.0; }
    int between(const std::string& stream, const std::string& entity, int draw, int lo, int hi) const {
        return hi <= lo ? lo : lo + static_cast<int>(value(stream, entity, draw) % static_cast<std::uint64_t>(hi - lo + 1));
    }

    // Lookups.
    Hero& hero(const std::string& id) { for (auto& h : c.heroes) if (h.id == id) return h; refuse("broken_join", "No hero " + id); }
    House* house_of_fief(const std::string& fief) { for (auto& x : c.houses) if (std::find(x.fiefs.begin(), x.fiefs.end(), fief) != x.fiefs.end()) return &x; return nullptr; }
    House& house(const std::string& id) { for (auto& x : c.houses) if (x.id == id) return x; refuse("broken_join", "No house " + id); }
    const source::Person* source_of(const Hero& h) const { const auto it = person.find(h.uid); return it == person.end() ? nullptr : it->second; }
    std::string presentation_of(const std::string& civ) const { const auto it = cultures.presentation.find(civ); return it == cultures.presentation.end() ? "" : it->second; }
    const names::Heritage& heritage_of(const std::string& civ) const { static const names::Heritage none; const auto it = heritage.find(civ); return it == heritage.end() ? none : it->second; }
    double distance(const std::string& a, const std::string& b) const {
        const auto pa = g.point.find(a), pb = g.point.find(b);
        if (pa == g.point.end() || pb == g.point.end()) return 1e18;
        double dx = std::abs(pa->second.x - pb->second.x);
        if (g.width > 0) dx = std::min(dx, g.width - dx);
        return std::hypot(dx, pa->second.y - pb->second.y);
    }
    // The member order of population.json houses.memberPriority (a council hero ranks by its seat; a regent as a sovereign).
    int member_rank(const Hero& h) const {
        std::string role = h.role == "regent" ? "sovereign" : h.role;
        if (role == "council") { const auto* p = source_of(h); role = p ? p->seat : ""; }
        const auto it = std::find(pop.member_priority.begin(), pop.member_priority.end(), role);
        return it == pop.member_priority.end() ? static_cast<int>(pop.member_priority.size()) : static_cast<int>(it - pop.member_priority.begin());
    }
    std::string race_of(const std::string& civ) const { for (const auto& h : c.heroes) if (h.civilization == civ && !h.race_id.empty()) return h.race_id; return ""; }
    int group_of(const std::string& id) const { const auto it = g.group.find(id); return it == g.group.end() ? -1 : it->second; }
    std::string town_of_city(const std::string& city_uid) const { const auto it = town_of_uid.find(city_uid); return it == town_of_uid.end() ? "" : it->second; }

    // A resident of the civilization and sex whose name no synthesized person carries yet, from a drawn start in the pool (another
    // civilization's pool of that sex when the civilization has none); null when every name is taken.
    std::set<std::string> used_given;
    const source::Npc* resident(const std::string& civ, bool female) {
        std::pair<std::string, bool> key{civ, female};
        if (!pool.count(key) || pool[key].empty()) {
            for (const auto& [k, list] : pool) if (k.second == female && !list.empty()) { key = k; break; }
            if (!pool.count(key) || pool[key].empty()) return nullptr;
        }
        auto& list = pool[key];
        auto& next = pool_next[key];
        if (next == 0) next = 1 + static_cast<std::size_t>(value("kin", "pool|" + key.first + (female ? "|f" : "|m"), 0) % list.size());
        for (std::size_t tries = 0; tries < list.size(); ++tries) {
            const auto* r = list[(next++) % list.size()];
            if (!used_given.count(names::strip_age_tag(r->name))) return r;
        }
        return nullptr;
    }
    // A given name: a free resident name, else a compound of two of the civilization's lexicon roots (as FWGC++ names its people).
    std::string given_of(const std::string& civ, bool female, const std::string& entity, const source::Npc* r = nullptr) {
        std::string name = r && !used_given.count(names::strip_age_tag(r->name)) ? names::strip_age_tag(r->name) : "";
        if (name.empty()) {
            const auto& lex = heritage_of(civ).lexicon;
            for (int d = 0; d < 64 && (name.empty() || used_given.count(name)); ++d)
                name = lex.empty() ? std::string(female ? "Ana" : "Aren") + names::compound({std::string(1, static_cast<char>('a' + d % 26))})
                                   : names::compound({lex[value("kin", entity, 2 * d) % lex.size()].second, lex[value("kin", entity, 2 * d + 1) % lex.size()].second});
        }
        used_given.insert(name);
        return name;
    }
    int age_of(const source::Npc* n, const std::string& civ, const std::string& race) const {
        if (!n) return -1;
        const source::LifeSpan* s = nullptr;
        if (span.count(civ)) s = span.at(civ); else if (span.count(race)) s = span.at(race);
        return bannerlord_age(s, n->age_years, n->age_band, pop.adult_age_min, pop.adult_age_max);
    }

    // Records.
    Hero& add_hero(Hero h) {
        if (h.id.empty()) h.id = reg.assign("hero", h.uid);
        h.synthesized = true;
        h.name = hero_names.take(h.name, heritage_of(h.civilization), value("kin", h.uid, 9));
        h.slots = {{"occupation", "resolved", "MMR-279", h.occupation}, {"characterTemplate", "pending", "MMR-32", ""}, {"ageTraitsAndFamily", "pending", "MMR-32", ""}};
        c.heroes.push_back(std::move(h));
        return c.heroes.back();
    }
    void join(Hero& h, const std::string& bucket, const std::string& clan, const std::string& kingdom) {
        h.bucket = bucket; h.clan_id = clan; h.kingdom_id = kingdom;
        h.affiliation = "clan"; h.occupation = "Lord"; h.settlement_id.clear(); h.settlement_kind.clear();
    }

    void facts();
    void merge_kingdoms();
    void castle_trade();
    void plan_houses();
    void place_house_members();
    void size_houses();
    void plan_companies_and_wanderers();
    void plan_outlaws();
    void plan_wars();
    void name_houses();
    void take_census();
    void plan_reserve();

    struct Unit { std::vector<std::size_t> realms; std::vector<std::string> fiefs; std::vector<campaign::MergeStep> steps; };
    std::vector<Unit> units;
    std::vector<std::tuple<int, int, int, std::int64_t>> rank_key;   // per kingdom (realm): sovereign, fiefs, towns, population
    bool ranks_above(std::size_t a, std::size_t b) const;
    bool land_border(const Unit& a, const Unit& b) const;
    bool shares_group(const Unit& a, const Unit& b) const;
    double unit_distance(const Unit& a, const Unit& b) const;
    std::map<std::string, std::vector<std::string>> neighbours;   // fief -> its 3 nearest fiefs of the same land group
    bool carve(House& donor);
};

// The realm ranking: a realm with a sovereign above a regent's, then more fiefs, more towns, more people, then the uid.
bool Pass::ranks_above(std::size_t a, std::size_t b) const {
    if (rank_key[a] != rank_key[b]) return rank_key[a] > rank_key[b];
    return c.kingdoms[a].uid < c.kingdoms[b].uid;
}

bool Pass::shares_group(const Unit& a, const Unit& b) const {
    for (const auto& f : a.fiefs) for (const auto& e : b.fiefs) if (group_of(f) >= 0 && group_of(f) == group_of(e)) return true;
    return false;
}

bool Pass::land_border(const Unit& a, const Unit& b) const {
    const std::set<std::string> other(b.fiefs.begin(), b.fiefs.end()), mine(a.fiefs.begin(), a.fiefs.end());
    for (const auto& f : a.fiefs) if (neighbours.count(f)) for (const auto& n : neighbours.at(f)) if (other.count(n)) return true;
    for (const auto& f : b.fiefs) if (neighbours.count(f)) for (const auto& n : neighbours.at(f)) if (mine.count(n)) return true;
    return false;
}

double Pass::unit_distance(const Unit& a, const Unit& b) const {
    double best = 1e18;
    for (const auto& f : a.fiefs) for (const auto& e : b.fiefs) best = std::min(best, distance(f, e));
    return best;
}

// Source facts: sex and age from the hero-linked NPC record (a draw when there is none), the people's life stage mapped to Bannerlord years.
void Pass::facts() {
    for (auto& h : c.heroes) {
        const auto it = link.find(h.uid);
        const source::Npc* n = it == link.end() ? nullptr : it->second;
        // A regent the compiler named after an FWGC++ resident (MMR-286) keeps that resident's sex.
        if (!(h.role == "regent" && !h.given.empty())) h.female = n && n->known_sex ? n->female : unit("kin", h.uid + "|sex", 0) < 0.5;
        const int age = age_of(n, h.civilization, h.race_id);
        h.age = age >= 0 ? age : between("kin", h.uid + "|age", 0, 25, 55);
        h.alive = true;
        if (h.role == "regent" && h.given.empty()) h.given = given_of(h.civilization, h.female, h.uid, resident(h.civilization, h.female));
    }
}

void Pass::merge_kingdoms() {
    for (const auto& k : c.kingdoms) {
        bool sovereign = false;
        for (const auto& h : c.heroes) if (h.id == k.ruler_id) sovereign = h.role != "regent";
        std::int64_t population = 0;
        for (const auto& t : c.towns) if (std::find(k.towns.begin(), k.towns.end(), t.id) != k.towns.end()) population += t.population;
        rank_key.emplace_back(sovereign ? 1 : 0, static_cast<int>(k.towns.size() + k.castles.size()), static_cast<int>(k.towns.size()), population);
    }
    // The 3 nearest fiefs of the same land group, per fief: "neighbouring towns" for the land-border test.
    std::vector<std::string> all;
    for (const auto& t : c.towns) all.push_back(t.id);
    for (const auto& k : c.castles) all.push_back(k.id);
    for (const auto& f : all) {
        std::vector<std::pair<double, std::string>> near;
        for (const auto& e : all) if (e != f && group_of(f) >= 0 && group_of(e) == group_of(f)) near.emplace_back(distance(f, e), e);
        std::sort(near.begin(), near.end());
        for (std::size_t i = 0; i < near.size() && i < 3; ++i) neighbours[f].push_back(near[i].second);
    }
    for (std::size_t i = 0; i < c.kingdoms.size(); ++i) {
        Unit u; u.realms = {i};
        u.fiefs = c.kingdoms[i].towns; u.fiefs.insert(u.fiefs.end(), c.kingdoms[i].castles.begin(), c.kingdoms[i].castles.end());
        u.steps = {{c.kingdoms[i].uid, "seed", false}};
        units.push_back(u);
    }
    auto primary = [&](const Unit& u) { std::size_t best = u.realms.front(); for (auto r : u.realms) if (ranks_above(r, best)) best = r; return best; };
    auto smallest_first = [&]() {
        std::vector<std::size_t> order(units.size());
        for (std::size_t i = 0; i < order.size(); ++i) order[i] = i;
        std::sort(order.begin(), order.end(), [&](std::size_t a, std::size_t b) {
            if (units[a].fiefs.size() != units[b].fiefs.size()) return units[a].fiefs.size() < units[b].fiefs.size();
            return c.kingdoms[primary(units[a])].uid < c.kingdoms[primary(units[b])].uid;
        });
        return order;
    };
    auto absorb = [&](std::size_t a, std::size_t b, const std::string& step) {
        const bool water = !shares_group(units[a], units[b]);
        for (const auto& s : units[b].steps) units[a].steps.push_back({s.realm, s.step == "seed" ? step : s.step, s.step == "seed" ? water : s.crosses_water});
        units[a].realms.insert(units[a].realms.end(), units[b].realms.begin(), units[b].realms.end());
        units[a].fiefs.insert(units[a].fiefs.end(), units[b].fiefs.begin(), units[b].fiefs.end());
        units.erase(units.begin() + static_cast<std::ptrdiff_t>(b));
    };
    const std::size_t cap = static_cast<std::size_t>(pop.kingdom_max_fiefs);
    std::size_t total = 0;
    for (const auto& u : units) total += u.fiefs.size();
    const std::size_t even = (total + static_cast<std::size_t>(K) - 1) / static_cast<std::size_t>(K);
    for (const auto& step : pop.merge_order) {
        if (step == "splitAboveMax") {
            // A kingdom of several realms above the maximum splits in two around its primary and its farthest realm, until each part fits.
            for (bool split = true; split;) {
                split = false;
                for (std::size_t i = 0; i < units.size() && !split; ++i) {
                    auto& u = units[i];
                    if (u.fiefs.size() <= cap || u.realms.size() < 2) continue;
                    const std::size_t p = primary(u);
                    auto realm_fiefs = [&](std::size_t r) { Unit x; x.fiefs = c.kingdoms[r].towns; x.fiefs.insert(x.fiefs.end(), c.kingdoms[r].castles.begin(), c.kingdoms[r].castles.end()); return x; };
                    std::size_t far = p; double far_d = -1;
                    for (auto r : u.realms) if (r != p) { const double d = unit_distance(realm_fiefs(p), realm_fiefs(r)); if (d > far_d || (d == far_d && c.kingdoms[r].uid < c.kingdoms[far].uid)) { far_d = d; far = r; } }
                    Unit a, b;
                    for (auto r : u.realms) {
                        const bool to_b = r == far || (r != p && unit_distance(realm_fiefs(far), realm_fiefs(r)) < unit_distance(realm_fiefs(p), realm_fiefs(r)));
                        auto& part = to_b ? b : a;
                        part.realms.push_back(r);
                        const auto f = realm_fiefs(r).fiefs;
                        part.fiefs.insert(part.fiefs.end(), f.begin(), f.end());
                    }
                    for (const auto& s : u.steps) {
                        const bool in_b = std::any_of(b.realms.begin(), b.realms.end(), [&](std::size_t r) { return c.kingdoms[r].uid == s.realm; });
                        (in_b ? b : a).steps.push_back(in_b ? campaign::MergeStep{s.realm, "splitAboveMax", false} : s);
                    }
                    units[i] = a; units.push_back(b);
                    split = true;
                }
            }
            continue;
        }
        if (units.size() <= static_cast<std::size_t>(K)) continue;
        if (step == "civilizationLand") {
            // Same-civilization realms that border on land merge, the smallest first with its nearest such neighbour, up to the even
            // share ceil(F / K), so that the kingdoms can still come out balanced.
            for (bool merged = true; merged && units.size() > static_cast<std::size_t>(K);) {
                merged = false;
                for (std::size_t a : smallest_first()) {
                    std::size_t best = units.size();
                    std::pair<double, std::string> best_key{0, ""};
                    for (std::size_t b = 0; b < units.size(); ++b) {
                        if (b == a || units[a].fiefs.size() + units[b].fiefs.size() > even) continue;
                        bool one_civ = true;
                        for (auto r : units[a].realms) for (auto s2 : units[b].realms) if (c.kingdoms[r].civilization != c.kingdoms[s2].civilization) one_civ = false;
                        if (!one_civ || !land_border(units[a], units[b])) continue;
                        const std::pair<double, std::string> key{unit_distance(units[a], units[b]), c.kingdoms[primary(units[b])].uid};
                        if (best == units.size() || key < best_key) { best = b; best_key = key; }
                    }
                    if (best == units.size()) continue;
                    std::size_t keep = a, gone = best;
                    if (ranks_above(primary(units[best]), primary(units[a]))) std::swap(keep, gone);
                    absorb(keep, gone, step);
                    merged = true;
                    break;
                }
            }
            continue;
        }
        // Presentation culture and proximity: the K largest units (by fiefs, then the realm ranking) seed the kingdoms and take the others,
        // the largest first, each into the kingdom within maxFiefs at the least cost: its distance to the kingdom's seed, times 1.5 for
        // another presentation culture, 1.5 for crossing water and 1.5 beyond the even share ceil(F / K). Distance to the seed keeps the
        // kingdoms compact around their cores.
        std::vector<std::size_t> order(units.size());
        for (std::size_t i = 0; i < order.size(); ++i) order[i] = i;
        std::sort(order.begin(), order.end(), [&](std::size_t x, std::size_t y) {
            if (units[x].fiefs.size() != units[y].fiefs.size()) return units[x].fiefs.size() > units[y].fiefs.size();
            return ranks_above(primary(units[x]), primary(units[y]));
        });
        const std::vector<std::size_t> seeds(order.begin(), order.begin() + K);
        std::vector<Unit> bins;
        for (auto i : seeds) bins.push_back(units[i]);
        const std::vector<Unit> cores = bins;
        for (auto i : order) {
            if (std::find(seeds.begin(), seeds.end(), i) != seeds.end()) continue;
            const auto& u = units[i];
            const auto& ku = c.kingdoms[primary(u)];
            std::size_t best = bins.size();
            std::tuple<int, double, std::string> best_key{9, 0, ""};
            bool best_water = false;
            for (std::size_t j = 0; j < bins.size(); ++j) {
                const std::size_t combined = u.fiefs.size() + bins[j].fiefs.size();
                const bool same_group = shares_group(u, bins[j]);
                if (!same_group && !pop.merge_cross_water) continue;
                const double cost = unit_distance(u, cores[j]) * (ku.presentation == c.kingdoms[primary(cores[j])].presentation ? 1.0 : 1.5) * (same_group ? 1.0 : 1.5) *
                                    (combined > even ? 1.5 : 1.0);
                const std::tuple<int, double, std::string> key{combined > cap ? 1 : 0, cost, c.kingdoms[primary(cores[j])].uid};
                if (best == bins.size() || key < best_key) { best = j; best_key = key; best_water = !same_group; }
            }
            if (best == bins.size()) { bins.push_back(u); continue; }
            auto& bin = bins[best];
            for (const auto& st : u.steps) bin.steps.push_back({st.realm, st.step == "seed" ? step : st.step, st.step == "seed" ? best_water : st.crosses_water});
            bin.realms.insert(bin.realms.end(), u.realms.begin(), u.realms.end());
            bin.fiefs.insert(bin.fiefs.end(), u.fiefs.begin(), u.fiefs.end());
        }
        units = std::move(bins);
    }
    // Apply: the primary realm's kingdom record stays; the others' fiefs, houses and heroes move to it.
    std::vector<campaign::Kingdom> kept;
    std::map<std::string, std::string> moved;   // absorbed kingdom id -> primary kingdom id
    for (auto& u : units) {
        const std::size_t p = primary(u);
        std::vector<std::size_t> realms = u.realms;
        std::sort(realms.begin(), realms.end(), [&](std::size_t a, std::size_t b) { return a == p ? b != p : (b == p ? false : ranks_above(a, b)); });
        campaign::Kingdom k = c.kingdoms[p];
        k.member_realms.clear(); k.merge_steps.clear(); k.towns.clear(); k.castles.clear();
        // The primary is the seed; a first realm it joined takes the primary's own step.
        campaign::MergeStep joined{"", "seed", false};
        for (const auto& s : u.steps) if (s.realm == c.kingdoms[p].uid) joined = s;
        for (auto r : realms) {
            const auto& m = c.kingdoms[r];
            k.member_realms.push_back(m.uid);
            for (const auto& s : u.steps)
                if (s.realm == m.uid) k.merge_steps.push_back(r == p ? campaign::MergeStep{s.realm, "seed", false} : s.step == "seed" ? campaign::MergeStep{s.realm, joined.step, joined.crosses_water} : s);
            k.towns.insert(k.towns.end(), m.towns.begin(), m.towns.end());
            k.castles.insert(k.castles.end(), m.castles.begin(), m.castles.end());
            if (r != p) moved[m.id] = k.id;
        }
        kept.push_back(k);
    }
    std::sort(kept.begin(), kept.end(), [](const campaign::Kingdom& a, const campaign::Kingdom& b) { return a.uid < b.uid; });
    auto remap = [&](std::string& id) { const auto it = moved.find(id); if (it != moved.end()) id = it->second; };
    for (auto& t : c.towns) remap(t.kingdom_id);
    for (auto& x : c.castles) remap(x.kingdom_id);
    for (auto& x : c.houses) remap(x.kingdom_id);
    for (auto& h : c.heroes) remap(h.kingdom_id);
    std::set<std::string> rulers;
    for (const auto& k : kept) rulers.insert(k.ruler_id);
    for (auto& h : c.heroes) h.ruler = rulers.count(h.id) != 0;
    c.kingdoms = std::move(kept);
    // A merged realm's kingdom id leaves the id map (the realm lives on in its kingdom's memberRealms).
    c.id_map.erase(std::remove_if(c.id_map.begin(), c.id_map.end(), [&](const ids::Entry& e) { return e.kind == "kingdom" && moved.count(e.id); }), c.id_map.end());
    ids::Registry fresh(c.prefix, hex_of(c));
    for (const auto& e : c.id_map) fresh.assign(e.kind, e.uid);
    reg = std::move(fresh);
}

// A castle's villages trade with the nearest town of the castle's kingdom within the engine trade limit, 3 x the town spacing S
// (VillageTradeBoundCampaignBehavior; SettlementFactory refuses a castle village without one). Before the merge, a castle whose villages
// have no town of its realm within the limit (seed 61) moves, with its villages, to the realm whose towns serve all of them nearest,
// into that realm's house; the merge then keeps it with that realm. Recorded in census notes and the castle's owner provenance.
void Pass::castle_trade() {
    std::vector<std::string> towns;
    for (const auto& t : c.towns) towns.push_back(t.id);
    double sum = 0; int n = 0;
    for (const auto& a : towns) {
        double best = 1e300;
        for (const auto& b : towns) if (a != b) best = std::min(best, distance(a, b));
        if (best < 1e17) { sum += best; ++n; }
    }
    if (n == 0) return;
    const double limit = 3.0 * sum / n;
    std::map<std::string, std::string> kingdom_of_town;
    for (const auto& t : c.towns) kingdom_of_town[t.id] = t.kingdom_id;
    for (auto& castle : c.castles) {
        std::vector<std::string> villages;
        for (const auto& v : c.villages) if (v.bound_id == castle.id) villages.push_back(v.id);
        if (villages.empty()) continue;
        // The summed distance of the castle's villages to their nearest town of a kingdom; infinite when one has none within the limit.
        auto cost = [&](const std::string& kingdom, std::string* nearest) {
            double total = 0, best_town = 1e300;
            for (const auto& v : villages) {
                double best = 1e300;
                for (const auto& t : towns)
                    if (kingdom_of_town[t] == kingdom && distance(v, t) <= limit) {
                        if (distance(v, t) < best) best = distance(v, t);
                        if (nearest && distance(v, t) < best_town) { best_town = distance(v, t); *nearest = t; }
                    }
                if (best > 1e299) return 1e300;
                total += best;
            }
            return total;
        };
        if (cost(castle.kingdom_id, nullptr) < 1e299) continue;
        std::string target, nearest;
        double best = 1e300;
        for (const auto& k : c.kingdoms) {
            std::string town;
            const double v = cost(k.id, &town);
            if (v < best) { best = v; target = k.id; nearest = town; }
        }
        if (target.empty()) { census.notes.push_back("castle " + castle.id + ": no realm has a town within the trade limit of its villages"); continue; }
        House* to = house_of_fief(nearest);
        House* from = house_of_fief(castle.id);
        if (!to || !from) continue;
        from->fiefs.erase(std::remove(from->fiefs.begin(), from->fiefs.end(), castle.id), from->fiefs.end());
        to->fiefs.push_back(castle.id);
        for (auto& k : c.kingdoms) {
            k.castles.erase(std::remove(k.castles.begin(), k.castles.end(), castle.id), k.castles.end());
            if (k.id == target) k.castles.push_back(castle.id);
        }
        census.notes.push_back("castle " + castle.id + " moved from " + castle.kingdom_id + " to " + target + ": its villages trade with " + nearest);
        castle.owner_provenance += "; moved to realm kingdom " + target + " (its villages' nearest town within the trade limit, population pass)";
        castle.kingdom_id = target;
    }
}

// Carves one fief off a donor house into a new house, by seat priority: a castle with its FWGC++ castellan, a castle with a castellan
// synthesized from the roll ledger, a pretender (on the town sheltering them, else a fief of the donor), a non-capital town's magistrate,
// else a forced seat with a synthesized head. The donor keeps its seat. Returns false when the donor has nothing to give.
bool Pass::carve(House& donor_ref) {
    const std::string donor_id = donor_ref.id;
    std::vector<std::string> spare;
    for (const auto& f : donor_ref.fiefs) if (f != donor_ref.seat_id) spare.push_back(f);
    if (spare.empty()) return false;
    std::set<std::string> heads;
    for (const auto& x : c.houses) heads.insert(x.leader_id);
    std::string seat, origin, head;
    std::sort(spare.begin(), spare.end());
    auto living = [&](const std::string& role, std::function<bool(const Hero&, const source::Person&)> test) -> std::string {
        std::vector<const Hero*> found;
        for (const auto& h : c.heroes) {
            const auto* p = source_of(h);
            if (h.role == role && h.bucket == "house" && !h.synthesized && !heads.count(h.id) && p && test(h, *p)) found.push_back(&h);
        }
        std::sort(found.begin(), found.end(), [](const Hero* a, const Hero* b) { return a->uid < b->uid; });
        return found.empty() ? "" : found.front()->id;
    };
    // a. A castle with its FWGC++ castellan.
    for (const auto& f : spare) {
        if (fief_kind[f] != "castle") continue;
        std::string uid; for (const auto& k : c.castles) if (k.id == f) uid = k.uid;
        const auto id = living("castellan", [&](const Hero&, const source::Person& p) { return p.presence_uid == uid; });
        if (!id.empty()) { seat = f; origin = "castellan"; head = id; break; }
    }
    const House donor = house(donor_id);
    std::string head_civ = hero(donor.leader_id).civilization;
    // b. A castle with a castellan synthesized from the roll ledger (named after the fortress's commander).
    if (seat.empty())
        for (const auto& f : spare) {
            if (fief_kind[f] != "castle") continue;
            const campaign::Castle* castle = nullptr; for (const auto& k : c.castles) if (k.id == f) castle = &k;
            std::string civ = head_civ;
            if (castle && culture_civ.count(castle->culture_uid)) civ = culture_civ.at(castle->culture_uid);
            const auto r = roll.find(castle ? castle->uid : "");
            Hero h;
            h.uid = r != roll.end() && !r->second->candidate_uid.empty() ? r->second->candidate_uid : "castellan:" + (castle ? castle->uid : f);
            if (reg.has("hero", h.uid)) h.uid = "castellan:" + (castle ? castle->uid : f);
            const auto n = commander.find(castle ? castle->uid : "");
            h.female = n != commander.end() ? n->second->female : unit("kin", h.uid + "|sex", 0) < 0.5;
            h.given = given_of(civ, h.female, h.uid, n != commander.end() ? n->second : nullptr);
            const int age = n != commander.end() ? age_of(n->second, civ, "") : -1;
            h.age = age >= 0 ? age : between("kin", h.uid + "|age", 0, 25, 55);
            h.name = h.given; h.role = "castellan"; h.civilization = civ; h.race_id = hero(donor.leader_id).race_id; h.presentation = presentation_of(civ);
            if (h.presentation.empty()) h.presentation = hero(donor.leader_id).presentation;
            h.provenance = "synthesized: castellan of " + f + (r != roll.end() ? " from the castellan roll ledger (" + r->second->candidate_uid + ")" : " (no roll in the ledger)");
            h.bucket = "house";
            head = add_hero(h).id;
            ++census.synthesized["castellans"];
            seat = f; origin = "castellan";
            break;
        }
    // c. A pretender: on the town sheltering them, else on a fief of the donor.
    if (seat.empty()) {
        for (const auto& f : spare) {
            std::string uid; for (const auto& t : c.towns) if (t.id == f) uid = t.uid;
            if (uid.empty()) continue;
            const auto id = living("pretender", [&](const Hero&, const source::Person& p) { return p.presence_uid == uid || (p.presence_uid.empty() && p.home_uid == uid); });
            if (!id.empty()) { seat = f; origin = "pretender"; head = id; break; }
        }
        if (seat.empty()) {
            const auto id = living("pretender", [&](const Hero& h, const source::Person&) { return h.kingdom_id == donor.kingdom_id; });
            if (!id.empty()) { seat = spare.front(); origin = "pretender"; head = id; }
        }
    }
    // d. The magistrate of a non-capital town.
    if (seat.empty())
        for (const auto& f : spare) {
            std::string uid; bool capital = false;
            for (const auto& t : c.towns) if (t.id == f) { uid = t.uid; capital = t.capital; }
            if (uid.empty() || capital) continue;
            const auto id = living("council", [&](const Hero&, const source::Person& p) { return p.home_uid == uid && p.seat == "magistrate"; });
            if (!id.empty()) { seat = f; origin = "magistrate"; head = id; break; }
        }
    // e. A forced seat: a synthesized head named after the fief's magistrate or commander.
    if (seat.empty()) {
        seat = spare.front(); origin = "forcedSeat";
        std::string uid; for (const auto& t : c.towns) if (t.id == seat) uid = t.uid;
        for (const auto& k : c.castles) if (k.id == seat) uid = k.uid;
        const source::Npc* n = magistrate.count(uid) ? magistrate.at(uid) : commander.count(uid) ? commander.at(uid) : nullptr;
        Hero h;
        h.uid = "seat:" + uid;
        h.female = n ? n->female : unit("kin", h.uid + "|sex", 0) < 0.5;
        h.given = given_of(head_civ, h.female, h.uid, n);
        const int age = n ? age_of(n, head_civ, "") : -1;
        h.age = age >= 0 ? age : between("kin", h.uid + "|age", 0, 25, 55);
        h.name = h.given; h.role = "lord"; h.civilization = head_civ; h.race_id = hero(donor.leader_id).race_id; h.presentation = hero(donor.leader_id).presentation;
        h.provenance = "synthesized: head of a forced seat on " + seat; h.bucket = "house";
        head = add_hero(h).id;
        ++census.synthesized["seatHeads"];
    }
    House x;
    const Hero& lead = hero(head);
    x.uid = "house:" + lead.uid; x.id = reg.assign("clan", x.uid);
    x.kingdom_id = donor.kingdom_id; x.leader_id = head; x.home_id = seat; x.seat_id = seat; x.fiefs = {seat}; x.origin = origin;
    x.presentation = lead.presentation; x.culture_uid = donor.culture_uid;
    for (const auto& k : c.castles) if (k.id == seat && !k.culture_uid.empty()) x.culture_uid = k.culture_uid;
    for (const auto& t : c.towns) if (t.id == seat) x.culture_uid = t.culture_uid;
    x.provenance = "population: a " + origin + " house carved from " + donor.id;
    x.slots = {{"tier", "pending", "MMR-31", ""}, {"banner", "pending", "MMR-31", ""}};
    auto& d = house(donor_id);
    d.fiefs.erase(std::remove(d.fiefs.begin(), d.fiefs.end(), seat), d.fiefs.end());
    c.houses.push_back(x);
    return true;
}

void Pass::plan_houses() {
    // Realm houses: the primary realm's is the kingdom's ruling house.
    std::set<std::string> ruling;
    for (const auto& k : c.kingdoms) ruling.insert(k.ruling_house_id);
    for (auto& x : c.houses) x.origin = ruling.count(x.id) ? "kingdomRuler" : "realmRuler";
    const std::int64_t F = static_cast<std::int64_t>(c.towns.size() + c.castles.size());
    // Houses per kingdom: the kingdom's share of H, at least its realms, min(minPerKingdom, fiefs) and ceil(fiefs / maxFiefs).
    std::map<std::string, int> fiefs, target, lower;
    std::map<std::string, double> share;
    for (const auto& k : c.kingdoms) {
        const int n = static_cast<int>(k.towns.size() + k.castles.size());
        int realms = 0; for (const auto& x : c.houses) if (x.kingdom_id == k.id) ++realms;
        fiefs[k.id] = n;
        lower[k.id] = std::max({realms, std::min(pop.house_min_per_kingdom, n), (n + pop.house_max_fiefs - 1) / pop.house_max_fiefs});
        share[k.id] = F ? static_cast<double>(H) * n / static_cast<double>(F) : 0;
        target[k.id] = std::clamp(static_cast<int>(round_half_up(share[k.id])), lower[k.id], std::max(lower[k.id], n));
    }
    auto total = [&]() { int s = 0; for (const auto& [k, n] : target) s += n; return s; };
    while (total() < H) {
        std::string best; double gap = -1e18;
        for (const auto& [k, n] : target) if (n < fiefs[k] && share[k] - n > gap) { gap = share[k] - n; best = k; }
        if (best.empty()) break;
        ++target[best];
    }
    while (total() > H) {
        std::string best; double gap = -1e18;
        for (const auto& [k, n] : target) if (n > lower[k] && n - share[k] > gap) { gap = n - share[k]; best = k; }
        if (best.empty()) break;
        --target[best];
    }
    // Forced seats first (no house above maxFiefs), then the kingdom's further seats, always from its house with the most fiefs.
    for (bool again = true; again;) {
        again = false;
        for (std::size_t i = 0; i < c.houses.size(); ++i)
            if (static_cast<int>(c.houses[i].fiefs.size()) > pop.house_max_fiefs) { if (!carve(c.houses[i])) refuse("population_invalid", "House " + c.houses[i].id + " keeps more than maxFiefs fiefs"); again = true; break; }
    }
    for (const auto& k : c.kingdoms) {
        for (;;) {
            int count = 0; House* donor = nullptr;
            for (auto& x : c.houses) {
                if (x.kingdom_id != k.id) continue;
                ++count;
                if (!donor || x.fiefs.size() > donor->fiefs.size() || (x.fiefs.size() == donor->fiefs.size() && x.id < donor->id)) donor = &x;
            }
            if (count >= target[k.id] || !donor || donor->fiefs.size() < 2) break;
            if (!carve(*donor)) break;
        }
    }
    // Ownership follows the houses.
    for (auto& x : c.houses)
        for (const auto& f : x.fiefs) {
            for (auto& t : c.towns) if (t.id == f) t.owner_clan_id = x.id;
            for (auto& k : c.castles) if (k.id == f) { k.owner_clan_id = x.id; k.owner_provenance = "house " + x.id + " (population pass)"; }
        }
    for (const auto& k : c.kingdoms) {
        int n = 0; for (const auto& x : c.houses) if (x.kingdom_id == k.id) ++n;
        if (n < pop.kingdom_min_houses && n < fiefs[k.id]) census.notes.push_back("kingdom " + k.id + " has " + std::to_string(n) + " houses");
        if (fiefs[k.id] > pop.kingdom_max_fiefs) census.notes.push_back("kingdom " + k.id + " holds " + std::to_string(fiefs[k.id]) + " fiefs, above maxFiefs");
    }
}

// Every house-bucket FWGC++ hero joins the house that holds their seat; the cap spills the lowest-ranked members to the nearest house
// of the kingdom with room. Castellans stay with the house owning their castle.
void Pass::place_house_members() {
    for (auto& x : c.houses) x.members.clear();
    std::map<std::string, std::string> head_of;   // hero -> the house they head
    for (const auto& x : c.houses) head_of[x.leader_id] = x.id;
    auto owner_of_city = [&](const std::string& city_uid) -> std::string {
        const auto t = town_of_city(city_uid);
        if (t.empty()) return "";
        const auto* x = house_of_fief(t);
        return x ? x->id : "";
    };
    std::vector<std::size_t> order;
    for (std::size_t i = 0; i < c.heroes.size(); ++i) if (c.heroes[i].bucket == "house" && c.heroes[i].alive) order.push_back(i);
    std::sort(order.begin(), order.end(), [&](std::size_t a, std::size_t b) { return c.heroes[a].uid < c.heroes[b].uid; });
    for (auto i : order) {
        auto& h = c.heroes[i];
        std::string target;
        if (head_of.count(h.id)) target = head_of.at(h.id);
        else if (const auto* p = source_of(h)) {
            if (h.role == "castellan" && castle_of_uid.count(p->presence_uid)) { const auto* x = house_of_fief(castle_of_uid.at(p->presence_uid)); if (x) target = x->id; }
            if (target.empty() && (h.role == "pretender") && p->presence_kind == "city") target = owner_of_city(p->presence_uid);
            if (target.empty() && p->home_kind == "city") target = owner_of_city(p->home_uid);
            if (target.empty() && p->realm_uid) for (const auto& x : c.houses) if (x.uid == "house:" + *p->realm_uid) target = x.id;
        }
        if (target.empty()) for (const auto& x : c.houses) if (!h.kingdom_id.empty() && x.kingdom_id == h.kingdom_id) { target = x.id; break; }
        if (target.empty()) {
            // A regal hero with no realm, home or town presence (a pretender leading a ruin camp, seed 60): the house owning the town
            // nearest its camp, else the smallest house. Recorded as overflow.
            std::string near;
            if (const auto* p = source_of(h); p && p->presence_kind == "camp")
                for (const auto& camp : w.camps)
                    if (camp.uid == p->presence_uid) {
                        const double period = w.metadata.grid_side > 1 ? static_cast<double>(w.metadata.grid_side - 1) : 0;
                        double best = 1e300;
                        for (const auto& city : w.cities) {
                            if (town_of_city(city.uid).empty()) continue;
                            double dx = std::abs(static_cast<double>(city.x - camp.x));
                            if (period > 0) dx = std::min(dx, period - dx);
                            const double d = std::hypot(dx, static_cast<double>(city.z - camp.z));
                            if (d < best) { best = d; near = town_of_city(city.uid); }
                        }
                    }
            if (const auto* x = near.empty() ? nullptr : house_of_fief(near)) target = x->id;
            for (const auto& x : c.houses) if (target.empty() || (near.empty() && x.members.size() < house(target).members.size())) target = x.id;
            census.overflow.push_back({h.id, "no realm, home or town", target, "a realm-less regal hero joins the house nearest its camp"});
        }
        if (target.empty()) refuse("population_unassigned", "House-bucket hero " + h.uid + " has no house");
        auto& x = house(target);
        x.members.push_back(h.id);
        join(h, "house", x.id, x.kingdom_id);
    }
    // The cap: head first, then castellans of the house's castles, then by member priority, fame and uid.
    for (std::size_t xi = 0; xi < c.houses.size(); ++xi) {
        if (static_cast<int>(c.houses[xi].members.size()) <= pop.house_cap) continue;
        auto& x = c.houses[xi];
        auto keeps = [&](const std::string& id) {
            const auto& h = hero(id);
            if (id == x.leader_id) return 0;
            if (h.role == "castellan") if (const auto* p = source_of(h)) if (castle_of_uid.count(p->presence_uid) && std::count(x.fiefs.begin(), x.fiefs.end(), castle_of_uid.at(p->presence_uid))) return 1;
            return 2 + member_rank(h);
        };
        std::vector<std::string> m = x.members;
        std::stable_sort(m.begin(), m.end(), [&](const std::string& a, const std::string& b) {
            const int ka = keeps(a), kb = keeps(b);
            if (ka != kb) return ka < kb;
            const auto *pa = source_of(hero(a)), *pb = source_of(hero(b));
            const double fa = pa ? pa->fame : 0, fb = pb ? pb->fame : 0;
            if (fa != fb) return fa > fb;
            return hero(a).uid < hero(b).uid;
        });
        const std::string from = x.id, kingdom = x.kingdom_id, home = x.home_id;
        x.members.assign(m.begin(), m.begin() + pop.house_cap);
        for (std::size_t j = static_cast<std::size_t>(pop.house_cap); j < m.size(); ++j) {
            House* best = nullptr; double bd = 1e300;
            for (auto& y : c.houses) {
                if (y.id == from || static_cast<int>(y.members.size()) >= pop.house_cap) continue;
                const double d = distance(home, y.home_id) + (y.kingdom_id == kingdom ? 0 : 1e9);
                if (d < bd || (d == bd && y.id < best->id)) { bd = d; best = &y; }
            }
            if (!best) refuse("population_unassigned", "No house has room for " + m[j]);
            best->members.push_back(m[j]);
            join(hero(m[j]), "house", best->id, best->kingdom_id);
            census.overflow.push_back({m[j], from, best->id, "house cap " + std::to_string(pop.house_cap)});
        }
    }
}

// House sizes: a chaotic 3..8 draw per house (at least the planned members, plus a spouse when the members are of one sex), then
// trimmed or grown one member at a time, within 3..8, toward the adult target adultLordsPerHouse x H; parented minors are planned in
// the minor share (minorShare of all planned members) in a drawn house order. Then synthesized kin fill each house: a spouse for the
// head, siblings with a dead parent, adult children, and the planned minor.
void Pass::size_houses() {
    std::map<std::string, int> size, floor_of;
    std::set<std::string> minor_at;
    std::vector<std::string> ids;
    for (const auto& x : c.houses) ids.push_back(x.id);
    std::sort(ids.begin(), ids.end());
    for (const auto& id : ids) {
        House& x = house(id);
        bool male = false, female = false;
        for (const auto& m : x.members) (hero(m).female ? female : male) = true;
        int floor = std::max(pop.house_size_min, static_cast<int>(x.members.size()) + (male && female ? 0 : 1));
        if (floor > pop.house_cap) {
            // A full house of one sex: its lowest-ranked member (never the head or a castellan of its castles) moves to the nearest house
            // with room, making room for a spouse.
            std::string id2;
            for (auto it = x.members.rbegin(); it != x.members.rend() && id2.empty(); ++it) if (*it != x.leader_id && hero(*it).role != "castellan") id2 = *it;
            House* best = nullptr; double bd = 1e300;
            for (auto& y : c.houses) if (y.id != x.id && static_cast<int>(y.members.size()) < pop.house_cap) { const double d = distance(x.home_id, y.home_id) + (y.kingdom_id == x.kingdom_id ? 0 : 1e9); if (d < bd) { bd = d; best = &y; } }
            if (!best || id2.empty()) refuse("population_invalid", "House " + x.id + " is full and of one sex");
            x.members.erase(std::find(x.members.begin(), x.members.end(), id2));
            best->members.push_back(id2);
            join(hero(id2), "house", best->id, best->kingdom_id);
            census.overflow.push_back({id2, x.id, best->id, "one sex at the house cap"});
            floor = pop.house_cap;
        }
        floor_of[id] = floor;
        size[id] = std::max(floor, between("houses", x.uid, 0, pop.house_size_min, pop.house_size_max));
    }
    // Minors: houses whose head is at least 20 and that have room for a spouse and a child, in a drawn order, up to the minor share.
    std::int64_t planned = 0;
    for (const auto& [id, n] : size) planned += n;
    if (pop.minors) {
        const auto cap = static_cast<std::int64_t>(std::floor(pop.minor_share * static_cast<double>(planned)));
        std::vector<std::pair<std::uint64_t, std::string>> order;
        for (const auto& id : ids) {
            const House& x = house(id);
            const int kin_slots = size[id] - static_cast<int>(x.members.size());
            if (kin_slots >= (hero(x.leader_id).spouse_id.empty() ? 2 : 1) && hero(x.leader_id).age >= pop.adult_age_min + 2) order.emplace_back(value("kin", x.uid + "|minor", 0), id);
        }
        std::sort(order.begin(), order.end());
        for (std::size_t i = 0; i < order.size() && static_cast<std::int64_t>(minor_at.size()) < cap; ++i) minor_at.insert(order[i].second);
    }
    // The adult budget: one member at a time off the largest house above its floor (or onto the smallest below the draw maximum).
    const auto adult_target = round_half_up(pop.adult_lords_per_house * static_cast<double>(c.houses.size()));
    auto adults = [&]() { std::int64_t a = 0; for (const auto& [id, n] : size) a += n - (minor_at.count(id) ? 1 : 0); return a; };
    auto lowest = [&](const std::string& id) { return std::max(floor_of[id], static_cast<int>(house(id).members.size()) + (minor_at.count(id) ? 2 : 0)); };
    for (std::int64_t a = adults(); a > adult_target; --a) {
        std::string best;
        for (const auto& id : ids) if (size[id] > lowest(id) && (best.empty() || size[id] > size[best])) best = id;
        if (best.empty()) break;
        --size[best];
    }
    for (std::int64_t a = adults(); a < adult_target; ++a) {
        std::string best;
        for (const auto& id : ids) if (size[id] < pop.house_size_max && (best.empty() || size[id] < size[best])) best = id;
        if (best.empty()) break;
        ++size[best];
    }
    for (const auto& id : ids) {
        House& x = house(id);
        const std::string house_uid = x.uid, house_id = x.id, kingdom = x.kingdom_id;
        const Hero lead = hero(x.leader_id);
        const std::string root = names::strip_age_tag(lead.given.empty() ? lead.name : lead.given);
        const std::string pattern = heritage_of(lead.civilization).pattern;
        const int slots = size[id] - static_cast<int>(x.members.size());
        std::string spouse, parent;
        int n = 0;
        auto kin = [&](const std::string& role, bool female, int age, const std::string& father, const std::string& mother, bool alive) -> Hero& {
            Hero h;
            h.uid = "kin:" + house_uid + ":" + std::to_string(n++);
            h.female = female; h.age = age; h.alive = alive; h.role = role;
            h.civilization = lead.civilization; h.race_id = lead.race_id; h.presentation = lead.presentation;
            h.given = given_of(lead.civilization, female, h.uid, resident(lead.civilization, female));
            std::string parent_given;
            if (!father.empty() || !mother.empty()) parent_given = hero(!father.empty() ? father : mother).given;
            h.name = names::person_name(pattern, h.given, root, parent_given, female);
            h.father_id = father; h.mother_id = mother;
            h.provenance = "synthesized: " + role + " in " + house_id + " (population pass; FWGC++ resident name, " + (pattern.empty() ? std::string("house") : pattern) + ")";
            join(h, "house", house_id, kingdom);
            Hero& added = add_hero(h);
            if (alive) house(house_id).members.push_back(added.id);
            ++census.synthesized[role == "parent" ? "deadParents" : role];
            return added;
        };
        int left = slots;
        std::string lead_id = lead.id;
        if (left > 0 && hero(lead_id).spouse_id.empty()) {
            const Hero& l = hero(lead_id);
            const int age = std::clamp(l.age + between("kin", house_uid + "|spouse", 0, -8, 6), pop.adult_age_min, pop.adult_age_max);
            const bool female = !l.female;
            spouse = kin("spouse", female, age, "", "", true).id;
            hero(spouse).spouse_id = lead_id; hero(lead_id).spouse_id = spouse;
            --left;
        } else if (!hero(lead_id).spouse_id.empty()) spouse = hero(lead_id).spouse_id;
        // Each sex among the adults: the spouse is the head's other sex; a house still of one sex makes its next kin the missing sex.
        auto missing = [&]() -> int {
            bool male = false, female = false;
            for (const auto& m : house(house_id).members) { const auto& h = hero(m); if (h.age >= pop.adult_age_min) (h.female ? female : male) = true; }
            return !male ? 0 : !female ? 1 : -1;   // 0: male missing, 1: female missing
        };
        const bool minor_here = left > 0 && minor_at.count(id) && !spouse.empty();
        if (minor_here) --left;
        for (int i = 0; i < left; ++i) {
            const Hero l = hero(lead_id);
            const int need = missing();
            const bool female = need >= 0 ? need == 1 : unit("kin", house_uid + "|sex", i) < 0.49;
            const std::string father = l.female ? spouse : lead_id, mother = l.female ? lead_id : spouse;
            const int oldest = std::min(l.age, spouse.empty() ? l.age : hero(spouse).age) - 20;
            const bool child = unit("kin", house_uid + "|kind", i) >= 0.5 && oldest >= pop.adult_age_min;
            if (child) {
                kin("child", female, between("kin", house_uid + "|age", i, pop.adult_age_min, oldest), spouse.empty() && l.female ? "" : father, spouse.empty() && !l.female ? "" : mother, true);
            } else {
                if (parent.empty() && pop.dead_parents) {
                    const bool pf = unit("kin", house_uid + "|parent", 0) < 0.5;
                    parent = kin("parent", pf, std::min(l.age + between("kin", house_uid + "|parent", 1, 22, 35), 110), "", "", false).id;
                    (pf ? hero(lead_id).mother_id : hero(lead_id).father_id) = parent;
                }
                const bool pf = !parent.empty() && hero(parent).female;
                kin("sibling", female, std::clamp(l.age + between("kin", house_uid + "|age", i, -12, 12), pop.adult_age_min, pop.adult_age_max),
                    pf ? "" : parent, pf ? parent : "", true);
            }
        }
        if (minor_here) {
            const Hero l = hero(lead_id);
            const int top = std::min({17, l.age - pop.adult_age_min, hero(spouse).age - pop.adult_age_min});
            if (top >= 2) {
                const std::string father = l.female ? spouse : lead_id, mother = l.female ? lead_id : spouse;
                kin("minor", unit("kin", house_uid + "|minorSex", 0) < 0.49, between("kin", house_uid + "|minorAge", 0, 2, top), father, mother, true);
            } else {
                kin("sibling", unit("kin", house_uid + "|sex", 99) < 0.49, std::clamp(l.age + between("kin", house_uid + "|age", 99, -12, 12), pop.adult_age_min, pop.adult_age_max), "", "", true);
            }
        }
    }
}

void Pass::plan_companies_and_wanderers() {
    // The living FWGC++ sources by role, each in fame then uid order.
    auto sources = [&](const std::string& role) {
        std::vector<std::size_t> out;
        for (std::size_t i = 0; i < c.heroes.size(); ++i) if (c.heroes[i].role == role && !c.heroes[i].synthesized && c.heroes[i].alive) out.push_back(i);
        std::sort(out.begin(), out.end(), [&](std::size_t a, std::size_t b) {
            const auto *pa = source_of(c.heroes[a]), *pb = source_of(c.heroes[b]);
            const double fa = pa ? pa->fame : 0, fb = pb ? pb->fame : 0;
            return fa != fb ? fa > fb : c.heroes[a].uid < c.heroes[b].uid;
        });
        return out;
    };
    std::set<std::string> used;
    auto remnant_hired = [&](std::size_t i) {
        const auto* p = source_of(c.heroes[i]);
        if (!p || p->join_condition != "hired" || !org.count(p->org_uid)) return false;
        return org.at(p->org_uid)->kind == "remnant";
    };
    std::vector<std::pair<std::string, std::string>> captains;   // (hero id or "", source kind)
    for (const auto& kind : pop.captain_order) {
        if (static_cast<int>(captains.size()) >= M) break;
        std::vector<std::size_t> list;
        if (kind == "remnantChampionHired") for (auto i : sources("champion")) { if (remnant_hired(i)) list.push_back(i); }
        else if (kind == "livingWarlord") list = sources("warlord");
        else if (kind == "domainHolder") list = sources("domain_holder");
        else if (kind == "synthesized") { while (static_cast<int>(captains.size()) < M) captains.push_back({"", kind}); break; }
        for (auto i : list) { if (static_cast<int>(captains.size()) >= M) break; if (used.insert(c.heroes[i].id).second) captains.push_back({c.heroes[i].id, kind}); }
    }
    auto town_near = [&](const std::string& presentation, const std::string& entity) {
        std::vector<std::string> same, any;
        for (const auto& t : c.towns) { any.push_back(t.id); if (t.presentation == presentation) same.push_back(t.id); }
        const auto& list = same.empty() ? any : same;
        return list[value("companies", entity, 1) % list.size()];
    };
    for (std::size_t i = 0; i < captains.size(); ++i) {
        MinorClan k;
        std::string captain = captains[i].first, civ, presentation, home, wanted, source = captains[i].second;
        if (!captain.empty()) {
            const auto& h = hero(captain);
            civ = h.civilization; presentation = h.presentation;
            const auto* p = source_of(h);
            if (p && p->presence_kind == "city") home = town_of_city(p->presence_uid);
            if (home.empty() && p && p->home_kind == "city") home = town_of_city(p->home_uid);
            const std::string given = names::strip_age_tag(h.given.empty() ? h.name : h.given);
            if (source == "remnantChampionHired" && p && org.count(p->org_uid) && !org.at(p->org_uid)->name.empty()) {
                std::string n = names::strip_age_tag(org.at(p->org_uid)->name);
                if (n.rfind("the ", 0) == 0) n = n.substr(4);
                wanted = names::capitalized(n); k.source_name = org.at(p->org_uid)->name; source = "remnant:" + p->org_uid;
            } else if (source == "domainHolder" && p && !p->lost_name.empty()) {
                wanted = "Wardens of " + names::strip_age_tag(p->lost_name); source = "domainHolder:" + h.uid;
            } else {
                wanted = "Companions of " + given; source = source + ":" + h.uid;
            }
            k.uid = "company:" + h.uid;
        } else {
            k.uid = "company:synthesized:" + std::to_string(i);
            const auto& t = c.towns[value("companies", k.uid, 0) % c.towns.size()];
            civ = t.civilization; presentation = t.presentation; home = t.id;
            const auto& lex = heritage_of(civ).lexicon;
            wanted = (lex.empty() ? std::string("Free") : names::compound({lex[value("companies", k.uid, 2) % lex.size()].second, lex[value("companies", k.uid, 3) % lex.size()].second})) + " Company";
        }
        if (presentation.empty() || !pop.mercenary.count(presentation)) presentation = pop.mercenary.begin()->first;
        if (home.empty()) home = town_near(presentation, k.uid);
        k.id = reg.assign("clan", k.uid);
        k.name = clan_names.take(wanted, heritage_of(civ), value("companies", k.uid, 4));
        k.name_provenance = "population: " + source;
        k.home_id = home; k.presentation = presentation; k.kind = "mercenary"; k.source = source;
        for (const auto& t : c.towns) if (t.id == home) k.culture_uid = t.culture_uid;
        const auto& line = pop.mercenary.at(presentation);
        k.party_template = line.party_template; k.character_templates = line.character_templates; k.tier = pop.company_tier;
        k.slots = {{"banner", "pending", "MMR-280", ""}, {"colors", "pending", "MMR-280", ""}};
        if (captain.empty()) {
            Hero h;
            h.uid = "captain:" + k.uid;
            const auto* r = resident(civ, unit("companies", k.uid, 5) < 0.3);
            h.female = r ? r->female : false;
            h.given = given_of(civ, h.female, h.uid, r);
            const int age = age_of(r, civ, "");
            h.age = age >= 0 ? age : between("companies", h.uid, 6, 25, 50);
            h.name = h.given; h.role = "captain"; h.civilization = civ; h.presentation = presentation; h.provenance = "synthesized: captain of " + k.uid;
            h.race_id = race_of(civ);
            captain = add_hero(h).id;
            ++census.synthesized["captains"];
        }
        k.leader_id = captain;
        k.members = {captain};
        join(hero(captain), "company", k.id, "");
        c.companies.push_back(k);
    }
    // Ruin wardens: the domain holder warding the ruin a remnant order comes from.
    for (auto& k : c.companies) {
        if (k.source.rfind("remnant:", 0) != 0) continue;
        const auto* o = org.at(k.source.substr(8));
        for (auto i : sources("domain_holder")) {
            const auto* p = source_of(c.heroes[i]);
            if (!p || used.count(c.heroes[i].id) || p->presence_uid != o->home_uid) continue;
            used.insert(c.heroes[i].id);
            k.members.push_back(c.heroes[i].id);
            join(c.heroes[i], "company", k.id, "");
            break;
        }
    }
    auto room = [&](const std::string& presentation) -> MinorClan* {
        MinorClan* best = nullptr;
        for (auto& k : c.companies) {
            if (static_cast<int>(k.members.size()) >= pop.company_members_max) continue;
            const bool same = k.presentation == presentation;
            if (!best || (same && best->presentation != presentation) || (same == (best->presentation == presentation) && k.members.size() < best->members.size())) best = &k;
        }
        return best;
    };
    auto enlist = [&](std::size_t i, const std::string& why) {
        auto& h = c.heroes[i];
        MinorClan* k = room(h.presentation);
        if (!k) {
            // Every company is full: one more company, captained by this hero (recorded).
            MinorClan x;
            x.uid = "company:" + h.uid; x.id = reg.assign("clan", x.uid);
            x.presentation = pop.mercenary.count(h.presentation) ? h.presentation : pop.mercenary.begin()->first;
            const auto& line = pop.mercenary.at(x.presentation);
            x.party_template = line.party_template; x.character_templates = line.character_templates; x.tier = pop.company_tier; x.kind = "mercenary";
            const auto* p = source_of(h);
            x.home_id = p && p->presence_kind == "city" ? town_of_city(p->presence_uid) : "";
            if (x.home_id.empty()) x.home_id = town_near(x.presentation, x.uid);
            for (const auto& t : c.towns) if (t.id == x.home_id) x.culture_uid = t.culture_uid;
            x.name = clan_names.take("Companions of " + names::strip_age_tag(h.given.empty() ? h.name : h.given), heritage_of(h.civilization), value("companies", x.uid, 4));
            x.name_provenance = "population: overflow company"; x.source = "overflow:" + h.uid; x.leader_id = h.id;
            x.slots = {{"banner", "pending", "MMR-280", ""}, {"colors", "pending", "MMR-280", ""}};
            c.companies.push_back(x);
            census.notes.push_back("company " + x.id + " added: every company was full");
            k = &c.companies.back();
        }
        k->members.push_back(h.id);
        join(h, "company", k->id, "");
        used.insert(h.id);
        if (!why.empty()) census.overflow.push_back({h.id, why, k->id, "companies take what the wanderers leave"});
    };
    for (auto i : sources("warlord")) if (!used.count(c.heroes[i].id)) enlist(i, "");
    // Wanderers: champions not captains, magisters, exiles and the remaining domain holders, rotating by civilization, up to W.
    std::map<std::string, std::vector<std::size_t>> by_civ;
    for (const auto& s : pop.wanderer_sources) {
        const std::string role = s == "domainHolder" ? "domain_holder" : s;
        for (auto i : sources(role)) if (!used.count(c.heroes[i].id)) by_civ[c.heroes[i].civilization].push_back(i);
    }
    std::vector<std::size_t> wanderers;
    for (std::size_t round = 0; static_cast<int>(wanderers.size()) < W; ++round) {
        bool any = false;
        for (auto& [civ, list] : by_civ) {
            if (round < list.size() && static_cast<int>(wanderers.size()) < W) { wanderers.push_back(list[round]); any = true; }
        }
        if (!any) break;
    }
    for (auto i : wanderers) {
        auto& h = c.heroes[i];
        used.insert(h.id);
        h.bucket = "wanderer"; h.occupation = "Wanderer"; h.affiliation = "none"; h.clan_id.clear(); h.settlement_id.clear(); h.settlement_kind.clear();
        c.wanderers.push_back({h.id, h.presentation, names::strip_age_tag(h.epithet), h.female});
    }
    if (static_cast<int>(wanderers.size()) < W) census.notes.push_back("only " + std::to_string(wanderers.size()) + " source wanderers for W " + std::to_string(W) + ": the runtime tops up");
    // What the wanderers leave (champions and domain holders) fills the companies.
    for (const char* role : {"champion", "domain_holder", "magister", "exile"})
        for (auto i : sources(role)) if (!used.count(c.heroes[i].id)) enlist(i, "wanderer");
    // Each company fills to its drawn size (6..10) with soldiers named after FWGC++ garrison residents.
    for (auto& k : c.companies) {
        const int target = std::max(static_cast<int>(k.members.size()), between("companies", k.uid, 7, pop.company_members_min, pop.company_members_max));
        const std::string civ = hero(k.leader_id).civilization;
        for (int n = 0; static_cast<int>(k.members.size()) < target; ++n) {
            Hero h;
            h.uid = "soldier:" + k.uid + ":" + std::to_string(n);
            const bool female = unit("companies", h.uid, 0) < 0.2;
            const auto* r = resident(civ, female);
            h.female = r ? r->female : female;
            h.given = given_of(civ, h.female, h.uid, r);
            const int age = age_of(r, civ, "");
            h.age = age >= 0 ? age : between("companies", h.uid, 1, 20, 45);
            h.name = names::person_name(heritage_of(civ).pattern, h.given, "", "", h.female);
            h.role = "soldier"; h.civilization = civ; h.presentation = k.presentation; h.race_id = hero(k.leader_id).race_id;
            h.provenance = "synthesized: soldier of " + k.id + " (FWGC++ garrison resident name)";
            join(h, "company", k.id, "");
            k.members.push_back(add_hero(h).id);
            ++census.synthesized["soldiers"];
        }
    }
}

void Pass::plan_outlaws() {
    struct Source { std::string uid, kind, civ; std::int64_t x = 0, z = 0; bool placed = false; std::string name; };
    std::vector<Source> sources;
    for (const auto& want : pop.outlaw_leader_order) {
        std::vector<Source> list;
        if (want == "banditBandLeader" || want == "deserterBandLeader")
            for (const auto& b : w.bands) if (b.classification == (want == "banditBandLeader" ? "bandits" : "deserters")) list.push_back({b.uid, want, "", b.x, b.z, true, ""});
        if (want == "ruinCampChief") for (const auto& k : w.camps) list.push_back({k.uid, want, k.civilization_id, k.x, k.z, true, k.name});
        std::sort(list.begin(), list.end(), [](const Source& a, const Source& b) { return a.uid < b.uid; });
        sources.insert(sources.end(), list.begin(), list.end());
        if (want == "synthesized") for (int i = 0; static_cast<int>(sources.size()) < O; ++i) sources.push_back({"synthesized:" + std::to_string(i), want, "", 0, 0, false, ""});
    }
    if (static_cast<int>(sources.size()) > O) sources.resize(static_cast<std::size_t>(O));
    std::map<std::string, int> kingdoms_of;
    for (const auto& k : c.kingdoms) ++kingdoms_of[k.presentation];
    const double period = w.metadata.grid_side > 1 ? static_cast<double>(w.metadata.grid_side - 1) : 0;
    for (const auto& s : sources) {
        // Home and culture: the nearest town whose presentation culture faces at most maxWarKingdoms kingdoms (the war guard).
        std::vector<std::pair<double, std::string>> towns;
        for (const auto& city : w.cities) {
            const auto t = town_of_city(city.uid);
            if (t.empty()) continue;
            double dx = std::abs(static_cast<double>(city.x - s.x));
            if (period > 0) dx = std::min(dx, period - dx);
            const double d = s.placed ? std::hypot(dx, static_cast<double>(city.z - s.z)) : static_cast<double>(value("outlaws", s.uid + "|" + t, 0) >> 11);
            towns.emplace_back(d, t);
        }
        std::sort(towns.begin(), towns.end());
        std::string home;
        for (const auto& [d, t] : towns) {
            for (const auto& town : c.towns) if (town.id == t && kingdoms_of[town.presentation] <= pop.outlaw_max_war_kingdoms && pop.outlaw_lines.count(town.presentation)) home = t;
            if (!home.empty()) break;
        }
        if (home.empty() && !towns.empty()) {
            std::string fewest;
            for (const auto& [p, lines] : pop.outlaw_lines) if (fewest.empty() || kingdoms_of[p] < kingdoms_of[fewest]) fewest = p;
            for (const auto& [d, t] : towns) { for (const auto& town : c.towns) if (town.id == t && town.presentation == fewest) home = t; if (!home.empty()) break; }
            if (home.empty()) home = towns.front().second;
            census.notes.push_back("outlaw clan from " + s.uid + ": no culture within the war guard; the culture with the fewest kingdoms");
        }
        const campaign::Town* town = nullptr; for (const auto& t : c.towns) if (t.id == home) town = &t;
        MinorClan k;
        k.uid = "outlaw:" + s.uid; k.id = reg.assign("clan", k.uid);
        k.presentation = town ? town->presentation : pop.outlaw_lines.begin()->first;
        if (!pop.outlaw_lines.count(k.presentation)) k.presentation = pop.outlaw_lines.begin()->first;
        k.home_id = home; k.culture_uid = town ? town->culture_uid : "";
        const std::string civ = !s.civ.empty() ? s.civ : town ? town->civilization : "";
        const std::string want = s.kind == "banditBandLeader" ? "mafia" : s.kind == "deserterBandLeader" ? "nomad" : s.kind == "ruinCampChief" ? "sect" : "mafia";
        const auto& lines = pop.outlaw_lines.at(k.presentation);
        const policy::TroopLine* line = &lines.front();
        for (const auto& l : lines) if (l.kind == want) { line = &l; break; }
        k.kind = line->kind; k.party_template = line->party_template; k.character_templates = line->character_templates; k.tier = pop.outlaw_tier;
        k.source = s.uid == "" ? "synthesized" : (s.kind + ":" + s.uid);
        k.source_name = s.name;
        const auto& lex = heritage_of(civ).lexicon;
        const std::string noun = k.kind == "mafia" ? " Brotherhood" : k.kind == "nomad" ? " Riders" : " Covenant";
        const std::string root = lex.empty() ? std::string("Grey") : names::compound({lex[value("outlaws", k.uid, 2) % lex.size()].second, lex[value("outlaws", k.uid, 3) % lex.size()].second});
        k.name = clan_names.take(root + noun, heritage_of(civ), value("outlaws", k.uid, 4));
        k.name_provenance = "population: " + k.source + " (lexicon roots, " + want + (k.kind != want ? " -> " + k.kind + ": the culture has no " + want + " line" : "") + ")";
        for (const auto& kingdom : c.kingdoms) if (kingdom.presentation == k.presentation) k.wars.push_back(kingdom.id);
        std::sort(k.wars.begin(), k.wars.end());
        k.slots = {{"banner", "pending", "MMR-280", ""}, {"colors", "pending", "MMR-280", ""}};
        const int size = between("outlaws", k.uid, 5, pop.outlaw_members_min, pop.outlaw_members_max);
        const std::string race = race_of(civ);
        for (int n = 0; n < size; ++n) {
            Hero h;
            h.uid = "outlaw:" + s.uid + ":" + std::to_string(n);
            const bool female = unit("outlaws", h.uid, 0) < (n == 0 ? 0.15 : 0.25);
            const auto* r = resident(civ, female);
            h.female = r ? r->female : female;
            h.given = given_of(civ, h.female, h.uid, r);
            const int age = age_of(r, civ, race);
            h.age = age >= 0 ? age : between("outlaws", h.uid, 1, 20, 50);
            h.name = h.given; h.role = n == 0 ? "outlawLeader" : "outlaw"; h.civilization = civ; h.race_id = race; h.presentation = k.presentation;
            h.provenance = "synthesized: " + std::string(n == 0 ? "leader" : "member") + " of " + k.uid + " (FWGC++ resident name)";
            join(h, "outlaw", k.id, "");
            k.members.push_back(add_hero(h).id);
            ++census.synthesized["outlaws"];
        }
        k.leader_id = k.members.front();
        c.outlaws.push_back(k);
    }
}

// About one starting war per kingdom: in a drawn order, each kingdom without a war takes the partner with the fewest wars, a land
// border first, then the nearest (by sea), never above maxPerKingdom.
void Pass::plan_wars() {
    if (c.kingdoms.size() < 2 || pop.wars_per_kingdom <= 0) return;
    std::vector<Unit> ku;
    for (const auto& k : c.kingdoms) { Unit u; u.fiefs = k.towns; u.fiefs.insert(u.fiefs.end(), k.castles.begin(), k.castles.end()); ku.push_back(u); }
    std::vector<int> wars(c.kingdoms.size(), 0);
    std::set<std::pair<std::size_t, std::size_t>> pairs;
    std::vector<std::size_t> order(c.kingdoms.size());
    for (std::size_t i = 0; i < order.size(); ++i) order[i] = i;
    std::sort(order.begin(), order.end(), [&](std::size_t a, std::size_t b) {
        const auto va = value("wars", c.kingdoms[a].uid, 0), vb = value("wars", c.kingdoms[b].uid, 0);
        return va != vb ? va < vb : c.kingdoms[a].uid < c.kingdoms[b].uid;
    });
    for (auto a : order) {
        while (wars[a] < pop.wars_per_kingdom) {
            std::size_t best = order.size();
            std::tuple<int, int, double, std::string> best_key{9, 9, 0, ""};
            for (std::size_t b = 0; b < c.kingdoms.size(); ++b) {
                if (b == a || wars[b] >= pop.wars_max_per_kingdom || pairs.count({std::min(a, b), std::max(a, b)})) continue;
                const bool border = land_border(ku[a], ku[b]);
                std::tuple<int, int, double, std::string> key{wars[b] > 0 ? 1 : 0, border ? 0 : 1, unit_distance(ku[a], ku[b]), c.kingdoms[b].uid};
                if (best == order.size() || key < best_key) { best = b; best_key = key; }
            }
            if (best == order.size()) break;
            pairs.insert({std::min(a, best), std::max(a, best)});
            ++wars[a]; ++wars[best];
            const auto& x = c.kingdoms[std::min(a, best)].id < c.kingdoms[std::max(a, best)].id ? std::make_pair(a, best) : std::make_pair(best, a);
            c.wars.push_back({c.kingdoms[x.first].id, c.kingdoms[x.second].id, std::get<1>(best_key) == 0 ? "landBorder" : "nearestBySea"});
        }
    }
    std::sort(c.wars.begin(), c.wars.end(), [](const campaign::War& a, const campaign::War& b) { return std::tie(a.a, a.b) < std::tie(b.a, b.b); });
}

// House names: the head's given name in the head's naming pattern, unique among all clans by a lexicon suffix (never a numeral).
void Pass::name_houses() {
    std::vector<std::size_t> order(c.houses.size());
    for (std::size_t i = 0; i < order.size(); ++i) order[i] = i;
    std::sort(order.begin(), order.end(), [&](std::size_t a, std::size_t b) { return c.houses[a].uid < c.houses[b].uid; });
    for (auto i : order) {
        auto& x = c.houses[i];
        const auto& lead = hero(x.leader_id);
        const std::string root = names::strip_age_tag(lead.given.empty() ? lead.name : lead.given);
        if (x.source_name.empty()) x.source_name = x.name;
        x.name = clan_names.take(names::house_name(heritage_of(lead.civilization).pattern, root), heritage_of(lead.civilization), value("houses", x.uid, 1));
        x.provenance = "population: " + x.origin + " house named after " + lead.uid + " (" + (heritage_of(lead.civilization).pattern.empty() ? std::string("house") : heritage_of(lead.civilization).pattern) + ")";
        x.presentation = lead.presentation;
    }
    std::sort(c.houses.begin(), c.houses.end(), [](const House& a, const House& b) { return a.id < b.id; });
    std::sort(c.companies.begin(), c.companies.end(), [](const MinorClan& a, const MinorClan& b) { return a.id < b.id; });
    std::sort(c.outlaws.begin(), c.outlaws.end(), [](const MinorClan& a, const MinorClan& b) { return a.id < b.id; });
}

// The census: HeroFactory's notable caps per settlement (source notables in uid order); a notable beyond them overflows to the house
// owning its settlement, then a wanderer, then a company. Every living FWGC++ hero ends in exactly one bucket.
void Pass::take_census() {
    std::map<std::string, std::string> owner_of;   // village or town -> owning house (a village through its bound fortification)
    for (const auto& t : c.towns) owner_of[t.id] = t.owner_clan_id;
    for (const auto& k : c.castles) owner_of[k.id] = k.owner_clan_id;
    for (const auto& v : c.villages) owner_of[v.id] = owner_of[v.bound_id];
    std::map<std::pair<std::string, std::string>, int> count;
    std::vector<std::size_t> notables;
    for (std::size_t i = 0; i < c.heroes.size(); ++i) if (c.heroes[i].bucket == "notable") notables.push_back(i);
    std::sort(notables.begin(), notables.end(), [&](std::size_t a, std::size_t b) { return c.heroes[a].uid < c.heroes[b].uid; });
    for (auto i : notables) {
        auto& h = c.heroes[i];
        const auto kind = pop.notable_caps.find(h.settlement_kind);
        int cap = 0;
        if (kind != pop.notable_caps.end() && kind->second.count(h.occupation)) cap = kind->second.at(h.occupation);
        auto& n = count[{h.settlement_id, h.occupation}];
        if (n < cap) { ++n; continue; }
        // Beyond the cap: the owning house (if it has room), else the nearest house of the kingdom with room.
        const std::string from = h.settlement_kind + " " + h.settlement_id + " " + h.occupation + " cap " + std::to_string(cap);
        House* best = nullptr; double bd = 1e300;
        const std::string owner = owner_of[h.settlement_id];
        std::string kingdom; for (const auto& x : c.houses) if (x.id == owner) kingdom = x.kingdom_id;
        for (auto& y : c.houses) {
            if (static_cast<int>(y.members.size()) >= pop.house_cap) continue;
            const double d = y.id == owner ? -1 : distance(h.settlement_id, y.home_id) + (y.kingdom_id == kingdom ? 0 : 1e9);
            if (d < bd) { bd = d; best = &y; }
        }
        if (best) {
            best->members.push_back(h.id);
            join(h, "house", best->id, best->kingdom_id);
            census.overflow.push_back({h.id, from, best->id, "notable beyond HeroFactory's cap joins a house"});
        } else {
            h.bucket = "wanderer"; h.occupation = "Wanderer"; h.affiliation = "none"; h.settlement_id.clear(); h.settlement_kind.clear();
            c.wanderers.push_back({h.id, h.presentation, names::strip_age_tag(h.epithet), h.female});
            census.overflow.push_back({h.id, from, "wanderer", "notable beyond HeroFactory's cap; every house is full"});
        }
        ++census.notables_bounded;
    }
    // Buckets and the unassigned count over the living FWGC++ heroes.
    std::map<std::string, std::set<std::string>> placed;
    for (const auto& x : c.houses) for (const auto& m : x.members) placed["house"].insert(m);
    for (const auto& x : c.companies) for (const auto& m : x.members) placed["company"].insert(m);
    for (const auto& x : c.outlaws) for (const auto& m : x.members) placed["outlaw"].insert(m);
    for (const auto& x : c.wanderers) placed["wanderer"].insert(x.hero);
    std::vector<std::string> unassigned;
    for (const auto& h : c.heroes) {
        if (!h.alive) { ++census.dead_parents; continue; }
        int in = 0;
        for (const auto& [bucket, set] : placed) if (set.count(h.id)) { ++in; if (bucket != h.bucket) in = 99; }
        if (h.bucket == "notable") in += (h.affiliation == "settlement" && !h.settlement_id.empty()) ? 1 : 0;
        if (in != 1) { unassigned.push_back(h.uid); continue; }
        if (h.synthesized) continue;
        ++census.source_heroes;
        ++census.buckets[h.bucket];
    }
    census.unassigned = static_cast<std::int64_t>(unassigned.size());
    if (!unassigned.empty()) refuse("population_unassigned", std::to_string(unassigned.size()) + " heroes are in no bucket or in two, first " + unassigned.front());
    // Invariants the policy sets (findings section 16): violations are internal errors, never a silent pass.
    std::set<std::string> lord_cultures(pop.lord_cultures.begin(), pop.lord_cultures.end());
    for (const auto& x : c.houses) {
        int adults = 0, minors = 0; bool male = false, female = false;
        for (const auto& m : x.members) {
            const auto& h = hero(m);
            if (!lord_cultures.count(h.presentation)) refuse("population_invalid", "Lord " + h.uid + " culture " + h.presentation + " is not a vanilla lord culture");
            if (h.age >= pop.adult_age_min) { ++adults; (h.female ? female : male) = true; } else ++minors;
        }
        if (x.fiefs.empty() || static_cast<int>(x.fiefs.size()) > pop.house_max_fiefs) refuse("population_invalid", "House " + x.id + " holds " + std::to_string(x.fiefs.size()) + " fiefs");
        if (static_cast<int>(x.members.size()) < pop.house_size_min || static_cast<int>(x.members.size()) > pop.house_cap || adults < pop.house_min_adults || !male || !female)
            refuse("population_invalid", "House " + x.id + " has " + std::to_string(x.members.size()) + " members, " + std::to_string(adults) + " adults" + (male && female ? "" : ", one sex"));
        census.adult_lords += adults; census.minors += minors;
        const std::int64_t children = std::max<std::int64_t>(0, (adults + 1) / 2 - minors);
        census.engine_children_by_house[x.id] = children; census.engine_children += children;
    }
    for (const auto& x : c.companies) {
        if (static_cast<int>(x.members.size()) < pop.company_members_min || static_cast<int>(x.members.size()) > pop.company_members_max) refuse("population_invalid", "Company " + x.id + " has " + std::to_string(x.members.size()) + " members");
        census.company_members += static_cast<std::int64_t>(x.members.size());
    }
    for (const auto& x : c.outlaws) census.outlaw_members += static_cast<std::int64_t>(x.members.size());
    for (const auto& t : c.towns) { const auto* x = house_of_fief(t.id); if (!x || x->id != t.owner_clan_id || x->kingdom_id != t.kingdom_id) refuse("population_invalid", "Town " + t.id + " is not owned by a house of its kingdom"); }
    for (const auto& k : c.castles) { const auto* x = house_of_fief(k.id); if (!x || x->id != k.owner_clan_id || x->kingdom_id != k.kingdom_id) refuse("population_invalid", "Castle " + k.id + " is not owned by a house of its kingdom"); }
    // Notables HeroFactory instantiates (every source notable now fits its cap).
    for (const auto& h : c.heroes) if (h.bucket == "notable") ++census.notables;
    census.fiefs = static_cast<std::int64_t>(c.towns.size() + c.castles.size());
    census.towns = static_cast<std::int64_t>(c.towns.size()); census.castles = static_cast<std::int64_t>(c.castles.size());
    census.kingdoms = static_cast<std::int64_t>(c.kingdoms.size()); census.houses = static_cast<std::int64_t>(c.houses.size());
    census.companies = static_cast<std::int64_t>(c.companies.size()); census.outlaws = static_cast<std::int64_t>(c.outlaws.size());
    census.wanderers = W; census.wanderers_planned = static_cast<std::int64_t>(c.wanderers.size());
    census.moved_notables = c.quota ? static_cast<std::int64_t>(c.quota->moved.size()) : 0;
    census.targets = {{"kingdoms", K}, {"houses", H}, {"adultLords", round_half_up(pop.adult_lords_per_house * H)}, {"mercenaryCompanies", M},
                      {"outlawClans", O}, {"wanderers", W}};
}

// MMR-281: the names the runtime wanderer tick gives the wanderers it makes (rotation and top-up), so they are FWGC++-style like the
// planned ones (RT-33): per presentation culture of the towns, population.json wanderers.reserve FWGC++ residents of the civilizations
// its towns present, in turn; each entry's sex is drawn at the residents' share of that civilization and the resident is of that sex;
// its given name is one no hero carries (a lexicon compound when the residents run out); its epithet is an archetypal FWGC++ epithet, one
// two or more of the world's people of the wanderer source roles carry (Age tag stripped), else none (the runtime adds the template's).
// Drawn after every other name.
void Pass::plan_reserve() {
    std::set<std::string> roles;
    for (const auto& s : pop.wanderer_sources) roles.insert(s == "domainHolder" ? "domain_holder" : s);
    std::map<std::string, int> carried;
    for (const auto& p : w.people) { const auto e = names::strip_age_tag(p.epithet); if (!e.empty() && roles.count(p.role)) ++carried[e]; }
    std::vector<std::string> epithets;
    for (const auto& [e, n] : carried) if (n >= 2) epithets.push_back(e);
    std::map<std::string, std::set<std::string>> civs;
    for (const auto& t : c.towns) if (!t.presentation.empty() && !t.civilization.empty()) civs[t.presentation].insert(t.civilization);
    for (const auto& [presentation, set] : civs) {
        const std::vector<std::string> list(set.begin(), set.end());
        for (int n = 0; n < pop.wanderer_reserve; ++n) {
            const std::string entity = "reserve:" + presentation + ":" + std::to_string(n);
            const std::string& civ = list[static_cast<std::size_t>(n) % list.size()];
            const auto f = pool.count({civ, true}) ? pool.at({civ, true}).size() : 0, m = pool.count({civ, false}) ? pool.at({civ, false}).size() : 0;
            const double share = f + m == 0 ? 0.5 : static_cast<double>(f) / static_cast<double>(f + m);
            const bool female = unit("wanderers", entity, 0) < share;
            const auto* r = resident(civ, female);
            campaign::ReserveName x;
            x.female = r ? r->female : female;
            // A small world can run out of names (no free resident, every lexicon compound taken): its reserve is shorter (noted).
            const std::size_t before = used_given.size();
            const std::string given = given_of(civ, x.female, entity, r);
            bool fresh = used_given.size() > before;
            if (fresh) {
                try { x.name = hero_names.take(names::person_name(heritage_of(civ).pattern, given, "", "", x.female), heritage_of(civ), value("wanderers", entity, 2)); }
                catch (const std::exception&) { fresh = false; }
            }
            if (!fresh) { census.notes.push_back("the wanderer reserve of " + presentation + " holds " + std::to_string(n) + " of " + std::to_string(pop.wanderer_reserve) + " names: the names ran out"); break; }
            x.epithet = epithets.empty() ? "" : epithets[value("wanderers", entity, 1) % epithets.size()];
            x.presentation = presentation; x.civilization = civ;
            c.wanderer_reserve.push_back(std::move(x));
        }
    }
}

void Pass::run() {
    if (c.towns.empty()) refuse("population_invalid", "The world has no town");
    const double F = static_cast<double>(c.towns.size() + c.castles.size()), base = static_cast<double>(pop.base_fiefs);
    K = std::max(pop.kingdoms_min, static_cast<int>(round_half_up(pop.kingdoms_per_base * F / base)));
    H = std::max(static_cast<int>(round_half_up(pop.houses_per_base * F / base)), std::min(pop.house_min_per_kingdom * K, static_cast<int>(F)));
    M = static_cast<int>(round_half_up(pop.companies_per_base * F / base));
    O = static_cast<int>(round_half_up(pop.outlaws_per_base * F / base));
    W = wanderer_target(pop.wanderers_per_town, static_cast<int>(c.towns.size()));
    for (const auto& h : c.heroes) { hero_names.reserve(h.name); if (!h.given.empty()) used_given.insert(names::strip_age_tag(h.given)); }
    facts();
    castle_trade();
    merge_kingdoms();
    plan_houses();
    place_house_members();
    size_houses();
    plan_companies_and_wanderers();
    plan_outlaws();
    plan_wars();
    name_houses();
    take_census();
    plan_reserve();
    // Sorted records and the id map with every new id.
    std::sort(c.heroes.begin(), c.heroes.end(), [](const Hero& a, const Hero& b) { return a.id < b.id; });
    for (auto& x : c.houses) std::sort(x.members.begin(), x.members.end());
    c.id_map = reg.entries();
    c.census = census;
}
}  // namespace

std::uint64_t stream_value(const std::string& stream, int version, const std::string& prefix, const std::string& entity, int draw) {
    const std::string hex = cjson::sha256_hex("mnbl.population-stream/1\n" + stream + "\n" + std::to_string(version) + "\n" + prefix + "\n" + entity + "\n" + std::to_string(draw));
    return std::stoull(hex.substr(0, 16), nullptr, 16);
}

int wanderer_target(double per_town, int towns) {
    const float desired = static_cast<float>(per_town) * static_cast<float>(towns);
    int w = 0;
    while (static_cast<float>(w) < desired) ++w;
    return w;
}

int bannerlord_age(const source::LifeSpan* span, std::int64_t age_years, const std::string& band, int lo, int hi) {
    if (!span || age_years < 0) return -1;
    // FWGC++ adults are at least 20 (rosters.json lords.minimumAge for rulers and clan leaders); synthesized kin clamp to houses.age (18..70).
    static const std::map<std::string, std::pair<int, int>> target{{"young", {20, 34}}, {"middle", {35, 49}}, {"old", {50, 70}}};
    std::string name = band;
    if (!span->bands.count(name) || !target.count(name)) {
        name.clear();
        for (const auto& [b, r] : span->bands) if (target.count(b) && age_years >= r.first && age_years <= r.second) name = b;
    }
    int out = 0;
    if (name.empty()) out = age_years < span->adult ? lo : hi;
    else {
        const auto [from, to] = span->bands.at(name);
        const auto [a, b] = target.at(name);
        const double t = to > from ? std::clamp(static_cast<double>(age_years - from) / static_cast<double>(to - from), 0.0, 1.0) : 0.0;
        out = static_cast<int>(std::lround(a + t * (b - a)));
    }
    return std::clamp(out, lo, hi);
}

Geometry geometry_of(const travel::Input& input, const travel::Result& result) {
    Geometry g;
    g.width = input.grid.cols * input.grid.spacing;
    for (const auto& s : input.settlements) g.point[s.id] = s.position;
    for (const auto& p : result.placements) {
        if (!p.cell.valid()) continue;
        g.point[p.id] = p.point;
        g.group[p.id] = p.group;
    }
    return g;
}

void plan(campaign::Compiled& compiled, const source::SourceWorld& world, const Geometry& geometry, const policy::Population& policy,
          const campaign::ContentPolicy& content, const campaign::CulturePolicy& cultures) {
    Pass(compiled, world, geometry, policy, content, cultures).run();
}

}  // namespace worldbridge::population
