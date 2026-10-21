#include "worldbridge/terrain_compiler.hpp"
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <iostream>
#include <numeric>
#include <set>
#include <stdexcept>
#include "worldbridge/canonical_json.hpp"
#include "worldbridge/projected_world.hpp"
#include "worldbridge/terrain_layers.hpp"

namespace worldbridge::terrain {
namespace {
using cjson::Value;
using json::Error;
using json::Location;

Location nowhere() { return Location{"", 0, 1, 1, "$"}; }
[[noreturn]] void refuse(const std::string& code, const std::string& message) { throw Error(code, message, nowhere()); }

// The surface encoder's rounding (surface.cpp even_round): half to even, independent of the FP environment.
double even_round(double x) {
    const double f = std::floor(x), d = x - f;
    return d < .5 ? f : d > .5 ? f + 1 : std::fmod(f, 2) == 0 ? f : f + 1;
}

const Value& need(const Value& object, const char* key, Value::Kind kind, const std::string& what) {
    const Value* v = object.find(key);
    if (!v) throw Error("missing_field", what + " lacks '" + key + "'", object.at);
    if (v->kind != kind) throw Error("wrong_type", what + " '" + key + "' has the wrong type", v->at);
    return *v;
}
std::vector<int> ints(const Value& object, const char* key, const std::string& what) {
    std::vector<int> out;
    for (const auto& v : need(object, key, Value::Kind::Array, what).items) {
        if (v.kind != Value::Kind::Number || v.text.find_first_not_of("0123456789") != std::string::npos) throw Error("wrong_type", what + " '" + key + "' must hold positive integers", v.at);
        out.push_back(std::atoi(v.text.c_str()));
    }
    return out;
}

int code_of(double v, const char* layer) {
    if (!(v == std::floor(v)) || v < 0 || v > 255) refuse("out_of_range", std::string(layer) + " code " + cjson::ecmascript_number(v) + " is not an integer in 0..255");
    return static_cast<int>(v);
}

Value number(double v) { Value out; out.kind = Value::Kind::Number; out.text = cjson::ecmascript_number(v); return out; }
Value pair(double a, double b) { Value out; out.kind = Value::Kind::Array; out.items = {number(a), number(b)}; return out; }
Value counts(const std::map<int, std::size_t>& m) { Value out = Value::object(); for (const auto& [k, v] : m) out.set(std::to_string(k), Value::integer(v)); return out; }

void append32(std::string& s, std::uint32_t v) { s.append(reinterpret_cast<const char*>(&v), 4); }
void append64(std::string& s, double v) { s.append(reinterpret_cast<const char*>(&v), 8); }
void write(const fs::path& file, const std::string& bytes) {
    std::ofstream out(file, std::ios::binary | std::ios::trunc);
    out.write(bytes.data(), static_cast<std::streamsize>(bytes.size()));
    out.close();
    if (!out) throw std::runtime_error("Cannot write " + file.string());
}

// The source grid position of a logical point, from the projection's inverse (rows beyond a pole line clamp to it).
struct Grid { double column = 0, row = 0; };
Grid source_position(const projection::Projection& p, std::int64_t n, double x, double y) {
    const auto g = p.inverse({x, std::clamp(y, 0.0, p.height())});
    const double n1 = static_cast<double>(n - 1);
    double column = (g.longitude + 180.0) / 360.0 * n1, row = (90.0 - g.latitude) / 180.0 * n1;
    const auto snap = [](double v) { const double r = std::round(v); return std::abs(v - r) < 1e-9 ? r : v; };
    column = snap(column); row = std::clamp(snap(row), 0.0, n1);
    if (column >= n1) column -= n1;
    if (column < 0) column += n1;
    return {column, row};
}

// Nearest sample, half rounding up; the duplicate column n-1 reads as column 0.
std::size_t nearest(std::int64_t n, Grid g) {
    std::int64_t c = static_cast<std::int64_t>(std::floor(g.column + 0.5)), r = static_cast<std::int64_t>(std::floor(g.row + 0.5));
    if (c >= n - 1) c -= n - 1;
    r = std::clamp<std::int64_t>(r, 0, n - 1);
    return static_cast<std::size_t>(r * n + c);
}

// Seam-aware bilinear: column c0 + 1 may be n - 1, the duplicate of column 0.
double bilinear(const std::vector<double>& v, std::int64_t n, Grid g) {
    std::int64_t c0 = static_cast<std::int64_t>(std::floor(g.column)), r0 = static_cast<std::int64_t>(std::floor(g.row));
    double fx = g.column - static_cast<double>(c0), fy = g.row - static_cast<double>(r0);
    if (c0 > n - 2) { c0 = n - 2; fx = 1; }
    if (r0 > n - 2) { r0 = n - 2; fy = 1; }
    const auto at = [&](std::int64_t r, std::int64_t c) { return v[static_cast<std::size_t>(r * n + c)]; };
    const double top = fx == 0 ? at(r0, c0) : (1 - fx) * at(r0, c0) + fx * at(r0, c0 + 1);
    const double bottom = fx == 0 ? at(r0 + 1, c0) : (1 - fx) * at(r0 + 1, c0) + fx * at(r0 + 1, c0 + 1);
    return fy == 0 ? top : (1 - fy) * top + fy * bottom;
}

double compiled_at(const Compiled& t, double xp, double yp) {
    const double c = std::clamp(xp / t.spacing, 0.0, static_cast<double>(t.side - 1)), r = std::clamp(yp / t.spacing, 0.0, static_cast<double>(t.side - 1));
    std::int64_t c0 = std::min<std::int64_t>(static_cast<std::int64_t>(c), t.side - 2), r0 = std::min<std::int64_t>(static_cast<std::int64_t>(r), t.side - 2);
    const double fx = c - static_cast<double>(c0), fy = r - static_cast<double>(r0);
    const auto at = [&](std::int64_t rr, std::int64_t cc) { return t.z[static_cast<std::size_t>(rr * t.side + cc)]; };
    return (1 - fy) * ((1 - fx) * at(r0, c0) + fx * at(r0, c0 + 1)) + fy * ((1 - fx) * at(r0 + 1, c0) + fx * at(r0 + 1, c0 + 1));
}

struct Union {
    std::vector<std::int64_t> parent;
    explicit Union(std::size_t n) : parent(n) { std::iota(parent.begin(), parent.end(), 0); }
    std::int64_t find(std::int64_t a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; }
    void join(std::int64_t a, std::int64_t b) { a = find(a); b = find(b); if (a != b) parent[std::max(a, b)] = std::min(a, b); }
};

void fidelity(Compiled& t, const SourceLayers& L, const projection::Projection& p, const MaterialPolicy& policy) {
    auto& f = t.fidelity;
    const std::int64_t n = L.side;
    const source::NodeGrid nodes(n);
    f.source_cell_x = p.width() / static_cast<double>(n - 1);
    f.source_cell_y = p.height() / static_cast<double>(n - 1);
    f.output_spacing = t.spacing;
    f.output_per_source_x = f.source_cell_x / t.spacing;
    f.output_per_source_y = f.source_cell_y / t.spacing;
    // Output samples that coincide with a source sample, among those inside the logical domain, and the distinct
    // source samples they cover (strip copies show the same logical points twice).
    std::vector<char> hit(static_cast<std::size_t>(nodes.node_count()), 0);
    for (int r = 0; r < t.side; ++r)
        for (int c = 0; c < t.side; ++c) {
            const double y = r * t.spacing - p.pole_margin();
            if (y < 0 || y > p.height()) continue;
            ++f.output_samples_in_domain;
            const auto g = source_position(p, n, c * t.spacing - p.strip(), y);
            if (g.column == std::floor(g.column) && g.row == std::floor(g.row)) {
                ++f.output_samples_on_source_samples;
                hit[static_cast<std::size_t>(nodes.node_of(static_cast<std::int64_t>(g.column), static_cast<std::int64_t>(g.row)))] = 1;
            }
        }
    f.source_samples_represented = static_cast<std::size_t>(std::count(hit.begin(), hit.end(), 1));
    // Every distinct source sample: height loss and coastline agreement at its canonical physical position.
    const auto count = static_cast<std::size_t>(nodes.node_count());
    std::vector<char> land(count, 0), represented(count, 0);
    double sum = 0;
    for (std::int64_t id = 0; id < nodes.node_count(); ++id) {
        const auto s = nodes.sample_of(id);
        const auto i = static_cast<std::size_t>(s.row * n + s.column);
        const auto pole = nodes.pole_of(id);
        const double xp = (pole == source::Pole::None ? p.node(id).x : 0.0) + p.strip();
        const double yp = (pole == source::Pole::North ? 0.0 : pole == source::Pole::South ? p.height() : p.node(id).y) + p.pole_margin();
        const double d = std::abs(compiled_at(t, xp, yp) - p.z(L.height[i]));
        sum += d * d; f.height_max = std::max(f.height_max, d);
        const int want = intent(policy, code_of(L.water_type[i], "water_type"), code_of(L.river[i], "river"));
        const auto out = static_cast<std::size_t>(std::llround(yp / t.spacing) * t.side + std::llround(xp / t.spacing));
        const int got = t.intent[out];
        if (want != got) ++f.coast_mismatches;
        land[id] = want == policy.land || want == policy.river;
        represented[id] = pole == source::Pole::None && (got == policy.land || got == policy.river);
        ++f.source_samples;
    }
    f.height_rms = std::sqrt(sum / static_cast<double>(f.source_samples));
    // Source land components (graph adjacency) with no compiled land sample at any member: lost to resampling.
    Union u(count);
    for (std::int64_t id = 0; id < nodes.node_count(); ++id) {
        if (!land[id]) continue;
        const auto s = nodes.sample_of(id);
        for (std::int64_t dr = -1; dr <= 1; ++dr)
            for (std::int64_t dc = -1; dc <= 1; ++dc) {
                const auto r = s.row + dr;
                if (r < 0 || r > n - 1) continue;
                const auto other = nodes.node_of(((s.column + dc) % (n - 1) + (n - 1)) % (n - 1), r);
                if (other != id && land[other] && nodes.adjacent(id, other)) u.join(id, other);
            }
        if (nodes.pole_of(id) != source::Pole::None)
            for (std::int64_t c = 0; c < n - 1; ++c) { const auto ring = nodes.node_of(c, id == 0 ? 1 : n - 2); if (land[ring]) u.join(id, ring); }
    }
    std::map<std::int64_t, std::pair<std::size_t, bool>> components;
    for (std::int64_t id = 0; id < nodes.node_count(); ++id)
        if (land[id]) { auto& c = components[u.find(id)]; ++c.first; c.second = c.second || represented[id]; }
    f.land_components = components.size();
    for (const auto& [root, c] : components) if (!c.second) { ++f.land_components_lost; f.land_samples_lost += c.first; }
}
}  // namespace

Import load_import(const fs::path& file) {
    const Value doc = cjson::parse(file);
    if (need(doc, "schema", Value::Kind::String, "Terrain import").text != "mnbl.terrain-import" || need(doc, "version", Value::Kind::Number, "Terrain import").text != "1")
        throw Error("unsupported_version", "Terrain import calibration must be mnbl.terrain-import version 1", doc.at);
    const Value& t = need(doc, "terrain", Value::Kind::Object, "Terrain import");
    Import k;
    const auto nodes = ints(t, "nodes", "terrain");
    if (nodes.size() != 2) throw Error("out_of_range", "terrain.nodes must be [x, y]", t.find("nodes")->at);
    k.nodes_x = nodes[0]; k.nodes_y = nodes[1];
    k.node_cells = ints(t, "nodeCells", "terrain");
    k.grid_sides = ints(t, "gridSides", "terrain");
    k.square = need(t, "square", Value::Kind::Bool, "terrain").boolean;
    k.shared_borders = need(t, "sharedNodeBorders", Value::Kind::Bool, "terrain").boolean;
    k.height_limit = std::strtod(need(t, "heightLimit", Value::Kind::Number, "terrain").text.c_str(), nullptr);
    const auto& range = need(t, "extentRange", Value::Kind::Array, "terrain");
    if (range.items.size() != 2) throw Error("out_of_range", "terrain.extentRange must be [min, max]", range.at);
    k.extent_min = std::strtod(range.items[0].text.c_str(), nullptr); k.extent_max = std::strtod(range.items[1].text.c_str(), nullptr);
    k.node_size_decimals = std::atoi(need(t, "nodeSizeDecimals", Value::Kind::Number, "terrain").text.c_str());
    const Value& h = need(doc, "height", Value::Kind::Object, "Terrain import");
    k.height_bits = std::atoi(need(h, "bits", Value::Kind::Number, "height").text.c_str());
    k.range_decimals = std::atoi(need(h, "rangeDecimals", Value::Kind::Number, "height").text.c_str());
    k.zero_range_span = std::strtod(need(h, "zeroRangeSpan", Value::Kind::Number, "height").text.c_str(), nullptr);
    k.engine_truncation = std::strtod(need(h, "engineVertexTruncation", Value::Kind::Number, "height").text.c_str(), nullptr);
    if (k.height_bits != 16) throw Error("unsupported_version", "The proven encoder stores 16-bit heights only", h.at);
    if (!(k.extent_min > 0 && k.extent_max > k.extent_min && k.height_limit > 0 && k.zero_range_span > 0 && k.engine_truncation >= 0) ||
        k.node_size_decimals < 0 || k.node_size_decimals > 6 || k.range_decimals < 0 || k.range_decimals > 6)
        throw Error("out_of_range", "Terrain import numeric limits are inconsistent", doc.at);
    k.weight_slots = std::atoi(need(need(doc, "materials", Value::Kind::Object, "Terrain import"), "slots", Value::Kind::Number, "materials").text.c_str());
    if (k.weight_slots < 2 || k.weight_slots > 16) throw Error("out_of_range", "materials.slots must be 2..16", doc.at);
    if (k.nodes_x <= 0 || k.nodes_x != k.nodes_y || k.grid_sides.empty() || k.grid_sides.size() != k.node_cells.size()) throw Error("out_of_range", "Terrain import grid description is inconsistent", t.at);
    for (std::size_t i = 0; i < k.grid_sides.size(); ++i)
        if (k.grid_sides[i] != k.nodes_x * k.node_cells[i] + 1) throw Error("out_of_range", "gridSides must equal nodes * nodeCells + 1", t.at);
    k.sha256 = cjson::sha256_hex(cjson::canonical(doc));
    return k;
}

SourceLayers layers_of(const source::SourceWorld& world, double snow_threshold) {
    SourceLayers L;
    L.side = static_cast<std::int64_t>(world.metadata.grid_side);
    L.height = world.read_layer("height").values;
    L.biome = world.read_layer("biome").values;
    L.water_type = world.read_layer("water_type").values;
    L.river = world.read_layer("river").values;
    L.landform = world.read_layer("landform").values;
    L.temperature = world.read_layer("temperature").values;
    L.moisture = world.read_layer("moisture").values;
    L.ice_cap = world.read_layer("ice_cap").values;
    L.rocky_coast = world.read_layer("rocky_coast").values;
    L.biomes = world.biomes;
    L.landforms = world.landforms;
    L.snow_months = world.snow_months(snow_threshold);
    return L;
}

Quantized quantize(const std::vector<double>& z, int bits, int decimals, double zero_span) {
    if (z.empty()) refuse("invalid_argument", "Nothing to quantize");
    if (bits != 16) refuse("unsupported_version", "Only 16-bit heights are stored");
    const double scale = std::pow(10.0, decimals), levels = 65535;
    Quantized out;
    const auto [lo, hi] = std::minmax_element(z.begin(), z.end());
    out.hmin = std::floor(*lo * scale) / scale;
    out.hmax = std::ceil(*hi * scale) / scale;
    if (out.hmax <= out.hmin) { out.hmax = out.hmin + zero_span; out.zero_range = true; }
    out.step = (out.hmax - out.hmin) / levels;
    out.q.resize(z.size());
    for (std::size_t i = 0; i < z.size(); ++i)
        out.q[i] = static_cast<std::uint16_t>(std::clamp(even_round((z[i] - out.hmin) / (out.hmax - out.hmin) * levels), 0.0, levels));
    return out;
}

Compiled compile(const SourceLayers& L, const projection::Projection& p, const MaterialPolicy& policy, const Import& import, int side) {
    const auto at_side = std::find(import.grid_sides.begin(), import.grid_sides.end(), side);
    if (at_side == import.grid_sides.end()) refuse("out_of_range", "Output side " + std::to_string(side) + " is not one the terrain import calibration lists");
    if (static_cast<int>(policy.layer_set.size()) > import.weight_slots) refuse("out_of_range", "The layer set has more layers than the engine's weight slots");
    const std::int64_t n = L.side;
    const auto samples = static_cast<std::size_t>(n * n);
    if (n != p.grid().side() || L.height.size() != samples || L.biome.size() != samples || L.water_type.size() != samples || L.river.size() != samples || L.landform.size() != samples ||
        L.temperature.size() != samples || L.moisture.size() != samples || L.ice_cap.size() != samples || L.rocky_coast.size() != samples || L.snow_months.size() != samples)
        refuse("broken_join", "Source layers do not match the projection's grid");
    check_taxonomy(policy, L.biomes, L.landforms);
    std::set<std::int64_t> exported;
    for (const auto& b : L.biomes) exported.insert(b.id);
    Compiled t;
    t.side = side;
    t.extent = p.width() + 2 * p.strip();
    if (std::abs(p.height() + 2 * p.pole_margin() - t.extent) > 1e-9 * t.extent)
        refuse("non_square_domain", "The projection's physical domain is not square; the proven encoder writes square terrain only");
    if (t.extent < import.extent_min || t.extent > import.extent_max) refuse("out_of_range", "Terrain extent is outside the import calibration's extent range");
    const double node_scale = std::pow(10.0, import.node_size_decimals), node_size = t.extent / import.nodes_x * node_scale;
    if (std::abs(node_size - even_round(node_size)) > 1e-7) refuse("out_of_range", "extent / nodes is not representable at the calibrated XML precision");
    t.spacing = t.extent / (side - 1);
    const double period = p.width() / t.spacing;
    t.period = std::llround(period);
    if (std::abs(period - static_cast<double>(t.period)) > 1e-9 || t.period < 1 || t.period > side - 1)
        refuse("seam_not_on_grid", "W is not a whole number of output columns, so seam pairs cannot match exactly");
    const auto out = static_cast<std::size_t>(side) * static_cast<std::size_t>(side);
    t.z.resize(out); t.biome.resize(out); t.intent.resize(out); t.landform.resize(out); t.padding.resize(out); t.white.resize(out);
    t.temperature.resize(out); t.moisture.resize(out); t.ice_cap.resize(out); t.rocky_coast.resize(out); t.snow_months.resize(out);
    const auto& white = policy.permanent_white;
    // Logical x from the canonical column when the strip is a whole number of columns, so seam pairs share bits even
    // when W is not a dyadic number; otherwise the physical position (seam pairs then rely on exact arithmetic).
    const double strip_columns = p.strip() / t.spacing;
    const bool column_aligned = std::abs(strip_columns - std::round(strip_columns)) < 1e-9;
    const auto S = static_cast<std::int64_t>(std::llround(strip_columns));
    for (int r = 0; r < side; ++r)
        for (int c = 0; c < side; ++c) {
            const auto i = static_cast<std::size_t>(r) * side + c;
            const double x = column_aligned ? static_cast<double>(((c - S) % t.period + t.period) % t.period) * t.spacing : c * t.spacing - p.strip();
            const double y = r * t.spacing - p.pole_margin();
            const auto g = source_position(p, n, x, y);
            double z = p.z(bilinear(L.height, n, g));
            const auto s = nearest(n, g);
            const int biome = code_of(L.biome[s], "biome"), landform = code_of(L.landform[s], "landform");
            if (!exported.count(biome)) refuse("unmapped_category", "Biome " + std::to_string(biome) + " in the raster is not in the exported terrain.biomes catalogue");
            const int purpose = intent(policy, code_of(L.water_type[s], "water_type"), code_of(L.river[s], "river"));
            t.temperature[i] = L.temperature[s]; t.moisture[i] = L.moisture[s]; t.ice_cap[i] = L.ice_cap[s]; t.rocky_coast[i] = L.rocky_coast[s];
            t.snow_months[i] = L.snow_months[s];
            if (static_cast<std::size_t>(landform) >= policy.landforms.size()) refuse("unmapped_category", "landform " + std::to_string(landform) + " has no mapping");
            if (purpose == policy.ocean && z > -policy.coast_epsilon) { t.max_adjustment = std::max(t.max_adjustment, z + policy.coast_epsilon); z = -policy.coast_epsilon; ++t.lowered; }
            else if ((purpose == policy.land || purpose == policy.river) && z < policy.coast_epsilon) { t.max_adjustment = std::max(t.max_adjustment, policy.coast_epsilon - z); z = policy.coast_epsilon; ++t.raised; }
            if (purpose == policy.lake && z < 0) ++t.lake_below_datum;
            if (std::abs(z) > import.height_limit) refuse("out_of_range", "Height beyond the import calibration's limit");
            t.z[i] = z; t.biome[i] = static_cast<std::uint8_t>(biome); t.landform[i] = static_cast<std::uint8_t>(landform); t.intent[i] = static_cast<std::uint8_t>(purpose);
            t.padding[i] = (y < 0 || y > p.height()) ? 1 : 0;
            // Permanent white (MMR-254): a listed biome, or land (and river) whose snow never melts (snowMonths months at the threshold).
            const auto core = policy.biome_core.find(biome);
            t.white[i] = (core != policy.biome_core.end() && std::find(white.biomes.begin(), white.biomes.end(), core->second) != white.biomes.end()) ||
                         ((purpose == policy.land || purpose == policy.river) && L.snow_months[s] >= white.snow_months) ? 1 : 0;
            ++t.intent_counts[purpose]; ++t.biome_counts[biome];
        }
    // The layer weights (MMR-255), periodic in x so seam pairs see the same neighbours.
    compile_layers(t, policy);
    t.height = quantize(t.z, import.height_bits, import.range_decimals, import.zero_range_span);
    for (std::size_t i = 0; i < out; ++i) {
        const double v = t.height.value(i);
        t.max_roundtrip_error = std::max(t.max_roundtrip_error, std::abs(v - t.z[i]));
        // The engine truncates vertices toward zero (import calibration): the compiled sign must survive that.
        if (t.intent[i] == policy.ocean && v > -import.engine_truncation) ++t.engine_sign_violations;
        if ((t.intent[i] == policy.land || t.intent[i] == policy.river) && v < import.engine_truncation) ++t.engine_sign_violations;
    }
    if (t.max_roundtrip_error > t.height.step / 2 * (1 + 1e-9)) refuse("quantization_error", "16-bit reconstruction error exceeds half a step");
    if (t.engine_sign_violations)
        refuse("coast_sign", "Coastline sign does not survive 16-bit quantization and the engine's vertex truncation at " + std::to_string(t.engine_sign_violations) + " samples");
    // Paired seam columns: column c and column c + period show the same logical x.
    for (int r = 0; r < side; ++r)
        for (int c = 0; c + t.period < side; ++c) {
            const auto a = static_cast<std::size_t>(r) * side + c, b = a + static_cast<std::size_t>(t.period);
            if (t.z[a] != t.z[b] || t.height.q[a] != t.height.q[b] || t.biome[a] != t.biome[b] || t.intent[a] != t.intent[b] || t.landform[a] != t.landform[b] || t.white[a] != t.white[b])
                refuse("seam_mismatch", "Seam pair differs at row " + std::to_string(r) + ", columns " + std::to_string(c) + " and " + std::to_string(c + t.period));
            ++t.seam_pairs;
        }
    check_layer_seams(t);
    for (int r = 0; r < side; ++r) t.padding_rows += t.padding[static_cast<std::size_t>(r) * side];
    // The pole lines themselves: the first and last rows inside the logical domain.
    const auto pole_row = [&](double y) { return std::clamp(static_cast<int>(std::llround((y + p.pole_margin()) / t.spacing)), 0, side - 1); };
    t.north_pole_intent = t.intent[static_cast<std::size_t>(pole_row(0)) * side];
    t.south_pole_intent = t.intent[static_cast<std::size_t>(pole_row(p.height())) * side];
    fidelity(t, L, p, policy);
    return t;
}

std::string heightfield_bytes(const Compiled& t) {
    std::string hf = "MNBLHF01";
    append32(hf, static_cast<std::uint32_t>(t.side));
    for (double v : t.z) append64(hf, v);
    return hf;
}

std::string weights_bytes(const Compiled& t) {
    std::string weights = "MNBLWT02";
    append32(weights, static_cast<std::uint32_t>(t.side));
    append32(weights, static_cast<std::uint32_t>(t.weights.size()));
    for (const auto& name : t.layer_names) { append32(weights, static_cast<std::uint32_t>(name.size())); weights += name; }
    for (const auto& plane : t.weights) weights.append(reinterpret_cast<const char*>(plane.data()), plane.size());
    return weights;
}

Value layers_value(const Compiled& t) {
    Value out; out.kind = Value::Kind::Array;
    for (std::size_t k = 0; k < t.layer_names.size(); ++k) {
        Value v = Value::object();
        v.set("slot", Value::integer(static_cast<std::uint64_t>(k)));
        v.set("name", Value::string(t.layer_names[k]));
        v.set("coverage", number(t.layers.dominant[k]));
        out.items.push_back(v);
    }
    return out;
}

Value permanent_white_value(const Compiled& t) {
    Value v = Value::object();
    v.set("samples", Value::integer(t.layers.white_samples));
    v.set("biomeSamples", Value::integer(t.layers.white_biome));
    v.set("seasonSamples", Value::integer(t.layers.white_season));
    return v;
}

std::string q16_bytes(const Compiled& t) {
    std::string q16 = "MNBLQ16B";
    append32(q16, static_cast<std::uint32_t>(t.side));
    append64(q16, t.height.hmin); append64(q16, t.height.hmax);
    for (auto v : t.height.q) { q16.push_back(static_cast<char>(v >> 8)); q16.push_back(static_cast<char>(v & 0xff)); }
    return q16;
}

std::string masks_bytes(const Compiled& t) {
    std::string masks = "MNBLMK01";
    append32(masks, static_cast<std::uint32_t>(t.side));
    for (const auto* plane : {&t.biome, &t.intent, &t.landform, &t.padding}) masks.append(reinterpret_cast<const char*>(plane->data()), plane->size());
    return masks;
}

int compile_terrain_main(int argc, char** argv) {
    const char* usage = "Usage: worldbridge --compile-terrain SPLIT --contract FILE --projection-policy FILE --materials FILE --import FILE --side N --out NEW_DIRECTORY";
    if (argc != 15) throw std::runtime_error(usage);
    std::map<std::string, std::string> o;
    for (int i = 3; i + 1 < argc; i += 2) {
        const std::string key = argv[i];
        static const std::set<std::string> known{"--contract", "--projection-policy", "--materials", "--import", "--side", "--out"};
        if (!known.count(key)) throw std::runtime_error("Unknown option " + key + "\n" + usage);
        if (!o.emplace(key, argv[i + 1]).second) throw std::runtime_error("Repeated option " + key);
    }
    const fs::path output = fs::absolute(o["--out"]);
    if (fs::exists(output)) throw std::runtime_error("Output must not already exist");
    fs::path partial = output; partial += ".partial";
    if (fs::exists(partial)) throw std::runtime_error("A previous incomplete output exists: " + partial.string() + " (remove it deliberately)");
    const auto started = std::chrono::steady_clock::now();
    const auto world = source::read_source(argv[2], source::load_compatibility(o["--contract"]));
    const auto policy = projection::load_policy(o["--projection-policy"]);
    const projection::Projection proj(policy, projection::frame_of(world));
    const auto materials = load_materials(o["--materials"]);
    const auto import = load_import(o["--import"]);
    const int side = std::atoi(o["--side"].c_str());
    const auto read = std::chrono::steady_clock::now();
    const auto t = compile(layers_of(world, materials.permanent_white.snow_threshold), proj, materials, import, side);
    const auto compiled = std::chrono::steady_clock::now();

    const std::string hf = heightfield_bytes(t), masks = masks_bytes(t), weights = weights_bytes(t), q16 = q16_bytes(t);

    const fs::path& pending = partial;
    fs::create_directories(pending);
    write(pending / "heightfield.bin", hf);
    write(pending / "weights.bin", weights);
    write(pending / "height-q16.bin", q16);
    write(pending / "masks.bin", masks);
    write(pending / "projection.json", proj.document());

    const auto& f = t.fidelity;
    Value doc = Value::object();
    doc.set("schema", Value::string("mnbl.terrain-metadata"));
    doc.set("version", Value::integer(1));
    doc.set("projectionSha256", Value::string(proj.identity()));
    doc.set("materialsSha256", Value::string(materials.sha256));
    doc.set("importSha256", Value::string(import.sha256));
    Value src = Value::object();
    src.set("layoutSeed", Value::string(world.metadata.layout_seed));
    src.set("gridSide", Value::integer(static_cast<std::uint64_t>(world.metadata.grid_side)));
    src.set("cellWorldUnits", pair(f.source_cell_x, f.source_cell_y));
    src.set("height", Value::string("layers.height: the only full-map continuous height the producer exports (no public continuous-height CLI; the 512 m patch API is not a campaign exporter)"));
    doc.set("source", src);
    Value o2 = Value::object();
    o2.set("side", Value::integer(static_cast<std::uint64_t>(side)));
    o2.set("extent", number(t.extent));
    o2.set("spacing", number(t.spacing));
    o2.set("nodes", pair(import.nodes_x, import.nodes_y));
    o2.set("nodeCells", Value::integer(static_cast<std::uint64_t>((side - 1) / import.nodes_x)));
    o2.set("seamPeriodColumns", Value::integer(static_cast<std::uint64_t>(t.period)));
    o2.set("rasterOrientation", Value::string("row-major; row r at physical y = r * spacing (row 0 is the north padding), column c at physical x = c * spacing; physical = logical + (strip, pole margin)"));
    doc.set("output", o2);
    Value h = Value::object();
    h.set("hmin", number(t.height.hmin)); h.set("hmax", number(t.height.hmax)); h.set("step", number(t.height.step));
    h.set("zeroRange", [&] { Value b; b.kind = Value::Kind::Bool; b.boolean = t.height.zero_range; return b; }());
    h.set("rule", Value::string("q = round-half-even(clamp((z - hmin) / (hmax - hmin), 0, 1) * 65535); hmin/hmax = floor/ceil(min/max * 1000) / 1000; zero range -> hmax = hmin + 1"));
    h.set("byteOrder", Value::string("height-q16.bin: big-endian u16 after an 8-byte magic, u32 side and f64 hmin, hmax (little-endian header)"));
    h.set("maxRoundtripError", number(t.max_roundtrip_error));
    h.set("tolerance", number(t.height.step / 2));
    doc.set("height", h);
    Value coh = Value::object();
    coh.set("coastEpsilon", number(materials.coast_epsilon));
    coh.set("oceanLowered", Value::integer(t.lowered));
    coh.set("landRaised", Value::integer(t.raised));
    coh.set("maxAdjustment", number(t.max_adjustment));
    coh.set("engineSignViolations", Value::integer(t.engine_sign_violations));
    coh.set("engineVertexTruncation", number(import.engine_truncation));
    coh.set("lakeBelowDatum", Value::integer(t.lake_below_datum));
    doc.set("coherence", coh);
    Value checks = Value::object();
    checks.set("seamPairs", Value::integer(t.seam_pairs));
    checks.set("nodeBorders", Value::string("shared by construction: the encoder cuts node (ix, iy) as rows and columns [i * cells, i * cells + cells] of the one grid (surface.cpp terrain()); verified on the encoded terrain.bin by tools/summarize_terrain.py"));
    doc.set("checks", checks);
    Value poles = Value::object();
    poles.set("northIntent", Value::integer(static_cast<std::uint64_t>(t.north_pole_intent)));
    poles.set("southIntent", Value::integer(static_cast<std::uint64_t>(t.south_pole_intent)));
    poles.set("paddingRows", Value::integer(t.padding_rows));
    poles.set("padding", Value::string("rows beyond a pole line repeat the pole row: ocean padding for an ocean pole, land padding for a land pole; masks.bin flags them"));
    doc.set("poles", poles);
    doc.set("intentSamples", counts(t.intent_counts));
    doc.set("biomeSamples", counts(t.biome_counts));
    doc.set("layers", layers_value(t));
    doc.set("permanentWhite", permanent_white_value(t));
    Value fid = Value::object();
    fid.set("sourceCellWorldUnits", pair(f.source_cell_x, f.source_cell_y));
    fid.set("outputSpacing", number(f.output_spacing));
    fid.set("outputSamplesPerSourceCell", pair(f.output_per_source_x, f.output_per_source_y));
    fid.set("outputSamplesOnSourceSamples", Value::integer(f.output_samples_on_source_samples));
    fid.set("outputSamplesInDomain", Value::integer(f.output_samples_in_domain));
    fid.set("sourceSamples", Value::integer(f.source_samples));
    fid.set("sourceSamplesRepresented", Value::integer(f.source_samples_represented));
    fid.set("heightRmsAtSourceSamples", number(f.height_rms));
    fid.set("heightMaxAtSourceSamples", number(f.height_max));
    fid.set("coastMismatchedSourceSamples", Value::integer(f.coast_mismatches));
    fid.set("landComponents", Value::integer(f.land_components));
    fid.set("landComponentsLost", Value::integer(f.land_components_lost));
    fid.set("landSamplesLost", Value::integer(f.land_samples_lost));
    fid.set("note", Value::string("Output pixels add no source detail: between source samples the terrain is bilinear. Where an output step is coarser than a source cell, the source samples in between are not represented, and heightRmsAtSourceSamples measures that loss."));
    doc.set("fidelity", fid);
    Value perf = Value::object();
    perf.set("readMilliseconds", Value::integer(static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::milliseconds>(read - started).count())));
    perf.set("compileMilliseconds", Value::integer(static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::milliseconds>(compiled - read).count())));
    doc.set("performance", perf);
    Value files = Value::object();
    for (const char* name : {"heightfield.bin", "weights.bin", "height-q16.bin", "masks.bin", "projection.json"}) files.set(name, Value::string(cjson::sha256_file_hex(pending / name)));
    doc.set("files", files);
    const std::string text = cjson::canonical(doc);
    write(pending / "terrain.json", text);
    fs::rename(pending, output);
    std::cout << text << std::endl;
    return 0;
}

}  // namespace worldbridge::terrain
