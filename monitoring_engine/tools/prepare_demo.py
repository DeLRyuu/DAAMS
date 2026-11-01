"""
prepare_demo.py -- Clean DAAMS data and prepare a demonstration environment.

Defaults to a dry run that prints what it would do. Pass --yes to apply:

    python tools/prepare_demo.py          # Dry run (prints plan without changes)
    python tools/prepare_demo.py --yes    # Executes cleanup and demo setup

Options:
    --yes        Apply the planned changes.
    --firestore  Also clean Firestore cloud collections (asks for confirmation).
"""

from datetime import datetime
import glob
import os
import shutil
import sqlite3
import subprocess
import sys

ENGINE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if ENGINE_DIR not in sys.path:
    sys.path.insert(0, ENGINE_DIR)

from config.settings import DATA_DIR, LOGS_DIR  # noqa: E402
from core import asset_registry, database  # noqa: E402

TABLES_TO_CLEAR = [
    "activity_logs",
    "security_alerts",
    "incident_reports",
    "protected_assets",
    "sync_history",
]

PRESERVED_TABLES = [
    "security_settings",
    "schema_migrations",
    "app_settings",
]

DEMO_ROOT = r"C:\DAAMS_Demo"
DEMO_SUBFOLDER = os.path.join(DEMO_ROOT, "Subfolder_for_testing")

DEMO_FILES = {
    "financial_records_2026.txt": (
        "DAAMS Demo Asset - Financial Records 2026\n"
        "Quarterly revenue forecasts and budget allocations.\n"
        "Confidential internal financial data.\n"
    ),
    "employee_payroll_q4.txt": (
        "DAAMS Demo Asset - Employee Payroll Q4\n"
        "Confidential employee compensation and bonus schedule.\n"
        "Restricted access - HR only.\n"
    ),
    "security_audit_plan.txt": (
        "DAAMS Demo Asset - Security Audit Plan\n"
        "Detailed infrastructure vulnerability assessment checklist.\n"
        "Confidential security team documentation.\n"
    ),
    "confidential_merger_notes.txt": (
        "DAAMS Demo Asset - Strategic Merger Notes\n"
        "Highly sensitive transaction notes and partner evaluations.\n"
        "Strictly confidential - executive eyes only.\n"
    ),
}


def check_running_processes() -> list:
    """
    Check if the Monitoring Engine or Control Center processes are running.
    Returns a list of descriptions for any running processes found.
    """
    running = []

    # 1. Check for Control Center executable
    try:
        res = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq DAAMS.ControlCenter.exe", "/FO", "CSV", "/NH"],
            capture_output=True,
            text=True,
            check=False,
            timeout=5,
        )
        if "DAAMS.ControlCenter.exe".lower() in res.stdout.lower():
            running.append("DAAMS Control Center (DAAMS.ControlCenter.exe)")
    except Exception:
        pass

    # 2. Check for Monitoring Engine (python running main.py or monitor.py)
    try:
        res = subprocess.run(
            [
                "powershell",
                "-NoProfile",
                "-Command",
                "Get-CimInstance Win32_Process -Filter \"name like 'python%'\" | ForEach-Object { \"$($_.ProcessId)::$($_.CommandLine)\" }",
            ],
            capture_output=True,
            text=True,
            check=False,
            timeout=5,
        )
        my_pid = str(os.getpid())
        for line in res.stdout.splitlines():
            line = line.strip()
            if not line or "::" not in line:
                continue
            pid_str, cmd = line.split("::", 1)
            if pid_str == my_pid:
                continue
            cmd_lower = cmd.lower()
            if "main.py" in cmd_lower or "monitor.py" in cmd_lower:
                running.append(f"DAAMS Monitoring Engine (PID {pid_str})")
    except Exception:
        pass

    return running


