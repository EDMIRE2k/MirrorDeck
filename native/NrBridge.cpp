// Feature-18 parameter contract documented by RenoDX-DLSSNR-Nexus (MIT).
// Calls live in a separately named module to satisfy the runtime caller ABI.
#include <windows.h>
#include <d3d12.h>
#include <nvsdk_ngx.h>

#define API extern "C" __declspec(dllexport)
// Preserve the bridge as the immediate caller; tail calls erase its ABI identity.
#pragma optimize("", off)
using InitFn = NVSDK_NGX_Result(__cdecl*)(unsigned long long, const wchar_t*, ID3D12Device*, NVSDK_NGX_Version, const NVSDK_NGX_Parameter*);
using CreateFn = NVSDK_NGX_Result(__cdecl*)(ID3D12GraphicsCommandList*, NVSDK_NGX_Feature, const NVSDK_NGX_Parameter*, NVSDK_NGX_Handle**);
using EvalFn = NVSDK_NGX_Result(__cdecl*)(ID3D12GraphicsCommandList*, const NVSDK_NGX_Handle*, const NVSDK_NGX_Parameter*, PFN_NVSDK_NGX_ProgressCallback);
using ReleaseFn = NVSDK_NGX_Result(__cdecl*)(NVSDK_NGX_Handle*);
using ShutdownFn = NVSDK_NGX_Result(__cdecl*)(ID3D12Device*);
static HMODULE runtime;
static InitFn init;
static CreateFn create;
static EvalFn evaluate;
static ReleaseFn release;
static ShutdownFn shutdownRuntime;

API NVSDK_NGX_Result MdNrInitialize(const wchar_t* path, const wchar_t* cache, ID3D12Device* device) {
    if (!runtime) {
        runtime = LoadLibraryExW(path, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (!runtime) return NVSDK_NGX_Result_FAIL_FeatureNotFound;
        init = reinterpret_cast<InitFn>(GetProcAddress(runtime, "NVSDK_NGX_D3D12_Init_Ext"));
        create = reinterpret_cast<CreateFn>(GetProcAddress(runtime, "NVSDK_NGX_D3D12_CreateFeature"));
        evaluate = reinterpret_cast<EvalFn>(GetProcAddress(runtime, "NVSDK_NGX_D3D12_EvaluateFeature"));
        release = reinterpret_cast<ReleaseFn>(GetProcAddress(runtime, "NVSDK_NGX_D3D12_ReleaseFeature"));
        shutdownRuntime = reinterpret_cast<ShutdownFn>(GetProcAddress(runtime, "NVSDK_NGX_D3D12_Shutdown1"));
    }
    if (!init || !create || !evaluate || !release || !shutdownRuntime) return NVSDK_NGX_Result_FAIL_FeatureNotSupported;
    return init(0, cache, device, NVSDK_NGX_Version_API, nullptr);
}
API NVSDK_NGX_Result MdNrCreate(ID3D12GraphicsCommandList* list, NVSDK_NGX_Parameter* params, NVSDK_NGX_Handle** feature) {
    return create(list, static_cast<NVSDK_NGX_Feature>(18), params, feature);
}
API NVSDK_NGX_Result MdNrEvaluate(ID3D12GraphicsCommandList* list, NVSDK_NGX_Handle* feature, NVSDK_NGX_Parameter* params) {
    return evaluate(list, feature, params, nullptr);
}
API NVSDK_NGX_Result MdNrRelease(NVSDK_NGX_Handle* feature) { return release(feature); }
API NVSDK_NGX_Result MdNrShutdown(ID3D12Device* device) { return shutdownRuntime(device); }
