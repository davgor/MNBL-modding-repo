#define main native_element_fixture_main
#include "native_element_factory_tests.cpp"
#undef main
#include "../../src/MNBL.AgentGrowthProbe/UnpublishedElementProbe.hpp"
int main(){
    try{
        auto owner=std::make_shared<CodeSlice>();FakeAgentState fixture;current=&fixture;
        NativeElementFactory factory(bindings(owner));
        mnbl::probe::ElementProbeResult result;
        mnbl::probe::exercise_unpublished_elements(factory,result);
        require(result.status==1 && result.stage==8 && result.capacity==90,"Probe completion");
        require(result.header_checks==450 && result.pointer_checks==600,"Probe coverage");
        require(result.allocations==20 && result.releases==20 && result.allocation_attempts==21,"Probe allocator failure/cleanup");
        require(result.constructed==std::array<std::uint64_t,5>{120,97,90,90,90},"Probe constructor coverage");
        require(result.constructed==result.destroyed && fixture.live.empty(),"Probe element cleanup");
        require(result.exhaustion_checked && result.reuse_checked && result.allocation_failure_checked && result.constructor_failure_checked,"Probe gates");
        std::cout<<"PASS unpublished element probe:30->60->90;600 stable-pointer checks;450 inactive headers;allocation and constructor failure;two independent exhaustion/reuse paths;487 completed element callbacks cleaned. Main Agent is synthetic.\n";
        return 0;
    }catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}
}
