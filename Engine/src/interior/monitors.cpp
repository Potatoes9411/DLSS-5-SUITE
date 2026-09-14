#include "interior/monitors.h"

#include "infrastructure/checked.h"
#include "infrastructure/fold.h"

#include <algorithm>
#include <ranges>

namespace interior {
namespace {

using infra::Fail;

[[nodiscard]] bool IsHandleBefore(const MonitorInfo& a, const MonitorInfo& b) noexcept
{
    return a.handle < b.handle;
}

[[nodiscard]] bool IsTopOrHandleBefore(const MonitorInfo& a, const MonitorInfo& b) noexcept
{
    static constexpr auto IsTopBefore = [] [[nodiscard]] (const MonitorInfo& a, const MonitorInfo& b) noexcept -> bool { return a.rect.Top() < b.rect.Top(); };

    static constexpr auto IsTopTiedAndHandleBefore = [] [[nodiscard]] (const MonitorInfo& a, const MonitorInfo& b) noexcept -> bool {
        static constexpr auto IsTopEqual = [] [[nodiscard]] (const MonitorInfo& a, const MonitorInfo& b) noexcept -> bool { return a.rect.Top() == b.rect.Top(); };
        return IsTopEqual(a, b) && IsHandleBefore(a, b);
    };
    return IsTopBefore(a, b) || IsTopTiedAndHandleBefore(a, b);
}

[[nodiscard]] bool IsLeftOrLaterBefore(const MonitorInfo& a, const MonitorInfo& b) noexcept
{
    static constexpr auto IsLeftBefore = [] [[nodiscard]] (const MonitorInfo& a, const MonitorInfo& b) noexcept -> bool { return a.rect.Left() < b.rect.Left(); };

    static constexpr auto IsLeftTiedAndLaterBefore = [] [[nodiscard]] (const MonitorInfo& a, const MonitorInfo& b) noexcept -> bool {
        static constexpr auto IsLeftEqual = [] [[nodiscard]] (const MonitorInfo& a, const MonitorInfo& b) noexcept -> bool { return a.rect.Left() == b.rect.Left(); };
        return IsLeftEqual(a, b) && IsTopOrHandleBefore(a, b);
    };
    return IsLeftBefore(a, b) || IsLeftTiedAndLaterBefore(a, b);
}

[[nodiscard]] MonitorError AsMonitorError(UnitError) noexcept
{
    return MonitorError::EmptyArea;
}

[[nodiscard]] MonitorError AsMonitorError(infra::CapacityExceeded) noexcept
{
    return MonitorError::TooManyMonitors;
}

[[nodiscard]] MonitorError AsMonitorError(infra::ArithmeticError) noexcept
{
    return MonitorError::EmptyArea;
}

[[nodiscard]] Result<MonitorList, MonitorError> IndexedOf(const MonitorList& ordered, RequestedMonitor requested) noexcept
{
    if (!ordered.HasIndex(requested.Get()))
        return Fail(MonitorError::IndexOutOfRange);
    return MonitorList{}.Push(ordered.At(requested.Get())).transform_error([](infra::CapacityExceeded e) { return AsMonitorError(e); });
}

} // namespace

bool ComesBefore(const MonitorInfo& a, const MonitorInfo& b) noexcept
{
    static constexpr auto IsPrimaryFirst = [] [[nodiscard]] (const MonitorInfo& a, const MonitorInfo& b) noexcept -> bool { return a.primary && !b.primary; };

    static constexpr auto IsPrimaryTiedAndBefore = [] [[nodiscard]] (const MonitorInfo& a, const MonitorInfo& b) noexcept -> bool {
        static constexpr auto IsPrimaryEqual = [] [[nodiscard]] (const MonitorInfo& a, const MonitorInfo& b) noexcept -> bool { return a.primary == b.primary; };
        return IsPrimaryEqual(a, b) && IsLeftOrLaterBefore(a, b);
    };
    return IsPrimaryFirst(a, b) || IsPrimaryTiedAndBefore(a, b);
}

MonitorList Ordered(const MonitorList& monitors) noexcept
{
    static constexpr auto AppendRanked = [] [[nodiscard]] (const MonitorList& monitors, const MonitorList& acc, std::size_t rank) noexcept -> Result<MonitorList, infra::CapacityExceeded> {
        static constexpr auto HasRank = [] [[nodiscard]] (const MonitorList& monitors, const MonitorInfo& monitor, std::size_t rank) noexcept -> bool {
            static constexpr auto RankOf = [] [[nodiscard]] (const MonitorList& monitors, const MonitorInfo& monitor) noexcept -> std::size_t {
                return static_cast<std::size_t>(std::ranges::count_if(monitors.Items(), [&monitor](const MonitorInfo& other) { return ComesBefore(other, monitor); }));
            };
            return RankOf(monitors, monitor) == rank;
        };
        const auto items = monitors.Items();
        const auto found = std::ranges::find_if(items, [&](const MonitorInfo& m) { return HasRank(monitors, m, rank); });
        if (found == items.end())
            return acc;
        return acc.Push(*found);
    };
    const Result<MonitorList, infra::CapacityExceeded> ordered = infra::FoldResult(std::views::iota(std::size_t{ 0 }, monitors.Size()), Result<MonitorList, infra::CapacityExceeded>(MonitorList{}),
                                                                                   [&monitors](const MonitorList& acc, std::size_t rank) { return AppendRanked(monitors, acc, rank); });
    ENSURE(ordered.has_value());
    return *ordered;
}

Result<ScreenRect, MonitorError> UnionRect(const MonitorList& monitors) noexcept
{
    static constexpr auto UnionWith = [] [[nodiscard]] (const ScreenRect& acc, const MonitorInfo& monitor) noexcept -> Result<ScreenRect, UnitError> {
        static constexpr auto Union = [] [[nodiscard]] (const ScreenRect& a, const ScreenRect& b) noexcept -> Result<ScreenRect, UnitError> {
            static constexpr auto Min = [] [[nodiscard]] (Coordinate a, Coordinate b) noexcept -> Coordinate { return a < b ? a : b; };

            static constexpr auto Max = [] [[nodiscard]] (Coordinate a, Coordinate b) noexcept -> Coordinate { return a > b ? a : b; };
            return ScreenRectTag::Parse(Min(a.Left(), b.Left()), Min(a.Top(), b.Top()), Max(a.Right(), b.Right()), Max(a.Bottom(), b.Bottom()));
        };
        return Union(acc, monitor.rect);
    };
    if (monitors.IsEmpty())
        return Fail(MonitorError::NoMonitors);
    return infra::FoldResult(monitors.Items(), Result<ScreenRect, UnitError>(monitors.At(0).rect), UnionWith).transform_error([](UnitError e) { return AsMonitorError(e); });
}

Result<Extent, MonitorError> ExtentOf(const ScreenRect& rect) noexcept
{
    static constexpr auto Span = [] [[nodiscard]] (Coordinate low, Coordinate high) noexcept -> Result<PixelCount, MonitorError> {
        return infra::CheckedSub(high.Get(), low.Get()).transform_error([](infra::ArithmeticError e) { return AsMonitorError(e); }).and_then([](std::int32_t length) {
            return PixelCountTag::Parse(static_cast<std::uint32_t>(length)).transform_error([](UnitError e) { return AsMonitorError(e); });
        });
    };
    return Span(rect.Left(), rect.Right()).and_then([&rect](PixelCount width) { return Span(rect.Top(), rect.Bottom()).transform([width](PixelCount height) { return Extent{ width, height }; }); });
}

Result<MonitorList, MonitorError> SelectSource(const MonitorList& ordered, const SourceSelection& selection) noexcept
{
    static constexpr auto SelectFrom = [] [[nodiscard]] (const MonitorList& ordered, const SourceSelection& selection) noexcept -> Result<MonitorList, MonitorError> {
        static constexpr auto PrimaryOf = [] [[nodiscard]] (const MonitorList& ordered) noexcept -> Result<MonitorList, MonitorError> {
            return MonitorList{}.Push(ordered.At(0)).transform_error([](infra::CapacityExceeded e) { return AsMonitorError(e); });
        };
        switch (selection.kind)
        {
        case MonitorSelectionKind::Primary: return PrimaryOf(ordered);
        case MonitorSelectionKind::All: return ordered;
        case MonitorSelectionKind::Index: return IndexedOf(ordered, selection.index);
        }
        return Fail(MonitorError::NoMonitors);
    };
    if (ordered.IsEmpty())
        return Fail(MonitorError::NoMonitors);
    return SelectFrom(ordered, selection);
}

Result<Geometry, MonitorError> ResolveGeometry(const MonitorList& monitors, const Options& options) noexcept
{
    static constexpr auto GeometryOf = [] [[nodiscard]] (const MonitorList& ordered, const MonitorList& source, std::optional<RequestedMonitor> target) noexcept -> Result<Geometry, MonitorError> {
        static constexpr auto TargetRectOf = [] [[nodiscard]] (const MonitorList& ordered, const ScreenRect& sourceRect,
                                                               std::optional<RequestedMonitor> target) noexcept -> Result<ScreenRect, MonitorError> {
            if (!target.has_value())
                return sourceRect;
            return IndexedOf(ordered, *target).transform([](const MonitorList& list) { return list.At(0).rect; });
        };

        static constexpr auto GeometryFrom = [] [[nodiscard]] (const MonitorList& source, const ScreenRect& sourceRect, const ScreenRect& targetRect) noexcept -> Result<Geometry, MonitorError> {
            return ExtentOf(sourceRect).and_then([&](Extent sourceExtent) {
                return ExtentOf(targetRect).transform([&](Extent targetExtent) { return Geometry{ source, sourceRect, targetRect, sourceExtent, targetExtent }; });
            });
        };
        return UnionRect(source).and_then([&](const ScreenRect& sourceRect) {
            return TargetRectOf(ordered, sourceRect, target).and_then([&](const ScreenRect& targetRect) { return GeometryFrom(source, sourceRect, targetRect); });
        });
    };
    const MonitorList ordered = Ordered(monitors);
    return SelectSource(ordered, options.source).and_then([&](const MonitorList& source) { return GeometryOf(ordered, source, options.target); });
}

bool IsSameRect(const ScreenRect& a, const ScreenRect& b) noexcept
{
    return a == b;
}

std::string_view Describe(MonitorError error) noexcept
{
    switch (error)
    {
    case MonitorError::NoMonitors: return "no monitors found";
    case MonitorError::IndexOutOfRange: return "no monitor has that index (use --list-monitors)";
    case MonitorError::EmptyArea: return "the selected monitors have an empty or oversized area";
    case MonitorError::TooManyMonitors: return "more monitors than supported";
    }
    return "monitor error";
}

} // namespace interior
