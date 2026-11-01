"""
config.py

Configuration for the DAAMS Python Monitoring Engine (Phase 1-4).

Phase 3 adds Firestore configuration. No credential VALUES live in this
file -- only a path to a locally-stored, gitignored service-account JSON
file (or an environment variable override). See README.md for setup.

Phase 4 change: monitoring is no longer driven by a single hard-coded
PROTECTED_PATH. Instead, the Monitoring Engine reads the Protected Asset
Registry (see asset_registry.py), which is populated via the Windows
right-click context menu (or, later, the WPF admin console). The
PROTECTED_PATH variable from Phases 1-3 has been removed -- to monitor
something, right-click it in File Explorer and choose
DAAMS -> Protect This File/Folder.
"""

import os

ENGINE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DATA_DIR = os.path.join(ENGINE_DIR, "data")
LOGS_DIR = os.path.join(ENGINE_DIR, "logs")

# ---------------------------------------------------------------------------
# PROTECTED ASSET SAFETY RULES (used by asset_registry.py and monitor.py)
# ---------------------------------------------------------------------------
# Path values that are explicitly disallowed because they represent
# "monitor everything" rather than a specific protected asset. No asset
# may ever be registered or monitored at one of these paths.
DISALLOWED_ROOTS = {
    "c:\\", "d:\\", "e:\\", "f:\\",
    "/", "c:/", "d:/", "e:/", "f:/",
}


def is_disallowed_root(path: str) -> bool:
    """
    Return True if the given path looks like a drive root or filesystem root,
    which DAAMS must never monitor (whole-disk monitoring is prohibited).
    """
    if not path:
        return False
    normalized = os.path.normpath(path).lower()
    # Covers "C:\\" -> normalized as "c:\\" and similar
    return normalized in DISALLOWED_ROOTS or normalized in {"c:", "d:", "e:", "f:"}


# ---------------------------------------------------------------------------
# LIVE REGISTRY RELOAD (Phase 4.1)
# ---------------------------------------------------------------------------
# How often (in seconds) the running Monitoring Engine re-checks the
# Protected Asset Registry for changes made via the context menu (or later,
# the WPF console) -- protect, reclassify, or remove protection -- without
# needing to restart main.py. This is a simple periodic re-scan of a small
# local JSON file, not a queue or retry system, so it stays well within
# "keep this phase simple" while closing the "changes don't show up until
# restart" gap.
REGISTRY_RELOAD_INTERVAL_SECONDS = 3
FILE_WATCH_RECURSIVE = True
DISK_SCAN_INTERVAL_SECONDS = 180
ENGINE_HEARTBEAT_INTERVAL_SECONDS = 60

# ---------------------------------------------------------------------------
# FIRESTORE CONFIGURATION (Phase 3, extended in Phase 4)
# ---------------------------------------------------------------------------
# Set to False to turn Firestore uploads off entirely. Local logging
# (Phase 2) and the local Protected Asset Registry (Phase 4) are both
# completely unaffected either way.
ENABLE_FIRESTORE = True

# Name of the Firestore collection that stores activity records.
FIRESTORE_COLLECTION = "activity_logs"

# Name of the Firestore collection that stores protected asset records (Phase 4).
PROTECTED_ASSETS_COLLECTION = "protected_assets"

# Path to the Firebase service-account JSON credential file.
#
# SECURITY: this file must NEVER be committed to source control, and its
# contents must never be pasted directly into this (or any) source file.
#
# Resolution order:
#   1. If the environment variable DAAMS_FIREBASE_CREDENTIALS is set,
#      that path is used (recommended for shared/deployed setups).
#   2. Otherwise, defaults to "serviceAccountKey.json" in the engine
#      root -- a filename already covered by .gitignore.
#
# See README.md "Secure Credential Setup" for how to obtain and place
# this file.
FIRESTORE_CREDENTIALS_PATH = os.environ.get(
    "DAAMS_FIREBASE_CREDENTIALS",
    os.path.join(ENGINE_DIR, "serviceAccountKey.json"),
)

# ---------------------------------------------------------------------------
# PIN CONFIRMATION (Phase 4.2)
# ---------------------------------------------------------------------------
# Protect / Change Classification / Remove Protection all require the
# admin to enter this PIN before the action takes effect -- see
# pin_auth.py. The PIN itself is NEVER stored in plaintext (only a salted
# hash, in security_settings.json / Firestore); DEFAULT_PIN below is only
# the STARTING value used the very first time DAAMS runs, before any
# admin has chosen their own PIN.
#
# SECURITY: change this immediately after first use (via change_pin_cli.py)
# -- DAAMS will keep warning you on every PIN-gated action until you do.
# Also note this value is public (it's sitting in this source file) --
# it exists only so the system is usable out of the box, never treat it
# as a real secret.
DEFAULT_PIN = "1234"
# Set False for demos that rely on the default PIN.
REQUIRE_PIN_CHANGE_ON_DEFAULT = True

# Name of the Firestore collection that mirrors the PIN configuration.
# Always contains exactly one document -- see pin_auth.py. Stores only a
# salted hash, never the PIN itself.
SECURITY_SETTINGS_COLLECTION = "security_settings"

# ---------------------------------------------------------------------------
# RISK ASSESSMENT (Phase 5) -- see risk_engine.py
# ---------------------------------------------------------------------------
# Base risk contribution from the protected asset's classification.
CLASSIFICATION_WEIGHTS = {
    "Public": 5,
    "Sensitive": 15,
    "Private": 25,
    "Confidential": 35,
}

