#pragma once
// WAIVER(R31): the COM helpers below are the effect interface for the Windows layer.
#include "infrastructure/bounded_string.h"
#include "infrastructure/contracts.h"
#include "infrastructure/result.h"

#include <unknwn.h>
#include <windows.h>
#include <wrl/client.h>

#include <cstdint>
#include <memory>
#include <string_view>
#include <type_traits>

namespace real {

enum class ApiCall : std::uint8_t {
    CreateDXGIFactory2,
    EnumAdapters1,
    D3D12CreateDevice,
    CreateCommandQueue,
    CreateFence,
    CreateEventW,
    CreateDescriptorHeap,
    CreateCommandAllocator,
    CreateCommandList,
    CloseCommandList,
    ResetCommandList,
    ResetAllocator,
    CreateCommittedResource,
    MapResource,
    SetEventOnCompletion,
    WaitForFence,
    QueueSignal,
    QueueWait,
    CreateSwapChainForComposition,
    QueryInterface,
    SetMaximumFrameLatency,
    GetBuffer,
    Present,
    WaitForFrame,
    DCompositionCreateDevice2,
    CreateTargetForHwnd,
    CreateVisual,
    SetContent,
    SetRoot,
    Commit,
    SerializeRootSignature,
    CreateRootSignature,
    CreateComputePipelineState,
    CreateGraphicsPipelineState,
    RegisterClassExW,
    CreateWindowExW,
    SetWindowDisplayAffinity,
    RegisterHotKey,
    CreateProcess,
    RoInitialize,
    SetEnvironmentVariable,
    RoGetActivationFactory,
    WindowsCreateStringReference,
    D3D11CreateDevice,
    CreateTexture2D,
    D3D11CreateFence,
    CreateSharedHandle,
    OpenSharedHandle,
    ContextWait,
    ContextSignal,
    CreateDirect3D11DeviceFromDXGIDevice,
    CreateForMonitor,
    CreateFreeThreaded,
    CreateCaptureSession,
    StartCapture,
    TryGetNextFrame,
    GetSurface,
    GetInterface,
    GetContentSize,
    PutIsCursorCaptureEnabled,
    PutIsBorderRequired,
    CloseFrame,
    IsCaptureSupported,
    GetMonitorInfoW,
    EnumDisplayMonitors,
    QueryPerformanceCounter,
    QueryPerformanceFrequency,
    OpenLogFile,
    WriteLog,
    NgxInit,
    NgxGetCapabilityParameters,
    NgxCreateFeature,
    NgxEvaluateFeature,
    NgxOptimalSettings,
    NgxParameterRoundTrip,
    NgxNeuralRenderingUnavailable,
    NgxModelMissing, // nvngx_dlssnr.dll is nowhere the loader looks, so there is nothing to build the feature from
    NgxDriverTooOld, // the driver's NGX loader does not know the feature; the code is the driver's own number, or 0 when that could not be read
    WindowNotFound,
    OpenModelFile,
    ModelNotSigned,
    ModelNotFromNvidia,
    OpenUpscalerFile, // the same three checks on nvngx_dlss.dll, which a refusal has to name instead
    UpscalerNotSigned,
    UpscalerNotFromNvidia,
    OpenOpticalFlowFile, // and on nvofapi64.dll, the driver's optical flow library, taken from the system folder
    OpticalFlowNotSigned,
    OpticalFlowNotFromNvidia,
    OpenRuntimeFile, // and on the NGX runtime, _nvngx.dll or nvngx.dll, when a copy sits beside the program
    RuntimeNotSigned,
    RuntimeNotFromNvidia,
    ModelRootNotTrusted, // the NVIDIA signer's chain does not reach a root on Microsoft's own list; one for each file, as above
    UpscalerRootNotTrusted,
    OpticalFlowRootNotTrusted,
    RuntimeRootNotTrusted,
    ImageLoadPolicy,
    TextureDescriptionMismatch,
    PlanFrame,
    LoadOpticalFlow,
    OpticalFlowCreate,
    OpticalFlowInit,
    OpticalFlowRegister,
    OpticalFlowExecute,
    ResourceMissing,
    ArgumentCount,
    DpiAwareness,
    SetProcessDpiAwareness,
    GetMonitorRect,
    AdapterNotFound,
    NotNvidia,
    DescriptorBudget,
    StatsOutOfRange,
    GetModuleFileNameW,
    CommandLineToArgvW,
    ResolveGeometry,
    PlanSession,
    NgxParameterList,
    NgxGetFeatureRequirements,
    NgxShutdown,
    OpticalFlowUnavailable,
    GetCurrentBackBufferIndex,
    ExecutableDirectory,
    CreateCaptureFolder,
    CapturePathTooLong,
    CaptureNameTaken,
    SnapshotFormat, // the code is the DXGI format the picture is in
    WicCreateFactory,
    WicCreateBitmap,
    WicConvertPixels,
    WicOpenFile,
    WicCreateEncoder,
    WicWriteFrame,
    MfStartup,
    MfCreateSinkWriter,
    MfConfigureStream,
    MfBeginWriting,
    MfCreateSample,
    MfWriteSample,
    MfFinalize,
    PngWriteTimeout,
};

struct Error
{
    ApiCall call;
    std::uint32_t code;
    [[nodiscard]] friend constexpr bool operator==(const Error&, const Error&) noexcept = default;
};

template <class T>
using Com = Microsoft::WRL::ComPtr<T>;

using ErrorText = infra::BoundedString<char, 400>;

[[nodiscard]] std::string_view Describe(ApiCall call) noexcept;
[[nodiscard]] ErrorText Describe(const Error& error) noexcept;

[[nodiscard]] constexpr bool IsFailure(HRESULT hr) noexcept
{
    return hr < 0;
}

[[nodiscard]] inline infra::Status<Error> Check(HRESULT hr, ApiCall call) noexcept
{
    if (IsFailure(hr))
        return infra::Fail(Error{ call, static_cast<std::uint32_t>(hr) });
    return {};
}

// The code a refused signature carries when the file has more signatures than are checked, in place of
// the verdict WinVerifyTrust gives, which is never this value.
constexpr std::uint32_t kSignaturesUnchecked = 0xFFFFFFFFu;

[[nodiscard]] inline Error LastError(ApiCall call) noexcept
{
    return Error{ call, static_cast<std::uint32_t>(::GetLastError()) };
}

[[nodiscard]] inline infra::Status<Error> CheckBool(BOOL ok, ApiCall call) noexcept
{
    if (ok == FALSE)
        return infra::Fail(LastError(call));
    return {};
}

template <class T, class U>
[[nodiscard]] infra::Result<Com<T>, Error> As(const Com<U>& source, ApiCall call) noexcept
{
    Com<T> out;
    const HRESULT hr = source.As(&out);
    return Check(hr, call).transform([&out] { return out; });
}

struct HandleCloser
{
    void operator()(void* handle) const noexcept { ENSURE(::CloseHandle(handle) != FALSE); }
};
using UniqueHandle = std::unique_ptr<void, HandleCloser>;

struct WindowDestroyer
{
    void operator()(HWND window) const noexcept { ENSURE(::DestroyWindow(window) != FALSE); }
};
using UniqueWindow = std::unique_ptr<std::remove_pointer_t<HWND>, WindowDestroyer>;

} // namespace real
