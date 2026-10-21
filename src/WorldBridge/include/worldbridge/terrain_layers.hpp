// Map epic 2 biome terrain layers and map trees (MMR-255; findings section 4 and the MMR-249 probe, section 13; docs/terrain-compiler.md
// "Layers"). The layer compile runs on any output grid: compile() calls it on the travel compile and the fine compile, and shape()
// calls it again on the shaped surface, so slope rock follows the shaped slopes.
//
// Per sample, target fractions over terrain-materials.json layerSet (summing to 1), on canonical periodic columns (a neighbour column
// past an edge wraps by the seam period, rows clamp), so seam pairs get identical planes:
//   1. the sample's biome row (the moisture biome's wet/dry pair split by moisture: the row's wet share at rules.moisture.split, 1 at
//      split + band, 0 at split - band, the pair's total kept);
//   2. kernel smoothing: the tent-weighted mean over (2 kernelSamples + 1)^2 neighbours of the same class (land: land and river
//      intents; water: ocean and lake), weights (k + 1 - |dr|)(k + 1 - |dc|);
//   then, on land only, in this order:
//   3. temperature below rules.temperature.belowC moves the wet and dry layers into the cold layer;
//   4. the mountain landform mixes in mountainShare of the mountain layer;
//   5. slope: the slopeRock share s of the slope layer (the desert slope layer on a Desert-face biome), the rest scaled by 1 - s;
//   6. coast: within coast.samples of an ocean sample, all coast layer, except a share max(rocky_coast, s) of the rocky layer;
//   7. water edge: a river sample, or within waterEdge.samples of a waterEdge intent, is all water-edge layer;
//   8. ice_cap at or above iceCap.threshold is all ice-cap layer;
//   9. permanent white (its biomes, or snowMonths months of snow at or above snowThreshold) is all permanentWhite.layer.
// Alphas: the over-compositing inverse (MMR-249: alpha-over from the highest slot down, weight linear in the byte):
//   a_0 = 1, a_k = f_k / (f_0 + ... + f_k), byte = round-half-even(a_k * 255), bytes below alpha.minByte dropped.
// Every step is plain double arithmetic in a fixed order (no hypot, no contraction) for bit parity with tools/terrain_layers.py.
#pragma once
#include <cstdint>
#include <map>
#include <string>
#include <vector>
#include "worldbridge/material_compiler.hpp"
#include "worldbridge/terrain_compiler.hpp"

namespace worldbridge::terrain {

// Fills t.layer_names, t.weights, t.flora_share and t.layers from t.z, the masks and the climate planes. Refuses (json::Error) climate
// planes of the wrong size ("broken_join") and a neighbourhood wider than the seam strip ("seam_not_on_grid").
void compile_layers(Compiled& t, const MaterialPolicy& policy);

// The dominant visible layer of sample i: alpha-over from the highest slot, the largest a_k * prod_{j>k}(1 - a_j), ties to the higher
// slot, the base only when it covers strictly more (tools/scenebin/terrain_bin.py dominant_layers; the encoder's PHYM rule).
int dominant_layer(const std::vector<std::vector<std::uint8_t>>& weights, std::size_t i);

// Seam pairs: every plane equal at columns c and c + period. Returns the pairs checked; refuses "seam_mismatch".
std::size_t check_layer_seams(const Compiled& t);

// Map trees (MMR-249 r1 go; terrain-materials.json flora). Canonical fine samples of a flora biome get Poisson-like counts from
// density x cell area x the flora layer's target fraction, on land (not river) samples clear of the sea and lakes by flora.clearance
// coastSamples, of slopes, settlement pads and road cells; each
// tree is written at every periodic image inside the domain, at the quantized surface height (main-diagonal triangles).
struct FloraInput {
    struct Pad { double x = 0, y = 0, radius = 0; };   // logical position and the clear radius
    std::vector<Pad> pads;
    std::vector<std::uint8_t> road;                     // travel cells (logical, cols x rows from the north pole line), 1 on a road cell
    int road_cols = 0, road_rows = 0;
    double road_spacing = 0;
    double strip = 0, pole_margin = 0, width = 0, height = 0;   // the projection's periodic layout
    std::uint64_t seed = 0;
};
struct FloraRecord {
    std::size_t canonical = 0, trees = 0;                // canonical trees; trees written (all images)
    std::size_t cleared_pads = 0, cleared_roads = 0;     // canonical candidates dropped
    double density_factor = 1;                           // < 1 when maxTrees thinned the density
    std::map<std::string, std::size_t> kinds;            // kind -> trees written
    std::map<std::string, std::size_t> biomes;           // biome core -> canonical trees
};
// FLR2 flora.bin: u32 payload bytes, u32 count, then per tree u32 name length, the name, u32 flora variation, 16 f32 (rotation x scale
// rows, then x y z 1) and 4 f32 colour (MMR-249, decoded from the installed Main_map). Refuses more than flora.maxTrees ("out_of_range").
std::string flora_bytes(const Compiled& t, const MaterialPolicy& policy, const FloraInput& input, FloraRecord& record);

}  // namespace worldbridge::terrain
