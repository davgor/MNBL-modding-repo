#include "worldbridge/travel_graph.hpp"
#include <algorithm>
#include <cmath>
#include <cstdlib>
#include <deque>
#include <fstream>
#include <iostream>
#include <iterator>
#include <limits>
#include <numeric>
#include <queue>
#include <stdexcept>
#include "worldbridge/canonical_json.hpp"
#include "worldbridge/policy_map2.hpp"
#include "worldbridge/population.hpp"
#include "worldbridge/projected_world.hpp"

namespace worldbridge::travel {
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
double real(const Value& object, const char* key, const std::string& what) { return std::strtod(need(object, key, Value::Kind::Number, what).text.c_str(), nullptr); }
long long whole(const Value& object, const char* key, const std::string& what) {
    const Value& v = need(object, key, Value::Kind::Number, what);
    const double d = std::strtod(v.text.c_str(), nullptr);
    if (d != std::floor(d) || std::abs(d) > 1e9) throw Error("wrong_type", what + " '" + key + "' must be an integer", v.at);
    return static_cast<long long>(d);
}
bool flag(const Value& object, const char* key, const std::string& what) { return need(object, key, Value::Kind::Bool, what).boolean; }
const std::string& text(const Value& object, const char* key, const std::string& what) { return need(object, key, Value::Kind::String, what).text; }
void header(const Value& doc, const char* schema, const std::string& what) {
    if (text(doc, "schema", what) != schema || need(doc, "version", Value::Kind::Number, what).text != "1")
        throw Error("unsupported_version", what + " must be " + schema + " version 1", doc.at);
}
void hash_shape(const std::string& h, const Value& at, const std::string& what) {
    if (h.size() != 64 || h.find_first_not_of("0123456789abcdef") != std::string::npos) throw Error("invalid_hash", what + " is not a lowercase SHA-256", at.at);
}

Value str(const std::string& s) { return Value::string(s); }
Value num(double v) { Value out; out.kind = Value::Kind::Number; out.text = cjson::ecmascript_number(v); return out; }
Value integer(long long v) { Value out; out.kind = Value::Kind::Number; out.text = std::to_string(v); return out; }
Value boolean(bool b) { Value out; out.kind = Value::Kind::Bool; out.boolean = b; return out; }
Value array() { Value out; out.kind = Value::Kind::Array; return out; }
Value strings(const std::vector<std::string>& items) { Value out = array(); for (const auto& s : items) out.items.push_back(str(s)); return out; }
Value ints(const std::vector<int>& items) { Value out = array(); for (int i : items) out.items.push_back(integer(i)); return out; }
Value cell_value(Cell c) { Value out = array(); out.items.push_back(integer(c.row)); out.items.push_back(integer(c.col)); return out; }
Value point_value(Point p) { Value out = Value::object(); out.set("x", num(p.x)); out.set("y", num(p.y)); return out; }
Value cells_value(const std::vector<Cell>& cells) { Value out = array(); for (const auto& c : cells) out.items.push_back(cell_value(c)); return out; }
Value counts_value(const std::map<std::string, std::size_t>& m) { Value out = Value::object(); for (const auto& [k, v] : m) out.set(k, integer(static_cast<long long>(v))); return out; }

bool water(std::uint8_t cls) { return cls == Sea || cls == Lake || cls == River; }
// Preference when a point touches several cells: land, then a mountain (a pass), then river, lake, sea.
int rank(std::uint8_t cls) { static const int r[] = {0, 1, 4, 3, 2}; return r[cls]; }
// Lattice path costs: stay on land, then cross a mountain (a pass), then river or lake, then sea; +1 per cell.
int penalty(std::uint8_t cls) { static const int p[] = {0, 10, 1000, 100, 100}; return p[cls]; }
// The middle of the edge a land cell shares with its water neighbour (the bank a road stops at), on the land cell's seam image.
Point bank_point(const Cells& cells, Cell land, Cell water) {
    int dc = water.col - land.col;
    if (dc > 1) dc = -1; else if (dc < -1) dc = 1;
    const Point c = cells.centre(land);
    return {c.x + 0.5 * dc * cells.spacing(), c.y + 0.5 * (water.row - land.row) * cells.spacing()};
}

// The land face types of the given cells (MMR-254), distinct, as "Forest" or "one of Forest, Plain": a host check's expected face. Cells
// without a land type (a carved or passed cell before its type is known never occurs; water) are left out; none -> the default face.
std::string land_names(const Policy& policy, const Result& r, const Cells& cells, const std::vector<Cell>& of) {
    std::set<std::string> names;
    for (const auto& c : of) {
        const std::size_t i = cells.index(c);
        if (i >= r.land_type.size() || r.land_type[i] == 0) continue;
        for (const auto& [name, id] : policy.calibration.face_groups) if (id == r.land_type[i]) names.insert(name);
    }
    if (names.empty()) names.insert(policy.land_face_default);
    std::string out;
    for (const auto& n : names) out += (out.empty() ? "" : ", ") + n;
    return names.size() == 1 ? out : "one of " + out;
}

class UnionFind {
public:
    explicit UnionFind(std::size_t n) : parent(n) { std::iota(parent.begin(), parent.end(), std::size_t{0}); }
    std::size_t find(std::size_t a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; }
    bool unite(std::size_t a, std::size_t b) { a = find(a); b = find(b); if (a == b) return false; if (b < a) std::swap(a, b); parent[b] = a; return true; }
private:
    std::vector<std::size_t> parent;
};

void write(const fs::path& file, const std::string& bytes) {
    std::ofstream out(file, std::ios::binary | std::ios::trunc);
    out.write(bytes.data(), static_cast<std::streamsize>(bytes.size()));
    out.close();
    if (!out) throw std::runtime_error("Cannot write " + file.string());
}
}  // namespace

const char* class_name(std::uint8_t cls) {
    switch (cls) {
    case Land: return "land";
    case Mountain: return "mountain";
    case Sea: return "sea";
    case Lake: return "lake";
    case River: return "river";
    default: return "unknown";
    }
}

Calibration load_calibration(const fs::path& file) {
    const Value doc = cjson::parse(file);
    const std::string what = "Naval transitions calibration";
    only(doc, {"schema", "version", "source", "invalidTerrain", "faceGroups", "regionSwitchCost", "embarkDisembarkThreshold", "observed", "open", "installedPortGateEnvelope"}, what);
    header(doc, "mnbl.naval-transitions", what);
    Calibration c;
    const Value& source = need(doc, "source", Value::Kind::Object, what);
    c.evidence_sha256 = text(source, "evidenceSha256", what + " source");
    hash_shape(c.evidence_sha256, *source.find("evidenceSha256"), what + " source.evidenceSha256");
    const Value& invalid = need(doc, "invalidTerrain", Value::Kind::Object, what);
    only(invalid, {"Default", "Naval", "All"}, what + " invalidTerrain");
    for (const char* nav : {"Default", "Naval", "All"}) {
        auto& set = c.invalid[nav];
        for (const auto& v : need(invalid, nav, Value::Kind::Array, what + " invalidTerrain").items) {
            if (v.kind != Value::Kind::Number) throw Error("wrong_type", what + " invalidTerrain ids must be integers", v.at);
            const int id = std::atoi(v.text.c_str());
            if (std::to_string(id) != v.text || id < 0 || id > 255) throw Error("out_of_range", what + " invalidTerrain id out of range", v.at);
            set.insert(id);
        }
    }
    const Value& groups = need(doc, "faceGroups", Value::Kind::Object, what);
    for (const auto& [name, v] : groups.members) {
        if (v.kind != Value::Kind::Number) throw Error("wrong_type", what + " faceGroups values must be integers", v.at);
        const int id = std::atoi(v.text.c_str());
        if (std::to_string(id) != v.text || id < 0 || id > 255) throw Error("out_of_range", what + " faceGroups id out of range", v.at);
        c.face_groups[name] = id;
    }
    const Value& costs = need(doc, "regionSwitchCost", Value::Kind::Object, what);
    only(costs, {"landToSea", "seaToLand"}, what + " regionSwitchCost");
    c.land_to_sea = static_cast<int>(whole(costs, "landToSea", what));
    c.sea_to_land = static_cast<int>(whole(costs, "seaToLand", what));
    c.embark_threshold = real(doc, "embarkDisembarkThreshold", what);
    need(doc, "observed", Value::Kind::Object, what);
    c.port_gate_envelope_max = real(need(doc, "installedPortGateEnvelope", Value::Kind::Object, what), "max", what + " installedPortGateEnvelope");
    need(doc, "open", Value::Kind::Array, what);
    c.sha256 = cjson::sha256_file_hex(file);
    return c;
}

Policy load_policy(const fs::path& file) {
    const Value doc = cjson::parse(file);
    const std::string what = "Playability policy";
    only(doc, {"schema", "version", "calibration", "grid", "cells", "repairs", "bridges", "ports", "basins", "reachability", "villages", "roads", "notes"}, what);
    header(doc, "mnbl.playability", what);
    if (const Value* n = doc.find("notes"); n && n->kind != Value::Kind::String) throw Error("wrong_type", what + " notes must be a string", n->at);
    Policy p;
    const Value& cal = need(doc, "calibration", Value::Kind::Object, what);
    only(cal, {"file", "sha256"}, what + " calibration");
    const std::string& cal_file = text(cal, "file", what + " calibration");
    const std::string& cal_sha = text(cal, "sha256", what + " calibration");
    hash_shape(cal_sha, *cal.find("sha256"), what + " calibration.sha256");
    const fs::path receipt = file.parent_path() / fs::path(std::u8string(cal_file.begin(), cal_file.end()));
    if (!fs::is_regular_file(receipt)) throw Error("missing_file", "Calibration not found: " + json::utf8(receipt), cal.find("file")->at);
    if (cjson::sha256_file_hex(receipt) != cal_sha) throw Error("calibration_mismatch", "Calibration " + json::utf8(receipt) + " does not match the pinned SHA-256", cal.find("sha256")->at);
    p.calibration = load_calibration(receipt);

    const Value& grid = need(doc, "grid", Value::Kind::Object, what);
    only(grid, {"side"}, what + " grid");
    p.side = static_cast<int>(whole(grid, "side", what + " grid"));
    const Value& cells = need(doc, "cells", Value::Kind::Object, what);
    only(cells, {"mountainSlope", "openSeaCells", "landFaceGroups", "faceGroups"}, what + " cells");
    p.mountain_slope = real(cells, "mountainSlope", what + " cells");
    p.open_sea_cells = static_cast<int>(whole(cells, "openSeaCells", what + " cells"));
    const Value& faces = need(cells, "faceGroups", Value::Kind::Object, what + " cells");
    only(faces, {"land", "mountain", "coastalSea", "openSea", "lake", "river", "bridge", "underBridge"}, what + " cells.faceGroups");
    for (const char* k : {"land", "mountain", "coastalSea", "openSea", "lake", "river", "bridge", "underBridge"}) p.face_groups[k] = text(faces, k, what + " cells.faceGroups");
    // Map epic 2 (MMR-248): the biome face table (MMR-254 classifies with it).
    const Value& land_faces = need(cells, "landFaceGroups", Value::Kind::Object, what + " cells");
    only(land_faces, {"default", "biomes", "permanentWhite"}, what + " cells.landFaceGroups");
    p.land_face_default = text(land_faces, "default", what + " cells.landFaceGroups");
    for (const auto& [core, v] : need(land_faces, "biomes", Value::Kind::Object, what + " cells.landFaceGroups").members) {
        if (v.kind != Value::Kind::String) throw Error("wrong_type", what + " cells.landFaceGroups biomes values must be TerrainType names", v.at);
        p.land_face_groups[core] = v.text;
    }
    if (p.land_face_groups.empty()) throw Error("missing_field", what + " cells.landFaceGroups names no biome", land_faces.at);
    const Value& white = need(land_faces, "permanentWhite", Value::Kind::Object, what + " cells.landFaceGroups");
    only(white, {"snowMonths", "snowThreshold", "faceGroup"}, what + " cells.landFaceGroups.permanentWhite");
    p.permanent_white_months = static_cast<int>(whole(white, "snowMonths", what + " cells.landFaceGroups.permanentWhite"));
    p.permanent_white_threshold = real(white, "snowThreshold", what + " cells.landFaceGroups.permanentWhite");
    p.permanent_white_face = text(white, "faceGroup", what + " cells.landFaceGroups.permanentWhite");
    if (p.permanent_white_months < 1 || p.permanent_white_months > 12 || !(p.permanent_white_threshold > 0 && p.permanent_white_threshold <= 1))
        throw Error("out_of_range", what + " cells.landFaceGroups.permanentWhite needs 1..12 months and a threshold in (0, 1]", white.at);

    const Value& repairs = need(doc, "repairs", Value::Kind::Object, what);
    only(repairs, {"roadPasses", "maxPassFraction", "settlementFootprint", "maxFootprintFraction", "maxAccessCells", "maxSnapCells", "maxSnapFraction", "maxHarbourShiftCells", "maxHarbourShiftFraction",
                   "maxTownSnapCells", "townCarve", "forcedPorts"}, what + " repairs");
    p.road_passes = flag(repairs, "roadPasses", what + " repairs");
    p.max_pass_fraction = real(repairs, "maxPassFraction", what + " repairs");
    p.settlement_footprint = flag(repairs, "settlementFootprint", what + " repairs");
    p.max_footprint_fraction = real(repairs, "maxFootprintFraction", what + " repairs");
    p.max_access_cells = static_cast<int>(whole(repairs, "maxAccessCells", what + " repairs"));
    p.max_snap_cells = static_cast<int>(whole(repairs, "maxSnapCells", what + " repairs"));
    p.max_snap_fraction = real(repairs, "maxSnapFraction", what + " repairs");
    p.max_harbour_shift_cells = static_cast<int>(whole(repairs, "maxHarbourShiftCells", what + " repairs"));
    p.max_harbour_shift_fraction = real(repairs, "maxHarbourShiftFraction", what + " repairs");
    p.max_town_snap_cells = static_cast<int>(whole(repairs, "maxTownSnapCells", what + " repairs"));
    p.town_carve = flag(repairs, "townCarve", what + " repairs");
    p.forced_ports = flag(repairs, "forcedPorts", what + " repairs");

    const Value& bridges = need(doc, "bridges", Value::Kind::Object, what);
    only(bridges, {"kinds", "maxCells", "maxRequired"}, what + " bridges");
    for (const auto& k : need(bridges, "kinds", Value::Kind::Array, what + " bridges").items) {
        if (k.kind != Value::Kind::String || (k.text != "river" && k.text != "lake" && k.text != "coast")) throw Error("unmapped_category", what + " bridge kinds are river, lake or coast", k.at);
        if (!p.bridge_kinds.insert(k.text).second) throw Error("duplicate_id", what + " repeats a bridge kind", k.at);
    }
    p.max_bridges = static_cast<int>(whole(bridges, "maxRequired", what + " bridges"));
    p.max_bridge_cells = static_cast<int>(whole(bridges, "maxCells", what + " bridges"));
    const Value& ports = need(doc, "ports", Value::Kind::Object, what);
    only(ports, {"maxPortGateCells"}, what + " ports");
    p.max_port_gate_cells = real(ports, "maxPortGateCells", what + " ports");
    const Value& basins = need(doc, "basins", Value::Kind::Object, what);
    only(basins, {"minOpenCells"}, what + " basins");
    p.min_open_cells = static_cast<int>(whole(basins, "minOpenCells", what + " basins"));
    const Value& reach = need(doc, "reachability", Value::Kind::Object, what);
    only(reach, {"maxExcludedVillageFraction", "maxExcludedCastleFraction"}, what + " reachability");
    p.max_excluded_village_fraction = real(reach, "maxExcludedVillageFraction", what + " reachability");
    p.max_excluded_castle_fraction = real(reach, "maxExcludedCastleFraction", what + " reachability");
    auto names = [&](const Value& o, const char* key, const std::string& w, const std::set<std::string>& allowed) {
        std::vector<std::string> out;
        for (const auto& v : need(o, key, Value::Kind::Array, w).items) {
            if (v.kind != Value::Kind::String || !allowed.count(v.text)) throw Error("unmapped_category", w + " '" + key + "' has an unknown entry", v.at);
            if (std::find(out.begin(), out.end(), v.text) != out.end()) throw Error("duplicate_id", w + " '" + key + "' repeats '" + v.text + "'", v.at);
            out.push_back(v.text);
        }
        return out;
    };
    // Map epic 2 (MMR-248): the village quota (MMR-252).
    const Value& villages = need(doc, "villages", Value::Kind::Object, what);
    const std::string vw = what + " villages";
    only(villages, {"townVillages", "castleVillages", "clearance", "minSeparation", "tiers", "isletExceptions", "preference", "maxExtraHarbours", "reassign",
                    "protectedClasses", "synthesizedClass", "uidPrefix"}, vw);
    auto& q = p.villages;
    q.town = static_cast<int>(whole(villages, "townVillages", vw)); q.castle = static_cast<int>(whole(villages, "castleVillages", vw));
    q.clearance = real(villages, "clearance", vw); q.min_separation = real(villages, "minSeparation", vw);
    for (const auto& tier : need(villages, "tiers", Value::Kind::Array, vw).items) {
        only(tier, {"name", "minSpacing", "maxSpacing"}, vw + ".tiers[]");
        Policy::VillageTier vt{text(tier, "name", vw + ".tiers[]"), real(tier, "minSpacing", vw + ".tiers[]"), real(tier, "maxSpacing", vw + ".tiers[]")};
        if (!(vt.min_spacing >= 0 && vt.max_spacing > vt.min_spacing && vt.max_spacing <= 1)) throw Error("out_of_range", vw + " tiers need 0 <= minSpacing < maxSpacing <= 1", tier.at);
        if (!q.tiers.empty() && vt.max_spacing < q.tiers.back().max_spacing) throw Error("out_of_range", vw + " tiers widen in order", tier.at);
        q.tiers.push_back(vt);
    }
    q.islet_exceptions = flag(villages, "isletExceptions", vw);
    q.preference = names(villages, "preference", vw, {"pinned", "harbour", "diverse", "nearest"});
    q.max_extra_harbours = static_cast<int>(whole(villages, "maxExtraHarbours", vw));
    q.reassign = text(villages, "reassign", vw);
    q.protected_classes = names(villages, "protectedClasses", vw, {"farming", "fishing", "resource", "harbor"});
    q.synthesized_class = text(villages, "synthesizedClass", vw);
    q.uid_prefix = text(villages, "uidPrefix", vw);
    if (q.town < 1 || q.castle < 1 || !(q.clearance >= 0) || !(q.min_separation > 0) || q.tiers.empty() || q.max_extra_harbours < 0 || q.reassign != "ownKingdomFirst" ||
        q.synthesized_class != "largestDeficit" || q.uid_prefix.empty() || q.preference.empty())
        throw Error("out_of_range", vw + " has a value outside its range", villages.at);
    // Map epic 2 (MMR-248): road cells and feeder roads (MMR-253).
    const Value& roads = need(doc, "roads", Value::Kind::Object, what);
    const std::string rw = what + " roads";
    only(roads, {"splitAtPoles", "skipSeaLegs", "passesAreRoad", "endAtUndeckedBank", "cellFlag", "feeders"}, rw);
    auto& rd = p.roads;
    rd.split_at_poles = flag(roads, "splitAtPoles", rw); rd.skip_sea_legs = flag(roads, "skipSeaLegs", rw); rd.passes_are_road = flag(roads, "passesAreRoad", rw);
    rd.end_at_undecked_bank = flag(roads, "endAtUndeckedBank", rw); rd.cell_flag = flag(roads, "cellFlag", rw);
    const Value& feeders = need(roads, "feeders", Value::Kind::Object, rw);
    only(feeders, {"from", "maxCells", "passes", "water"}, rw + ".feeders");
    rd.feeder_from = names(feeders, "from", rw + ".feeders", {"castle", "village", "roadlessTown"});
    rd.feeder_max_cells = static_cast<int>(whole(feeders, "maxCells", rw + ".feeders"));
    rd.feeder_passes = flag(feeders, "passes", rw + ".feeders");
    rd.feeder_water = text(feeders, "water", rw + ".feeders");
    // The compiler implements the frozen route rules only (MMR-253): the flags are records of them, not switches.
    if (!rd.split_at_poles || !rd.skip_sea_legs || !rd.passes_are_road || !rd.end_at_undecked_bank || !rd.cell_flag)
        throw Error("out_of_range", rw + " splitAtPoles, skipSeaLegs, passesAreRoad, endAtUndeckedBank and cellFlag are the compiler's fixed rules and must be true", roads.at);
    if (rd.feeder_max_cells < 1 || rd.feeder_water != "decksOnly") throw Error("out_of_range", rw + " feeders need maxCells >= 1 and cross water only on decks (owner decision 6)", feeders.at);

    for (double f : {p.max_pass_fraction, p.max_footprint_fraction, p.max_snap_fraction, p.max_harbour_shift_fraction, p.max_excluded_village_fraction, p.max_excluded_castle_fraction})
        if (!(f >= 0 && f <= 1)) throw Error("out_of_range", what + " fractions must lie in [0, 1]", doc.at);
    if (!(p.mountain_slope > 0) || p.side < 3 || p.open_sea_cells < 1 || p.max_snap_cells < 0 || p.max_access_cells < 0 || p.max_harbour_shift_cells < 0 || p.max_bridges < 0 || p.max_bridge_cells < 1 || !(p.max_port_gate_cells >= 1) || p.min_open_cells < 1 ||
        p.max_town_snap_cells < p.max_snap_cells)
        throw Error("out_of_range", what + " has a value outside its range", doc.at);
    // Each proposed face group exists in the engine table and has the validity its class needs.
    const auto& inv = p.calibration.invalid;
    auto id_of = [&](const std::string& cls) {
        const auto it = p.calibration.face_groups.find(p.face_groups.at(cls));
        if (it == p.calibration.face_groups.end()) throw Error("unmapped_category", what + " face group '" + p.face_groups.at(cls) + "' (" + cls + ") is not in the calibration", faces.at);
        return it->second;
    };
    auto valid = [&](int id, const char* nav) { return inv.at(nav).count(id) == 0; };
    auto require = [&](const char* cls, bool ok, const char* need_text) {
        if (!ok) throw Error("face_group_mismatch", what + " face group for " + cls + " must be " + need_text, faces.at);
    };
    require("land", valid(id_of("land"), "Default") && !valid(id_of("land"), "Naval"), "Default-valid and Naval-invalid");
    require("mountain", !valid(id_of("mountain"), "All"), "invalid for All");
    for (const char* cls : {"coastalSea", "openSea", "lake", "river"}) require(cls, valid(id_of(cls), "Naval") && !valid(id_of(cls), "Default"), "Naval-valid and Default-invalid");
    require("bridge", valid(id_of("bridge"), "Default"), "Default-valid");
    require("underBridge", valid(id_of("underBridge"), "Naval"), "Naval-valid");
    // Every biome face group is a land face: in the calibration, Default-valid and Naval-invalid, and one of the land face types.
    auto land_face = [&](const std::string& name, const Value& at) {
        const auto it = p.calibration.face_groups.find(name);
        if (it == p.calibration.face_groups.end()) throw Error("unmapped_category", what + " land face group '" + name + "' is not in the calibration", at.at);
        if (!valid(it->second, "Default") || valid(it->second, "Naval") || std::find(terrain::land_face_types().begin(), terrain::land_face_types().end(), name) == terrain::land_face_types().end())
            throw Error("face_group_mismatch", what + " land face group '" + name + "' must be a Default-valid, Naval-invalid land face type", at.at);
    };
    land_face(p.land_face_default, land_faces);
    land_face(p.permanent_white_face, white);
    for (const auto& [core, name] : p.land_face_groups) land_face(name, land_faces);
    if (p.land_face_default != p.face_groups.at("land")) throw Error("face_group_mismatch", what + " cells.landFaceGroups default must be faceGroups.land", land_faces.at);
    p.sha256 = cjson::sha256_hex(cjson::canonical(doc));
    return p;
}

