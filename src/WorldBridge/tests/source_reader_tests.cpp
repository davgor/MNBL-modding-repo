// Source contract tests: source_reader_tests FIXTURE_DIR CONTRACT_JSON WORK_DIR
// FIXTURE_DIR/valid is the split fixture written by make_fixture.py. Every refusal case copies it,
// applies one mutation, and checks the error code, JSON path and exact byte/line/column location.
#include <cstdint>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iostream>
#include <limits>
#include <optional>
#include <sstream>
#include <string>
#include "worldbridge/source_world.hpp"

namespace fs = std::filesystem;
using namespace worldbridge::source;

namespace {
int checks = 0, failures = 0;
fs::path fixture, work;
Compatibility contract;

void check(bool ok, const std::string& what) {
    ++checks;
    if (ok) std::cout << "ok    " << what << "\n";
    else { ++failures; std::cout << "FAIL  " << what << "\n"; }
}

std::string slurp(const fs::path& p) { std::ifstream in(p, std::ios::binary); std::ostringstream s; s << in.rdbuf(); return s.str(); }
void spit(const fs::path& p, const std::string& text) { std::ofstream out(p, std::ios::binary | std::ios::trunc); out << text; }

fs::path copy_fixture(const std::string& name) {
    const fs::path dir = work / name;
    fs::remove_all(dir);
    fs::copy(fixture / "valid", dir, fs::copy_options::recursive);
    return dir;
}

// Replaces the first occurrence of `old` at or after the first occurrence of `anchor`; returns its offset.
std::size_t replace_after(const fs::path& file, const std::string& anchor, const std::string& old, const std::string& replacement) {
    std::string text = slurp(file);
    const auto a = anchor.empty() ? 0 : text.find(anchor);
    if (a == std::string::npos) throw std::runtime_error("anchor not found: " + anchor);
    const auto at = text.find(old, a);
    if (at == std::string::npos) throw std::runtime_error("text not found: " + old);
    text.replace(at, old.size(), replacement);
    spit(file, text);
    return at;
}

std::size_t find_after(const fs::path& file, const std::string& anchor, const std::string& needle) {
    const std::string text = slurp(file);
    const auto a = text.find(anchor);
    if (a == std::string::npos) throw std::runtime_error("anchor not found: " + anchor);
    const auto at = text.find(needle, a);
    if (at == std::string::npos) throw std::runtime_error("needle not found: " + needle);
    return at;
}

struct Point { std::uint64_t offset, line, column; };
Point point_in(const std::string& text, std::size_t offset) {
    Point p{offset, 1, 1};
    for (std::size_t i = 0; i < offset && i < text.size(); ++i) { if (text[i] == '\n') { ++p.line; p.column = 1; } else ++p.column; }
    return p;
}

bool ends_with(const std::string& text, const std::string& tail) {
    return text.size() >= tail.size() && text.compare(text.size() - tail.size(), tail.size(), tail) == 0;
}

struct Expected {
    std::string code, path, file = "world.json";
    std::optional<std::size_t> offset;  // byte offset in `file` the error must point at
};

void refuse(const std::string& name, const std::function<Expected(const fs::path&)>& mutate, const Compatibility* override_contract = nullptr) {
    const fs::path dir = copy_fixture(name);
    Expected want;
    try { want = mutate(dir); } catch (const std::exception& e) { check(false, name + ": mutation failed: " + e.what()); return; }
    try {
        read_source(dir, override_contract ? *override_contract : contract);
        check(false, name + ": accepted, expected " + want.code);
    } catch (const Error& e) {
        bool ok = e.code == want.code && (want.path.empty() || e.where.path == want.path) && ends_with(e.where.file, want.file);
        std::string located;
        if (want.offset) {
            const auto p = point_in(slurp(dir / want.file), *want.offset);
            ok = ok && e.where.offset == p.offset && e.where.line == p.line && e.where.column == p.column;
            located = " expected " + std::to_string(p.line) + ":" + std::to_string(p.column) + " (byte " + std::to_string(p.offset) + ")";
        }
        check(ok, name + " -> " + e.code + " " + e.where.text() + (ok ? "" : " | wanted " + want.code + " " + want.path + located));
    } catch (const std::exception& e) {
        check(false, name + ": unexpected exception " + e.what());
    }
}

void accept(const std::string& name, const std::function<void(const fs::path&)>& mutate, const std::function<bool(const SourceWorld&)>& verify) {
    const fs::path dir = copy_fixture(name);
    try { mutate(dir); const auto world = read_source(dir, contract); check(verify(world), name + " accepted with the expected result"); }
    catch (const std::exception& e) { check(false, name + " refused: " + e.what()); }
}

// Tokenizer: parses a whole document and checks the refusal code and exact location.
void tokens(const std::string& name, const std::string& text, const std::string& code, std::size_t offset, worldbridge::json::Limits limits = {}) {
    const fs::path file = work / ("token-" + name + ".json");
    spit(file, text);
    try {
        worldbridge::json::Stream s(file, limits);
        while (s.next() != worldbridge::json::Token::End) {}
        check(code.empty(), "tokens " + name + (code.empty() ? " parsed" : ": accepted, expected " + code));
    } catch (const Error& e) {
        const auto p = point_in(text, offset);
        const bool ok = e.code == code && e.where.offset == p.offset && e.where.line == p.line && e.where.column == p.column;
        check(ok, "tokens " + name + " -> " + e.code + " " + std::to_string(e.where.line) + ":" + std::to_string(e.where.column) + " (byte " +
                      std::to_string(e.where.offset) + ") " + e.where.path + (ok ? "" : " | wanted " + code + " byte " + std::to_string(offset)));
    }
}

void tokenizer() {
    tokens("valid", "{\"a\":[1,-2.5e+3,true,false,null,\"\\uD83D\\uDE00\",{}],\"b\":{\"c\":[]}}\n", "", 0);
    tokens("newline_in_string", "[\"a\nb\"]", "invalid_json", 3);
    tokens("missing_colon", "{\"a\" 1}", "invalid_json", 5);
    tokens("bad_literal", "[tru]", "invalid_json", 4);
    tokens("trailing_comma", "[1,]", "invalid_json", 3);
    tokens("leading_zero", "01", "trailing_data", 1);
    tokens("duplicate_key", "{\"a\":1,\"a\":2}", "duplicate_key", 7);
    tokens("escaped_duplicate_key", "{\"a\":1,\"\\u0061\":2}", "duplicate_key", 7);
    tokens("unpaired_high_surrogate", "[\"\\uD800\"]", "invalid_json", 2);
    tokens("lone_low_surrogate", "[\"\\uDE00\"]", "invalid_json", 2);
    tokens("utf8_encoded_surrogate", std::string("[\"") + '\xed' + '\xa0' + '\x80' + "\"]", "invalid_json", 2);
    tokens("bad_escape", "[\"\\q\"]", "invalid_json", 3);
    tokens("control_character", std::string("[\"a") + '\x01' + "\"]", "invalid_json", 3);
    tokens("bad_utf8", std::string("[\"") + '\xff' + "\"]", "invalid_json", 2);
    tokens("infinity_1e999", "[1e999]", "nonfinite_number", 1);
    tokens("nan", "[NaN]", "invalid_json", 1);
    tokens("missing_exponent", "[1e]", "invalid_json", 3);
    tokens("truncated", "{\"a\":[1,2", "truncated_json", 9);
    tokens("depth_65", std::string(65, '[') + std::string(65, ']'), "limit_exceeded", 64);
    worldbridge::json::Limits small;
    small.max_open_key_bytes = 200;
    tokens("open_key_budget", "{\"k1\":1,\"k2\":2,\"k3\":3,\"k4\":{\"k5\":5}}", "limit_exceeded", 28, small);
}

void positive() {
    const auto world = read_source(fixture / "valid", contract);
    const auto& m = world.metadata;
    check(m.schema == "fwg.world" && m.schema_version == 2 && m.completed_stage == 16 && m.split_version == 1 && m.scene_version == 1, "root contract read");
    check(m.generator_version == "1" && m.block_versions.at("future_block") == 99, "generator_version kept as descriptive text; unknown optional block skipped with its version recorded");
    check(m.grid_side == 5 && world.layers.size() == 13 && world.layers.at("land").integral && !world.layers.at("height").integral, "13 layers validated at side 5; integral flag computed");
    check(world.cities.size() == 2 && world.hamlets.size() == 3 && world.castles.size() == 1 && world.key_locations.size() == 1, "record counts");
    const City& c0 = world.cities[0];
    const City& c1 = world.cities[1];
    check(c0.uid == "surface-city-0-12-elf" && c0.site_id == 0 && c0.hamlet_ids == std::vector<std::string>{"hamlet-0", "coastal-0-0"} &&
          c0.fortress_ids == std::vector<std::string>{"fortress-0"} && c0.culture_id == "elf-1a2b", "city joined to humans.cores by site_id with hamlet and fortress ids");
    check(c0.freshwater_distance_m && *c0.freshwater_distance_m == 812.5 && !c1.freshwater_distance_m, "nullable freshwater_distance_m: value kept, null kept as null");
    const Hamlet& farm = world.hamlets[0];
    const Hamlet& harbour = world.hamlets[1];
    const Hamlet& landing = world.hamlets[2];
    check(!farm.harbour && farm.id == "hamlet-0" && farm.uid == "hamlet-node-6", "plain hamlet row: no harbour fields, ordinal id and stable uid kept");
    check(harbour.harbour && harbour.harbour->sea_node == 9 && harbour.harbour->monthly_fish == 12 && harbour.harbour->fishing_nodes == 1 &&
          harbour.harbour->trade_terminal && harbour.uid == "hamlet-node-11" && harbour.kind == "hamlet", "harbour-shaped row dispatched on its fields, kind 'hamlet'");
    check(landing.harbour && landing.role == "landing" && !landing.harbour->trade_terminal, "role 'landing' is harbour-shaped (role does not decide the shape)");
    check(c0.plan_path == "plans/city/surface-city-0-12-elf-2.json" && harbour.plan_path == "plans/hamlet/coastal-0-0-2.json" &&
          world.castles[0].plan_path == "plans/castle/fortress-0-2.json" && world.key_locations[0].plan_path == "plans/key_location/keyloc-academy_ruin-13-2.json",
          "plan paths come from the *_plans.files indexes (disambiguated names, not derived from UIDs)");
    check(world.plans.size() == 7, "all 7 indexed plan files opened and joined; every record has exactly one plan");
    check(world.castles[0].core_id == 0 && world.castles[0].core_uid == c0.uid && world.castles[0].garrisoned, "fortress joined to its core city");
    const auto land = world.read_layer("land");
    check(land.side == 5 && land.values.size() == 25 && land.at(3, 3) == 16 && land.at(0, 2) == 10 && land.at(1, 4) == land.at(1, 0), "land layer materialized on request (seam/pole intact)");
    const auto height = world.read_layer("height");
    check(height.at(2, 1) == 3.25 && height.at(0, 4) == 0.25, "float layer materialized exactly");
    check(world.statistics.retained_bytes > 0 && world.statistics.retained_bytes < contract.max_retained_bytes, "retained record bytes counted: " + std::to_string(world.statistics.retained_bytes));

    // Identity contract round trip (64-bit layout seed as a decimal string), validated strictly on read.
    const fs::path identity = work / "identity.json";
    spit(identity, write_identity(m));
    const auto back = read_identity(identity);
    check(back.layout_seed == "1555514970434891308" && back.layout_seed == m.layout_seed && back.crust_seed == m.crust_seed && back.grid_side == 5 && back.generator_version == "1",
          "identity document written and read back: layout seed exact " + back.layout_seed);
    // read_layer after the file changed underneath it.
    const fs::path changed = copy_fixture("layer_changed");
    try {
        const auto w = read_source(changed, contract);
        replace_after(changed / "world.json", "\"land\"", "    16,\n", "");
        w.read_layer("land");
        check(false, "read_layer accepted a changed layer");
    } catch (const Error& e) { check(e.code == "bad_dimensions" && e.where.path == "$.layers.land[3]", "read_layer refuses a layer changed since validation -> " + e.where.text()); }
}

void identity_contract() {
    const double as_double = static_cast<double>(std::numeric_limits<std::uint64_t>::max());
    std::ostringstream printed; printed.precision(17); printed << as_double;
    const fs::path dir = copy_fixture("max_seed");
    replace_after(dir / "world.json", "\"seeds\"", "\"1555514970434891308\"", "\"18446744073709551615\"");
    replace_after(dir / "world.json", "\"generator_version\": ", "1", "\"1.4.2\"");
    try {
        const auto world = read_source(dir, contract);
        const fs::path identity = work / "identity-max.json";
        spit(identity, write_identity(world.metadata));
        const auto back = read_identity(identity);
        check(back.layout_seed == "18446744073709551615" && std::stoull(back.layout_seed) == std::numeric_limits<std::uint64_t>::max() && printed.str() != back.layout_seed &&
              back.generator_version == "1.4.2",
              "UINT64_MAX layout seed round-trips exactly (a JSON double would print " + printed.str() + "); string generator_version is descriptive");
    } catch (const std::exception& e) { check(false, std::string("max seed refused: ") + e.what()); }
    auto bad_identity = [&](const std::string& name, const std::string& text, const std::string& code) {
        const fs::path f = work / ("identity-" + name + ".json");
        spit(f, text);
        try { read_identity(f); check(false, "identity " + name + " accepted"); }
        catch (const Error& e) { check(e.code == code, "identity " + name + " -> " + e.code + " " + e.where.text()); }
    };
    const std::string good = "\"sourceSchema\":\"fwg.world\",\"schemaVersion\":2,\"completedStage\":16,\"generatorVersion\":\"1\",\"gridSide\":5,\"crustSeed\":1,\"detailSeed\":2";
    bad_identity("seed_number", "{\"schema\":\"mnbl.source-identity\",\"version\":1," + good + ",\"layoutSeed\":1555514970434891308}", "wrong_type");
    bad_identity("seed_overflow", "{\"schema\":\"mnbl.source-identity\",\"version\":1," + good + ",\"layoutSeed\":\"18446744073709551616\"}", "out_of_range");
    bad_identity("missing_seed", "{\"schema\":\"mnbl.source-identity\",\"version\":1," + good + "}", "missing_field");
    bad_identity("wrong_schema", "{\"schema\":\"other\",\"version\":1," + good + ",\"layoutSeed\":\"1\"}", "unsupported_schema");
    bad_identity("wrong_source_schema", "{\"schema\":\"mnbl.source-identity\",\"version\":1,\"sourceSchema\":\"other\",\"schemaVersion\":2,\"completedStage\":16,\"generatorVersion\":\"1\",\"gridSide\":5,\"crustSeed\":1,\"detailSeed\":2,\"layoutSeed\":\"1\"}", "unsupported_schema");
    bad_identity("extra_member", "{\"schema\":\"mnbl.source-identity\",\"version\":1," + good + ",\"layoutSeed\":\"1\",\"extra\":0}", "unexpected_member");
}

void accepted_variants() {
    accept("ungarrisoned_fort", [&](const fs::path& f) {
        // The writer's shape for a fort without a garrison city: core_id -1, core_uid "" (castle plan: core_id null).
        replace_after(f / "world.json", "\"fortresses\"", "[", "[\n   {\"id\": \"fortress-1\", \"uid\": \"fortress-node-24\", \"kind\": \"fortress\", \"node\": 24, \"x\": 4, \"z\": 4, \"core_id\": -1, \"core_uid\": \"\", \"culture_id\": \"elf-1a2b\", \"watch\": \"frontier\", \"frontier\": {\"between\": [\"elf\", \"gnome\"], \"corridor_km2\": 3.5}},");
        replace_after(f / "world.json", "\"castle_plans\"", "\"files\": [", "\"files\": [\n   {\"fortress_id\": \"fortress-1\", \"path\": \"plans/castle/fortress-1.json\"},");
        spit(f / "plans/castle/fortress-1.json", "{\"version\": 3, \"kind\": \"castle\", \"fortress_id\": \"fortress-1\", \"fortress_uid\": \"fortress-node-24\", \"core_id\": null, \"status\": \"complete\"}\n");
    }, [](const SourceWorld& w) { return w.castles.size() == 2 && !w.castles[0].garrisoned && w.castles[0].plan_path == "plans/castle/fortress-1.json"; });
    accept("unknown_block_string_version", [&](const fs::path& d) { replace_after(d / "world.json", "\"future_block\"", "99", "\"2.0\""); },
           [](const SourceWorld& w) { return w.metadata.block_versions.count("future_block") == 0; });
    // MMR-25 projection inputs: the physical frame, road node paths and river segments.
    accept("projection_frame_roads_rivers", [&](const fs::path& d) {
        replace_after(d / "world.json", "", " \"topology\": {", " \"effective_config\": {\"sea_level\": -2.5, \"globe_radius\": 31830.99, \"other\": [1]},\n \"topology\": {");
        replace_after(d / "world.json", "\"roads\"", "\"routes\": []", "\"routes\": [{\"from\": 0, \"to\": 1, \"length_m\": 9324.5, \"cost\": 1, \"nodes\": [6, 7, 8]}]");
        replace_after(d / "world.json", "\"water\"", "\"nodes\": 25", "\"nodes\": 25, \"river_segments\": [[6, 7], [7, 12]]");
    }, [](const SourceWorld& w) {
        const auto& m = w.metadata;
        return m.radius_m && *m.radius_m == 31830.99 && m.sea_level_m && *m.sea_level_m == -2.5 && m.effective_globe_radius_m && *m.effective_globe_radius_m == 31830.99 &&
               m.longitude_deg == std::vector<double>{-180, 180} && m.latitude_deg == std::vector<double>{90, -90} &&
               w.routes.size() == 1 && w.routes[0].from == 0 && w.routes[0].to == 1 && w.routes[0].length_m == 9324.5 && w.routes[0].nodes == std::vector<std::int64_t>{6, 7, 8} &&
               w.rivers.size() == 2 && w.rivers[1].from == 7 && w.rivers[1].to == 12;
    });
    // MMR-26 terrain taxonomy: the exported biome catalogue and landform names.
    accept("terrain_taxonomy", [&](const fs::path& d) {
        replace_after(d / "world.json", "\"terrain\"", "\"version\": 6", "\"version\": 6, \"biomes\": [{\"id\": 0, \"core\": \"ocean\", \"name\": \"Ocean\", \"color\": [1, 2, 3]}, {\"id\": 13, \"core\": \"marsh\"}], \"landforms\": [\"Submerged\", \"Valley\"]");
    }, [](const SourceWorld& w) {
        return w.biomes.size() == 2 && w.biomes[1].id == 13 && w.biomes[1].core == "marsh" && w.biomes[0].name == "Ocean" && w.landforms == std::vector<std::string>{"Submerged", "Valley"};
    });
    // MMR-27 campaign inputs: realms, people, cultures, the founding report and community limits.
    accept("campaign_inputs", [&](const fs::path& d) {
        replace_after(d / "world.json", "\"heroes\": {", "\"realms\": []", "\"realms\": [{\"uid\": \"realm-1\", \"name\": \"R\", \"english_name\": \"Realm R\", \"civilization_id\": \"gnome\", \"civilization_name\": \"Gnomes\", \"parent_race_id\": \"gnome\", \"culture_id\": \"c1\", \"capital_uid\": \"x\", \"city_uids\": [\"x\", \"y\"], \"sovereign_uid\": null}]");
        replace_after(d / "world.json", "\"heroes\": {", "\"people\": []", "\"people\": [{\"uid\": \"p1\", \"role\": \"sovereign\", \"status\": \"living\", \"name\": \"P\", \"display_name\": \"P the Tall\", \"realm_uid\": \"realm-1\", \"home\": {\"kind\": \"city\", \"uid\": \"x\", \"name\": \"X\"}, \"presence\": {\"site_kind\": \"hamlet\", \"uid\": \"hamlet-node-16\", \"situation\": \"reeve\"}, \"deeds\": [1]}, {\"uid\": \"p2\", \"role\": \"champion\", \"status\": \"living\", \"name\": \"Q\", \"realm_uid\": null, \"home\": null, \"presence\": null}]");
        replace_after(d / "world.json", "\"humans\"", "\"uid\": \"hamlet-node-16\"", "\"uid\": \"hamlet-node-16\", \"founded_by\": \"player\"");
        replace_after(d / "world.json", "\"humans\"", "\"uid\": \"fortress-node-8\"", "\"uid\": \"fortress-node-8\", \"founded_by\": null");
        replace_after(d / "world.json", "\"humans\"", "\"cores\": [", "\"cultures\": [{\"id\": \"c1\", \"civilization_id\": \"gnome\", \"city_ids\": [0, 1], \"architecture_style_id\": \"gnome\"}], \"cores\": [");
        replace_after(d / "world.json", "\"settlements\"", "\"sites\": [", "\"founding\": {\"target_cities\": 9, \"placed_cities\": 2, \"turns\": 3}, \"sites\": [");
        replace_after(d / "world.json", "", " \"topology\": {", " \"effective_config\": {\"settlement_count\": 24, \"hamlets_per_core\": 3, \"fortress_count\": 1024},\n \"topology\": {");
    }, [](const SourceWorld& w) {
        const auto& m = w.metadata;
        return w.realms.size() == 1 && !w.realms[0].sovereign_uid && w.realms[0].city_uids == std::vector<std::string>{"x", "y"} && w.realms[0].capital_uid == "x" &&
               w.people.size() == 2 && w.people[0].realm_uid == std::string("realm-1") && w.people[0].home_uid == "x" && w.people[0].home_kind == "city" && w.people[0].display_name == "P the Tall" &&
               w.people[0].presence_kind == "hamlet" && w.people[0].presence_uid == "hamlet-node-16" && w.people[1].presence_kind.empty() &&
               w.realms[0].civilization_name == "Gnomes" && w.realms[0].parent_race_id == "gnome" && w.hamlets.back().founded_by == "player" && w.castles[0].founded_by.empty() &&
               !w.people[1].realm_uid && w.people[1].home_uid.empty() && w.cultures.size() == 1 && w.cultures[0].city_ids == std::vector<std::int64_t>{0, 1} &&
               m.founding_target_cities == 9 && m.founding_placed_cities == 2 && m.settlement_count == 24 && m.hamlets_per_core == 3 && m.fortress_count == 1024;
    });
    accept("projection_frame_absent", [](const fs::path&) {}, [](const SourceWorld& w) {
        return w.metadata.radius_m && !w.metadata.sea_level_m && w.routes.empty() && w.rivers.empty();
    });
    accept("null_radius_and_sea_level", [&](const fs::path& d) {
        replace_after(d / "world.json", "\"topology\"", "31830.99", "null");
        replace_after(d / "world.json", "", " \"topology\": {", " \"effective_config\": {\"sea_level\": null},\n \"topology\": {");
    }, [](const SourceWorld& w) { return !w.metadata.radius_m && !w.metadata.sea_level_m; });
}

void refusals() {
    const std::string W = "world.json";
    auto at = [](std::size_t offset) { return std::optional<std::size_t>(offset); };
    auto files_entry = [&](const fs::path& d, const std::string& block, int index) {
        const std::string text = slurp(d / W);
        auto pos = text.find("\"files\"", text.find("\"" + block + "\""));
        for (int i = 0; i <= index; ++i) pos = text.find('{', pos + 1);
        return pos;
    };
    auto record_start = [&](const fs::path& d, const std::string& array_anchor, int index) {
        const std::string text = slurp(d / W);
        auto pos = text.find('[', text.find(array_anchor));
        int depth = 0;
        for (std::size_t i = pos + 1; i < text.size(); ++i) {
            if (text[i] == '{' && depth++ == 0 && index-- == 0) return i;
            if (text[i] == '}') --depth;
        }
        throw std::runtime_error("record not found");
    };
    // MMR-25 projection inputs.
    auto any = [&](const char* code) { return Expected{code, "", W, std::nullopt}; };
    refuse("route_with_one_node", [&](const fs::path& d) { replace_after(d / W, "\"roads\"", "\"routes\": []", "\"routes\": [{\"from\": 0, \"to\": 1, \"nodes\": [6]}]"); return any("invalid_record"); });
    refuse("route_without_nodes", [&](const fs::path& d) { replace_after(d / W, "\"roads\"", "\"routes\": []", "\"routes\": [{\"from\": 0, \"to\": 1}]"); return any("missing_field"); });
    refuse("route_node_as_string", [&](const fs::path& d) { replace_after(d / W, "\"roads\"", "\"routes\": []", "\"routes\": [{\"from\": 0, \"to\": 1, \"nodes\": [6, \"7\"]}]"); return any("wrong_type"); });
    // MMR-28: producer-reported river crossings on roads ([node, node] steps).
    accept("road_river_crossings", [&](const fs::path& d) {
        replace_after(d / W, "\"roads\"", "\"routes\": []", "\"routes\": [{\"from\": 0, \"to\": 1, \"nodes\": [6, 7, 8], \"river_crossings\": [[7, 8]]}, {\"from\": 1, \"to\": 0, \"nodes\": [8, 7], \"river_crossings\": []}]");
    }, [](const SourceWorld& w) {
        return w.routes.size() == 2 && w.routes[0].river_crossings == std::vector<std::pair<std::int64_t, std::int64_t>>{{7, 8}} && w.routes[1].river_crossings.empty();
    });
    refuse("road_river_crossing_with_one_node", [&](const fs::path& d) { replace_after(d / W, "\"roads\"", "\"routes\": []", "\"routes\": [{\"from\": 0, \"to\": 1, \"nodes\": [6, 7], \"river_crossings\": [[7]]}]"); return any("invalid_record"); });
    refuse("road_river_crossing_with_three_nodes", [&](const fs::path& d) { replace_after(d / W, "\"roads\"", "\"routes\": []", "\"routes\": [{\"from\": 0, \"to\": 1, \"nodes\": [6, 7], \"river_crossings\": [[6, 7, 8]]}]"); return any("limit_exceeded"); });
    refuse("river_segment_with_three_nodes", [&](const fs::path& d) { replace_after(d / W, "\"water\"", "\"nodes\": 25", "\"nodes\": 25, \"river_segments\": [[1, 2, 3]]"); return any("limit_exceeded"); });
    refuse("river_segment_with_one_node", [&](const fs::path& d) { replace_after(d / W, "\"water\"", "\"nodes\": 25", "\"nodes\": 25, \"river_segments\": [[1]]"); return any("invalid_record"); });
    refuse("realm_without_capital", [&](const fs::path& d) { replace_after(d / W, "\"heroes\": {", "\"realms\": []", "\"realms\": [{\"uid\": \"r\", \"name\": \"R\", \"civilization_id\": \"g\", \"culture_id\": \"c\", \"city_uids\": []}]"); return any("missing_field"); });
    refuse("person_without_status", [&](const fs::path& d) { replace_after(d / W, "\"heroes\": {", "\"people\": []", "\"people\": [{\"uid\": \"p\", \"role\": \"reeve\", \"name\": \"P\"}]"); return any("missing_field"); });
    // MMR-27: heroes is a pinned block (version 6, a failed status is refused, a required system).
    refuse("heroes_failed", [&](const fs::path& d) { replace_after(d / W, "\"heroes\": {", "\"status\": \"ok\"", "\"status\": \"failed\""); return any("failed_block"); });
    refuse("heroes_version_7", [&](const fs::path& d) { replace_after(d / W, "\"heroes\": {", "\"version\": 6", "\"version\": 7"); return any("unsupported_version"); });
    refuse("heroes_system_missing", [&](const fs::path& d) { replace_after(d / W, "\"systems\"", "\"heroes\",", ""); return any("missing_system"); });
    refuse("culture_city_ids_not_integers", [&](const fs::path& d) { replace_after(d / W, "\"humans\"", "\"cores\": [", "\"cultures\": [{\"id\": \"c\", \"civilization_id\": \"g\", \"city_ids\": [\"0\"]}], \"cores\": ["); return any("wrong_type"); });
    refuse("terrain_biome_without_core", [&](const fs::path& d) { replace_after(d / W, "\"terrain\"", "\"version\": 6", "\"version\": 6, \"biomes\": [{\"id\": 0}]"); return any("missing_field"); });
    refuse("terrain_landform_not_string", [&](const fs::path& d) { replace_after(d / W, "\"terrain\"", "\"version\": 6", "\"version\": 6, \"landforms\": [3]"); return any("wrong_type"); });
    refuse("radius_as_string", [&](const fs::path& d) { replace_after(d / W, "\"topology\"", "31830.99", "\"31830.99\""); return any("wrong_type"); });
    // Root contract.
    refuse("wrong_schema", [&](const fs::path& d) { auto o = replace_after(d / W, "", "\"fwg.world\"", "\"fwg.other\""); return Expected{"unsupported_schema", "$.schema", W, at(o)}; });
    refuse("wrong_schema_version", [&](const fs::path& d) { auto o = replace_after(d / W, "\"schema_version\": ", "2", "3"); return Expected{"unsupported_version", "$.schema_version", W, at(o)}; });
    refuse("schema_version_overflow", [&](const fs::path& d) { auto o = replace_after(d / W, "\"schema_version\": ", "2", "4294967298"); return Expected{"out_of_range", "$.schema_version", W, at(o)}; });
    refuse("schema_version_as_string", [&](const fs::path& d) { auto o = replace_after(d / W, "\"schema_version\": ", "2", "\"2\""); return Expected{"wrong_type", "$.schema_version", W, at(o)}; });
    refuse("incomplete_stage", [&](const fs::path& d) { auto o = replace_after(d / W, "\"completed_stage\": ", "16", "15"); return Expected{"incomplete_world", "$.completed_stage", W, at(o)}; });
    refuse("completed_stage_overflow", [&](const fs::path& d) { auto o = replace_after(d / W, "\"completed_stage\": ", "16", "4294967312"); return Expected{"out_of_range", "$.completed_stage", W, at(o)}; });
    refuse("wrong_split_version", [&](const fs::path& d) { auto o = replace_after(d / W, "\"split\"", "1", "2"); return Expected{"unsupported_version", "$.split.version", W, at(o)}; });
    refuse("split_version_overflow", [&](const fs::path& d) { auto o = replace_after(d / W, "\"split\"", "1", "4294967297"); return Expected{"out_of_range", "$.split.version", W, at(o)}; });
    refuse("missing_required_block", [&](const fs::path& d) { replace_after(d / W, "", "\"roads\"", "\"road_drafts\""); return Expected{"missing_block", "$", W, at(0)}; });
    refuse("block_without_version", [&](const fs::path& d) {
        replace_after(d / W, "\"roads\"", "\"version\": 1,", "");
        return Expected{"missing_field", "$.roads", W, at(find_after(d / W, "\"roads\"", "{"))}; });
    refuse("unsupported_block_version", [&](const fs::path& d) { auto o = replace_after(d / W, "\"humans\"", "10", "11"); return Expected{"unsupported_version", "$.humans.version", W, at(o)}; });
    refuse("block_version_overflow", [&](const fs::path& d) { auto o = replace_after(d / W, "\"humans\"", "10", "4294967306"); return Expected{"out_of_range", "$.humans.version", W, at(o)}; });
    refuse("missing_topology_type", [&](const fs::path& d) {
        replace_after(d / W, "\"topology\"", "\"type\": \"sphere\",", "");
        return Expected{"unsupported_topology", "$.topology", W, at(find_after(d / W, "\"topology\"", "{"))}; });
    refuse("missing_system", [&](const fs::path& d) { replace_after(d / W, "\"systems\"", "\"world_scene\",", ""); return Expected{"missing_system", "$.systems", W, at(find_after(d / W, "\"systems\"", "["))}; });
    refuse("missing_layer", [&](const fs::path& d) { replace_after(d / W, "\"layers\"", "\"river\"", "\"rivers\""); return Expected{"missing_layer", "$.layers", W, at(find_after(d / W, "\"layers\"", "{"))}; });
    refuse("config_size_out_of_range", [&](const fs::path& d) { auto o = replace_after(d / W, "\"config\"", "\"size\": 5", "\"size\": 1027") + 8; return Expected{"out_of_range", "$.config.size", W, at(o)}; });
    refuse("missing_record_array", [&](const fs::path& d) {
        replace_after(d / W, "\"humans\"", "\"hamlets\":", "\"hamlet_rows\":");
        return Expected{"missing_field", "$.humans", W, at(find_after(d / W, "\"humans\"", "{"))}; });
    refuse("missing_key_location_sites", [&](const fs::path& d) {
        replace_after(d / W, "\"key_locations\"", "\"sites\":", "\"site_list\":");
        return Expected{"missing_field", "$.key_locations", W, at(find_after(d / W, "\"key_locations\"", "{"))}; });
    refuse("failed_key_locations", [&](const fs::path& d) {
        auto o = replace_after(d / W, "\"key_locations\"", "\"version\": 2,", "\"version\": 2,\n  \"status\": \"failed\",");
        return Expected{"failed_block", "$.key_locations.status", W, at(slurp(d / W).find("\"failed\"", o))}; });
    refuse("failed_key_locations_upper_case", [&](const fs::path& d) {
        auto o = replace_after(d / W, "\"key_locations\"", "\"version\": 2,", "\"version\": 2,\n  \"status\": \"FAILED\",");
        return Expected{"failed_block", "$.key_locations.status", W, at(slurp(d / W).find("\"FAILED\"", o))}; });
    refuse("failed_plan_block", [&](const fs::path& d) {
        auto t = slurp(d / W); auto a = t.find("\"key_location_plans\""); auto b = t.find("\n },", a);
        t.replace(a, b + 3 - a, "\"key_location_plans\": {\"version\": 1, \"status\": \"failed\", \"error\": \"kit catalogue missing\"}");
        spit(d / W, t);
        return Expected{"failed_block", "$.key_location_plans.status", W, at(t.find("\"failed\"", a))}; });
    // Syntax and numbers.
    refuse("truncated_json", [&](const fs::path& d) { auto t = slurp(d / W); t.resize(t.size() * 3 / 5); spit(d / W, t); return Expected{"truncated_json", "", W, at(t.size())}; });
    refuse("duplicate_key", [&](const fs::path& d) { auto o = replace_after(d / W, "", "\"schema_version\"", "\"schema\": \"fwg.world\",\n \"schema_version\""); return Expected{"duplicate_key", "$.schema", W, at(o)}; });
    refuse("trailing_data", [&](const fs::path& d) { auto t = slurp(d / W); spit(d / W, t + "{}"); return Expected{"trailing_data", "$", W, at(t.size())}; });
    // Layers.
    refuse("nonfinite_1e999", [&](const fs::path& d) { auto o = replace_after(d / W, "\"land\"", "16", "1e999"); return Expected{"nonfinite_number", "$.layers.land[3][3]", W, at(o)}; });
    refuse("null_layer_sample", [&](const fs::path& d) { auto o = replace_after(d / W, "\"land\"", "16", "null"); return Expected{"nonfinite_number", "$.layers.land[3][3]", W, at(o)}; });
    refuse("nan_literal", [&](const fs::path& d) { auto o = replace_after(d / W, "\"land\"", "16", "NaN"); return Expected{"invalid_json", "$.layers.land[3][3]", W, at(o)}; });
    refuse("non_integral_categorical", [&](const fs::path& d) { auto o = replace_after(d / W, "\"land\"", "16", "16.5"); return Expected{"non_integral", "$.layers.land[3][3]", W, at(o)}; });
    refuse("ragged_row", [&](const fs::path& d) {
        replace_after(d / W, "\"land\"", "   16,\n", "");
        auto t = slurp(d / W); return Expected{"bad_dimensions", "$.layers.land[3]", W, at(t.find(']', t.find("    13\n", t.find("\"land\""))))}; });
    refuse("extra_sample", [&](const fs::path& d) { auto o = replace_after(d / W, "\"land\"", "    16,\n", "    16,\n    16,\n"); auto t = slurp(d / W); return Expected{"bad_dimensions", "$.layers.land[3][5]", W, at(t.find("    13\n", o) + 4)}; });
    refuse("extra_row", [&](const fs::path& d) { auto t = slurp(d / W); auto o = t.find("\n  ]", t.find("\"land\"")); t.insert(o, ",\n  [14, 14, 14, 14, 14]"); spit(d / W, t); return Expected{"bad_dimensions", "$.layers.land[5]", W, at(o + 4)}; });
    refuse("too_few_rows", [&](const fs::path& d) {
        auto t = slurp(d / W); const auto a = t.find("\"land\""); const auto close = t.find("\n  ]", a); const auto row4 = t.rfind(",\n   [", close);
        t.erase(row4, close - row4); spit(d / W, t);
        return Expected{"bad_dimensions", "$.layers.land", W, at(t.find("\n  ]", a) + 3)}; });
    refuse("seam_mismatch", [&](const fs::path& d) { auto o = replace_after(d / W, "\"land\"", "    14,\n    11\n", "    14,\n    99\n"); return Expected{"seam_mismatch", "$.layers.land[1]", W, at(slurp(d / W).find(']', o))}; });
    refuse("pole_mismatch", [&](const fs::path& d) { auto o = replace_after(d / W, "\"land\"", "    10,\n    10,\n    10,", "    10,\n    10,\n    77,"); return Expected{"pole_mismatch", "$.layers.land[0][2]", W, at(o + 20)}; });
    // Seeds.
    refuse("layout_seed_as_number", [&](const fs::path& d) { auto o = replace_after(d / W, "\"seeds\"", "\"1555514970434891308\"", "1555514970434891308"); return Expected{"wrong_type", "$.seeds.layout", W, at(o)}; });
    refuse("layout_seed_overflow", [&](const fs::path& d) { auto o = replace_after(d / W, "\"seeds\"", "\"1555514970434891308\"", "\"18446744073709551616\""); return Expected{"out_of_range", "$.seeds.layout", W, at(o)}; });
    refuse("missing_crust_seed", [&](const fs::path& d) { replace_after(d / W, "\"seeds\"", "\"crust\": 600007910,", ""); return Expected{"missing_field", "$.seeds", W, at(find_after(d / W, "\"seeds\"", "{"))}; });
    refuse("missing_generator_version", [&](const fs::path& d) { replace_after(d / W, "", "\"generator_version\": 1,", ""); return Expected{"missing_block", "$", W, at(0)}; });
    refuse("layout_seed_leading_zero", [&](const fs::path& d) { auto o = replace_after(d / W, "\"seeds\"", "\"1555514970434891308\"", "\"01\""); return Expected{"out_of_range", "$.seeds.layout", W, at(o)}; });
    // Scene.
    refuse("null_scene", [&](const fs::path& d) { auto o = replace_after(d / W, "\"split\"", "\"scene.json\"", "null"); return Expected{"missing_file", "$.split.scene", W, at(o)}; });
    refuse("absent_scene", [&](const fs::path& d) { replace_after(d / W, "\"split\"", ",\n  \"scene\": \"scene.json\"", ""); return Expected{"missing_field", "$.split", W, at(find_after(d / W, "\"split\"", "{"))}; });
    refuse("unsafe_scene_path", [&](const fs::path& d) { auto o = replace_after(d / W, "\"split\"", "\"scene.json\"", "\"../scene.json\""); return Expected{"unsafe_path", "$.split.scene", W, at(o)}; });
    refuse("missing_scene_file", [&](const fs::path& d) { auto o = replace_after(d / W, "\"split\"", "\"scene.json\"", "\"scene-missing.json\""); return Expected{"missing_file", "$.split.scene", W, at(o)}; });
    refuse("scene_version", [&](const fs::path& d) { auto o = replace_after(d / "scene.json", "\"version\": ", "1", "2"); return Expected{"unsupported_version", "$.version", "scene.json", at(o)}; });
    refuse("scene_is_a_plan_file", [&](const fs::path& d) {
        replace_after(d / W, "\"split\"", "\"scene.json\"", "\"plans/key_location/keyloc-academy_ruin-13-2.json\"");
        return Expected{"missing_field", "$", "plans/key_location/keyloc-academy_ruin-13-2.json", at(0)}; });
    // Records and joins.
    refuse("missing_record_field", [&](const fs::path& d) { replace_after(d / W, "\"sites\"", "\"uid\": \"surface-city-0-12-elf\",", ""); return Expected{"missing_field", "$.settlements.sites[0]", W, at(record_start(d, "\"sites\"", 0))}; });
    refuse("partial_harbour_row", [&](const fs::path& d) { replace_after(d / W, "\"hamlets\": [", "\"sea_node\": 9,", ""); return Expected{"inconsistent_row", "$.humans.hamlets[1]", W, at(record_start(d, "\"hamlets\": [", 1))}; });
    refuse("null_harbour_real", [&](const fs::path& d) { auto o = replace_after(d / W, "\"hamlets\": [", "\"harbor_quality\": 0.86", "\"harbor_quality\": null") + 18; return Expected{"nonfinite_number", "$.humans.hamlets[1].harbor_quality", W, at(o)}; });
    refuse("broken_core_join", [&](const fs::path& d) { replace_after(d / W, "\"cores\"", "\"coastal-1-0\"", "\"coastal-9-9\""); return Expected{"broken_join", "$.humans.cores[1]", W, at(record_start(d, "\"cores\"", 1))}; });
    refuse("hamlet_not_listed_by_core", [&](const fs::path& d) { replace_after(d / W, "\"cores\"", "\"hamlet-0\",", ""); return Expected{"broken_join", "$.humans.hamlets[0]", W, at(record_start(d, "\"hamlets\": [", 0))}; });
    refuse("hamlet_listed_twice", [&](const fs::path& d) { replace_after(d / W, "\"cores\"", "\"coastal-0-0\"", "\"coastal-0-0\",\n      \"hamlet-0\""); return Expected{"duplicate_id", "$.humans.cores[0]", W, at(record_start(d, "\"cores\"", 0))}; });
    refuse("fortress_core_mismatch", [&](const fs::path& d) { replace_after(d / W, "\"fortresses\"", "\"core_uid\": \"surface-city-0-12-elf\"", "\"core_uid\": \"surface-city-0-17-elf\""); return Expected{"broken_join", "$.humans.cores[0]", W, at(record_start(d, "\"cores\"", 0))}; });
    refuse("fortress_sentinel_inconsistent", [&](const fs::path& d) { replace_after(d / W, "\"fortresses\"", "\"core_id\": 0", "\"core_id\": -1"); return Expected{"inconsistent_row", "$.humans.fortresses[0]", W, at(record_start(d, "\"fortresses\"", 0))}; });
    refuse("duplicate_hamlet_uid", [&](const fs::path& d) { replace_after(d / W, "\"hamlets\": [", "\"hamlet-node-11\"", "\"hamlet-node-6\""); return Expected{"duplicate_id", "$.humans.hamlets[1]", W, at(record_start(d, "\"hamlets\": [", 1))}; });
    refuse("record_without_plan", [&](const fs::path& d) {
        auto t = slurp(d / W); const auto a = t.find("\"city_plans\""); const auto second = t.find(",\n   {", t.find("\"files\"", a)); const auto end = t.find("}", second) + 1;
        t.erase(second, end - second); spit(d / W, t);
        return Expected{"missing_plan", "$.settlements.sites[1]", W, at(record_start(d, "\"sites\"", 1))}; });
    refuse("plan_entry_without_record", [&](const fs::path& d) { replace_after(d / W, "\"hamlet_plans\"", "\"hamlet_id\": \"hamlet-0\"", "\"hamlet_id\": \"hamlet-999\""); return Expected{"broken_join", "$.hamlet_plans.files[0]", W, at(files_entry(d, "hamlet_plans", 0))}; });
    // Indexed paths.
    const std::string city0 = "\"plans/city/surface-city-0-12-elf-2.json\"";
    refuse("absolute_plan_path", [&](const fs::path& d) { replace_after(d / W, "\"city_plans\"", city0, "\"C:/Windows/win.ini\""); return Expected{"unsafe_path", "$.city_plans.files[0]", W, at(files_entry(d, "city_plans", 0))}; });
    refuse("traversal_plan_path", [&](const fs::path& d) { replace_after(d / W, "\"city_plans\"", city0, "\"plans/../../outside.json\""); return Expected{"unsafe_path", "$.city_plans.files[0]", W, at(files_entry(d, "city_plans", 0))}; });
    refuse("backslash_plan_path", [&](const fs::path& d) { replace_after(d / W, "\"city_plans\"", city0, "\"plans\\\\city\\\\surface-city-0-12-elf-2.json\""); return Expected{"unsafe_path", "$.city_plans.files[0]", W, at(files_entry(d, "city_plans", 0))}; });
    refuse("trailing_dot_alias", [&](const fs::path& d) { replace_after(d / W, "\"city_plans\"", city0, "\"plans/city/surface-city-0-12-elf-2.json.\""); return Expected{"unsafe_path", "$.city_plans.files[0]", W, at(files_entry(d, "city_plans", 0))}; });
    refuse("case_alias", [&](const fs::path& d) { replace_after(d / W, "\"city_plans\"", city0, "\"plans/city/SURFACE-city-0-12-elf-2.json\""); return Expected{"unsafe_path", "$.city_plans.files[0]", W, at(files_entry(d, "city_plans", 0))}; });
    refuse("device_name", [&](const fs::path& d) { replace_after(d / W, "\"city_plans\"", city0, "\"plans/city/NUL.json\""); return Expected{"unsafe_path", "$.city_plans.files[0]", W, at(files_entry(d, "city_plans", 0))}; });
    refuse("hard_link", [&](const fs::path& d) {
        fs::create_hard_link(d / "plans/city/surface-city-0-12-elf-2.json", d / "plans/city/linked.json");
        replace_after(d / W, "\"city_plans\"", city0, "\"plans/city/linked.json\"");
        return Expected{"unsafe_path", "$.city_plans.files[0]", W, at(files_entry(d, "city_plans", 0))}; });
    refuse("missing_plan_file", [&](const fs::path& d) { fs::remove(d / "plans/hamlet/coastal-0-0-2.json"); return Expected{"missing_file", "$.hamlet_plans.files[1]", W, at(files_entry(d, "hamlet_plans", 1))}; });
    refuse("junction_escapes_root", [&](const fs::path& d) {
        const fs::path outside = work / "outside_root";
        fs::remove_all(outside); fs::create_directories(outside);
        spit(outside / "fortress-0-2.json", slurp(d / "plans/castle/fortress-0-2.json"));
        fs::remove_all(d / "plans/castle");
        const std::string command = "cmd /c mklink /J \"" + (d / "plans/castle").make_preferred().string() + "\" \"" + fs::path(outside).make_preferred().string() + "\" >NUL";
        if (std::system(command.c_str()) != 0) throw std::runtime_error("mklink /J failed");
        return Expected{"unsafe_path", "$.castle_plans.files[0]", W, at(files_entry(d, "castle_plans", 0))}; });
    // Plan files.
    const std::string harbour_plan = "plans/hamlet/coastal-0-0-2.json";
    const std::string city_plan = "plans/city/surface-city-0-12-elf-2.json";
    refuse("plan_identity_mismatch", [&](const fs::path& d) { auto o = replace_after(d / harbour_plan, "", "\"hamlet-node-11\"", "\"hamlet-node-12\""); return Expected{"broken_join", "$.hamlet_uid", harbour_plan, at(o)}; });
    refuse("plan_version_mismatch", [&](const fs::path& d) { auto o = replace_after(d / city_plan, "\"version\": ", "8", "9"); return Expected{"unsupported_version", "$.version", city_plan, at(o)}; });
    refuse("plan_version_overflow", [&](const fs::path& d) { auto o = replace_after(d / city_plan, "\"version\": ", "8", "4294967304"); return Expected{"unsupported_version", "$.version", city_plan, at(o)}; });
    refuse("plan_trailing_data", [&](const fs::path& d) { auto t = slurp(d / city_plan); spit(d / city_plan, t + "{}"); return Expected{"trailing_data", "$", city_plan, at(t.size())}; });
    refuse("plan_duplicate_member", [&](const fs::path& d) { auto o = replace_after(d / city_plan, "", "\"city_uid\"", "\"version\": 8,\n \"city_uid\""); return Expected{"duplicate_key", "$.version", city_plan, at(o)}; });
    refuse("castle_plan_wrong_kind", [&](const fs::path& d) { const std::string P = "plans/castle/fortress-0-2.json"; auto o = replace_after(d / P, "", "\"castle\"", "\"city\""); return Expected{"broken_join", "$.kind", P, at(o)}; });
    refuse("plan_missing_id", [&](const fs::path& d) { replace_after(d / harbour_plan, "", "\"hamlet_id\": \"coastal-0-0\",", ""); return Expected{"broken_join", "$", harbour_plan, at(0)}; });
    // Budgets.
    Compatibility tight = contract;
    tight.max_retained_bytes = 2048;
    refuse("retained_budget", [&](const fs::path&) { return Expected{"limit_exceeded", "", W, std::nullopt}; }, &tight);
    // Root members (index entries) are charged too: 3000 extra root blocks exceed a 400 KB budget.
    Compatibility root_budget = contract;
    root_budget.max_retained_bytes = 400000;
    refuse("root_member_budget", [&](const fs::path& d) {
        std::string extra;
        for (int i = 0; i < 3000; ++i) extra += "\"x" + std::to_string(100000 + i) + "\": {\"version\": 1},\n ";
        replace_after(d / W, "", "\"future_block\"", extra + "\"future_block\"");
        return Expected{"limit_exceeded", "", W, std::nullopt}; }, &root_budget);
}
}  // namespace

int main(int argc, char** argv) {
    if (argc != 4) { std::cerr << "usage: source_reader_tests FIXTURE_DIR CONTRACT_JSON WORK_DIR\n"; return 2; }
    fixture = argv[1]; work = argv[3];
    fs::remove_all(work); fs::create_directories(work);
    try {
        contract = load_compatibility(argv[2]);
        tokenizer();
        positive();
        identity_contract();
        accepted_variants();
        refusals();
    } catch (const std::exception& e) {
        std::cout << "FAIL  unexpected: " << e.what() << "\n";
        ++failures;
    }
    std::cout << (failures ? "FAILED " : "PASSED ") << (checks - failures) << "/" << checks << " source contract checks\n";
    return failures ? 1 : 0;
}
