#pragma once

#include "../MNBL.AgentStorage/NativeElementFactory.hpp"

namespace mnbl::probe {
using namespace mnbl::storage;
struct ElementProbeResult {
    std::uint64_t abi_size=sizeof(ElementProbeResult), abi_version=1, status=0, stage=0;
    std::uint64_t capacity=0, pointer_checks=0, header_checks=0;
    std::uint64_t allocation_attempts=0, allocations=0, releases=0, constructor_attempts=0;
    std::array<std::uint64_t,5> constructed{}, destroyed{};
    std::uint64_t allocation_failure_checked=0, constructor_failure_checked=0, exhaustion_checked=0, reuse_checked=0;
    char error[512]{};
};
inline void require(bool value,const char* message) {if(!value)throw std::runtime_error(message);}
class InjectedConstructionFailure : public std::runtime_error {
public: InjectedConstructionFailure():std::runtime_error("Intentional pre-constructor rollback test"){}
};
struct InstrumentedCallbacks {
    Callbacks native;
    ElementProbeResult& result;
    bool fail_allocation=false;
    std::uint64_t fail_constructor=no_slot;
    static void* allocate(void* context,std::size_t size) {
        auto& self=*static_cast<InstrumentedCallbacks*>(context);++self.result.allocation_attempts;
        if(self.fail_allocation){self.fail_allocation=false;return nullptr;}
        auto p=self.native.allocate(self.native.context,size);if(p)++self.result.allocations;return p;
    }
    static void release(void* context,void* pointer)noexcept {
        auto& self=*static_cast<InstrumentedCallbacks*>(context);
        self.native.release(self.native.context,pointer);++self.result.releases;
    }
    static void construct(void* context,std::size_t pool,std::uint64_t index,void* pointer) {
        auto& self=*static_cast<InstrumentedCallbacks*>(context);
        if(self.result.constructor_attempts++==self.fail_constructor)throw InjectedConstructionFailure();
        self.native.construct(self.native.context,pool,index,pointer);++self.result.constructed[pool];
    }
    static void destroy(void* context,std::size_t pool,std::uint64_t index,void* pointer)noexcept {
        auto& self=*static_cast<InstrumentedCallbacks*>(context);
        self.native.destroy(self.native.context,pool,index,pointer);++self.result.destroyed[pool];
    }
    Callbacks callbacks(){return{this,allocate,release,construct,destroy};}
};

// No Mission pointer, hook or activation entry is accepted. These arrays remain
// private and every native element stays inactive. This is a constructor and
// cleanup prerequisite probe, NOT a live soldier-capacity expansion test.
// The caller must establish the native runtime/thread contract before entry.
inline void exercise_unpublished_elements(NativeElementFactory& factory,ElementProbeResult& result) {
    InstrumentedCallbacks callbacks{factory.callbacks(),result};
    AgentDomainStorage storage(30,NativeElementFactory::strides,callbacks.callbacks());
    result.stage=1;storage.grow(30);
    std::array<std::array<void*,30>,5> original{};
    {auto lease=storage.read();for(std::size_t p=0;p<5;++p)for(std::uint64_t i=0;i<30;++i)original[p][i]=lease.physical(p,i);}
    auto stable=[&](std::uint64_t expected){
        auto lease=storage.read();require(lease.capacity()==expected,"Unexpected private capacity");
        for(std::size_t p=0;p<5;++p)for(std::uint64_t i=0;i<30;++i){require(lease.physical(p,i)==original[p][i],"Private growth moved an earlier element");++result.pointer_checks;}
        result.capacity=expected;
    };
    result.stage=2;callbacks.fail_allocation=true;
    bool allocation_failed=false;
    try{storage.grow(60);}catch(const std::bad_alloc&){allocation_failed=true;}
    require(allocation_failed,"Allocation failure was not exercised");stable(30);result.allocation_failure_checked=1;
    result.stage=3;callbacks.fail_constructor=result.constructor_attempts+37;
    bool constructor_failed=false;
    try{storage.grow(90);}catch(const InjectedConstructionFailure&){constructor_failed=true;}
    require(constructor_failed,"Pre-constructor failure was not exercised");
    callbacks.fail_constructor=no_slot;stable(30);
    for(std::size_t p=0;p<5;++p)require(result.constructed[p]-result.destroyed[p]==30,"Rollback did not retain exactly the original private block");
    result.constructor_failure_checked=1;
    result.stage=4;storage.grow(60);stable(60);
    result.stage=5;storage.grow(90);stable(90);
    {auto lease=storage.read();for(std::size_t p=0;p<5;++p)for(std::uint64_t i=0;i<90;++i){
        std::int32_t header=0;std::memcpy(&header,static_cast<std::byte*>(lease.physical(p,i))+NativeElementFactory::headers[p],4);
        require(header==-1,"Unpublished native element not inactive");++result.header_checks;
    }}
    // Ledger-only exhaustion/reuse. Neither logical acquisition nor release
    // calls native activation/reset or changes the inactive object headers.
    result.stage=6;
    for(auto domain:{AgentDomain::Primary,AgentDomain::Secondary}){
        std::array<DomainHandle,90> handles{};
        for(auto& h:handles){h=storage.acquire(domain);require(h.index!=no_slot,"Private ledger exhausted early");}
        require(storage.acquire(domain).index==no_slot,"Private ledger overflow accepted");
        auto old=handles[17];require(storage.release(old),"Private release failed");
        auto again=storage.acquire(domain);require(again.index==old.index && again.generation==old.generation+1,"Private reuse lost identity");
        require(!storage.release(old),"Private stale generation accepted");handles[17]=again;
        for(auto h:handles)require(storage.release(h),"Private ledger cleanup failed");
        auto lease=storage.read();require(lease.live(domain)==0 && lease.highwater(domain)==0,"Private ledger not empty");
    }
    result.exhaustion_checked=1;result.reuse_checked=1;
    result.stage=7;require(storage.try_close(),"Private storage would not close");
    for(std::size_t p=0;p<5;++p)require(result.constructed[p]==result.destroyed[p],"Private element cleanup count differs");
    require(result.allocations==result.releases,"Private allocation leak");
    result.stage=8;result.status=1;
}
}
