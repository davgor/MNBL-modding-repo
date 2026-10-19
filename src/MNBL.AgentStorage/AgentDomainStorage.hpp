#pragma once

// Reuse only the public callback contract and constants. The accepted single
// ledger prototype remains unchanged and is not used as this allocator.
#include "StableAgentStorage.hpp"
#include <bit>

namespace mnbl::storage {

enum class AgentDomain : std::uint8_t { Primary, Secondary };

// Handles are local to one storage instance. Domain is part of identity, not
// an authorization token; callers must not forge or change handle fields.
struct DomainHandle {
    AgentDomain domain = AgentDomain::Primary;
    std::uint64_t index = no_slot;
    std::uint64_t generation = 0;
};

// Offline storage mechanics, not engine objects or native index compatibility.
// Primary uses physical pools0,2,3,4. Secondary has its own ledger and pool1;
// no secondary-to-companion relationship is assumed. Both domains have the
// same published physical capacity, but independent occupancy and free lists.
class AgentDomainStorage {
    static constexpr std::size_t domain_count = 2;
    static bool known(AgentDomain domain) noexcept {
        return domain == AgentDomain::Primary || domain == AgentDomain::Secondary;
    }
    static std::size_t ordinal(AgentDomain domain) noexcept {
        return static_cast<std::size_t>(domain);
    }
    static bool permits(AgentDomain domain, std::size_t pool) noexcept {
        return pool < pool_count && known(domain) &&
               (domain == AgentDomain::Secondary ? pool == 1 : pool != 1);
    }
    struct Slot {
        std::uint64_t generation = 1;
        std::uint64_t next = no_slot;
        bool occupied = false;
    };
    struct Block {
        Callbacks callbacks;
        std::array<std::size_t, pool_count> strides;
        std::array<void*, pool_count> payload{};
        std::array<std::size_t, pool_count> constructed{};
        std::array<std::unique_ptr<Slot[]>, domain_count> slots;
        std::array<std::unique_ptr<std::uint64_t[]>, domain_count> occupied_bits;
        std::uint64_t start;
        std::size_t size;
        bool owns_payload = true;