void check_face_table(const Policy& policy, const terrain::MaterialPolicy& materials) {
    std::set<std::string> land;
    for (const auto& [id, row] : materials.presentation) {
        const std::string& core = materials.biome_core.at(id);
        if (row.water) {
            if (policy.land_face_groups.count(core)) refuse("face_table_mismatch", "Playability cells.landFaceGroups names water biome " + core);
            continue;
        }
        land.insert(core);
        const auto it = policy.land_face_groups.find(core);
        if (it == policy.land_face_groups.end() || it->second != row.terrain_type)
            refuse("face_table_mismatch", "Playability cells.landFaceGroups gives biome " + core + " another face than the shared biome table (" + row.terrain_type + ")");
    }
    for (const auto& [core, name] : policy.land_face_groups)
        if (!land.count(core)) refuse("face_table_mismatch", "Playability cells.landFaceGroups names biome " + core + ", which the material policy does not list as land");
    const auto& w = materials.permanent_white;
    if (w.snow_months != policy.permanent_white_months || w.snow_threshold != policy.permanent_white_threshold || w.terrain_type != policy.permanent_white_face)
        refuse("face_table_mismatch", "Playability cells.landFaceGroups.permanentWhite differs from the material policy's permanentWhite");
}

std::vector<Cell> Cells::neighbours(Cell c) const {
    std::vector<Cell> out;
    out.push_back(wrap(c.row, c.col + 1));
    out.push_back(wrap(c.row, c.col - 1));
    if (c.row > 0) out.push_back({c.row - 1, c.col});
    if (c.row + 1 < rows_) out.push_back({c.row + 1, c.col});
    return out;
}

double Cells::distance(Cell a, Cell b) const {
    int dx = ((a.col - b.col) % cols_ + cols_) % cols_;
    if (dx > cols_ / 2) dx -= cols_;
    return std::hypot(static_cast<double>(dx), static_cast<double>(a.row - b.row));
}

double Cells::distance(Point p, Cell c) const {
    double dx = (c.col + 0.5) - p.x / spacing_;
    dx -= cols_ * std::round(dx / cols_);
    return std::hypot(dx, (c.row + 0.5) - p.y / spacing_);
}

std::vector<Cell> Cells::touching(Point p) const {
    const double fx = p.x / spacing_, fy = p.y / spacing_;
    auto on_line = [](double v) { return std::abs(v - std::round(v)) <= 1e-9 * std::max(1.0, std::abs(v)); };
    std::vector<int> cs, rs;
    if (on_line(fx)) { const int k = static_cast<int>(std::lround(fx)); cs = {k - 1, k}; }
    else cs = {static_cast<int>(std::floor(fx))};
    if (on_line(fy)) {
        const int k = static_cast<int>(std::lround(fy));
        if (k <= 0) rs = {0};
        else if (k >= rows_) rs = {rows_ - 1};
        else rs = {k - 1, k};
    } else {
        rs = {std::clamp(static_cast<int>(std::floor(fy)), 0, rows_ - 1)};
    }
    std::vector<Cell> out;
    for (int r : rs)
        for (int c : cs) {
            const Cell cell = wrap(r, c);
            if (std::find(out.begin(), out.end(), cell) == out.end()) out.push_back(cell);
        }
    return out;
}

namespace {
void check_grid(const Grid& g) {
    if (g.cols < 4 || g.rows < 2 || !(g.spacing > 0)) refuse("grid_mismatch", "A navigation grid needs at least 4 columns, 2 rows and a positive spacing");
    const std::size_t samples = static_cast<std::size_t>(g.rows + 1) * g.cols;
    if (g.z.size() != samples || g.intent.size() != samples) refuse("grid_mismatch", "Grid samples must be (rows + 1) * cols");
}
double cell_slope(const Grid& g, int r, int c) {
    const double a = g.z_at(r, c), b = g.z_at(r, c + 1), d = g.z_at(r + 1, c), e = g.z_at(r + 1, c + 1);
    return (std::max({a, b, d, e}) - std::min({a, b, d, e})) / g.spacing;
}
}  // namespace

std::vector<std::uint8_t> classify(const Grid& g, const Policy& policy) {
    check_grid(g);
    std::vector<std::uint8_t> out(static_cast<std::size_t>(g.cols) * g.rows);
    for (int r = 0; r < g.rows; ++r)
        for (int c = 0; c < g.cols; ++c) {
            const auto ia = g.intent_at(r, c), ie = g.intent_at(r + 1, c + 1);
            std::uint8_t k = Land;
            if ((g.z_at(r, c) + g.z_at(r + 1, c + 1)) / 2 < 0) k = Sea;
            else if (ia == 2 || ie == 2) k = Lake;
            else if (ia == 3 || ie == 3) k = River;
            else if (cell_slope(g, r, c) > policy.mountain_slope) k = Mountain;
            out[static_cast<std::size_t>(r) * g.cols + c] = k;
        }
    return out;
}

Result analyze(const Input& input, const Policy& policy) { return analyze_cells(input, policy, classify(input.grid, policy)); }

std::vector<std::uint8_t> land_types(const Input& input, const Policy& policy, const std::vector<std::uint8_t>& classes) {
    const Grid& g = input.grid;
    const std::size_t samples = static_cast<std::size_t>(g.rows + 1) * g.cols;
    if (g.biome.size() != samples || g.white.size() != samples) refuse("grid_mismatch", "Grid biome and permanent-white samples must be (rows + 1) * cols");
    if (classes.size() != static_cast<std::size_t>(g.cols) * g.rows) refuse("grid_mismatch", "Cell classes must be cols * rows");
    const auto& faces = policy.calibration.face_groups;
    const int fallback = faces.at(policy.land_face_default), snow = faces.at(policy.permanent_white_face);
    // A corner's vote: its biome's face, Snow in the permanent-white mask, none (0) for a water biome (one the face table does not name).
    std::map<int, int> face_of_biome;
    auto vote = [&](int r, int c) {
        const std::size_t i = g.sample(r, c);
        const int biome = g.biome[i];
        auto known = face_of_biome.find(biome);
        if (known == face_of_biome.end()) {
            const auto core = input.biome_core.find(biome);
            if (core == input.biome_core.end()) refuse("unmapped_category", "Biome " + std::to_string(biome) + " at a grid sample is not in the world's biome catalogue");
            const auto face = policy.land_face_groups.find(core->second);
            known = face_of_biome.emplace(biome, face == policy.land_face_groups.end() ? 0 : faces.at(face->second)).first;
        }
        return known->second == 0 ? 0 : g.white[i] ? snow : known->second;
    };
    std::vector<std::uint8_t> out(classes.size(), 0);
    for (int r = 0; r < g.rows; ++r)
        for (int c = 0; c < g.cols; ++c) {
            const std::size_t i = static_cast<std::size_t>(r) * g.cols + c;
            if (classes[i] != Land) continue;
            // NW, SE (the main diagonal: the tie-break order), NE, SW.
            const int corner[4] = {vote(r, c), vote(r + 1, c + 1), vote(r, c + 1), vote(r + 1, c)};
            int best = 0, most = 0;
            for (int k = 0; k < 4; ++k) {
                if (corner[k] == 0) continue;
                const int n = static_cast<int>(std::count(corner, corner + 4, corner[k]));
                if (n > most) { most = n; best = corner[k]; }
            }
            out[i] = static_cast<std::uint8_t>(best == 0 ? fallback : best);
        }
    return out;
}

