"""
firestore_logger.py

Handles ALL communication between the DAAMS Monitoring Engine and Firebase
Firestore. Kept in its own module (Phase 3 requirement) so that detection
and local logging (Phase 1 + 2) never depend on Firestore being reachable
or even configured.

Design goals:
- NEVER crash the monitoring engine. Every failure mode (missing
  credentials, invalid credentials, no network, Firestore quota/errors) is
  caught here and reported as a warning. Detection and local logging
  always continue regardless of what happens in this module.
- No credentials are hard-coded. The service-account JSON path comes from
  config.py, which resolves it from an environment variable or a local,
  gitignored file -- see README.md for setup.
- Lazy, one-time initialization. The Firebase Admin SDK / Firestore client
  is set up on the first upload attempt, not at import time, so simply
  importing this module never fails even if firebase-admin isn't
  installed or credentials aren't configured yet. If setup fails once,
  Firestore is marked unavailable and every later call becomes a fast
  no-op with no repeated warning spam.
"""

import os

from config import ENABLE_FIRESTORE, FIRESTORE_COLLECTION, FIRESTORE_CREDENTIALS_PATH

_firestore_client = None
_firestore_disabled = False
_init_attempted = False


def _initialize_firestore() -> None:
    """
    Attempt to initialize the Firebase Admin SDK and Firestore client.
    Safe to call many times -- only the first call does real work; every
    call after that returns immediately.
    """
    global _firestore_client, _firestore_disabled, _init_attempted

    if _init_attempted:
        return
    _init_attempted = True

    if not ENABLE_FIRESTORE:
        _firestore_disabled = True
        print("[FIRESTORE] Disabled in config.py (ENABLE_FIRESTORE = False). "
              "Local logging will continue normally.")
        return

    if not os.path.exists(FIRESTORE_CREDENTIALS_PATH):
        _firestore_disabled = True
        print(
            f"[FIRESTORE WARNING] Credentials file not found at "
            f"'{FIRESTORE_CREDENTIALS_PATH}'.\n"
            f"Firestore uploads are disabled for this session; local "
            f"logging will continue normally. See README.md 'Secure "
            f"Credential Setup' to configure Firestore."
        )
        return

    try:
        import firebase_admin
        from firebase_admin import credentials, firestore

        cred = credentials.Certificate(FIRESTORE_CREDENTIALS_PATH)
        firebase_admin.initialize_app(cred)
        _firestore_client = firestore.client()
        print("[FIRESTORE] Connected successfully.")
    except ImportError:
        _firestore_disabled = True
        print(
            "[FIRESTORE WARNING] The 'firebase-admin' package is not "
            "installed. Run: pip install -r requirements.txt\n"
            "Local logging will continue normally."
        )
    except Exception as e:
        _firestore_disabled = True
        print(f"[FIRESTORE WARNING] Could not initialize Firestore: {e}")
        print("Local logging will continue normally.")


def upload_activity(record: dict) -> bool:
    """
    Upload a single activity record to Firestore as its own document in
    the collection named by config.FIRESTORE_COLLECTION.

    Returns True if the upload succeeded, False otherwise. This function
    NEVER raises -- any failure is caught and reported as a clear warning
    so the monitoring engine's detection loop is never interrupted.
    """
    _initialize_firestore()

    if _firestore_disabled or _firestore_client is None:
        return False

    try:
        _firestore_client.collection(FIRESTORE_COLLECTION).add(record)
        return True
    except Exception as e:
        # Covers network errors, expired/invalid credentials, quota
        # issues, and any other Firestore-side failure.
        print(f"[FIRESTORE WARNING] Failed to upload activity to Firestore: {e}")
        print("The activity was still saved to the local log.")
        return False
