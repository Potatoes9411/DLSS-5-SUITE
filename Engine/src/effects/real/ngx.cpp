#include "effects/real/ngx.h"

#include "global_common.h"
#include "infrastructure/fold.h"
#include "infrastructure/overloaded.h"

#include <algorithm>
#include <string_view>

namespace real {
namespace {

using infra::Fail;
using infra::Result;
using infra::Status;

constexpr std::array<const char*, 6> kPresetNames{
    NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_DLAA,        NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Quality,          NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Balanced,
    NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Performance, NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_UltraPerformance, NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_UltraQuality
};
constexpr std::array<interior::SrQuality, 6> kQualities{ interior::SrQuality::Dlaa,     interior::SrQuality::UltraQuality, interior::SrQuality::Quality,
                                                         interior::SrQuality::Balanced, interior::SrQuality::Performance,  interior::SrQuality::UltraPerformance };
constexpr char kNeuralRenderingAvailable[] = "DLSSNR.Available";
// The model is asked how many sets of weights it carries. Nothing in NGX obliges it to answer, and the
// 310.8 model does not, so both spellings are tried and silence is taken at face value.
constexpr std::array<const char*, 2> kPresetCountNames{ "DLSSNR.PresetCount", "DLSSNR.Presets" };
constexpr std::wstring_view kNeuralRenderingModel = L"\\nvngx_dlssnr.dll";
constexpr std::wstring_view kSuperResolutionModel = L"\\nvngx_dlss.dll";
constexpr std::array<std::wstring_view, 2> kRuntimeNames{ L"\\_nvngx.dll", L"\\nvngx.dll" };
constexpr std::size_t kModelPathCapacity = interior::DirectoryPath::Capacity + std::max(kNeuralRenderingModel.size(), kSuperResolutionModel.size()) + 1;

[[nodiscard]] std::array<wchar_t, kModelPathCapacity> ModelPathIn(std::wstring_view directory, std::wstring_view model) noexcept
{
    std::array<wchar_t, kModelPathCapacity> chars{};
    // WAIVER(R2): a local buffer filled once, before use, from two bounded pieces.
    std::ranges::copy(directory, chars.begin());
    std::ranges::copy(model, chars.begin() + static_cast<std::ptrdiff_t>(directory.size()));
    return chars;
}

// Where the loader will find a model of ours: the folder the executable sits in, then --ngx-path. Both are
// folders anyone may write to, which is why what is found there is checked before the loader is let at it.
[[nodiscard]] std::optional<interior::DirectoryPath> ModelLocation(const NgxSettings& settings, std::wstring_view model) noexcept
{
    static constexpr auto ModelIn = [] [[nodiscard]] (const interior::DirectoryPath& directory, std::wstring_view model) noexcept -> std::optional<interior::DirectoryPath> {
        static constexpr auto HasModel = [] [[nodiscard]] (const interior::DirectoryPath& directory, std::wstring_view model) noexcept -> bool {
            return !directory.IsEmpty() && ::GetFileAttributesW(ModelPathIn(directory.Get(), model).data()) != INVALID_FILE_ATTRIBUTES;
        };
        if (!HasModel(directory, model))
            return std::nullopt;
        return directory;
    };
    return ModelIn(settings.executableDirectory, model).or_else([&settings, model] { return ModelIn(settings.featurePath, model); });
}

[[nodiscard]] Status<Error> CheckNgx(NVSDK_NGX_Result result, ApiCall call) noexcept
{
    if (NVSDK_NGX_FAILED(result))
        return Fail(Error{ call, static_cast<std::uint32_t>(result) });
    return {};
}

[[nodiscard]] unsigned int PathCount(const NgxSettings& s) noexcept
{
    return s.featurePath.IsEmpty() ? 1u : 2u;
}

// Where NGX's own log lines go when nobody asked for them: nowhere. The level is a floor rather than a
// ceiling ("if this is higher than the logging level otherwise configured, this will override that logging
// level"), so asking for OFF does not stop the driver writing its files; only disabling the other sinks
// does, and NGX takes that instruction only from a session that hands it a callback to write to instead.
void NVSDK_CONV DiscardNgxLine(const char*, NVSDK_NGX_Logging_Level, NVSDK_NGX_Feature) noexcept {}

[[nodiscard]] NVSDK_NGX_LoggingInfo LoggingOf(interior::NgxLogLevel level) noexcept
{
    static constexpr auto LoggingLevelOf = [] [[nodiscard]] (interior::NgxLogLevel level) noexcept -> NVSDK_NGX_Logging_Level {
        switch (level)
        {
        case interior::NgxLogLevel::Off: return NVSDK_NGX_LOGGING_LEVEL_OFF;
        case interior::NgxLogLevel::On: return NVSDK_NGX_LOGGING_LEVEL_ON;
        case interior::NgxLogLevel::Verbose: return NVSDK_NGX_LOGGING_LEVEL_VERBOSE;
        }
        return NVSDK_NGX_LOGGING_LEVEL_OFF;
    };
    if (level == interior::NgxLogLevel::Off)
        return NVSDK_NGX_LoggingInfo{ &DiscardNgxLine, NVSDK_NGX_LOGGING_LEVEL_OFF, true };
    return NVSDK_NGX_LoggingInfo{ nullptr, LoggingLevelOf(level), false };
}

[[nodiscard]] NVSDK_NGX_FeatureCommonInfo CommonInfoOf(const std::array<const wchar_t*, 2>& pointers, unsigned int count, interior::NgxLogLevel level) noexcept
{
    NVSDK_NGX_FeatureCommonInfo info{};
    info.PathListInfo = NVSDK_NGX_PathListInfo{ pointers.data(), count };
    info.LoggingInfo = LoggingOf(level);
    return info;
}

[[nodiscard]] std::optional<int> IntOf(const NVSDK_NGX_Parameter* p, const char* name) noexcept
{
    int value = 0;
    if (NVSDK_NGX_FAILED(p->Get(name, &value)))
        return std::nullopt;
    return value;
}

[[nodiscard]] std::optional<unsigned int> UIntOf(const NVSDK_NGX_Parameter* p, const char* name) noexcept
{
    unsigned int value = 0;
    if (NVSDK_NGX_FAILED(p->Get(name, &value)))
        return std::nullopt;
    return value;
}

[[nodiscard]] bool IsSuperResolutionAvailable(const NVSDK_NGX_Parameter* p) noexcept
{
    return IntOf(p, NVSDK_NGX_Parameter_SuperSampling_Available).value_or(0) != 0;
}

[[nodiscard]] NVSDK_NGX_PerfQuality_Value PerfQualityOf(interior::SrQuality quality) noexcept
{
    switch (quality)
    {
    case interior::SrQuality::Dlaa: return NVSDK_NGX_PerfQuality_Value_DLAA;
    case interior::SrQuality::UltraQuality: return NVSDK_NGX_PerfQuality_Value_UltraQuality;
    case interior::SrQuality::Quality: return NVSDK_NGX_PerfQuality_Value_MaxQuality;
    case interior::SrQuality::Balanced: return NVSDK_NGX_PerfQuality_Value_Balanced;
    case interior::SrQuality::Performance: return NVSDK_NGX_PerfQuality_Value_MaxPerf;
    case interior::SrQuality::UltraPerformance: return NVSDK_NGX_PerfQuality_Value_UltraPerformance;
    }
    return NVSDK_NGX_PerfQuality_Value_Balanced;
}

struct OptimalSettings
{
    unsigned int optimalWidth;
    unsigned int optimalHeight;
    unsigned int maxWidth;
    unsigned int maxHeight;
    unsigned int minWidth;
    unsigned int minHeight;
    float sharpness;
    NVSDK_NGX_Result result;
};

[[nodiscard]] std::optional<interior::Extent> ExtentOf(unsigned int width, unsigned int height) noexcept
{
    return interior::PixelCountTag::Parse(width)
        .and_then([height](interior::PixelCount w) { return interior::PixelCountTag::Parse(height).transform([w](interior::PixelCount h) { return interior::Extent{ w, h }; }); })
        .transform([](const interior::Extent& e) { return std::optional<interior::Extent>{ e }; })
        .value_or(std::nullopt);
}

[[nodiscard]] Status<Error> WriteVerified(NVSDK_NGX_Parameter* p, const char* name, const NgxSlot& value) noexcept
{
    static constexpr auto Write = [](NVSDK_NGX_Parameter* p, const char* name, const NgxSlot& value) noexcept -> void { std::visit([p, name](auto v) { p->Set(name, v); }, value); };

    static constexpr auto ReadsBack = [] [[nodiscard]] (const NVSDK_NGX_Parameter* p, const char* name, const NgxSlot& value) noexcept -> bool {
        return std::visit(infra::Overloaded{
                              [p, name](unsigned int v) {
                                  unsigned int back = 0;
                                  return !NVSDK_NGX_FAILED(p->Get(name, &back)) && back == v;
                              },
                              [p, name](float v) {
                                  float back = 0.0f;
                                  return !NVSDK_NGX_FAILED(p->Get(name, &back)) && back == v;
                              },
                              [p, name](ID3D12Resource* v) {
                                  ID3D12Resource* back = nullptr;
                                  return !NVSDK_NGX_FAILED(p->Get(name, &back)) && back == v;
                              },
                          },
                          value);
    };
    Write(p, name, value);
    if (!ReadsBack(p, name, value))
        return Fail(Error{ ApiCall::NgxParameterRoundTrip, static_cast<std::uint32_t>(value.index()) });
    return {};
}

[[nodiscard]] Status<Error> WriteAll(NVSDK_NGX_Parameter* p, const BoundNrParameters& list) noexcept
{
    static constexpr auto CName = [] [[nodiscard]] (interior::NrParameter parameter) noexcept -> const char* {
        const std::string_view name = interior::NameOf(parameter);
        REQUIRE(name.data()[name.size()] == '\0');
        return name.data();
    };
    return infra::ForEach(list.Items(), Status<Error>{}, [p](const BoundNrParameter& b) { return WriteVerified(p, CName(b.name), b.value); });
}

[[nodiscard]] Result<NgxSlot, Error> SlotOf(const interior::NgxValue& value, const ResourceTable& table) noexcept
{
    return std::visit(infra::Overloaded{
                          [](std::uint32_t v) -> Result<NgxSlot, Error> { return NgxSlot{ v }; },
                          [](float v) -> Result<NgxSlot, Error> { return NgxSlot{ v }; },
                          [&table](const interior::ResourceId& id) -> Result<NgxSlot, Error> { return Lookup(table, id).transform([](ID3D12Resource* r) { return NgxSlot{ r }; }); },
                      },
                      value);
}

[[nodiscard]] Result<Feature, Error> Created(NVSDK_NGX_Handle* raw, NVSDK_NGX_Result result) noexcept
{
    static constexpr auto OwnedFeature = [] [[nodiscard]] (NVSDK_NGX_Handle * raw) noexcept -> Result<Feature, Error> {
        if (raw == nullptr)
            return Fail(Error{ ApiCall::NgxCreateFeature, 0 });
        return Feature(raw);
    };
    return CheckNgx(result, ApiCall::NgxCreateFeature).and_then([raw] { return OwnedFeature(raw); });
}

[[nodiscard]] Result<BoundNrParameters, Error> BoundOrFull(const Result<interior::NrParameterList, infra::CapacityExceeded>& list, const ResourceTable& table) noexcept
{
    static constexpr auto FromCapacity = [] [[nodiscard]] (infra::CapacityExceeded) noexcept -> Error { return Error{ ApiCall::NgxParameterList, 0 }; };

    static constexpr auto Bound = [] [[nodiscard]] (const interior::NrParameterList& list, const ResourceTable& table) noexcept -> Result<BoundNrParameters, Error> {
        static constexpr auto WithBound = [] [[nodiscard]] (const BoundNrParameters& acc, const interior::NrParameterValue& v,
                                                            const ResourceTable& table) noexcept -> Result<BoundNrParameters, Error> {
            return SlotOf(v.value, table).and_then([&](const NgxSlot& slot) { return acc.Push(BoundNrParameter{ v.name, slot }).transform_error(FromCapacity); });
        };
        return infra::FoldResult(list.Items(), Result<BoundNrParameters, Error>(BoundNrParameters{}),
                                 [&table](const BoundNrParameters& acc, const interior::NrParameterValue& v) { return WithBound(acc, v, table); });
    };
    return list.transform_error(FromCapacity).and_then([&table](const interior::NrParameterList& l) { return Bound(l, table); });
}

} // namespace

NgxPaths::NgxPaths(const NgxSettings& settings) noexcept
    : executable_(settings.executableDirectory), feature_(settings.featurePath), pointers_{ executable_.CString(), feature_.CString() },
      common_(CommonInfoOf(pointers_, PathCount(settings), settings.logLevel))
{
}

void NgxShutdown::operator()(ID3D12Device* device) const noexcept
{
    ENSURE(!NVSDK_NGX_FAILED(NVSDK_NGX_D3D12_Shutdown1(device)));
}

void ParameterDestroyer::operator()(NVSDK_NGX_Parameter* parameters) const noexcept
{
    ENSURE(!NVSDK_NGX_FAILED(NVSDK_NGX_D3D12_DestroyParameters(parameters)));
}

void FeatureReleaser::operator()(NVSDK_NGX_Handle* handle) const noexcept
{
    ENSURE(!NVSDK_NGX_FAILED(NVSDK_NGX_D3D12_ReleaseFeature(handle)));
}

std::optional<interior::DirectoryPath> NeuralRenderingModelLocation(const NgxSettings& settings) noexcept
{
    return ModelLocation(settings, kNeuralRenderingModel);
}

LoadableFiles LoadableFilesOf(const NgxSettings& settings) noexcept
{
    static constexpr auto FileIn = [] [[nodiscard]] (const interior::DirectoryPath& directory, std::wstring_view name) noexcept -> std::optional<interior::FilePath> {
        if (directory.IsEmpty() || ::GetFileAttributesW(ModelPathIn(directory.Get(), name).data()) == INVALID_FILE_ATTRIBUTES)
            return std::nullopt;
        return interior::FilePath::Parse(ModelPathIn(directory.Get(), name).data())
            .transform([](const interior::FilePath& path) { return std::optional<interior::FilePath>{ path }; })
            .value_or(std::nullopt);
    };
    return LoadableFiles{ FileIn(settings.executableDirectory, kNeuralRenderingModel), FileIn(settings.featurePath, kNeuralRenderingModel),
                          FileIn(settings.executableDirectory, kSuperResolutionModel), FileIn(settings.featurePath, kSuperResolutionModel),
                          FileIn(settings.executableDirectory, kRuntimeNames[0]),      FileIn(settings.featurePath, kRuntimeNames[0]),
                          FileIn(settings.executableDirectory, kRuntimeNames[1]),      FileIn(settings.featurePath, kRuntimeNames[1]) };
}

Requirement RequirementOf(const GpuDevice& gpu, const NgxSettings& settings, NVSDK_NGX_Feature feature) noexcept
{
    static constexpr auto IdentifierOf = [] [[nodiscard]] (const NgxSettings& s) noexcept -> NVSDK_NGX_Application_Identifier {
        static constexpr auto AppIdentifier = [] [[nodiscard]] (interior::NgxAppId id) noexcept -> NVSDK_NGX_Application_Identifier {
            NVSDK_NGX_Application_Identifier identifier{};
            identifier.IdentifierType = NVSDK_NGX_Application_Identifier_Type_Application_Id;
            identifier.v.ApplicationId = id.Get();
            return identifier;
        };

        static constexpr auto ProjectIdentifier = [] [[nodiscard]] (const NgxSettings& s) noexcept -> NVSDK_NGX_Application_Identifier {
            NVSDK_NGX_Application_Identifier identifier{};
            identifier.IdentifierType = NVSDK_NGX_Application_Identifier_Type_Project_Id;
            identifier.v.ProjectDesc = NVSDK_NGX_ProjectIdDescription{ s.projectId.CString(), NVSDK_NGX_ENGINE_TYPE_CUSTOM, DSCREEN_VERSION_STRING };
            return identifier;
        };
        return s.appId.has_value() ? AppIdentifier(*s.appId) : ProjectIdentifier(s);
    };
    const NgxPaths paths{ settings };
    const NVSDK_NGX_FeatureDiscoveryInfo info{ NVSDK_NGX_Version_API, feature, IdentifierOf(settings), settings.dataPath.CString(), &paths.Common() };
    NVSDK_NGX_FeatureRequirement requirement{ static_cast<NVSDK_NGX_Feature_Support_Result>(0xFFFFFFFFu), 0, {} };
    const NVSDK_NGX_Result result = NVSDK_NGX_D3D12_GetFeatureRequirements(gpu.adapter.Get(), &info, &requirement);
    return Requirement{ result, static_cast<std::uint32_t>(requirement.FeatureSupported) };
}

Result<NgxRuntime, Error> CreateNgxRuntime(const GpuDevice& gpu, const NgxSettings& settings) noexcept
{
    static constexpr auto Init = [] [[nodiscard]] (const NgxSettings& s, ID3D12Device* device, const NVSDK_NGX_FeatureCommonInfo& common) noexcept -> NVSDK_NGX_Result {
        static constexpr auto InitWithAppId = [] [[nodiscard]] (interior::NgxAppId id, const NgxSettings& s, ID3D12Device* device,
                                                                const NVSDK_NGX_FeatureCommonInfo& common) noexcept -> NVSDK_NGX_Result {
            return NVSDK_NGX_D3D12_Init(id.Get(), s.dataPath.CString(), device, &common, NVSDK_NGX_Version_API);
        };

        static constexpr auto InitWithProjectId = [] [[nodiscard]] (const NgxSettings& s, ID3D12Device* device, const NVSDK_NGX_FeatureCommonInfo& common) noexcept -> NVSDK_NGX_Result {
            return NVSDK_NGX_D3D12_Init_with_ProjectID(s.projectId.CString(), NVSDK_NGX_ENGINE_TYPE_CUSTOM, DSCREEN_VERSION_STRING, s.dataPath.CString(), device, &common, NVSDK_NGX_Version_API);
        };
        return s.appId.has_value() ? InitWithAppId(*s.appId, s, device, common) : InitWithProjectId(s, device, common);
    };

    static constexpr auto Initialized = [] [[nodiscard]] (const GpuDevice& gpu, const std::shared_ptr<const NgxPaths>& paths) noexcept -> Result<NgxRuntime, Error> {
        static constexpr auto CapabilityParameters = [] [[nodiscard]] () noexcept -> Result<NgxParameters, Error> {
            static constexpr auto OwnedParameters = [] [[nodiscard]] (NVSDK_NGX_Parameter * raw) noexcept -> Result<NgxParameters, Error> {
                if (raw == nullptr)
                    return Fail(Error{ ApiCall::NgxGetCapabilityParameters, 0 });
                return NgxParameters(raw);
            };
            NVSDK_NGX_Parameter* raw = nullptr;
            return CheckNgx(NVSDK_NGX_D3D12_GetCapabilityParameters(&raw), ApiCall::NgxGetCapabilityParameters).and_then([raw] { return OwnedParameters(raw); });
        };
        NgxSession session(gpu.device.Get());
        return CapabilityParameters().transform([&](NgxParameters parameters) { return NgxRuntime{ paths, gpu.device, std::move(session), std::move(parameters) }; });
    };

    // The model draws its own overlay, naming its version, the preset it resolved and the sizes it is
    // working at, when this reads exactly 1024. It is read as the model loads, so it is asked for first.
    static constexpr auto RequestIndicator = [] [[nodiscard]] (bool wanted) noexcept -> Status<Error> {
        if (!wanted)
            return {};
        return CheckBool(::SetEnvironmentVariableW(L"__NGX_SHOW_INDICATOR", L"1024"), ApiCall::SetEnvironmentVariable);
    };

    // The model keeps its compiled kernels between runs unless told not to, which is worth a look when a
    // kernel is suspected of being stale.
    static constexpr auto RequestKernelCache = [] [[nodiscard]] (bool wanted) noexcept -> Status<Error> {
        if (wanted)
            return {};
        return CheckBool(::SetEnvironmentVariableW(L"__NGX_CUBIN_DISABLE_RESOURCE_CACHE", L"1"), ApiCall::SetEnvironmentVariable);
    };
    const std::shared_ptr<const NgxPaths> paths = std::make_shared<const NgxPaths>(settings);
    return RequestIndicator(settings.indicator)
        .and_then([&] { return RequestKernelCache(settings.cubinCache); })
        .and_then([&] { return CheckNgx(Init(settings, gpu.device.Get(), paths->Common()), ApiCall::NgxInit); })
        .and_then([&] { return Initialized(gpu, paths); });
}

bool OffersSuperResolution(const NgxRuntime& runtime) noexcept
{
    static constexpr auto IsCurrentDriver = [] [[nodiscard]] (const NVSDK_NGX_Parameter* p) noexcept -> bool {
        static constexpr auto NeedsDriverUpdate = [] [[nodiscard]] (const NVSDK_NGX_Parameter* p) noexcept -> bool {
            return IntOf(p, NVSDK_NGX_Parameter_SuperSampling_NeedsUpdatedDriver).value_or(0) != 0;
        };
        return !NeedsDriverUpdate(p);
    };
    return IsCurrentDriver(runtime.parameters.get()) && IsSuperResolutionAvailable(runtime.parameters.get());
}

std::optional<std::uint32_t> NeuralRenderingAvailability(const NgxRuntime& runtime) noexcept
{
    return UIntOf(runtime.parameters.get(), kNeuralRenderingAvailable);
}

std::optional<std::uint32_t> NeuralRenderingPresetCount(const NgxRuntime& runtime) noexcept
{
    const NVSDK_NGX_Parameter* p = runtime.parameters.get();
    const auto answered = [p](const std::optional<std::uint32_t>& so, const char* name) { return so.has_value() ? so : UIntOf(p, name); };
    return std::ranges::fold_left(kPresetCountNames, std::optional<std::uint32_t>{}, answered);
}

interior::QualityTable QualityTableFor(const NgxRuntime& runtime, const interior::Extent& target) noexcept
{
    static constexpr auto Optimal = [] [[nodiscard]] (NVSDK_NGX_Parameter * p, const interior::Extent& target, NVSDK_NGX_PerfQuality_Value quality) noexcept -> OptimalSettings {
        OptimalSettings o{};
        o.result =
            NGX_DLSS_GET_OPTIMAL_SETTINGS(p, target.width.Get(), target.height.Get(), quality, &o.optimalWidth, &o.optimalHeight, &o.maxWidth, &o.maxHeight, &o.minWidth, &o.minHeight, &o.sharpness);
        return o;
    };

    static constexpr auto RangeFrom = [] [[nodiscard]] (interior::SrQuality quality, const OptimalSettings& o) noexcept -> std::optional<interior::QualityRange> {
        if (NVSDK_NGX_FAILED(o.result))
            return std::nullopt;
        return ExtentOf(o.optimalWidth, o.optimalHeight).and_then([&](const interior::Extent& optimal) {
            return ExtentOf(o.minWidth, o.minHeight).and_then([&](const interior::Extent& minimum) {
                return ExtentOf(o.maxWidth, o.maxHeight).transform([&](const interior::Extent& maximum) { return interior::QualityRange{ quality, optimal, minimum, maximum }; });
            });
        });
    };

    static constexpr auto WithRange = [] [[nodiscard]] (const interior::QualityTable& table, const std::optional<interior::QualityRange>& range) noexcept -> interior::QualityTable {
        if (!range.has_value())
            return table;
        const Result<interior::QualityTable, infra::CapacityExceeded> pushed = table.Push(*range);
        ENSURE(pushed.has_value());
        return *pushed;
    };
    return std::ranges::fold_left(kQualities, interior::QualityTable{}, [&](const interior::QualityTable& acc, interior::SrQuality quality) {
        return WithRange(acc, RangeFrom(quality, Optimal(runtime.parameters.get(), target, PerfQualityOf(quality))));
    });
}

Result<Feature, Error> CreateSuperResolution(const NgxRuntime& runtime, ID3D12GraphicsCommandList* list, const interior::SrChoice& choice) noexcept
{
    static constexpr auto WritePresets = [] [[nodiscard]] (NVSDK_NGX_Parameter * p, interior::SrPreset preset) noexcept -> Status<Error> {
        if (preset.Get() == 0)
            return {};
        return infra::ForEach(kPresetNames, Status<Error>{}, [p, preset](const char* name) { return WriteVerified(p, name, NgxSlot{ preset.Get() }); });
    };

    static constexpr auto CreateParamsOf = [] [[nodiscard]] (const interior::SrChoice& c) noexcept -> NVSDK_NGX_DLSS_Create_Params {
        static constexpr auto CreateFlagsOf = [] [[nodiscard]] (bool hdr) noexcept -> int {
            return hdr ? (NVSDK_NGX_DLSS_Feature_Flags_MVLowRes | NVSDK_NGX_DLSS_Feature_Flags_IsHDR) : NVSDK_NGX_DLSS_Feature_Flags_MVLowRes;
        };
        NVSDK_NGX_DLSS_Create_Params create{};
        create.Feature = NVSDK_NGX_Feature_Create_Params{ c.input.width.Get(), c.input.height.Get(), c.output.width.Get(), c.output.height.Get(), PerfQualityOf(c.quality) };
        create.InFeatureCreateFlags = CreateFlagsOf(c.hdr);
        create.InEnableOutputSubrects = false;
        return create;
    };
    NVSDK_NGX_DLSS_Create_Params create = CreateParamsOf(choice);
    NVSDK_NGX_Handle* raw = nullptr;
    return WritePresets(runtime.parameters.get(), choice.preset).and_then([&] { return Created(raw, NGX_D3D12_CREATE_DLSS_EXT(list, 1, 1, &raw, runtime.parameters.get(), &create)); });
}

Result<Feature, Error> CreateNeuralRendering(const NgxRuntime& runtime, ID3D12GraphicsCommandList* list, const interior::NrTuning& tuning, const interior::Extent& work) noexcept
{
    NVSDK_NGX_Handle* raw = nullptr;
    return BoundOrFull(interior::NrCreationParameters(tuning, work), ResourceTable{})
        .and_then([&](const BoundNrParameters& parameters) { return WriteAll(runtime.parameters.get(), parameters); })
        .and_then([&] { return Created(raw, NVSDK_NGX_D3D12_CreateFeature(list, kNeuralRenderingFeature, runtime.parameters.get(), &raw)); });
}

Status<Error> EvaluateSuperResolution(const NgxRuntime& runtime, const Feature& feature, ID3D12GraphicsCommandList* list, const SrInputs& inputs) noexcept
{
    static constexpr auto EvalParamsOf = [] [[nodiscard]] (const SrInputs& in) noexcept -> NVSDK_NGX_D3D12_DLSS_Eval_Params {
        static constexpr auto WithRender = [] [[nodiscard]] (NVSDK_NGX_D3D12_DLSS_Eval_Params eval, const SrInputs& in) noexcept -> NVSDK_NGX_D3D12_DLSS_Eval_Params {
            eval.InRenderSubrectDimensions = NVSDK_NGX_Dimensions{ in.render.width.Get(), in.render.height.Get() };
            eval.InReset = in.reset ? 1 : 0;
            return eval;
        };

        static constexpr auto WithNeutralExposure = [] [[nodiscard]] (NVSDK_NGX_D3D12_DLSS_Eval_Params eval) noexcept -> NVSDK_NGX_D3D12_DLSS_Eval_Params {
            eval.InMVScaleX = 1.0f;
            eval.InMVScaleY = 1.0f;
            eval.InPreExposure = 1.0f;
            eval.InExposureScale = 1.0f;
            return eval;
        };
        NVSDK_NGX_D3D12_DLSS_Eval_Params eval{};
        eval.Feature = NVSDK_NGX_D3D12_Feature_Eval_Params{ in.io.color, in.io.output, 0.0f };
        eval.pInDepth = in.io.depth;
        eval.pInMotionVectors = in.io.motionVectors;
        return WithNeutralExposure(WithRender(eval, in));
    };
    NVSDK_NGX_D3D12_DLSS_Eval_Params eval = EvalParamsOf(inputs);
    return CheckNgx(NGX_D3D12_EVALUATE_DLSS_EXT(list, feature.get(), runtime.parameters.get(), &eval), ApiCall::NgxEvaluateFeature);
}

Status<Error> EvaluateNeuralRendering(const NgxRuntime& runtime, const Feature& feature, ID3D12GraphicsCommandList* list, const interior::NrTuning& tuning, const interior::EvaluateNr& evaluate,
                                      const ResourceTable& resources) noexcept
{
    return BoundOrFull(interior::NrEvaluationParameters(tuning, evaluate), resources)
        .and_then([&](const BoundNrParameters& parameters) { return WriteAll(runtime.parameters.get(), parameters); })
        .and_then([&] { return CheckNgx(NVSDK_NGX_D3D12_EvaluateFeature(list, feature.get(), runtime.parameters.get(), nullptr), ApiCall::NgxEvaluateFeature); });
}

} // namespace real
