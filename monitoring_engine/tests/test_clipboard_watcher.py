"""
test_clipboard_watcher.py

Unit tests for clipboard copy and cut detection.
"""

import unittest
from core.clipboard_watcher import ClipboardWatcher, find_covering_asset


class TestClipboardWatcher(unittest.TestCase):
    def test_path_matching(self):
        """1. Path matching: exact match on a protected file."""
        assets = [
            {"path": r"C:\Data\secret.txt", "asset_type": "file", "classification": "Confidential"}
        ]
        asset = find_covering_asset(r"C:\Data\secret.txt", assets)
        self.assertIsNotNone(asset)
        self.assertEqual(asset["path"], r"C:\Data\secret.txt")

    def test_boundary_test2_vs_test22(self):
        """2. Boundary 'Test 2' vs 'Test 22': boundary is backslash, no false prefix matches."""
        assets = [
            {"path": r"C:\Test 2", "asset_type": "folder", "classification": "Restricted"}
        ]
        self.assertIsNotNone(find_covering_asset(r"C:\Test 2\file.txt", assets))
        self.assertIsNone(find_covering_asset(r"C:\Test 22\file.txt", assets))

    def test_case_insensitive(self):
        """3. Case-insensitive: Windows paths match regardless of letter case."""
        assets = [
            {"path": r"C:\Projects\Alpha\specs.docx", "asset_type": "file", "classification": "Internal"}
        ]
        self.assertIsNotNone(find_covering_asset(r"c:\projects\alpha\SPECS.DOCX", assets))
        folder_assets = [
            {"path": r"C:\PROJECTS\ALPHA", "asset_type": "folder", "classification": "Internal"}
        ]
        self.assertIsNotNone(find_covering_asset(r"c:\projects\alpha\notes.txt", folder_assets))

    def test_longest_asset_wins(self):
        """4. Longest asset wins: most specific (longest) asset is selected."""
        assets = [
            {"path": r"C:\Vault", "asset_type": "folder", "classification": "Internal"},
            {"path": r"C:\Vault\TopSecret", "asset_type": "folder", "classification": "Confidential"},
        ]
        asset = find_covering_asset(r"C:\Vault\TopSecret\doc.pdf", assets)
        self.assertIsNotNone(asset)
        self.assertEqual(asset["path"], r"C:\Vault\TopSecret")
        self.assertEqual(asset["classification"], "Confidential")

    def test_folder_contents_match(self):
        """5. Folder contents match: any file inside a protected folder matches."""
        assets = [
            {"path": r"C:\ProtectedFolder", "asset_type": "folder", "classification": "Restricted"}
        ]
        asset = find_covering_asset(r"C:\ProtectedFolder\SubDir\data.csv", assets)
        self.assertIsNotNone(asset)
        self.assertEqual(asset["path"], r"C:\ProtectedFolder")

    def test_copy_records_copy(self):
        """6. Copy records Copy: drop effect 1 records action='Copy'."""
        recorded = []
        assets = [{"path": r"C:\Safe\doc.txt", "asset_type": "file", "classification": "Confidential"}]

        seq = [10]

        def fake_seq():
            return seq[0]

        def fake_read():
            return ([r"C:\Safe\doc.txt"], 1)

        def fake_record(**kwargs):
            recorded.append(kwargs)

        watcher = ClipboardWatcher(
            get_assets=lambda: assets,
            read=fake_read,
            sequence=fake_seq,
            record=fake_record,
        )
        watcher.poll()
        self.assertEqual(len(recorded), 0)

        seq[0] = 11
        watcher.poll()
        self.assertEqual(len(recorded), 1)
        self.assertEqual(recorded[0]["action"], "Copy")
        self.assertEqual(recorded[0]["asset_path"], r"C:\Safe\doc.txt")
        self.assertEqual(recorded[0]["classification"], "Confidential")
        self.assertEqual(
            recorded[0]["detection_note"],
            "Detection: clipboard, reported at copy or cut time, before any paste",
        )

    def test_cut_records_move(self):
        """7. Cut records Move: drop effect with bit 2 (e.g. 2) records action='Move'."""
        recorded = []
        assets = [{"path": r"C:\Safe\doc.txt", "asset_type": "file", "classification": "Confidential"}]

        seq = [20]

        def fake_seq():
            return seq[0]

        def fake_read():
            return ([r"C:\Safe\doc.txt"], 2)

        def fake_record(**kwargs):
            recorded.append(kwargs)

        watcher = ClipboardWatcher(
            get_assets=lambda: assets,
            read=fake_read,
            sequence=fake_seq,
            record=fake_record,
        )
        watcher.poll()
        self.assertEqual(len(recorded), 0)

        seq[0] = 21
        watcher.poll()
        self.assertEqual(len(recorded), 1)
        self.assertEqual(recorded[0]["action"], "Move")
        self.assertEqual(recorded[0]["asset_path"], r"C:\Safe\doc.txt")
        self.assertEqual(recorded[0]["classification"], "Confidential")

    def test_unprotected_path_records_nothing(self):
        """8. Unprotected path records nothing: files outside protected assets are ignored."""
        recorded = []
        assets = [{"path": r"C:\Safe", "asset_type": "folder", "classification": "Restricted"}]

        seq = [30]

        def fake_seq():
            return seq[0]

        def fake_read():
            return ([r"C:\Other\unrelated.txt"], 1)

        def fake_record(**kwargs):
            recorded.append(kwargs)

        watcher = ClipboardWatcher(
            get_assets=lambda: assets,
            read=fake_read,
            sequence=fake_seq,
            record=fake_record,
        )
        watcher.poll()
        seq[0] = 31
        watcher.poll()
        self.assertEqual(len(recorded), 0)

    def test_same_clipboard_sequence_records_once(self):
        """9. Same clipboard sequence records once: duplicate polls do not re-record."""
        recorded = []
        assets = [{"path": r"C:\Safe\doc.txt", "asset_type": "file", "classification": "Confidential"}]

        seq = [40]

        def fake_seq():
            return seq[0]

        def fake_read():
            return ([r"C:\Safe\doc.txt"], 1)

        def fake_record(**kwargs):
            recorded.append(kwargs)

        watcher = ClipboardWatcher(
            get_assets=lambda: assets,
            read=fake_read,
            sequence=fake_seq,
            record=fake_record,
        )
        watcher.poll()
        seq[0] = 41
        watcher.poll()
        self.assertEqual(len(recorded), 1)

        watcher.poll()
        watcher.poll()
        self.assertEqual(len(recorded), 1)

    def test_content_at_start_is_ignored(self):
        """10. Content at start is ignored: initial clipboard state is not treated as a new event."""
        recorded = []
        assets = [{"path": r"C:\Safe\doc.txt", "asset_type": "file", "classification": "Confidential"}]

        seq = [50]

        def fake_seq():
            return seq[0]

        def fake_read():
            return ([r"C:\Safe\doc.txt"], 1)

        def fake_record(**kwargs):
            recorded.append(kwargs)

        watcher = ClipboardWatcher(
            get_assets=lambda: assets,
            read=fake_read,
            sequence=fake_seq,
            record=fake_record,
        )
        watcher.poll()
        self.assertEqual(len(recorded), 0)


if __name__ == "__main__":
    unittest.main()
