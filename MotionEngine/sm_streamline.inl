// sm_streamline.inl - DLSS MFG through NVIDIA Streamline (DLSS 5 SUITE).
//
// MFG SUPER MOTION drove NVIDIA's frame generation by calling NGX's DLSS-G feature itself, which NVIDIA's runtime
// accepts only from a program it recognises: its own engine was built as "nvngx.dll" for it. Motion Forge's engine
// keeps its true name (MotionForgeEngine.exe), and NVIDIA's runtime refuses a direct call from it (Init_Ext
// 0xBAD00002). NVIDIA's supported way for any program is Streamline, so DLSS MFG goes through it here, as the
// Screen Engine's NVIDIA frame generation does (Engine/src/effects/real/streamline.cpp):
//   - sl.interposer.dll and its plugins (sl.common, sl.dlss_g, sl.reflex, sl.pcl, nvngx_dlssg.dll) are loaded from
//     SUITE's engine folder - the one the Screen Engine's copies are in (sl_dir=<folder> from the app; else beside
//     this program, else the folder above it) - each one's NVIDIA signature checked first (lf_signature.h). Nothing
//     in them is changed.
//   - Streamline is started with manual hooking: only the output window's queue and swap chain go through its
//     proxies; capture, the motion field and everything else stay on the device as it is.
//   - Each new real frame is copied into the back buffer as for the picture shown untouched (ShowPassThrough), the
//     motion field and a flat depth plane are tagged for that frame, the constants and DLSS-G's options set, Reflex
//     and PC Latency markers put around it, and it is presented: DLSS-G makes the frames in between and presents them
//     itself, paced by its own pacer. Frames with nothing new are not presented (the loop rests, sm_loop.inl).
//   - The multiplier is the app's (x2..x6), never above what DLSS-G says the card makes (numFramesToGenerateMax:
//     RTX 40 x2, RTX 50 up to x6). The preset stays the driver profile's (sm_dlssg_preset.inl).
//   - The status lines are the engine's own: @fps with every frame DLSS-G presented counted, @mfg the multiplier
//     running, @mfg unavailable <why> when Streamline cannot start.
// The engine's own NGX DLSS-G path (lf_dlssg.inl's runtime part) is not used any more; the shared presenter stays
// for XeSS MFG, SMFG and SMAI, which are unchanged.

#include <sl.h>
#include <sl_consts.h>
#include <sl_dlss_g.h>
#include <sl_pcl.h>
#include <sl_reflex.h>

namespace slfg
{
    static constexpr uint32_t kViewport = 0;
    static const wchar_t *const kFiles[] = { L"sl.interposer.dll", L"sl.common.dll", L"sl.dlss_g.dll", L"sl.reflex.dll",
                                             L"sl.pcl.dll", L"nvngx_dlssg.dll" };
    static const char kProjectId[] = "a0f57b54-1daf-4934-90ae-c4035c19df04";   // the engine's own (lf_ngx.inl)

    static struct State
    {
        bool started = false;           // Streamline up, DLSS-G supported on this card, the proxy queue made
        bool failed = false;            // it was tried and could not start (the reason went to the app)
        wchar_t dir[MAX_PATH] = {};
        HMODULE module = nullptr;
        PFun_slInit *init = nullptr;
        PFun_slShutdown *shutdown = nullptr;
        PFun_slSetD3DDevice *set_device = nullptr;
        PFun_slIsFeatureSupported *supported = nullptr;
        PFun_slGetFeatureFunction *feature_function = nullptr;
        PFun_slUpgradeInterface *upgrade = nullptr;
        PFun_slGetNewFrameToken *new_token = nullptr;
        PFun_slSetConstants *set_constants = nullptr;
        PFun_slSetTagForFrame *set_tags = nullptr;
        PFun_slDLSSGSetOptions *set_options = nullptr;
        PFun_slDLSSGGetState *get_state = nullptr;
        PFun_slPCLSetMarker *marker = nullptr;
        PFun_slReflexSetOptions *reflex = nullptr;
        ID3D12Device *proxy_device = nullptr;   // held for as long as the proxy queue (Streamline 2.14.16 keeps a bare pointer)
        unsigned most = 1;              // the most frames DLSS-G makes between two on this card
        bool dynamic = false;           // Dynamic MFG offered by DLSS-G here
        StagedTexture depth = {};       // the flat depth plane, at the motion field's size
        UINT depth_w = 0, depth_h = 0;
        unsigned between = 0;           // frames in between asked of DLSS-G now (0: off)
        unsigned told_most_for = 0;     // the multiplier the "most on this card" note was said for
        uint32_t status = 0;            // DLSS-G's last word (sl::DLSSGStatus)
        // Once a second, for the log: frames handed over, frames DLSS-G presented, presents the swap chain counted.
        unsigned handed = 0, shown = 0;
        UINT present_count_at = 0;
        bool present_count_ok = false;
        ULONGLONG second_at = 0;
    } g;

