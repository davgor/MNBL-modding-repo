// Bounded port of tools/scenebin/{terrain_bin,navmesh_bin,build_slot}.py.
// ZGR6 + RNM1: literal-only LZ4, PNG filter 0 / stored DEFLATE; no external libraries.
#include "surface.hpp"
#include <algorithm>
#include <array>
#include <bit>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <limits>
#include <map>
#include <set>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>

namespace {
namespace fs = std::filesystem;
using Bytes = std::vector<uint8_t>;
[[noreturn]] void fail(const std::string& why) { throw std::runtime_error("Surface: " + why); }
void need(bool ok, const std::string& why) { if (!ok) fail(why); }
void u32(Bytes& b, uint32_t v) { for (int i = 0; i < 4; ++i) b.push_back(static_cast<uint8_t>(v >> (8*i))); }
void u64(Bytes& b, uint64_t v) { for (int i = 0; i < 8; ++i) b.push_back(static_cast<uint8_t>(v >> (8*i))); }
void be32(Bytes& b, uint32_t v) { for (int i = 3; i >= 0; --i) b.push_back(static_cast<uint8_t>(v >> (8*i))); }
uint32_t get32(const Bytes& b, size_t p) { return uint32_t(b.at(p)) | uint32_t(b.at(p+1))<<8 | uint32_t(b.at(p+2))<<16 | uint32_t(b.at(p+3))<<24; }
void append(Bytes& a, const Bytes& b) { a.insert(a.end(), b.begin(), b.end()); }
void ascii(Bytes& b, const std::string& s) { b.insert(b.end(), s.begin(), s.end()); }
Bytes read(const fs::path& p, uintmax_t bound) {
    need(fs::is_regular_file(p), "missing regular input file: " + p.string());
    const auto n = fs::file_size(p); need(n <= bound, "input too large: " + p.string());
    Bytes b(static_cast<size_t>(n)); std::ifstream in(p, std::ios::binary);
    in.exceptions(std::ios::failbit | std::ios::badbit);
    if (n) in.read(reinterpret_cast<char*>(b.data()), static_cast<std::streamsize>(n));
    return b;
}
void write(const fs::path& p, const Bytes& b) {
    std::ofstream out(p, std::ios::binary); out.exceptions(std::ios::badbit | std::ios::failbit);
    out.write(reinterpret_cast<const char*>(b.data()), static_cast<std::streamsize>(b.size())); out.close();
}
void write(const fs::path& p, const std::string& s) { write(p, Bytes(s.begin(), s.end())); }
std::string fixed(double d) { std::ostringstream s; s.imbue(std::locale::classic()); s << std::fixed << std::setprecision(3) << d; return s.str(); }
double even_round(double x) {
    const double f = std::floor(x), d = x-f;
    return d < .5 ? f : d > .5 ? f+1 : std::fmod(f, 2) == 0 ? f : f+1;
}
Bytes lz4(const Bytes& raw) {
    Bytes b; const size_t n = raw.size(); b.push_back(static_cast<uint8_t>(std::min(n, size_t(15)) << 4));
    if (n >= 15) { size_t k=n-15; while(k>=255) { b.push_back(255); k-=255; } b.push_back(static_cast<uint8_t>(k)); }
    append(b, raw); return b;
}
uint32_t crc32(const Bytes& b) {
    uint32_t c=0xffffffffU;
    for (const auto x : b) { c ^= x; for(int i=0;i<8;++i) c=(c>>1) ^ (0xedb88320U & (0U-(c&1))); }
    return ~c;
}
void chunk(Bytes& out, const std::string& type, const Bytes& data) {
    be32(out, static_cast<uint32_t>(data.size())); Bytes t; ascii(t,type); append(t,data); append(out,t); be32(out,crc32(t));
}
Bytes png(int width, int height, int bits, int colour, const Bytes& raw) {
    const size_t stride = static_cast<size_t>(width) * (colour == 6 ? 4 : 1) * static_cast<size_t>(bits/8);
    need(raw.size() == stride*height, "internal PNG size mismatch"); Bytes filtered;
    for (int r=0;r<height;++r) { filtered.push_back(0); filtered.insert(filtered.end(),raw.begin()+r*stride,raw.begin()+(r+1)*stride); }
    Bytes z{0x78,0x01};
    for (size_t p=0;p<filtered.size();) {
        const auto n=static_cast<uint16_t>(std::min(size_t(65535),filtered.size()-p));
        z.push_back(p+n == filtered.size() ? 1 : 0);
        z.push_back(static_cast<uint8_t>(n)); z.push_back(static_cast<uint8_t>(n>>8));
        const auto inv=static_cast<uint16_t>(~n); z.push_back(static_cast<uint8_t>(inv)); z.push_back(static_cast<uint8_t>(inv>>8));
        z.insert(z.end(),filtered.begin()+p,filtered.begin()+p+n); p+=n;
    }
    uint32_t a=1,b=0; for(auto v:filtered) { a=(a+v)%65521; b=(b+a)%65521; } be32(z,(b<<16)|a);
    Bytes out{137,80,78,71,13,10,26,10}, hdr; be32(hdr,static_cast<uint32_t>(width)); be32(hdr,static_cast<uint32_t>(height));
    hdr.insert(hdr.end(),{static_cast<uint8_t>(bits),static_cast<uint8_t>(colour),0,0,0}); chunk(out,"IHDR",hdr);
    Bytes phys; be32(phys,2835); be32(phys,2835); phys.push_back(1); chunk(out,"pHYs",phys); chunk(out,"IDAT",z); chunk(out,"IEND",{}); return out;
}
Bytes lenpng(const Bytes& p) { Bytes out; u32(out,static_cast<uint32_t>(p.size())); append(out,p); return out; }

// Deliberately restricted XML grammar: element-only, quoted attributes, no entities,
// DTD, CDATA, text content or non-declaration PI. Parse structurally, never replace regex text.
struct Xml {
    std::string name; std::map<std::string,std::string> attrs; std::vector<Xml> children;
    std::string get(const std::string& k) const { const auto it=attrs.find(k); need(it!=attrs.end(),"template <"+name+"> missing "+k); return it->second; }
    Xml& one(const std::string& n) { Xml* found=nullptr; for(auto& c:children) if(c.name==n) { need(!found,"duplicate template <"+n+">"); found=&c; } need(found!=nullptr,"template missing <"+n+">"); return *found; }
    void set(const std::string& k,const std::string& v) { (void)get(k); attrs[k]=v; }
    std::string str(int depth=0) const {
        const std::string tab(static_cast<size_t>(depth),'\t'); std::string s=tab+"<"+name;
        for(const auto& [k,v]:attrs) s+=" "+k+"=\""+v+"\"";
        if(children.empty()) return s+"/>\n";
        s+=">\n"; for(const auto& c:children) s+=c.str(depth+1); return s+tab+"</"+name+">\n";
    }
};
class XmlReader {
    const std::string s; size_t p=0, count=0;
    bool at(const std::string& t) const { return s.compare(p,t.size(),t)==0; }
    void ws() { while(p<s.size() && (s[p]==' '||s[p]=='\t'||s[p]=='\r'||s[p]=='\n')) ++p; }
    std::string name() { const size_t start=p; while(p<s.size() && ((s[p]>='a'&&s[p]<='z')||(s[p]>='A'&&s[p]<='Z')||(s[p]>='0'&&s[p]<='9')||s[p]=='_'||s[p]=='-'||s[p]=='.')) ++p; need(p>start,"invalid XML name"); return s.substr(start,p-start); }
    Xml node(int depth) {
        need(depth<32 && ++count<20000,"template XML structural limit"); need(at("<"),"expected XML element"); ++p;
        Xml x; x.name=name();
        while(true) {
            const auto before=p; ws();
            if(at("/>")) { p+=2; return x; } if(at(">")) { ++p; break; }
            need(p>before,"XML attributes require whitespace"); auto k=name(); ws(); need(at("="),"XML attribute missing ="); ++p; ws();
            need(p<s.size() && (s[p]=='\"'||s[p]=='\''),"XML attribute missing quote"); const char quote=s[p++]; const size_t start=p;
            while(p<s.size() && s[p]!=quote) { need(s[p]!='<' && s[p]!='&' && s[p]!='\"' && static_cast<unsigned char>(s[p])>=32,"unsupported XML attribute character/entity"); ++p; }
            need(p<s.size(),"unterminated XML attribute"); need(x.attrs.emplace(k,s.substr(start,p-start)).second,"duplicate XML attribute"); ++p;
        }
        while(true) {
            ws(); if(at("</")) { p+=2; need(name()==x.name,"mismatched XML closing tag"); ws(); need(at(">"),"invalid XML closing tag"); ++p; return x; }
            x.children.push_back(node(depth+1));
        }
    }
public:
    explicit XmlReader(std::string text):s(std::move(text)) {}
    Xml parse() { ws(); if(at("<?xml ")) { const auto end=s.find("?>",p); need(end!=std::string::npos,"invalid XML declaration"); p=end+2; } ws(); auto root=node(0); ws(); need(p==s.size(),"trailing XML content"); return root; }
};
Xml variable(const std::string& name,const std::string& value) { return Xml{"variable",{{"name",name},{"value",value}}, {}}; }
void property(Xml& parent,const std::string& name,const std::string& value) {
    Xml* found=nullptr; for(auto& c:parent.children) if(c.name=="property" && c.get("name")==name) { need(!found,"duplicate property "+name); found=&c; }
    need(found!=nullptr,"template missing property "+name); found->set("value",value);
}
std::string check_template(Xml& root) {
    need(root.name=="scene" && root.get("version")=="2","template requires scene version 2"); const auto name=root.get("name");
    need(name.starts_with("mnbl_") && name.find_first_not_of("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_")==std::string::npos,"template scene name must be owned mnbl_ identifier");
    const std::set<std::string> allowed{"flora_bounding_rect","levels","environment_properties","entities","terrain","editor_data","camera_data","Paths"};
    std::set<std::string> seen; for(const auto& c:root.children) need(allowed.count(c.name) && seen.insert(c.name).second,"unsupported or duplicate scene child "+c.name);
    need(root.one("entities").children.empty(),"template entities must be empty");
    auto& t=root.one("terrain"); need(t.get("enabled")=="true" && t.get("node_dimension_x")=="4" && t.get("node_dimension_y")=="4","template requires enabled 4x4 terrain");
    need(t.get("vista_tileset").empty(),"tileset template incompatible with explicit height/normal/weight maps");
    auto& nodes=t.one("nodes"); need(nodes.children.size()==16,"template needs exactly 16 nodes"); std::set<std::pair<std::string,std::string>> xy;
    for(const auto& n:nodes.children) { const auto x=n.get("idx"), y=n.get("idy"); need(n.name=="node" && x.size()==1 && y.size()==1 && x[0]>='0' && x[0]<='3' && y[0]>='0' && y[0]<='3' && xy.emplace(x,y).second,"incompatible/duplicate template node coordinates"); }
    auto& layers=t.one("layers"); need(layers.get("version")=="1" && layers.children.size()>=2 && layers.children.size()<=16,"template material layer count/version");
    for(size_t k=0;k<layers.children.size();++k) need(layers.children[k].name=="layer","unknown material layer element");
    const std::array<std::string,4> seasons{"summer","fall","winter","spring"};
    for(int k=0;k<2;++k) {
        auto& layer=layers.children[static_cast<size_t>(k)]; need(layer.get("name")== (k==0 ? "grass" : "rock_k"),"required material order: grass, rock_k");
        for(const auto& season:seasons) { auto& s=layer.one(season); need(s.get("is_flora_layer")=="false" && s.get("is_mesh_blend_layer")=="false","used material must not be flora/mesh blend"); need(s.get("physics_material")== (season=="winter" ? "snow" : k==0 ? "grass" : "stone"),"incompatible physical material"); need(s.get("is_enabled")== (k==1 && season=="winter" ? "false" : "true"),"incompatible season material enablement"); }
    }
    // Drop authored paths and editor/camera geography. No template binary geometry is copied.
    std::erase_if(root.children,[](const Xml& x){return x.name=="Paths"||x.name=="editor_data"||x.name=="camera_data";});
    return name;
}

struct Grid {
    int side=0,cells=0,level=0; double extent=0,spacing=0,lo=0,hi=0,step=0;
    std::vector<double> heights,quantized; std::vector<int> q; Bytes normals,weights;
    size_t index(int r,int c) const { return static_cast<size_t>(r*side+c); }
};
Grid make_grid(const Bytes& hf,double extent,double scale) {
    need(hf.size()>=12 && std::string(hf.begin(),hf.begin()+8)=="MNBLHF01","heightfield magic/header must be MNBLHF01"); const auto side=get32(hf,8);
    need(side==129 || side==257,"heightfield gridSide must be 129 or 257"); need(hf.size()==12+size_t(side)*side*8,"heightfield truncated or trailing bytes");
    Grid g; g.side=static_cast<int>(side); g.cells=(g.side-1)/4; g.level=g.cells==32?5:6; g.extent=extent; g.spacing=extent/(g.side-1);
    const size_t n=size_t(side)*side; g.heights.resize(n); g.quantized.resize(n); g.q.resize(n); g.normals.resize(4*n); g.weights.resize(n);
    for(size_t i=0;i<n;++i) { double h; std::memcpy(&h,hf.data()+12+i*8,8); h*=scale; need(std::isfinite(h) && std::abs(h)<=1e6,"nonfinite/overflow or scaled height outside diagnostic +/-1000000 units"); g.heights[i]=h; }
    g.lo=std::floor(*std::min_element(g.heights.begin(),g.heights.end())*1000)/1000;
    g.hi=std::ceil(*std::max_element(g.heights.begin(),g.heights.end())*1000)/1000; if(g.hi<=g.lo) g.hi=g.lo+1;
    g.step=(g.hi-g.lo)/65535;
    for(size_t i=0;i<n;++i) { g.q[i]=static_cast<int>(std::clamp(even_round((g.heights[i]-g.lo)/(g.hi-g.lo)*65535),0.0,65535.0)); g.quantized[i]=g.lo+g.q[i]*g.step; }
    const double k=g.step/(4*g.spacing);
    for(int r=0;r<g.side;++r) for(int c=0;c<g.side;++c) {
        const int r0=std::max(r-1,0),r1=std::min(r+1,g.side-1),c0=std::max(c-1,0),c1=std::min(c+1,g.side-1); const auto i=g.index(r,c);
        const auto q=[&](int y,int x){return g.q[g.index(y,x)];};
        const double dx=((q(r0,c1)+2*q(r,c1)+q(r1,c1))-(q(r0,c0)+2*q(r,c0)+q(r1,c0)))*(k/(c1-c0));
        const double dy=((q(r1,c0)+2*q(r1,c)+q(r1,c1))-(q(r0,c0)+2*q(r0,c)+q(r0,c1)))*(k/(r1-r0));
        const double inv=1/std::sqrt(dx*dx+dy*dy+1);
        g.normals[4*i]=static_cast<uint8_t>(std::clamp(std::floor(-dx*inv*127.5+127.5),0.0,255.0));
        g.normals[4*i+1]=static_cast<uint8_t>(std::clamp(std::floor(-dy*inv*127.5+127.5),0.0,255.0)); g.normals[4*i+2]=63; g.normals[4*i+3]=0;
        const double sx=(g.heights[g.index(r,c1)]-g.heights[g.index(r,c0)])/(2*g.spacing), sy=(g.heights[g.index(r1,c)]-g.heights[g.index(r0,c)])/(2*g.spacing);
        g.weights[i]=static_cast<uint8_t>(std::clamp(even_round((std::hypot(sx,sy)-.6)/.6*255),0.0,255.0));
    }
    return g;
}
struct Section { std::string tag; std::vector<Bytes> parts; };
Bytes terrain(const Grid& g, Xml& nodes) {
    std::array<Section,5> sections{{{"MIDX",{}},{"HGHT",{}},{"NRML",{}},{"WGHT",{}},{"PHYM",{}}}};
    Bytes midx; const auto mp=lenpng(png(1024,1024,8,0,Bytes(1024*1024,255))); for(int i=0;i<4;++i) append(midx,mp); sections[0].parts.push_back(std::move(midx));
    nodes.children.clear(); const int R=g.cells+1;
    for(int iy=0;iy<4;++iy) for(int ix=0;ix<4;++ix) {
        Bytes h,n,w,phys; int qmin=65535,qmax=0; int64_t sum=0;
        for(int r=0;r<R;++r) for(int c=0;c<R;++c) {
            const int gr=iy*g.cells+r,gc=ix*g.cells+c; const auto i=g.index(gr,gc); const int q=g.q[i];
            qmin=std::min(qmin,q); qmax=std::max(qmax,q); h.push_back(static_cast<uint8_t>(q>>8)); h.push_back(static_cast<uint8_t>(q));
            n.insert(n.end(),g.normals.begin()+4*i,g.normals.begin()+4*i+4); w.push_back(g.weights[i]);
            if(r<g.cells && c<g.cells) sum+=std::abs(g.q[g.index(gr,gc+1)]-q)+std::abs(g.q[g.index(gr+1,gc)]-q);
        }
        const bool rock=std::any_of(w.begin(),w.end(),[](uint8_t v){return v!=0;}); const int mask=rock?3:1;
        sections[1].parts.push_back(lenpng(png(R,R,16,0,h))); sections[2].parts.push_back(lenpng(png(R,R,8,6,n)));
        Bytes wg; for(int slot=0;slot<16;++slot) { const bool present=slot==0 || (slot==1&&rock); u32(wg,present?1:0); if(present) append(wg,lenpng(png(R,R,8,0,slot==0?Bytes(static_cast<size_t>(R*R),255):w))); } sections[3].parts.push_back(std::move(wg));
        for(int s=0;s<4;++s) { u32(phys,static_cast<uint32_t>(s)); u32(phys,static_cast<uint32_t>(R*R)); for(int c=0;c<R;++c) for(int r=0;r<R;++r) phys.push_back(w[static_cast<size_t>(r*R+c)]>=128?1:0); } sections[4].parts.push_back(std::move(phys));
        Xml nd{"node",{{"idx",std::to_string(ix)},{"idy",std::to_string(iy)},{"occluders_enabled","true"},{"layer_is_used_mask","true"},{"height_complexity",fixed(std::min(1.0,static_cast<double>(sum)*g.step/(2*g.cells*g.cells*g.spacing)))},{"min_height",fixed(g.lo+qmin*g.step)},{"max_height",fixed(g.lo+qmax*g.step)}}, {}};
        for(const auto* season:{"summer","fall","winter","spring"}) nd.attrs[std::string("layer_is_used_mask_")+season]=std::to_string(mask);
        Xml levels{"texture_levels",{}, {variable("heightmap_level",std::to_string(g.level)),variable("min_heightmap_level","2"),variable("normalmap_level",std::to_string(g.level))}};
        Xml weights{"variable",{{"name","weightmap_levels"}}, {}}; for(int i=0;i<16;++i) weights.children.push_back(Xml{"variable",{{"layer",std::to_string(i)},{"value",std::to_string((mask>>i)&1?g.level:0)}},{}});
        levels.children.push_back(std::move(weights)); nd.children.push_back(std::move(levels)); nodes.children.push_back(std::move(nd));
    }
    Bytes out; ascii(out,"ZGR6RTRN"); u32(out,2); Bytes body;
    for(size_t s=0;s<sections.size();++s) {
        Bytes raw,offsets; for(const auto& p:sections[s].parts) { if(s) u32(offsets,static_cast<uint32_t>(raw.size())); append(raw,p); }
        auto compressed=lz4(raw); Bytes blob; u32(blob,3); u32(blob,0); u64(blob,raw.size()); u64(blob,40+compressed.size()); u64(blob,0); u64(blob,0); append(blob,compressed);
        ascii(out,sections[s].tag); u32(out,1); u32(out,static_cast<uint32_t>(112+body.size())); u32(out,static_cast<uint32_t>(blob.size())); u32(out,static_cast<uint32_t>(raw.size())); append(body,offsets); append(body,blob);
    }
    append(out,body); return out;
}
Bytes navmesh(const Grid& g) {
    constexpr int N=33; const int stride=(g.side-1)/32; const double cell=g.extent/32;
    const auto h=[&](int r,int c){return g.quantized[g.index(r*stride,c*stride)];};
    Bytes payload; ascii(payload,"NMG9"); u32(payload,N*N);
    for(int r=0;r<N;++r) for(int c=0;c<N;++c) for(double v:{c*cell,r*cell,h(r,c)+.2}) u32(payload,std::bit_cast<uint32_t>(static_cast<float>(v)));
    std::vector<std::array<int,6>> edges; std::map<std::pair<int,int>,int> edgeids; Bytes faces;
    const auto face=[&](std::array<int,3> loop,int group) {
        u32(faces,3); for(int v:loop) u32(faces,static_cast<uint32_t>(v));
        for(size_t j=0;j<3;++j) { const int a=loop[j],b=loop[(j+1)%3]; const std::pair<int,int> key{std::min(a,b),std::max(a,b)};
            auto it=edgeids.find(key); if(it==edgeids.end()) { const int id=static_cast<int>(edges.size()); it=edgeids.emplace(key,id).first; edges.push_back({-1,a,b,-1,-1,0}); } u32(faces,static_cast<uint32_t>(it->second)); }
        for(int v:{2,0,group,0,-1}) u32(faces,static_cast<uint32_t>(v)); faces.push_back(0);
    };
    for(int r=0;r<32;++r) for(int c=0;c<32;++c) {
        const double a=h(r,c),b=h(r,c+1),d=h(r+1,c),e=h(r+1,c+1); const int group=(std::max({a,b,d,e})-std::min({a,b,d,e}))/cell>1?7:1;
        const int v=r*N+c; if(std::abs(a-e)<=std::abs(b-d)) { face({v,v+1,v+N+1},group); face({v,v+N+1,v+N},group); } else { face({v,v+1,v+N},group); face({v+1,v+N+1,v+N},group); }
    }
    u32(payload,static_cast<uint32_t>(edges.size())); for(const auto& e:edges) for(int v:e) u32(payload,static_cast<uint32_t>(v)); u32(payload,2048); append(payload,faces); u32(payload,32); payload.resize(payload.size()+256,0);
    const auto compressed=lz4(payload); const auto size=40+compressed.size(); Bytes out; ascii(out,"RNM1"); u32(out,static_cast<uint32_t>(size)); u32(out,3); u32(out,0); u64(out,payload.size()); u64(out,size); u64(out,0); u64(out,0); append(out,compressed); return out;
}
std::string metadata(const Grid& g,const std::string& name,double scale,bool compiled_weights=false) {
    std::ostringstream s; s.imbue(std::locale::classic()); s<<std::setprecision(17);
    s<<"{\"schema\":\"mnbl.native-surface\",\"version\":1,\"sceneName\":\""<<name<<"\",\"nodes\":[4,4],\"nodeSize\":"<<g.extent/4<<",\"extent\":["<<g.extent<<","<<g.extent<<"],\"minHeight\":"<<g.lo<<",\"maxHeight\":"<<g.hi<<",\"gridSide\":"<<g.side<<",\"heightmapLevel\":"<<g.level<<",\"spacing\":"<<g.spacing<<",\"verticalScale\":"<<scale<<",\"quantizationStep\":"<<g.step<<",\"quantizationMaxError\":"<<g.step/2;
    s<<",\"quantizedHeightfield\":\"quantized-heightfield.bin\",\"heightLayout\":\"row-major +Y rows +X columns; world-unit binary64\",\"normalSource\":\"stitched quantized grid Sobel; RG=floor(normalXY*127.5+127.5), B=63 A=0; official kernel approximate\",\"weightSource\":\""<<(compiled_weights?"compiled rock_k weights (--surface-weights); grass base 255":"unquantized scaled grid slope; grass base 255; rock threshold 0.6")<<"\",\"physicsSource\":\"dominant grass/rock alpha, x-major, repeated four seasons\",\"heightComplexity\":\"inferred mean absolute slope clamped to 1; official meaning unknown\",\"navmesh\":{\"gridSide\":33,\"cell\":"<<g.extent/32<<",\"faces\":2048,\"zOffset\":0.2,\"source\":\"quantized sampled grid; auto diagonal; slope>1 group7 else group1\",\"interiorGrounding\":\"vertices grounded only; coarse triangle interiors may differ from terrain\"},\"nativeValidated\":false,\"templatePolicy\":\"preserve owned scene name, environment and material references; drop paths/editor/camera; clear outer mesh; regenerate all binary geometry except passive atmosphere\",\"points\":[";
    std::vector<std::pair<double,double>> points{{0,0},{g.extent,0},{0,g.extent},{g.extent,g.extent}};
    for(double fy:{.05,.27,.5,.73,.95}) for(double fx:{.05,.27,.5,.73,.95}) points.emplace_back(even_round(fx*g.extent*1000)/1000,even_round(fy*g.extent*1000)/1000);
    for(auto p:{std::pair{160.,90.},std::pair{200.,120.},std::pair{100.,80.}}) if(p.first<=g.extent && p.second<=g.extent) points.push_back(p);
    bool first=true; for(const auto& [x,y]:points) {
        const double c=x/g.spacing,r=y/g.spacing; const int c0=std::min(static_cast<int>(c),g.side-2),r0=std::min(static_cast<int>(r),g.side-2); const double fc=c-c0,fr=r-r0;
        const double h00=g.quantized[g.index(r0,c0)],h10=g.quantized[g.index(r0,c0+1)],h01=g.quantized[g.index(r0+1,c0)],h11=g.quantized[g.index(r0+1,c0+1)];
        const double bi=h00*(1-fc)*(1-fr)+h10*fc*(1-fr)+h01*(1-fc)*fr+h11*fc*fr;
        const double ta=fc>=fr?h00+(h10-h00)*fc+(h11-h10)*fr:h00+(h11-h01)*fc+(h01-h00)*fr;
        const double tb=fc+fr<=1?h00+(h10-h00)*fc+(h01-h00)*fr:h11+(h01-h11)*(1-fc)+(h10-h11)*(1-fr);
        if(!first) s<<","; first=false; s<<"{\"x\":"<<x<<",\"y\":"<<y<<",\"bilinear\":"<<bi<<",\"triangleA\":"<<ta<<",\"triangleB\":"<<tb<<"}";
    }
    s<<"]}\n"; return s.str();
}
double positive(const std::string& s,const std::string& flag) {
    size_t end=0; double v=0; try { v=std::stod(s,&end); } catch(const std::exception&) { fail(flag+" requires a positive finite number"); }
    need(end==s.size() && std::isfinite(v) && v>0,flag+" requires a positive finite number"); return v;
}
} // namespace