        Block(Callbacks cb, std::array<std::size_t, pool_count> widths,
              std::uint64_t first, std::size_t count)
            : callbacks(cb), strides(widths),
              slots{std::make_unique<Slot[]>(count), std::make_unique<Slot[]>(count)},
              occupied_bits{std::make_unique<std::uint64_t[]>(count / 64 + (count % 64 != 0)),
                            std::make_unique<std::uint64_t[]>(count / 64 + (count % 64 != 0))},
              start(first), size(count) {}
        void* at(std::size_t pool, std::size_t index) const noexcept {
            return static_cast<std::byte*>(payload[pool]) + strides[pool] * index;
        }
        ~Block() noexcept {
            if (!owns_payload) return;
            for (std::size_t pool = pool_count; pool-- > 0;) {
                while (constructed[pool] != 0) {
                    const auto index = --constructed[pool];
                    callbacks.destroy(callbacks.context, pool, start + index, at(pool, index));
                }
                if (payload[pool]) callbacks.release(callbacks.context, payload[pool]);
            }
        }
        void initialize() {
            for (std::size_t pool = 0; pool < pool_count; ++pool) {
                payload[pool] = callbacks.allocate(callbacks.context, size * strides[pool]);
                if (!payload[pool]) throw std::bad_alloc();
            }
            for (std::size_t pool = 0; pool < pool_count; ++pool) {
                for (std::size_t index = 0; index < size; ++index) {
                    callbacks.construct(callbacks.context, pool, start + index, at(pool, index));
                    ++constructed[pool];
                }
            }
        }
    };
    struct State {
        mutable std::shared_mutex mutex;
        Callbacks callbacks;
        std::array<std::size_t, pool_count> strides;
        std::size_t block_size;
        std::vector<std::unique_ptr<Block>> blocks;
        std::uint64_t capacity = 0;
        std::array<std::uint64_t, domain_count> live{};
        std::array<std::uint64_t, domain_count> highwater{};
        std::array<std::uint64_t, domain_count> free_head{no_slot, no_slot};
        // Declared after blocks, but State's destructor explicitly clears blocks
        // before this owner is released. Borrowed elements belong to this owner.
        std::shared_ptr<void> borrowed_owner;
        bool closed = false;
        State(Callbacks cb, std::array<std::size_t, pool_count> widths, std::size_t size)
            : callbacks(cb), strides(widths), block_size(size) {}
        ~State() noexcept { while (!blocks.empty()) blocks.pop_back(); }
        Slot& slot(AgentDomain domain, std::uint64_t index) const noexcept {
            return blocks[static_cast<std::size_t>(index / block_size)]
                ->slots[ordinal(domain)][static_cast<std::size_t>(index % block_size)];
        }
        bool valid(DomainHandle handle) const noexcept {
            return known(handle.domain) && !closed && handle.index < capacity &&
                   slot(handle.domain, handle.index).occupied &&
                   slot(handle.domain, handle.index).generation == handle.generation;
        }
        void mark(AgentDomain domain, std::uint64_t index, bool occupied) noexcept {
            auto& block = *blocks[static_cast<std::size_t>(index / block_size)];
            const auto local = static_cast<std::size_t>(index % block_size);
            auto& word = block.occupied_bits[ordinal(domain)][local / 64];
            const auto mask = std::uint64_t{1} << (local % 64);
            if (occupied) word |= mask; else word &= ~mask;
        }
        std::uint64_t next(AgentDomain domain, std::uint64_t from) const noexcept {
            if (closed || from >= capacity) return no_slot;
            auto block_index = static_cast<std::size_t>(from / block_size);
            auto local = static_cast<std::size_t>(from % block_size);
            while (block_index < blocks.size()) {
                const auto& block = *blocks[block_index];
                const auto* bits = block.occupied_bits[ordinal(domain)].get();
                auto word_index = local / 64;
                auto word = bits[word_index] & (~std::uint64_t{0} << (local % 64));
                const auto words = block_size / 64 + (block_size % 64 != 0);
                while (true) {
                    if (word) return block.start + word_index * 64 + std::countr_zero(word);
                    if (++word_index == words) break;
                    word = bits[word_index];
                }
                ++block_index; local = 0;
            }
            return no_slot;
        }
        std::uint64_t previous(AgentDomain domain, std::uint64_t before) const noexcept {
            if (closed || before == 0 || capacity == 0) return no_slot;
            if (before > capacity) before = capacity;
            auto block_index = static_cast<std::size_t>((before - 1) / block_size);
            auto local = static_cast<std::size_t>((before - 1) % block_size);
            while (true) {
                const auto& block = *blocks[block_index];
                const auto* bits = block.occupied_bits[ordinal(domain)].get();
                auto word_index = local / 64;
                auto word = bits[word_index] & (~std::uint64_t{0} >> (63 - local % 64));
                while (true) {
                    if (word) return block.start + word_index * 64 + 63 - std::countl_zero(word);
                    if (word_index == 0) break;
                    word = bits[--word_index];
                }
                if (block_index == 0) return no_slot;
                --block_index; local = block_size - 1;
            }
        }
    };
    std::shared_ptr<State> state_;

public:
    class ReadLease {
        friend class AgentDomainStorage;
        std::shared_ptr<State> state_;
        std::shared_lock<std::shared_mutex> lock_;
        explicit ReadLease(std::shared_ptr<State> state)
            : state_(std::move(state)), lock_(state_->mutex) {}
    public:
        ReadLease(ReadLease&&) noexcept = default;
        ReadLease(const ReadLease&) = delete;
        ReadLease& operator=(const ReadLease&) = delete;
        std::uint64_t capacity() const noexcept { return state_->capacity; }
        std::uint64_t live(AgentDomain domain) const {
            if (!known(domain)) throw std::invalid_argument("Unknown agent domain");
            return state_->live[ordinal(domain)];
        }
        std::uint64_t highwater(AgentDomain domain) const {
            if (!known(domain)) throw std::invalid_argument("Unknown agent domain");
            return state_->highwater[ordinal(domain)];
        }
        // Logical iteration crosses segments without pointer arithmetic or
        // signed16 successor sentinels. Keep this lease for the entire walk.
        // next is inclusive; previous is exclusive. no_slot marks completion.
        std::uint64_t next(AgentDomain domain, std::uint64_t from = 0) const {
            if (!known(domain)) throw std::invalid_argument("Unknown agent domain");
            return state_->next(domain, from);
        }
        std::uint64_t previous(AgentDomain domain, std::uint64_t before) const {
            if (!known(domain)) throw std::invalid_argument("Unknown agent domain");
            return state_->previous(domain, before);
        }
        // Native header scans must also inspect unoccupied physical records.
        // This provides storage, not proof of an active Agent or domain mapping.
        void* physical(std::size_t pool, std::uint64_t index) const {
            if (pool >= pool_count || state_->closed || index >= state_->capacity)
                throw std::out_of_range("Invalid physical pool slot");
            return state_->blocks[static_cast<std::size_t>(index / state_->block_size)]
                ->at(pool, static_cast<std::size_t>(index % state_->block_size));
        }
        bool valid(DomainHandle handle) const noexcept { return state_->valid(handle); }
        // Index-only compatibility identifies the current occupant; it does not
        // detect stale integer IDs. Retain DomainHandle for generation checks.
        DomainHandle current(AgentDomain domain, std::uint64_t index) const noexcept {
            if (!known(domain) || state_->closed || index >= state_->capacity ||
                !state_->slot(domain, index).occupied) return {domain, no_slot, 0};
            return {domain, index, state_->slot(domain, index).generation};
        }
        void* get(DomainHandle handle, std::size_t pool) const {
            if (!permits(handle.domain, pool) || !valid(handle))
                throw std::out_of_range("Invalid domain handle or cross-domain pool access");
            return state_->blocks[static_cast<std::size_t>(handle.index / state_->block_size)]
                ->at(pool, static_cast<std::size_t>(handle.index % state_->block_size));
        }
    };

