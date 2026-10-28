"""
database.py

Unified SQLite database service for DAAMS (Offline-First Architecture).

Shared between the Python Monitoring Engine and the C# WPF Control Center.
Accessible to both applications on the same Windows laptop.

Key design points:
- Uses SQLite WAL mode (Write-Ahead Logging) for concurrent access by both Python and C#.
- Uses busy_timeout = 5000ms so operations wait safely if a transaction is active.
- Defines canonical schema, tables, and timestamp format ("%Y-%m-%d %H:%M:%S").
- Manages schema migrations via the schema_migrations table.
- Automatically migrates existing JSON/JSONL data into the database on first run.
- Preserves local JSON/JSONL files for backwards-compatibility.
- Tracks persistent sync_status ('PENDING', 'SYNCED', 'FAILED') for Firestore sync.
"""

import hashlib
import json
import os
import sqlite3
import threading
from datetime import datetime
from typing import Any, Dict, List, Optional, Tuple

from config.settings import DATA_DIR, LOGS_DIR

DB_FILENAME = "daams.db"
_DB_LOCK = threading.RLock()


def get_db_path() -> str:
    """
    Resolve the canonical path to the shared SQLite database.
    Order of precedence:
      1. DAAMS_DATABASE_PATH environment variable (if set).
      2. 'monitoring_engine/data/daams.db' relative to project.
    """
    env_path = os.environ.get("DAAMS_DATABASE_PATH")
    if env_path and env_path.strip():
        return os.path.normpath(env_path.strip())
    return os.path.normpath(os.path.join(DATA_DIR, DB_FILENAME))


def get_connection() -> sqlite3.Connection:
    """
    Open and return a configured SQLite connection.
    Enables WAL mode, foreign keys, and safe busy timeout.
    """
    db_path = get_db_path()
    os.makedirs(os.path.dirname(os.path.abspath(db_path)), exist_ok=True)

    conn = sqlite3.connect(db_path, timeout=10.0, check_same_thread=False)
    conn.row_factory = sqlite3.Row

    # Concurrency and safety pragmas
    conn.execute("PRAGMA journal_mode = WAL;")
    conn.execute("PRAGMA busy_timeout = 5000;")
    conn.execute("PRAGMA synchronous = NORMAL;")
    conn.execute("PRAGMA foreign_keys = ON;")
    return conn


def _generate_activity_id(timestamp_str: str = None) -> str:
    """Generate a readable, unique, timestamped activity ID."""
    now_dt = datetime.now()
    if timestamp_str:
        try:
            now_dt = datetime.strptime(timestamp_str, "%Y-%m-%d %H:%M:%S")
        except Exception:
            pass
    date_part = now_dt.strftime("%Y%m%d-%H%M%S")
    random_part = os.urandom(3).hex()
    return f"ACT-{date_part}-{random_part}"


def _asset_doc_id(path: str) -> str:
    """Derive stable 24-char sha256 hex from normalized path."""
    normalized = os.path.normcase(os.path.normpath(os.path.abspath(path)))
    return hashlib.sha256(normalized.encode("utf-8")).hexdigest()[:24]


