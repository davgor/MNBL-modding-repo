#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "UnpublishedElementProbe.hpp"
#include "../MNBL.AgentStorage/VerifiedNativeElementBinding.hpp"
#include <sstream>

// Read-only diagnostic. Never constructs, activates, patches or unloads engine objects.
extern "C" __declspec(dllexport) std::uint64_t MNBLElementProbeResultSize() noexcept {
    return sizeof(mnbl::probe::ElementProbeResult);
}
extern "C" __declspec(dllexport) int MNBLElementProbe(mnbl::probe::ElementProbeResult* result,std::uint64_t bytes) noexcept {
    if(!result || bytes!=sizeof(*result))return 1;
    *result={};result->status=2;
    try {
        using namespace mnbl::storage;
        auto module=GetModuleHandleW(L"TaleWorlds.Native.dll");
        native_binding_detail::ModuleReference owner(module);
        std::wstring path(32768,L'\0');
        auto used=GetModuleFileNameW(module,path.data(),static_cast<DWORD>(path.size()));
        if(!used || used>=path.size())throw std::runtime_error("Native path unavailable");
        path.resize(used);
        native_binding_detail::File file(path.c_str());
        auto raw=file.read();
        if(!native_binding_detail::file_matches(raw))throw std::runtime_error("Native file differs");
        // Parsing is permitted only after the complete immutable file identity matches.
        auto dos=reinterpret_cast<const IMAGE_DOS_HEADER*>(raw.data());
        auto nt=reinterpret_cast<const IMAGE_NT_HEADERS64*>(raw.data()+dos->e_lfanew);
        auto sections=IMAGE_FIRST_SECTION(nt);
        std::ostringstream report;
        report<<"base="<<std::hex<<reinterpret_cast<std::uintptr_t>(module)<<" preferred="<<nt->OptionalHeader.ImageBase;
        unsigned regionIndex=0;
        for(auto region:native_identity::regions) {
            native_binding_detail::require_readable_image_region(module,region.offset,region.size,region.requires_execute);
            std::size_t rawOffset=region.offset;
            if(region.offset>=nt->OptionalHeader.SizeOfHeaders) {
                bool found=false;
                for(unsigned i=0;i<nt->FileHeader.NumberOfSections;++i) {
                    const auto& s=sections[i];
                    if(region.offset>=s.VirtualAddress && region.offset-s.VirtualAddress<s.SizeOfRawData) {
                        rawOffset=s.PointerToRawData+region.offset-s.VirtualAddress;found=true;break;
                    }
                }
                if(!found)throw std::runtime_error("Diagnostic region has no raw mapping");
            }
            if(rawOffset>raw.size() || region.size>raw.size()-rawOffset)throw std::runtime_error("Diagnostic raw range overflow");
            auto actual=reinterpret_cast<const unsigned char*>(module)+region.offset;
            auto expected=reinterpret_cast<const unsigned char*>(raw.data())+rawOffset;
            std::size_t differences=0;std::ostringstream first;
            for(std::size_t i=0;i<region.size;++i)if(actual[i]!=expected[i]) {
                if(differences<12)first<<" "<<std::hex<<region.offset+i<<":"<<unsigned(expected[i])<<">"<<unsigned(actual[i]);
                ++differences;
            }
            report<<" R"<<regionIndex++<<" n="<<std::dec<<differences<<first.str();
        }
        strncpy_s(result->error,report.str().c_str(),_TRUNCATE);
        return 3; // Deliberately cannot satisfy the allocation-probe success contract.
    }catch(const std::exception& error){strncpy_s(result->error,error.what(),_TRUNCATE);return 3;}
    catch(...){strcpy_s(result->error,"Unknown diagnostic C++ exception");return 4;}
}
