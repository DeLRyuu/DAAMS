import os
import sys

from unittest.mock import patch

import pytest

ENGINE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if ENGINE_DIR not in sys.path:
    sys.path.insert(0, ENGINE_DIR)

from core import risk_engine


@pytest.fixture(autouse=True)
def clear_burst_history():
    risk_engine._recent_activity.clear()
    yield
    risk_engine._recent_activity.clear()


def test_create_has_explicit_moderate_action_weight():
    assert risk_engine._action_risk("Create") == (8, "Create action (+8)")


def test_test_hours_override_makes_scores_reproducible():
    with patch.dict(os.environ, {"DAAMS_TEST_HOURS": "20"}):
        first = risk_engine.assess_risk(
            {"asset": "one", "action": "Create", "timestamp": "2026-10-30 10:00:00"},
            classification="Public",
        )
        second = risk_engine.assess_risk(
            {"asset": "two", "action": "Create", "timestamp": "2026-10-30 15:30:00"},
            classification="Public",
        )

    assert first["risk_score"] == second["risk_score"] == 38
    assert any("Outside normal working hours" in item for item in first["risk_factors"])


def test_invalid_test_hours_override_is_reported_without_crashing():
    with patch.dict(os.environ, {"DAAMS_TEST_HOURS": "24"}):
        result = risk_engine.assess_risk(
            {"asset": "test", "action": "Create", "timestamp": "2026-10-30 10:00:00"},
            classification="Public",
        )

    assert result["risk_score"] == 13
    assert any("Invalid DAAMS_TEST_HOURS" in item for item in result["risk_factors"])
