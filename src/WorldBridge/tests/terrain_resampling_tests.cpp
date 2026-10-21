// Terrain compiler tests (MMR-26): terrain_resampling_tests PROJECTION_POLICY MATERIALS IMPORT WORK_DIR
// Synthetic split-world layers through the real projection policy, material policy and import calibration:
// control points, bilinear midpoints, paired seam columns, categorical purity, 16-bit roundtrip and the zero-range
// case, shared tile edges, raster orientation, coastline coherence, unknown categories and domain refusals, fidelity; the layer
// compile (MMR-255: weights v2, the over-compositing alphas, seams on every plane, permanent white, the table check) and map trees.
#include <cmath>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iostream>
#include <set>
#include <algorithm>
#include <sstream>
#include <string>
#include "worldbridge/terrain_compiler.hpp"
#include "worldbridge/terrain_layers.hpp"

namespace fs = std::filesystem;
using namespace worldbridge;
using terrain::Compiled;
using terrain::SourceLayers;

namespace {
int checks = 0, failures = 0;
fs::path projection_file, materials_file, import_file, work;

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
void spit(const fs::path& p, const std::string& text) { std::ofstream out(p, std::ios::binary | std::ios::trunc); out << text; }

std::vector<source::BiomeEntry> taxonomy() {
    std::vector<source::BiomeEntry> out;
    for (auto [id, core] : std::initializer_list<std::pair<int, const char*>>{{0, "ocean"}, {1, "tundra"}, {2, "desert"}, {3, "grassland"}, {4, "forest"}, {5, "exposed_rock"},
                                                                             {6, "snow"}, {7, "rainforest"}, {8, "lake"}, {13, "marsh"}, {15, "boreal_forest"}, {16, "cold_tundra"}, {17, "land_ice"}})
        out.push_back({id, core, core});
    return out;
}

// A periodic synthetic world: a south-going slope with a sinusoidal ridge, ocean where the height is below 0, poles constant.
SourceLayers synthetic(std::int64_t n) {
    SourceLayers L;
    L.side = n;
    const auto N = static_cast<std::size_t>(n * n);
    L.height.resize(N); L.biome.resize(N); L.water_type.resize(N); L.river.resize(N); L.landform.resize(N); L.snow_months.assign(N, 0);
    L.temperature.resize(N); L.moisture.resize(N); L.ice_cap.resize(N); L.rocky_coast.resize(N);
    const double pi = 3.141592653589793;
    for (std::int64_t r = 0; r < n; ++r)
        for (std::int64_t c = 0; c < n; ++c) {
            const auto i = static_cast<std::size_t>(r * n + c);
            const auto cc = c == n - 1 ? 0 : c;  // duplicate seam column
            double h = 1000.0 * (static_cast<double>(r) / (n - 1)) - 330 + 2.0 * static_cast<double>(r * (n - 1 - r)) * std::sin(2 * pi * cc / (n - 1));
            if (r == 0) h = -330; if (r == n - 1) h = 670;  // single pole values
            L.height[i] = h;
            const bool ocean = h < 0;
            L.water_type[i] = ocean ? 1 : 0;
            // Grassland, with a forest quarter (MMR-255 trees), then desert, then land ice.
            L.biome[i] = ocean ? 0 : r < n / 2 ? (cc < (n - 1) / 2 ? 4 : 3) : r < 3 * n / 4 ? 2 : 17;
            L.landform[i] = ocean ? 0 : (h > 400 ? 2 : 1);
            L.river[i] = 0;
            L.temperature[i] = L.biome[i] == 17 ? -20 : 15;
            L.moisture[i] = cc >= (n - 1) / 2 && cc < (n - 1) / 2 + 6 ? 0.1 : 0.9;   // a dry grassland band, wet elsewhere
            L.ice_cap[i] = L.biome[i] == 17 ? 1 : 0;
            L.rocky_coast[i] = 0;
            if (!ocean && (L.biome[i] == 17 || cc == 5)) L.snow_months[i] = 12;   // land ice, and a meridian of land whose snow never melts (MMR-254)
            else if (!ocean) L.snow_months[i] = static_cast<std::uint8_t>(r % 12);
        }
    // A lake and a river cell on land (not on the seam, not on a pole row).
    const auto lake = static_cast<std::size_t>((3 * n / 4 - 1) * n + n / 3), river = static_cast<std::size_t>((3 * n / 4 - 2) * n + n / 3);
    L.water_type[lake] = 2; L.biome[lake] = 8; L.river[river] = 1;
    L.biomes = taxonomy();
    L.landforms = {"Submerged", "Plain / hillside", "Mountain / ridge", "Valley"};
    return L;
}

projection::Projection projection_for(std::int64_t n, double sea = 0) { return projection::Projection(projection::load_policy(projection_file), {n, 31830.99, sea, "1"}); }

void layer_tests(const SourceLayers& L, const projection::Projection& p, const terrain::MaterialPolicy& m, const terrain::Import& import, const Compiled& t);

void compile_tests() {
    const auto materials = terrain::load_materials(materials_file);
    const auto import = terrain::load_import(import_file);
    const auto L = synthetic(33);
    const auto p = projection_for(33);
    const Compiled t = terrain::compile(L, p, materials, import, 129);
    check(t.extent == 1600 && t.spacing == 12.5 && t.period == 64, "square 1600 domain, 129 samples, spacing 12.5, seam period 64 columns");
    // Control points: output samples that sit on source samples carry the source value (z and categories).
    std::size_t control = 0, exact = 0, categories = 0, white_rule = 0, white_melts = 0;
    for (int r = 0; r < 129; ++r)
        for (int c = 0; c < 129; ++c) {
            const double y = r * 12.5 - p.pole_margin(), x = c * 12.5 - p.strip();
            if (y < 0 || y > p.height()) continue;
            const auto g = p.inverse({x, y});
            const double gc = (g.longitude + 180) / 360 * 32, gr = (90 - g.latitude) / 180 * 32;
            if (std::abs(gc - std::round(gc)) > 1e-9 || std::abs(gr - std::round(gr)) > 1e-9) continue;
            const auto sc = static_cast<std::int64_t>(std::llround(gc)) % 32, sr = static_cast<std::int64_t>(std::llround(gr));
            const auto si = static_cast<std::size_t>(sr * 33 + sc), oi = static_cast<std::size_t>(r * 129 + c);
            const double z = p.z(L.height[si]);
            if (std::abs(z) <= materials.coast_epsilon) continue;
            ++control;
            exact += t.z[oi] == z;
            categories += t.biome[oi] == static_cast<std::uint8_t>(L.biome[si]) && t.landform[oi] == static_cast<std::uint8_t>(L.landform[si]);
            const bool land = L.water_type[si] == 0 || L.river[si] != 0;
            white_rule += t.white[oi] == ((L.biome[si] == 17 || (land && L.snow_months[si] >= 12)) ? 1 : 0);
            white_melts += L.biome[si] != 17 && land && L.snow_months[si] >= 12 && t.white[oi] == 1;
        }
    check(control > 2000 && exact == control, "control points: every output sample on a source sample has the source height exactly (" + std::to_string(control) + ")");
    check(categories == control, "control points: categorical masks equal the source categories");
    check(white_rule == control && white_melts > 0, "permanent white (MMR-254): land_ice, and land whose snow lasts 12 months (" + std::to_string(white_melts) + " control points of another biome), and nothing else");
    // Midpoints: output columns between two source columns are the bilinear mean.
    {
        const int r = 64;  // physical y = 800, logical y = 200, source row 16
        bool mean = true; int tested = 0;
        for (int c = 33; c < 64; c += 2) {  // odd output columns fall half way between source columns
            const auto g = p.inverse({c * 12.5 - p.strip(), r * 12.5 - p.pole_margin()});
            const double gc = (g.longitude + 180) / 360 * 32;
            const auto c0 = static_cast<std::int64_t>(std::floor(gc));
            const double a = p.z(L.height[static_cast<std::size_t>(16 * 33 + c0)]), b = p.z(L.height[static_cast<std::size_t>(16 * 33 + c0 + 1)]);
            if (std::abs(a) <= .05 || std::abs(b) <= .05 || (a > 0) != (b > 0)) continue;  // coast pairs are coherence-adjusted
            mean = mean && std::abs(t.z[static_cast<std::size_t>(r * 129 + c)] - (a + b) / 2) < 1e-9; ++tested;
        }
        check(mean && tested > 5, "midpoints between source columns are the bilinear mean");
    }
    // Paired seam columns.
    bool pairs = true;
    for (int r = 0; r < 129; ++r)
        for (int c = 0; c + 64 < 129; ++c) {
            const auto a = static_cast<std::size_t>(r * 129 + c), b = a + 64;
            pairs = pairs && t.z[a] == t.z[b] && t.height.q[a] == t.height.q[b] && t.biome[a] == t.biome[b] && t.intent[a] == t.intent[b];
            for (const auto& plane : t.weights) pairs = pairs && plane[a] == plane[b];
        }
    check(pairs && t.seam_pairs == static_cast<std::size_t>(129 * 65) && terrain::check_layer_seams(t) == static_cast<std::size_t>(129 * 65),
          "paired east/west seam columns match for height, 16-bit, biome, intent and every layer plane");
    // Categorical purity: only source codes appear.
    std::set<int> source_biomes, out_biomes(t.biome.begin(), t.biome.end());
    for (double b : L.biome) source_biomes.insert(static_cast<int>(b));
    bool subset = true; for (int b : out_biomes) subset = subset && source_biomes.count(b);
    check(subset, "biome codes are copied, never blended");
    // 16-bit roundtrip and tiles.
    check(t.max_roundtrip_error <= t.height.step / 2 * (1 + 1e-9), "16-bit roundtrip error within half a step");
    check(t.padding_rows == 96 && t.padding[0] == 1 && t.padding[static_cast<std::size_t>(64 * 129)] == 0, "96 padding rows flagged beyond the pole lines; rows 48-80 are the globe");
    check(t.north_pole_intent == materials.ocean && t.south_pole_intent == materials.land && t.lake_below_datum == 0, "pole intents recorded (ocean north, land south); no lake below the datum");
    // Orientation: row 0 is the north padding (the north pole value); rows increase southwards.
    const double north = p.z(-330), south = p.z(670);
    check(t.z[0] == north && t.z[128] == north && t.z[static_cast<std::size_t>(128 * 129)] == south, "row 0 repeats the north pole, the last row the south pole");
    check(t.z[static_cast<std::size_t>((48 + 8) * 129 + 70)] < t.z[static_cast<std::size_t>((48 + 24) * 129 + 70)], "+Y runs south: heights grow with rows on the synthetic slope");
    // Coherence and engine sign.
    bool coherent = true;
    for (std::size_t i = 0; i < t.z.size(); ++i) {
        if (t.intent[i] == materials.ocean) coherent = coherent && t.z[i] <= -materials.coast_epsilon;
        if (t.intent[i] == materials.land || t.intent[i] == materials.river) coherent = coherent && t.z[i] >= materials.coast_epsilon;
    }
    check(coherent && t.engine_sign_violations == 0 && (t.lowered + t.raised) > 0, "coastline coherent with the datum after coherence, and after 16-bit and 0.03 truncation");
    check(t.intent_counts.count(materials.lake) && t.intent_counts.count(materials.river), "lake and river intents survive as distinct classes");
    // The slope share ramp.
    check(terrain::slope_share(materials, 2 * materials.slope_full) == 1 && terrain::slope_share(materials, materials.slope_start) == 0 &&
          std::abs(terrain::slope_share(materials, materials.slope_start + 0.25 * (materials.slope_full - materials.slope_start)) - 0.25) < 1e-12,
          "slope share: 0 at start, 1 at full, a quarter up the ramp 0.25");
    layer_tests(L, p, materials, import, t);
    check(terrain::intent(materials, 0, 1) == materials.river && terrain::intent(materials, 2, 1) == materials.lake && terrain::intent(materials, 1, 0) == materials.ocean,
          "river overrides land only; lake and ocean keep their class");
    // Fidelity: n = 33 at 129 samples puts two output samples per source cell across and one along.
    check(t.fidelity.output_per_source_x == 2 && t.fidelity.output_per_source_y == 1 && t.fidelity.land_components_lost == 0, "fidelity: 2 x 1 output samples per source cell, no land lost");
    check(t.fidelity.source_samples == 994 && t.fidelity.source_samples_represented == 994, "fidelity: all 994 distinct source samples are represented exactly");
    // n = 65 at 129: rows are decimated 2:1, so a one-sample island on an odd source row is lost and reported.
    auto D = synthetic(65);
    for (int r = 8; r <= 10; ++r)
        for (int c = 19; c <= 21; ++c) {  // an ocean patch around a one-sample island on odd source row 9
            const auto i = static_cast<std::size_t>(r * 65 + c);
            const bool centre = r == 9 && c == 20;
            D.height[i] = centre ? 50 : -100; D.water_type[i] = centre ? 0 : 1; D.biome[i] = centre ? 3 : 0; D.landform[i] = centre ? 1 : 0;
        }
    const Compiled d = terrain::compile(D, projection_for(65), materials, import, 129);
    check(d.fidelity.output_per_source_y == 0.5 && d.fidelity.land_components_lost >= 1 && d.fidelity.land_samples_lost >= 1, "fidelity: decimated rows report the lost one-sample island");
    check(d.fidelity.height_rms > 0, "fidelity: height loss at unrepresented source samples is measured");
    // Even source rows only: both poles plus 31 interior rows of 64 columns, out of 2 + 63 * 64.
    check(d.fidelity.source_samples == 4034 && d.fidelity.source_samples_represented == 1986, "fidelity: decimated rows leave 2048 of 4034 source samples unrepresented");
    // A central meridian off the source columns: bilinear everywhere, seam pairs still exact.
    auto shifted_policy = projection::load_policy(projection_file); shifted_policy.central_meridian_deg = 22.5;
    const Compiled s = terrain::compile(L, projection::Projection(shifted_policy, {33, 31830.99, 0, "1"}), materials, import, 129);
    check(s.seam_pairs == static_cast<std::size_t>(129 * 65) && s.z != t.z, "central meridian 22.5 compiles with exact seam pairs");
    // A W that is not a dyadic number: logical x comes from the canonical column, so seam pairs still share bits.
    auto odd_policy = projection::load_policy(projection_file); odd_policy.logical_width = 800.1;
    const Compiled o = terrain::compile(L, projection::Projection(odd_policy, {33, 31830.99, 0, "1"}), materials, import, 129);
    check(o.period == 64 && o.seam_pairs == static_cast<std::size_t>(129 * 65), "W = 800.1 compiles with exact seam pairs");
    // Same inputs, same output.
    const Compiled again = terrain::compile(L, p, materials, import, 129);
    check(again.z == t.z && again.weights == t.weights && again.height.q == t.height.q && terrain::weights_bytes(again) == terrain::weights_bytes(t), "compilation is deterministic");
}

// The composited coverage of each slot at sample i (alpha-over from the highest slot down).
std::vector<double> coverage(const Compiled& t, std::size_t i) {
    std::vector<double> c(t.weights.size());
    double rem = 1;
    for (std::size_t k = t.weights.size(); k-- > 1;) { const double a = t.weights[k][i] / 255.0; c[k] = a * rem; rem *= 1 - a; }
    c[0] = rem;
    return c;
}
int slot(const terrain::MaterialPolicy& m, const std::string& name) { for (const auto& l : m.layer_set) if (l.name == name) return l.slot; return -1; }

void layer_tests(const SourceLayers& L, const projection::Projection& p, const terrain::MaterialPolicy& m, const terrain::Import& import, const Compiled& t) {
    const std::size_t n = 129 * 129;
    bool shape = t.weights.size() == m.layer_set.size() && t.layer_names.size() == m.layer_set.size();
    for (std::size_t k = 0; shape && k < t.weights.size(); ++k) shape = t.weights[k].size() == n && t.layer_names[k] == m.layer_set[k].name;
    check(shape && std::all_of(t.weights[0].begin(), t.weights[0].end(), [](std::uint8_t v) { return v == 255; }),
          "one alpha plane per layerSet slot, named in slot order; the base plane is 255 everywhere");
    const auto bytes = terrain::weights_bytes(t);
    std::uint32_t side = 0, count = 0; std::memcpy(&side, bytes.data() + 8, 4); std::memcpy(&count, bytes.data() + 12, 4);
    std::size_t names = 16; for (const auto& name : t.layer_names) names += 4 + name.size();
    check(bytes.compare(0, 8, "MNBLWT02") == 0 && side == 129 && count == m.layer_set.size() && bytes.size() == names + count * n,
          "weights.bin v2: MNBLWT02, side, layer count, the name table, then the planes");
    // The over-compositing inverse: the stored alphas composite back to the target fractions (interior samples equal the table row).
    check(t.layers.interior > 100 && t.layers.interior_max_error <= 0.02, "plain interior samples composite to their biome row within 0.02 (" + std::to_string(t.layers.interior) +
          " samples, max error " + std::to_string(t.layers.interior_max_error) + ")");
    const int sand = slot(m, "sand"), arid = slot(m, "arid_soil"), white = slot(m, "permanent_snow"), grass = slot(m, "grass"), steppe = slot(m, "steppe"), rock = slot(m, "rock");
    const int forest = slot(m, "forest_floor");
    // A desert interior sample: sand 0.6, arid soil 0.4 (sand at slot 6 under arid soil at slot 7: a_7 = 0.4 / 1.0, a_6 = 0.6 / 0.6).
    bool desert = false, ice = false, kernel = false, wet_dry = false, dry = false, steep = false;
    for (std::size_t i = 0; i < n; ++i) {
        const auto c = coverage(t, i);
        if (!desert && t.biome[i] == 2 && t.weights[arid][i] == 102 && t.weights[sand][i] == 255 && std::abs(c[sand] - 0.6) < 0.005 && std::abs(c[arid] - 0.4) < 0.005) desert = true;
        if (t.biome[i] == 17 && t.intent[i] == m.land) ice = ice || (terrain::dominant_layer(t.weights, i) == white && t.weights[white][i] == 255);
        if (t.biome[i] == 3 && c[sand] > 0.01 && c[grass] + c[steppe] > 0.01) kernel = true;
    }
    check(desert, "a desert interior sample stores sand 255 under arid soil 102 and composites to 0.6 / 0.4");
    check(ice && t.layers.white_samples > 0 && t.layers.white_biome > 0, "land ice is permanent white: all permanent_snow, the dominant layer (" + std::to_string(t.layers.white_samples) + " canonical samples)");
    check(kernel, "kernel smoothing blends desert sand into grassland next to the biome edge");
    {
        // Moisture on flat grassland: wet (0.9) is all grass, at the split (0.5) the table's 0.7 / 0.3, dry (0.1) all steppe.
        Compiled u = t;
        for (std::size_t i = 0; i < n; ++i) {
            const int c = static_cast<int>(i % 129);
            u.biome[i] = 3; u.intent[i] = static_cast<std::uint8_t>(m.land); u.z[i] = 1; u.landform[i] = 1; u.temperature[i] = 15; u.ice_cap[i] = 0; u.snow_months[i] = 0;
            u.moisture[i] = c < 43 ? 0.9 : c < 86 ? 0.5 : 0.1;
        }
        terrain::compile_layers(u, m);
        const auto wet = coverage(u, 64 * 129 + 20), split = coverage(u, 64 * 129 + 64), drier = coverage(u, 64 * 129 + 108);
        wet_dry = std::abs(wet[grass] - 1) < 1e-9 && std::abs(split[grass] - 0.7) < 0.003 && std::abs(split[steppe] - 0.3) < 0.003;
        dry = std::abs(drier[steppe] - 1) < 1e-9 && drier[grass] < 1e-9;
    }
    check(wet_dry && dry, "moisture: wet grassland is all grass, at the split the table's 0.7 / 0.3, dry grassland all steppe");
    {
        // A cliff on flat land: the slope share mixes in the rock layer (desert_rock on a Desert face).
        Compiled u = t;
        std::size_t at = 0;
        for (std::size_t i = 0; i < n && !at; ++i) if (u.biome[i] == 3 && u.intent[i] == m.land && i % 129 > 40 && i % 129 < 90 && i / 129 > 50 && i / 129 < 60) at = i;
        u.z[at] += 50;
        terrain::compile_layers(u, m);
        steep = at && u.weights[rock][at - 1] > 0 && u.weights[rock][at + 1] > 0 && t.weights[rock][at - 1] == 0;
    }
    check(steep, "a cliff takes the rock layer by the slope share");
    // Dominant layer: ties go to the higher slot; the base wins only when it covers strictly more.
    std::vector<std::vector<std::uint8_t>> w(3, std::vector<std::uint8_t>(4, 0));
    w[0] = {255, 255, 255, 255}; w[1] = {0, 255, 128, 0}; w[2] = {0, 100, 255, 100};
    check(terrain::dominant_layer(w, 0) == 0 && terrain::dominant_layer(w, 1) == 1 && terrain::dominant_layer(w, 2) == 2 && terrain::dominant_layer(w, 3) == 0,
          "dominant layer: base when nothing covers it, alpha-over from the highest slot, the base when it covers more");
    // Flora: the forest quarter gets trees, at every periodic image, clear of a pad; the same seed plants the same trees.
    check(t.flora_share.size() == n && std::any_of(t.flora_share.begin(), t.flora_share.end(), [](double v) { return v > 0; }), "the forest quarter carries a flora share");
    terrain::FloraInput in;
    in.strip = p.strip(); in.pole_margin = p.pole_margin(); in.width = p.width(); in.height = p.height(); in.seed = 42;
    // A pad in the middle of the forest quarter.
    double px = 0, py = 0; bool found = false;
    for (int r = 0; r < 129 && !found; ++r)
        for (int c = 40; c < 104 && !found; ++c) { const auto i = static_cast<std::size_t>(r) * 129 + c; if (t.flora_share[i] > 0.5) { px = c * t.spacing - p.strip(); py = r * t.spacing - p.pole_margin(); found = true; } }
    in.pads.push_back({px, py, 20.0});
    terrain::FloraRecord rec;
    const auto flora = terrain::flora_bytes(t, m, in, rec);
    std::uint32_t payload = 0, trees = 0; std::memcpy(&payload, flora.data() + 4, 4); std::memcpy(&trees, flora.data() + 8, 4);
    std::size_t pos = 12, near = 0, outside = 0, parsed = 0;
    while (pos < flora.size()) {
        std::uint32_t len = 0; std::memcpy(&len, flora.data() + pos, 4); pos += 4 + len + 4;
        float f[16]; std::memcpy(f, flora.data() + pos, 64); pos += 80; ++parsed;
        const double lx = f[12] - p.strip(), ly = f[13] - p.pole_margin();
        double dx = std::fmod(std::abs(lx - px), p.width()); dx = std::min(dx, p.width() - dx);
        if (dx * dx + (ly - py) * (ly - py) < 19.9 * 19.9) ++near;
        if (f[12] < 0 || f[12] > t.extent || ly < 0 || ly > p.height()) ++outside;
    }
    check(found && flora.compare(0, 4, "FLR2") == 0 && payload == flora.size() - 8 && trees == rec.trees && parsed == trees && pos == flora.size() && rec.canonical > 100,
          "flora.bin is FLR2 with its payload size and count (" + std::to_string(rec.trees) + " trees, " + std::to_string(rec.canonical) + " canonical)");
    check(near == 0 && rec.cleared_pads > 0 && outside == 0, "no tree within a pad's radius, every tree inside the domain");
    check(rec.trees > rec.canonical && rec.trees <= 2 * rec.canonical && rec.biomes.count("forest") && !rec.biomes.count("grassland"), "trees on the forest biome only, at one or two periodic images each");
    terrain::FloraRecord again;
    check(terrain::flora_bytes(t, m, in, again) == flora, "the same world plants the same trees");
    auto capped = m; capped.flora.max_trees = rec.trees / 4;
    terrain::FloraRecord thin;
    const auto fewer = terrain::flora_bytes(t, capped, in, thin);
    check(thin.density_factor < 1 && thin.trees <= capped.flora.max_trees, "maxTrees thins the density instead of rejecting the world");
    (void)fewer; (void)forest; (void)L; (void)import;
}

void quantize_tests() {
    // With the odd multipliers 65535 and 255 the only exact tie is the midpoint; half to even gives the even code.
    check(terrain::quantize({0.0, 0.5, 1.0}).q[1] == 32768, "quantization tie 32767.5 -> 32768 (half to even, as the encoder)");
    auto ramp = terrain::load_materials(materials_file); ramp.slope_start = 0; ramp.slope_full = 2;
    check(terrain::slope_share(ramp, 1.0) == 0.5, "slope share half way up the ramp is 0.5");
    const auto zero = terrain::quantize(std::vector<double>(10, 3.0));
    check(zero.zero_range && zero.hmin == 3 && zero.hmax == 4 && zero.q[0] == 0 && zero.value(5) == 3.0, "zero-height-range special case: [hmin, hmin + 1], q = 0, exact");
    const auto q = terrain::quantize({-1.2345, 0.0, 2.5, 7.0005});
    check(q.hmin == -1.235 && q.hmax == 7.001 && q.q[0] == 4 && q.q[3] == 65531, "global range rounded outward to 0.001 (encoder rule), round-half-even codes");
    bool half = true; for (std::size_t i = 0; i < 4; ++i) half = half && std::abs(q.value(i) - std::vector<double>{-1.2345, 0.0, 2.5, 7.0005}[i]) <= q.step / 2;
    check(half, "reconstruction within half a step");
}

void refusal_tests() {
    const auto materials = terrain::load_materials(materials_file);
    const auto import = terrain::load_import(import_file);
    const auto p = projection_for(33);
    auto with = [&](const std::function<void(SourceLayers&)>& f) { auto L = synthetic(33); f(L); return L; };
    refused("unknown biome id in the raster", "unmapped_category", [&] { terrain::compile(with([](SourceLayers& L) { L.biome[33 * 20 + 5] = 9; }), p, materials, import, 129); });
    refused("unknown water class", "unmapped_category", [&] { terrain::compile(with([](SourceLayers& L) { for (auto& v : L.water_type) if (v == 0) { v = 7; break; } }), p, materials, import, 129); });
    refused("unknown river code", "unmapped_category", [&] { terrain::compile(with([](SourceLayers& L) { L.river[33 * 20 + 5] = 2; for (auto& v : L.river) v = v == 0 ? 2 : v; }), p, materials, import, 129); });
    refused("landform outside the list", "unmapped_category", [&] { terrain::compile(with([](SourceLayers& L) { for (auto& v : L.landform) v = 4; }), p, materials, import, 129); });
    refused("exported biome with another core name", "unmapped_category", [&] { terrain::compile(with([](SourceLayers& L) { L.biomes[9].core = "swamp"; }), p, materials, import, 129); });
    refused("exported biome the policy does not map", "unmapped_category", [&] { terrain::compile(with([](SourceLayers& L) { L.biomes.push_back({20, "glass", "Glass"}); }), p, materials, import, 129); });
    refused("exported landforms differ", "unmapped_category", [&] { terrain::compile(with([](SourceLayers& L) { L.landforms.pop_back(); }), p, materials, import, 129); });
    refused("non-integral category value", "out_of_range", [&] { terrain::compile(with([](SourceLayers& L) { for (auto& v : L.biome) v = v + 0.5; }), p, materials, import, 129); });
    refused("output side the calibration does not list", "out_of_range", [&] { terrain::compile(synthetic(33), p, materials, import, 65); });
    auto narrow = projection::load_policy(projection_file); narrow.pole_margin_ratio = 0.5;
    refused("non-square physical domain", "non_square_domain", [&] { terrain::compile(synthetic(33), projection::Projection(narrow, {33, 31830.99, 0, "1"}), materials, import, 129); });
    auto skewed = projection::load_policy(projection_file); skewed.strip_ratio = 0.6; skewed.pole_margin_ratio = 0.85;
    refused("seam not on output columns", "seam_not_on_grid", [&] { terrain::compile(synthetic(33), projection::Projection(skewed, {33, 31830.99, 0, "1"}), materials, import, 129); });
    refused("raster biome missing from the exported catalogue", "unmapped_category", [&] { terrain::compile(with([](SourceLayers& L) {
        std::erase_if(L.biomes, [](const source::BiomeEntry& b) { return b.id == 8; }); }), p, materials, import, 129); });
    auto strict = import; strict.engine_truncation = 0.06;  // a truncation larger than coastEpsilon cannot keep the sign
    refused("coastline sign lost to the engine's truncation", "coast_sign", [&] { terrain::compile(synthetic(33), p, materials, strict, 129); });
    auto narrow_extent = import; narrow_extent.extent_max = 1000;
    refused("extent beyond the calibrated range", "out_of_range", [&] { terrain::compile(synthetic(33), p, materials, narrow_extent, 129); });
    auto low = import; low.height_limit = 1;
    refused("heights beyond the calibrated limit", "out_of_range", [&] { terrain::compile(synthetic(33), p, materials, low, 129); });
    auto few = import; few.weight_slots = 4;
    refused("more layers than the engine's weight slots", "out_of_range", [&] { terrain::compile(synthetic(33), p, materials, few, 129); });
    refused("climate layers of the wrong size", "broken_join", [&] { terrain::compile(with([](SourceLayers& L) { L.moisture.pop_back(); }), p, materials, import, 129); });
    refused("short snow months", "broken_join", [&] { terrain::compile(with([](SourceLayers& L) { L.snow_months.pop_back(); }), p, materials, import, 129); });
    // Policy file strictness.
    const std::string text = slurp(materials_file);
    auto mutated = [&](const std::string& name, const std::string& from, const std::string& to, const std::string& code) {
        const auto at = text.find(from);
        if (at == std::string::npos) { check(false, name + ": text not found"); return; }
        std::string copy = text; copy.replace(at, from.size(), to);
        const auto file = work / ("materials-" + name + ".json"); spit(file, copy);
        refused("material policy " + name, code, [&] { terrain::load_materials(file); });
    };
    mutated("unknown_member", "\"version\": 2,", "\"version\": 2, \"extra\": 1,", "unknown_field");
    mutated("version_1", "\"version\": 2,", "\"version\": 1,", "unsupported_version");
    mutated("duplicate_biome", "{\"id\": 1, \"core\": \"tundra\"", "{\"id\": 0, \"core\": \"tundra\"", "duplicate_id");
    mutated("biome_rock_gone", "\"core\": \"grassland\",", "\"core\": \"grassland\", \"rock\": 0,", "unknown_field");
    mutated("channels_gone", "\"version\": 2,", "\"version\": 2, \"channels\": [\"grass\", \"rock_k\"],", "unknown_field");
    mutated("tint_mode_3", "\"tint\": {\"mode\": 0", "\"tint\": {\"mode\": 3", "out_of_range");
    mutated("slope_transparency", "\"slopeTransparency\": false", "\"slopeTransparency\": true", "out_of_range");
    mutated("flora_weights", "\"weight\": 0.6}", "\"weight\": 0.7}", "out_of_range");
    mutated("flora_water", "\"boreal_forest\": {\"layer\"", "\"lake\": {\"layer\"", "out_of_range");
    mutated("moisture_band", "\"band\": 0.25", "\"band\": 0", "out_of_range");
    mutated("bad_ramp", "\"full\": 0.6", "\"full\": 0.3", "out_of_range");
    mutated("unknown_intent", "\"intent\": \"lake\"", "\"intent\": \"swamp\"", "unmapped_category");
    mutated("landform_gap", "{\"index\": 1,", "{\"index\": 2,", "out_of_range");
}
}  // namespace

int main(int argc, char** argv) {
    if (argc != 5) { std::cerr << "usage: terrain_resampling_tests PROJECTION_POLICY MATERIALS IMPORT WORK_DIR\n"; return 2; }
    projection_file = argv[1]; materials_file = argv[2]; import_file = argv[3]; work = argv[4];
    fs::create_directories(work);
    try { compile_tests(); quantize_tests(); refusal_tests(); }
    catch (const std::exception& e) { check(false, std::string("unexpected exception: ") + e.what()); }
    std::cout << checks - failures << "/" << checks << " checks passed\n";
    return failures == 0 ? 0 : 1;
}
