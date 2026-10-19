#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <memory>
#include <mutex>
#include <shared_mutex>
#include <stdexcept>
#include <utility>
#include <vector>

namespace mnbl::storage {

// Storage mechanics only. These are not Bannerlord indices or engine objects.
// Engine integration must translate every indexed access and establish its own
// job barrier; an engine worker does not automatically participate in this lock.
inline constexpr std::size_t pool_count = 5;
inline constexpr std::uint64_t no_slot = std::numeric_limits<std::uint64_t>::max();

struct Handle {
    std::uint64_t index = no_slot;
    std::uint64_t generation = 0;
};

struct Callbacks {
    // Context must outlive all storage leases. Callbacks execute under the
    // storage write lock and must not re-enter this storage instance.
    void* context = nullptr;
    void* (*allocate)(void*, std::size_t) = nullptr;
    void (*release)(void*, void*) noexcept = nullptr;
    void (*construct)(void*, std::size_t, std::uint64_t, void*) = nullptr;
    void (*destroy)(void*, std::size_t, std::uint64_t, void*) noexcept = nullptr;
};

class StableAgentStorage {
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
        std::unique_ptr<Slot[]> slots;
        std::uint64_t start;
        std::size_t size;

        Block(Callbacks cb, std::array<std::size_t, pool_count> widths,
              std::uint64_t first, std::size_t count)
            : callbacks(cb), strides(widths), slots(new Slot[count]), start(first), size(count) {}

        void* at(std::size_t pool, std::size_t index) const noexcept {
            return static_cast<std::byte*>(payload[pool]) + strides[pool] * index;
        }

        // Only completed constructors are unwound; the throwing constructor
        // remains responsible for its own partially initialized object.
        ~Block() noexcept {
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
        std::uint64_t live = 0;
        std::uint64_t free_head = no_slot;
        bool closed = false;

        State(Callbacks cb, std::array<std::size_t, pool_count> widths, std::size_t size)
            : callbacks(cb), strides(widths), block_size(size) {}
        ~State() noexcept { while (!blocks.empty()) blocks.pop_back(); }

        Slot& slot(std::uint64_t index) const noexcept {
            return blocks[static_cast<std::size_t>(index / block_size)]->slots[index % block_size];
        }
        bool valid(Handle handle) const noexcept {
            return !closed && handle.index < capacity && slot(handle.index).occupied &&
                   slot(handle.index).generation == handle.generation;
        }
    };

    std::shared_ptr<State> state_;

public:
    class ReadLease {
        friend class StableAgentStorage;
        std::shared_ptr<State> state_;
        std::shared_lock<std::shared_mutex> lock_;
        explicit ReadLease(std::shared_ptr<State> state)
            : state_(std::move(state)), lock_(state_->mutex) {}
    public:
        ReadLease(ReadLease&&) noexcept = default;
        ReadLease(const ReadLease&) = delete;
        ReadLease& operator=(const ReadLease&) = delete;
        std::uint64_t capacity() const noexcept { return state_->capacity; }
        std::uint64_t live() const noexcept { return state_->live; }
        bool valid(Handle handle) const noexcept { return state_->valid(handle); }
        // Compatibility for native APIs that expose an index without generation.
        // Returns the current occupant only; callers needing stale-handle checks
        // must retain the complete Handle instead.
        Handle current(std::uint64_t index) const noexcept {
            if (state_->closed || index >= state_->capacity || !state_->slot(index).occupied) return {};
            return {index, state_->slot(index).generation};
        }
        void* get(Handle handle, std::size_t pool) const {
            if (pool >= pool_count || !valid(handle)) throw std::out_of_range("Invalid stable agent handle");
            return state_->blocks[static_cast<std::size_t>(handle.index / state_->block_size)]
                ->at(pool, static_cast<std::size_t>(handle.index % state_->block_size));
        }
    };

    StableAgentStorage(std::size_t block_size, std::array<std::size_t, pool_count> strides,
                       Callbacks callbacks) {
        if (!block_size || !callbacks.allocate || !callbacks.release ||
            !callbacks.construct || !callbacks.destroy)
            throw std::invalid_argument("Incomplete storage configuration");
        for (auto stride : strides)
            if (!stride || block_size > std::numeric_limits<std::size_t>::max() / stride)
                throw std::invalid_argument("Storage byte size overflow");
        state_ = std::make_shared<State>(callbacks, strides, block_size);
    }

    StableAgentStorage(const StableAgentStorage&) = delete;
    StableAgentStorage& operator=(const StableAgentStorage&) = delete;
    ReadLease read() const { return ReadLease(state_); }

    // Publishes all five pools and their metadata together, after every new
    // constructor succeeds. Existing objects and their addresses are untouched.
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
        // Directory movement never moves the blocks. Reserve before constructors
        // so the final publication consists solely of nonthrowing operations.
        state.blocks.reserve(static_cast<std::size_t>(requested_blocks));
        try {
          for (std::size_t offset = 0; offset < additional; ++offset) {
            auto block = std::make_unique<Block>(state.callbacks, state.strides,
                state.capacity + static_cast<std::uint64_t>(offset) * size, state.block_size);
            block->initialize();
            for (std::size_t index = 0; index < state.block_size; ++index) {
                const auto absolute = block->start + index;
                block->slots[index].next = absolute + 1 == new_capacity ? state.free_head : absolute + 1;
            }
            pending.push_back(std::move(block));
          }
        } catch (...) {
            while (!pending.empty()) pending.pop_back();
            throw;
        }
        for (auto& block : pending) state.blocks.push_back(std::move(block));
        state.free_head = state.capacity;
        state.capacity = new_capacity;
    }

    Handle acquire() {
        auto& state = *state_;
        std::unique_lock lock(state.mutex);
        if (state.closed) throw std::logic_error("Storage is closed");
        if (state.free_head == no_slot) return {};
        const auto index = state.free_head;
        auto& slot = state.slot(index);
        state.free_head = slot.next;
        slot.next = no_slot;
        slot.occupied = true;
        ++state.live;
        return {index, slot.generation};
    }

    bool release(Handle handle) {
        auto& state = *state_;
        std::unique_lock lock(state.mutex);
        if (!state.valid(handle)) return false;
        auto& slot = state.slot(handle.index);
        slot.occupied = false;
        --state.live;
        // Never wrap a generation and accidentally validate a stale handle.
        if (slot.generation != no_slot) {
            ++slot.generation;
            slot.next = state.free_head;
            state.free_head = handle.index;
        }
        return true;
    }

    // Calling thread must not hold a read lease (including for try_close).
    // Readers retain State lifetime if the owning object goes out of scope.
    bool try_close() {
        auto& state = *state_;
        std::unique_lock lock(state.mutex, std::try_to_lock);
        if (!lock.owns_lock()) return false;
        if (state.closed) return true;
        state.closed = true;
        state.capacity = state.live = 0;
        state.free_head = no_slot;
        while (!state.blocks.empty()) state.blocks.pop_back();
        return true;
    }
};
}
