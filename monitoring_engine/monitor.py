"""
monitor.py

Watchdog-based filesystem monitor for the DAAMS Protected Asset Registry.

Phase 1 scope (unchanged):
- Detect: Create, Modify, Rename, Delete
- Do NOT attempt to detect Open, Copy, or Move yet (explained in README).
- Never watches an entire drive.

Phase 4 change: instead of watching a single hard-coded PROTECTED_PATH,
this module reads the Protected Asset Registry (asset_registry.py) and
sets up one Watchdog watch per currently-protected asset -- each
protected FILE is watched individually (filtered to just that file);
each protected FOLDER is watched recursively. Assets that are missing on
disk, or that somehow slipped past registry-time validation (e.g. a
drive root), are skipped with a warning rather than crashing the whole
engine.

Phase 4.1 change: the registry is no longer read only once at startup.
Every REGISTRY_RELOAD_INTERVAL_SECONDS (config.py), the engine re-reads
protected_assets.json and reconciles its watches to match -- so
protecting, reclassifying, or unprotecting something via the context
menu takes effect on an already-running engine within a few seconds,
with no restart needed. This is a simple periodic re-scan of a small
local file, not a background sync queue or retry system.

Design notes:
- Watchdog's FileSystemEventHandler naturally distinguishes "moved" events
  (which covers same-volume Renames) from "created"/"deleted"/"modified".
  A rename shows up as a FileMovedEvent with both src_path and dest_path
  inside the SAME watched folder, so we report it as "Rename" and show the
  path change. If a file is moved OUT of the protected folder entirely,
  Watchdog still reports it as a moved/deleted event depending on OS,
  which is a known limitation documented in the README.
"""

import os
import time

from watchdog.events import FileSystemEventHandler
from watchdog.observers import Observer

from activity_logger import record_activity
from config import is_disallowed_root, REGISTRY_RELOAD_INTERVAL_SECONDS
from asset_registry import get_all_assets


class ProtectedAssetEventHandler(FileSystemEventHandler):
    """
    Translates raw Watchdog filesystem events into DAAMS activity records
    for the configured protected path.
    """

    def on_created(self, event):
        if event.is_directory:
            return  # Phase 1 focuses on file-level activity, not folder creation noise.
        record_activity(asset_path=event.src_path, action="Create")

    def on_modified(self, event):
        if event.is_directory:
            return
        record_activity(asset_path=event.src_path, action="Modify")

    def on_deleted(self, event):
        if event.is_directory:
            return
        record_activity(asset_path=event.src_path, action="Delete")

    def on_moved(self, event):
        # A "moved" event where the file stays within the protected path
        # is treated as a Rename. (True Move-out-of-folder tracking is a
        # Phase 2+ concern -- see README limitations.)
        if event.is_directory:
            return
        asset_description = f"{event.src_path} -> {event.dest_path}"
        record_activity(asset_path=asset_description, action="Rename")


def _validate_asset_for_watching(asset: dict) -> bool:
    """
    Re-validate a registry entry right before watching it. The registry
    already validates at protect-time (asset_registry.protect_asset), but
    time may have passed since then -- a protected file/folder could have
    since been deleted or moved outside DAAMS's knowledge. Rather than
    crashing the whole engine over one bad entry, skip it with a warning
    and keep watching everything else.
    """
    path = asset.get("path", "")

    if is_disallowed_root(path):
        print(f"[WARNING] Protected asset '{path}' looks like a drive root; skipping for safety.")
        return False

    if not os.path.exists(path):
        print(f"[WARNING] Protected asset no longer exists on disk, skipping: {path}")
        return False

    return True


def _watch_asset(observer: Observer, asset: dict) -> dict:
    """
    Schedule a Watchdog watch for a single ALREADY-VALIDATED protected
    asset. Returns a small info dict used to track this watch (so it can
    later be unscheduled if protection is removed, and so classification
    changes can be detected on the next reconcile).
    """
    path = asset["path"]
    is_file = asset.get("asset_type") == "file" or os.path.isfile(path)

    # Watchdog observers watch directories. If the protected asset is a
    # single FILE, we watch its parent folder but filter events down to
    # just that file, so we still respect "protect only what was
    # selected" rather than silently watching the whole containing folder.
    if is_file:
        watch_dir = os.path.dirname(os.path.abspath(path))
        handler = _SingleFileFilterHandler(os.path.abspath(path))
        recursive = False
    else:
        watch_dir = path
        handler = ProtectedAssetEventHandler()
        recursive = True

    observed_watch = observer.schedule(handler, watch_dir, recursive=recursive)
    return {
        "observed_watch": observed_watch,
        "asset_type": "file" if is_file else "folder",
        "classification": asset.get("classification", "?"),
    }


