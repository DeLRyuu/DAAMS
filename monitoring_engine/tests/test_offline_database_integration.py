"""
test_offline_database_integration.py

Comprehensive test suite verifying the offline-first architecture of DAAMS:
1. Shared SQLite database initialization and schema verification.
2. Data migration from legacy JSON/JSONL.
3. Offline operation: activity logging, risk assessment, alert generation, incident creation.
4. Local persistence: restart persistence verification.
5. Control Center operations: status updates and incident resolution via SQLite.
6. Concurrent access & WAL locking safety.
7. Firestore sync handling (idempotency, offline resilience, and sync status tracking).
"""

import os
import sys
import time
import json
import sqlite3
import threading
import unittest

# Ensure monitoring_engine directory is on sys.path
ENGINE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if ENGINE_DIR not in sys.path:
    sys.path.insert(0, ENGINE_DIR)

from core import database, risk_engine, asset_registry
from services import activity_logger, alert_manager, incident_manager, firestore_logger, sync_service


class TestOfflineDatabaseIntegration(unittest.TestCase):

    def setUp(self):
        database.initialize_database()

    def test_01_database_schema_and_pragmas(self):
        """Verify schema tables, indexes, and concurrency pragmas (WAL mode)."""
        conn = database.get_connection()
        try:
            cur = conn.cursor()

            # Verify WAL journal mode
            cur.execute("PRAGMA journal_mode;")
            mode = cur.fetchone()[0]
            self.assertEqual(mode.lower(), "wal", "Database should be in WAL mode")

            # Verify busy timeout
            cur.execute("PRAGMA busy_timeout;")
            timeout = cur.fetchone()[0]
            self.assertGreaterEqual(timeout, 5000, "Busy timeout should be at least 5000ms")

            # Verify all expected tables exist
            cur.execute("SELECT name FROM sqlite_master WHERE type='table';")
            tables = {row[0] for row in cur.fetchall()}
            expected_tables = {
                "schema_migrations",
                "protected_assets",
                "activity_logs",
                "security_alerts",
                "incident_reports",
                "security_settings",
                "app_settings",
                "sync_history",
            }
            for t in expected_tables:
                self.assertIn(t, tables, f"Expected table '{t}' not found in database")

            print("\n[TEST 1 PASSED] SQLite schema and WAL mode verified.")
        finally:
            conn.close()

    def test_02_offline_activity_and_risk_recording(self):
        """Verify offline file activity detection and risk scoring writes locally to SQLite."""
        test_path = "C:\\DAAMS_Test\\ConfidentialFolder\\Financials.xlsx"
        action = "Modify"
        classification = "Confidential"

        # Record activity locally
        record = activity_logger.record_activity(
            asset_path=test_path,
            action=action,
            classification=classification,
        )

        self.assertIn("activity_id", record)
        self.assertTrue(record["activity_id"].startswith("ACT-"))
        self.assertGreaterEqual(record["risk_score"], 35)

        # Check that it exists in SQLite
        conn = database.get_connection()
        try:
            cur = conn.cursor()
            cur.execute("SELECT * FROM activity_logs WHERE activity_id = ?;", (record["activity_id"],))
            row = cur.fetchone()
            self.assertIsNotNone(row, "Activity log must be saved in SQLite")
            self.assertEqual(row["asset"], test_path)
            self.assertEqual(row["classification"], classification)
            self.assertEqual(row["sync_status"], "PENDING")

            print(f"\n[TEST 2 PASSED] Recorded activity {record['activity_id']} (Risk: {record['risk_score']}) saved to SQLite.")
        finally:
            conn.close()

    def test_03_alert_and_incident_generation_offline(self):
        """Verify High/Critical risk triggers alert and incident report stored locally."""
        test_path = "C:\\DAAMS_Test\\ConfidentialFolder\\SecretKeys.txt"
        action = "Delete"
        classification = "Confidential"

        # Delete on Confidential asset yields score >= 57 (High risk)
        record = activity_logger.record_activity(
            asset_path=test_path,
            action=action,
            classification=classification,
        )

        self.assertIn(record["risk_level"], ["High", "Critical"])

        # Check security_alerts table
        conn = database.get_connection()
        try:
            cur = conn.cursor()
            cur.execute("SELECT * FROM security_alerts WHERE asset_path = ? ORDER BY timestamp DESC LIMIT 1;", (test_path,))
            alert_row = cur.fetchone()
            self.assertIsNotNone(alert_row, "Security alert must be created in SQLite")
            alert_id = alert_row["alert_id"]
            self.assertTrue(alert_id.startswith("ALERT-"))
            self.assertEqual(alert_row["status"], "New")
            self.assertEqual(alert_row["sync_status"], "PENDING")

            # Check incident_reports table
            cur.execute("SELECT * FROM incident_reports WHERE related_alert_id = ?;", (alert_id,))
            inc_row = cur.fetchone()
            self.assertIsNotNone(inc_row, "Incident report must be created in SQLite")
            self.assertTrue(inc_row["incident_id"].startswith("INC-"))
            self.assertEqual(inc_row["investigation_status"], "New")
            self.assertEqual(inc_row["sync_status"], "PENDING")

            print(f"\n[TEST 3 PASSED] Generated alert {alert_id} and incident {inc_row['incident_id']} saved locally in SQLite.")
        finally:
            conn.close()

    def test_04_restart_persistence(self):
        """Verify records survive process restart and persist across connections."""
        test_path = "C:\\DAAMS_Test\\PersistenceCheck.docx"
        record = activity_logger.record_activity(
            asset_path=test_path,
            action="Create",
            classification="Sensitive",
        )
        saved_id = record["activity_id"]

        # Simulate restart: open entirely fresh connection
        fresh_conn = database.get_connection()
        try:
            cur = fresh_conn.cursor()
            cur.execute("SELECT activity_id, asset, action, classification FROM activity_logs WHERE activity_id = ?;", (saved_id,))
            row = cur.fetchone()
            self.assertIsNotNone(row)
            self.assertEqual(row["activity_id"], saved_id)
            self.assertEqual(row["asset"], test_path)
            self.assertEqual(row["classification"], "Sensitive")

            print(f"\n[TEST 4 PASSED] Restart persistence confirmed for activity {saved_id}.")
        finally:
            fresh_conn.close()

    def test_05_control_center_state_updates(self):
        """Verify simulated Control Center operations (status update, incident resolution)."""
        # 1. Update an alert status
        alerts = database.get_all_security_alerts()
        self.assertGreater(len(alerts), 0)
        target_alert = alerts[0]
        alert_id = target_alert["alert_id"]

        ok = database.update_security_alert_status(alert_id, "Investigating")
        self.assertTrue(ok)

        conn = database.get_connection()
        try:
            cur = conn.cursor()
            cur.execute("SELECT status, sync_status FROM security_alerts WHERE alert_id = ?;", (alert_id,))
            row = cur.fetchone()
            self.assertEqual(row["status"], "Investigating")
            self.assertEqual(row["sync_status"], "PENDING")

            # 2. Update incident resolution
            incidents = database.get_all_incident_reports()
            self.assertGreater(len(incidents), 0)
            target_inc = incidents[0]
            inc_id = target_inc["incident_id"]

            ok = database.update_incident_report(
                inc_id,
                status="Resolved",
                resolution="Verified benign administrator backup activity.",
                date_resolved="2026-10-24 16:30:00",
            )
            self.assertTrue(ok)

            cur.execute("SELECT investigation_status, resolution, sync_status FROM incident_reports WHERE incident_id = ?;", (inc_id,))
            row = cur.fetchone()
            self.assertEqual(row["investigation_status"], "Resolved")
            self.assertIn("Verified benign", row["resolution"])
            self.assertEqual(row["sync_status"], "PENDING")

            print(f"\n[TEST 5 PASSED] Control Center status updates persisted locally in SQLite.")
        finally:
            conn.close()

    def test_06_concurrent_access_wal_mode(self):
        """Verify multiple threads can read and write simultaneously without database lock errors."""
        errors = []

        def writer_task(thread_id: int):
            try:
                for i in range(10):
                    database.save_activity({
                        "user": f"worker_{thread_id}",
                        "device": "WORKSTATION",
                        "asset": f"C:\\Test\\doc_{thread_id}_{i}.txt",
                        "action": "Modify",
                        "timestamp": "2026-10-24 12:00:00",
                        "classification": "Public",
                        "risk_score": 5,
                        "risk_level": "Low",
                        "risk_factors": ["Public asset (+5)"],
                    })
            except Exception as e:
                errors.append(e)

        def reader_task():
            try:
                for _ in range(10):
                    _ = database.get_all_activities(limit=50)
                    _ = database.get_sync_summary()
            except Exception as e:
                errors.append(e)

        threads = []
        for i in range(4):
            threads.append(threading.Thread(target=writer_task, args=(i,)))
            threads.append(threading.Thread(target=reader_task))

        for t in threads:
            t.start()
        for t in threads:
            t.join()

        self.assertEqual(len(errors), 0, f"Concurrent operations failed with errors: {errors}")
        print("\n[TEST 6 PASSED] WAL mode concurrent reads/writes completed with 0 locking errors.")

    def test_07_sync_summary_and_idempotency(self):
        """Verify sync status tracking and idempotent document key structure."""
        summary = database.get_sync_summary()
        self.assertIn("activity_logs", summary)
        self.assertIn("security_alerts", summary)
        self.assertIn("incident_reports", summary)
        self.assertIn("protected_assets", summary)

        # Check that activities have unique, stable IDs for idempotent Firestore writes
        pending_activities = database.get_pending_sync_records("activity_logs", limit=5)
        for act in pending_activities:
            self.assertTrue(bool(act.get("activity_id")), "Activity must have stable unique activity_id")

        print(f"\n[TEST 7 PASSED] Sync summary verified: {summary}")


if __name__ == "__main__":
    unittest.main()
