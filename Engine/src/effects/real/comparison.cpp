#include "effects/real/comparison.h"

namespace real {
namespace {

using infra::Result;

// Every picture of a comparison carries every parameter in its name, so the ones swept can be told apart
// whatever their values.
[[nodiscard]] SnapshotOrder OrderFor(const Comparison& c, const interior::LiveSettings& live, Pictures pictures) noexcept
{
    return SnapshotOrder{ c.base.folder, c.base.label, live, true, c.base.when, pictures };
}

[[nodiscard]] Comparison WithOriginalSaved(const Comparison& c) noexcept
{
    return Comparison{ c.base, c.spec, c.total, c.done, true, c.cursor, c.started };
}

[[nodiscard]] Comparison WithOneMore(const Comparison& c) noexcept
{
    return Comparison{ c.base, c.spec, c.total, c.done + 1, c.originalSaved, c.cursor, c.started };
}

} // namespace

Result<Comparison, Error> StartComparison(const SnapshotOrder& order, const interior::SweepSpec& spec, const std::optional<CursorOverlay>& cursor, interior::Instant now) noexcept
{
    return EnsureCaptureFolder(order.folder)
        .and_then([&] { return MadeCaptureFolder(order.folder, interior::ComparisonFolderStemOf(order.label, order.when)); })
        .transform([&](const interior::DirectoryPath& folder) {
            return Comparison{ SnapshotOrder{ folder, order.label, order.live, true, order.when, Pictures::Both }, spec, interior::SweepCount(spec), 0, false, cursor, now };
        });
}

std::optional<interior::LiveSettings> ComparisonControls(const Comparison& c) noexcept
{
    if (!c.originalSaved || IsComparisonDone(c))
        return std::nullopt;
    return interior::SweepCombination(c.base.live, c.spec, c.done);
}

Result<Compared, Error> CapturedComparison(const Gpu& gpu, const FrameContext& frame, const interior::FrameState& after, const Comparison& c, PngWriter& writer) noexcept
{
    if (!c.originalSaved)
        return SaveSnapshot(gpu, frame, after, OrderFor(c, c.base.live, Pictures::Original), c.cursor, writer).transform([&](const Snapshot& s) { return Compared{ WithOriginalSaved(c), s.fence }; });
    if (IsComparisonDone(c))
        return Compared{ c, frame.fence };
    const interior::LiveSettings live = interior::SweepCombination(c.base.live, c.spec, c.done);
    return SaveSnapshot(gpu, frame, after, OrderFor(c, live, Pictures::Processed), c.cursor, writer).transform([&](const Snapshot& s) { return Compared{ WithOneMore(c), s.fence }; });
}

bool IsComparisonDone(const Comparison& c) noexcept
{
    return c.originalSaved && c.done >= c.total;
}

interior::SweepProgress ProgressOf(const Comparison& c) noexcept
{
    return interior::SweepProgress{ c.done, c.total };
}

} // namespace real
