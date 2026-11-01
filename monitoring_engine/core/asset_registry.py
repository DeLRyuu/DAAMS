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
  - SQLite is the SOURCE OF TRUTH for protected assets.
  - A local JSON file (protected_assets.json) is read only when SQLite
    raises an error, for compatibility during database outages.
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

from config.settings import DATA_DIR, is_disallowed_root
from core import database
from services import firestore_logger, sync_service

REGISTRY_FILE = os.path.join(DATA_DIR, "protected_assets.json")

VALID_CLASSIFICATIONS = ("Public", "Sensitive", "Private", "Confidential")


def _normalize(path: str) -> str:
    """
    Normalize a path into a stable registry key so that the same asset is
    always recognized as the same entry regardless of trailing slashes,
    relative segments, or case differences (Windows paths are
    case-insensitive).
    """
    return os.path.normcase(os.path.normpath(os.path.abspath(path)))


def _is_within(child: str, parent: str) -> bool:
    """Return whether child is parent or is below it at a path boundary."""
    child_path = _normalize(child)
    parent_path = _normalize(parent).rstrip("\\")
    return child_path == parent_path or child_path.startswith(parent_path + "\\")


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


def is_protected(path: str) -> bool:
    """Return True if `path` is currently a protected asset."""
    record = get_asset(path)
    return bool(record and record.get("status") == "protected")


def get_asset(path: str) -> Optional[dict]:
    """Return the registry record for `path` (protected or not), or None."""
    try:
        return database.get_protected_asset(path)
    except Exception as e:
        print(f"[REGISTRY WARNING] Could not read asset from SQLite: {e}")
        return _load_registry().get(_normalize(path))


def get_all_assets(only_protected: bool = True) -> list:
    """
    Return all registry records as a list.
    By default, only currently-protected assets are returned -- this is
    what the Monitoring Engine calls to build its watch list.
    """
    try:
        return database.get_all_protected_assets(only_protected=only_protected)
    except Exception as e:
        print(f"[REGISTRY WARNING] Could not read assets from SQLite: {e}")
        assets = list(_load_registry().values())
        if only_protected:
            assets = [a for a in assets if a.get("status") == "protected"]
        return assets


def get_covering_asset(path: str) -> Optional[dict]:
    """Return the most-specific protected asset covering `path`, if any."""
    matches = [
        asset for asset in get_all_assets()
        if asset.get("path") and _is_within(path, asset["path"])
        and (
            asset.get("asset_type") == "folder"
            or _normalize(path) == _normalize(asset["path"])
        )
    ]
    return max(matches, key=lambda asset: len(_normalize(asset["path"])), default=None)


def list_children(folder: str) -> list:
    """Return registered descendants of `folder`, including unprotected rows."""
    return [
        asset for asset in get_all_assets(only_protected=False)
        if asset.get("path")
        and _normalize(asset["path"]) != _normalize(folder)
        and _is_within(asset["path"], folder)
    ]


def relocate_asset(old_path: str, new_path: str) -> bool:
    """Keep a protected registry entry attached to a folder after it moves."""
    try:
        relocated = database.relocate_protected_asset(old_path, new_path)
    except Exception as e:
        print(f"[REGISTRY WARNING] Could not relocate protected asset: {e}")
        return False
    if relocated:
        try:
            sync_service.trigger_sync()
        except Exception as e:
            print(f"[SYNC WARNING] Could not schedule protected asset sync: {e}")
    return relocated


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

    try:
        existing = database.get_protected_asset(path)
    except Exception as e:
        print(f"[REGISTRY WARNING] Could not read asset from SQLite: {e}")
        existing = _load_registry().get(_normalize(path))

    if existing and existing.get("status") == "protected":
        return False, (
            f"'{path}' is already protected (classification: "
            f"{existing.get('classification')}). Use the DAAMS Admin "
            f"Control Center to change its classification."
        )

    asset_type = "folder" if os.path.isdir(path) else "file"
    record = {
        "path": existing.get("path", path) if existing else path,
        "asset_name": os.path.basename(os.path.normpath(path)),
        "asset_type": asset_type,
        "classification": classification,
        "protected_at": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
        "protected_by": protected_by or getpass.getuser(),
        "status": "protected",
    }

    try:
        if not database.upsert_protected_asset(record):
            return False, "Failed to save the protected asset registry to SQLite."
    except Exception as e:
        return False, f"Failed to save the protected asset registry to SQLite: {e}"

    # Trigger cloud sync
    try:
        sync_service.trigger_sync()
    except Exception as e:
        print(f"[REGISTRY WARNING] Could not trigger sync service: {e}")

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

    try:
        record = database.get_protected_asset(path)
    except Exception as e:
        print(f"[REGISTRY WARNING] Could not read asset from SQLite: {e}")
        record = _load_registry().get(_normalize(path))

    if not record or record.get("status") != "protected":
        return False, f"'{path}' is not currently protected. Nothing to change."

    record["classification"] = new_classification
    try:
        if not database.upsert_protected_asset(record):
            return False, "Failed to save the classification change to SQLite."
    except Exception as e:
        return False, f"Failed to save the classification change to SQLite: {e}"

    try:
        sync_service.trigger_sync()
    except Exception as e:
        print(f"[REGISTRY WARNING] Could not trigger sync service: {e}")

    return True, f"'{path}' classification changed to {new_classification}."


def remove_protection(path: str) -> tuple:
    """
    Mark an asset as unprotected. This ONLY updates registry metadata:
      - The Monitoring Engine will stop watching it (after restart/reload).
      - The actual file/folder on disk is NEVER touched or deleted.
      - Existing activity_log entries for it are NEVER deleted.

    Returns (success: bool, message: str).
    """
    try:
        record = database.get_protected_asset(path)
    except Exception as e:
        print(f"[REGISTRY WARNING] Could not read asset from SQLite: {e}")
        record = _load_registry().get(_normalize(path))

    if not record or record.get("status") != "protected":
        return False, f"'{path}' is not currently protected."

    record["status"] = "unprotected"
    try:
        if not database.upsert_protected_asset(record):
            return False, "Failed to save the protection removal to SQLite."
    except Exception as e:
        return False, f"Failed to save the protection removal to SQLite: {e}"

    try:
        sync_service.trigger_sync()
    except Exception as e:
        print(f"[REGISTRY WARNING] Could not trigger sync service: {e}")

    return True, f"Protection removed from '{path}'. The file/folder itself was not touched."