    AgentDomainStorage(std::size_t block_size,
                       std::array<std::size_t, pool_count> strides, Callbacks callbacks) {
        if (!block_size || !callbacks.allocate || !callbacks.release ||
            !callbacks.construct || !callbacks.destroy)
            throw std::invalid_argument("Incomplete storage configuration");
        for (auto stride : strides)
            if (!stride || block_size > std::numeric_limits<std::size_t>::max() / stride)
                throw std::invalid_argument("Storage byte size overflow");
        state_ = std::make_shared<State>(callbacks, strides, block_size);
    }
    AgentDomainStorage(const AgentDomainStorage&) = delete;
    AgentDomainStorage& operator=(const AgentDomainStorage&) = delete;
    ReadLease read() const { return ReadLease(state_); }

    // Attach an already-constructed first block without moving any element.
    // Caller must freeze native allocation/removal while taking the snapshots
    // and attaching; no engine synchronization or metadata decoding is implied.
    // The owner must keep ALL five arrays alive through every remaining lease.
    // Free indices are rebuilt in ascending order; original free-list ordering
    // is intentionally not imported. A complete allocator replacement is needed.
    void adopt_first_block(std::array<void*, pool_count> payload,
                           std::shared_ptr<void> owner,
                           const std::array<std::vector<std::uint64_t>, domain_count>& occupied) {
        auto& state = *state_;
        std::unique_lock lock(state.mutex);
        if (state.closed || state.capacity != 0)
            throw std::logic_error("Adoption requires open empty storage");
        if (!owner) throw std::invalid_argument("Borrowed pools need a lifetime owner");
        for (std::size_t pool = 0; pool < pool_count; ++pool) {
            if (!payload[pool]) throw std::invalid_argument("Null borrowed pool");
            const auto begin = reinterpret_cast<std::uintptr_t>(payload[pool]);
            const auto bytes = state.block_size * state.strides[pool];
            if (begin > std::numeric_limits<std::uintptr_t>::max() - bytes)
                throw std::invalid_argument("Borrowed pool address overflow");
            for (std::size_t prior = 0; prior < pool; ++prior) {
                const auto other = reinterpret_cast<std::uintptr_t>(payload[prior]);
                if (begin < other + state.block_size * state.strides[prior] && other < begin + bytes)
                    throw std::invalid_argument("Overlapping borrowed pools");
            }
        }
        auto block = std::make_unique<Block>(state.callbacks, state.strides, 0, state.block_size);
        block->owns_payload = false;
        block->payload = payload;
        std::array<std::uint64_t, domain_count> heads{no_slot, no_slot};
        std::array<std::uint64_t, domain_count> high{};
        for (std::size_t domain = 0; domain < domain_count; ++domain) {
            for (auto index : occupied[domain]) {
                if (index >= state.block_size || block->slots[domain][static_cast<std::size_t>(index)].occupied)
                    throw std::invalid_argument("Invalid or repeated adopted live index");
                block->slots[domain][static_cast<std::size_t>(index)].occupied = true;
                block->occupied_bits[domain][static_cast<std::size_t>(index / 64)] |= std::uint64_t{1} << (index % 64);
                if (index >= high[domain]) high[domain] = index + 1;
            }
            for (std::size_t index = state.block_size; index-- > 0;) {
                auto& slot = block->slots[domain][index];
                if (!slot.occupied) { slot.next = heads[domain]; heads[domain] = index; }
            }
        }
        state.blocks.reserve(1);
        state.blocks.push_back(std::move(block));
        state.borrowed_owner = std::move(owner);
        state.free_head = heads;
        state.highwater = high;
        for (std::size_t domain = 0; domain < domain_count; ++domain)
            state.live[domain] = occupied[domain].size();
        state.capacity = state.block_size;
    }

