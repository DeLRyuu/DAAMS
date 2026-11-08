"""
Tests for core/event_aggregator.py.

Each test replays the RAW event sequence that Windows/Watchdog was observed
to produce (tools/raw_event_probe.py output) and checks that exactly one
correct activity is recorded. A fake clock is used, so no test sleeps.

Run from the monitoring_engine folder:   python -m pytest tests/test_event_aggregator.py -v
"""

import os
import sys
import logging
import threading

import pytest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from core import event_aggregator
from core.event_aggregator import EventAggregator, FileIndex


class Clock:
    def __init__(self):
        self.t = 1000.0

    def __call__(self):
        return self.t

    def advance(self, seconds):
        self.t += seconds


@pytest.fixture
def env(tmp_path):
    clock = Clock()
    records = []
    index = FileIndex()
    agg = EventAggregator(lambda asset, action, note: records.append((asset, action, note)),
                          index, clock=clock)
    return tmp_path, clock, records, index, agg


def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as f:
        f.write(text)
    return str(path)


def settle(clock, agg, seconds=3.0):
    clock.advance(seconds)
    agg.flush()


def actions(records):
    return [r[1] for r in records]


def test_copy_in_same_folder_is_one_copy_not_modify(env):
    root, clock, records, index, agg = env
    src = write(root / "DAAMS 4.txt", "secret payroll")
    index.build(str(root))
    dst = write(root / "DAAMS 4 - Copy.txt", "secret payroll")
    agg.on_created(dst); agg.on_modified(dst); agg.on_modified(dst)   # the probe's exact sequence
    settle(clock, agg)
    assert actions(records) == ["Copy"]
    assert records[0][0] == f"{src} -> {dst}"
    assert "inferred" in records[0][2]


def test_new_file_with_different_content_is_plain_create(env):
    root, clock, records, index, agg = env
    write(root / "a.txt", "aaaa")
    index.build(str(root))
    new = write(root / "b.txt", "bbbb")
    agg.on_created(new); agg.on_modified(new)
    settle(clock, agg)
    assert records == [(new, "Create", None)]


def test_windows_style_move_between_folders_is_one_move(env):
    root, clock, records, index, agg = env
    old = write(root / "DAAMS 5.txt", "move me")
    index.build(str(root))
    os.makedirs(root / "Sub", exist_ok=True)
    new = str(root / "Sub" / "DAAMS 5.txt")
    os.replace(old, new)
    agg.on_deleted(old); agg.on_created(new); agg.on_modified(new)  # deleted + created + modified
    settle(clock, agg)
    assert actions(records) == ["Move"]
    assert records[0][0] == f"{old} -> {new}"
    assert "same NTFS file ID" in records[0][2]


def test_delete_alone_is_delete_after_pairing_window(env):
    root, clock, records, index, agg = env
    f = write(root / "gone.txt", "bye")
    index.build(str(root))
    os.remove(f)
    agg.on_deleted(f)
    clock.advance(1.0); agg.flush()
    assert records == []                      # still waiting in case it is a Move
    clock.advance(1.5); agg.flush()
    assert records == [(f, "Delete", None)]


def test_rename_with_extra_modify_is_one_rename(env):
    root, clock, records, index, agg = env
    a = write(root / "Copy.txt", "x")
    index.build(str(root))
    b = str(root / "DAAMS 5.txt")
    os.replace(a, b)
    agg.on_moved(a, b); agg.on_modified(b)
    settle(clock, agg)
    assert records == [(f"{a} -> {b}", "Rename", None)]


def test_native_moved_event_across_folders_is_move(env):
    root, clock, records, index, agg = env
    a = write(root / "f.txt", "x")
    index.build(str(root))
    os.makedirs(root / "Sub")
    b = str(root / "Sub" / "f.txt")
    os.replace(a, b)
    agg.on_moved(a, b)
    settle(clock, agg)
    assert actions(records) == ["Move"] and records[0][2] is None   # native event, not inferred


