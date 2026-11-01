"""
test_demo_config.py -- Unit tests for Phase 6 demo configuration:
- Work hours configuration from environment (DAAMS_WORK_START, DAAMS_WORK_END)
- Email test tool (tools/send_test_email.py)
- Demo data preparation tool (tools/prepare_demo.py)
"""

import os
import unittest
from unittest.mock import patch

from config import settings
from tools import prepare_demo, send_test_email


class TestWorkHoursConfig(unittest.TestCase):
    def test_parse_work_hour_defaults(self):
        with patch.dict(os.environ, {}, clear=True):
            self.assertEqual(settings._parse_work_hour("DAAMS_WORK_START", 7), 7)
            self.assertEqual(settings._parse_work_hour("DAAMS_WORK_END", 19), 19)

    def test_parse_work_hour_valid_values(self):
        with patch.dict(os.environ, {"DAAMS_WORK_START": "8", "DAAMS_WORK_END": "17"}):
            self.assertEqual(settings._parse_work_hour("DAAMS_WORK_START", 7), 8)
            self.assertEqual(settings._parse_work_hour("DAAMS_WORK_END", 19), 17)

    def test_parse_work_hour_boundary_values(self):
        with patch.dict(os.environ, {"DAAMS_WORK_START": "0", "DAAMS_WORK_END": "24"}):
            self.assertEqual(settings._parse_work_hour("DAAMS_WORK_START", 7), 0)
            self.assertEqual(settings._parse_work_hour("DAAMS_WORK_END", 19), 24)

    def test_parse_work_hour_invalid_values(self):
        invalid_cases = ["-1", "25", "100", "abc", "eight", "7:00", "", "   "]
        for bad_val in invalid_cases:
            with patch.dict(os.environ, {"DAAMS_WORK_START": bad_val, "DAAMS_WORK_END": bad_val}):
                self.assertEqual(
                    settings._parse_work_hour("DAAMS_WORK_START", 7),
                    7,
                    f"Failed for bad start hour: {bad_val}",
                )
                self.assertEqual(
                    settings._parse_work_hour("DAAMS_WORK_END", 19),
                    19,
                    f"Failed for bad end hour: {bad_val}",
                )


class TestSendTestEmail(unittest.TestCase):
    def test_send_test_email_disabled(self):
        with patch.object(send_test_email, "EMAIL_ENABLED", False):
            code = send_test_email.main()
            self.assertEqual(code, 1)

    def test_send_test_email_success(self):
        with patch.object(send_test_email, "EMAIL_ENABLED", True), \
             patch.object(send_test_email, "SMTP_HOST", "smtp.example.com"), \
             patch.object(send_test_email, "NOTIFICATION_EMAIL", "admin@example.com"), \
             patch.object(send_test_email.email_notifier, "send_alert_email", return_value=True):
            code = send_test_email.main()
            self.assertEqual(code, 0)


class TestPrepareDemo(unittest.TestCase):
    def test_pin_settings_strictly_preserved(self):
        self.assertNotIn("security_settings", prepare_demo.TABLES_TO_CLEAR)
        self.assertIn("security_settings", prepare_demo.PRESERVED_TABLES)

    def test_tables_to_clear_contains_expected(self):
        expected = {"activity_logs", "security_alerts", "incident_reports", "protected_assets"}
        self.assertTrue(expected.issubset(set(prepare_demo.TABLES_TO_CLEAR)))

    def test_demo_assets_configuration(self):
        self.assertEqual(prepare_demo.DEMO_ROOT, r"C:\DAAMS_Demo")
        self.assertEqual(len(prepare_demo.DEMO_FILES), 4)
        for fname, content in prepare_demo.DEMO_FILES.items():
            self.assertTrue(fname.endswith(".txt"))
            self.assertGreater(len(content.strip()), 0)
        self.assertTrue(prepare_demo.DEMO_SUBFOLDER.endswith("Subfolder_for_testing"))

    def test_dry_run_execution(self):
        with patch.object(prepare_demo, "check_running_processes", return_value=[]):
            with patch("sys.argv", ["prepare_demo.py"]):
                code = prepare_demo.main()
                self.assertEqual(code, 0)

    def test_refuse_if_process_running(self):
        with patch.object(prepare_demo, "check_running_processes", return_value=["DAAMS Control Center"]):
            code = prepare_demo.main()
            self.assertEqual(code, 1)


if __name__ == "__main__":
    unittest.main()
