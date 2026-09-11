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

import sys

from monitor import start_monitoring


def main():
    try:
        start_monitoring()
    except Exception as e:
        # Catch-all so unexpected errors are visible and explainable
        # during testing/defense, instead of a raw traceback only.
        print(f"[UNEXPECTED ERROR] {e}")
        sys.exit(1)


if __name__ == "__main__":
    main()
