"""
reset_firestore.py -- delete DAAMS test records from Firebase Firestore.

Run from the monitoring_engine folder, with main.py STOPPED:

    python tools/reset_firestore.py --dry-run   # just count what is there (deletes nothing)
    python tools/reset_firestore.py             # delete test history
    python tools/reset_firestore.py --all       # ALSO delete protected assets and PIN settings

By default this deletes every document in: activity_logs, security_alerts,
incident_reports. It KEEPS protected_assets and security_settings (the PIN).
With --all those two collections are emptied as well.

THIS CANNOT BE UNDONE. Firestore has no recycle bin, and this script makes no
backup. Check first that nobody on your team (WPF console, researcher,
testers) still needs what is in the project, and read the project ID the
script prints before you confirm.

Recommended order: run tools/reset_test_data.py first (local database), then
this script, then start main.py. Credentials come from the same place as the
engine (serviceAccountKey.json or DAAMS_FIREBASE_CREDENTIALS).
"""

import os
import sys

ENGINE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ENGINE_DIR)

from config.settings import (  # noqa: E402
    FIRESTORE_COLLECTION,
    INCIDENT_REPORTS_COLLECTION,
    PROTECTED_ASSETS_COLLECTION,
    SECURITY_ALERTS_COLLECTION,
    SECURITY_SETTINGS_COLLECTION,
)
from services import firestore_logger  # noqa: E402

HISTORY_COLLECTIONS = [FIRESTORE_COLLECTION, SECURITY_ALERTS_COLLECTION, INCIDENT_REPORTS_COLLECTION]
SETUP_COLLECTIONS = [PROTECTED_ASSETS_COLLECTION, SECURITY_SETTINGS_COLLECTION]
BATCH_SIZE = 400  # Firestore allows 500 writes per batch; stay under it
CONFIRM_PHRASE = "DELETE CLOUD DATA"


def count_documents(client, name: str) -> int:
    # select([]) fetches document references only, not their contents.
    return sum(1 for _ in client.collection(name).select([]).stream())


def delete_collection(client, name: str) -> int:
    deleted = 0
    while True:
        refs = [doc.reference for doc in client.collection(name).select([]).limit(BATCH_SIZE).stream()]
        if not refs:
            return deleted
        batch = client.batch()
        for ref in refs:
            batch.delete(ref)
        batch.commit()
        deleted += len(refs)
        print(f"  {name}: deleted {deleted} so far...")


def main() -> int:
    dry_run = "--dry-run" in sys.argv
    wipe_all = "--all" in sys.argv
    collections = HISTORY_COLLECTIONS + (SETUP_COLLECTIONS if wipe_all else [])

    client = firestore_logger.get_firestore_client()
    if client is None:
        reachable, reason = firestore_logger.is_firestore_reachable()
        print(f"Could not connect to Firestore: {reason}")
        print("Check serviceAccountKey.json (or DAAMS_FIREBASE_CREDENTIALS) and your internet connection.")
        return 1

    print(f"Firebase project: {getattr(client, 'project', 'unknown')}")
    print("Documents currently in the cloud:")
    counts = {}
    for name in collections:
        counts[name] = count_documents(client, name)
        print(f"  {name}: {counts[name]}")
    if not wipe_all:
        print(f"Keeping: {', '.join(SETUP_COLLECTIONS)}")
    else:
        print("WARNING: --all also deletes protected assets and the PIN settings from the cloud.")

    if dry_run:
        print("\nDry run only. Nothing was deleted.")
        return 0

    if sum(counts.values()) == 0:
        print("\nNothing to delete.")
        return 0

    print("\nThis cannot be undone and makes no backup. Make sure main.py is NOT running.")
    if input(f"Type {CONFIRM_PHRASE} to continue: ").strip() != CONFIRM_PHRASE:
        print("Cancelled. Nothing was deleted.")
        return 0

    for name in collections:
        if counts[name]:
            total = delete_collection(client, name)
            print(f"  {name}: removed {total} document(s)")

    print("\nDone. Start main.py when you are ready.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
