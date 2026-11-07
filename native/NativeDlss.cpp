// This software contains source code provided by NVIDIA Corporation.
// NGX helper APIs and Optical Flow headers retain their respective licenses in vendor/.
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <nvsdk_ngx_helpers.h>
#include <nvsdk_ngx_helpers_d3d.h>
#include <nvOpticalFlowD3D12.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <filesystem>
#include <memory>
#include <stdexcept>
#include <string>

using Microsoft::WRL::ComPtr;
extern "C" __declspec(dllimport) NVSDK_NGX_Result MdNrInitialize(const wchar_t*, const wchar_t*, ID3D12Device*);
extern "C" __declspec(dllimport) NVSDK_NGX_Result MdNrCreate(ID3D12GraphicsCommandList*, NVSDK_NGX_Parameter*, NVSDK_NGX_Handle**);
extern "C" __declspec(dllimport) NVSDK_NGX_Result MdNrEvaluate(ID3D12GraphicsCommandList*, NVSDK_NGX_Handle*, NVSDK_NGX_Parameter*);
extern "C" __declspec(dllimport) NVSDK_NGX_Result MdNrRelease(NVSDK_NGX_Handle*);
extern "C" __declspec(dllimport) NVSDK_NGX_Result MdNrShutdown(ID3D12Device*);
struct NrOptions {
    int preset, style, workHeight, autoMask, uiCorrection;
    float intensity, localTone, localStructure, skinStructure, colorStrength, paperWhite, transfer;
    int passes;
};
NVSDK_NGX_Result __cdecl NrRatio(NVSDK_NGX_Parameter* p) { p->Set("DLSSNR.ScalingRatio", 1.f); return NVSDK_NGX_Result_Success; }
namespace {
thread_local std::string error;
void Require(bool ok, const std::string& message) { if (!ok) throw std::runtime_error(message); }
void Hr(HRESULT result, const char* operation) {
    if (FAILED(result)) { char code[32]; sprintf_s(code, " (0x%08X)", static_cast<unsigned>(result)); throw std::runtime_error(std::string(operation) + code); }
}
void Ngx(NVSDK_NGX_Result result, const char* operation) {
    if (NVSDK_NGX_FAILED(result)) { char code[32]; sprintf_s(code, " (0x%08X)", static_cast<unsigned>(result)); throw std::runtime_error(std::string(operation) + code); }
}
void Flow(NV_OF_STATUS result, const char* operation) { Require(result == NV_OF_SUCCESS, std::string(operation) + " (NVOF " + std::to_string(result) + ")"); }
constexpr auto Common = D3D12_RESOURCE_STATE_COMMON;
constexpr auto Read = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
constexpr auto Uav = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;

struct Context {
    ComPtr<ID3D12Device> device;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12Fence> fence, ofFence;
    HANDLE event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    UINT64 submitted = 0, opticalValue = 0;
    ComPtr<ID3D12CommandAllocator> allocators[2];
    ComPtr<ID3D12GraphicsCommandList> list;
    UINT64 slotDone[2]{};
    unsigned slot = 0, frame = 0, w = 0, h = 0, ow = 0, oh = 0;
    ComPtr<ID3D12RootSignature> root;
    ComPtr<ID3D12PipelineState> orient, guides, stabilize;
    ComPtr<ID3D12DescriptorHeap> heap;
    UINT descriptorSize = 0;
    ComPtr<ID3D12Resource> raw, scratch, input[2], flow, cost, color, motion, depth, bias, output, stableOutput, uploads[2];
    std::array<BYTE*, 2> mapped{};
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint{};
    HMODULE ofModule = nullptr;
    NV_OF_D3D12_API_FUNCTION_LIST of{};
    NvOFHandle session = nullptr;
    NvOFGPUBufferHandle inputHandles[2]{}, flowHandle = nullptr, costHandle = nullptr;
    NVSDK_NGX_Parameter* params = nullptr;
    NVSDK_NGX_Handle* feature = nullptr;
    NVSDK_NGX_Handle* repeatFeatures[3]{};
    ComPtr<ID3D12Resource> repeatOutput;
    bool ngxReady = false, outputReadable = false, recording = false;
    LARGE_INTEGER last{}, frequency{};
    std::wstring runtimePath;
    std::filesystem::path tracePath;
    std::string status;
    NVSDK_NGX_DLSS_Hint_Render_Preset model = NVSDK_NGX_DLSS_Hint_Render_Preset_M;
    bool highMotion = false;
    float detailProtection = 1.f;
    bool neural = false, nrReady = false;
    UINT rawW = 0, rawH = 0;
    NrOptions nr{};

    void Trace(const char* text) {
        if (tracePath.empty()) return;
        FILE* file = nullptr;
        if (_wfopen_s(&file, tracePath.c_str(), L"a") == 0 && file) { fprintf(file, "%s\n", text); fclose(file); }
    }

