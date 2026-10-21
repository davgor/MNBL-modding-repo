// Static travel graph tests (MMR-28): travel_graph_tests PLAYABILITY_POLICY WORK_DIR
// Cell classification rules, policy strictness, and every topology outcome on small cell maps:
//   '.' land  '^' mountain  '~' sea  'L' lake  'R' river      (settlements and nodes sit at cell centres; every sample is grassland)
// two islands with ports, an island without a port, closed basins (lake-only, too small), river bridge candidates
// (with and without the producer's report), road passes and their bound, footprints with access passes, snaps and
// unplaceable towns, town repairs (town snaps, carves, mountain joins, forced ports, moves) and city preservation, the village quota
// (pins, keeps, castle takes, drops, protected classes, synthesis, clearance, notables, forced-port harbour villages, determinism),
// village and castle exclusions and their bounds, harbour shifts, seam wrap, OpenSea, pole passages, determinism, and the road network
// (route pieces and banks, decks, feeders, seam, pole, maxCells; MMR-253; gate spurs, a drawn road at every gate: MMR-247-R), and the land
// face types and cells.bin v2 (MMR-254).
#include <algorithm>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iostream>
#include <map>
#include <set>
#include <sstream>
#include <string>
#include "worldbridge/travel_graph.hpp"

namespace fs = std::filesystem;
using namespace worldbridge;
using namespace worldbridge::travel;

