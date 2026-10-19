#define main domain_baseline_main
#include "agent_domain_storage_tests.cpp"
#undef main

namespace {
struct Borrowed {
    std::array<void*, 5> payload{};
    std::size_t count;
    std::size_t* disposed;
    Borrowed(std::size_t size, std::size_t& destroyed) : count(size), disposed(&destroyed) {
        try {
            for (std::size_t pool = 0; pool < 5; ++pool) {
                payload[pool] = std::malloc(count * widths[pool]);
                if (!payload[pool]) throw std::bad_alloc();
                for (std::size_t index = 0; index < count; ++index) {
                    auto p = static_cast<std::byte*>(payload[pool]) + index * widths[pool];
                    new(p) Object{p, index, pool, index ^ pool ^ 0x123456789ULL};
                }
            }
        } catch (...) { for (auto p : payload) std::free(p); throw; }
    }
    ~Borrowed() { for (auto p : payload) std::free(p); ++*disposed; }
};

void adoption_and_growth() {
    std::size_t disposed = 0;
    auto original = std::make_shared<Borrowed>(2040, disposed);
    auto pointers = original->payload;
    Fixture fresh;
    auto storage = std::make_unique<AgentDomainStorage>(2040, widths, fresh.callbacks());
    storage->adopt_first_block(pointers, original, {{{0, 3, 2039}, {1, 5}}});
    original.reset();
    require(fresh.allocation_calls == 0 && fresh.construction_calls == 0 && disposed == 0,
            "Adoption constructed or released original objects");
    std::array<DomainHandle, 2> first;
    { auto lease = storage->read();
      require(lease.capacity() == 2040 && lease.live(primary) == 3 && lease.live(secondary) == 2,
              "Adopted occupancy wrong");
      require(lease.highwater(primary) == 2040 && lease.highwater(secondary) == 6, "Adopted highwater wrong");
      first = {lease.current(primary, 0), lease.current(secondary, 1)};
      for (std::size_t pool = 0; pool < 5; ++pool)
        for (auto index : {0U, 1U, 3U, 2039U}) {
            auto expected = static_cast<std::byte*>(pointers[pool]) + index * widths[pool];
            require(lease.physical(pool, index) == expected, "Adoption moved original pointer");
            check_object(lease.physical(pool, index), index, pool);
        }
      require(lease.current(primary, 1).index == no_slot && lease.physical(0, 1), "Inactive scan inaccessible"); }
    require(storage->acquire(primary).index == 1 && storage->acquire(secondary).index == 0,
            "Adopted holes merged across domains");
    fresh.fail_construction = fresh.construction_calls + 17;
    bool failed = false; try { storage->grow(6120); } catch (const std::runtime_error&) { failed = true; }
    require(failed && fresh.allocations.empty() && fresh.constructed.empty() && fresh.reverse_ok,
            "Failed suffix leaked or touched borrowed block");
    { auto lease = storage->read();
      require(lease.capacity() == 2040 && lease.valid(first[0]) && lease.valid(first[1]) &&
              lease.physical(0, 0) == pointers[0], "Failed adoption growth damaged original state"); }
    fresh.fail_construction = SIZE_MAX;
    for (auto target : {4080ULL, 6120ULL}) {
        storage->grow(target);
        auto lease = storage->read();
        for (std::size_t pool = 0; pool < 5; ++pool) {
            require(lease.physical(pool, 0) == pointers[pool], "Growth moved inline array");
            check_object(lease.physical(pool, target - 1), target - 1, pool);
        }
    }
    auto p = storage->acquire_at(primary, 5000), q = storage->acquire_at(secondary, 5000);
    require(p.index == 5000 && q.index == 5000 && storage->acquire_at(primary, 5000).index == no_slot &&
            storage->acquire_at(primary, 6120).index == no_slot && storage->acquire_at(primary, no_slot).index == no_slot,
            "Explicit index admission wrong");
    { auto lease = storage->read(); require(lease.highwater(primary) == 5001 && lease.highwater(secondary) == 5001,
                                           "Explicit index highwater wrong"); }
    require(storage->release(p), "Explicit release failed");
    { auto lease = storage->read(); require(lease.highwater(primary) == 2040 && lease.highwater(secondary) == 5001 && lease.valid(q),
                                           "Highwater shrink crossed domains"); }
    require(storage->acquire_at(primary, 5000).generation == p.generation + 1, "Explicit reuse lost generation");
    std::vector<bool> seen(6120);
    { auto lease = storage->read();
      for (std::size_t i = 0; i < seen.size(); ++i) seen[i] = lease.current(primary, i).index != no_slot; }
    while (true) {
        auto h = storage->acquire(primary); if (h.index == no_slot) break;
        require(h.index < seen.size() && !seen[static_cast<std::size_t>(h.index)], "Explicit unlink corrupted free list");
        seen[static_cast<std::size_t>(h.index)] = true;
    }
    for (bool occupied : seen) require(occupied, "Explicit allocation lost a free slot");
    auto held = std::make_unique<AgentDomainStorage::ReadLease>(storage->read());
    storage.reset();
    require(disposed == 0 && !fresh.allocations.empty(), "Teardown bypassed lease lifetime");
    check_object(held->physical(0, 0), 0, 0); check_object(held->get(q, 1), 5000, 1);
    held.reset();
    require(disposed == 1, "Borrowed owner not released exactly once"); fresh.clean();
}

void adoption_rejections_and_close() {
    std::size_t disposed = 0;
    auto owner = std::make_shared<Borrowed>(3, disposed);
    Fixture f;
    AgentDomainStorage s(3, widths, f.callbacks());
    auto rejects = [&](auto pointers, auto lifetime, const auto& occupancy) {
        bool rejected = false;
        try { s.adopt_first_block(pointers, lifetime, occupancy); }
        catch (const std::invalid_argument&) { rejected = true; }
        require(rejected && s.read().capacity() == 0 && f.allocations.empty(), "Invalid adoption published state");
    };
    const std::array<std::vector<std::uint64_t>, 2> empty{};
    rejects(owner->payload, std::shared_ptr<void>{}, empty);
    auto pointers = owner->payload; pointers[3] = nullptr; rejects(pointers, owner, empty);
    pointers = owner->payload; pointers[1] = pointers[0]; rejects(pointers, owner, empty);
    pointers = owner->payload; pointers[4] = reinterpret_cast<void*>(UINTPTR_MAX - 1); rejects(pointers, owner, empty);
    rejects(owner->payload, owner, std::array<std::vector<std::uint64_t>, 2>{{{0, 0}, {}}});
    rejects(owner->payload, owner, std::array<std::vector<std::uint64_t>, 2>{{{}, {3}}});
    s.adopt_first_block(owner->payload, owner, {{{0}, {2}}});
    bool rejected = false;
    try { s.adopt_first_block(owner->payload, owner, empty); } catch (const std::logic_error&) { rejected = true; }
    require(rejected, "Double adoption accepted");
    { auto lease = s.read();
      for (auto bad : {std::pair<std::size_t, std::uint64_t>{5, 0}, {0, 3}, {0, no_slot}}) {
          bool caught = false; try { lease.physical(bad.first, bad.second); } catch (const std::out_of_range&) { caught = true; }
          require(caught, "Invalid physical address exposed");
      }
      auto highest = lease.current(secondary, 2);
      require(highest.index == 2, "Adopted secondary missing"); }
    s.grow(6); owner.reset();
    require(s.try_close() && disposed == 1, "Close failed to release borrowed owner"); f.clean();
    { auto lease = s.read();
      require(lease.highwater(primary) == 0 && lease.highwater(secondary) == 0, "Closed highwater remained");
      bool caught = false; try { lease.physical(0, 0); } catch (const std::out_of_range&) { caught = true; }
      require(caught, "Closed physical address exposed"); }
    bool caught = false; try { s.acquire_at(primary, 0); } catch (const std::logic_error&) { caught = true; }
    require(caught, "Closed explicit allocation accepted");
}
void adopted_fault_matrix() {
    for (int mode = 0; mode < 3; ++mode) {
        const std::size_t count = mode == 2 ? 45 : 15;
        for (std::size_t point = 0; point < count; ++point) {
            std::size_t disposed = 0;
            auto owner = std::make_shared<Borrowed>(3, disposed);
            Fixture f;
            AgentDomainStorage s(3, widths, f.callbacks());
            s.adopt_first_block(owner->payload, owner, {{{0, 2}, {1}}});
            auto pointers = owner->payload; owner.reset();
            if (mode == 2) f.fail_construction = point;
            else { f.fail_allocation = point; f.throw_allocation = mode == 1; }
            bool failed = false; try { s.grow(12); } catch (const std::exception&) { failed = true; }
            require(failed && disposed == 0 && f.allocations.empty() && f.constructed.empty() && f.reverse_ok,
                    "Fault damaged borrowed ownership or leaked suffix");
            { auto lease = s.read();
              require(lease.capacity() == 3 && lease.live(primary) == 2 && lease.live(secondary) == 1 &&
                      lease.highwater(primary) == 3 && lease.highwater(secondary) == 2, "Fault published partial metadata");
              for (std::size_t pool = 0; pool < 5; ++pool) {
                  require(lease.physical(pool, 0) == pointers[pool], "Fault moved borrowed base");
                  check_object(lease.physical(pool, 2), 2, pool);
              } }
            require(s.acquire_at(primary, 1).index == 1 && s.acquire(primary).index == no_slot &&
                    s.acquire_at(secondary, 2).index == 2 && s.acquire(secondary).index == 0,
                    "Fault damaged imported free chains");
            f.fail_allocation = f.fail_construction = SIZE_MAX;
            s.grow(12);
            require(s.acquire(primary).index == 3 && s.acquire(secondary).index == 3, "Adopted retry failed");
            require(s.try_close() && disposed == 1, "Fault retry close leaked borrowed owner"); f.clean();
        }
    }
}
}
int main() {
    if (domain_baseline_main()) return 1;
    try {
        adoption_and_growth();
        std::cout << "PASS borrowed original arrays: no relocation or reconstruction; staged growth to6120; explicit allocation, holes, exhaustion, independent highwater; lease-held teardown\n";
        adoption_rejections_and_close();
        std::cout << "PASS invalid adoption rollback, duplicate/out-of-range occupancy, overlapping/null/overflow arrays, double adoption, closed physical access\n";
        adopted_fault_matrix();
        std::cout << "PASS 75 adopted-suffix failure points: original pointers, objects, both ledgers and highwater survive; retry and teardown succeed\n";
        std::cout << "OFFLINE ADOPTION ONLY: synthetic existing records; caller must supply actual engine lifetime and occupancy snapshots\n";
        return 0;
    } catch (const std::exception& e) { std::cerr << "FAIL: " << e.what() << '\n'; return 1; }
}
