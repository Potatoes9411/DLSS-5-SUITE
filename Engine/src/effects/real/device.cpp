#include "effects/real/device.h"

#include "infrastructure/fold.h"

#include <d3d12sdklayers.h>

#include <ranges>
#include <string_view>

namespace real {
namespace {

using infra::Fail;
using infra::Result;
using infra::Status;

constexpr UINT kNvidiaVendorId = 0x10DE;
constexpr std::uint32_t kMaxAdapters = 16;

struct Candidate
{
    Com<IDXGIAdapter1> adapter;
    DXGI_ADAPTER_DESC1 description;
    std::uint32_t index;
};

[[nodiscard]] bool IsNvidia(const Candidate& c) noexcept
{
    return c.description.VendorId == kNvidiaVendorId;
}

[[nodiscard]] interior::AdapterName NameOf(const Candidate& c) noexcept
{
    return interior::AdapterName::Parse(std::wstring_view(c.description.Description)).value_or(interior::AdapterName{});
}

[[nodiscard]] bool SupportsD3D12(const Candidate& c) noexcept
{
    return !IsFailure(D3D12CreateDevice(c.adapter.Get(), D3D_FEATURE_LEVEL_12_0, __uuidof(ID3D12Device), nullptr));
}

[[nodiscard]] bool IsUsable(const Candidate& c) noexcept
{
    static constexpr auto IsSoftware = [] [[nodiscard]] (const Candidate& c) noexcept -> bool { return (c.description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0; };
    return !IsSoftware(c) && SupportsD3D12(c);
}

[[nodiscard]] bool MatchesRequest(const Candidate& c, const std::optional<interior::RequestedAdapter>& request) noexcept
{
    return request.has_value() ? c.index == request->Get() : IsNvidia(c);
}

[[nodiscard]] std::optional<Candidate> CandidateAt(IDXGIFactory4* factory, std::uint32_t index) noexcept
{
    Candidate c{ nullptr, {}, index };
    if (factory->EnumAdapters1(index, &c.adapter) != S_OK)
        return std::nullopt;
    ENSURE(!IsFailure(c.adapter->GetDesc1(&c.description)));
    return c;
}

struct Core
{
    Com<IDXGIFactory4> factory;
    Candidate candidate;
    Com<ID3D12Device> device;
};

struct Queues
{
    Com<ID3D12CommandQueue> queue;
    Com<ID3D12Fence> fence;
};

[[nodiscard]] D3D12_CPU_DESCRIPTOR_HANDLE CpuHandleAt(ID3D12DescriptorHeap* heap, std::uint32_t increment, std::uint32_t index) noexcept
{
    return D3D12_CPU_DESCRIPTOR_HANDLE{ heap->GetCPUDescriptorHandleForHeapStart().ptr + static_cast<SIZE_T>(index) * increment };
}

} // namespace

Result<Com<ID3D12Fence>, Error> CreateFence(ID3D12Device* device, D3D12_FENCE_FLAGS flags) noexcept
{
    Com<ID3D12Fence> fence;
    const HRESULT hr = device->CreateFence(0, flags, IID_PPV_ARGS(&fence));
    return Check(hr, ApiCall::CreateFence).transform([&fence] { return fence; });
}

Result<GpuDevice, Error> CreateGpuDevice(const DeviceSettings& settings) noexcept
{
    static constexpr auto CreateCore = [] [[nodiscard]] (const DeviceSettings& settings) noexcept -> Result<Core, Error> {
        static constexpr auto EnableDebugLayer = [] [[nodiscard]] (bool wanted) noexcept -> Status<Error> {
            if (!wanted)
                return {};
            Com<ID3D12Debug> debug;
            return Check(D3D12GetDebugInterface(IID_PPV_ARGS(&debug)), ApiCall::D3D12CreateDevice).transform([&debug] { debug->EnableDebugLayer(); });
        };

        static constexpr auto CreateFactory = [] [[nodiscard]] (bool debugLayer) noexcept -> Result<Com<IDXGIFactory4>, Error> {
            static constexpr auto FactoryFlags = [] [[nodiscard]] (bool debugLayer) noexcept -> UINT { return debugLayer ? DXGI_CREATE_FACTORY_DEBUG : 0u; };
            Com<IDXGIFactory4> factory;
            const HRESULT hr = CreateDXGIFactory2(FactoryFlags(debugLayer), IID_PPV_ARGS(&factory));
            return Check(hr, ApiCall::CreateDXGIFactory2).transform([&factory] { return factory; });
        };

        static constexpr auto SelectAdapter = [] [[nodiscard]] (IDXGIFactory4 * factory, const std::optional<interior::RequestedAdapter>& request) noexcept -> Result<Candidate, Error> {
            static constexpr auto FirstWanted = [] [[nodiscard]] (const std::optional<Candidate>& found, IDXGIFactory4* factory, std::uint32_t index,
                                                                  const std::optional<interior::RequestedAdapter>& request) noexcept -> std::optional<Candidate> {
                static constexpr auto WantedAt = [] [[nodiscard]] (IDXGIFactory4 * factory, std::uint32_t index,
                                                                   const std::optional<interior::RequestedAdapter>& request) noexcept -> std::optional<Candidate> {
                    static constexpr auto WantedOrNothing = [] [[nodiscard]] (const Candidate& c, const std::optional<interior::RequestedAdapter>& request) noexcept -> std::optional<Candidate> {
                        static constexpr auto IsWanted = [] [[nodiscard]] (const Candidate& c, const std::optional<interior::RequestedAdapter>& request) noexcept -> bool {
                            return IsUsable(c) && MatchesRequest(c, request);
                        };
                        if (!IsWanted(c, request))
                            return std::nullopt;
                        return c;
                    };
                    return CandidateAt(factory, index).and_then([&request](const Candidate& c) { return WantedOrNothing(c, request); });
                };
                return found.has_value() ? found : WantedAt(factory, index, request);
            };
            const std::optional<Candidate> found = std::ranges::fold_left(std::views::iota(std::uint32_t{ 0 }, kMaxAdapters), std::optional<Candidate>{},
                                                                          [&](const std::optional<Candidate>& acc, std::uint32_t i) { return FirstWanted(acc, factory, i, request); });
            if (!found.has_value())
                return Fail(Error{ ApiCall::AdapterNotFound, 0 });
            return *found;
        };

        static constexpr auto CreateDevice = [] [[nodiscard]] (IDXGIAdapter1 * adapter) noexcept -> Result<Com<ID3D12Device>, Error> {
            Com<ID3D12Device> device;
            const HRESULT hr = D3D12CreateDevice(adapter, D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&device));
            return Check(hr, ApiCall::D3D12CreateDevice).transform([&device] { return device; });
        };
        return EnableDebugLayer(settings.debugLayer).and_then([&] { return CreateFactory(settings.debugLayer); }).and_then([&](const Com<IDXGIFactory4>& factory) {
            return SelectAdapter(factory.Get(), settings.adapter).and_then([&](const Candidate& candidate) {
                return CreateDevice(candidate.adapter.Get()).transform([&](const Com<ID3D12Device>& device) { return Core{ factory, candidate, device }; });
            });
        });
    };

