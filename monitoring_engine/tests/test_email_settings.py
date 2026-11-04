"""
test_email_settings.py

Targeted tests for Phase 9e:
- File value wins over environment
- Corrupt or missing file falls back
- Other keys survive a save
- Invalid addresses are rejected
- Email notifier resolves recipient dynamically at send time
"""

import json
import os
import shutil
import sys
import tempfile
import unittest
from unittest.mock import MagicMock, patch

ENGINE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if ENGINE_DIR not in sys.path:
    sys.path.insert(0, ENGINE_DIR)

from config.settings import (
    clear_notification_email_cache,
    clear_smtp_config_cache,
    get_smtp_config,
    get_smtp_settings_path,
    save_smtp_config,
    get_notification_email,
    is_valid_notification_email,
    save_notification_email,
    NOTIFICATION_EMAIL,
)
from services import email_notifier


class TestEmailSettings(unittest.TestCase):
    def setUp(self):
        clear_notification_email_cache()
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
        clear_notification_email_cache()
        if os.path.exists(self.temp_dir):
            shutil.rmtree(self.temp_dir, ignore_errors=True)

    def test_backward_compatibility_constant(self):
        """NOTIFICATION_EMAIL constant remains importable as a string."""
        self.assertIsInstance(NOTIFICATION_EMAIL, str)

    def test_file_value_wins_over_environment(self):
        """Value in admin_settings.json takes precedence over DAAMS_NOTIFICATION_EMAIL."""
        with patch.dict(os.environ, {"DAAMS_NOTIFICATION_EMAIL": "env@fallback.com"}):
            with open(self.settings_path, "w", encoding="utf-8") as f:
                json.dump({"notification_email": "admin@company.com"}, f)

            clear_notification_email_cache()
            recipient = get_notification_email(force_refresh=True)
            self.assertEqual(recipient, "admin@company.com")

    def test_missing_file_falls_back(self):
        """Missing file falls back to DAAMS_NOTIFICATION_EMAIL, then empty string."""
        if os.path.exists(self.settings_path):
            os.remove(self.settings_path)

        # Fallback to env var when present
        with patch.dict(os.environ, {"DAAMS_NOTIFICATION_EMAIL": "env@fallback.com"}):
            clear_notification_email_cache()
            self.assertEqual(get_notification_email(force_refresh=True), "env@fallback.com")

        # Fallback to empty string when env var is absent
        with patch.dict(os.environ, {}, clear=False):
            os.environ.pop("DAAMS_NOTIFICATION_EMAIL", None)
            clear_notification_email_cache()
            self.assertEqual(get_notification_email(force_refresh=True), "")

    def test_corrupt_file_falls_back(self):
        """Corrupt or malformed admin_settings.json gracefully falls back."""
        with open(self.settings_path, "w", encoding="utf-8") as f:
            f.write("{ invalid json notification_email: bad ")

        with patch.dict(os.environ, {"DAAMS_NOTIFICATION_EMAIL": "env@fallback.com"}):
            clear_notification_email_cache()
            self.assertEqual(get_notification_email(force_refresh=True), "env@fallback.com")

    def test_other_keys_survive_a_save(self):
        """Saving notification_email preserves all unrelated keys in admin_settings.json."""
        initial_data = {
            "work_start": 8,
            "work_end": 17,
            "app_theme": "dark",
            "notification_email": "old@company.com",
        }
        with open(self.settings_path, "w", encoding="utf-8") as f:
            json.dump(initial_data, f, indent=2)

        saved = save_notification_email("new_admin@company.com", path=self.settings_path)
        self.assertTrue(saved)

        with open(self.settings_path, "r", encoding="utf-8") as f:
            updated = json.load(f)

        self.assertEqual(updated["work_start"], 8)
        self.assertEqual(updated["work_end"], 17)
        self.assertEqual(updated["app_theme"], "dark")
        self.assertEqual(updated["notification_email"], "new_admin@company.com")

    def test_invalid_addresses_are_rejected(self):
        """Invalid email addresses are rejected by validation and save_notification_email."""
        invalid_candidates = [
            "",
            "plainaddress",
            "missing_at.com",
            "@nodomain.com",
            "user@nodot",
            "user@.dotstart.com",
            "user@dotend.com.",
            "user @domain.com",
            "user@ domain.com",
            "user1@domain.com,user2@domain.com",
            "user1@domain.com;user2@domain.com",
            "two@@at.com",
            "a" * 250 + "@b.com",  # Exceeds max 254 chars
        ]

        for bad in invalid_candidates:
            with self.subTest(candidate=bad):
                self.assertFalse(is_valid_notification_email(bad))
                self.assertFalse(save_notification_email(bad, path=self.settings_path))

        # Valid candidates should be accepted
        valid_candidates = [
            "admin@example.com",
            "sec_ops.alert@sub.domain.org",
            "user+tag@domain.co.uk",
        ]
        for good in valid_candidates:
            with self.subTest(candidate=good):
                self.assertTrue(is_valid_notification_email(good))

    def test_email_notifier_uses_dynamic_recipient_at_send_time(self):
        """email_notifier.send_alert_email resolves recipient from file at send time."""
        with open(self.settings_path, "w", encoding="utf-8") as f:
            json.dump({"notification_email": "dynamic_recipient@security.org"}, f)
        clear_notification_email_cache()

        fake_smtp_instance = MagicMock()
        with patch.object(email_notifier, "get_smtp_config", return_value=("sender@example.com", "dummy_pass", True)), \
             patch.object(email_notifier, "SMTP_HOST", "smtp.example.com"), \
             patch.object(email_notifier, "SMTP_PORT", 587), \
             patch("smtplib.SMTP") as mock_smtp_cls:
            mock_smtp_cls.return_value.__enter__.return_value = fake_smtp_instance

            alert = {
                "alert_id": "ALT-1234",
                "risk_level": "High",
                "asset": "C:\\Secret\\data.xlsx",
                "risk_factors": ["Outside normal working hours (+25)"],
            }
            incident = {"incident_id": "INC-5678", "status": "Open"}

            sent = email_notifier.send_alert_email(alert, incident)
            self.assertTrue(sent)

            # Verify send_message was called with an EmailMessage addressed to dynamic recipient
            self.assertEqual(fake_smtp_instance.send_message.call_count, 1)
            sent_msg = fake_smtp_instance.send_message.call_args[0][0]
            self.assertEqual(sent_msg["To"], "dynamic_recipient@security.org")


