"""
main.py

Entry point for the DAAMS Python Monitoring Engine (Phase 1 prototype).

Usage:
    python main.py

Configuration:
    Edit config.py and set PROTECTED_PATH to the folder or file you want
    DAAMS to monitor before running this script.
"""

import sys

from config import get_protected_path
from monitor import start_monitoring


def main():
    protected_path = get_protected_path()
    try:
        start_monitoring(protected_path)
    except (ValueError, FileNotFoundError) as e:
        print(f"[CONFIGURATION ERROR] {e}")
        sys.exit(1)
    except Exception as e:
        # Catch-all so unexpected errors are visible and explainable
        # during testing/defense, instead of a raw traceback only.
        print(f"[UNEXPECTED ERROR] {e}")
        sys.exit(1)


if __name__ == "__main__":
    main()
