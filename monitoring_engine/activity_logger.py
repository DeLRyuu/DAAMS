"""
activity_logger.py

Responsible for turning a raw filesystem event into a structured
"activity record" and displaying it to the administrator.

Phase 1 scope:
- Build a structured dict for each activity (User, Device, Asset, Action, Time).
- Print it clearly to the terminal.
- (Optional) Append it to a local activity_log.txt for basic persistence,
  so QA/testers can review what was captured during a test run.

Firebase/Firestore storage, risk scoring, and alerting are NOT part of
this file yet. This module only records "what happened", not "how risky
it was".
"""

import getpass
import os
import socket
from datetime import datetime

LOG_FILE = os.path.join(os.path.dirname(__file__), "activity_log.txt")


def get_windows_username() -> str:
    """
    Return the Windows account name of the currently logged-in user
    running this process.

    Note: This identifies the WINDOWS ACCOUNT performing the activity,
    not necessarily the physical person sitting at the keyboard (e.g.
    shared accounts, RDP sessions, or another user running the process
    under someone else's session would report that account's name).
    """
    try:
        return getpass.getuser()
    except Exception:
        return "UNKNOWN_USER"


def get_device_name() -> str:
    """Return the device/computer name this engine is running on."""
    try:
        return socket.gethostname()
    except Exception:
        return "UNKNOWN_DEVICE"


def build_activity_record(asset_path: str, action: str) -> dict:
    """
    Build a structured activity record for a single detected filesystem event.

    Fields:
        user       - Windows account running the monitoring process
        device     - Computer name
        asset      - File/folder path affected
        action     - One of: Create, Modify, Rename, Delete
        timestamp  - ISO-like readable timestamp string
    """
    return {
        "user": get_windows_username(),
        "device": get_device_name(),
        "asset": asset_path,
        "action": action,
        "timestamp": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
    }


def display_activity(record: dict) -> None:
    """Print a structured activity record to the terminal in a clear format."""
    print("\n[ACTIVITY DETECTED]")
    print(f"User: {record['user']}")
    print(f"Device: {record['device']}")
    print(f"Asset: {record['asset']}")
    print(f"Action: {record['action']}")
    print(f"Time: {record['timestamp']}")


def append_to_local_log(record: dict) -> None:
    """
    Append the activity record to a plain-text local log file.

    This is a simple, local, human-readable log for Phase 1 testing only.
    It is NOT the final storage mechanism -- Firebase Firestore integration
    will replace/extend this in a later phase.
    """
    line = (
        f"{record['timestamp']} | User={record['user']} | "
        f"Device={record['device']} | Asset={record['asset']} | "
        f"Action={record['action']}\n"
    )
    try:
        with open(LOG_FILE, "a", encoding="utf-8") as f:
            f.write(line)
    except OSError as e:
        print(f"[WARNING] Could not write to local log file: {e}")


def record_activity(asset_path: str, action: str) -> dict:
    """
    Convenience function: build, display, and locally log an activity
    in one call. Returns the record in case the caller needs it.
    """
    record = build_activity_record(asset_path, action)
    display_activity(record)
    append_to_local_log(record)
    return record
