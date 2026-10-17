#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include "../../src/MNBL.AgentStorage/NativeElementFactory.hpp"
#include "../../build/agent-storage/native_element_machine_code.hpp"
#include <algorithm>
#include <iostream>

using namespace mnbl::storage;
namespace {
void require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }
constexpr std::uintptr_t image_base = 0x180000000ULL;
class CodeSlice {
    void* memory_ = nullptr;
    bool registered_ = false;
    RUNTIME_FUNCTION row_{0x5e58e0, 0x5e5905, 0xb87098};
public:
    CodeSlice() {
        memory_ = VirtualAlloc(reinterpret_cast<void*>(image_base), 0xc00000, MEM_RESERVE, PAGE_NOACCESS);
        require(memory_ == reinterpret_cast<void*>(image_base), "Native slice address unavailable");
        try {
            for (auto rva : native_elements_fixture::pages)
                require(VirtualAlloc(reinterpret_cast<void*>(image_base + rva), 4096, MEM_COMMIT, PAGE_READWRITE) != nullptr, "Commit slice page");
            for (auto span : native_elements_fixture::spans)
                std::memcpy(reinterpret_cast<void*>(image_base + span.rva), span.bytes, span.size);
            for (auto rva : native_elements_fixture::pages) {
                const auto code = rva == 0x5d8000 || rva == 0x5e5000 || rva == 0x694000;
                DWORD old = 0;
                require(VirtualProtect(reinterpret_cast<void*>(image_base+rva), 4096, code ? PAGE_EXECUTE_READ : PAGE_READONLY, &old) != 0, "Protect slice page");
            }
            require(FlushInstructionCache(GetCurrentProcess(), memory_, 0xc00000) != 0, "Flush slice instructions");
            registered_ = RtlAddFunctionTable(&row_, 1, image_base) != FALSE;
            require(registered_, "Register original companion unwind");
            DWORD64 base = 0;
            require(RtlLookupFunctionEntry(image_base+row_.BeginAddress, &base, nullptr) == &row_ && base == image_base, "Read back original unwind");
        } catch (...) {
            if (registered_) RtlDeleteFunctionTable(&row_);
            VirtualFree(memory_, 0, MEM_RELEASE); memory_ = nullptr;
            throw;
        }
    }
    ~CodeSlice() { if (registered_) RtlDeleteFunctionTable(&row_); if (memory_) VirtualFree(memory_, 0, MEM_RELEASE); }
};

