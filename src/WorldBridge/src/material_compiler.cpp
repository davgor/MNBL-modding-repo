#include "worldbridge/material_compiler.hpp"
#include <algorithm>
#include <cmath>
#include <cstdlib>
#include <set>
#include "worldbridge/canonical_json.hpp"

namespace worldbridge::terrain {
namespace {
using cjson::Value;
using json::Error;
using json::Location;

Location nowhere() { return Location{"", 0, 1, 1, "$"}; }

// The surface encoder's rounding (surface.cpp even_round): half to even, independent of the FP environment.
double even_round(double x) {
    const double f = std::floor(x), d = x - f;
    return d < .5 ? f : d > .5 ? f + 1 : std::fmod(f, 2) == 0 ? f : f + 1;
}

void only(const Value& object, std::initializer_list<const char*> keys, const std::string& what) {
    if (object.kind != Value::Kind::Object) throw Error("wrong_type", what + " must be an object", object.at);
    std::set<std::string> allowed(keys.begin(), keys.end());
    for (const auto& m : object.members)
        if (!allowed.count(m.first)) throw Error("unknown_field", what + " has unknown member '" + m.first + "'", m.second.at);
}
const Value& need(const Value& object, const char* key, Value::Kind kind, const std::string& what) {
    const Value* v = object.find(key);
    if (!v) throw Error("missing_field", what + " lacks '" + key + "'", object.at);
    if (v->kind != kind) throw Error("wrong_type", what + " '" + key + "' has the wrong type", v->at);
    return *v;
}
double real(const Value& object, const char* key, const std::string& what) {
    const double d = std::strtod(need(object, key, Value::Kind::Number, what).text.c_str(), nullptr);
    if (!std::isfinite(d)) throw Error("nonfinite_number", what + " '" + key + "' is not finite", object.find(key)->at);
    return d;
}
std::int64_t whole(const Value& object, const char* key, const std::string& what) {
    const Value& v = need(object, key, Value::Kind::Number, what);
    if (v.text.find_first_not_of("-0123456789") != std::string::npos) throw Error("wrong_type", what + " '" + key + "' must be an integer", v.at);
    return std::strtoll(v.text.c_str(), nullptr, 10);
}
}  // namespace

MaterialPolicy load_materials(const fs::path& file) {
    const Value doc = cjson::parse(file);
    only(doc, {"schema", "version", "channels", "biomes", "waterTypes", "river", "landforms", "intents", "riverOverridesLand", "slopeRock", "coastEpsilon", "notes"}, "Material policy");
    if (need(doc, "schema", Value::Kind::String, "Material policy").text != "mnbl.terrain-materials" || need(doc, "version", Value::Kind::Number, "Material policy").text != "1")
        throw Error("unsupported_version", "Material policy must be mnbl.terrain-materials version 1", doc.at);
    if (const Value* n = doc.find("notes"); n && n->kind != Value::Kind::String) throw Error("wrong_type", "Material policy notes must be a string", n->at);
    MaterialPolicy p;
    for (const auto& c : need(doc, "channels", Value::Kind::Array, "Material policy").items) {
        if (c.kind != Value::Kind::String) throw Error("wrong_type", "channels must be strings", c.at);
        p.channels.push_back(c.text);
    }
    if (p.channels.size() != 2) throw Error("out_of_range", "Material policy v1 maps exactly the two proven template layers", doc.find("channels")->at);
    const Value& intents = need(doc, "intents", Value::Kind::Object, "Material policy");
    only(intents, {"land", "ocean", "lake", "river"}, "intents");
    p.land = static_cast<int>(whole(intents, "land", "intents")); p.ocean = static_cast<int>(whole(intents, "ocean", "intents"));
    p.lake = static_cast<int>(whole(intents, "lake", "intents")); p.river = static_cast<int>(whole(intents, "river", "intents"));
    const std::set<int> distinct{p.land, p.ocean, p.lake, p.river};
    if (distinct.size() != 4 || *distinct.begin() < 0 || *distinct.rbegin() > 255) throw Error("out_of_range", "intent values must be four distinct bytes", intents.at);
    auto intent_of = [&](const Value& v, const std::string& what) {
        const auto& name = need(v, "intent", Value::Kind::String, what).text;
        if (name == "land") return p.land; if (name == "ocean") return p.ocean; if (name == "lake") return p.lake; if (name == "river") return p.river;
        if (name == "none") return -1;
        throw Error("unmapped_category", what + " names unknown intent '" + name + "'", v.find("intent")->at);
    };
    for (const auto& b : need(doc, "biomes", Value::Kind::Array, "Material policy").items) {
        only(b, {"id", "core", "rock", "note"}, "biomes[]");
        const auto id = whole(b, "id", "biomes[]");
        const auto rock = whole(b, "rock", "biomes[]");
        if (rock < 0 || rock > 255) throw Error("out_of_range", "biome rock weight must be 0..255", b.find("rock")->at);
        if (!p.biome_core.emplace(id, need(b, "core", Value::Kind::String, "biomes[]").text).second) throw Error("duplicate_id", "duplicate biome id " + std::to_string(id), b.at);
        p.biome_rock[id] = static_cast<int>(rock);
    }
    for (const auto& w : need(doc, "waterTypes", Value::Kind::Array, "Material policy").items) {
        only(w, {"code", "name", "intent"}, "waterTypes[]");
        const int code = static_cast<int>(whole(w, "code", "waterTypes[]"));
        const int value = intent_of(w, "waterTypes[]");
        if (value < 0) throw Error("unmapped_category", "a water class needs a land, ocean or lake intent", w.at);
        if (!p.water_intent.emplace(code, value).second) throw Error("duplicate_id", "duplicate water_type code", w.at);
    }
    for (const auto& r : need(doc, "river", Value::Kind::Array, "Material policy").items) {
        only(r, {"code", "intent"}, "river[]");
        if (!p.river_intent.emplace(static_cast<int>(whole(r, "code", "river[]")), intent_of(r, "river[]")).second) throw Error("duplicate_id", "duplicate river code", r.at);
    }
    std::int64_t expected = 0;
    for (const auto& l : need(doc, "landforms", Value::Kind::Array, "Material policy").items) {
        only(l, {"index", "name", "use"}, "landforms[]");
        if (whole(l, "index", "landforms[]") != expected++) throw Error("out_of_range", "landforms must be listed by index from 0", l.at);
        p.landforms.push_back(need(l, "name", Value::Kind::String, "landforms[]").text);
    }
    p.river_overrides_land = need(doc, "riverOverridesLand", Value::Kind::Bool, "Material policy").boolean;
    const Value& ramp = need(doc, "slopeRock", Value::Kind::Object, "Material policy");
    only(ramp, {"start", "full"}, "slopeRock");
    p.slope_start = real(ramp, "start", "slopeRock"); p.slope_full = real(ramp, "full", "slopeRock");
    if (!(p.slope_start >= 0 && p.slope_full > p.slope_start)) throw Error("out_of_range", "slopeRock needs 0 <= start < full", ramp.at);
    p.coast_epsilon = real(doc, "coastEpsilon", "Material policy");
    if (!(p.coast_epsilon > 0 && p.coast_epsilon <= 10)) throw Error("out_of_range", "coastEpsilon must be in (0, 10] world units", doc.find("coastEpsilon")->at);
    if (p.biome_core.empty() || p.water_intent.empty() || p.river_intent.empty() || p.landforms.empty()) throw Error("missing_field", "Material policy has an empty category table", doc.at);
    p.sha256 = cjson::sha256_hex(cjson::canonical(doc));
    return p;
}

void check_taxonomy(const MaterialPolicy& p, const std::vector<source::BiomeEntry>& biomes, const std::vector<std::string>& landforms) {
    if (biomes.empty()) throw Error("missing_field", "The source exports no terrain.biomes catalogue", nowhere());
    std::set<std::int64_t> seen;
    for (const auto& b : biomes) {
        if (!seen.insert(b.id).second) throw Error("duplicate_id", "terrain.biomes repeats id " + std::to_string(b.id), nowhere());
        const auto it = p.biome_core.find(b.id);
        if (it == p.biome_core.end()) throw Error("unmapped_category", "Exported biome " + std::to_string(b.id) + " (" + b.core + ") has no material mapping", nowhere());
        if (it->second != b.core) throw Error("unmapped_category", "Exported biome " + std::to_string(b.id) + " is '" + b.core + "' but the policy maps '" + it->second + "'", nowhere());
    }
    if (landforms != p.landforms) throw Error("unmapped_category", "Exported terrain.landforms differ from the policy's landform list", nowhere());
}

int biome_rock(const MaterialPolicy& p, std::int64_t biome) {
    const auto it = p.biome_rock.find(biome);
    if (it == p.biome_rock.end()) throw Error("unmapped_category", "Biome " + std::to_string(biome) + " has no material mapping", nowhere());
    return it->second;
}

int slope_rock(const MaterialPolicy& p, double slope) {
    const double t = std::clamp((slope - p.slope_start) / (p.slope_full - p.slope_start), 0.0, 1.0);
    return static_cast<int>(even_round(t * 255));
}

int intent(const MaterialPolicy& p, int water_type, int river) {
    const auto w = p.water_intent.find(water_type);
    if (w == p.water_intent.end()) throw Error("unmapped_category", "water_type " + std::to_string(water_type) + " has no mapping", nowhere());
    const auto r = p.river_intent.find(river);
    if (r == p.river_intent.end()) throw Error("unmapped_category", "river " + std::to_string(river) + " has no mapping", nowhere());
    if (r->second >= 0 && p.river_overrides_land && w->second == p.land) return r->second;
    return w->second;
}

}  // namespace worldbridge::terrain
