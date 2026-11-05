"""
main.py

Entry point for the DAAMS Python Monitoring Engine.

Usage:
    python main.py

Configuration (Phase 4):
    There is no longer a single PROTECTED_PATH to edit here. Instead,
    right-click any file or folder in Windows Explorer and choose
    DAAMS -> Protect This File / Protect This Folder to register it in
    the Protected Asset Registry (see asset_registry.py). This script
    then monitors everything currently marked "protected" in that
    registry.
"""

import os
import sys
import traceback

# --- No-console build guard -------------------------------------------------
# When packaged with PyInstaller --noconsole, sys.stdout / sys.stderr are None,
# so any print() or stream call could crash. Redirect them to a log file.
if getattr(sys, "frozen", False):
    _log_dir = os.path.join(os.environ.get("LOCALAPPDATA", "."), "DAAMS", "logs")
    os.makedirs(_log_dir, exist_ok=True)
    _console_log = open(
        os.path.join(_log_dir, "engine_console.log"),
        "a", buffering=1, encoding="utf-8",
    )
    if sys.stdout is None:
        sys.stdout = _console_log
    if sys.stderr is None:
        sys.stderr = _console_log

# --- Hide child-process console windows ---------------------------------------
# A --noconsole exe has no console, so every console program the engine starts
# (powershell, wevtutil, auditpol, icacls, ...) opens its own flashing window.
# Make every subprocess.Popen start with CREATE_NO_WINDOW by default.
if getattr(sys, "frozen", False) and os.name == "nt":
    import subprocess

    _CREATE_NO_WINDOW = 0x08000000
    _orig_popen_init = subprocess.Popen.__init__

    def _popen_init_no_window(self, *args, **kwargs):
        kwargs["creationflags"] = kwargs.get("creationflags", 0) | _CREATE_NO_WINDOW
        _orig_popen_init(self, *args, **kwargs)

    subprocess.Popen.__init__ = _popen_init_no_window
# ---------------------------------------------------------------------------

_ENGINE_DIR = os.path.dirname(os.path.abspath(__file__))
if _ENGINE_DIR not in sys.path:
    sys.path.insert(0, _ENGINE_DIR)

from core.monitor import start_monitoring


def main():
    try:
        start_monitoring()
    except Exception as e:
        # Catch-all so unexpected errors are visible and explainable
        # during testing/defense, instead of a raw traceback only.
        print(f"[UNEXPECTED ERROR] {e}")
        traceback.print_exc()  # full trace goes to engine_console.log when hidden
        sys.exit(1)


if __name__ == "__main__":
    import multiprocessing
    multiprocessing.freeze_support()  # needed if anything uses multiprocessing in the frozen exe
    main()