    static constexpr auto CreateQueues = [] [[nodiscard]] (ID3D12Device * device) noexcept -> Result<Queues, Error> {
        static constexpr auto CreateQueue = [] [[nodiscard]] (ID3D12Device * device) noexcept -> Result<Com<ID3D12CommandQueue>, Error> {
            const D3D12_COMMAND_QUEUE_DESC desc{ D3D12_COMMAND_LIST_TYPE_DIRECT, 0, D3D12_COMMAND_QUEUE_FLAG_NONE, 0 };
            Com<ID3D12CommandQueue> queue;
            const HRESULT hr = device->CreateCommandQueue(&desc, IID_PPV_ARGS(&queue));
            return Check(hr, ApiCall::CreateCommandQueue).transform([&queue] { return queue; });
        };
        return CreateQueue(device).and_then([device](const Com<ID3D12CommandQueue>& queue) {
            return CreateFence(device, D3D12_FENCE_FLAG_NONE).transform([&queue](const Com<ID3D12Fence>& fence) { return Queues{ queue, fence }; });
        });
    };

    static constexpr auto AssembleDevice = [] [[nodiscard]] (Core core, Queues queues) noexcept -> Result<GpuDevice, Error> {
        static constexpr auto CreateFenceEvent = [] [[nodiscard]] () noexcept -> Result<UniqueHandle, Error> {
            HANDLE handle = ::CreateEventW(nullptr, FALSE, FALSE, nullptr);
            if (handle == nullptr)
                return Fail(LastError(ApiCall::CreateEventW));
            return UniqueHandle(handle);
        };

        static constexpr auto CreateHeap = [] [[nodiscard]] (ID3D12Device * device, D3D12_DESCRIPTOR_HEAP_TYPE type, std::uint32_t count,
                                                             D3D12_DESCRIPTOR_HEAP_FLAGS flags) noexcept -> Result<Com<ID3D12DescriptorHeap>, Error> {
            const D3D12_DESCRIPTOR_HEAP_DESC desc{ type, count, flags, 0 };
            Com<ID3D12DescriptorHeap> heap;
            const HRESULT hr = device->CreateDescriptorHeap(&desc, IID_PPV_ARGS(&heap));
            return Check(hr, ApiCall::CreateDescriptorHeap).transform([&heap] { return heap; });
        };

        static constexpr auto Assemble = [] [[nodiscard]] (Core & core, Queues & queues, UniqueHandle & event, Com<ID3D12DescriptorHeap> & rtv, Com<ID3D12DescriptorHeap> & srv) noexcept -> GpuDevice {
            // The user-mode driver version, which DXGI reports through the IDXGIDevice interface query.
            static constexpr auto ReadDriverVersion = [] [[nodiscard]] (IDXGIAdapter1 * adapter) noexcept -> std::optional<interior::DriverVersion> {
                LARGE_INTEGER version{};
                if (IsFailure(adapter->CheckInterfaceSupport(__uuidof(IDXGIDevice), &version)))
                    return std::nullopt;
                return interior::DriverVersionOf(static_cast<std::uint64_t>(version.QuadPart));
            };
            ID3D12Device* device = core.device.Get();
            return GpuDevice{ core.factory,
                              core.candidate.adapter,
                              core.device,
                              queues.queue,
                              queues.fence,
                              std::move(event),
                              rtv,
                              srv,
                              device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV),
                              device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV),
                              IsNvidia(core.candidate),
                              NameOf(core.candidate),
                              ReadDriverVersion(core.candidate.adapter.Get()) };
        };
        return CreateFenceEvent().and_then([&](UniqueHandle event) {
            return CreateHeap(core.device.Get(), D3D12_DESCRIPTOR_HEAP_TYPE_RTV, kRtvSlots, D3D12_DESCRIPTOR_HEAP_FLAG_NONE).and_then([&](Com<ID3D12DescriptorHeap> rtv) {
                return CreateHeap(core.device.Get(), D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV, kSrvSlots, D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE).transform([&](Com<ID3D12DescriptorHeap> srv) {
                    return Assemble(core, queues, event, rtv, srv);
                });
            });
        });
    };
    return CreateCore(settings).and_then([](Core core) { return CreateQueues(core.device.Get()).and_then([&core](Queues queues) { return AssembleDevice(std::move(core), std::move(queues)); }); });
}

