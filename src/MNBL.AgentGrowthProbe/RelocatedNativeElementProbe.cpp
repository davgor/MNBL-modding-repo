#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "UnpublishedElementProbe.hpp"
#include "../MNBL.AgentStorage/RelocatedNativeElementBinding.hpp"
#include <atomic>

namespace {std::atomic_flag active=ATOMIC_FLAG_INIT;}
extern "C" __declspec(dllexport) std::uint64_t MNBLElementProbeResultSize()noexcept {
    return sizeof(mnbl::probe::ElementProbeResult);
}
extern "C" __declspec(dllexport) int MNBLElementProbe(mnbl::probe::ElementProbeResult* result,std::uint64_t bytes)noexcept {
    if(!result || bytes!=sizeof(*result))return 1;
    *result={};
    if(active.test_and_set()){result->status=2;strcpy_s(result->error,"Native element probe is already active");return 2;}
    struct End{~End(){active.clear();}}end;
    try{
        mnbl::storage::NativeElementFactory factory(mnbl::storage::bind_relocated_verified_native_elements(GetModuleHandleW(L"TaleWorlds.Native.dll")));
        mnbl::probe::exercise_unpublished_elements(factory,*result);
        return 0;
    }catch(const std::exception& error){result->status=2;strncpy_s(result->error,error.what(),_TRUNCATE);return 3;}
    catch(...){result->status=2;strcpy_s(result->error,"Native element probe caught an unknown C++ exception");return 4;}
}
