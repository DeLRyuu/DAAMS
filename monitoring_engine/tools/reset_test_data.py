"""
reset_test_data.py -- clear DAAMS test history so a new test run starts clean.

Run from the monitoring_engine folder, with main.py STOPPED:

    python tools/reset_test_data.py          # clear history only (recommended)
    python tools/reset_test_data.py --all    # ALSO clear protected assets and the local Passkey copy

By default this clears: activity_logs, security_alerts, incident_reports and
sync_history in data/daams.db, and moves the matching .jsonl files in logs/
into a timestamped backup folder.

WHY THE FILES ARE MOVED TOO: core/database.py re-imports the legacy .jsonl /
.json files into the database every time it is imported. Clearing only the
database tables would let the old records come straight back.

By default it KEEPS: protected_assets (what you protected), security_settings
(the local Passkey PIN copy) and schema_migrations. With --all those are cleared too,
which unprotects everything and removes the local Passkey copy. There is no default
PIN: the engine downloads the Passkey from Firestore again once it is online.

Always makes a backup first. Only affects THIS computer: records that were
already uploaded to Firestore stay in the cloud.
"""

import os
import shutil
import sqlite3
import sys
from datetime import datetime

ENGINE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ENGINE_DIR)

from config.settings import DATA_DIR, LOGS_DIR  # noqa: E402
from core import database  # noqa: E402

HISTORY_TABLES = ["activity_logs", "security_alerts", "incident_reports", "sync_history"]
SETUP_TABLES = ["protected_assets", "security_settings"]
JSONL_FILES = ["activity_log.jsonl", "security_alerts.jsonl", "incident_reports.jsonl"]
SETUP_JSON_FILES = ["protected_assets.json", "security_settings.json"]  # only moved with --all


def main() -> int:
    wipe_all = "--all" in sys.argv
    db_path = database.get_db_path()

    if not os.path.exists(db_path):
        print(f"No database found at {db_path}. Nothing to reset.")
        return 1

    tables = HISTORY_TABLES + (SETUP_TABLES if wipe_all else [])
    print(f"Database : {db_path}")
    print(f"Will clear: {', '.join(tables)}")
    print(f"Will move : {', '.join(JSONL_FILES)} (from {LOGS_DIR}) into a backup folder")
    if wipe_all:
        print(f"Will move : {', '.join(SETUP_JSON_FILES)} (from {DATA_DIR}) too")
        print("WARNING   : --all also removes every protected asset and the local Passkey PIN copy.")
        print("            (It is downloaded again from Firestore when this PC is online.)")
    print("Make sure main.py is NOT running.")
    if input("Type RESET to continue: ").strip() != "RESET":
        print("Cancelled. Nothing was changed.")
        return 0

    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    backup_dir = os.path.join(os.path.dirname(db_path), f"backup_{stamp}")
    os.makedirs(backup_dir, exist_ok=True)

    conn = sqlite3.connect(db_path)
    try:
        conn.execute("PRAGMA wal_checkpoint(TRUNCATE);")  # fold any WAL data in before copying
    except sqlite3.DatabaseError:
        pass
    conn.close()
    shutil.copy2(db_path, os.path.join(backup_dir, os.path.basename(db_path)))

    conn = sqlite3.connect(db_path)
    try:
        with conn:
            existing = {r[0] for r in conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
            for table in tables:
                if table in existing:
                    before = conn.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0]
                    conn.execute(f"DELETE FROM {table}")
                    print(f"  cleared {table}: {before} row(s)")
            if "sqlite_sequence" in existing:
                for table in tables:
                    conn.execute("DELETE FROM sqlite_sequence WHERE name = ?", (table,))
    finally:
        conn.close()

    for name in JSONL_FILES:
        src = os.path.join(LOGS_DIR, name)
        if os.path.exists(src):
            shutil.move(src, os.path.join(backup_dir, name))
            print(f"  moved {name} to backup")

    if wipe_all:
        for name in SETUP_JSON_FILES:
            src = os.path.join(DATA_DIR, name)
            if os.path.exists(src):
                shutil.move(src, os.path.join(backup_dir, name))
                print(f"  moved {name} to backup")

    print(f"\nDone. Backup saved in: {backup_dir}")
    print("Restart main.py. Add backup_* folders to .gitignore.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
