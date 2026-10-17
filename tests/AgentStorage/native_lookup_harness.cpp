#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include "../../src/MNBL.AgentStorage/StableAgentStorage.hpp"
#include "../../build/agent-storage/lookup_machine_code.hpp"
#include <cstdlib>
#include <cstring>
#include <iostream>

using namespace mnbl::storage;
namespace {
constexpr std::uintptr_t wrapper_page = 0x18070b000ULL;
constexpr std::uintptr_t entry = wrapper_page + 0x150;
constexpr std::uintptr_t stub_page = 0x181000000ULL;
constexpr std::uintptr_t global_page = 0x180da2000ULL;
constexpr std::uintptr_t global_pointer = 0x180da2a28ULL;
struct Object { void* self; std::uint64_t index; std::int32_t native_index; std::uint32_t pad; std::uint32_t managed_id; std::uint32_t check; };
static_assert(offsetof(Object, managed_id) == 0x18);
void require(bool ok, const char* what) { if (!ok) throw std::runtime_error(what); }
struct Fixture {
    std::size_t allocations=0, constructions=0, destructions=0;
    static void* allocate(void* c, std::size_t n) { auto p=std::malloc(n); if(p) ++static_cast<Fixture*>(c)->allocations; return p; }
    static void release(void* c, void* p) noexcept { --static_cast<Fixture*>(c)->allocations; std::free(p); }
    static void construct(void* c, std::size_t, std::uint64_t i, void* p) {
        new(p) Object{p,i,static_cast<std::int32_t>(i),0,100+static_cast<std::uint32_t>(i),0x12345678};
        ++static_cast<Fixture*>(c)->constructions;
    }
    static void destroy(void* c, std::size_t, std::uint64_t, void* p) noexcept { static_cast<Object*>(p)->~Object(); ++static_cast<Fixture*>(c)->destructions; }
    Callbacks callbacks(){return {this,allocate,release,construct,destroy};}
};
StableAgentStorage* active_storage=nullptr;
void* expected_mission=reinterpret_cast<void*>(0x60000000ULL);
std::uint32_t retained=0,released=0,last_retain=0,last_release=0;
extern "C" void* resolve(void* mission,std::int32_t index) noexcept {
    if (mission!=expected_mission || index<0 || !active_storage) return nullptr;
    try {
        auto lease=active_storage->read();
        auto handle=lease.current(static_cast<std::uint64_t>(index));
        return handle.index==no_slot ? nullptr : lease.get(handle,0);
    } catch (...) { return nullptr; }
}
extern "C" void retain_id(std::uint32_t id) noexcept { ++retained;last_retain=id; }
extern "C" void release_id(std::uint32_t id) noexcept { ++released;last_release=id; }
class Window {
    void* pointer_=nullptr;
public:
    explicit Window(std::uintptr_t address) {
        const auto reservation=address & ~std::uintptr_t{65535};
        pointer_=VirtualAlloc(reinterpret_cast<void*>(reservation),65536,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE);
        if(pointer_!=reinterpret_cast<void*>(reservation)) {
            if(pointer_) VirtualFree(pointer_,0,MEM_RELEASE);
            pointer_=nullptr;throw std::runtime_error("Required isolated virtual address unavailable");
        }
    }
    ~Window(){if(pointer_)VirtualFree(pointer_,0,MEM_RELEASE);}
    void executable(){DWORD old;require(VirtualProtect(pointer_,65536,PAGE_EXECUTE_READ,&old)!=0,"Protect code");require(FlushInstructionCache(GetCurrentProcess(),pointer_,65536)!=0,"Flush code");}
    Window(const Window&)=delete;Window& operator=(const Window&)=delete;
};
class UnwindTable {
    RUNTIME_FUNCTION row_{};bool registered_=false;
public:
    UnwindTable(std::uintptr_t base,DWORD begin,DWORD end,DWORD info):row_{begin,end,info}{
        registered_=RtlAddFunctionTable(&row_,1,base)!=FALSE;
        require(registered_,"Register unwind table");
        DWORD64 found_base=0;
        auto found=RtlLookupFunctionEntry(base+begin,&found_base,nullptr);
        if(found!=&row_ || found_base!=base){
            RtlDeleteFunctionTable(&row_);registered_=false;
            throw std::runtime_error("Read back unwind table");
        }
    }
    ~UnwindTable(){if(registered_)RtlDeleteFunctionTable(&row_);}
    UnwindTable(const UnwindTable&)=delete;UnwindTable& operator=(const UnwindTable&)=delete;
};
void run() {
    Window wrapper(wrapper_page),stub(stub_page),globals(global_page);
    std::memcpy(reinterpret_cast<void*>(entry),wrapper_code.data(),wrapper_code.size());
    std::memcpy(reinterpret_cast<void*>(stub_page),stub_code.data(),stub_code.size());
    std::memcpy(reinterpret_cast<void*>(stub_page+0x400),stub_unwind.data(),stub_unwind.size());
    // Real helper may be beyond rel32 reach. A near leaf relay tail-jumps to it.
    unsigned char relay[]{0x48,0xb8,0,0,0,0,0,0,0,0,0xff,0xe0};
    auto helper=reinterpret_cast<std::uintptr_t>(&resolve);
    std::memcpy(relay+2,&helper,sizeof(helper));
    std::memcpy(reinterpret_cast<void*>(stub_page+0x100),relay,sizeof(relay));
    // Harness-only original wrapper prologue: PUSH RBX (2 bytes), SUB RSP,48
    // (4 bytes). Native engine EH handlers are not loaded in this harness.
    constexpr unsigned char wrapper_unwind[]{1,6,2,0,6,0x52,2,0x30};
    std::memcpy(reinterpret_cast<void*>(wrapper_page+0x400),wrapper_unwind,sizeof(wrapper_unwind));
    UnwindTable wrapper_table(wrapper_page,0x150,0x1e0,0x400);
    UnwindTable stub_table(stub_page,0,static_cast<DWORD>(stub_code.size()),0x400);
    wrapper.executable();stub.executable();
    using Lookup=std::uint32_t(*)(void*,std::int32_t);
    auto lookup=reinterpret_cast<Lookup>(entry);
    std::array<std::uintptr_t,8> callbacks{};
    callbacks[6]=reinterpret_cast<std::uintptr_t>(&retain_id);
    callbacks[3]=reinterpret_cast<std::uintptr_t>(&release_id);
    *reinterpret_cast<std::uintptr_t*>(global_pointer)=reinterpret_cast<std::uintptr_t>(callbacks.data());
    Fixture fixture;
    {
        StableAgentStorage storage(2040,{0xc60,0xc60,0x230,0x2c8,0x2c8},fixture.callbacks());
        active_storage=&storage;
        std::vector<Handle> handles;
        std::vector<void*> addresses;
        std::size_t calls=0;
        for(std::uint64_t capacity:{2040ULL,4080ULL,6120ULL}){
            storage.grow(capacity);
            while(true){auto handle=storage.acquire();if(handle.index==no_slot)break;handles.push_back(handle);auto lease=storage.read();addresses.push_back(lease.get(handle,0));}
            require(handles.size()==capacity,"Native test exhaustion mismatch");
            for(std::size_t i=0;i<handles.size();++i){
                {auto lease=storage.read();require(lease.get(handles[i],0)==addresses[i],"Existing native pointer moved");}
                auto before=retained;
                require(lookup(expected_mission,static_cast<std::int32_t>(i))==100+i,"Native machine-code lookup mismatch");
                require(retained==before+1 && released==retained && last_retain==100+i && last_release==last_retain,"Managed handle callback sequence");
                ++calls;
            }
            require(lookup(expected_mission,-1)==0 && lookup(expected_mission,static_cast<std::int32_t>(capacity))==0,"Native exhaustion/negative guard");
        }
        for(auto index:{0U,2039U,2040U,4079U,4080U,6119U}){
            auto old=handles[index];require(storage.release(old),"Native release");
            require(lookup(expected_mission,static_cast<std::int32_t>(index))==0,"Freed native slot exposed");
            auto replacement=storage.acquire();require(replacement.index==index && replacement.generation!=old.generation,"Native slot reuse");
            require(lookup(expected_mission,static_cast<std::int32_t>(index))==100+index,"Reused native slot lookup");
            handles[index]=replacement;
        }
        *reinterpret_cast<std::uintptr_t*>(global_pointer)=0;
        auto before=retained;
        require(lookup(expected_mission,2040)==2140 && retained==before,"Unregistered callbacks");
        require(lookup(nullptr,0)==0,"Foreign mission accepted");
        storage.try_close();
        require(lookup(expected_mission,0)==0,"Closed storage exposed");
        active_storage=nullptr;
        std::cout<<"PASS real x64 wrapper + stable C++ backend: "<<calls<<" lookups during 2040 -> 4080 -> 6120 growth\n";
    }
    require(fixture.allocations==0 && fixture.constructions==fixture.destructions,"Native harness teardown leak");
    std::cout<<"PASS exhaustion, six boundary reuse cases, callback ownership, registered unwind tables, teardown\n";
    std::cout<<"ISOLATED HARNESS: synthetic records; no Bannerlord process or live-engine growth\n";
}
}
int main(){try{run();return 0;}catch(const std::exception& e){std::cerr<<"FAIL: "<<e.what()<<'\n';return 1;}}
