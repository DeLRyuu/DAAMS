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

Changes in this version (Bugs 3 and 4):
- The Firestore reachability probe (1 read) now runs ONLY when something is
  pending. Before, it ran every 10 seconds forever (~8,640 reads/day per
  engine PC, about 17% of the 50,000/day free tier, even when idle).
- Once a record is confirmed in Firestore it is removed from local SQLite
  (activity logs, alerts, incident reports). The Passkey PIN (security_settings)
  and the protected assets are never removed. See database.purge_synced_records().
"""

import threading
from typing import Optional, Tuple

from core import database
from services import firestore_logger

# How often (in seconds) the background sync loop checks for pending records
DEFAULT_SYNC_INTERVAL_SECONDS = 10

# Bug 3: seconds a confirmed (SYNCED) record stays locally before it is removed.
# Not zero because save_activity() de-duplicates repeated events that arrive within 1 second.
LOCAL_PURGE_GRACE_SECONDS = 5

# DEMO SWITCH: True  = delete local rows right after Firestore confirms them (normal behavior).
#              False = keep everything in local SQLite (use this for the 1-laptop offline demo
#                      if you want the old data to stay visible).
# The Passkey PIN and protected assets are never deleted, whatever this is set to.
PURGE_ENABLED = True

_sync_thread: Optional[threading.Thread] = None
_stop_event = threading.Event()
_trigger_event = threading.Event()
_sync_lock = threading.Lock()


def _pending_total() -> int:
    """Count PENDING + FAILED rows across all synced tables (local query, costs no Firestore reads)."""
    summary = database.get_sync_summary()
    return sum(counts.get("PENDING", 0) + counts.get("FAILED", 0) for counts in summary.values())


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
        # Nothing to upload -> do not touch the network at all (saves the probe read).
        if _pending_total() == 0:
            return 0, 0

        # Check connectivity first
        reachable, _ = firestore_logger.is_firestore_reachable()
        if not reachable:
            return 0, 0

        # 1. Sync Security Settings (PIN) - upload is intentionally a no-op, see firestore_logger
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


def _purge_synced_records() -> None:
    """Bug 3: remove records that Firestore has confirmed from local SQLite (local only, no Firestore reads)."""
    if not PURGE_ENABLED:
        return
    deleted = database.purge_synced_records(LOCAL_PURGE_GRACE_SECONDS)
    total = sum(deleted.values())
    if total > 0:
        print(f"[SYNC] Removed {total} synced record(s) from the local database.")


def _sync_worker_loop():
    """Background worker loop that periodically flushes pending records."""
    while not _stop_event.is_set():
        try:
            sync_batch()
        except Exception as e:
            print(f"[SYNC WARNING] Unexpected error in sync loop: {e}")

        try:
            _purge_synced_records()
        except Exception as e:
            print(f"[SYNC WARNING] Local purge failed: {e}")

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
