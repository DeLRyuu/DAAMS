"""
config.py

Configuration for the DAAMS Python Monitoring Engine (Phase 1-3).

For this phase, only ONE protected path is supported, and it must be
explicitly set by an administrator. The engine will refuse to run
against the entire disk or an unset/invalid path.

Later phases may extend this to support multiple protected paths and
per-asset classification (Public/Sensitive/Private/Confidential), but
Phase 1 intentionally keeps this minimal.

Phase 3 adds Firestore configuration. No credential VALUES live in this
file -- only a path to a locally-stored, gitignored service-account JSON
file (or an environment variable override). See README.md for setup.
"""

import os

# ---------------------------------------------------------------------------
# ADMINISTRATOR CONFIGURATION
# ---------------------------------------------------------------------------
# Set this to the single folder or file you want DAAMS to protect/monitor.
# Examples (Windows):
#   PROTECTED_PATH = r"C:\Users\Juan\Documents\ProtectedFolder"
#   PROTECTED_PATH = r"D:\CompanyFiles\Payroll"
#
# Do NOT set this to a drive root (e.g. "C:\\") or the entire filesystem.
# DAAMS is designed to monitor administrator-selected assets only.
PROTECTED_PATH = r""  # <-- Set this before running.

# List of path values that are explicitly disallowed because they represent
# "monitor everything" rather than a specific protected asset.
DISALLOWED_ROOTS = {
    "c:\\", "d:\\", "e:\\", "f:\\",
    "/", "c:/", "d:/", "e:/", "f:/",
}

# ---------------------------------------------------------------------------
# FIRESTORE CONFIGURATION (Phase 3)
# ---------------------------------------------------------------------------
# Set to False to turn Firestore uploads off entirely. Local logging
# (Phase 2) is completely unaffected either way.
ENABLE_FIRESTORE = True

# Name of the Firestore collection that stores activity records.
FIRESTORE_COLLECTION = "activity_logs"

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


def get_protected_path() -> str:
    """Return the configured protected path (as configured, not yet validated)."""
    return PROTECTED_PATH


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