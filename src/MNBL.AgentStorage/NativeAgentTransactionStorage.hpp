#pragma once
#include "OwnedNativeAgentStorage.hpp"
#include <algorithm>
#include <functional>

namespace mnbl::storage {

// Isolated publication wrapper, not an engine allocation/removal implementation.
// Every access must use this wrapper. Its gate cannot stop original engine code,
// jobs or pointers retained after a lease. Payload mutation through concurrent
// read leases remains the caller's responsibility.
// A thread holding a lease must not invoke mutation (including try_close) on
// this instance: std::shared_mutex is nonrecursive. Use another thread or end
// the lease first. The public wrapper itself must outlive in-flight method calls.
class NativeAgentTransactionStorage {
public:
    using Elements = std::array<void*, pool_count>;
    using Transition = void (*)(void*, DomainHandle, const Elements&) noexcept;
    struct Transitions {
        void* context = nullptr;
        Transition reset = nullptr;
        Transition cleanup = nullptr;
        std::shared_ptr<void> lifetime;
    };
private:
    static std::size_t domain_index(AgentDomain domain) {
        if (domain != AgentDomain::Primary && domain != AgentDomain::Secondary)
            throw std::invalid_argument("Unknown agent domain");
        return static_cast<std::size_t>(domain);
    }
    static constexpr auto heap_order = std::greater<std::uint64_t>{};
    struct Owner {
        // Both runtime owners survive callbacks, payload destruction and leases.
        Transitions transitions;
        mutable std::shared_mutex gate;
        OwnedNativeAgentStorage storage;
        std::array<std::vector<std::uint64_t>, 2> free;
        std::size_t block_size;
        bool closed = false;

        Owner(std::size_t size, NativeElementFactory::Bindings bindings, Transitions effects)
            : transitions(std::move(effects)), storage(size, std::move(bindings)), block_size(size) {
            if (!transitions.reset || !transitions.cleanup || !transitions.lifetime)
                throw std::invalid_argument("Persistent reset/cleanup callbacks required");
        }
        Elements elements(const OwnedNativeAgentStorage::ReadLease& lease, DomainHandle h) const {
            Elements pointers{};
            for (std::size_t pool = 0; pool < pool_count; ++pool)
                if (h.domain == AgentDomain::Secondary ? pool == 1 : pool != 1)
                    pointers[pool] = lease.get(h, pool);
            return pointers;
        }
        bool release_locked(DomainHandle h, bool return_to_heap) {
            {
                auto lease = storage.read();
                if (!lease.valid(h)) return false;
                const auto pointers = elements(lease, h);
                // Cleanup sees the still-live identity. It must finish engine
                // removal, if any, and leave default-destructible inactive data.
                transitions.cleanup(transitions.context, h, pointers);
            }
            if (!storage.release(h)) std::terminate(); // impossible without bypass/reentrancy
            if (return_to_heap && h.generation != no_slot) {
                auto& heap = free[static_cast<std::size_t>(h.domain)];
                // Reserved before capacity publication. No allocation on release.
                if (heap.size() == heap.capacity()) std::terminate();
                heap.push_back(h.index);
                std::push_heap(heap.begin(), heap.end(), heap_order);
            }
            return true;
        }
        void close_locked() noexcept {
            if (closed) return;
            // No allocation, no saved pointer beyond its lease. The outer gate
            // is exclusive (or this is final-owner destruction with no readers).
            for (const auto domain : {AgentDomain::Primary, AgentDomain::Secondary}) {
                while (true) {
                    DomainHandle h;
                    {
                        auto lease = storage.read();
                        const auto index = lease.next(domain);
                        if (index == no_slot) break;
                        h = lease.current(domain, index);
                    }
                    if (!release_locked(h, false)) std::terminate();
                }
            }
            if (!storage.try_close()) std::terminate();
            free[0].clear(); free[1].clear(); closed = true;
        }
        ~Owner() noexcept { close_locked(); }
    };
    std::shared_ptr<Owner> owner_;

    DomainHandle acquire_locked(Owner& owner, AgentDomain domain, std::uint64_t index) {
        const auto ordinal = domain_index(domain);
        if (owner.closed) throw std::logic_error("Storage is closed");
        auto& heap = owner.free[ordinal];
        const auto found = std::find(heap.begin(), heap.end(), index);
        if (found == heap.end()) return {domain, no_slot, 0};
        // Backend's private free-list ordering is intentionally not consulted.
        const auto h = owner.storage.acquire_at(domain, index);
        if (h.index != index) throw std::logic_error("Free index ledger disagreement");
        *found = heap.back(); heap.pop_back();
        std::make_heap(heap.begin(), heap.end(), heap_order);
        {
            auto lease = owner.storage.read();
            const auto pointers = owner.elements(lease, h);
            // Backend occupancy is committed, but no wrapper reader can see it
            // until reset finishes. No exception/rollback policy is invented.
            owner.transitions.reset(owner.transitions.context, h, pointers);
        }
        return h;
    }
public:
    class ReadLease {
        // Reverse destruction: backend lock, outer gate, persistent owner.
        std::shared_ptr<Owner> owner_;
        std::shared_lock<std::shared_mutex> gate_;
        OwnedNativeAgentStorage::ReadLease lease_;
        friend class NativeAgentTransactionStorage;
        explicit ReadLease(std::shared_ptr<Owner> owner)
            : owner_(std::move(owner)), gate_(owner_->gate), lease_(owner_->storage.read()) {}
    public:
        ReadLease(ReadLease&&) noexcept = default;
        ReadLease(const ReadLease&) = delete;
        ReadLease& operator=(const ReadLease&) = delete;
        std::uint64_t capacity() const noexcept { return lease_.capacity(); }
        std::uint64_t live(AgentDomain domain) const { return lease_.live(domain); }
        std::uint64_t highwater(AgentDomain domain) const { return lease_.highwater(domain); }
        std::uint64_t next(AgentDomain domain, std::uint64_t from = 0) const { return lease_.next(domain, from); }
        std::uint64_t previous(AgentDomain domain, std::uint64_t before) const { return lease_.previous(domain, before); }
        DomainHandle current(AgentDomain domain, std::uint64_t index) const noexcept { return lease_.current(domain, index); }
        bool valid(DomainHandle h) const noexcept { return lease_.valid(h); }
        void* get(DomainHandle h, std::size_t pool) const { return lease_.get(h, pool); }
        void* physical(std::size_t pool, std::uint64_t index) const { return lease_.physical(pool, index); }
    };

