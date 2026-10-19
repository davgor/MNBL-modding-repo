// Port binding (MMR-28). A port comes only from a producer harbour record of a kept harbour village (MMR-27 ports),
// never from a shore cell, and it serves its town's gate as the installed War Sails towns do.
//   harbour cell   the first navigable cell touching the producer's sea node (NW..SE), else the nearest navigable cell
//                  within maxHarbourShiftCells (a recorded harbour_shift, from the sea-node cell); none ->
//                  sea_node_not_navigable. Its basin must be open -> else closed_basin.
//   port pair      a land cell of the gate's own land component sharing an edge with a cell of that basin: the pair
//                  whose sea cell is nearest the gate, then whose land cell is nearest the gate, then nearest the
//                  harbour cell, then cell order. Pairs another town already holds are skipped (towns in id order), and so
//                  are pairs whose sea cell is a deck cell (deck_cells). No pair -> basin_not_reachable_from_gate; only
//                  held pairs -> port_pair_taken; only held and deck pairs -> port_pair_under_bridge; a port cell farther
//                  than maxPortGateCells from the gate cell -> port_too_far_from_gate. The first pair in that order within
//                  maxPortGateCells whose path bound (port_path_bound) is at most maxPortGateCells x spacing is the
//                  candidate's; none -> port_path_too_long (with the first pair's bound).
// The town's port is its best valid candidate: nearest the gate, then highest harbour quality, then village UID.
#include <algorithm>
#include <cmath>
#include <limits>
#include <string>
#include "worldbridge/travel_graph.hpp"