SCHEMA_V1_DDL = """
-- Migrations table
CREATE TABLE IF NOT EXISTS schema_migrations (
    version INTEGER PRIMARY KEY,
    applied_at TEXT NOT NULL,
    description TEXT NOT NULL
);

-- Protected Assets
CREATE TABLE IF NOT EXISTS protected_assets (
    path TEXT PRIMARY KEY,
    asset_id TEXT NOT NULL UNIQUE,
    asset_name TEXT NOT NULL,
    asset_type TEXT NOT NULL,
    classification TEXT NOT NULL,
    protected_at TEXT NOT NULL,
    protected_by TEXT NOT NULL,
    status TEXT NOT NULL,
    sync_status TEXT NOT NULL DEFAULT 'PENDING',
    synced_at TEXT,
    sync_error TEXT,
    updated_at TEXT NOT NULL
);

-- Activity Logs
CREATE TABLE IF NOT EXISTS activity_logs (
    activity_id TEXT PRIMARY KEY,
    user TEXT NOT NULL,
    device TEXT NOT NULL,
    asset TEXT NOT NULL,
    action TEXT NOT NULL,
    timestamp TEXT NOT NULL,
    classification TEXT,
    risk_score INTEGER,
    risk_level TEXT,
    risk_factors TEXT,
    advisory TEXT,
    sync_status TEXT NOT NULL DEFAULT 'PENDING',
    synced_at TEXT,
    sync_error TEXT,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_activity_logs_timestamp ON activity_logs(timestamp DESC);
CREATE INDEX IF NOT EXISTS idx_activity_logs_sync ON activity_logs(sync_status);

-- Security Alerts
CREATE TABLE IF NOT EXISTS security_alerts (
    alert_id TEXT PRIMARY KEY,
    activity_id TEXT,
    user TEXT NOT NULL,
    device TEXT NOT NULL,
    asset TEXT NOT NULL,
    asset_path TEXT NOT NULL,
    action TEXT NOT NULL,
    asset_classification TEXT,
    risk_score INTEGER NOT NULL,
    risk_level TEXT NOT NULL,
    risk_factors TEXT,
    reason TEXT,
    timestamp TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'New',
    investigation_status TEXT DEFAULT 'Not Started',
    sync_status TEXT NOT NULL DEFAULT 'PENDING',
    synced_at TEXT,
    sync_error TEXT,
    updated_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_security_alerts_timestamp ON security_alerts(timestamp DESC);
CREATE INDEX IF NOT EXISTS idx_security_alerts_sync ON security_alerts(sync_status);

-- Incident Reports
CREATE TABLE IF NOT EXISTS incident_reports (
    incident_id TEXT PRIMARY KEY,
    related_alert_id TEXT,
    user TEXT NOT NULL,
    device TEXT NOT NULL,
    asset TEXT NOT NULL,
    asset_path TEXT,
    action TEXT NOT NULL,
    classification TEXT,
    timestamp TEXT NOT NULL,
    risk_score INTEGER,
    risk_level TEXT NOT NULL,
    risk_factors TEXT,
    description TEXT NOT NULL,
    investigation_status TEXT NOT NULL DEFAULT 'New',
    investigation_notes TEXT,
    resolution TEXT,
    date_resolved TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    sync_status TEXT NOT NULL DEFAULT 'PENDING',
    synced_at TEXT,
    sync_error TEXT
);

CREATE INDEX IF NOT EXISTS idx_incident_reports_updated ON incident_reports(updated_at DESC);
CREATE INDEX IF NOT EXISTS idx_incident_reports_sync ON incident_reports(sync_status);

-- Security Settings (PIN)
CREATE TABLE IF NOT EXISTS security_settings (
    key TEXT PRIMARY KEY,
    pin_hash TEXT NOT NULL,
    salt TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    updated_by TEXT NOT NULL,
    is_default INTEGER NOT NULL DEFAULT 1,
    sync_status TEXT NOT NULL DEFAULT 'PENDING',
    synced_at TEXT,
    sync_error TEXT
);

-- App Settings (Control Center preferences)
CREATE TABLE IF NOT EXISTS app_settings (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

-- Sync History / Audit log
CREATE TABLE IF NOT EXISTS sync_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    entity_type TEXT NOT NULL,
    entity_id TEXT NOT NULL,
    action TEXT NOT NULL,
    status TEXT NOT NULL,
    error_message TEXT,
    attempted_at TEXT NOT NULL
);
"""


