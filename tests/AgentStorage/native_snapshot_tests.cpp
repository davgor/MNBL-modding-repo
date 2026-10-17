#include "../../src/MNBL.AgentStorage/NativePoolSnapshot.hpp"
#include <iostream>
#include <algorithm>

using namespace mnbl::storage;
namespace {
constexpr std::array<std::size_t, 5> widths{0xc60,0xc60,0x230,0x2c8,0x2c8};
constexpr std::array<std::size_t, 5> headers{0x10,0x10,0x1c,0,0};
void require(bool value, const char* why) { if (!value) throw std::runtime_error(why); }
struct Snapshot {
    std::size_t capacity;
    std::array<std::vector<std::byte>, 5> records, links;
    std::array<NativePoolSnapshot, 5> views{};
    explicit Snapshot(std::size_t count) : capacity(count) {
        for (std::size_t p=0;p<5;++p) {
            records[p].resize(count*widths[p]);links[p].resize(count*2);
            views[p].records=records[p];views[p].signed_links=links[p];
            set(p,{});
        }
    }
    void header(std::size_t p, std::size_t i, std::int32_t value) {
        std::memcpy(records[p].data()+i*widths[p]+headers[p],&value,sizeof(value));
    }
    void link(std::size_t p,std::size_t i,std::int16_t value) { std::memcpy(links[p].data()+i*2,&value,2); }
    void set(std::size_t p,const std::vector<std::uint64_t>& live) {
        for (std::size_t i=0;i<capacity;++i) { header(p,i,-1);link(p,i,-1); }
        for (std::size_t j=0;j<live.size();++j) {
            header(p,static_cast<std::size_t>(live[j]),static_cast<std::int32_t>(live[j]));
            link(p,static_cast<std::size_t>(live[j]),static_cast<std::int16_t>(j+1<live.size()?live[j+1]:capacity+1));
        }
        views[p].live_count=static_cast<std::int32_t>(live.size());
        views[p].highwater=static_cast<std::int32_t>(live.empty()?0:live.back()+1);
    }
};
template<class F> void rejects(F action) {
    bool caught=false;try { action(); } catch(const std::invalid_argument&) { caught=true; }
    require(caught,"Invalid snapshot accepted");
}
}
int main() {
    try {
        std::size_t mutations=0;
        for (auto capacity : {3U,2040U,4080U}) {
            Snapshot s(capacity);
            auto empty=validate_native_pool_snapshot(s.views,capacity);
            require(empty[0].empty()&&empty[1].empty(),"Empty snapshot occupancy");
            const std::vector<std::uint64_t> primary{0,capacity-1},secondary{1};
            auto reset=[&]{for(std::size_t p=0;p<5;++p)s.set(p,p==1?secondary:primary);};
            reset();auto imported=validate_native_pool_snapshot(s.views,capacity);
            require(imported[0]==primary&&imported[1]==secondary,"Independent imported domain sets");
            for(std::size_t p=0;p<5;++p) {
                auto invalid=[&]{rejects([&]{validate_native_pool_snapshot(s.views,capacity);});++mutations;reset();};
                s.header(p,p==1?1:0,2);invalid();
                s.header(p,p==1?1:0,-1);invalid();
                s.link(p,p==1?1:0,-1);invalid();
                s.link(p,p==1?0:1,0);invalid();
                s.link(p,p==1?1:capacity-1,static_cast<std::int16_t>(capacity));invalid();
                ++s.views[p].live_count;invalid();
                s.views[p].highwater=0;invalid();
                s.views[p].live_count=-1;invalid();
                s.views[p].highwater=static_cast<std::int32_t>(capacity+1);invalid();
                auto saved=s.views[p].records;s.views[p].records=saved.first(saved.size()-1);
                invalid();s.views[p].records=saved;
                auto saved_links=s.views[p].signed_links;s.views[p].signed_links=saved_links.first(saved_links.size()-1);
                invalid();s.views[p].signed_links=saved_links;
            }
            for(std::size_t p=2;p<5;++p) {
                s.set(p,{1});rejects([&]{validate_native_pool_snapshot(s.views,capacity);});++mutations;reset();
            }
            rejects([&]{validate_native_pool_snapshot(s.views,0);});
            rejects([&]{validate_native_pool_snapshot(s.views,32767);});
        }
        require(mutations==174,"Mutation coverage changed");
        std::cout<<"PASS empty/sparse primary and independent secondary snapshots at3/2040/4080;174 malformed header/link/counter/dimension/coupling mutations rejected\n";
        std::cout<<"OFFLINE SNAPSHOT VALIDATION ONLY: no engine lifetime acquisition or mutation barrier\n";
        return 0;
    } catch(const std::exception& e) { std::cerr<<"FAIL: "<<e.what()<<'\n';return 1; }
}
