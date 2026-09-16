"""Install the SUITE bridge into a NeuralScreen folder. Safe to run again.

    python apply_bridge.py "<repo>/mod files/neuralscreen"

Each hook is checked before it is added, so a second run changes nothing:
  * startup.py starts the bridge once the command queues exist, and names the
    taskbar button DLSS 5 SUITE;
  * commands.py drains the bridge on the main thread, hands Num2 to SUITE
    (its window is the menu) and saves recordings in the capture folder;
  * tray.py says DLSS 5 SUITE and uses SUITE's icon;
  * the worker keeps the picture just above the captured window in one-window
    mode instead of above everything, and never covers SUITE's window.
The worker change needs native\\build-host.bat afterwards.
"""
import shutil
import sys
from pathlib import Path

root = Path(sys.argv[1])
here = Path(__file__).resolve().parent
shutil.copyfile(here / "suite_bridge.py", root / "suite_bridge.py")
NL = "\n"


def patch(name: str, anchor: str, replacement: str, marker: str) -> None:
    path = root / name
    text = path.read_text(encoding="utf-8")
    if marker in text:
        print(f"{name}: already patched ({marker[:40]})")
        return
    if text.count(anchor) != 1:
        sys.exit(f"{name}: anchor not found exactly once - NeuralScreen changed, update apply_bridge.py ({anchor[:60]!r})")
    path.write_text(text.replace(anchor, replacement), encoding="utf-8", newline="")
    print(f"{name}: patched ({marker[:40]})")


def lines(*parts: str) -> str:
    return NL.join(parts) + NL


# ----- startup.py -----
patch("startup.py",
      lines("    st.shot_dialog_open = False"),
      lines("    st.shot_dialog_open = False",
            "    import suite_bridge  # DLSS 5 SUITE control",
            "    suite_bridge.start()"),
      "suite_bridge.start()")

patch("startup.py",
      lines('    st.taskbar = TaskbarWindow(st.tray_commands, "NeuralScreen")'),
      lines("    import suite_bridge  # DLSS 5 SUITE",
            "    st.taskbar = TaskbarWindow(st.tray_commands, suite_bridge.name())"),
      "TaskbarWindow(st.tray_commands, suite_bridge.name())")

# ----- commands.py -----
patch("commands.py",
      lines("    try:", "        while True:", "            cmd = st.tray_commands.get_nowait()"),
      lines("    import suite_bridge  # DLSS 5 SUITE control",
            "    suite_bridge.drain(st, apply_menu_action)",
            "    try:", "        while True:", "            cmd = st.tray_commands.get_nowait()"),
      "suite_bridge.drain(")

SETTINGS_SUITE = lines(
    '            elif cmd == "settings" and suite_bridge.active():  # DLSS 5 SUITE: Num2 opens SUITE\'s controls',
    '                suite_bridge.notify("controls")',
    '            elif cmd == "settings" and not suite_bridge.active():  # DLSS 5 SUITE: its window is the menu')
if "suite_bridge.active():  # DLSS 5 SUITE: its window is the menu" in (root / "commands.py").read_text(encoding="utf-8"):
    # An earlier bridge blocked Num2 outright; it now opens SUITE's controls.
    patch("commands.py",
          lines('            elif cmd == "settings" and not suite_bridge.active():  # DLSS 5 SUITE: its window is the menu'),
          SETTINGS_SUITE,
          "suite_bridge.notify(")
else:
    patch("commands.py", lines('            elif cmd == "settings":'), SETTINGS_SUITE, "suite_bridge.notify(")

patch("commands.py",
      lines('                    rec_dir = BASE_DIR / "recordings"'),
      lines('                    rec_dir = Path(st.cfg.get("screenshot_dir") or (BASE_DIR / "recordings"))  # DLSS 5 SUITE'),
      'st.cfg.get("screenshot_dir") or (BASE_DIR / "recordings")')

patch("commands.py",
      lines('                        st.recorder = None',
            '                    else:',
            '                        print(f"[main] recording started: {path}")'),
      lines('                        st.recorder = None',
            '                        suite_bridge.notify("recording:off")  # DLSS 5 SUITE',
            '                    else:',
            '                        suite_bridge.notify("recording:on")  # DLSS 5 SUITE',
            '                        print(f"[main] recording started: {path}")'),
      'suite_bridge.notify("recording:on")')

patch("commands.py",
      lines('                    st.recorder = None',
            '            elif cmd == "window_mode":'),
      lines('                    st.recorder = None',
            '                    suite_bridge.notify("recording:off")  # DLSS 5 SUITE: recording stopped',
            '            elif cmd == "window_mode":'),
      'suite_bridge.notify("recording:off")  # DLSS 5 SUITE: recording stopped')

