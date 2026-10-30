#pragma once
#include "effects/real/device.h"
#include "interior/units.h"

#include <dcomp.h>

#include <array>
#include <variant>

namespace real {

constexpr DXGI_FORMAT kSwapChainFormat = DXGI_FORMAT_R8G8B8A8_UNORM;
constexpr std::uint64_t kFrameWaitMicroseconds = 2000000;
constexpr std::uint32_t kDepthRtvSlot = interior::kBackBufferCount;
constexpr std::uint32_t kHistoryRtvSlot = kDepthRtvSlot + 1;
static_assert(kHistoryRtvSlot < kRtvSlots);

using BackBuffers = std::array<Com<ID3D12Resource>, interior::kBackBufferCount>;

// The picture composited over the window, and the waitable that says when the next one is wanted.
struct Composited
{
    Com<IDCompositionDevice> device;
    Com<IDCompositionTarget> target;
    Com<IDCompositionVisual> visual;
    UniqueHandle waitable;
};

// The window's own swap chain, made through NVIDIA Streamline so DLSS Frame Generation presents into it. It owns
// the swap chain's waitable then, so frames are paced by the desktop compositor's own frames instead.
struct Generated
{
};

struct Presenter
{
    Com<IDXGISwapChain3> swapChain;
    std::variant<Composited, Generated> surface;
    BackBuffers backBuffers;
    interior::Extent extent;
};

// The picture is composited over the window rather than being the window's own content, the way that is known to
// work on every Windows the program runs on with a click-through WS_EX_LAYERED window. With NVIDIA Streamline on the
// device it is the window's own flip-model swap chain instead, which is the kind Streamline can put DLSS Frame
// Generation behind; on Windows 11 build 26300 a layered, click-through window shows one and lets clicks through.
[[nodiscard]] infra::Result<Presenter, Error> CreatePresenter(const GpuDevice& gpu, HWND window, const interior::Extent& extent) noexcept;
[[nodiscard]] infra::Status<Error> WaitForNextFrame(const Presenter& presenter) noexcept;
[[nodiscard]] infra::Result<interior::BackBufferIndex, Error> CurrentBackBuffer(const Presenter& presenter) noexcept;
[[nodiscard]] infra::Status<Error> PresentFrame(const Presenter& presenter, bool vsync) noexcept;

} // namespace real
