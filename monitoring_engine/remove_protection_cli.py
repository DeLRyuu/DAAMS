"""
remove_protection_cli.py

Launched by the DAAMS Windows Explorer context menu when the administrator
right-clicks an already-protected file or folder and selects:
    DAAMS -> Remove Protection

Confirms with the administrator, then calls asset_registry.remove_protection(),
which ONLY updates registry metadata (status -> "unprotected"). It NEVER
deletes the actual file/folder, and NEVER deletes any prior activity_log
history for that asset.

Usage (this is what the registered context-menu command runs):
    pythonw.exe remove_protection_cli.py "C:\\path\\to\\selected\\item"
"""

import sys
from tkinter import messagebox

import asset_registry as ar
import pin_auth


def main():
    if len(sys.argv) < 2:
        messagebox.showerror("DAAMS", "No file or folder path was provided.")
        sys.exit(1)

    path = sys.argv[1]
    existing = ar.get_asset(path)

    if not existing or existing.get("status") != "protected":
        messagebox.showwarning("DAAMS", f"'{path}' is not currently protected by DAAMS.")
        return

    confirm = messagebox.askyesno(
        "DAAMS - Remove Protection",
        f"Remove DAAMS protection from this {existing.get('asset_type', 'item')}?\n\n"
        f"{path}\n\n"
        f"The file/folder itself will NOT be deleted or modified. "
        f"Existing activity history will be kept.",
    )
    if not confirm:
        return

    if not pin_auth.confirm_with_pin("remove protection"):
        return  # Wrong PIN or cancelled -- confirm_with_pin already showed why.

    ok, msg = ar.remove_protection(path)
    if ok:
        messagebox.showinfo("DAAMS", msg)
    else:
        messagebox.showerror("DAAMS", msg)


if __name__ == "__main__":
    main()
