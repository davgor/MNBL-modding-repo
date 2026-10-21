// Terrain shaping tests (MMR-278): terrain_shaping_tests PROJECTION_POLICY MATERIALS IMPORT SHAPING WORK_DIR
// A synthetic split world (flat high land, a deep northern sea, a lake whose bed rises above its level, a river cell) compiled at
// the travel side (257) and the fine side (513) through the real policies, with a spike, a ridge and a tilted settlement area set
// into the coarse field: a spike is removed and a ridge kept, the slope bounds hold, sea, lake and river samples are handled per
// the spec, pads lie on capped planes and touching pads merge, pole rows stay single-valued, seam pairs are identical, two runs are
// byte-identical, and bad inputs are refused.
#include <algorithm>
#include <cmath>
#include <filesystem>
#include <functional>
#include <iostream>
#include <set>
#include <string>
#include "worldbridge/terrain_shaping.hpp"

namespace fs = std::filesystem;
using namespace worldbridge;
using terrain::Compiled;
using terrain::SourceLayers;

namespace {
int checks = 0, failures = 0;
fs::path projection_file, materials_file, import_file, shaping_file, work;

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

constexpr std::int64_t kN = 129;
constexpr double kPi = 3.141592653589793;

std::vector<source::BiomeEntry> taxonomy() {
    std::vector<source::BiomeEntry> out;
    for (auto [id, core] : std::initializer_list<std::pair<int, const char*>>{{0, "ocean"}, {1, "tundra"}, {2, "desert"}, {3, "grassland"}, {4, "forest"}, {5, "exposed_rock"},
                                                                             {6, "snow"}, {7, "rainforest"}, {8, "lake"}, {13, "marsh"}, {15, "boreal_forest"}, {16, "cold_tundra"}, {17, "land_ice"}})
        out.push_back({id, core, core});
    return out;
}

// Land at 2000 m (10 units at verticalScale 0.005) south of source row 40, sea at -3000 m north of it; a 3 x 3 lake at rows 80-82,
// columns 60-62 with its bed at 1900 m and its surface at 1800 m (hidden before shaping); a river cell at row 90, column 30.
SourceLayers synthetic(std::vector<double>& surface) {
    SourceLayers L;
    L.side = kN;
    const auto N = static_cast<std::size_t>(kN * kN);
    L.height.resize(N); L.biome.resize(N); L.water_type.resize(N); L.river.resize(N); L.landform.resize(N); L.snow_months.assign(N, 0);
    L.temperature.assign(N, 15.0); L.moisture.assign(N, 0.5); L.ice_cap.assign(N, 0.0); L.rocky_coast.assign(N, 0.0);
    surface.assign(N, 0.0);
    for (std::int64_t r = 0; r < kN; ++r)
        for (std::int64_t c = 0; c < kN; ++c) {
            const auto i = static_cast<std::size_t>(r * kN + c);
            const bool sea = r < 40;
            L.height[i] = sea ? -3000 : 2000; L.water_type[i] = sea ? 1 : 0; L.biome[i] = sea ? 0 : 3; L.landform[i] = sea ? 0 : 1; L.river[i] = 0;
        }
    for (std::int64_t r = 80; r <= 82; ++r)
        for (std::int64_t c = 60; c <= 62; ++c) {
            const auto i = static_cast<std::size_t>(r * kN + c);
            L.height[i] = 1900; L.water_type[i] = 2; L.biome[i] = 8; surface[i] = 1800;
        }
    L.river[static_cast<std::size_t>(90 * kN + 30)] = 1;
    L.biomes = taxonomy();
    L.landforms = {"Submerged", "Plain / hillside", "Mountain / ridge", "Valley"};
    return L;
}

struct World {
    projection::Projection p;
    terrain::MaterialPolicy m;
    terrain::Import import;
    policy::Shaping shaping;
    Compiled coarse, fine;
    terrain::LakeSource lakes;
    std::vector<terrain::Pad> pads;
};

double& at(Compiled& t, int r, int c) { return t.z[static_cast<std::size_t>(r) * t.side + c]; }
double at(const Compiled& t, int r, int c) { return t.z[static_cast<std::size_t>(r) * t.side + c]; }
// A coarse sample and its strip copy (columns 64..191 are canonical; a column c >= 192 or < 64 copies c -/+ 128).
void set_both(Compiled& t, int r, int c, double z) { at(t, r, c) = z; if (c - 128 >= 0) at(t, r, c - 128) = z; if (c + 128 < t.side) at(t, r, c + 128) = z; }

World world() {
    std::vector<double> surface;
    const auto L = synthetic(surface);
    World w{projection::Projection(projection::load_policy(projection_file), {kN, 31830.99, 0, "1"}), terrain::load_materials(materials_file), terrain::load_import(import_file),
            policy::load_shaping(shaping_file), {}, {}, {}, {}};
    w.coarse = terrain::compile(L, w.p, w.m, w.import, 257);
    w.fine = terrain::compile(L, w.p, w.m, w.import, 513);
    w.lakes.side = kN; w.lakes.water_type = L.water_type; w.lakes.water_surface = surface;
    w.lakes.sea_level_m = 0; w.lakes.vertical_scale = projection::load_policy(projection_file).vertical_scale; w.lakes.central_meridian_deg = 0;
    // A spike at (148, 170), a ridge along row 152, columns 70-80, and a tilted settlement area (rows 124-132, columns 136-164, 1 unit per sample east).
    set_both(w.coarse, 148, 170, at(w.coarse, 148, 170) + 20);
    for (int c = 70; c <= 80; ++c) set_both(w.coarse, 152, c, at(w.coarse, 152, c) + 20);
    for (int r = 124; r <= 132; ++r) for (int c = 136; c <= 164; ++c) set_both(w.coarse, r, c, 10 + 1.0 * (c - 136));
    // A town and a village 3 units apart on the tilt, a castle alone on flat land (logical positions).
    w.pads = {{"t1", "town", (150 - 64) * 6.25 + 0.4, (128 - 96) * 6.25 + 0.3}, {"v1", "village", (150 - 64) * 6.25 + 3.4, (128 - 96) * 6.25 + 0.3},
              {"k1", "castle", (100 - 64) * 6.25 + 0.2, (145 - 96) * 6.25 + 0.1}};
    return w;
}

terrain::Shaped run(const World& w, const policy::Shaping& s) { return terrain::shape(w.coarse, w.fine, w.p, w.m, w.import, s, w.pads, w.lakes, {}); }

double face_slope(const Compiled& t, double x, double y) {   // physical position, the main-diagonal triangle under it
    const double gx = x / t.spacing, gy = y / t.spacing;
    const int ix = static_cast<int>(gx), iy = static_cast<int>(gy);
    const double u = gx - ix, v = gy - iy;
    const auto z = [&](int r, int c) { return t.z[static_cast<std::size_t>(r) * t.side + c]; };
    const double dx = u >= v ? z(iy, ix + 1) - z(iy, ix) : z(iy + 1, ix + 1) - z(iy + 1, ix), dy = u >= v ? z(iy + 1, ix + 1) - z(iy, ix + 1) : z(iy + 1, ix) - z(iy, ix);
    return std::atan(std::hypot(dx, dy) / t.spacing) * 180 / kPi;
}

void tests() {
    const World w = world();
    const double eps = w.m.coast_epsilon, sp = 6.25;
    const auto c_at = [&](const std::vector<double>& z, int r, int c) { return z[static_cast<std::size_t>(r) * 257 + c]; };

    // Despike alone: the spike is capped at its lowest neighbour plus the bound; the ridge is kept.
    auto only = w.shaping;
    only.coast.enabled = only.pads.enabled = only.lakes.enabled = only.low_pass.enabled = false;
    const auto d = run(w, only);
    const double bound = std::tan(only.despike.max_slope_deg * kPi / 180) * sp;
    check(c_at(d.coarse_z, 148, 170) == c_at(d.coarse_z, 148, 171) + bound && c_at(d.coarse_z, 148, 42) == c_at(d.coarse_z, 148, 170),
          "a spike is lowered to its lowest neighbour plus tan(maxSlopeDeg) x spacing, on the sample and its strip copy");
    bool ridge = true;
    for (int c = 70; c <= 80; ++c) ridge = ridge && c_at(d.coarse_z, 152, c) == at(w.coarse, 152, c);
    check(ridge, "a ridge (opposite neighbours within the bound) is kept");
    auto flat = only; flat.despike.keep_ridges = false;
    const auto f = run(w, flat);
    check(c_at(f.coarse_z, 152, 75) == at(w.coarse, 152, 75), "without keepRidges a ridge sample that is not an extremum is still kept (only extrema are despiked)");
    check(d.record.spikes_before == 1 && d.record.sweeps >= 1 && d.record.residual == 0, "one isolated spike before; despike converged (residual 0)");

    // Coast alone: land and sea capped by distance from the shoreline (half a step less than dist).
    auto coast = only; coast.despike.enabled = false; coast.coast.enabled = true;
    const auto k = run(w, coast);
    const double tl = std::tan(coast.coast.land_max_slope_deg * kPi / 180), ts = std::tan(coast.coast.sea_max_slope_deg * kPi / 180);
    // Source row 40 starts the land: coarse row 96 + 40 / 2 = 116 is the first land row, 115 the last sea row.
    check(c_at(k.coarse_z, 116, 100) == eps + tl * sp * 0.5 && c_at(k.coarse_z, 117, 100) == eps + tl * sp * 1.5 && c_at(k.coarse_z, 118, 100) == std::min(c_at(w.coarse.z, 118, 100), eps + tl * sp * 2.5) &&
          c_at(k.coarse_z, 119, 100) == c_at(w.coarse.z, 119, 100), "coast envelope: land rises at most tan(landMaxSlopeDeg) per sample over bandSamples, then is untouched");
    check(c_at(k.coarse_z, 115, 100) == -eps - ts * sp * 0.5 && c_at(k.coarse_z, 113, 100) == -eps - ts * sp * 2.5 && c_at(k.coarse_z, 112, 100) == c_at(w.coarse.z, 112, 100),
          "coast envelope: the sea floor deepens at most tan(seaMaxSlopeDeg) per sample from the shore, then is untouched");

    // The whole stage.
    const auto s = run(w, w.shaping);
    const auto& t = s.terrain;
    bool sea = true, land = true, river = false;
    for (std::size_t i = 0; i < t.z.size(); ++i) {
        if (t.intent[i] == w.m.ocean) sea = sea && t.z[i] <= -eps;
        if (t.intent[i] == w.m.land || t.intent[i] == w.m.river) land = land && t.z[i] >= eps;
        river = river || t.intent[i] == w.m.river;
    }
    check(sea && land && river && t.engine_sign_violations == 0, "sea samples stay below -coastEpsilon, land and river samples above it (the river is carried, coherent)");
    check(t.side == 513 && t.spacing == 3.125 && t.period == 256 && t.height.q.size() == 513u * 513u, "the shaped surface is 513 samples at 3.125 units, seam period 256");
    double worst = 0;
    for (int r = 2 * 148 - 3; r <= 2 * 148 + 3; ++r) for (int c = 2 * 170 - 3; c <= 2 * 170 + 3; ++c) worst = std::max(worst, face_slope(t, c * 3.125 + 0.7, r * 3.125 + 0.2));
    check(s.record.spikes_after == 0 && worst <= w.shaping.despike.max_slope_deg, "no isolated spike after shaping; faces around the old spike within maxSlopeDeg (" + std::to_string(worst) + ")");
    // Lakes: the bed went below the level and the runtime plane rule shows the lake.
    std::size_t lakes = 0, shown = 0;
    for (const auto& l : s.record.lakes) if (l.matched) { ++lakes; shown += l.shown_after && l.bed_below_level; }
    check(lakes >= 1 && shown == lakes && s.record.lakes_shown_before == 0 && s.record.lakes_newly_hidden == 0,
          "a lake whose bed rose above its level is hidden before and shown after, its bed below the level (no lake newly hidden)");
    // Pads: the town and the village merge onto one plane capped at maxSlopeDeg; the castle stands alone on flat land.
    const double town = face_slope(t, w.pads[0].x + w.p.strip(), w.pads[0].y + w.p.pole_margin()), village = face_slope(t, w.pads[1].x + w.p.strip(), w.pads[1].y + w.p.pole_margin());
    check(s.record.pads == 3 && s.record.pads_merged == 1, "touching pads merge (3 pads, 1 merge)");
    check(std::abs(town - w.shaping.pads.max_slope_deg) < 1e-6 && std::abs(village - w.shaping.pads.max_slope_deg) < 1e-6,
          "a pad on a 9 degree tilt lies on its plane, capped at maxSlopeDeg (" + std::to_string(town) + ")");
    check(face_slope(t, w.pads[2].x + w.p.strip(), w.pads[2].y + w.p.pole_margin()) < 1e-9 && s.record.pads_over == 0, "a pad on flat land is flat; no pad over steepPadDeg");
    // Poles and seams.
    const auto row_values = [&](int r) { std::set<double> v; for (int c = 0; c < 513; ++c) v.insert(t.z[static_cast<std::size_t>(r) * 513 + c]); return v; };
    check(row_values(192).size() == 1 && row_values(320).size() == 1 && row_values(0) == row_values(192) && row_values(512) == row_values(320),
          "pole rows stay single-valued and the padding repeats them");
    bool pairs = true;
    for (int r = 0; r < 513; ++r)
        for (int c = 0; c + 256 < 513; ++c) {
            const auto a = static_cast<std::size_t>(r) * 513 + c, b = a + 256;
            pairs = pairs && t.z[a] == t.z[b] && t.height.q[a] == t.height.q[b] && t.intent[a] == t.intent[b];
            for (const auto& plane : t.weights) pairs = pairs && plane[a] == plane[b];
        }
    check(pairs && t.seam_pairs == 513u * 257u && t.weights.size() == 15, "seam pairs identical in height, 16-bit, intent and every one of the 15 layer planes");
    // Determinism.
    const auto again = run(w, w.shaping);
    check(terrain::heightfield_bytes(again.terrain) == terrain::heightfield_bytes(t) && terrain::weights_bytes(again.terrain) == terrain::weights_bytes(t) &&
          terrain::q16_bytes(again.terrain) == terrain::q16_bytes(t) && terrain::masks_bytes(again.terrain) == terrain::masks_bytes(t) &&
          cjson::canonical(terrain::shaping_value(again.record)) == cjson::canonical(terrain::shaping_value(s.record)), "two runs are byte-identical (surface files and record)");
    // The record names the policy and carries the schema's members.
    const auto doc = terrain::shaping_value(s.record);
    bool members = true;
    for (const char* key : {"policySha256", "sweeps", "residual", "samplesChanged", "interiorSlope", "coastSlope", "crease", "pads", "lakes", "navCoastMismatch"}) members = members && doc.find(key);
    check(members && doc.find("policySha256")->text == w.shaping.sha256, "the shaping record has the terrain-metadata shaping members and the policy hash");

    // MMR-285: the runtime plane rule counts no lake sample as shore, so two groups in each other's boxes (diagonal neighbours) both show.
    {
        const int n9 = 9;
        std::vector<double> h(n9 * n9, 2.0);
        std::vector<std::uint8_t> in(n9 * n9, 0);
        h[3 * n9 + 3] = 0.5; in[3 * n9 + 3] = 2; h[4 * n9 + 4] = 0.4; in[4 * n9 + 4] = 2;
        terrain::LakeSource src;
        src.side = 2; src.water_type.assign(4, 2); src.water_surface.assign(4, 100); src.sea_level_m = 0; src.vertical_scale = 0.01;
        const auto r = terrain::lake_rule(h, in, n9, 1.0, 9.0, 0, 0, 9.0, src);
        check(r.size() == 2 && r[0].shown_after && r[1].shown_after && r[0].z == 1.0 && r[1].z == 1.0 && r[0].shore_min_z == 2.0 && r[1].shore_min_z == 2.0,
              "two lake groups in each other's boxes both show at their level (another lake's sample is not shore)");
    }

    // Refusals.
    auto coarse_only = w.shaping; coarse_only.resolution.fine = false;
    refused("a policy with resolution.fine false", "out_of_range", [&] { run(w, coarse_only); });
    refused("a fine compile that is not 2 x the coarse", "grid_mismatch", [&] { terrain::shape(w.coarse, w.coarse, w.p, w.m, w.import, w.shaping, w.pads, w.lakes, {}); });
    refused("a pad kind without a radius", "unmapped_category", [&] { auto x = w; x.pads.push_back({"h1", "hideout", 100, 100}); run(x, x.shaping); });
    auto strict = w.import; strict.engine_truncation = 0.06;
    refused("a coastline sign the engine's truncation would flip", "coast_sign", [&] { terrain::shape(w.coarse, w.fine, w.p, w.m, strict, w.shaping, w.pads, w.lakes, {}); });
    auto broken = w; broken.lakes.water_surface.pop_back();
    refused("lake layers that do not match the source grid", "broken_join", [&] { run(broken, broken.shaping); });
}
}  // namespace

int main(int argc, char** argv) {
    if (argc != 6) { std::cerr << "usage: terrain_shaping_tests PROJECTION_POLICY MATERIALS IMPORT SHAPING WORK_DIR\n"; return 2; }
    projection_file = argv[1]; materials_file = argv[2]; import_file = argv[3]; shaping_file = argv[4]; work = argv[5];
    fs::create_directories(work);
    try { tests(); }
    catch (const std::exception& e) { check(false, std::string("unexpected exception: ") + e.what()); }
    std::cout << checks - failures << "/" << checks << " checks passed\n";
    return failures == 0 ? 0 : 1;
}