namespace {
// The town repairs carried from one analysis round to the next (docs/travel-graph.md "Town repairs"): mountain cells carved to join a
// town's land group to a player-safe group (cell index -> the UID of the town it serves), towns whose group could not join and may take
// a forced port (town ids), and towns moved to a land cell of a player-safe group (town id -> cell).
struct TownPlan { std::map<std::size_t, std::string> joins; std::set<std::string> ports; std::map<std::string, Cell> moves; };

Result analyze_round(const Input& input, const Policy& policy, std::vector<std::uint8_t> compiled, const TownPlan& plan) {
    const Grid& g = input.grid;
    check_grid(g);
    const Cells cells(g.cols, g.rows, g.spacing);
    if (compiled.size() != cells.size()) refuse("grid_mismatch", "The cell classes must be cols * rows");
    for (auto k : compiled) if (k > River) refuse("unmapped_category", "Unknown cell class");
    Result out;
    std::vector<double> slope(cells.size());
    for (int r = 0; r < g.rows; ++r)
        for (int c = 0; c < g.cols; ++c) slope[cells.index({r, c})] = cell_slope(g, r, c);
    for (auto k : compiled) ++out.compiled_class_counts[class_name(k)];
    std::size_t compiled_mountains = out.compiled_class_counts.count("mountain") ? out.compiled_class_counts.at("mountain") : 0;
    std::vector<std::uint8_t> cls = compiled;
    auto at = [&](Cell c) -> std::uint8_t& { return cls[cells.index(c)]; };

    // Settlements: direct placement on a touching land cell, else a footprint on a touching mountain cell.
    std::map<std::string, std::size_t> settlement_index;
    out.placements.resize(input.settlements.size());
    std::size_t footprints = 0;
    std::set<std::size_t> footprint_cells;
    std::vector<std::string> footprint_uids, pass_uids, snap_uids;
    for (std::size_t i = 0; i < input.settlements.size(); ++i) {
        const auto& s = input.settlements[i];
        if (s.kind != "town" && s.kind != "village" && s.kind != "castle") refuse("unmapped_category", "Settlement " + s.id + " has unknown kind '" + s.kind + "'");
        if (!settlement_index.emplace(s.id, i).second) refuse("duplicate_id", "Duplicate settlement id " + s.id);
        auto& pl = out.placements[i];
        pl.id = s.id; pl.uid = s.uid; pl.kind = s.kind;
        const auto touching = cells.touching(s.position);
        if (const auto moved = plan.moves.find(s.id); moved != plan.moves.end()) {
            pl.cell = moved->second; pl.method = "move"; pl.shift = cells.distance(s.position, pl.cell);
            out.repairs.push_back({"move", s.uid, "the town has no cell, or its land group is not player-safe and cannot be joined; it moves to the nearest land cell of a player-safe group",
                                   pl.cell, touching.front(), pl.shift});
            continue;
        }
        for (const auto& c : touching) if (at(c) == Land) { pl.cell = c; pl.method = "direct"; break; }
        if (pl.method.empty() && policy.settlement_footprint)
            for (const auto& c : touching)
                if (at(c) == Mountain) {
                    // A town's footprint (and its access pass below) is a town repair, outside maxFootprintFraction and maxPassFraction.
                    at(c) = Land; pl.cell = c; pl.method = "footprint";
                    footprint_cells.insert(cells.index(c));
                    if (s.kind != "town") { ++footprints; footprint_uids.push_back(s.uid); }
                    out.repairs.push_back({"footprint", s.uid, "the settlement's touching cells hold no land; its mountain cell becomes its footprint", c, {}, cells.distance(s.position, c)});
                    break;
                }
        if (pl.method == "direct" || pl.method == "footprint") pl.shift = cells.distance(s.position, pl.cell);
    }
    for (const auto& s : input.settlements)
        if (s.kind == "village" && (!settlement_index.count(s.bound_town_id) || input.settlements[settlement_index.at(s.bound_town_id)].kind == "village"))
            refuse("broken_join", "Village " + s.id + " is bound to unknown town or castle " + s.bound_town_id);

    // Roads: node cells and lattice paths on the compiled classes; mountain cells on them become passes.
    auto lattice = [&](Cell a, Cell b) {
        std::vector<Cell> path;
        if (a == b) { path.push_back(a); return path; }
        int dc = ((b.col - a.col) % g.cols + g.cols) % g.cols;
        if (dc > g.cols / 2) dc -= g.cols;
        const int c_lo = std::min(0, dc), w = std::abs(dc) + 1;
        const int r_lo = std::min(a.row, b.row), h = std::abs(b.row - a.row) + 1;
        const std::size_t n = static_cast<std::size_t>(w) * h;
        auto local = [&](int r, int dcol) { return static_cast<std::size_t>(r - r_lo) * w + (dcol - c_lo); };
        auto cell_of = [&](std::size_t i) { return cells.wrap(r_lo + static_cast<int>(i / w), a.col + c_lo + static_cast<int>(i % w)); };
        std::vector<long long> cost(n, std::numeric_limits<long long>::max());
        std::vector<std::size_t> prev(n, n);
        using Item = std::pair<long long, std::size_t>;
        std::priority_queue<Item, std::vector<Item>, std::greater<Item>> queue;
        const std::size_t start = local(a.row, 0), goal = local(b.row, dc);
        cost[start] = 0; queue.push({0, start});
        while (!queue.empty()) {
            const auto [d, i] = queue.top(); queue.pop();
            if (d != cost[i]) continue;
            if (i == goal) break;
            const int r = r_lo + static_cast<int>(i / w), dcol = c_lo + static_cast<int>(i % w);
            const int steps[4][2] = {{0, 1}, {0, -1}, {-1, 0}, {1, 0}};
            for (const auto& s : steps) {
                const int rr = r + s[0], cc = dcol + s[1];
                if (rr < r_lo || rr >= r_lo + h || cc < c_lo || cc >= c_lo + w) continue;
                const std::size_t j = local(rr, cc);
                const long long nd = d + 1 + penalty(compiled[cells.index(cell_of(j))]);
                if (nd < cost[j] || (nd == cost[j] && i < prev[j])) { if (nd < cost[j]) queue.push({nd, j}); cost[j] = nd; prev[j] = i; }
            }
        }
        for (std::size_t i = goal; i != n; i = prev[i]) { path.push_back(cell_of(i)); if (i == start) break; }
        std::reverse(path.begin(), path.end());
        return path;
    };
    std::set<std::size_t> carved, town_access;   // town_access: access passes of town footprints (town repairs, outside maxPassFraction)
    std::vector<std::vector<std::vector<Cell>>> road_pieces(input.roads.size());
    std::vector<std::set<std::size_t>> producer_cells(input.roads.size());
    std::vector<std::map<std::pair<int, int>, std::vector<std::size_t>>> producer_steps(input.roads.size());
    for (std::size_t ri = 0; ri < input.roads.size(); ++ri) {
        const auto& road = input.roads[ri];
        RoadResult rr;
        rr.index = road.index;
        for (const auto* id : {&road.from_town_id, &road.to_town_id})
            if (!settlement_index.count(*id) || input.settlements[settlement_index.at(*id)].kind != "town") refuse("broken_join", "Road " + std::to_string(road.index) + " names unknown town " + *id);
        rr.from_id = road.from_town_id; rr.to_id = road.to_town_id; rr.from_uid = road.from_uid; rr.to_uid = road.to_uid;
        rr.pole_passages = road.pieces.empty() ? 0 : static_cast<int>(road.pieces.size()) - 1;
        rr.producer_pole_crossings = road.pole_crossings;
        for (std::size_t pi = 0; pi < road.pieces.size(); ++pi) {
            const auto& nodes = road.pieces[pi];
            std::vector<Cell> node_cells;
            for (const auto& p : nodes) {
                auto touching = cells.touching(p);
                std::stable_sort(touching.begin(), touching.end(), [&](Cell x, Cell y) { return rank(compiled[cells.index(x)]) < rank(compiled[cells.index(y)]); });
                node_cells.push_back(touching.front());
            }
            std::vector<Cell> piece;
            for (std::size_t k = 0; k < node_cells.size(); ++k) {
                const auto seg = k + 1 < node_cells.size() ? lattice(node_cells[k], node_cells[k + 1]) : std::vector<Cell>{node_cells[k]};
                if (road.crossing_steps.count({static_cast<int>(pi), static_cast<int>(k)}))
                    for (const auto& c : seg) { producer_cells[ri].insert(cells.index(c)); producer_steps[ri][{static_cast<int>(pi), static_cast<int>(k)}].push_back(cells.index(c)); }
                for (const auto& c : seg) if (piece.empty() || !(piece.back() == c)) piece.push_back(c);
            }
            for (const auto& c : piece) {
                rr.path.push_back(c);
                ++rr.classes[class_name(compiled[cells.index(c)])];
                rr.max_grade = std::max(rr.max_grade, slope[cells.index(c)]);
                // Footprint cells are already land and recorded as footprints, never again as passes.
                if (compiled[cells.index(c)] == Mountain && policy.road_passes && !footprint_cells.count(cells.index(c))) {
                    rr.passes.push_back(c);
                    if (carved.insert(cells.index(c)).second) {
                        at(c) = Land;
                        pass_uids.push_back(road.from_uid); pass_uids.push_back(road.to_uid);
                        out.repairs.push_back({"pass", "road " + std::to_string(road.index) + " (" + road.from_uid + " - " + road.to_uid + ")", "a mountain cell on a producer road becomes a pass", c, {}, 0});
                    }
                }
            }
            road_pieces[ri].push_back(piece);
        }
        out.roads.push_back(rr);
    }

    // Access passes: a footprint walled in by mountains gets the shortest run of mountain cells to the nearest land cell
    // (breadth-first through mountain cells, neighbours in cell order), at most maxAccessCells, counted as passes.
    for (std::size_t i = 0; i < input.settlements.size(); ++i) {
        const auto& pl = out.placements[i];
        if (pl.method != "footprint") continue;
        std::vector<int> depth(cells.size(), -1);
        std::vector<std::size_t> prev(cells.size(), cells.size());
        std::deque<std::size_t> queue{cells.index(pl.cell)};
        depth[cells.index(pl.cell)] = 0;
        std::size_t target = cells.size();
        while (!queue.empty() && target == cells.size()) {
            const std::size_t j = queue.front(); queue.pop_front();
            if (depth[j] > policy.max_access_cells) break;
            std::vector<Cell> next = cells.neighbours(cells.at(j));
            std::sort(next.begin(), next.end());
            for (const auto& nb : next) {
                const std::size_t k = cells.index(nb);
                if (depth[k] >= 0) continue;
                if (cls[k] == Land) { target = k; prev[k] = j; break; }
                if (cls[k] == Mountain) { depth[k] = depth[j] + 1; prev[k] = j; queue.push_back(k); }
            }
        }
        if (target == cells.size()) continue;   // no land within reach: the footprint stays walled in
        for (std::size_t k = prev[target]; k != cells.index(pl.cell); k = prev[k])
            if (carved.insert(k).second) {
                cls[k] = Land;
                if (input.settlements[i].kind == "town") town_access.insert(k);
                else pass_uids.push_back(input.settlements[i].uid);
                out.repairs.push_back({"access", input.settlements[i].uid, "a mountain cell between the settlement's footprint and the nearest land becomes an access pass", cells.at(k), pl.cell, cells.distance(cells.at(k), pl.cell)});
            }
    }

    // Town joins (the plan's): mountain cells carved to join a town's land group to a player-safe group. A town repair: outside
    // maxPassFraction, each with a pass host check.
    std::vector<std::size_t> joined_cells;
    for (const auto& [k, uid] : plan.joins)
        if (cls[k] == Mountain) {
            cls[k] = Land; joined_cells.push_back(k);
            out.repairs.push_back({"join", uid, "a mountain cell between the town's land group and a player-safe group becomes a pass", cells.at(k), {}, 0});
        }

    // Snaps: settlements still without a cell go to the nearest land cell within the policy radius. Town repairs: a town looks as far as
    // maxTownSnapCells (beyond maxSnapCells a town_snap), and with none there, with townCarve, the touching river or lake cell of the best
    // rank under it becomes land (a carve; never a sea cell, whose ground lies below the datum). A town's snap sits outside maxSnapFraction.
    std::size_t snaps = 0;
    std::set<std::size_t> carve_cells;
    for (std::size_t i = 0; i < input.settlements.size(); ++i) {
        auto& pl = out.placements[i];
        if (!pl.method.empty()) continue;
        const auto& s = input.settlements[i];
        const bool is_town = s.kind == "town";
        const int radius = is_town ? policy.max_town_snap_cells : policy.max_snap_cells;
        double best = std::numeric_limits<double>::max();
        Cell found;
        const int reach = radius + 1;
        const auto base = cells.touching(s.position).front();
        for (int r = std::max(0, base.row - reach); r <= std::min(g.rows - 1, base.row + reach); ++r)
            for (int dc = -reach; dc <= reach; ++dc) {
                const Cell c = cells.wrap(r, base.col + dc);
                if (at(c) != Land) continue;
                const double d = cells.distance(s.position, c);
                if (d > radius) continue;
                if (d < best || (d == best && c < found)) { best = d; found = c; }
            }
        if (found.valid() && best <= policy.max_snap_cells) {
            pl.cell = found; pl.method = "snap"; pl.shift = best;
            if (!is_town) { ++snaps; snap_uids.push_back(s.uid); }
            out.repairs.push_back({"snap", s.uid, "no touching cell is land or mountain; moved to the nearest land cell", found, base, best});
            continue;
        }
        if (found.valid()) {
            pl.cell = found; pl.method = "snap"; pl.shift = best;
            out.repairs.push_back({"town_snap", s.uid, "no land cell within maxSnapCells; the town moves to the nearest land cell within maxTownSnapCells", found, base, best});
            continue;
        }
        if (is_town && policy.town_carve) {
            auto touching = cells.touching(s.position);
            std::stable_sort(touching.begin(), touching.end(), [&](Cell x, Cell y) { return rank(at(x)) < rank(at(y)); });
            const Cell c = touching.front();
            if (at(c) == River || at(c) == Lake) {
                out.repairs.push_back({"carve", s.uid, std::string("no land cell within maxTownSnapCells; the town's ") + class_name(at(c)) + " cell becomes land", c, {}, cells.distance(s.position, c)});
                at(c) = Land; carve_cells.insert(cells.index(c));
                pl.cell = c; pl.method = "carve"; pl.shift = cells.distance(s.position, c);
                continue;
            }
        }
        pl.method = "unplaceable";
    }

    // Emitted points: the source point clamped into its cell, a quarter cell inside the edges (nearest seam image); settlements that still
    // share a point are recorded and checked by the host (settlement_checks).
    {
        const double inset = 0.25 * g.spacing, width = g.cols * g.spacing;
        for (std::size_t i = 0; i < input.settlements.size(); ++i) {
            auto& pl = out.placements[i];
            if (!pl.cell.valid()) continue;
            const Point& src = input.settlements[i].position;
            const double x0 = pl.cell.col * g.spacing, y0 = pl.cell.row * g.spacing;
            const double x = src.x - width * std::round((src.x - (x0 + 0.5 * g.spacing)) / width);
            pl.point = {std::clamp(x, x0 + inset, x0 + g.spacing - inset), std::clamp(src.y, y0 + inset, y0 + g.spacing - inset)};
        }
    }

    // Components: land, and water basins with their OpenSea cells.
    out.cls = cls;
    for (auto k : cls) ++out.class_counts[class_name(k)];
    // Land face types (MMR-254) on these classes, which the village quota and the road network leave as they are.
    out.land_type = land_types(input, policy, cls);
    {
        std::map<int, std::string> name_of;
        for (const auto& [name, id] : policy.calibration.face_groups) name_of[id] = name;
        for (auto t : out.land_type) if (t) ++out.land_type_counts[name_of.at(t)];
    }
    out.land_of.assign(cells.size(), -1);
    out.basin_of.assign(cells.size(), -1);
    auto flood = [&](std::vector<int>& label, auto&& member, auto&& on_new, auto&& on_cell) {
        int next = 0;
        for (std::size_t i = 0; i < cells.size(); ++i) {
            if (label[i] >= 0 || !member(cls[i])) continue;
            on_new(next);
            std::deque<std::size_t> queue{i};
            label[i] = next;
            while (!queue.empty()) {
                const std::size_t j = queue.front(); queue.pop_front();
                on_cell(next, j);
                for (const auto& nb : cells.neighbours(cells.at(j))) {
                    const std::size_t k = cells.index(nb);
                    if (label[k] < 0 && member(cls[k])) { label[k] = next; queue.push_back(k); }
                }
            }
            ++next;
        }
    };
    flood(out.land_of, [](std::uint8_t k) { return k == Land; }, [&](int id) { LandComponent l; l.id = id; out.lands.push_back(l); }, [&](int id, std::size_t) { ++out.lands[static_cast<std::size_t>(id)].cells; });
    flood(out.basin_of, [](std::uint8_t k) { return water(k); }, [&](int id) { Basin b; b.id = id; out.basins.push_back(b); },
          [&](int id, std::size_t j) {
              auto& b = out.basins[static_cast<std::size_t>(id)];
              ++b.cells;
              if (cls[j] == Sea) ++b.sea; else if (cls[j] == Lake) ++b.lake; else ++b.river;
          });
    for (auto& b : out.basins) b.open = b.sea > 0 && b.cells >= static_cast<std::size_t>(policy.min_open_cells);
    {
        // Chebyshev distance from the nearest non-sea cell; sea cells at least openSeaCells away are OpenSea.
        std::vector<int> dist(cells.size(), -1);
        std::deque<std::size_t> queue;
        for (std::size_t i = 0; i < cells.size(); ++i) if (cls[i] != Sea) { dist[i] = 0; queue.push_back(i); }
        while (!queue.empty()) {
            const std::size_t j = queue.front(); queue.pop_front();
            const Cell c = cells.at(j);
            for (int dr = -1; dr <= 1; ++dr)
                for (int dcol = -1; dcol <= 1; ++dcol) {
                    if ((dr == 0 && dcol == 0) || c.row + dr < 0 || c.row + dr >= g.rows) continue;
                    const std::size_t k = cells.index(cells.wrap(c.row + dr, c.col + dcol));
                    if (dist[k] < 0) { dist[k] = dist[j] + 1; queue.push_back(k); }
                }
        }
        out.open_sea.assign(cells.size(), 0);
        for (std::size_t i = 0; i < cells.size(); ++i)
            if (cls[i] == Sea && (dist[i] < 0 || dist[i] >= policy.open_sea_cells)) { out.open_sea[i] = 1; ++out.basins[static_cast<std::size_t>(out.basin_of[i])].open_sea; }
    }

    // Crossings: runs of non-land cells on a road between land cells; listed kinds no longer than maxCells join land
    // components (coast crossings never do: sea is crossed only through ports).
    UnionFind uf(out.lands.size());
    std::set<std::pair<int, int>> joined_pairs;
    std::vector<std::string> bridge_uids;
    std::map<std::size_t, std::pair<int, int>> deck_pair;   // cell of a joining crossing -> the land components it joins
    for (std::size_t ri = 0; ri < out.roads.size(); ++ri) {
        auto& rr = out.roads[ri];
        for (const auto& piece : road_pieces[ri]) {
            Cell last_land;
            std::vector<Cell> run;
            auto emit = [&](Cell after) {
                Crossing x;
                x.cells = run;
                bool sea = false, lake = false, blocked = false;
                for (const auto& c : run) { const auto k = at(c); sea |= k == Sea; lake |= k == Lake; blocked |= k == Mountain; x.producer |= producer_cells[ri].count(cells.index(c)) != 0; }
                x.kind = blocked ? "blocked" : sea ? "coast" : lake ? "lake" : "river";
                x.land_a = last_land.valid() ? out.land_of[cells.index(last_land)] : -1;
                x.land_b = after.valid() ? out.land_of[cells.index(after)] : -1;
                x.joins = x.land_a >= 0 && x.land_b >= 0 && x.land_a != x.land_b && policy.bridge_kinds.count(x.kind) != 0 &&
                          run.size() <= static_cast<std::size_t>(policy.max_bridge_cells);
                // One deck per cell: a cell an earlier joining crossing bridges between two other land components cannot carry this
                // crossing too (the runtime lays one passage per deck cell). The 2026-10-19 sweep's world ae2f24ef: a river cell between
                // three components, the second join never realized (navigation.group_not_realized).
                const std::pair<int, int> lands{std::min(x.land_a, x.land_b), std::max(x.land_a, x.land_b)};
                if (x.joins)
                    for (const auto& c : run) { const auto it = deck_pair.find(cells.index(c)); if (it != deck_pair.end() && it->second != lands) x.joins = false; }
                if (x.joins) for (const auto& c : run) deck_pair.emplace(cells.index(c), lands);
                if (x.joins) {
                    uf.unite(static_cast<std::size_t>(x.land_a), static_cast<std::size_t>(x.land_b));
                    if (joined_pairs.insert({std::min(x.land_a, x.land_b), std::max(x.land_a, x.land_b)}).second) { bridge_uids.push_back(rr.from_uid); bridge_uids.push_back(rr.to_uid); }
                }
                rr.crossings.push_back(x);
                run.clear();
            };
            for (const auto& c : piece) {
                if (at(c) == Land) { if (!run.empty()) emit(c); last_land = c; }
                else run.push_back(c);
            }
            if (!run.empty()) emit(Cell{});
        }
        // Each producer-reported river crossing: matched by a compiled crossing, or no compiled water on its step.
        std::set<std::size_t> crossing_cells;
        for (const auto& x : rr.crossings) for (const auto& c : x.cells) crossing_cells.insert(cells.index(c));
        for (const auto& [step, step_cells] : producer_steps[ri]) {
            const bool matched = std::any_of(step_cells.begin(), step_cells.end(), [&](std::size_t c) { return crossing_cells.count(c) != 0; });
            rr.producer_crossings.push_back({step.first, step.second, matched ? "matched" : "no_compiled_water"});
        }
        // Drawable pieces (MMR-253, "Road network"): each lattice piece (split at poles) is cut at every crossing without a deck (the sea legs
        // of a road between groups, river and lake crossings that join nothing, blocked runs), ending at the bank; a joining crossing's deck
        // cells stay on the piece. The crossings come in the order the walk above emitted them.
        const auto& from_pl = out.placements[settlement_index.at(rr.from_id)];
        const auto& to_pl = out.placements[settlement_index.at(rr.to_id)];
        std::size_t xi = 0;
        for (std::size_t pi = 0; pi < road_pieces[ri].size(); ++pi) {
            const bool first = pi == 0, last = pi + 1 == road_pieces[ri].size();
            RoadPiece cur;
            std::vector<Cell> run;
            auto gate = [&](const Placement& pl, Cell c) { RoadEnd e; e.kind = "gate"; e.settlement = pl.id; e.cell = c; e.point = pl.cell.valid() ? pl.point : cells.centre(c); return e; };
            auto pole = [&](Cell c) { RoadEnd e; e.kind = "pole"; e.cell = c; e.point = cells.centre(c); return e; };
            auto bank = [&](Cell c, Cell w) { RoadEnd e; e.kind = "bank"; e.cell = c; e.water = w; e.point = bank_point(cells, c, w); return e; };
            for (const auto& c : road_pieces[ri][pi]) {
                if (at(c) != Land) { run.push_back(c); continue; }
                if (cur.path.empty()) {
                    if (!run.empty()) { ++xi; cur.start = bank(c, run.back()); }
                    else cur.start = first ? gate(from_pl, c) : pole(c);
                } else if (!run.empty()) {
                    const auto& x = rr.crossings[xi++];
                    if (x.joins) { for (const auto& w : run) { cur.path.push_back(w); rr.decks.push_back(w); } }
                    else {
                        cur.end = bank(cur.path.back(), run.front());
                        rr.pieces.push_back(cur);
                        cur = RoadPiece{};
                        cur.start = bank(c, run.back());
                    }
                }
                run.clear();
                cur.path.push_back(c);
            }
            if (!run.empty()) ++xi;
            if (cur.path.empty()) continue;
            cur.end = !run.empty() ? bank(cur.path.back(), run.front()) : last ? gate(to_pl, cur.path.back()) : pole(cur.path.back());
            rr.pieces.push_back(cur);
        }
    }
    out.required_bridges = joined_pairs.size();
    std::map<std::size_t, int> group_of_root;
    for (auto& l : out.lands) {
        const std::size_t root = uf.find(static_cast<std::size_t>(l.id));
        auto it = group_of_root.find(root);
        if (it == group_of_root.end()) { it = group_of_root.emplace(root, static_cast<int>(out.groups.size())).first; Group gr; gr.id = it->second; out.groups.push_back(gr); }
        l.group = it->second;
        out.groups[static_cast<std::size_t>(l.group)].lands.push_back(l.id);
    }
    auto group_of_cell = [&](Cell c) { const int l = out.land_of[cells.index(c)]; return l < 0 ? -1 : out.lands[static_cast<std::size_t>(l)].group; };
    for (auto& rr : out.roads) {
        const auto& a = out.placements[settlement_index.at(rr.from_id)];
        const auto& b = out.placements[settlement_index.at(rr.to_id)];
        rr.same_group = a.cell.valid() && b.cell.valid() && group_of_cell(a.cell) >= 0 && group_of_cell(a.cell) == group_of_cell(b.cell);
    }
    for (auto& pl : out.placements) {
        if (!pl.cell.valid()) continue;
        pl.land = out.land_of[cells.index(pl.cell)];
        pl.group = out.lands[static_cast<std::size_t>(pl.land)].group;
        auto& l = out.lands[static_cast<std::size_t>(pl.land)];
        (pl.kind == "town" ? l.towns : pl.kind == "village" ? l.villages : l.castles).push_back(pl.id);
    }

    // Villages must share their town's land group; unplaceable villages and castles are excluded.
    auto excluded = [&](const std::string& id) { return std::any_of(out.exclusions.begin(), out.exclusions.end(), [&](const Exclusion& e) { return e.id == id; }); };
    for (std::size_t i = 0; i < input.settlements.size(); ++i) {
        const auto& s = input.settlements[i];
        const auto& pl = out.placements[i];
        if (s.kind == "town") continue;
        if (pl.method == "unplaceable") { out.exclusions.push_back({s.id, s.uid, s.kind, "unplaceable: no land cell within " + std::to_string(policy.max_snap_cells) + " cells"}); continue; }
        if (s.kind == "village") {
            const auto& bound = out.placements[settlement_index.at(s.bound_town_id)];
            if (bound.group >= 0 && pl.group != bound.group)
                out.exclusions.push_back({s.id, s.uid, s.kind, "not land-reachable from its bound " + bound.kind + " " + bound.uid +
                                                                   " (village group " + std::to_string(pl.group) + ", " + bound.kind + " group " + std::to_string(bound.group) + ")"});
        }
    }

    // A village farther than the engine trade distance from every town and castle of its land group is excluded (before the ports, so a
    // harbour candidate sees it; the castle exclusions below never touch a village's group, which holds its player-safe town): the binding
    // (VillageBinding, economy.json binding) could bind it to none within that limit, and activation would refuse the world. The limit is
    // kEngineTradeRatio x the world's town spacing, the mean periodic distance from each town's point to its nearest town's (the
    // installed DefaultVillageTradeModel limit, economy.json binding.distance.engineTradeRatio). Seed 62: a village by the pole line 183
    // units from the nearest settlement of its group, against a limit of 128.8.
    {
        constexpr double kEngineTradeRatio = 3.0;
        const double width = g.cols * g.spacing;
        auto between = [&](Point a, Point b) { double dx = std::abs(a.x - b.x); dx = std::min(dx, width - dx); return std::hypot(dx, a.y - b.y); };
        std::vector<const Placement*> towns, forts;
        for (const auto& pl : out.placements)
            if (pl.cell.valid() && pl.kind != "village" && !excluded(pl.id)) { forts.push_back(&pl); if (pl.kind == "town") towns.push_back(&pl); }
        if (towns.size() >= 2) {
            double sum = 0;
            for (const auto* t : towns) {
                double nearest = std::numeric_limits<double>::max();
                for (const auto* u : towns) if (u != t) nearest = std::min(nearest, between(t->point, u->point));
                sum += nearest;
            }
            const double limit = kEngineTradeRatio * sum / static_cast<double>(towns.size());
            for (const auto& pl : out.placements) {
                if (pl.kind != "village" || !pl.cell.valid() || excluded(pl.id)) continue;
                double nearest = std::numeric_limits<double>::max();
                for (const auto* f : forts) if (f->group == pl.group) nearest = std::min(nearest, between(pl.point, f->point));
                if (nearest > limit)
                    out.exclusions.push_back({pl.id, pl.uid, "village", "beyond the engine trade distance of every town and castle of its land group (" + std::to_string(nearest) + " > " +
                                                                         std::to_string(limit) + " units)"});
            }
        }
    }

    // Ports, then player reachability over land groups and open basins.
    bind_ports(input, policy, cells, out);
    for (const auto& port : out.ports) {
        if (!port.valid) continue;
        const auto& town = out.placements[settlement_index.at(port.town_id)];
        out.groups[static_cast<std::size_t>(town.group)].port_basins.insert(port.chosen.basin);
    }
    for (std::size_t i = 0; i < cells.size(); ++i) {
        if (cls[i] != Land) continue;
        for (const auto& nb : cells.neighbours(cells.at(i))) {
            const int b = out.basin_of[cells.index(nb)];
            if (b >= 0 && out.basins[static_cast<std::size_t>(b)].open) out.groups[static_cast<std::size_t>(out.lands[static_cast<std::size_t>(out.land_of[i])].group)].coast_basins.insert(b);
        }
    }
    std::map<int, std::string> min_town_uid;
    for (const auto& pl : out.placements)
        if (pl.kind == "town" && pl.group >= 0) {
            ++out.groups[static_cast<std::size_t>(pl.group)].towns;
            auto it = min_town_uid.find(pl.group);
            if (it == min_town_uid.end() || pl.uid < it->second) min_town_uid[pl.group] = pl.uid;
        }
    for (const auto& gr : out.groups)
        if (gr.towns > 0) {
            if (out.main_group < 0) { out.main_group = gr.id; continue; }
            const auto& m = out.groups[static_cast<std::size_t>(out.main_group)];
            if (gr.towns > m.towns || (gr.towns == m.towns && min_town_uid[gr.id] < min_town_uid[m.id])) out.main_group = gr.id;
        }
    auto reach = [&]() {
        if (out.main_group < 0) return;
        auto& groups = out.groups;
        for (auto& gr : groups) gr.forward = gr.backward = gr.safe = false;
        groups[static_cast<std::size_t>(out.main_group)].forward = groups[static_cast<std::size_t>(out.main_group)].backward = true;
        for (bool changed = true; changed;) {
            changed = false;
            for (const auto& from : groups) {
                if (!from.forward) continue;
                for (int b : from.port_basins)
                    for (auto& to : groups) if (!to.forward && to.coast_basins.count(b)) { to.forward = true; changed = true; }
            }
            for (auto& from : groups) {
                if (from.backward) continue;
                for (int b : from.port_basins)
                    if (std::any_of(groups.begin(), groups.end(), [&](const Group& to) { return to.backward && to.coast_basins.count(b); })) { from.backward = true; changed = true; break; }
            }
        }
        for (auto& gr : groups) gr.safe = gr.forward && gr.backward;
    };
    reach();
    // Forced ports (a town repair, forcedPorts): a town the plan allows one (its group could not join a player-safe group) of a group that
    // is not player-safe, without a valid harbour port, whose group holds a kept village for each of its towns and castles, takes the
    // coast pair of its own land component on an open basin that a group returning to the main group touches: the pair whose sea cell is
    // nearest the gate, then whose land cell is, then cell order; never a held pair or a deck cell (deck_cells), the port cell at most
    // maxPortGateCells from the gate cell, the first such pair whose path bound (port_path_bound) is at most maxPortGateCells x spacing.
    // Towns in input order, one forced port per group in a round (RT-43: as few as possible); then reachability again. The port is marked
    // forced: MMR-252 pins or synthesizes its harbour village (RT-12).
    if (out.main_group >= 0 && policy.forced_ports && !plan.ports.empty()) {
        std::set<std::size_t> held_land, held_sea;
        for (const auto& port : out.ports) if (port.valid) { held_land.insert(cells.index(port.chosen.land_cell)); held_sea.insert(cells.index(port.chosen.sea_cell)); }
        const auto decks = deck_cells(cells, out);
        const double limit = policy.max_port_gate_cells * cells.spacing();
        std::map<int, int> kept_villages, fortifications;
        for (const auto& pl : out.placements)
            if (pl.group >= 0 && !excluded(pl.id)) ++(pl.kind == "village" ? kept_villages : fortifications)[pl.group];
        bool added = false;
        std::set<int> served;
        for (std::size_t i = 0; i < input.settlements.size(); ++i) {
            const auto& pl = out.placements[i];
            if (pl.kind != "town" || !plan.ports.count(pl.id) || pl.group < 0 || served.count(pl.group) || out.groups[static_cast<std::size_t>(pl.group)].safe ||
                kept_villages[pl.group] < fortifications[pl.group])
                continue;
            auto existing = std::find_if(out.ports.begin(), out.ports.end(), [&](const Port& p) { return p.town_id == pl.id; });
            if (existing != out.ports.end() && existing->valid) continue;
            std::vector<PortCandidate> pairs;
            for (std::size_t k = 0; k < cells.size(); ++k) {
                if (cls[k] != Land || out.land_of[k] != pl.land || held_land.count(k)) continue;
                const Cell l = cells.at(k);
                for (const auto& s : cells.neighbours(l)) {
                    const std::size_t si = cells.index(s);
                    const int b = out.basin_of[si];
                    if (b < 0 || !out.basins[static_cast<std::size_t>(b)].open || held_sea.count(si) || decks.count(si)) continue;
                    if (std::none_of(out.groups.begin(), out.groups.end(), [&](const Group& gr) { return gr.backward && gr.coast_basins.count(b); })) continue;
                    PortCandidate pair;
                    pair.land_cell = l; pair.sea_cell = s; pair.basin = b; pair.gate_distance = cells.distance(s, pl.cell); pair.entrance_distance = cells.distance(l, pl.cell);
                    if (pair.gate_distance <= policy.max_port_gate_cells) pairs.push_back(pair);
                }
            }
            std::sort(pairs.begin(), pairs.end(), [](const PortCandidate& a, const PortCandidate& b) {
                if (a.gate_distance != b.gate_distance) return a.gate_distance < b.gate_distance;
                if (a.entrance_distance != b.entrance_distance) return a.entrance_distance < b.entrance_distance;
                return a.land_cell < b.land_cell || (a.land_cell == b.land_cell && a.sea_cell < b.sea_cell);
            });
            auto pick = std::find_if(pairs.begin(), pairs.end(), [&](const PortCandidate& c) { return port_path_bound(cells, out, decks, c.sea_cell, c.basin, pl.point, limit) <= limit; });
            if (pick == pairs.end()) continue;
            PortCandidate best = *pick;
            best.kind = "coast"; best.status = "valid";
            if (existing == out.ports.end()) {
                Port p; p.town_id = pl.id; p.town_uid = pl.uid;
                existing = out.ports.insert(std::find_if(out.ports.begin(), out.ports.end(), [&](const Port& q) { return q.town_id > pl.id; }), p);
            }
            existing->valid = true; existing->forced = true; existing->chosen = best; existing->candidates.push_back(best);
            held_land.insert(cells.index(best.land_cell)); held_sea.insert(cells.index(best.sea_cell));
            out.groups[static_cast<std::size_t>(pl.group)].port_basins.insert(best.basin);
            auto& basin_ports = out.basins[static_cast<std::size_t>(best.basin)].ports;
            basin_ports.insert(std::upper_bound(basin_ports.begin(), basin_ports.end(), pl.id), pl.id);
            out.repairs.push_back({"coast_port", pl.uid, "the town's land group cannot reach and return to the main group, cannot join a player-safe group and the town has no valid harbour port; its coast pair nearest the gate on an open basin becomes its forced port",
                                   best.sea_cell, best.land_cell, best.gate_distance});
            served.insert(pl.group);
            added = true;
        }
        if (added) reach();
    }
    auto safe = [&](int group) { return group >= 0 && out.groups[static_cast<std::size_t>(group)].safe; };
    for (std::size_t i = 0; i < input.settlements.size(); ++i) {
        const auto& s = input.settlements[i];
        const auto& pl = out.placements[i];
        if (s.kind == "town") {
            if (pl.method == "unplaceable" || pl.group < 0) out.reasons.push_back({"quality.unplaceable_town", "Town " + s.uid + " has no cell: no land cell within " + std::to_string(policy.max_town_snap_cells) +
                                                                   " cells, no river or lake cell under it to carve and no free land cell of a player-safe group to move to", {s.uid}});
            else if (!safe(pl.group)) {
                const auto& gr = out.groups[static_cast<std::size_t>(pl.group)];
                std::string why = "no valid port";
                for (const auto& port : out.ports) if (port.town_id == s.id && !port.valid) why = "its port is invalid";
                out.reasons.push_back({"quality.unreachable_town", "Town " + s.uid + " in land group " + std::to_string(pl.group) + " is not player-safe (" + (gr.forward ? "" : "unreachable from the main group; ") +
                                                                       (gr.backward ? "" : "cannot return to the main group; ") + std::to_string(gr.port_basins.size()) + " port basins, " +
                                                                       std::to_string(gr.coast_basins.size()) + " open coast basins; " + why + ")", {s.uid}});
            }
        } else if (s.kind == "castle" && !excluded(s.id) && !safe(pl.group)) {
            out.exclusions.push_back({s.id, s.uid, s.kind, "not player-safe: land group " + std::to_string(pl.group) + " cannot be reached from the main group and left again"});
        }
    }

    // Bounds.
    const double settlements = static_cast<double>(input.settlements.size());
    // Every bound reason names the UIDs behind it: road ends for passes and bridges, settlements for access passes,
    // footprints and snaps, harbour villages for harbour shifts.
    auto unique = [](std::vector<std::string> uids) { std::sort(uids.begin(), uids.end()); uids.erase(std::unique(uids.begin(), uids.end()), uids.end()); return uids; };
    std::vector<std::string> shift_uids;
    for (const auto& x : out.repairs) if (x.kind == "harbour_shift") shift_uids.push_back(x.subject);
    const std::size_t bounded_passes = carved.size() - town_access.size();
    if (static_cast<double>(bounded_passes) > policy.max_pass_fraction * static_cast<double>(compiled_mountains))
        out.reasons.push_back({"quality.repair_bound_exceeded", std::to_string(bounded_passes) + " road and access passes exceed maxPassFraction of " + std::to_string(compiled_mountains) + " mountain cells", unique(pass_uids)});
    if (static_cast<double>(footprints) > policy.max_footprint_fraction * settlements)
        out.reasons.push_back({"quality.repair_bound_exceeded", std::to_string(footprints) + " settlement footprints exceed maxFootprintFraction", unique(footprint_uids)});
    if (static_cast<double>(snaps) > policy.max_snap_fraction * settlements)
        out.reasons.push_back({"quality.repair_bound_exceeded", std::to_string(snaps) + " settlement snaps exceed maxSnapFraction", unique(snap_uids)});
    if (out.required_bridges > static_cast<std::size_t>(policy.max_bridges))
        out.reasons.push_back({"quality.repair_bound_exceeded", std::to_string(out.required_bridges) + " required bridge candidates exceed bridges.maxRequired", unique(bridge_uids)});
    if (static_cast<double>(shift_uids.size()) > policy.max_harbour_shift_fraction * static_cast<double>(input.harbours.size()))
        out.reasons.push_back({"quality.repair_bound_exceeded", std::to_string(shift_uids.size()) + " harbour shifts exceed maxHarbourShiftFraction of " + std::to_string(input.harbours.size()) + " harbours", unique(shift_uids)});
    std::size_t villages = 0, castles = 0, excluded_villages = 0, excluded_castles = 0;
    for (const auto& s : input.settlements) { villages += s.kind == "village"; castles += s.kind == "castle"; }
    std::vector<std::string> village_uids, castle_uids;
    for (const auto& e : out.exclusions) {
        if (e.kind == "village") { ++excluded_villages; village_uids.push_back(e.uid); }
        else { ++excluded_castles; castle_uids.push_back(e.uid); }
    }
    if (static_cast<double>(excluded_villages) > policy.max_excluded_village_fraction * static_cast<double>(villages))
        out.reasons.push_back({"quality.too_many_exclusions", std::to_string(excluded_villages) + " of " + std::to_string(villages) + " villages excluded, above maxExcludedVillageFraction", village_uids});
    if (static_cast<double>(excluded_castles) > policy.max_excluded_castle_fraction * static_cast<double>(castles))
        out.reasons.push_back({"quality.too_many_exclusions", std::to_string(excluded_castles) + " of " + std::to_string(castles) + " castles excluded, above maxExcludedCastleFraction", castle_uids});

    // Transitions: each valid port, and each group's coasts on open basins (disembark candidates).
    for (const auto& port : out.ports)
        if (port.valid) {
            const auto& town = out.placements[settlement_index.at(port.town_id)];
            out.transitions.push_back({"port", port.town_id, town.group, port.chosen.basin, port.chosen.land_cell, port.chosen.sea_cell, 1});
        }
    std::map<std::pair<int, int>, Transition> coasts;
    for (std::size_t i = 0; i < cells.size(); ++i) {
        if (cls[i] != Land) continue;
        const int group = out.lands[static_cast<std::size_t>(out.land_of[i])].group;
        for (const auto& nb : cells.neighbours(cells.at(i))) {
            const int b = out.basin_of[cells.index(nb)];
            if (b < 0 || !out.basins[static_cast<std::size_t>(b)].open) continue;
            auto [it, fresh] = coasts.try_emplace({group, b}, Transition{"coast", "", group, b, cells.at(i), nb, 0});
            ++it->second.coast_pairs;
            (void)fresh;
        }
    }
    for (const auto& [key, t] : coasts) out.transitions.push_back(t);

    // Mandatory host checks for MMR-38 (the settlement checks come from settlement_checks, below).
    auto host = [&](const std::string& check, const std::string& subject, const std::string& expected) { out.host_checks.push_back({check, subject, expected}); };
    for (const auto& port : out.ports)
        if (port.valid) {
            host("portFace", port.town_id, "the port cell is a Naval-valid face (" + policy.face_groups.at("coastalSea") + ") sharing an edge with the land entrance, and SetSailAtPosition succeeds there");
            host("portGate", port.town_id, "GetPortToGateDistanceForSettlement is finite and within the installed envelope, and a Default route joins the land entrance and the gate");
        }
    for (const auto& rr : out.roads)
        for (std::size_t k = 0; k < rr.crossings.size(); ++k)
            if (rr.crossings[k].joins) host("bridge", "road " + std::to_string(rr.index) + " crossing " + std::to_string(k), "the crossing cells carry Default-valid faces over Naval-valid ones (proposed " +
                                                                                                                   policy.face_groups.at("bridge") + " over " + policy.face_groups.at("underBridge") +
                                                                                                                   "; no shipped campaign map authors Bridge faces, so MMR-38 chooses) and a Default route crosses them");
    // MMR-254: each names its cell's land face type (the shared table's face for its corners).
    auto face = [&](std::size_t i) { return land_names(policy, out, cells, {cells.at(i)}); };
    for (std::size_t i : carved) host("pass", "cell " + std::to_string(cells.at(i).row) + "," + std::to_string(cells.at(i).col), "a Default-valid face (" + face(i) + "), not " + policy.face_groups.at("mountain"));
    for (std::size_t i : joined_cells) host("pass", "cell " + std::to_string(cells.at(i).row) + "," + std::to_string(cells.at(i).col), "a Default-valid face (" + face(i) + "), not " + policy.face_groups.at("mountain"));
    for (std::size_t i : footprint_cells) host("footprint", "cell " + std::to_string(cells.at(i).row) + "," + std::to_string(cells.at(i).col), "a Default-valid face (" + face(i) + "), not " + policy.face_groups.at("mountain"));
    // A carved town cell (a town repair) is checked as a footprint: the land face the runtime builds there.
    for (std::size_t i : carve_cells) host("footprint", "cell " + std::to_string(cells.at(i).row) + "," + std::to_string(cells.at(i).col), "a Default-valid face (" + face(i) + "), carved from water under a town");
    for (const auto& rr : out.roads)
        if (rr.same_group) host("landRoute", "road " + std::to_string(rr.index), "a Default route joins the gates of " + rr.from_uid + " and " + rr.to_uid);
    for (const auto& gr : out.groups)
        if (gr.safe && gr.id != out.main_group) host("seaRoute", "group " + std::to_string(gr.id), "a player can sail from a port of the group to a coast of the main group and back");
    for (const auto& t : out.transitions)
        if (t.land_cell.col == 0 || t.land_cell.col == g.cols - 1 || t.sea_cell.col == 0 || t.sea_cell.col == g.cols - 1)
            host("seamTransition", t.kind + (t.town_id.empty() ? " group " + std::to_string(t.group) : " " + t.town_id), "embark and disembark work at the periodic seam (R10 leaves seam transitions unresolved)");
    for (const auto& b : out.basins)
        if (b.open) host("openSea", "basin " + std::to_string(b.id), b.open_sea > 0 ? "its OpenSea cells carry " + policy.face_groups.at("openSea") + " faces (storm spawn area)" : "no OpenSea cell: storms cannot spawn in this basin");
    host("islands", "world", "every town gate, port and kept castle lies on one engine island (All navigation)");
    host("padding", "grid", "faces beyond the pole lines are not navigable");
    settlement_checks(input, out, policy);

    out.region_land_to_sea = policy.calibration.land_to_sea; out.region_sea_to_land = policy.calibration.sea_to_land;
    out.embark_threshold = policy.calibration.embark_threshold; out.port_gate_envelope_max = policy.calibration.port_gate_envelope_max;
    out.max_port_gate_units = policy.max_port_gate_cells * g.spacing;
    out.status = out.reasons.empty() ? "ok" : "rejected";
    return out;
}

bool town_reasons(const Result& r) {
    return std::any_of(r.reasons.begin(), r.reasons.end(), [](const Reason& x) { return x.code == "quality.unplaceable_town" || x.code == "quality.unreachable_town"; });
}

// Grows the plan from a round's result; false when it adds nothing. Each land group holding a town that is not player-safe joins a
// player-safe group through the shortest run of mountain cells, at most maxAccessCells (breadth-first from the group's land cells in
// cell order, neighbours in cell order), served for the group's first town in input order. The towns of a group that cannot join may
// take a forced port in the next round (forcedPorts). A town whose forced port did not make it player-safe (or could not be laid), and
// a town with no cell, moves to the nearest land cell of a player-safe group from its source point that was land before any repair and
// holds no settlement, then cell order; with none it stays, and the world is rejected.
bool extend(TownPlan& plan, const Result& r, const Input& input, const Policy& policy, const std::vector<std::uint8_t>& compiled) {
    if (r.main_group < 0) return false;
    const Cells cells(input.grid.cols, input.grid.rows, input.grid.spacing);
    auto group_of = [&](std::size_t k) { return r.cls[k] == Land && r.land_of[k] >= 0 ? r.lands[static_cast<std::size_t>(r.land_of[k])].group : -1; };
    auto safe_land = [&](std::size_t k) { const int gr = group_of(k); return gr >= 0 && r.groups[static_cast<std::size_t>(gr)].safe; };
    std::set<std::size_t> occupied;
    for (const auto& pl : r.placements) if (pl.cell.valid()) occupied.insert(cells.index(pl.cell));
    bool changed = false;
    std::map<int, bool> joins;   // group -> joined this round
    std::vector<std::size_t> stranded;
    for (std::size_t i = 0; i < r.placements.size(); ++i) {
        const auto& pl = r.placements[i];
        if (pl.kind != "town") continue;
        if (!pl.cell.valid() || pl.group < 0) { stranded.push_back(i); continue; }
        if (r.groups[static_cast<std::size_t>(pl.group)].safe) continue;
        if (const auto it = joins.find(pl.group); it != joins.end()) { if (!it->second) stranded.push_back(i); continue; }
        std::vector<int> depth(cells.size(), -1);
        std::vector<std::size_t> prev(cells.size(), cells.size());
        std::deque<std::size_t> queue;
        for (std::size_t k = 0; k < cells.size(); ++k) if (group_of(k) == pl.group) { depth[k] = 0; queue.push_back(k); }
        std::size_t target = cells.size();
        while (!queue.empty() && target == cells.size()) {
            const std::size_t j = queue.front(); queue.pop_front();
            std::vector<Cell> next = cells.neighbours(cells.at(j));
            std::sort(next.begin(), next.end());
            for (const auto& nb : next) {
                const std::size_t k = cells.index(nb);
                if (depth[k] >= 0) continue;
                if (r.cls[j] == Mountain && safe_land(k)) { target = k; prev[k] = j; break; }
                if (r.cls[k] == Mountain && depth[j] < policy.max_access_cells) { depth[k] = depth[j] + 1; prev[k] = j; queue.push_back(k); }
            }
        }
        bool fresh = false;
        if (target != cells.size())
            for (std::size_t k = prev[target]; depth[k] > 0; k = prev[k]) fresh |= plan.joins.emplace(k, pl.uid).second;
        joins[pl.group] = fresh;
        changed |= fresh;
        if (!fresh) stranded.push_back(i);
    }
    // A stranded town with a cell and no forced port tried yet may take one next round; the others move.
    if (policy.forced_ports) {
        std::vector<std::size_t> moving;
        for (std::size_t i : stranded) {
            const auto& pl = r.placements[i];
            if (pl.cell.valid() && pl.group >= 0 && !plan.ports.count(pl.id)) { plan.ports.insert(pl.id); changed = true; }
            else moving.push_back(i);
        }
        stranded = moving;
    }
    for (std::size_t i : stranded) {
        const auto& s = input.settlements[i];
        if (plan.moves.count(s.id)) continue;
        Cell found;
        double best = 0;
        for (std::size_t k = 0; k < cells.size(); ++k) {
            if (compiled[k] != Land || !safe_land(k) || occupied.count(k)) continue;
            const Cell c = cells.at(k);
            const double d = cells.distance(s.position, c);
            if (!found.valid() || d < best || (d == best && c < found)) { found = c; best = d; }
        }
        if (!found.valid()) continue;
        plan.moves[s.id] = found;
        occupied.insert(cells.index(found));
        changed = true;
    }
    return changed;
}
}  // namespace

