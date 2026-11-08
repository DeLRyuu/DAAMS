"""
change_pin_cli.py

The shared DAAMS PIN is generated in the Control Center (Passkey PIN), so this
script only explains where to do that. The Monitoring Engine can no longer
create or change a PIN.

Usage:
    python change_pin_cli.py
    (or pythonw.exe change_pin_cli.py to avoid a console window)
"""

import os
import sys
import traceback

_ENGINE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ENGINE_DIR not in sys.path:
    sys.path.insert(0, _ENGINE_DIR)


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
    from ui import glass_ui
except BaseException:
    _show_fatal(traceback.format_exc())
    raise


def main():
    glass_ui.show_message(
        "info",
        "PIN is managed in the Control Center",
        "The DAAMS PIN is generated in the Control Center.\n\n"
        "Open Control Center -> Settings -> Passkey PIN and press "
        "\"Generate new Passkey PIN\". This PC downloads it automatically.",
    )


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        glass_ui.show_message("error", "Unexpected error", "Something went wrong.", detail=f"{type(e).__name__}: {e}")
        raise