    // Every Streamline call that returns sl::Result, as text for the log.
    static const char *ResultText(sl::Result r)
    {
        switch (r)
        {
        case sl::Result::eOk: return "ok";
        case sl::Result::eErrorIO: return "I/O error";
        case sl::Result::eErrorDriverOutOfDate: return "the graphics driver is too old";
        case sl::Result::eErrorOSOutOfDate: return "Windows is too old";
        case sl::Result::eErrorOSDisabledHWS: return "hardware-accelerated GPU scheduling is off";
        case sl::Result::eErrorDeviceNotCreated: return "no device";
        case sl::Result::eErrorNoSupportedAdapterFound: return "no supported graphics card";
        case sl::Result::eErrorAdapterNotSupported: return "this graphics card is not supported (DLSS Frame Generation needs an RTX 40 or 50)";
        case sl::Result::eErrorNoPlugins: return "no plugins found";
        case sl::Result::eErrorVulkanAPI: return "Vulkan error";
        case sl::Result::eErrorDXGIAPI: return "DXGI error";
        case sl::Result::eErrorD3DAPI: return "D3D error";
        case sl::Result::eErrorNRDAPI: return "NRD error";
        case sl::Result::eErrorNVAPI: return "NvAPI error";
        case sl::Result::eErrorReflexAPI: return "Reflex error";
        case sl::Result::eErrorNGXFailed: return "NGX failed";
        case sl::Result::eErrorJSONParsing: return "JSON error";
        case sl::Result::eErrorMissingProxy: return "missing proxy";
        case sl::Result::eErrorMissingResourceState: return "missing resource state";
        case sl::Result::eErrorInvalidIntegration: return "invalid integration";
        case sl::Result::eErrorMissingInputParameter: return "missing input";
        case sl::Result::eErrorNotInitialized: return "not initialised";
        case sl::Result::eErrorComputeFailed: return "compute failed";
        case sl::Result::eErrorInitNotCalled: return "slInit not called";
        case sl::Result::eErrorExceptionHandler: return "exception";
        case sl::Result::eErrorInvalidParameter: return "invalid parameter";
        case sl::Result::eErrorMissingConstants: return "missing constants";
        case sl::Result::eErrorDuplicatedConstants: return "duplicated constants";
        case sl::Result::eErrorMissingOrInvalidAPI: return "missing or invalid API";
        case sl::Result::eErrorCommonConstantsMissing: return "common constants missing";
        case sl::Result::eErrorUnsupportedInterface: return "unsupported interface";
        case sl::Result::eErrorFeatureMissing: return "feature missing";
        case sl::Result::eErrorFeatureNotSupported: return "DLSS Frame Generation is not supported on this graphics card (it needs an RTX 40 or 50)";
        case sl::Result::eErrorFeatureMissingHooks: return "feature hooks missing";
        case sl::Result::eErrorFeatureFailedToLoad: return "feature failed to load";
        case sl::Result::eErrorFeatureWrongPriority: return "feature priority";
        case sl::Result::eErrorFeatureMissingDependency: return "feature dependency missing";
        case sl::Result::eErrorFeatureManagerInvalidState: return "feature manager state";
        case sl::Result::eErrorInvalidState: return "invalid state";
        case sl::Result::eWarnOutOfVRAM: return "out of video memory";
        default: return "error";
        }
    }

    // The reason it cannot run, to the log and - by its "[dlssg] unavailable:" start - to the app (@mfg unavailable).
    static bool Refuse(const char *format, ...)
    {
        char text[512];
        va_list args;
        va_start(args, format);
        _vsnprintf_s(text, sizeof(text), _TRUNCATE, format, args);
        va_end(args);
        Trace("[dlssg] unavailable: %s", text);
        g.failed = true;
        return false;
    }

    static void Log(sl::LogType type, const char *message)
    {
        if (!message) return;
        char line[1024];
        strncpy_s(line, message, _TRUNCATE);
        for (size_t n = strlen(line); n > 0 && (line[n - 1] == '\n' || line[n - 1] == '\r'); --n) line[n - 1] = 0;
        Trace("[sl] %s%s", type == sl::LogType::eError ? "error: " : type == sl::LogType::eWarn ? "warning: " : "", line);
    }

