#include "worldbridge/source_world.hpp"

#include <algorithm>
#include <cctype>
#include <charconv>
#include <chrono>
#include <cmath>
#include <cstdlib>
#include <functional>
#include <limits>
#include <set>
#include <sstream>
#include <unordered_map>
#include <unordered_set>

namespace worldbridge::source {
namespace {
using json::Stream;
using json::Token;

bool integer_text(const std::string& text) {
    if (text.empty() || text == "-") return false;
    for (std::size_t i = text[0] == '-' ? 1 : 0; i < text.size(); ++i) if (text[i] < '0' || text[i] > '9') return false;
    return true;
}

std::string lower(std::string text) {
    for (auto& ch : text) ch = static_cast<char>(std::tolower(static_cast<unsigned char>(ch)));
    return text;
}

bool canonical_u64(const std::string& text) {
    std::uint64_t value = 0;
    const auto r = std::from_chars(text.data(), text.data() + text.size(), value);
    return !text.empty() && text.size() <= 20 && r.ec == std::errc() && r.ptr == text.data() + text.size() && (text.size() == 1 || text[0] != '0');
}

// Compact record position; a full Location (with file and JSON path) is built only for an error.
struct Pos {
    std::uint64_t offset = 0, line = 1, column = 1;
    Location at(const std::string& file, const std::string& path) const { return Location{file, offset, line, column, path}; }
};
Pos pos_of(const Location& l) { return Pos{l.offset, l.line, l.column}; }

// Typed reads over the pull stream. When `primed`, the value's first token was already consumed
// (array elements, member dispatch), so the next read uses it instead of advancing.
class Cursor {
public:
    explicit Cursor(Stream& stream) : s(stream) {}
    Stream& s;
    bool primed = false;
    Token advance() { if (primed) primed = false; else s.next(); return s.token(); }
    [[noreturn]] void fail(const std::string& code, const std::string& message) { s.fail(code, message); }
    void want(Token t, const char* what) { if (s.token() != t) fail("wrong_type", std::string("Expected ") + what); }
    std::string string() { advance(); want(Token::String, "a string"); return s.text(); }
    std::optional<std::string> string_or_null() { if (advance() == Token::Null) return std::nullopt; want(Token::String, "a string or null"); return s.text(); }
    std::int64_t integer() {
        advance();
        if (s.token() != Token::Number || !integer_text(s.text())) fail("wrong_type", "Expected an integer");
        std::int64_t n = 0;
        const auto& t = s.text();
        const auto r = std::from_chars(t.data(), t.data() + t.size(), n);
        if (r.ec != std::errc() || r.ptr != t.data() + t.size()) fail("out_of_range", "Integer does not fit 64 bits");
        return n;
    }
    std::optional<std::int64_t> integer_or_null() { if (advance() == Token::Null) return std::nullopt; primed = true; return integer(); }
    // Versions and small counts: full 64-bit value range-checked, never silently narrowed.
    int bounded(std::int64_t low, std::int64_t high, const char* what) {
        const auto n = integer();
        if (n < low || n > high) fail("out_of_range", std::string(what) + " " + std::to_string(n) + " is outside " + std::to_string(low) + ".." + std::to_string(high));
        return static_cast<int>(n);
    }
    int version() { return bounded(0, 1000000, "Version"); }
    double number() { advance(); want(Token::Number, "a number"); return std::strtod(s.text().c_str(), nullptr); }
    // FWGC++ writes non-finite reals as null (and early-block infinities as 1e999, refused by the tokenizer).
    double finite() { if (advance() == Token::Null) fail("nonfinite_number", "Real value is null (non-finite in the source)"); primed = true; return number(); }
    std::optional<double> number_or_null() { if (advance() == Token::Null) return std::nullopt; want(Token::Number, "a number or null"); return std::strtod(s.text().c_str(), nullptr); }
    bool boolean() { advance(); if (s.token() != Token::True && s.token() != Token::False) fail("wrong_type", "Expected a boolean"); return s.token() == Token::True; }
    std::string scalar_text() {
        advance();
        if (s.token() == Token::BeginObject || s.token() == Token::BeginArray) fail("wrong_type", "Expected a scalar");
        return s.text();
    }
    void skip() { advance(); s.skip(); }
    void object(const std::function<void(const std::string&)>& member) {
        advance(); want(Token::BeginObject, "an object");
        for (;;) {
            s.next();
            if (s.token() == Token::EndObject) return;
            const std::string key = s.text();
            member(key);
            if (primed) fail("internal_error", "Member value not consumed");
        }
    }
    std::size_t array(const std::function<void(std::size_t)>& element, std::size_t max_elements) {
        advance(); want(Token::BeginArray, "an array");
        for (std::size_t i = 0;; ++i) {
            s.next();
            if (s.token() == Token::EndArray) return i;
            if (i >= max_elements) fail("limit_exceeded", "Array exceeds the record bound");
            primed = true;
            element(i);
            if (primed) fail("internal_error", "Array element not consumed");
        }
    }
    std::size_t count(std::size_t max_elements) { return array([&](std::size_t) { skip(); }, max_elements); }
};

// Retained-memory budget for everything the reader keeps after the stream moves on.
class Budget {
public:
    Budget(Stream& s, std::uint64_t limit) : stream(s), cap(limit) {}
    void add(std::uint64_t bytes) { used += bytes; if (used > cap) stream.fail("limit_exceeded", "Retained record budget exceeded"); }
    void add(const std::string& text) { add(text.size() + 32); }
    std::uint64_t used = 0;
private:
    Stream& stream;
    std::uint64_t cap;
};

// Bounded object record: reads only the listed members, skips the rest, enforces presence.
class Record {
public:
    Record(Cursor& c, std::string what) : cursor(c), kind(std::move(what)) {}
    void read(const std::function<bool(const std::string&)>& member) {
        cursor.advance();
        cursor.want(Token::BeginObject, ("a " + kind + " object").c_str());
        start = cursor.s.where();
        cursor.primed = true;
        cursor.object([&](const std::string& key) {
            seen.insert(key);
            if (!member(key)) cursor.skip();
        });
    }
    bool has(const std::string& key) const { return seen.count(key) != 0; }
    void require(std::initializer_list<const char*> keys) const {
        for (const char* key : keys)
            if (!has(key)) throw Error("missing_field", kind + " lacks required field '" + key + "'", start);
    }
    [[noreturn]] void fail(const std::string& code, const std::string& message) const { throw Error(code, kind + ": " + message, start); }
    Location start;
private:
    Cursor& cursor;
    std::string kind;
    std::set<std::string> seen;
};

bool reserved_device(std::string stem) {
    for (auto& ch : stem) ch = static_cast<char>(std::toupper(static_cast<unsigned char>(ch)));
    if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || stem == "CONIN$" || stem == "CONOUT$") return true;
    return stem.size() == 4 && (stem.rfind("COM", 0) == 0 || stem.rfind("LPT", 0) == 0) && stem[3] >= '0' && stem[3] <= '9';
}

// FWGC++ names are [A-Za-z0-9._-] joined by '/', so anything else is refused before touching the disk.
bool safe_relative(const std::string& text) {
    if (text.empty() || text.size() > 1024 || text.front() == '/') return false;
    std::size_t start = 0;
    for (;;) {
        const auto end = text.find('/', start);
        const std::string part = text.substr(start, end == std::string::npos ? std::string::npos : end - start);
        if (part.empty() || part == "." || part == ".." || part.back() == '.' || part.back() == ' ') return false;
        for (char ch : part) if (!(std::isalnum(static_cast<unsigned char>(ch)) || ch == '-' || ch == '_' || ch == '.')) return false;
        if (reserved_device(part.substr(0, part.find('.')))) return false;
        if (end == std::string::npos) return true;
        start = end + 1;
    }
}

// Resolves an indexed path strictly inside the immutable staging root: no aliases (case, 8.3 names,
// trailing dots), no reparse-point escapes, no hard links to files elsewhere.
fs::path resolve_inside(const fs::path& root, const std::string& relative, const Location& where, std::uint64_t* bytes) {
    if (!safe_relative(relative)) throw Error("unsafe_path", "Indexed path is absolute, traverses, or is malformed: '" + relative + "'", where);
    const fs::path candidate = root / fs::path(relative);
    std::error_code ec;
    if (!fs::exists(candidate, ec)) throw Error("missing_file", "Indexed file does not exist: '" + relative + "'", where);
    const fs::path real = fs::canonical(candidate, ec);
    if (ec) throw Error("unsafe_path", "Indexed file cannot be resolved: '" + relative + "'", where);
    const auto inside = real.lexically_relative(root);
    if (inside.empty() || json::utf8(*inside.begin()) == "..") throw Error("unsafe_path", "Indexed file resolves outside the staging root: '" + relative + "'", where);
    if (json::utf8(inside) != relative) throw Error("unsafe_path", "Indexed path is an alias of '" + json::utf8(inside) + "'", where);
    if (!fs::is_regular_file(real, ec)) throw Error("missing_file", "Indexed path is not a regular file: '" + relative + "'", where);
    if (fs::hard_link_count(real, ec) != 1 || ec) throw Error("unsafe_path", "Indexed file has other hard links: '" + relative + "'", where);
    if (bytes) *bytes = fs::file_size(real);
    return real;
}

struct Core { std::int64_t site_id = 0; std::string culture_id; std::vector<std::string> hamlet_ids, fortress_ids; Pos at; };
struct IndexEntry { std::string kind, id, path; Pos at; std::size_t index = 0; };

struct PlanIndexSpec { const char* block; const char* kind; const char* id_field; };
constexpr PlanIndexSpec plan_specs[] = {
    {"city_plans", "city", "city_uid"}, {"hamlet_plans", "hamlet", "hamlet_id"},
    {"castle_plans", "castle", "fortress_id"}, {"key_location_plans", "key_location", "location_id"}};

// The record arrays the compiler needs; each must be present (it may be empty).
constexpr const char* required_arrays[] = {"settlements.sites", "humans.cores", "humans.hamlets", "humans.fortresses", "key_locations.sites",
                                           "city_plans.files", "hamlet_plans.files", "castle_plans.files", "key_location_plans.files"};

class RootPass {
public:
    RootPass(SourceWorld& w, const Compatibility& c)
        : world(w), contract(c), stream(w.root / "world.json"), cursor(stream), budget(stream, c.max_retained_bytes), file(json::utf8((w.root / "world.json"))) {}

