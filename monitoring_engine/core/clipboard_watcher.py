"""
clipboard_watcher.py

Detects file Copy operations through the Windows clipboard (CF_HDROP).
Windows Explorer puts file paths on the clipboard when a user copies or cuts
files, along with a 'Preferred DropEffect' value (1 = copy, 2 = cut).
A cut is ignored on purpose: nothing has moved yet, and the real Move is
recorded by the filesystem watcher when the file is pasted.
"""

import logging
import ntpath
import threading
import time
from typing import Callable, List, Optional, Tuple

try:
    import win32clipboard
except ImportError:
    win32clipboard = None

logger = logging.getLogger(__name__)


def find_covering_asset(path: str, assets: list) -> Optional[dict]:
    """
    Matches the asset itself or any file inside a protected folder.
    The boundary is a backslash, so 'C:\\Test 2' does not match 'C:\\Test 22'.
    Case-insensitive. The longest (most specific) asset wins.
    Uses ntpath so it can be tested off Windows.
    """
    if not path or not assets:
        return None

    norm_path = ntpath.normcase(ntpath.normpath(path))
    matches = []

    for asset in assets:
        asset_path = asset.get("path")
        if not asset_path:
            continue

        norm_asset = ntpath.normcase(ntpath.normpath(asset_path))
        is_file = asset.get("asset_type") == "file"

        if norm_path == norm_asset:
            matches.append((len(norm_asset), asset))
        elif not is_file:
            prefix = norm_asset.rstrip("\\") + "\\"
            if norm_path.startswith(prefix):
                matches.append((len(norm_asset), asset))

    if not matches:
        return None

    # The longest (most specific) asset wins
    matches.sort(key=lambda item: item[0], reverse=True)
    return matches[0][1]


def _default_read_clipboard() -> Tuple[List[str], int]:
    """
    Reads file paths and Preferred DropEffect from the Windows clipboard.
    Retries OpenClipboard up to 5 times.
    Returns:
        (paths, drop_effect): list of file path strings, and drop effect int
        (1 = copy, 2 = cut). Defaults to ([], 1) if unavailable.
    """
    if win32clipboard is None:
        return ([], 1)

    opened = False
    for attempt in range(5):
        try:
            win32clipboard.OpenClipboard()
            opened = True
            break
        except Exception:
            time.sleep(0.05)

    if not opened:
        return ([], 1)

    try:
        if not win32clipboard.IsClipboardFormatAvailable(win32clipboard.CF_HDROP):
            return ([], 1)

        paths = win32clipboard.GetClipboardData(win32clipboard.CF_HDROP) or []
        effect = 1  # Default to copy (1)

        try:
            drop_effect_format = win32clipboard.RegisterClipboardFormat("Preferred DropEffect")
            if win32clipboard.IsClipboardFormatAvailable(drop_effect_format):
                data = win32clipboard.GetClipboardData(drop_effect_format)
                if isinstance(data, bytes):
                    effect = int.from_bytes(data[:4], byteorder="little")
                elif isinstance(data, int):
                    effect = data
        except Exception:
            pass

        return (list(paths), effect)
    finally:
        win32clipboard.CloseClipboard()


class ClipboardWatcher(threading.Thread):
    """
    Polls the Windows clipboard every 0.3 s for file Copy and Cut events.
    Ignores clipboard content that was already present at start.
    On change, reads the paths and drop effect, and calls record_activity
    for each protected path.
    """

    def __init__(
        self,
        get_assets: Optional[Callable[[], list]] = None,
        read: Optional[Callable[[], Tuple[List[str], int]]] = None,
        sequence: Optional[Callable[[], int]] = None,
        record: Optional[Callable] = None,
        interval: float = 0.3,
        daemon: bool = True,
    ):
        super().__init__(name="DAAMS-ClipboardWatcher", daemon=daemon)
        self.get_assets = get_assets or (lambda: [])
        self._read_func = read
        self._sequence_func = sequence
        self._record_func = record
        self._interval = interval
        self._stop_event = threading.Event()
        self._last_sequence = None

        # Capture initial sequence number so pre-existing clipboard content is ignored
        if self._sequence_func is not None:
            try:
                self._last_sequence = self._sequence_func()
            except Exception:
                self._last_sequence = None
        elif win32clipboard is not None:
            try:
                self._last_sequence = win32clipboard.GetClipboardSequenceNumber()
            except Exception:
                self._last_sequence = None

    def _get_record_function(self):
        if self._record_func is not None:
            return self._record_func
        from services.activity_logger import record_activity
        return record_activity

    def _poll_once(self, seq_fn, read_fn, record_fn):
        try:
            current_seq = seq_fn()
        except Exception:
            return

        if self._last_sequence is not None and current_seq == self._last_sequence:
            return

        self._last_sequence = current_seq

        try:
            paths, effect = read_fn()
        except Exception:
            return

        if not paths:
            return

        # Ctrl+X (cut) changes nothing on disk. The file only moves when it is
        # pasted, and the filesystem watcher reports that as the real Move
        # (source -> destination). Logging at cut time produced a bogus
        # arrow-less "Move", so a cut is intentionally NOT recorded here.
        if effect & 2:
            return

        action = "Copy"
        assets = self.get_assets()

        for path in paths:
            covering = find_covering_asset(path, assets)
            if covering is not None:
                classification = covering.get("classification")
                note = "Detection: clipboard, reported at copy time, before any paste"
                try:
                    record_fn(
                        asset_path=path,
                        action=action,
                        classification=classification,
                        detection_note=note,
                    )
                except Exception as e:
                    print(f"[CLIPBOARD WARNING] Could not record clipboard activity: {e}")

    def poll(self):
        """Execute a single poll iteration (useful for synchronous testing)."""
        seq_fn = self._sequence_func or (win32clipboard.GetClipboardSequenceNumber if win32clipboard else None)
        read_fn = self._read_func or _default_read_clipboard
        record_fn = self._get_record_function()
        if seq_fn is None or read_fn is None:
            return
        self._poll_once(seq_fn, read_fn, record_fn)

    def run(self):
        if self._sequence_func is None or self._read_func is None:
            if win32clipboard is None:
                print("[WARNING] pywin32 is not installed; clipboard copy/cut detection is disabled.")
                return

        seq_fn = self._sequence_func or win32clipboard.GetClipboardSequenceNumber
        read_fn = self._read_func or _default_read_clipboard
        record_fn = self._get_record_function()

        if self._last_sequence is None:
            try:
                self._last_sequence = seq_fn()
            except Exception:
                pass

        while not self._stop_event.wait(self._interval):
            self._poll_once(seq_fn, read_fn, record_fn)

    def stop(self):
        self._stop_event.set()