class TestSmtpSettings(unittest.TestCase):
    def setUp(self):
        clear_smtp_config_cache()
        self.temp_dir = tempfile.mkdtemp()
        self.smtp_path = os.path.join(self.temp_dir, "smtp_settings.json")
        self.env_patch = patch.dict(
            os.environ,
            {"DAAMS_SMTP_SETTINGS_PATH": self.smtp_path},
            clear=False,
        )
        self.env_patch.start()

    def tearDown(self):
        self.env_patch.stop()
        clear_smtp_config_cache()
        if os.path.exists(self.temp_dir):
            shutil.rmtree(self.temp_dir, ignore_errors=True)

    def test_encrypt_then_decrypt_roundtrip(self):
        """Encrypt-then-decrypt roundtrip preserves sender, password, and enabled state."""
        test_pw = "SampleSecretAppPassword987!"
        saved = save_smtp_config("sender@company.com", test_pw, True, path=self.smtp_path)
        self.assertTrue(saved)

        clear_smtp_config_cache()
        sender, decrypted_pw, enabled = get_smtp_config(force_refresh=True)
        self.assertEqual(sender, "sender@company.com")
        self.assertEqual(decrypted_pw, test_pw)
        self.assertTrue(enabled)

    def test_missing_file_falls_back_to_environment(self):
        """Missing smtp_settings.json falls back to DAAMS_SMTP_* and DAAMS_EMAIL_ENABLED."""
        if os.path.exists(self.smtp_path):
            os.remove(self.smtp_path)

        with patch.dict(
            os.environ,
            {
                "DAAMS_SMTP_USERNAME": "env_sender@company.com",
                "DAAMS_SMTP_PASSWORD": "env_password_secret",
                "DAAMS_EMAIL_ENABLED": "true",
            },
        ):
            clear_smtp_config_cache()
            sender, pw, enabled = get_smtp_config(force_refresh=True)
            self.assertEqual(sender, "env_sender@company.com")
            self.assertEqual(pw, "env_password_secret")
            self.assertTrue(enabled)

    def test_corrupt_file_falls_back(self):
        """Corrupt smtp_settings.json gracefully falls back to environment."""
        with open(self.smtp_path, "w", encoding="utf-8") as f:
            f.write("{ invalid json sender_email: corrupt ")

        with patch.dict(
            os.environ,
            {
                "DAAMS_SMTP_USERNAME": "env_sender@company.com",
                "DAAMS_SMTP_PASSWORD": "env_password_secret",
                "DAAMS_EMAIL_ENABLED": "true",
            },
        ):
            clear_smtp_config_cache()
            sender, pw, enabled = get_smtp_config(force_refresh=True)
            self.assertEqual(sender, "env_sender@company.com")
            self.assertEqual(pw, "env_password_secret")
            self.assertTrue(enabled)

    def test_saved_values_persist_across_simulated_restart(self):
        """Saved values persist when read by a separate fresh Python subprocess (simulating restart)."""
        test_pw = "RestartTestSecret321!"
        saved = save_smtp_config("admin@domain.com", test_pw, True, path=self.smtp_path)
        self.assertTrue(saved)

        import subprocess
        code = (
            "import os, sys\n"
            f"ENGINE_DIR = os.path.abspath(r'{ENGINE_DIR}')\n"
            "if ENGINE_DIR not in sys.path: sys.path.insert(0, ENGINE_DIR)\n"
            "from config.settings import get_smtp_config\n"
            "s, p, e = get_smtp_config(force_refresh=True)\n"
            "assert s == 'admin@domain.com'\n"
            "assert e is True\n"
            "assert len(p) > 0\n"
        )
        proc = subprocess.run(
            [sys.executable, "-c", code],
            env=dict(os.environ, DAAMS_SMTP_SETTINGS_PATH=self.smtp_path),
            capture_output=True,
            text=True,
        )
        self.assertEqual(proc.returncode, 0, f"Subprocess failed: {proc.stderr}")

    def test_plaintext_password_never_in_json_file(self):
        """The plaintext password must never appear in the written JSON file."""
        test_pw = "DefinitivePlaintextPasswordCheck999!"
        saved = save_smtp_config("sender@test.com", test_pw, True, path=self.smtp_path)
        self.assertTrue(saved)

        with open(self.smtp_path, "r", encoding="utf-8") as f:
            raw_content = f.read()

        self.assertNotIn(test_pw, raw_content)
        data = json.loads(raw_content)
        self.assertIn("password_protected", data)
        self.assertNotEqual(data["password_protected"], test_pw)
        self.assertTrue(len(data["password_protected"]) > 0)


if __name__ == "__main__":
    unittest.main()
