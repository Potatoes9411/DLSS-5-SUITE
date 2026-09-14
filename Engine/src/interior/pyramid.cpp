#include "interior/pyramid.h"

#include "infrastructure/checked.h"
#include "infrastructure/fold.h"
#include "interior/enums.h"

#include <algorithm>
#include <ranges>

namespace interior {
namespace {

using infra::Fail;

[[nodiscard]] std::uint32_t AtLeastOne(std::uint32_t value) noexcept
{
    return std::max(value, std::uint32_t{ 1 });
}

[[nodiscard]] bool IsAtLeastMinimum(std::uint32_t value, std::uint32_t level) noexcept
{
    return (value >> level) >= kMinLevelSize;
}

[[nodiscard]] Result<std::uint32_t, PyramidError> CeilDiv(std::uint32_t value, std::uint32_t divisor) noexcept
{
    return infra::CheckedAdd(value, divisor - 1).and_then([divisor](std::uint32_t padded) { return infra::CheckedDiv(padded, divisor); }).transform_error([](infra::ArithmeticError) {
        return PyramidError::Arithmetic;
    });
}

[[nodiscard]] PyramidError FromUnit(UnitError) noexcept
{
    return PyramidError::Unit;
}

} // namespace

Extent Halved(const Extent& extent) noexcept
{
    static constexpr auto HalvedCount = [] [[nodiscard]] (PixelCount count) noexcept -> PixelCount {
        const Result<PixelCount, UnitError> halved = PixelCountTag::Parse(AtLeastOne(count.Get() >> 1));
        ENSURE(halved.has_value());
        return *halved;
    };
    return Extent{ HalvedCount(extent.width), HalvedCount(extent.height) };
}

bool IsLevelUsable(const Extent& source, std::uint32_t level) noexcept
{
    return IsAtLeastMinimum(source.width.Get(), level) && IsAtLeastMinimum(source.height.Get(), level);
}

LevelCount LevelCountFor(const Extent& source) noexcept
{
    const auto usable = std::ranges::count_if(std::views::iota(std::uint32_t{ 0 }, kCoarsestLevel + 1), [&source](std::uint32_t level) { return IsLevelUsable(source, level); });
    const Result<LevelCount, UnitError> count = LevelCountTag::Parse(AtLeastOne(static_cast<std::uint32_t>(usable)));
    ENSURE(count.has_value());
    return *count;
}

Result<LevelExtents, PyramidError> LevelExtentsOf(const Extent& source, LevelCount levels) noexcept
{
    static constexpr auto AppendHalved = [] [[nodiscard]] (const LevelExtents& acc, std::uint32_t) noexcept -> Result<LevelExtents, PyramidError> {
        return acc.Push(Halved(acc.Last())).transform_error([](infra::CapacityExceeded) { return PyramidError::Capacity; });
    };
    return LevelExtents{}.Push(source).transform_error([](infra::CapacityExceeded) { return PyramidError::Capacity; }).and_then([levels](const LevelExtents& first) {
        return infra::FoldResult(std::views::iota(std::uint32_t{ 1 }, levels.Get()), Result<LevelExtents, PyramidError>(first), AppendHalved);
    });
}

Result<ThreadGroups, PyramidError> GroupsFor(const Extent& extent) noexcept
{
    static constexpr auto GroupsAlong = [] [[nodiscard]] (PixelCount count) noexcept -> Result<GroupCount, PyramidError> {
        return CeilDiv(count.Get(), kGroupSize).and_then([](std::uint32_t groups) { return GroupCountTag::Parse(groups).transform_error(FromUnit); });
    };
    return GroupsAlong(extent.width).and_then([&extent](GroupCount x) { return GroupsAlong(extent.height).transform([x](GroupCount y) { return ThreadGroups{ x, y }; }); });
}

Result<Extent, PyramidError> GridExtent(const Extent& source, std::uint32_t grid) noexcept
{
    static constexpr auto CellsAlong = [] [[nodiscard]] (PixelCount count, std::uint32_t grid) noexcept -> Result<PixelCount, PyramidError> {
        return CeilDiv(count.Get(), grid).and_then([](std::uint32_t cells) { return PixelCountTag::Parse(cells).transform_error(FromUnit); });
    };
    return CellsAlong(source.width, grid).and_then([&source, grid](PixelCount width) {
        return CellsAlong(source.height, grid).transform([width](PixelCount height) { return Extent{ width, height }; });
    });
}

std::uint32_t GridCells(GridSize grid) noexcept
{
    switch (grid)
    {
    case GridSize::One: return 1;
    case GridSize::Two: return 2;
    case GridSize::Four: return 4;
    }
    return 1;
}

} // namespace interior