def initialize_database() -> None:
    """
    Initialize SQLite schema and apply migrations.
    Safely migrates existing legacy JSON/JSONL files if database is fresh.
    """
    with _DB_LOCK:
        conn = get_connection()
        try:
            with conn:
                conn.executescript(SCHEMA_V1_DDL)

                # Check migration version
                cur = conn.cursor()
                cur.execute("SELECT version FROM schema_migrations WHERE version = 1;")
                row = cur.fetchone()
                if not row:
                    cur.execute(
                        "INSERT INTO schema_migrations (version, applied_at, description) VALUES (?, ?, ?);",
                        (1, datetime.now().strftime("%Y-%m-%d %H:%M:%S"), "Initial offline-first schema"),
                    )

            # Perform safe initial data import from JSON/JSONL
            _migrate_existing_json_data(conn)
        finally:
            conn.close()


def _migrate_existing_json_data(conn: sqlite3.Connection) -> None:
    """
    Safely migrate existing JSON/JSONL records into SQLite.
    Does not delete the source files.
    """
    now_str = datetime.now().strftime("%Y-%m-%d %H:%M:%S")

    with conn:
        # 1. Protected Assets migration
        assets_json_path = os.path.join(DATA_DIR, "protected_assets.json")
        if os.path.exists(assets_json_path):
            try:
                with open(assets_json_path, "r", encoding="utf-8") as f:
                    data = json.load(f)
                for key, item in data.items():
                    path = item.get("path") or key
                    asset_id = _asset_doc_id(path)
                    conn.execute(
                        """
                        INSERT OR IGNORE INTO protected_assets
                        (path, asset_id, asset_name, asset_type, classification, protected_at, protected_by, status, sync_status, synced_at, updated_at)
                        VALUES (?, ?, ?, ?, ?, ?, ?, ?, 'SYNCED', ?, ?);
                        """,
                        (
                            path,
                            asset_id,
                            item.get("asset_name", os.path.basename(path)),
                            item.get("asset_type", "file"),
                            item.get("classification", "Public"),
                            item.get("protected_at", now_str),
                            item.get("protected_by", "admin"),
                            item.get("status", "protected"),
                            now_str,
                            now_str,
                        ),
                    )
            except Exception as e:
                print(f"[DB MIGRATION WARNING] Failed migrating protected_assets.json: {e}")

        # 2. Security Settings (PIN) migration
        pin_json_path = os.path.join(DATA_DIR, "security_settings.json")
        if os.path.exists(pin_json_path):
            try:
                with open(pin_json_path, "r", encoding="utf-8") as f:
                    data = json.load(f)
                if data.get("pin_hash"):
                    conn.execute(
                        """
                        INSERT OR IGNORE INTO security_settings
                        (key, pin_hash, salt, updated_at, updated_by, is_default, sync_status, synced_at)
                        VALUES ('pin_config', ?, ?, ?, ?, ?, 'SYNCED', ?);
                        """,
                        (
                            data.get("pin_hash"),
                            data.get("salt", ""),
                            data.get("updated_at", now_str),
                            data.get("updated_by", "system-default"),
                            1 if data.get("is_default") else 0,
                            now_str,
                        ),
                    )
            except Exception as e:
                print(f"[DB MIGRATION WARNING] Failed migrating security_settings.json: {e}")

        # 3. Activity Logs migration
        activity_log_path = os.path.join(LOGS_DIR, "activity_log.jsonl")
        if os.path.exists(activity_log_path):
            try:
                with open(activity_log_path, "r", encoding="utf-8") as f:
                    for line in f:
                        line = line.strip()
                        if not line:
                            continue
                        item = json.loads(line)
                        user = item.get("user", "UNKNOWN")
                        device = item.get("device", "UNKNOWN")
                        asset = item.get("asset", "")
                        action = item.get("action", "Open")
                        ts = item.get("timestamp", now_str)

                        # Generate deterministic ID for migrated historical line
                        seed = f"{user}|{device}|{asset}|{action}|{ts}"
                        act_id = f"ACT-MIG-{hashlib.sha256(seed.encode('utf-8')).hexdigest()[:12]}"

                        risk_factors_json = json.dumps(item.get("risk_factors") or [])

                        conn.execute(
                            """
                            INSERT OR IGNORE INTO activity_logs
                            (activity_id, user, device, asset, action, timestamp, classification, risk_score, risk_level, risk_factors, advisory, sync_status, synced_at, created_at)
                            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'SYNCED', ?, ?);
                            """,
                            (
                                act_id,
                                user,
                                device,
                                asset,
                                action,
                                ts,
                                item.get("classification"),
                                item.get("risk_score"),
                                item.get("risk_level"),
                                risk_factors_json,
                                item.get("advisory"),
                                now_str,
                                now_str,
                            ),
                        )
            except Exception as e:
                print(f"[DB MIGRATION WARNING] Failed migrating activity_log.jsonl: {e}")

        # 4. Security Alerts migration
        alerts_log_path = os.path.join(LOGS_DIR, "security_alerts.jsonl")
        if os.path.exists(alerts_log_path):
            try:
                with open(alerts_log_path, "r", encoding="utf-8") as f:
                    for line in f:
                        line = line.strip()
                        if not line:
                            continue
                        item = json.loads(line)
                        alert_id = item.get("alert_id")
                        if not alert_id:
                            continue

                        risk_factors_json = json.dumps(item.get("risk_factors") or [])
                        conn.execute(
                            """
                            INSERT OR IGNORE INTO security_alerts
                            (alert_id, activity_id, user, device, asset, asset_path, action, asset_classification, risk_score, risk_level, risk_factors, reason, timestamp, status, investigation_status, sync_status, synced_at, updated_at)
                            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'SYNCED', ?, ?);
                            """,
                            (
                                alert_id,
                                item.get("activity_id"),
                                item.get("user", "UNKNOWN"),
                                item.get("device", "UNKNOWN"),
                                item.get("asset", "Unknown"),
                                item.get("asset_path", ""),
                                item.get("action", "Open"),
                                item.get("asset_classification"),
                                item.get("risk_score", 0),
                                item.get("risk_level", "Low"),
                                risk_factors_json,
                                item.get("reason"),
                                item.get("timestamp", now_str),
                                item.get("status", "New"),
                                item.get("investigation_status", "Not Started"),
                                now_str,
                                now_str,
                            ),
                        )
            except Exception as e:
                print(f"[DB MIGRATION WARNING] Failed migrating security_alerts.jsonl: {e}")

        # 5. Incident Reports migration
        incidents_log_path = os.path.join(LOGS_DIR, "incident_reports.jsonl")
        if os.path.exists(incidents_log_path):
            try:
                with open(incidents_log_path, "r", encoding="utf-8") as f:
                    for line in f:
                        line = line.strip()
                        if not line:
                            continue
                        item = json.loads(line)
                        incident_id = item.get("incident_id")
                        if not incident_id:
                            continue

                        risk_factors_json = json.dumps(item.get("risk_factors") or [])
                        conn.execute(
                            """
                            INSERT OR IGNORE INTO incident_reports
                            (incident_id, related_alert_id, user, device, asset, asset_path, action, classification, timestamp, risk_score, risk_level, risk_factors, description, investigation_status, investigation_notes, resolution, date_resolved, created_at, updated_at, sync_status, synced_at)
                            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'SYNCED', ?);
                            """,
                            (
                                incident_id,
                                item.get("related_alert_id"),
                                item.get("user", "UNKNOWN"),
                                item.get("device", "UNKNOWN"),
                                item.get("asset", "Unknown"),
                                item.get("asset_path"),
                                item.get("action", "Open"),
                                item.get("classification"),
                                item.get("timestamp", now_str),
                                item.get("risk_score"),
                                item.get("risk_level", "Low"),
                                risk_factors_json,
                                item.get("description", ""),
                                item.get("investigation_status", "New"),
                                item.get("investigation_notes"),
                                item.get("resolution"),
                                item.get("date_resolved"),
                                item.get("timestamp", now_str),
                                now_str,
                                now_str,
                            ),
                        )
            except Exception as e:
                print(f"[DB MIGRATION WARNING] Failed migrating incident_reports.jsonl: {e}")


