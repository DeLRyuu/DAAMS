"""
monitor.py

Watchdog-based filesystem monitor for a single administrator-selected
protected path.

Phase 1 scope (per project instructions):
- Detect: Create, Modify, Rename, Delete
- Do NOT attempt to detect Open, Copy, or Move yet (explained in README).
- Only watch the explicitly configured protected path (file or folder).
  Never watches an entire drive.

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


def validate_protected_path(path: str) -> None:
    """
    Raise a clear, actionable error if the configured protected path is
    missing, invalid, or looks like an attempt to monitor an entire disk.
    """
    from config import is_disallowed_root

    if not path or not path.strip():
        raise ValueError(
            "PROTECTED_PATH is not set. Open config.py and set PROTECTED_PATH "
            "to a specific folder or file you want DAAMS to monitor."
        )

    if is_disallowed_root(path):
        raise ValueError(
            f"PROTECTED_PATH '{path}' looks like a drive root or filesystem root. "
            "DAAMS must only monitor administrator-selected assets, not an entire disk."
        )

    if not os.path.exists(path):
        raise FileNotFoundError(
            f"PROTECTED_PATH '{path}' does not exist. Check the path in config.py."
        )


def start_monitoring(protected_path: str):
    """
    Validate and begin monitoring the configured protected path.
    Blocks (runs) until interrupted with Ctrl+C.
    """
    validate_protected_path(protected_path)

    watch_target = protected_path
    is_file = os.path.isfile(protected_path)

    # Watchdog observers watch directories. If the administrator protected a
    # single FILE, we watch its parent folder but only report events whose
    # path matches that specific file, so we still respect "protect only
    # what was selected" rather than silently watching the whole folder.
    if is_file:
        watch_dir = os.path.dirname(os.path.abspath(protected_path))
        target_file = os.path.abspath(protected_path)
        handler = _SingleFileFilterHandler(target_file)
    else:
        watch_dir = protected_path
        handler = ProtectedAssetEventHandler()

    observer = Observer()
    observer.schedule(handler, watch_dir, recursive=not is_file)
    observer.start()

    print("=" * 60)
    print("DAAMS Python Monitoring Engine - Phase 1 Prototype")
    print("=" * 60)
    print(f"Protected path : {protected_path}")
    print(f"Watching mode  : {'Single file' if is_file else 'Folder (recursive)'}")
    print("Monitoring started. Press Ctrl+C to stop.\n")

    try:
        while True:
            time.sleep(1)
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
