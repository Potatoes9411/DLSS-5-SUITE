// WAIVER(R2): test suites accumulate failure counts and drive generated sequences with loops.
#include "interior/sweep.h"
#include "tests/test_registry.h"

#include <algorithm>
#include <array>
#include <cmath>

namespace tests {
namespace {

using namespace interior;

constexpr float kTolerance = 1e-5f;

[[nodiscard]] bool Near(float a, float b) noexcept
{
    return std::fabs(a - b) <= kTolerance * std::max(1.0f, std::fabs(b));
}

[[nodiscard]] SweepAxis AxisOf(const SweepSpec& spec, SweepParameter p) noexcept
{
    return spec[static_cast<std::size_t>(p)];
}

// A base with every strength above nothing, so a swept setting has somewhere to run.
[[nodiscard]] LiveSettings PositiveBase(infra::RngState& rng) noexcept
{
    const LiveSettings d = DefaultLive(DefaultOptions());
    const NrTuning t = d.tuning;
    const float skin = proptest::DrawBool(rng) ? -1.0f : 0.1f + proptest::DrawUnit(rng) * 3.0f;
    const NrTuning tuning{ t.preset,
                           NrIntensityTag::Parse(0.1f + proptest::DrawUnit(rng) * 0.9f).value_or(t.intensity),
                           static_cast<NrStyle>(proptest::DrawBelow(rng, 3)),
                           StrengthTag::Parse(0.1f + proptest::DrawUnit(rng) * 3.0f).value_or(t.localStructure),
                           StrengthTag::Parse(0.1f + proptest::DrawUnit(rng) * 3.0f).value_or(t.localTone),
                           SkinStrengthTag::Parse(skin).value_or(t.skinStructure),
                           proptest::DrawBool(rng),
                           t.uiCorrection };
    return LiveSettings{ proptest::DrawBool(rng), tuning, PassCountTag::Parse(proptest::DrawBetween(rng, 1, 6)).value_or(d.passes), d.depthInverted, d.mvScaleX, d.mvScaleY, d.vsync,
                         d.resetThreshold,        d.depth };
}

// Any spec at all, counts past either end included, so the clamping is exercised.
[[nodiscard]] SweepSpec RandomSpec(infra::RngState& rng) noexcept
{
    SweepSpec spec{};
    for (SweepAxis& axis : spec)
        axis = SweepAxis{ proptest::DrawBool(rng), proptest::DrawBelow(rng, 60) };
    return spec;
}

// A spec small enough to walk every combination of.
[[nodiscard]] SweepSpec SmallSpec(infra::RngState& rng) noexcept
{
    SweepSpec spec{};
    for (SweepAxis& axis : spec)
        axis = SweepAxis{ proptest::DrawBool(rng), proptest::DrawBetween(rng, 2, 4) };
    return spec;
}

// The count a setting runs through, worked out here on its own.
[[nodiscard]] std::uint32_t ExpectedValues(const SweepSpec& spec, SweepParameter p) noexcept
{
    const SweepAxis axis = AxisOf(spec, p);
    if (!axis.on)
        return 1;
    if (p == SweepParameter::Style)
        return 3;
    if (p == SweepParameter::AutoMask)
        return 2;
    return std::clamp(axis.values, 2u, 50u);
}

[[nodiscard]] std::array<std::uint32_t, kSweepParameterCount> ExpectedDigits(const SweepSpec& spec, std::uint32_t index) noexcept
{
    std::array<std::uint32_t, kSweepParameterCount> digits{};
    std::uint32_t rest = index;
    for (std::size_t p = 0; p < kSweepParameterCount; ++p)
    {
        const std::uint32_t values = ExpectedValues(spec, static_cast<SweepParameter>(p));
        digits[p] = rest % values;
        rest /= values;
    }
    return digits;
}

[[nodiscard]] float ExpectedFraction(std::uint32_t digit, std::uint32_t values) noexcept
{
    return values <= 1 ? 1.0f : static_cast<float>(digit) / static_cast<float>(values - 1);
}

[[nodiscard]] float SkinTopOf(const NrTuning& t) noexcept
{
    return t.skinStructure.Get() < 0.0f ? t.localStructure.Get() : t.skinStructure.Get();
}

[[nodiscard]] bool NothingSweptMeansNoPictures(infra::RngState& rng) noexcept
{
    SweepSpec spec = RandomSpec(rng);
    for (SweepAxis& axis : spec)
        axis.on = false;
    const LiveSettings base = PositiveBase(rng);
    const LiveSettings same = SweepCombination(base, spec, proptest::DrawBelow(rng, 10));
    LiveSettings expected = base;
    expected.neuralRendering = true;
    return SweepCount(spec) == 0 && same == expected;
}

[[nodiscard]] bool TheCountIsTheProductOfTheValues(infra::RngState& rng) noexcept
{
    const SweepSpec spec = RandomSpec(rng);
    const bool anyOn = std::ranges::any_of(spec, [](const SweepAxis& a) { return a.on; });
    std::uint32_t expected = anyOn ? 1u : 0u;
    for (std::size_t p = 0; p < kSweepParameterCount; ++p)
    {
        const SweepParameter parameter = static_cast<SweepParameter>(p);
        if (SweepValuesOf(spec, parameter) != ExpectedValues(spec, parameter))
            return false;
        expected *= anyOn ? ExpectedValues(spec, parameter) : 1u;
    }
    return SweepCount(spec) == expected;
}

[[nodiscard]] bool EachSettingChangesInItsOwnDigit(infra::RngState& rng) noexcept
{
    const SweepSpec spec = SmallSpec(rng);
    const LiveSettings base = PositiveBase(rng);
    const std::uint32_t count = SweepCount(spec);
    if (count == 0)
        return true;
    const std::uint32_t index = proptest::DrawBelow(rng, count);
    const std::array<std::uint32_t, kSweepParameterCount> digits = ExpectedDigits(spec, index);
    const LiveSettings got = SweepCombination(base, spec, index);
    const NrTuning& t = got.tuning;
    const NrTuning& b = base.tuning;
    const auto swept = [&](SweepParameter p, float top, float held) {
        const std::size_t i = static_cast<std::size_t>(p);
        return AxisOf(spec, p).on ? top * ExpectedFraction(digits[i], ExpectedValues(spec, p)) : held;
    };
    constexpr std::array<NrStyle, 3> styles{ NrStyle::Standard, NrStyle::Natural, NrStyle::Cinematic };
    const NrStyle style = AxisOf(spec, SweepParameter::Style).on ? styles[digits[static_cast<std::size_t>(SweepParameter::Style)]] : b.style;
    const bool mask = AxisOf(spec, SweepParameter::AutoMask).on ? digits[static_cast<std::size_t>(SweepParameter::AutoMask)] == 1 : b.autoMask;
    const std::uint32_t passes = AxisOf(spec, SweepParameter::Passes).on ? digits[static_cast<std::size_t>(SweepParameter::Passes)] + 1 : base.passes.Get();
    return got.neuralRendering && Near(t.intensity.Get(), swept(SweepParameter::Intensity, b.intensity.Get(), b.intensity.Get())) &&
           Near(t.localStructure.Get(), swept(SweepParameter::LocalStructure, b.localStructure.Get(), b.localStructure.Get())) &&
           Near(t.localTone.Get(), swept(SweepParameter::LocalTone, b.localTone.Get(), b.localTone.Get())) &&
           Near(t.skinStructure.Get(), swept(SweepParameter::SkinStructure, SkinTopOf(b), b.skinStructure.Get())) && t.style == style && t.autoMask == mask && got.passes.Get() == passes &&
           t.preset == b.preset && t.uiCorrection == b.uiCorrection && got.depth == base.depth && got.vsync == base.vsync;
}

[[nodiscard]] bool TheFirstCombinationIsTheBottomAndTheLastTheTop(infra::RngState& rng) noexcept
{
    const SweepSpec spec = SmallSpec(rng);
    const LiveSettings base = PositiveBase(rng);
    const std::uint32_t count = SweepCount(spec);
    if (count == 0)
        return true;
    const LiveSettings first = SweepCombination(base, spec, 0);
    const LiveSettings last = SweepCombination(base, spec, count - 1);
    const auto bottomAndTop = [&](SweepParameter p, float firstGot, float lastGot, float top, float held) {
        if (!AxisOf(spec, p).on)
            return Near(firstGot, held) && Near(lastGot, held);
        return Near(firstGot, 0.0f) && Near(lastGot, top);
    };
    const NrTuning& b = base.tuning;
    const bool strengths = bottomAndTop(SweepParameter::Intensity, first.tuning.intensity.Get(), last.tuning.intensity.Get(), b.intensity.Get(), b.intensity.Get()) &&
                           bottomAndTop(SweepParameter::LocalStructure, first.tuning.localStructure.Get(), last.tuning.localStructure.Get(), b.localStructure.Get(), b.localStructure.Get()) &&
                           bottomAndTop(SweepParameter::LocalTone, first.tuning.localTone.Get(), last.tuning.localTone.Get(), b.localTone.Get(), b.localTone.Get()) &&
                           bottomAndTop(SweepParameter::SkinStructure, first.tuning.skinStructure.Get(), last.tuning.skinStructure.Get(), SkinTopOf(b), b.skinStructure.Get());
    const bool style = !AxisOf(spec, SweepParameter::Style).on || (first.tuning.style == NrStyle::Standard && last.tuning.style == NrStyle::Cinematic);
    const bool mask = !AxisOf(spec, SweepParameter::AutoMask).on || (!first.tuning.autoMask && last.tuning.autoMask);
    const bool passes = !AxisOf(spec, SweepParameter::Passes).on || (first.passes.Get() == 1 && last.passes.Get() == ExpectedValues(spec, SweepParameter::Passes));
    return strengths && style && mask && passes;
}

[[nodiscard]] bool DifferentIndexesGiveDifferentSettings(infra::RngState& rng) noexcept
{
    const SweepSpec spec = SmallSpec(rng);
    const LiveSettings base = PositiveBase(rng);
    const std::uint32_t count = SweepCount(spec);
    if (count < 2)
        return true;
    const std::uint32_t a = proptest::DrawBelow(rng, count);
    const std::uint32_t b = (a + 1 + proptest::DrawBelow(rng, count - 1)) % count;
    return SweepCombination(base, spec, a) != SweepCombination(base, spec, b);
}

} // namespace

std::uint32_t SweepSuite(std::uint64_t seed) noexcept
{
    std::uint32_t failures = 0;
    failures += Failures(proptest::ForAll("Nothing swept means no pictures", seed, 200, NothingSweptMeansNoPictures));
    failures += Failures(proptest::ForAll("The count is the product of the values", seed, 300, TheCountIsTheProductOfTheValues));
    failures += Failures(proptest::ForAll("Each setting changes in its own digit", seed, 400, EachSettingChangesInItsOwnDigit));
    failures += Failures(proptest::ForAll("The first combination is the bottom and the last the top", seed, 300, TheFirstCombinationIsTheBottomAndTheLastTheTop));
    failures += Failures(proptest::ForAll("Different indexes give different settings", seed, 300, DifferentIndexesGiveDifferentSettings));
    return failures;
}

} // namespace tests
