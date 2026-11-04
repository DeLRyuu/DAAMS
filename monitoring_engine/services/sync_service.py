"""
sync_service.py

Firestore Background Synchronization Service for DAAMS.

Enforces the offline-first architecture:
- Local SQLite database is always written first.
- This service is the SINGLE designated component that owns uploading
  local records to Firebase Firestore, eliminating race conditions and duplicate uploads.
- When internet connectivity is unavailable, records remain safely in SQLite with
  sync_status = 'PENDING'.
- When Firestore connectivity returns (or on application start), all pending
  and failed records are automatically uploaded.
- Uses idempotent document writes (via stable primary keys) so retries never create duplicates.
- Marks records as 'SYNCED' only after Firestore confirms successful persistence.
"""

import threading
from typing import Optional, Tuple

from core import database
from services import firestore_logger

# How often (in seconds) the background sync loop checks for pending records
DEFAULT_SYNC_INTERVAL_SECONDS = 10

_sync_thread: Optional[threading.Thread] = None
_stop_event = threading.Event()
_trigger_event = threading.Event()
_sync_lock = threading.Lock()


def sync_batch() -> Tuple[int, int]:
    """
    Attempt to synchronize all currently pending records from SQLite to Firestore.
    Returns (synced_count, failed_count).
    Thread-safe; does nothing if another sync is currently executing.
    """
    if not _sync_lock.acquire(blocking=False):
        return 0, 0

    synced_total = 0
    failed_total = 0

    try:
        # Check connectivity first
        reachable, _ = firestore_logger.is_firestore_reachable()
        if not reachable:
            return 0, 0

        # 1. Sync Security Settings (PIN)
        settings_pending = database.get_pending_sync_records("security_settings")
        for rec in settings_pending:
            if firestore_logger.upload_security_settings(rec):
                database.mark_as_synced("security_settings", "key", rec["key"])
                synced_total += 1
            else:
                database.mark_as_failed("security_settings", "key", rec["key"], "Upload failed")
                failed_total += 1

        # 2. Sync Protected Assets
        assets_pending = database.get_pending_sync_records("protected_assets")
        for rec in assets_pending:
            if firestore_logger.upload_protected_asset(rec):
                database.mark_as_synced("protected_assets", "path", rec["path"])
                synced_total += 1
            else:
                database.mark_as_failed("protected_assets", "path", rec["path"], "Upload failed")
                failed_total += 1

        # 3. Sync Activity Logs
        activity_pending = database.get_pending_sync_records("activity_logs")
        for rec in activity_pending:
            if firestore_logger.upload_activity(rec):
                database.mark_as_synced("activity_logs", "activity_id", rec["activity_id"])
                synced_total += 1
            else:
                database.mark_as_failed("activity_logs", "activity_id", rec["activity_id"], "Upload failed")
                failed_total += 1

        # 4. Sync Security Alerts
        alerts_pending = database.get_pending_sync_records("security_alerts")
        for rec in alerts_pending:
            if firestore_logger.upload_security_alert(rec):
                database.mark_as_synced("security_alerts", "alert_id", rec["alert_id"])
                synced_total += 1
            else:
                database.mark_as_failed("security_alerts", "alert_id", rec["alert_id"], "Upload failed")
                failed_total += 1

        # 5. Sync Incident Reports
        incidents_pending = database.get_pending_sync_records("incident_reports")
        for rec in incidents_pending:
            if firestore_logger.upload_incident_report(rec):
                database.mark_as_synced("incident_reports", "incident_id", rec["incident_id"])
                synced_total += 1
            else:
                database.mark_as_failed("incident_reports", "incident_id", rec["incident_id"], "Upload failed")
                failed_total += 1

        if synced_total > 0:
            print(f"[SYNC] Successfully uploaded {synced_total} pending record(s) to Firestore.")

        return synced_total, failed_total
    finally:
        _sync_lock.release()


def _sync_worker_loop():
    """Background worker loop that periodically flushes pending records."""
    while not _stop_event.is_set():
        try:
            sync_batch()
        except Exception as e:
            print(f"[SYNC WARNING] Unexpected error in sync loop: {e}")

        # Wait for the next interval or an explicit trigger signal
        _trigger_event.wait(timeout=DEFAULT_SYNC_INTERVAL_SECONDS)
        _trigger_event.clear()


def start_sync_service():
    """Start the background synchronization thread."""
    global _sync_thread
    if _sync_thread is not None and _sync_thread.is_alive():
        return

    _stop_event.clear()
    _trigger_event.clear()
    _sync_thread = threading.Thread(target=_sync_worker_loop, name="DAAMS-SyncWorker", daemon=True)
    _sync_thread.start()


def stop_sync_service(timeout: float = 3.0):
    """Signal the background synchronization thread to stop and wait for it."""
    global _sync_thread
    _stop_event.set()
    _trigger_event.set()
    if _sync_thread is not None and _sync_thread.is_alive():
        _sync_thread.join(timeout=timeout)
    _sync_thread = None


def trigger_sync():
    """Wake up the sync worker immediately to upload newly saved records."""
    _trigger_event.set()
