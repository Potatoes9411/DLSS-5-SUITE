// The chain request the certificate API takes has fields its header offers only under this, and every
// field of it is named below.
#define CERT_CHAIN_PARA_HAS_EXTRA_FIELDS
#include "effects/real/trust.h"

#include <softpub.h>
#include <wintrust.h>
#include <winver.h>

#include <algorithm>
#include <array>
#include <cstring>
#include <cwchar>
#include <memory>
#include <optional>
#include <ranges>
#include <string_view>
#include <vector>

namespace real {
namespace {

using infra::Fail;
using infra::Result;
using infra::Status;

// WINTRUST_ACTION_GENERIC_VERIFY_V2 is a macro over an initialiser, so it is named once here. The call
// takes a mutable pointer to it and does not write through it.
GUID kVerifyAction = WINTRUST_ACTION_GENERIC_VERIFY_V2;          // WAIVER(R2): a constant the API insists on being handed by non-const pointer.
constexpr std::wstring_view kSignerName = L"NVIDIA Corporation"; // the common name of NVIDIA's signing certificates, compared whole
constexpr std::size_t kNameCapacity = 256;
constexpr std::size_t kKeyCapacity = 64;
// A file may carry signatures after its first, each verified on its own; one with more than this many is
// refused rather than looped over. NVIDIA's driver files carry two: NVIDIA's own and Microsoft's.
constexpr DWORD kMaxSecondarySignatures = 15;
// How long the chain build may spend on each thing it fetches, a root Microsoft lists that the machine does not hold yet above all.
constexpr DWORD kFetchMilliseconds = 15000;

// The calls a check on one of the files fails with, so a refusal names the file it is about.
struct Refusals
{
    ApiCall open;
    ApiCall notSigned;
    ApiCall notFromNvidia;
    ApiCall rootNotTrusted;
};

[[nodiscard]] Refusals RefusalsOf(ModelKind kind) noexcept
{
    switch (kind)
    {
    case ModelKind::NeuralRendering: return Refusals{ ApiCall::OpenModelFile, ApiCall::ModelNotSigned, ApiCall::ModelNotFromNvidia, ApiCall::ModelRootNotTrusted };
    case ModelKind::SuperResolution: return Refusals{ ApiCall::OpenUpscalerFile, ApiCall::UpscalerNotSigned, ApiCall::UpscalerNotFromNvidia, ApiCall::UpscalerRootNotTrusted };
    case ModelKind::OpticalFlow: return Refusals{ ApiCall::OpenOpticalFlowFile, ApiCall::OpticalFlowNotSigned, ApiCall::OpticalFlowNotFromNvidia, ApiCall::OpticalFlowRootNotTrusted };
    case ModelKind::Runtime: return Refusals{ ApiCall::OpenRuntimeFile, ApiCall::RuntimeNotSigned, ApiCall::RuntimeNotFromNvidia, ApiCall::RuntimeRootNotTrusted };
    }
    return Refusals{ ApiCall::OpenModelFile, ApiCall::ModelNotSigned, ApiCall::ModelNotFromNvidia, ApiCall::ModelRootNotTrusted };
}

// What a verified signature's signer turned out to be: not NVIDIA; NVIDIA Corporation by name, but with a
// chain that does not reach a root on Microsoft's own list; or NVIDIA. Listed in the order of preference.
enum class Signer : std::uint8_t { Other, NvidiaUnrooted, Nvidia };

// One signer as judged, with the chain's error when it is NVIDIA by name only.
struct Found
{
    Signer signer;
    DWORD rootError;
};

// What one verified signature said: who signed it, and how many signatures follow the first, which only
// the first is asked about.
struct Signature
{
    Found found;
    DWORD secondaries;
};

[[nodiscard]] Found Better(const Found& a, const Found& b) noexcept
{
    return b.signer > a.signer ? b : a;
}

struct StoreCloser
{
    void operator()(void* store) const noexcept { (void)::CertCloseStore(store, 0); }
};
using UniqueStore = std::unique_ptr<void, StoreCloser>;

struct ChainFreer
{
    void operator()(const CERT_CHAIN_CONTEXT* chain) const noexcept { ::CertFreeCertificateChain(chain); }
};
using UniqueChain = std::unique_ptr<const CERT_CHAIN_CONTEXT, ChainFreer>;

// One entry of a version resource's translation table: which language its strings are kept under.
struct Translation
{
    WORD language;
    WORD codePage;
};

// What the file's version resource calls its product. Read by path once the file is held, which is safe
// because the handle's share mode keeps the file from being written, deleted or renamed under the path.
[[nodiscard]] ProductName ProductNameOf(const wchar_t* path) noexcept
{
    static constexpr auto VersionBlock = [] [[nodiscard]] (const wchar_t* path) noexcept -> std::vector<std::byte> {
        DWORD ignored = 0; // WAIVER(R2): an out-parameter the API insists on, which it always sets to zero.
        const DWORD size = ::GetFileVersionInfoSizeW(path, &ignored);
        if (size == 0)
            return {};
        std::vector<std::byte> block(size); // WAIVER(R2): a buffer the API fills once, before use.
        if (::GetFileVersionInfoW(path, 0, size, block.data()) == FALSE)
            return {};
        return block;
    };

    // The strings are kept per language; the first language listed is the one asked.
    static constexpr auto FirstTranslation = [] [[nodiscard]] (const std::vector<std::byte>& block) noexcept -> std::optional<Translation> {
        void* found = nullptr; // WAIVER(R2): the answer of one query, read once after it.
        UINT length = 0;
        if (block.empty() || ::VerQueryValueW(block.data(), L"\\VarFileInfo\\Translation", &found, &length) == FALSE || length < sizeof(Translation))
            return std::nullopt;
        Translation first{ 0, 0 }; // WAIVER(R2): copied out of the block, whose alignment is the API's to promise.
        std::memcpy(&first, found, sizeof(Translation));
        return first;
    };

    static constexpr auto NamedProduct = [] [[nodiscard]] (const std::vector<std::byte>& block, const Translation& translation) noexcept -> ProductName {
        static constexpr auto KeyOf = [] [[nodiscard]] (const Translation& translation) noexcept -> std::array<wchar_t, kKeyCapacity> {
            std::array<wchar_t, kKeyCapacity> key{}; // WAIVER(R2): a local buffer filled once, before use.
            (void)::_snwprintf_s(key.data(), key.size(), _TRUNCATE, L"\\StringFileInfo\\%04x%04x\\ProductName", static_cast<unsigned int>(translation.language),
                                 static_cast<unsigned int>(translation.codePage));
            return key;
        };
        void* found = nullptr; // WAIVER(R2): the answer of one query, read once after it.
        UINT length = 0;
        if (::VerQueryValueW(block.data(), KeyOf(translation).data(), &found, &length) == FALSE || length == 0)
            return ProductName{};
        const wchar_t* text = static_cast<const wchar_t*>(found);
        return ProductName::Parse(std::wstring_view(text, ::wcsnlen(text, length))).value_or(ProductName{});
    };
    const std::vector<std::byte> block = VersionBlock(path);
    const std::optional<Translation> translation = FirstTranslation(block);
    if (!translation.has_value())
        return ProductName{};
    return NamedProduct(block, *translation);
}

} // namespace

Result<TrustedFile, Error> OpenTrusted(const interior::FilePath& path, ModelKind kind) noexcept
{
    // Shared for reading only, so nothing else may write to the file, delete it or rename it while it is held.
    static constexpr auto OpenForReading = [] [[nodiscard]] (const wchar_t* path, ModelKind kind) noexcept -> Result<UniqueHandle, Error> {
        void* handle = ::CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (handle == INVALID_HANDLE_VALUE)
            return Fail(LastError(RefusalsOf(kind).open));
        return UniqueHandle(handle);
    };

    // Every signature the file carries is verified, the first and each after it, one call each; the first
    // call also counts the rest. The file passes when each is trusted and one of them is NVIDIA's.
    static constexpr auto Verified = [] [[nodiscard]] (const wchar_t* path, void* handle, ModelKind kind) noexcept -> Status<Error> {
        static constexpr auto FileInfoFor = [] [[nodiscard]] (const wchar_t* path, void* handle) noexcept -> WINTRUST_FILE_INFO {
            return WINTRUST_FILE_INFO{ .cbStruct = sizeof(WINTRUST_FILE_INFO), .pcwszFilePath = path, .hFile = handle, .pgKnownSubject = nullptr };
        };

        // Which signature to verify, by index: the first is 0 and the ones after it count from 1. The count
        // of the ones after the first is written into the record by the call that asks for it.
        static constexpr auto SettingsFor = [] [[nodiscard]] (DWORD index, DWORD flags) noexcept -> WINTRUST_SIGNATURE_SETTINGS {
            return WINTRUST_SIGNATURE_SETTINGS{
                .cbStruct = sizeof(WINTRUST_SIGNATURE_SETTINGS), .dwIndex = index, .dwFlags = flags, .cSecondarySigs = 0, .dwVerifiedSigIndex = 0, .pCryptoPolicy = nullptr
            };
        };

        // Asked of the handle already held rather than of the path, so it cannot be answered about another file.
        // WAIVER(R1): every field is named, including the ones that want nothing, which is the point of it.
        static constexpr auto RequestFor = [] [[nodiscard]] (WINTRUST_FILE_INFO * file, WINTRUST_SIGNATURE_SETTINGS * settings) noexcept -> WINTRUST_DATA {
            WINTRUST_DATA request{}; // WAIVER(R2): a request record filled once, before it is asked.
            request.cbStruct = sizeof(WINTRUST_DATA);
            request.pPolicyCallbackData = nullptr;
            request.pSIPClientData = nullptr;
            request.dwUIChoice = WTD_UI_NONE;
            request.fdwRevocationChecks = WTD_REVOKE_NONE;
            request.dwUnionChoice = WTD_CHOICE_FILE;
            request.pFile = file;
            request.dwStateAction = WTD_STATEACTION_VERIFY;
            request.hWVTStateData = nullptr;
            request.pwszURLReference = nullptr;
            request.dwProvFlags = WTD_SAFER_FLAG | WTD_CACHE_ONLY_URL_RETRIEVAL;
            request.dwUIContext = 0;
            request.pSignatureSettings = settings;
            return request;
        };

        static constexpr auto Answered = [] [[nodiscard]] (WINTRUST_DATA & request, ModelKind kind) noexcept -> Result<Signature, Error> {
            // Who signed: the signer's certificate has to be NVIDIA Corporation by common name, and its chain,
            // built again here under stricter terms than Windows applied, has to reach a root on Microsoft's own list.
            static constexpr auto Judged = [] [[nodiscard]] (HANDLE state) noexcept -> Found {
                static constexpr auto SignerOf = [] [[nodiscard]] (CRYPT_PROVIDER_DATA * provider) noexcept -> CRYPT_PROVIDER_SGNR* {
                    return provider == nullptr ? nullptr : ::WTHelperGetProvSignerFromChain(provider, 0, FALSE, 0);
                };

                static constexpr auto CertificateOf = [] [[nodiscard]] (CRYPT_PROVIDER_SGNR * signer) noexcept -> CRYPT_PROVIDER_CERT* {
                    return signer == nullptr ? nullptr : ::WTHelperGetProvCertFromChain(signer, 0);
                };

                // The certificate's common name on its own; the display name would stand in another attribute when there is none.
                static constexpr auto NamedNvidia = [] [[nodiscard]] (const CERT_CONTEXT* certificate) noexcept -> bool {
                    std::array<wchar_t, kNameCapacity> name{};                                  // WAIVER(R2): a local buffer filled once, before use.
                    std::array<char, sizeof(szOID_COMMON_NAME)> attribute{ szOID_COMMON_NAME }; // the identifier is taken through a pointer to non-const
                    (void)::CertGetNameStringW(certificate, CERT_NAME_ATTR_TYPE, 0, attribute.data(), name.data(), kNameCapacity);
                    return ::CompareStringOrdinal(name.data(), -1, kSignerName.data(), -1, TRUE) == CSTR_EQUAL;
                };

                // The signer's chain built again from two sources only, Microsoft's own root list and the
                // certificates the signature carries in its message, at the moment the signature was verified for,
                // and asked for code signing: a root anyone put into the ordinary stores does not count, and a
                // root on Microsoft's list that the machine does not hold yet is fetched, with a bound on the
                // wait. Zero when the chain is clean and the base policy accepts it; otherwise the chain's
                // trust status, the policy's error or the call's.
                static constexpr auto RootTrouble = [] [[nodiscard]] (const CRYPT_PROVIDER_DATA* provider, const CRYPT_PROVIDER_SGNR* signer, const CERT_CONTEXT* certificate) noexcept -> DWORD {
                    // The certificates the signature carries in its own message, as a store: WinTrust holds the
                    // message it verified, the nested one when the signature is a secondary. A root among them
                    // is not trusted for being there.
                    static constexpr auto Carried = [] [[nodiscard]] (const CRYPT_PROVIDER_DATA* provider) noexcept -> UniqueStore {
                        if (provider->hMsg == nullptr)
                            return UniqueStore{};
                        return UniqueStore(::CertOpenStore(CERT_STORE_PROV_MSG, provider->dwEncoding, 0, 0, provider->hMsg));
                    };

                    // WAIVER(R1): every field of the request is named, the ones that want nothing included.
                    static constexpr auto Built = [] [[nodiscard]] (const CRYPT_PROVIDER_SGNR* signer, const CERT_CONTEXT* certificate, void* carried) noexcept -> Result<UniqueChain, DWORD> {
                        std::array<char, sizeof(szOID_PKIX_KP_CODE_SIGNING)> codeSigning{ szOID_PKIX_KP_CODE_SIGNING }; // the usage is taken through pointers to non-const
                        std::array<LPSTR, 1> usages{ codeSigning.data() };
                        // WAIVER(R2): the request and the time are handed over by non-const pointer; the call writes neither.
                        CERT_CHAIN_PARA request{ .cbSize = sizeof(CERT_CHAIN_PARA),
                                                 .RequestedUsage =
                                                     CERT_USAGE_MATCH{ .dwType = USAGE_MATCH_TYPE_AND, .Usage = CERT_ENHKEY_USAGE{ .cUsageIdentifier = 1, .rgpszUsageIdentifier = usages.data() } },
                                                 .RequestedIssuancePolicy =
                                                     CERT_USAGE_MATCH{ .dwType = USAGE_MATCH_TYPE_AND, .Usage = CERT_ENHKEY_USAGE{ .cUsageIdentifier = 0, .rgpszUsageIdentifier = nullptr } },
                                                 .dwUrlRetrievalTimeout = kFetchMilliseconds,
                                                 .fCheckRevocationFreshnessTime = FALSE,
                                                 .dwRevocationFreshnessTime = 0,
                                                 .pftCacheResync = nullptr,
                                                 .pStrongSignPara = nullptr,
                                                 .dwStrongSignFlags = 0 };
                        FILETIME asOf = signer->sftVerifyAsOf;
                        const CERT_CHAIN_CONTEXT* chain = nullptr; // WAIVER(R2): the answer of one call, read once after it.
                        if (::CertGetCertificateChain(HCCE_LOCAL_MACHINE, certificate, &asOf, carried, &request, CERT_CHAIN_ONLY_ADDITIONAL_AND_AUTH_ROOT, nullptr, &chain) == FALSE)
                        {
                            const DWORD error = ::GetLastError();
                            return Fail(error == 0 ? static_cast<DWORD>(TRUST_E_SYSTEM_ERROR) : error);
                        }
                        return UniqueChain(chain);
                    };

                    // Clean is no trust error at all, and the base policy content with that.
                    static constexpr auto Trouble = [] [[nodiscard]] (const CERT_CHAIN_CONTEXT* chain) noexcept -> DWORD {
                        if (chain->TrustStatus.dwErrorStatus != CERT_TRUST_NO_ERROR)
                            return chain->TrustStatus.dwErrorStatus;
                        CERT_CHAIN_POLICY_PARA policy{ .cbSize = sizeof(CERT_CHAIN_POLICY_PARA), .dwFlags = 0, .pvExtraPolicyPara = nullptr }; // WAIVER(R2): by non-const pointer; not written.
                        CERT_CHAIN_POLICY_STATUS status{
                            .cbSize = sizeof(CERT_CHAIN_POLICY_STATUS), .dwError = 0, .lChainIndex = -1, .lElementIndex = -1, .pvExtraPolicyStatus = nullptr
                        }; // WAIVER(R2): the call writes its answer into it.
                        if (::CertVerifyCertificateChainPolicy(CERT_CHAIN_POLICY_BASE, chain, &policy, &status) == FALSE)
                            return static_cast<DWORD>(TRUST_E_FAIL);
                        return status.dwError;
                    };
                    const UniqueStore carried = Carried(provider);
                    if (carried == nullptr)
                        return static_cast<DWORD>(TRUST_E_SYSTEM_ERROR);
                    const Result<UniqueChain, DWORD> chain = Built(signer, certificate, carried.get());
                    if (!chain.has_value())
                        return chain.error();
                    return Trouble(chain->get());
                };
                CRYPT_PROVIDER_DATA* provider = ::WTHelperProvDataFromStateData(state);
                CRYPT_PROVIDER_SGNR* signer = SignerOf(provider);
                CRYPT_PROVIDER_CERT* certificate = CertificateOf(signer);
                if (certificate == nullptr || !NamedNvidia(certificate->pCert))
                    return Found{ Signer::Other, 0 };
                const DWORD trouble = RootTrouble(provider, signer, certificate->pCert);
                if (trouble != 0)
                    return Found{ Signer::NvidiaUnrooted, trouble };
                return Found{ Signer::Nvidia, 0 };
            };

            static constexpr auto Read = [] [[nodiscard]] (const WINTRUST_DATA& request, LONG verdict, ModelKind kind) noexcept -> Result<Signature, Error> {
                if (verdict != ERROR_SUCCESS)
                    return Fail(Error{ RefusalsOf(kind).notSigned, static_cast<std::uint32_t>(verdict) });
                return Signature{ Judged(request.hWVTStateData), request.pSignatureSettings->cSecondarySigs };
            };

            // The verification allocates state that has to be given back whatever the answer was.
            static constexpr auto CloseVerification = [](WINTRUST_DATA& request) noexcept -> void {
                request.dwStateAction = WTD_STATEACTION_CLOSE;
                (void)::WinVerifyTrust(nullptr, &kVerifyAction, &request);
            };
            const LONG verdict = ::WinVerifyTrust(nullptr, &kVerifyAction, &request);
            const Result<Signature, Error> answer = Read(request, verdict, kind);
            CloseVerification(request);
            return answer;
        };

        static constexpr auto Checked = [] [[nodiscard]] (WINTRUST_FILE_INFO * file, DWORD index, DWORD flags, ModelKind kind) noexcept -> Result<Signature, Error> {
            WINTRUST_SIGNATURE_SETTINGS settings = SettingsFor(index, flags); // WAIVER(R2): the call writes the count into it.
            WINTRUST_DATA request = RequestFor(file, &settings);              // WAIVER(R2): the call writes its state into the record it is given.
            return Answered(request, kind);
        };

        // The signatures after the first, each verified on its own. The fold keeps the best signer found so
        // far, and a signature that is not trusted ends it with that refusal.
        static constexpr auto Rest = [] [[nodiscard]] (WINTRUST_FILE_INFO * file, const Signature& first, ModelKind kind) noexcept -> Status<Error> {
            static constexpr auto Best = [] [[nodiscard]] (WINTRUST_FILE_INFO * file, const Signature& first, ModelKind kind) noexcept -> Result<Found, Error> {
                return std::ranges::fold_left(std::views::iota(DWORD{ 1 }, first.secondaries + 1), Result<Found, Error>{ first.found }, [file, kind](Result<Found, Error> best, DWORD index) {
                    return best.and_then(
                        [file, kind, index](const Found& found) { return Checked(file, index, WSS_VERIFY_SPECIFIC, kind).transform([found](const Signature& s) { return Better(found, s.found); }); });
                });
            };

            static constexpr auto Accepted = [] [[nodiscard]] (const Found& best, ModelKind kind) noexcept -> Status<Error> {
                if (best.signer == Signer::Nvidia)
                    return {};
                if (best.signer == Signer::NvidiaUnrooted)
                    return Fail(Error{ RefusalsOf(kind).rootNotTrusted, best.rootError });
                return Fail(Error{ RefusalsOf(kind).notFromNvidia, 0 });
            };
            if (first.secondaries > kMaxSecondarySignatures)
                return Fail(Error{ RefusalsOf(kind).notSigned, kSignaturesUnchecked });
            return Best(file, first, kind).and_then([kind](const Found& best) { return Accepted(best, kind); });
        };
        WINTRUST_FILE_INFO file = FileInfoFor(path, handle);
        return Checked(&file, 0, WSS_VERIFY_SPECIFIC | WSS_GET_SECONDARY_SIG_COUNT, kind).and_then([&file, kind](const Signature& first) { return Rest(&file, first, kind); });
    };
    // The product name is read only of a file that has passed, and says nothing about whether it passed.
    return OpenForReading(path.CString(), kind).and_then([&path, kind](UniqueHandle handle) {
        return Verified(path.CString(), handle.get(), kind).transform([&path, &handle] { return TrustedFile{ std::move(handle), ProductNameOf(path.CString()) }; });
    });
}

Status<Error> PreferSystemLibraries() noexcept
{
    // The record is a union over a bitfield, so its flags are zeroed as one word, which decides every bit
    // (remote images and low-label images allowed, and their audit bits off), and then the one wanted set.
    PROCESS_MITIGATION_IMAGE_LOAD_POLICY policy{}; // WAIVER(R2): a record filled once, before it is handed over.
    policy.Flags = 0;
    policy.PreferSystem32Images = 1;
    if (::SetProcessMitigationPolicy(ProcessImageLoadPolicy, &policy, sizeof(policy)) == FALSE)
        return Fail(LastError(ApiCall::ImageLoadPolicy));
    return {};
}

} // namespace real