    static bool HasAll(const wchar_t *dir)
    {
        for (const wchar_t *name : kFiles)
        {
            wchar_t path[MAX_PATH];
            if (swprintf_s(path, L"%ls\\%ls", dir, name) < 0 || GetFileAttributesW(path) == INVALID_FILE_ATTRIBUTES) return false;
        }
        return true;
    }

    // SUITE's folder named by the app, else beside the engine, else the folder above it (SUITE's engine\, where the
    // Screen Engine's copies are: the engine is engine\motion\MotionForgeEngine.exe).
    static bool FindDir(wchar_t (&dir)[MAX_PATH])
    {
        wchar_t candidates[3][MAX_PATH] = {};
        if (g_sl_dir[0]) wcscpy_s(candidates[0], g_sl_dir);
        GetModuleFileNameW(nullptr, candidates[1], MAX_PATH);
        if (wchar_t *s = wcsrchr(candidates[1], L'\\')) *s = 0;
        wcscpy_s(candidates[2], candidates[1]);
        if (wchar_t *s = wcsrchr(candidates[2], L'\\')) *s = 0;
        for (auto &c : candidates)
        {
            size_t n = wcslen(c);
            while (n > 0 && (c[n - 1] == L'\\' || c[n - 1] == L'/')) c[--n] = 0;
            if (n > 0 && HasAll(c)) { wcscpy_s(dir, c); return true; }
        }
        return false;
    }

    // sl.common.dll counts the program as in front only while the foreground window is one of the program's own
    // (Keyboard::hasFocus, Streamline 2.14.16 source/core/sl.extra/extra.cpp), and DLSS-G makes no frames otherwise
    // ("window not focused"). The engine's output is a click-through window over the game, which keeps the focus -
    // so, as the Screen Engine does, sl.common's own import of GetForegroundWindow answers with the output window.
    // Only that slot of its import table in this process is pointed elsewhere; no file and no code of NVIDIA's is
    // changed. SM_SL_FOCUS=0 leaves it alone (to see what DLSS-G does without it).
    static HWND WINAPI OutputInFront()
    {
        const HWND out = lfShow.hwnd;
        return out ? out : GetForegroundWindow();
    }

    static bool CountOutputInFront()
    {
        char v[4] = {};
        if (GetEnvironmentVariableA("SM_SL_FOCUS", v, sizeof(v)) > 0 && v[0] == '0')
        { Trace("[sl] SM_SL_FOCUS=0: sl.common's foreground test is left as it is"); return true; }
        const HMODULE common = GetModuleHandleW(L"sl.common.dll");
        const HMODULE user = GetModuleHandleW(L"user32.dll");
        if (!common || !user) return false;
        const ULONG_PTR system = reinterpret_cast<ULONG_PTR>(GetProcAddress(user, "GetForegroundWindow"));
        BYTE *const base = reinterpret_cast<BYTE *>(common);
        const auto *dos = reinterpret_cast<const IMAGE_DOS_HEADER *>(base);
        const auto *nt = reinterpret_cast<const IMAGE_NT_HEADERS *>(base + dos->e_lfanew);
        const IMAGE_DATA_DIRECTORY &dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
        if (!dir.VirtualAddress) return false;
        const auto *libraries = reinterpret_cast<const IMAGE_IMPORT_DESCRIPTOR *>(base + dir.VirtualAddress);
        for (unsigned l = 0; l < 128 && libraries[l].Name; ++l)
        {
            auto *calls = reinterpret_cast<IMAGE_THUNK_DATA *>(base + libraries[l].FirstThunk);
            for (unsigned i = 0; i < 4096 && calls[i].u1.Function; ++i)
            {
                const ULONG_PTR at = calls[i].u1.Function;
                if (at != system && at != reinterpret_cast<ULONG_PTR>(&OutputInFront)) continue;
                DWORD was = 0, unused = 0;
                if (!VirtualProtect(&calls[i].u1.Function, sizeof(ULONG_PTR), PAGE_READWRITE, &was)) return false;
                calls[i].u1.Function = reinterpret_cast<ULONG_PTR>(&OutputInFront);
                VirtualProtect(&calls[i].u1.Function, sizeof(ULONG_PTR), was, &unused);
                Trace("[sl] sl.common counts the output window as the one in front (it is click-through; the game keeps the focus)");
                return true;
            }
        }
        return false;
    }

    template <class Call> static Call *Export(const char *name)
    {
        return reinterpret_cast<Call *>(reinterpret_cast<void *>(GetProcAddress(g.module, name)));
    }

