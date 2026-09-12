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
- Only actions the Monitoring Engine actually detects (Create, Modify,
  Rename, Delete as of Phase 1-4) ever reach this module in practice.
  Open/Copy/Move weights exist in config.ACTION_WEIGHTS because the
  Phase 5 brief specifies them, but they will never be applied to a real
  activity until a future phase adds real detection for those actions --
  this module does not fabricate activity that wasn't detected.
- "Create" is NOT in the brief's action-weight table, even though it IS
  one of the four actions this system actually detects today. Rather
  than inventing an unofficial weight, it defaults to 0 points and is
  flagged by name in risk_factors every time, so this gap stays visible
  instead of silently guessed at.
- Burst/frequency tracking is in-memory only, per protected asset path,
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

from config import (
    CLASSIFICATION_WEIGHTS,
    ACTION_WEIGHTS,
    WORK_HOURS_START,
    WORK_HOURS_END,
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

# In-memory burst-tracking state: asset path -> list of datetimes of
# recent relevant activity on that asset. Deliberately NOT persisted to
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
    in config.ACTION_WEIGHTS (notably "Create" -- see module docstring)
    defaults to 0 points rather than an invented number.
    """
    if not action:
        return 0, "Missing action on activity record (default 0)"
    points = ACTION_WEIGHTS.get(action)
    if points is None:
        return 0, f"'{action}' action has no configured risk weight (default 0)"
    return points, f"{action} action (+{points})"


def _time_risk(timestamp_str):
    """
    Returns (points, factor_label_or_None) based on config.WORK_HOURS_START
    / WORK_HOURS_END and the optional near-boundary rule. An invalid or
    unparseable timestamp safely contributes 0 (no time-based risk
    applied) rather than guessing.
    """
    try:
        dt = datetime.strptime(timestamp_str, TIMESTAMP_FORMAT)
    except (TypeError, ValueError):
        return 0, "Could not parse activity timestamp; time-based risk skipped"

    hour_decimal = dt.hour + dt.minute / 60

    if not (WORK_HOURS_START <= hour_decimal < WORK_HOURS_END):
        return AFTER_HOURS_RISK_POINTS, f"Outside normal working hours (+{AFTER_HOURS_RISK_POINTS})"

    if ENABLE_NEAR_BOUNDARY_RISK:
        minutes_from_start = (hour_decimal - WORK_HOURS_START) * 60
        minutes_from_end = (WORK_HOURS_END - hour_decimal) * 60
        if minutes_from_start <= NEAR_BOUNDARY_MINUTES or minutes_from_end <= NEAR_BOUNDARY_MINUTES:
            return (
                NEAR_BOUNDARY_RISK_POINTS,
                f"Near boundary of working hours (+{NEAR_BOUNDARY_RISK_POINTS})",
            )

    return 0, None


def _burst_risk(asset_path, timestamp_str):
    """
    Returns (points, factor_label_or_None). Tracks recent activity
    per-asset, in memory. If config.BURST_THRESHOLD or more relevant
    activities have occurred on this SAME asset within
    config.BURST_WINDOW_SECONDS (including the current one), burst risk
    is applied.
    """
    try:
        now = datetime.strptime(timestamp_str, TIMESTAMP_FORMAT)
    except (TypeError, ValueError):
        return 0, None  # Can't do burst math without a valid timestamp.

    history = _recent_activity[asset_path]
    history.append(now)

    cutoff = now.timestamp() - BURST_WINDOW_SECONDS
    _recent_activity[asset_path] = [t for t in history if t.timestamp() >= cutoff]

    count = len(_recent_activity[asset_path])
    if count >= BURST_THRESHOLD:
        return (
            BURST_RISK_POINTS,
            f"Burst activity: {count} actions on this asset within "
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
            "timestamp" to score anything meaningful; "asset" is used
            (as a plain lookup key) for burst tracking.
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

        points, label = _burst_risk(asset_path, timestamp)
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