def test_delete_then_recreate_same_path_is_one_modify(env):
    root, clock, records, index, agg = env
    f = write(root / "doc.txt", "v1")
    index.build(str(root))
    agg.on_deleted(f)
    write(root / "doc.txt", "v2")
    agg.on_created(f); agg.on_modified(f)
    settle(clock, agg)
    assert records == [(f, "Modify", None)]


def test_temp_file_renamed_over_target_is_one_modify(env):
    root, clock, records, index, agg = env
    target = write(root / "report.docx", "v1")
    index.build(str(root))
    tmp1, tmp2 = str(root / "~tmp1"), str(root / "~tmp2")
    agg.on_moved(target, tmp1)        # original moved aside
    agg.on_moved(tmp2, target)        # new version renamed into place
    write(root / "report.docx", "v2")
    settle(clock, agg)
    assert records == [(target, "Modify", None)]


def test_many_modifies_in_one_save_are_one_modify(env):
    root, clock, records, index, agg = env
    f = write(root / "n.txt", "1")
    index.build(str(root))
    for _ in range(5):
        agg.on_modified(f); clock.advance(0.2)
    settle(clock, agg)
    assert actions(records) == ["Modify"]


def test_waits_for_quiet_period(env):
    root, clock, records, index, agg = env
    f = write(root / "n.txt", "1")
    index.build(str(root))
    agg.on_modified(f)
    clock.advance(0.5); agg.flush()
    assert records == []
    clock.advance(0.6); agg.flush()
    assert actions(records) == ["Modify"]


def test_empty_files_with_unrelated_names_are_not_called_copies(env):
    root, clock, records, index, agg = env
    write(root / "e1.txt", "")
    index.build(str(root))
    e2 = write(root / "e2.txt", "")
    agg.on_created(e2)
    settle(clock, agg)
    assert records == [(e2, "Create", None)]


@pytest.mark.parametrize(
    ("source_name", "copy_name"),
    [
        ("report.txt", "report - Copy.txt"),
        ("report.txt", "report (2).txt"),
        ("report.txt", "report - Copy (2).txt"),
        ("archive.tar.gz", "archive.tar - Copy.gz"),
    ],
)
def test_empty_file_windows_copy_names_are_detected(
    env, source_name, copy_name
):
    root, clock, records, index, agg = env
    source = write(root / source_name, "")
    index.build(str(root))
    destination = write(root / copy_name, "")

    agg.on_created(destination, neighbor=True)
    settle(clock, agg)

    assert actions(records) == ["Copy"]
    assert records[0][0] == f"{source} -> {destination}"
    assert "empty file" in records[0][2]


def test_chained_copy_of_a_copy_is_detected(env):
    root, clock, records, index, agg = env
    write(root / "o.txt", "same")
    index.build(str(root))
    c1 = write(root / "c1.txt", "same")
    agg.on_created(c1); settle(clock, agg)
    c2 = write(root / "c2.txt", "same")
    agg.on_created(c2); settle(clock, agg)
    assert actions(records) == ["Copy", "Copy"]


def test_single_file_asset_neighbour_copy_is_detected(env):
    root, clock, records, index, agg = env
    target = write(root / "protected.txt", "classified")
    index.build(target)                                   # index holds ONLY the protected file
    dup = write(root / "protected - Copy.txt", "classified")
    agg.on_created(dup, neighbor=True); agg.on_modified(dup)
    settle(clock, agg)
    assert actions(records) == ["Copy"]
    assert records[0][0] == f"{target} -> {dup}"


def test_unrelated_neighbour_is_silent_and_never_hashed(env, monkeypatch):
    root, clock, records, index, agg = env
    target = write(root / "protected.txt", "classified")
    index.build(target)
    calls = []
    real = event_aggregator.hash_file
    monkeypatch.setattr(event_aggregator, "hash_file", lambda p, m=0: calls.append(p) or real(p))
    other = write(root / "holiday.txt", "completely different length text")
    agg.on_created(other, neighbor=True)
    settle(clock, agg)
    assert records == []                 # no record about a non-protected file
    assert calls == []                   # and its contents were never read