# ---------------------------------------------------------------------------
# CRUD - Activity Logs
# ---------------------------------------------------------------------------

def save_activity(record: dict) -> dict:
    """
    Save activity record to SQLite database with sync_status = 'PENDING'.
    Attaches a unique activity_id if none exists.
    """
    with _DB_LOCK:
        conn = get_connection()
        try:
            now_str = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
            act_id = record.get("activity_id") or _generate_activity_id(record.get("timestamp"))
            record["activity_id"] = act_id

            risk_factors = record.get("risk_factors")
            risk_factors_json = json.dumps(risk_factors) if isinstance(risk_factors, list) else (risk_factors or "[]")

            with conn:
                conn.execute(
                    """
                    INSERT OR REPLACE INTO activity_logs
                    (activity_id, user, device, asset, action, timestamp, classification, risk_score, risk_level, risk_factors, advisory, sync_status, created_at)
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'PENDING', ?);
                    """,
                    (
                        act_id,
                        record.get("user", "UNKNOWN"),
                        record.get("device", "UNKNOWN"),
                        record.get("asset", ""),
                        record.get("action", ""),
                        record.get("timestamp", now_str),
                        record.get("classification"),
                        record.get("risk_score"),
                        record.get("risk_level"),
                        risk_factors_json,
                        record.get("advisory"),
                        now_str,
                    ),
                )
            return record
        finally:
            conn.close()


