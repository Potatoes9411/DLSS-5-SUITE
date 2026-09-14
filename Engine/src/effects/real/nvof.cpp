#include "effects/real/nvof.h"

#include "interior/pyramid.h"

#include <algorithm>

namespace real {
namespace {

using infra::Fail;
using infra::Result;
using infra::Status;

using CreateInstance = NV_OF_STATUS(NVOFAPI*)(uint32_t, NV_OF_D3D12_API_FUNCTION_LIST*);

// The library is the driver's, installed to the system folder, and is asked for there by its full path.
constexpr std::wstring_view kOpticalFlowLibrary = L"\\nvofapi64.dll";
constexpr std::size_t kLibraryPathCapacity = MAX_PATH + kOpticalFlowLibrary.size() + 1;

constexpr auto kZeroLevel = interior::LevelIndexTag::Parse(0);
static_assert(kZeroLevel.has_value());

[[nodiscard]] Status<Error> CheckFlow(NV_OF_STATUS status, ApiCall call) noexcept
{
    if (status != NV_OF_SUCCESS)
        return Fail(Error{ call, static_cast<std::uint32_t>(status) });
    return {};
}

struct Registration
{
    const NV_OF_D3D12_API_FUNCTION_LIST* api;
    NvOFHandle session;
    ID3D12Fence* input;
    ID3D12Fence* output;
};

struct Loaded
{
    TrustedFile file;
    UniqueModule library;
    NV_OF_D3D12_API_FUNCTION_LIST api;
};

} // namespace

void ModuleFreer::operator()(HMODULE module) const noexcept
{
    ENSURE(::FreeLibrary(module) != FALSE);
}

void SessionDestroyer::operator()(NvOFHandle session) const noexcept
{
    ENSURE(destroy(session) == NV_OF_SUCCESS);
}

void BufferUnregister::operator()(NvOFGPUBufferHandle buffer) const noexcept
{
    NV_OF_UNREGISTER_RESOURCE_PARAMS_D3D12 params{ buffer };
    ENSURE(unregister(&params) == NV_OF_SUCCESS);
}

Result<OpticalFlow, Error> CreateOpticalFlow(const GpuDevice& gpu, const interior::SessionPlan& plan, const ResourceTable& resources) noexcept
{
    // Named by its full path in the system folder, so the folder the executable sits in is never searched
    // for it, and its own imports are confined to the system folder too. Before it is loaded it is checked
    // and held the way the models are.
    static constexpr auto LoadApi = [] [[nodiscard]] () noexcept -> Result<Loaded, Error> {
        static constexpr auto LibraryPath = [] [[nodiscard]] () noexcept -> Result<interior::FilePath, Error> {
            std::array<wchar_t, kLibraryPathCapacity> chars{}; // WAIVER(R2): a local buffer filled once, before use, from two bounded pieces.
            const UINT length = ::GetSystemDirectoryW(chars.data(), MAX_PATH);
            if (length == 0 || length >= MAX_PATH)
                return Fail(LastError(ApiCall::LoadOpticalFlow));
            std::ranges::copy(kOpticalFlowLibrary, chars.begin() + static_cast<std::ptrdiff_t>(length));
            return interior::FilePath::Parse(chars.data()).transform_error([](infra::StringTooLong) { return Error{ ApiCall::LoadOpticalFlow, 0 }; });
        };

        static constexpr auto LoadedLibrary = [] [[nodiscard]] (const interior::FilePath& path) noexcept -> Result<UniqueModule, Error> {
            HMODULE module = ::LoadLibraryExW(path.CString(), nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
            if (module == nullptr)
                return Fail(LastError(ApiCall::LoadOpticalFlow));
            return UniqueModule(module);
        };

        static constexpr auto EntryPoint = [] [[nodiscard]] (HMODULE module) noexcept -> Result<CreateInstance, Error> {
            const FARPROC proc = ::GetProcAddress(module, "NvOFAPICreateInstanceD3D12");
            if (proc == nullptr)
                return Fail(LastError(ApiCall::LoadOpticalFlow));
            return reinterpret_cast<CreateInstance>(reinterpret_cast<void*>(proc));
        };

        static constexpr auto ApiOf = [] [[nodiscard]] (CreateInstance create) noexcept -> Result<NV_OF_D3D12_API_FUNCTION_LIST, Error> {
            NV_OF_D3D12_API_FUNCTION_LIST api{};
            return CheckFlow(create(NV_OF_API_VERSION, &api), ApiCall::OpticalFlowCreate).transform([&api] { return api; });
        };
        return LibraryPath().and_then([](const interior::FilePath& path) {
            return OpenTrusted(path, ModelKind::OpticalFlow).and_then([&path](TrustedFile file) {
                return LoadedLibrary(path).and_then([&file](UniqueModule library) {
                    return EntryPoint(library.get()).and_then(ApiOf).transform([&file, &library](const NV_OF_D3D12_API_FUNCTION_LIST& api) {
                        return Loaded{ std::move(file), std::move(library), api };
                    });
                });
            });
        });
    };

    static constexpr auto WithSession = [] [[nodiscard]] (Loaded loaded, const GpuDevice& gpu, const interior::SessionPlan& plan,
                                                          const ResourceTable& resources) noexcept -> Result<OpticalFlow, Error> {
        static constexpr auto SessionOf = [] [[nodiscard]] (const NV_OF_D3D12_API_FUNCTION_LIST& api, ID3D12Device* device) noexcept -> Result<OpticalFlowSession, Error> {
            NvOFHandle handle = nullptr;
            return CheckFlow(api.nvCreateOpticalFlowD3D12(device, &handle), ApiCall::OpticalFlowCreate).transform([&] { return OpticalFlowSession(handle, SessionDestroyer{ api.nvOFDestroy }); });
        };

        static constexpr auto Initialized = [] [[nodiscard]] (const NV_OF_D3D12_API_FUNCTION_LIST& api, NvOFHandle session, const interior::SessionPlan& plan) noexcept -> Status<Error> {
            static constexpr auto InitParamsOf = [] [[nodiscard]] (const interior::SessionPlan& plan) noexcept -> NV_OF_INIT_PARAMS {
                static constexpr auto OutputGridOf = [] [[nodiscard]] (interior::GridSize grid) noexcept -> NV_OF_OUTPUT_VECTOR_GRID_SIZE {
                    switch (grid)
                    {
                    case interior::GridSize::One: return NV_OF_OUTPUT_VECTOR_GRID_SIZE_1;
                    case interior::GridSize::Two: return NV_OF_OUTPUT_VECTOR_GRID_SIZE_2;
                    case interior::GridSize::Four: return NV_OF_OUTPUT_VECTOR_GRID_SIZE_4;
                    }
                    return NV_OF_OUTPUT_VECTOR_GRID_SIZE_1;
                };

                static constexpr auto HintGridOf = [] [[nodiscard]] (interior::GridSize grid) noexcept -> NV_OF_HINT_VECTOR_GRID_SIZE {
                    switch (grid)
                    {
                    case interior::GridSize::One: return NV_OF_HINT_VECTOR_GRID_SIZE_1;
                    case interior::GridSize::Two: return NV_OF_HINT_VECTOR_GRID_SIZE_2;
                    case interior::GridSize::Four: return NV_OF_HINT_VECTOR_GRID_SIZE_4;
                    }
                    return NV_OF_HINT_VECTOR_GRID_SIZE_1;
                };

                static constexpr auto PerfLevelOf = [] [[nodiscard]] (interior::PerfLevel level) noexcept -> NV_OF_PERF_LEVEL {
                    switch (level)
                    {
                    case interior::PerfLevel::Slow: return NV_OF_PERF_LEVEL_SLOW;
                    case interior::PerfLevel::Medium: return NV_OF_PERF_LEVEL_MEDIUM;
                    case interior::PerfLevel::Fast: return NV_OF_PERF_LEVEL_FAST;
                    }
                    return NV_OF_PERF_LEVEL_MEDIUM;
                };
                return NV_OF_INIT_PARAMS{ plan.source.width.Get(),
                                          plan.source.height.Get(),
                                          OutputGridOf(plan.nvofGrid),
                                          HintGridOf(plan.nvofGrid),
                                          NV_OF_MODE_OPTICALFLOW,
                                          PerfLevelOf(plan.nvofPerf),
                                          NV_OF_FALSE,
                                          NV_OF_FALSE,
                                          nullptr,
                                          NV_OF_STEREO_DISPARITY_RANGE_UNDEFINED,
                                          NV_OF_FALSE };
            };
            const NV_OF_INIT_PARAMS init = InitParamsOf(plan);
            return CheckFlow(api.nvOFInit(session, &init), ApiCall::OpticalFlowInit);
        };

        static constexpr auto CompletionFence = [] [[nodiscard]] (const GpuDevice& gpu) noexcept -> Result<Com<ID3D12Fence>, Error> { return CreateFence(gpu.device.Get(), D3D12_FENCE_FLAG_NONE); };

        static constexpr auto Assembled = [] [[nodiscard]] (Loaded loaded, OpticalFlowSession session, const Com<ID3D12Fence>& completion, const GpuDevice& gpu,
                                                            const ResourceTable& resources) noexcept -> Result<OpticalFlow, Error> {
            static constexpr auto RegisteredResource = [] [[nodiscard]] (const Registration& r, const ResourceTable& resources,
                                                                         const interior::ResourceId& id) noexcept -> Result<RegisteredBuffer, Error> {
                static constexpr auto Registered = [] [[nodiscard]] (const Registration& r, ID3D12Resource* resource) noexcept -> Result<RegisteredBuffer, Error> {
                    NvOFGPUBufferHandle handle = nullptr;
                    NV_OF_REGISTER_RESOURCE_PARAMS_D3D12 params{ resource, &handle, NV_OF_FENCE_POINT{ r.input, 0 }, NV_OF_FENCE_POINT{ r.output, 0 } };
                    return CheckFlow(r.api->nvOFRegisterResourceD3D12(r.session, &params), ApiCall::OpticalFlowRegister).transform([&] {
                        return RegisteredBuffer(handle, BufferUnregister{ r.api->nvOFUnregisterResourceD3D12 });
                    });
                };
                return Lookup(resources, id).and_then([&r](ID3D12Resource* resource) { return Registered(r, resource); });
            };

            static constexpr auto LumaIdOf = [] [[nodiscard]] (std::uint32_t set) noexcept -> interior::ResourceId {
                const Result<interior::SetIndex, interior::UnitError> s = interior::SetIndexTag::Parse(set);
                ENSURE(s.has_value());
                return interior::LumaId(*s, *kZeroLevel);
            };
            const Registration r{ &loaded.api, session.get(), gpu.fence.Get(), completion.Get() };
            return RegisteredResource(r, resources, LumaIdOf(0)).and_then([&](RegisteredBuffer first) {
                return RegisteredResource(r, resources, LumaIdOf(1)).and_then([&](RegisteredBuffer second) {
                    return RegisteredResource(r, resources, interior::SimpleId(interior::ResourceKind::OpticalFlowOutput)).transform([&](RegisteredBuffer flow) {
                        return OpticalFlow{ std::move(loaded.file), std::move(loaded.library), loaded.api, std::move(session), completion, { std::move(first), std::move(second) }, std::move(flow) };
                    });
                });
            });
        };
        return SessionOf(loaded.api, gpu.device.Get()).and_then([&](OpticalFlowSession session) {
            return Initialized(loaded.api, session.get(), plan).and_then([&] { return CompletionFence(gpu); }).and_then([&](const Com<ID3D12Fence>& completion) {
                return Assembled(std::move(loaded), std::move(session), completion, gpu, resources);
            });
        });
    };
    return LoadApi().and_then([&](Loaded loaded) { return WithSession(std::move(loaded), gpu, plan, resources); });
}

Status<Error> ExecuteOpticalFlow(const OpticalFlow& flow, const GpuDevice& gpu, const OpticalFlowFrame& frame) noexcept
{
    static constexpr auto InputParamsOf = [] [[nodiscard]] (const OpticalFlow& f, const OpticalFlowFrame& frame, NV_OF_FENCE_POINT* wait) noexcept -> NV_OF_EXECUTE_INPUT_PARAMS_D3D12 {
        static constexpr auto OtherSet = [] [[nodiscard]] (interior::SetIndex set) noexcept -> std::uint32_t { return set.Get() ^ 1u; };
        return NV_OF_EXECUTE_INPUT_PARAMS_D3D12{ f.luma[frame.set.Get()].get(), f.luma[OtherSet(frame.set)].get(), nullptr, NV_OF_FALSE, 0, wait, 1, 0, nullptr, 0, nullptr };
    };

    static constexpr auto OutputParamsOf = [] [[nodiscard]] (const OpticalFlow& f, const OpticalFlowFrame& frame) noexcept -> NV_OF_EXECUTE_OUTPUT_PARAMS_D3D12 {
        return NV_OF_EXECUTE_OUTPUT_PARAMS_D3D12{ f.flow.get(), nullptr, NV_OF_FENCE_POINT{ f.completion.Get(), frame.number.Get() + 1 }, nullptr };
    };
    if (!frame.hasPrevious)
        return {};
    NV_OF_FENCE_POINT wait{ gpu.fence.Get(), frame.phaseOne.Get() };
    const NV_OF_EXECUTE_INPUT_PARAMS_D3D12 in = InputParamsOf(flow, frame, &wait);
    NV_OF_EXECUTE_OUTPUT_PARAMS_D3D12 out = OutputParamsOf(flow, frame);
    return CheckFlow(flow.api.nvOFExecuteD3D12(flow.session.get(), &in, &out), ApiCall::OpticalFlowExecute).and_then([&] {
        return Check(gpu.queue->Wait(flow.completion.Get(), out.fencePoint.value), ApiCall::QueueWait);
    });
}

} // namespace real
