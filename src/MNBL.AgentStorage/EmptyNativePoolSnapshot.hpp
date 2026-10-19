#pragma once
#include "NativePoolSnapshot.hpp"

namespace mnbl::storage {
// This is a necessary metadata gate for a newly constructed STOCK Mission.
// It deliberately cannot import occupied state: the230 header is phase-sensitive.
// Caller still provides readable, immutable views, completed construction,
// native lifetime ownership and exclusion of mutation until adoption commits.
// Passing this check alone grants none of those guarantees.
enum class EmptySnapshotFailure { None, Dimensions, Counters, Header, Link };
struct EmptySnapshotCheck {
    EmptySnapshotFailure failure = EmptySnapshotFailure::None;
    std::size_t pool = pool_count;
    std::uint64_t index = no_slot;
    explicit operator bool() const noexcept { return failure == EmptySnapshotFailure::None; }
};
inline EmptySnapshotCheck check_empty_stock_native_pools(
    const std::array<NativePoolSnapshot, pool_count>& pools) noexcept {
    constexpr std::size_t capacity = 2040;
    constexpr std::array<std::size_t, pool_count> widths{0xc60,0xc60,0x230,0x2c8,0x2c8};
    constexpr std::array<std::size_t, pool_count> headers{0x10,0x10,0x1c,0,0};
    for (std::size_t pool=0;pool<pool_count;++pool) {
        const auto& snapshot=pools[pool];
        if (snapshot.records.size()!=capacity*widths[pool] || snapshot.signed_links.size()!=capacity*2)
            return {EmptySnapshotFailure::Dimensions,pool,no_slot};
        if (snapshot.live_count!=0 || snapshot.highwater!=0)
            return {EmptySnapshotFailure::Counters,pool,no_slot};
        for (std::size_t index=0;index<capacity;++index) {
            std::int32_t header;
            std::int16_t link;
            std::memcpy(&header,snapshot.records.data()+index*widths[pool]+headers[pool],sizeof(header));
            std::memcpy(&link,snapshot.signed_links.data()+index*2,sizeof(link));
            if (header!=-1) return {EmptySnapshotFailure::Header,pool,index};
            if (link!=-1) return {EmptySnapshotFailure::Link,pool,index};
        }
    }
    return {};
}
}
