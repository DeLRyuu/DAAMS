"""
alert_manager.py

Phase 6: Security Alert generation.

Turns an ALREADY risk-assessed activity record (Phase 5's risk_score /
risk_level / risk_factors -- never recalculated here) into a Security
Alert when the risk level is High or Critical, persists it locally
(security_alerts.jsonl) and mirrors it to Firestore's `security_alerts`
collection, using the same tolerant, non-blocking patterns already
established for activity_logs (Phase 3) and protected_assets (Phase 4).

This module does NOT decide risk. It only decides: given a risk result
Phase 5 already produced, does this warrant a Security Alert -- and if
so, builds, stores, and displays that alert. monitor.py still only
detects; risk_engine.py still only scores; this module only decides
whether a score is alert-worthy and records that decision. Three
separate responsibilities, three separate files, per the project's
established architecture pattern.

A note on field naming (see README for the full explanation): in
activity_log.jsonl (Phase 1-2 convention), the "asset" field has always
meant the full PATH. The Phase 6 brief's own alert example uses "asset"
to mean a short display NAME (e.g. "Payroll.xlsx") and separately lists
"asset_path" as its own field. To satisfy the brief's example exactly
without silently overloading what "asset" means elsewhere in this
codebase, alerts here use:
    asset            -> short display name, derived from the path
    asset_path       -> the full path (or "old -> new" for a rename),
                        i.e. exactly what activity_log.jsonl calls "asset"
    asset_classification -> the protected asset's classification
"""

import json
import os
from datetime import datetime

import firestore_logger
from config import ALERT_RISK_LEVELS, SECURITY_ALERTS_COLLECTION

ALERT_LOG_FILE = os.path.join(os.path.dirname(__file__), "security_alerts.jsonl")

# In-memory duplicate-alert guard: (asset_path, action, timestamp) -> the
# alert_id already issued for that EXACT activity. This protects against
# the same underlying Watchdog event firing twice -- a known, previously
# documented pre-existing quirk on some OS/editor combinations -- from
# generating two separate alerts for what is really one detected
# activity. It is intentionally simple (an in-memory dict keyed by the
# activity's own natural key), not a general-purpose incident-correlation
# system -- see README "How duplicate alerts are prevented".
_recent_alert_keys = {}


def _generate_alert_id() -> str:
    """
    ALERT-YYYYMMDD-NNN, where NNN is a per-day sequence number derived by
    counting how many alerts already exist for today's date in the local
    alert log. No separate counter file is needed, and this is
    consistent with the existing single-writer-process assumption
    already documented for activity_log.jsonl (Phase 2) -- DAAMS assumes
    one Monitoring Engine instance per machine.
    """
    date_str = datetime.now().strftime("%Y%m%d")
    prefix = f"ALERT-{date_str}-"
    count_today = 0
    if os.path.exists(ALERT_LOG_FILE):
        try:
            with open(ALERT_LOG_FILE, "r", encoding="utf-8") as f:
                for line in f:
                    if prefix in line:
                        count_today += 1
        except OSError:
            pass  # Worst case, numbering restarts from 001 -- still a
            # valid, unique-enough ID for this capstone's scale.
    return f"{prefix}{count_today + 1:03d}"


def _derive_asset_name(asset_path_field: str) -> str:
    """
    Turn the activity record's "asset" field (which, per Phase 1-5
    convention, is actually a full PATH -- or "old -> new" for a rename)
    into a short, friendly display name for the alert.
    """
    if not asset_path_field:
        return "Unknown asset"
    if " -> " in asset_path_field:
        return asset_path_field  # A rename doesn't reduce to one clean name; show both.
    return os.path.basename(asset_path_field.rstrip("\\/")) or asset_path_field


def _append_alert_locally(alert: dict) -> bool:
    """
    Append the alert as one JSON line, same append-only pattern as
    activity_log.jsonl. Never overwrites previous alerts. Never raises --
    a logging failure is reported and swallowed so it can't stop the
    Monitoring Engine.
    """
    try:
        with open(ALERT_LOG_FILE, "a", encoding="utf-8") as f:
            f.write(json.dumps(alert, ensure_ascii=False) + "\n")
        return True
    except OSError as e:
        print(f"[ALERT ERROR] Could not write security alert to local log: {e}")
        return False


def _display_alert(alert: dict) -> None:
    print("\n[SECURITY ALERT]")
    print(f"Alert ID: {alert['alert_id']}")
    print(f"Risk Level: {alert['risk_level']}")
    print(f"Status: {alert['status']}")
    # A short human-readable reason line, built from the same
    # risk_factors Phase 5 already produced (strip the "(+N)" suffix
    # for a cleaner one-line summary; the full factors are still stored
    # in the alert record itself for detailed review).
    reason_parts = [factor.split(" (+")[0] for factor in alert.get("risk_factors", [])]
    print(f"Reason: {' + '.join(reason_parts) if reason_parts else 'Not specified'}")


def process_activity(record: dict, classification: str = None):
    """
    Given a FULLY risk-assessed activity record (must already contain
    risk_score/risk_level/risk_factors from risk_engine.py -- this
    function never computes risk itself), decide whether it warrants a
    Security Alert, and if so, build, store, and display it.

    Returns the generated alert dict, or None if no alert was warranted
    (Low/Medium risk, or a detected duplicate) or generation failed.

    NEVER raises, and never lets an alert-related failure stop the
    Monitoring Engine -- every internal step is individually guarded,
    matching the "errors must not crash the engine" requirement.
    """
    try:
        risk_level = record.get("risk_level")
        if risk_level not in ALERT_RISK_LEVELS:
            return None  # Low/Medium -- no alert, per Phase 6 spec.

        # Duplicate-alert guard -- see module docstring.
        dedupe_key = (record.get("asset"), record.get("action"), record.get("timestamp"))
        if dedupe_key in _recent_alert_keys:
            print(
                f"[ALERT] This exact activity was already alerted as "
                f"{_recent_alert_keys[dedupe_key]}; not creating a duplicate."
            )
            return None

        alert = {
            "alert_id": _generate_alert_id(),
            "user": record.get("user"),
            "device": record.get("device"),
            "asset": _derive_asset_name(record.get("asset")),
            "asset_path": record.get("asset"),
            "action": record.get("action"),
            "asset_classification": classification,
            "risk_score": record.get("risk_score"),
            "risk_level": risk_level,
            "risk_factors": record.get("risk_factors", []),
            "timestamp": record.get("timestamp"),
            "status": "New",
        }

        _recent_alert_keys[dedupe_key] = alert["alert_id"]

        _append_alert_locally(alert)

        try:
            firestore_logger.upload_security_alert(alert)
        except Exception as e:
            print(f"[FIRESTORE WARNING] Could not upload security alert: {e}")
            print("The alert was still saved to the local alert log.")

        _display_alert(alert)

        return alert

    except Exception as e:
        print(f"[ALERT ERROR] Unexpected error generating security alert: {e}")
        return None
