#include "../../src/MNBL.AgentStorage/StableAgentStorage.hpp"
#include <atomic>
#include <chrono>
#include <cstdlib>
#include <future>
#include <iostream>
#include <set>
#include <thread>

using namespace mnbl::storage;
namespace {
void require(bool ok, const char* message) { if (!ok) throw std::runtime_error(message); }
struct Header { void* self; std::uint64_t index; std::uint64_t pool; std::uint64_t check; };
struct Fixture {
    std::set<void*> allocations;
    std::vector<void*> constructed;
    std::size_t allocation_calls = 0, construction_calls = 0;
    std::size_t fail_allocation = SIZE_MAX, fail_construction = SIZE_MAX;
    std::size_t destroyed = 0;
    bool reverse_ok = true;
    static void* allocate(void* context, std::size_t bytes) {
        auto& f = *static_cast<Fixture*>(context);
        if (f.allocation_calls++ == f.fail_allocation) return nullptr;
        void* p = std::malloc(bytes);
        if (p) f.allocations.insert(p);
        return p;
    }
    static void release(void* context, void* p) noexcept {
        auto& f = *static_cast<Fixture*>(context);
        if (f.allocations.erase(p) != 1) std::abort();
        std::free(p);
    }
    static void construct(void* context, std::size_t pool, std::uint64_t index, void* p) {
        auto& f = *static_cast<Fixture*>(context);
        if (f.construction_calls++ == f.fail_construction) throw std::runtime_error("Injected constructor failure");
        new (p) Header{p, index, pool, index ^ (pool << 24) ^ 0x987654321ULL};
        f.constructed.push_back(p);
    }
    static void destroy(void* context, std::size_t, std::uint64_t, void* p) noexcept {
        auto& f = *static_cast<Fixture*>(context);
        if (f.constructed.empty() || f.constructed.back() != p) f.reverse_ok = false;
        if (!f.constructed.empty()) f.constructed.pop_back();
        static_cast<Header*>(p)->~Header();
        ++f.destroyed;
    }
    Callbacks callbacks() { return {this, allocate, release, construct, destroy}; }
    void clean() const { require(allocations.empty() && constructed.empty() && reverse_ok, "Teardown leak or wrong destruction order"); }
};
constexpr std::array<std::size_t, 5> widths{0xc60, 0xc60, 0x230, 0x2c8, 0x2c8};
constexpr std::array<std::size_t, 5> small{32, 32, 32, 32, 32};
void check(StableAgentStorage& storage, const std::vector<Handle>& handles,
           const std::vector<std::array<void*, 5>>& addresses) {
    auto lease = storage.read();
    for (std::size_t i = 0; i < handles.size(); ++i) {
        for (std::size_t pool = 0; pool < 5; ++pool) {
            auto* h = static_cast<Header*>(lease.get(handles[i], pool));
            require(h == addresses[i][pool] && h->self == h && h->index == handles[i].index &&
                    h->pool == pool && h->check == (h->index ^ (pool << 24) ^ 0x987654321ULL), "Moved or corrupted object");
        }
    }
}
void growth() {
    Fixture f;
    StableAgentStorage storage(2040, widths, f.callbacks());
    std::vector<Handle> handles;
    std::vector<std::array<void*, 5>> addresses;
    for (std::uint64_t target : {2040ULL, 4080ULL, 6120ULL}) {
        storage.grow(target);
        check(storage, handles, addresses);
        while (true) {
            auto h = storage.acquire();
            if (h.index == no_slot) break;
            handles.push_back(h);
            std::array<void*, 5> row{};
            auto lease = storage.read();
            for (std::size_t p = 0; p < 5; ++p) row[p] = lease.get(h, p);
            addresses.push_back(row);
        }
        require(handles.size() == target && storage.acquire().index == no_slot, "Exhaustion boundary");
    }
    for (auto i : {0U, 2039U, 2040U, 4079U, 4080U, 6119U}) {
        auto old = handles[i];
        require(storage.release(old) && !storage.release(old), "Double release accepted");
        auto current = storage.acquire();
        require(current.index == old.index && current.generation == old.generation + 1, "Slot reuse or generation");
        { auto lease = storage.read(); require(!lease.valid(old), "Stale handle accepted"); }
        handles[i] = current;
    }
    check(storage, handles, addresses);
    require(storage.try_close(), "Close failed");
    require(f.destroyed == 6120 * 5, "Incomplete destruction");
    f.clean();
}
void rollback() {
    // Fail each payload allocation in three new blocks, then every constructor.
    // One existing block and its reusable slot must survive every failed growth.
    for (int mode = 0; mode < 2; ++mode) {
        const std::size_t attempts = mode == 0 ? 15 : 45;
        for (std::size_t failure = 0; failure < attempts; ++failure) {
            Fixture f;
            StableAgentStorage storage(3, small, f.callbacks());
            storage.grow(3);
            auto old = storage.acquire();
            void* address;
            { auto lease = storage.read(); address = lease.get(old, 0); }
            if (mode == 0) f.fail_allocation = f.allocation_calls + failure;
            else f.fail_construction = f.construction_calls + failure;
            bool failed = false;
            try { storage.grow(12); } catch (const std::exception&) { failed = true; }
            require(failed, "Injected failure not reached");
            { auto lease = storage.read(); require(lease.capacity() == 3 && lease.live() == 1 && lease.get(old, 0) == address, "Failure published partial growth"); }
            require(f.allocations.size() == 5 && f.constructed.size() == 15 && f.reverse_ok, "Rollback leakage or order");
            require(storage.acquire().index == 1 && storage.acquire().index == 2 && storage.acquire().index == no_slot, "Rollback free chain corrupted");
            f.fail_allocation = f.fail_construction = SIZE_MAX;
            storage.grow(12);
            require(storage.acquire().index == 3, "Retry cannot use new storage");
            storage.try_close();
            f.clean();
        }
    }
}
void wide_indices() {
    Fixture f;
    StableAgentStorage storage(257, small, f.callbacks());
    storage.grow(66000);
    Handle last;
    for (std::uint64_t i = 0; i < 66049; ++i) {
        auto h = storage.acquire();
        require(h.index == i, "Narrow index or sentinel collision");
        last = h;
    }
    require(storage.acquire().index == no_slot, "Wide exhaustion");
    { auto lease = storage.read(); require(static_cast<Header*>(lease.get(last, 4))->index == 66048, "Wide address lookup"); }
    require(storage.release(last) && storage.acquire().index == last.index, "Wide reuse");
    bool rejected = false;
    try { storage.grow(no_slot); } catch (const std::length_error&) { rejected = true; }
    require(rejected, "Index arithmetic overflow accepted");
    storage.try_close();
    f.clean();
}
void synchronization() {
    Fixture f;
    auto storage = std::make_unique<StableAgentStorage>(3, small, f.callbacks());
    storage->grow(3);
    auto h = storage->acquire();
    auto lease = std::make_unique<StableAgentStorage::ReadLease>(storage->read());
    void* address = lease->get(h, 0);
    auto busy_close = std::async(std::launch::async, [&] { return storage->try_close(); });
    require(!busy_close.get(), "Close ignored reader");
    std::promise<void> entered;
    auto growth = std::async(std::launch::async, [&] { entered.set_value(); storage->grow(9); });
    entered.get_future().wait();
    require(growth.wait_for(std::chrono::milliseconds(30)) == std::future_status::timeout, "Growth ignored reader");
    require(lease->capacity() == 3 && lease->get(h, 0) == address, "Partial publication");
    lease.reset();
    growth.get();
    lease = std::make_unique<StableAgentStorage::ReadLease>(storage->read());
    require(lease->capacity() == 9 && lease->get(h, 0) == address, "Growth after barrier");
    storage.reset();
    require(lease->get(h, 0) == address && f.destroyed == 0, "Lease lifetime lost");
    lease.reset();
    require(f.destroyed == 45, "Deferred teardown incomplete");
    f.clean();
}
}
int main() {
    try {
        growth(); std::cout << "PASS growth/exhaustion/reuse: 2040 -> 4080 -> 6120, five pools, stable addresses\n";
        rollback(); std::cout << "PASS rollback: 15 allocation and 45 constructor failure points, reverse teardown, retry\n";
        wide_indices(); std::cout << "PASS wide indices: 66049 slots, no 16-bit sentinel collision, overflow rejected\n";
        synchronization(); std::cout << "PASS synchronization: reader barrier, atomic publication, deferred teardown\n";
        std::cout << "OFFLINE STORAGE ONLY: Bannerlord integration and live growth are not validated\n";
    } catch (const std::exception& e) { std::cerr << "FAIL: " << e.what() << '\n'; return 1; }
}
