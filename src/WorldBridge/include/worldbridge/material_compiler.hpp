// Terrain material policy (MMR-26, worldgen/policies/terrain-materials.json): explicit handling for every exported
// biome, water class, river flag and landform, and the weights of the material layers the proven terrain template
// provides (MMR-16: grass at full weight, rock_k painted over it). Unknown categories fail; nothing silently becomes grass.
#pragma once
#include <cstdint>
#include <filesystem>
#include <map>
#include <string>
#include <vector>
#include "worldbridge/source_world.hpp"

namespace worldbridge::terrain {
namespace fs = std::filesystem;

struct MaterialPolicy {
    std::vector<std::string> channels;               // engine layer order; must equal the import calibration's template layers
    std::map<std::int64_t, std::string> biome_core;  // biome id -> core name
    std::map<std::int64_t, int> biome_rock;          // biome id -> rock_k weight 0..255
    std::map<int, int> water_intent;                 // water_type code -> intent value
    std::map<int, int> river_intent;                 // river code -> intent value, or -1 for "none"
    std::vector<std::string> landforms;              // landform index -> name
    int land = 0, ocean = 1, lake = 2, river = 3;    // intent values
    bool river_overrides_land = true;
    double slope_start = 0.6, slope_full = 1.2, coast_epsilon = 0.05;
    std::string sha256;                              // of the file's canonical form
};

// Strict: refuses unknown members, duplicate ids or codes, out-of-range weights and a non-increasing slope ramp.
MaterialPolicy load_materials(const fs::path& file);

// Every exported biome id must be in the policy with the same core name (the policy may list more), and the exported
// landform names must equal the policy's, in order. Refuses (json::Error "unmapped_category") otherwise. The compiler
// separately refuses raster biome ids that the exported catalogue does not list.
void check_taxonomy(const MaterialPolicy& policy, const std::vector<source::BiomeEntry>& biomes, const std::vector<std::string>& landforms);

// Refuses an unknown biome id ("unmapped_category").
int biome_rock(const MaterialPolicy& policy, std::int64_t biome);
// round-half-even(clamp((slope - start) / (full - start), 0, 1) * 255)
int slope_rock(const MaterialPolicy& policy, double slope);
// The navigation intent of one sample; refuses unknown water or river codes ("unmapped_category").
int intent(const MaterialPolicy& policy, int water_type, int river);

}  // namespace worldbridge::terrain
