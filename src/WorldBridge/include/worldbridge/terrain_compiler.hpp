// Campaign terrain compiler (MMR-26): bakes a split world's layers through the accepted projection (MMR-25) into the
// square terrain the proven surface encoder imports (MMR-16, worldgen/calibration/terrain-import).
//
// Every output sample (row r, column c) sits at physical (c, r) * spacing, spacing = extent / (side - 1), where the
// physical domain is the projection's (logical + strip, logical + pole margin) and must be square. Its source position
// is the projection's inverse; rows beyond a pole line repeat the pole row, and x wraps every W.
//   height (continuous)        seam-aware bilinear: column n-1 is the duplicate of column 0, so the seam interpolates
//   biome, water, river, landform (categorical)   nearest sample, half rounding up; never mixed
//   intent                     land / ocean / lake / river from water_type and river through the material policy
//   coherence                  ocean <= -coastEpsilon, land and river >= +coastEpsilon (lakes as compiled)
//   permanent white (MMR-254)  the material policy's permanentWhite: a sample whose nearest source biome it lists, or a land or river
//                              sample whose nearest source sample has snowMonths months of snow at or above snowThreshold; not written
//                              to masks.bin (the travel compiler's Snow face and MMR-255's permanent_snow layer read it)
//   climate (temperature, moisture, ice_cap, rocky_coast, snow months)   nearest sample, like the categories
//   layer weights              the layer compile (terrain_layers.hpp, MMR-255): one alpha plane per layerSet slot, x periodic
//   16-bit                     the import calibration's global range and round-half-even rule, big-endian
#pragma once
#include <cstdint>
#include <filesystem>
#include <map>
#include <string>
#include <vector>
#include "worldbridge/canonical_json.hpp"
#include "worldbridge/material_compiler.hpp"
#include "worldbridge/projection.hpp"
#include "worldbridge/source_world.hpp"