namespace worldbridge::travel {
namespace {
bool navigable(std::uint8_t cls) { return cls == Sea || cls == Lake || cls == River; }
std::string cells_text(double d) { std::string t = std::to_string(d); return t.substr(0, t.find('.') + 3) + " cells"; }
std::string units_text(double d) { std::string t = std::to_string(d); return t.substr(0, t.find('.') + 4) + " units"; }
// Whether the segment a-b enters the open square [x0, x0 + s] x [y0, y0 + s]: Liang-Barsky against the square shrunk by a hair,
// so a segment along an edge or through a corner does not.
bool enters(Point a, Point b, double x0, double y0, double s) {
    const double e = s * 1e-7, dx = b.x - a.x, dy = b.y - a.y;
    const double p[4] = {-dx, dx, -dy, dy}, q[4] = {a.x - (x0 + e), (x0 + s - e) - a.x, a.y - (y0 + e), (y0 + s - e) - a.y};
    double t0 = 0, t1 = 1;
    for (int i = 0; i < 4; ++i) {
        if (p[i] == 0) { if (q[i] <= 0) return false; continue; }
        const double t = q[i] / p[i];
        if (p[i] < 0) t0 = std::max(t0, t); else t1 = std::min(t1, t);
        if (t1 - t0 <= 1e-9) return false;
    }
    return true;
}
}  // namespace

std::set<std::size_t> deck_cells(const Cells& cells, const Result& result) {
    std::set<std::size_t> out;
    for (const auto& rr : result.roads)
        for (const auto& x : rr.crossings)
            if (x.joins) for (const auto& c : x.cells) out.insert(cells.index(c));
    return out;
}

// GetPortToGateDistanceForSettlement reads the All navigation cache: sea faces of the port's basin, one region switch, then land.
// The bound is the shortest plane path from the sea cell's point nearest the gate point to the gate point that enters no mountain
// cell, no row off the grid and no navigable cell of another basin (a deck cell excepted). The placed port point lies in its cell
// and the engine path enters none of those cells, so the engine path is no shorter. Only cells within limit of the start are
// obstacles, which leaves every path up to limit long as it was: the result is exact against limit, and once the search passes
// limit it returns a length over limit (callers compare it only against limit). Seed 43's town mnbl_add01e64_t_80ec1de1a0: a port
// cell two cells from the gate cell with a mountain cell between them, an engine path of 15.02 units against 12.5.
double port_path_bound(const Cells& cells, const Result& result, const std::set<std::size_t>& decks, Cell sea, int basin, Point gate, double limit) {
    const double s = cells.spacing(), width = cells.cols() * s, sx = (sea.col + 0.5) * s;
    Point g = gate;
    while (g.x - sx > width / 2) g.x -= width;
    while (sx - g.x > width / 2) g.x += width;
    const Point start{std::clamp(g.x, sea.col * s, (sea.col + 1) * s), std::clamp(g.y, sea.row * s, (sea.row + 1) * s)};
    std::vector<std::pair<int, int>> blocked;   // (row, unwrapped column)
    for (int r = static_cast<int>(std::floor((start.y - limit) / s)); r <= static_cast<int>(std::floor((start.y + limit) / s)); ++r)
        for (int c = static_cast<int>(std::floor((start.x - limit) / s)); c <= static_cast<int>(std::floor((start.x + limit) / s)); ++c) {
            if (r < 0 || r >= cells.rows()) { blocked.push_back({r, c}); continue; }
            const std::size_t i = cells.index(cells.wrap(r, c));
            const auto k = result.cls[i];
            if (k == Mountain || (navigable(k) && result.basin_of[i] != basin && !decks.count(i))) blocked.push_back({r, c});
        }
    auto clear = [&](Point a, Point b) { return std::none_of(blocked.begin(), blocked.end(), [&](const std::pair<int, int>& o) { return enters(a, b, o.second * s, o.first * s, s); }); };
    // Dijkstra over the visibility graph of the start, the gate and the obstacle corners.
    std::vector<Point> nodes{start, g};
    std::set<std::pair<int, int>> corners;
    for (const auto& [r, c] : blocked)
        for (int dr = 0; dr <= 1; ++dr)
            for (int dc = 0; dc <= 1; ++dc)
                if (corners.insert({r + dr, c + dc}).second) nodes.push_back({(c + dc) * s, (r + dr) * s});
    const double inf = std::numeric_limits<double>::infinity();
    std::vector<double> dist(nodes.size(), inf);
    std::vector<bool> done(nodes.size(), false);
    dist[0] = 0;
    for (;;) {
        std::size_t u = nodes.size();
        for (std::size_t i = 0; i < nodes.size(); ++i) if (!done[i] && (u == nodes.size() || dist[i] < dist[u])) u = i;
        if (u == nodes.size() || dist[u] == inf) return inf;
        if (u == 1 || dist[u] > limit) return dist[u];
        done[u] = true;
        for (std::size_t v = 0; v < nodes.size(); ++v) {
            if (done[v]) continue;
            const double d = dist[u] + std::hypot(nodes[v].x - nodes[u].x, nodes[v].y - nodes[u].y);
            if (d < dist[v] && clear(nodes[u], nodes[v])) dist[v] = d;
        }
    }
}

void bind_ports(const Input& input, const Policy& policy, const Cells& cells, Result& result) {
    std::map<std::string, std::size_t> placement_of;
    for (std::size_t i = 0; i < result.placements.size(); ++i) placement_of[result.placements[i].id] = i;
    std::set<std::string> excluded;
    for (const auto& e : result.exclusions) excluded.insert(e.id);
    std::map<std::string, std::vector<const Harbour*>> by_town;   // ordered by town id
    for (const auto& h : input.harbours) {
        if (!placement_of.count(h.town_id) || result.placements[placement_of.at(h.town_id)].kind != "town") throw json::Error("broken_join", "Harbour " + h.village_uid + " names unknown town " + h.town_id, json::Location{"", 0, 1, 1, "$"});
        by_town[h.town_id].push_back(&h);
    }
    const auto decks = deck_cells(cells, result);
    const double limit = policy.max_port_gate_cells * cells.spacing();
    std::set<std::size_t> held_land, held_sea;   // pairs already serving a town
    for (auto& [town_id, harbours] : by_town) {
        std::sort(harbours.begin(), harbours.end(), [](const Harbour* a, const Harbour* b) { return a->village_uid < b->village_uid; });
        const auto& town = result.placements[placement_of.at(town_id)];
        Port port;
        port.town_id = town_id; port.town_uid = town.uid;
        const int gate_land = town.cell.valid() ? result.land_of[cells.index(town.cell)] : -1;
        for (const auto* h : harbours) {
            PortCandidate c;
            c.village_id = h->village_id; c.village_uid = h->village_uid; c.sea_node = h->sea_node; c.quality = h->quality;
            auto invalid = [&](const std::string& reason) { c.status = "invalid"; c.reason = reason; port.candidates.push_back(c); };
            if (excluded.count(h->village_id)) { invalid("harbour_village_excluded"); continue; }
            if (!town.cell.valid() || gate_land < 0) { invalid("town_unplaceable"); continue; }
            const auto touching = cells.touching(h->sea_point);
            for (const auto& t : touching)
                if (navigable(result.cls[cells.index(t)])) { c.harbour_cell = t; break; }
            bool shifted = false;
            if (!c.harbour_cell.valid()) {
                double best = std::numeric_limits<double>::max();
                const auto base = touching.front();
                const int reach = policy.max_harbour_shift_cells + 1;
                for (int r = std::max(0, base.row - reach); r <= std::min(cells.rows() - 1, base.row + reach); ++r)
                    for (int dc = -reach; dc <= reach; ++dc) {
                        const Cell cell = cells.wrap(r, base.col + dc);
                        if (!navigable(result.cls[cells.index(cell)])) continue;
                        const double d = cells.distance(h->sea_point, cell);
                        if (d > policy.max_harbour_shift_cells) continue;
                        if (d < best || (d == best && cell < c.harbour_cell)) { best = d; c.harbour_cell = cell; }
                    }
                shifted = c.harbour_cell.valid();
            }
            if (!c.harbour_cell.valid()) { invalid("sea_node_not_navigable"); continue; }
            c.harbour_shift = cells.distance(h->sea_point, c.harbour_cell);
            if (shifted) result.repairs.push_back({"harbour_shift", h->village_uid, "the producer sea node touches no navigable cell; the harbour resolves to the nearest one", c.harbour_cell, touching.front(), c.harbour_shift});
            c.basin = result.basin_of[cells.index(c.harbour_cell)];
            if (!result.basins[static_cast<std::size_t>(c.basin)].open) { invalid("closed_basin"); continue; }
            struct Pair { double gate, entrance, harbour; Cell land, sea; };
            std::vector<Pair> pairs;
            bool any_pair = false, any_free = false;
            for (std::size_t i = 0; i < cells.size(); ++i) {
                if (result.cls[i] != Land || result.land_of[i] != gate_land) continue;
                const Cell l = cells.at(i);
                for (const auto& s : cells.neighbours(l)) {
                    const std::size_t si = cells.index(s);
                    if (result.basin_of[si] != c.basin) continue;
                    any_pair = true;
                    if (held_land.count(i) || held_sea.count(si)) continue;
                    any_free = true;
                    if (decks.count(si)) continue;
                    pairs.push_back({cells.distance(s, town.cell), cells.distance(l, town.cell), cells.distance(s, c.harbour_cell), l, s});
                }
            }
            if (pairs.empty()) { invalid(any_free ? "port_pair_under_bridge" : any_pair ? "port_pair_taken" : "basin_not_reachable_from_gate"); continue; }
            std::sort(pairs.begin(), pairs.end(), [](const Pair& a, const Pair& b) {
                if (a.gate != b.gate) return a.gate < b.gate;
                if (a.entrance != b.entrance) return a.entrance < b.entrance;
                if (a.harbour != b.harbour) return a.harbour < b.harbour;
                return a.land < b.land || (a.land == b.land && a.sea < b.sea);
            });
            if (pairs.front().gate > policy.max_port_gate_cells) { invalid("port_too_far_from_gate (" + cells_text(pairs.front().gate) + ")"); continue; }
            const Pair* pick = nullptr;
            double first_bound = -1;
            for (const auto& p : pairs) {
                if (p.gate > policy.max_port_gate_cells) break;
                const double bound = port_path_bound(cells, result, decks, p.sea, c.basin, town.point, limit);
                if (bound <= limit) { pick = &p; break; }
                if (first_bound < 0) first_bound = bound;
            }
            if (!pick) { invalid("port_path_too_long (" + units_text(first_bound) + ")"); continue; }
            c.land_cell = pick->land; c.sea_cell = pick->sea; c.gate_distance = pick->gate; c.entrance_distance = pick->entrance;
            c.status = "valid";
            port.candidates.push_back(c);
        }
        for (const auto& c : port.candidates) {
            if (c.status != "valid") continue;
            const bool better = !port.valid || c.gate_distance < port.chosen.gate_distance || (c.gate_distance == port.chosen.gate_distance && (c.quality > port.chosen.quality ||
                                (c.quality == port.chosen.quality && c.village_uid < port.chosen.village_uid)));
            if (better) { port.valid = true; port.chosen = c; }
        }
        if (port.valid) {
            held_land.insert(cells.index(port.chosen.land_cell));
            held_sea.insert(cells.index(port.chosen.sea_cell));
            result.basins[static_cast<std::size_t>(port.chosen.basin)].ports.push_back(town_id);
        }
        result.ports.push_back(port);
    }
}

}  // namespace worldbridge::travel
