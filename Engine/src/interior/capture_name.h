#pragma once
#include "infrastructure/bounded_string.h"
#include "interior/options.h"

#include <cstdint>
#include <string_view>

namespace interior {

// What a capture's file is named from, apart from the settings: what was captured, and when.
using CaptureLabel = infra::BoundedString<char, 40>;
using CaptureStem = infra::BoundedString<char, 200>;

struct CaptureMoment
{
    std::uint32_t month;
    std::uint32_t day;
    std::uint32_t hour;
    std::uint32_t minute;
    [[nodiscard]] friend constexpr bool operator==(const CaptureMoment&, const CaptureMoment&) noexcept = default;
};

constexpr std::string_view kDesktopLabel = "Desktop";
constexpr std::string_view kWindowLabel = "Window";

// A window's title reduced to what a file name may hold: its ASCII letters and digits, as many as fit, or
// "Window" when none are left.
[[nodiscard]] CaptureLabel CaptureLabelOf(std::wstring_view title) noexcept;

// The file name a capture is saved under, before the kind of picture and the extension: the label, the
// settings that shaped the picture, and the moment. `everything` adds the values usually left off for
// being at their defaults: the mask when it is on, the intensity when it is full, the passes when there
// is one.
[[nodiscard]] CaptureStem CaptureStemOf(const CaptureLabel& label, const LiveSettings& live, bool everything, const CaptureMoment& when) noexcept;

// The same name with a count on the end, for when the first is taken. The first attempt is the name itself.
[[nodiscard]] CaptureStem NumberedStem(const CaptureStem& stem, std::uint32_t attempt) noexcept;

// A name with the source and the minute alone, for a picture no setting shaped.
[[nodiscard]] CaptureStem PlainStemOf(const CaptureLabel& label, const CaptureMoment& when) noexcept;

// The folder a comparison capture's pictures go into, named for the source and the minute.
[[nodiscard]] CaptureStem ComparisonFolderStemOf(const CaptureLabel& label, const CaptureMoment& when) noexcept;

// Where captures go unless the operator says otherwise: a Captures folder next to the executable.
[[nodiscard]] DirectoryPath DefaultCaptureFolder(const DirectoryPath& executableDirectory) noexcept;

} // namespace interior
