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
shows a minimal Tkinter window to choose a classification, then calls
straight into asset_registry.py -- the SAME module the Monitoring Engine
and (later) the WPF console use, so there is exactly one implementation
of "what protecting an asset means."

Usage (this is what the registered context-menu command runs):
    pythonw.exe protect_asset_cli.py "C:\\path\\to\\selected\\item"

Duplicate protection handling:
    If the selected path is already protected, this script does NOT
    create a duplicate and does NOT ask for a PIN (nothing is actually
    changing). It shows an informational message and points the admin
    to the DAAMS Admin Control Center (WPF) to change classification --
    reclassifying is intentionally not available from this right-click
    menu; see context_menu_setup.py for why.
"""

import getpass
import sys
import tkinter as tk
from tkinter import messagebox

import asset_registry as ar
import pin_auth

CLASSIFICATIONS = ("Public", "Sensitive", "Private", "Confidential")


def ask_classification(path: str, asset_type: str, initial: str = None) -> str:
    """
    Show a small window with one button per classification. Returns the
    chosen classification string, or None if the window was closed/cancelled.
    """
    result = {"value": None}

    root = tk.Tk()
    root.title("DAAMS - Protect Asset")
    root.resizable(False, False)

    tk.Label(
        root,
        text=f"Protect this {asset_type} as:",
        font=("Segoe UI", 10, "bold"),
        pady=8,
    ).pack()
    tk.Label(root, text=path, wraplength=360, fg="gray20").pack(padx=12, pady=(0, 10))

    def choose(value):
        result["value"] = value
        root.destroy()

    button_frame = tk.Frame(root)
    button_frame.pack(padx=12, pady=(0, 12))

    for classification in CLASSIFICATIONS:
        btn = tk.Button(
            button_frame,
            text=classification,
            width=28,
            command=lambda c=classification: choose(c),
        )
        if initial and classification == initial:
            btn.config(relief=tk.SUNKEN)
        btn.pack(pady=2)

    tk.Button(root, text="Cancel", width=28, command=root.destroy).pack(pady=(0, 12))

    root.eval("tk::PlaceWindow . center")
    root.mainloop()
    return result["value"]


def main():
    if len(sys.argv) < 2:
        messagebox.showerror("DAAMS", "No file or folder path was provided.")
        sys.exit(1)

    path = sys.argv[1]
    asset_type = "folder" if ar.os.path.isdir(path) else "file"

    # Check for duplicate protection FIRST, before asking for a PIN --
    # if nothing is actually going to change, there's no action to gate.
    existing = ar.get_asset(path)
    if existing and existing.get("status") == "protected":
        messagebox.showinfo(
            "DAAMS - Already Protected",
            f"This {asset_type} is already protected as "
            f"'{existing.get('classification')}'.\n\n"
            f"To change its classification, use the DAAMS Admin Control "
            f"Center.",
        )
        return

    action_phrase = f"protect this {asset_type}"
    if not pin_auth.confirm_with_pin(action_phrase):
        return  # Wrong PIN or cancelled -- confirm_with_pin already showed why.

    classification = ask_classification(path, asset_type)
    if not classification:
        return  # Cancelled

    ok, msg = ar.protect_asset(path, classification, protected_by=getpass.getuser())
    if ok:
        messagebox.showinfo("DAAMS", msg)
    else:
        messagebox.showerror("DAAMS", msg)


if __name__ == "__main__":
    main()
