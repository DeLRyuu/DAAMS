"""
monitor.py

Watchdog-based filesystem monitor for the DAAMS Protected Asset Registry.

Detects: Create, Modify, Rename, Delete (native Watchdog events) plus
Copy and Move (INFERRED -- see event_aggregator.py). Open is NOT detected:
Windows does not report file reads through Watchdog. Never watches an
entire drive.

The registry is routed through non-overlapping recursive directory watches.
Each watch has one handler, a routing table, and a shared file index.

Phase 4.1: every REGISTRY_RELOAD_INTERVAL_SECONDS the registry is re-read
and watches are reconciled, so protect / reclassify / unprotect take effect
without restarting.

Phase 5: each handler carries its asset's classification (updated in place
on live reclassification) and passes it to activity_logger.record_activity().

Phase 6: folder-root events are recorded, missing registry entries generate
an activity and alert, and periodic disk scans reconcile missed Watchdog events.

Engine testing update (this version):
- Raw Watchdog events no longer go straight to the logger. They are fed to
  an EventAggregator (event_aggregator.py) which merges the several raw
  events Windows produces for ONE user action into ONE record, and infers
  Copy and Move from file hashes. This fixes: copies logged as Modify,
  cut-and-paste logged as Delete + Create, extra Modify events after
  Create/Rename, and every copy tripping the burst-risk factor.
- Events are routed to the most-specific protected asset, so nested
  protections do not create duplicate activities.

Known limits (also in README): records are emitted ~1 s after the action;
copying a protected file outside the active watch roots, and Open/read
access, are invisible to Watchdog.
"""

import logging
import os
import sys
import threading
import time
from typing import Optional

_ENGINE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ENGINE_DIR not in sys.path:
    sys.path.insert(0, _ENGINE_DIR)

from watchdog.events import FileSystemEventHandler
from watchdog.observers import Observer

from config.settings import (
    CLASSIFICATION_WEIGHTS,
    COALESCE_MAX_WAIT_SECONDS,
    COALESCE_QUIET_SECONDS,
    DISK_SCAN_INTERVAL_SECONDS,
    ENGINE_HEARTBEAT_INTERVAL_SECONDS,
    FILE_WATCH_RECURSIVE,
    HASH_MAX_BYTES,
    MAX_INDEXED_FILES_PER_ASSET,
    MOVE_PAIRING_WINDOW_SECONDS,
    REGISTRY_RELOAD_INTERVAL_SECONDS,
    is_disallowed_root,
)
from core import database
from core.asset_registry import get_all_assets, relocate_asset
from core.clipboard_watcher import ClipboardWatcher
from core.event_aggregator import EventAggregator, FileIndex
from services import sync_service
from services.activity_logger import record_activity

FLUSH_INTERVAL_SECONDS = 0.25
logger = logging.getLogger(__name__)


class ProtectedAssetEventHandler(FileSystemEventHandler):
    """Route one recursive watch's relevant events into a shared aggregator."""

    def __init__(self, assets: list, index: FileIndex,
                 aggregator: EventAggregator, router,
                 folder_event_paths: Optional[list] = None,
                 on_asset_removed=None):
        super().__init__()
        self.index = index
        self.aggregator = aggregator
        self.router = router
        self.on_asset_removed = on_asset_removed
        self.update_assets(assets, folder_event_paths)

    def update_assets(self, assets: list,
                      folder_event_paths: Optional[list] = None) -> None:
        self.assets = list(assets)
        self.folder_event_paths = {
            _normalized_path(path) for path in (
                folder_event_paths
                if folder_event_paths is not None
                else [
                    asset["path"] for asset in assets
                    if asset.get("asset_type") == "folder"
                ]
            )
        }

    def on_created(self, event):
        if event.is_directory:
            return
        if not self.assets:
            return
        path = event.src_path
        if self.router.match(path, self.assets):
            self.aggregator.on_created(path)
        elif self.index.has_size_candidate(path):
            self.aggregator.on_created(path, neighbor=True)
        else:
            logger.debug("Dropped neighbour outside copy candidates: %s", path)

    def on_modified(self, event):
        if event.is_directory:
            return
        if not self.assets:
            return
        path = event.src_path
        if (
            self.router.match(path, self.assets)
            or self.aggregator.has_pending_neighbor(path)
        ):
            self.aggregator.on_modified(path)

    def on_deleted(self, event):
        if event.is_directory:
            path = event.src_path
            if _normalized_path(path) in self.folder_event_paths:
                asset = self.router.match_exact_folder(path)
                if asset:
                    self.router.emit(
                        path, "Delete",
                        "Protected folder was deleted.",
                        path, None,
                    )
                    if self.on_asset_removed:
                        self.on_asset_removed(path)
            return
        if not self.assets:
            return
        path = event.src_path
        matched = self.router.match(path, self.assets)
        if matched or self.index.contains(path):
            self.aggregator.on_deleted(path)
            if (
                matched
                and matched.get("asset_type") == "file"
                and _normalized_path(path) == _normalized_path(matched["path"])
                and self.on_asset_removed
            ):
                self.on_asset_removed(path)

    def on_moved(self, event):
        if event.is_directory:
            src, dest = event.src_path, event.dest_path
            if _normalized_path(src) in self.folder_event_paths:
                asset = self.router.match_exact_folder(src)
                if asset:
                    self.router.emit(
                        f"{src} -> {dest}", "Move",
                        "Protected folder was renamed or moved.",
                        src, dest,
                    )
            return
        if not self.assets:
            return
        src, dest = event.src_path, event.dest_path
        matched_source = self.router.match(src, self.assets)
        relevant = (
            matched_source
            or self.router.match(dest, self.assets)
            or self.index.contains(src)
        )
        if relevant:
            self.aggregator.on_moved(src, dest)
            if (
                matched_source
                and matched_source.get("asset_type") == "file"
                and _normalized_path(src) == _normalized_path(matched_source["path"])
                and self.on_asset_removed
            ):
                self.on_asset_removed(src)


