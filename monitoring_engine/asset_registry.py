"""
asset_registry.py

The Protected Asset Registry (Phase 4).

This is the SINGLE shared implementation of "what is protected right
now" for the whole DAAMS system. It is used by:
  - The Windows Explorer context-menu scripts (protect_asset_cli.py,
    remove_protection_cli.py)
  - The Monitoring Engine (monitor.py), which reads it to know what to
    watch
  - (Later) the C# WPF Admin Control Center, which will call into the
    same underlying data rather than maintaining a separate copy --
    including change_classification() below, which is intentionally
    NOT exposed from the right-click menu; reclassifying an asset is
    reserved for the WPF console by project decision

Storage model:
  - A local JSON file (protected_assets.json) is the SOURCE OF TRUTH.
    The Monitoring Engine reads ONLY this file, so it keeps working even
    if Firebase/Firestore or the network is temporarily unavailable --
    this matches the Phase 4 requirement directly.
  - Firestore (protected_assets collection) is kept in sync on a
    best-effort basis via firestore_logger.upload_protected_asset(),
    using the same "never crash, just warn" pattern established for
    activity_logs in Phase 3.

Each registry entry follows this structure:
{
    "path": "C:\\Company\\Payroll.xlsx",
    "asset_name": "Payroll.xlsx",
    "asset_type": "file",              # "file" or "folder"
    "classification": "Confidential",  # Public / Sensitive / Private / Confidential
    "protected_at": "2026-09-08 20:30:00",
    "protected_by": "admin",
    "status": "protected"              # "protected" or "unprotected"
}
"""

import getpass
import json
import os
from datetime import datetime
from typing import Optional

from config import is_disallowed_root
import firestore_logger

REGISTRY_FILE = os.path.join(os.path.dirname(__file__), "protected_assets.json")

VALID_CLASSIFICATIONS = ("Public", "Sensitive", "Private", "Confidential")


def _normalize(path: str) -> str:
    """
    Normalize a path into a stable registry key so that the same asset is
    always recognized as the same entry regardless of trailing slashes,
    relative segments, or case differences (Windows paths are
    case-insensitive).
    """
    return os.path.normcase(os.path.normpath(os.path.abspath(path)))


def _load_registry() -> dict:
    """
    Load the registry as a dict keyed by normalized path -> asset record.
    Returns an empty dict if the file doesn't exist yet, or if it's
    unreadable/corrupted -- a missing or damaged registry must never
    crash a caller; it should just behave as "nothing is protected yet".
    """
    if not os.path.exists(REGISTRY_FILE):
        return {}
    try:
        with open(REGISTRY_FILE, "r", encoding="utf-8") as f:
            return json.load(f)
    except (OSError, json.JSONDecodeError) as e:
        print(f"[REGISTRY WARNING] Could not read protected asset registry: {e}")
        return {}


def _save_registry(data: dict) -> bool:
    """
    Save the registry atomically: write to a temp file, then rename it
    over the real file. This means a crash or power loss mid-write can
    never leave a half-written, corrupted registry behind -- the old
    file stays intact until the new one is fully written.
    """
    try:
        tmp_path = REGISTRY_FILE + ".tmp"
        with open(tmp_path, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)
        os.replace(tmp_path, REGISTRY_FILE)
        return True
    except OSError as e:
        print(f"[REGISTRY WARNING] Could not save protected asset registry: {e}")
        return False


def is_protected(path: str) -> bool:
    """Return True if `path` is currently a protected asset."""
    record = _load_registry().get(_normalize(path))
    return bool(record and record.get("status") == "protected")


def get_asset(path: str) -> Optional[dict]:
    """Return the registry record for `path` (protected or not), or None."""
    return _load_registry().get(_normalize(path))


def get_all_assets(only_protected: bool = True) -> list:
    """
    Return all registry records as a list.
    By default, only currently-protected assets are returned -- this is
    what the Monitoring Engine calls to build its watch list.
    """
    assets = list(_load_registry().values())
    if only_protected:
        assets = [a for a in assets if a.get("status") == "protected"]
    return assets