void settlement_checks(const Input& input, Result& r, const Policy& policy) {
    static const std::set<std::string> kinds{"gateFace", "settlementFace", "villageRoute", "distinctPositions"};
    r.host_checks.erase(std::remove_if(r.host_checks.begin(), r.host_checks.end(), [](const HostCheck& h) { return kinds.count(h.check) != 0; }), r.host_checks.end());
    std::set<std::string> excluded_ids;
    for (const auto& e : r.exclusions) excluded_ids.insert(e.id);
    std::map<std::string, std::string> uid_of;
    for (const auto& s : input.settlements) uid_of[s.id] = s.uid;
    std::vector<HostCheck> checks;
    // MMR-254: the expected face is any land type of the cells touching the point (15-16% of land-land edges change biome).
    const Cells cells(input.grid.cols, input.grid.rows, input.grid.spacing);
    for (const auto& pl : r.placements) {
        if (!pl.cell.valid() || excluded_ids.count(pl.id)) continue;
        const std::string face = land_names(policy, r, cells, cells.touching(pl.point));
        if (pl.kind == "town") checks.push_back({"gateFace", pl.id, "the gate cell is a Default-valid face (" + face + ") on the engine island of every player-safe group"});
        else checks.push_back({"settlementFace", pl.id, "the " + pl.kind + "'s cell is a Default-valid face (" + face + ") on the island of its land group"});
    }
    for (std::size_t i = 0; i < input.settlements.size(); ++i) {
        const auto& s = input.settlements[i];
        if (s.kind == "village" && !excluded_ids.count(s.id) && r.placements[i].cell.valid())
            checks.push_back({"villageRoute", s.id, "a Default route joins the village and its bound settlement " + uid_of.at(s.bound_town_id)});
    }
    std::map<std::pair<double, double>, std::vector<std::string>> by_point;
    for (const auto& pl : r.placements) if (pl.cell.valid() && !excluded_ids.count(pl.id)) by_point[{pl.point.x, pl.point.y}].push_back(pl.id);
    r.collisions.clear();
    for (const auto& [point, ids] : by_point) if (ids.size() > 1) r.collisions.push_back(ids);
    r.host_checks.insert(r.host_checks.begin(), checks.begin(), checks.end());
    for (const auto& ids : r.collisions) {
        std::string joined;
        for (const auto& id : ids) joined += (joined.empty() ? "" : ", ") + id;
        r.host_checks.push_back({"distinctPositions", joined, "these settlements share one emitted point; the engine accepts it or MMR-38 separates them"});
    }
}

