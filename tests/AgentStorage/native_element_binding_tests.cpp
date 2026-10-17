#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "../../src/MNBL.AgentStorage/VerifiedNativeElementBinding.hpp"
#include <fstream>
#include <iostream>
#include <iterator>
using namespace mnbl::storage;
namespace {
void require(bool value,const char* message){if(!value)throw std::runtime_error(message);}
template<class Function> void rejects(Function action,const char* message){bool failed=false;try{action();}catch(const std::exception&){failed=true;}require(failed,message);}
void run(){
    std::ifstream stream("build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0",std::ios::binary);
    require(stream.good(),"Offline original unavailable");
    std::vector<char> raw((std::istreambuf_iterator<char>(stream)),{});
    auto bytes=std::span<const std::byte>(reinterpret_cast<const std::byte*>(raw.data()),raw.size());
    require(native_binding_detail::file_matches(bytes),"Original native file pin");
    const auto dos=reinterpret_cast<const IMAGE_DOS_HEADER*>(raw.data());
    const auto nt=reinterpret_cast<const IMAGE_NT_HEADERS64*>(raw.data()+dos->e_lfanew);
    // Parsing follows the exact full-file SHA check, not arbitrary PE input.
    std::vector<std::byte> image(native_identity::image_size);
    std::memcpy(image.data(),raw.data(),nt->OptionalHeader.SizeOfHeaders);
    auto section=IMAGE_FIRST_SECTION(nt);
    for(unsigned i=0;i<nt->FileHeader.NumberOfSections;++i)
        std::memcpy(image.data()+section[i].VirtualAddress,raw.data()+section[i].PointerToRawData,section[i].SizeOfRawData);
    require(native_binding_detail::image_matches(image),"Original mapped-layout identity");
    unsigned mutations=0;
    // Cover every selected constructor, destructor, the first/last code bytes,
    // region edges, and independent points throughout the executable section.
    for(auto region:native_identity::regions){
        std::vector<std::size_t> offsets{region.offset,region.offset+region.size-1};
        for(std::size_t n=1;n<16;++n)offsets.push_back(region.offset+n*region.size/16);
        for(auto offset:offsets){image[offset]^=std::byte{1};require(!native_binding_detail::image_matches(image),"Changed image accepted");image[offset]^=std::byte{1};++mutations;}
    }
    for(auto offset:{0x606a20U,0x6070c0U,0x5e58e0U,0x5d8860U}){
        image[offset]^=std::byte{0x80};require(!native_binding_detail::image_matches(image),"Changed entry accepted");image[offset]^=std::byte{0x80};++mutations;
    }
    require(!native_binding_detail::image_matches(std::span<const std::byte>(image).first(image.size()-1)),"Short image accepted");
    require(!native_binding_detail::file_matches(bytes.first(bytes.size()-1)),"Short file accepted");
    raw[0]^=1;require(!native_binding_detail::file_matches(bytes),"Changed file accepted");raw[0]^=1;
    // Runtime global data is intentionally not treated as immutable identity.
    image[0xcef000]^=std::byte{0x80};require(native_binding_detail::image_matches(image),"Runtime data wrongly pinned");
    rejects([]{bind_verified_native_elements(nullptr);},"Null module accepted");
    auto own=GetModuleHandleW(nullptr);
    rejects([own]{bind_verified_native_elements(own);},"Wrong loaded executable accepted");
    rejects([own]{bind_verified_native_elements(reinterpret_cast<HMODULE>(reinterpret_cast<std::uintptr_t>(own)+1));},"Interior module address accepted");
    rejects([]{bind_verified_native_elements(GetModuleHandleW(L"kernel32.dll"));},"Wrong loaded DLL accepted");
    native_binding_detail::require_readable_image_region(own,0,64);
    MEMORY_BASIC_INFORMATION header_region{};
    require(VirtualQuery(own,&header_region,sizeof(header_region))==sizeof(header_region) && header_region.Protect==PAGE_READONLY,"Nonexecutable loaded-image fixture");
    rejects([own]{native_binding_detail::require_readable_image_region(own,0,64,true);},"Nonexecutable loaded-image code accepted");
    const auto function_offset=reinterpret_cast<std::uintptr_t>(&run)-reinterpret_cast<std::uintptr_t>(own);
    native_binding_detail::require_readable_image_region(own,function_offset,1,true);
    rejects([own]{native_binding_detail::require_readable_image_region(own,SIZE_MAX,1);},"Overflow offset accepted");
    auto private_memory=VirtualAlloc(nullptr,4096,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE);
    require(private_memory!=nullptr,"Private fixture allocation");
    try{rejects([private_memory]{native_binding_detail::require_readable_image_region(static_cast<HMODULE>(private_memory),0,64);},"Private allocation accepted as module");}
    catch(...){VirtualFree(private_memory,0,MEM_RELEASE);throw;}
    VirtualFree(private_memory,0,MEM_RELEASE);
    require(mutations==55,"Mutation count");
    std::cout<<"PASS native binding: exact original file/mapped image;55 code/header/unwind mutations;4 wrong-module rejections;short buffers;private-memory rejection;nonexecutable MEM_IMAGE rejected;executable MEM_IMAGE accepted;runtime data excluded. No native module loaded or executed.\n";
}
}
int main(){try{run();return 0;}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
