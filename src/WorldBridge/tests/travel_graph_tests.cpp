// Static travel graph tests (MMR-28): travel_graph_tests PLAYABILITY_POLICY WORK_DIR
// Cell classification rules, policy strictness, and every topology outcome on small cell maps:
//   '.' land  '^' mountain  '~' sea  'L' lake  'R' river      (settlements and nodes sit at cell centres)
// two islands with ports, an island without a port, closed basins (lake-only, too small), river bridge candidates
// (with and without the producer's report), road passes and their bound, footprints with access passes, snaps and
// unplaceable towns,
// village and castle exclusions and their bounds, harbour shifts, seam wrap, OpenSea, pole passages, determinism.
#include <algorithm>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iostream>
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
    g = grid(); set(g, 0, 1, -3);
    check(classify(g, policy)[0] == Land, "an off-diagonal low corner does not make a sea cell");
    g = grid(); set(g, 1, 1, 1, 2);
    check(classify(g, policy)[0] == Lake && classify(g, policy)[5] == Lake, "lake: a main-diagonal corner carries lake intent (both cells sharing it)");
    g = grid(); set(g, 0, 0, 1, 3);
    check(classify(g, policy)[0] == River, "river: a main-diagonal corner carries river intent");
    g = grid(); set(g, 0, 0, -2, 2); set(g, 1, 1, -2);
    check(classify(g, policy)[0] == Sea, "below-datum lake corners still give sea (the datum rule comes first)");
    g = grid(); set(g, 0, 1, 1 + 1.01 * kSpacing);
    check(classify(g, policy)[0] == Mountain, "mountain: (max - min corner) / spacing above mountainSlope");
    g = grid(); set(g, 0, 1, 1 + 0.99 * kSpacing);
    check(classify(g, policy)[0] == Land, "slope at or below mountainSlope stays land");
    g = grid(); g.z.pop_back();
    refused("a grid with the wrong sample count", "grid_mismatch", [&] { classify(g, policy); });
}

