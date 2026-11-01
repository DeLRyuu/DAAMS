"""
test_protect_dialog_labels.py

Unit tests verifying that classification choice labels do not reveal
numeric risk weights, '+' signs, or the word 'risk' to the administrator.
"""

import unittest

from cli.protect_asset_cli import CLASSIFICATION_DESCRIPTIONS
from ui.glass_ui import DEFAULT_CLASSIFICATION_DESCRIPTIONS


class TestProtectDialogLabels(unittest.TestCase):
    def test_protect_cli_descriptions_hide_risk_and_scores(self):
        """Verify protect_asset_cli descriptions contain no digits, no '+', and no 'risk'."""
        self.assertEqual(len(CLASSIFICATION_DESCRIPTIONS), 4)
        for name, desc in CLASSIFICATION_DESCRIPTIONS.items():
            self.assertFalse(
                any(ch.isdigit() for ch in desc),
                f"Description for '{name}' contains digits: {desc}",
            )
            self.assertNotIn("+", desc, f"Description for '{name}' contains '+': {desc}")
            self.assertNotIn("risk", desc.lower(), f"Description for '{name}' contains 'risk': {desc}")

    def test_glass_ui_descriptions_hide_risk_and_scores(self):
        """Verify glass_ui default descriptions contain no digits, no '+', and no 'risk'."""
        self.assertEqual(len(DEFAULT_CLASSIFICATION_DESCRIPTIONS), 4)
        for name, desc in DEFAULT_CLASSIFICATION_DESCRIPTIONS.items():
            self.assertFalse(
                any(ch.isdigit() for ch in desc),
                f"Description for '{name}' contains digits: {desc}",
            )
            self.assertNotIn("+", desc, f"Description for '{name}' contains '+': {desc}")
            self.assertNotIn("risk", desc.lower(), f"Description for '{name}' contains 'risk': {desc}")


if __name__ == "__main__":
    unittest.main()