    void run() {
        cursor.object([&](const std::string& key) {
            stream.next();
            cursor.primed = true;
            const Location at = stream.where();
            member(key, at);
            world.members[key] = Span{at, stream.bytes_read()};
            // members + where each hold the key and a Location (file and JSON path) per root member.
            budget.add(2 * (key.size() + at.file.size() + at.path.size() + sizeof(Span) + 64));
        });
        stream.next();  // trailing data check
        world.statistics.root_bytes = stream.bytes_read();
        world.statistics.retained_bytes = budget.used;
    }

    std::map<std::string, Location> where;   // locations of contract-relevant values
    std::set<std::string> arrays;            // required record arrays seen
    std::vector<Core> cores;
    std::vector<IndexEntry> index;
    std::vector<Pos> city_at, hamlet_at, castle_at, key_at;
    const std::string file;

    Location record(const char* path, const std::vector<Pos>& positions, std::size_t i) const {
        return positions[i].at(file, std::string(path) + "[" + std::to_string(i) + "]");
    }

private:
    SourceWorld& world;
    const Compatibility& contract;
    Stream stream;
    Cursor cursor;
    Budget budget;
    Metadata& meta() { return world.metadata; }
    std::string keep(std::string text) { budget.add(text); return text; }

    void member(const std::string& key, const Location& at) {
        where[key] = at;
        if (key == "schema") meta().schema = cursor.string();
        else if (key == "schema_version") meta().schema_version = cursor.version();
        else if (key == "generator_version") meta().generator_version = cursor.scalar_text();  // descriptive
        else if (key == "completed_stage") meta().completed_stage = cursor.bounded(0, 1000, "completed_stage");
        else if (key == "split") cursor.object([&](const std::string& k) {
            if (k == "version") { meta().split_version = cursor.version(); where["split.version"] = stream.where(); }
            else if (k == "scene") {
                const auto scene = cursor.string_or_null();
                where["split.scene"] = stream.where();
                if (!scene) cursor.fail("missing_file", "split.scene is null; the compiler requires the world scene");
                meta().scene_path = *scene;
            } else cursor.skip();
        });
        else if (key == "config") cursor.object([&](const std::string& k) {
            if (k == "size") { meta().config_size = static_cast<std::size_t>(cursor.bounded(3, static_cast<std::int64_t>(contract.max_side), "config.size")); where["config.size"] = stream.where(); }
            else cursor.skip();
        });
        else if (key == "topology") cursor.object([&](const std::string& k) {
            if (k == "type") { meta().topology_type = cursor.string(); where["topology.type"] = stream.where(); }
            else if (k == "duplicate_seam") { meta().duplicate_seam = cursor.boolean(); where["topology.duplicate_seam"] = stream.where(); }
            else if (k == "radius_m") meta().radius_m = cursor.number_or_null();  // null leaves it unset; the projector refuses
            else if (k == "longitude_deg") cursor.array([&](std::size_t) { meta().longitude_deg.push_back(cursor.finite()); }, 2);
            else if (k == "latitude_deg") cursor.array([&](std::size_t) { meta().latitude_deg.push_back(cursor.finite()); }, 2);
            else cursor.skip();
        });
        else if (key == "effective_config") cursor.object([&](const std::string& k) {
            // Metres. A null (non-finite in the source) leaves the value unset; the projector refuses it.
            if (k == "sea_level") meta().sea_level_m = cursor.number_or_null();
            else if (k == "globe_radius") meta().effective_globe_radius_m = cursor.number_or_null();
            else if (k == "settlement_count") meta().settlement_count = cursor.integer_or_null();
            else if (k == "hamlets_per_core") meta().hamlets_per_core = cursor.integer_or_null();
            else if (k == "fortress_count") meta().fortress_count = cursor.integer_or_null();
            else cursor.skip();
        });
        else if (key == "seeds") cursor.object([&](const std::string& k) {
            if (k == "layout") {
                // A 64-bit value: it must stay a decimal string; a JSON number would lose precision.
                cursor.advance();
                where["seeds.layout"] = stream.where();
                if (stream.token() != Token::String) cursor.fail("wrong_type", "seeds.layout must be a decimal string (64-bit value)");
                if (!canonical_u64(stream.text())) cursor.fail("out_of_range", "seeds.layout is not a canonical unsigned 64-bit decimal");
                meta().layout_seed = stream.text();
            } else if (k == "crust") { meta().crust_seed = cursor.integer(); where["seeds.crust"] = stream.where(); }
            else if (k == "detail") { meta().detail_seed = cursor.integer(); where["seeds.detail"] = stream.where(); }
            else cursor.skip();
        });
        else if (key == "systems") cursor.array([&](std::size_t) { meta().systems.push_back(keep(cursor.string())); }, 4096);
        else if (key == "layers") layers();
        else if (key == "settlements") versioned(key, [&](const std::string& k) {
            if (k == "founding") {
                cursor.object([&](const std::string& f) {
                    if (f == "target_cities") meta().founding_target_cities = cursor.integer_or_null();
                    else if (f == "placed_cities") meta().founding_placed_cities = cursor.integer_or_null();
                    else cursor.skip();
                });
                return true;
            }
            if (k != "sites") return false;
            arrays.insert("settlements.sites"); cities(); return true;
        });
        else if (key == "heroes") versioned(key, [&](const std::string& k) {
            if (k == "realms") realms(); else if (k == "people") people(); else return false;
            return true;
        });
        else if (key == "humans") versioned(key, [&](const std::string& k) {
            if (k == "cultures") { culture_entries(); return true; }
            if (k == "cores") read_cores(); else if (k == "hamlets") hamlets(); else if (k == "fortresses") castles(); else return false;
            arrays.insert("humans." + k);
            return true;
        });
        else if (key == "key_locations") versioned(key, [&](const std::string& k) { if (k != "sites") return false; arrays.insert("key_locations.sites"); key_locations(); return true; });
        else if (key == "terrain") versioned(key, [&](const std::string& k) {
            if (k == "biomes") biomes();
            else if (k == "landforms") cursor.array([&](std::size_t) { world.landforms.push_back(keep(cursor.string())); }, 256);
            else return false;
            return true;
        });
        else if (key == "roads") versioned(key, [&](const std::string& k) { if (k != "routes") return false; routes(); return true; });
        else if (key == "water") versioned(key, [&](const std::string& k) { if (k != "river_segments") return false; rivers(); return true; });
        else {
            for (const auto& spec : plan_specs)
                if (key == spec.block) {
                    versioned(key, [&](const std::string& k) { if (k != "files") return false; arrays.insert(key + ".files"); plan_index(spec); return true; });
                    return;
                }
            // Any other block: record an integer version if it declares one; skip the rest (still tokenized).
            cursor.advance();
            if (stream.token() != Token::BeginObject) { stream.skip(); return; }
            cursor.primed = true;
            versioned(key, [](const std::string&) { return false; });
        }
    }