Result<interior::FenceValue, Error> SignalFence(const GpuDevice& gpu, interior::FenceValue previous) noexcept
{
    const interior::FenceValue next = interior::FenceValueTag::Parse(previous.Get() + 1);
    return Check(gpu.queue->Signal(gpu.fence.Get(), next.Get()), ApiCall::QueueSignal).transform([next] { return next; });
}

Status<Error> WaitForFence(const GpuDevice& gpu, interior::FenceValue value, interior::Microseconds timeout) noexcept
{
    static constexpr auto IsComplete = [] [[nodiscard]] (ID3D12Fence * fence, interior::FenceValue value) noexcept -> bool { return fence->GetCompletedValue() >= value.Get(); };

    static constexpr auto ArmAndWait = [] [[nodiscard]] (const GpuDevice& gpu, interior::FenceValue value, interior::Microseconds timeout) noexcept -> Status<Error> {
        static constexpr auto WaitOnEvent = [] [[nodiscard]] (const GpuDevice& gpu, interior::Microseconds timeout) noexcept -> Status<Error> {
            const DWORD milliseconds = static_cast<DWORD>(timeout.Get() / 1000u);
            if (::WaitForSingleObject(gpu.fenceEvent.get(), milliseconds) != WAIT_OBJECT_0)
                return Fail(Error{ ApiCall::WaitForFence, static_cast<std::uint32_t>(milliseconds) });
            return {};
        };
        return Check(gpu.fence->SetEventOnCompletion(value.Get(), gpu.fenceEvent.get()), ApiCall::SetEventOnCompletion).and_then([&] { return WaitOnEvent(gpu, timeout); });
    };
    if (IsComplete(gpu.fence.Get(), value))
        return {};
    return ArmAndWait(gpu, value, timeout);
}