    template <class Call> static Call *FeatureCall(sl::Feature feature, const char *name)
    {
        void *call = nullptr;
        return g.feature_function(feature, name, call) == sl::Result::eOk ? reinterpret_cast<Call *>(call) : nullptr;
    }

    static bool Active() { return g.started && !g.failed; }

    // Streamline on the engine's device, before the output window is made. False (and the reason told) when DLSS
    // MFG cannot run; the picture is then shown as it is.
    static bool Start()
    {
        if (g.started) return true;
        g.failed = false;
        if (!FindDir(g.dir))
            return Refuse("NVIDIA Streamline was not found - SUITE's engine folder should hold sl.interposer.dll and its plugins (reinstall SUITE)");
        Trace("[sl] Streamline from %ls", g.dir);
        for (const wchar_t *name : kFiles)
        {
            wchar_t path[MAX_PATH];
            swprintf_s(path, L"%ls\\%ls", g.dir, name);
            if (!TrustLoadDll(path)) return Refuse("%ls in SUITE's engine folder is not signed by NVIDIA, so it is not loaded", name);
        }
        Trace("[sl] NVIDIA's signature checked on sl.interposer, sl.common, sl.dlss_g, sl.reflex, sl.pcl and nvngx_dlssg");
        wchar_t interposer[MAX_PATH];
        swprintf_s(interposer, L"%ls\\sl.interposer.dll", g.dir);
        g.module = LoadLibraryExW(interposer, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!g.module) return Refuse("sl.interposer.dll did not load (Windows %lu)", GetLastError());
        g.init = Export<PFun_slInit>("slInit");
        g.shutdown = Export<PFun_slShutdown>("slShutdown");
        g.set_device = Export<PFun_slSetD3DDevice>("slSetD3DDevice");
        g.supported = Export<PFun_slIsFeatureSupported>("slIsFeatureSupported");
        g.feature_function = Export<PFun_slGetFeatureFunction>("slGetFeatureFunction");
        g.upgrade = Export<PFun_slUpgradeInterface>("slUpgradeInterface");
        g.new_token = Export<PFun_slGetNewFrameToken>("slGetNewFrameToken");
        g.set_constants = Export<PFun_slSetConstants>("slSetConstants");
        g.set_tags = Export<PFun_slSetTagForFrame>("slSetTagForFrame");
        if (!g.init || !g.shutdown || !g.set_device || !g.supported || !g.feature_function || !g.upgrade || !g.new_token ||
            !g.set_constants || !g.set_tags)
            return Refuse("sl.interposer.dll lacks the calls the engine needs");

        static const sl::Feature features[] = { sl::kFeatureDLSS_G, sl::kFeatureReflex, sl::kFeaturePCL };
        const wchar_t *plugins[1] = { g.dir };
        sl::Preferences pref{};
        pref.showConsole = false;
        pref.logLevel = smOpt.log.load() || g_trace_echo ? sl::LogLevel::eDefault : sl::LogLevel::eOff;
        pref.pathsToPlugins = plugins;
        pref.numPathsToPlugins = 1;
        pref.pathToLogsAndData = nullptr;
        pref.logMessageCallback = &Log;
        // Manual hooking: only what is asked for by name goes through Streamline. Nothing is fetched over the network.
        pref.flags = sl::PreferenceFlags::eDisableCLStateTracking | sl::PreferenceFlags::eUseManualHooking |
                     sl::PreferenceFlags::eUseFrameBasedResourceTagging;
        pref.featuresToLoad = features;
        pref.numFeaturesToLoad = static_cast<uint32_t>(_countof(features));
        pref.applicationId = 0;
        pref.engine = sl::EngineType::eCustom;
        pref.engineVersion = "1.0";
        pref.projectId = kProjectId;
        pref.renderAPI = sl::RenderAPI::eD3D12;
        sl::Result r = g.init(pref, sl::kSDKVersion);
        if (r != sl::Result::eOk) return Refuse("Streamline did not start (%s, %d)", ResultText(r), static_cast<int>(r));
        auto down = [](const char *format, sl::Result r) {
            g.shutdown();
            return Refuse(format, ResultText(r), static_cast<int>(r));
        };
        r = g.set_device(lfCore.device);
        if (r != sl::Result::eOk) return down("Streamline did not take the device (%s, %d)", r);
        LUID luid = lfCore.device->GetAdapterLuid();
        sl::AdapterInfo adapter{};
        adapter.deviceLUID = reinterpret_cast<uint8_t *>(&luid);
        adapter.deviceLUIDSizeInBytes = sizeof(LUID);
        r = g.supported(sl::kFeatureDLSS_G, adapter);
        if (r != sl::Result::eOk) return down("%s (Streamline %d)", r);
        g.set_options = FeatureCall<PFun_slDLSSGSetOptions>(sl::kFeatureDLSS_G, "slDLSSGSetOptions");
        g.get_state = FeatureCall<PFun_slDLSSGGetState>(sl::kFeatureDLSS_G, "slDLSSGGetState");
        g.marker = FeatureCall<PFun_slPCLSetMarker>(sl::kFeaturePCL, "slPCLSetMarker");
        g.reflex = FeatureCall<PFun_slReflexSetOptions>(sl::kFeatureReflex, "slReflexSetOptions");
        if (!g.set_options || !g.get_state || !g.marker || !g.reflex)
            return down("Streamline's DLSS-G, Reflex or PC Latency calls are missing (%s, %d)", sl::Result::eErrorFeatureMissing);
        // DLSS-G needs Reflex on; low latency asks nothing more of the program.
        sl::ReflexOptions reflex{};
        reflex.mode = sl::ReflexMode::eLowLatency;
        r = g.reflex(reflex);
        if (r != sl::Result::eOk) return down("Reflex did not start (%s, %d)", r);
        if (!CountOutputInFront()) Trace("[sl] sl.common's foreground test could not be pointed at the output window - DLSS-G may stop while the game has the focus");
        sl::DLSSGState state{};
        r = g.get_state(sl::ViewportHandle(kViewport), state, nullptr);
        if (r != sl::Result::eOk) return down("DLSS-G gave no state (%s, %d)", r);
        g.most = std::max(1u, state.numFramesToGenerateMax);
        g.dynamic = state.bIsDynamicMFGSupported == sl::Boolean::eTrue;
        // The output window's queue through Streamline's proxy of the device: that is how DLSS-G learns which queue
        // presents. It takes the place of the engine's own output queue (lf_gpu_core.inl).
        lfCore.device->AddRef();
        void *proxy = lfCore.device;
        r = g.upgrade(&proxy);
        if (r != sl::Result::eOk) { lfCore.device->Release(); return down("no proxy of the device (%s, %d)", r); }
        g.proxy_device = static_cast<ID3D12Device *>(proxy);
        D3D12_COMMAND_QUEUE_DESC desc = {};
        desc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        desc.Priority = D3D12_COMMAND_QUEUE_PRIORITY_HIGH;
        ID3D12CommandQueue *queue = nullptr;
        if (FAILED(g.proxy_device->CreateCommandQueue(&desc, __uuidof(ID3D12CommandQueue), reinterpret_cast<void **>(&queue))) || !queue)
            return down("no output queue through Streamline (%s, %d)", sl::Result::eErrorD3DAPI);
        if (lfCore.show_queue) lfCore.show_queue->Release();
        lfCore.show_queue = queue;
        g.started = true;
        DXGI_ADAPTER_DESC1 card = {};
        if (lfCard.adapter) lfCard.adapter->GetDesc1(&card);
        Trace("[sl] NVIDIA Streamline started: DLSS Frame Generation on %ls, up to x%u on this card%s", card.Description,
              g.most + 1, g.dynamic ? ", Dynamic MFG offered" : "");
        return true;
    }

