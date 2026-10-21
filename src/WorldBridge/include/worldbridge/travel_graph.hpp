// Static travel graph (MMR-28): land components, sea basins, roads, bridge candidates, ports and transitions on the
// compiled terrain (MMR-26), under the MMR-17 War Sails contract (worldgen/calibration/naval-transitions) and the
// playability policy (worldgen/policies/playability.json). Everything here is a static candidate with status
// pendingEngineValidation: MMR-38 builds the faces and runs the emitted host checks; nothing claims engine reachability.
//
//   cells        the compiled grid's cells over one seam period (columns wrap) between the pole lines (no padding):
//                sea (main-diagonal centre below the datum) > lake > river (main-diagonal corner intents) > land,
//                land is mountain when (max - min corner height) / spacing > mountainSlope
//   components   land and water cells joined through shared edges only (MMR-17: corner contact never connects)
//   repairs      mountain cells on producer roads become passes; settlement footprints (with an access pass to the
//                nearest land when the footprint is walled in) and snaps; harbour shifts;
//                all bounded by the policy and recorded with stable UIDs
//   groups       land components joined by bridge candidates (producer roads over water cells)
//   ports        from producer harbour records only: the coast pair of the gate's land component nearest the gate,
//                within maxPortGateCells (the installed War Sails envelope), never shared by two towns
//   reachability the player embarks at a town port and disembarks on any coast of an open basin; a group is
//                player-safe when it can be reached from the main group and can return to it
//   town repairs no generated town is dropped (docs/travel-graph.md "Town repairs"): a town with no touching land or mountain
//                cell snaps within maxTownSnapCells, else a river or lake cell under it is carved to land (townCarve); a town
//                that is not player-safe gets, in order, a mountain pass joining its group to a player-safe one (at most
//                maxAccessCells), a forced port (forcedPorts: a coast port of its own island, only after the join failed, its
//                group holding a kept village for each of its towns and castles), or a move to the nearest land cell of a
//                player-safe group; a town with no cell moves too. They run only for towns that would otherwise reject the
//                world, outside the repair bounds, and are recorded with the town's UID
//   village quota exactly villages.townVillages villages per town and castleVillages per castle (village_quota.cpp, MMR-252): pins, keeps,
//                reassigns, drops ("quota:" exclusions outside the travel bound) and synthesizes villages, clears them of the fortifications
//                (clearance repairs, islet exceptions) and checks itself with a second analysis on the frozen classes; run() applies it
//   road network route pieces (split at poles, cut at crossings without a deck, decks kept), then feeder roads from road-less towns,
//                castles and villages along land travel cells and existing decks, the per-cell road flag and its summary (lay_roads,
//                MMR-253); run() lays it after the village quota
//   land types   every land cell's face TerrainType from the shared biome table (MMR-254, docs/travel-graph.md "Land face types"): the
//                majority of its four corner samples' types (a corner's biome through cells.landFaceGroups, Snow in the permanent-white
//                mask; water biomes do not vote), ties to the main diagonal; the default when no corner votes. Written to cells.bin v2
//                with the road bit, counted per type, and named by the settlement, pass and footprint host checks
#pragma once
#include <cstdint>
#include <filesystem>
#include <map>
#include <optional>
#include <set>
#include <string>
#include <vector>
#include "worldbridge/campaign_compiler.hpp"
#include "worldbridge/projection.hpp"
#include "worldbridge/terrain_compiler.hpp"

