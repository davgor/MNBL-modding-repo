// Bounded reader for FWGC++ split-world documents (fwg generate --split). One streaming pass over
// world.json indexes the top-level members, validates the contract and every layer raster without
// materializing it, and extracts the bounded records the campaign compiler needs. Layers are
// materialized only on request, by seeking to their recorded offset. Plan files are reached only
// through the root's *_plans.files indexes, never by deriving names from UIDs.
//
// Memory bound: the tokenizer's limits (1 MiB token, depth 64, 16 MiB of open-object keys) plus the
// retained records, capped at Compatibility::max_retained_bytes; a materialized layer costs 8*side^2.
#pragma once
#include <cstdint>
#include <filesystem>
#include <map>
#include <optional>
#include <string>
#include <utility>
#include <vector>
#include "worldbridge/json_stream.hpp"

namespace worldbridge::source {
namespace fs = std::filesystem;
using json::Error;
using json::Location;

// worldgen/contracts/source-compatibility.json, the versions and blocks the compiler consumes.
struct Compatibility {
    std::string schema;
    int schema_version = 0, completed_stage = 0, split_version = 0, scene_version = 0;
    std::string topology_type;
    bool duplicate_seam = true;
    std::map<std::string, std::vector<int>> block_versions;   // block -> supported versions
    std::vector<std::string> required_systems, required_layers, integral_layers;
    std::map<std::string, int> plan_versions;                 // plan kind -> per-file version
    double seam_tolerance = 0, pole_tolerance = 0;
    std::size_t max_side = 0, max_records = 0, max_plan_files = 0;
    std::uint64_t max_plan_bytes = 0, max_retained_bytes = 0;
};
Compatibility load_compatibility(const fs::path& file);

struct Metadata {
    std::string schema, scene_path, layout_seed, topology_type;  // layout seed stays a decimal string
    std::string generator_version;                                // descriptive only (number or string text)
    int schema_version = 0, completed_stage = 0, split_version = 0, scene_version = 0;
    std::int64_t crust_seed = 0, detail_seed = 0;
    bool duplicate_seam = false;
    std::size_t config_size = 0, grid_side = 0;
    std::vector<std::string> systems;
    std::map<std::string, int> block_versions;  // top-level blocks declaring an integer version
    // Physical frame for the projection (MMR-25). Optional here: the reader contract does not require them,
    // the projector refuses a world that lacks them. topology.radius_m and effective_config are metres,
    // never the design-space config.
    std::optional<double> radius_m, effective_globe_radius_m, sea_level_m;
    // Producer community limits (MMR-27): effective_config caps and the founding report.
    std::optional<std::int64_t> settlement_count, hamlets_per_core, fortress_count, founding_target_cities, founding_placed_cities;
    std::vector<double> longitude_deg, latitude_deg;  // topology ranges as written
};

// Top-level member index: where each root member's value starts and ends.
struct Span { Location start; std::uint64_t end = 0; };

struct LayerInfo {
    std::string name;
    Location start;
    bool integral = true;  // every sample is integer-valued
    double minimum = 0, maximum = 0;
};

struct Layer {
    std::string name;
    std::size_t side = 0;
    std::vector<double> values;  // row-major, side * side
    double at(std::size_t row, std::size_t column) const { return values[row * side + column]; }
};

struct City {           // settlements.sites[]
    std::int64_t site_id = 0, node = 0, x = 0, z = 0, population_estimate = 0;
    std::string uid, name, kind, city_class, civilization_id, layer;
    std::optional<double> freshwater_distance_m;  // nullable in the source
    std::optional<std::int64_t> urban_population_estimate, rural_population_estimate;
    bool founding_capital = false;
    std::vector<std::string> hamlet_ids, fortress_ids;  // from humans.cores[] joined by site_id
    std::string culture_id;
    std::string plan_path;
};

struct Harbour {        // the harbour-shaped humans.hamlets[] rows
    std::int64_t sea_node = 0;
    double harbor_quality = 0, landing_distance_m = 0, winter_fishing_fraction = 0;
    std::string layer;
    bool trade_terminal = false;
    std::size_t fishing_nodes = 0, monthly_fish = 0;
};

struct Hamlet {         // humans.hamlets[]
    std::string id, uid, kind, role, culture_id;  // id is an ordinal; uid (hamlet-node-<n>) is the stable identity
    std::string founded_by;                       // "player" for player-adopted rows; empty for generated ones
    std::int64_t core_id = 0, node = 0, x = 0, z = 0;
    std::optional<Harbour> harbour;
    std::string plan_path;
};

struct Castle {         // humans.fortresses[]
    std::string id, uid, kind, core_uid, culture_id;
    std::string founded_by;  // "player" for player-adopted rows; empty for generated ones
    std::int64_t core_id = 0, node = 0, x = 0, z = 0;
    bool garrisoned = true;  // false when the writer's sentinel core (-1, "") marks no garrison city
    std::string plan_path;
};

struct KeyLocation {    // key_locations.sites[]
    std::string id, kind, family, name, layer;
    std::int64_t node = 0;
    std::string plan_path;
};

struct Route {          // roads.routes[]: a node path through the sphere graph
    std::int64_t from = 0, to = 0;  // settlement site ids
    double length_m = 0;
    std::vector<std::int64_t> nodes;
    std::vector<std::pair<std::int64_t, std::int64_t>> river_crossings;  // producer-reported [node, node] steps (bridge candidates)
};

struct RiverSegment {   // water.river_segments[]: [node, receiver node]
    std::int64_t from = 0, to = 0;
};

struct Realm {          // heroes.realms[] (MMR-27): a generated polity; source relationships, not a Kingdom definition
    std::string uid, name, english_name, civilization_id, civilization_name, parent_race_id, culture_id, capital_uid;
    std::optional<std::string> sovereign_uid;  // nullable in the source
    std::vector<std::string> city_uids;
};

struct Person {         // heroes.people[] (MMR-27): only the identity fields the campaign compiler joins on
    std::string uid, role, status, name, display_name, civilization_id, race_id, home_kind, home_uid;
    std::string presence_kind, presence_uid;  // where the person is found (presence.site_kind, presence.uid)
    std::optional<std::string> realm_uid;
};

struct CultureEntry {   // humans.cultures[] (MMR-27)
    std::string id, civilization_id;
    std::vector<std::int64_t> city_ids;
};

struct BiomeEntry {     // terrain.biomes[]: the exported natural biome catalogue (MMR-26)
    std::int64_t id = 0;
    std::string core, name;
};

struct PlanFile {
    std::string kind, id, relative_path;
    fs::path resolved;
    std::uint64_t bytes = 0;
    int version = 0;
    std::string status;
};

struct Statistics {
    std::uint64_t root_bytes = 0, plan_bytes = 0, scene_bytes = 0, retained_bytes = 0;
    std::size_t layers = 0, layer_samples = 0, plan_files = 0;
    double seconds = 0;
};

struct SourceWorld {
    fs::path root;
    Metadata metadata;
    std::map<std::string, Span> members;
    std::map<std::string, LayerInfo> layers;
    std::vector<City> cities;
    std::vector<Hamlet> hamlets;
    std::vector<Castle> castles;
    std::vector<KeyLocation> key_locations;
    std::vector<Route> routes;            // empty when the block has none
    std::vector<RiverSegment> rivers;
    std::vector<BiomeEntry> biomes;       // terrain.biomes; empty when absent
    std::vector<Realm> realms;            // heroes.realms; empty when absent
    std::vector<Person> people;           // heroes.people; empty when absent
    std::vector<CultureEntry> cultures;   // humans.cultures; empty when absent
    std::vector<std::string> landforms;   // terrain.landforms; empty when absent
    std::vector<PlanFile> plans;
    Statistics statistics;
    // Materializes one validated layer (side * side doubles) by seeking to its recorded offset.
    Layer read_layer(const std::string& name) const;
};

struct ReadOptions {
    bool validate_plans = true;  // open every indexed plan file and check its identity and version
};

SourceWorld read_source(const fs::path& split_root, const Compatibility& contract, const ReadOptions& options = {});

// The identity the compiler carries forward (worldgen/contracts/source-identity.schema.json): the
// 64-bit layout seed stays a canonical decimal string. read_identity validates the document strictly.
std::string write_identity(const Metadata& metadata);
Metadata read_identity(const fs::path& file);

}  // namespace worldbridge::source