class _Ticker(threading.Thread):
    """Calls flush() on every registered aggregator a few times a second."""

    def __init__(self, interval: float = FLUSH_INTERVAL_SECONDS):
        super().__init__(name="DAAMS-Aggregator", daemon=True)
        self._interval = interval
        self._aggregators = set()
        self._lock = threading.Lock()
        self._halt = threading.Event()

    def register(self, aggregator) -> None:
        with self._lock:
            self._aggregators.add(aggregator)

    def unregister(self, aggregator) -> None:
        with self._lock:
            self._aggregators.discard(aggregator)

    def run(self):
        while not self._halt.wait(self._interval):
            with self._lock:
                current = list(self._aggregators)
            for aggregator in current:
                try:
                    aggregator.flush()
                except Exception as e:
                    print(f"[AGGREGATOR WARNING] Unexpected error while flushing events: {e}")

    def stop(self) -> None:
        self._halt.set()


def _record_missing_asset(asset: dict) -> None:
    path = asset.get("path", "")
    logger.warning("Protected asset is missing from disk: %s", path)
    record_activity(
        asset_path=path,
        action="Delete",
        classification=asset.get("classification"),
        detection_note="Protected asset was missing when the monitoring engine checked disk.",
        protected_root=path,
        force_alert=True,
    )


def _validate_asset_for_watching(asset: dict) -> bool:
    """
    Re-validate a registry entry right before watching it. A protected
    file/folder may have been deleted or moved since it was registered;
    skip it with a warning instead of crashing the whole engine.
    """
    path = asset.get("path", "")

    if is_disallowed_root(path):
        logger.warning("Protected asset '%s' looks like a drive root; skipping for safety.", path)
        return False

    if not os.path.exists(path):
        logger.warning("Protected asset no longer exists on disk, skipping: %s", path)
        return False

    return True


def _normalized_path(path: str) -> str:
    return os.path.normcase(os.path.abspath(os.path.normpath(path)))


def _path_is_within(path: str, parent: str) -> bool:
    child_path = _normalized_path(path)
    parent_path = _normalized_path(parent).rstrip("\\/")
    return child_path == parent_path or child_path.startswith(parent_path + os.sep)


