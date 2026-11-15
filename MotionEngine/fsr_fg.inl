// fsr_fg.inl - DLSS 5 SUITE: "FSR FG", AMD FidelityFX Super Resolution 3.1 Frame Generation as a generator of
// Motion Forge's (generator 4). It runs on any card D3D12 runs on.
//
// AMD's runtime is the FidelityFX SDK 1.1.4's signed amd_fidelityfx_dx12.dll (MIT; tools\get-fidelityfx.ps1 fetches
// it, SUITE ships it in its engine\ folder beside the Screen Engine); the headers it is driven through are the
// SDK's FidelityFX API (external\fidelityfx\include, shared with the Screen Engine). The file is looked for where
// the app says (ffx_dir=<folder>), beside this program, and in the folder above it, and AMD's signature is checked
// before it is mapped (lf_signature.h, TrustAmdDll).
//
// It goes through the shared presenter as XeSS MFG does, but without a swap chain of AMD's: the frame generation
// context is made with the DX12 backend alone and each real frame is handed to it on the engine's own command list
// - "prepare" with the engine's motion field (or none: FSR's own optical flow alone) and the flat depth plane, then
// the frame generation dispatch with the real frame as its present colour, which draws the frame in between it and
// the real frame before into the pair's picture (FFX_FRAMEGENERATION_FLAG_NO_SWAPCHAIN_CONTEXT_NOTIFY). FSR 3.1
// makes one frame in between a pair, so FSR FG is x2; the presenter shows it at the middle of the pair.

#include "ffx_api/ffx_api.h"
#include "ffx_api/ffx_api_types.h"
#include "ffx_api/ffx_framegeneration.h"
#include "ffx_api/dx12/ffx_api_dx12.h"

// DLSS 5 SUITE: where SUITE keeps amd_fidelityfx_dx12.dll (ffx_dir=<folder>, sm_control.inl); empty: beside the engine.
static wchar_t g_ffx_dir[MAX_PATH] = {};

namespace fsrfg
{
    static struct State
    {
        HMODULE dll = nullptr;
        PfnFfxCreateContext create = nullptr;
        PfnFfxDestroyContext destroy = nullptr;
        PfnFfxConfigure configure = nullptr;
        PfnFfxDispatch dispatch = nullptr;
        PfnFfxQuery query = nullptr;
        ffxContext context = nullptr;
        // (What the context was made with: they stay alive as long as it does.)
        ffxCreateBackendDX12Desc backend = {};
        ffxCreateContextDescFrameGeneration desc = {};
        UINT w = 0, h = 0, rw = 0, rh = 0;
        DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
        uint64_t frame = 0;
        bool restart = true;
        bool failed = false;
        unsigned errors = 0;
        char version[64] = {};
    } g;

    static void Message(uint32_t type, const wchar_t *message)
    {
        if (message == nullptr) return;
        static unsigned told = 0;
        if (++told > 200) return;       // (a runtime that repeats itself every frame does not fill the log)
        Trace("[fsrfg] AMD's runtime %s: %ls", type == FFX_API_MESSAGE_TYPE_ERROR ? "error" : "warning", message);
    }

    static ffxReturnCode_t SafeDispatch(ffxContext *context, const ffxDispatchDescHeader *desc)
    {
        __try { return g.dispatch(context, desc); }
        __except (EXCEPTION_EXECUTE_HANDLER) { return FFX_API_RETURN_ERROR_RUNTIME_ERROR; }
    }
    static ffxReturnCode_t SafeConfigure(ffxContext *context, const ffxConfigureDescHeader *desc)
    {
        __try { return g.configure(context, desc); }
        __except (EXCEPTION_EXECUTE_HANDLER) { return FFX_API_RETURN_ERROR_RUNTIME_ERROR; }
    }
    static ffxReturnCode_t SafeCreate(ffxContext *context, ffxCreateContextDescHeader *desc)
    {
        __try { return g.create(context, desc, nullptr); }
        __except (EXCEPTION_EXECUTE_HANDLER) { return FFX_API_RETURN_ERROR_RUNTIME_ERROR; }
    }

