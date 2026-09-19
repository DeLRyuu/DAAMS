"""
remove_protection_cli.py

Launched by the DAAMS Windows Explorer context menu when the administrator
right-clicks an already-protected file or folder and selects:
    DAAMS -> Remove Protection

Confirms with the administrator, then calls asset_registry.remove_protection(),
which ONLY updates registry metadata (status -> "unprotected"). It NEVER
deletes the actual file/folder, and NEVER deletes any prior activity_log
history for that asset.

All windows use the shared glass look from glass_ui.py.

Usage (this is what the registered context-menu command runs):
    pythonw.exe remove_protection_cli.py "C:\\path\\to\\selected\\item"

Debugging: every run appends to daams_cli_debug.log (next to this file),
same as protect_asset_cli.py, since pythonw.exe has no console.
"""

import os
import sys
import traceback
from datetime import datetime

_LOG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "daams_cli_debug.log")


def _log(msg):
    try:
        with open(_LOG, "a", encoding="utf-8") as f:
            f.write(f"{datetime.now():%Y-%m-%d %H:%M:%S}  {msg}\n")
    except OSError:
        pass


def _show_fatal(err: str) -> None:
    """Best-effort visible error; falls back to a native box if glass_ui can't load."""
    summary = err.strip().splitlines()[-1] if err.strip() else "Unknown error"
    try:
        import glass_ui
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
    import asset_registry as ar
    import glass_ui
    import pin_auth
except BaseException:
    _err = traceback.format_exc()
    _log("IMPORT FAILED\n" + _err)
    _show_fatal(_err)
    raise

_log("imports finished")


def main():
    if len(sys.argv) < 2:
        glass_ui.show_message("error", "No path provided", "No file or folder path was provided.")
        sys.exit(1)

    path = sys.argv[1]
    existing = ar.get_asset(path)

    if not existing or existing.get("status") != "protected":
        glass_ui.show_message("warning", "Not protected", "This item is not currently protected by DAAMS.", detail=path)
        return

    confirm = glass_ui.ask_confirm(
        "Remove protection",
        f"Remove DAAMS protection from this {existing.get('asset_type', 'item')}?",
        path=path,
        note="The file/folder itself will NOT be deleted or modified. "
             "Existing activity history will be kept.",
        confirm_label="Remove protection",
        danger=True,
    )
    if not confirm:
        return

    _log("asking for PIN")
    if not pin_auth.confirm_with_pin("remove protection"):
        _log("PIN not confirmed (wrong or cancelled)")
        return  # Wrong PIN or cancelled -- confirm_with_pin already showed why.
    _log("PIN confirmed")

    ok, msg = glass_ui.run_with_loading(
        "Removing protection... please wait.",
        ar.remove_protection,
        path,
    )
    _log(f"remove_protection finished: ok={ok}")

    if ok:
        glass_ui.show_message("success", "Protection removed", msg)
    else:
        glass_ui.show_message("error", "Could not remove protection", msg)


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