    ~Context() {
        try { Clear(); } catch (...) { }
        if (params) NVSDK_NGX_D3D12_DestroyParameters(params);
        if (nrReady) MdNrShutdown(device.Get());
        if (ngxReady) NVSDK_NGX_D3D12_Shutdown1(device.Get());
        if (ofModule) FreeLibrary(ofModule);
        if (event) CloseHandle(event);
    }
    void Wait(UINT64 value, ID3D12Fence* target = nullptr) {
        if (!target) target = fence.Get();
        if (target->GetCompletedValue() >= value) return;
        Hr(target->SetEventOnCompletion(value, event), "Set completion event");
        Require(WaitForSingleObject(event, 5000) == WAIT_OBJECT_0, "Native DLSS GPU wait timed out");
    }
    UINT64 Submit() {
        Hr(list->Close(), "Close native commands");
        recording = false;
        ID3D12CommandList* lists[] = { list.Get() };
        queue->ExecuteCommandLists(1, lists);
        Hr(queue->Signal(fence.Get(), ++submitted), "Signal native commands");
        return submitted;
    }
    void Idle() {
        if (!queue || !fence) return;
        Hr(queue->Signal(fence.Get(), ++submitted), "Drain native DLSS");
        Wait(submitted);
        if (ofFence && opticalValue) Wait(opticalValue, ofFence.Get());
    }
    void Barrier(ID3D12Resource* resource, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after) {
        D3D12_RESOURCE_BARRIER b{};
        b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Transition = { resource, D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES, before, after };
        list->ResourceBarrier(1, &b);
    }
    ComPtr<ID3D12Resource> Texture(UINT width, UINT height, DXGI_FORMAT format, bool uav = false) {
        D3D12_HEAP_PROPERTIES hp{}; hp.Type = D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC d{};
        d.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D; d.Width = width; d.Height = height;
        d.DepthOrArraySize = 1; d.MipLevels = 1; d.Format = format; d.SampleDesc.Count = 1;
        if (uav) d.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
        ComPtr<ID3D12Resource> resource;
        Hr(device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &d, Common, nullptr, IID_PPV_ARGS(&resource)), "Create native texture");
        return resource;
    }
    D3D12_CPU_DESCRIPTOR_HANDLE Cpu(unsigned index) {
        auto result = heap->GetCPUDescriptorHandleForHeapStart(); result.ptr += SIZE_T(index) * descriptorSize; return result;
    }
    D3D12_GPU_DESCRIPTOR_HANDLE Gpu(unsigned index) {
        auto result = heap->GetGPUDescriptorHandleForHeapStart(); result.ptr += UINT64(index) * descriptorSize; return result;
    }
    void Srv(unsigned index, ID3D12Resource* resource, DXGI_FORMAT format) {
        D3D12_SHADER_RESOURCE_VIEW_DESC d{}; d.Format = format; d.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
        d.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING; d.Texture2D.MipLevels = 1;
        device->CreateShaderResourceView(resource, &d, Cpu(index));
    }
    void UavView(unsigned index, ID3D12Resource* resource, DXGI_FORMAT format) {
        D3D12_UNORDERED_ACCESS_VIEW_DESC d{}; d.Format = format; d.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
        device->CreateUnorderedAccessView(resource, nullptr, &d, Cpu(index));
    }
    void Dispatch(ID3D12PipelineState* pipeline, unsigned descriptors, const float* constants, bool atOutput = false) {
        ID3D12DescriptorHeap* heaps[] = { heap.Get() };
        list->SetDescriptorHeaps(1, heaps); list->SetComputeRootSignature(root.Get()); list->SetPipelineState(pipeline);
        list->SetComputeRootDescriptorTable(0, Gpu(descriptors));
        list->SetComputeRootDescriptorTable(1, Gpu(descriptors + 4));
        list->SetComputeRoot32BitConstants(2, 8, constants, 0);
        list->Dispatch(((atOutput ? ow : w) + 7) / 8, ((atOutput ? oh : h) + 7) / 8, 1);
    }
    void Register(ID3D12Resource* resource, NvOFGPUBufferHandle& handle) {
        NV_OF_REGISTER_RESOURCE_PARAMS_D3D12 p{};
        p.resource = resource; p.hOFGpuBuffer = &handle;
        p.inputFencePoint = { ofFence.Get(), opticalValue };
        p.outputFencePoint = { ofFence.Get(), opticalValue + 1 };
        Flow(of.nvOFRegisterResourceD3D12(session, &p), "Register optical flow resource");
        opticalValue++;
    }
    void Unregister(NvOFGPUBufferHandle& handle) {
        if (!handle) return;
        NV_OF_UNREGISTER_RESOURCE_PARAMS_D3D12 p{}; p.hOFGpuBuffer = handle;
        of.nvOFUnregisterResourceD3D12(&p); handle = nullptr;
    }
    void Clear() {
        if (w) Trace("Clear: idle");
        Idle();
        if (w) Trace("Clear: release textures");
        if (list && allocators[0]) {
            if (recording) { list->Close(); recording = false; }
            Hr(list->Reset(allocators[0].Get(), nullptr), "Clear native command references");
            Hr(list->Close(), "Close cleared native commands");
            for (auto& allocator : allocators) Hr(allocator->Reset(), "Clear native allocator references");
        }
        if (feature) { if (neural) MdNrRelease(feature); else NVSDK_NGX_D3D12_ReleaseFeature(feature); feature = nullptr; }
        for (auto& repeat : repeatFeatures) { if (repeat) MdNrRelease(repeat); repeat = nullptr; }
        repeatOutput.Reset();
        Unregister(inputHandles[0]); Unregister(inputHandles[1]); Unregister(flowHandle); Unregister(costHandle);
        for (unsigned i = 0; i < 2; i++) {
            if (uploads[i] && mapped[i]) uploads[i]->Unmap(0, nullptr);
            mapped[i] = nullptr; uploads[i].Reset(); input[i].Reset();
        }
        raw.Reset(); scratch.Reset(); flow.Reset(); cost.Reset(); color.Reset(); motion.Reset(); depth.Reset(); bias.Reset(); output.Reset(); stableOutput.Reset();
        // Release texture and command references while the optical-flow session's
        // driver state is still alive; reversing this order crashes on resize.
        if (w) Trace("Clear: destroy optical session");
        if (session) { of.nvOFDestroy(session); session = nullptr; }
        if (w) Trace("Clear: finished");
        w = h = ow = oh = frame = 0; outputReadable = false;
        status = "Native DLSS idle";
    }
    void Initialize(ID3D12Device* d, ID3D12CommandQueue* q, const wchar_t* runtime, const wchar_t* cache, bool useNr = false) {
        neural = useNr;
        Require(d && q && event, "Invalid native DLSS device or synchronization handle");
        device = d; queue = q; runtimePath = runtime;
        if (GetEnvironmentVariableW(L"MIRRORDECK_NATIVE_DLSS_TRACE", nullptr, 0)) tracePath = std::filesystem::path(cache) / L"native-dlss.log";
        QueryPerformanceFrequency(&frequency);
        Hr(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence)), "Create native fence");
        Hr(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&ofFence)), "Create optical fence");
        for (auto& allocator : allocators) Hr(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator)), "Create allocator");
        Hr(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocators[0].Get(), nullptr, IID_PPV_ARGS(&list)), "Create native command list");
        Hr(list->Close(), "Close initial native list");
        D3D12_DESCRIPTOR_HEAP_DESC hd{}; hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV; hd.NumDescriptors = 40; hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        Hr(device->CreateDescriptorHeap(&hd, IID_PPV_ARGS(&heap)), "Create native descriptor heap");
        descriptorSize = device->GetDescriptorHandleIncrementSize(hd.Type);
        D3D12_DESCRIPTOR_RANGE ranges[2]{};
        ranges[0] = { D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 4, 0, 0, 0 };
        ranges[1] = { D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 4, 0, 0, 0 };
        D3D12_ROOT_PARAMETER roots[3]{};
        for (int i = 0; i < 2; i++) { roots[i].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE; roots[i].DescriptorTable = { 1, &ranges[i] }; }
        roots[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS; roots[2].Constants = { 0, 0, 8 };
        D3D12_STATIC_SAMPLER_DESC sampler{}; sampler.Filter = D3D12_FILTER_MIN_MAG_MIP_LINEAR;
        sampler.AddressU = sampler.AddressV = sampler.AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        sampler.MaxLOD = D3D12_FLOAT32_MAX; sampler.MaxAnisotropy = 1; sampler.ComparisonFunc = D3D12_COMPARISON_FUNC_ALWAYS;
        D3D12_ROOT_SIGNATURE_DESC rd{}; rd.NumParameters = 3; rd.pParameters = roots; rd.NumStaticSamplers = 1; rd.pStaticSamplers = &sampler;
        ComPtr<ID3DBlob> blob, errors;
        Hr(D3D12SerializeRootSignature(&rd, D3D_ROOT_SIGNATURE_VERSION_1, &blob, &errors), "Serialize native root signature");
        Hr(device->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(), IID_PPV_ARGS(&root)), "Create native root signature");
        for (int i = 0; i < 3; i++) {
            ComPtr<ID3DBlob> shader;
            auto file = std::filesystem::path(runtimePath) / (i == 2 ? (neural ? L"NrResolve.hlsl" : L"Stabilize.hlsl") : L"Guides.hlsl");
            HRESULT result = D3DCompileFromFile(file.c_str(), nullptr, D3D_COMPILE_STANDARD_FILE_INCLUDE, i == 0 ? "Orient" : i == 1 ? "Guides" : "Stabilize", "cs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &shader, &errors);
            if (FAILED(result) && errors) throw std::runtime_error(static_cast<const char*>(errors->GetBufferPointer()));
            Hr(result, "Compile native motion guide shader");
            D3D12_COMPUTE_PIPELINE_STATE_DESC pd{}; pd.pRootSignature = root.Get(); pd.CS = { shader->GetBufferPointer(), shader->GetBufferSize() };
            auto& pipeline = i == 0 ? orient : i == 1 ? guides : stabilize;
            Hr(device->CreateComputePipelineState(&pd, IID_PPV_ARGS(&pipeline)), "Create guide pipeline");
        }
        ofModule = LoadLibraryExW(L"nvofapi64.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        Require(ofModule != nullptr, "NVIDIA Optical Flow driver is unavailable");
        auto create = reinterpret_cast<decltype(&NvOFAPICreateInstanceD3D12)>(GetProcAddress(ofModule, "NvOFAPICreateInstanceD3D12"));
        Require(create != nullptr, "NVIDIA Optical Flow DX12 API is unavailable");
        Flow(create(NV_OF_API_VERSION, &of), "Initialize optical flow API");
        const wchar_t* paths[] = { runtimePath.c_str() };
        NVSDK_NGX_FeatureCommonInfo info{}; info.PathListInfo.Path = paths; info.PathListInfo.Length = 1;
        Ngx(NVSDK_NGX_D3D12_Init_with_ProjectID("0b2a6062-61de-49df-8bd6-69a30d958976", NVSDK_NGX_ENGINE_TYPE_CUSTOM, "MirrorDeck-1.0.34", cache, d, &info), "Initialize NVIDIA NGX");
        ngxReady = true;
        Ngx(NVSDK_NGX_D3D12_GetCapabilityParameters(&params), "Get DLSS capabilities");
        int available = 0; params->Get(NVSDK_NGX_Parameter_SuperSampling_Available, &available);
        Require(available != 0, "DLSS Super Resolution is unavailable on this GPU/driver");
        if (neural) {
            auto nrPath = std::filesystem::path(runtimePath) / L"nvngx_dlssnr.dll";
            Ngx(MdNrInitialize(nrPath.c_str(), cache, d), "Initialize native Neural Rendering");
            nrReady = true;
        }
    }
    void Configure(UINT width, UINT height, UINT outWidth, UINT outHeight, int requestedModel, int motionQuality, UINT uploadWidth, UINT uploadHeight) {
        float ratio = float(outWidth) / std::max(1u, width);
        auto selected = requestedModel == 2 ? NVSDK_NGX_DLSS_Hint_Render_Preset_K :
            requestedModel == 3 ? NVSDK_NGX_DLSS_Hint_Render_Preset_L : NVSDK_NGX_DLSS_Hint_Render_Preset_M;
        if (requestedModel == 0) selected = ratio > 2.6f ? NVSDK_NGX_DLSS_Hint_Render_Preset_L :
            ratio > 1.8f ? NVSDK_NGX_DLSS_Hint_Render_Preset_M : NVSDK_NGX_DLSS_Hint_Render_Preset_K;
        bool high = motionQuality == 1;
        if (feature && w == width && h == height && ow == outWidth && oh == outHeight && rawW == uploadWidth && rawH == uploadHeight && model == selected && highMotion == high) return;
        Trace("Configure: clear"); Clear(); w = width; h = height; ow = outWidth; oh = outHeight;
        rawW = uploadWidth; rawH = uploadHeight;
        model = selected; highMotion = high;
        Require(w >= 160 && h >= 120 && w <= 7680 && h <= 4320 && ow >= w && oh >= h && ow <= 7680 && oh <= 4320, "Unsupported native DLSS source/output dimensions");
        Trace("Configure: create optical session"); Flow(of.nvCreateOpticalFlowD3D12(device.Get(), &session), "Create optical flow session");
        NV_OF_INIT_PARAMS fp{}; fp.width = w; fp.height = h; fp.outGridSize = NV_OF_OUTPUT_VECTOR_GRID_SIZE_4;
        fp.mode = NV_OF_MODE_OPTICALFLOW; fp.perfLevel = highMotion ? NV_OF_PERF_LEVEL_SLOW : NV_OF_PERF_LEVEL_MEDIUM;
        fp.enableOutputCost = NV_OF_TRUE; fp.inputBufferFormat = NV_OF_BUFFER_FORMAT_ABGR8;
        fp.predDirection = NV_OF_PRED_DIRECTION_FORWARD;
        Trace("Configure: initialize optical session"); Flow(of.nvOFInit(session, &fp), "Initialize optical flow session");
        raw = Texture(rawW, rawH, DXGI_FORMAT_B8G8R8A8_UNORM);
        scratch = Texture(w, h, DXGI_FORMAT_R8G8B8A8_UNORM, true);
        for (int i = 0; i < 2; i++) { input[i] = Texture(w, h, DXGI_FORMAT_R8G8B8A8_UNORM); Register(input[i].Get(), inputHandles[i]); }
        flow = Texture((w + 3) / 4, (h + 3) / 4, DXGI_FORMAT_R16G16_SINT); Register(flow.Get(), flowHandle);
        cost = Texture((w + 3) / 4, (h + 3) / 4, DXGI_FORMAT_R8_UINT); Register(cost.Get(), costHandle);
        Trace("Configure: registered resources"); Wait(opticalValue, ofFence.Get());
        color = Texture(w, h, DXGI_FORMAT_R16G16B16A16_FLOAT, true);
        motion = Texture(w, h, DXGI_FORMAT_R16G16_FLOAT, true);
        depth = Texture(w, h, DXGI_FORMAT_R32_FLOAT, true);
        bias = Texture(w, h, DXGI_FORMAT_R8_UNORM, true);
        output = Texture(neural ? w : ow, neural ? h : oh, DXGI_FORMAT_R16G16B16A16_FLOAT, true);
        if (neural && nr.passes > 1) repeatOutput = Texture(w, h, DXGI_FORMAT_R16G16B16A16_FLOAT, true);
        stableOutput = Texture(ow, oh, DXGI_FORMAT_R16G16B16A16_FLOAT, true);
        auto rawDesc = raw->GetDesc(); UINT64 bytes = 0;
        device->GetCopyableFootprints(&rawDesc, 0, 1, 0, &footprint, nullptr, nullptr, &bytes);
        for (unsigned i = 0; i < 2; i++) {
            D3D12_HEAP_PROPERTIES hp{}; hp.Type = D3D12_HEAP_TYPE_UPLOAD;
            D3D12_RESOURCE_DESC bd{}; bd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER; bd.Width = bytes; bd.Height = 1;
            bd.DepthOrArraySize = 1; bd.MipLevels = 1; bd.SampleDesc.Count = 1; bd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
            Hr(device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &bd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&uploads[i])), "Create DLSS upload");
            D3D12_RANGE empty{};
            Hr(uploads[i]->Map(0, &empty, reinterpret_cast<void**>(&mapped[i])), "Map DLSS upload");
            unsigned start = 8 + i * 8;
            Srv(start, input[i].Get(), DXGI_FORMAT_R8G8B8A8_UNORM); Srv(start + 1, input[1 - i].Get(), DXGI_FORMAT_R8G8B8A8_UNORM);
            Srv(start + 2, flow.Get(), DXGI_FORMAT_R16G16_SINT); Srv(start + 3, cost.Get(), DXGI_FORMAT_R8_UINT);
            UavView(start + 4, color.Get(), DXGI_FORMAT_R16G16B16A16_FLOAT); UavView(start + 5, motion.Get(), DXGI_FORMAT_R16G16_FLOAT);
            UavView(start + 6, depth.Get(), DXGI_FORMAT_R32_FLOAT); UavView(start + 7, bias.Get(), DXGI_FORMAT_R8_UNORM);
            unsigned stabilizeStart = 24 + i * 8;
            Srv(stabilizeStart, neural ? raw.Get() : input[i].Get(), neural ? DXGI_FORMAT_B8G8R8A8_UNORM : DXGI_FORMAT_R8G8B8A8_UNORM);
            Srv(stabilizeStart + 1, neural ? input[i].Get() : input[1 - i].Get(), DXGI_FORMAT_R8G8B8A8_UNORM);
            Srv(stabilizeStart + 2, output.Get(), DXGI_FORMAT_R16G16B16A16_FLOAT);
            Srv(stabilizeStart + 3, bias.Get(), DXGI_FORMAT_R8_UNORM);
            UavView(stabilizeStart + 4, stableOutput.Get(), DXGI_FORMAT_R16G16B16A16_FLOAT);
        }
        Srv(0, raw.Get(), DXGI_FORMAT_B8G8R8A8_UNORM); UavView(4, scratch.Get(), DXGI_FORMAT_R8G8B8A8_UNORM);
        Hr(allocators[0]->Reset(), "Reset initialization allocator"); Hr(list->Reset(allocators[0].Get(), nullptr), "Reset DLSS initialization list");
        recording = true;
        for (const char* key : { NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_DLAA, NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Quality,
            NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Balanced, NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Performance,
            NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_UltraPerformance }) params->Set(key, static_cast<unsigned>(model));
        NVSDK_NGX_DLSS_Create_Params cp{};
        cp.Feature.InWidth = w; cp.Feature.InHeight = h; cp.Feature.InTargetWidth = ow; cp.Feature.InTargetHeight = oh;
        cp.Feature.InPerfQualityValue = ratio < 1.05f ? NVSDK_NGX_PerfQuality_Value_DLAA : ratio > 2.6f ? NVSDK_NGX_PerfQuality_Value_UltraPerformance :
            ratio > 1.8f ? NVSDK_NGX_PerfQuality_Value_MaxPerf : ratio > 1.55f ? NVSDK_NGX_PerfQuality_Value_Balanced : NVSDK_NGX_PerfQuality_Value_MaxQuality;
        cp.InFeatureCreateFlags = NVSDK_NGX_DLSS_Feature_Flags_MVLowRes | NVSDK_NGX_DLSS_Feature_Flags_AutoExposure;
        if (neural) {
            for (const char* key : {"Width", "OutWidth", "DLSSNR.Width"}) params->Set(key, w);
            for (const char* key : {"Height", "OutHeight", "DLSSNR.Height"}) params->Set(key, h);
            params->Set("DLSSNR.ScalingRatio", 1.f);
            params->Set("DLSSNRComputeScalingRatioCallback", reinterpret_cast<void*>(&NrRatio));
            params->Set("DLSSNR.Hint.Render.Preset", static_cast<unsigned>(nr.preset));
            params->Set("PerfQualityValue", 0);
            params->Set("DLSS.Feature.Create.Flags", static_cast<int>(NVSDK_NGX_DLSS_Feature_Flags_MVLowRes));
            params->Set(NVSDK_NGX_Parameter_CreationNodeMask, 1u);
            params->Set(NVSDK_NGX_Parameter_VisibilityNodeMask, 1u);
            Ngx(MdNrCreate(list.Get(), params, &feature), "Create native DLSS 5 feature");
            for (int pass = 1; pass < nr.passes; pass++)
                Ngx(MdNrCreate(list.Get(), params, &repeatFeatures[pass - 1]), "Create independent NR repeat history");
        } else {
            Trace("Configure: NGX create"); Ngx(NGX_D3D12_CREATE_DLSS_EXT(list.Get(), 1, 1, &feature, params, &cp), "Create native DLSS feature");
        }
        Wait(Submit());
        Trace("Configure: complete");
        std::string label = model == NVSDK_NGX_DLSS_Hint_Render_Preset_K ? "DLSS 4 preset K" :
            model == NVSDK_NGX_DLSS_Hint_Render_Preset_L ? "DLSS 4.5 preset L" : "DLSS 4.5 preset M";
        status = label + " + NVIDIA Optical Flow (" + (highMotion ? "High" : "Medium") + "): " + std::to_string(w) + "x" + std::to_string(h) + " -> " + std::to_string(ow) + "x" + std::to_string(oh);
        if (neural) status = "DLSS 5 NR + NVIDIA Optical Flow: work=" + std::to_string(w) + "x" + std::to_string(h) + "; source=" + std::to_string(rawW) + "x" + std::to_string(rawH);
        if (neural) status += "; passes=" + std::to_string(nr.passes);
    }
    ID3D12Resource* Process(const BYTE* pixels, UINT stride, bool bottomUp, UINT width, UINT height, UINT outWidth, UINT outHeight, bool reset, int requestedModel, int motionQuality, float protection) {
        UINT workH = neural ? std::min(height, static_cast<UINT>(std::clamp(nr.workHeight, 240, 2160))) : height;
        UINT workW = neural ? std::max(160u, static_cast<UINT>(std::round(double(width) * workH / height))) : width;
        Configure(workW, workH, outWidth, outHeight, requestedModel, motionQuality, width, height);
        protection = std::isfinite(protection) ? std::clamp(protection, 0.f, 1.f) : 1.f;
        // Release held static detail when the live strength changes, without
        // allocating resources or restarting capture / the optical-flow session.
        reset = reset || protection != detailProtection;
        detailProtection = protection;
        if (frame == 0) Trace("Process: upload");
        LARGE_INTEGER now{}; QueryPerformanceCounter(&now);
        double dt = last.QuadPart ? 1000.0 * (now.QuadPart - last.QuadPart) / frequency.QuadPart : 16.67;
        reset = reset || frame == 0 || dt > 250;
        last = now;
        if (reset) frame = 0;
        slot = frame % 2;
        Wait(slotDone[slot]);
        Hr(allocators[slot]->Reset(), "Reset native allocator"); Hr(list->Reset(allocators[slot].Get(), nullptr), "Reset native upload list");
        recording = true;
        for (unsigned y = 0; y < rawH; y++) memcpy(mapped[slot] + SIZE_T(y) * footprint.Footprint.RowPitch, pixels + SIZE_T(y) * stride, SIZE_T(rawW) * 4);
        Barrier(raw.Get(), Common, D3D12_RESOURCE_STATE_COPY_DEST);
        D3D12_TEXTURE_COPY_LOCATION dst{}; dst.pResource = raw.Get(); dst.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        D3D12_TEXTURE_COPY_LOCATION src{}; src.pResource = uploads[slot].Get(); src.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT; src.PlacedFootprint = footprint;
        list->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
        Barrier(raw.Get(), D3D12_RESOURCE_STATE_COPY_DEST, Read); Barrier(scratch.Get(), Common, Uav);
        // Captured frames are already rasterized, not camera-jittered samples.
        // Resampling them with a changing offset fabricates motion in static UI.
        float constants[8] = { float(w), float(h), 0, 0, reset ? 1.f : 0.f, bottomUp ? 1.f : 0.f, detailProtection, 0 };
        Dispatch(orient.Get(), 0, constants);
        Barrier(scratch.Get(), Uav, D3D12_RESOURCE_STATE_COPY_SOURCE);
        Barrier(input[slot].Get(), Common, D3D12_RESOURCE_STATE_COPY_DEST);
        list->CopyResource(input[slot].Get(), scratch.Get());
        Barrier(input[slot].Get(), D3D12_RESOURCE_STATE_COPY_DEST, Common);
        if (reset) {
            Barrier(input[1-slot].Get(), Common, D3D12_RESOURCE_STATE_COPY_DEST);
            list->CopyResource(input[1-slot].Get(), scratch.Get());
            Barrier(input[1-slot].Get(), D3D12_RESOURCE_STATE_COPY_DEST, Common);
        }
        Barrier(scratch.Get(), D3D12_RESOURCE_STATE_COPY_SOURCE, Common); Barrier(raw.Get(), Read, Common);
        auto ready = Submit();
        if (frame == 0) Trace("Process: optical execute");
        NV_OF_FENCE_POINT inputPoint{ fence.Get(), ready }, outputPoint{ ofFence.Get(), opticalValue + 1 };
        NV_OF_EXECUTE_INPUT_PARAMS_D3D12 ip{}; ip.inputFrame = inputHandles[slot]; ip.referenceFrame = inputHandles[1-slot];
        ip.disableTemporalHints = reset ? NV_OF_TRUE : NV_OF_FALSE; ip.numFencePoints = 1; ip.fencePoint = &inputPoint;
        NV_OF_EXECUTE_OUTPUT_PARAMS_D3D12 op{}; op.outputBuffer = flowHandle; op.outputCostBuffer = costHandle; op.fencePoint = &outputPoint;
        Flow(of.nvOFExecuteD3D12(session, &ip, &op), "Estimate capture motion"); opticalValue++;
        if (frame == 0) Trace("Process: guides");
        Hr(queue->Wait(ofFence.Get(), opticalValue), "Wait for optical flow on GPU");
        Hr(list->Reset(allocators[slot].Get(), nullptr), "Reset guide/evaluate list");
        recording = true;
        for (auto* r : { input[0].Get(), input[1].Get(), flow.Get(), cost.Get() }) Barrier(r, Common, Read);
        for (auto* r : { color.Get(), motion.Get(), depth.Get(), bias.Get() }) Barrier(r, Common, Uav);
        Dispatch(guides.Get(), 8 + slot * 8, constants);
        for (auto* r : { color.Get(), motion.Get(), depth.Get(), bias.Get() }) Barrier(r, Uav, Read);
        Barrier(output.Get(), Common, Uav);
        NVSDK_NGX_D3D12_DLSS_Eval_Params ep{};
        ep.Feature.pInColor = color.Get(); ep.Feature.pInOutput = output.Get(); ep.pInDepth = depth.Get(); ep.pInMotionVectors = motion.Get();
        ep.pInBiasCurrentColorMask = bias.Get(); ep.InJitterOffsetX = constants[2]; ep.InJitterOffsetY = constants[3];
        ep.InRenderSubrectDimensions = { w, h }; ep.InReset = reset; ep.InMVScaleX = ep.InMVScaleY = 1;
        ep.InPreExposure = ep.InExposureScale = 1; ep.InFrameTimeDeltaInMsec = float(std::clamp(dt, 1.0, 100.0));
        if (frame == 0) Trace("Process: NGX evaluate");
        if (neural) {
            params->Set("DLSSNR.Color", color.Get()); params->Set("DLSSNR.Output", output.Get());
            params->Set("DLSSNR.MVec", motion.Get()); params->Set("DLSSNR.Depth", depth.Get());
            for (const char* prefix : {"Color", "Depth", "MVec", "Output"}) {
                std::string key = std::string("DLSSNR.") + prefix + "Subrect";
                params->Set((key + "BaseX").c_str(), 0u); params->Set((key + "BaseY").c_str(), 0u);
                params->Set((key + "Width").c_str(), w); params->Set((key + "Height").c_str(), h);
            }
            params->Set("DLSSNR.MVecScaleX", 1.f); params->Set("DLSSNR.MVecScaleY", 1.f);
            params->Set("DLSSNR.DepthInverted", 0); params->Set("DLSSNR.Enabled", 1); params->Set("DLSSNR.Reset", reset ? 1 : 0);
            params->Set("DLSSNR.Intensity", nr.intensity);
            params->Set("DLSSNR.Style", static_cast<unsigned>(nr.style));
            params->Set("DLSSNR.LocalToneStrength", nr.localTone);
            params->Set("DLSSNR.LocalStructureStrength", nr.localStructure);
            params->Set("DLSSNR.SkinStructureStrength", nr.skinStructure);
            params->Set("DLSSNR.UseAutoMask", nr.autoMask); params->Set("DLSSNR.UICorrection", nr.uiCorrection);
            if (nr.intensity > 0) {
                Ngx(MdNrEvaluate(list.Get(), feature, params), "Evaluate native DLSS 5");
                // Ping-pong color only; each pass owns its temporal feature history.
                ID3D12Resource* textures[] = { output.Get(), repeatOutput.Get() };
                D3D12_RESOURCE_STATES states[] = { Uav, Common };
                int previous = 0;
                for (int pass = 1; pass < nr.passes; pass++) {
                    int next = 1 - previous;
                    Barrier(textures[previous], states[previous], Read); states[previous] = Read;
                    Barrier(textures[next], states[next], Uav); states[next] = Uav;
                    params->Set("DLSSNR.Color", textures[previous]);
                    params->Set("DLSSNR.Output", textures[next]);
                    Ngx(MdNrEvaluate(list.Get(), repeatFeatures[pass - 1], params), "Evaluate repeated DLSS 5");
                    previous = next;
                }
                if (previous == 1) {
                    Barrier(textures[0], states[0], D3D12_RESOURCE_STATE_COPY_DEST);
                    Barrier(textures[1], states[1], D3D12_RESOURCE_STATE_COPY_SOURCE);
                    list->CopyResource(textures[0], textures[1]);
                    Barrier(textures[0], D3D12_RESOURCE_STATE_COPY_DEST, Uav);
                    Barrier(textures[1], D3D12_RESOURCE_STATE_COPY_SOURCE, Common);
                } else if (nr.passes > 1) Barrier(textures[1], states[1], Common);
            }
        } else Ngx(NGX_D3D12_EVALUATE_DLSS_EXT(list.Get(), feature, params, &ep), "Evaluate DLSS 4.5");
        Barrier(output.Get(), Uav, Read);
        Barrier(stableOutput.Get(), outputReadable ? D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE : Common, Uav);
        float stabilizeConstants[8] = { float(w), float(h), float(ow), float(oh), reset ? 1.f : 0.f, 0, 0, 0 };
        if (neural) {
            Barrier(raw.Get(), Common, Read);
            stabilizeConstants[4] = nr.intensity > 0 ? 1.f : 0.f;
            stabilizeConstants[5] = nr.transfer; stabilizeConstants[6] = nr.colorStrength; stabilizeConstants[7] = bottomUp ? 1.f : 0.f;
        }
        Dispatch(stabilize.Get(), 24 + slot * 8, stabilizeConstants, true);
        if (neural) Barrier(raw.Get(), Read, Common);
        Barrier(output.Get(), Read, Common);
        Barrier(stableOutput.Get(), Uav, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE); outputReadable = true;
        for (auto* r : { input[0].Get(), input[1].Get(), flow.Get(), cost.Get(), color.Get(), motion.Get(), depth.Get(), bias.Get() }) Barrier(r, Read, Common);
        slotDone[slot] = Submit(); frame++;
        if (frame == 1) Trace("Process: submitted");
        return stableOutput.Get();
    }
};
}

