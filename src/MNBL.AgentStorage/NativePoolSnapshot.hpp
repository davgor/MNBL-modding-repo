#pragma once
#include "StableAgentStorage.hpp"
#include <cstring>
#include <span>

namespace mnbl::storage {

// Views of a caller-frozen snapshot, never a process-memory acquisition API.
// Native mutation must remain excluded until adoption commits. The validator
// does not retain these views or infer ownership from numeric addresses.
struct NativePoolSnapshot {
    std::span<const std::byte> records;
    std::span<const std::byte> signed_links;
    std::int32_t live_count;
    std::int32_t highwater;
};

inline std::array<std::vector<std::uint64_t>, 2> validate_native_pool_snapshot(
    const std::array<NativePoolSnapshot, pool_count>& pools, std::uint64_t capacity) {
    // This imports the old signed16 format; growing storage itself is wider.
    if (capacity == 0 || capacity > 32766)
        throw std::invalid_argument("Native signed-link snapshot capacity invalid");
    constexpr std::array<std::size_t, pool_count> widths{0xc60, 0xc60, 0x230, 0x2c8, 0x2c8};
    constexpr std::array<std::size_t, pool_count> header_offsets{0x10, 0x10, 0x1c, 0, 0};
    const auto count = static_cast<std::size_t>(capacity);
    std::array<std::vector<std::uint64_t>, pool_count> occupied;
    for (std::size_t pool = 0; pool < pool_count; ++pool) {
        const auto& snapshot = pools[pool];
        if (snapshot.records.size() != count * widths[pool] ||
            snapshot.signed_links.size() != count * sizeof(std::int16_t) ||
            snapshot.live_count < 0 || snapshot.highwater < 0 ||
            static_cast<std::uint64_t>(snapshot.live_count) > capacity ||
            static_cast<std::uint64_t>(snapshot.highwater) > capacity)
            throw std::invalid_argument("Native snapshot dimensions or counters invalid");
        auto& live = occupied[pool];
        live.reserve(static_cast<std::size_t>(snapshot.live_count));
        for (std::size_t index = 0; index < count; ++index) {
            std::int32_t header;
            std::memcpy(&header, snapshot.records.data() + index * widths[pool] + header_offsets[pool], sizeof(header));
            if (header < 0) continue;
            if (static_cast<std::uint64_t>(header) != index)
                throw std::invalid_argument("Native record header disagrees with slot index");
            live.push_back(index);
        }
        if (live.size() != static_cast<std::size_t>(snapshot.live_count) ||
            (live.empty() ? 0 : live.back() + 1) != static_cast<std::uint64_t>(snapshot.highwater))
            throw std::invalid_argument("Native occupancy disagrees with count or highwater");
        std::size_t current = 0;
        for (std::size_t index = 0; index < count; ++index) {
            std::int16_t link;
            std::memcpy(&link, snapshot.signed_links.data() + index * sizeof(link), sizeof(link));
            std::int64_t expected = -1;
            if (current < live.size() && live[current] == index) {
                ++current;
                expected = static_cast<std::int64_t>(current < live.size() ? live[current] : capacity + 1);
            }
            if (link != expected)
                throw std::invalid_argument("Native live-successor link disagrees with occupancy");
        }
    }
    for (std::size_t companion = 2; companion < pool_count; ++companion)
        if (occupied[companion] != occupied[0])
            throw std::invalid_argument("Primary and companion occupancy disagree");
    return {std::move(occupied[0]), std::move(occupied[1])};
}
}
