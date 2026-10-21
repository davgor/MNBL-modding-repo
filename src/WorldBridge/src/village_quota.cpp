// The village quota (MMR-252; docs/travel-graph.md "Village quota", docs/research/map-epic-2-findings.md sections 9 and 17): exactly
// playability villages.townVillages villages per town and castleVillages per castle, inside travel::run after placement. compile() stays
// faithful to the source; this pass binds, reassigns, drops and synthesizes villages, keeps them clear of the fortifications, writes the
// campaign v2 binding and checks itself with a second analysis on the frozen pass-1 classes.
//   1 pin       each valid port's chosen harbour village stays with its town; a forced port (MMR-251) gets a synthesized harbour village
//               on a coastal land cell within kHarbourReach of its port cell (step 6), pinned too (RT-12)
//   2 towns     keep their own villages in the policy's preference order: the pinned one, at most maxExtraHarbours more harbour
//               villages, land villages of a production class the town does not hold yet, then the nearest (a fortification never holds
//               more than its pinned village plus maxExtraHarbours harbour villages)
//   3 castles   in turn (id order) take their nearest remaining village of their garrison town, within the reach
//   4 surplus   goes to short fortifications of its land group within the reach, its own kingdom first, then the nearest
//   5 drop      the rest is excluded ("quota: ..."; outside the travel exclusion bound); a presentation culture's last village of a protected
//               class (farming: horse ranches and wheat farms, RT-09) is swapped in for its town's farthest unprotected village instead
//   6 clearance kept villages closer than `clearance` to a fortification, or within minSeparation of another settlement, move to the
//               nearest point of their land component that is clear (RT-13); then short fortifications get synthesized villages on land
//               cells of their own land component, tier by tier (distances from the fortification as fractions of the town spacing S),
//               each the point whose distance to every other settlement is largest up to twice the clearance, then nearest the
//               fortification; uid "<uidPrefix><fortification uid>:<n>", the production class with the largest deficit of its
//               presentation culture against that culture's source land villages (RT-09), named from its fortification's
//               civilization's lexicon roots (MMR-286). Where no clear point exists (an islet), the
//               clearest one within the reach is used and recorded as an exception (isletExceptions); a castle with no point at all is
//               excluded (the last resort; a town always has its own cell)
//   7 notables  of dropped villages (reeves included) move to the least-settled final village of the dropped village's town (RT-11)
//   8 pass 2    the analysis again on the pass-1 classes with the final settlements: every settlement keeps its cell and point, every port
//               its pair, and nothing is excluded or rejected; otherwise "quota_unstable" (an internal refusal)
// The reach is the widest tier's maxSpacing x S. Candidate points are the 3 x 3 points a quarter cell apart inside a cell, which the
// emitted-point clamp keeps as they are. Everything runs in id order and is deterministic.
#include <algorithm>
#include <cmath>
#include <limits>
#include <map>
#include <set>
#include <string>
#include "worldbridge/travel_graph.hpp"

