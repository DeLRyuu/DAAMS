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
#   2. Otherwise, defaults to "serviceAccountKey.json" in this same
#      folder -- a filename already covered by .gitignore.
#
# See README.md "Secure Credential Setup" for how to obtain and place
# this file.
FIRESTORE_CREDENTIALS_PATH = os.environ.get(
    "DAAMS_FIREBASE_CREDENTIALS",
    os.path.join(os.path.dirname(__file__), "serviceAccountKey.json"),
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

# Name of the Firestore collection that mirrors the PIN configuration.
# Always contains exactly one document -- see pin_auth.py. Stores only a
# salted hash, never the PIN itself.
SECURITY_SETTINGS_COLLECTION = "security_settings"
