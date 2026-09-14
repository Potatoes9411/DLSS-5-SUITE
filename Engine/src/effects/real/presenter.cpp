#include "effects/real/presenter.h"

#include "effects/real/resources.h"
#include "infrastructure/fold.h"

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

} // namespace

Result<Presenter, Error> CreatePresenter(const GpuDevice& gpu, HWND window, const interior::Extent& extent) noexcept
{
    static constexpr auto CreateSwapChain = [] [[nodiscard]] (const GpuDevice& gpu, const interior::Extent& extent) noexcept -> Result<Com<IDXGISwapChain3>, Error> {
        static constexpr auto CreateCompositionSwapChain = [] [[nodiscard]] (const GpuDevice& gpu, const interior::Extent& extent) noexcept -> Result<Com<IDXGISwapChain1>, Error> {
            static constexpr auto SwapChainDescription = [] [[nodiscard]] (const interior::Extent& extent) noexcept -> DXGI_SWAP_CHAIN_DESC1 {
                return DXGI_SWAP_CHAIN_DESC1{ extent.width.Get(),
                                              extent.height.Get(),
                                              kSwapChainFormat,
                                              FALSE,
                                              DXGI_SAMPLE_DESC{ 1, 0 },
                                              DXGI_USAGE_RENDER_TARGET_OUTPUT,
                                              interior::kBackBufferCount,
                                              DXGI_SCALING_STRETCH,
                                              DXGI_SWAP_EFFECT_FLIP_DISCARD,
                                              DXGI_ALPHA_MODE_IGNORE,
                                              DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT };
            };
            const DXGI_SWAP_CHAIN_DESC1 desc = SwapChainDescription(extent);
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

        static constexpr auto CollectBuffers = [] [[nodiscard]] (const GpuDevice& gpu, IDXGISwapChain3* chain) noexcept -> Result<BackBuffers, Error> {
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
        };
        return WaitableOf(chain.Get()).and_then([&](UniqueHandle waitable) {
            return CollectBuffers(gpu, chain.Get()).transform([&](const BackBuffers& buffers) {
                return Presenter{ chain, composition.device, composition.target, composition.visual, std::move(waitable), buffers, extent };
            });
        });
    };
    return CreateSwapChain(gpu, extent).and_then([&](const Com<IDXGISwapChain3>& chain) {
        return CreateComposition(window, chain.Get()).and_then([&](const Composition& composition) { return Rest(gpu, composition, chain, extent); });
    });
}

Status<Error> WaitForNextFrame(const Presenter& presenter) noexcept
{
    static constexpr auto IsWaitFailure = [] [[nodiscard]] (DWORD result) noexcept -> bool { return result == WAIT_FAILED; };
    const DWORD result = ::WaitForSingleObjectEx(presenter.waitable.get(), static_cast<DWORD>(kFrameWaitMicroseconds / 1000u), TRUE);
    if (IsWaitFailure(result))
        return Fail(LastError(ApiCall::WaitForFrame));
    return {};
}

Result<interior::BackBufferIndex, Error> CurrentBackBuffer(const Presenter& presenter) noexcept
{
    return interior::BackBufferIndexTag::Parse(presenter.swapChain->GetCurrentBackBufferIndex()).transform_error([](interior::UnitError) { return Error{ ApiCall::GetCurrentBackBufferIndex, 0 }; });
}

Status<Error> PresentFrame(const Presenter& presenter, bool vsync) noexcept
{
    return Check(presenter.swapChain->Present(vsync ? 1u : 0u, 0), ApiCall::Present);
}

} // namespace real
