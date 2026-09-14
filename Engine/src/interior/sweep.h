#pragma once
#include "interior/options.h"

#include <array>
#include <cstddef>
#include <cstdint>

namespace interior {

// The settings a comparison capture can run through: the model's tunings that a capture's name tells apart.
enum class SweepParameter : std::uint8_t { Intensity, LocalStructure, LocalTone, SkinStructure, Style, AutoMask, Passes, Count };

constexpr std::size_t kSweepParameterCount = static_cast<std::size_t>(SweepParameter::Count);

// A strength or the intensity runs from nothing up to the value it has now, in as many values as asked
// for; the passes from one up to that many; the style through its three, and the mask through off and on.
constexpr std::uint32_t kMinSweepValues = 2;
constexpr std::uint32_t kMaxSweepValues = 50;
constexpr std::uint32_t kStyleValues = 3;
constexpr std::uint32_t kMaskValues = 2;

struct SweepAxis
{
    bool on;
    std::uint32_t values; // how many, for a setting that takes a count; the style and the mask have their own
    [[nodiscard]] friend constexpr bool operator==(const SweepAxis&, const SweepAxis&) noexcept = default;
};

using SweepSpec = std::array<SweepAxis, kSweepParameterCount>;

// How far a comparison capture has got: pictures taken, and pictures in all.
struct SweepProgress
{
    std::uint32_t done;
    std::uint32_t total;
    [[nodiscard]] friend constexpr bool operator==(const SweepProgress&, const SweepProgress&) noexcept = default;
};

// How many values one setting runs through: one when it is not swept.
[[nodiscard]] std::uint32_t SweepValuesOf(const SweepSpec& spec, SweepParameter parameter) noexcept;

// How many combinations there are, which is how many pictures: none when nothing is swept.
[[nodiscard]] std::uint32_t SweepCount(const SweepSpec& spec) noexcept;

// The settings of one combination, counted from zero, the first setting in the table changing fastest. A
// setting not swept keeps what `base` holds. The model is run whatever `base` says, since a picture without
// it compares nothing.
[[nodiscard]] LiveSettings SweepCombination(const LiveSettings& base, const SweepSpec& spec, std::uint32_t index) noexcept;

} // namespace interior
