"""
activity_logger.py

Responsible for turning a raw filesystem event into a structured
"activity record", displaying it to the administrator, and persisting
it locally.

Phase 1 scope (unchanged):
- Build a structured dict for each activity (User, Device, Asset, Action, Time).
- Print it clearly to the terminal.

Phase 2 scope (this update):
- Persist EVERY activity record permanently as its own line in a local
  JSON Lines (.jsonl) file, so activity history survives across runs and
  can be reviewed/parsed later (e.g. before Firestore sync exists).
- Handle logging failures (e.g. disk full, permissions) without crashing
  the monitoring engine -- a failed log write should never stop detection.

Firebase/Firestore storage, risk scoring, and alerting are still NOT part
of this file. This module only records "what happened", not "how risky
it was".
"""

import getpass
import json
import os
import socket
from datetime import datetime

# JSON Lines format: one JSON object per line. Chosen over a single JSON
# array because it lets us APPEND new records cheaply and safely without
# re-reading/re-writing the whole file, and a corrupted/incomplete final
# line can't break the records before it.
LOG_FILE = os.path.join(os.path.dirname(__file__), "activity_log.jsonl")


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


def append_to_local_log(record: dict) -> bool:
    """
    Permanently store the activity record as one JSON object per line
    (JSON Lines / .jsonl) in the local log file.

    Each call appends exactly one line -- previous activity history is
    never overwritten or rewritten, so the file is a durable, append-only
    record of everything DAAMS has detected across every run.

    This is still local storage, not the final architecture -- Firebase
    Firestore integration will read/sync from an equivalent structure in
    a later phase.

    Returns True if the write succeeded, False if it failed. A failure
    here must NEVER crash or stop the monitoring engine -- detection is
    the priority, so logging errors are caught and reported, not raised.
    """
    try:
        line = json.dumps(record, ensure_ascii=False)
    except (TypeError, ValueError) as e:
        # Should not normally happen since our records are plain strings,
        # but guard against it so a bad record can't take down logging.
        print(f"[WARNING] Could not serialize activity record: {e}")
        return False

    try:
        with open(LOG_FILE, "a", encoding="utf-8") as f:
            f.write(line + "\n")
        return True
    except OSError as e:
        # Covers disk full, permission denied, path issues, etc.
        print(f"[WARNING] Could not write to local activity log: {e}")
        return False


def read_all_activities() -> list:
    """
    Read back every activity record stored in the local JSONL log, in the
    order they were recorded. Used for testing/verification (e.g. QA
    confirming that history is preserved across runs) -- not used by the
    monitoring loop itself.

    Skips and reports any individual line that fails to parse, instead of
    failing the whole read.
    """
    records = []
    if not os.path.exists(LOG_FILE):
        return records

    try:
        with open(LOG_FILE, "r", encoding="utf-8") as f:
            for line_number, line in enumerate(f, start=1):
                line = line.strip()
                if not line:
                    continue
                try:
                    records.append(json.loads(line))
                except json.JSONDecodeError:
                    print(f"[WARNING] Skipping unreadable log line {line_number}")
    except OSError as e:
        print(f"[WARNING] Could not read local activity log: {e}")

    return records


def record_activity(asset_path: str, action: str) -> dict:
    """
    Convenience function: build, display, and permanently log an activity
    in one call. Returns the record in case the caller needs it.

    Display and logging are intentionally isolated with their own error
    handling (see display_activity / append_to_local_log) so that a
    problem in one does not prevent the other, and neither can crash the
    monitoring engine's event loop.
    """
    record = build_activity_record(asset_path, action)

    try:
        display_activity(record)
    except Exception as e:
        # Terminal output failing (e.g. encoding issue in some consoles)
        # should not stop the record from being logged.
        print(f"[WARNING] Could not display activity in terminal: {e}")

    append_to_local_log(record)
    return record
