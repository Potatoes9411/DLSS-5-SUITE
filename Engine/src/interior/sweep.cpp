#include "interior/sweep.h"

#include "infrastructure/array_util.h"

#include <algorithm>
#include <ranges>

namespace interior {
namespace {

// Each setting's digit of the index, in as many values as the setting runs through.
using Digits = std::array<std::uint32_t, kSweepParameterCount>;

struct Counting
{
    Digits digits;
    std::uint32_t rest;
};

[[nodiscard]] bool IsSwept(const SweepSpec& spec, SweepParameter parameter) noexcept
{
    return spec[static_cast<std::size_t>(parameter)].on;
}

[[nodiscard]] std::uint32_t DigitOf(const Digits& digits, SweepParameter parameter) noexcept
{
    return digits[static_cast<std::size_t>(parameter)];
}

[[nodiscard]] Digits DigitsOf(const SweepSpec& spec, std::uint32_t index) noexcept
{
    static constexpr auto Taken = [] [[nodiscard]] (const Counting& so, std::size_t p, const SweepSpec& spec) noexcept -> Counting {
        const std::uint32_t values = SweepValuesOf(spec, static_cast<SweepParameter>(p));
        return Counting{ infra::WithElement(so.digits, p, so.rest % values), so.rest / values };
    };
    const auto parameters = std::views::iota(std::size_t{ 0 }, kSweepParameterCount);
    return std::ranges::fold_left(parameters, Counting{ Digits{}, index }, [&spec](const Counting& so, std::size_t p) { return Taken(so, p, spec); }).digits;
}

// How far along its range a value stands: the first is the bottom and the last is the top.
[[nodiscard]] float FractionOf(std::uint32_t digit, std::uint32_t values) noexcept
{
    if (values <= 1)
        return 1.0f;
    return static_cast<float>(digit) / static_cast<float>(values - 1);
}

// Where a swept setting stands: how far along its range this combination puts it.
[[nodiscard]] float SweptTo(float top, const SweepSpec& spec, const Digits& digits, SweepParameter parameter) noexcept
{
    return top * FractionOf(DigitOf(digits, parameter), SweepValuesOf(spec, parameter));
}

[[nodiscard]] NrStyle StyleAt(std::uint32_t digit) noexcept
{
    constexpr std::array<NrStyle, kStyleValues> styles{ NrStyle::Standard, NrStyle::Natural, NrStyle::Cinematic };
    return styles[std::min<std::size_t>(digit, styles.size() - 1)];
}

// Skin following local structure is the model's -1, so what skin runs up to is then what local structure has.
[[nodiscard]] float SkinTop(const NrTuning& t) noexcept
{
    if (t.skinStructure.Get() < 0.0f)
        return t.localStructure.Get();
    return t.skinStructure.Get();
}

// WAIVER(R2): a copy of the base with each swept setting written over once; a setting not swept is never
// touched, so it stays exactly what the base holds.
[[nodiscard]] NrTuning TuningOf(const NrTuning& base, const SweepSpec& spec, const Digits& digits) noexcept
{
    NrTuning tuning = base;
    if (IsSwept(spec, SweepParameter::Intensity))
        tuning.intensity = NrIntensityTag::Parse(SweptTo(base.intensity.Get(), spec, digits, SweepParameter::Intensity)).value_or(base.intensity);
    if (IsSwept(spec, SweepParameter::LocalStructure))
        tuning.localStructure = StrengthTag::Parse(SweptTo(base.localStructure.Get(), spec, digits, SweepParameter::LocalStructure)).value_or(base.localStructure);
    if (IsSwept(spec, SweepParameter::LocalTone))
        tuning.localTone = StrengthTag::Parse(SweptTo(base.localTone.Get(), spec, digits, SweepParameter::LocalTone)).value_or(base.localTone);
    if (IsSwept(spec, SweepParameter::SkinStructure))
        tuning.skinStructure = SkinStrengthTag::Parse(SweptTo(SkinTop(base), spec, digits, SweepParameter::SkinStructure)).value_or(base.skinStructure);
    if (IsSwept(spec, SweepParameter::Style))
        tuning.style = StyleAt(DigitOf(digits, SweepParameter::Style));
    if (IsSwept(spec, SweepParameter::AutoMask))
        tuning.autoMask = DigitOf(digits, SweepParameter::AutoMask) == 1;
    return tuning;
}

[[nodiscard]] PassCount PassesOf(const LiveSettings& base, const SweepSpec& spec, const Digits& digits) noexcept
{
    if (!IsSwept(spec, SweepParameter::Passes))
        return base.passes;
    return PassCountTag::Parse(DigitOf(digits, SweepParameter::Passes) + 1).value_or(base.passes);
}

} // namespace

std::uint32_t SweepValuesOf(const SweepSpec& spec, SweepParameter parameter) noexcept
{
    const SweepAxis& axis = spec[static_cast<std::size_t>(parameter)];
    if (!axis.on)
        return 1;
    if (parameter == SweepParameter::Style)
        return kStyleValues;
    if (parameter == SweepParameter::AutoMask)
        return kMaskValues;
    return std::clamp(axis.values, kMinSweepValues, kMaxSweepValues);
}

std::uint32_t SweepCount(const SweepSpec& spec) noexcept
{
    // Fifty values on every counted setting, times the three styles and the two mask states, still fits the count.
    static_assert(kMaxSweepValues * kMaxSweepValues * kMaxSweepValues * kMaxSweepValues * kMaxSweepValues * static_cast<std::uint64_t>(kStyleValues) * kMaskValues <= UINT32_MAX);
    if (std::ranges::none_of(spec, [](const SweepAxis& axis) { return axis.on; }))
        return 0;
    const auto parameters = std::views::iota(std::size_t{ 0 }, kSweepParameterCount);
    return std::ranges::fold_left(parameters, std::uint32_t{ 1 }, [&spec](std::uint32_t so, std::size_t p) { return so * SweepValuesOf(spec, static_cast<SweepParameter>(p)); });
}

// WAIVER(R2): a copy of the base with the model switched on, its tuning replaced and its passes written over
// when they are swept; everything else stays what the base holds.
LiveSettings SweepCombination(const LiveSettings& base, const SweepSpec& spec, std::uint32_t index) noexcept
{
    const Digits digits = DigitsOf(spec, index);
    LiveSettings live = base;
    live.neuralRendering = true;
    live.tuning = TuningOf(base.tuning, spec, digits);
    live.passes = PassesOf(base, spec, digits);
    return live;
}

} // namespace interior
