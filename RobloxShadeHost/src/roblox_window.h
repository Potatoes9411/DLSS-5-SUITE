#pragma once

#include <windows.h>

// Finds the main window of a running Roblox client, or nullptr. Roblox is only observed from outside,
// through process and window enumeration. Nothing is opened, read or loaded into its process.
HWND FindRobloxWindow();
