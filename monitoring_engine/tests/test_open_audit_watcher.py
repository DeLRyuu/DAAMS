"""
test_open_audit_watcher.py

Unit tests for Windows audit log Open (read) event detection.
"""

import unittest
from core.open_audit_watcher import OpenAuditWatcher


class TestOpenAuditWatcher(unittest.TestCase):
    def test_read_of_protected_file_records_open(self):
        """A read of a protected file records Open."""
        recorded = []
        assets = [
            {"path": r"C:\Data\secret.txt", "asset_type": "file", "classification": "Confidential"}
        ]

        def fake_read(seconds=5):
            return [
                (
                    101,
                    {
                        "ObjectName": r"C:\Data\secret.txt",
                        "AccessMask": "0x1",
                        "ProcessName": r"C:\Windows\System32\notepad.exe",
                    },
                )
            ]

        def fake_record(**kwargs):
            recorded.append(kwargs)

        watcher = OpenAuditWatcher(
            get_assets=lambda: assets,
            record=fake_record,
            read=fake_read,
        )
        watcher.check_once()

        self.assertEqual(len(recorded), 1)
        self.assertEqual(recorded[0]["action"], "Open")
        self.assertEqual(recorded[0]["asset_path"], r"C:\Data\secret.txt")
        self.assertEqual(recorded[0]["classification"], "Confidential")
        self.assertEqual(
            recorded[0]["detection_note"],
            "Detection: Windows audit log (Event 4663), opened by notepad.exe",
        )

    def test_write_only_access_mask_does_not_record(self):
        """A write-only AccessMask does not record Open."""
        recorded = []
        assets = [
            {"path": r"C:\Data\secret.txt", "asset_type": "file", "classification": "Confidential"}
        ]

        def fake_read(seconds=5):
            return [
                (
                    102,
                    {
                        "ObjectName": r"C:\Data\secret.txt",
                        "AccessMask": "0x2",  # WriteData only, no 0x1 bit
                        "ProcessName": r"C:\Windows\System32\notepad.exe",
                    },
                )
            ]

        watcher = OpenAuditWatcher(
            get_assets=lambda: assets,
            record=lambda **kwargs: recorded.append(kwargs),
            read=fake_read,
        )
        watcher.check_once()
        self.assertEqual(len(recorded), 0)

    def test_ignored_process_does_not_record(self):
        """An ignored process does not record Open."""
        recorded = []
        assets = [
            {"path": r"C:\Data\secret.txt", "asset_type": "file", "classification": "Confidential"}
        ]

        def fake_read(seconds=5):
            return [
                (
                    103,
                    {
                        "ObjectName": r"C:\Data\secret.txt",
                        "AccessMask": "0x1",
                        "ProcessName": r"C:\Windows\explorer.exe",
                    },
                ),
                (
                    104,
                    {
                        "ObjectName": r"C:\Data\secret.txt",
                        "AccessMask": "0x1",
                        "ProcessName": r"C:\Python314\python.exe",
                    },
                ),
            ]

        watcher = OpenAuditWatcher(
            get_assets=lambda: assets,
            record=lambda **kwargs: recorded.append(kwargs),
            read=fake_read,
        )
        watcher.check_once()
        self.assertEqual(len(recorded), 0)

    def test_searchprotocolhost_does_not_record(self):
        """Windows Search protocol host does not record Open."""
        recorded = []
        assets = [
            {"path": r"C:\Data\secret.txt", "asset_type": "file", "classification": "Confidential"}
        ]

        def fake_read(seconds=5):
            return [
                (
                    109,
                    {
                        "ObjectName": r"C:\Data\secret.txt",
                        "AccessMask": "0x1",
                        "ProcessName": r"C:\Windows\System32\searchprotocolhost.exe",
                    },
                )
            ]

        watcher = OpenAuditWatcher(
            get_assets=lambda: assets,
            record=lambda **kwargs: recorded.append(kwargs),
            read=fake_read,
        )
        watcher.check_once()
        self.assertEqual(len(recorded), 0)

    def test_searchfilterhost_does_not_record(self):
        """Windows Search filter host does not record Open."""
        recorded = []
        assets = [
            {"path": r"C:\Data\secret.txt", "asset_type": "file", "classification": "Confidential"}
        ]

        def fake_read(seconds=5):
            return [
                (
                    110,
                    {
                        "ObjectName": r"C:\Data\secret.txt",
                        "AccessMask": "0x1",
                        "ProcessName": r"C:\Windows\System32\searchfilterhost.exe",
                    },
                )
            ]

        watcher = OpenAuditWatcher(
            get_assets=lambda: assets,
            record=lambda **kwargs: recorded.append(kwargs),
            read=fake_read,
        )
        watcher.check_once()
        self.assertEqual(len(recorded), 0)

    def test_notepad_still_records_open(self):
        """Notepad still records Open."""
        recorded = []
        assets = [
            {"path": r"C:\Data\secret.txt", "asset_type": "file", "classification": "Confidential"}
        ]

        def fake_read(seconds=5):
            return [
                (
                    111,
                    {
                        "ObjectName": r"C:\Data\secret.txt",
                        "AccessMask": "0x1",
                        "ProcessName": r"C:\Windows\System32\notepad.exe",
                    },
                )
            ]

        watcher = OpenAuditWatcher(
            get_assets=lambda: assets,
            record=lambda **kwargs: recorded.append(kwargs),
            read=fake_read,
        )
        watcher.check_once()
        self.assertEqual(len(recorded), 1)
        self.assertEqual(recorded[0]["action"], "Open")

    def test_unprotected_path_does_not_record(self):
        """An unprotected path does not record Open."""
        recorded = []
        assets = [
            {"path": r"C:\Data\secret.txt", "asset_type": "file", "classification": "Confidential"}
        ]

        def fake_read(seconds=5):
            return [
                (
                    105,
                    {
                        "ObjectName": r"C:\Other\unrelated.txt",
                        "AccessMask": "0x1",
                        "ProcessName": r"C:\Windows\System32\notepad.exe",
                    },
                )
            ]

        watcher = OpenAuditWatcher(
            get_assets=lambda: assets,
            record=lambda **kwargs: recorded.append(kwargs),
            read=fake_read,
        )
        watcher.check_once()
        self.assertEqual(len(recorded), 0)

    def test_two_reads_within_two_seconds_record_once(self):
        """Two reads of the same file within 2 seconds record once."""
        recorded = []
        assets = [
            {"path": r"C:\Data\secret.txt", "asset_type": "file", "classification": "Confidential"}
        ]

        events = [
            (
                106,
                {
                    "ObjectName": r"C:\Data\secret.txt",
                    "AccessMask": "0x1",
                    "ProcessName": r"C:\Windows\System32\notepad.exe",
                },
            ),
            (
                107,
                {
                    "ObjectName": r"C:\Data\secret.txt",
                    "AccessMask": "0x1",
                    "ProcessName": r"C:\Windows\System32\wordpad.exe",
                },
            ),
        ]

        watcher = OpenAuditWatcher(
            get_assets=lambda: assets,
            record=lambda **kwargs: recorded.append(kwargs),
            read=lambda seconds=5: events,
        )
        watcher.check_once()
        self.assertEqual(len(recorded), 1)
        self.assertEqual(recorded[0]["action"], "Open")

    def test_file_inside_protected_folder_records(self):
        """A file inside a protected folder records Open."""
        recorded = []
        assets = [
            {"path": r"C:\Vault", "asset_type": "folder", "classification": "Restricted"}
        ]

        def fake_read(seconds=5):
            return [
                (
                    108,
                    {
                        "ObjectName": r"C:\Vault\SubFolder\document.docx",
                        "AccessMask": "0x120089",  # includes 0x1 bit
                        "ProcessName": r"C:\Program Files\Microsoft Office\WINWORD.EXE",
                    },
                )
            ]

        watcher = OpenAuditWatcher(
            get_assets=lambda: assets,
            record=lambda **kwargs: recorded.append(kwargs),
            read=fake_read,
        )
        watcher.check_once()

        self.assertEqual(len(recorded), 1)
        self.assertEqual(recorded[0]["action"], "Open")
        self.assertEqual(recorded[0]["asset_path"], r"C:\Vault\SubFolder\document.docx")
        self.assertEqual(recorded[0]["classification"], "Restricted")
        self.assertEqual(
            recorded[0]["detection_note"],
            "Detection: Windows audit log (Event 4663), opened by winword.exe",
        )


if __name__ == "__main__":
    unittest.main()
