#pragma once
#include "effects/real/trust.h"
#include "interior/frame.h"

#include <d3d12.h>
#include <dxgi1_6.h>

#include <memory>

namespace real {

// The calls this program makes into NVIDIA Streamline, found in sl.interposer.dll once it has been checked and
// loaded. They are defined beside the code that makes them, so the Streamline headers are read in one place.
struct StreamlineCalls;

// Ends Streamline for the session: slShutdown, which must come after the swap chain it made is gone and
// before the device it was given is.
struct StreamlineEnder
{
    void operator()(const StreamlineCalls* calls) const noexcept;
};

// NVIDIA Streamline started for one session, with DLSS Frame Generation found supported on its adapter. The
// library stays loaded for the rest of the process once loaded: the proxies it hands out are its code, and
// some of them are let go after slShutdown, with the device.
struct Streamline
{
    TrustedFile file; // sl.interposer.dll, checked and held open for as long as the session runs
    std::unique_ptr<const StreamlineCalls, StreamlineEnder> calls;
};

// What goes with one presented picture: what DLSS Frame Generation is to do with it, the motion vectors and
// depth plane it makes the pictures between from, both readable by shaders, and the size of the picture.
struct GeneratedFrame
{
    interior::Generation generation;
    ID3D12Resource* motion;
    ID3D12Resource* depth;
    interior::Extent colorExtent;
};

// What DLSS Frame Generation said about the frames it was handed, for the log.
struct GenerationReport
{
    std::uint32_t shownPerPicture; // pictures shown for the last one handed over, that one included
    std::uint32_t mostBetween;     // the most it can make between two, on this GPU
    std::uint32_t status;          // sl::DLSSGStatus: zero when nothing stops it
};

// Checks sl.interposer.dll beside the executable, loads it and starts Streamline with DLSS Frame Generation,
// Reflex and PC Latency, hands it the device, and fails unless DLSS Frame Generation is supported on the adapter.
[[nodiscard]] infra::Result<Streamline, Error> StartStreamline(const interior::DirectoryPath& executableDirectory, const interior::ProjectIdText& projectId, ID3D12Device* device,
                                                               IDXGIAdapter1* adapter) noexcept;
// The queue the swap chain is made on and the frames are submitted to, made through Streamline's proxy of the
// device, which is how it learns which queue presents. The proxy queue keeps a bare pointer to the proxy device
// (Streamline 2.14.16, d3d12CommandQueue.h, m_device), so the device has to be held for as long as the queue.
struct ProxyQueue
{
    Com<ID3D12Device> device;
    Com<ID3D12CommandQueue> queue;
};

[[nodiscard]] infra::Result<ProxyQueue, Error> CreateProxyQueue(const Streamline& streamline, ID3D12Device* device) noexcept;
// Streamline's proxy of the factory: a swap chain made through it is the one DLSS Frame Generation presents into.
[[nodiscard]] infra::Result<Com<IDXGIFactory2>, Error> ProxyFactory(const Streamline& streamline, IDXGIFactory4* factory) noexcept;
// Hands one picture to DLSS Frame Generation with what goes with it, and presents it.
[[nodiscard]] infra::Status<Error> PresentGenerated(const Streamline& streamline, IDXGISwapChain3* swapChain, const GeneratedFrame& frame, bool vsync) noexcept;
[[nodiscard]] infra::Result<GenerationReport, Error> ReportGeneration(const Streamline& streamline) noexcept;

} // namespace real
