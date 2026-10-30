#pragma once
#include "effects/real/com.h"
#include "effects/real/streamline.h"
#include "infrastructure/bounded_vector.h"
#include "interior/driver.h"
#include "interior/units.h"

#include <d3d12.h>
#include <dxgi1_6.h>

#include <optional>

namespace real {

struct DeviceSettings
{
    bool debugLayer;
    std::optional<interior::RequestedAdapter> adapter;
};

struct GpuDevice
{
    Com<IDXGIFactory4> factory;
    Com<IDXGIAdapter1> adapter;
    Com<ID3D12Device> device;
    // Streamline's proxy of the device, when it is on the device: its proxy queue keeps a bare pointer to it,
    // so it is held for as long as the queue is, and let go after it.
    Com<ID3D12Device> proxyDevice;
    Com<ID3D12CommandQueue> queue;
    Com<ID3D12Fence> fence;
    UniqueHandle fenceEvent;
    Com<ID3D12DescriptorHeap> rtvHeap;
    Com<ID3D12DescriptorHeap> srvHeap;
    std::uint32_t rtvIncrement;
    std::uint32_t srvIncrement;
    bool nvidia;
    interior::AdapterName name;
    std::optional<interior::DriverVersion> driverVersion;
    // NVIDIA Streamline, when DLSS Frame Generation makes the pictures between; the queue above is then its proxy.
    // Last, so it is shut down before the device and queue above are let go, and after everything made from them.
    std::optional<Streamline> streamline;
};

constexpr std::uint32_t kMaxAdapterEntries = 16;

// One adapter as DXGI names it, for a panel that would rather offer names than an index to guess at.
struct AdapterEntry
{
    std::uint32_t index;
    interior::AdapterName name;
    bool nvidia;
};

using AdapterList = infra::BoundedVector<AdapterEntry, kMaxAdapterEntries>;

constexpr std::uint32_t kRtvSlots = 8;
constexpr std::uint32_t kSrvSlots = interior::kDescriptorsPerFrame * interior::kFrameSlotCount;
constexpr std::uint64_t kFenceTimeoutMicroseconds = 4000000;

[[nodiscard]] infra::Result<GpuDevice, Error> CreateGpuDevice(const DeviceSettings& settings) noexcept;
// NVIDIA Streamline started on a device, and the queue made through it that the device is to use from then on.
struct FrameGeneration
{
    ProxyQueue proxy;
    Streamline streamline;
};

// Started after the session's own NGX runtime is: Streamline starts NGX for itself too, and started first it
// leaves the session's NGX unable to build DLSS 5 (DLSSNR.Available = 0, observed with driver 621.01). On a
// failure the device is as it was, and can be used without it.
[[nodiscard]] infra::Result<FrameGeneration, Error> StartFrameGeneration(const GpuDevice& device, const interior::DirectoryPath& executableDirectory,
                                                                         const interior::ProjectIdText& projectId) noexcept;
[[nodiscard]] GpuDevice WithFrameGeneration(GpuDevice device, FrameGeneration generation) noexcept;
// The adapters the session could run on, in the order DXGI reports them, skipping the ones it could not use.
[[nodiscard]] AdapterList UsableAdapters(IDXGIFactory4* factory) noexcept;
[[nodiscard]] infra::Result<Com<ID3D12Fence>, Error> CreateFence(ID3D12Device* device, D3D12_FENCE_FLAGS flags) noexcept;
[[nodiscard]] infra::Result<interior::FenceValue, Error> SignalFence(const GpuDevice& gpu, interior::FenceValue previous) noexcept;
[[nodiscard]] infra::Status<Error> WaitForFence(const GpuDevice& gpu, interior::FenceValue value, interior::Microseconds timeout) noexcept;
[[nodiscard]] infra::Result<interior::FenceValue, Error> WaitIdle(const GpuDevice& gpu, interior::FenceValue previous) noexcept;
[[nodiscard]] D3D12_CPU_DESCRIPTOR_HANDLE RtvHandle(const GpuDevice& gpu, std::uint32_t index) noexcept;
[[nodiscard]] D3D12_CPU_DESCRIPTOR_HANDLE SrvCpuHandle(const GpuDevice& gpu, std::uint32_t index) noexcept;
[[nodiscard]] D3D12_GPU_DESCRIPTOR_HANDLE SrvGpuHandle(const GpuDevice& gpu, std::uint32_t index) noexcept;
[[nodiscard]] infra::Result<Com<ID3D12CommandAllocator>, Error> CreateAllocator(const GpuDevice& gpu) noexcept;
[[nodiscard]] infra::Result<Com<ID3D12GraphicsCommandList>, Error> CreateClosedCommandList(const GpuDevice& gpu, ID3D12CommandAllocator* allocator) noexcept;
[[nodiscard]] infra::Status<Error> ExecuteList(const GpuDevice& gpu, ID3D12GraphicsCommandList* list) noexcept;
[[nodiscard]] infra::Status<Error> OpenCommandList(const GpuDevice& gpu, ID3D12GraphicsCommandList* list, ID3D12CommandAllocator* allocator) noexcept;
[[nodiscard]] infra::Result<interior::FenceValue, Error> FlushCommandList(const GpuDevice& gpu, ID3D12GraphicsCommandList* list, interior::FenceValue previous) noexcept;

} // namespace real
