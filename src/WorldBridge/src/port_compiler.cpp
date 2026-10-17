// Port binding (MMR-28). A port comes only from a producer harbour record of a kept harbour village (MMR-27 ports),
// never from a shore cell, and it serves its town's gate as the installed War Sails towns do.
//   harbour cell   the first navigable cell touching the producer's sea node (NW..SE), else the nearest navigable cell
//                  within maxHarbourShiftCells (a recorded harbour_shift, from the sea-node cell); none ->
//                  sea_node_not_navigable. Its basin must be open -> else closed_basin.
//   port pair      a land cell of the gate's own land component sharing an edge with a cell of that basin: the pair
//                  whose sea cell is nearest the gate, then whose land cell is nearest the gate, then nearest the
//                  harbour cell, then cell order. Pairs another town already holds are skipped (towns in id order).
//                  No pair -> basin_not_reachable_from_gate; only held pairs -> port_pair_taken; a port cell farther than
//                  maxPortGateCells from the gate cell -> port_too_far_from_gate.
// The town's port is its best valid candidate: nearest the gate, then highest harbour quality, then village UID.
#include <algorithm>
#include <limits>
#include <string>
#include "worldbridge/travel_graph.hpp"

namespace worldbridge::travel {
namespace {
bool navigable(std::uint8_t cls) { return cls == Sea || cls == Lake || cls == River; }
std::string cells_text(double d) { std::string t = std::to_string(d); return t.substr(0, t.find('.') + 3) + " cells"; }
}  // namespace

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
            bool found = false, any_pair = false;
            double best_gate = 0, best_entrance = 0, best_harbour = 0;
            for (std::size_t i = 0; i < cells.size(); ++i) {
                if (result.cls[i] != Land || result.land_of[i] != gate_land) continue;
                const Cell l = cells.at(i);
                for (const auto& s : cells.neighbours(l)) {
                    if (result.basin_of[cells.index(s)] != c.basin) continue;
                    any_pair = true;
                    if (held_land.count(i) || held_sea.count(cells.index(s))) continue;
                    const double dg = cells.distance(s, town.cell), de = cells.distance(l, town.cell), dh = cells.distance(s, c.harbour_cell);
                    const bool better = !found || dg < best_gate || (dg == best_gate && (de < best_entrance || (de == best_entrance && (dh < best_harbour ||
                                        (dh == best_harbour && (l < c.land_cell || (l == c.land_cell && s < c.sea_cell)))))));
                    if (better) { found = true; best_gate = dg; best_entrance = de; best_harbour = dh; c.land_cell = l; c.sea_cell = s; }
                }
            }
            if (!found) { invalid(any_pair ? "port_pair_taken" : "basin_not_reachable_from_gate"); continue; }
            c.gate_distance = best_gate; c.entrance_distance = best_entrance;
            if (c.gate_distance > policy.max_port_gate_cells) { invalid("port_too_far_from_gate (" + cells_text(c.gate_distance) + ")"); continue; }
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
