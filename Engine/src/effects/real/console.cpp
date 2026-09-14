#include "effects/real/console.h"

#include "global_common.h"

#include <array>
#include <cstring>
#include <string_view>

namespace real {

constexpr std::size_t kMessageCapacity = 512;

namespace {

using infra::Fail;
using infra::Result;
using infra::Status;

[[nodiscard]] const char* Tag(interior::LogLevel level) noexcept
{
    switch (level)
    {
    case interior::LogLevel::Debug: return "[debug] ";
    case interior::LogLevel::Info: return "[info]  ";
    case interior::LogLevel::Warn: return "[warn]  ";
    case interior::LogLevel::Error: return "[error] ";
    }
    return "";
}

[[nodiscard]] bool OpenedConsole(interior::ConsoleMode mode) noexcept
{
    static constexpr auto AttachedToParent = [] [[nodiscard]] () noexcept -> bool { return ::AttachConsole(ATTACH_PARENT_PROCESS) != FALSE; };

    static constexpr auto AllocatedIfAsked = [] [[nodiscard]] (interior::ConsoleMode mode) noexcept -> bool { return mode == interior::ConsoleMode::On && ::AllocConsole() != FALSE; };
    return AttachedToParent() || AllocatedIfAsked(mode);
}

// The standard streams point nowhere until they are aimed at the console just attached.
[[nodiscard]] bool RedirectedToConsole() noexcept
{
    return std::freopen("CONOUT$", "w", stdout) != nullptr && std::freopen("CONOUT$", "w", stderr) != nullptr;
}

} // namespace

Result<Console, Error> OpenConsole(interior::LogLevel minimum, const interior::DirectoryPath& logFile, interior::ConsoleMode mode) noexcept
{
    static constexpr auto Attached = [] [[nodiscard]] (interior::ConsoleMode mode) noexcept -> bool {
        static constexpr auto HasConsole = [] [[nodiscard]] (interior::ConsoleMode mode) noexcept -> bool {
            static constexpr auto WantsConsole = [] [[nodiscard]] (interior::ConsoleMode mode) noexcept -> bool { return mode != interior::ConsoleMode::Off; };
            return WantsConsole(mode) && OpenedConsole(mode);
        };
        return HasConsole(mode) && RedirectedToConsole();
    };

    static constexpr auto OpenMirror = [] [[nodiscard]] (const interior::DirectoryPath& logFile) noexcept -> Result<std::shared_ptr<std::FILE>, Error> {
        static constexpr auto OpenedFile = [] [[nodiscard]] (const interior::DirectoryPath& logFile) noexcept -> Result<std::shared_ptr<std::FILE>, Error> {
            std::FILE* file = _wfopen(logFile.CString(), L"a");
            if (file == nullptr)
                return Fail(Error{ ApiCall::OpenLogFile, static_cast<std::uint32_t>(errno) });
            return std::shared_ptr<std::FILE>(file, FileCloser{});
        };
        if (logFile.IsEmpty())
            return std::shared_ptr<std::FILE>{};
        return OpenedFile(logFile);
    };
    const bool attached = Attached(mode);
    return OpenMirror(logFile).transform([minimum, attached](const std::shared_ptr<std::FILE>& mirror) { return Console{ minimum, mirror, attached }; });
}

void ShowMessage(std::string_view text) noexcept
{
    const infra::BoundedString<char, kMessageCapacity> message = infra::BoundedString<char, kMessageCapacity>::Parse(text).value_or(infra::BoundedString<char, kMessageCapacity>{});
    ::MessageBoxA(nullptr, message.CString(), DSCREEN_PRODUCT_NAME, MB_OK | MB_ICONERROR);
}

Status<Error> Log(const Console& console, interior::LogLevel level, std::string_view text) noexcept
{
    static constexpr auto WriteLine = [] [[nodiscard]] (std::FILE * sink, interior::LogLevel level, std::string_view text) noexcept -> Status<Error> {
        static constexpr auto Flushed = [] [[nodiscard]] (std::FILE * sink) noexcept -> Status<Error> {
            if (std::fflush(sink) != 0)
                return Fail(Error{ ApiCall::WriteLog, 1 });
            return {};
        };
        return WriteText(sink, Tag(level)).and_then([&] { return WriteText(sink, text); }).and_then([&] { return WriteText(sink, "\n"); }).and_then([&] { return Flushed(sink); });
    };

    static constexpr auto WriteMirror = [] [[nodiscard]] (const Console& console, interior::LogLevel level, std::string_view text) noexcept -> Status<Error> {
        if (!console.mirror)
            return {};
        return WriteLine(console.mirror.get(), level, text);
    };

    static constexpr auto WriteConsole = [] [[nodiscard]] (const Console& console, interior::LogLevel level, std::string_view text) noexcept -> Status<Error> {
        static constexpr auto StreamFor = [] [[nodiscard]] (interior::LogLevel level) noexcept -> std::FILE* { return level >= interior::LogLevel::Warn ? stderr : stdout; };
        if (!console.attached)
            return {};
        return WriteLine(StreamFor(level), level, text);
    };
    if (level < console.minimum)
        return {};
    return WriteConsole(console, level, text).and_then([&] { return WriteMirror(console, level, text); });
}

Status<Error> WriteText(std::FILE* sink, std::string_view text) noexcept
{
    const std::size_t written = std::fwrite(text.data(), 1, text.size(), sink);
    if (written != text.size())
        return Fail(Error{ ApiCall::WriteLog, static_cast<std::uint32_t>(written) });
    return {};
}

} // namespace real
