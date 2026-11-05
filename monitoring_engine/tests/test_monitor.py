import logging
import os
import sys
import threading
from types import SimpleNamespace
from unittest.mock import patch

import pytest

ENGINE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if ENGINE_DIR not in sys.path:
    sys.path.insert(0, ENGINE_DIR)

from core import monitor
from core.event_aggregator import EventAggregator, FileIndex


def _asset(path, kind, classification, name):
    return {
        "path": str(path),
        "asset_type": kind,
        "classification": classification,
        "asset_name": name,
    }


def _write(path, content):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as file:
        file.write(content)
    return path


def test_watch_roots_collapse_nested_assets_to_one_recursive_watch(tmp_path):
    outer = tmp_path / "outer"
    nested = outer / "nested"
    file_path = nested / "important.txt"
    assets = [
        _asset(outer, "folder", "Public", "outer"),
        _asset(nested, "folder", "Private", "nested"),
        _asset(file_path, "file", "Confidential", "important.txt"),
    ]

    roots = monitor._watch_roots(assets)

    assert monitor._normalized_path(str(outer)) in roots
    outer_watch = roots[monitor._normalized_path(str(outer))]
    assert {
        asset["asset_name"]
        for asset in outer_watch["assets"]
    } == {
        "outer",
        "nested",
        "important.txt",
    }
    assert monitor._normalized_path(str(tmp_path)) in roots
    assert outer_watch["recursive"] is True
    assert str(outer) in roots[monitor._normalized_path(str(tmp_path))]["folder_event_paths"]


def test_router_prefers_file_then_most_specific_folder(tmp_path):
    outer = tmp_path / "outer"
    nested = outer / "nested"
    file_path = nested / "important.txt"
    assets = [
        _asset(outer, "folder", "Public", "outer"),
        _asset(nested, "folder", "Private", "nested"),
        _asset(file_path, "file", "Confidential", "important.txt"),
    ]
    router = monitor._AssetRouter(assets)

    assert router.match(str(file_path))["asset_name"] == "important.txt"
    assert router.match(str(nested / "other.txt"))["asset_name"] == "nested"
    assert router.match(str(outer / "other.txt"))["asset_name"] == "outer"


def test_native_move_between_assets_emits_once_at_higher_classification(tmp_path):
    outer = tmp_path / "outer"
    nested = outer / "nested"
    source = _write(str(nested / "DAAMS 2.txt"), "sensitive data")
    destination = str(nested / "moved.txt")
    os.makedirs(nested, exist_ok=True)
    file_asset = _asset(source, "file", "Sensitive", "DAAMS 2.txt")
    folder_asset = _asset(nested, "folder", "Confidential", "nested")
    assets = [
        _asset(outer, "folder", "Public", "outer"),
        folder_asset,
        file_asset,
    ]
    index = FileIndex()
    index.build_assets(assets)
    router = monitor._AssetRouter(assets)
    aggregator = EventAggregator(
        emit=lambda *_args: None,
        emit_with_paths=router.emit,
        index=index,
        quiet=0,
    )
    handler = monitor.ProtectedAssetEventHandler(
        assets, index, aggregator, router
    )
    os.replace(source, destination)

    with patch.object(monitor, "record_activity") as record:
        handler.on_moved(SimpleNamespace(
            is_directory=False, src_path=source, dest_path=destination
        ))
        aggregator.flush(force=True)

    record.assert_called_once()
    assert record.call_args.kwargs["classification"] == "Confidential"
    assert "DAAMS 2.txt" in record.call_args.kwargs["detection_note"]
    assert record.call_args.kwargs["action"] == "Rename"