# Base risk contribution from the detected action.
#
# Create receives a moderate weight (8), matching Modify: both introduce
# or change data within a protected scope, without implying deletion risk.
ACTION_WEIGHTS = {
    "Open": 2,
    "Create": 8,
    "Modify": 8,
    "Rename": 6,
    "Copy": 18,
    "Move": 14,
    "Delete": 22,
}

# Normal working hours, 24-hour clock. WORK_HOURS_START is inclusive,
# WORK_HOURS_END is exclusive (so 19 means "up to but not including 7:00 PM").
WORK_HOURS_START = 7
WORK_HOURS_END = 19
AFTER_HOURS_RISK_POINTS = 25

# Smaller risk contribution for activity that falls just inside working
# hours but close to the boundary (e.g. 7:05 AM) -- optional and
# configurable per the brief. Set ENABLE_NEAR_BOUNDARY_RISK = False to
# disable this entirely.
ENABLE_NEAR_BOUNDARY_RISK = True
NEAR_BOUNDARY_MINUTES = 30
NEAR_BOUNDARY_RISK_POINTS = 8

# Burst/frequency risk: if this many (or more) relevant activities happen
# on the SAME protected asset within this many seconds, add burst risk.
# Tracked in-memory only -- see risk_engine.py module docstring.
BURST_THRESHOLD = 3
BURST_WINDOW_SECONDS = 60
BURST_RISK_POINTS = 20

# Risk level thresholds, checked highest-first. A score of exactly a
# threshold value belongs to that level (e.g. 75 -> Critical, 74 -> High).
RISK_LEVEL_THRESHOLDS = [
    (75, "Critical"),
    (50, "High"),
    (25, "Medium"),
    (0, "Low"),
]

# ---------------------------------------------------------------------------
# SECURITY ALERTS (Phase 6) -- see alert_manager.py
# ---------------------------------------------------------------------------
# Risk levels that generate a Security Alert. Per the brief: Low and
# Medium never alert; High and Critical always do. This does NOT
# recalculate risk -- it only reads the risk_level Phase 5 already
# produced.
ALERT_RISK_LEVELS = {"High", "Critical"}

# Name of the Firestore collection that stores security alerts.
SECURITY_ALERTS_COLLECTION = "security_alerts"

# ---------------------------------------------------------------------------
# INCIDENT REPORTS (Phase 7) -- see incident_manager.py
# ---------------------------------------------------------------------------
# Name of the Firestore collection that stores incident reports. Every
# incident is created for (and references) a Security Alert that has
# already been generated -- see alert_manager.py's ALERT_RISK_LEVELS.
# There is no separate threshold here: an incident is created whenever an
# alert is.
INCIDENT_REPORTS_COLLECTION = "incident_reports"

# ---------------------------------------------------------------------------
# EMAIL NOTIFICATIONS (Phase 7) -- see email_notifier.py
# ---------------------------------------------------------------------------
# All values come from environment variables ONLY -- never hardcoded,
# never read from a file this project owns. This mirrors the same
# "credentials never live in source" approach already used for Firestore
# (FIRESTORE_CREDENTIALS_PATH) and the DAAMS PIN. See README.md "Email
# Setup" for how to set these on Windows (PowerShell $env:... or a
# permanent System Environment Variable).
#
# EMAIL_ENABLED defaults to OFF: a fresh checkout of this project must
# never silently start sending email just because SMTP values happen to
# be present in someone's environment.
EMAIL_ENABLED = os.environ.get("DAAMS_EMAIL_ENABLED", "false").strip().lower() == "true"

SMTP_HOST = os.environ.get("DAAMS_SMTP_HOST", "")

try:
    SMTP_PORT = int(os.environ.get("DAAMS_SMTP_PORT", "587"))
except ValueError:
    # A garbled env var must not crash startup -- fall back to the
    # standard STARTTLS submission port and let the actual send attempt
    # fail (and be reported) later if the value was wrong for another reason.
    SMTP_PORT = 587

SMTP_USERNAME = os.environ.get("DAAMS_SMTP_USERNAME", "")
SMTP_PASSWORD = os.environ.get("DAAMS_SMTP_PASSWORD", "")

# Where security alert / incident notifications are sent.
NOTIFICATION_EMAIL = os.environ.get("DAAMS_NOTIFICATION_EMAIL", "")

# ---------------------------------------------------------------------------
# EVENT COALESCING AND COPY/MOVE INFERENCE -- see event_aggregator.py
# ---------------------------------------------------------------------------
# Windows reports one user action (copy, move, save) as several raw
# filesystem events. Events on the same path are merged until the path has
# been quiet for COALESCE_QUIET_SECONDS, so one action produces one record.
# Records therefore appear about this long after the real action.
COALESCE_QUIET_SECONDS = 1.0

# A path that keeps changing (a large file still being copied) is reported
# anyway after this long.
COALESCE_MAX_WAIT_SECONDS = 10.0

# A Delete and a Create of the same file within this window are reported as
# one Move; a path that vanishes and reappears is reported as one Modify.
MOVE_PAIRING_WINDOW_SECONDS = 2.0

# Copy/Move inference compares SHA-256 hashes of files inside the protected
# scope (kept in memory only, never stored). Files larger than this are not
# hashed, so Copy cannot be inferred for them.
HASH_MAX_BYTES = 50 * 1024 * 1024

# Safety cap on how many files of ONE protected folder are indexed.
MAX_INDEXED_FILES_PER_ASSET = 2000


# ---------------------------------------------------------------------------
# WINDOWS AUDIT LOG OPEN DETECTION (Phase 3)
# ---------------------------------------------------------------------------
ENABLE_OPEN_DETECTION = os.environ.get("DAAMS_OPEN_DETECTION", "true").strip().lower() in ("true", "1", "yes")