    // Callbacks are persistent, nonreentrant, nonescaping and nonfailing. Their
    // noexcept type does not contain SEH or make arbitrary engine code safe.
    NativeAgentTransactionStorage(std::size_t block_size, NativeElementFactory::Bindings bindings,
                                  Transitions transitions)
        : owner_(std::make_shared<Owner>(block_size, std::move(bindings), std::move(transitions))) {}
    NativeAgentTransactionStorage(const NativeAgentTransactionStorage&) = delete;
    NativeAgentTransactionStorage& operator=(const NativeAgentTransactionStorage&) = delete;
    ReadLease read() const { return ReadLease(owner_); }

    void grow(std::uint64_t minimum_capacity) {
        auto& owner = *owner_; std::unique_lock lock(owner.gate);
        if (owner.closed) throw std::logic_error("Storage is closed");
        std::uint64_t old_capacity;
        { auto lease = owner.storage.read(); old_capacity = lease.capacity(); }
        if (minimum_capacity <= old_capacity) return;
        const auto block = static_cast<std::uint64_t>(owner.block_size);
        if (minimum_capacity > no_slot - block) throw std::length_error("Storage index overflow");
        const auto capacity = ((minimum_capacity + block - 1) / block) * block;
        for (auto& heap : owner.free) {
            if (capacity > heap.max_size()) throw std::length_error("Free index directory overflow");
            heap.reserve(static_cast<std::size_t>(capacity));
        }
        // Every potentially throwing heap operation precedes backend publication.
        // A failure may enlarge vector capacity, never logical storage state.
        owner.storage.grow(minimum_capacity);
        for (auto& heap : owner.free) {
            for (auto index = old_capacity; index < capacity; ++index) heap.push_back(index);
            std::make_heap(heap.begin(), heap.end(), heap_order);
        }
    }
    void adopt_first_block(std::array<void*, pool_count> payload, std::shared_ptr<void> lifetime,
                           const std::array<std::vector<std::uint64_t>, 2>& occupied) {
        auto& owner = *owner_; std::unique_lock lock(owner.gate);
        if (owner.closed) throw std::logic_error("Storage is closed");
        { auto lease = owner.storage.read(); if (lease.capacity()) throw std::logic_error("Adoption requires empty storage"); }
        std::array<std::vector<std::uint64_t>, 2> pending;
        for (std::size_t domain = 0; domain < 2; ++domain) {
            pending[domain].reserve(owner.block_size);
            std::vector<bool> used(owner.block_size, false);
            for (const auto index : occupied[domain]) {
                if (index >= owner.block_size || used[static_cast<std::size_t>(index)])
                    throw std::invalid_argument("Invalid or duplicate adopted index");
                used[static_cast<std::size_t>(index)] = true;
            }
            for (std::size_t index = 0; index < owner.block_size; ++index)
                if (!used[index]) pending[domain].push_back(index);
            std::make_heap(pending[domain].begin(), pending[domain].end(), heap_order);
        }
        owner.storage.adopt_first_block(payload, std::move(lifetime), occupied);
        owner.free.swap(pending); // no-throw publication after successful backend adoption
    }
    DomainHandle acquire(AgentDomain domain) {
        const auto ordinal = domain_index(domain);
        auto& owner = *owner_; std::unique_lock lock(owner.gate);
        if (owner.closed) throw std::logic_error("Storage is closed");
        if (owner.free[ordinal].empty()) return {domain, no_slot, 0};
        return acquire_locked(owner, domain, owner.free[ordinal].front());
    }
    DomainHandle acquire_at(AgentDomain domain, std::uint64_t index) {
        domain_index(domain);
        auto& owner = *owner_; std::unique_lock lock(owner.gate);
        return acquire_locked(owner, domain, index);
    }
    bool release(DomainHandle h) {
        auto& owner = *owner_; std::unique_lock lock(owner.gate);
        return owner.release_locked(h, true);
    }
    bool try_close() {
        auto& owner = *owner_; std::unique_lock lock(owner.gate, std::try_to_lock);
        if (!lock.owns_lock()) return false;
        owner.close_locked(); return true;
    }
};
}