class _AssetRouter:
    def __init__(self, assets: list, on_asset_moved=None):
        self.on_asset_moved = on_asset_moved
        self.update(assets)

    def update(self, assets: list) -> None:
        self.assets = list(assets)

    def match_exact_folder(self, path: Optional[str]) -> Optional[dict]:
        if not path:
            return None
        normalized = _normalized_path(path)
        return next(
            (
                asset for asset in self.assets
                if asset.get("asset_type") == "folder"
                and _normalized_path(asset.get("path", "")) == normalized
            ),
            None,
        )

    def match(self, path: Optional[str], assets: Optional[list] = None) -> Optional[dict]:
        if not path:
            return None
        matching = []
        for asset in assets if assets is not None else self.assets:
            asset_path = asset.get("path", "")
            if not asset_path:
                continue
            if asset.get("asset_type") == "folder":
                if _path_is_within(path, asset_path):
                    matching.append(asset)
            elif _normalized_path(path) == _normalized_path(asset_path):
                matching.append(asset)
        if not matching:
            return None
        return max(
            matching,
            key=lambda asset: (
                asset.get("asset_type") == "file",
                len(_normalized_path(asset["path"])),
            ),
        )

    def emit(self, asset_path: str, action: str, note: Optional[str],
             src: Optional[str], dest: Optional[str]) -> None:
        assets = self.assets
        source_asset = self.match(src, assets)
        destination_asset = self.match(dest, assets)
        participants = {
            _normalized_path(item["path"]): item
            for item in (source_asset, destination_asset) if item
        }
        if not participants:
            return

        chosen = max(
            participants.values(),
            key=lambda item: CLASSIFICATION_WEIGHTS.get(item.get("classification"), 0),
        )
        other_assets = [
            item for key, item in participants.items()
            if key != _normalized_path(chosen["path"])
        ]
        if other_assets:
            other = other_assets[0]
            other_name = other.get("asset_name") or other["path"]
            detail = f"Other protected asset involved: {other_name} ({other['path']})."
            note = f"{note} {detail}" if note else detail

        record_activity(
            asset_path=asset_path,
            action=action,
            classification=chosen.get("classification"),
            detection_note=note,
            protected_root=chosen.get("path"),
        )
        if (
            action in {"Rename", "Move"}
            and source_asset
            and src
            and dest
            and self.on_asset_moved
            and _normalized_path(src) == _normalized_path(source_asset["path"])
        ):
            if not relocate_asset(source_asset["path"], dest):
                logger.warning(
                    "Could not move protected registry entry from %s to %s",
                    src, dest,
                )
            self.on_asset_moved(src)


def _watch_roots(assets: list) -> dict:
    """Build the smallest watch roots without making non-recursive roots hide descendants."""
    roots = {}
    folder_assets = [
        asset for asset in assets if asset.get("asset_type") == "folder"
    ]

    def add_root(path: str, recursive: bool) -> None:
        root = os.path.abspath(path)
        root_key = _normalized_path(root)
        if root_key not in roots:
            roots[root_key] = {"path": root, "recursive": recursive}
        else:
            roots[root_key]["recursive"] |= recursive

    for asset in assets:
        path = asset["path"]
        if asset.get("asset_type") == "folder":
            add_root(path, True)
            parent = os.path.dirname(os.path.abspath(path))
            has_protected_ancestor = any(
                _path_is_within(parent, folder["path"])
                for folder in folder_assets
                if _normalized_path(folder["path"]) != _normalized_path(path)
            )
            if not has_protected_ancestor and not is_disallowed_root(parent):
                add_root(parent, False)
        else:
            root = os.path.dirname(os.path.abspath(path))
            add_root(root, FILE_WATCH_RECURSIVE and not is_disallowed_root(root))

    ordered = sorted(roots, key=len)
    selected = []
    for candidate in ordered:
        if not any(
            roots[parent]["recursive"]
            and _path_is_within(candidate, roots[parent]["path"])
            for parent in selected
        ):
            selected.append(candidate)

    plan = {}
    for root_key in selected:
        root_path = roots[root_key]["path"]
        recursive = roots[root_key]["recursive"]
        root_assets = []
        for asset in assets:
            asset_scope = (
                asset["path"] if asset.get("asset_type") == "folder"
                else os.path.dirname(os.path.abspath(asset["path"]))
            )
            in_scope = (
                _path_is_within(asset_scope, root_path)
                if recursive
                else _normalized_path(asset_scope) == root_key
            )
            if in_scope:
                root_assets.append(asset)

        folder_event_paths = [
            asset["path"] for asset in folder_assets
            if (
                _path_is_within(asset["path"], root_path)
                if recursive
                else _normalized_path(os.path.dirname(os.path.abspath(asset["path"])))
                == root_key
            )
        ]
        plan[root_key] = {
            "assets": root_assets,
            "recursive": recursive,
            "folder_event_paths": folder_event_paths,
        }
    return plan


def _warn_for_large_file_parents(assets: list, warned: set) -> None:
    if not FILE_WATCH_RECURSIVE:
        return
    parents = {
        os.path.dirname(os.path.abspath(asset["path"]))
        for asset in assets if asset.get("asset_type") == "file"
    }
    for parent in parents:
        parent_key = _normalized_path(parent)
        if parent_key in warned:
            continue
        if is_disallowed_root(parent):
            print(
                f"[WARNING] Protected file parent '{parent}' is a drive root; "
                "recursive monitoring is disabled for safety."
            )
            warned.add(parent_key)
            continue
        count = 0
        for _dirpath, dirs, files in os.walk(parent):
            count += len(dirs) + len(files)
            if count > MAX_INDEXED_FILES_PER_ASSET:
                print(
                    f"[WARNING] Protected file parent '{parent}' contains more than "
                    f"{MAX_INDEXED_FILES_PER_ASSET} entries; recursive monitoring may be expensive."
                )
                warned.add(parent_key)
                break


