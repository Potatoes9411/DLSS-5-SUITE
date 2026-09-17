#include "roblox_window.h"

#include <tlhelp32.h>

#include <algorithm>
#include <vector>

namespace
{
constexpr wchar_t kRobloxExe[] = L"RobloxPlayerBeta.exe";
}

HWND FindRobloxWindow()
{
    std::vector<DWORD> pids;
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE)
        return nullptr;
    PROCESSENTRY32W entry{ sizeof(entry) };
    for (BOOL ok = Process32FirstW(snapshot, &entry); ok; ok = Process32NextW(snapshot, &entry))
    {
        if (_wcsicmp(entry.szExeFile, kRobloxExe) == 0)
            pids.push_back(entry.th32ProcessID);
    }
    CloseHandle(snapshot);
    if (pids.empty())
        return nullptr;

    struct Search
    {
        const std::vector<DWORD>& pids;
        HWND found = nullptr;
    } search{ pids };

    EnumWindows(
        [](HWND hwnd, LPARAM param) -> BOOL {
            auto& search = *reinterpret_cast<Search*>(param);
            DWORD pid = 0;
            GetWindowThreadProcessId(hwnd, &pid);
            if (std::find(search.pids.begin(), search.pids.end(), pid) == search.pids.end())
                return TRUE;
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) || (GetWindowLongPtrW(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW))
                return TRUE;
            search.found = hwnd;
            return FALSE;
        },
        reinterpret_cast<LPARAM>(&search));

    return search.found;
}