    // All five payloads, both ledgers and the common capacity publish together.
    // Callbacks must be nonreentrant and correctly aligned; a throwing constructor
    // owns cleanup of its own incomplete object. Completed objects unwind here.
    void grow(std::uint64_t minimum_capacity) {
        auto& state = *state_;
        std::unique_lock lock(state.mutex);
        if (state.closed) throw std::logic_error("Storage is closed");
        if (minimum_capacity <= state.capacity) return;
        const auto size = static_cast<std::uint64_t>(state.block_size);
        if (minimum_capacity > no_slot - size) throw std::length_error("Storage index overflow");
        const auto requested_blocks = (minimum_capacity + size - 1) / size;
        if (requested_blocks > state.blocks.max_size()) throw std::length_error("Storage directory overflow");
        const auto new_capacity = requested_blocks * size;
        const auto additional = static_cast<std::size_t>(requested_blocks) - state.blocks.size();
        std::vector<std::unique_ptr<Block>> pending;
        pending.reserve(additional);
        state.blocks.reserve(static_cast<std::size_t>(requested_blocks));
        try {
            for (std::size_t offset = 0; offset < additional; ++offset) {
                auto block = std::make_unique<Block>(state.callbacks, state.strides,
                    state.capacity + static_cast<std::uint64_t>(offset) * size, state.block_size);
                block->initialize();
                for (std::size_t domain = 0; domain < domain_count; ++domain) {
                    for (std::size_t index = 0; index < state.block_size; ++index) {
                        const auto absolute = block->start + index;
                        block->slots[domain][index].next = absolute + 1 == new_capacity
                            ? state.free_head[domain] : absolute + 1;
                    }
                }
                pending.push_back(std::move(block));
            }
        } catch (...) {
            while (!pending.empty()) pending.pop_back();
            throw;
        }
        for (auto& block : pending) state.blocks.push_back(std::move(block));
        state.free_head.fill(state.capacity);
        state.capacity = new_capacity;
    }

    DomainHandle acquire(AgentDomain domain) {
        if (!known(domain)) throw std::invalid_argument("Unknown agent domain");
        auto& state = *state_;
        std::unique_lock lock(state.mutex);
        if (state.closed) throw std::logic_error("Storage is closed");
        auto& head = state.free_head[ordinal(domain)];
        if (head == no_slot) return {domain, no_slot, 0};
        const auto index = head;
        auto& slot = state.slot(domain, index);
        head = slot.next;
        slot.next = no_slot;
        slot.occupied = true;
        state.mark(domain, index, true);
        ++state.live[ordinal(domain)];
        if (index >= state.highwater[ordinal(domain)]) state.highwater[ordinal(domain)] = index + 1;
        return {domain, index, slot.generation};
    }
    DomainHandle acquire_at(AgentDomain domain, std::uint64_t index) {
        if (!known(domain)) throw std::invalid_argument("Unknown agent domain");
        auto& state = *state_;
        std::unique_lock lock(state.mutex);
        if (state.closed) throw std::logic_error("Storage is closed");
        if (index >= state.capacity || state.slot(domain, index).occupied)
            return {domain, no_slot, 0};
        auto* link = &state.free_head[ordinal(domain)];
        while (*link != no_slot && *link != index) link = &state.slot(domain, *link).next;
        if (*link == no_slot) return {domain, no_slot, 0}; // retired generation
        auto& slot = state.slot(domain, index);
        *link = slot.next;
        slot.next = no_slot;
        slot.occupied = true;
        state.mark(domain, index, true);
        ++state.live[ordinal(domain)];
        if (index >= state.highwater[ordinal(domain)]) state.highwater[ordinal(domain)] = index + 1;
        return {domain, index, slot.generation};
    }
    bool release(DomainHandle handle) {
        auto& state = *state_;
        std::unique_lock lock(state.mutex);
        if (!state.valid(handle)) return false;
        auto& slot = state.slot(handle.domain, handle.index);
        slot.occupied = false;
        state.mark(handle.domain, handle.index, false);
        --state.live[ordinal(handle.domain)];
        auto& high = state.highwater[ordinal(handle.domain)];
        if (handle.index + 1 == high) {
            const auto last = state.previous(handle.domain, high);
            high = last == no_slot ? 0 : last + 1;
        }
        if (slot.generation != no_slot) {
            ++slot.generation;
            slot.next = state.free_head[ordinal(handle.domain)];
            state.free_head[ordinal(handle.domain)] = handle.index;
        }
        return true;
    }

    // No read lease may be held by this calling thread. Leases retain State and
    // callback-context lifetime must cover them. Returned pointers are protected
    // only for the lease/barrier duration; engine jobs need an external barrier
    // covering their entire access, not just a short resolver call.
    bool try_close() {
        auto& state = *state_;
        std::unique_lock lock(state.mutex, std::try_to_lock);
        if (!lock.owns_lock()) return false;
        if (state.closed) return true;
        state.closed = true;
        state.capacity = 0;
        state.live.fill(0);
        state.highwater.fill(0);
        state.free_head.fill(no_slot);
        while (!state.blocks.empty()) state.blocks.pop_back();
        state.borrowed_owner.reset();
        return true;
    }
};
}
