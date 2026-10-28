"""
context_menu_setup.py

Installs (or removes) the DAAMS right-click context menu in Windows
Explorer, for both individual files and folders:

    Right-click a file:
        DAAMS
         `- Protect This File

    Right-click a folder:
        DAAMS
         `- Protect This Folder

Note: "Remove Protection" and "Change Classification" are intentionally NOT
in this menu. Protection removal and reclassification are reserved for the
DAAMS Control Center, which gives an administrator full context and centralized
control. The underlying logic still exists -- see
asset_registry.change_classification() -- the WPF console calls straight
into it, the same shared-implementation approach used everywhere else in
this project. Right-clicking an already-protected file/folder and
choosing "Protect This File/Folder" again now just informs the admin it's
already protected and points them to the WPF console instead of offering
an inline reclassify flow.

Approach: this uses the Windows Registry (via the built-in `winreg`
module) to register a cascading ("submenu") context-menu entry -- no
compiled shell extension, no admin rights required. This matches the
project brief's guidance to prefer a simple, maintainable Registry-based
approach over a full shell extension.

Scope: entries are written under HKEY_CURRENT_USER, so:
  - No administrator privileges are required to install/uninstall.
  - The menu only appears for the Windows user who ran this script
    (which is appropriate -- DAAMS is administrator-controlled per user,
    not machine-wide spyware).

The menu command runs the companion GUI script (protect_asset_cli.py)
via `pythonw.exe`, passing the selected path as %1. It calls directly
into asset_registry.py -- the SAME Protected Asset Registry logic the
Monitoring Engine reads, and that the WPF Control Center also uses.

Usage:
    python windows/context_menu_setup.py install
    python windows/context_menu_setup.py uninstall
"""

import os
import sys

try:
    import winreg
except ImportError:
    winreg = None  # Allows this file to be imported/inspected on non-Windows
    # systems (e.g. during development) without crashing at import time.
    # install()/uninstall() will refuse to run with a clear message instead.


ENGINE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCRIPT_DIR = os.path.join(ENGINE_DIR, "cli")

# (registry key name, menu label, script filename)
# "Remove Protection" is now managed from the DAAMS Control Center only.
MENU_ENTRIES = []

# Roots where the DAAMS submenu is installed, and the label for the
# "protect" entry specific to that root (files vs. folders get different
# top wording, per the project brief's example).
#
# IMPORTANT: per-user class registrations must live under
# HKEY_CURRENT_USER\Software\Classes\... -- NOT directly under
# HKEY_CURRENT_USER\... -- because Windows Explorer reads context-menu
# verbs from HKEY_CLASSES_ROOT, which is a merged view of
# HKEY_LOCAL_MACHINE\SOFTWARE\Classes and HKEY_CURRENT_USER\SOFTWARE\Classes.
# Writing anywhere else creates real registry keys with no effect on Explorer.
ROOTS = [
    (r"Software\Classes\*", "01ProtectAsset", "Protect This File", "protect_asset_cli.py"),
    (r"Software\Classes\Directory", "01ProtectAsset", "Protect This Folder", "protect_asset_cli.py"),
]


def _require_windows():
    if winreg is None:
        print(
            "[ERROR] The 'winreg' module is only available on Windows. "
            "context_menu_setup.py must be run on the Windows machine "
            "where DAAMS is installed."
        )
        sys.exit(1)


def _pythonw_path() -> str:
    """
    Prefer pythonw.exe (no console window flash) if it exists next to the
    current interpreter; otherwise fall back to the current interpreter.
    """
    exe_dir = os.path.dirname(sys.executable)
    pythonw = os.path.join(exe_dir, "pythonw.exe")
    return pythonw if os.path.exists(pythonw) else sys.executable


def _script_path(filename: str) -> str:
    return os.path.join(SCRIPT_DIR, filename)


def _command_line(script_filename: str) -> str:
    return f'"{_pythonw_path()}" "{_script_path(script_filename)}" "%1"'


