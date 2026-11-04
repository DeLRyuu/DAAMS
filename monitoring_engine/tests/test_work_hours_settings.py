"""
test_work_hours_settings.py

Targeted tests for Phase 9c:
- Hours read from the shared data/admin_settings.json file
- Bad or missing file falls back to DAAMS_WORK_START / DAAMS_WORK_END, then 7 and 19
- Invalid hours rejected (out of 0-24 range, start == end, non-numeric, boolean)
- File value wins over environment variables
- Other keys survive a save (atomic update preserving existing JSON keys)
- Risk engine dynamic assessment at assessment time
"""

import json
import os
import shutil
import sys
import tempfile

ENGINE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if ENGINE_DIR not in sys.path:
    sys.path.insert(0, ENGINE_DIR)
import unittest
from unittest.mock import patch

from config.settings import (
    get_work_hours,
    clear_work_hours_cache,
    save_work_hours,
    WORK_HOURS_START,
    WORK_HOURS_END,
)
from core.risk_engine import assess_risk


class TestWorkHoursSettings(unittest.TestCase):
    def setUp(self):
        clear_work_hours_cache()
        self.temp_dir = tempfile.mkdtemp()
        self.settings_path = os.path.join(self.temp_dir, "admin_settings.json")
        self.env_patch = patch.dict(
            os.environ,
            {"DAAMS_ADMIN_SETTINGS_PATH": self.settings_path},
            clear=False,
        )
        self.env_patch.start()

    def tearDown(self):
        self.env_patch.stop()
        clear_work_hours_cache()
        if os.path.exists(self.temp_dir):
            shutil.rmtree(self.temp_dir, ignore_errors=True)

    def test_backward_compatibility_constants(self):
        """WORK_HOURS_START and WORK_HOURS_END constants remain importable and numeric."""
        self.assertIsInstance(WORK_HOURS_START, int)
        self.assertIsInstance(WORK_HOURS_END, int)

    def test_hours_read_from_file(self):
        """Hours are read accurately from admin_settings.json."""
        with open(self.settings_path, "w", encoding="utf-8") as f:
            json.dump({"work_start": 8, "work_end": 17}, f)

        clear_work_hours_cache()
        start, end = get_work_hours(force_refresh=True)
        self.assertEqual(start, 8)
        self.assertEqual(end, 17)

    def test_missing_file_falls_back(self):
        """Missing admin_settings.json falls back to DAAMS_WORK_START/END, then 7 and 19."""
        if os.path.exists(self.settings_path):
            os.remove(self.settings_path)

        # Without env vars: falls back to (7, 19)
        with patch.dict(os.environ, {}, clear=False):
            os.environ.pop("DAAMS_WORK_START", None)
            os.environ.pop("DAAMS_WORK_END", None)
            clear_work_hours_cache()
            start, end = get_work_hours(force_refresh=True)
            self.assertEqual(start, 7)
            self.assertEqual(end, 19)

        # With env vars: falls back to env vars
        with patch.dict(os.environ, {"DAAMS_WORK_START": "9", "DAAMS_WORK_END": "18"}):
            clear_work_hours_cache()
            start, end = get_work_hours(force_refresh=True)
            self.assertEqual(start, 9)
            self.assertEqual(end, 18)

    def test_corrupt_file_falls_back(self):
        """Corrupt or malformed admin_settings.json gracefully falls back."""
        with open(self.settings_path, "w", encoding="utf-8") as f:
            f.write("{ invalid json work_start: 9 ")

        with patch.dict(os.environ, {}, clear=False):
            os.environ.pop("DAAMS_WORK_START", None)
            os.environ.pop("DAAMS_WORK_END", None)
            clear_work_hours_cache()
            start, end = get_work_hours(force_refresh=True)
            self.assertEqual(start, 7)
            self.assertEqual(end, 19)

    def test_invalid_hours_rejected(self):
        """Negative hours, >24, start == end, non-numeric and boolean values are rejected."""
        # start == end
        with open(self.settings_path, "w", encoding="utf-8") as f:
            json.dump({"work_start": 8, "work_end": 8}, f)
        clear_work_hours_cache()
        self.assertEqual(get_work_hours(force_refresh=True), (7, 19))

        # out of bounds (<0 or >24)
        with open(self.settings_path, "w", encoding="utf-8") as f:
            json.dump({"work_start": -5, "work_end": 28}, f)
        clear_work_hours_cache()
        self.assertEqual(get_work_hours(force_refresh=True), (7, 19))

        # boolean values (JSON true/false) must not be treated as 1/0
        with open(self.settings_path, "w", encoding="utf-8") as f:
            json.dump({"work_start": True, "work_end": False}, f)
        clear_work_hours_cache()
        self.assertEqual(get_work_hours(force_refresh=True), (7, 19))

        # strings that cannot be parsed
        with open(self.settings_path, "w", encoding="utf-8") as f:
            json.dump({"work_start": "morning", "work_end": "night"}, f)
        clear_work_hours_cache()
        self.assertEqual(get_work_hours(force_refresh=True), (7, 19))

    def test_file_value_wins_over_environment(self):
        """Values in admin_settings.json take precedence over DAAMS_WORK_START/END env vars."""
        with patch.dict(os.environ, {"DAAMS_WORK_START": "6", "DAAMS_WORK_END": "15"}):
            with open(self.settings_path, "w", encoding="utf-8") as f:
                json.dump({"work_start": 9, "work_end": 18}, f)

            clear_work_hours_cache()
            start, end = get_work_hours(force_refresh=True)
            self.assertEqual(start, 9)
            self.assertEqual(end, 18)

    def test_other_keys_survive_a_save(self):
        """Saving work hours preserves any unrelated keys in admin_settings.json."""
        initial_data = {
            "app_theme": "dark",
            "audit_retention_days": 90,
            "work_start": 8,
            "work_end": 17,
        }
        with open(self.settings_path, "w", encoding="utf-8") as f:
            json.dump(initial_data, f, indent=2)

        saved = save_work_hours(10, 20, path=self.settings_path)
        self.assertTrue(saved)

        with open(self.settings_path, "r", encoding="utf-8") as f:
            updated = json.load(f)

        self.assertEqual(updated["app_theme"], "dark")
        self.assertEqual(updated["audit_retention_days"], 90)
        self.assertEqual(updated["work_start"], 10)
        self.assertEqual(updated["work_end"], 20)

    def test_save_work_hours_rejects_invalid_inputs(self):
        """save_work_hours rejects invalid start/end hours without modifying the file."""
        saved_same = save_work_hours(8, 8, path=self.settings_path)
        self.assertFalse(saved_same)

        saved_out_of_range = save_work_hours(-1, 20, path=self.settings_path)
        self.assertFalse(saved_out_of_range)

    def test_risk_engine_reflects_work_hours_at_assessment_time(self):
        """risk_engine.assess_risk evaluates time risk based on current work hours from file."""
        # Configure work hours 10 to 18
        with open(self.settings_path, "w", encoding="utf-8") as f:
            json.dump({"work_start": 10, "work_end": 18}, f)
        clear_work_hours_cache()

        # Activity record at 08:30 (outside 10..18)
        record = {
            "timestamp": "2026-11-04 08:30:00",
            "action": "Open",
            "classification": "Sensitive",
            "user": "alice",
            "device": "PC-1",
        }

        # Clear DAAMS_TEST_HOURS so it parses record timestamp
        with patch.dict(os.environ, {}, clear=False):
            os.environ.pop("DAAMS_TEST_HOURS", None)
            result = assess_risk(record, "C:\\test\\doc.txt")
            self.assertTrue(
                any("Outside normal working hours" in factor for factor in result["risk_factors"]),
                "Expected outside normal working hours risk factor",
            )

        # Update work hours to 08 to 18
        with open(self.settings_path, "w", encoding="utf-8") as f:
            json.dump({"work_start": 8, "work_end": 18}, f)
        clear_work_hours_cache()

        with patch.dict(os.environ, {}, clear=False):
            os.environ.pop("DAAMS_TEST_HOURS", None)
            result2 = assess_risk(record, "C:\\test\\doc.txt")
            self.assertFalse(
                any("Outside normal working hours" in factor for factor in result2["risk_factors"]),
                "Expected activity at 08:30 to be within 08:00 - 18:00",
            )


if __name__ == "__main__":
    unittest.main()