    // Contract blocks: strict integer version, and a "failed" status is refused with its location.
    // Other blocks: an integer version is recorded, anything else in "version" is skipped.
    void versioned(const std::string& block, const std::function<bool(const std::string&)>& member) {
        const bool required = contract.block_versions.count(block) != 0;
        cursor.object([&](const std::string& k) {
            if (k == "version") {
                if (required) { meta().block_versions[block] = cursor.version(); where[block + ".version"] = stream.where(); return; }
                cursor.advance();
                if (stream.token() == Token::Number && integer_text(stream.text()) && stream.text().size() <= 7) {
                    meta().block_versions[block] = std::stoi(stream.text());
                    budget.add(block.size() + 64);
                }
                else stream.skip();
                return;
            }
            if (required && k == "status") {
                cursor.advance();
                if (stream.token() == Token::String && lower(stream.text()) == "failed") cursor.fail("failed_block", block + " reports status '" + stream.text() + "'");
                stream.skip();
                return;
            }
            if (!member(k)) cursor.skip();
        });
    }

    void layers() {
        std::set<std::string> integral(contract.integral_layers.begin(), contract.integral_layers.end());
        cursor.object([&](const std::string& name) {
            if (world.layers.size() >= 4096) cursor.fail("limit_exceeded", "Too many layers");
            stream.next();
            cursor.primed = true;
            LayerInfo info{keep(name), stream.where()};
            budget.add(sizeof(LayerInfo) + 160);
            raster(name, info, integral.count(name) != 0);
            world.layers[name] = info;
            world.statistics.layers++;
        });
    }

    // Validates one n x n layer while streaming: dimensions, finite numbers, seam and pole identity,
    // and integer values for the contract's categorical layers.
    void raster(const std::string& name, LayerInfo& info, bool must_be_integral) {
        const std::size_t side = meta().grid_side ? meta().grid_side : meta().config_size;
        if (!side) cursor.fail("bad_dimensions", "Layer appears before config.size: " + name);
        bool first_sample = true;
        const std::size_t rows = cursor.array([&](std::size_t row) {
            if (row >= side) cursor.fail("bad_dimensions", "Layer '" + name + "' has more than " + std::to_string(side) + " rows");
            double first = 0, last = 0;
            const std::size_t columns = cursor.array([&](std::size_t column) {
                if (column >= side) cursor.fail("bad_dimensions", "Layer '" + name + "' row has more than " + std::to_string(side) + " samples");
                cursor.advance();
                if (stream.token() == Token::Null) cursor.fail("nonfinite_number", "Layer sample is null (FWGC++ writes NaN as null)");
                if (stream.token() != Token::Number) cursor.fail("wrong_type", "Layer sample must be a number");
                const double v = std::strtod(stream.text().c_str(), nullptr);
                if (std::floor(v) != v) {
                    info.integral = false;
                    if (must_be_integral) cursor.fail("non_integral", "Categorical layer '" + name + "' holds a non-integer sample");
                }
                if (first_sample) { info.minimum = info.maximum = v; first_sample = false; }
                info.minimum = std::min(info.minimum, v); info.maximum = std::max(info.maximum, v);
                if (column == 0) first = v;
                else if ((row == 0 || row + 1 == side) && std::abs(v - first) > contract.pole_tolerance)
                    cursor.fail("pole_mismatch", "Pole row of layer '" + name + "' is not constant");
                last = v;
            }, side + 1);
            if (columns != side) cursor.fail("bad_dimensions", "Layer '" + name + "' row has " + std::to_string(columns) + " samples, expected " + std::to_string(side));
            if (contract.duplicate_seam && std::abs(first - last) > contract.seam_tolerance)
                cursor.fail("seam_mismatch", "Layer '" + name + "' seam columns differ in row " + std::to_string(row));
            world.statistics.layer_samples += columns;
        }, side + 1);
        if (rows != side) cursor.fail("bad_dimensions", "Layer '" + name + "' has " + std::to_string(rows) + " rows, expected " + std::to_string(side));
        meta().grid_side = side;
    }

    void cities() {
        cursor.array([&](std::size_t) {
            City c;
            Record r(cursor, "settlements.sites[] record");
            r.read([&](const std::string& k) {
                if (k == "id") c.site_id = cursor.integer();
                else if (k == "uid") c.uid = keep(cursor.string());
                else if (k == "name") c.name = keep(cursor.string());
                else if (k == "kind") c.kind = keep(cursor.string());
                else if (k == "city_class") c.city_class = keep(cursor.string());
                else if (k == "civilization_id") c.civilization_id = keep(cursor.string());
                else if (k == "layer") c.layer = keep(cursor.string());
                else if (k == "node") c.node = cursor.integer();
                else if (k == "x") c.x = cursor.integer();
                else if (k == "z") c.z = cursor.integer();
                else if (k == "population_estimate") c.population_estimate = cursor.integer();
                else if (k == "freshwater_distance_m") c.freshwater_distance_m = cursor.number_or_null();
                else if (k == "urban_population_estimate") c.urban_population_estimate = cursor.integer_or_null();
                else if (k == "rural_population_estimate") c.rural_population_estimate = cursor.integer_or_null();
                else if (k == "founding_capital") c.founding_capital = cursor.boolean();
                else return false;
                return true;
            });
            r.require({"id", "uid", "x", "z", "node", "kind"});
            if (c.uid.empty()) r.fail("missing_field", "empty uid");
            budget.add(sizeof(City) + sizeof(Pos));
            city_at.push_back(pos_of(r.start));
            world.cities.push_back(std::move(c));
        }, contract.max_records);
    }

    void read_cores() {
        cursor.array([&](std::size_t) {
            Core c;
            Record r(cursor, "humans.cores[] record");
            auto ids = [&](std::vector<std::string>& out) { cursor.array([&](std::size_t) { out.push_back(keep(cursor.string())); }, contract.max_records); };
            r.read([&](const std::string& k) {
                if (k == "site_id") c.site_id = cursor.integer();
                else if (k == "culture_id") c.culture_id = keep(cursor.string());
                else if (k == "hamlet_ids") ids(c.hamlet_ids);
                else if (k == "fortress_ids") ids(c.fortress_ids);
                else return false;
                return true;
            });
            r.require({"site_id", "hamlet_ids", "fortress_ids"});
            c.at = pos_of(r.start);
            budget.add(sizeof(Core));
            cores.push_back(std::move(c));
        }, contract.max_records);
    }