def _schedule_watch(observer: Observer, root: str, assets: list,
                    index: FileIndex, aggregator: EventAggregator,
                    router: _AssetRouter, recursive: bool,
                    folder_event_paths: Optional[list] = None,
                    on_asset_removed=None) -> dict:
    handler = ProtectedAssetEventHandler(
        assets, index, aggregator, router, folder_event_paths,
        on_asset_removed,
    )
    watch = observer.schedule(handler, root, recursive=recursive)
    return {"handler": handler, "watch": watch}


def _scan_assets_for_changes(assets: list, index: FileIndex,
                             aggregator: EventAggregator) -> None:
    """Reconcile the indexed file snapshot with disk without blocking event callbacks."""
    was_truncated = index.truncated
    disk_entries = index.scan_assets(assets)
    if index.truncated and not was_truncated:
        logger.warning(
            "Periodic disk scan reached the protected-file index limit; "
            "some missed events may not be detected."
        )
    indexed = {_normalized_path(entry.path): entry for entry in index.entries()}
    current = {_normalized_path(entry.path): entry for entry in disk_entries}

    for key, entry in indexed.items():
        if key not in current:
            aggregator.on_deleted(entry.path)

    for key, entry in current.items():
        previous = indexed.get(key)
        if previous is None:
            aggregator.on_created(entry.path)
        elif (
            previous.size,
            previous.mtime_ns,
            previous.file_id,
        ) != (entry.size, entry.mtime_ns, entry.file_id):
            aggregator.on_modified(entry.path)


def _save_heartbeat(status: str) -> None:
    try:
        database.save_engine_heartbeat(status)
    except Exception:
        logger.exception("Could not save Monitoring Engine %s heartbeat", status)