def test_same_size_but_different_neighbour_is_hashed_but_not_logged(env, caplog):
    caplog.set_level(logging.DEBUG, logger=event_aggregator.__name__)
    root, clock, records, index, agg = env
    target = write(root / "protected.txt", "AAAA")
    index.build(target)
    other = write(root / "other.txt", "BBBB")
    agg.on_created(other, neighbor=True)
    settle(clock, agg)
    assert records == []
    assert "Dropped unrelated neighbour event" in caplog.text


def test_move_without_hash_falls_back_to_name_and_size(env, monkeypatch):
    root, clock, records, index, agg = env
    big = write(root / "big.bin", "x" * 100)
    index = FileIndex(hash_max_bytes=10)                  # too small to hash anything
    index.build(str(root))
    metadata = event_aggregator._file_metadata
    monkeypatch.setattr(
        event_aggregator,
        "_file_metadata",
        lambda path: (
            (*metadata(path)[:2], None) if metadata(path) is not None else None
        ),
    )
    agg = EventAggregator(lambda a, b, c: records.append((a, b, c)), index,
                          hash_max_bytes=10, clock=clock)
    os.makedirs(root / "Sub")
    new = str(root / "Sub" / "big.bin")
    os.replace(big, new)
    agg.on_deleted(big); agg.on_created(new)
    settle(clock, agg)
    assert actions(records) == ["Move"]
    assert "content not compared" in records[0][2]


def test_index_metadata_is_lazy_and_hashing_does_not_hold_index_lock(tmp_path, monkeypatch):
    target = write(tmp_path / "source.txt", "initial content")
    index = FileIndex()
    hash_calls = []
    original_hash_file = event_aggregator.hash_file

    def checked_hash(path, max_bytes=event_aggregator.DEFAULT_HASH_MAX_BYTES):
        acquired = []

        def acquire_from_other_thread():
            acquired.append(index._lock.acquire(timeout=1))
            if acquired[-1]:
                index._lock.release()

        worker = threading.Thread(target=acquire_from_other_thread)
        worker.start()
        worker.join()
        assert acquired == [True]
        hash_calls.append(path)
        return original_hash_file(path, max_bytes)

    monkeypatch.setattr(event_aggregator, "hash_file", checked_hash)
    index.build(target)

    entry = index.get(target)
    stat = os.stat(target)
    assert entry is not None
    assert entry.size == stat.st_size
    assert entry.mtime_ns == stat.st_mtime_ns
    assert entry.file_id == (
        (stat.st_dev, stat.st_ino) if stat.st_ino else None
    )
    assert entry.hash is None
    assert hash_calls == []

    digest = index.hash_for(target)

    assert digest == original_hash_file(target)
    assert hash_calls == [target]
    assert index.get(target).hash == digest


def test_changed_mtime_invalidates_cached_hash(tmp_path):
    target = write(tmp_path / "source.txt", "first")
    index = FileIndex()
    index.build(target)
    first_hash = index.hash_for(target)
    previous_mtime = os.stat(target).st_mtime_ns

    with open(target, "w", encoding="utf-8") as file:
        file.write("other")
    os.utime(target, ns=(previous_mtime + 10_000_000, previous_mtime + 10_000_000))

    assert index.hash_for(target) != first_hash
    assert index.get(target).mtime_ns == os.stat(target).st_mtime_ns


def test_copy_resolution_hashes_without_holding_aggregator_lock(tmp_path, monkeypatch):
    source = write(tmp_path / "source.txt", "same data")
    destination = write(tmp_path / "copy.txt", "same data")
    index = FileIndex()
    index.build(source)
    agg = EventAggregator(lambda *_args: None, index, quiet=0)
    agg.on_created(destination)
    original_hash_file = event_aggregator.hash_file
    lock_checks = []

    def checked_hash(path, max_bytes=event_aggregator.DEFAULT_HASH_MAX_BYTES):
        worker = threading.Thread(
            target=lambda: lock_checks.append(agg._lock.acquire(timeout=1))
        )
        worker.start()
        worker.join()
        if lock_checks[-1]:
            agg._lock.release()
        return original_hash_file(path, max_bytes)

    monkeypatch.setattr(event_aggregator, "hash_file", checked_hash)

    agg.flush(force=True)

    assert lock_checks
    assert all(lock_checks)


