#include "../../src/MNBL.AgentStorage/NativeAgentTransactionStorage.hpp"
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdlib>
#include <future>
#include <iostream>
#include <random>
#include <set>
#include <thread>

// Test-only host allocation failures. Never injected into actual native code.
std::atomic<long> allocation_failure{-1};
void* operator new(std::size_t size) {
    auto n = allocation_failure.load();
    if (n >= 0 && allocation_failure.fetch_sub(1) == 0) {
        allocation_failure = -1; throw std::bad_alloc();
    }
    if (auto* p = std::malloc(size ? size : 1)) return p;
    throw std::bad_alloc();
}
void operator delete(void* p) noexcept { std::free(p); }
void operator delete(void* p, std::size_t) noexcept { std::free(p); }
void* operator new[](std::size_t n) { return ::operator new(n); }
void operator delete[](void* p) noexcept { ::operator delete(p); }
void operator delete[](void* p, std::size_t) noexcept { ::operator delete(p); }

using namespace mnbl::storage;
using namespace std::chrono_literals;
namespace {
using Storage = NativeAgentTransactionStorage;
constexpr auto primary = AgentDomain::Primary, secondary = AgentDomain::Secondary;
std::size_t assertions = 0, resets = 0, cleanups = 0, injected_failures = 0;
void require(bool condition, const char* message) {
    ++assertions; if (!condition) throw std::runtime_error(message);
}
struct Fixture;
Fixture* current = nullptr;
struct Fixture {
    std::atomic<std::size_t> constructed{0}, destroyed{0}, reset_count{0}, cleanup_count{0};
    std::atomic<long> constructor_failure{-1};
    std::mutex mutex;
    std::condition_variable cv;
    bool block_reset = false, block_cleanup = false, entered = false, proceed = false;
    void before_construct() {
        const auto n = constructor_failure.load();
        if (n >= 0 && constructor_failure.fetch_sub(1) == 0) {
            constructor_failure = -1; throw std::bad_alloc();
        }
        ++constructed;
    }
    static void* agent(void* p) { current->before_construct(); std::memset(p, 0x34, 0xc60); return p; }
    static void* companion230(void* p) { current->before_construct(); std::memset(p, 0x34, 0x230); return p; }
    static void* companion2c8(void* p) { current->before_construct(); std::memset(p, 0x34, 0x2c8); return p; }
    static std::int32_t header(void* p, std::size_t pool) {
        std::int32_t value; std::memcpy(&value,static_cast<std::byte*>(p)+NativeElementFactory::headers[pool],4); return value;
    }
    static void destroy_agent(void* p) {
        if (header(p,0) != -1) std::terminate();
        ++current->destroyed;
    }
    void transition(DomainHandle h, const Storage::Elements& elements, bool reset) noexcept {
        for (std::size_t pool = 0; pool < pool_count; ++pool) {
            const bool present = h.domain == secondary ? pool == 1 : pool != 1;
            if ((elements[pool] != nullptr) != present) std::terminate();
            if (present && header(elements[pool],pool) != (reset ? -1 : static_cast<std::int32_t>(h.index))) std::terminate();
        }
        {
            std::unique_lock lock(mutex);
            if (reset ? block_reset : block_cleanup) {
                entered = true; cv.notify_all();
                cv.wait(lock,[&]{return proceed;});
            }
        }
        for (std::size_t pool = 0; pool < pool_count; ++pool) if (elements[pool]) {
            const auto value = reset ? static_cast<std::int32_t>(h.index) : -1;
            std::memcpy(static_cast<std::byte*>(elements[pool])+NativeElementFactory::headers[pool],&value,4);
        }
        if (reset) ++reset_count; else ++cleanup_count;
    }
    static void reset(void* context,DomainHandle h,const Storage::Elements& e) noexcept { static_cast<Fixture*>(context)->transition(h,e,true); }
    static void cleanup(void* context,DomainHandle h,const Storage::Elements& e) noexcept { static_cast<Fixture*>(context)->transition(h,e,false); }
    void wait_entered() {
        std::unique_lock lock(mutex);
        if (!cv.wait_for(lock,5s,[&]{return entered;})) throw std::runtime_error("Transition entry timeout");
    }
    void unblock() noexcept { std::lock_guard lock(mutex); proceed = true; cv.notify_all(); }
};
NativeElementFactory::Bindings bindings(const std::shared_ptr<Fixture>& f, std::shared_ptr<void> runtime = {}) {
    return {Fixture::agent,Fixture::destroy_agent,Fixture::companion230,Fixture::companion2c8,runtime ? std::move(runtime) : f};
}
Storage::Transitions transitions(const std::shared_ptr<Fixture>& f) { return {f.get(),Fixture::reset,Fixture::cleanup,f}; }
void complete(const std::shared_ptr<Fixture>& f) {
    require(f->reset_count == f->cleanup_count,"Every active transition must be cleaned exactly once");
    resets += f->reset_count; cleanups += f->cleanup_count;
}
struct Unblock { std::shared_ptr<Fixture> f; ~Unblock(){f->unblock();} };

void lowest_and_domains() {
    auto f=std::make_shared<Fixture>(); current=f.get();
    {
        Storage s(3,bindings(f),transitions(f)); s.grow(6);
        std::array<std::vector<DomainHandle>,2> handles;
        for (auto d : {primary,secondary}) for (std::uint64_t i=0;i<6;++i) {
            auto h=s.acquire(d); require(h.index==i,"Lowest initial index"); handles[static_cast<std::size_t>(d)].push_back(h);
        }
        const auto old_reset=f->reset_count.load();
        require(s.acquire(primary).index==no_slot && s.acquire(secondary).index==no_slot,"Exhaustion");
        require(f->reset_count==old_reset,"No exhaustion callback");
        require(s.release(handles[0][1]) && s.release(handles[0][4]),"Out-of-order holes");
        s.grow(9);
        require(s.acquire(primary).index==1 && s.acquire(primary).index==4 && s.acquire(primary).index==6,"Old lowest holes precede growth");
        require(s.acquire(secondary).index==6,"Independent secondary ledger");
        require(s.acquire_at(primary,8).index==8,"Explicit slot");
        require(s.acquire(primary).index==7,"Lowest after explicit slot");
        require(s.acquire_at(primary,8).index==no_slot && s.acquire_at(primary,no_slot).index==no_slot,"Invalid explicit insert");
        const auto old_cleanup=f->cleanup_count.load();
        require(!s.release(handles[0][1]) && !s.release({primary,no_slot,0}) && !s.release({static_cast<AgentDomain>(9),0,1}),"Stale/invalid handles");
        require(f->cleanup_count==old_cleanup,"Invalid handle must not call cleanup");
        bool invalid=false; try { s.acquire(static_cast<AgentDomain>(9)); } catch(const std::invalid_argument&) { invalid=true; }
        require(invalid,"Unknown domain rejects");
        require(s.try_close() && s.try_close(),"Idempotent close cleans active records");
        bool closed=false; try{s.acquire(primary);}catch(const std::logic_error&){closed=true;}
        require(closed,"Closed allocation rejects");
        {auto l=s.read();require(l.capacity()==0 && l.live(primary)==0 && l.live(secondary)==0,"Closed public state");}
    }
    require(f->constructed==45 && f->destroyed==18,"All owned Agent destructors after close"); complete(f);
}

void transition_barriers(bool cleanup) {
    auto f=std::make_shared<Fixture>(); current=f.get();
    Storage s(3,bindings(f),transitions(f)); s.grow(3);
    auto original=cleanup?s.acquire(primary):DomainHandle{};
    f->block_cleanup=cleanup; f->block_reset=!cleanup;
    std::future<DomainHandle> writer;
    std::future<std::uint64_t> reader;
    Unblock guard{f}; // destroyed before futures on failure
    writer=std::async(std::launch::async,[&]{ if(cleanup){s.release(original);return original;} return s.acquire(primary); });
    f->wait_entered();
    auto ready=std::make_shared<std::promise<void>>();auto ready_future=ready->get_future();
    reader=std::async(std::launch::async,[&,ready]{ready->set_value();auto l=s.read();return l.live(primary);});
    ready_future.get();
    require(reader.wait_for(100ms)==std::future_status::timeout,"Reader must wait for whole transition");
    require(!s.try_close(),"Close cannot pass active transition");
    f->unblock();writer.get();
    require(reader.get()==(cleanup?0U:1U),"Reader observes complete transition only");
    f->block_reset=false;f->block_cleanup=false;
    require(s.try_close(),"Transition close");complete(f);
}

void read_lifetime_and_growth() {
    auto f=std::make_shared<Fixture>(); current=f.get();
    auto s=std::make_unique<Storage>(3,bindings(f),transitions(f)); s->grow(3); auto h=s->acquire(primary);
    std::future<void> growth;
    {
        auto l=s->read(); auto moved=std::move(l);
        auto* pointer=moved.get(h,0);
        auto ready=std::make_shared<std::promise<void>>();auto started=ready->get_future();
        growth=std::async(std::launch::async,[&,ready]{ready->set_value();s->grow(6);});
        started.get();
        require(growth.wait_for(100ms)==std::future_status::timeout,"Growth blocked through full read lease");
        require(!std::async(std::launch::async,[&]{return s->try_close();}).get(),"Close blocked through read lease");
        require(moved.get(h,0)==pointer,"Payload stable through blocked growth");
    }
    growth.get();
    {
        auto l=s->read();auto moved=std::move(l);
        s.reset();require(f->destroyed==0,"Moved lease retains final storage owner");
        require(Fixture::header(moved.get(h,0),0)==0,"Live payload retained after wrapper destruction");
    }
    require(f->cleanup_count==1 && f->destroyed==12,"Final lease release cleans before native destruction");complete(f);
}

void failed_growth() {
    for (long failure=0;failure<24;++failure) {
        auto f=std::make_shared<Fixture>(); current=f.get();
        {
            Storage s(3,bindings(f),transitions(f));s.grow(3); auto h=s.acquire(primary);
            void* before;{auto l=s.read();before=l.get(h,0);}
            bool failed=false;allocation_failure=failure;
            try{s.grow(12);}catch(const std::bad_alloc&){failed=true;}
            allocation_failure=-1;
            if(failed){++injected_failures;auto l=s.read();require(l.capacity()==3 && l.live(primary)==1 && l.get(h,0)==before,"Host allocation failure atomicity");}
            require(s.acquire(primary).index==1,"Old lowest free unchanged after growth attempt");
            require(s.try_close(),"Failure scenario close");
        }
        complete(f);
    }
    for(long failure : {0L,1L,4L,14L,23L,44L}) {
        auto f=std::make_shared<Fixture>();current=f.get();
        {
            Storage s(3,bindings(f),transitions(f));s.grow(3);auto h=s.acquire(primary);
            f->constructor_failure=failure;bool failed=false;
            try{s.grow(12);}catch(const std::bad_alloc&){failed=true;}
            require(failed,"Before-constructor injection reached");++injected_failures;
            {auto l=s.read();require(l.capacity()==3 && l.valid(h) && l.live(primary)==1,"Constructor failure preserves published state");}
            require(s.acquire(primary).index==1,"Failure preserves lowest-free heap");s.try_close();
        }
        complete(f);
    }
    require(injected_failures>=16,"Failure injection coverage");
}

void adoption() {
    auto f=std::make_shared<Fixture>();current=f.get();
    {
        auto backing=std::make_shared<OwnedNativeAgentStorage>(3,bindings(f));backing->grow(3);
        Storage::Elements first{};{auto l=backing->read();for(std::size_t p=0;p<pool_count;++p) first[p]=l.physical(p,0);}
        Storage s(3,bindings(f),transitions(f));
        bool failed=false;try{s.adopt_first_block(first,backing,{{{0,0},{}}});}catch(const std::invalid_argument&){failed=true;}
        require(failed,"Duplicate adoption rejected");
        {auto l=s.read();require(l.capacity()==0,"Invalid adoption leaves empty storage");}
        s.adopt_first_block(first,backing,{});backing.reset();
        auto h=s.acquire_at(primary,2);require(h.index==2,"Borrowed explicit acquisition");
        s.grow(6);require(s.acquire(primary).index==0 && s.acquire(secondary).index==0,"Borrowed lowest holes precede growth");
        {auto l=s.read();require(l.physical(0,0)==first[0],"No borrowed movement");}
        require(s.try_close(),"Borrowed close");
    }
    require(f->destroyed==12,"Owned and borrowed runtime owners release after inactive cleanup");complete(f);
}

void adoption_failures_and_live_import() {
    auto f=std::make_shared<Fixture>();current=f.get();
    {
        auto backing=std::make_shared<OwnedNativeAgentStorage>(3,bindings(f));backing->grow(3);
        Storage::Elements first{};
        {auto l=backing->read();for(std::size_t p=0;p<pool_count;++p)first[p]=l.physical(p,0);}
        for(long failure=0;failure<10;++failure) {
            Storage s(3,bindings(f),transitions(f));
            allocation_failure=failure;bool failed=false;
            try{s.adopt_first_block(first,backing,{});}catch(const std::bad_alloc&){failed=true;}
            allocation_failure=-1;
            {auto l=s.read();require(l.capacity()==(failed?0U:3U),"Adoption allocation failure atomicity");}
            if(failed)++injected_failures;
            require(s.try_close(),"Failed or empty adoption close");
        }
        Storage::Elements active{};
        {auto l=backing->read();for(std::size_t p=0;p<pool_count;++p)if(p!=1)active[p]=l.physical(p,2);}
        Fixture::reset(f.get(),{primary,2,1},active); // synthetic preexisting active record
        Storage s(3,bindings(f),transitions(f));
        s.adopt_first_block(first,backing,{{{2},{}}});
        {auto l=s.read();require(l.current(primary,2).index==2 && l.highwater(primary)==3,"Occupied adoption identity");}
        require(s.acquire(primary).index==0,"Occupied adoption builds correct free heap");
        require(s.try_close(),"Occupied adoption receives cleanup before borrowed release");
    }
    require(f->destroyed==6,"Borrowed default destructors see all imported records inactive");complete(f);
}

void no_allocation_and_persistent_lifetime() {
    auto f=std::make_shared<Fixture>();current=f.get();
    {
        Storage s(3,bindings(f),transitions(f));s.grow(3);
        auto h=s.acquire(primary);s.acquire(secondary);
        allocation_failure=0;
        const auto released=s.release(h);
        const bool no_release_allocation=allocation_failure==0;
        const auto closed=s.try_close();
        const bool no_close_allocation=allocation_failure==0;
        allocation_failure=-1;
        require(released && closed && no_release_allocation && no_close_allocation,"Release and active close allocate nothing");
    }
    complete(f);
    struct Token { std::atomic<int>* deaths; ~Token(){++*deaths;} };
    std::atomic<int> deaths{0};
    std::weak_ptr<Fixture> weak_context;
    std::weak_ptr<Token> weak_runtime;
    {
        auto context=std::make_shared<Fixture>();current=context.get();weak_context=context;
        auto runtime=std::make_shared<Token>();runtime->deaths=&deaths;weak_runtime=runtime;
        auto s=std::make_unique<Storage>(3,bindings(context,runtime),transitions(context));
        s->grow(3);auto h=s->acquire(primary);
        auto lease=s->read();auto moved=std::move(lease);
        context.reset();runtime.reset();s.reset();
        require(!weak_context.expired() && !weak_runtime.expired() && deaths==0,"Lease keeps callback context and native runtime owner alive");
        require(moved.valid(h),"Moved lease still owns live identity");
        allocation_failure=0;
        // Final lease destruction performs cleanup and destruction without a host allocation.
    }
    const bool no_final_allocation=allocation_failure==0;allocation_failure=-1;
    require(no_final_allocation && weak_context.expired() && weak_runtime.expired() && deaths==1,"Final owner drains before releasing contexts without allocation");
    ++resets;++cleanups;
}

void varied_model() {
    for(const std::size_t block : {1U,3U,63U,64U,65U}) {
        auto f=std::make_shared<Fixture>();current=f.get();
        {
            Storage s(block,bindings(f),transitions(f));s.grow(block*2);
            std::array<std::set<std::uint64_t>,2> live;
            std::mt19937_64 random(0x44+block);
            std::uint64_t capacity=block*2;
            for(std::size_t step=0;step<240;++step) {
                if(step==80){s.grow(block*3);capacity=block*3;}
                const auto domain=(random()&1)?primary:secondary;
                auto& set=live[static_cast<std::size_t>(domain)];
                if(step%3==0 && !set.empty()) {
                    auto it=set.begin();std::advance(it,static_cast<std::ptrdiff_t>(random()%set.size()));
                    DomainHandle h;{auto l=s.read();h=l.current(domain,*it);}
                    require(s.release(h),"Model release");set.erase(it);
                } else {
                    std::uint64_t expected=0;while(expected<capacity && set.contains(expected))++expected;
                    auto h=s.acquire(domain);require(h.index==(expected==capacity?no_slot:expected),"Model globally lowest free");
                    if(h.index!=no_slot)set.insert(h.index);
                }
                auto l=s.read();require(l.live(domain)==set.size(),"Model live count");
                require(l.highwater(domain)==(set.empty()?0:*set.rbegin()+1),"Model highwater");
                std::vector<std::uint64_t> walked;
                for(auto i=l.next(domain);i!=no_slot;i=l.next(domain,i+1))walked.push_back(i);
                require(walked==std::vector<std::uint64_t>(set.begin(),set.end()),"Model iteration");
            }
            s.try_close();
        }
        complete(f);
    }
}
}
int main() {
    try {
        lowest_and_domains();transition_barriers(false);transition_barriers(true);
        read_lifetime_and_growth();failed_growth();adoption();adoption_failures_and_live_import();
        no_allocation_and_persistent_lifetime();varied_model();
        std::cout<<"PASS native-transaction fixtures assertions="<<assertions<<" resets="<<resets<<" cleanups="<<cleanups<<" failures="<<injected_failures<<"\n";
        return 0;
    } catch(const std::exception& e) {
        allocation_failure=-1;std::cerr<<"FAIL "<<e.what()<<"\n";return 1;
    }
}