def get_all_activities(limit: int = 1000) -> List[dict]:
    """Return all activity logs ordered by timestamp descending."""
    with _DB_LOCK:
        conn = get_connection()
        try:
            cur = conn.cursor()
            cur.execute("SELECT * FROM activity_logs ORDER BY timestamp DESC LIMIT ?;", (limit,))
            rows = cur.fetchall()
            results = []
            for r in rows:
                item = dict(r)
                if item.get("risk_factors"):
                    try:
                        item["risk_factors"] = json.loads(item["risk_factors"])
                    except Exception:
                        pass
                results.append(item)
            return results
        finally:
            conn.close()


# ---------------------------------------------------------------------------
# CRUD - Security Alerts
# ---------------------------------------------------------------------------

def save_security_alert(alert: dict) -> dict:
    """Save security alert to SQLite database with sync_status = 'PENDING'."""
    with _DB_LOCK:
        conn = get_connection()
        try:
            now_str = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
            alert_id = alert.get("alert_id")

            risk_factors = alert.get("risk_factors")
            risk_factors_json = json.dumps(risk_factors) if isinstance(risk_factors, list) else (risk_factors or "[]")

            reason = alert.get("reason")
            if not reason and isinstance(risk_factors, list) and risk_factors:
                parts = [f.split(" (+")[0] for f in risk_factors]
                reason = " + ".join(parts)

            with conn:
                conn.execute(
                    """
                    INSERT OR REPLACE INTO security_alerts
                    (alert_id, activity_id, user, device, asset, asset_path, action, asset_classification, risk_score, risk_level, risk_factors, reason, timestamp, status, investigation_status, sync_status, updated_at)
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'PENDING', ?);
                    """,
                    (
                        alert_id,
                        alert.get("activity_id"),
                        alert.get("user", "UNKNOWN"),
                        alert.get("device", "UNKNOWN"),
                        alert.get("asset", "Unknown"),
                        alert.get("asset_path", ""),
                        alert.get("action", ""),
                        alert.get("asset_classification"),
                        alert.get("risk_score", 0),
                        alert.get("risk_level", "Low"),
                        risk_factors_json,
                        reason,
                        alert.get("timestamp", now_str),
                        alert.get("status", "New"),
                        alert.get("investigation_status", "Not Started"),
                        now_str,
                    ),
                )
            return alert
        finally:
            conn.close()


def get_all_security_alerts() -> List[dict]:
    """Return all security alerts ordered by timestamp descending."""
    with _DB_LOCK:
        conn = get_connection()
        try:
            cur = conn.cursor()
            cur.execute("SELECT * FROM security_alerts ORDER BY timestamp DESC;")
            rows = cur.fetchall()
            results = []
            for r in rows:
                item = dict(r)
                if item.get("risk_factors"):
                    try:
                        item["risk_factors"] = json.loads(item["risk_factors"])
                    except Exception:
                        pass
                results.append(item)
            return results
        finally:
            conn.close()


