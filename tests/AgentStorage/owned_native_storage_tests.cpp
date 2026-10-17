#define main native_element_fixture_main
#include "native_element_factory_tests.cpp"
#undef main
#include "../../src/MNBL.AgentStorage/OwnedNativeAgentStorage.hpp"
#include <optional>
#include <type_traits>
namespace {
struct RuntimeOwner {
    std::shared_ptr<CodeSlice> code;
    unsigned& releases;
    FakeAgentState& fixture;
    ~RuntimeOwner(){require(fixture.live.empty(),"Runtime released before native destructors");++releases;}
};
void retained_factory(){
    FakeAgentState fixture;current=&fixture;unsigned releases=0;
    auto runtime=std::make_shared<RuntimeOwner>(std::make_shared<CodeSlice>(),releases,fixture);
    std::weak_ptr<RuntimeOwner> weak=runtime;
    auto storage=std::make_unique<OwnedNativeAgentStorage>(3,bindings(runtime));runtime.reset();
    storage->grow(3);auto handle=storage->acquire_at(AgentDomain::Primary,2);
    {auto lease=storage->read();require(lease.live(AgentDomain::Primary)==1 && lease.highwater(AgentDomain::Primary)==3,"Owned ledger forwarding");
     require(lease.next(AgentDomain::Primary)==2 && lease.previous(AgentDomain::Primary,3)==2,"Owned iteration forwarding");}
    storage->grow(9);
    std::optional<OwnedNativeAgentStorage::ReadLease> first(storage->read());
    auto pointer=first->get(handle,0);
    std::optional<OwnedNativeAgentStorage::ReadLease> second(std::move(*first));first.reset();
    require(!storage->try_close(),"Owned close ignored lease");
    storage.reset();
    require(!weak.expired() && releases==0 && fixture.live.size()==18,"Wrapper destruction lost native lifetime");
    require(second->capacity()==9 && second->valid(handle) && second->get(handle,0)==pointer && second->current(AgentDomain::Primary,2).generation==handle.generation,"Retained lease invalid after wrapper destruction");
    require(second->physical(0,2)==pointer,"Retained physical address changed");
    second.reset();require(weak.expired() && releases==1 && fixture.live.empty() && fixture.completed==fixture.destroyed,"Final lease did not release native lifetime exactly once");
}
void failed_growth_and_close(){
    FakeAgentState fixture;current=&fixture;unsigned releases=0;
    auto runtime=std::make_shared<RuntimeOwner>(std::make_shared<CodeSlice>(),releases,fixture);
    std::weak_ptr<RuntimeOwner> weak=runtime;
    {
        OwnedNativeAgentStorage storage(3,bindings(runtime));runtime.reset();storage.grow(3);
        fixture.fail_at=fixture.attempts+4;
        bool failed=false;try{storage.grow(9);}catch(const std::runtime_error&){failed=true;}
        require(failed && fixture.live.size()==6 && !weak.expired(),"Owned rollback lifetime");
        fixture.fail_at=SIZE_MAX;storage.grow(6);
        auto h=storage.acquire(AgentDomain::Secondary);require(storage.release(h),"Owned release forwarding");
        require(storage.try_close() && fixture.live.empty() && !weak.expired(),"Close did not retain callable runtime until owner destruction");
        {auto lease=storage.read();require(lease.capacity()==0,"Closed owned capacity");}
    }
    require(weak.expired() && releases==1,"Closed owner leak");
}
}
int main(){
    static_assert(!std::is_copy_constructible_v<OwnedNativeAgentStorage::ReadLease>);
    static_assert(!std::is_move_assignable_v<OwnedNativeAgentStorage::ReadLease>);
    try{retained_factory();failed_growth_and_close();std::cout<<"PASS owned native storage: retained factory/module through moved lease;wrapper destroyed before lease;rollback;close;exact final destruction order.\n";return 0;}
    catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}
}