struct FakeAgentState {
    std::vector<void*> live;
    std::size_t attempts = 0, fail_at = SIZE_MAX, completed = 0, destroyed = 0;
    std::size_t native230 = 0, native2c8 = 0, partial_cleanup = 0;
    unsigned char poison = 0xa5;
    bool fail_partial = false;
    void before(void* pointer, std::size_t size) {
        if (attempts++ == fail_at) throw std::runtime_error("Injected constructor failure");
        std::memset(pointer, poison, size);
    }
};
FakeAgentState* current = nullptr;
void* agent_ctor(void* pointer) {
    auto& s = *current; s.before(pointer, 0xc60);
    auto owned = std::make_unique<unsigned>(0x12345678);
    if (s.fail_partial) { ++s.partial_cleanup; throw std::runtime_error("Partially constructed fake Agent"); }
    s.live.push_back(pointer); // May throw; unique_ptr still owns partial member.
    auto raw = owned.release();
    std::memcpy(pointer, &pointer, sizeof(pointer));
    std::memcpy(static_cast<std::byte*>(pointer)+8, &raw, sizeof(raw));
    auto interior = static_cast<std::byte*>(pointer)+0x150;
    std::memcpy(static_cast<std::byte*>(pointer)+0x148, &interior, sizeof(interior));
    ++s.completed;
    return pointer;
}
void agent_dtor(void* pointer) {
    auto& s = *current;
    require(!s.live.empty() && s.live.back() == pointer, "Reverse fake Agent destruction");
    void* self = nullptr; void* interior = nullptr; unsigned* owned = nullptr;
    std::memcpy(&self, pointer, sizeof(self));
    std::memcpy(&owned, static_cast<std::byte*>(pointer)+8, sizeof(owned));
    std::memcpy(&interior, static_cast<std::byte*>(pointer)+0x148, sizeof(interior));
    require(self == pointer && interior == static_cast<std::byte*>(pointer)+0x150 && *owned == 0x12345678, "Stable fake Agent interior/member");
    delete owned; s.live.pop_back(); ++s.destroyed;
}
void* companion230_ctor(void* pointer) {
    current->before(pointer, 0x230);
    auto result = reinterpret_cast<NativeElementFactory::Constructor>(image_base+0x5e58e0)(pointer);
    require(result == pointer, "Native230 returned this"); ++current->native230; return result;
}
void* companion2c8_ctor(void* pointer) {
    current->before(pointer, 0x2c8);
    auto result = reinterpret_cast<NativeElementFactory::Constructor>(image_base+0x5d8860)(pointer);
    require(result == pointer, "Native2c8 returned this"); ++current->native2c8; return result;
}
NativeElementFactory::Bindings bindings(std::shared_ptr<void> owner) {
    return {agent_ctor, agent_dtor, companion230_ctor, companion2c8_ctor, std::move(owner)};
}
void check_headers_and_companions(AgentDomainStorage& storage, unsigned char poison) {
    auto lease = storage.read();
    for (std::uint64_t i = 0; i < lease.capacity(); ++i) {
        for (std::size_t pool=0; pool<5; ++pool) {
            auto p=static_cast<std::byte*>(lease.physical(pool,i));
            require(reinterpret_cast<std::uintptr_t>(p) % (pool<3 ? 16 : 8) == 0, "Element alignment");
            std::int32_t header=0; std::memcpy(&header,p+NativeElementFactory::headers[pool],4);
            require(header == -1, "Inactive native header");
            if (pool>=2) {
                auto expected = pool==2
                    ? (poison==0xa5 ? native_elements_fixture::expected2 : native_elements_fixture::expected3)
                    : (poison==0xa5 ? native_elements_fixture::expected0 : native_elements_fixture::expected1);
                require(std::memcmp(p,expected,NativeElementFactory::strides[pool])==0, "Whole companion output including untouched bytes");
            }
        }
    }
}
void failures(const std::shared_ptr<void>& owner) {
    // Every constructor position in a three-block transaction, including each
    // of the five pool boundaries. Real companion code runs before later failures.
    for (std::size_t fail=0; fail<75; ++fail) {
        FakeAgentState fixture; current=&fixture;
        NativeElementFactory factory(bindings(owner));
        AgentDomainStorage storage(5,NativeElementFactory::strides,factory.callbacks());
        storage.grow(5);
        void* old=nullptr; { auto lease=storage.read(); old=lease.physical(0,0); }
        fixture.fail_at=fixture.attempts+fail;
        bool failed=false; try { storage.grow(20); } catch (const std::runtime_error&) { failed=true; }
        require(failed && fixture.live.size()==10, "Rollback left only original fake Agents");
        { auto lease=storage.read(); require(lease.capacity()==5 && lease.physical(0,0)==old, "Failed growth publication"); }
        fixture.fail_at=SIZE_MAX; storage.grow(20); check_headers_and_companions(storage,0xa5);
        require(storage.try_close(), "Close recovered storage");
        require(fixture.live.empty() && fixture.completed==fixture.destroyed, "Completed fake Agents cleaned once");
    }
    FakeAgentState fixture; current=&fixture;
    NativeElementFactory factory(bindings(owner));
    AgentDomainStorage storage(5,NativeElementFactory::strides,factory.callbacks());
    fixture.fail_partial=true;
    bool failed=false; try { storage.grow(5); } catch (const std::runtime_error&) { failed=true; }
    require(failed && fixture.partial_cleanup==1 && fixture.destroyed==0 && fixture.live.empty(), "No destructor on throwing partial object");
    { auto lease=storage.read(); require(lease.capacity()==0, "Partial constructor published capacity"); }
}
void growth(const std::shared_ptr<void>& owner) {
    for (auto poison : {0xa5,0x5a}) {
        FakeAgentState fixture; fixture.poison=static_cast<unsigned char>(poison); current=&fixture;
        NativeElementFactory factory(bindings(owner));
        AgentDomainStorage storage(2040,NativeElementFactory::strides,factory.callbacks());
        storage.grow(2040);
        void* first=nullptr; { auto lease=storage.read(); first=lease.physical(0,0); }
        for (auto target : {4080ULL,6120ULL}) {
            storage.grow(target);
            { auto lease=storage.read(); require(lease.physical(0,0)==first, "Real initializer growth moved old fake Agent"); }
        }
        check_headers_and_companions(storage,fixture.poison);
        // Metadata can reach the suffix; these handles do not activate agents.
        auto high=storage.acquire_at(AgentDomain::Primary,6119);
        require(high.index==6119 && storage.release(high), "Suffix ledger allocation/release");
        require(storage.try_close() && fixture.live.empty() && fixture.destroyed==12240, "Grown factory teardown");
        require(fixture.native230==6120 && fixture.native2c8==12240, "Executed original companion constructor counts");
    }
}
void invalid_bindings(const std::shared_ptr<void>& owner) {
    for (unsigned i=0;i<5;++i) {
        auto b=bindings(owner);
        if(i==0)b.agent_constructor=nullptr;
        if(i==1)b.agent_destructor=nullptr;
        if(i==2)b.companion230_constructor=nullptr;
        if(i==3)b.companion2c8_constructor=nullptr;
        if(i==4)b.runtime_owner.reset();
        bool rejected=false; try { NativeElementFactory factory(std::move(b)); } catch(const std::invalid_argument&) { rejected=true; }
        require(rejected,"Incomplete binding accepted");
    }
}
}
int main() {
    try {
        auto owner=std::make_shared<CodeSlice>();
        invalid_bindings(owner); failures(owner); growth(owner);
        std::cout << "PASS native element factory: original companion initializers;2 poison patterns;2040->4080->6120;75 failure positions;partial constructor cleanup;5 invalid bindings;stable addresses;teardown. Agent ctor/dtor synthetic.\n";
        return 0;
    } catch(const std::exception& e) { std::cerr << e.what() << '\n'; return 1; }
}
