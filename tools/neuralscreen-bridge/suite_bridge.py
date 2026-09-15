"""DLSS 5 SUITE control for NeuralScreen.

SUITE is NeuralScreen's control panel when the two ship together. It drops one
JSON file per request into the folder named by NS_SUITE_INBOX (written to a
temporary name and renamed, so a file is never read half-written). A thread
here picks them up; the main loop hands them to the same code the overlay menu,
the tray and the hotkeys already use, so nothing is decided twice.

    {"action": ["param", "intensity", 0.8]}   what the menu would report
    {"command": "record"}                      what a hotkey would push
    {"shot": "C:/Captures/shot.png"}           a screenshot, no dialog

Without NS_SUITE_INBOX this does nothing and NeuralScreen runs as it always has.
"""
from __future__ import annotations

import json
import os
import queue
import sys
import threading
import time
from pathlib import Path

_pending: queue.Queue = queue.Queue()


def active() -> bool:
    """SUITE is the control panel: NeuralScreen's own menu stays shut."""
    return bool(os.environ.get("NS_SUITE_INBOX"))


SUITE_NAME = "DLSS 5 SUITE"


def name(default: str = "NeuralScreen") -> str:
    """What the program calls itself: DLSS 5 SUITE when SUITE runs it."""
    return SUITE_NAME if active() else default


def _brand() -> None:
    """Every on-screen string that names the program names DLSS 5 SUITE."""
    try:
        import i18n
        for strings in i18n.STRINGS.values():
            for key, value in list(strings.items()):
                if isinstance(value, str) and "NeuralScreen" in value:
                    strings[key] = value.replace("NeuralScreen", SUITE_NAME)
    except Exception as exc:
        print(f"[suite] branding skipped: {exc}", file=sys.stderr)


def notify(event: str) -> None:
    """Tell SUITE something happened here - Num2 asks for its control window."""
    outbox = os.environ.get("NS_SUITE_OUTBOX")
    if not outbox:
        return
    try:
        folder = Path(outbox)
        folder.mkdir(parents=True, exist_ok=True)
        stamp = f"{time.time_ns():020d}"
        tmp = folder / f"{stamp}.tmp"
        tmp.write_text(json.dumps({"event": event}), encoding="utf-8")
        os.replace(tmp, folder / f"{stamp}.json")
    except Exception as exc:
        print(f"[suite] could not tell SUITE {event!r}: {exc}", file=sys.stderr)


def start() -> None:
    inbox = os.environ.get("NS_SUITE_INBOX")
    if not inbox:
        return
    folder = Path(inbox)
    folder.mkdir(parents=True, exist_ok=True)
    _brand()
    # SUITE empties the folder before it starts NeuralScreen, so anything here
    # was sent for this run - the window to capture, NR off - and is kept.
    threading.Thread(target=_poll, args=(folder,), name="suite-bridge", daemon=True).start()
    print(f"[suite] taking requests from {folder}")


def _poll(folder: Path) -> None:
    while True:
        try:
            for request in sorted(folder.glob("*.json")):
                try:
                    _pending.put(json.loads(request.read_text(encoding="utf-8")))
                except Exception as exc:
                    print(f"[suite] unreadable request {request.name}: {exc}", file=sys.stderr)
                finally:
                    request.unlink(missing_ok=True)
        except Exception as exc:
            print(f"[suite] inbox error: {exc}", file=sys.stderr)
        time.sleep(0.03)


def drain(st, apply_menu_action) -> None:
    """Run the waiting requests. Main thread only: the pipeline is not shared."""
    while True:
        try:
            request = _pending.get_nowait()
        except queue.Empty:
            return
        try:
            if "action" in request:
                apply_menu_action(st, tuple(request["action"]))
            elif "command" in request:
                st.tray_commands.put(str(request["command"]))
            elif "shot" in request:
                target = Path(request["shot"])
                target.parent.mkdir(parents=True, exist_ok=True)
                st.shot_paths.put(target)
        except Exception as exc:
            print(f"[suite] request {request!r} failed: {exc}", file=sys.stderr)