    // Streamline's proxy of the factory: a swap chain made through it is the one DLSS-G presents into.
    static IDXGIFactory2 *ProxyFactory(IDXGIFactory2 *native)
    {
        native->AddRef();
        void *proxy = native;
        if (g.upgrade(&proxy) != sl::Result::eOk) { native->Release(); return nullptr; }
        return static_cast<IDXGIFactory2 *>(proxy);
    }

    // The frames in between asked for: the app's multiplier (or a decimal one's whole part, or Adaptive's six),
    // never more than DLSS-G makes on this card. 0: frame generation off.
    static unsigned BetweenWanted()
    {
        if (smOpt.mfg.load() == 0) return 0;
        const int ratio = smOpt.ratio.load();
        const int multiplier = ratio > 10 ? ratio / 10 : smOpt.multiplier.load();
        const unsigned asked = static_cast<unsigned>(std::clamp(multiplier, 2, 6) - 1);
        if (asked > g.most && g.told_most_for != asked + 1)
        {
            g.told_most_for = asked + 1;
            Trace("[dlssg] multiplier x%u is the most DLSS MFG makes on this card - x%u was asked", g.most + 1, asked + 1);
        }
        return std::min(asked, g.most);
    }

    // The multiplier for the app's status line: running, 0 off, -1 cannot run.
    static int Multiplier()
    {
        if (smOpt.mfg.load() == 0) return 0;
        if (!Active()) return -1;
        return static_cast<int>(BetweenWanted()) + 1;
    }