int surface_main(int argc,char** argv) {
    static_assert(std::endian::native==std::endian::little && sizeof(double)==8 && std::numeric_limits<double>::is_iec559);
    const std::set<std::string> flags{"--surface-heightfield","--surface-template","--surface-output","--surface-extent","--surface-scale","--surface-weights"}; std::map<std::string,std::string> args;
    need(argc==11 || argc==13,"usage: --surface-heightfield MNBLHF01 --surface-template OWNED_DIRECTORY --surface-output NEW_DIRECTORY --surface-extent POSITIVE --surface-scale POSITIVE [--surface-weights MNBLWT01]");
    for(int i=1;i<argc;i+=2) need(flags.count(argv[i]) && args.emplace(argv[i],argv[i+1]).second,"unknown or duplicate surface argument");
    const double extent=positive(args.at("--surface-extent"),"extent"),scale=positive(args.at("--surface-scale"),"scale");
    need(extent>=.004 && extent<=1000000 && std::abs(extent/4*1000-even_round(extent/4*1000))<1e-7,"extent/4 must be representable at XML 0.001 precision, extent in [0.004,1000000]");
    const fs::path output=fs::absolute(args.at("--surface-output")),templ=fs::canonical(args.at("--surface-template"));
    need(!fs::exists(output) && !fs::is_symlink(output),"output must be absent: "+output.string()); need(fs::is_directory(templ),"template must be a directory");
    for(const auto* file:{"scene.xscene","terrain.bin","navmesh.bin","flora.bin","atmosphere.xml"}) need(fs::is_regular_file(templ/file) && !fs::is_symlink(templ/file),std::string("template missing regular non-symlink file: ")+file);
    const auto source=read(args.at("--surface-heightfield"),12+257*257*8); auto g=make_grid(source,extent,scale);
    need(argc==11 || args.count("--surface-weights"),"the optional argument is --surface-weights");
    if(args.count("--surface-weights")) {
        // MMR-26 compiled rock_k weights replace the slope rule: "MNBLWT01", u32 side, side*side bytes, same row order.
        const auto w=read(args.at("--surface-weights"),12+257*257);
        need(w.size()>=12 && std::string(w.begin(),w.begin()+8)=="MNBLWT01" && get32(w,8)==static_cast<uint32_t>(g.side) && w.size()==12+g.weights.size(),"weights must be MNBLWT01 with the heightfield's side");
        std::copy(w.begin()+12,w.end(),g.weights.begin());
    }
    const auto xmlbytes=read(templ/"scene.xscene",2*1024*1024); auto xml=XmlReader(std::string(xmlbytes.begin(),xmlbytes.end())).parse(); const auto name=check_template(xml);
    const auto atmosphere=read(templ/"atmosphere.xml",2*1024*1024);
    auto& rect=xml.one("flora_bounding_rect"); rect.set("min","0.000, 0.000"); rect.set("max",fixed(extent)+", "+fixed(extent));
    auto& env=xml.one("environment_properties"); property(env,"physics_world_min","-50.000, -50.000"); property(env,"physics_world_max_",fixed(extent+50)+", "+fixed(extent+50));
    auto& t=xml.one("terrain"); t.set("node_size",fixed(extent/4)); t.set("min_height",fixed(g.lo)); t.set("max_height",fixed(g.hi));
    auto& outer=t.one("outer_mesh"); size_t cleared=0; for(auto& c:outer.children) if(c.name=="variable" && c.get("name")=="outer_mesh_name") { c.set("value",""); ++cleared; } need(cleared==1,"template must contain exactly one outer_mesh_name");
    const auto terrainbytes=terrain(g,t.one("nodes")); const auto navbytes=navmesh(g); Bytes hf; ascii(hf,"MNBLHF01"); u32(hf,static_cast<uint32_t>(g.side)); for(double h:g.quantized) u64(hf,std::bit_cast<uint64_t>(h));
    Bytes flora; ascii(flora,"FLR2"); u32(flora,4); u32(flora,0);
    // Validate/compute everything before reserving the destination. Never replace prior results.
    fs::create_directories(output.parent_path()); need(fs::create_directory(output),"output appeared during generation");
    // surface.json is the completion marker, written last. A failed IO leaves an incomplete
    // directory for diagnosis which is deliberately not accepted as an overwrite destination.
    write(output/"terrain.bin",terrainbytes); write(output/"navmesh.bin",navbytes); write(output/"flora.bin",flora); write(output/"atmosphere.xml",atmosphere);
    write(output/"scene.xscene",std::string("<?xml version=\"1.0\"?>\n")+xml.str()); write(output/"quantized-heightfield.bin",hf); write(output/"surface.json",metadata(g,name,scale,args.count("--surface-weights")!=0));
    std::cout<<"Surface bundle completed: "<<g.side<<"x"<<g.side<<", extent "<<extent<<", scene "<<name<<". Native loading not tested by this encoder.\n"; return 0;
}
