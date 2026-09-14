#include "effects/real/com.h"

#include "infrastructure/text.h"
#include "interior/driver.h"

namespace real {

std::string_view Describe(ApiCall call) noexcept
{
    switch (call)
    {
    case ApiCall::CreateDXGIFactory2: return "CreateDXGIFactory2";
    case ApiCall::EnumAdapters1: return "IDXGIFactory::EnumAdapters1";
    case ApiCall::D3D12CreateDevice: return "D3D12CreateDevice";
    case ApiCall::CreateCommandQueue: return "ID3D12Device::CreateCommandQueue";
    case ApiCall::CreateFence: return "ID3D12Device::CreateFence";
    case ApiCall::CreateEventW: return "CreateEventW";
    case ApiCall::CreateDescriptorHeap: return "ID3D12Device::CreateDescriptorHeap";
    case ApiCall::CreateCommandAllocator: return "ID3D12Device::CreateCommandAllocator";
    case ApiCall::CreateCommandList: return "ID3D12Device::CreateCommandList";
    case ApiCall::CloseCommandList: return "ID3D12GraphicsCommandList::Close";
    case ApiCall::ResetCommandList: return "ID3D12GraphicsCommandList::Reset";
    case ApiCall::ResetAllocator: return "ID3D12CommandAllocator::Reset";
    case ApiCall::CreateCommittedResource: return "ID3D12Device::CreateCommittedResource";
    case ApiCall::MapResource: return "ID3D12Resource::Map";
    case ApiCall::SetEventOnCompletion: return "ID3D12Fence::SetEventOnCompletion";
    case ApiCall::WaitForFence: return "fence wait timed out (GPU hang?)";
    case ApiCall::QueueSignal: return "ID3D12CommandQueue::Signal";
    case ApiCall::QueueWait: return "ID3D12CommandQueue::Wait";
    case ApiCall::CreateSwapChainForComposition: return "IDXGIFactory2::CreateSwapChainForComposition";
    case ApiCall::QueryInterface: return "QueryInterface";
    case ApiCall::SetMaximumFrameLatency: return "IDXGISwapChain2::SetMaximumFrameLatency";
    case ApiCall::GetBuffer: return "IDXGISwapChain::GetBuffer";
    case ApiCall::Present: return "IDXGISwapChain::Present";
    case ApiCall::WaitForFrame: return "frame latency wait timed out";
    case ApiCall::DCompositionCreateDevice2: return "DCompositionCreateDevice2";
    case ApiCall::CreateTargetForHwnd: return "IDCompositionDevice::CreateTargetForHwnd";
    case ApiCall::CreateVisual: return "IDCompositionDevice::CreateVisual";
    case ApiCall::SetContent: return "IDCompositionVisual::SetContent";
    case ApiCall::SetRoot: return "IDCompositionTarget::SetRoot";
    case ApiCall::Commit: return "IDCompositionDevice::Commit";
    case ApiCall::SerializeRootSignature: return "D3D12SerializeRootSignature";
    case ApiCall::CreateRootSignature: return "ID3D12Device::CreateRootSignature";
    case ApiCall::CreateComputePipelineState: return "ID3D12Device::CreateComputePipelineState";
    case ApiCall::CreateGraphicsPipelineState: return "ID3D12Device::CreateGraphicsPipelineState";
    case ApiCall::RegisterClassExW: return "RegisterClassExW";
    case ApiCall::CreateWindowExW: return "CreateWindowExW";
    case ApiCall::SetWindowDisplayAffinity: return "SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)";
    case ApiCall::RegisterHotKey: return "RegisterHotKey";
    case ApiCall::CreateProcess: return "CreateProcessW";
    case ApiCall::RoInitialize: return "RoInitialize";
    case ApiCall::SetEnvironmentVariable: return "SetEnvironmentVariableW";
    case ApiCall::RoGetActivationFactory: return "RoGetActivationFactory";
    case ApiCall::WindowsCreateStringReference: return "WindowsCreateStringReference";
    case ApiCall::D3D11CreateDevice: return "D3D11CreateDevice";
    case ApiCall::CreateTexture2D: return "ID3D11Device::CreateTexture2D";
    case ApiCall::D3D11CreateFence: return "ID3D11Device5::CreateFence";
    case ApiCall::CreateSharedHandle: return "CreateSharedHandle";
    case ApiCall::OpenSharedHandle: return "ID3D12Device::OpenSharedHandle";
    case ApiCall::ContextWait: return "ID3D11DeviceContext4::Wait";
    case ApiCall::ContextSignal: return "ID3D11DeviceContext4::Signal";
    case ApiCall::CreateDirect3D11DeviceFromDXGIDevice: return "CreateDirect3D11DeviceFromDXGIDevice";
    case ApiCall::CreateForMonitor: return "IGraphicsCaptureItemInterop::CreateForMonitor";
    case ApiCall::CreateFreeThreaded: return "Direct3D11CaptureFramePool::CreateFreeThreaded";
    case ApiCall::CreateCaptureSession: return "Direct3D11CaptureFramePool::CreateCaptureSession";
    case ApiCall::StartCapture: return "GraphicsCaptureSession::StartCapture";
    case ApiCall::TryGetNextFrame: return "Direct3D11CaptureFramePool::TryGetNextFrame";
    case ApiCall::GetSurface: return "Direct3D11CaptureFrame::Surface";
    case ApiCall::GetInterface: return "IDirect3DDxgiInterfaceAccess::GetInterface";
    case ApiCall::GetContentSize: return "Direct3D11CaptureFrame::ContentSize";
    case ApiCall::PutIsCursorCaptureEnabled: return "GraphicsCaptureSession::IsCursorCaptureEnabled";
    case ApiCall::PutIsBorderRequired: return "GraphicsCaptureSession::IsBorderRequired";
    case ApiCall::CloseFrame: return "Direct3D11CaptureFrame::Close";
    case ApiCall::IsCaptureSupported: return "GraphicsCaptureSession::IsSupported";
    case ApiCall::GetMonitorInfoW: return "GetMonitorInfoW";
    case ApiCall::EnumDisplayMonitors: return "EnumDisplayMonitors";
    case ApiCall::QueryPerformanceCounter: return "QueryPerformanceCounter";
    case ApiCall::QueryPerformanceFrequency: return "QueryPerformanceFrequency";
    case ApiCall::OpenLogFile: return "opening the log file";
    case ApiCall::WriteLog: return "writing the log";
    case ApiCall::NgxInit: return "NVSDK_NGX_D3D12_Init";
    case ApiCall::NgxGetCapabilityParameters: return "NVSDK_NGX_D3D12_GetCapabilityParameters";
    case ApiCall::NgxCreateFeature: return "NVSDK_NGX_D3D12_CreateFeature";
    case ApiCall::NgxEvaluateFeature: return "NVSDK_NGX_D3D12_EvaluateFeature";
    case ApiCall::NgxOptimalSettings: return "NGX_DLSS_GET_OPTIMAL_SETTINGS";
    case ApiCall::NgxParameterRoundTrip: return "an NGX parameter did not read back the value written";
    case ApiCall::WindowNotFound: return "no visible window has --window in its title";
    case ApiCall::OpenModelFile: return "opening nvngx_dlssnr.dll to check its signature";
    case ApiCall::ModelNotSigned: return "ERROR: nvngx_dlssnr.dll has no signature Windows trusts. It is not the correct file.";
    case ApiCall::ModelNotFromNvidia: return "ERROR: nvngx_dlssnr.dll is signed, but none of its signers is NVIDIA. It is not the correct file.";
    case ApiCall::OpenUpscalerFile: return "opening nvngx_dlss.dll to check its signature";
    case ApiCall::UpscalerNotSigned: return "ERROR: nvngx_dlss.dll has no signature Windows trusts. It is not the correct file.";
    case ApiCall::UpscalerNotFromNvidia: return "ERROR: nvngx_dlss.dll is signed, but none of its signers is NVIDIA. It is not the correct file.";
    case ApiCall::OpenOpticalFlowFile: return "opening nvofapi64.dll in the system folder to check its signature";
    case ApiCall::OpticalFlowNotSigned: return "ERROR: nvofapi64.dll has no signature Windows trusts. It is not the correct file.";
    case ApiCall::OpticalFlowNotFromNvidia: return "ERROR: nvofapi64.dll is signed, but none of its signers is NVIDIA. It is not the correct file.";
    case ApiCall::OpenRuntimeFile: return "opening the NGX runtime beside the program to check its signature";
    case ApiCall::RuntimeNotSigned: return "ERROR: the NGX runtime beside the program (_nvngx.dll or nvngx.dll) has no signature Windows trusts. It is not the correct file.";
    case ApiCall::RuntimeNotFromNvidia: return "ERROR: the NGX runtime beside the program (_nvngx.dll or nvngx.dll) is signed, but none of its signers is NVIDIA. It is not the correct file.";
    case ApiCall::ModelRootNotTrusted:
        return "ERROR: nvngx_dlssnr.dll is signed as NVIDIA Corporation, but the signature's chain does not reach a root on Microsoft's own trusted root list. It is not the correct file.";
    case ApiCall::UpscalerRootNotTrusted:
        return "ERROR: nvngx_dlss.dll is signed as NVIDIA Corporation, but the signature's chain does not reach a root on Microsoft's own trusted root list. It is not the correct file.";
    case ApiCall::OpticalFlowRootNotTrusted:
        return "ERROR: nvofapi64.dll is signed as NVIDIA Corporation, but the signature's chain does not reach a root on Microsoft's own trusted root list. It is not the correct file.";
    case ApiCall::RuntimeRootNotTrusted:
        return "ERROR: the NGX runtime beside the program (_nvngx.dll or nvngx.dll) is signed as NVIDIA Corporation, but the signature's chain does not reach a root on Microsoft's own trusted root "
               "list. It is not the correct file.";
    case ApiCall::ImageLoadPolicy: return "asking Windows to take a library from its own folder before the program's";
    case ApiCall::NgxNeuralRenderingUnavailable:
        return "DLSS 5 Neural Rendering is unavailable: the NGX loader found nvngx_dlssnr.dll but would not build the feature from it (DLSSNR.Available = 0). Run with --ngx-log 2 for "
               "the loader's own account of why.";
    case ApiCall::NgxModelMissing:
        return "Error nvngx_dlssnr.dll is required but was not found.\n\n"
               "Due to copyright, I cannot bundle it with this program, you need to obtain it yourself. Just search the web for nvngx_dlssnr.dll and put it next to the exe.\n\nThe app will verify "
               "the "
               "signature of the dll before using it to ensure it's correct.";
    case ApiCall::NgxDriverTooOld: return "DLSS 5 Neural Rendering is not offered by this NVIDIA driver: its NGX loader has no DLSSNR.Available";
    case ApiCall::TextureDescriptionMismatch: return "a created texture does not match its description";
    case ApiCall::PlanFrame: return "frame planning";
    case ApiCall::LoadOpticalFlow: return "loading nvofapi64.dll";
    case ApiCall::OpticalFlowCreate: return "NvOFAPICreateInstanceD3D12 / nvCreateOpticalFlowD3D12";
    case ApiCall::OpticalFlowInit: return "nvOFInit";
    case ApiCall::OpticalFlowRegister: return "nvOFRegisterResourceD3D12";
    case ApiCall::OpticalFlowExecute: return "nvOFExecuteD3D12";
    case ApiCall::ResourceMissing: return "a planned resource does not exist";
    case ApiCall::ArgumentCount: return "too many command-line arguments";
    case ApiCall::DpiAwareness: return "SetProcessDpiAwarenessContext";
    case ApiCall::SetProcessDpiAwareness: return "SetProcessDpiAwarenessContext";
    case ApiCall::GetMonitorRect: return "monitor rectangle";
    case ApiCall::AdapterNotFound: return "no Direct3D 12 adapter matched the request";
    case ApiCall::NotNvidia: return "the selected adapter is not an NVIDIA GPU";
    case ApiCall::DescriptorBudget: return "a frame needs more descriptors than its ring holds";
    case ApiCall::StatsOutOfRange: return "the unmatched-pixel count exceeds the pixel count";
    case ApiCall::GetModuleFileNameW: return "GetModuleFileNameW";
    case ApiCall::CommandLineToArgvW: return "CommandLineToArgvW";
    case ApiCall::ResolveGeometry: return "monitor selection";
    case ApiCall::PlanSession: return "session planning";
    case ApiCall::NgxParameterList: return "the DLSS 5 parameter list exceeded its capacity";
    case ApiCall::NgxGetFeatureRequirements: return "NVSDK_NGX_D3D12_GetFeatureRequirements";
    case ApiCall::NgxShutdown: return "NVSDK_NGX_D3D12_Shutdown1";
    case ApiCall::OpticalFlowUnavailable: return "this build has no NVIDIA Optical Flow backend (configure with -DDSCREEN_ENABLE_NVOF=ON)";
    case ApiCall::GetCurrentBackBufferIndex: return "IDXGISwapChain3::GetCurrentBackBufferIndex";
    case ApiCall::ExecutableDirectory: return "the executable path has no directory";
    case ApiCall::CreateCaptureFolder: return "creating the capture folder";
    case ApiCall::CapturePathTooLong: return "the capture folder and the capture's name together are longer than a path may be";
    case ApiCall::CaptureNameTaken: return "a thousand captures already have this name, and the count stops there";
    case ApiCall::SnapshotFormat: return "the picture is in a format the capture cannot write";
    case ApiCall::WicCreateFactory: return "CoCreateInstance(WICImagingFactory)";
    case ApiCall::WicCreateBitmap: return "IWICImagingFactory::CreateBitmapFromMemory";
    case ApiCall::WicConvertPixels: return "IWICFormatConverter::Initialize";
    case ApiCall::WicOpenFile: return "IWICStream::InitializeFromFilename on the capture file";
    case ApiCall::WicCreateEncoder: return "IWICImagingFactory::CreateEncoder(PNG)";
    case ApiCall::WicWriteFrame: return "IWICBitmapFrameEncode::WriteSource";
    case ApiCall::MfStartup: return "MFStartup";
    case ApiCall::MfCreateSinkWriter: return "MFCreateSinkWriterFromURL on the recording file";
    case ApiCall::MfConfigureStream: return "describing the recording's video stream to the sink writer";
    case ApiCall::MfBeginWriting: return "IMFSinkWriter::BeginWriting";
    case ApiCall::MfCreateSample: return "making a video sample for the recording";
    case ApiCall::MfWriteSample: return "IMFSinkWriter::WriteSample";
    case ApiCall::MfFinalize: return "IMFSinkWriter::Finalize";
    case ApiCall::PngWriteTimeout: return "the picture writer took longer than a minute over one picture, so it is taken to be stuck";
    }
    return "unknown call";
}

ErrorText Describe(const Error& error) noexcept
{
    // The one error that is about the driver carries the driver's number as its code, and says which is needed.
    static constexpr auto DriverText = [] [[nodiscard]] (std::uint32_t installed) noexcept -> ErrorText {
        static constexpr auto InstalledText = [] [[nodiscard]] (std::uint32_t installed) noexcept -> infra::BoundedString<char, 64> {
            if (installed == 0)
                return infra::Formatted<64>("the installed version could not be read");
            return infra::Formatted<64>("this is {}.{:02}", interior::NvidiaDriverMajor(installed), interior::NvidiaDriverMinor(installed));
        };
        return infra::Formatted<ErrorText::Capacity>("{}. Driver {}.{:02} or newer is required, and {}.", Describe(ApiCall::NgxDriverTooOld),
                                                     interior::NvidiaDriverMajor(interior::kFirstNeuralRenderingDriver), interior::NvidiaDriverMinor(interior::kFirstNeuralRenderingDriver),
                                                     InstalledText(installed).Get());
    };
    // A refused signature carries WinVerifyTrust's verdict as its code, which says whether there was a
    // signature at all, one that no longer matches the file, or one Windows does not trust; or the one code
    // that is no verdict, for a file with more signatures than are checked.
    static constexpr auto SignatureText = [] [[nodiscard]] (std::string_view file, std::uint32_t verdict) noexcept -> ErrorText {
        if (verdict == kSignaturesUnchecked)
            return infra::Formatted<ErrorText::Capacity>("ERROR: {} carries more signatures than this program checks, so they cannot all be trusted. It is not the correct file.", file);
        if (verdict == static_cast<std::uint32_t>(TRUST_E_NOSIGNATURE))
            return infra::Formatted<ErrorText::Capacity>("ERROR: {} is not signed. It is not the correct file.", file);
        if (verdict == static_cast<std::uint32_t>(TRUST_E_BAD_DIGEST))
            return infra::Formatted<ErrorText::Capacity>("ERROR: {} is signed, but the signature does not match the file: it has been altered since it was signed. It is not the correct file.", file);
        return infra::Formatted<ErrorText::Capacity>("ERROR: {} has a signature Windows does not trust (0x{:08X}). It is not the correct file.", file, verdict);
    };
    if (error.call == ApiCall::NgxDriverTooOld)
        return DriverText(error.code);
    if (error.call == ApiCall::ModelNotSigned)
        return SignatureText("nvngx_dlssnr.dll", error.code);
    if (error.call == ApiCall::UpscalerNotSigned)
        return SignatureText("nvngx_dlss.dll", error.code);
    if (error.call == ApiCall::OpticalFlowNotSigned)
        return SignatureText("nvofapi64.dll", error.code);
    if (error.call == ApiCall::RuntimeNotSigned)
        return SignatureText("the NGX runtime beside the program (_nvngx.dll or nvngx.dll)", error.code);
    // A signer named NVIDIA Corporation whose chain, built again from Microsoft's own root list alone, was
    // not clean: the code is the chain's trust status, or the policy's error, whichever said so.
    static constexpr auto RootText = [] [[nodiscard]] (std::string_view file, std::uint32_t code) noexcept -> ErrorText {
        return infra::Formatted<ErrorText::Capacity>(
            "ERROR: {} is signed as NVIDIA Corporation, but the signature's chain does not reach a root on Microsoft's own trusted root list (0x{:08X}). It is not the correct file.", file, code);
    };
    if (error.call == ApiCall::ModelRootNotTrusted)
        return RootText("nvngx_dlssnr.dll", error.code);
    if (error.call == ApiCall::UpscalerRootNotTrusted)
        return RootText("nvngx_dlss.dll", error.code);
    if (error.call == ApiCall::OpticalFlowRootNotTrusted)
        return RootText("nvofapi64.dll", error.code);
    if (error.call == ApiCall::RuntimeRootNotTrusted)
        return RootText("the NGX runtime beside the program (_nvngx.dll or nvngx.dll)", error.code);
    // No call fails with a code of zero, so a zero is an error that has said all it has to say.
    if (error.code == 0)
        return infra::Formatted<ErrorText::Capacity>("{}", Describe(error.call));
    return infra::Formatted<ErrorText::Capacity>("{} failed with 0x{:08X}", Describe(error.call), error.code);
}

} // namespace real
