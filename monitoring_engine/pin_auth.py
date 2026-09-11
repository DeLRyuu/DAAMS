"""
pin_auth.py

PIN-based confirmation gate for security-sensitive DAAMS actions:
Protect This File/Folder, Change Classification, and Remove Protection.

Why: the Windows right-click menu is only as trustworthy as "whoever is
currently logged into this Windows account" -- there's no separate login
step of its own. A PIN adds one lightweight extra confirmation before
any of these three actions actually take effect, so an unattended
moment at an unlocked PC (or a misclick) can't silently change what
DAAMS is protecting.

Storage model (mirrors asset_registry.py):
  - A local JSON file (security_settings.json) is the SOURCE OF TRUTH,
    so PIN verification keeps working even if Firestore is unreachable.
  - Firestore ('security_settings' collection, one fixed document) is
    kept in sync on a best-effort basis, using the same tolerant,
    never-crash pattern as the rest of Phase 3/4.
  - This module -- not any individual script -- owns the PIN. The
    Windows Explorer scripts AND the future C# WPF admin console are
    both expected to call into this same logic/data rather than each
    keeping their own separate idea of "what the PIN is", per the
    project's "one shared implementation" architecture rule.

Honesty about what this is (and isn't):
  - The PIN is never stored in plaintext -- only a per-install salted
    SHA-256 hash.
  - There is NO rate limiting, lockout, or attempt logging in this
    phase. This is a deliberate, honest "raise the bar past a single
    accidental or casual click" control, appropriate for a student
    capstone -- it is not a claim of strong authentication, and
    shouldn't be described as one.
  - DAAMS ships with a default PIN (config.DEFAULT_PIN) so the very
    first Protect/Reclassify/Remove action already has something to
    check against. Every confirm_with_pin() call warns the admin if
    they're still using it, until they run change_pin_cli.py.
"""

import getpass
import hashlib
import json
import os
import secrets
import tkinter as tk
from datetime import datetime
from tkinter import messagebox, simpledialog

from config import DEFAULT_PIN
import firestore_logger

SETTINGS_FILE = os.path.join(os.path.dirname(__file__), "security_settings.json")

MIN_PIN_LENGTH = 4
MAX_PIN_LENGTH = 8


def _hash_pin(pin: str, salt: str) -> str:
    return hashlib.sha256((salt + pin).encode("utf-8")).hexdigest()


def _load_settings() -> dict:
    if not os.path.exists(SETTINGS_FILE):
        return {}
    try:
        with open(SETTINGS_FILE, "r", encoding="utf-8") as f:
            return json.load(f)
    except (OSError, json.JSONDecodeError) as e:
        print(f"[SECURITY WARNING] Could not read PIN settings file: {e}")
        return {}


def _save_settings(data: dict) -> bool:
    try:
        tmp_path = SETTINGS_FILE + ".tmp"
        with open(tmp_path, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)
        os.replace(tmp_path, SETTINGS_FILE)
        return True
    except OSError as e:
        print(f"[SECURITY WARNING] Could not save PIN settings: {e}")
        return False


def _ensure_initialized() -> dict:
    """
    If no PIN has ever been set on this machine, silently initialize one
    using config.DEFAULT_PIN, so the very first security-sensitive
    action already has something real to check against -- rather than
    treating "nothing configured yet" as "anything goes".
    """
    settings = _load_settings()
    if settings.get("pin_hash"):
        return settings

    salt = secrets.token_hex(16)
    settings = {
        "pin_hash": _hash_pin(DEFAULT_PIN, salt),
        "salt": salt,
        "updated_at": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
        "updated_by": "system-default",
        "is_default": True,
    }
    _save_settings(settings)
    try:
        firestore_logger.upload_security_settings(settings)
    except Exception as e:
        print(f"[FIRESTORE WARNING] Could not sync default PIN to Firestore: {e}")
    return settings


def is_using_default_pin() -> bool:
    """True if DAAMS is still using the un-changed config.DEFAULT_PIN."""
    return bool(_ensure_initialized().get("is_default"))


def verify_pin(candidate: str) -> bool:
    """Check a candidate PIN against the stored (hashed) PIN."""
    if not candidate:
        return False
    settings = _ensure_initialized()
    return _hash_pin(candidate, settings.get("salt", "")) == settings.get("pin_hash")


def set_pin(new_pin: str, changed_by: str = None) -> tuple:
    """
    Change the shared DAAMS PIN. Returns (success: bool, message: str).
    Validates the new PIN is digits-only and within the allowed length
    before storing it, so a mistyped/empty PIN can't accidentally lock
    everyone out.
    """
    if not new_pin or not new_pin.isdigit():
        return False, "PIN must contain digits only."
    if not (MIN_PIN_LENGTH <= len(new_pin) <= MAX_PIN_LENGTH):
        return False, f"PIN must be {MIN_PIN_LENGTH}-{MAX_PIN_LENGTH} digits long."

    salt = secrets.token_hex(16)
    settings = {
        "pin_hash": _hash_pin(new_pin, salt),
        "salt": salt,
        "updated_at": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
        "updated_by": changed_by or getpass.getuser(),
        "is_default": False,
    }
    if not _save_settings(settings):
        return False, "Failed to save the new PIN locally. PIN was NOT changed."

    try:
        firestore_logger.upload_security_settings(settings)
    except Exception as e:
        print(f"[FIRESTORE WARNING] Could not sync new PIN to Firestore: {e}")

    return True, "PIN changed successfully."


def prompt_pin(title: str = "DAAMS - PIN Required", message: str = "Enter your DAAMS PIN:") -> str:
    """
    Show a small modal, masked PIN-entry dialog and return what was
    typed, or None if cancelled. Creates and cleans up its own hidden Tk
    root, so callers don't need a Tk window already open.
    """
    root = tk.Tk()
    root.withdraw()
    pin = simpledialog.askstring(title, message, show="*", parent=root)
    root.destroy()
    return pin


def confirm_with_pin(action_description: str) -> bool:
    """
    Convenience wrapper used by protect_asset_cli.py and
    remove_protection_cli.py: prompts
    for a PIN, verifies it, and shows a clear error on failure.

    `action_description` is a short phrase completing "to ___", e.g.
    "protect this file" or "remove protection".

    Returns True only if the correct PIN was entered. Also warns (but
    does not block) if the default PIN is still in use.
    """
    pin = prompt_pin(message=f"Enter your DAAMS PIN to {action_description}:")
    if pin is None:
        return False  # Cancelled -- treated the same as a failed check.

    if not verify_pin(pin):
        messagebox.showerror("DAAMS", "Incorrect PIN. Action cancelled.")
        return False

    if is_using_default_pin():
        messagebox.showwarning(
            "DAAMS - Security Notice",
            "You are still using the DEFAULT PIN (1234).\n\n"
            "Please change it soon by running change_pin_cli.py.",
        )

    return True
