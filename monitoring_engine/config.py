"""
config.py

Configuration for the DAAMS Python Monitoring Engine (Phase 1).

For this phase, only ONE protected path is supported, and it must be
explicitly set by an administrator. The engine will refuse to run
against the entire disk or an unset/invalid path.

Later phases may extend this to support multiple protected paths and
per-asset classification (Public/Sensitive/Private/Confidential), but
Phase 1 intentionally keeps this minimal.
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
