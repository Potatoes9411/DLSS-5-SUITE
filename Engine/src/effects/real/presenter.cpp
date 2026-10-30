#include "effects/real/presenter.h"

#include "effects/real/resources.h"
#include "infrastructure/fold.h"
#include "infrastructure/overloaded.h"

#include <dwmapi.h>

#include <ranges>

namespace real {
namespace {

using infra::Fail;
using infra::Result;
using infra::Status;

struct Composition
{
    Com<IDCompositionDevice> device;
    Com<IDCompositionTarget> target;
    Com<IDCompositionVisual> visual;
};

[[nodiscard]] Result<Com<IDCompositionVisual>, Error> CreateVisual(IDCompositionDevice* device) noexcept
{
    Com<IDCompositionVisual> visual;
    const HRESULT hr = device->CreateVisual(&visual);
    return Check(hr, ApiCall::CreateVisual).transform([&visual] { return visual; });
}

[[nodiscard]] Result<BackBuffers, Error> CollectBuffers(const GpuDevice& gpu, IDXGISwapChain3* chain) noexcept
{
    static constexpr auto WithBuffer = [] [[nodiscard]] (const BackBuffers& buffers, const GpuDevice& gpu, IDXGISwapChain3* chain, std::uint32_t index) noexcept -> Result<BackBuffers, Error> {
        static constexpr auto BufferAt = [] [[nodiscard]] (IDXGISwapChain3 * chain, std::uint32_t index) noexcept -> Result<Com<ID3D12Resource>, Error> {
            static constexpr auto NamedBuffer = [] [[nodiscard]] (const Com<ID3D12Resource>& buffer) noexcept -> Result<Com<ID3D12Resource>, Error> {
                return Check(buffer->SetName(L"Back buffer"), ApiCall::GetBuffer).transform([&buffer] { return buffer; });
            };
            Com<ID3D12Resource> buffer;
            const HRESULT hr = chain->GetBuffer(index, IID_PPV_ARGS(&buffer));
            return Check(hr, ApiCall::GetBuffer).and_then([&buffer] { return NamedBuffer(buffer); });
        };
        return BufferAt(chain, index).transform([&](const Com<ID3D12Resource>& buffer) {
            CreateRtv(gpu, buffer.Get(), kSwapChainFormat, RtvHandle(gpu, index));
            return infra::WithElement(buffers, index, buffer);
        });
    };
    return infra::FoldResult(std::views::iota(std::uint32_t{ 0 }, interior::kBackBufferCount), Result<BackBuffers, Error>(BackBuffers{}),
                             [&](const BackBuffers& acc, std::uint32_t index) { return WithBuffer(acc, gpu, chain, index); });
}

[[nodiscard]] DXGI_SWAP_CHAIN_DESC1 SwapChainDescription(const interior::Extent& extent, UINT flags) noexcept
{
    return DXGI_SWAP_CHAIN_DESC1{ .Width = extent.width.Get(),
                                  .Height = extent.height.Get(),
                                  .Format = kSwapChainFormat,
                                  .Stereo = FALSE,
                                  .SampleDesc = DXGI_SAMPLE_DESC{ .Count = 1, .Quality = 0 },
                                  .BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT,
                                  .BufferCount = interior::kBackBufferCount,
                                  .Scaling = DXGI_SCALING_STRETCH,
                                  .SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD,
                                  .AlphaMode = DXGI_ALPHA_MODE_IGNORE,
                                  .Flags = flags };
}

[[nodiscard]] Result<Presenter, Error> CreateCompositedPresenter(const GpuDevice& gpu, HWND window, const interior::Extent& extent) noexcept
{
    static constexpr auto CreateSwapChain = [] [[nodiscard]] (const GpuDevice& gpu, const interior::Extent& extent) noexcept -> Result<Com<IDXGISwapChain3>, Error> {
        static constexpr auto CreateCompositionSwapChain = [] [[nodiscard]] (const GpuDevice& gpu, const interior::Extent& extent) noexcept -> Result<Com<IDXGISwapChain1>, Error> {
            const DXGI_SWAP_CHAIN_DESC1 desc = SwapChainDescription(extent, DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT);
            Com<IDXGISwapChain1> chain;
            const HRESULT hr = gpu.factory->CreateSwapChainForComposition(gpu.queue.Get(), &desc, nullptr, &chain);
            return Check(hr, ApiCall::CreateSwapChainForComposition).transform([&chain] { return chain; });
        };

        static constexpr auto WithLatencyOne = [] [[nodiscard]] (const Com<IDXGISwapChain3>& chain) noexcept -> Result<Com<IDXGISwapChain3>, Error> {
            return Check(chain->SetMaximumFrameLatency(1), ApiCall::SetMaximumFrameLatency).transform([&chain] { return chain; });
        };
        return CreateCompositionSwapChain(gpu, extent).and_then([](const Com<IDXGISwapChain1>& made) { return As<IDXGISwapChain3>(made, ApiCall::QueryInterface).and_then(WithLatencyOne); });
    };

    static constexpr auto CreateComposition = [] [[nodiscard]] (HWND window, IDXGISwapChain3 * chain) noexcept -> Result<Composition, Error> {
        static constexpr auto CreateCompositionDevice = [] [[nodiscard]] () noexcept -> Result<Com<IDCompositionDevice>, Error> {
            Com<IDCompositionDevice> device;
            const HRESULT hr = ::DCompositionCreateDevice2(nullptr, IID_PPV_ARGS(&device));
            return Check(hr, ApiCall::DCompositionCreateDevice2).transform([&device] { return device; });
        };

        static constexpr auto CreateTarget = [] [[nodiscard]] (IDCompositionDevice * device, HWND window) noexcept -> Result<Com<IDCompositionTarget>, Error> {
            Com<IDCompositionTarget> target;
            const HRESULT hr = device->CreateTargetForHwnd(window, TRUE, &target);
            return Check(hr, ApiCall::CreateTargetForHwnd).transform([&target] { return target; });
        };

        static constexpr auto Bound = [] [[nodiscard]] (const Composition& c, IDXGISwapChain3* chain) noexcept -> Result<Composition, Error> {
            static constexpr auto Bind = [] [[nodiscard]] (const Composition& c, IDXGISwapChain3* chain) noexcept -> Status<Error> {
                return Check(c.visual->SetContent(chain), ApiCall::SetContent).and_then([&c] { return Check(c.target->SetRoot(c.visual.Get()), ApiCall::SetRoot); }).and_then([&c] {
                    return Check(c.device->Commit(), ApiCall::Commit);
                });
            };
            return Bind(c, chain).transform([&c] { return c; });
        };
        return CreateCompositionDevice().and_then([window, chain](const Com<IDCompositionDevice>& device) {
            return CreateTarget(device.Get(), window).and_then([&](const Com<IDCompositionTarget>& target) {
                return CreateVisual(device.Get()).and_then([&](const Com<IDCompositionVisual>& visual) { return Bound(Composition{ device, target, visual }, chain); });
            });
        });
    };

    static constexpr auto Rest = [] [[nodiscard]] (const GpuDevice& gpu, const Composition& composition, const Com<IDXGISwapChain3>& chain,
                                                   const interior::Extent& extent) noexcept -> Result<Presenter, Error> {
        static constexpr auto WaitableOf = [] [[nodiscard]] (IDXGISwapChain3 * chain) noexcept -> Result<UniqueHandle, Error> {
            HANDLE handle = chain->GetFrameLatencyWaitableObject();
            if (handle == nullptr)
                return Fail(Error{ ApiCall::WaitForFrame, 0 });
            return UniqueHandle(handle);
        };
        return WaitableOf(chain.Get()).and_then([&](UniqueHandle waitable) {
            return CollectBuffers(gpu, chain.Get()).transform([&](const BackBuffers& buffers) {
                return Presenter{ chain, Composited{ composition.device, composition.target, composition.visual, std::move(waitable) }, buffers, extent };
            });
        });
    };
    return CreateSwapChain(gpu, extent).and_then([&](const Com<IDXGISwapChain3>& chain) {
        return CreateComposition(window, chain.Get()).and_then([&](const Composition& composition) { return Rest(gpu, composition, chain, extent); });
    });
}

// No waitable is asked for and the frame latency is left alone: Streamline's pacer owns both on this swap chain.
[[nodiscard]] Result<Presenter, Error> CreateGeneratedPresenter(const GpuDevice& gpu, const Streamline& streamline, HWND window, const interior::Extent& extent) noexcept
{
    static constexpr auto CreateWindowSwapChain = [] [[nodiscard]] (const GpuDevice& gpu, IDXGIFactory2* factory, HWND window,
                                                                    const interior::Extent& extent) noexcept -> Result<Com<IDXGISwapChain3>, Error> {
        const DXGI_SWAP_CHAIN_DESC1 desc = SwapChainDescription(extent, 0);
        Com<IDXGISwapChain1> chain;
        const HRESULT hr = factory->CreateSwapChainForHwnd(gpu.queue.Get(), window, &desc, nullptr, nullptr, &chain);
        return Check(hr, ApiCall::CreateSwapChainForHwnd).and_then([&chain] { return As<IDXGISwapChain3>(chain, ApiCall::QueryInterface); });
    };

    static constexpr auto Assembled = [] [[nodiscard]] (const GpuDevice& gpu, const Com<IDXGISwapChain3>& chain, const interior::Extent& extent) noexcept -> Result<Presenter, Error> {
        return CollectBuffers(gpu, chain.Get()).transform([&](const BackBuffers& buffers) { return Presenter{ chain, Generated{}, buffers, extent }; });
    };
    return ProxyFactory(streamline, gpu.factory.Get()).and_then([&](const Com<IDXGIFactory2>& factory) {
        return CreateWindowSwapChain(gpu, factory.Get(), window, extent).and_then([&](const Com<IDXGISwapChain3>& chain) { return Assembled(gpu, chain, extent); });
    });
}

[[nodiscard]] bool IsOffscreen(const Presenter& presenter) noexcept
{
    return std::holds_alternative<Offscreen>(presenter.surface);
}

} // namespace

Result<Presenter, Error> CreatePresenter(const GpuDevice& gpu, HWND window, const interior::Extent& extent) noexcept
{
    if (!gpu.streamline.has_value())
        return CreateCompositedPresenter(gpu, window, extent);
    return CreateGeneratedPresenter(gpu, *gpu.streamline, window, extent);
}

Result<Presenter, Error> CreateOffscreenPresenter(const GpuDevice& gpu, const interior::Extent& extent) noexcept
{
    static constexpr auto WithTarget = [] [[nodiscard]] (const BackBuffers& buffers, const GpuDevice& gpu, const interior::Extent& extent, std::uint32_t index) noexcept -> Result<BackBuffers, Error> {
        static constexpr auto TargetRequest = [] [[nodiscard]] (const interior::Extent& extent) noexcept -> TextureRequest {
            return TextureRequest{
                .extent = extent, .format = kSwapChainFormat, .flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET, .initialState = D3D12_RESOURCE_STATE_PRESENT, .name = L"Offscreen back buffer"
            };
        };
        return CreateTexture(gpu, TargetRequest(extent)).transform([&](const Texture& texture) {
            CreateRtv(gpu, texture.resource.Get(), kSwapChainFormat, RtvHandle(gpu, index));
            return infra::WithElement(buffers, index, texture.resource);
        });
    };
    return infra::FoldResult(std::views::iota(std::uint32_t{ 0 }, interior::kBackBufferCount), Result<BackBuffers, Error>(BackBuffers{}),
                             [&](const BackBuffers& acc, std::uint32_t index) { return WithTarget(acc, gpu, extent, index); })
        .transform([&extent](const BackBuffers& buffers) { return Presenter{ nullptr, Offscreen{}, buffers, extent }; });
}

Status<Error> WaitForNextFrame(const Presenter& presenter) noexcept
{
    static constexpr auto WaitForSwapChain = [] [[nodiscard]] (const Composited& composited) noexcept -> Status<Error> {
        static constexpr auto IsWaitFailure = [] [[nodiscard]] (DWORD result) noexcept -> bool { return result == WAIT_FAILED; };
        const DWORD result = ::WaitForSingleObjectEx(composited.waitable.get(), static_cast<DWORD>(kFrameWaitMicroseconds / 1000u), TRUE);
        if (IsWaitFailure(result))
            return Fail(LastError(ApiCall::WaitForFrame));
        return {};
    };

    // A frame with nothing new is not presented while DLSS Frame Generation runs, so no present comes back to wait
    // on; the compositor's next frame is what the next look at the capture waits for. GetContainingOutput, which
    // WaitForVBlank needs, answers DXGI_ERROR_UNSUPPORTED for this swap chain (observed on Windows 11 build 26300).
    static constexpr auto WaitForRefresh = [] [[nodiscard]] (const Generated&) noexcept -> Status<Error> { return Check(::DwmFlush(), ApiCall::WaitForCompositor); };
    // Nothing is shown, so nothing is waited for: the next picture is wanted as soon as the last is read back.
    static constexpr auto WaitForNothing = [] [[nodiscard]] (const Offscreen&) noexcept -> Status<Error> { return {}; };
    return std::visit(infra::Overloaded{ WaitForSwapChain, WaitForRefresh, WaitForNothing }, presenter.surface);
}

Result<interior::BackBufferIndex, Error> CurrentBackBuffer(const Presenter& presenter) noexcept
{
    if (IsOffscreen(presenter))
        return Fail(Error{ ApiCall::GetCurrentBackBufferIndex, 1 });
    return interior::BackBufferIndexTag::Parse(presenter.swapChain->GetCurrentBackBufferIndex()).transform_error([](interior::UnitError) { return Error{ ApiCall::GetCurrentBackBufferIndex, 0 }; });
}

// Offscreen, a picture is finished once it is drawn: it is read back from its texture, and there is nothing to show.
Status<Error> PresentFrame(const Presenter& presenter, bool vsync) noexcept
{
    if (IsOffscreen(presenter))
        return {};
    return Check(presenter.swapChain->Present(vsync ? 1u : 0u, 0), ApiCall::Present);
}

} // namespace real