def run_dry_run(touch_firestore: bool) -> int:
    """Print the actions that would be performed without executing them."""
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    backup_folder_name = f"backup_{stamp}"
    db_path = database.get_db_path()

    print("=" * 60)
    print("=== DAAMS Prepare Demo (DRY RUN) ===")
    print("Pass --yes to apply these actions.")
    print("=" * 60)
    print("\nPlanned Actions:")
    print("1. Process Check:")
    print("   - Verified that neither Monitoring Engine nor Control Center is running.")
    print(f"\n2. Backup Creation (data/ -> data/{backup_folder_name}):")
    print(f"   - Database: {db_path} (checkpoint WAL and copy)")
    print(f"   - Settings: {os.path.join(DATA_DIR, 'security_settings.json')} (copied to backup)")
    print(f"   - Assets  : {os.path.join(DATA_DIR, 'protected_assets.json')} (copied to backup)")
    print("   - Logs    : Copy all logs/*.jsonl files to backup folder before clearing")
    print("\n3. Database Cleanup (SQLite):")
    for tbl in TABLES_TO_CLEAR:
        print(f"   - Delete all rows from table '{tbl}'")
    print("   - Reset sqlite_sequence entries for cleared tables")
    print("   - PRESERVED (NEVER TOUCHED):")
    for tbl in PRESERVED_TABLES:
        print(f"     * Table '{tbl}' (PIN and internal configuration preserved)")
    print(f"     * File  '{os.path.join(DATA_DIR, 'security_settings.json')}' (PIN file preserved)")
    print("\n4. Log Files Cleanup:")
    jsonl_files = glob.glob(os.path.join(LOGS_DIR, "*.jsonl"))
    if jsonl_files:
        for f in jsonl_files:
            print(f"   - Clear contents of {os.path.basename(f)}")
    else:
        print("   - Clear logs/*.jsonl (none currently present)")
    print(f"\n5. Demo Assets Creation:")
    print(f"   - Directory: {DEMO_ROOT}")
    print(f"   - Subfolder: {DEMO_SUBFOLDER}")
    for fname, content in DEMO_FILES.items():
        print(f"   - File: {os.path.join(DEMO_ROOT, fname)} ({len(content)} bytes)")
    print(f"\n6. Protected Asset Registration:")
    print(f"   - Protect '{DEMO_ROOT}' as Confidential via core.asset_registry.protect_asset")
    print("\n7. Cloud (Firestore):")
    if touch_firestore:
        print("   - --firestore passed: will prompt for confirmation before deleting cloud records")
    else:
        print("   - Firestore will NOT be touched (pass --firestore to include cloud records)")

    print("\n" + "=" * 60)
    print("Dry run complete. No files or database records were modified.")
    print("To execute this plan, run: python tools/prepare_demo.py --yes")
    print("=" * 60)
    return 0


