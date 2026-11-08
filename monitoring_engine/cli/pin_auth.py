"""
pin_auth.py  --  Passkey PIN (download-only on the Monitoring Engine)

The shared DAAMS PIN is generated in the Control Center (Settings -> Passkey PIN)
and saved to Firestore (security_settings/pin_config) as a salted SHA-256 hash:
    SHA256(salt + pin), lowercase hex.

This module NEVER creates, changes, or uploads a PIN. It only:
  1. DOWNLOADS the Passkey hash from Firestore (in the background, and once more
     right before a PIN prompt) and keeps a LOCAL copy, so PIN checks keep working
     offline.
  2. VERIFIES a typed PIN against that local copy.
  3. Gates Protect This File/Folder behind that PIN (confirm_with_pin).

Safe-replace rules for the local copy:
  - Cloud unreachable            -> keep the current local copy (never wipe it).
  - Cloud document missing       -> keep the current local copy.
  - Cloud document malformed     -> keep the current local copy.
  - Cloud document valid + new   -> replace the local copy (the old one stops working).
  - No Passkey anywhere yet      -> Protect is blocked with a clear message.
    There is NO default PIN any more. An old local default PIN (is_default) is ignored.

Local copy = security_settings.json + the SQLite security_settings row, written the
same way the Control Center's StoreLocalPin does (sync_status = 'SYNCED', so the
engine's sync service never tries to upload it).
"""

import hashlib
import hmac
import json
import os
import re
import sqlite3
import sys
import threading
from datetime import datetime

_ENGINE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ENGINE_DIR not in sys.path:
    sys.path.insert(0, _ENGINE_DIR)

from config import settings
from config.settings import DATA_DIR
from core import database
from services import firestore_logger

# glass_ui (Tkinter/Pillow) is imported lazily inside the UI functions so the
# background engine can import this module without loading any GUI code.

PIN_SYNC_INTERVAL_SECONDS = getattr(settings, "PIN_SYNC_INTERVAL_SECONDS", 30)
PRE_PROMPT_REFRESH_TIMEOUT_SECONDS = 3.0

SETTINGS_FILE = os.path.join(DATA_DIR, "security_settings.json")

_HEX64 = re.compile(r"^[0-9a-f]{64}$")
_sync_lock = threading.Lock()


# ----------------------------------------------------------------------
# Hashing / verification
# ----------------------------------------------------------------------
def _hash_pin(pin: str, salt: str) -> str:
    return hashlib.sha256((salt + pin).encode("utf-8")).hexdigest()


def _is_valid_record(rec) -> bool:
    if not isinstance(rec, dict):
        return False
    pin_hash = str(rec.get("pin_hash") or "").strip().lower()
    salt = rec.get("salt")
    return bool(_HEX64.match(pin_hash)) and isinstance(salt, str) and salt.strip() != ""


# ----------------------------------------------------------------------
# Local copy (read)
# ----------------------------------------------------------------------
def _load_db_copy() -> dict:
    try:
        rec = database.get_security_settings()
        if rec and rec.get("pin_hash"):
            return dict(rec)
    except Exception:
        pass
    return {}


def _load_json_copy() -> dict:
    if not os.path.exists(SETTINGS_FILE):
        return {}
    try:
        with open(SETTINGS_FILE, "r", encoding="utf-8") as f:
            data = json.load(f)
        return data if isinstance(data, dict) else {}
    except (OSError, json.JSONDecodeError) as e:
        print(f"[SECURITY WARNING] Could not read PIN settings file: {e}")
        return {}


def _load_settings() -> dict:
    """Local Passkey record: the newer (by updated_at) of the SQLite row and the JSON file."""
    candidates = [c for c in (_load_db_copy(), _load_json_copy()) if c.get("pin_hash")]
    if not candidates:
        return {}
    # max() keeps the first one on a tie, so the SQLite row wins when both are equal.
    return max(candidates, key=lambda c: str(c.get("updated_at") or ""))


def _usable_local() -> dict:
    """Local record, but only if it is a real Passkey (not the retired default PIN)."""
    rec = _load_settings()
    if not rec or rec.get("is_default") or not _is_valid_record(rec):
        return {}
    return rec


def has_passkey() -> bool:
    return bool(_usable_local())


def verify_pin(candidate: str) -> bool:
    """Check a typed PIN against the LOCAL Passkey hash (works offline)."""
    if not candidate:
        return False
    rec = _usable_local()
    if not rec:
        return False
    computed = _hash_pin(candidate, rec["salt"])
    return hmac.compare_digest(computed, str(rec["pin_hash"]).strip().lower())


# ----------------------------------------------------------------------
# Local copy (write)
# ----------------------------------------------------------------------
def _write_json_copy(rec: dict) -> bool:
    try:
        os.makedirs(DATA_DIR, exist_ok=True)
        tmp_path = SETTINGS_FILE + ".tmp"
        with open(tmp_path, "w", encoding="utf-8") as f:
            json.dump(rec, f, indent=2)
        os.replace(tmp_path, SETTINGS_FILE)
        return True
    except OSError as e:
        print(f"[SECURITY WARNING] Could not save the Passkey PIN file: {e}")
        return False