    void hamlets() {
        static const std::set<std::string> harbour_fields{"sea_node", "harbor_quality", "landing_distance_m", "layer", "trade_terminal", "winter_fishing_fraction", "fishing_nodes", "monthly_fish"};
        cursor.array([&](std::size_t) {
            Hamlet h; Harbour harbour; std::size_t harbour_seen = 0;
            Record r(cursor, "humans.hamlets[] record");
            r.read([&](const std::string& k) {
                if (k == "id") h.id = keep(cursor.string());
                else if (k == "uid") h.uid = keep(cursor.string());
                else if (k == "kind") h.kind = keep(cursor.string());
                else if (k == "role") h.role = keep(cursor.string());
                else if (k == "culture_id") h.culture_id = keep(cursor.string());
                else if (k == "founded_by") { auto v = cursor.string_or_null(); if (v) h.founded_by = keep(*v); }
                else if (k == "core_id") h.core_id = cursor.integer();
                else if (k == "node") h.node = cursor.integer();
                else if (k == "x") h.x = cursor.integer();
                else if (k == "z") h.z = cursor.integer();
                else if (harbour_fields.count(k)) {
                    ++harbour_seen;
                    if (k == "sea_node") harbour.sea_node = cursor.integer();
                    else if (k == "harbor_quality") harbour.harbor_quality = cursor.finite();
                    else if (k == "landing_distance_m") harbour.landing_distance_m = cursor.finite();
                    else if (k == "layer") harbour.layer = keep(cursor.string());
                    else if (k == "trade_terminal") harbour.trade_terminal = cursor.boolean();
                    else if (k == "winter_fishing_fraction") harbour.winter_fishing_fraction = cursor.finite();
                    else if (k == "fishing_nodes") harbour.fishing_nodes = cursor.count(contract.max_records);
                    else harbour.monthly_fish = cursor.count(12);
                } else return false;
                return true;
            });
            r.require({"id", "uid", "kind", "role", "core_id", "node", "x", "z"});
            if (h.kind != "hamlet") r.fail("unsupported_row", "kind '" + h.kind + "' is not 'hamlet'");
            // Dispatch on the actual fields, all or none: roles vary within the harbour shape
            // ("harbor", "fishing", "harbor + fishing", "landing" observed), so the role does not decide it.
            if (harbour_seen != 0 && harbour_seen != harbour_fields.size()) r.fail("inconsistent_row", "partial harbour-shaped row (" + std::to_string(harbour_seen) + " of " + std::to_string(harbour_fields.size()) + " harbour fields)");
            if (harbour_seen) h.harbour = harbour;
            budget.add(sizeof(Hamlet) + sizeof(Pos));
            hamlet_at.push_back(pos_of(r.start));
            world.hamlets.push_back(std::move(h));
        }, contract.max_records);
    }

    void castles() {
        cursor.array([&](std::size_t) {
            Castle c;
            Record r(cursor, "humans.fortresses[] record");
            r.read([&](const std::string& k) {
                if (k == "id") c.id = keep(cursor.string());
                else if (k == "uid") c.uid = keep(cursor.string());
                else if (k == "kind") c.kind = keep(cursor.string());
                else if (k == "core_uid") c.core_uid = keep(cursor.string());
                else if (k == "culture_id") c.culture_id = keep(cursor.string());
                else if (k == "founded_by") { auto v = cursor.string_or_null(); if (v) c.founded_by = keep(*v); }
                else if (k == "core_id") c.core_id = cursor.integer();
                else if (k == "node") c.node = cursor.integer();
                else if (k == "x") c.x = cursor.integer();
                else if (k == "z") c.z = cursor.integer();
                else return false;
                return true;
            });
            r.require({"id", "uid", "kind", "core_id", "core_uid", "node", "x", "z"});
            if (c.kind != "fortress") r.fail("unsupported_row", "kind '" + c.kind + "' is not 'fortress'");
            // The writer marks a fort without a garrison city with core_id -1 and core_uid "".
            c.garrisoned = !(c.core_id == -1 && c.core_uid.empty());
            if (!c.garrisoned != (c.core_id == -1 || c.core_uid.empty())) r.fail("inconsistent_row", "core_id and core_uid disagree about the garrison city");
            budget.add(sizeof(Castle) + sizeof(Pos));
            castle_at.push_back(pos_of(r.start));
            world.castles.push_back(std::move(c));
        }, contract.max_records);
    }

    void key_locations() {
        cursor.array([&](std::size_t) {
            KeyLocation k;
            Record r(cursor, "key_locations.sites[] record");
            r.read([&](const std::string& f) {
                if (f == "id") k.id = keep(cursor.string());
                else if (f == "kind") k.kind = keep(cursor.string());
                else if (f == "family") k.family = keep(cursor.string());
                else if (f == "name") k.name = keep(cursor.string());
                else if (f == "layer") k.layer = keep(cursor.string());
                else if (f == "node") k.node = cursor.integer();
                else return false;
                return true;
            });
            r.require({"id", "kind", "node"});
            budget.add(sizeof(KeyLocation) + sizeof(Pos));
            key_at.push_back(pos_of(r.start));
            world.key_locations.push_back(std::move(k));
        }, contract.max_records);
    }

    // roads.routes[]: node paths and producer-reported river crossings; the other fields are skipped.
    void routes() {
        cursor.array([&](std::size_t) {
            Route route;
            Record r(cursor, "roads.routes[] record");
            r.read([&](const std::string& k) {
                if (k == "from") route.from = cursor.integer();
                else if (k == "to") route.to = cursor.integer();
                else if (k == "length_m") route.length_m = cursor.finite();
                else if (k == "nodes") cursor.array([&](std::size_t) { budget.add(sizeof(std::int64_t)); route.nodes.push_back(cursor.integer()); }, contract.max_records);
                else if (k == "river_crossings") cursor.array([&](std::size_t) {
                    std::pair<std::int64_t, std::int64_t> step;
                    const auto count = cursor.array([&](std::size_t i) { const auto node = cursor.integer(); if (i == 0) step.first = node; else step.second = node; }, 2);
                    if (count != 2) cursor.fail("invalid_record", "roads.routes[].river_crossings[] must be [node, node]");
                    budget.add(sizeof(step));
                    route.river_crossings.push_back(step);
                }, contract.max_records);
                else return false;
                return true;
            });
            r.require({"from", "to", "nodes"});
            if (route.nodes.size() < 2) r.fail("invalid_record", "a route needs at least two nodes");
            budget.add(sizeof(Route));
            world.routes.push_back(std::move(route));
        }, contract.max_records);
    }

    std::vector<std::string> strings(std::size_t max) {
        std::vector<std::string> out;
        cursor.array([&](std::size_t) { out.push_back(keep(cursor.string())); }, max);
        return out;
    }

    // heroes.realms[]: generated polities and the cities they hold.
    void realms() {
        cursor.array([&](std::size_t) {
            Realm realm;
            Record r(cursor, "heroes.realms[] record");
            r.read([&](const std::string& k) {
                if (k == "uid") realm.uid = keep(cursor.string());
                else if (k == "name") realm.name = keep(cursor.string());
                else if (k == "english_name") realm.english_name = keep(cursor.string());
                else if (k == "civilization_id") realm.civilization_id = keep(cursor.string());
                else if (k == "civilization_name") { auto v = cursor.string_or_null(); if (v) realm.civilization_name = keep(*v); }
                else if (k == "parent_race_id") { auto v = cursor.string_or_null(); if (v) realm.parent_race_id = keep(*v); }
                else if (k == "culture_id") realm.culture_id = keep(cursor.string());
                else if (k == "capital_uid") realm.capital_uid = keep(cursor.string());
                else if (k == "sovereign_uid") { auto v = cursor.string_or_null(); if (v) realm.sovereign_uid = keep(*v); }
                else if (k == "city_uids") realm.city_uids = strings(contract.max_records);
                else return false;
                return true;
            });
            r.require({"uid", "name", "civilization_id", "culture_id", "capital_uid", "city_uids"});
            if (realm.uid.empty()) r.fail("missing_field", "empty uid");
            budget.add(sizeof(Realm));
            world.realms.push_back(std::move(realm));
        }, contract.max_records);
    }

