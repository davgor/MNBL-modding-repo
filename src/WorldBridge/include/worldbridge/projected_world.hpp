// Projected world export (MMR-25): every terrain sample and exported object position of one split world, through one
// Projection, with that projection's identity on every output.
//
//   projection.json  the resolved projection document (its SHA-256 is the identity)
//   terrain.bin      little-endian: "MNBLPT01", u32 side, 64 ASCII hex identity, side*side binary64 z,
//                    side*side below-datum bytes, side*side water_type bytes
//   terrain.json     how terrain.bin is laid out, its hash, ranges and the identity
//   objects.json     settlements, hamlets (with ports), castles, key locations, roads and rivers, with the identity
//   manifest.json    every file's SHA-256 and the identity
#pragma once
#include <cstdint>
#include <string>
#include <vector>
#include "worldbridge/projection.hpp"
#include "worldbridge/source_world.hpp"

namespace worldbridge::projection {

// The physical frame of a split world. Refuses a world without topology.radius_m or effective_config.sea_level, a
// radius that disagrees with effective_config.globe_radius, or topology ranges other than the full globe.
SourceFrame frame_of(const source::SourceWorld& world);

struct Export {
    std::string identity;
    std::int64_t side = 0;
    std::vector<double> z;              // row-major, side * side
    std::vector<std::uint8_t> below;    // 1 where the sample is below the sea datum
    std::vector<std::uint8_t> water;    // the source water_type code, unchanged
    std::string terrain_bin, terrain_json, objects_json;
};

// Projects everything; refuses (json::Error) a record on a pole node, a record whose node disagrees with its sample,
// and a road or river step that is not a graph edge.
Export export_world(const source::SourceWorld& world, const Projection& projection);

// worldbridge --project SPLIT --contract FILE --policy FILE --out NEW_DIRECTORY
int project_main(int argc, char** argv);

}  // namespace worldbridge::projection
