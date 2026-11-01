"""
event_aggregator.py

Turns the RAW Watchdog event stream into the activities an administrator
actually cares about.

Why this module exists (see README "Detection limits"):
Windows does not report "copy" or "move between folders" as events. What
Watchdog really delivers (confirmed with tools/raw_event_probe.py) is:

    Copy         ->  created, modified, modified          (new file only;
                                                           NOTHING on the source)
    Move to      ->  deleted (old path) + created + modified
    another dir      (NOT a "moved" event)
    Rename       ->  moved, then an extra modified
    Save         ->  modified (1+ times)

Logging those raw events one by one produced false "Modify" records,
turned a harmless cut-and-paste into a weighted "Delete", and made every
single copy trip the burst-risk factor (3 events within a second).

This module sits between the Watchdog handlers (core/monitor.py) and
activity_logger.record_activity():

  1. COALESCING  - events on the same path are merged until the path has
                   been quiet for `quiet` seconds, so one user action
                   becomes one record.
  2. MOVE        - a Delete and a Create that look like the same file
                   (identical content hash, or same name + size when no
                   hash is available) within `move_window` seconds is a Move.
  3. COPY        - a Create whose SHA-256 matches a file that still exists
                   in the protected scope is a Copy. Empty files use
                   Windows' generated copy-name suffixes instead.
  4. REPLACE     - a path that vanishes (deleted or renamed away) and
                   reappears within `move_window` seconds is how many
                   editors save a file; it is reported as ONE Modify.

HONESTY NOTES
- Copy and Move-by-delete+create are INFERENCES, not native Windows
  events. They are labelled as such through the `note` passed to emit().
- Only protected files are indexed. New files in a recursive file-watch
  root are size-checked; they are hashed ONLY when their size matches a
  protected file, and logged only if the content matches. Empty-file copy
  detection compares names and does not hash file contents.
- File CONTENTS are never stored or logged -- only path, size, and a
  SHA-256 digest held in memory.
- Records are emitted about `quiet` seconds after the last raw event, so
  timestamps are slightly later than the real action.
- Copying a protected file OUT of the protected scope, and Open/read
  access, are not visible here at all.
"""

import hashlib
import logging
import os
import re
import threading
import time
from dataclasses import dataclass, replace
from typing import Callable, Dict, List, Optional

DEFAULT_QUIET_SECONDS = 1.0
DEFAULT_MAX_WAIT_SECONDS = 10.0
DEFAULT_MOVE_WINDOW_SECONDS = 2.0
DEFAULT_HASH_MAX_BYTES = 50 * 1024 * 1024
DEFAULT_MAX_INDEXED_FILES = 2000
logger = logging.getLogger(__name__)


def _key(path: str) -> str:
    return os.path.normcase(os.path.abspath(path))


def hash_file(path: str, max_bytes: int = DEFAULT_HASH_MAX_BYTES) -> Optional[str]:
    """SHA-256 of a file, or None if it is too large, locked, or missing."""
    try:
        if os.path.getsize(path) > max_bytes:
            return None
        digest = hashlib.sha256()
        with open(path, "rb") as f:
            for chunk in iter(lambda: f.read(1024 * 1024), b""):
                digest.update(chunk)
        return digest.hexdigest()
    except OSError:
        return None


@dataclass
class IndexEntry:
    path: str
    size: int
    mtime_ns: int
    file_id: Optional[tuple[int, int]]
    hash: Optional[str] = None


def _file_metadata(path: str) -> Optional[tuple[int, int, Optional[tuple[int, int]]]]:
    try:
        stat = os.stat(path)
    except OSError:
        return None
    file_id = (stat.st_dev, stat.st_ino) if stat.st_ino else None
    return stat.st_size, stat.st_mtime_ns, file_id


def _looks_like_copy_name(source_path: str, destination_path: str) -> bool:
    source_name = os.path.basename(source_path)
    destination_name = os.path.basename(destination_path)
    source_stem, source_ext = os.path.splitext(source_name)
    destination_stem, destination_ext = os.path.splitext(destination_name)
    if source_ext.casefold() != destination_ext.casefold():
        return False
    suffix = re.compile(
        rf"^{re.escape(source_stem)}(?: - Copy(?: \(\d+\))?| \(\d+\))$",
        re.IGNORECASE,
    )
    return suffix.fullmatch(destination_stem) is not None


