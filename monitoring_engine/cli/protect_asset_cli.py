"""
protect_asset_cli.py

Launched by the DAAMS Windows Explorer context menu when the administrator
right-clicks a file or folder and selects:
    DAAMS -> Protect This File   (or "Protect This Folder")

Windows' classic context menu can show static text labels, but it cannot
natively collect additional input (like "which classification?") without
either a full shell extension (out of scope for this capstone, per the
project brief) or a small companion GUI. This script IS that small
companion GUI: it receives the selected path as a command-line argument,
shows a glass-styled window (see glass_ui.py) to choose a classification,
then calls straight into asset_registry.py -- the SAME module the
Monitoring Engine and (later) the WPF console use, so there is exactly
one implementation of "what protecting an asset means."

Usage (this is what the registered context-menu command runs):
    pythonw.exe protect_asset_cli.py "C:\\path\\to\\selected\\item"

Duplicate protection handling:
    If the selected path is already protected, this script does NOT
    create a duplicate and does NOT ask for a PIN (nothing is actually
    changing). It shows an informational message and points the admin
    to the DAAMS Admin Control Center (WPF) to change classification --
    reclassifying is intentionally not available from this right-click
    menu; see context_menu_setup.py for why.

Debugging:
    Because this runs under pythonw.exe (no console), errors would
    otherwise be invisible. Every run appends to daams_cli_debug.log
    (next to this file) with timestamps, so both crashes and slow steps
    can be diagnosed after the fact.

Loading window:
    protect_asset() saves the local registry and then syncs to Firestore,
    which can take several seconds on a cold start. glass_ui's
    run_with_loading() shows a "please wait" window while that runs on a
    background thread, so the admin knows DAAMS is working.
"""

import getpass
import os
import sys
import traceback
from datetime import datetime

# ---------------------------------------------------------------------------
# Debug logging -- this MUST come before the project imports below, so that
# "START" is recorded even if one of those imports fails.
# ---------------------------------------------------------------------------
_ENGINE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
_LOG = os.path.join(_ENGINE_DIR, "logs", "daams_cli_debug.log")
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
    """
    Best-effort visible error for a crash. Tries the glass dialog first;
    if even that can't load (e.g. Pillow isn't installed), falls back to a
    plain native message box so the failure is never completely silent.
    """
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


_log(f"START argv={sys.argv} exe={sys.executable} cwd={os.getcwd()}")

try:
    import ntpath
    from core import asset_registry as ar
    from core.clipboard_watcher import find_covering_asset
    from ui import glass_ui
    from cli import pin_auth
except BaseException:
    _err = traceback.format_exc()
    _log("IMPORT FAILED\n" + _err)
    _show_fatal(_err)
    raise

_log("imports finished")

CLASSIFICATIONS = ("Public", "Sensitive", "Private", "Confidential")

CLASSIFICATION_DESCRIPTIONS = {
    "Public": "Can generally be accessed by anyone",
    "Sensitive": "Important information with limited access",
    "Private": "Restricted or personal information",
    "Confidential": "Highly sensitive, strictest protection",
}


def check_already_protected(path: str, assets: list = None) -> tuple:
    """
    Check whether `path` is already protected or inside a protected folder.

    Returns:
        ("self", record) if `path` itself is currently protected.
        ("folder", record) if `path` is inside a currently protected folder.
        (None, None) if `path` is not protected (or its status is 'unprotected').
    """
    if not path:
        return None, None

    if assets is None:
        try:
            assets = ar.get_all_assets(only_protected=True)
        except Exception as e:
            _log(f"check_already_protected error: {e}")
            assets = []

    # Only consider records that are actively protected
    protected = [a for a in assets if a.get("status", "protected") == "protected"]
    covering = find_covering_asset(path, protected)
    if not covering:
        return None, None

    cov_path = covering.get("path", "")
    norm_path = ntpath.normcase(ntpath.normpath(path))
    norm_cov = ntpath.normcase(ntpath.normpath(cov_path))

    if norm_path == norm_cov:
        return "self", covering
    return "folder", covering


def main():
    if len(sys.argv) < 2:
        glass_ui.show_message("error", "No path provided", "No file or folder path was provided.")
        sys.exit(1)

    path = sys.argv[1]
    asset_type = "folder" if os.path.isdir(path) else "file"

    # Check for duplicate protection or covering folder FIRST, before asking for a PIN --
    # if nothing is actually going to change, there's no action to gate.
    status_kind, covering = check_already_protected(path)
    if status_kind == "self":
        classification = covering.get("classification", "Protected")
        glass_ui.show_message(
            "info",
            "Already protected",
            f"This {asset_type} is already protected as {classification}.",
        )
        return

    if status_kind == "folder":
        folder_name = covering.get("asset_name") or ntpath.basename(ntpath.normpath(covering.get("path", "")))
        classification = covering.get("classification", "Protected")
        glass_ui.show_message(
            "info",
            "Already protected",
            f"This {asset_type} is already covered by the protected folder {folder_name} ({classification}).",
        )
        return

    action_phrase = f"protect this {asset_type}"
    _log("asking for PIN")
    if not pin_auth.confirm_with_pin(action_phrase):
        _log("PIN not confirmed (wrong or cancelled)")
        return  # Wrong PIN or cancelled -- confirm_with_pin already showed why.
    _log("PIN confirmed")

    sublabels = {
        "Public": "Can generally be accessed by anyone",
        "Sensitive": "Important information with limited access",
        "Private": "Restricted or personal information",
        "Confidential": "Highly sensitive, strictest protection",
    }
    classification = glass_ui.ask_classification(path, asset_type, CLASSIFICATIONS, sublabels)
    _log(f"classification chosen: {classification}")
    if not classification:
        return  # Cancelled

    ok, msg = glass_ui.run_with_loading(
        "Protecting... please wait.",
        ar.protect_asset,
        path,
        classification,
        protected_by=getpass.getuser(),
    )
    _log(f"protect_asset finished: ok={ok}")

    if ok:
        glass_ui.show_message("success", "Protected", msg)
    else:
        glass_ui.show_message("error", "Could not protect", msg)


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