def test_inferred_move_across_watch_roots_emits_single_record(tmp_path):
    source_dir = tmp_path / "source"
    destination_dir = tmp_path / "destination"
    source = _write(str(source_dir / "DAAMS 2.txt"), "same contents")
    os.makedirs(destination_dir, exist_ok=True)
    destination = str(destination_dir / "DAAMS 2.txt")
    source_asset = _asset(source, "file", "Public", "source asset")
    destination_asset = _asset(destination_dir, "folder", "Confidential", "vault")
    assets = [source_asset, destination_asset]

    index = FileIndex()
    index.build_assets(assets)
    router = monitor._AssetRouter(assets)
    aggregator = EventAggregator(
        emit=lambda *_args: None,
        emit_with_paths=router.emit,
        index=index,
        quiet=0,
        move_window=0,
    )
    source_handler = monitor.ProtectedAssetEventHandler(
        [source_asset], index, aggregator, router
    )
    destination_handler = monitor.ProtectedAssetEventHandler(
        [destination_asset], index, aggregator, router
    )
    os.replace(source, destination)

    with patch.object(monitor, "record_activity") as record:
        source_handler.on_deleted(SimpleNamespace(
            is_directory=False, src_path=source
        ))
        destination_handler.on_created(SimpleNamespace(
            is_directory=False, src_path=destination
        ))
        destination_handler.on_modified(SimpleNamespace(
            is_directory=False, src_path=destination
        ))
        aggregator.flush(force=True)

    record.assert_called_once()
    assert record.call_args.kwargs["action"] == "Move"
    assert record.call_args.kwargs["classification"] == "Confidential"
    assert "source asset" in record.call_args.kwargs["detection_note"]


def test_unrelated_event_is_dropped_before_aggregation(tmp_path, caplog):
    caplog.set_level(logging.DEBUG, logger=monitor.__name__)
    protected = _write(str(tmp_path / "protected.txt"), "classified")
    asset = _asset(protected, "file", "Private", "protected.txt")
    index = FileIndex()
    index.build_assets([asset])
    router = monitor._AssetRouter([asset])
    aggregator = EventAggregator(lambda *_args: None, index)
    handler = monitor.ProtectedAssetEventHandler(
        [asset], index, aggregator, router
    )
    unrelated = _write(str(tmp_path / "unrelated.txt"), "x")
    same_size_modified = _write(str(tmp_path / "same-size.txt"), "unrelated!")

    handler.on_created(SimpleNamespace(is_directory=False, src_path=unrelated))
    handler.on_modified(SimpleNamespace(
        is_directory=False, src_path=same_size_modified
    ))

    assert aggregator.pending_count() == 0
    assert "Dropped neighbour outside copy candidates" in caplog.text


def test_recursive_file_watch_detects_neighbor_copy_without_logging_other_events(tmp_path):
    protected = _write(str(tmp_path / "protected.txt"), "classified")
    asset = _asset(protected, "file", "Private", "protected.txt")
    index = FileIndex()
    index.build_assets([asset])
    router = monitor._AssetRouter([asset])
    aggregator = EventAggregator(
        emit=lambda *_args: None,
        emit_with_paths=router.emit,
        index=index,
        quiet=0,
    )
    handler = monitor.ProtectedAssetEventHandler(
        [asset], index, aggregator, router
    )
    copy_path = _write(str(tmp_path / "subfolder" / "copy.txt"), "classified")

    with patch.object(monitor, "record_activity") as record:
        handler.on_modified(SimpleNamespace(
            is_directory=False, src_path=copy_path
        ))
        assert aggregator.pending_count() == 0

        handler.on_created(SimpleNamespace(
            is_directory=False, src_path=copy_path
        ))
        handler.on_modified(SimpleNamespace(
            is_directory=False, src_path=copy_path
        ))
        aggregator.flush(force=True)

    record.assert_called_once()
    assert record.call_args.kwargs["action"] == "Copy"
    assert record.call_args.kwargs["classification"] == "Private"


def test_file_watch_recursive_setting_is_used(tmp_path, monkeypatch):
    class FakeObserver:
        def schedule(self, handler, path, recursive):
            self.scheduled = (handler, path, recursive)
            return "watch"

    observer = FakeObserver()
    asset_path = _write(str(tmp_path / "protected.txt"), "content")
    asset = _asset(asset_path, "file", "Private", "protected.txt")
    index = FileIndex()
    router = monitor._AssetRouter([asset])
    aggregator = EventAggregator(lambda *_args: None, index)
    monkeypatch.setattr(monitor, "FILE_WATCH_RECURSIVE", True)

    info = monitor._schedule_watch(
        observer, str(tmp_path), [asset], index, aggregator, router,
        recursive=True,
    )

    assert info["handler"] is observer.scheduled[0]
    assert observer.scheduled[2] is True


