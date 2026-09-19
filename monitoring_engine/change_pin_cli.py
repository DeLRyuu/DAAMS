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

All windows use the shared glass look from glass_ui.py.

Usage:
    python change_pin_cli.py
    (or pythonw.exe change_pin_cli.py to avoid a console window)
"""

import sys
import traceback


def _show_fatal(err: str) -> None:
    try:
        import tkinter as tk
        from tkinter import messagebox
        root = tk.Tk()
        root.withdraw()
        messagebox.showerror("DAAMS - Startup Error", err[-800:], parent=root)
        root.destroy()
    except BaseException:
        pass


try:
    import glass_ui
    import pin_auth
except BaseException:
    _show_fatal(traceback.format_exc())
    raise


def main():
    if pin_auth.is_using_default_pin():
        glass_ui.show_message(
            "info",
            "Default PIN in use",
            "DAAMS is currently using the default PIN.\nLet's set a new one.",
        )
    else:
        current_pin = glass_ui.ask_pin("Enter your CURRENT PIN.", title="Change PIN")
        if current_pin is None:
            return
        if not pin_auth.verify_pin(current_pin):
            glass_ui.show_message("error", "Incorrect PIN", "Incorrect current PIN. No changes made.")
            return

    new_pin = glass_ui.ask_pin(
        f"Enter your NEW PIN ({pin_auth.MIN_PIN_LENGTH}-{pin_auth.MAX_PIN_LENGTH} digits).",
        title="Change PIN",
    )
    if new_pin is None:
        return

    confirm_pin = glass_ui.ask_pin("Re-enter your NEW PIN to confirm.", title="Confirm new PIN")
    if confirm_pin is None:
        return

    if new_pin != confirm_pin:
        glass_ui.show_message("error", "PINs don't match", "The two PINs did not match. No changes made.")
        return

    ok, msg = pin_auth.set_pin(new_pin)
    if ok:
        glass_ui.show_message("success", "PIN changed", msg)
    else:
        glass_ui.show_message("error", "Could not change PIN", msg)


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        # No console under pythonw.exe -- make sure a crash is still visible.
        glass_ui.show_message("error", "Unexpected error", "Something went wrong.", detail=f"{type(e).__name__}: {e}")
        raise
