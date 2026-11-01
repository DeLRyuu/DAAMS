"""
test_protect_already_protected.py

Unit tests verifying that protect_asset_cli:
1. Detects when a path itself is already protected.
2. Detects when a path is covered by a protected folder.
3. Allows unprotected paths and paths with status 'unprotected' to continue normally.
4. Shows friendly messages without asking for a PIN or revealing risk numbers.
"""

import sys
import unittest
from unittest.mock import MagicMock, patch

from cli.protect_asset_cli import check_already_protected, main


class TestProtectAlreadyProtected(unittest.TestCase):
    def setUp(self):
        self.fake_protected_file = {
            "path": r"C:\Company\ConfidentialDoc.docx",
            "asset_name": "ConfidentialDoc.docx",
            "asset_type": "file",
            "classification": "Confidential",
            "status": "protected",
        }
        self.fake_protected_folder = {
            "path": r"C:\Company\HR_Dept",
            "asset_name": "HR_Dept",
            "asset_type": "folder",
            "classification": "Sensitive",
            "status": "protected",
        }
        self.fake_unprotected_file = {
            "path": r"C:\Company\ArchivedNotes.txt",
            "asset_name": "ArchivedNotes.txt",
            "asset_type": "file",
            "classification": "Public",
            "status": "unprotected",
        }
        self.fakes = [
            self.fake_protected_file,
            self.fake_protected_folder,
            self.fake_unprotected_file,
        ]

    def test_protected_path_detected_as_self(self):
        """A path matching an actively protected file returns ('self', record)."""
        kind, record = check_already_protected(r"C:\Company\ConfidentialDoc.docx", self.fakes)
        self.assertEqual(kind, "self")
        self.assertIsNotNone(record)
        self.assertEqual(record["classification"], "Confidential")

    def test_protected_folder_detected_as_self(self):
        """A path matching an actively protected folder returns ('self', record)."""
        kind, record = check_already_protected(r"C:\Company\HR_Dept", self.fakes)
        self.assertEqual(kind, "self")
        self.assertIsNotNone(record)
        self.assertEqual(record["classification"], "Sensitive")

    def test_path_inside_protected_folder_detected_as_folder(self):
        """A file inside a protected folder returns ('folder', parent_folder_record)."""
        kind, record = check_already_protected(r"C:\Company\HR_Dept\sub\employee.xlsx", self.fakes)
        self.assertEqual(kind, "folder")
        self.assertIsNotNone(record)
        self.assertEqual(record["asset_name"], "HR_Dept")
        self.assertEqual(record["classification"], "Sensitive")

    def test_unprotected_path_returns_none(self):
        """An unknown path not in the registry returns (None, None)."""
        kind, record = check_already_protected(r"C:\Other\Random.txt", self.fakes)
        self.assertIsNone(kind)
        self.assertIsNone(record)

    def test_unprotected_status_returns_none(self):
        """A path with status='unprotected' (not in a protected folder) returns (None, None)."""
        kind, record = check_already_protected(r"C:\Company\ArchivedNotes.txt", self.fakes)
        self.assertIsNone(kind)
        self.assertIsNone(record)

    @patch("cli.protect_asset_cli.pin_auth.confirm_with_pin")
    @patch("cli.protect_asset_cli.glass_ui.show_message")
    @patch("cli.protect_asset_cli.os.path.isdir", return_value=False)
    @patch("cli.protect_asset_cli.check_already_protected")
    def test_main_already_protected_file_message_and_no_pin(
        self, mock_check, mock_isdir, mock_show_message, mock_pin
    ):
        """When path itself is protected, show message and exit without asking for PIN."""
        mock_check.return_value = ("self", self.fake_protected_file)
        with patch.object(sys, "argv", ["protect_asset_cli.py", r"C:\Company\ConfidentialDoc.docx"]):
            main()

        mock_show_message.assert_called_once_with(
            "info",
            "Already protected",
            "This file is already protected as Confidential.",
        )
        mock_pin.assert_not_called()

        # Verify no risk numbers, weights, or '+'
        msg = mock_show_message.call_args[0][2]
        self.assertFalse(any(ch.isdigit() for ch in msg), f"Message contains digits: {msg}")
        self.assertNotIn("+", msg)
        self.assertNotIn("risk", msg.lower())

    @patch("cli.protect_asset_cli.pin_auth.confirm_with_pin")
    @patch("cli.protect_asset_cli.glass_ui.show_message")
    @patch("cli.protect_asset_cli.os.path.isdir", return_value=True)
    @patch("cli.protect_asset_cli.check_already_protected")
    def test_main_already_protected_folder_message_and_no_pin(
        self, mock_check, mock_isdir, mock_show_message, mock_pin
    ):
        """When folder itself is protected, show folder wording and exit without asking for PIN."""
        mock_check.return_value = ("self", self.fake_protected_folder)
        with patch.object(sys, "argv", ["protect_asset_cli.py", r"C:\Company\HR_Dept"]):
            main()

        mock_show_message.assert_called_once_with(
            "info",
            "Already protected",
            "This folder is already protected as Sensitive.",
        )
        mock_pin.assert_not_called()

    @patch("cli.protect_asset_cli.pin_auth.confirm_with_pin")
    @patch("cli.protect_asset_cli.glass_ui.show_message")
    @patch("cli.protect_asset_cli.os.path.isdir", return_value=False)
    @patch("cli.protect_asset_cli.check_already_protected")
    def test_main_inside_protected_folder_message_and_no_pin(
        self, mock_check, mock_isdir, mock_show_message, mock_pin
    ):
        """When inside a protected folder, show covering message and exit without asking for PIN."""
        mock_check.return_value = ("folder", self.fake_protected_folder)
        with patch.object(sys, "argv", ["protect_asset_cli.py", r"C:\Company\HR_Dept\sub\file.txt"]):
            main()

        mock_show_message.assert_called_once_with(
            "info",
            "Already protected",
            "This file is already covered by the protected folder HR_Dept (Sensitive).",
        )
        mock_pin.assert_not_called()

        # Verify no risk numbers, weights, or '+'
        msg = mock_show_message.call_args[0][2]
        self.assertFalse(any(ch.isdigit() for ch in msg), f"Message contains digits: {msg}")
        self.assertNotIn("+", msg)
        self.assertNotIn("risk", msg.lower())

    @patch("cli.protect_asset_cli.pin_auth.confirm_with_pin", return_value=False)
    @patch("cli.protect_asset_cli.glass_ui.show_message")
    @patch("cli.protect_asset_cli.os.path.isdir", return_value=False)
    @patch("cli.protect_asset_cli.check_already_protected")
    def test_main_unprotected_continues_to_pin(
        self, mock_check, mock_isdir, mock_show_message, mock_pin
    ):
        """When path is unprotected, it does NOT show 'Already protected' and proceeds to PIN."""
        mock_check.return_value = (None, None)
        with patch.object(sys, "argv", ["protect_asset_cli.py", r"C:\Other\Doc.txt"]):
            main()

        mock_show_message.assert_not_called()
        mock_pin.assert_called_once_with("protect this file")

    @patch("cli.protect_asset_cli.pin_auth.confirm_with_pin", return_value=False)
    @patch("cli.protect_asset_cli.glass_ui.show_message")
    @patch("cli.protect_asset_cli.os.path.isdir", return_value=False)
    @patch("cli.protect_asset_cli.check_already_protected")
    def test_main_status_unprotected_continues_to_pin(
        self, mock_check, mock_isdir, mock_show_message, mock_pin
    ):
        """When path has status='unprotected', it proceeds normally to PIN."""
        mock_check.return_value = (None, None)
        with patch.object(sys, "argv", ["protect_asset_cli.py", r"C:\Company\ArchivedNotes.txt"]):
            main()

        mock_show_message.assert_not_called()
        mock_pin.assert_called_once_with("protect this file")


if __name__ == "__main__":
    unittest.main()
