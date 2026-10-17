#pragma once

#include "AgentDomainStorage.hpp"
#include <cstring>
#include <malloc.h>

namespace mnbl::storage {

// Addresses must be bound to the reviewed, initialized native module by the
// caller. This class does not discover a module, install hooks or freeze jobs.
// The factory AND its runtime owner must outlive storage and every read lease.
// Constructed elements are inactive. Activation/removal remain engine actions.
class NativeElementFactory {
public:
    using Constructor = void* (*)(void*);
    using Destructor = void (*)(void*);
    struct Bindings {
        Constructor agent_constructor;       // original RVA606a20
        Destructor agent_destructor;         // original RVA6070c0, non-deleting
        Constructor companion230_constructor; // original RVA5e58e0
        Constructor companion2c8_constructor; // original RVA5d8860
        std::shared_ptr<void> runtime_owner;
    };
    inline static constexpr std::array<std::size_t, pool_count> strides{
        0xc60, 0xc60, 0x230, 0x2c8, 0x2c8};
    inline static constexpr std::array<std::size_t, pool_count> headers{
        0x10, 0x10, 0x1c, 0, 0};

private:
    Bindings bindings_;
    static void* allocate(void*, std::size_t bytes) {
        return _aligned_malloc(bytes, 16);
    }
    static void release(void*, void* pointer) noexcept {
        _aligned_free(pointer);
    }
    static void construct(void* context, std::size_t pool, std::uint64_t, void* pointer) {
        auto& self = *static_cast<NativeElementFactory*>(context);
        if (pool >= pool_count || !pointer ||
            (reinterpret_cast<std::uintptr_t>(pointer) % (pool < 3 ? 16 : 8)) != 0)
            throw std::invalid_argument("Invalid native element destination");
        const auto constructor = pool < 2 ? self.bindings_.agent_constructor
            : pool == 2 ? self.bindings_.companion230_constructor
                        : self.bindings_.companion2c8_constructor;
        constructor(pointer);
        // Exactly the reviewed caller's inactive-header write. Never zero or
        // clone the entire object: constructors establish interior pointers and
        // intentionally leave some fields untouched.
        constexpr std::int32_t inactive = -1;
        std::memcpy(static_cast<std::byte*>(pointer) + headers[pool], &inactive, sizeof(inactive));
    }
    static void destroy(void* context, std::size_t pool, std::uint64_t, void* pointer) noexcept {
        // Active objects must have completed normal engine removal before this
        // storage-level destructor runs. Unpublished default companions have no
        // resource destructor in the reviewed native lifecycle.
        if (pool < 2)
            static_cast<NativeElementFactory*>(context)->bindings_.agent_destructor(pointer);
    }

public:
    explicit NativeElementFactory(Bindings bindings) : bindings_(std::move(bindings)) {
        if (!bindings_.agent_constructor || !bindings_.agent_destructor ||
            !bindings_.companion230_constructor || !bindings_.companion2c8_constructor ||
            !bindings_.runtime_owner)
            throw std::invalid_argument("Incomplete native element bindings");
    }
    NativeElementFactory(const NativeElementFactory&) = delete;
    NativeElementFactory& operator=(const NativeElementFactory&) = delete;
    Callbacks callbacks() noexcept { return {this, allocate, release, construct, destroy}; }
};
}