def test_drive_root_file_watch_stays_nonrecursive_and_does_not_hide_folder(monkeypatch):
    monkeypatch.setattr(monitor, "FILE_WATCH_RECURSIVE", True)
    monkeypatch.setattr(
        monitor, "is_disallowed_root", lambda path: path.lower() == "c:\\"
    )
    assets = [
        _asset("C:\\root-file.txt", "file", "Private", "root-file.txt"),
        _asset("C:\\protected", "folder", "Confidential", "protected"),
    ]

    roots = monitor._watch_roots(assets)

    assert roots["c:\\"]["recursive"] is False
    assert [asset["asset_name"] for asset in roots["c:\\"]["assets"]] == [
        "root-file.txt"
    ]
    assert roots["c:\\protected"]["recursive"] is True


def test_protected_folder_delete_is_recorded_once(tmp_path):
    protected = str(tmp_path / "folder")
    asset = _asset(protected, "folder", "Private", "folder")
    index = FileIndex()
    router = monitor._AssetRouter([asset])
    aggregator = EventAggregator(lambda *_args: None, index)
    deleted = []
    handler = monitor.ProtectedAssetEventHandler(
        [asset], index, aggregator, router, on_asset_removed=deleted.append
    )

    with patch.object(monitor, "record_activity") as record:
        handler.on_deleted(SimpleNamespace(is_directory=True, src_path=protected))

    record.assert_called_once()
    assert record.call_args.kwargs["action"] == "Delete"
    assert record.call_args.kwargs["protected_root"] == protected
    assert deleted == [protected]


def test_protected_folder_move_is_recorded_at_higher_classification(tmp_path):
    source = str(tmp_path / "source")
    vault = str(tmp_path / "vault")
    destination = str(tmp_path / "vault" / "renamed")
    source_asset = _asset(source, "folder", "Private", "source")
    destination_asset = _asset(vault, "folder", "Confidential", "vault")
    assets = [source_asset, destination_asset]
    index = FileIndex()
    removed = []
    router = monitor._AssetRouter(assets, on_asset_moved=removed.append)
    aggregator = EventAggregator(lambda *_args: None, index)
    handler = monitor.ProtectedAssetEventHandler(
        assets, index, aggregator, router
    )

    with patch.object(monitor, "record_activity") as record, \
            patch.object(monitor, "relocate_asset", return_value=True) as relocate:
        handler.on_moved(SimpleNamespace(
            is_directory=True, src_path=source, dest_path=destination
        ))

    record.assert_called_once()
    assert record.call_args.kwargs["action"] == "Move"
    assert record.call_args.kwargs["classification"] == "Confidential"
    assert "source" in record.call_args.kwargs["detection_note"]
    relocate.assert_called_once_with(source, destination)
    assert removed == [source]


def test_same_directory_protected_folder_rename_is_a_move(tmp_path):
    source = str(tmp_path / "folder")
    destination = str(tmp_path / "renamed")
    asset = _asset(source, "folder", "Private", "folder")
    router = monitor._AssetRouter([asset])
    index = FileIndex()
    handler = monitor.ProtectedAssetEventHandler(
        [asset], index, EventAggregator(lambda *_args: None, index), router
    )

    with patch.object(monitor, "record_activity") as record:
        handler.on_moved(SimpleNamespace(
            is_directory=True, src_path=source, dest_path=destination
        ))

    record.assert_called_once()
    assert record.call_args.kwargs["action"] == "Move"


def test_parent_only_folder_watch_drops_sibling_file_events(tmp_path):
    protected = _write(str(tmp_path / "protected.txt"), "known content")
    asset = _asset(protected, "file", "Private", "protected.txt")
    index = FileIndex()
    index.build_assets([asset])
    router = monitor._AssetRouter([asset])
    aggregator = EventAggregator(lambda *_args: None, index)
    handler = monitor.ProtectedAssetEventHandler(
        [], index, aggregator, router, folder_event_paths=[str(tmp_path / "folder")]
    )

    sibling = _write(str(tmp_path / "sibling.txt"), "known content")
    handler.on_created(SimpleNamespace(is_directory=False, src_path=sibling))

    assert aggregator.pending_count() == 0