namespace worldbridge::travel {
namespace fs = std::filesystem;
using projection::Point;

// worldgen/calibration/naval-transitions/naval-transitions.json: the engine contract MMR-17 observed.
struct Calibration {
    std::map<std::string, std::set<int>> invalid;   // Default, Naval, All -> invalid TerrainType ids
    std::map<std::string, int> face_groups;         // TerrainType name -> id
    int land_to_sea = 0, sea_to_land = 0;
    double embark_threshold = 0;
    double port_gate_envelope_max = 0;              // largest gate-to-port distance of the installed War Sails towns
    std::string evidence_sha256, sha256;
};
Calibration load_calibration(const fs::path& file);

struct Policy {
    int side = 0;
    double mountain_slope = 0;
    int open_sea_cells = 0;
    std::map<std::string, std::string> face_groups;  // land, mountain, coastalSea, openSea, lake, river, bridge, underBridge -> name
    bool road_passes = true, settlement_footprint = true;
    double max_pass_fraction = 0, max_footprint_fraction = 0, max_snap_fraction = 0;
    int max_snap_cells = 0, max_harbour_shift_cells = 0, max_access_cells = 0;
    double max_harbour_shift_fraction = 0, max_port_gate_cells = 0;
    std::set<std::string> bridge_kinds;
    int max_bridges = 0, max_bridge_cells = 0, min_open_cells = 0;
    double max_excluded_village_fraction = 0, max_excluded_castle_fraction = 0;
    // Map epic 2 keys (MMR-248; docs/biome-presentation.md, playability.json notes), consumed by MMR-251..254.
    std::map<std::string, std::string> land_face_groups;   // land biome core -> TerrainType name (MMR-254)
    std::string land_face_default;                          // a land cell whose corners are all water biomes
    int permanent_white_months = 12;                        // permanent white: snow months at or above the threshold -> Snow
    double permanent_white_threshold = 0.5;
    std::string permanent_white_face;
    int max_town_snap_cells = 0;                            // MMR-251 town repairs: towns-only snap radius, carve, forced port
    bool town_carve = false, forced_ports = false;
    struct VillageTier { std::string name; double min_spacing = 0, max_spacing = 0; };   // fractions of the town spacing S
    struct Villages {                                       // MMR-252 quota
        int town = 3, castle = 2, max_extra_harbours = 1;
        double clearance = 4, min_separation = 1;
        std::vector<VillageTier> tiers;
        bool islet_exceptions = true;
        std::vector<std::string> preference, protected_classes;
        std::string reassign, synthesized_class, uid_prefix;
    } villages;
    struct Roads {                                          // MMR-253 road cells and feeders
        bool split_at_poles = true, skip_sea_legs = true, passes_are_road = true, end_at_undecked_bank = true, cell_flag = true;
        std::vector<std::string> feeder_from;
        int feeder_max_cells = 0;
        bool feeder_passes = true;
        std::string feeder_water;
    } roads;
    Calibration calibration;
    std::string sha256;
};
// Strict members; the calibration is resolved relative to the policy file and must match its pinned SHA-256; every
// proposed face group must exist in the calibration and have the validity its class needs.
Policy load_policy(const fs::path& file);
// The shared biome table holds (docs/biome-presentation.md): cells.landFaceGroups names, for every land biome of the material
// policy, the biome's terrainType, and permanentWhite agrees with the material policy's. Refuses "face_table_mismatch".
void check_face_table(const Policy& policy, const terrain::MaterialPolicy& materials);

enum Cls : std::uint8_t { Land = 0, Mountain = 1, Sea = 2, Lake = 3, River = 4 };
const char* class_name(std::uint8_t cls);

// The canonical navigation grid. Samples: rows 0..rows (row 0 the north pole line, row `rows` the south), columns
// 0..cols-1 (column `cols` is column 0 again). Cell (r, c) has corners (r, c), (r, c+1), (r+1, c), (r+1, c+1).
struct Grid {
    int cols = 0, rows = 0;
    double spacing = 0;
    std::vector<double> z;              // (rows + 1) * cols, world units, sea datum 0
    std::vector<std::uint8_t> intent;   // 0 land, 1 ocean, 2 lake, 3 river
    std::vector<std::uint8_t> biome;    // the compiled biome id (MMR-254)
    std::vector<std::uint8_t> white;    // 1 in the permanent-white mask (terrain::Compiled::white)
    double z_at(int r, int c) const { return z[static_cast<std::size_t>(r) * cols + ((c % cols) + cols) % cols]; }
    std::uint8_t intent_at(int r, int c) const { return intent[static_cast<std::size_t>(r) * cols + ((c % cols) + cols) % cols]; }
    std::size_t sample(int r, int c) const { return static_cast<std::size_t>(r) * cols + ((c % cols) + cols) % cols; }
};

struct Cell { int row = -1, col = -1; bool valid() const { return row >= 0; } };
inline bool operator==(const Cell& a, const Cell& b) { return a.row == b.row && a.col == b.col; }
inline bool operator<(const Cell& a, const Cell& b) { return a.row != b.row ? a.row < b.row : a.col < b.col; }

// Index arithmetic, wrapping columns and bounding rows.
class Cells {
public:
    Cells(int cols, int rows, double spacing) : cols_(cols), rows_(rows), spacing_(spacing) {}
    int cols() const { return cols_; }
    int rows() const { return rows_; }
    double spacing() const { return spacing_; }
    std::size_t size() const { return static_cast<std::size_t>(cols_) * rows_; }
    std::size_t index(Cell c) const { return static_cast<std::size_t>(c.row) * cols_ + c.col; }
    Cell at(std::size_t i) const { return {static_cast<int>(i / cols_), static_cast<int>(i % cols_)}; }
    Cell wrap(int row, int col) const { return {row, ((col % cols_) + cols_) % cols_}; }
    std::vector<Cell> neighbours(Cell c) const;                 // shared edges: E, W wrap; N, S bounded
    Point centre(Cell c) const { return {(c.col + 0.5) * spacing_, (c.row + 0.5) * spacing_}; }
    double distance(Cell a, Cell b) const;                      // centre distance in cells, nearest seam image
    double distance(Point p, Cell c) const;                     // logical point to centre, in cells
    std::vector<Cell> touching(Point p) const;                  // cells whose closure holds p, NW..SE order
private:
    int cols_, rows_;
    double spacing_;
};

// kind: town, village, castle; bound_town_id: a village's bound town, or castle after the village quota.
struct Settlement { std::string id, uid, kind, bound_town_id; Point position; };
struct Harbour { std::string village_id, village_uid, town_id; std::int64_t sea_node = 0; Point sea_point; double quality = 0; };
// A producer road: consecutive node positions per piece (a pole passage splits it); crossing_steps are producer-reported
// river crossings as (piece, step) where step joins nodes[step] and nodes[step + 1].
struct Road {
    int index = 0;
    std::string from_town_id, to_town_id, from_uid, to_uid;
    std::vector<std::vector<Point>> pieces;
    std::set<std::pair<int, int>> crossing_steps;
    int pole_crossings = 0;   // producer-reported crossings on a step that touches a pole node (no step to rasterize)
};
struct Input {
    Grid grid;
    std::map<int, std::string> biome_core;   // biome id -> core name (the exported terrain.biomes); the table names land cores
    std::vector<Settlement> settlements;
    std::vector<Harbour> harbours;
    std::vector<Road> roads;
};

// method: direct, footprint, snap, carve and move (town repairs), synthesized (a village of the quota), unplaceable. point: the source point
// clamped into the cell, a quarter cell inside its edges (a quota village's chosen point, or a clearance move's).
struct Placement { std::string id, uid, kind, method; Cell cell; Point point; double shift = 0; int land = -1, group = -1; };
struct Crossing { std::vector<Cell> cells; std::string kind; bool producer = false; int land_a = -1, land_b = -1; bool joins = false; };
struct ProducerCrossing { int piece = 0, step = 0; std::string outcome; };   // outcome: matched, no_compiled_water
// The road network (MMR-253, docs/travel-graph.md "Road network"). An end's kind: gate (a settlement's end; settlement names it, point is its
// placement point), pole (a pole passage cuts the road; the end cell's centre), bank (the road stops at the bank of a crossing without a
// deck; point is the middle of the bank edge, water the first water cell beyond it), junction (a feeder meets an earlier road; road is its
// index; the end cell's centre).
struct RoadEnd { std::string kind, settlement; Cell cell, water; Point point; int road = -1; };
// One drawable run of road cells, edge-connected in order (columns wrap); deck cells of an existing bridge are part of it.
struct RoadPiece { std::vector<Cell> path; RoadEnd start, end; };
struct RoadResult {
    int index = 0;
    std::string kind = "route";                  // route (an FWGC++ route) or feeder (MMR-253)
    std::string settlement;                      // a feeder: the castle, village or road-less town it serves
    std::string from_id, to_id, from_uid, to_uid;
    std::vector<Cell> path;
    std::map<std::string, std::size_t> classes;   // of the path cells before repairs
    std::vector<Cell> passes;
    std::vector<Crossing> crossings;
    double max_grade = 0;
    int pole_passages = 0;
    bool same_group = false;                     // both ends in one land group after repairs and joins
    std::vector<ProducerCrossing> producer_crossings;
    int producer_pole_crossings = 0;
    std::vector<RoadPiece> pieces;               // the road cells, split at poles and (routes) at crossings without a deck
    std::vector<Cell> decks;                     // deck cells of existing bridges on the pieces, in order
};
// The laid network's summary (lay_roads). connected[kind] = {connected, total} over the kept settlements; a settlement is connected when its
// cell is a road cell whose road component (edge-connected road cells) holds a town, or, in a land group without road cells whose kept
// settlements all share one cell, a town's cell (an islet town and its villages: no network to join). bound_not_reached: connected castles and villages whose
// road component does not hold their bound settlement (it lies beyond feeders.maxCells by land; they joined the nearest road or town).
struct Unconnected { std::string id, uid, kind, reason; };
struct RoadNetwork {
    bool laid = false;
    std::size_t cells = 0, route_cells = 0, feeder_only_cells = 0, deck_cells = 0, water_without_deck = 0, feeders = 0, max_feeder_cells = 0;
    std::map<std::string, std::size_t> ends;
    std::map<std::string, std::pair<std::size_t, std::size_t>> connected;
    std::vector<Unconnected> unconnected, bound_not_reached;
};
struct LandComponent { int id = 0; std::size_t cells = 0; int group = -1; std::vector<std::string> towns, villages, castles; };
struct Group { int id = 0; std::vector<int> lands; std::size_t towns = 0; std::set<int> port_basins, coast_basins; bool forward = false, backward = false, safe = false; };
struct Basin { int id = 0; std::size_t cells = 0, sea = 0, lake = 0, river = 0, open_sea = 0; bool open = false; std::vector<std::string> ports; };
// gate_distance: port cell to gate cell; entrance_distance: land entrance to gate cell; harbour_shift: producer sea
// point to the harbour cell (the navigable cell its sea node resolves to).
// kind: empty for a harbour candidate (a producer harbour record), "coast" for a town repair's coast port (no harbour village,
// sea node or harbour cell).
struct PortCandidate {
    std::string village_id, village_uid, status, reason, kind;  // status: valid, invalid
    std::int64_t sea_node = 0;
    Cell harbour_cell, sea_cell, land_cell;
    double harbour_shift = 0, gate_distance = 0, entrance_distance = 0, quality = 0;
    int basin = -1;
};
// forced: the chosen port is a forced port (a town repair, kind "coast"); MMR-252 pins or synthesizes its harbour village.
struct Port { std::string town_id, town_uid; bool valid = false, forced = false; PortCandidate chosen; std::vector<PortCandidate> candidates; };
struct Transition { std::string kind, town_id; int group = -1, basin = -1; Cell land_cell, sea_cell; std::size_t coast_pairs = 0; };  // kind: port, coast
struct Exclusion { std::string id, uid, kind, reason; };
// kind: pass, access, footprint, snap, harbour_shift; the town repairs town_snap (beyond maxSnapCells), carve, join, coast_port
// (a forced port) and move. A town's snap within maxSnapCells keeps the kind snap but, like every town repair, sits outside the bounds.
// clearance: a kept village the village quota moved clear of the fortifications (subject its UID; distance in cells, outside the bounds).
struct Repair { std::string kind, subject, detail; Cell cell, from; double distance = 0; };
struct Reason { std::string code, message; std::vector<std::string> uids; };
struct HostCheck { std::string check, subject, expected; };

struct Result {
    std::string status;                         // ok | rejected
    std::vector<Reason> reasons;
    std::vector<std::uint8_t> cls;              // per cell, after repairs
    std::vector<std::uint8_t> open_sea;         // per cell: 1 for OpenSea
    std::vector<std::uint8_t> road;             // per cell: 1 on a road cell (lay_roads; the cells.bin v2 road bit); empty before
    std::vector<std::uint8_t> land_type;        // per cell: the land face's TerrainType id (calibration face group), 0 on every other class
    std::map<std::string, std::size_t> land_type_counts;   // land cells per TerrainType name
    RoadNetwork network;
    std::vector<int> land_of, basin_of;         // per cell, -1 outside
    std::map<std::string, std::size_t> class_counts, compiled_class_counts;
    std::vector<LandComponent> lands;
    std::vector<Group> groups;
    std::vector<Basin> basins;
    std::vector<Placement> placements;
    std::vector<RoadResult> roads;
    std::vector<Port> ports;
    std::vector<Transition> transitions;
    std::vector<Exclusion> exclusions;
    std::vector<Repair> repairs;
    std::vector<HostCheck> host_checks;
    int main_group = -1;
    std::size_t required_bridges = 0;
    std::vector<std::vector<std::string>> collisions;   // settlement ids sharing one emitted point
    // The engine contract the candidates assume (from the calibration), carried into travel-graph.json.
    int region_land_to_sea = 0, region_sea_to_land = 0;
    double embark_threshold = 0, port_gate_envelope_max = 0, max_port_gate_units = 0;
};

// Cell classes from the grid samples (the rule above); cols * rows, row-major.
std::vector<std::uint8_t> classify(const Grid& grid, const Policy& policy);
// The land face type of every land cell of classes (the rule above; MMR-254), as calibration TerrainType ids; 0 on other classes. Refuses a
// grid without biome or permanent-white samples and a corner biome the input's catalogue does not name.
std::vector<std::uint8_t> land_types(const Input& input, const Policy& policy, const std::vector<std::uint8_t>& classes);
// The whole static analysis: classify, then analyze_cells. Refuses (json::Error) malformed input (unknown ids, grids
// of the wrong size); a world that fails the policy comes back with status "rejected" and quality.* reasons naming
// stable UIDs, never an exception.
Result analyze(const Input& input, const Policy& policy);
// The analysis from given cell classes (grid samples still give each cell's slope for road grades).
Result analyze_cells(const Input& input, const Policy& policy, std::vector<std::uint8_t> classes);
// Port binding (port_compiler.cpp): harbour candidates per town after placement, components and exclusions.
void bind_ports(const Input& input, const Policy& policy, const Cells& cells, Result& result);
// The cells of joining crossings (port_compiler.cpp): the runtime lays a bridge deck over each, and no port's sea point snaps there.
std::set<std::size_t> deck_cells(const Cells& cells, const Result& result);
// A lower bound on the engine's port-to-gate path (port_compiler.cpp), in units: see there.
double port_path_bound(const Cells& cells, const Result& result, const std::set<std::size_t>& decks, Cell sea, int basin, Point gate, double limit);

// The real-world adapter: canonical grid from the compiled terrain, settlements from the campaign records joined to
// their source positions through the projection, harbours from the campaign ports, roads from roads.routes.
Input input_of(const source::SourceWorld& world, const projection::Projection& projection, const terrain::Compiled& terrain, const campaign::Compiled& campaign);

// The settlement host checks (gateFace, settlementFace, villageRoute, distinctPositions) and positionCollisions from the placements of the
// settlements that are not excluded; replaces earlier ones (analyze and the village quota both call it).
void settlement_checks(const Input& input, Result& result, const Policy& policy);
// The town spacing S: the mean periodic distance (world width `width`) from each town point to its nearest other town; 0 below two towns.
double town_spacing(const std::vector<Point>& towns, double width);
// The village quota (village_quota.cpp; docs/travel-graph.md "Village quota"): binds exactly policy.villages.town villages to every town and
// .castle to every castle on an accepted pass-1 result, writing the campaign v2 binding (bound, boundKind, sourceTown, origin, tier,
// pinned, villageQuota), "quota:" exclusions, synthesized villages (records, id-map entries, placements, settlements), clearance repairs,
// forced ports' harbour villages, and the settlement host checks. Refuses "quota_unstable" when its second analysis differs.
void village_quota(campaign::Compiled& campaign, Input& input, Result& result, const Policy& policy, const campaign::ContentPolicy& content);

// The road network (MMR-253, docs/travel-graph.md "Road network") on the final settlements, after the village quota: feeder roads along land
// travel cells (and existing decks, along their route) from every road-less town, castle and village to its bound settlement (castles: the
// garrison town in castle_towns; road-less towns: the nearest road or town of their land group), then a gate spur (a feeder into the land
// road cell beside it) from every settlement on a road cell that no piece leaves over land from its gate, the per-cell road flag and the summary.
// Never adds a water crossing or changes a cell class. Replaces earlier feeders and flags.
void lay_roads(const Input& input, Result& result, const Policy& policy, const std::map<std::string, std::string>& castle_towns);

// City preservation (MMR-251, docs/travel-graph.md "City preservation"): every FWGC++ city is exactly one town at every stage. The source
// sites, settlements.founding.placed_cities, the compiled towns, the id-map's town entries and the travel result's town placements are the
// same count with the same uid set, and every town has a cell. Refuses "city_not_preserved" naming the stage and the uids.
void check_preservation(const source::SourceWorld& world, const campaign::Compiled& campaign, const Result& result);

struct Identity { std::string world_sha256, source_identity, projection_sha256, masks_sha256, heightfield_sha256, id_map_sha256, policy_sha256, calibration_sha256; int side = 0, column_offset = 0, row_offset = 0; };
// cells.bin v2 (MMR-254; capability R11): "MNBLTC02", u32 cols, u32 rows, then four cols*rows byte planes, row-major from the north pole
// line: the class (after repairs), the OpenSea flag, the land type (TerrainType id of a land cell, 0 elsewhere) and the flags (bit 0: a
// road cell, deck cells included; other bits 0). Cell (r, c) sits at physical sample (row_offset + r, column_offset + c).
std::string cells_bytes(const Input& input, const Result& result);
std::string graph_document(const Input& input, const Result& result, const Identity& identity);   // canonical travel-graph.json
std::string ports_document(const Input& input, const Result& result, const Identity& identity);   // canonical ports.json

// The whole pipeline from a split world and its policies, as the CLI and the generation driver run it.
// population: worldgen/policies/population.json (the population pass, MMR-279, after the village quota on an accepted world).
struct Policies { fs::path projection, materials, import, content, cultures, playability, population; };
// campaign: MMR-27 records after the exclusions. compiled: every compiled file of the world by its bundle path under
// compiled/ (terrain/heightfield.bin, masks.bin, weights.bin, height-q16.bin, projection.json; campaign/id-map.json,
// campaign.json; travel/travel-graph.json, ports.json, cells.bin), exactly as the per-stage commands write them.
struct Run {
    Result result; Identity identity; std::string graph, ports, cells, campaign; std::map<std::string, std::string> compiled;
    std::map<std::string, std::size_t> pending_slots;   // pending typed slots of the campaign records, by owner ticket
};
Run run(const source::SourceWorld& world, const fs::path& world_json, const Policies& policies);

// worldbridge --compile-travel SPLIT --contract FILE --projection-policy FILE --materials FILE --import FILE
//   --content FILE --cultures FILE --playability FILE --out NEW_DIRECTORY        (exit 3 when the world is rejected)
int compile_travel_main(int argc, char** argv);

}  // namespace worldbridge::travel
