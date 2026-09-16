"""Install the SUITE bridge into a NeuralScreen folder. Safe to run again.

    python apply_bridge.py "<repo>/mod files/neuralscreen"

Each hook is checked before it is added, so a second run changes nothing:
  * startup.py starts the bridge once the command queues exist, and names the
    taskbar button DLSS 5 SUITE;
  * commands.py drains the bridge on the main thread, hands Num2 to SUITE
    (its window is the menu) and saves recordings in the capture folder;
  * tray.py, taskbar.py and display.py use SUITE's name, icon and palette;
  * pipeline.py hides both output layers while a captured app is not focused;
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

patch("commands.py",
      lines('    elif kind == "frame_multiplier":',
            '        st.cfg["frame_multiplier"] = min(4, max(2, int(action[1])))',
            '        settings_io.save_menu_layout(st)'),
      lines('    elif kind == "frame_multiplier":',
            '        st.cfg["frame_multiplier"] = min(4, max(2, int(action[1])))',
            '        settings_io.save_menu_layout(st)',
            '    elif kind == "passes":  # DLSS 5 SUITE: repeated Neural Rendering passes',
            '        st.cfg["passes"] = min(4, max(1, int(action[1])))',
            '        settings_io.save_menu_layout(st)'),
      'kind == "passes":  # DLSS 5 SUITE')

# ----- protocol.py / main.py: carry pass count in unused frame flag bits -----
patch("protocol.py",
      lines('               frame_generation: bool | None = None, frame_multiplier: int = 2,',
            '               prepared: bool = False) -> None:'),
      lines('               frame_generation: bool | None = None, frame_multiplier: int = 2,',
            '               prepared: bool = False, passes: int = 1) -> None:'),
      "prepared: bool = False, passes: int = 1")
patch("protocol.py",
      lines('    if prepared:',
            '        flags |= FRAME_FLAG_PREPARED'),
      lines('    if prepared:',
            '        flags |= FRAME_FLAG_PREPARED',
            '    # DLSS 5 SUITE: bits 13-14 carry one through four NR passes.',
            '    flags |= (min(4, max(1, int(passes))) - 1) << 13'),
      "bits 13-14 carry one through four NR passes")
patch("main.py",
      lines('                           frame_multiplier=int(st.cfg.get("frame_multiplier", 2)),',
            '                           prepared=bool(st.gray_active))'),
      lines('                           frame_multiplier=int(st.cfg.get("frame_multiplier", 2)),',
            '                           prepared=bool(st.gray_active),',
            '                           passes=int(st.cfg.get("passes", 1)))'),
      'passes=int(st.cfg.get("passes", 1))')

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

# ----- taskbar.py -----
patch("taskbar.py",
      lines("import ctypes"),
      lines("import ctypes", "import os  # DLSS 5 SUITE"),
      "import os  # DLSS 5 SUITE")
patch("taskbar.py",
      lines('        ico = Path(__file__).resolve().parent / "native" / "neuralscreen.ico"'),
      lines('        ico = Path(__file__).resolve().parent / "native" / "neuralscreen.ico"',
            '        suite_ico = ico.parent / "suite.ico"  # DLSS 5 SUITE',
            '        if os.environ.get("NS_SUITE_INBOX") and suite_ico.is_file():',
            '            ico = suite_ico'),
      "suite_ico = ico.parent")

# ----- display.py -----
patch("display.py",
      lines('        pygame.display.set_caption("NeuralScreen")'),
      lines('        pygame.display.set_caption("DLSS 5 SUITE" if os.environ.get("NS_SUITE_INBOX") else "NeuralScreen")'),
      'pygame.display.set_caption("DLSS 5 SUITE"')
patch("display.py",
      lines('        self._visible = False',
            '        self._reveal_pending = True'),
      lines('        self._visible = False',
            '        self._focus_visible = True  # DLSS 5 SUITE: captured app owns visibility',
            '        self._reveal_pending = True'),
      "self._focus_visible = True")
patch("display.py",
      lines('        return ui_palette(self.menu.state.get("theme", "light"))'),
      lines('        if os.environ.get("NS_SUITE_INBOX"):  # DLSS 5 SUITE dark-green glass palette',
            '            return {"bg": "#07110C", "surface": "#101B14", "border": "#2DD43A",',
            '                    "text": "#F8FAFC", "muted": "#A3B8AC", "accent": "#35D22B",',
            '                    "ok": "#4ADE80", "danger": "#F87171"}',
            '        return ui_palette(self.menu.state.get("theme", "light"))'),
      "DLSS 5 SUITE dark-green glass palette")
patch("display.py",
      lines('        if visible and self._reveal_pending:'),
      lines('        if visible and not getattr(self, "_focus_visible", True):  # DLSS 5 SUITE',
            '            return',
            '        if visible and self._reveal_pending:'),
      "visible and not getattr(self, \"_focus_visible\"")
patch("display.py",
      lines('    def reveal(self) -> None:'),
      lines('    def set_focus_visible(self, visible: bool) -> None:  # DLSS 5 SUITE',
            '        """Hide the HUD while another app owns the foreground."""',
            '        visible = bool(visible)',
            '        if visible == getattr(self, "_focus_visible", True):',
            '            return',
            '        self._focus_visible = visible',
            '        self.set_visible(visible)',
            '',
            '    def reveal(self) -> None:'),
      "def set_focus_visible(self, visible: bool)")
patch("display.py",
      lines('                user32.ShowWindow(hwnd, 5)  # SW_SHOW',
            '                self._visible = True',
            '            finally:',
            '                self._reveal_pending = False'),
      lines('                if getattr(self, "_focus_visible", True):  # DLSS 5 SUITE',
            '                    user32.ShowWindow(hwnd, 5)  # SW_SHOW',
            '                    self._visible = True',
            '                else:',
            '                    self._visible = False',
            '            finally:',
            '                self._reveal_pending = False'),
      "if getattr(self, \"_focus_visible\", True):  # DLSS 5 SUITE")
patch("display.py",
      lines('            present = user32.FindWindowW("NeuralScreenPresent", "NeuralScreen")'),
      lines('            present_title = "DLSS 5 SUITE" if os.environ.get("NS_SUITE_INBOX") else "NeuralScreen"',
            '            present = user32.FindWindowW("NeuralScreenPresent", present_title)'),
      "present_title = \"DLSS 5 SUITE\"")

# ----- pipeline.py -----
patch("pipeline.py",
      lines('    x, y, w, h = rect',
            '    if ctypes.windll.user32.IsIconic(ctypes.c_void_p(st.window_hwnd)):'),
      lines('    x, y, w, h = rect',
            '    if os.environ.get("NS_SUITE_INBOX"):',
            '        focused = int(ctypes.windll.user32.GetForegroundWindow() or 0) == int(st.window_hwnd)',
            '        st.display.set_focus_visible(focused)',
            '        if not focused:',
            '            st.follow_pos = None',
            '            return',
            '    if ctypes.windll.user32.IsIconic(ctypes.c_void_p(st.window_hwnd)):'),
      "st.display.set_focus_visible(focused)")

# ----- the worker -----
CPP = "native/dlss5-feed-host64.cpp"
patch(CPP,
      lines("static constexpr uint32_t FRAME_FLAG_PREPARED = 0x1000u;"),
      lines("static constexpr uint32_t FRAME_FLAG_PREPARED = 0x1000u;",
            "static constexpr uint32_t FRAME_PASS_SHIFT = 13u;  // DLSS 5 SUITE",
            "static constexpr uint32_t FRAME_PASS_MASK = 0x6000u;"),
      "static constexpr uint32_t FRAME_PASS_SHIFT")
patch(CPP,
      lines("    ID3D12Resource *nr_out = nullptr;   // UNORDERED_ACCESS at rest"),
      lines("    ID3D12Resource *nr_out = nullptr;   // UNORDERED_ACCESS at rest",
            "    ID3D12Resource *nr_repeat = nullptr; // ping-pong target for passes 2-4"),
      "ping-pong target for passes 2-4")
patch(CPP,
      lines("    UINT64 total = 0;", "    h.dev->GetCopyableFootprints(&td, 0, 1, 0, &v.out_fp, &v.out_rows, &v.out_row_size, &total);"),
      lines("    // DLSS 5 SUITE: repeated passes use a second work-sized target, so an",
            "    // evaluate never reads and writes the same D3D12 resource.",
            "    D3D12_RESOURCE_DESC repeat_desc = td;",
            "    repeat_desc.Width = v.nr_small ? v.nr_w : cw;",
            "    repeat_desc.Height = v.nr_small ? v.nr_h : ch;",
            "    if (FAILED(h.dev->CreateCommittedResource(",
            "        &def, D3D12_HEAP_FLAG_NONE, &repeat_desc,",
            "        D3D12_RESOURCE_STATE_UNORDERED_ACCESS, nullptr,",
            "        __uuidof(ID3D12Resource), reinterpret_cast<void **>(&v.nr_repeat))))",
            "        return false;",
            "",
            "    UINT64 total = 0;",
            "    h.dev->GetCopyableFootprints(&td, 0, 1, 0, &v.out_fp, &v.out_rows, &v.out_row_size, &total);"),
      "D3D12_RESOURCE_DESC repeat_desc = td")
patch(CPP,
      lines("static bool EvaluateVideo(VideoState &v, int reset, UINT64 *submitted = nullptr)"),
      lines("static bool EvaluateVideo(VideoState &v, int reset, UINT64 *submitted = nullptr,",
            "                          UINT passes = 1)"),
      "UINT passes = 1)")
patch(CPP,
      lines(
          "    ID3D12Resource *nr_color = v.nr_small ? v.nr_in : v.color.tex;",
          "    ID3D12Resource *nr_result = v.nr_small ? v.nr_out : v.output;",
          "    h.params->Reset();",
          '    h.params->Set("DLSSNR.Color", nr_color); h.params->Set("DLSSNR.Output", nr_result);',
          '    h.params->Set("DLSSNR.MVec", v.mv.tex);',
          '    h.params->Set("DLSSNR.ColorSubrectBaseX", 0u); h.params->Set("DLSSNR.ColorSubrectBaseY", 0u);',
          '    h.params->Set("DLSSNR.ColorSubrectWidth", nw); h.params->Set("DLSSNR.ColorSubrectHeight", nh);',
          '    h.params->Set("DLSSNR.MVecSubrectBaseX", 0u); h.params->Set("DLSSNR.MVecSubrectBaseY", 0u);',
          '    h.params->Set("DLSSNR.MVecSubrectWidth", v.w); h.params->Set("DLSSNR.MVecSubrectHeight", v.hgt);',
          '    h.params->Set("DLSSNR.OutputSubrectBaseX", 0u); h.params->Set("DLSSNR.OutputSubrectBaseY", 0u);',
          '    h.params->Set("DLSSNR.OutputSubrectWidth", nw); h.params->Set("DLSSNR.OutputSubrectHeight", nh);',
          '    h.params->Set("DLSSNR.MVecScaleX", v.nr_small ? float(nw)/v.w : 1.0f);',
          '    h.params->Set("DLSSNR.MVecScaleY", v.nr_small ? float(nh)/v.hgt : 1.0f);',
          '    h.params->Set("DLSSNR.Enabled", 1u); h.params->Set("DLSSNR.Reset", reset);',
          '    h.params->Set("DLSSNR.Intensity", g_video_options.intensity);',
          '    h.params->Set("DLSSNR.LocalToneStrength", g_video_options.local_tone);',
          '    h.params->Set("DLSSNR.LocalStructureStrength", g_video_options.local_structure);',
          '    h.params->Set("DLSSNR.SkinStructureStrength", g_video_options.skin_structure);',
          '    h.params->Set("DLSSNR.UseAutoMask", g_video_options.auto_mask);',
          '    h.params->Set("DLSSNR.Style", g_video_options.style);',
          '    h.params->Set("DLSSNR.UICorrection", g_video_options.ui_correction);',
          '    h.params->Set("DLSS.Pre.Exposure", 1.0f);',
          '    h.params->Set("DLSS.Exposure.Scale", g_pw_exposure);',
          "    DWORD code = 0;",
          "    NVSDK_NGX_Result result = static_cast<NVSDK_NGX_Result>(0x7FFFFFFF);",
          "    __try { result = g_nr_evaluate(h.list, h.feature, h.params, nullptr); }",
          "    __except (EXCEPTION_EXECUTE_HANDLER) { code = GetExceptionCode(); }",
          "    g_last_eval_result = static_cast<uint32_t>(result);"),
      lines(
          "    passes = (std::min)(4u, (std::max)(1u, passes));  // DLSS 5 SUITE",
          "    ID3D12Resource *final_result = v.nr_small ? v.nr_out : v.output;",
          "    ID3D12Resource *nr_color = v.nr_small ? v.nr_in : v.color.tex;",
          "    ID3D12Resource *nr_result = (passes % 2u == 0u) ? v.nr_repeat : final_result;",
          "    bool final_is_srv = false;",
          "    bool repeat_is_srv = false;",
          "    DWORD code = 0;",
          "    NVSDK_NGX_Result result = static_cast<NVSDK_NGX_Result>(0x7FFFFFFF);",
          "    for (UINT pass = 0; pass < passes; ++pass)",
          "    {",
          "        h.params->Reset();",
          '        h.params->Set("DLSSNR.Color", nr_color); h.params->Set("DLSSNR.Output", nr_result);',
          '        h.params->Set("DLSSNR.MVec", v.mv.tex);',
          '        h.params->Set("DLSSNR.ColorSubrectBaseX", 0u); h.params->Set("DLSSNR.ColorSubrectBaseY", 0u);',
          '        h.params->Set("DLSSNR.ColorSubrectWidth", nw); h.params->Set("DLSSNR.ColorSubrectHeight", nh);',
          '        h.params->Set("DLSSNR.MVecSubrectBaseX", 0u); h.params->Set("DLSSNR.MVecSubrectBaseY", 0u);',
          '        h.params->Set("DLSSNR.MVecSubrectWidth", v.w); h.params->Set("DLSSNR.MVecSubrectHeight", v.hgt);',
          '        h.params->Set("DLSSNR.OutputSubrectBaseX", 0u); h.params->Set("DLSSNR.OutputSubrectBaseY", 0u);',
          '        h.params->Set("DLSSNR.OutputSubrectWidth", nw); h.params->Set("DLSSNR.OutputSubrectHeight", nh);',
          '        h.params->Set("DLSSNR.MVecScaleX", v.nr_small ? float(nw)/v.w : 1.0f);',
          '        h.params->Set("DLSSNR.MVecScaleY", v.nr_small ? float(nh)/v.hgt : 1.0f);',
          '        h.params->Set("DLSSNR.Enabled", 1u);',
          '        h.params->Set("DLSSNR.Reset", pass == 0 ? reset : 1);',
          '        h.params->Set("DLSSNR.Intensity", g_video_options.intensity);',
          '        h.params->Set("DLSSNR.LocalToneStrength", g_video_options.local_tone);',
          '        h.params->Set("DLSSNR.LocalStructureStrength", g_video_options.local_structure);',
          '        h.params->Set("DLSSNR.SkinStructureStrength", g_video_options.skin_structure);',
          '        h.params->Set("DLSSNR.UseAutoMask", g_video_options.auto_mask);',
          '        h.params->Set("DLSSNR.Style", g_video_options.style);',
          '        h.params->Set("DLSSNR.UICorrection", g_video_options.ui_correction);',
          '        h.params->Set("DLSS.Pre.Exposure", 1.0f);',
          '        h.params->Set("DLSS.Exposure.Scale", g_pw_exposure);',
          "        __try { result = g_nr_evaluate(h.list, h.feature, h.params, nullptr); }",
          "        __except (EXCEPTION_EXECUTE_HANDLER) { code = GetExceptionCode(); }",
          "        g_last_eval_result = static_cast<uint32_t>(result);",
          "        if (code != 0 || NVSDK_NGX_FAILED(result)) break;",
          "        if (pass + 1u < passes)",
          "        {",
          "            D3D12_RESOURCE_BARRIER to_srv = Transition(",
          "                nr_result, D3D12_RESOURCE_STATE_UNORDERED_ACCESS,",
          "                D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);",
          "            h.list->ResourceBarrier(1, &to_srv);",
          "            if (nr_result == final_result) final_is_srv = true;",
          "            else repeat_is_srv = true;",
          "            ID3D12Resource *next_result =",
          "                nr_result == final_result ? v.nr_repeat : final_result;",
          "            bool &next_is_srv =",
          "                nr_result == final_result ? repeat_is_srv : final_is_srv;",
          "            if (next_is_srv)",
          "            {",
          "                D3D12_RESOURCE_BARRIER to_uav = Transition(",
          "                    next_result, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,",
          "                    D3D12_RESOURCE_STATE_UNORDERED_ACCESS);",
          "                h.list->ResourceBarrier(1, &to_uav);",
          "                next_is_srv = false;",
          "            }",
          "            nr_color = nr_result;",
          "            nr_result = next_result;",
          "        }",
          "    }",
          "    if (repeat_is_srv)",
          "    {",
          "        D3D12_RESOURCE_BARRIER repeat_back = Transition(",
          "            v.nr_repeat, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,",
          "            D3D12_RESOURCE_STATE_UNORDERED_ACCESS);",
          "        h.list->ResourceBarrier(1, &repeat_back);",
          "    }"),
      "passes = (std::min)(4u, (std::max)(1u, passes))")
patch(CPP,
      lines("    if (v.nr_out != nullptr) { v.nr_out->Release(); v.nr_out = nullptr; }"),
      lines("    if (v.nr_out != nullptr) { v.nr_out->Release(); v.nr_out = nullptr; }",
            "    if (v.nr_repeat != nullptr) { v.nr_repeat->Release(); v.nr_repeat = nullptr; }"),
      "v.nr_repeat->Release()")
patch(CPP,
      lines("            const bool ev_ok = EvaluateVideo(v, (frame == 0 || fh.reset != 0) ? 1 : 0,",
            "                                             defer_tail ? &eval_done : nullptr);"),
      lines("            const UINT passes = 1u + ((fh.reserved & FRAME_PASS_MASK) >> FRAME_PASS_SHIFT);",
            "            const bool ev_ok = EvaluateVideo(v, (frame == 0 || fh.reset != 0) ? 1 : 0,",
            "                                             defer_tail ? &eval_done : nullptr, passes);"),
      "fh.reserved & FRAME_PASS_MASK")
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
      lines("static void FollowCapturedWindow()", "{",
            "    if (!g_wgc_active || g_present_hwnd == nullptr || g_wgc_hwnd == nullptr) return;"),
      lines("static bool CapturedWindowIsForeground()  // DLSS 5 SUITE", "{",
            "    if (!SuiteMode() || !g_wgc_active || g_wgc_hwnd == nullptr) return true;",
            "    return GetForegroundWindow() == g_wgc_hwnd;",
            "}", "",
            "static void FollowCapturedWindow()", "{",
            "    if (!g_wgc_active || g_present_hwnd == nullptr || g_wgc_hwnd == nullptr) return;",
            "    if (!CapturedWindowIsForeground())",
            "    {",
            "        if (g_present_shown)",
            "        {",
            "            ShowWindow(g_present_hwnd, SW_HIDE);",
            "            g_present_shown = false;",
            "        }",
            "        return;",
            "    }"),
      "static bool CapturedWindowIsForeground()")
patch(CPP,
      lines("    if (g_present_hwnd == nullptr || g_present_revealed) return;",
            "    ShowWindow(g_present_hwnd, SW_SHOWNOACTIVATE);"),
      lines("    if (g_present_hwnd == nullptr || g_present_revealed) return;",
            "    if (!CapturedWindowIsForeground()) return;  // DLSS 5 SUITE",
            "    ShowWindow(g_present_hwnd, SW_SHOWNOACTIVATE);"),
      "if (!CapturedWindowIsForeground()) return;")
patch(CPP,
      lines('        wc.lpszClassName, L"NeuralScreen", WS_POPUP,'),
      lines('        wc.lpszClassName, GetEnvironmentVariableW(L"NS_SUITE_INBOX", nullptr, 0) > 0 ? L"DLSS 5 SUITE" : L"NeuralScreen", WS_POPUP,'),
      'L"DLSS 5 SUITE" : L"NeuralScreen", WS_POPUP')
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