namespace worldbridge::terrain {
namespace fs = std::filesystem;

// worldgen/calibration/terrain-import/terrain-import.json: the engine terrain format as calibration output.
struct Import {
    std::vector<int> grid_sides, node_cells;
    int nodes_x = 0, nodes_y = 0;
    bool square = false, shared_borders = false;
    double height_limit = 0, extent_min = 0, extent_max = 0;
    int node_size_decimals = 0;
    int height_bits = 0, range_decimals = 0;
    double zero_range_span = 0, engine_truncation = 0;
    int weight_slots = 16;                             // materials.slots: the engine's layer limit
    std::string sha256;
};
Import load_import(const fs::path& file);

struct SourceLayers {
    std::int64_t side = 0;
    std::vector<double> height, biome, water_type, river, landform;  // row-major, side * side
    // MMR-255: the layer rules' climate inputs, row-major side * side: degrees C, 0..1, 0..1, 0..1.
    std::vector<double> temperature, moisture, ice_cap, rocky_coast;
    std::vector<source::BiomeEntry> biomes;
    std::vector<std::string> landforms;
    std::vector<std::uint8_t> snow_months;                           // per sample, months with snow >= the permanent-white threshold
};
// snow_threshold: the material policy's permanentWhite.snowThreshold (SourceWorld::snow_months).
SourceLayers layers_of(const source::SourceWorld& world, double snow_threshold);

struct Quantized {
    double hmin = 0, hmax = 0, step = 0;
    bool zero_range = false;
    std::vector<std::uint16_t> q;
    double value(std::size_t i) const { return hmin + q[i] * step; }
};
// The import calibration's rule: global range rounded outward to 10^-decimals, zero range -> hmax = hmin + span,
// q = round-half-even(clamp((z - hmin) / (hmax - hmin), 0, 1) * (2^bits - 1)). Defaults are the calibrated values.
Quantized quantize(const std::vector<double>& z, int bits = 16, int decimals = 3, double zero_span = 1);

struct Fidelity {
    double source_cell_x = 0, source_cell_y = 0, output_spacing = 0;   // world units
    double output_per_source_x = 0, output_per_source_y = 0;           // output samples per source cell
    std::size_t output_samples_on_source_samples = 0, output_samples_in_domain = 0;
    std::size_t source_samples_represented = 0;                         // distinct source samples some output sample sits on
    std::size_t source_samples = 0;
    double height_rms = 0, height_max = 0;                             // compiled terrain at source samples vs source z
    std::size_t coast_mismatches = 0;                                  // source intent vs nearest output intent
    std::size_t land_components = 0, land_components_lost = 0, land_samples_lost = 0;
};

struct Compiled {
    int side = 0;
    double extent = 0, spacing = 0;
    std::int64_t period = 0;                                           // output columns per W
    std::vector<double> z;                                             // world units after coherence
    Quantized height;
    std::vector<std::uint8_t> biome, intent, landform, padding;        // padding: 1 on rows beyond a pole line
    std::vector<std::uint8_t> white;                                   // 1 in the permanent-white mask (MMR-254; not in masks.bin)
    std::vector<double> temperature, moisture, ice_cap, rocky_coast;   // the climate inputs at each output sample (nearest)
    std::vector<std::uint8_t> snow_months;
    std::vector<std::string> layer_names;                              // terrain-materials.json layerSet, by slot
    std::vector<std::vector<std::uint8_t>> weights;                    // one alpha plane per slot (weights.bin v2); plane 0 is 255
    std::vector<double> flora_share;                                   // the target fraction of the sample biome's flora layer (0 elsewhere)
    struct LayerRecord {
        std::vector<double> target, dominant;                          // per slot: mean target fraction and dominant-visible share over land
        std::size_t land = 0, white_samples = 0, white_biome = 0, white_season = 0;
        std::size_t interior = 0;                                      // plain interior land samples checked against their biome row
        double interior_max_error = 0;                                 // the largest |composited coverage - table fraction| among them
        double moisture_pair_max_error = 0;                            // the moisture biome's wet + dry total against its row
        std::map<int, std::vector<double>> biome_coverage;             // biome id -> per slot mean composited coverage over its land samples
        std::map<int, std::size_t> biome_land;
    } layers;
    std::size_t lowered = 0, raised = 0, engine_sign_violations = 0, lake_below_datum = 0;
    double max_adjustment = 0, max_roundtrip_error = 0;
    std::size_t seam_pairs = 0, padding_rows = 0;
    int north_pole_intent = -1, south_pole_intent = -1;
    std::map<int, std::size_t> intent_counts, biome_counts;
    Fidelity fidelity;
};

// Refuses (json::Error) a non-square physical domain, a side or extent the import calibration does not allow, a seam
// that does not fall on output columns, unmapped categories (including biome ids missing from the exported
// catalogue), heights beyond the calibrated limit, a coastline sign that would not survive 16-bit quantization and the
// engine's vertex truncation ("coast_sign"), and any seam-pair mismatch. Node borders are shared by construction: the
// encoder cuts every node from the one grid (surface.cpp terrain()); the evidence verifies them on the encoded file.
Compiled compile(const SourceLayers& layers, const projection::Projection& projection, const MaterialPolicy& policy, const Import& import, int side);

// The bytes of heightfield.bin ("MNBLHF01", u32 side, side^2 little-endian binary64 z) and masks.bin ("MNBLMK01", u32
// side, then the biome, intent, landform and padding planes, side^2 bytes each), exactly as --compile-terrain writes them.
std::string heightfield_bytes(const Compiled& compiled);
std::string masks_bytes(const Compiled& compiled);
// weights.bin v2 ("MNBLWT02", u32 side, u32 layer count N, N x (u32 byte length, UTF-8 layer name), then N planes of side^2
// alpha bytes in slot order) and height-q16.bin ("MNBLQ16B", u32 side, binary64 hmin and hmax, side^2 big-endian 16-bit codes),
// exactly as --compile-terrain writes them.
std::string weights_bytes(const Compiled& compiled);
std::string q16_bytes(const Compiled& compiled);
// The terrain-metadata layers array (per slot, the share of canonical land samples where it is the dominant visible layer) and
// permanentWhite block, as terrain.json and the bundle's shaping record carry them.
cjson::Value layers_value(const Compiled& compiled);
cjson::Value permanent_white_value(const Compiled& compiled);

// worldbridge --compile-terrain SPLIT --contract FILE --projection-policy FILE --materials FILE --import FILE --side N --out NEW_DIRECTORY
int compile_terrain_main(int argc, char** argv);

}  // namespace worldbridge::terrain
