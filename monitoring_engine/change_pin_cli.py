"""
change_pin_cli.py

Standalone way for an administrator to change the shared DAAMS PIN used
to confirm Protect / Change Classification / Remove Protection actions.

This is intentionally a plain, manually-launched script for now -- NOT a
Windows Explorer context-menu entry -- because PIN management isn't tied
to any particular file or folder the way the other three DAAMS actions
are, so it doesn't fit the existing "%1 = selected path" pattern.

All the actual logic lives in pin_auth.py, which this script simply
calls into. The planned C# WPF admin console is expected to provide a
proper Settings screen for this that calls the exact same functions
(reading/writing the same security_settings.json / Firestore document),
so this script and the future console can never disagree about what the
current PIN is.

Usage:
    python change_pin_cli.py
    (or pythonw.exe change_pin_cli.py to avoid a console window)
"""

import tkinter as tk
from tkinter import messagebox, simpledialog

import pin_auth


def main():
    root = tk.Tk()
    root.withdraw()

    if pin_auth.is_using_default_pin():
        messagebox.showinfo(
            "DAAMS - Change PIN",
            "DAAMS is currently using the default PIN.\nLet's set a new one.",
            parent=root,
        )
    else:
        current_pin = simpledialog.askstring(
            "DAAMS - Change PIN", "Enter your CURRENT PIN:", show="*", parent=root
        )
        if current_pin is None:
            root.destroy()
            return
        if not pin_auth.verify_pin(current_pin):
            messagebox.showerror("DAAMS", "Incorrect current PIN. No changes made.", parent=root)
            root.destroy()
            return

    new_pin = simpledialog.askstring(
        "DAAMS - Change PIN",
        f"Enter your NEW PIN ({pin_auth.MIN_PIN_LENGTH}-{pin_auth.MAX_PIN_LENGTH} digits):",
        show="*",
        parent=root,
    )
    if new_pin is None:
        root.destroy()
        return

    confirm_pin = simpledialog.askstring(
        "DAAMS - Change PIN", "Re-enter your NEW PIN to confirm:", show="*", parent=root
    )
    if confirm_pin is None:
        root.destroy()
        return

    if new_pin != confirm_pin:
        messagebox.showerror("DAAMS", "The two PINs did not match. No changes made.", parent=root)
        root.destroy()
        return

    ok, msg = pin_auth.set_pin(new_pin)
    if ok:
        messagebox.showinfo("DAAMS", msg, parent=root)
    else:
        messagebox.showerror("DAAMS", msg, parent=root)

    root.destroy()


if __name__ == "__main__":
    main()
