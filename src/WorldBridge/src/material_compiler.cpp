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
const std::string& str(const Value& object, const char* key, const std::string& what) { return need(object, key, Value::Kind::String, what).text; }
std::vector<std::string> str_list(const Value& object, const char* key, const std::string& what) {
    std::vector<std::string> out;
    for (const auto& v : need(object, key, Value::Kind::Array, what).items) {
        if (v.kind != Value::Kind::String || v.text.empty()) throw Error("wrong_type", what + " '" + key + "' must hold non-empty strings", v.at);
        if (std::find(out.begin(), out.end(), v.text) != out.end()) throw Error("duplicate_id", what + " '" + key + "' repeats '" + v.text + "'", v.at);
        out.push_back(v.text);
    }
    return out;
}
bool in(const std::vector<std::string>& set, const std::string& v) { return std::find(set.begin(), set.end(), v) != set.end(); }
}  // namespace

const std::vector<std::string>& land_face_types() {
    static const std::vector<std::string> types{"Plain", "Desert", "Snow", "Forest", "Steppe", "Swamp"};
    return types;
}

MaterialPolicy load_materials(const fs::path& file) {
    const Value doc = cjson::parse(file);
    only(doc, {"schema", "version", "biomes", "layerSet", "rules", "permanentWhite", "alpha", "flora", "mutation", "waterTypes", "river", "landforms", "intents",
               "riverOverridesLand", "slopeRock", "coastEpsilon", "notes"}, "Material policy");
    if (need(doc, "schema", Value::Kind::String, "Material policy").text != "mnbl.terrain-materials" || need(doc, "version", Value::Kind::Number, "Material policy").text != "2")
        throw Error("unsupported_version", "Material policy must be mnbl.terrain-materials version 2", doc.at);
    if (const Value* n = doc.find("notes"); n && n->kind != Value::Kind::String) throw Error("wrong_type", "Material policy notes must be a string", n->at);
    MaterialPolicy p;
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

    // The 15-slot layer set: slots from 0, at most 15 (the engine has 16; slot 15 stays free), unique names, every map named.
    static const std::set<std::string> physics{"grass", "stone", "sand", "snow", "mud", "soil", "path", "ice"};
    std::vector<std::string> layer_names;
    for (const auto& l : need(doc, "layerSet", Value::Kind::Array, "Material policy").items) {
        only(l, {"slot", "name", "diffuse", "normal", "specular", "height", "scale", "tint", "physics", "optional", "use"}, "layerSet[]");
        LayerSlot s;
        s.slot = static_cast<int>(whole(l, "slot", "layerSet[]"));
        if (s.slot != static_cast<int>(p.layer_set.size())) throw Error("out_of_range", "layerSet must be listed by slot from 0", l.at);
        s.name = str(l, "name", "layerSet[]"); s.diffuse = str(l, "diffuse", "layerSet[]"); s.normal = str(l, "normal", "layerSet[]");
        s.specular = str(l, "specular", "layerSet[]"); s.height = str(l, "height", "layerSet[]"); s.physics = str(l, "physics", "layerSet[]"); s.use = str(l, "use", "layerSet[]");
        s.scale = real(l, "scale", "layerSet[]");
        const Value& tint = need(l, "tint", Value::Kind::Object, "layerSet[]");
        only(tint, {"mode", "colour"}, "layerSet[].tint");
        s.tint_mode = static_cast<int>(whole(tint, "mode", "layerSet[].tint"));
        const Value& colour = need(tint, "colour", Value::Kind::Array, "layerSet[].tint");
        if (s.tint_mode < 0 || s.tint_mode > 2 || colour.items.size() != 3) throw Error("out_of_range", "layerSet[] tint mode is 0, 1 or 2 with an RGB colour", tint.at);
        for (int k = 0; k < 3; ++k) {
            if (colour.items[k].kind != Value::Kind::Number) throw Error("wrong_type", "layerSet[] tint colour holds numbers", colour.at);
            s.tint[k] = std::strtod(colour.items[k].text.c_str(), nullptr);
            if (!(s.tint[k] >= 0 && s.tint[k] <= 2)) throw Error("out_of_range", "layerSet[] tint colour components lie in [0, 2]", colour.at);
        }
        s.optional = need(l, "optional", Value::Kind::Bool, "layerSet[]").boolean;
        if (s.name.empty() || in(layer_names, s.name)) throw Error("duplicate_id", "layerSet names must be unique and non-empty", l.at);
        if (s.diffuse.empty() || s.diffuse == "none" || s.normal.empty() || s.normal == "none" || s.specular.empty() || s.height.empty())
            throw Error("missing_field", "layerSet[] " + s.name + " needs a diffuse and a normal texture (specular and height may be none)", l.at);
        if (!(s.scale > 0) || !physics.count(s.physics)) throw Error("out_of_range", "layerSet[] " + s.name + " needs a positive scale and an installed physics material", l.at);
        if (s.slot == 0 && s.optional) throw Error("out_of_range", "layerSet slot 0 is the base layer and cannot be optional", l.at);
        layer_names.push_back(s.name);
        p.layer_set.push_back(s);
    }
    if (p.layer_set.empty() || p.layer_set.size() > 15) throw Error("out_of_range", "layerSet holds 1 to 15 layers", doc.find("layerSet")->at);
    auto layer = [&](const Value& o, const char* key, const std::string& what) {
        const std::string& v = str(o, key, what);
        if (!in(layer_names, v)) throw Error("unmapped_category", what + " '" + key + "' names layer '" + v + "', which layerSet does not list", o.find(key)->at);
        return v;
    };

    // The shared biome table.
    static const std::set<std::string> battle_classes{"open", "woodland", "steppe", "rock", "cold", "desert", "wetland"};
    static const std::set<int> grades{20, 40, 60, 80, 100, 120, 140};
    std::vector<std::string> cores;
    for (const auto& b : need(doc, "biomes", Value::Kind::Array, "Material policy").items) {
        only(b, {"id", "core", "water", "terrainType", "layers", "battleClass", "colourGrade", "colourGradeWet", "snow", "note"}, "biomes[]");
        const auto id = whole(b, "id", "biomes[]");
        const std::string& core = need(b, "core", Value::Kind::String, "biomes[]").text;
        if (!p.biome_core.emplace(id, core).second) throw Error("duplicate_id", "duplicate biome id " + std::to_string(id), b.at);
        if (in(cores, core)) throw Error("duplicate_id", "duplicate biome core " + core, b.at);
        cores.push_back(core);
        BiomePresentation row;
        const std::string what = "biomes[] " + core;
        row.water = need(b, "water", Value::Kind::Bool, what).boolean;
        const Value* type = b.find("terrainType");
        if (!type) throw Error("missing_field", what + " lacks 'terrainType'", b.at);
        if (row.water != (type->kind == Value::Kind::Null)) throw Error("out_of_range", what + " terrainType is null exactly for a water biome", type->at);
        if (!row.water) {
            if (type->kind != Value::Kind::String || !in(land_face_types(), type->text)) throw Error("face_group_mismatch", what + " terrainType must be a land face type (Plain, Desert, Snow, Forest, Steppe, Swamp)", type->at);
            row.terrain_type = type->text;
        }
        double sum = 0;
        const Value& mix = need(b, "layers", Value::Kind::Object, what);
        if (mix.members.empty()) throw Error("missing_field", what + " has no layer mix", mix.at);
        for (const auto& [name, v] : mix.members) {
            if (!in(layer_names, name)) throw Error("unmapped_category", what + " mixes layer '" + name + "', which layerSet does not list", v.at);
            if (v.kind != Value::Kind::Number) throw Error("wrong_type", what + " layer fractions must be numbers", v.at);
            const double f = std::strtod(v.text.c_str(), nullptr);
            if (!(f > 0 && f <= 1)) throw Error("out_of_range", what + " layer fractions lie in (0, 1]", v.at);
            row.layers[name] = f; sum += f;
        }
        if (std::abs(sum - 1) > 1e-9) throw Error("out_of_range", what + " layer fractions must sum to 1", mix.at);
        row.battle_class = str(b, "battleClass", what);
        if (!battle_classes.count(row.battle_class)) throw Error("unmapped_category", what + " battleClass is not a mission-scenes class", b.find("battleClass")->at);
        const Value* grade = b.find("colourGrade");
        if (!grade) throw Error("missing_field", what + " lacks 'colourGrade'", b.at);
        if (row.water != (grade->kind == Value::Kind::Null)) throw Error("out_of_range", what + " colourGrade is null exactly for a water biome (the temperature band)", grade->at);
        if (!row.water) {
            row.colour_grade = static_cast<int>(whole(b, "colourGrade", what));
            if (!grades.count(row.colour_grade)) throw Error("out_of_range", what + " colourGrade is not an installed worldmap grade", grade->at);
        }
        if (b.find("colourGradeWet")) {
            row.colour_grade_wet = static_cast<int>(whole(b, "colourGradeWet", what));
            if (row.water || !grades.count(row.colour_grade_wet)) throw Error("out_of_range", what + " colourGradeWet is an installed grade of a land biome", b.find("colourGradeWet")->at);
        }
        row.snow = str(b, "snow", what);
        if (row.snow != "seasonal" && row.snow != "permanent") throw Error("unmapped_category", what + " snow is seasonal or permanent", b.find("snow")->at);
        p.presentation[id] = row;
    }

    const Value& rules = need(doc, "rules", Value::Kind::Object, "Material policy");
    only(rules, {"kernelSamples", "slopeLayer", "desertSlopeLayer", "mountainLandform", "mountainLayer", "mountainShare", "moisture", "coast", "waterEdge", "iceCap", "temperature"}, "rules");
    LayerRules& r = p.rules;
    r.kernel_samples = static_cast<int>(whole(rules, "kernelSamples", "rules"));
    r.slope_layer = layer(rules, "slopeLayer", "rules"); r.desert_slope_layer = layer(rules, "desertSlopeLayer", "rules");
    r.mountain_landform = str(rules, "mountainLandform", "rules"); r.mountain_layer = layer(rules, "mountainLayer", "rules");
    r.mountain_share = real(rules, "mountainShare", "rules");
    const Value& moisture = need(rules, "moisture", Value::Kind::Object, "rules");
    only(moisture, {"biome", "wetLayer", "dryLayer", "split", "band"}, "rules.moisture");
    r.moisture_biome = str(moisture, "biome", "rules.moisture"); r.moisture_wet_layer = layer(moisture, "wetLayer", "rules.moisture");
    r.moisture_dry_layer = layer(moisture, "dryLayer", "rules.moisture"); r.moisture_split = real(moisture, "split", "rules.moisture");
    r.moisture_band = real(moisture, "band", "rules.moisture");
    if (!(r.moisture_band > 0 && r.moisture_band <= 1)) throw Error("out_of_range", "rules.moisture band lies in (0, 1]", moisture.at);
    if (!in(cores, r.moisture_biome)) throw Error("unmapped_category", "rules.moisture biome is not a biome core", moisture.at);
    const Value& coast = need(rules, "coast", Value::Kind::Object, "rules");
    only(coast, {"layer", "rockyLayer", "samples"}, "rules.coast");
    r.coast_layer = layer(coast, "layer", "rules.coast"); r.coast_rocky_layer = layer(coast, "rockyLayer", "rules.coast");
    r.coast_samples = static_cast<int>(whole(coast, "samples", "rules.coast"));
    const Value& edge = need(rules, "waterEdge", Value::Kind::Object, "rules");
    only(edge, {"layer", "samples", "intents"}, "rules.waterEdge");
    r.water_edge_layer = layer(edge, "layer", "rules.waterEdge"); r.water_edge_samples = static_cast<int>(whole(edge, "samples", "rules.waterEdge"));
    r.water_edge_intents = str_list(edge, "intents", "rules.waterEdge");
    for (const auto& i : r.water_edge_intents) if (i != "lake" && i != "river") throw Error("unmapped_category", "rules.waterEdge intents are lake or river", edge.at);
    const Value& ice = need(rules, "iceCap", Value::Kind::Object, "rules");
    only(ice, {"layer", "threshold"}, "rules.iceCap");
    r.ice_cap_layer = layer(ice, "layer", "rules.iceCap"); r.ice_cap_threshold = real(ice, "threshold", "rules.iceCap");
    const Value& temperature = need(rules, "temperature", Value::Kind::Object, "rules");
    only(temperature, {"coldLayer", "belowC"}, "rules.temperature");
    r.cold_layer = layer(temperature, "coldLayer", "rules.temperature"); r.cold_below_c = real(temperature, "belowC", "rules.temperature");
    for (double f : {r.mountain_share, r.moisture_split, r.ice_cap_threshold})
        if (!(f >= 0 && f <= 1)) throw Error("out_of_range", "rules shares and thresholds lie in [0, 1]", rules.at);
    if (r.kernel_samples < 0 || r.coast_samples < 0 || r.water_edge_samples < 0 || r.cold_below_c < -100 || r.cold_below_c > 60)
        throw Error("out_of_range", "rules sample counts are non-negative and belowC is a temperature", rules.at);

    const Value& white = need(doc, "permanentWhite", Value::Kind::Object, "Material policy");
    only(white, {"biomes", "snowMonths", "snowThreshold", "layer", "terrainType"}, "permanentWhite");
    PermanentWhite& w = p.permanent_white;
    w.biomes = str_list(white, "biomes", "permanentWhite");
    w.snow_months = static_cast<int>(whole(white, "snowMonths", "permanentWhite"));
    w.snow_threshold = real(white, "snowThreshold", "permanentWhite");
    w.layer = layer(white, "layer", "permanentWhite");
    w.terrain_type = str(white, "terrainType", "permanentWhite");
    if (w.snow_months < 1 || w.snow_months > 12 || !(w.snow_threshold > 0 && w.snow_threshold <= 1) || w.terrain_type != "Snow")
        throw Error("out_of_range", "permanentWhite needs 1..12 snow months, a threshold in (0, 1] and the Snow face", white.at);
    // permanent snow treatment <=> in the mask, and a permanent biome is all permanent layer and the Snow face.
    for (const auto& [id, row] : p.presentation) {
        const std::string& core = p.biome_core.at(id);
        if ((row.snow == "permanent") != in(w.biomes, core)) throw Error("unmapped_category", "biome " + core + " snow must be permanent exactly when permanentWhite lists it", white.at);
        if (row.snow == "permanent" && (row.terrain_type != w.terrain_type || row.layers.size() != 1 || !row.layers.count(w.layer)))
            throw Error("face_group_mismatch", "permanent-white biome " + core + " must be all " + w.layer + " with the " + w.terrain_type + " face", white.at);
    }
    for (const auto& core : w.biomes) if (!in(cores, core)) throw Error("unmapped_category", "permanentWhite names unknown biome " + core, white.at);

    const Value& alpha = need(doc, "alpha", Value::Kind::Object, "Material policy");
    only(alpha, {"rule", "smoothBlendAmount", "minByte", "slopeTransparency"}, "alpha");
    p.alpha.rule = str(alpha, "rule", "alpha");
    p.alpha.smooth_blend = real(alpha, "smoothBlendAmount", "alpha");
    p.alpha.min_byte = static_cast<int>(whole(alpha, "minByte", "alpha"));
    p.alpha.slope_transparency = need(alpha, "slopeTransparency", Value::Kind::Bool, "alpha").boolean;
    // MMR-249: alpha-over compositing; the weight is divided by smooth_blend_amount, so 1 keeps bytes linear in coverage; the slope flag
    // height-blends (hidden until w ~ 0.6) and is never used.
    if (p.alpha.rule != "overInverse" || p.alpha.smooth_blend != 1 || p.alpha.slope_transparency || p.alpha.min_byte < 0 || p.alpha.min_byte > 16)
        throw Error("out_of_range", "alpha is the overInverse rule with smoothBlendAmount 1, slopeTransparency off and a minByte in 0..16 (MMR-249)", alpha.at);

    const Value& flora = need(doc, "flora", Value::Kind::Object, "Material policy");
    only(flora, {"biomes", "density", "scale", "clearance", "maxTrees"}, "flora");
    FloraPolicy& fl = p.flora;
    for (const auto& [core, v] : need(flora, "biomes", Value::Kind::Object, "flora").members) {
        if (!in(cores, core)) throw Error("unmapped_category", "flora names unknown biome " + core, v.at);
        const std::string fwhat = "flora.biomes." + core;
        only(v, {"layer", "kinds"}, fwhat);
        FloraBiome fb;
        fb.layer = layer(v, "layer", fwhat);
        double total = 0;
        for (const auto& k : need(v, "kinds", Value::Kind::Array, fwhat).items) {
            only(k, {"name", "variations", "weight"}, "flora kinds[]");
            FloraKind kind{str(k, "name", "flora kinds[]"), static_cast<int>(whole(k, "variations", "flora kinds[]")), real(k, "weight", "flora kinds[]")};
            if (kind.name.empty() || kind.variations < 1 || kind.variations > 16 || !(kind.weight > 0)) throw Error("out_of_range", "flora kinds need a name, 1..16 variations and a positive weight", k.at);
            total += kind.weight; fb.kinds.push_back(kind);
        }
        if (fb.kinds.empty() || std::abs(total - 1) > 1e-9) throw Error("out_of_range", fwhat + " kind weights must sum to 1", v.at);
        for (const auto& [bid, bcore] : p.biome_core)
            if (bcore == core && p.presentation.at(bid).water) throw Error("out_of_range", "flora names water biome " + core, v.at);
        fl.biomes[core] = fb;
    }
    fl.density = real(flora, "density", "flora");
    const Value& scale = need(flora, "scale", Value::Kind::Array, "flora");
    if (scale.items.size() != 2 || scale.items[0].kind != Value::Kind::Number || scale.items[1].kind != Value::Kind::Number) throw Error("wrong_type", "flora scale is [min, max]", scale.at);
    fl.scale_min = std::strtod(scale.items[0].text.c_str(), nullptr); fl.scale_max = std::strtod(scale.items[1].text.c_str(), nullptr);
    const Value& clearance = need(flora, "clearance", Value::Kind::Object, "flora");
    only(clearance, {"padMargin", "coastSamples", "slopeShare", "roads"}, "flora.clearance");
    fl.pad_margin = real(clearance, "padMargin", "flora.clearance");
    fl.coast_samples = static_cast<int>(whole(clearance, "coastSamples", "flora.clearance"));
    fl.slope_share = real(clearance, "slopeShare", "flora.clearance");
    fl.roads = need(clearance, "roads", Value::Kind::Bool, "flora.clearance").boolean;
    const auto max_trees = whole(flora, "maxTrees", "flora");
    if (!(fl.density >= 0 && fl.density <= 4) || !(fl.scale_min > 0 && fl.scale_max >= fl.scale_min && fl.scale_max <= 4) || !(fl.pad_margin >= 0) ||
        fl.coast_samples < 0 || fl.coast_samples > 8 || !(fl.slope_share >= 0 && fl.slope_share <= 1) || max_trees < 0 || max_trees > 2000000)
        throw Error("out_of_range", "flora density, scale, clearance or maxTrees is out of range", flora.at);
    fl.max_trees = static_cast<std::size_t>(max_trees);

    const Value& mutation = need(doc, "mutation", Value::Kind::Object, "Material policy");
    only(mutation, {"landValid", "schools", "coreOverrides", "hiddenLooks", "generatedVariants", "battleClass", "thresholds"}, "mutation");
    MutationRemap& m = p.mutation;
    m.land_valid = str_list(mutation, "landValid", "mutation");
    for (const auto& t : m.land_valid) if (!in(land_face_types(), t)) throw Error("face_group_mismatch", "mutation landValid holds only Plain, Desert, Snow, Forest, Steppe and Swamp (never Dune)", mutation.at);
    auto valid_type = [&](const Value& v, const std::string& what) {
        if (v.kind != Value::Kind::String || !in(m.land_valid, v.text)) throw Error("face_group_mismatch", what + " remaps outside mutation landValid", v.at);
        return v.text;
    };
    for (const auto& [school, v] : need(mutation, "schools", Value::Kind::Object, "mutation").members) m.schools[school] = valid_type(v, "mutation schools " + school);
    if (m.schools.empty()) throw Error("missing_field", "mutation schools is empty", mutation.at);
    for (const auto& [school, v] : need(mutation, "coreOverrides", Value::Kind::Object, "mutation").members) {
        if (!m.schools.count(school) || v.kind != Value::Kind::Object) throw Error("unmapped_category", "mutation coreOverrides names unknown school " + school, v.at);
        for (const auto& [core, t] : v.members) {
            if (!in(cores, core)) throw Error("unmapped_category", "mutation coreOverrides names unknown biome " + core, t.at);
            m.core_overrides[school][core] = valid_type(t, "mutation coreOverrides " + school + "/" + core);
        }
    }
    for (const auto& [school, v] : need(mutation, "hiddenLooks", Value::Kind::Object, "mutation").members) {
        if (m.schools.count(school) || v.kind != Value::Kind::String || !m.schools.count(v.text)) throw Error("unmapped_category", "mutation hiddenLooks maps a hidden school to a visible one", v.at);
        m.hidden_looks[school] = v.text;
    }
    m.generated_variants = str(mutation, "generatedVariants", "mutation");
    m.battle_class = str(mutation, "battleClass", "mutation");
    if (m.generated_variants != "visual" || m.battle_class != "faceClass") throw Error("unmapped_category", "mutation generatedVariants is visual and battleClass is faceClass (RT-20, RT-26)", mutation.at);
    const Value& thresholds = need(mutation, "thresholds", Value::Kind::Object, "mutation");
    only(thresholds, {"source", "threshold", "dominance"}, "mutation.thresholds");
    m.threshold_source = str(thresholds, "source", "mutation.thresholds"); m.threshold_key = str(thresholds, "threshold", "mutation.thresholds");
    m.dominance_key = str(thresholds, "dominance", "mutation.thresholds");
    if (m.threshold_source != "world.json") throw Error("unmapped_category", "mutation thresholds are read from world.json", thresholds.at);

    for (const auto& w2 : need(doc, "waterTypes", Value::Kind::Array, "Material policy").items) {
        only(w2, {"code", "name", "intent"}, "waterTypes[]");
        const int code = static_cast<int>(whole(w2, "code", "waterTypes[]"));
        const int value = intent_of(w2, "waterTypes[]");
        if (value < 0) throw Error("unmapped_category", "a water class needs a land, ocean or lake intent", w2.at);
        if (!p.water_intent.emplace(code, value).second) throw Error("duplicate_id", "duplicate water_type code", w2.at);
    }
    for (const auto& rv : need(doc, "river", Value::Kind::Array, "Material policy").items) {
        only(rv, {"code", "intent"}, "river[]");
        if (!p.river_intent.emplace(static_cast<int>(whole(rv, "code", "river[]")), intent_of(rv, "river[]")).second) throw Error("duplicate_id", "duplicate river code", rv.at);
    }
    std::int64_t expected = 0;
    for (const auto& l : need(doc, "landforms", Value::Kind::Array, "Material policy").items) {
        only(l, {"index", "name", "use"}, "landforms[]");
        if (whole(l, "index", "landforms[]") != expected++) throw Error("out_of_range", "landforms must be listed by index from 0", l.at);
        p.landforms.push_back(need(l, "name", Value::Kind::String, "landforms[]").text);
    }
    if (!in(p.landforms, r.mountain_landform)) throw Error("unmapped_category", "rules mountainLandform is not a listed landform", rules.at);
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

const BiomePresentation& biome_row(const MaterialPolicy& p, std::int64_t biome) {
    const auto it = p.presentation.find(biome);
    if (it == p.presentation.end()) throw Error("unmapped_category", "Biome " + std::to_string(biome) + " has no material mapping", nowhere());
    return it->second;
}

double slope_share(const MaterialPolicy& p, double slope) {
    return std::clamp((slope - p.slope_start) / (p.slope_full - p.slope_start), 0.0, 1.0);
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
