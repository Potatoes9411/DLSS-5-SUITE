#pragma once
#include "effects/real/capture.h"
#include "effects/real/ngx.h"
#include "effects/real/pipelines.h"
#include "effects/real/presenter.h"
#include "effects/real/resources.h"
#include "interior/frame.h"
#include "interior/plan.h"

#include <array>
#include <optional>
#include <vector>

#if DSCREEN_HAVE_NVOF
#include "effects/real/nvof.h"
#endif

namespace real {

// FEATURE-FLAG DSCREEN_HAVE_NVOF: the only build-time flag; both settings are built by the gate.
#if DSCREEN_HAVE_NVOF
using OpticalFlowSlot = std::optional<OpticalFlow>;
#else
struct OpticalFlowSlot
{
};
#endif

// What the neural rendering instances were built with. The model reads its tuning when a feature is
// built, not at evaluate, and the count decides how many there are, so a change to either means a rebuild.
struct BuiltModel
{
    interior::NrTuning tuning;
    interior::PassCount passes;
    [[nodiscard]] friend constexpr bool operator==(const BuiltModel&, const BuiltModel&) noexcept = default;
};

// One instance of the model per pass, each with a history of its own, as many as the operator asks for: the
// GPU's memory is the bound, and an instance it cannot hold fails to build and says so.
using Passes = std::vector<Feature>;

struct Models
{
    std::optional<NgxRuntime> runtime;
    std::optional<Feature> superResolution;
    Passes neuralRendering;
    std::optional<BuiltModel> builtWith;
};

using Allocators = std::array<Com<ID3D12CommandAllocator>, interior::kFrameSlotCount>;

struct Gpu
{
    GpuDevice device;
    Pipelines pipelines;
    Presenter presenter;
    Capture capture;
    Allocators allocators;
    Com<ID3D12GraphicsCommandList> list;
    ResourceTable resources;
    Models models;
    OpticalFlowSlot opticalFlow;
};

struct FrameContext
{
    interior::FrameNumber number;
    interior::FrameSlot slot;
    interior::SetIndex set;
    bool hasPrevious;
    interior::FenceValue fence;
};

[[nodiscard]] infra::Status<Error> OpenList(const Gpu& gpu, interior::FrameSlot slot) noexcept;
[[nodiscard]] infra::Result<interior::FenceValue, Error> FlushList(const Gpu& gpu, interior::FenceValue previous) noexcept;
[[nodiscard]] infra::Result<interior::FenceValue, Error> ExecuteSteps(const Gpu& gpu, const FrameContext& frame, const interior::StepList& steps) noexcept;

} // namespace real
