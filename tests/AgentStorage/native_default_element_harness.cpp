#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include "../../src/MNBL.AgentGrowthProbe/UnpublishedElementProbe.hpp"
#include "../../build/agent-storage/native_default_image.hpp"
#include <bit>
#include <cstring>
#include <iostream>
#include <map>
#include <string>
using namespace mnbl::storage;
namespace {
void require(bool b,const char* message){if(!b)throw std::runtime_error(message);}
unsigned images_closed=0;
class CopiedImage {
    void* memory_=nullptr;HMODULE crt_=nullptr;bool registered_=false;
    std::array<RUNTIME_FUNCTION,default_runtime_functions.size()> functions_{};
    std::map<unsigned,bool> pages_;
    void dispose()noexcept{
        if(registered_){RtlDeleteFunctionTable(functions_.data());registered_=false;}
        if(memory_){VirtualFree(memory_,0,MEM_RELEASE);memory_=nullptr;}
        if(crt_){FreeLibrary(crt_);crt_=nullptr;}
    }
public:
    explicit CopiedImage(std::uint64_t empty_sentinel){
        try{
            memory_=VirtualAlloc(nullptr,default_image_size,MEM_RESERVE,PAGE_NOACCESS);if(!memory_)throw std::bad_alloc();
            for(auto r:default_image_regions)for(unsigned page=r.rva&~4095U;page<((r.rva+static_cast<unsigned>(r.bytes.size())+4095U)&~4095U);page+=4096){
                auto [it,inserted]=pages_.emplace(page,r.executable);require(inserted||it->second==r.executable,"Mixed copied page permissions");
            }
            for(auto [page,executable]:pages_){auto p=at(page);require(VirtualAlloc(p,4096,MEM_COMMIT,PAGE_READWRITE)==p,"Commit copied page");if(executable)std::memset(p,0xcc,4096);}
            for(auto r:default_image_regions)std::memcpy(at(r.rva),r.bytes.data(),r.bytes.size());
            // Actual host CRT, confined to System32. No game DLL is loaded and
            // no original module initializer is executed.
            crt_=LoadLibraryExW(L"ucrtbase.dll",nullptr,LOAD_LIBRARY_SEARCH_SYSTEM32);require(crt_!=nullptr,"Host CRT unavailable");
            const auto symbol=GetProcAddress(crt_,"atan2f");require(symbol!=nullptr,"Host atan2f unavailable");
            using Math=float(*)(float,float);Math math=nullptr;static_assert(sizeof(math)==sizeof(symbol));std::memcpy(&math,&symbol,sizeof(math));
            MEMORY_BASIC_INFORMATION math_region{};require(VirtualQuery(reinterpret_cast<const void*>(math),&math_region,sizeof(math_region))==sizeof(math_region)&&math_region.Type==MEM_IMAGE&&math_region.AllocationBase==crt_,"atan2f provider differs from loaded CRT");
            require(std::bit_cast<std::uint32_t>(math(-0.0f,1.0f))==0x80000000U,"Host atan2f signed zero differs");
            const auto imported=reinterpret_cast<std::uintptr_t>(math),dispatcher=base()+0x9e3660;
            std::memcpy(at(0xa2dfa8),&imported,8);std::memcpy(at(0xa2e4e8),&dispatcher,8);
            std::memcpy(at(0xda29b8),&empty_sentinel,8);
            constexpr std::uint64_t registry=0x7777000011110000ULL;std::memcpy(at(0xda2a28),&registry,8);
            require(*static_cast<unsigned char*>(at(0x9e3177))==0xcc&&*static_cast<unsigned char*>(at(0x9e318f))==0xcc,"Unreviewed EH handlers not trapped");
            for(auto r:default_image_regions){
                if(r.rva==0xa2dfa8||r.rva==0xa2e4e8||r.rva==0xda29b8||r.rva==0xda2a28)continue;
                require(std::memcmp(at(r.rva),r.bytes.data(),r.bytes.size())==0,"Copied original region differs");
            }
            for(std::size_t i=0;i<functions_.size();++i){auto f=default_runtime_functions[i];functions_[i]={f.begin,f.end,f.unwind};}
            registered_=RtlAddFunctionTable(functions_.data(),static_cast<DWORD>(functions_.size()),base())!=FALSE;require(registered_,"Register original copied unwind records");
            for(auto [page,executable]:pages_){DWORD old;require(VirtualProtect(at(page),4096,executable?PAGE_EXECUTE_READ:PAGE_READONLY,&old)!=0,"Protect copied page");}
            require(FlushInstructionCache(GetCurrentProcess(),memory_,default_image_size)!=0,"Flush copied code");
            MEMORY_BASIC_INFORMATION info{};require(VirtualQuery(at(0x5cd130),&info,sizeof(info))==sizeof(info)&&info.State==MEM_RESERVE,"Agent activation unexpectedly mapped");
            validate_unwind();
            wchar_t path[32768]{};const auto count=GetModuleFileNameW(crt_,path,32768);require(count!=0&&count<32768,"CRT module path");
            std::wcout<<L"CRT_PATH="<<path<<L'\n';
        }catch(...){dispose();throw;}
    }
    ~CopiedImage(){dispose();++images_closed;}
    std::uintptr_t base()const noexcept{return reinterpret_cast<std::uintptr_t>(memory_);}
    void* at(unsigned rva)const noexcept{return reinterpret_cast<void*>(base()+rva);}
    void validate_unwind(){
        for(auto& f:functions_){
            DWORD64 image_base=0;require(RtlLookupFunctionEntry(base()+f.BeginAddress,&image_base,nullptr)==&f&&image_base==base(),"Copied runtime function lookup");
            alignas(16) std::array<std::uint64_t,32> stack{};auto entry_rsp=reinterpret_cast<std::uintptr_t>(stack.data()+17);
            constexpr std::uint64_t pc=0x123456789abcdef0;std::memcpy(reinterpret_cast<void*>(entry_rsp),&pc,8);
            CONTEXT c{};c.ContextFlags=CONTEXT_ALL;c.Rip=base()+f.BeginAddress;c.Rsp=entry_rsp;
            c.Rbx=3;c.Rbp=5;c.Rsi=6;c.Rdi=7;c.R12=12;c.R13=13;c.R14=14;c.R15=15;
            void* handler=nullptr;DWORD64 frame=0;
            RtlVirtualUnwind(UNW_FLAG_NHANDLER,base(),c.Rip,&f,&c,&handler,&frame,nullptr);
            require(c.Rip==pc&&c.Rsp==entry_rsp+8,"Original entry unwind return/stack");
            require(c.Rbx==3&&c.Rbp==5&&c.Rsi==6&&c.Rdi==7&&c.R12==12&&c.R13==13&&c.R14==14&&c.R15==15,"Original entry unwind nonvolatiles");
        }
    }
};
NativeElementFactory::Bindings bindings(const std::shared_ptr<CopiedImage>& image){
    return{reinterpret_cast<NativeElementFactory::Constructor>(image->at(0x606a20)),
           reinterpret_cast<NativeElementFactory::Destructor>(image->at(0x6070c0)),
           reinterpret_cast<NativeElementFactory::Constructor>(image->at(0x5e58e0)),
           reinterpret_cast<NativeElementFactory::Constructor>(image->at(0x5d8860)),image};
}
void default_checks(void* object,std::size_t pool){
    auto p=static_cast<std::byte*>(object);std::int32_t index=0;std::memcpy(&index,p+NativeElementFactory::headers[pool],4);require(index==-1,"Default inactive index");
    if(pool<2){
        std::uint64_t mission=1;std::uint32_t managed=1,angle=0;
        std::memcpy(&mission,p+0xab8,8);std::memcpy(&managed,p+0x18,4);std::memcpy(&angle,p+0x484,4);
        require(mission==0&&managed==0,"Default Agent published state");require(angle==0x80000000U,"Actual constructor atan2f result");
    }
}
void canary_lifetimes(NativeElementFactory& factory){
    auto callbacks=factory.callbacks();
    for(std::size_t pool=0;pool<5;++pool)for(unsigned poison:{0U,0x55U,0xaaU,0xffU}){
        const auto size=NativeElementFactory::strides[pool];auto allocation=static_cast<std::byte*>(_aligned_malloc(size+64,16));if(!allocation)throw std::bad_alloc();
        struct Free{void* p;~Free(){_aligned_free(p);}}free{allocation};
        std::memset(allocation,0x69,size+64);auto object=allocation+32;std::memset(object,static_cast<int>(poison),size);
        callbacks.construct(callbacks.context,pool,0,object);default_checks(object,pool);callbacks.destroy(callbacks.context,pool,0,object);
        for(unsigned i=0;i<32;++i)require(allocation[i]==std::byte{0x69}&&allocation[size+32+i]==std::byte{0x69},"Native element crossed private canary");
    }
}
void small(NativeElementFactory& factory){
    mnbl::probe::ElementProbeResult r;mnbl::probe::exercise_unpublished_elements(factory,r);
    require(r.status==1&&r.stage==8&&r.capacity==90,"Private core did not finish");
    require(r.pointer_checks==600&&r.header_checks==450,"Private core coverage");
    require(r.allocation_failure_checked&&r.constructor_failure_checked&&r.exhaustion_checked&&r.reuse_checked,"Private core failure/ledger checks missing");
    std::uint64_t count=0;for(std::size_t p=0;p<5;++p){require(r.constructed[p]==r.destroyed[p],"Small cleanup count");count+=r.constructed[p];}
    require(count==487&&r.allocations==r.releases,"Small callback/allocator coverage");
    std::cout<<"PASS real all-five-pool core30->60->90:487 completed callbacks;600 stable-pointer checks;450 inactive headers;allocation/pre-constructor failures;two-ledger exhaustion/reuse;teardown\n";
}
void large(NativeElementFactory& factory){
    mnbl::probe::ElementProbeResult r;mnbl::probe::InstrumentedCallbacks callbacks{factory.callbacks(),r};
    AgentDomainStorage storage(2040,NativeElementFactory::strides,callbacks.callbacks());std::array<std::vector<void*>,5> pointers;
    std::uint64_t stable=0,headers=0;
    for(std::uint64_t capacity:{2040ULL,4080ULL,6120ULL}){
        storage.grow(capacity);auto lease=storage.read();require(lease.capacity()==capacity,"Large private capacity");
        for(std::size_t pool=0;pool<5;++pool)for(std::uint64_t index=0;index<capacity;++index){
            auto p=lease.physical(pool,index);if(index<pointers[pool].size()){require(pointers[pool][static_cast<std::size_t>(index)]==p,"Real native payload moved");++stable;}else pointers[pool].push_back(p);
            default_checks(p,pool);++headers;
        }
        require(lease.live(AgentDomain::Primary)==0&&lease.live(AgentDomain::Secondary)==0,"Unpublished native payload became occupied");
    }
    require(storage.try_close(),"Large private close");
    std::uint64_t constructed=0;for(std::size_t p=0;p<5;++p){require(r.constructed[p]==6120&&r.destroyed[p]==6120,"Large real callback cleanup");constructed+=r.constructed[p];}
    require(constructed==30600&&headers==61200&&stable==30600&&r.allocations==r.releases,"Large exact coverage/leak");
    std::cout<<"PASS real all-five-pool private2040->4080->6120:30600 native-construction callbacks;30600 stable-pointer checks;61200 inactive headers;balanced teardown\n";
}
void run(){
    for(auto sentinel:{0ULL,0x777788889999aaaaULL}){
        auto image=std::make_shared<CopiedImage>(sentinel);NativeElementFactory factory(bindings(image));
        canary_lifetimes(factory);small(factory);
        // The small real lifecycle must finish before the large private test.
        if(sentinel!=0)large(factory);
    }
    require(images_closed==2,"Copied image/runtime ownership leak");
    std::cout<<"PASS40 poisoned real native element lifetimes;18 original function-entry unwinds;real CRT signed-zero;read-only copied globals;two non-null-registry fixtures\n";
    std::cout<<"ISOLATED DEFAULT PATH ONLY: native EH handlers remain trap-filled/unmapped;unexpected native exceptions fail process;no game,activation,Mission writes or live growth publication\n";
}
}
int main(int argc,char** argv){
    // No SEH handler: faults or unsupported copied-native exception paths must
    // fail the process/test. Only host C++ failures become a nonzero result.
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX);
    try{
        if(argc==2&&std::strcmp(argv[1],"--unsupported-handler-must-fail")==0){
            auto image=std::make_shared<CopiedImage>(0);
            std::cout<<"EXPECTED_FAILURE: entering trap-filled unsupported native EH handler\n"<<std::flush;
            reinterpret_cast<void(*)()>(image->at(0x9e3177))();
            std::cerr<<"FAIL: unsupported handler returned\n";return 2;
        }
        require(argc==1,"Unknown harness argument");run();return 0;
    }catch(const std::exception& e){std::cerr<<"FAIL: "<<e.what()<<'\n';return 1;}
}
