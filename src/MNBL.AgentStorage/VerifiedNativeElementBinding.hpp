#pragma once

#include "NativeElementFactory.hpp"
#include "../../build/agent-storage/native_element_identity.hpp"
#include <Windows.h>
#include <bcrypt.h>
#include <span>
#include <string>

namespace mnbl::storage {
namespace native_binding_detail {
inline std::array<unsigned char,32> hash(std::span<const std::byte> bytes) {
    if(bytes.size()>ULONG_MAX)throw std::length_error("Native hash input too large");
    std::array<unsigned char,32> result{};
    if(BCryptHash(BCRYPT_SHA256_ALG_HANDLE,nullptr,0,
        const_cast<PUCHAR>(reinterpret_cast<const unsigned char*>(bytes.data())),
        static_cast<ULONG>(bytes.size()),result.data(),static_cast<ULONG>(result.size()))<0)
        throw std::runtime_error("Native SHA256 failed");
    return result;
}
// Pure identity checks also used by the offline mutation tests. They never
// execute the supplied bytes or turn an arbitrary buffer into a module handle.
inline bool file_matches(std::span<const std::byte> bytes) {
    return bytes.size()==native_identity::file_size && hash(bytes)==native_identity::file_hash;
}
inline bool image_matches(std::span<const std::byte> bytes) {
    if(bytes.size()!=native_identity::image_size)return false;
    for(auto region:native_identity::regions)
        if(hash(bytes.subspan(region.offset,region.size))!=region.hash)return false;
    return true;
}
class ModuleReference {
    HMODULE module_=nullptr;
public:
    explicit ModuleReference(HMODULE candidate) {
        if(!candidate || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
            reinterpret_cast<LPCWSTR>(candidate),&module_))
            throw std::invalid_argument("Native module is not loaded");
        if(module_!=candidate) {FreeLibrary(module_);module_=nullptr;throw std::invalid_argument("Expected native module base");}
    }
    ~ModuleReference(){if(module_)FreeLibrary(module_);}
    ModuleReference(const ModuleReference&)=delete;
    ModuleReference& operator=(const ModuleReference&)=delete;
};
class File {
    HANDLE handle_=INVALID_HANDLE_VALUE;
public:
    explicit File(const wchar_t* path) {
        // Keep writers/deletion excluded while reading the exact file identity.
        handle_=CreateFileW(path,GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
        if(handle_==INVALID_HANDLE_VALUE)throw std::runtime_error("Cannot lock native module file for verification");
    }
    ~File(){if(handle_!=INVALID_HANDLE_VALUE)CloseHandle(handle_);}
    File(const File&)=delete;
    File& operator=(const File&)=delete;
    std::vector<std::byte> read() const {
        LARGE_INTEGER size{};
        if(!GetFileSizeEx(handle_,&size) || size.QuadPart!=native_identity::file_size)
            throw std::runtime_error("Wrong native file size");
        std::vector<std::byte> bytes(native_identity::file_size);
        DWORD got=0;
        if(!ReadFile(handle_,bytes.data(),static_cast<DWORD>(bytes.size()),&got,nullptr) || got!=bytes.size())
            throw std::runtime_error("Incomplete native module read");
        return bytes;
    }
};
inline void require_readable_image_region(HMODULE module,std::size_t offset,std::size_t size,bool requires_execute=false) {
    auto base=reinterpret_cast<std::uintptr_t>(module);
    if(base>UINTPTR_MAX-native_identity::image_size || offset>native_identity::image_size || size>native_identity::image_size-offset)
        throw std::invalid_argument("Native image address overflow");
    auto cursor=base+offset;const auto end=cursor+size;
    while(cursor<end) {
        MEMORY_BASIC_INFORMATION info{};
        if(VirtualQuery(reinterpret_cast<const void*>(cursor),&info,sizeof(info))!=sizeof(info) ||
           info.State!=MEM_COMMIT || info.Type!=MEM_IMAGE || info.AllocationBase!=module ||
           (info.Protect&(PAGE_GUARD|PAGE_NOACCESS))!=0)
            throw std::runtime_error("Native region is not a readable loaded image");
        const auto protection=info.Protect&0xff;
        if(protection!=PAGE_READONLY && protection!=PAGE_READWRITE && protection!=PAGE_WRITECOPY &&
           protection!=PAGE_EXECUTE_READ && protection!=PAGE_EXECUTE_READWRITE && protection!=PAGE_EXECUTE_WRITECOPY)
            throw std::runtime_error("Native region lacks read permission");
        if(requires_execute && protection!=PAGE_EXECUTE_READ && protection!=PAGE_EXECUTE_READWRITE && protection!=PAGE_EXECUTE_WRITECOPY)
            throw std::runtime_error("Native code region lacks execute permission");
        const auto begin=reinterpret_cast<std::uintptr_t>(info.BaseAddress);
        if(info.RegionSize>UINTPTR_MAX-begin || begin+info.RegionSize<=cursor)
            throw std::runtime_error("Invalid native memory region");
        cursor=begin+info.RegionSize;
    }
}
}

// Call only after the engine is initialized and while module/code mutation is
// excluded. GetModuleHandleEx retains the actual loaded module; no LoadLibrary,
// initializer execution, patch installation, or worker synchronization occurs.
inline NativeElementFactory::Bindings bind_verified_native_elements(HMODULE module) {
    auto owner=std::make_shared<native_binding_detail::ModuleReference>(module);
    std::wstring path(32768,L'\0');
    const auto used=GetModuleFileNameW(module,path.data(),static_cast<DWORD>(path.size()));
    if(!used || used>=path.size())throw std::runtime_error("Native module path unavailable or truncated");
    path.resize(used);
    native_binding_detail::File file(path.c_str());
    const auto bytes=file.read();
    if(!native_binding_detail::file_matches(bytes))throw std::runtime_error("Native module file identity differs");
    for(auto region:native_identity::regions)
        native_binding_detail::require_readable_image_region(module,region.offset,region.size,region.requires_execute);
    const auto base=reinterpret_cast<std::uintptr_t>(module);
    if(!native_binding_detail::image_matches({reinterpret_cast<const std::byte*>(base),native_identity::image_size}))
        throw std::runtime_error("Loaded native code/header/unwind identity differs");
    return {reinterpret_cast<NativeElementFactory::Constructor>(base+0x606a20),
            reinterpret_cast<NativeElementFactory::Destructor>(base+0x6070c0),
            reinterpret_cast<NativeElementFactory::Constructor>(base+0x5e58e0),
            reinterpret_cast<NativeElementFactory::Constructor>(base+0x5d8860),std::move(owner)};
}
}