void lay_roads(const Input& input, Result& r, const Policy& policy, const std::map<std::string, std::string>& castle_towns) {
    const Grid& g = input.grid;
    const Cells cells(g.cols, g.rows, g.spacing);
    if (r.cls.size() != cells.size() || r.placements.size() != input.settlements.size()) refuse("grid_mismatch", "The road network needs the analysed cells and placements");
    r.roads.erase(std::remove_if(r.roads.begin(), r.roads.end(), [](const RoadResult& x) { return x.kind == "feeder"; }), r.roads.end());
    r.road.assign(cells.size(), 0);
    RoadNetwork net;
    net.laid = true;
    std::vector<int> owner(cells.size(), -1);   // the road that first laid each cell
    // A deck cell is entered and left only along the route that crosses it (deck_steps, both directions): a feeder never crosses water
    // anywhere else, nor turns on a deck.
    std::set<std::pair<std::size_t, std::size_t>> deck_steps;
    int next_index = 0;
    for (const auto& rr : r.roads) {
        next_index = std::max(next_index, rr.index + 1);
        for (const auto& piece : rr.pieces)
            for (std::size_t k = 0; k < piece.path.size(); ++k) {
                const std::size_t i = cells.index(piece.path[k]);
                r.road[i] = 1;
                if (owner[i] < 0) owner[i] = rr.index;
                if (k + 1 < piece.path.size() && (r.cls[i] != Land || r.cls[cells.index(piece.path[k + 1])] != Land)) {
                    const std::size_t j = cells.index(piece.path[k + 1]);
                    deck_steps.insert({i, j}); deck_steps.insert({j, i});
                }
            }
    }
    for (std::size_t i = 0; i < cells.size(); ++i) net.route_cells += r.road[i];
    // Without feeders.passes, the carved passes (road, access, join) are not feeder ground.
    std::set<std::size_t> no_feeder;
    if (!policy.roads.feeder_passes)
        for (const auto& x : r.repairs) if (x.kind == "pass" || x.kind == "access" || x.kind == "join") no_feeder.insert(cells.index(x.cell));
    std::set<std::string> gone;
    for (const auto& e : r.exclusions) gone.insert(e.id);
    std::map<std::string, std::size_t> index_of;
    for (std::size_t i = 0; i < input.settlements.size(); ++i) index_of[input.settlements[i].id] = i;
    auto kept = [&](std::size_t i) { return !gone.count(r.placements[i].id) && r.placements[i].cell.valid(); };
    auto cell_of = [&](std::size_t i) { return cells.index(r.placements[i].cell); };
    std::vector<char> town_cell(cells.size(), 0);
    for (std::size_t i = 0; i < input.settlements.size(); ++i) if (kept(i) && input.settlements[i].kind == "town") town_cell[cell_of(i)] = 1;
    auto component = [&](std::size_t start) {
        std::vector<char> mark(cells.size(), 0);
        if (!r.road[start]) { mark[start] = 1; return mark; }
        std::deque<std::size_t> queue{start};
        mark[start] = 1;
        while (!queue.empty()) {
            const std::size_t j = queue.front(); queue.pop_front();
            for (const auto& nb : cells.neighbours(cells.at(j))) {
                const std::size_t k = cells.index(nb);
                if (!mark[k] && r.road[k]) { mark[k] = 1; queue.push_back(k); }
            }
        }
        return mark;
    };
    // The cheapest edge path from `from` to a target cell over land travel cells and existing decks (along their route), at most
    // feeders.maxCells cells: entering a road cell costs 1, any other cell 2; ties go to the lower predecessor index.
    const int max_cells = policy.roads.feeder_max_cells;
    auto search = [&](std::size_t from, const std::vector<char>& target) {
        std::vector<long long> cost(cells.size(), std::numeric_limits<long long>::max());
        std::vector<int> len(cells.size(), 0);
        std::vector<std::size_t> prev(cells.size(), cells.size());
        using Item = std::pair<long long, std::size_t>;
        std::priority_queue<Item, std::vector<Item>, std::greater<Item>> queue;
        cost[from] = 0; len[from] = 1; queue.push({0, from});
        std::size_t goal = cells.size();
        while (!queue.empty()) {
            const auto [d, i] = queue.top(); queue.pop();
            if (d != cost[i]) continue;
            if (target[i]) { goal = i; break; }
            if (len[i] >= max_cells) continue;
            for (const auto& nb : cells.neighbours(cells.at(i))) {
                const std::size_t k = cells.index(nb);
                const bool ok = r.cls[i] == Land && r.cls[k] == Land ? !no_feeder.count(k) : deck_steps.count({i, k}) != 0;
                if (!ok) continue;
                const long long nd = d + (r.road[k] ? 1 : 2);
                if (nd < cost[k] || (nd == cost[k] && i < prev[k])) { if (nd < cost[k]) queue.push({nd, k}); cost[k] = nd; prev[k] = i; len[k] = len[i] + 1; }
            }
        }
        std::vector<std::size_t> path;
        if (goal == cells.size()) return path;
        for (std::size_t i = goal;; i = prev[i]) { path.push_back(i); if (i == from) break; }
        std::reverse(path.begin(), path.end());
        return path;
    };
    auto any_road_or_town = [&](std::size_t self) {
        std::vector<char> t(cells.size(), 0);
        for (std::size_t i = 0; i < cells.size(); ++i) t[i] = r.road[i] || town_cell[i];
        t[self] = 0;
        return t;
    };
    const auto& from = policy.roads.feeder_from;
    auto listed = [&](const char* k) { return std::find(from.begin(), from.end(), k) != from.end(); };
    auto bound_of = [&](const Settlement& s) { return s.kind == "village" ? s.bound_town_id : s.kind == "castle" && castle_towns.count(s.id) ? castle_towns.at(s.id) : std::string(); };
    auto bound_kept = [&](const std::string& id) { return !id.empty() && index_of.count(id) && kept(index_of.at(id)); };
    // A feeder of settlement i along path (cells, from its own cell): from its gate to the gate of its bound settlement (primary: the path
    // reached the bound settlement's road and ends on its cell) or of a town on the last cell, else to a junction with the road that laid it.
    auto lay = [&](std::size_t i, const std::vector<std::size_t>& path, bool primary, const std::string& bound_id) {
        const auto& s = input.settlements[i];
        RoadResult f;
        f.kind = "feeder"; f.index = next_index++; f.settlement = s.id;
        f.from_id = s.id; f.from_uid = s.uid; f.same_group = true;
        const std::size_t end = path.back();
        std::string to_id;
        if (primary && end == cell_of(index_of.at(bound_id))) to_id = bound_id;
        else
            for (std::size_t k = 0; k < input.settlements.size() && to_id.empty(); ++k)
                if (kept(k) && input.settlements[k].kind == "town" && cell_of(k) == end) to_id = input.settlements[k].id;
        RoadPiece piece;
        for (std::size_t c : path) {
            piece.path.push_back(cells.at(c));
            if (r.cls[c] != Land) f.decks.push_back(cells.at(c));
        }
        piece.start.kind = "gate"; piece.start.settlement = s.id; piece.start.cell = cells.at(path.front()); piece.start.point = r.placements[i].point;
        if (!to_id.empty()) {
            piece.end.kind = "gate"; piece.end.settlement = to_id; piece.end.cell = cells.at(end); piece.end.point = r.placements[index_of.at(to_id)].point;
        } else {
            piece.end.kind = "junction"; piece.end.road = owner[end]; piece.end.cell = cells.at(end); piece.end.point = cells.centre(cells.at(end));
            const auto joined = std::find_if(r.roads.begin(), r.roads.end(), [&](const RoadResult& x) { return x.index == owner[end]; });
            to_id = primary ? bound_id : joined->kind == "feeder" ? joined->to_id : joined->from_id;
        }
        f.to_id = to_id; f.to_uid = input.settlements[index_of.at(to_id)].uid;
        f.path = piece.path;
        f.pieces.push_back(piece);
        for (std::size_t c : path) { if (!r.road[c]) ++net.feeder_only_cells; r.road[c] = 1; if (owner[c] < 0) owner[c] = f.index; }
        net.max_feeder_cells = std::max(net.max_feeder_cells, path.size());
        ++net.feeders;
        r.roads.push_back(f);
    };
    // Road-less towns first (to the nearest road or town of their land group), then castles (to their garrison town), then villages (to
    // their bound town or castle); each in input order, each on the network the earlier ones laid. A castle or village that cannot reach its
    // bound settlement's road within maxCells takes the nearest road or town instead.
    for (const char* kind : {"town", "castle", "village"}) {
        if (!listed(std::string(kind) == "town" ? "roadlessTown" : kind)) continue;
        for (std::size_t i = 0; i < input.settlements.size(); ++i) {
            const auto& s = input.settlements[i];
            if (s.kind != kind || !kept(i)) continue;
            const std::size_t self = cell_of(i);
            const std::string bound_id = bound_of(s);
            std::vector<std::size_t> path;
            bool primary = false;
            if (bound_kept(bound_id)) {
                const std::size_t b = cell_of(index_of.at(bound_id));
                std::vector<char> target = component(b);
                if (target[self]) continue;   // already on its bound settlement's road (a gate spur below, if no piece ends at its gate)
                target[self] = 0;
                path = search(self, target);
                primary = !path.empty();
            } else if (s.kind == "town" && r.road[self]) {
                continue;
            }
            if (path.empty()) {
                if (s.kind != "town" && r.road[self]) continue;   // on a road already; the summary judges it
                path = search(self, any_road_or_town(self));
            }
            if (path.empty()) {
                if (s.kind != "town") net.unconnected.push_back({s.id, s.uid, s.kind, "no road or town within feeders.maxCells (" + std::to_string(max_cells) + ") travel cells"});
                continue;
            }
            lay(i, path, primary, bound_id);
        }
    }
    // Gate spurs (acc4 c6). A settlement whose cell already lies on a road has no feeder of its own; when no piece ends at its gate, the
    // drawn road only passes its cell and its decals are clipped at the gates around it (a village of A' sat 4.73 units from the nearest
    // decal). The same holds when every piece that ends at its gate leaves its cell over water (a deck: the decals stop at the bank). Each
    // such kept settlement on a road cell, in the same order, gets a spur: a feeder from its gate through its cell to the land road cell
    // beside it whose centre is nearest the gate (ties: the lower cell index). The second cell gives the spur a drawable length (the gate's
    // own cell centre can lie 2.2 units from the gate, less than the gate clip and one decal), and such a spur adds no road cell. Without a
    // land road cell beside it, the ordinary search to the nearest other road or town runs; a path that steps onto water first is dropped.
    std::set<std::string> at_gate;   // settlements with a piece that leaves their gate's cell over land
    auto gate_ends = [&](const RoadResult& x) {
        for (const auto& p : x.pieces) {
            const std::size_t n = p.path.size();
            if (n < 2) continue;
            if (p.start.kind == "gate" && r.cls[cells.index(p.path[1])] == Land) at_gate.insert(p.start.settlement);
            if (p.end.kind == "gate" && r.cls[cells.index(p.path[n - 2])] == Land) at_gate.insert(p.end.settlement);
        }
    };
    for (const auto& x : r.roads) gate_ends(x);
    for (const char* kind : {"town", "castle", "village"}) {
        if (!listed(std::string(kind) == "town" ? "roadlessTown" : kind)) continue;
        for (std::size_t i = 0; i < input.settlements.size(); ++i) {
            const auto& s = input.settlements[i];
            if (s.kind != kind || !kept(i) || at_gate.count(s.id) || !r.road[cell_of(i)]) continue;   // off every road: unconnected or an islet
            const std::size_t self = cell_of(i);
            if (r.cls[self] != Land) continue;
            const Point gate = r.placements[i].point;
            std::size_t best = cells.size();
            double nearest = 0;
            for (const auto& nb : cells.neighbours(cells.at(self))) {
                const std::size_t k = cells.index(nb);
                if (!r.road[k] || r.cls[k] != Land || no_feeder.count(k)) continue;
                const double d = cells.distance(gate, nb);
                if (best == cells.size() || d < nearest || (d == nearest && k < best)) { best = k; nearest = d; }
            }
            std::vector<std::size_t> path;
            if (best != cells.size()) path = {self, best};
            else path = search(self, any_road_or_town(self));
            if (path.size() < 2 || r.cls[path[1]] != Land) continue;
            const std::string bound_id = bound_of(s);
            const bool primary = bound_kept(bound_id) && component(cell_of(index_of.at(bound_id)))[path.back()];
            lay(i, path, primary, bound_id);
            gate_ends(r.roads.back());
        }
    }

    // The summary: road cells, decks, water without a deck (0 by construction; RT-17), ends, and who is connected.
    const auto decks = deck_cells(cells, r);
    for (std::size_t i = 0; i < cells.size(); ++i) {
        if (!r.road[i]) continue;
        ++net.cells;
        if (r.cls[i] != Land) { if (decks.count(i)) ++net.deck_cells; else ++net.water_without_deck; }
    }
    for (const auto& rr : r.roads)
        for (const auto& p : rr.pieces) { ++net.ends[p.start.kind]; ++net.ends[p.end.kind]; }
    std::vector<int> label(cells.size(), -1);
    std::vector<char> has_town;
    for (std::size_t i = 0; i < cells.size(); ++i) {
        if (!r.road[i] || label[i] >= 0) continue;
        const int id = static_cast<int>(has_town.size());
        has_town.push_back(0);
        std::deque<std::size_t> queue{i};
        label[i] = id;
        while (!queue.empty()) {
            const std::size_t j = queue.front(); queue.pop_front();
            if (town_cell[j]) has_town[static_cast<std::size_t>(id)] = 1;
            for (const auto& nb : cells.neighbours(cells.at(j))) {
                const std::size_t k = cells.index(nb);
                if (label[k] < 0 && r.road[k]) { label[k] = id; queue.push_back(k); }
            }
        }
    }
    std::set<std::string> already;
    for (const auto& u : net.unconnected) already.insert(u.id);
    // A land group with no road cell and all its kept settlements on one cell (an islet town with its villages) has no network to join:
    // its settlements sit at their town.
    std::map<int, std::set<std::size_t>> group_cells;
    std::set<int> road_groups;
    for (std::size_t i = 0; i < input.settlements.size(); ++i) if (kept(i)) group_cells[r.placements[i].group].insert(cell_of(i));
    for (std::size_t i = 0; i < cells.size(); ++i)
        if (r.road[i] && r.land_of[i] >= 0) road_groups.insert(r.lands[static_cast<std::size_t>(r.land_of[i])].group);
    for (std::size_t i = 0; i < input.settlements.size(); ++i) {
        const auto& s = input.settlements[i];
        if (!kept(i)) continue;
        auto& tally = net.connected[s.kind];
        ++tally.second;
        const int l = label[cell_of(i)];
        std::string why;
        const int grp = r.placements[i].group;
        if (l < 0 && town_cell[cell_of(i)] && !road_groups.count(grp) && group_cells[grp].size() == 1) why = "";
        else if (l < 0) why = "its cell is not a road cell";
        else if (!has_town[static_cast<std::size_t>(l)]) why = "its road holds no town";
        if (why.empty()) ++tally.first;
        else if (!already.count(s.id)) net.unconnected.push_back({s.id, s.uid, s.kind, why});
        // Connected, but to another town's road: its bound settlement lies beyond feeders.maxCells by land (across water without a deck).
        const std::string bound = s.kind == "village" ? s.bound_town_id : s.kind == "castle" && castle_towns.count(s.id) ? castle_towns.at(s.id) : "";
        if (why.empty() && !bound.empty() && index_of.count(bound) && kept(index_of.at(bound)) && label[cell_of(index_of.at(bound))] != l)
            net.bound_not_reached.push_back({s.id, s.uid, s.kind, "its road does not reach its bound settlement " + input.settlements[index_of.at(bound)].uid});
    }
    r.network = net;
}