def update_security_alert_status(alert_id: str, new_status: str) -> bool:
    """Update alert status in SQLite and mark sync_status = 'PENDING'."""
    with _DB_LOCK:
        conn = get_connection()
        try:
            now_str = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
            with conn:
                cur = conn.execute(
                    """
                    UPDATE security_alerts
                    SET status = ?, sync_status = 'PENDING', updated_at = ?
                    WHERE alert_id = ?;
                    """,
                    (new_status, now_str, alert_id),
                )
                return cur.rowcount > 0
        finally:
            conn.close()


# ---------------------------------------------------------------------------
# CRUD - Incident Reports
# ---------------------------------------------------------------------------

def save_incident_report(incident: dict) -> dict:
    """Save incident report to SQLite with sync_status = 'PENDING'."""
    with _DB_LOCK:
        conn = get_connection()
        try:
            now_str = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
            inc_id = incident.get("incident_id")

            risk_factors = incident.get("risk_factors")
            risk_factors_json = json.dumps(risk_factors) if isinstance(risk_factors, list) else (risk_factors or "[]")

            with conn:
                conn.execute(
                    """
                    INSERT OR REPLACE INTO incident_reports
                    (incident_id, related_alert_id, user, device, asset, asset_path, action, classification, timestamp, risk_score, risk_level, risk_factors, description, investigation_status, investigation_notes, resolution, date_resolved, created_at, updated_at, sync_status)
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'PENDING');
                    """,
                    (
                        inc_id,
                        incident.get("related_alert_id"),
                        incident.get("user", "UNKNOWN"),
                        incident.get("device", "UNKNOWN"),
                        incident.get("asset", "Unknown"),
                        incident.get("asset_path"),
                        incident.get("action", ""),
                        incident.get("classification"),
                        incident.get("timestamp", now_str),
                        incident.get("risk_score"),
                        incident.get("risk_level", "Low"),
                        risk_factors_json,
                        incident.get("description", ""),
                        incident.get("investigation_status") or incident.get("status", "New"),
                        incident.get("investigation_notes") or incident.get("notes"),
                        incident.get("resolution"),
                        incident.get("date_resolved"),
                        incident.get("created_at", now_str),
                        now_str,
                    ),
                )
            return incident
        finally:
            conn.close()


def get_all_incident_reports() -> List[dict]:
    """Return all incidents ordered by updated_at descending."""
    with _DB_LOCK:
        conn = get_connection()
        try:
            cur = conn.cursor()
            cur.execute("SELECT * FROM incident_reports ORDER BY updated_at DESC;")
            rows = cur.fetchall()
            results = []
            for r in rows:
                item = dict(r)
                if item.get("risk_factors"):
                    try:
                        item["risk_factors"] = json.loads(item["risk_factors"])
                    except Exception:
                        pass
                results.append(item)
            return results
        finally:
            conn.close()


def update_incident_report(
    incident_id: str,
    status: str = None,
    notes: str = None,
    resolution: str = None,
    date_resolved: str = None,
) -> bool:
    """Update incident report in SQLite and mark sync_status = 'PENDING'."""
    with _DB_LOCK:
        conn = get_connection()
        try:
            now_str = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
            updates = ["updated_at = ?", "sync_status = 'PENDING'"]
            params: List[Any] = [now_str]

            if status is not None:
                updates.append("investigation_status = ?")
                params.append(status)
            if notes is not None:
                updates.append("investigation_notes = ?")
                params.append(notes)
            if resolution is not None:
                updates.append("resolution = ?")
                params.append(resolution)
            if date_resolved is not None:
                updates.append("date_resolved = ?")
                params.append(date_resolved)

            params.append(incident_id)
            sql = f"UPDATE incident_reports SET {', '.join(updates)} WHERE incident_id = ?;"
            with conn:
                cur = conn.execute(sql, tuple(params))
                return cur.rowcount > 0
        finally:
            conn.close()


