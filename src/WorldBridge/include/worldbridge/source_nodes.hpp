// FWGC++ sphere-graph node indices, decoded separately from grid samples (MMR-25).
//
// A layer is an n-by-n latitude/longitude grid whose redundant samples the graph collapses
// (generator/src/grid.cpp SphereGrid::node_index and build_sphere_grid):
//   * node 0 is the north pole: every sample of row 0;
//   * nodes 1 .. (n-2)(n-1) are the distinct cells, row by row: row z in 1..n-2, column x in 0..n-2;
//   * the last node, 1 + (n-2)(n-1), is the south pole: every sample of row n-1;
//   * column n-1 duplicates column 0 (the longitude seam), so it has no nodes of its own.
// So a sample maps to exactly one node, and a node decodes to one canonical sample (poles at column 0).
#pragma once
#include <cstdint>
#include "worldbridge/json_stream.hpp"

namespace worldbridge::source {

enum class Pole { None, North, South };

struct Sample { std::int64_t column = 0, row = 0; };

class NodeGrid {
public:
    explicit NodeGrid(std::int64_t side);  // refuses side < 3
    std::int64_t side() const { return n; }
    std::int64_t node_count() const { return 2 + (n - 2) * (n - 1); }
    std::int64_t south_pole() const { return node_count() - 1; }
    // The node of grid sample (column, row). Column n-1 is the seam duplicate of column 0; every sample of a pole
    // row is that pole. Refuses samples outside the grid (out_of_coverage); it never wraps or clamps.
    std::int64_t node_of(std::int64_t column, std::int64_t row) const;
    // The canonical sample of a node: column in 0..n-2, poles at column 0. Refuses unknown nodes (invalid_node).
    Sample sample_of(std::int64_t node) const;
    Pole pole_of(std::int64_t node) const;
    // True for samples that are copies: column n-1, and every pole-row sample but column 0.
    bool duplicate(std::int64_t column, std::int64_t row) const;
    // A record's (node, column, row) triple is consistent.
    bool agrees(std::int64_t node, std::int64_t column, std::int64_t row) const;
    // Graph adjacency of two distinct nodes: the 8-neighbourhood with wrapped longitude, and a pole is adjacent to
    // every cell of the ring next to it.
    bool adjacent(std::int64_t a, std::int64_t b) const;

private:
    std::int64_t n;
    void check(std::int64_t node) const;
};

}  // namespace worldbridge::source
