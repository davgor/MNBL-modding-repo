// The campaign world projection (MMR-25): one versioned object maps every FWGC++ source position -- terrain samples,
// settlements, hamlets, castles, key locations, ports, roads and rivers -- to campaign world units.
//
// periodic-equirectangular, version 1 (worldgen/policies/projection.json):
//   x = W * frac((longitude - centralMeridian) / 360 + 1/2)   logical, canonical in [0, W); wraps every W
//   y = H * (90 - latitude) / 180                              0 at the north pole, H at the south pole
//   z = (radialHeight - seaLevel) * verticalScale              0 at the sea datum
// Axes follow the accepted MMR-16 calibration: source columns run +X, source rows run +Y, origin (0, 0), so +X is
// east and +Y is south. The frame (east, south, up) is a reflection of the geographic (east, north, up) frame: a
// vector's north component maps to -Y. With heightRatio 1/2 the scale is true along the equator and every meridian,
// and east-west distance is stretched by 1/cos(latitude).
//
// The east/west edges are one seam (R10, MMR-63): canonical positions are logical; the physical terrain carries
// strips of the other side, physical x = x + strip. A pole is a single node; on the map it is the line y = 0 or
// y = H, so a point exactly at a pole has no x and is refused (singular_point). Rows beyond the poles are padding
// and north and south are never connected.
#pragma once
#include <cstdint>
#include <filesystem>
#include <string>
#include <vector>
#include "worldbridge/canonical_json.hpp"
#include "worldbridge/source_nodes.hpp"

namespace worldbridge::projection {
namespace fs = std::filesystem;
using source::Pole;

struct Policy {
    std::string type;          // "periodic-equirectangular"
    int type_version = 0;      // 1
    double central_meridian_deg = 0, logical_width = 0, height_ratio = 0, vertical_scale = 0, strip_ratio = 0, pole_margin_ratio = 0;
    std::string calibration_file, calibration_sha256;
    std::string sha256;        // of the mapping members (Projection recomputes it; notes are not hashed)
};
// Strict: exactly the documented members; refuses an unknown type or version and out-of-range values. The
// calibration receipt is resolved relative to the policy file and must match its pinned SHA-256.
Policy load_policy(const fs::path& file);

// The physical frame of one generated world, from its split export (never the design-space config).
struct SourceFrame {
    std::int64_t side = 0;     // n of the n-by-n layers
    double radius_m = 0, sea_level_m = 0;
    std::string layout_seed;
};

struct Point { double x = 0, y = 0; };   // logical campaign world units
struct Geo { double latitude = 0, longitude = 0; Pole pole = Pole::None; };

// A source path in campaign coordinates. Each piece is a polyline whose steps never exceed half the world width;
// links[i] joins pieces[i] and pieces[i+1]: "seam" (the path continues across the east/west seam; the pieces end
// and start on x = W and x = 0 at the same y) or "pole" (the path passes through a pole: one piece ends on the pole
// line at its own x and the next starts on it at its own x).
struct Path {
    std::vector<std::vector<Point>> pieces;
    std::vector<std::string> links;
};

class Projection {
public:
    Projection(const Policy& policy, const SourceFrame& frame);

    double width() const { return w; }
    double height() const { return h; }
    double strip() const { return w * policy_.strip_ratio; }
    double pole_margin() const { return w * policy_.pole_margin_ratio; }
    const source::NodeGrid& grid() const { return nodes; }

    Point forward(double latitude_deg, double longitude_deg) const;   // refuses poles, non-finite and out-of-coverage
    Point forward_pole(Pole pole, double x) const;                     // a point on a pole line
    Geo inverse(Point p) const;                                        // x wraps; refuses y outside [0, H]
    double canonical_x(double x) const;                                // into [0, W)
    double wrap_delta(double dx) const;                                // nearest-image difference, in [-W/2, W/2]

    Point sample(std::int64_t column, std::int64_t row) const;        // grid sample; column n-1 lands on column 0
    Point node(std::int64_t node) const;                               // refuses the pole nodes (singular_point)
    double sample_latitude(std::int64_t row) const;
    double sample_longitude(std::int64_t column) const;

    double z(double radial_height_m) const;
    bool below_datum(double radial_height_m) const;                   // radial height below the sea datum
    double east_west_factor(double latitude_deg) const;               // map / sphere east-west scale; refuses poles
    double horizontal_scale() const { return w / (2 * kPi * frame_.radius_m); }   // world units per metre at the equator
    double meridional_scale() const { return h / (kPi * frame_.radius_m); }       // world units per metre along meridians
    Point physical(Point p) const { return {p.x + strip(), p.y + pole_margin()}; }

    Path path(const std::vector<std::int64_t>& nodes) const;          // refuses non-adjacent steps and pole-only paths

    const std::string& document() const { return text; }              // canonical projection.json
    const std::string& identity() const { return sha; }               // SHA-256 of document()

    static constexpr double kPi = 3.141592653589793;

private:
    Policy policy_;
    SourceFrame frame_;
    source::NodeGrid nodes;
    double w = 0, h = 0;
    std::string text, sha;
    cjson::Value build_document() const;
};

}  // namespace worldbridge::projection
