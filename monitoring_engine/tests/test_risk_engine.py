import os
import sys
import unittest
from unittest.mock import patch

ENGINE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if ENGINE_DIR not in sys.path:
    sys.path.insert(0, ENGINE_DIR)

from core import risk_engine


class TestRiskEngine(unittest.TestCase):
    def setUp(self):
        risk_engine._recent_activity.clear()
        # These tests assume the default 07:00-19:00 working hours. Without this patch the
        # risk engine reads the REAL data/admin_settings.json (whatever work hours the admin
        # saved in the Control Center), so the scores change whenever those hours change.
        self.work_hours_patch = patch.object(risk_engine, "get_work_hours", return_value=(7, 19))
        self.work_hours_patch.start()

    def tearDown(self):
        self.work_hours_patch.stop()
        risk_engine._recent_activity.clear()

    def test_create_has_explicit_moderate_action_weight(self):
        self.assertEqual(
            risk_engine._action_risk("Create"),
            (8, "Create action (+8)"),
        )

    def test_test_hours_override_makes_scores_reproducible(self):
        with patch.dict(os.environ, {"DAAMS_TEST_HOURS": "20"}):
            first = risk_engine.assess_risk(
                {"asset": "one", "action": "Create", "timestamp": "2026-10-30 10:00:00"},
                classification="Public",
            )
            second = risk_engine.assess_risk(
                {"asset": "two", "action": "Create", "timestamp": "2026-10-30 15:30:00"},
                classification="Public",
            )

        self.assertEqual(first["risk_score"], 38)
        self.assertEqual(second["risk_score"], 38)
        self.assertTrue(any("Outside normal working hours" in item for item in first["risk_factors"]))

    def test_invalid_test_hours_override_is_reported_without_crashing(self):
        with patch.dict(os.environ, {"DAAMS_TEST_HOURS": "24"}):
            result = risk_engine.assess_risk(
                {"asset": "test", "action": "Create", "timestamp": "2026-10-30 10:00:00"},
                classification="Public",
            )

        self.assertEqual(result["risk_score"], 13)
        self.assertTrue(any("Invalid DAAMS_TEST_HOURS" in item for item in result["risk_factors"]))

    def test_burst_scoring_across_different_files_same_user(self):
        """Three Copy records on three different files by the same user within 3s give the burst factor on the 3rd."""
        with patch.dict(os.environ, {"DAAMS_TEST_HOURS": "10"}):
            user = "alice"
            device = "WORKSTATION-1"
            classification = "Sensitive"

            r1 = risk_engine.assess_risk(
                {
                    "user": user,
                    "device": device,
                    "asset": r"C:\Data\file1.txt",
                    "action": "Copy",
                    "timestamp": "2026-11-01 10:00:00",
                },
                classification=classification,
            )
            self.assertFalse(any("Burst activity" in factor for factor in r1["risk_factors"]))

            r2 = risk_engine.assess_risk(
                {
                    "user": user,
                    "device": device,
                    "asset": r"C:\Data\file2.txt",
                    "action": "Copy",
                    "timestamp": "2026-11-01 10:00:01",
                },
                classification=classification,
            )
            self.assertFalse(any("Burst activity" in factor for factor in r2["risk_factors"]))

            r3 = risk_engine.assess_risk(
                {
                    "user": user,
                    "device": device,
                    "asset": r"C:\Data\file3.txt",
                    "action": "Copy",
                    "timestamp": "2026-11-01 10:00:02",
                },
                classification=classification,
            )
            burst_factors = [f for f in r3["risk_factors"] if "Burst activity" in f]
            self.assertEqual(len(burst_factors), 1)
            self.assertEqual(
                burst_factors[0],
                f"Burst activity: 3 actions by this user within {risk_engine.BURST_WINDOW_SECONDS}s (+{risk_engine.BURST_RISK_POINTS})",
            )
            # Sensitive (+15) + Copy (+18) + Burst (+20) = 53
            self.assertEqual(r3["risk_score"], 53)

    def test_different_users_do_not_share_burst_counter(self):
        """Two different users do not share a counter."""
        with patch.dict(os.environ, {"DAAMS_TEST_HOURS": "10"}):
            # User A performs 2 actions
            for i in range(2):
                res = risk_engine.assess_risk(
                    {
                        "user": "alice",
                        "device": "WORKSTATION-1",
                        "asset": f"C:\\Data\\alice_{i}.txt",
                        "action": "Copy",
                        "timestamp": f"2026-11-01 10:00:0{i}",
                    },
                    classification="Sensitive",
                )
                self.assertFalse(any("Burst activity" in factor for factor in res["risk_factors"]))

            # User B performs 2 actions
            for i in range(2):
                res = risk_engine.assess_risk(
                    {
                        "user": "bob",
                        "device": "WORKSTATION-2",
                        "asset": f"C:\\Data\\bob_{i}.txt",
                        "action": "Copy",
                        "timestamp": f"2026-11-01 10:00:0{i+2}",
                    },
                    classification="Sensitive",
                )
                self.assertFalse(any("Burst activity" in factor for factor in res["risk_factors"]))

    def test_burst_key_helper(self):
        """_burst_key returns user@device when present, otherwise asset_path."""
        self.assertEqual(
            risk_engine._burst_key({"user": "alice", "device": "PC-1"}, "C:\\test.txt"),
            "alice@PC-1",
        )
        self.assertEqual(
            risk_engine._burst_key({"user": "alice"}, "C:\\test.txt"),
            "C:\\test.txt",
        )
        self.assertEqual(
            risk_engine._burst_key({"device": "PC-1"}, "C:\\test.txt"),
            "C:\\test.txt",
        )
        self.assertEqual(
            risk_engine._burst_key({}, "C:\\test.txt"),
            "C:\\test.txt",
        )
        self.assertEqual(
            risk_engine._burst_key(None, "C:\\test.txt"),
            "C:\\test.txt",
        )


if __name__ == "__main__":
    unittest.main()
