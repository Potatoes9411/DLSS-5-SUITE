"""Install the SUITE bridge into a NeuralScreen folder. Safe to run again.

    python apply_bridge.py "<repo>/mod files/neuralscreen"

Three small hooks, each checked before it is added:
  * startup.py starts the bridge once the command queues exist;
  * commands.py drains it at the top of drain_commands (the main thread);
  * recordings go to the configured screenshot folder when one is set, so
    SUITE's capture folder holds both.
"""
import shutil
import sys
from pathlib import Path

root = Path(sys.argv[1])
here = Path(__file__).resolve().parent
shutil.copyfile(here / "suite_bridge.py", root / "suite_bridge.py")


def patch(name: str, anchor: str, replacement: str, marker: str) -> None:
    path = root / name
    text = path.read_text(encoding="utf-8")
    if marker in text:
        print(f"{name}: already patched")
        return
    if text.count(anchor) != 1:
        sys.exit(f"{name}: anchor not found exactly once - NeuralScreen changed, update apply_bridge.py")
    path.write_text(text.replace(anchor, replacement), encoding="utf-8", newline="")
    print(f"{name}: patched")


patch("startup.py",
      "    st.shot_dialog_open = False\n",
      "    st.shot_dialog_open = False\n    import suite_bridge  # DLSS 5 SUITE control\n    suite_bridge.start()\n",
      "suite_bridge.start()")

patch("commands.py",
      "    try:\n        while True:\n            cmd = st.tray_commands.get_nowait()\n",
      "    import suite_bridge  # DLSS 5 SUITE control\n    suite_bridge.drain(st, apply_menu_action)\n"
      "    try:\n        while True:\n            cmd = st.tray_commands.get_nowait()\n",
      "suite_bridge.drain(")

patch("commands.py",
      '            elif cmd == "settings":\n',
      '            elif cmd == "settings" and not suite_bridge.active():  # DLSS 5 SUITE: its window is the menu\n',
      "suite_bridge.active()")

patch("commands.py",
      '                    rec_dir = BASE_DIR / "recordings"\n',
      '                    rec_dir = Path(st.cfg.get("screenshot_dir") or (BASE_DIR / "recordings"))  # DLSS 5 SUITE\n',
      "# DLSS 5 SUITE\n")
