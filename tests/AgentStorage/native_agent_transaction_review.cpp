#define main original_transaction_fixture_main
#include "native_agent_transaction_tests.cpp"
#undef main

int main() {
    if (original_transaction_fixture_main()!=0) return 1;
    try {
        auto f=std::make_shared<Fixture>();current=f.get();
        Storage storage(17,bindings(f),transitions(f));storage.grow(34);
        std::array<void*,5> first{};
        {auto lease=storage.read();for(std::size_t p=0;p<5;++p)first[p]=lease.physical(p,0);}
        std::atomic<bool> start{false},failed{false};
        std::vector<std::future<void>> workers;
        for(unsigned thread=0;thread<4;++thread)workers.push_back(std::async(std::launch::async,[&,thread]{
            while(!start.load())std::this_thread::yield();
            try {
                for(unsigned iteration=0;iteration<500;++iteration) {
                    auto domain=(thread&1)?primary:secondary;
                    auto handle=storage.acquire(domain);
                    if(handle.index==no_slot){failed=true;return;}
                    {
                        auto lease=storage.read();
                        if(!lease.valid(handle)){failed=true;return;}
                        for(std::size_t pool=0;pool<5;++pool)
                            if(domain==secondary?pool==1:pool!=1)
                                if(Fixture::header(lease.get(handle,pool),pool)!=static_cast<std::int32_t>(handle.index))failed=true;
                    }
                    if(!storage.release(handle)){failed=true;return;}
                    {auto lease=storage.read();if(lease.valid(handle))failed=true;}
                }
            }catch(...){failed=true;}
        }));
        auto growth=std::async(std::launch::async,[&]{
            while(!start.load())std::this_thread::yield();
            try {for(std::uint64_t blocks=3;blocks<=59;++blocks)storage.grow(blocks*17);}
            catch(...){failed=true;}
        });
        start=true;for(auto& worker:workers)worker.get();growth.get();
        require(!failed,"Concurrent transition/growth invariant");
        {
            auto lease=storage.read();
            require(lease.capacity()==1003 && lease.live(primary)==0 && lease.live(secondary)==0,"Concurrent final ledgers");
            for(std::size_t p=0;p<5;++p)require(lease.physical(p,0)==first[p],"Concurrent growth moved first block");
        }
        require(f->reset_count==2000 && f->cleanup_count==2000,"Concurrent exact transition coverage");
        require(storage.acquire(primary).index==0 && storage.acquire(secondary).index==0,"Lowest free after concurrent reuse");
        require(storage.try_close(),"Concurrent test close");
        require(f->reset_count==2002 && f->cleanup_count==2002 && f->destroyed==2006,"Concurrent final active cleanup and all Agent destruction");
        std::cout<<"PASS independent concurrent transactions:4workers;2000 acquire/release pairs;57growth steps;1003slots;stablefirstblock;2002balancedtransitions\n";
        return 0;
    }catch(const std::exception& error){std::cerr<<error.what()<<'\n';return 2;}
}
