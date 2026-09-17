#pragma once

#include "hotkey.h"

// Reads ToggleKey from RobloxShadeHost.ini beside the exe, creating the file with the default on first run.
// Shows an error and throws when the value cannot be parsed.
Hotkey LoadInputHotkey();
