#include "interior/units.h"

#include <algorithm>
#include <array>
#include <cstddef>
#include <ranges>

namespace interior {
namespace {

constexpr std::array<std::size_t, 4> kDashPositions{ 8, 13, 18, 23 };

[[nodiscard]] bool IsGuidShaped(std::string_view text) noexcept
{
    static constexpr auto IsValidGuidChar = [] [[nodiscard]] (char c, std::size_t index) noexcept -> bool {
        static constexpr auto IsHexDigit = [] [[nodiscard]] (char c) noexcept -> bool {
            static constexpr auto IsDecimalDigit = [] [[nodiscard]] (char c) noexcept -> bool { return c >= '0' && c <= '9'; };

            static constexpr auto IsLowerHexLetter = [] [[nodiscard]] (char c) noexcept -> bool { return c >= 'a' && c <= 'f'; };
            return IsDecimalDigit(c) || IsLowerHexLetter(c);
        };

        static constexpr auto IsDashPosition = [] [[nodiscard]] (std::size_t index) noexcept -> bool { return std::ranges::find(kDashPositions, index) != kDashPositions.end(); };
        return IsDashPosition(index) ? c == '-' : IsHexDigit(c);
    };
    return std::ranges::all_of(std::views::iota(std::size_t{ 0 }, text.size()), [text](std::size_t i) { return IsValidGuidChar(text[i], i); });
}

} // namespace

Result<ProjectIdText, UnitError> ParseProjectId(std::string_view raw) noexcept
{
    static constexpr auto IsGuid = [] [[nodiscard]] (std::string_view text) noexcept -> bool {
        static constexpr auto IsGuidLength = [] [[nodiscard]] (std::string_view text) noexcept -> bool { return text.size() == ProjectIdText::Capacity; };
        return IsGuidLength(text) && IsGuidShaped(text);
    };
    if (!IsGuid(raw))
        return infra::Fail(UnitError::ProjectIdMalformed);
    return ProjectIdText::Parse(raw).transform_error([](infra::StringTooLong) { return UnitError::ProjectIdMalformed; });
}

} // namespace interior
