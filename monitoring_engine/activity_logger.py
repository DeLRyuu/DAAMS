"""
activity_logger.py

Responsible for turning a raw filesystem event into a structured
"activity record", assessing its risk, displaying it to the
administrator, persisting it locally, and forwarding it to Firebase
Firestore.

Phase 1 scope (unchanged):
- Build a structured dict for each activity (User, Device, Asset, Action, Time).
- Print it clearly to the terminal.

Phase 2 scope (unchanged):
- Persist EVERY activity record permanently as its own line in a local
  JSON Lines (.jsonl) file, so activity history survives across runs.
- Handle logging failures (e.g. disk full, permissions) without crashing
  the monitoring engine -- a failed log write should never stop detection.

Phase 3 scope (unchanged):
- After the local log write, attempt to also upload the same record to
  Firebase Firestore via firestore_logger.py.
- Firestore is entirely optional from this module's point of view: if it
  is unavailable, misconfigured, or errors out, that is caught here (in
  addition to being caught inside firestore_logger itself) and reported
  as a warning. Local logging and terminal display are never affected.

Phase 5 scope (unchanged):
- Between building the record and saving it anywhere, the record is
  passed to risk_engine.assess_risk() (a separate module -- risk
  CALCULATION logic does not live here, only the call to it). The
  resulting risk_score/risk_level/risk_factors/advisory are merged into
  the SAME record dict before it's logged locally or uploaded, so the
  local log and Firestore both end up with risk data included, with no
  second log and no schema change to how records are stored -- just more
  keys in the same dict Phase 2/3 already knew how to persist.
- Risk assessment failures are caught here as an extra safety net (on
  top of risk_engine.py never raising on its own) so a risk-scoring bug
  can never take down detection, local logging, or Firestore upload.

Phase 6 scope (this update):
- After the activity itself is fully logged and displayed, the SAME
  risk-assessed record is handed to alert_manager.process_activity(),
  which decides (using the risk_level Phase 5 already computed -- risk
  is NOT recalculated) whether this warrants a Security Alert, and if
  so builds/stores/displays it. This module doesn't know or care what
  "warrants an alert" means; that decision, and all alert storage, is
  alert_manager.py's job alone.
- Alert-processing failures are caught here as an extra safety net (on
  top of alert_manager.py never raising on its own) so an alerting bug
  can never take down detection or activity logging.
"""

import getpass
import json
import os
import socket
from datetime import datetime

import alert_manager
import firestore_logger
import risk_engine

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

    # Phase 5: risk fields are optional in this function's eyes -- if
    # something upstream failed to attach them, we still display the
    # base activity rather than erroring out over missing keys.
    if "risk_score" in record:
        print(f"Risk: {record['risk_score']} ({record.get('risk_level', '?')})")
        factors = record.get("risk_factors") or []
        if factors:
            print("Risk Factors:")
            for factor in factors:
                print(f"  - {factor}")
        if record.get("advisory"):
            print(f"Notice: {record['advisory']}")


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


def _assess_risk_safely(record: dict, classification: str) -> dict:
    """
    Call risk_engine.assess_risk() with an extra safety net on top of the
    one risk_engine.py already has internally -- belt and suspenders, so
    a risk-scoring problem can NEVER prevent an activity from being
    logged. Always returns a dict with risk_score/risk_level/
    risk_factors/advisory, even in the worst case.
    """
    try:
        return risk_engine.assess_risk(record, classification=classification)
    except Exception as e:
        print(f"[WARNING] Unexpected error during risk assessment: {e}")
        return {
            "risk_score": 0,
            "risk_level": "Low",
            "risk_factors": [f"Risk assessment failed unexpectedly: {e} (defaulted to Low/0)"],
            "advisory": None,
        }


def record_activity(asset_path: str, action: str, classification: str = None) -> dict:
    """
    Build, assess risk for, locally log, upload to Firestore, display,
    and (if warranted) alert on an activity, in that order:
        A. Build the structured activity record.
        B. Assess its risk (Phase 5) -- risk_engine.py does the actual
           calculation; this just calls it and merges the result in.
        C. Save the (now risk-annotated) record to the local activity
           log (Phase 2).
        D. Attempt to upload the same record to Firestore (Phase 3).
        E. Display the activity, including risk, in the terminal.
        F. Hand the SAME record to alert_manager.py (Phase 6), which
           decides -- using the risk_level already computed in step B,
           never recalculating it -- whether to generate, store, and
           display a Security Alert.

    Each step is isolated with its own error handling so that a problem
    in any one of them (risk scoring, local disk, Firestore, terminal
    encoding, alert generation) can never crash the monitoring engine or
    prevent the others from running.

    Args:
        asset_path: the file/folder path the event happened to.
        action: one of the actions the Monitoring Engine detects
            (Create, Modify, Rename, Delete as of Phase 1-4).
        classification: the protected asset's classification from the
            Phase 4 registry, passed through to risk_engine.py and (for
            display purposes only) into any resulting Security Alert.
            None is handled safely if unavailable.

    Returns the final record (including risk fields) in case the caller
    needs it.
    """
    record = build_activity_record(asset_path, action)

    # B. Risk assessment (Phase 5) -- merged into the SAME record dict,
    # so everything downstream (local log, Firestore, display, alerting)
    # just sees a slightly richer activity record with no format/schema
    # change.
    risk = _assess_risk_safely(record, classification)
    record.update(risk)

    # C. Local log (Phase 2) -- always attempted first, since it's the
    # most reliable storage and has no external dependency.
    append_to_local_log(record)

    # D. Firestore upload (Phase 3) -- best-effort. firestore_logger
    # already catches everything internally; this try/except is an extra
    # safety net in case of an unexpected error outside that module.
    try:
        firestore_logger.upload_activity(record)
    except Exception as e:
        print(f"[WARNING] Unexpected error during Firestore upload attempt: {e}")

    # E. Terminal display -- after both storage attempts, so the
    # administrator sees the activity block before any alert block.
    try:
        display_activity(record)
    except Exception as e:
        print(f"[WARNING] Could not display activity in terminal: {e}")

    # F. Security Alert (Phase 6) -- alert_manager.py decides based on
    # risk_level (already computed above) whether this warrants an
    # alert, and if so builds/stores/displays it. This is an extra
    # safety net on top of alert_manager.py never raising on its own.
    try:
        alert_manager.process_activity(record, classification=classification)
    except Exception as e:
        print(f"[WARNING] Unexpected error during alert processing: {e}")

    return record