def _write_db_copy(rec: dict) -> bool:
    try:
        now = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        conn = sqlite3.connect(database.get_db_path(), timeout=5)
        try:
            with conn:
                conn.execute(
                    """
                    INSERT INTO security_settings
                        (key, pin_hash, salt, updated_at, updated_by, is_default,
                         sync_status, synced_at, sync_error)
                    VALUES ('pin_config', ?, ?, ?, ?, 0, 'SYNCED', ?, NULL)
                    ON CONFLICT(key) DO UPDATE SET
                        pin_hash = excluded.pin_hash,
                        salt = excluded.salt,
                        updated_at = excluded.updated_at,
                        updated_by = excluded.updated_by,
                        is_default = 0,
                        sync_status = 'SYNCED',
                        synced_at = excluded.synced_at,
                        sync_error = NULL
                    """,
                    (rec["pin_hash"], rec["salt"], rec["updated_at"], rec["updated_by"], now),
                )
        finally:
            conn.close()
        return True
    except Exception as e:
        print(f"[SECURITY WARNING] Could not save the Passkey PIN to SQLite: {e}")
        return False


def _store_local(rec: dict) -> bool:
    """Replace the local copy. Succeeds if at least one copy was written."""
    json_ok = _write_json_copy(rec)
    db_ok = _write_db_copy(rec)
    return json_ok or db_ok


# ----------------------------------------------------------------------
# Download from Firestore
# ----------------------------------------------------------------------
def sync_passkey_from_cloud() -> str:
    """
    One download attempt. Never raises. Returns one of:
      "updated"      new Passkey downloaded and stored locally
      "unchanged"    cloud Passkey matches the local copy
      "missing"      Firestore reached but no Passkey exists yet (local copy untouched)
      "unreachable"  offline / no key / error (local copy untouched)
      "invalid"      cloud document malformed (local copy untouched)
      "save_failed"  downloaded but could not be stored (local copy untouched)
    """
    with _sync_lock:
        try:
            status, cloud = firestore_logger.fetch_pin_config()
        except Exception:
            return "unreachable"

        if status == "unreachable":
            return "unreachable"
        if status == "missing":
            return "missing"
        if not _is_valid_record(cloud):
            return "invalid"

        new_rec = {
            "pin_hash": str(cloud["pin_hash"]).strip().lower(),
            "salt": cloud["salt"],
            "updated_at": str(cloud.get("updated_at") or datetime.now().strftime("%Y-%m-%d %H:%M:%S")),
            "updated_by": str(cloud.get("updated_by") or "control-center"),
            "is_default": False,
        }

        local = _usable_local()
        if local and local.get("pin_hash") == new_rec["pin_hash"] and local.get("salt") == new_rec["salt"]:
            return "unchanged"

        if not _store_local(new_rec):
            return "save_failed"

        print("[PIN] New Passkey PIN downloaded from the Control Center; the previous one no longer works.")
        return "updated"


def refresh_now(timeout: float = PRE_PROMPT_REFRESH_TIMEOUT_SECONDS) -> None:
    """Try one download but never wait longer than `timeout` seconds (offline PCs must not hang)."""
    t = threading.Thread(target=sync_passkey_from_cloud, name="daams-pin-refresh", daemon=True)
    t.start()
    t.join(timeout)


def start_background_sync(stop_event: threading.Event, interval: float = None) -> threading.Thread:
    """Start the engine's periodic Passkey download. Stops when `stop_event` is set."""
    wait_seconds = float(interval or PIN_SYNC_INTERVAL_SECONDS)

    def _run():
        while not stop_event.is_set():
            try:
                sync_passkey_from_cloud()
            except Exception:
                pass  # never let this thread die
            if stop_event.wait(wait_seconds):
                break

    t = threading.Thread(target=_run, name="daams-pin-sync", daemon=True)
    t.start()
    return t


# ----------------------------------------------------------------------
# UI gate used by protect_asset_cli.py
# ----------------------------------------------------------------------
def prompt_pin(title: str = "PIN required", message: str = "Enter your DAAMS PIN:") -> str:
    from ui import glass_ui
    return glass_ui.ask_pin(message, title=title)


def confirm_with_pin(action_description: str) -> bool:
    """
    Ask for the Passkey PIN. `action_description` completes "to ___",
    e.g. "protect this file". True only if the correct PIN was entered.
    """
    from ui import glass_ui

    refresh_now()  # pick up a freshly regenerated Passkey before checking

    if not has_passkey():
        glass_ui.show_message(
            "warning",
            "No Passkey PIN yet",
            "Ask your manager to generate a Passkey PIN in the Control Center "
            "(Settings -> Passkey PIN).\n\n"
            "This PC downloads it automatically once it is online.",
        )
        return False

    pin = prompt_pin(message=f"Enter your DAAMS PIN to {action_description}.")
    if pin is None:
        return False  # cancelled

    if not verify_pin(pin):
        glass_ui.show_message("error", "Incorrect PIN", "The PIN you entered is incorrect. The action was cancelled.")
        return False

    return True
