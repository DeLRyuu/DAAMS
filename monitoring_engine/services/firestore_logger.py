"""
firestore_logger.py

Handles communication between the DAAMS Monitoring Engine and Firebase Firestore.
Supports offline-first operation with retryable initialization and idempotent document writes.

Design goals:
- NEVER crash the monitoring engine. Every failure mode (missing credentials,
  network down, offline laptop, quota errors) is caught and handled cleanly.
- Credentials path is configurable via DAAMS_FIREBASE_CREDENTIALS or defaults
  to local serviceAccountKey.json. No credentials in code.
- Idempotent document writes using stable primary keys (activity_id, alert_id,
  incident_id, asset_id hash, pin_config) to prevent duplicates on retry.
- Retryable initialization: if initial connection fails because the machine is offline,
  subsequent attempts when internet connectivity returns will succeed.
"""

import hashlib
import os
import threading
from typing import Optional, Tuple

from config.settings import (
    ENABLE_FIRESTORE,
    ENGINE_DIR,
    FIRESTORE_COLLECTION,
    INCIDENT_REPORTS_COLLECTION,
    PROTECTED_ASSETS_COLLECTION,
    SECURITY_ALERTS_COLLECTION,
    SECURITY_SETTINGS_COLLECTION,
)

_firestore_client = None
_firestore_app = None
_init_lock = threading.Lock()
_firestore_status = None


def _resolve_credentials_path() -> Tuple[str, bool]:
    candidates = []
    configured_path = os.environ.get("DAAMS_FIREBASE_CREDENTIALS")
    if configured_path and configured_path.strip():
        candidates.append(configured_path.strip())

    local_app_data = os.environ.get("LOCALAPPDATA")
    if local_app_data:
        candidates.append(os.path.join(local_app_data, "DAAMS", "serviceAccountKey.json"))
    candidates.append(os.path.join(ENGINE_DIR, "serviceAccountKey.json"))

    seen = set()
    unique_candidates = []
    for candidate in candidates:
        normalized = os.path.normcase(os.path.abspath(candidate))
        if normalized not in seen:
            seen.add(normalized)
            unique_candidates.append(candidate)

    for candidate in unique_candidates:
        if os.path.isfile(candidate):
            return candidate, True
    return unique_candidates[0], False


def _set_firestore_status(status: str, message: str) -> None:
    global _firestore_status
    if status != _firestore_status:
        print(message)
        _firestore_status = status


def _is_authorization_error(error: Exception) -> bool:
    details = f"{type(error).__name__} {error}".lower()
    return any(term in details for term in ("unauthorized", "permissiondenied", "permission denied", "unauthenticated", "403", "401"))


def _record_firestore_error(error: Exception) -> None:
    if _is_authorization_error(error):
        _set_firestore_status(
            "unauthorized",
            "Firestore key is invalid or access is unauthorized; records remain pending or failed.",
        )
    else:
        _set_firestore_status(
            "unavailable",
            "Firestore unavailable; records remain pending or failed.",
        )


def _record_firestore_success() -> None:
    if _firestore_status != "connected":
        _set_firestore_status("connected", "Firestore connection restored.")


def _asset_doc_id(path: str) -> str:
    """Derive stable 24-character hex ID from normalized path."""
    normalized = os.path.normcase(os.path.normpath(os.path.abspath(path)))
    return hashlib.sha256(normalized.encode("utf-8")).hexdigest()[:24]


def get_firestore_client() -> Optional[object]:
    """
    Return active Firestore client or attempt initialization.
    Thread-safe and retryable if previously offline.
    """
    global _firestore_client, _firestore_app

    if not ENABLE_FIRESTORE:
        return None

    if _firestore_client is not None:
        return _firestore_client

    with _init_lock:
        if _firestore_client is not None:
            return _firestore_client

        credentials_path, credentials_exist = _resolve_credentials_path()
        if not credentials_exist:
            _set_firestore_status(
                "missing",
                f"Firestore key not found; running offline. Put serviceAccountKey.json in {credentials_path}",
            )
            return None

        try:
            import firebase_admin
            from firebase_admin import credentials, firestore

            if not _firestore_app:
                # Check if default app already exists
                try:
                    _firestore_app = firebase_admin.get_app()
                except ValueError:
                    cred = credentials.Certificate(credentials_path)
                    _firestore_app = firebase_admin.initialize_app(cred)

            _firestore_client = firestore.client()
            _set_firestore_status("connected", "Firestore connected.")
            return _firestore_client
        except ImportError:
            _set_firestore_status(
                "unavailable",
                "Firestore unavailable because firebase-admin is not installed; records remain pending.",
            )
            return None
        except Exception:
            _set_firestore_status(
                "invalid_key",
                "Firestore key could not be initialized; records remain pending.",
            )
            return None


def is_firestore_reachable() -> Tuple[bool, str]:
    """Check whether Firestore can be contacted right now."""
    client = get_firestore_client()
    if client is None:
        if not ENABLE_FIRESTORE:
            return False, "Firestore disabled in configuration."
        _, credentials_exist = _resolve_credentials_path()
        if not credentials_exist:
            return False, "Firestore credentials file not found."
        return False, "Firestore client unavailable (offline or connection error)."

    try:
        # Lightweight probe query
        client.collection(PROTECTED_ASSETS_COLLECTION).limit(1).get()
        if _firestore_status == "offline":
            _set_firestore_status("connected", "Firestore connection restored.")
        return True, "Connected"
    except Exception as e:
        if _is_authorization_error(e):
            _set_firestore_status(
                "unauthorized",
                "Firestore key is invalid or access is unauthorized; records remain pending or failed.",
            )
        else:
            _set_firestore_status(
                "offline",
                "Firestore unavailable; running offline and keeping records local.",
            )
        return False, str(e)


