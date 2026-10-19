#include "worldbridge/projection.hpp"
#include <cmath>
#include <cstdlib>
#include <set>
#include <string>

namespace worldbridge::projection {
namespace {
using cjson::Value;
using json::Error;
using json::Location;

Location nowhere() { return Location{"", 0, 1, 1, "$"}; }
[[noreturn]] void refuse(const std::string& code, const std::string& message) { throw Error(code, message, nowhere()); }

Value number(double v) { Value out; out.kind = Value::Kind::Number; out.text = cjson::ecmascript_number(v); return out; }
Value str(const std::string& s) { return Value::string(s); }
Value boolean(bool b) { Value out; out.kind = Value::Kind::Bool; out.boolean = b; return out; }
Value list(std::initializer_list<Value> items) { Value out; out.kind = Value::Kind::Array; out.items = items; return out; }

double finite(double v, const char* what) {
    if (!std::isfinite(v)) refuse("nonfinite_number", std::string(what) + " is not finite");
    return v;
}

// Policy members: exactly these, each once (the tokenizer already refuses duplicates).
const Value& member(const Value& doc, const char* key, Value::Kind kind) {
    const Value* v = doc.find(key);
    if (!v) throw Error("missing_field", std::string("Projection policy lacks '") + key + "'", doc.at);
    if (v->kind != kind) throw Error("wrong_type", std::string("Projection policy '") + key + "' has the wrong type", v->at);
    return *v;
}
double real(const Value& doc, const char* key) {
    const Value& v = member(doc, key, Value::Kind::Number);
    const double d = std::strtod(v.text.c_str(), nullptr);
    if (!std::isfinite(d)) throw Error("nonfinite_number", std::string("Projection policy '") + key + "' is not finite", v.at);
    return d;
}
// The first policy member outside its range, or nullptr. Checked at load (with the file location) and again by every
// Projection, so a Policy edited in memory cannot slip past.
const char* policy_problem(const Policy& p) {
    if (!(p.central_meridian_deg >= -180 && p.central_meridian_deg < 180)) return "centralMeridianDeg";
    if (!(p.logical_width > 0 && p.logical_width <= 100000)) return "logicalWidth";
    if (!(p.height_ratio > 0 && p.height_ratio <= 1)) return "heightRatio";
    if (!(p.vertical_scale > 0 && p.vertical_scale <= 1)) return "verticalScale";
    // R10: the strips must hold the image of every shortest seam-crossing route, so at least half the width.
    if (!(p.strip_ratio >= 0.5 && p.strip_ratio <= 1)) return "stripRatio";
    if (!(p.pole_margin_ratio >= 0 && p.pole_margin_ratio <= 1)) return "poleMarginRatio";
    return nullptr;
}

// The policy hash covers exactly the members that change the mapping (notes are descriptive).
std::string policy_sha256(const Policy& p) {
    Value v = Value::object();
    v.set("schema", str("mnbl.projection-policy"));
    v.set("version", Value::integer(1));
    v.set("projection", str(p.type));
    v.set("projectionVersion", Value::integer(static_cast<std::uint64_t>(p.type_version)));
    v.set("centralMeridianDeg", number(p.central_meridian_deg));
    v.set("logicalWidth", number(p.logical_width));
    v.set("heightRatio", number(p.height_ratio));
    v.set("verticalScale", number(p.vertical_scale));
    v.set("stripRatio", number(p.strip_ratio));
    v.set("poleMarginRatio", number(p.pole_margin_ratio));
    Value calibration = Value::object();
    calibration.set("file", str(p.calibration_file));
    calibration.set("sha256", str(p.calibration_sha256));
    v.set("calibration", calibration);
    return cjson::sha256_hex(cjson::canonical(v));
}
}  // namespace

Policy load_policy(const fs::path& file) {
    const Value doc = cjson::parse(file);
    if (doc.kind != Value::Kind::Object) throw Error("wrong_type", "Projection policy must be an object", doc.at);
    static const std::set<std::string> known{"schema", "version", "projection", "projectionVersion", "centralMeridianDeg", "logicalWidth", "heightRatio",
                                             "verticalScale", "stripRatio", "poleMarginRatio", "calibration", "notes"};
    for (const auto& m : doc.members)
        if (!known.count(m.first)) throw Error("unknown_field", "Projection policy has unknown member '" + m.first + "'", m.second.at);
    if (doc.find("notes") && doc.find("notes")->kind != Value::Kind::String) throw Error("wrong_type", "Projection policy 'notes' must be a string", doc.find("notes")->at);
    if (member(doc, "schema", Value::Kind::String).text != "mnbl.projection-policy") throw Error("unsupported_version", "Projection policy schema is not mnbl.projection-policy", doc.find("schema")->at);
    if (member(doc, "version", Value::Kind::Number).text != "1") throw Error("unsupported_version", "Projection policy version must be 1", doc.find("version")->at);
    Policy p;
    p.type = member(doc, "projection", Value::Kind::String).text;
    if (p.type != "periodic-equirectangular") throw Error("unsupported_version", "Unknown projection '" + p.type + "'", doc.find("projection")->at);
    if (member(doc, "projectionVersion", Value::Kind::Number).text != "1") throw Error("unsupported_version", "projectionVersion must be 1", doc.find("projectionVersion")->at);
    p.type_version = 1;
    p.central_meridian_deg = real(doc, "centralMeridianDeg");
    p.logical_width = real(doc, "logicalWidth");
    p.height_ratio = real(doc, "heightRatio");
    p.vertical_scale = real(doc, "verticalScale");
    p.strip_ratio = real(doc, "stripRatio");
    p.pole_margin_ratio = real(doc, "poleMarginRatio");
    if (const char* key = policy_problem(p))
        throw Error("out_of_range", std::string("Projection policy '") + key + "' is outside its range", doc.find(key)->at);
    const Value& calibration = member(doc, "calibration", Value::Kind::Object);
    for (const auto& m : calibration.members)
        if (m.first != "file" && m.first != "sha256") throw Error("unknown_field", "Projection policy calibration has unknown member '" + m.first + "'", m.second.at);
    p.calibration_file = member(calibration, "file", Value::Kind::String).text;
    p.calibration_sha256 = member(calibration, "sha256", Value::Kind::String).text;
    if (p.calibration_sha256.size() != 64 || p.calibration_sha256.find_first_not_of("0123456789abcdef") != std::string::npos)
        throw Error("invalid_hash", "Projection policy calibration.sha256 is not a lowercase SHA-256", calibration.find("sha256")->at);
    // The calibration receipt is resolved next to the policy and must still be the pinned bytes.
    const fs::path receipt = file.parent_path() / fs::path(std::u8string(p.calibration_file.begin(), p.calibration_file.end()));
    if (!fs::is_regular_file(receipt)) throw Error("missing_file", "Calibration receipt not found: " + json::utf8(receipt), calibration.find("file")->at);
    if (cjson::sha256_file_hex(receipt) != p.calibration_sha256)
        throw Error("calibration_mismatch", "Calibration receipt " + json::utf8(receipt) + " does not match the pinned SHA-256", calibration.find("sha256")->at);
    p.sha256 = policy_sha256(p);
    return p;
}

Projection::Projection(const Policy& policy, const SourceFrame& frame) : policy_(policy), frame_(frame), nodes(frame.side) {
    if (policy.type != "periodic-equirectangular" || policy.type_version != 1) refuse("unsupported_version", "Projection policy is not periodic-equirectangular v1");
    if (const char* key = policy_problem(policy)) refuse("out_of_range", std::string("Projection policy '") + key + "' is outside its range");
    policy_.sha256 = policy_sha256(policy_);  // never trust a hash carried in from elsewhere
    if (!(std::isfinite(frame.radius_m) && frame.radius_m > 0)) refuse("out_of_range", "Source radius_m must be a positive finite number of metres");
    finite(frame.sea_level_m, "Source sea level");
    w = policy.logical_width;
    h = policy.logical_width * policy.height_ratio;
    text = cjson::canonical(build_document());
    sha = cjson::sha256_hex(text);
}

double Projection::canonical_x(double x) const {
    finite(x, "x");
    double c = std::fmod(x, w);
    if (c < 0) c += w;
    if (c >= w) c -= w;  // fmod of a value just below a multiple of w can round up to w
    return c;
}

double Projection::wrap_delta(double dx) const {
    finite(dx, "dx");
    return dx - w * std::round(dx / w);
}

Point Projection::forward(double latitude_deg, double longitude_deg) const {
    finite(latitude_deg, "latitude"); finite(longitude_deg, "longitude");
    if (latitude_deg < -90 || latitude_deg > 90 || longitude_deg < -180 || longitude_deg > 180)
        refuse("out_of_coverage", "(" + cjson::ecmascript_number(latitude_deg) + ", " + cjson::ecmascript_number(longitude_deg) + ") is outside latitude [-90, 90], longitude [-180, 180]");
    if (latitude_deg == 90 || latitude_deg == -90) refuse("singular_point", "A pole has no longitude; place it with forward_pole or refuse it");
    const double u = (longitude_deg - policy_.central_meridian_deg) / 360.0 + 0.5;
    return {canonical_x(w * (u - std::floor(u))), h * (90.0 - latitude_deg) / 180.0};
}

Point Projection::forward_pole(Pole pole, double x) const {
    if (pole == Pole::None) refuse("invalid_argument", "forward_pole needs a pole");
    return {canonical_x(x), pole == Pole::North ? 0.0 : h};
}

Geo Projection::inverse(Point p) const {
    finite(p.x, "x"); finite(p.y, "y");
    if (p.y < 0 || p.y > h) refuse("out_of_coverage", "y = " + cjson::ecmascript_number(p.y) + " is outside [0, " + cjson::ecmascript_number(h) + "]");
    double longitude = policy_.central_meridian_deg - 180.0 + 360.0 * canonical_x(p.x) / w;
    if (longitude >= 180) longitude -= 360;
    if (longitude < -180) longitude += 360;
    Geo g{90.0 - 180.0 * p.y / h, longitude, p.y == 0 ? Pole::North : p.y == h ? Pole::South : Pole::None};
    return g;
}

double Projection::sample_latitude(std::int64_t row) const {
    nodes.node_of(0, row);
    return 90.0 - 180.0 * static_cast<double>(row) / static_cast<double>(nodes.side() - 1);
}

double Projection::sample_longitude(std::int64_t column) const {
    nodes.node_of(column, 0);
    return -180.0 + 360.0 * static_cast<double>(column) / static_cast<double>(nodes.side() - 1);
}

Point Projection::sample(std::int64_t column, std::int64_t row) const {
    nodes.node_of(column, row);  // bounds
    const double n1 = static_cast<double>(nodes.side() - 1);
    // Reduce first, so the duplicate column n-1 is bit-identical to column 0 for every central meridian.
    const double u = static_cast<double>(column % (nodes.side() - 1)) / n1 - policy_.central_meridian_deg / 360.0;
    return {canonical_x(w * (u - std::floor(u))), h * static_cast<double>(row) / n1};
}

Point Projection::node(std::int64_t id) const {
    if (nodes.pole_of(id) != Pole::None) refuse("singular_point", "Node " + std::to_string(id) + " is a pole: it has no position on the map");
    const auto s = nodes.sample_of(id);
    return sample(s.column, s.row);
}

double Projection::z(double radial_height_m) const {
    return (finite(radial_height_m, "height") - frame_.sea_level_m) * policy_.vertical_scale;
}

bool Projection::below_datum(double radial_height_m) const {
    return finite(radial_height_m, "height") - frame_.sea_level_m < 0;
}

double Projection::east_west_factor(double latitude_deg) const {
    finite(latitude_deg, "latitude");
    if (!(latitude_deg > -90 && latitude_deg < 90)) refuse("singular_point", "East-west scale is unbounded at a pole");
    return 1.0 / std::cos(latitude_deg * kPi / 180.0);
}

Path Projection::path(const std::vector<std::int64_t>& ids) const {
    if (ids.size() < 2) refuse("invalid_record", "A path needs at least two nodes");
    for (std::size_t i = 0; i + 1 < ids.size(); ++i)
        if (!nodes.adjacent(ids[i], ids[i + 1]))
            refuse("non_adjacent_step", "Path step " + std::to_string(ids[i]) + " -> " + std::to_string(ids[i + 1]) + " is not a graph edge");
    Path out;
    out.pieces.emplace_back();
    for (std::size_t i = 0; i < ids.size(); ++i) {
        const Pole pole = nodes.pole_of(ids[i]);
        auto& piece = out.pieces.back();
        if (pole != Pole::None) {
            // The pole is a line on the map: end this piece at the incoming longitude, start the next at the outgoing.
            // Adjacency already guarantees the neighbours are ring cells, not the other pole.
            const bool has_prev = !piece.empty(), has_next = i + 1 < ids.size();
            if (has_prev) piece.push_back(forward_pole(pole, piece.back().x));
            if (has_prev && has_next) { out.pieces.emplace_back(); out.links.push_back("pole"); }
            if (has_next) out.pieces.back().push_back(forward_pole(pole, node(ids[i + 1]).x));
            continue;
        }
        const Point p = node(ids[i]);
        if (piece.empty()) { piece.push_back(p); continue; }
        const Point last = piece.back();
        const double image = last.x + wrap_delta(p.x - last.x);   // the step's nearest image of p
        if (image >= 0 && image < w) { piece.push_back(p); continue; }
        if (image == w) {  // lands exactly on the seam: end here, continue from x = 0
            piece.push_back({w, p.y});
            out.pieces.emplace_back(); out.links.push_back("seam");
            out.pieces.back().push_back(p);
            continue;
        }
        const double boundary = image > last.x ? w : 0.0;
        const double t = (boundary - last.x) / (image - last.x);
        const double y = last.y + t * (p.y - last.y);
        if (t > 0) piece.push_back({boundary, y});   // t == 0: the last point already is the crossing
        if (piece.size() >= 2) { out.pieces.emplace_back(); out.links.push_back("seam"); }
        else if (out.pieces.size() > 1 && out.links.back() == "seam") {
            // The previous step ended exactly on the seam and this one turns back: nothing crossed, so rejoin.
            out.pieces.pop_back(); out.links.pop_back();
            out.pieces.back().push_back(p);
            continue;
        }
        else piece.clear();                           // the path starts on the seam: begin on the other edge
        out.pieces.back().push_back({boundary == w ? 0.0 : w, y});
        out.pieces.back().push_back(p);
    }
    // A path that ends exactly on the seam leaves a one-point piece on the far edge: nothing to draw.
    if (out.pieces.back().size() < 2 && out.pieces.size() > 1) { out.pieces.pop_back(); out.links.pop_back(); }
    for (const auto& piece : out.pieces)
        if (piece.size() < 2) refuse("singular_point", "Path has no drawable piece (it is only a pole)");
    return out;
}

Value Projection::build_document() const {
    const double n1 = static_cast<double>(nodes.side() - 1);
    Value doc = Value::object();
    doc.set("schema", str("mnbl.projection"));
    doc.set("version", Value::integer(1));
    doc.set("policySha256", str(policy_.sha256));
    Value type = Value::object();
    type.set("name", str(policy_.type)); type.set("version", Value::integer(1));
    doc.set("projection", type);

    Value source = Value::object();
    source.set("layoutSeed", str(frame_.layout_seed));
    source.set("gridSide", Value::integer(static_cast<std::uint64_t>(nodes.side())));
    source.set("nodeCount", Value::integer(static_cast<std::uint64_t>(nodes.node_count())));
    source.set("radiusM", number(frame_.radius_m));
    source.set("seaLevelM", number(frame_.sea_level_m));
    source.set("longitudeDeg", list({number(-180), number(180)}));
    source.set("latitudeDeg", list({number(90), number(-90)}));
    source.set("coverage", str("full globe: every latitude and longitude; column n-1 duplicates column 0 and each pole row is one node"));
    source.set("samples", str("longitude = -180 + 360 * column / (n - 1), latitude = 90 - 180 * row / (n - 1); row 0 north"));
    doc.set("source", source);

    doc.set("centralMeridianDeg", number(policy_.central_meridian_deg));
    Value seam = Value::object();
    seam.set("longitudeDeg", number(policy_.central_meridian_deg >= 0 ? policy_.central_meridian_deg - 180 : policy_.central_meridian_deg + 180));
    seam.set("x", str("0 = W"));
    seam.set("wrapIntervalDeg", number(360));
    seam.set("wrapIntervalWorldUnits", number(w));
    doc.set("seam", seam);

    Value target = Value::object();
    target.set("units", str("campaign world units"));
    target.set("logicalWidth", number(w));
    target.set("logicalHeight", number(h));
    target.set("x", str("[0, W) canonical, periodic with period W"));
    target.set("y", str("[0, H]; 0 is the north pole line, H the south pole line"));
    doc.set("target", target);

    Value axes = Value::object();
    axes.set("x", str("east (source columns, MMR-16 +X)"));
    axes.set("y", str("south (source rows, MMR-16 +Y)"));
    axes.set("z", str("up"));
    axes.set("origin", str("(0, 0) = the north pole line at the seam"));
    axes.set("handedness", str("(east, south, up) reflects the geographic (east, north, up) frame: a vector's north component maps to -y"));
    doc.set("axes", axes);

    Value forward = Value::object();
    forward.set("x", str("W * frac((longitude - centralMeridian) / 360 + 1/2)"));
    forward.set("y", str("H * (90 - latitude) / 180"));
    forward.set("z", str("(radialHeightM - seaLevelM) * verticalScale"));
    forward.set("sourceSample", str("x = W * frac(column / (n - 1) - centralMeridian / 360), y = H * row / (n - 1)"));
    forward.set("refused", str("non-finite values, latitude outside [-90, 90], longitude outside [-180, 180], and exact poles (singular: no longitude)"));
    doc.set("forward", forward);
    Value inverse = Value::object();
    inverse.set("longitudeDeg", str("centralMeridian - 180 + 360 * canonical(x) / W, normalized to [-180, 180)"));
    inverse.set("latitudeDeg", str("90 - 180 * y / H"));
    inverse.set("poles", str("y = 0 and y = H return a pole; their longitude is not meaningful"));
    inverse.set("sourceGrid", str("column = (n - 1) * (canonical(x) / W + centralMeridian / 360) mod (n - 1), row = (n - 1) * y / H"));
    inverse.set("refused", str("non-finite values and y outside [0, H]"));
    doc.set("inverse", inverse);

    Value horizontal = Value::object();
    horizontal.set("equatorWorldUnitsPerMetre", number(horizontal_scale()));
    horizontal.set("meridianWorldUnitsPerMetre", number(meridional_scale()));
    horizontal.set("sourceCellWorldUnits", list({number(w / n1), number(h / n1)}));
    // Formula only: transcendental results could differ in the last bit between C runtimes and change the identity.
    horizontal.set("eastWestDistortion", str("map / sphere east-west scale = 1/cos(latitude): 1.155 at 30, 2 at 60, 5.759 at 80 degrees; unbounded on the pole lines"));
    doc.set("horizontal", horizontal);

    Value vertical = Value::object();
    vertical.set("datum", str("effective_config.sea_level: scene z = 0"));
    vertical.set("datumM", number(frame_.sea_level_m));
    vertical.set("worldUnitsPerMetre", number(policy_.vertical_scale));
    vertical.set("exaggeration", number(policy_.vertical_scale / horizontal_scale()));
    vertical.set("belowDatum", str("a sample is below the datum when radialHeightM - seaLevelM < 0 (a per-sample sign test)"));
    vertical.set("water", str("the source water_type layer is carried unchanged (0 land, 1 ocean, 2 lake in the pinned producer); a below-datum lake stays a lake. Land/sea face classification is MMR-28's: MMR-17 classified each face by its main-diagonal centre height"));
    doc.set("vertical", vertical);

    Value layout = Value::object();
    layout.set("source", str("R10 (MMR-63, run 12348)"));
    layout.set("physicalX", str("x + strip"));
    layout.set("strip", number(strip()));
    layout.set("physicalWidth", number(w + 2 * strip()));
    layout.set("canonicalPhysicalX", list({number(strip()), number(strip() + w)}));
    layout.set("strips", str("each strip copies the other side of the seam; campaign objects are canonical, strip content is display only"));
    layout.set("physicalY", str("y + poleMargin"));
    layout.set("poleMargin", number(pole_margin()));
    layout.set("physicalHeight", number(h + 2 * pole_margin()));
    layout.set("beyondPoles", str("rows beyond each pole line repeat the pole row; north and south are never connected"));
    doc.set("periodicLayout", layout);

    Value paths = Value::object();
    paths.set("seam", str("split: a step that crosses the seam ends its piece on x = W (or 0) and the next piece starts on x = 0 (or W) at the same y"));
    paths.set("pole", str("split: a path through a pole ends its piece on the pole line at the incoming x and continues from the pole line at the outgoing x"));
    paths.set("steps", str("graph edges only; no drawn step is longer than W/2"));
    doc.set("paths", paths);

    Value sea = Value::object();
    sea.set("seam", str("column n-1 equals column 0 in every layer, so height, water class and below-datum flags agree across x = 0 = W"));
    sea.set("strips", str("the physical strips copy the other side of the seam, so a sea crossing the seam is one connected water body"));
    sea.set("poles", str("each pole row is one value; the padding beyond a pole line repeats it, so polar sea or land is neither cut nor connected north-south"));
    doc.set("seaContinuity", sea);
    doc.set("singular", str("objects on a pole node are refused; terrain pole rows are lines of one value"));
    Value calibration = Value::object();
    calibration.set("file", str(policy_.calibration_file));
    calibration.set("sha256", str(policy_.calibration_sha256));
    doc.set("calibration", calibration);
    doc.set("tradeoffs", str("true scale along the equator and meridians; east-west stretch grows toward the poles (1/cos latitude); a pole is a line"));
    doc.set("duplicateSeam", boolean(true));
    return doc;
}

}  // namespace worldbridge::projection