def test_missing_asset_is_logged_and_forces_alert():
    missing = _asset("C:\\missing\\protected.txt", "file", "Sensitive", "protected.txt")
    with patch.object(monitor, "record_activity") as record:
        monitor._record_missing_asset(missing)

    record.assert_called_once()
    assert record.call_args.kwargs["action"] == "Delete"
    assert record.call_args.kwargs["classification"] == "Sensitive"
    assert record.call_args.kwargs["force_alert"] is True


def test_disk_scan_detects_missed_move_modify_and_create_with_fake_clock(tmp_path):
    class Clock:
        def __init__(self):
            self.value = 100.0

        def __call__(self):
            return self.value

        def advance(self, seconds):
            self.value += seconds

    root = tmp_path / "protected"
    old_path = _write(str(root / "old.txt"), "same content")
    modified_path = _write(str(root / "modified.txt"), "old")
    assets = [_asset(str(root), "folder", "Private", "protected")]
    index = FileIndex()
    index.build_assets(assets)

    destination = str(root / "moved.txt")
    os.replace(old_path, destination)
    _write(modified_path, "updated content")
    created_path = _write(str(root / "created.txt"), "new content")

    clock = Clock()
    records = []
    aggregator = EventAggregator(
        lambda asset, action, note: records.append((asset, action, note)),
        index,
        quiet=0.1,
        move_window=2,
        clock=clock,
    )
    monitor._scan_assets_for_changes(assets, index, aggregator)
    clock.advance(3)
    aggregator.flush()

    assert {record[1] for record in records} == {"Move", "Modify", "Create"}
    assert any(record[0] == f"{old_path} -> {destination}" for record in records)
    assert any(record[0] == modified_path for record in records)
    assert any(record[0] == created_path for record in records)


def test_console_close_handler_requests_keyboard_interrupt_shutdown(monkeypatch):
    registered = threading.Event()
    callbacks = []
    callback_results = []
    flushes = []
    heartbeats = []
    unregistered = []

    class FakeKernel32:
        def SetConsoleCtrlHandler(self, callback, add):
            if add:
                callbacks.append(callback)
                registered.set()
            else:
                unregistered.append(callback)
            return 1

    class FakeObserver:
        def start(self):
            pass

        def stop(self):
            pass

        def join(self):
            pass

    class FakeTicker:
        def register(self, _aggregator):
            pass

        def start(self):
            pass

        def stop(self):
            pass

        def join(self):
            pass

    class FakeClipboardWatcher:
        def __init__(self, **_kwargs):
            pass

        def start(self):
            pass

        def stop(self):
            pass

        def join(self):
            pass

    class FakeAggregator:
        def __init__(self, **_kwargs):
            pass

        def flush(self, force=False):
            flushes.append(force)

    monkeypatch.setattr(
        monitor.ctypes,
        "WINFUNCTYPE",
        lambda *_args: lambda callback: callback,
        raising=False,
    )
    monkeypatch.setattr(
        monitor.ctypes,
        "windll",
        SimpleNamespace(kernel32=FakeKernel32()),
        raising=False,
    )
    monkeypatch.setattr(monitor.database, "initialize_database", lambda: None)
    monkeypatch.setattr(monitor, "Observer", FakeObserver)
    monkeypatch.setattr(monitor, "_Ticker", FakeTicker)
    monkeypatch.setattr(monitor, "ClipboardWatcher", FakeClipboardWatcher)
    monkeypatch.setattr(monitor, "EventAggregator", FakeAggregator)
    monkeypatch.setattr(monitor, "ENABLE_OPEN_DETECTION", False)
    monkeypatch.setattr(monitor, "get_all_assets", lambda **_kwargs: [])
    monkeypatch.setattr(monitor.sync_service, "start_sync_service", lambda: None)
    monkeypatch.setattr(monitor.sync_service, "stop_sync_service", lambda: None)
    monkeypatch.setattr(monitor, "_save_heartbeat", heartbeats.append)

    def send_close_event():
        assert registered.wait(timeout=5)
        callback_results.append(callbacks[0](monitor.CTRL_CLOSE_EVENT))

    closer = threading.Thread(target=send_close_event)
    closer.start()
    monitor.start_monitoring()
    closer.join(timeout=5)

    assert not closer.is_alive()
    assert callback_results == [True]
    assert unregistered == callbacks
    assert True in flushes
    assert heartbeats[-1] == "stopped"
