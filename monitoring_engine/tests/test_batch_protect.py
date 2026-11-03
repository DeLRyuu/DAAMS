"""
test_batch_protect.py

Unit tests for Phase 9b: Batch protect.
Verifies:
1. Queue append, read, clear, and deduplication logic.
2. File locking: coordinator acquires lock, follower exits silently, lock release, and stale lock recovery.
3. Batch classification picker dialog structure and descriptions (no risk numbers).
4. Single selection vs batch selection handling.
5. Skip rules: items already protected or inside protected folders are skipped.
6. PIN is asked once for the entire batch.
7. Final message reports 'N protected, M skipped' with skipped names.
"""

import os
import sys
import tempfile
import time
import unittest
from unittest.mock import MagicMock, call, patch

from cli import protect_asset_cli
from cli.protect_asset_cli import (
    append_to_queue,
    get_lock_file,
    get_queue_file,
    main,
    protect_batch_assets,
    protect_single_asset,
    read_and_clear_queue,
    release_lock,
    try_acquire_lock,
)
from ui import glass_ui


class TestBatchQueueAndLock(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        self.queue_file = os.path.join(self.temp_dir.name, "test_queue.txt")
        self.lock_file = os.path.join(self.temp_dir.name, "test_lock.lock")

    def tearDown(self):
        self.temp_dir.cleanup()

    def test_queue_append_read_and_clear(self):
        """Multiple paths appended to queue are read in order and cleared."""
        p1 = os.path.abspath("file1.txt")
        p2 = os.path.abspath("file2.txt")
        p3 = os.path.abspath("file3.txt")

        append_to_queue(p1, self.queue_file)
        append_to_queue(p2, self.queue_file)
        append_to_queue(p3, self.queue_file)

        paths = read_and_clear_queue(self.queue_file)
        self.assertEqual(paths, [p1, p2, p3])

        # Queue file should be cleared / removed
        self.assertEqual(read_and_clear_queue(self.queue_file), [])

    def test_queue_deduplication(self):
        """Duplicate paths appended to queue are deduplicated preserving order."""
        p1 = os.path.abspath("file1.txt")
        p2 = os.path.abspath("file2.txt")

        append_to_queue(p1, self.queue_file)
        append_to_queue(p2, self.queue_file)
        append_to_queue(p1, self.queue_file)

        paths = read_and_clear_queue(self.queue_file)
        self.assertEqual(paths, [p1, p2])

    def test_read_empty_queue(self):
        """Reading a non-existent queue returns an empty list."""
        paths = read_and_clear_queue(self.queue_file)
        self.assertEqual(paths, [])

    def test_lock_acquire_and_release(self):
        """First instance acquires lock; second instance fails until lock is released."""
        fd1 = try_acquire_lock(self.lock_file)
        self.assertIsNotNone(fd1)

        # Second attempt must fail
        fd2 = try_acquire_lock(self.lock_file)
        self.assertIsNone(fd2)

        # Release first lock
        release_lock(fd1, self.lock_file)

        # Third attempt must now succeed
        fd3 = try_acquire_lock(self.lock_file)
        self.assertIsNotNone(fd3)
        release_lock(fd3, self.lock_file)

    def test_stale_lock_recovery(self):
        """Locks older than 15 seconds are treated as stale and cleaned up."""
        fd = try_acquire_lock(self.lock_file)
        self.assertIsNotNone(fd)
        os.close(fd)

        # Set mtime to 20 seconds in the past
        past = time.time() - 20.0
        os.utime(self.lock_file, (past, past))

        # Should recover and acquire new lock
        new_fd = try_acquire_lock(self.lock_file)
        self.assertIsNotNone(new_fd)
        release_lock(new_fd, self.lock_file)


class TestBatchClassificationUI(unittest.TestCase):
    def test_ask_batch_classification_empty(self):
        """Empty path list returns empty dict immediately without opening UI."""
        res = glass_ui.ask_batch_classification([])
        self.assertEqual(res, {})

    def test_batch_dialog_data_building_with_none_classifications(self):
        """Calls the batch dialog's data-building logic (not the window) with 5 paths and classifications=None."""
        paths = [rf"C:\test\file_{i}.txt" for i in range(5)]

        def fake_show(self_win):
            protect_cb = None
            for draw_call in self_win._draws:
                if getattr(draw_call, "args", None) and len(draw_call.args) >= 2:
                    spec = draw_call.args[1]
                    if spec[0] == "Protect":
                        protect_cb = spec[2]
                        break
            result = protect_cb() if callable(protect_cb) else None
            self_win.root.destroy()
            return result

        with patch.object(glass_ui.GlassWindow, "show", fake_show):
            res = glass_ui.ask_batch_classification(paths, classifications=None)
            self.assertIsNotNone(res)
            self.assertEqual(len(res), 5)
            for path in paths:
                self.assertEqual(res[path], "Public")

    def test_batch_descriptions_hide_risk_numbers(self):
        """Verify that descriptions used in batch UI have no risk scores or digits."""
        for name, desc in glass_ui.DEFAULT_CLASSIFICATION_DESCRIPTIONS.items():
            self.assertFalse(any(ch.isdigit() for ch in desc), f"Digit found in {desc}")
            self.assertNotIn("+", desc)
            self.assertNotIn("risk", desc.lower())


class TestBatchProtectFlow(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        self.queue_file = os.path.join(self.temp_dir.name, "queue.txt")
        self.lock_file = os.path.join(self.temp_dir.name, "lock.lock")

        self.fake_protected_file = {
            "path": os.path.abspath(r"C:\Company\ConfidentialDoc.docx"),
            "asset_name": "ConfidentialDoc.docx",
            "asset_type": "file",
            "classification": "Confidential",
            "status": "protected",
        }
        self.fake_protected_folder = {
            "path": os.path.abspath(r"C:\Company\HR_Dept"),
            "asset_name": "HR_Dept",
            "asset_type": "folder",
            "classification": "Sensitive",
            "status": "protected",
        }
        self.existing_assets = [self.fake_protected_file, self.fake_protected_folder]

    def tearDown(self):
        self.temp_dir.cleanup()

    @patch("cli.protect_asset_cli.protect_single_asset")
    @patch("cli.protect_asset_cli.protect_batch_assets")
    def test_single_selection_invokes_single_flow(self, mock_batch, mock_single):
        """When queue resolves to 1 item, single-selection flow is executed."""
        p1 = os.path.abspath("single.txt")
        with patch.dict(os.environ, {
            "DAAMS_BATCH_QUEUE_FILE": self.queue_file,
            "DAAMS_BATCH_LOCK_FILE": self.lock_file,
            "DAAMS_BATCH_WAIT_SECONDS": "0",
        }):
            with patch.object(sys, "argv", ["protect_asset_cli.py", p1]):
                main()

        mock_single.assert_called_once_with(p1)
        mock_batch.assert_not_called()

    @patch("cli.protect_asset_cli.protect_single_asset")
    @patch("cli.protect_asset_cli.protect_batch_assets")
    def test_batch_selection_invokes_batch_flow(self, mock_batch, mock_single):
        """When multiple items are queued, batch-selection flow is executed."""
        p1 = os.path.abspath("item1.txt")
        p2 = os.path.abspath("item2.txt")

        # Simulate another instance already queued p1
        append_to_queue(p1, self.queue_file)

        with patch.dict(os.environ, {
            "DAAMS_BATCH_QUEUE_FILE": self.queue_file,
            "DAAMS_BATCH_LOCK_FILE": self.lock_file,
            "DAAMS_BATCH_WAIT_SECONDS": "0",
        }):
            with patch.object(sys, "argv", ["protect_asset_cli.py", p2]):
                main()

        mock_batch.assert_called_once()
        batch_paths = mock_batch.call_args[0][0]
        self.assertEqual(set(batch_paths), {p1, p2})
        mock_single.assert_not_called()

    def test_follower_instance_exits_silently(self):
        """Follower instance appends its path and exits silently with code 0 if lock is held."""
        p1 = os.path.abspath("follower.txt")
        lock_fd = try_acquire_lock(self.lock_file)
        self.assertIsNotNone(lock_fd)

        try:
            with patch.dict(os.environ, {
                "DAAMS_BATCH_QUEUE_FILE": self.queue_file,
                "DAAMS_BATCH_LOCK_FILE": self.lock_file,
                "DAAMS_BATCH_WAIT_SECONDS": "0",
            }):
                with patch.object(sys, "argv", ["protect_asset_cli.py", p1]):
                    with self.assertRaises(SystemExit) as cm:
                        main()
                    self.assertEqual(cm.exception.code, 0)

            # Path was appended to queue by follower
            paths = read_and_clear_queue(self.queue_file)
            self.assertEqual(paths, [p1])
        finally:
            release_lock(lock_fd, self.lock_file)

    @patch("cli.protect_asset_cli.glass_ui.show_message")
    @patch("cli.protect_asset_cli.glass_ui.run_with_loading", side_effect=lambda msg, fn, *a, **kw: fn(*a, **kw))
    @patch("cli.protect_asset_cli.pin_auth.confirm_with_pin", return_value=True)
    @patch("cli.protect_asset_cli.glass_ui.ask_batch_classification")
    @patch("cli.protect_asset_cli.ar.get_all_assets")
    @patch("cli.protect_asset_cli.ar.protect_asset", return_value=(True, "Success"))
    def test_batch_protect_skip_rules_and_final_message(
        self, mock_protect, mock_get_assets, mock_ask_batch, mock_pin, mock_loading, mock_show_message
    ):
        """
        Batch protect skips:
        - items already protected (status_kind == 'self')
        - items inside a protected folder (status_kind == 'folder')
        Protects unprotected items, asks PIN once, and reports 'N protected, M skipped'.
        """
        mock_get_assets.return_value = list(self.existing_assets)

        f_already_self = self.fake_protected_file["path"]
        f_inside_folder = os.path.join(self.fake_protected_folder["path"], "sub", "report.xlsx")
        f_unprotected_1 = os.path.abspath(r"C:\Other\Doc1.txt")
        f_unprotected_2 = os.path.abspath(r"C:\Other\Doc2.txt")

        paths = [f_already_self, f_inside_folder, f_unprotected_1, f_unprotected_2]

        mock_ask_batch.return_value = {
            f_already_self: "Confidential",
            f_inside_folder: "Sensitive",
            f_unprotected_1: "Private",
            f_unprotected_2: "Public",
        }

        protect_batch_assets(paths)

        # PIN must be asked exactly once
        mock_pin.assert_called_once_with("protect 4 assets")

        # ar.protect_asset should only be called for the 2 unprotected files
        self.assertEqual(mock_protect.call_count, 2)
        protected_calls = [c[0][0] for c in mock_protect.call_args_list]
        self.assertIn(f_unprotected_1, protected_calls)
        self.assertIn(f_unprotected_2, protected_calls)

        # Final message must show '2 protected, 2 skipped'
        mock_show_message.assert_called_once()
        kind, title, message = mock_show_message.call_args[0][:3]
        detail = mock_show_message.call_args[1].get("detail") or (
            mock_show_message.call_args[0][3] if len(mock_show_message.call_args[0]) > 3 else None
        )
        self.assertEqual(message, "2 protected, 2 skipped")
        self.assertIn("ConfidentialDoc.docx", detail)
        self.assertIn("report.xlsx", detail)

    @patch("cli.protect_asset_cli.glass_ui.show_message")
    @patch("cli.protect_asset_cli.pin_auth.confirm_with_pin")
    @patch("cli.protect_asset_cli.glass_ui.ask_batch_classification", return_value=None)
    def test_batch_protect_cancel_dialog_exits_early(self, mock_ask_batch, mock_pin, mock_msg):
        """Cancelling the batch classification dialog exits without asking PIN."""
        protect_batch_assets(["p1.txt", "p2.txt"])
        mock_pin.assert_not_called()
        mock_msg.assert_not_called()

    @patch("cli.protect_asset_cli.ar.protect_asset")
    @patch("cli.protect_asset_cli.glass_ui.show_message")
    @patch("cli.protect_asset_cli.pin_auth.confirm_with_pin", return_value=False)
    @patch("cli.protect_asset_cli.glass_ui.ask_batch_classification", return_value={"p1.txt": "Public"})
    def test_batch_protect_cancel_pin_exits_early(self, mock_ask_batch, mock_pin, mock_msg, mock_protect):
        """Cancelling the PIN prompt exits without protecting assets."""
        protect_batch_assets(["p1.txt", "p2.txt"])
        mock_pin.assert_called_once()
        mock_protect.assert_not_called()
        mock_msg.assert_not_called()


if __name__ == "__main__":
    unittest.main()
