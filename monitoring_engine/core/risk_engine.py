"""
risk_engine.py

Phase 5: Risk Assessment & Risk Scoring Engine.

Takes a structured activity record (the Phase 1-2 format: user/device/
asset/action/timestamp) plus the protected asset's classification (from
the Phase 4 registry), and produces an explainable risk assessment:

    {
        "risk_score": 78,
        "risk_level": "Critical",
        "risk_factors": [
            "Confidential asset (+35)",
            "Copy action (+18)",
            "Outside normal working hours (+25)"
        ],
        "advisory": "Suspicious activity requiring investigation. This is
                      a behavioral risk signal, not a determination that
                      the user acted maliciously."
    }

This module is intentionally separate from monitor.py and
activity_logger.py: monitor.py only detects and reports WHAT happened;
this module decides HOW RISKY it looks; activity_logger.py decides WHERE
that gets stored. None of those three responsibilities live in the same
file, and none of them import risk-scoring logic from each other.

Honesty notes (also covered in README):
- Open, Copy and Move are now detected. Actions detected or inferred
  by the Monitoring Engine (Create, Modify, Rename, Delete, Copy, and Move)
  reach this module.
- "Create" has an explicit moderate weight in config.ACTION_WEIGHTS.
- Burst/frequency tracking is in-memory only, per user and device,
  and resets whenever the Monitoring Engine process restarts -- there is
  no persisted history of past bursts across runs. See README
  limitations.
- No authorization/session signal is included in the score. The
  Monitoring Engine has no reliable way to know whether an action was
  "authorized" -- inventing one would directly violate Phase 5 section 6
  of the brief. `session_status` is accepted as a parameter so callers
  won't need to change again once real session data exists, but it
  currently has zero effect on the score.
"""

from collections import defaultdict
from datetime import datetime
import os

from config.settings import (
    CLASSIFICATION_WEIGHTS,
    ACTION_WEIGHTS,
    WORK_HOURS_START,
    WORK_HOURS_END,
    get_work_hours,
    AFTER_HOURS_RISK_POINTS,
    ENABLE_NEAR_BOUNDARY_RISK,
    NEAR_BOUNDARY_MINUTES,
    NEAR_BOUNDARY_RISK_POINTS,
    BURST_THRESHOLD,
    BURST_WINDOW_SECONDS,
    BURST_RISK_POINTS,
    RISK_LEVEL_THRESHOLDS,
)

TIMESTAMP_FORMAT = "%Y-%m-%d %H:%M:%S"

# In-memory burst-tracking state: user@device (or fallback asset path) -> list
# of datetimes of recent relevant activity. Deliberately NOT persisted to
# disk or Firestore -- see module docstring "Honesty notes".
_recent_activity = defaultdict(list)


def _classification_risk(classification):
    """
    Returns (points, factor_label). A missing or unrecognized
    classification is treated as a safe default of 0 points, with the
    factor label explicitly saying so -- never silently guessed.
    """
    if not classification:
        return 0, "Missing classification on protected asset (default 0)"
    points = CLASSIFICATION_WEIGHTS.get(classification)
    if points is None:
        return 0, f"Unknown classification '{classification}' (default 0)"
    return points, f"{classification} asset (+{points})"


def _action_risk(action):
    """
    Returns (points, factor_label). An action with no configured weight
    in config.ACTION_WEIGHTS defaults to 0 points and is identified in
    risk_factors rather than silently guessed at.
    """
    if not action:
        return 0, "Missing action on activity record (default 0)"
    points = ACTION_WEIGHTS.get(action)
    if points is None:
        return 0, f"'{action}' action has no configured risk weight (default 0)"
    return points, f"{action} action (+{points})"


def _time_risk(timestamp_str):
    """
    Returns (points, factor_label_or_None) based on get_work_hours()
    / WORK_HOURS_END and the optional near-boundary rule. An invalid or
    unparseable timestamp safely contributes 0 (no time-based risk
    applied) rather than guessing.
    """
    test_hours = os.environ.get("DAAMS_TEST_HOURS")
    if test_hours is not None:
        try:
            test_hour = int(test_hours)
            if not 0 <= test_hour <= 23:
                raise ValueError
        except ValueError:
            return 0, "Invalid DAAMS_TEST_HOURS; time-based risk skipped"
        hour_decimal = float(test_hour)
    else:
        try:
            dt = datetime.strptime(timestamp_str, TIMESTAMP_FORMAT)
        except (TypeError, ValueError):
            return 0, "Could not parse activity timestamp; time-based risk skipped"
        hour_decimal = dt.hour + dt.minute / 60

    work_start, work_end = get_work_hours()
    if work_start < work_end:
        is_working_hours = work_start <= hour_decimal < work_end
        minutes_from_start = (hour_decimal - work_start) * 60
        minutes_from_end = (work_end - hour_decimal) * 60
    else:
        is_working_hours = hour_decimal >= work_start or hour_decimal < work_end
        if hour_decimal >= work_start:
            minutes_from_start = (hour_decimal - work_start) * 60
            minutes_from_end = (work_end + 24 - hour_decimal) * 60
        else:
            minutes_from_start = (hour_decimal + 24 - work_start) * 60
            minutes_from_end = (work_end - hour_decimal) * 60

    if not is_working_hours:
        return AFTER_HOURS_RISK_POINTS, f"Outside normal working hours (+{AFTER_HOURS_RISK_POINTS})"

    if ENABLE_NEAR_BOUNDARY_RISK:


        if minutes_from_start <= NEAR_BOUNDARY_MINUTES or minutes_from_end <= NEAR_BOUNDARY_MINUTES:
            return (
                NEAR_BOUNDARY_RISK_POINTS,
                f"Near boundary of working hours (+{NEAR_BOUNDARY_RISK_POINTS})",
            )

    return 0, None


