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
//   rock weight                max(biome rock, slope rule on the output grid, x periodic)
//   16-bit                     the import calibration's global range and round-half-even rule, big-endian
#pragma once
#include <cstdint>
#include <filesystem>
#include <map>
#include <string>
#include <vector>
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
    std::vector<std::string> template_layers;
    std::string sha256;
};
Import load_import(const fs::path& file);

struct SourceLayers {
    std::int64_t side = 0;
    std::vector<double> height, biome, water_type, river, landform;  // row-major, side * side
    std::vector<source::BiomeEntry> biomes;
    std::vector<std::string> landforms;
};
SourceLayers layers_of(const source::SourceWorld& world);

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
    std::vector<std::uint8_t> biome, intent, landform, rock, padding;   // padding: 1 on rows beyond a pole line
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
// weights.bin ("MNBLWT01", u32 side, side^2 rock_k bytes) and height-q16.bin ("MNBLQ16B", u32 side, binary64 hmin and hmax,
// side^2 big-endian 16-bit codes), exactly as --compile-terrain writes them.
std::string weights_bytes(const Compiled& compiled);
std::string q16_bytes(const Compiled& compiled);

// worldbridge --compile-terrain SPLIT --contract FILE --projection-policy FILE --materials FILE --import FILE --side N --out NEW_DIRECTORY
int compile_terrain_main(int argc, char** argv);

}  // namespace worldbridge::terrain
