#include "effects/real/streamline.h"

#include "effects/real/window.h"
#include "global_common.h"

#include <sl.h>
#include <sl_consts.h>
#include <sl_dlss_g.h>
#include <sl_pcl.h>
#include <sl_reflex.h>

#include <algorithm>
#include <array>
#include <optional>
#include <ranges>

namespace real {

namespace {

// The calls found by name in sl.interposer.dll itself.
struct CoreCalls
{
    PFun_slInit* init;
    PFun_slShutdown* shutdown;
    PFun_slSetD3DDevice* setDevice;
    PFun_slIsFeatureSupported* isSupported;
    PFun_slGetFeatureFunction* featureFunction;
    PFun_slUpgradeInterface* upgrade;
    PFun_slGetNewFrameToken* newFrameToken;
    PFun_slSetConstants* setConstants;
    PFun_slSetTagForFrame* setTags;
};

// The calls a feature hands out once Streamline has its device.
struct FeatureCalls
{
    PFun_slDLSSGSetOptions* setOptions;
    PFun_slDLSSGGetState* getState;
    PFun_slPCLSetMarker* setMarker;
    PFun_slReflexSetOptions* setReflex;
};

} // namespace

struct StreamlineCalls
{
    CoreCalls core;
    FeatureCalls features;
    std::uint32_t mostBetween; // the most pictures DLSS Frame Generation makes between two on this GPU
};

void StreamlineEnder::operator()(const StreamlineCalls* calls) const noexcept
{
    // WAIVER(R17): the session is over and nothing waits on the answer; a refused shutdown is in Streamline's own log.
    (void)calls->core.shutdown();
    delete calls;
}

namespace {

using infra::Fail;
using infra::Result;
using infra::Status;

constexpr std::wstring_view kInterposer = L"\\sl.interposer.dll";
constexpr std::size_t kInterposerPathCapacity = interior::DirectoryPath::Capacity + kInterposer.size() + 1;
constexpr std::uint32_t kViewport = 0;
constexpr std::uint32_t kTagCount = 2;
// Import tables are walked no further than this: sl.common.dll imports from a few dozen libraries, a few
// hundred calls in all.
constexpr std::uint32_t kMaxImportedLibraries = 128;
constexpr std::uint32_t kMaxImportsPerLibrary = 4096;
constexpr std::array<sl::Feature, 3> kFeatures{ sl::kFeatureDLSS_G, sl::kFeatureReflex, sl::kFeaturePCL };

[[nodiscard]] Status<Error> CheckSl(sl::Result result, ApiCall call) noexcept
{
    if (result != sl::Result::eOk)
        return Fail(Error{ call, static_cast<std::uint32_t>(result) });
    return {};
}

[[nodiscard]] std::array<wchar_t, kInterposerPathCapacity> InterposerPathIn(std::wstring_view directory) noexcept
{
    std::array<wchar_t, kInterposerPathCapacity> chars{}; // WAIVER(R2): a local buffer filled once, before use, from two bounded pieces.
    std::ranges::copy(directory, chars.begin());
    std::ranges::copy(kInterposer, chars.begin() + static_cast<std::ptrdiff_t>(directory.size()));
    return chars;
}

[[nodiscard]] Result<interior::FilePath, Error> InterposerPath(const interior::DirectoryPath& directory) noexcept
{
    return interior::FilePath::Parse(InterposerPathIn(directory.Get()).data()).transform_error([](infra::StringTooLong) { return Error{ ApiCall::LoadStreamline, ERROR_FILENAME_EXCED_RANGE }; });
}

// Loaded by its full path, with what it loads in turn taken from its own folder or the system folder only. It is
// never let go: see Streamline.
[[nodiscard]] Result<HMODULE, Error> LoadInterposer(const interior::FilePath& path) noexcept
{
    const HMODULE module = ::LoadLibraryExW(path.CString(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (module == nullptr)
        return Fail(LastError(ApiCall::LoadStreamline));
    return module;
}

template <class Call>
[[nodiscard]] Call* ExportOrNothing(HMODULE module, const char* name) noexcept
{
    return reinterpret_cast<Call*>(reinterpret_cast<void*>(::GetProcAddress(module, name)));
}

[[nodiscard]] bool HasEveryCall(const CoreCalls& c) noexcept
{
    return c.init != nullptr && c.shutdown != nullptr && c.setDevice != nullptr && c.isSupported != nullptr && c.featureFunction != nullptr && c.upgrade != nullptr && c.newFrameToken != nullptr &&
           c.setConstants != nullptr && c.setTags != nullptr;
}

[[nodiscard]] Result<CoreCalls, Error> CoreCallsOf(HMODULE module) noexcept
{
    const CoreCalls calls{ .init = ExportOrNothing<PFun_slInit>(module, "slInit"),
                           .shutdown = ExportOrNothing<PFun_slShutdown>(module, "slShutdown"),
                           .setDevice = ExportOrNothing<PFun_slSetD3DDevice>(module, "slSetD3DDevice"),
                           .isSupported = ExportOrNothing<PFun_slIsFeatureSupported>(module, "slIsFeatureSupported"),
                           .featureFunction = ExportOrNothing<PFun_slGetFeatureFunction>(module, "slGetFeatureFunction"),
                           .upgrade = ExportOrNothing<PFun_slUpgradeInterface>(module, "slUpgradeInterface"),
                           .newFrameToken = ExportOrNothing<PFun_slGetNewFrameToken>(module, "slGetNewFrameToken"),
                           .setConstants = ExportOrNothing<PFun_slSetConstants>(module, "slSetConstants"),
                           .setTags = ExportOrNothing<PFun_slSetTagForFrame>(module, "slSetTagForFrame") };
    if (!HasEveryCall(calls))
        return Fail(Error{ ApiCall::LoadStreamline, ERROR_PROC_NOT_FOUND });
    return calls;
}

// Manual hooking: only the device's queue and the factory's swap chain go through Streamline's proxies, which
// the code asks for by name; everything else, the models' work included, stays on the native device. The
// frames' resources are tagged for the frame they belong to. Nothing is fetched over the network.
[[nodiscard]] sl::Preferences PreferencesOf(const wchar_t* const* pluginPaths, const char* projectId) noexcept
{
    // WAIVER(R1): Streamline's records have constructors, so this one is made and then given every field by name.
    sl::Preferences preferences{};
    preferences.showConsole = false;
    preferences.logLevel = sl::LogLevel::eDefault;
    preferences.pathsToPlugins = const_cast<const wchar_t**>(pluginPaths);
    preferences.numPathsToPlugins = 1;
    preferences.pathToLogsAndData = nullptr;
    preferences.allocateCallback = nullptr;
    preferences.releaseCallback = nullptr;
    preferences.logMessageCallback = nullptr;
    preferences.flags = sl::PreferenceFlags::eDisableCLStateTracking | sl::PreferenceFlags::eUseManualHooking | sl::PreferenceFlags::eUseFrameBasedResourceTagging;
    preferences.featuresToLoad = kFeatures.data();
    preferences.numFeaturesToLoad = static_cast<std::uint32_t>(kFeatures.size());
    preferences.applicationId = 0;
    preferences.engine = sl::EngineType::eCustom;
    preferences.engineVersion = DSCREEN_VERSION_STRING;
    preferences.projectId = projectId;
    preferences.renderAPI = sl::RenderAPI::eD3D12;
    return preferences;
}

[[nodiscard]] Status<Error> Initialized(const CoreCalls& core, const interior::DirectoryPath& directory, const interior::ProjectIdText& projectId) noexcept
{
    const std::array<const wchar_t*, 1> pluginPaths{ directory.CString() };
    return CheckSl(core.init(PreferencesOf(pluginPaths.data(), projectId.CString()), sl::kSDKVersion), ApiCall::StreamlineInit);
}

[[nodiscard]] Result<LUID, Error> LuidOf(IDXGIAdapter1* adapter) noexcept
{
    DXGI_ADAPTER_DESC1 description{};
    return Check(adapter->GetDesc1(&description), ApiCall::EnumAdapters1).transform([&description] { return description.AdapterLuid; });
}

[[nodiscard]] Status<Error> Supported(const CoreCalls& core, IDXGIAdapter1* adapter) noexcept
{
    static constexpr auto SupportedOn = [] [[nodiscard]] (const CoreCalls& core, LUID luid) noexcept -> Status<Error> {
        // WAIVER(R1): Streamline's records have constructors, so this one is made and then given every field by name.
        sl::AdapterInfo info{};
        info.deviceLUID = reinterpret_cast<uint8_t*>(&luid);
        info.deviceLUIDSizeInBytes = sizeof(LUID);
        info.vkPhysicalDevice = nullptr;
        return CheckSl(core.isSupported(sl::kFeatureDLSS_G, info), ApiCall::FrameGenerationUnsupported);
    };
    return LuidOf(adapter).and_then([&core](LUID luid) { return SupportedOn(core, luid); });
}

template <class Call>
[[nodiscard]] Call* FeatureCallOrNothing(const CoreCalls& core, sl::Feature feature, const char* name) noexcept
{
    void* call = nullptr; // WAIVER(R2): slGetFeatureFunction's out-parameter, read once after.
    if (core.featureFunction(feature, name, call) != sl::Result::eOk)
        return nullptr;
    return reinterpret_cast<Call*>(call);
}

[[nodiscard]] bool HasEveryCall(const FeatureCalls& c) noexcept
{
    return c.setOptions != nullptr && c.getState != nullptr && c.setMarker != nullptr && c.setReflex != nullptr;
}

[[nodiscard]] Result<FeatureCalls, Error> FeatureCallsOf(const CoreCalls& core) noexcept
{
    const FeatureCalls calls{ .setOptions = FeatureCallOrNothing<PFun_slDLSSGSetOptions>(core, sl::kFeatureDLSS_G, "slDLSSGSetOptions"),
                              .getState = FeatureCallOrNothing<PFun_slDLSSGGetState>(core, sl::kFeatureDLSS_G, "slDLSSGGetState"),
                              .setMarker = FeatureCallOrNothing<PFun_slPCLSetMarker>(core, sl::kFeaturePCL, "slPCLSetMarker"),
                              .setReflex = FeatureCallOrNothing<PFun_slReflexSetOptions>(core, sl::kFeatureReflex, "slReflexSetOptions") };
    if (!HasEveryCall(calls))
        return Fail(Error{ ApiCall::StreamlineInit, static_cast<std::uint32_t>(sl::Result::eErrorFeatureMissing) });
    return calls;
}

// DLSS Frame Generation needs Reflex running; low latency is the mode that asks nothing else of the program.
[[nodiscard]] Status<Error> ReflexStarted(const FeatureCalls& features) noexcept
{
    // WAIVER(R1): Streamline's records have constructors, so this one is made and then given every field by name.
    sl::ReflexOptions options{};
    options.mode = sl::ReflexMode::eLowLatency;
    options.frameLimitUs = 0;
    options.useMarkersToOptimize = false;
    options.virtualKey = 0;
    options.idThread = 0;
    return CheckSl(features.setReflex(options), ApiCall::StreamlineInit);
}

[[nodiscard]] Result<std::uint32_t, Error> MostBetween(const FeatureCalls& features) noexcept
{
    sl::DLSSGState state{}; // WAIVER(R2): slDLSSGGetState's out-parameter, read once after.
    return CheckSl(features.getState(sl::ViewportHandle(kViewport), state, nullptr), ApiCall::StreamlineState).transform([&state] { return state.numFramesToGenerateMax; });
}

// sl.common.dll takes the program to be in front only when the foreground window is one of the program's own,
// and DLSS Frame Generation stops making pictures otherwise ("DLSS-G disabled: window not focused" in its log;
// Keyboard::hasFocus in Streamline 2.14.16, source/core/sl.extra/extra.cpp). The overlay never has the foreground:
// it is a click-through window over the program it shows, which keeps it. So sl.common.dll's import of
// GetForegroundWindow is pointed here, and answers with the overlay.
[[nodiscard]] HWND WINAPI OverlayInFront() noexcept
{
    return ::FindWindowW(kOutputWindowClass, nullptr);
}

[[nodiscard]] bool IsForegroundCall(ULONG_PTR call, ULONG_PTR system) noexcept
{
    return call == system || call == reinterpret_cast<ULONG_PTR>(&OverlayInFront);
}

[[nodiscard]] std::optional<ULONG_PTR*> SlotAmong(BYTE* base, const IMAGE_IMPORT_DESCRIPTOR& library, ULONG_PTR system) noexcept
{
    IMAGE_THUNK_DATA* const calls = reinterpret_cast<IMAGE_THUNK_DATA*>(base + library.FirstThunk);
    const auto isListed = [calls](std::uint32_t i) noexcept { return calls[i].u1.Function != 0; };
    const auto isForeground = [calls, system](std::uint32_t i) noexcept { return IsForegroundCall(calls[i].u1.Function, system); };
    auto listed = std::views::iota(std::uint32_t{ 0 }, kMaxImportsPerLibrary) | std::views::take_while(isListed);
    const auto found = std::ranges::find_if(listed, isForeground);
    if (found == std::ranges::end(listed))
        return std::nullopt;
    return &calls[*found].u1.Function;
}

[[nodiscard]] std::optional<ULONG_PTR*> ForegroundSlot(HMODULE module, ULONG_PTR system) noexcept
{
    BYTE* const base = reinterpret_cast<BYTE*>(module);
    const IMAGE_NT_HEADERS* const headers = reinterpret_cast<const IMAGE_NT_HEADERS*>(base + reinterpret_cast<const IMAGE_DOS_HEADER*>(base)->e_lfanew);
    const IMAGE_IMPORT_DESCRIPTOR* const libraries = reinterpret_cast<const IMAGE_IMPORT_DESCRIPTOR*>(base + headers->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress);
    const auto isListed = [libraries](std::uint32_t i) noexcept { return libraries[i].Name != 0; };
    const auto firstFound = [&](const std::optional<ULONG_PTR*>& found, std::uint32_t i) noexcept { return found.has_value() ? found : SlotAmong(base, libraries[i], system); };
    return std::ranges::fold_left(std::views::iota(std::uint32_t{ 0 }, kMaxImportedLibraries) | std::views::take_while(isListed), std::optional<ULONG_PTR*>{}, firstFound);
}

[[nodiscard]] Status<Error> Repointed(ULONG_PTR* slot, ULONG_PTR to) noexcept
{
    DWORD was = 0;    // WAIVER(R2): VirtualProtect's out-parameter, read once after.
    DWORD unused = 0; // WAIVER(R2): VirtualProtect's out-parameter, not read.
    if (::VirtualProtect(slot, sizeof(ULONG_PTR), PAGE_READWRITE, &was) == FALSE)
        return Fail(LastError(ApiCall::StreamlineFocus));
    *slot = to; // WAIVER(R2): the one write this patch is, into sl.common.dll's import table in this process.
    return CheckBool(::VirtualProtect(slot, sizeof(ULONG_PTR), was, &unused), ApiCall::StreamlineFocus);
}

[[nodiscard]] Status<Error> CountOverlayInFront() noexcept
{
    const HMODULE common = ::GetModuleHandleW(L"sl.common.dll");
    const HMODULE user = ::GetModuleHandleW(L"user32.dll");
    if (common == nullptr || user == nullptr)
        return Fail(Error{ ApiCall::StreamlineFocus, ERROR_MOD_NOT_FOUND });
    const ULONG_PTR system = reinterpret_cast<ULONG_PTR>(::GetProcAddress(user, "GetForegroundWindow"));
    const std::optional<ULONG_PTR*> slot = ForegroundSlot(common, system);
    if (!slot.has_value())
        return Fail(Error{ ApiCall::StreamlineFocus, ERROR_PROC_NOT_FOUND });
    return Repointed(*slot, reinterpret_cast<ULONG_PTR>(&OverlayInFront));
}

// Everything after slInit; a failure in any of it shuts Streamline down again before it is reported.
[[nodiscard]] Result<const StreamlineCalls*, Error> Attached(const CoreCalls& core, ID3D12Device* device, IDXGIAdapter1* adapter) noexcept
{
    static constexpr auto Assembled = [] [[nodiscard]] (const CoreCalls& core, const FeatureCalls& features, std::uint32_t most) noexcept -> const StreamlineCalls* {
        return new StreamlineCalls{ core, features, most };
    };

    static constexpr auto ShutDown = [] [[nodiscard]] (const CoreCalls& core, const Error& error) noexcept -> Result<const StreamlineCalls*, Error> {
        // WAIVER(R17): the failure that brought this here is the one reported; a refused shutdown is in Streamline's own log.
        (void)core.shutdown();
        return Fail(error);
    };
    return CheckSl(core.setDevice(device), ApiCall::StreamlineDevice)
        .and_then([&core, adapter] { return Supported(core, adapter); })
        .and_then([&core] { return FeatureCallsOf(core); })
        .and_then([&core](const FeatureCalls& features) {
            return ReflexStarted(features).and_then(CountOverlayInFront).and_then([&features] { return MostBetween(features); }).transform([&core, &features](std::uint32_t most) {
                return Assembled(core, features, most);
            });
        })
        .or_else([&core](const Error& error) { return ShutDown(core, error); });
}

template <class Interface>
[[nodiscard]] Result<Com<Interface>, Error> Upgraded(const Streamline& streamline, Interface* native) noexcept
{
    static constexpr auto Owned = [] [[nodiscard]] (Interface * proxy) noexcept -> Com<Interface> {
        Com<Interface> owned; // WAIVER(R2): takes over the reference slUpgradeInterface handed back.
        owned.Attach(proxy);
        return owned;
    };
    native->AddRef();
    Interface* upgraded = native; // WAIVER(R2): slUpgradeInterface replaces the pointer it is given with its proxy.
    const sl::Result result = streamline.calls->core.upgrade(reinterpret_cast<void**>(&upgraded));
    return CheckSl(result, ApiCall::StreamlineUpgrade).transform([upgraded] { return Owned(upgraded); }).or_else([native](const Error& error) -> Result<Com<Interface>, Error> {
        native->Release();
        return Fail(error);
    });
}

[[nodiscard]] Status<Error> Marked(const Streamline& streamline, sl::PCLMarker marker, const sl::FrameToken& token) noexcept
{
    return CheckSl(streamline.calls->features.setMarker(marker, token), ApiCall::StreamlineFrame);
}

[[nodiscard]] Result<sl::FrameToken*, Error> NewToken(const Streamline& streamline) noexcept
{
    sl::FrameToken* token = nullptr; // WAIVER(R2): slGetNewFrameToken's out-parameter, read once after.
    return CheckSl(streamline.calls->core.newFrameToken(token, nullptr), ApiCall::StreamlineFrame).transform([&token] { return token; });
}

[[nodiscard]] sl::float4x4 Identity() noexcept
{
    sl::float4x4 identity{}; // WAIVER(R2): a matrix given its four rows once, before use.
    identity.setRow(0, sl::float4(1.0f, 0.0f, 0.0f, 0.0f));
    identity.setRow(1, sl::float4(0.0f, 1.0f, 0.0f, 0.0f));
    identity.setRow(2, sl::float4(0.0f, 0.0f, 1.0f, 0.0f));
    identity.setRow(3, sl::float4(0.0f, 0.0f, 0.0f, 1.0f));
    return identity;
}

[[nodiscard]] sl::Boolean BooleanOf(bool value) noexcept
{
    return value ? sl::Boolean::eTrue : sl::Boolean::eFalse;
}

// A picture of a screen has no camera: the motion vectors carry all of its motion, in pixels of the motion
// texture, and the depth plane is flat. The camera is the identity, looking straight ahead.
[[nodiscard]] sl::Constants ConstantsOf(const GeneratedFrame& frame) noexcept
{
    // WAIVER(R1): Streamline's records have constructors, so this one is made and then given every field by name.
    sl::Constants constants{};
    constants.cameraViewToClip = Identity();
    constants.clipToCameraView = Identity();
    constants.clipToLensClip = Identity();
    constants.clipToPrevClip = Identity();
    constants.prevClipToClip = Identity();
    constants.jitterOffset = sl::float2(0.0f, 0.0f);
    constants.mvecScale = sl::float2(1.0f / static_cast<float>(frame.generation.motion.width.Get()), 1.0f / static_cast<float>(frame.generation.motion.height.Get()));
    constants.cameraPinholeOffset = sl::float2(0.0f, 0.0f);
    constants.cameraPos = sl::float3(0.0f, 0.0f, 0.0f);
    constants.cameraUp = sl::float3(0.0f, 1.0f, 0.0f);
    constants.cameraRight = sl::float3(1.0f, 0.0f, 0.0f);
    constants.cameraFwd = sl::float3(0.0f, 0.0f, 1.0f);
    constants.cameraNear = 1.0f;
    constants.cameraFar = 1000.0f;
    constants.cameraFOV = 1.0f;
    constants.cameraAspectRatio = static_cast<float>(frame.colorExtent.width.Get()) / static_cast<float>(frame.colorExtent.height.Get());
    constants.motionVectorsInvalidValue = 0.0f;
    constants.depthInverted = sl::Boolean::eFalse;
    constants.cameraMotionIncluded = sl::Boolean::eTrue;
    constants.motionVectors3D = sl::Boolean::eFalse;
    constants.reset = BooleanOf(frame.generation.reset);
    constants.orthographicProjection = sl::Boolean::eFalse;
    constants.motionVectorsDilated = sl::Boolean::eFalse;
    constants.motionVectorsJittered = sl::Boolean::eFalse;
    constants.minRelativeLinearDepthObjectSeparation = 40.0f;
    return constants;
}

// What Streamline is told about a texture: its size, as the session made it, and the rest read from the texture.
[[nodiscard]] sl::Resource ResourceOf(ID3D12Resource* texture, const interior::Extent& extent) noexcept
{
    // WAIVER(R1): Streamline's records have constructors, so this one is made and then given every field by name.
    const D3D12_RESOURCE_DESC description = texture->GetDesc();
    sl::Resource resource(sl::ResourceType::eTex2d, texture, nullptr, nullptr, static_cast<std::uint32_t>(D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE | D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE));
    resource.width = extent.width.Get();
    resource.height = extent.height.Get();
    resource.nativeFormat = static_cast<std::uint32_t>(description.Format);
    resource.mipLevels = description.MipLevels;
    resource.arrayLayers = description.DepthOrArraySize;
    resource.gpuVirtualAddress = 0;
    resource.flags = static_cast<std::uint32_t>(description.Flags);
    resource.usage = 0;
    resource.internalFlags = sl::Resource::eNone;
    resource.reserved = 0;
    return resource;
}

[[nodiscard]] sl::Extent WholeOf(const interior::Extent& extent) noexcept
{
    return sl::Extent{ .top = 0, .left = 0, .width = extent.width.Get(), .height = extent.height.Get() };
}

// Both are read when the picture is presented and left alone until the frame after, which is the lifetime asked for.
[[nodiscard]] Status<Error> Tagged(const Streamline& streamline, const sl::FrameToken& token, const GeneratedFrame& frame) noexcept
{
    sl::Resource motion = ResourceOf(frame.motion, frame.generation.motion); // WAIVER(R2): Streamline takes the records by pointer, so they are held here while it reads them.
    sl::Resource depth = ResourceOf(frame.depth, frame.generation.motion);   // WAIVER(R2): as above.
    const sl::Extent extent = WholeOf(frame.generation.motion);
    const std::array<sl::ResourceTag, kTagCount> tags{ sl::ResourceTag(&motion, sl::kBufferTypeMotionVectors, sl::ResourceLifecycle::eValidUntilPresent, &extent),
                                                       sl::ResourceTag(&depth, sl::kBufferTypeDepth, sl::ResourceLifecycle::eValidUntilPresent, &extent) };
    return CheckSl(streamline.calls->core.setTags(token, sl::ViewportHandle(kViewport), tags.data(), kTagCount, nullptr), ApiCall::StreamlineFrame);
}

[[nodiscard]] bool IsGenerating(const interior::Generation& generation) noexcept
{
    return generation.multiplier.Get() > 1;
}

// One picture shown for one made is DLSS Frame Generation off; more asks for one fewer made between, up to what
// this GPU can make.
[[nodiscard]] std::uint32_t BetweenOf(const interior::Generation& generation, std::uint32_t most) noexcept
{
    return std::clamp(generation.multiplier.Get() - 1u, 1u, std::max(most, 1u));
}

[[nodiscard]] sl::DLSSGOptions OptionsOf(const GeneratedFrame& frame, std::uint32_t most) noexcept
{
    // WAIVER(R1): Streamline's records have constructors, so this one is made and then given every field by name.
    sl::DLSSGOptions options{};
    options.mode = IsGenerating(frame.generation) ? sl::DLSSGMode::eOn : sl::DLSSGMode::eOff;
    options.numFramesToGenerate = BetweenOf(frame.generation, most);
    options.flags = static_cast<sl::DLSSGFlags>(0);
    options.dynamicResWidth = 0;
    options.dynamicResHeight = 0;
    options.numBackBuffers = interior::kBackBufferCount;
    options.mvecDepthWidth = frame.generation.motion.width.Get();
    options.mvecDepthHeight = frame.generation.motion.height.Get();
    options.colorWidth = frame.colorExtent.width.Get();
    options.colorHeight = frame.colorExtent.height.Get();
    options.colorBufferFormat = DXGI_FORMAT_R8G8B8A8_UNORM;
    options.mvecBufferFormat = static_cast<std::uint32_t>(frame.motion->GetDesc().Format);
    options.depthBufferFormat = static_cast<std::uint32_t>(frame.depth->GetDesc().Format);
    options.hudLessBufferFormat = DXGI_FORMAT_UNKNOWN;
    options.uiBufferFormat = DXGI_FORMAT_UNKNOWN;
    options.onErrorCallback = nullptr;
    options.queueParallelismMode = sl::DLSSGQueueParallelismMode::eBlockPresentingClientQueue;
    options.enableUserInterfaceRecomposition = sl::Boolean::eFalse;
    options.dynamicTargetFrameRate = 0.0f;
    return options;
}

[[nodiscard]] Status<Error> Configured(const Streamline& streamline, const GeneratedFrame& frame) noexcept
{
    return CheckSl(streamline.calls->features.setOptions(sl::ViewportHandle(kViewport), OptionsOf(frame, streamline.calls->mostBetween)), ApiCall::StreamlineFrame);
}

[[nodiscard]] Status<Error> Presented(IDXGISwapChain3* swapChain, bool vsync) noexcept
{
    return Check(swapChain->Present(vsync ? 1u : 0u, 0), ApiCall::Present);
}

} // namespace

Result<Streamline, Error> StartStreamline(const interior::DirectoryPath& executableDirectory, const interior::ProjectIdText& projectId, ID3D12Device* device, IDXGIAdapter1* adapter) noexcept
{
    static constexpr auto Started = [] [[nodiscard]] (TrustedFile file, const StreamlineCalls* calls) noexcept -> Streamline {
        return Streamline{ std::move(file), std::unique_ptr<const StreamlineCalls, StreamlineEnder>(calls) };
    };
    return InterposerPath(executableDirectory).and_then([&](const interior::FilePath& path) {
        return OpenApproved(path, ModelKind::Streamline).and_then([&](TrustedFile file) {
            return LoadInterposer(path).and_then(CoreCallsOf).and_then([&](const CoreCalls& core) {
                return Initialized(core, executableDirectory, projectId).and_then([&] { return Attached(core, device, adapter); }).transform([&file](const StreamlineCalls* calls) {
                    return Started(std::move(file), calls);
                });
            });
        });
    });
}

Result<ProxyQueue, Error> CreateProxyQueue(const Streamline& streamline, ID3D12Device* device) noexcept
{
    static constexpr auto QueueOn = [] [[nodiscard]] (const Com<ID3D12Device>& proxy) noexcept -> Result<ProxyQueue, Error> {
        const D3D12_COMMAND_QUEUE_DESC desc{ .Type = D3D12_COMMAND_LIST_TYPE_DIRECT, .Priority = 0, .Flags = D3D12_COMMAND_QUEUE_FLAG_NONE, .NodeMask = 0 };
        Com<ID3D12CommandQueue> queue;
        const HRESULT hr = proxy->CreateCommandQueue(&desc, IID_PPV_ARGS(&queue));
        return Check(hr, ApiCall::CreateCommandQueue).transform([&proxy, &queue] { return ProxyQueue{ proxy, queue }; });
    };
    return Upgraded(streamline, device).and_then(QueueOn);
}

Result<Com<IDXGIFactory2>, Error> ProxyFactory(const Streamline& streamline, IDXGIFactory4* factory) noexcept
{
    return Upgraded<IDXGIFactory2>(streamline, factory);
}

Status<Error> PresentGenerated(const Streamline& streamline, IDXGISwapChain3* swapChain, const GeneratedFrame& frame, bool vsync) noexcept
{
    return NewToken(streamline).and_then([&](sl::FrameToken* token) {
        return Marked(streamline, sl::PCLMarker::eSimulationStart, *token)
            .and_then([&] { return Marked(streamline, sl::PCLMarker::eSimulationEnd, *token); })
            .and_then([&] { return Marked(streamline, sl::PCLMarker::eRenderSubmitStart, *token); })
            .and_then([&] { return CheckSl(streamline.calls->core.setConstants(ConstantsOf(frame), *token, sl::ViewportHandle(kViewport)), ApiCall::StreamlineFrame); })
            .and_then([&] { return Tagged(streamline, *token, frame); })
            .and_then([&] { return Configured(streamline, frame); })
            .and_then([&] { return Marked(streamline, sl::PCLMarker::eRenderSubmitEnd, *token); })
            .and_then([&] { return Marked(streamline, sl::PCLMarker::ePresentStart, *token); })
            .and_then([&] { return Presented(swapChain, vsync); })
            .and_then([&] { return Marked(streamline, sl::PCLMarker::ePresentEnd, *token); });
    });
}

Result<GenerationReport, Error> ReportGeneration(const Streamline& streamline) noexcept
{
    sl::DLSSGState state{}; // WAIVER(R2): slDLSSGGetState's out-parameter, read once after.
    return CheckSl(streamline.calls->features.getState(sl::ViewportHandle(kViewport), state, nullptr), ApiCall::StreamlineState).transform([&state] {
        return GenerationReport{ .shownPerPicture = state.numFramesActuallyPresented, .mostBetween = state.numFramesToGenerateMax, .status = static_cast<std::uint32_t>(state.status) };
    });
}

} // namespace real