    // DLSS-G off before the swap chain it presents into goes (ShowStop).
    static void BeforeChainGone()
    {
        if (!Active()) return;
        sl::DLSSGOptions off{};
        off.mode = sl::DLSSGMode::eOff;
        g.set_options(sl::ViewportHandle(kViewport), off);
        g.between = 0;
    }

    // The flat depth plane at the motion field's size (a picture of a screen has no depth; the camera is still).
    static bool DepthReady(UINT w, UINT h)
    {
        if (g.depth.gpu_tex && g.depth_w == w && g.depth_h == h) return true;
        if (g.depth.gpu_tex) { CmdDrain(); StagedRelease(g.depth); }
        if (!StagedCreate(g.depth, w, h, DXGI_FORMAT_R32_FLOAT, w * 4)) return false;
        std::vector<float> plane(static_cast<size_t>(w) * h, 0.5f);
        if (!StagedFill(g.depth, reinterpret_cast<BYTE *>(plane.data()), w * 4, h) || !CmdOpen()) return false;
        StagedCopy(lfCore.cmd, g.depth);
        auto b = Barrier(g.depth.gpu_tex, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        lfCore.cmd->ResourceBarrier(1, &b);
        if (!AwaitMark(lfCore.timeline, CmdSubmit(), 30000)) return false;
        g.depth_w = w; g.depth_h = h;
        return true;
    }

    static sl::float4x4 Identity()
    {
        sl::float4x4 m{};
        m.setRow(0, sl::float4(1, 0, 0, 0));
        m.setRow(1, sl::float4(0, 1, 0, 0));
        m.setRow(2, sl::float4(0, 0, 1, 0));
        m.setRow(3, sl::float4(0, 0, 0, 1));
        return m;
    }

    static sl::Resource ResourceOf(ID3D12Resource *texture, UINT w, UINT h, D3D12_RESOURCE_STATES state = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE)
    {
        const D3D12_RESOURCE_DESC d = texture->GetDesc();
        sl::Resource r(sl::ResourceType::eTex2d, texture, nullptr, nullptr, static_cast<uint32_t>(state));
        r.width = w;
        r.height = h;
        r.nativeFormat = static_cast<uint32_t>(d.Format);
        r.mipLevels = d.MipLevels;
        r.arrayLayers = d.DepthOrArraySize;
        r.flags = static_cast<uint32_t>(d.Flags);
        return r;
    }

    // Once a second, into the log: what was handed over, what DLSS-G says it presented, and what the swap chain
    // itself counted (its Present count, which every frame DLSS-G presents adds to).
    static void Second(unsigned shown_now)
    {
        ++g.handed;
        g.shown += shown_now;
        const ULONGLONG now = GetTickCount64();
        if (g.second_at == 0) g.second_at = now;
        if (now - g.second_at < 1000) return;
        const double seconds = (now - g.second_at) / 1000.0;
        UINT count = 0;
        const bool counted = lfShow.chain && SUCCEEDED(lfShow.chain->GetLastPresentCount(&count));
        char chain[96] = "the swap chain gave no count";
        if (counted && g.present_count_ok) sprintf_s(chain, "the swap chain counted %.1f presents a second", (count - g.present_count_at) / seconds);
        Trace("[sl] %.1f frames handed over a second, %.1f presented by DLSS-G (x%u asked, status 0x%X); %s", g.handed / seconds,
              g.shown / seconds, g.between + 1, g.status, chain);
        g.present_count_at = count;
        g.present_count_ok = counted;
        g.handed = g.shown = 0;
        g.second_at = now;
    }

    // One new real frame: into the back buffer, tagged and presented through Streamline; DLSS-G makes and presents
    // the frames in between. False: it was not shown (the loop makes a new window, as for any failed present).
    static bool Present(Pipeline &pipe)
    {
        const UINT mw = pipe.work_w, mh = pipe.work_h;
        if (!DepthReady(mw, mh)) { Trace("[sl] no depth plane for DLSS-G"); return false; }
        ID3D12Resource *back_buffer = nullptr;
        if (FAILED(ShowBackBuffer(__uuidof(ID3D12Resource), reinterpret_cast<void **>(&back_buffer))) || !back_buffer)
        { Trace("[sl] no back buffer"); return false; }
        bool ok = false;
        if (CmdOpen())
        {
            GpuStageOpen(GS_SHOW);
            D3D12_RESOURCE_BARRIER in[] = {
                Barrier(back_buffer, D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_COPY_DEST),
                Barrier(pipe.picture.gpu_tex, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COPY_SOURCE),
            };
            lfCore.cmd->ResourceBarrier(_countof(in), in);
            ShowBlit(lfCore.cmd, back_buffer, pipe.picture.gpu_tex);
            ShowProof(lfCore.cmd, back_buffer, true);
            CounterStamp(lfCore.cmd, back_buffer);
            BannerStamp(lfCore.cmd, back_buffer);
            D3D12_RESOURCE_BARRIER out[] = {
                Barrier(back_buffer, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_PRESENT),
                Barrier(pipe.picture.gpu_tex, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE),
            };
            lfCore.cmd->ResourceBarrier(_countof(out), out);
            GpuStageClose(GS_SHOW);
            ok = CmdSubmitShow(2000);
            if (!ok) Trace("[sl] waited too long for the frame's copy");
        }
        const D3D12_RESOURCE_DESC bb = back_buffer->GetDesc();
        // (Held until it is tagged below; the swap chain keeps it in any case.)
        struct Let { ID3D12Resource *r; ~Let() { r->Release(); } } let{ back_buffer };
        if (!ok) return false;

        sl::FrameToken *token = nullptr;
        if (g.new_token(token, nullptr) != sl::Result::eOk || !token) { Trace("[sl] no frame token"); return false; }
        g.marker(sl::PCLMarker::eSimulationStart, *token);
        g.marker(sl::PCLMarker::eSimulationEnd, *token);
        g.marker(sl::PCLMarker::eRenderSubmitStart, *token);
        // A picture of a screen has no camera: the motion vectors (in the motion field's pixels) carry all of its
        // motion, the depth plane is flat, the camera is the identity looking straight ahead.
        sl::Constants c{};
        c.cameraViewToClip = c.clipToCameraView = c.clipToLensClip = c.clipToPrevClip = c.prevClipToClip = Identity();
        c.jitterOffset = sl::float2(0, 0);
        c.mvecScale = sl::float2(1.0f / mw, 1.0f / mh);
        c.cameraPinholeOffset = sl::float2(0, 0);
        c.cameraPos = sl::float3(0, 0, 0);
        c.cameraUp = sl::float3(0, 1, 0);
        c.cameraRight = sl::float3(1, 0, 0);
        c.cameraFwd = sl::float3(0, 0, 1);
        c.cameraNear = 1.0f;
        c.cameraFar = 1000.0f;
        c.cameraFOV = 1.0f;
        c.cameraAspectRatio = static_cast<float>(bb.Width) / static_cast<float>(bb.Height);
        c.motionVectorsInvalidValue = 0.0f;
        c.depthInverted = sl::Boolean::eFalse;
        c.cameraMotionIncluded = sl::Boolean::eTrue;
        c.motionVectors3D = sl::Boolean::eFalse;
        c.reset = lfShow.history_restart ? sl::Boolean::eTrue : sl::Boolean::eFalse;
        c.orthographicProjection = sl::Boolean::eFalse;
        c.motionVectorsDilated = sl::Boolean::eFalse;
        c.motionVectorsJittered = sl::Boolean::eFalse;
        c.minRelativeLinearDepthObjectSeparation = 40.0f;
        sl::Result r = g.set_constants(c, *token, sl::ViewportHandle(kViewport));
        if (r != sl::Result::eOk) Trace("[sl] constants refused (%s)", ResultText(r));
        // The motion field and the depth plane stay as they are until DLSS-G has read them (the next frame's work
        // waits for it: below), and the back buffer is the whole picture. No HUD-less picture is tagged: a picture
        // taken of the screen already has the game's HUD in it, so there is none to give.
        sl::Resource motion = ResourceOf(pipe.motion.gpu_tex, mw, mh);
        sl::Resource depth = ResourceOf(g.depth.gpu_tex, mw, mh);
        sl::Resource back = ResourceOf(back_buffer, static_cast<UINT>(bb.Width), bb.Height, D3D12_RESOURCE_STATE_PRESENT);
        const sl::Extent extent{ 0, 0, mw, mh };
        const sl::Extent whole{ 0, 0, static_cast<uint32_t>(bb.Width), bb.Height };
        const sl::ResourceTag tags[] = { sl::ResourceTag(&motion, sl::kBufferTypeMotionVectors, sl::ResourceLifecycle::eValidUntilPresent, &extent),
                                         sl::ResourceTag(&depth, sl::kBufferTypeDepth, sl::ResourceLifecycle::eValidUntilPresent, &extent),
                                         sl::ResourceTag(&back, sl::kBufferTypeBackbuffer, sl::ResourceLifecycle::eValidUntilPresent, &whole) };
        r = g.set_tags(*token, sl::ViewportHandle(kViewport), tags, static_cast<uint32_t>(_countof(tags)), nullptr);
        if (r != sl::Result::eOk) Trace("[sl] tags refused (%s)", ResultText(r));
        const unsigned between = BetweenWanted();
        sl::DLSSGOptions o{};
        const bool dynamic = between > 0 && g.dynamic && smOpt.adaptive.load() > 0;
        o.mode = between == 0 ? sl::DLSSGMode::eOff : dynamic ? sl::DLSSGMode::eDynamic : sl::DLSSGMode::eOn;
        o.numFramesToGenerate = std::max(1u, between);
        o.numBackBuffers = kBackBuffers;
        o.mvecDepthWidth = mw;
        o.mvecDepthHeight = mh;
        o.colorWidth = static_cast<uint32_t>(bb.Width);
        o.colorHeight = bb.Height;
        o.colorBufferFormat = static_cast<uint32_t>(bb.Format);
        o.mvecBufferFormat = static_cast<uint32_t>(pipe.motion.gpu_tex->GetDesc().Format);
        o.depthBufferFormat = static_cast<uint32_t>(DXGI_FORMAT_R32_FLOAT);
        o.queueParallelismMode = sl::DLSSGQueueParallelismMode::eBlockPresentingClientQueue;
        o.dynamicTargetFrameRate = dynamic && smOpt.adaptive.load() >= 30 ? static_cast<float>(smOpt.adaptive.load()) : 0.0f;
        r = g.set_options(sl::ViewportHandle(kViewport), o);
        if (r != sl::Result::eOk) Trace("[sl] DLSS-G options refused (%s)", ResultText(r));
        if (between != g.between)
        {
            if (between) Trace("[sl] DLSS-G on: x%u (%u frame%s in between%s)", between + 1, between, between == 1 ? "" : "s", dynamic ? ", Dynamic MFG" : "");
            else Trace("[sl] DLSS-G off: the real frames only");
            g.between = between;
        }
        g.marker(sl::PCLMarker::eRenderSubmitEnd, *token);
        g.marker(sl::PCLMarker::ePresentStart, *token);
        if (PhasesOn()) lfProf.stamp.present_call = PhaseClock();
        const HRESULT hr = lfShow.chain->Present(0, lfShow.tearing ? DXGI_PRESENT_ALLOW_TEARING : 0u);
        g.marker(sl::PCLMarker::ePresentEnd, *token);
        if (!ShowResult(hr, "present through Streamline")) { lfProf.stamp.present_call = 0.0; return false; }
        LatencyPresented(lfCap.stamp);
        CounterPresented(-1.0, true);
        // What DLSS-G presented for it - this real frame and the ones it made: the counter's "all frames" counts them.
        sl::DLSSGState state{};
        unsigned shown = 1;
        if (g.get_state(sl::ViewportHandle(kViewport), state, nullptr) == sl::Result::eOk)
        {
            if (static_cast<uint32_t>(state.status) != g.status)
            {
                g.status = static_cast<uint32_t>(state.status);
                Trace("[sl] DLSS-G status 0x%X%s", g.status, g.status == 0 ? " (ok)" : " - it makes no frames until this clears");
            }
            if (between > 0 && state.numFramesActuallyPresented > 0) shown = state.numFramesActuallyPresented;
            // The engine's own queue writes the motion field (and the next picture) for the next frame: not before
            // DLSS-G has read this frame's inputs on the queue that presents (Streamline's word for it).
            if (state.inputsProcessingCompletionFence && state.lastPresentInputsProcessingCompletionFenceValue)
                lfCore.direct_queue->Wait(static_cast<ID3D12Fence *>(state.inputsProcessingCompletionFence),
                                          state.lastPresentInputsProcessingCompletionFenceValue);
        }
        if (shown > 1) CounterAddShown(shown - 1);
        Second(shown);
        front::Tell();
        ShowRevealOnce();
        return true;
    }

    // At the end, after the swap chain is gone (SessionRelease) and before the device is.
    static void Shutdown()
    {
        if (!g.started) return;
        CmdDrain();
        StagedRelease(g.depth);
        g.shutdown();
        g.started = false;
        Trace("[sl] Streamline shut down");
    }
}
