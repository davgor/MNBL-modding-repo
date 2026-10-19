#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include <intrin.h>
#include "../../src/MNBL.AgentStorage/AgentDomainStorage.hpp"
#include "../../build/agent-storage/scalar_machine_code.hpp"
#include <atomic>
#include <chrono>
#include <cstdlib>
#include <cstring>
#include <future>
#include <iostream>
#include <thread>
using namespace mnbl::storage;
namespace {
constexpr std::array<std::size_t,5> strides{0xc60,0xc60,0x230,0x2c8,0x2c8};
constexpr auto primary=AgentDomain::Primary,secondary=AgentDomain::Secondary;
void require(bool value,const char* message){if(!value)throw std::runtime_error(message);}
void initialize(void* p,std::size_t pool,std::uint64_t index){std::memset(p,0,strides[pool]);std::memcpy(p,&p,8);std::memcpy(static_cast<std::byte*>(p)+8,&index,8);}
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
        try{for(std::size_t p=0;p<5;++p){pointers[p]=std::malloc(2040*strides[p]);if(!pointers[p])throw std::bad_alloc();for(std::size_t i=0;i<2040;++i)initialize(static_cast<std::byte*>(pointers[p])+i*strides[p],p,i);}}
        catch(...){for(auto p:pointers)std::free(p);throw;}
    }
    ~Borrowed(){for(auto p:pointers)std::free(p);++*disposed;}
};
AgentDomainStorage* active=nullptr;
void* const mission=reinterpret_cast<void*>(0x60000000ULL);
std::atomic<bool> hold_helper=false,inside_helper=false,allow_helper=false,fail_helper=false,throw_helper=false;
std::atomic<unsigned> operations=0,traps=0;
thread_local unsigned last_error=0;
std::uintptr_t code_base=0;
enum Error:unsigned{success=0,foreign_owner=1,inactive_index=2,injected_failure=3,invalid_site=4,bad_alignment=5,unexpected_exception=6};
std::uint64_t failure(Error e)noexcept{last_error=e;return static_cast<std::uint64_t>(e)<<32;}
extern "C" __declspec(noinline) std::uint64_t scalar_operation(void* owner,std::int64_t index,unsigned site)noexcept{
    last_error=0;
    if((reinterpret_cast<std::uintptr_t>(_AddressOfReturnAddress())+8)%16!=0)return failure(bad_alignment);
    if(site>=scalar_templates.size())return failure(invalid_site);
    if(owner!=mission||!active)return failure(foreign_owner);
    if(index<0)return failure(inactive_index);
    try{
        auto lease=active->read();auto h=lease.current(primary,static_cast<std::uint64_t>(index));
        if(h.index==no_slot)return failure(inactive_index);
        if(hold_helper){inside_helper=true;while(!allow_helper)std::this_thread::yield();}
        if(fail_helper)return failure(injected_failure);
        if(throw_helper)throw std::runtime_error("Injected C++ helper exception");
        const auto& spec=scalar_templates[site];auto p=static_cast<std::byte*>(lease.get(h,0));
        if(!p)return failure(inactive_index);
        std::uint32_t value=0;
        if(spec.writes)p[spec.field]=std::byte{1};else std::memcpy(&value,p+spec.field,sizeof(value));
        ++operations;return value; // Scalar only; lease remains held through memory access.
    }catch(...){return failure(unexpected_exception);}
}
using Entry=std::uint32_t(*)(void*,std::int64_t);
int exact_trap_filter(EXCEPTION_POINTERS* info,unsigned site)noexcept{
    if(site<scalar_templates.size()&&info->ExceptionRecord->ExceptionCode==EXCEPTION_ILLEGAL_INSTRUCTION&&
       reinterpret_cast<std::uintptr_t>(info->ExceptionRecord->ExceptionAddress)==code_base+site*0x1000+scalar_templates[site].trap_offset&&last_error!=0){++traps;return EXCEPTION_EXECUTE_HANDLER;}
    return EXCEPTION_CONTINUE_SEARCH;
}
// Separate POD-only SEH function permits actual stack unwinding through both
// dynamically registered generated frames. Unexpected native faults escape.
bool invoke(Entry f,void* owner,std::int64_t index,unsigned site,std::uint32_t* value){
    __try{*value=f(owner,index);return true;}
    __except(exact_trap_filter(GetExceptionInformation(),site)){return false;}
}
class CodeWindow {
    void* data_=nullptr;
public:
    CodeWindow(){data_=VirtualAlloc(nullptr,65536,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE);if(!data_)throw std::bad_alloc();code_base=reinterpret_cast<std::uintptr_t>(data_);}
    ~CodeWindow(){if(data_)VirtualFree(data_,0,MEM_RELEASE);code_base=0;}
    void protect(){DWORD old;require(VirtualProtect(data_,65536,PAGE_EXECUTE_READ,&old)!=0,"Code protect");require(FlushInstructionCache(GetCurrentProcess(),data_,65536)!=0,"Instruction cache");}
};
class Tables {
    std::array<RUNTIME_FUNCTION,14> rows_{};bool registered_=false;
public:
    Tables(){
        for(std::size_t i=0;i<scalar_templates.size();++i){auto base=static_cast<DWORD>(i*0x1000);const auto& s=scalar_templates[i];rows_[2*i]={base,base+static_cast<DWORD>(s.stub.size()),base+0x300};rows_[2*i+1]={base+0x400,base+0x400+static_cast<DWORD>(s.entry.size()),base+0x700};}
        registered_=RtlAddFunctionTable(rows_.data(),static_cast<DWORD>(rows_.size()),code_base)!=FALSE;require(registered_,"Unwind register");
        try{for(auto& row:rows_){DWORD64 base=0;require(RtlLookupFunctionEntry(code_base+row.BeginAddress,&base,nullptr)==&row&&base==code_base,"Unwind readback");}}
        catch(...){RtlDeleteFunctionTable(rows_.data());registered_=false;throw;}
    }
    ~Tables(){if(registered_)RtlDeleteFunctionTable(rows_.data());}
    RUNTIME_FUNCTION* get(std::size_t site,bool entry){return &rows_[site*2+(entry?1:0)];}
};
std::size_t unwind_checks(Tables& tables){
    constexpr std::array<unsigned,15> codes{0,1,2,3,5,6,7,8,9,10,11,12,13,14,15};
    constexpr std::uint64_t return_pc=0x123456789abcdef0ULL;std::size_t checked=0;
    for(std::size_t s=0;s<scalar_templates.size();++s)for(bool entry:{false,true}){
        const auto& spec=scalar_templates[s];const auto offsets=entry?spec.entry_offsets:spec.stub_offsets;
        const auto allocation=entry?spec.entry_alloc:spec.stub_alloc,epilog=entry?spec.entry_epilog:spec.stub_epilog;
        const auto size=entry?spec.entry.size():spec.stub.size();auto fn=tables.get(s,entry);
        for(auto offset:offsets){
            alignas(16) std::array<std::byte,2048> stack{};std::memset(stack.data(),0xcc,stack.size());
            auto entry_rsp=reinterpret_cast<std::uintptr_t>(stack.data()+1032);std::memcpy(reinterpret_cast<void*>(entry_rsp),&return_pc,8);
            CONTEXT ctx{};ctx.ContextFlags=CONTEXT_ALL;ctx.Rip=code_base+fn->BeginAddress+offset;ctx.Rsp=(offset<allocation||offset==size-1)?entry_rsp:entry_rsp-264;
            std::array<DWORD64*,15> fields{&ctx.Rax,&ctx.Rcx,&ctx.Rdx,&ctx.Rbx,&ctx.Rbp,&ctx.Rsi,&ctx.Rdi,&ctx.R8,&ctx.R9,&ctx.R10,&ctx.R11,&ctx.R12,&ctx.R13,&ctx.R14,&ctx.R15};
            for(std::size_t i=0;i<codes.size();++i){
                const auto value=0x1111000000000000ULL+codes[i];const bool saved=offset>=allocation+(i+1)*8;
                if(saved)std::memcpy(reinterpret_cast<void*>(entry_rsp-264+32+i*8),&value,8);
                *fields[i]=saved&&offset<epilog?0xdddd000000000000ULL+codes[i]:value;
            }
            PVOID handler=nullptr;DWORD64 frame=0;RtlVirtualUnwind(UNW_FLAG_NHANDLER,code_base,ctx.Rip,fn,&ctx,&handler,&frame,nullptr);
            require(ctx.Rip==return_pc&&ctx.Rsp==entry_rsp+8,"Instruction-boundary unwind stack/return");
            for(auto i:{3,4,5,6,11,12,13,14})require(*fields[static_cast<std::size_t>(i)]==0x1111000000000000ULL+codes[static_cast<std::size_t>(i)],"Unwind nonvolatile restore");
            ++checked;
        }
    }
    return checked;
}
std::uint32_t field_value(std::uint64_t index,unsigned field){return 0x80000000U|static_cast<std::uint32_t>((index*7919+field*101)&0x7fffffff);}
void seed(void* pointer,std::uint64_t index){for(const auto& s:scalar_templates){auto p=static_cast<std::byte*>(pointer)+s.field;if(s.writes)*p=std::byte{0};else{auto v=field_value(index,s.field);std::memcpy(p,&v,4);}}}
void run(){
    CodeWindow window;std::array<Entry,7> entries{};
    for(std::size_t i=0;i<scalar_templates.size();++i){
        const auto& s=scalar_templates[i];auto stub=std::vector<unsigned char>(s.stub.begin(),s.stub.end());auto helper=reinterpret_cast<std::uintptr_t>(&scalar_operation);std::memcpy(stub.data()+s.helper_offset,&helper,8);
        auto base=code_base+i*0x1000;std::memcpy(reinterpret_cast<void*>(base),stub.data(),stub.size());std::memcpy(reinterpret_cast<void*>(base+0x300),s.unwind.data(),s.unwind.size());
        std::memcpy(reinterpret_cast<void*>(base+0x400),s.entry.data(),s.entry.size());std::memcpy(reinterpret_cast<void*>(base+0x700),s.entry_unwind.data(),s.entry_unwind.size());entries[i]=reinterpret_cast<Entry>(base+0x400);
    }
    Tables tables;window.protect();const auto unwinds=unwind_checks(tables);
    Fixture fixture;std::atomic<unsigned> disposed=0;std::size_t main_operations=0;
    {
        auto borrowed=std::make_shared<Borrowed>(disposed);AgentDomainStorage storage(2040,strides,fixture.callbacks());
        storage.adopt_first_block(borrowed->pointers,borrowed,{{{0,2039},{1}}});borrowed.reset();active=&storage;std::vector<void*> old;
        auto reject=[&](void* owner,std::int64_t index,unsigned site){auto before=operations.load();std::uint32_t value=0x12345678;require(!invoke(entries[site],owner,index,site,&value)&&last_error!=0&&value==0x12345678,"Invalid request did not report explicit UD2 failure");require(operations==before,"Rejected operation accessed payload");};
        for(std::uint64_t capacity:{2040ULL,4080ULL,6120ULL}){
            storage.grow(capacity);
            for(std::uint64_t i=0;i<capacity;++i){DomainHandle h;{auto l=storage.read();h=l.current(primary,i);}if(h.index==no_slot)require(storage.acquire_at(primary,i).index==i,"Populate primary");}
            for(std::uint64_t i=0;i<capacity;++i){
                {auto l=storage.read();auto p=l.get(l.current(primary,i),0);if(i<old.size())require(old[static_cast<std::size_t>(i)]==p,"Original address moved");else old.push_back(p);seed(p,i);}
                for(unsigned site=0;site<entries.size();++site){std::uint32_t value=0;require(invoke(entries[site],mission,static_cast<std::int64_t>(i),site,&value)&&last_error==0,"Routed operation failed");const auto& s=scalar_templates[site];require(value==(s.writes?1:field_value(i,s.field)),"Scalar output/zero extension/index overlap");++main_operations;}
                {auto l=storage.read();require(static_cast<std::byte*>(l.get(l.current(primary,i),0))[0x580]==std::byte{1},"Routed byte store missing");}
            }
            for(unsigned site=0;site<entries.size();++site){reject(mission,-1,site);reject(mission,static_cast<std::int64_t>(capacity),site);reject(nullptr,0,site);}
        }
        DomainHandle removed;{auto l=storage.read();removed=l.current(primary,1);}require(storage.release(removed),"Release primary");
        for(unsigned site=0;site<entries.size();++site)reject(mission,1,site);
        require(storage.acquire_at(primary,1).generation!=removed.generation,"Reuse generation");
        for(unsigned site=0;site<entries.size();++site){std::uint32_t value=0;require(invoke(entries[site],mission,1,site,&value),"Reused index");}
        fail_helper=true;for(unsigned site=0;site<entries.size();++site)reject(mission,0,site);fail_helper=false;
        throw_helper=true;for(unsigned site=0;site<entries.size();++site){reject(mission,0,site);require(last_error==unexpected_exception,"C++ exception was not translated to explicit failure");}throw_helper=false;
        require((scalar_operation(mission,0,999)>>32)==invalid_site,"Invalid site ID");
        for(unsigned site:{0U,4U}){
            hold_helper=true;inside_helper=false;allow_helper=false;std::future<void> call,growth;
            struct ReleaseHelper{~ReleaseHelper(){allow_helper=true;hold_helper=false;}} release_helper;
            call=std::async(std::launch::async,[&,site]{std::uint32_t value=0;require(invoke(entries[site],mission,0,site,&value),"Held helper failed");});
            auto deadline=std::chrono::steady_clock::now()+std::chrono::seconds(5);while(!inside_helper&&std::chrono::steady_clock::now()<deadline)std::this_thread::yield();
            require(inside_helper,"Held helper entry timeout");const bool close_blocked=!storage.try_close();
            auto entered=std::make_shared<std::promise<void>>();auto ready=entered->get_future();const auto target=site==0?8160ULL:10200ULL;
            growth=std::async(std::launch::async,[&,entered,target]{entered->set_value();storage.grow(target);});
            require(ready.wait_for(std::chrono::seconds(5))==std::future_status::ready,"Growth entry timeout");ready.get();
            const bool blocked=growth.wait_for(std::chrono::milliseconds(30))==std::future_status::timeout;
            allow_helper=true;call.get();growth.get();hold_helper=false;require(close_blocked&&blocked,"Read lease did not cover full scalar operation");
            {auto l=storage.read();for(std::size_t i=0;i<old.size();++i)require(l.physical(0,i)==old[i],"Growth moved original payload");}
        }
        require(storage.try_close()&&disposed==1,"Close borrowed ownership");for(unsigned site=0;site<entries.size();++site)reject(mission,0,site);active=nullptr;
    }
    require(fixture.allocated==0&&fixture.constructed==fixture.destroyed,"Synthetic teardown leaked");
    require(main_operations==85680&&traps==91,"Unexpected test coverage");
    std::cout<<"PASS seven real x64 scalar adapters: "<<main_operations<<" operations;2040->4080->6120->8160->10200;stable borrowed/owned payloads\n";
    std::cout<<"PASS "<<unwinds<<" instruction-boundary Windows unwinds;"<<traps<<" exact UD2 error-path unwinds;index reuse;independent domains;read/write lease blocks growth/close;teardown\n";
    std::cout<<"ISOLATED HARNESS ONLY: explicit fixture traps, no production failure policy, live game, real Agent constructors or whole-engine barrier\n";
}
}
int main(){try{run();return 0;}catch(const std::exception& e){std::cerr<<"FAIL: "<<e.what()<<'\n';return 1;}}