    // heroes.people[]: identity, role, status, realm, home and presence; everything else is skipped.
    void people() {
        cursor.array([&](std::size_t) {
            Person p;
            Record r(cursor, "heroes.people[] record");
            r.read([&](const std::string& k) {
                if (k == "uid") p.uid = keep(cursor.string());
                else if (k == "role") p.role = keep(cursor.string());
                else if (k == "status") p.status = keep(cursor.string());
                else if (k == "name") p.name = keep(cursor.string());
                else if (k == "display_name") p.display_name = keep(cursor.string());
                else if (k == "civilization_id") p.civilization_id = keep(cursor.string());
                else if (k == "race_id") p.race_id = keep(cursor.string());
                else if (k == "realm_uid") { auto v = cursor.string_or_null(); if (v) p.realm_uid = keep(*v); }
                else if (k == "home") {
                    if (cursor.advance() == Token::Null) return true;
                    cursor.primed = true;
                    cursor.object([&](const std::string& h) {
                        if (h == "kind") { auto v = cursor.string_or_null(); if (v) p.home_kind = keep(*v); }
                        else if (h == "uid") { auto v = cursor.string_or_null(); if (v) p.home_uid = keep(*v); }
                        else cursor.skip();
                    });
                }
                else if (k == "presence") {
                    if (cursor.advance() == Token::Null) return true;
                    cursor.primed = true;
                    cursor.object([&](const std::string& h) {
                        if (h == "site_kind") { auto v = cursor.string_or_null(); if (v) p.presence_kind = keep(*v); }
                        else if (h == "uid") { auto v = cursor.string_or_null(); if (v) p.presence_uid = keep(*v); }
                        else cursor.skip();
                    });
                }
                else return false;
                return true;
            });
            r.require({"uid", "role", "status", "name"});
            if (p.uid.empty()) r.fail("missing_field", "empty uid");
            budget.add(sizeof(Person));
            world.people.push_back(std::move(p));
        }, contract.max_records);
    }

    // humans.cultures[]: generated cultures and their civilization.
    void culture_entries() {
        cursor.array([&](std::size_t) {
            CultureEntry c;
            Record r(cursor, "humans.cultures[] record");
            r.read([&](const std::string& k) {
                if (k == "id") c.id = keep(cursor.string());
                else if (k == "civilization_id") c.civilization_id = keep(cursor.string());
                else if (k == "city_ids") cursor.array([&](std::size_t) { budget.add(8); c.city_ids.push_back(cursor.integer()); }, contract.max_records);
                else return false;
                return true;
            });
            r.require({"id", "civilization_id"});
            budget.add(sizeof(CultureEntry));
            world.cultures.push_back(std::move(c));
        }, contract.max_records);
    }

    // terrain.biomes[]: id, core and name of each natural biome.
    void biomes() {
        cursor.array([&](std::size_t) {
            BiomeEntry b;
            Record r(cursor, "terrain.biomes[] record");
            r.read([&](const std::string& k) {
                if (k == "id") b.id = cursor.integer();
                else if (k == "core") b.core = keep(cursor.string());
                else if (k == "name") b.name = keep(cursor.string());
                else return false;
                return true;
            });
            r.require({"id", "core"});
            budget.add(sizeof(BiomeEntry));
            world.biomes.push_back(std::move(b));
        }, 4096);
    }

    // water.river_segments[]: [node, receiver node] pairs.
    void rivers() {
        cursor.array([&](std::size_t) {
            RiverSegment segment;
            const auto count = cursor.array([&](std::size_t i) { const auto node = cursor.integer(); if (i == 0) segment.from = node; else segment.to = node; }, 2);
            if (count != 2) cursor.fail("invalid_record", "water.river_segments[] must be [node, receiver]");
            budget.add(sizeof(RiverSegment));
            world.rivers.push_back(segment);
        }, contract.max_records);
    }

