#include "config.h"
#include "state.h"

#include <iterator>

Hotkey LoadInputHotkey()
{
    wchar_t executable[32768]{};
    const DWORD length = GetModuleFileNameW(nullptr, executable, static_cast<DWORD>(std::size(executable)));
    winrt::check_bool(length && length < std::size(executable));
    const std::wstring path = std::wstring(executable).substr(0, std::wstring(executable).find_last_of(L"\\/") + 1)
                              + L"RobloxShadeHost.ini";
    if (GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES)
        winrt::check_bool(WritePrivateProfileStringW(L"Input", L"ToggleKey", g.inputHotkey.c_str(), path.c_str()));

    wchar_t value[128]{};
    const DWORD count = GetPrivateProfileStringW(L"Input", L"ToggleKey", L"Ctrl+Home", value, static_cast<DWORD>(std::size(value)), path.c_str());
    Hotkey hotkey;
    if (count == std::size(value) - 1 || !ParseHotkey(value, hotkey))
    {
        MessageBoxW(nullptr, L"Invalid ToggleKey in RobloxShadeHost.ini. Use a key such as Ctrl+Home or F8. See the README for supported keys.",
                    L"RobloxShadeHost", MB_OK | MB_ICONERROR);
        winrt::throw_hresult(E_INVALIDARG);
    }
    g.inputHotkey = value;
    g.indicatorText = L"Input captured | " + g.inputHotkey + L" to return to Roblox";
    return hotkey;
}