// Towns are never dropped (docs/travel-graph.md "Town repairs"): a round with no town reason is the result; otherwise the plan grows
// (joins, then forced ports, then moves) and the analysis runs again from the compiled classes, at most six rounds. A world whose towns
// are all player-safe in the first round (world A) is analysed once, unchanged.
Result analyze_cells(const Input& input, const Policy& policy, std::vector<std::uint8_t> compiled) {
    TownPlan plan;
    for (int round = 1;; ++round) {
        Result r = analyze_round(input, policy, compiled, plan);
        if (!town_reasons(r) || round == 6 || !extend(plan, r, input, policy, compiled)) return r;
    }
}

void check_preservation(const source::SourceWorld& world, const campaign::Compiled& campaign, const Result& result) {
    auto set_of = [](std::vector<std::string> uids) { std::sort(uids.begin(), uids.end()); return uids; };
    std::vector<std::string> sites, towns, ids, travel;
    for (const auto& c : world.cities) sites.push_back(c.uid);
    for (const auto& t : campaign.towns) towns.push_back(t.uid);
    for (const auto& e : campaign.id_map) if (e.kind == "town") ids.push_back(e.uid);
    std::vector<std::string> unplaced;
    for (const auto& pl : result.placements)
        if (pl.kind == "town") { travel.push_back(pl.uid); if (!pl.cell.valid()) unplaced.push_back(pl.uid); }
    sites = set_of(sites);
    const auto& placed = world.metadata.founding_placed_cities;
    auto differ = [&](const std::string& stage, const std::vector<std::string>& got) {
        std::vector<std::string> diff;
        std::set_symmetric_difference(sites.begin(), sites.end(), got.begin(), got.end(), std::back_inserter(diff));
        std::string named;
        for (std::size_t i = 0; i < diff.size() && i < 10; ++i) named += " " + diff[i];
        refuse("city_not_preserved", "City preservation: " + std::to_string(sites.size()) + " FWGC++ sites but " + std::to_string(got.size()) + " " + stage + (diff.empty() ? " (a repeated uid)" : "; differing uids:" + named));
    };
    if (std::adjacent_find(sites.begin(), sites.end()) != sites.end()) differ("sites (a repeated site uid)", sites);
    if (!placed || *placed != static_cast<std::int64_t>(sites.size()))
        refuse("city_not_preserved", "City preservation: settlements.founding.placed_cities (" + (placed ? std::to_string(*placed) : std::string("absent")) + ") differs from the " + std::to_string(sites.size()) + " sites");
    for (const auto& [stage, uids] : {std::pair<const char*, std::vector<std::string>*>{"compiled towns", &towns}, {"id-map towns", &ids}, {"travel towns", &travel}}) {
        *uids = set_of(*uids);
        if (*uids != sites) differ(stage, *uids);
    }
    if (result.status == "ok" && !unplaced.empty()) refuse("city_not_preserved", "City preservation: town " + unplaced.front() + " has no cell in the travel graph (" + std::to_string(unplaced.size()) + " unplaced)");
}