    void plan_index(const PlanIndexSpec& spec) {
        cursor.array([&](std::size_t i) {
            IndexEntry e{spec.kind};
            Record r(cursor, std::string(spec.block) + ".files[] entry");
            r.read([&](const std::string& k) {
                if (k == spec.id_field) e.id = keep(cursor.string());
                else if (k == "path") e.path = keep(cursor.string());
                else return false;
                return true;
            });
            r.require({spec.id_field, "path"});
            e.at = pos_of(r.start);
            e.index = i;
            budget.add(sizeof(IndexEntry));
            index.push_back(std::move(e));
        }, contract.max_plan_files);
    }
};

const char* block_of(const std::string& kind) {
    for (const auto& spec : plan_specs) if (kind == spec.kind) return spec.block;
    return "";
}

void check_contract(SourceWorld& world, const Compatibility& contract, RootPass& pass) {
    auto& m = world.metadata;
    const Location root_at{pass.file, 0, 1, 1, "$"};
    // Location of a key, else of its nearest enclosing block, else the document start.
    auto at = [&](std::string key) {
        for (;;) {
            auto it = pass.where.find(key);
            if (it != pass.where.end()) return it->second;
            const auto dot = key.rfind('.');
            if (dot == std::string::npos) return root_at;
            key.resize(dot);
        }
    };
    auto need = [&](bool ok, const std::string& key, const std::string& code, const std::string& message) { if (!ok) throw Error(code, message, at(key)); };
    for (const char* key : {"schema", "schema_version", "completed_stage", "split", "config", "topology", "seeds", "systems", "layers"})
        need(world.members.count(key) != 0, "", "missing_block", std::string("Required root member missing: ") + key);
    need(m.schema == contract.schema, "schema", "unsupported_schema", "schema is '" + m.schema + "', expected '" + contract.schema + "'");
    need(m.schema_version == contract.schema_version, "schema_version", "unsupported_version", "schema_version " + std::to_string(m.schema_version) + " is not supported");
    need(m.completed_stage == contract.completed_stage, "completed_stage", "incomplete_world", "completed_stage " + std::to_string(m.completed_stage) + " is not " + std::to_string(contract.completed_stage));
    need(m.split_version == contract.split_version, "split.version", "unsupported_version", "split.version " + std::to_string(m.split_version) + " is not supported");
    need(!m.scene_path.empty(), "split.scene", "missing_field", "split.scene missing");
    need(m.config_size != 0, "config.size", "missing_field", "config.size missing");
    need(!m.layout_seed.empty(), "seeds.layout", "missing_field", "seeds.layout missing");
    need(pass.where.count("seeds.crust") != 0, "seeds.crust", "missing_field", "seeds.crust missing");
    need(pass.where.count("seeds.detail") != 0, "seeds.detail", "missing_field", "seeds.detail missing");
    need(world.members.count("generator_version") != 0, "", "missing_block", "Required root member missing: generator_version");
    need(m.topology_type == contract.topology_type, "topology.type", "unsupported_topology", "topology.type '" + m.topology_type + "' is not supported");
    need(m.duplicate_seam == contract.duplicate_seam, "topology.duplicate_seam", "unsupported_topology", "topology.duplicate_seam differs from the contract");
    for (const auto& [block, versions] : contract.block_versions) {
        need(world.members.count(block) != 0, "", "missing_block", "Required block missing: " + block);
        auto v = m.block_versions.find(block);
        need(v != m.block_versions.end(), block, "missing_field", block + ".version missing");
        need(std::find(versions.begin(), versions.end(), v->second) != versions.end(), block + ".version", "unsupported_version",
             block + ".version " + std::to_string(v->second) + " is not supported");
    }
    for (const char* array : required_arrays) {
        const std::string name(array);
        need(pass.arrays.count(name) != 0, name.substr(0, name.find('.')), "missing_field", "Required record array missing: " + name);
    }
    const std::set<std::string> systems(m.systems.begin(), m.systems.end());
    for (const auto& s : contract.required_systems) need(systems.count(s) != 0, "systems", "missing_system", "Required system missing: " + s);
    for (const auto& l : contract.required_layers) need(world.layers.count(l) != 0, "layers", "missing_layer", "Required layer missing: " + l);
}

void check_joins(SourceWorld& world, RootPass& pass) {
    std::unordered_map<std::int64_t, std::size_t> by_site;
    std::unordered_map<std::string, std::size_t> by_city_uid, hamlets, castles, keys;
    std::unordered_set<std::string> hamlet_uids, castle_uids;
    for (std::size_t i = 0; i < world.cities.size(); ++i) {
        const auto& c = world.cities[i];
        if (!by_site.emplace(c.site_id, i).second) throw Error("duplicate_id", "Duplicate settlements.sites id " + std::to_string(c.site_id), pass.record("$.settlements.sites", pass.city_at, i));
        if (!by_city_uid.emplace(c.uid, i).second) throw Error("duplicate_id", "Duplicate settlements.sites uid '" + c.uid + "'", pass.record("$.settlements.sites", pass.city_at, i));
    }
    for (std::size_t i = 0; i < world.hamlets.size(); ++i) {
        const auto& h = world.hamlets[i];
        if (!hamlets.emplace(h.id, i).second || !hamlet_uids.insert(h.uid).second)
            throw Error("duplicate_id", "Duplicate hamlet id or uid '" + h.id + "'/'" + h.uid + "'", pass.record("$.humans.hamlets", pass.hamlet_at, i));
    }
    for (std::size_t i = 0; i < world.castles.size(); ++i) {
        const auto& c = world.castles[i];
        if (!castles.emplace(c.id, i).second || !castle_uids.insert(c.uid).second)
            throw Error("duplicate_id", "Duplicate fortress id or uid '" + c.id + "'/'" + c.uid + "'", pass.record("$.humans.fortresses", pass.castle_at, i));
    }
    for (std::size_t i = 0; i < world.key_locations.size(); ++i)
        if (!keys.emplace(world.key_locations[i].id, i).second)
            throw Error("duplicate_id", "Duplicate key location id '" + world.key_locations[i].id + "'", pass.record("$.key_locations.sites", pass.key_at, i));

    // humans.cores: one per city; every listed hamlet/fortress exists, belongs to that city, and is listed once.
    std::unordered_set<std::int64_t> cored;
    std::vector<int> hamlet_listed(world.hamlets.size(), 0), castle_listed(world.castles.size(), 0);
    for (std::size_t ci = 0; ci < pass.cores.size(); ++ci) {
        const auto& core = pass.cores[ci];
        const Location at = core.at.at(pass.file, "$.humans.cores[" + std::to_string(ci) + "]");
        auto city = by_site.find(core.site_id);
        if (city == by_site.end()) throw Error("broken_join", "humans.cores site_id " + std::to_string(core.site_id) + " has no settlements.sites record", at);
        if (!cored.insert(core.site_id).second) throw Error("duplicate_id", "Two humans.cores records share site_id " + std::to_string(core.site_id), at);
        City& c = world.cities[city->second];
        c.culture_id = core.culture_id; c.hamlet_ids = core.hamlet_ids; c.fortress_ids = core.fortress_ids;
        for (const auto& id : core.hamlet_ids) {
            auto h = hamlets.find(id);
            if (h == hamlets.end()) throw Error("broken_join", "humans.cores hamlet id '" + id + "' has no hamlet record", at);
            if (world.hamlets[h->second].core_id != core.site_id) throw Error("broken_join", "hamlet '" + id + "' core_id does not match its core", at);
            if (++hamlet_listed[h->second] > 1) throw Error("duplicate_id", "hamlet '" + id + "' is listed more than once", at);
        }
        for (const auto& id : core.fortress_ids) {
            auto f = castles.find(id);
            if (f == castles.end()) throw Error("broken_join", "humans.cores fortress id '" + id + "' has no fortress record", at);
            const Castle& fort = world.castles[f->second];
            if (!fort.garrisoned || fort.core_id != core.site_id || fort.core_uid != c.uid) throw Error("broken_join", "fortress '" + id + "' core does not match", at);
            if (++castle_listed[f->second] > 1) throw Error("duplicate_id", "fortress '" + id + "' is listed more than once", at);
        }
    }
    // Reverse joins: every hamlet and every garrisoned fortress is listed by its own core.
    for (std::size_t i = 0; i < world.hamlets.size(); ++i)
        if (hamlet_listed[i] != 1) throw Error("broken_join", "hamlet '" + world.hamlets[i].id + "' is not listed by humans.cores site " + std::to_string(world.hamlets[i].core_id), pass.record("$.humans.hamlets", pass.hamlet_at, i));
    for (std::size_t i = 0; i < world.castles.size(); ++i)
        if (world.castles[i].garrisoned != (castle_listed[i] == 1)) throw Error("broken_join", "fortress '" + world.castles[i].id + "' garrison does not match humans.cores", pass.record("$.humans.fortresses", pass.castle_at, i));

    // Plan indexes: join each entry to its record; paths only from the index; one plan per record.
    std::set<std::pair<std::string, std::string>> seen_ids;
    for (const auto& e : pass.index) {
        const Location at = e.at.at(pass.file, std::string("$.") + block_of(e.kind) + ".files[" + std::to_string(e.index) + "]");
        if (!seen_ids.insert({e.kind, e.id}).second) throw Error("duplicate_id", "Duplicate " + e.kind + " plan entry for '" + e.id + "'", at);
        std::string* slot = nullptr;
        if (e.kind == "city") { auto it = by_city_uid.find(e.id); if (it != by_city_uid.end()) slot = &world.cities[it->second].plan_path; }
        else if (e.kind == "hamlet") { auto it = hamlets.find(e.id); if (it != hamlets.end()) slot = &world.hamlets[it->second].plan_path; }
        else if (e.kind == "castle") { auto it = castles.find(e.id); if (it != castles.end()) slot = &world.castles[it->second].plan_path; }
        else { auto it = keys.find(e.id); if (it != keys.end()) slot = &world.key_locations[it->second].plan_path; }
        if (!slot) throw Error("broken_join", e.kind + " plan entry '" + e.id + "' has no matching record", at);
        *slot = e.path;
    }
    auto covered = [&](const std::string& path, const char* where, const std::vector<Pos>& positions, std::size_t i, const std::string& id) {
        if (path.empty()) throw Error("missing_plan", "Record '" + id + "' has no plan file entry", pass.record(where, positions, i));
    };
    for (std::size_t i = 0; i < world.cities.size(); ++i) covered(world.cities[i].plan_path, "$.settlements.sites", pass.city_at, i, world.cities[i].uid);
    for (std::size_t i = 0; i < world.hamlets.size(); ++i) covered(world.hamlets[i].plan_path, "$.humans.hamlets", pass.hamlet_at, i, world.hamlets[i].id);
    for (std::size_t i = 0; i < world.castles.size(); ++i) covered(world.castles[i].plan_path, "$.humans.fortresses", pass.castle_at, i, world.castles[i].id);
    for (std::size_t i = 0; i < world.key_locations.size(); ++i) covered(world.key_locations[i].plan_path, "$.key_locations.sites", pass.key_at, i, world.key_locations[i].id);
}

struct Lookups {
    std::unordered_map<std::string, const City*> city_by_uid;
    std::unordered_map<std::int64_t, const City*> city_by_site;
    std::unordered_map<std::string, const Hamlet*> hamlets;
    std::unordered_map<std::string, const Castle*> castles;
    std::unordered_map<std::string, const KeyLocation*> keys;
};

// Opens one indexed plan file and checks its identity, version and cross-references.
PlanFile check_plan(const SourceWorld& world, const IndexEntry& e, const Location& entry_at, const Compatibility& contract, const Lookups& look) {
    PlanFile plan{e.kind, e.id, e.path};
    plan.resolved = resolve_inside(world.root, e.path, entry_at, &plan.bytes);
    if (plan.bytes > contract.max_plan_bytes) throw Error("limit_exceeded", "Plan file exceeds the byte bound: '" + e.path + "'", entry_at);
    json::Limits limits; limits.max_bytes = contract.max_plan_bytes;
    Stream s(plan.resolved, limits);
    Cursor c(s);
    std::map<std::string, std::string> ids;
    std::map<std::string, std::int64_t> numbers;
    std::map<std::string, Location> at;
    std::set<std::string> nulls;
    static const std::set<std::string> id_fields{"city_uid", "hamlet_id", "hamlet_uid", "core_city_uid", "fortress_id", "fortress_uid", "location_id", "kind", "status"};
    static const std::set<std::string> number_fields{"version", "site_id", "core_id"};
    c.object([&](const std::string& k) {
        if (id_fields.count(k)) { const auto v = c.string_or_null(); at[k] = s.where(); if (v) ids[k] = *v; else nulls.insert(k); }
        else if (number_fields.count(k)) { const auto v = c.integer_or_null(); at[k] = s.where(); if (v) numbers[k] = *v; else nulls.insert(k); }
        else c.skip();
    });
    s.next();  // trailing data check
    const Location file_at{json::utf8(plan.resolved), 0, 1, 1, "$"};
    auto loc = [&](const std::string& k) { auto it = at.find(k); return it == at.end() ? file_at : it->second; };
    auto need = [&](bool ok, const std::string& k, const std::string& code, const std::string& message) { if (!ok) throw Error(code, message, loc(k)); };
    auto text = [&](const std::string& k) -> const std::string* { auto it = ids.find(k); return it == ids.end() ? nullptr : &it->second; };
    auto equals = [&](const std::string& k, const std::string& want) { const auto* v = text(k); return v && *v == want; };
    need(numbers.count("version") != 0, "version", "missing_field", "Plan file lacks version");
    plan.version = static_cast<int>(std::clamp<std::int64_t>(numbers["version"], -1, 1000000));
    need(numbers["version"] == contract.plan_versions.at(e.kind), "version", "unsupported_version", e.kind + " plan version " + std::to_string(numbers["version"]) + " is not supported");
    if (const auto* status = text("status")) plan.status = *status;
    need(lower(plan.status) != "failed", "status", "failed_block", e.kind + " plan reports status '" + plan.status + "'");
    if (e.kind == "city") {
        const City& city = *look.city_by_uid.at(e.id);
        need(equals("city_uid", e.id), "city_uid", "broken_join", "city plan city_uid differs from its index entry");
        need(numbers.count("site_id") && numbers["site_id"] == city.site_id, "site_id", "broken_join", "city plan site_id differs from settlements.sites");
    } else if (e.kind == "hamlet") {
        const Hamlet& h = *look.hamlets.at(e.id);
        need(equals("hamlet_id", e.id), "hamlet_id", "broken_join", "hamlet plan hamlet_id differs from its index entry");
        need(equals("hamlet_uid", h.uid), "hamlet_uid", "broken_join", "hamlet plan hamlet_uid differs from humans.hamlets");
        need(numbers.count("core_id") && numbers["core_id"] == h.core_id, "core_id", "broken_join", "hamlet plan core_id differs from humans.hamlets");
        auto core = look.city_by_site.find(h.core_id);
        need(core != look.city_by_site.end() && equals("core_city_uid", core->second->uid), "core_city_uid", "broken_join", "hamlet plan core_city_uid differs from its core city");
        need(equals("kind", "hamlet"), "kind", "broken_join", "hamlet plan kind is not 'hamlet'");
    } else if (e.kind == "castle") {
        const Castle& f = *look.castles.at(e.id);
        need(equals("fortress_id", e.id), "fortress_id", "broken_join", "castle plan fortress_id differs from its index entry");
        need(equals("fortress_uid", f.uid), "fortress_uid", "broken_join", "castle plan fortress_uid differs from humans.fortresses");
        need(equals("kind", "castle"), "kind", "broken_join", "castle plan kind is not 'castle'");
        // The castle plan writes null where the root row carries the no-garrison sentinel (-1).
        const bool same_core = f.garrisoned ? (numbers.count("core_id") && numbers["core_id"] == f.core_id) : nulls.count("core_id") != 0;
        need(same_core, "core_id", "broken_join", "castle plan core_id differs from humans.fortresses");
    } else {
        const KeyLocation& k = *look.keys.at(e.id);
        need(equals("location_id", e.id), "location_id", "broken_join", "key location plan location_id differs from its index entry");
        need(equals("kind", k.kind), "kind", "broken_join", "key location plan kind differs from key_locations.sites");
    }
    return plan;
}

std::vector<int> int_list(Cursor& c) {
    std::vector<int> out;
    c.array([&](std::size_t) { out.push_back(c.version()); }, 64);
    return out;
}

}  // namespace