namespace worldbridge::travel {
namespace {
[[noreturn]] void refuse(const std::string& code, const std::string& message) { throw json::Error(code, message, json::Location{"", 0, 1, 1, "$"}); }
constexpr std::size_t npos = std::numeric_limits<std::size_t>::max();
constexpr double kHarbourReach = 2.0;   // cells: a harbour village stays within two cells of its harbour (port) cell

struct Fort {
    std::string id, uid, kind, kingdom, culture_uid, presentation, garrison, name;
    Point p;
    int land = -1, group = -1, quota = 0;
    std::vector<std::size_t> villages;   // indexes into the village list
    bool forced = false;                 // a forced port town still owed its synthesized harbour village
    Cell port_sea;
};
struct Vil {
    std::string id, uid, production, presentation, source_town, bound, tier;
    Point p;
    Cell cell;
    int land = -1, group = -1;
    std::size_t placement = npos;
    bool harbour = false, pinned = false, synthesized = false, dropped = false;
    Cell harbour_cell;   // a harbour village's harbour cell (its pinned port's sea cell for a synthesized one)
};

}  // namespace

double town_spacing(const std::vector<Point>& towns, double width) {
    if (towns.size() < 2) return 0;
    double sum = 0;
    for (std::size_t i = 0; i < towns.size(); ++i) {
        double nearest = std::numeric_limits<double>::max();
        for (std::size_t j = 0; j < towns.size(); ++j) {
            if (i == j) continue;
            double dx = std::abs(towns[i].x - towns[j].x);
            dx = std::min(dx, width - dx);
            nearest = std::min(nearest, std::hypot(dx, towns[i].y - towns[j].y));
        }
        sum += nearest;
    }
    return sum / static_cast<double>(towns.size());
}

void village_quota(campaign::Compiled& c, Input& in, Result& r, const Policy& policy, const campaign::ContentPolicy& content) {
    const auto& q = policy.villages;
    const Grid& g = in.grid;
    const Cells cells(g.cols, g.rows, g.spacing);
    const double width = g.cols * g.spacing;
    auto dist = [&](Point a, Point b) { double dx = std::abs(a.x - b.x); dx = std::min(dx, width - dx); return std::hypot(dx, a.y - b.y); };
    std::map<std::string, std::size_t> placement_of;
    for (std::size_t i = 0; i < r.placements.size(); ++i) placement_of[r.placements[i].id] = i;
    std::set<std::string> excluded;
    for (const auto& e : r.exclusions) excluded.insert(e.id);

    // Fortifications: every town, every kept castle with a cell.
    std::vector<Fort> forts;
    std::map<std::string, std::size_t> fort_of;
    std::map<std::string, std::string> presentation_of;   // culture uid -> presentation (towns first, then kingdoms; as HeroFactory resolves it)
    for (const auto& t : c.towns) if (!t.culture_uid.empty()) presentation_of.emplace(t.culture_uid, t.presentation);
    for (const auto& k : c.kingdoms) if (!k.culture_uid.empty()) presentation_of.emplace(k.culture_uid, k.presentation);
    std::map<std::string, const campaign::Town*> town_by_id;
    for (const auto& t : c.towns) town_by_id[t.id] = &t;
    std::map<std::string, const campaign::Kingdom*> kingdom_by_id;
    for (const auto& k : c.kingdoms) kingdom_by_id[k.id] = &k;
    for (const auto& t : c.towns) {
        const auto& pl = r.placements.at(placement_of.at(t.id));
        Fort f; f.id = t.id; f.uid = t.uid; f.kind = "town"; f.kingdom = t.kingdom_id; f.culture_uid = t.culture_uid; f.presentation = t.presentation; f.name = t.name;
        f.p = pl.point; f.land = pl.land; f.group = pl.group; f.quota = q.town;
        fort_of[f.id] = forts.size(); forts.push_back(f);
    }
    std::vector<Point> town_points;
    for (const auto& f : forts) town_points.push_back(f.p);
    const double spacing = town_spacing(town_points, width);
    if (!(spacing > 0)) refuse("quota_unstable", "The village quota needs a town spacing: the world has fewer than two placed towns");
    const double reach = q.tiers.back().max_spacing * spacing;
    // A town keeps its own villages only within the engine trade distance (kEngineTradeRatio x S, as the binding validates); a farther one
    // is surplus (seeds 45 and 52 of the 2026-10-19 sweep: a source village 131.5 and 240.7 units from its town, against 122.8 and 119.7).
    constexpr double kEngineTradeRatio = 3.0;
    const double trade = kEngineTradeRatio * spacing;
    for (const auto& k : c.castles) {
        const auto it = placement_of.find(k.id);
        if (it == placement_of.end() || !r.placements[it->second].cell.valid()) continue;
        const auto& pl = r.placements[it->second];
        Fort f; f.id = k.id; f.uid = k.uid; f.kind = "castle"; f.kingdom = k.kingdom_id; f.name = k.name; f.garrison = k.garrison_town_id;
        f.culture_uid = !k.culture_uid.empty() ? k.culture_uid : !k.garrison_town_id.empty() ? town_by_id.at(k.garrison_town_id)->culture_uid : kingdom_by_id.at(k.kingdom_id)->culture_uid;
        const auto pres = presentation_of.find(f.culture_uid);
        f.presentation = pres != presentation_of.end() ? pres->second : kingdom_by_id.at(k.kingdom_id)->presentation;
        f.p = pl.point; f.land = pl.land; f.group = pl.group; f.quota = q.castle;
        fort_of[f.id] = forts.size(); forts.push_back(f);
    }
    // The town a castle's synthesized villages take their hearth share from: its garrison town, else the nearest town of its kingdom
    // (else the nearest town).
    auto source_town_of = [&](const Fort& f) {
        if (f.kind == "town") return f.id;
        if (!f.garrison.empty()) return f.garrison;
        std::string best;
        double bd = 0;
        for (int pass = 0; pass < 2 && best.empty(); ++pass)
            for (const auto& t : forts)
                if (t.kind == "town" && (pass == 1 || t.kingdom == f.kingdom) && (best.empty() || dist(t.p, f.p) < bd)) { best = t.id; bd = dist(t.p, f.p); }
        return best;
    };

    // Villages: every kept village of the records, at its placement.
    std::map<std::string, Cell> harbour_cell_of;
    for (const auto& port : r.ports)
        for (const auto& cand : port.candidates) if (!cand.village_id.empty() && cand.harbour_cell.valid()) harbour_cell_of[cand.village_id] = cand.harbour_cell;
    std::vector<Vil> vils;
    std::map<std::string, std::size_t> vil_of;
    for (const auto& v : c.villages) {
        const auto& pl = r.placements.at(placement_of.at(v.id));
        Vil x; x.id = v.id; x.uid = v.uid; x.production = v.production; x.source_town = v.source_town; x.harbour = v.harbour;
        const auto pres = presentation_of.find(v.culture_uid);
        x.presentation = pres != presentation_of.end() ? pres->second : town_by_id.at(v.source_town)->presentation;
        x.p = pl.point; x.cell = pl.cell; x.land = pl.land; x.group = pl.group; x.placement = placement_of.at(v.id);
        if (const auto h = harbour_cell_of.find(v.id); h != harbour_cell_of.end() && x.harbour) x.harbour_cell = h->second;
        vil_of[x.id] = vils.size(); vils.push_back(x);
    }
    auto assign = [&](std::size_t vi, std::size_t fi) { vils[vi].bound = forts[fi].id; forts[fi].villages.push_back(vi); };
    auto unassign = [&](std::size_t vi) {
        auto& list = forts[fort_of.at(vils[vi].bound)].villages;
        list.erase(std::remove(list.begin(), list.end(), vi), list.end());
        vils[vi].bound.clear();
    };
    auto harbours_of = [&](const Fort& f) { int n = 0; for (auto vi : f.villages) n += vils[vi].harbour; return n; };
    auto harbour_room = [&](const Fort& f) {
        const int pinned = std::any_of(f.villages.begin(), f.villages.end(), [&](std::size_t vi) { return vils[vi].pinned; }) || f.forced ? 1 : 0;
        return harbours_of(f) < pinned + q.max_extra_harbours;
    };
    auto count_of = [&](const Fort& f) { return static_cast<int>(f.villages.size()) + (f.forced ? 1 : 0); };

    // 1. Pins.
    for (const auto& port : r.ports) {
        if (!port.valid) continue;
        auto& f = forts[fort_of.at(port.town_id)];
        if (port.forced) { f.forced = true; f.port_sea = port.chosen.sea_cell; continue; }
        const auto it = vil_of.find(port.chosen.village_id);
        if (it == vil_of.end()) refuse("quota_unstable", "Port " + port.town_uid + " chose harbour village " + port.chosen.village_uid + ", which is not a kept village");
        vils[it->second].pinned = true;
        assign(it->second, fort_of.at(port.town_id));
    }
    // 2. Towns keep their own villages in the preference order.
    std::map<std::string, std::vector<std::size_t>> own;   // town id -> its compiler villages, nearest first
    for (std::size_t vi = 0; vi < vils.size(); ++vi)
        if (dist(vils[vi].p, forts[fort_of.at(vils[vi].source_town)].p) <= trade) own[vils[vi].source_town].push_back(vi);
    for (auto& [town, list] : own) {
        const Point tp = forts[fort_of.at(town)].p;
        std::sort(list.begin(), list.end(), [&](std::size_t a, std::size_t b) { const double da = dist(vils[a].p, tp), db = dist(vils[b].p, tp); return da != db ? da < db : vils[a].id < vils[b].id; });
    }
    for (auto& f : forts) {
        if (f.kind != "town") continue;
        const auto& list = own[f.id];
        for (const auto& step : q.preference) {
            if (step == "pinned") continue;   // step 1
            if (step == "harbour") {
                for (auto vi : list) if (count_of(f) < f.quota && vils[vi].bound.empty() && vils[vi].harbour && harbour_room(f)) { assign(vi, fort_of.at(f.id)); break; }
            } else if (step == "diverse") {
                for (auto vi : list) {
                    if (count_of(f) >= f.quota || !vils[vi].bound.empty() || vils[vi].harbour) continue;
                    const bool held = std::any_of(f.villages.begin(), f.villages.end(), [&](std::size_t w) { return !vils[w].harbour && vils[w].production == vils[vi].production; });
                    if (!held) assign(vi, fort_of.at(f.id));
                }
            } else {   // nearest
                for (auto vi : list) if (count_of(f) < f.quota && vils[vi].bound.empty() && (!vils[vi].harbour || harbour_room(f))) assign(vi, fort_of.at(f.id));
            }
        }
    }
    // 3. Castles take their garrison town's nearest remaining villages, in turn.
    for (bool progress = true; progress;) {
        progress = false;
        for (std::size_t fi = 0; fi < forts.size(); ++fi) {
            auto& f = forts[fi];
            if (f.kind != "castle" || f.garrison.empty() || count_of(f) >= f.quota) continue;
            std::size_t best = npos;
            for (auto vi : own[f.garrison]) {
                const auto& v = vils[vi];
                if (!v.bound.empty() || v.group != f.group || dist(v.p, f.p) > reach || (v.harbour && !harbour_room(f))) continue;
                if (best == npos || dist(v.p, f.p) < dist(vils[best].p, f.p) || (dist(v.p, f.p) == dist(vils[best].p, f.p) && v.id < vils[best].id)) best = vi;
            }
            if (best != npos) { assign(best, fi); progress = true; }
        }
    }
    // 4. Surplus to short fortifications of its land group, its own kingdom first, then the nearest.
    {
        struct Pair { bool other; double d; std::size_t vi, fi; };
        std::vector<Pair> pairs;
        for (std::size_t vi = 0; vi < vils.size(); ++vi) {
            if (!vils[vi].bound.empty()) continue;
            const std::string& kingdom = forts[fort_of.at(vils[vi].source_town)].kingdom;
            for (std::size_t fi = 0; fi < forts.size(); ++fi)
                if (forts[fi].group == vils[vi].group && count_of(forts[fi]) < forts[fi].quota && dist(vils[vi].p, forts[fi].p) <= reach)
                    pairs.push_back({forts[fi].kingdom != kingdom, dist(vils[vi].p, forts[fi].p), vi, fi});
        }
        std::sort(pairs.begin(), pairs.end(), [&](const Pair& a, const Pair& b) {
            if (a.other != b.other) return !a.other;
            if (a.d != b.d) return a.d < b.d;
            if (vils[a.vi].id != vils[b.vi].id) return vils[a.vi].id < vils[b.vi].id;
            return forts[a.fi].id < forts[b.fi].id;
        });
        for (const auto& p : pairs)
            if (vils[p.vi].bound.empty() && count_of(forts[p.fi]) < forts[p.fi].quota && (!vils[p.vi].harbour || harbour_room(forts[p.fi]))) assign(p.vi, p.fi);
    }
    // 5. A presentation culture's last village of a protected class is never dropped: it takes its town's place of the farthest unprotected
    //    (or still-redundant) village, which drops instead.
    campaign::VillageQuota quota;
    quota.town = q.town; quota.castle = q.castle; quota.spacing = spacing;
    for (const auto& cls : q.protected_classes) {
        std::set<std::string> cultures;
        for (const auto& v : vils) if (!v.harbour && v.production == cls) cultures.insert(v.presentation);
        for (const auto& culture : cultures) {
            auto kept = [&](const std::string& pres) { int n = 0; for (const auto& v : vils) n += !v.bound.empty() && !v.harbour && v.production == cls && v.presentation == pres; return n; };
            if (kept(culture) > 0) continue;
            // The culture's dropped village of the class nearest its own town.
            std::size_t pick = npos;
            double pick_d = 0;
            for (std::size_t vi = 0; vi < vils.size(); ++vi) {
                const auto& v = vils[vi];
                if (!v.bound.empty() || v.harbour || v.production != cls || v.presentation != culture) continue;
                const double d = dist(v.p, forts[fort_of.at(v.source_town)].p);
                if (d > trade) continue;
                if (pick == npos || d < pick_d || (d == pick_d && v.id < vils[pick].id)) { pick = vi; pick_d = d; }
            }
            if (pick == npos) continue;
            auto& f = forts[fort_of.at(vils[pick].source_town)];
            std::size_t out = npos;
            for (auto vi : f.villages) {
                const auto& v = vils[vi];
                if (v.pinned) continue;
                const bool needed = !v.harbour && std::find(q.protected_classes.begin(), q.protected_classes.end(), v.production) != q.protected_classes.end() && kept(v.presentation) <= 1;
                if (needed) continue;
                if (out == npos || dist(v.p, f.p) > dist(vils[out].p, f.p) || (dist(v.p, f.p) == dist(vils[out].p, f.p) && v.id > vils[out].id)) out = vi;
            }
            if (count_of(f) >= f.quota) { if (out == npos) continue; unassign(out); }
            assign(pick, fort_of.at(f.id));
            ++quota.protected_kept;
        }
    }
    for (auto& v : vils) v.dropped = v.bound.empty();

    // 6. Clearance of the kept villages, then synthesis.
    std::vector<std::pair<std::string, Point>> others;   // every final settlement point, by id
    auto rebuild_others = [&]() {
        others.clear();
        for (const auto& f : forts) others.push_back({f.id, f.p});
        for (const auto& v : vils) if (!v.dropped) others.push_back({v.id, v.p});
    };
    auto fort_clearance = [&](Point p, std::string* nearest = nullptr) {
        double best = std::numeric_limits<double>::max();
        for (const auto& f : forts) { const double d = dist(p, f.p); if (d < best) { best = d; if (nearest) *nearest = f.id; } }
        return best;
    };
    auto separation = [&](Point p, const std::string& self) {
        double best = std::numeric_limits<double>::max();
        for (const auto& [id, o] : others) if (id != self) best = std::min(best, dist(p, o));
        return best;
    };
    struct Candidate { Point p; Cell cell; };
    auto candidates = [&](int land, Point centre, double radius, Cell harbour) {
        std::vector<Candidate> out;
        const double s = g.spacing;
        const int r0 = std::max(0, static_cast<int>(std::floor((centre.y - radius) / s))), r1 = std::min(g.rows - 1, static_cast<int>(std::floor((centre.y + radius) / s)));
        const int c0 = static_cast<int>(std::floor((centre.x - radius) / s)), c1 = static_cast<int>(std::floor((centre.x + radius) / s));
        std::set<std::size_t> seen;
        const Point hp = harbour.valid() ? cells.centre(harbour) : Point{};
        for (int row = r0; row <= r1; ++row)
            for (int col = c0; col <= c1; ++col) {
                const Cell cell = cells.wrap(row, col);
                const std::size_t k = cells.index(cell);
                if (!seen.insert(k).second || r.cls[k] != Land || r.land_of[k] != land) continue;
                for (double fy : {0.25, 0.5, 0.75})
                    for (double fx : {0.25, 0.5, 0.75}) {
                        const Point p{(cell.col + fx) * s, (cell.row + fy) * s};
                        if (dist(p, centre) > radius) continue;
                        if (harbour.valid() && dist(p, hp) > kHarbourReach * s) continue;
                        out.push_back({p, cell});
                    }
            }
        return out;
    };
    auto better = [](double a, double b) { return a > b + 1e-12; };
    const double cap = 2 * q.clearance;
    // The clearest candidate (an islet exception): separated where possible, then farthest from every fortification, then cell and point order.
    auto clearest = [&](const std::vector<Candidate>& list, const std::string& self, std::size_t& pick) {
        double best_fort = -1; bool best_sep = false;
        for (std::size_t i = 0; i < list.size(); ++i) {
            const bool sep = separation(list[i].p, self) >= q.min_separation;
            const double fc = fort_clearance(list[i].p);
            if (pick == npos || (sep && !best_sep) || (sep == best_sep && better(fc, best_fort))) { pick = i; best_fort = fc; best_sep = sep; }
        }
    };
    rebuild_others();
    for (std::size_t vi = 0; vi < vils.size(); ++vi) {
        auto& v = vils[vi];
        if (v.dropped || (fort_clearance(v.p) >= q.clearance && separation(v.p, v.id) >= q.min_separation)) continue;
        const auto list = candidates(v.land, v.p, reach, v.harbour_cell);
        std::size_t pick = npos;
        for (std::size_t i = 0; i < list.size(); ++i) {
            if (fort_clearance(list[i].p) < q.clearance || separation(list[i].p, v.id) < q.min_separation) continue;
            if (pick == npos || dist(list[i].p, v.p) < dist(list[pick].p, v.p) - 1e-12) pick = i;
        }
        bool exception = false;
        if (pick == npos) {
            if (!q.islet_exceptions) refuse("quota_unstable", "Village " + v.uid + " has no point clear of the fortifications and isletExceptions is off");
            clearest(list, v.id, pick);
            exception = true;
        }
        if (pick == npos) continue;   // no land point at all: it stays (its own cell is always a candidate, so this does not happen)
        const Cell from = v.cell;
        const double moved = dist(list[pick].p, v.p) / g.spacing;
        if (moved > 0) {
            v.p = list[pick].p; v.cell = list[pick].cell;
            r.repairs.push_back({"clearance", v.uid, exception ? "the village is closer than the clearance to a fortification and no clear point exists within reach (islet exception); it moves to the clearest point"
                                                               : "the village is closer than the clearance to a fortification, or shares a point; it moves to the nearest clear point of its land component",
                                 v.cell, from, moved});
            ++quota.clearance_moves;
            rebuild_others();
        }
        if (exception) {
            std::string nearest;
            const double d = fort_clearance(v.p, &nearest);
            if (d < q.clearance) quota.exceptions.push_back({v.id, nearest, "islet: no point of the village's land component within reach is clear of the fortifications", d});
        }
    }
    // Synthesis: the production class with the largest deficit of the fortification's presentation culture.
    std::map<std::string, std::map<std::string, int>> source_land;   // presentation -> land class -> source villages
    std::map<std::string, int> world_land;
    std::set<std::string> land_classes;
    for (const auto& [role, rule] : content.hamlet_roles) if (!rule.harbour) land_classes.insert(rule.production);
    for (const auto& v : vils) if (!v.harbour && land_classes.count(v.production)) { ++source_land[v.presentation][v.production]; ++world_land[v.production]; }
    auto role_of = [&](const std::string& production, bool harbour) -> std::pair<std::string, const campaign::RoleRule*> {
        for (const auto& [role, rule] : content.hamlet_roles) if (rule.production == production && rule.harbour == harbour) return {role, &rule};
        for (const auto& [role, rule] : content.hamlet_roles) if (rule.harbour == harbour) return {role, &rule};
        refuse("quota_unstable", std::string("The content policy has no ") + (harbour ? "harbour" : "land") + " hamlet role for a synthesized village");
    };
    auto deficit_class = [&](const std::string& presentation) {
        const auto& target = source_land.count(presentation) && !source_land.at(presentation).empty() ? source_land.at(presentation) : world_land;
        int total = 0; for (const auto& [k, n] : target) total += n;
        std::map<std::string, int> have;
        int held = 0;
        for (const auto& v : vils) if (!v.dropped && !v.harbour && v.presentation == presentation && land_classes.count(v.production)) { ++have[v.production]; ++held; }
        std::string best;
        double best_deficit = 0;
        for (const auto& cls : land_classes) {
            const double share = total > 0 && target.count(cls) ? target.at(cls) / static_cast<double>(total) : 0;
            const double deficit = share * (held + 1) - (have.count(cls) ? have.at(cls) : 0);
            const bool prot = std::find(q.protected_classes.begin(), q.protected_classes.end(), cls) != q.protected_classes.end();
            const bool best_prot = std::find(q.protected_classes.begin(), q.protected_classes.end(), best) != q.protected_classes.end();
            if (best.empty() || better(deficit, best_deficit) || (std::abs(deficit - best_deficit) <= 1e-12 && prot && !best_prot)) { best = cls; best_deficit = deficit; }
        }
        return best;
    };
    // Names (MMR-286): coined from the fortification's civilization's lexicon roots for the role's concepts, unique among every
    // settlement name (the compiler's), never a numeral.
    names::Unique place_names(content.cleaner);
    for (const auto& x : c.towns) place_names.reserve(x.name);
    for (const auto& x : c.villages) place_names.reserve(x.name);
    for (const auto& x : c.castles) place_names.reserve(x.name);
    std::map<std::string, std::pair<std::string, std::string>> naming_of;   // fortification -> (culture uid, civilization)
    for (const auto& t : c.towns) naming_of[t.id] = {t.culture_uid, t.civilization};
    for (const auto& k : c.castles) {
        std::string civ;
        for (const auto& t : c.towns) if (t.id == k.garrison_town_id) civ = t.civilization;
        naming_of[k.id] = {k.culture_uid, civ};
    }
    // Ids: the compiler's registry, rebuilt from the id map, so a synthesized uid can never collide with a source id.
    const std::size_t hex = c.id_map.empty() ? 10 : c.id_map.front().id.size() - c.prefix.size() - 3;
    ids::Registry reg(c.prefix, static_cast<int>(hex));
    for (const auto& e : c.id_map) reg.assign(e.kind, e.uid);
    std::set<std::string> castles_out;
    std::map<std::string, int> serial;
    for (int pass = 0; pass < 2; ++pass)   // towns first, then castles
        for (std::size_t fi = 0; fi < forts.size(); ++fi) {
            auto& f = forts[fi];
            if ((pass == 0) != (f.kind == "town")) continue;
            while (count_of(f) < f.quota || f.forced) {   // a forced port town owes its harbour village even when its reserved slot fills it
                const bool harbour = f.forced;
                const Cell harbour_cell = harbour ? f.port_sea : Cell{};
                const std::string uid = q.uid_prefix + f.uid + ":" + std::to_string(++serial[f.id]);
                std::vector<Candidate> list = candidates(f.land, f.p, reach, harbour_cell);
                std::size_t pick = npos;
                std::string tier;
                for (const auto& t : q.tiers) {
                    double best_score = -1, best_d = 0;
                    for (std::size_t i = 0; i < list.size(); ++i) {
                        const double d = dist(list[i].p, f.p);
                        if (d < t.min_spacing * spacing || d > t.max_spacing * spacing || fort_clearance(list[i].p) < q.clearance) continue;
                        const double sep = separation(list[i].p, uid);
                        if (sep < q.min_separation) continue;
                        const double score = std::min(sep, cap);
                        if (pick == npos || better(score, best_score) || (std::abs(score - best_score) <= 1e-12 && d < best_d - 1e-12)) { pick = i; best_score = score; best_d = d; }
                    }
                    if (pick != npos) { tier = t.name; break; }
                }
                if (pick == npos && q.islet_exceptions) { clearest(list, uid, pick); if (pick != npos) tier = "islet"; }
                if (pick == npos) {
                    if (f.kind == "town") refuse("quota_unstable", "Town " + f.uid + " has no land point for a village (its own cell is always one)");
                    castles_out.insert(f.id);   // the last resort: the castle is excluded
                    break;
                }
                // The new village: a land village of the largest-deficit class, or the forced port's harbour village.
                const std::string production = harbour ? role_of("harbor", true).second->production : deficit_class(f.presentation);
                const auto [role, rule] = role_of(production, harbour);
                campaign::Village nv;
                nv.uid = uid; nv.id = reg.assign("village", uid); nv.role = role; nv.production = rule->production; nv.harbour = harbour;
                nv.culture_uid = f.culture_uid; nv.bound_id = f.id; nv.bound_kind = f.kind; nv.source_town = source_town_of(f); nv.origin = "synthesized"; nv.tier = tier; nv.pinned = harbour;
                const auto& who = naming_of[f.id];
                nv.name = place_names.coin(c.naming.of(who.first, who.second), rule->concepts, names::salt_of(uid));
                nv.name_provenance = "synthesized: the village quota (MMR-252) for " + f.kind + " " + f.uid + "; FWGC++ lexicon roots of the role's concepts";
                nv.slots = {{"villageType", "pending", "MMR-33", nv.production}, {"hearth", "pending", "MMR-33", ""}, {"position", "pending", "MMR-28", ""}};
                c.villages.push_back(nv);
                Vil x; x.id = nv.id; x.uid = uid; x.production = nv.production; x.presentation = f.presentation; x.source_town = nv.source_town; x.harbour = harbour; x.pinned = harbour;
                x.synthesized = true; x.tier = tier; x.p = list[pick].p; x.cell = list[pick].cell; x.land = f.land; x.group = f.group; x.harbour_cell = harbour_cell;
                vil_of[x.id] = vils.size(); vils.push_back(x);
                if (harbour) f.forced = false;
                assign(vils.size() - 1, fi);
                others.push_back({x.id, x.p});
                ++quota.synthesized; ++quota.tiers[tier];
                std::string nearest;
                const double d = fort_clearance(x.p, &nearest);
                if (d < q.clearance) quota.exceptions.push_back({x.id, nearest, "islet: no point of the " + f.kind + "'s land component within reach is clear of the fortifications", d});
            }
        }
    // A castle the quota cannot fill gives up its villages (dropped) and is excluded.
    for (const auto& id : castles_out) {
        auto& f = forts[fort_of.at(id)];
        for (auto vi : std::vector<std::size_t>(f.villages)) { unassign(vi); vils[vi].dropped = true; }
    }
    for (const auto& f : forts)
        if (!castles_out.count(f.id) && (f.forced || static_cast<int>(f.villages.size()) != f.quota))
            refuse("quota_unstable", "The village quota left " + f.kind + " " + f.uid + " with " + std::to_string(f.villages.size()) + " villages, not " + std::to_string(f.quota));

    // Campaign records: binding, origin, source town and pins; dropped villages' notables move, then the villages are excluded.
    for (auto& t : c.towns) t.villages.clear();
    for (auto& k : c.castles) k.villages.clear();
    std::map<std::string, campaign::Village*> record;
    for (auto& v : c.villages) record[v.id] = &v;
    for (auto& v : vils) {
        auto& rec = *record.at(v.id);
        if (v.dropped) { rec.bound_id = v.source_town; rec.bound_kind = "town"; continue; }
        rec.bound_id = v.bound; rec.bound_kind = forts[fort_of.at(v.bound)].kind; rec.pinned = v.pinned;
        if (!v.synthesized) rec.origin = v.bound == v.source_town ? "source" : "reassigned";
        if (!v.synthesized) ++(rec.origin == "source" ? quota.kept : quota.reassigned);
    }
    for (auto& v : c.villages) {
        if (vils[vil_of.at(v.id)].dropped) continue;
        if (v.bound_kind == "town") { for (auto& t : c.towns) if (t.id == v.bound_id) t.villages.push_back(v.id); }
        else for (auto& k : c.castles) if (k.id == v.bound_id) k.villages.push_back(v.id);
    }
    for (auto& t : c.towns) std::sort(t.villages.begin(), t.villages.end());
    for (auto& k : c.castles) std::sort(k.villages.begin(), k.villages.end());
    // A dropped village still lists under its town until it is excluded (closure holds in between).
    for (const auto& v : vils) if (v.dropped) for (auto& t : c.towns) if (t.id == v.source_town) t.villages.push_back(v.id);
    std::map<std::string, int> settled;   // final village -> heroes settled there
    for (const auto& h : c.heroes) if (h.settlement_kind == "village") ++settled[h.settlement_id];
    for (const auto& v : vils) {
        if (!v.dropped) continue;
        const auto& town = forts[fort_of.at(v.source_town)];
        for (auto& h : c.heroes) {
            if (h.settlement_kind != "village" || h.settlement_id != v.id) continue;
            std::size_t to = npos;
            for (auto wi : town.villages) {
                const auto& w = vils[wi];
                const int a = settled[w.id], b = to == npos ? 0 : settled[vils[to].id];
                if (to == npos || a < b || (a == b && (dist(w.p, v.p) < dist(vils[to].p, v.p) || (dist(w.p, v.p) == dist(vils[to].p, v.p) && w.id < vils[to].id)))) to = wi;
            }
            if (to == npos) continue;   // the town has no final village (cannot happen: every town is filled); exclude() moves it to the town
            h.settlement_id = vils[to].id;
            h.provenance += "; village " + v.id + " dropped by the village quota (MMR-252): moved to " + vils[to].id + " of the same town";
            ++settled[vils[to].id];
            quota.moved.push_back({h.id, v.id, vils[to].id});
        }
    }
    for (const auto& v : vils) {
        if (!v.dropped) continue;
        const std::string reason = "quota: beyond the " + std::to_string(q.town) + "/" + std::to_string(q.castle) + " binding (MMR-252)";
        campaign::exclude(c, v.id, reason);
        ++quota.dropped;
        r.exclusions.push_back({v.id, v.uid, "village", reason});
    }
    for (const auto& id : castles_out) {
        campaign::exclude(c, id, "quota: no land point for its villages (MMR-252, the last resort)");
        r.exclusions.push_back({id, forts[fort_of.at(id)].uid, "castle", "quota: no land point for its villages (MMR-252, the last resort)"});
    }
    // Port records: the harbour villages bound to their town, the pinned one first; a forced port's synthesized harbour village.
    std::map<std::string, const campaign::Village*> village_by_id;
    for (const auto& v : c.villages) village_by_id[v.id] = &v;
    for (auto& p : c.ports) {
        std::vector<std::string> list;
        for (const auto& v : c.villages) if (v.harbour && v.bound_id == p.town_id && v.bound_kind == "town") list.push_back(v.id);
        std::stable_sort(list.begin(), list.end(), [&](const std::string& a, const std::string& b) { return village_by_id.at(a)->pinned && !village_by_id.at(b)->pinned; });
        p.village_ids = list; p.sea_nodes.clear(); p.best_quality = 0;
        for (const auto& id : list) {
            const auto* v = village_by_id.at(id);
            if (v->sea_node) p.sea_nodes.push_back(*v->sea_node);
            p.best_quality = std::max(p.best_quality, v->harbour_quality);
        }
    }
    for (const auto& v : c.villages)
        if (v.origin == "synthesized" && std::none_of(c.id_map.begin(), c.id_map.end(), [&](const ids::Entry& e) { return e.id == v.id; })) c.id_map.push_back({v.id, "village", v.uid});
    std::sort(c.id_map.begin(), c.id_map.end(), [](const ids::Entry& x, const ids::Entry& y) { return x.id < y.id; });

    // Travel records: positions, synthesized placements, bindings and the settlement host checks.
    for (const auto& v : vils) {
        if (v.synthesized) {
            Placement pl; pl.id = v.id; pl.uid = v.uid; pl.kind = "village"; pl.method = "synthesized"; pl.cell = v.cell; pl.point = v.p; pl.land = v.land; pl.group = v.group;
            r.placements.push_back(pl);
            in.settlements.push_back({v.id, v.uid, "village", v.dropped ? v.source_town : v.bound, v.p});
            r.lands[static_cast<std::size_t>(v.land)].villages.push_back(v.id);
            continue;
        }
        auto& pl = r.placements[v.placement];
        if (!(pl.cell == v.cell) || pl.point.x != v.p.x || pl.point.y != v.p.y) { pl.cell = v.cell; pl.point = v.p; pl.shift = cells.distance(in.settlements[v.placement].position, v.cell); }
        if (!v.dropped) in.settlements[v.placement].bound_town_id = v.bound;
    }
    for (auto& port : r.ports) {
        if (!port.valid || !port.forced) continue;
        for (const auto& v : vils)
            if (v.synthesized && v.harbour && v.bound == port.town_id) {
                port.chosen.village_id = v.id; port.chosen.village_uid = v.uid; port.chosen.harbour_cell = port.chosen.sea_cell;
                for (auto& cand : port.candidates) if (cand.kind == "coast" && cand.status == "valid") { cand.village_id = v.id; cand.village_uid = v.uid; cand.harbour_cell = cand.sea_cell; }
            }
    }
    settlement_checks(in, r, policy);

    // 8. Pass 2: the analysis on the frozen pass-1 classes with the final settlements.
    {
        std::set<std::string> gone;
        for (const auto& e : r.exclusions) gone.insert(e.id);
        Input two;
        two.grid = in.grid; two.biome_core = in.biome_core; two.roads = in.roads;
        for (std::size_t i = 0; i < r.placements.size(); ++i)
            if (!gone.count(r.placements[i].id) && r.placements[i].cell.valid()) two.settlements.push_back({in.settlements[i].id, in.settlements[i].uid, in.settlements[i].kind, in.settlements[i].bound_town_id, r.placements[i].point});
        for (const auto& h : in.harbours) {
            const auto it = vil_of.find(h.village_id);
            if (it != vil_of.end() && !vils[it->second].dropped && vils[it->second].bound == h.town_id) two.harbours.push_back(h);
        }
        const Result r2 = analyze_cells(two, policy, r.cls);
        std::vector<std::string> problems;
        if (r2.status != "ok") problems.push_back("pass 2 is rejected (" + r2.reasons.front().code + ": " + r2.reasons.front().message + ")");
        for (const auto& e : r2.exclusions) problems.push_back("pass 2 excludes " + e.uid + " (" + e.reason + ")");
        std::map<std::string, const Placement*> first;
        for (const auto& pl : r.placements) first[pl.id] = &pl;
        for (const auto& pl : r2.placements) {
            const auto* a = first.at(pl.id);
            if (!(a->cell == pl.cell) || a->point.x != pl.point.x || a->point.y != pl.point.y) problems.push_back("pass 2 moves " + pl.uid);
        }
        std::map<std::string, const Port*> ports_one;
        for (const auto& p : r.ports) if (p.valid) ports_one[p.town_id] = &p;
        std::size_t valid_two = 0;
        for (const auto& p : r2.ports) {
            if (!p.valid) continue;
            ++valid_two;
            const auto it = ports_one.find(p.town_id);
            if (it == ports_one.end() || it->second->forced != p.forced || !(it->second->chosen.land_cell == p.chosen.land_cell) || !(it->second->chosen.sea_cell == p.chosen.sea_cell))
                problems.push_back("pass 2 changes the port of " + p.town_uid);
        }
        if (valid_two != ports_one.size()) problems.push_back("pass 2 holds " + std::to_string(valid_two) + " valid ports, pass 1 " + std::to_string(ports_one.size()));
        if (!problems.empty()) refuse("quota_unstable", "The village quota's second analysis differs: " + problems.front() + (problems.size() > 1 ? " (+" + std::to_string(problems.size() - 1) + " more)" : ""));
        quota.passes = 2; quota.stable = true;
    }
    c.quota = quota;
}

}  // namespace worldbridge::travel