def upload_protected_asset(record: dict) -> bool:
    """
    Create or update protected asset in Firestore idempotently using asset_id.
    """
    client = get_firestore_client()
    if client is None:
        return False

    try:
        doc_id = record.get("asset_id") or _asset_doc_id(record["path"])
        payload = {
            "path": record.get("path"),
            "asset_id": doc_id,
            "asset_name": record.get("asset_name"),
            "asset_type": record.get("asset_type"),
            "classification": record.get("classification"),
            "protected_at": record.get("protected_at"),
            "protected_by": record.get("protected_by"),
            "status": record.get("status"),
        }
        client.collection(PROTECTED_ASSETS_COLLECTION).document(doc_id).set(payload, merge=True)
        _record_firestore_success()
        return True
    except Exception as e:
        _record_firestore_error(e)
        return False


def fetch_pin_config() -> Tuple[str, dict]:
    """
    Download the Passkey PIN document (security_settings/pin_config), written
    ONLY by the Control Center. Never raises.

    Returns (status, data):
      ("found", {...doc fields...})  document exists
      ("missing", {})                Firestore reached, but no Passkey generated yet
      ("unreachable", {})            offline / no key / any error (NOT the same as missing)
    """
    client = get_firestore_client()
    if client is None:
        return "unreachable", {}

    try:
        snapshot = client.collection(SECURITY_SETTINGS_COLLECTION).document("pin_config").get()
        _record_firestore_success()
        if not snapshot.exists:
            return "missing", {}
        return "found", snapshot.to_dict() or {}
    except Exception as e:
        _record_firestore_error(e)
        return "unreachable", {}


def upload_security_settings(record: dict) -> bool:
    """
    DISABLED on purpose. The Passkey PIN is generated in the Control Center and
    the Monitoring Engine is download-only (see pin_auth.py). Uploading from here
    could overwrite the manager's Passkey, so this does nothing and reports
    success so any leftover pending PIN row is simply marked as handled.
    """
    return True


def upload_security_alert(alert: dict) -> bool:
    """
    Upload a single Security Alert to Firestore idempotently using alert_id.
    """
    client = get_firestore_client()
    if client is None:
        return False

    try:
        alert_id = alert.get("alert_id", "unknown")
        payload = {
            "alert_id": alert_id,
            "activity_id": alert.get("activity_id"),
            "user": alert.get("user"),
            "device": alert.get("device"),
            "asset": alert.get("asset"),
            "asset_path": alert.get("asset_path"),
            "action": alert.get("action"),
            "asset_classification": alert.get("asset_classification"),
            "risk_score": alert.get("risk_score"),
            "risk_level": alert.get("risk_level"),
            "risk_factors": alert.get("risk_factors") or [],
            "reason": alert.get("reason"),
            "timestamp": alert.get("timestamp"),
            "status": alert.get("status", "New"),
            "investigation_status": alert.get("investigation_status", "Not Started"),
            "updated_at": alert.get("updated_at"),
        }
        client.collection(SECURITY_ALERTS_COLLECTION).document(alert_id).set(payload, merge=True)
        _record_firestore_success()
        return True
    except Exception as e:
        _record_firestore_error(e)
        return False


def upload_incident_report(incident: dict) -> bool:
    """
    Upload a single Incident Report to Firestore idempotently using incident_id.
    """
    client = get_firestore_client()
    if client is None:
        return False

    try:
        incident_id = incident.get("incident_id", "unknown")
        payload = {
            "incident_id": incident_id,
            "related_alert_id": incident.get("related_alert_id"),
            "user": incident.get("user"),
            "device": incident.get("device"),
            "asset": incident.get("asset"),
            "asset_path": incident.get("asset_path"),
            "action": incident.get("action"),
            "classification": incident.get("classification"),
            "timestamp": incident.get("timestamp"),
            "risk_score": incident.get("risk_score"),
            "risk_level": incident.get("risk_level"),
            "risk_factors": incident.get("risk_factors") or [],
            "description": incident.get("description"),
            "investigation_status": incident.get("investigation_status") or incident.get("status", "New"),
            "investigation_notes": incident.get("investigation_notes") or incident.get("notes"),
            "resolution": incident.get("resolution"),
            "date_resolved": incident.get("date_resolved"),
            "created_at": incident.get("created_at") or incident.get("timestamp"),
            "updated_at": incident.get("updated_at") or incident.get("timestamp"),
        }
        client.collection(INCIDENT_REPORTS_COLLECTION).document(incident_id).set(payload, merge=True)
        _record_firestore_success()
        return True
    except Exception as e:
        _record_firestore_error(e)
        return False


def upload_activity(record: dict) -> bool:
    """
    Upload a single activity record to Firestore using its unique activity_id.
    Idempotent write: overwrites/updates the document rather than creating duplicates.
    """
    client = get_firestore_client()
    if client is None:
        return False

    try:
        activity_id = record.get("activity_id")
        payload = {
            "activity_id": activity_id,
            "user": record.get("user"),
            "device": record.get("device"),
            "asset": record.get("asset"),
            "action": record.get("action"),
            "timestamp": record.get("timestamp"),
            "classification": record.get("classification"),
            "risk_score": record.get("risk_score"),
            "risk_level": record.get("risk_level"),
            "risk_factors": record.get("risk_factors") or [],
            "advisory": record.get("advisory"),
            "asset_path": record.get("asset_path"),
            "protected_root": record.get("protected_root"),
        }

        if activity_id:
            client.collection(FIRESTORE_COLLECTION).document(activity_id).set(payload, merge=True)
        else:
            client.collection(FIRESTORE_COLLECTION).add(payload)
        _record_firestore_success()
        return True
    except Exception as e:
        _record_firestore_error(e)
        return False