Compatibility load_compatibility(const fs::path& file) {
    Compatibility k;
    Stream s(file);
    Cursor c(s);
    std::string schema;
    auto strings = [&](std::vector<std::string>& out, std::size_t max) { c.array([&](std::size_t) { out.push_back(c.string()); }, max); };
    c.object([&](const std::string& key) {
        if (key == "schema") schema = c.string();
        else if (key == "root") c.object([&](const std::string& f) {
            if (f == "schema") k.schema = c.string();
            else if (f == "schema_version") k.schema_version = c.version();
            else if (f == "completed_stage") k.completed_stage = c.version();
            else if (f == "split_version") k.split_version = c.version();
            else if (f == "scene_version") k.scene_version = c.version();
            else if (f == "topology_type") k.topology_type = c.string();
            else if (f == "duplicate_seam") k.duplicate_seam = c.boolean();
            else c.skip();
        });
        else if (key == "blocks") c.object([&](const std::string& block) { k.block_versions[block] = int_list(c); });
        else if (key == "plan_file_versions") c.object([&](const std::string& kind) { k.plan_versions[kind] = c.version(); });
        else if (key == "required_systems") strings(k.required_systems, 256);
        else if (key == "required_layers") strings(k.required_layers, 1024);
        else if (key == "integral_layers") strings(k.integral_layers, 1024);
        else if (key == "tolerances") c.object([&](const std::string& f) {
            if (f == "seam") k.seam_tolerance = c.number(); else if (f == "pole") k.pole_tolerance = c.number(); else c.skip();
        });
        else if (key == "limits") c.object([&](const std::string& f) {
            if (f == "max_side") k.max_side = static_cast<std::size_t>(c.bounded(3, 65537, "max_side"));
            else if (f == "max_records") k.max_records = static_cast<std::size_t>(c.bounded(1, 100000000, "max_records"));
            else if (f == "max_plan_files") k.max_plan_files = static_cast<std::size_t>(c.bounded(1, 100000000, "max_plan_files"));
            else if (f == "max_plan_bytes") k.max_plan_bytes = static_cast<std::uint64_t>(c.integer());
            else if (f == "max_retained_bytes") k.max_retained_bytes = static_cast<std::uint64_t>(c.integer());
            else c.skip();
        });
        else c.skip();
    });
    s.next();
    const Location doc{json::utf8(file), 0, 1, 1, "$"};
    if (schema != "mnbl.source-compatibility") throw Error("unsupported_schema", "Not a source compatibility contract", doc);
    const std::set<std::string> kinds{"city", "hamlet", "castle", "key_location"};
    std::set<std::string> declared;
    for (const auto& [kind, version] : k.plan_versions) declared.insert(kind);
    if (declared != kinds) throw Error("missing_field", "Compatibility contract must declare plan_file_versions for city, hamlet, castle and key_location", doc);
    for (const auto& l : k.integral_layers)
        if (std::find(k.required_layers.begin(), k.required_layers.end(), l) == k.required_layers.end()) throw Error("missing_field", "Integral layer is not required: " + l, doc);
    if (k.schema.empty() || !k.max_side || !k.max_records || !k.max_plan_files || !k.max_plan_bytes || !k.max_retained_bytes)
        throw Error("missing_field", "Compatibility contract is incomplete", doc);
    return k;
}

