#include "worldbridge/campaign_compiler.hpp"
#include <algorithm>
#include <cstdlib>
#include <fstream>
#include <iostream>
#include <set>
#include <stdexcept>
#include "worldbridge/canonical_json.hpp"

namespace worldbridge::campaign {
namespace {
using cjson::Value;
using json::Error;
using json::Location;

Location nowhere() { return Location{"", 0, 1, 1, "$"}; }
[[noreturn]] void refuse(const std::string& code, const std::string& message) { throw Error(code, message, nowhere()); }

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
const std::string& text(const Value& object, const char* key, const std::string& what) { return need(object, key, Value::Kind::String, what).text; }
void header(const Value& doc, const char* schema, const std::string& what) {
    if (text(doc, "schema", what) != schema || need(doc, "version", Value::Kind::Number, what).text != "1")
        throw Error("unsupported_version", what + " must be " + schema + " version 1", doc.at);
    if (const Value* n = doc.find("notes"); n && n->kind != Value::Kind::String) throw Error("wrong_type", what + " notes must be a string", n->at);
}

// One pass over the template: substituted text is never scanned again, so a source name containing "{n}" stays as is.
std::string fill(const std::string& pattern, const std::map<std::string, std::string>& values) {
    std::string out;
    for (std::size_t at = 0; at < pattern.size(); ++at) {
        if (pattern[at] == '{') {
            const auto close = pattern.find('}', at);
            if (close != std::string::npos) {
                const auto it = values.find(pattern.substr(at + 1, close - at - 1));
                if (it != values.end()) { out += it->second; at = close; continue; }
            }
        }
        out += pattern[at];
    }
    return out;
}

Value str(const std::string& s) { return Value::string(s); }
Value number(double v) { Value out; out.kind = Value::Kind::Number; out.text = cjson::ecmascript_number(v); return out; }
Value integer(std::int64_t v) { Value out; out.kind = Value::Kind::Number; out.text = std::to_string(v); return out; }
Value boolean(bool b) { Value out; out.kind = Value::Kind::Bool; out.boolean = b; return out; }
Value strings(const std::vector<std::string>& items) { Value out; out.kind = Value::Kind::Array; for (const auto& s : items) out.items.push_back(str(s)); return out; }
Value slots(const std::vector<Slot>& list) {
    Value out; out.kind = Value::Kind::Array;
    for (const auto& s : list) { Value v = Value::object(); v.set("name", str(s.name)); v.set("status", str(s.status)); v.set("owner", str(s.owner)); if (!s.value.empty()) v.set("value", str(s.value)); out.items.push_back(v); }
    return out;
}
Value named(const std::string& id, const std::string& name, const std::string& provenance) {
    Value v = Value::object();
    v.set("id", str(id)); v.set("name", str(name)); v.set("nameXml", str(xml_escape(name)));
    v.set("nameLocalized", str(localized(id, name))); v.set("nameLocalizedXml", str(localized_xml(id, name)));
    v.set("nameProvenance", str(provenance));
    return v;
}
void write(const fs::path& file, const std::string& bytes) {
    std::ofstream out(file, std::ios::binary | std::ios::trunc);
    out.write(bytes.data(), static_cast<std::streamsize>(bytes.size()));
    out.close();
    if (!out) throw std::runtime_error("Cannot write " + file.string());
}

// Source records in UID order, so synthesized names (and their lexicon suffixes) do not depend on the source array order.
template <class T> std::vector<const T*> uid_order(const std::vector<T>& list) {
    std::vector<const T*> out;
    for (const auto& x : list) out.push_back(&x);
    std::sort(out.begin(), out.end(), [](const T* a, const T* b) { return a->uid < b->uid; });
    return out;
}

void append(std::string& provenance, const std::string& note) { provenance += "; " + note; }
}  // namespace

const names::Heritage& Naming::of(const std::string& culture_uid, const std::string& fallback_civilization) const {
    static const names::Heritage none;
    const auto c = culture_civilization.find(culture_uid);
    const auto it = heritage.find(c == culture_civilization.end() ? fallback_civilization : c->second);
    if (it != heritage.end()) return it->second;
    const auto f = heritage.find(fallback_civilization);
    return f == heritage.end() ? none : f->second;
}

std::string xml_escape(const std::string& text) {
    std::string out;
    for (const unsigned char ch : text) {
        switch (ch) {
        case '&': out += "&amp;"; break;
        case '<': out += "&lt;"; break;
        case '>': out += "&gt;"; break;
        case '"': out += "&quot;"; break;
        case '\'': out += "&apos;"; break;
        default: if (ch >= 0x20) out += static_cast<char>(ch);  // control characters, tab included, are dropped
        }
    }
    return out;
}

std::string localized(const std::string& id, const std::string& name) {
    std::string clean;
    for (const unsigned char ch : name) {
        if (ch < 0x20) continue;
        clean += ch == '{' ? '(' : ch == '}' ? ')' : static_cast<char>(ch);  // TextObject reads braces as variables
    }
    return "{=" + id + "}" + clean;
}

std::string localized_xml(const std::string& id, const std::string& name) { return xml_escape(localized(id, name)); }

ContentPolicy load_content(const fs::path& file) {
    const Value doc = cjson::parse(file);
    only(doc, {"schema", "version", "settlementKinds", "fortressKinds", "hamletKinds", "hamletRoles", "heroRoles", "heroStatuses", "occupations", "names", "minima", "notes"}, "Campaign content policy");
    header(doc, "mnbl.campaign-content", "Campaign content policy");
    ContentPolicy p;
    auto kinds = [&](const char* key, const char* game, std::map<std::string, std::string>& out) {
        for (const auto& k : need(doc, key, Value::Kind::Array, "Campaign content policy").items) {
            only(k, {"source", "game"}, key);
            if (text(k, "game", key) != game) throw Error("unmapped_category", std::string(key) + " must map to '" + game + "'", k.at);
            if (!out.emplace(text(k, "source", key), game).second) throw Error("duplicate_id", std::string("duplicate ") + key + " entry", k.at);
        }
    };
    // A place kind's lexicon concepts (MMR-286): a non-empty list of distinct lower-case words.
    auto concepts = [&](const Value& object, const std::string& what) {
        std::vector<std::string> out;
        for (const auto& c : need(object, "concepts", Value::Kind::Array, what).items) {
            if (c.kind != Value::Kind::String || c.text.empty() || std::any_of(c.text.begin(), c.text.end(), [](char ch) { return ch < 'a' || ch > 'z'; }))
                throw Error("wrong_type", what + " concepts must be lower-case lexicon concept words", c.at);
            if (std::find(out.begin(), out.end(), c.text) != out.end()) throw Error("duplicate_id", what + " repeats a concept", c.at);
            out.push_back(c.text);
        }
        if (out.empty()) throw Error("missing_field", what + " needs at least one concept", object.at);
        return out;
    };
    kinds("settlementKinds", "town", p.settlement_kinds); kinds("fortressKinds", "castle", p.fortress_kinds); kinds("hamletKinds", "village", p.hamlet_kinds);
    for (const auto& r : need(doc, "hamletRoles", Value::Kind::Array, "Campaign content policy").items) {
        only(r, {"role", "production", "harbour", "concepts"}, "hamletRoles[]");
        RoleRule rule{text(r, "production", "hamletRoles[]"), concepts(r, "hamletRoles[]"), need(r, "harbour", Value::Kind::Bool, "hamletRoles[]").boolean};
        if (!p.hamlet_roles.emplace(text(r, "role", "hamletRoles[]"), rule).second) throw Error("duplicate_id", "duplicate hamlet role", r.at);
    }
    for (const auto& o : need(doc, "occupations", Value::Kind::Array, "Campaign content policy").items) {
        only(o, {"occupation", "affiliation", "settlement", "evidence"}, "occupations[]");
        OccupationRule rule{text(o, "affiliation", "occupations[]"), o.find("settlement") ? text(o, "settlement", "occupations[]") : "", text(o, "evidence", "occupations[]")};
        if (rule.affiliation != "clan" && rule.affiliation != "settlement" && rule.affiliation != "none") throw Error("unmapped_category", "affiliation must be clan, settlement or none", o.at);
        if ((rule.affiliation == "settlement") == rule.settlement.empty()) throw Error("unmapped_category", "an occupation names a settlement kind exactly when its affiliation is settlement", o.at);
        if (!rule.settlement.empty() && rule.settlement != "town" && rule.settlement != "village") throw Error("unmapped_category", "settlement must be town or village", o.at);
        if (rule.evidence.empty()) throw Error("missing_field", "occupations[] evidence must name the installed characters that carry the occupation", o.at);
        if (!p.occupations.emplace(text(o, "occupation", "occupations[]"), rule).second) throw Error("duplicate_id", "duplicate occupation", o.at);
    }
    for (const auto& r : need(doc, "heroRoles", Value::Kind::Array, "Campaign content policy").items) {
        only(r, {"role", "occupation", "withoutRealm", "bucket", "fallbackBucket", "seatBuckets"}, "heroRoles[]");
        HeroRule rule{text(r, "occupation", "heroRoles[]"), r.find("withoutRealm") ? text(r, "withoutRealm", "heroRoles[]") : "", text(r, "bucket", "heroRoles[]"),
                      r.find("fallbackBucket") ? text(r, "fallbackBucket", "heroRoles[]") : "", {}};
        static const std::set<std::string> buckets{"house", "company", "wanderer", "notable", "none"};
        if (const Value* seats = r.find("seatBuckets")) {
            if (seats->kind != Value::Kind::Object || seats->members.empty()) throw Error("wrong_type", "heroRoles[] seatBuckets must be a non-empty object", seats->at);
            for (const auto& [seat, v] : seats->members) {
                if (v.kind != Value::Kind::String || !buckets.count(v.text)) throw Error("unmapped_category", "heroRoles[] seatBuckets values are population buckets", v.at);
                rule.seat_buckets[seat] = v.text;
            }
        }
        if (!buckets.count(rule.bucket) || (!rule.fallback_bucket.empty() && (!buckets.count(rule.fallback_bucket) || rule.fallback_bucket == rule.bucket)))
            throw Error("unmapped_category", "heroRoles[] bucket and fallbackBucket are distinct population buckets (house, company, wanderer, notable, none)", r.at);
        for (const auto* occupation : {&rule.occupation, &rule.without_realm})
            if (!occupation->empty() && !p.occupations.count(*occupation)) throw Error("unmapped_category", "occupation '" + *occupation + "' has no occupations[] rule", r.at);
        if (!p.hero_roles.emplace(text(r, "role", "heroRoles[]"), rule).second) throw Error("duplicate_id", "duplicate hero role", r.at);
    }
    for (const auto& s : need(doc, "heroStatuses", Value::Kind::Array, "Campaign content policy").items) {
        only(s, {"status", "instantiate"}, "heroStatuses[]");
        if (!p.hero_statuses.emplace(text(s, "status", "heroStatuses[]"), need(s, "instantiate", Value::Kind::Bool, "heroStatuses[]").boolean).second) throw Error("duplicate_id", "duplicate hero status", s.at);
    }
    // Name templates: only their own placeholders, balanced braces. MMR-286: the cleaner names every kind; places (town for a nameless
    // source site, castle) list the lexicon concepts their names are made of (villages: hamletRoles[] concepts).
    static const std::map<std::string, std::set<std::string>> placeholders{{"rulingClan", {"realm"}}};
    const Value& names = need(doc, "names", Value::Kind::Object, "Campaign content policy");
    only(names, {"rulingClan", "places", "cleaner"}, "names");
    {
        const Value& c = need(names, "cleaner", Value::Kind::Object, "names");
        only(c, {"stripAgeTag", "dedupe", "numerals", "keepSourceName"}, "names.cleaner");
        p.cleaner.strip_age_tag = need(c, "stripAgeTag", Value::Kind::Bool, "names.cleaner").boolean;
        p.cleaner.numerals = need(c, "numerals", Value::Kind::Bool, "names.cleaner").boolean;
        p.cleaner.keep_source_name = need(c, "keepSourceName", Value::Kind::Bool, "names.cleaner").boolean;
        p.cleaner.dedupe = text(c, "dedupe", "names.cleaner");
        if (p.cleaner.dedupe != "lexiconSuffix") throw Error("unmapped_category", "names.cleaner dedupe must be lexiconSuffix", c.at);
        if (!p.cleaner.strip_age_tag || p.cleaner.numerals) throw Error("unmapped_category", "names.cleaner must strip Age tags and allow no numerals (MMR-286)", c.at);
    }
    {
        const Value& places = need(names, "places", Value::Kind::Object, "names");
        only(places, {"town", "castle"}, "names.places");
        for (const char* kind : {"town", "castle"}) {
            const Value& k = need(places, kind, Value::Kind::Object, "names.places");
            only(k, {"concepts"}, std::string("names.places.") + kind);
            p.places[kind] = concepts(k, std::string("names.places.") + kind);
        }
    }
    for (const auto& [key, allowed] : placeholders) {
        const std::string& t = text(names, key.c_str(), "names");
        for (std::size_t at = 0; at < t.size(); ++at) {
            if (t[at] == '}') throw Error("invalid_template", "names." + key + " has an unmatched '}'", names.at);
            if (t[at] != '{') continue;
            const auto close = t.find('}', at);
            if (close == std::string::npos) throw Error("invalid_template", "names." + key + " has an unmatched '{'", names.at);
            const std::string token = t.substr(at + 1, close - at - 1);
            if (!allowed.count(token)) throw Error("invalid_template", "names." + key + " uses unknown placeholder {" + token + "}", names.at);
            at = close;
        }
        p.names[key] = t;
    }
    static const std::set<std::string> known_rules{"towns", "kingdomTown", "villageBound", "settlementOwner", "clanLeader", "kingdomRuler", "playerAliases"};
    for (const auto& m : need(doc, "minima", Value::Kind::Array, "Campaign content policy").items) {
        only(m, {"rule", "minimum", "evidence"}, "minima[]");
        ContentPolicy::Minimum min{text(m, "rule", "minima[]"), text(m, "evidence", "minima[]"), std::atoll(need(m, "minimum", Value::Kind::Number, "minima[]").text.c_str())};
        if (!known_rules.count(min.rule)) throw Error("unmapped_category", "unknown minimum rule '" + min.rule + "'", m.at);
        p.minima.push_back(min);
    }
    if (p.settlement_kinds.empty() || p.hamlet_roles.empty() || p.hero_roles.empty() || p.hero_statuses.empty()) throw Error("missing_field", "Campaign content policy has an empty table", doc.at);
    p.sha256 = cjson::sha256_hex(cjson::canonical(doc));
    return p;
}

CulturePolicy load_cultures(const fs::path& file) {
    const Value doc = cjson::parse(file);
    only(doc, {"schema", "version", "officialCultures", "civilizations", "notes"}, "Culture presentation policy");
    header(doc, "mnbl.culture-presentations", "Culture presentation policy");
    CulturePolicy p;
    for (const auto& c : need(doc, "officialCultures", Value::Kind::Array, "Culture presentation policy").items) {
        if (c.kind != Value::Kind::String) throw Error("wrong_type", "officialCultures must be strings", c.at);
        p.official.push_back(c.text);
    }
    for (const auto& c : need(doc, "civilizations", Value::Kind::Array, "Culture presentation policy").items) {
        only(c, {"civilization", "culture", "reason"}, "civilizations[]");
        const auto& culture = text(c, "culture", "civilizations[]");
        text(c, "reason", "civilizations[]");
        if (std::find(p.official.begin(), p.official.end(), culture) == p.official.end()) throw Error("unmapped_category", "culture '" + culture + "' is not an official culture", c.at);
        if (!p.presentation.emplace(text(c, "civilization", "civilizations[]"), culture).second) throw Error("duplicate_id", "duplicate civilization", c.at);
    }
    p.sha256 = cjson::sha256_hex(cjson::canonical(doc));
    return p;
}

Compiled compile(const source::SourceWorld& world, const ContentPolicy& content, const CulturePolicy& cultures, const std::string& world_sha256, int id_hash_hex) {
    if (world.cities.empty()) refuse("missing_field", "The source has no settlements.sites");
    if (world.realms.empty()) refuse("missing_field", "The source has no heroes.realms");
    Compiled out;
    const auto& m = world.metadata;
    out.layout_seed = m.layout_seed;
    out.source_identity = source::write_identity(m);
    out.world_sha256 = world_sha256;
    out.prefix = ids::world_prefix(out.source_identity, world_sha256);
    ids::Registry reg(out.prefix, id_hash_hex);
    // Producer limits: the producer's own report is required and authoritative; nothing is added or removed here.
    auto reported = [](const std::optional<std::int64_t>& value, const char* what) {
        if (!value) refuse("missing_field", std::string("The producer report lacks ") + what + "; every world is checked against it");
        return *value;
    };
    const auto placed = reported(m.founding_placed_cities, "settlements.founding.placed_cities");
    const auto target = reported(m.founding_target_cities, "settlements.founding.target_cities");
    const auto fortress_limit = reported(m.fortress_count, "effective_config.fortress_count");
    const auto hamlet_limit = reported(m.hamlets_per_core, "effective_config.hamlets_per_core");
    const auto sites = static_cast<std::int64_t>(world.cities.size());
    out.producer["sites"] = sites;
    out.producer["hamlets"] = static_cast<std::int64_t>(world.hamlets.size());
    out.producer["fortresses"] = static_cast<std::int64_t>(world.castles.size());
    out.producer["realms"] = static_cast<std::int64_t>(world.realms.size());
    out.producer["foundingPlacedCities"] = placed;
    out.producer["foundingTargetCities"] = target;
    out.producer["fortressCountLimit"] = fortress_limit;
    out.producer["hamletsPerCoreLimit"] = hamlet_limit;
    if (m.settlement_count) out.producer["settlementCountSetting"] = *m.settlement_count;
    if (placed != sites) refuse("producer_mismatch", "settlements.founding.placed_cities differs from the site count");
    if (sites > target) refuse("producer_mismatch", "More sites than settlements.founding.target_cities");
    if (static_cast<std::int64_t>(world.castles.size()) > fortress_limit) refuse("producer_mismatch", "More fortresses than effective_config.fortress_count");
    // Player-adopted rows sit outside the producer's limits and belong to the player faction, which the registry
    // creates (MMR-30); this compiler takes generated rows only.
    for (const auto& h : world.hamlets) if (!h.founded_by.empty()) refuse("unsupported_row", "Hamlet " + h.uid + " was founded by '" + h.founded_by + "'; player-adopted rows are not compiled here (MMR-30)");
    for (const auto& k : world.castles) if (!k.founded_by.empty()) refuse("unsupported_row", "Fortress " + k.uid + " was founded by '" + k.founded_by + "'; player-adopted rows are not compiled here (MMR-30)");
    // hamlets_per_core caps the land hamlets a core raises (generator humans.cpp); harbour-shaped hamlets come from
    // fisheries (society.cpp) and are not under it.
    std::map<std::int64_t, std::int64_t> per_core, land_per_core;
    for (const auto& h : world.hamlets) { ++per_core[h.core_id]; if (!h.harbour) ++land_per_core[h.core_id]; }
    std::int64_t most = 0, most_land = 0;
    for (const auto& [core, count] : per_core) most = std::max(most, count);
    for (const auto& [core, count] : land_per_core) {
        most_land = std::max(most_land, count);
        if (count > hamlet_limit) refuse("producer_mismatch", "Core site " + std::to_string(core) + " has " + std::to_string(count) + " land hamlets, above effective_config.hamlets_per_core " + std::to_string(hamlet_limit));
    }
    out.producer["maxHamletsPerCore"] = most;
    out.producer["maxLandHamletsPerCore"] = most_land;

    // Kinds, then indexes by stable UID.
    for (const auto& c : world.cities) if (!content.settlement_kinds.count(c.kind)) refuse("unmapped_category", "Settlement kind '" + c.kind + "' (" + c.uid + ") has no mapping");
    for (const auto& k : world.castles) if (!content.fortress_kinds.count(k.kind)) refuse("unmapped_category", "Fortress kind '" + k.kind + "' (" + k.uid + ") has no mapping");
    for (const auto& h : world.hamlets) if (!content.hamlet_kinds.count(h.kind)) refuse("unmapped_category", "Hamlet kind '" + h.kind + "' (" + h.uid + ") has no mapping");
    std::map<std::string, const source::City*> city_by_uid;
    std::map<std::int64_t, const source::City*> city_by_site;
    for (const auto& c : world.cities) {
        if (!city_by_uid.emplace(c.uid, &c).second) refuse("duplicate_id", "Duplicate city uid " + c.uid);
        city_by_site[c.site_id] = &c;
    }
    std::map<std::string, const source::CultureEntry*> culture_by_id;
    for (const auto& c : world.cultures) if (!culture_by_id.emplace(c.id, &c).second) refuse("duplicate_id", "Duplicate humans.cultures id " + c.id);
    auto culture_of = [&](const std::string& id, const std::string& who) -> const source::CultureEntry& {
        const auto it = culture_by_id.find(id);
        if (it == culture_by_id.end()) refuse("broken_join", who + " names unknown culture '" + id + "'");
        return *it->second;
    };
    std::map<std::string, const source::Person*> person_by_uid;
    for (const auto& p : world.people) if (!person_by_uid.emplace(p.uid, &p).second) refuse("duplicate_id", "Duplicate person uid " + p.uid);
    auto presentation = [&](const std::string& civilization, const std::string& who) {
        const auto it = cultures.presentation.find(civilization);
        if (it == cultures.presentation.end()) refuse("unmapped_category", "Civilization '" + civilization + "' of " + who + " has no official culture presentation");
        return it->second;
    };
    // Faction distribution: every city is held by exactly one realm.
    std::map<std::string, const source::Realm*> realm_of_city;
    std::map<std::string, const source::Realm*> realm_by_uid;
    for (const auto& r : world.realms) {
        if (!realm_by_uid.emplace(r.uid, &r).second) refuse("duplicate_id", "Duplicate realm uid " + r.uid);
        if (r.city_uids.empty()) refuse("faction_distribution", "Realm " + r.uid + " holds no city");
        for (const auto& u : r.city_uids) {
            if (!city_by_uid.count(u)) refuse("broken_join", "Realm " + r.uid + " names unknown city " + u);
            if (!realm_of_city.emplace(u, &r).second) refuse("faction_distribution", "City " + u + " is held by two realms");
        }
        if (std::find(r.city_uids.begin(), r.city_uids.end(), r.capital_uid) == r.city_uids.end()) refuse("broken_join", "Realm " + r.uid + " capital " + r.capital_uid + " is not one of its cities");
        if (culture_of(r.culture_id, "Realm " + r.uid).civilization_id != r.civilization_id) refuse("broken_join", "Realm " + r.uid + " civilization differs from its culture's");
        presentation(r.civilization_id, "realm " + r.uid);
    }
    for (const auto& c : world.cities) if (!realm_of_city.count(c.uid)) refuse("faction_distribution", "City " + c.uid + " is held by no realm");

    // Ids for every source record (heroes below, once their status and role are known).
    for (const auto& r : world.realms) { reg.assign("kingdom", r.uid); reg.assign("clan", "house:" + r.uid); }
    for (const auto& c : world.cities) reg.assign("town", c.uid);
    for (const auto& k : world.castles) reg.assign("castle", k.uid);
    for (const auto& h : world.hamlets) reg.assign("village", h.uid);

    // Names (MMR-286): the shared cleaner per kind (kingdoms; clans; towns, castles and villages together; heroes): Age tags stripped,
    // collisions told apart by a lexicon suffix, never a numeral; villages, castles and nameless towns are coined from their
    // civilization's lexicon roots. The source name stays as sourceName.
    for (const auto& h : world.heritage) out.naming.heritage[h.civilization_id] = names::Heritage{h.naming_pattern, h.lexicon};
    for (const auto& cu : world.cultures) out.naming.culture_civilization[cu.id] = cu.civilization_id;
    names::Unique kingdom_names(content.cleaner), clan_names(content.cleaner), place_names(content.cleaner), hero_names(content.cleaner), ruin_names(content.cleaner);
    auto cleaned = [](const std::string& source, const std::string& name) {
        if (name == source) return std::string("source");
        return names::strip_age_tag(source) == name ? std::string("source; Age tag removed") : std::string("source; Age tag removed, disambiguated with a lexicon suffix");
    };
    std::map<std::string, std::size_t> kingdom_index, house_index, town_index, village_index;
    for (const auto* rp : uid_order(world.realms)) {
        const auto& r = *rp;
        Kingdom k;
        k.id = reg.at("kingdom", r.uid); k.uid = r.uid; k.source_name = r.name;
        k.name = kingdom_names.take(r.name, out.naming.of("", r.civilization_id), names::salt_of(r.uid)); k.english_name = r.english_name; k.civilization = r.civilization_id;
        k.civilization_name = r.civilization_name; k.parent_race_id = r.parent_race_id;
        k.culture_uid = r.culture_id; k.presentation = presentation(r.civilization_id, "realm " + r.uid); k.capital_id = reg.at("town", r.capital_uid);
        k.member_realms = {r.uid}; k.merge_steps = {{r.uid, "seed", false}};
        House c;
        c.id = reg.at("clan", "house:" + r.uid); c.uid = "house:" + r.uid; c.name = clan_names.take(fill(content.names.at("rulingClan"), {{"realm", k.name}}), out.naming.of("", r.civilization_id), names::salt_of(c.uid));
        c.kingdom_id = k.id; c.home_id = k.capital_id; c.seat_id = k.capital_id; c.culture_uid = r.culture_id; c.presentation = k.presentation; c.origin = "realmRuler";
        c.provenance = "synthesized: the realm's provisional house (realm " + r.uid + "); the population pass plans the houses";
        k.ruling_house_id = c.id;
        kingdom_index[r.uid] = out.kingdoms.size(); out.kingdoms.push_back(k);
        house_index[r.uid] = out.houses.size(); out.houses.push_back(c);
    }
    // Towns. A town's culture is its core's (it must exist and belong to the town's civilization).
    for (const auto* cp : uid_order(world.cities)) {
        const auto& c = *cp;
        const auto& realm = *realm_of_city.at(c.uid);
        Town t;
        t.id = reg.at("town", c.uid); t.uid = c.uid; t.civilization = c.civilization_id;
        if (!c.culture_id.empty()) {
            const auto& culture = culture_of(c.culture_id, "City " + c.uid);
            if (culture.civilization_id != c.civilization_id) refuse("broken_join", "City " + c.uid + " civilization " + c.civilization_id + " differs from its culture's (" + culture.civilization_id + ")");
            t.culture_uid = c.culture_id; t.culture_provenance = "source: humans.cores culture_id";
        } else {
            t.culture_uid = realm.culture_id; t.culture_provenance = "the holding realm's culture: the core carries no culture_id";
        }
        t.presentation = presentation(c.civilization_id, "city " + c.uid);
        t.kingdom_id = out.kingdoms[kingdom_index.at(realm.uid)].id; t.owner_clan_id = out.houses[house_index.at(realm.uid)].id;
        out.houses[house_index.at(realm.uid)].fiefs.push_back(t.id);
        t.city_class = c.city_class; t.capital = realm.capital_uid == c.uid;
        t.population = c.population_estimate; t.urban = c.urban_population_estimate; t.rural = c.rural_population_estimate;
        const auto& heritage = out.naming.of(t.culture_uid, c.civilization_id);
        if (!c.name.empty()) { t.source_name = c.name; t.name = place_names.take(c.name, heritage, names::salt_of(c.uid)); t.name_provenance = cleaned(c.name, t.name); }
        else { t.name = place_names.coin(heritage, content.places.at("town"), names::salt_of(c.uid)); t.name_provenance = "synthesized: the source site has no name; FWGC++ lexicon roots"; }
        town_index[c.uid] = out.towns.size(); out.towns.push_back(t);
        out.kingdoms[kingdom_index.at(realm.uid)].towns.push_back(t.id);
    }
    // Villages, bound to their core city's town; production and harbour by role.
    std::map<std::string, std::vector<std::size_t>> harbours;  // town uid -> village indexes
    for (const auto* hp : uid_order(world.hamlets)) {
        const auto& h = *hp;
        const auto core = city_by_site.find(h.core_id);
        if (core == city_by_site.end()) refuse("broken_join", "Hamlet " + h.uid + " names unknown core site " + std::to_string(h.core_id));
        const auto role = content.hamlet_roles.find(h.role);
        if (role == content.hamlet_roles.end()) refuse("unmapped_category", "Hamlet role '" + h.role + "' (" + h.uid + ") has no mapping");
        if (role->second.harbour && !h.harbour) refuse("broken_join", "Hamlet " + h.uid + " has a harbour role but no harbour record (sea node)");
        auto& town = out.towns[town_index.at(core->second->uid)];
        Village v;
        v.id = reg.at("village", h.uid); v.uid = h.uid; v.bound_id = town.id; v.source_town = town.id; v.role = h.role; v.production = role->second.production;
        v.harbour = role->second.harbour;
        if (!h.culture_id.empty()) culture_of(h.culture_id, "Hamlet " + h.uid);
        v.culture_uid = h.culture_id.empty() ? town.culture_uid : h.culture_id;
        if (h.harbour) { v.sea_node = h.harbour->sea_node; v.harbour_quality = h.harbour->harbor_quality; }
        v.name = place_names.coin(out.naming.of(v.culture_uid, town.civilization), role->second.concepts, names::salt_of(h.uid));
        v.name_provenance = "synthesized: hamlets carry no names; FWGC++ lexicon roots of the role's concepts";
        town.villages.push_back(v.id);
        if (v.harbour) harbours[core->second->uid].push_back(out.villages.size());
        village_index[h.uid] = out.villages.size();
        out.villages.push_back(v);
    }
    // Castles: owned through the garrison city, or through the realm of the fortress's culture.
    for (const auto* fp : uid_order(world.castles)) {
        const auto& f = *fp;
        Castle c;
        c.id = reg.at("castle", f.uid); c.uid = f.uid; c.culture_uid = f.culture_id;
        if (!f.culture_id.empty()) culture_of(f.culture_id, "Fortress " + f.uid);
        const source::Realm* owner = nullptr;
        if (f.garrisoned && !f.core_uid.empty()) {
            const auto city = city_by_uid.find(f.core_uid);
            if (city == city_by_uid.end()) refuse("broken_join", "Fortress " + f.uid + " names unknown garrison city " + f.core_uid);
            owner = realm_of_city.at(f.core_uid);
            auto& town = out.towns[town_index.at(f.core_uid)];
            c.garrison_town_id = town.id; town.castles.push_back(c.id);
            c.name = place_names.coin(out.naming.of(f.culture_id, town.civilization), content.places.at("castle"), names::salt_of(f.uid));
            c.owner_provenance = "garrison city " + f.core_uid + " is held by realm " + owner->uid;
        } else {
            if (f.culture_id.empty()) refuse("broken_join", "Ungarrisoned fortress " + f.uid + " has no culture_id, so no realm can own it");
            std::vector<const source::Realm*> matches;
            for (const auto& r : world.realms) if (r.culture_id == f.culture_id) matches.push_back(&r);
            if (matches.size() != 1) refuse("broken_join", "Ungarrisoned fortress " + f.uid + " culture " + f.culture_id + " matches " + std::to_string(matches.size()) + " realms (needs exactly one)");
            owner = matches.front();
            c.name = place_names.coin(out.naming.of(f.culture_id, owner->civilization_id), content.places.at("castle"), names::salt_of(f.uid));
            c.owner_provenance = "no garrison city: the realm whose culture is " + f.culture_id;
        }
        c.name_provenance = "synthesized: fortresses carry no names; FWGC++ lexicon roots";
        c.owner_clan_id = out.houses[house_index.at(owner->uid)].id;
        out.houses[house_index.at(owner->uid)].fiefs.push_back(c.id);
        c.kingdom_id = out.kingdoms[kingdom_index.at(owner->uid)].id;
        out.kingdoms[kingdom_index.at(owner->uid)].castles.push_back(c.id);
        out.castles.push_back(c);
    }
    // Heroes. Every home reference must be a generated city; notables attach by their occupation's settlement kind.
    std::map<std::string, std::string> hero_of_person;
    for (const auto& p : world.people) {
        const auto status = content.hero_statuses.find(p.status);
        if (status == content.hero_statuses.end()) refuse("unmapped_category", "Hero status '" + p.status + "' (" + p.uid + ") has no mapping");
        const auto role = content.hero_roles.find(p.role);
        if (role == content.hero_roles.end()) refuse("unmapped_category", "Hero role '" + p.role + "' (" + p.uid + ") has no mapping");
        if (!status->second) { out.not_instantiated.push_back(p.uid); continue; }
        Hero h;
        h.uid = p.uid; h.role = p.role; h.civilization = p.civilization_id; h.race_id = p.race_id; h.provenance = "source";
        h.given = p.name; h.epithet = p.epithet;
        // The population bucket: the role's, or its council seat's (campaign-content.json heroRoles bucket, seatBuckets).
        h.bucket = role->second.bucket;
        if (const auto seat = role->second.seat_buckets.find(p.seat); !p.seat.empty() && seat != role->second.seat_buckets.end()) h.bucket = seat->second;
        h.presentation = presentation(p.civilization_id, "hero " + p.uid);
        h.source_name = p.display_name.empty() ? p.name : p.display_name;
        if ((!p.home_kind.empty() || !p.home_uid.empty()) && (p.home_kind != "city" || !town_index.count(p.home_uid)))
            refuse("broken_join", "Hero " + p.uid + " home (" + p.home_kind + " '" + p.home_uid + "') is not a generated city");
        const bool without_realm = !p.realm_uid && !role->second.without_realm.empty();
        h.occupation = without_realm ? role->second.without_realm : role->second.occupation;
        const auto& rule = content.occupations.at(h.occupation);
        h.affiliation = rule.affiliation;
        if (!p.realm_uid && h.affiliation == "clan") refuse("broken_join", "Hero " + p.uid + " (" + p.role + ") needs a realm for a clan and the policy has no withoutRealm rule");
        const source::Realm* realm = nullptr;
        if (p.realm_uid) {
            const auto it = realm_by_uid.find(*p.realm_uid);
            if (it == realm_by_uid.end()) refuse("broken_join", "Hero " + p.uid + " names unknown realm " + *p.realm_uid);
            realm = it->second;
        }
        if (without_realm) append(h.provenance, "role " + p.role + " without a realm uses the policy's withoutRealm occupation");
        h.id = reg.assign("hero", p.uid);
        if (h.affiliation == "clan") {
            auto& clan = out.houses[house_index.at(realm->uid)];
            h.clan_id = clan.id; h.kingdom_id = clan.kingdom_id; clan.members.push_back(h.id);
        } else if (h.affiliation == "settlement") {
            if (rule.settlement == "village" && p.presence_kind == "hamlet") {
                const auto v = village_index.find(p.presence_uid);
                if (v == village_index.end()) refuse("broken_join", "Hero " + p.uid + " is present at unknown hamlet '" + p.presence_uid + "'");
                h.settlement_id = out.villages[v->second].id; h.settlement_kind = "village";
            } else if (!p.home_uid.empty()) {
                h.settlement_id = out.towns[town_index.at(p.home_uid)].id; h.settlement_kind = "town";
                if (rule.settlement == "village") append(h.provenance, "no hamlet presence: attached to its home town");
            } else if (realm) {
                h.settlement_id = reg.at("town", realm->capital_uid); h.settlement_kind = "town";
                append(h.provenance, "no home city: attached to its realm's capital");
            } else {
                refuse("broken_join", "Notable " + p.uid + " (" + p.role + ") has no home city, hamlet presence or realm, and the policy has no withoutRealm rule");
            }
            if (realm) h.kingdom_id = out.kingdoms[kingdom_index.at(realm->uid)].id;
        } else if (realm) {
            h.kingdom_id = out.kingdoms[kingdom_index.at(realm->uid)].id;  // a realm-affiliated wanderer, still clanless
        }
        hero_of_person[p.uid] = h.id;
        out.heroes.push_back(h);
    }
    // Hero display names in source-UID order: the source display name, cleaned.
    {
        std::vector<Hero*> order;
        for (auto& h : out.heroes) order.push_back(&h);
        std::sort(order.begin(), order.end(), [](const Hero* a, const Hero* b) { return a->uid < b->uid; });
        for (auto* h : order) {
            h->name = hero_names.take(h->source_name, out.naming.of("", h->civilization), names::salt_of(h->uid));
            if (h->name != h->source_name) append(h->provenance, "name: " + cleaned(h->source_name, h->name).substr(8));
        }
    }
    // Regents' personal names (MMR-286): FWGC++ residents of the realm's civilization (npcs.people of the naming posts) in uid order from
    // a start drawn by the realm, the first whose name no hero carries, with the resident's sex; a two-root lexicon compound without one.
    std::map<std::string, std::vector<const source::Npc*>> residents;
    for (const auto& n : world.residents) residents[n.civilization_id].push_back(&n);
    for (auto& [civ, list] : residents) std::sort(list.begin(), list.end(), [](const source::Npc* a, const source::Npc* b) { return a->uid < b->uid; });
    std::set<std::string> given_taken;
    for (const auto& h : out.heroes) given_taken.insert(names::strip_age_tag(h.given));
    auto regent_name = [&](const source::Realm& r, Hero& regent) {
        const auto& heritage = out.naming.of("", r.civilization_id);
        const std::uint64_t salt = names::salt_of("regent|" + r.uid);
        const auto it = residents.find(r.civilization_id);
        if (it != residents.end())
            for (std::size_t i = 0; i < it->second.size(); ++i) {
                const auto* n = it->second[(salt + i) % it->second.size()];
                const auto given = names::strip_age_tag(n->name);
                if (given.empty() || given_taken.count(given) || hero_names.taken(given)) continue;
                regent.given = given; regent.female = n->known_sex && n->female;
                regent.name = hero_names.take(given, heritage, salt);
                regent.provenance += "; named after FWGC++ resident " + n->uid;
                given_taken.insert(given);
                return;
            }
        names::Unique roots(content.cleaner);
        for (int d = 0;; ++d) {
            const auto given = roots.coin(heritage, {"king", "realm"}, salt + static_cast<std::uint64_t>(d));
            if (given_taken.count(given) || hero_names.taken(given)) continue;
            regent.given = given; regent.name = hero_names.take(given, heritage, salt); given_taken.insert(given);
            break;
        }
        regent.provenance += "; named from FWGC++ lexicon roots (the civilization has no residents)";
    };
    // Rulers: the sovereign, or a synthesized regent when the source has none.
    for (const auto* rp : uid_order(world.realms)) {
        const auto& r = *rp;
        auto& k = out.kingdoms[kingdom_index.at(r.uid)];
        auto& clan = out.houses[house_index.at(r.uid)];
        std::string ruler;
        if (r.sovereign_uid) {
            const auto person = person_by_uid.find(*r.sovereign_uid);
            if (person == person_by_uid.end()) refuse("broken_join", "Realm " + r.uid + " sovereign " + *r.sovereign_uid + " is not a known person");
            const auto hero = hero_of_person.find(*r.sovereign_uid);
            if (hero == hero_of_person.end() || person->second->realm_uid != r.uid)
                refuse("sovereign_mismatch", "Realm " + r.uid + " sovereign " + *r.sovereign_uid + " is not an instantiated member of the realm");
            ruler = hero->second;
            for (auto& h : out.heroes) if (h.id == ruler && h.affiliation != "clan") refuse("sovereign_mismatch", "Sovereign " + *r.sovereign_uid + " does not map to a clan Lord");
        } else {
            Hero regent;
            regent.uid = "regent:" + r.uid; regent.id = reg.assign("hero", regent.uid);
            regent.role = "regent"; regent.occupation = "Lord"; regent.affiliation = "clan"; regent.civilization = r.civilization_id;
            regent.race_id = r.parent_race_id; regent.presentation = k.presentation; regent.bucket = "house"; regent.synthesized = true;
            regent.clan_id = clan.id; regent.kingdom_id = k.id; regent.provenance = "synthesized: realm " + r.uid + " has no sovereign";
            regent_name(r, regent);
            clan.members.push_back(regent.id);
            ruler = regent.id;
            out.heroes.push_back(regent);
        }
        k.ruler_id = ruler; clan.leader_id = ruler;
        for (auto& h : out.heroes) if (h.id == ruler) h.ruler = true;
    }
    // Ports: the harbour villages of each town.
    for (auto& t : out.towns) {
        const auto it = harbours.find(t.uid);
        if (it == harbours.end()) continue;
        Port p; p.town_id = t.id;
        for (std::size_t i : it->second) {
            const auto& v = out.villages[i];
            p.village_ids.push_back(v.id);
            if (v.sea_node) p.sea_nodes.push_back(*v.sea_node);
            p.best_quality = std::max(p.best_quality, v.harbour_quality);
        }
        t.port = true;
        out.ports.push_back(p);
    }
    // Ruins (MMR-286): the cleaned names the map's markers and the ruins page show, unique among the ruins, in uid order.
    for (const auto* rp : uid_order(world.ruins)) {
        Ruin x; x.uid = rp->uid; x.source_name = rp->name;
        x.name = rp->name.empty() ? ruin_names.coin(out.naming.of("", rp->civilization_id), content.places.at("town"), names::salt_of(rp->uid))
                                  : ruin_names.take(rp->name, out.naming.of("", rp->civilization_id), names::salt_of(rp->uid));
        out.ruins.push_back(x);
    }
    // Typed slots: decided here (resolved) or owned by a later ticket (pending).
    for (auto& k : out.kingdoms) k.slots = {{"culture", "resolved", "MMR-27", k.presentation}, {"bannerAndColors", "pending", "MMR-31", ""}, {"policies", "pending", "MMR-31", ""}, {"troopTreeAndRecruitTemplates", "pending", "MMR-32", ""}};
    for (auto& c : out.houses) c.slots = {{"tier", "pending", "MMR-31", ""}, {"banner", "pending", "MMR-31", ""}};
    for (auto& h : out.heroes) h.slots = {{"occupation", "resolved", "MMR-27", h.occupation}, {"characterTemplate", "pending", "MMR-32", ""}, {"ageTraitsAndFamily", "pending", "MMR-32", ""}};
    for (auto& t : out.towns) t.slots = {{"culture", "resolved", "MMR-27", t.presentation}, {"position", "pending", "MMR-28", ""}, {"prototypeAndScenes", "pending", "MMR-42", ""}, {"prosperityAndEconomy", "pending", "MMR-33", ""}, {"garrison", "pending", "MMR-32", ""}};
    for (auto& v : out.villages) v.slots = {{"villageType", "pending", "MMR-33", v.production}, {"hearth", "pending", "MMR-33", ""}, {"position", "pending", "MMR-28", ""}};
    for (auto& c : out.castles) c.slots = {{"position", "pending", "MMR-28", ""}, {"prototypeAndScenes", "pending", "MMR-42", ""}, {"garrison", "pending", "MMR-32", ""}};
    for (auto& p : out.ports) p.slots = {{"portSite", "pending", "MMR-28", ""}, {"navalFacility", "pending", "MMR-35", ""}, {"shipyardAndFleet", "pending", "MMR-35", ""}};
    // Minima from installed-game evidence.
    for (const auto& min : content.minima) {
        RuleResult r{min.rule, min.evidence, "ok", min.minimum, 0};
        auto smallest = [](std::int64_t current, std::int64_t value, bool first) { return first ? value : std::min(current, value); };
        bool first = true;
        if (min.rule == "towns") r.actual = static_cast<std::int64_t>(out.towns.size());
        else if (min.rule == "kingdomTown") for (const auto& k : out.kingdoms) { r.actual = smallest(r.actual, static_cast<std::int64_t>(k.towns.size()), first); first = false; }
        else if (min.rule == "villageBound") { r.actual = 1; for (const auto& v : out.villages) if (v.bound_id.empty()) r.actual = 0; }
        else if (min.rule == "settlementOwner") { r.actual = 1; for (const auto& t : out.towns) if (t.owner_clan_id.empty()) r.actual = 0; for (const auto& c : out.castles) if (c.owner_clan_id.empty()) r.actual = 0; }
        else if (min.rule == "clanLeader") { r.actual = 1; for (const auto& c : out.houses) if (c.leader_id.empty() || c.home_id.empty()) r.actual = 0; }
        else if (min.rule == "kingdomRuler") { r.actual = 1; for (const auto& k : out.kingdoms) if (k.ruler_id.empty()) r.actual = 0; }
        else if (min.rule == "playerAliases") { r.status = "deferred"; r.actual = 0; }  // the registry creates them (MMR-30)
        if (r.status == "ok" && r.actual < r.minimum) refuse("invalid_world", "Rule '" + r.rule + "' needs at least " + std::to_string(r.minimum) + ", the world has " + std::to_string(r.actual) + " (" + r.evidence + ")");
        out.rules.push_back(r);
    }
    out.id_map = reg.entries();
    const auto problems = closure_problems(out);
    if (!problems.empty()) refuse("broken_join", "Compiled records are not closed: " + problems.front());
    return out;
}

std::vector<std::string> closure_problems(const Compiled& c) {
    std::vector<std::string> problems;
    std::set<std::string> kingdoms, houses, clans, heroes, towns, castles, fiefs, villages;
    std::map<std::string, const std::vector<std::string>*> clan_members;   // house, company or outlaw clan id -> its members
    std::map<std::string, std::string> house_kingdom;
    for (const auto& k : c.kingdoms) kingdoms.insert(k.id);
    for (const auto& x : c.houses) { houses.insert(x.id); clans.insert(x.id); clan_members[x.id] = &x.members; house_kingdom[x.id] = x.kingdom_id; }
    for (const auto* list : {&c.companies, &c.outlaws}) for (const auto& x : *list) { clans.insert(x.id); clan_members[x.id] = &x.members; }
    for (const auto& h : c.heroes) heroes.insert(h.id);
    for (const auto& t : c.towns) { towns.insert(t.id); fiefs.insert(t.id); }
    for (const auto& x : c.castles) { castles.insert(x.id); fiefs.insert(x.id); }
    for (const auto& v : c.villages) villages.insert(v.id);
    auto need = [&](const std::set<std::string>& set, const std::string& id, const std::string& what) {
        if (id.empty() || !set.count(id)) problems.push_back(what + " -> '" + id + "'");
    };
    for (const auto& k : c.kingdoms) {
        need(houses, k.ruling_house_id, "kingdom " + k.id + " ruling house"); need(heroes, k.ruler_id, "kingdom " + k.id + " ruler"); need(towns, k.capital_id, "kingdom " + k.id + " capital");
        if (std::find(k.towns.begin(), k.towns.end(), k.capital_id) == k.towns.end()) problems.push_back("kingdom " + k.id + " capital is not one of its towns");
        for (const auto& x : c.houses) if (x.id == k.ruling_house_id && x.leader_id != k.ruler_id) problems.push_back("kingdom " + k.id + " ruler does not lead its ruling house");
        for (const auto& t : k.towns) need(towns, t, "kingdom " + k.id + " town");
        for (const auto& x : k.castles) need(castles, x, "kingdom " + k.id + " castle");
    }
    for (const auto& x : c.houses) {
        need(kingdoms, x.kingdom_id, "house " + x.id + " kingdom"); need(heroes, x.leader_id, "house " + x.id + " leader"); need(fiefs, x.home_id, "house " + x.id + " home");
        if (std::find(x.members.begin(), x.members.end(), x.leader_id) == x.members.end()) problems.push_back("house " + x.id + " leader is not a member");
        if (std::find(x.fiefs.begin(), x.fiefs.end(), x.home_id) == x.fiefs.end()) problems.push_back("house " + x.id + " home is not one of its fiefs");
    }
    for (const auto* list : {&c.companies, &c.outlaws})
        for (const auto& x : *list) {
            need(heroes, x.leader_id, "minor clan " + x.id + " leader"); need(towns, x.home_id, "minor clan " + x.id + " home");
            if (std::find(x.members.begin(), x.members.end(), x.leader_id) == x.members.end()) problems.push_back("minor clan " + x.id + " leader is not a member");
            for (const auto& k : x.wars) need(kingdoms, k, "outlaw clan " + x.id + " war");
        }
    for (const auto& w : c.wars) { need(kingdoms, w.a, "war"); need(kingdoms, w.b, "war"); }
    for (const auto& h : c.heroes) {
        if (h.affiliation == "clan") {
            need(clans, h.clan_id, "hero " + h.id + " clan");
            const auto it = clan_members.find(h.clan_id);
            if (h.alive && it != clan_members.end() && std::find(it->second->begin(), it->second->end(), h.id) == it->second->end()) problems.push_back("hero " + h.id + " is not listed by its clan");
        }
        if (h.affiliation == "settlement") need(h.settlement_kind == "village" ? villages : towns, h.settlement_id, "hero " + h.id + " " + h.settlement_kind);
        if (!h.kingdom_id.empty()) need(kingdoms, h.kingdom_id, "hero " + h.id + " kingdom");
        for (const auto* kin : {&h.spouse_id, &h.father_id, &h.mother_id}) if (!kin->empty()) need(heroes, *kin, "hero " + h.id + " kin");
    }
    std::map<std::string, const std::vector<std::string>*> listed;   // fortification id -> its village list
    for (const auto& t : c.towns) {
        need(kingdoms, t.kingdom_id, "town " + t.id + " kingdom"); need(houses, t.owner_clan_id, "town " + t.id + " owner");
        if (house_kingdom.count(t.owner_clan_id) && house_kingdom.at(t.owner_clan_id) != t.kingdom_id) problems.push_back("town " + t.id + " owner is not a house of its kingdom");
        for (const auto& v : t.villages) need(villages, v, "town " + t.id + " village");
        for (const auto& x : t.castles) need(castles, x, "town " + t.id + " castle");
        listed[t.id] = &t.villages;
    }
    for (const auto& x : c.castles) {
        need(houses, x.owner_clan_id, "castle " + x.id + " owner"); need(kingdoms, x.kingdom_id, "castle " + x.id + " kingdom");
        if (house_kingdom.count(x.owner_clan_id) && house_kingdom.at(x.owner_clan_id) != x.kingdom_id) problems.push_back("castle " + x.id + " owner is not a house of its kingdom");
        if (!x.garrison_town_id.empty()) need(towns, x.garrison_town_id, "castle " + x.id + " garrison town");
        for (const auto& v : x.villages) need(villages, v, "castle " + x.id + " village");
        listed[x.id] = &x.villages;
    }
    // A village is bound to a town or a castle (bound_kind) that lists it; its source town is a town.
    for (const auto& v : c.villages) {
        if (v.bound_kind != "town" && v.bound_kind != "castle") problems.push_back("village " + v.id + " bound kind '" + v.bound_kind + "'");
        need(v.bound_kind == "castle" ? castles : towns, v.bound_id, "village " + v.id + " bound " + v.bound_kind);
        need(towns, v.source_town, "village " + v.id + " source town");
        const auto it = listed.find(v.bound_id);
        if (it != listed.end() && std::find(it->second->begin(), it->second->end(), v.id) == it->second->end()) problems.push_back("village " + v.id + " is not listed by its bound " + v.bound_kind);
    }
    for (const auto& p : c.ports) {
        need(towns, p.town_id, "port town");
        for (const auto& v : p.village_ids) need(villages, v, "port " + p.town_id + " village");
    }
    return problems;
}

void exclude(Compiled& compiled, const std::string& id, const std::string& reason) {
    if (reason.empty()) refuse("missing_field", "An exclusion needs a reason");
    for (const auto& t : compiled.towns) if (t.id == id) refuse("preserved_settlement", "Town " + id + " is generated and preserved; placement must find it a site");
    Compiled c = compiled;  // committed only when closure still holds
    const auto village = std::find_if(c.villages.begin(), c.villages.end(), [&](const Village& v) { return v.id == id; });
    const auto castle = std::find_if(c.castles.begin(), c.castles.end(), [&](const Castle& x) { return x.id == id; });
    if (village == c.villages.end() && castle == c.castles.end()) refuse("broken_join", "No village or castle " + id + " to exclude");
    auto drop = [&](std::vector<std::string>& list) { list.erase(std::remove(list.begin(), list.end(), id), list.end()); };
    if (village != c.villages.end()) {
        const Village removed = *village;
        c.villages.erase(village);
        for (auto& t : c.towns) drop(t.villages);
        for (auto& x : c.castles) drop(x.villages);
        // Only a port this removal empties is erased (RT-12): a forced port that never had a village keeps its record.
        for (auto it = c.ports.begin(); it != c.ports.end();) {
            const bool held = std::find(it->village_ids.begin(), it->village_ids.end(), id) != it->village_ids.end();
            drop(it->village_ids);
            if (held && it->village_ids.empty()) { for (auto& t : c.towns) if (t.id == it->town_id) t.port = false; it = c.ports.erase(it); } else ++it;
        }
        for (auto& h : c.heroes)
            if (h.settlement_id == id) { h.settlement_id = removed.source_town; h.settlement_kind = "town"; append(h.provenance, "village " + id + " excluded (" + reason + "): moved to its town"); }
        c.exclusions.push_back({id, "village", removed.uid, reason});
    } else {
        const Castle removed = *castle;
        c.castles.erase(castle);
        for (auto& t : c.towns) drop(t.castles);
        for (auto& k : c.kingdoms) drop(k.castles);
        for (auto& x : c.houses) drop(x.fiefs);
        c.exclusions.push_back({id, "castle", removed.uid, reason});
    }
    const auto problems = closure_problems(c);
    if (!problems.empty()) refuse("broken_join", "Excluding " + id + " (" + reason + ") breaks closure: " + problems.front());
    compiled = std::move(c);
}

std::string id_map_document(const Compiled& c) {
    Value map = Value::object();
    map.set("schema", str("mnbl.id-map")); map.set("version", Value::integer(1));
    map.set("layoutSeed", str(c.layout_seed)); map.set("sourceIdentity", str(c.source_identity)); map.set("worldSha256", str(c.world_sha256)); map.set("prefix", str(c.prefix));
    map.set("algorithm", str("id = prefix + '_' + kind code + '_' + hex(sha256('mnbl.stable-id/1\\n' + kind + '\\n' + uid))[0:10]; prefix = 'mnbl_' + hex(sha256('mnbl.world-prefix/2\\n' + sourceIdentity + '\\n' + worldSha256))[0:8]"));
    Value entries; entries.kind = Value::Kind::Array;
    for (const auto& e : c.id_map) { Value v = Value::object(); v.set("id", str(e.id)); v.set("kind", str(e.kind)); v.set("uid", str(e.uid)); entries.items.push_back(v); }
    map.set("entries", entries);
    return cjson::canonical(map);
}

std::string records_document(const Compiled& c, const ContentPolicy& content, const CulturePolicy& cultures) {
    const std::string map_text = id_map_document(c);
    Value doc = Value::object();
    doc.set("schema", str("mnbl.campaign-records")); doc.set("version", Value::integer(2));
    doc.set("layoutSeed", str(c.layout_seed)); doc.set("worldSha256", str(c.world_sha256)); doc.set("idMapSha256", str(cjson::sha256_hex(map_text)));
    doc.set("contentPolicySha256", str(content.sha256)); doc.set("culturePolicySha256", str(cultures.sha256));
    Value producer = Value::object(); for (const auto& [k, v] : c.producer) producer.set(k, integer(v)); doc.set("producer", producer);
    Value list; list.kind = Value::Kind::Array;
    Value kingdoms = list, houses = list, companies = list, outlaws = list, wanderers = list, wars = list, heroes = list, towns = list, villages = list, castles = list,
          ports = list, rules = list, exclusions = list;
    for (const auto& k : c.kingdoms) {
        Value v = named(k.id, k.name, k.name == k.source_name ? "source" : names::strip_age_tag(k.source_name) == k.name ? "source; Age tag removed" : "source; Age tag removed, disambiguated with a lexicon suffix");
        if (k.name != k.source_name) v.set("sourceName", str(k.source_name));
        v.set("uid", str(k.uid)); v.set("englishName", str(k.english_name)); v.set("civilization", str(k.civilization));
        v.set("civilizationName", str(k.civilization_name)); v.set("parentRaceId", str(k.parent_race_id));
        v.set("cultureUid", str(k.culture_uid)); v.set("presentation", str(k.presentation)); v.set("rulingHouse", str(k.ruling_house_id)); v.set("ruler", str(k.ruler_id));
        v.set("capital", str(k.capital_id)); v.set("towns", strings(k.towns)); v.set("castles", strings(k.castles));
        v.set("memberRealms", strings(k.member_realms));
        Value steps = list;
        for (const auto& m : k.merge_steps) { Value s2 = Value::object(); s2.set("realm", str(m.realm)); s2.set("step", str(m.step)); if (m.crosses_water) s2.set("crossesWater", boolean(true)); steps.items.push_back(s2); }
        v.set("mergeSteps", steps);
        v.set("slots", slots(k.slots)); kingdoms.items.push_back(v);
    }
    for (const auto& x : c.houses) {
        Value v = named(x.id, x.name, x.provenance); v.set("uid", str(x.uid)); v.set("kingdom", str(x.kingdom_id)); v.set("leader", str(x.leader_id)); v.set("home", str(x.home_id));
        v.set("seat", str(x.seat_id)); v.set("fiefs", strings(x.fiefs)); v.set("origin", str(x.origin));
        if (!x.source_name.empty()) v.set("sourceName", str(x.source_name));
        v.set("cultureUid", str(x.culture_uid)); v.set("presentation", str(x.presentation)); v.set("members", strings(x.members)); v.set("slots", slots(x.slots)); houses.items.push_back(v);
    }
    auto minor = [&](const MinorClan& x, bool outlaw) {
        Value v = named(x.id, x.name, x.name_provenance); v.set("uid", str(x.uid)); v.set("home", str(x.home_id)); v.set("leader", str(x.leader_id));
        v.set("members", strings(x.members)); v.set("presentation", str(x.presentation)); v.set("cultureUid", str(x.culture_uid));
        if (outlaw) { v.set("kind", str(x.kind)); v.set("wars", strings(x.wars)); }
        if (!x.source_name.empty()) v.set("sourceName", str(x.source_name));
        v.set("partyTemplate", str(x.party_template)); v.set("characterTemplates", strings(x.character_templates)); v.set("tier", integer(x.tier));
        v.set("source", str(x.source)); v.set("slots", slots(x.slots));
        return v;
    };
    for (const auto& x : c.companies) companies.items.push_back(minor(x, false));
    for (const auto& x : c.outlaws) outlaws.items.push_back(minor(x, true));
    for (const auto& w : c.wanderers) {
        Value v = Value::object(); v.set("hero", str(w.hero)); v.set("presentation", str(w.presentation)); v.set("female", boolean(w.female));
        if (!w.epithet.empty()) v.set("epithet", str(w.epithet));
        wanderers.items.push_back(v);
    }
    for (const auto& w : c.wars) { Value v = Value::object(); v.set("a", str(w.a)); v.set("b", str(w.b)); v.set("partner", str(w.partner)); wars.items.push_back(v); }
    for (const auto& h : c.heroes) {
        Value v = named(h.id, h.name, h.provenance); v.set("uid", str(h.uid)); v.set("role", str(h.role));
        if (!h.source_name.empty() && h.source_name != h.name) v.set("sourceName", str(h.source_name)); v.set("occupation", str(h.occupation)); v.set("affiliation", str(h.affiliation));
        if (!h.clan_id.empty()) v.set("clan", str(h.clan_id));
        if (!h.settlement_id.empty()) { v.set("settlement", str(h.settlement_id)); v.set("settlementKind", str(h.settlement_kind)); }
        if (!h.kingdom_id.empty()) v.set("kingdom", str(h.kingdom_id));
        v.set("civilization", str(h.civilization)); v.set("raceId", str(h.race_id)); v.set("presentation", str(h.presentation));
        // The population fields (MMR-279), written once the population pass placed every hero.
        if (c.census) {
            v.set("bucket", str(h.bucket)); v.set("female", boolean(h.female)); v.set("age", integer(h.age)); v.set("alive", boolean(h.alive));
            if (!h.spouse_id.empty()) v.set("spouse", str(h.spouse_id));
            if (!h.father_id.empty()) v.set("father", str(h.father_id));
            if (!h.mother_id.empty()) v.set("mother", str(h.mother_id));
            if (h.synthesized) v.set("synthesized", boolean(true));
        }
        v.set("ruler", boolean(h.ruler)); v.set("slots", slots(h.slots)); heroes.items.push_back(v);
    }
    for (const auto& t : c.towns) {
        Value v = named(t.id, t.name, t.name_provenance); v.set("uid", str(t.uid)); v.set("civilization", str(t.civilization));
        if (!t.source_name.empty() && t.source_name != t.name) v.set("sourceName", str(t.source_name)); v.set("cultureUid", str(t.culture_uid));
        v.set("cultureProvenance", str(t.culture_provenance)); v.set("presentation", str(t.presentation));
        v.set("kingdom", str(t.kingdom_id)); v.set("owner", str(t.owner_clan_id)); v.set("cityClass", str(t.city_class)); v.set("capital", boolean(t.capital)); v.set("port", boolean(t.port));
        Value pop = Value::object(); pop.set("estimate", integer(t.population)); if (t.urban) pop.set("urban", integer(*t.urban)); if (t.rural) pop.set("rural", integer(*t.rural)); pop.set("use", str("metadata only (MMR-33 decides any economy mapping)"));
        v.set("population", pop); v.set("villages", strings(t.villages)); v.set("castles", strings(t.castles)); v.set("slots", slots(t.slots)); towns.items.push_back(v);
    }
    for (const auto& x : c.villages) {
        Value v = named(x.id, x.name, x.name_provenance); v.set("uid", str(x.uid)); v.set("bound", str(x.bound_id)); v.set("role", str(x.role)); v.set("production", str(x.production));
        v.set("boundKind", str(x.bound_kind)); v.set("sourceTown", str(x.source_town)); v.set("origin", str(x.origin));
        if (!x.tier.empty()) v.set("tier", str(x.tier));
        if (x.pinned) v.set("pinned", boolean(true));
        v.set("harbour", boolean(x.harbour)); if (x.sea_node) v.set("seaNode", integer(*x.sea_node)); v.set("cultureUid", str(x.culture_uid)); v.set("slots", slots(x.slots)); villages.items.push_back(v);
    }
    for (const auto& x : c.castles) {
        Value v = named(x.id, x.name, x.name_provenance); v.set("uid", str(x.uid)); v.set("owner", str(x.owner_clan_id)); v.set("ownerProvenance", str(x.owner_provenance)); v.set("kingdom", str(x.kingdom_id));
        if (!x.garrison_town_id.empty()) v.set("garrisonTown", str(x.garrison_town_id));
        v.set("villages", strings(x.villages));
        v.set("cultureUid", str(x.culture_uid)); v.set("slots", slots(x.slots)); castles.items.push_back(v);
    }
    for (const auto& p : c.ports) {
        Value v = Value::object(); v.set("town", str(p.town_id)); v.set("villages", strings(p.village_ids));
        Value nodes; nodes.kind = Value::Kind::Array; for (auto n : p.sea_nodes) nodes.items.push_back(integer(n)); v.set("seaNodes", nodes);
        v.set("bestHarbourQuality", number(p.best_quality));
        if (p.forced) v.set("forced", boolean(true));
        v.set("slots", slots(p.slots)); ports.items.push_back(v);
    }
    for (const auto& r : c.rules) { Value v = Value::object(); v.set("rule", str(r.rule)); v.set("status", str(r.status)); v.set("minimum", integer(r.minimum)); v.set("actual", integer(r.actual)); v.set("evidence", str(r.evidence)); rules.items.push_back(v); }
    for (const auto& e : c.exclusions) { Value v = Value::object(); v.set("id", str(e.id)); v.set("kind", str(e.kind)); v.set("uid", str(e.uid)); v.set("reason", str(e.reason)); exclusions.items.push_back(v); }
    doc.set("kingdoms", kingdoms); doc.set("wars", wars); doc.set("houses", houses); doc.set("mercenaryCompanies", companies); doc.set("outlawClans", outlaws);
    doc.set("wanderers", wanderers);
    // MMR-281: the runtime wanderers' names, written once the population pass ran.
    if (c.census) {
        Value reserve = list;
        for (const auto& r : c.wanderer_reserve) {
            Value v = Value::object(); v.set("name", str(r.name)); v.set("presentation", str(r.presentation)); v.set("civilization", str(r.civilization)); v.set("female", boolean(r.female));
            if (!r.epithet.empty()) v.set("epithet", str(r.epithet));
            reserve.items.push_back(v);
        }
        doc.set("wandererReserve", reserve);
    }
    // MMR-286: the ruins' cleaned names (the map's ruin markers and the ruins page), sourceName when it differs.
    Value ruins = list;
    for (const auto& r : c.ruins) {
        Value v = Value::object(); v.set("uid", str(r.uid)); v.set("name", str(r.name));
        if (r.source_name != r.name) v.set("sourceName", str(r.source_name));
        ruins.items.push_back(v);
    }
    doc.set("ruins", ruins);
    doc.set("heroes", heroes); doc.set("towns", towns); doc.set("villages", villages); doc.set("castles", castles); doc.set("ports", ports);
    doc.set("notInstantiated", strings(c.not_instantiated)); doc.set("rules", rules); doc.set("exclusions", exclusions);
    // The village quota (MMR-252), written by the travel compiler; the MMR-27 stage alone (--compile-campaign) writes the source binding.
    if (c.quota) {
        const auto& q = *c.quota;
        Value block = Value::object();
        block.set("townVillages", integer(q.town)); block.set("castleVillages", integer(q.castle));
        block.set("kept", integer(q.kept)); block.set("reassigned", integer(q.reassigned)); block.set("dropped", integer(q.dropped)); block.set("synthesized", integer(q.synthesized));
        block.set("protectedKept", integer(q.protected_kept)); block.set("clearanceMoves", integer(q.clearance_moves)); block.set("spacing", number(q.spacing));
        Value tiers = Value::object();
        for (const auto& [k, n] : q.tiers) tiers.set(k, integer(n));
        block.set("synthesizedByTier", tiers);
        Value exceptions = list;
        for (const auto& e : q.exceptions) { Value v = Value::object(); v.set("village", str(e.village)); v.set("fortification", str(e.fortification)); v.set("distance", number(e.distance)); v.set("reason", str(e.reason)); exceptions.items.push_back(v); }
        block.set("exceptions", exceptions);
        Value moved = list;
        for (const auto& m : q.moved) { Value v = Value::object(); v.set("hero", str(m.hero)); v.set("from", str(m.from)); v.set("to", str(m.to)); moved.items.push_back(v); }
        block.set("movedNotables", moved);
        block.set("passes", integer(q.passes)); block.set("stable", boolean(q.stable));
        doc.set("villageQuota", block);
    }
    // The population census (MMR-279), written by the population pass.
    if (c.census) {
        const auto& n = *c.census;
        Value block = Value::object();
        auto ints = [&](const std::map<std::string, std::int64_t>& m) { Value o = Value::object(); for (const auto& [k, x] : m) o.set(k, integer(x)); return o; };
        for (const auto& [k, x] : std::map<std::string, std::int64_t>{
                 {"fiefs", n.fiefs}, {"towns", n.towns}, {"castles", n.castles}, {"kingdoms", n.kingdoms}, {"houses", n.houses}, {"adultLords", n.adult_lords},
                 {"minors", n.minors}, {"deadParents", n.dead_parents}, {"engineChildren", n.engine_children}, {"mercenaryCompanies", n.companies},
                 {"companyMembers", n.company_members}, {"outlawClans", n.outlaws}, {"outlawMembers", n.outlaw_members}, {"wanderers", n.wanderers},
                 {"wanderersPlanned", n.wanderers_planned}, {"notables", n.notables}, {"notablesBounded", n.notables_bounded}, {"sourceHeroes", n.source_heroes},
                 {"unassigned", n.unassigned}, {"movedNotables", n.moved_notables}})
            block.set(k, integer(x));
        block.set("buckets", ints(n.buckets)); block.set("synthesized", ints(n.synthesized)); block.set("targets", ints(n.targets));
        block.set("engineChildrenByHouse", ints(n.engine_children_by_house));
        Value overflow = list;
        for (const auto& o : n.overflow) { Value v = Value::object(); v.set("hero", str(o.hero)); v.set("from", str(o.from)); v.set("to", str(o.to)); v.set("reason", str(o.reason)); overflow.items.push_back(v); }
        block.set("overflow", overflow);
        block.set("notes", strings(n.notes));
        doc.set("census", block);
    }
    Value counts = Value::object();
    counts.set("kingdoms", integer(static_cast<std::int64_t>(c.kingdoms.size()))); counts.set("houses", integer(static_cast<std::int64_t>(c.houses.size())));
    counts.set("mercenaryCompanies", integer(static_cast<std::int64_t>(c.companies.size()))); counts.set("outlawClans", integer(static_cast<std::int64_t>(c.outlaws.size())));
    counts.set("wanderers", integer(static_cast<std::int64_t>(c.wanderers.size()))); counts.set("wars", integer(static_cast<std::int64_t>(c.wars.size())));
    counts.set("heroes", integer(static_cast<std::int64_t>(c.heroes.size()))); counts.set("towns", integer(static_cast<std::int64_t>(c.towns.size())));
    counts.set("villages", integer(static_cast<std::int64_t>(c.villages.size()))); counts.set("castles", integer(static_cast<std::int64_t>(c.castles.size())));
    counts.set("ports", integer(static_cast<std::int64_t>(c.ports.size()))); counts.set("notInstantiated", integer(static_cast<std::int64_t>(c.not_instantiated.size())));
    counts.set("regents", integer(static_cast<std::int64_t>(std::count_if(c.heroes.begin(), c.heroes.end(), [](const Hero& h) { return h.role == "regent"; }))));
    doc.set("counts", counts);
    return cjson::canonical(doc);
}
int compile_campaign_main(int argc, char** argv) {
    const char* usage = "Usage: worldbridge --compile-campaign SPLIT --contract FILE --content FILE --cultures FILE --out NEW_DIRECTORY";
    if (argc != 11) throw std::runtime_error(usage);
    std::map<std::string, std::string> o;
    for (int i = 3; i + 1 < argc; i += 2) {
        const std::string key = argv[i];
        static const std::set<std::string> known{"--contract", "--content", "--cultures", "--out"};
        if (!known.count(key)) throw std::runtime_error("Unknown option " + key + "\n" + usage);
        if (!o.emplace(key, argv[i + 1]).second) throw std::runtime_error("Repeated option " + key);
    }
    const fs::path output = fs::absolute(o["--out"]);
    fs::path pending = output; pending += ".partial";
    if (fs::exists(output)) throw std::runtime_error("Output must not already exist");
    if (fs::exists(pending)) throw std::runtime_error("A previous incomplete output exists: " + pending.string());
    const auto world = source::read_source(argv[2], source::load_compatibility(o["--contract"]));
    const auto content = load_content(o["--content"]);
    const auto cultures = load_cultures(o["--cultures"]);
    const auto c = compile(world, content, cultures, cjson::sha256_file_hex(fs::path(argv[2]) / "world.json"));
    const std::string map_text = id_map_document(c);
    const std::string doc_text = records_document(c, content, cultures);

    fs::create_directories(pending);
    write(pending / "id-map.json", map_text);
    write(pending / "campaign.json", doc_text);
    fs::rename(pending, output);
    Value report = Value::object();
    report.set("idMapSha256", str(cjson::sha256_hex(map_text))); report.set("campaignSha256", str(cjson::sha256_hex(doc_text)));
    Value counts = Value::object();
    for (const auto& [name, n] : std::map<std::string, std::size_t>{{"kingdoms", c.kingdoms.size()}, {"houses", c.houses.size()}, {"heroes", c.heroes.size()}, {"towns", c.towns.size()},
                                                                      {"villages", c.villages.size()}, {"castles", c.castles.size()}, {"ports", c.ports.size()}, {"notInstantiated", c.not_instantiated.size()}})
        counts.set(name, integer(static_cast<std::int64_t>(n)));
    counts.set("regents", integer(static_cast<std::int64_t>(std::count_if(c.heroes.begin(), c.heroes.end(), [](const Hero& h) { return h.role == "regent"; }))));
    report.set("counts", counts);
    std::cout << cjson::canonical(report) << std::endl;
    return 0;
}

}  // namespace worldbridge::campaign