# ---------------------------------------------------------------------------
# CRUD - Protected Assets
# ---------------------------------------------------------------------------

def upsert_protected_asset(record: dict) -> bool:
    """Upsert protected asset record into SQLite with sync_status = 'PENDING'."""
    with _DB_LOCK:
        conn = get_connection()
        try:
            now_str = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
            path = record["path"]
            asset_id = _asset_doc_id(path)

            with conn:
                conn.execute(
                    """
                    INSERT INTO protected_assets
                    (path, asset_id, asset_name, asset_type, classification, protected_at, protected_by, status, sync_status, updated_at)
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, 'PENDING', ?)
                    ON CONFLICT(path) DO UPDATE SET
                        asset_name = excluded.asset_name,
                        asset_type = excluded.asset_type,
                        classification = excluded.classification,
                        protected_at = excluded.protected_at,
                        protected_by = excluded.protected_by,
                        status = excluded.status,
                        sync_status = 'PENDING',
                        updated_at = excluded.updated_at;
                    """,
                    (
                        path,
                        asset_id,
                        record.get("asset_name", os.path.basename(path)),
                        record.get("asset_type", "file"),
                        record.get("classification", "Public"),
                        record.get("protected_at", now_str),
                        record.get("protected_by", "admin"),
                        record.get("status", "protected"),
                        now_str,
                    ),
                )
            return True
        finally:
            conn.close()


def get_all_protected_assets(only_protected: bool = True) -> List[dict]:
    """Return all protected assets."""
    with _DB_LOCK:
        conn = get_connection()
        try:
            cur = conn.cursor()
            if only_protected:
                cur.execute("SELECT * FROM protected_assets WHERE status = 'protected';")
            else:
                cur.execute("SELECT * FROM protected_assets;")
            return [dict(r) for r in cur.fetchall()]
        finally:
            conn.close()


def get_protected_asset(path: str) -> Optional[dict]:
    """Return asset by path (case-insensitive check on Windows)."""
    with _DB_LOCK:
        conn = get_connection()
        try:
            cur = conn.cursor()
            cur.execute("SELECT * FROM protected_assets;")
            normalized_target = os.path.normcase(os.path.normpath(os.path.abspath(path)))
            for row in cur.fetchall():
                row_dict = dict(row)
                row_norm = os.path.normcase(os.path.normpath(os.path.abspath(row_dict["path"])))
                if row_norm == normalized_target:
                    return row_dict
            return None
        finally:
            conn.close()


# ---------------------------------------------------------------------------
# CRUD - Security Settings (PIN)
# ---------------------------------------------------------------------------

def upsert_security_settings(record: dict) -> bool:
    """Upsert PIN security settings into SQLite with sync_status = 'PENDING'."""
    with _DB_LOCK:
        conn = get_connection()
        try:
            now_str = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
            with conn:
                conn.execute(
                    """
                    INSERT INTO security_settings
                    (key, pin_hash, salt, updated_at, updated_by, is_default, sync_status)
                    VALUES ('pin_config', ?, ?, ?, ?, ?, 'PENDING')
                    ON CONFLICT(key) DO UPDATE SET
                        pin_hash = excluded.pin_hash,
                        salt = excluded.salt,
                        updated_at = excluded.updated_at,
                        updated_by = excluded.updated_by,
                        is_default = excluded.is_default,
                        sync_status = 'PENDING';
                    """,
                    (
                        record.get("pin_hash"),
                        record.get("salt", ""),
                        record.get("updated_at", now_str),
                        record.get("updated_by", "system-default"),
                        1 if record.get("is_default") else 0,
                    ),
                )
            return True
        finally:
            conn.close()


