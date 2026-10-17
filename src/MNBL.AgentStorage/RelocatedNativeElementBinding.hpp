#pragma once
#include "VerifiedNativeElementBinding.hpp"

namespace mnbl::storage {
namespace native_binding_detail {
// The pinned image's loader rewrites OptionalHeader.ImageBase. It must equal
// the actual retained module base, not an arbitrary value accepted by masking.
inline bool relocated_image_matches(std::span<const std::byte> image,std::uintptr_t loaded_base) {
    constexpr std::size_t image_base_offset=0x1c8;
    constexpr std::uint64_t preferred_base=0x180000000ull;
    if(image.size()!=native_identity::image_size)return false;
    std::uint64_t recorded_base=0;
    std::memcpy(&recorded_base,image.data()+image_base_offset,sizeof(recorded_base));
    if(recorded_base!=loaded_base)return false;
    for(auto region:native_identity::regions) {
        if(region.offset==0) {
            std::vector<std::byte> header(image.begin(),image.begin()+region.size);
            std::memcpy(header.data()+image_base_offset,&preferred_base,sizeof(preferred_base));
            if(hash(header)!=region.hash)return false;
        } else if(hash(image.subspan(region.offset,region.size))!=region.hash)return false;
    }
    return true;
}
}
inline NativeElementFactory::Bindings bind_relocated_verified_native_elements(HMODULE module) {
    auto owner=std::make_shared<native_binding_detail::ModuleReference>(module);
    std::wstring path(32768,L'\0');
    auto used=GetModuleFileNameW(module,path.data(),static_cast<DWORD>(path.size()));
    if(!used || used>=path.size())throw std::runtime_error("Native module path unavailable or truncated");
    path.resize(used);
    native_binding_detail::File file(path.c_str());
    auto bytes=file.read();
    if(!native_binding_detail::file_matches(bytes))throw std::runtime_error("Native module file identity differs");
    for(auto region:native_identity::regions)
        native_binding_detail::require_readable_image_region(module,region.offset,region.size,region.requires_execute);
    auto base=reinterpret_cast<std::uintptr_t>(module);
    if(!native_binding_detail::relocated_image_matches({reinterpret_cast<const std::byte*>(base),native_identity::image_size},base))
        throw std::runtime_error("Loaded native code/header/unwind or relocated base identity differs");
    return {reinterpret_cast<NativeElementFactory::Constructor>(base+0x606a20),
            reinterpret_cast<NativeElementFactory::Destructor>(base+0x6070c0),
            reinterpret_cast<NativeElementFactory::Constructor>(base+0x5e58e0),
            reinterpret_cast<NativeElementFactory::Constructor>(base+0x5d8860),std::move(owner)};
}
}
