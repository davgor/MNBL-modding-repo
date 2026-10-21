// Terrain material policy (MMR-26, worldgen/policies/terrain-materials.json): explicit handling for every exported
// biome, water class, river flag and landform, and the weights of the material layers the proven terrain template
// provides (MMR-16: grass at full weight, rock_k painted over it). Unknown categories fail; nothing silently becomes grass.
// Version 2 (map epic 2, MMR-248) is the shared biome table: per biome its face type, layer mix, battle class, colour grade
// and snow treatment, the 15-slot layer set, the layer rules, the permanent-white mask and the live-mutation remap
// (docs/biome-presentation.md). MMR-255 compiles the layer set's weights (weights.bin v2, terrain_layers.hpp) and plants map trees
// from the flora block; the two-layer channels and per-biome rock weights of the MMR-26 compile are gone.
#pragma once
#include <cstdint>
#include <filesystem>
#include <map>
#include <string>
#include <vector>
#include "worldbridge/source_world.hpp"

namespace worldbridge::terrain {
namespace fs = std::filesystem;

// One row of the shared biome table. terrain_type is empty for a water biome (ocean, lake); colour_grade is -1 for water
// (the temperature band), colour_grade_wet -1 when the biome has no wet split.
struct BiomePresentation {
    bool water = false;
    std::string terrain_type, battle_class, snow;     // snow: seasonal | permanent
    std::map<std::string, double> layers;             // layerSet name -> target fraction, summing to 1
    int colour_grade = -1, colour_grade_wet = -1;
};

struct LayerSlot {
    int slot = 0;
    std::string name, diffuse, normal, specular, height, physics, use;   // texture names; "none" for an unused map
    double scale = 0;                                // tile size in map units (MMR-249: scale 20 repeats every 20 units)
    int tint_mode = 0;                               // albedo_factor_mode: 0 none, 1 adds the colour, 2 multiplies (only darkens)
    double tint[3] = {1, 1, 1};
    bool optional = false;
};

struct LayerRules {
    int kernel_samples = 0, coast_samples = 0, water_edge_samples = 0;
    std::string slope_layer, desert_slope_layer, mountain_landform, mountain_layer;
    double mountain_share = 0;
    std::string moisture_biome, moisture_wet_layer, moisture_dry_layer;
    double moisture_split = 0, moisture_band = 0;    // the wet share runs linearly from 0 at split - band to 1 at split + band
    std::string coast_layer, coast_rocky_layer, water_edge_layer, ice_cap_layer, cold_layer;
    std::vector<std::string> water_edge_intents;
    double ice_cap_threshold = 0, cold_below_c = 0;
};

// The one permanent-white mask: these biomes plus land samples with snow_months months of snow >= snow_threshold.
struct PermanentWhite {
    std::vector<std::string> biomes;
    int snow_months = 12;
    double snow_threshold = 0.5;
    std::string layer, terrain_type;
};

// The live-mutation remap: school -> land-valid TerrainType (no Dune), per-core overrides, hidden schools' look schools.
struct MutationRemap {
    std::vector<std::string> land_valid;
    std::map<std::string, std::string> schools, hidden_looks;
    std::map<std::string, std::map<std::string, std::string>> core_overrides;   // school -> biome core -> type
    std::string generated_variants, battle_class, threshold_source, threshold_key, dominance_key;
};

// How target fractions become the stored bytes (MMR-249: alpha-over from the highest slot down, weight linear in the byte divided by
// smooth_blend_amount, bytes <= 2 skipped): a_k = f_k / (f_0 + ... + f_k), byte = round-half-even(a_k * 255), bytes below min_byte
// dropped; slot 0 is 255 everywhere. use_transparency_of_ground_slope stays off (it height-blends).
struct Alpha { std::string rule; double smooth_blend = 1; int min_byte = 3; bool slope_transparency = false; };

// Map trees (MMR-249 r1 go; flora.bin FLR2). Per biome core the layer whose target fraction scales the density and the tree kinds.
struct FloraKind { std::string name; int variations = 1; double weight = 0; };
struct FloraBiome { std::string layer; std::vector<FloraKind> kinds; };
struct FloraPolicy {
    std::map<std::string, FloraBiome> biomes;        // biome core -> trees
    double density = 0;                              // trees per square map unit at full layer weight
    double scale_min = 0, scale_max = 0;
    double pad_margin = 0, slope_share = 0;          // clear of settlement pads (radius + margin); no tree where the slope share exceeds this
    int coast_samples = 0;                           // clear of the sea and lakes by this many samples (never on a river sample)
    bool roads = true;                               // clear of travel road cells
    std::size_t max_trees = 0;                       // refuses a world that would plant more (all periodic images)
};

struct MaterialPolicy {
    std::map<std::int64_t, std::string> biome_core;  // biome id -> core name
    std::map<std::int64_t, BiomePresentation> presentation;   // biome id -> its row of the shared table
    std::vector<LayerSlot> layer_set;                // by slot, from 0
    LayerRules rules;
    PermanentWhite permanent_white;
    MutationRemap mutation;
    Alpha alpha;
    FloraPolicy flora;
    std::map<int, int> water_intent;                 // water_type code -> intent value
    std::map<int, int> river_intent;                 // river code -> intent value, or -1 for "none"
    std::vector<std::string> landforms;              // landform index -> name
    int land = 0, ocean = 1, lake = 2, river = 3;    // intent values
    bool river_overrides_land = true;
    double slope_start = 0.3, slope_full = 0.6, coast_epsilon = 0.05;
    std::string sha256;                              // of the file's canonical form
};

// The land TerrainTypes a biome face may take and a live mutation may retype a land face to (no Dune, Bridge, Fording or Beach).
const std::vector<std::string>& land_face_types();

// Strict: refuses unknown members, duplicate ids or codes, out-of-range weights and a non-increasing slope ramp, and every
// shared-table rule (layer names and fractions, face types, battle classes, grades, the permanent-white and mutation tables).
MaterialPolicy load_materials(const fs::path& file);

// Every exported biome id must be in the policy with the same core name (the policy may list more), and the exported
// landform names must equal the policy's, in order. Refuses (json::Error "unmapped_category") otherwise. The compiler
// separately refuses raster biome ids that the exported catalogue does not list.
void check_taxonomy(const MaterialPolicy& policy, const std::vector<source::BiomeEntry>& biomes, const std::vector<std::string>& landforms);

// The slope layer's share of a sample: clamp((slope - start) / (full - start), 0, 1), slope the central-difference gradient.
double slope_share(const MaterialPolicy& policy, double slope);
// The biome row of an id; refuses an unknown biome id ("unmapped_category").
const BiomePresentation& biome_row(const MaterialPolicy& policy, std::int64_t biome);
// The navigation intent of one sample; refuses unknown water or river codes ("unmapped_category").
int intent(const MaterialPolicy& policy, int water_type, int river);

}  // namespace worldbridge::terrain