def start_monitoring():
    """
    Read the Protected Asset Registry and begin monitoring every
    currently-protected file and folder. Blocks until Ctrl+C.
    """
    print("=" * 60)
    print("DAAMS Python Monitoring Engine - Offline-First")
    print("=" * 60)

    database.initialize_database()
    observer = Observer()
    ticker = _Ticker()

    watched = {}
    invalid_warned = set()
    observed_removals = set()
    large_parents_warned = set()
    router = _AssetRouter(
        [],
        on_asset_moved=lambda path: observed_removals.add(_normalized_path(path)),
    )
    index = FileIndex(
        hash_max_bytes=HASH_MAX_BYTES,
        max_files=MAX_INDEXED_FILES_PER_ASSET,
    )
    aggregator = EventAggregator(
        emit=lambda _asset, _action, _note: None,
        emit_with_paths=router.emit,
        index=index,
        quiet=COALESCE_QUIET_SECONDS,
        max_wait=COALESCE_MAX_WAIT_SECONDS,
        move_window=MOVE_PAIRING_WINDOW_SECONDS,
        hash_max_bytes=HASH_MAX_BYTES,
    )
    ticker.register(aggregator)
    previous_signature = None
    previous_assets = {}

    def reconcile(initial: bool = False):
        nonlocal previous_signature, previous_assets
        registry_records = get_all_assets(only_protected=True)
        registered_assets = {
            _normalized_path(asset["path"]): asset
            for asset in registry_records if asset.get("path")
        }
        valid_assets = []
        for path, asset in registered_assets.items():
            if path in invalid_warned:
                if is_disallowed_root(asset["path"]) or not os.path.exists(asset["path"]):
                    continue
                invalid_warned.discard(path)
            elif not _validate_asset_for_watching(asset):
                invalid_warned.add(path)
                if (
                    not is_disallowed_root(asset["path"])
                    and path not in observed_removals
                ):
                    _record_missing_asset(asset)
                continue
            valid_assets.append(asset)
            invalid_warned.discard(path)
            observed_removals.discard(path)
        current_assets = {
            _normalized_path(asset["path"]): asset for asset in valid_assets
        }

        signature = tuple(sorted(
            (
                _normalized_path(asset["path"]),
                asset.get("asset_type"),
                asset.get("classification"),
            )
            for asset in valid_assets
        ))
        if signature != previous_signature:
            if previous_signature is not None:
                aggregator.flush(force=True)
            router.update(valid_assets)
            index.rebuild_assets(
                valid_assets,
                max_files=MAX_INDEXED_FILES_PER_ASSET * max(1, len(valid_assets)),
            )
            if index.truncated:
                print(
                    f"[WARNING] The protected-file index exceeded "
                    f"{index.max_files} files; Copy/Move inference may be incomplete."
                )
            _warn_for_large_file_parents(valid_assets, large_parents_warned)
            previous_signature = signature

        plan = _watch_roots(valid_assets)
        for root_key in list(watched):
            if root_key not in plan:
                info = watched.pop(root_key)
                observer.remove_handler_for_watch(info["handler"], info["watch"])

        for root, watch_plan in plan.items():
            root_assets = watch_plan["assets"]
            if root in watched:
                watched[root]["handler"].update_assets(
                    root_assets, watch_plan["folder_event_paths"]
                )
            else:
                try:
                    watched[root] = _schedule_watch(
                        observer, root, root_assets, index, aggregator, router,
                        recursive=watch_plan["recursive"],
                        folder_event_paths=watch_plan["folder_event_paths"],
                        on_asset_removed=lambda path: observed_removals.add(
                            _normalized_path(path)
                        ),
                    )
                except OSError:
                    logger.exception("Could not watch protected directory: %s", root)

        if not initial:
            for path in previous_assets.keys() - current_assets.keys():
                removed = previous_assets[path]
                print(
                    f"\n[REGISTRY] Protection removed for '{removed['path']}' "
                    f"-- DAAMS stopped monitoring it."
                )
            for path in current_assets.keys() - previous_assets.keys():
                added = current_assets[path]
                print(
                    f"\n[REGISTRY] Now watching newly protected "
                    f"[{added.get('classification', '?')}] "
                    f"{added.get('asset_type', 'asset')}: {added['path']}"
                )
            for path in current_assets.keys() & previous_assets.keys():
                before = previous_assets[path]
                after = current_assets[path]
                if before.get("classification") != after.get("classification"):
                    print(
                        f"\n[REGISTRY] Classification changed for '{after['path']}': "
                        f"{before.get('classification')} -> {after.get('classification')}"
                    )

        previous_assets = current_assets

    observer_started = False
    ticker_started = False
    clipboard_watcher_started = False
    sync_started = False
    shutdown_status = "stopped"
    _save_heartbeat("started")
    try:
        sync_service.start_sync_service()
        sync_started = True
        observer.start()
        observer_started = True
        ticker.start()
        ticker_started = True
        clipboard_watcher = ClipboardWatcher(get_assets=lambda: get_all_assets(only_protected=True))
        clipboard_watcher.start()
        clipboard_watcher_started = True
        reconcile(initial=True)

        if not previous_assets:
            print("No protected assets found in the registry.")
            print("Right-click a file or folder in Windows Explorer and choose")
            print("DAAMS -> Protect This File / Protect This Folder.")
            print("DAAMS will start watching it automatically within a few "
                  "seconds -- no restart needed.\n")
        else:
            print(f"\n{len(previous_assets)} protected asset(s) being monitored.")

        print("Press Ctrl+C to stop.\n")
        last_scan = time.monotonic()
        last_heartbeat = last_scan
        while True:
            time.sleep(min(REGISTRY_RELOAD_INTERVAL_SECONDS, 1))
            reconcile()
            now = time.monotonic()
            if now - last_scan >= DISK_SCAN_INTERVAL_SECONDS:
                try:
                    _scan_assets_for_changes(
                        list(previous_assets.values()), index, aggregator
                    )
                except Exception:
                    logger.exception("Periodic protected-file scan failed")
                last_scan = now
            if now - last_heartbeat >= ENGINE_HEARTBEAT_INTERVAL_SECONDS:
                _save_heartbeat("running")
                last_heartbeat = now
    except KeyboardInterrupt:
        print("\nStopping DAAMS monitoring engine...")
    except Exception:
        shutdown_status = "failed"
        raise
    finally:
        if observer_started:
            try:
                observer.stop()
                observer.join()
            except Exception:
                logger.exception("Could not stop the filesystem observer cleanly")
        if ticker_started:
            try:
                ticker.stop()
                ticker.join()
            except Exception:
                logger.exception("Could not stop the event ticker cleanly")
        if clipboard_watcher_started:
            try:
                clipboard_watcher.stop()
                clipboard_watcher.join()
            except Exception:
                logger.exception("Could not stop the clipboard watcher cleanly")
        try:
            aggregator.flush(force=True)
        except Exception:
            logger.exception("Could not flush pending filesystem events at shutdown")
        if sync_started:
            try:
                sync_service.stop_sync_service()
            except Exception:
                logger.exception("Could not stop the sync service cleanly")
        _save_heartbeat(shutdown_status)
        print("Monitoring stopped cleanly.")