def execute_prepare(touch_firestore: bool) -> int:
    """Execute the data cleanup and demo setup."""
    db_path = database.get_db_path()
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    backup_dir = os.path.join(DATA_DIR, f"backup_{stamp}")

    print("=" * 60)
    print("=== Executing DAAMS Demo Preparation ===")
    print("=" * 60)

    # 1. Back up data/
    print(f"\n[1/5] Backing up data/ to {backup_dir}...")
    os.makedirs(backup_dir, exist_ok=True)

    if os.path.exists(db_path):
        try:
            conn = sqlite3.connect(db_path, timeout=5.0)
            conn.execute("PRAGMA wal_checkpoint(TRUNCATE);")
            conn.close()
        except Exception as e:
            print(f"  [WARNING] WAL checkpoint before backup encountered: {e}")

    # Copy files in data/ (excluding backup folders)
    if os.path.exists(DATA_DIR):
        for item in os.listdir(DATA_DIR):
            item_path = os.path.join(DATA_DIR, item)
            if item.startswith("backup_") and os.path.isdir(item_path):
                continue
            if os.path.isfile(item_path):
                shutil.copy2(item_path, os.path.join(backup_dir, item))
                print(f"  Backed up data file: {item}")

    # Copy logs/*.jsonl into backup folder
    jsonl_files = glob.glob(os.path.join(LOGS_DIR, "*.jsonl"))
    for log_path in jsonl_files:
        shutil.copy2(log_path, os.path.join(backup_dir, os.path.basename(log_path)))
        print(f"  Backed up log file: {os.path.basename(log_path)}")

    # 2. Clear SQLite tables
    print("\n[2/5] Clearing SQLite tables (preserving PIN settings)...")
    if os.path.exists(db_path):
        conn = database.get_connection()
        try:
            with conn:
                existing_tables = {
                    r[0] for r in conn.execute("SELECT name FROM sqlite_master WHERE type='table'")
                }
                for table in TABLES_TO_CLEAR:
                    if table in existing_tables:
                        count = conn.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0]
                        conn.execute(f"DELETE FROM {table}")
                        print(f"  Cleared table '{table}': {count} row(s) deleted")
                if "sqlite_sequence" in existing_tables:
                    for table in TABLES_TO_CLEAR:
                        conn.execute("DELETE FROM sqlite_sequence WHERE name = ?", (table,))
        finally:
            conn.close()
    else:
        print(f"  Database not found at {db_path}; initializing fresh database...")
        database.initialize_database()

    # 3. Clear logs/*.jsonl
    print("\n[3/5] Clearing logs/*.jsonl files...")
    for log_path in jsonl_files:
        try:
            with open(log_path, "w", encoding="utf-8") as f:
                pass
            print(f"  Cleared {os.path.basename(log_path)}")
        except Exception as e:
            print(f"  [WARNING] Failed to clear {os.path.basename(log_path)}: {e}")

    # 4. Create C:\DAAMS_Demo
    print(f"\n[4/5] Creating demo directory structure at {DEMO_ROOT}...")
    os.makedirs(DEMO_SUBFOLDER, exist_ok=True)
    for fname, content in DEMO_FILES.items():
        fpath = os.path.join(DEMO_ROOT, fname)
        with open(fpath, "w", encoding="utf-8") as f:
            f.write(content)
        print(f"  Created file: {fpath} ({len(content)} bytes)")
    print(f"  Created folder: {DEMO_SUBFOLDER}")

    # 5. Protect C:\DAAMS_Demo as Confidential
    print(f"\n[5/5] Protecting '{DEMO_ROOT}' as Confidential...")
    success, msg = asset_registry.protect_asset(DEMO_ROOT, classification="Confidential")
    if success:
        print(f"  [SUCCESS] {msg}")
    else:
        print(f"  [ERROR] {msg}")
        return 1

    # Optional Firestore cleanup
    if touch_firestore:
        print("\n[FIRESTORE] Cloud cleanup requested via --firestore.")
        ans = input("Are you sure you want to clean Firestore cloud test data? Type DELETE CLOUD DATA to proceed: ").strip()
        if ans == "DELETE CLOUD DATA":
            try:
                from config.settings import (
                    FIRESTORE_COLLECTION,
                    INCIDENT_REPORTS_COLLECTION,
                    PROTECTED_ASSETS_COLLECTION,
                    SECURITY_ALERTS_COLLECTION,
                )
                from services import firestore_logger

                client = firestore_logger.get_firestore_client()
                if client is None:
                    print("  [FIRESTORE WARNING] Could not connect to Firestore; skipping.")
                else:
                    collections_to_delete = [
                        FIRESTORE_COLLECTION,
                        SECURITY_ALERTS_COLLECTION,
                        INCIDENT_REPORTS_COLLECTION,
                        PROTECTED_ASSETS_COLLECTION,
                    ]
                    for coll in collections_to_delete:
                        deleted = 0
                        while True:
                            refs = [doc.reference for doc in client.collection(coll).select([]).limit(100).stream()]
                            if not refs:
                                break
                            batch = client.batch()
                            for ref in refs:
                                batch.delete(ref)
                            batch.commit()
                            deleted += len(refs)
                        print(f"  Cleared Firestore collection '{coll}': {deleted} doc(s)")
            except Exception as e:
                print(f"  [FIRESTORE WARNING] Error cleaning Firestore: {e}")
        else:
            print("  Cancelled Firestore cloud cleanup.")
    else:
        print("\nFirestore cloud data was not touched (use --firestore to clean cloud data).")

    print("\n" + "=" * 60)
    print("Demo preparation complete!")
    print(f"Protected folder: {DEMO_ROOT} (Confidential)")
    print(f"Backup location: {backup_dir}")
    print("PIN settings and tables were left untouched.")
    print("=" * 60)
    return 0


def main() -> int:
    # Check if engine or Control Center is running
    running = check_running_processes()
    if running:
        print("[ERROR] Refusing to run: DAAMS processes are currently active:")
        for proc in running:
            print(f"  - {proc}")
        print("\nPlease close the Monitoring Engine and Control Center before preparing demo data.")
        return 1

    apply_changes = "--yes" in sys.argv
    touch_firestore = "--firestore" in sys.argv

    if not apply_changes:
        return run_dry_run(touch_firestore)
    else:
        return execute_prepare(touch_firestore)


if __name__ == "__main__":
    sys.exit(main())