    // amd_fidelityfx_dx12.dll, signed by AMD, from the first place that has one.
    static bool Load()
    {
        if (g.dll) return true;
        if (g.failed) return false;
        g.failed = true;
        wchar_t places[3][MAX_PATH] = {};
        if (g_ffx_dir[0]) wcscpy_s(places[0], g_ffx_dir);
        GetModuleFileNameW(nullptr, places[1], MAX_PATH);
        if (wchar_t *slash = wcsrchr(places[1], L'\\')) *slash = 0;
        wcscpy_s(places[2], places[1]);
        if (wchar_t *slash = wcsrchr(places[2], L'\\')) *slash = 0; else places[2][0] = 0;
        for (const auto &place : places)
        {
            if (!place[0]) continue;
            wchar_t path[MAX_PATH] = {};
            if (swprintf_s(path, L"%s\\amd_fidelityfx_dx12.dll", place) < 0) continue;
            if (GetFileAttributesW(path) == INVALID_FILE_ATTRIBUTES) continue;
            if (!TrustAmdDll(path)) { Trace("[fsrfg] %ls is not signed by AMD - not loaded", path); continue; }
            g.dll = LoadLibraryExW(path, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
            if (!g.dll) { Trace("[fsrfg] %ls did not load (%lu)", path, GetLastError()); continue; }
            Trace("[fsrfg] AMD FidelityFX runtime from %ls, AMD's signature checked", path);
            break;
        }
        if (!g.dll)
        {
            Trace("[dlssg] unavailable: AMD's FidelityFX runtime (amd_fidelityfx_dx12.dll) is not in SUITE's engine folder");
            return false;
        }
        g.create = reinterpret_cast<PfnFfxCreateContext>(GetProcAddress(g.dll, "ffxCreateContext"));
        g.destroy = reinterpret_cast<PfnFfxDestroyContext>(GetProcAddress(g.dll, "ffxDestroyContext"));
        g.configure = reinterpret_cast<PfnFfxConfigure>(GetProcAddress(g.dll, "ffxConfigure"));
        g.dispatch = reinterpret_cast<PfnFfxDispatch>(GetProcAddress(g.dll, "ffxDispatch"));
        g.query = reinterpret_cast<PfnFfxQuery>(GetProcAddress(g.dll, "ffxQuery"));
        if (!g.create || !g.destroy || !g.configure || !g.dispatch)
        {
            Trace("[dlssg] unavailable: amd_fidelityfx_dx12.dll lacks the FidelityFX API");
            FreeLibrary(g.dll); g.dll = nullptr;
            return false;
        }
        // Its own messages into the engine's log.
        ffxConfigureDescGlobalDebug1 debug = {};
        debug.header.type = FFX_API_CONFIGURE_DESC_TYPE_GLOBALDEBUG1;
        debug.fpMessage = &Message;
        debug.debugLevel = FFX_API_CONFIGURE_GLOBALDEBUG_LEVEL_WARNINGS;
        SafeConfigure(nullptr, &debug.header);
        g.failed = false;
        return true;
    }

    static void Close()
    {
        if (g.context && g.destroy)
        {
            ffxContext context = g.context;
            g.context = nullptr;
            __try { g.destroy(&context, nullptr); } __except (EXCEPTION_EXECUTE_HANDLER) {}
        }
        g.w = g.h = g.rw = g.rh = 0;
        g.restart = true;
    }

    // The next frame starts a new history (a cut, a rest, a new size).
    static void Forget() { g.restart = true; }

    static bool Live(UINT w, UINT h, DXGI_FORMAT format) { return g.context && g.w == w && g.h == h && g.format == format; }

    // A context for frames of w x h (the picture's size), motion and depth at rw x rh (the motion field's).
    static bool Ready(UINT w, UINT h, UINT rw, UINT rh, DXGI_FORMAT format)
    {
        if (g.context && g.w == w && g.h == h && g.rw == rw && g.rh == rh && g.format == format) return true;
        Close();
        if (!Load()) return false;
        if (ffxApiGetSurfaceFormatDX12(format) == FFX_API_SURFACE_FORMAT_UNKNOWN)
        { Trace("[dlssg] unavailable: FSR FG does not take pictures of format %u", static_cast<unsigned>(format)); return false; }
        g.backend = {};
        g.backend.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12;
        g.backend.device = lfCore.device;
        g.desc = {};
        g.desc.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION;
        g.desc.header.pNext = &g.backend.header;
        g.desc.flags = format == DXGI_FORMAT_R10G10B10A2_UNORM || format == DXGI_FORMAT_R16G16B16A16_FLOAT ? FFX_FRAMEGENERATION_ENABLE_HIGH_DYNAMIC_RANGE : 0u;
        g.desc.displaySize = { w, h };
        g.desc.maxRenderSize = { rw, rh };
        g.desc.backBufferFormat = ffxApiGetSurfaceFormatDX12(format);
        ffxContext context = nullptr;
        const ffxReturnCode_t made = SafeCreate(&context, &g.desc.header);
        if (made != FFX_API_RETURN_OK || context == nullptr)
        {
            Trace("[dlssg] unavailable: AMD's runtime did not make FSR frame generation (%u)", made);
            return false;
        }
        g.context = context;
        g.w = w; g.h = h; g.rw = rw; g.rh = rh; g.format = format;
        g.restart = true;
        g.errors = 0;
        if (g.query)
        {
            ffxQueryGetProviderVersion version = {};
            version.header.type = FFX_API_QUERY_DESC_TYPE_GET_PROVIDER_VERSION;
            __try { if (g.query(&g.context, &version.header) == FFX_API_RETURN_OK && version.versionName) strncpy_s(g.version, version.versionName, _TRUNCATE); }
            __except (EXCEPTION_EXECUTE_HANDLER) {}
        }
        Trace("[fsrfg] FSR 3.1 frame generation (AMD's provider %s) at %ux%u, motion and depth at %ux%u", g.version[0] ? g.version : "?", w, h, rw, rh);
        return true;
    }

    // One real frame handed over and the frame in between it and the one before drawn into `out`, on `list`.
    // `color` in color_state; `motion` (null: FSR's own optical flow alone, the engine's zero field given) and
    // `depth` in NON_PIXEL_SHADER_RESOURCE; `out` in out_state. AMD's runtime puts each back in the state given.
    // False: nothing was drawn (the pair goes without, as when another generator says no).
    static bool Make(ID3D12GraphicsCommandList *list, ID3D12Resource *color, D3D12_RESOURCE_STATES color_state, ID3D12Resource *motion,
                     ID3D12Resource *depth, ID3D12Resource *out, D3D12_RESOURCE_STATES out_state, bool reset, double frame_seconds)
    {
        if (!g.context || !color || !motion || !depth || !out) return false;
        auto state_of = [](D3D12_RESOURCE_STATES s) -> uint32_t {
            switch (s)
            {
            case D3D12_RESOURCE_STATE_COPY_SOURCE: return FFX_API_RESOURCE_STATE_COPY_SRC;
            case D3D12_RESOURCE_STATE_COPY_DEST: return FFX_API_RESOURCE_STATE_COPY_DEST;
            case D3D12_RESOURCE_STATE_UNORDERED_ACCESS: return FFX_API_RESOURCE_STATE_UNORDERED_ACCESS;
            case D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE: return FFX_API_RESOURCE_STATE_PIXEL_READ;
            case D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE | D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE: return FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ;
            case D3D12_RESOURCE_STATE_COMMON: return FFX_API_RESOURCE_STATE_COMMON;
            default: return FFX_API_RESOURCE_STATE_COMPUTE_READ;
            }
        };
        const uint64_t id = ++g.frame;
        const bool restart = reset || g.restart;
        g.restart = false;
        const FfxApiRect2D rect = { 0, 0, static_cast<int32_t>(g.w), static_cast<int32_t>(g.h) };

        ffxConfigureDescFrameGeneration config = {};
        config.header.type = FFX_API_CONFIGURE_DESC_TYPE_FRAMEGENERATION;
        config.swapChain = nullptr;
        config.frameGenerationEnabled = true;
        config.allowAsyncWorkloads = false;
        config.flags = FFX_FRAMEGENERATION_FLAG_NO_SWAPCHAIN_CONTEXT_NOTIFY;
        config.onlyPresentGenerated = false;
        config.generationRect = rect;
        config.frameID = id;
        ffxReturnCode_t r = SafeConfigure(&g.context, &config.header);

        // A picture of a screen has no camera: the motion field (in its own texels, current -> previous) carries
        // all of the motion, the depth plane is flat, the camera is the identity looking straight ahead.
        ffxDispatchDescFrameGenerationPrepareCameraInfo camera = {};
        camera.header.type = FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION_PREPARE_CAMERAINFO;
        camera.cameraUp[1] = 1.0f; camera.cameraRight[0] = 1.0f; camera.cameraForward[2] = 1.0f;
        ffxDispatchDescFrameGenerationPrepare prepare = {};
        prepare.header.type = FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION_PREPARE;
        prepare.header.pNext = &camera.header;
        prepare.frameID = id;
        prepare.flags = 0;
        prepare.commandList = list;
        prepare.renderSize = { g.rw, g.rh };
        prepare.jitterOffset = { 0.0f, 0.0f };
        prepare.motionVectorScale = { 1.0f, 1.0f };
        prepare.frameTimeDelta = static_cast<float>(std::clamp(frame_seconds, 0.001, 0.2) * 1000.0);
        prepare.cameraNear = 0.1f;
        prepare.cameraFar = 1000.0f;
        prepare.cameraFovAngleVertical = 1.0f;
        prepare.viewSpaceToMetersFactor = 1.0f;
        prepare.depth = ffxApiGetResourceDX12(depth, FFX_API_RESOURCE_STATE_COMPUTE_READ);
        prepare.motionVectors = ffxApiGetResourceDX12(motion, FFX_API_RESOURCE_STATE_COMPUTE_READ);
        if (r == FFX_API_RETURN_OK) r = SafeDispatch(&g.context, &prepare.header);

        ffxDispatchDescFrameGeneration make = {};
        make.header.type = FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION;
        make.commandList = list;
        make.presentColor = ffxApiGetResourceDX12(color, state_of(color_state));
        make.outputs[0] = ffxApiGetResourceDX12(out, state_of(out_state), FFX_API_RESOURCE_USAGE_UAV);
        make.numGeneratedFrames = 1;
        make.reset = restart;
        const bool hdr = (g.desc.flags & FFX_FRAMEGENERATION_ENABLE_HIGH_DYNAMIC_RANGE) != 0;
        make.backbufferTransferFunction = hdr ? (g.format == DXGI_FORMAT_R16G16B16A16_FLOAT ? FFX_API_BACKBUFFER_TRANSFER_FUNCTION_SCRGB : FFX_API_BACKBUFFER_TRANSFER_FUNCTION_PQ)
                                              : FFX_API_BACKBUFFER_TRANSFER_FUNCTION_SRGB;
        make.minMaxLuminance[0] = 0.0001f;
        make.minMaxLuminance[1] = 1000.0f;
        make.generationRect = rect;
        make.frameID = id;
        if (r == FFX_API_RETURN_OK) r = SafeDispatch(&g.context, &make.header);
        if (r != FFX_API_RETURN_OK)
        {
            if (++g.errors <= 5) Trace("[fsrfg] AMD's runtime refused frame %llu (%u) - the pair goes without", static_cast<unsigned long long>(id), r);
            g.restart = true;
            return false;
        }
        return !restart;    // (the first frame of a history has nothing before it: what was drawn is not shown)
    }
}