def _delete_key_recursive(hive, path: str) -> None:
    """
    winreg.DeleteKey only deletes a key with no subkeys, so to remove the
    whole DAAMS submenu tree we have to walk it depth-first and delete
    from the bottom up. Silently does nothing if the key doesn't exist.
    """
    try:
        with winreg.OpenKey(hive, path, 0, winreg.KEY_ALL_ACCESS) as key:
            while True:
                try:
                    subkey_name = winreg.EnumKey(key, 0)
                except OSError:
                    break  # No more subkeys
                _delete_key_recursive(hive, f"{path}\\{subkey_name}")
        winreg.DeleteKey(hive, path)
    except FileNotFoundError:
        pass


def _install_for_root(root_key: str, protect_key_name: str, protect_label: str, protect_script: str) -> None:
    base = f"{root_key}\\shell\\DAAMS"

    # The top-level "DAAMS" entry: MUIVerb sets the label, and having a
    # "shell" subkey full of commands turns it into a cascading submenu
    # rather than a single clickable item.
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, base) as key:
        winreg.SetValueEx(key, "MUIVerb", 0, winreg.REG_SZ, "DAAMS")
        winreg.SetValueEx(key, "SubCommands", 0, winreg.REG_SZ, "")

    entries = [(protect_key_name, protect_label, protect_script)] + MENU_ENTRIES

    for key_name, label, script in entries:
        entry_path = f"{base}\\shell\\{key_name}"
        with winreg.CreateKey(winreg.HKEY_CURRENT_USER, entry_path) as entry_key:
            winreg.SetValueEx(entry_key, "", 0, winreg.REG_SZ, label)

        command_path = f"{entry_path}\\command"
        with winreg.CreateKey(winreg.HKEY_CURRENT_USER, command_path) as command_key:
            winreg.SetValueEx(command_key, "", 0, winreg.REG_SZ, _command_line(script))


def _delete_legacy_remove_protection(root_key: str) -> None:
    """
    Deletes any leftover 02RemoveProtection entry from earlier DAAMS versions:
    deletes its 'command' subkey first, then the key itself, ignoring FileNotFoundError.
    """
    legacy_base = f"{root_key}\\shell\\DAAMS\\shell\\02RemoveProtection"
    try:
        winreg.DeleteKey(winreg.HKEY_CURRENT_USER, f"{legacy_base}\\command")
    except FileNotFoundError:
        pass
    try:
        winreg.DeleteKey(winreg.HKEY_CURRENT_USER, legacy_base)
    except FileNotFoundError:
        pass


def install() -> None:
    _require_windows()
    for root_key, protect_key_name, protect_label, protect_script in ROOTS:
        _install_for_root(root_key, protect_key_name, protect_label, protect_script)
        _delete_legacy_remove_protection(root_key)
        friendly = "files (*)" if root_key.endswith("*") else "folders (Directory)"
        print(f"Installed DAAMS context menu for: {friendly}")
    print("\nDAAMS context menu installed successfully.")
    print("Right-click any file or folder in Explorer to see the DAAMS submenu.")
    print("(You may need to restart Explorer or sign out/in for it to appear immediately.)")


def uninstall() -> None:
    _require_windows()
    for root_key, _, _, _ in ROOTS:
        _delete_legacy_remove_protection(root_key)
        _delete_key_recursive(winreg.HKEY_CURRENT_USER, f"{root_key}\\shell\\DAAMS")
        friendly = "files (*)" if root_key.endswith("*") else "folders (Directory)"
        print(f"Removed DAAMS context menu for: {friendly}")
    print("\nDAAMS context menu uninstalled successfully.")


if __name__ == "__main__":
    action = sys.argv[1].lower() if len(sys.argv) > 1 else "install"
    if action == "install":
        install()
    elif action == "uninstall":
        uninstall()
    else:
        print(f"Unknown action '{action}'. Use: python windows/context_menu_setup.py [install|uninstall]")
        sys.exit(1)
