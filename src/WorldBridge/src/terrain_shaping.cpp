// Map epic 2 terrain shaping (MMR-278): see include/worldbridge/terrain_shaping.hpp and docs/terrain-compiler.md "Shaping".
// tools/terrain_shaping.py is the Python reference; both follow the same operation order, so their heights agree bit for bit.
#include "worldbridge/terrain_shaping.hpp"
#include "worldbridge/terrain_layers.hpp"
#include <algorithm>
#include <cmath>
#include <deque>
#include <functional>
#include <limits>
#include <map>
#include <numeric>
#include <set>

namespace worldbridge::terrain {
namespace {
using cjson::Value;
using json::Error;

[[noreturn]] void refuse(const std::string& code, const std::string& message) { throw Error(code, message, json::Location{"", 0, 1, 1, "$"}); }

constexpr double kPi = 3.141592653589793;
double radians(double deg) { return deg * kPi / 180.0; }
double degrees(double rad) { return rad * 180.0 / kPi; }

// The 8 neighbours in a fixed order (dr, dc); opposite of j is (j + 4) % 8.
constexpr int kDr[8] = {-1, -1, 0, 1, 1, 1, 0, -1};
constexpr int kDc[8] = {0, 1, 1, 1, 0, -1, -1, -1};

// A canonical periodic field: rows 0..rows-1 (physical rows, poles and padding included), columns 0..cols-1 wrapping.
struct Canon {
    int rows = 0, cols = 0;
    std::vector<double> z;
    std::vector<std::uint8_t> intent;
    std::size_t at(int r, int k) const { return static_cast<std::size_t>(r) * cols + static_cast<std::size_t>(((k % cols) + cols) % cols); }
};

// Physical column c of a side-wide grid shows canonical column (c - strip) mod period.
int canonical_column(int c, int strip, int period) { return ((c - strip) % period + period) % period; }

Canon canonical_of(const Compiled& t, int strip) {
    Canon f;
    f.rows = t.side; f.cols = static_cast<int>(t.period);
    f.z.resize(static_cast<std::size_t>(f.rows) * f.cols); f.intent.resize(f.z.size());
    for (int r = 0; r < f.rows; ++r)
        for (int k = 0; k < f.cols; ++k) {
            const auto i = static_cast<std::size_t>(r) * t.side + static_cast<std::size_t>(strip + k);
            f.z[f.at(r, k)] = t.z[i]; f.intent[f.at(r, k)] = t.intent[i];
        }
    return f;
}

std::vector<double> physical_of(const Canon& f, int side, int strip) {
    std::vector<double> out(static_cast<std::size_t>(side) * side);
    for (int r = 0; r < side; ++r)
        for (int c = 0; c < side; ++c) out[static_cast<std::size_t>(r) * side + c] = f.z[f.at(r, canonical_column(c, strip, f.cols))];
    return out;
}

bool is_land(const MaterialPolicy& m, int intent) { return intent == m.land || intent == m.river; }

// C# Math.Round(x, MidpointRounding.AwayFromZero) for x >= 0, exactly.
double round_away(double x) { const double f = std::floor(x); return x - f >= 0.5 ? f + 1 : f; }

// The runtime lake level at a physical position (GeneratedWaterSystem.Lakes with GeneratedClimateSystem.SourceGrid / Nearest):
// the source water_surface of the nearest source cell when it is a lake cell, else the largest over its lake neighbours; -inf none.
double lake_level(const LakeSource& s, double width, double origin_x, double origin_y, double height, double x, double y) {
    const std::int64_t n = s.side - 1;
    double canonical = x;
    if (!(x >= origin_x && x < origin_x + width)) {
        double t = std::fmod(x - origin_x, width);
        if (t < 0) t += width;
        if (t >= width) t = 0;
        canonical = origin_x + t;
    }
    const double lx = canonical - origin_x, ly = std::max(0.0, std::min(height, y - origin_y));
    const double nn = static_cast<double>(n);
    double column = std::fmod(nn * (lx / width + s.central_meridian_deg / 360.0), nn);
    if (column < 0) column += nn;
    const double row = nn * ly / height;
    std::int64_t c = static_cast<std::int64_t>(round_away(column)) % n;
    const std::int64_t r = static_cast<std::int64_t>(round_away(std::max(0.0, std::min(nn, row))));
    if (c < 0) c += n;
    const auto cell = static_cast<std::size_t>(r * s.side + c);
    if (s.water_type[cell] == 2) return s.water_surface[cell];
    double level = -std::numeric_limits<double>::infinity();
    for (int dr = -1; dr <= 1; ++dr)
        for (int dc = -1; dc <= 1; ++dc) {
            const std::int64_t r2 = r + dr, c2 = ((c + dc) % n + n) % n;
            if (r2 < 0 || r2 > n) continue;
            const auto k = static_cast<std::size_t>(r2 * s.side + c2);
            if (s.water_type[k] == 2) level = std::max(level, s.water_surface[k]);
        }
    return level;
}

// Face statistics on a physical grid over the canonical columns [strip, strip + period) and the rows between the pole lines.
struct FaceStats { std::vector<double> interior, coast, crease; };
FaceStats faces(const std::vector<double>& z, const std::vector<std::uint8_t>& intent, const MaterialPolicy& m, int side, double sp, int strip, int period, int r_lo, int r_hi) {
    FaceStats out;
    const auto idx = [&](int r, int c) { return static_cast<std::size_t>(r) * side + c; };
    std::vector<char> water_edge(z.size(), 0);
    for (int r = 0; r < side; ++r)
        for (int c = 0; c < side; ++c) {
            bool edge = !is_land(m, intent[idx(r, c)]);
            for (int j = 0; j < 8 && !edge; ++j) {
                const int rr = r + kDr[j], cc = c + kDc[j];
                if (rr < 0 || rr >= side || cc < 0 || cc >= side) continue;
                edge = !is_land(m, intent[idx(rr, cc)]);
            }
            water_edge[idx(r, c)] = edge;
        }
    // Triangle 0: (r,c) (r,c+1) (r+1,c+1); triangle 1: (r,c) (r+1,c+1) (r+1,c). Class 0 other, 1 interior, 2 coast.
    struct Tri { double a = 0, b = 0, len = 1; int cls = 0; };
    const auto tri = [&](int r, int c, int which) {
        Tri t;
        std::size_t v[3];
        double dx, dy;
        if (which == 0) { v[0] = idx(r, c); v[1] = idx(r, c + 1); v[2] = idx(r + 1, c + 1); dx = z[v[1]] - z[v[0]]; dy = z[v[2]] - z[v[1]]; }
        else { v[0] = idx(r, c); v[1] = idx(r + 1, c + 1); v[2] = idx(r + 1, c); dx = z[v[1]] - z[v[2]]; dy = z[v[2]] - z[v[0]]; }
        bool all_land = true, any_land = false, any_ocean = false, any_edge = false;
        for (const auto i : v) {
            const bool l = is_land(m, intent[i]);
            all_land = all_land && l; any_land = any_land || l; any_ocean = any_ocean || intent[i] == m.ocean; any_edge = any_edge || water_edge[i];
        }
        t.cls = all_land && !any_edge ? 1 : (any_ocean && any_land) ? 2 : 0;
        t.a = -dx / sp; t.b = -dy / sp; t.len = std::sqrt(t.a * t.a + t.b * t.b + 1.0);
        return t;
    };
    const auto slope = [&](const Tri& t) { return degrees(std::atan(std::sqrt(t.a * t.a + t.b * t.b))); };
    const auto crease = [&](const Tri& p, const Tri& q) {
        const double d = std::max(-1.0, std::min(1.0, (p.a * q.a + p.b * q.b + 1.0) / (p.len * q.len)));
        return degrees(std::acos(d));
    };
    for (int r = r_lo; r < r_hi; ++r)
        for (int c = strip; c < strip + period; ++c) {
            const Tri t0 = tri(r, c, 0), t1 = tri(r, c, 1);
            for (const Tri* t : {&t0, &t1}) {
                if (t->cls == 1) out.interior.push_back(slope(*t));
                else if (t->cls == 2) out.coast.push_back(slope(*t));
            }
            if (t0.cls == 1 && t1.cls == 1) out.crease.push_back(crease(t0, t1));
            const Tri right = tri(r, c + 1, 1);
            if (t0.cls == 1 && right.cls == 1) out.crease.push_back(crease(t0, right));
            if (r + 1 < r_hi) { const Tri below = tri(r + 1, c, 0); if (t1.cls == 1 && below.cls == 1) out.crease.push_back(crease(t1, below)); }
        }
    return out;
}

SlopeStats stats(std::vector<double> v) {
    SlopeStats s;
    s.faces = v.size();
    if (v.empty()) return s;
    std::sort(v.begin(), v.end());
    s.p95 = v[std::min(v.size() - 1, v.size() * 95 / 100)];
    s.max = v.back();
    return s;
}

// Isolated spikes: a land sample that is a strict extremum of its 8 neighbours and steeper than the despike bound to every one.
std::size_t spikes(const std::vector<double>& z, const std::vector<std::uint8_t>& intent, const std::vector<char>& pad, const MaterialPolicy& m, int side, double sp,
                   int strip, int period, int r_lo, int r_hi, double t) {
    std::size_t count = 0;
    const double diag = sp * std::sqrt(2.0);
    for (int r = r_lo + 1; r < r_hi; ++r)
        for (int c = strip; c < strip + period; ++c) {
            const auto i = static_cast<std::size_t>(r) * side + c;
            if (!is_land(m, intent[i]) || (!pad.empty() && pad[i])) continue;
            bool above = true, below = true, steep = true;
            for (int j = 0; j < 8; ++j) {
                const double n = z[static_cast<std::size_t>(r + kDr[j]) * side + (c + kDc[j])];
                const double bound = t * (j % 2 ? diag : sp);
                above = above && z[i] > n; below = below && z[i] < n; steep = steep && std::abs(z[i] - n) > bound;
            }
            if ((above || below) && steep) ++count;
        }
    return count;
}

// A pad sample: its canonical index and its position in units (logical x, unwrapped around its pad; physical y).
struct PadSample { std::size_t index = 0; double x = 0, y = 0; };
// The samples of a settlement's pad: every land sample of the cells its disk overlaps, between the pole lines, row-major (a block is
// far narrower than the period, so its samples are distinct).
std::vector<PadSample> block(const Canon& f, double spacing, double x, double y, double radius, int r_lo, int r_hi, const MaterialPolicy& m) {
    std::vector<PadSample> out;
    const double u = x / spacing, v = y / spacing, rad = radius / spacing;
    const int k0 = static_cast<int>(std::floor(u - rad)), k1 = static_cast<int>(std::ceil(u + rad));
    const int r0 = std::max(r_lo + 1, static_cast<int>(std::floor(v - rad))), r1 = std::min(r_hi - 1, static_cast<int>(std::ceil(v + rad)));
    for (int r = r0; r <= r1; ++r)
        for (int k = k0; k <= k1; ++k) {
            const auto i = f.at(r, k);
            if (is_land(m, f.intent[i])) out.push_back({i, k * spacing, r * spacing});
        }
    return out;
}

// A pad's plane: the least-squares plane of the heights it covers, its gradient capped at tmax (pads.maxSlopeDeg); flat when the
// samples do not span both axes.
struct Plane { double mx = 0, my = 0, mz = 0, a = 0, b = 0; double at(double x, double y) const { return mz + a * (x - mx) + b * (y - my); } };
Plane fit(const std::vector<PadSample>& s, const std::vector<double>& z, double tmax) {
    Plane p;
    double sx = 0, sy = 0, sz = 0;
    for (const auto& q : s) { sx += q.x; sy += q.y; sz += z[q.index]; }
    const double n = static_cast<double>(s.size());
    p.mx = sx / n; p.my = sy / n; p.mz = sz / n;
    double xx = 0, xy = 0, yy = 0, xz = 0, yz = 0;
    for (const auto& q : s) {
        const double dx = q.x - p.mx, dy = q.y - p.my, dz = z[q.index] - p.mz;
        xx += dx * dx; xy += dx * dy; yy += dy * dy; xz += dx * dz; yz += dy * dz;
    }
    const double det = xx * yy - xy * xy;
    if (det > 1e-9 * (xx * yy)) { p.a = (xz * yy - yz * xy) / det; p.b = (yz * xx - xz * xy) / det; }
    const double g = std::sqrt(p.a * p.a + p.b * p.b);
    if (g > tmax) { p.a = p.a * (tmax / g); p.b = p.b * (tmax / g); }
    return p;
}

// Settlements whose fine pads share or touch samples form one union with one plane; shift aligns each pad's x with the first's image.
struct Union { std::vector<int> pads; std::vector<double> shift; Plane plane; bool empty = true; };

}  // namespace

std::vector<LakeRecord> lake_rule(const std::vector<double>& heights, const std::vector<std::uint8_t>& intent, int side, double spacing, double width,
                                  double origin_x, double origin_y, double height, const LakeSource& source, std::vector<std::vector<std::size_t>>* members_out) {
    const auto n = static_cast<std::size_t>(side) * side;
    std::vector<char> seen(n, 0);
    std::vector<int> owner(n, -1);
    std::vector<std::vector<std::size_t>> groups;
    std::vector<LakeRecord> lakes;
    std::vector<char> matched;
    for (std::size_t start = 0; start < n; ++start) {
        if (seen[start] || intent[start] != 2) continue;
        std::vector<std::size_t> group;
        std::deque<std::size_t> queue{start};
        seen[start] = 1;
        while (!queue.empty()) {
            const auto i = queue.front(); queue.pop_front(); group.push_back(i);
            const int col = static_cast<int>(i % side), row = static_cast<int>(i / side);
            const int d[4][2] = {{1, 0}, {-1, 0}, {0, 1}, {0, -1}};
            for (const auto& dd : d) {
                const int cc = col + dd[0], rr = row + dd[1];
                if (cc < 0 || rr < 0 || cc >= side || rr >= side) continue;
                const auto j = static_cast<std::size_t>(rr) * side + cc;
                if (!seen[j] && intent[j] == 2) { seen[j] = 1; queue.push_back(j); }
            }
        }
        LakeRecord lake;
        lake.index = static_cast<int>(groups.size());
        lake.samples = static_cast<int>(group.size());
        lake.first_column = static_cast<int>(group[0] % side); lake.first_row = static_cast<int>(group[0] / side);
        double level = -std::numeric_limits<double>::infinity(), min_x = 1e300, max_x = -1e300, min_y = 1e300, max_y = -1e300, bed = -1e300;
        for (const auto i : group) {
            const double x = static_cast<double>(i % side) * spacing, y = static_cast<double>(i / side) * spacing;
            min_x = std::min(min_x, x); max_x = std::max(max_x, x); min_y = std::min(min_y, y); max_y = std::max(max_y, y);
            bed = std::max(bed, heights[i]);
            const double l = lake_level(source, width, origin_x, origin_y, height, x, y);
            if (!std::isinf(l)) { ++lake.matched; level = std::max(level, l); }
        }
        lake.bed_max_z = bed;
        for (const auto i : group) owner[i] = static_cast<int>(groups.size());
        groups.push_back(group);
        if (lake.matched) {
            const int c0 = static_cast<int>(std::lround(min_x / spacing)) - 1, c1 = static_cast<int>(std::lround(max_x / spacing)) + 1;
            const int r0 = static_cast<int>(std::lround(min_y / spacing)) - 1, r1 = static_cast<int>(std::lround(max_y / spacing)) + 1;
            const int period = static_cast<int>(std::lround(width / spacing));
            double shore = 1.7976931348623157e308;
            for (int rr = std::max(0, r0); rr <= std::min(side - 1, r1); ++rr)
                for (int cc = c0; cc <= c1; ++cc) {
                    const int wrapped = cc < 0 ? cc + period : cc >= side ? cc - period : cc;
                    if (wrapped < 0 || wrapped >= side) continue;
                    const auto at = static_cast<std::size_t>(rr) * side + wrapped;
                    if (intent[at] == 2) continue;   // MMR-285: every lake sample is water, its own or another group's (or the copy's)
                    shore = std::min(shore, heights[at]);
                }
            lake.level_m = level;
            lake.level_z = (level - source.sea_level_m) * source.vertical_scale;
            lake.shore_min_z = shore;
            lake.z = std::min(lake.level_z, shore - .001);
            lake.clamped = lake.z < lake.level_z;
        }
        matched.push_back(lake.matched != 0);
        lakes.push_back(lake);
    }
    // A copy cut by the terrain's east or west edge takes its whole twin's verdict and level.
    const int sample_period = static_cast<int>(std::lround(width / spacing));
    std::vector<char> shown(lakes.size()), verdict(lakes.size());
    for (std::size_t k = 0; k < lakes.size(); ++k) shown[k] = verdict[k] = matched[k] && lakes[k].z > lakes[k].bed_max_z;
    for (std::size_t k = 0; k < lakes.size(); ++k) {
        if (!matched[k]) continue;
        const bool edge = std::any_of(groups[k].begin(), groups[k].end(), [&](std::size_t i) { const auto c = i % side; return c == 0 || c == static_cast<std::size_t>(side - 1); });
        if (!edge) continue;
        const auto first = groups[k][0];
        const int col = static_cast<int>(first % side), row = static_cast<int>(first / side);
        const int copy = col + sample_period < side ? col + sample_period : col - sample_period;
        const int twin = copy >= 0 && copy < side ? owner[static_cast<std::size_t>(row) * side + copy] : -1;
        if (twin < 0 || twin == static_cast<int>(k)) continue;
        verdict[k] = shown[static_cast<std::size_t>(twin)];
        if (!shown[static_cast<std::size_t>(twin)]) continue;
        const auto& t = lakes[static_cast<std::size_t>(twin)];
        lakes[k].level_m = t.level_m; lakes[k].level_z = t.level_z; lakes[k].z = t.z; lakes[k].clamped = t.clamped;
    }
    for (std::size_t k = 0; k < lakes.size(); ++k) lakes[k].shown_after = verdict[k];
    if (members_out) *members_out = groups;
    return lakes;
}

Shaped shape(const Compiled& coarse, const Compiled& fine, const projection::Projection& p, const MaterialPolicy& m, const Import& import,
             const policy::Shaping& policy, const std::vector<Pad>& pads_in, const LakeSource& source, const TravelCells& cells) {
    if (!policy.resolution.fine) refuse("out_of_range", "The terrain shaping policy's resolution.fine is false: the shaped surface is the 513-sample output (MMR-249 go)");
    if (coarse.side != policy.resolution.side || fine.side != policy.resolution.fine_side || fine.side != 2 * (coarse.side - 1) + 1 || fine.period != 2 * coarse.period ||
        std::abs(fine.extent - coarse.extent) > 0)
        refuse("grid_mismatch", "The shaping stage needs the travel compile at resolution.side and the fine compile at resolution.fineSide of the same extent");
    if (source.water_type.size() != static_cast<std::size_t>(source.side * source.side) || source.water_surface.size() != source.water_type.size())
        refuse("broken_join", "The lake source layers do not match the source grid");
    Shaped out;
    auto& rec = out.record;
    rec.policy_sha256 = policy.sha256;
    const double eps = m.coast_epsilon, sp = coarse.spacing, spf = fine.spacing;
    const int strip = static_cast<int>(std::lround(p.strip() / sp)), strip_f = 2 * strip;
    const int rn = static_cast<int>(std::lround(p.pole_margin() / sp)), rs = rn + static_cast<int>(std::lround(p.height() / sp));
    const int P = static_cast<int>(coarse.period), PF = 2 * P;
    const auto active = [&](int r) { return r > rn && r < rs; };
    const auto active_f = [&](int r) { return r > 2 * rn && r < 2 * rs; };

    Canon g = canonical_of(coarse, strip);
    const Canon fm = canonical_of(fine, strip_f);
    const std::vector<double> original = g.z;
    std::vector<char> pad(g.z.size(), 0);
    const auto level_z = [&](double x, double y) {
        const double l = lake_level(source, p.width(), p.strip(), p.pole_margin(), p.height(), x, y);
        return std::isinf(l) ? l : (l - source.sea_level_m) * source.vertical_scale;
    };

    // Lake groups of a canonical field (4-connected, wrapping columns, rows between the pole lines), in scan order.
    const auto lake_groups = [&](const Canon& f, const std::function<bool(int)>& act) {
        std::vector<std::vector<std::size_t>> groups;
        std::vector<char> seen(f.z.size(), 0);
        for (int r = 0; r < f.rows; ++r)
            for (int k = 0; k < f.cols; ++k) {
                const auto s = f.at(r, k);
                if (!act(r) || seen[s] || f.intent[s] != m.lake) continue;
                std::vector<std::size_t> group;
                std::deque<std::size_t> queue{s};
                seen[s] = 1;
                while (!queue.empty()) {
                    const auto i = queue.front(); queue.pop_front(); group.push_back(i);
                    const int rr = static_cast<int>(i / f.cols), kk = static_cast<int>(i % f.cols);
                    const int d[4][2] = {{0, 1}, {0, -1}, {1, 0}, {-1, 0}};
                    for (const auto& dd : d) {
                        const int r2 = rr + dd[0];
                        if (!act(r2)) continue;
                        const auto j = f.at(r2, kk + dd[1]);
                        if (!seen[j] && f.intent[j] == m.lake) { seen[j] = 1; queue.push_back(j); }
                    }
                }
                std::sort(group.begin(), group.end());
                groups.push_back(group);
            }
        return groups;
    };
    // Beds below the lake level, shores (8-adjacent land samples) above it.
    const auto lakes_step = [&](Canon& f, const std::function<bool(int)>& act, double spacing, int col_strip, const std::vector<char>& skip) {
        for (const auto& group : lake_groups(f, act)) {
            double level = -std::numeric_limits<double>::infinity();
            for (const auto i : group) {
                const int r = static_cast<int>(i / f.cols), k = static_cast<int>(i % f.cols);
                level = std::max(level, level_z((col_strip + k) * spacing, r * spacing));
            }
            if (std::isinf(level)) continue;
            const std::set<std::size_t> own(group.begin(), group.end());
            for (const auto i : group) f.z[i] = std::min(f.z[i], level - policy.lakes.bed_below_level);
            for (const auto i : group) {
                const int r = static_cast<int>(i / f.cols), k = static_cast<int>(i % f.cols);
                for (int j = 0; j < 8; ++j) {
                    const int r2 = r + kDr[j];
                    if (!act(r2)) continue;
                    const auto s = f.at(r2, k + kDc[j]);
                    if (own.count(s) || !is_land(m, f.intent[s]) || (!skip.empty() && skip[s])) continue;
                    f.z[s] = std::max(f.z[s], level + policy.lakes.shore_above_level);
                }
            }
        }
    };

    // Pads, sorted by settlement id; unions of the pads whose fine blocks share or touch samples (8-adjacent).
    std::vector<Pad> pads = pads_in;
    std::sort(pads.begin(), pads.end(), [](const Pad& a, const Pad& b) { return a.id < b.id; });
    const auto radius = [&](const Pad& pd) {
        const auto it = policy.pads.radius.find(pd.kind);
        if (it == policy.pads.radius.end()) refuse("unmapped_category", "Pad kind " + pd.kind + " has no radius in the shaping policy");
        return it->second;
    };
    std::vector<std::vector<std::size_t>> blocks;
    for (const auto& pd : pads) {
        std::vector<std::size_t> b;
        for (const auto& s : block(fm, spf, pd.x, pd.y + p.pole_margin(), radius(pd), 2 * rn, 2 * rs, m)) b.push_back(s.index);
        blocks.push_back(b);
    }
    std::vector<int> parent(pads.size());
    std::iota(parent.begin(), parent.end(), 0);
    const std::function<int(int)> find = [&](int a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; };
    if (policy.pads.merge_touching) {
        std::map<std::size_t, int> first;   // sample -> a pad holding it or touching it
        for (std::size_t a = 0; a < pads.size(); ++a) {
            std::set<std::size_t> reach;
            for (const auto s : blocks[a]) {
                reach.insert(s);
                const int r = static_cast<int>(s / fm.cols), k = static_cast<int>(s % fm.cols);
                for (int j = 0; j < 8; ++j) if (r + kDr[j] >= 0 && r + kDr[j] < fm.rows) reach.insert(fm.at(r + kDr[j], k + kDc[j]));
            }
            for (const auto s : reach) {
                const auto f = first.find(s);
                if (f != first.end()) { const int x = find(static_cast<int>(a)), y = find(f->second); if (x != y) parent[std::max(x, y)] = std::min(x, y); }
            }
            for (const auto s : blocks[a]) first.emplace(s, static_cast<int>(a));
        }
    }
    std::map<int, Union> unions;
    for (std::size_t a = 0; a < pads.size(); ++a) {
        auto& u = unions[find(static_cast<int>(a))];
        const double ref = u.pads.empty() ? pads[a].x : pads[static_cast<std::size_t>(u.pads[0])].x;
        u.pads.push_back(static_cast<int>(a));
        u.shift.push_back(p.width() * std::round((ref - pads[a].x) / p.width()));
    }
    // A union's samples on a canonical grid (the first occurrence of each, pads in order).
    const auto union_samples = [&](const Canon& f, double spacing, const Union& u, int r_lo, int r_hi) {
        std::vector<PadSample> out;
        std::set<std::size_t> seen;
        for (std::size_t j = 0; j < u.pads.size(); ++j) {
            const auto& pd = pads[static_cast<std::size_t>(u.pads[j])];
            for (const auto& s : block(f, spacing, pd.x + u.shift[j], pd.y + p.pole_margin(), radius(pd), r_lo, r_hi, m)) if (seen.insert(s.index).second) out.push_back(s);
        }
        return out;
    };
    // The land samples within `band` steps (8-neighbourhood) of a union, with their positions and steps, in index order.
    struct RingSample { int dist = 0; double x = 0, y = 0; };
    const auto ring_of = [&](const Canon& f, double spacing, const std::vector<PadSample>& samples, int band, const std::function<bool(int)>& act, const std::vector<char>& is_pad) {
        std::map<std::size_t, RingSample> seen, ring;
        std::deque<std::size_t> queue;
        for (const auto& s : samples) { seen[s.index] = {0, s.x, s.y}; queue.push_back(s.index); }
        while (!queue.empty()) {
            const auto s = queue.front(); queue.pop_front();
            const auto here = seen.at(s);
            if (here.dist >= band) continue;
            const int r = static_cast<int>(s / f.cols), k = static_cast<int>(s % f.cols);
            for (int j = 0; j < 8; ++j) {
                if (!act(r + kDr[j])) continue;
                const auto o = f.at(r + kDr[j], k + kDc[j]);
                if (seen.count(o)) continue;
                const RingSample next{here.dist + 1, here.x + kDc[j] * spacing, here.y + kDr[j] * spacing};
                seen[o] = next; queue.push_back(o);
                if (is_land(m, f.intent[o]) && !is_pad[o]) ring[o] = next;
            }
        }
        return ring;
    };
    // Pads on a canonical grid: each union's samples on its plane (never below the land datum), then the blend ring.
    const auto place_pads = [&](Canon& f, double spacing, std::vector<char>& is_pad, int r_lo, int r_hi, int band, bool fit_planes) {
        const double tmax = std::tan(radians(policy.pads.max_slope_deg));
        std::vector<std::vector<PadSample>> placed;
        for (auto& [root, u] : unions) {
            auto samples = union_samples(f, spacing, u, r_lo, r_hi);
            if (fit_planes) { u.empty = samples.empty(); if (!u.empty) u.plane = fit(samples, f.z, tmax); }
            if (u.empty) { placed.emplace_back(); continue; }
            for (const auto& s : samples) { f.z[s.index] = std::max(u.plane.at(s.x, s.y), eps); is_pad[s.index] = 1; }
            placed.push_back(samples);
        }
        if (band <= 0) return;
        std::size_t n = 0;
        for (const auto& [root, u] : unions) {
            const auto& samples = placed[n++];
            if (u.empty) continue;
            for (const auto& [s, ring] : ring_of(f, spacing, samples, band, [&](int r) { return r > r_lo && r < r_hi; }, is_pad)) {
                const double w = static_cast<double>(band + 1 - ring.dist) / static_cast<double>(band + 1);
                f.z[s] = f.z[s] + (std::max(u.plane.at(ring.x, ring.y), eps) - f.z[s]) * w;
            }
        }
    };
    rec.pads = pads.size();
    rec.pads_merged = pads.size() - unions.size();

    const auto coast_step = [&](Canon& f) {
        const double tl = std::tan(radians(policy.coast.land_max_slope_deg)), ts = std::tan(radians(policy.coast.sea_max_slope_deg));
        for (const bool land_side : {true, false}) {
            std::vector<int> dist(f.z.size(), 0);
            std::deque<std::size_t> queue;
            for (int r = 0; r < f.rows; ++r)
                for (int k = 0; k < f.cols; ++k) {
                    const auto s = f.at(r, k);
                    const bool mine = land_side ? is_land(m, f.intent[s]) : f.intent[s] == m.ocean;
                    if (!mine || !active(r)) continue;
                    bool shore = false;
                    for (int j = 0; j < 8 && !shore; ++j) {
                        const int r2 = r + kDr[j];
                        if (r2 < 0 || r2 >= f.rows) continue;
                        const auto o = f.intent[f.at(r2, k + kDc[j])];
                        shore = land_side ? o == m.ocean : is_land(m, o);
                    }
                    if (shore) { dist[s] = 1; queue.push_back(s); }
                }
            while (!queue.empty()) {
                const auto s = queue.front(); queue.pop_front();
                if (dist[s] >= policy.coast.band_samples) continue;
                const int r = static_cast<int>(s / f.cols), k = static_cast<int>(s % f.cols);
                for (int j = 0; j < 8; ++j) {
                    const int r2 = r + kDr[j];
                    if (!active(r2)) continue;
                    const auto o = f.at(r2, k + kDc[j]);
                    const bool mine = land_side ? is_land(m, f.intent[o]) : f.intent[o] == m.ocean;
                    if (mine && !dist[o]) { dist[o] = dist[s] + 1; queue.push_back(o); }
                }
            }
            for (std::size_t s = 0; s < f.z.size(); ++s) {
                if (!dist[s]) continue;
                // Half a step less than dist: the shoreline lies between the last land and the first sea sample.
                if (land_side) f.z[s] = std::min(f.z[s], eps + tl * sp * (dist[s] - 0.5));
                else f.z[s] = std::max(f.z[s], -eps - ts * sp * (dist[s] - 0.5));
            }
        }
    };
    const auto pads_step = [&](Canon& f) { place_pads(f, sp, pad, rn, rs, policy.pads.blend_samples, true); };
    const auto despike_step = [&](Canon& f) {
        const double t = std::tan(radians(policy.despike.max_slope_deg)), diag = sp * std::sqrt(2.0);
        double bound[8];
        for (int j = 0; j < 8; ++j) bound[j] = t * (j % 2 ? diag : sp);
        for (int sweep = 0; sweep < policy.despike.sweeps; ++sweep) {
            std::vector<double> next = f.z;
            std::size_t changed = 0;
            double residual = 0;
            for (int r = rn + 1; r < rs; ++r)
                for (int k = 0; k < f.cols; ++k) {
                    const auto s = f.at(r, k);
                    if (!is_land(m, f.intent[s]) || pad[s]) continue;
                    double n[8];
                    bool within[8], above = true, below = true, steep = false;
                    for (int j = 0; j < 8; ++j) {
                        n[j] = f.z[f.at(r + kDr[j], k + kDc[j])];
                        within[j] = std::abs(f.z[s] - n[j]) <= bound[j];
                        above = above && f.z[s] > n[j]; below = below && f.z[s] < n[j]; steep = steep || !within[j];
                    }
                    int ridge = 0;
                    for (int j = 0; j < 8; ++j) ridge += within[j] && within[(j + 4) % 8];
                    if (!steep || (policy.despike.keep_ridges && ridge >= policy.despike.ridge_neighbours)) continue;
                    double v = f.z[s];
                    if (above) { v = n[0] + bound[0]; for (int j = 1; j < 8; ++j) v = std::min(v, n[j] + bound[j]); }
                    else if (below) { v = n[0] - bound[0]; for (int j = 1; j < 8; ++j) v = std::max(v, n[j] - bound[j]); }
                    else continue;
                    v = std::max(v, eps);
                    if (v != f.z[s]) { residual = std::max(residual, std::abs(v - f.z[s])); next[s] = v; ++changed; }
                }
            f.z = next;
            rec.sweeps = sweep + 1;
            rec.residual = residual;
            if (!changed) break;
        }
    };
    for (const auto& step : policy.coherence_order) {
        if (step == "lakes" && policy.lakes.enabled) lakes_step(g, active, sp, strip, {});
        else if (step == "coast" && policy.coast.enabled) coast_step(g);
        else if (step == "pads" && policy.pads.enabled) pads_step(g);
        else if (step == "despike" && policy.despike.enabled) despike_step(g);
    }
    // The masked low-pass: free land samples only (not sea, lakes or their edges, pads or the pole and padding rows).
    if (policy.low_pass.enabled) {
        const auto keep = [&](const std::string& what) { return std::find(policy.low_pass.keep.begin(), policy.low_pass.keep.end(), what) != policy.low_pass.keep.end(); };
        std::vector<char> free(g.z.size(), 0);
        for (int r = rn + 1; r < rs; ++r)
            for (int k = 0; k < g.cols; ++k) {
                const auto s = g.at(r, k);
                const int it = g.intent[s];
                if (pad[s] || (it == m.ocean && keep("sea")) || (it == m.lake && keep("lake"))) continue;
                if (!is_land(m, it) && it != m.ocean && it != m.lake) continue;
                if (it == m.ocean || it == m.lake) { free[s] = 1; continue; }
                bool edge = false;
                for (int j = 0; j < 8; ++j) { const int o = g.intent[g.at(r + kDr[j], k + kDc[j])]; edge = edge || o == m.ocean || o == m.lake; }
                if (edge && keep("coast")) continue;
                free[s] = 1;
            }
        const auto& kern = policy.low_pass.kernel;
        const int half = static_cast<int>(kern.size() / 2);
        double total = 0;
        for (const int w : kern) total += w;
        std::vector<double> tmp = g.z, res;
        for (int r = 0; r < g.rows; ++r)
            for (int k = 0; k < g.cols; ++k) {
                const auto s = g.at(r, k);
                if (!free[s]) continue;
                double acc = 0;
                for (int j = 0; j < static_cast<int>(kern.size()); ++j) acc += kern[j] * g.z[g.at(r, k + j - half)];
                tmp[s] = acc / total;
            }
        res = tmp;
        for (int r = 0; r < g.rows; ++r)
            for (int k = 0; k < g.cols; ++k) {
                const auto s = g.at(r, k);
                if (!free[s]) continue;
                double acc = 0;
                for (int j = 0; j < static_cast<int>(kern.size()); ++j) acc += kern[j] * tmp[g.at(std::clamp(r + j - half, 0, g.rows - 1), k)];
                res[s] = acc / total;
                if (is_land(m, g.intent[s])) res[s] = std::max(res[s], eps);
            }
        g.z = res;
    }
    for (std::size_t s = 0; s < g.z.size(); ++s) rec.samples_changed += g.z[s] != original[s];
    out.coarse_z = physical_of(g, coarse.side, strip);

    // Cubic B-spline 2x: at sample i (p[i-1] + 4 p[i] + p[i+1]) / 6, at i + 1/2 (p[i-1] + 23 p[i] + 23 p[i+1] + p[i+2]) / 48; columns
    // wrap, rows clamp.
    Canon h;
    h.rows = fine.side; h.cols = PF;
    h.z.assign(static_cast<std::size_t>(h.rows) * h.cols, 0.0); h.intent.resize(h.z.size());
    {
        std::vector<double> xpass(static_cast<std::size_t>(g.rows) * PF);
        for (int r = 0; r < g.rows; ++r)
            for (int i = 0; i < P; ++i) {
                const auto at = [&](int k) { return g.z[g.at(r, k)]; };
                xpass[static_cast<std::size_t>(r) * PF + 2 * i] = (at(i - 1) + 4 * at(i) + at(i + 1)) / 6.0;
                xpass[static_cast<std::size_t>(r) * PF + 2 * i + 1] = (at(i - 1) + 23 * at(i) + 23 * at(i + 1) + at(i + 2)) / 48.0;
            }
        const int last = g.rows - 1;
        for (int k = 0; k < PF; ++k)
            for (int r = 0; r < g.rows; ++r) {
                const auto at = [&](int rr) { return xpass[static_cast<std::size_t>(std::clamp(rr, 0, last)) * PF + k]; };
                h.z[h.at(2 * r, k)] = (at(r - 1) + 4 * at(r) + at(r + 1)) / 6.0;
                if (r < last) h.z[h.at(2 * r + 1, k)] = (at(r - 1) + 23 * at(r) + 23 * at(r + 1) + at(r + 2)) / 48.0;
            }
    }
    // Fine fix-ups on the fine masks.
    h.intent = fm.intent;
    std::vector<char> pad_f(h.z.size(), 0);
    for (int r = 0; r < h.rows; ++r)
        for (int k = 0; k < h.cols; ++k) {
            const auto s = h.at(r, k);
            if (!active_f(r)) { h.z[s] = fm.z[s]; continue; }
            if (h.intent[s] == m.ocean) h.z[s] = std::min(h.z[s], -eps);
            else if (is_land(m, h.intent[s])) h.z[s] = std::max(h.z[s], eps);
        }
    if (policy.pads.enabled) place_pads(h, spf, pad_f, 2 * rn, 2 * rs, 2 * policy.pads.blend_samples, false);
    if (policy.lakes.enabled) lakes_step(h, active_f, spf, strip_f, pad_f);
    for (int r = 2 * rn + 1; r < 2 * rs; ++r)
        for (int k = 0; k < h.cols; ++k) { const auto s = h.at(r, k); if (is_land(m, h.intent[s])) h.z[s] = std::max(h.z[s], eps); }

    // Lake groups shown under the runtime plane rule, on the quantized surface it reads. Phase 0: a hidden group's bed goes below the
    // plane its own shore allows (the lowest non-lake sample within one sample of its box, bedBelowLevel under it); other lakes' beds
    // are left out, as the runtime rule leaves them out of the shore since MMR-285, so lowering one lake never lowers another's target.
    // Phase 1: a group the unshaped compile showed and that is still hidden goes below its whole shore (kept as a guard; with the
    // MMR-285 rule phase 0 shows every matched group).
    const int side = fine.side;
    std::vector<double> fq(fine.z.size());
    for (std::size_t i = 0; i < fq.size(); ++i) fq[i] = fine.height.value(i);
    const auto unshaped = lake_rule(fq, fine.intent, side, spf, p.width(), p.strip(), p.pole_margin(), p.height(), source);
    std::vector<std::vector<std::size_t>> members;
    std::vector<LakeRecord> after;
    Quantized q;
    std::vector<double> phys, qv(static_cast<std::size_t>(side) * side);
    int phase = 0;
    for (int pass = 0;; ++pass) {
        phys = physical_of(h, side, strip_f);
        q = quantize(phys, import.height_bits, import.range_decimals, import.zero_range_span);
        for (std::size_t i = 0; i < qv.size(); ++i) qv[i] = q.value(i);
        after = lake_rule(qv, fine.intent, side, spf, p.width(), p.strip(), p.pole_margin(), p.height(), source, &members);
        if (!policy.lakes.enabled || pass >= 16) break;
        bool changed = false;
        for (std::size_t k = 0; k < after.size(); ++k) {
            const auto& lk = after[k];
            if (!lk.matched || lk.shown_after || (phase == 1 && !unshaped[k].shown_after)) continue;
            double target = lk.z - policy.lakes.bed_below_level;
            if (phase == 0) {
            int c0 = side, c1 = -1, r0 = side, r1 = -1;
            for (const auto i : members[k]) { const int c = static_cast<int>(i % side), r = static_cast<int>(i / side); c0 = std::min(c0, c); c1 = std::max(c1, c); r0 = std::min(r0, r); r1 = std::max(r1, r); }
            double shore = 1.7976931348623157e308;
            for (int rr = std::max(0, r0 - 1); rr <= std::min(side - 1, r1 + 1); ++rr)
                for (int cc = c0 - 1; cc <= c1 + 1; ++cc) {
                    const int wrapped = cc < 0 ? cc + PF : cc >= side ? cc - PF : cc;
                    if (wrapped < 0 || wrapped >= side) continue;
                    const auto at = static_cast<std::size_t>(rr) * side + wrapped;
                    if (fine.intent[at] == m.lake) continue;
                    shore = std::min(shore, qv[at]);
                }
            target = std::min(lk.level_z, shore - .001) - policy.lakes.bed_below_level;
            }
            for (const auto i : members[k]) {
                const auto s = h.at(static_cast<int>(i / side), canonical_column(static_cast<int>(i % side), strip_f, PF));
                if (h.z[s] > target) { h.z[s] = target; changed = true; }
            }
        }
        if (!changed) { if (phase++ == 0) continue; break; }
        rec.lake_bed_passes = static_cast<std::size_t>(pass) + 1;
    }

    // The fine surface.
    Compiled& t = out.terrain;
    t.side = side; t.extent = fine.extent; t.spacing = spf; t.period = fine.period;
    t.z = phys; t.biome = fine.biome; t.intent = fine.intent; t.landform = fine.landform; t.padding = fine.padding;
    t.intent_counts = fine.intent_counts; t.biome_counts = fine.biome_counts; t.fidelity = fine.fidelity;
    t.north_pole_intent = fine.north_pole_intent; t.south_pole_intent = fine.south_pole_intent; t.padding_rows = fine.padding_rows;
    t.temperature = fine.temperature; t.moisture = fine.moisture; t.ice_cap = fine.ice_cap; t.rocky_coast = fine.rocky_coast; t.snow_months = fine.snow_months;
    // The layer weights on the shaped slopes (MMR-255).
    compile_layers(t, m);
    t.height = q;
    for (std::size_t i = 0; i < t.z.size(); ++i) {
        const double v = t.height.value(i);
        t.max_roundtrip_error = std::max(t.max_roundtrip_error, std::abs(v - t.z[i]));
        if (t.intent[i] == m.ocean && v > -import.engine_truncation) ++t.engine_sign_violations;
        if (is_land(m, t.intent[i]) && v < import.engine_truncation) ++t.engine_sign_violations;
    }
    if (t.max_roundtrip_error > t.height.step / 2 * (1 + 1e-9)) refuse("quantization_error", "16-bit reconstruction error exceeds half a step");
    if (t.engine_sign_violations)
        refuse("coast_sign", "Coastline sign does not survive 16-bit quantization and the engine's vertex truncation at " + std::to_string(t.engine_sign_violations) + " shaped samples");
    for (int r = 0; r < side; ++r)
        for (int c = 0; c + t.period < side; ++c) {
            const auto a = static_cast<std::size_t>(r) * side + c, b = a + static_cast<std::size_t>(t.period);
            if (t.z[a] != t.z[b] || t.height.q[a] != t.height.q[b] || t.biome[a] != t.biome[b] || t.intent[a] != t.intent[b] || t.landform[a] != t.landform[b])
                refuse("seam_mismatch", "Shaped seam pair differs at row " + std::to_string(r) + ", columns " + std::to_string(c) + " and " + std::to_string(c + t.period));
            ++t.seam_pairs;
        }
    check_layer_seams(t);

    // Records: slopes and creases before (the travel compile) and after (the shaped surface), spikes, pads, lakes, the coast mismatch.
    const double td = std::tan(radians(policy.despike.max_slope_deg));
    const auto before = faces(coarse.z, coarse.intent, m, coarse.side, sp, strip, P, rn, rs);
    const auto shaped = faces(t.z, t.intent, m, side, spf, strip_f, PF, 2 * rn, 2 * rs);
    rec.interior_before = stats(before.interior); rec.coast_before = stats(before.coast); rec.crease_before = stats(before.crease);
    rec.interior_after = stats(shaped.interior); rec.coast_after = stats(shaped.coast); rec.crease_after = stats(shaped.crease);
    rec.spikes_before = spikes(coarse.z, coarse.intent, {}, m, coarse.side, sp, strip, P, rn, rs, td);
    std::vector<char> pad_phys(t.z.size(), 0);
    for (int r = 0; r < side; ++r) for (int c = 0; c < side; ++c) pad_phys[static_cast<std::size_t>(r) * side + c] = pad_f[h.at(r, canonical_column(c, strip_f, PF))];
    rec.spikes_after = spikes(t.z, t.intent, pad_phys, m, side, spf, strip_f, PF, 2 * rn, 2 * rs, td);
    for (const auto& pd : pads) {
        const double gx = (pd.x + p.strip()) / spf, gy = (pd.y + p.pole_margin()) / spf;
        const int ix = std::min(static_cast<int>(gx), side - 2), iy = std::min(static_cast<int>(gy), side - 2);
        const double u = gx - ix, v = gy - iy;
        const auto z = [&](int rr, int cc) { return t.z[static_cast<std::size_t>(rr) * side + cc]; };
        double dx, dy;
        if (u >= v) { dx = z(iy, ix + 1) - z(iy, ix); dy = z(iy + 1, ix + 1) - z(iy, ix + 1); }
        else { dx = z(iy + 1, ix + 1) - z(iy + 1, ix); dy = z(iy + 1, ix) - z(iy, ix); }
        const double a = dx / spf, b = dy / spf;
        const double deg = degrees(std::atan(std::sqrt(a * a + b * b)));
        rec.pad_slopes.push_back(deg);
        if (deg > policy.gates.steep_pad_deg) ++rec.pads_over;
    }
    rec.lake_groups = after.size();
    for (std::size_t k = 0; k < after.size(); ++k) {
        auto lk = after[k];
        lk.shown_before = unshaped[k].shown_after;
        lk.canonical = lk.first_column >= strip_f && lk.first_column < strip_f + PF;
        if (lk.matched) {
            lk.bed_below_level = std::all_of(members[k].begin(), members[k].end(), [&](std::size_t i) { return t.z[i] <= lk.level_z - policy.lakes.bed_below_level; });
            const std::set<std::size_t> own(members[k].begin(), members[k].end());
            bool shore_ok = true;
            for (const auto i : members[k]) {
                const int r = static_cast<int>(i / side), c = static_cast<int>(i % side);
                for (int j = 0; j < 8; ++j) {
                    const int rr = r + kDr[j], cc = c + kDc[j];
                    if (rr < 0 || rr >= side || cc < 0 || cc >= side) continue;
                    const auto o = static_cast<std::size_t>(rr) * side + cc;
                    if (!own.count(o) && is_land(m, t.intent[o]) && t.z[o] < lk.level_z + policy.lakes.shore_above_level) shore_ok = false;
                }
            }
            lk.shore_above_level = shore_ok;
        } else ++rec.lakes_unmatched;
        rec.lakes_shown_before += lk.shown_before; rec.lakes_shown_after += lk.shown_after;
        rec.lakes_newly_hidden += lk.shown_before && !lk.shown_after;
        rec.lakes.push_back(lk);
    }
    for (int r = 0; r < cells.rows; ++r)
        for (int c = 0; c < cells.cols; ++c) {
            const auto cls = cells.classes[static_cast<std::size_t>(r) * cells.cols + c];
            if (cls > 2) continue;   // Land 0, Mountain 1, Sea 2; lakes and rivers keep their own surfaces
            const auto centre = static_cast<std::size_t>(2 * (cells.row_offset + r) + 1) * side + static_cast<std::size_t>(2 * (cells.column_offset + c) + 1);
            ++rec.nav_cells_checked;
            if (cls == 2 ? t.z[centre] >= 0 : t.z[centre] <= 0) ++rec.nav_coast_mismatch;
            if (cls == 2 ? fine.z[centre] >= 0 : fine.z[centre] <= 0) ++rec.nav_coast_mismatch_before;
        }
    return out;
}

Value shaping_value(const ShapingRecord& r) {
    const auto num = [](double v) { Value out; out.kind = Value::Kind::Number; out.text = cjson::ecmascript_number(v); return out; };
    const auto flag = [](bool b) { Value v; v.kind = Value::Kind::Bool; v.boolean = b; return v; };
    const auto pair = [&](const SlopeStats& before, const SlopeStats& after) {
        Value o = Value::object();
        for (const auto& [name, s] : {std::pair<const char*, const SlopeStats*>{"before", &before}, {"after", &after}}) {
            Value v = Value::object();
            v.set("p95", num(s->p95)); v.set("max", num(s->max)); v.set("faces", Value::integer(s->faces));
            o.set(name, v);
        }
        return o;
    };
    Value doc = Value::object();
    doc.set("policySha256", Value::string(r.policy_sha256));
    doc.set("sweeps", Value::integer(static_cast<std::uint64_t>(r.sweeps)));
    doc.set("residual", num(r.residual));
    doc.set("samplesChanged", Value::integer(r.samples_changed));
    doc.set("interiorSlope", pair(r.interior_before, r.interior_after));
    doc.set("coastSlope", pair(r.coast_before, r.coast_after));
    doc.set("crease", pair(r.crease_before, r.crease_after));
    doc.set("isolatedSpikes", Value::integer(r.spikes_after));
    doc.set("isolatedSpikesBefore", Value::integer(r.spikes_before));
    Value pads = Value::object();
    pads.set("count", Value::integer(r.pads)); pads.set("merged", Value::integer(r.pads_merged)); pads.set("over25", Value::integer(r.pads_over));
    double worst = 0;
    for (const double s : r.pad_slopes) worst = std::max(worst, s);
    pads.set("maxSlope", num(worst));
    doc.set("pads", pads);
    Value lakes = Value::object();
    lakes.set("groups", Value::integer(r.lake_groups)); lakes.set("shownBefore", Value::integer(r.lakes_shown_before));
    lakes.set("shownAfter", Value::integer(r.lakes_shown_after)); lakes.set("newlyHidden", Value::integer(r.lakes_newly_hidden));
    lakes.set("unmatched", Value::integer(r.lakes_unmatched)); lakes.set("bedPasses", Value::integer(r.lake_bed_passes));
    Value records; records.kind = Value::Kind::Array;
    for (const auto& l : r.lakes) {
        Value v = Value::object();
        v.set("index", Value::integer(static_cast<std::uint64_t>(l.index))); v.set("samples", Value::integer(static_cast<std::uint64_t>(l.samples)));
        v.set("matched", Value::integer(static_cast<std::uint64_t>(l.matched)));
        Value first; first.kind = Value::Kind::Array; first.items = {Value::integer(static_cast<std::uint64_t>(l.first_column)), Value::integer(static_cast<std::uint64_t>(l.first_row))};
        v.set("firstSample", first); v.set("canonical", flag(l.canonical));
        if (l.matched) {
            v.set("levelM", num(l.level_m)); v.set("levelZ", num(l.level_z)); v.set("planeZ", num(l.z)); v.set("clamped", flag(l.clamped));
            v.set("bedMaxZ", num(l.bed_max_z)); v.set("shoreMinZ", num(l.shore_min_z));
            v.set("bedBelowLevel", flag(l.bed_below_level)); v.set("shoreAboveLevel", flag(l.shore_above_level));
        }
        v.set("shownBefore", flag(l.shown_before)); v.set("shownAfter", flag(l.shown_after));
        records.items.push_back(v);
    }
    lakes.set("records", records);
    doc.set("lakes", lakes);
    doc.set("navCoastMismatch", Value::integer(r.nav_coast_mismatch));
    doc.set("navCellsChecked", Value::integer(r.nav_cells_checked));
    doc.set("navCoastMismatchBefore", Value::integer(r.nav_coast_mismatch_before));
    return doc;
}

}  // namespace worldbridge::terrain