Result<interior::FenceValue, Error> WaitIdle(const GpuDevice& gpu, interior::FenceValue previous) noexcept
{
    return SignalFence(gpu, previous).and_then([&gpu](interior::FenceValue signaled) {
        return WaitForFence(gpu, signaled, interior::MicrosecondsTag::Parse(kFenceTimeoutMicroseconds)).transform([signaled] { return signaled; });
    });
}

D3D12_CPU_DESCRIPTOR_HANDLE RtvHandle(const GpuDevice& gpu, std::uint32_t index) noexcept
{
    REQUIRE(index < kRtvSlots);
    return CpuHandleAt(gpu.rtvHeap.Get(), gpu.rtvIncrement, index);
}

D3D12_CPU_DESCRIPTOR_HANDLE SrvCpuHandle(const GpuDevice& gpu, std::uint32_t index) noexcept
{
    REQUIRE(index < kSrvSlots);
    return CpuHandleAt(gpu.srvHeap.Get(), gpu.srvIncrement, index);
}

D3D12_GPU_DESCRIPTOR_HANDLE SrvGpuHandle(const GpuDevice& gpu, std::uint32_t index) noexcept
{
    REQUIRE(index < kSrvSlots);
    return D3D12_GPU_DESCRIPTOR_HANDLE{ gpu.srvHeap->GetGPUDescriptorHandleForHeapStart().ptr + static_cast<UINT64>(index) * gpu.srvIncrement };
}

Result<Com<ID3D12CommandAllocator>, Error> CreateAllocator(const GpuDevice& gpu) noexcept
{
    Com<ID3D12CommandAllocator> allocator;
    const HRESULT hr = gpu.device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator));
    return Check(hr, ApiCall::CreateCommandAllocator).transform([&allocator] { return allocator; });
}

Result<Com<ID3D12GraphicsCommandList>, Error> CreateClosedCommandList(const GpuDevice& gpu, ID3D12CommandAllocator* allocator) noexcept
{
    Com<ID3D12GraphicsCommandList> list;
    const HRESULT hr = gpu.device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator, nullptr, IID_PPV_ARGS(&list));
    return Check(hr, ApiCall::CreateCommandList).and_then([&list] { return Check(list->Close(), ApiCall::CloseCommandList); }).transform([&list] { return list; });
}

Status<Error> ExecuteList(const GpuDevice& gpu, ID3D12GraphicsCommandList* list) noexcept
{
    ID3D12CommandList* lists[] = { list };
    gpu.queue->ExecuteCommandLists(1, lists);
    return {};
}

Status<Error> OpenCommandList(const GpuDevice& gpu, ID3D12GraphicsCommandList* list, ID3D12CommandAllocator* allocator) noexcept
{
    ID3D12DescriptorHeap* heaps[] = { gpu.srvHeap.Get() };
    return Check(list->Reset(allocator, nullptr), ApiCall::ResetCommandList).transform([&] { list->SetDescriptorHeaps(1, heaps); });
}

Result<interior::FenceValue, Error> FlushCommandList(const GpuDevice& gpu, ID3D12GraphicsCommandList* list, interior::FenceValue previous) noexcept
{
    return Check(list->Close(), ApiCall::CloseCommandList).and_then([&] { return ExecuteList(gpu, list); }).and_then([&] { return WaitIdle(gpu, previous); });
}

AdapterList UsableAdapters(IDXGIFactory4* factory) noexcept
{
    static constexpr auto WithUsable = [] [[nodiscard]] (const AdapterList& so, const std::optional<Candidate>& c) noexcept -> AdapterList {
        static constexpr auto IsListable = [] [[nodiscard]] (const std::optional<Candidate>& c) noexcept -> bool { return c.has_value() && IsUsable(*c); };
        if (!IsListable(c))
            return so;
        return so.Push(AdapterEntry{ c->index, NameOf(*c), IsNvidia(*c) }).value_or(so);
    };
    const auto add = [factory](const AdapterList& so, std::uint32_t index) { return WithUsable(so, CandidateAt(factory, index)); };
    return std::ranges::fold_left(std::views::iota(std::uint32_t{ 0 }, kMaxAdapters), AdapterList{}, add);
}

} // namespace real
