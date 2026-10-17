#pragma once
#include "NativeElementFactory.hpp"

namespace mnbl::storage {
// Keep callback context, native module ownership and payloads in one lifetime.
// Every returned lease retains this owner; destroying the public wrapper cannot
// leave a lease with a dangling factory. Engine Mission/job lifetime remains a
// separate adoption/barrier obligation.
class OwnedNativeAgentStorage {
    struct Owner {
        NativeElementFactory factory;
        AgentDomainStorage storage;
        Owner(std::size_t block_size,NativeElementFactory::Bindings bindings)
            :factory(std::move(bindings)),storage(block_size,NativeElementFactory::strides,factory.callbacks()){}
        // Reverse member destruction: storage finishes native destructors while
        // factory and its runtime_owner are still alive.
    };
    std::shared_ptr<Owner> owner_;
public:
    class ReadLease {
        std::shared_ptr<Owner> owner_;
        AgentDomainStorage::ReadLease lease_;
        friend class OwnedNativeAgentStorage;
        explicit ReadLease(std::shared_ptr<Owner> owner):owner_(std::move(owner)),lease_(owner_->storage.read()){}
    public:
        ReadLease(ReadLease&&) noexcept=default;
        ReadLease(const ReadLease&)=delete;
        ReadLease& operator=(const ReadLease&)=delete;
        // Move assignment is deliberately unavailable: owner replacement must
        // never destroy callbacks before an older read lock is released.
        std::uint64_t capacity()const noexcept{return lease_.capacity();}
        std::uint64_t live(AgentDomain domain)const{return lease_.live(domain);}
        std::uint64_t highwater(AgentDomain domain)const{return lease_.highwater(domain);}
        std::uint64_t next(AgentDomain domain,std::uint64_t from=0)const{return lease_.next(domain,from);}
        std::uint64_t previous(AgentDomain domain,std::uint64_t before)const{return lease_.previous(domain,before);}
        bool valid(DomainHandle handle)const noexcept{return lease_.valid(handle);}
        DomainHandle current(AgentDomain domain,std::uint64_t index)const noexcept{return lease_.current(domain,index);}
        void* get(DomainHandle handle,std::size_t pool)const{return lease_.get(handle,pool);}
        void* physical(std::size_t pool,std::uint64_t index)const{return lease_.physical(pool,index);}
    };
    OwnedNativeAgentStorage(std::size_t block_size,NativeElementFactory::Bindings bindings)
        :owner_(std::make_shared<Owner>(block_size,std::move(bindings))){}
    OwnedNativeAgentStorage(const OwnedNativeAgentStorage&)=delete;
    OwnedNativeAgentStorage& operator=(const OwnedNativeAgentStorage&)=delete;
    ReadLease read()const{return ReadLease(owner_);}
    void grow(std::uint64_t capacity){owner_->storage.grow(capacity);}
    void adopt_first_block(std::array<void*,pool_count> payload,std::shared_ptr<void> lifetime,
                          const std::array<std::vector<std::uint64_t>,2>& occupied){
        owner_->storage.adopt_first_block(payload,std::move(lifetime),occupied);
    }
    DomainHandle acquire(AgentDomain domain){return owner_->storage.acquire(domain);}
    DomainHandle acquire_at(AgentDomain domain,std::uint64_t index){return owner_->storage.acquire_at(domain,index);}
    bool release(DomainHandle handle){return owner_->storage.release(handle);}
    bool try_close(){return owner_->storage.try_close();}
};
}
