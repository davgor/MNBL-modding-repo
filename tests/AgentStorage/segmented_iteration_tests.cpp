#define main domain_baseline_main
#include "agent_domain_storage_tests.cpp"
#undef main
#include <random>

namespace {
std::size_t iteration_checks=0;
void compare_walk(AgentDomainStorage& storage, AgentDomain domain, const std::set<std::uint64_t>& expected) {
    auto lease=storage.read();
    std::vector<std::uint64_t> forward,reverse;
    for(auto i=lease.next(domain);i!=no_slot;i=lease.next(domain,i+1)) {
        require(forward.size()<expected.size(),"Iteration cycle or extra live bit");
        forward.push_back(i);
        auto handle=lease.current(domain,i);
        require(handle.index==i,"Bitmap exposed inactive slot");
        check_object(lease.get(handle,domain==secondary?1:0),i,domain==secondary?1:0);
    }
    for(auto i=lease.previous(domain,no_slot);i!=no_slot;i=lease.previous(domain,i)) {
        require(reverse.size()<expected.size(),"Reverse iteration cycle");reverse.push_back(i);
    }
    require(forward==std::vector<std::uint64_t>(expected.begin(),expected.end()),"Forward ordered occupancy mismatch");
    require(reverse==std::vector<std::uint64_t>(expected.rbegin(),expected.rend()),"Reverse ordered occupancy mismatch");
    require(lease.live(domain)==expected.size(),"Live count disagrees with walk");
    require(lease.highwater(domain)==(expected.empty()?0:*expected.rbegin()+1),"Highwater disagrees with walk");
    require(lease.next(domain,no_slot)==no_slot && lease.previous(domain,0)==no_slot,"Iteration sentinel bounds");
    for(std::uint64_t from : {0ULL,1ULL,62ULL,63ULL,64ULL,65ULL,66ULL,2040ULL,32767ULL,65535ULL,66049ULL}) {
        const auto next=expected.lower_bound(from),prev=expected.lower_bound(from);
        require(lease.next(domain,from)==(next==expected.end()?no_slot:*next),"Inclusive next boundary");
        require(lease.previous(domain,from)==(prev==expected.begin()?no_slot:*std::prev(prev)),"Exclusive previous boundary");
        ++iteration_checks;
    }
}
void varied_segments() {
    for(std::size_t block : {1U,3U,63U,64U,65U,67U,2040U}) {
        Fixture f;
        {
            AgentDomainStorage s(block,small,f.callbacks());
            std::array<std::set<std::uint64_t>,2> expected;
            for(std::size_t stage=1;stage<=3;++stage) {
                s.grow(block*stage);
                for(auto domain : {primary,secondary}) {
                    auto& set=expected[static_cast<std::size_t>(domain)];
                    for(std::uint64_t index=0;index<block*stage;++index) {
                        if((index+static_cast<std::size_t>(domain))%3!=0 || set.contains(index))continue;
                        auto h=s.acquire_at(domain,index);require(h.index==index,"Explicit iteration fixture insert");set.insert(index);
                    }
                    compare_walk(s,domain,set);
                }
            }
            for(auto domain : {primary,secondary}) {
                auto& set=expected[static_cast<std::size_t>(domain)];
                while(!set.empty()) {
                    const auto index=*set.rbegin();DomainHandle h;
                    {auto lease=s.read();h=lease.current(domain,index);}
                    require(s.release(h),"Reverse highest removal");set.erase(index);
                    if(set.size()%17==0)compare_walk(s,domain,set);
                }
                compare_walk(s,domain,set);
            }
            require(s.try_close(),"Iteration close");
            {auto lease=s.read();require(lease.next(primary)==no_slot && lease.previous(secondary,no_slot)==no_slot,"Closed iteration");}
        }
        f.clean();
    }
}
void randomized_and_wide() {
    Fixture f;
    {
        AgentDomainStorage s(67,small,f.callbacks());s.grow(201);
        std::array<std::set<std::uint64_t>,2> expected;
        std::mt19937_64 random(0x5a17);
        for(std::size_t step=0;step<5000;++step) {
            if(step==1000)s.grow(670);
            const auto domain=(random()&1)?primary:secondary;
            auto& set=expected[static_cast<std::size_t>(domain)];
            const auto index=random()%(step<1000?201:670);
            if(set.contains(index)) {
                DomainHandle h;{auto lease=s.read();h=lease.current(domain,index);}
                require(s.release(h),"Random iteration release");set.erase(index);
            } else {
                require(s.acquire_at(domain,index).index==index,"Random explicit allocation");set.insert(index);
            }
            if(step%29==0) {compare_walk(s,primary,expected[0]);compare_walk(s,secondary,expected[1]);}
        }
        compare_walk(s,primary,expected[0]);compare_walk(s,secondary,expected[1]);
    }
    f.clean();
    Fixture wide;
    {
        AgentDomainStorage s(257,small,wide.callbacks());s.grow(66049);
        std::set<std::uint64_t> expected;
        while(true) {auto h=s.acquire(primary);if(h.index==no_slot)break;expected.insert(h.index);}
        require(expected.size()==66049,"Wide iteration capacity");compare_walk(s,primary,expected);
        for(std::uint64_t i=0;i<66049;i+=2) {
            DomainHandle h;{auto lease=s.read();h=lease.current(primary,i);}
            require(s.release(h),"Wide alternating release");expected.erase(i);
        }
        compare_walk(s,primary,expected);compare_walk(s,secondary,{});
    }
    wide.clean();
}
void adopted_bitmap() {
    Fixture f;std::size_t disposed=0;
    auto owner=std::shared_ptr<std::array<void*,5>>(new std::array<void*,5>{},[&](auto* p){for(auto value:*p)std::free(value);delete p;++disposed;});
    for(std::size_t p=0;p<5;++p) {
        (*owner)[p]=std::malloc(67*32);require((*owner)[p]!=nullptr,"Borrowed iteration allocation");
        for(std::size_t i=0;i<67;++i)new(static_cast<std::byte*>((*owner)[p])+i*32)Object{static_cast<std::byte*>((*owner)[p])+i*32,i,p,i^p^0x123456789ULL};
    }
    {
        AgentDomainStorage s(67,small,f.callbacks());
        s.adopt_first_block(*owner,owner,{{{0,63,64,66},{1,64}}});owner.reset();
        compare_walk(s,primary,{0,63,64,66});compare_walk(s,secondary,{1,64});
        s.grow(201);compare_walk(s,primary,{0,63,64,66});compare_walk(s,secondary,{1,64});
        require(s.acquire_at(primary,67).index==67 && s.acquire_at(secondary,200).index==200,"Adopted suffix insertion");
        compare_walk(s,primary,{0,63,64,66,67});compare_walk(s,secondary,{1,64,200});
        require(s.try_close(),"Adopted iteration close");
    }
    require(disposed==1,"Borrowed bitmap owner leak");f.clean();
}
}
int main() {
    try {
        varied_segments();randomized_and_wide();adopted_bitmap();
        std::cout<<"PASS segmented forward/reverse bitmap iteration: "<<iteration_checks<<" boundary queries;5000 randomized mutations;66049 slots;borrowed adoption;growth;teardown\n";
        std::cout<<"STORAGE BACKEND ONLY: native pointer walks and short-link consumers still require replacement\n";
        return 0;
    } catch(const std::exception& e) {std::cerr<<"FAIL: "<<e.what()<<'\n';return 1;}
}
