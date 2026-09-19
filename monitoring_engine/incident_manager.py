"""
incident_manager.py

Phase 7: Incident Report generation.

Turns an ALREADY-CREATED Security Alert (alert_manager.py -- this module
never generates alerts itself, and never recalculates risk) into an
Incident Report record, persists it locally (incident_reports.jsonl) and
mirrors it to Firestore's 'incident_reports' collection, using the same
tolerant, non-blocking patterns established for activity_logs (Phase 3),
protected_assets (Phase 4), and security_alerts (Phase 6).

Per the Phase 7 brief, this module only ever creates an incident in the
"Open" state with "Pending investigation." notes and no resolution.
Status transitions, investigation notes, and resolution management
belong to the C# WPF Control Center -- nothing here is a step toward
building that workflow in Python.

Relationship preserved for traceability (per the brief):
    Activity -> Security Alert -> Incident Report
An incident always carries related_alert_id, so a later reader (the
Control Center, or a person reading the JSONL files) can trace an
incident back to the alert -- and, via that alert's own fields, back to
the original activity -- without a database join.
"""

import json
import os
from datetime import datetime

import firestore_logger
from config import INCIDENT_REPORTS_COLLECTION

INCIDENT_LOG_FILE = os.path.join(os.path.dirname(__file__), "incident_reports.jsonl")

# Plain-language phrasing for each action the Monitoring Engine actually
# detects (Create/Modify/Rename/Delete). An action outside this map (e.g.
# if Open/Copy/Move detection is added in a future phase) still gets a
# safe, honest description rather than a crash or a fabricated phrase.
_ACTION_PHRASES = {
    "Create": "created a new file within",
    "Modify": "modified",
    "Rename": "renamed",
    "Delete": "deleted",
}


def _generate_incident_id() -> str:
    """
    INC-YYYYMMDD-NNN, using the exact same per-day sequence-counting
    approach already established for Alert IDs in alert_manager.py --
    see that module's _generate_alert_id() for the full reasoning
    (single-writer-process assumption, no separate counter file needed).
    """
    date_str = datetime.now().strftime("%Y%m%d")
    prefix = f"INC-{date_str}-"
    count_today = 0
    if os.path.exists(INCIDENT_LOG_FILE):
        try:
            with open(INCIDENT_LOG_FILE, "r", encoding="utf-8") as f:
                for line in f:
                    if prefix in line:
                        count_today += 1
        except OSError:
            pass  # Worst case, numbering restarts from 001 -- still unique enough.
    return f"{prefix}{count_today + 1:03d}"


def _build_description(alert: dict) -> str:
    """
    Build a concise, explainable, NON-accusatory description using ONLY
    fields the alert actually has -- no fabricated detail, no claim of
    intent or identity beyond "the Windows account that performed the
    action". Matches the Phase 7 brief's own example style:
    "High-risk activity detected involving a Confidential asset. The
    user copied the protected file during an unusual access period."
    """
    classification = alert.get("asset_classification")
    risk_level = alert.get("risk_level", "High")
    action = alert.get("action", "")
    action_phrase = _ACTION_PHRASES.get(action, f"performed a '{action}' action on")

    factors = alert.get("risk_factors") or []
    off_hours = any(
        "outside normal working hours" in f.lower() or "near boundary of working hours" in f.lower()
        for f in factors
    )
    burst = any("burst activity" in f.lower() for f in factors)

    context_bits = []
    if off_hours:
        context_bits.append("during an unusual access period")
    if burst:
        context_bits.append("as part of an unusually frequent burst of activity on this asset")
    context = f" {' and '.join(context_bits)}" if context_bits else ""

    classification_phrase = f"a {classification} asset" if classification else "a protected asset"

    return (
        f"{risk_level}-risk activity detected involving {classification_phrase}. "
        f"The user {action_phrase} the protected item{context}. "
        f"This is potentially suspicious activity that requires investigation."
    )


def _append_incident_locally(incident: dict) -> bool:
    """
    Append the incident as one JSON line -- same append-only JSONL
    pattern as activity_log.jsonl and security_alerts.jsonl. Never
    overwrites previous incidents. Never raises -- a logging failure is
    reported and swallowed so it can't stop the Monitoring Engine.
    """
    try:
        with open(INCIDENT_LOG_FILE, "a", encoding="utf-8") as f:
            f.write(json.dumps(incident, ensure_ascii=False) + "\n")
        return True
    except OSError as e:
        print(f"[INCIDENT ERROR] Could not write incident report to local log: {e}")
        return False


def create_incident_from_alert(alert: dict):
    """
    Build, store (local + Firestore), and display an Incident Report for
    an already-created Security Alert.

    Returns the incident dict, or None if generation failed outright.
    NEVER raises -- any failure is caught, reported, and results in None
    rather than crashing the Monitoring Engine or undoing the alert that
    was already saved by alert_manager.py before this function was ever
    called.
    """
    try:
        incident = {
            "incident_id": _generate_incident_id(),
            "related_alert_id": alert.get("alert_id"),
            "user": alert.get("user"),
            "device": alert.get("device"),
            "asset": alert.get("asset"),
            "asset_path": alert.get("asset_path"),
            "action": alert.get("action"),
            "classification": alert.get("asset_classification"),
            "timestamp": alert.get("timestamp"),
            "risk_score": alert.get("risk_score"),
            "risk_level": alert.get("risk_level"),
            "risk_factors": alert.get("risk_factors", []),
            "description": _build_description(alert),
            "investigation_notes": "Pending investigation.",
            "status": "Open",
            "resolution": None,
            "date_resolved": None,
        }

        _append_incident_locally(incident)

        try:
            firestore_logger.upload_incident_report(incident)
        except Exception as e:
            print(f"[FIRESTORE WARNING] Could not upload incident report: {e}")
            print("The incident was still saved to the local incident log.")

        print("\n[INCIDENT REPORT]")
        print(f"Incident ID: {incident['incident_id']}")
        print(f"Related Alert: {incident['related_alert_id']}")
        print(f"Status: {incident['status']}")
        print(f"Description: {incident['description']}")

        return incident

    except Exception as e:
        print(f"[INCIDENT ERROR] Unexpected error generating incident report: {e}")
        return None
