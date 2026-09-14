#pragma once
#include "effects/real/snapshot.h"
#include "interior/sweep.h"

#include <optional>

namespace real {

// A comparison capture under way: one frame's original, then the model's picture for every combination of
// the settings swept, all from that one frame, in a folder of their own.
struct Comparison
{
    SnapshotOrder base; // the folder made for it, the source, the settings it starts from and the minute
    interior::SweepSpec spec;
    std::uint32_t total;
    std::uint32_t done; // combinations captured so far
    bool originalSaved;
    std::optional<CursorOverlay> cursor; // where the cursor was when the frame was taken, drawn into every picture
    interior::Instant started;
};

// A comparison after a picture was saved, and the fence the copies were waited for at.
struct Compared
{
    Comparison comparison;
    interior::FenceValue fence;
};

// Makes the folder, named for the source and the minute with a count when that name is taken. Nothing is
// captured until the frame the button was clicked on has been submitted.
[[nodiscard]] infra::Result<Comparison, Error> StartComparison(const SnapshotOrder& order, const interior::SweepSpec& spec, const std::optional<CursorOverlay>& cursor, interior::Instant now) noexcept;

// The settings the next frame runs with: the next combination once the original is saved, and nothing before
// that, when the frame runs as the panel says.
[[nodiscard]] std::optional<interior::LiveSettings> ComparisonControls(const Comparison& comparison) noexcept;

// Saves what the frame just submitted holds: the original the first time, then the picture the frame's
// combination made, named for its settings.
[[nodiscard]] infra::Result<Compared, Error> CapturedComparison(const Gpu& gpu, const FrameContext& frame, const interior::FrameState& after, const Comparison& comparison, PngWriter& writer) noexcept;

[[nodiscard]] bool IsComparisonDone(const Comparison& comparison) noexcept;
[[nodiscard]] interior::SweepProgress ProgressOf(const Comparison& comparison) noexcept;

} // namespace real
