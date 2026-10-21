// Map epic 2 terrain shaping (MMR-278, worldgen/policies/terrain-shaping.json, docs/terrain-compiler.md "Shaping"; findings
// section 15). The terrain compiler's last stage: it runs after travel placement, so settlement pads use the final positions,
// and it never touches navigation, which keeps its own scaled, unshaped field (the travel compile at playability grid.side).
//
//   coarse (the travel compile, side 257, 6.25 units)   lakes, coast envelope, settlement pads, despike in the policy's
//                                                       coherence order, then the masked low-pass, on the canonical periodic grid
//   fine (side 513, 3.125 units, level 7)               cubic B-spline 2x of the shaped coarse field (canonical columns, so seam
//                                                       pairs match by construction), then the fine fix-ups: pole and padding rows
//                                                       as compiled, sign coherence from the fine masks, pads, lake beds and
//                                                       shores, and every matched lake group shown under the runtime plane rule
//                                                       (GeneratedWaterSystem.Lakes), then the layer weights on the shaped slopes
//                                                       (terrain_layers.hpp, MMR-255)
//
// The fine masks (biome, intent, landform, padding) are the terrain compile at the fine side: its even samples are the coarse
// samples exactly. Every step is deterministic and written for bit parity with tools/terrain_shaping.py: Jacobi sweeps, fixed
// orders, sums in index order, no contraction.
#pragma once
#include <cstdint>
#include <string>
#include <vector>
#include "worldbridge/canonical_json.hpp"
#include "worldbridge/policy_map2.hpp"
#include "worldbridge/terrain_compiler.hpp"

namespace worldbridge::terrain {

// A kept settlement's final position (logical campaign units, travel placement point) and kind (town, castle, village).
struct Pad { std::string id, kind; double x = 0, y = 0; };

// The travel cells (cells.bin classes after repairs, row-major from the north pole line) for the nav/visual coast mismatch.
struct TravelCells { int cols = 0, rows = 0, column_offset = 0, row_offset = 0; std::vector<std::uint8_t> classes; };

// The source layers the lake rule reads (the runtime's GeneratedClimateSystem.SourceGrid / Nearest mapping).
struct LakeSource { std::int64_t side = 0; std::vector<double> water_type, water_surface; double sea_level_m = 0, vertical_scale = 0, central_meridian_deg = 0; };

// One lake group of the physical fine grid under the runtime plane rule, before and after shaping.
struct LakeRecord {
    int index = 0, samples = 0, matched = 0, first_column = 0, first_row = 0;
    bool canonical = false;                          // the group's first sample lies in the canonical columns
    double level_m = 0, level_z = 0, z = 0, bed_max_z = 0, shore_min_z = 0;
    bool clamped = false, shown_before = false, shown_after = false;
    bool bed_below_level = false, shore_above_level = false;   // every member <= level - bedBelow; every 8-adjacent land sample >= level + shoreAbove
};

struct SlopeStats { double p95 = 0, max = 0; std::size_t faces = 0; };
struct ShapingRecord {
    std::string policy_sha256;
    int sweeps = 0;                                  // despike sweeps run
    double residual = 0;                             // the largest change of the last despike sweep
    std::size_t samples_changed = 0;                 // coarse samples the shaping steps changed
    SlopeStats interior_before, interior_after, coast_before, coast_after, crease_before, crease_after;
    std::size_t spikes_before = 0, spikes_after = 0;
    std::size_t pads = 0, pads_merged = 0, pads_over = 0;
    std::size_t lake_groups = 0, lakes_shown_before = 0, lakes_shown_after = 0, lakes_newly_hidden = 0, lakes_unmatched = 0, lake_bed_passes = 0;
    std::size_t nav_coast_mismatch = 0, nav_cells_checked = 0, nav_coast_mismatch_before = 0;   // before: the unshaped fine compile
    std::vector<LakeRecord> lakes;
    std::vector<double> pad_slopes;                  // per pad, the final face slope at the settlement point (degrees), pads in id order
};

struct Shaped {
    Compiled terrain;                                // the fine surface: z, height, masks, climate, layer weights
    std::vector<double> coarse_z;                    // the coarse field after the coarse steps (physical, side 257), before the B-spline
    ShapingRecord record;
};

// Refuses (json::Error) a fine compile that is not the coarse one at 2 x (side - 1) + 1, a coarse side other than the policy's,
// a policy with resolution.fine false, and, like compile(), a coastline sign that would not survive quantization ("coast_sign")
// or a seam-pair mismatch.
Shaped shape(const Compiled& coarse, const Compiled& fine, const projection::Projection& projection, const MaterialPolicy& materials,
             const Import& import, const policy::Shaping& policy, const std::vector<Pad>& pads, const LakeSource& lakes, const TravelCells& cells);

// The shaping block of terrain.json (worldgen/contracts/terrain-metadata.schema.json "shaping"), canonical member order.
cjson::Value shaping_value(const ShapingRecord& record);

// The runtime lake plane rule (src/MNBL.ProceduralWorld/World/GeneratedWaterSystem.cs Lakes) on a physical grid: one record per
// 4-connected lake group in scan order (unmatched groups have matched 0 and are never shown). shown_after holds the verdict.
std::vector<LakeRecord> lake_rule(const std::vector<double>& heights, const std::vector<std::uint8_t>& intent, int side, double spacing, double width,
                                  double origin_x, double origin_y, double height, const LakeSource& source, std::vector<std::vector<std::size_t>>* members = nullptr);

}  // namespace worldbridge::terrain
