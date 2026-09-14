#include "interior/frame.h"

#include "infrastructure/array_util.h"
#include "infrastructure/fold.h"
#include "interior/pyramid.h"

#include <algorithm>
#include <array>
#include <bit>
#include <ranges>

namespace interior {
namespace {

using infra::Fail;

struct Builder
{
    StepList steps;
    StateTable states;
};

using BuildResult = Result<Builder, PlanFrameError>;

constexpr Fraction kCentreSplit = *FractionTag::Parse(0.5f); // the divider starts in the middle
constexpr float kLambda = 0.004f;
constexpr float kZeroBias = 0.006f;
constexpr float kBadThreshold = 0.12f;
constexpr std::uint32_t kCoarseRadius = 3;
constexpr std::uint32_t kFineRadius = 1;
constexpr std::uint32_t kFlagHasPrediction = 1;
constexpr std::uint32_t kFlagCountUnmatched = 2;
constexpr std::uint32_t kFlagSubpixel = 4;

constexpr auto kZeroLevel = LevelIndexTag::Parse(0);
constexpr auto kZeroSet = SetIndexTag::Parse(0);
constexpr auto kZeroBuffer = BackBufferIndexTag::Parse(0);
constexpr auto kZeroSlot = FrameSlotTag::Parse(0);
static_assert(kZeroLevel.has_value() && kZeroSet.has_value() && kZeroBuffer.has_value() && kZeroSlot.has_value());

// --- step emission -----------------------------------------------------------------

[[nodiscard]] BuildResult Emit(const Builder& b, const Step& step) noexcept
{
    static constexpr auto FromCapacity = [] [[nodiscard]] (infra::CapacityExceeded) noexcept -> PlanFrameError { return PlanFrameError::Capacity; };
    return b.steps.Push(step).transform([&b](const StepList& steps) { return Builder{ steps, b.states }; }).transform_error(FromCapacity);
}

[[nodiscard]] BuildResult MoveTo(const Builder& b, const ResourceId& id, ResourceState to) noexcept
{
    static constexpr auto WithState = [] [[nodiscard]] (const Builder& b, const ResourceId& id, ResourceState state) noexcept -> Builder {
        return Builder{ b.steps, infra::WithElement(b.states, SlotOf(id), state) };
    };
    const ResourceState from = StateOf(b.states, id);
    if (from == to)
        return b;
    return Emit(b, Step{ Transition{ id, from, to } }).transform([&id, to](const Builder& next) { return WithState(next, id, to); });
}

// --- phase two: matching ------------------------------------------------------------------

[[nodiscard]] BuildResult StatsReadback(const Builder& b, FrameSlot slot) noexcept
{
    return MoveTo(b, SimpleId(ResourceKind::Stats), ResourceState::CopySource).and_then([slot](const Builder& n) {
        return Emit(n, Step{ CopyBuffer{ SimpleId(ResourceKind::Stats), ReadbackId(slot), ByteCountTag::Parse(4) } });
    });
}

// --- phase two: models ------------------------------------------------------------------------

[[nodiscard]] ResourceKind ColorSourceOf(const SessionPlan& plan) noexcept
{
    return plan.superResolution.has_value() ? ResourceKind::SrOutput : ResourceKind::ModelColor;
}

[[nodiscard]] ResourceKind DisplaySourceOf(const SessionPlan& plan, bool neuralRendering) noexcept
{
    return neuralRendering ? ResourceKind::NrOutput : ColorSourceOf(plan);
}

// --- blit and present --------------------------------------------------------------------------

// What the blit shows this frame: the mode and, for the split view, where the divider sits.
struct Display
{
    DisplayMode mode;
    Fraction split;
};

// --- state bookkeeping ---------------------------------------------------------------------------

// Capture delivers a frame only when the desktop changes, so a still screen sends nothing and would keep
// whatever the model last made of it. A changed setting is its own reason to run the frame again.
[[nodiscard]] bool Retuned(const FrameState& state, const FrameInput& input) noexcept
{
    return input.controlRequest.has_value() && *input.controlRequest != state.controls;
}

} // namespace

ResourceId SimpleId(ResourceKind kind) noexcept
{
    return ResourceId{ kind, *kZeroSet, *kZeroLevel, *kZeroBuffer, *kZeroSlot };
}

ResourceId LumaId(SetIndex set, LevelIndex level) noexcept
{
    return ResourceId{ ResourceKind::Luma, set, level, *kZeroBuffer, *kZeroSlot };
}

ResourceId FlowId(LevelIndex level) noexcept
{
    return ResourceId{ ResourceKind::Flow, *kZeroSet, level, *kZeroBuffer, *kZeroSlot };
}

ResourceId NrPassId(SetIndex set) noexcept
{
    return ResourceId{ ResourceKind::NrPass, set, *kZeroLevel, *kZeroBuffer, *kZeroSlot };
}

ResourceId BackBufferId(BackBufferIndex index) noexcept
{
    return ResourceId{ ResourceKind::BackBuffer, *kZeroSet, *kZeroLevel, index, *kZeroSlot };
}

ResourceId ReadbackId(FrameSlot slot) noexcept
{
    return ResourceId{ ResourceKind::StatsReadback, *kZeroSet, *kZeroLevel, *kZeroBuffer, slot };
}

std::size_t SlotOf(const ResourceId& id) noexcept
{
    switch (id.kind)
    {
    case ResourceKind::Canvas: return 0;
    case ResourceKind::ModelColor: return 1;
    case ResourceKind::Depth: return 2;
    case ResourceKind::MotionVectors: return 3;
    case ResourceKind::Stats: return 4;
    case ResourceKind::ZeroBuffer: return 5;
    case ResourceKind::SrOutput: return 6;
    case ResourceKind::NrOutput: return 7;
    case ResourceKind::NrPass: return 38 + id.set.Get();
    case ResourceKind::OpticalFlowOutput: return 8;
    case ResourceKind::BackBuffer: return 9 + id.buffer.Get();
    case ResourceKind::StatsReadback: return 12 + id.slot.Get();
    case ResourceKind::Luma: return 14 + id.set.Get() * kMaxLevels + id.level.Get();
    case ResourceKind::Flow: return 30 + id.level.Get();
    }
    return 0;
}

ResourceState StateOf(const StateTable& table, const ResourceId& id) noexcept
{
    return table[SlotOf(id)];
}

struct InitialEntry
{
    std::size_t slot;
    ResourceState state;
};

constexpr std::array<InitialEntry, 10> kInitialEntries{ { { 0, ResourceState::Common },
                                                          { 2, ResourceState::ShaderRead },
                                                          { 4, ResourceState::CopyDest },
                                                          { 5, ResourceState::GenericRead },
                                                          { 8, ResourceState::Common },
                                                          { 9, ResourceState::Present },
                                                          { 10, ResourceState::Present },
                                                          { 11, ResourceState::Present },
                                                          { 12, ResourceState::CopyDest },
                                                          { 13, ResourceState::CopyDest } } };

StateTable InitialStates() noexcept
{
    static constexpr auto WithEntry = [] [[nodiscard]] (const StateTable& table, const InitialEntry& entry) noexcept -> StateTable { return infra::WithElement(table, entry.slot, entry.state); };
    return std::ranges::fold_left(kInitialEntries, infra::Filled<ResourceState, kSlotCount>(ResourceState::UnorderedAccess), WithEntry);
}

FrameState InitialFrameState(const SessionPlan& plan) noexcept
{
    return FrameState{ FrameNumberTag::Parse(0),
                       *kZeroSet,
                       InitialStates(),
                       false,
                       false,
                       false,
                       false,
                       std::nullopt,
                       plan.initialDisplay,
                       { FenceValueTag::Parse(0), FenceValueTag::Parse(0) },
                       { false, false },
                       DisplaySourceOf(plan, plan.neuralRendering),
                       kCentreSplit,
                       StartingLive(plan) };
}

FrameSlot SlotOfFrame(FrameNumber number) noexcept
{
    const Result<FrameSlot, UnitError> slot = FrameSlotTag::Parse(static_cast<std::uint32_t>(number.Get() % kFrameSlotCount));
    ENSURE(slot.has_value());
    return *slot;
}

Fraction NextSplit(Fraction current, const std::optional<Fraction>& request) noexcept
{
    return request.value_or(current);
}

LiveSettings NextControls(const LiveSettings& current, const std::optional<LiveSettings>& request) noexcept
{
    return request.value_or(current);
}

DisplayMode NextDisplay(DisplayMode current, bool toggleOriginal, bool toggleSplit) noexcept
{
    static constexpr auto ToggledIf = [] [[nodiscard]] (DisplayMode current, DisplayMode mode, bool toggle) noexcept -> DisplayMode {
        static constexpr auto Toggled = [] [[nodiscard]] (DisplayMode current, DisplayMode mode) noexcept -> DisplayMode { return current == mode ? DisplayMode::Processed : mode; };
        return toggle ? Toggled(current, mode) : current;
    };
    return ToggledIf(ToggledIf(current, DisplayMode::Original, toggleOriginal), DisplayMode::Split, toggleSplit);
}

bool IsLongPause(const FrameState& state, Instant now) noexcept
{
    return state.lastCapture.has_value() && now.Get() - state.lastCapture->Get() > kPauseResetMicroseconds;
}

bool ExceedsThreshold(std::optional<Fraction> unmatched, Fraction threshold) noexcept
{
    return unmatched.has_value() && unmatched->Get() > threshold.Get();
}

Result<FramePlan, PlanFrameError> PlanFrame(const SessionPlan& plan, const FrameState& state, const FrameInput& input) noexcept
{
    static constexpr auto FromPyramid = [] [[nodiscard]] (PyramidError error) noexcept -> PlanFrameError {
        switch (error)
        {
        case PyramidError::Arithmetic: return PlanFrameError::Arithmetic;
        case PyramidError::Capacity: return PlanFrameError::Capacity;
        case PyramidError::Unit: return PlanFrameError::Unit;
        }
        return PlanFrameError::Unit;
    };

    static constexpr auto OtherSet = [] [[nodiscard]] (SetIndex set) noexcept -> SetIndex {
        const Result<SetIndex, UnitError> other = SetIndexTag::Parse(set.Get() ^ 1u);
        ENSURE(other.has_value());
        return *other;
    };

    static constexpr auto ProcessesFrame = [] [[nodiscard]] (const FrameState& state, const FrameInput& input) noexcept -> bool {
        static constexpr auto HasSomethingToRedo = [] [[nodiscard]] (const FrameState& state, const FrameInput& input) noexcept -> bool { return state.hasOutput && Retuned(state, input); };
        return input.freshCapture || HasSomethingToRedo(state, input);
    };

    static constexpr auto NextMode = [] [[nodiscard]] (const FrameState& state, const FrameInput& input) noexcept -> DisplayMode {
        return input.displayRequest.value_or(NextDisplay(state.display, input.toggleOriginal, input.toggleSplit));
    };

    static constexpr auto NextState = [] [[nodiscard]] (const SessionPlan& plan, const FrameState& state, const FrameInput& input, const StateTable& states, FrameSlot slot) noexcept -> FrameState {
        static constexpr auto EmitsStats = [] [[nodiscard]] (const SessionPlan& plan, bool fresh) noexcept -> bool { return fresh && plan.motion == MotionBackend::BuiltIn; };

        static constexpr auto NextSet = [] [[nodiscard]] (const FrameState& state, bool fresh) noexcept -> SetIndex { return fresh ? OtherSet(state.currentSet) : state.currentSet; };

        static constexpr auto NextCapture = [] [[nodiscard]] (const FrameState& state, const FrameInput& input) noexcept -> std::optional<Instant> {
            return input.freshCapture ? std::optional<Instant>{ input.now } : state.lastCapture;
        };

        static constexpr auto OrProcessed = [] [[nodiscard]] (bool flag, const FrameState& state, const FrameInput& input) noexcept -> bool { return flag || ProcessesFrame(state, input); };
        return FrameState{ FrameNumberTag::Parse(state.number.Get() + 1),
                           NextSet(state, ProcessesFrame(state, input)),
                           states,
                           OrProcessed(state.hasOutput, state, input),
                           OrProcessed(state.hasPrevious, state, input),
                           ExceedsThreshold(input.unmatched, NextControls(state.controls, input.controlRequest).resetThreshold),
                           OrProcessed(state.zeroMotionWritten, state, input),
                           NextCapture(state, input),
                           NextMode(state, input),
                           state.slotFences,
                           infra::WithElement(state.statsPending, slot.Get(), EmitsStats(plan, ProcessesFrame(state, input))),
                           DisplaySourceOf(plan, NextControls(state.controls, input.controlRequest).neuralRendering),
                           NextSplit(state.split, input.splitRequest),
                           NextControls(state.controls, input.controlRequest) };
    };

    static constexpr auto StepsFor = [] [[nodiscard]] (const SessionPlan& plan, const FrameState& state, const FrameInput& input, const LevelExtents& extents, FrameSlot slot) noexcept -> BuildResult {
        static constexpr auto BlitSteps = [] [[nodiscard]] (const Builder& b, bool hasOutput, ResourceKind source, const Display& display, BackBufferIndex index, bool vsync) noexcept -> BuildResult {
            static constexpr auto TargetContent = [] [[nodiscard]] (const Builder& b, bool hasOutput, ResourceKind source, const Display& display, BackBufferIndex index) noexcept -> BuildResult {
                static constexpr auto DrawSteps = [] [[nodiscard]] (const Builder& b, ResourceKind source, const Display& display, BackBufferIndex index) noexcept -> BuildResult {
                    return MoveTo(b, SimpleId(source), ResourceState::ShaderRead)
                        .and_then([](const Builder& n) { return MoveTo(n, SimpleId(ResourceKind::ModelColor), ResourceState::ShaderRead); })
                        .and_then([&](const Builder& n) { return Emit(n, Step{ Draw{ SimpleId(source), SimpleId(ResourceKind::ModelColor), BackBufferId(index), display.mode, display.split } }); });
                };

                static constexpr auto ClearSteps = [] [[nodiscard]] (const Builder& b, BackBufferIndex index) noexcept -> BuildResult { return Emit(b, Step{ ClearTarget{ BackBufferId(index) } }); };
                return hasOutput ? DrawSteps(b, source, display, index) : ClearSteps(b, index);
            };
            return MoveTo(b, BackBufferId(index), ResourceState::RenderTarget)
                .and_then([&](const Builder& n) { return TargetContent(n, hasOutput, source, display, index); })
                .and_then([index](const Builder& n) { return MoveTo(n, BackBufferId(index), ResourceState::Present); })
                .and_then([](const Builder& n) { return Emit(n, Step{ Submit{ Phase::Two } }); })
                .and_then([vsync](const Builder& n) { return Emit(n, Step{ Present{ vsync } }); });
        };

        static constexpr auto DisplayOf = [] [[nodiscard]] (const FrameState& state, const FrameInput& input) noexcept -> Display {
            return Display{ NextMode(state, input), NextSplit(state.split, input.splitRequest) };
        };

        static constexpr auto FreshSteps = [] [[nodiscard]] (const SessionPlan& plan, const FrameState& state, const FrameInput& input, const LevelExtents& extents,
                                                             FrameSlot slot) noexcept -> BuildResult {
            static constexpr auto Level = [] [[nodiscard]] (std::uint32_t raw) noexcept -> Result<LevelIndex, PlanFrameError> {
                static constexpr auto FromUnit = [] [[nodiscard]] (UnitError) noexcept -> PlanFrameError { return PlanFrameError::Unit; };
                return LevelIndexTag::Parse(raw).transform_error(FromUnit);
            };

            static constexpr auto Bind = [] [[nodiscard]] (std::optional<ResourceId> s0, std::optional<ResourceId> s1, std::optional<ResourceId> s2, std::optional<ResourceId> u0,
                                                           std::optional<ResourceId> u1) noexcept -> Binding { return Binding{ { s0, s1, s2, std::nullopt }, { u0, u1 } }; };

            static constexpr auto Consts = [] [[nodiscard]] (std::array<std::uint32_t, 16> values, std::uint32_t count) noexcept -> Constants { return Constants{ values, count }; };

            static constexpr auto EmitDispatch = [] [[nodiscard]] (const Builder& b, PassId pass, const Binding& binding, const Constants& constants, const Extent& extent) noexcept -> BuildResult {
                return GroupsFor(extent).transform_error(FromPyramid).and_then([&](ThreadGroups groups) { return Emit(b, Step{ Dispatch{ pass, binding, constants, groups } }); });
            };

            static constexpr auto PhaseOne = [] [[nodiscard]] (const Builder& b, const SessionPlan& plan, const LevelExtents& extents, SetIndex set) noexcept -> BuildResult {
                static constexpr auto ConvertSteps = [] [[nodiscard]] (const Builder& b, const SessionPlan& plan, SetIndex set) noexcept -> BuildResult {
                    static constexpr auto ConvertDispatch = [] [[nodiscard]] (const Builder& b, const SessionPlan& plan, SetIndex set) noexcept -> BuildResult {
                        static constexpr auto ConvertConstants = [] [[nodiscard]] (const Extent& e) noexcept -> Constants { return Consts({ e.width.Get(), e.height.Get(), 0u, 0u }, 4); };
                        const Binding binding = Bind(SimpleId(ResourceKind::Canvas), std::nullopt, std::nullopt, SimpleId(ResourceKind::ModelColor), LumaId(set, *kZeroLevel));
                        return EmitDispatch(b, PassId::Convert, binding, ConvertConstants(plan.source), plan.source);
                    };
                    return MoveTo(b, SimpleId(ResourceKind::Canvas), ResourceState::ShaderRead)
                        .and_then([set](const Builder& n) { return MoveTo(n, LumaId(set, *kZeroLevel), ResourceState::UnorderedAccess); })
                        .and_then([](const Builder& n) { return MoveTo(n, SimpleId(ResourceKind::ModelColor), ResourceState::UnorderedAccess); })
                        .and_then([&plan, set](const Builder& n) { return ConvertDispatch(n, plan, set); })
                        .and_then([](const Builder& n) { return MoveTo(n, SimpleId(ResourceKind::Canvas), ResourceState::Common); })
                        .and_then([](const Builder& n) { return MoveTo(n, SimpleId(ResourceKind::ModelColor), ResourceState::ShaderRead); });
                };

                static constexpr auto MotionPhaseOne = [] [[nodiscard]] (const Builder& b, const SessionPlan& plan, const LevelExtents& extents, SetIndex set) noexcept -> BuildResult {
                    static constexpr auto PyramidSteps = [] [[nodiscard]] (const Builder& b, const SessionPlan& plan, const LevelExtents& extents, SetIndex set) noexcept -> BuildResult {
                        static constexpr auto DownsampleLevel = [] [[nodiscard]] (const Builder& b, const LevelExtents& extents, SetIndex set, std::uint32_t level) noexcept -> BuildResult {
                            static constexpr auto DownsampleConstants = [] [[nodiscard]] (const Extent& dst, const Extent& src) noexcept -> Constants {
                                return Consts({ dst.width.Get(), dst.height.Get(), src.width.Get(), src.height.Get() }, 4);
                            };
                            return Level(level).and_then([&](LevelIndex dst) {
                                return Level(level - 1).and_then([&](LevelIndex src) {
                                    return MoveTo(b, LumaId(set, src), ResourceState::ShaderRead)
                                        .and_then([&](const Builder& n) { return MoveTo(n, LumaId(set, dst), ResourceState::UnorderedAccess); })
                                        .and_then([&](const Builder& n) {
                                            return EmitDispatch(n, PassId::Downsample, Bind(LumaId(set, src), std::nullopt, std::nullopt, LumaId(set, dst), std::nullopt),
                                                                DownsampleConstants(extents.At(level), extents.At(level - 1)), extents.At(level));
                                        });
                                });
                            });
                        };
                        return infra::FoldResult(std::views::iota(std::uint32_t{ 1 }, plan.levels.Get()), BuildResult(b),
                                                 [&](const Builder& acc, std::uint32_t level) { return DownsampleLevel(acc, extents, set, level); })
                            .and_then(
                                [&](const Builder& n) { return Level(plan.levels.Get() - 1).and_then([&](LevelIndex last) { return MoveTo(n, LumaId(set, last), ResourceState::ShaderRead); }); });
                    };

                    static constexpr auto OpticalFlowHandoff = [] [[nodiscard]] (const Builder& b, SetIndex set) noexcept -> BuildResult {
                        return MoveTo(b, LumaId(set, *kZeroLevel), ResourceState::Common)
                            .and_then([set](const Builder& n) { return MoveTo(n, LumaId(OtherSet(set), *kZeroLevel), ResourceState::Common); })
                            .and_then([](const Builder& n) { return MoveTo(n, SimpleId(ResourceKind::OpticalFlowOutput), ResourceState::Common); });
                    };
                    switch (plan.motion)
                    {
                    case MotionBackend::BuiltIn: return PyramidSteps(b, plan, extents, set);
                    case MotionBackend::NvOpticalFlow: return OpticalFlowHandoff(b, set);
                    case MotionBackend::None: return MoveTo(b, LumaId(set, *kZeroLevel), ResourceState::ShaderRead);
                    }
                    return b;
                };
                return ConvertSteps(b, plan, set).and_then([&](const Builder& n) { return MotionPhaseOne(n, plan, extents, set); }).and_then([](const Builder& n) {
                    return Emit(n, Step{ Submit{ Phase::One } });
                });
            };

            static constexpr auto MotionPhaseTwo = [] [[nodiscard]] (const Builder& b, const SessionPlan& plan, const LevelExtents& extents, const FrameState& state,
                                                                     FrameSlot slot) noexcept -> BuildResult {
                static constexpr auto Bits = [] [[nodiscard]] (float value) noexcept -> std::uint32_t { return std::bit_cast<std::uint32_t>(value); };

                static constexpr auto FinalizeConstants = [] [[nodiscard]] (const Extent& from, const Extent& to, float scale) noexcept -> Constants {
                    return Consts({ from.width.Get(), from.height.Get(), to.width.Get(), to.height.Get(), 0u, 0u, 0u, 0u, 0u, Bits(scale) }, 10);
                };

                static constexpr auto BlockMatchPhaseTwo = [] [[nodiscard]] (const Builder& b, const SessionPlan& plan, const LevelExtents& extents, SetIndex set,
                                                                             FrameSlot slot) noexcept -> BuildResult {
                    static constexpr auto MatchAllLevels = [] [[nodiscard]] (const Builder& b, const SessionPlan& plan, const LevelExtents& extents, SetIndex set) noexcept -> BuildResult {
                        static constexpr auto MatchLevel = [] [[nodiscard]] (const Builder& b, const SessionPlan& plan, const LevelExtents& extents, SetIndex set,
                                                                             std::uint32_t level) noexcept -> BuildResult {
                            static constexpr auto MatchDispatch = [] [[nodiscard]] (const Builder& b, const SessionPlan& plan, const LevelExtents& extents, SetIndex set, std::uint32_t level,
                                                                                    LevelIndex index) noexcept -> BuildResult {
                                static constexpr auto IsCoarsest = [] [[nodiscard]] (const SessionPlan& plan, std::uint32_t level) noexcept -> bool { return level + 1 == plan.levels.Get(); };

                                static constexpr auto MatchConstants = [] [[nodiscard]] (const SessionPlan& plan, const Extent& e, std::uint32_t level) noexcept -> Constants {
                                    static constexpr auto MatchFlags = [] [[nodiscard]] (const SessionPlan& plan, std::uint32_t level) noexcept -> std::uint32_t {
                                        static constexpr auto PredictionFlag = [] [[nodiscard]] (const SessionPlan& plan, std::uint32_t level) noexcept -> std::uint32_t {
                                            return IsCoarsest(plan, level) ? 0u : kFlagHasPrediction;
                                        };

                                        static constexpr auto FinestFlags = [] [[nodiscard]] (const SessionPlan& plan, std::uint32_t level) noexcept -> std::uint32_t {
                                            static constexpr auto IsFinest = [] [[nodiscard]] (const SessionPlan& plan, std::uint32_t level) noexcept -> bool {
                                                return level == plan.finestLevel.Get();
                                            };
                                            return IsFinest(plan, level) ? (kFlagCountUnmatched | kFlagSubpixel) : 0u;
                                        };
                                        return PredictionFlag(plan, level) | FinestFlags(plan, level);
                                    };

                                    static constexpr auto RadiusFor = [] [[nodiscard]] (const SessionPlan& plan, std::uint32_t level) noexcept -> std::uint32_t {
                                        return IsCoarsest(plan, level) ? kCoarseRadius : kFineRadius;
                                    };
                                    return Consts(
                                        { e.width.Get(), e.height.Get(), 0u, 0u, RadiusFor(plan, level), MatchFlags(plan, level), Bits(kLambda), Bits(kZeroBias), Bits(kBadThreshold), Bits(1.0f) },
                                        10);
                                };

                                static constexpr auto PredictionOf = [] [[nodiscard]] (const SessionPlan& plan, std::uint32_t level) noexcept -> std::optional<ResourceId> {
                                    if (IsCoarsest(plan, level))
                                        return std::nullopt;
                                    return Level(level + 1).transform([](LevelIndex coarser) { return std::optional<ResourceId>{ FlowId(coarser) }; }).value_or(std::nullopt);
                                };

                                static constexpr auto PredictionReady = [] [[nodiscard]] (const Builder& b, std::optional<ResourceId> prediction) noexcept -> BuildResult {
                                    if (!prediction.has_value())
                                        return b;
                                    return MoveTo(b, *prediction, ResourceState::ShaderRead);
                                };
                                const std::optional<ResourceId> prediction = PredictionOf(plan, level);
                                const Binding binding = Bind(LumaId(set, index), LumaId(OtherSet(set), index), prediction, FlowId(index), SimpleId(ResourceKind::Stats));
                                return PredictionReady(b, prediction).and_then([&](const Builder& n) {
                                    return EmitDispatch(n, PassId::Match, binding, MatchConstants(plan, extents.At(level), level), extents.At(level));
                                });
                            };
                            return Level(level).and_then([&](LevelIndex index) {
                                return MoveTo(b, LumaId(set, index), ResourceState::ShaderRead)
                                    .and_then([&](const Builder& n) { return MoveTo(n, LumaId(OtherSet(set), index), ResourceState::ShaderRead); })
                                    .and_then([&](const Builder& n) { return MoveTo(n, FlowId(index), ResourceState::UnorderedAccess); })
                                    .and_then([&](const Builder& n) { return MatchDispatch(n, plan, extents, set, level, index); });
                            });
                        };

                        static constexpr auto DescendingLevel = [] [[nodiscard]] (const SessionPlan& plan, std::uint32_t offset) noexcept -> std::uint32_t { return plan.levels.Get() - 1 - offset; };
                        const std::uint32_t count = plan.levels.Get() - plan.finestLevel.Get();
                        return infra::FoldResult(std::views::iota(std::uint32_t{ 0 }, count), BuildResult(b),
                                                 [&](const Builder& acc, std::uint32_t offset) { return MatchLevel(acc, plan, extents, set, DescendingLevel(plan, offset)); });
                    };

                    static constexpr auto StatsClear = [] [[nodiscard]] (const Builder& b) noexcept -> BuildResult {
                        return MoveTo(b, SimpleId(ResourceKind::Stats), ResourceState::CopyDest)
                            .and_then(
                                [](const Builder& n) { return Emit(n, Step{ CopyBuffer{ SimpleId(ResourceKind::ZeroBuffer), SimpleId(ResourceKind::Stats), ByteCountTag::Parse(kStatsBytes) } }); })
                            .and_then([](const Builder& n) { return MoveTo(n, SimpleId(ResourceKind::Stats), ResourceState::UnorderedAccess); });
                    };

                    static constexpr auto FinalizeSteps = [] [[nodiscard]] (const Builder& b, const SessionPlan& plan, const LevelExtents& extents) noexcept -> BuildResult {
                        const Extent finest = extents.At(plan.finestLevel.Get());
                        const float scale = static_cast<float>(1u << plan.finestLevel.Get());
                        return MoveTo(b, FlowId(plan.finestLevel), ResourceState::ShaderRead)
                            .and_then([](const Builder& n) { return MoveTo(n, SimpleId(ResourceKind::MotionVectors), ResourceState::UnorderedAccess); })
                            .and_then([&](const Builder& n) {
                                return EmitDispatch(n, PassId::Finalize, Bind(std::nullopt, std::nullopt, FlowId(plan.finestLevel), SimpleId(ResourceKind::MotionVectors), std::nullopt),
                                                    FinalizeConstants(finest, plan.source, scale), plan.source);
                            })
                            .and_then([](const Builder& n) { return MoveTo(n, SimpleId(ResourceKind::MotionVectors), ResourceState::ShaderRead); });
                    };
                    return StatsClear(b)
                        .and_then([&](const Builder& n) { return MatchAllLevels(n, plan, extents, set); })
                        .and_then([&](const Builder& n) { return FinalizeSteps(n, plan, extents); })
                        .and_then([slot](const Builder& n) { return StatsReadback(n, slot); });
                };

                static constexpr auto OpticalFlowPhaseTwo = [] [[nodiscard]] (const Builder& b, const SessionPlan& plan, bool hasPrevious) noexcept -> BuildResult {
                    static constexpr auto FlowToMvConstants = [] [[nodiscard]] (const SessionPlan& plan, float scale) noexcept -> Constants {
                        return Consts({ plan.source.width.Get(), plan.source.height.Get(), plan.flowExtent.width.Get(), plan.flowExtent.height.Get(), GridCells(plan.nvofGrid), Bits(scale), 0u, 0u },
                                      8);
                    };
                    return MoveTo(b, SimpleId(ResourceKind::OpticalFlowOutput), ResourceState::ShaderRead)
                        .and_then([](const Builder& n) { return MoveTo(n, SimpleId(ResourceKind::MotionVectors), ResourceState::UnorderedAccess); })
                        .and_then([&](const Builder& n) {
                            return EmitDispatch(n, PassId::FlowToMv, Bind(SimpleId(ResourceKind::OpticalFlowOutput), std::nullopt, std::nullopt, SimpleId(ResourceKind::MotionVectors), std::nullopt),
                                                FlowToMvConstants(plan, hasPrevious ? 1.0f : 0.0f), plan.source);
                        })
                        .and_then([](const Builder& n) { return MoveTo(n, SimpleId(ResourceKind::MotionVectors), ResourceState::ShaderRead); })
                        .and_then([](const Builder& n) { return MoveTo(n, SimpleId(ResourceKind::OpticalFlowOutput), ResourceState::Common); });
                };

                static constexpr auto ZeroMotionPhaseTwo = [] [[nodiscard]] (const Builder& b, const SessionPlan& plan, bool written) noexcept -> BuildResult {
                    if (written)
                        return b;
                    return MoveTo(b, SimpleId(ResourceKind::MotionVectors), ResourceState::UnorderedAccess)
                        .and_then([&](const Builder& n) {
                            return EmitDispatch(n, PassId::Finalize, Bind(std::nullopt, std::nullopt, std::nullopt, SimpleId(ResourceKind::MotionVectors), std::nullopt),
                                                FinalizeConstants(plan.source, plan.source, 0.0f), plan.source);
                        })
                        .and_then([](const Builder& n) { return MoveTo(n, SimpleId(ResourceKind::MotionVectors), ResourceState::ShaderRead); });
                };
                switch (plan.motion)
                {
                case MotionBackend::BuiltIn: return BlockMatchPhaseTwo(b, plan, extents, state.currentSet, slot);
                case MotionBackend::NvOpticalFlow: return OpticalFlowPhaseTwo(b, plan, state.hasPrevious);
                case MotionBackend::None: return ZeroMotionPhaseTwo(b, plan, state.zeroMotionWritten);
                }
                return b;
            };

            static constexpr auto ModelIoOf = [] [[nodiscard]] (ResourceKind color, ResourceKind output) noexcept -> ModelIo {
                return ModelIo{ SimpleId(color), SimpleId(ResourceKind::Depth), SimpleId(ResourceKind::MotionVectors), SimpleId(output) };
            };

            static constexpr auto SuperResolutionSteps = [] [[nodiscard]] (const Builder& b, const SessionPlan& plan, bool reset) noexcept -> BuildResult {
                static constexpr auto SrStep = [] [[nodiscard]] (const SessionPlan& plan, bool reset) noexcept -> EvaluateSr {
                    return EvaluateSr{ ModelIoOf(ResourceKind::ModelColor, ResourceKind::SrOutput), plan.source, reset };
                };
                if (!plan.superResolution.has_value())
                    return b;
                return MoveTo(b, SimpleId(ResourceKind::SrOutput), ResourceState::UnorderedAccess)
                    .and_then([&](const Builder& n) { return Emit(n, Step{ SrStep(plan, reset) }); })
                    .and_then([](const Builder& n) { return MoveTo(n, SimpleId(ResourceKind::SrOutput), ResourceState::ShaderRead); });
            };

            // The model runs once, or as many times as asked, each pass on the picture the one before it made: the
            // first reads the picture, the last writes the output that is shown, and the ones between hand their work
            // on through two intermediates that the executor keeps as it finds them, so the plan is one step long.
            static constexpr auto NeuralRenderingSteps = [] [[nodiscard]] (const Builder& b, const SessionPlan& plan, const LiveSettings& controls, bool reset) noexcept -> BuildResult {
                static constexpr auto NrStep = [] [[nodiscard]] (const SessionPlan& plan, const LiveSettings& live, bool reset) noexcept -> EvaluateNr {
                    return EvaluateNr{
                        ModelIoOf(ColorSourceOf(plan), ResourceKind::NrOutput), plan.work, plan.source, live.mvScaleX, live.mvScaleY, reset, live.depthInverted, live.tuning, live.passes
                    };
                };
                if (!controls.neuralRendering)
                    return b;
                return MoveTo(b, SimpleId(ResourceKind::NrOutput), ResourceState::UnorderedAccess)
                    .and_then([&](const Builder& n) { return Emit(n, Step{ NrStep(plan, controls, reset) }); })
                    .and_then([](const Builder& n) { return MoveTo(n, SimpleId(ResourceKind::NrOutput), ResourceState::ShaderRead); });
            };

            static constexpr auto NeedsReset = [] [[nodiscard]] (const FrameState& state, Instant now) noexcept -> bool {
                static constexpr auto IsHistoryStale = [] [[nodiscard]] (const FrameState& state, Instant now) noexcept -> bool { return state.resetPending || IsLongPause(state, now); };
                return !state.hasPrevious || IsHistoryStale(state, now);
            };
            const bool reset = NeedsReset(state, input.now);
            const LiveSettings controls = NextControls(state.controls, input.controlRequest);
            return PhaseOne(Builder{ StepList{}, state.states }, plan, extents, state.currentSet)
                .and_then([&](const Builder& n) { return MotionPhaseTwo(n, plan, extents, state, slot); })
                .and_then([&](const Builder& n) { return SuperResolutionSteps(n, plan, reset); })
                .and_then([&](const Builder& n) { return NeuralRenderingSteps(n, plan, controls, reset); })
                .and_then([&](const Builder& n) { return BlitSteps(n, true, DisplaySourceOf(plan, controls.neuralRendering), DisplayOf(state, input), input.backBuffer, controls.vsync); });
        };

        static constexpr auto RepeatSteps = [] [[nodiscard]] (const SessionPlan& plan, const FrameState& state, const FrameInput& input) noexcept -> BuildResult {
            const LiveSettings live = NextControls(state.controls, input.controlRequest);
            return BlitSteps(Builder{ StepList{}, state.states }, state.hasOutput, DisplaySourceOf(plan, live.neuralRendering), DisplayOf(state, input), input.backBuffer, live.vsync);
        };
        return ProcessesFrame(state, input) ? FreshSteps(plan, state, input, extents, slot) : RepeatSteps(plan, state, input);
    };
    const FrameSlot slot = SlotOfFrame(state.number);
    return LevelExtentsOf(plan.source, plan.levels).transform_error(FromPyramid).and_then([&](const LevelExtents& extents) {
        return StepsFor(plan, state, input, extents, slot).transform([&](const Builder& built) { return FramePlan{ built.steps, NextState(plan, state, input, built.states, slot), input.quit }; });
    });
}

std::string_view Describe(PlanFrameError error) noexcept
{
    switch (error)
    {
    case PlanFrameError::Capacity: return "frame plan exceeded its step capacity";
    case PlanFrameError::Arithmetic: return "arithmetic overflow while planning a frame";
    case PlanFrameError::Unit: return "invalid level or slot while planning a frame";
    }
    return "frame planning error";
}

} // namespace interior
