#include "../../src/MNBL.AgentStorage/AgentDomainStorage.hpp"
#include <chrono>
#include <cstdlib>
#include <future>
#include <iostream>
#include <set>

using namespace mnbl::storage;
namespace {
constexpr auto primary = AgentDomain::Primary;
constexpr auto secondary = AgentDomain::Secondary;
constexpr std::array<std::size_t, 5> widths{0xc60, 0xc60, 0x230, 0x2c8, 0x2c8};
constexpr std::array<std::size_t, 5> small{32, 32, 32, 32, 32};
void require(bool ok, const char* message) { if (!ok) throw std::runtime_error(message); }
struct Object { void* self; std::uint64_t index; std::uint64_t pool; std::uint64_t check; };
struct Fixture {
    std::set<void*> allocations;
    std::vector<void*> constructed;
    std::size_t allocation_calls = 0, construction_calls = 0, destroyed = 0;
    std::size_t fail_allocation = SIZE_MAX, fail_construction = SIZE_MAX;
    bool throw_allocation = false, reverse_ok = true;
    static void* allocate(void* context, std::size_t bytes) {
        auto& f = *static_cast<Fixture*>(context);
        if (f.allocation_calls++ == f.fail_allocation) {
            if (f.throw_allocation) throw std::bad_alloc();
            return nullptr;
        }
        auto p = std::malloc(bytes);
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
        new(p) Object{p, index, pool, index ^ pool ^ 0x123456789ULL};
        f.constructed.push_back(p);
    }
    static void destroy(void* context, std::size_t, std::uint64_t, void* p) noexcept {
        auto& f = *static_cast<Fixture*>(context);
        if (f.constructed.empty() || f.constructed.back() != p) f.reverse_ok = false;
        if (!f.constructed.empty()) f.constructed.pop_back();
        static_cast<Object*>(p)->~Object(); ++f.destroyed;
    }
    Callbacks callbacks() { return {this, allocate, release, construct, destroy}; }
    void clean() const { require(allocations.empty() && constructed.empty() && reverse_ok, "Leak or wrong destruction order"); }
};
void check_object(void* pointer, std::uint64_t index, std::size_t pool) {
    auto* p = static_cast<Object*>(pointer);
    require(p->self == p && p->index == index && p->pool == pool &&
            p->check == (index ^ pool ^ 0x123456789ULL), "Object moved or corrupted");
}
void independence_and_growth() {
    Fixture f;
    AgentDomainStorage storage(2040, widths, f.callbacks());
    std::array<std::vector<DomainHandle>, 2> handles;
    std::vector<std::array<void*, 5>> addresses;
    for (auto target : {2040ULL, 4080ULL, 6120ULL}) {
        storage.grow(target);
        { auto lease = storage.read();
          for (std::size_t i = 0; i < addresses.size(); ++i)
            for (std::size_t pool = 0; pool < 5; ++pool)
                require(lease.get(handles[pool == 1 ? 1 : 0][i], pool) == addresses[i][pool], "Growth moved prior pointer"); }
        for (auto domain : {primary, secondary}) {
            auto& list = handles[static_cast<std::size_t>(domain)];
            while (true) {
                auto h = storage.acquire(domain);
                if (h.index == no_slot) break;
                require(h.domain == domain && h.index == list.size(), "Domain free chain index");
                list.push_back(h);
            }
            require(list.size() == target && storage.acquire(domain).index == no_slot, "Domain exhaustion");
            if (domain == primary) {
                auto lease = storage.read();
                require(lease.live(secondary) == handles[1].size(), "Primary exhaustion changed secondary occupancy");
            }
        }
        auto lease = storage.read();
        for (std::size_t i = addresses.size(); i < target; ++i) {
            std::array<void*, 5> row{};
            for (std::size_t pool = 0; pool < 5; ++pool) {
                row[pool] = lease.get(handles[pool == 1 ? 1 : 0][i], pool);
                check_object(row[pool], i, pool);
            }
            require(row[0] != row[1], "Independent index aliases physical object");
            addresses.push_back(row);
        }
        require(lease.live(primary) == target && lease.live(secondary) == target, "Per-domain live count");
    }
    for (auto index : {0U, 2039U, 2040U, 4079U, 4080U, 6119U}) {
        for (auto domain : {primary, secondary}) {
            const auto d = static_cast<std::size_t>(domain);
            auto old = handles[d][index]; auto other = handles[1-d][index];
            require(storage.release(old) && !storage.release(old), "Domain double release");
            { auto lease = storage.read();
              require(!lease.valid(old) && lease.valid(other), "Release invalidated opposite domain");
              require(lease.current(domain, index).index == no_slot, "Released current exposed"); }
            auto next = storage.acquire(domain);
            require(next.index == index && next.generation == old.generation + 1, "Domain generation reuse");
            { auto lease = storage.read(); require(!lease.valid(old) && lease.valid(other), "Reuse invalidated opposite domain"); }
            handles[d][index] = next;
        }
    }
    { auto lease = storage.read();
      for (std::size_t pool = 0; pool < 5; ++pool) {
        const auto wrong = handles[pool == 1 ? 0 : 1][0];
        bool rejected = false; try { lease.get(wrong, pool); } catch (const std::out_of_range&) { rejected = true; }
        require(rejected, "Cross-domain physical pool accepted");
      }
      bool rejected = false; try { lease.get(handles[0][0], 5); } catch (const std::out_of_range&) { rejected = true; }
      require(rejected, "Invalid pool accepted"); }
    require(storage.try_close() && storage.try_close(), "Close failed");
    require(f.destroyed == 6120 * 5, "Physical records destroyed per-domain instead of once");
    f.clean();
}
void rollback() {
    for (int mode = 0; mode < 3; ++mode) {
        const std::size_t count = mode == 2 ? 45 : 15;
        for (std::size_t fail = 0; fail < count; ++fail) {
            Fixture f; AgentDomainStorage s(3, small, f.callbacks()); s.grow(3);
            auto p = s.acquire(primary); auto q = s.acquire(secondary); s.acquire(secondary);
            void* p_address; void* q_address;
            { auto lease = s.read(); p_address = lease.get(p, 0); q_address = lease.get(q, 1); }
            if (mode == 2) f.fail_construction = f.construction_calls + fail;
            else { f.fail_allocation = f.allocation_calls + fail; f.throw_allocation = mode == 1; }
            bool failed = false; try { s.grow(12); } catch (const std::exception&) { failed = true; }
            require(failed, "Fault not reached");
            { auto lease = s.read();
              require(lease.capacity() == 3 && lease.live(primary) == 1 && lease.live(secondary) == 2 &&
                  lease.get(p, 0) == p_address && lease.get(q, 1) == q_address, "Failed growth published partial domain state"); }
            require(f.allocations.size() == 5 && f.constructed.size() == 15 && f.reverse_ok, "Rollback leaked or wrong order");
            require(s.acquire(primary).index == 1 && s.acquire(primary).index == 2 && s.acquire(primary).index == no_slot, "Primary chain corrupted by rollback");
            require(s.acquire(secondary).index == 2 && s.acquire(secondary).index == no_slot, "Secondary chain corrupted by rollback");
            f.fail_allocation = f.fail_construction = SIZE_MAX;
            s.grow(12);
            require(s.acquire(primary).index == 3 && s.acquire(secondary).index == 3, "Retry lost independent new heads");
            s.try_close(); f.clean();
        }
    }
}
void holes_and_wide_indices() {
    Fixture f;
    { AgentDomainStorage s(3, small, f.callbacks()); s.grow(3);
      auto p = s.acquire(primary); s.acquire(primary); s.acquire(secondary);
      s.release(p); s.grow(6);
      for (auto i : {3ULL, 4ULL, 5ULL, 0ULL, 2ULL}) require(s.acquire(primary).index == i, "Primary old holes lost");
      for (auto i : {3ULL, 4ULL, 5ULL, 1ULL, 2ULL}) require(s.acquire(secondary).index == i, "Secondary old holes lost");
      require(s.acquire(primary).index == no_slot && s.acquire(secondary).index == no_slot, "Hole chain duplication"); }
    f.clean();
    Fixture wide;
    { AgentDomainStorage s(257, small, wide.callbacks()); s.grow(66000);
      for (auto domain : {primary, secondary}) {
        DomainHandle last;
        for (std::uint64_t i = 0; i < 66049; ++i) { last = s.acquire(domain); require(last.index == i, "Narrow domain index"); }
        require(s.acquire(domain).index == no_slot, "Wide domain exhaustion");
        { auto lease = s.read(); check_object(lease.get(last, domain == primary ? 4 : 1), 66048, domain == primary ? 4 : 1); }
        require(s.release(last) && s.acquire(domain).index == 66048, "Wide domain reuse");
      }
      bool rejected = false; try { s.grow(no_slot); } catch (const std::length_error&) { rejected = true; }
      require(rejected, "Wide capacity overflow accepted"); }
    wide.clean();
}
void invalid_and_lifetime() {
    Fixture f;
    auto s = std::make_unique<AgentDomainStorage>(3, small, f.callbacks()); s->grow(3);
    auto p = s->acquire(primary), q = s->acquire(secondary);
    auto invalid = static_cast<AgentDomain>(255);
    bool rejected = false; try { s->acquire(invalid); } catch (const std::invalid_argument&) { rejected = true; }
    require(rejected && !s->release({invalid, 0, 1}), "Invalid domain accepted");
    auto lease = std::make_unique<AgentDomainStorage::ReadLease>(s->read());
    require(lease->current(invalid, 0).index == no_slot && lease->current(primary, no_slot).index == no_slot, "Invalid current accepted");
    auto close = std::async(std::launch::async, [&] { return s->try_close(); });
    require(!close.get(), "Close ignored read lease");
    std::promise<void> entered;
    auto growth = std::async(std::launch::async, [&] { entered.set_value(); s->grow(9); });
    entered.get_future().wait();
    require(growth.wait_for(std::chrono::milliseconds(30)) == std::future_status::timeout, "Growth ignored reader");
    require(lease->capacity() == 3 && lease->valid(p) && lease->valid(q), "Partial growth visible");
    lease.reset(); growth.get();
    lease = std::make_unique<AgentDomainStorage::ReadLease>(s->read());
    require(lease->capacity() == 9, "Shared growth capacity wrong");
    s.reset();
    check_object(lease->get(p, 0), 0, 0); check_object(lease->get(q, 1), 0, 1);
    require(f.destroyed == 0, "Owner destruction ignored lease");
    lease.reset(); require(f.destroyed == 45, "Deferred teardown incomplete"); f.clean();
    Fixture closed;
    { AgentDomainStorage c(3, small, closed.callbacks()); c.grow(3); auto h = c.acquire(secondary); c.try_close();
      require(!c.release(h), "Release after close");
      for (auto domain : {primary, secondary}) {
        bool caught = false; try { c.acquire(domain); } catch (const std::logic_error&) { caught = true; }
        require(caught, "Acquire after close");
        auto read = c.read(); require(read.live(domain) == 0 && read.current(domain, 0).index == no_slot, "Closed domain visible");
      }
      bool caught = false; try { c.grow(3); } catch (const std::logic_error&) { caught = true; } require(caught, "Grow after close"); }
    closed.clean();
}
}
int main() {
    try {
        independence_and_growth(); std::cout << "PASS independent domains: primary pools0,2,3,4 and secondary pool1; overlapping indices, 2040 -> 4080 -> 6120, stable five-pool addresses, cross-access rejection, 12 boundary reuses\n";
        rollback(); std::cout << "PASS atomic shared growth: 15 null allocations, 15 throwing allocations, 45 constructor failures; both old ledgers survive; reverse rollback and retry\n";
        holes_and_wide_indices(); std::cout << "PASS independent old free chains and 66049 indices per domain, beyond signed16 and unsigned16 limits; overflow rejected\n";
        invalid_and_lifetime(); std::cout << "PASS invalid domains, reader barrier, deferred five-pool teardown, closed-domain rejection\n";
        std::cout << "OFFLINE DOMAIN STORAGE ONLY: synthetic callbacks; no engine allocation, native lookup, or secondary companion relationship established\n";
        return 0;
    } catch (const std::exception& e) { std::cerr << "FAIL: " << e.what() << '\n'; return 1; }
}
