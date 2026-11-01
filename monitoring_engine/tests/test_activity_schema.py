import os
import sqlite3
import sys
import tempfile
import unittest
from unittest.mock import patch

ENGINE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if ENGINE_DIR not in sys.path:
    sys.path.insert(0, ENGINE_DIR)

from core import database
from services import activity_logger, alert_manager


class TestActivitySchema(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        self.db_path = os.path.join(self.temp_dir.name, "daams.db")
        self.db_path_patch = patch.object(database, "get_db_path", return_value=self.db_path)
        self.db_path_patch.start()
        conn = sqlite3.connect(self.db_path)
        conn.execute(
            """
            CREATE TABLE schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL,
                description TEXT NOT NULL
            )
            """
        )
        conn.execute(
            "INSERT INTO schema_migrations VALUES (1, ?, ?)",
            ("2026-01-01 00:00:00", "v1"),
        )
        conn.commit()
        conn.close()
        database.initialize_database()

    def tearDown(self):
        self.db_path_patch.stop()
        self.temp_dir.cleanup()

    def test_schema_v2_adds_path_columns_to_existing_v1_database(self):
        legacy_path = os.path.join(self.temp_dir.name, "legacy.db")
        conn = sqlite3.connect(legacy_path)
        conn.executescript(
            """
            CREATE TABLE schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL,
                description TEXT NOT NULL
            );
            INSERT INTO schema_migrations VALUES (1, '2026-01-01 00:00:00', 'v1');
            CREATE TABLE activity_logs (
                activity_id TEXT PRIMARY KEY,
                user TEXT NOT NULL,
                device TEXT NOT NULL,
                asset TEXT NOT NULL,
                action TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                classification TEXT,
                risk_score INTEGER,
                risk_level TEXT,
                risk_factors TEXT,
                advisory TEXT,
                sync_status TEXT NOT NULL DEFAULT 'PENDING',
                synced_at TEXT,
                sync_error TEXT,
                created_at TEXT NOT NULL
            );
            """
        )
        conn.execute(
            """
            INSERT INTO activity_logs
                (activity_id, user, device, asset, action, timestamp, created_at)
            VALUES (?, ?, ?, ?, ?, ?, ?)
            """,
            (
                "old",
                "admin",
                "device",
                "C:\\old.txt",
                "Modify",
                "2026-01-01 00:00:00",
                "2026-01-01 00:00:00",
            ),
        )
        conn.commit()
        conn.close()
        with patch.object(database, "get_db_path", return_value=legacy_path):
            database.initialize_database()

        conn = sqlite3.connect(legacy_path)
        try:
            columns = {row[1] for row in conn.execute("PRAGMA table_info(activity_logs)")}
            versions = {row[0] for row in conn.execute("SELECT version FROM schema_migrations")}
            row = conn.execute(
                "SELECT asset, asset_path, protected_root FROM activity_logs WHERE activity_id = 'old'"
            ).fetchone()
        finally:
            conn.close()

        self.assertIn("asset_path", columns)
        self.assertIn("protected_root", columns)
        self.assertIn(2, versions)
        self.assertEqual(row, ("C:\\old.txt", None, None))

    def test_save_activity_persists_asset_path_and_protected_root(self):
        record = {
            "user": "admin",
            "device": "device",
            "asset": "C:\\old.txt -> C:\\new.txt",
            "action": "Move",
            "timestamp": "2026-10-30 12:00:00",
            "asset_path": "C:\\new.txt",
            "protected_root": "C:\\protected",
        }

        database.save_activity(record)

        saved = database.get_all_activities(limit=1)[0]
        self.assertEqual(saved["asset_path"], "C:\\new.txt")
        self.assertEqual(saved["protected_root"], "C:\\protected")

    def test_duplicate_action_and_paths_within_one_second_are_ignored(self):
        first = {
            "user": "admin",
            "device": "device",
            "asset": "C:\\old.txt -> C:\\new.txt",
            "action": "Move",
            "timestamp": "2026-10-30 12:00:00",
        }
        duplicate = {
            **first,
            "timestamp": "2026-10-30 12:00:01",
        }
        database.save_activity(first)
        result = database.save_activity(duplicate)

        self.assertTrue(result.get("_deduplicated"))
        self.assertEqual(len(database.get_all_activities()), 1)

    def test_deduplication_does_not_merge_other_paths_actions_or_later_records(self):
        base = {
            "user": "admin",
            "device": "device",
            "asset": "C:\\old.txt -> C:\\new.txt",
            "action": "Move",
            "timestamp": "2026-10-30 12:00:00",
        }
        database.save_activity({**base})
        records = [
            {**base, "asset": "C:\\other.txt -> C:\\new.txt"},
            {**base, "action": "Copy"},
            {**base, "timestamp": "2026-10-30 12:00:02"},
        ]

        for record in records:
            database.save_activity(record)

        self.assertEqual(len(database.get_all_activities()), 4)

    def test_record_activity_skips_side_effects_for_duplicate(self):
        record = {
            "user": "admin",
            "device": "device",
            "asset": "C:\\old.txt -> C:\\new.txt",
            "action": "Move",
            "timestamp": "2026-10-30 12:00:00",
        }
        with patch.object(activity_logger, "build_activity_record", return_value=record.copy()), \
                patch.object(activity_logger, "_assess_risk_safely", return_value={
                    "risk_score": 0, "risk_level": "Low",
                    "risk_factors": [], "advisory": None,
                }), patch.object(activity_logger, "append_to_local_log") as append_log, \
                patch.object(activity_logger.sync_service, "trigger_sync") as sync, \
                patch.object(activity_logger, "display_activity") as display, \
                patch.object(activity_logger.alert_manager, "process_activity") as alert:
            activity_logger.record_activity(
                "C:\\old.txt -> C:\\new.txt", "Move", protected_root="C:\\protected"
            )
            result = activity_logger.record_activity(
                "C:\\old.txt -> C:\\new.txt", "Move", protected_root="C:\\protected"
            )

        self.assertNotIn("_deduplicated", result)
        append_log.assert_called_once()
        sync.assert_called_once()
        display.assert_called_once()
        alert.assert_called_once()

    def test_forced_missing_asset_alert_is_created_even_at_low_risk(self):
        record = {
            "activity_id": "ACT-test",
            "user": "admin",
            "device": "device",
            "asset": "C:\\missing\\protected.txt",
            "action": "Delete",
            "timestamp": "2026-10-30 12:00:00",
            "risk_score": 23,
            "risk_level": "Low",
            "risk_factors": ["Protected asset missing"],
        }
        alert_manager._recent_alert_keys.clear()
        with patch.object(alert_manager, "_generate_alert_id", return_value="ALERT-test"), \
                patch.object(alert_manager.database, "save_security_alert") as save, \
                patch.object(alert_manager, "_append_alert_locally"), \
                patch.object(alert_manager.sync_service, "trigger_sync"), \
                patch.object(alert_manager, "_display_alert"), \
                patch.object(alert_manager.incident_manager, "create_incident_from_alert", return_value=None), \
                patch.object(alert_manager.email_notifier, "send_alert_email"):
            alert = alert_manager.process_activity(
                record, classification="Sensitive", force=True
            )

        self.assertIsNotNone(alert)
        self.assertEqual(alert["risk_level"], "Low")
        save.assert_called_once()

    def test_engine_heartbeat_and_protected_folder_relocation_persist(self):
        heartbeat = database.save_engine_heartbeat("started")
        self.assertEqual(database.get_engine_heartbeat(), heartbeat)

        old_path = os.path.join(self.temp_dir.name, "old-folder")
        new_path = os.path.join(self.temp_dir.name, "new-folder")
        self.assertTrue(database.upsert_protected_asset({
            "path": old_path,
            "asset_name": "old-folder",
            "asset_type": "folder",
            "classification": "Private",
            "protected_at": "2026-10-30 12:00:00",
            "protected_by": "admin",
            "status": "protected",
        }))

        self.assertTrue(database.relocate_protected_asset(old_path, new_path))
        relocated = database.get_protected_asset(new_path)
        self.assertEqual(relocated["path"], new_path)
        self.assertEqual(relocated["classification"], "Private")
        self.assertEqual(database.get_protected_asset(old_path)["status"], "unprotected")

        stopped = database.save_engine_heartbeat("stopped")
        self.assertEqual(database.get_engine_heartbeat(), stopped)


if __name__ == "__main__":
    unittest.main()
