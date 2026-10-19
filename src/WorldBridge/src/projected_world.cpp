#include "worldbridge/projected_world.hpp"
#include <algorithm>
#include <cmath>
#include <cstring>
#include <fstream>
#include <iostream>
#include <map>
#include <stdexcept>

namespace worldbridge::projection {
namespace {
using cjson::Value;
using json::Error;
using json::Location;

Location nowhere() { return Location{"", 0, 1, 1, "$"}; }
Value number(double v) { Value out; out.kind = Value::Kind::Number; out.text = cjson::ecmascript_number(v); return out; }
Value signed_integer(std::int64_t v) { Value out; out.kind = Value::Kind::Number; out.text = std::to_string(v); return out; }
Value array() { Value out; out.kind = Value::Kind::Array; return out; }
Value pair(Point p) { Value out = array(); out.items.push_back(number(p.x)); out.items.push_back(number(p.y)); return out; }

// A record's node must agree with its own sample and must not be a pole.
Point place(const Projection& projection, const std::string& what, std::int64_t node, std::int64_t column, std::int64_t row) {
    if (!projection.grid().agrees(node, column, row))
        throw Error("node_mismatch", what + ": node " + std::to_string(node) + " is not sample (" + std::to_string(column) + ", " + std::to_string(row) + ")", nowhere());
    try { return projection.node(node); }
    catch (const Error& e) { throw Error(e.code, what + ": " + e.message, nowhere()); }
}

Value located(const Projection& projection, Point p) {
    Value out = Value::object();
    out.set("position", pair(p));
    out.set("physical", pair(projection.physical(p)));
    return out;
}

Value drawn(const Path& path) {
    Value pieces = array();
    for (const auto& piece : path.pieces) {
        Value points = array();
        for (const auto& p : piece) points.items.push_back(pair(p));
        pieces.items.push_back(points);
    }
    Value links = array();
    for (const auto& link : path.links) links.items.push_back(Value::string(link));
    Value out = Value::object();
    out.set("pieces", pieces);
    out.set("links", links);
    return out;
}

Path route_path(const Projection& projection, const std::string& what, const std::vector<std::int64_t>& nodes) {
    try { return projection.path(nodes); }
    catch (const Error& e) { throw Error(e.code, what + ": " + e.message, nowhere()); }
}

void write(const fs::path& file, const std::string& bytes) {
    std::ofstream out(file, std::ios::binary | std::ios::trunc);
    out.write(bytes.data(), static_cast<std::streamsize>(bytes.size()));
    out.close();
    if (!out) throw std::runtime_error("Cannot write " + file.string());
}
}  // namespace

SourceFrame frame_of(const source::SourceWorld& world) {
    const auto& m = world.metadata;
    const Location at{json::utf8(world.root / "world.json"), 0, 1, 1, "$.topology"};
    if (!m.radius_m) throw Error("missing_field", "topology.radius_m is required for the projection", at);
    if (!m.sea_level_m) throw Error("missing_field", "effective_config.sea_level is required for the projection", Location{at.file, 0, 1, 1, "$.effective_config"});
    if (!(*m.radius_m > 0)) throw Error("out_of_range", "topology.radius_m must be positive", at);
    if (m.effective_globe_radius_m && std::abs(*m.effective_globe_radius_m - *m.radius_m) > 1e-9 * *m.radius_m)
        throw Error("broken_join", "effective_config.globe_radius differs from topology.radius_m", at);
    if (m.longitude_deg != std::vector<double>{-180, 180} || m.latitude_deg != std::vector<double>{90, -90})
        throw Error("out_of_coverage", "The projection needs a full-globe source: longitude_deg [-180, 180], latitude_deg [90, -90]", at);
    if (m.topology_type != "sphere" || !m.duplicate_seam) throw Error("unsupported_version", "The projection needs a sphere with a duplicate seam column", at);
    SourceFrame f;
    f.side = static_cast<std::int64_t>(m.grid_side ? m.grid_side : m.config_size);
    f.radius_m = *m.radius_m;
    f.sea_level_m = *m.sea_level_m;
    f.layout_seed = m.layout_seed;
    return f;
}

Export export_world(const source::SourceWorld& world, const Projection& projection) {
    Export out;
    out.identity = projection.identity();
    const auto height = world.read_layer("height");
    const auto side = static_cast<std::int64_t>(height.side);
    if (side != projection.grid().side()) throw Error("broken_join", "Height layer side differs from the projection's grid", nowhere());
    out.side = side;
    const auto water_type = world.read_layer("water_type");
    if (water_type.side != height.side) throw Error("broken_join", "water_type layer side differs from height", nowhere());
    out.z.resize(height.values.size());
    out.below.resize(height.values.size());
    out.water.resize(height.values.size());
    double lo = 0, hi = 0; std::size_t below = 0, below_not_ocean = 0, ocean_above = 0;
    std::map<int, std::size_t> water_counts;
    for (std::size_t i = 0; i < height.values.size(); ++i) {
        out.z[i] = projection.z(height.values[i]);
        out.below[i] = projection.below_datum(height.values[i]) ? 1 : 0;
        const double code = water_type.values[i];
        if (!(code >= 0 && code <= 255 && code == std::floor(code))) throw Error("out_of_range", "water_type code outside 0..255", nowhere());
        out.water[i] = static_cast<std::uint8_t>(code);
        ++water_counts[out.water[i]];
        below += out.below[i];
        below_not_ocean += out.below[i] && out.water[i] != 1;
        ocean_above += !out.below[i] && out.water[i] == 1;
        lo = i ? std::min(lo, out.z[i]) : out.z[i]; hi = i ? std::max(hi, out.z[i]) : out.z[i];
    }
    out.terrain_bin = "MNBLPT01";
    const auto side32 = static_cast<std::uint32_t>(side);
    out.terrain_bin.append(reinterpret_cast<const char*>(&side32), sizeof(side32));
    out.terrain_bin += out.identity;
    out.terrain_bin.append(reinterpret_cast<const char*>(out.z.data()), out.z.size() * sizeof(double));
    out.terrain_bin.append(reinterpret_cast<const char*>(out.below.data()), out.below.size());
    out.terrain_bin.append(reinterpret_cast<const char*>(out.water.data()), out.water.size());

    Value terrain = Value::object();
    terrain.set("schema", Value::string("mnbl.projected-terrain"));
    terrain.set("version", Value::integer(1));
    terrain.set("projectionSha256", Value::string(out.identity));
    terrain.set("file", Value::string("terrain.bin"));
    terrain.set("sha256", Value::string(cjson::sha256_hex(out.terrain_bin)));
    terrain.set("layer", Value::string("height"));
    terrain.set("side", Value::integer(static_cast<std::uint64_t>(side)));
    terrain.set("layout", Value::string("little-endian: \"MNBLPT01\", u32 side, 64 ASCII hex projection identity, side*side binary64 z, side*side below-datum bytes (0/1), side*side water_type bytes; row-major, row 0 north"));
    terrain.set("samplePosition", Value::string("Projection.sample(column, row): x = W * frac(column / (n - 1) - centralMeridian / 360), y = H * row / (n - 1); column n - 1 repeats column 0"));
    terrain.set("z", Value::string("Projection.z(height) = (height - seaLevel) * verticalScale"));
    terrain.set("belowDatum", Value::string("Projection.below_datum(height): height - seaLevel < 0"));
    terrain.set("water", Value::string("source layers.water_type, unchanged (categorical; never interpolate)"));
    terrain.set("zRange", [&] { Value r = array(); r.items.push_back(number(lo)); r.items.push_back(number(hi)); return r; }());
    terrain.set("belowDatumSamples", Value::integer(below));
    Value codes = Value::object();
    for (const auto& [code, count] : water_counts) codes.set(std::to_string(code), Value::integer(count));
    terrain.set("waterTypeSamples", codes);
    terrain.set("belowDatumNotOcean", Value::integer(below_not_ocean));
    terrain.set("oceanNotBelowDatum", Value::integer(ocean_above));
    terrain.set("samples", Value::integer(height.values.size()));
    out.terrain_json = cjson::canonical(terrain);

    Value objects = Value::object();
    objects.set("schema", Value::string("mnbl.projected-objects"));
    objects.set("version", Value::integer(1));
    objects.set("projectionSha256", Value::string(out.identity));
    Value cities = array();
    for (const auto& c : world.cities) {
        Value v = located(projection, place(projection, "settlement " + c.uid, c.node, c.x, c.z));
        v.set("uid", Value::string(c.uid)); v.set("siteId", signed_integer(c.site_id)); v.set("kind", Value::string(c.kind)); v.set("node", signed_integer(c.node));
        cities.items.push_back(v);
    }
    objects.set("settlements", cities);
    Value hamlets = array();
    std::size_t ports = 0;
    for (const auto& h : world.hamlets) {
        Value v = located(projection, place(projection, "hamlet " + h.uid, h.node, h.x, h.z));
        v.set("uid", Value::string(h.uid)); v.set("kind", Value::string(h.kind)); v.set("node", signed_integer(h.node));
        if (h.harbour) {
            Point sea_point;
            try { sea_point = projection.node(h.harbour->sea_node); }
            catch (const Error& e) { throw Error(e.code, "port of hamlet " + h.uid + ": " + e.message, nowhere()); }
            Value port = located(projection, sea_point);
            port.set("seaNode", signed_integer(h.harbour->sea_node));
            v.set("port", port);
            ++ports;
        }
        hamlets.items.push_back(v);
    }
    objects.set("hamlets", hamlets);
    Value castles = array();
    for (const auto& c : world.castles) {
        Value v = located(projection, place(projection, "castle " + c.uid, c.node, c.x, c.z));
        v.set("uid", Value::string(c.uid)); v.set("node", signed_integer(c.node));
        castles.items.push_back(v);
    }
    objects.set("castles", castles);
    Value keys = array();
    for (const auto& k : world.key_locations) {
        Point p;
        try { p = projection.node(k.node); }
        catch (const Error& e) { throw Error(e.code, "key location " + k.id + ": " + e.message, nowhere()); }
        Value v = located(projection, p);
        v.set("id", Value::string(k.id)); v.set("kind", Value::string(k.kind)); v.set("node", signed_integer(k.node));
        keys.items.push_back(v);
    }
    objects.set("keyLocations", keys);
    Value roads = array();
    std::size_t road_seams = 0, road_poles = 0;
    for (std::size_t i = 0; i < world.routes.size(); ++i) {
        const auto& r = world.routes[i];
        const auto path = route_path(projection, "roads.routes[" + std::to_string(i) + "]", r.nodes);
        for (const auto& link : path.links) (link == "seam" ? road_seams : road_poles)++;
        Value v = drawn(path);
        v.set("from", signed_integer(r.from)); v.set("to", signed_integer(r.to)); v.set("lengthM", number(r.length_m));
        roads.items.push_back(v);
    }
    objects.set("roads", roads);
    Value rivers = array();
    std::size_t river_seams = 0, river_poles = 0;
    for (std::size_t i = 0; i < world.rivers.size(); ++i) {
        const auto& r = world.rivers[i];
        const auto path = route_path(projection, "water.river_segments[" + std::to_string(i) + "]", {r.from, r.to});
        for (const auto& link : path.links) (link == "seam" ? river_seams : river_poles)++;
        Value v = drawn(path);
        v.set("from", signed_integer(r.from)); v.set("to", signed_integer(r.to));
        rivers.items.push_back(v);
    }
    objects.set("rivers", rivers);
    Value counts = Value::object();
    counts.set("settlements", Value::integer(world.cities.size()));
    counts.set("hamlets", Value::integer(world.hamlets.size()));
    counts.set("ports", Value::integer(ports));
    counts.set("castles", Value::integer(world.castles.size()));
    counts.set("keyLocations", Value::integer(world.key_locations.size()));
    counts.set("roads", Value::integer(world.routes.size()));
    counts.set("roadSeamSplits", Value::integer(road_seams));
    counts.set("roadPoleSplits", Value::integer(road_poles));
    counts.set("rivers", Value::integer(world.rivers.size()));
    counts.set("riverSeamSplits", Value::integer(river_seams));
    counts.set("riverPoleSplits", Value::integer(river_poles));
    objects.set("counts", counts);
    out.objects_json = cjson::canonical(objects);
    return out;
}

int project_main(int argc, char** argv) {
    const char* usage = "Usage: worldbridge --project SPLIT --contract FILE --policy FILE --out NEW_DIRECTORY";
    if (argc != 9) throw std::runtime_error(usage);
    std::map<std::string, std::string> options;
    for (int i = 3; i + 1 < argc; i += 2) {
        const std::string key = argv[i];
        if (key != "--contract" && key != "--policy" && key != "--out") throw std::runtime_error(std::string("Unknown option ") + key + "\n" + usage);
        if (!options.emplace(key, argv[i + 1]).second) throw std::runtime_error("Repeated option " + key);
    }
    if (options.size() != 3) throw std::runtime_error(usage);
    const fs::path output = fs::absolute(options["--out"]);
    if (fs::exists(output)) throw std::runtime_error("Output must not already exist");
    const auto contract = source::load_compatibility(options["--contract"]);
    const auto world = source::read_source(argv[2], contract);
    const auto policy = load_policy(options["--policy"]);
    const Projection projection(policy, frame_of(world));
    const auto result = export_world(world, projection);
    fs::path pending = output; pending += ".partial";
    fs::remove_all(pending);
    fs::create_directories(pending);
    write(pending / "projection.json", projection.document());
    write(pending / "terrain.bin", result.terrain_bin);
    write(pending / "terrain.json", result.terrain_json);
    write(pending / "objects.json", result.objects_json);
    Value manifest = Value::object();
    manifest.set("schema", Value::string("mnbl.projected-world"));
    manifest.set("version", Value::integer(1));
    manifest.set("projectionSha256", Value::string(projection.identity()));
    manifest.set("policySha256", Value::string(policy.sha256));
    manifest.set("layoutSeed", Value::string(world.metadata.layout_seed));
    Value files = Value::object();
    for (const char* name : {"projection.json", "terrain.bin", "terrain.json", "objects.json"}) files.set(name, Value::string(cjson::sha256_file_hex(pending / name)));
    manifest.set("files", files);
    const std::string manifest_text = cjson::canonical(manifest);
    write(pending / "manifest.json", manifest_text);
    fs::rename(pending, output);
    std::cout << manifest_text << std::endl;
    return 0;
}

}  // namespace worldbridge::projection