def get_security_settings() -> Optional[dict]:
    """Retrieve the current PIN security settings."""
    with _DB_LOCK:
        conn = get_connection()
        try:
            cur = conn.cursor()
            cur.execute("SELECT * FROM security_settings WHERE key = 'pin_config';")
            row = cur.fetchone()
            if not row:
                return None
            res = dict(row)
            res["is_default"] = bool(res.get("is_default"))
            return res
        finally:
            conn.close()


# ---------------------------------------------------------------------------
# Sync Status Management
# ---------------------------------------------------------------------------

def get_pending_sync_records(table_name: str, limit: int = 50) -> List[dict]:
    """Retrieve records awaiting cloud synchronization."""
    allowed_tables = {"protected_assets", "activity_logs", "security_alerts", "incident_reports", "security_settings"}
    if table_name not in allowed_tables:
        return []

    with _DB_LOCK:
        conn = get_connection()
        try:
            cur = conn.cursor()
            cur.execute(
                f"SELECT * FROM {table_name} WHERE sync_status IN ('PENDING', 'FAILED') LIMIT ?;",
                (limit,),
            )
            rows = cur.fetchall()
            results = []
            for r in rows:
                item = dict(r)
                if item.get("risk_factors"):
                    try:
                        item["risk_factors"] = json.loads(item["risk_factors"])
                    except Exception:
                        pass
                results.append(item)
            return results
        finally:
            conn.close()


def mark_as_synced(table_name: str, id_column: str, id_val: Any) -> None:
    """Mark a record as successfully synced to Firestore."""
    allowed_tables = {"protected_assets", "activity_logs", "security_alerts", "incident_reports", "security_settings"}
    if table_name not in allowed_tables:
        return

    with _DB_LOCK:
        conn = get_connection()
        try:
            now_str = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
            with conn:
                conn.execute(
                    f"UPDATE {table_name} SET sync_status = 'SYNCED', synced_at = ?, sync_error = NULL WHERE {id_column} = ?;",
                    (now_str, id_val),
                )
                conn.execute(
                    """
                    INSERT INTO sync_history (entity_type, entity_id, action, status, attempted_at)
                    VALUES (?, ?, 'UPLOAD', 'SUCCESS', ?);
                    """,
                    (table_name, str(id_val), now_str),
                )
        finally:
            conn.close()


def mark_as_failed(table_name: str, id_column: str, id_val: Any, error_msg: str) -> None:
    """Record a synchronization failure for retry."""
    allowed_tables = {"protected_assets", "activity_logs", "security_alerts", "incident_reports", "security_settings"}
    if table_name not in allowed_tables:
        return

    with _DB_LOCK:
        conn = get_connection()
        try:
            now_str = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
            with conn:
                conn.execute(
                    f"UPDATE {table_name} SET sync_status = 'FAILED', sync_error = ? WHERE {id_column} = ?;",
                    (error_msg[:500], id_val),
                )
                conn.execute(
                    """
                    INSERT INTO sync_history (entity_type, entity_id, action, status, error_message, attempted_at)
                    VALUES (?, ?, 'UPLOAD', 'ERROR', ?, ?);
                    """,
                    (table_name, str(id_val), error_msg[:500], now_str),
                )
        finally:
            conn.close()


def get_sync_summary() -> dict:
    """Return summary counts of sync statuses across tables."""
    tables = ["protected_assets", "activity_logs", "security_alerts", "incident_reports", "security_settings"]
    summary = {}
    with _DB_LOCK:
        conn = get_connection()
        try:
            cur = conn.cursor()
            for t in tables:
                cur.execute(f"SELECT sync_status, COUNT(*) as c FROM {t} GROUP BY sync_status;")
                counts = {"PENDING": 0, "SYNCED": 0, "FAILED": 0}
                for row in cur.fetchall():
                    status = row["sync_status"]
                    if status in counts:
                        counts[status] = row["c"]
                summary[t] = counts
            return summary
        finally:
            conn.close()


# Auto-initialize database on import
try:
    initialize_database()
except Exception as _e:
    print(f"[DATABASE WARNING] Failed initial database setup: {_e}")