def test_large_file_copy_is_not_claimed(env):
    root, clock, records, index, agg = env
    write(root / "big.bin", "x" * 100)
    index = FileIndex(hash_max_bytes=10)
    index.build(str(root))
    agg = EventAggregator(lambda a, b, c: records.append((a, b, c)), index,
                          hash_max_bytes=10, clock=clock)
    dup = write(root / "big2.bin", "x" * 100)
    agg.on_created(dup)
    settle(clock, agg)
    assert records == [(dup, "Create", None)]             # honest: cannot verify, so plain Create


def test_create_then_rename_reports_both(env):
    root, clock, records, index, agg = env
    new = write(root / "New Text Document.txt", "hello")
    agg.on_created(new)
    final = str(root / "Report.txt")
    os.replace(new, final)
    agg.on_moved(new, final)
    settle(clock, agg)
    assert actions(records) == ["Create", "Rename"]


def test_force_flush_emits_everything_pending(env):
    root, clock, records, index, agg = env
    f = write(root / "n.txt", "1")
    index.build(str(root))
    agg.on_modified(f)
    agg.flush(force=True)
    assert actions(records) == ["Modify"] and agg.pending_count() == 0


def test_emit_failure_does_not_stop_other_records(env):
    root, clock, records, index, agg = env
    a = write(root / "a.txt", "1"); b = write(root / "b.txt", "2")
    index.build(str(root))
    seen = []

    def flaky(asset, action, note):
        seen.append(asset)
        if asset == a:
            raise RuntimeError("boom")

    agg2 = EventAggregator(flaky, index, clock=clock)
    agg2.on_modified(a); agg2.on_modified(b)
    settle(clock, agg2)
    assert sorted(seen) == sorted([a, b])


def test_metadata_only_event_gives_no_modify(env):
    root, clock, records, index, agg = env
    f = write(root / "doc.txt", "same content")
    index.build(str(root))
    index.hash_for(f)
    stat = os.stat(f)
    os.utime(f, ns=(stat.st_mtime_ns + 10_000_000, stat.st_mtime_ns + 10_000_000))
    agg.on_modified(f)
    settle(clock, agg)
    assert records == []


def test_changed_content_gives_modify(env):
    root, clock, records, index, agg = env
    f = write(root / "doc.txt", "initial content")
    index.build(str(root))
    index.hash_for(f)
    write(f, "new modified content")
    agg.on_modified(f)
    settle(clock, agg)
    assert actions(records) == ["Modify"]
    assert records == [(f, "Modify", None)]


def test_large_file_metadata_only_gives_no_modify(env):
    root, clock, records, index, agg = env
    big = write(root / "big.bin", "x" * 100)
    index = FileIndex(hash_max_bytes=10)
    index.build(str(root))
    agg = EventAggregator(lambda a, b, c: records.append((a, b, c)), index,
                          hash_max_bytes=10, clock=clock)
    agg.on_modified(big)
    settle(clock, agg)
    assert records == []


def test_large_file_changed_content_gives_modify(env):
    root, clock, records, index, agg = env
    big = write(root / "big.bin", "x" * 100)
    index = FileIndex(hash_max_bytes=10)
    index.build(str(root))
    agg = EventAggregator(lambda a, b, c: records.append((a, b, c)), index,
                          hash_max_bytes=10, clock=clock)
    write(big, "y" * 150)
    agg.on_modified(big)
    settle(clock, agg)
    assert actions(records) == ["Modify"]


def test_has_pending_for_covers_path_and_rename_source(env):
    tmp, clock, records, index, agg = env
    src = write(tmp / "pc-02" / "A.txt", "hello")
    dest = str(tmp / "pc-02" / "B.txt")
    index.build_assets([{"path": str(tmp / "pc-02"), "asset_type": "folder"}])
    os.rename(src, dest)

    assert not agg.has_pending_for(src)
    agg.on_moved(src, dest)
    assert agg.has_pending_for(src)    # old name is still being explained
    assert agg.has_pending_for(dest)

    settle(clock, agg)
    assert actions(records) == ["Rename"]
    assert not agg.has_pending_for(src)
