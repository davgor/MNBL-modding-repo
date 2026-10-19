#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include "../../src/MNBL.AgentStorage/AgentDomainStorage.hpp"
#include "../../build/agent-storage/write_machine_code.hpp"
#include <atomic>
#include <chrono>
#include <cstdlib>
#include <cstring>
#include <future>
#include <iostream>
#include <thread>
using namespace mnbl::storage;
namespace {
constexpr std::uintptr_t stub_address=0x181100000ULL,entry_address=stub_address+0x2000;
constexpr std::array<std::size_t,5> strides{0xc60,0xc60,0x230,0x2c8,0x2c8};
constexpr auto primary=AgentDomain::Primary,secondary=AgentDomain::Secondary;
void require(bool value,const char* message){if(!value)throw std::runtime_error(message);}
void initialize(void* p,std::size_t pool,std::uint64_t index) {
    std::memset(p,0,strides[pool]);std::memcpy(p,&p,sizeof(p));std::memcpy(static_cast<std::byte*>(p)+8,&index,8);
}
struct Fixture {
    std::size_t allocated=0,constructed=0,destroyed=0;
    static void* allocate(void* c,std::size_t bytes){auto p=std::malloc(bytes);if(p)++static_cast<Fixture*>(c)->allocated;return p;}
    static void release(void* c,void* p)noexcept{--static_cast<Fixture*>(c)->allocated;std::free(p);}
    static void construct(void* c,std::size_t pool,std::uint64_t index,void* p){initialize(p,pool,index);++static_cast<Fixture*>(c)->constructed;}
    static void destroy(void* c,std::size_t,std::uint64_t,void*)noexcept{++static_cast<Fixture*>(c)->destroyed;}
    Callbacks callbacks(){return{this,allocate,release,construct,destroy};}
};
struct Borrowed {
    std::array<void*,5> pointers{};std::atomic<unsigned>* disposed;
    explicit Borrowed(std::atomic<unsigned>& count):disposed(&count){
        try {for(std::size_t p=0;p<5;++p){pointers[p]=std::malloc(2040*strides[p]);if(!pointers[p])throw std::bad_alloc();for(std::size_t i=0;i<2040;++i)initialize(static_cast<std::byte*>(pointers[p])+i*strides[p],p,i);}}
        catch(...){for(auto p:pointers)std::free(p);throw;}
    }
    ~Borrowed(){for(auto p:pointers)std::free(p);++*disposed;}
};
AgentDomainStorage* active=nullptr;
void* const mission=reinterpret_cast<void*>(0x60000000ULL);
std::atomic<bool> hold_helper=false,inside_helper=false,allow_helper=false,fail_helper=false;
std::atomic<unsigned> writes=0;
extern "C" void write_flag(void* owner,std::int64_t index)noexcept {
    if(owner!=mission||index<0||!active)return;
    try {
        auto lease=active->read();auto handle=lease.current(primary,static_cast<std::uint64_t>(index));
        if(handle.index==no_slot)return;
        if(hold_helper){inside_helper=true;while(!allow_helper)std::this_thread::yield();}
        if(fail_helper)throw std::runtime_error("Injected helper failure");
        auto p=static_cast<std::byte*>(lease.get(handle,0));p[0x580]=std::byte{1};++writes;
    }catch(...){/* Fail closed in this isolated prospective adapter. */}
}
class CodeWindow {
    void* data_=nullptr;
public:
    CodeWindow(){data_=VirtualAlloc(reinterpret_cast<void*>(stub_address),65536,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE);if(data_!=reinterpret_cast<void*>(stub_address)){if(data_)VirtualFree(data_,0,MEM_RELEASE);data_=nullptr;throw std::runtime_error("Isolated code address unavailable");}}
    ~CodeWindow(){if(data_)VirtualFree(data_,0,MEM_RELEASE);}
    void protect(){DWORD old;require(VirtualProtect(data_,65536,PAGE_EXECUTE_READ,&old)!=0,"Code protect");require(FlushInstructionCache(GetCurrentProcess(),data_,65536)!=0,"Instruction cache");}
};
class Tables {
    std::array<RUNTIME_FUNCTION,2> rows_{{{0,static_cast<DWORD>(write_stub_template.size()),0x1000},{0x2000,0x2020,0x1100}}};bool registered_=false;
public:
    Tables(){registered_=RtlAddFunctionTable(rows_.data(),2,stub_address)!=FALSE;require(registered_,"Unwind register");try{for(auto& row:rows_){DWORD64 base=0;require(RtlLookupFunctionEntry(stub_address+row.BeginAddress,&base,nullptr)==&row && base==stub_address,"Unwind readback");}}catch(...){RtlDeleteFunctionTable(rows_.data());registered_=false;throw;}}
    ~Tables(){if(registered_)RtlDeleteFunctionTable(rows_.data());}
    RUNTIME_FUNCTION* stub(){return &rows_[0];}
};
void unwind_checks(Tables& tables) {
    alignas(16) std::array<std::byte,2048> stack{};
    auto entry_rsp=reinterpret_cast<std::uintptr_t>(stack.data()+1032); // callee entry8mod16
    constexpr std::uint64_t return_pc=0x123456789abcdef0ULL;
    std::memcpy(reinterpret_cast<void*>(entry_rsp),&return_pc,8);
    constexpr std::array<unsigned,15> codes{0,1,2,3,5,6,7,8,9,10,11,12,13,14,15};
    for(std::size_t i=0;i<codes.size();++i){const auto value=0x1111000000000000ULL+codes[i];std::memcpy(reinterpret_cast<void*>(entry_rsp-264+32+i*8),&value,8);}
    for(auto offset:write_instruction_offsets) {
        CONTEXT ctx{};ctx.ContextFlags=CONTEXT_ALL;ctx.Rip=stub_address+offset;
        ctx.Rsp=(offset<14||offset==write_stub_template.size()-1)?entry_rsp:entry_rsp-264;
        ctx.Rbx=0x1111000000000003ULL;ctx.Rbp=0x1111000000000005ULL;ctx.Rsi=0x1111000000000006ULL;ctx.Rdi=0x1111000000000007ULL;
        ctx.R12=0x111100000000000cULL;ctx.R13=0x111100000000000dULL;ctx.R14=0x111100000000000eULL;ctx.R15=0x111100000000000fULL;
        PVOID handler=nullptr;DWORD64 frame=0;RtlVirtualUnwind(UNW_FLAG_NHANDLER,stub_address,ctx.Rip,tables.stub(),&ctx,&handler,&frame,nullptr);
        require(ctx.Rip==return_pc&&ctx.Rsp==entry_rsp+8,"Instruction-boundary unwind stack/return");
        require(ctx.Rbx==0x1111000000000003ULL&&ctx.Rbp==0x1111000000000005ULL&&ctx.Rsi==0x1111000000000006ULL&&ctx.Rdi==0x1111000000000007ULL&&ctx.R12==0x111100000000000cULL&&ctx.R13==0x111100000000000dULL&&ctx.R14==0x111100000000000eULL&&ctx.R15==0x111100000000000fULL,"Unwind nonvolatile restoration");
    }
}
void run() {
    CodeWindow window;
    auto stub=write_stub_template;auto address=reinterpret_cast<std::uintptr_t>(&write_flag);std::memcpy(stub.data()+write_helper_offset,&address,8);
    std::memcpy(reinterpret_cast<void*>(stub_address),stub.data(),stub.size());
    std::memcpy(reinterpret_cast<void*>(stub_address+0x1000),write_stub_unwind.data(),write_stub_unwind.size());
    // Isolated stand-in for the original PUSH RDI/SUB20 frame and reviewed site.
    // Caller ABI: RCX Mission, EDX index. The actual game callback is not invoked.
    unsigned char entry[]{0x57,0x48,0x83,0xec,0x20,0x48,0x8b,0xf9,0x48,0x63,0xc2,0xe8,0,0,0,0,
                         0x90,0x90,0x90,0x90,0x90,0x90,0x90,0x90,0x90,0x90,0x48,0x83,0xc4,0x20,0x5f,0xc3};
    auto displacement=static_cast<std::int32_t>(stub_address-(entry_address+16));std::memcpy(entry+12,&displacement,4);
    std::memcpy(reinterpret_cast<void*>(entry_address),entry,sizeof(entry));
    constexpr unsigned char entry_unwind[]{1,5,2,0,5,0x32,1,0x70};std::memcpy(reinterpret_cast<void*>(stub_address+0x1100),entry_unwind,sizeof(entry_unwind));
    Tables tables;window.protect();unwind_checks(tables);
    using Write=void(*)(void*,std::int32_t);auto write=reinterpret_cast<Write>(entry_address);
    Fixture fixture;std::atomic<unsigned> disposed=0;std::size_t operations=0;
    {
        auto borrowed=std::make_shared<Borrowed>(disposed);AgentDomainStorage storage(2040,strides,fixture.callbacks());
        storage.adopt_first_block(borrowed->pointers,borrowed,{{{0,2039},{1}}});borrowed.reset();active=&storage;
        std::vector<void*> old;
        for(std::uint64_t capacity:{2040ULL,4080ULL,6120ULL}) {
            storage.grow(capacity);
            for(std::uint64_t i=0;i<capacity;++i){DomainHandle h;{auto l=storage.read();h=l.current(primary,i);}if(h.index==no_slot)require(storage.acquire_at(primary,i).index==i,"Populate domain");}
            for(std::uint64_t i=0;i<capacity;++i) {
                {auto l=storage.read();auto p=l.get(l.current(primary,i),0);if(i<old.size())require(old[static_cast<std::size_t>(i)]==p,"Existing pointer moved");else old.push_back(p);static_cast<std::byte*>(p)[0x580]=std::byte{0};}
                write(mission,static_cast<std::int32_t>(i));
                {auto l=storage.read();require(static_cast<std::byte*>(l.get(l.current(primary,i),0))[0x580]==std::byte{1},"Native inline routed write");}
                ++operations;
            }
            auto before=writes.load();write(mission,-1);write(mission,static_cast<std::int32_t>(capacity));write(nullptr,0);require(writes==before,"Invalid write escaped");
        }
        DomainHandle removed;{auto l=storage.read();removed=l.current(primary,1);}
        require(storage.release(removed),"Delete primary while secondary exists");auto before=writes.load();write(mission,1);require(writes==before,"Secondary occupancy exposed as primary");
        require(storage.acquire_at(primary,1).generation!=removed.generation,"Reuse generation");write(mission,1);
        fail_helper=true;before=writes.load();write(mission,0);require(writes==before,"Failure helper wrote");fail_helper=false;
        hold_helper=true;inside_helper=false;allow_helper=false;
        std::future<void> call,growth;
        // Declared after both futures: every exceptional exit releases the
        // held helper before either future's destructor can wait for its task.
        struct HelperRelease {~HelperRelease(){allow_helper=true;hold_helper=false;}} release_helper;
        call=std::async(std::launch::async,[&]{write(mission,0);});
        const auto deadline=std::chrono::steady_clock::now()+std::chrono::seconds(5);
        while(!inside_helper && std::chrono::steady_clock::now()<deadline)std::this_thread::yield();
        if(!inside_helper){allow_helper=true;call.get();throw std::runtime_error("Helper lease wait timed out");}
        const bool close_blocked=!storage.try_close();
        auto growth_entered=std::make_shared<std::promise<void>>();auto growth_started=growth_entered->get_future();
        growth=std::async(std::launch::async,[&,growth_entered]{growth_entered->set_value();storage.grow(8160);});
        require(growth_started.wait_for(std::chrono::seconds(5))==std::future_status::ready,"Growth worker did not enter");
        growth_started.get();
        const bool growth_blocked=growth.wait_for(std::chrono::milliseconds(30))==std::future_status::timeout;
        allow_helper=true;call.get();growth.get();hold_helper=false;
        require(close_blocked&&growth_blocked,"Read lease did not cover complete native write");
        {auto l=storage.read();for(std::size_t i=0;i<old.size();++i)require(l.physical(0,i)==old[i],"Concurrent growth moved pointer");}
        require(storage.try_close()&&disposed==1,"Native write close ownership");before=writes.load();write(mission,0);require(writes==before,"Closed write");active=nullptr;
    }
    require(fixture.allocated==0&&fixture.constructed==fixture.destroyed,"Native write teardown");
    std::cout<<"PASS real x64 inline write + adopted segmented backend: "<<operations<<" writes;2040->4080->6120->8160;stable original pointers\n";
    std::cout<<"PASS "<<write_instruction_offsets.size()<<" instruction-boundary Windows unwinds;invalid/failing helper;independent domains;reuse;lease-held write blocks growth/close;teardown\n";
    std::cout<<"ISOLATED HARNESS ONLY: no live Bannerlord callback, engine objects, installation or whole-engine barrier\n";
}
}
int main(){try{run();return 0;}catch(const std::exception& e){std::cerr<<"FAIL: "<<e.what()<<'\n';return 1;}}
