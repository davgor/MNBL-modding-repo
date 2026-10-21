// Projection and source-node tests (MMR-25): projection_tests POLICY_JSON CALIBRATION_JSON FIXTURE_DIR CONTRACT_JSON WORK_DIR
// Node decoding (poles, duplicate seam, adjacency), forward/inverse projection (poles, antimeridian, duplicate seam,
// coverage), seam and pole path splitting, the policy contract, the calibration pin, and the projected export: every
// terrain sample and object position must come from the one projection whose identity each output carries.
#include <cmath>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iostream>
#include <limits>
#include <sstream>
#include <string>
#include "worldbridge/projected_world.hpp"

namespace fs = std::filesystem;
using namespace worldbridge;
using projection::Point;
using projection::Projection;
using source::NodeGrid;
using source::Pole;

namespace {
int checks = 0, failures = 0;
fs::path policy_file, calibration_file, fixture, contract_file, work;

void check(bool ok, const std::string& what) {
    ++checks;
    if (ok) std::cout << "ok    " << what << "\n";
    else { ++failures; std::cout << "FAIL  " << what << "\n"; }
}
bool near(double a, double b, double tolerance = 1e-9) { return std::abs(a - b) <= tolerance; }
bool same(Point a, Point b, double tolerance = 1e-9) { return near(a.x, b.x, tolerance) && near(a.y, b.y, tolerance); }
std::string slurp(const fs::path& p) { std::ifstream in(p, std::ios::binary); std::ostringstream s; s << in.rdbuf(); return s.str(); }
void spit(const fs::path& p, const std::string& text) { std::ofstream out(p, std::ios::binary | std::ios::trunc); out << text; }

void refused(const std::string& what, const std::string& code, const std::function<void()>& body) {
    try { body(); check(false, what + ": accepted, expected " + code); }
    catch (const json::Error& e) { check(e.code == code, what + " -> " + e.code + (e.code == code ? "" : " (wanted " + code + "): " + e.message)); }
    catch (const std::exception& e) { check(false, what + ": unexpected " + std::string(e.what())); }
}

projection::Policy policy_with(double central_meridian) {
    auto p = projection::load_policy(policy_file);
    p.central_meridian_deg = central_meridian;
    return p;
}
projection::SourceFrame frame(std::int64_t side, double sea = 0, double radius = 31830.988618379066) { return {side, radius, sea, "42"}; }

void nodes() {
    const NodeGrid g(5);
    check(g.node_count() == 14 && g.south_pole() == 13, "n=5: 2 + 3*4 = 14 nodes, south pole 13");
    bool round_trip = true;
    for (std::int64_t i = 0; i < g.node_count(); ++i) { const auto s = g.sample_of(i); round_trip = round_trip && g.node_of(s.column, s.row) == i; }
    check(round_trip, "every node decodes to a sample that encodes back to it");
    bool north = true, south = true;
    for (std::int64_t c = 0; c < 5; ++c) { north = north && g.node_of(c, 0) == 0; south = south && g.node_of(c, 4) == 13; }
    check(north && south, "every sample of a pole row is that pole's single node");
    check(g.sample_of(0).column == 0 && g.sample_of(0).row == 0 && g.sample_of(13).column == 0 && g.sample_of(13).row == 4, "pole nodes decode to column 0");
    bool seam = true;
    for (std::int64_t r = 0; r < 5; ++r) seam = seam && g.node_of(4, r) == g.node_of(0, r);
    check(seam, "column n-1 duplicates column 0 (duplicate seam)");
    check(g.duplicate(4, 2) && g.duplicate(3, 0) && !g.duplicate(0, 0) && !g.duplicate(3, 2), "duplicate samples flagged; canonical samples not");
    check(g.node_of(1, 1) == 2 && g.node_of(3, 3) == 12 && g.sample_of(6).column == 1 && g.sample_of(6).row == 2, "row-major interior numbering");
    check(NodeGrid(33).node_of(18, 8) == 243 && NodeGrid(33).node_count() == 994, "generator parity: size-33 site node 243 is sample (18, 8); 994 nodes");
    check(g.agrees(6, 1, 2) && !g.agrees(7, 1, 2) && !g.agrees(99, 1, 2), "record (node, x, z) agreement");
    check(g.pole_of(0) == Pole::North && g.pole_of(13) == Pole::South && g.pole_of(5) == Pole::None, "pole_of");
    bool ring = true;
    for (std::int64_t c = 0; c < 4; ++c) ring = ring && g.adjacent(0, g.node_of(c, 1)) && !g.adjacent(0, g.node_of(c, 2)) && g.adjacent(13, g.node_of(c, 3));
    check(ring, "a pole is adjacent to its whole ring and nothing else");
    check(g.adjacent(g.node_of(3, 2), g.node_of(0, 2)) && g.adjacent(g.node_of(0, 1), g.node_of(3, 2)), "adjacency wraps across the seam, diagonals included");
    check(!g.adjacent(g.node_of(0, 2), g.node_of(2, 2)) && !g.adjacent(0, 13) && !g.adjacent(5, 5), "non-neighbours, the two poles and a node with itself are not adjacent");
    refused("sample column 5 on n=5", "out_of_coverage", [&] { g.node_of(5, 1); });
    refused("sample column -1 (no wrapping)", "out_of_coverage", [&] { g.node_of(-1, 1); });
    refused("sample row 5", "out_of_coverage", [&] { g.node_of(0, 5); });
    refused("node -1", "invalid_node", [&] { g.sample_of(-1); });
    refused("node 14", "invalid_node", [&] { g.sample_of(14); });
    refused("side 2", "out_of_range", [] { NodeGrid(2); });
}

void forward_inverse() {
    const Projection p(policy_with(0), frame(129));
    check(near(p.width(), 800) && near(p.height(), 400), "W = 800, H = W/2");
    check(same(p.forward(0, 0), {400, 200}), "(0, 0) -> centre (400, 200)");
    check(same(p.forward(0, -180), {0, 200}) && same(p.forward(0, 180), {0, 200}), "antimeridian: -180 and +180 are the same point, x = 0");
    check(p.forward(10, 179.999).x > 799.99 && p.forward(10, -179.999).x < 0.01, "points either side of the antimeridian sit at either edge");
    check(near(p.wrap_delta(p.forward(10, -179.999).x - p.forward(10, 179.999).x), 2 * 0.001 * 800 / 360, 1e-7), "nearest-image distance across the antimeridian is the short way");
    bool round_trip = true;
    for (double lat = -89.5; lat < 90; lat += 7.25)
        for (double lon = -180; lon < 180; lon += 11.5) {
            const auto g = p.inverse(p.forward(lat, lon));
            round_trip = round_trip && near(g.latitude, lat) && near(g.longitude, lon) && g.pole == Pole::None;
        }
    check(round_trip, "inverse(forward(lat, lon)) = (lat, lon) over the whole globe");
    bool back = true;
    for (double x = 0; x < 800; x += 37.5)
        for (double y = 0.5; y < 400; y += 41) { const auto g = p.inverse({x, y}); back = back && same(p.forward(g.latitude, g.longitude), {x, y}); }
    check(back, "forward(inverse(x, y)) = (x, y)");
    check(near(p.inverse({800, 200}).longitude, -180) && near(p.inverse({-0.25, 200}).longitude, p.inverse({799.75, 200}).longitude), "inverse wraps x");
    check(p.inverse({123, 0}).pole == Pole::North && near(p.inverse({123, 0}).latitude, 90) && p.inverse({5, 400}).pole == Pole::South, "y = 0 and y = H are the poles");
    check(same(p.forward_pole(Pole::North, 812), {12, 0}) && same(p.forward_pole(Pole::South, 7), {7, 400}), "pole lines");
    refused("forward at the north pole", "singular_point", [&] { p.forward(90, 0); });
    refused("forward at the south pole", "singular_point", [&] { p.forward(-90, 45); });
    refused("latitude 90.5", "out_of_coverage", [&] { p.forward(90.5, 0); });
    refused("longitude 180.5", "out_of_coverage", [&] { p.forward(0, 180.5); });
    refused("NaN latitude", "nonfinite_number", [&] { p.forward(std::numeric_limits<double>::quiet_NaN(), 0); });
    refused("infinite x", "nonfinite_number", [&] { p.inverse({std::numeric_limits<double>::infinity(), 1}); });
    refused("y below the north pole line", "out_of_coverage", [&] { p.inverse({1, -0.001}); });
    refused("y beyond the south pole line", "out_of_coverage", [&] { p.inverse({1, 400.001}); });
    refused("east-west factor at a pole", "singular_point", [&] { p.east_west_factor(90); });
    check(near(p.east_west_factor(60), 2.0, 1e-12) && near(p.east_west_factor(0), 1.0), "east-west factor 1/cos(latitude)");
    check(near(p.horizontal_scale(), p.meridional_scale(), 1e-15) && near(p.horizontal_scale(), 800 / 200000.0, 1e-12), "true scale on the equator and meridians: 0.004 units per metre for a 200 km globe");
    check(near(p.canonical_x(-0.5), 799.5) && near(p.canonical_x(800), 0) && near(p.canonical_x(1600.25), 0.25), "canonical_x");
    check(near(p.wrap_delta(790), -10) && near(p.wrap_delta(-790), 10) && near(p.wrap_delta(5), 5), "wrap_delta picks the nearest image");
    check(same(p.physical({0, 0}), {400, 600}) && same(p.physical({799, 400}), {1199, 1000}) && near(p.height() + 2 * p.pole_margin(), p.width() + 2 * p.strip()), "periodic layout: physical = logical + (strip W/2, pole margin 3W/4); the physical domain is square, 1600 x 1600");

    const Projection shifted(policy_with(90), frame(129));
    check(same(shifted.forward(0, 90), {400, 200}) && same(shifted.forward(0, -90), {0, 200}), "central meridian 90: the seam moves to -90");
    bool shifted_trip = true;
    for (double lon = -180; lon < 180; lon += 13) shifted_trip = shifted_trip && near(shifted.inverse(shifted.forward(-20, lon)).longitude, lon);
    check(shifted_trip, "round trip with a shifted central meridian");
    check(shifted.identity() != p.identity(), "the central meridian is part of the identity");
}

void samples() {
    const Projection p(policy_with(0), frame(9, 10));
    bool agree = true, seam = true, poles = true;
    for (std::int64_t r = 1; r < 8; ++r)
        for (std::int64_t c = 0; c < 9; ++c) agree = agree && same(p.sample(c, r), p.forward(p.sample_latitude(r), p.sample_longitude(c)));
    for (std::int64_t r = 0; r < 9; ++r) seam = seam && p.sample(8, r).x == p.sample(0, r).x && p.sample(8, r).y == p.sample(0, r).y;
    for (std::int64_t c = 0; c < 9; ++c) poles = poles && p.sample(c, 0).y == 0 && p.sample(c, 8).y == p.height();
    check(agree, "grid samples equal forward(latitude, longitude) of the sample");
    check(seam, "duplicate seam column n-1 lands exactly on column 0");
    bool shifted_seam = true;
    for (double meridian : {-10.0, 22.5, 77.7, -179.0})
        for (std::int64_t r = 0; r < 9; ++r) {
            const Projection q(policy_with(meridian), frame(9));
            shifted_seam = shifted_seam && q.sample(8, r).x == q.sample(0, r).x && q.sample(8, r).y == q.sample(0, r).y;
        }
    check(shifted_seam, "column n-1 is bit-identical to column 0 for non-zero central meridians too");
    check(poles, "pole rows are the pole lines");
    check(same(p.node(p.grid().node_of(3, 4)), p.sample(3, 4)), "node position = its canonical sample");
    refused("position of the north pole node", "singular_point", [&] { p.node(0); });
    refused("position of the south pole node", "singular_point", [&] { p.node(p.grid().south_pole()); });
    const double vs = projection::load_policy(policy_file).vertical_scale;
    check(near(p.z(110), 100 * vs) && near(p.z(10), 0) && near(p.z(-90), -100 * vs), "z = (height - sea level) * verticalScale");
    check(p.below_datum(9.999) && !p.below_datum(10) && !p.below_datum(11), "below the datum only");
    refused("non-finite height", "nonfinite_number", [&] { p.z(std::numeric_limits<double>::infinity()); });
    refused("zero radius", "out_of_range", [&] { Projection(policy_with(0), frame(9, 0, 0)); });
}

void paths() {
    const Projection p(policy_with(0), frame(9));
    const auto& g = p.grid();
    const double cell = 100;  // W / (n - 1)
    auto steps_short = [&](const projection::Path& path) {
        for (const auto& piece : path.pieces)
            for (std::size_t i = 1; i < piece.size(); ++i) if (std::abs(piece[i].x - piece[i - 1].x) > p.width() / 2) return false;
        return true;
    };
    const auto east = p.path({g.node_of(6, 4), g.node_of(7, 4), g.node_of(0, 4), g.node_of(1, 4)});
    check(east.pieces.size() == 2 && east.links.size() == 1 && east.links[0] == "seam", "eastward road across the seam: two pieces, one seam link");
    check(same(east.pieces[0].back(), {800, 200}) && same(east.pieces[1].front(), {0, 200}) && steps_short(east), "it ends on x = W and continues from x = 0 at the same y; no map-wide step");
    const auto west = p.path({g.node_of(1, 4), g.node_of(0, 4), g.node_of(7, 4)});
    check(west.pieces.size() == 2 && west.links[0] == "seam" && same(west.pieces[0].back(), {0, 200}) && same(west.pieces[1].front(), {800, 200}) && same(west.pieces[1].back(), {7 * cell, 200}), "westward across the seam");
    const auto ends = p.path({g.node_of(7, 3), g.node_of(0, 4)});
    check(ends.pieces.size() == 1 && ends.links.empty() && same(ends.pieces[0].back(), {800, 200}), "a path ending on the seam column ends on x = W (the same point as x = 0)");
    const auto through = p.path({g.node_of(7, 3), g.node_of(0, 4), g.node_of(1, 5)});
    check(through.pieces.size() == 2 && through.links.size() == 1 && same(through.pieces[0].back(), {800, 200}) && same(through.pieces[1].front(), {0, 200}) &&
          same(through.pieces[1].back(), {100, 250}), "a diagonal path through the seam column continues from x = 0");
    const auto starts_on_seam = p.path({g.node_of(0, 4), g.node_of(7, 4)});
    check(starts_on_seam.pieces.size() == 1 && starts_on_seam.links.empty() && same(starts_on_seam.pieces[0].front(), {800, 200}) && same(starts_on_seam.pieces[0].back(), {700, 200}),
          "a path starting on the seam heading west starts on x = W, with no zero-length piece");
    const auto touch = p.path({g.node_of(7, 3), g.node_of(0, 4), g.node_of(7, 5)});
    check(touch.pieces.size() == 1 && touch.links.empty() && touch.pieces[0].size() == 3 && same(touch.pieces[0][0], {700, 150}) &&
          same(touch.pieces[0][1], {800, 200}) && same(touch.pieces[0][2], {700, 250}), "touching the seam column and turning back is one piece, no seam link");
    const auto zigzag = p.path({g.node_of(0, 3), g.node_of(7, 4), g.node_of(0, 5), g.node_of(7, 6)});
    check(zigzag.pieces.size() == 1 && zigzag.links.empty() && steps_short(zigzag), "zigzag along the seam column without crossing is one piece");
    const auto back_and_forth = p.path({g.node_of(1, 4), g.node_of(0, 4), g.node_of(7, 4), g.node_of(0, 4), g.node_of(1, 4)});
    check(back_and_forth.pieces.size() == 3 && back_and_forth.links.size() == 2 && steps_short(back_and_forth), "crossing the seam twice gives three pieces and no map-wide step");
    // Central meridian 22.5 puts the seam between columns 0 (x = 750) and 1 (x = 50): the crossing is interpolated.
    const Projection shifted(policy_with(22.5), frame(9));
    const auto mid = shifted.path({g.node_of(0, 3), g.node_of(1, 4)});
    check(mid.pieces.size() == 2 && same(mid.pieces[0].front(), {750, 150}) && same(mid.pieces[0].back(), {800, 175}) && same(mid.pieces[1].front(), {0, 175}) &&
          same(mid.pieces[1].back(), {50, 200}), "a seam between columns: the crossing point is interpolated on both edges");
    const auto pole = p.path({g.node_of(2, 1), 0, g.node_of(6, 1)});
    check(pole.pieces.size() == 2 && pole.links.size() == 1 && pole.links[0] == "pole", "a path through the north pole is split with a pole link");
    check(same(pole.pieces[0].back(), {2 * cell, 0}) && same(pole.pieces[1].front(), {6 * cell, 0}) && same(pole.pieces[1].back(), {6 * cell, 50}), "it meets the pole line at its own longitudes");
    const auto south = p.path({g.node_of(3, 7), g.south_pole()});
    check(south.pieces.size() == 1 && same(south.pieces[0].back(), {3 * cell, 400}), "a path ending at the south pole ends on the south pole line");
    const auto starts = p.path({0, g.node_of(5, 1), g.node_of(5, 2)});
    check(starts.pieces.size() == 1 && same(starts.pieces[0].front(), {5 * cell, 0}), "a path starting at a pole starts on the pole line");
    const auto plain = p.path({g.node_of(2, 2), g.node_of(3, 3), g.node_of(4, 3)});
    check(plain.pieces.size() == 1 && plain.links.empty() && plain.pieces[0].size() == 3, "an inland path is one piece");
    refused("non-adjacent step", "non_adjacent_step", [&] { p.path({g.node_of(1, 4), g.node_of(3, 4)}); });
    refused("pole to pole", "non_adjacent_step", [&] { p.path({0, g.south_pole()}); });
    refused("one-node path", "invalid_record", [&] { p.path({g.node_of(1, 4)}); });
}

void policy() {
    const auto p = projection::load_policy(policy_file);
    check(p.type == "periodic-equirectangular" && p.logical_width == 800 && p.height_ratio == 0.5 && p.vertical_scale == 0.005 && p.strip_ratio == 0.5,
          "policy: periodic-equirectangular, W 800, H/W 1/2, 0.005 units per metre (map epic 2), strips W/2");
    check(cjson::sha256_file_hex(calibration_file) == p.calibration_sha256, "policy pins the calibration receipt by SHA-256");
    check(projection::load_policy(policy_file).sha256 == p.sha256, "policy hash is stable");
    const std::string text = slurp(policy_file);
    auto mutated = [&](const std::string& name, const std::string& from, const std::string& to, const std::string& code) {
        const auto at = text.find(from);
        if (at == std::string::npos) { check(false, name + ": text not found"); return; }
        std::string copy = text; copy.replace(at, from.size(), to);
        const auto file = work / ("policy-" + name + ".json");
        spit(file, copy);
        refused("policy " + name, code, [&] { projection::load_policy(file); });
    };
    mutated("unknown_member", "\"version\": 1", "\"version\": 1, \"extra\": 1", "unknown_field");
    mutated("wrong_projection", "\"periodic-equirectangular\"", "\"mercator\"", "unsupported_version");
    mutated("version_2", "\"projectionVersion\": 1", "\"projectionVersion\": 2", "unsupported_version");
    mutated("narrow_strip", "\"stripRatio\": 0.5", "\"stripRatio\": 0.4", "out_of_range");
    mutated("zero_height", "\"heightRatio\": 0.5", "\"heightRatio\": 0", "out_of_range");
    mutated("string_width", "\"logicalWidth\": 800", "\"logicalWidth\": \"800\"", "wrong_type");
    mutated("bad_hash", "\"sha256\": \"", "\"sha256\": \"X", "invalid_hash");
    mutated("calibration_extra", "\"sha256\": \"", "\"extra\": 1, \"sha256\": \"", "unknown_field");
    {
        std::string copy = text; const auto at = copy.find("\"notes\": \"");
        copy.replace(at, std::string("\"notes\": \"").size(), "\"notes\": [\"");
        copy.replace(copy.find("\"", copy.find("[\"") + 2), 1, "\"]");
        const auto file = work / "policy-notes_array.json"; spit(file, copy);
        refused("policy notes_array", "wrong_type", [&] { projection::load_policy(file); });
    }
    // The receipt resolves next to the policy: a copied policy with a missing or altered receipt is refused.
    const fs::path tree = work / "policy-tree";
    fs::remove_all(tree); fs::create_directories(tree / "policies"); fs::create_directories(tree / "calibration" / "axis-sea-datum");
    fs::copy_file(policy_file, tree / "policies" / "projection.json");
    refused("policy with its calibration receipt missing", "missing_file", [&] { projection::load_policy(tree / "policies" / "projection.json"); });
    spit(tree / "calibration" / "axis-sea-datum" / "calibration.json", slurp(calibration_file) + " ");
    refused("policy with an altered calibration receipt", "calibration_mismatch", [&] { projection::load_policy(tree / "policies" / "projection.json"); });
    fs::copy_file(calibration_file, tree / "calibration" / "axis-sea-datum" / "calibration.json", fs::copy_options::overwrite_existing);
    check(projection::load_policy(tree / "policies" / "projection.json").sha256 == p.sha256, "a copied policy with its pinned receipt loads with the same hash");
    // A Policy edited in memory is re-validated and re-hashed by the Projection.
    auto edited = p; edited.strip_ratio = 0.3;
    refused("Projection with an in-memory strip below W/2", "out_of_range", [&] { Projection(edited, frame(9)); });
    auto stale = p; stale.central_meridian_deg = 10; stale.sha256 = p.sha256;
    const Projection restamped(stale, frame(9));
    check(restamped.document().find(p.sha256) == std::string::npos, "an edited policy is re-hashed, not stamped with the file's hash");
}

void identity() {
    const Projection a(policy_with(0), frame(129)), b(policy_with(0), frame(129)), c(policy_with(0), frame(129, 0, 40000)), d(policy_with(0), frame(257));
    check(a.identity() == b.identity() && a.identity().size() == 64, "same inputs, same identity");
    check(a.identity() != c.identity() && a.identity() != d.identity(), "radius and grid side change the identity");
    check(cjson::sha256_hex(a.document()) == a.identity(), "identity = SHA-256 of the canonical document");
    check(a.document().find("\"policySha256\":\"" + projection::load_policy(policy_file).sha256 + "\"") != std::string::npos, "the document names the policy hash");
}

fs::path generator_numbered_fixture() {
    const fs::path dir = work / "projected";
    fs::remove_all(dir);
    fs::copy(fixture / "valid", dir, fs::copy_options::recursive);
    std::string text = slurp(dir / "world.json");
    // The reader fixture numbers nodes 5x + z; renumber to the generator's sphere graph (node_of) for n = 5.
    for (const auto& [from, to] : std::initializer_list<std::pair<const char*, const char*>>{
             {"\"node\": 6,", "\"node\": 2,"}, {"\"node\": 7,", "\"node\": 6,"}, {"\"node\": 8,", "\"node\": 10,"}, {"\"node\": 11,", "\"node\": 3,"},
             {"\"node\": 16,", "\"node\": 4,"}, {"\"node\": 17,", "\"node\": 8,"}, {"\"node\": 13,", "\"node\": 12,"},
             {" \"topology\": {", " \"effective_config\": {\"sea_level\": 0, \"globe_radius\": 31830.99},\n \"topology\": {"}}) {
        const auto at = text.find(from);
        if (at == std::string::npos) throw std::runtime_error(std::string("fixture text not found: ") + from);
        text.replace(at, std::string(from).size(), to);
    }
    spit(dir / "world.json", text);
    return dir;
}

void exported() {
    const auto contract = source::load_compatibility(contract_file);
    refused("export of a world without effective_config.sea_level", "missing_field", [&] { projection::frame_of(source::read_source(fixture / "valid", contract)); });
    try {
        const auto world = source::read_source(generator_numbered_fixture(), contract);
        const Projection p(projection::load_policy(policy_file), projection::frame_of(world));
        const auto out = projection::export_world(world, p);
        const std::string& id = p.identity();
        check(out.identity == id && out.terrain_bin.compare(12, 64, id) == 0, "terrain.bin carries the projection identity");
        check(out.terrain_json.find("\"projectionSha256\":\"" + id + "\"") != std::string::npos &&
              out.objects_json.find("\"projectionSha256\":\"" + id + "\"") != std::string::npos, "terrain.json and objects.json carry the same identity");
        const auto height = world.read_layer("height");
        const auto water = world.read_layer("water_type");
        bool z = out.z.size() == height.values.size(), below = true, codes = out.water.size() == water.values.size();
        for (std::size_t i = 0; z && i < height.values.size(); ++i) {
            z = out.z[i] == p.z(height.values[i]);
            below = below && (out.below[i] == 1) == p.below_datum(height.values[i]);
            codes = codes && out.water[i] == static_cast<std::uint8_t>(water.values[i]);
        }
        check(z && below, "every terrain sample's z and below-datum flag come from the projection");
        check(codes && out.terrain_bin.size() == 76 + 10 * height.values.size(), "the source water class is carried unchanged after the below-datum bytes");
        auto has = [&](Point q) { return out.objects_json.find("\"position\":[" + cjson::ecmascript_number(q.x) + "," + cjson::ecmascript_number(q.y) + "]") != std::string::npos; };
        bool positions = true;
        for (const auto& c : world.cities) positions = positions && has(p.node(c.node));
        for (const auto& h : world.hamlets) { positions = positions && has(p.node(h.node)); if (h.harbour) positions = positions && has(p.node(h.harbour->sea_node)); }
        for (const auto& c : world.castles) positions = positions && has(p.node(c.node));
        for (const auto& k : world.key_locations) positions = positions && has(p.node(k.node));
        check(positions, "every settlement, hamlet, port, castle and key-location position is the projection of its node");
        check(out.objects_json.find("\"ports\":2") != std::string::npos && out.objects_json.find("\"settlements\":2") != std::string::npos, "export counts");
    } catch (const std::exception& e) { check(false, std::string("export of the renumbered fixture: ") + e.what()); }
    refused("export of the reader fixture's synthetic node numbering", "node_mismatch", [&] {
        auto text = slurp(fixture / "valid" / "world.json");
        const fs::path dir = work / "synthetic";
        fs::remove_all(dir); fs::copy(fixture / "valid", dir, fs::copy_options::recursive);
        text.replace(text.find(" \"topology\": {"), 14, " \"effective_config\": {\"sea_level\": 0},\n \"topology\": {");
        spit(dir / "world.json", text);
        const auto world = source::read_source(dir, contract);
        projection::export_world(world, Projection(projection::load_policy(policy_file), projection::frame_of(world)));
    });
}
}  // namespace

int main(int argc, char** argv) {
    if (argc != 6) { std::cerr << "usage: projection_tests POLICY_JSON CALIBRATION_JSON FIXTURE_DIR CONTRACT_JSON WORK_DIR\n"; return 2; }
    policy_file = argv[1]; calibration_file = argv[2]; fixture = argv[3]; contract_file = argv[4]; work = argv[5];
    fs::create_directories(work);
    try { nodes(); forward_inverse(); samples(); paths(); policy(); identity(); exported(); }
    catch (const std::exception& e) { check(false, std::string("unexpected exception: ") + e.what()); }
    std::cout << checks - failures << "/" << checks << " checks passed\n";
    return failures == 0 ? 0 : 1;
}
