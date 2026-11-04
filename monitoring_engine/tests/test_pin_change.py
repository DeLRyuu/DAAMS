"""
test_pin_change.py

Targeted tests for Phase 9f-change-pin:
- Correct current PIN with matching new PINs succeeds, and new PIN verifies via pin_auth.verify_pin
- Wrong current PIN is rejected
- Mismatched new PINs are rejected
- Invalid new PIN format (non-digits, too short, too long) is rejected
- After a PIN change, the old PIN no longer verifies
"""

import hashlib
import json
import os
import secrets
import shutil
import sys
import tempfile
import unittest
from unittest.mock import patch

ENGINE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if ENGINE_DIR not in sys.path:
    sys.path.insert(0, ENGINE_DIR)

from cli import pin_auth
from core import database


def simulate_csharp_save_new_pin(
    current_pin: str,
    new_pin: str,
    confirm_pin: str,
    json_path: str,
    db_path: str,
) -> tuple[bool, str]:
    """
    Simulates the exact algorithm implemented in C# SettingsService.SaveNewPin:
    1. Loads stored hash and salt from SQLite or JSON file.
    2. Verifies current_pin using SHA256(salt + current_pin) lowercase hex.
    3. Validates new_pin matches confirm_pin, is digits only, and length 4-8.
    4. Generates 16-byte random hex salt (32 chars) and computes SHA256(salt + new_pin).
    5. Atomically writes to JSON file and upserts into SQLite security_settings table.
    """
    # 1. Load stored settings (SQLite first, then JSON file)
    stored = None
    try:
        settings_row = database.get_security_settings()
        if settings_row and settings_row.get("pin_hash"):
            stored = settings_row
    except Exception:
        pass

    if not stored and os.path.isfile(json_path):
        try:
            with open(json_path, "r", encoding="utf-8") as f:
                data = json.load(f)
            if data.get("pin_hash"):
                stored = data
        except Exception:
            pass

    # 2. Verify current PIN
    if stored:
        salt = stored.get("salt", "")
        expected_hash = stored.get("pin_hash", "")
        computed = hashlib.sha256((salt + current_pin).encode("utf-8")).hexdigest()
        if computed.lower() != expected_hash.lower():
            return False, "Incorrect current PIN"
    else:
        if current_pin != "1234":
            return False, "Incorrect current PIN"

    # 3. Validations
    if new_pin != confirm_pin:
        return False, "PINs do not match"

    if not new_pin or not new_pin.isdigit():
        return False, "PIN must contain digits only."

    if not (4 <= len(new_pin) <= 8):
        return False, "PIN must be 4-8 digits long."

    # 4. Generate salt and hash (matching C# SettingsService)
    new_salt = secrets.token_hex(16)
    new_hash = hashlib.sha256((new_salt + new_pin).encode("utf-8")).hexdigest().lower()
    now_str = "2026-11-04 12:00:00"

    record = {
        "pin_hash": new_hash,
        "salt": new_salt,
        "updated_at": now_str,
        "updated_by": "admin",
        "is_default": False,
    }

    # 5a. Atomically write to JSON
    tmp_path = json_path + ".tmp"
    with open(tmp_path, "w", encoding="utf-8") as f:
        json.dump(record, f, indent=2)
    os.replace(tmp_path, json_path)

    # 5b. Upsert into SQLite
    try:
        database.upsert_security_settings(record)
    except Exception:
        pass

    return True, "PIN changed successfully."


class TestPinChange(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.mkdtemp()
        self.db_path = os.path.join(self.temp_dir, "test_daams.db")
        self.json_path = os.path.join(self.temp_dir, "security_settings.json")

        self.env_patch = patch.dict(
            os.environ,
            {
                "DAAMS_DATABASE_PATH": self.db_path,
                "DAAMS_SECURITY_SETTINGS_PATH": self.json_path,
            },
            clear=False,
        )
        self.env_patch.start()

        database.init_db()
        self.orig_settings_file = pin_auth.SETTINGS_FILE
        pin_auth.SETTINGS_FILE = self.json_path

    def tearDown(self):
        pin_auth.SETTINGS_FILE = self.orig_settings_file
        self.env_patch.stop()
        if os.path.exists(self.temp_dir):
            shutil.rmtree(self.temp_dir, ignore_errors=True)

    def test_correct_current_pin_succeeds_and_verifies(self):
        """A correct current PIN with matching new PINs succeeds and verifies via pin_auth."""
        # Initialize default PIN (1234)
        pin_auth._ensure_initialized()
        self.assertTrue(pin_auth.verify_pin("1234"))

        # Save new PIN using C# logic
        success, msg = simulate_csharp_save_new_pin(
            "1234", "5678", "5678", self.json_path, self.db_path
        )
        self.assertTrue(success)
        self.assertEqual(msg, "PIN changed successfully.")

        # Verify via pin_auth.verify_pin
        self.assertTrue(pin_auth.verify_pin("5678"))

    def test_wrong_current_pin_rejected(self):
        """A wrong current PIN is rejected with 'Incorrect current PIN' and no change is made."""
        pin_auth._ensure_initialized()

        success, msg = simulate_csharp_save_new_pin(
            "9999", "5678", "5678", self.json_path, self.db_path
        )
        self.assertFalse(success)
        self.assertEqual(msg, "Incorrect current PIN")

        # Original PIN should still be valid, new PIN should NOT be valid
        self.assertTrue(pin_auth.verify_pin("1234"))
        self.assertFalse(pin_auth.verify_pin("5678"))

    def test_mismatched_new_pins_rejected(self):
        """Mismatched new and confirm PINs are rejected with 'PINs do not match'."""
        pin_auth._ensure_initialized()

        success, msg = simulate_csharp_save_new_pin(
            "1234", "5678", "8765", self.json_path, self.db_path
        )
        self.assertFalse(success)
        self.assertEqual(msg, "PINs do not match")

        self.assertTrue(pin_auth.verify_pin("1234"))

    def test_invalid_new_pin_format_rejected(self):
        """Invalid PIN formats (non-digits, too short, too long) are rejected with clear messages."""
        pin_auth._ensure_initialized()

        # Non-digits
        success, msg = simulate_csharp_save_new_pin(
            "1234", "abcd", "abcd", self.json_path, self.db_path
        )
        self.assertFalse(success)
        self.assertEqual(msg, "PIN must contain digits only.")

        # Too short (< 4 digits)
        success, msg = simulate_csharp_save_new_pin(
            "1234", "123", "123", self.json_path, self.db_path
        )
        self.assertFalse(success)
        self.assertEqual(msg, "PIN must be 4-8 digits long.")

        # Too long (> 8 digits)
        success, msg = simulate_csharp_save_new_pin(
            "1234", "123456789", "123456789", self.json_path, self.db_path
        )
        self.assertFalse(success)
        self.assertEqual(msg, "PIN must be 4-8 digits long.")

    def test_old_pin_no_longer_verifies_after_change(self):
        """After a successful change, the old PIN no longer verifies."""
        pin_auth._ensure_initialized()
        self.assertTrue(pin_auth.verify_pin("1234"))

        success, _ = simulate_csharp_save_new_pin(
            "1234", "8888", "8888", self.json_path, self.db_path
        )
        self.assertTrue(success)

        # New PIN verifies
        self.assertTrue(pin_auth.verify_pin("8888"))
        # Old PIN MUST NOT verify
        self.assertFalse(pin_auth.verify_pin("1234"))


if __name__ == "__main__":
    unittest.main()

