#pragma once
#include "effects/real/com.h"
#include "infrastructure/bounded_vector.h"
#include "interior/monitors.h"

#include <optional>

namespace real {

struct WindowSettings
{
    bool topmost;
    bool clickThrough;
    bool excludeFromCapture;
    bool redirectionBitmap;
};

struct OutputWindow
{
    UniqueWindow handle;
    interior::ScreenRect rect;
};

// One change SUITE asked for while the session runs: which setting, and what to set it to. They arrive as
// window messages like the capture requests do, and several can land in one frame, so they are kept in the
// order they came in rather than merged.
constexpr std::size_t kMaxTuneRequests = 32;
using TuneRequests = infra::BoundedVector<std::uint64_t, kMaxTuneRequests>;

struct WindowEvents
{
    bool quit;
    bool toggleOriginal;
    bool toggleSplit;
    bool screenshot;
    bool record;
    std::uint64_t comparisonSweep;
    TuneRequests tunes;
};

constexpr int kHotkeyToggleOriginal = 1;
constexpr int kHotkeyToggleSplit = 2;
constexpr int kHotkeyQuit = 3;
constexpr UINT kSuiteScreenshotMessage = WM_APP + 0x451;
constexpr UINT kSuiteRecordMessage = WM_APP + 0x452;
constexpr UINT kSuiteComparisonMessage = WM_APP + 0x453;
// A single setting, so the session does not have to be restarted to change one. lParam carries the field in
// its top byte and the value in its low 32 bits; a float value is that number of ten-thousandths.
constexpr UINT kSuiteTuneMessage = WM_APP + 0x454;
constexpr std::uint64_t kTuneFieldShift = 56;
constexpr std::int32_t kTuneScale = 10000;

enum class TuneField : std::uint8_t {
    None = 0,
    Intensity = 1,
    LocalStructure = 2,
    LocalTone = 3,
    SkinStructure = 4,
    Style = 5,
    AutoMask = 6,
    Passes = 7,
    NeuralRendering = 8,
};

[[nodiscard]] constexpr TuneField FieldOfTune(std::uint64_t word) noexcept { return static_cast<TuneField>(static_cast<std::uint8_t>(word >> kTuneFieldShift)); }

[[nodiscard]] constexpr std::int32_t RawOfTune(std::uint64_t word) noexcept { return static_cast<std::int32_t>(static_cast<std::uint32_t>(word & 0xFFFFFFFFULL)); }

[[nodiscard]] constexpr float FloatOfTune(std::uint64_t word) noexcept { return static_cast<float>(RawOfTune(word)) / static_cast<float>(kTuneScale); }

// Registers a window class, treating "already registered" as success. Shared with the control panel.
[[nodiscard]] infra::Status<Error> RegisterWindowClass(const WNDCLASSEXW& description) noexcept;

// The icon the executable carries, at the two sizes a window class is asked for. Shared with the resource
// it comes from, so neither is freed; nothing at all when the executable carries none, and a class given
// nothing shows the system's default.
[[nodiscard]] HICON LargeAppIcon() noexcept;
[[nodiscard]] HICON SmallAppIcon() noexcept;
// Starts this program again with different arguments and leaves it running; used when the operator asks
// the panel for a session the current one cannot become.
[[nodiscard]] infra::Status<Error> StartProcess(std::wstring_view executable, std::wstring_view arguments) noexcept;

[[nodiscard]] infra::Status<Error> SetDpiAwareness() noexcept;
[[nodiscard]] infra::Result<interior::MonitorList, Error> EnumerateMonitors() noexcept;
// The first visible top-level window whose title contains what was asked for, as a source of its own.
[[nodiscard]] infra::Result<interior::MonitorInfo, Error> FindWindowNamed(const interior::WindowTitle& asked) noexcept;
// The title of a window, for showing which one was picked. Empty once the window has gone.
[[nodiscard]] interior::WindowTitle TitleOfWindow(interior::MonitorHandle window) noexcept;
// The top-level window under a point on the screen, skipping this program's own windows. Nothing over the
// desktop, or over anything of ours.
[[nodiscard]] std::optional<interior::MonitorHandle> WindowUnder(long x, long y) noexcept;
// Where that window is now, so the overlay can follow it. Nothing once the window has gone.
[[nodiscard]] std::optional<interior::ScreenRect> BoundsOfWindow(interior::MonitorHandle window) noexcept;

// Whether a window is still something to capture. Closed, hidden and minimised all answer no, and a
// session following such a window is built again for the monitor its source names.
[[nodiscard]] bool IsWindowShowing(interior::MonitorHandle window) noexcept;
// Moves the overlay so its top-left sits where the given point is. Its size is the session's and stays.
void MoveOutputWindow(const OutputWindow& window, const interior::ScreenRect& rect) noexcept;

// Moves the overlay and puts it directly above `above` in the stack, so a window raised in front of the
// one being worked on covers the overlay too rather than being hidden behind a picture of what it covers.
void MoveOutputWindowAbove(const OutputWindow& window, const interior::ScreenRect& rect, HWND above) noexcept;

// Whether the overlay is already where that call would put it. Asked of the stack each time the window
// being followed is looked at, because being raised over the overlay is what makes the effect vanish.
[[nodiscard]] bool IsOutputWindowPlaced(const OutputWindow& window, const interior::ScreenRect& rect, HWND above) noexcept;

// Puts the overlay directly behind one of our own windows, and asks whether it is there already. The
// overlay covers a whole monitor, so the panel has to be in front of it or it cannot be seen at all.
[[nodiscard]] bool IsOutputWindowBehind(const OutputWindow& window, HWND front) noexcept;
void KeepOutputWindowBehind(const OutputWindow& window, HWND front) noexcept;

// Whether a window has the style that keeps it above every window without it.
[[nodiscard]] bool IsTopmostWindow(HWND window) noexcept;

// Puts the overlay back above everything, for after it was kept behind a window that is not.
void RaiseOutputWindow(const OutputWindow& window) noexcept;

// Puts a window back into every capture on the machine, for when our own capture excludes it by name.
[[nodiscard]] infra::Status<Error> UncoverWindow(HWND window) noexcept;
[[nodiscard]] infra::Result<OutputWindow, Error> CreateOutputWindow(const interior::ScreenRect& rect, const WindowSettings& settings) noexcept;
[[nodiscard]] infra::Status<Error> RegisterHotkeys(const OutputWindow& window) noexcept;
void ShowOutputWindow(const OutputWindow& window) noexcept;
[[nodiscard]] infra::Result<WindowEvents, Error> PumpEvents(const OutputWindow& window) noexcept;

// Changes how the output window behaves: whether the capture sees it, whether it stays above everything,
// and whether the mouse passes through it. The redirection surface is fixed when the window is made.
[[nodiscard]] infra::Status<Error> ApplyWindowSettings(const OutputWindow& window, const WindowSettings& settings) noexcept;

// Where the split divider should sit, or nothing when it is not being dragged. Holding the hotkey
// modifiers and moving the cursor drags it; no button is involved, so the desktop keeps its clicks.
[[nodiscard]] std::optional<interior::Fraction> SplitRequest(const OutputWindow& window) noexcept;

} // namespace real
