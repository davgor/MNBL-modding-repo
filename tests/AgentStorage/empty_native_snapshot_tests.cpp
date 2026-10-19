#include "../../src/MNBL.AgentStorage/EmptyNativePoolSnapshot.hpp"
#include <iostream>
using namespace mnbl::storage;
int main() {
    constexpr std::array<std::size_t,5> strides{0xc60,0xc60,0x230,0x2c8,0x2c8},headers{0x10,0x10,0x1c,0,0};
    std::array<std::vector<std::byte>,5> payload,links;
    std::array<NativePoolSnapshot,5> views;
    std::size_t checks=0;
    auto require=[&](bool value){++checks;if(!value)throw std::runtime_error("Empty native snapshot contract failed");};
    for(std::size_t p=0;p<5;++p) {
        payload[p].assign(2040*strides[p],std::byte{0xa5});
        links[p].assign(4080,std::byte{0xff});
        for(std::size_t i=0;i<2040;++i){std::int32_t inactive=-1;std::memcpy(payload[p].data()+i*strides[p]+headers[p],&inactive,4);}
        views[p]={payload[p],links[p],0,0};
    }
    require(static_cast<bool>(check_empty_stock_native_pools(views)));
    for(std::size_t p=0;p<5;++p) {
        auto rejected=[&](EmptySnapshotFailure reason,std::uint64_t index=no_slot){
            auto result=check_empty_stock_native_pools(views);
            require(!result&&result.failure==reason&&result.pool==p&&result.index==index);
        };
        auto original=views[p];
        views[p].records=std::span<const std::byte>(payload[p]).first(payload[p].size()-1);
        rejected(EmptySnapshotFailure::Dimensions);views[p]=original;
        views[p].signed_links=std::span<const std::byte>(links[p]).first(4079);
        rejected(EmptySnapshotFailure::Dimensions);views[p]=original;
        for(auto value:{-1,1,2041}) {
            views[p].live_count=value;rejected(EmptySnapshotFailure::Counters);views[p]=original;
            views[p].highwater=value;rejected(EmptySnapshotFailure::Counters);views[p]=original;
        }
        for(std::size_t index:{0U,1019U,2039U}) {
            auto address=payload[p].data()+index*strides[p]+headers[p];
            for(std::int32_t value:{0,-2,2039}) {
                std::memcpy(address,&value,4);rejected(EmptySnapshotFailure::Header,index);
            }
            std::int32_t inactive=-1;std::memcpy(address,&inactive,4);
            for(std::int16_t value:{std::int16_t{0},std::int16_t{-2},std::int16_t{2041}}) {
                std::memcpy(links[p].data()+index*2,&value,2);rejected(EmptySnapshotFailure::Link,index);
            }
            std::int16_t empty=-1;std::memcpy(links[p].data()+index*2,&empty,2);
        }
        require(static_cast<bool>(check_empty_stock_native_pools(views)));
    }
    // Native230 reset can hide an occupied payload behind header-1. An occupied
    // link must reject even if someone falsely supplies zero counters.
    std::int16_t sentinel=2041;std::memcpy(links[2].data()+17*2,&sentinel,2);
    auto phase=check_empty_stock_native_pools(views);
    require(!phase&&phase.failure==EmptySnapshotFailure::Link&&phase.pool==2&&phase.index==17);
    std::cout<<"PASS empty stock snapshot: "<<checks<<" checks;poisoned payloads;all-five dimensions/counters/headers/links;phase-mismatch rejection\n";
}