# ----- tray.py -----
patch("tray.py",
      lines("from PIL import Image, ImageDraw"),
      lines("from PIL import Image, ImageDraw", "", "",
            "def _suite_name() -> str:  # DLSS 5 SUITE",
            "    import os",
            '    return "DLSS 5 SUITE" if os.environ.get("NS_SUITE_INBOX") else "NeuralScreen"'),
      "def _suite_name()")
patch("tray.py", 'f"NeuralScreen — NR', 'f"{_suite_name()} — NR', "{_suite_name()} — NR")
patch("tray.py", '"NeuralScreen", self._build_menu())', '_suite_name(), self._build_menu())', "_suite_name(), self._build_menu())")
patch("tray.py",
      lines('    ico = Path(__file__).resolve().parent / "native" / "neuralscreen.ico"'),
      lines('    ico = Path(__file__).resolve().parent / "native" / "neuralscreen.ico"',
            '    if _suite_name() != "NeuralScreen" and (ico.parent / "suite.ico").exists():  # DLSS 5 SUITE',
            '        ico = ico.parent / "suite.ico"'),
      '(ico.parent / "suite.ico").exists()')

# ----- the worker -----
CPP = "native/dlss5-feed-host64.cpp"
patch(CPP,
      lines("static void FollowCapturedWindow()", "{"),
      lines("// DLSS 5 SUITE: set when SUITE runs this worker.",
            "static bool SuiteMode()",
            "{",
            '    static const bool suite = GetEnvironmentVariableW(L"NS_SUITE_INBOX", nullptr, 0) > 0;',
            "    return suite;",
            "}",
            "",
            "// DLSS 5 SUITE: in one-window mode the picture sits directly above the window it",
            "// processes, in the normal z-order, so another program brought forward covers both.",
            "// Topmost windows (our own HUD) are skipped: they are always above everything and",
            "// counting them would restack the picture every frame, which flickers.",
            "static HWND NextOrdinaryWindow(HWND from, UINT direction)",
            "{",
            "    for (HWND next = GetWindow(from, direction); next != nullptr; next = GetWindow(next, direction))",
            "    {",
            "        if (!IsWindowVisible(next)) continue;",
            "        if (GetWindowLongPtrW(next, GWL_EXSTYLE) & WS_EX_TOPMOST) continue;",
            "        return next;",
            "    }",
            "    return nullptr;",
            "}",
            "",
            "static void KeepAboveCapturedWindow()",
            "{",
            "    if (!SuiteMode() || g_present_hwnd == nullptr || g_wgc_hwnd == nullptr) return;",
            "    if (GetWindowLongPtrW(g_present_hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST)",
            "        SetWindowPos(g_present_hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);",
            "    if (NextOrdinaryWindow(g_present_hwnd, GW_HWNDNEXT) == g_wgc_hwnd) return;  // already right above it",
            "    const HWND above = NextOrdinaryWindow(g_wgc_hwnd, GW_HWNDPREV);",
            "    SetWindowPos(g_present_hwnd, above == nullptr ? HWND_TOP : above, 0, 0, 0, 0,",
            "                 SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);",
            "}",
            "",
            "static void FollowCapturedWindow()",
            "{"),
      "static void KeepAboveCapturedWindow()")
patch(CPP,
      lines("        SetWindowPos(g_present_hwnd, HWND_TOPMOST, left, top, w, hgt,",
            "                     SWP_NOACTIVATE);",
            "    }",
            "}"),
      lines("        SetWindowPos(g_present_hwnd, HWND_TOPMOST, left, top, w, hgt,",
            "                     SWP_NOACTIVATE | (SuiteMode() ? SWP_NOZORDER : 0));",
            "    }",
            "    KeepAboveCapturedWindow();  // DLSS 5 SUITE",
            "}"),
      "    KeepAboveCapturedWindow();  // DLSS 5 SUITE")
patch(CPP,
      lines("    if (top == nullptr || top == g_present_hwnd) return;"),
      lines("    if (top == nullptr || top == g_present_hwnd) return;",
            "    if (SuiteMode())  // DLSS 5 SUITE: one-window mode is not topmost, and SUITE's window stays in front",
            "    {",
            "        if (g_wgc_active) return;",
            "        wchar_t title[128];",
            '        if (GetWindowTextW(top, title, 128) > 0 && wcsstr(title, L"DLSS 5 SUITE") != nullptr) return;',
            "    }"),
      "one-window mode is not topmost, and SUITE's window stays in front")