void policy_tests(const Policy& policy) {
    check(policy.calibration.invalid.at("Default").count(18) && policy.calibration.invalid.at("All").count(7) && policy.calibration.land_to_sea == 50 &&
          policy.calibration.embark_threshold == 0.5 && policy.calibration.evidence_sha256.size() == 64 && policy.calibration.port_gate_envelope_max == 10.97,
          "the calibration carries the MMR-17 invalid-terrain sets, region switch costs, embark threshold and the installed port-gate envelope");
    check(policy.side == 257 && policy.mountain_slope == 1.0 && policy.open_sea_cells == 4 && policy.max_access_cells == 6 && policy.bridge_kinds.size() == 2 && policy.max_bridge_cells == 2 && policy.max_port_gate_cells == 2 && policy.sha256.size() == 64, "the policy loads with its pinned values");
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
    mutated("unknown_face_group", "\"lake\": \"Lake\"", "\"lake\": \"Steppe\"", "unmapped_category");
    mutated("calibration_altered", "2cf08bceca6d7fd089b4ef99fcb77f8c06c247534dd3e5d4b5864233b38f4e85", std::string(64, '0'), "calibration_mismatch");
    mutated("unknown_member", "\"version\": 1,", "\"version\": 1, \"extra\": 1,", "unknown_field");
    mutated("bridge_kind", "\"river\",", "\"ford\",", "unmapped_category");
    mutated("port_gate_zero", "\"maxPortGateCells\": 2", "\"maxPortGateCells\": 0", "out_of_range");
    mutated("fraction_range", "\"maxPassFraction\": 0.1", "\"maxPassFraction\": 1.5", "out_of_range");
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

    // An island town without a harbour record can be reached by sea but never left: rejected.
    World b = map(kTwoIslands);
    town(b, "t1", 1, 2); town(b, "t2", 1, 12);
    village(b, "v1", "t1", 2, 2); harbour(b, "v1", "t1", 3, 2);
    const Result rb = run(b, strict);
    const auto& g2 = rb.groups[static_cast<std::size_t>(placement(rb, "t2")->group)];
    check(rb.status == "rejected" && has_reason(rb, "quality.unreachable_town", "uid-t2") && g2.forward && !g2.backward,
          "an island town without a port is reachable but cannot return: quality.unreachable_town names its UID");
    check(!port(rb, "t2"), "a shore without a harbour record is never a port");

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
    check(rd2.status == "rejected" && has_reason(rd2, "quality.unreachable_town", "uid-t2"), "without the road the far town is unreachable (no ports): rejected");
    Policy no_rivers = strict; no_rivers.bridge_kinds = {"coast"};
    check(run(d, no_rivers).status == "rejected" && !run(d, no_rivers).roads[0].crossings[0].joins, "a crossing kind the policy does not list joins nothing");

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
    check(re2.status == "rejected" && re2.roads[0].crossings[0].kind == "blocked", "with passes disabled the ridge blocks the road and the far town is unreachable");

    // Placement: a footprint on a mountain, a snap from the sea, and an unplaceable town.
    World f = map({"~~~~~~~~", "~^.....~", "~......~", "~~~~~~~~"});
    f.in.settlements.push_back({"t1", "uid-t1", "town", "", at(1, 1)});
    f.in.settlements.push_back({"t2", "uid-t2", "town", "", at(0, 4)});
    const Result rf = run(f, lenient);
    check(placement(rf, "t1")->method == "footprint" && rf.cls[1 * 8 + 1] == Land && repairs(rf, "footprint") == 1, "a town on a mountain cell gets it as its footprint");
    check(placement(rf, "t2")->method == "snap" && placement(rf, "t2")->cell == Cell{1, 4} && placement(rf, "t2")->shift == 1 && repairs(rf, "snap") == 1 && rf.status == "ok",
          "a town on a sea cell snaps to the nearest land cell within maxSnapCells");
    check(run(f, strict).status == "rejected" && has_reason(run(f, strict), "quality.repair_bound_exceeded"), "footprints and snaps beyond their fractions reject the world");
    World fa = map({"~~~~~~~~~~", "~^^^......", "~^^^......", "~~~~~~~~~~"});
    town(fa, "t1", 1, 1); town(fa, "t2", 1, 7);
    const Result rfa = run(fa, lenient);
    check(rfa.status == "ok" && repairs(rfa, "access") == 2 && placement(rfa, "t1")->land == placement(rfa, "t2")->land && rfa.cls[1 * 10 + 2] == Land && rfa.cls[1 * 10 + 3] == Land,
          "a footprint walled in by mountains gets a two-cell access pass to the nearest land");
    Policy short_access = lenient; short_access.max_access_cells = 1;
    const Result rfb = run(fa, short_access);
    check(rfb.status == "rejected" && repairs(rfb, "access") == 0 && has_reason(rfb, "quality.unreachable_town") && placement(rfb, "t1")->land != placement(rfb, "t2")->land,
          "beyond maxAccessCells the footprint stays walled in, the two towns are split and the world is rejected");
    World f2 = map({"~~~~~~~~~~~~", "~~~~~~~~~~~~", "~~~~~~~~~~~~", "~~~~~~~~~~~~", "~~~~~~~~~~~~", "..~~~~~~~~~~"});
    town(f2, "t1", 5, 0); town(f2, "t2", 0, 8);
    const Result rf2 = run(f2, lenient);
    check(rf2.status == "rejected" && has_reason(rf2, "quality.unplaceable_town", "uid-t2") && placement(rf2, "t2")->method == "unplaceable", "a town with no land within reach is unplaceable: rejected");

    // A village on the other island is excluded; too many exclusions reject the world.
    World g = map(kTwoIslands);
    town(g, "t1", 1, 2); town(g, "t2", 1, 12);
    village(g, "v1", "t1", 2, 2); village(g, "v2", "t2", 2, 12); village(g, "vx", "t1", 2, 13);
    harbour(g, "v1", "t1", 3, 2); harbour(g, "v2", "t2", 3, 12);
    const Result rg = run(g, lenient);
    check(rg.status == "ok" && excluded(rg, "vx", "not land-reachable"), "a village outside its town's land group is excluded with the reason");
    check(has_reason(run(g, strict), "quality.too_many_exclusions", "uid-vx"), "1 of 3 villages excluded exceeds maxExcludedVillageFraction 0.1: rejected, naming the UID");

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
    check(rl.roads[0].pole_passages == 1 && rl.roads[0].crossings.empty() && !rl.roads[0].same_group && rl.status == "rejected",
          "a pole passage splits the road: the river the unsplit road would bridge is never crossed");

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
    check(rbb.status == "rejected" && rbb.roads[0].crossings[0].kind == "coast" && !rbb.roads[0].crossings[0].joins, "a sea crossing is recorded as coast but never joins land: sea is crossed through ports");
    World bc = map({"...RRR..RRRR", "...RRR..RRRR", "...RRR..RRRR"});
    town(bc, "t1", 1, 1); town(bc, "t2", 1, 6);
    road(bc, "t1", "t2", {{{1, 1}, {1, 6}}});
    Policy wide = strict; wide.max_bridge_cells = 3;
    check(run(bc, strict).status == "rejected" && !run(bc, strict).roads[0].crossings[0].joins && run(bc, wide).status == "ok", "a river crossing longer than bridges.maxCells joins nothing");
    Policy no_bridges = strict; no_bridges.max_bridges = 0;
    check(has_reason(run(d, no_bridges), "quality.repair_bound_exceeded", "uid-t1") && has_reason(run(d, no_bridges), "quality.repair_bound_exceeded", "uid-t2"),
          "required bridges beyond maxRequired reject the world, naming the road's towns");

    // Bound reasons name UIDs; harbour shifts are capped; footprints on roads are not passes.
    check(has_reason(run(e, strict), "quality.repair_bound_exceeded", "uid-t1") && has_reason(run(e, strict), "quality.repair_bound_exceeded", "uid-t2"), "a pass bound reason names the road's towns");
    check(has_reason(run(f, strict), "quality.repair_bound_exceeded", "uid-t1") && has_reason(run(f, strict), "quality.repair_bound_exceeded", "uid-t2"), "footprint and snap bound reasons name the settlements");
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
    check(cells_bytes(a.in, ra).size() == 16 + 2 * 64 && graph_document(a.in, ra, id).find("\"engineValidation\":\"pending\"") != std::string::npos, "cells.bin holds classes and OpenSea flags; the graph is pending engine validation");
    refused("a village bound to an unknown town", "broken_join", [&] { World x = map(kTwoIslands); town(x, "t1", 1, 2); village(x, "v1", "nope", 2, 2); run(x, strict); });
    refused("a road naming an unknown town", "broken_join", [&] { World x = map(kTwoIslands); town(x, "t1", 1, 2); road(x, "t1", "nope", {{{1, 2}}}); run(x, strict); });
    refused("duplicate settlement ids", "duplicate_id", [&] { World x = map(kTwoIslands); town(x, "t1", 1, 2); town(x, "t1", 1, 3); run(x, strict); });
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
    } catch (const std::exception& e) { check(false, std::string("unexpected exception: ") + e.what()); }
    std::cout << checks - failures << "/" << checks << " checks passed\n";
    return failures == 0 ? 0 : 1;
}
