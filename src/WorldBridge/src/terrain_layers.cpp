#include "worldbridge/terrain_layers.hpp"
#include <algorithm>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <set>

namespace worldbridge::terrain {
namespace {
using json::Error;
using json::Location;

Location nowhere() { return Location{"", 0, 1, 1, "$"}; }
[[noreturn]] void refuse(const std::string& code, const std::string& message) { throw Error(code, message, nowhere()); }

// The surface encoder's rounding (surface.cpp even_round): half to even, independent of the FP environment.
double even_round(double x) {
    const double f = std::floor(x), d = x - f;
    return d < .5 ? f : d > .5 ? f + 1 : std::fmod(f, 2) == 0 ? f : f + 1;
}

int slot_of(const MaterialPolicy& m, const std::string& name) {
    for (const auto& l : m.layer_set) if (l.name == name) return l.slot;
    refuse("unmapped_category", "layerSet has no layer " + name);
}

// Mix a share s of layer k into g (the rest scaled by 1 - s); set g to all layer k.
void mix(double* g, std::size_t layers, int k, double s) {
    const double keep = 1.0 - s;
    for (std::size_t l = 0; l < layers; ++l) g[l] = g[l] * keep;
    g[k] = g[k] + s;
}
void only(double* g, std::size_t layers, int k) {
    for (std::size_t l = 0; l < layers; ++l) g[l] = 0.0;
    g[k] = 1.0;
}

// The composited coverage of each slot from the stored bytes.
void coverage(const std::vector<std::vector<std::uint8_t>>& w, std::size_t i, std::vector<double>& out) {
    double rem = 1.0;
    for (std::size_t k = w.size(); k-- > 1;) {
        const double a = w[k][i] / 255.0;
        out[k] = a * rem;
        rem *= 1.0 - a;
    }
    out[0] = rem;
}

std::uint64_t splitmix(std::uint64_t& s) {
    std::uint64_t z = (s += 0x9e3779b97f4a7c15ULL);
    z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9ULL;
    z = (z ^ (z >> 27)) * 0x94d049bb133111ebULL;
    return z ^ (z >> 31);
}
double uniform(std::uint64_t& s) { return static_cast<double>(splitmix(s) >> 11) * (1.0 / 9007199254740992.0); }

void put32(std::string& b, std::uint32_t v) { b.append(reinterpret_cast<const char*>(&v), 4); }
void putf(std::string& b, double v) { const float f = static_cast<float>(v); b.append(reinterpret_cast<const char*>(&f), 4); }
}  // namespace

int dominant_layer(const std::vector<std::vector<std::uint8_t>>& w, std::size_t i) {
    double rem = 1.0, best = -1.0;
    int arg = 0;
    for (std::size_t k = w.size(); k-- > 1;) {
        const double a = w[k][i] / 255.0;
        if (a != 0) {
            const double c = a * rem;
            if (c > best + 1e-12) { best = c; arg = static_cast<int>(k); }
            rem *= 1.0 - a;
        }
        if (rem <= 0.0) break;
    }
    const double base = (w[0][i] / 255.0) * rem;
    return best >= base && best > 0.0 ? arg : 0;
}

void compile_layers(Compiled& t, const MaterialPolicy& m) {
    const int side = t.side, P = static_cast<int>(t.period);
    const auto n = static_cast<std::size_t>(side) * side;
    const std::size_t L = m.layer_set.size();
    if (t.z.size() != n || t.biome.size() != n || t.intent.size() != n || t.landform.size() != n || t.padding.size() != n || t.temperature.size() != n ||
        t.moisture.size() != n || t.ice_cap.size() != n || t.rocky_coast.size() != n || t.snow_months.size() != n)
        refuse("broken_join", "The layer compile's inputs do not match the output grid");
    const LayerRules& r = m.rules;
    const int K = r.kernel_samples, reach = std::max({K, r.coast_samples, r.water_edge_samples, m.flora.coast_samples, 1});
    if (P < 1 || side - 1 - P < reach) refuse("seam_not_on_grid", "The layer neighbourhood is wider than the seam strip");
    const int wet = slot_of(m, r.moisture_wet_layer), dry = slot_of(m, r.moisture_dry_layer), cold = slot_of(m, r.cold_layer);
    const int mountain = slot_of(m, r.mountain_layer), slope_l = slot_of(m, r.slope_layer), desert_slope = slot_of(m, r.desert_slope_layer);
    const int coast_l = slot_of(m, r.coast_layer), rocky = slot_of(m, r.coast_rocky_layer), edge_l = slot_of(m, r.water_edge_layer);
    const int ice = slot_of(m, r.ice_cap_layer), white = slot_of(m, m.permanent_white.layer);
    const int mountain_landform = static_cast<int>(std::find(m.landforms.begin(), m.landforms.end(), r.mountain_landform) - m.landforms.begin());
    const bool edge_lake = std::find(r.water_edge_intents.begin(), r.water_edge_intents.end(), "lake") != r.water_edge_intents.end();
    const bool edge_river = std::find(r.water_edge_intents.begin(), r.water_edge_intents.end(), "river") != r.water_edge_intents.end();

    // Per biome: its dense row, whether it is the moisture biome, a Desert face, permanent white, and its flora layer.
    struct Biome { std::vector<double> row; bool moisture = false, desert = false, white = false; int flora = -1; };
    std::map<int, Biome> biomes;
    for (const auto& [id, row] : m.presentation) {
        Biome b;
        b.row.assign(L, 0.0);
        for (const auto& [name, f] : row.layers) b.row[static_cast<std::size_t>(slot_of(m, name))] = f;
        const auto& core = m.biome_core.at(id);
        b.moisture = core == r.moisture_biome;
        b.desert = row.terrain_type == "Desert";
        b.white = std::find(m.permanent_white.biomes.begin(), m.permanent_white.biomes.end(), core) != m.permanent_white.biomes.end();
        if (const auto fl = m.flora.biomes.find(core); fl != m.flora.biomes.end()) b.flora = slot_of(m, fl->second.layer);
        biomes[static_cast<int>(id)] = b;
    }
    const auto biome = [&](std::size_t i) -> const Biome& {
        const auto it = biomes.find(t.biome[i]);
        if (it == biomes.end()) refuse("unmapped_category", "Biome " + std::to_string(t.biome[i]) + " has no material mapping");
        return it->second;
    };
    const auto land = [&](std::size_t i) { return t.intent[i] == m.land || t.intent[i] == m.river; };
    const auto column = [&](int c) { return c < 0 ? c + P : c > side - 1 ? c - P : c; };
    const auto at = [&](int rr, int cc) { return static_cast<std::size_t>(std::clamp(rr, 0, side - 1)) * side + column(cc); };

    // 1. The biome rows, moisture split.
    std::vector<double> base(n * L, 0.0);
    for (std::size_t i = 0; i < n; ++i) {
        const Biome& b = biome(i);
        double* g = &base[i * L];
        for (std::size_t l = 0; l < L; ++l) g[l] = b.row[l];
        if (b.moisture) {
            const double total = b.row[wet] + b.row[dry];
            if (total > 0) {
                const double r0 = b.row[wet] / total, d = (t.moisture[i] - r.moisture_split) / r.moisture_band;
                const double w = d >= 0 ? r0 + (1.0 - r0) * std::min(d, 1.0) : r0 * std::max(1.0 + d, 0.0);
                g[wet] = total * w;
                g[dry] = total * (1.0 - w);
            }
        }
    }
    // 2. Kernel smoothing within the class.
    std::vector<double> f(n * L, 0.0);
    for (int rr = 0; rr < side; ++rr)
        for (int cc = 0; cc < side; ++cc) {
            const std::size_t i = static_cast<std::size_t>(rr) * side + cc;
            const bool cls = land(i);
            double* acc = &f[i * L];
            double wsum = 0;
            for (int dr = -K; dr <= K; ++dr)
                for (int dc = -K; dc <= K; ++dc) {
                    const std::size_t j = at(rr + dr, cc + dc);
                    if (land(j) != cls) continue;
                    const double w = static_cast<double>((K + 1 - std::abs(dr)) * (K + 1 - std::abs(dc)));
                    wsum += w;
                    const double* b = &base[j * L];
                    for (std::size_t l = 0; l < L; ++l) acc[l] += w * b[l];
                }
            for (std::size_t l = 0; l < L; ++l) acc[l] = acc[l] / wsum;
        }
    base.clear(); base.shrink_to_fit();

    // 3-9 on land; then the alphas, the flora share and the record.
    t.layer_names.clear();
    for (const auto& l : m.layer_set) t.layer_names.push_back(l.name);
    t.weights.assign(L, std::vector<std::uint8_t>(n, 0));
    t.flora_share.assign(n, 0.0);
    auto& rec = t.layers;
    rec = Compiled::LayerRecord{};
    rec.target.assign(L, 0.0); rec.dominant.assign(L, 0.0);
    std::vector<char> plain(n, 0);
    for (int rr = 0; rr < side; ++rr)
        for (int cc = 0; cc < side; ++cc) {
            const std::size_t i = static_cast<std::size_t>(rr) * side + cc;
            double* g = &f[i * L];
            if (land(i)) {
                const Biome& b = biome(i);
                bool changed = false;
                if (t.temperature[i] < r.cold_below_c) {
                    g[cold] = g[cold] + g[wet] + g[dry];
                    g[wet] = 0.0; g[dry] = 0.0; changed = true;
                }
                if (t.landform[i] == mountain_landform) { mix(g, L, mountain, r.mountain_share); changed = true; }
                const int left = column(cc - 1), right = column(cc + 1), up = std::max(rr - 1, 0), down = std::min(rr + 1, side - 1);
                const double sx = (t.z[static_cast<std::size_t>(rr) * side + right] - t.z[static_cast<std::size_t>(rr) * side + left]) / (2 * t.spacing);
                const double sy = (t.z[static_cast<std::size_t>(down) * side + cc] - t.z[static_cast<std::size_t>(up) * side + cc]) / ((down - up) * t.spacing);
                const double s = slope_share(m, std::sqrt(sx * sx + sy * sy));
                if (s > 0) { mix(g, L, b.desert ? desert_slope : slope_l, s); changed = true; }
                bool coast = false, edge = edge_river && t.intent[i] == m.river;
                const int reach_c = std::max(r.coast_samples, r.water_edge_samples);
                for (int dr = -reach_c; dr <= reach_c; ++dr)
                    for (int dc = -reach_c; dc <= reach_c; ++dc) {
                        const std::size_t j = at(rr + dr, cc + dc);
                        const int d = std::max(std::abs(dr), std::abs(dc));
                        if (d <= r.coast_samples && t.intent[j] == m.ocean) coast = true;
                        if (d <= r.water_edge_samples && ((edge_lake && t.intent[j] == m.lake) || (edge_river && t.intent[j] == m.river))) edge = true;
                    }
                if (coast) {
                    const double rho = std::max(std::clamp(t.rocky_coast[i], 0.0, 1.0), s);
                    only(g, L, coast_l);
                    g[coast_l] = 1.0 - rho;
                    g[rocky] = g[rocky] + rho;
                    changed = true;
                }
                if (edge) { only(g, L, edge_l); changed = true; }
                if (t.ice_cap[i] >= r.ice_cap_threshold) { only(g, L, ice); changed = true; }
                const bool season = t.snow_months[i] >= m.permanent_white.snow_months;
                if (b.white || season) {
                    only(g, L, white); changed = true;
                    if (rr >= 0 && cc < P && !t.padding[i]) { ++rec.white_samples; rec.white_biome += b.white; rec.white_season += season && !b.white; }
                }
                if (b.flora >= 0) t.flora_share[i] = g[b.flora];
                // A plain interior sample: no rule after the kernel changed it and every kernel neighbour is land of its own biome.
                if (!changed) {
                    bool same = true;
                    for (int dr = -K; dr <= K && same; ++dr)
                        for (int dc = -K; dc <= K && same; ++dc) { const std::size_t j = at(rr + dr, cc + dc); same = land(j) && t.biome[j] == t.biome[i]; }
                    plain[i] = same;
                }
            }
            double sum = 0;
            for (std::size_t k = 0; k < L; ++k) {
                sum += g[k];
                double byte = 0;
                if (k == 0) byte = 255;
                else if (g[k] > 0 && sum > 0) byte = std::min(255.0, even_round(g[k] / sum * 255.0));
                if (k > 0 && byte < m.alpha.min_byte) byte = 0;
                t.weights[k][i] = static_cast<std::uint8_t>(byte);
            }
        }

    // The record over canonical land (columns [0, period), rows inside the pole lines).
    std::vector<double> cov(L);
    for (int rr = 0; rr < side; ++rr)
        for (int cc = 0; cc < P; ++cc) {
            const std::size_t i = static_cast<std::size_t>(rr) * side + cc;
            if (t.padding[i] || !land(i)) continue;
            ++rec.land;
            const double* g = &f[i * L];
            for (std::size_t k = 0; k < L; ++k) rec.target[k] += g[k];
            rec.dominant[static_cast<std::size_t>(dominant_layer(t.weights, i))] += 1;
            coverage(t.weights, i, cov);
            auto& bc = rec.biome_coverage[t.biome[i]];
            if (bc.empty()) bc.assign(L, 0.0);
            for (std::size_t k = 0; k < L; ++k) bc[k] += cov[k];
            ++rec.biome_land[t.biome[i]];
            if (!plain[i]) continue;
            ++rec.interior;
            const Biome& b = biome(i);
            if (b.moisture) {
                rec.moisture_pair_max_error = std::max(rec.moisture_pair_max_error, std::abs(cov[wet] + cov[dry] - (b.row[wet] + b.row[dry])));
                for (std::size_t k = 0; k < L; ++k)
                    if (static_cast<int>(k) != wet && static_cast<int>(k) != dry) rec.interior_max_error = std::max(rec.interior_max_error, std::abs(cov[k] - b.row[k]));
            } else
                for (std::size_t k = 0; k < L; ++k) rec.interior_max_error = std::max(rec.interior_max_error, std::abs(cov[k] - b.row[k]));
        }
    if (rec.land)
        for (std::size_t k = 0; k < L; ++k) { rec.target[k] /= static_cast<double>(rec.land); rec.dominant[k] /= static_cast<double>(rec.land); }
    for (auto& [id, bc] : rec.biome_coverage)
        for (auto& v : bc) v /= static_cast<double>(rec.biome_land.at(id));
}

std::size_t check_layer_seams(const Compiled& t) {
    std::size_t pairs = 0;
    for (int r = 0; r < t.side; ++r)
        for (int c = 0; c + t.period < t.side; ++c) {
            const auto a = static_cast<std::size_t>(r) * t.side + c, b = a + static_cast<std::size_t>(t.period);
            for (std::size_t k = 0; k < t.weights.size(); ++k)
                if (t.weights[k][a] != t.weights[k][b])
                    refuse("seam_mismatch", "Layer plane " + t.layer_names[k] + " differs across the seam at row " + std::to_string(r) + ", columns " + std::to_string(c) + " and " + std::to_string(c + t.period));
            ++pairs;
        }
    return pairs;
}

std::string flora_bytes(const Compiled& t, const MaterialPolicy& m, const FloraInput& in, FloraRecord& rec) {
    const int side = t.side, P = static_cast<int>(t.period);
    const double sp = t.spacing, area = sp * sp;
    if (t.flora_share.size() != static_cast<std::size_t>(side) * side) refuse("broken_join", "The flora pass needs the layer compile");
    if (!in.road.empty() && (in.road.size() != static_cast<std::size_t>(in.road_cols) * in.road_rows || !(in.road_spacing > 0))) refuse("broken_join", "The road cells do not match their grid");
    const int S = static_cast<int>(std::lround(in.strip / sp));
    const auto column = [&](int c) { return c < 0 ? c + P : c > side - 1 ? c - P : c; };
    const auto land = [&](std::size_t i) { return t.intent[i] == m.land; };
    struct Tree { int kind; std::string core; int variation; double x, y, yaw, scale; };
    std::vector<std::pair<std::string, const FloraKind*>> kinds;
    std::vector<Tree> trees;
    // Candidates: canonical samples of a flora biome, clear of water and slopes.
    struct Candidate { std::size_t i; int r, c; double lambda; };
    std::vector<Candidate> candidates;
    double expected = 0;
    for (int r = 0; r < side; ++r)
        for (int c = S; c < S + P; ++c) {
            const std::size_t i = static_cast<std::size_t>(r) * side + c;
            if (t.padding[i] || !(t.flora_share[i] > 0) || !land(i)) continue;
            // Clear of the sea and lakes (river banks carry the water-edge layer, whose flora share is 0).
            bool clear = true;
            for (int dr = -m.flora.coast_samples; dr <= m.flora.coast_samples && clear; ++dr)
                for (int dc = -m.flora.coast_samples; dc <= m.flora.coast_samples && clear; ++dc) {
                    const auto j = static_cast<std::size_t>(std::clamp(r + dr, 0, side - 1)) * side + column(c + dc);
                    clear = t.intent[j] != m.ocean && t.intent[j] != m.lake;
                }
            if (!clear) continue;
            const int left = column(c - 1), right = column(c + 1), up = std::max(r - 1, 0), down = std::min(r + 1, side - 1);
            const double sx = (t.z[static_cast<std::size_t>(r) * side + right] - t.z[static_cast<std::size_t>(r) * side + left]) / (2 * sp);
            const double sy = (t.z[static_cast<std::size_t>(down) * side + c] - t.z[static_cast<std::size_t>(up) * side + c]) / ((down - up) * sp);
            if (slope_share(m, std::sqrt(sx * sx + sy * sy)) > m.flora.slope_share) continue;
            const double lambda = m.flora.density * area * t.flora_share[i];
            candidates.push_back({i, r, c, lambda});
            expected += lambda;
        }
    // Every canonical tree has one or two images (the strips copy the other side of the seam): about 2 x the canonical count.
    const double images = (t.extent - 0.0) / in.width;
    if (expected * images > 0.95 * static_cast<double>(m.flora.max_trees) && expected > 0) rec.density_factor = 0.95 * static_cast<double>(m.flora.max_trees) / (expected * images);
    for (const auto& cand : candidates) {
        const auto& core = m.biome_core.at(t.biome[cand.i]);
        const auto& fb = m.flora.biomes.at(core);
        std::uint64_t state = in.seed ^ (static_cast<std::uint64_t>(cand.i) * 0x9e3779b97f4a7c15ULL);
        const int count = static_cast<int>(std::floor(cand.lambda * rec.density_factor + uniform(state)));
        for (int k = 0; k < count; ++k) {
            const double x = (cand.c - 0.5 + uniform(state)) * sp, y = (cand.r - 0.5 + uniform(state)) * sp;
            double pick = uniform(state), acc = 0;
            std::size_t kind = 0;
            for (; kind + 1 < fb.kinds.size(); ++kind) { acc += fb.kinds[kind].weight; if (pick < acc) break; }
            const int variation = static_cast<int>(splitmix(state) % static_cast<std::uint64_t>(fb.kinds[kind].variations));
            const double yaw = uniform(state) * 6.283185307179586, scale = m.flora.scale_min + uniform(state) * (m.flora.scale_max - m.flora.scale_min);
            const double lx = x - in.strip, ly = y - in.pole_margin;
            if (ly < 0 || ly > in.height || x < 0 || x > t.extent) continue;
            bool on_pad = false;
            for (const auto& pad : in.pads) {
                double dx = std::fmod(std::abs(lx - pad.x), in.width);
                dx = std::min(dx, in.width - dx);
                const double dy = ly - pad.y;
                if (dx * dx + dy * dy < pad.radius * pad.radius) { on_pad = true; break; }
            }
            if (on_pad) { ++rec.cleared_pads; continue; }
            if (m.flora.roads && !in.road.empty()) {
                int rc = static_cast<int>(std::floor(lx / in.road_spacing)) % in.road_cols;
                if (rc < 0) rc += in.road_cols;
                const int rr = std::clamp(static_cast<int>(std::floor(ly / in.road_spacing)), 0, in.road_rows - 1);
                if (in.road[static_cast<std::size_t>(rr) * in.road_cols + rc]) { ++rec.cleared_roads; continue; }
            }
            std::size_t ki = 0;
            const std::string key = fb.kinds[kind].name;
            for (; ki < kinds.size() && kinds[ki].first != key; ++ki) {}
            if (ki == kinds.size()) kinds.push_back({key, &fb.kinds[kind]});
            trees.push_back({static_cast<int>(ki), core, variation, x, y, yaw, scale});
            ++rec.canonical; ++rec.biomes[core];
        }
    }
    // Every image inside the domain, at the quantized surface (main-diagonal triangles).
    const auto height = [&](double x, double y) {
        const double gx = x / sp, gy = y / sp;
        const int ix = std::clamp(static_cast<int>(gx), 0, side - 2), iy = std::clamp(static_cast<int>(gy), 0, side - 2);
        const double u = gx - ix, v = gy - iy;
        const auto q = [&](int rr, int cc) { return t.height.value(static_cast<std::size_t>(rr) * side + cc); };
        const double a = q(iy, ix), b = q(iy, ix + 1), c = q(iy + 1, ix), d = q(iy + 1, ix + 1);
        return u >= v ? a * (1 - u) + b * (u - v) + d * v : a * (1 - v) + c * (v - u) + d * u;
    };
    std::string body;
    for (const auto& tr : trees)
        for (const double x : {tr.x - in.width, tr.x, tr.x + in.width}) {
            if (x < 0 || x > t.extent) continue;
            const std::string& name = kinds[static_cast<std::size_t>(tr.kind)].first;
            put32(body, static_cast<std::uint32_t>(name.size())); body += name; put32(body, static_cast<std::uint32_t>(tr.variation));
            const double cs = std::cos(tr.yaw) * tr.scale, sn = std::sin(tr.yaw) * tr.scale;
            for (const double v : {cs, sn, 0.0, 0.0, -sn, cs, 0.0, 0.0, 0.0, 0.0, tr.scale, 0.0, x, tr.y, height(x, tr.y), 1.0}) putf(body, v);
            for (int k = 0; k < 4; ++k) putf(body, 1.0);
            ++rec.trees; ++rec.kinds[name];
        }
    if (rec.trees > m.flora.max_trees) refuse("out_of_range", "The world would plant " + std::to_string(rec.trees) + " map trees, more than flora.maxTrees");
    std::string out = "FLR2";
    put32(out, static_cast<std::uint32_t>(body.size() + 4));
    put32(out, static_cast<std::uint32_t>(rec.trees));
    return out + body;
}

}  // namespace worldbridge::terrain