class FileIndex:
    """
    In-memory (path, size, hash) index of protected files in the watched
    scopes. Never persisted, never contains file contents.
    """

    def __init__(self, hash_max_bytes: int = DEFAULT_HASH_MAX_BYTES,
                 max_files: int = DEFAULT_MAX_INDEXED_FILES):
        self._entries: Dict[str, IndexEntry] = {}
        self._lock = threading.RLock()
        self._hash_max_bytes = hash_max_bytes
        self._max_files = max_files
        self.truncated = False

    def build(self, asset_path: str) -> None:
        asset_type = "folder" if os.path.isdir(asset_path) else "file"
        self.build_assets([{"path": asset_path, "asset_type": asset_type}])

    def build_assets(self, assets: List[dict]) -> None:
        """Index files protected directly or through a protected folder."""
        staged = FileIndex(self._hash_max_bytes, self._max_files)
        staged._build_assets(assets)
        with self._lock:
            for key, entry in staged._entries.items():
                if key not in self._entries and len(self._entries) >= self._max_files:
                    self.truncated = True
                    break
                self._entries[key] = entry
            self.truncated = self.truncated or staged.truncated

    def rebuild_assets(self, assets: List[dict], max_files: int) -> None:
        """Atomically replace indexed scopes after registry changes."""
        staged = FileIndex(self._hash_max_bytes, max_files)
        staged._build_assets(assets)
        with self._lock:
            self._entries = staged._entries
            self._max_files = max_files
            self.truncated = staged.truncated

    def _build_assets(self, assets: List[dict]) -> None:
        for asset in sorted(assets, key=lambda item: item.get("asset_type") != "file"):
            path = asset.get("path", "")
            if not path:
                continue
            if asset.get("asset_type") != "folder":
                if len(self._entries) >= self._max_files:
                    self.truncated = True
                    return
                self.add_or_update(path)
                continue

            for dirpath, _dirs, files in os.walk(path):
                for name in files:
                    file_path = os.path.join(dirpath, name)
                    if _key(file_path) in self._entries:
                        continue
                    if len(self._entries) >= self._max_files:
                        self.truncated = True
                        return
                    self.add_or_update(file_path)

    def scan_assets(self, assets: List[dict]) -> List[IndexEntry]:
        """Snapshot files currently on disk in the protected scopes without mutating the index."""
        discovered: Dict[str, IndexEntry] = {}

        def add_path(path: str) -> None:
            key = _key(path)
            if key not in discovered and len(discovered) >= self._max_files:
                with self._lock:
                    self.truncated = True
                return
            metadata = _file_metadata(path)
            if metadata is None:
                return
            size, mtime_ns, file_id = metadata
            discovered[key] = IndexEntry(path, size, mtime_ns, file_id)

        for asset in assets:
            path = asset.get("path", "")
            if not path:
                continue
            if asset.get("asset_type") == "folder":
                for dirpath, _dirs, files in os.walk(path):
                    for name in files:
                        add_path(os.path.join(dirpath, name))
                        if len(discovered) >= self._max_files:
                            return list(discovered.values())
            else:
                add_path(path)
        return list(discovered.values())

    def add_or_update(self, path: str) -> None:
        with self._lock:
            metadata = _file_metadata(path)
            if metadata is None:
                return
            size, mtime_ns, file_id = metadata
            key = _key(path)
            if key not in self._entries and len(self._entries) >= self._max_files:
                self.truncated = True
                return
            previous = self._entries.get(key)
            cached_hash = None
            if previous and (
                previous.size, previous.mtime_ns, previous.file_id
            ) == (size, mtime_ns, file_id):
                cached_hash = previous.hash
            self._entries[key] = IndexEntry(
                path, size, mtime_ns, file_id, cached_hash
            )

    def remove(self, path: str) -> None:
        with self._lock:
            self._entries.pop(_key(path), None)

    def clear(self) -> None:
        with self._lock:
            self._entries.clear()
            self.truncated = False

    def contains(self, path: str) -> bool:
        with self._lock:
            return _key(path) in self._entries

    def has_size_candidate(self, path: str) -> bool:
        metadata = _file_metadata(path)
        if metadata is None:
            return False
        size = metadata[0]
        with self._lock:
            return any(
                entry.size == size for entry in self._entries.values()
            )

    def rename(self, src: str, dest: str) -> None:
        with self._lock:
            if self._entries.pop(_key(src), None) is not None:
                self.add_or_update(dest)
            elif os.path.isfile(dest):
                self.add_or_update(dest)

    def get(self, path: str) -> Optional[IndexEntry]:
        with self._lock:
            entry = self._entries.get(_key(path))
            return replace(entry) if entry else None

    def hash_for(self, path: str) -> Optional[str]:
        """Lazily hash a file without holding the index lock during I/O."""
        key = _key(path)
        with self._lock:
            entry = self._entries.get(key)
            if entry is None:
                return None
            snapshot = replace(entry)

        before = _file_metadata(snapshot.path)
        if before is None:
            return None
        if before != (snapshot.size, snapshot.mtime_ns, snapshot.file_id):
            self.add_or_update(snapshot.path)
            with self._lock:
                current = self._entries.get(key)
                if current is None:
                    return None
                snapshot = replace(current)
            before = _file_metadata(snapshot.path)
            if before is None or before != (
                snapshot.size, snapshot.mtime_ns, snapshot.file_id
            ):
                return None
        if snapshot.hash is not None:
            return snapshot.hash

        digest = hash_file(snapshot.path, self._hash_max_bytes)
        after = _file_metadata(snapshot.path)
        if digest is None or after != before:
            return None

        with self._lock:
            current = self._entries.get(key)
            if current is None or (
                current.size, current.mtime_ns, current.file_id
            ) != before:
                return None
            current.hash = digest
        return digest

    def entries(self) -> List[IndexEntry]:
        with self._lock:
            return [replace(entry) for entry in self._entries.values()]

    def __len__(self):
        with self._lock:
            return len(self._entries)