Input input_of(const source::SourceWorld& world, const projection::Projection& projection, const terrain::Compiled& terrain, const campaign::Compiled& campaign) {
    Input in;
    const double sp = terrain.spacing;
    auto cells_of = [&](double v, const char* what) {
        const double k = v / sp;
        if (std::abs(k - std::round(k)) > 1e-9) refuse("grid_mismatch", std::string("The ") + what + " is not a whole number of terrain cells");
        return static_cast<int>(std::lround(k));
    };
    const int c0 = cells_of(projection.strip(), "seam strip"), r0 = cells_of(projection.pole_margin(), "pole margin");
    in.grid.cols = cells_of(projection.width(), "world width");
    in.grid.rows = cells_of(projection.height(), "world height");
    in.grid.spacing = sp;
    if (in.grid.cols != terrain.period) refuse("grid_mismatch", "The terrain seam period differs from the world width");
    if (r0 + in.grid.rows >= terrain.side || c0 + in.grid.cols >= terrain.side) refuse("grid_mismatch", "The canonical globe does not fit the compiled terrain");
    for (int r = 0; r <= in.grid.rows; ++r)
        for (int c = 0; c < in.grid.cols; ++c) {
            const std::size_t i = static_cast<std::size_t>(r0 + r) * terrain.side + (c0 + c);
            in.grid.z.push_back(terrain.z[i]);
            in.grid.intent.push_back(terrain.intent[i]);
            in.grid.biome.push_back(terrain.biome[i]);
            in.grid.white.push_back(terrain.white[i]);
        }
    for (const auto& b : world.biomes) in.biome_core[static_cast<int>(b.id)] = b.core;
    std::map<std::string, const source::City*> city_by_uid;
    std::map<std::int64_t, const source::City*> city_by_site;
    for (const auto& c : world.cities) { city_by_uid[c.uid] = &c; city_by_site[c.site_id] = &c; }
    std::map<std::string, const source::Hamlet*> hamlet_by_uid;
    for (const auto& h : world.hamlets) hamlet_by_uid[h.uid] = &h;
    std::map<std::string, const source::Castle*> castle_by_uid;
    for (const auto& k : world.castles) castle_by_uid[k.uid] = &k;
    std::map<std::string, std::string> town_id_of_uid;
    for (const auto& t : campaign.towns) {
        const auto* c = city_by_uid.at(t.uid);
        in.settlements.push_back({t.id, t.uid, "town", "", projection.sample(c->x, c->z)});
        town_id_of_uid[t.uid] = t.id;
    }
    std::map<std::string, const campaign::Village*> village_by_id;
    for (const auto& v : campaign.villages) {
        const auto* h = hamlet_by_uid.at(v.uid);
        in.settlements.push_back({v.id, v.uid, "village", v.bound_id, projection.sample(h->x, h->z)});
        village_by_id[v.id] = &v;
    }
    for (const auto& k : campaign.castles) {
        const auto* c = castle_by_uid.at(k.uid);
        in.settlements.push_back({k.id, k.uid, "castle", "", projection.sample(c->x, c->z)});
    }
    for (const auto& p : campaign.ports)
        for (const auto& vid : p.village_ids) {
            const auto* v = village_by_id.at(vid);
            const auto* h = hamlet_by_uid.at(v->uid);
            if (!h->harbour) refuse("broken_join", "Port village " + v->uid + " has no harbour record");
            in.harbours.push_back({v->id, v->uid, p.town_id, h->harbour->sea_node, projection.node(h->harbour->sea_node), h->harbour->harbor_quality});
        }
    const auto& grid = projection.grid();
    int index = 0;
    for (const auto& route : world.routes) {
        Road road;
        road.index = index++;
        const auto from = city_by_site.find(route.from), to = city_by_site.find(route.to);
        if (from == city_by_site.end() || to == city_by_site.end()) refuse("broken_join", "Road " + std::to_string(road.index) + " names an unknown settlement site");
        road.from_uid = from->second->uid; road.to_uid = to->second->uid;
        road.from_town_id = town_id_of_uid.at(road.from_uid); road.to_town_id = town_id_of_uid.at(road.to_uid);
        std::vector<std::vector<std::int64_t>> piece_nodes(1);
        for (std::size_t k = 0; k < route.nodes.size(); ++k) {
            if (k > 0 && !grid.adjacent(route.nodes[k - 1], route.nodes[k])) refuse("invalid_path", "Road " + std::to_string(road.index) + " step " + std::to_string(k) + " is not a graph edge");
            if (grid.pole_of(route.nodes[k]) != source::Pole::None) { if (!piece_nodes.back().empty()) piece_nodes.emplace_back(); continue; }
            piece_nodes.back().push_back(route.nodes[k]);
        }
        if (piece_nodes.back().empty()) piece_nodes.pop_back();
        for (const auto& [a, b] : route.river_crossings)
            if (grid.pole_of(a) != source::Pole::None || grid.pole_of(b) != source::Pole::None) ++road.pole_crossings;
        for (std::size_t pi = 0; pi < piece_nodes.size(); ++pi) {
            std::vector<Point> points;
            for (auto node : piece_nodes[pi]) points.push_back(projection.node(node));
            road.pieces.push_back(points);
            for (std::size_t k = 0; k + 1 < piece_nodes[pi].size(); ++k)
                for (const auto& [a, b] : route.river_crossings)
                    if ((a == piece_nodes[pi][k] && b == piece_nodes[pi][k + 1]) || (b == piece_nodes[pi][k] && a == piece_nodes[pi][k + 1]))
                        road.crossing_steps.insert({static_cast<int>(pi), static_cast<int>(k)});
        }
        in.roads.push_back(road);
    }
    return in;
}

std::string cells_bytes(const Input& input, const Result& result) {
    const std::size_t n = static_cast<std::size_t>(input.grid.cols) * input.grid.rows;
    if (result.cls.size() != n || result.open_sea.size() != n || result.land_type.size() != n || (!result.road.empty() && result.road.size() != n))
        refuse("grid_mismatch", "cells.bin planes must be cols * rows");
    std::string out = "MNBLTC02";
    for (int v : {input.grid.cols, input.grid.rows}) { const auto u = static_cast<std::uint32_t>(v); out.append(reinterpret_cast<const char*>(&u), 4); }
    out.append(reinterpret_cast<const char*>(result.cls.data()), n);
    out.append(reinterpret_cast<const char*>(result.open_sea.data()), n);
    out.append(reinterpret_cast<const char*>(result.land_type.data()), n);
    // Flags: bit 0 the road cell (lay_roads; none before it ran).
    std::string flags(n, static_cast<char>(0));
    for (std::size_t i = 0; i < result.road.size(); ++i) if (result.road[i]) flags[i] = 1;
    return out + flags;
}

namespace {
Value inputs_value(const Identity& id) {
    Value v = Value::object();
    v.set("worldSha256", str(id.world_sha256)); v.set("sourceIdentity", str(id.source_identity)); v.set("projectionSha256", str(id.projection_sha256));
    v.set("terrainMasksSha256", str(id.masks_sha256)); v.set("terrainHeightfieldSha256", str(id.heightfield_sha256)); v.set("idMapSha256", str(id.id_map_sha256));
    v.set("playabilitySha256", str(id.policy_sha256)); v.set("navalTransitionsSha256", str(id.calibration_sha256)); v.set("terrainSide", integer(id.side));
    return v;
}
Value reasons_value(const std::vector<Reason>& reasons) {
    Value out = array();
    for (const auto& r : reasons) { Value v = Value::object(); v.set("code", str(r.code)); v.set("message", str(r.message)); v.set("uids", strings(r.uids)); out.items.push_back(v); }
    return out;
}
Value candidate_value(const PortCandidate& c, double spacing) {
    Value v = Value::object();
    // A coast port (a town repair) has no sea node; its harbour village is the one the village quota synthesized and pinned (its harbour
    // cell the port cell). A harbour candidate carries no kind.
    if (c.kind == "coast") { v.set("kind", str(c.kind)); if (!c.village_id.empty()) { v.set("village", str(c.village_id)); v.set("villageUid", str(c.village_uid)); } }
    else { v.set("village", str(c.village_id)); v.set("villageUid", str(c.village_uid)); v.set("seaNode", integer(c.sea_node)); }
    v.set("status", str(c.status));
    if (!c.reason.empty()) v.set("reason", str(c.reason));
    if (c.harbour_cell.valid()) v.set("harbourCell", cell_value(c.harbour_cell));
    if (c.status == "valid") {
        v.set("seaCell", cell_value(c.sea_cell)); v.set("landCell", cell_value(c.land_cell));
        v.set("seaPoint", point_value({(c.sea_cell.col + 0.5) * spacing, (c.sea_cell.row + 0.5) * spacing}));
        v.set("landPoint", point_value({(c.land_cell.col + 0.5) * spacing, (c.land_cell.row + 0.5) * spacing}));
        if (c.kind != "coast") v.set("harbourShiftCells", num(c.harbour_shift));
        v.set("gateDistanceCells", num(c.gate_distance)); v.set("entranceDistanceCells", num(c.entrance_distance)); v.set("basin", integer(c.basin));
    }
    v.set("quality", num(c.quality));
    return v;
}
}  // namespace

std::string graph_document(const Input& input, const Result& r, const Identity& id) {
    const Cells cells(input.grid.cols, input.grid.rows, input.grid.spacing);
    Value doc = Value::object();
    doc.set("schema", str("mnbl.travel-graph")); doc.set("version", integer(1));
    doc.set("status", str(r.status)); doc.set("engineValidation", str("pending"));
    doc.set("reasons", reasons_value(r.reasons));
    doc.set("inputs", inputs_value(id));
    Value grid = Value::object();
    grid.set("cols", integer(input.grid.cols)); grid.set("rows", integer(input.grid.rows)); grid.set("spacing", num(input.grid.spacing));
    grid.set("columnOffset", integer(id.column_offset)); grid.set("rowOffset", integer(id.row_offset));
    grid.set("coordinates", str("cell (r, c) covers logical [c, c+1] x [r, r+1] * spacing; columns wrap; physical sample = (rowOffset + r, columnOffset + c)"));
    grid.set("compiledClasses", counts_value(r.compiled_class_counts)); grid.set("classes", counts_value(r.class_counts));
    std::size_t open_sea = 0; for (auto f : r.open_sea) open_sea += f;
    grid.set("openSeaCells", integer(static_cast<long long>(open_sea)));
    grid.set("cellsSha256", str(cjson::sha256_hex(cells_bytes(input, r))));
    grid.set("landTypes", counts_value(r.land_type_counts));
    if (r.network.laid) grid.set("roadCells", integer(static_cast<long long>(r.network.cells)));
    doc.set("grid", grid);
    Value lands = array();
    for (const auto& l : r.lands) {
        Value v = Value::object(); v.set("id", integer(l.id)); v.set("cells", integer(static_cast<long long>(l.cells))); v.set("group", integer(l.group));
        v.set("towns", strings(l.towns)); v.set("villages", strings(l.villages)); v.set("castles", strings(l.castles)); lands.items.push_back(v);
    }
    doc.set("landComponents", lands);
    Value groups = array();
    for (const auto& g : r.groups) {
        Value v = Value::object(); v.set("id", integer(g.id)); v.set("lands", ints(g.lands)); v.set("towns", integer(static_cast<long long>(g.towns)));
        v.set("portBasins", ints(std::vector<int>(g.port_basins.begin(), g.port_basins.end()))); v.set("coastBasins", ints(std::vector<int>(g.coast_basins.begin(), g.coast_basins.end())));
        v.set("reachableFromMain", boolean(g.forward)); v.set("returnsToMain", boolean(g.backward)); v.set("playerSafe", boolean(g.safe)); groups.items.push_back(v);
    }
    doc.set("groups", groups); doc.set("mainGroup", integer(r.main_group));
    Value basins = array();
    for (const auto& b : r.basins) {
        Value v = Value::object(); v.set("id", integer(b.id)); v.set("cells", integer(static_cast<long long>(b.cells))); v.set("sea", integer(static_cast<long long>(b.sea)));
        v.set("lake", integer(static_cast<long long>(b.lake))); v.set("river", integer(static_cast<long long>(b.river))); v.set("openSea", integer(static_cast<long long>(b.open_sea)));
        v.set("open", boolean(b.open)); v.set("ports", strings(b.ports)); basins.items.push_back(v);
    }
    doc.set("basins", basins);
    Value settlements = array();
    for (std::size_t i = 0; i < r.placements.size(); ++i) {
        const auto& p = r.placements[i];
        Value v = Value::object(); v.set("id", str(p.id)); v.set("uid", str(p.uid)); v.set("kind", str(p.kind)); v.set("method", str(p.method));
        v.set("sourcePoint", point_value(input.settlements[i].position));
        if (p.cell.valid()) { v.set("cell", cell_value(p.cell)); v.set("point", point_value(p.point)); v.set("shiftCells", num(p.shift)); v.set("land", integer(p.land)); v.set("group", integer(p.group)); }
        settlements.items.push_back(v);
    }
    doc.set("settlements", settlements);
    Value collisions = array();
    for (const auto& ids : r.collisions) collisions.items.push_back(strings(ids));
    doc.set("positionCollisions", collisions);
    Value engine = Value::object();
    engine.set("regionSwitchCostLandToSea", integer(r.region_land_to_sea)); engine.set("regionSwitchCostSeaToLand", integer(r.region_sea_to_land));
    engine.set("embarkDisembarkThreshold", num(r.embark_threshold)); engine.set("installedPortGateMax", num(r.port_gate_envelope_max)); engine.set("maxPortGateUnits", num(r.max_port_gate_units));
    engine.set("contract", str("MMR-17: the player embarks only at a town port and disembarks on any coast; the AI embarks and disembarks on any coast"));
    doc.set("engine", engine);
    Value roads = array();
    auto end_value = [&](const RoadEnd& e) {
        Value v = Value::object(); v.set("kind", str(e.kind)); v.set("cell", cell_value(e.cell)); v.set("point", point_value(e.point));
        if (!e.settlement.empty()) v.set("settlement", str(e.settlement));
        if (e.water.valid()) v.set("water", cell_value(e.water));
        if (e.road >= 0) v.set("road", integer(e.road));
        return v;
    };
    for (const auto& rr : r.roads) {
        Value v = Value::object(); v.set("index", integer(rr.index)); v.set("kind", str(rr.kind)); v.set("from", str(rr.from_id)); v.set("to", str(rr.to_id)); v.set("fromUid", str(rr.from_uid)); v.set("toUid", str(rr.to_uid));
        v.set("cells", integer(static_cast<long long>(rr.path.size())));
        Value pieces = array();
        for (const auto& p : rr.pieces) { Value pv = Value::object(); pv.set("path", cells_value(p.path)); pv.set("start", end_value(p.start)); pv.set("end", end_value(p.end)); pieces.items.push_back(pv); }
        v.set("pieces", pieces); v.set("decks", cells_value(rr.decks));
        if (rr.kind == "feeder") { v.set("settlement", str(rr.settlement)); roads.items.push_back(v); continue; }
        v.set("compiledClasses", counts_value(rr.classes)); v.set("passes", cells_value(rr.passes));
        v.set("maxGrade", num(rr.max_grade)); v.set("polePassages", integer(rr.pole_passages)); v.set("sameGroup", boolean(rr.same_group));
        Value xs = array();
        for (const auto& x : rr.crossings) {
            Value xv = Value::object(); xv.set("kind", str(x.kind)); xv.set("cells", cells_value(x.cells)); xv.set("producerReported", boolean(x.producer));
            xv.set("lands", ints({x.land_a, x.land_b})); xv.set("joins", boolean(x.joins)); xs.items.push_back(xv);
        }
        v.set("crossings", xs);
        Value pcs = array();
        for (const auto& pc : rr.producer_crossings) { Value pv = Value::object(); pv.set("piece", integer(pc.piece)); pv.set("step", integer(pc.step)); pv.set("outcome", str(pc.outcome)); pcs.items.push_back(pv); }
        v.set("producerCrossings", pcs); v.set("producerPoleCrossings", integer(rr.producer_pole_crossings));
        roads.items.push_back(v);
    }
    doc.set("roads", roads); doc.set("requiredBridges", integer(static_cast<long long>(r.required_bridges)));
    if (r.network.laid) {
        const auto& n = r.network;
        Value net = Value::object();
        net.set("cells", integer(static_cast<long long>(n.cells))); net.set("routeCells", integer(static_cast<long long>(n.route_cells)));
        net.set("feederOnlyCells", integer(static_cast<long long>(n.feeder_only_cells))); net.set("deckCells", integer(static_cast<long long>(n.deck_cells)));
        net.set("waterCellsWithoutDeck", integer(static_cast<long long>(n.water_without_deck))); net.set("feeders", integer(static_cast<long long>(n.feeders)));
        net.set("maxFeederCells", integer(static_cast<long long>(n.max_feeder_cells))); net.set("ends", counts_value(n.ends));
        Value connected = Value::object();
        for (const auto& [kind, t] : n.connected) { Value cv = Value::object(); cv.set("connected", integer(static_cast<long long>(t.first))); cv.set("total", integer(static_cast<long long>(t.second))); connected.set(kind, cv); }
        net.set("connected", connected);
        Value un = array();
        for (const auto& u : n.unconnected) { Value uv = Value::object(); uv.set("id", str(u.id)); uv.set("uid", str(u.uid)); uv.set("kind", str(u.kind)); uv.set("reason", str(u.reason)); un.items.push_back(uv); }
        net.set("unconnected", un);
        Value apart = array();
        for (const auto& u : n.bound_not_reached) { Value uv = Value::object(); uv.set("id", str(u.id)); uv.set("uid", str(u.uid)); uv.set("kind", str(u.kind)); uv.set("reason", str(u.reason)); apart.items.push_back(uv); }
        net.set("boundNotReached", apart);
        doc.set("roadNetwork", net);
    }
    Value transitions = array();
    for (const auto& t : r.transitions) {
        Value v = Value::object(); v.set("kind", str(t.kind)); if (!t.town_id.empty()) v.set("town", str(t.town_id));
        v.set("group", integer(t.group)); v.set("basin", integer(t.basin)); v.set("landCell", cell_value(t.land_cell)); v.set("seaCell", cell_value(t.sea_cell));
        v.set("landPoint", point_value(cells.centre(t.land_cell))); v.set("seaPoint", point_value(cells.centre(t.sea_cell)));
        v.set("coastPairs", integer(static_cast<long long>(t.coast_pairs))); v.set("status", str("pendingEngineValidation")); transitions.items.push_back(v);
    }
    doc.set("transitions", transitions);
    Value exclusions = array();
    for (const auto& e : r.exclusions) { Value v = Value::object(); v.set("id", str(e.id)); v.set("uid", str(e.uid)); v.set("kind", str(e.kind)); v.set("reason", str(e.reason)); exclusions.items.push_back(v); }
    doc.set("exclusions", exclusions);
    Value repairs = array();
    for (const auto& x : r.repairs) {
        Value v = Value::object(); v.set("kind", str(x.kind)); v.set("subject", str(x.subject)); v.set("detail", str(x.detail)); v.set("cell", cell_value(x.cell));
        if (x.from.valid()) v.set("from", cell_value(x.from));
        v.set("distanceCells", num(x.distance)); repairs.items.push_back(v);
    }
    doc.set("repairs", repairs);
    Value checks = array();
    for (const auto& h : r.host_checks) { Value v = Value::object(); v.set("check", str(h.check)); v.set("subject", str(h.subject)); v.set("expected", str(h.expected)); v.set("status", str("pending")); checks.items.push_back(v); }
    doc.set("hostChecks", checks);
    return cjson::canonical(doc);
}