#define API extern "C" __declspec(dllexport)
API void* __cdecl MdDlssCreate(ID3D12Device* device, ID3D12CommandQueue* queue, const wchar_t* runtime, const wchar_t* cache) {
    try { auto context = std::make_unique<Context>(); context->Initialize(device, queue, runtime, cache); return context.release(); }
    catch (const std::exception& ex) { error = ex.what(); return nullptr; }
}
API ID3D12Resource* __cdecl MdDlssProcessOptions(Context* context, const BYTE* pixels, UINT stride, int bottomUp, UINT w, UINT h, UINT ow, UINT oh, int reset, int model, int motionQuality, float detailProtection) {
    try { Require(context && pixels && stride >= UINT64(w) * 4, "Invalid DLSS frame"); return context->Process(pixels, stride, bottomUp != 0, w, h, ow, oh, reset != 0, model, motionQuality, detailProtection); }
    catch (const std::exception& ex) { error = ex.what(); return nullptr; }
}
API const char* __cdecl MdDlssError() { return error.c_str(); }
API const char* __cdecl MdDlssStatus(Context* context) { return context ? context->status.c_str() : "Native DLSS unavailable"; }
API int __cdecl MdDlssSuspend(Context* context) {
    try { if (context) context->Clear(); return 1; }
    catch (const std::exception& ex) { error = ex.what(); return 0; }
}
API void __cdecl MdDlssDestroy(Context* context) { delete context; }
API void* __cdecl MdNeuralCreate(ID3D12Device* device, ID3D12CommandQueue* queue, const wchar_t* runtime, const wchar_t* cache) {
    try { auto context = std::make_unique<Context>(); context->Initialize(device, queue, runtime, cache, true); return context.release(); }
    catch (const std::exception& ex) { error = ex.what(); return nullptr; }
}
API ID3D12Resource* __cdecl MdNeuralProcess(Context* context, const BYTE* pixels, UINT stride, int bottomUp, UINT w, UINT h, const NrOptions* options) {
    try {
        Require(context && options && pixels && stride >= UINT64(w) * 4, "Invalid NR frame");
        bool changed = context->nr.style != options->style || (context->nr.intensity <= 0 && options->intensity > 0);
        Require(options->passes >= 1 && options->passes <= 4, "Invalid NR pass count");
        if (context->nr.preset != options->preset || context->nr.passes != options->passes) context->Clear();
        context->nr = *options;
        return context->Process(pixels, stride, bottomUp != 0, w, h, w, h, changed, 1, 0, 0);
    } catch (const std::exception& ex) { error = ex.what(); return nullptr; }
}
