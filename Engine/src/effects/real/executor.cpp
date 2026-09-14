#include "effects/real/executor.h"

#include "infrastructure/fold.h"
#include "infrastructure/overloaded.h"

#include <algorithm>
#include <bit>
#include <ranges>
#include <span>

namespace real {
namespace {

using infra::Fail;
using infra::Result;
using infra::Status;
using interior::ResourceId;

constexpr std::uint32_t kDispatchDescriptors = kComputeSrvCount + kComputeUavCount;
constexpr std::array<float, 4> kBlack{ 0.0f, 0.0f, 0.0f, 1.0f };

struct Cursor
{
    std::uint32_t descriptor;
    interior::FenceValue fence;
};

using StepResult = Result<Cursor, Error>;

enum class ViewKind : std::uint8_t { Srv, Uav };

[[nodiscard]] Cursor Advanced(const Cursor& c, std::uint32_t count) noexcept
{
    return Cursor{ c.descriptor + count, c.fence };
}

#if DSCREEN_HAVE_NVOF
[[nodiscard]] Status<Error> RunOpticalFlow(const Gpu& gpu, const FrameContext& f, const Cursor& c) noexcept
{
    if (!gpu.opticalFlow.has_value())
        return {};
    return ExecuteOpticalFlow(*gpu.opticalFlow, gpu.device, OpticalFlowFrame{ f.number, f.set, f.hasPrevious, c.fence });
}
#else
[[nodiscard]] Status<Error> RunOpticalFlow(const Gpu&, const FrameContext&, const Cursor&) noexcept
{
    return {};
}
#endif

} // namespace

Status<Error> OpenList(const Gpu& gpu, interior::FrameSlot slot) noexcept
{
    return OpenCommandList(gpu.device, gpu.list.Get(), gpu.allocators[slot.Get()].Get());
}

Result<interior::FenceValue, Error> FlushList(const Gpu& gpu, interior::FenceValue previous) noexcept
{
    return FlushCommandList(gpu.device, gpu.list.Get(), previous);
}

Result<interior::FenceValue, Error> ExecuteSteps(const Gpu& gpu, const FrameContext& f, const interior::StepList& steps) noexcept
{
    // WAIVER(R7): the real and the simulated interpreter dispatch the same step variant; every arm differs.
    static constexpr auto ExecuteStep = [] [[nodiscard]] (const Gpu& gpu, const FrameContext& f, const interior::Step& step, const Cursor& c) noexcept -> StepResult {
        static constexpr auto RequireBudget = [] [[nodiscard]] (const Cursor& c, std::uint32_t count) noexcept -> Status<Error> {
            if (c.descriptor + count > interior::kDescriptorsPerFrame)
                return Fail(Error{ ApiCall::DescriptorBudget, c.descriptor });
            return {};
        };

        static constexpr auto WithFence = [] [[nodiscard]] (const Cursor& c, interior::FenceValue fence) noexcept -> Cursor { return Cursor{ c.descriptor, fence }; };

        static constexpr auto HeapIndex = [] [[nodiscard]] (interior::FrameSlot slot, std::uint32_t offset) noexcept -> std::uint32_t { return slot.Get() * interior::kDescriptorsPerFrame + offset; };

        static constexpr auto IsBackBuffer = [] [[nodiscard]] (const ResourceId& id) noexcept -> bool { return id.kind == interior::ResourceKind::BackBuffer; };

        static constexpr auto WriteView = [] [[nodiscard]] (const Gpu& gpu, ViewKind kind, const std::optional<ResourceId>& id, std::uint32_t heapIndex) noexcept -> Status<Error> {
            static constexpr auto CreateNullView = [](const GpuDevice& device, ViewKind kind, D3D12_CPU_DESCRIPTOR_HANDLE handle) noexcept -> void {
                switch (kind)
                {
                case ViewKind::Srv: return CreateNullSrv(device, handle);
                case ViewKind::Uav: return CreateNullUav(device, handle);
                }
            };

            static constexpr auto CreateResourceView = [](const GpuDevice& device, ViewKind kind, ID3D12Resource* resource, D3D12_CPU_DESCRIPTOR_HANDLE handle) noexcept -> void {
                static constexpr auto CreateAnyUav = [](const GpuDevice& device, ID3D12Resource* resource, D3D12_CPU_DESCRIPTOR_HANDLE handle) noexcept -> void {
                    static constexpr auto IsBuffer = [] [[nodiscard]] (ID3D12Resource * resource) noexcept -> bool { return resource->GetDesc().Dimension == D3D12_RESOURCE_DIMENSION_BUFFER; };

                    static constexpr auto SizeOf = [] [[nodiscard]] (ID3D12Resource * buffer) noexcept -> interior::ByteCount {
                        return interior::ByteCountTag::Parse(static_cast<std::uint32_t>(buffer->GetDesc().Width));
                    };
                    if (IsBuffer(resource))
                        return CreateRawUav(device, resource, SizeOf(resource), handle);
                    return CreateUav(device, resource, FormatOf(resource), handle);
                };
                switch (kind)
                {
                case ViewKind::Srv: return CreateSrv(device, resource, FormatOf(resource), handle);
                case ViewKind::Uav: return CreateAnyUav(device, resource, handle);
                }
            };
            const D3D12_CPU_DESCRIPTOR_HANDLE handle = SrvCpuHandle(gpu.device, heapIndex);
            if (!id.has_value())
            {
                CreateNullView(gpu.device, kind, handle);
                return {};
            }
            return Lookup(gpu.resources, *id).transform([&](ID3D12Resource* resource) { CreateResourceView(gpu.device, kind, resource, handle); });
        };

        static constexpr auto RecordDispatch = [] [[nodiscard]] (const Gpu& gpu, interior::FrameSlot slot, const interior::Dispatch& d, const Cursor& c) noexcept -> StepResult {
            static constexpr auto WriteBinding = [] [[nodiscard]] (const Gpu& gpu, const interior::Binding& b, std::uint32_t base) noexcept -> Status<Error> {
                static constexpr auto WriteViews = [] [[nodiscard]] (const Gpu& gpu, ViewKind kind, std::span<const std::optional<ResourceId>> ids, std::uint32_t base) noexcept -> Status<Error> {
                    return infra::ForEach(std::views::iota(std::size_t{ 0 }, ids.size()), Status<Error>{},
                                          [&](std::size_t i) { return WriteView(gpu, kind, ids[i], base + static_cast<std::uint32_t>(i)); });
                };
                return WriteViews(gpu, ViewKind::Srv, b.srv, base).and_then([&] { return WriteViews(gpu, ViewKind::Uav, b.uav, base + kComputeSrvCount); });
            };

            static constexpr auto Dispatched = [] [[nodiscard]] (const Gpu& gpu, const interior::Dispatch& d, std::uint32_t base, const Cursor& c) noexcept -> Cursor {
                static constexpr auto SetComputeState = [](const Gpu& gpu, const interior::Dispatch& d, std::uint32_t base) noexcept -> void {
                    static constexpr auto SetComputeTables = [](const Gpu& gpu, std::uint32_t base) noexcept -> void {
                        gpu.list->SetComputeRootDescriptorTable(1, SrvGpuHandle(gpu.device, base));
                        gpu.list->SetComputeRootDescriptorTable(2, SrvGpuHandle(gpu.device, base + kComputeSrvCount));
                    };
                    gpu.list->SetComputeRootSignature(gpu.pipelines.computeRoot.Get());
                    gpu.list->SetPipelineState(PsoFor(gpu.pipelines, d.pass));
                    gpu.list->SetComputeRoot32BitConstants(0, d.constants.count, d.constants.values.data(), 0);
                    SetComputeTables(gpu, base);
                };
                SetComputeState(gpu, d, base);
                gpu.list->Dispatch(d.groups.x.Get(), d.groups.y.Get(), 1);
                return Advanced(c, kDispatchDescriptors);
            };
            const std::uint32_t base = HeapIndex(slot, c.descriptor);
            return RequireBudget(c, kDispatchDescriptors).and_then([&] { return WriteBinding(gpu, d.binding, base); }).transform([&] { return Dispatched(gpu, d, base, c); });
        };

        static constexpr auto RecordTransition = [] [[nodiscard]] (const Gpu& gpu, const interior::Transition& t, const Cursor& c) noexcept -> StepResult {
            return Lookup(gpu.resources, t.resource).transform([&](ID3D12Resource* resource) {
                RecordBarrier(gpu.list.Get(), resource, t.from, t.to);
                return c;
            });
        };

        static constexpr auto RecordCopy = [] [[nodiscard]] (const Gpu& gpu, const interior::CopyBuffer& copy, const Cursor& c) noexcept -> StepResult {
            return Lookup(gpu.resources, copy.source).and_then([&](ID3D12Resource* source) {
                return Lookup(gpu.resources, copy.destination).transform([&](ID3D12Resource* destination) {
                    gpu.list->CopyBufferRegion(destination, 0, source, 0, copy.bytes.Get());
                    return c;
                });
            });
        };

        static constexpr auto RecordClear = [] [[nodiscard]] (const Gpu& gpu, const interior::ClearTarget& clear, const Cursor& c) noexcept -> StepResult {
            REQUIRE(IsBackBuffer(clear.target));
            gpu.list->ClearRenderTargetView(RtvHandle(gpu.device, clear.target.buffer.Get()), kBlack.data(), 0, nullptr);
            return c;
        };

        static constexpr auto RecordSuperResolution = [] [[nodiscard]] (const Gpu& gpu, const interior::EvaluateSr& e, const Cursor& c) noexcept -> StepResult {
            static constexpr auto ResolvedIo = [] [[nodiscard]] (const ResourceTable& table, const interior::ModelIo& io) noexcept -> Result<ModelIo, Error> {
                return Lookup(table, io.color).and_then([&](ID3D12Resource* color) {
                    return Lookup(table, io.depth).and_then([&](ID3D12Resource* depth) {
                        return Lookup(table, io.motionVectors).and_then([&](ID3D12Resource* motionVectors) {
                            return Lookup(table, io.output).transform([&](ID3D12Resource* output) { return ModelIo{ color, depth, motionVectors, output }; });
                        });
                    });
                });
            };
            REQUIRE(gpu.models.runtime.has_value());
            REQUIRE(gpu.models.superResolution.has_value());
            return ResolvedIo(gpu.resources, e.io)
                .and_then([&](const ModelIo& io) { return EvaluateSuperResolution(*gpu.models.runtime, *gpu.models.superResolution, gpu.list.Get(), SrInputs{ io, e.render, e.reset }); })
                .transform([&c] { return c; });
        };

        // The passes hand each other two intermediate pictures in turn: what one writes the next reads, the first
        // reads the picture and the last writes the output. Both intermediates are found writable and left so.
        static constexpr auto RecordNeuralRendering = [] [[nodiscard]] (const Gpu& gpu, const interior::EvaluateNr& e, const Cursor& c) noexcept -> StepResult {
            static constexpr auto Intermediate = [] [[nodiscard]] (std::uint32_t pass) noexcept -> interior::ResourceId {
                const Result<interior::SetIndex, interior::UnitError> set = interior::SetIndexTag::Parse(pass % 2);
                ENSURE(set.has_value());
                return interior::NrPassId(*set);
            };

            static constexpr auto IsLast = [] [[nodiscard]] (const interior::EvaluateNr& e, std::uint32_t pass) noexcept -> bool { return pass + 1 == e.passes.Get(); };

            static constexpr auto InputOf = [] [[nodiscard]] (const interior::EvaluateNr& e, std::uint32_t pass) noexcept -> interior::ResourceId {
                return pass == 0 ? e.io.color : Intermediate(pass - 1);
            };

            static constexpr auto OutputOf = [] [[nodiscard]] (const interior::EvaluateNr& e, std::uint32_t pass) noexcept -> interior::ResourceId {
                return IsLast(e, pass) ? e.io.output : Intermediate(pass);
            };

            static constexpr auto WithIo = [] [[nodiscard]] (const interior::EvaluateNr& e, const interior::ResourceId& color, const interior::ResourceId& output) noexcept -> interior::EvaluateNr {
                return interior::EvaluateNr{
                    interior::ModelIo{ color, e.io.depth, e.io.motionVectors, output }, e.work, e.guide, e.mvScaleX, e.mvScaleY, e.reset, e.depthInverted, e.tuning, e.passes
                };
            };

            static constexpr auto Moved = [] [[nodiscard]] (const Gpu& gpu, const interior::ResourceId& id, interior::ResourceState from, interior::ResourceState to) noexcept -> infra::Status<Error> {
                return Lookup(gpu.resources, id).transform([&](ID3D12Resource* resource) { RecordBarrier(gpu.list.Get(), resource, from, to); });
            };

            // What the pass before wrote is read from now, and what this pass writes was read by the one before it, when
            // there was one that far back.
            static constexpr auto Handed = [] [[nodiscard]] (const Gpu& gpu, std::uint32_t pass) noexcept -> infra::Status<Error> {
                static constexpr auto ReusedIfAny = [] [[nodiscard]] (const Gpu& gpu, std::uint32_t pass) noexcept -> infra::Status<Error> {
                    if (pass < 2)
                        return {};
                    return Moved(gpu, Intermediate(pass), interior::ResourceState::ShaderRead, interior::ResourceState::UnorderedAccess);
                };
                if (pass == 0)
                    return {};
                return Moved(gpu, Intermediate(pass - 1), interior::ResourceState::UnorderedAccess, interior::ResourceState::ShaderRead).and_then([&] { return ReusedIfAny(gpu, pass); });
            };

            static constexpr auto RecordPass = [] [[nodiscard]] (const Gpu& gpu, const interior::EvaluateNr& e, std::uint32_t pass) noexcept -> infra::Status<Error> {
                REQUIRE(pass < gpu.models.neuralRendering.size());
                return Handed(gpu, pass).and_then([&] {
                    return EvaluateNeuralRendering(*gpu.models.runtime, gpu.models.neuralRendering[pass], gpu.list.Get(), e.tuning, WithIo(e, InputOf(e, pass), OutputOf(e, pass)), gpu.resources);
                });
            };

            // Every intermediate written is read by the next pass, so each that was used is left readable: the first
            // from two passes, the second from three. Both are put back the way they were found.
            static constexpr auto Restored = [] [[nodiscard]] (const Gpu& gpu, const interior::EvaluateNr& e) noexcept -> infra::Status<Error> {
                const std::uint32_t used = std::min(e.passes.Get() - 1, 2u);
                return infra::ForEach(std::views::iota(std::uint32_t{ 0 }, used), infra::Status<Error>{},
                                      [&](std::uint32_t k) { return Moved(gpu, Intermediate(k), interior::ResourceState::ShaderRead, interior::ResourceState::UnorderedAccess); });
            };
            REQUIRE(gpu.models.runtime.has_value());
            return infra::ForEach(std::views::iota(std::uint32_t{ 0 }, e.passes.Get()), infra::Status<Error>{}, [&](std::uint32_t pass) { return RecordPass(gpu, e, pass); })
                .and_then([&] { return Restored(gpu, e); })
                .transform([&c] { return c; });
        };

        static constexpr auto RecordDraw = [] [[nodiscard]] (const Gpu& gpu, interior::FrameSlot slot, const interior::Draw& d, const Cursor& c) noexcept -> StepResult {
            static constexpr auto Drawn = [] [[nodiscard]] (const Gpu& gpu, const interior::Draw& d, std::uint32_t base, const Cursor& c) noexcept -> Cursor {
                static constexpr auto SetBlitTarget = [](const Gpu& gpu, const interior::Draw& d) noexcept -> void {
                    static constexpr auto SetViewport = [](const Gpu& gpu) noexcept -> void {
                        static constexpr auto ViewportOf = [] [[nodiscard]] (const interior::Extent& e) noexcept -> D3D12_VIEWPORT {
                            return D3D12_VIEWPORT{ 0.0f, 0.0f, static_cast<float>(e.width.Get()), static_cast<float>(e.height.Get()), 0.0f, 1.0f };
                        };

                        static constexpr auto ScissorOf = [] [[nodiscard]] (const interior::Extent& e) noexcept -> D3D12_RECT {
                            return D3D12_RECT{ 0, 0, static_cast<LONG>(e.width.Get()), static_cast<LONG>(e.height.Get()) };
                        };
                        const D3D12_VIEWPORT viewport = ViewportOf(gpu.presenter.extent);
                        const D3D12_RECT scissor = ScissorOf(gpu.presenter.extent);
                        gpu.list->RSSetViewports(1, &viewport);
                        gpu.list->RSSetScissorRects(1, &scissor);
                    };
                    REQUIRE(IsBackBuffer(d.target));
                    const D3D12_CPU_DESCRIPTOR_HANDLE rtv = RtvHandle(gpu.device, d.target.buffer.Get());
                    gpu.list->OMSetRenderTargets(1, &rtv, FALSE, nullptr);
                    SetViewport(gpu);
                };

                static constexpr auto SetBlitPipeline = [](const Gpu& gpu, const interior::Draw& d, std::uint32_t base) noexcept -> void {
                    static constexpr auto BlitConstants = [] [[nodiscard]] (interior::DisplayMode mode, interior::Fraction split) noexcept -> std::array<std::uint32_t, 4> {
                        static constexpr auto ModeCode = [] [[nodiscard]] (interior::DisplayMode mode) noexcept -> std::uint32_t {
                            switch (mode)
                            {
                            case interior::DisplayMode::Processed: return 0;
                            case interior::DisplayMode::Original: return 1;
                            case interior::DisplayMode::Split: return 2;
                            }
                            return 0;
                        };
                        return { ModeCode(mode), std::bit_cast<std::uint32_t>(split.Get()), 0u, 0u };
                    };
                    const std::array<std::uint32_t, 4> constants = BlitConstants(d.mode, d.split);
                    gpu.list->SetGraphicsRootSignature(gpu.pipelines.blitRoot.Get());
                    gpu.list->SetPipelineState(gpu.pipelines.blit.Get());
                    gpu.list->SetGraphicsRoot32BitConstants(0, static_cast<UINT>(constants.size()), constants.data(), 0);
                    gpu.list->SetGraphicsRootDescriptorTable(1, SrvGpuHandle(gpu.device, base));
                };
                SetBlitTarget(gpu, d);
                SetBlitPipeline(gpu, d, base);
                gpu.list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
                gpu.list->DrawInstanced(3, 1, 0, 0);
                return Advanced(c, kBlitSrvCount);
            };
            const std::uint32_t base = HeapIndex(slot, c.descriptor);
            return RequireBudget(c, kBlitSrvCount)
                .and_then([&] { return WriteView(gpu, ViewKind::Srv, d.processed, base); })
                .and_then([&] { return WriteView(gpu, ViewKind::Srv, d.original, base + 1); })
                .transform([&] { return Drawn(gpu, d, base, c); });
        };

        static constexpr auto RecordSubmit = [] [[nodiscard]] (const Gpu& gpu, const FrameContext& f, const interior::Submit& s, const Cursor& c) noexcept -> StepResult {
            static constexpr auto SubmitList = [] [[nodiscard]] (const Gpu& gpu, const Cursor& c) noexcept -> StepResult {
                return Check(gpu.list->Close(), ApiCall::CloseCommandList)
                    .and_then([&] { return ExecuteList(gpu.device, gpu.list.Get()); })
                    .and_then([&] { return SignalFence(gpu.device, c.fence); })
                    .transform([&c](interior::FenceValue signaled) { return WithFence(c, signaled); });
            };

            static constexpr auto BetweenPhases = [] [[nodiscard]] (const Gpu& gpu, const FrameContext& f, const Cursor& c) noexcept -> StepResult {
                return RunOpticalFlow(gpu, f, c).and_then([&] { return OpenList(gpu, f.slot); }).transform([&c] { return c; });
            };
            switch (s.phase)
            {
            case interior::Phase::One: return SubmitList(gpu, c).and_then([&](const Cursor& n) { return BetweenPhases(gpu, f, n); });
            case interior::Phase::Two: return SubmitList(gpu, c);
            }
            return c;
        };

        static constexpr auto RecordPresent = [] [[nodiscard]] (const Gpu& gpu, const interior::Present& present, const Cursor& c) noexcept -> StepResult {
            return PresentFrame(gpu.presenter, present.vsync).and_then([&] { return SignalFence(gpu.device, c.fence); }).transform([&c](interior::FenceValue v) { return WithFence(c, v); });
        };
        return std::visit(infra::Overloaded{
                              [&](const interior::Transition& t) { return RecordTransition(gpu, t, c); },
                              [&](const interior::Dispatch& d) { return RecordDispatch(gpu, f.slot, d, c); },
                              [&](const interior::CopyBuffer& copy) { return RecordCopy(gpu, copy, c); },
                              [&](const interior::ClearTarget& clear) { return RecordClear(gpu, clear, c); },
                              [&](const interior::EvaluateSr& e) { return RecordSuperResolution(gpu, e, c); },
                              [&](const interior::EvaluateNr& e) { return RecordNeuralRendering(gpu, e, c); },
                              [&](const interior::Draw& d) { return RecordDraw(gpu, f.slot, d, c); },
                              [&](const interior::Submit& s) { return RecordSubmit(gpu, f, s, c); },
                              [&](const interior::Present& p) { return RecordPresent(gpu, p, c); },
                          },
                          step);
    };
    return OpenList(gpu, f.slot)
        .and_then([&] { return infra::FoldResult(steps.Items(), StepResult(Cursor{ 0, f.fence }), [&](const Cursor& c, const interior::Step& s) { return ExecuteStep(gpu, f, s, c); }); })
        .transform([](const Cursor& c) { return c.fence; });
}

} // namespace real