namespace {
int checks = 0, failures = 0;
fs::path policy_file, work;
constexpr double kSpacing = 6.25;

void check(bool ok, const std::string& what) {
    ++checks;
    if (ok) std::cout << "ok    " << what << "\n";
    else { ++failures; std::cout << "FAIL  " << what << "\n"; }
}
void refused(const std::string& what, const std::string& code, const std::function<void()>& body) {
    try { body(); check(false, what + ": accepted, expected " + code); }
    catch (const json::Error& e) { check(e.code == code, what + " -> " + e.code + (e.code == code ? "" : " (wanted " + code + "): " + e.message)); }
    catch (const std::exception& e) { check(false, what + ": unexpected " + std::string(e.what())); }
}
std::string slurp(const fs::path& p) { std::ifstream in(p, std::ios::binary); std::ostringstream s; s << in.rdbuf(); return s.str(); }
void spit(const fs::path& p, const std::string& text) { fs::create_directories(p.parent_path()); std::ofstream out(p, std::ios::binary | std::ios::trunc); out << text; }

struct World { Input in; std::vector<std::uint8_t> cls; };
World map(const std::vector<std::string>& rows) {
    World w;
    w.in.grid.rows = static_cast<int>(rows.size());
    w.in.grid.cols = static_cast<int>(rows[0].size());
    w.in.grid.spacing = kSpacing;
    w.in.grid.z.assign(static_cast<std::size_t>(w.in.grid.rows + 1) * w.in.grid.cols, 1.0);
    w.in.grid.intent.assign(w.in.grid.z.size(), 0);
    w.in.grid.biome.assign(w.in.grid.z.size(), 3);
    w.in.grid.white.assign(w.in.grid.z.size(), 0);
    w.in.biome_core = {{0, "ocean"}, {2, "desert"}, {3, "grassland"}, {4, "forest"}, {8, "lake"}, {13, "marsh"}, {15, "boreal_forest"}, {16, "cold_tundra"}, {17, "land_ice"}};
    for (const auto& row : rows)
        for (char ch : row) w.cls.push_back(ch == '.' ? Land : ch == '^' ? Mountain : ch == '~' ? Sea : ch == 'L' ? Lake : River);
    return w;
}
Point at(int r, int c) { return {(c + 0.5) * kSpacing, (r + 0.5) * kSpacing}; }
void town(World& w, const std::string& id, int r, int c) { w.in.settlements.push_back({id, "uid-" + id, "town", "", at(r, c)}); }
void village(World& w, const std::string& id, const std::string& town_id, int r, int c) { w.in.settlements.push_back({id, "uid-" + id, "village", town_id, at(r, c)}); }
void castle(World& w, const std::string& id, int r, int c) { w.in.settlements.push_back({id, "uid-" + id, "castle", "", at(r, c)}); }
void harbour(World& w, const std::string& village_id, const std::string& town_id, int r, int c, double quality = 0.5) {
    w.in.harbours.push_back({village_id, "uid-" + village_id, town_id, 0, at(r, c), quality});
}
void road(World& w, const std::string& from, const std::string& to, const std::vector<std::vector<std::pair<int, int>>>& pieces, std::set<std::pair<int, int>> producer = {}) {
    Road rd;
    rd.index = static_cast<int>(w.in.roads.size());
    rd.from_town_id = from; rd.to_town_id = to; rd.from_uid = "uid-" + from; rd.to_uid = "uid-" + to;
    for (const auto& piece : pieces) { std::vector<Point> pts; for (const auto& [r, c] : piece) pts.push_back(at(r, c)); rd.pieces.push_back(pts); }
    rd.crossing_steps = std::move(producer);
    w.in.roads.push_back(rd);
}
Result run(const World& w, const Policy& p) { return analyze_cells(w.in, p, w.cls); }
bool has_reason(const Result& r, const std::string& code, const std::string& uid = "") {
    for (const auto& x : r.reasons)
        if (x.code == code && (uid.empty() || std::find(x.uids.begin(), x.uids.end(), uid) != x.uids.end())) return true;
    return false;
}
const Placement* placement(const Result& r, const std::string& id) { for (const auto& p : r.placements) if (p.id == id) return &p; return nullptr; }
const Port* port(const Result& r, const std::string& town) { for (const auto& p : r.ports) if (p.town_id == town) return &p; return nullptr; }
bool excluded(const Result& r, const std::string& id, const std::string& reason_part = "") {
    for (const auto& e : r.exclusions) if (e.id == id && e.reason.find(reason_part) != std::string::npos) return true;
    return false;
}
std::size_t repairs(const Result& r, const std::string& kind) { std::size_t n = 0; for (const auto& x : r.repairs) n += x.kind == kind; return n; }
std::size_t checks_of(const Result& r, const std::string& kind) { std::size_t n = 0; for (const auto& h : r.host_checks) n += h.check == kind; return n; }

const std::vector<std::string> kTwoIslands = {
    "~~~~~~~~~~~~~~~~",
    "~....~~~~~~....~",
    "~....~~~~~~....~",
    "~~~~~~~~~~~~~~~~"};

void classification_tests(const Policy& policy) {
    auto grid = [] { Grid g; g.cols = 4; g.rows = 2; g.spacing = kSpacing; g.z.assign(12, 1.0); g.intent.assign(12, 0); return g; };
    auto set = [](Grid& g, int r, int c, double z, std::uint8_t intent = 0) { g.z[static_cast<std::size_t>(r) * g.cols + c] = z; g.intent[static_cast<std::size_t>(r) * g.cols + c] = intent; };
    Grid g = grid();
    check(classify(g, policy) == std::vector<std::uint8_t>(8, Land), "flat ground above the datum is land");
    g = grid(); set(g, 0, 0, -1); set(g, 1, 1, -1); set(g, 0, 1, 5);
    check(classify(g, policy)[0] == Sea, "sea: the main-diagonal centre is below the datum, whatever the other corners");
    g = grid(); set(g, 0, 0, -1); set(g, 1, 1, 0.5);
    check(classify(g, policy)[0] == Sea, "sea when the diagonal mean is negative even with one corner above the datum");
    g = grid(); set(g, 0, 1, -1);
    check(classify(g, policy)[0] == Land, "an off-diagonal low corner does not make a sea cell");
    g = grid(); set(g, 1, 1, 1, 2);
    check(classify(g, policy)[0] == Lake && classify(g, policy)[5] == Lake, "lake: a main-diagonal corner carries lake intent (both cells sharing it)");
    g = grid(); set(g, 0, 0, 1, 3);
    check(classify(g, policy)[0] == River, "river: a main-diagonal corner carries river intent");
    g = grid(); set(g, 0, 0, -2, 2); set(g, 1, 1, -2);
    check(classify(g, policy)[0] == Sea, "below-datum lake corners still give sea (the datum rule comes first)");
    g = grid(); set(g, 0, 1, 1 + 1.01 * policy.mountain_slope * kSpacing);
    check(classify(g, policy)[0] == Mountain, "mountain: (max - min corner) / spacing above mountainSlope");
    g = grid(); set(g, 0, 1, 1 + 0.99 * policy.mountain_slope * kSpacing);
    check(classify(g, policy)[0] == Land, "slope at or below mountainSlope stays land");
    g = grid(); g.z.pop_back();
    refused("a grid with the wrong sample count", "grid_mismatch", [&] { classify(g, policy); });
}

void policy_tests(const Policy& policy) {
    check(policy.calibration.invalid.at("Default").count(18) && policy.calibration.invalid.at("All").count(7) && policy.calibration.land_to_sea == 50 &&
          policy.calibration.embark_threshold == 0.5 && policy.calibration.evidence_sha256.size() == 64 && policy.calibration.port_gate_envelope_max == 10.97,
          "the calibration carries the MMR-17 invalid-terrain sets, region switch costs, embark threshold and the installed port-gate envelope");
    check(policy.side == 257 && policy.mountain_slope == 0.5 && policy.open_sea_cells == 4 && policy.max_access_cells == 6 && policy.bridge_kinds.size() == 2 && policy.max_bridge_cells == 2 && policy.max_port_gate_cells == 2 && policy.sha256.size() == 64, "the policy loads with its pinned values");
    const fs::path calibration = policy_file.parent_path() / ".." / "calibration" / "naval-transitions" / "naval-transitions.json";
    spit(work / "calibration" / "naval-transitions" / "naval-transitions.json", slurp(calibration));
    const std::string text = slurp(policy_file);
    auto mutated = [&](const std::string& name, const std::string& from, const std::string& to, const std::string& code) {
        const auto at_ = text.find(from);
        if (at_ == std::string::npos) { check(false, name + ": text not found"); return; }
        std::string copy = text; copy.replace(at_, from.size(), to);
        const auto file = work / "policies" / (name + ".json");
        spit(file, copy);
        refused("policy " + name, code, [&] { load_policy(file); });
    };
    spit(work / "policies" / "same.json", text);
    check(load_policy(work / "policies" / "same.json").sha256 == policy.sha256, "a byte-identical copy with its calibration loads with the same hash");
    mutated("land_as_sea", "\"land\": \"Plain\"", "\"land\": \"CoastalSea\"", "face_group_mismatch");
    mutated("mountain_walkable", "\"mountain\": \"Mountain\"", "\"mountain\": \"Plain\"", "face_group_mismatch");
    mutated("unknown_face_group", "\"lake\": \"Lake\"", "\"lake\": \"Dune\"", "unmapped_category");
    mutated("calibration_altered", "9dd9604566d15394dcb644712c26b508e25f3f97d47e38860c5aaf0bb60030e3", std::string(64, '0'), "calibration_mismatch");
    mutated("unknown_member", "\"version\": 1,", "\"version\": 1, \"extra\": 1,", "unknown_field");
    mutated("bridge_kind", "\"river\",", "\"ford\",", "unmapped_category");
    mutated("port_gate_zero", "\"maxPortGateCells\": 2", "\"maxPortGateCells\": 0", "out_of_range");
    mutated("fraction_range", "\"maxPassFraction\": 0.1", "\"maxPassFraction\": 1.5", "out_of_range");
    // Map epic 2 keys (MMR-248): the biome face table, the town repairs, the village quota and the road rules.
    check(policy.land_face_groups.at("desert") == "Desert" && policy.land_face_groups.at("cold_tundra") == "Steppe" && policy.land_face_groups.at("land_ice") == "Snow" &&
          policy.land_face_groups.at("marsh") == "Swamp" && policy.land_face_groups.count("ocean") == 0 && policy.land_face_default == "Plain" && policy.permanent_white_months == 12 &&
          policy.permanent_white_face == "Snow", "cells.landFaceGroups: the shared table's face per land biome, Plain by default, Snow for permanent white");
    check(policy.max_town_snap_cells >= policy.max_snap_cells && policy.town_carve && policy.forced_ports && policy.villages.town == 3 && policy.villages.castle == 2 &&
          policy.villages.clearance == 4 && policy.villages.tiers.size() == 3 && policy.villages.uid_prefix == "mnbl-quota-village:" && policy.roads.feeder_water == "decksOnly" &&
          policy.roads.feeder_from.size() == 3 && policy.roads.cell_flag, "town repairs, the 3/2 village quota with its 4-unit clearance, and the road rules load");
    mutated("land_face_dune", "\"desert\": \"Desert\"", "\"desert\": \"Dune\"", "unmapped_category");
    mutated("land_face_sea", "\"forest\": \"Forest\"", "\"forest\": \"CoastalSea\"", "face_group_mismatch");
    mutated("land_face_unknown_member", "\"default\": \"Plain\",", "\"default\": \"Plain\", \"extra\": 1,", "unknown_field");
    mutated("village_unknown_member", "\"townVillages\": 3,", "\"townVillages\": 3, \"extra\": 1,", "unknown_field");
    mutated("village_tier_range", "\"maxSpacing\": 0.65", "\"maxSpacing\": 1.65", "out_of_range");
    mutated("feeder_new_crossings", "\"water\": \"decksOnly\"", "\"water\": \"anywhere\"", "out_of_range");
    mutated("road_unknown_member", "\"cellFlag\": true,", "\"cellFlag\": true, \"extra\": 1,", "unknown_field");
    mutated("town_snap_below_snap", "\"maxTownSnapCells\": 6", "\"maxTownSnapCells\": 1", "out_of_range");
}

void topology_tests(const Policy& strict) {
    Policy lenient = strict;
    lenient.max_pass_fraction = lenient.max_footprint_fraction = lenient.max_snap_fraction = 1;
    lenient.max_excluded_village_fraction = lenient.max_excluded_castle_fraction = 1;

    // Two islands, each town with a harbour village: both groups are player-safe.
    World a = map(kTwoIslands);
    town(a, "t1", 1, 2); town(a, "t2", 1, 12);
    village(a, "v1", "t1", 2, 2); village(a, "v2", "t2", 2, 12);
    harbour(a, "v1", "t1", 3, 2); harbour(a, "v2", "t2", 3, 12);
    const Result ra = run(a, strict);
    check(ra.status == "ok" && ra.lands.size() == 2 && ra.groups.size() == 2 && ra.basins.size() == 1 && ra.basins[0].open, "two islands: two land components, one open basin, status ok");
    const Port* p1 = port(ra, "t1");
    check(p1 && p1->valid && p1->chosen.sea_cell == Cell{0, 2} && p1->chosen.land_cell == Cell{1, 2} && p1->chosen.harbour_cell == Cell{3, 2} && p1->chosen.harbour_shift == 0 &&
          p1->chosen.gate_distance == 1 && p1->chosen.entrance_distance == 0, "the port serves the gate: the coast pair of the gate's component nearest the gate, on the harbour's basin");
    check(ra.groups[0].safe && ra.groups[1].safe && ra.main_group == placement(ra, "t1")->group, "both groups are player-safe; the main group holds the smallest town UID on a tie");
    std::size_t port_t = 0, coast_t = 0;
    for (const auto& t : ra.transitions) { port_t += t.kind == "port"; coast_t += t.kind == "coast"; }
    check(port_t == 2 && coast_t == 2, "transitions: one per port and one coast edge per group and basin, all pending engine validation");
    check(checks_of(ra, "portFace") == 2 && checks_of(ra, "portGate") == 2 && checks_of(ra, "gateFace") == 2 && checks_of(ra, "settlementFace") == 2 && checks_of(ra, "villageRoute") == 2 &&
          checks_of(ra, "seaRoute") == 1 && checks_of(ra, "islands") == 1, "host checks cover gates, village faces and routes, ports and their gate distance, the sea route and islands");
    check(placement(ra, "t1")->point.x == at(1, 2).x && placement(ra, "t1")->point.y == at(1, 2).y && ra.collisions.empty(), "a settlement at its cell centre keeps that point; no collisions");

    // An island town without a harbour record can be reached by sea but never left. Town repairs: with a kept village on its island it
    // takes a coast port; without one it moves to the nearest free land cell of the main group. No town is dropped.
    World b = map(kTwoIslands);
    town(b, "t1", 1, 2); town(b, "t2", 1, 12);
    village(b, "v1", "t1", 2, 2); harbour(b, "v1", "t1", 3, 2);
    const Result rb = run(b, strict);
    check(rb.status == "ok" && placement(rb, "t2")->method == "move" && placement(rb, "t2")->group == placement(rb, "t1")->group && repairs(rb, "move") == 1 &&
          placement(rb, "t2")->cell == Cell{1, 1} && !has_reason(rb, "quality.unreachable_town"),
          "an island town without a port or a kept village on its island moves to the nearest free land cell of the main group (a recorded move)");
    check(!port(rb, "t2"), "a shore without a harbour record is never a harbour port");
    World b2 = map(kTwoIslands);
    town(b2, "t1", 1, 2); town(b2, "t2", 1, 12);
    village(b2, "v1", "t1", 2, 2); village(b2, "v2", "t2", 2, 12); harbour(b2, "v1", "t1", 3, 2);
    const Result rb2 = run(b2, strict);
    const Port* coast = port(rb2, "t2");
    check(rb2.status == "ok" && placement(rb2, "t2")->method == "direct" && coast && coast->valid && coast->chosen.kind == "coast" && coast->chosen.village_id.empty() &&
          coast->chosen.gate_distance <= strict.max_port_gate_cells && repairs(rb2, "coast_port") == 1 && rb2.groups[static_cast<std::size_t>(placement(rb2, "t2")->group)].safe,
          "an island town without a port but with a kept village on its island takes a coast port (a recorded town repair) and its group becomes player-safe");
    check(port(rb2, "t1")->chosen.kind.empty() && repairs(ra, "coast_port") == 0 && repairs(ra, "move") == 0 && repairs(ra, "join") == 0, "player-safe worlds get no town repair");
    Identity coast_id; coast_id.world_sha256 = std::string(64, 'a'); coast_id.side = 257;
    const std::string coast_doc = ports_document(b2.in, rb2, coast_id);
    check(coast_doc.find("\"kind\":\"coast\"") != std::string::npos && ports_document(a.in, ra, coast_id).find("\"kind\"") == std::string::npos,
          "ports.json marks a coast port with kind coast; harbour candidates carry no kind");

    // Closed basins: a lake and a sea pocket below minOpenCells.
    World c = map({"................", "..LLL...........", "..LLL.....~~....", "................"});
    town(c, "t1", 0, 0); town(c, "t2", 0, 12);
    village(c, "v1", "t1", 3, 3); village(c, "v2", "t2", 3, 10);
    harbour(c, "v1", "t1", 2, 3); harbour(c, "v2", "t2", 2, 10);
    const Result rc = run(c, strict);
    check(rc.status == "ok" && rc.basins.size() == 2 && !rc.basins[0].open && !rc.basins[1].open, "a lake-only basin and a two-cell sea pocket are closed basins");
    check(port(rc, "t1") && !port(rc, "t1")->valid && port(rc, "t1")->candidates[0].reason == "closed_basin" && port(rc, "t2")->candidates[0].reason == "closed_basin",
          "harbours on closed basins are invalid ports (closed_basin)");

    // A river splits the land; a producer road across it is a bridge candidate that joins the groups.
    const std::vector<std::string> river = {"....R...R", "....R...R", "....R...R"};
    World d = map(river);
    town(d, "t1", 1, 1); town(d, "t2", 1, 6);
    road(d, "t1", "t2", {{{1, 1}, {1, 2}, {1, 3}, {1, 4}, {1, 5}, {1, 6}}}, {{0, 2}});
    const Result rd = run(d, strict);
    check(rd.status == "ok" && rd.lands.size() == 2 && rd.groups.size() == 1 && rd.required_bridges == 1, "a road across a river joins two land components through one required bridge");
    check(rd.roads[0].crossings.size() == 1 && rd.roads[0].crossings[0].kind == "river" && rd.roads[0].crossings[0].joins && rd.roads[0].crossings[0].producer &&
          rd.roads[0].same_group && checks_of(rd, "bridge") == 1, "the crossing is a producer-reported river bridge candidate with a host check");
    World d2 = map(river);
    town(d2, "t1", 1, 1); town(d2, "t2", 1, 6);
    const Result rd2 = run(d2, strict);
    check(rd2.status == "ok" && placement(rd2, "t2")->method == "move" && placement(rd2, "t2")->cell == Cell{1, 0} && rd2.groups.size() == 2,
          "without the road the far town is unreachable (no ports, no mountain to carve): it moves to the nearest free land cell of the main group");
    Policy no_rivers = strict; no_rivers.bridge_kinds = {"coast"};
    check(!run(d, no_rivers).roads[0].crossings[0].joins && placement(run(d, no_rivers), "t2")->method == "move", "a crossing kind the policy does not list joins nothing");
    // One deck per cell: a river cell between three land components carries the first joining crossing only.
    World d3 = map({"RRR.RRR", "R..R..R", "RRRRRRR"});
    town(d3, "t1", 1, 1); town(d3, "t2", 1, 5); town(d3, "t3", 0, 3);
    road(d3, "t1", "t2", {{{1, 1}, {1, 2}, {1, 3}, {1, 4}, {1, 5}}});
    road(d3, "t3", "t1", {{{0, 3}, {1, 3}, {1, 2}, {1, 1}}});
    const Result rd3 = run(d3, lenient);
    check(rd3.roads[0].crossings.size() == 1 && rd3.roads[0].crossings[0].joins && rd3.roads[1].crossings.size() == 1 && rd3.roads[1].crossings[0].cells == std::vector<Cell>{{1, 3}} &&
          !rd3.roads[1].crossings[0].joins && rd3.required_bridges == 1,
          "a crossing on a cell an earlier crossing already decks between two other components joins nothing (one deck per cell)");

    // A mountain ridge: a road over it becomes a pass, within the bound or not.
    const std::vector<std::string> ridge = {"....^....^", "....^....^", "....^....^"};
    World e = map(ridge);
    town(e, "t1", 1, 1); town(e, "t2", 1, 6);
    road(e, "t1", "t2", {{{1, 1}, {1, 6}}});
    Policy passes = strict; passes.max_pass_fraction = 0.5;
    const Result re = run(e, passes);
    check(re.status == "ok" && re.lands.size() == 1 && repairs(re, "pass") == 1 && re.roads[0].passes == std::vector<Cell>{{1, 4}} && checks_of(re, "pass") == 1,
          "a road over a ridge carves one pass (recorded, with a host check) and joins the land");
    check(run(e, strict).status == "rejected" && has_reason(run(e, strict), "quality.repair_bound_exceeded"), "one pass among 6 mountain cells exceeds maxPassFraction 0.1: rejected");
    Policy no_pass = passes; no_pass.road_passes = false;
    const Result re2 = run(e, no_pass);
    check(re2.roads[0].crossings[0].kind == "blocked" && re2.status == "ok" && repairs(re2, "join") == 1 && repairs(re2, "pass") == 0 && re2.cls[0 * 10 + 4] == Land &&
          checks_of(re2, "pass") == 1 && re2.lands.size() == 1 && placement(re2, "t2")->method == "direct",
          "with passes disabled the ridge blocks the road; the far town's group joins the main group through one carved mountain cell (a recorded join with a pass host check)");
    Policy no_join = no_pass; no_join.max_access_cells = 0;
    check(placement(run(e, no_join), "t2")->method == "move", "beyond maxAccessCells no join is carved and the town moves");

    // Placement: a footprint on a mountain, a snap from the sea, and an unplaceable town.
    World f = map({"~~~~~~~~", "~^.....~", "~......~", "~~~~~~~~"});
    f.in.settlements.push_back({"t1", "uid-t1", "town", "", at(1, 1)});
    f.in.settlements.push_back({"t2", "uid-t2", "town", "", at(0, 4)});
    const Result rf = run(f, lenient);
    check(placement(rf, "t1")->method == "footprint" && rf.cls[1 * 8 + 1] == Land && repairs(rf, "footprint") == 1, "a town on a mountain cell gets it as its footprint");
    check(placement(rf, "t2")->method == "snap" && placement(rf, "t2")->cell == Cell{1, 4} && placement(rf, "t2")->shift == 1 && repairs(rf, "snap") == 1 && rf.status == "ok",
          "a town on a sea cell snaps to the nearest land cell within maxSnapCells");
    check(run(f, strict).status == "ok", "a town's footprint and snap are town repairs outside maxFootprintFraction and maxSnapFraction");
    World fk = map({"~~~~~~~~", "~^.....~", "~......~", "~~~~~~~~"});
    town(fk, "t1", 1, 4); castle(fk, "k1", 1, 1); village(fk, "v1", "t1", 0, 5);
    const Result rfk = run(fk, strict);
    check(rfk.status == "rejected" && has_reason(rfk, "quality.repair_bound_exceeded", "uid-k1") && has_reason(rfk, "quality.repair_bound_exceeded", "uid-v1") &&
          placement(rfk, "k1")->method == "footprint" && placement(rfk, "v1")->method == "snap",
          "a castle's footprint and a village's snap beyond their fractions reject the world, naming them");
    World fa = map({"~~~~~~~~~~", "~^^^......", "~^^^......", "~~~~~~~~~~"});
    town(fa, "t1", 1, 1); town(fa, "t2", 1, 7);
    const Result rfa = run(fa, lenient);
    check(rfa.status == "ok" && repairs(rfa, "access") == 2 && placement(rfa, "t1")->land == placement(rfa, "t2")->land && rfa.cls[1 * 10 + 2] == Land && rfa.cls[1 * 10 + 3] == Land,
          "a footprint walled in by mountains gets a two-cell access pass to the nearest land");
    check(run(fa, strict).status == "ok", "a town's footprint and its access passes (2 of 6 mountain cells) sit outside maxFootprintFraction and maxPassFraction");
    Policy short_access = lenient; short_access.max_access_cells = 1;
    const Result rfb = run(fa, short_access);
    check(rfb.status == "rejected" && repairs(rfb, "access") == 0 && has_reason(rfb, "quality.unreachable_town") && placement(rfb, "t1")->land != placement(rfb, "t2")->land,
          "beyond maxAccessCells the footprint stays walled in, the two towns are split and the world is rejected");
    World f2 = map({"~~~~~~~~~~~~", "~~~~~~~~~~~~", "~~~~~~~~~~~~", "~~~~~~~~~~~~", "~~~~~~~~~~~~", "..~~~~~~~~~~"});
    town(f2, "t1", 5, 0); town(f2, "t2", 0, 8);
    const Result rf2 = run(f2, lenient);
    check(rf2.status == "ok" && placement(rf2, "t2")->method == "move" && placement(rf2, "t2")->cell == Cell{5, 1} && !has_reason(rf2, "quality.unplaceable_town"),
          "a town with no land within maxSnapCells moves to the nearest free land cell of a player-safe group");
    World f3 = map({"~~~~~~~~~~~~", "~~~~~~~~~~~~", "~~~~~~~~~~~~", "~~~~~~~~~~~~", "~~~~~~~~~~~~", ".~~~~~~~~~~~"});
    town(f3, "t1", 5, 0); town(f3, "t2", 0, 8);
    const Result rf3 = run(f3, lenient);
    check(rf3.status == "rejected" && has_reason(rf3, "quality.unplaceable_town", "uid-t2") && placement(rf3, "t2")->method == "unplaceable",
          "a town with no land within reach and no free land cell to move to is unplaceable: rejected");

    // A village on the other island is excluded; too many exclusions reject the world.
    World g = map(kTwoIslands);
    town(g, "t1", 1, 2); town(g, "t2", 1, 12);
    village(g, "v1", "t1", 2, 2); village(g, "v2", "t2", 2, 12); village(g, "vx", "t1", 2, 13);
    harbour(g, "v1", "t1", 3, 2); harbour(g, "v2", "t2", 3, 12);
    const Result rg = run(g, lenient);
    check(rg.status == "ok" && excluded(rg, "vx", "not land-reachable"), "a village outside its town's land group is excluded with the reason");
    check(has_reason(run(g, strict), "quality.too_many_exclusions", "uid-vx"), "1 of 3 villages excluded exceeds maxExcludedVillageFraction 0.1: rejected, naming the UID");

    // A village beyond the engine trade distance (3 x the town spacing) of every town and castle of its land group is excluded.
    World gt = map({std::string(40, '.'), std::string(40, '.')});
    town(gt, "t1", 0, 0); town(gt, "t2", 0, 2); village(gt, "vn", "t1", 1, 4); village(gt, "vf", "t1", 1, 20);
    const Result rgt = run(gt, lenient);
    check(rgt.status == "ok" && excluded(rgt, "vf", "beyond the engine trade distance") && !excluded(rgt, "vn"),
          "a village 20 cells from every town of its group, with towns 2 cells apart (limit 6 cells), is excluded; one 4 cells away is kept");

    // A castle on an island without a port is not player-safe.
    World h = map({"~~~~~~~~~~~~~~~~", "~....~~~~~~..~~~", "~....~~~~~~..~~~", "~~~~~~~~~~~~~~~~"});
    town(h, "t1", 1, 2); village(h, "v1", "t1", 2, 2); harbour(h, "v1", "t1", 3, 2);
    castle(h, "k1", 1, 11); castle(h, "k2", 1, 3);
    const Result rh = run(h, lenient);
    check(rh.status == "ok" && excluded(rh, "k1", "not player-safe") && !excluded(rh, "k2"), "a castle on a portless island is excluded; one on the main island is kept");
    check(has_reason(run(h, strict), "quality.too_many_exclusions", "uid-k1"), "1 of 2 castles excluded exceeds maxExcludedCastleFraction 0.25: rejected");

    // Harbour shifts: a sea node on land shifts to the nearest sea cell; too far is invalid.
    World i = map(kTwoIslands);
    town(i, "t1", 1, 2); town(i, "t2", 1, 12);
    village(i, "v1", "t1", 2, 2); village(i, "v2", "t2", 2, 12);
    harbour(i, "v1", "t1", 1, 3); harbour(i, "v2", "t2", 3, 12);
    const Result ri = run(i, strict);
    check(port(ri, "t1")->valid && port(ri, "t1")->chosen.harbour_cell == Cell{0, 3} && port(ri, "t1")->chosen.harbour_shift == 1 && repairs(ri, "harbour_shift") == 1,
          "a sea node touching only land shifts one cell to the sea (recorded harbour_shift)");
    World i2 = map({"~~~~~~~~~~~~", "~..........~", "~..........~", "~..........~", "~..........~", "~..........~", "~~~~~~~~~~~~"});
    town(i2, "t1", 3, 3); village(i2, "v1", "t1", 3, 4); harbour(i2, "v1", "t1", 3, 5);
    const Result ri2 = run(i2, strict);
    check(ri2.status == "ok" && !port(ri2, "t1")->valid && port(ri2, "t1")->candidates[0].reason == "sea_node_not_navigable", "a sea node three cells inland is not navigable: invalid port");

    // The seam: land on both edges is one component.
    World j = map({"..~~~~~~~~~~~~..", "..~~~~~~~~~~~~.."});
    town(j, "t1", 0, 0); town(j, "t2", 0, 15);
    const Result rj = run(j, strict);
    check(rj.status == "ok" && rj.lands.size() == 1 && placement(rj, "t1")->land == placement(rj, "t2")->land, "land across the seam is one component (columns wrap)");
    check(Cells(16, 2, kSpacing).distance(Cell{0, 0}, Cell{0, 15}) == 1, "cell distance takes the nearest seam image");

    // OpenSea: sea cells at least openSeaCells (Chebyshev) from other classes.
    std::vector<std::string> ocean(12, std::string(12, '~'));
    ocean[0][0] = '.';
    World k = map(ocean);
    town(k, "t1", 0, 0);
    const Result rk = run(k, strict);
    std::size_t open_sea = 0; for (auto f_ : rk.open_sea) open_sea += f_;
    check(open_sea > 0 && rk.basins[0].open_sea == open_sea && rk.open_sea[0 * 12 + 3] == 0 && rk.open_sea[4 * 12 + 4] == 1, "OpenSea cells lie at least 4 cells from land; nearer sea is coastal");

    // A road through a pole passage is split into pieces.
    World l = map(river);
    town(l, "t1", 1, 1); town(l, "t2", 1, 6);
    road(l, "t1", "t2", {{{1, 1}, {1, 3}}, {{1, 5}, {1, 6}}});
    const Result rl = run(l, strict);
    check(rl.roads[0].pole_passages == 1 && rl.roads[0].crossings.empty() && placement(rl, "t2")->method == "move" && rl.roads[0].same_group,
          "a pole passage splits the road: the river the unsplit road would bridge is never crossed (the far town moves into the first town's group)");

    // Ports: a held pair, a port too far from the gate, a basin the gate cannot reach, excluded and unplaceable towns.
    World pa = map({"~~~~~~~~~~~~~~~~~~~~", "^^^^^.^^^^^^^^^^^^^^", "^^^^...^^^^^^^^^^^^^"});
    town(pa, "t1", 2, 5); town(pa, "t2", 2, 4); village(pa, "v1", "t1", 2, 6); village(pa, "v2", "t2", 1, 5);
    harbour(pa, "v1", "t1", 0, 5); harbour(pa, "v2", "t2", 0, 5);
    const Result rpa = run(pa, lenient);
    check(port(rpa, "t1")->valid && port(rpa, "t1")->chosen.sea_cell == Cell{0, 5} && !port(rpa, "t2")->valid && port(rpa, "t2")->candidates[0].reason == "port_pair_taken",
          "two towns never share a port pair: the second finds it held (port_pair_taken)");
    World pb = map({"~~~~~~~~~~~~", "~..........~", "~..........~", "~..........~", "~..........~", "~..........~", "~~~~~~~~~~~~"});
    town(pb, "t1", 3, 5); village(pb, "v1", "t1", 3, 4); harbour(pb, "v1", "t1", 0, 5);
    const Result rpb = run(pb, strict);
    check(rpb.status == "ok" && !port(rpb, "t1")->valid && port(rpb, "t1")->candidates[0].reason.rfind("port_too_far_from_gate", 0) == 0,
          "a coast three cells from the gate is beyond maxPortGateCells (port_too_far_from_gate)");
    World pc = map({"~~~~~~~~.^~~~~~^^^", "~~~~~~~~.^~~~~~^^^", "~~~~~~~~.^~~~~~^^^", "~~~~~~~~.^~~~~~^^^"});
    town(pc, "t1", 1, 8); village(pc, "v1", "t1", 2, 8); harbour(pc, "v1", "t1", 1, 12);
    const Result rpc = run(pc, strict);
    check(rpc.basins.size() == 2 && rpc.basins[1].open && !port(rpc, "t1")->valid && port(rpc, "t1")->candidates[0].reason == "basin_not_reachable_from_gate",
          "a harbour on an open basin the gate's component does not touch is invalid (basin_not_reachable_from_gate)");
    World pd = map(kTwoIslands);
    town(pd, "t1", 1, 2); town(pd, "t2", 1, 12); village(pd, "v1", "t1", 2, 2); village(pd, "v2", "t2", 2, 12); village(pd, "vx", "t1", 2, 13);
    harbour(pd, "v1", "t1", 3, 2); harbour(pd, "v2", "t2", 3, 12); harbour(pd, "vx", "t1", 3, 13);
    const Result rpd = run(pd, lenient);
    bool vx_excluded = false;
    for (const auto& c_ : port(rpd, "t1")->candidates) vx_excluded = vx_excluded || (c_.village_id == "vx" && c_.reason == "harbour_village_excluded");
    check(vx_excluded && port(rpd, "t1")->valid && port(rpd, "t1")->chosen.village_id == "v1", "an excluded harbour village yields no port (harbour_village_excluded); the town keeps its other harbour");
    World pe = map({"~~~~~~~~~~~~", "~~~~~~~~~~~~", "~~~~~~~~~~~~", "~~~~~~~~~~~~", "~~~~~~~~~~~~", "..~~~~~~~~~~"});
    town(pe, "t1", 5, 0); town(pe, "t2", 0, 8); village(pe, "v2", "t2", 5, 1); harbour(pe, "v2", "t2", 4, 1);
    const Result rpe = run(pe, lenient);
    check(port(rpe, "t2") && port(rpe, "t2")->candidates[0].reason == "town_unplaceable", "a harbour of an unplaceable town is invalid (town_unplaceable)");
    // A port's sea cell is never a deck cell (a joining crossing's cell: the runtime lays a bridge deck over it). Seed 43's world.
    const std::vector<std::string> estuary = {"~~~~~~~~~~", "~...R...~~", "~...R...~~", "~~~~~~~~~~"};
    World pf = map(estuary);
    town(pf, "t1", 1, 3); town(pf, "t2", 1, 6); village(pf, "v1", "t1", 2, 2); village(pf, "v2", "t2", 2, 6); harbour(pf, "v1", "t1", 1, 4);
    World pf_road = pf;
    road(pf_road, "t1", "t2", {{{1, 3}, {1, 4}, {1, 5}, {1, 6}}});
    const Result rpf = run(pf, lenient), rpf_road = run(pf_road, lenient);
    check(port(rpf, "t1")->valid && port(rpf, "t1")->chosen.sea_cell == Cell{1, 4} && rpf_road.roads[0].crossings[0].joins && deck_cells(Cells(10, 4, kSpacing), rpf_road) == std::set<std::size_t>{14} &&
          port(rpf_road, "t1")->valid && port(rpf_road, "t1")->chosen.sea_cell == Cell{0, 3},
          "a harbour on a river mouth takes the river cell, unless a joining crossing bridges it: then the next pair (deck cells are skipped)");
    World pg = map({"^^^^~~~~~~", "^...R...~~", "^^^^R^^^~~", "^^^^~~~~~~"});
    town(pg, "t1", 1, 3); town(pg, "t2", 1, 6); village(pg, "v1", "t1", 1, 2); village(pg, "v2", "t2", 1, 7); harbour(pg, "v1", "t1", 1, 4);
    road(pg, "t1", "t2", {{{1, 3}, {1, 4}, {1, 5}, {1, 6}}});
    const Result rpg = run(pg, lenient);
    check(!port(rpg, "t1")->valid && port(rpg, "t1")->candidates[0].reason == "port_pair_under_bridge", "a gate whose only coast pair is a deck cell has no port (port_pair_under_bridge)");
    // The engine path from the port to the gate must fit the envelope: a mountain cell between a port cell two cells from the gate and
    // the gate makes the path 2.21 cells (seed 43's town mnbl_add01e64_t_80ec1de1a0, 15.02 units against 12.5).
    const std::vector<std::string> spur = {"~~~~~~~~~~~~", "~~~~.......~", "~~~~.......~", "~~~~~^.....~", "~~~~~^^....~", "~~~~~~~~~~~~"};
    World ph = map(spur);
    town(ph, "t1", 3, 6); village(ph, "v1", "t1", 2, 8); harbour(ph, "v1", "t1", 3, 4);
    const Result rph = run(ph, strict);
    const double bound = port_path_bound(Cells(12, 6, kSpacing), rph, {}, Cell{3, 4}, rph.basin_of[3 * 12 + 4], at(3, 6), 2 * kSpacing);
    check(rph.status == "ok" && !port(rph, "t1")->valid && port(rph, "t1")->candidates[0].reason.rfind("port_path_too_long (13.79", 0) == 0 && std::abs(bound - (0.5 + 1 + std::sqrt(0.5)) * kSpacing) < 1e-9,
          "a port cell two cells from the gate behind a mountain cell: the path bound goes round its corner (2.21 cells) and the candidate is port_path_too_long");
    World ph_open = map(spur);
    ph_open.cls[3 * 12 + 5] = Land;
    town(ph_open, "t1", 3, 6); village(ph_open, "v1", "t1", 2, 8); harbour(ph_open, "v1", "t1", 3, 4);
    const Result rph_open = run(ph_open, strict);
    check(port(rph_open, "t1")->valid && port(rph_open, "t1")->chosen.sea_cell == Cell{3, 4} &&
          port_path_bound(Cells(12, 6, kSpacing), rph_open, {}, Cell{3, 4}, rph_open.basin_of[3 * 12 + 4], at(3, 6), 2 * kSpacing) == 1.5 * kSpacing,
          "without the mountain the same port cell is valid: a straight 1.5-cell path");

    // Bridge kinds and bounds: lake joins, coast does not, length and count bounds name the road's towns.
    World ba = map({"....L...L", "....L...L", "....L...L"});
    town(ba, "t1", 1, 1); town(ba, "t2", 1, 6);
    road(ba, "t1", "t2", {{{1, 1}, {1, 6}}});
    const Result rba = run(ba, strict);
    check(rba.status == "ok" && rba.roads[0].crossings[0].kind == "lake" && rba.roads[0].crossings[0].joins, "a short lake crossing on a road is a joining bridge candidate");
    World bb = map({"....~...~", "....~...~", "....~...~"});
    town(bb, "t1", 1, 1); town(bb, "t2", 1, 6);
    road(bb, "t1", "t2", {{{1, 1}, {1, 6}}});
    const Result rbb = run(bb, strict);
    check(rbb.roads[0].crossings[0].kind == "coast" && !rbb.roads[0].crossings[0].joins && placement(rbb, "t2")->method == "move",
          "a sea crossing is recorded as coast but never joins land: sea is crossed through ports (the far town moves)");
    World bc = map({"...RRR..RRRR", "...RRR..RRRR", "...RRR..RRRR"});
    town(bc, "t1", 1, 1); town(bc, "t2", 1, 6);
    road(bc, "t1", "t2", {{{1, 1}, {1, 6}}});
    Policy wide = strict; wide.max_bridge_cells = 3;
    check(!run(bc, strict).roads[0].crossings[0].joins && placement(run(bc, strict), "t2")->method == "move" && run(bc, wide).roads[0].crossings[0].joins &&
          placement(run(bc, wide), "t2")->method == "direct", "a river crossing longer than bridges.maxCells joins nothing");
    Policy no_bridges = strict; no_bridges.max_bridges = 0;
    check(has_reason(run(d, no_bridges), "quality.repair_bound_exceeded", "uid-t1") && has_reason(run(d, no_bridges), "quality.repair_bound_exceeded", "uid-t2"),
          "required bridges beyond maxRequired reject the world, naming the road's towns");

    // Bound reasons name UIDs; harbour shifts are capped; footprints on roads are not passes.
    check(has_reason(run(e, strict), "quality.repair_bound_exceeded", "uid-t1") && has_reason(run(e, strict), "quality.repair_bound_exceeded", "uid-t2"), "a pass bound reason names the road's towns");
    check(!has_reason(run(f, strict), "quality.repair_bound_exceeded", "uid-t1") && !has_reason(run(f, strict), "quality.repair_bound_exceeded", "uid-t2"),
          "a town's footprint and snap are town repairs outside the bounds and are never named in a bound reason");
    check(has_reason(ri, "quality.repair_bound_exceeded", "uid-v1"), "1 harbour shift of 2 harbours exceeds maxHarbourShiftFraction 0.1, naming the village");
    const Repair* shift = nullptr;
    for (const auto& x : ri.repairs) if (x.kind == "harbour_shift") shift = &x;
    check(shift && shift->from == Cell{1, 3} && shift->cell == Cell{0, 3}, "a harbour shift records the sea-node cell it starts from");
    World fr = map({"....^....^", "....^....^", "....^....^"});
    town(fr, "t1", 1, 4); town(fr, "t2", 1, 6);
    road(fr, "t1", "t2", {{{1, 4}, {1, 6}}});
    const Result rfr = run(fr, lenient);
    check(rfr.status == "ok" && repairs(rfr, "footprint") == 1 && repairs(rfr, "pass") == 0 && rfr.roads[0].passes.empty() && checks_of(rfr, "footprint") == 1 && checks_of(rfr, "pass") == 0,
          "a footprint on a road is recorded once, as a footprint with its host check, never as a pass");

    // Producer crossings: matched by a compiled crossing, or no compiled water on the step.
    World dp = map(river);
    town(dp, "t1", 1, 1); town(dp, "t2", 1, 6);
    road(dp, "t1", "t2", {{{1, 1}, {1, 2}, {1, 3}, {1, 4}, {1, 5}, {1, 6}}}, {{0, 0}, {0, 2}});
    const Result rdp = run(dp, strict);
    check(rdp.roads[0].producer_crossings.size() == 2 && rdp.roads[0].producer_crossings[0].outcome == "no_compiled_water" && rdp.roads[0].producer_crossings[1].outcome == "matched",
          "every producer river crossing gets an outcome: matched, or no compiled water");

    // Emitted points: clamped inside the cell; settlements on one point are recorded with a host check.
    World pt = map(kTwoIslands);
    pt.in.settlements.push_back({"t1", "uid-t1", "town", "", {2 * kSpacing, 1 * kSpacing}});
    village(pt, "v1", "t1", 1, 2); village(pt, "v2", "t1", 1, 2);
    const Result rpt = run(pt, lenient);
    const auto* t1p = placement(rpt, "t1");
    check(t1p->cell == Cell{1, 1} && t1p->point.x == 2 * kSpacing - kSpacing / 4 && t1p->point.y == kSpacing + kSpacing / 4,
          "a corner point takes the first land cell touching it and is clamped a quarter cell inside it");
    check(rpt.collisions.size() == 1 && rpt.collisions[0].size() == 2 && checks_of(rpt, "distinctPositions") == 1, "two villages on one point are a recorded collision with a host check");

    // Seam transitions get a host check.
    World sm = map({".~~~~~~~~~~~~~~.", ".~~~~~~~~~~~~~~."});
    town(sm, "t1", 0, 0);
    check(checks_of(run(sm, strict), "seamTransition") >= 1, "a transition on a seam column gets a seamTransition host check");

    // Determinism and documents.
    Identity id; id.world_sha256 = std::string(64, 'a'); id.side = 257;
    const Result ra2 = run(a, strict);
    check(graph_document(a.in, ra, id) == graph_document(a.in, ra2, id) && ports_document(a.in, ra, id) == ports_document(a.in, ra2, id), "the documents are deterministic");
    check(cells_bytes(a.in, ra).size() == 16 + 4 * 64 && cells_bytes(a.in, ra).compare(0, 8, "MNBLTC02") == 0 && graph_document(a.in, ra, id).find("\"engineValidation\":\"pending\"") != std::string::npos,
          "cells.bin v2 holds classes, OpenSea flags, land types and flags; the graph is pending engine validation");
    refused("a village bound to an unknown town", "broken_join", [&] { World x = map(kTwoIslands); town(x, "t1", 1, 2); village(x, "v1", "nope", 2, 2); run(x, strict); });
    refused("a road naming an unknown town", "broken_join", [&] { World x = map(kTwoIslands); town(x, "t1", 1, 2); road(x, "t1", "nope", {{{1, 2}}}); run(x, strict); });
    refused("duplicate settlement ids", "duplicate_id", [&] { World x = map(kTwoIslands); town(x, "t1", 1, 2); town(x, "t1", 1, 3); run(x, strict); });
}

// MMR-251: towns are repaired, never dropped, and the forced port comes last before a move.
void town_repair_tests(const Policy& strict) {
    // A town 4 cells out to sea: beyond maxSnapCells, within maxTownSnapCells. A town snap, outside maxSnapFraction (one snap of two
    // settlements would exceed 0.05).
    World ts = map({"~~~~~~~~~~", "~~~~~~~~~~", "~~~~~~~~~~", "~~~~~~~~~~", ".........."});
    town(ts, "t1", 4, 0); town(ts, "t2", 0, 8);
    const Result rts = run(ts, strict);
    check(rts.status == "ok" && placement(rts, "t2")->method == "snap" && placement(rts, "t2")->cell == Cell{4, 8} && repairs(rts, "town_snap") == 1 && repairs(rts, "move") == 0,
          "a town with no land within maxSnapCells snaps within maxTownSnapCells (a town_snap), outside the snap bound");
    World tn = map({"~~~~~~~~~~", ".........."});
    town(tn, "t1", 1, 0); town(tn, "t2", 0, 8);
    check(run(tn, strict).status == "ok" && repairs(run(tn, strict), "snap") == 1, "a town's snap within maxSnapCells keeps the kind snap and does not count toward maxSnapFraction");

    // A town on a lake cell with no land within its snap radius: the cell under it is carved to land, with a footprint host check. A town
    // out at sea is never carved.
    Policy no_snap = strict; no_snap.max_snap_cells = 0; no_snap.max_town_snap_cells = 0;
    World tc = map({"......", ".LLL..", "......"});
    town(tc, "t1", 0, 5); town(tc, "t2", 1, 2);
    const Result rtc = run(tc, no_snap);
    check(rtc.status == "ok" && placement(rtc, "t2")->method == "carve" && placement(rtc, "t2")->cell == Cell{1, 2} && rtc.cls[1 * 6 + 2] == Land && repairs(rtc, "carve") == 1 &&
          checks_of(rtc, "footprint") == 1 && placement(rtc, "t2")->group == placement(rtc, "t1")->group,
          "a town on a lake cell with no land within reach carves that cell to land (a recorded carve with a footprint host check) and joins the land around it");
    Policy no_carve = no_snap; no_carve.town_carve = false;
    check(placement(run(tc, no_carve), "t2")->method == "move", "without townCarve that town moves instead");
    World tsea = map({"~~~~~~", "~~~~~~", "......"});
    town(tsea, "t1", 2, 0); town(tsea, "t2", 0, 4);
    check(placement(run(tsea, no_snap), "t2")->method == "move" && repairs(run(tsea, no_snap), "carve") == 0, "a sea cell is never carved: the town moves");

    // Forced ports come only after a join failed. Two islands split by a two-cell ridge, the second with a kept village and no harbour:
    // the ridge is carved (a join), and no forced port is laid.
    World tj = map({"~~~~~~~~~~~~", "~....^^....~", "~....^^....~", "~~~~~~~~~~~~"});
    town(tj, "t1", 1, 2); town(tj, "t2", 1, 9);
    village(tj, "v1", "t1", 2, 2); village(tj, "v2", "t2", 2, 9); harbour(tj, "v1", "t1", 3, 2);
    const Result rtj = run(tj, strict);
    check(rtj.status == "ok" && repairs(rtj, "join") == 2 && repairs(rtj, "coast_port") == 0 && !port(rtj, "t2") && placement(rtj, "t2")->group == placement(rtj, "t1")->group,
          "a town whose group can join a player-safe group through a pass gets the join, not a forced port");
    Policy wide_ridge = strict; wide_ridge.max_access_cells = 1;
    const Result rtw = run(tj, wide_ridge);
    const Port* forced = port(rtw, "t2");
    check(rtw.status == "ok" && repairs(rtw, "join") == 0 && repairs(rtw, "coast_port") == 1 && forced && forced->valid && forced->forced && forced->chosen.kind == "coast" &&
          !port(rtw, "t1")->forced && placement(rtw, "t2")->method == "direct",
          "when the ridge is too wide to join, the island town takes a forced port (marked forced) and stays where it is");
    Identity pid; pid.world_sha256 = std::string(64, 'a'); pid.side = 257;
    check(ports_document(tj.in, rtw, pid).find("\"forced\":true") != std::string::npos && ports_document(tj.in, rtj, pid).find("\"forced\"") == std::string::npos,
          "ports.json marks a forced port \"forced\": true for MMR-252, and only that port");
    Policy no_forced = wide_ridge; no_forced.forced_ports = false;
    check(placement(run(tj, no_forced), "t2")->method == "move" && repairs(run(tj, no_forced), "coast_port") == 0, "without forcedPorts the island town moves instead");
    // Two towns on one island that cannot join: one forced port serves the group (RT-43: as few as possible).
    World tt = map({"~~~~~~~~~~~~~~~~", "~....~~~~~~....~", "~....~~~~~~....~", "~~~~~~~~~~~~~~~~"});
    town(tt, "t1", 1, 2); town(tt, "t4", 2, 3); town(tt, "t5", 1, 4); town(tt, "t2", 1, 12); town(tt, "t3", 2, 14);
    village(tt, "v1", "t1", 2, 2); village(tt, "v2", "t2", 2, 12); village(tt, "v3", "t3", 1, 14); harbour(tt, "v1", "t1", 3, 2);
    const Result rtt = run(tt, strict);
    check(rtt.status == "ok" && repairs(rtt, "coast_port") == 1 && placement(rtt, "t3")->group == placement(rtt, "t2")->group && rtt.groups[static_cast<std::size_t>(placement(rtt, "t3")->group)].safe,
          "one forced port makes an island of two towns player-safe; the second town gets none");

    // City preservation: sites = placed_cities = compiled towns = id-map towns = travel towns, one uid set.
    source::SourceWorld world;
    campaign::Compiled compiled;
    for (const char* uid : {"uid-t1", "uid-t2"}) {
        source::City c; c.uid = uid; world.cities.push_back(c);
        campaign::Town t; t.uid = uid; compiled.towns.push_back(t);
        compiled.id_map.push_back({std::string("id-") + uid, "town", uid});
    }
    world.metadata.founding_placed_cities = 2;
    World pw = map(kTwoIslands);
    town(pw, "t1", 1, 2); town(pw, "t2", 1, 12);
    village(pw, "v1", "t1", 2, 2); village(pw, "v2", "t2", 2, 12);
    harbour(pw, "v1", "t1", 3, 2); harbour(pw, "v2", "t2", 3, 12);
    const Result rp = run(pw, strict);
    bool kept = true;
    try { check_preservation(world, compiled, rp); } catch (const std::exception&) { kept = false; }
    check(kept, "a world whose every city is one town at every stage passes the city preservation assertion");
    refused("a compiled town missing", "city_not_preserved", [&] { auto c = compiled; c.towns.pop_back(); check_preservation(world, c, rp); });
    refused("an id-map town of another uid", "city_not_preserved", [&] { auto c = compiled; c.id_map.back().uid = "uid-other"; check_preservation(world, c, rp); });
    refused("placed_cities differing from the sites", "city_not_preserved", [&] { auto w = world; w.metadata.founding_placed_cities = 3; check_preservation(w, compiled, rp); });
    refused("a town without a cell in an accepted world", "city_not_preserved", [&] { auto r = rp; r.placements[1].cell = Cell{}; check_preservation(world, compiled, r); });
}

// The village quota (MMR-252) on small worlds: a hand-built campaign over a cell map, through analyze and village_quota.
struct QuotaWorld {
    World w;
    campaign::Compiled c;
    ids::Registry reg{"mnbl_0000a252"};
    std::map<std::string, std::string> id;   // short name -> game id
};
campaign::ContentPolicy quota_content() {
    campaign::ContentPolicy p;
    p.hamlet_roles["farming"] = {"farming", {"field"}, false};
    p.hamlet_roles["resource"] = {"resource", {"stone"}, false};
    p.hamlet_roles["harbor"] = {"harbor", {"harbour"}, true};
    return p;
}
QuotaWorld quota_world(const std::vector<std::string>& rows) {
    QuotaWorld q;
    q.w = map(rows);
    q.c.prefix = "mnbl_0000a252";
    campaign::Kingdom k; k.uid = "u-k1"; k.id = q.reg.assign("kingdom", k.uid); k.culture_uid = "cA"; k.presentation = "empire";
    campaign::House l; l.uid = "u-l1"; l.id = q.reg.assign("clan", l.uid); l.kingdom_id = k.id;
    campaign::Hero h; h.uid = "u-h0"; h.id = q.reg.assign("hero", h.uid); h.name = "Ruler"; h.role = "sovereign"; h.occupation = "Lord"; h.affiliation = "clan"; h.clan_id = l.id; h.kingdom_id = k.id; h.ruler = true;
    k.ruling_house_id = l.id; k.ruler_id = h.id; l.leader_id = h.id; l.members = {h.id};
    q.c.kingdoms.push_back(k); q.c.houses.push_back(l); q.c.heroes.push_back(h);
    return q;
}
void q_town(QuotaWorld& q, const std::string& name, int r, int c, const std::string& culture = "cA", const std::string& presentation = "empire") {
    campaign::Town t; t.uid = "u-" + name; t.id = q.reg.assign("town", t.uid); t.name = "Town " + name; t.culture_uid = culture; t.presentation = presentation;
    t.kingdom_id = q.c.kingdoms[0].id; t.owner_clan_id = q.c.houses[0].id;
    if (q.c.kingdoms[0].capital_id.empty()) { q.c.kingdoms[0].capital_id = t.id; q.c.houses[0].home_id = t.id; }
    q.c.kingdoms[0].towns.push_back(t.id); q.c.houses[0].fiefs.push_back(t.id);
    q.c.towns.push_back(t);
    q.id[name] = t.id;
    q.w.in.settlements.push_back({t.id, t.uid, "town", "", at(r, c)});
}
void q_castle(QuotaWorld& q, const std::string& name, const std::string& garrison, int r, int c) {
    campaign::Castle k; k.uid = "u-" + name; k.id = q.reg.assign("castle", k.uid); k.name = "Castle " + name; k.kingdom_id = q.c.kingdoms[0].id; k.owner_clan_id = q.c.houses[0].id;
    k.garrison_town_id = q.id.at(garrison); k.culture_uid = "cA";
    q.c.kingdoms[0].castles.push_back(k.id); q.c.houses[0].fiefs.push_back(k.id);
    for (auto& t : q.c.towns) if (t.id == k.garrison_town_id) t.castles.push_back(k.id);
    q.c.castles.push_back(k);
    q.id[name] = k.id;
    q.w.in.settlements.push_back({k.id, k.uid, "castle", "", at(r, c)});
}
void q_village(QuotaWorld& q, const std::string& name, const std::string& town, const std::string& production, int r, int c, int hr = -1, int hc = -1, const std::string& culture = "cA") {
    campaign::Village v; v.uid = "u-" + name; v.id = q.reg.assign("village", v.uid); v.name = "Village " + name; v.bound_id = q.id.at(town); v.source_town = v.bound_id;
    v.production = production; v.role = production == "harbor" ? "harbor" : production; v.harbour = hr >= 0; v.culture_uid = culture;
    v.slots = {{"villageType", "pending", "MMR-33", production}, {"hearth", "pending", "MMR-33", ""}, {"position", "pending", "MMR-28", ""}};
    if (hr >= 0) v.sea_node = static_cast<std::int64_t>(q.c.villages.size());
    for (auto& t : q.c.towns) if (t.id == v.bound_id) t.villages.push_back(v.id);
    q.c.villages.push_back(v);
    q.id[name] = v.id;
    q.w.in.settlements.push_back({v.id, v.uid, "village", v.bound_id, at(r, c)});
    if (hr >= 0) {
        q.w.in.harbours.push_back({v.id, v.uid, v.bound_id, *v.sea_node, at(hr, hc), 0.5});
        auto port = std::find_if(q.c.ports.begin(), q.c.ports.end(), [&](const campaign::Port& p) { return p.town_id == v.bound_id; });
        if (port == q.c.ports.end()) { campaign::Port p; p.town_id = v.bound_id; q.c.ports.push_back(p); port = q.c.ports.end() - 1; for (auto& t : q.c.towns) if (t.id == v.bound_id) t.port = true; }
        port->village_ids.push_back(v.id);
    }
}
void q_notable(QuotaWorld& q, const std::string& name, const std::string& village) {
    campaign::Hero h; h.uid = "u-" + name; h.id = q.reg.assign("hero", h.uid); h.name = "Reeve " + name; h.role = "reeve"; h.occupation = "RuralNotable"; h.affiliation = "settlement";
    h.settlement_id = q.id.at(village); h.settlement_kind = "village"; h.kingdom_id = q.c.kingdoms[0].id;
    q.c.heroes.push_back(h);
}
// The pass-1 analysis, the campaign exclusions and port records as travel::run applies them, then the quota.
Result quota_run(QuotaWorld& q, const Policy& p) {
    q.c.id_map = q.reg.entries();
    Result r = analyze_cells(q.w.in, p, q.w.cls);
    for (const auto& e : r.exclusions) campaign::exclude(q.c, e.id, e.reason);
    std::set<std::string> valid;
    for (const auto& port : r.ports) if (port.valid) valid.insert(port.town_id);
    for (auto it = q.c.ports.begin(); it != q.c.ports.end();) { if (!valid.count(it->town_id)) it = q.c.ports.erase(it); else ++it; }
    for (const auto& port : r.ports)
        if (port.valid && port.forced && std::none_of(q.c.ports.begin(), q.c.ports.end(), [&](const campaign::Port& x) { return x.town_id == port.town_id; })) {
            campaign::Port record; record.town_id = port.town_id; record.forced = true; q.c.ports.push_back(record);
            for (auto& t : q.c.towns) if (t.id == port.town_id) t.port = true;
        }
    if (r.status == "ok") village_quota(q.c, q.w.in, r, p, quota_content());
    return r;
}
bool closure_free(const campaign::Compiled& c) { return campaign::closure_problems(c).empty(); }
int bound_count(const campaign::Compiled& c, const std::string& fort) { int n = 0; for (const auto& v : c.villages) n += v.bound_id == fort; return n; }
const campaign::Village* village_of(const campaign::Compiled& c, const std::string& id) { for (const auto& v : c.villages) if (v.id == id) return &v; return nullptr; }
// r4: no kept village within the clearance of a fortification (exceptions aside), no two kept settlements on one point.
bool clear_of_forts(const Result& r, const campaign::Compiled& c, double clearance, double width) {
    std::set<std::string> excluded_ids, excepted;
    for (const auto& e : r.exclusions) excluded_ids.insert(e.id);
    if (c.quota) for (const auto& e : c.quota->exceptions) excepted.insert(e.village);
    auto d = [&](Point a, Point b) { double dx = std::abs(a.x - b.x); dx = std::min(dx, width - dx); return std::hypot(dx, a.y - b.y); };
    std::vector<const Placement*> kept;
    for (const auto& pl : r.placements) if (pl.cell.valid() && !excluded_ids.count(pl.id)) kept.push_back(&pl);
    for (const auto* a : kept)
        for (const auto* b : kept) {
            if (a == b) continue;
            if (d(a->point, b->point) < 1e-9) return false;
            if (a->kind == "village" && b->kind != "village" && d(a->point, b->point) < clearance && !excepted.count(a->id)) return false;
        }
    return true;
}

void quota_tests(const Policy& strict) {
    const std::vector<std::string> rows = {
        "~~~~~~~~~~~~~~~~~~~~~~~~",
        "~......................~",
        "~......................~",
        "~......................~",
        "~......................~",
        "~......................~",
        "~......................~",
        "~~~~~~~~~~~~~~~~~~~~~~~~"};
    auto build = [&]() {
        QuotaWorld q = quota_world(rows);
        q_town(q, "t1", 2, 4); q_town(q, "t2", 2, 18); q_castle(q, "k1", "t1", 2, 10);
        q_village(q, "v1", "t1", "harbor", 6, 4, 7, 4);     // the port's harbour village (pinned)
        q_village(q, "v2", "t1", "harbor", 6, 5, 7, 5);     // one more harbour village
        q_village(q, "v3", "t1", "harbor", 6, 2, 7, 2);     // a third: beyond the harbour cap
        q_village(q, "v4", "t1", "farming", 1, 1);           // the farthest: dropped
        q_village(q, "v5", "t1", "farming", 2, 4);           // on the town's own point: moves clear
        q_village(q, "v6", "t1", "resource", 1, 7);
        q_village(q, "v7", "t1", "farming", 1, 9);
        q_village(q, "w1", "t2", "farming", 4, 19);
        q_notable(q, "n4", "v4");
        return q;
    };
    QuotaWorld q = build();
    const Result r = quota_run(q, strict);
    const double width = q.w.in.grid.cols * q.w.in.grid.spacing;
    const auto& quota = *q.c.quota;
    check(r.status == "ok" && bound_count(q.c, q.id["t1"]) == 3 && bound_count(q.c, q.id["t2"]) == 3 && bound_count(q.c, q.id["k1"]) == 2 &&
          quota.passes == 2 && quota.stable && closure_free(q.c),
          "every town has exactly 3 and the castle exactly 2 bound villages, the records stay closed and the second analysis is stable");
    const auto* v1 = village_of(q.c, q.id["v1"]);
    const auto* t1port = std::find_if(q.c.ports.begin(), q.c.ports.end(), [&](const campaign::Port& p) { return p.town_id == q.id["t1"]; }).operator->();
    check(v1 && v1->pinned && v1->bound_id == q.id["t1"] && t1port->village_ids.front() == v1->id && port(r, q.id["t1"])->valid,
          "the port's chosen harbour village is pinned to its town and leads its port record; the port survives");
    check(village_of(q.c, q.id["v2"])->bound_id == q.id["t1"] && village_of(q.c, q.id["v5"])->bound_id == q.id["t1"] && !village_of(q.c, q.id["v4"]) &&
          excluded(r, q.id["v4"], "quota:"),
          "the town keeps its pinned village, one more harbour village and its nearest land village of a new class; its farthest is dropped with a quota: exclusion");
    const auto* v7 = village_of(q.c, q.id["v7"]);
    const auto* v6 = village_of(q.c, q.id["v6"]);
    check(v7 && v6 && v7->bound_id == q.id["k1"] && v6->bound_id == q.id["k1"] && v7->bound_kind == "castle" && v7->origin == "reassigned" && v7->source_town == q.id["t1"],
          "the castle takes its garrison town's two nearest remaining villages (boundKind castle, origin reassigned, sourceTown the town)");
    int synth = 0; bool synth_ok = true;
    for (const auto& v : q.c.villages)
        if (v.origin == "synthesized") {
            ++synth;
            const auto* pl = placement(r, v.id);
            const auto* t2 = placement(r, q.id["t2"]);
            const double d = std::hypot(pl->point.x - t2->point.x, pl->point.y - t2->point.y);
            synth_ok &= v.bound_id == q.id["t2"] && v.source_town == q.id["t2"] && v.uid.rfind(strict.villages.uid_prefix + "u-t2:", 0) == 0 && v.tier == "preferred" &&
                        v.production == "farming" && !v.harbour && pl->method == "synthesized" && d >= 4 && d >= 0.12 * quota.spacing && d <= 0.35 * quota.spacing &&
                        std::any_of(q.c.id_map.begin(), q.c.id_map.end(), [&](const ids::Entry& e) { return e.id == v.id && e.kind == "village"; });
        }
    check(synth == 2 && synth_ok && quota.synthesized == 2,
          "the short town gets two synthesized villages: uid prefix + town uid, in the preferred tier of the town spacing, clear of every fortification, of the largest-deficit class (farming), with id-map entries");
    const Placement* v5 = placement(r, q.id["v5"]);
    check(v5->point.x != at(2, 4).x || v5->point.y != at(2, 4).y, "the village on the town's point moves");
    check(clear_of_forts(r, q.c, strict.villages.clearance, width) && quota.exceptions.empty() && repairs(r, "clearance") >= 1 && quota.clearance_moves >= 1 && r.collisions.empty(),
          "no village sits within 4 units of a fortification and no two settlements share a point; the move is a recorded clearance repair (RT-13)");
    bool moved = !quota.moved.empty();
    for (const auto& h : q.c.heroes) if (h.uid == "u-n4") moved &= h.settlement_kind == "village" && village_of(q.c, h.settlement_id) && village_of(q.c, h.settlement_id)->bound_id == q.id["t1"];
    check(moved, "the notable of the dropped village moves to a kept village of the same town, recorded in movedNotables (RT-11)");
    const std::string doc = campaign::records_document(q.c, quota_content(), campaign::CulturePolicy{});
    check(doc.find("\"version\":2") != std::string::npos && doc.find("\"villageQuota\":{") != std::string::npos && doc.find("\"sourceTown\":") != std::string::npos &&
          doc.find("\"origin\":\"synthesized\"") != std::string::npos && doc.find("\"boundKind\":\"castle\"") != std::string::npos && doc.find("\"pinned\":true") != std::string::npos,
          "campaign.json v2 records boundKind, sourceTown, origin, pinned and the villageQuota block");
    // Determinism: the same world twice gives the same records and travel graph.
    QuotaWorld again = build();
    const Result r2 = quota_run(again, strict);
    Identity qid; qid.world_sha256 = std::string(64, 'a'); qid.side = 257;
    check(campaign::records_document(again.c, quota_content(), campaign::CulturePolicy{}) == doc && graph_document(again.w.in, r2, qid) == graph_document(q.w.in, r, qid),
          "the quota is deterministic: two runs give byte-identical campaign records and travel graphs");
    bool zero = false;
    for (const auto& t : q.c.towns) zero |= bound_count(q.c, t.id) == 0;
    for (const auto& k : q.c.castles) zero |= bound_count(q.c, k.id) == 0;
    check(!zero, "no fortification has 0 villages (RT-31)");

    // RT-09: a presentation culture's last farming village is never dropped. A town of another culture with a pinned and an extra harbour
    // village and two resource villages nearer than its only farming village keeps that farming village in place of its farthest.
    QuotaWorld p = quota_world(rows);
    q_town(p, "t1", 2, 4); q_town(p, "t3", 2, 18, "cB", "battania");
    q_village(p, "a1", "t1", "farming", 2, 3); q_village(p, "a2", "t1", "farming", 4, 6); q_village(p, "a3", "t1", "resource", 2, 6);
    q_village(p, "h1", "t3", "harbor", 6, 18, 7, 18, "cB"); q_village(p, "h2", "t3", "harbor", 6, 19, 7, 19, "cB");
    q_village(p, "r1", "t3", "resource", 4, 18, -1, -1, "cB"); q_village(p, "f1", "t3", "farming", 1, 21, -1, -1, "cB");
    const Result rp = quota_run(p, strict);
    check(rp.status == "ok" && village_of(p.c, p.id["f1"]) && village_of(p.c, p.id["f1"])->bound_id == p.id["t3"] && p.c.quota->protected_kept == 1 && bound_count(p.c, p.id["t3"]) == 3,
          "a culture's last farming village (horse ranches and wheat farms, RT-09) is kept in place of its town's farthest unprotected village");

    // RT-12: a forced-port town gets a synthesized harbour village, pinned, on the coast within two cells of its port cell; its port record
    // and ports.json name it.
    Policy wide_ridge = strict; wide_ridge.max_access_cells = 1;
    QuotaWorld f = quota_world({"~~~~~~~~~~~~", "~....^^....~", "~....^^....~", "~~~~~~~~~~~~"});
    q_town(f, "t1", 1, 2); q_town(f, "t2", 1, 9);
    q_village(f, "v1", "t1", "harbor", 2, 2, 3, 2); q_village(f, "v2", "t2", "farming", 2, 9);
    const Result rf = quota_run(f, wide_ridge);
    const Port* fp = port(rf, f.id["t2"]);
    const campaign::Village* harbour = nullptr;
    for (const auto& v : f.c.villages) if (v.origin == "synthesized" && v.harbour && v.bound_id == f.id["t2"]) harbour = &v;
    const auto record = std::find_if(f.c.ports.begin(), f.c.ports.end(), [&](const campaign::Port& x) { return x.town_id == f.id["t2"]; });
    bool near_port = false;
    if (harbour && fp) { const auto* pl = placement(rf, harbour->id); near_port = std::hypot(pl->point.x - (fp->chosen.sea_cell.col + 0.5) * kSpacing, pl->point.y - (fp->chosen.sea_cell.row + 0.5) * kSpacing) <= 2 * kSpacing; }
    check(rf.status == "ok" && fp && fp->forced && harbour && harbour->pinned && harbour->role == "harbor" && near_port && record != f.c.ports.end() && record->village_ids == std::vector<std::string>{harbour->id} &&
          fp->chosen.village_id == harbour->id && fp->chosen.harbour_cell == fp->chosen.sea_cell && bound_count(f.c, f.id["t2"]) == 3 && f.c.quota->stable &&
          ports_document(f.w.in, rf, qid).find("\"village\":\"" + harbour->id + "\"") != std::string::npos,
          "a forced-port town gets a pinned, synthesized harbour village within two cells of its port cell, named by its port record and ports.json (RT-12)");
}
// The road network (MMR-253): route pieces cut at crossings without a deck, decks kept, feeders on land travel cells and decks only,
// seam and pole safety, castles to their garrison town, road-less towns, maxCells, the summary and determinism.
bool edge_path(int cols, const std::vector<Cell>& path) {
    for (std::size_t k = 0; k + 1 < path.size(); ++k) {
        int dc = std::abs(path[k].col - path[k + 1].col);
        dc = std::min(dc, cols - dc);
        if (dc + std::abs(path[k].row - path[k + 1].row) != 1) return false;
    }
    return !path.empty();
}
const RoadResult* feeder_of(const Result& r, const std::string& id) { for (const auto& x : r.roads) if (x.kind == "feeder" && x.settlement == id) return &x; return nullptr; }
bool on_road(const Result& r, int cols, int row, int col) { return r.road[static_cast<std::size_t>(row) * cols + col] != 0; }
// The kept settlements on a road cell that are not the gate end (at their placement point) of a piece leaving their cell over land
// (MMR-247-R, acc4 c6: a drawn road reaches every gate); empty when all are. Settlements off every road are unconnected or islets.
std::string gates_unreached(const Result& r, int cols) {
    std::set<std::string> gone;
    for (const auto& e : r.exclusions) gone.insert(e.id);
    auto land = [&](Cell c) { return r.cls[static_cast<std::size_t>(c.row) * cols + c.col] == Land; };
    std::string missing;
    for (const auto& pl : r.placements) {
        if (gone.count(pl.id) || !pl.cell.valid() || !on_road(r, cols, pl.cell.row, pl.cell.col)) continue;
        bool reached = false;
        auto at_gate = [&](const RoadEnd& e, Cell next) { return e.kind == "gate" && e.settlement == pl.id && e.cell == pl.cell && e.point.x == pl.point.x && e.point.y == pl.point.y && land(next); };
        for (const auto& x : r.roads)
            for (const auto& p : x.pieces)
                if (p.path.size() >= 2) reached = reached || at_gate(p.start, p.path[1]) || at_gate(p.end, p.path[p.path.size() - 2]);
        if (!reached) missing += " " + pl.id;
    }
    return missing;
}

void road_network_tests(const Policy& strict) {
    // An undecked river crossing (one land component on both sides): the route stops at both banks; the village across the river from its
    // town's piece goes round by land and never over the river.
    World a = map({std::string(24, '~'), "~....R" + std::string(17, '.') + "~", "~....R" + std::string(17, '.') + "~", "~" + std::string(22, '.') + "~", std::string(24, '~')});
    town(a, "t1", 1, 1); town(a, "t2", 1, 10); village(a, "v1", "t1", 2, 7); village(a, "v2", "t2", 3, 10);
    road(a, "t1", "t2", {{{1, 1}, {1, 10}}});
    Result ra = run(a, strict);
    lay_roads(a.in, ra, strict, {});
    const auto& route = ra.roads[0];
    const RoadResult* fa = feeder_of(ra, "v1");
    bool land_only = fa != nullptr;
    if (fa) for (const auto& c : fa->path) land_only = land_only && a.cls[static_cast<std::size_t>(a.in.grid.cols * c.row + c.col)] == Land;
    check(ra.status == "ok" && route.kind == "route" && route.pieces.size() == 2 && route.decks.empty() && route.pieces[0].start.kind == "gate" && route.pieces[0].start.settlement == "t1" &&
          route.pieces[0].end.kind == "bank" && route.pieces[0].end.water == Cell{1, 5} && route.pieces[0].end.point.x == 5 * kSpacing && route.pieces[1].start.kind == "bank" &&
          route.pieces[1].end.kind == "gate" && route.pieces[1].end.settlement == "t2" && !on_road(ra, 24, 1, 5) && route.path.size() == 10,
          "a route stops at the bank of a crossing without a deck, on both sides, with the bank edge and its water cell (RT-17)");
    check(fa && land_only && edge_path(24, fa->path) && fa->path.size() >= 6 && fa->to_id == "t1" && fa->pieces[0].start.kind == "gate" && fa->pieces[0].end.kind == "junction" &&
          fa->pieces[0].end.road == 0 && ra.network.water_without_deck == 0 && ra.network.connected.at("village").first == 2 && ra.network.unconnected.empty(),
          "the village's feeder goes round the river by land to its town's piece; no road cell is on water without a deck");

    // A decked crossing (two land components joined by the route's bridge): the deck stays on the piece; a feeder from the far side reaches
    // its town over the route, and the deck is entered only along the route.
    World b = map({std::string(22, '~'), "~....R" + std::string(15, '.') + "~", "~....R" + std::string(15, '.') + "~", std::string(22, '~')});
    town(b, "t1", 1, 1); town(b, "t2", 1, 9); village(b, "v1", "t1", 2, 8); castle(b, "k1", 2, 2); village(b, "v2", "k1", 2, 3);
    road(b, "t1", "t2", {{{1, 1}, {1, 9}}});
    Result rb = run(b, strict);
    lay_roads(b.in, rb, strict, {{"k1", "t2"}});
    const RoadResult* fk = feeder_of(rb, "k1");
    bool via_deck = true;
    for (const auto& x : rb.roads) if (x.kind == "feeder") for (const auto& c : x.path) via_deck = via_deck && (b.cls[static_cast<std::size_t>(b.in.grid.cols * c.row + c.col)] == Land || c == Cell{1, 5});
    check(rb.status == "ok" && rb.roads[0].pieces.size() == 1 && rb.roads[0].decks == std::vector<Cell>{{1, 5}} && on_road(rb, 22, 1, 5) && !on_road(rb, 22, 2, 5) && via_deck &&
          rb.network.deck_cells == 1 && rb.network.water_without_deck == 0 && fk && fk->to_id == "t2" && feeder_of(rb, "v2") && feeder_of(rb, "v2")->to_id == "k1" &&
          rb.network.connected.at("castle").first == 1 && rb.network.connected.at("village").first == 2,
          "a joining crossing's deck stays on the route; feeders cross water only there, the castle reaches its garrison town and its village the castle");

    // Seam: a feeder takes the short way across the wrapped columns. Road-less town: the nearest town of its land group.
    World c = map({"~~~~~~~~~~~~", "............", "............", "~~~~~~~~~~~~"});
    town(c, "t1", 1, 1); town(c, "t2", 2, 4); village(c, "v1", "t1", 2, 10); village(c, "v2", "t2", 1, 5);
    Result rc = run(c, strict);
    lay_roads(c.in, rc, strict, {});
    const RoadResult* fc = feeder_of(rc, "v1");
    bool seam = false;
    if (fc) for (std::size_t k = 0; k + 1 < fc->path.size(); ++k) seam |= std::abs(fc->path[k].col - fc->path[k + 1].col) == 11;
    check(rc.status == "ok" && fc && seam && fc->path.size() <= 5 && edge_path(12, fc->path) && feeder_of(rc, "t1") && !feeder_of(rc, "t2") &&
          feeder_of(rc, "t1")->pieces[0].end.kind == "gate" && feeder_of(rc, "t1")->to_id == "t2" && rc.network.connected.at("town").first == 2,
          "a feeder crosses the seam the short way; the first road-less town feeds to the nearest town and the second is then on the network");

    // Pole: a route through a pole passage is two pieces with pole ends; maxCells: a village beyond it is recorded unconnected.
    World d = map({"............", "............", "~~~~~~~~~~~~", "............"});
    town(d, "t1", 0, 1); town(d, "t2", 0, 8); village(d, "v1", "t1", 1, 5); village(d, "v2", "t2", 1, 8);
    road(d, "t1", "t2", {{{0, 1}, {0, 3}}, {{0, 6}, {0, 8}}});
    Result rd = run(d, strict);
    Policy tight = strict; tight.roads.feeder_max_cells = 2;
    lay_roads(d.in, rd, tight, {});
    check(rd.roads[0].pieces.size() == 2 && rd.roads[0].pieces[0].end.kind == "pole" && rd.roads[0].pieces[1].start.kind == "pole" && rd.roads[0].pieces[0].end.cell == Cell{0, 3} &&
          !on_road(rd, 12, 0, 4) && !feeder_of(rd, "v1") && feeder_of(rd, "v2") && !rd.network.unconnected.empty() && rd.network.unconnected[0].id == "v1" &&
          rd.network.connected.at("village").first == 1,
          "a pole passage cuts a route into pieces with pole ends; a village beyond feeders.maxCells is recorded unconnected");

    // An islet: a town and its village on one cell, no road in their land group, have no network to join and count as connected.
    World e = map({"~~~~~~~~~~", "~.~~~~~..~", "~~~~~~~~~~"});
    town(e, "t1", 1, 1); town(e, "t3", 1, 1); village(e, "v1", "t1", 1, 1); town(e, "t2", 1, 8); village(e, "v2", "t2", 1, 7);
    Result re = run(e, strict);
    lay_roads(e.in, re, strict, {});
    check(re.network.connected.at("town").first == 3 && re.network.connected.at("village").first == 2 && placement(re, "t1")->method == "direct" && re.network.unconnected.empty() && !on_road(re, 10, 1, 1) && feeder_of(re, "v2"),
          "an islet (two towns and a village on one cell, the main group) counts as connected without a road; the other town's village feeds to it");

    // Gate spurs (MMR-247-R, acc4 c6): a village, a castle and a town on a route cell no piece ends at, and a village on its town's route-end
    // cell, each get a two-cell feeder from the gate to the land road cell beside it nearest the gate; no road cell is added. The village's
    // gate sits east of its cell centre, so its spur turns east. A village off the road keeps its ordinary feeder and gets no spur.
    World f = map({std::string(24, '~'), "~" + std::string(22, '.') + "~", "~" + std::string(22, '.') + "~", std::string(24, '~')});
    town(f, "t1", 1, 1); town(f, "t2", 1, 10); town(f, "t3", 1, 8); castle(f, "k1", 1, 7); village(f, "v2", "t1", 1, 1); village(f, "v3", "t2", 2, 9);
    f.in.settlements.push_back({"v1", "uid-v1", "village", "t1", Point{4.75 * kSpacing, 1.75 * kSpacing}});
    road(f, "t1", "t2", {{{1, 1}, {1, 10}}});
    Result rf = run(f, strict);
    lay_roads(f.in, rf, strict, {{"k1", "t2"}});
    const std::size_t laid = rf.network.cells, feeder_only = rf.network.feeder_only_cells, feeders = rf.network.feeders;
    lay_roads(f.in, rf, strict, {{"k1", "t2"}});
    auto spur = [&](const std::string& id, Cell to, const std::string& end_kind, const std::string& to_id) {
        const RoadResult* x = feeder_of(rf, id);
        const Placement* pl = placement(rf, id);
        return x && pl && x->pieces.size() == 1 && x->pieces[0].path.size() == 2 && x->pieces[0].path[0] == pl->cell && x->pieces[0].path[1] == to &&
               x->pieces[0].start.kind == "gate" && x->pieces[0].start.point.x == pl->point.x && x->pieces[0].start.point.y == pl->point.y && x->pieces[0].end.kind == end_kind &&
               (end_kind != "junction" || (x->pieces[0].end.road == 0 && x->pieces[0].end.point.x == (to.col + 0.5) * kSpacing)) && x->to_id == to_id;
    };
    check(rf.status == "ok" && placement(rf, "v1")->cell == Cell{1, 4} && placement(rf, "v1")->point.x == 4.75 * kSpacing && spur("v1", {1, 5}, "junction", "t1") &&
          spur("k1", {1, 6}, "junction", "t2") && spur("t3", {1, 7}, "junction", "t1") && spur("v2", {1, 2}, "junction", "t1") &&
          feeder_of(rf, "v3") && feeder_of(rf, "v3")->pieces[0].path.size() == 2 && feeder_of(rf, "v3")->pieces[0].path[1] == Cell{1, 9} && rf.network.feeders == 5,
          "a village, a castle and a town on a route cell, and a village on its town's cell, get a gate spur to the land road cell beside them nearest the gate (the off-centre gate turns east; ties to the lower cell)");
    check(laid == rf.network.cells && feeder_only == rf.network.feeder_only_cells && feeders == rf.network.feeders && rf.network.cells == 11 && rf.network.feeder_only_cells == 1 && rf.network.unconnected.empty(),
          "gate spurs add no road cell (11 road cells, 1 feeder-only cell), and laying the roads again replaces them");
    // A town whose only route leaves its cell over a deck (the decals stop at the bank) gets a spur to the land road cell beside it, here another
    // route's end cell, so the spur ends at that town's gate.
    World g = map({std::string(13, '~'), "~....R......~", "~....R......~", std::string(13, '~')});
    town(g, "t1", 1, 1); town(g, "t2", 1, 6); town(g, "t3", 2, 10); town(g, "t4", 2, 6);
    road(g, "t1", "t2", {{{1, 1}, {1, 6}}}); road(g, "t3", "t4", {{{2, 10}, {2, 6}}});
    Result rg = run(g, strict);
    lay_roads(g.in, rg, strict, {});
    const RoadResult* fg = feeder_of(rg, "t2");
    check(rg.status == "ok" && rg.roads[0].decks == std::vector<Cell>{{1, 5}} && fg && fg->pieces[0].path.size() == 2 && fg->pieces[0].path[1] == Cell{2, 6} &&
          fg->pieces[0].end.kind == "gate" && fg->pieces[0].end.settlement == "t4" && fg->to_id == "t4" && !feeder_of(rg, "t1") && !feeder_of(rg, "t3") && !feeder_of(rg, "t4"),
          "a town whose route leaves its cell over a deck gets a gate spur to the land road cell beside it (another route's end, so it ends at that town's gate)");
    check(gates_unreached(ra, 24).empty() && gates_unreached(rb, 22).empty() && gates_unreached(rc, 12).empty() && gates_unreached(rd, 12).empty() && gates_unreached(re, 10).empty() &&
          gates_unreached(rf, 24).empty() && gates_unreached(rg, 13).empty(),
          "every kept settlement on a road cell is the gate end, at its placement point, of a piece that leaves its cell over land (a drawn road reaches every gate)" +
          gates_unreached(ra, 24) + gates_unreached(rb, 22) + gates_unreached(rc, 12) + gates_unreached(rd, 12) + gates_unreached(re, 10) + gates_unreached(rf, 24) + gates_unreached(rg, 13));

    // Determinism, and the graph document carries pieces, ends and the network.
    Result ra2 = run(a, strict);
    lay_roads(a.in, ra2, strict, {});
    lay_roads(a.in, ra2, strict, {});
    Identity id; id.world_sha256 = std::string(64, 'a'); id.side = 257;
    const std::string doc = graph_document(a.in, ra, id);
    check(doc == graph_document(a.in, ra2, id) && ra2.road == ra.road && doc.find("\"roadNetwork\":") != std::string::npos && doc.find("\"kind\":\"bank\"") != std::string::npos &&
          doc.find("\"water\":[1,5]") != std::string::npos && doc.find("\"roadCells\":") != std::string::npos && doc.find("\"kind\":\"feeder\"") != std::string::npos,
          "road cells are deterministic and laying them again replaces the feeders; the document carries the pieces, their ends and the network");
}

// MMR-254: land face types from the shared table (corner majority, water excluded, main-diagonal ties, permanent white, the default), their
// counts and host checks, and the cells.bin v2 planes.
void land_type_tests(const Policy& strict) {
    const auto& faces = strict.calibration.face_groups;
    const int plain = faces.at("Plain"), desert = faces.at("Desert"), forest = faces.at("Forest"), steppe = faces.at("Steppe"), snow = faces.at("Snow"), swamp = faces.at("Swamp");
    World w = map({"....", "...."});
    auto set = [&](int r, int c, int biome, bool white = false) { const auto i = w.in.grid.sample(r, c); w.in.grid.biome[i] = static_cast<std::uint8_t>(biome); w.in.grid.white[i] = white ? 1 : 0; };
    auto type = [&](int r, int c) { return land_types(w.in, strict, w.cls)[static_cast<std::size_t>(r) * 4 + c]; };
    check(type(0, 0) == plain, "a grassland cell is Plain (the shared table)");
    set(0, 0, 2); set(0, 1, 2); set(1, 0, 2); set(1, 1, 2);
    check(type(0, 0) == desert, "four desert corners give a Desert face");
    set(0, 0, 4); set(1, 1, 4); set(0, 1, 4);
    check(type(0, 0) == forest, "three forest corners against one desert give Forest (majority)");
    set(0, 0, 2); set(1, 1, 2); set(0, 1, 15); set(1, 0, 4);
    check(type(0, 0) == desert, "two desert corners on the main diagonal against two forest corners off it: the main diagonal wins the tie");
    set(0, 0, 0); set(1, 1, 8); set(0, 1, 13); set(1, 0, 2);
    check(type(0, 0) == swamp, "water corners do not vote; a tie between the off-diagonal corners goes to the NE one (marsh: Swamp)");
    set(0, 0, 4); set(1, 1, 0); set(0, 1, 0); set(1, 0, 0);
    check(type(0, 0) == forest, "one land corner among three water corners decides alone");
    set(0, 0, 0);
    check(type(0, 0) == plain, "a land cell whose corners are all water biomes takes the default face");
    set(0, 0, 16); set(1, 1, 16); set(0, 1, 16); set(1, 0, 16);
    check(type(0, 0) == steppe, "cold tundra is Steppe where its snow melts");
    set(0, 0, 16, true); set(1, 1, 16, true); set(0, 1, 16, true);
    check(type(0, 0) == snow, "cold tundra is Snow in the permanent-white mask (12 snow months)");
    set(0, 0, 17); set(1, 1, 17); set(0, 1, 17, true); set(1, 0, 17, true);
    check(type(0, 0) == snow, "land ice is Snow");
    // The seam: cell (0, 3) reads column 0 as its east corners.
    w = map({"....", "...."});
    set(0, 0, 4); set(1, 0, 4); set(0, 3, 2); set(1, 3, 4);
    check(type(0, 3) == forest && type(0, 0) == forest, "the seam cell's east corners are column 0's samples (columns wrap)");
    w.cls[1] = Mountain;
    check(land_types(w.in, strict, w.cls)[1] == 0, "a cell that is not land has no land type");
    refused("a corner biome the world's catalogue does not name", "unmapped_category", [&] { World x = map({"....", "...."}); x.in.grid.biome[0] = 99; land_types(x.in, strict, x.cls); });
    refused("a grid without biome samples", "grid_mismatch", [&] { World x = map({"....", "...."}); x.in.grid.biome.clear(); land_types(x.in, strict, x.cls); });

    // A world: counts, host checks naming the touching cells' types, and the cells.bin v2 planes.
    World a = map({"~~~~~~~~", "~......~", "~......~", "~~~~~~~~"});
    for (int r = 0; r <= 4; ++r) for (int c = 4; c < 8; ++c) a.in.grid.biome[a.in.grid.sample(r, c)] = 2;
    town(a, "t1", 1, 1); town(a, "t2", 1, 6); village(a, "v1", "t1", 2, 2);
    road(a, "t1", "t2", {{{1, 1}, {1, 2}, {1, 3}, {1, 4}, {1, 5}, {1, 6}}});
    Result r = run(a, strict);
    lay_roads(a.in, r, strict, {});
    std::size_t land = 0; for (auto k : a.cls) land += k == Land;
    const std::size_t plains = r.land_type_counts.count("Plain") ? r.land_type_counts.at("Plain") : 0, deserts = r.land_type_counts.count("Desert") ? r.land_type_counts.at("Desert") : 0;
    check(r.status == "ok" && plains + deserts == land && deserts > 0 && plains > 0, "per-type counts cover every land cell (" + std::to_string(plains) + " Plain, " + std::to_string(deserts) + " Desert)");
    auto expected = [&](const std::string& check_name, const std::string& subject) { for (const auto& h : r.host_checks) if (h.check == check_name && h.subject == subject) return h.expected; return std::string(); };
    check(expected("gateFace", "t1").find("(Plain)") != std::string::npos && expected("gateFace", "t2").find("(Desert)") != std::string::npos && expected("settlementFace", "v1").find("(Plain)") != std::string::npos,
          "gate and settlement host checks name the land types of the cells touching the point");
    const std::string bytes = cells_bytes(a.in, r);
    const std::size_t n = 32;
    bool planes = bytes.size() == 16 + 4 * n;
    for (std::size_t i = 0; planes && i < n; ++i)
        planes = static_cast<std::uint8_t>(bytes[16 + i]) == r.cls[i] && static_cast<std::uint8_t>(bytes[16 + n + i]) == r.open_sea[i] && static_cast<std::uint8_t>(bytes[16 + 2 * n + i]) == r.land_type[i] &&
                 static_cast<std::uint8_t>(bytes[16 + 3 * n + i]) == (r.road[i] ? 1 : 0) && (r.land_type[i] != 0) == (r.cls[i] == Land);
    std::size_t roads = 0; for (std::size_t i = 0; i < n; ++i) roads += bytes[16 + 3 * n + i] == 1;
    check(planes && roads == r.network.cells && roads >= 6, "cells.bin v2: class, OpenSea, land type (exactly on land cells) and the road bit (" + std::to_string(roads) + " road cells, the network's count)");
    Identity id; id.world_sha256 = std::string(64, 'a'); id.side = 257;
    check(graph_document(a.in, r, id).find("\"landTypes\":{\"Desert\":" + std::to_string(deserts) + ",\"Plain\":" + std::to_string(plains) + "}") != std::string::npos, "travel-graph.json grid.landTypes holds the per-type counts");
}
}  // namespace

int main(int argc, char** argv) {
    if (argc != 3) { std::cerr << "usage: travel_graph_tests PLAYABILITY_POLICY WORK_DIR\n"; return 2; }
    policy_file = argv[1]; work = argv[2];
    fs::create_directories(work);
    try {
        const Policy policy = load_policy(policy_file);
        classification_tests(policy);
        policy_tests(policy);
        topology_tests(policy);
        town_repair_tests(policy);
        quota_tests(policy);
        road_network_tests(policy);
        land_type_tests(policy);
    } catch (const std::exception& e) { check(false, std::string("unexpected exception: ") + e.what()); }
    std::cout << checks - failures << "/" << checks << " checks passed\n";
    return failures == 0 ? 0 : 1;
}
