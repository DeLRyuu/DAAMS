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
    """
    tools/send_test_email.py reads its settings through get_smtp_config() (smtp_settings.json,
    then environment variables) and sends through email_notifier. These tests replace all of
    that, so they never use the real SMTP settings and NEVER send a real email.
    """

    SENDER = "sender@example.com"

    def _patched(self, enabled: bool, send_result: bool = False):
        """Patch every place the script looks up SMTP settings, and block real sending."""
        config = (self.SENDER, "secret", enabled)
        notifier = send_test_email.email_notifier
        return [
            patch.object(send_test_email, "get_smtp_config", return_value=config),
            patch.object(notifier, "get_smtp_config", return_value=config),
            patch.object(notifier, "get_notification_email", return_value="admin@example.com"),
            patch.object(notifier.smtplib, "SMTP"),
            patch.object(notifier.smtplib, "SMTP_SSL"),
            patch.object(notifier, "send_alert_email", return_value=send_result),
        ]

    def _run_main(self, enabled: bool, send_result: bool = False):
        patches = self._patched(enabled, send_result)
        mocks = [p.start() for p in patches]
        try:
            code = send_test_email.main()
        finally:
            for p in reversed(patches):
                p.stop()
        return code, mocks

    def test_send_test_email_disabled(self):
        # Email turned off: the tool must report failure (exit code 1).
        code, mocks = self._run_main(enabled=False, send_result=False)
        self.assertEqual(code, 1)

    def test_disabled_does_not_touch_smtp(self):
        # With email disabled, the real notifier must not open an SMTP connection.
        config = (self.SENDER, "secret", False)
        notifier = send_test_email.email_notifier
        with patch.object(send_test_email, "get_smtp_config", return_value=config), \
             patch.object(notifier, "get_smtp_config", return_value=config), \
             patch.object(notifier.smtplib, "SMTP") as smtp, \
             patch.object(notifier.smtplib, "SMTP_SSL") as smtp_ssl:
            code = send_test_email.main()
        self.assertEqual(code, 1)
        smtp.assert_not_called()
        smtp_ssl.assert_not_called()

    def test_send_test_email_success(self):
        # Email on and the (mocked) send succeeds: exit code 0.
        code, mocks = self._run_main(enabled=True, send_result=True)
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