@dataclass
class _Pending:
    kind: str                 # "create" | "modify" | "delete" | "rename"
    path: str                 # current path (destination for "rename")
    first: float
    last: float
    src: Optional[str] = None  # only for "rename"
    neighbor: bool = False     # unprotected create eligible only for copy detection
    ready: bool = False        # flush at the next tick regardless of quiet time


class EventAggregator:
    """
    Feed it raw events with on_created/on_modified/on_deleted/on_moved and
    call flush() periodically (core/monitor.py does this every ~0.25 s).
    Finished activities are delivered through emit(asset, action, note):
        asset  - a path, or "source -> destination" for Rename/Move/Copy
        action - Create | Modify | Delete | Rename | Move | Copy
        note   - None, or a short "Detection: ..." string for inferred actions
    """

    def __init__(self, emit: Callable[[str, str, Optional[str]], None], index: FileIndex,
                 quiet: float = DEFAULT_QUIET_SECONDS,
                 max_wait: float = DEFAULT_MAX_WAIT_SECONDS,
                 move_window: float = DEFAULT_MOVE_WINDOW_SECONDS,
                 hash_max_bytes: int = DEFAULT_HASH_MAX_BYTES,
                 clock: Callable[[], float] = time.time,
                 emit_with_paths: Optional[
                     Callable[[str, str, Optional[str], Optional[str], Optional[str]], None]
                 ] = None):
        self._emit = emit
        self._emit_with_paths = emit_with_paths
        self._index = index
        self._quiet = quiet
        self._max_wait = max_wait
        self._move_window = move_window
        self._hash_max_bytes = hash_max_bytes
        self._clock = clock
        self._pending: List[_Pending] = []
        self._lock = threading.Lock()

    # ------------------------------------------------------------------ intake
    def on_created(self, path: str, neighbor: bool = False) -> None:
        now = self._clock()
        with self._lock:
            # Vanished and reappeared => atomic-replace save, not a new file.
            gone = self._find(path, {"delete"}) or self._find_rename_from(path)
            if gone is not None:
                self._pending.remove(gone)
                self._add("modify", path, now)
                return
            current = self._find(path, {"create", "modify", "rename"})
            if current is not None:
                current.last = now
                return
            self._add("create", path, now, neighbor=neighbor)

    def on_modified(self, path: str) -> None:
        now = self._clock()
        with self._lock:
            current = self._find(path, {"create", "modify", "rename"})
            if current is not None:
                current.last = now  # absorbed: Windows' write side effect
            else:
                self._add("modify", path, now)

    def on_deleted(self, path: str) -> None:
        now = self._clock()
        with self._lock:
            current = self._find(path, {"create", "modify", "rename"})
            if current is not None:
                current.ready = True  # report what happened before the delete
            self._add("delete", path, now)

    def on_moved(self, src: str, dest: str) -> None:
        now = self._clock()
        with self._lock:
            current = self._find(src, {"create", "modify", "rename"})
            if current is not None:
                current.ready = True
            gone = self._find(dest, {"delete"}) or self._find_rename_from(dest)
            if gone is not None:
                self._pending.remove(gone)
                self._add("modify", dest, now)
                return
            self._add("rename", dest, now, src=src)

    # ------------------------------------------------------------------- flush
    def flush(self, force: bool = False) -> int:
        """Emit every finished activity. Returns how many were emitted."""
        now = self._clock()
        outputs = []
        with self._lock:
            due = [p for p in self._pending if self._is_due(p, now, force)]
            due.sort(key=lambda p: (p.kind == "delete", p.first))  # deletes last, so creates can pair with them
            due_non_deletes = [p for p in due if p.kind != "delete"]
            for pending in due_non_deletes:
                self._pending.remove(pending)

        for pending in due_non_deletes:
            result = self._resolve(pending)
            if result is not None:
                outputs.append(result)

        with self._lock:
            due_deletes = [
                pending for pending in self._pending
                if pending.kind == "delete" and self._is_due(pending, now, force)
            ]
            for pending in due_deletes:
                self._pending.remove(pending)
        for pending in due_deletes:
            result = self._resolve(pending)
            if result is not None:
                outputs.append(result)

        for asset, action, note, src, dest in outputs:  # emit may be slow (DB, alerts, email)
            try:
                if self._emit_with_paths:
                    self._emit_with_paths(asset, action, note, src, dest)
                else:
                    self._emit(asset, action, note)
            except Exception as e:  # never let one bad record stop the rest
                print(f"[AGGREGATOR WARNING] Could not record {action} for {asset}: {e}")
        return len(outputs)

    def pending_count(self) -> int:
        with self._lock:
            return len(self._pending)

    def has_pending_neighbor(self, path: str) -> bool:
        key = _key(path)
        with self._lock:
            return any(
                item.neighbor and item.kind == "create" and _key(item.path) == key
                for item in self._pending
            )

    # ---------------------------------------------------------------- internals
    def _add(self, kind, path, now, src=None, neighbor=False):
        self._pending.append(_Pending(kind, path, now, now, src=src, neighbor=neighbor))

    def _find(self, path, kinds):
        k = _key(path)
        for p in self._pending:
            if p.kind in kinds and _key(p.path) == k:
                return p
        return None

    def _find_rename_from(self, src):
        k = _key(src)
        for p in self._pending:
            if p.kind == "rename" and _key(p.src) == k:
                return p
        return None

    def _is_due(self, p, now, force):
        if force or p.ready:
            return True
        if p.kind == "delete":
            return now - p.first >= self._move_window
        return (now - p.last >= self._quiet) or (now - p.first >= self._max_wait)

    def _resolve(self, p):
        if p.kind == "modify":
            self._index.add_or_update(p.path)
            return (p.path, "Modify", None, None, p.path)

        if p.kind == "rename":
            same_dir = _key(os.path.dirname(p.src)) == _key(os.path.dirname(p.path))
            self._index.rename(p.src, p.path)
            return (f"{p.src} -> {p.path}", "Rename" if same_dir else "Move",
                    None, p.src, p.path)

        if p.kind == "delete":
            self._index.remove(p.path)
            return (p.path, "Delete", None, p.path, None)

        return self._resolve_create(p)

    def _resolve_create(self, p):
        path = p.path
        metadata = _file_metadata(path)
        if metadata is None:
            is_file = False
            size = 0
            file_id = None
        else:
            size, _mtime_ns, file_id = metadata
            is_file = os.path.isfile(path)
        if not is_file:  # vanished again before we could look at it
            if p.neighbor:
                logger.debug("Dropped neighbour event for missing file: %s", path)
                return None
            return (path, "Create", None, None, path)

        with self._lock:
            pending_deletes = [
                pending for pending in self._pending
                if pending.kind == "delete"
            ]
        if file_id is not None:
            for pending_delete in pending_deletes:
                old = self._index.get(pending_delete.path)
                if old is None or old.file_id != file_id:
                    continue
                with self._lock:
                    if pending_delete not in self._pending:
                        continue
                    self._pending.remove(pending_delete)
                self._index.rename(pending_delete.path, path)
                return (
                    f"{pending_delete.path} -> {path}",
                    "Move",
                    f"Detection: Move inferred (file removed and re-created within "
                    f"{self._move_window:g}s, same NTFS file ID)",
                    pending_delete.path,
                    path,
                )

        if size == 0:
            for entry in self._index.entries():
                if (
                    entry.size == 0
                    and os.path.exists(entry.path)
                    and _looks_like_copy_name(entry.path, path)
                ):
                    if p.neighbor:
                        logger.debug(
                            "Recognized empty-file copy neighbour: %s -> %s",
                            entry.path,
                            path,
                        )
                    else:
                        self._index.add_or_update(path)
                    return (
                        f"{entry.path} -> {path}",
                        "Copy",
                        "Detection: Copy inferred from Windows copy filename for an empty file",
                        entry.path,
                        path,
                    )

        digest = {"value": None, "done": False}

        def new_hash():
            if not digest["done"]:
                digest["value"] = hash_file(path, self._hash_max_bytes)
                digest["done"] = True
            return digest["value"]

        # 1. Move: use the filesystem identity when available; otherwise
        # compare cached content hashes or the existing name-and-size fallback.
        for pending_delete in pending_deletes:
            old = self._index.get(pending_delete.path)
            if old is None or old.size != size:
                continue
            if old.file_id is not None and file_id == old.file_id:
                matched, basis = True, "same NTFS file ID"
            elif size > 0:
                old_hash = self._index.hash_for(old.path)
                current_hash = new_hash()
                if old_hash is not None and current_hash is not None:
                    matched, basis = old_hash == current_hash, "identical content"
                else:
                    matched = (
                        os.path.basename(pending_delete.path).casefold()
                        == os.path.basename(path).casefold()
                    )
                    basis = "same name and size; content not compared"
            else:
                matched = (
                    os.path.basename(pending_delete.path).casefold()
                    == os.path.basename(path).casefold()
                )
                basis = "same name and size; content not compared"
            if matched:
                with self._lock:
                    if pending_delete not in self._pending:
                        continue
                    self._pending.remove(pending_delete)
                self._index.rename(pending_delete.path, path)
                return (
                    f"{pending_delete.path} -> {path}",
                    "Move",
                    f"Detection: Move inferred (file removed and re-created within "
                    f"{self._move_window:g}s, {basis})",
                    pending_delete.path,
                    path,
                )

        # 2. Copy: same content as a protected file that still exists.
        candidates = [
            entry for entry in self._index.entries()
            if size > 0 and entry.size == size
            and _key(entry.path) != _key(path) and os.path.exists(entry.path)
        ]
        if candidates:
            current_hash = new_hash()
            if current_hash is not None:
                for entry in sorted(candidates, key=lambda item: _key(item.path)):
                    if self._index.hash_for(entry.path) == current_hash:
                        if not p.neighbor:
                            self._index.add_or_update(path)
                        return (
                            f"{entry.path} -> {path}",
                            "Copy",
                            "Detection: Copy inferred (new file's SHA-256 matches "
                            f"'{os.path.basename(entry.path)}'; Windows has no copy event)",
                            entry.path,
                            path,
                        )

        # 3. Plain create. A non-protected neighbour that matched nothing is not our business.
        if p.neighbor:
            logger.debug("Dropped unrelated neighbour event: %s", path)
            return None
        self._index.add_or_update(path)
        return (path, "Create", None, None, path)
