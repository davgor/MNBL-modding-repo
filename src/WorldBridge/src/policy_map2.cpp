#include "worldbridge/policy_map2.hpp"
#include <algorithm>
#include <cmath>
#include <cstdlib>
#include <set>
#include <tuple>
#include "worldbridge/canonical_json.hpp"

namespace worldbridge::policy {
namespace {
using cjson::Value;
using json::Error;

void only(const Value& object, std::initializer_list<const char*> keys, const std::string& what) {
    if (object.kind != Value::Kind::Object) throw Error("wrong_type", what + " must be an object", object.at);
    const std::set<std::string> allowed(keys.begin(), keys.end());
    for (const auto& m : object.members)
        if (!allowed.count(m.first)) throw Error("unknown_field", what + " has unknown member '" + m.first + "'", m.second.at);
}
const Value& need(const Value& object, const char* key, Value::Kind kind, const std::string& what) {
    const Value* v = object.find(key);
    if (!v) throw Error("missing_field", what + " lacks '" + key + "'", object.at);
    if (v->kind != kind) throw Error("wrong_type", what + " '" + key + "' has the wrong type", v->at);
    return *v;
}
const Value& obj(const Value& o, const char* key, const std::string& what) { return need(o, key, Value::Kind::Object, what); }
double real(const Value& o, const char* key, const std::string& what) {
    const double d = std::strtod(need(o, key, Value::Kind::Number, what).text.c_str(), nullptr);
    if (!std::isfinite(d)) throw Error("nonfinite_number", what + " '" + key + "' is not finite", o.find(key)->at);
    return d;
}
int whole(const Value& o, const char* key, const std::string& what) {
    const Value& v = need(o, key, Value::Kind::Number, what);
    if (v.text.find_first_not_of("-0123456789") != std::string::npos) throw Error("wrong_type", what + " '" + key + "' must be an integer", v.at);
    return std::atoi(v.text.c_str());
}
bool flag(const Value& o, const char* key, const std::string& what) { return need(o, key, Value::Kind::Bool, what).boolean; }
const std::string& text(const Value& o, const char* key, const std::string& what) {
    const Value& v = need(o, key, Value::Kind::String, what);
    if (v.text.empty()) throw Error("missing_field", what + " '" + key + "' is empty", v.at);
    return v.text;
}
std::vector<std::string> strings(const Value& o, const char* key, const std::string& what, bool allow_empty = false) {
    std::vector<std::string> out;
    for (const auto& v : need(o, key, Value::Kind::Array, what).items) {
        if (v.kind != Value::Kind::String || v.text.empty()) throw Error("wrong_type", what + " '" + key + "' must hold non-empty strings", v.at);
        if (std::find(out.begin(), out.end(), v.text) != out.end()) throw Error("duplicate_id", what + " '" + key + "' repeats '" + v.text + "'", v.at);
        out.push_back(v.text);
    }
    if (out.empty() && !allow_empty) throw Error("missing_field", what + " '" + key + "' is empty", o.at);
    return out;
}
std::vector<int> ints(const Value& o, const char* key, const std::string& what) {
    std::vector<int> out;
    for (const auto& v : need(o, key, Value::Kind::Array, what).items) {
        if (v.kind != Value::Kind::Number || v.text.find_first_not_of("-0123456789") != std::string::npos) throw Error("wrong_type", what + " '" + key + "' must hold integers", v.at);
        out.push_back(std::atoi(v.text.c_str()));
    }
    return out;
}
void header(const Value& doc, const char* schema, const std::string& what) {
    if (text(doc, "schema", what) != schema || need(doc, "version", Value::Kind::Number, what).text != "1")
        throw Error("unsupported_version", what + " must be " + schema + " version 1", doc.at);
    if (const Value* n = doc.find("notes"); n && n->kind != Value::Kind::String) throw Error("wrong_type", what + " notes must be a string", n->at);
}
void range(bool ok, const std::string& what, const Value& at) { if (!ok) throw Error("out_of_range", what + " has a value outside its range", at.at); }
void subset(const std::vector<std::string>& got, std::initializer_list<const char*> allowed, const std::string& what, const Value& at) {
    const std::set<std::string> a(allowed.begin(), allowed.end());
    for (const auto& g : got) if (!a.count(g)) throw Error("unmapped_category", what + " has unknown entry '" + g + "'", at.at);
}
std::pair<int, int> span(const Value& o, const char* key, const std::string& what) {
    const auto v = ints(o, key, what);
    if (v.size() != 2 || v[0] < 1 || v[1] < v[0]) throw Error("out_of_range", what + " '" + key + "' is [min, max] with 1 <= min <= max", o.find(key)->at);
    return {v[0], v[1]};
}
const std::vector<std::string>& lord_cultures() {
    static const std::vector<std::string> c{"aserai", "battania", "empire", "khuzait", "nord", "sturgia", "vlandia"};
    return c;
}
}  // namespace

Shaping load_shaping(const fs::path& file) {
    const Value doc = cjson::parse(file);
    const std::string what = "Terrain shaping policy";
    only(doc, {"schema", "version", "stage", "despike", "coast", "pads", "lakes", "coherence", "lowPass", "normals", "resolution", "gates", "notes"}, what);
    header(doc, "mnbl.terrain-shaping", what);
    Shaping s;
    s.stage = text(doc, "stage", what);
    if (s.stage != "afterTravelPlacement") throw Error("unmapped_category", what + " stage must be afterTravelPlacement (pads use final settlement positions)", doc.at);
    auto deg = [&](double d) { return d > 0 && d < 90; };
    const Value& d = obj(doc, "despike", what);
    only(d, {"enabled", "maxSlopeDeg", "keepRidges", "ridgeNeighbours", "sweeps"}, what + " despike");
    s.despike = {flag(d, "enabled", what), flag(d, "keepRidges", what), real(d, "maxSlopeDeg", what), whole(d, "ridgeNeighbours", what), whole(d, "sweeps", what)};
    range(deg(s.despike.max_slope_deg) && s.despike.ridge_neighbours >= 1 && s.despike.ridge_neighbours <= 4 && s.despike.sweeps >= 1, what + " despike", d);
    const Value& c = obj(doc, "coast", what);
    only(c, {"enabled", "landMaxSlopeDeg", "seaMaxSlopeDeg", "bandSamples"}, what + " coast");
    s.coast = {flag(c, "enabled", what), real(c, "landMaxSlopeDeg", what), real(c, "seaMaxSlopeDeg", what), whole(c, "bandSamples", what)};
    range(deg(s.coast.land_max_slope_deg) && deg(s.coast.sea_max_slope_deg) && s.coast.band_samples >= 1, what + " coast", c);
    const Value& p = obj(doc, "pads", what);
    only(p, {"enabled", "radius", "maxSlopeDeg", "blendSamples", "mergeTouching"}, what + " pads");
    s.pads.enabled = flag(p, "enabled", what); s.pads.merge_touching = flag(p, "mergeTouching", what);
    s.pads.max_slope_deg = real(p, "maxSlopeDeg", what); s.pads.blend_samples = whole(p, "blendSamples", what);
    const Value& radius = obj(p, "radius", what + " pads");
    only(radius, {"town", "castle", "village"}, what + " pads.radius");
    for (const char* k : {"town", "castle", "village"}) { s.pads.radius[k] = real(radius, k, what + " pads.radius"); range(s.pads.radius[k] > 0, what + " pads.radius", radius); }
    range(deg(s.pads.max_slope_deg) && s.pads.blend_samples >= 0, what + " pads", p);
    const Value& l = obj(doc, "lakes", what);
    only(l, {"enabled", "bedBelowLevel", "shoreAboveLevel"}, what + " lakes");
    s.lakes = {flag(l, "enabled", what), real(l, "bedBelowLevel", what), real(l, "shoreAboveLevel", what)};
    range(s.lakes.bed_below_level > 0 && s.lakes.shore_above_level > 0, what + " lakes", l);
    const Value& co = obj(doc, "coherence", what);
    only(co, {"order"}, what + " coherence");
    s.coherence_order = strings(co, "order", what + " coherence");
    subset(s.coherence_order, {"lakes", "coast", "pads", "despike"}, what + " coherence.order", co);
    range(s.coherence_order.size() == 4, what + " coherence.order (every step once)", co);
    const Value& lp = obj(doc, "lowPass", what);
    only(lp, {"enabled", "kernel", "keep"}, what + " lowPass");
    s.low_pass = {flag(lp, "enabled", what), ints(lp, "kernel", what), strings(lp, "keep", what, true)};
    subset(s.low_pass.keep, {"sea", "lake", "coast"}, what + " lowPass.keep", lp);
    auto kernel_ok = [](const std::vector<int>& k) {
        if (k.size() < 3 || k.size() % 2 == 0) return false;
        for (std::size_t i = 0; i < k.size(); ++i) if (k[i] < 1 || k[i] != k[k.size() - 1 - i]) return false;
        return true;
    };
    range(kernel_ok(s.low_pass.kernel), what + " lowPass.kernel (odd, symmetric, positive)", lp);
    const Value& n = obj(doc, "normals", what);
    only(n, {"smoothedField", "kernel"}, what + " normals");
    s.normals = {flag(n, "smoothedField", what), ints(n, "kernel", what)};
    range(kernel_ok(s.normals.kernel), what + " normals.kernel (odd, symmetric, positive)", n);
    const Value& r = obj(doc, "resolution", what);
    only(r, {"side", "fineSide", "fine", "spline"}, what + " resolution");
    s.resolution = {whole(r, "side", what), whole(r, "fineSide", what), flag(r, "fine", what), text(r, "spline", what)};
    range(s.resolution.side == 257 && s.resolution.fine_side == 2 * (s.resolution.side - 1) + 1 && s.resolution.spline == "cubicBSpline", what + " resolution", r);
    const Value& g = obj(doc, "gates", what);
    only(g, {"interiorSlopeP95Deg", "interiorSlopeMaxDeg", "coastSlopeP95Deg", "isolatedSpikes", "steepPadDeg", "steepPadsMax", "travelClassesIdentical", "noLakeNewlyHidden", "seamPairsIdentical"}, what + " gates");
    s.gates.interior_p95_deg = real(g, "interiorSlopeP95Deg", what); s.gates.interior_max_deg = real(g, "interiorSlopeMaxDeg", what);
    s.gates.coast_p95_deg = real(g, "coastSlopeP95Deg", what); s.gates.steep_pad_deg = real(g, "steepPadDeg", what);
    s.gates.isolated_spikes = whole(g, "isolatedSpikes", what); s.gates.steep_pads_max = whole(g, "steepPadsMax", what);
    s.gates.travel_identical = flag(g, "travelClassesIdentical", what); s.gates.no_lake_hidden = flag(g, "noLakeNewlyHidden", what);
    s.gates.seams_identical = flag(g, "seamPairsIdentical", what);
    range(deg(s.gates.interior_p95_deg) && deg(s.gates.interior_max_deg) && s.gates.interior_p95_deg <= s.gates.interior_max_deg && deg(s.gates.coast_p95_deg) &&
          deg(s.gates.steep_pad_deg) && s.gates.isolated_spikes >= 0 && s.gates.steep_pads_max >= 0, what + " gates", g);
    s.sha256 = cjson::sha256_hex(cjson::canonical(doc));
    return s;
}

Population load_population(const fs::path& file) {
    const Value doc = cjson::parse(file);
    const std::string what = "Population policy";
    only(doc, {"schema", "version", "notes", "streams", "base", "targets", "kingdomMerge", "wars", "houses", "companies", "outlaws", "troopTemplates", "lordTraits",
               "wanderers", "spill", "census"}, what);
    header(doc, "mnbl.population", what);
    Population p;
    const Value& streams = obj(doc, "streams", what);
    only(streams, {"rule", "houses", "kin", "traits", "wanderers", "companies", "outlaws", "wars"}, what + " streams");
    text(streams, "rule", what + " streams");
    for (const char* k : {"houses", "kin", "traits", "wanderers", "companies", "outlaws", "wars"}) { p.streams[k] = whole(streams, k, what + " streams"); range(p.streams[k] >= 1, what + " streams", streams); }

    const Value& base = obj(doc, "base", what);
    only(base, {"fiefs", "towns", "castles", "kingdoms", "nobleClans", "aliveLords", "adultLords", "minorLords", "mercenaryClans", "outlawClans", "fiefsPerKingdom", "source"}, what + " base");
    p.base_fiefs = whole(base, "fiefs", what); p.base_towns = whole(base, "towns", what); p.base_kingdoms = whole(base, "kingdoms", what); p.base_noble_clans = whole(base, "nobleClans", what);
    for (const char* k : {"castles", "aliveLords", "adultLords", "minorLords", "mercenaryClans", "outlawClans"}) range(whole(base, k, what) >= 0, what + " base", base);
    text(base, "source", what + " base");
    int kingdom_fiefs = 0, largest = 0;
    for (const auto& [k, v] : obj(base, "fiefsPerKingdom", what).members) {
        if (v.kind != Value::Kind::Number) throw Error("wrong_type", what + " base.fiefsPerKingdom values are integers", v.at);
        const int f = std::atoi(v.text.c_str()); kingdom_fiefs += f; largest = std::max(largest, f);
    }
    // One fief base everywhere (RT-03): the installed War Sails campaign, 133 fiefs.
    if (p.base_fiefs != 133 || kingdom_fiefs != p.base_fiefs || whole(base, "towns", what) + whole(base, "castles", what) != p.base_fiefs)
        throw Error("out_of_range", what + " base must be the 133 War Sails fiefs, split over its kingdoms", base.at);

    const Value& targets = obj(doc, "targets", what);
    only(targets, {"kingdoms", "houses", "adultLordsPerHouse", "minorShare", "companies", "outlawClans", "wanderers", "engineChildren"}, what + " targets");
    const Value& tk = obj(targets, "kingdoms", what);
    only(tk, {"perBase", "min", "minHouses", "maxFiefs", "maxFiefsSource"}, what + " targets.kingdoms");
    p.kingdoms_per_base = real(tk, "perBase", what); p.kingdoms_min = whole(tk, "min", what); p.kingdom_min_houses = whole(tk, "minHouses", what);
    p.kingdom_max_fiefs = whole(tk, "maxFiefs", what); text(tk, "maxFiefsSource", what);
    if (p.kingdom_max_fiefs != largest) throw Error("out_of_range", what + " targets.kingdoms.maxFiefs must be the largest War Sails kingdom's fief count", tk.at);
    const Value& th = obj(targets, "houses", what);
    only(th, {"perBase", "minPerKingdom", "maxFiefs"}, what + " targets.houses");
    p.houses_per_base = real(th, "perBase", what); p.house_min_per_kingdom = whole(th, "minPerKingdom", what); p.house_max_fiefs = whole(th, "maxFiefs", what);
    p.adult_lords_per_house = real(targets, "adultLordsPerHouse", what);
    p.minor_share = real(targets, "minorShare", what);
    const Value& tc = obj(targets, "companies", what);
    only(tc, {"perBase", "members"}, what + " targets.companies");
    p.companies_per_base = real(tc, "perBase", what);
    std::tie(p.company_members_min, p.company_members_max) = span(tc, "members", what + " targets.companies");
    const Value& to = obj(targets, "outlawClans", what);
    only(to, {"perBase", "members"}, what + " targets.outlawClans");
    p.outlaws_per_base = real(to, "perBase", what);
    std::tie(p.outlaw_members_min, p.outlaw_members_max) = span(to, "members", what + " targets.outlawClans");
    const Value& tw = obj(targets, "wanderers", what);
    only(tw, {"perTown", "rounding"}, what + " targets.wanderers");
    p.wanderers_per_town = real(tw, "perTown", what);
    if (text(tw, "rounding", what) != "ceilFloat32") throw Error("unmapped_category", what + " targets.wanderers rounding is ceilFloat32 (War Sails' own rule, RT-04)", tw.at);
    text(targets, "engineChildren", what);
    range(p.kingdoms_per_base > 0 && p.kingdoms_min >= 1 && p.kingdom_min_houses >= 1 && p.houses_per_base > 0 && p.house_min_per_kingdom >= 1 && p.house_max_fiefs >= 1 &&
          p.adult_lords_per_house > 0 && p.minor_share >= 0 && p.minor_share < 1 && p.companies_per_base >= 0 && p.outlaws_per_base >= 0 && p.wanderers_per_town > 0, what + " targets", targets);

    const Value& merge = obj(doc, "kingdomMerge", what);
    only(merge, {"order", "crossWater", "ruler", "vassals", "record"}, what + " kingdomMerge");
    p.merge_order = strings(merge, "order", what + " kingdomMerge");
    subset(p.merge_order, {"civilizationLand", "presentationProximity", "splitAboveMax"}, what + " kingdomMerge.order", merge);
    if (p.merge_order.front() != "civilizationLand") throw Error("out_of_range", what + " kingdomMerge goes by civilization first (RT-01)", merge.at);
    p.merge_cross_water = flag(merge, "crossWater", what);
    if (text(merge, "ruler", what) != "highestRankedRealm") throw Error("unmapped_category", what + " kingdomMerge ruler is highestRankedRealm", merge.at);
    text(merge, "vassals", what); subset(strings(merge, "record", what), {"memberRealms", "mergeSteps"}, what + " kingdomMerge.record", merge);

    const Value& wars = obj(doc, "wars", what);
    only(wars, {"perKingdom", "maxPerKingdom", "partners", "relation"}, what + " wars");
    p.wars_per_kingdom = whole(wars, "perKingdom", what); p.wars_max_per_kingdom = whole(wars, "maxPerKingdom", what);
    p.war_partners = strings(wars, "partners", what + " wars");
    subset(p.war_partners, {"landBorder", "nearestBySea"}, what + " wars.partners", wars);
    range(p.wars_per_kingdom >= 0 && p.wars_max_per_kingdom >= p.wars_per_kingdom && whole(wars, "relation", what) == -1, what + " wars", wars);

    const Value& houses = obj(doc, "houses", what);
    only(houses, {"seatPriority", "memberPriority", "kin", "deadParents", "minors", "size", "age", "lordCultures", "castellanRule"}, what + " houses");
    p.seat_priority = strings(houses, "seatPriority", what + " houses");
    subset(p.seat_priority, {"kingdomRuler", "realmRuler", "castellan", "pretender", "magistrate"}, what + " houses.seatPriority", houses);
    p.member_priority = strings(houses, "memberPriority", what + " houses");
    subset(p.member_priority, {"sovereign", "pretender", "castellan", "founder", "magistrate", "commander"}, what + " houses.memberPriority", houses);
    p.kin = strings(houses, "kin", what + " houses");
    subset(p.kin, {"spouse", "sibling", "adultChild"}, what + " houses.kin", houses);
    p.dead_parents = flag(houses, "deadParents", what);
    const Value& minors = obj(houses, "minors", what);
    only(minors, {"enabled", "requireBothParents"}, what + " houses.minors");
    p.minors = flag(minors, "enabled", what); p.minors_both_parents = flag(minors, "requireBothParents", what);
    const Value& size = obj(houses, "size", what);
    only(size, {"draw", "min", "max", "cap", "minAdultLords", "eachSex"}, what + " houses.size");
    if (text(size, "draw", what) != "chaotic") throw Error("unmapped_category", what + " houses.size draw is chaotic", size.at);
    p.house_size_min = whole(size, "min", what); p.house_size_max = whole(size, "max", what); p.house_cap = whole(size, "cap", what);
    p.house_min_adults = whole(size, "minAdultLords", what); p.house_each_sex = flag(size, "eachSex", what);
    // The engine's random child generator clones a same-sex adult of the house: two adults, one of each sex (findings section 16).
    range(p.house_min_adults >= 2 && p.house_each_sex && p.house_size_min >= p.house_min_adults && p.house_size_max >= p.house_size_min && p.house_cap >= p.house_size_max, what + " houses.size", size);
    const Value& age = obj(houses, "age", what);
    only(age, {"adultMin", "adultMax", "source"}, what + " houses.age");
    p.adult_age_min = whole(age, "adultMin", what); p.adult_age_max = whole(age, "adultMax", what); text(age, "source", what);
    range(p.adult_age_min == 18 && p.adult_age_max > p.adult_age_min, what + " houses.age (heirs, party leaders and marriage need 18+)", age);
    p.lord_cultures = strings(houses, "lordCultures", what + " houses");
    if (p.lord_cultures != lord_cultures()) throw Error("unmapped_category", what + " houses.lordCultures are the 7 vanilla cultures with child and teen equipment", houses.at);
    text(houses, "castellanRule", what);

    const Value& companies = obj(doc, "companies", what);
    only(companies, {"captains", "members", "tier", "flags", "home"}, what + " companies");
    p.captain_order = strings(companies, "captains", what + " companies");
    subset(p.captain_order, {"remnantChampionHired", "livingWarlord", "domainHolder", "synthesized"}, what + " companies.captains", companies);
    p.company_member_order = strings(companies, "members", what + " companies");
    subset(p.company_member_order, {"ruinWarden", "warlord", "domainHolder", "synthesizedSoldier"}, what + " companies.members", companies);
    p.company_tier = whole(companies, "tier", what);
    subset(strings(companies, "flags", what), {"is_minor_faction", "is_clan_type_mercenary"}, what + " companies.flags", companies);
    if (text(companies, "home", what) != "town") throw Error("unmapped_category", what + " companies home is a town", companies.at);
    range(p.company_tier >= 1 && p.company_tier <= 6, what + " companies.tier", companies);

    const Value& outlaws = obj(doc, "outlaws", what);
    only(outlaws, {"leaders", "kinds", "tier", "installedTiers", "flags", "maxWarKingdoms", "home"}, what + " outlaws");
    p.outlaw_leader_order = strings(outlaws, "leaders", what + " outlaws");
    subset(p.outlaw_leader_order, {"banditBandLeader", "deserterBandLeader", "ruinCampChief", "synthesized"}, what + " outlaws.leaders", outlaws);
    p.outlaw_kinds = strings(outlaws, "kinds", what + " outlaws");
    subset(p.outlaw_kinds, {"mafia", "nomad", "sect"}, what + " outlaws.kinds", outlaws);
    p.outlaw_tier = whole(outlaws, "tier", what);
    obj(outlaws, "installedTiers", what);
    subset(strings(outlaws, "flags", what), {"is_minor_faction", "is_outlaw"}, what + " outlaws.flags", outlaws);
    p.outlaw_max_war_kingdoms = whole(outlaws, "maxWarKingdoms", what);
    text(outlaws, "home", what);
    range(p.outlaw_tier >= 1 && p.outlaw_tier <= 6 && p.outlaw_max_war_kingdoms >= 0, what + " outlaws", outlaws);

    // The per-culture troop template table (RT-03): every lord culture has a company line and at least one outlaw line.
    const Value& troops = obj(doc, "troopTemplates", what);
    only(troops, {"rule", "mercenaryTiers", "cultures"}, what + " troopTemplates");
    text(troops, "rule", what); obj(troops, "mercenaryTiers", what);
    auto line = [&](const Value& v, const std::string& w) {
        only(v, {"sourceClan", "kind", "partyTemplate", "characterTemplates", "tier", "templateCulture"}, w);
        TroopLine t{text(v, "sourceClan", w), text(v, "kind", w), text(v, "partyTemplate", w), text(v, "templateCulture", w), strings(v, "characterTemplates", w), whole(v, "tier", w)};
        if (t.character_templates.size() != 4 || t.party_template.rfind("kingdom_hero_party_", 0) != 0 || t.party_template == "kingdom_hero_party_" + t.template_culture + "_template" ||
            t.tier < 1 || t.tier > 6 || std::find(lord_cultures().begin(), lord_cultures().end(), t.template_culture) == lord_cultures().end())
            throw Error("out_of_range", w + " needs 4 leader templates of a lord culture, a minor-clan party template (never a lord template) and a tier", v.at);
        return t;
    };
    const Value& cultures = obj(troops, "cultures", what);
    for (const auto& [culture, v] : cultures.members) {
        const std::string w = what + " troopTemplates." + culture;
        only(v, {"mercenary", "outlaws", "outlawFallback"}, w);
        TroopLine m = line(obj(v, "mercenary", w), w + ".mercenary");
        if (m.kind != "mercenary") throw Error("unmapped_category", w + ".mercenary kind must be mercenary", v.at);
        p.mercenary[culture] = m;
        for (const auto& o : need(v, "outlaws", Value::Kind::Array, w).items) {
            TroopLine t = line(o, w + ".outlaws[]");
            if (t.kind != "mafia" && t.kind != "nomad" && t.kind != "sect") throw Error("unmapped_category", w + ".outlaws[] kind is mafia, nomad or sect", o.at);
            p.outlaw_lines[culture].push_back(t);
        }
        if (p.outlaw_lines[culture].empty()) throw Error("missing_field", w + " has no outlaw line", v.at);
        if (const Value* f = v.find("outlawFallback"); f && (f->kind != Value::Kind::String || f->text.empty())) throw Error("wrong_type", w + ".outlawFallback must be a string", f->at);
    }
    for (const auto& c : lord_cultures())
        if (!p.mercenary.count(c)) throw Error("missing_field", what + " troopTemplates has no line for presentation culture " + c + " (RT-03)", cultures.at);

    const Value& traits = obj(doc, "lordTraits", what);
    only(traits, {"rule", "traits", "lords", "sameCultureSharedShare", "marginals", "joint"}, what + " lordTraits");
    text(traits, "rule", what);
    p.traits = strings(traits, "traits", what + " lordTraits");
    subset(p.traits, {"Mercy", "Valor", "Honor", "Generosity", "Calculating"}, what + " lordTraits.traits", traits);
    p.shared_trait_share = real(traits, "sameCultureSharedShare", what);
    const int lords = whole(traits, "lords", what);
    obj(traits, "marginals", what);
    int counted = 0;
    for (const auto& j : need(traits, "joint", Value::Kind::Array, what).items) {
        only(j, {"values", "count"}, what + " lordTraits.joint[]");
        auto values = ints(j, "values", what + " lordTraits.joint[]");
        const int n = whole(j, "count", what);
        if (values.size() != p.traits.size() || n < 1 || std::any_of(values.begin(), values.end(), [](int x) { return x < -2 || x > 2; }))
            throw Error("out_of_range", what + " lordTraits.joint[] needs one value in -2..2 per trait and a positive count", j.at);
        counted += n;
        p.trait_joint.emplace_back(std::move(values), n);
    }
    range(counted == lords && lords > 0 && p.shared_trait_share >= 0 && p.shared_trait_share <= 1, what + " lordTraits (joint counts sum to lords)", traits);

    const Value& wanderers = obj(doc, "wanderers", what);
    only(wanderers, {"rule", "sources", "rotation", "topUp", "keepEpithet", "sex", "reserve", "backstoryUnsafe", "backstoryUnsafeOwner", "backstoryCensus", "fallback", "templates"}, what + " wanderers");
    text(wanderers, "rule", what); text(wanderers, "backstoryUnsafeOwner", what);
    // MMR-281: the runtime names per presentation culture (campaign.json wandererReserve).
    p.wanderer_reserve = whole(wanderers, "reserve", what + " wanderers");
    range(p.wanderer_reserve >= 1 && p.wanderer_reserve <= 256, what + " wanderers.reserve", wanderers);
    p.wanderer_sources = strings(wanderers, "sources", what + " wanderers");
    subset(p.wanderer_sources, {"champion", "magister", "exile", "domainHolder"}, what + " wanderers.sources", wanderers);
    const Value& rotation = obj(wanderers, "rotation", what);
    only(rotation, {"dailyChance", "retire"}, what + " wanderers.rotation");
    p.rotation_daily_chance = real(rotation, "dailyChance", what);
    if (text(rotation, "retire", what) != "unmetUnhired") throw Error("unmapped_category", what + " wanderers.rotation retires only unmet, unhired wanderers", rotation.at);
    range(p.rotation_daily_chance >= 0 && p.rotation_daily_chance <= 1 && flag(wanderers, "topUp", what) && flag(wanderers, "keepEpithet", what) && text(wanderers, "sex", what) == "source",
          what + " wanderers", wanderers);
    std::set<std::string> template_ids;
    for (const auto& t : need(wanderers, "templates", Value::Kind::Array, what).items) {
        only(t, {"id", "culture", "female", "epithet"}, what + " wanderers.templates[]");
        Population::WandererTemplate wt{text(t, "id", what), text(t, "culture", what), flag(t, "female", what)};
        text(t, "epithet", what);
        if (!template_ids.insert(wt.id).second) throw Error("duplicate_id", what + " wanderers.templates repeats " + wt.id, t.at);
        p.wanderer_templates.push_back(wt);
    }
    p.backstory_unsafe = strings(wanderers, "backstoryUnsafe", what + " wanderers", true);
    for (const auto& id : p.backstory_unsafe) if (!template_ids.count(id)) throw Error("unmapped_category", what + " wanderers.backstoryUnsafe names unknown template " + id, wanderers.at);
    range(!p.wanderer_templates.empty(), what + " wanderers.templates", wanderers);
    // MMR-287: the census file the unsafe list comes from, and each lord culture's ordered cross-culture fallback (managed HeroFactory.WandererPool).
    text(wanderers, "backstoryCensus", what);
    const Value& fallback = obj(wanderers, "fallback", what);
    only(fallback, {"aserai", "battania", "empire", "khuzait", "nord", "sturgia", "vlandia"}, what + " wanderers.fallback");
    for (const auto& c : lord_cultures()) {
        const auto order = strings(fallback, c.c_str(), what + " wanderers.fallback");
        subset(order, {"aserai", "battania", "empire", "khuzait", "nord", "sturgia", "vlandia"}, what + " wanderers.fallback." + c, fallback);
        if (std::find(order.begin(), order.end(), c) != order.end()) throw Error("out_of_range", what + " wanderers.fallback." + c + " names its own culture", fallback.at);
    }

    const Value& spill = obj(doc, "spill", what);
    only(spill, {"order", "houseCap", "regalToCompany"}, what + " spill");
    p.spill_order = strings(spill, "order", what + " spill");
    if (p.spill_order != std::vector<std::string>{"house", "wanderer", "company"}) throw Error("out_of_range", what + " spill order is house, wanderer, company", spill.at);
    p.spill_house_cap = whole(spill, "houseCap", what); p.regal_to_company = flag(spill, "regalToCompany", what);
    range(p.spill_house_cap == p.house_cap && !p.regal_to_company, what + " spill (cap as houses.size.cap; no regal hero spills to a company)", spill);
    const Value& census = obj(doc, "census", what);
    only(census, {"notableCaps", "assertUnassigned"}, what + " census");
    // HeroFactory's per-settlement notable caps (rosters.json notables.caps), which the census applies (RT-11).
    const Value& caps = obj(census, "notableCaps", what);
    only(caps, {"source", "town", "village"}, what + " census.notableCaps");
    text(caps, "source", what);
    for (const char* kind : {"town", "village"})
        for (const auto& [occupation, v] : obj(caps, kind, what).members) {
            if (v.kind != Value::Kind::Number || v.text.find_first_not_of("0123456789") != std::string::npos) throw Error("wrong_type", what + " census.notableCaps values are counts", v.at);
            p.notable_caps[kind][occupation] = std::atoi(v.text.c_str());
        }
    range(whole(census, "assertUnassigned", what) == 0, what + " census.assertUnassigned", census);
    p.sha256 = cjson::sha256_hex(cjson::canonical(doc));
    return p;
}

}  // namespace worldbridge::policy