SourceWorld read_source(const fs::path& split_root, const Compatibility& contract, const ReadOptions& options) {
    const auto started = std::chrono::steady_clock::now();
    SourceWorld world;
    std::error_code ec;
    world.root = fs::canonical(split_root, ec);
    if (ec) throw Error("missing_file", "Split root does not exist", Location{json::utf8(split_root), 0, 1, 1, "$"});
    RootPass pass(world, contract);
    pass.run();
    check_contract(world, contract, pass);
    check_joins(world, pass);

    // Scene: path from split.scene only, inside the root; must carry the scene's own members and version.
    std::uint64_t scene_bytes = 0;
    const auto scene = resolve_inside(world.root, world.metadata.scene_path, pass.where.at("split.scene"), &scene_bytes);
    {
        Stream s(scene);
        Cursor c(s);
        std::set<std::string> members;
        Location version_at{json::utf8(scene), 0, 1, 1, "$"};
        c.object([&](const std::string& k) {
            members.insert(k);
            if (k == "version") { world.metadata.scene_version = c.version(); version_at = s.where(); }
            else c.skip();
        });
        s.next();
        for (const char* k : {"version", "unit", "buildings", "streets", "junctions", "regional_roads"})
            if (!members.count(k)) throw Error("missing_field", std::string("Scene lacks required member '") + k + "'", Location{json::utf8(scene), 0, 1, 1, "$"});
        if (world.metadata.scene_version != contract.scene_version)
            throw Error("unsupported_version", "Scene version " + std::to_string(world.metadata.scene_version) + " is not supported", version_at);
        world.statistics.scene_bytes = scene_bytes;
    }

    for (const auto& spec : plan_specs) {
        const auto block = world.metadata.block_versions.find(spec.block);
        if (block != world.metadata.block_versions.end() && block->second != contract.plan_versions.at(spec.kind))
            throw Error("unsupported_version", std::string(spec.block) + ".version does not match its plan file version", pass.where.at(std::string(spec.block) + ".version"));
    }
    if (options.validate_plans) {
        Lookups look;
        for (const auto& c : world.cities) { look.city_by_uid[c.uid] = &c; look.city_by_site[c.site_id] = &c; }
        for (const auto& h : world.hamlets) look.hamlets[h.id] = &h;
        for (const auto& f : world.castles) look.castles[f.id] = &f;
        for (const auto& k : world.key_locations) look.keys[k.id] = &k;
        std::set<fs::path> resolved;
        for (const auto& e : pass.index) {
            const Location at = e.at.at(pass.file, std::string("$.") + block_of(e.kind) + ".files[" + std::to_string(e.index) + "]");
            world.plans.push_back(check_plan(world, e, at, contract, look));
            if (!resolved.insert(world.plans.back().resolved).second) throw Error("duplicate_id", "Two plan entries resolve to the same file '" + e.path + "'", at);
            world.statistics.plan_bytes += world.plans.back().bytes;
        }
        world.statistics.plan_files = world.plans.size();
    }
    world.statistics.seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count();
    return world;
}

Layer SourceWorld::read_layer(const std::string& name) const {
    const auto info = layers.find(name);
    if (info == layers.end()) throw Error("missing_layer", "Layer not present: " + name, Location{json::utf8((root / "world.json")), 0, 1, 1, "$.layers"});
    Layer layer{name, metadata.grid_side};
    layer.values.reserve(layer.side * layer.side);
    Stream s(root / "world.json");
    s.seek(info->second.start, {".layers", "." + name});
    Cursor c(s);
    c.array([&](std::size_t) {
        const std::size_t columns = c.array([&](std::size_t) { layer.values.push_back(c.number()); }, layer.side + 1);
        if (columns != layer.side) c.fail("bad_dimensions", "Layer changed since validation");
    }, layer.side + 1);
    if (layer.values.size() != layer.side * layer.side) throw Error("bad_dimensions", "Layer changed since validation", info->second.start);
    return layer;
}

namespace {
std::string quote(const std::string& text) {
    std::string out = "\"";
    for (unsigned char ch : text) {
        if (ch == '"' || ch == '\\') { out += '\\'; out += static_cast<char>(ch); }
        else if (ch < 0x20) { static const char* hex = "0123456789abcdef"; out += "\\u00"; out += hex[ch >> 4]; out += hex[ch & 15]; }
        else out += static_cast<char>(ch);
    }
    return out + "\"";
}
}  // namespace

std::string write_identity(const Metadata& m) {
    std::ostringstream out;
    out << "{\"schema\":\"mnbl.source-identity\",\"version\":1,\"sourceSchema\":" << quote(m.schema) << ",\"schemaVersion\":" << m.schema_version
        << ",\"completedStage\":" << m.completed_stage << ",\"generatorVersion\":" << quote(m.generator_version) << ",\"gridSide\":" << m.grid_side
        << ",\"layoutSeed\":\"" << m.layout_seed << "\",\"crustSeed\":" << m.crust_seed << ",\"detailSeed\":" << m.detail_seed << "}";
    return out.str();
}

Metadata read_identity(const fs::path& file) {
    Metadata m;
    Stream s(file);
    Cursor c(s);
    std::string schema; int version = 0;
    std::set<std::string> seen;
    c.object([&](const std::string& k) {
        seen.insert(k);
        if (k == "schema") schema = c.string();
        else if (k == "version") version = c.version();
        else if (k == "sourceSchema") m.schema = c.string();
        else if (k == "schemaVersion") m.schema_version = c.version();
        else if (k == "completedStage") m.completed_stage = c.version();
        else if (k == "generatorVersion") m.generator_version = c.string();
        else if (k == "gridSide") m.grid_side = static_cast<std::size_t>(c.bounded(0, 65537, "gridSide"));
        else if (k == "layoutSeed") {
            c.advance();
            if (s.token() != Token::String) c.fail("wrong_type", "layoutSeed must be a decimal string");
            if (!canonical_u64(s.text())) c.fail("out_of_range", "layoutSeed is not a canonical unsigned 64-bit decimal");
            m.layout_seed = s.text();
        } else if (k == "crustSeed") m.crust_seed = c.integer();
        else if (k == "detailSeed") m.detail_seed = c.integer();
        else c.fail("unexpected_member", "Unknown identity member");
    });
    s.next();
    const Location doc{json::utf8(file), 0, 1, 1, "$"};
    if (schema != "mnbl.source-identity" || version != 1) throw Error("unsupported_schema", "Not an mnbl.source-identity v1 document", doc);
    if (seen.count("sourceSchema") && m.schema != "fwg.world") throw Error("unsupported_schema", "Identity sourceSchema is not 'fwg.world'", doc);
    for (const char* k : {"sourceSchema", "schemaVersion", "completedStage", "generatorVersion", "gridSide", "layoutSeed", "crustSeed", "detailSeed"})
        if (!seen.count(k)) throw Error("missing_field", std::string("Identity lacks '") + k + "'", doc);
    return m;
}

}  // namespace worldbridge::source