def start_monitoring():
    """
    Read the Protected Asset Registry and begin monitoring every
    currently-protected file and folder. Blocks (runs) until interrupted
    with Ctrl+C.

    While running, the registry is periodically re-read
    (REGISTRY_RELOAD_INTERVAL_SECONDS) and watches are reconciled to
    match it -- so protecting, reclassifying, or removing protection via
    the context menu is picked up automatically, without restarting.
    """
    print("=" * 60)
    print("DAAMS Python Monitoring Engine - Phase 4")
    print("=" * 60)

    observer = Observer()
    observer.start()

    watched = {}          # path -> info dict from _watch_asset
    invalid_warned = set()  # paths already warned about (missing/disallowed)

    def reconcile(initial: bool = False):
        current_assets = {a["path"]: a for a in get_all_assets(only_protected=True)}

        # New (or newly re-protected) assets -> start watching them.
        for path, asset in current_assets.items():
            if path in watched or path in invalid_warned:
                continue
            if not _validate_asset_for_watching(asset):
                invalid_warned.add(path)
                continue
            info = _watch_asset(observer, asset)
            watched[path] = info
            label = "Watching" if initial else "\n[REGISTRY] Now watching newly protected"
            print(f"{label} [{info['classification']}] {info['asset_type']}: {path}")

        # No longer protected (or removed from the registry) -> stop watching.
        for path in list(watched.keys()):
            if path not in current_assets:
                observer.unschedule(watched[path]["observed_watch"])
                del watched[path]
                invalid_warned.discard(path)
                print(f"\n[REGISTRY] Protection removed for '{path}' "
                      f"-- DAAMS stopped monitoring it.")

        # Still protected, but reclassified -- no watch change needed,
        # just let the administrator know the terminal reflects reality.
        for path, asset in current_assets.items():
            if path in watched:
                new_classification = asset.get("classification", "?")
                if watched[path]["classification"] != new_classification:
                    print(f"\n[REGISTRY] Classification changed for '{path}': "
                          f"{watched[path]['classification']} -> {new_classification}")
                    watched[path]["classification"] = new_classification

    reconcile(initial=True)

    if not watched:
        print("No protected assets found in the registry.")
        print("Right-click a file or folder in Windows Explorer and choose")
        print("DAAMS -> Protect This File / Protect This Folder.")
        print("DAAMS will start watching it automatically within a few "
              "seconds -- no restart needed.\n")
    else:
        print(f"\n{len(watched)} protected asset(s) being monitored.")

    print("Press Ctrl+C to stop.\n")

    try:
        while True:
            time.sleep(REGISTRY_RELOAD_INTERVAL_SECONDS)
            reconcile()
    except KeyboardInterrupt:
        print("\nStopping DAAMS monitoring engine...")
        observer.stop()
    observer.join()
    print("Monitoring stopped cleanly.")


class _SingleFileFilterHandler(ProtectedAssetEventHandler):
    """
    Used when the protected asset is a single file rather than a folder.
    Watchdog must watch the parent directory, so this handler filters out
    events for any other file in that directory, ensuring DAAMS only
    reports activity on the specific protected file.
    """

    def __init__(self, target_file: str):
        super().__init__()
        self.target_file = os.path.normcase(os.path.abspath(target_file))

    def _matches(self, path: str) -> bool:
        return os.path.normcase(os.path.abspath(path)) == self.target_file

    def on_created(self, event):
        if not event.is_directory and self._matches(event.src_path):
            super().on_created(event)

    def on_modified(self, event):
        if not event.is_directory and self._matches(event.src_path):
            super().on_modified(event)

    def on_deleted(self, event):
        if not event.is_directory and self._matches(event.src_path):
            super().on_deleted(event)

    def on_moved(self, event):
        if not event.is_directory and self._matches(event.src_path):
            super().on_moved(event)