def _burst_key(activity_record, asset_path):
    """
    Returns 'user@device' when both activity_record['user'] and
    activity_record['device'] exist, otherwise asset_path.
    """
    if activity_record and activity_record.get("user") and activity_record.get("device"):
        return f"{activity_record['user']}@{activity_record['device']}"
    return asset_path


def _burst_risk(target_key, timestamp_str):
    """
    Returns (points, factor_label_or_None). Tracks recent activity
    per user and device (or asset path fallback), in memory.
    If config.BURST_THRESHOLD or more relevant activities have occurred
    within config.BURST_WINDOW_SECONDS (including the current one),
    burst risk is applied.
    """
    try:
        now = datetime.strptime(timestamp_str, TIMESTAMP_FORMAT)
    except (TypeError, ValueError):
        return 0, None  # Can't do burst math without a valid timestamp.

    history = _recent_activity[target_key]
    history.append(now)

    cutoff = now.timestamp() - BURST_WINDOW_SECONDS
    _recent_activity[target_key] = [t for t in history if t.timestamp() >= cutoff]

    count = len(_recent_activity[target_key])
    if count >= BURST_THRESHOLD:
        return (
            BURST_RISK_POINTS,
            f"Burst activity: {count} actions by this user within "
            f"{BURST_WINDOW_SECONDS}s (+{BURST_RISK_POINTS})",
        )
    return 0, None


def _risk_level(score):
    for threshold, level in RISK_LEVEL_THRESHOLDS:
        if score >= threshold:
            return level
    return "Low"  # Unreachable given a 0-point floor entry in the config, but safe.


def assess_risk(activity_record: dict, classification: str = None, session_status: str = None) -> dict:
    """
    Compute an explainable risk assessment for a single activity record.

    Args:
        activity_record: the Phase 1-2 record dict. Needs "action" and
            "timestamp" to score anything meaningful; "user" and "device"
            are used for per-user burst tracking (with "asset" as fallback).
        classification: the protected asset's classification from the
            Phase 4 registry (Public/Sensitive/Private/Confidential), or
            None if unavailable.
        session_status: RESERVED for future authorization/session
            integration -- currently has NO effect on the score. See
            module docstring "Honesty notes".

    Returns: {risk_score, risk_level, risk_factors, advisory}.

    NEVER raises. Any internal error produces a safe, explained degraded
    result (score 0 / Low, with the error itself listed as a factor)
    instead of crashing the caller -- risk calculation errors must not
    take down the Monitoring Engine.
    """
    try:
        action = activity_record.get("action")
        timestamp = activity_record.get("timestamp")
        asset_path = activity_record.get("asset", "")

        factors = []
        score = 0

        points, label = _classification_risk(classification)
        score += points
        factors.append(label)

        points, label = _action_risk(action)
        score += points
        factors.append(label)

        points, label = _time_risk(timestamp)
        score += points
        if label:
            factors.append(label)

        points, label = _burst_risk(_burst_key(activity_record, asset_path), timestamp)
        score += points
        if label:
            factors.append(label)

        # Authorization/session risk: intentionally NOT implemented.
        # session_status is accepted but never contributes to the score
        # -- see module docstring. Nothing fabricated here.

        level = _risk_level(score)

        advisory = None
        if level in ("High", "Critical"):
            advisory = (
                "Suspicious activity requiring investigation. This is a "
                "behavioral risk signal, not a determination that the "
                "user acted maliciously."
            )

        return {
            "risk_score": score,
            "risk_level": level,
            "risk_factors": factors,
            "advisory": advisory,
        }

    except Exception as e:
        # A bug in this module must never take down detection/logging --
        # return an honest "couldn't assess" result instead of crashing.
        return {
            "risk_score": 0,
            "risk_level": "Low",
            "risk_factors": [f"Risk assessment failed unexpectedly: {e} (defaulted to Low/0)"],
            "advisory": None,
        }
