"""
remove_protection_cli.py

Legacy stub kept for fail-safe operation. Protection removal is now
managed exclusively from the DAAMS Control Center.

If an older Explorer context menu or shortcut invokes this script, it
informs the user that removal can only be done from the Control Center
and exits safely without modifying any assets or prompting for PIN.
"""

import os
import sys
import traceback
from datetime import datetime

_ENGINE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
_LOG = os.path.join(_ENGINE_DIR, "logs", "daams_cli_debug.log")
if getattr(sys, "frozen", False):
    # Installed build: the program folder is read-only, so log next to the engine's other logs.
    _LOG = os.path.join(
        os.environ.get("DAAMS_HOME") or os.path.join(os.environ.get("LOCALAPPDATA", ""), "DAAMS"),
        "logs", "daams_cli_debug.log",
    )
if _ENGINE_DIR not in sys.path:
    sys.path.insert(0, _ENGINE_DIR)


def _log(msg):
    try:
        os.makedirs(os.path.dirname(_LOG), exist_ok=True)
        with open(_LOG, "a", encoding="utf-8") as f:
            f.write(f"{datetime.now():%Y-%m-%d %H:%M:%S}  {msg}\n")
    except OSError:
        pass


def _show_fatal(err: str) -> None:
    """Best-effort visible error; falls back to a native box if glass_ui can't load."""
    summary = err.strip().splitlines()[-1] if err.strip() else "Unknown error"
    try:
        from ui import glass_ui
        glass_ui.show_message(
            "error", "Unexpected error",
            "Something went wrong. Full details were saved to daams_cli_debug.log.",
            detail=summary,
        )
    except BaseException:
        try:
            import tkinter as tk
            from tkinter import messagebox
            root = tk.Tk()
            root.withdraw()
            messagebox.showerror("DAAMS - Unexpected Error", err[-800:], parent=root)
            root.destroy()
        except BaseException:
            pass


_log(f"START (remove) argv={sys.argv} exe={sys.executable} cwd={os.getcwd()}")

try:
    from ui import glass_ui
except BaseException:
    _err = traceback.format_exc()
    _log("IMPORT FAILED\n" + _err)
    _show_fatal(_err)
    raise

_log("imports finished")


def main():
    glass_ui.show_message(
        "info",
        "Managed from the Control Center",
        "Protection can only be removed from the DAAMS Control Center.",
    )
    return


if __name__ == "__main__":
    try:
        main()
        _log("END ok")
    except SystemExit:
        raise
    except BaseException:
        err = traceback.format_exc()
        _log(err)
        _show_fatal(err)
        raise
