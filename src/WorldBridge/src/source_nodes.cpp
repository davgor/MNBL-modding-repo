#include "worldbridge/source_nodes.hpp"
#include <cstdlib>
#include <string>

namespace worldbridge::source {
namespace {
json::Location here() { return json::Location{"", 0, 1, 1, "$"}; }
std::int64_t floor_mod(std::int64_t value, std::int64_t modulus) { const auto r = value % modulus; return r < 0 ? r + modulus : r; }
}  // namespace

NodeGrid::NodeGrid(std::int64_t side) : n(side) {
    if (side < 3 || side > 65537) throw json::Error("out_of_range", "Grid side " + std::to_string(side) + " is outside 3..65537", here());
}

void NodeGrid::check(std::int64_t node) const {
    if (node < 0 || node >= node_count())
        throw json::Error("invalid_node", "Node " + std::to_string(node) + " is outside 0.." + std::to_string(node_count() - 1), here());
}

std::int64_t NodeGrid::node_of(std::int64_t column, std::int64_t row) const {
    if (column < 0 || column >= n || row < 0 || row >= n)
        throw json::Error("out_of_coverage", "Sample (" + std::to_string(column) + ", " + std::to_string(row) + ") is outside the " +
                                                 std::to_string(n) + "x" + std::to_string(n) + " grid", here());
    if (row == 0) return 0;
    if (row == n - 1) return south_pole();
    return 1 + (row - 1) * (n - 1) + column % (n - 1);  // column n-1 is column 0
}

Sample NodeGrid::sample_of(std::int64_t node) const {
    check(node);
    if (node == 0) return {0, 0};
    if (node == south_pole()) return {0, n - 1};
    return {(node - 1) % (n - 1), 1 + (node - 1) / (n - 1)};
}

Pole NodeGrid::pole_of(std::int64_t node) const {
    check(node);
    return node == 0 ? Pole::North : node == south_pole() ? Pole::South : Pole::None;
}

bool NodeGrid::duplicate(std::int64_t column, std::int64_t row) const {
    const auto node = node_of(column, row);
    const auto canonical = sample_of(node);
    return canonical.column != column || canonical.row != row;
}

bool NodeGrid::agrees(std::int64_t node, std::int64_t column, std::int64_t row) const {
    if (node < 0 || node >= node_count() || column < 0 || column >= n || row < 0 || row >= n) return false;
    return node_of(column, row) == node;
}

bool NodeGrid::adjacent(std::int64_t a, std::int64_t b) const {
    check(a); check(b);
    if (a == b) return false;
    const auto pa = pole_of(a), pb = pole_of(b);
    if (pa != Pole::None && pb != Pole::None) return false;  // the two poles never touch (n >= 3)
    if (pa != Pole::None || pb != Pole::None) {
        const auto ring = sample_of(pa != Pole::None ? b : a).row;
        return (pa == Pole::North || pb == Pole::North) ? ring == 1 : ring == n - 2;
    }
    const auto sa = sample_of(a), sb = sample_of(b);
    const auto dx = floor_mod(sb.column - sa.column, n - 1);
    return std::llabs(sb.row - sa.row) <= 1 && (dx <= 1 || dx == n - 2);
}

}  // namespace worldbridge::source