std::string ports_document(const Input& input, const Result& r, const Identity& id) {
    Value doc = Value::object();
    doc.set("schema", str("mnbl.ports")); doc.set("version", integer(1)); doc.set("engineValidation", str("pending"));
    doc.set("inputs", inputs_value(id));
    Value ports = array();
    for (const auto& p : r.ports) {
        Value v = Value::object(); v.set("town", str(p.town_id)); v.set("townUid", str(p.town_uid)); v.set("valid", boolean(p.valid));
        // A forced port (a town repair): the village quota synthesizes and pins its harbour village on a coastal land cell next to it.
        if (p.forced) v.set("forced", boolean(true));
        if (p.valid) v.set("chosen", candidate_value(p.chosen, input.grid.spacing));
        Value cs = array(); for (const auto& c : p.candidates) cs.items.push_back(candidate_value(c, input.grid.spacing));
        v.set("candidates", cs); ports.items.push_back(v);
    }
    doc.set("ports", ports);
    // Towns on an open coast without a producer harbour record get no port: a shore is not a port.
    const Cells cells(input.grid.cols, input.grid.rows, input.grid.spacing);
    std::set<std::string> with_port;
    for (const auto& p : r.ports) with_port.insert(p.town_id);
    std::vector<std::string> coastal;
    for (const auto& pl : r.placements) {
        if (pl.kind != "town" || !pl.cell.valid() || with_port.count(pl.id)) continue;
        for (const auto& nb : cells.neighbours(pl.cell)) {
            const int b = r.basin_of[cells.index(nb)];
            if (b >= 0 && r.basins[static_cast<std::size_t>(b)].open) { coastal.push_back(pl.id); break; }
        }
    }
    doc.set("coastalTownsWithoutHarbourRecord", strings(coastal));
    return cjson::canonical(doc);
}

Run run(const source::SourceWorld& world, const fs::path& world_json, const Policies& p) {
    const auto proj_policy = projection::load_policy(p.projection);
    const projection::Projection proj(proj_policy, projection::frame_of(world));
    const auto materials = terrain::load_materials(p.materials);
    const auto import = terrain::load_import(p.import);
    const auto policy = load_policy(p.playability);
    check_face_table(policy, materials);
    const auto t = terrain::compile(terrain::layers_of(world, materials.permanent_white.snow_threshold), proj, materials, import, policy.side);
    const auto content = campaign::load_content(p.content);
    const auto cultures = campaign::load_cultures(p.cultures);
    const auto population = policy::load_population(p.population);
    auto c = campaign::compile(world, content, cultures, cjson::sha256_file_hex(world_json));
    Input in = input_of(world, proj, t, c);
    Run out;
    out.result = analyze(in, policy);
    // The exclusions must keep the campaign records closed (campaign::exclude re-checks closure).
    for (const auto& e : out.result.exclusions) campaign::exclude(c, e.id, e.reason);
    // A kept harbour village whose producer sea node resolves to no navigable cell (sea_node_not_navigable) loses its harbour role: it has
    // no harbour cell for its port, and activation refuses a harbour village without one (SettlementFactory). Seed 60. So does one whose
    // harbour cell lies on a closed basin (closed_basin): activation lands only on open basins and refuses the village's port otherwise
    // (NavalContentFactory landings, quality.naval_port; 11 of the 17 other accepted worlds of the 2026-10-19 seed sweep). Its town's port
    // record keeps its other harbour villages.
    std::set<std::string> no_harbour;
    for (const auto& port : out.result.ports)
        for (const auto& cand : port.candidates)
            if (cand.kind.empty() && ((!cand.harbour_cell.valid() && cand.reason == "sea_node_not_navigable") || cand.reason == "closed_basin")) no_harbour.insert(cand.village_id);
    if (!no_harbour.empty()) {
        for (auto& v : c.villages) if (no_harbour.count(v.id)) { v.harbour = false; v.sea_node.reset(); v.harbour_quality = 0; }
        for (auto& record : c.ports) {
            record.village_ids.erase(std::remove_if(record.village_ids.begin(), record.village_ids.end(), [&](const std::string& id) { return no_harbour.count(id) != 0; }), record.village_ids.end());
            record.sea_nodes.clear(); record.best_quality = 0;
            for (const auto& id : record.village_ids)
                for (const auto& v : c.villages)
                    if (v.id == id) { if (v.sea_node) record.sea_nodes.push_back(*v.sea_node); record.best_quality = std::max(record.best_quality, v.harbour_quality); }
        }
    }
    // A town whose port is invalid here loses its port record (ports.json keeps the reason). A forced port (a town repair) of a town
    // without a port record gets one, marked forced, and the town is a port; the village quota gives it its harbour village.
    {
        std::map<std::string, const Port*> travel_port;
        for (const auto& port : out.result.ports) if (port.valid) travel_port[port.town_id] = &port;
        for (auto it = c.ports.begin(); it != c.ports.end();) {
            if (!travel_port.count(it->town_id)) {
                for (auto& town : c.towns) if (town.id == it->town_id) town.port = false;
                it = c.ports.erase(it);
                continue;
            }
            it->forced = travel_port.at(it->town_id)->forced;
            ++it;
        }
        for (const auto& [town_id, port] : travel_port) {
            if (port->chosen.kind != "coast" || std::any_of(c.ports.begin(), c.ports.end(), [&](const campaign::Port& p) { return p.town_id == town_id; })) continue;
            campaign::Port record;
            record.town_id = town_id;
            record.forced = true;
            record.slots = {{"portSite", "pending", "MMR-28", ""}, {"navalFacility", "pending", "MMR-35", ""}, {"shipyardAndFleet", "pending", "MMR-35", ""}};
            for (auto& town : c.towns) if (town.id == town_id) town.port = true;
            c.ports.push_back(record);
        }
    }
    // The village quota (MMR-252) on an accepted world: exactly 3 villages per town and 2 per castle, with its second analysis.
    if (out.result.status == "ok") village_quota(c, in, out.result, policy, content);
    // The road network (MMR-253) on the final settlements: feeders to every castle, village and road-less town, and the road cells.
    {
        std::map<std::string, std::string> castle_towns;
        for (const auto& k : c.castles) castle_towns[k.id] = k.garrison_town_id;
        lay_roads(in, out.result, policy, castle_towns);
    }
    // The population pass (MMR-279, population.cpp) on the final binding: kingdoms, houses, companies, outlaw clans, wanderers, census.
    if (out.result.status == "ok") population::plan(c, world, population::geometry_of(in, out.result), population, content, cultures);
    // Resolve the slots MMR-27 left to MMR-28: settlement positions and port sites.
    auto resolve = [](std::vector<campaign::Slot>& slots, const std::string& name, const std::string& value) {
        for (auto& slot : slots) if (slot.name == name && slot.owner == "MMR-28") { slot.status = "resolved"; slot.value = value; }
    };
    auto xy = [](Point p) { return cjson::ecmascript_number(p.x) + "," + cjson::ecmascript_number(p.y); };
    const Cells grid_cells(in.grid.cols, in.grid.rows, in.grid.spacing);
    std::map<std::string, const Placement*> placed;
    for (const auto& pl : out.result.placements) if (pl.cell.valid()) placed[pl.id] = &pl;
    for (auto& town : c.towns) if (placed.count(town.id)) resolve(town.slots, "position", xy(placed.at(town.id)->point));
    for (auto& v : c.villages) if (placed.count(v.id)) resolve(v.slots, "position", xy(placed.at(v.id)->point));
    for (auto& k : c.castles) if (placed.count(k.id)) resolve(k.slots, "position", xy(placed.at(k.id)->point));
    for (auto& record : c.ports)
        for (const auto& port : out.result.ports) {
            if (!port.valid || port.town_id != record.town_id) continue;
            const auto& chosen = port.chosen;
            resolve(record.slots, "portSite", "port " + xy(grid_cells.centre(chosen.sea_cell)) + "; land entrance " + xy(grid_cells.centre(chosen.land_cell)) +
                                              (chosen.kind == "coast" ? std::string("; forced port (town repair)") : std::string()) +
                                              (chosen.village_id.empty() ? std::string() : "; harbour village " + chosen.village_id));
        }
    if (const auto problems = campaign::closure_problems(c); !problems.empty()) refuse("broken_join", "Travel results break the campaign records: " + problems.front());
    // Every FWGC++ city is one town at every stage (owner decision 9): the generation driver, --compile-travel and --bundle all run this.
    check_preservation(world, c, out.result);
    auto& id = out.identity;
    id.world_sha256 = c.world_sha256; id.source_identity = c.source_identity; id.projection_sha256 = proj.identity();
    id.masks_sha256 = cjson::sha256_hex(terrain::masks_bytes(t)); id.heightfield_sha256 = cjson::sha256_hex(terrain::heightfield_bytes(t));
    id.id_map_sha256 = cjson::sha256_hex(campaign::id_map_document(c));
    id.policy_sha256 = policy.sha256; id.calibration_sha256 = policy.calibration.sha256; id.side = t.side;
    id.column_offset = static_cast<int>(std::lround(proj.strip() / t.spacing)); id.row_offset = static_cast<int>(std::lround(proj.pole_margin() / t.spacing));
    out.graph = graph_document(in, out.result, id);
    out.ports = ports_document(in, out.result, id);
    out.cells = cells_bytes(in, out.result);
    out.campaign = campaign::records_document(c, content, cultures);
    auto count_pending = [&](const std::vector<campaign::Slot>& slots) { for (const auto& slot : slots) if (slot.status == "pending") ++out.pending_slots[slot.owner]; };
    for (const auto& x : c.kingdoms) count_pending(x.slots);
    for (const auto& x : c.houses) count_pending(x.slots);
    for (const auto& x : c.companies) count_pending(x.slots);
    for (const auto& x : c.outlaws) count_pending(x.slots);
    for (const auto& x : c.heroes) count_pending(x.slots);
    for (const auto& x : c.towns) count_pending(x.slots);
    for (const auto& x : c.villages) count_pending(x.slots);
    for (const auto& x : c.castles) count_pending(x.slots);
    for (const auto& x : c.ports) count_pending(x.slots);
    out.compiled = {{"terrain/heightfield.bin", terrain::heightfield_bytes(t)}, {"terrain/masks.bin", terrain::masks_bytes(t)}, {"terrain/weights.bin", terrain::weights_bytes(t)},
                    {"terrain/height-q16.bin", terrain::q16_bytes(t)}, {"terrain/projection.json", proj.document()},
                    {"campaign/id-map.json", campaign::id_map_document(c)}, {"campaign/campaign.json", out.campaign},
                    {"travel/travel-graph.json", out.graph}, {"travel/ports.json", out.ports}, {"travel/cells.bin", out.cells}};
    return out;
}

int compile_travel_main(int argc, char** argv) {
    const char* usage = "Usage: worldbridge --compile-travel SPLIT --contract FILE --projection-policy FILE --materials FILE --import FILE --content FILE --cultures FILE --playability FILE --population FILE --out NEW_DIRECTORY";
    if (argc != 21) throw std::runtime_error(usage);
    std::map<std::string, std::string> o;
    for (int i = 3; i + 1 < argc; i += 2) {
        const std::string key = argv[i];
        static const std::set<std::string> known{"--contract", "--projection-policy", "--materials", "--import", "--content", "--cultures", "--playability", "--population", "--out"};
        if (!known.count(key)) throw std::runtime_error("Unknown option " + key + "\n" + usage);
        if (!o.emplace(key, argv[i + 1]).second) throw std::runtime_error("Repeated option " + key);
    }
    const fs::path output = fs::absolute(o["--out"]);
    fs::path pending = output; pending += ".partial";
    if (fs::exists(output)) throw std::runtime_error("Output must not already exist");
    if (fs::exists(pending)) throw std::runtime_error("A previous incomplete output exists: " + pending.string());
    const fs::path split = argv[2];
    const auto world = source::read_source(split, source::load_compatibility(o["--contract"]));
    const Policies policies{o["--projection-policy"], o["--materials"], o["--import"], o["--content"], o["--cultures"], o["--playability"], o["--population"]};
    const auto r = run(world, split / "world.json", policies);
    fs::create_directories(pending);
    write(pending / "travel-graph.json", r.graph);
    write(pending / "ports.json", r.ports);
    write(pending / "cells.bin", r.cells);
    write(pending / "campaign.json", r.campaign);
    fs::rename(pending, output);
    Value report = Value::object();
    report.set("status", str(r.result.status)); report.set("reasons", reasons_value(r.result.reasons));
    report.set("travelGraphSha256", str(cjson::sha256_hex(r.graph))); report.set("portsSha256", str(cjson::sha256_hex(r.ports))); report.set("campaignSha256", str(cjson::sha256_hex(r.campaign)));
    std::size_t valid_ports = 0; for (const auto& p : r.result.ports) valid_ports += p.valid;
    report.set("landComponents", integer(static_cast<long long>(r.result.lands.size()))); report.set("groups", integer(static_cast<long long>(r.result.groups.size())));
    report.set("basins", integer(static_cast<long long>(r.result.basins.size()))); report.set("validPorts", integer(static_cast<long long>(valid_ports)));
    report.set("exclusions", integer(static_cast<long long>(r.result.exclusions.size()))); report.set("repairs", integer(static_cast<long long>(r.result.repairs.size())));
    std::cout << cjson::canonical(report) << std::endl;
    return r.result.status == "ok" ? 0 : 3;
}

}  // namespace worldbridge::travel
