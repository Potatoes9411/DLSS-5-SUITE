#pragma once

#include <windows.h>
#include <cwctype>
#include <string>
#include <string_view>

struct Hotkey
{
    UINT modifiers = MOD_NOREPEAT;
    UINT key = 0;
};

inline bool ParseHotkey(std::wstring_view text, Hotkey& result)
{
    Hotkey parsed;
    while (!text.empty())
    {
        const auto separator = text.find(L'+');
        std::wstring token(text.substr(0, separator));
        const auto first = token.find_first_not_of(L" \t");
        if (first == std::wstring::npos)
            return false;
        token = token.substr(first, token.find_last_not_of(L" \t") - first + 1);
        for (auto& character : token)
            character = static_cast<wchar_t>(std::towupper(character));

        UINT modifier = 0;
        if (token == L"CTRL") modifier = MOD_CONTROL;
        else if (token == L"ALT") modifier = MOD_ALT;
        else if (token == L"SHIFT") modifier = MOD_SHIFT;
        else if (token == L"WIN") modifier = MOD_WIN;

        if (modifier)
        {
            if (parsed.key || (parsed.modifiers & modifier))
                return false;
            parsed.modifiers |= modifier;
        }
        else
        {
            if (parsed.key)
                return false;
            if (token.size() == 1 && ((token[0] >= L'A' && token[0] <= L'Z') || (token[0] >= L'0' && token[0] <= L'9')))
                parsed.key = token[0];
            else if (token[0] == L'F' && token.size() >= 2 && token.size() <= 3)
            {
                unsigned number = 0;
                for (size_t i = 1; i < token.size(); ++i)
                {
                    if (token[i] < L'0' || token[i] > L'9')
                        return false;
                    number = number * 10 + token[i] - L'0';
                }
                // Windows reserves F12 for the debugger.
                if (number < 1 || number > 24 || number == 12)
                    return false;
                parsed.key = VK_F1 + number - 1;
            }
            else
            {
                const struct { const wchar_t* name; UINT key; } keys[] = {
                    { L"HOME", VK_HOME }, { L"END", VK_END }, { L"INSERT", VK_INSERT },
                    { L"DELETE", VK_DELETE }, { L"PAGEUP", VK_PRIOR }, { L"PAGEDOWN", VK_NEXT },
                    { L"PAUSE", VK_PAUSE }, { L"SCROLLLOCK", VK_SCROLL }, { L"SPACE", VK_SPACE },
                    { L"TAB", VK_TAB }, { L"ESCAPE", VK_ESCAPE },
                };
                for (const auto& named : keys)
                    if (token == named.name)
                        parsed.key = named.key;
                if (!parsed.key)
                    return false;
            }
        }
        if (separator == std::wstring_view::npos)
            break;
        text.remove_prefix(separator + 1);
        if (text.empty())
            return false;
    }
    if (!parsed.key)
        return false;
    result = parsed;
    return true;
}