def protect_asset(path: str, classification: str, protected_by: str = None) -> tuple:
    """
    Register `path` (a file or folder) as a protected asset.

    Returns (success: bool, message: str).

    Rejects (does not create a duplicate) if the path is already
    protected -- the caller should direct the admin to the WPF Admin
    Control Center if they want to change its classification (see
    change_classification() below, which still exists as the shared
    logic that console will call -- it's just not exposed from the
    Windows right-click menu).
    """
    if classification not in VALID_CLASSIFICATIONS:
        return False, (
            f"Invalid classification '{classification}'. Must be one of: "
            f"{', '.join(VALID_CLASSIFICATIONS)}."
        )

    if is_disallowed_root(path):
        return False, f"'{path}' looks like a drive root. DAAMS cannot protect an entire disk."

    if not os.path.exists(path):
        return False, f"Path does not exist: {path}"

    key = _normalize(path)
    registry = _load_registry()

    existing = registry.get(key)
    if existing and existing.get("status") == "protected":
        return False, (
            f"'{path}' is already protected (classification: "
            f"{existing.get('classification')}). Use the DAAMS Admin "
            f"Control Center to change its classification."
        )

    asset_type = "folder" if os.path.isdir(path) else "file"
    record = {
        "path": path,
        "asset_name": os.path.basename(os.path.normpath(path)),
        "asset_type": asset_type,
        "classification": classification,
        "protected_at": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
        "protected_by": protected_by or getpass.getuser(),
        "status": "protected",
    }

    registry[key] = record
    if not _save_registry(registry):
        return False, "Failed to save the protected asset registry locally. Protection was NOT applied."

    try:
        firestore_logger.upload_protected_asset(record)
    except Exception as e:
        print(f"[FIRESTORE WARNING] Could not sync protected asset to Firestore: {e}")

    return True, f"'{path}' is now protected as {classification}."


def change_classification(path: str, new_classification: str) -> tuple:
    """
    Change the classification of an already-protected asset WITHOUT
    removing/recreating it. The underlying file/folder is never touched
    -- only the registry metadata changes.

    Returns (success: bool, message: str).
    """
    if new_classification not in VALID_CLASSIFICATIONS:
        return False, (
            f"Invalid classification '{new_classification}'. Must be one of: "
            f"{', '.join(VALID_CLASSIFICATIONS)}."
        )

    key = _normalize(path)
    registry = _load_registry()
    record = registry.get(key)

    if not record or record.get("status") != "protected":
        return False, f"'{path}' is not currently protected. Nothing to change."

    record["classification"] = new_classification
    registry[key] = record
    if not _save_registry(registry):
        return False, "Failed to save the classification change locally."

    try:
        firestore_logger.upload_protected_asset(record)
    except Exception as e:
        print(f"[FIRESTORE WARNING] Could not sync classification change to Firestore: {e}")

    return True, f"'{path}' classification changed to {new_classification}."


def remove_protection(path: str) -> tuple:
    """
    Mark an asset as unprotected. This ONLY updates registry metadata:
      - The Monitoring Engine will stop watching it (after restart/reload).
      - The actual file/folder on disk is NEVER touched or deleted.
      - Existing activity_log entries for it are NEVER deleted.

    Returns (success: bool, message: str).
    """
    key = _normalize(path)
    registry = _load_registry()
    record = registry.get(key)

    if not record or record.get("status") != "protected":
        return False, f"'{path}' is not currently protected."

    record["status"] = "unprotected"
    registry[key] = record
    if not _save_registry(registry):
        return False, "Failed to save the protection removal locally."

    try:
        firestore_logger.upload_protected_asset(record)
    except Exception as e:
        print(f"[FIRESTORE WARNING] Could not sync protection removal to Firestore: {e}")

    return True, f"Protection removed from '{path}'. The file/folder itself was not touched."
